using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Ai
{
    /// <summary>
    /// Flusso OAuth di Sign in with ChatGPT: autorizzazione PKCE con registrazione dinamica, scambio codice, rinnovo e revoca
    /// </summary>
    public class AiOAuthFlow
    {
        #region Costanti

        /// <summary>
        /// Endpoint di autorizzazione
        /// </summary>
        private const string AUTHORIZE_URL = "https://auth.openai.com/api/accounts/authorize";

        /// <summary>
        /// Endpoint token
        /// </summary>
        private const string TOKEN_URL = "https://auth.openai.com/api/accounts/oauth/token";

        /// <summary>
        /// Configurazione OpenID, da cui si leggono JWKS e revoca
        /// </summary>
        private const string OPENID_CONFIGURATION_URL = "https://auth.openai.com/.well-known/openid-configuration";

        /// <summary>
        /// Issuer atteso nell'ID token
        /// </summary>
        private const string ISSUER = "https://auth.openai.com";

        /// <summary>
        /// Resource dei token, uguale in autorizzazione, scambio e rinnovo
        /// </summary>
        private const string RESOURCE = "https://api.openai.com/v1";

        /// <summary>
        /// Scope richiesti per l'uso del piano
        /// </summary>
        private const string SCOPES = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";

        /// <summary>
        /// Client ID della registrazione dinamica, usato solo al primo login
        /// </summary>
        private const string DYNAMIC_CLIENT_ID = "dynamic_agent_client";

        /// <summary>
        /// Nome dell'applicazione mostrato da OpenAI alla registrazione
        /// </summary>
        private const string AGENT_NAME = "RemuxForge";

        /// <summary>
        /// Percorso fisso dell'indirizzo di ritorno loopback
        /// </summary>
        private const string CALLBACK_PATH = "/callback";

        /// <summary>
        /// Tolleranza sugli orari dell'ID token in secondi
        /// </summary>
        private const int CLOCK_SKEW_SECONDS = 300;

        /// <summary>
        /// Codici di errore del rinnovo che rendono inutilizzabili i token
        /// </summary>
        private static readonly string[] UNUSABLE_TOKEN_ERRORS = new string[] { "invalid_grant", "invalid_refresh_token", "token_expired" };

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Client HTTP del flusso OAuth
        /// </summary>
        private readonly HttpClient _httpClient;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public AiOAuthFlow()
        {
            this._httpClient = new HttpClient();
            this._httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce l'indirizzo di ritorno loopback per la porta indicata
        /// </summary>
        /// <param name="port">Porta locale in ascolto</param>
        /// <returns>Redirect URI</returns>
        public static string BuildRedirectUri(int port)
        {
            return "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + CALLBACK_PATH;
        }

        /// <summary>
        /// Crea un tentativo di login: PKCE, state, nonce e URL di autorizzazione
        /// </summary>
        /// <param name="credentials">Configurazione AI con ExtAgentHostId valorizzato</param>
        /// <param name="redirectUri">Indirizzo di ritorno loopback</param>
        /// <returns>Tentativo di login</returns>
        public AiAuthorizationRequest CreateAuthorization(AiCredentials credentials, string redirectUri)
        {
            AiAuthorizationRequest request = new AiAuthorizationRequest();
            Dictionary<string, string> query = new Dictionary<string, string>();
            StringBuilder url = new StringBuilder(AUTHORIZE_URL);
            bool firstLogin = string.IsNullOrEmpty(credentials.ClientId);

            if (string.IsNullOrEmpty(credentials.ExtAgentHostId))
                throw new InvalidOperationException(AppText.T("ai.oauth.missingHostId"));

            request.RedirectUri = redirectUri;
            request.State = CreateRandomToken(24);
            request.Nonce = CreateRandomToken(24);
            request.CodeVerifier = CreateRandomToken(32);
            request.ClientId = firstLogin ? DYNAMIC_CLIENT_ID : credentials.ClientId;

            query["client_id"] = request.ClientId;
            query["ext_agent_host_id"] = credentials.ExtAgentHostId;
            query["response_type"] = "code";
            query["redirect_uri"] = redirectUri;
            query["scope"] = SCOPES;
            query["resource"] = RESOURCE;
            query["state"] = request.State;
            query["nonce"] = request.Nonce;
            query["code_challenge_method"] = "S256";
            query["code_challenge"] = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier)));

            // La registrazione dinamica avviene solo al primo login; dopo si riusa il client ID rilasciato
            if (firstLogin)
                query["agent_name_hint"] = AGENT_NAME;
            else if (!string.IsNullOrEmpty(credentials.Email))
                query["login_hint"] = credentials.Email;

            bool first = true;
            foreach (KeyValuePair<string, string> pair in query)
            {
                url.Append(first ? "?" : "&");
                url.Append(pair.Key).Append('=').Append(Uri.EscapeDataString(pair.Value));
                first = false;
            }

            request.AuthorizationUrl = url.ToString();
            return request;
        }

        /// <summary>
        /// Completa il login dall'indirizzo di ritorno: verifica state, scambia il codice e valida l'ID token
        /// </summary>
        /// <param name="request">Tentativo di login in corso</param>
        /// <param name="callbackUrl">Indirizzo di ritorno ricevuto o incollato dall'utente</param>
        /// <param name="credentials">Configurazione AI da aggiornare</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Configurazione aggiornata, non ancora salvata</returns>
        public async Task<AiCredentials> CompleteAuthorizationAsync(AiAuthorizationRequest request, string callbackUrl, AiCredentials credentials, CancellationToken cancellationToken)
        {
            Dictionary<string, string> callback = ParseCallback(callbackUrl);
            string value;
            string clientId;
            Dictionary<string, string> form = new Dictionary<string, string>();
            JsonObject tokens;
            JsonObject idClaims;

            if (callback.TryGetValue("error", out value))
            {
                string description;
                callback.TryGetValue("error_description", out description);
                throw new AiServiceException(AiErrorKind.InvalidSignIn, AppText.F("ai.oauth.authorizationDenied", string.IsNullOrEmpty(description) ? value : description));
            }

            if (!callback.TryGetValue("state", out value) || !FixedTimeEquals(value, request.State))
                throw new AiServiceException(AiErrorKind.InvalidSignIn, AppText.T("ai.oauth.stateMismatch"));

            if (!callback.TryGetValue("code", out value) || string.IsNullOrEmpty(value))
                throw new AiServiceException(AiErrorKind.InvalidSignIn, AppText.T("ai.oauth.codeMissing"));

            // Il client ID rilasciato arriva solo nella callback del primo login
            if (!callback.TryGetValue("client_id", out clientId) || string.IsNullOrEmpty(clientId))
                clientId = request.ClientId == DYNAMIC_CLIENT_ID ? "" : request.ClientId;

            if (string.IsNullOrEmpty(clientId))
                throw new AiServiceException(AiErrorKind.InvalidSignIn, AppText.T("ai.oauth.clientIdMissing"));

            form["grant_type"] = "authorization_code";
            form["client_id"] = clientId;
            form["code"] = value;
            form["code_verifier"] = request.CodeVerifier;
            form["redirect_uri"] = request.RedirectUri;
            form["resource"] = RESOURCE;
            tokens = await this.PostTokenAsync(form, cancellationToken);

            idClaims = await this.ValidateIdTokenAsync(GetString(tokens, "id_token"), clientId, request.Nonce, cancellationToken);
            credentials.ClientId = clientId;
            credentials.Email = GetString(idClaims, "email");
            credentials.Subject = GetString(idClaims, "sub");
            ApplyTokens(credentials, tokens);
            return credentials;
        }

        /// <summary>
        /// Rinnova l'access token; i token inutilizzabili diventano errore di nuovo login
        /// </summary>
        /// <param name="credentials">Configurazione AI da aggiornare</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        public async Task RefreshAsync(AiCredentials credentials, CancellationToken cancellationToken)
        {
            Dictionary<string, string> form = new Dictionary<string, string>();

            if (string.IsNullOrEmpty(credentials.RefreshToken) || string.IsNullOrEmpty(credentials.ClientId))
                throw new AiServiceException(AiErrorKind.SignInRequired, AppText.T("ai.error.signInRequired"));

            // Lo scope si omette per conservare quello concesso al login
            form["grant_type"] = "refresh_token";
            form["client_id"] = credentials.ClientId;
            form["refresh_token"] = credentials.RefreshToken;
            form["resource"] = RESOURCE;
            ApplyTokens(credentials, await this.PostTokenAsync(form, cancellationToken));
        }

        /// <summary>
        /// Revoca il refresh token presso OpenAI
        /// </summary>
        /// <param name="credentials">Configurazione AI con il refresh token da revocare</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        public async Task RevokeAsync(AiCredentials credentials, CancellationToken cancellationToken)
        {
            JsonObject configuration;
            string revocationUrl;
            Dictionary<string, string> form = new Dictionary<string, string>();

            if (string.IsNullOrEmpty(credentials.RefreshToken) || string.IsNullOrEmpty(credentials.ClientId))
                return;

            configuration = await this.GetJsonAsync(OPENID_CONFIGURATION_URL, cancellationToken);
            revocationUrl = GetString(configuration, "revocation_endpoint");
            if (string.IsNullOrEmpty(revocationUrl))
                throw new AiServiceException(AiErrorKind.InvalidResponse, AppText.T("ai.error.invalidResponse"));

            form["token"] = credentials.RefreshToken;
            form["token_type_hint"] = "refresh_token";
            form["client_id"] = credentials.ClientId;

            try
            {
                using (FormUrlEncodedContent content = new FormUrlEncodedContent(form))
                using (HttpResponseMessage response = await this._httpClient.PostAsync(revocationUrl, content, cancellationToken))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new AiServiceException(AiErrorKind.Unknown, AppText.F("ai.oauth.revokeFailed", (int)response.StatusCode));
                }
            }
            catch (HttpRequestException ex)
            {
                throw new AiServiceException(AiErrorKind.Network, AppText.F("ai.error.network", ex.Message), ex);
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Invia una richiesta all'endpoint token e restituisce la risposta
        /// </summary>
        /// <param name="form">Campi del form</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Risposta token</returns>
        private async Task<JsonObject> PostTokenAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
        {
            string body;
            int status;

            try
            {
                using (FormUrlEncodedContent content = new FormUrlEncodedContent(form))
                using (HttpResponseMessage response = await this._httpClient.PostAsync(TOKEN_URL, content, cancellationToken))
                {
                    body = await response.Content.ReadAsStringAsync(cancellationToken);
                    status = (int)response.StatusCode;
                }
            }
            catch (HttpRequestException ex)
            {
                throw new AiServiceException(AiErrorKind.Network, AppText.F("ai.error.network", ex.Message), ex);
            }

            JsonObject json = TryParseObject(body);
            if (status >= 200 && status < 300 && json != null && !string.IsNullOrEmpty(GetString(json, "access_token")))
                return json;

            string error = json != null ? GetString(json, "error") : "";
            for (int i = 0; i < UNUSABLE_TOKEN_ERRORS.Length; i++)
            {
                if (string.Equals(error, UNUSABLE_TOKEN_ERRORS[i], StringComparison.Ordinal))
                    throw new AiServiceException(AiErrorKind.SignInRequired, AppText.T("ai.error.signInRequired"));
            }

            string description = json != null ? GetString(json, "error_description") : "";
            throw new AiServiceException(AiErrorKind.InvalidSignIn, AppText.F("ai.oauth.tokenFailed", status, string.IsNullOrEmpty(description) ? error : description));
        }

        /// <summary>
        /// Valida firma RS256, issuer, audience, scadenza e nonce dell'ID token
        /// </summary>
        /// <param name="idToken">ID token</param>
        /// <param name="clientId">Client ID atteso come audience</param>
        /// <param name="nonce">Nonce atteso</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Claim dell'ID token</returns>
        private async Task<JsonObject> ValidateIdTokenAsync(string idToken, string clientId, string nonce, CancellationToken cancellationToken)
        {
            string[] parts = idToken != null ? idToken.Split('.') : new string[0];
            JsonObject header;
            JsonObject claims;
            JsonObject configuration;
            JsonObject jwks;
            JsonArray keys;
            JsonObject key = null;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (parts.Length != 3)
                throw InvalidIdToken();

            header = TryParseObject(Encoding.UTF8.GetString(Base64UrlDecode(parts[0])));
            claims = TryParseObject(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            if (header == null || claims == null || GetString(header, "alg") != "RS256")
                throw InvalidIdToken();

            configuration = await this.GetJsonAsync(OPENID_CONFIGURATION_URL, cancellationToken);
            jwks = await this.GetJsonAsync(GetString(configuration, "jwks_uri"), cancellationToken);
            keys = jwks["keys"] as JsonArray;
            if (keys != null)
            {
                for (int i = 0; i < keys.Count && key == null; i++)
                {
                    JsonObject candidate = keys[i] as JsonObject;
                    if (candidate != null && GetString(candidate, "kid") == GetString(header, "kid"))
                        key = candidate;
                }
            }

            if (key == null)
                throw InvalidIdToken();

            using (RSA rsa = RSA.Create())
            {
                RSAParameters parameters = new RSAParameters();
                parameters.Modulus = Base64UrlDecode(GetString(key, "n"));
                parameters.Exponent = Base64UrlDecode(GetString(key, "e"));
                rsa.ImportParameters(parameters);
                if (!rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64UrlDecode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    throw InvalidIdToken();
            }

            if (GetString(claims, "iss") != ISSUER || !HasAudience(claims, clientId))
                throw InvalidIdToken();

            if (GetLong(claims, "exp") < now - CLOCK_SKEW_SECONDS || !FixedTimeEquals(GetString(claims, "nonce"), nonce))
                throw InvalidIdToken();

            return claims;
        }

        /// <summary>
        /// Legge un documento JSON da un URL
        /// </summary>
        /// <param name="url">URL</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Oggetto JSON</returns>
        private async Task<JsonObject> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            string body;

            if (string.IsNullOrEmpty(url))
                throw new AiServiceException(AiErrorKind.InvalidResponse, AppText.T("ai.error.invalidResponse"));

            try
            {
                body = await this._httpClient.GetStringAsync(url, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new AiServiceException(AiErrorKind.Network, AppText.F("ai.error.network", ex.Message), ex);
            }

            JsonObject json = TryParseObject(body);
            if (json == null)
                throw new AiServiceException(AiErrorKind.InvalidResponse, AppText.T("ai.error.invalidResponse"));

            return json;
        }

        /// <summary>
        /// Copia i token di una risposta nella configurazione
        /// </summary>
        /// <param name="credentials">Configurazione da aggiornare</param>
        /// <param name="tokens">Risposta token</param>
        private static void ApplyTokens(AiCredentials credentials, JsonObject tokens)
        {
            long expiresIn = GetLong(tokens, "expires_in");
            string scope = GetString(tokens, "scope");

            credentials.AccessToken = GetString(tokens, "access_token");

            // Il refresh token ruota a ogni rinnovo; l'ID token arriva anche nei rinnovi
            if (!string.IsNullOrEmpty(GetString(tokens, "refresh_token")))
                credentials.RefreshToken = GetString(tokens, "refresh_token");
            if (!string.IsNullOrEmpty(GetString(tokens, "id_token")))
                credentials.IdToken = GetString(tokens, "id_token");
            if (!string.IsNullOrEmpty(scope))
                credentials.Scopes = new List<string>(scope.Split(' ', StringSplitOptions.RemoveEmptyEntries));

            credentials.AccessTokenExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (expiresIn > 0 ? expiresIn : 3600);
        }

        /// <summary>
        /// Estrae i parametri della query dall'indirizzo di ritorno
        /// </summary>
        /// <param name="callbackUrl">Indirizzo di ritorno</param>
        /// <returns>Parametri della query</returns>
        private static Dictionary<string, string> ParseCallback(string callbackUrl)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            Uri uri;

            if (!Uri.TryCreate(callbackUrl != null ? callbackUrl.Trim() : "", UriKind.Absolute, out uri))
                throw new AiServiceException(AiErrorKind.InvalidSignIn, AppText.T("ai.oauth.callbackInvalid"));

            string query = uri.Query.TrimStart('?');
            string[] pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < pairs.Length; i++)
            {
                string[] pair = pairs[i].Split('=', 2);
                string name = Uri.UnescapeDataString(pair[0].Replace('+', ' '));
                result[name] = pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            }

            return result;
        }

        /// <summary>
        /// Verifica che l'audience dell'ID token contenga il client ID
        /// </summary>
        /// <param name="claims">Claim dell'ID token</param>
        /// <param name="clientId">Client ID atteso</param>
        /// <returns>True se presente</returns>
        private static bool HasAudience(JsonObject claims, string clientId)
        {
            JsonNode audience = claims["aud"];
            JsonArray list = audience as JsonArray;

            if (list == null)
                return GetString(claims, "aud") == clientId;

            for (int i = 0; i < list.Count; i++)
            {
                JsonValue item = list[i] as JsonValue;
                string value;
                if (item != null && item.TryGetValue<string>(out value) && value == clientId)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Crea l'errore di ID token non valido
        /// </summary>
        /// <returns>Eccezione</returns>
        private static AiServiceException InvalidIdToken()
        {
            return new AiServiceException(AiErrorKind.InvalidSignIn, AppText.T("ai.oauth.idTokenInvalid"));
        }

        /// <summary>
        /// Legge una proprietà stringa da un oggetto JSON
        /// </summary>
        /// <param name="source">Oggetto JSON</param>
        /// <param name="name">Nome proprietà</param>
        /// <returns>Valore o stringa vuota</returns>
        internal static string GetString(JsonObject source, string name)
        {
            JsonValue value = source != null ? source[name] as JsonValue : null;
            string result;
            if (value != null && value.TryGetValue<string>(out result))
                return result != null ? result : "";

            return "";
        }

        /// <summary>
        /// Legge una proprietà numerica da un oggetto JSON
        /// </summary>
        /// <param name="source">Oggetto JSON</param>
        /// <param name="name">Nome proprietà</param>
        /// <returns>Valore o zero</returns>
        private static long GetLong(JsonObject source, string name)
        {
            JsonValue value = source != null ? source[name] as JsonValue : null;
            long result;
            if (value != null && value.TryGetValue<long>(out result))
                return result;

            return 0;
        }

        /// <summary>
        /// Interpreta un testo come oggetto JSON
        /// </summary>
        /// <param name="text">Testo</param>
        /// <returns>Oggetto JSON o null</returns>
        internal static JsonObject TryParseObject(string text)
        {
            try
            {
                return JsonNode.Parse(text != null ? text : "") as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Confronta due stringhe in tempo costante
        /// </summary>
        /// <param name="left">Primo valore</param>
        /// <param name="right">Secondo valore</param>
        /// <returns>True se uguali</returns>
        private static bool FixedTimeEquals(string left, string right)
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left != null ? left : ""), Encoding.UTF8.GetBytes(right != null ? right : ""));
        }

        /// <summary>
        /// Genera un valore casuale codificato base64url
        /// </summary>
        /// <param name="byteCount">Byte casuali</param>
        /// <returns>Valore casuale</returns>
        private static string CreateRandomToken(int byteCount)
        {
            return Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));
        }

        /// <summary>
        /// Codifica base64url senza padding
        /// </summary>
        /// <param name="data">Dati</param>
        /// <returns>Testo codificato</returns>
        private static string Base64UrlEncode(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// Decodifica base64url con o senza padding
        /// </summary>
        /// <param name="text">Testo codificato</param>
        /// <returns>Dati decodificati</returns>
        private static byte[] Base64UrlDecode(string text)
        {
            string value = (text != null ? text : "").Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                throw InvalidIdToken();
            }
        }

        #endregion
    }
}
