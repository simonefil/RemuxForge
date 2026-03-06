using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;

namespace MergeLanguageTracks
{
    /// <summary>
    /// Individua o scarica l'eseguibile ffmpeg
    /// </summary>
    public class FfmpegProvider
    {
        #region Costanti

        /// <summary>
        /// URL download Windows x64
        /// </summary>
        private const string WINDOWS_X64_URL = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        /// <summary>
        /// URL download Linux x64
        /// </summary>
        private const string LINUX_X64_URL = "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz";

        /// <summary>
        /// URL download Linux arm64
        /// </summary>
        private const string LINUX_ARM64_URL = "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-arm64-static.tar.xz";

        /// <summary>
        /// URL download macOS universal binary
        /// </summary>
        private const string MACOS_FFMPEG_URL = "https://evermeet.cx/ffmpeg/getrelease/ffmpeg/zip";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Cartella dei tool scaricati
        /// </summary>
        private string _toolsFolder;

        /// <summary>
        /// Percorso risolto di ffmpeg
        /// </summary>
        private string _ffmpegPath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="toolsFolder">Cartella di destinazione dei tool</param>
        public FfmpegProvider(string toolsFolder)
        {
            this._toolsFolder = toolsFolder;
            this._ffmpegPath = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Individua ffmpeg, scaricandolo se necessario
        /// </summary>
        /// <returns>True se ffmpeg e' stato trovato o scaricato</returns>
        public bool Resolve()
        {
            bool resolved = false;
            string exeExt = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : "";
            string ffmpegName = "ffmpeg" + exeExt;
            string toolsFfmpeg = Path.Combine(this._toolsFolder, ffmpegName);
            string pathFfmpeg = "";

            // Controlla prima la cartella tools
            if (File.Exists(toolsFfmpeg))
            {
                this._ffmpegPath = toolsFfmpeg;
                resolved = true;
            }
            else
            {
                // Controlla il PATH di sistema
                pathFfmpeg = FindInPath(ffmpegName);

                if (pathFfmpeg.Length > 0)
                {
                    this._ffmpegPath = pathFfmpeg;
                    resolved = true;
                }
                else
                {
                    // Scarica per la piattaforma corrente
                    resolved = this.DownloadForCurrentPlatform(toolsFfmpeg);
                }
            }

            return resolved;
        }

        #endregion

        #region Metodi privati

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

            ConsoleHelper.WriteDarkGray("  Piattaforma rilevata: " + osName + " " + archName);

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
                success = this.DownloadMacOS(ffmpegDest);
            }
            else
            {
                ConsoleHelper.WriteRed("  Piattaforma non supportata: " + osName + " " + archName);
                ConsoleHelper.WriteYellow("  Piattaforme supportate: Windows x64, Linux x64, Linux arm64, macOS x64, macOS arm64");
                ConsoleHelper.WriteYellow("  Installa ffmpeg manualmente e assicurati che sia nel PATH.");
            }

            return success;
        }

        /// <summary>
        /// Cerca ffmpeg nel PATH di sistema
        /// </summary>
        /// <param name="executableName">Nome dell'eseguibile da cercare</param>
        /// <returns>Percorso completo dell'eseguibile, stringa vuota se non trovato</returns>
        private static string FindInPath(string executableName)
        {
            string result = "";
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            char separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
            string[] paths = null;
            string candidate = "";

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
            string foundFfmpeg = "";

            try
            {
                ConsoleHelper.WriteYellow("\n  Download ffmpeg per Windows x64...");
                ConsoleHelper.WriteDarkGray("  URL: " + WINDOWS_X64_URL);

                webClient = new WebClient();
                webClient.DownloadFile(WINDOWS_X64_URL, zipPath);

                ConsoleHelper.WriteDarkGray("  Estrazione in corso...");

                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                }

                ZipFile.ExtractToDirectory(zipPath, extractPath);

                foundFfmpeg = FindFileRecursive(extractPath, "ffmpeg.exe");

                if (foundFfmpeg.Length > 0)
                {
                    File.Copy(foundFfmpeg, ffmpegDest, true);
                    this._ffmpegPath = ffmpegDest;
                    success = true;
                    ConsoleHelper.WriteGreen("  ffmpeg scaricato in: " + this._toolsFolder);
                }
                else
                {
                    ConsoleHelper.WriteRed("  Impossibile trovare ffmpeg.exe nell'archivio");
                }
            }
            catch (Exception ex)
            {
                // Download o estrazione fallita
                ConsoleHelper.WriteWarning("Impossibile scaricare ffmpeg: " + ex.Message);
                ConsoleHelper.WriteYellow("  Scaricalo manualmente da https://www.gyan.dev/ffmpeg/builds/");
            }
            finally
            {
                if (webClient != null) { webClient.Dispose(); webClient = null; }
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
            int tarExitCode = 0;
            string foundFfmpeg = "";

            try
            {
                ConsoleHelper.WriteYellow("\n  Download ffmpeg per Linux " + archName + "...");
                ConsoleHelper.WriteDarkGray("  URL: " + downloadUrl);

                webClient = new WebClient();
                webClient.DownloadFile(downloadUrl, tarPath);

                ConsoleHelper.WriteDarkGray("  Estrazione in corso...");

                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                }
                Directory.CreateDirectory(extractPath);

                tarExitCode = RunCommand("tar", "xf \"" + tarPath + "\" -C \"" + extractPath + "\"");

                if (tarExitCode != 0)
                {
                    ConsoleHelper.WriteRed("  Errore durante l'estrazione (tar exit code: " + tarExitCode + ")");
                    ConsoleHelper.WriteYellow("  Assicurati che tar e xz-utils siano installati");
                }
                else
                {
                    foundFfmpeg = FindFileRecursive(extractPath, "ffmpeg");

                    if (foundFfmpeg.Length > 0)
                    {
                        File.Copy(foundFfmpeg, ffmpegDest, true);
                        RunCommand("chmod", "+x \"" + ffmpegDest + "\"");
                        this._ffmpegPath = ffmpegDest;
                        success = true;
                        ConsoleHelper.WriteGreen("  ffmpeg scaricato in: " + this._toolsFolder);
                    }
                    else
                    {
                        ConsoleHelper.WriteRed("  Impossibile trovare ffmpeg nell'archivio");
                    }
                }
            }
            catch (Exception ex)
            {
                // Download o estrazione fallita
                ConsoleHelper.WriteWarning("Impossibile scaricare ffmpeg: " + ex.Message);
                ConsoleHelper.WriteYellow("  Scaricalo manualmente da https://johnvansickle.com/ffmpeg/");
                ConsoleHelper.WriteYellow("  Oppure installa con: sudo apt install ffmpeg");
            }
            finally
            {
                if (webClient != null) { webClient.Dispose(); webClient = null; }
                CleanupTempFiles(tarPath, extractPath);
            }

            return success;
        }

        /// <summary>
        /// Scarica ffmpeg su macOS
        /// </summary>
        /// <param name="ffmpegDest">Percorso di destinazione dell'eseguibile</param>
        /// <returns>True se il download e l'estrazione sono riusciti</returns>
        private bool DownloadMacOS(string ffmpegDest)
        {
            bool success = false;
            string ffmpegZipPath = Path.Combine(this._toolsFolder, "ffmpeg.zip");
            string extractPath = Path.Combine(this._toolsFolder, "ffmpeg_temp");
            WebClient webClient = null;
            string foundFfmpeg = "";

            try
            {
                ConsoleHelper.WriteYellow("\n  Download ffmpeg per macOS (universal binary)...");

                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                }
                Directory.CreateDirectory(extractPath);

                webClient = new WebClient();

                ConsoleHelper.WriteDarkGray("  Download ffmpeg da: " + MACOS_FFMPEG_URL);
                webClient.DownloadFile(MACOS_FFMPEG_URL, ffmpegZipPath);

                ConsoleHelper.WriteDarkGray("  Estrazione in corso...");
                ZipFile.ExtractToDirectory(ffmpegZipPath, extractPath);
                foundFfmpeg = FindFileRecursive(extractPath, "ffmpeg");

                if (foundFfmpeg.Length > 0)
                {
                    File.Copy(foundFfmpeg, ffmpegDest, true);
                    RunCommand("chmod", "+x \"" + ffmpegDest + "\"");
                    RunCommand("xattr", "-d com.apple.quarantine \"" + ffmpegDest + "\"");
                    this._ffmpegPath = ffmpegDest;
                    success = true;
                    ConsoleHelper.WriteGreen("  ffmpeg scaricato in: " + this._toolsFolder);
                }
                else
                {
                    ConsoleHelper.WriteRed("  Impossibile trovare ffmpeg nell'archivio");
                }
            }
            catch (Exception ex)
            {
                // Download o estrazione fallita
                ConsoleHelper.WriteWarning("Impossibile scaricare ffmpeg: " + ex.Message);
                ConsoleHelper.WriteYellow("  Scaricalo manualmente da https://evermeet.cx/ffmpeg/");
                ConsoleHelper.WriteYellow("  Oppure installa con: brew install ffmpeg");
            }
            finally
            {
                if (webClient != null) { webClient.Dispose(); webClient = null; }
                CleanupTempFiles(ffmpegZipPath, extractPath);
            }

            return success;
        }

        /// <summary>
        /// Esegue un comando shell e restituisce l'exit code
        /// </summary>
        /// <param name="command">Comando da eseguire</param>
        /// <param name="arguments">Argomenti del comando</param>
        /// <returns>Exit code del processo, -1 in caso di errore</returns>
        private static int RunCommand(string command, string arguments)
        {
            int exitCode = -1;
            Process proc = null;

            try
            {
                proc = new Process();
                proc.StartInfo.FileName = command;
                proc.StartInfo.Arguments = arguments;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.RedirectStandardError = true;
                proc.Start();
                proc.WaitForExit();
                exitCode = proc.ExitCode;
            }
            catch
            {
                // Comando non trovato o non eseguibile
            }
            finally
            {
                if (proc != null) { proc.Dispose(); proc = null; }
            }

            return exitCode;
        }

        /// <summary>
        /// Pulisce file e directory temporanei
        /// </summary>
        /// <param name="filePath">Percorso del file temporaneo da eliminare</param>
        /// <param name="directoryPath">Percorso della directory temporanea da eliminare</param>
        private static void CleanupTempFiles(string filePath, string directoryPath)
        {
            // Errori cleanup ignorati, file temporanei non critici
            if (filePath != null && File.Exists(filePath))
            {
                try { File.Delete(filePath); } catch { }
            }
            if (directoryPath != null && Directory.Exists(directoryPath))
            {
                try { Directory.Delete(directoryPath, true); } catch { }
            }
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
            string[] subdirs = null;

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
        public string FfmpegPath { get { return this._ffmpegPath; } }

        #endregion
    }
}
