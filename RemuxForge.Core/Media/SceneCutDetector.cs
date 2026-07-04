using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Rileva tagli scena su frame grayscale normalizzati
    /// </summary>
    public class SceneCutDetector
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
        public SceneCutDetector(VideoSyncConfig videoSyncConfig)
        {
            this._videoSyncConfig = videoSyncConfig;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Rileva tagli di scena tramite MSE tra frame consecutivi
        /// </summary>
        public List<int> Detect(List<byte[]> frames)
        {
            return this.Detect(frames, true);
        }

        /// <summary>
        /// Rileva tagli di scena tramite MSE tra frame consecutivi con soglia più permissiva
        /// </summary>
        public List<int> DetectRelaxed(List<byte[]> frames)
        {
            return this.Detect(frames, false);
        }

        /// <summary>
        /// Calcola una soglia dinamica per tagli scena usando mediana e MAD dei delta inter-frame
        /// </summary>
        public double ComputeAdaptiveThreshold(double[] frameDiffs, bool useConfiguredFloor)
        {
            double result = useConfiguredFloor ? this._videoSyncConfig.SceneCutThreshold : 5.0;
            double[] sorted;
            double[] deviations;
            double median;
            double mad;
            double adaptive;
            if (frameDiffs != null && frameDiffs.Length > 0)
            {
                sorted = new double[frameDiffs.Length];
                Array.Copy(frameDiffs, sorted, frameDiffs.Length);
                Array.Sort(sorted);
                median = this.ComputeMedianSorted(sorted);

                deviations = new double[frameDiffs.Length];
                for (int i = 0; i < frameDiffs.Length; i++)
                {
                    deviations[i] = Math.Abs(frameDiffs[i] - median);
                }
                Array.Sort(deviations);
                mad = this.ComputeMedianSorted(deviations);

                adaptive = median + ((useConfiguredFloor ? 8.0 : 5.0) * mad);
                if (adaptive > result)
                {
                    result = adaptive;
                }
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Rileva tagli di scena tramite MSE tra frame consecutivi
        /// </summary>
        private List<int> Detect(List<byte[]> frames, bool useConfiguredFloor)
        {
            List<int> cuts = new List<int>();
            double[] frameDiffs;
            double interMse;
            int lastCutIdx = -this._videoSyncConfig.MinCutSpacingFrames;
            double threshold;

            if (frames != null && frames.Count > 1)
            {
                frameDiffs = new double[frames.Count - 1];

                for (int i = 0; i < frames.Count - 1; i++)
                {
                    frameDiffs[i] = this.ComputeMse(frames[i], frames[i + 1]);
                }

                threshold = this.ComputeAdaptiveThreshold(frameDiffs, useConfiguredFloor);

                for (int i = 0; i < frameDiffs.Length; i++)
                {
                    interMse = frameDiffs[i];
                    if (interMse > threshold && (i + 1 - lastCutIdx) >= this._videoSyncConfig.MinCutSpacingFrames)
                    {
                        cuts.Add(i + 1);
                        lastCutIdx = i + 1;
                    }
                }
            }

            return cuts;
        }

        /// <summary>
        /// Calcola MSE tra due frame grayscale
        /// </summary>
        private double ComputeMse(byte[] frame1, byte[] frame2)
        {
            double sumSquaredDiff = 0.0;
            int length = frame1.Length;
            int diff;
            for (int i = 0; i < length; i++)
            {
                diff = frame1[i] - frame2[i];
                sumSquaredDiff += diff * diff;
            }

            return sumSquaredDiff / length;
        }

        /// <summary>
        /// Calcola la mediana di un array già ordinato
        /// </summary>
        private double ComputeMedianSorted(double[] values)
        {
            double result = 0.0;
            int mid;
            if (values != null && values.Length > 0)
            {
                mid = values.Length / 2;
                if ((values.Length % 2) == 0)
                {
                    result = (values[mid - 1] + values[mid]) / 2.0;
                }
                else
                {
                    result = values[mid];
                }
            }

            return result;
        }

        #endregion
    }
}
