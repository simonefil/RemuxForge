using RemuxForge.Core.Analysis.Edit.Boundary;
using RemuxForge.Core.Analysis.Edit.Detection;
using RemuxForge.Core.Analysis.Edit.Duration;
using RemuxForge.Core.Analysis.Edit.Extraction;
using RemuxForge.Core.Analysis.Edit.Verification;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Core.Analysis.Edit
{
    /// <summary>
    /// Esito completo dell'analisi di una coppia
    /// </summary>
    internal class EditAnalysisOutcome
    {
        #region Proprietà

        /// <summary>
        /// Operazioni accettate dalla ricostruzione globale
        /// </summary>
        public List<EditOperationCandidate> Operations { get; set; }

        /// <summary>
        /// Operazioni scartate, con il motivo già registrato nella diagnostica
        /// </summary>
        public List<EditOperationCandidate> Rejected { get; set; }

        /// <summary>
        /// Offset del primo tratto, ancorato sulla copertura complessiva
        /// </summary>
        public double InitialOffsetMs { get; set; }

        /// <summary>Compensazione audio misurata dopo la decisione video</summary>
        public DeepAudioOffsetDiagnostic AudioOffset { get; set; } = new DeepAudioOffsetDiagnostic();

        /// <summary>
        /// Frazione del film che resta agganciata applicando l'EditMap
        /// </summary>
        public double Coverage { get; set; }

        /// <summary>
        /// Corrispondenze univoche su cui il solver ha costruito i pianori
        /// </summary>
        public List<SolverAnchorDiagnostic> Anchors { get; set; }

        #endregion
    }

    /// <summary>
    /// Orchestrazione della ricostruzione globale, dei confini e della verifica finale
    /// </summary>
    internal class EditMapComposer
    {
        #region Variabili di istanza

        /// <summary>
        /// Risolutore dell'unica scala globale degli offset
        /// </summary>
        private GlobalOffsetSolver _globalSolver;

        /// <summary>
        /// Changepoint sui fotogrammi a piena frequenza
        /// </summary>
        private ChangePointRefiner _refiner;

        /// <summary>
        /// Convenzioni sulle run di nero
        /// </summary>
        private BlackRunRules _blackRunRules;

        /// <summary>
        /// Il confine dentro la run scura, deciso dall'audio
        /// </summary>
        private AudioBlackBoundary _audioBoundary;

        /// <summary>
        /// I vincoli dei fotogrammi esclusivi
        /// </summary>
        private ExclusiveFrameRules _exclusiveRules;

        /// <summary>
        /// Verifica della copertura complessiva
        /// </summary>
        private CoverageVerifier _coverageVerifier;

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
        public EditMapComposer(HashBackendBase hashBackend)
        {
            this._hashBackend = hashBackend;
            this._globalSolver = new GlobalOffsetSolver();
            this._refiner = new ChangePointRefiner();
            this._blackRunRules = new BlackRunRules();
            this._audioBoundary = new AudioBlackBoundary();
            this._exclusiveRules = new ExclusiveFrameRules();
            this._coverageVerifier = new CoverageVerifier(hashBackend);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce l'EditMap della coppia, dalla rilevazione al giudizio
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="envelopes">Inviluppi audio oppure null quando l'audio non è disponibile</param>
        /// <param name="cancellation">Token di annullamento</param>
        /// <returns>Operazioni, offset iniziale e copertura</returns>
        public EditAnalysisOutcome Compose(PairSignals pair, AudioEnvelopePair envelopes, CancellationToken cancellation)
        {
            // Rileva i cambi di offset sull'unica scala globale
            this._hashBackend.Attach(pair);
            List<EditOperationCandidate> operations = this._globalSolver.Detect(pair, cancellation, out double globalInitialOffsetMs);

            // Riancora la scala sulla copertura
            double preliminaryInitialOffsetMs = this._coverageVerifier.Anchor(pair, operations, globalInitialOffsetMs);
            double offsetShiftMs = preliminaryInitialOffsetMs - globalInitialOffsetMs;
            globalInitialOffsetMs = preliminaryInitialOffsetMs;

            // Propaga lo spostamento a tutte le operazioni
            foreach (EditOperationCandidate operation in operations)
            {
                operation.OffsetBeforeMs += offsetShiftMs;
                operation.OffsetAfterMs += offsetShiftMs;
            }

            // Misura le durate
            OperationDurationRefiner operationRefiner = new OperationDurationRefiner(this._hashBackend);
            operations = operationRefiner.Apply(pair, operations);

            // Prepara la scala allineata alla fase dei fotogrammi
            List<EditOperationCandidate> phaseAlignedOperations = operationRefiner.Apply(
                pair, operationRefiner.MeasureBoundaryOffsets(pair, operations, cancellation));

            // Congela le stime di partenza
            double[] transitionEstimates = new double[operations.Count];
            for (int i = 0; i < operations.Count; i++)
                transitionEstimates[i] = operations[i].TimestampMs;

            // Decide il confine di ogni operazione
            for (int operationIndex = 0; operationIndex < operations.Count; operationIndex++)
            {
                cancellation.ThrowIfCancellationRequested();
                EditOperationCandidate operation = operations[operationIndex];

                // Delimita il campo sulle operazioni vicine
                double lowerLimitMs = operationIndex == 0 ? pair.Source.PtsMs[0] :
                    (transitionEstimates[operationIndex - 1] + transitionEstimates[operationIndex]) / 2.0;
                double upperLimitMs = operationIndex + 1 == operations.Count ? pair.Source.PtsMs[pair.Source.Count - 1] :
                    (transitionEstimates[operationIndex] + transitionEstimates[operationIndex + 1]) / 2.0;

                // Apre la finestra di ricerca attorno ai due pianori
                double windowStartMs = Math.Max(lowerLimitMs, operation.PlateauEndBeforeMs - EditAnalysisProfile.CHANGEPOINT_MARGIN_MS);
                double windowEndMs = Math.Min(upperLimitMs, operation.PlateauStartAfterMs + EditAnalysisProfile.CHANGEPOINT_MARGIN_MS);

                // Cerca il changepoint, allargando finché si appoggia al bordo
                ChangePointResult refined;
                while (true)
                {
                    refined = this._refiner.Refine(pair, windowStartMs, windowEndMs,
                        operation.OffsetBeforeMs, operation.OffsetAfterMs,
                        phaseAlignedOperations[operationIndex].OffsetBeforeMs, phaseAlignedOperations[operationIndex].OffsetAfterMs,
                        phaseAlignedOperations[operationIndex].DurationMs);
                    if (refined == null)
                        break;
                    double expandedStartMs = refined.TouchesWindowStart ?
                        Math.Max(lowerLimitMs, windowStartMs - EditAnalysisProfile.CHANGEPOINT_MARGIN_MS) : windowStartMs;
                    double expandedEndMs = refined.TouchesWindowEnd ?
                        Math.Min(upperLimitMs, windowEndMs + EditAnalysisProfile.CHANGEPOINT_MARGIN_MS) : windowEndMs;
                    if (expandedStartMs == windowStartMs && expandedEndMs == windowEndMs)
                        break;
                    windowStartMs = expandedStartMs;
                    windowEndMs = expandedEndMs;
                }

                // Registra la finestra effettivamente esplorata
                operation.ChangePointWindowStartMs = windowStartMs;
                operation.ChangePointWindowEndMs = windowEndMs;
                if (refined != null)
                {
                    // Accetta il confine del changepoint
                    operation.ChangePointEquivalentStartMs = refined.EquivalentBoundaryStartMs;
                    operation.ChangePointEquivalentEndMs = refined.EquivalentBoundaryEndMs;
                    operation.TimestampMs = refined.NextAfterLastMs;

                    // Dentro la finestra cercata comanda la dissolvenza al nero, non l'estremo
                    double? equivalentRunStartMs = this._blackRunRules.FindRunStartInRange(pair, envelopes,
                        windowStartMs, windowEndMs,
                        operation.OffsetBeforeMs, operation.OffsetAfterMs, operation.DurationMs);
                    if (equivalentRunStartMs.HasValue)
                    {
                        operation.TimestampMs = equivalentRunStartMs.Value;
                        operation.Boundary = BoundaryDecision.BlackRunStart;
                        continue;
                    }
                }

                // Arretra dentro la run scura fin dove l'audio dà ragione all'offset di sinistra
                double timestampMs = this._audioBoundary.Resolve(pair.Source, envelopes, operation.TimestampMs, operation.OffsetBeforeMs);
                if (timestampMs < operation.TimestampMs)
                    operation.Boundary = BoundaryDecision.AudioInsideBlack;

                // Sposta all'inizio della run di nero
                double? runStartMs = this._blackRunRules.FindRunStart(pair.Source, timestampMs);
                if (runStartMs.HasValue)
                {
                    operation.TimestampMs = runStartMs.Value;
                    operation.Boundary = BoundaryDecision.BlackRunStart;
                    continue;
                }

                // Sotto il salto minimo il confine lo dà lo stacco di scena
                if (refined != null && Math.Abs(operation.OffsetAfterMs - operation.OffsetBeforeMs) < EditAnalysisProfile.CHANGEPOINT_MIN_JUMP_MS)
                {
                    double visualBoundaryMs = this._refiner.VisualBoundary(pair.Source, refined.NextAfterLastMs,
                        refined.FirstCommonMs, timestampMs, out int visualDistance);
                    if (visualDistance > EditAnalysisProfile.VERIFICATION_THRESHOLD)
                    {
                        timestampMs = visualBoundaryMs;
                        operation.Boundary = BoundaryDecision.SceneChange;
                    }
                }

                // Posticipa oltre i fotogrammi che esistono solo nella sorgente
                double postponedMs = this._exclusiveRules.Postpone(pair, timestampMs, operation.OffsetBeforeMs, operation.OffsetAfterMs);
                if (postponedMs != timestampMs)
                    operation.Boundary = BoundaryDecision.ExclusiveFrame;
                operation.TimestampMs = postponedMs;
            }

            // Rimisura gli offset sui confini appena decisi
            operations = operationRefiner.MeasureBoundaryOffsets(pair, operations, cancellation);

            // Arretra all'estremo della finestra ambigua
            foreach (EditOperationCandidate operation in operations)
            {
                cancellation.ThrowIfCancellationRequested();

                // Dove il nero ha già deciso non si arretra
                if (operation.Boundary == BoundaryDecision.BlackRunStart)
                    continue;
                double extremeMs = this._exclusiveRules.LeftExtreme(pair, operation.TimestampMs, operation.OffsetBeforeMs, operation.OffsetAfterMs);

                // Nemmeno quando lo stacco è già abbastanza vicino
                if (operation.Boundary == BoundaryDecision.SceneChange && operation.TimestampMs - extremeMs <= EditAnalysisProfile.EXTREME_FORWARD_MS)
                    continue;
                if (extremeMs != operation.TimestampMs)
                    operation.Boundary = BoundaryDecision.AmbiguousExtreme;
                operation.TimestampMs = extremeMs;
            }

            // Scarta le operazioni che la misura non conferma
            List<EditOperationCandidate> rejected = new List<EditOperationCandidate>();
            operations = operationRefiner.Filter(pair, operations, rejected);
            operations = operationRefiner.Apply(pair, operations);

            // Ancora la scala definitiva e misura la copertura
            double initialOffsetMs = this._coverageVerifier.Anchor(pair, operations, globalInitialOffsetMs);
            EditAnalysisOutcome outcome = new EditAnalysisOutcome
            {
                Operations = operations,
                Rejected = rejected,
                Anchors = this._globalSolver.LastAnchors,
                InitialOffsetMs = initialOffsetMs,
                Coverage = this._coverageVerifier.Coverage(pair, operations, initialOffsetMs)
            };
            outcome.AudioOffset = new AudioOffsetEstimator().Measure(pair, outcome, envelopes, cancellation);
            return outcome;
        }

        #endregion
    }
}
