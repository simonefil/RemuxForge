using RemuxForge.Core.Analysis.Edit.Extraction;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Core.Analysis.Edit.Duration
{
    /// <summary>
    /// Quantizza le durate sulla griglia video e usa l'audio solo per sciogliere un frame ambiguo
    /// </summary>
    internal class OperationDurationRefiner
    {
        #region Variabili di istanza

        /// <summary>
        /// Inviluppi sulla griglia comune, oppure null
        /// </summary>
        private AudioEnvelopePair _envelopes;

        /// <summary>
        /// Energia lang compensata del divario di livello
        /// </summary>
        private float[] _language;

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
        /// <param name="envelopes">Inviluppi audio oppure null</param>
        public OperationDurationRefiner(HashBackendBase hashBackend, AudioEnvelopePair envelopes)
        {
            this._hashBackend = hashBackend;
            this._envelopes = envelopes;
            if (envelopes == null)
                return;

            float[] present = Array.FindAll(envelopes.Language, value => value > AudioEnvelopePair.SILENCE_FLOOR_DB + 1.0);
            if (present.Length == 0)
            {
                this._language = envelopes.Language;
                return;
            }

            double shift = AudioEnvelopePair.Percentile(envelopes.Source, 50.0) - AudioEnvelopePair.Percentile(present, 50.0);
            this._language = new float[envelopes.Language.Length];
            for (int i = 0; i < this._language.Length; i++)
                this._language[i] = (float)(envelopes.Language[i] + shift);
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

            double frameStepMs = MedianStep(pair.LanguagePtsMs);
            bool[] closedExcursions = this.FindClosedExcursions(operations, frameStepMs);
            double offsetMs = operations[0].OffsetBeforeMs;
            for (int operationIndex = 0; operationIndex < operations.Count; operationIndex++)
            {
                EditOperationCandidate operation = operations[operationIndex];
                EditOperationCandidate refined = operation.Clone();
                int videoFrames = Math.Max(1, (int)Math.Round(operation.DurationMs / frameStepMs));
                int frames = videoFrames;
                if (!closedExcursions[operationIndex] && this.TryMeasureJump(operation, out double audioJumpMs))
                {
                    int audioFrames = Math.Max(1, (int)Math.Round(Math.Abs(audioJumpMs) / frameStepMs));
                    double videoJumpMs = operation.OffsetAfterMs - operation.OffsetBeforeMs;
                    if (Math.Sign(audioJumpMs) == Math.Sign(videoJumpMs) && Math.Abs(audioFrames - videoFrames) == 1)
                        frames = audioFrames;
                }

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
        /// Elimina le sole transizioni che sono fase di un fotogramma o che l'audio misura ferme
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
            double frameStepMs = MedianStep(pair.LanguagePtsMs);
            bool duplicateCadence = HasDuplicateCadence(pair.Source);
            bool anyRejected = false;
            foreach (EditOperationCandidate operation in operations)
            {
                int frames = (int)Math.Round(operation.DurationMs / frameStepMs);
                if (frames <= 1)
                {
                    operation.RejectReason = "transizione entro la fase di un fotogramma";
                    rejected.Add(operation);
                    anyRejected = true;
                    continue;
                }
                if (duplicateCadence && !this.AudioHolds(operation))
                {
                    operation.RejectReason = "l'audio misura fermo lo stesso pianoro";
                    rejected.Add(operation);
                    anyRejected = true;
                    continue;
                }
                accepted.Add(operation.Clone());
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
        /// True quando l'audio conferma il verso e almeno metà del salto, oppure si astiene
        /// </summary>
        private bool AudioHolds(EditOperationCandidate operation)
        {
            if (!this.TryMeasureJump(operation, out double audioJumpMs))
                return true;
            double videoJumpMs = operation.OffsetAfterMs - operation.OffsetBeforeMs;
            return Math.Sign(audioJumpMs) == Math.Sign(videoJumpMs) &&
                   Math.Abs(audioJumpMs / videoJumpMs) >= EditAnalysisProfile.AUDIO_MIN_STEP_RATIO;
        }

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

        /// <summary>
        /// Individua le coppie A-B-A già determinate completamente dal video
        /// </summary>
        private bool[] FindClosedExcursions(IReadOnlyList<EditOperationCandidate> operations, double frameStepMs)
        {
            bool[] result = new bool[operations.Count];
            for (int i = 0; i + 1 < operations.Count; i++)
            {
                EditOperationCandidate first = operations[i];
                EditOperationCandidate second = operations[i + 1];
                int firstFrames = (int)Math.Round(first.DurationMs / frameStepMs);
                int secondFrames = (int)Math.Round(second.DurationMs / frameStepMs);
                if (first.Kind == second.Kind || firstFrames != secondFrames)
                    continue;
                result[i] = true;
                result[i + 1] = true;
            }
            return result;
        }

        /// <summary>
        /// Misura il salto audio soltanto quando due finestre per pianoro concordano
        /// </summary>
        /// <param name="operation">Operazione video</param>
        /// <param name="jumpMs">Salto audio misurato</param>
        /// <returns>True quando la misura audio è affidabile</returns>
        private bool TryMeasureJump(EditOperationCandidate operation, out double jumpMs)
        {
            jumpMs = 0.0;
            if (this._envelopes == null)
                return false;

            double? before = this.PlateauOffset(operation.TimestampMs - EditAnalysisProfile.AUDIO_GUARD_MS - EditAnalysisProfile.AUDIO_WINDOW_MS, operation.OffsetBeforeMs);
            double? after = this.PlateauOffset(operation.ResumeMs + EditAnalysisProfile.AUDIO_GUARD_MS + EditAnalysisProfile.AUDIO_WINDOW_MS / 2.0, operation.OffsetAfterMs);
            if (!before.HasValue || !after.HasValue)
                return false;
            jumpMs = after.Value - before.Value;
            return true;
        }

        /// <summary>
        /// Offset audio di un pianoro, accettato soltanto da due finestre concordi
        /// </summary>
        private double? PlateauOffset(double startMs, double centerMs)
        {
            double? first = this.Correlate(startMs, centerMs);
            double? second = this.Correlate(startMs - EditAnalysisProfile.AUDIO_WINDOW_MS / 2.0, centerMs);
            if (!first.HasValue || !second.HasValue || Math.Abs(first.Value - second.Value) > EditAnalysisProfile.AUDIO_AGREEMENT_MS)
                return null;
            return (first.Value + second.Value) / 2.0;
        }

        /// <summary>
        /// Ritardo che massimizza la correlazione normalizzata fra due finestre audio
        /// </summary>
        private double? Correlate(double startMs, double centerMs)
        {
            float[] source = this._envelopes.Source;
            int length = (int)(EditAnalysisProfile.AUDIO_WINDOW_MS / AudioEnvelopeExtractor.STEP_MS);
            int sourceIndex = this._envelopes.IndexOf(startMs);
            if (sourceIndex < 0 || sourceIndex + length > source.Length)
                return null;

            double sourceMean = 0.0;
            for (int i = 0; i < length; i++)
                sourceMean += source[sourceIndex + i];
            sourceMean /= length;
            double sourceEnergy = 0.0;
            for (int i = 0; i < length; i++)
                sourceEnergy += (source[sourceIndex + i] - sourceMean) * (source[sourceIndex + i] - sourceMean);

            double? result = null;
            double best = -9.0;
            int lagCount = (int)Math.Ceiling(2.0 * EditAnalysisProfile.AUDIO_SCAN_RADIUS_MS / EditAnalysisProfile.AUDIO_SCAN_STEP_MS);
            for (int k = 0; k < lagCount; k++)
            {
                double lagMs = centerMs - EditAnalysisProfile.AUDIO_SCAN_RADIUS_MS + k * EditAnalysisProfile.AUDIO_SCAN_STEP_MS;
                int languageIndex = this._envelopes.IndexOf(startMs + lagMs);
                if (languageIndex < 0 || languageIndex + length > this._language.Length)
                    continue;

                double languageMean = 0.0;
                for (int i = 0; i < length; i++)
                    languageMean += this._language[languageIndex + i];
                languageMean /= length;
                double cross = 0.0;
                double languageEnergy = 0.0;
                for (int i = 0; i < length; i++)
                {
                    double left = source[sourceIndex + i] - sourceMean;
                    double right = this._language[languageIndex + i] - languageMean;
                    cross += left * right;
                    languageEnergy += right * right;
                }

                double norm = Math.Sqrt(sourceEnergy * languageEnergy);
                double correlation = norm > 0.0 ? cross / norm : 0.0;
                if (correlation > best)
                {
                    best = correlation;
                    result = lagMs;
                }
            }
            return result;
        }

        /// <summary>
        /// Passo mediano di una sequenza di PTS
        /// </summary>
        private static double MedianStep(double[] values)
        {
            double[] steps = new double[values.Length - 1];
            for (int i = 1; i < values.Length; i++)
                steps[i - 1] = values[i] - values[i - 1];
            Array.Sort(steps);
            int middle = steps.Length / 2;
            return steps.Length % 2 == 1 ? steps[middle] : (steps[middle - 1] + steps[middle]) / 2.0;
        }

        /// <summary>
        /// True quando oltre metà dei fotogrammi ripete esattamente il precedente
        /// </summary>
        private static bool HasDuplicateCadence(FrameSignals signals)
        {
            if (signals.Count < 2)
                return false;
            int duplicates = 0;
            for (int i = 1; i < signals.Count; i++)
            {
                if (signals.Hash0[i] == signals.Hash0[i - 1] && signals.Hash1[i] == signals.Hash1[i - 1])
                    duplicates++;
            }
            return duplicates * 2 > signals.Count - 1;
        }

        #endregion
    }
}
