using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Analizza e mantiene in cache la geometria video effettiva
    /// </summary>
    public class VideoGeometryAnalyzer
    {
        #region Variabili statiche

        /// <summary>
        /// Regex per parsing geometria video da stderr ffmpeg
        /// </summary>
        private static readonly Regex s_videoGeometryRegex = new Regex(@"Video:.*?(\d{2,5})x(\d{2,5})(?:[^\r\n]*?\[SAR\s+(\d+):(\d+)\s+DAR\s+(\d+):(\d+)\])?", RegexOptions.Compiled);

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso ffmpeg
        /// </summary>
        private string _ffmpegPath;

        /// <summary>
        /// Configurazione ffmpeg
        /// </summary>
        private FfmpegConfig _ffmpegConfig;

        /// <summary>
        /// Sezione log
        /// </summary>
        private LogSection _logSection;

        /// <summary>
        /// Lock per cache profili geometry
        /// </summary>
        private object _lock;

        /// <summary>
        /// Cache profili geometry per file
        /// </summary>
        private Dictionary<string, VideoGeometryProfile> _cache;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
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
        /// Analizza geometria video tramite ffmpeg
        /// </summary>
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
                ConsoleHelper.Write(this._logSection, LogLevel.Warning, "  Errore AnalyzeVideoGeometry: " + ex.Message);
            }

            return profile;
        }

        /// <summary>
        /// Aggiorna il profilo geometry cached con il crop rilevato in analisi frame
        /// </summary>
        public void UpdateCropProfile(string filePath, int cropLeft, int cropRight, int cropTop, int cropBottom)
        {
            VideoGeometryProfile profile;
            lock (this._lock)
            {
                if (this._cache.TryGetValue(filePath, out profile))
                {
                    profile.CropLeft = cropLeft;
                    profile.CropRight = cropRight;
                    profile.CropTop = cropTop;
                    profile.CropBottom = cropBottom;
                    profile.HasBlackBorderCrop = true;
                }
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Costruisce profilo geometria
        /// </summary>
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
