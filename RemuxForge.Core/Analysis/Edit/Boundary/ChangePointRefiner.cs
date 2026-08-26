using System;

namespace RemuxForge.Core.Analysis.Edit.Boundary
{
    /// <summary>
    /// Changepoint esatto sui fotogrammi a piena frequenza fra due offset noti
    /// </summary>
    internal class ChangePointRefiner
    {
        #region Metodi pubblici

        /// <summary>
        /// Trova il confine che minimizza i fotogrammi non spiegati fra i due offset
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="windowStartMs">Inizio della finestra di ricerca</param>
        /// <param name="windowEndMs">Fine della finestra di ricerca</param>
        /// <param name="offsetBeforeMs">Offset del pianoro precedente</param>
        /// <param name="offsetAfterMs">Offset del pianoro successivo</param>
        /// <returns>Confine trovato oppure null quando la finestra è troppo corta</returns>
        public ChangePointResult Refine(PairSignals pair, double windowStartMs, double windowEndMs, double offsetBeforeMs, double offsetAfterMs)
        {
            double[] sourcePts = pair.Source.PtsMs;
            int first = HashOps.LowerBound(sourcePts, windowStartMs);
            int count = HashOps.LowerBound(sourcePts, windowEndMs) - first;
            if (count < 4)
                return null;

            double[] distanceBefore = new double[count];
            double[] distanceAfter = new double[count];
            int[] unexplainedBefore = new int[count + 1];
            int[] unexplainedAfter = new int[count + 1];
            for (int i = 0; i < count; i++)
            {
                distanceBefore[i] = HashOps.Distance(pair, first + i, offsetBeforeMs, EditAnalysisProfile.DETECTION_RADIUS);
                distanceAfter[i] = HashOps.Distance(pair, first + i, offsetAfterMs, EditAnalysisProfile.DETECTION_RADIUS);
                unexplainedBefore[i + 1] = unexplainedBefore[i] + (distanceBefore[i] <= EditAnalysisProfile.DETECTION_THRESHOLD ? 0 : 1);
                unexplainedAfter[i + 1] = unexplainedAfter[i] + (distanceAfter[i] <= EditAnalysisProfile.DETECTION_THRESHOLD ? 0 : 1);
            }

            int best = int.MaxValue;
            for (int k = 0; k <= count; k++)
            {
                int cost = unexplainedBefore[k] + (unexplainedAfter[count] - unexplainedAfter[k]);
                if (cost < best)
                    best = cost;
            }
            int plateauStart = -1;
            int plateauEnd = -1;
            for (int k = 0; k <= count; k++)
            {
                if (unexplainedBefore[k] + (unexplainedAfter[count] - unexplainedAfter[k]) != best)
                    continue;
                if (plateauStart < 0)
                    plateauStart = k;
                plateauEnd = k;
            }

            // Dove i due offset stanno tutti e due sotto soglia il costo è piatto per secondi.
            // Dentro il pianoro l'informazione resta nel margine: A è più vicino di B fino al
            // confine, e dopo si scambiano. Vale solo per i salti grandi
            int lastIndex = Math.Min(plateauEnd, count - 1);
            int firstIndex = Math.Min(plateauStart, count - 1);
            if (Math.Abs(offsetAfterMs - offsetBeforeMs) >= EditAnalysisProfile.CHANGEPOINT_MIN_JUMP_MS &&
                sourcePts[first + lastIndex] - sourcePts[first + firstIndex] >= EditAnalysisProfile.CHANGEPOINT_MIN_PLATEAU_MS)
            {
                double stepMs = MedianFrameStep(sourcePts, first, count);
                int window = Math.Max(3, (int)(EditAnalysisProfile.CHANGEPOINT_SMOOTH_MS / stepMs));
                double firstMargin = SmoothedMargin(distanceAfter, distanceBefore, window, firstIndex);
                if (firstMargin > 0.0)
                {
                    for (int k = firstIndex; k <= lastIndex; k++)
                    {
                        if (SmoothedMargin(distanceAfter, distanceBefore, window, k) > 0.0)
                            continue;
                        plateauStart = k;
                        break;
                    }
                }
            }

            int lastCommon = plateauStart > 0 ? first + plateauStart - 1 : first;
            int firstCommon = plateauEnd < count ? first + plateauEnd : first + count - 1;
            bool black = firstCommon > lastCommon + 1;
            for (int i = lastCommon + 1; i < firstCommon && black; i++)
                black = pair.Source.ThumbStd[i] < EditAnalysisProfile.BLACK_LUMA;

            return new ChangePointResult
            {
                LastCommonMs = sourcePts[lastCommon],
                NextAfterLastMs = sourcePts[Math.Min(lastCommon + 1, sourcePts.Length - 1)],
                FirstCommonMs = sourcePts[firstCommon],
                UnexplainedFrames = best,
                IsBlack = black
            };
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Passo mediano fra i fotogrammi della finestra
        /// </summary>
        /// <param name="sourcePts">PTS della sorgente</param>
        /// <param name="first">Primo indice della finestra</param>
        /// <param name="count">Fotogrammi della finestra</param>
        /// <returns>Durata mediana di un fotogramma</returns>
        private static double MedianFrameStep(double[] sourcePts, int first, int count)
        {
            double[] steps = new double[count - 1];
            for (int i = 0; i < count - 1; i++)
                steps[i] = sourcePts[first + i + 1] - sourcePts[first + i];
            Array.Sort(steps);
            int middle = steps.Length / 2;
            double result = steps.Length % 2 == 1 ? steps[middle] : (steps[middle - 1] + steps[middle]) / 2.0;
            return result > 0.0 ? result : 40.0;
        }

        /// <summary>
        /// Margine fra le due distanze mediato su una finestra centrata
        /// </summary>
        /// <param name="distanceAfter">Distanze rispetto all'offset successivo</param>
        /// <param name="distanceBefore">Distanze rispetto all'offset precedente</param>
        /// <param name="window">Ampiezza della media mobile in fotogrammi</param>
        /// <param name="index">Fotogramma su cui valutare il margine</param>
        /// <returns>Media mobile di distanza successiva meno precedente</returns>
        private static double SmoothedMargin(double[] distanceAfter, double[] distanceBefore, int window, int index)
        {
            int center = index + (window - 1) / 2;
            int from = Math.Max(0, center - window + 1);
            int to = Math.Min(distanceAfter.Length - 1, center);
            double total = 0.0;
            for (int i = from; i <= to; i++)
                total += distanceAfter[i] - distanceBefore[i];
            return total / window;
        }

        #endregion
    }
}
