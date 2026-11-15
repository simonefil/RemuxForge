using RemuxForge.Core.Ai;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Base dei provider del modello: prepara la richiesta secondo le capacità del job e, se l'accesso rifiuta un parametro
    /// facoltativo, lo spegne per il resto del job e ripete la chiamata una volta
    /// </summary>
    public abstract class TranslationModelProviderBase : ITranslationModelProvider
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="accessMode">Modalità di accesso del job</param>
        /// <param name="retry">Politica di retry del job, con la pausa condivisa dai file in parallelo</param>
        /// <param name="disabledCapabilities">Capacità già spente nel job, null se nessuna</param>
        protected TranslationModelProviderBase(AiConnectionMode accessMode, AiRetryPolicy retry, IEnumerable<string> disabledCapabilities)
        {
            this.Retry = retry;
            this.Capabilities = new TranslationProviderCapabilities(accessMode, disabledCapabilities);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Manda una chiamata con la politica di retry del job; un parametro rifiutato dall'accesso si toglie e la chiamata si ripete
        /// una volta
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output, consumo ed esito</returns>
        public async Task<AiResponseResult> SendAsync(TranslationModelRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            AiResponseRequest prepared = this.Capabilities.Prepare(request);
            try
            {
                return await this.SendPreparedAsync(request, prepared, cancellationToken);
            }
            catch (AiServiceException ex)
            {
                if (ex.Kind != AiErrorKind.UnsupportedCapability)
                    throw;

                string capability = this.Capabilities.ResolveRejected(ex.RejectedParameter, prepared);
                if (string.IsNullOrEmpty(capability))
                    throw;

                this.Capabilities.Disable(capability);
            }

            return await this.SendPreparedAsync(request, this.Capabilities.Prepare(request), cancellationToken);
        }

        /// <summary>
        /// Elenca i modelli selezionabili con i loro livelli di ragionamento
        /// </summary>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Modelli ordinati per nome</returns>
        public abstract Task<List<AiModelInfo>> ListModelsAsync(CancellationToken cancellationToken);

        #endregion

        #region Metodi protected

        /// <summary>
        /// Manda la richiesta già preparata secondo le capacità, con la politica di retry del job
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="prepared">Richiesta da mandare</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output, consumo ed esito</returns>
        protected abstract Task<AiResponseResult> SendPreparedAsync(TranslationModelRequest request, AiResponseRequest prepared, CancellationToken cancellationToken);

        #endregion

        #region Proprietà

        /// <summary>
        /// Capacità dell'accesso corrente nel job
        /// </summary>
        public TranslationProviderCapabilities Capabilities { get; private set; }

        /// <summary>
        /// Politica di retry del job
        /// </summary>
        protected AiRetryPolicy Retry { get; private set; }

        #endregion
    }
}
