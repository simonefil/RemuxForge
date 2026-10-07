using RemuxForge.Core.Ai;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Gestisce il login ChatGPT dalla UI: un tentativo alla volta, completato dal listener loopback o dall'indirizzo incollato
    /// </summary>
    public class AiAuthService
    {
        #region Costanti

        /// <summary>
        /// Durata massima di un tentativo di login
        /// </summary>
        private static readonly TimeSpan ATTEMPT_LIFETIME = TimeSpan.FromMinutes(10);

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Servizio del file di configurazione AI
        /// </summary>
        private readonly AiCredentialService _credentialService;

        /// <summary>
        /// Flusso OAuth
        /// </summary>
        private readonly AiOAuthFlow _oauthFlow;

        /// <summary>
        /// Sincronizza lo stato del tentativo
        /// </summary>
        private readonly object _stateLock;

        /// <summary>
        /// Tentativo in corso, null se nessuno
        /// </summary>
        private AiAuthorizationRequest _pending;

        /// <summary>
        /// Listener loopback del tentativo in corso
        /// </summary>
        private HttpListener _listener;

        /// <summary>
        /// Annulla l'attesa del listener e la scadenza del tentativo
        /// </summary>
        private CancellationTokenSource _attemptCancellation;

        /// <summary>
        /// Ultimo errore del login, vuoto se nessuno
        /// </summary>
        private string _lastError;

        #endregion

        #region Eventi

        /// <summary>Evento emesso quando cambia lo stato del login o della configurazione AI</summary>
        public event Action OnStateChanged;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="credentialService">Servizio del file di configurazione AI</param>
        /// <param name="oauthFlow">Flusso OAuth</param>
        public AiAuthService(AiCredentialService credentialService, AiOAuthFlow oauthFlow)
        {
            this._credentialService = credentialService;
            this._oauthFlow = oauthFlow;
            this._stateLock = new object();
            this._pending = null;
            this._listener = null;
            this._attemptCancellation = null;
            this._lastError = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True se un tentativo di login è in corso
        /// </summary>
        public bool IsLoginPending
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._pending != null;
                }
            }
        }

        /// <summary>
        /// URL di autorizzazione del tentativo in corso, vuoto se nessuno
        /// </summary>
        public string PendingAuthorizationUrl
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._pending != null ? this._pending.AuthorizationUrl : "";
                }
            }
        }

        /// <summary>
        /// Ultimo errore del login, vuoto se nessuno
        /// </summary>
        public string LastError
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._lastError;
                }
            }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Avvia un tentativo di login sostituendo l'eventuale precedente
        /// </summary>
        /// <returns>URL di autorizzazione da aprire nel browser</returns>
        public string StartLogin()
        {
            AiCredentials credentials = this._credentialService.LoadWithHostId();
            int port = GetFreeLoopbackPort();
            AiAuthorizationRequest request = this._oauthFlow.CreateAuthorization(credentials, AiOAuthFlow.BuildRedirectUri(port));
            HttpListener listener = new HttpListener();
            CancellationTokenSource cancellation = new CancellationTokenSource(ATTEMPT_LIFETIME);

            this.CancelLogin();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            listener.Start();

            lock (this._stateLock)
            {
                this._pending = request;
                this._listener = listener;
                this._attemptCancellation = cancellation;
                this._lastError = "";
            }

            // Il listener riceve la callback solo se il browser gira sulla stessa macchina; altrimenti vale l'indirizzo incollato
            _ = Task.Run(() => this.ListenAsync(request, listener, cancellation.Token));
            cancellation.Token.Register(() => this.ExpireAttempt(request));
            this.NotifyStateChanged();
            return request.AuthorizationUrl;
        }

        /// <summary>
        /// Completa il login con l'indirizzo copiato dalla barra del browser
        /// </summary>
        /// <param name="callbackUrl">Indirizzo di ritorno incollato</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>True se il login è riuscito</returns>
        public async Task<bool> CompleteWithPastedUrlAsync(string callbackUrl, CancellationToken cancellationToken)
        {
            AiAuthorizationRequest request;

            lock (this._stateLock)
            {
                request = this._pending;
            }

            if (request == null)
            {
                this.SetError(AppText.T("web.aiSettings.noPendingLogin"));
                return false;
            }

            return await this.CompleteAsync(request, callbackUrl, true, cancellationToken);
        }

        /// <summary>
        /// Annulla il tentativo di login in corso
        /// </summary>
        public void CancelLogin()
        {
            HttpListener listener;
            CancellationTokenSource cancellation;

            lock (this._stateLock)
            {
                listener = this._listener;
                cancellation = this._attemptCancellation;
                this._pending = null;
                this._listener = null;
                this._attemptCancellation = null;
                this._lastError = "";
            }

            StopListener(listener);
            if (cancellation != null)
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }
        }

        /// <summary>
        /// Scollega l'account: revoca il refresh token presso OpenAI e cancella i token locali
        /// </summary>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Messaggio di avviso se la revoca non è riuscita, vuoto altrimenti</returns>
        public async Task<string> DisconnectAsync(CancellationToken cancellationToken)
        {
            string warning = "";
            AiCredentials credentials = this._credentialService.Load();

            try
            {
                await this._oauthFlow.RevokeAsync(credentials, cancellationToken);
            }
            catch (AiServiceException ex)
            {
                // I token locali si cancellano comunque: la revoca mancata resta solo un avviso
                warning = AppText.F("web.aiSettings.revokeWarning", ex.Message);
            }

            this._credentialService.ClearAccountTokens();
            this.NotifyStateChanged();
            return warning;
        }

        /// <summary>
        /// Notifica ai componenti un cambio della configurazione AI
        /// </summary>
        public void NotifyStateChanged()
        {
            Action handler = this.OnStateChanged;
            if (handler != null)
                handler();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Attende la callback sul listener loopback e completa il login
        /// </summary>
        /// <param name="request">Tentativo di login</param>
        /// <param name="listener">Listener del tentativo</param>
        /// <param name="cancellationToken">Token di annullamento del tentativo</param>
        private async Task ListenAsync(AiAuthorizationRequest request, HttpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                // Richieste diverse dalla callback, ad esempio la favicon, non chiudono il tentativo
                if (!string.Equals(context.Request.Url.AbsolutePath, "/callback", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                // Una callback fallita, ad esempio con state estraneo, non chiude il tentativo: si resta in ascolto fino a scadenza o annullamento
                bool success = await this.CompleteAsync(request, context.Request.Url.ToString(), false, cancellationToken);
                WriteCallbackPage(context, success ? AppText.T("web.aiSettings.callbackSuccess") : AppText.F("web.aiSettings.callbackFailure", this.LastError));
                if (success)
                {
                    // Il listener si chiude solo dopo aver inviato la pagina di esito, altrimenti il browser riceve una risposta vuota
                    this.CancelLogin();
                    this.NotifyStateChanged();
                    return;
                }
            }
        }

        /// <summary>
        /// Completa il tentativo indicato e salva la connessione
        /// </summary>
        /// <param name="request">Tentativo di login</param>
        /// <param name="callbackUrl">Indirizzo di ritorno</param>
        /// <param name="finishAttempt">True per chiudere subito il tentativo, false se lo chiude il chiamante</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>True se il login è riuscito</returns>
        private async Task<bool> CompleteAsync(AiAuthorizationRequest request, string callbackUrl, bool finishAttempt, CancellationToken cancellationToken)
        {
            try
            {
                AiCredentials credentials = this._credentialService.LoadWithHostId();
                credentials = await this._oauthFlow.CompleteAuthorizationAsync(request, callbackUrl, credentials, cancellationToken);

                lock (this._stateLock)
                {
                    // Un tentativo sostituito o annullato nel frattempo non deve sovrascrivere lo stato
                    if (!ReferenceEquals(this._pending, request))
                        return false;
                }

                this._credentialService.SaveAccountConnection(credentials);
                if (finishAttempt)
                    this.CancelLogin();

                this.NotifyStateChanged();
                return true;
            }
            catch (AiServiceException ex)
            {
                this.SetError(ex.Message);
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        /// <summary>
        /// Chiude il tentativo scaduto se è ancora quello in corso
        /// </summary>
        /// <param name="request">Tentativo scaduto</param>
        private void ExpireAttempt(AiAuthorizationRequest request)
        {
            bool expired;

            lock (this._stateLock)
            {
                expired = ReferenceEquals(this._pending, request);
            }

            if (!expired)
                return;

            this.CancelLogin();
            this.SetError(AppText.T("web.aiSettings.loginExpired"));
        }

        /// <summary>
        /// Registra un errore e lo notifica
        /// </summary>
        /// <param name="message">Messaggio</param>
        private void SetError(string message)
        {
            lock (this._stateLock)
            {
                this._lastError = message != null ? message : "";
            }

            this.NotifyStateChanged();
        }

        /// <summary>
        /// Risponde al browser con una pagina di esito
        /// </summary>
        /// <param name="context">Contesto della richiesta</param>
        /// <param name="message">Messaggio</param>
        private static void WriteCallbackPage(HttpListenerContext context, string message)
        {
            try
            {
                byte[] page = Encoding.UTF8.GetBytes("<!doctype html><html><head><meta charset=\"utf-8\"><title>RemuxForge</title></head><body style=\"font-family:sans-serif;padding:2em\"><p>" + WebUtility.HtmlEncode(message) + "</p></body></html>");
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = page.Length;
                context.Response.OutputStream.Write(page, 0, page.Length);
                context.Response.Close();
            }
            catch (HttpListenerException)
            {
                // Il browser può aver già chiuso la connessione: il login è comunque registrato
            }
            catch (ObjectDisposedException)
            {
                // Il listener è stato chiuso a login completato: la pagina di esito resta best-effort
            }
        }

        /// <summary>
        /// Ferma un listener ignorando gli errori di chiusura
        /// </summary>
        /// <param name="listener">Listener</param>
        private static void StopListener(HttpListener listener)
        {
            if (listener == null)
                return;

            try
            {
                listener.Close();
            }
            catch (ObjectDisposedException)
            {
                // Già chiuso
            }
        }

        /// <summary>
        /// Trova una porta loopback libera
        /// </summary>
        /// <returns>Porta</returns>
        private static int GetFreeLoopbackPort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        #endregion
    }
}
