using RemuxForge.Core.Analysis.Deep.Features;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Analysis.Speed
{
    /// <summary>
    /// Verifica un rapporto di playback noto e risolve il delay tramite corrispondenze visuali SIFT
    /// </summary>
    public class SpeedCorrectionService : VideoSyncServiceBase
    {
        private const double SAMPLE_INTERVAL_SEC = 1.0;
        private const double OFFSET_BIN_MS = 250.0;

        private readonly SpeedCorrectionConfig _speedConfig;
        private int _initialDelayMs;
        private string _stretchFactor;
        private int _syncDelayMs;
        private long _executionTimeMs;
        private string _rejectReason;
        private int _supportCount;
        private double _sourceSpanMs;
        private double _medianResidualMs;

        /// <summary>
        /// Costruisce il servizio usando la configurazione SpeedCorrection corrente
        /// </summary>
        public SpeedCorrectionService(string ffmpegPath) : base(ffmpegPath, LogSection.Speed)
        {
            this._speedConfig = AppSettingsService.Instance.Settings.Advanced.SpeedCorrection;
            this._stretchFactor = "";
            this._rejectReason = "";
        }

        /// <summary>
        /// Risolve il delay visuale usando un rapporto manuale esplicito
        /// </summary>
        /// <param name="sourceFile">File che definisce la timeline finale</param>
        /// <param name="languageFile">File Language da adattare</param>
        /// <param name="manualStretchFactor">Fattore temporale decimale o frazionario</param>
        /// <returns>True se il rapporto è verificato e il delay è stato risolto</returns>
        public bool FindDelayAndVerifyManual(string sourceFile, string languageFile, string manualStretchFactor)
        {
            this.ResetResult();
            if (!TryParseStretchFactor(manualStretchFactor, out double stretchRatio, out string normalized))
            {
                this._rejectReason = "Fattore stretch manuale non valido";
                return false;
            }
            double scale = 1.0 / stretchRatio;
            if (!double.IsFinite(scale) || scale <= 0.0)
            {
                this._rejectReason = "Scala temporale manuale non valida";
                return false;
            }
            return this.Resolve(sourceFile, languageFile, scale, normalized);
        }

        /// <summary>
        /// Converte un fattore decimale o frazionario in rapporto positivo normalizzato
        /// </summary>
        public static bool TryParseStretchFactor(string value, out double ratio, out string normalized)
        {
            ratio = 0.0;
            normalized = "";
            string text = value != null ? value.Trim() : "";
            if (string.IsNullOrEmpty(text))
                return false;
            int separatorIndex = text.IndexOf('/');
            if (separatorIndex >= 0)
            {
                if (separatorIndex == 0 || separatorIndex == text.Length - 1 || text.IndexOf('/', separatorIndex + 1) >= 0)
                    return false;
                if (!double.TryParse(text.Substring(0, separatorIndex), NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) ||
                    !double.TryParse(text.Substring(separatorIndex + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) ||
                    numerator <= 0.0 || denominator <= 0.0 || double.IsNaN(numerator) || double.IsInfinity(numerator) || double.IsNaN(denominator) || double.IsInfinity(denominator))
                    return false;
                ratio = numerator / denominator;
                if (!double.IsFinite(ratio) || ratio <= 0.0)
                    return false;
                normalized = text;
                return true;
            }
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out ratio) || ratio <= 0.0 || double.IsNaN(ratio) || double.IsInfinity(ratio))
                return false;
            normalized = ratio.ToString("R", CultureInfo.InvariantCulture);
            return true;
        }

        /// <summary>
        /// Restituisce il riepilogo dell'ultima risoluzione visuale
        /// </summary>
        public string GetDetailSummary()
        {
            if (!string.IsNullOrEmpty(this._rejectReason))
                return this._rejectReason;
            if (this._supportCount == 0)
                return "nessuna analisi visuale";
            return "supporto=" + this._supportCount.ToString(CultureInfo.InvariantCulture) +
                ", span=" + this._sourceSpanMs.ToString("F0", CultureInfo.InvariantCulture) + "ms" +
                ", residuo=" + this._medianResidualMs.ToString("F1", CultureInfo.InvariantCulture) + "ms";
        }

        private bool Resolve(string sourceFile, string languageFile, double scale, string stretchFactor)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                this.PrepareGeometryDrivenCrop(sourceFile, languageFile);
                this.ExtractInitialFrames(sourceFile, languageFile, out List<byte[]> sourceFrames, out double[] sourcePtsMs, out List<byte[]> languageFrames, out double[] languagePtsMs);
                if (sourceFrames.Count < 5 || languageFrames.Count < 5)
                {
                    this._rejectReason = "Frame visuali insufficienti";
                    return false;
                }
                List<DeepSiftVisualAnchor> sourceAnchors = this.BuildAnchors(sourceFrames, sourcePtsMs);
                List<DeepSiftVisualAnchor> languageAnchors = this.BuildAnchors(languageFrames, languagePtsMs);
                using (FrameFeatureBatchMatcherBase matcher = this.CreateMatcher())
                {
                    if (!matcher.IsAvailable(out string rejectReason))
                    {
                        this._rejectReason = rejectReason;
                        return false;
                    }
                    DeepSiftBatchMatchResult batch = matcher.BuildMatrix(sourceAnchors, languageAnchors, ParallelismHelper.ResolveDefaultMaxDegree(), CancellationToken.None);
                    if (batch == null || batch.Cancelled || !string.IsNullOrEmpty(batch.RejectReason))
                    {
                        this._rejectReason = batch != null ? batch.RejectReason : "Matching visuale non disponibile";
                        return false;
                    }
                    double sourceSpanMs = sourcePtsMs[sourcePtsMs.Length - 1] - sourcePtsMs[0];
                    if (!this.TryResolveOffset(batch.AcceptedPairs, scale, sourceSpanMs, out double offsetMs))
                        return false;
                    this._stretchFactor = stretchFactor;
                    this._syncDelayMs = (int)Math.Round(offsetMs);
                    this._initialDelayMs = (int)Math.Round(scale * offsetMs);
                }
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Rapporto verificato: scala=" + scale.ToString("R", CultureInfo.InvariantCulture) + ", stretch=" + this._stretchFactor + ", sync=" + this._syncDelayMs.ToString(CultureInfo.InvariantCulture) + "ms");
                return true;
            }
            finally
            {
                stopwatch.Stop();
                this._executionTimeMs = stopwatch.ElapsedMilliseconds;
            }
        }

        private void ResetResult()
        {
            this._initialDelayMs = 0;
            this._stretchFactor = "";
            this._syncDelayMs = 0;
            this._executionTimeMs = 0;
            this._rejectReason = "";
            this._supportCount = 0;
            this._sourceSpanMs = 0.0;
            this._medianResidualMs = 0.0;
        }

        /// <summary>
        /// Trova l'offset con il maggior supporto per una scala già nota
        /// </summary>
        /// <param name="pairs">Corrispondenze visuali accettate dal matcher</param>
        /// <param name="scale">Scala temporale già nota</param>
        /// <param name="availableSourceSpanMs">Intervallo sorgente disponibile in millisecondi</param>
        /// <param name="offsetMs">Offset visuale risolto in millisecondi</param>
        /// <returns>True se il supporto temporale è sufficiente</returns>
        private bool TryResolveOffset(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, double scale, double availableSourceSpanMs, out double offsetMs)
        {
            OffsetCluster best = null;
            offsetMs = 0.0;

            for (int phaseIndex = 0; phaseIndex < 2; phaseIndex++)
            {
                double phaseMs = phaseIndex * OFFSET_BIN_MS * 0.5;
                Dictionary<long, OffsetCluster> clusters = new Dictionary<long, OffsetCluster>();
                for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
                {
                    DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                    double pairOffsetMs = pair.SourcePtsMs - (pair.LanguagePtsMs / scale);
                    long bin = (long)Math.Floor((pairOffsetMs + phaseMs) / OFFSET_BIN_MS);
                    if (!clusters.TryGetValue(bin, out OffsetCluster cluster))
                    {
                        cluster = new OffsetCluster();
                        clusters.Add(bin, cluster);
                    }
                    cluster.Add(pair, pairOffsetMs);
                }

                foreach (OffsetCluster cluster in clusters.Values)
                {
                    if (best == null || cluster.IsBetterThan(best))
                        best = cluster;
                }
            }

            if (best == null || best.SourceCount < 5 || best.LanguageCount < 5 || best.SourceSpanMs < Math.Min(90000.0, availableSourceSpanMs * 0.3))
            {
                this._rejectReason = "Supporto visuale temporale insufficiente";
                return false;
            }

            offsetMs = best.MedianOffsetMs;
            this._supportCount = best.SourceCount;
            this._sourceSpanMs = best.SourceSpanMs;
            this._medianResidualMs = best.MedianResidualMs;
            return true;
        }

        private void ExtractInitialFrames(string sourceFile, string languageFile, out List<byte[]> sourceFrames, out double[] sourcePtsMs, out List<byte[]> languageFrames, out double[] languagePtsMs)
        {
            List<byte[]> source = null;
            List<byte[]> language = null;
            double[] sourcePts = null;
            double[] languagePts = null;
            Parallel.Invoke(
                () => this.ExtractSegmentAtInterval(sourceFile, this._speedConfig.SourceStartSec * 1000, this._speedConfig.SourceDurationSec, SAMPLE_INTERVAL_SEC, this._geometryCropSourceToFourThree, this._analysisCropSourcePx, out source, out sourcePts),
                () => this.ExtractSegmentAtInterval(languageFile, 0, this._speedConfig.LangDurationSec, SAMPLE_INTERVAL_SEC, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx, out language, out languagePts));
            sourceFrames = source ?? new List<byte[]>();
            languageFrames = language ?? new List<byte[]>();
            sourcePtsMs = sourcePts ?? Array.Empty<double>();
            languagePtsMs = languagePts ?? Array.Empty<double>();
        }

        private List<DeepSiftVisualAnchor> BuildAnchors(List<byte[]> frames, double[] ptsMs)
        {
            int count = Math.Min(frames.Count, ptsMs.Length);
            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>(count);
            for (int frameIndex = 0; frameIndex < count; frameIndex++)
            {
                double durationMs = frameIndex + 1 < count ? ptsMs[frameIndex + 1] - ptsMs[frameIndex] : (frameIndex > 0 ? ptsMs[frameIndex] - ptsMs[frameIndex - 1] : 1.0);
                result.Add(new DeepSiftVisualAnchor { Index = frameIndex, FrameIndex = frameIndex, PtsMs = ptsMs[frameIndex], DurationMs = durationMs, FrameDurationMs = durationMs, Frame = frames[frameIndex], Width = this._vsConfig.FrameWidth, Height = this._vsConfig.FrameHeight });
            }
            return result;
        }

        private FrameFeatureBatchMatcherBase CreateMatcher()
        {
            if (string.Equals(this._speedConfig.SiftBackend, "vulkan", StringComparison.OrdinalIgnoreCase))
                return new VulkanSiftBatchMatcher();
            if (string.IsNullOrEmpty(this._speedConfig.SiftBackend) || string.Equals(this._speedConfig.SiftBackend, "cpu", StringComparison.OrdinalIgnoreCase))
                return new OpenCvSiftBatchMatcher();
            throw new InvalidOperationException("Backend SIFT SpeedCorrection non supportato: " + this._speedConfig.SiftBackend);
        }

        /// <summary>
        /// Accumula le corrispondenze appartenenti alla stessa banda di offset
        /// </summary>
        private sealed class OffsetCluster
        {
            private readonly HashSet<int> _sourceIndexes = new HashSet<int>();
            private readonly HashSet<int> _languageIndexes = new HashSet<int>();
            private readonly List<double> _offsets = new List<double>();
            private double _minimumSourcePtsMs = double.PositiveInfinity;
            private double _maximumSourcePtsMs = double.NegativeInfinity;
            private double _score;

            /// <summary>
            /// Aggiunge una corrispondenza visuale al cluster
            /// </summary>
            /// <param name="pair">Corrispondenza visuale accettata</param>
            /// <param name="offsetMs">Offset calcolato per la scala nota</param>
            public void Add(DeepSiftAcceptedPairDiagnostic pair, double offsetMs)
            {
                this._sourceIndexes.Add(pair.SourceAnchorIndex);
                this._languageIndexes.Add(pair.LanguageAnchorIndex);
                this._offsets.Add(offsetMs);
                this._minimumSourcePtsMs = Math.Min(this._minimumSourcePtsMs, pair.SourcePtsMs);
                this._maximumSourcePtsMs = Math.Max(this._maximumSourcePtsMs, pair.SourcePtsMs);
                this._score += pair.Score;
            }

            /// <summary>
            /// Confronta supporto, copertura temporale e qualità visuale
            /// </summary>
            /// <param name="other">Cluster corrente migliore</param>
            /// <returns>True se questa istanza deve sostituire il cluster corrente</returns>
            public bool IsBetterThan(OffsetCluster other)
            {
                int comparison;
                if (this.SourceCount != other.SourceCount)
                    return this.SourceCount > other.SourceCount;
                if (this.LanguageCount != other.LanguageCount)
                    return this.LanguageCount > other.LanguageCount;
                comparison = this.SourceSpanMs.CompareTo(other.SourceSpanMs);
                if (comparison != 0)
                    return comparison > 0;
                comparison = this.MedianResidualMs.CompareTo(other.MedianResidualMs);
                if (comparison != 0)
                    return comparison < 0;
                return this._score > other._score;
            }

            /// <summary>
            /// Numero di frame sorgente distinti
            /// </summary>
            public int SourceCount { get { return this._sourceIndexes.Count; } }

            /// <summary>
            /// Numero di frame lingua distinti
            /// </summary>
            public int LanguageCount { get { return this._languageIndexes.Count; } }

            /// <summary>
            /// Copertura temporale sorgente in millisecondi
            /// </summary>
            public double SourceSpanMs { get { return this._maximumSourcePtsMs - this._minimumSourcePtsMs; } }

            /// <summary>
            /// Mediana degli offset del cluster
            /// </summary>
            public double MedianOffsetMs
            {
                get
                {
                    this._offsets.Sort();
                    return Median(this._offsets);
                }
            }

            /// <summary>
            /// Mediana degli scarti assoluti dall'offset centrale
            /// </summary>
            public double MedianResidualMs
            {
                get
                {
                    double median = this.MedianOffsetMs;
                    List<double> residuals = new List<double>(this._offsets.Count);
                    for (int offsetIndex = 0; offsetIndex < this._offsets.Count; offsetIndex++)
                        residuals.Add(Math.Abs(this._offsets[offsetIndex] - median));
                    residuals.Sort();
                    return Median(residuals);
                }
            }

            /// <summary>
            /// Calcola la mediana di una lista già ordinata
            /// </summary>
            /// <param name="values">Valori ordinati</param>
            /// <returns>Mediana dei valori</returns>
            private static double Median(List<double> values)
            {
                int middle = values.Count / 2;
                return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) * 0.5;
            }
        }

        public int InitialDelayMs { get { return this._initialDelayMs; } }
        public string StretchFactor { get { return this._stretchFactor; } }
        public int SyncDelayMs { get { return this._syncDelayMs; } }
        public long ExecutionTimeMs { get { return this._executionTimeMs; } }
    }
}
