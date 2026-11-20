using RemuxForge.Core.Ai;
using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Provider del modello sopra la Responses API di OpenAI. Con l'account ChatGPT il servizio lascia in cache un solo punto per
    /// richiesta, all'ultimo confine di 128 token prima della fine del prompt: con una coda lunga il punto cade nella coda e la
    /// richiesta dopo, che ha una coda diversa, non lo trova. Prima della prima richiesta di una sessione che il runner dichiara
    /// riletta parte quindi una volta la stessa richiesta senza la coda, che lascia il punto dentro la parte fissa: anche il
    /// primo blocco la trova in cache. Una sessione che il runner non dichiara riletta, come un blocco solo coi suoi
    /// ritentativi, prepara la parte fissa alla sua seconda richiesta; una sessione di una richiesta sola non la prepara mai. Il
    /// runner sa quante richieste avrà la sessione, il provider sa come funziona la cache del servizio: la decisione sta divisa
    /// così, e un altro provider ignora il segnale del runner.
    /// La richiesta senza coda ha model, reasoning, tool, tool_choice e instructions della vera, che fanno parte del prefisso in
    /// cache; con tool_choice forzato il modello consegna subito una chiamata vuota. Il suo consumo si somma alla prima risposta
    /// riuscita della sessione, anche dopo un tentativo fallito della richiesta vera.
    /// Con la API key la richiesta in più non parte: se l'API pubblica mette già in cache il prefisso comune, costerebbe la parte
    /// fissa intera senza risparmio
    /// </summary>
    public class OpenAiTranslationModelProvider : TranslationModelProviderBase
    {
        #region Variabili di classe

        /// <summary>
        /// Client della Responses API
        /// </summary>
        private readonly OpenAiResponsesClient _client;

        /// <summary>
        /// Sessioni di cui è già partita una richiesta
        /// </summary>
        private readonly HashSet<string> _sessions;

        /// <summary>
        /// Sessioni con la parte fissa già mandata in cache
        /// </summary>
        private readonly HashSet<string> _primed;

        /// <summary>
        /// Consumo delle richieste senza coda non ancora sommato a una risposta, per sessione
        /// </summary>
        private readonly Dictionary<string, AiUsage> _pendingUsage;

        /// <summary>
        /// Protegge le sessioni, toccate dai file in parallelo
        /// </summary>
        private readonly object _lock;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="client">Client della Responses API</param>
        /// <param name="accessMode">Modalità di accesso del job</param>
        /// <param name="disabledCapabilities">Capacità già spente nel job, null se nessuna</param>
        public OpenAiTranslationModelProvider(OpenAiResponsesClient client, AiConnectionMode accessMode, IEnumerable<string> disabledCapabilities) : base(accessMode, disabledCapabilities)
        {
            this._client = client;
            this._sessions = new HashSet<string>();
            this._primed = new HashSet<string>();
            this._pendingUsage = new Dictionary<string, AiUsage>();
            this._lock = new object();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Elenca i modelli dell'accesso configurato con i loro livelli di ragionamento
        /// </summary>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Modelli ordinati per nome</returns>
        public override Task<List<AiModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        {
            return this._client.ListModelsAsync(cancellationToken);
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Manda un tentativo della richiesta preparata alla Responses API, preceduto con l'account ChatGPT una volta per sessione
        /// dalla richiesta senza coda quando la parte fissa verrà riletta
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="prepared">Richiesta da mandare</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output, consumo ed esito</returns>
        protected override async Task<AiResponseResult> SendAttemptAsync(TranslationModelRequest request, AiResponseRequest prepared, CancellationToken cancellationToken)
        {
            string session = prepared.SessionId;
            bool prime = false;
            if (this.Capabilities.AccessMode == AiConnectionMode.ChatGptAccount && prepared.Input.Count > 1 && !string.IsNullOrEmpty(session))
            {
                lock (this._lock)
                {
                    bool seen = !this._sessions.Add(session);
                    prime = (request.PrefixReused || seen) && this._primed.Add(session);
                }
            }

            if (prime)
            {
                // Stessa richiesta senza l'ultimo item, così il prompt finisce sulla parte fissa
                AiResponseRequest fixedPart = new AiResponseRequest();
                fixedPart.Model = prepared.Model;
                fixedPart.Instructions = prepared.Instructions;
                fixedPart.Input = prepared.Input.DeepClone() as JsonArray;
                fixedPart.Input.RemoveAt(fixedPart.Input.Count - 1);
                fixedPart.Tools = prepared.Tools;
                fixedPart.ToolChoice = prepared.ToolChoice;
                fixedPart.SessionId = session;
                fixedPart.PromptCacheKey = prepared.PromptCacheKey;
                fixedPart.ReasoningEffort = prepared.ReasoningEffort;
                try
                {
                    AiResponseResult primed = await this._client.CreateResponseAsync(fixedPart, cancellationToken);
                    lock (this._lock)
                        this._pendingUsage[session] = primed.Usage;
                }
                catch (AiServiceException ex)
                {
                    // Senza cache la richiesta vera parte lo stesso; la prossima della sessione riprova a prepararla. Il consumo della
                    // richiesta fallita va nel registro come quello di una riuscita
                    lock (this._lock)
                    {
                        this._primed.Remove(session);
                        if (ex.Usage != null)
                            this._pendingUsage[session] = ex.Usage;
                    }
                }
            }

            // Il retry lo fa la base, che registra ogni tentativo
            AiResponseResult result = await this._client.CreateResponseAsync(prepared, cancellationToken);

            // Il consumo della parte fissa resta nel registro, sommato alla prima risposta riuscita della sessione
            AiUsage pending;
            lock (this._lock)
            {
                if (string.IsNullOrEmpty(session) || !this._pendingUsage.Remove(session, out pending))
                    pending = null;
            }
            if (pending != null)
            {
                result.Usage.InputTokens = (result.Usage.InputTokens ?? 0) + (pending.InputTokens ?? 0);
                result.Usage.CachedTokens = (result.Usage.CachedTokens ?? 0) + (pending.CachedTokens ?? 0);
                result.Usage.CacheWriteTokens = (result.Usage.CacheWriteTokens ?? 0) + (pending.CacheWriteTokens ?? 0);
                result.Usage.OutputTokens = (result.Usage.OutputTokens ?? 0) + (pending.OutputTokens ?? 0);
                result.Usage.ReasoningTokens = (result.Usage.ReasoningTokens ?? 0) + (pending.ReasoningTokens ?? 0);
            }

            return result;
        }

        #endregion
    }
}
