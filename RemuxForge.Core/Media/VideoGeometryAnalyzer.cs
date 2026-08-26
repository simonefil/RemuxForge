using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Analizza la geometria video effettiva e conserva i profili in cache
    /// </summary>
    public class VideoGeometryAnalyzer
    {
        #region Variabili statiche

        /// <summary>
        /// Espressione regolare compilata per estrarre la geometria video dall'output di ffmpeg
        /// </summary>
        private static readonly Regex s_videoGeometryRegex = new Regex(@"Video:.*?(\d{2,5})x(\d{2,5})(?:[^\r\n]*?\[SAR\s+(\d+):(\d+)\s+DAR\s+(\d+):(\d+)\])?", RegexOptions.Compiled);

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso dell'eseguibile ffmpeg
        /// </summary>
        private string _ffmpegPath;

        /// <summary>
        /// Configurazione usata per invocare ffmpeg
        /// </summary>
        private FfmpegConfig _ffmpegConfig;

        /// <summary>
        /// Sezione di log usata per segnalare gli errori di analisi
        /// </summary>
        private LogSection _logSection;

        /// <summary>
        /// Oggetto di sincronizzazione per l'accesso alla cache dei profili di geometria
        /// </summary>
        private object _lock;

        /// <summary>
        /// Cache dei profili di geometria indicizzata per percorso file
        /// </summary>
        private Dictionary<string, VideoGeometryProfile> _cache;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza l'analizzatore della geometria video
        /// </summary>
        /// <param name="ffmpegPath">Percorso dell'eseguibile ffmpeg</param>
        /// <param name="ffmpegConfig">Configurazione da usare per ffmpeg</param>
        /// <param name="logSection">Sezione di log per gli errori di analisi</param>
        public VideoGeometryAnalyzer(string ffmpegPath, FfmpegConfig ffmpegConfig, LogSection logSection)
        {
            this._ffmpegPath = ffmpegPath;
            this._ffmpegConfig = ffmpegConfig;
            this._logSection = logSection;
            this._lock = new object();
            this._cache = new Dictionary<string, VideoGeometryProfile>(StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Analizza la geometria video tramite ffmpeg e restituisce il profilo associato al file
        /// </summary>
        /// <param name="filePath">Percorso del file video da analizzare</param>
        /// <returns>Profilo della geometria video, oppure null se l'analisi non produce un risultato</returns>
        public VideoGeometryProfile Analyze(string filePath)
        {
            VideoGeometryProfile profile;
            ProcessResult processResult;
            string output;
            Match match;
            int width;
            int height;
            int sarNum = 1;
            int sarDen = 1;
            int darNum = 0;
            int darDen = 0;
            List<string> args = new List<string>();

            lock (this._lock)
            {
                this._cache.TryGetValue(filePath, out profile);
            }

            if (profile != null)
            {
                return profile;
            }

            try
            {
                args.Add("-nostdin");
                args.Add("-hide_banner");
                if (this._ffmpegConfig.HardwareAcceleration)
                {
                    args.Add("-hwaccel");
                    args.Add(this._ffmpegConfig.HardwareAccelerationMethod);
                }
                args.Add("-i");
                args.Add(filePath);

                processResult = ProcessRunner.Run(this._ffmpegPath, args.ToArray());
                output = processResult.Stdout + processResult.Stderr;
                match = s_videoGeometryRegex.Match(output);
                if (match.Success)
                {
                    width = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    height = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

                    if (match.Groups[3].Success && match.Groups[4].Success)
                    {
                        sarNum = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                        sarDen = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                        if (sarNum <= 0) { sarNum = 1; }
                        if (sarDen <= 0) { sarDen = 1; }
                    }

                    if (match.Groups[5].Success && match.Groups[6].Success)
                    {
                        darNum = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
                        darDen = int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);
                    }

                    profile = this.BuildProfile(filePath, width, height, sarNum, sarDen, darNum, darDen);
                    lock (this._lock)
                    {
                        if (!this._cache.ContainsKey(filePath))
                        {
                            this._cache.Add(filePath, profile);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(this._logSection, LogLevel.Warning, AppText.F("deep.temporal.geometry.analysisError", ex.Message));
            }

            return profile;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Costruisce il profilo della geometria video a partire dai valori rilevati
        /// </summary>
        /// <param name="filePath">Percorso del file video analizzato</param>
        /// <param name="width">Larghezza del video in pixel</param>
        /// <param name="height">Altezza del video in pixel</param>
        /// <param name="sarNum">Numeratore del rapporto tra pixel del video</param>
        /// <param name="sarDen">Denominatore del rapporto tra pixel del video</param>
        /// <param name="darNum">Numeratore del rapporto d'aspetto di visualizzazione</param>
        /// <param name="darDen">Denominatore del rapporto d'aspetto di visualizzazione</param>
        /// <returns>Profilo della geometria video calcolato</returns>
        private VideoGeometryProfile BuildProfile(string filePath, int width, int height, int sarNum, int sarDen, int darNum, int darDen)
        {
            VideoGeometryProfile profile = new VideoGeometryProfile();
            profile.FilePath = filePath;
            profile.Width = width;
            profile.Height = height;
            profile.SarNum = sarNum;
            profile.SarDen = sarDen;
            profile.DarNum = darNum;
            profile.DarDen = darDen;
            profile.DisplayWidth = (int)Math.Round(width * (double)sarNum / sarDen);
            profile.DisplayHeight = height;
            profile.DisplayAspect = profile.DisplayHeight > 0 ? profile.DisplayWidth / (double)profile.DisplayHeight : 0.0;
            return profile;
        }

        #endregion
    }
}
