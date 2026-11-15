using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Accesso al modello per un job di traduzione: un'istanza per job, che tiene le capacità spente e la pausa condivisa dai file
    /// in parallelo
    /// </summary>
    public interface ITranslationModelProvider
    {
        /// <summary>
        /// Manda una chiamata con la politica di retry del job; un parametro rifiutato dall'accesso si toglie e la chiamata si ripete
        /// una volta
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Item di output, consumo ed esito</returns>
        Task<AiResponseResult> SendAsync(TranslationModelRequest request, CancellationToken cancellationToken);

        /// <summary>
        /// Elenca i modelli selezionabili con i loro livelli di ragionamento
        /// </summary>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Modelli ordinati per nome</returns>
        Task<List<AiModelInfo>> ListModelsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Capacità dell'accesso corrente nel job
        /// </summary>
        TranslationProviderCapabilities Capabilities { get; }
    }
}
