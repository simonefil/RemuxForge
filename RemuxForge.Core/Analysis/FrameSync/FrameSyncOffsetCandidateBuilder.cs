using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Costruisce e clusterizza candidati offset FrameSync da coppie di scene-cut
    /// </summary>
    public class FrameSyncOffsetCandidateBuilder
    {
        #region Metodi pubblici

        /// <summary>
        /// Seleziona i cluster di offset più promettenti dai voti ordinati
        /// </summary>
        public List<FrameSyncCandidate> SelectInitialCandidates(double[] sortedCandidates, int candidateCount, double frameIntervalMs, int maxInitialCandidates, int maxAbsOffsetMs)
        {
            List<FrameSyncCandidate> result = new List<FrameSyncCandidate>();
            int left = 0;
            int currentCount;
            double offset;
            bool duplicate;
            for (int r = 0; r < candidateCount; r++)
            {
                while (sortedCandidates[r] - sortedCandidates[left] > frameIntervalMs)
                {
                    left++;
                }

                currentCount = r - left + 1;
                offset = sortedCandidates[left + currentCount / 2];
                duplicate = false;

                for (int i = 0; i < result.Count; i++)
                {
                    if (Math.Abs(result[i].OffsetMs - offset) <= frameIntervalMs)
                    {
                        duplicate = true;
                        if (currentCount > result[i].VoteCount)
                        {
                            result[i].OffsetMs = (int)Math.Round(offset);
                            result[i].VoteCount = currentCount;
                            result[i].CombinedScore = currentCount;
                        }
                        break;
                    }
                }

                if (!duplicate)
                {
                    FrameSyncCandidate candidate = new FrameSyncCandidate();
                    candidate.OffsetMs = (int)Math.Round(offset);
                    candidate.Source = FrameSyncCandidate.SCENE_CUT_VOTING;
                    candidate.VoteCount = currentCount;
                    candidate.CombinedScore = currentCount;
                    result.Add(candidate);
                }
            }

            result.Sort((a, b) => b.VoteCount.CompareTo(a.VoteCount));

            if (result.Count > maxInitialCandidates)
            {
                List<FrameSyncCandidate> plausible = new List<FrameSyncCandidate>();
                for (int i = 0; i < result.Count; i++)
                {
                    if (Math.Abs(result[i].OffsetMs) <= maxAbsOffsetMs)
                    {
                        plausible.Add(result[i]);
                    }
                }

                if (plausible.Count >= maxInitialCandidates)
                {
                    result = plausible;
                    result.Sort((a, b) => b.VoteCount.CompareTo(a.VoteCount));
                }
            }

            if (result.Count > maxInitialCandidates)
            {
                result.RemoveRange(maxInitialCandidates, result.Count - maxInitialCandidates);
            }

            return result;
        }

        /// <summary>
        /// Genera candidati offset da tutte le coppie di cut dentro un range locale
        /// </summary>
        public List<FrameSyncCandidate> BuildOffsetCandidates(double[] sourceTimestampsMs, double[] langTimestampsMs, List<int> validSourceCuts, List<int> validLangCuts, double minOffsetMs, double maxOffsetMs, double frameIntervalMs, int maxInitialCandidates, int maxAbsOffsetMs)
        {
            List<FrameSyncCandidate> result = new List<FrameSyncCandidate>();
            int maxCandidateCount = validSourceCuts.Count * validLangCuts.Count;
            double[] candidates;
            int candidateCount = 0;
            double srcMs;
            double lngMs;
            double offsetMs;
            if (maxCandidateCount > 0)
            {
                candidates = new double[maxCandidateCount];

                for (int s = 0; s < validSourceCuts.Count; s++)
                {
                    srcMs = sourceTimestampsMs[validSourceCuts[s]];
                    for (int l = 0; l < validLangCuts.Count; l++)
                    {
                        lngMs = langTimestampsMs[validLangCuts[l]];
                        offsetMs = lngMs - srcMs;

                        if (offsetMs >= minOffsetMs && offsetMs <= maxOffsetMs)
                        {
                            candidates[candidateCount] = offsetMs;
                            candidateCount++;
                        }
                    }
                }

                if (candidateCount > 0)
                {
                    Array.Sort(candidates, 0, candidateCount);
                    result = this.SelectInitialCandidates(candidates, candidateCount, frameIntervalMs, maxInitialCandidates, maxAbsOffsetMs);
                }
            }

            return result;
        }

        #endregion
    }
}
