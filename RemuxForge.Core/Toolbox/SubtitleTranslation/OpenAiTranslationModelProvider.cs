using RemuxForge.Core.Ai;
using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Provider del modello sopra la Responses API di OpenAI
    /// </summary>
    public class OpenAiTranslationModelProvider : TranslationModelProviderBase
    {
        #region Variabili di classe

        /// <summary>
        /// Client della Responses API
        /// </summary>
        private readonly OpenAiResponsesClient _client;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="client">Client della Responses API</param>
        /// <param name="accessMode">Modalità di accesso del job</param>
        /// <param name="retry">Politica di retry del job</param>
        /// <param name="disabledCapabilities">Capacità già spente nel job, null se nessuna</param>
        public OpenAiTranslationModelProvider(OpenAiResponsesClient client, AiConnectionMode accessMode, AiRetryPolicy retry, IEnumerable<string> disabledCapabilities) : base(accessMode, retry, disabledCapabilities)
        {
            this._client = client;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Elenca i modelli dell'accesso configurato con i loro livelli di ragionamento
        /// </summary>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Modelli ordinati per nome</returns>
        public override Task<List<AiModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
        {
            return this._client.ListModelsAsync(cancellationToken);
        }

        #endregion

        #region Metodi protected

        /// <summary>
        /// Manda un tentativo della richiesta preparata alla Responses API
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="prepared">Richiesta da mandare</param>
        /// <param name="attempt">Tentativo del retry di trasporto, da 1</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output, consumo ed esito</returns>
        protected override Task<AiResponseResult> SendAttemptAsync(TranslationModelRequest request, AiResponseRequest prepared, int attempt, CancellationToken cancellationToken)
        {
            // Il retry lo fa la base, che registra ogni tentativo
            return this._client.CreateResponseAsync(prepared, AiRetryPolicy.None, cancellationToken);
        }

        #endregion
    }
}
