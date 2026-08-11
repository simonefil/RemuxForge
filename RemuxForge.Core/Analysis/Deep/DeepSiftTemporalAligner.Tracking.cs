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
    /// Traccia l'offset visuale lungo l'intera timeline mediante finestre SIFT locali
    /// </summary>
    internal sealed partial class DeepSiftTemporalAligner
    {
        #region Costanti

        private const double CHECKPOINT_PERIOD_MS = 30000.0;
        private const double SOURCE_WINDOW_HALF_WIDTH_MS = CHECKPOINT_PERIOD_MS * 0.5;
        private const double OFFSET_SEARCH_RADIUS_MS = 15000.0;
        private const double LANGUAGE_WINDOW_HALF_WIDTH_MS = SOURCE_WINDOW_HALF_WIDTH_MS + OFFSET_SEARCH_RADIUS_MS;
        private const double OFFSET_CLUSTER_TOLERANCE_MS = 125.0;
        private const double SAMPLE_PERIOD_MS = 500.0;
        private const int MINIMUM_DISTINCT_SOURCE_SUPPORT = 3;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Risolve l'offset iniziale dal cluster SIFT con maggior supporto source distinto
        /// </summary>
        public bool TryResolveInitialOffset(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, double sourceToLanguageScale, out double offsetMs)
        {
            if (this.TryResolveObservation(pairs, sourceToLanguageScale, 0.0, 30000.0, 0, out DeepSiftAcceptedPairDiagnostic observation))
            {
                offsetMs = observation.SourcePtsMs - (observation.LanguagePtsMs / sourceToLanguageScale);
                return true;
            }
            offsetMs = 0.0;
            return false;
        }

        /// <summary>
        /// Costruisce una topologia temporale lineare senza usare timestamp annotati
        /// </summary>
        public DeepSiftTemporalTrackingResult Track(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors, IReadOnlyList<DeepSiftAcceptedPairDiagnostic> bootstrapPairs, FrameFeatureBatchMatcherBase matcher, double initialOffsetMs, double sourceToLanguageScale, int maximumParallelism, CancellationToken cancellationToken)
        {
            if (sourceAnchors == null)
                throw new ArgumentNullException(nameof(sourceAnchors));
            if (languageAnchors == null)
                throw new ArgumentNullException(nameof(languageAnchors));
            if (bootstrapPairs == null)
                throw new ArgumentNullException(nameof(bootstrapPairs));
            if (matcher == null)
                throw new ArgumentNullException(nameof(matcher));
            if (sourceAnchors.Count == 0 || languageAnchors.Count == 0)
                throw new ArgumentException("Timeline SIFT vuota");

            List<DeepSiftBatchMatchResult> batches = new List<DeepSiftBatchMatchResult>();
            List<DeepSiftAcceptedPairDiagnostic> observations = new List<DeepSiftAcceptedPairDiagnostic>();
            double currentOffsetMs = initialOffsetMs;
            double sourceDurationMs = this.GetTimelineEndMs(sourceAnchors);
            double languageDurationMs = this.GetTimelineEndMs(languageAnchors);
            double bootstrapEndMs = this.GetEvidenceEndMs(bootstrapPairs);
            double firstCheckpointMs = Math.Min(SOURCE_WINDOW_HALF_WIDTH_MS, sourceDurationMs * 0.5);
            int observationIndex = 0;
            int initialEvidenceCount = Math.Min(3, sourceAnchors.Count);
            for (int initialIndex = 0; initialIndex < initialEvidenceCount; initialIndex++)
            {
                DeepSiftVisualAnchor anchor = sourceAnchors[initialIndex];
                DeepSiftAcceptedPairDiagnostic initialObservation = new DeepSiftAcceptedPairDiagnostic();
                initialObservation.SourceAnchorIndex = observationIndex;
                initialObservation.LanguageAnchorIndex = observationIndex;
                initialObservation.SourcePtsMs = anchor.PtsMs;
                initialObservation.LanguagePtsMs = (anchor.PtsMs - initialOffsetMs) * sourceToLanguageScale;
                initialObservation.SourceFrameDurationMs = anchor.FrameDurationMs;
                initialObservation.LanguageFrameDurationMs = anchor.FrameDurationMs * sourceToLanguageScale;
                initialObservation.Score = 1.0;
                observations.Add(initialObservation);
                observationIndex++;
            }
            double pendingOffsetMs = double.NaN;
            int pendingOffsetCount = 0;
            int pendingObservationStartIndex = -1;
            for (double checkpointMs = firstCheckpointMs; checkpointMs < sourceDurationMs; checkpointMs += CHECKPOINT_PERIOD_MS)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double sourceStartMs = Math.Max(0.0, checkpointMs - SOURCE_WINDOW_HALF_WIDTH_MS);
                double sourceEndMs = Math.Min(sourceDurationMs, checkpointMs + SOURCE_WINDOW_HALF_WIDTH_MS);
                DeepSiftAcceptedPairDiagnostic observation;
                if (sourceEndMs <= bootstrapEndMs)
                {
                    List<DeepSiftAcceptedPairDiagnostic> bootstrapWindow = this.SelectWindowPairs(bootstrapPairs, sourceStartMs, sourceEndMs);
                    if (!this.TryResolveObservation(bootstrapWindow, sourceToLanguageScale, currentOffsetMs, OFFSET_SEARCH_RADIUS_MS, observationIndex, out observation))
                        continue;
                }
                else
                {
                    double languageCenterMs = (checkpointMs - currentOffsetMs) * sourceToLanguageScale;
                    double languageStartMs = Math.Max(0.0, languageCenterMs - LANGUAGE_WINDOW_HALF_WIDTH_MS);
                    double languageEndMs = Math.Min(languageDurationMs, languageCenterMs + LANGUAGE_WINDOW_HALF_WIDTH_MS);
                    List<DeepSiftVisualAnchor> sourceWindow = this.SelectWindowAnchors(sourceAnchors, sourceStartMs, sourceEndMs);
                    List<DeepSiftVisualAnchor> languageWindow = this.SelectAllWindowAnchors(languageAnchors, languageStartMs, languageEndMs);
                    if (sourceWindow.Count == 0 || languageWindow.Count == 0)
                        continue;

                    List<DeepSiftFramePair> plannedPairs = this.BuildOffsetBandPairs(sourceWindow, languageWindow, currentOffsetMs, sourceToLanguageScale, OFFSET_SEARCH_RADIUS_MS);
                    if (plannedPairs.Count == 0)
                        continue;
                    DeepSiftBatchMatchResult batch = matcher.BuildMatrix(sourceWindow, languageWindow, maximumParallelism, cancellationToken, null, plannedPairs);
                    batches.Add(batch);
                    if (batch.Cancelled)
                        throw new OperationCanceledException(cancellationToken);
                    if (!string.IsNullOrEmpty(batch.RejectReason) || !this.TryResolveObservation(batch.AcceptedPairs, sourceToLanguageScale, currentOffsetMs, OFFSET_SEARCH_RADIUS_MS, observationIndex, out observation))
                        continue;
                }

                double observedOffsetMs = observation.SourcePtsMs - (observation.LanguagePtsMs / sourceToLanguageScale);
                if (Math.Abs(observedOffsetMs - currentOffsetMs) <= 500.0)
                {
                    pendingOffsetMs = double.NaN;
                    pendingOffsetCount = 0;
                    pendingObservationStartIndex = -1;
                    observation.LanguagePtsMs = (observation.SourcePtsMs - currentOffsetMs) * sourceToLanguageScale;
                }
                else
                {
                    if (double.IsNaN(pendingOffsetMs) || Math.Abs(observedOffsetMs - pendingOffsetMs) > 300.0)
                    {
                        pendingOffsetMs = observedOffsetMs;
                        pendingOffsetCount = 1;
                        pendingObservationStartIndex = observations.Count;
                    }
                    else
                    {
                        pendingOffsetMs = ((pendingOffsetMs * pendingOffsetCount) + observedOffsetMs) / (pendingOffsetCount + 1);
                        pendingOffsetCount++;
                    }

                    if (pendingOffsetCount >= 3)
                    {
                        currentOffsetMs = pendingOffsetMs;
                        this.ApplyOffset(observations, pendingObservationStartIndex, currentOffsetMs, sourceToLanguageScale);
                        pendingOffsetMs = double.NaN;
                        pendingOffsetCount = 0;
                        pendingObservationStartIndex = -1;
                    }
                    observation.LanguagePtsMs = (observation.SourcePtsMs - currentOffsetMs) * sourceToLanguageScale;
                }
                observations.Add(observation);
                observationIndex++;
            }

            DeepSiftTemporalEvidenceOptions options = new DeepSiftTemporalEvidenceOptions();
            options.SupportWindowMatchCount = 3;
            DeepSiftTemporalEvidenceResult temporal = new DeepSiftTemporalEvidenceSolver(options).Solve(observations, sourceToLanguageScale);
            temporal.InputEvidenceCount = observations.Count;
            long pairCount = 0;
            for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++)
                pairCount += batches[batchIndex].ProcessedCellCount;
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  tracking lineare: pair=" + pairCount.ToString(CultureInfo.InvariantCulture) + ", checkpoint=" + observations.Count.ToString(CultureInfo.InvariantCulture) + ", plateau=" + temporal.Plateaus.Count.ToString(CultureInfo.InvariantCulture) + ", offset=" + this.SummarizeOffsets(temporal));
            return new DeepSiftTemporalTrackingResult(batches, temporal);
        }

        #endregion

        #region Metodi privati

        private List<DeepSiftVisualAnchor> SelectWindowAnchors(IReadOnlyList<DeepSiftVisualAnchor> anchors, double startMs, double endMs)
        {
            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>();
            long previousRegularBucket = long.MinValue;
            for (int anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
            {
                DeepSiftVisualAnchor anchor = anchors[anchorIndex];
                if (anchor.PtsMs < startMs)
                    continue;
                if (anchor.PtsMs >= endMs)
                    break;
                long bucket = (long)Math.Floor(anchor.PtsMs / SAMPLE_PERIOD_MS);
                if (bucket == previousRegularBucket)
                    continue;
                previousRegularBucket = bucket;
                if (result.Count == 0 || !ReferenceEquals(result[result.Count - 1], anchor))
                    result.Add(anchor);
            }
            return result;
        }

        private List<DeepSiftVisualAnchor> SelectAllWindowAnchors(IReadOnlyList<DeepSiftVisualAnchor> anchors, double startMs, double endMs)
        {
            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>();
            for (int anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
            {
                if (anchors[anchorIndex].PtsMs < startMs)
                    continue;
                if (anchors[anchorIndex].PtsMs >= endMs)
                    break;
                result.Add(anchors[anchorIndex]);
            }
            return result;
        }

        private List<DeepSiftFramePair> BuildOffsetBandPairs(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors, double expectedOffsetMs, double scale, double radiusMs)
        {
            List<DeepSiftFramePair> result = new List<DeepSiftFramePair>();
            for (int sourceIndex = 0; sourceIndex < sourceAnchors.Count; sourceIndex++)
            {
                double minimumLanguagePtsMs = (sourceAnchors[sourceIndex].PtsMs - expectedOffsetMs - radiusMs) * scale;
                double maximumLanguagePtsMs = (sourceAnchors[sourceIndex].PtsMs - expectedOffsetMs + radiusMs) * scale;
                int languageIndex = this.FindFirstAnchorAtOrAfter(languageAnchors, minimumLanguagePtsMs);
                while (languageIndex < languageAnchors.Count && languageAnchors[languageIndex].PtsMs <= maximumLanguagePtsMs)
                {
                    result.Add(new DeepSiftFramePair { SourceAnchorIndex = sourceIndex, LanguageAnchorIndex = languageIndex });
                    languageIndex++;
                }
            }
            return result;
        }

        private int FindFirstAnchorAtOrAfter(IReadOnlyList<DeepSiftVisualAnchor> anchors, double ptsMs)
        {
            int low = 0;
            int high = anchors.Count;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (anchors[middle].PtsMs < ptsMs)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        private double GetTimelineEndMs(IReadOnlyList<DeepSiftVisualAnchor> anchors)
        {
            DeepSiftVisualAnchor last = anchors[anchors.Count - 1];
            return last.PtsMs + Math.Max(last.DurationMs, last.FrameDurationMs);
        }

        private double GetEvidenceEndMs(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs)
        {
            double endMs = 0.0;
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
                endMs = Math.Max(endMs, pairs[pairIndex].SourcePtsMs);
            return endMs;
        }

        /// <summary>
        /// Consolida sul nuovo plateau le osservazioni raccolte durante la conferma
        /// </summary>
        private void ApplyOffset(List<DeepSiftAcceptedPairDiagnostic> observations, int startIndex, double offsetMs, double scale)
        {
            for (int observationIndex = Math.Max(0, startIndex); observationIndex < observations.Count; observationIndex++)
            {
                DeepSiftAcceptedPairDiagnostic observation = observations[observationIndex];
                observation.LanguagePtsMs = (observation.SourcePtsMs - offsetMs) * scale;
            }
        }

        private List<DeepSiftAcceptedPairDiagnostic> SelectWindowPairs(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, double startMs, double endMs)
        {
            List<DeepSiftAcceptedPairDiagnostic> selected = new List<DeepSiftAcceptedPairDiagnostic>();
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                if (pair.SourcePtsMs >= startMs && pair.SourcePtsMs <= endMs)
                    selected.Add(pair);
            }
            return selected;
        }

        private bool TryResolveObservation(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, double scale, double expectedOffsetMs, double searchRadiusMs, int observationIndex, out DeepSiftAcceptedPairDiagnostic observation)
        {
            List<OffsetCandidate> candidates = new List<OffsetCandidate>();
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                double offsetMs = pair.SourcePtsMs - (pair.LanguagePtsMs / scale);
                if (Math.Abs(offsetMs - expectedOffsetMs) > searchRadiusMs)
                    continue;
                candidates.Add(new OffsetCandidate(pair, offsetMs));
            }
            candidates.Sort((left, right) => left.OffsetMs.CompareTo(right.OffsetMs));

            OffsetCluster selected = null;
            for (int startIndex = 0; startIndex < candidates.Count; startIndex++)
            {
                OffsetCluster cluster = new OffsetCluster();
                for (int candidateIndex = startIndex; candidateIndex < candidates.Count; candidateIndex++)
                {
                    if (candidates[candidateIndex].OffsetMs - candidates[startIndex].OffsetMs > OFFSET_CLUSTER_TOLERANCE_MS)
                        break;
                    cluster.Add(candidates[candidateIndex]);
                }
                if (cluster.SourceIndexes.Count < MINIMUM_DISTINCT_SOURCE_SUPPORT)
                    continue;
                if (selected == null || cluster.SourceIndexes.Count > selected.SourceIndexes.Count ||
                    (cluster.SourceIndexes.Count == selected.SourceIndexes.Count && (cluster.LanguageIndexes.Count > selected.LanguageIndexes.Count ||
                    (cluster.LanguageIndexes.Count == selected.LanguageIndexes.Count && (cluster.TotalScore > selected.TotalScore ||
                    (Math.Abs(cluster.TotalScore - selected.TotalScore) <= 0.000001 && Math.Abs(cluster.OffsetMs - expectedOffsetMs) < Math.Abs(selected.OffsetMs - expectedOffsetMs)))))))
                    selected = cluster;
            }
            if (selected == null)
            {
                observation = null;
                return false;
            }

            OffsetCandidate representative = selected.Candidates[0];
            double bestDistanceMs = Math.Abs(representative.OffsetMs - selected.OffsetMs);
            for (int candidateIndex = 1; candidateIndex < selected.Candidates.Count; candidateIndex++)
            {
                OffsetCandidate candidate = selected.Candidates[candidateIndex];
                double distanceMs = Math.Abs(candidate.OffsetMs - selected.OffsetMs);
                if (distanceMs < bestDistanceMs || (Math.Abs(distanceMs - bestDistanceMs) <= 0.001 && candidate.Pair.Score > representative.Pair.Score))
                {
                    representative = candidate;
                    bestDistanceMs = distanceMs;
                }
            }

            observation = new DeepSiftAcceptedPairDiagnostic();
            observation.SourceAnchorIndex = observationIndex;
            observation.LanguageAnchorIndex = observationIndex;
            observation.SourcePtsMs = representative.Pair.SourcePtsMs;
            observation.LanguagePtsMs = (representative.Pair.SourcePtsMs - selected.OffsetMs) * scale;
            observation.SourceFrameDurationMs = representative.Pair.SourceFrameDurationMs;
            observation.LanguageFrameDurationMs = representative.Pair.LanguageFrameDurationMs;
            observation.Score = representative.Pair.Score;
            observation.InlierCount = representative.Pair.InlierCount;
            observation.InlierRatio = representative.Pair.InlierRatio;
            observation.SourceCoverage = representative.Pair.SourceCoverage;
            observation.LanguageCoverage = representative.Pair.LanguageCoverage;
            observation.MeanReprojectionError = representative.Pair.MeanReprojectionError;
            return true;
        }

        private string SummarizeOffsets(DeepSiftTemporalEvidenceResult result)
        {
            if (result == null || result.Plateaus.Count == 0)
                return "-";
            List<string> values = new List<string>(result.Plateaus.Count);
            for (int plateauIndex = 0; plateauIndex < result.Plateaus.Count; plateauIndex++)
                values.Add(result.Plateaus[plateauIndex].OffsetMs.ToString("F1", CultureInfo.InvariantCulture));
            return string.Join("/", values);
        }

        #endregion

        #region Classi annidate

        private sealed class OffsetCandidate
        {
            public OffsetCandidate(DeepSiftAcceptedPairDiagnostic pair, double offsetMs)
            {
                this.Pair = pair;
                this.OffsetMs = offsetMs;
            }

            public DeepSiftAcceptedPairDiagnostic Pair { get; }
            public double OffsetMs { get; }
        }

        private sealed class OffsetCluster
        {
            public OffsetCluster()
            {
                this.Candidates = new List<OffsetCandidate>();
                this.SourceIndexes = new HashSet<int>();
                this.LanguageIndexes = new HashSet<int>();
            }

            public void Add(OffsetCandidate candidate)
            {
                this.Candidates.Add(candidate);
                this.SourceIndexes.Add(candidate.Pair.SourceAnchorIndex);
                this.LanguageIndexes.Add(candidate.Pair.LanguageAnchorIndex);
                this.TotalScore += candidate.Pair.Score;
                List<double> offsets = new List<double>(this.Candidates.Count);
                for (int candidateIndex = 0; candidateIndex < this.Candidates.Count; candidateIndex++)
                    offsets.Add(this.Candidates[candidateIndex].OffsetMs);
                offsets.Sort();
                int middle = offsets.Count / 2;
                this.OffsetMs = offsets.Count % 2 == 0 ? (offsets[middle - 1] + offsets[middle]) * 0.5 : offsets[middle];
            }

            public List<OffsetCandidate> Candidates { get; }
            public HashSet<int> SourceIndexes { get; }
            public HashSet<int> LanguageIndexes { get; }
            public double TotalScore { get; private set; }
            public double OffsetMs { get; private set; }
        }

        #endregion
    }

    /// <summary>
    /// Risultato del tracking lineare con diagnostica batch completa
    /// </summary>
    internal sealed class DeepSiftTemporalTrackingResult
    {
        /// <summary>
        /// Costruisce il risultato del tracking lineare
        /// </summary>
        /// <param name="batches">Batch elaborati durante il tracking</param>
        /// <param name="temporal">Evidenza temporale consolidata</param>
        public DeepSiftTemporalTrackingResult(IReadOnlyList<DeepSiftBatchMatchResult> batches, DeepSiftTemporalEvidenceResult temporal)
        {
            this.Batches = batches ?? throw new ArgumentNullException(nameof(batches));
            this.Temporal = temporal ?? throw new ArgumentNullException(nameof(temporal));
        }

        /// <summary>
        /// Batch elaborati durante il tracking
        /// </summary>
        public IReadOnlyList<DeepSiftBatchMatchResult> Batches { get; }

        /// <summary>
        /// Evidenza temporale consolidata
        /// </summary>
        public DeepSiftTemporalEvidenceResult Temporal { get; }
    }
}
