using RemuxForge.Core.Analysis.Edit.Extraction;
using System;

namespace RemuxForge.Core.Analysis.Edit.Boundary
{
    /// <summary>
    /// Le due convenzioni sulle run di nero, dove l'hash non sa decidere
    /// </summary>
    internal class BlackRunRules
    {
        #region Metodi pubblici

        /// <summary>
        /// Primo fotogramma della run di nero vicina alla stima, oppure null
        /// </summary>
        /// <param name="signals">Segnali della sorgente</param>
        /// <param name="timestampMs">Confine stimato dall'hash</param>
        /// <returns>Inizio della run oppure null quando qui non c'è nessuna dissolvenza</returns>
        public double? FindRunStart(FrameSignals signals, double timestampMs)
        {
            // Su una run di nero l'operazione sta all'inizio della run: così non serve nessun
            // discorso di equivalenza fra posizioni dentro il nero
            int first = HashOps.LowerBound(signals.PtsMs, timestampMs - EditAnalysisProfile.BLACK_LOOKBEHIND_MS);
            int count = Math.Min(EditAnalysisProfile.BLACK_FRAMES, signals.Count - first);
            if (count < 20)
                return null;

            double minimum = double.MaxValue;
            double maximum = double.MinValue;
            for (int i = 0; i < count; i++)
            {
                double luma = signals.LumaMean[first + i];
                minimum = Math.Min(minimum, luma);
                maximum = Math.Max(maximum, luma);
            }
            if (minimum >= EditAnalysisProfile.BLACK_LUMA || maximum - minimum < EditAnalysisProfile.BLACK_EXCURSION)
                return null;

            // il primo nero non basta: fotogrammi neri isolati alternati a immagine piena
            // precedono a volte la run vera, che comincia dopo di loro
            double? nearestStartMs = null;
            double nearestDistanceMs = double.MaxValue;
            for (int i = 1; i < count - EditAnalysisProfile.BLACK_CONSECUTIVE; i++)
            {
                bool run = true;
                for (int k = 0; k < EditAnalysisProfile.BLACK_CONSECUTIVE && run; k++)
                    run = signals.LumaMean[first + i + k] < EditAnalysisProfile.BLACK_LUMA;
                if (!run || signals.PtsMs[first + i] < timestampMs - EditAnalysisProfile.BLACK_LOOKBEHIND_MS)
                    continue;
                double startMs = signals.PtsMs[first + i];
                double distanceMs = Math.Abs(startMs - timestampMs);
                if (distanceMs <= EditAnalysisProfile.BLACK_LOOKBEHIND_MS && distanceMs < nearestDistanceMs)
                {
                    nearestStartMs = startMs;
                    nearestDistanceMs = distanceMs;
                }

                while (i + 1 < count && signals.LumaMean[first + i + 1] < EditAnalysisProfile.BLACK_LUMA)
                    i++;
            }

            return nearestStartMs;
        }

        #endregion
    }
}
