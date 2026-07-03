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
        private const string BOUNDARY_NONE = "none";
        private const string BOUNDARY_SILENCE_RUN_END = "silence-run-end";
        private const string BOUNDARY_SILENCE_RUN_START = "silence-run-start";
        private const string BOUNDARY_ENERGY_VALLEY = "energy-valley";

        #endregion

        #region Variabili di classe

        private readonly DeepAnalysisConfig _config;

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
        /// <param name="langFile">File language da analizzare</param>
        /// <param name="operations">Operazioni editmap</param>
        /// <param name="transitions">Diagnostica transizioni</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <param name="initialDelayMs">Delay iniziale applicato al mux finale</param>
        /// <param name="languageAudioAvailable">True se la traccia audio lang è disponibile</param>
        /// <param name="languageAudioStreamIndex">Indice ffmpeg della traccia audio lang</param>
        /// <param name="noAudioReason">Motivo indisponibilità audio</param>
        public void FineTune(string langFile, List<EditOperation> operations, List<DeepAnalysisTransitionDiagnostic> transitions, double stretchRatio, int initialDelayMs, bool languageAudioAvailable, int languageAudioStreamIndex, string noAudioReason)
        {
            bool[] transitionUsed;

            if (this._config == null || !this._config.AudioFineTuneEnabled || operations == null || operations.Count == 0)
            {
                return;
            }

            transitionUsed = transitions != null ? new bool[transitions.Count] : null;
            for (int i = 0; i < operations.Count; i++)
            {
                EditOperation operation = operations[i];
                DeepAnalysisTransitionDiagnostic transition = this.FindTransition(operation, transitions, transitionUsed);
                if (transition == null)
                {
                    continue;
                }

                this.FineTuneOperation(langFile, operations, i, operation, transition, stretchRatio, initialDelayMs, languageAudioAvailable, languageAudioStreamIndex, noAudioReason);
            }
        }

        /// <summary>
        /// Cerca un boundary audio in un envelope già estratto
        /// </summary>
        /// <param name="envelope">Envelope audio normalizzato</param>
        /// <param name="extractStartLanguageMs">Timestamp language del primo sample envelope</param>
        /// <param name="operation">Operazione corrente</param>
        /// <param name="operationIndex">Indice operazione corrente</param>
        /// <param name="operations">Operazioni editmap ordinate</param>
        /// <param name="stretchRatio">Rapporto stretch applicato alla timeline lang</param>
        /// <param name="renderedBeforeMs">Timestamp renderizzato prima del fine tuning</param>
        /// <param name="boundary">Boundary trovato</param>
        /// <returns>True se è stato trovato un boundary utilizzabile</returns>
        public bool TryFindBoundaryFromEnvelope(double[] envelope, int extractStartLanguageMs, EditOperation operation, int operationIndex, List<EditOperation> operations, double stretchRatio, int renderedBeforeMs, out AudioFineTuneBoundary boundary)
        {
            double[] smoothEnvelope;
            double threshold;

            boundary = null;
            if (envelope == null || envelope.Length < 5 || operation == null)
            {
                return false;
            }

            smoothEnvelope = this.SmoothEnvelope(envelope);
            threshold = this.ComputeLowEnergyThreshold(smoothEnvelope);
            if (this.TryFindSilenceRunBoundary(smoothEnvelope, threshold, extractStartLanguageMs, operation, operationIndex, operations, stretchRatio, renderedBeforeMs, out boundary))
            {
                return true;
            }

            return this.TryFindEnergyValleyBoundary(smoothEnvelope, threshold, extractStartLanguageMs, operation, operationIndex, operations, stretchRatio, renderedBeforeMs, out boundary);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Rifinisce una singola operazione
        /// </summary>
        private void FineTuneOperation(string langFile, List<EditOperation> operations, int operationIndex, EditOperation operation, DeepAnalysisTransitionDiagnostic transition, double stretchRatio, int initialDelayMs, bool languageAudioAvailable, int languageAudioStreamIndex, string noAudioReason)
        {
            int renderedBeforeMs;
            int cumulativeDeltaBeforeMs;
            int windowMs;
            int languageWindowMs;
            int extractStartLanguageMs;
            int extractEndLanguageMs;
            double extractStartSec;
            double extractDurationSec;
            double[] envelope;
            AudioFineTuneBoundary boundary;
            int snappedSourceMs;
            int shiftMs;

            if (operation.VisualSourceTimestampMs <= 0)
            {
                operation.VisualSourceTimestampMs = operation.SourceTimestampMs;
            }

            renderedBeforeMs = EditMapTimelineHelper.LanguageTimestampToRenderedTimestampMs(operation.LangTimestampMs, operations, operationIndex, stretchRatio);
            cumulativeDeltaBeforeMs = EditMapTimelineHelper.GetRenderedDeltaBeforeMs(operations, operationIndex, stretchRatio);
            windowMs = this.ResolveWindowMs();
            this.InitializeDiagnostic(transition, operation, renderedBeforeMs, cumulativeDeltaBeforeMs, windowMs);

            if (!this.IsOperationEnabled(operation.Type))
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_UNSUPPORTED, "Tipo operazione non abilitato per fine tuning audio", BOUNDARY_NONE);
                return;
            }

            if (!languageAudioAvailable || this._audioEnvelopeService == null || string.IsNullOrEmpty(langFile))
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_NO_AUDIO, noAudioReason != null && noAudioReason.Length > 0 ? noAudioReason : "traccia audio language non disponibile", BOUNDARY_NONE);
                return;
            }

            if (operation.DurationMs <= 0)
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_AMBIGUOUS, "Durata operazione non valida", BOUNDARY_NONE);
                return;
            }

            languageWindowMs = this.ResolveLanguageWindowMs(windowMs, stretchRatio);
            extractStartLanguageMs = Math.Max(0, operation.LangTimestampMs - languageWindowMs);
            extractEndLanguageMs = operation.LangTimestampMs + languageWindowMs;
            if (extractEndLanguageMs <= extractStartLanguageMs)
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_AMBIGUOUS, "Finestra audio language non valida", BOUNDARY_NONE);
                return;
            }

            extractStartSec = extractStartLanguageMs / 1000.0;
            extractDurationSec = (extractEndLanguageMs - extractStartLanguageMs) / 1000.0;
            envelope = this._audioEnvelopeService.Extract(langFile, extractStartSec, extractDurationSec, this.ResolveEnvelopeWindowMs(), languageAudioStreamIndex);
            if (envelope == null)
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_NO_AUDIO, "Envelope audio language non disponibile", BOUNDARY_NONE);
                return;
            }

            if (!this.TryFindBoundaryFromEnvelope(envelope, extractStartLanguageMs, operation, operationIndex, operations, stretchRatio, renderedBeforeMs, out boundary))
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_AMBIGUOUS, "Nessun silenzio o valle affidabile nella finestra audio", BOUNDARY_NONE);
                return;
            }

            shiftMs = boundary.RenderedTimestampMs - renderedBeforeMs;
            if (Math.Abs(shiftMs) > this.ResolveMaxShiftMs())
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_OUT_OF_WINDOW, "Boundary audio fuori shift massimo", boundary.Kind);
                return;
            }

            if (!this.IsCandidateMonotonic(operations, operationIndex, operation, boundary.LanguageTimestampMs))
            {
                this.MarkSkipped(transition, STATUS_SKIPPED_NON_MONOTONIC, "Boundary audio attraversa operazioni adiacenti", boundary.Kind);
                return;
            }

            snappedSourceMs = boundary.RenderedTimestampMs + initialDelayMs;
            if (snappedSourceMs < 0)
            {
                snappedSourceMs = 0;
            }

            operation.LangTimestampMs = boundary.LanguageTimestampMs;
            operation.SourceTimestampMs = snappedSourceMs;
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

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Transizione " + transition.Index.ToString(CultureInfo.InvariantCulture) + ": audio fine-tune " + operation.Type + " @ rendered " + (renderedBeforeMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s -> " + (boundary.RenderedTimestampMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s, lang " + (transition.AudioFineTuneOriginalLangTimestampMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s -> " + (operation.LangTimestampMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s, shift=" + shiftMs.ToString(CultureInfo.InvariantCulture) + "ms, boundary=" + boundary.Kind);
        }

        /// <summary>
        /// Inizializza diagnostica fine tuning per una transizione
        /// </summary>
        private void InitializeDiagnostic(DeepAnalysisTransitionDiagnostic transition, EditOperation operation, int renderedBeforeMs, int cumulativeDeltaBeforeMs, int windowMs)
        {
            transition.AudioFineTuneStatus = "NotRun";
            transition.AudioFineTuneRejectReason = "";
            transition.AudioFineTuneBoundaryKind = BOUNDARY_NONE;
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
        private void MarkSkipped(DeepAnalysisTransitionDiagnostic transition, string status, string reason, string boundaryKind)
        {
            transition.AudioFineTuneStatus = status;
            transition.AudioFineTuneRejectReason = reason != null ? reason : "";
            transition.AudioFineTuneBoundaryKind = boundaryKind != null && boundaryKind.Length > 0 ? boundaryKind : BOUNDARY_NONE;
        }

        /// <summary>
        /// Cerca la transizione diagnostica associata a una operazione
        /// </summary>
        private DeepAnalysisTransitionDiagnostic FindTransition(EditOperation operation, List<DeepAnalysisTransitionDiagnostic> transitions, bool[] transitionUsed)
        {
            int visualSourceMs;
            DeepAnalysisTransitionDiagnostic bestFallback = null;
            int bestFallbackIndex = -1;

            if (operation == null || transitions == null || transitionUsed == null)
            {
                return null;
            }

            visualSourceMs = operation.VisualSourceTimestampMs > 0 ? operation.VisualSourceTimestampMs : operation.SourceTimestampMs;
            for (int i = 0; i < transitions.Count; i++)
            {
                DeepAnalysisTransitionDiagnostic transition = transitions[i];
                if (transitionUsed[i] || transition == null || !string.Equals(transition.OperationType, operation.Type, StringComparison.Ordinal))
                {
                    continue;
                }

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
            {
                transitionUsed[bestFallbackIndex] = true;
            }

            return bestFallback;
        }

        /// <summary>
        /// Cerca un boundary basato su run di bassa energia
        /// </summary>
        private bool TryFindSilenceRunBoundary(double[] envelope, double threshold, int extractStartLanguageMs, EditOperation operation, int operationIndex, List<EditOperation> operations, double stretchRatio, int renderedBeforeMs, out AudioFineTuneBoundary boundary)
        {
            bool result = false;
            int minRunSamples = Math.Max(1, this.ResolveMinSilenceMs() / this.ResolveEnvelopeWindowMs());
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
                {
                    i++;
                }

                int runEnd = i;
                if (runEnd - runStart < minRunSamples)
                {
                    continue;
                }

                int boundaryIndex = string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal) ? runEnd : runStart;
                if (boundaryIndex <= 1 || boundaryIndex >= envelope.Length - 2)
                {
                    continue;
                }

                string boundaryKind = string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal) ? BOUNDARY_SILENCE_RUN_END : BOUNDARY_SILENCE_RUN_START;
                AudioFineTuneBoundary candidate = this.CreateBoundaryCandidate(boundaryIndex, extractStartLanguageMs, operationIndex, operations, stretchRatio, renderedBeforeMs, envelope, threshold, runStart, runEnd, boundaryKind);
                if (candidate == null)
                {
                    continue;
                }

                double score = Math.Abs(candidate.RenderedTimestampMs - renderedBeforeMs) - (candidate.Confidence * 500.0);
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
        private bool TryFindEnergyValleyBoundary(double[] envelope, double threshold, int extractStartLanguageMs, EditOperation operation, int operationIndex, List<EditOperation> operations, double stretchRatio, int renderedBeforeMs, out AudioFineTuneBoundary boundary)
        {
            int centerLanguageMs = operation.LangTimestampMs;
            int centerIndex = (centerLanguageMs - extractStartLanguageMs) / this.ResolveEnvelopeWindowMs();
            int maxShiftSamples = Math.Max(1, this.ResolveMaxShiftMs() / this.ResolveEnvelopeWindowMs());
            int startIndex;
            int endIndex;
            int bestIndex = -1;
            double bestValue = double.MaxValue;

            boundary = null;
            if (centerIndex < 0) { centerIndex = 0; }
            if (centerIndex >= envelope.Length) { centerIndex = envelope.Length - 1; }
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
            {
                return false;
            }

            boundary = this.CreateBoundaryCandidate(bestIndex, extractStartLanguageMs, operationIndex, operations, stretchRatio, renderedBeforeMs, envelope, threshold, bestIndex, bestIndex + 1, BOUNDARY_ENERGY_VALLEY);
            return boundary != null;
        }

        /// <summary>
        /// Crea un candidato boundary convertendo language in rendered
        /// </summary>
        private AudioFineTuneBoundary CreateBoundaryCandidate(int boundaryIndex, int extractStartLanguageMs, int operationIndex, List<EditOperation> operations, double stretchRatio, int renderedBeforeMs, double[] envelope, double threshold, int runStart, int runEnd, string boundaryKind)
        {
            int languageTimestampMs = extractStartLanguageMs + (boundaryIndex * this.ResolveEnvelopeWindowMs());
            int renderedTimestampMs = EditMapTimelineHelper.LanguageTimestampToRenderedTimestampMs(languageTimestampMs, operations, operationIndex, stretchRatio);
            int shiftMs = renderedTimestampMs - renderedBeforeMs;
            if (Math.Abs(shiftMs) > this.ResolveWindowMs())
            {
                return null;
            }

            AudioFineTuneBoundary result = new AudioFineTuneBoundary();
            result.LanguageTimestampMs = languageTimestampMs;
            result.RenderedTimestampMs = renderedTimestampMs;
            result.Kind = boundaryKind;
            result.Confidence = this.ComputeBoundaryConfidence(envelope, threshold, runStart, runEnd);
            return result;
        }

        /// <summary>
        /// Controlla che il nuovo punto non attraversi operazioni vicine
        /// </summary>
        private bool IsCandidateMonotonic(List<EditOperation> operations, int operationIndex, EditOperation operation, int snappedLanguageMs)
        {
            int candidateEndMs = snappedLanguageMs;
            if (string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
            {
                candidateEndMs = snappedLanguageMs + operation.DurationMs;
            }

            if (operationIndex > 0)
            {
                EditOperation previous = operations[operationIndex - 1];
                int previousEndMs = previous.LangTimestampMs;
                if (string.Equals(previous.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                {
                    previousEndMs += previous.DurationMs;
                }

                if (snappedLanguageMs < previousEndMs)
                {
                    return false;
                }
            }

            if (operationIndex < operations.Count - 1)
            {
                EditOperation next = operations[operationIndex + 1];
                if (candidateEndMs > next.LangTimestampMs)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Smussa envelope con media mobile breve
        /// </summary>
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
            {
                threshold = 0.0015;
            }

            return threshold;
        }

        /// <summary>
        /// Calcola confidenza del boundary su separazione energia e durata run
        /// </summary>
        private double ComputeBoundaryConfidence(double[] envelope, double threshold, int runStart, int runEnd)
        {
            double p75 = this.Percentile(envelope, 0.75);
            double separation = p75 > 0.000001 ? (p75 - threshold) / p75 : 0.0;
            double runMs = Math.Max(1, runEnd - runStart) * this.ResolveEnvelopeWindowMs();
            double runScore = Math.Min(1.0, runMs / Math.Max(1.0, this.ResolveMinSilenceMs() * 2.0));
            double result = (separation * 0.7) + (runScore * 0.3);
            if (result < 0.0) { result = 0.0; }
            if (result > 1.0) { result = 1.0; }
            return result;
        }

        /// <summary>
        /// Calcola percentile su una copia ordinata
        /// </summary>
        private double Percentile(double[] values, double percentile)
        {
            double[] sorted = new double[values.Length];
            Array.Copy(values, sorted, values.Length);
            Array.Sort(sorted);
            int index = (int)Math.Round((sorted.Length - 1) * percentile);
            if (index < 0) { index = 0; }
            if (index >= sorted.Length) { index = sorted.Length - 1; }
            return sorted[index];
        }

        /// <summary>
        /// Verifica se il tipo operazione è abilitato da config
        /// </summary>
        private bool IsOperationEnabled(string operationType)
        {
            string configured = this._config.AudioFineTuneOperationTypes != null ? this._config.AudioFineTuneOperationTypes.ToLowerInvariant() : "";
            if (configured.IndexOf("all", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            if (string.Equals(operationType, EditOperation.INSERT_SILENCE, StringComparison.Ordinal))
            {
                return configured.IndexOf("insert", StringComparison.Ordinal) >= 0;
            }

            if (string.Equals(operationType, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
            {
                return configured.IndexOf("cut", StringComparison.Ordinal) >= 0;
            }

            return false;
        }

        /// <summary>
        /// Risolve finestra fine tuning renderizzata
        /// </summary>
        private int ResolveWindowMs()
        {
            return this._config.AudioFineTuneWindowMs > 0 ? this._config.AudioFineTuneWindowMs : 3500;
        }

        /// <summary>
        /// Risolve shift massimo renderizzato
        /// </summary>
        private int ResolveMaxShiftMs()
        {
            return this._config.AudioFineTuneMaxShiftMs > 0 ? this._config.AudioFineTuneMaxShiftMs : this.ResolveWindowMs();
        }

        /// <summary>
        /// Risolve finestra envelope
        /// </summary>
        private int ResolveEnvelopeWindowMs()
        {
            return this._config.AudioFineTuneEnvelopeWindowMs > 0 ? this._config.AudioFineTuneEnvelopeWindowMs : 25;
        }

        /// <summary>
        /// Risolve durata minima silenzio
        /// </summary>
        private int ResolveMinSilenceMs()
        {
            return this._config.AudioFineTuneMinSilenceMs > 0 ? this._config.AudioFineTuneMinSilenceMs : 150;
        }

        /// <summary>
        /// Converte la finestra renderizzata in finestra lang
        /// </summary>
        private int ResolveLanguageWindowMs(int renderedWindowMs, double stretchRatio)
        {
            if (stretchRatio <= 0.0)
            {
                return renderedWindowMs;
            }

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
            /// Timestamp language originale
            /// </summary>
            public int LanguageTimestampMs { get; set; }

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
