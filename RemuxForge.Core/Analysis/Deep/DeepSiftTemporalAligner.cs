using RemuxForge.Core.Analysis.Deep.Features;
using RemuxForge.Core.Infrastructure;
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
            DeepSiftBatchMatchResult bootstrapBatch = matcher.BuildMatrix(bootstrapSourceAnchors, bootstrapLanguageAnchors, maximumParallelism, cancellationToken);
            result.Batch = bootstrapBatch;
            if (bootstrapBatch == null)
                return this.Reject(result, "Bootstrap visuale non disponibile");
            if (bootstrapBatch.Cancelled)
                throw new OperationCanceledException(cancellationToken);
            if (!string.IsNullOrEmpty(bootstrapBatch.RejectReason) || bootstrapBatch.AcceptedPairs.Count == 0)
                return this.Reject(result, string.IsNullOrEmpty(bootstrapBatch.RejectReason) ? "Bootstrap visuale senza match accettati" : bootstrapBatch.RejectReason);

            if (!this.TryResolveInitialOffset(bootstrapBatch.AcceptedPairs, scale, out double bootstrapOffsetMs))
                return this.Reject(result, "Bootstrap visuale senza cluster sostenuto");

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  bootstrap locale: offset=" + bootstrapOffsetMs.ToString("F1", CultureInfo.InvariantCulture) + "ms");
            DeepSiftTemporalTrackingResult tracking = this.Track(sourceTimeline.Anchors, languageTimeline.Anchors, bootstrapBatch.AcceptedPairs, matcher, bootstrapOffsetMs, scale, maximumParallelism, cancellationToken);
            result.Temporal = tracking.Temporal;
            if (result.Temporal == null || !result.Temporal.Accepted)
                return this.Reject(result, result.Temporal != null ? result.Temporal.RejectReason : "Stabilizzazione topologica senza risultato");
            if (tracking.Batches.Count == 0)
                return this.Reject(result, "Tracking lineare senza batch");

            this.ExpandTransitionCorridors(result.Temporal, 10000.0);
            result.EditMapResult = editMapBuilder.BuildTemporal(sourcePath, languagePath, sourceCropPx, languageCropPx, stretchFactor, scale, sourceTimeline, languageTimeline, result.Temporal, cancellationToken);
            result.Accepted = result.EditMapResult != null && result.EditMapResult.Success;
            result.RejectReason = result.Accepted ? "" : (result.EditMapResult != null ? result.EditMapResult.RejectReason : "Costruzione EditMap adattiva fallita");
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Valida le dipendenze e gli input pubblici prima di allocare timeline o matcher
        /// </summary>
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
                throw new ArgumentException("Percorso source mancante", nameof(sourcePath));
            if (string.IsNullOrEmpty(languagePath))
                throw new ArgumentException("Percorso language mancante", nameof(languagePath));
            if (!double.IsFinite(scale) || scale <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(scale));
            if (maximumParallelism < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumParallelism));
        }

        /// <summary>
        /// Seleziona le ancore iniziali comprese nella finestra di bootstrap
        /// </summary>
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
        /// Costruisce in parallelo le due timeline uniformi una sola volta
        /// </summary>
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
        /// Espande i corridoi delle transizioni sulle due scale temporali
        /// </summary>
        private void ExpandTransitionCorridors(DeepSiftTemporalEvidenceResult temporal, double marginMs)
        {
            for (int transitionIndex = 0; transitionIndex < temporal.Transitions.Count; transitionIndex++)
            {
                DeepSiftTemporalTransition transition = temporal.Transitions[transitionIndex];
                transition.LastOldSourcePtsMs = Math.Max(0.0, transition.LastOldSourcePtsMs - marginMs);
                transition.FirstNewSourcePtsMs += marginMs;
                transition.LastOldLanguagePtsMs = Math.Max(0.0, transition.LastOldLanguagePtsMs - marginMs);
                transition.FirstNewLanguagePtsMs += marginMs;
            }
        }

        /// <summary>
        /// Imposta un rifiuto fail-closed senza cambiare algoritmo
        /// </summary>
        private DeepSiftTemporalAlignmentStageResult Reject(DeepSiftTemporalAlignmentStageResult result, string reason)
        {
            result.Accepted = false;
            result.RejectReason = string.IsNullOrEmpty(reason) ? "Allineamento temporale rifiutato" : reason;
            return result;
        }

        #endregion
    }

    /// <summary>
    /// Risultato dell'unico stage temporale DeepAnalysis
    /// </summary>
    internal sealed class DeepSiftTemporalAlignmentStageResult
    {
        /// <summary>
        /// True quando allineamento e costruzione EditMap sono riusciti
        /// </summary>
        public bool Accepted { get; set; }

        /// <summary>
        /// Motivo del rifiuto, vuoto in caso di successo
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Scala temporale applicata dal percorso
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
        /// Batch del bootstrap conservato per diagnostica backend
        /// </summary>
        public DeepSiftBatchMatchResult Batch { get; set; }

        /// <summary>
        /// Catena, plateau e transizioni definitive
        /// </summary>
        public DeepSiftTemporalEvidenceResult Temporal { get; set; }

        /// <summary>
        /// EditMap rifinita ai boundary visuali
        /// </summary>
        public DeepSiftEditMapResult EditMapResult { get; set; }
    }
}
