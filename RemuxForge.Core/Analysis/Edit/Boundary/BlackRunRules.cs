using RemuxForge.Core.Analysis.Edit.Extraction;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Edit.Boundary
{
    /// <summary>
    /// Le due convenzioni sulle run di nero, dove l'hash non sa decidere
    /// </summary>
    internal class BlackRunRules
    {
        #region Costanti

        /// <summary>
        /// Scarto entro cui due run di nero spiegano il salto altrettanto bene
        /// </summary>
        private const double RUN_MATCH_TOLERANCE_MS = 40.0;

        #endregion

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

        /// <summary>
        /// Inizio della run di nero che spiega il salto fra le posizioni giudicate equivalenti dai fotogrammi
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="envelopes">Inviluppi audio oppure null quando l'audio non è disponibile</param>
        /// <param name="startMs">Prima posizione equivalente</param>
        /// <param name="endMs">Ultima posizione equivalente</param>
        /// <param name="offsetBeforeMs">Offset del pianoro precedente</param>
        /// <param name="offsetAfterMs">Offset del pianoro successivo</param>
        /// <param name="durationMs">Durata dell'operazione da spiegare</param>
        /// <returns>Inizio della run scelta oppure null quando qui non c'è nessun nero</returns>
        public double? FindRunStartInRange(PairSignals pair, AudioEnvelopePair envelopes, double startMs, double endMs,
            double offsetBeforeMs, double offsetAfterMs, double durationMs)
        {
            // Dove il costo non distingue una posizione dall'altra la run di nero decide da sola:
            // il confine è il suo primo fotogramma, non l'estremo del tratto ambiguo
            List<BlackRun> candidates = this.CollectRuns(pair.Source, startMs, endMs);
            if (candidates.Count == 0)
                return null;
            if (candidates.Count == 1)
                return candidates[0].StartMs;

            // Più neri nello stesso tratto: vince quello la cui differenza di durata fra le due
            // copie si avvicina di più al salto misurato
            double bestScore = double.MaxValue;
            foreach (BlackRun candidate in candidates)
            {
                double score = this.Score(pair.Language, candidate, offsetBeforeMs, offsetAfterMs, durationMs);
                if (score < bestScore)
                    bestScore = score;
            }

            List<BlackRun> tied = new List<BlackRun>();
            foreach (BlackRun candidate in candidates)
            {
                if (this.Score(pair.Language, candidate, offsetBeforeMs, offsetAfterMs, durationMs) <= bestScore + RUN_MATCH_TOLERANCE_MS)
                    tied.Add(candidate);
            }
            if (tied.Count == 1 || envelopes == null)
                return tied[0].StartMs;

            // Le durate non discriminano: si guarda dove tacciono entrambe le tracce
            foreach (BlackRun candidate in tied)
            {
                if (this.MutedInBoth(envelopes, candidate, offsetBeforeMs))
                    return candidate.StartMs;
            }
            return tied[0].StartMs;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Run di fotogrammi neri contigui comprese fra due istanti
        /// </summary>
        /// <param name="signals">Segnali della traccia</param>
        /// <param name="startMs">Inizio dell'intervallo</param>
        /// <param name="endMs">Fine dell'intervallo</param>
        /// <returns>Run trovate in ordine di tempo</returns>
        private List<BlackRun> CollectRuns(FrameSignals signals, double startMs, double endMs)
        {
            List<BlackRun> result = new List<BlackRun>();
            int index = Math.Max(0, HashOps.LowerBound(signals.PtsMs, startMs));
            int last = Math.Min(signals.Count, HashOps.LowerBound(signals.PtsMs, endMs) + 1);
            while (index < last)
            {
                if (signals.LumaMean[index] >= EditAnalysisProfile.BLACK_LUMA)
                {
                    index++;
                    continue;
                }
                int runEnd = index;
                while (runEnd + 1 < last && signals.LumaMean[runEnd + 1] < EditAnalysisProfile.BLACK_LUMA)
                    runEnd++;
                if (runEnd - index + 1 >= EditAnalysisProfile.BLACK_CONSECUTIVE)
                    result.Add(new BlackRun { StartMs = signals.PtsMs[index], EndMs = signals.PtsMs[runEnd] });
                index = runEnd + 1;
            }
            return result;
        }

        /// <summary>
        /// Quanto la run si discosta dal salto che deve spiegare
        /// </summary>
        /// <param name="language">Segnali della copia doppiata</param>
        /// <param name="run">Run della sorgente da valutare</param>
        /// <param name="offsetBeforeMs">Offset del pianoro precedente</param>
        /// <param name="offsetAfterMs">Offset del pianoro successivo</param>
        /// <param name="durationMs">Durata dell'operazione da spiegare</param>
        /// <returns>Scarto in millisecondi, tanto minore quanto meglio la run spiega il salto</returns>
        private double Score(FrameSignals language, BlackRun run, double offsetBeforeMs, double offsetAfterMs, double durationMs)
        {
            // La run corrispondente si cerca fra i due offset, perché il salto cade proprio dentro
            // il nero e nessuno dei due la colloca da solo
            double fromMs = run.StartMs + Math.Min(offsetBeforeMs, offsetAfterMs);
            double toMs = run.EndMs + Math.Max(offsetBeforeMs, offsetAfterMs);
            List<BlackRun> matches = this.CollectRuns(language, fromMs, toMs);
            double languageDurationMs = 0.0;
            foreach (BlackRun match in matches)
            {
                double candidateMs = match.EndMs - match.StartMs;
                if (candidateMs > languageDurationMs)
                    languageDurationMs = candidateMs;
            }
            return Math.Abs(run.EndMs - run.StartMs - languageDurationMs - durationMs);
        }

        /// <summary>
        /// Se lungo tutta la run tacciono sia la sorgente sia la copia doppiata
        /// </summary>
        /// <param name="envelopes">Inviluppi audio</param>
        /// <param name="run">Run della sorgente da esaminare</param>
        /// <param name="offsetMs">Offset con cui leggere la copia doppiata</param>
        /// <returns>Vero quando nessuna delle due tracce supera il proprio silenzio</returns>
        private bool MutedInBoth(AudioEnvelopePair envelopes, BlackRun run, double offsetMs)
        {
            double sourceFloor = AudioEnvelopePair.Percentile(envelopes.Source, 5.0) + EditAnalysisProfile.AUDIO_MUTE_MARGIN_DB;
            float[] present = Array.FindAll(envelopes.Language, value => value > AudioEnvelopePair.SILENCE_FLOOR_DB + 1.0);
            if (present.Length == 0)
                return false;
            double languageFloor = AudioEnvelopePair.Percentile(present, 5.0) + EditAnalysisProfile.AUDIO_MUTE_MARGIN_DB;

            int count = (int)Math.Ceiling((run.EndMs - run.StartMs) / AudioEnvelopeExtractor.STEP_MS);
            for (int k = 0; k <= count; k++)
            {
                double timeMs = run.StartMs + k * AudioEnvelopeExtractor.STEP_MS;
                int sourceIndex = envelopes.IndexOf(timeMs);
                int languageIndex = envelopes.IndexOf(timeMs + offsetMs);
                if (sourceIndex < 0 || sourceIndex >= envelopes.SourcePooled.Length || languageIndex < 0 || languageIndex >= envelopes.LanguagePooled.Length)
                    return false;
                if (envelopes.SourcePooled[sourceIndex] >= sourceFloor || envelopes.LanguagePooled[languageIndex] >= languageFloor)
                    return false;
            }
            return true;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Run di fotogrammi neri contigui
        /// </summary>
        private class BlackRun
        {
            /// <summary>
            /// Primo fotogramma della run
            /// </summary>
            public double StartMs { get; set; }

            /// <summary>
            /// Ultimo fotogramma della run
            /// </summary>
            public double EndMs { get; set; }
        }

        #endregion
    }
}
