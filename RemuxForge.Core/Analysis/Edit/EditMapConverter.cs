using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Edit
{
    /// <summary>
    /// Traduce le operazioni misurate nella timeline language nativa attesa dalla EditMap
    /// </summary>
    internal class EditMapConverter
    {
        #region Costanti

        /// <summary>
        /// Durata sotto la quale un'operazione di testa o di coda non vale la pena di essere scritta
        /// </summary>
        private const double MINIMUM_EDGE_MS = 40.0;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Compone la EditMap a partire dall'esito dell'analisi
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="outcome">Operazioni, offset iniziale e copertura</param>
        /// <param name="stretchFactor">Fattore di stretch serializzato</param>
        /// <returns>EditMap con InitialDelayMs sempre zero</returns>
        public EditMap Convert(PairSignals pair, EditAnalysisOutcome outcome, string stretchFactor)
        {
            EditMap result = new EditMap();
            result.InitialDelayMs = 0;
            result.StretchFactor = stretchFactor ?? "";

            double stretch = pair.Stretch;
            double offsetMs = outcome.InitialOffsetMs;
            if (Math.Abs(offsetMs) >= MINIMUM_EDGE_MS)
            {
                // L'offset del primo tratto è materiale di troppo in una delle due copie, e va
                // materializzato come operazione: la EditMap non porta delay di container
                result.Operations.Add(new EditOperation
                {
                    Type = offsetMs < 0.0 ? EditOperation.INSERT_SILENCE : EditOperation.CUT_SEGMENT,
                    LangTimestampMs = 0,
                    DurationMs = RoundPositive(Math.Abs(offsetMs) / stretch),
                    SourceTimestampMs = 0,
                    VisualSourceTimestampMs = 0,
                    Scope = EditOperation.SCOPE_HEAD
                });
            }

            foreach (EditOperationCandidate operation in outcome.Operations)
            {
                int boundaryMs = (int)Math.Round(operation.TimestampMs, MidpointRounding.AwayFromZero);
                result.Operations.Add(new EditOperation
                {
                    Type = operation.Kind == EditOperationKind.InsertSilence ? EditOperation.INSERT_SILENCE : EditOperation.CUT_SEGMENT,
                    LangTimestampMs = (int)Math.Round((operation.TimestampMs + offsetMs) / stretch, MidpointRounding.AwayFromZero),
                    DurationMs = RoundPositive(operation.DurationMs / stretch),
                    SourceTimestampMs = boundaryMs,
                    VisualSourceTimestampMs = boundaryMs,
                    Scope = EditOperation.SCOPE_BODY
                });
                offsetMs += operation.Kind == EditOperationKind.InsertSilence ? -operation.DurationMs : operation.DurationMs;
            }

            double sourceEndMs = EndOfContentMs(pair.Source.PtsMs);
            double languageEndMs = EndOfContentMs(pair.LanguagePtsMs);
            double tailMs = sourceEndMs - (languageEndMs - offsetMs);
            if (Math.Abs(tailMs) >= MINIMUM_EDGE_MS)
            {
                double boundaryMs = Math.Min(sourceEndMs + offsetMs, languageEndMs);
                result.Operations.Add(new EditOperation
                {
                    Type = tailMs > 0.0 ? EditOperation.INSERT_SILENCE : EditOperation.CUT_SEGMENT,
                    LangTimestampMs = (int)Math.Round(boundaryMs / stretch, MidpointRounding.AwayFromZero),
                    DurationMs = RoundPositive(Math.Abs(tailMs) / stretch),
                    SourceTimestampMs = (int)Math.Round(boundaryMs - offsetMs, MidpointRounding.AwayFromZero),
                    VisualSourceTimestampMs = (int)Math.Round(boundaryMs - offsetMs, MidpointRounding.AwayFromZero),
                    Scope = EditOperation.SCOPE_TAIL
                });
            }

            return result;
        }

        /// <summary>
        /// I tratti a offset costante descritti dalle operazioni accettate
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="outcome">Esito dell'analisi</param>
        /// <returns>Pianori nella timeline source</returns>
        public List<DeepAnalysisPlateau> BuildPlateaus(PairSignals pair, EditAnalysisOutcome outcome)
        {
            List<DeepAnalysisPlateau> result = new List<DeepAnalysisPlateau>();
            double offsetMs = outcome.InitialOffsetMs;
            double startMs = pair.Source.PtsMs[0];
            foreach (EditOperationCandidate operation in outcome.Operations)
            {
                result.Add(new DeepAnalysisPlateau { StartMs = startMs, EndMs = operation.TimestampMs, OffsetMs = offsetMs });
                offsetMs += operation.Kind == EditOperationKind.InsertSilence ? -operation.DurationMs : operation.DurationMs;
                startMs = operation.ResumeMs;
            }
            result.Add(new DeepAnalysisPlateau { StartMs = startMs, EndMs = pair.Source.PtsMs[pair.Source.Count - 1], OffsetMs = offsetMs });
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Istante in cui finisce il contenuto, dedotto dai PTS reali
        /// </summary>
        /// <param name="timestamps">PTS crescenti della traccia</param>
        /// <returns>Fine del contenuto in millisecondi</returns>
        private static double EndOfContentMs(double[] timestamps)
        {
            // Se un file è troncato il contenuto finisce dove finiscono i frame, non dove dice l'header
            int count = Math.Min(200, timestamps.Length - 1);
            if (count <= 0)
                return timestamps[timestamps.Length - 1];
            double[] steps = new double[count];
            for (int i = 0; i < count; i++)
                steps[i] = timestamps[timestamps.Length - count + i] - timestamps[timestamps.Length - count + i - 1];
            Array.Sort(steps);
            int middle = steps.Length / 2;
            double step = steps.Length % 2 == 1 ? steps[middle] : (steps[middle - 1] + steps[middle]) / 2.0;
            return timestamps[timestamps.Length - 1] + step;
        }

        /// <summary>
        /// Arrotonda una durata mantenendola positiva
        /// </summary>
        /// <param name="durationMs">Durata in millisecondi</param>
        /// <returns>Durata intera non nulla</returns>
        private static int RoundPositive(double durationMs)
        {
            return Math.Max(1, (int)Math.Round(durationMs, MidpointRounding.AwayFromZero));
        }

        #endregion
    }
}
