using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Rileva ambiguità temporali nelle componenti many-to-many delle coppie SIFT accettate
    /// </summary>
    /// <remarks>
    /// Conserva le componenti del nucleo ciclico bipartito la cui estensione supera la risoluzione temporale su entrambi gli assi PTS
    /// </remarks>
    internal static class DeepSiftTemporalAmbiguityDetector
    {
        #region Metodi pubblici

        /// <summary>
        /// Individua le coppie appartenenti a componenti bipartite many-to-many non risolte dalla confidence locale e oltre la risoluzione PTS
        /// </summary>
        /// <param name="pairs">Coppie SIFT già accettate dal controllo geometrico</param>
        /// <param name="minimumScoreMargin">Margine quantizzato richiesto perché una coppia con score maggiore risolva l'alternativa su uno dei due estremi</param>
        /// <returns>Chiavi PTS quantizzate delle coppie appartenenti alle componenti temporalmente ambigue</returns>
        public static HashSet<(long SourcePts, long LanguagePts)> FindManyToManyPairs(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, double minimumScoreMargin)
        {
            HashSet<(long SourcePts, long LanguagePts)> result = new HashSet<(long SourcePts, long LanguagePts)>();
            Dictionary<long, int> sourceNodes = new Dictionary<long, int>();
            Dictionary<long, int> languageNodes = new Dictionary<long, int>();
            int[] sourceNodeIndexes = new int[pairs.Count];
            int[] languageNodeIndexes = new int[pairs.Count];
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                sourceNodeIndexes[pairIndex] = GetNodeIndex(sourceNodes, DeepSiftTemporalMetricComparer.QuantizeMilliseconds(pairs[pairIndex].SourcePtsMs));
                languageNodeIndexes[pairIndex] = GetNodeIndex(languageNodes, DeepSiftTemporalMetricComparer.QuantizeMilliseconds(pairs[pairIndex].LanguagePtsMs));
            }

            int sourceNodeCount = sourceNodes.Count;
            int nodeCount = sourceNodeCount + languageNodes.Count;
            int[] sourceBestPairIndexes = BuildBestPairIndexes(sourceNodeIndexes, sourceNodeCount, pairs);
            int[] languageBestPairIndexes = BuildBestPairIndexes(languageNodeIndexes, languageNodes.Count, pairs);
            bool[] equivalentPairs = new bool[pairs.Count];
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                DeepSiftAcceptedPairDiagnostic sourceBest = pairs[sourceBestPairIndexes[sourceNodeIndexes[pairIndex]]];
                DeepSiftAcceptedPairDiagnostic languageBest = pairs[languageBestPairIndexes[languageNodeIndexes[pairIndex]]];
                equivalentPairs[pairIndex] = !DeepSiftTemporalMetricComparer.HasHigherConfidence(sourceBest, pair, minimumScoreMargin) &&
                                             !DeepSiftTemporalMetricComparer.HasHigherConfidence(languageBest, pair, minimumScoreMargin);
            }

            List<int>[] adjacency = new List<int>[nodeCount];
            int[] degrees = new int[nodeCount];
            for (int nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
                adjacency[nodeIndex] = new List<int>();
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                int sourceNodeIndex = sourceNodeIndexes[pairIndex];
                int languageNodeIndex = sourceNodeCount + languageNodeIndexes[pairIndex];
                languageNodeIndexes[pairIndex] = languageNodeIndex;
                if (!equivalentPairs[pairIndex])
                    continue;
                adjacency[sourceNodeIndex].Add(pairIndex);
                adjacency[languageNodeIndex].Add(pairIndex);
                degrees[sourceNodeIndex]++;
                degrees[languageNodeIndex]++;
            }

            bool[] removedPairs = PeelAcyclicEdges(adjacency, sourceNodeIndexes, languageNodeIndexes, degrees, pairs.Count);
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
                removedPairs[pairIndex] |= !equivalentPairs[pairIndex];
            bool[] visitedPairs = new bool[pairs.Count];
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                if (removedPairs[pairIndex] || visitedPairs[pairIndex])
                    continue;
                List<int> component = BuildCyclicComponent(pairIndex, adjacency, sourceNodeIndexes, languageNodeIndexes, removedPairs, visitedPairs);
                if (!ExceedsPtsResolution(component, pairs))
                    continue;
                for (int componentIndex = 0; componentIndex < component.Count; componentIndex++)
                {
                    DeepSiftAcceptedPairDiagnostic pair = pairs[component[componentIndex]];
                    result.Add((DeepSiftTemporalMetricComparer.QuantizeMilliseconds(pair.SourcePtsMs), DeepSiftTemporalMetricComparer.QuantizeMilliseconds(pair.LanguagePtsMs)));
                }
            }
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Recupera o assegna l'indice compatto associato a un nodo PTS quantizzato
        /// </summary>
        /// <param name="indexes">Mappa dei nodi PTS quantizzati agli indici già assegnati</param>
        /// <param name="key">PTS quantizzato del nodo</param>
        /// <returns>Indice compatto del nodo</returns>
        private static int GetNodeIndex(Dictionary<long, int> indexes, long key)
        {
            if (indexes.TryGetValue(key, out int index))
                return index;
            index = indexes.Count;
            indexes.Add(key, index);
            return index;
        }

        /// <summary>
        /// Seleziona per ogni nodo la coppia con score quantizzato più alto
        /// </summary>
        /// <param name="nodeIndexes">Indice del nodo associato a ogni coppia</param>
        /// <param name="nodeCount">Numero complessivo di nodi</param>
        /// <param name="pairs">Coppie candidate da confrontare</param>
        /// <returns>Indice della coppia migliore per ciascun nodo</returns>
        private static int[] BuildBestPairIndexes(IReadOnlyList<int> nodeIndexes, int nodeCount, IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs)
        {
            int[] result = new int[nodeCount];
            Array.Fill(result, -1);
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                int nodeIndex = nodeIndexes[pairIndex];
                int bestPairIndex = result[nodeIndex];
                if (bestPairIndex < 0 || DeepSiftTemporalMetricComparer.QuantizeMetric(pairs[pairIndex].Score) > DeepSiftTemporalMetricComparer.QuantizeMetric(pairs[bestPairIndex].Score))
                    result[nodeIndex] = pairIndex;
            }
            return result;
        }

        /// <summary>
        /// Rimuove a cascata gli archi incidenti a nodi con grado inferiore a due
        /// </summary>
        /// <param name="adjacency">Indici degli archi incidenti a ogni nodo</param>
        /// <param name="sourceNodeIndexes">Indice del nodo source associato a ogni coppia</param>
        /// <param name="languageNodeIndexes">Indice del nodo language associato a ogni coppia</param>
        /// <param name="degrees">Grado corrente di ogni nodo</param>
        /// <param name="pairCount">Numero complessivo di coppie</param>
        /// <returns>Maschera degli archi rimossi dal nucleo ciclico bipartito</returns>
        private static bool[] PeelAcyclicEdges(List<int>[] adjacency, int[] sourceNodeIndexes, int[] languageNodeIndexes, int[] degrees, int pairCount)
        {
            bool[] removedPairs = new bool[pairCount];
            Queue<int> pendingNodes = new Queue<int>();
            for (int nodeIndex = 0; nodeIndex < degrees.Length; nodeIndex++)
            {
                if (degrees[nodeIndex] < 2)
                    pendingNodes.Enqueue(nodeIndex);
            }
            while (pendingNodes.Count > 0)
            {
                int nodeIndex = pendingNodes.Dequeue();
                for (int edgeIndex = 0; edgeIndex < adjacency[nodeIndex].Count; edgeIndex++)
                {
                    int pairIndex = adjacency[nodeIndex][edgeIndex];
                    if (removedPairs[pairIndex])
                        continue;
                    removedPairs[pairIndex] = true;
                    int otherNodeIndex = sourceNodeIndexes[pairIndex] == nodeIndex ? languageNodeIndexes[pairIndex] : sourceNodeIndexes[pairIndex];
                    degrees[nodeIndex]--;
                    degrees[otherNodeIndex]--;
                    if (degrees[otherNodeIndex] == 1)
                        pendingNodes.Enqueue(otherNodeIndex);
                }
            }
            return removedPairs;
        }

        /// <summary>
        /// Visita in ampiezza una componente connessa degli archi rimasti nel nucleo ciclico
        /// </summary>
        /// <param name="firstPairIndex">Indice del primo arco della componente</param>
        /// <param name="adjacency">Indici degli archi incidenti a ogni nodo</param>
        /// <param name="sourceNodeIndexes">Indice del nodo source associato a ogni coppia</param>
        /// <param name="languageNodeIndexes">Indice del nodo language associato a ogni coppia</param>
        /// <param name="removedPairs">Maschera degli archi esclusi dal nucleo ciclico</param>
        /// <param name="visitedPairs">Maschera degli archi già visitati durante la scansione</param>
        /// <returns>Indici delle coppie nella componente</returns>
        private static List<int> BuildCyclicComponent(int firstPairIndex, List<int>[] adjacency, int[] sourceNodeIndexes, int[] languageNodeIndexes, bool[] removedPairs, bool[] visitedPairs)
        {
            List<int> result = new List<int>();
            Queue<int> pendingPairs = new Queue<int>();
            pendingPairs.Enqueue(firstPairIndex);
            visitedPairs[firstPairIndex] = true;
            while (pendingPairs.Count > 0)
            {
                int pairIndex = pendingPairs.Dequeue();
                result.Add(pairIndex);
                EnqueueCyclicAdjacent(adjacency[sourceNodeIndexes[pairIndex]], removedPairs, visitedPairs, pendingPairs);
                EnqueueCyclicAdjacent(adjacency[languageNodeIndexes[pairIndex]], removedPairs, visitedPairs, pendingPairs);
            }
            return result;
        }

        /// <summary>
        /// Accoda gli archi adiacenti ancora validi e non visitati durante la scansione del nucleo
        /// </summary>
        /// <param name="pairIndexes">Indici degli archi incidenti al nodo corrente</param>
        /// <param name="removedPairs">Maschera degli archi esclusi dal nucleo ciclico</param>
        /// <param name="visitedPairs">Maschera degli archi già visitati durante la scansione</param>
        /// <param name="pendingPairs">Coda della visita in ampiezza</param>
        private static void EnqueueCyclicAdjacent(IReadOnlyList<int> pairIndexes, bool[] removedPairs, bool[] visitedPairs, Queue<int> pendingPairs)
        {
            for (int index = 0; index < pairIndexes.Count; index++)
            {
                int pairIndex = pairIndexes[index];
                if (removedPairs[pairIndex] || visitedPairs[pairIndex])
                    continue;
                visitedPairs[pairIndex] = true;
                pendingPairs.Enqueue(pairIndex);
            }
        }

        /// <summary>
        /// Verifica che l'estensione PTS di una componente superi la massima risoluzione osservata su entrambi gli assi
        /// </summary>
        /// <param name="component">Indici delle coppie appartenenti alla componente</param>
        /// <param name="pairs">Coppie SIFT candidate complessive</param>
        /// <returns>True quando la dispersione supera la risoluzione sia sull'asse source sia sull'asse language</returns>
        private static bool ExceedsPtsResolution(IReadOnlyList<int> component, IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs)
        {
            double sourceStartMs = double.PositiveInfinity;
            double sourceEndMs = double.NegativeInfinity;
            double languageStartMs = double.PositiveInfinity;
            double languageEndMs = double.NegativeInfinity;
            double sourceResolutionMs = 1.0;
            double languageResolutionMs = 1.0;
            for (int componentIndex = 0; componentIndex < component.Count; componentIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[component[componentIndex]];
                sourceStartMs = Math.Min(sourceStartMs, pair.SourcePtsMs);
                sourceEndMs = Math.Max(sourceEndMs, pair.SourcePtsMs);
                languageStartMs = Math.Min(languageStartMs, pair.LanguagePtsMs);
                languageEndMs = Math.Max(languageEndMs, pair.LanguagePtsMs);
                sourceResolutionMs = Math.Max(sourceResolutionMs, Math.Max(pair.SourceFrameDurationMs, pair.SourceSamplingDurationMs));
                languageResolutionMs = Math.Max(languageResolutionMs, Math.Max(pair.LanguageFrameDurationMs, pair.LanguageSamplingDurationMs));
            }
            return sourceEndMs - sourceStartMs > sourceResolutionMs &&
                   languageEndMs - languageStartMs > languageResolutionMs;
        }

        #endregion
    }
}
