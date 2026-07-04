using RemuxForge.Core.Media.Ffmpeg;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Calcola descriptor, scoring e campionamento visual scan per FrameSync
    /// </summary>
    public class FrameSyncVisualScanMatcher
    {
        #region Variabili di classe

        private readonly VideoSyncConfig _videoSyncConfig;
        private readonly VisualMetricCalculator _visualMetricCalculator;
        private readonly FrameSyncCandidateScorer _candidateScorer;
        private readonly int _visualScanMaxSamples;
        private readonly int _visualScanFastTopCandidates;
        private readonly int _visualScanOffsetStepMs;
        private readonly int _checkpointLocalMaxSamples;
        private readonly int _checkpointLocalMinSamples;

        #endregion

        #region Costruttore

        public FrameSyncVisualScanMatcher(VideoSyncConfig videoSyncConfig, FrameSyncCandidateScorer candidateScorer, int visualScanMaxSamples, int visualScanFastTopCandidates, int visualScanOffsetStepMs, int checkpointLocalMaxSamples, int checkpointLocalMinSamples)
        {
            this._videoSyncConfig = videoSyncConfig;
            this._visualMetricCalculator = new VisualMetricCalculator(videoSyncConfig);
            this._candidateScorer = candidateScorer;
            this._visualScanMaxSamples = visualScanMaxSamples;
            this._visualScanFastTopCandidates = visualScanFastTopCandidates;
            this._visualScanOffsetStepMs = visualScanOffsetStepMs;
            this._checkpointLocalMaxSamples = checkpointLocalMaxSamples;
            this._checkpointLocalMinSamples = checkpointLocalMinSamples;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Sceglie campioni con movimento locale alto, distanziati temporalmente
        /// </summary>
        public List<int> SelectVisualScanSamples(List<byte[]> frames, double[] timestampsMs)
        {
            List<VisualScanMotionSample> motion = new List<VisualScanMotionSample>();
            List<int> result = new List<int>();

            for (int i = 1; i < frames.Count; i++)
            {
                VisualScanMotionSample sample = new VisualScanMotionSample();
                sample.Index = i;
                sample.TimestampMs = timestampsMs[i];
                sample.Motion = this._visualMetricCalculator.ComputeMse(frames[i - 1], frames[i]);
                motion.Add(sample);
            }

            motion.Sort((a, b) => b.Motion.CompareTo(a.Motion));

            for (int i = 0; i < motion.Count && result.Count < this._visualScanMaxSamples; i++)
            {
                bool tooClose = false;
                for (int r = 0; r < result.Count; r++)
                {
                    if (Math.Abs(timestampsMs[result[r]] - motion[i].TimestampMs) < 2000.0)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose && motion[i].Motion > 4.0)
                {
                    result.Add(motion[i].Index);
                }
            }

            result.Sort();

            return result;
        }

        /// <summary>
        /// Calcola lo score medio di un offset visuale sui campioni scelti con descriptor precomputati
        /// </summary>
        public VisualScanCandidate ScoreVisualScanOffset(VisualScanFrameDescriptor[] sourceDescriptors, VisualScanFrameDescriptor[] langDescriptors, double[] sourceTimestampsMs, double[] langTimestampsMs, List<int> sampleIndices, int offsetMs, double maxTimestampDistanceMs)
        {
            return this.ScoreVisualScanOffset(sourceDescriptors, langDescriptors, sourceTimestampsMs, langTimestampsMs, sampleIndices, offsetMs, maxTimestampDistanceMs, true);
        }

        /// <summary>
        /// Calcola lo score medio di un offset visuale sui campioni scelti con descriptor precomputati
        /// </summary>
        public VisualScanCandidate ScoreVisualScanOffset(VisualScanFrameDescriptor[] sourceDescriptors, VisualScanFrameDescriptor[] langDescriptors, double[] sourceTimestampsMs, double[] langTimestampsMs, List<int> sampleIndices, int offsetMs, double maxTimestampDistanceMs, bool includeSsim)
        {
            VisualScanCandidate result = new VisualScanCandidate();
            double totalScore = 0.0;
            double totalBlur = 0.0;
            double totalEdge = 0.0;
            double totalBlock = 0.0;
            double totalMotion = 0.0;
            double totalHash = 0.0;
            int totalVotes = 0;
            result.OffsetMs = offsetMs;

            for (int i = 0; i < sampleIndices.Count; i++)
            {
                int srcIndex = sampleIndices[i];
                int langIndex = this.FindNearestTimestampIndex(langTimestampsMs, sourceTimestampsMs[srcIndex] + offsetMs, maxTimestampDistanceMs);

                if (langIndex >= 0)
                {
                    VisualScanFrameDescriptor sourceDescriptor = sourceDescriptors[srcIndex];
                    VisualScanFrameDescriptor langDescriptor = langDescriptors[langIndex];
                    double block = this.ComputeUnsignedCorrelation(sourceDescriptor.Blocks, langDescriptor.Blocks);
                    double edgeBlock = this.ComputeUnsignedCorrelation(sourceDescriptor.EdgeBlocks, langDescriptor.EdgeBlocks);
                    block = (block * 0.65) + (edgeBlock * 0.35);
                    double motion = 0.0;
                    if (srcIndex > 0 && langIndex > 0)
                    {
                        motion = this.ComputeSignedCorrelation(sourceDescriptor.MotionBlocks, langDescriptor.MotionBlocks);
                    }
                    double hash = (this._visualMetricCalculator.ComputeHashSimilarity(sourceDescriptor.AverageHash, langDescriptor.AverageHash) * 0.45) + (this._visualMetricCalculator.ComputeHashSimilarity(sourceDescriptor.DifferenceHash, langDescriptor.DifferenceHash) * 0.55);
                    double blur = block;
                    double edge = edgeBlock;
                    double ssim = block;
                    double temporalContext = this.ComputeTemporalContextScore(sourceDescriptors, langDescriptors, sourceTimestampsMs, langTimestampsMs, srcIndex, langIndex);
                    if (includeSsim)
                    {
                        ssim = this.ComputeSsim(sourceDescriptor, langDescriptor);
                        blur = this.ComputeUnsignedCorrelation(sourceDescriptor.BlurSamples, langDescriptor.BlurSamples);
                        edge = this.ComputeUnsignedCorrelation(sourceDescriptor.EdgeSamples, langDescriptor.EdgeSamples);
                    }
                    double score = this._candidateScorer.ComputeVisualCandidateScore(ssim, blur, edge, block, motion, hash);
                    score = (score * 0.82) + (temporalContext * 0.18);

                    totalScore += score;
                    totalBlur += blur;
                    totalEdge += edge;
                    totalBlock += block;
                    totalMotion += motion;
                    totalHash += hash;
                    totalVotes += this._candidateScorer.CountDescriptorVotes(ssim, blur, edge, block, motion, hash);
                    result.SampleCount++;
                }
            }

            if (result.SampleCount > 0)
            {
                result.Score = totalScore / result.SampleCount;
                result.BlurScore = totalBlur / result.SampleCount;
                result.EdgeScore = totalEdge / result.SampleCount;
                result.BlockScore = totalBlock / result.SampleCount;
                result.MotionScore = totalMotion / result.SampleCount;
                result.HashScore = totalHash / result.SampleCount;
                result.DescriptorVotes = (int)Math.Round(totalVotes / (double)result.SampleCount);
            }

            return result;
        }

        /// <summary>
        /// Score temporale VFR-aware: confronta durata locale PTS e hold di frame quasi identici
        /// Serve a separare pose anime uguali ma tenute per durate diverse
        /// </summary>
        private double ComputeTemporalContextScore(VisualScanFrameDescriptor[] sourceDescriptors, VisualScanFrameDescriptor[] langDescriptors, double[] sourceTimestampsMs, double[] langTimestampsMs, int sourceIndex, int langIndex)
        {
            double sourceHoldMs = this.ComputeHoldDurationMs(sourceDescriptors, sourceTimestampsMs, sourceIndex);
            double langHoldMs = this.ComputeHoldDurationMs(langDescriptors, langTimestampsMs, langIndex);
            double holdScore = this.ComputeDurationSimilarity(sourceHoldMs, langHoldMs, 2000.0);
            double sourceStepMs = this.ComputeLocalDurationMs(sourceTimestampsMs, sourceIndex);
            double langStepMs = this.ComputeLocalDurationMs(langTimestampsMs, langIndex);
            double stepScore = this.ComputeDurationSimilarity(sourceStepMs, langStepMs, 500.0);
            double sourceMotionMs = this.ComputeMotionSpanMs(sourceDescriptors, sourceTimestampsMs, sourceIndex);
            double langMotionMs = this.ComputeMotionSpanMs(langDescriptors, langTimestampsMs, langIndex);
            double motionSpanScore = this.ComputeDurationSimilarity(sourceMotionMs, langMotionMs, 1200.0);

            return (holdScore * 0.50) + (stepScore * 0.25) + (motionSpanScore * 0.25);
        }

        /// <summary>
        /// Durata PTS locale attorno a un indice
        /// </summary>
        private double ComputeLocalDurationMs(double[] timestampsMs, int index)
        {
            double before = 0.0;
            double after = 0.0;
            if (timestampsMs == null || timestampsMs.Length == 0)
            {
                return 0.0;
            }

            if (index > 0)
            {
                before = timestampsMs[index] - timestampsMs[index - 1];
            }
            if (index + 1 < timestampsMs.Length)
            {
                after = timestampsMs[index + 1] - timestampsMs[index];
            }

            if (before < 0.0)
            {
                before = 0.0;
            }
            if (after < 0.0)
            {
                after = 0.0;
            }

            return before + after;
        }

        /// <summary>
        /// Stima per quanto tempo resta lo stesso disegno attorno al frame
        /// </summary>
        private double ComputeHoldDurationMs(VisualScanFrameDescriptor[] descriptors, double[] timestampsMs, int index)
        {
            int left = index;
            int right = index;
            double result = 0.0;
            if (descriptors == null || timestampsMs == null || index < 0 || index >= descriptors.Length || index >= timestampsMs.Length)
            {
                return result;
            }

            while (left > 0 && this.AreHoldEquivalent(descriptors[index], descriptors[left - 1]))
            {
                left--;
            }
            while (right + 1 < descriptors.Length && right + 1 < timestampsMs.Length && this.AreHoldEquivalent(descriptors[index], descriptors[right + 1]))
            {
                right++;
            }

            if (right > left)
            {
                result = timestampsMs[right] - timestampsMs[left];
            }
            else
            {
                result = this.ComputeLocalDurationMs(timestampsMs, index);
            }

            if (result < 0.0)
            {
                result = 0.0;
            }
            if (result > 2000.0)
            {
                result = 2000.0;
            }

            return result;
        }

        /// <summary>
        /// Durata della zona temporalmente poco mossa attorno al frame
        /// </summary>
        private double ComputeMotionSpanMs(VisualScanFrameDescriptor[] descriptors, double[] timestampsMs, int index)
        {
            int left = index;
            int right = index;
            double motionThreshold = 0.985;

            if (descriptors == null || timestampsMs == null || index < 0 || index >= descriptors.Length || index >= timestampsMs.Length)
            {
                return 0.0;
            }

            while (left > 0 && this.ComputeUnsignedCorrelation(descriptors[left].Blocks, descriptors[left - 1].Blocks) >= motionThreshold)
            {
                left--;
            }
            while (right + 1 < descriptors.Length && right + 1 < timestampsMs.Length && this.ComputeUnsignedCorrelation(descriptors[right].Blocks, descriptors[right + 1].Blocks) >= motionThreshold)
            {
                right++;
            }

            if (right <= left)
            {
                return this.ComputeLocalDurationMs(timestampsMs, index);
            }

            return timestampsMs[right] - timestampsMs[left];
        }

        /// <summary>
        /// Similarità durata normalizzata e robusta a VFR estremi
        /// </summary>
        private double ComputeDurationSimilarity(double firstMs, double secondMs, double capMs)
        {
            double diff = Math.Abs(firstMs - secondMs);
            double scale = firstMs > secondMs ? firstMs : secondMs;

            if (scale < 1.0)
            {
                return 1.0;
            }
            if (scale > capMs)
            {
                scale = capMs;
            }

            return 1.0 - Math.Min(1.0, diff / scale);
        }

        /// <summary>
        /// Equivalenza hold basata su hash percettivi e blocchi luma
        /// </summary>
        private bool AreHoldEquivalent(VisualScanFrameDescriptor first, VisualScanFrameDescriptor second)
        {
            double avgHash = this._visualMetricCalculator.ComputeHashSimilarity(first.AverageHash, second.AverageHash);
            double diffHash = this._visualMetricCalculator.ComputeHashSimilarity(first.DifferenceHash, second.DifferenceHash);
            double blocks = this.ComputeUnsignedCorrelation(first.Blocks, second.Blocks);

            return avgHash >= 0.985 && diffHash >= 0.970 && blocks >= 0.985;
        }

        /// <summary>
        /// Mantiene una lista corta dei migliori offset del ranking veloce
        /// </summary>
        public void AddTopVisualScanCandidate(List<VisualScanCandidate> candidates, VisualScanCandidate candidate, int maxCount)
        {
            if (candidate == null || candidate.SampleCount == 0)
            {
                return;
            }

            candidates.Add(candidate);
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

            while (candidates.Count > maxCount)
            {
                candidates.RemoveAt(candidates.Count - 1);
            }
        }

        /// <summary>
        /// Crea gli offset da rivalutare con score completo partendo dai migliori candidati veloci
        /// </summary>
        public List<int> BuildExactVisualScanOffsets(List<VisualScanCandidate> fastCandidates, int minOffsetMs, int maxOffsetMs)
        {
            List<int> result = new List<int>();

            fastCandidates.Sort((a, b) => b.Score.CompareTo(a.Score));

            for (int i = 0; i < fastCandidates.Count && i < this._visualScanFastTopCandidates; i++)
            {
                this.AddUniqueOffset(result, fastCandidates[i].OffsetMs - this._visualScanOffsetStepMs, minOffsetMs, maxOffsetMs);
                this.AddUniqueOffset(result, fastCandidates[i].OffsetMs, minOffsetMs, maxOffsetMs);
                this.AddUniqueOffset(result, fastCandidates[i].OffsetMs + this._visualScanOffsetStepMs, minOffsetMs, maxOffsetMs);
            }

            return result;
        }

        /// <summary>
        /// Aggiunge un offset se nel range richiesto e non già presente
        /// </summary>
        public void AddUniqueOffset(List<int> offsets, int offsetMs, int minOffsetMs, int maxOffsetMs)
        {
            if (offsetMs < minOffsetMs || offsetMs > maxOffsetMs)
            {
                return;
            }

            for (int i = 0; i < offsets.Count; i++)
            {
                if (offsets[i] == offsetMs)
                {
                    return;
                }
            }

            offsets.Add(offsetMs);
        }

        /// <summary>
        /// Precalcola descriptor visuali per tutti i frame estratti
        /// </summary>
        public VisualScanFrameDescriptor[] BuildVisualScanDescriptors(List<byte[]> frames)
        {
            VisualScanFrameDescriptor[] result = new VisualScanFrameDescriptor[frames.Count];

            for (int i = 0; i < frames.Count; i++)
            {
                byte[] previousFrame = null;
                if (i > 0)
                {
                    previousFrame = frames[i - 1];
                }

                result[i] = this.BuildVisualScanDescriptor(frames[i], previousFrame);
            }

            return result;
        }

        /// <summary>
        /// Precalcola i segnali usati dallo scoring visuale
        /// </summary>
        public VisualScanFrameDescriptor BuildVisualScanDescriptor(byte[] frame, byte[] previousFrame)
        {
            VisualScanFrameDescriptor result = new VisualScanFrameDescriptor();

            result.Frame = frame;
            result.BlurSamples = this.ComputeBlurSamples(frame);
            result.EdgeSamples = this.ComputeEdgeSamples(frame);
            result.Blocks = this.ComputeBlockMeans(frame);
            result.EdgeBlocks = this.ComputeEdgeBlockMeans(frame);
            result.MotionBlocks = this.ComputeMotionBlockMeans(previousFrame, frame);
            result.AverageHash = this._visualMetricCalculator.ComputeAverageHash(frame);
            result.DifferenceHash = this._visualMetricCalculator.ComputeDifferenceHash(frame);
            this.ComputeFrameStatistics(frame, out double mean, out double variance);
            result.Mean = mean;
            result.Variance = variance;

            return result;
        }

        /// <summary>
        /// Calcola media e varianza luma una sola volta per frame
        /// </summary>
        public void ComputeFrameStatistics(byte[] frame, out double mean, out double variance)
        {
            double sum = 0.0;
            double diff;
            int length = frame.Length;

            mean = 0.0;
            variance = 0.0;

            if (length <= 0)
            {
                return;
            }

            for (int i = 0; i < length; i++)
            {
                sum += frame[i];
            }

            mean = sum / length;

            for (int i = 0; i < length; i++)
            {
                diff = frame[i] - mean;
                variance += diff * diff;
            }

            variance /= length;
        }

        /// <summary>
        /// SSIM con media e varianza già precomputate nel descriptor
        /// </summary>
        public double ComputeSsim(VisualScanFrameDescriptor descriptor1, VisualScanFrameDescriptor descriptor2)
        {
            byte[] frame1 = descriptor1.Frame;
            byte[] frame2 = descriptor2.Frame;
            int length = frame1.Length;
            double covariance = 0.0;
            double diff1;
            double diff2;
            double c1 = 6.5025;
            double c2 = 58.5225;
            double numerator;
            double denominator;
            if (frame2.Length < length)
            {
                length = frame2.Length;
            }

            if (length <= 0)
            {
                return 0.0;
            }

            for (int i = 0; i < length; i++)
            {
                diff1 = frame1[i] - descriptor1.Mean;
                diff2 = frame2[i] - descriptor2.Mean;
                covariance += diff1 * diff2;
            }

            covariance /= length;

            numerator = (2.0 * descriptor1.Mean * descriptor2.Mean + c1) * (2.0 * covariance + c2);
            denominator = (descriptor1.Mean * descriptor1.Mean + descriptor2.Mean * descriptor2.Mean + c1) * (descriptor1.Variance + descriptor2.Variance + c2);

            if (denominator <= 0.0)
            {
                return 0.0;
            }

            return numerator / denominator;
        }

        /// <summary>
        /// Campioni blur 3x3 precomputati, equivalenti al percorso on-the-fly precedente
        /// </summary>
        public ushort[] ComputeBlurSamples(byte[] frame)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            int sampleWidth = (width - 2 + 1) / 2;
            int sampleHeight = (height - 2 + 1) / 2;
            ushort[] result = new ushort[sampleWidth * sampleHeight];
            int outIndex = 0;
            int index;
            int sum;
            for (int y = 1; y < height - 1; y += 2)
            {
                for (int x = 1; x < width - 1; x += 2)
                {
                    index = y * width + x;
                    sum = 0;
                    sum += frame[index - width - 1];
                    sum += frame[index - width];
                    sum += frame[index - width + 1];
                    sum += frame[index - 1];
                    sum += frame[index];
                    sum += frame[index + 1];
                    sum += frame[index + width - 1];
                    sum += frame[index + width];
                    sum += frame[index + width + 1];
                    result[outIndex] = (ushort)(sum / 9);
                    outIndex++;
                }
            }

            return result;
        }

        /// <summary>
        /// Campioni edge Sobel precomputati
        /// </summary>
        public ushort[] ComputeEdgeSamples(byte[] frame)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            int sampleWidth = (width - 2 + 1) / 2;
            int sampleHeight = (height - 2 + 1) / 2;
            ushort[] result = new ushort[sampleWidth * sampleHeight];
            int outIndex = 0;
            int index;
            int top;
            int mid;
            int bottom;
            int gx;
            int gy;
            int edge;
            for (int y = 1; y < height - 1; y += 2)
            {
                for (int x = 1; x < width - 1; x += 2)
                {
                    index = y * width + x;
                    top = index - width;
                    mid = index;
                    bottom = index + width;
                    gx = -frame[top - 1] + frame[top + 1] - (2 * frame[mid - 1]) + (2 * frame[mid + 1]) - frame[bottom - 1] + frame[bottom + 1];
                    gy = -frame[top - 1] - (2 * frame[top]) - frame[top + 1] + frame[bottom - 1] + (2 * frame[bottom]) + frame[bottom + 1];
                    edge = Math.Abs(gx) + Math.Abs(gy);
                    if (edge > ushort.MaxValue)
                    {
                        edge = ushort.MaxValue;
                    }
                    result[outIndex] = (ushort)edge;
                    outIndex++;
                }
            }

            return result;
        }

        /// <summary>
        /// Medie luma a blocchi precomputate
        /// </summary>
        public ushort[] ComputeBlockMeans(byte[] frame)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            int blocksX = 16;
            int blocksY = 12;
            ushort[] result = new ushort[blocksX * blocksY];
            int x0;
            int x1;
            int y0;
            int y1;
            int idx;
            int pixelCount;
            int row;
            long blockSum;
            for (int by = 0; by < blocksY; by++)
            {
                y0 = (by * height) / blocksY;
                y1 = ((by + 1) * height) / blocksY;

                for (int bx = 0; bx < blocksX; bx++)
                {
                    x0 = (bx * width) / blocksX;
                    x1 = ((bx + 1) * width) / blocksX;
                    idx = (by * blocksX) + bx;
                    pixelCount = (x1 - x0) * (y1 - y0);
                    blockSum = 0;

                    for (int y = y0; y < y1; y++)
                    {
                        row = y * width;
                        for (int x = x0; x < x1; x++)
                        {
                            blockSum += frame[row + x];
                        }
                    }

                    if (pixelCount > 0)
                    {
                        result[idx] = (ushort)(blockSum / pixelCount);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Medie edge a blocchi precomputate
        /// </summary>
        public ushort[] ComputeEdgeBlockMeans(byte[] frame)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            int blocksX = 16;
            int blocksY = 12;
            ushort[] result = new ushort[blocksX * blocksY];
            int x0;
            int x1;
            int y0;
            int y1;
            int idx;
            int pixelCount;
            int index;
            int top;
            int mid;
            int bottom;
            int gx;
            int gy;
            long blockSum;
            for (int by = 0; by < blocksY; by++)
            {
                y0 = (by * height) / blocksY;
                y1 = ((by + 1) * height) / blocksY;
                if (y0 < 1) { y0 = 1; }
                if (y1 > height - 1) { y1 = height - 1; }

                for (int bx = 0; bx < blocksX; bx++)
                {
                    x0 = (bx * width) / blocksX;
                    x1 = ((bx + 1) * width) / blocksX;
                    if (x0 < 1) { x0 = 1; }
                    if (x1 > width - 1) { x1 = width - 1; }

                    idx = (by * blocksX) + bx;
                    blockSum = 0;
                    pixelCount = 0;

                    for (int y = y0; y < y1; y += 2)
                    {
                        for (int x = x0; x < x1; x += 2)
                        {
                            index = y * width + x;
                            top = index - width;
                            mid = index;
                            bottom = index + width;
                            gx = -frame[top - 1] + frame[top + 1] - (2 * frame[mid - 1]) + (2 * frame[mid + 1]) - frame[bottom - 1] + frame[bottom + 1];
                            gy = -frame[top - 1] - (2 * frame[top]) - frame[top + 1] + frame[bottom - 1] + (2 * frame[bottom]) + frame[bottom + 1];
                            blockSum += Math.Abs(gx) + Math.Abs(gy);
                            pixelCount++;
                        }
                    }

                    if (pixelCount > 0)
                    {
                        result[idx] = (ushort)(blockSum / pixelCount);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Medie differenza inter-frame a blocchi precomputate
        /// </summary>
        public short[] ComputeMotionBlockMeans(byte[] previousFrame, byte[] frame)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            int blocksX = 16;
            int blocksY = 12;
            short[] result = new short[blocksX * blocksY];
            int x0;
            int x1;
            int y0;
            int y1;
            int idx;
            int pixelCount;
            int row;
            int pos;
            long blockDelta;
            if (previousFrame == null)
            {
                return result;
            }

            for (int by = 0; by < blocksY; by++)
            {
                y0 = (by * height) / blocksY;
                y1 = ((by + 1) * height) / blocksY;

                for (int bx = 0; bx < blocksX; bx++)
                {
                    x0 = (bx * width) / blocksX;
                    x1 = ((bx + 1) * width) / blocksX;
                    idx = (by * blocksX) + bx;
                    pixelCount = (x1 - x0) * (y1 - y0);
                    blockDelta = 0;

                    for (int y = y0; y < y1; y++)
                    {
                        row = y * width;
                        for (int x = x0; x < x1; x++)
                        {
                            pos = row + x;
                            blockDelta += frame[pos] - previousFrame[pos];
                        }
                    }

                    if (pixelCount > 0)
                    {
                        result[idx] = (short)(blockDelta / pixelCount);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Correlazione normalizzata per vettori unsigned precomputati
        /// </summary>
        public double ComputeUnsignedCorrelation(ushort[] values1, ushort[] values2)
        {
            double result = 0.0;
            double sum1 = 0.0;
            double sum2 = 0.0;
            double mean1;
            double mean2;
            double diff1;
            double diff2;
            double numerator = 0.0;
            double den1 = 0.0;
            double den2 = 0.0;
            double denominator;
            int count = values1.Length;

            if (values2.Length < count)
            {
                count = values2.Length;
            }

            if (count <= 0)
            {
                return result;
            }

            for (int i = 0; i < count; i++)
            {
                sum1 += values1[i];
                sum2 += values2[i];
            }

            mean1 = sum1 / count;
            mean2 = sum2 / count;

            for (int i = 0; i < count; i++)
            {
                diff1 = values1[i] - mean1;
                diff2 = values2[i] - mean2;
                numerator += diff1 * diff2;
                den1 += diff1 * diff1;
                den2 += diff2 * diff2;
            }

            denominator = Math.Sqrt(den1 * den2);
            if (denominator > 0.0)
            {
                result = numerator / denominator;
                if (result < 0.0) { result = 0.0; }
                if (result > 1.0) { result = 1.0; }
            }

            return result;
        }

        /// <summary>
        /// Correlazione normalizzata per vettori signed precomputati
        /// </summary>
        public double ComputeSignedCorrelation(short[] values1, short[] values2)
        {
            double result = 0.0;
            double sum1 = 0.0;
            double sum2 = 0.0;
            double mean1;
            double mean2;
            double diff1;
            double diff2;
            double numerator = 0.0;
            double den1 = 0.0;
            double den2 = 0.0;
            double denominator;
            int count = values1.Length;

            if (values2.Length < count)
            {
                count = values2.Length;
            }

            if (count <= 0)
            {
                return result;
            }

            for (int i = 0; i < count; i++)
            {
                sum1 += values1[i];
                sum2 += values2[i];
            }

            mean1 = sum1 / count;
            mean2 = sum2 / count;

            for (int i = 0; i < count; i++)
            {
                diff1 = values1[i] - mean1;
                diff2 = values2[i] - mean2;
                numerator += diff1 * diff2;
                den1 += diff1 * diff1;
                den2 += diff2 * diff2;
            }

            denominator = Math.Sqrt(den1 * den2);
            if (denominator > 0.0)
            {
                result = numerator / denominator;
                if (result < 0.0) { result = 0.0; }
                if (result > 1.0) { result = 1.0; }
            }

            return result;
        }

        /// <summary>
        /// Aggiorna best/second mantenendo candidati separati almeno due step
        /// </summary>
        public void TrackVisualScanCandidate(VisualScanCandidate candidate, ref VisualScanCandidate best, ref VisualScanCandidate second)
        {
            if (candidate == null || candidate.SampleCount == 0)
            {
                return;
            }

            if (candidate.Score > best.Score)
            {
                if (Math.Abs(candidate.OffsetMs - best.OffsetMs) > this._visualScanOffsetStepMs * 2)
                {
                    second = best;
                }
                best = candidate;
            }
            else if (Math.Abs(candidate.OffsetMs - best.OffsetMs) > this._visualScanOffsetStepMs * 2 && candidate.Score > second.Score)
            {
                second = candidate;
            }
        }

        /// <summary>
        /// Trova l'indice del timestamp più vicino con distanza massima esplicita
        /// </summary>
        public int FindNearestTimestampIndex(double[] timestampsMs, double expectedMs, double maxDistanceMs)
        {
            int result = -1;
            int low = 0;
            int high = timestampsMs.Length - 1;
            int mid;
            double bestDistance = double.MaxValue;
            while (low <= high)
            {
                mid = low + ((high - low) / 2);
                if (timestampsMs[mid] < expectedMs)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            for (int i = low - 1; i <= low + 1; i++)
            {
                if (i >= 0 && i < timestampsMs.Length)
                {
                    double distance = Math.Abs(timestampsMs[i] - expectedMs);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        result = i;
                    }
                }
            }

            if (bestDistance > maxDistanceMs)
            {
                result = -1;
            }

            return result;
        }

        /// <summary>
        /// Seleziona campioni informativi nel segmento checkpoint
        /// </summary>
        public List<int> SelectCheckpointLocalSearchSamples(List<byte[]> frames, double[] timestampsMs)
        {
            List<VisualScanMotionSample> motion = new List<VisualScanMotionSample>();
            List<int> result = new List<int>();
            double previousMotion;
            double nextMotion;
            double localMotion;
            double variance;
            for (int i = 2; i < frames.Count - 2; i++)
            {
                previousMotion = this._visualMetricCalculator.ComputeMse(frames[i - 1], frames[i]);
                nextMotion = this._visualMetricCalculator.ComputeMse(frames[i], frames[i + 1]);
                localMotion = previousMotion > nextMotion ? previousMotion : nextMotion;
                variance = this.ComputeFrameVariance(frames[i]);

                if (variance >= 18.0 && localMotion <= 90.0)
                {
                    VisualScanMotionSample sample = new VisualScanMotionSample();
                    sample.Index = i;
                    sample.TimestampMs = timestampsMs[i];
                    sample.Motion = variance;
                    motion.Add(sample);
                }
            }

            motion.Sort((a, b) => b.Motion.CompareTo(a.Motion));

            for (int i = 0; i < motion.Count && result.Count < this._checkpointLocalMaxSamples; i++)
            {
                bool tooClose = false;
                for (int r = 0; r < result.Count; r++)
                {
                    if (Math.Abs(timestampsMs[result[r]] - motion[i].TimestampMs) < 500.0)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose && motion[i].Motion > 2.0)
                {
                    result.Add(motion[i].Index);
                }
            }

            if (result.Count < this._checkpointLocalMinSamples)
            {
                motion.Clear();
                for (int i = 1; i < frames.Count - 1; i++)
                {
                    VisualScanMotionSample sample = new VisualScanMotionSample();
                    sample.Index = i;
                    sample.TimestampMs = timestampsMs[i];
                    sample.Motion = this._visualMetricCalculator.ComputeMse(frames[i - 1], frames[i]);
                    motion.Add(sample);
                }

                motion.Sort((a, b) => b.Motion.CompareTo(a.Motion));

                for (int i = 0; i < motion.Count && result.Count < this._checkpointLocalMaxSamples; i++)
                {
                    bool tooClose = false;
                    for (int r = 0; r < result.Count; r++)
                    {
                        if (Math.Abs(timestampsMs[result[r]] - motion[i].TimestampMs) < 500.0)
                        {
                            tooClose = true;
                            break;
                        }
                    }

                    if (!tooClose && motion[i].Motion > 2.0)
                    {
                        result.Add(motion[i].Index);
                    }
                }
            }

            if (result.Count < this._checkpointLocalMinSamples)
            {
                int step = frames.Count / (this._checkpointLocalMaxSamples + 1);
                if (step < 1)
                {
                    step = 1;
                }

                for (int i = step; i < frames.Count - 1 && result.Count < this._checkpointLocalMaxSamples; i += step)
                {
                    bool exists = false;
                    for (int r = 0; r < result.Count; r++)
                    {
                        if (result[r] == i)
                        {
                            exists = true;
                            break;
                        }
                    }

                    if (!exists && this.ComputeFrameVariance(frames[i]) >= 18.0)
                    {
                        result.Add(i);
                    }
                }
            }

            result.Sort();

            return result;
        }

        /// <summary>
        /// Calcola varianza luma leggera per scartare frame neri/statici
        /// </summary>
        public double ComputeFrameVariance(byte[] frame)
        {
            double mean = 0.0;
            double variance = 0.0;
            double diff;
            int step = 4;
            int count = 0;
            for (int i = 0; i < frame.Length; i += step)
            {
                mean += frame[i];
                count++;
            }

            if (count > 0)
            {
                mean /= count;
                for (int i = 0; i < frame.Length; i += step)
                {
                    diff = frame[i] - mean;
                    variance += diff * diff;
                }
                variance /= count;
            }

            return variance;
        }

        /// <summary>
        /// Aggiorna best/second per la ricerca locale checkpoint
        /// </summary>
        public void TrackCheckpointLocalCandidate(VisualScanCandidate candidate, ref VisualScanCandidate best, ref VisualScanCandidate second, int frameIntervalMs)
        {
            if (candidate == null || candidate.SampleCount == 0)
            {
                return;
            }

            if (candidate.Score > best.Score)
            {
                if (Math.Abs(candidate.OffsetMs - best.OffsetMs) > frameIntervalMs * 2)
                {
                    second = best;
                }
                best = candidate;
            }
            else if (Math.Abs(candidate.OffsetMs - best.OffsetMs) > frameIntervalMs * 2 && candidate.Score > second.Score)
            {
                second = candidate;
            }
        }

        #endregion
    }
}
