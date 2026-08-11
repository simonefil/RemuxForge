using RemuxForge.Core.Analysis.Deep.Features;
using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Orchestratore della pipeline DeepAnalysis globale SIFT backend-neutral
    /// </summary>
    public sealed class DeepAnalysisService : VideoSyncServiceBase
    {
        #region Variabili di classe

        private readonly ToolPathResolverService _toolPathResolver;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso FFmpeg risolto</param>
        /// <param name="toolPathResolver">Resolver degli strumenti MKV</param>
        public DeepAnalysisService(string ffmpegPath, ToolPathResolverService toolPathResolver) : base(ffmpegPath, LogSection.Deep)
        {
            if (string.IsNullOrEmpty(ffmpegPath))
                throw new ArgumentException("Percorso FFmpeg mancante", nameof(ffmpegPath));
            if (toolPathResolver == null)
                throw new ArgumentNullException(nameof(toolPathResolver));
            this._ffmpegPath = ffmpegPath;
            this._toolPathResolver = toolPathResolver;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Esegue discovery sparsa, tracking temporale SIFT e refinement dei boundary
        /// </summary>
        /// <param name="sourceFile">File source</param>
        /// <param name="languageFile">File language</param>
        /// <param name="manualStretchFactor">Stretch manuale</param>
        /// <param name="sourceCropPx">Crop source</param>
        /// <param name="languageCropPx">Crop language</param>
        /// <param name="cancellationToken">Token di annullamento cooperativo</param>
        /// <returns>EditMap completa oppure null</returns>
        public EditMap Analyze(string sourceFile, string languageFile, string manualStretchFactor, string sourceCropPx, string languageCropPx, CancellationToken cancellationToken = default)
        {
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            DeepAnalysisResult result = new DeepAnalysisResult();
            this.LastResult = result;

            try
            {
                if (!this.TryResolveStretch(manualStretchFactor, out double sourceToLanguageScale, out string stretchFactor, out string rejectReason))
                {
                    return this.Reject(result, rejectReason);
                }

                result.SourceToLanguageScale = sourceToLanguageScale;
                result.StretchFactor = stretchFactor;
                cancellationToken.ThrowIfCancellationRequested();
                this.SetAnalysisCrop(sourceCropPx, languageCropPx);
                this.PrepareGeometryDrivenCrop(sourceFile, languageFile);
                result.SourceGeometry = this._lastSourceGeometryInfo;
                result.LanguageGeometry = this._lastLanguageGeometryInfo;
                sourceCropPx = Options.NormalizeAnalysisCropPx(sourceCropPx);
                languageCropPx = Options.NormalizeAnalysisCropPx(languageCropPx);
                bool sourceGeometryCrop = this.UseGeometryCrop(this._geometryCropSourceToFourThree, sourceCropPx);
                bool languageGeometryCrop = this.UseGeometryCrop(this._geometryCropLanguageToFourThree, languageCropPx);
                AdvancedConfig advanced = AppSettingsService.Instance.Settings.Advanced;
                string siftBackend = advanced.DeepAnalysis.SiftBackend;
                FfmpegConfig ffmpegConfig = new FfmpegConfig();
                ffmpegConfig.HardwareAcceleration = advanced.Ffmpeg.HardwareAcceleration;
                ffmpegConfig.HardwareAccelerationMethod = advanced.Ffmpeg.HardwareAccelerationMethod;
                ffmpegConfig.FrameExtractionTimeoutMs = Math.Max(advanced.Ffmpeg.FrameExtractionTimeoutMs, advanced.DeepAnalysis.SceneExtractTimeoutMs);
                VideoSyncConfig videoSyncConfig = advanced.VideoSync;
                string mkvMergePath = this._toolPathResolver.ResolveMkvMergePath(false);
                string mkvExtractPath = this._toolPathResolver.ResolveMkvExtractPath(mkvMergePath, false);
                DeepSiftAnchorTimelineBuilder sourceTimelineBuilder = new DeepSiftAnchorTimelineBuilder(this._ffmpegPath, mkvMergePath, mkvExtractPath, ffmpegConfig, videoSyncConfig.FrameWidth, videoSyncConfig.FrameHeight, 1.0, sourceGeometryCrop, frames => this.NormalizeBlackBorders(sourceFile, sourceGeometryCrop, sourceCropPx, frames));
                DeepSiftAnchorTimelineBuilder languageTimelineBuilder = new DeepSiftAnchorTimelineBuilder(this._ffmpegPath, mkvMergePath, mkvExtractPath, ffmpegConfig, videoSyncConfig.FrameWidth, videoSyncConfig.FrameHeight, 1.0, languageGeometryCrop, frames => this.NormalizeBlackBorders(languageFile, languageGeometryCrop, languageCropPx, frames));

                ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  fase 1: discovery PTS adattiva...");
                ConsoleHelper.Progress(LogSection.Deep, 14, "Deep: ancore SIFT");
                using (FrameFeatureBatchMatcherBase batchMatcher = this.CreateBatchMatcher(siftBackend))
                {
                    result.BackendName = batchMatcher.BackendName;
                    if (!batchMatcher.IsAvailable(out rejectReason))
                        return this.Reject(result, rejectReason);

                    Func<string, bool> geometryCropResolver = filePath => string.Equals(filePath, sourceFile, StringComparison.OrdinalIgnoreCase) ? sourceGeometryCrop : languageGeometryCrop;
                    Action<string, bool, string, List<byte[]>> frameNormalizer = (filePath, geometryCrop, manualCrop, frames) => this.NormalizeBlackBorders(filePath, geometryCrop, manualCrop, frames);
                    int maximumParallelism = ParallelismHelper.ResolveDefaultMaxDegree();
                    DeepSiftEditMapBuilder editMapBuilder = new DeepSiftEditMapBuilder(this._ffmpegPath, ffmpegConfig, videoSyncConfig, LogSection.Deep, batchMatcher, geometryCropResolver, frameNormalizer, maximumParallelism);
                    DeepSiftTemporalAlignmentStageResult alignment = new DeepSiftTemporalAligner().Align(sourceTimelineBuilder, languageTimelineBuilder, batchMatcher, editMapBuilder, sourceFile, languageFile, sourceCropPx, languageCropPx, stretchFactor, sourceToLanguageScale, maximumParallelism, cancellationToken);
                    result.SourceTimeline = alignment.SourceTimeline;
                    result.LanguageTimeline = alignment.LanguageTimeline;
                    result.BatchMatching = alignment.Batch;
                    if (!alignment.Accepted || result.BatchMatching == null)
                        return this.Reject(result, string.IsNullOrEmpty(alignment.RejectReason) ? "Tracking SIFT adattivo non disponibile" : alignment.RejectReason);
                    this.ReleaseAnchorFrames(result.SourceTimeline.Anchors);
                    this.ReleaseAnchorFrames(result.LanguageTimeline.Anchors);

                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  matching: elaborate=" + result.BatchMatching.ProcessedCellCount.ToString(CultureInfo.InvariantCulture) + ", accettate=" + result.BatchMatching.AcceptedPairs.Count.ToString(CultureInfo.InvariantCulture) + ", scala=" + alignment.AppliedScale.ToString("R", CultureInfo.InvariantCulture));
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  fase 3: plateau temporali globali...");
                    ConsoleHelper.Progress(LogSection.Deep, 68, "Deep: plateau globali");
                    result.Alignment = alignment.Temporal;
                    sourceToLanguageScale = alignment.AppliedScale;
                    result.SourceToLanguageScale = sourceToLanguageScale;
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  percorso: match=" + result.Alignment.Chain.Count.ToString(CultureInfo.InvariantCulture) + ", score=" + result.Alignment.ChainScore.ToString("F3", CultureInfo.InvariantCulture) + ", plateau=" + result.Alignment.Plateaus.Count.ToString(CultureInfo.InvariantCulture));
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  fase 4: boundary al frame common-side...");
                    ConsoleHelper.Progress(LogSection.Deep, 82, "Deep: boundary frame");
                    result.EditMapResult = alignment.EditMapResult;
                }

                if (result.EditMapResult == null || !result.EditMapResult.Success)
                    return this.Reject(result, result.EditMapResult != null ? result.EditMapResult.RejectReason : "Costruzione EditMap fallita");

                result.Status = "Accepted";
                result.EditMapResult.EditMap.AnalysisTimeMs = totalStopwatch.ElapsedMilliseconds;
                return result.EditMapResult.EditMap;
            }
            catch (OperationCanceledException)
            {
                result.Status = "Cancelled";
                result.RejectReason = "DeepAnalysis annullata";
                throw;
            }
            catch (Exception ex)
            {
                return this.Reject(result, ex.Message);
            }
            finally
            {
                this.ReleaseResultFrames(result);
                totalStopwatch.Stop();
                result.TotalElapsedMs = totalStopwatch.ElapsedMilliseconds;
                if (result.EditMapResult != null && result.EditMapResult.EditMap != null)
                    result.EditMapResult.EditMap.AnalysisTimeMs = result.TotalElapsedMs;
            }
        }

        /// <summary>
        /// Ultimo risultato diagnostico, valorizzato anche in caso di rifiuto
        /// </summary>
        public DeepAnalysisResult LastResult { get; private set; }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Imposta un rifiuto esplicito e restituisce null come mappa
        /// </summary>
        private EditMap Reject(DeepAnalysisResult result, string reason)
        {
            result.Status = "Rejected";
            result.RejectReason = string.IsNullOrEmpty(reason) ? "DeepAnalysis rifiutata" : reason;
            return null;
        }

        /// <summary>
        /// Crea esclusivamente il backend selezionato senza fallback impliciti
        /// </summary>
        private FrameFeatureBatchMatcherBase CreateBatchMatcher(string backendName)
        {
            if (string.Equals(backendName, "vulkan", StringComparison.OrdinalIgnoreCase))
                return new VulkanSiftBatchMatcher();
            if (string.IsNullOrEmpty(backendName) || string.Equals(backendName, "cpu", StringComparison.OrdinalIgnoreCase))
                return new OpenCvSiftBatchMatcher();
            throw new InvalidOperationException("Backend SIFT DeepAnalysis non supportato: " + backendName);
        }

        /// <summary>
        /// Risolve la scala source-language con la semantica stretch esistente
        /// </summary>
        private bool TryResolveStretch(string manualStretchFactor, out double sourceToLanguageScale, out string stretchFactor, out string rejectReason)
        {
            double stretchRatio;
            string normalizedManualFactor;
            sourceToLanguageScale = 1.0;
            stretchFactor = "";
            rejectReason = "";

            if (!string.IsNullOrEmpty(manualStretchFactor != null ? manualStretchFactor.Trim() : null))
            {
                if (!SpeedCorrectionService.TryParseStretchFactor(manualStretchFactor, out stretchRatio, out normalizedManualFactor))
                {
                    rejectReason = "Stretch manuale non valido: " + manualStretchFactor;
                    return false;
                }

                sourceToLanguageScale = 1.0 / stretchRatio;
                if (!double.IsFinite(sourceToLanguageScale) || sourceToLanguageScale <= 0.0)
                {
                    rejectReason = "Scala temporale manuale non valida: " + manualStretchFactor;
                    return false;
                }
                stretchFactor = normalizedManualFactor;
                return true;
            }

            return true;
        }

        /// <summary>
        /// Rilascia i buffer grayscale globali dopo che il backend ha completato la matrice
        /// </summary>
        private void ReleaseAnchorFrames(System.Collections.Generic.List<DeepSiftVisualAnchor> anchors)
        {
            if (anchors == null)
                return;
            for (int i = 0; i < anchors.Count; i++)
                anchors[i].Frame = Array.Empty<byte>();
        }

        /// <summary>
        /// Rilascia i buffer frame trattenuti dai risultati anche sui percorsi di rifiuto
        /// </summary>
        /// <param name="result">Risultato parziale o completo</param>
        private void ReleaseResultFrames(DeepAnalysisResult result)
        {
            if (result == null)
                return;
            if (result.SourceTimeline != null)
                this.ReleaseAnchorFrames(result.SourceTimeline.Anchors);
            if (result.LanguageTimeline != null)
                this.ReleaseAnchorFrames(result.LanguageTimeline.Anchors);
            if (result.BatchMatching != null)
            {
                this.ReleaseAnchorFrames(result.BatchMatching.SourceAnchors);
                this.ReleaseAnchorFrames(result.BatchMatching.LanguageAnchors);
            }
        }

        #endregion

    }
}
