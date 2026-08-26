namespace RemuxForge.Core.Analysis.Edit.Boundary
{
    /// <summary>
    /// I vincoli che i fotogrammi spiegati dal solo offset di sinistra impongono al confine
    /// </summary>
    internal class ExclusiveFrameRules
    {
        #region Costanti

        /// <summary>
        /// Semiampiezza della finestra esaminata da Postpone
        /// </summary>
        private const double POSTPONE_WINDOW_MS = 3000.0;

        /// <summary>
        /// Semiampiezza della finestra esaminata da LeftExtreme
        /// </summary>
        private const double EXTREME_WINDOW_MS = 6000.0;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Sposta il confine oltre l'ultimo fotogramma che solo l'offset di sinistra spiega
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="timestampMs">Confine proposto</param>
        /// <param name="offsetBeforeMs">Offset del pianoro precedente</param>
        /// <param name="offsetAfterMs">Offset del pianoro successivo</param>
        /// <returns>Confine corretto</returns>
        public double Postpone(PairSignals pair, double timestampMs, double offsetBeforeMs, double offsetAfterMs)
        {
            // È il vincolo più stretto che si abbia sulla posizione. Si segue solo la run
            // contigua, perché un aggancio isolato molto più avanti è un falso positivo
            double[] sourcePts = pair.Source.PtsMs;
            int first = HashOps.LowerBound(sourcePts, timestampMs - POSTPONE_WINDOW_MS);
            int count = HashOps.LowerBound(sourcePts, timestampMs + POSTPONE_WINDOW_MS) - first;
            if (count <= 0)
                return timestampMs;

            bool[] exclusive = this.MarkExclusiveFrames(pair, first, count, offsetBeforeMs, offsetAfterMs);
            int index = HashOps.LowerBound(sourcePts, timestampMs) - first;
            if (index < 0)
                index = 0;
            double result = timestampMs;
            while (index < count)
            {
                int last = -1;
                for (int k = index; k < count && sourcePts[first + k] <= sourcePts[first + index] + EditAnalysisProfile.EXCLUSIVE_GAP_MS; k++)
                {
                    if (exclusive[k])
                        last = k;
                }
                if (last < 0)
                    break;
                index = last + 1;
                if (index < count)
                    result = sourcePts[first + index];
            }

            return result;
        }

        /// <summary>
        /// Porta il confine all'estremo sinistro della finestra ambigua
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="timestampMs">Confine proposto</param>
        /// <param name="offsetBeforeMs">Offset del pianoro precedente</param>
        /// <param name="offsetAfterMs">Offset del pianoro successivo</param>
        /// <returns>Confine corretto</returns>
        public double LeftExtreme(PairSignals pair, double timestampMs, double offsetBeforeMs, double offsetAfterMs)
        {
            // All'indietro non si mette tetto: è proprio il caso che serve. In avanti il tetto
            // serve, perché un solo-A isolato molto più avanti è un falso positivo dentro
            // un'inquadratura tenuta
            double[] sourcePts = pair.Source.PtsMs;
            int first = HashOps.LowerBound(sourcePts, timestampMs - EXTREME_WINDOW_MS);
            int count = HashOps.LowerBound(sourcePts, timestampMs + EXTREME_WINDOW_MS) - first;
            if (count <= 0)
                return timestampMs;

            bool[] exclusive = this.MarkExclusiveFrames(pair, first, count, offsetBeforeMs, offsetAfterMs);
            int last = -1;
            for (int k = 0; k < count; k++)
            {
                if (exclusive[k])
                    last = k;
            }
            if (last < 0 || last + 1 >= count)
                return timestampMs;

            double candidateMs = sourcePts[first + last + 1];
            return candidateMs - timestampMs <= EditAnalysisProfile.EXTREME_FORWARD_MS ? candidateMs : timestampMs;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Segna i fotogrammi che solo l'offset di sinistra spiega
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="first">Primo indice sorgente della finestra</param>
        /// <param name="count">Fotogrammi della finestra</param>
        /// <param name="offsetBeforeMs">Offset del pianoro precedente</param>
        /// <param name="offsetAfterMs">Offset del pianoro successivo</param>
        /// <returns>Maschera dei fotogrammi esclusivi</returns>
        private bool[] MarkExclusiveFrames(PairSignals pair, int first, int count, double offsetBeforeMs, double offsetAfterMs)
        {
            bool[] result = new bool[count];
            for (int k = 0; k < count; k++)
            {
                result[k] = HashOps.Distance(pair, first + k, offsetBeforeMs, EditAnalysisProfile.DETECTION_RADIUS) <= EditAnalysisProfile.DETECTION_THRESHOLD &&
                            HashOps.Distance(pair, first + k, offsetAfterMs, EditAnalysisProfile.DETECTION_RADIUS) > EditAnalysisProfile.DETECTION_THRESHOLD;
            }
            return result;
        }

        #endregion
    }
}
