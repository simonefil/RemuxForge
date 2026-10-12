using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Segments
{
    /// <summary>
    /// Modalità a intervalli: un segmento per intervallo. Un solo intervallo è un Trim, e --split-at, già riscritto in intervalli,
    /// resta SplitAt
    /// </summary>
    public sealed class MkvSplitRangeSegmentStrategy : MkvSplitSegmentStrategyBase
    {
        #region Variabili di classe

        /// <summary>
        /// Intervalli nella sintassi del core
        /// </summary>
        private readonly string _ranges;

        /// <summary>
        /// Punti di taglio chiesti, vuoti se gli intervalli non vengono da --split-at
        /// </summary>
        private readonly string _splitAt;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ranges">Intervalli delle opzioni</param>
        /// <param name="splitAt">Punti di taglio delle opzioni, vuoti o null se non indicati</param>
        public MkvSplitRangeSegmentStrategy(string ranges, string splitAt)
        {
            this._ranges = ranges;
            this._splitAt = splitAt;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Un segmento per intervallo, nell'ordine scritto
        /// </summary>
        /// <param name="service">Servizio che legge gli intervalli e ne raccoglie gli avvisi</param>
        /// <param name="chapters">Non usati</param>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente</param>
        /// <param name="duration">Durata totale del file in secondi</param>
        /// <returns>Segmenti e modalità SplitAt, Trim o Ranges</returns>
        public override (List<MkvSplitSegment> segments, MkvSplitMode mode) Build(MkvSplitSegmentService service, List<ChapterMark> chapters, double[] sourcePts, double duration)
        {
            List<(int startFrame, int endFrame)> frameRanges = service.ParseRanges(this._ranges, sourcePts, duration);
            // --split-at è già stato riscritto in range, ma resta leggibile nelle opzioni: senza
            // questo la griglia direbbe "estrai intervalli" a chi ha chiesto di tagliare a un punto
            MkvSplitMode mode = !string.IsNullOrEmpty(this._splitAt) ? MkvSplitMode.SplitAt : frameRanges.Count == 1 ? MkvSplitMode.Trim : MkvSplitMode.Ranges;
            List<MkvSplitSegment> segments = new List<MkvSplitSegment>(frameRanges.Count);

            for (int i = 0; i < frameRanges.Count; i++)
            {
                segments.Add(MkvSplitSegment.FromFrames(i + 1, frameRanges[i].startFrame, frameRanges[i].endFrame, sourcePts, duration));
            }

            return (segments, mode);
        }

        #endregion
    }
}
