using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Edit.Verification
{
    /// <summary>
    /// Quanto un'EditMap tiene agganciato il film, dal primo fotogramma all'ultimo
    /// </summary>
    internal class CoverageVerifier
    {
        #region Variabili di classe

        /// <summary>
        /// Backend che calcola gli hash e misura le griglie di offset
        /// </summary>
        private HashBackendBase _hashBackend;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="hashBackend">Backend che calcola gli hash e misura le griglie di offset</param>
        public CoverageVerifier(HashBackendBase hashBackend)
        {
            this._hashBackend = hashBackend;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Offset del primo tratto, cercato sulla testa del film
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="untilMs">Istante oltre il quale non guardare</param>
        /// <returns>Offset iniziale grezzo</returns>
        public double InitialOffset(PairSignals pair, double untilMs)
        {
            double[] sourcePts = pair.Source.PtsMs;
            HashOps.Range(pair, sourcePts[0], Math.Min(untilMs, sourcePts[0] + EditAnalysisProfile.COVERAGE_HEAD_MS), EditAnalysisProfile.SAMPLING_STRIDE, out int first, out int indexCount);
            if (indexCount < 20)
                return 0.0;

            double bestOffsetMs = 0.0;
            double bestFraction = -1.0;
            int coarseCount = (int)(2.0 * EditAnalysisProfile.COVERAGE_INITIAL_RADIUS_MS / 200.0) + 1;
            double[] coarse = this._hashBackend.Scan(first, EditAnalysisProfile.SAMPLING_STRIDE, indexCount, -EditAnalysisProfile.COVERAGE_INITIAL_RADIUS_MS, 200.0, coarseCount, EditAnalysisProfile.VERIFICATION_RADIUS, EditAnalysisProfile.VERIFICATION_THRESHOLD);
            for (int i = 0; i < coarseCount; i++)
            {
                if (coarse[i] > bestFraction)
                {
                    bestFraction = coarse[i];
                    bestOffsetMs = -EditAnalysisProfile.COVERAGE_INITIAL_RADIUS_MS + i * 200.0;
                }
            }

            double refinedMs = bestOffsetMs;
            bestFraction = -1.0;
            double[] fine = this._hashBackend.Scan(first, EditAnalysisProfile.SAMPLING_STRIDE, indexCount, bestOffsetMs - 200.0, 10.0, 41, EditAnalysisProfile.VERIFICATION_RADIUS, EditAnalysisProfile.VERIFICATION_THRESHOLD);
            for (int i = 0; i <= 40; i++)
            {
                if (fine[i] > bestFraction)
                {
                    bestFraction = fine[i];
                    refinedMs = bestOffsetMs - 200.0 + i * 10.0;
                }
            }

            return refinedMs;
        }

        /// <summary>
        /// La costante di ancoraggio che massimizza la copertura complessiva
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="operations">Operazioni dell'EditMap</param>
        /// <param name="initialOffsetMs">Offset iniziale grezzo</param>
        /// <returns>Offset del primo tratto che aggancia di più tutto il film</returns>
        public double Anchor(PairSignals pair, IReadOnlyList<EditOperationCandidate> operations, double initialOffsetMs)
        {
            // L'EditMap descrive la scala a meno di una costante, e stimarla sui primi sessanta
            // secondi la lega a com'è fatta la testa del film
            int[] indices = HashOps.RangeIndices(pair, pair.Source.PtsMs[0], double.MaxValue, 4 * EditAnalysisProfile.SAMPLING_STRIDE);
            double[] boundaries = BuildBoundaries(operations);
            double[] offsets = BuildOffsets(operations, 0.0);

            // La costante si cerca prima da lontano e a passo grosso: se la testa del film ha
            // mentito, un campo stretto attorno a lei resta chiuso dentro l'errore che deve curare
            double centerMs = initialOffsetMs;
            double bestFraction = -1.0;
            int sweepCount = (int)(2.0 * EditAnalysisProfile.COVERAGE_ANCHOR_SWEEP_MS / EditAnalysisProfile.COVERAGE_ANCHOR_SWEEP_STEP_MS) + 1;
            for (int i = 0; i < sweepCount; i++)
            {
                double candidateMs = initialOffsetMs - EditAnalysisProfile.COVERAGE_ANCHOR_SWEEP_MS + i * EditAnalysisProfile.COVERAGE_ANCHOR_SWEEP_STEP_MS;
                double fraction = this.Explained(pair, indices, boundaries, offsets, candidateMs);
                if (fraction > bestFraction)
                {
                    bestFraction = fraction;
                    centerMs = candidateMs;
                }
            }

            double bestOffsetMs = centerMs;
            bestFraction = -1.0;
            int coarseCount = (int)(2.0 * EditAnalysisProfile.COVERAGE_ANCHOR_FIELD_MS / 5.0) + 1;
            for (int i = 0; i < coarseCount; i++)
            {
                double candidateMs = centerMs - EditAnalysisProfile.COVERAGE_ANCHOR_FIELD_MS + i * 5.0;
                double fraction = this.Explained(pair, indices, boundaries, offsets, candidateMs);
                if (fraction > bestFraction)
                {
                    bestFraction = fraction;
                    bestOffsetMs = candidateMs;
                }
            }

            double[] fractions = new double[11];
            double peak = -1.0;
            for (int i = 0; i <= 10; i++)
            {
                fractions[i] = this.Explained(pair, indices, boundaries, offsets, bestOffsetMs - 5.0 + i);
                peak = Math.Max(peak, fractions[i]);
            }

            double total = 0.0;
            int members = 0;
            for (int i = 0; i <= 10; i++)
            {
                if (fractions[i] < peak - 1e-12)
                    continue;
                total += bestOffsetMs - 5.0 + i;
                members++;
            }

            return total / members;
        }

        /// <summary>
        /// Frazione del film che resta agganciata applicando l'EditMap
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="operations">Operazioni dell'EditMap</param>
        /// <param name="initialOffsetMs">Offset del primo tratto</param>
        /// <returns>Quota agganciata fra zero e uno</returns>
        public double Coverage(PairSignals pair, IReadOnlyList<EditOperationCandidate> operations, double initialOffsetMs)
        {
            int[] indices = HashOps.RangeIndices(pair, pair.Source.PtsMs[0], double.MaxValue, EditAnalysisProfile.SAMPLING_STRIDE);
            return this.Explained(pair, indices, BuildBoundaries(operations), BuildOffsets(operations, initialOffsetMs), 0.0);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Confini della funzione a gradini che l'EditMap descrive
        /// </summary>
        /// <param name="operations">Operazioni dell'EditMap</param>
        /// <returns>Istanti dei confini</returns>
        private static double[] BuildBoundaries(IReadOnlyList<EditOperationCandidate> operations)
        {
            double[] result = new double[operations.Count];
            for (int i = 0; i < operations.Count; i++)
                result[i] = operations[i].TimestampMs;
            return result;
        }

        /// <summary>
        /// Offset di ciascun tratto della funzione a gradini
        /// </summary>
        /// <param name="operations">Operazioni dell'EditMap</param>
        /// <param name="initialOffsetMs">Offset del primo tratto</param>
        /// <returns>Offset dei tratti, uno in più delle operazioni</returns>
        private static double[] BuildOffsets(IReadOnlyList<EditOperationCandidate> operations, double initialOffsetMs)
        {
            double[] result = new double[operations.Count + 1];
            result[0] = initialOffsetMs;
            for (int i = 0; i < operations.Count; i++)
                result[i + 1] = operations[i].Kind == EditOperationKind.InsertSilence ? result[i] - operations[i].DurationMs : result[i] + operations[i].DurationMs;
            return result;
        }

        /// <summary>
        /// Frazione dei fotogrammi campionati che la scala spiega
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="indices">Indici sorgente campionati</param>
        /// <param name="boundaries">Confini della scala</param>
        /// <param name="offsets">Offset dei tratti</param>
        /// <param name="shiftMs">Costante da sommare a tutti gli offset</param>
        /// <returns>Quota agganciata fra zero e uno</returns>
        private double Explained(PairSignals pair, int[] indices, double[] boundaries, double[] offsets, double shiftMs)
        {
            if (indices.Length == 0)
                return 0.0;
            int explained = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                double timeMs = pair.Source.PtsMs[indices[i]];
                int segment = 0;
                while (segment < boundaries.Length && boundaries[segment] <= timeMs)
                    segment++;
                if (HashOps.Distance(pair, indices[i], offsets[segment] + shiftMs, EditAnalysisProfile.VERIFICATION_RADIUS) <= EditAnalysisProfile.VERIFICATION_THRESHOLD)
                    explained++;
            }
            return (double)explained / indices.Length;
        }

        #endregion
    }
}
