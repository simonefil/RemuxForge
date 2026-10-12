using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Splitting.Segments
{
    /// <summary>
    /// Modalità Pattern: raggruppa i capitoli secondo --pattern, che deve sommare al numero di capitoli del file; senza pattern
    /// nessuna modalità è stata scelta e l'errore elenca i capitoli
    /// </summary>
    public sealed class MkvSplitPatternSegmentStrategy : MkvSplitChapterGroupSegmentStrategyBase
    {
        #region Variabili di classe

        /// <summary>
        /// Pattern del tipo "5,5,5,6", vuoto se non indicato
        /// </summary>
        private readonly string _pattern;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="pattern">Pattern delle opzioni, vuoto o null se non indicato</param>
        public MkvSplitPatternSegmentStrategy(string pattern)
        {
            this._pattern = pattern;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Modalità Pattern
        /// </summary>
        protected override MkvSplitMode Mode { get { return MkvSplitMode.Pattern; } }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Gruppi del pattern, dopo il controllo della somma; il riepilogo va nel log
        /// </summary>
        /// <param name="service">Non usato: il pattern non produce avvisi</param>
        /// <param name="chapters">Capitoli estratti dal sorgente, null se assenti</param>
        /// <returns>Capitoli di ogni episodio</returns>
        protected override int[] GroupSizes(MkvSplitSegmentService service, List<ChapterMark> chapters)
        {
            int count = chapters != null ? chapters.Count : 0;
            if (string.IsNullOrEmpty(this._pattern))
            {
                // Nessuna modalità scelta: l'errore elenca i capitoli per suggerire un pattern valido
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.T("split.noModeSelected"));
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.Count("split.fileHasChapters", count));
                for (int i = 0; i < count; i++)
                {
                    string name = chapters[i].Name ?? string.Empty;
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.chapterLine", i + 1, Utils.SecsToTs(chapters[i].StartSeconds), name));
                }
                throw new ArgumentException(AppText.T("split.noModeSelectedShort"));
            }

            string[] parts = this._pattern.Split(',');
            int[] sizes = new int[parts.Length];
            int sum = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                sizes[i] = int.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
                sum += sizes[i];
            }
            if (sum != count)
            {
                throw new ArgumentException(AppText.Count("split.patternSumMismatch", count, sum));
            }

            ConsoleHelper.Write(LogSection.Split, LogLevel.Info, AppText.Count("split.patternSummary", sizes.Length, string.Join(",", sizes)));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "");
            return sizes;
        }

        #endregion
    }
}
