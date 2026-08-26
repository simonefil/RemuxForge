using System;

namespace RemuxForge.Core.Analysis.Edit.Judgement
{
    /// <summary>
    /// L'operazione dichiara uno scalino: o lo si rimisura sulle due finestre solide ai lati, o non c'è
    /// </summary>
    internal class PlateauStepVerifier
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
        public PlateauStepVerifier(HashBackendBase hashBackend)
        {
            this._hashBackend = hashBackend;
        }

        #endregion

        #region Costanti

        /// <summary>
        /// Ampiezza della finestra su cui si misura l'offset di un lato
        /// </summary>
        private const double WINDOW_MS = 6000.0;

        /// <summary>
        /// Quanto ci si può allontanare dal confine cercando una finestra che tenga
        /// </summary>
        private const double SEARCH_MS = 20000.0;

        /// <summary>
        /// Passo con cui ci si allontana dal confine
        /// </summary>
        private const double SEARCH_STEP_MS = 1000.0;

        /// <summary>
        /// Frazione agganciata sotto cui la finestra non è solida
        /// </summary>
        private const double SOLID_FRACTION = 0.85;

        /// <summary>
        /// Semiampiezza della ricerca grezza dell'offset di una finestra
        /// </summary>
        private const double RADIUS_MS = 400.0;

        /// <summary>
        /// Passo della ricerca grezza
        /// </summary>
        private const double COARSE_STEP_MS = 20.0;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Verifica che lo scalino dichiarato si rimisuri ai due lati del confine
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="operation">Operazione da verificare</param>
        /// <returns>True quando lo scalino si rimisura o non si può misurare</returns>
        public bool Holds(PairSignals pair, EditOperationCandidate operation)
        {
            // La copertura del pianoro non serve sotto il fotogramma: misurare l'offset su due
            // finestre solide lo vede, perché il picco di una finestra è preciso a qualche decina di ms
            double? before = this.SolidOffset(pair, operation.TimestampMs, -1, operation.OffsetBeforeMs);
            double? after = this.SolidOffset(pair, operation.ResumeMs, +1, operation.OffsetAfterMs);
            if (!before.HasValue || !after.HasValue)
                return true;

            double expected = operation.Kind == EditOperationKind.InsertSilence ? -operation.DurationMs : operation.DurationMs;
            return Math.Abs(after.Value - before.Value) >= EditAnalysisProfile.PLATEAU_STEP_RATIO * Math.Abs(expected);
        }

        /// <summary>
        /// Offset e quota agganciata di una finestra, cercati attorno a un centro
        /// </summary>
        /// <param name="firstIndex">Primo indice sorgente della finestra</param>
        /// <param name="stride">Passo fra due indici sorgente consecutivi</param>
        /// <param name="indexCount">Quanti indici sorgente contiene la finestra</param>
        /// <param name="centerMs">Offset da cui partire</param>
        /// <param name="explained">Frazione agganciata alla cima</param>
        /// <returns>Centro della cima piatta</returns>
        private double MeasureOffset(int firstIndex, int stride, int indexCount, double centerMs, out double explained)
        {
            double bestFraction = -1.0;
            double bestOffsetMs = centerMs;
            int coarseCount = (int)Math.Floor(2.0 * RADIUS_MS / COARSE_STEP_MS) + 1;
            double[] coarse = this._hashBackend.Scan(firstIndex, stride, indexCount, centerMs - RADIUS_MS, COARSE_STEP_MS, coarseCount, EditAnalysisProfile.VERIFICATION_RADIUS, EditAnalysisProfile.DETECTION_THRESHOLD);
            for (int i = 0; i < coarseCount; i++)
            {
                if (coarse[i] > bestFraction)
                {
                    bestFraction = coarse[i];
                    bestOffsetMs = centerMs - RADIUS_MS + i * COARSE_STEP_MS;
                }
            }

            int fineCount = (int)(2.0 * COARSE_STEP_MS) + 1;
            double[] fractions = this._hashBackend.Scan(firstIndex, stride, indexCount, bestOffsetMs - COARSE_STEP_MS, 1.0, fineCount, EditAnalysisProfile.VERIFICATION_RADIUS, EditAnalysisProfile.DETECTION_THRESHOLD);
            double peak = -1.0;
            for (int i = 0; i < fineCount; i++)
                peak = Math.Max(peak, fractions[i]);

            double total = 0.0;
            int members = 0;
            for (int i = 0; i < fineCount; i++)
            {
                if (fractions[i] < peak - 1e-12)
                    continue;
                total += bestOffsetMs - COARSE_STEP_MS + i;
                members++;
            }

            explained = peak;
            return total / members;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Offset misurato sulla prima finestra che tiene, allontanandosi dal confine
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="timestampMs">Confine da cui partire</param>
        /// <param name="direction">Verso in cui allontanarsi</param>
        /// <param name="centerMs">Offset da cui partire a cercare</param>
        /// <returns>Offset della prima finestra solida oppure null</returns>
        private double? SolidOffset(PairSignals pair, double timestampMs, int direction, double centerMs)
        {
            for (double distanceMs = 0.0; distanceMs < SEARCH_MS; distanceMs += SEARCH_STEP_MS)
            {
                double startMs = direction > 0 ? timestampMs + distanceMs : timestampMs - distanceMs - WINDOW_MS;
                HashOps.Range(pair, startMs, startMs + WINDOW_MS, 2, out int first, out int indexCount);
                if (indexCount < 30)
                    continue;
                double offsetMs = this.MeasureOffset(first, 2, indexCount, centerMs, out double explained);
                if (explained >= SOLID_FRACTION)
                    return offsetMs;
            }
            return null;
        }

        #endregion
    }
}
