using RemuxForge.Core.Ai;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Capacità dell'accesso corrente per un job di traduzione: quelle dichiarate dall'accesso, meno quelle che il servizio ha
    /// rifiutato in esecuzione e che restano spente per il resto del job
    /// </summary>
    public class TranslationProviderCapabilities
    {
        #region Costanti

        /// <summary>
        /// Ricerca web del modello (tool web_search)
        /// </summary>
        public const string WEB_SEARCH = "web_search";

        /// <summary>
        /// Parametro temperature
        /// </summary>
        public const string TEMPERATURE = "temperature";

        /// <summary>
        /// Parametro max_output_tokens
        /// </summary>
        public const string MAX_OUTPUT_TOKENS = "max_output_tokens";

        /// <summary>
        /// Parametro prompt_cache_key
        /// </summary>
        public const string PROMPT_CACHE_KEY = "prompt_cache_key";

        /// <summary>
        /// Parametro reasoning
        /// </summary>
        public const string REASONING = "reasoning";

        /// <summary>
        /// Capacità facoltative nell'ordine in cui si tolgono quando il servizio non dice quale ha rifiutato
        /// </summary>
        private static readonly string[] REMOVAL_ORDER = new string[] { WEB_SEARCH, TEMPERATURE, MAX_OUTPUT_TOKENS, PROMPT_CACHE_KEY, REASONING };

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Capacità spente nel job, nell'ordine in cui sono state spente
        /// </summary>
        private readonly List<string> _disabled;

        /// <summary>
        /// Protegge le capacità spente, toccate dai file in parallelo
        /// </summary>
        private readonly object _lock;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="accessMode">Modalità di accesso</param>
        /// <param name="disabledCapabilities">Capacità già spente nel job, per la ripresa; null se nessuna</param>
        public TranslationProviderCapabilities(AiConnectionMode accessMode, IEnumerable<string> disabledCapabilities)
        {
            this.AccessMode = accessMode;
            this._disabled = new List<string>();
            this._lock = new object();
            if (disabledCapabilities != null)
            {
                foreach (string capability in disabledCapabilities)
                    this.Disable(capability);
            }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Spegne una capacità per il resto del job
        /// </summary>
        /// <param name="capability">Nome della capacità</param>
        public void Disable(string capability)
        {
            lock (this._lock)
            {
                if (!string.IsNullOrEmpty(capability) && !this._disabled.Contains(capability))
                    this._disabled.Add(capability);
            }
        }

        /// <summary>
        /// Prepara la richiesta da mandare: chiave di cache dal progetto, dal file, dal modello e dal tipo di chiamata, e solo i
        /// parametri facoltativi delle capacità accese
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <returns>Copia della richiesta pronta per l'invio</returns>
        public AiResponseRequest Prepare(TranslationModelRequest request)
        {
            AiResponseRequest source = request.Request;
            AiResponseRequest prepared = new AiResponseRequest();

            prepared.Model = source.Model;
            prepared.Instructions = source.Instructions;
            prepared.Input = source.Input;
            prepared.ToolChoice = source.ToolChoice;
            prepared.SessionId = TranslationSessionKey.Create(request.ProjectId, request.FileId, source.Model, request.Kind.GetSessionKind());
            prepared.Temperature = this.Temperature ? source.Temperature : null;
            prepared.MaxOutputTokens = this.MaxOutputTokens ? source.MaxOutputTokens : null;
            prepared.PromptCacheKey = this.PromptCacheKey ? prepared.SessionId : "";
            prepared.ReasoningEffort = this.Reasoning ? source.ReasoningEffort : "";

            if (source.Tools != null)
            {
                for (int i = 0; i < source.Tools.Count; i++)
                {
                    if (!this.WebSearch && IsWebSearchTool(source.Tools[i]))
                        continue;

                    prepared.Tools.Add(source.Tools[i].DeepClone());
                }
            }

            return prepared;
        }

        /// <summary>
        /// Riconosce la capacità rifiutata dal servizio: dal parametro indicato nell'errore, altrimenti, se il parametro manca, la
        /// prima capacità facoltativa presente nella richiesta nell'ordine fisso di rimozione
        /// </summary>
        /// <param name="rejectedParameter">Parametro indicato dal servizio (error.param), anche vuoto</param>
        /// <param name="sent">Richiesta mandata</param>
        /// <returns>Nome della capacità, vuoto se il parametro indicato non è una capacità mandata o se la richiesta non ne conteneva
        /// nessuna da togliere</returns>
        public string ResolveRejected(string rejectedParameter, AiResponseRequest sent)
        {
            string parameter = rejectedParameter != null ? rejectedParameter.Trim() : "";

            // Il servizio può indicare il percorso del campo: reasoning.effort, tools[0], tools[0].type
            if (!string.IsNullOrEmpty(parameter))
            {
                for (int i = 0; i < REMOVAL_ORDER.Length; i++)
                {
                    if (parameter.StartsWith(REMOVAL_ORDER[i], StringComparison.OrdinalIgnoreCase))
                        return IsSent(REMOVAL_ORDER[i], sent) ? REMOVAL_ORDER[i] : "";
                }

                if (parameter.StartsWith("tools", StringComparison.OrdinalIgnoreCase))
                    return IsSent(WEB_SEARCH, sent) ? WEB_SEARCH : "";

                return "";
            }

            for (int i = 0; i < REMOVAL_ORDER.Length; i++)
            {
                if (IsSent(REMOVAL_ORDER[i], sent))
                    return REMOVAL_ORDER[i];
            }

            return "";
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Indica se una capacità è accesa: dichiarata dall'accesso e non spenta nel job
        /// </summary>
        /// <param name="capability">Nome della capacità</param>
        /// <param name="declared">True se l'accesso la dichiara</param>
        /// <returns>True se accesa</returns>
        private bool IsEnabled(string capability, bool declared)
        {
            lock (this._lock)
            {
                return declared && !this._disabled.Contains(capability);
            }
        }

        /// <summary>
        /// Indica se una capacità facoltativa è presente nella richiesta mandata
        /// </summary>
        /// <param name="capability">Nome della capacità</param>
        /// <param name="sent">Richiesta mandata</param>
        /// <returns>True se presente</returns>
        private static bool IsSent(string capability, AiResponseRequest sent)
        {
            if (capability == WEB_SEARCH)
            {
                for (int i = 0; sent.Tools != null && i < sent.Tools.Count; i++)
                {
                    if (IsWebSearchTool(sent.Tools[i]))
                        return true;
                }

                return false;
            }

            if (capability == TEMPERATURE)
                return sent.Temperature.HasValue;
            if (capability == MAX_OUTPUT_TOKENS)
                return sent.MaxOutputTokens.HasValue;
            if (capability == PROMPT_CACHE_KEY)
                return !string.IsNullOrEmpty(sent.PromptCacheKey);
            if (capability == REASONING)
                return !string.IsNullOrEmpty(sent.ReasoningEffort);

            return false;
        }

        /// <summary>
        /// Indica se una definizione di tool è la ricerca web
        /// </summary>
        /// <param name="tool">Definizione del tool</param>
        /// <returns>True per type web_search</returns>
        private static bool IsWebSearchTool(JsonNode tool)
        {
            JsonObject definition = tool as JsonObject;
            return definition != null && AiOAuthFlow.GetString(definition, "type") == WEB_SEARCH;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Modalità di accesso
        /// </summary>
        public AiConnectionMode AccessMode { get; private set; }

        /// <summary>
        /// Ricerca web disponibile
        /// </summary>
        public bool WebSearch { get { return this.IsEnabled(WEB_SEARCH, true); } }

        /// <summary>
        /// Temperatura disponibile: solo con la API key
        /// </summary>
        public bool Temperature { get { return this.IsEnabled(TEMPERATURE, this.AccessMode == AiConnectionMode.ApiKey); } }

        /// <summary>
        /// Tetto dei token in output disponibile: solo con la API key
        /// </summary>
        public bool MaxOutputTokens { get { return this.IsEnabled(MAX_OUTPUT_TOKENS, this.AccessMode == AiConnectionMode.ApiKey); } }

        /// <summary>
        /// Chiave di cache nel body, uguale all'header session_id: col piano senza la chiave nel body la cache vale solo fra
        /// richieste identiche
        /// </summary>
        public bool PromptCacheKey { get { return this.IsEnabled(PROMPT_CACHE_KEY, true); } }

        /// <summary>
        /// Livello di ragionamento disponibile
        /// </summary>
        public bool Reasoning { get { return this.IsEnabled(REASONING, true); } }

        /// <summary>
        /// Capacità spente nel job, da salvare per la ripresa
        /// </summary>
        public List<string> DisabledCapabilities
        {
            get
            {
                lock (this._lock)
                {
                    return new List<string>(this._disabled);
                }
            }
        }

        #endregion
    }
}
