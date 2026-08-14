using RemuxForge.Core.Analysis.Deep.Features;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Esegue bootstrap, tracking e segmentazione temporale lungo un unico percorso lineare
    /// </summary>
    internal sealed partial class DeepSiftTemporalAligner
    {
        #region Costanti

        /// <summary>
        /// Ampiezza temporale della finestra source usata per ogni batch di bootstrap
        /// </summary>
        private const double BOOTSTRAP_SOURCE_WINDOW_MS = 30000.0;

        /// <summary>
        /// Raggio temporale attorno all'offset iniziale previsto per il bootstrap
        /// </summary>
        private const double BOOTSTRAP_OFFSET_RADIUS_MS = 30000.0;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce una sola volta le timeline e produce l'allineamento temporale definitivo
        /// </summary>
        /// <param name="sourceBuilder">Builder della timeline source</param>
        /// <param name="languageBuilder">Builder della timeline language</param>
        /// <param name="matcher">Backend di matching SIFT</param>
        /// <param name="editMapBuilder">Builder della mappa di montaggio</param>
        /// <param name="sourcePath">Percorso del file source</param>
        /// <param name="languagePath">Percorso del file language</param>
        /// <param name="sourceCropPx">Crop manuale source</param>
        /// <param name="languageCropPx">Crop manuale language</param>
        /// <param name="stretchFactor">Fattore di stretch serializzato</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="maximumParallelism">Parallelismo massimo consentito</param>
        /// <param name="cancellationToken">Token di annullamento cooperativo</param>
        /// <returns>Risultato dell'unico percorso temporale</returns>
        public DeepSiftTemporalAlignmentStageResult Align(DeepSiftAnchorTimelineBuilder sourceBuilder, DeepSiftAnchorTimelineBuilder languageBuilder, FrameFeatureBatchMatcherBase matcher, DeepSiftEditMapBuilder editMapBuilder, string sourcePath, string languagePath, string sourceCropPx, string languageCropPx, string stretchFactor, double scale, int maximumParallelism, CancellationToken cancellationToken)
        {
            this.ValidateArguments(sourceBuilder, languageBuilder, matcher, editMapBuilder, sourcePath, languagePath, scale, maximumParallelism);
            DeepSiftTemporalAlignmentStageResult result = new DeepSiftTemporalAlignmentStageResult();
            this.BuildTimelines(sourceBuilder, languageBuilder, sourcePath, languagePath, sourceCropPx, languageCropPx, maximumParallelism, cancellationToken, out DeepSiftAnchorTimeline sourceTimeline, out DeepSiftAnchorTimeline languageTimeline);
            result.SourceTimeline = sourceTimeline;
            result.LanguageTimeline = languageTimeline;
            result.AppliedScale = scale;

            List<DeepSiftVisualAnchor> bootstrapSourceAnchors = this.SelectLeadingAnchors(sourceTimeline.Anchors, 180000.0);
            List<DeepSiftVisualAnchor> bootstrapLanguageAnchors = this.SelectLeadingAnchors(languageTimeline.Anchors, 210000.0);
            DeepSiftBatchMatchResult bootstrapBatch = this.BuildBootstrapBatch(bootstrapSourceAnchors, bootstrapLanguageAnchors, matcher, scale, maximumParallelism, cancellationToken);
            result.Batch = bootstrapBatch;
            if (bootstrapBatch == null)
                return this.Reject(result, AppText.T("deep.temporal.aligner.bootstrapUnavailable"));
            if (bootstrapBatch.Cancelled)
                throw new OperationCanceledException(cancellationToken);
            List<DeepSiftAcceptedPairDiagnostic> bootstrapEvidence = this.SelectNonBlackPairs(bootstrapBatch.AcceptedPairs, sourceTimeline.BlackRuns, languageTimeline.BlackRuns);
            if (!string.IsNullOrEmpty(bootstrapBatch.RejectReason) || bootstrapEvidence.Count == 0)
                return this.Reject(result, string.IsNullOrEmpty(bootstrapBatch.RejectReason) ? AppText.T("deep.temporal.aligner.bootstrapWithoutAcceptedMatches") : bootstrapBatch.RejectReason);

            if (!this.TryResolveInitialOffset(bootstrapEvidence, scale, out double bootstrapOffsetMs))
                return this.Reject(result, AppText.T("deep.temporal.aligner.bootstrapWithoutSupportedCluster"));

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, AppText.F("deep.temporal.log.bootstrapOffset", bootstrapOffsetMs.ToString("F1", CultureInfo.InvariantCulture)));
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, AppText.T("deep.temporal.log.phaseGlobalEvidence"));
            ConsoleHelper.Progress(LogSection.Deep, 52, AppText.T("deep.temporal.progress.globalEvidence"));
            result.Temporal = this.Track(sourceTimeline.Anchors, languageTimeline.Anchors, sourceTimeline.BlackRuns, languageTimeline.BlackRuns, bootstrapEvidence, matcher, bootstrapOffsetMs, scale, maximumParallelism, cancellationToken);
            if (result.Temporal == null || !result.Temporal.Accepted)
                return this.Reject(result, result.Temporal != null ? result.Temporal.RejectReason : AppText.T("deep.temporal.aligner.topologyWithoutResult"));
            DeepSiftVisualAnchorBufferHelper.ReleaseFrames(sourceTimeline.Anchors);
            DeepSiftVisualAnchorBufferHelper.ReleaseFrames(languageTimeline.Anchors);
            result.EditMapResult = editMapBuilder.BuildTemporal(sourcePath, languagePath, sourceCropPx, languageCropPx, stretchFactor, scale, sourceTimeline, languageTimeline, result.Temporal, cancellationToken);
            result.Accepted = result.EditMapResult != null && result.EditMapResult.Success;
            result.RejectReason = result.Accepted ? "" : (result.EditMapResult != null ? result.EditMapResult.RejectReason : AppText.T("deep.temporal.aligner.adaptiveEditMapFailed"));
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Valida le dipendenze e gli input pubblici prima di allocare le timeline o avviare il matcher
        /// </summary>
        /// <param name="sourceBuilder">Builder della timeline source</param>
        /// <param name="languageBuilder">Builder della timeline language</param>
        /// <param name="matcher">Backend SIFT usato per confrontare le finestre</param>
        /// <param name="editMapBuilder">Builder della EditMap finale</param>
        /// <param name="sourcePath">Percorso del file source</param>
        /// <param name="languagePath">Percorso del file language</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="maximumParallelism">Parallelismo massimo consentito</param>
        private void ValidateArguments(DeepSiftAnchorTimelineBuilder sourceBuilder, DeepSiftAnchorTimelineBuilder languageBuilder, FrameFeatureBatchMatcherBase matcher, DeepSiftEditMapBuilder editMapBuilder, string sourcePath, string languagePath, double scale, int maximumParallelism)
        {
            if (sourceBuilder == null)
                throw new ArgumentNullException(nameof(sourceBuilder));
            if (languageBuilder == null)
                throw new ArgumentNullException(nameof(languageBuilder));
            if (matcher == null)
                throw new ArgumentNullException(nameof(matcher));
            if (editMapBuilder == null)
                throw new ArgumentNullException(nameof(editMapBuilder));
            if (string.IsNullOrEmpty(sourcePath))
                throw new ArgumentException(AppText.T("deep.temporal.argument.missingSourcePath"), nameof(sourcePath));
            if (string.IsNullOrEmpty(languagePath))
                throw new ArgumentException(AppText.T("deep.temporal.argument.missingLanguagePath"), nameof(languagePath));
            if (!double.IsFinite(scale) || scale <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(scale));
            if (maximumParallelism < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumParallelism));
        }

        /// <summary>
        /// Seleziona le ancore iniziali fino al limite temporale del bootstrap
        /// </summary>
        /// <param name="anchors">Timeline di ancore ordinata per PTS</param>
        /// <param name="maximumPtsMs">Limite PTS incluso nella selezione</param>
        /// <returns>Ancore comprese nella finestra iniziale</returns>
        private List<DeepSiftVisualAnchor> SelectLeadingAnchors(IReadOnlyList<DeepSiftVisualAnchor> anchors, double maximumPtsMs)
        {
            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>();
            for (int anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
            {
                if (anchors[anchorIndex].PtsMs > maximumPtsMs)
                    break;
                result.Add(anchors[anchorIndex]);
            }
            return result;
        }

        /// <summary>
        /// Costruisce batch locali nel corridoio temporale dell'offset iniziale, mantenendo separate le finestre source e language
        /// </summary>
        /// <param name="sourceAnchors">Ancore source candidate per il bootstrap</param>
        /// <param name="languageAnchors">Ancore language candidate per il bootstrap</param>
        /// <param name="matcher">Backend SIFT usato per costruire i batch</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="maximumParallelism">Parallelismo massimo consentito dal matcher</param>
        /// <param name="cancellationToken">Token di annullamento cooperativo</param>
        /// <returns>Batch aggregato del bootstrap con match e diagnostica backend</returns>
        private DeepSiftBatchMatchResult BuildBootstrapBatch(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors, FrameFeatureBatchMatcherBase matcher, double scale, int maximumParallelism, CancellationToken cancellationToken)
        {
            DeepSiftBatchMatchResult result = new DeepSiftBatchMatchResult();
            result.BackendName = matcher.BackendName;
            result.DeclaredSourceAnchorCount = sourceAnchors.Count;
            result.DeclaredLanguageAnchorCount = languageAnchors.Count;
            result.SourceAnchors = new List<DeepSiftVisualAnchor>();
            result.LanguageAnchors = new List<DeepSiftVisualAnchor>();
            if (sourceAnchors.Count == 0 || languageAnchors.Count == 0)
                return result;

            int sourceStartIndex = 0;
            while (sourceStartIndex < sourceAnchors.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double sourceStartPtsMs = sourceAnchors[sourceStartIndex].PtsMs;
                double sourceEndPtsMs = sourceStartPtsMs + BOOTSTRAP_SOURCE_WINDOW_MS;
                int sourceEndIndex = this.FindFirstAnchorAtOrAfter(sourceAnchors, sourceEndPtsMs);
                if (sourceEndIndex <= sourceStartIndex)
                    sourceEndIndex = sourceStartIndex + 1;
                List<DeepSiftVisualAnchor> sourceWindow = this.SelectAnchorRange(sourceAnchors, sourceStartIndex, sourceEndIndex);

                double languageStartPtsMs = Math.Max(0.0, (sourceStartPtsMs - BOOTSTRAP_OFFSET_RADIUS_MS) * scale);
                double languageEndPtsMs = (sourceAnchors[sourceEndIndex - 1].PtsMs + BOOTSTRAP_OFFSET_RADIUS_MS) * scale;
                int languageStartIndex = this.FindFirstAnchorAtOrAfter(languageAnchors, languageStartPtsMs);
                int languageEndIndex = this.FindFirstAnchorAtOrAfter(languageAnchors, languageEndPtsMs);
                if (languageEndIndex < languageAnchors.Count && languageAnchors[languageEndIndex].PtsMs <= languageEndPtsMs)
                    languageEndIndex++;
                if (languageEndIndex <= languageStartIndex)
                {
                    sourceStartIndex = sourceEndIndex;
                    continue;
                }
                List<DeepSiftVisualAnchor> languageWindow = this.SelectAnchorRange(languageAnchors, languageStartIndex, languageEndIndex);
                List<DeepSiftFramePair> plannedPairs = this.BuildOffsetBandPairs(sourceWindow, languageWindow, 0.0, scale, BOOTSTRAP_OFFSET_RADIUS_MS);
                if (plannedPairs.Count == 0)
                {
                    sourceStartIndex = sourceEndIndex;
                    continue;
                }

                DeepSiftBatchMatchResult batch = matcher.BuildMatrix(sourceWindow, languageWindow, maximumParallelism, cancellationToken, null, plannedPairs);
                this.AppendBootstrapBatch(result, batch, sourceStartIndex, languageStartIndex);
                if (result.Cancelled || !string.IsNullOrEmpty(result.RejectReason))
                    return result;
                sourceStartIndex = sourceEndIndex;
            }
            result.SourceAnchorCount = Math.Max(0, result.DeclaredSourceAnchorCount - result.SourceFeaturelessAnchorCount);
            result.LanguageAnchorCount = Math.Max(0, result.DeclaredLanguageAnchorCount - result.LanguageFeaturelessAnchorCount);
            result.AcceptedCellCount = result.AcceptedPairs.Count;
            return result;
        }

        /// <summary>
        /// Seleziona l'intervallo di ancore richiesto mantenendo gli indici relativi al batch
        /// </summary>
        /// <param name="anchors">Timeline completa da suddividere</param>
        /// <param name="startIndex">Indice iniziale incluso</param>
        /// <param name="endIndex">Indice finale escluso</param>
        /// <returns>Ancore comprese nell'intervallo richiesto</returns>
        private List<DeepSiftVisualAnchor> SelectAnchorRange(IReadOnlyList<DeepSiftVisualAnchor> anchors, int startIndex, int endIndex)
        {
            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>(Math.Max(0, endIndex - startIndex));
            for (int anchorIndex = startIndex; anchorIndex < endIndex; anchorIndex++)
                result.Add(anchors[anchorIndex]);
            return result;
        }

        /// <summary>
        /// Accumula un batch di bootstrap nel risultato aggregato rimappando gli indici sulle timeline complete
        /// </summary>
        /// <param name="target">Risultato aggregato del bootstrap</param>
        /// <param name="source">Batch locale da incorporare</param>
        /// <param name="sourceIndexOffset">Primo indice source della finestra locale</param>
        /// <param name="languageIndexOffset">Primo indice language della finestra locale</param>
        private void AppendBootstrapBatch(DeepSiftBatchMatchResult target, DeepSiftBatchMatchResult source, int sourceIndexOffset, int languageIndexOffset)
        {
            if (source == null)
            {
                target.RejectReason = AppText.T("deep.temporal.aligner.bootstrapUnavailable");
                return;
            }
            target.Cancelled |= source.Cancelled;
            if (!string.IsNullOrEmpty(source.RejectReason))
            {
                target.RejectReason = source.RejectReason;
                return;
            }
            for (int pairIndex = 0; pairIndex < source.AcceptedPairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = source.AcceptedPairs[pairIndex];
                pair.SourceAnchorIndex += sourceIndexOffset;
                pair.LanguageAnchorIndex += languageIndexOffset;
                target.AcceptedPairs.Add(pair);
            }
            target.WorkerCount = Math.Max(target.WorkerCount, source.WorkerCount);
            target.ProcessedCellCount += source.ProcessedCellCount;
            target.MatrixSizeBytes = Math.Max(target.MatrixSizeBytes, source.MatrixSizeBytes);
            target.PeakWorkingSetBytes = Math.Max(target.PeakWorkingSetBytes, source.PeakWorkingSetBytes);
            target.CompletedTileCount += source.CompletedTileCount;
            target.FeatureExtractionMs += source.FeatureExtractionMs;
            target.MatchingMs += source.MatchingMs;
            target.DescriptorMatchingMs += source.DescriptorMatchingMs;
            target.GeometryMs += source.GeometryMs;
            target.VulkanDeviceName = string.IsNullOrEmpty(target.VulkanDeviceName) ? source.VulkanDeviceName : target.VulkanDeviceName;
            target.UploadMs += source.UploadMs;
            target.KernelMs += source.KernelMs;
            target.GpuUploadMs += source.GpuUploadMs;
            target.GpuNormalizeMs += source.GpuNormalizeMs;
            target.GpuGaussianPyramidMs += source.GpuGaussianPyramidMs;
            target.GpuExtremaMs += source.GpuExtremaMs;
            target.GpuOrientationMs += source.GpuOrientationMs;
            target.GpuDescriptorMs += source.GpuDescriptorMs;
            target.GpuMatchingMs += source.GpuMatchingMs;
            target.GpuRansacMs += source.GpuRansacMs;
            target.HostWaitMs += source.HostWaitMs;
            target.PeakVramBytes = Math.Max(target.PeakVramBytes, source.PeakVramBytes);
            target.ReadbackMs += source.ReadbackMs;
            target.SubmitCount += source.SubmitCount;
            target.DispatchCount += source.DispatchCount;
            target.WaitCount += source.WaitCount;
            target.CandidateKeypointCount += source.CandidateKeypointCount;
            target.RefinedKeypointCount += source.RefinedKeypointCount;
            target.DescriptorCount += source.DescriptorCount;
            target.TruncatedKeypointCount += source.TruncatedKeypointCount;
        }

        /// <summary>
        /// Costruisce in parallelo le due timeline uniformi una sola volta
        /// </summary>
        /// <param name="sourceBuilder">Builder della timeline source</param>
        /// <param name="languageBuilder">Builder della timeline language</param>
        /// <param name="sourcePath">Percorso del file source</param>
        /// <param name="languagePath">Percorso del file language</param>
        /// <param name="sourceCropPx">Crop manuale source</param>
        /// <param name="languageCropPx">Crop manuale language</param>
        /// <param name="maximumParallelism">Parallelismo massimo usato per la costruzione</param>
        /// <param name="cancellationToken">Token di annullamento cooperativo</param>
        /// <param name="sourceTimeline">Timeline source costruita</param>
        /// <param name="languageTimeline">Timeline language costruita</param>
        private void BuildTimelines(DeepSiftAnchorTimelineBuilder sourceBuilder, DeepSiftAnchorTimelineBuilder languageBuilder, string sourcePath, string languagePath, string sourceCropPx, string languageCropPx, int maximumParallelism, CancellationToken cancellationToken, out DeepSiftAnchorTimeline sourceTimeline, out DeepSiftAnchorTimeline languageTimeline)
        {
            DeepSiftAnchorTimeline source = null;
            DeepSiftAnchorTimeline language = null;
            System.Threading.Tasks.ParallelOptions options = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(2, Math.Max(1, maximumParallelism)),
                CancellationToken = cancellationToken
            };
            System.Threading.Tasks.Parallel.Invoke(options,
                () => source = sourceBuilder.BuildUniform(sourcePath, sourceCropPx),
                () => language = languageBuilder.BuildUniform(languagePath, languageCropPx));
            sourceTimeline = source;
            languageTimeline = language;
        }

        /// <summary>
        /// Marca il risultato come rifiutato assegnando un motivo localizzato quando necessario
        /// </summary>
        /// <param name="result">Risultato da marcare come rifiutato</param>
        /// <param name="reason">Motivo del rifiuto oppure stringa vuota</param>
        /// <returns>Risultato marcato come rifiutato</returns>
        private DeepSiftTemporalAlignmentStageResult Reject(DeepSiftTemporalAlignmentStageResult result, string reason)
        {
            result.Accepted = false;
            result.RejectReason = string.IsNullOrEmpty(reason) ? AppText.T("deep.temporal.aligner.rejected") : reason;
            return result;
        }

        #endregion
    }

    /// <summary>
    /// Risultato dello stage temporale unico di DeepAnalysis
    /// </summary>
    internal sealed class DeepSiftTemporalAlignmentStageResult
    {
        /// <summary>
        /// True quando allineamento e costruzione EditMap sono riusciti
        /// </summary>
        public bool Accepted { get; set; }

        /// <summary>
        /// Motivo del rifiuto oppure stringa vuota in caso di successo
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Scala temporale source-language applicata dal percorso
        /// </summary>
        public double AppliedScale { get; set; }

        /// <summary>
        /// Timeline source usata dal matcher
        /// </summary>
        public DeepSiftAnchorTimeline SourceTimeline { get; set; }

        /// <summary>
        /// Timeline language usata dal matcher
        /// </summary>
        public DeepSiftAnchorTimeline LanguageTimeline { get; set; }

        /// <summary>
        /// Batch del bootstrap conservato per la diagnostica del backend
        /// </summary>
        public DeepSiftBatchMatchResult Batch { get; set; }

        /// <summary>
        /// Evidenza temporale globale con supporti e regioni candidate non ancora convertite in operazioni
        /// </summary>
        public DeepSiftTemporalEvidenceResult Temporal { get; set; }

        /// <summary>
        /// EditMap rifinita ai boundary visuali
        /// </summary>
        public DeepSiftEditMapResult EditMapResult { get; set; }
    }
}
