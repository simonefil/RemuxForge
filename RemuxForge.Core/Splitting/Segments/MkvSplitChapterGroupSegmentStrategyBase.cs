using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Segments
{
    /// <summary>
    /// Modalità che raggruppano capitoli consecutivi: ogni gruppo diventa un segmento dal fotogramma del suo primo capitolo a quello
    /// del gruppo successivo; l'ultimo chiude alla fine del file
    /// </summary>
    public abstract class MkvSplitChapterGroupSegmentStrategyBase : MkvSplitSegmentStrategyBase
    {
        #region Proprietà

        /// <summary>
        /// Modalità dei segmenti prodotti
        /// </summary>
        protected abstract MkvSplitMode Mode { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Un segmento per gruppo di capitoli
        /// </summary>
        /// <param name="service">Servizio che raccoglie gli avvisi</param>
        /// <param name="chapters">Capitoli estratti dal sorgente</param>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente</param>
        /// <param name="duration">Durata totale del file in secondi</param>
        /// <returns>Segmenti e modalità della strategia</returns>
        public override (List<MkvSplitSegment> segments, MkvSplitMode mode) Build(MkvSplitSegmentService service, List<ChapterMark> chapters, double[] sourcePts, double duration)
        {
            int[] groups = this.GroupSizes(service, chapters);
            int chapterCount = chapters != null ? chapters.Count : 0;
            List<MkvSplitSegment> segments = new List<MkvSplitSegment>(groups.Length);
            int first = 0;

            for (int i = 0; i < groups.Length; i++)
            {
                // Range frame reale del blocco di capitoli (O(log N) per ciascun episodio)
                int startFrame = MkvSplitSegmentService.ChapterFrame(sourcePts, chapters[first].StartSeconds);
                int endFrame = first + groups[i] >= chapterCount ? sourcePts.Length : MkvSplitSegmentService.ChapterFrame(sourcePts, chapters[first + groups[i]].StartSeconds);
                segments.Add(MkvSplitSegment.FromFrames(i + 1, startFrame, endFrame, sourcePts, duration));
                first += groups[i];
            }

            return (segments, this.Mode);
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Numero di capitoli di ogni gruppo, nell'ordine: valida i capitoli e registra gli avvisi della modalità
        /// </summary>
        /// <param name="service">Servizio che raccoglie gli avvisi</param>
        /// <param name="chapters">Capitoli estratti dal sorgente, null se assenti</param>
        /// <returns>Dimensione dei gruppi</returns>
        protected abstract int[] GroupSizes(MkvSplitSegmentService service, List<ChapterMark> chapters);

        #endregion
    }
}
