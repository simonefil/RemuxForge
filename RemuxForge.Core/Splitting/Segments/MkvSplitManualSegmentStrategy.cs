using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Segments
{
    /// <summary>
    /// Modalità Manuale: nessun segmento finché non li costruisce l'editor
    /// </summary>
    public sealed class MkvSplitManualSegmentStrategy : MkvSplitSegmentStrategyBase
    {
        #region Metodi pubblici

        /// <summary>
        /// Nessun segmento
        /// </summary>
        /// <param name="service">Non usato</param>
        /// <param name="chapters">Non usati</param>
        /// <param name="sourcePts">Non usati</param>
        /// <param name="duration">Non usata</param>
        /// <returns>Lista vuota e modalità Manuale</returns>
        public override (List<MkvSplitSegment> segments, MkvSplitMode mode) Build(MkvSplitSegmentService service, List<ChapterMark> chapters, double[] sourcePts, double duration)
        {
            return (new List<MkvSplitSegment>(), MkvSplitMode.Manual);
        }

        #endregion
    }
}
