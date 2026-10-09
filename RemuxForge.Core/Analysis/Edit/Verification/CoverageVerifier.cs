using RemuxForge.Core.Analysis.Edit.Extraction;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Edit.Verification
{
    /// <summary>
    /// Esito della verifica globale con i campioni che formano il denominatore
    /// </summary>
    internal class CoverageMeasurement
    {
        #region Proprietà

        /// <summary>
        /// Frazione del film spiegata dalla EditMap
        /// </summary>
        public double Coverage { get; set; }

        /// <summary>
        /// Campioni giudicati: tutto il film tranne i buchi che la EditMap dichiara
        /// </summary>
        public int ComparedSamples { get; set; }

        /// <summary>
        /// Campioni in un buco dichiarato da un INSERT o dall'offset iniziale, esclusi dal giudizio
        /// </summary>
        public int ExcludedSamples { get; set; }

        #endregion
    }

    /// <summary>
    /// Quanto un'EditMap tiene agganciato il film, dal primo fotogramma all'ultimo
    /// </summary>
    internal class CoverageVerifier
    {
        #region Costanti

        /// <summary>
        /// Identificatore stabile della metrica esposto nella diagnostica
        /// </summary>
        public const string METRIC_NAME = "thumbnail-gradient-cosine";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// La costante di ancoraggio che massimizza la copertura complessiva
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="operations">Operazioni dell'EditMap</param>
        /// <param name="initialOffsetMs">Offset iniziale grezzo</param>
        /// <returns>Offset del primo tratto che aggancia di più tutto il film</returns>
        public double Anchor(PairSignals pair, IReadOnlyList<EditOperationCandidate> operations, double initialOffsetMs)
        {
            // L'EditMap descrive la scala a meno di una costante: la copertura dell'intero film
            // la ancora senza dipendere da come è fatta la testa
            int[] indices = HashOps.RangeIndices(pair, pair.Source.PtsMs[0], double.MaxValue, 4 * EditAnalysisProfile.SAMPLING_STRIDE);
            double[] boundaries = BuildBoundaries(operations);
            double[] offsets = BuildOffsets(operations, 0.0);

            // La costante si cerca prima da lontano e a passo grosso: se la testa del film ha
            // mentito, un campo stretto attorno a lei resta chiuso dentro l'errore che deve curare
            double centerMs;
            int sweepCount = (int)(2.0 * EditAnalysisProfile.COVERAGE_ANCHOR_SWEEP_MS / EditAnalysisProfile.COVERAGE_ANCHOR_SWEEP_STEP_MS) + 1;
            double[] sweepFractions = new double[sweepCount];
            for (int i = 0; i < sweepCount; i++)
            {
                double candidateMs = initialOffsetMs - EditAnalysisProfile.COVERAGE_ANCHOR_SWEEP_MS + i * EditAnalysisProfile.COVERAGE_ANCHOR_SWEEP_STEP_MS;
                sweepFractions[i] = this.Explained(pair, indices, boundaries, offsets, candidateMs, 0);
            }
            centerMs = PeakNearest(initialOffsetMs - EditAnalysisProfile.COVERAGE_ANCHOR_SWEEP_MS,
                EditAnalysisProfile.COVERAGE_ANCHOR_SWEEP_STEP_MS, sweepFractions, initialOffsetMs);

            int coarseCount = (int)(2.0 * EditAnalysisProfile.COVERAGE_ANCHOR_FIELD_MS / 5.0) + 1;
            double[] coarseFractions = new double[coarseCount];
            for (int i = 0; i < coarseCount; i++)
            {
                double candidateMs = centerMs - EditAnalysisProfile.COVERAGE_ANCHOR_FIELD_MS + i * 5.0;
                coarseFractions[i] = this.Explained(pair, indices, boundaries, offsets, candidateMs, 0);
            }
            double bestOffsetMs = PeakNearest(centerMs - EditAnalysisProfile.COVERAGE_ANCHOR_FIELD_MS, 5.0, coarseFractions, centerMs);

            double[] fractions = new double[11];
            for (int i = 0; i <= 10; i++)
                fractions[i] = this.Explained(pair, indices, boundaries, offsets, bestOffsetMs - 5.0 + i, 0);

            return PeakNearest(bestOffsetMs - 5.0, 1.0, fractions, bestOffsetMs);
        }

        /// <summary>
        /// Misura la copertura sui gradienti delle miniature e registra il denominatore effettivo
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="operations">Operazioni dell'EditMap</param>
        /// <param name="initialOffsetMs">Offset del primo tratto</param>
        /// <returns>Copertura e conteggi dei campioni</returns>
        public CoverageMeasurement MeasureCoverage(PairSignals pair, IReadOnlyList<EditOperationCandidate> operations, double initialOffsetMs)
        {
            int[] indices = HashOps.RangeIndices(pair, pair.Source.PtsMs[0], double.MaxValue, EditAnalysisProfile.SAMPLING_STRIDE);
            double[] boundaries = BuildBoundaries(operations);
            double[] offsets = BuildOffsets(operations, initialOffsetMs);
            double[] languagePts = pair.LanguagePtsMs;
            int compared = 0;
            int excluded = 0;
            int explained = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                int sourceIndex = indices[i];
                double sourceTimeMs = pair.Source.PtsMs[sourceIndex];

                // Dentro il buco di un INSERT la sorgente non ha controparte perché la mappa lo
                // dichiara: chiedere a quel campione di agganciarsi punirebbe la mappa per aver
                // detto il vero
                if (IsInsideDeclaredGap(operations, sourceTimeMs))
                {
                    excluded++;
                    continue;
                }

                int segment = 0;
                while (segment < boundaries.Length && boundaries[segment] <= sourceTimeMs)
                    segment++;
                double languageTimeMs = sourceTimeMs + offsets[segment];

                // Prima dell'inizio del lang il buco lo dichiara l'offset iniziale, che è una misura
                // e non un avanzo: una testa che il lang non ha vale quanto quella di un INSERT
                if (languageTimeMs < languagePts[0])
                {
                    excluded++;
                    continue;
                }

                compared++;

                // Oltre la fine del lang invece non ha misurato niente nessuno: è il residuo che la
                // EditMap chiuderà in coda, e toglierlo dal denominatore nasconderebbe proprio la
                // parte di film che non si sa spiegare
                if (languageTimeMs > languagePts[languagePts.Length - 1])
                    continue;

                int center = HashOps.LowerBound(languagePts, languageTimeMs);
                double best = -1.0;
                for (int shift = -EditAnalysisProfile.VERIFICATION_RADIUS; shift <= EditAnalysisProfile.VERIFICATION_RADIUS; shift++)
                {
                    int languageIndex = Math.Clamp(center + shift, 0, languagePts.Length - 1);
                    best = Math.Max(best, GradientCosine(pair, sourceIndex, languageIndex));
                }
                if (best >= EditAnalysisProfile.COVERAGE_GRADIENT_COSINE_MINIMUM)
                    explained++;
            }

            return new CoverageMeasurement
            {
                Coverage = compared > 0 ? (double)explained / compared : 0.0,
                ComparedSamples = compared,
                ExcludedSamples = excluded
            };
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Campione massimo più vicino alla stima che ha guidato la scansione
        /// </summary>
        /// <param name="startMs">Valore del primo campione</param>
        /// <param name="stepMs">Passo fra i campioni</param>
        /// <param name="fractions">Coperture misurate</param>
        /// <param name="referenceMs">Stima da conservare in caso di parità</param>
        /// <returns>Campione scelto sulla cima piatta</returns>
        private static double PeakNearest(double startMs, double stepMs, double[] fractions, double referenceMs)
        {
            double peak = -1.0;
            for (int i = 0; i < fractions.Length; i++)
                peak = Math.Max(peak, fractions[i]);

            double result = startMs;
            double nearest = double.MaxValue;
            for (int i = 0; i < fractions.Length; i++)
            {
                if (fractions[i] < peak - 1e-12)
                    continue;
                double candidateMs = startMs + i * stepMs;
                double distanceMs = Math.Abs(candidateMs - referenceMs);
                if (distanceMs >= nearest)
                    continue;
                result = candidateMs;
                nearest = distanceMs;
            }
            return result;
        }

        /// <summary>
        /// Confini della funzione a gradini che l'EditMap descrive
        /// </summary>
        /// <param name="operations">Operazioni dell'EditMap</param>
        /// <returns>Istanti dei confini</returns>
        private static double[] BuildBoundaries(IReadOnlyList<EditOperationCandidate> operations)
        {
            double[] result = new double[operations.Count];
            for (int i = 0; i < operations.Count; i++)
                result[i] = operations[i].TimestampMs;
            return result;
        }

        /// <summary>
        /// Indica se un istante sorgente cade nel buco che un INSERT dichiara
        /// </summary>
        /// <param name="operations">Operazioni dell'EditMap</param>
        /// <param name="sourceTimeMs">Istante sorgente</param>
        /// <returns>Vero quando la mappa dichiara che lì il language non ha nulla</returns>
        private static bool IsInsideDeclaredGap(IReadOnlyList<EditOperationCandidate> operations, double sourceTimeMs)
        {
            for (int i = 0; i < operations.Count; i++)
            {
                if (operations[i].Kind != EditOperationKind.InsertSilence)
                    continue;
                if (sourceTimeMs >= operations[i].TimestampMs && sourceTimeMs < operations[i].ResumeMs)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Offset di ciascun tratto della funzione a gradini
        /// </summary>
        /// <param name="operations">Operazioni dell'EditMap</param>
        /// <param name="initialOffsetMs">Offset del primo tratto</param>
        /// <returns>Offset dei tratti, uno in più delle operazioni</returns>
        private static double[] BuildOffsets(IReadOnlyList<EditOperationCandidate> operations, double initialOffsetMs)
        {
            double[] result = new double[operations.Count + 1];
            result[0] = initialOffsetMs;
            for (int i = 0; i < operations.Count; i++)
                result[i + 1] = operations[i].Kind == EditOperationKind.InsertSilence ? result[i] - operations[i].DurationMs : result[i] + operations[i].DurationMs;
            return result;
        }

        /// <summary>
        /// Confronta i 264 gradienti firmati delle miniature 12x12
        /// </summary>
        /// <param name="pair">Coppia che possiede le miniature</param>
        /// <param name="sourceIndex">Indice source</param>
        /// <param name="languageIndex">Indice language</param>
        /// <returns>Similarita' coseno fra -1 e 1, oppure -1 per miniature piatte</returns>
        private static double GradientCosine(PairSignals pair, int sourceIndex, int languageIndex)
        {
            int side = FrameSignals.THUMB_SIDE;
            int sourceOrigin = sourceIndex * side * side;
            int languageOrigin = languageIndex * side * side;
            long dot = 0;
            long sourceSquare = 0;
            long languageSquare = 0;
            for (int row = 0; row < side; row++)
            {
                for (int column = 0; column < side - 1; column++)
                    AccumulateGradient(pair, sourceOrigin + row * side + column, languageOrigin + row * side + column, 1, ref dot, ref sourceSquare, ref languageSquare);
            }
            for (int row = 0; row < side - 1; row++)
            {
                for (int column = 0; column < side; column++)
                    AccumulateGradient(pair, sourceOrigin + row * side + column, languageOrigin + row * side + column, side, ref dot, ref sourceSquare, ref languageSquare);
            }
            if (sourceSquare == 0 || languageSquare == 0)
                return -1.0;
            return dot / Math.Sqrt((double)sourceSquare * languageSquare);
        }

        /// <summary>
        /// Accumula una componente del prodotto scalare e delle due norme
        /// </summary>
        /// <param name="pair">Coppia che possiede le miniature</param>
        /// <param name="sourceOffset">Posizione del primo pixel source</param>
        /// <param name="languageOffset">Posizione del primo pixel language</param>
        /// <param name="step">Distanza dal pixel adiacente</param>
        /// <param name="dot">Prodotto scalare accumulato</param>
        /// <param name="sourceSquare">Norma source al quadrato accumulata</param>
        /// <param name="languageSquare">Norma language al quadrato accumulata</param>
        private static void AccumulateGradient(PairSignals pair, int sourceOffset, int languageOffset, int step,
            ref long dot, ref long sourceSquare, ref long languageSquare)
        {
            int sourceGradient = pair.Source.ThumbPixels[sourceOffset + step] - pair.Source.ThumbPixels[sourceOffset];
            int languageGradient = pair.Language.ThumbPixels[languageOffset + step] - pair.Language.ThumbPixels[languageOffset];
            dot += sourceGradient * languageGradient;
            sourceSquare += sourceGradient * sourceGradient;
            languageSquare += languageGradient * languageGradient;
        }

        /// <summary>
        /// Frazione dei fotogrammi campionati che la scala spiega
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="indices">Indici sorgente campionati</param>
        /// <param name="boundaries">Confini della scala</param>
        /// <param name="offsets">Offset dei tratti</param>
        /// <param name="shiftMs">Costante da sommare a tutti gli offset</param>
        /// <param name="radius">Tolleranza in frame: zero per ancorare, permissiva per verificare</param>
        /// <returns>Quota agganciata fra zero e uno</returns>
        private double Explained(PairSignals pair, int[] indices, double[] boundaries, double[] offsets, double shiftMs, int radius = EditAnalysisProfile.VERIFICATION_RADIUS)
        {
            if (indices.Length == 0)
                return 0.0;
            int explained = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                double timeMs = pair.Source.PtsMs[indices[i]];
                int segment = 0;
                while (segment < boundaries.Length && boundaries[segment] <= timeMs)
                    segment++;
                if (HashOps.Distance(pair, indices[i], offsets[segment] + shiftMs, radius) <= EditAnalysisProfile.VERIFICATION_THRESHOLD)
                    explained++;
            }
            return (double)explained / indices.Length;
        }

        #endregion
    }
}
