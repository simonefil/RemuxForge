using RemuxForge.Core.Analysis.Edit.Extraction;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Offset costante letto dagli inviluppi audio, quando le due copie condividono una traccia
    /// </summary>
    internal class FrameSyncAudioOffsetResolver
    {
        #region Costanti

        /// <summary>
        /// Posizioni relative delle finestre di correlazione
        /// </summary>
        private static readonly double[] POSITIONS = new double[] { 0.2, 0.5, 0.8 };

        /// <summary>
        /// Campioni di inviluppo accorpati nella scansione grossolana
        /// </summary>
        private const int POOL = 10;

        /// <summary>
        /// Durata di una finestra di correlazione, in millisecondi
        /// </summary>
        private const double WINDOW_MS = 20000.0;

        /// <summary>
        /// Ritardo massimo esplorato in millisecondi
        /// </summary>
        private const double MAX_LAG_MS = 120000.0;

        /// <summary>
        /// Semiampiezza della scansione fine attorno al ritardo grossolano
        /// </summary>
        private const double FINE_RADIUS_MS = 400.0;

        /// <summary>
        /// Distanza entro cui due finestre raccontano lo stesso ritardo
        /// </summary>
        private const double AGREEMENT_MS = 60.0;

        /// <summary>
        /// Correlazione normalizzata minima perché una finestra conti
        /// </summary>
        private const double MIN_CORRELATION = 0.35;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Cerca il ritardo costante che allinea i due inviluppi
        /// </summary>
        /// <param name="envelopes">Inviluppi sulla griglia comune, con lo stretch già applicato</param>
        /// <param name="offsetMs">Ritardo trovato, nella convenzione lang = source - offset</param>
        /// <param name="correlation">Correlazione normalizzata media delle finestre concordi</param>
        /// <returns>True quando almeno due finestre lontane danno lo stesso ritardo</returns>
        public bool TryResolve(AudioEnvelopePair envelopes, out double offsetMs, out double correlation)
        {
            // Su tutto il film la valle di costo è un pianoro largo secondi e il suo minimo non
            // significa niente: sono finestre lontane che concordano a dire dov'è il ritardo
            offsetMs = 0.0;
            correlation = 0.0;
            if (envelopes == null || envelopes.Source.Length == 0)
                return false;

            float[] coarseSource = Pool(envelopes.Source);
            float[] coarseLanguage = Pool(envelopes.Language);
            double coarseStepMs = AudioEnvelopeExtractor.STEP_MS * POOL;
            double durationMs = envelopes.Source.Length * AudioEnvelopeExtractor.STEP_MS;
            List<double> offsets = new List<double>();
            List<double> correlations = new List<double>();

            for (int positionIndex = 0; positionIndex < POSITIONS.Length; positionIndex++)
            {
                double startMs = durationMs * POSITIONS[positionIndex] - WINDOW_MS / 2.0;
                if (!this.Scan(coarseSource, coarseLanguage, coarseStepMs, startMs, WINDOW_MS, 0.0, MAX_LAG_MS, out double coarseOffsetMs, out double coarseCorrelation))
                    continue;
                if (!this.Scan(envelopes.Source, envelopes.Language, AudioEnvelopeExtractor.STEP_MS, startMs, WINDOW_MS, coarseOffsetMs, FINE_RADIUS_MS + coarseStepMs, out double windowOffsetMs, out double windowCorrelation))
                {
                    windowOffsetMs = coarseOffsetMs;
                    windowCorrelation = coarseCorrelation;
                }
                if (windowCorrelation < MIN_CORRELATION)
                    continue;
                offsets.Add(windowOffsetMs);
                correlations.Add(windowCorrelation);
            }

            // Basterebbe una coppia concorde, ma due finestre su tre che vanno d'accordo mentre
            // la terza dice altro non sono la prova di un offset costante: sono la prova di un taglio
            if (offsets.Count < 2)
                return false;
            double lowest = offsets[0];
            double highest = offsets[0];
            double sum = 0.0;
            double correlationSum = 0.0;
            for (int i = 0; i < offsets.Count; i++)
            {
                lowest = Math.Min(lowest, offsets[i]);
                highest = Math.Max(highest, offsets[i]);
                sum += offsets[i];
                correlationSum += correlations[i];
            }
            if (highest - lowest > AGREEMENT_MS)
                return false;

            offsetMs = sum / offsets.Count;
            correlation = correlationSum / correlations.Count;
            return true;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Accorpa l'inviluppo per media, per portare la scansione a una scala grossolana
        /// </summary>
        /// <param name="values">Inviluppo a passo pieno</param>
        /// <returns>Inviluppo accorpato</returns>
        private static float[] Pool(float[] values)
        {
            float[] result = new float[values.Length / POOL];
            for (int i = 0; i < result.Length; i++)
            {
                double sum = 0.0;
                for (int k = 0; k < POOL; k++)
                    sum += values[i * POOL + k];
                result[i] = (float)(sum / POOL);
            }
            return result;
        }

        /// <summary>
        /// Massimizza la correlazione normalizzata di una finestra sui ritardi della griglia
        /// </summary>
        /// <param name="source">Inviluppo della sorgente sulla griglia</param>
        /// <param name="language">Inviluppo della copia doppiata sulla stessa griglia</param>
        /// <param name="stepMs">Passo temporale della griglia</param>
        /// <param name="startMs">Inizio della finestra rispetto al primo campione</param>
        /// <param name="windowMs">Durata della finestra</param>
        /// <param name="centerMs">Ritardo attorno a cui si scandisce</param>
        /// <param name="radiusMs">Semiampiezza della scansione</param>
        /// <param name="offsetMs">Ritardo migliore trovato</param>
        /// <param name="correlation">Correlazione del ritardo migliore</param>
        /// <returns>True quando la finestra ricade nel materiale di entrambe le copie</returns>
        private bool Scan(float[] source, float[] language, double stepMs, double startMs, double windowMs, double centerMs, double radiusMs, out double offsetMs, out double correlation)
        {
            offsetMs = 0.0;
            correlation = -2.0;
            int start = (int)Math.Round(startMs / stepMs);
            int length = (int)(windowMs / stepMs);
            if (start < 0 || start + length > source.Length)
                return false;

            int centerLag = (int)Math.Round(-centerMs / stepMs);
            int lagRadius = (int)Math.Ceiling(radiusMs / stepMs);
            for (int lag = centerLag - lagRadius; lag <= centerLag + lagRadius; lag++)
            {
                if (start + lag < 0 || start + lag + length > language.Length)
                    continue;

                double value = Correlate(source, language, lag, start, start + length);
                if (value <= correlation)
                    continue;
                correlation = value;
                offsetMs = -lag * stepMs;
            }

            return correlation > -2.0;
        }

        /// <summary>
        /// Correlazione normalizzata dei due inviluppi sulla finestra indicata
        /// </summary>
        /// <param name="source">Inviluppo della sorgente</param>
        /// <param name="language">Inviluppo della copia doppiata</param>
        /// <param name="lag">Ritardo in campioni della griglia</param>
        /// <param name="start">Primo indice sorgente della finestra</param>
        /// <param name="end">Indice sorgente successivo all'ultimo</param>
        /// <returns>Correlazione fra meno uno e uno</returns>
        private static double Correlate(float[] source, float[] language, int lag, int start, int end)
        {
            int length = end - start;
            double sourceMean = 0.0;
            double languageMean = 0.0;
            for (int i = start; i < end; i++)
            {
                sourceMean += source[i];
                languageMean += language[i + lag];
            }
            sourceMean /= length;
            languageMean /= length;

            double cross = 0.0;
            double sourceEnergy = 0.0;
            double languageEnergy = 0.0;
            for (int i = start; i < end; i++)
            {
                double left = source[i] - sourceMean;
                double right = language[i + lag] - languageMean;
                cross += left * right;
                sourceEnergy += left * left;
                languageEnergy += right * right;
            }

            double norm = Math.Sqrt(sourceEnergy * languageEnergy);
            return norm > 0.0 ? cross / norm : 0.0;
        }

        #endregion
    }
}
