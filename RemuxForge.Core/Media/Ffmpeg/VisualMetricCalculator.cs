using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace RemuxForge.Core.Media.Ffmpeg
{
    /// <summary>
    /// Calcola metriche visuali low-level su frame grayscale normalizzati
    /// </summary>
    public class VisualMetricCalculator
    {
        #region Variabili di classe

        /// <summary>
        /// Configurazione VideoSync
        /// </summary>
        private VideoSyncConfig _videoSyncConfig;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public VisualMetricCalculator(VideoSyncConfig videoSyncConfig)
        {
            this._videoSyncConfig = videoSyncConfig;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Calcola MSE tra due frame grayscale
        /// </summary>
        public double ComputeMse(byte[] frame1, byte[] frame2)
        {
            double sumSquaredDiff = 0.0;
            int length = frame1.Length;
            double diff;
            for (int i = 0; i < length; i++)
            {
                diff = frame1[i] - (double)frame2[i];
                sumSquaredDiff += diff * diff;
            }

            return sumSquaredDiff / length;
        }

        /// <summary>
        /// Calcola SSIM tra due frame grayscale
        /// </summary>
        public double ComputeSsim(byte[] frame1, byte[] frame2)
        {
            int length = frame1.Length;
            double mean1 = 0.0;
            double mean2 = 0.0;
            double variance1 = 0.0;
            double variance2 = 0.0;
            double covariance = 0.0;
            double diff1;
            double diff2;
            double c1 = 6.5025;
            double c2 = 58.5225;
            double numerator;
            double denominator;
            for (int i = 0; i < length; i++)
            {
                mean1 += frame1[i];
                mean2 += frame2[i];
            }
            mean1 /= length;
            mean2 /= length;

            for (int i = 0; i < length; i++)
            {
                diff1 = frame1[i] - mean1;
                diff2 = frame2[i] - mean2;
                variance1 += diff1 * diff1;
                variance2 += diff2 * diff2;
                covariance += diff1 * diff2;
            }
            variance1 /= length;
            variance2 /= length;
            covariance /= length;

            numerator = (2.0 * mean1 * mean2 + c1) * (2.0 * covariance + c2);
            denominator = (mean1 * mean1 + mean2 * mean2 + c1) * (variance1 + variance2 + c2);

            return numerator / denominator;
        }

        /// <summary>
        /// Calcola SSIM medio di una sequenza di frame consecutivi
        /// </summary>
        public double ComputeSequenceSsim(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            double totalSsim = 0.0;
            int validFrames = 0;
            double result = 0.0;
            int srcIdx;
            int lngIdx;
            for (int k = 0; k < sequenceLength; k++)
            {
                srcIdx = sourceStartIdx + k;
                lngIdx = langStartIdx + k;

                if (srcIdx >= sourceFrames.Count || lngIdx >= langFrames.Count)
                {
                    break;
                }

                totalSsim += this.ComputeSsim(sourceFrames[srcIdx], langFrames[lngIdx]);
                validFrames++;
            }

            if (validFrames >= sequenceLength)
            {
                result = totalSsim / validFrames;
            }

            return result;
        }

        /// <summary>
        /// Calcola correlazione su luma blur 3x3 campionata
        /// </summary>
        public double ComputeBlurredCorrelation(byte[] frame1, byte[] frame2)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
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
            double value1;
            double value2;
            double result = 0.0;
            int count = 0;
            for (int y = 1; y < height - 1; y += 2)
            {
                for (int x = 1; x < width - 1; x += 2)
                {
                    value1 = this.GetBlurredPixel(frame1, width, x, y);
                    value2 = this.GetBlurredPixel(frame2, width, x, y);
                    sum1 += value1;
                    sum2 += value2;
                    count++;
                }
            }

            if (count > 0)
            {
                mean1 = sum1 / count;
                mean2 = sum2 / count;

                for (int y = 1; y < height - 1; y += 2)
                {
                    for (int x = 1; x < width - 1; x += 2)
                    {
                        value1 = this.GetBlurredPixel(frame1, width, x, y);
                        value2 = this.GetBlurredPixel(frame2, width, x, y);
                        diff1 = value1 - mean1;
                        diff2 = value2 - mean2;
                        numerator += diff1 * diff2;
                        den1 += diff1 * diff1;
                        den2 += diff2 * diff2;
                    }
                }

                denominator = System.Math.Sqrt(den1 * den2);
                if (denominator > 0.0)
                {
                    result = numerator / denominator;
                    if (result < 0.0) { result = 0.0; }
                    if (result > 1.0) { result = 1.0; }
                }
            }

            return result;
        }

        /// <summary>
        /// Calcola correlazione blur media su una sequenza
        /// </summary>
        public double ComputeSequenceBlurredCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            double totalCorrelation = 0.0;
            int validFrames = 0;
            double result = 0.0;
            int srcIdx;
            int lngIdx;
            for (int k = 0; k < sequenceLength; k++)
            {
                srcIdx = sourceStartIdx + k;
                lngIdx = langStartIdx + k;

                if (srcIdx >= sourceFrames.Count || lngIdx >= langFrames.Count)
                {
                    break;
                }

                totalCorrelation += this.ComputeBlurredCorrelation(sourceFrames[srcIdx], langFrames[lngIdx]);
                validFrames++;
            }

            if (validFrames >= sequenceLength)
            {
                result = totalCorrelation / validFrames;
            }

            return result;
        }

        /// <summary>
        /// Calcola correlazione tra edge map Sobel ricavate al volo da due frame
        /// Usa campionamento 2x per ridurre costo CPU mantenendo robustezza su contorni e geometria
        /// </summary>
        /// <param name="frame1">Primo frame grayscale</param>
        /// <param name="frame2">Secondo frame grayscale</param>
        /// <returns>Correlazione normalizzata 0..1</returns>
        public double ComputeEdgeCorrelation(byte[] frame1, byte[] frame2)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            int index;
            int top;
            int mid;
            int bottom;
            int gx1;
            int gy1;
            int gx2;
            int gy2;
            double edge1;
            double edge2;
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
            int count = 0;
            double result = 0.0;
            for (int y = 1; y < height - 1; y += 2)
            {
                for (int x = 1; x < width - 1; x += 2)
                {
                    index = y * width + x;
                    top = index - width;
                    mid = index;
                    bottom = index + width;

                    gx1 = -frame1[top - 1] + frame1[top + 1] - (2 * frame1[mid - 1]) + (2 * frame1[mid + 1]) - frame1[bottom - 1] + frame1[bottom + 1];
                    gy1 = -frame1[top - 1] - (2 * frame1[top]) - frame1[top + 1] + frame1[bottom - 1] + (2 * frame1[bottom]) + frame1[bottom + 1];
                    gx2 = -frame2[top - 1] + frame2[top + 1] - (2 * frame2[mid - 1]) + (2 * frame2[mid + 1]) - frame2[bottom - 1] + frame2[bottom + 1];
                    gy2 = -frame2[top - 1] - (2 * frame2[top]) - frame2[top + 1] + frame2[bottom - 1] + (2 * frame2[bottom]) + frame2[bottom + 1];

                    edge1 = Math.Abs(gx1) + Math.Abs(gy1);
                    edge2 = Math.Abs(gx2) + Math.Abs(gy2);

                    sum1 += edge1;
                    sum2 += edge2;
                    count++;
                }
            }

            if (count > 0)
            {
                mean1 = sum1 / count;
                mean2 = sum2 / count;

                for (int y = 1; y < height - 1; y += 2)
                {
                    for (int x = 1; x < width - 1; x += 2)
                    {
                        index = y * width + x;
                        top = index - width;
                        mid = index;
                        bottom = index + width;

                        gx1 = -frame1[top - 1] + frame1[top + 1] - (2 * frame1[mid - 1]) + (2 * frame1[mid + 1]) - frame1[bottom - 1] + frame1[bottom + 1];
                        gy1 = -frame1[top - 1] - (2 * frame1[top]) - frame1[top + 1] + frame1[bottom - 1] + (2 * frame1[bottom]) + frame1[bottom + 1];
                        gx2 = -frame2[top - 1] + frame2[top + 1] - (2 * frame2[mid - 1]) + (2 * frame2[mid + 1]) - frame2[bottom - 1] + frame2[bottom + 1];
                        gy2 = -frame2[top - 1] - (2 * frame2[top]) - frame2[top + 1] + frame2[bottom - 1] + (2 * frame2[bottom]) + frame2[bottom + 1];

                        edge1 = Math.Abs(gx1) + Math.Abs(gy1);
                        edge2 = Math.Abs(gx2) + Math.Abs(gy2);
                        diff1 = edge1 - mean1;
                        diff2 = edge2 - mean2;

                        numerator += diff1 * diff2;
                        den1 += diff1 * diff1;
                        den2 += diff2 * diff2;
                    }
                }

                denominator = Math.Sqrt(den1 * den2);
                if (denominator > 0.0)
                {
                    result = numerator / denominator;
                    if (result < 0.0) { result = 0.0; }
                    if (result > 1.0) { result = 1.0; }
                }
            }

            return result;
        }

        /// <summary>
        /// Calcola correlazione edge media di una sequenza di frame consecutivi
        /// </summary>
        /// <param name="sourceFrames">Lista frame sorgente</param>
        /// <param name="sourceStartIdx">Indice iniziale nei frame sorgente</param>
        /// <param name="langFrames">Lista frame lingua</param>
        /// <param name="langStartIdx">Indice iniziale nei frame lingua</param>
        /// <param name="sequenceLength">Numero di frame nella sequenza</param>
        /// <returns>Correlazione media 0..1 o 0 se frame insufficienti</returns>
        public double ComputeSequenceEdgeCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            double totalCorrelation = 0.0;
            int validFrames = 0;
            double result = 0.0;
            int srcIdx;
            int lngIdx;
            for (int k = 0; k < sequenceLength; k++)
            {
                srcIdx = sourceStartIdx + k;
                lngIdx = langStartIdx + k;

                if (srcIdx >= sourceFrames.Count || lngIdx >= langFrames.Count)
                {
                    break;
                }

                totalCorrelation += this.ComputeEdgeCorrelation(sourceFrames[srcIdx], langFrames[lngIdx]);
                validFrames++;
            }

            if (validFrames >= sequenceLength)
            {
                result = totalCorrelation / validFrames;
            }

            return result;
        }

        /// <summary>
        /// Calcola correlazione tra fingerprint a blocchi su luma
        /// La media globale viene rimossa per resistere a differenze di luminosità/contrasto
        /// </summary>
        /// <param name="frame1">Primo frame grayscale</param>
        /// <param name="frame2">Secondo frame grayscale</param>
        /// <returns>Correlazione normalizzata 0..1</returns>
        public double ComputeBlockCorrelation(byte[] frame1, byte[] frame2)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            int blocksX = 16;
            int blocksY = 12;
            int blockCount = blocksX * blocksY;
            Span<double> means1 = stackalloc double[blockCount];
            Span<double> means2 = stackalloc double[blockCount];
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
            double result = 0.0;
            int x0;
            int x1;
            int y0;
            int y1;
            int idx;
            int pixelCount;
            long blockSum1;
            long blockSum2;
            int row;
            for (int by = 0; by < blocksY; by++)
            {
                y0 = (by * height) / blocksY;
                y1 = ((by + 1) * height) / blocksY;

                for (int bx = 0; bx < blocksX; bx++)
                {
                    x0 = (bx * width) / blocksX;
                    x1 = ((bx + 1) * width) / blocksX;
                    idx = (by * blocksX) + bx;
                    blockSum1 = 0;
                    blockSum2 = 0;
                    pixelCount = (x1 - x0) * (y1 - y0);

                    for (int y = y0; y < y1; y++)
                    {
                        row = y * width;
                        for (int x = x0; x < x1; x++)
                        {
                            blockSum1 += frame1[row + x];
                            blockSum2 += frame2[row + x];
                        }
                    }

                    if (pixelCount > 0)
                    {
                        means1[idx] = blockSum1 / (double)pixelCount;
                        means2[idx] = blockSum2 / (double)pixelCount;
                        sum1 += means1[idx];
                        sum2 += means2[idx];
                    }
                }
            }

            if (blockCount > 0)
            {
                mean1 = sum1 / blockCount;
                mean2 = sum2 / blockCount;

                for (int i = 0; i < blockCount; i++)
                {
                    diff1 = means1[i] - mean1;
                    diff2 = means2[i] - mean2;
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
            }

            return result;
        }

        /// <summary>
        /// Calcola correlazione media dei fingerprint a blocchi su una sequenza di frame
        /// </summary>
        /// <param name="sourceFrames">Lista frame sorgente</param>
        /// <param name="sourceStartIdx">Indice iniziale nei frame sorgente</param>
        /// <param name="langFrames">Lista frame lingua</param>
        /// <param name="langStartIdx">Indice iniziale nei frame lingua</param>
        /// <param name="sequenceLength">Numero di frame nella sequenza</param>
        /// <returns>Correlazione media 0..1 o 0 se frame insufficienti</returns>
        public double ComputeSequenceBlockCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            double totalCorrelation = 0.0;
            int validFrames = 0;
            double result = 0.0;
            int srcIdx;
            int lngIdx;
            for (int k = 0; k < sequenceLength; k++)
            {
                srcIdx = sourceStartIdx + k;
                lngIdx = langStartIdx + k;

                if (srcIdx >= sourceFrames.Count || lngIdx >= langFrames.Count)
                {
                    break;
                }

                totalCorrelation += this.ComputeBlockCorrelation(sourceFrames[srcIdx], langFrames[lngIdx]);
                validFrames++;
            }

            if (validFrames >= sequenceLength)
            {
                result = totalCorrelation / validFrames;
            }

            return result;
        }

        /// <summary>
        /// Calcola correlazione tra medie edge a blocchi
        /// Offre un segnale strutturale meno sensibile a grain/luma rispetto ai blocchi luma puri
        /// </summary>
        public double ComputeEdgeBlockCorrelation(byte[] frame1, byte[] frame2)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            int blocksX = 16;
            int blocksY = 12;
            int blockCount = blocksX * blocksY;
            Span<double> means1 = stackalloc double[blockCount];
            Span<double> means2 = stackalloc double[blockCount];
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
            double result = 0.0;
            int x0;
            int x1;
            int y0;
            int y1;
            int idx;
            int pixelCount;
            long blockSum1;
            long blockSum2;
            int index;
            int top;
            int mid;
            int bottom;
            int gx1;
            int gy1;
            int gx2;
            int gy2;
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
                    blockSum1 = 0;
                    blockSum2 = 0;
                    pixelCount = 0;

                    for (int y = y0; y < y1; y += 2)
                    {
                        for (int x = x0; x < x1; x += 2)
                        {
                            index = y * width + x;
                            top = index - width;
                            mid = index;
                            bottom = index + width;

                            gx1 = -frame1[top - 1] + frame1[top + 1] - (2 * frame1[mid - 1]) + (2 * frame1[mid + 1]) - frame1[bottom - 1] + frame1[bottom + 1];
                            gy1 = -frame1[top - 1] - (2 * frame1[top]) - frame1[top + 1] + frame1[bottom - 1] + (2 * frame1[bottom]) + frame1[bottom + 1];
                            gx2 = -frame2[top - 1] + frame2[top + 1] - (2 * frame2[mid - 1]) + (2 * frame2[mid + 1]) - frame2[bottom - 1] + frame2[bottom + 1];
                            gy2 = -frame2[top - 1] - (2 * frame2[top]) - frame2[top + 1] + frame2[bottom - 1] + (2 * frame2[bottom]) + frame2[bottom + 1];

                            blockSum1 += Math.Abs(gx1) + Math.Abs(gy1);
                            blockSum2 += Math.Abs(gx2) + Math.Abs(gy2);
                            pixelCount++;
                        }
                    }

                    if (pixelCount > 0)
                    {
                        means1[idx] = blockSum1 / (double)pixelCount;
                        means2[idx] = blockSum2 / (double)pixelCount;
                        sum1 += means1[idx];
                        sum2 += means2[idx];
                    }
                }
            }

            mean1 = sum1 / blockCount;
            mean2 = sum2 / blockCount;

            for (int i = 0; i < blockCount; i++)
            {
                diff1 = means1[i] - mean1;
                diff2 = means2[i] - mean2;
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
        /// Calcola correlazione media edge-block su una sequenza
        /// </summary>
        public double ComputeSequenceEdgeBlockCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            double totalCorrelation = 0.0;
            int validFrames = 0;
            double result = 0.0;
            int srcIdx;
            int lngIdx;
            for (int k = 0; k < sequenceLength; k++)
            {
                srcIdx = sourceStartIdx + k;
                lngIdx = langStartIdx + k;

                if (srcIdx >= sourceFrames.Count || lngIdx >= langFrames.Count)
                {
                    break;
                }

                totalCorrelation += this.ComputeEdgeBlockCorrelation(sourceFrames[srcIdx], langFrames[lngIdx]);
                validFrames++;
            }

            if (validFrames >= sequenceLength)
            {
                result = totalCorrelation / validFrames;
            }

            return result;
        }

        /// <summary>
        /// Calcola correlazione del movimento a blocchi tra due coppie di frame consecutivi
        /// Confronta le differenze per blocco, quindi resiste meglio a luma/contrasto diversi
        /// </summary>
        public double ComputeBlockMotionCorrelation(byte[] prevFrame1, byte[] frame1, byte[] prevFrame2, byte[] frame2)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            int blocksX = 16;
            int blocksY = 12;
            int blockCount = blocksX * blocksY;
            Span<double> motion1 = stackalloc double[blockCount];
            Span<double> motion2 = stackalloc double[blockCount];
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
            double result = 0.0;
            int x0;
            int x1;
            int y0;
            int y1;
            int idx;
            int pixelCount;
            long blockDelta1;
            long blockDelta2;
            int row;
            int pos;
            for (int by = 0; by < blocksY; by++)
            {
                y0 = (by * height) / blocksY;
                y1 = ((by + 1) * height) / blocksY;

                for (int bx = 0; bx < blocksX; bx++)
                {
                    x0 = (bx * width) / blocksX;
                    x1 = ((bx + 1) * width) / blocksX;
                    idx = (by * blocksX) + bx;
                    blockDelta1 = 0;
                    blockDelta2 = 0;
                    pixelCount = (x1 - x0) * (y1 - y0);

                    for (int y = y0; y < y1; y++)
                    {
                        row = y * width;
                        for (int x = x0; x < x1; x++)
                        {
                            pos = row + x;
                            blockDelta1 += frame1[pos] - prevFrame1[pos];
                            blockDelta2 += frame2[pos] - prevFrame2[pos];
                        }
                    }

                    if (pixelCount > 0)
                    {
                        motion1[idx] = blockDelta1 / (double)pixelCount;
                        motion2[idx] = blockDelta2 / (double)pixelCount;
                        sum1 += motion1[idx];
                        sum2 += motion2[idx];
                    }
                }
            }

            mean1 = sum1 / blockCount;
            mean2 = sum2 / blockCount;

            for (int i = 0; i < blockCount; i++)
            {
                diff1 = motion1[i] - mean1;
                diff2 = motion2[i] - mean2;
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
        /// Calcola correlazione media block-motion su una sequenza
        /// </summary>
        public double ComputeSequenceBlockMotionCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            double totalCorrelation = 0.0;
            int validFrames = 0;
            double result = 0.0;
            int srcIdx;
            int lngIdx;
            for (int k = 1; k < sequenceLength; k++)
            {
                srcIdx = sourceStartIdx + k;
                lngIdx = langStartIdx + k;

                if (srcIdx >= sourceFrames.Count || lngIdx >= langFrames.Count)
                {
                    break;
                }

                totalCorrelation += this.ComputeBlockMotionCorrelation(sourceFrames[srcIdx - 1], sourceFrames[srcIdx], langFrames[lngIdx - 1], langFrames[lngIdx]);
                validFrames++;
            }

            if (validFrames >= sequenceLength - 1)
            {
                result = totalCorrelation / validFrames;
            }

            return result;
        }

        /// <summary>
        /// Calcola similarità percettiva leggera combinando aHash e dHash
        /// </summary>
        /// <param name="frame1">Primo frame grayscale</param>
        /// <param name="frame2">Secondo frame grayscale</param>
        /// <returns>Similarità normalizzata 0..1</returns>
        public double ComputePerceptualHashSimilarity(byte[] frame1, byte[] frame2)
        {
            ulong averageHash1 = this.ComputeAverageHash(frame1);
            ulong averageHash2 = this.ComputeAverageHash(frame2);
            ulong differenceHash1 = this.ComputeDifferenceHash(frame1);
            ulong differenceHash2 = this.ComputeDifferenceHash(frame2);
            double averageSimilarity = this.ComputeHashSimilarity(averageHash1, averageHash2);
            double differenceSimilarity = this.ComputeHashSimilarity(differenceHash1, differenceHash2);

            return (averageSimilarity * 0.45) + (differenceSimilarity * 0.55);
        }

        /// <summary>
        /// Calcola similarità media aHash/dHash su una sequenza
        /// </summary>
        /// <param name="sourceFrames">Lista frame sorgente</param>
        /// <param name="sourceStartIdx">Indice iniziale nei frame sorgente</param>
        /// <param name="langFrames">Lista frame lingua</param>
        /// <param name="langStartIdx">Indice iniziale nei frame lingua</param>
        /// <param name="sequenceLength">Numero di frame nella sequenza</param>
        /// <returns>Similarità media 0..1 o 0 se frame insufficienti</returns>
        public double ComputeSequenceHashSimilarity(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            double totalSimilarity = 0.0;
            int validFrames = 0;
            double result = 0.0;
            int srcIdx;
            int lngIdx;
            for (int k = 0; k < sequenceLength; k++)
            {
                srcIdx = sourceStartIdx + k;
                lngIdx = langStartIdx + k;

                if (srcIdx >= sourceFrames.Count || lngIdx >= langFrames.Count)
                {
                    break;
                }

                totalSimilarity += this.ComputePerceptualHashSimilarity(sourceFrames[srcIdx], langFrames[lngIdx]);
                validFrames++;
            }

            if (validFrames >= sequenceLength)
            {
                result = totalSimilarity / validFrames;
            }

            return result;
        }

        /// <summary>
        /// Calcola average hash 8x8 su frame grayscale
        /// </summary>
        /// <param name="frame">Frame grayscale</param>
        /// <returns>Hash a 64 bit</returns>
        public ulong ComputeAverageHash(byte[] frame)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            Span<int> samples = stackalloc int[64];
            int index = 0;
            int sum = 0;
            int x;
            int y;
            int value;
            int average;
            ulong result = 0UL;

            for (int hy = 0; hy < 8; hy++)
            {
                y = ((hy * 2 + 1) * height) / 16;
                if (y >= height) { y = height - 1; }

                for (int hx = 0; hx < 8; hx++)
                {
                    x = ((hx * 2 + 1) * width) / 16;
                    if (x >= width) { x = width - 1; }

                    value = frame[y * width + x];
                    samples[index] = value;
                    sum += value;
                    index++;
                }
            }

            average = sum / 64;
            for (int i = 0; i < 64; i++)
            {
                if (samples[i] >= average)
                {
                    result |= 1UL << i;
                }
            }

            return result;
        }

        /// <summary>
        /// Calcola difference hash 8x8 confrontando campioni adiacenti su griglia 9x8
        /// </summary>
        /// <param name="frame">Frame grayscale</param>
        /// <returns>Hash a 64 bit</returns>
        public ulong ComputeDifferenceHash(byte[] frame)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            int left;
            int right;
            int x1;
            int x2;
            int y;
            int bit = 0;
            ulong result = 0UL;

            for (int hy = 0; hy < 8; hy++)
            {
                y = ((hy * 2 + 1) * height) / 16;
                if (y >= height) { y = height - 1; }

                for (int hx = 0; hx < 8; hx++)
                {
                    x1 = (hx * width) / 9;
                    x2 = ((hx + 1) * width) / 9;
                    if (x1 >= width) { x1 = width - 1; }
                    if (x2 >= width) { x2 = width - 1; }

                    left = frame[y * width + x1];
                    right = frame[y * width + x2];

                    if (left > right)
                    {
                        result |= 1UL << bit;
                    }
                    bit++;
                }
            }

            return result;
        }

        /// <summary>
        /// Calcola similarità tra due hash a 64 bit tramite distanza di Hamming
        /// </summary>
        /// <param name="hash1">Primo hash</param>
        /// <param name="hash2">Secondo hash</param>
        /// <returns>Similarità 0..1</returns>
        public double ComputeHashSimilarity(ulong hash1, ulong hash2)
        {
            int distance = BitOperations.PopCount(hash1 ^ hash2);

            return 1.0 - (distance / 64.0);
        }

        /// <summary>
        /// Calcola il fingerprint temporale di un taglio di scena usando luma, edge e block-motion
        /// </summary>
        /// <param name="frames">Lista frame grayscale</param>
        /// <param name="cutIndex">Indice del frame dove avviene il taglio</param>
        /// <returns>Array di valori inter-frame, o null se indici fuori range</returns>
        public double[] ComputeTemporalFingerprint(List<byte[]> frames, int cutIndex)
        {
            double[] fingerprint = null;
            int startIdx = cutIndex - this._videoSyncConfig.CutHalfWindow;
            int deltaCount = this._videoSyncConfig.CutSignatureLength - 1;
            int fingerprintLength = deltaCount * 3;

            if (startIdx >= 0 && startIdx + this._videoSyncConfig.CutSignatureLength <= frames.Count)
            {
                fingerprint = new double[fingerprintLength];

                for (int i = 0; i < deltaCount; i++)
                {
                    fingerprint[i] = this.ComputeMse(frames[startIdx + i], frames[startIdx + i + 1]);
                    fingerprint[deltaCount + i] = this.ComputeEdgeChange(frames[startIdx + i], frames[startIdx + i + 1]);
                    fingerprint[(deltaCount * 2) + i] = this.ComputeBlockMotionMagnitude(frames[startIdx + i], frames[startIdx + i + 1]);
                }

                this.NormalizeFingerprintInPlace(fingerprint);
            }

            return fingerprint;
        }

        /// <summary>
        /// Calcola la correlazione di Pearson tra due fingerprint temporali
        /// </summary>
        /// <param name="fp1">Primo fingerprint</param>
        /// <param name="fp2">Secondo fingerprint</param>
        /// <returns>Coefficiente di correlazione [-1, 1], o 0 se non calcolabile</returns>
        public double ComputeFingerprintCorrelation(double[] fp1, double[] fp2)
        {
            double result = 0.0;
            double mean1 = 0.0;
            double mean2 = 0.0;
            double num = 0.0;
            double den1 = 0.0;
            double den2 = 0.0;
            double diff1;
            double diff2;
            double denominator;
            int fingerprintLength;
            if (fp1 == null || fp2 == null || fp1.Length == 0 || fp1.Length != fp2.Length)
            {
                return result;
            }

            fingerprintLength = fp1.Length;

            for (int i = 0; i < fingerprintLength; i++)
            {
                mean1 += fp1[i];
                mean2 += fp2[i];
            }
            mean1 /= fingerprintLength;
            mean2 /= fingerprintLength;

            for (int i = 0; i < fingerprintLength; i++)
            {
                diff1 = fp1[i] - mean1;
                diff2 = fp2[i] - mean2;
                num += diff1 * diff2;
                den1 += diff1 * diff1;
                den2 += diff2 * diff2;
            }

            denominator = Math.Sqrt(den1 * den2);
            if (denominator > 0.0)
            {
                result = num / denominator;
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Calcola variazione edge campionata tra due frame consecutivi
        /// </summary>
        private double ComputeEdgeChange(byte[] frame1, byte[] frame2)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            int index;
            int top;
            int mid;
            int bottom;
            int gx1;
            int gy1;
            int gx2;
            int gy2;
            double edge1;
            double edge2;
            double sum = 0.0;
            int count = 0;
            for (int y = 1; y < height - 1; y += 3)
            {
                for (int x = 1; x < width - 1; x += 3)
                {
                    index = y * width + x;
                    top = index - width;
                    mid = index;
                    bottom = index + width;

                    gx1 = -frame1[top - 1] + frame1[top + 1] - (2 * frame1[mid - 1]) + (2 * frame1[mid + 1]) - frame1[bottom - 1] + frame1[bottom + 1];
                    gy1 = -frame1[top - 1] - (2 * frame1[top]) - frame1[top + 1] + frame1[bottom - 1] + (2 * frame1[bottom]) + frame1[bottom + 1];
                    gx2 = -frame2[top - 1] + frame2[top + 1] - (2 * frame2[mid - 1]) + (2 * frame2[mid + 1]) - frame2[bottom - 1] + frame2[bottom + 1];
                    gy2 = -frame2[top - 1] - (2 * frame2[top]) - frame2[top + 1] + frame2[bottom - 1] + (2 * frame2[bottom]) + frame2[bottom + 1];

                    edge1 = Math.Abs(gx1) + Math.Abs(gy1);
                    edge2 = Math.Abs(gx2) + Math.Abs(gy2);
                    sum += Math.Abs(edge2 - edge1);
                    count++;
                }
            }

            return count > 0 ? sum / count : 0.0;
        }

        /// <summary>
        /// Calcola magnitudine media del movimento a blocchi tra due frame consecutivi
        /// </summary>
        private double ComputeBlockMotionMagnitude(byte[] prevFrame, byte[] frame)
        {
            int width = this._videoSyncConfig.FrameWidth;
            int height = this._videoSyncConfig.FrameHeight;
            int blocksX = 16;
            int blocksY = 12;
            int x0;
            int x1;
            int y0;
            int y1;
            int pixelCount;
            int row;
            int pos;
            long blockDelta;
            double total = 0.0;
            int blockCount = 0;
            for (int by = 0; by < blocksY; by++)
            {
                y0 = (by * height) / blocksY;
                y1 = ((by + 1) * height) / blocksY;

                for (int bx = 0; bx < blocksX; bx++)
                {
                    x0 = (bx * width) / blocksX;
                    x1 = ((bx + 1) * width) / blocksX;
                    blockDelta = 0;
                    pixelCount = (x1 - x0) * (y1 - y0);

                    for (int y = y0; y < y1; y++)
                    {
                        row = y * width;
                        for (int x = x0; x < x1; x++)
                        {
                            pos = row + x;
                            blockDelta += frame[pos] - prevFrame[pos];
                        }
                    }

                    if (pixelCount > 0)
                    {
                        total += Math.Abs(blockDelta / (double)pixelCount);
                        blockCount++;
                    }
                }
            }

            return blockCount > 0 ? total / blockCount : 0.0;
        }

        /// <summary>
        /// Normalizza fingerprint con z-score e clipping outlier
        /// </summary>
        private void NormalizeFingerprintInPlace(double[] fingerprint)
        {
            double mean = 0.0;
            double variance = 0.0;
            double std;
            double value;
            for (int i = 0; i < fingerprint.Length; i++)
            {
                mean += fingerprint[i];
            }
            mean /= fingerprint.Length;

            for (int i = 0; i < fingerprint.Length; i++)
            {
                value = fingerprint[i] - mean;
                variance += value * value;
            }
            variance /= fingerprint.Length;
            std = Math.Sqrt(variance);

            if (std <= 0.000001)
            {
                return;
            }

            for (int i = 0; i < fingerprint.Length; i++)
            {
                value = (fingerprint[i] - mean) / std;
                if (value > 3.0) { value = 3.0; }
                if (value < -3.0) { value = -3.0; }
                fingerprint[i] = value;
            }
        }

        /// <summary>
        /// Calcola pixel blur 3x3 al volo
        /// </summary>
        private double GetBlurredPixel(byte[] frame, int width, int x, int y)
        {
            int idx = y * width + x;
            int top = idx - width;
            int bottom = idx + width;

            return (frame[top - 1] + frame[top] + frame[top + 1] +
                    frame[idx - 1] + frame[idx] + frame[idx + 1] +
                    frame[bottom - 1] + frame[bottom] + frame[bottom + 1]) / 9.0;
        }

        #endregion
    }
}
