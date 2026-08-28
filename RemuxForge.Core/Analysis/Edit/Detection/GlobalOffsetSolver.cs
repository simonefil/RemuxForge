using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Analysis.Edit.Detection
{
    /// <summary>
    /// Ricostruisce l'unica scala globale degli offset a partire da corrispondenze dHash univoche
    /// </summary>
    internal class GlobalOffsetSolver
    {
        #region Costanti

        /// <summary>
        /// Distanza temporale fra due fotogrammi sorgente interrogati
        /// </summary>
        private const double ANCHOR_INTERVAL_MS = 160.0;

        /// <summary>
        /// Ricompensa parziale per l'oscillazione di un solo fotogramma attorno allo stato
        /// </summary>
        private const double ADJACENT_STATE_REWARD = 0.5;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Trova tutti i pianori della scala e le discontinuità che li separano
        /// </summary>
        /// <param name="pair">Coppia di tracce già portata nello stesso dominio temporale</param>
        /// <param name="cancellation">Token di annullamento</param>
        /// <param name="initialOffsetMs">Offset del primo pianoro</param>
        /// <returns>Operazioni candidate ordinate nel dominio della sorgente</returns>
        public List<EditOperationCandidate> Detect(PairSignals pair, CancellationToken cancellation, out double initialOffsetMs)
        {
            initialOffsetMs = 0.0;
            double languageStepMs = MedianStep(pair.LanguagePtsMs);
            if (pair.Source.Count == 0 || pair.Language.Count == 0 || languageStepMs <= 0.0)
                return new List<EditOperationCandidate>();

            List<TemporalAnchor> anchors = this.BuildAnchors(pair, languageStepMs, cancellation);
            if (anchors.Count == 0)
                return new List<EditOperationCandidate>();

            int[] path = this.SolvePath(anchors);
            List<OffsetRegime> regimes = this.BuildRegimes(anchors, path);
            this.AddTerminalEvidence(anchors, regimes);
            if (regimes.Count == 0)
                return new List<EditOperationCandidate>();

            initialOffsetMs = regimes[0].State * languageStepMs;
            List<EditOperationCandidate> result = new List<EditOperationCandidate>();
            for (int i = 0; i + 1 < regimes.Count; i++)
            {
                OffsetRegime before = regimes[i];
                OffsetRegime after = regimes[i + 1];
                double offsetBeforeMs = before.State * languageStepMs;
                double offsetAfterMs = after.State * languageStepMs;
                result.Add(new EditOperationCandidate
                {
                    Kind = offsetAfterMs < offsetBeforeMs ? EditOperationKind.InsertSilence : EditOperationKind.CutSegment,
                    TimestampMs = (before.EndMs + after.StartMs) / 2.0,
                    DurationMs = Math.Abs(offsetAfterMs - offsetBeforeMs),
                    OffsetBeforeMs = offsetBeforeMs,
                    OffsetAfterMs = offsetAfterMs,
                    PlateauEndBeforeMs = before.EndMs,
                    PlateauStartAfterMs = after.StartMs
                });
            }

            return result;
        }

        /// <summary>
        /// Conserva un ultimo pianoro breve quando prova un salto maggiore dell'ambiguità di verifica
        /// </summary>
        private void AddTerminalEvidence(IReadOnlyList<TemporalAnchor> anchors, List<OffsetRegime> regimes)
        {
            if (anchors.Count < 2 || regimes.Count == 0)
                return;
            int state = anchors[anchors.Count - 1].State;
            int first = anchors.Count - 1;
            while (first > 0 && Math.Abs(anchors[first - 1].State - state) <= 1)
                first--;
            if (anchors.Count - first < 2 || Math.Abs(regimes[regimes.Count - 1].State - state) <= EditAnalysisProfile.VERIFICATION_RADIUS)
                return;

            OffsetRegime terminal = new OffsetRegime(state, anchors[first].TimeMs, anchors[anchors.Count - 1].TimeMs);
            for (int i = first; i < anchors.Count; i++)
                terminal.Observe(anchors[i].State);
            regimes[regimes.Count - 1].EndMs = first > 0 ? anchors[first - 1].TimeMs : anchors[first].TimeMs;
            terminal.Recenter();
            regimes.Add(terminal);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Estrae in parallelo soltanto le corrispondenze che hanno un'unica posizione temporale plausibile
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="languageStepMs">Passo mediano dei fotogrammi lang</param>
        /// <param name="cancellation">Token di annullamento</param>
        /// <returns>Corrispondenze temporali ordinate</returns>
        private List<TemporalAnchor> BuildAnchors(PairSignals pair, double languageStepMs, CancellationToken cancellation)
        {
            double sourceStepMs = MedianStep(pair.Source.PtsMs);
            int stride = Math.Max(1, (int)Math.Round(ANCHOR_INTERVAL_MS / sourceStepMs));
            int slotCount = (pair.Source.Count + stride - 1) / stride;
            TemporalAnchor[] anchors = new TemporalAnchor[slotCount];
            ParallelOptions options = new ParallelOptions();
            options.CancellationToken = cancellation;
            options.MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount);

            Parallel.For(0, slotCount, options, slot =>
            {
                int sourceIndex = slot * stride;
                if (sourceIndex >= pair.Source.Count)
                    return;
                anchors[slot] = this.FindAnchor(pair, sourceIndex, languageStepMs);
            });

            List<TemporalAnchor> result = new List<TemporalAnchor>();
            foreach (TemporalAnchor anchor in anchors)
            {
                if (anchor != null)
                    result.Add(anchor);
            }
            return result;
        }

        /// <summary>
        /// Cerca la migliore corrispondenza e la accetta solo se non ne esiste una seconda lontana
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="sourceIndex">Fotogramma sorgente</param>
        /// <param name="languageStepMs">Passo mediano dei fotogrammi lang</param>
        /// <returns>Corrispondenza univoca oppure null</returns>
        private TemporalAnchor FindAnchor(PairSignals pair, int sourceIndex, double languageStepMs)
        {
            double sourceTimeMs = pair.Source.PtsMs[sourceIndex];
            int first = HashOps.LowerBound(pair.LanguagePtsMs, sourceTimeMs - EditAnalysisProfile.COVERAGE_INITIAL_RADIUS_MS);
            int end = HashOps.LowerBound(pair.LanguagePtsMs, sourceTimeMs + EditAnalysisProfile.COVERAGE_INITIAL_RADIUS_MS);
            if (first >= end)
                return null;

            ulong sourceHash0 = pair.Source.Hash0[sourceIndex];
            ulong sourceHash1 = pair.Source.Hash1[sourceIndex];
            int bestDistance = 129;
            int bestIndex = -1;
            for (int languageIndex = first; languageIndex < end; languageIndex++)
            {
                int distance = BitOperations.PopCount(sourceHash0 ^ pair.Language.Hash0[languageIndex]) +
                               BitOperations.PopCount(sourceHash1 ^ pair.Language.Hash1[languageIndex]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = languageIndex;
                }
            }
            if (bestDistance > EditAnalysisProfile.DETECTION_THRESHOLD)
                return null;

            int exclusionRadius = EditAnalysisProfile.VERIFICATION_RADIUS;
            int secondDistance = 129;
            for (int languageIndex = first; languageIndex < end; languageIndex++)
            {
                if (Math.Abs(languageIndex - bestIndex) <= exclusionRadius)
                    continue;
                int distance = BitOperations.PopCount(sourceHash0 ^ pair.Language.Hash0[languageIndex]) +
                               BitOperations.PopCount(sourceHash1 ^ pair.Language.Hash1[languageIndex]);
                if (distance < secondDistance)
                    secondDistance = distance;
            }
            if (secondDistance <= EditAnalysisProfile.DETECTION_THRESHOLD)
                return null;

            int state = (int)Math.Round((pair.LanguagePtsMs[bestIndex] - sourceTimeMs) / languageStepMs);
            return new TemporalAnchor(sourceTimeMs, state);
        }

        /// <summary>
        /// Risolve globalmente gli stati premiando le prove e penalizzando ogni discontinuità
        /// </summary>
        /// <param name="anchors">Corrispondenze temporali univoche</param>
        /// <returns>Stato scelto per ogni corrispondenza</returns>
        private int[] SolvePath(IReadOnlyList<TemporalAnchor> anchors)
        {
            List<int> states = new List<int>();
            foreach (TemporalAnchor anchor in anchors)
            {
                if (!states.Contains(anchor.State))
                    states.Add(anchor.State);
            }
            states.Sort();

            int stateCount = states.Count;
            double transitionPenalty = Math.Log(Math.Max(anchors.Count, 2));
            double[] previous = new double[stateCount];
            double[] current = new double[stateCount];
            int[] predecessors = new int[anchors.Count * stateCount];
            for (int stateIndex = 0; stateIndex < stateCount; stateIndex++)
                previous[stateIndex] = this.Emission(states[stateIndex], anchors[0].State);

            for (int anchorIndex = 1; anchorIndex < anchors.Count; anchorIndex++)
            {
                int bestIndex = 0;
                int secondIndex = stateCount > 1 ? 1 : 0;
                if (previous[secondIndex] > previous[bestIndex])
                    Swap(ref bestIndex, ref secondIndex);
                for (int stateIndex = 2; stateIndex < stateCount; stateIndex++)
                {
                    if (previous[stateIndex] > previous[bestIndex])
                    {
                        secondIndex = bestIndex;
                        bestIndex = stateIndex;
                    }
                    else if (previous[stateIndex] > previous[secondIndex])
                    {
                        secondIndex = stateIndex;
                    }
                }

                for (int stateIndex = 0; stateIndex < stateCount; stateIndex++)
                {
                    int otherIndex = bestIndex == stateIndex ? secondIndex : bestIndex;
                    double stay = previous[stateIndex];
                    double change = stateCount > 1 ? previous[otherIndex] - transitionPenalty : double.NegativeInfinity;
                    if (change > stay)
                    {
                        current[stateIndex] = change + this.Emission(states[stateIndex], anchors[anchorIndex].State);
                        predecessors[anchorIndex * stateCount + stateIndex] = otherIndex;
                    }
                    else
                    {
                        current[stateIndex] = stay + this.Emission(states[stateIndex], anchors[anchorIndex].State);
                        predecessors[anchorIndex * stateCount + stateIndex] = stateIndex;
                    }
                }

                double[] temporary = previous;
                previous = current;
                current = temporary;
            }

            int lastStateIndex = 0;
            for (int stateIndex = 1; stateIndex < stateCount; stateIndex++)
            {
                if (previous[stateIndex] > previous[lastStateIndex])
                    lastStateIndex = stateIndex;
            }

            int[] result = new int[anchors.Count];
            for (int anchorIndex = anchors.Count - 1; anchorIndex >= 0; anchorIndex--)
            {
                result[anchorIndex] = states[lastStateIndex];
                if (anchorIndex > 0)
                    lastStateIndex = predecessors[anchorIndex * stateCount + lastStateIndex];
            }
            return result;
        }

        /// <summary>
        /// Compatta il percorso in pianori e assorbe l'oscillazione di pochi fotogrammi
        /// </summary>
        /// <param name="anchors">Corrispondenze temporali</param>
        /// <param name="path">Stati globali scelti</param>
        /// <returns>Pianori consecutivi</returns>
        private List<OffsetRegime> BuildRegimes(IReadOnlyList<TemporalAnchor> anchors, int[] path)
        {
            List<OffsetRegime> regimes = new List<OffsetRegime>();
            for (int i = 0; i < path.Length; i++)
            {
                if (regimes.Count > 0 && regimes[regimes.Count - 1].State == path[i])
                {
                    regimes[regimes.Count - 1].EndMs = anchors[i].TimeMs;
                    regimes[regimes.Count - 1].Observe(anchors[i].State);
                    continue;
                }
                OffsetRegime regime = new OffsetRegime(path[i], anchors[i].TimeMs, anchors[i].TimeMs);
                regime.Observe(anchors[i].State);
                regimes.Add(regime);
            }

            List<OffsetRegime> result = new List<OffsetRegime>();
            foreach (OffsetRegime regime in regimes)
            {
                if (result.Count > 0 && Math.Abs(result[result.Count - 1].State - regime.State) <= 1)
                {
                    result[result.Count - 1].Merge(regime);
                    continue;
                }
                result.Add(regime);
            }
            foreach (OffsetRegime regime in result)
                regime.Recenter();
            for (int i = 1; i + 1 < result.Count; i++)
            {
                OffsetRegime before = result[i - 1];
                OffsetRegime after = result[i + 1];
                if (Math.Abs(before.State - after.State) > EditAnalysisProfile.VERIFICATION_RADIUS)
                    continue;
                int commonState = before.AnchorCount >= after.AnchorCount ? before.State : after.State;
                before.State = commonState;
                after.State = commonState;
            }
            return result;
        }

        /// <summary>
        /// Ricompensa associata alla distanza fra lo stato osservato e quello ipotizzato
        /// </summary>
        private double Emission(int state, int observed)
        {
            int distance = Math.Abs(state - observed);
            if (distance == 0)
                return 1.0;
            return distance == 1 ? ADJACENT_STATE_REWARD : 0.0;
        }

        /// <summary>
        /// Passo mediano di una sequenza di PTS
        /// </summary>
        private static double MedianStep(double[] values)
        {
            if (values.Length < 2)
                return 0.0;
            double[] steps = new double[values.Length - 1];
            for (int i = 1; i < values.Length; i++)
                steps[i - 1] = values[i] - values[i - 1];
            Array.Sort(steps);
            int middle = steps.Length / 2;
            return steps.Length % 2 == 1 ? steps[middle] : (steps[middle - 1] + steps[middle]) / 2.0;
        }

        /// <summary>
        /// Scambia due indici
        /// </summary>
        private static void Swap(ref int left, ref int right)
        {
            int temporary = left;
            left = right;
            right = temporary;
        }

        #endregion

        #region Tipi privati

        /// <summary>
        /// Una corrispondenza temporale dHash univoca
        /// </summary>
        private class TemporalAnchor
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="timeMs">PTS sorgente</param>
            /// <param name="state">Offset quantizzato in fotogrammi lang</param>
            public TemporalAnchor(double timeMs, int state)
            {
                this.TimeMs = timeMs;
                this.State = state;
            }

            /// <summary>
            /// PTS sorgente
            /// </summary>
            public double TimeMs { get; private set; }

            /// <summary>
            /// Offset quantizzato in fotogrammi lang
            /// </summary>
            public int State { get; private set; }

        }

        /// <summary>
        /// Un pianoro consecutivo della scala globale
        /// </summary>
        private class OffsetRegime
        {
            /// <summary>
            /// Frequenza degli stati osservati nel pianoro
            /// </summary>
            private Dictionary<int, int> _observations;

            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="state">Stato scelto dal percorso globale</param>
            /// <param name="startMs">Prima ancora del pianoro</param>
            /// <param name="endMs">Ultima ancora del pianoro</param>
            public OffsetRegime(int state, double startMs, double endMs)
            {
                this.State = state;
                this.StartMs = startMs;
                this.EndMs = endMs;
                this._observations = new Dictionary<int, int>();
            }

            /// <summary>
            /// Offset quantizzato in fotogrammi lang
            /// </summary>
            public int State { get; set; }

            /// <summary>
            /// Prima ancora del pianoro
            /// </summary>
            public double StartMs { get; private set; }

            /// <summary>
            /// Ultima ancora del pianoro
            /// </summary>
            public double EndMs { get; set; }

            /// <summary>
            /// Numero di ancore assegnate al pianoro
            /// </summary>
            public int AnchorCount { get; private set; }

            /// <summary>
            /// Registra uno stato osservato
            /// </summary>
            /// <param name="state">Stato osservato</param>
            public void Observe(int state)
            {
                if (!this._observations.ContainsKey(state))
                    this._observations[state] = 0;
                this._observations[state]++;
                this.AnchorCount++;
            }

            /// <summary>
            /// Assorbe un pianoro adiacente equivalente
            /// </summary>
            /// <param name="other">Pianoro da assorbire</param>
            public void Merge(OffsetRegime other)
            {
                foreach (KeyValuePair<int, int> observation in other._observations)
                {
                    if (!this._observations.ContainsKey(observation.Key))
                        this._observations[observation.Key] = 0;
                    this._observations[observation.Key] += observation.Value;
                    this.AnchorCount += observation.Value;
                }
                this.EndMs = other.EndMs;
            }

            /// <summary>
            /// Porta lo stato sulla moda delle osservazioni
            /// </summary>
            public void Recenter()
            {
                int bestState = this.State;
                int bestCount = -1;
                foreach (KeyValuePair<int, int> observation in this._observations)
                {
                    if (observation.Value > bestCount)
                    {
                        bestState = observation.Key;
                        bestCount = observation.Value;
                    }
                }
                this.State = bestState;
            }
        }

        #endregion
    }
}
