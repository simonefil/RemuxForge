using System;

namespace RemuxForge.Core.Analysis.Edit.Extraction
{
    /// <summary>
    /// Le due tracce audio portate sulla stessa griglia assoluta della sorgente
    /// </summary>
    internal class AudioEnvelopePair
    {
        #region Costanti

        /// <summary>
        /// Campioni su cui si prende il massimo locale: 200 ms di energia di picco
        /// </summary>
        private const int POOL = 20;

        /// <summary>
        /// dB sotto cui il campione della copia doppiata è fuori dal materiale interpolato
        /// </summary>
        public const double SILENCE_FLOOR_DB = -140.0;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore che ricampiona la copia doppiata sulla griglia della sorgente
        /// </summary>
        /// <param name="source">Inviluppo della sorgente</param>
        /// <param name="language">Inviluppo della copia doppiata</param>
        /// <param name="stretch">Fattore di stretch della copia doppiata</param>
        public AudioEnvelopePair(AudioEnvelope source, AudioEnvelope language, double stretch)
        {
            this.OriginMs = source.OriginMs;
            this.Source = source.Decibel;
            this.Language = new float[this.Source.Length];

            double step = AudioEnvelopeExtractor.STEP_MS;
            double languageOrigin = language.OriginMs * stretch;
            double languageStep = step * stretch;
            for (int i = 0; i < this.Language.Length; i++)
            {
                double position = (this.OriginMs + i * step - languageOrigin) / languageStep;
                int left = (int)Math.Floor(position);
                if (left < 0 || left >= language.Count - 1)
                {
                    this.Language[i] = left == language.Count - 1 && position == left ? language.Decibel[left] : (float)SILENCE_FLOOR_DB;
                    continue;
                }
                double fraction = position - left;
                this.Language[i] = (float)(language.Decibel[left] * (1.0 - fraction) + language.Decibel[left + 1] * fraction);
            }

            this.SourcePooled = Pool(this.Source);
            this.LanguagePooled = Pool(this.Language);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Istante del primo campione della griglia comune
        /// </summary>
        public double OriginMs { get; private set; }

        /// <summary>
        /// Energia in dB della sorgente
        /// </summary>
        public float[] Source { get; private set; }

        /// <summary>
        /// Energia in dB della copia doppiata sulla griglia della sorgente
        /// </summary>
        public float[] Language { get; private set; }

        /// <summary>
        /// Energia di picco locale della sorgente
        /// </summary>
        public float[] SourcePooled { get; private set; }

        /// <summary>
        /// Energia di picco locale della copia doppiata
        /// </summary>
        public float[] LanguagePooled { get; private set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Indice del campione corrispondente a un istante assoluto
        /// </summary>
        /// <param name="timeMs">Istante assoluto in millisecondi</param>
        /// <returns>Indice nella griglia comune, anche fuori dai limiti</returns>
        public int IndexOf(double timeMs)
        {
            return (int)((timeMs - this.OriginMs) / AudioEnvelopeExtractor.STEP_MS);
        }

        /// <summary>
        /// Percentile lineare di una sequenza di valori
        /// </summary>
        /// <param name="values">Valori da ordinare</param>
        /// <param name="percent">Percentile richiesto fra 0 e 100</param>
        /// <returns>Valore interpolato</returns>
        public static double Percentile(float[] values, double percent)
        {
            float[] sorted = (float[])values.Clone();
            Array.Sort(sorted);
            double position = percent / 100.0 * (sorted.Length - 1);
            int left = (int)Math.Floor(position);
            int right = Math.Min(left + 1, sorted.Length - 1);
            double fraction = position - left;
            return sorted[left] * (1.0 - fraction) + sorted[right] * fraction;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Massimo scorrevole centrato: l'energia di picco locale, insensibile ai microscarti
        /// </summary>
        /// <param name="values">Inviluppo di partenza</param>
        /// <returns>Inviluppo di picco della stessa lunghezza</returns>
        private static float[] Pool(float[] values)
        {
            float[] result = new float[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                int from = Math.Max(0, i - POOL / 2);
                int to = Math.Min(values.Length - 1, i + POOL - POOL / 2 - 1);
                float best = values[from];
                for (int k = from + 1; k <= to; k++)
                {
                    if (values[k] > best)
                        best = values[k];
                }
                result[i] = best;
            }
            return result;
        }

        #endregion
    }
}
