using RemuxForge.Core.Configuration;
using System.IO;
using System.Runtime.InteropServices;

namespace RemuxForge.Core.Tools
{
    /// <summary>
    /// Individua l'eseguibile mediainfo nelle posizioni note del sistema
    /// </summary>
    public class MediaInfoProvider : ToolProviderBase
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MediaInfoProvider()
        {
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Individua mediainfo nel sistema
        /// Ordine: AppSettings → posizioni note → PATH
        /// </summary>
        /// <param name="autoSave">Se true, salva il percorso trovato in AppSettings</param>
        /// <returns>True se mediainfo è stato trovato</returns>
        public bool Resolve(bool autoSave)
        {
            bool resolved = false;
            string miName = "mediainfo" + GetExecutableExtension();
            string found;
            // Controlla percorso salvato in AppSettings
            if (!string.IsNullOrEmpty(AppSettingsService.Instance.Settings.Tools.MediaInfoPath)
                && File.Exists(AppSettingsService.Instance.Settings.Tools.MediaInfoPath)
                && IsCliExecutablePath(AppSettingsService.Instance.Settings.Tools.MediaInfoPath))
            {
                this._resolvedPath = AppSettingsService.Instance.Settings.Tools.MediaInfoPath;
                resolved = true;
            }

            // Controlla posizioni note del sistema (solo CLI, non app bundle macOS)
            if (!resolved && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                found = SearchInPaths(miName, this.GetWellKnownPaths());
                if (!string.IsNullOrEmpty(found))
                {
                    this._resolvedPath = found;
                    resolved = true;
                }
            }

            // Controlla il PATH di sistema
            if (!resolved)
            {
                found = FindInSystemPath(miName);
                if (!string.IsNullOrEmpty(found))
                {
                    this._resolvedPath = found;
                    resolved = true;
                }
            }

            // Salva percorso trovato in AppSettings per le prossime volte
            if (autoSave && resolved && this._resolvedPath != AppSettingsService.Instance.Settings.Tools.MediaInfoPath)
            {
                AppSettingsService.Instance.Settings.Tools.MediaInfoPath = this._resolvedPath;
                AppSettingsService.Instance.Save();
            }

            return resolved;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Restituisce le posizioni di installazione note per mediainfo per ogni OS
        /// </summary>
        /// <returns>Array di percorsi di ricerca</returns>
        private string[] GetWellKnownPaths()
        {
            string[] paths;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                paths = new string[]
                {
                    @"C:\Program Files\MediaInfo",
                    @"C:\Program Files (x86)\MediaInfo"
                };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                paths = new string[] { "/usr/bin", "/usr/local/bin" };
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

        /// <summary>
        /// Verifica che il path punti alla CLI e non al bundle grafico macOS
        /// </summary>
        /// <param name="path">Percorso da verificare</param>
        /// <returns>True se il path è utilizzabile come CLI</returns>
        public static bool IsCliExecutablePath(string path)
        {
            bool result = false;
            if (string.IsNullOrEmpty(path))
            {
                return result;
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && path.Contains(".app/Contents/MacOS"))
            {
                return result;
            }

            result = true;
            return result;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percorso risolto dell'eseguibile mediainfo
        /// </summary>
        public string MediaInfoPath { get { return this._resolvedPath; } }

        #endregion
    }
}
