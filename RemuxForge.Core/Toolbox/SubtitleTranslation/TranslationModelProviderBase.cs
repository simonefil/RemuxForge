using RemuxForge.Core.Ai;
using RemuxForge.Core.Localization;
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
        #region Eventi

        /// <summary>
        /// Cambio di stato dello stream di una chiamata, dal thread che legge la risposta
        /// </summary>
        public event Action<TranslationModelRequest, AiStreamProgress> CallProgress;

        /// <summary>
        /// Chiamata conclusa, riuscita, fallita o fermata
        /// </summary>
        public event Action<TranslationModelRequest> CallFinished;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Politica di retry del job, con la pausa condivisa dai file in parallelo
        /// </summary>
        private readonly AiRetryPolicy _retry;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="accessMode">Modalità di accesso del job</param>
        /// <param name="disabledCapabilities">Capacità già spente nel job, null se nessuna</param>
        protected TranslationModelProviderBase(AiConnectionMode accessMode, IEnumerable<string> disabledCapabilities)
        {
            this._retry = AiRetryPolicy.CreateDefault();
            this.Capabilities = new TranslationProviderCapabilities(accessMode, disabledCapabilities);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Manda una chiamata con la politica di retry del job; un parametro rifiutato dall'accesso si toglie e la chiamata si ripete
        /// una volta. Lo stream avvisa con CallProgress e la fine della chiamata con CallFinished
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output, consumo ed esito</returns>
        public async Task<AiResponseResult> SendAsync(TranslationModelRequest request, CancellationToken cancellationToken)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            Action<AiStreamProgress> progress = stream => this.CallProgress?.Invoke(request, stream);
            AiResponseRequest prepared = this.Capabilities.Prepare(request);
            prepared.Progress = progress;
            try
            {
                try
                {
                    return await this.SendWithRetryAsync(request, prepared, cancellationToken);
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

                prepared = this.Capabilities.Prepare(request);
                prepared.Progress = progress;
                return await this.SendWithRetryAsync(request, prepared, cancellationToken);
            }
            finally
            {
                this.CallFinished?.Invoke(request);
            }
        }

        /// <summary>
        /// Elenca i modelli selezionabili con i loro livelli di ragionamento
        /// </summary>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Modelli ordinati per nome</returns>
        public abstract Task<List<AiModelInfo>> ListModelsAsync(CancellationToken cancellationToken);

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Manda un tentativo della richiesta già preparata secondo le capacità; il retry lo fa la base
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="prepared">Richiesta da mandare</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output, consumo ed esito</returns>
        protected abstract Task<AiResponseResult> SendAttemptAsync(TranslationModelRequest request, AiResponseRequest prepared, CancellationToken cancellationToken);

        #endregion

        #region Metodi privati

        /// <summary>
        /// Manda una richiesta preparata con la politica di retry del job e avvisa la chiamata di ogni tentativo arrivato al servizio,
        /// anche di quello interrotto da Stop. Il timeout del client arriva come annullamento senza che l'operazione sia ferma:
        /// diventa un errore di rete
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="prepared">Richiesta preparata secondo le capacità</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Risposta dell'ultimo tentativo</returns>
        private Task<AiResponseResult> SendWithRetryAsync(TranslationModelRequest request, AiResponseRequest prepared, CancellationToken cancellationToken)
        {
            return this._retry.ExecuteAsync(async attempt =>
            {
                TranslationTransportAttempt tracked = new TranslationTransportAttempt(attempt, prepared);
                try
                {
                    tracked.Result = await this.SendAttemptAsync(request, prepared, cancellationToken);
                }
                catch (Exception ex)
                {
                    AiServiceException error = ex as AiServiceException;
                    bool stopped = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
                    if (error == null && !stopped)
                    {
                        if (!(ex is OperationCanceledException))
                            throw;
                        error = new AiServiceException(AiErrorKind.Network, AppText.F("ai.error.network", ex.Message), ex);
                    }

                    tracked.Error = error;
                    tracked.Stopped = stopped;
                    tracked.Finish();
                    if (request.AttemptFinished != null)
                        request.AttemptFinished(tracked);
                    if (error != null && !ReferenceEquals(error, ex))
                        throw error;
                    throw;
                }

                tracked.Finish();
                if (request.AttemptFinished != null)
                    request.AttemptFinished(tracked);
                return tracked.Result;
            }, cancellationToken);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Capacità dell'accesso corrente nel job
        /// </summary>
        public TranslationProviderCapabilities Capabilities { get; private set; }

        #endregion
    }
}
