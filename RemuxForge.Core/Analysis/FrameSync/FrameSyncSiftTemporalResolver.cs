using RemuxForge.Core.Analysis.Deep;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Risolve modi a offset costante dalle coppie SIFT tramite unicità reciproca e percorsi PTS monotoni
    /// </summary>
    internal sealed class FrameSyncSiftTemporalResolver
    {
        #region Costanti

        /// <summary>
        /// Margine minimo fra coppie temporalmente incompatibili
        /// </summary>
        private const double MINIMUM_SCORE_MARGIN = 0.04;

        /// <summary>
        /// Supporto distinto minimo richiesto su entrambi gli assi temporali
        /// </summary>
        private const int MINIMUM_DISTINCT_SUPPORT = 3;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce e ordina i modi temporali sostenuti dalle coppie accettate
        /// </summary>
        /// <param name="pairs">Coppie SIFT accettate geometricamente</param>
        /// <param name="backend">Backend SIFT usato dal batch</param>
        /// <param name="processedPairCount">Numero di coppie elaborate dal matcher</param>
        /// <returns>Modi a offset costante ordinati per affidabilità</returns>
        public List<FrameSyncCandidate> Resolve(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, string backend, long processedPairCount)
        {
            List<TemporalPair> candidates = this.BuildTemporalPairs(pairs);
            List<TemporalCluster> clusters = this.BuildClusters(candidates);
            List<FrameSyncCandidate> result = new List<FrameSyncCandidate>();
            for (int clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
            {
                TemporalCluster cluster = clusters[clusterIndex];
                cluster.ResolveMonotonePath();
                if (cluster.StrongPath.Count < MINIMUM_DISTINCT_SUPPORT)
                    continue;
                result.Add(cluster.ToCandidate(backend, processedPairCount));
            }
            result.Sort(this.CompareCandidates);
            return result;
        }

        /// <summary>
        /// Verifica che il modo migliore domini l'eventuale alternativa temporalmente incompatibile
        /// </summary>
        /// <param name="candidates">Modi ordinati prodotti da <see cref="Resolve"/></param>
        /// <returns>True quando il primo modo è temporalmente univoco</returns>
        public bool IsBestCandidateUnique(IReadOnlyList<FrameSyncCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return false;
            if (candidates.Count == 1)
                return true;

            FrameSyncCandidate best = candidates[0];
            FrameSyncCandidate alternative = candidates[1];
            if (best.StrongPairCount > alternative.StrongPairCount)
                return true;
            if (best.StrongPairCount < alternative.StrongPairCount)
                return false;
            if (DeepSiftTemporalMetricComparer.QuantizeMilliseconds(Math.Min(best.SourceCoverageMs, best.LanguageCoverageMs)) > DeepSiftTemporalMetricComparer.QuantizeMilliseconds(Math.Min(alternative.SourceCoverageMs, alternative.LanguageCoverageMs)))
                return true;
            return DeepSiftTemporalMetricComparer.QuantizeMetric(best.MeanScore - alternative.MeanScore) >= DeepSiftTemporalMetricComparer.QuantizeMetric(MINIMUM_SCORE_MARGIN);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Classifica le coppie valide tramite dominanza reciproca sugli assi source e language
        /// </summary>
        /// <param name="pairs">Coppie SIFT accettate geometricamente</param>
        /// <returns>Coppie temporali con classificazione forte o ambigua</returns>
        private List<TemporalPair> BuildTemporalPairs(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs)
        {
            List<TemporalPair> result = new List<TemporalPair>();
            if (pairs == null || pairs.Count == 0)
                return result;

            Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> bySource = this.BuildIndex(pairs, true);
            Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> byLanguage = this.BuildIndex(pairs, false);
            HashSet<(long SourcePts, long LanguagePts)> manyToMany = DeepSiftTemporalAmbiguityDetector.FindManyToManyPairs(pairs, MINIMUM_SCORE_MARGIN);
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                if (pair == null || pair.Score <= 0.0 || !double.IsFinite(pair.SourcePtsMs) || !double.IsFinite(pair.LanguagePtsMs))
                    continue;
                long sourceKey = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(pair.SourcePtsMs);
                long languageKey = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(pair.LanguagePtsMs);
                double uncertaintyMs = DeepSiftTemporalMetricComparer.GetFinitePairUncertaintyMs(pair, 1.0);
                bool sourceBest = this.IsUniqueTemporalBest(pair, bySource[sourceKey], uncertaintyMs);
                bool languageBest = this.IsUniqueTemporalBest(pair, byLanguage[languageKey], uncertaintyMs);
                TemporalPair candidate = new TemporalPair(pair, pair.SourcePtsMs - pair.LanguagePtsMs, uncertaintyMs);
                candidate.Strong = sourceBest && languageBest && !manyToMany.Contains((sourceKey, languageKey));
                result.Add(candidate);
            }
            result.Sort((left, right) => left.OffsetMs != right.OffsetMs ? left.OffsetMs.CompareTo(right.OffsetMs) : left.Pair.SourcePtsMs.CompareTo(right.Pair.SourcePtsMs));
            return result;
        }

        /// <summary>
        /// Indicizza le coppie per il PTS di uno dei due assi
        /// </summary>
        /// <param name="pairs">Coppie da indicizzare</param>
        /// <param name="sourceAxis">True per l'asse source, false per l'asse language</param>
        /// <returns>Indice delle famiglie temporali</returns>
        private Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> BuildIndex(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, bool sourceAxis)
        {
            Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> result = new Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>>();
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                if (pair == null || !double.IsFinite(pair.SourcePtsMs) || !double.IsFinite(pair.LanguagePtsMs))
                    continue;
                long key = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(sourceAxis ? pair.SourcePtsMs : pair.LanguagePtsMs);
                if (!result.TryGetValue(key, out List<DeepSiftAcceptedPairDiagnostic> values))
                {
                    values = new List<DeepSiftAcceptedPairDiagnostic>();
                    result.Add(key, values);
                }
                values.Add(pair);
            }
            return result;
        }

        /// <summary>
        /// Verifica che nessuna alternativa incompatibile abbia confidence equivalente
        /// </summary>
        /// <param name="candidate">Coppia candidata</param>
        /// <param name="alternatives">Coppie che condividono lo stesso estremo temporale</param>
        /// <param name="candidateUncertaintyMs">Incertezza temporale della candidata</param>
        /// <returns>True quando la candidata domina ogni alternativa incompatibile</returns>
        private bool IsUniqueTemporalBest(DeepSiftAcceptedPairDiagnostic candidate, IReadOnlyList<DeepSiftAcceptedPairDiagnostic> alternatives, double candidateUncertaintyMs)
        {
            double candidateOffsetMs = candidate.SourcePtsMs - candidate.LanguagePtsMs;
            for (int alternativeIndex = 0; alternativeIndex < alternatives.Count; alternativeIndex++)
            {
                DeepSiftAcceptedPairDiagnostic alternative = alternatives[alternativeIndex];
                if (ReferenceEquals(candidate, alternative))
                    continue;
                double alternativeOffsetMs = alternative.SourcePtsMs - alternative.LanguagePtsMs;
                double alternativeUncertaintyMs = DeepSiftTemporalMetricComparer.GetFinitePairUncertaintyMs(alternative, 1.0);
                if (Math.Abs(candidateOffsetMs - alternativeOffsetMs) <= candidateUncertaintyMs + alternativeUncertaintyMs)
                    continue;
                if (!DeepSiftTemporalMetricComparer.HasHigherConfidence(candidate, alternative, MINIMUM_SCORE_MARGIN))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Raggruppa le coppie i cui intervalli di offset si sovrappongono
        /// </summary>
        /// <param name="pairs">Coppie temporali ordinate per offset</param>
        /// <returns>Cluster di offset compatibili</returns>
        private List<TemporalCluster> BuildClusters(IReadOnlyList<TemporalPair> pairs)
        {
            List<TemporalCluster> result = new List<TemporalCluster>();
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                TemporalPair pair = pairs[pairIndex];
                TemporalCluster selected = null;
                double selectedDistanceMs = double.PositiveInfinity;
                for (int clusterIndex = 0; clusterIndex < result.Count; clusterIndex++)
                {
                    double distanceMs = Math.Abs(pair.OffsetMs - result[clusterIndex].OffsetMs);
                    if (distanceMs <= pair.UncertaintyMs + result[clusterIndex].MaximumUncertaintyMs && distanceMs < selectedDistanceMs)
                    {
                        selected = result[clusterIndex];
                        selectedDistanceMs = distanceMs;
                    }
                }
                if (selected == null)
                {
                    selected = new TemporalCluster();
                    result.Add(selected);
                }
                selected.Add(pair);
            }
            return result;
        }

        /// <summary>
        /// Ordina i modi con la stessa gerarchia deterministica del percorso Deep
        /// </summary>
        private int CompareCandidates(FrameSyncCandidate left, FrameSyncCandidate right)
        {
            int comparison = right.StrongPairCount.CompareTo(left.StrongPairCount);
            if (comparison != 0)
                return comparison;
            comparison = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(Math.Min(right.SourceCoverageMs, right.LanguageCoverageMs)).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(Math.Min(left.SourceCoverageMs, left.LanguageCoverageMs)));
            if (comparison != 0)
                return comparison;
            comparison = DeepSiftTemporalMetricComparer.QuantizeMetric(right.MeanScore).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMetric(left.MeanScore));
            return comparison != 0 ? comparison : left.OffsetMs.CompareTo(right.OffsetMs);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Coppia SIFT arricchita con offset, incertezza e classificazione temporale
        /// </summary>
        private sealed class TemporalPair
        {
            /// <summary>
            /// Inizializza la coppia temporale
            /// </summary>
            public TemporalPair(DeepSiftAcceptedPairDiagnostic pair, double offsetMs, double uncertaintyMs)
            {
                this.Pair = pair;
                this.OffsetMs = offsetMs;
                this.UncertaintyMs = uncertaintyMs;
            }

            /// <summary>
            /// Diagnostica SIFT originale
            /// </summary>
            public DeepSiftAcceptedPairDiagnostic Pair { get; }

            /// <summary>
            /// Offset source-language in millisecondi
            /// </summary>
            public double OffsetMs { get; }

            /// <summary>
            /// Semilarghezza dell'intervallo di incertezza PTS
            /// </summary>
            public double UncertaintyMs { get; }

            /// <summary>
            /// Indica che la coppia è reciprocamente univoca
            /// </summary>
            public bool Strong { get; set; }
        }

        /// <summary>
        /// Cluster di coppie con offset compatibili e relativo percorso monotono forte
        /// </summary>
        private sealed class TemporalCluster
        {
            /// <summary>
            /// Inizializza le collezioni del cluster
            /// </summary>
            public TemporalCluster()
            {
                this.Pairs = new List<TemporalPair>();
                this.StrongPath = new List<TemporalPair>();
            }

            /// <summary>
            /// Aggiunge una coppia e aggiorna mediana e incertezza del cluster
            /// </summary>
            public void Add(TemporalPair pair)
            {
                this.Pairs.Add(pair);
                this.MaximumUncertaintyMs = Math.Max(this.MaximumUncertaintyMs, pair.UncertaintyMs);
                this.OffsetMs = this.GetMedianOffset(this.Pairs);
            }

            /// <summary>
            /// Seleziona la sottosequenza forte strettamente crescente su entrambi gli assi PTS
            /// </summary>
            public void ResolveMonotonePath()
            {
                List<TemporalPair> strong = new List<TemporalPair>();
                for (int pairIndex = 0; pairIndex < this.Pairs.Count; pairIndex++)
                {
                    if (this.Pairs[pairIndex].Strong)
                        strong.Add(this.Pairs[pairIndex]);
                }
                strong.Sort((left, right) => left.Pair.SourcePtsMs != right.Pair.SourcePtsMs ? left.Pair.SourcePtsMs.CompareTo(right.Pair.SourcePtsMs) : left.Pair.LanguagePtsMs.CompareTo(right.Pair.LanguagePtsMs));
                if (strong.Count == 0)
                    return;

                int[] lengths = new int[strong.Count];
                double[] scores = new double[strong.Count];
                int[] previous = new int[strong.Count];
                int bestIndex = 0;
                for (int candidateIndex = 0; candidateIndex < strong.Count; candidateIndex++)
                {
                    lengths[candidateIndex] = 1;
                    scores[candidateIndex] = strong[candidateIndex].Pair.Score;
                    previous[candidateIndex] = -1;
                    for (int precedingIndex = 0; precedingIndex < candidateIndex; precedingIndex++)
                    {
                        if (strong[precedingIndex].Pair.SourcePtsMs >= strong[candidateIndex].Pair.SourcePtsMs || strong[precedingIndex].Pair.LanguagePtsMs >= strong[candidateIndex].Pair.LanguagePtsMs)
                            continue;
                        int length = lengths[precedingIndex] + 1;
                        double score = scores[precedingIndex] + strong[candidateIndex].Pair.Score;
                        if (length > lengths[candidateIndex] || (length == lengths[candidateIndex] && DeepSiftTemporalMetricComparer.QuantizeMetric(score) > DeepSiftTemporalMetricComparer.QuantizeMetric(scores[candidateIndex])))
                        {
                            lengths[candidateIndex] = length;
                            scores[candidateIndex] = score;
                            previous[candidateIndex] = precedingIndex;
                        }
                    }
                    if (lengths[candidateIndex] > lengths[bestIndex] || (lengths[candidateIndex] == lengths[bestIndex] && DeepSiftTemporalMetricComparer.QuantizeMetric(scores[candidateIndex]) > DeepSiftTemporalMetricComparer.QuantizeMetric(scores[bestIndex])))
                        bestIndex = candidateIndex;
                }
                while (bestIndex >= 0)
                {
                    this.StrongPath.Add(strong[bestIndex]);
                    bestIndex = previous[bestIndex];
                }
                this.StrongPath.Reverse();
                this.OffsetMs = this.GetMedianOffset(this.StrongPath);
            }

            /// <summary>
            /// Converte il cluster risolto nel contratto diagnostico FrameSync
            /// </summary>
            public FrameSyncCandidate ToCandidate(string backend, long processedPairCount)
            {
                FrameSyncCandidate result = new FrameSyncCandidate();
                result.OffsetMs = (int)Math.Round(this.OffsetMs);
                result.Backend = backend ?? "";
                result.ProcessedPairCount = processedPairCount;
                result.AcceptedPairCount = this.Pairs.Count;
                result.StrongPairCount = this.StrongPath.Count;
                for (int pairIndex = 0; pairIndex < this.Pairs.Count; pairIndex++)
                {
                    if (!this.Pairs[pairIndex].Strong)
                        result.AmbiguousPairCount++;
                }
                if (this.StrongPath.Count > 0)
                {
                    result.SourceCoverageMs = this.StrongPath[this.StrongPath.Count - 1].Pair.SourcePtsMs - this.StrongPath[0].Pair.SourcePtsMs;
                    result.LanguageCoverageMs = this.StrongPath[this.StrongPath.Count - 1].Pair.LanguagePtsMs - this.StrongPath[0].Pair.LanguagePtsMs;
                    List<double> deviations = new List<double>(this.StrongPath.Count);
                    double score = 0.0;
                    for (int pairIndex = 0; pairIndex < this.StrongPath.Count; pairIndex++)
                    {
                        score += this.StrongPath[pairIndex].Pair.Score;
                        deviations.Add(Math.Abs(this.StrongPath[pairIndex].OffsetMs - this.OffsetMs));
                    }
                    deviations.Sort();
                    result.MeanScore = score / this.StrongPath.Count;
                    result.DispersionMs = this.GetMedian(deviations);
                }
                return result;
            }

            /// <summary>
            /// Calcola la mediana degli offset di una sequenza già materializzata
            /// </summary>
            private double GetMedianOffset(IReadOnlyList<TemporalPair> pairs)
            {
                List<double> values = new List<double>(pairs.Count);
                for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
                    values.Add(pairs[pairIndex].OffsetMs);
                values.Sort();
                return this.GetMedian(values);
            }

            /// <summary>
            /// Calcola la mediana di valori ordinati
            /// </summary>
            private double GetMedian(IReadOnlyList<double> values)
            {
                if (values.Count == 0)
                    return 0.0;
                int middle = values.Count / 2;
                return values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) * 0.5 : values[middle];
            }

            /// <summary>
            /// Coppie assegnate al cluster
            /// </summary>
            public List<TemporalPair> Pairs { get; }

            /// <summary>
            /// Sottosequenza forte monotona
            /// </summary>
            public List<TemporalPair> StrongPath { get; }

            /// <summary>
            /// Offset mediano corrente
            /// </summary>
            public double OffsetMs { get; private set; }

            /// <summary>
            /// Massima incertezza temporale delle coppie
            /// </summary>
            public double MaximumUncertaintyMs { get; private set; }
        }

        #endregion
    }
}
