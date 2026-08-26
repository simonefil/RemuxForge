using RemuxForge.Core.Analysis.Edit.Extraction;
using System;

namespace RemuxForge.Core.Analysis.Edit.Boundary
{
    /// <summary>
    /// Dentro la run scura il confine non può precedere il punto in cui l'audio smette di dare ragione all'offset di sinistra
    /// </summary>
    internal class AudioBlackBoundary
    {
        #region Metodi pubblici

        /// <summary>
        /// Arretra il confine all'inizio della run scura, senza superare la rottura dell'audio
        /// </summary>
        /// <param name="signals">Segnali della sorgente</param>
        /// <param name="envelopes">Inviluppi audio sulla griglia della sorgente</param>
        /// <param name="timestampMs">Confine proposto dal changepoint</param>
        /// <param name="offsetBeforeMs">Offset del pianoro precedente</param>
        /// <returns>Confine corretto</returns>
        public double Resolve(FrameSignals signals, AudioEnvelopePair envelopes, double timestampMs, double offsetBeforeMs)
        {
            // Nero contro nero il video non decide. L'eccezione la vede solo l'audio: se in mezzo
            // alla run una delle due tracce riparte mentre l'altra tace, lì l'offset di sinistra
            // ha già smesso di spiegare
            double runStartMs = this.DarkRunStart(signals, timestampMs);
            if (runStartMs >= timestampMs - 1.0)
                return timestampMs;
            if (envelopes == null)
                return runStartMs;

            // la rottura può cadere a ridosso del punto proposto: serve spazio oltre, per la tenuta
            double? breakMs = this.FindAudioBreak(envelopes, runStartMs, timestampMs + EditAnalysisProfile.AUDIO_HOLD_SAMPLES * AudioEnvelopeExtractor.STEP_MS, offsetBeforeMs);
            if (!breakMs.HasValue)
                return runStartMs;
            return Math.Min(Math.Max(runStartMs, breakMs.Value), timestampMs);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Primo fotogramma della run scura contigua che contiene l'istante
        /// </summary>
        /// <param name="signals">Segnali della sorgente</param>
        /// <param name="timestampMs">Istante da collocare</param>
        /// <returns>Inizio della run scura oppure l'istante stesso</returns>
        private double DarkRunStart(FrameSignals signals, double timestampMs)
        {
            int index = HashOps.LowerBound(signals.PtsMs, timestampMs);
            if (index < 0)
                index = 0;
            if (index > signals.Count - 1)
                index = signals.Count - 1;
            if (signals.ThumbStd[index] >= EditAnalysisProfile.DARK_STD)
                return timestampMs;

            int from = index;
            while (from > 0 && signals.ThumbStd[from - 1] < EditAnalysisProfile.DARK_STD)
                from--;
            return signals.PtsMs[from];
        }

        /// <summary>
        /// Primo istante in cui il pattern di silenzio dice che l'offset di sinistra non spiega più
        /// </summary>
        /// <param name="envelopes">Inviluppi audio</param>
        /// <param name="startMs">Inizio dell'intervallo da esaminare</param>
        /// <param name="endMs">Fine dell'intervallo da esaminare</param>
        /// <param name="offsetBeforeMs">Offset del pianoro precedente</param>
        /// <returns>Istante della rottura oppure null</returns>
        private double? FindAudioBreak(AudioEnvelopePair envelopes, double startMs, double endMs, double offsetBeforeMs)
        {
            double sourceFloor = AudioEnvelopePair.Percentile(envelopes.Source, 5.0) + EditAnalysisProfile.AUDIO_MUTE_MARGIN_DB;
            float[] present = Array.FindAll(envelopes.Language, value => value > AudioEnvelopePair.SILENCE_FLOOR_DB + 1.0);
            if (present.Length == 0)
                return null;
            double languageFloor = AudioEnvelopePair.Percentile(present, 5.0) + EditAnalysisProfile.AUDIO_MUTE_MARGIN_DB;

            int count = (int)Math.Ceiling((endMs - startMs) / AudioEnvelopeExtractor.STEP_MS);
            if (count < EditAnalysisProfile.AUDIO_HOLD_SAMPLES)
                return null;

            bool[] violation = new bool[count];
            int valid = 0;
            for (int k = 0; k < count; k++)
            {
                double timeMs = startMs + k * AudioEnvelopeExtractor.STEP_MS;
                int sourceIndex = envelopes.IndexOf(timeMs);
                int languageIndex = envelopes.IndexOf(timeMs + offsetBeforeMs);
                if (sourceIndex < 0 || sourceIndex >= envelopes.SourcePooled.Length || languageIndex < 0 || languageIndex >= envelopes.LanguagePooled.Length)
                    continue;
                valid++;
                violation[k] = (envelopes.SourcePooled[sourceIndex] < sourceFloor) != (envelopes.LanguagePooled[languageIndex] < languageFloor);
            }
            if (valid < EditAnalysisProfile.AUDIO_HOLD_SAMPLES)
                return null;

            int run = 0;
            for (int k = 0; k < count; k++)
            {
                run = violation[k] ? run + 1 : 0;
                if (run >= EditAnalysisProfile.AUDIO_HOLD_SAMPLES)
                    return startMs + (k - EditAnalysisProfile.AUDIO_HOLD_SAMPLES + 1) * AudioEnvelopeExtractor.STEP_MS;
            }

            return null;
        }

        #endregion
    }
}
