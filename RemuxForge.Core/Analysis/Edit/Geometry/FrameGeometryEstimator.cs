using OpenCvSharp;
using RemuxForge.Core.Analysis.Edit.Extraction;
using RemuxForge.Core.Analysis.Features;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Media.Ffmpeg;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;

namespace RemuxForge.Core.Analysis.Edit.Geometry
{
    /// <summary>
    /// Risultato completo del bootstrap geometrico condiviso
    /// </summary>
    internal class FrameGeometryEstimationResult
    {
        #region Costruttore

        /// <summary>
        /// Inizializza il risultato non conclusivo
        /// </summary>
        public FrameGeometryEstimationResult()
        {
            this.Alignment = new VisualGeometryAlignment();
            this.SourceCommonGeometry = new FrameGeometry();
            this.LanguageCommonGeometry = new FrameGeometry();
            this.AffineLanguageDHashGeometry = new FrameGeometry();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Trasformazione geometrica fra le due copie
        /// </summary>
        public VisualGeometryAlignment Alignment { get; set; }

        /// <summary>
        /// Geometria video nativa source
        /// </summary>
        public FrameSyncGeometryInfo SourceGeometryInfo { get; set; }

        /// <summary>
        /// Geometria video nativa language
        /// </summary>
        public FrameSyncGeometryInfo LanguageGeometryInfo { get; set; }

        /// <summary>
        /// Crop comune applicato dall'estrattore source
        /// </summary>
        public FrameGeometry SourceCommonGeometry { get; set; }

        /// <summary>
        /// Crop comune applicato dall'estrattore language
        /// </summary>
        public FrameGeometry LanguageCommonGeometry { get; set; }

        /// <summary>
        /// Viewport language proiettato nell'area dHash della sorgente
        /// </summary>
        public FrameGeometry AffineLanguageDHashGeometry { get; set; }

        #endregion
    }

    /// <summary>
    /// Stima crop nativi e trasformazione globale prima della ricerca temporale
    /// </summary>
    internal class FrameGeometryEstimator
    {
        #region Costanti

        /// <summary>
        /// Lato dei frame normalizzati consegnati ai backend SIFT
        /// </summary>
        private const int SIFT_SIDE = 512;

        /// <summary>
        /// Lato dei frame usati dall'affinamento sui pixel
        /// </summary>
        private const int REFINE_SIDE = 256;

        /// <summary>
        /// Durata massima del bootstrap temporale iniziale
        /// </summary>
        private const double BOOTSTRAP_SECONDS = 180.0;

        /// <summary>
        /// Intervallo fra due campioni SIFT iniziali
        /// </summary>
        private const double BOOTSTRAP_INTERVAL_SECONDS = 3.0;

        /// <summary>
        /// Numero minimo di match geometrici indipendenti
        /// </summary>
        private const int REQUIRED_MATCHES = 5;

        /// <summary>
        /// Numero di posizioni distribuite usate dal crop nativo
        /// </summary>
        private const int CROP_SAMPLE_COUNT = 24;

        /// <summary>
        /// Luminanza massima ammessa sul bordo nero
        /// </summary>
        private const double BLACK_BORDER_MAX = 32.0;

        /// <summary>
        /// Luminanza minima richiesta all'area attiva
        /// </summary>
        private const double ACTIVE_MINIMUM = 48.0;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso ffmpeg
        /// </summary>
        private readonly string _ffmpegPath;

        /// <summary>
        /// Configurazione ffmpeg corrente
        /// </summary>
        private readonly FfmpegConfig _ffmpegConfig;

        /// <summary>
        /// Backend visuale scelto dall'utente
        /// </summary>
        private readonly VisionBackendKind _backend;

        /// <summary>
        /// Sezione di log del chiamante
        /// </summary>
        private readonly LogSection _logSection;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza il bootstrap con backend esplicito
        /// </summary>
        /// <param name="ffmpegPath">Percorso ffmpeg</param>
        /// <param name="ffmpegConfig">Configurazione ffmpeg</param>
        /// <param name="backend">Backend visuale richiesto</param>
        /// <param name="logSection">Sezione di log</param>
        public FrameGeometryEstimator(string ffmpegPath, FfmpegConfig ffmpegConfig, VisionBackendKind backend, LogSection logSection)
        {
            this._ffmpegPath = ffmpegPath ?? "";
            this._ffmpegConfig = ffmpegConfig ?? new FfmpegConfig();
            this._backend = backend;
            this._logSection = logSection;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Esegue crop indipendente, matching iniziale e consenso geometrico
        /// </summary>
        /// <param name="sourceFile">File source</param>
        /// <param name="languageFile">File language</param>
        /// <param name="sourceManualCropPx">Crop manuale source</param>
        /// <param name="languageManualCropPx">Crop manuale language</param>
        /// <param name="sourceDurationMs">Durata source nota</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Risultato completo del bootstrap</returns>
        public FrameGeometryEstimationResult Estimate(string sourceFile, string languageFile, string sourceManualCropPx, string languageManualCropPx, int sourceDurationMs, CancellationToken cancellationToken)
        {
            return this.Estimate(sourceFile, languageFile, sourceManualCropPx, languageManualCropPx, sourceDurationMs, null, null, cancellationToken);
        }

        /// <summary>
        /// Esegue il bootstrap e verifica sulle coppie SIFT se l'affine ripara il contratto dHash
        /// </summary>
        /// <param name="sourceFile">File source</param>
        /// <param name="languageFile">File language</param>
        /// <param name="sourceManualCropPx">Crop manuale source</param>
        /// <param name="languageManualCropPx">Crop manuale language</param>
        /// <param name="sourceDurationMs">Durata source nota</param>
        /// <param name="sourceDHashGeometry">Viewport dHash source già calibrato</param>
        /// <param name="languageDHashGeometry">Viewport dHash language già calibrato</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Risultato completo del bootstrap</returns>
        public FrameGeometryEstimationResult Estimate(string sourceFile, string languageFile, string sourceManualCropPx, string languageManualCropPx, int sourceDurationMs, FrameGeometry sourceDHashGeometry, FrameGeometry languageDHashGeometry, CancellationToken cancellationToken)
        {
            FrameGeometryEstimationResult result = new FrameGeometryEstimationResult();
            result.Alignment.RequiredMatchCount = REQUIRED_MATCHES;
            result.Alignment.BackendName = AdvancedConfig.GetVisionBackendValue(this._backend);
            ConsoleHelper.Write(this._logSection, RemuxForge.Core.Models.LogLevel.Phase, AppText.T("analysis.geometry.bootstrap"));

            VideoGeometryAnalyzer analyzer = new VideoGeometryAnalyzer(this._ffmpegPath, this._ffmpegConfig, this._logSection);
            VideoGeometryProfile sourceProfile = analyzer.Analyze(sourceFile);
            VideoGeometryProfile languageProfile = analyzer.Analyze(languageFile);
            if (sourceProfile == null || languageProfile == null)
                return this.Reject(result, "Geometria video nativa non disponibile");

            FfmpegVideoInfoReader reader = new FfmpegVideoInfoReader(this._ffmpegPath, this._ffmpegConfig, this._logSection);
            reader.TryRead(languageFile, out int languageDurationMs, out _);
            if (sourceDurationMs <= 0 || languageDurationMs <= 0)
                return this.Reject(result, "Durata video non disponibile per il bootstrap geometrico");

            cancellationToken.ThrowIfCancellationRequested();
            if (!this.TryResolveActiveRect(sourceFile, sourceProfile, sourceDurationMs, sourceManualCropPx, cancellationToken, out PixelRect sourceActive, out CropDetectionDiagnostics sourceCropDiagnostics, out string sourceMode, out string cropRejectReason))
                return this.Reject(result, cropRejectReason);
            if (!this.TryResolveActiveRect(languageFile, languageProfile, languageDurationMs, languageManualCropPx, cancellationToken, out PixelRect languageActive, out CropDetectionDiagnostics languageCropDiagnostics, out string languageMode, out cropRejectReason))
                return this.Reject(result, cropRejectReason);

            result.SourceGeometryInfo = this.BuildGeometryInfo(sourceProfile, sourceActive, sourceCropDiagnostics, sourceManualCropPx, sourceMode);
            result.LanguageGeometryInfo = this.BuildGeometryInfo(languageProfile, languageActive, languageCropDiagnostics, languageManualCropPx, languageMode);

            List<DeepSiftVisualAnchor> sourceAnchors = this.ExtractBootstrapAnchors(sourceFile, sourceDurationMs, sourceProfile, sourceActive, cancellationToken);
            List<DeepSiftVisualAnchor> languageAnchors = this.ExtractBootstrapAnchors(languageFile, languageDurationMs, languageProfile, languageActive, cancellationToken);
            if (sourceAnchors.Count < REQUIRED_MATCHES || languageAnchors.Count < REQUIRED_MATCHES)
                return this.Reject(result, "Frame informativi insufficienti nei primi tre minuti");

            FrameFeatureMatcherOptions matcherOptions = new FrameFeatureMatcherOptions();
            matcherOptions.MaxFeatures = 2400;
            matcherOptions.ContrastThreshold = 0.03;
            matcherOptions.EdgeThreshold = 12.0;
            matcherOptions.MinInliers = 8;
            matcherOptions.MinCoverage = 0.16;
            using (FrameFeatureBatchMatcherBase matcher = FrameFeatureBatchMatcherBase.Create(this._backend, matcherOptions))
            {
                if (!matcher.IsAvailable(out string backendRejectReason))
                    return this.Reject(result, backendRejectReason);

                DeepSiftBatchMatchResult batch = matcher.BuildMatrix(sourceAnchors, languageAnchors, ParallelismHelper.ResolveDefaultMaxDegree(), cancellationToken);
                result.Alignment.BackendName = batch.BackendName;
                result.Alignment.ProcessedPairCount = batch.ProcessedCellCount;
                result.Alignment.UploadMs = batch.UploadMs;
                result.Alignment.ReadbackMs = batch.ReadbackMs;
                if (batch.Cancelled)
                    cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(batch.RejectReason))
                    return this.Reject(result, batch.RejectReason);
                List<GeometryCandidate> candidates = this.BuildCandidates(batch.AcceptedPairs, sourceAnchors, languageAnchors);
                if (!this.TryBuildConsensus(candidates, out GeometryConsensus consensus))
                    return this.Reject(result, "Meno di cinque match SIFT/RANSAC geometricamente concordi nei primi tre minuti");

                result.Alignment.PixelScore = this.RefineConsensus(sourceAnchors, languageAnchors, consensus);
                this.PopulateAlignment(result.Alignment, consensus, sourceProfile, languageProfile, sourceActive, languageActive);
                result.SourceCommonGeometry.CropPx = result.Alignment.SourceCommonCropPx;
                result.LanguageCommonGeometry.CropPx = result.Alignment.LanguageCommonCropPx;
                if (sourceDHashGeometry != null && languageDHashGeometry != null)
                    this.ValidateDHashContract(result, sourceProfile, languageProfile, sourceActive, languageActive, sourceDHashGeometry, languageDHashGeometry, sourceAnchors, languageAnchors, consensus);
            }

            result.Alignment.Success = true;
            ConsoleHelper.Write(this._logSection, RemuxForge.Core.Models.LogLevel.Debug, AppText.F("analysis.geometry.result",
                result.Alignment.AcceptedMatchCount,
                result.Alignment.SourceCommonCropPx,
                result.Alignment.LanguageCommonCropPx,
                result.Alignment.ScaleX,
                result.Alignment.ScaleY,
                result.Alignment.TranslateX,
                result.Alignment.TranslateY,
                result.Alignment.BackendName));
            return result;
        }

        #endregion

        #region Metodi privati - Crop

        /// <summary>
        /// Risolve il rettangolo attivo da crop manuale o campioni nativi distribuiti
        /// </summary>
        private bool TryResolveActiveRect(string filePath, VideoGeometryProfile profile, int durationMs, string manualCropPx, CancellationToken cancellationToken, out PixelRect rect, out CropDetectionDiagnostics diagnostics, out string mode, out string rejectReason)
        {
            rect = new PixelRect(0, 0, profile.Width, profile.Height);
            diagnostics = new CropDetectionDiagnostics();
            mode = "none";
            rejectReason = "";
            string normalizedManualCrop = Options.NormalizeAnalysisCropPx(manualCropPx);
            if (!string.IsNullOrEmpty(normalizedManualCrop))
            {
                if (!Options.TryParseAnalysisCropPx(normalizedManualCrop, out int left, out int right, out int top, out int bottom))
                {
                    rejectReason = "Crop manuale non valido";
                    return false;
                }
                rect = new PixelRect(left, top, profile.Width - right, profile.Height - bottom);
                mode = "manual_analysis_crop";
                return rect.IsValid;
            }

            List<byte[]> frames = new List<byte[]>();
            for (int i = 0; i < CROP_SAMPLE_COUNT; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double fraction = (i + 1.0) / (CROP_SAMPLE_COUNT + 1.0);
                byte[] frame = this.ExtractNativeFrame(filePath, durationMs * fraction, profile.Width, profile.Height);
                if (frame != null)
                    frames.Add(frame);
            }
            frames = this.SelectInformativeFrames(frames);
            if (frames.Count < 4)
            {
                rejectReason = "Campioni non neri insufficienti per il crop geometrico";
                return false;
            }

            rect = this.DetectActiveRect(frames, profile.Width, profile.Height, out diagnostics);
            if (!rect.IsValid || rect.Width < profile.Width / 3 || rect.Height < profile.Height / 3)
            {
                rejectReason = "Area attiva rilevata non valida";
                return false;
            }
            mode = rect.Left > 0 || rect.Top > 0 || rect.Right < profile.Width || rect.Bottom < profile.Height ? "black_border_autocrop" : "none";
            return true;
        }

        /// <summary>
        /// Estrae un singolo frame grayscale alla risoluzione storage nativa
        /// </summary>
        private byte[] ExtractNativeFrame(string filePath, double ptsMs, int width, int height)
        {
            int expectedBytes = checked(width * height);
            int attempts = this._ffmpegConfig.HardwareAcceleration ? 2 : 1;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                List<string> arguments = new List<string>();
                arguments.Add("-nostdin");
                arguments.Add("-v");
                arguments.Add("error");
                if (attempt == 0 && attempts > 1 && FfmpegConfig.IsValidHardwareAccelerationMethod(this._ffmpegConfig.HardwareAccelerationMethod))
                {
                    arguments.Add("-hwaccel");
                    arguments.Add(this._ffmpegConfig.HardwareAccelerationMethod);
                }
                arguments.Add("-ss");
                arguments.Add((Math.Max(0.0, ptsMs) / 1000.0).ToString("0.######", CultureInfo.InvariantCulture));
                arguments.Add("-i");
                arguments.Add(filePath);
                arguments.Add("-an");
                arguments.Add("-sn");
                arguments.Add("-dn");
                arguments.Add("-map");
                arguments.Add("0:v:0");
                arguments.Add("-frames:v");
                arguments.Add("1");
                arguments.Add("-vf");
                arguments.Add("format=gray");
                arguments.Add("-f");
                arguments.Add("rawvideo");
                arguments.Add("-");

                NativeFrameCollector collector = new NativeFrameCollector(expectedBytes);
                ProcessBinaryResult run = ProcessRunner.RunBinaryStdout(this._ffmpegPath, arguments.ToArray(), collector.Append, this._ffmpegConfig.FrameExtractionTimeoutMs);
                if (run.ExitCode == 0 && collector.IsComplete)
                    return collector.Frame;
            }
            return null;
        }

        /// <summary>
        /// Verifica che il frame contenga contrasto e luminanza utili
        /// </summary>
        private List<byte[]> SelectInformativeFrames(List<byte[]> frames)
        {
            List<double> deviations = new List<double>(frames.Count);
            List<int> spreads = new List<int>(frames.Count);
            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                byte[] frame = frames[frameIndex];
                int[] histogram = new int[256];
                double sum = 0.0;
                double squares = 0.0;
                for (int i = 0; i < frame.Length; i++)
                {
                    int value = frame[i];
                    histogram[value]++;
                    sum += value;
                    squares += value * value;
                }
                double mean = sum / frame.Length;
                deviations.Add(Math.Sqrt(Math.Max(0.0, squares / frame.Length - mean * mean)));
                spreads.Add(this.HistogramPercentile(histogram, frame.Length, 0.95) - this.HistogramPercentile(histogram, frame.Length, 0.05));
            }
            if (frames.Count == 0)
                return frames;

            List<double> sortedDeviations = new List<double>(deviations);
            sortedDeviations.Sort();
            double floor = this.Quantile(sortedDeviations, 0.35);
            List<byte[]> selected = new List<byte[]>();
            for (int i = 0; i < frames.Count; i++)
            {
                if (spreads[i] >= ACTIVE_MINIMUM && deviations[i] >= Math.Max(3.0, floor))
                    selected.Add(frames[i]);
            }
            return selected.Count >= 4 ? selected : frames;
        }

        /// <summary>
        /// Verifica che un frame normalizzato contenga contrasto sufficiente per SIFT
        /// </summary>
        private bool IsInformative(byte[] frame)
        {
            int[] histogram = new int[256];
            double sum = 0.0;
            double squares = 0.0;
            for (int i = 0; i < frame.Length; i++)
            {
                int value = frame[i];
                histogram[value]++;
                sum += value;
                squares += value * value;
            }
            double mean = sum / frame.Length;
            double deviation = Math.Sqrt(Math.Max(0.0, squares / frame.Length - mean * mean));
            int low = this.HistogramPercentile(histogram, frame.Length, 0.05);
            int high = this.HistogramPercentile(histogram, frame.Length, 0.95);
            return high - low >= ACTIVE_MINIMUM && deviation >= 3.0;
        }

        /// <summary>
        /// Costruisce i profili di luminanza al percentile 99 e trova i quattro bordi
        /// </summary>
        private PixelRect DetectActiveRect(List<byte[]> frames, int width, int height, out CropDetectionDiagnostics diagnostics)
        {
            int[] columnHistograms = new int[checked(width * 256)];
            int[] rowHistograms = new int[checked(height * 256)];
            List<double> leftValues = new List<double>(frames.Count);
            List<double> rightValues = new List<double>(frames.Count);
            List<double> topValues = new List<double>(frames.Count);
            List<double> bottomValues = new List<double>(frames.Count);
            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                Array.Clear(columnHistograms, 0, columnHistograms.Length);
                Array.Clear(rowHistograms, 0, rowHistograms.Length);
                byte[] frame = frames[frameIndex];
                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    int rowHistogram = y * 256;
                    for (int x = 0; x < width; x++)
                    {
                        int value = frame[row + x];
                        columnHistograms[x * 256 + value]++;
                        rowHistograms[rowHistogram + value]++;
                    }
                }
                double[] columns = this.BuildPercentileProfile(columnHistograms, width, height, 0.99);
                double[] rows = this.BuildPercentileProfile(rowHistograms, height, width, 0.99);
                leftValues.Add(this.FindBorder(columns, false));
                rightValues.Add(this.FindBorder(columns, true));
                topValues.Add(this.FindBorder(rows, false));
                bottomValues.Add(this.FindBorder(rows, true));
            }

            int left = (int)Math.Round(this.Median(leftValues));
            int right = (int)Math.Round(this.Median(rightValues));
            int top = (int)Math.Round(this.Median(topValues));
            int bottom = (int)Math.Round(this.Median(bottomValues));
            diagnostics = new CropDetectionDiagnostics();
            diagnostics.SampleCount = frames.Count;
            diagnostics.LeftDispersionPx = this.Mad(leftValues, left);
            diagnostics.RightDispersionPx = this.Mad(rightValues, right);
            diagnostics.TopDispersionPx = this.Mad(topValues, top);
            diagnostics.BottomDispersionPx = this.Mad(bottomValues, bottom);
            return new PixelRect(left, top, width - right, height - bottom);
        }

        /// <summary>
        /// Converte istogrammi contigui in un profilo percentile
        /// </summary>
        private double[] BuildPercentileProfile(int[] histograms, int count, int samplesPerEntry, double percentile)
        {
            double[] result = new double[count];
            int rank = Math.Max(0, (int)Math.Ceiling(samplesPerEntry * percentile) - 1);
            for (int entry = 0; entry < count; entry++)
            {
                int cumulative = 0;
                int origin = entry * 256;
                for (int value = 0; value < 256; value++)
                {
                    cumulative += histograms[origin + value];
                    if (cumulative > rank)
                    {
                        result[entry] = value;
                        break;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Trova il primo run attivo partendo da uno dei due estremi
        /// </summary>
        private int FindBorder(double[] profile, bool reverse)
        {
            int length = profile.Length;
            int edgeCount = Math.Max(2, length / 100);
            List<double> edgeValues = new List<double>(edgeCount);
            for (int i = 0; i < edgeCount; i++)
                edgeValues.Add(profile[reverse ? length - 1 - i : i]);
            List<double> middleValues = new List<double>();
            for (int i = length * 3 / 8; i < length * 5 / 8; i++)
                middleValues.Add(profile[i]);
            double edge = this.Median(edgeValues);
            double middle = this.Median(middleValues);
            if (edge > BLACK_BORDER_MAX || middle < ACTIVE_MINIMUM)
                return 0;

            double threshold = Math.Max(ACTIVE_MINIMUM, Math.Max(edge + 8.0, middle * 0.25));
            int run = Math.Max(3, length / 500);
            int consecutive = 0;
            for (int offset = 0; offset < length; offset++)
            {
                int index = reverse ? length - 1 - offset : offset;
                if (profile[index] > threshold)
                    consecutive++;
                else
                    consecutive = 0;
                if (consecutive >= run)
                {
                    int position = offset - run + 1;
                    return position <= length * 0.45 ? position : 0;
                }
            }
            return 0;
        }

        #endregion

        #region Metodi privati - Bootstrap

        /// <summary>
        /// Estrae e filtra i frame informativi dei primi tre minuti
        /// </summary>
        private List<DeepSiftVisualAnchor> ExtractBootstrapAnchors(string filePath, int durationMs, VideoGeometryProfile profile, PixelRect active, CancellationToken cancellationToken)
        {
            VideoSyncConfig config = new VideoSyncConfig();
            config.FrameWidth = SIFT_SIDE;
            config.FrameHeight = SIFT_SIDE;
            FrameExtractionService extractor = new FrameExtractionService(this._ffmpegPath, config, this._ffmpegConfig, this._logSection);
            double durationSeconds = Math.Min(BOOTSTRAP_SECONDS, durationMs / 1000.0);
            extractor.ExtractSegmentAtInterval(filePath, 0, durationSeconds, BOOTSTRAP_INTERVAL_SECONDS, false, this.FormatCrop(active, profile.Width, profile.Height), out List<byte[]> frames, out double[] timestampsMs);

            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>();
            List<ulong> recentHashes = new List<ulong>();
            int count = Math.Min(frames.Count, timestampsMs.Length);
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!this.IsInformative(frames[i]))
                    continue;
                DeepSiftVisualAnchor anchor = new DeepSiftVisualAnchor();
                anchor.Index = result.Count;
                anchor.FrameIndex = i;
                anchor.PtsMs = timestampsMs[i];
                anchor.DurationMs = BOOTSTRAP_INTERVAL_SECONDS * 1000.0;
                anchor.FrameDurationMs = BOOTSTRAP_INTERVAL_SECONDS * 1000.0;
                anchor.Frame = frames[i];
                anchor.Width = SIFT_SIDE;
                anchor.Height = SIFT_SIDE;
                ulong hash = this.ComputeDHash(anchor);
                bool duplicate = recentHashes.Any(value => BitOperations.PopCount(value ^ hash) <= 4);
                if (duplicate)
                    continue;
                recentHashes.Add(hash);
                if (recentHashes.Count > 2)
                    recentHashes.RemoveAt(0);
                result.Add(anchor);
            }
            return result;
        }

        /// <summary>
        /// Converte le omografie accettate in trasformazioni axis-aligned language-source
        /// </summary>
        private List<GeometryCandidate> BuildCandidates(List<DeepSiftAcceptedPairDiagnostic> pairs, List<DeepSiftVisualAnchor> sourceAnchors, List<DeepSiftVisualAnchor> languageAnchors)
        {
            List<GeometryCandidate> result = new List<GeometryCandidate>();
            if (pairs == null)
                return result;
            for (int i = 0; i < pairs.Count; i++)
            {
                if (!this.TryConvertHomography(pairs[i].Homography, out GeometryCandidate candidate))
                    continue;
                candidate.SourceIndex = pairs[i].SourceAnchorIndex;
                candidate.LanguageIndex = pairs[i].LanguageAnchorIndex;
                candidate.SourcePtsMs = pairs[i].SourcePtsMs;
                candidate.LanguagePtsMs = pairs[i].LanguagePtsMs;
                candidate.SourceHash = this.ComputeDHash(sourceAnchors[candidate.SourceIndex]);
                candidate.LanguageHash = this.ComputeDHash(languageAnchors[candidate.LanguageIndex]);
                candidate.Score = pairs[i].Score;
                result.Add(candidate);
            }
            return result;
        }

        /// <summary>
        /// Inverte l'omografia source-language e ne conserva la componente axis-aligned
        /// </summary>
        private bool TryConvertHomography(double[] homography, out GeometryCandidate result)
        {
            result = null;
            if (!this.TryInvert3x3(homography, out double[] inverse) || Math.Abs(inverse[8]) < 1.0e-12)
                return false;
            for (int i = 0; i < inverse.Length; i++)
                inverse[i] /= inverse[8];

            double perspective = Math.Max(Math.Abs(inverse[6] * SIFT_SIDE), Math.Abs(inverse[7] * SIFT_SIDE));
            if (perspective > 0.03 || !this.TryApproximateAffine(inverse, out double scaleX, out double shearX, out double translateX, out double shearY, out double scaleY, out double translateY))
                return false;
            if (scaleX < 0.65 || scaleX > 1.50 || scaleY < 0.65 || scaleY > 1.50 || Math.Abs(shearX) > 0.08 || Math.Abs(shearY) > 0.08)
                return false;
            if (Math.Abs(translateX) > SIFT_SIDE * 0.35 || Math.Abs(translateY) > SIFT_SIDE * 0.35)
                return false;

            result = new GeometryCandidate();
            result.ScaleX = scaleX;
            result.ScaleY = scaleY;
            result.TranslateX = translateX / SIFT_SIDE;
            result.TranslateY = translateY / SIFT_SIDE;
            return true;
        }

        /// <summary>
        /// Approssima l'omografia con un affine sull'intero canvas per non leggere la scala locale all'origine
        /// </summary>
        private bool TryApproximateAffine(double[] homography, out double scaleX, out double shearX, out double translateX, out double shearY, out double scaleY, out double translateY)
        {
            scaleX = 0.0;
            shearX = 0.0;
            translateX = 0.0;
            shearY = 0.0;
            scaleY = 0.0;
            translateY = 0.0;
            const int GRID = 5;
            double meanInput = SIFT_SIDE * 0.5;
            double sumAxisSquares = 0.0;
            double sumOutputX = 0.0;
            double sumOutputY = 0.0;
            double sumDxOutputX = 0.0;
            double sumDyOutputX = 0.0;
            double sumDxOutputY = 0.0;
            double sumDyOutputY = 0.0;
            int count = 0;
            for (int row = 0; row < GRID; row++)
            {
                double y = row * SIFT_SIDE / (double)(GRID - 1);
                double dy = y - meanInput;
                for (int column = 0; column < GRID; column++)
                {
                    double x = column * SIFT_SIDE / (double)(GRID - 1);
                    double dx = x - meanInput;
                    double denominator = homography[6] * x + homography[7] * y + homography[8];
                    if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1.0e-12)
                        return false;
                    double outputX = (homography[0] * x + homography[1] * y + homography[2]) / denominator;
                    double outputY = (homography[3] * x + homography[4] * y + homography[5]) / denominator;
                    if (!double.IsFinite(outputX) || !double.IsFinite(outputY))
                        return false;
                    sumOutputX += outputX;
                    sumOutputY += outputY;
                    sumDxOutputX += dx * outputX;
                    sumDyOutputX += dy * outputX;
                    sumDxOutputY += dx * outputY;
                    sumDyOutputY += dy * outputY;
                    sumAxisSquares += dx * dx;
                    count++;
                }
            }
            if (count == 0 || sumAxisSquares <= 0.0)
                return false;
            scaleX = sumDxOutputX / sumAxisSquares;
            shearX = sumDyOutputX / sumAxisSquares;
            shearY = sumDxOutputY / sumAxisSquares;
            scaleY = sumDyOutputY / sumAxisSquares;
            double meanOutputX = sumOutputX / count;
            double meanOutputY = sumOutputY / count;
            translateX = meanOutputX - scaleX * meanInput - shearX * meanInput;
            translateY = meanOutputY - shearY * meanInput - scaleY * meanInput;
            return true;
        }

        /// <summary>
        /// Trova il cluster geometrico più denso e ne calcola mediana e dispersione
        /// </summary>
        private bool TryBuildConsensus(List<GeometryCandidate> candidates, out GeometryConsensus consensus)
        {
            consensus = null;
            if (candidates == null || candidates.Count < REQUIRED_MATCHES)
                return false;

            List<GeometryCandidate> bestCluster = new List<GeometryCandidate>();
            for (int centerIndex = 0; centerIndex < candidates.Count; centerIndex++)
            {
                List<GeometryCandidate> cluster = new List<GeometryCandidate>();
                GeometryCandidate center = candidates[centerIndex];
                for (int i = 0; i < candidates.Count; i++)
                {
                    GeometryCandidate value = candidates[i];
                    if (Math.Abs(value.ScaleX - center.ScaleX) <= 0.025 && Math.Abs(value.ScaleY - center.ScaleY) <= 0.025 && Math.Abs(value.TranslateX - center.TranslateX) <= 0.025 && Math.Abs(value.TranslateY - center.TranslateY) <= 0.025)
                        cluster.Add(value);
                }
                if (cluster.Count > bestCluster.Count)
                    bestCluster = cluster;
            }
            if (bestCluster.Count < REQUIRED_MATCHES)
                return false;

            bestCluster.Sort((left, right) => right.Score.CompareTo(left.Score));
            HashSet<int> sourceIndexes = new HashSet<int>();
            HashSet<int> languageIndexes = new HashSet<int>();
            List<GeometryCandidate> independent = new List<GeometryCandidate>();
            for (int i = 0; i < bestCluster.Count; i++)
            {
                GeometryCandidate value = bestCluster[i];
                if (sourceIndexes.Contains(value.SourceIndex) || languageIndexes.Contains(value.LanguageIndex))
                    continue;
                if (independent.Any(existing => BitOperations.PopCount(existing.SourceHash ^ value.SourceHash) <= 4 || BitOperations.PopCount(existing.LanguageHash ^ value.LanguageHash) <= 4))
                    continue;
                sourceIndexes.Add(value.SourceIndex);
                languageIndexes.Add(value.LanguageIndex);
                independent.Add(value);
            }
            if (independent.Count < REQUIRED_MATCHES)
                return false;

            consensus = new GeometryConsensus();
            consensus.Candidates = independent;
            consensus.ScaleX = this.Median(independent.Select(x => x.ScaleX).ToList());
            consensus.ScaleY = this.Median(independent.Select(x => x.ScaleY).ToList());
            consensus.TranslateX = this.Median(independent.Select(x => x.TranslateX).ToList());
            consensus.TranslateY = this.Median(independent.Select(x => x.TranslateY).ToList());
            consensus.ScaleXDispersion = this.Mad(independent.Select(x => x.ScaleX).ToList(), consensus.ScaleX);
            consensus.ScaleYDispersion = this.Mad(independent.Select(x => x.ScaleY).ToList(), consensus.ScaleY);
            consensus.TranslateXDispersion = this.Mad(independent.Select(x => x.TranslateX).ToList(), consensus.TranslateX);
            consensus.TranslateYDispersion = this.Mad(independent.Select(x => x.TranslateY).ToList(), consensus.TranslateY);
            return true;
        }

        /// <summary>
        /// Affina i quattro parametri globali massimizzando la correlazione dei pixel e dei gradienti
        /// </summary>
        private double RefineConsensus(List<DeepSiftVisualAnchor> sourceAnchors, List<DeepSiftVisualAnchor> languageAnchors, GeometryConsensus consensus)
        {
            List<RefinementPair> allPairs = new List<RefinementPair>();
            try
            {
                for (int i = 0; i < consensus.Candidates.Count; i++)
                {
                    GeometryCandidate candidate = consensus.Candidates[i];
                    allPairs.Add(new RefinementPair(sourceAnchors[candidate.SourceIndex], languageAnchors[candidate.LanguageIndex]));
                }
                if (allPairs.Count == 0)
                    return -1.0;

                double[] parameters = new double[]
                {
                    consensus.ScaleX,
                    consensus.ScaleY,
                    consensus.TranslateX * REFINE_SIDE,
                    consensus.TranslateY * REFINE_SIDE
                };
                for (int i = 0; i < allPairs.Count; i++)
                    allPairs[i].InitialQuality = this.PixelQuality(new List<RefinementPair> { allPairs[i] }, parameters);
                allPairs.Sort((left, right) => right.InitialQuality.CompareTo(left.InitialQuality));
                int trainingCount = Math.Min(8, Math.Max(3, (allPairs.Count + 1) / 2));
                List<RefinementPair> pairs = allPairs.Take(trainingCount).ToList();
                double[] start = (double[])parameters.Clone();
                double[] limits = new double[] { 0.035, 0.035, 6.0, 6.0 };
                double best = this.PixelQuality(pairs, parameters);
                double[,] steps = new double[,]
                {
                    { 0.004, 0.004, 1.0, 1.0 },
                    { 0.002, 0.002, 0.5, 0.5 },
                    { 0.001, 0.001, 0.25, 0.25 },
                    { 0.0005, 0.0005, 0.125, 0.125 },
                    { 0.00025, 0.00025, 0.0625, 0.0625 }
                };
                for (int level = 0; level < steps.GetLength(0); level++)
                {
                    bool moved;
                    do
                    {
                        moved = false;
                        for (int parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                        {
                            for (int direction = -1; direction <= 1; direction += 2)
                            {
                                double[] candidate = (double[])parameters.Clone();
                                candidate[parameterIndex] = Math.Clamp(candidate[parameterIndex] + direction * steps[level, parameterIndex], start[parameterIndex] - limits[parameterIndex], start[parameterIndex] + limits[parameterIndex]);
                                double quality = this.PixelQuality(pairs, candidate);
                                if (quality > best + 1.0e-6)
                                {
                                    parameters = candidate;
                                    best = quality;
                                    moved = true;
                                }
                            }
                        }
                    }
                    while (moved);
                }

                consensus.ScaleX = parameters[0];
                consensus.ScaleY = parameters[1];
                consensus.TranslateX = parameters[2] / REFINE_SIDE;
                consensus.TranslateY = parameters[3] / REFINE_SIDE;
                return best;
            }
            finally
            {
                for (int i = 0; i < allPairs.Count; i++)
                    allPairs[i].Dispose();
            }
        }

        /// <summary>
        /// Misura la qualità mediana di una trasformazione sulle coppie di training
        /// </summary>
        private double PixelQuality(List<RefinementPair> pairs, double[] parameters)
        {
            using (Mat transform = new Mat(2, 3, MatType.CV_64FC1))
            {
                transform.Set(0, 0, parameters[0]);
                transform.Set(0, 1, 0.0);
                transform.Set(0, 2, parameters[2]);
                transform.Set(1, 0, 0.0);
                transform.Set(1, 1, parameters[1]);
                transform.Set(1, 2, parameters[3]);
                List<double> qualities = new List<double>();
                for (int i = 0; i < pairs.Count; i++)
                {
                    using (Mat warped = new Mat())
                    using (Mat mask = new Mat())
                    using (Mat gradientX = new Mat())
                    using (Mat gradientY = new Mat())
                    {
                        Cv2.WarpAffine(pairs[i].Language, warped, transform, new Size(REFINE_SIDE, REFINE_SIDE), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);
                        Cv2.WarpAffine(pairs[i].Valid, mask, transform, new Size(REFINE_SIDE, REFINE_SIDE), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.Black);
                        Cv2.Sobel(warped, gradientX, MatType.CV_32FC1, 1, 0, 3);
                        Cv2.Sobel(warped, gradientY, MatType.CV_32FC1, 0, 1, 3);
                        double quality = this.TileCorrelation(pairs[i], warped, mask, gradientX, gradientY, out int tiles);
                        if (tiles >= 8)
                            qualities.Add(quality);
                    }
                }
                return qualities.Count > 0 ? this.Median(qualities) : -1.0;
            }
        }

        /// <summary>
        /// Calcola NCC locale combinata di luminanza e gradiente su una griglia 6x4
        /// </summary>
        private double TileCorrelation(RefinementPair pair, Mat warped, Mat mask, Mat gradientX, Mat gradientY, out int validTiles)
        {
            warped.GetArray(out byte[] warpedValues);
            mask.GetArray(out byte[] maskValues);
            gradientX.GetArray(out float[] gradientXValues);
            gradientY.GetArray(out float[] gradientYValues);
            List<double> luminanceScores = new List<double>();
            List<double> gradientScores = new List<double>();
            for (int row = 0; row < 4; row++)
            {
                int y0 = row * REFINE_SIDE / 4;
                int y1 = (row + 1) * REFINE_SIDE / 4;
                for (int column = 0; column < 6; column++)
                {
                    int x0 = column * REFINE_SIDE / 6;
                    int x1 = (column + 1) * REFINE_SIDE / 6;
                    int area = (x1 - x0) * (y1 - y0);
                    int count = 0;
                    double sourceSum = 0.0;
                    double languageSum = 0.0;
                    for (int y = y0; y < y1; y++)
                    {
                        int origin = y * REFINE_SIDE;
                        for (int x = x0; x < x1; x++)
                        {
                            int index = origin + x;
                            if (maskValues[index] == 0 || x < 4 || y < 4 || x >= REFINE_SIDE - 4 || y >= REFINE_SIDE - 4)
                                continue;
                            count++;
                            sourceSum += pair.SourceValues[index];
                            languageSum += warpedValues[index];
                        }
                    }
                    if (count < area * 0.70)
                        continue;

                    double sourceMean = sourceSum / count;
                    double languageMean = languageSum / count;
                    double luminanceDot = 0.0;
                    double sourceLuminanceNorm = 0.0;
                    double languageLuminanceNorm = 0.0;
                    double gradientDot = 0.0;
                    double sourceGradientNorm = 0.0;
                    double languageGradientNorm = 0.0;
                    for (int y = y0; y < y1; y++)
                    {
                        int origin = y * REFINE_SIDE;
                        for (int x = x0; x < x1; x++)
                        {
                            int index = origin + x;
                            if (maskValues[index] == 0 || x < 4 || y < 4 || x >= REFINE_SIDE - 4 || y >= REFINE_SIDE - 4)
                                continue;
                            double sourceValue = pair.SourceValues[index] - sourceMean;
                            double languageValue = warpedValues[index] - languageMean;
                            luminanceDot += sourceValue * languageValue;
                            sourceLuminanceNorm += sourceValue * sourceValue;
                            languageLuminanceNorm += languageValue * languageValue;
                            gradientDot += pair.SourceGradientXValues[index] * gradientXValues[index] + pair.SourceGradientYValues[index] * gradientYValues[index];
                            sourceGradientNorm += pair.SourceGradientXValues[index] * pair.SourceGradientXValues[index] + pair.SourceGradientYValues[index] * pair.SourceGradientYValues[index];
                            languageGradientNorm += gradientXValues[index] * gradientXValues[index] + gradientYValues[index] * gradientYValues[index];
                        }
                    }
                    double luminanceDenominator = Math.Sqrt(sourceLuminanceNorm * languageLuminanceNorm);
                    double gradientDenominator = Math.Sqrt(sourceGradientNorm * languageGradientNorm);
                    if (luminanceDenominator > 1.0e-5)
                        luminanceScores.Add(luminanceDot / luminanceDenominator);
                    if (gradientDenominator > 1.0e-5)
                        gradientScores.Add(gradientDot / gradientDenominator);
                }
            }
            validTiles = Math.Min(luminanceScores.Count, gradientScores.Count);
            if (validTiles == 0)
                return -1.0;
            return 0.30 * this.Median(luminanceScores) + 0.70 * this.Median(gradientScores);
        }

        /// <summary>
        /// Completa il contratto pubblico e calcola i due crop comuni fissi
        /// </summary>
        private void PopulateAlignment(VisualGeometryAlignment alignment, GeometryConsensus consensus, VideoGeometryProfile sourceProfile, VideoGeometryProfile languageProfile, PixelRect sourceActive, PixelRect languageActive)
        {
            alignment.ScaleX = consensus.ScaleX;
            alignment.ScaleY = consensus.ScaleY;
            alignment.TranslateX = consensus.TranslateX;
            alignment.TranslateY = consensus.TranslateY;
            alignment.ScaleXDispersion = consensus.ScaleXDispersion;
            alignment.ScaleYDispersion = consensus.ScaleYDispersion;
            alignment.TranslateXDispersion = consensus.TranslateXDispersion;
            alignment.TranslateYDispersion = consensus.TranslateYDispersion;
            alignment.AcceptedMatchCount = consensus.Candidates.Count;
            alignment.AcceptedMatches.Clear();
            for (int i = 0; i < consensus.Candidates.Count; i++)
            {
                GeometryCandidate candidate = consensus.Candidates[i];
                alignment.AcceptedMatches.Add(new VisualGeometryMatch
                {
                    SourcePtsMs = candidate.SourcePtsMs,
                    LanguagePtsMs = candidate.LanguagePtsMs,
                    Score = candidate.Score,
                    ScaleX = candidate.ScaleX,
                    ScaleY = candidate.ScaleY,
                    TranslateX = candidate.TranslateX,
                    TranslateY = candidate.TranslateY
                });
            }

            double sourceLeft = Math.Max(0.0, consensus.TranslateX);
            double sourceTop = Math.Max(0.0, consensus.TranslateY);
            double sourceRight = Math.Min(1.0, consensus.ScaleX + consensus.TranslateX);
            double sourceBottom = Math.Min(1.0, consensus.ScaleY + consensus.TranslateY);
            if (sourceRight <= sourceLeft || sourceBottom <= sourceTop)
                throw new InvalidOperationException("Nessuna area comune fra le geometrie video");
            double languageLeft = (sourceLeft - consensus.TranslateX) / consensus.ScaleX;
            double languageTop = (sourceTop - consensus.TranslateY) / consensus.ScaleY;
            double languageRight = (sourceRight - consensus.TranslateX) / consensus.ScaleX;
            double languageBottom = (sourceBottom - consensus.TranslateY) / consensus.ScaleY;

            PixelRect sourceCommon = this.ProjectRect(sourceActive, sourceLeft, sourceTop, sourceRight, sourceBottom);
            PixelRect languageCommon = this.ProjectRect(languageActive, languageLeft, languageTop, languageRight, languageBottom);
            alignment.SourceCommonCropPx = this.FormatCrop(sourceCommon, sourceProfile.Width, sourceProfile.Height);
            alignment.LanguageCommonCropPx = this.FormatCrop(languageCommon, languageProfile.Width, languageProfile.Height);
        }

        #endregion

        #region Metodi privati - Contratto dHash

        /// <summary>
        /// Usa i match già confermati per scegliere l'affine quando il viewport indipendente non tiene e l'affine lo migliora
        /// </summary>
        private void ValidateDHashContract(FrameGeometryEstimationResult result, VideoGeometryProfile sourceProfile, VideoGeometryProfile languageProfile, PixelRect sourceActive, PixelRect languageActive, FrameGeometry sourceGeometry, FrameGeometry languageGeometry, List<DeepSiftVisualAnchor> sourceAnchors, List<DeepSiftVisualAnchor> languageAnchors, GeometryConsensus consensus)
        {
            NormalizedRect sourceViewport = this.BuildDHashViewport(sourceProfile, sourceActive, sourceGeometry);
            NormalizedRect independentLanguageViewport = this.BuildDHashViewport(languageProfile, languageActive, languageGeometry);
            NormalizedRect mappedLanguageViewport = new NormalizedRect(
                (sourceViewport.Left - consensus.TranslateX) / consensus.ScaleX,
                (sourceViewport.Top - consensus.TranslateY) / consensus.ScaleY,
                (sourceViewport.Right - consensus.TranslateX) / consensus.ScaleX,
                (sourceViewport.Bottom - consensus.TranslateY) / consensus.ScaleY);
            double viewportEpsilon = 1.0 / SIFT_SIDE;
            bool affineIsInside = mappedLanguageViewport.Left >= -viewportEpsilon && mappedLanguageViewport.Top >= -viewportEpsilon && mappedLanguageViewport.Right <= 1.0 + viewportEpsilon && mappedLanguageViewport.Bottom <= 1.0 + viewportEpsilon;
            NormalizedRect affineLanguageViewport = new NormalizedRect(
                Math.Clamp(mappedLanguageViewport.Left, 0.0, 1.0),
                Math.Clamp(mappedLanguageViewport.Top, 0.0, 1.0),
                Math.Clamp(mappedLanguageViewport.Right, 0.0, 1.0),
                Math.Clamp(mappedLanguageViewport.Bottom, 0.0, 1.0));

            int independentExplained = 0;
            int affineExplained = 0;
            using (CpuHashBackend backend = new CpuHashBackend())
            {
                for (int i = 0; i < consensus.Candidates.Count; i++)
                {
                    GeometryCandidate candidate = consensus.Candidates[i];
                    this.ComputeViewportHashes(sourceAnchors[candidate.SourceIndex], sourceViewport, backend, out ulong sourceHash0, out ulong sourceHash1);
                    this.ComputeViewportHashes(languageAnchors[candidate.LanguageIndex], independentLanguageViewport, backend, out ulong independentHash0, out ulong independentHash1);
                    this.ComputeViewportHashes(languageAnchors[candidate.LanguageIndex], affineLanguageViewport, backend, out ulong affineHash0, out ulong affineHash1);
                    int independentDistance = BitOperations.PopCount(sourceHash0 ^ independentHash0) + BitOperations.PopCount(sourceHash1 ^ independentHash1);
                    int affineDistance = BitOperations.PopCount(sourceHash0 ^ affineHash0) + BitOperations.PopCount(sourceHash1 ^ affineHash1);
                    if (independentDistance <= EditAnalysisProfile.DETECTION_THRESHOLD)
                        independentExplained++;
                    if (affineDistance <= EditAnalysisProfile.DETECTION_THRESHOLD)
                        affineExplained++;
                }
            }

            int pairCount = consensus.Candidates.Count;
            result.Alignment.DHashContractPairCount = pairCount;
            result.Alignment.IndependentDHashExplainedCount = independentExplained;
            result.Alignment.AffineDHashExplainedCount = affineExplained;
            result.Alignment.UseAffineDHashViewport = affineIsInside && independentExplained * 2 <= pairCount && affineExplained > independentExplained;
            if (!result.Alignment.UseAffineDHashViewport)
                return;

            result.AffineLanguageDHashGeometry.CropPx = this.FormatCrop(languageActive, languageProfile.Width, languageProfile.Height);
            result.AffineLanguageDHashGeometry.UseNormalizedActiveViewport = true;
            result.AffineLanguageDHashGeometry.ViewportLeft = affineLanguageViewport.Left;
            result.AffineLanguageDHashGeometry.ViewportTop = affineLanguageViewport.Top;
            result.AffineLanguageDHashGeometry.ViewportRight = affineLanguageViewport.Right;
            result.AffineLanguageDHashGeometry.ViewportBottom = affineLanguageViewport.Bottom;
        }

        /// <summary>
        /// Proietta il viewport dHash indipendente nello spazio normalizzato dell'area attiva
        /// </summary>
        private NormalizedRect BuildDHashViewport(VideoGeometryProfile profile, PixelRect active, FrameGeometry geometry)
        {
            int cropLeft;
            int cropRight;
            int cropTop;
            int cropBottom;
            Options.TryParseAnalysisCropPx(geometry.CropPx, out cropLeft, out cropRight, out cropTop, out cropBottom);
            double sar = profile.SarDen > 0 ? profile.SarNum / (double)profile.SarDen : 1.0;
            double croppedWidth = profile.Width - cropLeft - cropRight;
            double croppedHeight = profile.Height - cropTop - cropBottom;
            double displayWidth = croppedWidth * sar;
            double squareSide = Math.Min(displayWidth, croppedHeight);
            double squareLeft = (displayWidth - squareSide) / 2.0;
            double squareTop = (croppedHeight - squareSide) / 2.0;
            double zoom = Math.Clamp(geometry.Zoom, 0.01, 1.0);
            double verticalFraction = Math.Clamp((1.0 - zoom) / 2.0 + geometry.VerticalShift, 0.0, 1.0 - zoom);
            double rawLeft = cropLeft + (squareLeft + squareSide * (1.0 - zoom) / 2.0) / sar;
            double rawRight = cropLeft + (squareLeft + squareSide * (1.0 + zoom) / 2.0) / sar;
            double rawTop = cropTop + squareTop + squareSide * verticalFraction;
            double rawBottom = rawTop + squareSide * zoom;
            return new NormalizedRect(
                (rawLeft - active.Left) / active.Width,
                (rawTop - active.Top) / active.Height,
                (rawRight - active.Left) / active.Width,
                (rawBottom - active.Top) / active.Height);
        }

        /// <summary>
        /// Estrae un viewport dall'ancora normalizzata e calcola i due hash di produzione
        /// </summary>
        private void ComputeViewportHashes(DeepSiftVisualAnchor anchor, NormalizedRect viewport, CpuHashBackend backend, out ulong hash0, out ulong hash1)
        {
            int left = (int)Math.Round(viewport.Left * anchor.Width);
            int top = (int)Math.Round(viewport.Top * anchor.Height);
            int right = (int)Math.Round(viewport.Right * anchor.Width);
            int bottom = (int)Math.Round(viewport.Bottom * anchor.Height);
            int width = Math.Max(1, right - left);
            int height = Math.Max(1, bottom - top);
            using (Mat input = Mat.FromPixelData(anchor.Height, anchor.Width, MatType.CV_8UC1, anchor.Frame))
            using (Mat selected = new Mat(height, width, MatType.CV_8UC1, Scalar.Black))
            using (Mat resized = new Mat())
            {
                int sourceLeft = Math.Max(0, left);
                int sourceTop = Math.Max(0, top);
                int sourceRight = Math.Min(anchor.Width, right);
                int sourceBottom = Math.Min(anchor.Height, bottom);
                if (sourceRight > sourceLeft && sourceBottom > sourceTop)
                {
                    Rect sourceRect = new Rect(sourceLeft, sourceTop, sourceRight - sourceLeft, sourceBottom - sourceTop);
                    Rect targetRect = new Rect(sourceLeft - left, sourceTop - top, sourceRight - sourceLeft, sourceBottom - sourceTop);
                    using (Mat sourceRegion = new Mat(input, sourceRect))
                    using (Mat targetRegion = new Mat(selected, targetRect))
                        sourceRegion.CopyTo(targetRegion);
                }
                Cv2.Resize(selected, resized, new Size(FrameSignals.SIDE, FrameSignals.SIDE), 0.0, 0.0, InterpolationFlags.Area);
                resized.GetArray(out byte[] pixels);
                List<ulong> horizontal = new List<ulong>(1);
                List<ulong> vertical = new List<ulong>(1);
                backend.Hash(pixels, 1, horizontal, vertical);
                hash0 = horizontal[0];
                hash1 = vertical[0];
            }
        }

        #endregion

        #region Metodi privati - Utility

        /// <summary>
        /// Costruisce la geometria video diagnostica con crop in pixel storage nativi
        /// </summary>
        private FrameSyncGeometryInfo BuildGeometryInfo(VideoGeometryProfile profile, PixelRect active, CropDetectionDiagnostics diagnostics, string manualCropPx, string mode)
        {
            FrameSyncGeometryInfo result = new FrameSyncGeometryInfo();
            result.FilePath = profile.FilePath;
            result.Width = profile.Width;
            result.Height = profile.Height;
            result.SarNum = profile.SarNum;
            result.SarDen = profile.SarDen;
            result.DarNum = profile.DarNum;
            result.DarDen = profile.DarDen;
            result.DisplayWidth = profile.DisplayWidth;
            result.DisplayHeight = profile.DisplayHeight;
            result.DisplayAspect = profile.DisplayAspect;
            result.CropLeft = active.Left;
            result.CropRight = profile.Width - active.Right;
            result.CropTop = active.Top;
            result.CropBottom = profile.Height - active.Bottom;
            result.CropSampleCount = diagnostics.SampleCount;
            result.CropLeftDispersionPx = diagnostics.LeftDispersionPx;
            result.CropRightDispersionPx = diagnostics.RightDispersionPx;
            result.CropTopDispersionPx = diagnostics.TopDispersionPx;
            result.CropBottomDispersionPx = diagnostics.BottomDispersionPx;
            result.HasBlackBorderCrop = string.Equals(mode, "black_border_autocrop", StringComparison.Ordinal);
            result.ManualAnalysisCropPx = Options.NormalizeAnalysisCropPx(manualCropPx);
            result.CropMode = mode;
            return result;
        }

        /// <summary>
        /// Proietta un rettangolo normalizzato dentro un'area attiva nativa
        /// </summary>
        private PixelRect ProjectRect(PixelRect active, double left, double top, double right, double bottom)
        {
            int x0 = active.Left + (int)Math.Round(left * active.Width);
            int y0 = active.Top + (int)Math.Round(top * active.Height);
            int x1 = active.Left + (int)Math.Round(right * active.Width);
            int y1 = active.Top + (int)Math.Round(bottom * active.Height);
            return new PixelRect(Math.Max(active.Left, x0), Math.Max(active.Top, y0), Math.Min(active.Right, x1), Math.Min(active.Bottom, y1));
        }

        /// <summary>
        /// Formatta un rettangolo esclusivo come crop L:R:T:B
        /// </summary>
        private string FormatCrop(PixelRect rect, int width, int height)
        {
            return rect.Left.ToString(CultureInfo.InvariantCulture) + ":" + (width - rect.Right).ToString(CultureInfo.InvariantCulture) + ":" + rect.Top.ToString(CultureInfo.InvariantCulture) + ":" + (height - rect.Bottom).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Inverte una matrice 3x3 row-major
        /// </summary>
        private bool TryInvert3x3(double[] value, out double[] result)
        {
            result = null;
            if (value == null || value.Length != 9)
                return false;
            double determinant = value[0] * (value[4] * value[8] - value[5] * value[7]) - value[1] * (value[3] * value[8] - value[5] * value[6]) + value[2] * (value[3] * value[7] - value[4] * value[6]);
            if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1.0e-12)
                return false;
            double inverse = 1.0 / determinant;
            result = new double[]
            {
                (value[4] * value[8] - value[5] * value[7]) * inverse,
                (value[2] * value[7] - value[1] * value[8]) * inverse,
                (value[1] * value[5] - value[2] * value[4]) * inverse,
                (value[5] * value[6] - value[3] * value[8]) * inverse,
                (value[0] * value[8] - value[2] * value[6]) * inverse,
                (value[2] * value[3] - value[0] * value[5]) * inverse,
                (value[3] * value[7] - value[4] * value[6]) * inverse,
                (value[1] * value[6] - value[0] * value[7]) * inverse,
                (value[0] * value[4] - value[1] * value[3]) * inverse
            };
            return true;
        }

        /// <summary>
        /// Restituisce un percentile da un istogramma a 256 valori
        /// </summary>
        private int HistogramPercentile(int[] histogram, int count, double percentile)
        {
            int rank = Math.Max(0, (int)Math.Ceiling(count * percentile) - 1);
            int cumulative = 0;
            for (int i = 0; i < histogram.Length; i++)
            {
                cumulative += histogram[i];
                if (cumulative > rank)
                    return i;
            }
            return histogram.Length - 1;
        }

        /// <summary>
        /// Calcola un dHash orizzontale compatto per eliminare duplicati visivi
        /// </summary>
        private ulong ComputeDHash(DeepSiftVisualAnchor anchor)
        {
            using (Mat input = Mat.FromPixelData(anchor.Height, anchor.Width, MatType.CV_8UC1, anchor.Frame))
            using (Mat small = new Mat())
            {
                Cv2.Resize(input, small, new Size(9, 8), 0.0, 0.0, InterpolationFlags.Area);
                small.GetArray(out byte[] values);
                ulong result = 0;
                int bit = 0;
                for (int y = 0; y < 8; y++)
                {
                    int origin = y * 9;
                    for (int x = 0; x < 8; x++)
                    {
                        if (values[origin + x] > values[origin + x + 1])
                            result |= 1UL << bit;
                        bit++;
                    }
                }
                return result;
            }
        }

        /// <summary>
        /// Calcola la mediana di una lista non vuota
        /// </summary>
        private double Median(List<double> values)
        {
            if (values == null || values.Count == 0)
                return 0.0;
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) * 0.5 : values[middle];
        }

        /// <summary>
        /// Calcola un quantile lineare da una lista già ordinata
        /// </summary>
        private double Quantile(List<double> sortedValues, double probability)
        {
            if (sortedValues == null || sortedValues.Count == 0)
                return 0.0;
            double index = Math.Clamp(probability, 0.0, 1.0) * (sortedValues.Count - 1);
            int lower = (int)Math.Floor(index);
            int upper = (int)Math.Ceiling(index);
            if (lower == upper)
                return sortedValues[lower];
            double fraction = index - lower;
            return sortedValues[lower] * (1.0 - fraction) + sortedValues[upper] * fraction;
        }

        /// <summary>
        /// Calcola la dispersione MAD normalizzata
        /// </summary>
        private double Mad(List<double> values, double center)
        {
            List<double> deviations = new List<double>(values.Count);
            for (int i = 0; i < values.Count; i++)
                deviations.Add(Math.Abs(values[i] - center));
            return 1.4826 * this.Median(deviations);
        }

        /// <summary>
        /// Imposta il rifiuto mantenendo le diagnostiche già raccolte
        /// </summary>
        private FrameGeometryEstimationResult Reject(FrameGeometryEstimationResult result, string reason)
        {
            result.Alignment.Success = false;
            result.Alignment.RejectReason = reason ?? "";
            return result;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Rettangolo espresso come frazioni dell'area attiva
        /// </summary>
        private readonly struct NormalizedRect
        {
            public NormalizedRect(double left, double top, double right, double bottom)
            {
                this.Left = left;
                this.Top = top;
                this.Right = right;
                this.Bottom = bottom;
            }

            public double Left { get; }
            public double Top { get; }
            public double Right { get; }
            public double Bottom { get; }
        }

        /// <summary>
        /// Rettangolo con estremi destro e inferiore esclusivi
        /// </summary>
        private readonly struct PixelRect
        {
            public PixelRect(int left, int top, int right, int bottom)
            {
                this.Left = left;
                this.Top = top;
                this.Right = right;
                this.Bottom = bottom;
            }

            public int Left { get; }
            public int Top { get; }
            public int Right { get; }
            public int Bottom { get; }
            public int Width { get { return this.Right - this.Left; } }
            public int Height { get { return this.Bottom - this.Top; } }
            public bool IsValid { get { return this.Left >= 0 && this.Top >= 0 && this.Right > this.Left && this.Bottom > this.Top; } }
        }

        /// <summary>
        /// Stabilità del consenso sui quattro bordi nativi
        /// </summary>
        private class CropDetectionDiagnostics
        {
            public int SampleCount;
            public double LeftDispersionPx;
            public double RightDispersionPx;
            public double TopDispersionPx;
            public double BottomDispersionPx;
        }

        /// <summary>
        /// Trasformazione ricavata da una singola coppia
        /// </summary>
        private class GeometryCandidate
        {
            public int SourceIndex;
            public int LanguageIndex;
            public double SourcePtsMs;
            public double LanguagePtsMs;
            public ulong SourceHash;
            public ulong LanguageHash;
            public double ScaleX;
            public double ScaleY;
            public double TranslateX;
            public double TranslateY;
            public double Score;
        }

        /// <summary>
        /// Consenso robusto delle trasformazioni indipendenti
        /// </summary>
        private class GeometryConsensus
        {
            public GeometryConsensus()
            {
                this.Candidates = new List<GeometryCandidate>();
            }

            public List<GeometryCandidate> Candidates;
            public double ScaleX;
            public double ScaleY;
            public double TranslateX;
            public double TranslateY;
            public double ScaleXDispersion;
            public double ScaleYDispersion;
            public double TranslateXDispersion;
            public double TranslateYDispersion;
        }

        /// <summary>
        /// Coppia di frame e gradienti preparata una volta per l'affinamento
        /// </summary>
        private sealed class RefinementPair : IDisposable
        {
            public RefinementPair(DeepSiftVisualAnchor source, DeepSiftVisualAnchor language)
            {
                this.Source = new Mat();
                this.Language = new Mat();
                this.SourceGradientX = new Mat();
                this.SourceGradientY = new Mat();
                this.Valid = new Mat(REFINE_SIDE, REFINE_SIDE, MatType.CV_8UC1, Scalar.All(255));
                using (Mat sourceInput = Mat.FromPixelData(source.Height, source.Width, MatType.CV_8UC1, source.Frame))
                using (Mat languageInput = Mat.FromPixelData(language.Height, language.Width, MatType.CV_8UC1, language.Frame))
                {
                    Cv2.Resize(sourceInput, this.Source, new Size(REFINE_SIDE, REFINE_SIDE), 0.0, 0.0, InterpolationFlags.Area);
                    Cv2.Resize(languageInput, this.Language, new Size(REFINE_SIDE, REFINE_SIDE), 0.0, 0.0, InterpolationFlags.Area);
                }
                Cv2.Sobel(this.Source, this.SourceGradientX, MatType.CV_32FC1, 1, 0, 3);
                Cv2.Sobel(this.Source, this.SourceGradientY, MatType.CV_32FC1, 0, 1, 3);
                this.Source.GetArray(out byte[] sourceValues);
                this.SourceGradientX.GetArray(out float[] sourceGradientXValues);
                this.SourceGradientY.GetArray(out float[] sourceGradientYValues);
                this.SourceValues = sourceValues;
                this.SourceGradientXValues = sourceGradientXValues;
                this.SourceGradientYValues = sourceGradientYValues;
            }

            public Mat Source { get; }
            public Mat Language { get; }
            public Mat SourceGradientX { get; }
            public Mat SourceGradientY { get; }
            public Mat Valid { get; }
            public byte[] SourceValues { get; }
            public float[] SourceGradientXValues { get; }
            public float[] SourceGradientYValues { get; }
            public double InitialQuality { get; set; }

            public void Dispose()
            {
                this.Source.Dispose();
                this.Language.Dispose();
                this.SourceGradientX.Dispose();
                this.SourceGradientY.Dispose();
                this.Valid.Dispose();
            }
        }

        /// <summary>
        /// Accumulatore limitato a un frame nativo
        /// </summary>
        private class NativeFrameCollector
        {
            private int _count;

            public NativeFrameCollector(int size)
            {
                this.Frame = new byte[size];
            }

            public byte[] Frame { get; private set; }
            public bool IsComplete { get { return this._count == this.Frame.Length; } }

            public void Append(byte[] buffer, int count)
            {
                int copied = Math.Min(count, this.Frame.Length - this._count);
                if (copied <= 0)
                    return;
                Buffer.BlockCopy(buffer, 0, this.Frame, this._count, copied);
                this._count += copied;
            }
        }

        #endregion
    }
}
