using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Rifinisce i punti operativi audio dopo la verifica globale video
    /// </summary>
    public class DeepAudioOperationFineTuner
    {
        #region Costanti

        private const string STATUS_APPLIED = "Applied";
        private const string STATUS_SKIPPED_NO_AUDIO = "SkippedNoAudio";
        private const string STATUS_SKIPPED_AMBIGUOUS = "SkippedAmbiguous";
        private const string STATUS_SKIPPED_OUT_OF_WINDOW = "SkippedOutOfWindow";
        private const string STATUS_SKIPPED_NON_MONOTONIC = "SkippedNonMonotonic";
        private const string STATUS_SKIPPED_UNSUPPORTED = "SkippedUnsupportedOperation";
        private const string STATUS_SKIPPED_VISUAL_DARK_BOUNDARY = "SkippedVisualDarkBoundary";
        private const int LANGUAGE_FALLBACK_MAX_SHIFT_MS = 750;
        private const int ENERGY_VALLEY_MAX_SHIFT_MS = 750;
        private const int FRAME_CONFIRMED_MAX_SHIFT_MS = 250;
        private const string BOUNDARY_NONE = "none";
        private const string BOUNDARY_SILENCE_RUN_START = "silence-run-start";
        private const string BOUNDARY_ENERGY_VALLEY = "energy-valley";
        private const string REFERENCE_SOURCE = "source";
        private const string REFERENCE_LANGUAGE_FALLBACK = "language-fallback";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Configurazione DeepAnalysis
        /// </summary>
        private readonly DeepAnalysisConfig _config;

        /// <summary>
        /// Servizio per estrarre envelope audio locali
        /// </summary>
        private readonly DeepAudioEnvelopeService _audioEnvelopeService;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="config">Configurazione DeepAnalysis</param>
        /// <param name="audioEnvelopeService">Servizio envelope audio</param>
        public DeepAudioOperationFineTuner(DeepAnalysisConfig config, DeepAudioEnvelopeService audioEnvelopeService)
        {
            this._config = config;
            this._audioEnvelopeService = audioEnvelopeService;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Applica fine tuning audio alle operazioni già verificate dal video
        /// </summary>
        /// <param name="sourceFile">File source da usare come reference primaria</param>
        /// <param name="langFile">File language da usare come fallback</param>
        /// <param name="operations">Operazioni editmap</param>
        /// <param name="transitions">Diagnostica transizioni</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <param name="initialDelayMs">Delay iniziale applicato al mux finale</param>
        /// <param name="trackPolicy">Policy tracce DeepAnalysis</param>
        public void FineTune(string sourceFile, string langFile, List<EditOperation> operations, List<DeepAnalysisTransitionDiagnostic> transitions, double stretchRatio, int initialDelayMs, DeepAnalysisTrackPolicy trackPolicy)
        {
            bool[] transitionUsed;

            if (this._config == null || !this._config.AudioFineTuneEnabled || operations == null || operations.Count == 0)
                return;

            transitionUsed = transitions != null ? new bool[transitions.Count] : null;
            for (int i = 0; i < operations.Count; i++)
            {
                EditOperation operation = operations[i];
                DeepAnalysisTransitionDiagnostic transition = this.FindTransition(operation, transitions, transitionUsed);
                if (transition == null)
                    continue;

                this.FineTuneOperation(sourceFile, langFile, operations, i, operation, transition, stretchRatio, initialDelayMs, trackPolicy);
            }
        }

        /// <summary>
        /// Cerca un boundary audio in un envelope già estratto
        /// </summary>
        /// <param name="envelope">Envelope audio normalizzato</param>
        /// <param name="extractStartReferenceMs">Timestamp reference del primo sample envelope</param>
        /// <param name="expectedReferenceMs">Timestamp reference atteso prima del fine tuning</param>
        /// <param name="referenceIsSource">True se la reference è source</param>
        /// <param name="initialDelayMs">Delay iniziale applicato al mux finale</param>
        /// <param name="operationIndex">Indice operazione corrente</param>
        /// <param name="operations">Operazioni editmap ordinate</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <param name="renderedBeforeMs">Timestamp renderizzato prima del fine tuning</param>
        /// <param name="maxReferenceShiftMs">Shift massimo nella timeline reference</param>
        /// <param name="preferExpectedInsideLowEnergyRun">True se un expected già dentro una run low-energy deve essere preservato</param>
        /// <param name="boundary">Boundary trovato</param>
        /// <returns>True se è stato trovato un boundary utilizzabile</returns>
        public bool TryFindBoundaryFromEnvelope(double[] envelope, int extractStartReferenceMs, int expectedReferenceMs, bool referenceIsSource, int initialDelayMs, int operationIndex, List<EditOperation> operations, double stretchRatio, int renderedBeforeMs, int maxReferenceShiftMs, bool preferExpectedInsideLowEnergyRun, out AudioFineTuneBoundary boundary)
        {
            double[] smoothEnvelope;
            double threshold;

            boundary = null;
            if (envelope == null || envelope.Length < 5)
                return false;

            threshold = this.ComputeLowEnergyThreshold(envelope);
            if (this.TryFindSilenceRunBoundary(envelope, threshold, extractStartReferenceMs, expectedReferenceMs, referenceIsSource, initialDelayMs, operationIndex, operations, stretchRatio, renderedBeforeMs, maxReferenceShiftMs, preferExpectedInsideLowEnergyRun, out boundary))
                return true;

            smoothEnvelope = this.SmoothEnvelope(envelope);
            return this.TryFindEnergyValleyBoundary(smoothEnvelope, threshold, extractStartReferenceMs, expectedReferenceMs, referenceIsSource, initialDelayMs, operationIndex, operations, stretchRatio, renderedBeforeMs, maxReferenceShiftMs, out boundary);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Rifinisce una singola operazione
        /// </summary>
        /// <param name="sourceFile">File source da usare come reference primaria</param>
        /// <param name="langFile">File language da usare come fallback</param>
        /// <param name="operations">Operazioni editmap</param>
        /// <param name="operationIndex">Indice operazione corrente</param>
        /// <param name="operation">Operazione corrente</param>
        /// <param name="transition">Diagnostica transizione associata</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <param name="initialDelayMs">Delay iniziale applicato al mux finale</param>
        /// <param name="trackPolicy">Policy tracce DeepAnalysis</param>
        private void FineTuneOperation(string sourceFile, string langFile, List<EditOperation> operations, int operationIndex, EditOperation operation, DeepAnalysisTransitionDiagnostic transition, double stretchRatio, int initialDelayMs, DeepAnalysisTrackPolicy trackPolicy)
        {
            int renderedBeforeMs;
            int cumulativeDeltaBeforeMs;
            int windowMs;
            bool referenceIsSource;
            string referenceKind;
            string referenceFile;
            string referenceTrackName;
            int referenceStreamIndex;
            int centerReferenceMs;
            int referenceWindowMs;
            int maxReferenceShiftMs;
            int extractStartReferenceMs;
            int extractEndReferenceMs;
            double extractStartSec;
            double extractDurationSec;
            double[] envelope;
            AudioFineTuneBoundary boundary;
            int shiftMs;

            if (operation.VisualSourceTimestampMs <= 0)
                operation.VisualSourceTimestampMs = operation.SourceTimestampMs;

            renderedBeforeMs = EditMapTimelineHelper.LanguageTimestampToRenderedTimestampMs(operation.LangTimestampMs, operations, operationIndex, stretchRatio);
            cumulativeDeltaBeforeMs = EditMapTimelineHelper.GetRenderedDeltaBeforeMs(operations, operationIndex, stretchRatio);
            windowMs = this.ResolveWindowMs();
            this.InitializeDiagnostic(transition, operation, renderedBeforeMs, cumulativeDeltaBeforeMs, windowMs);

            if (this.IsVisualDarkBoundaryLocked(operation, transition))
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_VISUAL_DARK_BOUNDARY, "Boundary visuale dark verificato mantenuto all'inizio della run nera", BOUNDARY_NONE);
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Transizione " + transition.Index.ToString(CultureInfo.InvariantCulture) + ": audio fine-tune saltato, boundary visuale dark verificato bloccato a source " + (operation.VisualSourceTimestampMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s");
                return;
            }

            if (!this.IsOperationEnabled(operation.Type))
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_UNSUPPORTED, "Tipo operazione non abilitato per fine tuning audio", BOUNDARY_NONE);
                return;
            }

            if (operation.DurationMs <= 0)
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_AMBIGUOUS, "Durata operazione non valida", BOUNDARY_NONE);
                return;
            }

            if (this._audioEnvelopeService == null)
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_NO_AUDIO, "servizio envelope audio non disponibile", BOUNDARY_NONE);
                return;
            }

            referenceIsSource = false;
            referenceKind = "";
            referenceFile = "";
            referenceTrackName = "";
            referenceStreamIndex = 0;
            centerReferenceMs = 0;
            referenceWindowMs = windowMs;
            maxReferenceShiftMs = this.ResolveMaxShiftMs();

            if (trackPolicy != null && trackPolicy.SourceFineTuneAudioAvailable && !string.IsNullOrEmpty(sourceFile))
            {
                referenceIsSource = true;
                referenceKind = REFERENCE_SOURCE;
                referenceFile = sourceFile;
                referenceTrackName = trackPolicy.SourceFineTuneTrackName;
                referenceStreamIndex = trackPolicy.SourceFineTuneAudioStreamIndex;
                centerReferenceMs = operation.VisualSourceTimestampMs > 0 ? operation.VisualSourceTimestampMs : operation.SourceTimestampMs;
            }
            else if (trackPolicy != null && trackPolicy.LanguageFineTuneAudioAvailable && !string.IsNullOrEmpty(langFile))
            {
                referenceKind = REFERENCE_LANGUAGE_FALLBACK;
                referenceFile = langFile;
                referenceTrackName = trackPolicy.LanguageFineTuneTrackName;
                referenceStreamIndex = trackPolicy.LanguageFineTuneAudioStreamIndex;
                centerReferenceMs = operation.LangTimestampMs;
                referenceWindowMs = this.ResolveLanguageWindowMs(windowMs, stretchRatio);
                maxReferenceShiftMs = Math.Min(this.ResolveMaxShiftMs(), LANGUAGE_FALLBACK_MAX_SHIFT_MS);
            }
            else
            {
                string sourceReason = trackPolicy != null && !string.IsNullOrEmpty(trackPolicy.SourceFineTuneRejectReason) ? trackPolicy.SourceFineTuneRejectReason : "source audio non disponibile";
                string languageReason = trackPolicy != null && !string.IsNullOrEmpty(trackPolicy.LanguageFineTuneRejectReason) ? trackPolicy.LanguageFineTuneRejectReason : "language audio non disponibile";
                this.MarkSkipped(transition, STATUS_SKIPPED_NO_AUDIO, sourceReason + "; " + languageReason, BOUNDARY_NONE);
                return;
            }

            transition.AudioFineTuneReferenceKind = referenceKind;
            transition.AudioFineTuneReferenceTrackName = referenceTrackName;
            transition.AudioFineTuneReferenceStreamIndex = referenceStreamIndex;
            transition.AudioFineTuneReferenceOriginalTimestampMs = centerReferenceMs;
            transition.AudioFineTuneReferenceSnappedTimestampMs = centerReferenceMs;

            extractStartReferenceMs = Math.Max(0, centerReferenceMs - referenceWindowMs);
            extractEndReferenceMs = centerReferenceMs + referenceWindowMs;
            if (extractEndReferenceMs <= extractStartReferenceMs)
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_AMBIGUOUS, "Finestra audio reference non valida", BOUNDARY_NONE);
                return;
            }

            extractStartSec = extractStartReferenceMs / 1000.0;
            extractDurationSec = (extractEndReferenceMs - extractStartReferenceMs) / 1000.0;
            envelope = this._audioEnvelopeService.Extract(referenceFile, extractStartSec, extractDurationSec, this.ResolveEnvelopeWindowMs(), referenceStreamIndex);
            if (envelope == null)
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_NO_AUDIO, "Envelope audio " + referenceKind + " non disponibile", BOUNDARY_NONE);
                return;
            }

            if (!this.TryFindBoundaryFromEnvelope(envelope, extractStartReferenceMs, centerReferenceMs, referenceIsSource, initialDelayMs, operationIndex, operations, stretchRatio, renderedBeforeMs, maxReferenceShiftMs, referenceIsSource && string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal), out boundary))
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_AMBIGUOUS, "Nessun silenzio o valle affidabile nella reference audio " + referenceKind, BOUNDARY_NONE);
                return;
            }

            shiftMs = boundary.RenderedTimestampMs - renderedBeforeMs;
            if (this.IsVisualBoundaryFrameConfirmed(operation, transition) && Math.Abs(boundary.ReferenceTimestampMs - centerReferenceMs) > FRAME_CONFIRMED_MAX_SHIFT_MS)
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_OUT_OF_WINDOW, "Boundary audio troppo distante dal boundary visuale frame-confirmed", boundary.Kind);
                return;
            }

            if (Math.Abs(shiftMs) > this.ResolveMaxShiftMs())
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_OUT_OF_WINDOW, "Boundary audio fuori shift massimo", boundary.Kind);
                return;
            }

            if (!referenceIsSource && Math.Abs(boundary.ReferenceTimestampMs - centerReferenceMs) > maxReferenceShiftMs)
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_OUT_OF_WINDOW, "Boundary audio fallback language fuori shift massimo", boundary.Kind);
                return;
            }

            if (!this.IsCandidateMonotonic(operations, operationIndex, operation, boundary.LanguageTimestampMs))
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_NON_MONOTONIC, "Boundary audio attraversa operazioni adiacenti", boundary.Kind);
                return;
            }

            operation.LangTimestampMs = boundary.LanguageTimestampMs;
            operation.SourceTimestampMs = boundary.SourceTimestampMs;
            transition.LangTimestampMs = operation.LangTimestampMs;
            transition.SourceTimestampMs = operation.SourceTimestampMs;
            transition.AudioFineTuneStatus = STATUS_APPLIED;
            transition.AudioFineTuneRejectReason = "";
            transition.AudioFineTuneBoundaryKind = boundary.Kind;
            transition.AudioFineTuneConfidence = boundary.Confidence;
            transition.AudioFineTuneSnappedLangTimestampMs = operation.LangTimestampMs;
            transition.AudioFineTuneSnappedSourceTimestampMs = operation.SourceTimestampMs;
            transition.AudioFineTuneRenderedAfterMs = boundary.RenderedTimestampMs;
            transition.AudioFineTuneShiftMs = shiftMs;
            transition.AudioFineTuneReferenceSnappedTimestampMs = boundary.ReferenceTimestampMs;

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Transizione " + transition.Index.ToString(CultureInfo.InvariantCulture) + ": audio fine-tune " + operation.Type + " reference=" + referenceKind + " track=\"" + referenceTrackName + "\" source " + (transition.AudioFineTuneOriginalSourceTimestampMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s -> " + (operation.SourceTimestampMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s, rendered " + (renderedBeforeMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s -> " + (boundary.RenderedTimestampMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s, lang " + (transition.AudioFineTuneOriginalLangTimestampMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s -> " + (operation.LangTimestampMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s, shift=" + shiftMs.ToString(CultureInfo.InvariantCulture) + "ms, boundary=" + boundary.Kind);
        }

        /// <summary>
        /// Inizializza diagnostica fine tuning per una transizione
        /// </summary>
        /// <param name="transition">Diagnostica transizione</param>
        /// <param name="operation">Operazione corrente</param>
        /// <param name="renderedBeforeMs">Timestamp renderizzato prima del fine tuning</param>
        /// <param name="cumulativeDeltaBeforeMs">Delta cumulativo operazioni precedenti</param>
        /// <param name="windowMs">Finestra fine tuning in millisecondi</param>
        private void InitializeDiagnostic(DeepAnalysisTransitionDiagnostic transition, EditOperation operation, int renderedBeforeMs, int cumulativeDeltaBeforeMs, int windowMs)
        {
            transition.AudioFineTuneStatus = "NotRun";
            transition.AudioFineTuneRejectReason = "";
            transition.AudioFineTuneBoundaryKind = BOUNDARY_NONE;
            transition.AudioFineTuneReferenceKind = "";
            transition.AudioFineTuneReferenceTrackName = "";
            transition.AudioFineTuneReferenceStreamIndex = 0;
            transition.AudioFineTuneReferenceOriginalTimestampMs = 0;
            transition.AudioFineTuneReferenceSnappedTimestampMs = 0;
            transition.AudioFineTuneConfidence = 0.0;
            transition.AudioFineTuneOriginalLangTimestampMs = operation.LangTimestampMs;
            transition.AudioFineTuneSnappedLangTimestampMs = operation.LangTimestampMs;
            transition.AudioFineTuneOriginalSourceTimestampMs = operation.SourceTimestampMs;
            transition.AudioFineTuneSnappedSourceTimestampMs = operation.SourceTimestampMs;
            transition.AudioFineTuneVisualSourceTimestampMs = operation.VisualSourceTimestampMs;
            transition.AudioFineTuneRenderedBeforeMs = renderedBeforeMs;
            transition.AudioFineTuneRenderedAfterMs = renderedBeforeMs;
            transition.AudioFineTuneShiftMs = 0;
            transition.AudioFineTuneWindowStartMs = renderedBeforeMs - windowMs;
            transition.AudioFineTuneWindowEndMs = renderedBeforeMs + windowMs;
            transition.AudioFineTuneCumulativeDeltaBeforeMs = cumulativeDeltaBeforeMs;
        }

        /// <summary>
        /// Imposta diagnostica skip
        /// </summary>
        /// <param name="transition">Diagnostica transizione</param>
        /// <param name="status">Stato fine tuning</param>
        /// <param name="reason">Motivo skip</param>
        /// <param name="boundaryKind">Tipo boundary valutato</param>
        private void MarkSkipped(DeepAnalysisTransitionDiagnostic transition, string status, string reason, string boundaryKind)
        {
            transition.AudioFineTuneStatus = status;
            transition.AudioFineTuneRejectReason = reason != null ? reason : "";
            transition.AudioFineTuneBoundaryKind = !string.IsNullOrEmpty(boundaryKind) ? boundaryKind : BOUNDARY_NONE;
        }

        /// <summary>
        /// Verifica se un boundary visuale dark verificato deve restare autoritativo
        /// </summary>
        /// <param name="operation">Operazione editmap</param>
        /// <param name="transition">Diagnostica transizione associata</param>
        /// <returns>True se il fine tuning audio non deve spostare il boundary</returns>
        private bool IsVisualDarkBoundaryLocked(EditOperation operation, DeepAnalysisTransitionDiagnostic transition)
        {
            if (operation == null || transition == null || transition.Candidates == null || operation.VisualSourceTimestampMs <= 0)
                return false;

            double visualSourceSec = operation.VisualSourceTimestampMs / 1000.0;
            for (int i = 0; i < transition.Candidates.Count; i++)
            {
                DeepAnalysisTransitionCandidateDiagnostic candidate = transition.Candidates[i];
                if (candidate == null || Math.Abs(candidate.SourceSec - visualSourceSec) > 0.05)
                    continue;

                if (candidate.DarkBoundaryRunFrames < 2 || candidate.DarkBoundaryIntervalDarkRatio < 0.75)
                    continue;

                if (string.Equals(candidate.Decision, "accepted-timeline-dark-duration", StringComparison.Ordinal))
                    return true;

                if (candidate.DarkBoundaryRewritten && candidate.Verified)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Verifica se il boundary visuale è confermato direttamente dalla sequenza dei frame
        /// </summary>
        /// <param name="operation">Operazione editmap</param>
        /// <param name="transition">Diagnostica transizione associata</param>
        /// <returns>True se l'audio può applicare soltanto uno shift locale</returns>
        private bool IsVisualBoundaryFrameConfirmed(EditOperation operation, DeepAnalysisTransitionDiagnostic transition)
        {
            if (operation == null || transition == null || transition.Candidates == null || operation.VisualSourceTimestampMs <= 0)
                return false;

            double visualSourceSec = operation.VisualSourceTimestampMs / 1000.0;
            for (int i = 0; i < transition.Candidates.Count; i++)
            {
                DeepAnalysisTransitionCandidateDiagnostic candidate = transition.Candidates[i];
                if (candidate == null || Math.Abs(candidate.SourceSec - visualSourceSec) > 0.05)
                    continue;

                if (string.Equals(candidate.Decision, "accepted-insert-unmatched-boundary", StringComparison.Ordinal) ||
                    string.Equals(candidate.Decision, "accepted-insert-mse-motion-boundary", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Cerca la transizione diagnostica associata a una operazione
        /// </summary>
        /// <param name="operation">Operazione corrente</param>
        /// <param name="transitions">Transizioni diagnostiche</param>
        /// <param name="transitionUsed">Mappa transizioni già usate</param>
        /// <returns>Transizione associata o null</returns>
        private DeepAnalysisTransitionDiagnostic FindTransition(EditOperation operation, List<DeepAnalysisTransitionDiagnostic> transitions, bool[] transitionUsed)
        {
            int visualSourceMs;
            DeepAnalysisTransitionDiagnostic bestFallback = null;
            int bestFallbackIndex = -1;

            if (operation == null || transitions == null || transitionUsed == null)
                return null;

            visualSourceMs = operation.VisualSourceTimestampMs > 0 ? operation.VisualSourceTimestampMs : operation.SourceTimestampMs;
            for (int i = 0; i < transitions.Count; i++)
            {
                DeepAnalysisTransitionDiagnostic transition = transitions[i];
                if (transitionUsed[i] || transition == null || !string.Equals(transition.OperationType, operation.Type, StringComparison.Ordinal))
                    continue;

                if (Math.Abs(transition.SourceTimestampMs - visualSourceMs) <= 2 && Math.Abs(transition.LangTimestampMs - operation.LangTimestampMs) <= 2)
                {
                    transitionUsed[i] = true;
                    return transition;
                }

                if (bestFallback == null && Math.Abs(transition.SourceTimestampMs - visualSourceMs) <= 250)
                {
                    bestFallback = transition;
                    bestFallbackIndex = i;
                }
            }

            if (bestFallback != null && bestFallbackIndex >= 0)
                transitionUsed[bestFallbackIndex] = true;

            return bestFallback;
        }

        /// <summary>
        /// Cerca un boundary basato su run di bassa energia
        /// </summary>
        /// <param name="envelope">Envelope audio normalizzato</param>
        /// <param name="threshold">Soglia low-energy</param>
        /// <param name="extractStartReferenceMs">Timestamp reference del primo sample envelope</param>
        /// <param name="expectedReferenceMs">Timestamp reference atteso</param>
        /// <param name="referenceIsSource">True se la reference è source</param>
        /// <param name="initialDelayMs">Delay iniziale applicato al mux finale</param>
        /// <param name="operationIndex">Indice operazione corrente</param>
        /// <param name="operations">Operazioni editmap</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <param name="renderedBeforeMs">Timestamp renderizzato prima del fine tuning</param>
        /// <param name="maxReferenceShiftMs">Shift massimo nella timeline reference</param>
        /// <param name="preferExpectedInsideLowEnergyRun">True se un expected già dentro una run low-energy deve essere preservato</param>
        /// <param name="boundary">Boundary trovato</param>
        /// <returns>True se è stato trovato un boundary utilizzabile</returns>
        private bool TryFindSilenceRunBoundary(double[] envelope, double threshold, int extractStartReferenceMs, int expectedReferenceMs, bool referenceIsSource, int initialDelayMs, int operationIndex, List<EditOperation> operations, double stretchRatio, int renderedBeforeMs, int maxReferenceShiftMs, bool preferExpectedInsideLowEnergyRun, out AudioFineTuneBoundary boundary)
        {
            bool result = false;
            int minRunSamples = Math.Max(1, this.ResolveMinSilenceMs() / this.ResolveEnvelopeWindowMs());
            int expectedIndex = (expectedReferenceMs - extractStartReferenceMs) / this.ResolveEnvelopeWindowMs();
            double bestScore = double.MaxValue;
            AudioFineTuneBoundary bestBoundary = null;
            int i = 0;

            boundary = null;
            while (i < envelope.Length)
            {
                if (envelope[i] > threshold)
                {
                    i++;
                    continue;
                }

                int runStart = i;
                while (i < envelope.Length && envelope[i] <= threshold)
                    i++;

                int runEnd = i;
                if (runEnd - runStart < minRunSamples)
                    continue;

                int boundaryIndex = runStart;
                if (preferExpectedInsideLowEnergyRun && expectedIndex >= runStart && expectedIndex < runEnd)
                    boundaryIndex = expectedIndex;

                if (boundaryIndex <= 1 || boundaryIndex >= envelope.Length - 2)
                    continue;

                AudioFineTuneBoundary candidate = this.CreateBoundaryCandidate(boundaryIndex, extractStartReferenceMs, expectedReferenceMs, referenceIsSource, initialDelayMs, operationIndex, operations, stretchRatio, renderedBeforeMs, maxReferenceShiftMs, envelope, threshold, runStart, runEnd, BOUNDARY_SILENCE_RUN_START);
                if (candidate == null)
                    continue;

                double score = Math.Abs(candidate.ReferenceTimestampMs - expectedReferenceMs) - (candidate.Confidence * 500.0);
                if (score < bestScore)
                {
                    bestScore = score;
                    bestBoundary = candidate;
                    result = true;
                }
            }

            boundary = bestBoundary;
            return result;
        }

        /// <summary>
        /// Cerca una valle locale quando non esiste una run di silenzio piena
        /// </summary>
        /// <param name="envelope">Envelope audio normalizzato</param>
        /// <param name="threshold">Soglia low-energy</param>
        /// <param name="extractStartReferenceMs">Timestamp reference del primo sample envelope</param>
        /// <param name="expectedReferenceMs">Timestamp reference atteso</param>
        /// <param name="referenceIsSource">True se la reference è source</param>
        /// <param name="initialDelayMs">Delay iniziale applicato al mux finale</param>
        /// <param name="operationIndex">Indice operazione corrente</param>
        /// <param name="operations">Operazioni editmap</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <param name="renderedBeforeMs">Timestamp renderizzato prima del fine tuning</param>
        /// <param name="maxReferenceShiftMs">Shift massimo nella timeline reference</param>
        /// <param name="boundary">Boundary trovato</param>
        /// <returns>True se è stato trovato un boundary utilizzabile</returns>
        private bool TryFindEnergyValleyBoundary(double[] envelope, double threshold, int extractStartReferenceMs, int expectedReferenceMs, bool referenceIsSource, int initialDelayMs, int operationIndex, List<EditOperation> operations, double stretchRatio, int renderedBeforeMs, int maxReferenceShiftMs, out AudioFineTuneBoundary boundary)
        {
            int centerIndex = (expectedReferenceMs - extractStartReferenceMs) / this.ResolveEnvelopeWindowMs();
            int maxShiftSamples = Math.Max(1, maxReferenceShiftMs / this.ResolveEnvelopeWindowMs());
            int startIndex;
            int endIndex;
            int bestIndex = -1;
            double bestValue = double.MaxValue;

            boundary = null;
            if (centerIndex < 0)
                centerIndex = 0;

            if (centerIndex >= envelope.Length)
                centerIndex = envelope.Length - 1;

            startIndex = Math.Max(1, centerIndex - maxShiftSamples);
            endIndex = Math.Min(envelope.Length - 2, centerIndex + maxShiftSamples);

            for (int i = startIndex; i <= endIndex; i++)
            {
                if (envelope[i] < bestValue)
                {
                    bestValue = envelope[i];
                    bestIndex = i;
                }
            }

            if (bestIndex < 0 || bestValue > threshold * 1.10)
                return false;

            boundary = this.CreateBoundaryCandidate(bestIndex, extractStartReferenceMs, expectedReferenceMs, referenceIsSource, initialDelayMs, operationIndex, operations, stretchRatio, renderedBeforeMs, maxReferenceShiftMs, envelope, threshold, bestIndex, bestIndex + 1, BOUNDARY_ENERGY_VALLEY);
            return boundary != null;
        }

        /// <summary>
        /// Crea un candidato boundary convertendo la reference in source, rendered e lang
        /// </summary>
        /// <param name="boundaryIndex">Indice sample boundary nell'envelope</param>
        /// <param name="extractStartReferenceMs">Timestamp reference del primo sample envelope</param>
        /// <param name="expectedReferenceMs">Timestamp reference atteso</param>
        /// <param name="referenceIsSource">True se la reference è source</param>
        /// <param name="initialDelayMs">Delay iniziale applicato al mux finale</param>
        /// <param name="operationIndex">Indice operazione corrente</param>
        /// <param name="operations">Operazioni editmap</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <param name="renderedBeforeMs">Timestamp renderizzato prima del fine tuning</param>
        /// <param name="maxReferenceShiftMs">Shift massimo nella timeline reference</param>
        /// <param name="envelope">Envelope audio normalizzato</param>
        /// <param name="threshold">Soglia low-energy</param>
        /// <param name="runStart">Inizio run usata per la confidenza</param>
        /// <param name="runEnd">Fine run usata per la confidenza</param>
        /// <param name="boundaryKind">Tipo boundary</param>
        /// <returns>Candidato boundary o null</returns>
        private AudioFineTuneBoundary CreateBoundaryCandidate(int boundaryIndex, int extractStartReferenceMs, int expectedReferenceMs, bool referenceIsSource, int initialDelayMs, int operationIndex, List<EditOperation> operations, double stretchRatio, int renderedBeforeMs, int maxReferenceShiftMs, double[] envelope, double threshold, int runStart, int runEnd, string boundaryKind)
        {
            int referenceTimestampMs = extractStartReferenceMs + (boundaryIndex * this.ResolveEnvelopeWindowMs());
            int languageTimestampMs;
            int renderedTimestampMs;
            int sourceTimestampMs;

            if (Math.Abs(referenceTimestampMs - expectedReferenceMs) > maxReferenceShiftMs)
                return null;

            if (string.Equals(boundaryKind, BOUNDARY_ENERGY_VALLEY, StringComparison.Ordinal) && Math.Abs(referenceTimestampMs - expectedReferenceMs) > ENERGY_VALLEY_MAX_SHIFT_MS)
                return null;

            if (referenceIsSource)
            {
                sourceTimestampMs = referenceTimestampMs;
                renderedTimestampMs = sourceTimestampMs - initialDelayMs;
                if (renderedTimestampMs < 0)
                    renderedTimestampMs = 0;

                languageTimestampMs = EditMapTimelineHelper.RenderedTimestampToLanguageTimestampMs(renderedTimestampMs, operations, operationIndex, stretchRatio);
            }
            else
            {
                languageTimestampMs = referenceTimestampMs;
                renderedTimestampMs = EditMapTimelineHelper.LanguageTimestampToRenderedTimestampMs(languageTimestampMs, operations, operationIndex, stretchRatio);
                sourceTimestampMs = renderedTimestampMs + initialDelayMs;
                if (sourceTimestampMs < 0)
                    sourceTimestampMs = 0;
            }

            AudioFineTuneBoundary result = new AudioFineTuneBoundary();
            result.ReferenceTimestampMs = referenceTimestampMs;
            result.LanguageTimestampMs = languageTimestampMs;
            result.SourceTimestampMs = sourceTimestampMs;
            result.RenderedTimestampMs = renderedTimestampMs;
            result.Kind = boundaryKind;
            result.Confidence = this.ComputeBoundaryConfidence(envelope, threshold, runStart, runEnd);
            return result;
        }

        /// <summary>
        /// Controlla che il nuovo punto non attraversi operazioni vicine
        /// </summary>
        /// <param name="operations">Operazioni editmap ordinate</param>
        /// <param name="operationIndex">Indice operazione corrente</param>
        /// <param name="operation">Operazione corrente</param>
        /// <param name="snappedLanguageMs">Nuovo timestamp lang candidato</param>
        /// <returns>True se il candidato resta monotono</returns>
        private bool IsCandidateMonotonic(List<EditOperation> operations, int operationIndex, EditOperation operation, int snappedLanguageMs)
        {
            int candidateEndMs = snappedLanguageMs;
            if (string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                candidateEndMs = snappedLanguageMs + operation.DurationMs;

            if (operationIndex > 0)
            {
                EditOperation previous = operations[operationIndex - 1];
                int previousEndMs = previous.LangTimestampMs;
                if (string.Equals(previous.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                    previousEndMs += previous.DurationMs;

                if (snappedLanguageMs < previousEndMs)
                    return false;
            }

            if (operationIndex < operations.Count - 1)
            {
                EditOperation next = operations[operationIndex + 1];
                if (candidateEndMs > next.LangTimestampMs)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Smussa envelope con media mobile breve
        /// </summary>
        /// <param name="envelope">Envelope originale</param>
        /// <returns>Envelope smussato</returns>
        private double[] SmoothEnvelope(double[] envelope)
        {
            double[] result = new double[envelope.Length];
            for (int i = 0; i < envelope.Length; i++)
            {
                int start = Math.Max(0, i - 2);
                int end = Math.Min(envelope.Length - 1, i + 2);
                double sum = 0.0;
                int count = 0;
                for (int j = start; j <= end; j++)
                {
                    sum += envelope[j];
                    count++;
                }

                result[i] = count > 0 ? sum / count : envelope[i];
            }

            return result;
        }

        /// <summary>
        /// Calcola soglia low-energy adattiva
        /// </summary>
        /// <param name="envelope">Envelope audio normalizzato</param>
        /// <returns>Soglia low-energy</returns>
        private double ComputeLowEnergyThreshold(double[] envelope)
        {
            double p05 = this.Percentile(envelope, 0.05);
            double p75 = this.Percentile(envelope, 0.75);
            double threshold;
            if (p75 <= p05)
            {
                threshold = p05 * 1.25;
            }
            else
            {
                threshold = p05 + ((p75 - p05) * 0.22);
            }

            if (threshold < 0.0015)
                threshold = 0.0015;

            return threshold;
        }

        /// <summary>
        /// Calcola confidenza del boundary su separazione energia e durata run
        /// </summary>
        /// <param name="envelope">Envelope audio normalizzato</param>
        /// <param name="threshold">Soglia low-energy</param>
        /// <param name="runStart">Inizio run</param>
        /// <param name="runEnd">Fine run</param>
        /// <returns>Confidenza normalizzata 0-1</returns>
        private double ComputeBoundaryConfidence(double[] envelope, double threshold, int runStart, int runEnd)
        {
            double p75 = this.Percentile(envelope, 0.75);
            double separation = p75 > 0.000001 ? (p75 - threshold) / p75 : 0.0;
            double runMs = Math.Max(1, runEnd - runStart) * this.ResolveEnvelopeWindowMs();
            double runScore = Math.Min(1.0, runMs / Math.Max(1.0, this.ResolveMinSilenceMs() * 2.0));
            double result = (separation * 0.7) + (runScore * 0.3);
            if (result < 0.0)
                result = 0.0;

            if (result > 1.0)
                result = 1.0;

            return result;
        }

        /// <summary>
        /// Calcola percentile su una copia ordinata
        /// </summary>
        /// <param name="values">Valori da ordinare</param>
        /// <param name="percentile">Percentile richiesto 0-1</param>
        /// <returns>Valore percentile</returns>
        private double Percentile(double[] values, double percentile)
        {
            double[] sorted = new double[values.Length];
            Array.Copy(values, sorted, values.Length);
            Array.Sort(sorted);
            int index = (int)Math.Round((sorted.Length - 1) * percentile);
            if (index < 0)
                index = 0;

            if (index >= sorted.Length)
                index = sorted.Length - 1;

            return sorted[index];
        }

        /// <summary>
        /// Verifica se il tipo operazione è abilitato da config
        /// </summary>
        /// <param name="operationType">Tipo operazione</param>
        /// <returns>True se il fine tuning è abilitato per il tipo</returns>
        private bool IsOperationEnabled(string operationType)
        {
            string configured = this._config.AudioFineTuneOperationTypes != null ? this._config.AudioFineTuneOperationTypes.ToLowerInvariant() : "";
            if (configured.IndexOf("all", StringComparison.Ordinal) >= 0)
                return true;

            if (string.Equals(operationType, EditOperation.INSERT_SILENCE, StringComparison.Ordinal))
                return configured.IndexOf("insert", StringComparison.Ordinal) >= 0;

            if (string.Equals(operationType, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                return configured.IndexOf("cut", StringComparison.Ordinal) >= 0;

            return false;
        }

        /// <summary>
        /// Risolve finestra fine tuning renderizzata
        /// </summary>
        /// <returns>Finestra in millisecondi</returns>
        private int ResolveWindowMs()
        {
            return this._config.AudioFineTuneWindowMs > 0 ? this._config.AudioFineTuneWindowMs : 3500;
        }

        /// <summary>
        /// Risolve shift massimo renderizzato
        /// </summary>
        /// <returns>Shift massimo in millisecondi</returns>
        private int ResolveMaxShiftMs()
        {
            return this._config.AudioFineTuneMaxShiftMs > 0 ? this._config.AudioFineTuneMaxShiftMs : this.ResolveWindowMs();
        }

        /// <summary>
        /// Risolve finestra envelope
        /// </summary>
        /// <returns>Finestra envelope in millisecondi</returns>
        private int ResolveEnvelopeWindowMs()
        {
            return this._config.AudioFineTuneEnvelopeWindowMs > 0 ? this._config.AudioFineTuneEnvelopeWindowMs : 25;
        }

        /// <summary>
        /// Risolve durata minima silenzio
        /// </summary>
        /// <returns>Durata minima in millisecondi</returns>
        private int ResolveMinSilenceMs()
        {
            return this._config.AudioFineTuneMinSilenceMs > 0 ? this._config.AudioFineTuneMinSilenceMs : 150;
        }

        /// <summary>
        /// Converte la finestra renderizzata in finestra lang
        /// </summary>
        /// <param name="renderedWindowMs">Finestra renderizzata in millisecondi</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <returns>Finestra lang in millisecondi</returns>
        private int ResolveLanguageWindowMs(int renderedWindowMs, double stretchRatio)
        {
            if (stretchRatio <= 0.0)
                return renderedWindowMs;

            return Math.Max(1, (int)Math.Ceiling(renderedWindowMs / stretchRatio));
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Boundary audio candidato
        /// </summary>
        public class AudioFineTuneBoundary
        {
            /// <summary>
            /// Timestamp nella timeline reference usata
            /// </summary>
            public int ReferenceTimestampMs { get; set; }

            /// <summary>
            /// Timestamp language originale
            /// </summary>
            public int LanguageTimestampMs { get; set; }

            /// <summary>
            /// Timestamp source operativo
            /// </summary>
            public int SourceTimestampMs { get; set; }

            /// <summary>
            /// Timestamp renderizzato
            /// </summary>
            public int RenderedTimestampMs { get; set; }

            /// <summary>
            /// Tipo boundary
            /// </summary>
            public string Kind { get; set; }

            /// <summary>
            /// Confidenza boundary
            /// </summary>
            public double Confidence { get; set; }
        }

        #endregion
    }
}
