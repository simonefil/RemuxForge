using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Servizio per esecuzione mediainfo CLI e generazione report
    /// </summary>
    public class MediaInfoService
    {
        #region Costanti

        /// <summary>
        /// Timeout rapido per letture puntuali MediaInfo
        /// </summary>
        private const int QUICK_QUERY_TIMEOUT_MS = 3000;

        /// <summary>
        /// Timeout massimo per report MediaInfo completi
        /// </summary>
        private const int REPORT_TIMEOUT_MS = 30000;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso eseguibile mediainfo
        /// </summary>
        private string _mediaInfoPath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="mediaInfoPath">Percorso eseguibile mediainfo</param>
        public MediaInfoService(string mediaInfoPath)
        {
            this._mediaInfoPath = mediaInfoPath;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Genera il report standard di mediainfo per un file
        /// </summary>
        /// <param name="filePath">Percorso del file da analizzare</param>
        /// <returns>Report testuale o stringa vuota in caso di errore</returns>
        public string GetReport(string filePath)
        {
            MediaInfoReportResult result = this.GetReportDetailed(filePath);
            // Contratto legacy: restituisce stdout anche su exit nonzero, senza interpretare il report.
            return result.Report;
        }

        /// <summary>Report detached con stato operativo esplicito; nessun log, stop o callback globale.</summary>
        public MediaInfoReportResult GetReportDetailed(string filePath, CancellationToken cancellationToken = default)
        {
            MediaInfoReportResult result = new MediaInfoReportResult();
            if (cancellationToken.IsCancellationRequested)
            {
                result.Cancelled = true;
                result.ErrorCode = "cancelled";
                result.ErrorMessage = AppText.T("remuxConfiguration.mediaInfoCancelled");
                return result;
            }
            if (!File.Exists(filePath))
            {
                result.ErrorCode = "fileNotFound";
                result.ErrorMessage = AppText.F("remuxConfiguration.mediaInfoFileUnavailable", filePath);
                return result;
            }
            try
            {
                using Process process = new Process();
                process.StartInfo.FileName = this._mediaInfoPath;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                ProcessRunner.ApplyUtf8Locale(process.StartInfo);
                process.StartInfo.ArgumentList.Add(filePath);
                process.Start();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                using CancellationTokenSource timeout = new CancellationTokenSource(REPORT_TIMEOUT_MS);
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    process.WaitForExitAsync(linked.Token).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    result.Cancelled = cancellationToken.IsCancellationRequested;
                    result.TimedOut = !result.Cancelled && timeout.IsCancellationRequested;
                    if (!process.HasExited) process.Kill(true);
                    process.WaitForExit();
                }
                result.ExitCode = process.ExitCode;
                result.Report = stdout.GetAwaiter().GetResult();
                result.Stderr = stderr.GetAwaiter().GetResult();
                result.Success = !result.Cancelled && !result.TimedOut && result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Report);
                if (!result.Success)
                {
                    result.ErrorCode = result.Cancelled ? "cancelled" : result.TimedOut ? "timeout" : result.ExitCode != 0 ? "exitCode" : "emptyReport";
                    result.ErrorMessage = result.Cancelled ? AppText.T("remuxConfiguration.mediaInfoCancelled") : result.TimedOut ? AppText.T("remuxConfiguration.mediaInfoTimeout") :
                        !string.IsNullOrEmpty(result.Stderr) ? result.Stderr : AppText.F("remuxConfiguration.mediaInfoInvalidReport", result.ExitCode);
                }
            }
            catch (Exception ex)
            {
                result.ErrorCode = "exception";
                result.ExceptionType = ex.GetType().FullName;
                result.ExceptionMessage = ex.Message;
                result.ExceptionDetails = ex.ToString();
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        public Task<MediaInfoReportResult> GetReportDetailedAsync(string filePath, CancellationToken cancellationToken = default)
        {
            // Il token è gestito nel risultato anche se annullato prima dell'avvio.
            return Task.Run(() => this.GetReportDetailed(filePath, cancellationToken));
        }

        /// <summary>
        /// Legge la modalità frame rate video tramite MediaInfo
        /// </summary>
        /// <param name="filePath">Percorso file</param>
        /// <returns>Valore MediaInfo FrameRate_Mode, stringa vuota se non disponibile</returns>
        public string GetVideoFrameRateMode(string filePath)
        {
            string result = "";
            if (!File.Exists(filePath))
            {
                return result;
            }

            try
            {
                result = this.RunProcess(QUICK_QUERY_TIMEOUT_MS, "--Output=Video;%FrameRate_Mode%", filePath).Trim();
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.General, LogLevel.Warning, "Errore lettura FrameRate_Mode MediaInfo: " + ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Legge campi timing video principali tramite MediaInfo
        /// </summary>
        /// <param name="filePath">Percorso file</param>
        /// <param name="frameRateMode">Modalità frame rate</param>
        /// <param name="frameRate">Frame rate nominale</param>
        /// <param name="originalFrameRate">Frame rate originale</param>
        /// <param name="frameCount">Numero frame</param>
        /// <param name="durationMs">Durata in millisecondi</param>
        /// <param name="minFrameRate">Frame rate minimo</param>
        /// <param name="maxFrameRate">Frame rate massimo</param>
        /// <returns>True se almeno un campo utile è stato letto</returns>
        public bool TryGetVideoTiming(string filePath, out string frameRateMode, out double frameRate, out double originalFrameRate, out long frameCount, out double durationMs, out double minFrameRate, out double maxFrameRate)
        {
            bool result = false;
            string output;
            string[] parts;

            frameRateMode = "";
            frameRate = 0.0;
            originalFrameRate = 0.0;
            frameCount = 0;
            durationMs = 0.0;
            minFrameRate = 0.0;
            maxFrameRate = 0.0;

            if (!File.Exists(filePath))
            {
                return result;
            }

            try
            {
                output = this.RunProcess(QUICK_QUERY_TIMEOUT_MS, "--Output=Video;%FrameRate_Mode%|%FrameRate%|%OriginalFrameRate%|%FrameCount%|%Duration%|%FrameRate_Minimum%|%FrameRate_Maximum%", filePath).Trim();
                parts = output.Split('|');
                if (parts.Length >= 7)
                {
                    frameRateMode = parts[0].Trim();
                    _ = TryParseDouble(parts[1], out frameRate);
                    _ = TryParseDouble(parts[2], out originalFrameRate);
                    long.TryParse(parts[3].Trim(), out frameCount);
                    _ = TryParseDouble(parts[4], out durationMs);
                    _ = TryParseDouble(parts[5], out minFrameRate);
                    _ = TryParseDouble(parts[6], out maxFrameRate);
                    result = !string.IsNullOrEmpty(frameRateMode) || frameRate > 0.0 || frameCount > 0 || durationMs > 0.0;
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.General, LogLevel.Warning, "Errore lettura timing MediaInfo: " + ex.Message);
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Esegue mediainfo con gli argomenti dati e restituisce stdout
        /// </summary>
        /// <param name="timeoutMs">Timeout in millisecondi, 0 = nessun timeout</param>
        /// <param name="arguments">Argomenti da passare a mediainfo</param>
        /// <returns>Output stdout del processo</returns>
        private string RunProcess(int timeoutMs, params string[] arguments)
        {
            ProcessResult result = ProcessRunner.Run(this._mediaInfoPath, arguments, timeoutMs);

            return result.Stdout;
        }

        /// <summary>
        /// Parsa double MediaInfo con separatore invariant
        /// </summary>
        private static bool TryParseDouble(string text, out double value)
        {
            value = 0.0;
            if (text == null)
            {
                return false;
            }

            return double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        #endregion
    }

    /// <summary>Report grezzo e failure strutturata; ExitCode null se il processo non è partito.</summary>
    public sealed class MediaInfoReportResult
    {
        public bool Success { get; internal set; }
        public string Report { get; internal set; } = "";
        public int? ExitCode { get; internal set; }
        public string Stderr { get; internal set; } = "";
        public string ErrorCode { get; internal set; } = "";
        public string ErrorMessage { get; internal set; } = "";
        public string ExceptionType { get; internal set; } = "";
        public string ExceptionMessage { get; internal set; } = "";
        public string ExceptionDetails { get; internal set; } = "";
        public bool Cancelled { get; internal set; }
        public bool TimedOut { get; internal set; }
    }
}
