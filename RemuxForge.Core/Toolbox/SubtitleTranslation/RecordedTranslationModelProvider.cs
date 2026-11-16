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

        /// <summary>
        /// Sezione dei candidati nel messaggio di A1 e A2
        /// </summary>
        private const string CANDIDATES_HEADER = "RECURRING CANDIDATES (extracted by code; occurrences per episode file):\n";

        /// <summary>
        /// Sezione dei conflitti nel messaggio di A2
        /// </summary>
        private const string CONFLICTS_HEADER = "CONFLICTS (variants proposed by the episodes):\n";

        /// <summary>
        /// Secondo modello sintetico, con altri livelli di ragionamento
        /// </summary>
        public const string SMALL_MODEL_ID = "recorded-translation-small";

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
            if (this._scenario.Deliveries == null)
                this._scenario.Deliveries = new List<RecordedTranslationDelivery>();
            this._occurrences = new Dictionary<string, KeyValuePair<TranslationModelRequest, int>>(StringComparer.Ordinal);
            this._rejected = new Dictionary<RecordedTranslationFault, string>();
            this._lock = new object();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Elenca i due modelli sintetici, con livelli di ragionamento diversi
        /// </summary>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Modelli sintetici</returns>
        public override Task<List<AiModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        {
            AiModelInfo model = new AiModelInfo();
            model.Id = MODEL_ID;
            model.DisplayName = MODEL_ID;
            model.DefaultReasoningLevel = "medium";
            model.SupportedReasoningLevels.AddRange(new string[] { "low", "medium", "high" });

            AiModelInfo small = new AiModelInfo();
            small.Id = SMALL_MODEL_ID;
            small.DisplayName = SMALL_MODEL_ID;
            small.DefaultReasoningLevel = "low";
            small.SupportedReasoningLevels.AddRange(new string[] { "minimal", "low" });
            return Task.FromResult(new List<AiModelInfo> { model, small });
        }

        #endregion

        #region Metodi protected

        /// <summary>
        /// Esegue un tentativo simulato: il guasto dello scenario o la consegna sintetica
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="prepared">Richiesta da mandare</param>
        /// <param name="attempt">Tentativo di trasporto, da 1</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output, consumo ed esito</returns>
        protected override async Task<AiResponseResult> SendAttemptAsync(TranslationModelRequest request, AiResponseRequest prepared, int attempt, CancellationToken cancellationToken)
        {
            string key = request.Kind.ToString() + "/" + request.FileId;
            int occurrence;
            RecordedTranslationFault fault = null;
            RecordedTranslationDelivery delivery = null;

            // Il numero di chiamata cambia solo con una richiesta nuova: i tentativi di trasporto e la ripetizione dopo un parametro
            // rifiutato tengono lo stesso
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

            for (int i = 0; i < this._scenario.Deliveries.Count && delivery == null; i++)
            {
                RecordedTranslationDelivery candidate = this._scenario.Deliveries[i];
                if (candidate.Occurrence == occurrence && candidate.CallKind == request.Kind && (string.IsNullOrEmpty(candidate.FileId) || candidate.FileId == request.FileId))
                    delivery = candidate;
            }

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

            // Consegna sintetica: traduzione col prefisso fisso davanti al testo sorgente, diagnosi vuota o glossario dai candidati
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

            if (delivery != null)
            {
                // Consegna dello scenario, anche non valida, per i giri di correzione e i conflitti fra file
                toolName = request.Kind == TranslationCallKind.D1 ? "submit_diagnosis" : request.Kind == TranslationCallKind.A1 || request.Kind == TranslationCallKind.A2 ? GlossaryBuilder.SUBMIT_TOOL : "submit_translation";
                arguments = delivery.Arguments != null ? delivery.Arguments.DeepClone().AsObject() : null;
            }
            else if (request.Kind == TranslationCallKind.A1 || request.Kind == TranslationCallKind.A2)
            {
                // Glossario sintetico valido: in A1 un nome invariato per candidato, in A2 la prima variante di ogni conflitto
                string message = prepared.Input.Count > 0 ? AiOAuthFlow.GetString(prepared.Input[0] as JsonObject, "content") : "";
                JsonArray terms = new JsonArray();
                JsonArray characters = new JsonArray();
                JsonArray addressing = new JsonArray();
                int candidatesStart = message.IndexOf(CANDIDATES_HEADER, StringComparison.Ordinal);
                int conflictsStart = message.IndexOf(CONFLICTS_HEADER, StringComparison.Ordinal);

                if (request.Kind == TranslationCallKind.A2 && conflictsStart >= 0)
                {
                    int jsonStart = conflictsStart + CONFLICTS_HEADER.Length;
                    int jsonEnd = message.IndexOf("\n\n", jsonStart, StringComparison.Ordinal);
                    JsonObject conflicts = JsonNode.Parse(jsonEnd >= 0 ? message.Substring(jsonStart, jsonEnd - jsonStart) : message.Substring(jsonStart)).AsObject();
                    foreach (JsonNode conflict in conflicts["terms"].AsArray())
                    {
                        JsonObject entry = conflict["variants"][0].DeepClone().AsObject();
                        entry.Remove("episodes");
                        entry.Insert(0, "source", AiOAuthFlow.GetString(conflict.AsObject(), "source"));
                        terms.Add(entry);
                    }

                    foreach (JsonNode conflict in conflicts["characters"].AsArray())
                    {
                        JsonObject entry = conflict["variants"][0].DeepClone().AsObject();
                        entry.Remove("episodes");
                        entry.Insert(0, "name", AiOAuthFlow.GetString(conflict.AsObject(), "name"));
                        characters.Add(entry);
                    }

                    JsonArray pairs = conflicts["addressing"] as JsonArray;
                    for (int i = 0; pairs != null && i < pairs.Count; i++)
                    {
                        JsonObject entry = pairs[i]["variants"][0].DeepClone().AsObject();
                        entry.Remove("episodes");
                        entry.Insert(0, "addressee", AiOAuthFlow.GetString(pairs[i].AsObject(), "addressee"));
                        entry.Insert(0, "speaker", AiOAuthFlow.GetString(pairs[i].AsObject(), "speaker"));
                        addressing.Add(entry);
                    }
                }
                else if (candidatesStart >= 0)
                {
                    string[] rows = message.Substring(candidatesStart + CANDIDATES_HEADER.Length).Split('\n');
                    for (int i = 1; i < rows.Length && rows[i].Length > 0; i++)
                    {
                        string term = rows[i].Substring(0, rows[i].IndexOf('\t'));
                        terms.Add(new JsonObject { ["source"] = term, ["target"] = term, ["kind"] = "name", ["invariable"] = true });
                    }
                }

                toolName = GlossaryBuilder.SUBMIT_TOOL;
                arguments["series"] = SYNTHETIC_PREFIX + "series";
                arguments["tone"] = SYNTHETIC_PREFIX + "tone";
                arguments["register_rules"] = SYNTHETIC_PREFIX + "register rules";
                arguments["terms"] = terms;
                arguments["characters"] = characters;
                arguments["addressing"] = addressing;
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

                JsonArray newTerms = new JsonArray();
                if (fault != null && fault.Kind == RecordedTranslationFaultKind.NewTerm)
                    newTerms.Add(new JsonObject { ["source"] = fault.Term, ["target"] = SYNTHETIC_PREFIX + fault.Term });

                toolName = "submit_translation";
                arguments["translations"] = translations;
                arguments["new_terms"] = newTerms;
            }

            // La ricerca web offerta lascia un item web_search_call, come nelle risposte vere
            AiResponseResult result = new AiResponseResult();
            for (int i = 0; i < prepared.Tools.Count; i++)
            {
                if (AiOAuthFlow.GetString(prepared.Tools[i] as JsonObject, "type") == TranslationProviderCapabilities.WEB_SEARCH)
                    result.Items.Add(new JsonObject { ["type"] = "web_search_call", ["id"] = "ws_recorded", ["status"] = "completed" });
            }

            // Una consegna dello scenario senza argomenti è una risposta di solo testo, senza chiamata al tool
            string argumentsJson = arguments != null ? arguments.ToJsonString() : "";
            if (arguments != null)
            {
                result.Items.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["id"] = "fc_recorded",
                    ["call_id"] = "call_recorded",
                    ["name"] = toolName,
                    ["arguments"] = argumentsJson,
                    ["status"] = "completed"
                });
            }
            else
            {
                result.Items.Add(new JsonObject
                {
                    ["type"] = "message",
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Which series is this?" })
                });
            }
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
