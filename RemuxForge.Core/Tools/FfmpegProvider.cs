using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;

namespace RemuxForge.Core.Tools
{
    /// <summary>
    /// Individua o scarica l'eseguibile ffmpeg
    /// </summary>
    public class FfmpegProvider : ToolProviderBase
    {
        #region Costanti

        /// <summary>
        /// Versione FFmpeg stabile usata dove il provider pubblica release versionate permanenti
        /// </summary>
        private const string FFMPEG_PINNED_VERSION = "8.1.1";

        /// <summary>
        /// Release branch FFmpeg stabile usata per i download BtbN.
        /// BtbN mantiene solo poche autobuild, quindi su Linux si usa latest del branch stabile.
        /// </summary>
        private const string FFMPEG_STABLE_BRANCH = "8.1";

        /// <summary>
        /// URL download Windows x64
        /// </summary>
        private const string WINDOWS_X64_URL = "https://github.com/GyanD/codexffmpeg/releases/download/" + FFMPEG_PINNED_VERSION + "/ffmpeg-" + FFMPEG_PINNED_VERSION + "-full_build.zip";

        /// <summary>
        /// URL download Linux x64 dalla release branch stabile BtbN
        /// </summary>
        private const string LINUX_X64_URL = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n" + FFMPEG_STABLE_BRANCH + "-latest-linux64-gpl-" + FFMPEG_STABLE_BRANCH + ".tar.xz";

        /// <summary>
        /// URL download Linux arm64 dalla release branch stabile BtbN
        /// </summary>
        private const string LINUX_ARM64_URL = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n" + FFMPEG_STABLE_BRANCH + "-latest-linuxarm64-gpl-" + FFMPEG_STABLE_BRANCH + ".tar.xz";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Cartella dei tool scaricati
        /// </summary>
        private string _toolsFolder;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="toolsFolder">Cartella di destinazione dei tool</param>
        public FfmpegProvider(string toolsFolder)
        {
            this._toolsFolder = toolsFolder;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Individua ffmpeg nel sistema con eventuale requisito libsoxr per il binario gestito
        /// </summary>
        /// <param name="autoSave">Se true, salva il percorso trovato in AppSettings</param>
        /// <param name="allowDownload">Se true, tenta il download se non trovato localmente</param>
        /// <param name="requireLibSoxr">Se true, aggiorna il binario RemuxForge gestito se non espone libsoxr</param>
        /// <returns>True se ffmpeg e' stato trovato o scaricato</returns>
        public bool Resolve(bool autoSave, bool allowDownload, bool requireLibSoxr)
        {
            bool resolved = false;
            string ffmpegName = "ffmpeg" + GetExecutableExtension();
            string toolsFfmpeg = Path.Combine(this._toolsFolder, ffmpegName);
            string found;
            // Controlla percorso salvato in AppSettings
            if (AppSettingsService.Instance.Settings.Tools.FfmpegPath.Length > 0 && File.Exists(AppSettingsService.Instance.Settings.Tools.FfmpegPath))
            {
                this._resolvedPath = AppSettingsService.Instance.Settings.Tools.FfmpegPath;
                resolved = true;
            }

            // Controlla la cartella tools
            if (!resolved && File.Exists(toolsFfmpeg))
            {
                this._resolvedPath = toolsFfmpeg;
                resolved = true;
            }

            if (resolved && requireLibSoxr && allowDownload && this.IsManagedFfmpegPath(this._resolvedPath, toolsFfmpeg) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && !SupportsLibSoxr(this._resolvedPath))
            {
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Warning, "  ffmpeg gestito da RemuxForge senza libsoxr: download build aggiornata");
                resolved = false;
            }

            // Controlla posizioni note del sistema
            if (!resolved)
            {
                found = SearchInPaths(ffmpegName, this.GetWellKnownPaths());
                if (found.Length > 0)
                {
                    this._resolvedPath = found;
                    resolved = true;
                }
            }

            // Controlla il PATH di sistema
            if (!resolved)
            {
                found = FindInSystemPath(ffmpegName);
                if (found.Length > 0)
                {
                    this._resolvedPath = found;
                    resolved = true;
                }
            }

            // Scarica per la piattaforma corrente
            if (!resolved && allowDownload)
            {
                resolved = this.DownloadForCurrentPlatform(toolsFfmpeg);
            }

            // Salva percorso trovato in AppSettings per le prossime volte
            if (autoSave && resolved && this._resolvedPath != AppSettingsService.Instance.Settings.Tools.FfmpegPath)
            {
                AppSettingsService.Instance.Settings.Tools.FfmpegPath = this._resolvedPath;
                AppSettingsService.Instance.Save();
            }

            return resolved;
        }

        /// <summary>
        /// Legge la prima riga di versione ffmpeg
        /// </summary>
        /// <param name="ffmpegPath">Percorso ffmpeg</param>
        /// <returns>Prima riga di ffmpeg -version, vuota se non leggibile</returns>
        public static string ReadVersionLine(string ffmpegPath)
        {
            string output;
            string[] lines;

            if (!TryReadVersionOutput(ffmpegPath, out output))
            {
                return "";
            }

            lines = output.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length > 0)
                {
                    return line;
                }
            }

            return "";
        }

        /// <summary>
        /// Indica se la build ffmpeg espone libsoxr
        /// </summary>
        /// <param name="ffmpegPath">Percorso ffmpeg</param>
        /// <returns>True se ffmpeg -version contiene --enable-libsoxr</returns>
        public static bool SupportsLibSoxr(string ffmpegPath)
        {
            string output;
            if (!TryReadVersionOutput(ffmpegPath, out output))
            {
                return false;
            }

            return output.IndexOf("--enable-libsoxr", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Restituisce le posizioni di installazione note per ffmpeg per ogni OS
        /// </summary>
        /// <returns>Array di percorsi di ricerca</returns>
        private string[] GetWellKnownPaths()
        {
            string[] paths;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                paths = new string[] { "/usr/bin", "/usr/local/bin", "/snap/bin" };
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                paths = new string[] { "/usr/local/bin", "/opt/homebrew/bin" };
            }
            else
            {
                // Windows: ffmpeg non ha posizione standard, si affida a tools/PATH/download
                paths = new string[0];
            }

            return paths;
        }

        /// <summary>
        /// Determina la piattaforma e avvia il download appropriato
        /// </summary>
        /// <param name="ffmpegDest">Percorso di destinazione dell'eseguibile</param>
        /// <returns>True se il download e' riuscito</returns>
        private bool DownloadForCurrentPlatform(string ffmpegDest)
        {
            bool success = false;
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            bool isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            bool isArm64 = RuntimeInformation.OSArchitecture == Architecture.Arm64;
            bool isX64 = RuntimeInformation.OSArchitecture == Architecture.X64;
            string archName = isArm64 ? "arm64" : (isX64 ? "x64" : RuntimeInformation.OSArchitecture.ToString());
            string osName = isWindows ? "Windows" : (isLinux ? "Linux" : (isMacOS ? "macOS" : "Unknown"));

            ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Debug, "  Piattaforma rilevata: " + osName + " " + archName);

            if (!Directory.Exists(this._toolsFolder))
            {
                Directory.CreateDirectory(this._toolsFolder);
            }

            if (isWindows && isX64)
            {
                success = this.DownloadWindows(ffmpegDest);
            }
            else if (isLinux && isX64)
            {
                success = this.DownloadLinux(ffmpegDest, LINUX_X64_URL, "amd64");
            }
            else if (isLinux && isArm64)
            {
                success = this.DownloadLinux(ffmpegDest, LINUX_ARM64_URL, "arm64");
            }
            else if (isMacOS)
            {
                success = this.DownloadMacOS();
            }
            else
            {
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Error, "  Piattaforma non supportata: " + osName + " " + archName);
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Info, "  Piattaforme supportate: Windows x64, Linux x64, Linux arm64, macOS x64, macOS arm64");
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Info, "  Installa ffmpeg manualmente e assicurati che sia nel PATH.");
            }

            return success;
        }

        /// <summary>
        /// Scarica ed estrae ffmpeg su Windows
        /// </summary>
        /// <param name="ffmpegDest">Percorso di destinazione dell'eseguibile</param>
        /// <returns>True se il download e l'estrazione sono riusciti</returns>
        private bool DownloadWindows(string ffmpegDest)
        {
            bool success = false;
            string zipPath = Path.Combine(this._toolsFolder, "ffmpeg.zip");
            string extractPath = Path.Combine(this._toolsFolder, "ffmpeg_temp");
            WebClient webClient = null;
            string foundFfmpeg;
            string foundFfprobe;
            string ffprobeDest;
            try
            {
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Info, "\n  Download ffmpeg per Windows x64...");
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Debug, "  URL: " + WINDOWS_X64_URL);

                webClient = new WebClient();
                webClient.DownloadFile(WINDOWS_X64_URL, zipPath);

                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Debug, "  Estrazione in corso...");

                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                }

                ZipFile.ExtractToDirectory(zipPath, extractPath);

                foundFfmpeg = FindFileRecursive(extractPath, "ffmpeg.exe");
                foundFfprobe = FindFileRecursive(extractPath, "ffprobe.exe");

                if (foundFfmpeg.Length > 0)
                {
                    File.Copy(foundFfmpeg, ffmpegDest, true);
                    if (foundFfprobe.Length > 0)
                    {
                        ffprobeDest = Path.Combine(this._toolsFolder, "ffprobe.exe");
                        File.Copy(foundFfprobe, ffprobeDest, true);
                    }

                    this._resolvedPath = ffmpegDest;
                    success = true;
                    ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Success, "  ffmpeg scaricato in: " + this._toolsFolder);
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Error, "  Impossibile trovare ffmpeg.exe nell'archivio");
                }
            }
            catch (Exception ex)
            {
                // Download o estrazione fallita
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Warning, "Impossibile scaricare ffmpeg: " + ex.Message);
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Info, "  Scaricalo manualmente da https://www.gyan.dev/ffmpeg/builds/");
            }
            finally
            {
                if (webClient != null) { webClient.Dispose(); }
                CleanupTempFiles(zipPath, extractPath);
            }

            return success;
        }

        /// <summary>
        /// Scarica ed estrae ffmpeg su Linux
        /// </summary>
        /// <param name="ffmpegDest">Percorso di destinazione dell'eseguibile</param>
        /// <param name="downloadUrl">URL di download dell'archivio</param>
        /// <param name="archName">Nome dell'architettura</param>
        /// <returns>True se il download e l'estrazione sono riusciti</returns>
        private bool DownloadLinux(string ffmpegDest, string downloadUrl, string archName)
        {
            bool success = false;
            string tarPath = Path.Combine(this._toolsFolder, "ffmpeg.tar.xz");
            string extractPath = Path.Combine(this._toolsFolder, "ffmpeg_temp");
            WebClient webClient = null;
            int tarExitCode;
            string foundFfmpeg;
            string foundFfprobe;
            string ffprobeDest;
            try
            {
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Info, "\n  Download ffmpeg per Linux " + archName + "...");
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Debug, "  URL: " + downloadUrl);

                webClient = new WebClient();
                webClient.DownloadFile(downloadUrl, tarPath);

                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Debug, "  Estrazione in corso...");

                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                }
                Directory.CreateDirectory(extractPath);

                tarExitCode = RunCommand("tar", new string[] { "xf", tarPath, "-C", extractPath });

                if (tarExitCode != 0)
                {
                    ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Error, "  Errore durante l'estrazione (tar exit code: " + tarExitCode + ")");
                    ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Info, "  Assicurati che tar e xz-utils siano installati");
                }
                else
                {
                    foundFfmpeg = FindFileRecursive(extractPath, "ffmpeg");
                    foundFfprobe = FindFileRecursive(extractPath, "ffprobe");

                    if (foundFfmpeg.Length > 0)
                    {
                        File.Copy(foundFfmpeg, ffmpegDest, true);
                        RunCommand("chmod", new string[] { "+x", ffmpegDest });
                        if (foundFfprobe.Length > 0)
                        {
                            ffprobeDest = Path.Combine(this._toolsFolder, "ffprobe");
                            File.Copy(foundFfprobe, ffprobeDest, true);
                            RunCommand("chmod", new string[] { "+x", ffprobeDest });
                        }

                        this._resolvedPath = ffmpegDest;
                        success = true;
                        ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Success, "  ffmpeg scaricato in: " + this._toolsFolder);
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Error, "  Impossibile trovare ffmpeg nell'archivio");
                    }
                }
            }
            catch (Exception ex)
            {
                // Download o estrazione fallita
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Warning, "Impossibile scaricare ffmpeg: " + ex.Message);
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Info, "  Scaricalo manualmente da https://github.com/BtbN/FFmpeg-Builds/releases");
                ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Info, "  Oppure installa con: sudo apt install ffmpeg");
            }
            finally
            {
                if (webClient != null) { webClient.Dispose(); }
                CleanupTempFiles(tarPath, extractPath);
            }

            return success;
        }

        /// <summary>
        /// Segnala che il download automatico ffmpeg non e' supportato su macOS
        /// </summary>
        /// <returns>Sempre false: usare Homebrew o path manuale</returns>
        private bool DownloadMacOS()
        {
            ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Warning, "  Download automatico ffmpeg disabilitato su macOS");
            ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Info, "  Installa ffmpeg con Homebrew: brew install ffmpeg");
            ConsoleHelper.Write(LogSection.Ffmpeg, LogLevel.Info, "  Oppure specifica manualmente il percorso di ffmpeg nelle impostazioni.");
            return false;
        }

        /// <summary>
        /// Esegue un comando shell e restituisce l'exit code
        /// </summary>
        /// <param name="command">Comando da eseguire</param>
        /// <param name="args">Argomenti del comando</param>
        /// <returns>Exit code del processo, -1 in caso di errore</returns>
        private static int RunCommand(string command, string[] args)
        {
            ProcessResult result = ProcessRunner.Run(command, args);

            return result.ExitCode;
        }

        /// <summary>
        /// Legge output completo di ffmpeg -version
        /// </summary>
        private static bool TryReadVersionOutput(string ffmpegPath, out string output)
        {
            ProcessResult result;

            output = "";
            if (ffmpegPath == null || ffmpegPath.Length == 0 || !File.Exists(ffmpegPath))
            {
                return false;
            }

            result = ProcessRunner.Run(ffmpegPath, new string[] { "-version" });
            output = result.Stdout + "\n" + result.Stderr;

            return result.ExitCode == 0 && output.Length > 0;
        }

        /// <summary>
        /// Indica se il path ffmpeg corrisponde al binario gestito nella cartella RemuxForge
        /// </summary>
        private bool IsManagedFfmpegPath(string resolvedPath, string toolsFfmpeg)
        {
            string resolvedFullPath;
            string toolsFullPath;

            if (resolvedPath == null || toolsFfmpeg == null || resolvedPath.Length == 0 || toolsFfmpeg.Length == 0)
            {
                return false;
            }

            try
            {
                resolvedFullPath = Path.GetFullPath(resolvedPath);
                toolsFullPath = Path.GetFullPath(toolsFfmpeg);
                return string.Equals(resolvedFullPath, toolsFullPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Pulisce file e directory temporanei
        /// </summary>
        /// <param name="filePath">Percorso del file temporaneo da eliminare</param>
        /// <param name="directoryPath">Percorso della directory temporanea da eliminare</param>
        private static void CleanupTempFiles(string filePath, string directoryPath)
        {
            // Errori cleanup ignorati, file temporanei non critici
            FileHelper.DeleteTempFile(filePath);
            FileHelper.DeleteTempDirectory(directoryPath);
        }

        /// <summary>
        /// Cerca ricorsivamente un file per nome in un albero di directory
        /// </summary>
        /// <param name="directory">Directory di partenza della ricerca</param>
        /// <param name="fileName">Nome del file da cercare</param>
        /// <returns>Percorso completo del file, stringa vuota se non trovato</returns>
        private static string FindFileRecursive(string directory, string fileName)
        {
            string result = "";
            string[] files = Directory.GetFiles(directory);
            string[] subdirs;

            // Controlla file nella directory corrente
            for (int i = 0; i < files.Length; i++)
            {
                if (Path.GetFileName(files[i]).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    result = files[i];
                    break;
                }
            }

            // Se non trovato, cerca nelle sottodirectory
            if (result.Length == 0)
            {
                subdirs = Directory.GetDirectories(directory);
                for (int i = 0; i < subdirs.Length; i++)
                {
                    result = FindFileRecursive(subdirs[i], fileName);
                    if (result.Length > 0)
                    {
                        break;
                    }
                }
            }

            return result;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Percorso risolto dell'eseguibile ffmpeg
        /// </summary>
        public string FfmpegPath { get { return this._resolvedPath; } }

        #endregion
    }
}
