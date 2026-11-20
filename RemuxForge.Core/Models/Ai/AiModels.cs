using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

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

        /// <summary>Parametro facoltativo rifiutato dall'accesso corrente</summary>
        UnsupportedCapability,

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
            this.ReasoningEffort = "";
            this.MaxRounds = RemuxForge.Core.Ai.MetadataAiPresetGenerator.DEFAULT_MAX_ROUNDS;
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
        /// Modello del Metadata AI
        /// </summary>
        public string Model { get; set; }

        /// <summary>
        /// Livello di ragionamento del Metadata AI, vuoto per non mandarlo
        /// </summary>
        public string ReasoningEffort { get; set; }

        /// <summary>
        /// Giri massimi del modello per una generazione
        /// </summary>
        public int MaxRounds { get; set; }

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
            this.DefaultReasoningLevel = "";
            this.SupportedReasoningLevels = new List<string>();
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

        /// <summary>
        /// Livello di ragionamento predefinito del modello
        /// </summary>
        public string DefaultReasoningLevel { get; set; }

        /// <summary>
        /// Livelli di ragionamento accettati dal modello, nell'ordine del catalogo
        /// </summary>
        public List<string> SupportedReasoningLevels { get; set; }

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
        public AiServiceException(AiErrorKind kind, string message) : base(message)
        {
            this.Kind = kind;
            this.ServiceCode = "";
            this.RejectedParameter = "";
        }

        /// <summary>
        /// Costruttore con eccezione interna
        /// </summary>
        /// <param name="kind">Categoria errore</param>
        /// <param name="message">Messaggio localizzato</param>
        /// <param name="innerException">Eccezione originale</param>
        public AiServiceException(AiErrorKind kind, string message, Exception innerException) : base(message, innerException)
        {
            this.Kind = kind;
            this.ServiceCode = "";
            this.RejectedParameter = "";
        }

        /// <summary>
        /// Costruttore con i dati del servizio su cui decide la politica di retry
        /// </summary>
        /// <param name="kind">Categoria errore</param>
        /// <param name="message">Messaggio localizzato</param>
        /// <param name="serviceCode">Codice d'errore del servizio, vuoto se assente</param>
        /// <param name="httpStatus">Stato HTTP, zero per gli errori arrivati nello stream</param>
        /// <param name="retryAfter">Attesa chiesta dall'header Retry-After</param>
        /// <param name="rejectedParameter">Parametro rifiutato indicato dal servizio, vuoto se assente</param>
        public AiServiceException(AiErrorKind kind, string message, string serviceCode, int httpStatus, TimeSpan? retryAfter, string rejectedParameter) : base(message)
        {
            this.Kind = kind;
            this.ServiceCode = serviceCode != null ? serviceCode : "";
            this.HttpStatus = httpStatus;
            this.RetryAfter = retryAfter;
            this.RejectedParameter = rejectedParameter != null ? rejectedParameter : "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Categoria errore
        /// </summary>
        public AiErrorKind Kind { get; private set; }

        /// <summary>
        /// Codice d'errore del servizio, vuoto se assente
        /// </summary>
        public string ServiceCode { get; private set; }

        /// <summary>
        /// Stato HTTP, zero per gli errori arrivati nello stream o fuori dal servizio
        /// </summary>
        public int HttpStatus { get; private set; }

        /// <summary>
        /// Attesa chiesta dall'header Retry-After
        /// </summary>
        public TimeSpan? RetryAfter { get; private set; }

        /// <summary>
        /// Parametro rifiutato indicato dal servizio (error.param), vuoto se assente
        /// </summary>
        public string RejectedParameter { get; private set; }

        /// <summary>
        /// Consumo di token riportato dalla risposta fallita, null se il servizio non lo dà
        /// </summary>
        public AiUsage Usage { get; set; }

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
        MissingSubmission,

        /// <summary>Cambio di stato della risposta in streaming del giro</summary>
        Streaming
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
            this.Stream = null;
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

        /// <summary>
        /// Stato della risposta in streaming, solo per Streaming
        /// </summary>
        public AiStreamProgress Stream { get; set; }

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

    /// <summary>
    /// Esito di una risposta del modello arrivata al suo evento finale
    /// </summary>
    public enum AiResponseStatus
    {
        /// <summary>Risposta completa</summary>
        Completed,

        /// <summary>Risposta troncata dal servizio, con gli item ricevuti fino a lì</summary>
        Incomplete
    }

    /// <summary>
    /// Consumo di token di una risposta; un valore null indica un dato non riportato dal servizio
    /// </summary>
    public class AiUsage
    {
        #region Proprietà

        /// <summary>
        /// Token in input, compresi quelli in cache
        /// </summary>
        public long? InputTokens { get; set; }

        /// <summary>
        /// Token in input letti dalla cache
        /// </summary>
        public long? CachedTokens { get; set; }

        /// <summary>
        /// Token in input scritti in cache
        /// </summary>
        public long? CacheWriteTokens { get; set; }

        /// <summary>
        /// Token in output, compresi quelli di ragionamento
        /// </summary>
        public long? OutputTokens { get; set; }

        /// <summary>
        /// Token di ragionamento
        /// </summary>
        public long? ReasoningTokens { get; set; }

        /// <summary>
        /// Ricerche web eseguite dal modello
        /// </summary>
        public long? WebSearchRequests { get; set; }

        #endregion
    }

    /// <summary>
    /// Richiesta alla Responses API; i campi facoltativi vuoti o null non vengono mandati
    /// </summary>
    public class AiResponseRequest
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public AiResponseRequest()
        {
            this.Model = "";
            this.Instructions = "";
            this.Input = new JsonArray();
            this.Tools = new JsonArray();
            this.SessionId = "";
            this.ToolChoice = null;
            this.PromptCacheKey = "";
            this.ReasoningEffort = "";
            this.Progress = null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Modello
        /// </summary>
        public string Model { get; set; }

        /// <summary>
        /// Istruzioni di sistema
        /// </summary>
        public string Instructions { get; set; }

        /// <summary>
        /// Storico completo degli item di input
        /// </summary>
        public JsonArray Input { get; set; }

        /// <summary>
        /// Tool disponibili
        /// </summary>
        public JsonArray Tools { get; set; }

        /// <summary>
        /// Chiave di cache mandata come header session_id
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// Valore di tool_choice
        /// </summary>
        public JsonNode ToolChoice { get; set; }

        /// <summary>
        /// Chiave di cache nel body (prompt_cache_key), vuota per non mandarla
        /// </summary>
        public string PromptCacheKey { get; set; }

        /// <summary>
        /// Livello di ragionamento (reasoning.effort)
        /// </summary>
        public string ReasoningEffort { get; set; }

        /// <summary>
        /// Avviso a ogni cambio di stato dello stream, dal thread che legge la risposta; null se non serve
        /// </summary>
        public Action<AiStreamProgress> Progress { get; set; }

        #endregion
    }

    /// <summary>
    /// Stato di una risposta in streaming, ricavato dagli eventi della Responses API
    /// </summary>
    public enum AiStreamStage
    {
        /// <summary>Richiesta inviata, nessun evento ancora arrivato</summary>
        Sent,

        /// <summary>Richiesta in coda presso il servizio (response.queued)</summary>
        Queued,

        /// <summary>Risposta creata e in lavorazione (response.created, response.in_progress)</summary>
        Started,

        /// <summary>Il modello ragiona</summary>
        Reasoning,

        /// <summary>Ricerca web in corso; il testo è la query, vuoto finché il servizio non la dice</summary>
        WebSearch,

        /// <summary>Ricerca web conclusa; il testo è la query, anche vuoto</summary>
        WebSearchDone,

        /// <summary>Apertura di una pagina dai risultati; il testo è l'indirizzo</summary>
        WebPage,

        /// <summary>Ricerca di un testo in una pagina; il testo è quello cercato</summary>
        WebFind,

        /// <summary>Fonte citata nella risposta; il testo è titolo o indirizzo</summary>
        Citation,

        /// <summary>Il modello scrive la risposta, testo o chiamata di funzione; il testo è la quantità già scritta</summary>
        Writing,

        /// <summary>Il modello rifiuta la richiesta</summary>
        Refusal
    }

    /// <summary>
    /// Avanzamento di una risposta in streaming
    /// </summary>
    public class AiStreamProgress
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="stage">Stato della risposta</param>
        /// <param name="text">Testo dello stato: query, titolo, indirizzo o nome di funzione; vuoto se non ne ha</param>
        public AiStreamProgress(AiStreamStage stage, string text)
        {
            this.Stage = stage;
            this.Text = text != null ? text : "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Descrive lo stato nella lingua dell'interfaccia
        /// </summary>
        /// <returns>Testo per l'utente</returns>
        public string Describe()
        {
            string key = "ai.stream." + char.ToLowerInvariant(this.Stage.ToString()[0]) + this.Stage.ToString().Substring(1);

            // Le ricerche hanno una forma senza query, finché il servizio non la dice
            if (this.Text.Length == 0 && (this.Stage == AiStreamStage.WebSearch || this.Stage == AiStreamStage.WebSearchDone))
                return AppText.T(key + "NoQuery");

            // La scrittura parte senza quantità, poi la dice man mano che cresce
            if (this.Text.Length == 0 && this.Stage == AiStreamStage.Writing)
                return AppText.T(key + "Start");

            return AppText.F(key, this.Text);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Stato della risposta
        /// </summary>
        public AiStreamStage Stage { get; private set; }

        /// <summary>
        /// Testo dello stato, vuoto se non ne ha
        /// </summary>
        public string Text { get; private set; }

        #endregion
    }

    /// <summary>
    /// Risultato di una richiesta alla Responses API
    /// </summary>
    public class AiResponseResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public AiResponseResult()
        {
            this.Items = new List<JsonObject>();
            this.Usage = new AiUsage();
            this.Status = AiResponseStatus.Completed;
            this.IncompleteReason = "";
            this.PromptCacheKey = "";
            this.HttpStatus = 0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Item di output del modello, da rimandare invariati nel giro successivo
        /// </summary>
        public List<JsonObject> Items { get; set; }

        /// <summary>
        /// Consumo di token
        /// </summary>
        public AiUsage Usage { get; set; }

        /// <summary>
        /// Risposta completa o troncata
        /// </summary>
        public AiResponseStatus Status { get; set; }

        /// <summary>
        /// Motivo del troncamento (incomplete_details.reason), vuoto se completa
        /// </summary>
        public string IncompleteReason { get; set; }

        /// <summary>
        /// Chiave di cache applicata dal servizio (response.prompt_cache_key), vuota se non dichiarata
        /// </summary>
        public string PromptCacheKey { get; set; }

        /// <summary>
        /// Stato HTTP della risposta
        /// </summary>
        public int HttpStatus { get; set; }

        #endregion
    }
}
