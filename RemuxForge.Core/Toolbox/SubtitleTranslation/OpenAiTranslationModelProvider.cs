using RemuxForge.Core.Ai;
using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Provider del modello sopra la Responses API di OpenAI. Il servizio lascia in cache un solo punto per richiesta, all'ultimo
    /// confine di 128 token prima della fine del prompt: con una coda lunga il punto cade nella coda e la richiesta dopo, che ha
    /// una coda diversa, non lo trova. Alla seconda richiesta di una sessione parte quindi una volta la richiesta senza la coda,
    /// che lascia il punto dentro la parte fissa riletta dalle richieste successive
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
        /// <param name="retry">Politica di retry del job</param>
        /// <param name="disabledCapabilities">Capacità già spente nel job, null se nessuna</param>
        public OpenAiTranslationModelProvider(OpenAiResponsesClient client, AiConnectionMode accessMode, AiRetryPolicy retry, IEnumerable<string> disabledCapabilities) : base(accessMode, retry, disabledCapabilities)
        {
            this._client = client;
            this._sessions = new HashSet<string>();
            this._primed = new HashSet<string>();
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

        #region Metodi protected

        /// <summary>
        /// Manda un tentativo della richiesta preparata alla Responses API
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="prepared">Richiesta da mandare</param>
        /// <param name="attempt">Tentativo del retry di trasporto, da 1</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output, consumo ed esito</returns>
        protected override async Task<AiResponseResult> SendAttemptAsync(TranslationModelRequest request, AiResponseRequest prepared, int attempt, CancellationToken cancellationToken)
        {
            // Una richiesta sola non ha chi rilegga la cache: la parte fissa si manda dalla seconda, una volta per sessione
            bool prime = false;
            if (prepared.Input.Count > 1 && !string.IsNullOrEmpty(prepared.SessionId))
            {
                lock (this._lock)
                    prime = !this._sessions.Add(prepared.SessionId) && this._primed.Add(prepared.SessionId);
            }

            AiResponseResult primed = null;
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
                fixedPart.SessionId = prepared.SessionId;
                fixedPart.Temperature = prepared.Temperature;
                fixedPart.MaxOutputTokens = prepared.MaxOutputTokens;
                fixedPart.PromptCacheKey = prepared.PromptCacheKey;
                fixedPart.ReasoningEffort = prepared.ReasoningEffort;
                try
                {
                    primed = await this._client.CreateResponseAsync(fixedPart, AiRetryPolicy.None, cancellationToken);
                }
                catch (AiServiceException)
                {
                    // Senza cache la richiesta vera parte lo stesso
                }
            }

            // Il retry lo fa la base, che registra ogni tentativo
            AiResponseResult result = await this._client.CreateResponseAsync(prepared, AiRetryPolicy.None, cancellationToken);

            // Il consumo della parte fissa resta nel registro, sommato alla richiesta che l'ha fatta partire
            if (primed != null)
            {
                result.Usage.InputTokens = (result.Usage.InputTokens ?? 0) + (primed.Usage.InputTokens ?? 0);
                result.Usage.CachedTokens = (result.Usage.CachedTokens ?? 0) + (primed.Usage.CachedTokens ?? 0);
                result.Usage.CacheWriteTokens = (result.Usage.CacheWriteTokens ?? 0) + (primed.Usage.CacheWriteTokens ?? 0);
                result.Usage.OutputTokens = (result.Usage.OutputTokens ?? 0) + (primed.Usage.OutputTokens ?? 0);
                result.Usage.ReasoningTokens = (result.Usage.ReasoningTokens ?? 0) + (primed.Usage.ReasoningTokens ?? 0);
            }

            return result;
        }

        #endregion
    }
}
