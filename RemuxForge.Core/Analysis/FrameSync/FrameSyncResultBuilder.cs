using RemuxForge.Core.Models;
using System;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Costruisce il risultato dettagliato FrameSync dai dati prodotti dal flusso legacy
    /// </summary>
    public class FrameSyncResultBuilder
    {
        #region Metodi pubblici

        /// <summary>
        /// Costruisce il risultato dettagliato usando i dati prodotti dall'algoritmo legacy
        /// </summary>
        public FrameSyncResult Build(
            int finalOffset,
            int initialDelay,
            string failureReason,
            FrameSyncGeometryInfo sourceGeometry,
            FrameSyncGeometryInfo languageGeometry,
            AudioGlobalFingerprintResult audioGlobalResult,
            FrameSyncTimingInfo timing,
            FrameSyncInitialResult initialResult,
            FrameSyncPointResult[] pointResults,
            bool[] pointValid,
            int[] offsets,
            double[] ssimValues,
            int numCheckPoints,
            int minValidPoints,
            double finalMinConfidence)
        {
            FrameSyncResult result = new FrameSyncResult();
            int validCount = 0;
            double scoreSum = 0.0;
            double score;
            result.OffsetMs = finalOffset;
            result.FailureReason = failureReason;
            result.SourceGeometry = sourceGeometry;
            result.LanguageGeometry = languageGeometry;
            result.AudioGlobal = audioGlobalResult != null ? audioGlobalResult : new AudioGlobalFingerprintResult();
            result.Timing = timing;

            this.FillInitialResult(result, initialResult, initialDelay, failureReason);

            for (int p = 0; p < numCheckPoints; p++)
            {
                FrameSyncPointResult point = pointResults[p] != null ? pointResults[p] : new FrameSyncPointResult();

                if (point.CheckpointPercent == 0)
                {
                    point.CheckpointPercent = (p + 1) * 10;
                }
                if (point.ExpectedOffsetMs == 0 && initialDelay != int.MinValue)
                {
                    point.ExpectedOffsetMs = -initialDelay;
                }
                if (pointValid[p])
                {
                    point.Accepted = true;
                    point.BestOffsetMs = -offsets[p];
                }

                if (point.BestScore <= 0.0 && ssimValues[p] < 0.0)
                {
                    point.BestScore = -ssimValues[p];
                    point.MatchMethod = FrameSyncCandidate.TEMPORAL_FINGERPRINT;
                }
                else if (point.BestScore <= 0.0)
                {
                    point.BestScore = ssimValues[p];
                    point.MatchMethod = "SSIM";
                }

                if (point.Accepted)
                {
                    validCount++;
                    score = point.BestScore;
                    if (score < 0.0) { score = 0.0; }
                    if (score > 1.0) { score = 1.0; }
                    scoreSum += score;
                }
                else if (string.IsNullOrEmpty(point.RejectReason))
                {
                    point.RejectReason = "Nessun match";
                }

                result.Points.Add(point);
            }

            if (validCount > 0)
            {
                if (finalOffset != int.MinValue)
                {
                    result.Confidence = scoreSum / validCount;
                }
                else
                {
                    result.Confidence = (scoreSum / validCount) * (validCount / (double)numCheckPoints);
                }
                if (result.Confidence > 1.0) { result.Confidence = 1.0; }
            }

            result.Success = finalOffset != int.MinValue &&
                result.Initial.Success &&
                validCount >= minValidPoints &&
                result.Confidence >= finalMinConfidence;

            if (!result.Success && finalOffset != int.MinValue && string.IsNullOrEmpty(result.FailureReason))
            {
                if (!result.Initial.Success)
                {
                    result.FailureReason = "Delay iniziale non verificato";
                }
                else if (validCount < minValidPoints)
                {
                    result.FailureReason = "Punti validi insufficienti";
                }
                else if (result.Confidence < finalMinConfidence)
                {
                    result.FailureReason = "Confidence finale insufficiente";
                }
            }

            if (!result.Success && !string.IsNullOrEmpty(failureReason))
            {
                result.Ambiguous = failureReason.IndexOf("coerenti", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Compila la sezione initial del risultato
        /// </summary>
        private void FillInitialResult(FrameSyncResult result, FrameSyncInitialResult initialResult, int initialDelay, string failureReason)
        {
            if (initialResult != null && initialResult.Candidates.Count > 0)
            {
                result.Initial.Success = initialResult.Success;
                result.Initial.Ambiguous = initialResult.Ambiguous;
                result.Initial.FailureReason = initialResult.FailureReason;

                for (int i = 0; i < initialResult.Candidates.Count; i++)
                {
                    FrameSyncCandidate sourceCandidate = initialResult.Candidates[i];
                    FrameSyncCandidate displayCandidate = this.BuildDisplayCandidate(sourceCandidate);
                    result.Initial.Candidates.Add(displayCandidate);

                    if (initialResult.BestCandidate == sourceCandidate)
                    {
                        result.Initial.BestCandidate = displayCandidate;
                    }
                }

                if (result.Initial.BestCandidate == null && initialDelay != int.MinValue)
                {
                    for (int i = 0; i < result.Initial.Candidates.Count; i++)
                    {
                        if (Math.Abs(result.Initial.Candidates[i].OffsetMs + initialDelay) <= 1)
                        {
                            result.Initial.BestCandidate = result.Initial.Candidates[i];
                            break;
                        }
                    }
                }
            }
            else if (initialDelay != int.MinValue)
            {
                result.Initial.Success = false;
                result.Initial.FailureReason = "Delay iniziale senza candidato verificato";
            }
            else
            {
                result.Initial.Success = false;
                result.Initial.FailureReason = failureReason;
            }
        }

        /// <summary>
        /// Crea il candidato da esporre ribaltando il segno dell'offset interno
        /// </summary>
        private FrameSyncCandidate BuildDisplayCandidate(FrameSyncCandidate sourceCandidate)
        {
            FrameSyncCandidate displayCandidate = new FrameSyncCandidate();
            displayCandidate.OffsetMs = -sourceCandidate.OffsetMs;
            displayCandidate.Source = sourceCandidate.Source;
            displayCandidate.VoteCount = sourceCandidate.VoteCount;
            displayCandidate.VisualScore = sourceCandidate.VisualScore;
            displayCandidate.BlurScore = sourceCandidate.BlurScore;
            displayCandidate.TemporalScore = sourceCandidate.TemporalScore;
            displayCandidate.EdgeScore = sourceCandidate.EdgeScore;
            displayCandidate.BlockScore = sourceCandidate.BlockScore;
            displayCandidate.MotionScore = sourceCandidate.MotionScore;
            displayCandidate.HashScore = sourceCandidate.HashScore;
            displayCandidate.DescriptorVotes = sourceCandidate.DescriptorVotes;
            displayCandidate.DescriptorAgreement = sourceCandidate.DescriptorAgreement;
            displayCandidate.CombinedScore = sourceCandidate.CombinedScore;
            displayCandidate.SecondBestScore = sourceCandidate.SecondBestScore;
            displayCandidate.Margin = sourceCandidate.Margin;
            displayCandidate.MatchedCuts = sourceCandidate.MatchedCuts;

            return displayCandidate;
        }

        #endregion
    }
}
