using RemuxForge.Core.Analysis.Deep.Features;
using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
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
    /// Orchestratore della pipeline DeepAnalysis globale basata su SIFT e indipendente dal backend di matching
    /// </summary>
    public sealed class DeepAnalysisService : VideoSyncServiceBase
    {
        #region Variabili di classe

        /// <summary>
        /// Risolve i percorsi degli strumenti MKV usati dalla pipeline
        /// </summary>
        private readonly ToolPathResolverService _toolPathResolver;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza il servizio con i percorsi necessari alla pipeline DeepAnalysis
        /// </summary>
        /// <param name="ffmpegPath">Percorso FFmpeg risolto</param>
        /// <param name="toolPathResolver">Resolver dei percorsi degli strumenti MKV</param>
        public DeepAnalysisService(string ffmpegPath, ToolPathResolverService toolPathResolver) : base(ffmpegPath, LogSection.Deep)
        {
            if (string.IsNullOrEmpty(ffmpegPath))
                throw new ArgumentException(AppText.T("deep.temporal.argument.missingFfmpegPath"), nameof(ffmpegPath));
            if (toolPathResolver == null)
                throw new ArgumentNullException(nameof(toolPathResolver));
            this._ffmpegPath = ffmpegPath;
            this._toolPathResolver = toolPathResolver;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Esegue la pipeline completa di discovery, tracking temporale SIFT e refinement dei boundary
        /// </summary>
        /// <param name="sourceFile">Percorso del file video source</param>
        /// <param name="languageFile">Percorso del file video language</param>
        /// <param name="manualStretchFactor">Fattore di stretch manuale oppure stringa vuota per la risoluzione automatica</param>
        /// <param name="sourceCropPx">Crop manuale in pixel per il file source</param>
        /// <param name="languageCropPx">Crop manuale in pixel per il file language</param>
        /// <param name="cancellationToken">Token di annullamento cooperativo</param>
        /// <returns>Mappa di montaggio completa se l'analisi viene accettata, altrimenti null</returns>
        public EditMap Analyze(string sourceFile, string languageFile, string manualStretchFactor, string sourceCropPx, string languageCropPx, CancellationToken cancellationToken = default)
        {
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            DeepAnalysisResult result = new DeepAnalysisResult();
            this.LastResult = result;

            try
            {
                // Risolve il fattore manuale prima di allocare le risorse della pipeline
                if (!this.TryResolveStretch(manualStretchFactor, out double sourceToLanguageScale, out string stretchFactor, out string rejectReason))
                {
                    return this.Reject(result, rejectReason);
                }

                result.SourceToLanguageScale = sourceToLanguageScale;
                result.StretchFactor = stretchFactor;
                cancellationToken.ThrowIfCancellationRequested();

                // Determina la geometria effettiva per mantenere coerenti crop e frame SIFT
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

                // Prepara le timeline uniformi e il preprocess condiviso dai due video
                DeepSiftAnchorTimelineBuilder sourceTimelineBuilder = new DeepSiftAnchorTimelineBuilder(this._ffmpegPath, mkvMergePath, mkvExtractPath, ffmpegConfig, videoSyncConfig.FrameWidth, videoSyncConfig.FrameHeight, 0.25, sourceGeometryCrop, frames => this.NormalizeBlackBorders(sourceFile, sourceGeometryCrop, sourceCropPx, frames));
                DeepSiftAnchorTimelineBuilder languageTimelineBuilder = new DeepSiftAnchorTimelineBuilder(this._ffmpegPath, mkvMergePath, mkvExtractPath, ffmpegConfig, videoSyncConfig.FrameWidth, videoSyncConfig.FrameHeight, 0.25, languageGeometryCrop, frames => this.NormalizeBlackBorders(languageFile, languageGeometryCrop, languageCropPx, frames));

                // Esegue matching, tracking temporale e costruzione della mappa nello stesso percorso
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, AppText.T("deep.temporal.log.phaseDiscovery"));
                ConsoleHelper.Progress(LogSection.Deep, 14, AppText.T("deep.temporal.progress.siftAnchors"));
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
                    result.Alignment = alignment.Temporal;
                    result.EditMapResult = alignment.EditMapResult;
                    if (!alignment.Accepted || result.BatchMatching == null)
                        return this.Reject(result, string.IsNullOrEmpty(alignment.RejectReason) ? AppText.T("deep.temporal.service.adaptiveTrackingUnavailable") : alignment.RejectReason);

                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, AppText.F("deep.temporal.log.matchingSummary", result.BatchMatching.ProcessedCellCount, result.BatchMatching.AcceptedPairs.Count, alignment.AppliedScale.ToString("R", CultureInfo.InvariantCulture)));
                    sourceToLanguageScale = alignment.AppliedScale;
                    result.SourceToLanguageScale = sourceToLanguageScale;
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, AppText.F("deep.temporal.log.pathSummary", result.Alignment.Chain.Count, result.Alignment.ChainScore.ToString("F3", CultureInfo.InvariantCulture), result.Alignment.SupportRuns.Count));
                }

                if (result.EditMapResult == null || !result.EditMapResult.Success)
                    return this.Reject(result, result.EditMapResult != null ? result.EditMapResult.RejectReason : AppText.T("deep.temporal.service.editMapFailed"));

                result.Status = DeepAnalysisStatus.Accepted;
                result.EditMapResult.EditMap.AnalysisTimeMs = totalStopwatch.ElapsedMilliseconds;
                return result.EditMapResult.EditMap;
            }
            catch (OperationCanceledException)
            {
                result.Status = DeepAnalysisStatus.Cancelled;
                result.RejectReason = AppText.T("deep.temporal.service.cancelled");
                throw;
            }
            catch (Exception ex)
            {
                return this.Reject(result, ex.Message);
            }
            finally
            {
                // Rilascia i frame anche quando l'analisi termina con rifiuto, errore o annullamento
                this.ReleaseResultFrames(result);
                totalStopwatch.Stop();
                result.TotalElapsedMs = totalStopwatch.ElapsedMilliseconds;
                if (result.EditMapResult != null && result.EditMapResult.EditMap != null)
                    result.EditMapResult.EditMap.AnalysisTimeMs = result.TotalElapsedMs;
            }
        }

        /// <summary>
        /// Ultimo risultato diagnostico dell'analisi, valorizzato anche in caso di rifiuto o errore gestito
        /// </summary>
        public DeepAnalysisResult LastResult { get; private set; }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Imposta lo stato di rifiuto e conserva la motivazione nel risultato diagnostico
        /// </summary>
        /// <param name="result">Risultato diagnostico da aggiornare</param>
        /// <param name="reason">Motivazione del rifiuto oppure stringa vuota per il messaggio predefinito</param>
        /// <returns>Valore null per segnalare che non è disponibile una mappa valida</returns>
        private EditMap Reject(DeepAnalysisResult result, string reason)
        {
            result.Status = DeepAnalysisStatus.Rejected;
            result.RejectReason = string.IsNullOrEmpty(reason) ? AppText.T("deep.temporal.service.rejected") : reason;
            return null;
        }

        /// <summary>
        /// Crea esclusivamente il matcher del backend selezionato senza fallback impliciti
        /// </summary>
        /// <param name="backendName">Nome del backend SIFT configurato</param>
        /// <returns>Matcher SIFT associato al backend richiesto</returns>
        private FrameFeatureBatchMatcherBase CreateBatchMatcher(string backendName)
        {
            if (string.Equals(backendName, "vulkan", StringComparison.OrdinalIgnoreCase))
                return new VulkanSiftBatchMatcher();
            if (string.IsNullOrEmpty(backendName) || string.Equals(backendName, "cpu", StringComparison.OrdinalIgnoreCase))
                return new OpenCvSiftBatchMatcher();
            throw new InvalidOperationException(AppText.F("deep.temporal.service.unsupportedBackend", backendName));
        }

        /// <summary>
        /// Risolve la scala source-language applicando la semantica di stretch esistente
        /// </summary>
        /// <param name="manualStretchFactor">Fattore di stretch manuale oppure stringa vuota</param>
        /// <param name="sourceToLanguageScale">Scala temporale source-language calcolata</param>
        /// <param name="stretchFactor">Fattore normalizzato da propagare alla fase di allineamento</param>
        /// <param name="rejectReason">Motivazione del rifiuto quando il valore manuale non è valido</param>
        /// <returns>True se la scala è valida o non è stato richiesto uno stretch manuale</returns>
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
                    rejectReason = AppText.F("deep.temporal.service.invalidManualStretch", manualStretchFactor);
                    return false;
                }

                sourceToLanguageScale = 1.0 / stretchRatio;
                if (!double.IsFinite(sourceToLanguageScale) || sourceToLanguageScale <= 0.0)
                {
                    rejectReason = AppText.F("deep.temporal.service.invalidManualScale", manualStretchFactor);
                    return false;
                }
                stretchFactor = normalizedManualFactor;
            }

            return true;
        }

        /// <summary>
        /// Rilascia i buffer dei frame trattenuti dai risultati parziali o completi
        /// </summary>
        /// <param name="result">Risultato parziale o completo</param>
        private void ReleaseResultFrames(DeepAnalysisResult result)
        {
            if (result == null)
                return;
            if (result.SourceTimeline != null)
                DeepSiftVisualAnchorBufferHelper.ReleaseFrames(result.SourceTimeline.Anchors);
            if (result.LanguageTimeline != null)
                DeepSiftVisualAnchorBufferHelper.ReleaseFrames(result.LanguageTimeline.Anchors);
            if (result.BatchMatching != null)
            {
                DeepSiftVisualAnchorBufferHelper.ReleaseFrames(result.BatchMatching.SourceAnchors);
                DeepSiftVisualAnchorBufferHelper.ReleaseFrames(result.BatchMatching.LanguageAnchors);
            }
        }

        #endregion

    }
}
