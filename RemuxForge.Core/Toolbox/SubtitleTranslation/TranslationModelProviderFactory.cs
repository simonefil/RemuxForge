using RemuxForge.Core.Ai;
using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Crea il provider del modello di un job
    /// </summary>
    public class TranslationModelProviderFactory
    {
        #region Variabili di classe

        /// <summary>
        /// Client della Responses API
        /// </summary>
        private readonly OpenAiResponsesClient _client;

        /// <summary>
        /// Servizio del file di configurazione AI, per la modalità di accesso
        /// </summary>
        private readonly AiCredentialService _credentialService;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="client">Client della Responses API</param>
        /// <param name="credentialService">Servizio del file di configurazione AI</param>
        public TranslationModelProviderFactory(OpenAiResponsesClient client, AiCredentialService credentialService)
        {
            this._client = client;
            this._credentialService = credentialService;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Crea il provider di un job, con la sua politica di retry e la sua pausa condivisa
        /// </summary>
        /// <param name="disabledCapabilities">Capacità già spente nel job, null se nessuna</param>
        /// <returns>Provider del job</returns>
        public ITranslationModelProvider Create(IEnumerable<string> disabledCapabilities)
        {
            AiConnectionMode accessMode = this._credentialService.Load().Mode;
            return new OpenAiTranslationModelProvider(this._client, accessMode, AiRetryPolicy.CreateDefault(), disabledCapabilities);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True se i provider creati possono chiamare il modello: servono le credenziali dell'accesso configurato, come il client
        /// le chiede a ogni chiamata
        /// </summary>
        public bool IsAccessConfigured
        {
            get { return AiCredentialService.IsConfigured(this._credentialService.Load()); }
        }

        #endregion
    }
}
