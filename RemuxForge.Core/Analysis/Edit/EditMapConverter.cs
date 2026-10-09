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
        /// Durata sotto la quale un'operazione di testa o di coda si arrotonda a zero millisecondi.
        /// Sopra va scritta sempre: la testa omessa sposta l'audio importato, e la coda omessa lascia
        /// all'editor una durata che non torna con il Source
        /// </summary>
        private const double MINIMUM_EDGE_MS = 0.5;

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
            result.LanguageAudioOffsetMs = outcome.AudioOffset != null && outcome.AudioOffset.Accepted ? outcome.AudioOffset.LanguageOffsetMs : 0;
            result.StretchFactor = stretchFactor ?? "";

            double stretch = pair.Stretch;
            double offsetMs = outcome.InitialOffsetMs;
            double sourceEndMs = EndOfContentMs(pair.Source.PtsMs);
            double languageEndMs = EndOfContentMs(pair.LanguagePtsMs);
            // L'editor rilegge la mappa in millisecondi interi: il Source di ogni confine è
            // l'istante Language più le durate già scritte, non l'offset misurato in double.
            // shiftMs è quella somma firmata nella timeline Language (insert +, cut -), shiftSourceMs
            // la stessa somma portata nel Source con lo stretch, accumulata come la accumula l'editor
            int shiftMs = 0;
            double shiftSourceMs = 0.0;
            double skippedHeadMs = 0.0;
            if (Math.Abs(offsetMs) / stretch >= MINIMUM_EDGE_MS)
            {
                // L'offset del primo tratto è materiale di troppo in una delle due copie, e va
                // materializzato come operazione: la EditMap non porta delay di container
                int headMs = RoundPositive(Math.Abs(offsetMs) / stretch);
                result.Operations.Add(new EditOperation
                {
                    Type = offsetMs < 0.0 ? EditOperation.INSERT_SILENCE : EditOperation.CUT_SEGMENT,
                    LangTimestampMs = 0,
                    DurationMs = headMs,
                    SourceTimestampMs = 0,
                    VisualSourceTimestampMs = 0,
                    Scope = EditOperation.SCOPE_HEAD
                });
                shiftMs = offsetMs < 0.0 ? headMs : -headMs;
                shiftSourceMs += offsetMs < 0.0 ? headMs * stretch : -headMs * stretch;
            }
            else
            {
                skippedHeadMs = offsetMs;
            }

            for (int i = 0; i < outcome.Operations.Count; i++)
            {
                EditOperationCandidate operation = outcome.Operations[i];
                int languageTimestampMs = (int)Math.Round((operation.TimestampMs + offsetMs) / stretch, MidpointRounding.AwayFromZero);
                // La durata è la differenza fra offset cumulati arrotondati: arrotondare ogni
                // durata per conto suo farebbe derivare la mappa di mezzo millisecondo a operazione
                double offsetAfterMs = offsetMs + (operation.Kind == EditOperationKind.InsertSilence ? -operation.DurationMs : operation.DurationMs);
                int targetShiftMs = -(int)Math.Round((offsetAfterMs - skippedHeadMs) / stretch, MidpointRounding.AwayFromZero);
                int durationMs = Math.Max(1, Math.Abs(targetShiftMs - shiftMs));
                double appliedDurationMs = operation.DurationMs;
                if (i == outcome.Operations.Count - 1 && operation.Kind == EditOperationKind.CutSegment)
                {
                    int availableLanguageMs = Math.Max(0, (int)Math.Floor(languageEndMs / stretch - languageTimestampMs));
                    if (durationMs > availableLanguageMs)
                    {
                        durationMs = availableLanguageMs;
                        appliedDurationMs = durationMs * stretch;
                    }
                }
                if (durationMs <= 0)
                    continue;
                int boundaryMs = (int)Math.Round(languageTimestampMs * stretch + shiftSourceMs, MidpointRounding.AwayFromZero);
                result.Operations.Add(new EditOperation
                {
                    Type = operation.Kind == EditOperationKind.InsertSilence ? EditOperation.INSERT_SILENCE : EditOperation.CUT_SEGMENT,
                    LangTimestampMs = languageTimestampMs,
                    DurationMs = durationMs,
                    SourceTimestampMs = boundaryMs,
                    VisualSourceTimestampMs = boundaryMs,
                    Scope = EditOperation.SCOPE_BODY
                });
                offsetMs += operation.Kind == EditOperationKind.InsertSilence ? -appliedDurationMs : appliedDurationMs;
                shiftMs += operation.Kind == EditOperationKind.InsertSilence ? durationMs : -durationMs;
                shiftSourceMs += operation.Kind == EditOperationKind.InsertSilence ? durationMs * stretch : -durationMs * stretch;
            }

            // La coda chiude la durata che l'editor ricompone dalle durate scritte, non dall'offset misurato
            double tailMs = sourceEndMs - languageEndMs - shiftMs * stretch;
            if (Math.Abs(tailMs) / stretch >= MINIMUM_EDGE_MS)
            {
                // Arrotondare in su la fine Language porterebbe l'istante oltre l'ultimo fotogramma
                int languageLimitMs = (int)Math.Floor(languageEndMs / stretch);
                int languageTimestampMs = Math.Min(languageLimitMs, (int)Math.Round(Math.Min(sourceEndMs + offsetMs, languageEndMs) / stretch, MidpointRounding.AwayFromZero));
                int durationMs = RoundPositive(Math.Abs(tailMs) / stretch);
                if (tailMs < 0.0)
                    durationMs = Math.Min(durationMs, languageLimitMs - languageTimestampMs);
                if (durationMs > 0)
                {
                    int boundaryMs = (int)Math.Round(languageTimestampMs * stretch + shiftSourceMs, MidpointRounding.AwayFromZero);
                    result.Operations.Add(new EditOperation
                    {
                        Type = tailMs > 0.0 ? EditOperation.INSERT_SILENCE : EditOperation.CUT_SEGMENT,
                        LangTimestampMs = languageTimestampMs,
                        DurationMs = durationMs,
                        SourceTimestampMs = boundaryMs,
                        VisualSourceTimestampMs = boundaryMs,
                        Scope = EditOperation.SCOPE_TAIL
                    });
                }
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
