using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System;
using System.IO;

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
            string result = "";
            if (!File.Exists(filePath))
            {
                return result;
            }

            try
            {
                result = this.RunProcess(REPORT_TIMEOUT_MS, filePath);
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.General, LogLevel.Warning, "Errore esecuzione mediainfo: " + ex.Message);
                result = "Errore esecuzione mediainfo: " + ex.Message;
            }

            return result;
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
}
