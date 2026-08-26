using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Core.Analysis.Edit.Detection
{
    /// <summary>
    /// Misura offset(t) su griglia fitta, dove si può misurare bene: dentro i pianori
    /// </summary>
    internal class OffsetProfileBuilder
    {
        #region Variabili di classe

        /// <summary>
        /// Backend che calcola gli hash e misura le griglie di offset
        /// </summary>
        private HashBackendBase _hashBackend;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="hashBackend">Backend che calcola gli hash e misura le griglie di offset</param>
        public OffsetProfileBuilder(HashBackendBase hashBackend)
        {
            this._hashBackend = hashBackend;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce il profilo offset(t) su tutta la durata della sorgente
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="initialOffsetMs">Offset da cui partire a cercare</param>
        /// <param name="cancellation">Token di annullamento</param>
        /// <returns>Punti del profilo, anche quelli in cui l'aggancio è perso</returns>
        public List<OffsetProfilePoint> Build(PairSignals pair, double initialOffsetMs, CancellationToken cancellation)
        {
            // Invece di far proporre le operazioni a un rivelatore e poi misurarle, si misura
            // offset(t) dove si può misurare bene e le operazioni sono quello che resta
            List<OffsetProfilePoint> result = new List<OffsetProfilePoint>();
            double lastMs = pair.Source.PtsMs[pair.Source.Count - 1];
            double current = initialOffsetMs;
            bool locked = false;

            for (double timeMs = EditAnalysisProfile.PROFILE_WINDOW_MS; timeMs < lastMs - EditAnalysisProfile.PROFILE_WINDOW_MS; timeMs += EditAnalysisProfile.PROFILE_STEP_MS)
            {
                cancellation.ThrowIfCancellationRequested();
                double offsetMs;
                double explained;
                if (locked)
                {
                    this.Search(pair, timeMs, current, EditAnalysisProfile.PROFILE_NEAR_RADIUS_MS, EditAnalysisProfile.PROFILE_FINE_STEP_MS, out offsetMs, out explained);
                }
                else
                {
                    this.Search(pair, timeMs, current, EditAnalysisProfile.PROFILE_WIDE_RADIUS_MS, EditAnalysisProfile.PROFILE_COARSE_STEP_MS, out offsetMs, out explained);
                    if (explained > 0.0)
                        this.Search(pair, timeMs, offsetMs, EditAnalysisProfile.PROFILE_COARSE_STEP_MS, EditAnalysisProfile.PROFILE_FINE_STEP_MS, out offsetMs, out explained);
                }

                if (explained < EditAnalysisProfile.PROFILE_GOOD_FRACTION && locked)
                {
                    // perso l'aggancio: si riparte largo dallo stesso istante
                    this.Search(pair, timeMs, current, EditAnalysisProfile.PROFILE_WIDE_RADIUS_MS, EditAnalysisProfile.PROFILE_COARSE_STEP_MS, out double wideOffsetMs, out double wideExplained);
                    if (wideExplained > explained)
                        this.Search(pair, timeMs, wideOffsetMs, EditAnalysisProfile.PROFILE_COARSE_STEP_MS, EditAnalysisProfile.PROFILE_FINE_STEP_MS, out offsetMs, out explained);
                }

                locked = explained >= EditAnalysisProfile.PROFILE_GOOD_FRACTION;
                if (locked)
                    current = offsetMs;
                result.Add(new OffsetProfilePoint(timeMs, offsetMs, explained));
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Cerca l'offset che spiega più fotogrammi nella finestra centrata sull'istante
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="timeMs">Centro della finestra di misura</param>
        /// <param name="centerMs">Centro della scansione degli offset</param>
        /// <param name="radiusMs">Semiampiezza della scansione</param>
        /// <param name="stepMs">Passo della scansione</param>
        /// <param name="offsetMs">Offset migliore trovato</param>
        /// <param name="explained">Frazione spiegata dall'offset migliore</param>
        private void Search(PairSignals pair, double timeMs, double centerMs, double radiusMs, double stepMs, out double offsetMs, out double explained)
        {
            HashOps.Range(pair, timeMs - EditAnalysisProfile.PROFILE_WINDOW_MS, timeMs + EditAnalysisProfile.PROFILE_WINDOW_MS, 1, out int first, out int indexCount);
            offsetMs = centerMs - radiusMs;
            explained = -1.0;
            if (indexCount < EditAnalysisProfile.PROFILE_MIN_FRAMES)
                return;

            int count = (int)Math.Ceiling((2.0 * radiusMs + stepMs) / stepMs);
            double[] fractions = this._hashBackend.Scan(first, 1, indexCount, centerMs - radiusMs, stepMs, count, EditAnalysisProfile.DETECTION_RADIUS, EditAnalysisProfile.DETECTION_THRESHOLD);
            for (int i = 0; i < count; i++)
            {
                if (fractions[i] > explained)
                {
                    explained = fractions[i];
                    offsetMs = centerMs - radiusMs + i * stepMs;
                }
            }
        }

        #endregion
    }
}
