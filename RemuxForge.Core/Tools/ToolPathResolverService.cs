using RemuxForge.Core.Configuration;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RemuxForge.Core.Tools
{
    /// <summary>
    /// Centro unico per la risoluzione dei binari esterni
    /// </summary>
    public class ToolPathResolverService
    {
        #region Variabili di classe

        /// <summary>
        /// Cartella config interna usata per il download di ffmpeg
        /// </summary>
        private string _configFolder;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="configFolder">Cartella config interna dell'applicazione</param>
        public ToolPathResolverService(string configFolder)
        {
            this._configFolder = configFolder;
            if (string.IsNullOrEmpty(this._configFolder))
            {
                this._configFolder = AppSettingsService.Instance.ConfigFolder;
            }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Risolve il path di mkvmerge
        /// </summary>
        /// <param name="autoSave">True per salvare il risultato in AppSettings quando valido</param>
        /// <returns>Path risolto o stringa vuota</returns>
        public string ResolveMkvMergePath(bool autoSave)
        {
            string result = "";
            MkvMergeProvider provider = new MkvMergeProvider();
            if (provider.Resolve(autoSave))
            {
                result = provider.MkvMergePath;
            }

            return result;
        }

        /// <summary>
        /// Indica se un path mkvmerge punta a un eseguibile CLI valido
        /// </summary>
        /// <param name="path">Percorso da verificare</param>
        /// <returns>True se il path è utilizzabile come mkvmerge</returns>
        public bool IsMkvMergeExecutablePath(string path)
        {
            return MkvMergeProvider.IsExecutablePath(path);
        }

        /// <summary>
        /// Risolve il path di ffmpeg
        /// </summary>
        /// <param name="autoSave">True per salvare il risultato in AppSettings quando valido</param>
        /// <param name="allowDownload">True per tentare il download automatico</param>
        /// <returns>Path risolto o stringa vuota</returns>
        public string ResolveFfmpegPath(bool autoSave, bool allowDownload)
        {
            return this.ResolveFfmpegPath(autoSave, allowDownload, false);
        }

        /// <summary>
        /// Risolve il path di ffmpeg con eventuale requisito libsoxr
        /// </summary>
        /// <param name="autoSave">True per salvare il risultato in AppSettings quando valido</param>
        /// <param name="allowDownload">True per tentare il download automatico</param>
        /// <param name="requireLibSoxr">True quando serve una build con --enable-libsoxr</param>
        /// <returns>Path risolto o stringa vuota</returns>
        public string ResolveFfmpegPath(bool autoSave, bool allowDownload, bool requireLibSoxr)
        {
            string result = "";
            FfmpegProvider provider = new FfmpegProvider(this._configFolder);
            if (provider.Resolve(autoSave, allowDownload, requireLibSoxr))
            {
                result = provider.FfmpegPath;
            }

            return result;
        }

        /// <summary>
        /// Risolve il path di mediainfo
        /// </summary>
        /// <param name="autoSave">True per salvare il risultato in AppSettings quando valido</param>
        /// <returns>Path risolto o stringa vuota</returns>
        public string ResolveMediaInfoPath(bool autoSave)
        {
            string result = "";
            MediaInfoProvider provider = new MediaInfoProvider();
            if (provider.Resolve(autoSave))
            {
                result = provider.MediaInfoPath;
            }

            return result;
        }

        /// <summary>
        /// Indica se un path mediainfo punta a un eseguibile CLI valido
        /// </summary>
        /// <param name="path">Percorso da verificare</param>
        /// <returns>True se il path è un eseguibile CLI</returns>
        public bool IsMediaInfoCliPath(string path)
        {
            return MediaInfoProvider.IsCliExecutablePath(path);
        }

        /// <summary>
        /// Risolve mkvextract partendo da mkvmerge
        /// </summary>
        /// <param name="mkvMergePath">Percorso mkvmerge</param>
        /// <param name="autoSave">True per tentare auto-resolve di mkvmerge quando vuoto</param>
        /// <returns>Path mkvextract o stringa vuota</returns>
        public string ResolveMkvExtractPath(string mkvMergePath, bool autoSave)
        {
            string result = ResolveConfiguredOrPath("mkvextract", AppSettingsService.Instance.Settings.Tools.MkvExtractPath);
            string mkvPath = mkvMergePath;
            if (result.Length == 0)
            {
                result = this.ResolveSiblingToolPath("mkvextract", mkvPath, autoSave);
            }

            if (autoSave && result.Length > 0 && AppSettingsService.Instance.Settings.Tools.MkvExtractPath != result)
            {
                AppSettingsService.Instance.Settings.Tools.MkvExtractPath = result;
                AppSettingsService.Instance.Save();
            }

            return result;
        }

        /// <summary>
        /// Risolve mkvpropedit partendo da configurazione, PATH o cartella mkvmerge
        /// </summary>
        /// <param name="mkvMergePath">Percorso mkvmerge</param>
        /// <param name="autoSave">True per salvare il risultato in AppSettings quando valido</param>
        /// <returns>Path mkvpropedit o stringa vuota</returns>
        public string ResolveMkvPropEditPath(string mkvMergePath, bool autoSave)
        {
            string result = ResolveConfiguredOrPath("mkvpropedit", AppSettingsService.Instance.Settings.Tools.MkvPropEditPath);
            if (result.Length == 0)
            {
                result = this.ResolveSiblingToolPath("mkvpropedit", mkvMergePath, autoSave);
            }

            if (autoSave && result.Length > 0 && AppSettingsService.Instance.Settings.Tools.MkvPropEditPath != result)
            {
                AppSettingsService.Instance.Settings.Tools.MkvPropEditPath = result;
                AppSettingsService.Instance.Save();
            }

            return result;
        }

        /// <summary>
        /// Risolve ffprobe partendo da configurazione, PATH o cartella ffmpeg
        /// </summary>
        /// <param name="ffmpegPath">Percorso ffmpeg</param>
        /// <param name="autoSave">True per salvare il risultato in AppSettings quando valido</param>
        /// <returns>Path ffprobe o stringa vuota</returns>
        public string ResolveFfprobePath(string ffmpegPath, bool autoSave)
        {
            string result = ResolveConfiguredOrPath("ffprobe", AppSettingsService.Instance.Settings.Tools.FfprobePath);
            if (result.Length == 0)
            {
                result = this.ResolveSiblingExecutable("ffprobe", ffmpegPath);
            }

            if (autoSave && result.Length > 0 && AppSettingsService.Instance.Settings.Tools.FfprobePath != result)
            {
                AppSettingsService.Instance.Settings.Tools.FfprobePath = result;
                AppSettingsService.Instance.Save();
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Risolve un tool configurato o nel PATH
        /// </summary>
        private static string ResolveConfiguredOrPath(string toolName, string configuredPath)
        {
            string result;
            string executableName = toolName + GetExecutableExtension();

            if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath))
            {
                result = configuredPath;
            }
            else
            {
                result = FindInSystemPath(executableName);
            }

            return result;
        }

        /// <summary>
        /// Risolve un tool MKVToolNix nella stessa cartella di mkvmerge
        /// </summary>
        private string ResolveSiblingToolPath(string toolName, string mkvMergePath, bool autoSave)
        {
            string result = "";
            string mkvPath = mkvMergePath;
            if (mkvPath.Length == 0 && autoSave)
            {
                mkvPath = this.ResolveMkvMergePath(autoSave);
            }

            if (mkvPath.Length > 0)
            {
                result = this.ResolveSiblingExecutable(toolName, mkvPath);
            }

            return result;
        }

        /// <summary>
        /// Risolve un eseguibile nella stessa cartella di un tool noto
        /// </summary>
        private string ResolveSiblingExecutable(string toolName, string siblingPath)
        {
            string result = "";
            string folder;
            string candidate;

            if (!string.IsNullOrEmpty(siblingPath))
            {
                folder = Path.GetDirectoryName(siblingPath);
                if (!string.IsNullOrEmpty(folder))
                {
                    candidate = Path.Combine(folder, toolName + GetExecutableExtension());
                    if (File.Exists(candidate))
                    {
                        result = candidate;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Cerca un eseguibile nel PATH
        /// </summary>
        private static string FindInSystemPath(string executableName)
        {
            string result = "";
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            char separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            string[] paths;
            string candidate;

            if (pathEnv != null)
            {
                paths = pathEnv.Split(separator);
                for (int i = 0; i < paths.Length; i++)
                {
                    candidate = Path.Combine(paths[i], executableName);
                    if (File.Exists(candidate))
                    {
                        result = candidate;
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Restituisce l'estensione eseguibile per la piattaforma corrente
        /// </summary>
        private static string GetExecutableExtension()
        {
            string result = "";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                result = ".exe";
            }

            return result;
        }

        #endregion
    }
}
