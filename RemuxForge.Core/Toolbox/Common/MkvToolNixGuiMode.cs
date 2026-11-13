using RemuxForge.Core.Infrastructure;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Toolbox.Common
{
    /// <summary>
    /// Esegue mkvmerge o mkvextract in --gui-mode e ne smista le righe: avanzamento in percentuale, avvisi ed errori
    /// </summary>
    public static class MkvToolNixGuiMode
    {
        #region Costanti

        /// <summary>
        /// Exit code di mkvmerge e mkvextract per esecuzione riuscita con avvisi
        /// </summary>
        public const int WARNING_EXIT_CODE = 1;

        /// <summary>
        /// Prefisso delle righe di avanzamento
        /// </summary>
        private const string PROGRESS_PREFIX = "#GUI#progress ";

        /// <summary>
        /// Prefisso degli avvisi
        /// </summary>
        private const string WARNING_PREFIX = "#GUI#warning ";

        /// <summary>
        /// Prefisso degli errori
        /// </summary>
        private const string ERROR_PREFIX = "#GUI#error ";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Lancia lo strumento con --gui-mode davanti agli argomenti; l'annullamento dell'operazione corrente lo interrompe
        /// </summary>
        /// <param name="executable">Percorso di mkvmerge o mkvextract</param>
        /// <param name="arguments">Argomenti dello strumento, senza --gui-mode</param>
        /// <param name="onProgress">Avanzamento in percentuale; null per ignorarlo</param>
        /// <param name="onWarning">Riceve ogni avviso; null per ignorarli</param>
        /// <param name="errors">Riceve gli errori, nell'ordine in cui arrivano; null per ignorarli</param>
        /// <returns>Esito del processo, con lo stderr</returns>
        public static ProcessResult Run(string executable, IEnumerable<string> arguments, Action<int> onProgress, Action<string> onWarning, List<string> errors)
        {
            List<string> all = new List<string> { "--gui-mode" };

            all.AddRange(arguments);
            return ProcessRunner.RunWithStdoutLines(executable, all, line =>
            {
                int percent;
                if (line.StartsWith(PROGRESS_PREFIX, StringComparison.Ordinal) &&
                    int.TryParse(line.Substring(PROGRESS_PREFIX.Length).TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out percent))
                    onProgress?.Invoke(percent);
                else if (line.StartsWith(WARNING_PREFIX, StringComparison.Ordinal))
                    onWarning?.Invoke(line.Substring(WARNING_PREFIX.Length).Trim());
                else if (line.StartsWith(ERROR_PREFIX, StringComparison.Ordinal))
                    errors?.Add(line.Substring(ERROR_PREFIX.Length).Trim());
            });
        }

        #endregion
    }
}
