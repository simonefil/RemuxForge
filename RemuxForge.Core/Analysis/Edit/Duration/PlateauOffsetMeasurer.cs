using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Core.Analysis.Edit.Duration
{
    /// <summary>
    /// La durata è la differenza di due offset, ciascuno scelto per tenere agganciato il proprio pianoro
    /// </summary>
    internal class PlateauOffsetMeasurer
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
        public PlateauOffsetMeasurer(HashBackendBase hashBackend)
        {
            this._hashBackend = hashBackend;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Rimisura i due offset di ogni operazione sui pianori che la circondano
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="operations">Operazioni con il confine già deciso</param>
        /// <param name="cancellation">Token di annullamento</param>
        /// <returns>Le stesse operazioni con durata, offset e incertezza rimisurati</returns>
        public List<EditOperationCandidate> Apply(PairSignals pair, IReadOnlyList<EditOperationCandidate> operations, CancellationToken cancellation)
        {
            double[] sourcePts = pair.Source.PtsMs;
            List<EditOperationCandidate> result = new List<EditOperationCandidate>();
            for (int k = 0; k < operations.Count; k++)
            {
                cancellation.ThrowIfCancellationRequested();
                EditOperationCandidate operation = operations[k];
                double previousMs = k > 0 ? operations[k - 1].TimestampMs : sourcePts[0];
                double nextMs = k + 1 < operations.Count ? operations[k + 1].TimestampMs : sourcePts[sourcePts.Length - 1];

                bool hasBefore = this.TryMeasure(pair, previousMs + EditAnalysisProfile.DURATION_GUARD_MS, operation.TimestampMs - EditAnalysisProfile.DURATION_GUARD_MS, operation.OffsetBeforeMs, out double offsetBeforeMs, out double widthBefore);
                bool hasAfter = this.TryMeasure(pair, operation.TimestampMs + EditAnalysisProfile.DURATION_GUARD_MS, nextMs - EditAnalysisProfile.DURATION_GUARD_MS, operation.OffsetAfterMs, out double offsetAfterMs, out double widthAfter);
                if (!hasBefore || !hasAfter)
                {
                    result.Add(operation);
                    continue;
                }

                EditOperationCandidate measured = operation.Clone();
                measured.OffsetBeforeMs = offsetBeforeMs;
                measured.OffsetAfterMs = offsetAfterMs;
                measured.DurationMs = Math.Abs(offsetAfterMs - offsetBeforeMs);
                measured.UncertaintyMs = Math.Max(widthBefore, widthAfter);
                result.Add(measured);
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Centro e larghezza della cima piatta della copertura su un pianoro
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="startMs">Inizio del pianoro</param>
        /// <param name="endMs">Fine del pianoro</param>
        /// <param name="centerMs">Offset da cui partire a cercare</param>
        /// <param name="offsetMs">Centro della cima piatta</param>
        /// <param name="widthMs">Larghezza della cima, cioè l'incertezza della misura</param>
        /// <returns>True quando il pianoro basta a misurare un offset</returns>
        private bool TryMeasure(PairSignals pair, double startMs, double endMs, double centerMs, out double offsetMs, out double widthMs)
        {
            // La domanda giusta non è "quale offset minimizza la distanza mediana" ma "quale
            // tiene agganciati più fotogrammi del pianoro intero": non è la stessa cosa
            offsetMs = centerMs;
            widthMs = 0.0;
            if (endMs - startMs < EditAnalysisProfile.DURATION_MIN_PLATEAU_MS)
                return false;
            HashOps.Range(pair, startMs, endMs, EditAnalysisProfile.SAMPLING_STRIDE, out int first, out int indexCount);
            if (indexCount < 30)
                return false;

            double bestFraction = -1.0;
            double bestOffsetMs = centerMs;
            int coarseCount = (int)Math.Floor(2.0 * EditAnalysisProfile.DURATION_RADIUS_MS / EditAnalysisProfile.DURATION_COARSE_STEP_MS) + 1;
            double[] coarse = this._hashBackend.Scan(first, EditAnalysisProfile.SAMPLING_STRIDE, indexCount, centerMs - EditAnalysisProfile.DURATION_RADIUS_MS, EditAnalysisProfile.DURATION_COARSE_STEP_MS, coarseCount, EditAnalysisProfile.DETECTION_RADIUS, EditAnalysisProfile.DETECTION_THRESHOLD);
            for (int i = 0; i < coarseCount; i++)
            {
                if (coarse[i] > bestFraction)
                {
                    bestFraction = coarse[i];
                    bestOffsetMs = centerMs - EditAnalysisProfile.DURATION_RADIUS_MS + i * EditAnalysisProfile.DURATION_COARSE_STEP_MS;
                }
            }
            if (bestFraction < 0.5)
                return false;

            // La cima è piatta -- tutti gli offset che cadono dentro lo stesso fotogramma lang
            // tengono gli stessi fotogrammi -- e va presa al centro
            int fineCount = (int)(2.0 * EditAnalysisProfile.DURATION_FINE_RADIUS_MS) + 1;
            double[] fractions = this._hashBackend.Scan(first, EditAnalysisProfile.SAMPLING_STRIDE, indexCount, bestOffsetMs - EditAnalysisProfile.DURATION_FINE_RADIUS_MS, 1.0, fineCount, EditAnalysisProfile.DETECTION_RADIUS, EditAnalysisProfile.DETECTION_THRESHOLD);
            double peak = -1.0;
            for (int i = 0; i < fineCount; i++)
                peak = Math.Max(peak, fractions[i]);

            double total = 0.0;
            int members = 0;
            int firstMember = -1;
            int lastMember = -1;
            for (int i = 0; i < fineCount; i++)
            {
                if (fractions[i] < peak - 1e-12)
                    continue;
                total += bestOffsetMs - EditAnalysisProfile.DURATION_FINE_RADIUS_MS + i;
                members++;
                if (firstMember < 0)
                    firstMember = i;
                lastMember = i;
            }

            offsetMs = total / members;
            widthMs = lastMember - firstMember;
            return true;
        }

        #endregion
    }
}
