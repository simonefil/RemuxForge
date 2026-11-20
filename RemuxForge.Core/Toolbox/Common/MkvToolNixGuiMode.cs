using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media;
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

        /// <summary>
        /// True se lo strumento è terminato bene, anche con avvisi
        /// </summary>
        /// <param name="exitCode">Exit code di mkvmerge o mkvextract</param>
        /// <returns>True per esecuzione riuscita</returns>
        public static bool IsSuccess(int exitCode)
        {
            return exitCode == 0 || exitCode == MediaContainerNormalizer.MKVMERGE_WARNING_EXIT_CODE;
        }

        /// <summary>
        /// True se mkvmerge non è riuscito; senza errori in --gui-mode il motivo diventa lo stderr, o in sua mancanza il testo
        /// dell'exit code
        /// </summary>
        /// <param name="run">Esito del processo</param>
        /// <param name="errors">Errori raccolti da Run, completati con il motivo</param>
        /// <param name="exitCodeMessage">Motivo da usare senza errori né stderr</param>
        /// <returns>True per esecuzione non riuscita</returns>
        public static bool Failed(ProcessResult run, List<string> errors, string exitCodeMessage)
        {
            if (IsSuccess(run.ExitCode))
                return false;

            if (errors.Count == 0)
                errors.Add(run.Stderr.Trim().Length > 0 ? run.Stderr.Trim() : exitCodeMessage);
            return true;
        }

        #endregion
    }
}
