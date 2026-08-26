using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using System.IO;
using System.Runtime.InteropServices;

namespace RemuxForge.Core.Tools
{
    /// <summary>
    /// Individua l'eseguibile mkvmerge nelle posizioni note del sistema
    /// </summary>
    public class MkvMergeProvider : ToolProviderBase
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMergeProvider()
        {
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Individua mkvmerge nel sistema
        /// Ordine: AppSettings → posizioni note → PATH
        /// </summary>
        /// <param name="autoSave">Se true, salva il percorso trovato in AppSettings</param>
        /// <returns>True se mkvmerge è stato trovato</returns>
        public bool Resolve(bool autoSave)
        {
            bool resolved = false;
            string mkvName = "mkvmerge" + GetExecutableExtension();
            string found;
            // Controlla percorso salvato in AppSettings
            if (IsExecutablePath(AppSettingsService.Instance.Settings.Tools.MkvMergePath))
            {
                this._resolvedPath = AppSettingsService.Instance.Settings.Tools.MkvMergePath;
                resolved = true;
            }

            // Controlla posizioni note del sistema
            if (!resolved)
            {
                found = SearchInPaths(mkvName, this.GetWellKnownPaths());
                if (IsExecutablePath(found))
                {
                    this._resolvedPath = found;
                    resolved = true;
                }
            }

            // Controlla il PATH di sistema
            if (!resolved)
            {
                found = FindInSystemPath(mkvName);
                if (IsExecutablePath(found))
                {
                    this._resolvedPath = found;
                    resolved = true;
                }
            }

            // Salva percorso trovato in AppSettings per le prossime volte
            if (autoSave && resolved && this._resolvedPath != AppSettingsService.Instance.Settings.Tools.MkvMergePath)
            {
                AppSettingsService.Instance.Settings.Tools.MkvMergePath = this._resolvedPath;
                AppSettingsService.Instance.Save();
            }

            return resolved;
        }

        /// <summary>
        /// Verifica che il path punti a un mkvmerge CLI eseguibile
        /// </summary>
        /// <param name="path">Percorso da verificare</param>
        /// <returns>True se mkvmerge risponde a --version</returns>
        public static bool IsExecutablePath(string path)
        {
            bool result = false;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return result;
            }

            try
            {
                ProcessResult processResult = ProcessRunner.Run(path, new string[] { "--version" });
                result = !string.IsNullOrEmpty(processResult.Stdout);
            }
            catch
            {
                result = false;
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Restituisce le posizioni di installazione note per mkvmerge per ogni OS
        /// </summary>
        /// <returns>Array di percorsi di ricerca</returns>
        private string[] GetWellKnownPaths()
        {
            string[] paths;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // MKVToolNix viene installato sempre nella stessa posizione su Windows
                paths = new string[]
                {
                    @"C:\Program Files\MKVToolNix",
                    @"C:\Program Files (x86)\MKVToolNix"
                };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                paths = new string[] { "/usr/bin", "/usr/local/bin", "/snap/bin" };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                paths = new string[] { "/usr/local/bin", "/opt/homebrew/bin" };
            }
            else
            {
                paths = new string[0];
            }

            return paths;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percorso risolto dell'eseguibile mkvmerge
        /// </summary>
        public string MkvMergePath { get { return this._resolvedPath; } }

        #endregion
    }
}
