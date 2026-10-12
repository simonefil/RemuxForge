using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Segments
{
    /// <summary>
    /// Costruzione dei segmenti di una modalità di taglio, scelta dalle opzioni nell'ordine Manuale, capitoli a blocchi,
    /// un capitolo per file, intervalli, pattern
    /// </summary>
    public abstract class MkvSplitSegmentStrategyBase
    {
        #region Metodi pubblici

        /// <summary>
        /// Strategia della modalità chiesta dalle opzioni; senza modalità quella del pattern, che spiega cosa manca
        /// </summary>
        /// <param name="args">Opzioni Split del file, con le scorciatoie già tradotte</param>
        /// <returns>Strategia della modalità</returns>
        public static MkvSplitSegmentStrategyBase For(MkvSplitOptions args)
        {
            if (args.Manual)
                return new MkvSplitManualSegmentStrategy();
            if (args.ChaptersPerEpisode > 0)
                return new MkvSplitChapterBlockSegmentStrategy(args.ChaptersPerEpisode, MkvSplitMode.ChaptersPerEpisode);
            if (args.ChaptersEach)
                return new MkvSplitChapterBlockSegmentStrategy(1, MkvSplitMode.ChaptersEach);
            if (!string.IsNullOrEmpty(args.Ranges))
                return new MkvSplitRangeSegmentStrategy(args.Ranges, args.SplitAt);
            return new MkvSplitPatternSegmentStrategy(args.Pattern);
        }

        /// <summary>
        /// Costruisce i segmenti della modalità. I capitoli dei segmenti li assegna il planner sui confini effettivi, dopo lo snap
        /// </summary>
        /// <param name="service">Servizio che raccoglie gli avvisi e legge gli intervalli</param>
        /// <param name="chapters">Capitoli estratti dal sorgente</param>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente</param>
        /// <param name="duration">Durata totale del file in secondi</param>
        /// <returns>Segmenti e modalità effettiva</returns>
        public abstract (List<MkvSplitSegment> segments, MkvSplitMode mode) Build(MkvSplitSegmentService service, List<ChapterMark> chapters, double[] sourcePts, double duration);

        #endregion
    }
}
