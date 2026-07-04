using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace RemuxForge.Core.Analysis.Speed
{
    /// <summary>
    /// Rilevamento e correzione differenze di velocità tra sorgente e lingua
    /// </summary>
    public class SpeedCorrectionService : VideoSyncServiceBase
    {
        #region Variabili di classe

        /// <summary>
        /// Riferimento alla configurazione SpeedCorrection (binding diretto, modifiche immediate)
        /// </summary>
        private readonly SpeedCorrectionConfig _scConfig;

        /// <summary>
        /// Delay iniziale rilevato in ms
        /// </summary>
        private int _initialDelayMs;

        /// <summary>
        /// Rapporto stretch come stringa per mkvmerge
        /// </summary>
        private string _stretchFactor;

        /// <summary>
        /// Delay calcolato per mkvmerge --sync
        /// </summary>
        private int _syncDelayMs;

        /// <summary>
        /// FPS traccia video sorgente
        /// </summary>
        private double _sourceFps;

        /// <summary>
        /// FPS traccia video lingua
        /// </summary>
        private double _langFps;

        /// <summary>
        /// Rapporto inverso di stretch (1/stretchRatio)
        /// </summary>
        private double _inverseRatio;

        /// <summary>
        /// Tolleranza temporale frame sorgente stimata dai PTS
        /// </summary>
        private double _sourceFrameToleranceMs;

        /// <summary>
        /// Tempo di esecuzione totale in ms
        /// </summary>
        private long _executionTimeMs;

        /// <summary>
        /// Offset di verifica per ciascun punto
        /// </summary>
        private readonly int[] _verifyOffsets;

        /// <summary>
        /// SSIM di verifica per ciascun punto
        /// </summary>
        private readonly double[] _verifySsimValues;

        /// <summary>
        /// Validità di ciascun punto di verifica
        /// </summary>
        private readonly bool[] _verifyPointValid;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public SpeedCorrectionService(string ffmpegPath) : base(ffmpegPath, LogSection.Speed)
        {
            // Carica configurazione speed correction
            this._scConfig = AppSettingsService.Instance.Settings.Advanced.SpeedCorrection;

            this._initialDelayMs = 0;
            this._stretchFactor = "";
            this._syncDelayMs = 0;
            this._sourceFps = 0.0;
            this._langFps = 0.0;
            this._inverseRatio = 0.0;
            this._sourceFrameToleranceMs = 1000.0 / 24000.0 * 1001.0;
            this._executionTimeMs = 0;
            this._verifyOffsets = new int[this._vsConfig.NumCheckPoints];
            this._verifySsimValues = new double[this._vsConfig.NumCheckPoints];
            this._verifyPointValid = new bool[this._vsConfig.NumCheckPoints];
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Rileva mismatch di velocità usando timing validato MediaInfo/default_duration
        /// </summary>
        public static bool DetectSpeedMismatch(MkvFileInfo sourceInfo, MkvFileInfo langInfo, VideoTimingInfo sourceTiming, VideoTimingInfo langTiming, out double sourceFps, out double langFps, out bool vfrSuspect)
        {
            bool detected = false;
            sourceFps = 0.0;
            langFps = 0.0;
            vfrSuspect = false;
            long sourceDefaultDuration;
            long langDefaultDuration;
            double speedRatio;
            double ratioDiff;
            double durationRatio;
            double durationDiff;
            SpeedCorrectionConfig cfg = AppSettingsService.Instance.Settings.Advanced.SpeedCorrection;
            double minSpeedRatioDiff = cfg.MinSpeedRatioDiff;
            double maxDurationDiffTelecine = cfg.MaxDurationDiffTelecine;

            if (sourceTiming != null && langTiming != null)
            {
                vfrSuspect = !sourceTiming.CanAutoSpeedCorrect || !langTiming.CanAutoSpeedCorrect;
                if (vfrSuspect)
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  Auto speed correction saltata: source=" + sourceTiming.Reason + ", lang=" + langTiming.Reason);
                    return false;
                }
            }

            // Cerca default_duration per tracce video, già validato quando VideoTimingInfo è disponibile
            sourceDefaultDuration = Utils.GetVideoDefaultDuration(sourceInfo.Tracks);
            langDefaultDuration = Utils.GetVideoDefaultDuration(langInfo.Tracks);
            vfrSuspect = sourceDefaultDuration <= 0 || langDefaultDuration <= 0;

            // Confronta solo se entrambi hanno default_duration
            if (sourceDefaultDuration > 0 && langDefaultDuration > 0)
            {
                speedRatio = (double)sourceDefaultDuration / langDefaultDuration;
                ratioDiff = Math.Abs(speedRatio - 1.0);

                if (ratioDiff >= minSpeedRatioDiff)
                {
                    // Imposta fps solo se differenza significativa
                    sourceFps = 1000000000.0 / sourceDefaultDuration;
                    langFps = 1000000000.0 / langDefaultDuration;

                    // Verifica durata container per escludere soft telecine
                    // Se le durate sono quasi uguali nonostante FPS diversi, non è un vero speed mismatch
                    if (sourceInfo.ContainerDurationNs > 0 && langInfo.ContainerDurationNs > 0)
                    {
                        durationRatio = (double)sourceInfo.ContainerDurationNs / langInfo.ContainerDurationNs;
                        durationDiff = Math.Abs(durationRatio - 1.0);

                        if (durationDiff < maxDurationDiffTelecine)
                        {
                            // Durate quasi identiche: probabile soft telecine o metadata errata
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  FPS diversi (" + sourceFps.ToString("F3", CultureInfo.InvariantCulture) + " vs " + langFps.ToString("F3", CultureInfo.InvariantCulture) + ") ma durata container identica (diff " + (durationDiff * 100).ToString("F2", CultureInfo.InvariantCulture) + "%) - probabile soft telecine, speed correction saltata");
                        }
                        else
                        {
                            // Durate effettivamente diverse: vero speed mismatch
                            detected = true;
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Speed mismatch rilevato: " + sourceFps.ToString("F3", CultureInfo.InvariantCulture) + "fps vs " + langFps.ToString("F3", CultureInfo.InvariantCulture) + "fps (durata diff " + (durationDiff * 100).ToString("F2", CultureInfo.InvariantCulture) + "%)");
                        }
                    }
                    else
                    {
                        // Durata container non disponibile, usa solo il confronto FPS
                        detected = true;
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Speed mismatch rilevato: " + sourceFps.ToString("F3", CultureInfo.InvariantCulture) + "fps vs " + langFps.ToString("F3", CultureInfo.InvariantCulture) + "fps");
                    }
                }
            }

            return detected;
        }

        /// <summary>
        /// Verifica se MediaInfo classifica uno dei file come VFR
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="languageFile">File lingua</param>
        /// <param name="reason">Motivo blocco Auto</param>
        /// <returns>True se Auto deve essere bloccata</returns>
        public static bool ShouldBlockAutoForVfr(string sourceFile, string languageFile, out string reason)
        {
            bool result = false;
            string sourceMode;
            string langMode;
            reason = "";

            if (!TryGetFrameRateModes(sourceFile, languageFile, out sourceMode, out langMode, out reason))
            {
                return result;
            }

            if (sourceMode.IndexOf("Variable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                langMode.IndexOf("Variable", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sourceMode.IndexOf("VFR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                langMode.IndexOf("VFR", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "VFR rilevato da MediaInfo: source=" + sourceMode + ", lang=" + langMode + ". Usare stretch manuale.";
                return result;
            }

            if (sourceMode.IndexOf("Constant", StringComparison.OrdinalIgnoreCase) < 0 ||
                langMode.IndexOf("Constant", StringComparison.OrdinalIgnoreCase) < 0)
            {
                reason = "FrameRate_Mode non costante: source=" + sourceMode + ", lang=" + langMode + ". Auto speed correction saltata.";
                return result;
            }

            reason = "MediaInfo conferma CFR: source=" + sourceMode + ", lang=" + langMode;
            return result;
        }

        /// <summary>
        /// Legge FrameRate_Mode source/lang tramite MediaInfo
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="languageFile">File lingua</param>
        /// <param name="sourceMode">FrameRate_Mode sorgente</param>
        /// <param name="langMode">FrameRate_Mode lingua</param>
        /// <param name="reason">Motivo se non leggibile</param>
        /// <returns>True se i mode sono stati letti</returns>
        public static bool TryGetFrameRateModes(string sourceFile, string languageFile, out string sourceMode, out string langMode, out string reason)
        {
            bool result = false;
            string mediaInfoPath = "";

            sourceMode = "";
            langMode = "";
            reason = "";

            if (string.IsNullOrEmpty(mediaInfoPath) || !System.IO.File.Exists(mediaInfoPath))
            {
                mediaInfoPath = new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder).ResolveMediaInfoPath(false);
            }

            if (string.IsNullOrEmpty(mediaInfoPath) || !System.IO.File.Exists(mediaInfoPath))
            {
                if (TryGetFrameRateModeFromFfprobe(sourceFile, out sourceMode) &&
                    TryGetFrameRateModeFromFfprobe(languageFile, out langMode))
                {
                    result = true;
                    return result;
                }

                reason = "MediaInfo non disponibile: Auto speed correction saltata per policy conservativa";
                return result;
            }

            MediaInfoService service = new MediaInfoService(mediaInfoPath);
            sourceMode = service.GetVideoFrameRateMode(sourceFile);
            langMode = service.GetVideoFrameRateMode(languageFile);

            if (string.IsNullOrEmpty(sourceMode) || string.IsNullOrEmpty(langMode))
            {
                if (TryGetFrameRateModeFromFfprobe(sourceFile, out sourceMode) &&
                    TryGetFrameRateModeFromFfprobe(languageFile, out langMode))
                {
                    result = true;
                    reason = "FrameRate_Mode ricavato da ffprobe frame PTS";
                    return result;
                }

                reason = "FrameRate_Mode MediaInfo non disponibile: Auto speed correction saltata";
                return result;
            }

            result = true;
            return result;
        }

        /// <summary>
        /// Fallback VFR/CFR tramite delta PTS dei frame video letti da ffprobe
        /// </summary>
        /// <param name="filePath">File video</param>
        /// <param name="mode">Mode rilevato</param>
        /// <returns>True se rilevamento completato</returns>
        private static bool TryGetFrameRateModeFromFfprobe(string filePath, out string mode)
        {
            bool result = false;
            ProcessResult processResult;
            List<double> pts = new List<double>();
            List<double> deltas = new List<double>();
            double value;
            double delta;
            double minDelta = 0.0;
            double maxDelta = 0.0;
            string line;
            string[] lines;

            mode = "";

            try
            {
                processResult = ProcessRunner.Run("ffprobe", new string[]
                {
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-read_intervals", "%+#240",
                    "-show_entries", "frame=best_effort_timestamp_time",
                    "-of", "csv=p=0",
                    filePath
                }, 5000);

                lines = processResult.Stdout.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (int i = 0; i < lines.Length && pts.Count < 240; i++)
                {
                    line = lines[i];
                    if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        pts.Add(value);
                    }
                }

                for (int i = 1; i < pts.Count; i++)
                {
                    delta = pts[i] - pts[i - 1];
                    if (delta > 0.001)
                    {
                        deltas.Add(delta);
                        if (delta < minDelta) { minDelta = delta; }
                        if (delta > maxDelta) { maxDelta = delta; }
                    }
                }

                if (deltas.Count >= 20)
                {
                    if (maxDelta - minDelta > 0.010)
                    {
                        mode = "Variable (ffprobe frame PTS)";
                    }
                    else
                    {
                        mode = "Constant (ffprobe frame PTS)";
                    }
                    result = true;
                }
            }
            catch
            {
                result = false;
            }

            return result;
        }

        /// <summary>
        /// Cerca delay iniziale e verifica correzione a 9 punti
        /// </summary>
        public bool FindDelayAndVerify(string sourceFile, string languageFile, long sourceDefaultDurationNs, long langDefaultDurationNs, int sourceDurationMs)
        {
            return this.FindDelayAndVerify(sourceFile, languageFile, sourceDefaultDurationNs, langDefaultDurationNs, sourceDurationMs, "");
        }

        /// <summary>
        /// Cerca delay iniziale e verifica correzione con stretch factor manuale
        /// </summary>
        public bool FindDelayAndVerifyManual(string sourceFile, string languageFile, string manualStretchFactor, int sourceDurationMs)
        {
            return this.FindDelayAndVerify(sourceFile, languageFile, 0, 0, sourceDurationMs, manualStretchFactor);
        }

        /// <summary>
        /// Cerca delay iniziale e verifica correzione a 9 punti
        /// </summary>
        private bool FindDelayAndVerify(string sourceFile, string languageFile, long sourceDefaultDurationNs, long langDefaultDurationNs, int sourceDurationMs, string manualStretchFactor)
        {
            bool success = false;
            Stopwatch stopwatch = new Stopwatch();
            double stretchRatio;
            int initialDelay;
            bool verified = false;
            string normalizedManualFactor;
            stopwatch.Start();
            this.PrepareGeometryDrivenCrop(sourceFile, languageFile);

            // Resetta risultati verifica
            for (int i = 0; i < this._vsConfig.NumCheckPoints; i++)
            {
                this._verifyOffsets[i] = int.MinValue;
                this._verifySsimValues[i] = 0.0;
                this._verifyPointValid[i] = false;
            }

            if (!string.IsNullOrEmpty(manualStretchFactor != null ? manualStretchFactor.Trim() : null))
            {
                if (!TryParseStretchFactor(manualStretchFactor, out stretchRatio, out normalizedManualFactor))
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, "  Stretch factor manuale non valido: " + manualStretchFactor);
                    stopwatch.Stop();
                    this._executionTimeMs = stopwatch.ElapsedMilliseconds;
                    return success;
                }

                this._sourceFps = 0.0;
                this._langFps = 0.0;
                this._stretchFactor = normalizedManualFactor;
            }
            else
            {
                // Calcola rapporto stretch e fps da default_duration
                stretchRatio = (double)sourceDefaultDurationNs / langDefaultDurationNs;
                this._sourceFps = 1000000000.0 / sourceDefaultDurationNs;
                this._langFps = 1000000000.0 / langDefaultDurationNs;
                this._stretchFactor = sourceDefaultDurationNs + "/" + langDefaultDurationNs;
            }

            this._inverseRatio = 1.0 / stretchRatio;

            if (this._sourceFps > 0.0 && this._langFps > 0.0)
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Rapporto stretch: " + stretchRatio.ToString("F6", CultureInfo.InvariantCulture) + " (" + this._sourceFps.ToString("F3", CultureInfo.InvariantCulture) + "fps -> " + this._langFps.ToString("F3", CultureInfo.InvariantCulture) + "fps)");
            }
            else
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Rapporto stretch manuale: " + stretchRatio.ToString("F6", CultureInfo.InvariantCulture) + " (" + this._stretchFactor + ")");
            }

            // Cerca delay iniziale tramite scene-cut voting
            initialDelay = this.FindInitialDelay(sourceFile, languageFile);

            // Libera memoria frame FindInitialDelay prima della verifica a 9 punti
            GC.Collect();

            if (initialDelay != int.MinValue)
            {
                this._initialDelayMs = initialDelay;

                // Calcola delay per mkvmerge
                this._syncDelayMs = (int)Math.Round(-this._initialDelayMs * stretchRatio);

                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Delay iniziale: " + this._initialDelayMs + "ms, sync delay: " + this._syncDelayMs + "ms, stretch: " + this._stretchFactor);

                // Verifica correzione a 9 punti con scene-cut matching
                verified = this.VerifyCorrection(sourceFile, languageFile, sourceDurationMs, this._initialDelayMs);

                if (!verified)
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Verifica a 9 punti fallita");
                }
            }
            else
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Impossibile determinare delay iniziale");
            }

            stopwatch.Stop();
            this._executionTimeMs = stopwatch.ElapsedMilliseconds;

            if (initialDelay != int.MinValue && verified)
            {
                success = true;
            }

            return success;
        }

        /// <summary>
        /// Parsa e normalizza uno stretch factor
        /// </summary>
        public static bool TryParseStretchFactor(string value, out double ratio, out string normalized)
        {
            bool result = false;
            string trimmed = value != null ? value.Trim() : "";
            string[] parts;
            long numerator;
            long denominator;
            double decimalValue;
            ratio = 0.0;
            normalized = "";

            if (trimmed.IndexOf('/') >= 0)
            {
                parts = trimmed.Split('/');
                if (parts.Length == 2 &&
                    long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out numerator) &&
                    long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out denominator) &&
                    numerator > 0 &&
                    denominator > 0)
                {
                    ratio = numerator / (double)denominator;
                    normalized = numerator.ToString(CultureInfo.InvariantCulture) + "/" + denominator.ToString(CultureInfo.InvariantCulture);
                    result = ratio > 0.0;
                }
            }
            else if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out decimalValue) && decimalValue > 0.0)
            {
                ratio = decimalValue;
                normalized = decimalValue.ToString("0.########", CultureInfo.InvariantCulture);
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Riepilogo risultati verifica per tutti i punti
        /// </summary>
        public string GetDetailSummary()
        {
            StringBuilder sb = new StringBuilder();
            int validCount = 0;
            int failedCount = 0;
            int minOffset = 0;
            int maxOffset = 0;
            bool hasOffset = false;
            for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
            {
                if (this._verifyPointValid[p])
                {
                    validCount++;
                    if (!hasOffset)
                    {
                        minOffset = this._verifyOffsets[p];
                        maxOffset = this._verifyOffsets[p];
                        hasOffset = true;
                    }
                    else
                    {
                        if (this._verifyOffsets[p] < minOffset) { minOffset = this._verifyOffsets[p]; }
                        if (this._verifyOffsets[p] > maxOffset) { maxOffset = this._verifyOffsets[p]; }
                    }
                }
                else
                {
                    failedCount++;
                }
            }

            sb.Append("checkpoint validi=");
            sb.Append(validCount);
            sb.Append('/');
            sb.Append(this._vsConfig.NumCheckPoints);
            if (hasOffset)
            {
                sb.Append(", offset range=");
                if (minOffset == maxOffset)
                {
                    sb.Append(minOffset).Append("ms");
                }
                else
                {
                    sb.Append(minOffset).Append("..").Append(maxOffset).Append("ms");
                }
            }
            if (failedCount > 0)
            {
                sb.Append(", fail=").Append(failedCount);
            }

            return sb.ToString();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Risolve l'intervallo frame sorgente evitando tolleranze infinite in modalità manuale/VFR
        /// </summary>
        /// <param name="timestampsMs">Timestamp frame da cui stimare l'intervallo, opzionali</param>
        /// <returns>Intervallo frame in millisecondi</returns>
        private double ResolveSourceFrameIntervalMs(double[] timestampsMs)
        {
            double result = 1000.0 / 24000.0 * 1001.0;
            double interval;
            if (this._sourceFps > 0.0)
            {
                result = 1000.0 / this._sourceFps;
            }
            else if (timestampsMs != null && this.TryEstimateFrameIntervalMs(timestampsMs, out interval))
            {
                result = interval;
                this._sourceFps = 1000.0 / result;
            }

            return result;
        }

        /// <summary>
        /// Risolve la tolleranza di verifica offset in base ai PTS sorgente
        /// </summary>
        /// <param name="sourceFrameIntervalMs">Intervallo frame sorgente</param>
        /// <returns>Tolleranza offset in millisecondi</returns>
        private double ResolveVerifyOffsetToleranceMs(double sourceFrameIntervalMs)
        {
            double result = sourceFrameIntervalMs;

            if (this._sourceFrameToleranceMs > result)
            {
                result = this._sourceFrameToleranceMs;
            }

            return result;
        }

        /// <summary>
        /// Stima l'intervallo frame tramite mediana dei delta PTS positivi
        /// </summary>
        /// <param name="timestampsMs">Timestamp frame in millisecondi</param>
        /// <param name="intervalMs">Intervallo stimato</param>
        /// <returns>True se la stima è disponibile</returns>
        private bool TryEstimateFrameIntervalMs(double[] timestampsMs, out double intervalMs)
        {
            bool result = false;
            List<double> deltas = new List<double>();
            double delta;
            intervalMs = 0.0;

            if (timestampsMs == null || timestampsMs.Length < 2)
            {
                return result;
            }

            for (int i = 1; i < timestampsMs.Length; i++)
            {
                delta = timestampsMs[i] - timestampsMs[i - 1];
                if (delta > 1.0 && delta < 200.0)
                {
                    deltas.Add(delta);
                }
            }

            if (deltas.Count >= 10)
            {
                deltas.Sort();
                intervalMs = deltas[deltas.Count / 2];
                this._sourceFrameToleranceMs = deltas[(int)Math.Min(deltas.Count - 1, Math.Round((deltas.Count - 1) * 0.99))];
                if (this._sourceFrameToleranceMs < intervalMs)
                {
                    this._sourceFrameToleranceMs = intervalMs;
                }
                else if (this._sourceFrameToleranceMs > intervalMs * 3.0)
                {
                    this._sourceFrameToleranceMs = intervalMs * 3.0;
                }

                result = intervalMs > 1.0;
            }

            return result;
        }

        /// <summary>
        /// Ricerca delay iniziale tramite voting su tagli di scena
        /// </summary>
        private int FindInitialDelay(string sourceFile, string languageFile)
        {
            int result = int.MinValue;
            List<byte[]> sourceFrames = null;
            List<byte[]> langFrames = null;
            double[] sourceTimestampsMs = null;
            double[] langTimestampsMs = null;
            string sourceFileCopy = sourceFile;
            string langFileCopy = languageFile;
            double sourceFrameIntervalMs;
            double nearestTolMs;
            List<int> sourceCutsRaw;
            List<int> langCutsRaw;
            List<int> validSourceCuts = new List<int>();
            List<int> validLangCuts = new List<int>();
            int srcCutCount;
            int lngCutCount;
            int candidateCount;
            double[] candidates;
            int candidateIdx;
            int bestClusterStart = 0;
            int bestClusterCount;
            int left;
            int currentCount;
            double winningDelay;
            List<DelayCandidateCluster> clusterCandidates = new List<DelayCandidateCluster>();
            List<DelayCandidateCluster> selectedClusters = new List<DelayCandidateCluster>();
            DelayCandidateCluster cluster;
            bool duplicateCluster;
            int maxClustersToVerify = 8;
            List<double> verifiedDelays = new List<double>();
            int verifiedCount;
            double medianDelay;
            int consistentCount;
            double srcCutMs;
            double driftComponent;
            double expectedLangCutMs;
            int nearestLangCutIdx;
            double nearestDistMs;
            double distMs;
            int sigStartSrc;
            int sigStartLng;
            double ssim;
            ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Estrazione frame per ricerca delay (source " + this._scConfig.SourceDurationSec + "s, lang " + this._scConfig.LangDurationSec + "s)...");
            ConsoleHelper.Progress(LogSection.Speed, 28, "Speed: estrazione");

            // Estrae segmenti in parallelo a fps nativo
            bool sourceGeometryCropCopy = this._geometryCropSourceToFourThree;
            bool languageGeometryCropCopy = this._geometryCropLanguageToFourThree;
            string sourceManualCropCopy = this._analysisCropSourcePx;
            string languageManualCropCopy = this._analysisCropLanguagePx;
            Thread sourceThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegment(sourceFileCopy, this._scConfig.SourceStartSec * 1000, this._scConfig.SourceDurationSec, 0, sourceGeometryCropCopy, sourceManualCropCopy, out f, out t);
                sourceFrames = f;
                sourceTimestampsMs = t;
            });
            Thread langThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegment(langFileCopy, 0, this._scConfig.LangDurationSec, 0, languageGeometryCropCopy, languageManualCropCopy, out f, out t);
                langFrames = f;
                langTimestampsMs = t;
            });
            sourceThread.Start();
            langThread.Start();
            sourceThread.Join();
            langThread.Join();

            // Verifica frame sufficienti
            if (sourceFrames == null || sourceFrames.Count < this._vsConfig.CutSignatureLength)
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Frame sorgente insufficienti: " + (sourceFrames != null ? sourceFrames.Count : 0));
            }
            else if (langFrames == null || langFrames.Count < this._vsConfig.CutSignatureLength)
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Frame lingua insufficienti: " + (langFrames != null ? langFrames.Count : 0));
            }
            else
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Frame estratti: source=" + sourceFrames.Count + ", lang=" + langFrames.Count);
                sourceFrameIntervalMs = this.ResolveSourceFrameIntervalMs(sourceTimestampsMs);
                nearestTolMs = 3.0 * sourceFrameIntervalMs;

                // Rileva tagli di scena in entrambi i segmenti
                sourceCutsRaw = this.DetectSceneCuts(sourceFrames);
                langCutsRaw = this.DetectSceneCuts(langFrames);

                // Filtra tagli con margine sufficiente per la firma
                for (int i = 0; i < sourceCutsRaw.Count; i++)
                {
                    if (sourceCutsRaw[i] >= this._vsConfig.CutHalfWindow && sourceCutsRaw[i] + this._vsConfig.CutHalfWindow <= sourceFrames.Count)
                    {
                        validSourceCuts.Add(sourceCutsRaw[i]);
                    }
                }
                for (int i = 0; i < langCutsRaw.Count; i++)
                {
                    if (langCutsRaw[i] >= this._vsConfig.CutHalfWindow && langCutsRaw[i] + this._vsConfig.CutHalfWindow <= langFrames.Count)
                    {
                        validLangCuts.Add(langCutsRaw[i]);
                    }
                }

                srcCutCount = validSourceCuts.Count;
                lngCutCount = validLangCuts.Count;
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Tagli rilevati: source=" + sourceCutsRaw.Count + " (" + srcCutCount + " utilizzabili), lang=" + langCutsRaw.Count + " (" + lngCutCount + " utilizzabili)");
                ConsoleHelper.Progress(LogSection.Speed, 38, "Speed: candidati");

                if (srcCutCount >= this._vsConfig.MinSceneCuts && lngCutCount >= this._vsConfig.MinSceneCuts)
                {
                    // Genera langDelay candidati da tutte le coppie (sourceCut, langCut)
                    candidateCount = srcCutCount * lngCutCount;
                    candidates = new double[candidateCount];
                    candidateIdx = 0;

                    for (int s = 0; s < srcCutCount; s++)
                    {
                        srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                        driftComponent = srcCutMs * (this._inverseRatio - 1.0);

                        for (int l = 0; l < lngCutCount; l++)
                        {
                            double lngCutMs = langTimestampsMs[validLangCuts[l]];
                            // Rimuove componente drift per isolare il delay iniziale
                            candidates[candidateIdx] = (lngCutMs - srcCutMs) - driftComponent;
                            candidateIdx++;
                        }
                    }

                    // Ordina e trova i cluster più densi (sliding window di 3 frame sorgente)
                    // Sul VFR il cluster più denso può essere un falso positivo: si verificano più
                    // candidati, mantenendo fail-safe se nessuno è confermato dai tagli reali
                    double votingWindow = sourceFrameIntervalMs * 3.0;
                    Array.Sort(candidates);
                    bestClusterCount = 0;
                    left = 0;

                    for (int r = 0; r < candidateCount; r++)
                    {
                        while (candidates[r] - candidates[left] > votingWindow)
                        {
                            left++;
                        }
                        currentCount = r - left + 1;
                        if (currentCount > bestClusterCount)
                        {
                            bestClusterCount = currentCount;
                            bestClusterStart = left;
                        }

                        if (currentCount >= this._vsConfig.MinSceneCuts)
                        {
                            cluster = new DelayCandidateCluster();
                            cluster.Start = left;
                            cluster.Count = currentCount;
                            cluster.DelayMs = candidates[left + currentCount / 2];
                            clusterCandidates.Add(cluster);
                        }
                    }

                    clusterCandidates.Sort(delegate (DelayCandidateCluster a, DelayCandidateCluster b)
                    {
                        int cmp = b.Count.CompareTo(a.Count);
                        if (cmp != 0)
                        {
                            return cmp;
                        }

                        return Math.Abs(a.DelayMs).CompareTo(Math.Abs(b.DelayMs));
                    });

                    for (int i = 0; i < clusterCandidates.Count && selectedClusters.Count < maxClustersToVerify; i++)
                    {
                        duplicateCluster = false;
                        for (int j = 0; j < selectedClusters.Count; j++)
                        {
                            if (Math.Abs(clusterCandidates[i].DelayMs - selectedClusters[j].DelayMs) <= votingWindow)
                            {
                                duplicateCluster = true;
                                break;
                            }
                        }

                        if (!duplicateCluster)
                        {
                            selectedClusters.Add(clusterCandidates[i]);
                        }
                    }

                    if (selectedClusters.Count == 0 && bestClusterCount > 0)
                    {
                        cluster = new DelayCandidateCluster();
                        cluster.Start = bestClusterStart;
                        cluster.Count = bestClusterCount;
                        cluster.DelayMs = candidates[bestClusterStart + bestClusterCount / 2];
                        selectedClusters.Add(cluster);
                    }

                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Voting: " + bestClusterCount + " voti su " + candidateCount + " candidati, delay=" + ((int)Math.Round(candidates[bestClusterStart + bestClusterCount / 2])) + "ms, cluster verificati=" + selectedClusters.Count);

                    for (int c = 0; c < selectedClusters.Count && result == int.MinValue; c++)
                    {
                        winningDelay = selectedClusters[c].DelayMs;
                        verifiedDelays.Clear();
                        consistentCount = 0;

                        // Verifica MSE: per ogni taglio source trova il taglio lang atteso e confronta firma
                        for (int s = 0; s < srcCutCount; s++)
                        {
                            srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                            driftComponent = srcCutMs * (this._inverseRatio - 1.0);

                            // Posizione attesa del taglio lang: srcCutMs * _inverseRatio + winningDelay
                            expectedLangCutMs = srcCutMs + winningDelay + driftComponent;

                            // Cerca il taglio lang più vicino alla posizione attesa (distanza in ms)
                            nearestLangCutIdx = -1;
                            nearestDistMs = double.MaxValue;
                            for (int l = 0; l < lngCutCount; l++)
                            {
                                distMs = Math.Abs(langTimestampsMs[validLangCuts[l]] - expectedLangCutMs);
                                if (distMs < nearestDistMs)
                                {
                                    nearestDistMs = distMs;
                                    nearestLangCutIdx = l;
                                }
                            }

                            // Verifica solo se taglio lang entro 3 frame dalla posizione attesa
                            if (nearestLangCutIdx >= 0 && nearestDistMs <= nearestTolMs)
                            {
                                sigStartSrc = validSourceCuts[s] - this._vsConfig.CutHalfWindow;
                                sigStartLng = validLangCuts[nearestLangCutIdx] - this._vsConfig.CutHalfWindow;

                                if (sigStartLng >= 0 && sigStartLng + this._vsConfig.CutSignatureLength <= langFrames.Count)
                                {
                                    ssim = this.ComputeSequenceSsim(sourceFrames, sigStartSrc, langFrames, sigStartLng, this._vsConfig.CutSignatureLength);

                                    if (ssim >= this._vsConfig.SsimThreshold && ssim <= this._vsConfig.SsimMaxThreshold)
                                    {
                                        // Calcola langDelay verificato
                                        double actualLngMs = langTimestampsMs[validLangCuts[nearestLangCutIdx]];
                                        double actualDelay = (actualLngMs - srcCutMs) - driftComponent;
                                        verifiedDelays.Add(actualDelay);
                                    }
                                }
                            }
                        }

                        verifiedCount = verifiedDelays.Count;

                        // Fallback: se SSIM insufficiente, ritenta con fingerprint temporale
                        // Il fingerprint confronta pattern di variazione inter-frame ed è fps-indipendente
                        if (verifiedCount < this._vsConfig.MinSceneCuts)
                        {
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  SSIM insufficiente, fallback a fingerprint temporale...");
                            ConsoleHelper.Progress(LogSection.Speed, 45, "Speed: fingerprint");
                            verifiedDelays.Clear();

                            for (int s = 0; s < srcCutCount; s++)
                            {
                                srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                                driftComponent = srcCutMs * (this._inverseRatio - 1.0);
                                expectedLangCutMs = srcCutMs + winningDelay + driftComponent;

                                // Cerca il taglio lang più vicino alla posizione attesa (distanza in ms)
                                nearestLangCutIdx = -1;
                                nearestDistMs = double.MaxValue;
                                for (int l = 0; l < lngCutCount; l++)
                                {
                                    distMs = Math.Abs(langTimestampsMs[validLangCuts[l]] - expectedLangCutMs);
                                    if (distMs < nearestDistMs)
                                    {
                                        nearestDistMs = distMs;
                                        nearestLangCutIdx = l;
                                    }
                                }

                                // Verifica solo se taglio lang entro 3 frame dalla posizione attesa
                                if (nearestLangCutIdx >= 0 && nearestDistMs <= nearestTolMs)
                                {
                                    double[] srcFingerprint = this.ComputeTemporalFingerprint(sourceFrames, validSourceCuts[s]);
                                    double[] lngFingerprint = this.ComputeTemporalFingerprint(langFrames, validLangCuts[nearestLangCutIdx]);

                                    if (srcFingerprint != null && lngFingerprint != null)
                                    {
                                        double correlation = this.ComputeFingerprintCorrelation(srcFingerprint, lngFingerprint);

                                        if (correlation >= this._vsConfig.FingerprintCorrelationThreshold)
                                        {
                                            double actualLngMs = langTimestampsMs[validLangCuts[nearestLangCutIdx]];
                                            double actualDelay = (actualLngMs - srcCutMs) - driftComponent;
                                            verifiedDelays.Add(actualDelay);
                                        }
                                    }
                                }
                            }

                        }

                        if (verifiedDelays.Count >= this._vsConfig.MinSceneCuts)
                        {
                            // Mediana dei delay verificati
                            verifiedDelays.Sort();
                            medianDelay = verifiedDelays[verifiedDelays.Count / 2];

                            // Verifica consistenza entro 1 frame dalla mediana
                            for (int i = 0; i < verifiedDelays.Count; i++)
                            {
                                if (Math.Abs(verifiedDelays[i] - medianDelay) <= sourceFrameIntervalMs)
                                {
                                    consistentCount++;
                                }
                            }

                            if (consistentCount >= this._vsConfig.MinSceneCuts)
                            {
                                result = (int)Math.Round(medianDelay);
                                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Delay iniziale: " + result + "ms (mediana di " + verifiedDelays.Count + " tagli, " + consistentCount + " consistenti)");
                            }
                            else
                            {
                                ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Delay non consistente: solo " + consistentCount + "/" + verifiedDelays.Count + " entro 1 frame dalla mediana");
                            }
                        }
                        else
                        {
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Solo " + verifiedDelays.Count + " tagli verificati (minimo: " + this._vsConfig.MinSceneCuts + ")");
                        }
                    }
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Tagli di scena insufficienti: source=" + srcCutCount + ", lang=" + lngCutCount + " (minimo: " + this._vsConfig.MinSceneCuts + ")");
                }
            }

            return result;
        }

        /// <summary>
        /// Verifica correzione velocità a 9 punti con scene-cut matching parallelo
        /// </summary>
        private bool VerifyCorrection(string sourceFile, string languageFile, int sourceDurationMs, int initialDelayMs)
        {
            bool success = false;
            int validCount = 0;
            double sourceFrameIntervalMs = this.ResolveSourceFrameIntervalMs(null);
            double verifyOffsetToleranceMs = this.ResolveVerifyOffsetToleranceMs(sourceFrameIntervalMs);
            int threadCount = 4;
            string srcFile = sourceFile;
            string lngFile = languageFile;
            int percentage;
            int expectedOffset;
            int offsetError;
            int retryCount = 0;
            // Limita ai punti disponibili
            if (threadCount > this._vsConfig.NumCheckPoints)
            {
                threadCount = this._vsConfig.NumCheckPoints;
            }

            // Primo passaggio con finestra base
            Thread[] workers = new Thread[threadCount];

            for (int w = 0; w < threadCount; w++)
            {
                int workerIndex = w;

                workers[w] = new Thread(() =>
                {
                    for (int p = workerIndex; p < this._vsConfig.NumCheckPoints; p += threadCount)
                    {
                        int pct = (p + 1) * 10;
                        int sourceTimestampMs = (int)((long)sourceDurationMs * pct / 100);
                        int expOffset = initialDelayMs + (int)Math.Round(sourceTimestampMs * (this._inverseRatio - 1.0));

                        double ssim;
                        int actualOffset = this.VerifyAtPoint(srcFile, lngFile, sourceTimestampMs, expOffset, this._vsConfig.VerifySourceDurationSec, this._vsConfig.VerifyLangDurationSec, out ssim);

                        this._verifyOffsets[p] = actualOffset;
                        this._verifySsimValues[p] = ssim;

                        if (actualOffset != int.MinValue)
                        {
                            int err = Math.Abs(actualOffset - expOffset);
                            if (err <= verifyOffsetToleranceMs)
                            {
                                this._verifyPointValid[p] = true;
                            }
                        }
                    }
                });

                workers[w].Start();
            }

            // Attende completamento primo passaggio
            for (int w = 0; w < threadCount; w++)
            {
                workers[w].Join();
            }

            // Retry con finestra allargata per i punti falliti
            for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
            {
                if (!this._verifyPointValid[p])
                {
                    retryCount++;
                }
            }

            if (retryCount > 0 && retryCount < this._vsConfig.NumCheckPoints)
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Retry " + retryCount + " punti con finestra allargata (" + this._vsConfig.VerifySourceRetrySec + "s/" + this._vsConfig.VerifyLangRetrySec + "s)...");
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Tolleranza verifica offset: " + verifyOffsetToleranceMs.ToString("F1", CultureInfo.InvariantCulture) + "ms");

                // Raccoglie indici dei punti falliti
                List<int> failedPoints = new List<int>();
                for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
                {
                    if (!this._verifyPointValid[p])
                    {
                        failedPoints.Add(p);
                    }
                }

                // Limita thread retry a 4 (ogni ffmpeg usa già auto-threading)
                int retryThreadCount = 4;
                if (retryThreadCount > failedPoints.Count)
                {
                    retryThreadCount = failedPoints.Count;
                }

                Thread[] retryWorkers = new Thread[retryThreadCount];
                List<int> failedPointsCopy = failedPoints;

                for (int w = 0; w < retryThreadCount; w++)
                {
                    int workerIndex = w;

                    retryWorkers[w] = new Thread(() =>
                    {
                        for (int f = workerIndex; f < failedPointsCopy.Count; f += retryThreadCount)
                        {
                            int pointIndex = failedPointsCopy[f];
                            int pct = (pointIndex + 1) * 10;
                            int sourceTimestampMs = (int)((long)sourceDurationMs * pct / 100);
                            int expOffset = initialDelayMs + (int)Math.Round(sourceTimestampMs * (this._inverseRatio - 1.0));

                            double ssim;
                            int actualOffset = this.VerifyAtPoint(srcFile, lngFile, sourceTimestampMs, expOffset, this._vsConfig.VerifySourceRetrySec, this._vsConfig.VerifyLangRetrySec, out ssim);

                            this._verifyOffsets[pointIndex] = actualOffset;
                            this._verifySsimValues[pointIndex] = ssim;

                            if (actualOffset != int.MinValue)
                            {
                                int err = Math.Abs(actualOffset - expOffset);
                                if (err <= verifyOffsetToleranceMs)
                                {
                                    this._verifyPointValid[pointIndex] = true;
                                }
                            }
                        }
                    });

                    retryWorkers[w].Start();
                }

                // Attende completamento retry
                for (int r = 0; r < retryThreadCount; r++)
                {
                    retryWorkers[r].Join();
                }
            }

            // Log risultati
            for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
            {
                percentage = (p + 1) * 10;
                expectedOffset = initialDelayMs + (int)Math.Round(((double)sourceDurationMs * percentage / 100.0) * (this._inverseRatio - 1.0));

                if (this._verifyPointValid[p])
                {
                    validCount++;
                    offsetError = Math.Abs(this._verifyOffsets[p] - expectedOffset);
                    // Valore negativo = correlazione fingerprint, positivo = SSIM
                    if (this._verifySsimValues[p] < 0)
                    {
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  " + percentage + "%: offset=" + this._verifyOffsets[p] + "ms (atteso=" + expectedOffset + "ms, err=" + offsetError + "ms, corr=" + (-this._verifySsimValues[p]).ToString("F2", CultureInfo.InvariantCulture) + ")");
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  " + percentage + "%: offset=" + this._verifyOffsets[p] + "ms (atteso=" + expectedOffset + "ms, err=" + offsetError + "ms, SSIM=" + this._verifySsimValues[p].ToString("F3", CultureInfo.InvariantCulture) + ")");
                    }
                }
                else if (this._verifyOffsets[p] != int.MinValue)
                {
                    offsetError = Math.Abs(this._verifyOffsets[p] - expectedOffset);
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  " + percentage + "%: offset=" + this._verifyOffsets[p] + "ms troppo diverso da atteso=" + expectedOffset + "ms (err=" + offsetError + "ms)");
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  " + percentage + "%: nessun match");
                }
            }

            // Verifica numero minimo punti validi
            if (validCount >= this._vsConfig.MinValidPoints)
            {
                success = true;
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Verifica superata: " + validCount + "/" + this._vsConfig.NumCheckPoints + " punti validi");
                ConsoleHelper.Progress(LogSection.Speed, 55, "Speed: verifica");
            }
            else
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Solo " + validCount + "/" + this._vsConfig.NumCheckPoints + " punti validi (minimo: " + this._vsConfig.MinValidPoints + ")");
            }

            return success;
        }

        /// <summary>
        /// Verifica offset a un singolo punto temporale tramite cut-to-cut matching
        /// </summary>
        private int VerifyAtPoint(string sourceFile, string languageFile, int sourceTimestampMs, int expectedOffset, int sourceDurationSec, int langDurationSec, out double bestSsim)
        {
            bestSsim = 0.0;
            int resultOffset = int.MinValue;
            double sourceFrameIntervalMs = this.ResolveSourceFrameIntervalMs(null);
            double nearestTolMs = 3.0 * sourceFrameIntervalMs;
            int halfSourceMs = (sourceDurationSec * 1000) / 2;
            int sourceStartMs = sourceTimestampMs - halfSourceMs;
            int langCenter = sourceTimestampMs + expectedOffset;
            int halfLangMs = (langDurationSec * 1000) / 2;
            int langStartMs = langCenter - halfLangMs;
            List<byte[]> sourceFrames = null;
            List<byte[]> langFrames = null;
            double[] sourceTimestampsMs = null;
            double[] langTimestampsMs = null;
            string sourceFileCopy = sourceFile;
            string langFileCopy = languageFile;
            int sourceStartMsCopy;
            int langStartMsCopy;
            List<int> sourceCutsRaw;
            List<int> langCutsRaw;
            List<int> validSourceCuts = new List<int>();
            List<int> validLangCuts = new List<int>();
            int srcCutCount;
            int lngCutCount;
            List<double> cutOffsets = new List<double>();
            double medianOffset;
            double srcCutMs;
            double expectedLangCutMs;
            int nearestLangCutIdx;
            double nearestDistMs;
            double distMs;
            int sigStartSrc;
            int sigStartLng;
            double ssim;
            double driftDelta;
            // Limita inizio segmenti a 0
            if (sourceStartMs < 0)
            {
                sourceStartMs = 0;
            }
            if (langStartMs < 0)
            {
                langStartMs = 0;
            }
            sourceStartMsCopy = sourceStartMs;
            langStartMsCopy = langStartMs;

            // Estrae i due segmenti in parallelo a fps nativo
            bool sourceGeometryCropCopy = this._geometryCropSourceToFourThree;
            bool languageGeometryCropCopy = this._geometryCropLanguageToFourThree;
            string sourceManualCropCopy = this._analysisCropSourcePx;
            string languageManualCropCopy = this._analysisCropLanguagePx;
            Thread sourceThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegment(sourceFileCopy, sourceStartMsCopy, sourceDurationSec, 0, sourceGeometryCropCopy, sourceManualCropCopy, out f, out t);
                sourceFrames = f;
                sourceTimestampsMs = t;
            });
            Thread langThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegment(langFileCopy, langStartMsCopy, langDurationSec, 0, languageGeometryCropCopy, languageManualCropCopy, out f, out t);
                langFrames = f;
                langTimestampsMs = t;
            });
            sourceThread.Start();
            langThread.Start();
            sourceThread.Join();
            langThread.Join();

            // Verifica frame sufficienti
            if (sourceFrames != null && sourceFrames.Count >= this._vsConfig.CutSignatureLength && langFrames != null && langFrames.Count >= this._vsConfig.CutSignatureLength)
            {
                // Rileva tagli in entrambi i segmenti
                sourceCutsRaw = this.DetectSceneCuts(sourceFrames);
                langCutsRaw = this.DetectSceneCuts(langFrames);

                // Filtra tagli con margine sufficiente
                for (int i = 0; i < sourceCutsRaw.Count; i++)
                {
                    if (sourceCutsRaw[i] >= this._vsConfig.CutHalfWindow && sourceCutsRaw[i] + this._vsConfig.CutHalfWindow <= sourceFrames.Count)
                    {
                        validSourceCuts.Add(sourceCutsRaw[i]);
                    }
                }
                for (int i = 0; i < langCutsRaw.Count; i++)
                {
                    if (langCutsRaw[i] >= this._vsConfig.CutHalfWindow && langCutsRaw[i] + this._vsConfig.CutHalfWindow <= langFrames.Count)
                    {
                        validLangCuts.Add(langCutsRaw[i]);
                    }
                }

                srcCutCount = validSourceCuts.Count;
                lngCutCount = validLangCuts.Count;

                // Per ogni taglio source, cerca il taglio lang atteso e verifica MSE
                for (int s = 0; s < srcCutCount; s++)
                {
                    // Posizione assoluta del taglio source (timestamp reale dal showinfo)
                    srcCutMs = sourceTimestampsMs[validSourceCuts[s]];

                    // Posizione attesa del taglio lang (basata su expectedOffset)
                    expectedLangCutMs = srcCutMs + expectedOffset;

                    // Cerca il taglio lang più vicino (distanza in ms)
                    nearestLangCutIdx = -1;
                    nearestDistMs = double.MaxValue;
                    for (int l = 0; l < lngCutCount; l++)
                    {
                        distMs = Math.Abs(langTimestampsMs[validLangCuts[l]] - expectedLangCutMs);
                        if (distMs < nearestDistMs)
                        {
                            nearestDistMs = distMs;
                            nearestLangCutIdx = l;
                        }
                    }

                    // Verifica solo se taglio lang entro 3 frame dalla posizione attesa
                    if (nearestLangCutIdx >= 0 && nearestDistMs <= nearestTolMs)
                    {
                        sigStartSrc = validSourceCuts[s] - this._vsConfig.CutHalfWindow;
                        sigStartLng = validLangCuts[nearestLangCutIdx] - this._vsConfig.CutHalfWindow;

                        if (sigStartLng >= 0 && sigStartLng + this._vsConfig.CutSignatureLength <= langFrames.Count)
                        {
                            ssim = this.ComputeSequenceSsim(sourceFrames, sigStartSrc, langFrames, sigStartLng, this._vsConfig.CutSignatureLength);

                            if (ssim >= this._vsConfig.SsimThreshold && ssim <= this._vsConfig.SsimMaxThreshold)
                            {
                                // Offset grezzo al punto del taglio
                                double actualLngMs = langTimestampsMs[validLangCuts[nearestLangCutIdx]];
                                double rawOffset = actualLngMs - srcCutMs;

                                // Normalizza rimuovendo il drift relativo al centro della finestra
                                // Così tutti gli offset sono riferiti a sourceTimestampMs
                                driftDelta = (srcCutMs - sourceTimestampMs) * (this._inverseRatio - 1.0);
                                cutOffsets.Add(rawOffset - driftDelta);

                                // Aggiorna SSIM migliore
                                if (ssim > bestSsim)
                                {
                                    bestSsim = ssim;
                                }
                            }
                        }
                    }
                }

                // Fallback: se MSE pixel fallisce, ritenta con fingerprint temporale
                if (cutOffsets.Count == 0 && srcCutCount > 0 && lngCutCount > 0)
                {
                    for (int s = 0; s < srcCutCount; s++)
                    {
                        srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                        expectedLangCutMs = srcCutMs + expectedOffset;

                        nearestLangCutIdx = -1;
                        nearestDistMs = double.MaxValue;
                        for (int l = 0; l < lngCutCount; l++)
                        {
                            distMs = Math.Abs(langTimestampsMs[validLangCuts[l]] - expectedLangCutMs);
                            if (distMs < nearestDistMs)
                            {
                                nearestDistMs = distMs;
                                nearestLangCutIdx = l;
                            }
                        }

                        if (nearestLangCutIdx >= 0 && nearestDistMs <= nearestTolMs)
                        {
                            double[] srcFp = this.ComputeTemporalFingerprint(sourceFrames, validSourceCuts[s]);
                            double[] lngFp = this.ComputeTemporalFingerprint(langFrames, validLangCuts[nearestLangCutIdx]);

                            if (srcFp != null && lngFp != null)
                            {
                                double correlation = this.ComputeFingerprintCorrelation(srcFp, lngFp);

                                if (correlation >= this._vsConfig.FingerprintCorrelationThreshold)
                                {
                                    double actualLngMs = langTimestampsMs[validLangCuts[nearestLangCutIdx]];
                                    double rawOffset = actualLngMs - srcCutMs;

                                    driftDelta = (srcCutMs - sourceTimestampMs) * (this._inverseRatio - 1.0);
                                    cutOffsets.Add(rawOffset - driftDelta);

                                    // Salva correlazione come valore negativo per distinguerla da SSIM
                                    if (bestSsim == 0.0 || correlation > -bestSsim)
                                    {
                                        bestSsim = -correlation;
                                    }
                                }
                            }
                        }
                    }
                }

                // Calcola offset come mediana dei tagli matchati
                if (cutOffsets.Count > 0)
                {
                    cutOffsets.Sort();
                    medianOffset = cutOffsets[cutOffsets.Count / 2];
                    resultOffset = (int)Math.Round(medianOffset);
                }
            }

            return resultOffset;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Cluster candidato generato dal voting dei tagli di scena
        /// </summary>
        private class DelayCandidateCluster
        {
            /// <summary>
            /// Offset iniziale del cluster nella lista candidati
            /// </summary>
            public int Start { get; set; }

            /// <summary>
            /// Numero candidati nel cluster
            /// </summary>
            public int Count { get; set; }

            /// <summary>
            /// Delay medio/mediano associato al cluster
            /// </summary>
            public double DelayMs { get; set; }
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Delay iniziale rilevato in ms
        /// </summary>
        public int InitialDelayMs { get { return this._initialDelayMs; } }

        /// <summary>
        /// Rapporto stretch per mkvmerge
        /// </summary>
        public string StretchFactor { get { return this._stretchFactor; } }

        /// <summary>
        /// Delay per mkvmerge --sync
        /// </summary>
        public int SyncDelayMs { get { return this._syncDelayMs; } }

        /// <summary>
        /// FPS file sorgente
        /// </summary>
        public double SourceFps { get { return this._sourceFps; } }

        /// <summary>
        /// FPS file lingua
        /// </summary>
        public double LangFps { get { return this._langFps; } }

        /// <summary>
        /// Tempo di esecuzione totale in ms
        /// </summary>
        public long ExecutionTimeMs { get { return this._executionTimeMs; } }

        /// <summary>
        /// Offset di verifica per ciascun punto
        /// </summary>
        public int[] VerifyOffsets { get { return this._verifyOffsets; } }

        /// <summary>
        /// Valori SSIM di verifica per ciascun punto
        /// </summary>
        public double[] VerifySsimValues { get { return this._verifySsimValues; } }

        /// <summary>
        /// Validità di ciascun punto di verifica
        /// </summary>
        public bool[] VerifyPointValid { get { return this._verifyPointValid; } }

        #endregion
    }
}
