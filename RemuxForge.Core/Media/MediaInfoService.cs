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
        /// Legge la modalità frame rate della prima traccia video tramite MediaInfo: è la traccia principale, la stessa che lo
        /// Split taglia, mentre copertine e fotogrammi singoli in altre tracce video non contano
        /// </summary>
        /// <param name="filePath">Percorso file</param>
        /// <returns>Valore MediaInfo FrameRate_Mode, stringa vuota se non disponibile</returns>
        public string GetVideoFrameRateMode(string filePath)
        {
            string result = "";

            try
            {
                this.TryReadFirstVideoField(filePath, "FrameRate_Mode", out result);
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.General, LogLevel.Warning, AppText.F("remux.media.frameRateModeReadFailed", ex.Message));
            }

            return result;
        }

        /// <summary>
        /// Legge la durata video tramite MediaInfo
        /// </summary>
        /// <param name="filePath">Percorso file</param>
        /// <param name="durationMs">Durata in millisecondi</param>
        /// <returns>True se la durata è stata letta</returns>
        public bool TryGetVideoDuration(string filePath, out double durationMs)
        {
            bool result = false;

            durationMs = 0.0;
            try
            {
                this.TryReadFirstVideoField(filePath, "Duration", out string output);
                result = TryParseDouble(output, out durationMs) && durationMs > 0.0;
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.General, LogLevel.Warning, AppText.F("remux.media.timingReadFailed", ex.Message));
            }

            return result;
        }

        /// <summary>
        /// Legge la scansione della prima traccia video
        /// </summary>
        /// <param name="filePath">Percorso file</param>
        /// <param name="scanType">Scansione MediaInfo, vuota se non dichiarata o non letta</param>
        /// <returns>True se MediaInfo ha risposto</returns>
        public bool TryGetVideoScanType(string filePath, out VideoScanType scanType)
        {
            bool result = this.TryReadFirstVideoField(filePath, "ScanType", out string value);

            scanType = new VideoScanType(result ? value : "");
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Legge un campo della prima traccia video: il separatore isola la prima traccia quando il file ne contiene più di una
        /// </summary>
        /// <param name="filePath">Percorso file</param>
        /// <param name="field">Nome del campo MediaInfo della sezione Video</param>
        /// <param name="value">Valore del campo letto dallo stdout anche con exit code diverso da zero, vuoto se il file non esiste</param>
        /// <returns>True se il file esiste e MediaInfo è uscito con 0</returns>
        private bool TryReadFirstVideoField(string filePath, string field, out string value)
        {
            ProcessResult result;

            value = "";
            if (!File.Exists(filePath))
            {
                return false;
            }

            result = ProcessRunner.Run(this._mediaInfoPath, new string[] { "--Output=Video;%" + field + "%|", filePath }, QUICK_QUERY_TIMEOUT_MS);
            value = result.Stdout.Trim().Split('|')[0].Trim();
            return result.ExitCode == 0;
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
        public string ExceptionDetails { get; internal set; } = "";
        public bool Cancelled { get; internal set; }
        public bool TimedOut { get; internal set; }
    }
}
