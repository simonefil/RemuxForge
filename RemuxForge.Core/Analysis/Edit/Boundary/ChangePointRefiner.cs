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
        /// <param name="phaseOffsetBeforeMs">Offset precedente della scala allineata alla fase dei fotogrammi</param>
        /// <param name="phaseOffsetAfterMs">Offset successivo della scala allineata alla fase dei fotogrammi</param>
        /// <param name="globalJumpMs">Salto determinato dalla scala globale</param>
        /// <returns>Confine trovato oppure null quando la finestra è troppo corta</returns>
        public ChangePointResult Refine(PairSignals pair, double windowStartMs, double windowEndMs,
            double offsetBeforeMs, double offsetAfterMs, double phaseOffsetBeforeMs, double phaseOffsetAfterMs, double globalJumpMs)
        {
            double[] sourcePts = pair.Source.PtsMs;
            int first = HashOps.LowerBound(sourcePts, windowStartMs);
            int count = HashOps.LowerBound(sourcePts, windowEndMs) - first;
            if (count < 4)
                return null;

            double languageFrameStepMs = MedianFrameStep(pair.LanguagePtsMs, 0, Math.Min(pair.LanguagePtsMs.Length, 1000));
            int jumpFrames = (int)Math.Round(globalJumpMs / languageFrameStepMs);
            bool overlappingNeighborhoods = jumpFrames <= 2 * EditAnalysisProfile.DETECTION_RADIUS;
            if (overlappingNeighborhoods)
            {
                offsetBeforeMs = phaseOffsetBeforeMs;
                offsetAfterMs = phaseOffsetAfterMs;
            }

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
            // Gli intorni sovrapposti non distinguono i due offset: in quel regime decide la
            // miniatura sul frame previsto. Per i salti grandi resta informativo il margine dHash
            int lastIndex = Math.Min(plateauEnd, count - 1);
            int firstIndex = Math.Min(plateauStart, count - 1);
            if (overlappingNeighborhoods)
            {
                double[] pixelBefore = new double[count];
                double[] pixelAfter = new double[count];
                for (int i = 0; i < count; i++)
                {
                    pixelBefore[i] = ThumbnailDistance(pair, first + i, offsetBeforeMs);
                    pixelAfter[i] = ThumbnailDistance(pair, first + i, offsetAfterMs);
                }

                double[] pixelPrefixBefore = PrefixSum(pixelBefore);
                double[] pixelPrefixAfter = PrefixSum(pixelAfter);
                double bestPixelCost = double.MaxValue;
                int bestPixelIndex = 0;
                for (int k = 0; k <= count; k++)
                {
                    double cost = pixelPrefixBefore[k] + pixelPrefixAfter[count] - pixelPrefixAfter[k];
                    if (cost < bestPixelCost)
                    {
                        bestPixelCost = cost;
                        bestPixelIndex = k;
                    }
                }
                plateauStart = bestPixelIndex;
                plateauEnd = bestPixelIndex;
            }
            else if (globalJumpMs >= EditAnalysisProfile.CHANGEPOINT_MIN_JUMP_MS &&
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

            return new ChangePointResult
            {
                NextAfterLastMs = sourcePts[Math.Min(lastCommon + 1, sourcePts.Length - 1)],
                FirstCommonMs = sourcePts[firstCommon],
                TouchesWindowStart = plateauStart == 0,
                TouchesWindowEnd = plateauEnd == count
            };
        }

        /// <summary>
        /// Trova lo stacco più vicino alla stima soltanto fra changepoint equivalenti
        /// </summary>
        /// <param name="signals">Segnali sorgente</param>
        /// <param name="startMs">Primo changepoint equivalente</param>
        /// <param name="endMs">Ultimo changepoint equivalente</param>
        /// <param name="referenceMs">Stima ottenuta dal costo di allineamento</param>
        /// <param name="distance">Distanza dHash attraverso lo stacco</param>
        /// <returns>PTS dello stacco oppure la stima quando non esiste uno stacco netto</returns>
        public double VisualBoundary(Extraction.FrameSignals signals, double startMs, double endMs, double referenceMs, out int distance)
        {
            int first = Math.Max(1, HashOps.LowerBound(signals.PtsMs, startMs) - EditAnalysisProfile.VERIFICATION_RADIUS);
            int end = Math.Min(signals.Count - 1, HashOps.LowerBound(signals.PtsMs, endMs) + EditAnalysisProfile.VERIFICATION_RADIUS);
            int bestIndex = -1;
            double bestDistanceMs = double.MaxValue;
            distance = 0;
            for (int index = first; index <= end; index++)
            {
                int candidateDistance = HashOps.Distance(signals, index - 1, signals, index);
                if (candidateDistance <= EditAnalysisProfile.VERIFICATION_THRESHOLD)
                    continue;
                double candidateDistanceMs = Math.Abs(signals.PtsMs[index] - referenceMs);
                if (candidateDistanceMs >= bestDistanceMs)
                    continue;
                bestIndex = index;
                bestDistanceMs = candidateDistanceMs;
                distance = candidateDistance;
            }
            return bestIndex >= 0 ? signals.PtsMs[bestIndex] : referenceMs;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Distanza media fra miniature, senza il livello medio che cambia fra due edizioni
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="sourceIndex">Indice del fotogramma sorgente</param>
        /// <param name="offsetMs">Offset da verificare</param>
        /// <returns>Distanza dal fotogramma lang temporalmente più vicino</returns>
        private static double ThumbnailDistance(PairSignals pair, int sourceIndex, double offsetMs)
        {
            int pixels = Extraction.FrameSignals.THUMB_SIDE * Extraction.FrameSignals.THUMB_SIDE;
            int sourceOffset = sourceIndex * pixels;
            double sourceMean = 0.0;
            for (int pixel = 0; pixel < pixels; pixel++)
                sourceMean += pair.Source.ThumbPixels[sourceOffset + pixel];
            sourceMean /= pixels;

            double targetMs = pair.Source.PtsMs[sourceIndex] + offsetMs;
            int languageIndex = HashOps.LowerBound(pair.LanguagePtsMs, targetMs);
            if (languageIndex >= pair.Language.Count)
                languageIndex = pair.Language.Count - 1;
            else if (languageIndex > 0 && targetMs - pair.LanguagePtsMs[languageIndex - 1] <= pair.LanguagePtsMs[languageIndex] - targetMs)
                languageIndex--;

            int languageOffset = languageIndex * pixels;
            double languageMean = 0.0;
            for (int pixel = 0; pixel < pixels; pixel++)
                languageMean += pair.Language.ThumbPixels[languageOffset + pixel];
            languageMean /= pixels;

            double distance = 0.0;
            for (int pixel = 0; pixel < pixels; pixel++)
                distance += Math.Abs((pair.Source.ThumbPixels[sourceOffset + pixel] - sourceMean) -
                                     (pair.Language.ThumbPixels[languageOffset + pixel] - languageMean));
            return distance / pixels;
        }

        /// <summary>
        /// Somme prefisse con lo zero iniziale
        /// </summary>
        /// <param name="values">Valori da accumulare</param>
        /// <returns>Somme prefisse</returns>
        private static double[] PrefixSum(double[] values)
        {
            double[] result = new double[values.Length + 1];
            for (int i = 0; i < values.Length; i++)
                result[i + 1] = result[i] + values[i];
            return result;
        }

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
