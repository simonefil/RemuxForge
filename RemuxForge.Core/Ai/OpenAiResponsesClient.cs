using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Ai
{
    /// <summary>
    /// Client HTTP della Responses API di OpenAI, unico per abbonamento ChatGPT e API key
    /// </summary>
    public class OpenAiResponsesClient
    {
        #region Costanti

        /// <summary>
        /// Base URL della API
        /// </summary>
        private const string API_BASE_URL = "https://api.openai.com/v1";

        /// <summary>
        /// Anticipo con cui si rinnova l'access token prima della scadenza, in secondi
        /// </summary>
        private const int REFRESH_MARGIN_SECONDS = 300;

        /// <summary>
        /// Prefissi dei modelli API key che non producono testo con la Responses API
        /// </summary>
        private static readonly string[] EXCLUDED_API_KEY_MODEL_PREFIXES = new string[]
        {
            "text-embedding", "whisper", "tts", "dall-e", "gpt-image", "omni-moderation", "text-moderation",
            "babbage", "davinci", "sora", "gpt-realtime", "gpt-audio", "gpt-4o-realtime", "gpt-4o-audio",
            "gpt-4o-transcribe", "gpt-4o-mini-transcribe", "gpt-4o-mini-tts", "gpt-4o-search", "gpt-4o-mini-search",
            "gpt-4o-mini-realtime", "gpt-4o-mini-audio", "computer-use"
        };

        /// <summary>
        /// Livelli di ragionamento offerti con la API key, il cui elenco modelli non li dichiara
        /// </summary>
        private static readonly string[] API_KEY_REASONING_LEVELS = new string[] { "low", "medium", "high" };

        /// <summary>
        /// Livello di ragionamento predefinito con la API key
        /// </summary>
        private const string API_KEY_DEFAULT_REASONING_LEVEL = "medium";

        /// <summary>
        /// Parametro del riassunto del ragionamento, come lo indica il servizio quando lo rifiuta
        /// </summary>
        private const string REASONING_SUMMARY_PARAMETER = "reasoning.summary";

        /// <summary>
        /// Riassunto del ragionamento chiesto: il dettaglio lo sceglie il modello
        /// </summary>
        private const string REASONING_SUMMARY_MODE = "auto";

        /// <summary>
        /// Passo in caratteri con cui si annuncia la quantità di risposta già scritta
        /// </summary>
        private const int WRITING_STEP_CHARS = 1000;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Servizio del file di configurazione AI
        /// </summary>
        private readonly AiCredentialService _credentialService;

        /// <summary>
        /// Flusso OAuth usato per rinnovare l'access token
        /// </summary>
        private readonly AiOAuthFlow _oauthFlow;

        /// <summary>
        /// Client HTTP della API
        /// </summary>
        private readonly HttpClient _httpClient;

        /// <summary>
        /// Serializza i rinnovi dell'access token
        /// </summary>
        private readonly SemaphoreSlim _refreshLock;

        /// <summary>
        /// True dopo che il servizio ha rifiutato il riassunto del ragionamento: per il resto della sessione non si chiede più
        /// </summary>
        private volatile bool _reasoningSummaryRejected;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="credentialService">Servizio del file di configurazione AI</param>
        /// <param name="oauthFlow">Flusso OAuth</param>
        public OpenAiResponsesClient(AiCredentialService credentialService, AiOAuthFlow oauthFlow)
        {
            this._credentialService = credentialService;
            this._oauthFlow = oauthFlow;
            this._httpClient = new HttpClient();
            this._httpClient.Timeout = TimeSpan.FromMinutes(10);
            this._refreshLock = new SemaphoreSlim(1, 1);
            this._reasoningSummaryRejected = false;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Elenca i modelli selezionabili con l'accesso configurato
        /// </summary>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Modelli ordinati per nome</returns>
        public async Task<List<AiModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        {
            List<AiModelInfo> result = new List<AiModelInfo>();
            AiCredentials credentials = await this.GetAuthorizedCredentialsAsync(cancellationToken);
            string body;
            int status;

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, API_BASE_URL + "/models"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetBearer(credentials));
                try
                {
                    using (HttpResponseMessage response = await this._httpClient.SendAsync(request, cancellationToken))
                    {
                        body = await response.Content.ReadAsStringAsync(cancellationToken);
                        status = (int)response.StatusCode;
                    }
                }
                catch (HttpRequestException ex)
                {
                    throw new AiServiceException(AiErrorKind.Network, AppText.F("ai.error.network", ex.Message), ex);
                }
            }

            JsonObject root = AiOAuthFlow.TryParseObject(body);
            if (status < 200 || status >= 300)
                throw CreateError(credentials.Mode, status, root != null ? root["error"] as JsonObject : null, null);

            if (root == null)
                throw new AiServiceException(AiErrorKind.InvalidResponse, AppText.T("ai.error.invalidResponse"));

            // L'abbonamento risponde con "models" e la visibilità, la API key con l'elenco standard "data"
            JsonArray models = root["models"] as JsonArray;
            JsonArray data = root["data"] as JsonArray;
            if (models != null)
            {
                for (int i = 0; i < models.Count; i++)
                {
                    JsonObject model = models[i] as JsonObject;
                    if (model == null || AiOAuthFlow.GetString(model, "visibility") != "list")
                        continue;

                    AiModelInfo info = new AiModelInfo();
                    info.Id = AiOAuthFlow.GetString(model, "slug");
                    info.DisplayName = AiOAuthFlow.GetString(model, "display_name");
                    if (string.IsNullOrEmpty(info.DisplayName))
                        info.DisplayName = info.Id;
                    info.DefaultReasoningLevel = AiOAuthFlow.GetString(model, "default_reasoning_level");

                    JsonArray levels = model["supported_reasoning_levels"] as JsonArray;
                    for (int j = 0; levels != null && j < levels.Count; j++)
                    {
                        string effort = AiOAuthFlow.GetString(levels[j] as JsonObject, "effort");
                        if (!string.IsNullOrEmpty(effort))
                            info.SupportedReasoningLevels.Add(effort);
                    }

                    if (!string.IsNullOrEmpty(info.Id))
                        result.Add(info);
                }
            }
            else if (data != null)
            {
                for (int i = 0; i < data.Count; i++)
                {
                    JsonObject model = data[i] as JsonObject;
                    string id = model != null ? AiOAuthFlow.GetString(model, "id") : "";
                    if (string.IsNullOrEmpty(id) || IsExcludedApiKeyModel(id))
                        continue;

                    AiModelInfo info = new AiModelInfo();
                    info.Id = id;
                    info.DisplayName = id;
                    info.DefaultReasoningLevel = API_KEY_DEFAULT_REASONING_LEVEL;
                    info.SupportedReasoningLevels.AddRange(API_KEY_REASONING_LEVELS);
                    result.Add(info);
                }
            }

            result.Sort((left, right) => string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>
        /// Esegue una richiesta Responses in streaming senza stato lato server, ripetendola intera secondo la politica di retry
        /// </summary>
        /// <param name="request">Richiesta</param>
        /// <param name="retry">Politica di retry; AiRetryPolicy.None per un solo tentativo</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output, consumo ed esito, completo o troncato</returns>
        public Task<AiResponseResult> CreateResponseAsync(AiResponseRequest request, AiRetryPolicy retry, CancellationToken cancellationToken)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (retry == null)
                throw new ArgumentNullException(nameof(retry));

            return retry.ExecuteAsync(_ => this.SendResponseRequestAsync(request, cancellationToken), cancellationToken);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Esegue un tentativo di trasporto di una richiesta Responses. Il riassunto del ragionamento serve solo all'avanzamento:
        /// se il servizio lo rifiuta (organizzazione non verificata) o rifiuta qualcosa senza dire cosa, la richiesta riparte subito
        /// senza, e per il resto della sessione non si chiede più
        /// </summary>
        /// <param name="request">Richiesta</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Risultato della risposta</returns>
        private async Task<AiResponseResult> SendResponseRequestAsync(AiResponseRequest request, CancellationToken cancellationToken)
        {
            AiCredentials credentials = await this.GetAuthorizedCredentialsAsync(cancellationToken);
            bool apiKey = credentials.Mode == AiConnectionMode.ApiKey;
            bool summary = !string.IsNullOrEmpty(request.ReasoningEffort) && !this._reasoningSummaryRejected;
            JsonObject body = new JsonObject();

            body["model"] = request.Model;
            if (!string.IsNullOrEmpty(request.ReasoningEffort))
            {
                JsonObject reasoning = new JsonObject { ["effort"] = request.ReasoningEffort };
                if (summary)
                    reasoning["summary"] = REASONING_SUMMARY_MODE;
                body["reasoning"] = reasoning;
            }
            body["instructions"] = request.Instructions;
            body["input"] = request.Input.DeepClone();
            if (request.Tools != null && request.Tools.Count > 0)
                body["tools"] = request.Tools.DeepClone();
            if (request.ToolChoice != null)
                body["tool_choice"] = request.ToolChoice.DeepClone();

            // Col piano ChatGPT temperatura e tetto di output non partono; la chiave di cache nel body serve anche al piano, che
            // senza riusa la cache solo fra richieste identiche
            if (apiKey && request.Temperature.HasValue)
                body["temperature"] = request.Temperature.Value;
            if (apiKey && request.MaxOutputTokens.HasValue)
                body["max_output_tokens"] = request.MaxOutputTokens.Value;
            if (!string.IsNullOrEmpty(request.PromptCacheKey))
                body["prompt_cache_key"] = request.PromptCacheKey;

            // Senza stato lato server il ragionamento torna al modello solo come contenuto cifrato
            body["store"] = false;
            body["stream"] = true;
            body["include"] = new JsonArray("reasoning.encrypted_content");

            using (HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Post, API_BASE_URL + "/responses"))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetBearer(credentials));
                message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (!string.IsNullOrEmpty(request.SessionId))
                    message.Headers.TryAddWithoutValidation("session_id", request.SessionId);
                message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                if (request.Progress != null)
                    request.Progress(new AiStreamProgress(AiStreamStage.Sent, ""));

                try
                {
                    using (HttpResponseMessage response = await this._httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            string errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                            TimeSpan? retryAfter = null;
                            RetryConditionHeaderValue retryHeader = response.Headers.RetryAfter;
                            if (retryHeader != null && retryHeader.Delta.HasValue)
                                retryAfter = retryHeader.Delta.Value;
                            else if (retryHeader != null && retryHeader.Date.HasValue)
                                retryAfter = retryHeader.Date.Value > DateTimeOffset.UtcNow ? retryHeader.Date.Value - DateTimeOffset.UtcNow : TimeSpan.Zero;

                            JsonObject error = AiOAuthFlow.TryParseObject(errorText);
                            throw CreateError(credentials.Mode, (int)response.StatusCode, error != null ? error["error"] as JsonObject : null, retryAfter);
                        }

                        using (Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            AiResponseResult result = await ReadStreamAsync(reader, credentials.Mode, request.Progress, cancellationToken);
                            result.HttpStatus = (int)response.StatusCode;
                            return result;
                        }
                    }
                }
                catch (AiServiceException ex) when (summary && (ex.RejectedParameter.StartsWith(REASONING_SUMMARY_PARAMETER, StringComparison.OrdinalIgnoreCase) ||
                    (ex.Kind == AiErrorKind.UnsupportedCapability && ex.RejectedParameter.Length == 0)))
                {
                    // Un rifiuto senza parametro può riguardare il riassunto: si riprova senza, e un rifiuto che resta segue la via solita
                    // delle capacità
                    this._reasoningSummaryRejected = true;
                }
                catch (HttpRequestException ex)
                {
                    throw new AiServiceException(AiErrorKind.Network, AppText.F("ai.error.network", ex.Message), ex);
                }
                catch (IOException ex)
                {
                    throw new AiServiceException(AiErrorKind.Network, AppText.F("ai.error.network", ex.Message), ex);
                }
            }

            return await this.SendResponseRequestAsync(request, cancellationToken);
        }

        /// <summary>
        /// Legge la configurazione e, con l'abbonamento, rinnova l'access token in scadenza
        /// </summary>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Configurazione con credenziali utilizzabili</returns>
        private async Task<AiCredentials> GetAuthorizedCredentialsAsync(CancellationToken cancellationToken)
        {
            AiCredentials credentials = this._credentialService.Load();

            if (!AiCredentialService.IsConfigured(credentials))
                throw new AiServiceException(AiErrorKind.NotConfigured, AppText.T("ai.error.notConfigured"));

            if (credentials.Mode == AiConnectionMode.ApiKey || !NeedsRefresh(credentials))
                return credentials;

            await this._refreshLock.WaitAsync(cancellationToken);
            try
            {
                // Un'altra richiesta può aver già rinnovato mentre questa era in attesa
                credentials = this._credentialService.Load();
                if (!AiCredentialService.IsConfigured(credentials))
                    throw new AiServiceException(AiErrorKind.NotConfigured, AppText.T("ai.error.notConfigured"));

                if (credentials.Mode == AiConnectionMode.ChatGptAccount && NeedsRefresh(credentials))
                {
                    try
                    {
                        await this._oauthFlow.RefreshAsync(credentials, cancellationToken);
                    }
                    catch (AiServiceException ex) when (ex.Kind == AiErrorKind.SignInRequired)
                    {
                        // Token inutilizzabili: l'account risulta scollegato finché l'utente non rifà il login
                        this._credentialService.ClearAccountTokens();
                        throw;
                    }

                    this._credentialService.Save(credentials);
                }

                return credentials;
            }
            finally
            {
                this._refreshLock.Release();
            }
        }

        /// <summary>
        /// Legge lo stream SSE della Responses API fino all'evento finale e avvisa dei cambi di stato: coda, ragionamento col titolo
        /// del riassunto, ricerche web, fonti citate, scrittura della risposta o di una chiamata di funzione, rifiuto. I delta di
        /// testo non avvisano: conta solo l'inizio di ogni item
        /// </summary>
        /// <param name="reader">Lettore dello stream</param>
        /// <param name="mode">Modalità di accesso, per la mappatura degli errori</param>
        /// <param name="progress">Avviso dei cambi di stato, null se non serve</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output, consumo ed esito</returns>
        private static async Task<AiResponseResult> ReadStreamAsync(StreamReader reader, AiConnectionMode mode, Action<AiStreamProgress> progress, CancellationToken cancellationToken)
        {
            List<JsonObject> doneItems = new List<JsonObject>();
            string createdCacheKey = "";
            StringBuilder summary = new StringBuilder();
            bool summaryReported = false;
            bool refusalReported = false;
            string searchItemId = "";
            string searchQuery = "";
            int written = 0;
            string line;

            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                string data = line.Substring(5).Trim();
                if (data == "[DONE]")
                    break;

                JsonObject streamEvent = AiOAuthFlow.TryParseObject(data);
                string type = streamEvent != null ? AiOAuthFlow.GetString(streamEvent, "type") : "";

                if (type == "response.created")
                {
                    createdCacheKey = AiOAuthFlow.GetString(streamEvent["response"] as JsonObject, "prompt_cache_key");
                    Report(progress, AiStreamStage.Started, "");
                }
                else if (type == "response.queued")
                {
                    Report(progress, AiStreamStage.Queued, "");
                }
                else if (type == "response.in_progress")
                {
                    Report(progress, AiStreamStage.Started, "");
                }
                else if (type == "response.output_item.added")
                {
                    JsonObject item = streamEvent["item"] as JsonObject;
                    string itemType = item != null ? AiOAuthFlow.GetString(item, "type") : "";
                    if (itemType == "reasoning")
                    {
                        Report(progress, AiStreamStage.Reasoning, "");
                    }
                    else if (itemType == "web_search_call")
                    {
                        searchItemId = AiOAuthFlow.GetString(item, "id");
                        searchQuery = GetSearchQuery(item);
                        Report(progress, AiStreamStage.WebSearch, searchQuery);
                    }
                    else if (itemType == "message" || itemType == "function_call")
                    {
                        refusalReported = false;
                        written = 0;
                        Report(progress, AiStreamStage.Writing, "");
                    }
                }
                else if (type == "response.web_search_call.in_progress" || type == "response.web_search_call.searching")
                {
                    Report(progress, AiStreamStage.WebSearch, AiOAuthFlow.GetString(streamEvent, "item_id") == searchItemId ? searchQuery : "");
                }
                else if (type == "response.reasoning_summary_part.added")
                {
                    summary.Clear();
                    summaryReported = false;
                }
                else if (type == "response.reasoning_summary_text.delta" || type == "response.reasoning_summary_text.done")
                {
                    if (summaryReported)
                        continue;

                    // Il riassunto apre di solito con un titolo in grassetto: si mostra appena il titolo è chiuso, altrimenti la prima
                    // riga appena è completa, o tutto il testo a fine parte
                    bool done = type == "response.reasoning_summary_text.done";
                    if (done)
                        summary.Clear().Append(AiOAuthFlow.GetString(streamEvent, "text"));
                    else
                        summary.Append(AiOAuthFlow.GetString(streamEvent, "delta"));

                    string text = summary.ToString().TrimStart();
                    int end = text.StartsWith("**", StringComparison.Ordinal) ? text.IndexOf("**", 2, StringComparison.Ordinal) : text.IndexOf('\n');
                    string heading = end >= 0 ? text.Substring(0, end) : done ? text : "";
                    heading = heading.Replace("**", "").Trim();
                    if (heading.Length > 0)
                    {
                        summaryReported = true;
                        Report(progress, AiStreamStage.ReasoningSummary, heading);
                    }
                }
                else if (type == "response.output_text.annotation.added")
                {
                    JsonObject annotation = streamEvent["annotation"] as JsonObject;
                    if (annotation != null && AiOAuthFlow.GetString(annotation, "type") == "url_citation")
                    {
                        string title = AiOAuthFlow.GetString(annotation, "title");
                        Report(progress, AiStreamStage.Citation, string.IsNullOrEmpty(title) ? AiOAuthFlow.GetString(annotation, "url") : title);
                    }
                }
                else if (type == "response.output_text.delta" || type == "response.function_call_arguments.delta")
                {
                    // La quantità scritta si annuncia a ogni migliaio di caratteri: abbastanza per vederla crescere senza ridisegnare a ogni pezzo
                    int before = written / WRITING_STEP_CHARS;
                    written += AiOAuthFlow.GetString(streamEvent, "delta").Length;
                    if (written / WRITING_STEP_CHARS > before)
                        Report(progress, AiStreamStage.Writing, (written / WRITING_STEP_CHARS * WRITING_STEP_CHARS).ToString("N0", CultureInfo.CurrentCulture));
                }
                else if (type == "response.refusal.delta")
                {
                    if (!refusalReported)
                    {
                        refusalReported = true;
                        Report(progress, AiStreamStage.Refusal, "");
                    }
                }
                else if (type == "response.output_item.done")
                {
                    JsonObject item = streamEvent["item"] as JsonObject;
                    if (item != null)
                        doneItems.Add((JsonObject)item.DeepClone());

                    // L'azione della ricerca, con query o pagina, arriva completa solo nell'item finito
                    if (item != null && AiOAuthFlow.GetString(item, "type") == "web_search_call")
                    {
                        JsonObject action = item["action"] as JsonObject;
                        string actionType = action != null ? AiOAuthFlow.GetString(action, "type") : "";
                        if (actionType == "open_page")
                            Report(progress, AiStreamStage.WebPage, AiOAuthFlow.GetString(action, "url"));
                        else if (actionType == "find_in_page")
                            Report(progress, AiStreamStage.WebFind, AiOAuthFlow.GetString(action, "pattern"));
                        else
                            Report(progress, AiStreamStage.WebSearchDone, GetSearchQuery(item));
                    }
                }
                else if (type == "response.completed" || type == "response.incomplete")
                {
                    JsonObject responseObject = streamEvent["response"] as JsonObject;
                    JsonArray output = responseObject != null ? responseObject["output"] as JsonArray : null;
                    AiResponseResult result = new AiResponseResult();

                    // Con store:false l'output finale può arrivare vuoto: valgono gli item raccolti dallo stream
                    if (output == null || output.Count == 0)
                    {
                        result.Items = doneItems;
                    }
                    else
                    {
                        for (int i = 0; i < output.Count; i++)
                        {
                            JsonObject item = output[i] as JsonObject;
                            if (item != null)
                                result.Items.Add((JsonObject)item.DeepClone());
                        }
                    }

                    result.ResponseId = AiOAuthFlow.GetString(responseObject, "id");

                    // La chiave applicata dal servizio sta nella risposta finale; se manca vale quella dell'evento iniziale
                    result.PromptCacheKey = AiOAuthFlow.GetString(responseObject, "prompt_cache_key");
                    if (string.IsNullOrEmpty(result.PromptCacheKey))
                        result.PromptCacheKey = createdCacheKey;
                    result.Usage = ReadUsage(responseObject);

                    if (type == "response.incomplete")
                    {
                        result.Status = AiResponseStatus.Incomplete;
                        result.IncompleteReason = AiOAuthFlow.GetString(responseObject != null ? responseObject["incomplete_details"] as JsonObject : null, "reason");
                    }

                    return result;
                }
                else if (type == "response.failed")
                {
                    // Anche la risposta fallita può riportare il consumo: resta nell'errore per il registro delle chiamate
                    JsonObject responseObject = streamEvent["response"] as JsonObject;
                    AiServiceException failed = CreateError(mode, 0, responseObject != null ? responseObject["error"] as JsonObject : null, null);
                    if (responseObject != null && responseObject["usage"] is JsonObject)
                        failed.Usage = ReadUsage(responseObject);
                    throw failed;
                }
                else if (type == "error")
                {
                    JsonObject error = streamEvent["error"] as JsonObject;
                    throw CreateError(mode, 0, error != null ? error : streamEvent, null);
                }
            }

            // Lo stream valido termina sempre con response.completed o response.incomplete
            throw new AiServiceException(AiErrorKind.InvalidResponse, AppText.T("ai.error.incomplete"));
        }

        /// <summary>
        /// Converte un errore del servizio nella categoria con messaggio localizzato, conservando codice, stato e attesa chiesta
        /// </summary>
        /// <param name="mode">Modalità di accesso</param>
        /// <param name="status">Stato HTTP, zero per errori dello stream</param>
        /// <param name="error">Oggetto errore del servizio</param>
        /// <param name="retryAfter">Attesa letta dall'header Retry-After</param>
        /// <returns>Eccezione</returns>
        private static AiServiceException CreateError(AiConnectionMode mode, int status, JsonObject error, TimeSpan? retryAfter)
        {
            string code = error != null ? AiOAuthFlow.GetString(error, "code") : "";
            string message = error != null ? AiOAuthFlow.GetString(error, "message") : "";
            string param = error != null ? AiOAuthFlow.GetString(error, "param") : "";
            AiErrorKind kind;
            string text;

            if (code == "subscription_sharing_usage_limit_exceeded" || (status == 429 && string.IsNullOrEmpty(code)) || code == "rate_limit_exceeded" || code == "insufficient_quota")
            {
                kind = AiErrorKind.UsageLimit;
                text = AppText.T(mode == AiConnectionMode.ChatGptAccount ? "ai.error.usageLimitAccount" : "ai.error.usageLimitApiKey");
            }
            else if (code == "subscription_sharing_user_not_eligible")
            {
                kind = AiErrorKind.NotEligible;
                text = AppText.T("ai.error.notEligible");
            }
            else if (code == "subscription_sharing_usage_unavailable" || status == 503)
            {
                kind = AiErrorKind.Unavailable;
                text = AppText.T("ai.error.unavailable");
            }
            else if (code == "subscription_sharing_invalid_user" || status == 401)
            {
                kind = mode == AiConnectionMode.ApiKey ? AiErrorKind.InvalidApiKey : AiErrorKind.SignInRequired;
                text = AppText.T(mode == AiConnectionMode.ApiKey ? "ai.error.invalidApiKey" : "ai.error.signInRequired");
            }
            else if (code == "subscription_sharing_unsupported_capability")
            {
                kind = AiErrorKind.UnsupportedCapability;
                text = AppText.F("ai.error.unsupported", string.IsNullOrEmpty(message) ? code : message);
            }
            else if (code == "subscription_sharing_route_not_supported")
            {
                kind = AiErrorKind.Unsupported;
                text = AppText.F("ai.error.unsupported", string.IsNullOrEmpty(message) ? code : message);
            }
            else
            {
                // Con la API key un parametro rifiutato arriva come 400 generico: il nome in error.param permette di toglierlo e ripetere
                kind = mode == AiConnectionMode.ApiKey && status == 400 && code == "unsupported_parameter" && !string.IsNullOrEmpty(param) ? AiErrorKind.UnsupportedCapability : AiErrorKind.Unknown;
                text = AppText.F("ai.error.service", status, string.IsNullOrEmpty(message) ? code : message);
            }

            return new AiServiceException(kind, text, code, status, retryAfter, param);
        }

        /// <summary>
        /// Indica se l'access token è assente o in scadenza
        /// </summary>
        /// <param name="credentials">Configurazione AI</param>
        /// <returns>True se va rinnovato</returns>
        private static bool NeedsRefresh(AiCredentials credentials)
        {
            return string.IsNullOrEmpty(credentials.AccessToken) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= credentials.AccessTokenExpiresAt - REFRESH_MARGIN_SECONDS;
        }

        /// <summary>
        /// Restituisce il bearer della modalità attiva
        /// </summary>
        /// <param name="credentials">Configurazione AI</param>
        /// <returns>Access token o API key</returns>
        private static string GetBearer(AiCredentials credentials)
        {
            return credentials.Mode == AiConnectionMode.ApiKey ? credentials.ApiKey : credentials.AccessToken;
        }

        /// <summary>
        /// Indica se un modello API key va escluso dal dropdown
        /// </summary>
        /// <param name="id">Identificativo modello</param>
        /// <returns>True se escluso</returns>
        private static bool IsExcludedApiKeyModel(string id)
        {
            for (int i = 0; i < EXCLUDED_API_KEY_MODEL_PREFIXES.Length; i++)
            {
                if (id.StartsWith(EXCLUDED_API_KEY_MODEL_PREFIXES[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Avvisa di un cambio di stato dello stream
        /// </summary>
        /// <param name="progress">Destinatario, null se non serve</param>
        /// <param name="stage">Stato</param>
        /// <param name="text">Testo dello stato, vuoto se non ne ha</param>
        private static void Report(Action<AiStreamProgress> progress, AiStreamStage stage, string text)
        {
            if (progress != null)
                progress(new AiStreamProgress(stage, text));
        }

        /// <summary>
        /// Legge la query di un item web_search_call: action.query, altrimenti le action.queries unite
        /// </summary>
        /// <param name="item">Item della ricerca</param>
        /// <returns>Query, vuota se il servizio non la dice</returns>
        private static string GetSearchQuery(JsonObject item)
        {
            JsonObject action = item["action"] as JsonObject;
            if (action == null)
                return "";

            string query = AiOAuthFlow.GetString(action, "query");
            JsonArray queries = action["queries"] as JsonArray;
            if (!string.IsNullOrEmpty(query) || queries == null)
                return query;

            List<string> parts = new List<string>();
            for (int i = 0; i < queries.Count; i++)
            {
                JsonValue value = queries[i] as JsonValue;
                string part;
                if (value != null && value.TryGetValue<string>(out part) && !string.IsNullOrEmpty(part))
                    parts.Add(part);
            }

            return string.Join(" · ", parts);
        }

        /// <summary>
        /// Legge il consumo di token dalla risposta finale dello stream
        /// </summary>
        /// <param name="responseObject">Oggetto response dell'evento, anche null</param>
        /// <returns>Consumo, coi campi assenti a null</returns>
        private static AiUsage ReadUsage(JsonObject responseObject)
        {
            JsonObject usage = responseObject != null ? responseObject["usage"] as JsonObject : null;
            JsonObject toolUsage = responseObject != null ? responseObject["tool_usage"] as JsonObject : null;
            AiUsage result = new AiUsage();
            result.InputTokens = GetOptionalLong(usage, "input_tokens");
            result.CachedTokens = GetOptionalLong(usage != null ? usage["input_tokens_details"] as JsonObject : null, "cached_tokens");
            result.CacheWriteTokens = GetOptionalLong(usage != null ? usage["input_tokens_details"] as JsonObject : null, "cache_write_tokens");
            result.OutputTokens = GetOptionalLong(usage, "output_tokens");
            result.ReasoningTokens = GetOptionalLong(usage != null ? usage["output_tokens_details"] as JsonObject : null, "reasoning_tokens");
            result.WebSearchRequests = GetOptionalLong(toolUsage != null ? toolUsage["web_search"] as JsonObject : null, "num_requests");
            return result;
        }

        /// <summary>
        /// Legge una proprietà numerica facoltativa da un oggetto JSON
        /// </summary>
        /// <param name="source">Oggetto JSON, anche null</param>
        /// <param name="name">Nome proprietà</param>
        /// <returns>Valore o null se assente</returns>
        private static long? GetOptionalLong(JsonObject source, string name)
        {
            JsonValue value = source != null ? source[name] as JsonValue : null;
            long result;
            if (value != null && value.TryGetValue<long>(out result))
                return result;

            return null;
        }

        #endregion
    }
}
