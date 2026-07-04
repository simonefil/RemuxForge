using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Calcola score e selezione candidati FrameSync
    /// </summary>
    public class FrameSyncCandidateScorer
    {
        #region Variabili di classe

        /// <summary>
        /// Configurazione VideoSync
        /// </summary>
        private readonly VideoSyncConfig _videoSyncConfig;

        /// <summary>
        /// Configurazione FrameSync
        /// </summary>
        private readonly FrameSyncConfig _frameSyncConfig;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public FrameSyncCandidateScorer(VideoSyncConfig videoSyncConfig, FrameSyncConfig frameSyncConfig)
        {
            this._videoSyncConfig = videoSyncConfig;
            this._frameSyncConfig = frameSyncConfig;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Calcola uno score normalizzato per un candidato già verificato
        /// </summary>
        public double ComputeCandidateScore(double temporalScore, double matchScore, double blurScore, double edgeScore, double blockScore, double motionScore, double hashScore)
        {
            double normalizedMatchScore = Math.Abs(matchScore);
            double normalizedBlurScore = blurScore;
            double normalizedEdgeScore = edgeScore;
            double normalizedBlockScore = blockScore;
            double normalizedMotionScore = motionScore;
            double normalizedHashScore = hashScore;
            double result = 0.0;
            if (normalizedMatchScore > 1.0)
            {
                normalizedMatchScore = 1.0;
            }
            if (normalizedBlurScore < 0.0) { normalizedBlurScore = 0.0; }
            if (normalizedBlurScore > 1.0) { normalizedBlurScore = 1.0; }
            if (normalizedEdgeScore < 0.0) { normalizedEdgeScore = 0.0; }
            if (normalizedEdgeScore > 1.0) { normalizedEdgeScore = 1.0; }
            if (normalizedBlockScore < 0.0) { normalizedBlockScore = 0.0; }
            if (normalizedBlockScore > 1.0) { normalizedBlockScore = 1.0; }
            if (normalizedMotionScore < 0.0) { normalizedMotionScore = 0.0; }
            if (normalizedMotionScore > 1.0) { normalizedMotionScore = 1.0; }
            if (normalizedHashScore < 0.0) { normalizedHashScore = 0.0; }
            if (normalizedHashScore > 1.0) { normalizedHashScore = 1.0; }

            if (temporalScore > 0.0)
            {
                result = (temporalScore * 0.20) + (normalizedMatchScore * 0.25) + (normalizedBlurScore * 0.13) + (normalizedEdgeScore * 0.14) + (normalizedBlockScore * 0.12) + (normalizedMotionScore * 0.10) + (normalizedHashScore * 0.06);
            }

            return result;
        }

        /// <summary>
        /// Calcola lo score visuale combinando SSIM, blur, edge, blocchi, movimento e hash
        /// </summary>
        public double ComputeVisualCandidateScore(double ssim, double blurScore, double edgeScore, double blockScore, double motionScore, double hashScore)
        {
            double normalizedSsim = ssim;
            double normalizedBlur = blurScore;
            double normalizedEdge = edgeScore;
            double normalizedBlock = blockScore;
            double normalizedMotion = motionScore;
            double normalizedHash = hashScore;
            double result;
            if (normalizedSsim < 0.0) { normalizedSsim = 0.0; }
            if (normalizedSsim > 1.0) { normalizedSsim = 1.0; }
            if (normalizedBlur < 0.0) { normalizedBlur = 0.0; }
            if (normalizedBlur > 1.0) { normalizedBlur = 1.0; }
            if (normalizedEdge < 0.0) { normalizedEdge = 0.0; }
            if (normalizedEdge > 1.0) { normalizedEdge = 1.0; }
            if (normalizedBlock < 0.0) { normalizedBlock = 0.0; }
            if (normalizedBlock > 1.0) { normalizedBlock = 1.0; }
            if (normalizedMotion < 0.0) { normalizedMotion = 0.0; }
            if (normalizedMotion > 1.0) { normalizedMotion = 1.0; }
            if (normalizedHash < 0.0) { normalizedHash = 0.0; }
            if (normalizedHash > 1.0) { normalizedHash = 1.0; }

            result = (normalizedSsim * 0.23) + (normalizedBlur * 0.16) + (normalizedEdge * 0.22) + (normalizedBlock * 0.17) + (normalizedMotion * 0.14) + (normalizedHash * 0.08);

            return result;
        }

        /// <summary>
        /// Conta quanti descriptor visuali confermano il match
        /// </summary>
        public int CountDescriptorVotes(double ssim, double blurScore, double edgeScore, double blockScore, double motionScore, double hashScore)
        {
            int result = 0;
            if (ssim >= this._videoSyncConfig.SsimThreshold && ssim <= this._videoSyncConfig.SsimMaxThreshold)
            {
                result++;
            }
            if (blurScore >= this._frameSyncConfig.MinBlurredCorrelation)
            {
                result++;
            }
            if (edgeScore >= this._frameSyncConfig.MinEdgeCorrelation)
            {
                result++;
            }
            if (blockScore >= this._frameSyncConfig.MinBlockCorrelation)
            {
                result++;
            }
            if (motionScore >= this._frameSyncConfig.MinMotionCorrelation)
            {
                result++;
            }
            if (hashScore >= this._frameSyncConfig.MinHashSimilarity)
            {
                result++;
            }

            return result;
        }

        /// <summary>
        /// Seleziona il miglior candidato verificato e valuta l'ambiguita' contro il secondo
        /// </summary>
        public FrameSyncCandidate SelectBestCandidate(List<FrameSyncCandidate> candidates, double frameIntervalMs, int minMatchedCuts, double minScore, double minMargin, out FrameSyncCandidate secondCandidate, out double margin, out bool ambiguous)
        {
            FrameSyncCandidate bestCandidate = null;
            FrameSyncCandidate currentCandidate;
            double offsetDistance;
            secondCandidate = null;
            margin = 1.0;
            ambiguous = false;

            for (int i = 0; i < candidates.Count; i++)
            {
                currentCandidate = candidates[i];

                if (currentCandidate.MatchedCuts < minMatchedCuts || currentCandidate.CombinedScore < minScore)
                {
                    continue;
                }

                if (bestCandidate == null || currentCandidate.CombinedScore > bestCandidate.CombinedScore)
                {
                    secondCandidate = bestCandidate;
                    bestCandidate = currentCandidate;
                }
                else if (secondCandidate == null || currentCandidate.CombinedScore > secondCandidate.CombinedScore)
                {
                    secondCandidate = currentCandidate;
                }
            }

            if (bestCandidate != null && secondCandidate != null)
            {
                margin = bestCandidate.CombinedScore - secondCandidate.CombinedScore;
                offsetDistance = Math.Abs(bestCandidate.OffsetMs - secondCandidate.OffsetMs);
                ambiguous = margin < minMargin && offsetDistance > frameIntervalMs;
            }

            return bestCandidate;
        }

        #endregion
    }
}
