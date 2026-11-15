using RemuxForge.Core.Ai;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Provider del modello che non consuma: risponde con consegne sintetiche deterministiche e simula i guasti dello scenario,
    /// passando dalla stessa politica di retry del provider reale
    /// </summary>
    public class RecordedTranslationModelProvider : TranslationModelProviderBase
    {
        #region Costanti

        /// <summary>
        /// Modello sintetico elencato dal provider
        /// </summary>
        public const string MODEL_ID = "recorded-translation";

        /// <summary>
        /// Prefisso del testo delle traduzioni sintetiche
        /// </summary>
        public const string SYNTHETIC_PREFIX = "[tr] ";

        /// <summary>
        /// Righe della coda che elencano gli id richiesti
        /// </summary>
        private static readonly string[] REQUESTED_IDS_HEADERS = new string[] { "TRANSLATE cue ids ", "REWRITE cue ids ", "DELIVER cue ids ", "REVIEW cue ids " };

        /// <summary>
        /// Riga del prefisso che introduce le battute sorgente
        /// </summary>
        private const string SOURCE_HEADER = "SOURCE EPISODE:";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Scenario da simulare
        /// </summary>
        private readonly RecordedTranslationScenario _scenario;

        /// <summary>
        /// Ultima chiamata vista e numero di chiamate per tipo e file; la ripetizione dopo un parametro rifiutato non conta
        /// </summary>
        private readonly Dictionary<string, KeyValuePair<TranslationModelRequest, int>> _occurrences;

        /// <summary>
        /// Capacità rifiutata da ciascun guasto «capacità rifiutata», risolta al primo lancio: le ripetizioni non ne rifiutano altre
        /// </summary>
        private readonly Dictionary<RecordedTranslationFault, string> _rejected;

        /// <summary>
        /// Protegge il conteggio delle chiamate e le capacità rifiutate, toccati dai file in parallelo
        /// </summary>
        private readonly object _lock;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="scenario">Scenario da simulare</param>
        /// <param name="accessMode">Modalità di accesso simulata</param>
        /// <param name="retry">Politica di retry del job</param>
        /// <param name="disabledCapabilities">Capacità già spente nel job, null se nessuna</param>
        public RecordedTranslationModelProvider(RecordedTranslationScenario scenario, AiConnectionMode accessMode, AiRetryPolicy retry, IEnumerable<string> disabledCapabilities) : base(accessMode, retry, disabledCapabilities)
        {
            this._scenario = scenario != null ? scenario : new RecordedTranslationScenario();
            if (this._scenario.Faults == null)
                this._scenario.Faults = new List<RecordedTranslationFault>();
            this._occurrences = new Dictionary<string, KeyValuePair<TranslationModelRequest, int>>(StringComparer.Ordinal);
            this._rejected = new Dictionary<RecordedTranslationFault, string>();
            this._lock = new object();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Elenca il solo modello sintetico, con i livelli di ragionamento della API key
        /// </summary>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Modello sintetico</returns>
        public override Task<List<AiModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        {
            AiModelInfo model = new AiModelInfo();
            model.Id = MODEL_ID;
            model.DisplayName = MODEL_ID;
            model.DefaultReasoningLevel = "medium";
            model.SupportedReasoningLevels.AddRange(new string[] { "low", "medium", "high" });
            return Task.FromResult(new List<AiModelInfo> { model });
        }

        #endregion

        #region Metodi protected

        /// <summary>
        /// Risponde alla richiesta preparata con la politica di retry del job, simulando il guasto dello scenario
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="prepared">Richiesta da mandare</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output, consumo ed esito</returns>
        protected override Task<AiResponseResult> SendPreparedAsync(TranslationModelRequest request, AiResponseRequest prepared, CancellationToken cancellationToken)
        {
            string key = request.Kind.ToString() + "/" + request.FileId;
            int occurrence;
            RecordedTranslationFault fault = null;

            lock (this._lock)
            {
                KeyValuePair<TranslationModelRequest, int> last;
                occurrence = this._occurrences.TryGetValue(key, out last) ? last.Value : 0;
                if (!ReferenceEquals(last.Key, request))
                    occurrence++;
                this._occurrences[key] = new KeyValuePair<TranslationModelRequest, int>(request, occurrence);
            }

            for (int i = 0; i < this._scenario.Faults.Count && fault == null; i++)
            {
                RecordedTranslationFault candidate = this._scenario.Faults[i];
                if (candidate.Occurrence == occurrence && (!candidate.CallKind.HasValue || candidate.CallKind.Value == request.Kind) && (string.IsNullOrEmpty(candidate.FileId) || candidate.FileId == request.FileId))
                    fault = candidate;
            }

            return this.Retry.ExecuteAsync(attempt => this.RespondAsync(request, prepared, fault, attempt, cancellationToken), cancellationToken);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Esegue un tentativo simulato: guasto o consegna sintetica
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="prepared">Richiesta preparata</param>
        /// <param name="fault">Guasto dello scenario, null se nessuno</param>
        /// <param name="attempt">Tentativo di trasporto, da 1</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Risultato simulato</returns>
        private async Task<AiResponseResult> RespondAsync(TranslationModelRequest request, AiResponseRequest prepared, RecordedTranslationFault fault, int attempt, CancellationToken cancellationToken)
        {
            if (this._scenario.LatencyMilliseconds > 0)
                await Task.Delay(this._scenario.LatencyMilliseconds, cancellationToken);

            bool account = this.Capabilities.AccessMode == AiConnectionMode.ChatGptAccount;

            if (fault != null && fault.Kind == RecordedTranslationFaultKind.RateLimit && attempt == 1)
            {
                TimeSpan? retryAfter = fault.RetryAfterSeconds > 0 ? TimeSpan.FromSeconds(fault.RetryAfterSeconds) : null;
                throw new AiServiceException(AiErrorKind.UsageLimit, AppText.T(account ? "ai.error.usageLimitAccount" : "ai.error.usageLimitApiKey"), "rate_limit_exceeded", 429, retryAfter, "");
            }

            // Col piano il limite arriva nello stream con HTTP 200, con la API key il credito esaurito è un 429 con codice
            if (fault != null && fault.Kind == RecordedTranslationFaultKind.UsageLimit)
            {
                if (account)
                    throw new AiServiceException(AiErrorKind.UsageLimit, AppText.T("ai.error.usageLimitAccount"), "subscription_sharing_usage_limit_exceeded", 0, null, "");

                throw new AiServiceException(AiErrorKind.UsageLimit, AppText.T("ai.error.usageLimitApiKey"), "insufficient_quota", 429, null, "");
            }

            if (fault != null && fault.Kind == RecordedTranslationFaultKind.UnsupportedCapability)
            {
                // Senza parametro nello scenario la capacità si risolve una volta sola, poi vale finché la richiesta la contiene
                string rejected;
                lock (this._lock)
                {
                    if (!this._rejected.TryGetValue(fault, out rejected))
                    {
                        rejected = this.Capabilities.ResolveRejected(fault.Parameter, prepared);
                        if (!string.IsNullOrEmpty(rejected))
                            this._rejected[fault] = rejected;
                    }
                }

                if (!string.IsNullOrEmpty(rejected) && !string.IsNullOrEmpty(this.Capabilities.ResolveRejected(rejected, prepared)))
                {
                    string code = "subscription_sharing_unsupported_capability";
                    throw new AiServiceException(AiErrorKind.UnsupportedCapability, AppText.F("ai.error.unsupported", code), code, 0, null, fault.Parameter);
                }
            }

            // Consegna sintetica: traduzione col prefisso fisso davanti al testo sorgente, diagnosi vuota o glossario vuoto
            StringBuilder inputText = new StringBuilder(prepared.Instructions);
            Dictionary<int, string> sources = new Dictionary<int, string>();
            List<int> requestedIds = new List<int>();
            JsonObject arguments = new JsonObject();
            string toolName;

            for (int i = 0; i < prepared.Input.Count; i++)
            {
                JsonObject message = prepared.Input[i] as JsonObject;
                string content = message != null ? AiOAuthFlow.GetString(message, "content") : "";
                inputText.Append(content);

                // Battute sorgente: dopo SOURCE EPISODE fino alla riga vuota, con le righe di continuazione che iniziano col tab
                string[] lines = content.Replace("\r\n", "\n").Split('\n');
                bool inSource = false;
                int currentId = 0;
                for (int j = 0; j < lines.Length; j++)
                {
                    string line = lines[j];
                    if (line.StartsWith(SOURCE_HEADER, StringComparison.Ordinal))
                    {
                        inSource = true;
                        continue;
                    }

                    for (int h = 0; h < REQUESTED_IDS_HEADERS.Length; h++)
                    {
                        if (line.StartsWith(REQUESTED_IDS_HEADERS[h], StringComparison.Ordinal))
                        {
                            // Intervalli separati da virgola, per esempio 61-118 o 74,90-92
                            string ranges = line.Substring(REQUESTED_IDS_HEADERS[h].Length);
                            int end = ranges.IndexOf(' ');
                            string[] parts = (end >= 0 ? ranges.Substring(0, end) : ranges).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                            requestedIds = new List<int>();
                            for (int p = 0; p < parts.Length; p++)
                            {
                                int dash = parts[p].IndexOf('-');
                                int first;
                                int last;
                                if (dash < 0 && int.TryParse(parts[p], NumberStyles.Integer, CultureInfo.InvariantCulture, out first))
                                {
                                    requestedIds.Add(first);
                                }
                                else if (dash > 0 && int.TryParse(parts[p].Substring(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out first) && int.TryParse(parts[p].Substring(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out last))
                                {
                                    for (int id = first; id <= last; id++)
                                        requestedIds.Add(id);
                                }
                            }
                        }
                    }

                    if (!inSource)
                        continue;

                    if (line.Length == 0)
                    {
                        inSource = false;
                    }
                    else if (line[0] == '\t' && currentId > 0)
                    {
                        sources[currentId] = sources[currentId] + "\n" + line.Substring(1);
                    }
                    else
                    {
                        int tab = line.IndexOf('\t');
                        string header = tab >= 0 ? line.Substring(0, tab) : line;
                        int space = header.IndexOf(' ');
                        if (int.TryParse(space >= 0 ? header.Substring(0, space) : header, NumberStyles.Integer, CultureInfo.InvariantCulture, out currentId))
                            sources[currentId] = tab >= 0 ? line.Substring(tab + 1) : "";
                    }
                }
            }

            if (request.Kind == TranslationCallKind.A1 || request.Kind == TranslationCallKind.A2)
            {
                toolName = "submit_glossary";
                arguments["series"] = "";
                arguments["tone"] = "";
                arguments["register_rules"] = "";
                arguments["terms"] = new JsonArray();
                arguments["characters"] = new JsonArray();
                arguments["addressing"] = new JsonArray();
            }
            else if (request.Kind == TranslationCallKind.D1)
            {
                toolName = "submit_diagnosis";
                arguments["issues"] = new JsonArray();
            }
            else
            {
                JsonArray translations = new JsonArray();
                for (int i = 0; i < requestedIds.Count; i++)
                {
                    int id = requestedIds[i];
                    string source;
                    if (fault != null && fault.Kind == RecordedTranslationFaultKind.WrongCues && requestedIds.Count > 1 && i == requestedIds.Count - 1)
                        continue;

                    string text = sources.TryGetValue(id, out source) ? SYNTHETIC_PREFIX + source : SYNTHETIC_PREFIX + "#" + id.ToString(CultureInfo.InvariantCulture);
                    translations.Add(new JsonObject { ["id"] = id, ["text"] = fault != null && fault.Kind == RecordedTranslationFaultKind.WrongCues && i == 0 ? "" : text });
                }

                toolName = "submit_translation";
                arguments["translations"] = translations;
                arguments["new_terms"] = new JsonArray();
            }

            string argumentsJson = arguments.ToJsonString();
            AiResponseResult result = new AiResponseResult();
            result.Items.Add(new JsonObject
            {
                ["type"] = "function_call",
                ["id"] = "fc_recorded",
                ["call_id"] = "call_recorded",
                ["name"] = toolName,
                ["arguments"] = argumentsJson,
                ["status"] = "completed"
            });
            result.ResponseId = "resp_recorded";
            result.HttpStatus = 200;

            // Consumo stimato a 4 caratteri per token, sempre uguale a parità di richiesta
            result.Usage.InputTokens = (inputText.Length + 3) / 4;
            result.Usage.CachedTokens = 0;
            result.Usage.CacheWriteTokens = 0;
            result.Usage.OutputTokens = (argumentsJson.Length + 3) / 4;
            result.Usage.ReasoningTokens = 0;

            if (fault != null && fault.Kind == RecordedTranslationFaultKind.Incomplete)
            {
                result.Items = new List<JsonObject>();
                result.Status = AiResponseStatus.Incomplete;
                result.IncompleteReason = "max_output_tokens";
            }

            return result;
        }

        #endregion
    }
}
