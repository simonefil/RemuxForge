using RemuxForge.Core.Analysis.Edit.Boundary;
using RemuxForge.Core.Analysis.Edit.Detection;
using RemuxForge.Core.Analysis.Edit.Duration;
using RemuxForge.Core.Analysis.Edit.Extraction;
using RemuxForge.Core.Analysis.Edit.Judgement;
using RemuxForge.Core.Analysis.Edit.Verification;
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
        /// Operazioni sopravvissute ai tre filtri
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

        /// <summary>
        /// Frazione del film che resta agganciata applicando l'EditMap
        /// </summary>
        public double Coverage { get; set; }

        /// <summary>
        /// Profilo offset(t) su cui è costruita la rilevazione
        /// </summary>
        public List<OffsetProfilePoint> Profile { get; set; }

        #endregion
    }

    /// <summary>
    /// Orchestrazione della catena: prima i pianori, poi i confini, poi il giudizio
    /// </summary>
    internal class EditMapComposer
    {
        #region Variabili di istanza

        /// <summary>
        /// Costruttore del profilo offset(t)
        /// </summary>
        private OffsetProfileBuilder _profileBuilder;

        /// <summary>
        /// Segmentatore del profilo in rette
        /// </summary>
        private OffsetStaircaseSolver _staircaseSolver;

        /// <summary>
        /// Seconda passata a scala fine
        /// </summary>
        private FineScaleRescuer _rescuer;

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
        /// Misura delle durate dai due offset di pianoro
        /// </summary>
        private PlateauOffsetMeasurer _durationMeasurer;

        /// <summary>
        /// Verifica dello scalino sui pianori
        /// </summary>
        private PlateauStepVerifier _plateauVerifier;

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
            this._profileBuilder = new OffsetProfileBuilder(hashBackend);
            this._staircaseSolver = new OffsetStaircaseSolver();
            this._rescuer = new FineScaleRescuer();
            this._refiner = new ChangePointRefiner();
            this._blackRunRules = new BlackRunRules();
            this._audioBoundary = new AudioBlackBoundary();
            this._exclusiveRules = new ExclusiveFrameRules();
            this._durationMeasurer = new PlateauOffsetMeasurer(hashBackend);
            this._plateauVerifier = new PlateauStepVerifier(hashBackend);
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
            this._hashBackend.Attach(pair);
            List<OffsetProfilePoint> profile = this._profileBuilder.Build(pair, 0.0, cancellation);
            List<OffsetProfilePoint> good = new List<OffsetProfilePoint>();
            foreach (OffsetProfilePoint point in profile)
            {
                if (point.Explained >= EditAnalysisProfile.PROFILE_GOOD_FRACTION)
                    good.Add(point);
            }

            List<EditOperationCandidate> operations = this._staircaseSolver.Detect(this._staircaseSolver.Segment(good), 6.0);
            foreach (EditOperationCandidate operation in operations)
            {
                cancellation.ThrowIfCancellationRequested();
                ChangePointResult refined = this._refiner.Refine(pair,
                    operation.PlateauEndBeforeMs - EditAnalysisProfile.SEGMENT_REFINE_MARGIN_MS,
                    operation.PlateauStartAfterMs + EditAnalysisProfile.SEGMENT_REFINE_MARGIN_MS,
                    operation.OffsetBeforeMs, operation.OffsetAfterMs);
                if (refined != null)
                    operation.TimestampMs = refined.NextAfterLastMs;
                operation.TimestampMs = this._audioBoundary.Resolve(pair.Source, envelopes, operation.TimestampMs, operation.OffsetBeforeMs);
            }

            // il profilo largo su un gradino non salta, rampa: dove rampa la segmentazione ci vede
            // una retta e l'operazione sparisce. La scala fine la ritrova, ma vale come aggiunta
            foreach (EditOperationCandidate addition in this._rescuer.Detect(pair, profile, cancellation))
            {
                if (addition.DurationMs < EditAnalysisProfile.FINE_MIN_DURATION_MS || this.IsNearKnown(operations, addition.TimestampMs))
                    continue;
                ChangePointResult refined = this._refiner.Refine(pair, addition.PlateauEndBeforeMs - 2000.0, addition.PlateauStartAfterMs + 2000.0, addition.OffsetBeforeMs, addition.OffsetAfterMs);
                if (refined != null)
                    addition.TimestampMs = refined.NextAfterLastMs;
                operations.Add(addition);
            }
            operations.Sort((left, right) => left.TimestampMs.CompareTo(right.TimestampMs));

            foreach (EditOperationCandidate operation in operations)
            {
                cancellation.ThrowIfCancellationRequested();
                double timestampMs = this._audioBoundary.Resolve(pair.Source, envelopes, operation.TimestampMs, operation.OffsetBeforeMs);
                operation.Boundary = timestampMs < operation.TimestampMs ? BoundaryDecision.AudioInsideBlack : BoundaryDecision.ChangePoint;
                // dentro una dissolvenza l'hash non separa i due offset: quando la run c'è decide
                // lei, e né i fotogrammi esclusivi né il posticipo hanno voce in capitolo
                double? runStartMs = this._blackRunRules.FindRunStart(pair.Source, timestampMs);
                if (runStartMs.HasValue)
                {
                    operation.TimestampMs = runStartMs.Value;
                    operation.Boundary = BoundaryDecision.BlackRunStart;
                    continue;
                }
                double postponedMs = this._exclusiveRules.Postpone(pair, timestampMs, operation.OffsetBeforeMs, operation.OffsetAfterMs);
                if (postponedMs != timestampMs)
                    operation.Boundary = BoundaryDecision.ExclusiveFrame;
                operation.TimestampMs = postponedMs;
            }

            operations = this._durationMeasurer.Apply(pair, operations, cancellation);

            foreach (EditOperationCandidate operation in operations)
            {
                cancellation.ThrowIfCancellationRequested();
                operation.TimestampMs = this._blackRunRules.Contain(pair.Source, operation.TimestampMs, operation.DurationMs, out bool movedToRunEnd);
                if (movedToRunEnd)
                    operation.Boundary = BoundaryDecision.BlackRunEnd;
                if (this._blackRunRules.FindRunStart(pair.Source, operation.TimestampMs).HasValue)
                    continue;
                double extremeMs = this._exclusiveRules.LeftExtreme(pair, operation.TimestampMs, operation.OffsetBeforeMs, operation.OffsetAfterMs);
                if (extremeMs != operation.TimestampMs)
                    operation.Boundary = BoundaryDecision.AmbiguousExtreme;
                operation.TimestampMs = extremeMs;
            }

            AudioStepJudge audioJudge = new AudioStepJudge(envelopes);
            List<EditOperationCandidate> kept = new List<EditOperationCandidate>();
            List<EditOperationCandidate> rejected = new List<EditOperationCandidate>();
            foreach (EditOperationCandidate operation in operations)
            {
                cancellation.ThrowIfCancellationRequested();
                // un'operazione il cui salto è molto più piccolo dell'incertezza con cui si
                // misurano i suoi due offset non è misurata: è rumore
                if (operation.DurationMs < EditAnalysisProfile.CONFIDENCE_RATIO * operation.UncertaintyMs)
                {
                    operation.RejectReason = "salto sotto l'incertezza dei suoi offset";
                    rejected.Add(operation);
                    continue;
                }
                if (!audioJudge.Holds(operation))
                {
                    operation.RejectReason = "l'audio non vede lo stesso scalino";
                    rejected.Add(operation);
                    continue;
                }
                kept.Add(operation);
            }

            List<EditOperationCandidate> result = new List<EditOperationCandidate>();
            foreach (EditOperationCandidate operation in kept)
            {
                cancellation.ThrowIfCancellationRequested();
                if (this._plateauVerifier.Holds(pair, operation))
                {
                    result.Add(operation);
                    continue;
                }
                operation.RejectReason = "lo scalino non si rimisura sui pianori ai lati";
                rejected.Add(operation);
            }

            double firstBoundaryMs = result.Count > 0 ? result[0].TimestampMs : pair.Source.PtsMs[pair.Source.Count - 1];
            double initialOffsetMs = this._coverageVerifier.Anchor(pair, result, this._coverageVerifier.InitialOffset(pair, firstBoundaryMs));
            return new EditAnalysisOutcome
            {
                Operations = result,
                Rejected = rejected,
                InitialOffsetMs = initialOffsetMs,
                Coverage = this._coverageVerifier.Coverage(pair, result, initialOffsetMs),
                Profile = profile
            };
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Indica se un'operazione della scala fine descrive un confine già trovato dal profilo largo
        /// </summary>
        /// <param name="known">Operazioni già trovate</param>
        /// <param name="timestampMs">Confine proposto dalla scala fine</param>
        /// <returns>True quando è la stessa operazione</returns>
        private bool IsNearKnown(IReadOnlyList<EditOperationCandidate> known, double timestampMs)
        {
            foreach (EditOperationCandidate operation in known)
            {
                if (Math.Abs(timestampMs - operation.TimestampMs) <= EditAnalysisProfile.FINE_SAME_OPERATION_MS)
                    return true;
            }
            return false;
        }

        #endregion
    }
}
