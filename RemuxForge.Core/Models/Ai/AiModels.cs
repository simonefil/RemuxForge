using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Modalità di accesso ai modelli OpenAI
    /// </summary>
    public enum AiConnectionMode
    {
        /// <summary>AI non configurata</summary>
        None,

        /// <summary>Abbonamento ChatGPT collegato con Sign in with ChatGPT</summary>
        ChatGptAccount,

        /// <summary>API key a consumo</summary>
        ApiKey
    }

    /// <summary>
    /// Categoria di errore delle chiamate AI, tradotta al momento della visualizzazione
    /// </summary>
    public enum AiErrorKind
    {
        /// <summary>Errore non classificato</summary>
        Unknown,

        /// <summary>AI non configurata o account non collegato</summary>
        NotConfigured,

        /// <summary>Sessione scaduta o revocata: serve un nuovo login</summary>
        SignInRequired,

        /// <summary>API key rifiutata</summary>
        InvalidApiKey,

        /// <summary>Account non idoneo all'uso del piano</summary>
        NotEligible,

        /// <summary>Limite di utilizzo raggiunto</summary>
        UsageLimit,

        /// <summary>Servizio temporaneamente non disponibile</summary>
        Unavailable,

        /// <summary>Richiesta non supportata dal servizio</summary>
        Unsupported,

        /// <summary>Errore di rete</summary>
        Network,

        /// <summary>Login non valido: state, callback o ID token non corrispondono</summary>
        InvalidSignIn,

        /// <summary>Risposta del servizio non interpretabile</summary>
        InvalidResponse
    }

    /// <summary>
    /// Contenuto del file di configurazione dell'integrazione AI
    /// </summary>
    public class AiCredentials
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public AiCredentials()
        {
            this.Mode = AiConnectionMode.None;
            this.ExtAgentHostId = "";
            this.Model = "";
            this.MaxRounds = RemuxForge.Core.Ai.MetadataAiPresetGenerator.DEFAULT_MAX_ROUNDS;
            this.ConsentAccepted = false;
            this.Email = "";
            this.Subject = "";
            this.ClientId = "";
            this.AccessToken = "";
            this.RefreshToken = "";
            this.IdToken = "";
            this.Scopes = new List<string>();
            this.AccessTokenExpiresAt = 0;
            this.ApiKey = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Modalità di accesso attiva
        /// </summary>
        public AiConnectionMode Mode { get; set; }

        /// <summary>
        /// Identificativo opaco e stabile di questa installazione, richiesto dal login ChatGPT
        /// </summary>
        public string ExtAgentHostId { get; set; }

        /// <summary>
        /// Modello scelto nelle impostazioni
        /// </summary>
        public string Model { get; set; }

        /// <summary>
        /// Giri massimi del modello per una generazione
        /// </summary>
        public int MaxRounds { get; set; }

        /// <summary>
        /// Consenso all'invio dei dati a OpenAI già accettato
        /// </summary>
        public bool ConsentAccepted { get; set; }

        /// <summary>
        /// Email dell'account ChatGPT, usata come login_hint ai login successivi
        /// </summary>
        public string Email { get; set; }

        /// <summary>
        /// Subject dell'ID token validato
        /// </summary>
        public string Subject { get; set; }

        /// <summary>
        /// Client ID rilasciato dalla registrazione dinamica al primo login
        /// </summary>
        public string ClientId { get; set; }

        /// <summary>
        /// Access token dell'abbonamento
        /// </summary>
        public string AccessToken { get; set; }

        /// <summary>
        /// Refresh token, ruotato a ogni rinnovo
        /// </summary>
        public string RefreshToken { get; set; }

        /// <summary>
        /// ID token dell'ultimo login
        /// </summary>
        public string IdToken { get; set; }

        /// <summary>
        /// Scope concessi
        /// </summary>
        public List<string> Scopes { get; set; }

        /// <summary>
        /// Scadenza dell'access token in secondi Unix
        /// </summary>
        public long AccessTokenExpiresAt { get; set; }

        /// <summary>
        /// API key a consumo
        /// </summary>
        public string ApiKey { get; set; }

        #endregion
    }

    /// <summary>
    /// Modello OpenAI selezionabile nelle impostazioni
    /// </summary>
    public class AiModelInfo
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public AiModelInfo()
        {
            this.Id = "";
            this.DisplayName = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Identificativo da inviare nelle richieste
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Nome mostrato nel dropdown
        /// </summary>
        public string DisplayName { get; set; }

        #endregion
    }

    /// <summary>
    /// Tentativo di login ChatGPT in corso
    /// </summary>
    public class AiAuthorizationRequest
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public AiAuthorizationRequest()
        {
            this.AuthorizationUrl = "";
            this.RedirectUri = "";
            this.State = "";
            this.Nonce = "";
            this.CodeVerifier = "";
            this.ClientId = "";
            this.CreatedAtUtc = DateTime.UtcNow;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// URL da aprire nel browser
        /// </summary>
        public string AuthorizationUrl { get; set; }

        /// <summary>
        /// Indirizzo di ritorno loopback usato dalla richiesta
        /// </summary>
        public string RedirectUri { get; set; }

        /// <summary>
        /// State anti-CSRF del tentativo
        /// </summary>
        public string State { get; set; }

        /// <summary>
        /// Nonce atteso nell'ID token
        /// </summary>
        public string Nonce { get; set; }

        /// <summary>
        /// Verifier PKCE
        /// </summary>
        public string CodeVerifier { get; set; }

        /// <summary>
        /// Client ID usato nella richiesta: dynamic_agent_client al primo login
        /// </summary>
        public string ClientId { get; set; }

        /// <summary>
        /// Istante di creazione del tentativo
        /// </summary>
        public DateTime CreatedAtUtc { get; set; }

        #endregion
    }

    /// <summary>
    /// Errore di una chiamata AI con categoria e messaggio tecnico del servizio
    /// </summary>
    public class AiServiceException : Exception
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="kind">Categoria errore</param>
        /// <param name="message">Messaggio localizzato</param>
        /// <param name="serviceCode">Codice errore restituito dal servizio, vuoto se assente</param>
        public AiServiceException(AiErrorKind kind, string message, string serviceCode) : base(message)
        {
            this.Kind = kind;
            this.ServiceCode = serviceCode != null ? serviceCode : "";
        }

        /// <summary>
        /// Costruttore con eccezione interna
        /// </summary>
        /// <param name="kind">Categoria errore</param>
        /// <param name="message">Messaggio localizzato</param>
        /// <param name="serviceCode">Codice errore restituito dal servizio, vuoto se assente</param>
        /// <param name="innerException">Eccezione originale</param>
        public AiServiceException(AiErrorKind kind, string message, string serviceCode, Exception innerException) : base(message, innerException)
        {
            this.Kind = kind;
            this.ServiceCode = serviceCode != null ? serviceCode : "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Categoria errore
        /// </summary>
        public AiErrorKind Kind { get; private set; }

        /// <summary>
        /// Codice errore restituito dal servizio
        /// </summary>
        public string ServiceCode { get; private set; }

        #endregion
    }

    /// <summary>
    /// Tipo di passo della generazione preset, tradotto dal wizard
    /// </summary>
    public enum AiPresetGenerationStepKind
    {
        /// <summary>Richiesta inviata al modello</summary>
        RequestSent,

        /// <summary>Il modello ha interrogato i file</summary>
        FilesQueried,

        /// <summary>Preset inviato e rifiutato dalla validazione</summary>
        PresetRejected,

        /// <summary>Preset inviato e accettato</summary>
        PresetAccepted,

        /// <summary>Il modello ha risposto senza inviare il preset</summary>
        MissingSubmission
    }

    /// <summary>
    /// Passo della generazione preset notificato al wizard
    /// </summary>
    public class AiPresetGenerationStep
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public AiPresetGenerationStep()
        {
            this.Kind = AiPresetGenerationStepKind.RequestSent;
            this.Round = 0;
            this.Detail = "";
            this.Errors = new List<string>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Tipo di passo
        /// </summary>
        public AiPresetGenerationStepKind Kind { get; set; }

        /// <summary>
        /// Giro di tool corrente, 1-based
        /// </summary>
        public int Round { get; set; }

        /// <summary>
        /// Dettaglio del passo: argomenti della query o testo del modello
        /// </summary>
        public string Detail { get; set; }

        /// <summary>
        /// Errori di validazione del preset rifiutato
        /// </summary>
        public List<string> Errors { get; set; }

        #endregion
    }

    /// <summary>
    /// Esito della generazione preset
    /// </summary>
    public class AiPresetGenerationResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public AiPresetGenerationResult()
        {
            this.Success = false;
            this.Preset = null;
            this.PresetJson = "";
            this.Rounds = 0;
            this.Errors = new List<string>();
            this.Warnings = new List<string>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True se il modello ha prodotto un preset valido
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Preset validato
        /// </summary>
        public MkvMetadataPreset Preset { get; set; }

        /// <summary>
        /// JSON del preset validato, serializzato con le opzioni dei preset
        /// </summary>
        public string PresetJson { get; set; }

        /// <summary>
        /// Giri di tool eseguiti
        /// </summary>
        public int Rounds { get; set; }

        /// <summary>
        /// Errori dell'ultimo tentativo quando la generazione non riesce
        /// </summary>
        public List<string> Errors { get; set; }

        /// <summary>
        /// Avvisi della validazione del preset accettato
        /// </summary>
        public List<string> Warnings { get; set; }

        #endregion
    }
}
