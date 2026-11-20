using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Ai
{
    /// <summary>
    /// Servizio del file di configurazione dell'integrazione AI: credenziali e modello del Metadata AI
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
        /// File di configurazione AI
        /// </summary>
        private readonly PrivateJsonFile<AiCredentials> _file;

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
            this._file = new PrivateJsonFile<AiCredentials>(configFolder, FILE_NAME);
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
                result = this._file.Read();
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

            lock (this._fileLock)
            {
                this._file.Write(credentials);
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
        /// Scollega l'account: cancella solo i token, conserva client ID, email, host ID, modello e livello di ragionamento
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
        /// Salva modello e livello di ragionamento del Metadata AI
        /// </summary>
        /// <param name="model">Identificativo modello</param>
        /// <param name="reasoningEffort">Livello di ragionamento, vuoto per non mandarlo</param>
        public void SetModel(string model, string reasoningEffort)
        {
            AiCredentials credentials = this.Load();
            credentials.Model = model != null ? model.Trim() : "";
            credentials.ReasoningEffort = reasoningEffort != null ? reasoningEffort.Trim() : "";
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

        #endregion

        #region Metodi privati

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
            if (credentials.ReasoningEffort == null)
                credentials.ReasoningEffort = "";
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
