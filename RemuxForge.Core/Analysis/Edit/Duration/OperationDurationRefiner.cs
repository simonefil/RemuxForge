using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Core.Analysis.Edit.Duration
{
    /// <summary>
    /// Misura gli offset e quantizza le durate sulla griglia video
    /// </summary>
    internal class OperationDurationRefiner
    {
        #region Variabili di istanza

        /// <summary>
        /// Backend usato per centrare gli offset dei pianori
        /// </summary>
        private HashBackendBase _hashBackend;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="hashBackend">Backend degli hash già legato alla coppia</param>
        public OperationDurationRefiner(HashBackendBase hashBackend)
        {
            this._hashBackend = hashBackend;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Centra gli offset assoluti usati per decidere i confini senza alterare le durate globali
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="operations">Operazioni globali ordinate</param>
        /// <param name="cancellation">Token di annullamento</param>
        /// <returns>Operazioni con offset di confine centrati</returns>
        public List<EditOperationCandidate> MeasureBoundaryOffsets(PairSignals pair, IReadOnlyList<EditOperationCandidate> operations, CancellationToken cancellation)
        {
            List<EditOperationCandidate> result = new List<EditOperationCandidate>();
            if (operations.Count == 0)
                return result;

            double[] centers = new double[operations.Count + 1];
            centers[0] = operations[0].OffsetBeforeMs;
            for (int i = 0; i < operations.Count; i++)
                centers[i + 1] = operations[i].OffsetAfterMs;

            double[] offsets = new double[centers.Length];
            double[] sourcePts = pair.Source.PtsMs;
            for (int i = 0; i < centers.Length; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                double startMs = i == 0 ? sourcePts[0] : operations[i - 1].ResumeMs;
                double endMs = i == operations.Count ? sourcePts[sourcePts.Length - 1] : operations[i].TimestampMs;
                if (!this.TryMeasureOffset(pair, startMs + EditAnalysisProfile.DURATION_GUARD_MS,
                        endMs - EditAnalysisProfile.DURATION_GUARD_MS, centers[i], out offsets[i]))
                    offsets[i] = centers[i];
            }

            for (int i = 0; i < operations.Count; i++)
            {
                EditOperationCandidate measured = operations[i].Clone();
                measured.OffsetBeforeMs = offsets[i];
                measured.OffsetAfterMs = offsets[i + 1];
                result.Add(measured);
            }
            return result;
        }

        /// <summary>
        /// Restituisce una scala continua con durate intere in fotogrammi lang
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="operations">Operazioni ordinate</param>
        /// <returns>Operazioni con durate raffinate</returns>
        public List<EditOperationCandidate> Apply(PairSignals pair, IReadOnlyList<EditOperationCandidate> operations)
        {
            List<EditOperationCandidate> result = new List<EditOperationCandidate>();
            if (operations.Count == 0)
                return result;

            double frameStepMs = HashOps.FrameStep(pair.LanguagePtsMs, pair.Stretch);
            double offsetMs = operations[0].OffsetBeforeMs;
            for (int operationIndex = 0; operationIndex < operations.Count; operationIndex++)
            {
                EditOperationCandidate operation = operations[operationIndex];
                EditOperationCandidate refined = operation.Clone();
                int frames = Math.Max(1, (int)Math.Round(operation.DurationMs / frameStepMs));
                refined.OffsetBeforeMs = offsetMs;
                refined.DurationMs = frames * frameStepMs;
                offsetMs += refined.Kind == EditOperationKind.InsertSilence ? -refined.DurationMs : refined.DurationMs;
                refined.OffsetAfterMs = offsetMs;
                refined.UncertaintyMs = 0.0;
                result.Add(refined);
            }
            return result;
        }

        /// <summary>
        /// Misura le durate dagli offset video raffinati ed elimina le transizioni entro la fase di un fotogramma
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="operations">Operazioni video ordinate</param>
        /// <param name="rejected">Destinazione delle operazioni scartate</param>
        /// <returns>Scala ricucita senza le transizioni non sostenute</returns>
        public List<EditOperationCandidate> Filter(PairSignals pair, IReadOnlyList<EditOperationCandidate> operations, List<EditOperationCandidate> rejected)
        {
            List<EditOperationCandidate> accepted = new List<EditOperationCandidate>();
            if (operations.Count == 0)
                return accepted;
            double frameStepMs = HashOps.FrameStep(pair.LanguagePtsMs, pair.Stretch);
            bool anyRejected = false;
            foreach (EditOperationCandidate operation in operations)
            {
                // Gli offset sono stati raffinati sui frame: la durata iniziale del solver può essere imprecisa
                double measuredJumpMs = operation.OffsetAfterMs - operation.OffsetBeforeMs;
                int frames = (int)Math.Round(Math.Abs(measuredJumpMs) / frameStepMs);
                if (frames <= 1)
                {
                    operation.RejectReason = "transizione entro la fase di un fotogramma";
                    rejected.Add(operation);
                    anyRejected = true;
                    continue;
                }
                EditOperationCandidate measured = operation.Clone();
                measured.DurationMs = Math.Abs(measuredJumpMs);
                measured.Kind = measuredJumpMs < 0.0 ? EditOperationKind.InsertSilence : EditOperationKind.CutSegment;
                accepted.Add(measured);
            }

            if (accepted.Count == 0 || !anyRejected)
                return accepted;
            double offsetMs = operations[0].OffsetBeforeMs;
            foreach (EditOperationCandidate operation in accepted)
            {
                operation.OffsetBeforeMs = offsetMs;
                operation.DurationMs = Math.Abs(operation.OffsetAfterMs - offsetMs);
                operation.Kind = operation.OffsetAfterMs < offsetMs ? EditOperationKind.InsertSilence : EditOperationKind.CutSegment;
                offsetMs = operation.OffsetAfterMs;
            }
            return accepted;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Centro della cima di copertura di un intero pianoro
        /// </summary>
        private bool TryMeasureOffset(PairSignals pair, double startMs, double endMs, double centerMs, out double offsetMs)
        {
            offsetMs = centerMs;
            if (endMs - startMs < EditAnalysisProfile.DURATION_MIN_PLATEAU_MS)
                return false;
            HashOps.Range(pair, startMs, endMs, EditAnalysisProfile.SAMPLING_STRIDE, out int first, out int indexCount);
            if (indexCount < 30)
                return false;

            double bestFraction = -1.0;
            double bestOffsetMs = centerMs;
            int coarseCount = (int)Math.Floor(2.0 * EditAnalysisProfile.DURATION_RADIUS_MS / EditAnalysisProfile.DURATION_COARSE_STEP_MS) + 1;
            double[] coarse = this._hashBackend.Scan(first, EditAnalysisProfile.SAMPLING_STRIDE, indexCount,
                centerMs - EditAnalysisProfile.DURATION_RADIUS_MS, EditAnalysisProfile.DURATION_COARSE_STEP_MS,
                coarseCount, 0, EditAnalysisProfile.DETECTION_THRESHOLD);
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

            int fineCount = (int)(2.0 * EditAnalysisProfile.DURATION_FINE_RADIUS_MS) + 1;
            double[] fractions = this._hashBackend.Scan(first, EditAnalysisProfile.SAMPLING_STRIDE, indexCount,
                bestOffsetMs - EditAnalysisProfile.DURATION_FINE_RADIUS_MS, 1.0, fineCount,
                0, EditAnalysisProfile.DETECTION_THRESHOLD);
            double peak = -1.0;
            for (int i = 0; i < fineCount; i++)
                peak = Math.Max(peak, fractions[i]);

            double total = 0.0;
            int members = 0;
            for (int i = 0; i < fineCount; i++)
            {
                if (fractions[i] < peak - 1e-12)
                    continue;
                total += bestOffsetMs - EditAnalysisProfile.DURATION_FINE_RADIUS_MS + i;
                members++;
            }
            offsetMs = total / members;
            return true;
        }

        #endregion
    }
}
