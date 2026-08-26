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
            for (int i = 1; i < count - EditAnalysisProfile.BLACK_CONSECUTIVE; i++)
            {
                bool run = true;
                for (int k = 0; k < EditAnalysisProfile.BLACK_CONSECUTIVE && run; k++)
                    run = signals.LumaMean[first + i + k] < EditAnalysisProfile.BLACK_LUMA;
                if (!run || signals.PtsMs[first + i] < timestampMs - EditAnalysisProfile.BLACK_LOOKBEHIND_MS)
                    continue;
                double startMs = signals.PtsMs[first + i];
                return Math.Abs(startMs - timestampMs) <= EditAnalysisProfile.BLACK_NEAR_MS ? startMs : (double?)null;
            }

            return null;
        }

        /// <summary>
        /// Corregge il confine perché l'operazione non sbordi oltre la fine della run di nero
        /// </summary>
        /// <param name="signals">Segnali della sorgente</param>
        /// <param name="timestampMs">Confine proposto</param>
        /// <param name="durationMs">Durata dell'operazione</param>
        /// <param name="movedToRunEnd">True quando ha comandato la fine della run</param>
        /// <returns>Confine corretto</returns>
        public double Contain(FrameSignals signals, double timestampMs, double durationMs, out bool movedToRunEnd)
        {
            // Si taglia in nero a tutti e due i capi: finché la run è più lunga dell'operazione
            // i due capi ci stanno dentro e comanda l'inizio; quando non ci sta, e oltre la fine
            // non c'è un'altra run che raccolga l'altro capo, comanda la fine
            movedToRunEnd = false;
            if (!this.TryFindRun(signals, timestampMs, out double runEndMs))
                return timestampMs;
            if (timestampMs + durationMs <= runEndMs || timestampMs + durationMs > runEndMs + EditAnalysisProfile.BLACK_OVERHANG_MS)
                return timestampMs;
            if (this.TryFindRun(signals, timestampMs + durationMs, out _))
                return timestampMs;

            movedToRunEnd = true;
            return runEndMs - durationMs;
        }

        /// <summary>
        /// Estremi della run di nero contigua che contiene l'istante richiesto
        /// </summary>
        /// <param name="signals">Segnali della sorgente</param>
        /// <param name="timestampMs">Istante da collocare</param>
        /// <param name="endMs">Fine della run</param>
        /// <returns>True quando l'istante cade dentro una run vera</returns>
        public bool TryFindRun(FrameSignals signals, double timestampMs, out double endMs)
        {
            // Servono fotogrammi neri consecutivi: una luminanza che alterna piena e zero
            // è interlacciamento, non una run
            endMs = 0.0;
            int index = HashOps.LowerBound(signals.PtsMs, timestampMs);
            if (index >= signals.Count || signals.LumaMean[index] >= EditAnalysisProfile.BLACK_LUMA)
                return false;

            int from = index;
            while (from > 0 && signals.LumaMean[from - 1] < EditAnalysisProfile.BLACK_LUMA)
                from--;
            int to = index;
            while (to + 1 < signals.Count && signals.LumaMean[to + 1] < EditAnalysisProfile.BLACK_LUMA)
                to++;
            if (to - from + 1 < EditAnalysisProfile.BLACK_CONSECUTIVE)
                return false;

            endMs = signals.PtsMs[to];
            return true;
        }

        #endregion
    }
}
