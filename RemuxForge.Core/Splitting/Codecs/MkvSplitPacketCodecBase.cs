using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Cutters;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// Codec tagliato a pacchetti: testa e coda ricodificate con lo stesso codec e corpo copiato selezionando i pacchetti del sorgente
    /// per PTS, con le parti accodate da mkvmerge. Serve ai codec il cui flusso non si copia a byte da un elementary stream
    /// </summary>
    public abstract class MkvSplitPacketCodecBase : MkvSplitExactCutCodecBase
    {
        #region Proprietà

        /// <summary>
        /// Taglio a pacchetti
        /// </summary>
        public override MkvSplitExactCutMethod Method => MkvSplitExactCutMethod.Packets;

        /// <summary>
        /// Filtri di bitstream del corpo copiato dopo la selezione per PTS, ognuno preceduto dalla virgola; vuoto se non ne servono
        /// </summary>
        public abstract string BodyBitstreamFilters { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Strategia del taglio a pacchetti
        /// </summary>
        /// <returns>Strategia nuova sul codec</returns>
        public override MkvSplitExactCutterBase CreateCutter()
        {
            return new MkvSplitPacketCutter(this);
        }

        #endregion
    }
}
