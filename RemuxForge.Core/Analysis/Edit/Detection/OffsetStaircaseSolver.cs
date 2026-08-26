using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Edit.Detection
{
    /// <summary>
    /// Spezza il profilo in rette e legge le operazioni come sue discontinuità significative
    /// </summary>
    internal class OffsetStaircaseSolver
    {
        #region Metodi pubblici

        /// <summary>
        /// Partiziona il profilo minimizzando residui più una penalità per ogni rottura
        /// </summary>
        /// <param name="points">Punti buoni del profilo, in ordine di tempo</param>
        /// <returns>Tratti rettilinei consecutivi</returns>
        public List<OffsetSegment> Segment(IReadOnlyList<OffsetProfilePoint> points)
        {
            // I segmenti sono rette e non costanti perché il residuo dello stretch fa derivare
            // l'offset, e una deriva letta come costante a tratti diventa una scala di finti gradini
            int count = points.Count;
            List<OffsetSegment> result = new List<OffsetSegment>();
            if (count < EditAnalysisProfile.SEGMENT_MIN_POINTS)
                return result;

            double[] times = new double[count];
            double[] values = new double[count];
            for (int i = 0; i < count; i++)
            {
                times[i] = points[i].TimeMs;
                values[i] = points[i].OffsetMs;
            }

            double[][] prefixes = BuildPrefixes(times, values);
            double[] cost = new double[count + 1];
            int[] origin = new int[count + 1];
            for (int i = 1; i <= count; i++)
                cost[i] = double.PositiveInfinity;
            cost[0] = -EditAnalysisProfile.SEGMENT_BREAK_PENALTY;

            for (int end = EditAnalysisProfile.SEGMENT_MIN_POINTS; end <= count; end++)
            {
                for (int start = 0; start <= end - EditAnalysisProfile.SEGMENT_MIN_POINTS; start++)
                {
                    if (double.IsInfinity(cost[start]))
                        continue;
                    double candidate = cost[start] + Fit(prefixes, start, end, out _, out _, out _) + EditAnalysisProfile.SEGMENT_BREAK_PENALTY;
                    if (candidate < cost[end])
                    {
                        cost[end] = candidate;
                        origin[end] = start;
                    }
                }
            }

            List<int> cuts = new List<int>();
            for (int j = count; j > 0; j = origin[j])
                cuts.Add(j);
            cuts.Add(0);
            cuts.Sort();

            for (int k = 0; k + 1 < cuts.Count; k++)
            {
                int start = cuts[k];
                int end = cuts[k + 1];
                double residual = Fit(prefixes, start, end, out double intercept, out double slope, out int points_);
                double timeMean = 0.0;
                for (int i = start; i < end; i++)
                    timeMean += times[i];
                timeMean /= Math.Max(points_, 1);
                double timeVariation = 0.0;
                for (int i = start; i < end; i++)
                    timeVariation += (times[i] - timeMean) * (times[i] - timeMean);

                result.Add(new OffsetSegment
                {
                    FirstIndex = start,
                    EndIndex = end,
                    Intercept = intercept,
                    Slope = slope,
                    ResidualVariance = residual / Math.Max(points_ - 2, 1),
                    PointCount = points_,
                    TimeMean = timeMean,
                    TimeVariation = timeVariation,
                    StartMs = times[start],
                    EndMs = times[end - 1]
                });
            }

            return result;
        }

        /// <summary>
        /// Legge i gradini significativi fra segmenti adiacenti
        /// </summary>
        /// <param name="segments">Tratti rettilinei del profilo</param>
        /// <param name="sigmaFactor">Deviazioni standard che un gradino deve valere</param>
        /// <returns>Operazioni candidate, ancora senza confine raffinato</returns>
        public List<EditOperationCandidate> Detect(IReadOnlyList<OffsetSegment> segments, double sigmaFactor)
        {
            // Un punto di rottura sopravvive solo se il salto supera l'incertezza delle due
            // stime: è un t di Welch, non una soglia di comodo
            List<EditOperationCandidate> result = new List<EditOperationCandidate>();
            for (int i = 0; i + 1 < segments.Count; i++)
            {
                OffsetSegment before = segments[i];
                OffsetSegment after = segments[i + 1];
                double timeMs = (before.EndMs + after.StartMs) / 2.0;
                double jump = after.ValueAt(timeMs) - before.ValueAt(timeMs);
                double sigma = Math.Sqrt(Math.Pow(StandardError(before, timeMs), 2.0) + Math.Pow(StandardError(after, timeMs), 2.0));
                if (Math.Abs(jump) < sigmaFactor * Math.Max(sigma, 1e-6))
                    continue;

                result.Add(new EditOperationCandidate
                {
                    Kind = jump < 0.0 ? EditOperationKind.InsertSilence : EditOperationKind.CutSegment,
                    TimestampMs = timeMs,
                    DurationMs = Math.Abs(jump),
                    OffsetBeforeMs = before.ValueAt(timeMs),
                    OffsetAfterMs = after.ValueAt(timeMs),
                    PlateauEndBeforeMs = before.EndMs,
                    PlateauStartAfterMs = after.StartMs
                });
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Somme cumulate che rendono costante il costo di una regressione su un tratto
        /// </summary>
        /// <param name="times">Istanti dei punti</param>
        /// <param name="values">Offset dei punti</param>
        /// <returns>Sei prefissi cumulati</returns>
        private static double[][] BuildPrefixes(double[] times, double[] values)
        {
            int count = times.Length;
            double[][] result = new double[6][];
            for (int k = 0; k < 6; k++)
                result[k] = new double[count + 1];
            for (int i = 0; i < count; i++)
            {
                result[0][i + 1] = result[0][i] + 1.0;
                result[1][i + 1] = result[1][i] + times[i];
                result[2][i + 1] = result[2][i] + times[i] * times[i];
                result[3][i + 1] = result[3][i] + values[i];
                result[4][i + 1] = result[4][i] + times[i] * values[i];
                result[5][i + 1] = result[5][i] + values[i] * values[i];
            }
            return result;
        }

        /// <summary>
        /// Regressione lineare a pendenza vincolata sul tratto richiesto
        /// </summary>
        /// <param name="prefixes">Somme cumulate</param>
        /// <param name="start">Primo punto incluso</param>
        /// <param name="end">Primo punto escluso</param>
        /// <param name="intercept">Intercetta della retta</param>
        /// <param name="slope">Pendenza della retta</param>
        /// <param name="count">Punti del tratto</param>
        /// <returns>Somma dei residui quadri</returns>
        private static double Fit(double[][] prefixes, int start, int end, out double intercept, out double slope, out int count)
        {
            count = (int)(prefixes[0][end] - prefixes[0][start]);
            intercept = 0.0;
            slope = 0.0;
            if (count < 2)
                return 0.0;

            double sumTime = prefixes[1][end] - prefixes[1][start];
            double sumTimeSquared = prefixes[2][end] - prefixes[2][start];
            double sumValue = prefixes[3][end] - prefixes[3][start];
            double sumTimeValue = prefixes[4][end] - prefixes[4][start];
            double sumValueSquared = prefixes[5][end] - prefixes[5][start];
            double variation = sumTimeSquared - sumTime * sumTime / count;
            if (variation > 1e-9)
            {
                slope = (sumTimeValue - sumTime * sumValue / count) / variation;
                slope = Math.Min(Math.Max(slope, -EditAnalysisProfile.SEGMENT_MAX_SLOPE), EditAnalysisProfile.SEGMENT_MAX_SLOPE);
            }
            intercept = (sumValue - slope * sumTime) / count;
            double residual = sumValueSquared - 2.0 * intercept * sumValue - 2.0 * slope * sumTimeValue +
                count * intercept * intercept + 2.0 * intercept * slope * sumTime + slope * slope * sumTimeSquared;
            return Math.Max(residual, 0.0);
        }

        /// <summary>
        /// Errore standard della retta di un tratto valutata in un istante
        /// </summary>
        /// <param name="segment">Tratto rettilineo</param>
        /// <param name="timeMs">Istante di valutazione</param>
        /// <returns>Errore standard della previsione</returns>
        private static double StandardError(OffsetSegment segment, double timeMs)
        {
            if (segment.PointCount < 3 || segment.TimeVariation <= 1e-9)
                return Math.Max(Math.Sqrt(segment.ResidualVariance), 20.0);
            return Math.Sqrt(segment.ResidualVariance * (1.0 / segment.PointCount + Math.Pow(timeMs - segment.TimeMean, 2.0) / segment.TimeVariation));
        }

        #endregion
    }
}
