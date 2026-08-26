using RemuxForge.Core.Analysis.Edit.Extraction;
using System;

namespace RemuxForge.Core.Analysis.Edit.Judgement
{
    /// <summary>
    /// Un'operazione vera sposta anche l'audio: se non lo sposta, non è un'operazione
    /// </summary>
    internal class AudioStepJudge
    {
        #region Variabili di istanza

        /// <summary>
        /// Inviluppi sulla griglia comune, con la copia doppiata già riportata al livello della sorgente
        /// </summary>
        private AudioEnvelopePair _envelopes;

        /// <summary>
        /// Energia della copia doppiata compensata del divario di livello
        /// </summary>
        private float[] _language;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="envelopes">Inviluppi audio della coppia</param>
        public AudioStepJudge(AudioEnvelopePair envelopes)
        {
            this._envelopes = envelopes;
            if (envelopes == null)
                return;

            float[] present = Array.FindAll(envelopes.Language, value => value > AudioEnvelopePair.SILENCE_FLOOR_DB + 1.0);
            if (present.Length == 0)
            {
                this._language = envelopes.Language;
                return;
            }
            double shift = AudioEnvelopePair.Percentile(envelopes.Source, 50.0) - AudioEnvelopePair.Percentile(present, 50.0);
            this._language = new float[envelopes.Language.Length];
            for (int i = 0; i < this._language.Length; i++)
                this._language[i] = (float)(envelopes.Language[i] + shift);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// False solo quando l'audio si misura e dice che quel salto lui non ce l'ha
        /// </summary>
        /// <param name="operation">Operazione da giudicare</param>
        /// <returns>True quando l'audio conferma o si astiene</returns>
        public bool Holds(EditOperationCandidate operation)
        {
            // Si astiene sulla metà dei casi ed è voluto: due finestre per lato devono dare
            // lo stesso offset, altrimenti l'aggancio non è affidabile e la prova tace
            double jump = operation.OffsetAfterMs - operation.OffsetBeforeMs;
            if (this._envelopes == null || Math.Abs(jump) < 1.0)
                return true;

            double? before = this.PlateauOffset(operation.TimestampMs - EditAnalysisProfile.AUDIO_GUARD_MS - EditAnalysisProfile.AUDIO_WINDOW_MS, operation.OffsetBeforeMs);
            double? after = this.PlateauOffset(operation.TimestampMs + EditAnalysisProfile.AUDIO_GUARD_MS + EditAnalysisProfile.AUDIO_WINDOW_MS / 2.0, operation.OffsetAfterMs);
            if (!before.HasValue || !after.HasValue)
                return true;

            return Math.Abs((after.Value - before.Value) / jump) >= EditAnalysisProfile.AUDIO_MIN_STEP_RATIO;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// L'offset audio di un pianoro, solo se due finestre sfalsate sono d'accordo
        /// </summary>
        /// <param name="startMs">Inizio della prima finestra</param>
        /// <param name="centerMs">Offset video da cui partire a cercare</param>
        /// <returns>Offset audio del pianoro oppure null</returns>
        private double? PlateauOffset(double startMs, double centerMs)
        {
            double? first = this.Correlate(startMs, centerMs);
            double? second = this.Correlate(startMs - EditAnalysisProfile.AUDIO_WINDOW_MS / 2.0, centerMs);
            if (!first.HasValue || !second.HasValue || Math.Abs(first.Value - second.Value) > EditAnalysisProfile.AUDIO_AGREEMENT_MS)
                return null;
            return (first.Value + second.Value) / 2.0;
        }

        /// <summary>
        /// Ritardo che massimizza la correlazione normalizzata fra le due finestre di inviluppo
        /// </summary>
        /// <param name="startMs">Inizio della finestra sulla sorgente</param>
        /// <param name="centerMs">Centro della scansione dei ritardi</param>
        /// <returns>Ritardo migliore oppure null quando la finestra esce dal materiale</returns>
        private double? Correlate(double startMs, double centerMs)
        {
            float[] source = this._envelopes.Source;
            int length = (int)(EditAnalysisProfile.AUDIO_WINDOW_MS / AudioEnvelopeExtractor.STEP_MS);
            int sourceIndex = this._envelopes.IndexOf(startMs);
            if (sourceIndex < 0 || sourceIndex + length > source.Length)
                return null;

            double sourceMean = 0.0;
            for (int i = 0; i < length; i++)
                sourceMean += source[sourceIndex + i];
            sourceMean /= length;
            double sourceEnergy = 0.0;
            for (int i = 0; i < length; i++)
                sourceEnergy += (source[sourceIndex + i] - sourceMean) * (source[sourceIndex + i] - sourceMean);

            double? result = null;
            double best = -9.0;
            int lagCount = (int)Math.Ceiling(2.0 * EditAnalysisProfile.AUDIO_SCAN_RADIUS_MS / EditAnalysisProfile.AUDIO_SCAN_STEP_MS);
            for (int k = 0; k < lagCount; k++)
            {
                double lagMs = centerMs - EditAnalysisProfile.AUDIO_SCAN_RADIUS_MS + k * EditAnalysisProfile.AUDIO_SCAN_STEP_MS;
                int languageIndex = this._envelopes.IndexOf(startMs + lagMs);
                if (languageIndex < 0 || languageIndex + length > this._language.Length)
                    continue;

                double languageMean = 0.0;
                for (int i = 0; i < length; i++)
                    languageMean += this._language[languageIndex + i];
                languageMean /= length;
                double cross = 0.0;
                double languageEnergy = 0.0;
                for (int i = 0; i < length; i++)
                {
                    double left = source[sourceIndex + i] - sourceMean;
                    double right = this._language[languageIndex + i] - languageMean;
                    cross += left * right;
                    languageEnergy += right * right;
                }

                double norm = Math.Sqrt(sourceEnergy * languageEnergy);
                double correlation = norm > 0.0 ? cross / norm : 0.0;
                if (correlation > best)
                {
                    best = correlation;
                    result = lagMs;
                }
            }

            return result;
        }

        #endregion
    }
}
