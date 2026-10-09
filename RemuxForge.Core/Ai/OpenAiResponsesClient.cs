using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
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
                throw CreateError(credentials.Mode, status, root != null ? root["error"] as JsonObject : null);

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
                    result.Add(info);
                }
            }

            result.Sort((left, right) => string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase));
            return result;
        }

        /// <summary>
        /// Esegue una richiesta Responses in streaming senza stato lato server
        /// </summary>
        /// <param name="model">Modello</param>
        /// <param name="instructions">Istruzioni di sistema</param>
        /// <param name="input">Storico completo degli item di input</param>
        /// <param name="tools">Tool disponibili</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output del modello, da rimandare invariati nel giro successivo</returns>
        public async Task<List<JsonObject>> CreateResponseAsync(string model, string instructions, JsonArray input, JsonArray tools, CancellationToken cancellationToken)
        {
            AiCredentials credentials = await this.GetAuthorizedCredentialsAsync(cancellationToken);
            JsonObject body = new JsonObject();

            body["model"] = model;
            body["instructions"] = instructions;
            body["input"] = input.DeepClone();
            if (tools != null && tools.Count > 0)
                body["tools"] = tools.DeepClone();

            // Senza stato lato server il ragionamento torna al modello solo come contenuto cifrato
            body["store"] = false;
            body["stream"] = true;
            body["include"] = new JsonArray("reasoning.encrypted_content");

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, API_BASE_URL + "/responses"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetBearer(credentials));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                try
                {
                    using (HttpResponseMessage response = await this._httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            JsonObject error = AiOAuthFlow.TryParseObject(await response.Content.ReadAsStringAsync(cancellationToken));
                            throw CreateError(credentials.Mode, (int)response.StatusCode, error != null ? error["error"] as JsonObject : null);
                        }

                        using (Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            return await ReadStreamAsync(reader, credentials.Mode, cancellationToken);
                        }
                    }
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
        }

        #endregion

        #region Metodi privati

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
        /// Legge lo stream SSE della Responses API e raccoglie gli item completati
        /// </summary>
        /// <param name="reader">Lettore dello stream</param>
        /// <param name="mode">Modalità di accesso, per la mappatura degli errori</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output</returns>
        private static async Task<List<JsonObject>> ReadStreamAsync(StreamReader reader, AiConnectionMode mode, CancellationToken cancellationToken)
        {
            List<JsonObject> doneItems = new List<JsonObject>();
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

                if (type == "response.output_item.done")
                {
                    JsonObject item = streamEvent["item"] as JsonObject;
                    if (item != null)
                        doneItems.Add((JsonObject)item.DeepClone());
                }
                else if (type == "response.completed")
                {
                    // Con store:false l'output finale può arrivare vuoto: valgono gli item raccolti dallo stream
                    JsonObject responseObject = streamEvent["response"] as JsonObject;
                    JsonArray output = responseObject != null ? responseObject["output"] as JsonArray : null;
                    if (output == null || output.Count == 0)
                        return doneItems;

                    List<JsonObject> result = new List<JsonObject>();
                    for (int i = 0; i < output.Count; i++)
                    {
                        JsonObject item = output[i] as JsonObject;
                        if (item != null)
                            result.Add((JsonObject)item.DeepClone());
                    }

                    return result;
                }
                else if (type == "response.failed")
                {
                    JsonObject responseObject = streamEvent["response"] as JsonObject;
                    throw CreateError(mode, 0, responseObject != null ? responseObject["error"] as JsonObject : null);
                }
                else if (type == "response.incomplete")
                {
                    throw new AiServiceException(AiErrorKind.InvalidResponse, AppText.T("ai.error.incomplete"));
                }
                else if (type == "error")
                {
                    JsonObject error = streamEvent["error"] as JsonObject;
                    throw CreateError(mode, 0, error != null ? error : streamEvent);
                }
            }

            // Lo stream valido termina sempre con response.completed
            throw new AiServiceException(AiErrorKind.InvalidResponse, AppText.T("ai.error.incomplete"));
        }

        /// <summary>
        /// Converte un errore del servizio nella categoria con messaggio localizzato
        /// </summary>
        /// <param name="mode">Modalità di accesso</param>
        /// <param name="status">Stato HTTP, zero per errori dello stream</param>
        /// <param name="error">Oggetto errore del servizio</param>
        /// <returns>Eccezione</returns>
        private static AiServiceException CreateError(AiConnectionMode mode, int status, JsonObject error)
        {
            string code = error != null ? AiOAuthFlow.GetString(error, "code") : "";
            string message = error != null ? AiOAuthFlow.GetString(error, "message") : "";

            if (code == "subscription_sharing_usage_limit_exceeded" || (status == 429 && string.IsNullOrEmpty(code)) || code == "rate_limit_exceeded" || code == "insufficient_quota")
                return new AiServiceException(AiErrorKind.UsageLimit, AppText.T(mode == AiConnectionMode.ChatGptAccount ? "ai.error.usageLimitAccount" : "ai.error.usageLimitApiKey"));

            if (code == "subscription_sharing_user_not_eligible")
                return new AiServiceException(AiErrorKind.NotEligible, AppText.T("ai.error.notEligible"));

            if (code == "subscription_sharing_usage_unavailable" || status == 503)
                return new AiServiceException(AiErrorKind.Unavailable, AppText.T("ai.error.unavailable"));

            if (code == "subscription_sharing_invalid_user" || status == 401)
            {
                if (mode == AiConnectionMode.ApiKey)
                    return new AiServiceException(AiErrorKind.InvalidApiKey, AppText.T("ai.error.invalidApiKey"));

                return new AiServiceException(AiErrorKind.SignInRequired, AppText.T("ai.error.signInRequired"));
            }

            if (code == "subscription_sharing_unsupported_capability" || code == "subscription_sharing_route_not_supported")
                return new AiServiceException(AiErrorKind.Unsupported, AppText.F("ai.error.unsupported", string.IsNullOrEmpty(message) ? code : message));

            return new AiServiceException(AiErrorKind.Unknown, AppText.F("ai.error.service", status, string.IsNullOrEmpty(message) ? code : message));
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

        #endregion
    }
}
