using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Segments
{
    /// <summary>
    /// Blocchi di un numero fisso di capitoli, adattandosi a qualunque conteggio: un capitolo per file oppure k capitoli per episodio
    /// </summary>
    public sealed class MkvSplitChapterBlockSegmentStrategy : MkvSplitChapterGroupSegmentStrategyBase
    {
        #region Variabili di classe

        /// <summary>
        /// Capitoli per blocco
        /// </summary>
        private readonly int _size;

        /// <summary>
        /// Modalità dei segmenti prodotti
        /// </summary>
        private readonly MkvSplitMode _mode;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="size">Capitoli per blocco, almeno uno</param>
        /// <param name="mode">Modalità dichiarata dai segmenti</param>
        public MkvSplitChapterBlockSegmentStrategy(int size, MkvSplitMode mode)
        {
            this._size = size;
            this._mode = mode;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Modalità dei segmenti prodotti
        /// </summary>
        protected override MkvSplitMode Mode { get { return this._mode; } }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Blocchi pieni e un ultimo blocco più corto, legittimo ma dichiarato: cambia la durata dell'ultimo episodio
        /// </summary>
        /// <param name="service">Servizio che raccoglie gli avvisi</param>
        /// <param name="chapters">Capitoli estratti dal sorgente, null se assenti</param>
        /// <returns>Dimensione dei blocchi</returns>
        protected override int[] GroupSizes(MkvSplitSegmentService service, List<ChapterMark> chapters)
        {
            int count = chapters != null ? chapters.Count : 0;
            if (count == 0)
            {
                throw new ArgumentException(AppText.T("split.chaptersEachRequiresChapters"));
            }

            int[] sizes = new int[(count + this._size - 1) / this._size];
            for (int i = 0; i < sizes.Length; i++)
                sizes[i] = Math.Min(this._size, count - i * this._size);
            if (count % this._size != 0)
            {
                service.AddWarning(MkvSplitWarningKind.ChapterGrouping, AppText.F("split.warnChaptersPerEpisodeRemainder", this._size, count, count % this._size));
            }

            return sizes;
        }

        #endregion
    }
}
