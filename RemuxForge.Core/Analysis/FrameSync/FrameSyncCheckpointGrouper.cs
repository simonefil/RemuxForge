using RemuxForge.Core.Models;
using System;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Calcola gruppi coerenti e criteri di retry checkpoint FrameSync
    /// </summary>
    public class FrameSyncCheckpointGrouper
    {
        #region Variabili di classe

        private readonly VideoSyncConfig _videoSyncConfig;
        private readonly FrameSyncConfig _frameSyncConfig;

        #endregion

        #region Costruttore

        public FrameSyncCheckpointGrouper(VideoSyncConfig videoSyncConfig, FrameSyncConfig frameSyncConfig)
        {
            this._videoSyncConfig = videoSyncConfig;
            this._frameSyncConfig = frameSyncConfig;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Calcola score medio e minimo del gruppo checkpoint finale
        /// </summary>
        public void ComputeGroupScore(int groupOffset, int groupingToleranceMs, bool[] pointValid, int[] offsets, FrameSyncPointResult[] pointResults, double[] ssimValues, out double averageScore, out double minScore)
        {
            double sum = 0.0;
            int count = 0;
            averageScore = 0.0;
            minScore = 1.0;

            for (int p = 0; p < this._videoSyncConfig.NumCheckPoints; p++)
            {
                if (pointValid[p] && Math.Abs(offsets[p] - groupOffset) <= groupingToleranceMs)
                {
                    double score = pointResults[p] != null ? pointResults[p].BestScore : ssimValues[p];
                    if (score < 0.0) { score = -score; }
                    if (score > 1.0) { score = 1.0; }

                    sum += score;
                    if (score < minScore)
                    {
                        minScore = score;
                    }
                    count++;
                }
            }

            if (count > 0)
            {
                averageScore = sum / count;
            }
            else
            {
                minScore = 0.0;
            }
        }

        /// <summary>
        /// Valuta se il primo passaggio checkpoint è già sufficiente per saltare il retry
        /// </summary>
        public bool CanSkipRetry(int initialDelay, double fps, bool[] pointValid, int[] offsets, FrameSyncPointResult[] pointResults, double[] ssimValues, out int validCount, out int bestGroupCount, out double bestGroupScoreAverage)
        {
            bool result = false;
            double frameIntervalMs = 1000.0 / fps;
            int groupingToleranceMs = (int)Math.Round(frameIntervalMs * this._frameSyncConfig.GroupingToleranceFrames);
            int bestGroupOffset = 0;
            int groupCount;
            int groupSum;
            double initialFinalDeltaMs;
            validCount = 0;
            bestGroupCount = 0;
            bestGroupScoreAverage = 0.0;

            for (int i = 0; i < this._videoSyncConfig.NumCheckPoints; i++)
            {
                if (pointValid[i])
                {
                    validCount++;
                }
            }

            if (validCount < this._frameSyncConfig.MinValidPoints)
            {
                return result;
            }

            for (int i = 0; i < this._videoSyncConfig.NumCheckPoints; i++)
            {
                if (!pointValid[i])
                {
                    continue;
                }

                groupCount = 0;
                groupSum = 0;

                for (int j = 0; j < this._videoSyncConfig.NumCheckPoints; j++)
                {
                    if (pointValid[j] && Math.Abs(offsets[i] - offsets[j]) <= groupingToleranceMs)
                    {
                        groupCount++;
                        groupSum += offsets[j];
                    }
                }

                if (groupCount > bestGroupCount)
                {
                    bestGroupCount = groupCount;
                    bestGroupOffset = groupSum / groupCount;
                }
            }

            if (bestGroupCount < this._frameSyncConfig.MinValidPoints)
            {
                return result;
            }

            this.ComputeGroupScore(bestGroupOffset, groupingToleranceMs, pointValid, offsets, pointResults, ssimValues, out bestGroupScoreAverage, out _);
            if (bestGroupScoreAverage < this._frameSyncConfig.FinalMinConfidence)
            {
                return result;
            }

            initialFinalDeltaMs = Math.Abs(bestGroupOffset - initialDelay);
            if (initialFinalDeltaMs > frameIntervalMs * this._frameSyncConfig.InitialCheckpointDriftRejectFrames)
            {
                return result;
            }

            result = true;

            return result;
        }

        #endregion
    }
}
