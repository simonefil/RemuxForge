using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Core.Analysis.Edit.Detection
{
    /// <summary>
    /// Rimisura la scala a finestra stretta dove il profilo largo rampa invece di gradinare
    /// </summary>
    internal class FineScaleRescuer
    {
        #region Metodi pubblici

        /// <summary>
        /// Le operazioni che la scala fine trova dentro le zone sospette del profilo largo
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="profile">Profilo largo completo</param>
        /// <param name="cancellation">Token di annullamento</param>
        /// <returns>Operazioni candidate lette dai pianori della scala fine</returns>
        public List<EditOperationCandidate> Detect(PairSignals pair, IReadOnlyList<OffsetProfilePoint> profile, CancellationToken cancellation)
        {
            // Non si può misurare tutto così: si rimisura solo dove il profilo largo si muove
            // o cala di qualità, che è anche l'unico posto dove può esserci un'operazione
            List<EditOperationCandidate> result = new List<EditOperationCandidate>();
            foreach (double[] zone in this.SuspectZones(profile))
            {
                cancellation.ThrowIfCancellationRequested();
                List<double[]> plateaus = this.Plateaus(this.Scale(pair, zone[0], zone[1], zone[2], cancellation));
                for (int i = 0; i + 1 < plateaus.Count; i++)
                {
                    double offsetBeforeMs = plateaus[i][2];
                    double offsetAfterMs = plateaus[i + 1][2];
                    if (Math.Abs(offsetAfterMs - offsetBeforeMs) <= EditAnalysisProfile.FINE_GAP_MS)
                        continue;
                    result.Add(new EditOperationCandidate
                    {
                        Kind = offsetAfterMs < offsetBeforeMs ? EditOperationKind.InsertSilence : EditOperationKind.CutSegment,
                        TimestampMs = (plateaus[i][1] + plateaus[i + 1][0]) / 2.0,
                        DurationMs = Math.Abs(offsetAfterMs - offsetBeforeMs),
                        OffsetBeforeMs = offsetBeforeMs,
                        OffsetAfterMs = offsetAfterMs,
                        PlateauEndBeforeMs = plateaus[i][1],
                        PlateauStartAfterMs = plateaus[i + 1][0]
                    });
                }
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Intervalli in cui il profilo largo non sta fermo, uniti se si toccano
        /// </summary>
        /// <param name="profile">Profilo largo completo</param>
        /// <returns>Terne di inizio, fine e offset di riferimento</returns>
        private List<double[]> SuspectZones(IReadOnlyList<OffsetProfilePoint> profile)
        {
            List<double[]> moved = new List<double[]>();
            OffsetProfilePoint previous = null;
            foreach (OffsetProfilePoint point in profile)
            {
                if (point.Explained < EditAnalysisProfile.PROFILE_GOOD_FRACTION)
                {
                    moved.Add(new double[] { point.TimeMs, point.TimeMs, point.OffsetMs });
                    continue;
                }
                if (previous != null && Math.Abs(point.OffsetMs - previous.OffsetMs) > EditAnalysisProfile.FINE_STILL_MS)
                    moved.Add(new double[] { previous.TimeMs, point.TimeMs, (previous.OffsetMs + point.OffsetMs) / 2.0 });
                previous = point;
            }

            moved.Sort((left, right) => left[0].CompareTo(right[0]));
            List<double[]> merged = new List<double[]>();
            foreach (double[] zone in moved)
            {
                if (merged.Count > 0 && zone[0] - EditAnalysisProfile.FINE_WIDEN_MS <= merged[merged.Count - 1][1] + EditAnalysisProfile.FINE_WIDEN_MS)
                {
                    merged[merged.Count - 1][1] = Math.Max(merged[merged.Count - 1][1], zone[1]);
                    continue;
                }
                merged.Add(new double[] { zone[0], zone[1], zone[2] });
            }

            List<double[]> result = new List<double[]>();
            foreach (double[] zone in merged)
                result.Add(new double[] { zone[0] - EditAnalysisProfile.FINE_WIDEN_MS, zone[1] + EditAnalysisProfile.FINE_WIDEN_MS, zone[2] });
            return result;
        }

        /// <summary>
        /// La scala offset(t) misurata a finestra di un secondo, con offset cercato al millisecondo
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="startMs">Inizio dell'intervallo</param>
        /// <param name="endMs">Fine dell'intervallo</param>
        /// <param name="centerMs">Offset di riferimento</param>
        /// <param name="cancellation">Token di annullamento</param>
        /// <returns>Quaterne di istante, offset, distanza mediana e nettezza del minimo</returns>
        private List<double[]> Scale(PairSignals pair, double startMs, double endMs, double centerMs, CancellationToken cancellation)
        {
            List<double[]> result = new List<double[]>();
            int coarseCount = (int)Math.Floor(2.0 * EditAnalysisProfile.FINE_RADIUS_MS / EditAnalysisProfile.FINE_COARSE_STEP_MS) + 1;
            int fineCount = (int)(2.0 * EditAnalysisProfile.FINE_COARSE_STEP_MS) + 1;

            for (double timeMs = startMs; timeMs < endMs; timeMs += EditAnalysisProfile.FINE_TIME_STEP_MS)
            {
                cancellation.ThrowIfCancellationRequested();
                int[] indices = HashOps.RangeIndices(pair, timeMs, timeMs + EditAnalysisProfile.FINE_WINDOW_MS, 1);
                if (indices.Length < 8)
                    continue;

                double[] coarse = new double[coarseCount];
                int bestIndex = 0;
                for (int i = 0; i < coarseCount; i++)
                {
                    coarse[i] = HashOps.MedianDistance(pair, indices, centerMs - EditAnalysisProfile.FINE_RADIUS_MS + i * EditAnalysisProfile.FINE_COARSE_STEP_MS, EditAnalysisProfile.DETECTION_RADIUS);
                    if (coarse[i] < coarse[bestIndex])
                        bestIndex = i;
                }
                double coarseBestMs = centerMs - EditAnalysisProfile.FINE_RADIUS_MS + bestIndex * EditAnalysisProfile.FINE_COARSE_STEP_MS;

                double[] fine = new double[fineCount];
                double floor = double.MaxValue;
                for (int i = 0; i < fineCount; i++)
                {
                    fine[i] = HashOps.MedianDistance(pair, indices, coarseBestMs - EditAnalysisProfile.FINE_COARSE_STEP_MS + i, EditAnalysisProfile.DETECTION_RADIUS);
                    floor = Math.Min(floor, fine[i]);
                }

                double total = 0.0;
                int members = 0;
                for (int i = 0; i < fineCount; i++)
                {
                    if (fine[i] > floor)
                        continue;
                    total += coarseBestMs - EditAnalysisProfile.FINE_COARSE_STEP_MS + i;
                    members++;
                }

                double[] sorted = (double[])coarse.Clone();
                Array.Sort(sorted);
                double median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
                result.Add(new double[] { timeMs, total / members, floor, median - floor });
            }

            return result;
        }

        /// <summary>
        /// I tratti della scala fine in cui l'offset sta fermo
        /// </summary>
        /// <param name="scale">Punti della scala fine</param>
        /// <returns>Terne di inizio, fine e offset del pianoro</returns>
        private List<double[]> Plateaus(IReadOnlyList<double[]> scale)
        {
            List<double[]> running = new List<double[]>();
            List<int> counts = new List<int>();
            foreach (double[] point in scale)
            {
                if (point[3] <= EditAnalysisProfile.FINE_MIN_CONTRAST || point[2] > EditAnalysisProfile.DETECTION_THRESHOLD)
                    continue;
                if (running.Count > 0 && Math.Abs(point[1] - running[running.Count - 1][2]) <= EditAnalysisProfile.FINE_GAP_MS)
                {
                    int index = running.Count - 1;
                    int total = counts[index] + 1;
                    running[index][1] = point[0];
                    running[index][2] = (running[index][2] * counts[index] + point[1]) / total;
                    counts[index] = total;
                    continue;
                }
                running.Add(new double[] { point[0], point[0], point[1] });
                counts.Add(1);
            }

            List<double[]> result = new List<double[]>();
            foreach (double[] plateau in running)
            {
                if (plateau[1] - plateau[0] >= EditAnalysisProfile.FINE_PLATEAU_MS)
                    result.Add(plateau);
            }
            return result;
        }

        #endregion
    }
}
