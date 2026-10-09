using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Ai
{
    /// <summary>
    /// Servizio del file di configurazione dell'integrazione AI: credenziali, modello e consenso
    /// </summary>
    public class AiCredentialService
    {
        #region Costanti

        /// <summary>
        /// Nome del file di configurazione AI
        /// </summary>
        public const string FILE_NAME = "ai-credentials.json";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso completo del file
        /// </summary>
        private readonly string _filePath;

        /// <summary>
        /// Serializza lettura e scrittura del file
        /// </summary>
        private readonly object _fileLock;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="configFolder">Cartella configurazione RemuxForge</param>
        public AiCredentialService(string configFolder)
        {
            string root = configFolder != null ? configFolder : "";
            this._filePath = Path.Combine(root, FILE_NAME);
            this._fileLock = new object();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge la configurazione AI, vuota se il file non esiste
        /// </summary>
        /// <returns>Configurazione AI</returns>
        public AiCredentials Load()
        {
            AiCredentials result;

            lock (this._fileLock)
            {
                if (!File.Exists(this._filePath))
                    return new AiCredentials();

                result = JsonSerializer.Deserialize<AiCredentials>(File.ReadAllText(this._filePath), CreateSerializerOptions());
            }

            if (result == null)
                result = new AiCredentials();

            Normalize(result);
            return result;
        }

        /// <summary>
        /// Salva la configurazione AI con scrittura atomica
        /// </summary>
        /// <param name="credentials">Configurazione da salvare</param>
        public void Save(AiCredentials credentials)
        {
            if (credentials == null)
                throw new ArgumentNullException(nameof(credentials));

            Normalize(credentials);
            string json = JsonSerializer.Serialize(credentials, CreateSerializerOptions());
            string tempFilePath = this._filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            lock (this._fileLock)
            {
                try
                {
                    string folder = Path.GetDirectoryName(this._filePath);
                    if (!string.IsNullOrEmpty(folder))
                        Directory.CreateDirectory(folder);

                    FileStreamOptions streamOptions = new FileStreamOptions();
                    streamOptions.Mode = FileMode.CreateNew;
                    streamOptions.Access = FileAccess.Write;
                    streamOptions.Share = FileShare.None;

                    // Il file contiene token e API key: su macOS e Linux nasce leggibile solo dal proprietario
                    if (!OperatingSystem.IsWindows())
                        streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                    using (FileStream stream = new FileStream(tempFilePath, streamOptions))
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(json);
                        writer.Flush();
                        stream.Flush(true);
                    }

                    File.Move(tempFilePath, this._filePath, true);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempFilePath))
                            File.Delete(tempFilePath);
                    }
                    catch (IOException)
                    {
                        // La scrittura ha già propagato l'errore principale; la pulizia resta best-effort
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // La scrittura ha già propagato l'errore principale; la pulizia resta best-effort
                    }
                }
            }
        }

        /// <summary>
        /// Legge la configurazione garantendo l'identificativo stabile dell'installazione
        /// </summary>
        /// <returns>Configurazione AI con ExtAgentHostId valorizzato</returns>
        public AiCredentials LoadWithHostId()
        {
            AiCredentials credentials = this.Load();
            if (string.IsNullOrEmpty(credentials.ExtAgentHostId))
            {
                credentials.ExtAgentHostId = "urn:uuid:" + Guid.NewGuid().ToString("D");
                this.Save(credentials);
            }

            return credentials;
        }

        /// <summary>
        /// Indica se l'AI è configurata con un account collegato o una API key
        /// </summary>
        /// <param name="credentials">Configurazione AI</param>
        /// <returns>True se configurata</returns>
        public static bool IsConfigured(AiCredentials credentials)
        {
            if (credentials == null)
                return false;

            if (credentials.Mode == AiConnectionMode.ChatGptAccount)
                return !string.IsNullOrEmpty(credentials.RefreshToken);

            if (credentials.Mode == AiConnectionMode.ApiKey)
                return !string.IsNullOrEmpty(credentials.ApiKey);

            return false;
        }

        /// <summary>
        /// Registra i token di un login ChatGPT riuscito
        /// </summary>
        /// <param name="credentials">Configurazione aggiornata dal login</param>
        public void SaveAccountConnection(AiCredentials credentials)
        {
            if (credentials == null)
                throw new ArgumentNullException(nameof(credentials));

            credentials.Mode = AiConnectionMode.ChatGptAccount;
            credentials.ApiKey = "";
            this.Save(credentials);
        }

        /// <summary>
        /// Scollega l'account: cancella solo i token, conserva client ID, email, host ID, modello e consenso
        /// </summary>
        /// <returns>Configurazione aggiornata</returns>
        public AiCredentials ClearAccountTokens()
        {
            AiCredentials credentials = this.Load();
            credentials.AccessToken = "";
            credentials.RefreshToken = "";
            credentials.IdToken = "";
            credentials.Scopes = new List<string>();
            credentials.AccessTokenExpiresAt = 0;
            if (credentials.Mode == AiConnectionMode.ChatGptAccount)
                credentials.Mode = AiConnectionMode.None;

            this.Save(credentials);
            return credentials;
        }

        /// <summary>
        /// Imposta la API key; rifiutata finché un account è collegato
        /// </summary>
        /// <param name="apiKey">API key</param>
        /// <returns>Configurazione aggiornata</returns>
        public AiCredentials SetApiKey(string apiKey)
        {
            string key = apiKey != null ? apiKey.Trim() : "";
            AiCredentials credentials = this.Load();

            if (string.IsNullOrEmpty(key))
                throw new ArgumentException(AppText.T("ai.credentials.apiKeyEmpty"), nameof(apiKey));

            if (credentials.Mode == AiConnectionMode.ChatGptAccount && !string.IsNullOrEmpty(credentials.RefreshToken))
                throw new InvalidOperationException(AppText.T("ai.credentials.disconnectBeforeApiKey"));

            credentials.Mode = AiConnectionMode.ApiKey;
            credentials.ApiKey = key;
            this.Save(credentials);
            return credentials;
        }

        /// <summary>
        /// Rimuove la API key
        /// </summary>
        /// <returns>Configurazione aggiornata</returns>
        public AiCredentials RemoveApiKey()
        {
            AiCredentials credentials = this.Load();
            credentials.ApiKey = "";
            if (credentials.Mode == AiConnectionMode.ApiKey)
                credentials.Mode = AiConnectionMode.None;

            this.Save(credentials);
            return credentials;
        }

        /// <summary>
        /// Salva il modello scelto
        /// </summary>
        /// <param name="model">Identificativo modello</param>
        public void SetModel(string model)
        {
            AiCredentials credentials = this.Load();
            credentials.Model = model != null ? model.Trim() : "";
            this.Save(credentials);
        }

        /// <summary>
        /// Salva i giri massimi del modello, ricondotti all'intervallo ammesso
        /// </summary>
        /// <param name="maxRounds">Giri massimi</param>
        public void SetMaxRounds(int maxRounds)
        {
            AiCredentials credentials = this.Load();
            credentials.MaxRounds = Math.Clamp(maxRounds, MetadataAiPresetGenerator.MIN_MAX_ROUNDS, MetadataAiPresetGenerator.MAX_MAX_ROUNDS);
            this.Save(credentials);
        }

        /// <summary>
        /// Registra il consenso all'invio dei dati
        /// </summary>
        public void AcceptConsent()
        {
            AiCredentials credentials = this.Load();
            credentials.ConsentAccepted = true;
            this.Save(credentials);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Crea le opzioni JSON del file di configurazione AI
        /// </summary>
        /// <returns>Opzioni serializzatore</returns>
        private static JsonSerializerOptions CreateSerializerOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.WriteIndented = true;
            options.PropertyNameCaseInsensitive = true;
            options.Converters.Add(new JsonStringEnumConverter(null, false));
            return options;
        }

        /// <summary>
        /// Sostituisce i valori null letti dal file con i default
        /// </summary>
        /// <param name="credentials">Configurazione da normalizzare</param>
        private static void Normalize(AiCredentials credentials)
        {
            if (credentials.ExtAgentHostId == null)
                credentials.ExtAgentHostId = "";
            if (credentials.Model == null)
                credentials.Model = "";
            if (credentials.Email == null)
                credentials.Email = "";
            if (credentials.Subject == null)
                credentials.Subject = "";
            if (credentials.ClientId == null)
                credentials.ClientId = "";
            if (credentials.AccessToken == null)
                credentials.AccessToken = "";
            if (credentials.RefreshToken == null)
                credentials.RefreshToken = "";
            if (credentials.IdToken == null)
                credentials.IdToken = "";
            if (credentials.Scopes == null)
                credentials.Scopes = new List<string>();
            if (credentials.ApiKey == null)
                credentials.ApiKey = "";
            if (credentials.MaxRounds < MetadataAiPresetGenerator.MIN_MAX_ROUNDS || credentials.MaxRounds > MetadataAiPresetGenerator.MAX_MAX_ROUNDS)
                credentials.MaxRounds = MetadataAiPresetGenerator.DEFAULT_MAX_ROUNDS;
        }

        #endregion
    }
}
