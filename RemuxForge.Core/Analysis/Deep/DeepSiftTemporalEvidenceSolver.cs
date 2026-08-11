using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Costruisce una catena monotona e segmenta direttamente gli offset visuali
    /// </summary>
    public sealed class DeepSiftTemporalEvidenceSolver
    {
        private readonly DeepSiftTemporalEvidenceOptions _options;

        /// <summary>
        /// Costruisce il solver con opzioni esplicite o predefinite
        /// </summary>
        /// <param name="options">Opzioni del solver, null per usare i valori predefiniti</param>
        public DeepSiftTemporalEvidenceSolver(DeepSiftTemporalEvidenceOptions options = null)
        {
            this._options = options ?? new DeepSiftTemporalEvidenceOptions();
            if (this._options.SupportWindowMatchCount < 3 || this._options.SupportWindowMatchCount % 2 == 0)
                throw new ArgumentOutOfRangeException(nameof(options));
            if (this._options.FrameUncertaintyMultiplier <= 0.0 || double.IsNaN(this._options.FrameUncertaintyMultiplier) || double.IsInfinity(this._options.FrameUncertaintyMultiplier))
                throw new ArgumentOutOfRangeException(nameof(options));
        }

        /// <summary>
        /// Costruisce la catena monotona e i plateau per una scala temporale nota
        /// </summary>
        /// <param name="pairs">Evidenze visuali positive</param>
        /// <param name="sourceToLanguageScale">Scala temporale source-language</param>
        /// <returns>Risultato replayabile della segmentazione temporale</returns>
        public DeepSiftTemporalEvidenceResult Solve(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, double sourceToLanguageScale)
        {
            if (pairs == null)
                throw new ArgumentNullException(nameof(pairs));
            if (sourceToLanguageScale <= 0.0 || double.IsNaN(sourceToLanguageScale) || double.IsInfinity(sourceToLanguageScale))
                throw new ArgumentOutOfRangeException(nameof(sourceToLanguageScale));

            DeepSiftTemporalEvidenceResult result = new DeepSiftTemporalEvidenceResult();
            result.InputEvidenceCount = pairs.Count;
            List<EvidenceNode> nodes = this.BuildNodes(pairs, sourceToLanguageScale);
            if (nodes.Count == 0)
            {
                result.RejectReason = "Nessuna evidenza visuale temporale valida";
                return result;
            }

            double chainScore;
            result.Chain = this.BuildLocalVotingChain(nodes, out chainScore);
            result.ChainScore = chainScore;
            int window = this._options.SupportWindowMatchCount;
            if (result.Chain.Count < window * 2)
            {
                result.RejectReason = "Catena monotona con supporto temporale insufficiente";
                return result;
            }

            List<BoundaryCandidate> boundaries = this.FindBoundaries(result.Chain, window);
            this.BuildPlateaus(result, boundaries);
            if (result.Plateaus.Count == 0)
            {
                result.RejectReason = "Nessun plateau temporale sostenuto";
                return result;
            }

            boundaries = this.RefineBoundaries(result.Chain, result.Plateaus);
            result.Plateaus.Clear();
            this.BuildPlateaus(result, boundaries);

            this.BuildTransitions(result);
            result.Accepted = true;
            return result;
        }

        private List<EvidenceNode> BuildNodes(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, double scale)
        {
            List<EvidenceNode> result = new List<EvidenceNode>();
            for (int i = 0; i < pairs.Count; i++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[i];
                if (pair == null || pair.SourceAnchorIndex < 0 || pair.LanguageAnchorIndex < 0 || pair.Score <= 0.0)
                    continue;
                if (double.IsNaN(pair.SourcePtsMs) || double.IsInfinity(pair.SourcePtsMs) || double.IsNaN(pair.LanguagePtsMs) || double.IsInfinity(pair.LanguagePtsMs))
                    continue;

                double sourceFrameMs = pair.SourceFrameDurationMs > 0.0 ? pair.SourceFrameDurationMs : 1.0;
                double languageFrameMs = pair.LanguageFrameDurationMs > 0.0 ? pair.LanguageFrameDurationMs / scale : 1.0;
                EvidenceNode node = new EvidenceNode();
                node.Pair = pair;
                node.OffsetMs = pair.SourcePtsMs - (pair.LanguagePtsMs / scale);
                node.UncertaintyMs = Math.Max(1.0, Math.Max(sourceFrameMs, languageFrameMs) * this._options.FrameUncertaintyMultiplier);
                result.Add(node);
            }
            result.Sort(this.CompareNodes);
            return result;
        }

        private int CompareNodes(EvidenceNode first, EvidenceNode second)
        {
            int result = first.Pair.SourceAnchorIndex.CompareTo(second.Pair.SourceAnchorIndex);
            if (result != 0)
                return result;
            result = first.Pair.LanguageAnchorIndex.CompareTo(second.Pair.LanguageAnchorIndex);
            if (result != 0)
                return result;
            return second.Pair.Score.CompareTo(first.Pair.Score);
        }

        /// <summary>
        /// Riduce le evidenze dense a voti locali di offset con supporto source distinto
        /// </summary>
        private List<DeepSiftTemporalChainMatch> BuildLocalVotingChain(List<EvidenceNode> nodes, out double chainScore)
        {
            List<DeepSiftTemporalChainMatch> result = new List<DeepSiftTemporalChainMatch>();
            List<EvidenceNode> block = new List<EvidenceNode>();
            chainScore = 0.0;
            int sourceStart = 0;
            while (sourceStart < nodes.Count)
            {
                int sourceEnd = sourceStart + 1;
                while (sourceEnd < nodes.Count && nodes[sourceEnd].Pair.SourceAnchorIndex == nodes[sourceStart].Pair.SourceAnchorIndex)
                    sourceEnd++;
                for (int i = sourceStart; i < sourceEnd; i++)
                    block.Add(nodes[i]);

                List<EvidenceCluster> clusters = this.BuildEvidenceClusters(block);
                EvidenceCluster selected = this.SelectSupportedCluster(clusters);
                if (selected != null && selected.SourceIndexes.Count >= this._options.SupportWindowMatchCount)
                {
                    EvidenceNode representative = this.SelectRepresentative(selected);
                    if (representative != null)
                    {
                        DeepSiftTemporalChainMatch match = new DeepSiftTemporalChainMatch();
                        match.SourceAnchorIndex = representative.Pair.SourceAnchorIndex;
                        match.LanguageAnchorIndex = representative.Pair.LanguageAnchorIndex;
                        match.SourcePtsMs = representative.Pair.SourcePtsMs;
                        match.LanguagePtsMs = representative.Pair.LanguagePtsMs;
                        match.OffsetMs = selected.OffsetMs;
                        match.UncertaintyMs = selected.UncertaintyMs;
                        match.Score = representative.Pair.Score;
                        result.Add(match);
                        chainScore += selected.TotalScore;
                        block.Clear();
                    }
                }
                sourceStart = sourceEnd;
            }
            return this.BuildMonotoneVotingChain(result, out chainScore);
        }

        private List<EvidenceCluster> BuildEvidenceClusters(List<EvidenceNode> block)
        {
            List<EvidenceNode> ordered = new List<EvidenceNode>(block);
            ordered.Sort((first, second) => first.OffsetMs.CompareTo(second.OffsetMs));
            List<EvidenceCluster> result = new List<EvidenceCluster>();
            for (int i = 0; i < ordered.Count; i++)
            {
                EvidenceNode node = ordered[i];
                EvidenceCluster cluster = result.Count > 0 ? result[result.Count - 1] : null;
                if (cluster == null || Math.Abs(node.OffsetMs - cluster.OffsetMs) > (node.UncertaintyMs + cluster.UncertaintyMs) * 2.0)
                {
                    cluster = new EvidenceCluster();
                    result.Add(cluster);
                }
                cluster.Nodes.Add(node);
                cluster.SourceIndexes.Add(node.Pair.SourceAnchorIndex);
                cluster.TotalScore += node.Pair.Score;
                cluster.UncertaintyMs = Math.Max(cluster.UncertaintyMs, node.UncertaintyMs);
                cluster.OffsetMs = this.MedianNodeOffset(cluster.Nodes);
            }
            return result;
        }

        private EvidenceCluster SelectSupportedCluster(List<EvidenceCluster> clusters)
        {
            EvidenceCluster result = null;
            for (int i = 0; i < clusters.Count; i++)
            {
                EvidenceCluster cluster = clusters[i];
                if (result == null || cluster.SourceIndexes.Count > result.SourceIndexes.Count || (cluster.SourceIndexes.Count == result.SourceIndexes.Count && cluster.TotalScore > result.TotalScore))
                    result = cluster;
            }
            return result;
        }

        private List<DeepSiftTemporalChainMatch> BuildMonotoneVotingChain(List<DeepSiftTemporalChainMatch> votes, out double chainScore)
        {
            double[] scores = new double[votes.Count];
            int[] predecessors = new int[votes.Count];
            Array.Fill(predecessors, -1);
            int bestIndex = -1;
            for (int i = 0; i < votes.Count; i++)
            {
                scores[i] = 1.0 + Math.Max(0.0, votes[i].Score);
                for (int previous = 0; previous < i; previous++)
                {
                    if (votes[previous].SourceAnchorIndex >= votes[i].SourceAnchorIndex || votes[previous].LanguageAnchorIndex >= votes[i].LanguageAnchorIndex)
                        continue;
                    double candidateScore = scores[previous] + 1.0 + Math.Max(0.0, votes[i].Score);
                    if (candidateScore > scores[i])
                    {
                        scores[i] = candidateScore;
                        predecessors[i] = previous;
                    }
                }
                if (bestIndex < 0 || scores[i] > scores[bestIndex])
                    bestIndex = i;
            }

            List<DeepSiftTemporalChainMatch> result = new List<DeepSiftTemporalChainMatch>();
            chainScore = bestIndex >= 0 ? scores[bestIndex] : 0.0;
            while (bestIndex >= 0)
            {
                result.Add(votes[bestIndex]);
                bestIndex = predecessors[bestIndex];
            }
            result.Reverse();
            return result;
        }

        private EvidenceNode SelectRepresentative(EvidenceCluster cluster)
        {
            EvidenceNode result = null;
            double bestDistance = double.PositiveInfinity;
            for (int i = 0; i < cluster.Nodes.Count; i++)
            {
                EvidenceNode node = cluster.Nodes[i];
                double distance = Math.Abs(node.OffsetMs - cluster.OffsetMs);
                if (result == null || distance < bestDistance || (Math.Abs(distance - bestDistance) <= 0.001 && node.Pair.Score > result.Pair.Score))
                {
                    result = node;
                    bestDistance = distance;
                }
            }
            return result;
        }

        private double MedianNodeOffset(List<EvidenceNode> nodes)
        {
            List<double> values = new List<double>(nodes.Count);
            for (int i = 0; i < nodes.Count; i++)
                values.Add(nodes[i].OffsetMs);
            values.Sort();
            return values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) * 0.5;
        }

        private List<BoundaryCandidate> FindBoundaries(List<DeepSiftTemporalChainMatch> chain, int window)
        {
            int smoothingWindow = Math.Max(window, (int)Math.Floor(Math.Sqrt(chain.Count)));
            if (smoothingWindow % 2 == 0)
                smoothingWindow--;
            smoothingWindow = Math.Max(window, smoothingWindow);
            int radius = smoothingWindow / 2;
            double[] smoothedOffsets = new double[chain.Count];
            for (int chainIndex = 0; chainIndex < chain.Count; chainIndex++)
            {
                int start = Math.Max(0, chainIndex - radius);
                int end = Math.Min(chain.Count - 1, chainIndex + radius);
                smoothedOffsets[chainIndex] = this.MedianOffset(chain, start, end);
            }

            List<SmoothedRun> runs = new List<SmoothedRun>();
            int runStart = 0;
            for (int chainIndex = 1; chainIndex < chain.Count; chainIndex++)
            {
                double uncertaintyMs = (chain[chainIndex - 1].UncertaintyMs + chain[chainIndex].UncertaintyMs) * 2.0;
                if (Math.Abs(smoothedOffsets[chainIndex] - smoothedOffsets[chainIndex - 1]) <= uncertaintyMs)
                    continue;
                runs.Add(this.CreateSmoothedRun(smoothedOffsets, runStart, chainIndex - 1));
                runStart = chainIndex;
            }
            runs.Add(this.CreateSmoothedRun(smoothedOffsets, runStart, chain.Count - 1));

            int minimumRunMatchCount = Math.Max(window, smoothingWindow / 2);
            bool merged;
            do
            {
                merged = false;
                for (int runIndex = 0; runIndex < runs.Count; runIndex++)
                {
                    SmoothedRun run = runs[runIndex];
                    if (run.EndIndex - run.StartIndex + 1 >= minimumRunMatchCount || runs.Count == 1)
                        continue;

                    int targetIndex;
                    if (runIndex == 0)
                        targetIndex = 1;
                    else if (runIndex == runs.Count - 1)
                        targetIndex = runIndex - 1;
                    else
                    {
                        double previousDistance = Math.Abs(run.OffsetMs - runs[runIndex - 1].OffsetMs);
                        double nextDistance = Math.Abs(run.OffsetMs - runs[runIndex + 1].OffsetMs);
                        targetIndex = previousDistance <= nextDistance ? runIndex - 1 : runIndex + 1;
                    }

                    if (targetIndex < runIndex)
                    {
                        runs[targetIndex] = this.CreateSmoothedRun(smoothedOffsets, runs[targetIndex].StartIndex, run.EndIndex);
                        runs.RemoveAt(runIndex);
                    }
                    else
                    {
                        runs[targetIndex] = this.CreateSmoothedRun(smoothedOffsets, run.StartIndex, runs[targetIndex].EndIndex);
                        runs.RemoveAt(runIndex);
                    }
                    merged = true;
                    break;
                }
            }
            while (merged);

            for (int runIndex = runs.Count - 2; runIndex >= 0; runIndex--)
            {
                SmoothedRun current = runs[runIndex];
                SmoothedRun next = runs[runIndex + 1];
                double uncertaintyMs = this.MaximumUncertainty(chain, current.StartIndex, next.EndIndex) * 4.0;
                if (Math.Abs(current.OffsetMs - next.OffsetMs) > uncertaintyMs)
                    continue;
                runs[runIndex] = this.CreateSmoothedRun(smoothedOffsets, current.StartIndex, next.EndIndex);
                runs.RemoveAt(runIndex + 1);
            }

            List<BoundaryCandidate> result = new List<BoundaryCandidate>();
            for (int runIndex = 0; runIndex + 1 < runs.Count; runIndex++)
            {
                SmoothedRun current = runs[runIndex];
                SmoothedRun next = runs[runIndex + 1];
                double uncertaintyMs = (chain[current.EndIndex].UncertaintyMs + chain[next.StartIndex].UncertaintyMs) * 2.0;
                double separationMs = Math.Abs(next.OffsetMs - current.OffsetMs) - uncertaintyMs;
                if (separationMs > 0.0)
                    result.Add(new BoundaryCandidate { ChainIndex = current.EndIndex });
            }
            return result;
        }

        private List<BoundaryCandidate> RefineBoundaries(List<DeepSiftTemporalChainMatch> chain, List<DeepSiftTemporalPlateau> plateaus)
        {
            List<BoundaryCandidate> result = new List<BoundaryCandidate>();
            for (int plateauIndex = 0; plateauIndex + 1 < plateaus.Count; plateauIndex++)
            {
                DeepSiftTemporalPlateau before = plateaus[plateauIndex];
                DeepSiftTemporalPlateau after = plateaus[plateauIndex + 1];
                int searchStart = plateauIndex == 0 ? before.FirstChainIndex : result[result.Count - 1].ChainIndex + 1;
                int searchEnd = plateauIndex + 2 < plateaus.Count ? plateaus[plateauIndex + 2].FirstChainIndex - 1 : after.LastChainIndex - 1;
                searchStart = Math.Max(searchStart, before.FirstChainIndex);
                searchEnd = Math.Min(searchEnd, after.LastChainIndex - 1);
                int bestBoundary = before.LastChainIndex;
                double bestCost = double.PositiveInfinity;
                for (int boundary = searchStart; boundary <= searchEnd; boundary++)
                {
                    double cost = 0.0;
                    for (int i = searchStart; i <= boundary; i++)
                        cost += Math.Abs(chain[i].OffsetMs - before.OffsetMs) / Math.Max(1.0, chain[i].UncertaintyMs);
                    for (int i = boundary + 1; i <= searchEnd + 1; i++)
                        cost += Math.Abs(chain[i].OffsetMs - after.OffsetMs) / Math.Max(1.0, chain[i].UncertaintyMs);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestBoundary = boundary;
                    }
                }
                result.Add(new BoundaryCandidate { ChainIndex = bestBoundary });
            }
            return result;
        }

        private SmoothedRun CreateSmoothedRun(double[] offsets, int start, int end)
        {
            List<double> values = new List<double>(end - start + 1);
            for (int i = start; i <= end; i++)
                values.Add(offsets[i]);
            values.Sort();
            double median = values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) * 0.5;
            return new SmoothedRun { StartIndex = start, EndIndex = end, OffsetMs = median };
        }

        private void BuildPlateaus(DeepSiftTemporalEvidenceResult result, List<BoundaryCandidate> boundaries)
        {
            int start = 0;
            for (int i = 0; i <= boundaries.Count; i++)
            {
                int end = i < boundaries.Count ? boundaries[i].ChainIndex : result.Chain.Count - 1;
                if (end < start)
                    continue;
                DeepSiftTemporalPlateau plateau = new DeepSiftTemporalPlateau();
                plateau.FirstChainIndex = start;
                plateau.LastChainIndex = end;
                plateau.MatchCount = end - start + 1;
                plateau.OffsetMs = this.MedianOffset(result.Chain, start, end);
                plateau.UncertaintyMs = this.MaximumUncertainty(result.Chain, start, end);
                plateau.SourceStartPtsMs = result.Chain[start].SourcePtsMs;
                plateau.SourceEndPtsMs = result.Chain[end].SourcePtsMs;
                plateau.LanguageStartPtsMs = result.Chain[start].LanguagePtsMs;
                plateau.LanguageEndPtsMs = result.Chain[end].LanguagePtsMs;
                result.Plateaus.Add(plateau);
                start = end + 1;
            }
        }

        private void BuildTransitions(DeepSiftTemporalEvidenceResult result)
        {
            for (int i = 0; i + 1 < result.Plateaus.Count; i++)
            {
                DeepSiftTemporalPlateau before = result.Plateaus[i];
                DeepSiftTemporalPlateau after = result.Plateaus[i + 1];
                DeepSiftTemporalChainMatch oldMatch = result.Chain[before.LastChainIndex];
                DeepSiftTemporalChainMatch newMatch = result.Chain[after.FirstChainIndex];
                DeepSiftTemporalTransition transition = new DeepSiftTemporalTransition();
                transition.BeforePlateauIndex = i;
                transition.AfterPlateauIndex = i + 1;
                transition.OffsetDeltaMs = after.OffsetMs - before.OffsetMs;
                transition.SeparationMs = Math.Abs(transition.OffsetDeltaMs) - before.UncertaintyMs - after.UncertaintyMs;
                transition.LastOldSourcePtsMs = oldMatch.SourcePtsMs;
                transition.FirstNewSourcePtsMs = newMatch.SourcePtsMs;
                transition.LastOldLanguagePtsMs = oldMatch.LanguagePtsMs;
                transition.FirstNewLanguagePtsMs = newMatch.LanguagePtsMs;
                result.Transitions.Add(transition);
            }
        }

        private double MedianOffset(List<DeepSiftTemporalChainMatch> chain, int start, int end)
        {
            List<double> values = new List<double>(end - start + 1);
            for (int i = start; i <= end; i++)
                values.Add(chain[i].OffsetMs);
            values.Sort();
            return values.Count % 2 == 1 ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) * 0.5;
        }

        private double MaximumUncertainty(List<DeepSiftTemporalChainMatch> chain, int start, int end)
        {
            double result = 1.0;
            for (int i = start; i <= end; i++)
                result = Math.Max(result, chain[i].UncertaintyMs);
            return result;
        }

        private class EvidenceNode
        {
            public DeepSiftAcceptedPairDiagnostic Pair { get; set; }
            public double OffsetMs { get; set; }
            public double UncertaintyMs { get; set; }
        }

        private class BoundaryCandidate
        {
            public int ChainIndex { get; set; }
        }

        private class EvidenceCluster
        {
            public EvidenceCluster()
            {
                this.Nodes = new List<EvidenceNode>();
                this.SourceIndexes = new HashSet<int>();
            }

            public List<EvidenceNode> Nodes { get; }
            public HashSet<int> SourceIndexes { get; }
            public double OffsetMs { get; set; }
            public double UncertaintyMs { get; set; }
            public double TotalScore { get; set; }
        }

        private class SmoothedRun
        {
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public double OffsetMs { get; set; }
        }

    }
}
