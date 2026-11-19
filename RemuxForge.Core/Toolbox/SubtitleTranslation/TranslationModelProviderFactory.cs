using RemuxForge.Core.Ai;
using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Crea il provider del modello di un job: quello reale, oppure quello registrato quando è indicato uno scenario
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

        /// <summary>
        /// File JSON dello scenario del provider registrato, vuoto per il provider reale
        /// </summary>
        private readonly string _recordedScenarioPath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="client">Client della Responses API</param>
        /// <param name="credentialService">Servizio del file di configurazione AI</param>
        /// <param name="recordedScenarioPath">File JSON dello scenario del provider registrato, vuoto per il provider reale</param>
        public TranslationModelProviderFactory(OpenAiResponsesClient client, AiCredentialService credentialService, string recordedScenarioPath)
        {
            this._client = client;
            this._credentialService = credentialService;
            this._recordedScenarioPath = recordedScenarioPath != null ? recordedScenarioPath : "";
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

            // Lo scenario si rilegge a ogni job, così si cambia senza riavviare
            if (!string.IsNullOrEmpty(this._recordedScenarioPath))
            {
                JsonSerializerOptions options = new JsonSerializerOptions();
                options.PropertyNameCaseInsensitive = true;
                options.Converters.Add(new JsonStringEnumConverter(null, false));
                RecordedTranslationScenario scenario = JsonSerializer.Deserialize<RecordedTranslationScenario>(File.ReadAllText(this._recordedScenarioPath, Encoding.UTF8), options);
                return new RecordedTranslationModelProvider(scenario, accessMode, AiRetryPolicy.CreateDefault(), disabledCapabilities);
            }

            return new OpenAiTranslationModelProvider(this._client, accessMode, AiRetryPolicy.CreateDefault(), disabledCapabilities);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True se i provider creati possono chiamare il modello: quello reale vuole le credenziali dell'accesso configurato, come
        /// il client le chiede a ogni chiamata; quello registrato non le chiede
        /// </summary>
        public bool IsAccessConfigured
        {
            get { return !string.IsNullOrEmpty(this._recordedScenarioPath) || AiCredentialService.IsConfigured(this._credentialService.Load()); }
        }

        #endregion
    }
}
