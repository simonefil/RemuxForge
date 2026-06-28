using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Servizio di analisi avanzata per file con edit diversi tra source e lang
    /// Rileva offset multi-segmento e produce una EditMap per il riallineamento
    /// </summary>
    public class DeepAnalysisService : VideoSyncServiceBase
    {
        #region Costanti

        private const double INITIAL_VISUAL_GUARD_FPS = 1.0;
        private const int INITIAL_VISUAL_GUARD_SEARCH_STEP_MS = 500;
        private const int INITIAL_VISUAL_GUARD_LOCAL_DISTINCT_OFFSET_MS = 1000;
        private const int INITIAL_VISUAL_GUARD_DISTINCT_OFFSET_MS = 5000;
        private const double INITIAL_VISUAL_GUARD_MAX_REGION_SEC = 60.0;
        private const double INITIAL_VISUAL_GUARD_TIMELINE_LEAD_IN_SEC = 20.0;
        private const int INITIAL_VISUAL_GUARD_TIMELINE_START_PADDING_MS = 500;
        private const double INITIAL_VISUAL_GUARD_STRONG_MSE = 3500.0;
        private const double INITIAL_VISUAL_GUARD_STRONG_MARGIN = 0.08;
        private const double INITIAL_VISUAL_GUARD_LOCAL_MARGIN = 0.04;
        private const double INITIAL_VISUAL_GUARD_RELATIVE_IMPROVEMENT = 0.60;
        private const double INITIAL_VISUAL_GUARD_TIMELINE_IMPROVEMENT = 0.75;
        private const double INITIAL_VISUAL_GUARD_BORDER_MARGIN = 0.12;
        private const double LOCAL_MSE_RATIO_EPSILON = 1.0;
        private const double LOCAL_AUDIO_VERIFY_WINDOW_SEC = 6.0;
        private const int LOCAL_AUDIO_VERIFY_WINDOW_MS = 50;
        private const int LOCAL_AUDIO_VERIFY_MIN_WINDOWS = 40;
        private const double LOCAL_AUDIO_VERIFY_MIN_SCORE = 0.62;
        private const double LOCAL_AUDIO_VERIFY_MIN_MARGIN = 0.06;
        private const double LOCAL_AUDIO_REJECT_MIN_SCORE = 0.70;
        private const double LOCAL_AUDIO_REJECT_MIN_MARGIN = 0.12;
        private const double LOCAL_AUDIO_VERIFY_BEFORE_TOLERANCE = 0.04;
        private const double LOCAL_FORWARD_STRONG_RATIO = 1.50;
        private const double LOCAL_TIMELINE_CUT_FORWARD_SEC = 30.0;
        private const double LOCAL_SSIM_CLEAR_MARGIN = 0.015;
        private const double LOCAL_SSIM_TIE_MARGIN = 0.005;
        private const double TAIL_GAP_MIN_UNSUPPORTED_SEC = 180.0;
        private const int TAIL_GAP_MIN_DELTA_MS = 5000;
        private const double TAIL_GAP_SCAN_STEP_SEC = 30.0;
        private const double TAIL_GAP_SCAN_MARGIN_SEC = 60.0;
        private const double TAIL_GAP_MIN_IMPROVEMENT = 1.45;
        private const double TAIL_GAP_FRAME_SEARCH_MARGIN_SEC = 60.0;
        private const int TAIL_GAP_FRAME_CONFIRM_FRAMES = 6;
        private const int TAIL_GAP_FRAME_MIN_VOTES = 4;
        private const double TAIL_GAP_FRAME_MATCH_SSIM = 0.72;
        private const double TAIL_GAP_FRAME_MISMATCH_SSIM = 0.55;
        private const int INITIAL_DELAY_DIRECT_OFFSET_MAX_MS = 1000;
        private const int INITIAL_EDIT_MIN_DURATION_MS = 100;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Riferimento alla configurazione DeepAnalysis (binding diretto, modifiche immediate)
        /// </summary>
        private readonly DeepAnalysisConfig _daConfig;

        /// <summary>
        /// Tempo di esecuzione analisi in ms
        /// </summary>
        private long _analysisTimeMs;

        /// <summary>
        /// Contatori prestazionali dell'analisi corrente
        /// </summary>
        private DeepAnalysisPerformanceDiagnostic _performanceDiagnostics;

        /// <summary>
        /// Lock per aggiornare i contatori prestazionali durante estrazioni parallele
        /// </summary>
        private readonly object _performanceDiagnosticsLock;

        /// <summary>
        /// Refiner transizioni DeepAnalysis
        /// </summary>
        private readonly DeepTransitionRefiner _transitionRefiner;

        /// <summary>
        /// Servizio envelope audio locale
        /// </summary>
        private readonly DeepAudioEnvelopeService _audioEnvelopeService;

        /// <summary>
        /// Analyzer visuale frame-based DeepAnalysis
        /// </summary>
        private readonly DeepVisualFrameAnalyzer _visualFrameAnalyzer;

        /// <summary>
        /// Verificatore globale DeepAnalysis
        /// </summary>
        private readonly DeepGlobalVerifier _globalVerifier;

        /// <summary>
        /// Risolutore centralizzato per mkvmerge e tool esterni
        /// </summary>
        private readonly ToolPathResolverService _toolPathResolver;

        /// <summary>
        /// True se l'analisi corrente usa la mappa timeline-first
        /// </summary>
        private bool _currentAnalysisUsesTimelineMap;

        /// <summary>
        /// Policy tracce audio consentite per la validazione locale
        /// </summary>
        private DeepAnalysisTrackPolicy _currentTrackPolicy;

        /// <summary>
        /// File source dell'analisi corrente
        /// </summary>
        private string _currentAnalysisSourceFile;

        /// <summary>
        /// File lang dell'analisi corrente
        /// </summary>
        private string _currentAnalysisLanguageFile;

        /// <summary>
        /// Ultima mappa diagnostica prodotta, anche in caso di fallimento
        /// </summary>
        private EditMap _lastAnalysisMap;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso eseguibile ffmpeg</param>
        /// <param name="toolPathResolver">Risolutore percorsi tool esterni</param>
        public DeepAnalysisService(string ffmpegPath, ToolPathResolverService toolPathResolver = null) : base(ffmpegPath, LogSection.Deep)
        {
            this._daConfig = AppSettingsService.Instance.Settings.Advanced.DeepAnalysis;
            this._analysisTimeMs = 0;
            this._performanceDiagnostics = new DeepAnalysisPerformanceDiagnostic();
            this._performanceDiagnosticsLock = new object();
            this._toolPathResolver = toolPathResolver ?? new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder);
            this._audioEnvelopeService = new DeepAudioEnvelopeService(this._ffmpegPath, this.RecordAudioEnvelopeExtract);
            this._visualFrameAnalyzer = new DeepVisualFrameAnalyzer(this._daConfig, this.ExtractDeepSegment, this.ComputeSsim, this.ComputeMse);
            this._transitionRefiner = new DeepTransitionRefiner(this.GetTransitionRefineRadiusSec, this.DifferentialScanCrossover, this._visualFrameAnalyzer, this.LinearScanConfirm, this.VerifyTransitionLocal);
            this._globalVerifier = new DeepGlobalVerifier(this._daConfig, this._vsConfig, this._visualFrameAnalyzer);
            this._currentAnalysisUsesTimelineMap = false;
            this._currentTrackPolicy = new DeepAnalysisTrackPolicy();
            this._currentAnalysisSourceFile = "";
            this._currentAnalysisLanguageFile = "";
            this._lastAnalysisMap = null;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Analizza source e lang per produrre una EditMap con policy stretch esplicita
        /// </summary>
        /// <param name="sourceFile">Percorso file source</param>
        /// <param name="langFile">Percorso file lang</param>
        /// <param name="sourceDefaultDurationNs">Default duration traccia video source in nanosecondi</param>
        /// <param name="langDefaultDurationNs">Default duration traccia video lang in nanosecondi</param>
        /// <param name="sourceDurationMs">Durata container source in millisecondi</param>
        /// <param name="manualStretchFactor">Stretch factor manuale, vuoto se assente</param>
        /// <param name="allowAutoStretch">True per consentire stretch automatico da metadata gia' validati</param>
        /// <param name="trackPolicy">Policy delle tracce audio ammesse come validazione</param>
        /// <returns>EditMap con operazioni di edit, null se analisi fallita</returns>
        public EditMap Analyze(string sourceFile, string langFile, long sourceDefaultDurationNs, long langDefaultDurationNs, int sourceDurationMs, string manualStretchFactor, bool allowAutoStretch, DeepAnalysisTrackPolicy trackPolicy)
        {
            EditMap result = null;
            Stopwatch stopwatch = new Stopwatch();
            double stretchRatio;
            double inverseRatio;
            string stretchFactor;
            List<OffsetRegion> regions = null;
            List<EditOperation> operations = null;
            bool verified;
            double baselineMse = 0.0;
            int acceptedAnchors;
            long phaseStartMs;
            DeepAnalysisDiagnostics diagnostics = new DeepAnalysisDiagnostics();
            DeepAnalysisGlobalVerificationDiagnostic globalVerification;
            List<DeepAnalysisTransitionDiagnostic> transitions;
            DeepAnalysisInitialAlignmentDiagnostic initialAlignment;
            DeepTimelineMapResult timelineMap;
            int visualStartOffsetMs;
            double visualStartEndSec;
            double visualStartMse;
            double visualStartSecondMse;
            bool visualStartConfirmed;
            bool leadingInitialEditCreated;
            int initialDelayMs;
            string mkvMergePath;
            stopwatch.Start();
            this._lastAnalysisMap = null;
            lock (this._performanceDiagnosticsLock)
            {
                this._performanceDiagnostics = diagnostics.Performance;
            }
            this._currentAnalysisUsesTimelineMap = false;
            this._currentTrackPolicy = trackPolicy ?? new DeepAnalysisTrackPolicy();
            this._currentAnalysisSourceFile = sourceFile;
            this._currentAnalysisLanguageFile = langFile;
            diagnostics.ManualStretchRequested = manualStretchFactor != null ? manualStretchFactor : "";
            diagnostics.AllowAutoStretch = allowAutoStretch;
            this.PrepareGeometryDrivenCrop(sourceFile, langFile);
            diagnostics.SourceGeometry = this._lastSourceGeometryInfo;
            diagnostics.LanguageGeometry = this._lastLanguageGeometryInfo;

            // Fase 1: Rilevamento stretch globale
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Fase 1: Rilevamento stretch...");
            ConsoleHelper.Progress(LogSection.Deep, 14, "Deep: stretch");
            phaseStartMs = stopwatch.ElapsedMilliseconds;
            if (!this.DetectStretch(sourceDefaultDurationNs, langDefaultDurationNs, manualStretchFactor, allowAutoStretch, out stretchRatio, out inverseRatio, out stretchFactor))
            {
                stopwatch.Stop();
                this._analysisTimeMs = stopwatch.ElapsedMilliseconds;
                return result;
            }
            diagnostics.Timing.StretchMs = stopwatch.ElapsedMilliseconds - phaseStartMs;
            diagnostics.StretchRatio = stretchRatio;
            diagnostics.InverseRatio = inverseRatio;
            diagnostics.StretchFactor = stretchFactor;

            // Fase 2: Timeline-first audio/video
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Fase 2: Mappa timeline...");
            ConsoleHelper.Progress(LogSection.Deep, 25, "Deep: timeline");
            phaseStartMs = stopwatch.ElapsedMilliseconds;
            mkvMergePath = this._toolPathResolver.ResolveMkvMergePath(false);
            if (mkvMergePath.Length == 0)
            {
                mkvMergePath = AppSettingsService.Instance.Settings.Tools.MkvMergePath;
            }

            DeepTimelineAnchorMapper timelineAnchorMapper = new DeepTimelineAnchorMapper(mkvMergePath, this.BuildVisualTimelineAnchor);
            timelineMap = timelineAnchorMapper.Build(sourceFile, langFile, sourceDurationMs, inverseRatio);
            diagnostics.Timing.TimelineMapMs = stopwatch.ElapsedMilliseconds - phaseStartMs;
            if (timelineMap != null && timelineMap.Diagnostic != null)
            {
                diagnostics.TimelineMap = timelineMap.Diagnostic;
            }

            if (timelineMap == null || !timelineMap.Success || timelineMap.Regions == null || timelineMap.Regions.Count == 0)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  Timeline fallita: " + (timelineMap != null ? timelineMap.RejectReason : "risultato nullo"));
                this.StoreFailedAnalysis(stopwatch, diagnostics, stretchFactor, regions, operations, baselineMse);
                return result;
            }

            this._currentAnalysisUsesTimelineMap = true;
            if (this._currentTrackPolicy.AudioValidationAvailable)
            {
                timelineMap.Diagnostic.TrackLanguage = this._currentTrackPolicy.TrackLanguage;
                timelineMap.Diagnostic.SourceAudioStreamIndex = this._currentTrackPolicy.SourceAudioStreamIndex;
                timelineMap.Diagnostic.LanguageAudioStreamIndex = this._currentTrackPolicy.LanguageAudioStreamIndex;
                timelineMap.Diagnostic.SourceTrackName = this._currentTrackPolicy.SourceTrackName;
                timelineMap.Diagnostic.LanguageTrackName = this._currentTrackPolicy.LanguageTrackName;
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Audio validator: " + this._currentTrackPolicy.TrackLanguage + " source stream " + this._currentTrackPolicy.SourceAudioStreamIndex.ToString(CultureInfo.InvariantCulture) + ", lang stream " + this._currentTrackPolicy.LanguageAudioStreamIndex.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Audio validator non disponibile: " + this._currentTrackPolicy.RejectReason);
            }

            regions = timelineMap.Regions;
            this.RecoverUnsupportedTailGap(sourceFile, langFile, sourceDurationMs, inverseRatio, regions, timelineMap);
            this.ApplyInitialVisualGuard(sourceFile, langFile, sourceDurationMs, regions, inverseRatio, out visualStartOffsetMs, out visualStartEndSec, out visualStartMse, out visualStartSecondMse, out visualStartConfirmed);

            acceptedAnchors = timelineMap.Diagnostic.AcceptedAnchorCount;
            initialAlignment = new DeepAnalysisInitialAlignmentDiagnostic();
            initialAlignment.SceneCandidateAvailable = visualStartConfirmed;
            initialAlignment.SceneOffsetMs = visualStartConfirmed ? visualStartOffsetMs : 0;
            initialAlignment.SceneVotes = visualStartConfirmed ? (int)Math.Round(visualStartEndSec) : 0;
            initialAlignment.SelectedSource = visualStartConfirmed ? "timeline+visual-start-guard" : "timeline";
            initialAlignment.SelectedOffsetMs = (int)Math.Round(regions[0].OffsetMs);
            initialAlignment.DecisionReason = visualStartConfirmed ? "timeline-first vincolata dal match video iniziale (mse=" + visualStartMse.ToString("F1", CultureInfo.InvariantCulture) + ", second=" + visualStartSecondMse.ToString("F1", CultureInfo.InvariantCulture) + ")" : "timeline-first video anchor map";
            diagnostics.InitialAlignment = initialAlignment;
            diagnostics.Regions = new List<DeepAnalysisRegionDiagnostic>();
            for (int i = 0; i < regions.Count; i++)
            {
                DeepAnalysisRegionDiagnostic regionDiagnostic = new DeepAnalysisRegionDiagnostic();
                regionDiagnostic.Index = i + 1;
                regionDiagnostic.StartSrcSec = regions[i].StartSrcSec;
                regionDiagnostic.EndSrcSec = regions[i].EndSrcSec;
                regionDiagnostic.OffsetMs = regions[i].OffsetMs;
                regionDiagnostic.MatchCount = regions[i].MatchCount;
                diagnostics.Regions.Add(regionDiagnostic);
            }
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Timeline accettata: " + regions.Count.ToString(CultureInfo.InvariantCulture) + " regioni, " + acceptedAnchors.ToString(CultureInfo.InvariantCulture) + " anchor");

            // Fase 3: Raffinamento transizioni
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Fase 3: Raffinamento transizioni...");
            ConsoleHelper.Progress(LogSection.Deep, 70, "Deep: transizioni");
            phaseStartMs = stopwatch.ElapsedMilliseconds;
            operations = this._transitionRefiner.Refine(sourceFile, langFile, regions, inverseRatio, diagnostics.Performance, this._currentAnalysisUsesTimelineMap, this._geometryCropSourceToFourThree, this._geometryCropLanguageToFourThree, out transitions);

            diagnostics.Timing.TransitionRefineMs = stopwatch.ElapsedMilliseconds - phaseStartMs;
            diagnostics.Transitions = transitions;

            if (!this.ValidateTimelineTransitions(transitions, true))
            {
                this.StoreFailedAnalysis(stopwatch, diagnostics, stretchFactor, regions, operations, baselineMse);
                return result;
            }
            initialDelayMs = this.ResolveInitialDelayAndLeadingOperation(regions, operations, inverseRatio, visualStartConfirmed, out leadingInitialEditCreated);
            if (leadingInitialEditCreated)
            {
                diagnostics.InitialAlignment.SelectedOffsetMs = initialDelayMs;
                diagnostics.InitialAlignment.DecisionReason = diagnostics.InitialAlignment.DecisionReason + "; offset primo plateau convertito in operazione iniziale";
            }

            // Fase 4: Verifica globale
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Fase 4: Verifica globale...");
            ConsoleHelper.Progress(LogSection.Deep, 82, "Deep: verifica globale");
            phaseStartMs = stopwatch.ElapsedMilliseconds;
            verified = this._globalVerifier.Verify(sourceFile, langFile, regions, operations, inverseRatio, initialDelayMs, sourceDurationMs, this._geometryCropSourceToFourThree, this._geometryCropLanguageToFourThree, out baselineMse, out globalVerification);
            diagnostics.Timing.GlobalVerifyMs = stopwatch.ElapsedMilliseconds - phaseStartMs;
            diagnostics.GlobalVerification = globalVerification;

            if (!verified)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  Verifica globale fallita");
                this.StoreFailedAnalysis(stopwatch, diagnostics, stretchFactor, regions, operations, baselineMse);
                return result;
            }

            // Costruisci EditMap
            stopwatch.Stop();
            this._analysisTimeMs = stopwatch.ElapsedMilliseconds;
            diagnostics.Timing.TotalMs = this._analysisTimeMs;

            result = new EditMap();
            result.InitialDelayMs = initialDelayMs;
            result.StretchFactor = stretchFactor;
            result.Operations = operations;
            result.AnalysisTimeMs = this._analysisTimeMs;
            result.BaselineMse = baselineMse;
            result.Diagnostics = diagnostics;
            this._lastAnalysisMap = result;

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Success, "  EditMap: delay=" + result.InitialDelayMs + "ms, " + operations.Count + " operazioni, analisi " + this._analysisTimeMs + "ms");
            ConsoleHelper.Progress(LogSection.Deep, 88, "Deep: edit map");

            return result;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Tempo di esecuzione analisi in ms
        /// </summary>
        public long AnalysisTimeMs { get { return this._analysisTimeMs; } }

        /// <summary>
        /// Ultima mappa prodotta, inclusa diagnostica su fallimento
        /// </summary>
        public EditMap LastAnalysisMap { get { return this._lastAnalysisMap; } }

        #endregion

        #region Metodi privati — Fase 2: Timeline

        /// <summary>
        /// Decide se il primo offset e' un vero delay mux o un edit iniziale da materializzare nella timeline lang.
        /// </summary>
        /// <param name="regions">Regioni offset rilevate</param>
        /// <param name="operations">Operazioni EditMap gia' raffinate</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="visualStartConfirmed">True se lo start guard visuale ha confermato l'offset iniziale</param>
        /// <param name="leadingInitialEditCreated">True se e' stata aggiunta una operazione iniziale</param>
        /// <returns>Delay iniziale da applicare con mkvmerge</returns>
        private int ResolveInitialDelayAndLeadingOperation(List<OffsetRegion> regions, List<EditOperation> operations, double inverseRatio, bool visualStartConfirmed, out bool leadingInitialEditCreated)
        {
            int initialOffsetMs;
            int leadingDurationMs;
            EditOperation leadingOperation;
            leadingInitialEditCreated = false;

            if (regions == null || regions.Count == 0)
            {
                return 0;
            }

            initialOffsetMs = (int)Math.Round(regions[0].OffsetMs);
            if (Math.Abs(initialOffsetMs) <= INITIAL_DELAY_DIRECT_OFFSET_MAX_MS)
            {
                return initialOffsetMs;
            }

            if (visualStartConfirmed || operations == null || operations.Count == 0)
            {
                return initialOffsetMs;
            }

            leadingDurationMs = EditMapTimelineHelper.SourceDurationToLanguageDurationMs(Math.Abs(initialOffsetMs), inverseRatio);
            if (leadingDurationMs < INITIAL_EDIT_MIN_DURATION_MS)
            {
                return initialOffsetMs;
            }

            /*
             * Il primo plateau puo' essere il primo contenuto comune dopo un edit iniziale.
             * In quel caso usarlo come delay mux sposta anche cue e audio precedenti.
             */
            leadingOperation = new EditOperation();
            leadingOperation.Type = initialOffsetMs > 0 ? EditOperation.INSERT_SILENCE : EditOperation.CUT_SEGMENT;
            leadingOperation.LangTimestampMs = 0;
            leadingOperation.DurationMs = leadingDurationMs;
            leadingOperation.SourceTimestampMs = 0;
            operations.Insert(0, leadingOperation);
            leadingInitialEditCreated = true;

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Initial offset " + initialOffsetMs.ToString(CultureInfo.InvariantCulture) + "ms convertito in " + leadingOperation.Type + " iniziale da " + leadingDurationMs.ToString(CultureInfo.InvariantCulture) + "ms lang");
            return 0;
        }

        /// <summary>
        /// Rileva stretch globale da parametro manuale o default duration delle tracce video
        /// </summary>
        /// <param name="sourceDefaultDurationNs">Default duration source in nanosecondi</param>
        /// <param name="langDefaultDurationNs">Default duration lang in nanosecondi</param>
        /// <param name="manualStretchFactor">Stretch manuale richiesto</param>
        /// <param name="allowAutoStretch">True se puo' usare metadata video</param>
        /// <param name="stretchRatio">Ratio stretch source/lang</param>
        /// <param name="inverseRatio">Ratio inverso da applicare al language</param>
        /// <param name="stretchFactor">Fattore stretch normalizzato per mkvmerge</param>
        /// <returns>True se lo stretch e' valido</returns>
        private bool DetectStretch(long sourceDefaultDurationNs, long langDefaultDurationNs, string manualStretchFactor, bool allowAutoStretch, out double stretchRatio, out double inverseRatio, out string stretchFactor)
        {
            double sourceFps;
            double langFps;
            double ratioDiff;
            string normalizedManualFactor;
            stretchRatio = 1.0;
            inverseRatio = 1.0;
            stretchFactor = "";

            if (manualStretchFactor != null && manualStretchFactor.Trim().Length > 0)
            {
                if (!SpeedCorrectionService.TryParseStretchFactor(manualStretchFactor, out stretchRatio, out normalizedManualFactor))
                {
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  Stretch manuale non valido: " + manualStretchFactor);
                    return false;
                }

                inverseRatio = 1.0 / stretchRatio;
                stretchFactor = normalizedManualFactor;
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Stretch manuale: " + stretchRatio.ToString("F6", CultureInfo.InvariantCulture) + " (" + stretchFactor + ")");
            }
            else if (!allowAutoStretch)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Stretch: disabilitato");
            }
            else if (sourceDefaultDurationNs > 0 && langDefaultDurationNs > 0)
            {
                stretchRatio = (double)sourceDefaultDurationNs / langDefaultDurationNs;
                ratioDiff = Math.Abs(stretchRatio - 1.0);

                if (ratioDiff >= 0.001)
                {
                    inverseRatio = 1.0 / stretchRatio;
                    stretchFactor = sourceDefaultDurationNs + "/" + langDefaultDurationNs;
                    sourceFps = 1000000000.0 / sourceDefaultDurationNs;
                    langFps = 1000000000.0 / langDefaultDurationNs;

                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Stretch: " + stretchRatio.ToString("F6", CultureInfo.InvariantCulture) + " (" + sourceFps.ToString("F3", CultureInfo.InvariantCulture) + "fps -> " + langFps.ToString("F3", CultureInfo.InvariantCulture) + "fps)");
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Stretch: nessuno (stesso fps)");
                }
            }

            return true;
        }

        /// <summary>
        /// Impedisce alla timeline video di estendere all'inizio un plateau non confermato dal contenuto
        /// </summary>
        /// <param name="sourceFile">File source</param>
        /// <param name="langFile">File lang</param>
        /// <param name="sourceDurationMs">Durata source in millisecondi</param>
        /// <param name="regions">Regioni offset rilevate</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="visualStartOffsetMs">Offset iniziale stimato dalla guardia visuale</param>
        /// <param name="visualStartEndSec">Fine finestra visuale confermata</param>
        /// <param name="visualStartMse">MSE migliore della guardia visuale</param>
        /// <param name="visualStartSecondMse">Secondo MSE migliore della guardia visuale</param>
        /// <param name="visualStartConfirmed">True se la guardia visuale conferma l'offset iniziale</param>
        private void ApplyInitialVisualGuard(string sourceFile, string langFile, int sourceDurationMs, List<OffsetRegion> regions, double inverseRatio, out int visualStartOffsetMs, out double visualStartEndSec, out double visualStartMse, out double visualStartSecondMse, out bool visualStartConfirmed)
        {
            int timelineOffsetMs;
            double visualStartLocalSecondMse;
            double timelineMse;
            bool visualStartBorderCandidate;
            double sourceDurationSec = sourceDurationMs / 1000.0;

            visualStartOffsetMs = int.MinValue;
            visualStartEndSec = 0.0;
            visualStartMse = 0.0;
            visualStartSecondMse = 0.0;
            visualStartConfirmed = false;

            if (regions == null || regions.Count == 0 || sourceDurationSec < 90.0)
            {
                return;
            }

            timelineOffsetMs = (int)Math.Round(regions[0].OffsetMs);
            if (!this.TryFindInitialVisualGuardOffset(sourceFile, langFile, sourceDurationSec, timelineOffsetMs, inverseRatio, out visualStartOffsetMs, out visualStartEndSec, out visualStartMse, out visualStartSecondMse, out visualStartLocalSecondMse, out timelineMse, out visualStartBorderCandidate))
            {
                visualStartOffsetMs = int.MinValue;
                return;
            }

            if (Math.Abs(timelineOffsetMs - visualStartOffsetMs) <= 1000)
            {
                visualStartOffsetMs = timelineOffsetMs;
                visualStartConfirmed = true;
                return;
            }

            if (!double.IsPositiveInfinity(timelineMse) && visualStartMse > timelineMse * INITIAL_VISUAL_GUARD_TIMELINE_IMPROVEMENT)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Start guard scartato: candidate=" + visualStartOffsetMs.ToString(CultureInfo.InvariantCulture) + "ms non migliora timeline " + timelineOffsetMs.ToString(CultureInfo.InvariantCulture) + "ms (mse=" + visualStartMse.ToString("F1", CultureInfo.InvariantCulture) + ", timelineMse=" + timelineMse.ToString("F1", CultureInfo.InvariantCulture) + ")");
                visualStartOffsetMs = int.MinValue;
                return;
            }

            if (visualStartBorderCandidate && (visualStartMse > INITIAL_VISUAL_GUARD_STRONG_MSE || double.IsPositiveInfinity(visualStartLocalSecondMse) || ((visualStartLocalSecondMse - visualStartMse) / Math.Max(visualStartLocalSecondMse, 1.0)) < INITIAL_VISUAL_GUARD_BORDER_MARGIN))
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Start guard scartato: candidate=" + visualStartOffsetMs.ToString(CultureInfo.InvariantCulture) + "ms e' sul bordo language (mse=" + visualStartMse.ToString("F1", CultureInfo.InvariantCulture) + ", localSecond=" + visualStartLocalSecondMse.ToString("F1", CultureInfo.InvariantCulture) + ")");
                visualStartOffsetMs = int.MinValue;
                return;
            }

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Start guard: timeline initial " + timelineOffsetMs.ToString(CultureInfo.InvariantCulture) + "ms corretto a " + visualStartOffsetMs.ToString(CultureInfo.InvariantCulture) + "ms sui primi " + visualStartEndSec.ToString("F0", CultureInfo.InvariantCulture) + "s");
            visualStartConfirmed = true;
            this.PrependInitialGuardRegion(regions, visualStartOffsetMs, visualStartEndSec, sourceDurationSec);
        }

        /// <summary>
        /// Cerca il miglior offset visuale sui primi secondi, dove sigla/logo iniziale non devono ereditare plateau successivi
        /// </summary>
        private bool TryFindInitialVisualGuardOffset(string sourceFile, string langFile, double sourceDurationSec, int timelineOffsetMs, double inverseRatio, out int offsetMs, out double guardEndSec, out double bestMse, out double secondMse, out double localSecondMse, out double timelineMse, out bool borderCandidate)
        {
            bool result = false;
            FrameSyncConfig frameSyncConfig = AppSettingsService.Instance.Settings.Advanced.FrameSync;
            double languageExtractDurationSec = frameSyncConfig.LangDurationSec;
            int sourceStartMs = frameSyncConfig.SourceStartSec * 1000;
            double sourceExtractDurationSec;
            int minOffsetMs;
            int maxOffsetMs;
            List<int> candidateOffsets = new List<int>();
            List<double> candidateMseValues = new List<double>();
            List<byte[]> sourceFrames;
            double[] sourceTimestampsMs;
            List<byte[]> langFrames;
            double[] langTimestampsMs;

            offsetMs = 0;
            if (timelineOffsetMs > sourceStartMs)
            {
                sourceStartMs = timelineOffsetMs + INITIAL_VISUAL_GUARD_TIMELINE_START_PADDING_MS;
            }

            sourceExtractDurationSec = Math.Min(sourceDurationSec - (sourceStartMs / 1000.0), frameSyncConfig.SourceDurationSec);
            guardEndSec = Math.Min(sourceDurationSec, INITIAL_VISUAL_GUARD_MAX_REGION_SEC);
            bestMse = double.PositiveInfinity;
            secondMse = double.PositiveInfinity;
            localSecondMse = double.PositiveInfinity;
            timelineMse = double.PositiveInfinity;
            borderCandidate = false;

            if (sourceExtractDurationSec < 20.0)
            {
                return result;
            }

            minOffsetMs = -sourceStartMs - (int)Math.Ceiling(sourceExtractDurationSec * 1000.0);
            maxOffsetMs = (int)Math.Ceiling(languageExtractDurationSec * 1000.0) - sourceStartMs;
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Start guard: ricerca visuale iniziale source start " + (sourceStartMs / 1000.0).ToString("F3", CultureInfo.InvariantCulture) + "s, durata " + sourceExtractDurationSec.ToString("F0", CultureInfo.InvariantCulture) + "s/lang " + languageExtractDurationSec.ToString("F0", CultureInfo.InvariantCulture) + "s, range " + minOffsetMs + "ms.." + maxOffsetMs + "ms");

            this.ExtractDeepSegment(sourceFile, sourceStartMs, sourceExtractDurationSec, INITIAL_VISUAL_GUARD_FPS, this._geometryCropSourceToFourThree, this._analysisCropSourcePx, out sourceFrames, out sourceTimestampsMs);
            this.ExtractDeepSegment(langFile, 0, languageExtractDurationSec, INITIAL_VISUAL_GUARD_FPS, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx, out langFrames, out langTimestampsMs);
            if (sourceFrames == null || langFrames == null || sourceFrames.Count < 20 || langFrames.Count < 20)
            {
                return result;
            }

            for (int candidateOffsetMs = minOffsetMs; candidateOffsetMs <= maxOffsetMs; candidateOffsetMs += INITIAL_VISUAL_GUARD_SEARCH_STEP_MS)
            {
                double languageStartMs = (sourceStartMs - candidateOffsetMs) * inverseRatio;
                if (languageStartMs < 0.0)
                {
                    continue;
                }

                double mse = this.ComputeTimestampMatchedMseAtStart(sourceFrames, sourceTimestampsMs, langFrames, langTimestampsMs, languageStartMs, INITIAL_VISUAL_GUARD_FPS, inverseRatio);
                if (double.IsPositiveInfinity(mse))
                {
                    continue;
                }

                candidateOffsets.Add(candidateOffsetMs);
                candidateMseValues.Add(mse);
                if (mse < bestMse)
                {
                    bestMse = mse;
                    offsetMs = candidateOffsetMs;
                    borderCandidate = languageStartMs <= INITIAL_VISUAL_GUARD_SEARCH_STEP_MS;
                }
            }

            for (int i = 0; i < candidateMseValues.Count; i++)
            {
                if (Math.Abs(candidateOffsets[i] - offsetMs) < INITIAL_VISUAL_GUARD_DISTINCT_OFFSET_MS)
                {
                    continue;
                }

                if (candidateMseValues[i] < secondMse)
                {
                    secondMse = candidateMseValues[i];
                }
            }

            for (int i = 0; i < candidateMseValues.Count; i++)
            {
                if (Math.Abs(candidateOffsets[i] - offsetMs) < INITIAL_VISUAL_GUARD_LOCAL_DISTINCT_OFFSET_MS)
                {
                    continue;
                }

                if (candidateMseValues[i] < localSecondMse)
                {
                    localSecondMse = candidateMseValues[i];
                }
            }

            double timelineLanguageStartMs = (sourceStartMs - timelineOffsetMs) * inverseRatio;
            if (timelineLanguageStartMs >= 0.0)
            {
                timelineMse = this.ComputeTimestampMatchedMseAtStart(sourceFrames, sourceTimestampsMs, langFrames, langTimestampsMs, timelineLanguageStartMs, INITIAL_VISUAL_GUARD_FPS, inverseRatio);
            }

            if (double.IsPositiveInfinity(bestMse) || double.IsPositiveInfinity(secondMse) || double.IsPositiveInfinity(localSecondMse))
            {
                return result;
            }

            double relativeMargin = (secondMse - bestMse) / Math.Max(secondMse, 1.0);
            double localMargin = (localSecondMse - bestMse) / Math.Max(localSecondMse, 1.0);
            bool absoluteStrong = bestMse <= INITIAL_VISUAL_GUARD_STRONG_MSE && relativeMargin >= INITIAL_VISUAL_GUARD_STRONG_MARGIN && localMargin >= INITIAL_VISUAL_GUARD_LOCAL_MARGIN;
            bool relativeStrong = !double.IsPositiveInfinity(timelineMse) && bestMse <= timelineMse * INITIAL_VISUAL_GUARD_RELATIVE_IMPROVEMENT && relativeMargin >= INITIAL_VISUAL_GUARD_STRONG_MARGIN && localMargin >= INITIAL_VISUAL_GUARD_LOCAL_MARGIN;

            if (absoluteStrong || relativeStrong)
            {
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Recupera gap di coda quando la timeline estende l'ultimo plateau oltre l'ultimo supporto visuale.
        /// </summary>
        private void RecoverUnsupportedTailGap(string sourceFile, string langFile, int sourceDurationMs, double inverseRatio, List<OffsetRegion> regions, DeepTimelineMapResult timelineMap)
        {
            OffsetRegion lastRegion;
            OffsetRegion tailRegion;
            double sourceDurationSec = sourceDurationMs / 1000.0;
            double unsupportedTailSec;
            double stretchRatio;
            int langDurationMs;
            int expectedTailOffsetMs;
            int deltaMs;
            int recoveredTailOffsetMs;
            int recoveredDeltaMs;
            double crossoverSec;
            double postGapSupportSec;
            double frameBoundarySec;
            double recoveredTailOffsetSec;

            if (regions == null || regions.Count == 0 || sourceDurationMs <= 0 || Math.Abs(inverseRatio) < 0.000001)
            {
                return;
            }

            lastRegion = regions[regions.Count - 1];
            unsupportedTailSec = sourceDurationSec - lastRegion.SupportEndSrcSec;
            if (unsupportedTailSec < TAIL_GAP_MIN_UNSUPPORTED_SEC)
            {
                return;
            }

            langDurationMs = this.ResolveMediaDurationMs(langFile);
            if (langDurationMs <= 0)
            {
                return;
            }

            stretchRatio = 1.0 / inverseRatio;
            expectedTailOffsetMs = sourceDurationMs - (int)Math.Round(langDurationMs * stretchRatio);
            deltaMs = expectedTailOffsetMs - (int)Math.Round(lastRegion.OffsetMs);
            if (deltaMs < TAIL_GAP_MIN_DELTA_MS)
            {
                return;
            }

            if (!this.TryFindTailGapCrossover(sourceFile, langFile, lastRegion, sourceDurationSec, lastRegion.OffsetMs / 1000.0, expectedTailOffsetMs / 1000.0, inverseRatio, out postGapSupportSec))
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Tail gap recovery non conclusivo: coda non supportata " + unsupportedTailSec.ToString("F0", CultureInfo.InvariantCulture) + "s, delta atteso " + deltaMs.ToString(CultureInfo.InvariantCulture) + "ms");
                return;
            }

            if (!this.TryEstimateTailGapPostOffset(sourceFile, langFile, postGapSupportSec, expectedTailOffsetMs / 1000.0, inverseRatio, out recoveredTailOffsetSec))
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Tail gap recovery non conclusivo: offset locale post-gap non stimabile");
                return;
            }

            recoveredTailOffsetMs = (int)Math.Round(recoveredTailOffsetSec * 1000.0);
            recoveredDeltaMs = recoveredTailOffsetMs - (int)Math.Round(lastRegion.OffsetMs);
            if (recoveredDeltaMs < TAIL_GAP_MIN_DELTA_MS)
            {
                return;
            }

            if (!this.TryFindTailGapFrameBoundary(sourceFile, langFile, lastRegion.OffsetMs / 1000.0, recoveredTailOffsetSec, inverseRatio, postGapSupportSec, recoveredDeltaMs / 1000.0, sourceDurationSec, out frameBoundarySec))
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Tail gap recovery non conclusivo: boundary frame-perfect non trovato attorno a " + (postGapSupportSec - (recoveredDeltaMs / 1000.0)).ToString("F1", CultureInfo.InvariantCulture) + "s");
                return;
            }

            crossoverSec = frameBoundarySec;
            if (crossoverSec <= lastRegion.StartSrcSec + 1.0 || crossoverSec >= sourceDurationSec - 1.0)
            {
                return;
            }

            lastRegion.EndSrcSec = crossoverSec;
            tailRegion = new OffsetRegion();
            tailRegion.StartSrcSec = crossoverSec;
            tailRegion.EndSrcSec = sourceDurationSec;
            tailRegion.SupportStartSrcSec = crossoverSec;
            tailRegion.SupportEndSrcSec = Math.Max(crossoverSec, postGapSupportSec);
            tailRegion.OffsetMs = recoveredTailOffsetMs;
            tailRegion.MatchCount = 1;
            regions.Add(tailRegion);

            if (timelineMap != null && timelineMap.Diagnostic != null)
            {
                DeepAnalysisTimelinePlateauDiagnostic plateau = new DeepAnalysisTimelinePlateauDiagnostic();
                plateau.Index = timelineMap.Diagnostic.Plateaus.Count;
                plateau.StartSrcSec = crossoverSec;
                plateau.EndSrcSec = Math.Max(crossoverSec, postGapSupportSec);
                plateau.OffsetMs = recoveredTailOffsetMs;
                plateau.AnchorCount = 1;
                plateau.AverageScore = 0.0;
                timelineMap.Diagnostic.Plateaus.Add(plateau);
                timelineMap.Diagnostic.PlateauCount = timelineMap.Diagnostic.Plateaus.Count;
            }

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Tail gap recovery: coda non supportata " + unsupportedTailSec.ToString("F0", CultureInfo.InvariantCulture) + "s, offset " + lastRegion.OffsetMs.ToString(CultureInfo.InvariantCulture) + "ms -> " + recoveredTailOffsetMs.ToString(CultureInfo.InvariantCulture) + "ms da " + crossoverSec.ToString("F3", CultureInfo.InvariantCulture) + "s (boundary frame, supporto " + postGapSupportSec.ToString("F1", CultureInfo.InvariantCulture) + "s, atteso " + expectedTailOffsetMs.ToString(CultureInfo.InvariantCulture) + "ms)");
        }

        /// <summary>
        /// Stima l'offset locale dopo il gap su frame nativi, invece di derivarlo dalla durata totale file.
        /// </summary>
        private bool TryEstimateTailGapPostOffset(string sourceFile, string langFile, double supportSrcSec, double expectedOffsetSec, double inverseRatio, out double offsetSec)
        {
            bool result = false;
            double windowStartSec = Math.Max(0.0, supportSrcSec - 8.0);
            double durationSec = 16.0;
            double searchRadiusSec = 5.0;
            double stepSec = 1.0 / 24.0;
            double bestMse = double.MaxValue;
            double secondMse = double.MaxValue;
            double bestOffsetSec = expectedOffsetSec;
            List<byte[]> sourceFrames;
            double[] sourceTimestampsMs;
            List<byte[]> langFrames;
            double[] langTimestampsMs;

            offsetSec = 0.0;
            this.ExtractDeepSegment(sourceFile, (int)Math.Round(windowStartSec * 1000.0), durationSec, 0.0, this._geometryCropSourceToFourThree, this._analysisCropSourcePx, out sourceFrames, out sourceTimestampsMs);
            if (sourceFrames.Count < 8 || sourceTimestampsMs.Length < 8)
            {
                return result;
            }

            double langStartSec = windowStartSec - expectedOffsetSec - searchRadiusSec;
            if (Math.Abs(inverseRatio - 1.0) > 0.0001)
            {
                langStartSec *= inverseRatio;
            }
            if (langStartSec < 0.0) { langStartSec = 0.0; }

            this.ExtractDeepSegment(langFile, (int)Math.Round(langStartSec * 1000.0), durationSec + (searchRadiusSec * 2.0) + 2.0, 0.0, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx, out langFrames, out langTimestampsMs);
            if (langFrames.Count < 8 || langTimestampsMs.Length < 8)
            {
                return result;
            }

            for (double candidateOffsetSec = expectedOffsetSec - searchRadiusSec; candidateOffsetSec <= expectedOffsetSec + searchRadiusSec; candidateOffsetSec += stepSec)
            {
                double mse = this.ComputeTailGapOffsetMse(sourceFrames, sourceTimestampsMs, langFrames, langTimestampsMs, candidateOffsetSec, inverseRatio, 125.0);
                if (mse <= 0.0 || mse >= double.MaxValue)
                {
                    continue;
                }

                if (mse < bestMse)
                {
                    secondMse = bestMse;
                    bestMse = mse;
                    bestOffsetSec = candidateOffsetSec;
                }
                else if (mse < secondMse)
                {
                    secondMse = mse;
                }
            }

            if (bestMse < double.MaxValue && (secondMse >= double.MaxValue || bestMse <= secondMse * 0.98 || bestMse < 200.0))
            {
                offsetSec = bestOffsetSec;
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Tail gap offset locale: " + (offsetSec * 1000.0).ToString("F0", CultureInfo.InvariantCulture) + "ms vicino a src " + supportSrcSec.ToString("F1", CultureInfo.InvariantCulture) + "s (mse=" + bestMse.ToString("F1", CultureInfo.InvariantCulture) + ")");
                result = true;
            }

            return result;
        }

        /// <summary>
        /// MSE medio per un offset candidato nella finestra post-gap.
        /// </summary>
        private double ComputeTailGapOffsetMse(List<byte[]> sourceFrames, double[] sourceTimestampsMs, List<byte[]> langFrames, double[] langTimestampsMs, double offsetSec, double inverseRatio, double toleranceMs)
        {
            double total = 0.0;
            int count = 0;
            for (int i = 0; i < sourceFrames.Count; i++)
            {
                double targetLangMs = sourceTimestampsMs[i] - (offsetSec * 1000.0);
                if (Math.Abs(inverseRatio - 1.0) > 0.0001)
                {
                    targetLangMs *= inverseRatio;
                }

                int langIndex = NearestTimestampIndex(langTimestampsMs, targetLangMs);
                if (langIndex < 0 || langIndex >= langFrames.Count)
                {
                    continue;
                }

                if (Math.Abs(langTimestampsMs[langIndex] - targetLangMs) > toleranceMs)
                {
                    continue;
                }

                total += this.ComputeMse(sourceFrames[i], langFrames[langIndex]);
                count++;
            }

            if (count < 6)
            {
                return double.MaxValue;
            }

            return total / count;
        }

        /// <summary>
        /// Localizza l'inizio del gap su frame nativi: il vecchio offset deve combaciare prima,
        /// divergere subito dopo, e il nuovo offset deve combaciare dopo la durata gap.
        /// </summary>
        private bool TryFindTailGapFrameBoundary(string sourceFile, string langFile, double oldOffsetSec, double newOffsetSec, double inverseRatio, double postGapSupportSec, double gapDurationSec, double sourceDurationSec, out double boundarySec)
        {
            bool result = false;
            double approximateBoundarySec = postGapSupportSec - gapDurationSec;
            double searchStartSec = approximateBoundarySec - TAIL_GAP_FRAME_SEARCH_MARGIN_SEC;
            double searchEndSec = approximateBoundarySec + TAIL_GAP_FRAME_SEARCH_MARGIN_SEC;
            double extractEndSec;
            double durationSec;
            double langOldStartSec;
            double langNewStartSec;
            double langDurationSec;
            double toleranceMs = 125.0;
            List<byte[]> sourceFrames;
            double[] sourceTimestampsMs;
            List<byte[]> langOldFrames;
            double[] langOldTimestampsMs;
            List<byte[]> langNewFrames;
            double[] langNewTimestampsMs;

            boundarySec = 0.0;
            if (searchStartSec < 0.0) { searchStartSec = 0.0; }
            if (searchEndSec > sourceDurationSec - 1.0) { searchEndSec = sourceDurationSec - 1.0; }
            extractEndSec = Math.Min(sourceDurationSec - 1.0, searchEndSec + gapDurationSec + 2.0);
            durationSec = extractEndSec - searchStartSec;
            if (durationSec <= gapDurationSec || searchEndSec <= searchStartSec)
            {
                return result;
            }

            this.ExtractDeepSegment(sourceFile, (int)Math.Round(searchStartSec * 1000.0), durationSec, 0.0, this._geometryCropSourceToFourThree, this._analysisCropSourcePx, out sourceFrames, out sourceTimestampsMs);
            if (sourceFrames.Count < TAIL_GAP_FRAME_CONFIRM_FRAMES * 3 || sourceTimestampsMs.Length < TAIL_GAP_FRAME_CONFIRM_FRAMES * 3)
            {
                return result;
            }

            langOldStartSec = searchStartSec - oldOffsetSec;
            langNewStartSec = searchStartSec - newOffsetSec;
            if (Math.Abs(inverseRatio - 1.0) > 0.0001)
            {
                langOldStartSec *= inverseRatio;
                langNewStartSec *= inverseRatio;
            }
            if (langOldStartSec < 0.0) { langOldStartSec = 0.0; }
            if (langNewStartSec < 0.0) { langNewStartSec = 0.0; }

            langDurationSec = durationSec + 2.0;
            this.ExtractDeepSegment(langFile, (int)Math.Round(langOldStartSec * 1000.0), langDurationSec, 0.0, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx, out langOldFrames, out langOldTimestampsMs);
            this.ExtractDeepSegment(langFile, (int)Math.Round(langNewStartSec * 1000.0), langDurationSec, 0.0, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx, out langNewFrames, out langNewTimestampsMs);
            if (langOldFrames.Count < TAIL_GAP_FRAME_CONFIRM_FRAMES || langNewFrames.Count < TAIL_GAP_FRAME_CONFIRM_FRAMES)
            {
                return result;
            }

            for (int i = TAIL_GAP_FRAME_CONFIRM_FRAMES; i < sourceFrames.Count - TAIL_GAP_FRAME_CONFIRM_FRAMES; i++)
            {
                double candidateSec = sourceTimestampsMs[i] / 1000.0;
                int beforeMatches;
                int afterMismatches;
                int postMatches;

                if (candidateSec < searchStartSec || candidateSec > searchEndSec)
                {
                    continue;
                }

                beforeMatches = this.CountTailGapOldMatches(sourceFrames, sourceTimestampsMs, langOldFrames, langOldTimestampsMs, i - TAIL_GAP_FRAME_CONFIRM_FRAMES, i, oldOffsetSec, inverseRatio, toleranceMs);
                if (beforeMatches < TAIL_GAP_FRAME_MIN_VOTES)
                {
                    continue;
                }

                afterMismatches = this.CountTailGapOldMismatches(sourceFrames, sourceTimestampsMs, langOldFrames, langOldTimestampsMs, i, i + TAIL_GAP_FRAME_CONFIRM_FRAMES, oldOffsetSec, inverseRatio, toleranceMs);
                if (afterMismatches < TAIL_GAP_FRAME_MIN_VOTES)
                {
                    continue;
                }

                postMatches = this.CountTailGapNewMatches(sourceFrames, sourceTimestampsMs, langNewFrames, langNewTimestampsMs, candidateSec + gapDurationSec, newOffsetSec, inverseRatio, toleranceMs);
                if (postMatches < TAIL_GAP_FRAME_MIN_VOTES)
                {
                    continue;
                }

                boundarySec = candidateSec;
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Tail gap frame boundary: src " + boundarySec.ToString("F3", CultureInfo.InvariantCulture) + "s, old-before " + beforeMatches.ToString(CultureInfo.InvariantCulture) + "/" + TAIL_GAP_FRAME_CONFIRM_FRAMES.ToString(CultureInfo.InvariantCulture) + ", old-after mismatch " + afterMismatches.ToString(CultureInfo.InvariantCulture) + "/" + TAIL_GAP_FRAME_CONFIRM_FRAMES.ToString(CultureInfo.InvariantCulture) + ", new-post " + postMatches.ToString(CultureInfo.InvariantCulture) + "/" + TAIL_GAP_FRAME_CONFIRM_FRAMES.ToString(CultureInfo.InvariantCulture));
                result = true;
                return result;
            }

            return result;
        }

        /// <summary>
        /// Conta frame source che combaciano col vecchio offset.
        /// </summary>
        private int CountTailGapOldMatches(List<byte[]> sourceFrames, double[] sourceTimestampsMs, List<byte[]> langFrames, double[] langTimestampsMs, int startIndex, int endIndex, double offsetSec, double inverseRatio, double toleranceMs)
        {
            int result = 0;
            for (int i = startIndex; i < endIndex && i < sourceFrames.Count; i++)
            {
                double ssim;
                if (this.TryScoreTailGapFrame(sourceFrames[i], sourceTimestampsMs[i], langFrames, langTimestampsMs, offsetSec, inverseRatio, toleranceMs, out ssim) &&
                    ssim >= TAIL_GAP_FRAME_MATCH_SSIM)
                {
                    result++;
                }
            }

            return result;
        }

        /// <summary>
        /// Conta frame source che non combaciano piu' col vecchio offset.
        /// </summary>
        private int CountTailGapOldMismatches(List<byte[]> sourceFrames, double[] sourceTimestampsMs, List<byte[]> langFrames, double[] langTimestampsMs, int startIndex, int endIndex, double offsetSec, double inverseRatio, double toleranceMs)
        {
            int result = 0;
            for (int i = startIndex; i < endIndex && i < sourceFrames.Count; i++)
            {
                double ssim;
                if (this.TryScoreTailGapFrame(sourceFrames[i], sourceTimestampsMs[i], langFrames, langTimestampsMs, offsetSec, inverseRatio, toleranceMs, out ssim) &&
                    ssim <= TAIL_GAP_FRAME_MISMATCH_SSIM)
                {
                    result++;
                }
            }

            return result;
        }

        /// <summary>
        /// Conta frame source post-gap che combaciano col nuovo offset.
        /// </summary>
        private int CountTailGapNewMatches(List<byte[]> sourceFrames, double[] sourceTimestampsMs, List<byte[]> langFrames, double[] langTimestampsMs, double sourceStartSec, double offsetSec, double inverseRatio, double toleranceMs)
        {
            int result = 0;
            int startIndex = NearestTimestampIndex(sourceTimestampsMs, sourceStartSec * 1000.0);
            if (startIndex < 0)
            {
                return result;
            }

            for (int i = startIndex; i < sourceFrames.Count && result < TAIL_GAP_FRAME_CONFIRM_FRAMES; i++)
            {
                double ssim;
                if (this.TryScoreTailGapFrame(sourceFrames[i], sourceTimestampsMs[i], langFrames, langTimestampsMs, offsetSec, inverseRatio, toleranceMs, out ssim) &&
                    ssim >= TAIL_GAP_FRAME_MATCH_SSIM)
                {
                    result++;
                }
                else if (sourceTimestampsMs[i] - sourceTimestampsMs[startIndex] > 1000.0)
                {
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Calcola SSIM tra un frame source e il frame lang corrispondente all'offset dato.
        /// </summary>
        private bool TryScoreTailGapFrame(byte[] sourceFrame, double sourceTimestampMs, List<byte[]> langFrames, double[] langTimestampsMs, double offsetSec, double inverseRatio, double toleranceMs, out double ssim)
        {
            bool result = false;
            double targetLangMs = sourceTimestampMs - (offsetSec * 1000.0);
            int langIndex;
            double distanceMs;

            ssim = 0.0;
            if (Math.Abs(inverseRatio - 1.0) > 0.0001)
            {
                targetLangMs *= inverseRatio;
            }

            langIndex = NearestTimestampIndex(langTimestampsMs, targetLangMs);
            if (langIndex < 0 || langIndex >= langFrames.Count)
            {
                return result;
            }

            distanceMs = Math.Abs(langTimestampsMs[langIndex] - targetLangMs);
            if (distanceMs > toleranceMs)
            {
                return result;
            }

            ssim = this.ComputeSsim(sourceFrame, langFrames[langIndex]);
            result = true;
            return result;
        }

        /// <summary>
        /// Cerca il primo punto di coda dove il nuovo offset stimato batte chiaramente quello esteso dalla timeline.
        /// </summary>
        private bool TryFindTailGapCrossover(string sourceFile, string langFile, OffsetRegion lastRegion, double sourceDurationSec, double oldOffsetSec, double newOffsetSec, double inverseRatio, out double crossoverSec)
        {
            bool result = false;
            double scanStartSec = lastRegion.SupportEndSrcSec + TAIL_GAP_SCAN_STEP_SEC;
            double scanEndSec = sourceDurationSec - TAIL_GAP_SCAN_MARGIN_SEC;
            double bestCenterSec = -1.0;
            double bestRatio = 0.0;
            double oldMse;
            double oldSsim;
            double newMse;
            double newSsim;
            double ratio;
            int consecutiveWins = 0;

            crossoverSec = 0.0;
            if (scanStartSec >= scanEndSec)
            {
                return result;
            }

            for (double centerSec = scanStartSec; centerSec <= scanEndSec; centerSec += TAIL_GAP_SCAN_STEP_SEC)
            {
                if (!this._visualFrameAnalyzer.TryComputeLocalVisualScoreAt(sourceFile, langFile, centerSec, oldOffsetSec, inverseRatio, 1.0, this._geometryCropSourceToFourThree, this._geometryCropLanguageToFourThree, out oldMse, out oldSsim))
                {
                    consecutiveWins = 0;
                    continue;
                }
                if (!this._visualFrameAnalyzer.TryComputeLocalVisualScoreAt(sourceFile, langFile, centerSec, newOffsetSec, inverseRatio, 1.0, this._geometryCropSourceToFourThree, this._geometryCropLanguageToFourThree, out newMse, out newSsim))
                {
                    consecutiveWins = 0;
                    continue;
                }

                ratio = this.ComputeSafeImprovementRatio(oldMse, newMse);
                if (ratio >= TAIL_GAP_MIN_IMPROVEMENT || (newSsim > oldSsim + LOCAL_SSIM_CLEAR_MARGIN && ratio > 1.10))
                {
                    consecutiveWins++;
                    if (ratio > bestRatio)
                    {
                        bestRatio = ratio;
                        bestCenterSec = centerSec;
                    }

                    if (consecutiveWins >= 2)
                    {
                        crossoverSec = centerSec - TAIL_GAP_SCAN_STEP_SEC;
                        result = true;
                        break;
                    }
                }
                else
                {
                    consecutiveWins = 0;
                }
            }

            if (!result && bestCenterSec > 0.0 && bestRatio >= TAIL_GAP_MIN_IMPROVEMENT * 2.0)
            {
                crossoverSec = bestCenterSec;
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Legge la durata container via ffmpeg, utile quando mkvmerge non la espone per AVI.
        /// </summary>
        private int ResolveMediaDurationMs(string filePath)
        {
            ProcessResult processResult;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return 0;
            }

            processResult = ProcessRunner.Run(this._ffmpegPath, new string[] { "-hide_banner", "-i", filePath });
            return this.ParseFfmpegDurationMs(processResult.Stderr.Length > 0 ? processResult.Stderr : processResult.Stdout);
        }

        /// <summary>
        /// Estrae la durata dal formato ffmpeg "Duration: HH:MM:SS.xx".
        /// </summary>
        private int ParseFfmpegDurationMs(string output)
        {
            int marker;
            int end;
            string value;
            TimeSpan duration;
            if (output == null)
            {
                return 0;
            }

            marker = output.IndexOf("Duration:", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                return 0;
            }

            marker += "Duration:".Length;
            end = output.IndexOf(",", marker, StringComparison.Ordinal);
            if (end <= marker)
            {
                return 0;
            }

            value = output.Substring(marker, end - marker).Trim();
            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration))
            {
                return (int)Math.Round(duration.TotalMilliseconds);
            }

            return 0;
        }

        /// <summary>
        /// Inserisce una regione iniziale supportata dal video e lascia al refine il compito di localizzare il primo cambio
        /// </summary>
        private void PrependInitialGuardRegion(List<OffsetRegion> regions, int visualStartOffsetMs, double visualStartEndSec, double sourceDurationSec)
        {
            OffsetRegion firstRegion = regions[0];
            OffsetRegion guardRegion = new OffsetRegion();
            double guardEndSec = visualStartEndSec;
            double firstSupportStartSec = firstRegion.SupportStartSrcSec;
            double bridgedGuardEndSec;

            if (guardEndSec < 20.0) { guardEndSec = 20.0; }
            if (guardEndSec > sourceDurationSec) { guardEndSec = sourceDurationSec; }

            if (firstSupportStartSec > guardEndSec + INITIAL_VISUAL_GUARD_TIMELINE_LEAD_IN_SEC)
            {
                bridgedGuardEndSec = firstSupportStartSec - INITIAL_VISUAL_GUARD_TIMELINE_LEAD_IN_SEC;
                if (bridgedGuardEndSec > guardEndSec)
                {
                    guardEndSec = bridgedGuardEndSec;
                }
            }

            if (guardEndSec > firstRegion.EndSrcSec - 1.0 && firstRegion.EndSrcSec > 1.0)
            {
                guardEndSec = firstRegion.EndSrcSec - 1.0;
            }

            if (guardEndSec > sourceDurationSec) { guardEndSec = sourceDurationSec; }

            guardRegion.StartSrcSec = 0.0;
            guardRegion.EndSrcSec = guardEndSec;
            guardRegion.SupportStartSrcSec = 0.0;
            guardRegion.SupportEndSrcSec = guardEndSec;
            guardRegion.OffsetMs = visualStartOffsetMs;
            guardRegion.MatchCount = 1;

            firstRegion.StartSrcSec = guardEndSec;
            if (firstRegion.SupportStartSrcSec < guardEndSec)
            {
                firstRegion.SupportStartSrcSec = guardEndSec;
            }

            if (firstRegion.EndSrcSec <= firstRegion.StartSrcSec)
            {
                firstRegion.EndSrcSec = firstRegion.StartSrcSec + 1.0;
            }

            regions.Insert(0, guardRegion);
        }

        /// <summary>
        /// Costruisce un anchor timeline via confronto visuale distribuito
        /// </summary>
        private bool BuildVisualTimelineAnchor(string sourceFile, string langFile, double sourceCenterSec, int searchRadiusMs, int searchStepMs, double inverseRatio, out DeepAnalysisTimelineAnchorDiagnostic anchor)
        {
            bool result = false;
            double durationSec = 4.0;
            double targetFps = Math.Max(4.0, 1000.0 / Math.Max(searchStepMs, 1));
            double sourceStartSec = sourceCenterSec - (durationSec / 2.0);
            double languageWideStartSec;
            double languageWideDurationSec;
            int bestOffsetMs = 0;
            int secondOffsetMs = 0;
            double bestMse = double.PositiveInfinity;
            double secondMse = double.PositiveInfinity;
            double score;
            double margin;
            List<byte[]> sourceFrames;
            double[] sourceTs;
            List<byte[]> languageFrames;
            double[] languageTs;

            anchor = new DeepAnalysisTimelineAnchorDiagnostic();
            anchor.SourceCenterSec = sourceCenterSec;

            if (sourceStartSec < 0.0)
            {
                sourceStartSec = 0.0;
            }

            this.ExtractDeepSegment(sourceFile, (int)Math.Round(sourceStartSec * 1000.0), durationSec, targetFps, this._geometryCropSourceToFourThree, this._analysisCropSourcePx, out sourceFrames, out sourceTs);
            if (sourceFrames == null || sourceFrames.Count == 0)
            {
                anchor.RejectReason = "frame source insufficienti";
                return result;
            }

            languageWideStartSec = (sourceStartSec - (searchRadiusMs / 1000.0)) * inverseRatio;
            if (languageWideStartSec < 0.0)
            {
                languageWideStartSec = 0.0;
            }

            languageWideDurationSec = (durationSec + ((searchRadiusMs * 2.0) / 1000.0)) * inverseRatio;
            this.ExtractDeepSegment(langFile, (int)Math.Round(languageWideStartSec * 1000.0), languageWideDurationSec, targetFps, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx, out languageFrames, out languageTs);
            if (languageFrames == null || languageFrames.Count == 0)
            {
                anchor.RejectReason = "frame language insufficienti";
                return result;
            }

            for (int offsetMs = -searchRadiusMs; offsetMs <= searchRadiusMs; offsetMs += searchStepMs)
            {
                double languageStartSec = (sourceStartSec - (offsetMs / 1000.0)) * inverseRatio;
                if (languageStartSec < 0.0)
                {
                    continue;
                }

                double mse = this.ComputeTimestampMatchedMseAtStart(sourceFrames, sourceTs, languageFrames, languageTs, languageStartSec * 1000.0, targetFps, inverseRatio);
                if (mse < bestMse)
                {
                    secondMse = bestMse;
                    secondOffsetMs = bestOffsetMs;
                    bestMse = mse;
                    bestOffsetMs = offsetMs;
                }
                else if (mse < secondMse)
                {
                    secondMse = mse;
                    secondOffsetMs = offsetMs;
                }
            }

            if (double.IsPositiveInfinity(bestMse) || double.IsPositiveInfinity(secondMse))
            {
                anchor.RejectReason = "nessun candidato video valido";
                return result;
            }

            score = 1.0 / (1.0 + (bestMse / 2500.0));
            margin = !double.IsPositiveInfinity(secondMse) ? Math.Abs(secondMse - bestMse) / Math.Max(secondMse, 1.0) : 1.0;

            anchor.OffsetMs = bestOffsetMs;
            anchor.Score = score;
            anchor.Margin = margin;
            anchor.Accepted = score >= 0.35 && margin >= 0.04 && Math.Abs(bestOffsetMs - secondOffsetMs) >= searchStepMs;
            if (!anchor.Accepted)
            {
                anchor.RejectReason = "score/margine video bassi";
            }

            result = true;
            return result;
        }

        /// <summary>
        /// Calcola MSE medio tra frame source e una finestra language larga, usando uno start language candidato
        /// </summary>
        private double ComputeTimestampMatchedMseAtStart(List<byte[]> srcFrames, double[] srcTimestampsMs, List<byte[]> langFrames, double[] langTimestampsMs, double languageStartMs, double targetFps, double languageRelativeScale)
        {
            double total = 0.0;
            int count = 0;
            double toleranceMs = 1000.0 / Math.Max(targetFps, 1.0);

            if (srcFrames == null || langFrames == null || srcTimestampsMs == null || langTimestampsMs == null || srcFrames.Count == 0 || langFrames.Count == 0)
            {
                return double.PositiveInfinity;
            }

            for (int i = 0; i < srcFrames.Count && i < srcTimestampsMs.Length; i++)
            {
                double sourceRelativeMs = srcTimestampsMs[i] - srcTimestampsMs[0];
                double targetLangMs = languageStartMs + (sourceRelativeMs * languageRelativeScale);
                int languageIndex = NearestTimestampIndex(langTimestampsMs, targetLangMs);
                if (languageIndex < 0 || languageIndex >= langFrames.Count)
                {
                    continue;
                }

                if (Math.Abs(langTimestampsMs[languageIndex] - targetLangMs) > toleranceMs)
                {
                    continue;
                }

                total += this.ComputeMse(srcFrames[i], langFrames[languageIndex]);
                count++;
            }

            if (count >= Math.Max(3, srcFrames.Count / 2))
            {
                return total / count;
            }

            return double.PositiveInfinity;
        }

        #endregion

        #region Metodi privati — Fase 3: Raffinamento transizioni

        /// <summary>
        /// Valida che le transizioni non scartate abbiano prodotto operazioni applicabili
        /// </summary>
        private bool ValidateTimelineTransitions(List<DeepAnalysisTransitionDiagnostic> transitions, bool writeLog)
        {
            bool result = true;

            if (transitions == null)
            {
                return false;
            }

            for (int i = 0; i < transitions.Count; i++)
            {
                if (Math.Abs(transitions[i].DeltaMs) < 500)
                {
                    continue;
                }

                if (string.Equals(transitions[i].Status, "SkippedUnverified", StringComparison.Ordinal))
                {
                    if (writeLog)
                    {
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  Timeline rifiutata: transizione " + transitions[i].Index.ToString(CultureInfo.InvariantCulture) + " scartata dalla verifica locale (" + transitions[i].RejectReason + ")");
                    }
                    result = false;
                    break;
                }

                if (!string.Equals(transitions[i].Status, "Accepted", StringComparison.Ordinal) && !string.Equals(transitions[i].Status, "AcceptedTimeline", StringComparison.Ordinal) && !string.Equals(transitions[i].Status, "AcceptedTentative", StringComparison.Ordinal))
                {
                    if (writeLog)
                    {
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  Timeline rifiutata: transizione " + transitions[i].Index.ToString(CultureInfo.InvariantCulture) + " non risolta (" + transitions[i].RejectReason + ")");
                    }
                    result = false;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Salva diagnostica completa anche quando l'analisi fallisce
        /// </summary>
        private void StoreFailedAnalysis(Stopwatch stopwatch, DeepAnalysisDiagnostics diagnostics, string stretchFactor, List<OffsetRegion> regions, List<EditOperation> operations, double baselineMse)
        {
            if (stopwatch.IsRunning)
            {
                stopwatch.Stop();
            }

            this._analysisTimeMs = stopwatch.ElapsedMilliseconds;
            diagnostics.Timing.TotalMs = this._analysisTimeMs;

            EditMap map = new EditMap();
            map.InitialDelayMs = regions != null && regions.Count > 0 ? (int)Math.Round(regions[0].OffsetMs) : 0;
            map.StretchFactor = stretchFactor != null ? stretchFactor : "";
            map.Operations = operations != null ? operations : new List<EditOperation>();
            map.AnalysisTimeMs = this._analysisTimeMs;
            map.BaselineMse = baselineMse;
            map.Diagnostics = diagnostics;
            this._lastAnalysisMap = map;
        }

        /// <summary>
        /// Calcola il raggio massimo di raffinamento attorno al breakpoint tra due regioni
        /// </summary>
        /// <param name="leftRegion">Regione precedente</param>
        /// <param name="rightRegion">Regione successiva</param>
        /// <returns>Raggio in secondi</returns>
        private double GetTransitionRefineRadiusSec(OffsetRegion leftRegion, OffsetRegion rightRegion)
        {
            double result;
            double maxRadius = 20.0;
            double deltaMs;
            if (this._daConfig.WideProbeToleranceSec > 0.0)
            {
                maxRadius = this._daConfig.WideProbeToleranceSec;
            }

            if (this._currentAnalysisUsesTimelineMap && maxRadius < 45.0)
            {
                maxRadius = 45.0;
            }

            deltaMs = Math.Abs(rightRegion.OffsetMs - leftRegion.OffsetMs);
            if (deltaMs <= 1500.0)
            {
                result = 10.0;
            }
            else if (deltaMs <= 8000.0)
            {
                result = this._currentAnalysisUsesTimelineMap ? 45.0 : 15.0;
            }
            else
            {
                result = this._currentAnalysisUsesTimelineMap ? 45.0 : 20.0;
            }

            if (result > maxRadius) { result = maxRadius; }
            if (result < 3.0) { result = 3.0; }

            return result;
        }

        /// <summary>
        /// Cerca il crossover confrontando direttamente vecchio e nuovo offset nella finestra locale
        /// </summary>
        /// <param name="sourceFile">Percorso file source</param>
        /// <param name="langFile">Percorso file lang</param>
        /// <param name="searchStartSrc">Inizio finestra source in secondi</param>
        /// <param name="searchEndSrc">Fine finestra source in secondi</param>
        /// <param name="oldOffsetSec">Offset precedente in secondi</param>
        /// <param name="newOffsetSec">Offset successivo in secondi</param>
        /// <param name="inverseRatio">Rapporto inverso stretch</param>
        /// <param name="transition">Diagnostica transizione da popolare</param>
        /// <returns>Timestamp source del crossover, oppure -1 se non conclusivo</returns>
        private double DifferentialScanCrossover(string sourceFile, string langFile, double searchStartSrc, double searchEndSrc, double oldOffsetSec, double newOffsetSec, double inverseRatio, DeepAnalysisTransitionDiagnostic transition)
        {
            double result = -1.0;
            double duration = searchEndSrc - searchStartSrc;
            double langStartOld = searchStartSrc - oldOffsetSec;
            double langStartNew = searchStartSrc - newOffsetSec;
            List<byte[]> srcFrames;
            double[] sourceTimestampsMs;
            List<byte[]> langOldFrames;
            double[] langOldTimestampsMs;
            List<byte[]> langNewFrames;
            double[] langNewTimestampsMs;
            double toleranceMs = 100.0;
            int maxIdx;
            double targetOldMs;
            double targetNewMs;
            int oldIdx;
            int newIdx;
            double oldDistMs;
            double newDistMs;
            bool[] valid;
            bool[] changed;
            double[] oldScores;
            double[] newScores;
            double[] motionScores;
            double[] meanScores;
            double[] oldMseScores;
            double[] newMseScores;
            List<KeyValuePair<int, double>> candidates = new List<KeyValuePair<int, double>>();
            double beforeOldAdvantage;
            double afterNewAdvantage;
            double motionThreshold = 25.0;
            double cutSwitchMseMargin = 100.0;
            int minSideFrames = 5;
            int halfWindowFrames = 60;
            double minNewSsim = Math.Max(this._daConfig.OffsetProbeMinSsim, 0.60);
            double minMargin = 0.015;
            double duplicateSsim = 0.995;
            double beforeWindowMs = 3000.0;
            double afterWindowMs = 6000.0;
            double darkFrameMean = 8.0;
            int requiredAfterVotes = 2;
            int offsetDirection = Math.Sign(newOffsetSec - oldOffsetSec);
            bool collectCutDiagnostics = transition != null && duration > 60.0 && offsetDirection < 0 && Math.Abs(newOffsetSec - oldOffsetSec) <= 1.5;
            int candidateDiagnosticsLimit = 80;

            if (duration < 1.0)
            {
                return result;
            }

            if (offsetDirection == 0)
            {
                return result;
            }

            if (Math.Abs(inverseRatio - 1.0) > 0.0001)
            {
                langStartOld = langStartOld * inverseRatio;
                langStartNew = langStartNew * inverseRatio;
            }

            if (langStartOld < 0.0) { langStartOld = 0.0; }
            if (langStartNew < 0.0) { langStartNew = 0.0; }

            /*
             * Estrazione frame e scoring dei due offset candidati
             */

            // Estrae frame nativi per non perdere i PTS reali nei source VFR
            this.ExtractDeepSegment(sourceFile, (int)(searchStartSrc * 1000), duration, 0.0, this._geometryCropSourceToFourThree, this._analysisCropSourcePx, out srcFrames, out sourceTimestampsMs);
            this.ExtractDeepSegment(langFile, (int)(langStartOld * 1000), duration, 0.0, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx, out langOldFrames, out langOldTimestampsMs);
            this.ExtractDeepSegment(langFile, (int)(langStartNew * 1000), duration, 0.0, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx, out langNewFrames, out langNewTimestampsMs);

            if (srcFrames.Count < minSideFrames || langOldFrames.Count < minSideFrames || langNewFrames.Count < minSideFrames)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    Scansione differenziale: frame insufficienti");
                return result;
            }

            maxIdx = srcFrames.Count;
            valid = new bool[maxIdx];
            changed = new bool[maxIdx];
            oldScores = new double[maxIdx];
            newScores = new double[maxIdx];
            motionScores = new double[maxIdx];
            meanScores = new double[maxIdx];
            oldMseScores = new double[maxIdx];
            newMseScores = new double[maxIdx];

            // Allinea ogni frame source al frame lang piu' vicino per ciascun offset candidato
            for (int i = 0; i < maxIdx; i++)
            {
                targetOldMs = sourceTimestampsMs[i] - (oldOffsetSec * 1000.0);
                targetNewMs = sourceTimestampsMs[i] - (newOffsetSec * 1000.0);
                if (Math.Abs(inverseRatio - 1.0) > 0.0001)
                {
                    targetOldMs = targetOldMs * inverseRatio;
                    targetNewMs = targetNewMs * inverseRatio;
                }
                oldIdx = NearestTimestampIndex(langOldTimestampsMs, targetOldMs);
                newIdx = NearestTimestampIndex(langNewTimestampsMs, targetNewMs);

                if (oldIdx < 0 || oldIdx >= langOldFrames.Count || newIdx < 0 || newIdx >= langNewFrames.Count)
                {
                    continue;
                }

                oldDistMs = Math.Abs(langOldTimestampsMs[oldIdx] - targetOldMs);
                newDistMs = Math.Abs(langNewTimestampsMs[newIdx] - targetNewMs);
                if (oldDistMs > toleranceMs || newDistMs > toleranceMs)
                {
                    continue;
                }

                valid[i] = true;
                oldScores[i] = this.ComputeSsim(srcFrames[i], langOldFrames[oldIdx]);
                newScores[i] = this.ComputeSsim(srcFrames[i], langNewFrames[newIdx]);
                oldMseScores[i] = this.ComputeMse(srcFrames[i], langOldFrames[oldIdx]);
                newMseScores[i] = this.ComputeMse(srcFrames[i], langNewFrames[newIdx]);
                motionScores[i] = i == 0 ? 0.0 : this.ComputeMse(srcFrames[i - 1], srcFrames[i]);
                double pixelSum = 0.0;
                for (int p = 0; p < srcFrames[i].Length; p++)
                {
                    pixelSum += srcFrames[i][p];
                }
                meanScores[i] = pixelSum / srcFrames[i].Length;
                changed[i] = i == 0 || this.ComputeSsim(srcFrames[i - 1], srcFrames[i]) < duplicateSsim;
            }

            /*
             * Candidati: old deve vincere prima, new deve vincere dopo
             */

            // Cerca punti dove il vecchio offset domina prima e il nuovo offset domina dopo
            for (int i = minSideFrames; i < maxIdx - minSideFrames; i++)
            {
                if (!valid[i])
                {
                    continue;
                }

                List<double> before = new List<double>();
                List<double> after = new List<double>();
                int beforeStart = Math.Max(0, i - halfWindowFrames);
                int afterEnd = Math.Min(maxIdx, i + halfWindowFrames);

                for (int j = beforeStart; j < i; j++)
                {
                    if (valid[j])
                    {
                        before.Add(newMseScores[j] - oldMseScores[j]);
                    }
                }

                for (int j = i; j < afterEnd; j++)
                {
                    if (valid[j])
                    {
                        after.Add(oldMseScores[j] - newMseScores[j]);
                    }
                }

                if (before.Count < minSideFrames || after.Count < minSideFrames)
                {
                    continue;
                }

                before.Sort();
                after.Sort();
                beforeOldAdvantage = before[before.Count / 2];
                afterNewAdvantage = after[after.Count / 2];
                if (beforeOldAdvantage <= 0.0 || afterNewAdvantage <= 0.0)
                {
                    continue;
                }

                candidates.Add(new KeyValuePair<int, double>(i, beforeOldAdvantage + afterNewAdvantage));
            }

            if (offsetDirection > 0)
            {
                /*
                 * Ramo INSERT: boundary MSE robusto e verifica locale
                 */

                // INSERT: prima prova un boundary MSE robusto, utile quando SSIM satura su frame statici
                List<KeyValuePair<int, double>> insertCandidates = new List<KeyValuePair<int, double>>(candidates);
                double mseInsertResult = -1.0;
                double mseInsertScore = 0.0;
                double mseInsertOldMse = double.MaxValue;
                double mseInsertNewMse = double.MaxValue;
                bool mseInsertAccepted = false;
                bool longInsertTransition = Math.Abs(newOffsetSec - oldOffsetSec) > 1.5;
                double insertRunMseMargin = 100.0;
                double insertRunTieMargin = 5.0;
                double insertRunPreferredBoundaryMargin = 1500.0;
                double longInsertTargetSec = transition != null && transition.BreakpointSrcSec > 0.0 ? transition.BreakpointSrcSec : (searchStartSrc + (duration / 2.0));
                insertCandidates.Sort((left, right) => right.Value.CompareTo(left.Value));

                // Valuta prima i candidati con differenziale MSE piu' forte: sono i piu' stabili
                // quando SSIM resta alto su pose statiche, fade o frame quasi identici.
                for (int c = 0; c < insertCandidates.Count; c++)
                {
                    int candidateIdx = insertCandidates[c].Key;
                    int boundaryIdx = candidateIdx;
                    DeepAnalysisLocalVerificationDiagnostic candidateVerification;
                    DeepAnalysisTransitionCandidateDiagnostic candidateDiagnostic = null;
                    bool candidateRejected;

                    // Sposta il candidato sul primo frame vicino dove il nuovo offset diventa almeno
                    // competitivo e c'e' movimento reale nel source.
                    for (int i = candidateIdx; i < maxIdx && i < candidateIdx + 120; i++)
                    {
                        if (valid[i] && motionScores[i] >= motionThreshold && oldMseScores[i] >= newMseScores[i])
                        {
                            boundaryIdx = i;
                            break;
                        }
                    }
                    if (boundaryIdx + 1 < maxIdx && valid[boundaryIdx + 1] && motionScores[boundaryIdx + 1] < motionThreshold && oldMseScores[boundaryIdx] - newMseScores[boundaryIdx] < 500.0)
                    {
                        // Se il boundary cade dentro una posa statica, prova a spostarlo al primo frame con movimento reale
                        int staticBoundaryIdx = boundaryIdx;
                        for (int i = boundaryIdx + 1; i < maxIdx && sourceTimestampsMs[i] - sourceTimestampsMs[boundaryIdx] <= 3000.0; i++)
                        {
                            if (valid[i] && motionScores[i] >= motionThreshold)
                            {
                                if (oldMseScores[i] - newMseScores[i] < oldMseScores[staticBoundaryIdx] - newMseScores[staticBoundaryIdx])
                                {
                                    boundaryIdx = i;
                                }
                                break;
                            }
                        }
                    }

                    // Se il boundary cade in nero/fade, risale finche' il nuovo offset rimane nettamente migliore:
                    // l'edit deve partire all'inizio del tratto source-only, non al primo frame luminoso.
                    if (meanScores[boundaryIdx] <= darkFrameMean)
                    {
                        int rewindIdx = boundaryIdx;
                        double rewindWindowMs = Math.Max(1000.0, Math.Abs(newOffsetSec - oldOffsetSec) * 1000.0 + 500.0);
                        for (int i = boundaryIdx - 1; i >= 0 && sourceTimestampsMs[boundaryIdx] - sourceTimestampsMs[i] <= rewindWindowMs; i--)
                        {
                            if (!valid[i])
                            {
                                continue;
                            }

                            if (meanScores[i] > darkFrameMean)
                            {
                                break;
                            }

                            if (newMseScores[i] <= 1200.0 && newMseScores[i] <= oldMseScores[i] * 0.60 && oldMseScores[i] - newMseScores[i] >= 250.0)
                            {
                                rewindIdx = i;
                                continue;
                            }

                            if (oldMseScores[i] <= newMseScores[i] * 0.98)
                            {
                                break;
                            }
                        }

                        boundaryIdx = rewindIdx;
                    }

                    // Un MSE quasi perfetto spesso identifica contenuto gia' agganciato dopo il gap:
                    // risale al primo frame della stessa run perfetta, se esiste.
                    if (newMseScores[boundaryIdx] <= 100.0)
                    {
                        int contentStartIdx = boundaryIdx;
                        for (int i = boundaryIdx - 1; i >= 0 && sourceTimestampsMs[boundaryIdx] - sourceTimestampsMs[i] <= 1000.0; i--)
                        {
                            if (!valid[i])
                            {
                                continue;
                            }

                            if (newMseScores[i] <= 100.0 && newMseScores[i] <= oldMseScores[i] * 0.50)
                            {
                                contentStartIdx = i;
                                continue;
                            }

                            if (oldMseScores[i] <= newMseScores[i] * 0.98)
                            {
                                break;
                            }
                        }

                        boundaryIdx = contentStartIdx;
                    }

                    // Risale la run new-dominante evitando di attraversare frame dove old torna chiaramente migliore.
                    if (oldMseScores[boundaryIdx] - newMseScores[boundaryIdx] >= insertRunMseMargin)
                    {
                        int runStartIdx = boundaryIdx;
                        double rewindWindowMs = Math.Max(1000.0, Math.Abs(newOffsetSec - oldOffsetSec) * 1000.0 + 500.0);
                        for (int i = boundaryIdx - 1; i >= 0 && sourceTimestampsMs[boundaryIdx] - sourceTimestampsMs[i] <= rewindWindowMs; i--)
                        {
                            if (!valid[i])
                            {
                                continue;
                            }

                            double newAdvantage = oldMseScores[i] - newMseScores[i];
                            if (newMseScores[i] - oldMseScores[i] >= insertRunMseMargin)
                            {
                                break;
                            }

                            if (newAdvantage >= insertRunMseMargin || (newMseScores[i] <= oldMseScores[i] * 0.98 && newAdvantage >= insertRunTieMargin))
                            {
                                runStartIdx = i;
                            }
                        }

                        boundaryIdx = runStartIdx;
                    }

                    result = sourceTimestampsMs[boundaryIdx] / 1000.0;

                    // Le transizioni INSERT lunghe possono essere confermate globalmente dalla timeline:
                    // in diagnostica restano visibili ma non pagano la verifica locale audio/video.
                    if (transition != null && transition.Candidates.Count < candidateDiagnosticsLimit && longInsertTransition)
                    {
                        transition.Candidates.Add(new DeepAnalysisTransitionCandidateDiagnostic
                        {
                            SourceSec = result,
                            Score = insertCandidates[c].Value,
                            MotionMse = motionScores[boundaryIdx],
                            OldMse = oldMseScores[boundaryIdx],
                            NewMse = newMseScores[boundaryIdx],
                            Verified = false,
                            CanDeferToGlobalVerification = true,
                            AudioRejected = false,
                            Decision = "insert-mse-unverified-large-delta"
                        });
                    }

                    // Per delta piccoli invece il boundary MSE deve superare la verifica locale.
                    // Se fallisce ma il differenziale e' molto forte, accetta solo se old vince subito prima
                    // e new ha abbastanza voti subito dopo.
                    if (!longInsertTransition)
                    {
                        candidateVerification = this.VerifyTransitionLocal(sourceFile, langFile, result, oldOffsetSec, newOffsetSec, inverseRatio);
                        if (transition != null && transition.Candidates.Count < candidateDiagnosticsLimit)
                        {
                            candidateDiagnostic = new DeepAnalysisTransitionCandidateDiagnostic
                            {
                                SourceSec = result,
                                Score = insertCandidates[c].Value,
                                MotionMse = motionScores[boundaryIdx],
                                OldMse = oldMseScores[boundaryIdx],
                                NewMse = newMseScores[boundaryIdx],
                                Verified = candidateVerification != null && candidateVerification.Verified,
                                CanDeferToGlobalVerification = candidateVerification != null && candidateVerification.CanDeferToGlobalVerification,
                                AudioRejected = candidateVerification != null && candidateVerification.AudioRejected,
                                Decision = candidateVerification == null || (!candidateVerification.Verified && !candidateVerification.CanDeferToGlobalVerification) ? "insert-mse-rejected-local" : "insert-mse-accepted-local"
                            };
                            transition.Candidates.Add(candidateDiagnostic);
                        }
                        candidateRejected = candidateVerification == null || (!candidateVerification.Verified && !candidateVerification.CanDeferToGlobalVerification);
                        if (candidateRejected && oldMseScores[boundaryIdx] - newMseScores[boundaryIdx] >= 1000.0)
                        {
                            int oldBeforeIdx = -1;
                            int newAfterVotes = 0;
                            for (int i = boundaryIdx - 1; i >= 0 && sourceTimestampsMs[boundaryIdx] - sourceTimestampsMs[i] <= 500.0; i--)
                            {
                                if (!valid[i])
                                {
                                    continue;
                                }

                                if (newMseScores[i] - oldMseScores[i] >= insertRunMseMargin)
                                {
                                    oldBeforeIdx = i;
                                    break;
                                }
                            }

                            for (int i = boundaryIdx; i < maxIdx && sourceTimestampsMs[i] - sourceTimestampsMs[boundaryIdx] <= 1000.0; i++)
                            {
                                if (!valid[i])
                                {
                                    continue;
                                }

                                if (newMseScores[i] - oldMseScores[i] >= insertRunMseMargin)
                                {
                                    break;
                                }

                                if (oldMseScores[i] - newMseScores[i] >= insertRunMseMargin || (newMseScores[i] <= oldMseScores[i] * 0.98 && oldMseScores[i] - newMseScores[i] >= insertRunTieMargin))
                                {
                                    newAfterVotes++;
                                }
                            }

                            if (oldBeforeIdx >= 0 && newAfterVotes >= 3)
                            {
                                candidateRejected = false;
                                if (candidateDiagnostic != null)
                                {
                                    candidateDiagnostic.CanDeferToGlobalVerification = true;
                                    candidateDiagnostic.Decision = "accepted-strong-differential";
                                }
                            }
                        }

                        if (candidateRejected)
                        {
                            continue;
                        }
                    }

                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione differenziale: boundary video a src " + result.ToString("F3", CultureInfo.InvariantCulture) + "s (score=" + insertCandidates[c].Value.ToString("F1", CultureInfo.InvariantCulture) + ")");
                    mseInsertResult = result;
                    mseInsertScore = insertCandidates[c].Value;
                    mseInsertOldMse = oldMseScores[boundaryIdx];
                    mseInsertNewMse = newMseScores[boundaryIdx];
                    mseInsertAccepted = true;
                    break;
                }

                // Se il boundary MSE e' vicino al breakpoint di una INSERT lunga, e' gia' il punto operativo.
                // Il passaggio SSIM successivo rischierebbe di spostarsi sul contenuto visibile dopo il gap.
                if (mseInsertResult >= 0.0 && longInsertTransition && Math.Abs(mseInsertResult - longInsertTargetSec) <= 2.0)
                {
                    double insertDurationSec = Math.Abs(newOffsetSec - oldOffsetSec);
                    if (mseInsertNewMse <= 100.0)
                    {
                        double shiftedResult = mseInsertResult - insertDurationSec;
                        if (mseInsertResult - longInsertTargetSec >= insertDurationSec * 0.75 &&
                            Math.Abs(shiftedResult - longInsertTargetSec) <= 1.0 &&
                            shiftedResult >= searchStartSrc)
                        {
                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione differenziale: boundary MSE post-gap riportato a src " + shiftedResult.ToString("F3", CultureInfo.InvariantCulture) + "s da contenuto " + mseInsertResult.ToString("F3", CultureInfo.InvariantCulture) + "s");
                            return shiftedResult;
                        }
                    }

                    // Nei gap INSERT lunghi il punto operativo e' l'inizio dello stacco nero/fade source-only:
                    // un match SSIM piu' tardo sul contenuto non deve sostituire un boundary MSE vicino al breakpoint timeline.
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione differenziale: uso boundary MSE insert lungo a src " + mseInsertResult.ToString("F3", CultureInfo.InvariantCulture) + "s (score=" + mseInsertScore.ToString("F1", CultureInfo.InvariantCulture) + ")");
                    return mseInsertResult;
                }

                /*
                 * Ramo INSERT: fallback SSIM su frame realmente cambiati
                 */

                // Secondo passaggio INSERT: usa solo frame cambiati e richiede voti SSIM coerenti dopo il boundary
                for (int i = 0; i < maxIdx; i++)
                {
                    int afterNewVotes = 0;
                    int afterOldVotes = 0;
                    int beforeNewVotes = 0;
                    int beforeOldVotes = 0;
                    int beforeEvidence = 0;

                    // Il frame candidato deve gia' favorire new sia in SSIM sia in MSE.
                    if (!valid[i] || newScores[i] < minNewSsim || newScores[i] <= oldScores[i] || newMseScores[i] > oldMseScores[i] * 1.02)
                    {
                        continue;
                    }

                    if (i > 0 && !changed[i] && meanScores[i] > darkFrameMean)
                    {
                        continue;
                    }

                    // Dopo il boundary servono piu' voti new che old, altrimenti e' un match isolato.
                    for (int j = i; j < maxIdx && sourceTimestampsMs[j] - sourceTimestampsMs[i] <= afterWindowMs; j++)
                    {
                        if (!valid[j] || (j > i && !changed[j] && meanScores[j] > darkFrameMean))
                        {
                            continue;
                        }

                        if (newScores[j] >= minNewSsim && newScores[j] > oldScores[j] + minMargin && newMseScores[j] <= oldMseScores[j] * 1.02)
                        {
                            afterNewVotes++;
                        }
                        else if (oldScores[j] >= minNewSsim && oldScores[j] > newScores[j] + minMargin && oldMseScores[j] <= newMseScores[j] * 1.02)
                        {
                            afterOldVotes++;
                        }
                    }

                    if (afterNewVotes < requiredAfterVotes || afterNewVotes <= afterOldVotes)
                    {
                        continue;
                    }

                    // Prima del boundary non deve esserci gia' evidenza forte per new, altrimenti il punto e' tardivo.
                    for (int j = i - 1; j >= 0 && sourceTimestampsMs[i] - sourceTimestampsMs[j] <= beforeWindowMs; j--)
                    {
                        if (!valid[j] || (!changed[j] && meanScores[j] > darkFrameMean))
                        {
                            continue;
                        }

                        if (newScores[j] >= minNewSsim && newScores[j] > oldScores[j] + minMargin && newMseScores[j] <= oldMseScores[j] * 1.02)
                        {
                            if (meanScores[j] <= darkFrameMean)
                            {
                                continue;
                            }

                            beforeEvidence++;
                            beforeNewVotes++;
                        }
                        else if (oldScores[j] >= minNewSsim && oldScores[j] > newScores[j] + minMargin && oldMseScores[j] <= newMseScores[j] * 1.02)
                        {
                            beforeEvidence++;
                            beforeOldVotes++;
                        }
                    }

                    if (beforeEvidence > 0 && beforeNewVotes > beforeOldVotes && beforeNewVotes >= requiredAfterVotes)
                    {
                        continue;
                    }

                    int ssimBoundaryIdx = i;

                    // Anche per SSIM, se il match cade in nero/fade prova a risalire dentro la stessa run.
                    if (meanScores[ssimBoundaryIdx] <= darkFrameMean)
                    {
                        double rewindWindowMs = Math.Max(1000.0, Math.Abs(newOffsetSec - oldOffsetSec) * 1000.0 + 500.0);
                        for (int j = ssimBoundaryIdx - 1; j >= 0 && sourceTimestampsMs[ssimBoundaryIdx] - sourceTimestampsMs[j] <= rewindWindowMs; j--)
                        {
                            if (!valid[j])
                            {
                                continue;
                            }

                            if (meanScores[j] > darkFrameMean)
                            {
                                break;
                            }

                            if (newMseScores[j] <= 1200.0 && newMseScores[j] <= oldMseScores[j] * 0.60 && oldMseScores[j] - newMseScores[j] >= 250.0)
                            {
                                ssimBoundaryIdx = j;
                                continue;
                            }

                            if (oldMseScores[j] <= newMseScores[j] * 0.98)
                            {
                                break;
                            }
                        }
                    }

                    result = sourceTimestampsMs[ssimBoundaryIdx] / 1000.0;

                    // Le INSERT lunghe tengono il candidato SSIM come diagnostica deferibile; quelle corte
                    // richiedono comunque verifica locale prima di produrre un edit.
                    if (transition != null && transition.Candidates.Count < candidateDiagnosticsLimit && longInsertTransition)
                    {
                        transition.Candidates.Add(new DeepAnalysisTransitionCandidateDiagnostic
                        {
                            SourceSec = result,
                            Score = afterNewVotes - afterOldVotes,
                            MotionMse = motionScores[ssimBoundaryIdx],
                            OldMse = oldMseScores[ssimBoundaryIdx],
                            NewMse = newMseScores[ssimBoundaryIdx],
                            Verified = false,
                            CanDeferToGlobalVerification = true,
                            AudioRejected = false,
                            Decision = "insert-ssim-unverified-large-delta"
                        });
                    }
                    if (!longInsertTransition)
                    {
                        DeepAnalysisLocalVerificationDiagnostic candidateVerification = this.VerifyTransitionLocal(sourceFile, langFile, result, oldOffsetSec, newOffsetSec, inverseRatio);
                        if (transition != null && transition.Candidates.Count < candidateDiagnosticsLimit)
                        {
                            transition.Candidates.Add(new DeepAnalysisTransitionCandidateDiagnostic
                            {
                                SourceSec = result,
                                Score = afterNewVotes - afterOldVotes,
                                MotionMse = motionScores[ssimBoundaryIdx],
                                OldMse = oldMseScores[ssimBoundaryIdx],
                                NewMse = newMseScores[ssimBoundaryIdx],
                                Verified = candidateVerification != null && candidateVerification.Verified,
                                CanDeferToGlobalVerification = candidateVerification != null && candidateVerification.CanDeferToGlobalVerification,
                                AudioRejected = candidateVerification != null && candidateVerification.AudioRejected,
                                Decision = candidateVerification == null || (!candidateVerification.Verified && !candidateVerification.CanDeferToGlobalVerification) ? "insert-ssim-rejected-local" : "insert-ssim-accepted-local"
                            });
                        }
                        if (candidateVerification == null || (!candidateVerification.Verified && !candidateVerification.CanDeferToGlobalVerification))
                        {
                            continue;
                        }
                    }

                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione differenziale: boundary video a src " + result.ToString("F3", CultureInfo.InvariantCulture) + "s (after new=" + afterNewVotes.ToString(CultureInfo.InvariantCulture) + ", old=" + afterOldVotes.ToString(CultureInfo.InvariantCulture) + ")");
                    if (mseInsertAccepted &&
                        mseInsertOldMse - mseInsertNewMse >= insertRunPreferredBoundaryMargin &&
                        mseInsertResult >= 0.0 &&
                        mseInsertResult <= result &&
                        result - mseInsertResult <= 3.0)
                    {
                        // Usa il primo boundary MSE solo quando il cambio e' netto: in fade/logo ambigui SSIM
                        // puo' trovare un frame quasi perfetto piu' avanti e non va scavalcato da un vantaggio debole.
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione differenziale: uso boundary MSE insert a src " + mseInsertResult.ToString("F3", CultureInfo.InvariantCulture) + "s (score=" + mseInsertScore.ToString("F1", CultureInfo.InvariantCulture) + ")");
                        return mseInsertResult;
                    }

                    if (longInsertTransition && newMseScores[ssimBoundaryIdx] <= 100.0)
                    {
                        double insertDurationSec = Math.Abs(newOffsetSec - oldOffsetSec);
                        double shiftedResult = result - insertDurationSec;
                        if (result - longInsertTargetSec >= insertDurationSec * 0.75 &&
                            Math.Abs(shiftedResult - longInsertTargetSec) <= 1.0 &&
                            shiftedResult >= searchStartSrc)
                        {
                            // Se SSIM aggancia il primo contenuto dopo un gap source-only, l'edit operativo parte all'inizio del gap.
                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione differenziale: boundary SSIM post-gap riportato a src " + shiftedResult.ToString("F3", CultureInfo.InvariantCulture) + "s da contenuto " + result.ToString("F3", CultureInfo.InvariantCulture) + "s");
                            return shiftedResult;
                        }
                    }

                    return result;
                }

                if (mseInsertResult >= 0.0)
                {
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione differenziale: fallback boundary MSE insert a src " + mseInsertResult.ToString("F3", CultureInfo.InvariantCulture) + "s (score=" + mseInsertScore.ToString("F1", CultureInfo.InvariantCulture) + ")");
                    return mseInsertResult;
                }

                result = -1.0;
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    Scansione differenziale: non conclusiva");
                return result;
            }
            /*
             * Ramo CUT: candidati MSE con validazione locale limitata
             */

            // CUT: scorre i candidati MSE e valida solo una quota limitata di boundary locali
            int verifiedCutCandidates = 0;
            double strongMotionThreshold = 10000.0;
            double strongDifferentialScore = 100.0;
            double cutRewindMargin = 5.0;
            for (int c = 0; c < candidates.Count; c++)
            {
                int candidateIdx = candidates[c].Key;
                int boundaryIdx = candidateIdx;
                DeepAnalysisLocalVerificationDiagnostic candidateVerification;
                DeepAnalysisTransitionCandidateDiagnostic candidateDiagnostic = null;
                bool candidateRejected;
                bool candidateLocalVerifiedOrDeferred;
                bool strongDifferentialCut;
                bool timelineCutStrongBoundary;
                bool timelineCutOldBeforeEvidence;
                bool timelineCutForwardOldDominates;
                bool timelineCutBoundaryAccepted = false;

                // Parte dal candidato differenziale e cerca poco avanti il primo frame con motion
                // sufficiente e vantaggio MSE stabile per il nuovo offset.
                for (int i = candidateIdx; i < maxIdx && i < candidateIdx + 120; i++)
                {
                    if (valid[i] && motionScores[i] >= motionThreshold && oldMseScores[i] - newMseScores[i] >= cutSwitchMseMargin)
                    {
                        boundaryIdx = i;
                        break;
                    }
                }

                if (oldMseScores[boundaryIdx] - newMseScores[boundaryIdx] >= cutSwitchMseMargin)
                {
                    // Nei CUT il primo frame utile puo' precedere il frame con contrasto piu' forte:
                    // risale la run new-dominante senza attraversare un frame dove il vecchio offset torna corretto.
                    int rewindIdx = boundaryIdx;
                    double rewindWindowMs = Math.Max(1000.0, Math.Abs(newOffsetSec - oldOffsetSec) * 1000.0 + 500.0);
                    for (int i = boundaryIdx - 1; i >= 0 && sourceTimestampsMs[boundaryIdx] - sourceTimestampsMs[i] <= rewindWindowMs; i--)
                    {
                        if (!valid[i])
                        {
                            continue;
                        }

                        double newAdvantage = oldMseScores[i] - newMseScores[i];
                        if (oldMseScores[i] <= newMseScores[i] * 0.98 && -newAdvantage >= cutRewindMargin)
                        {
                            break;
                        }

                        if (newAdvantage >= cutSwitchMseMargin || (newMseScores[i] <= oldMseScores[i] * 0.98 && newAdvantage >= cutRewindMargin))
                        {
                            rewindIdx = i;
                            continue;
                        }

                        if (motionScores[i] >= motionThreshold)
                        {
                            break;
                        }
                    }

                    boundaryIdx = rewindIdx;
                }

                result = sourceTimestampsMs[boundaryIdx] / 1000.0;
                if (Math.Abs(newOffsetSec - oldOffsetSec) > 1.5 && verifiedCutCandidates >= 30)
                {
                    // Su CUT grandi evita una scansione locale troppo costosa: oltre i primi candidati
                    // forti lascia fallire il differenziale e passare ai fallback superiori.
                    break;
                }

                verifiedCutCandidates++;

                // La verifica locale confronta audio/video prima e dopo il boundary proposto.
                // Se fallisce, il candidato puo' sopravvivere solo con evidenza visuale molto forte.
                candidateVerification = this.VerifyTransitionLocal(sourceFile, langFile, result, oldOffsetSec, newOffsetSec, inverseRatio);
                if (collectCutDiagnostics && transition.Candidates.Count < candidateDiagnosticsLimit)
                {
                    candidateDiagnostic = new DeepAnalysisTransitionCandidateDiagnostic
                    {
                        SourceSec = result,
                        Score = candidates[c].Value,
                        MotionMse = motionScores[boundaryIdx],
                        OldMse = oldMseScores[boundaryIdx],
                        NewMse = newMseScores[boundaryIdx],
                        Verified = candidateVerification != null && candidateVerification.Verified,
                        CanDeferToGlobalVerification = candidateVerification != null && candidateVerification.CanDeferToGlobalVerification,
                        AudioRejected = candidateVerification != null && candidateVerification.AudioRejected,
                        Decision = candidateVerification == null || (!candidateVerification.Verified && !candidateVerification.CanDeferToGlobalVerification) ? "rejected-local" : "accepted-local"
                    };
                    transition.Candidates.Add(candidateDiagnostic);
                }
                candidateRejected = candidateVerification == null || (!candidateVerification.Verified && !candidateVerification.CanDeferToGlobalVerification);
                candidateLocalVerifiedOrDeferred = candidateVerification != null && (candidateVerification.Verified || candidateVerification.CanDeferToGlobalVerification);
                timelineCutOldBeforeEvidence = false;
                // Se la finestra forward conferma ancora il vecchio offset, non promuove un boundary timeline anticipato.
                timelineCutForwardOldDominates = candidateVerification != null &&
                    candidateVerification.ForwardNewMse > candidateVerification.ForwardOldMse * 1.25 &&
                    candidateVerification.ForwardNewMse - candidateVerification.ForwardOldMse >= cutRewindMargin;

                // In timeline map un CUT vero deve mostrare old migliore poco prima del boundary:
                // e' la guardia che distingue un cambio offset reale da un candidato anticipato.
                if (this._currentAnalysisUsesTimelineMap && offsetDirection < 0)
                {
                    double oldBeforeWindowMs = Math.Max(1000.0, Math.Abs(newOffsetSec - oldOffsetSec) * 1000.0 + 500.0);
                    for (int i = boundaryIdx - 1; i >= 0 && sourceTimestampsMs[boundaryIdx] - sourceTimestampsMs[i] <= oldBeforeWindowMs; i--)
                    {
                        if (!valid[i])
                        {
                            continue;
                        }

                        if (oldMseScores[i] <= newMseScores[i] * 0.98 && newMseScores[i] - oldMseScores[i] >= cutRewindMargin)
                        {
                            timelineCutOldBeforeEvidence = true;
                            break;
                        }
                    }
                }

                // strongDifferentialCut e' il bypass visuale generico; timelineCutStrongBoundary e'
                // piu' restrittivo e vale solo quando la timeline conferma il pattern old->new.
                strongDifferentialCut = collectCutDiagnostics &&
                    candidates[c].Value >= strongDifferentialScore &&
                    oldMseScores[boundaryIdx] - newMseScores[boundaryIdx] >= cutSwitchMseMargin &&
                    (candidateVerification == null || !candidateVerification.AudioRejected);
                timelineCutStrongBoundary = this._currentAnalysisUsesTimelineMap &&
                    offsetDirection < 0 &&
                    timelineCutOldBeforeEvidence &&
                    !timelineCutForwardOldDominates &&
                    oldMseScores[boundaryIdx] - newMseScores[boundaryIdx] >= 1000.0 &&
                    (candidateVerification == null || !candidateVerification.AudioRejected);
                if (candidateRejected && !strongDifferentialCut && !timelineCutStrongBoundary)
                {
                    continue;
                }

                // Se il candidato e' accettato solo da timeline map, impedisce il successivo
                // spostamento automatico su motion forte: quel fallback lo renderebbe tardivo.
                if (candidateRejected && (strongDifferentialCut || timelineCutStrongBoundary))
                {
                    timelineCutBoundaryAccepted = timelineCutStrongBoundary;
                }
                if (candidateRejected && strongDifferentialCut && candidateDiagnostic != null)
                {
                    candidateDiagnostic.Decision = "accepted-strong-differential";
                }
                if (candidateRejected && timelineCutStrongBoundary && candidateDiagnostic != null)
                {
                    candidateDiagnostic.CanDeferToGlobalVerification = true;
                    candidateDiagnostic.Decision = "accepted-timeline-cut-boundary";
                }

                if (duration > 60.0 && Math.Abs(newOffsetSec - oldOffsetSec) <= 1.5 && boundaryIdx >= 0 && boundaryIdx < motionScores.Length && !(this._currentAnalysisUsesTimelineMap && offsetDirection < 0 && (candidateLocalVerifiedOrDeferred || timelineCutBoundaryAccepted)))
                {
                    // Nei CUT lunghi con boundary debole prova l'ultimo spostamento su motion forte,
                    // ma non scavalca candidati timeline gia' verificati o deferibili.
                    for (int i = boundaryIdx + 1; i < maxIdx; i++)
                    {
                        if (!valid[i] || motionScores[i] < strongMotionThreshold)
                        {
                            continue;
                        }

                        double motionBoundary = sourceTimestampsMs[i] / 1000.0;
                        DeepAnalysisLocalVerificationDiagnostic motionVerification = this.VerifyTransitionLocal(sourceFile, langFile, motionBoundary, oldOffsetSec, newOffsetSec, inverseRatio);
                        if (motionVerification != null && motionVerification.Verified)
                        {
                            if (collectCutDiagnostics && transition.Candidates.Count < candidateDiagnosticsLimit)
                            {
                                transition.Candidates.Add(new DeepAnalysisTransitionCandidateDiagnostic
                                {
                                    SourceSec = motionBoundary,
                                    Score = candidates[c].Value,
                                    MotionMse = motionScores[i],
                                    OldMse = oldMseScores[i],
                                    NewMse = newMseScores[i],
                                    Verified = motionVerification.Verified,
                                    CanDeferToGlobalVerification = motionVerification.CanDeferToGlobalVerification,
                                    AudioRejected = motionVerification.AudioRejected,
                                    Decision = "accepted-strong-motion"
                                });
                            }
                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione differenziale: boundary spostato su motion forte a src " + motionBoundary.ToString("F3", CultureInfo.InvariantCulture) + "s (motion=" + motionScores[i].ToString("F1", CultureInfo.InvariantCulture) + ")");
                            return motionBoundary;
                        }
                    }
                }

                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione differenziale: boundary video a src " + result.ToString("F3", CultureInfo.InvariantCulture) + "s (score=" + candidates[c].Value.ToString("F1", CultureInfo.InvariantCulture) + ")");
                return result;
            }

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    Scansione differenziale: non conclusiva");

            return result;
        }

        /// <summary>
        /// Estrae un envelope audio mono PCM tramite ffmpeg
        /// </summary>
        /// <param name="filePath">Percorso file</param>
        /// <param name="startSec">Start in secondi</param>
        /// <param name="durationSec">Durata in secondi</param>
        /// <param name="windowMs">Finestra envelope in ms</param>
        /// <returns>Envelope normalizzato, oppure null</returns>
        private double[] ExtractAudioEnvelope(string filePath, double startSec, double durationSec, int windowMs)
        {
            int audioStreamIndex = 0;
            if (this._currentTrackPolicy != null && this._currentTrackPolicy.AudioValidationAvailable)
            {
                if (string.Equals(filePath, this._currentAnalysisSourceFile, StringComparison.Ordinal))
                {
                    audioStreamIndex = this._currentTrackPolicy.SourceAudioStreamIndex;
                }
                else if (string.Equals(filePath, this._currentAnalysisLanguageFile, StringComparison.Ordinal))
                {
                    audioStreamIndex = this._currentTrackPolicy.LanguageAudioStreamIndex;
                }
            }

            return this._audioEnvelopeService.Extract(filePath, startSec, durationSec, windowMs, audioStreamIndex);
        }

        /// <summary>
        /// Registra una estrazione envelope audio nei contatori performance
        /// </summary>
        private void RecordAudioEnvelopeExtract(long elapsedMs)
        {
            lock (this._performanceDiagnosticsLock)
            {
                if (this._performanceDiagnostics != null)
                {
                    this._performanceDiagnostics.AudioEnvelopeExtractCalls++;
                    this._performanceDiagnostics.AudioEnvelopeExtractMs += elapsedMs;
                }
            }
        }

        /// <summary>
        /// Scansione lineare di conferma attorno al punto trovato dalla binaria
        /// Cerca il primo frame dove il vecchio offset smette di produrre match visivo
        /// </summary>
        /// <param name="sourceFile">Percorso file source</param>
        /// <param name="langFile">Percorso file lang</param>
        /// <param name="approximateSrc">Punto approssimato dalla binaria in secondi source</param>
        /// <param name="oldOffsetSec">Offset vecchio in secondi</param>
        /// <param name="inverseRatio">Rapporto inverso stretch</param>
        /// <returns>Timestamp source raffinato del crossover in secondi</returns>
        private double LinearScanConfirm(string sourceFile, string langFile, double approximateSrc, double oldOffsetSec, double inverseRatio)
        {
            double result = approximateSrc;
            double scanStart = approximateSrc - this._daConfig.LinearScanWindowSec;
            if (scanStart < 0.0) { scanStart = 0.0; }
            double scanDuration = this._daConfig.LinearScanWindowSec * 2.0;
            List<byte[]> srcFrames;
            double[] sourceTimestampsMs;
            List<byte[]> langOldFrames;
            double[] langOldTimestampsMs;
            double toleranceMs;
            double targetLangOldMs;
            int nearestOldIdx;
            double nearestOldDistMs;
            double ssimOld;
            // Estrai frame source a fps nativo nella finestra (passthrough)
            this.ExtractDeepSegment(sourceFile, (int)(scanStart * 1000), scanDuration, 0.0, this._geometryCropSourceToFourThree, this._analysisCropSourcePx, out srcFrames, out sourceTimestampsMs);
            if (srcFrames.Count < 4 || sourceTimestampsMs.Length < 4) { return result; }

            // Posizione lang col vecchio offset
            double langStartOld = scanStart - oldOffsetSec;
            if (Math.Abs(inverseRatio - 1.0) > 0.0001) { langStartOld = langStartOld * inverseRatio; }
            if (langStartOld < 0.0) { langStartOld = 0.0; }

            // Estrae il vecchio offset: questa conferma lineare cerca il primo punto dove old smette di funzionare
            this.ExtractDeepSegment(langFile, (int)(langStartOld * 1000), scanDuration, 0.0, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx, out langOldFrames, out langOldTimestampsMs);

            if (langOldFrames.Count < 4 || langOldTimestampsMs.Length < 4) { return result; }

            int maxIdx = srcFrames.Count;

            // Tolleranza ampia: scanDuration lo fps nativo puo' variare; uso 100ms
            toleranceMs = 100.0;

            // Scorri e trova il primo frame dove old offset smette di funzionare
            // (SSIM_old < 0.5) per almeno this._daConfig.LinearScanConfirmFrames consecutivi
            // Matching per tempo relativo: robusto a VFR
            int consecutiveBad = 0;
            int crossoverIdx = -1;

            for (int i = 0; i < maxIdx; i++)
            {
                targetLangOldMs = sourceTimestampsMs[i] - (oldOffsetSec * 1000.0);
                if (Math.Abs(inverseRatio - 1.0) > 0.0001)
                {
                    targetLangOldMs = targetLangOldMs * inverseRatio;
                }
                nearestOldIdx = NearestTimestampIndex(langOldTimestampsMs, targetLangOldMs);
                if (nearestOldIdx < 0 || nearestOldIdx >= langOldFrames.Count) { continue; }
                nearestOldDistMs = Math.Abs(langOldTimestampsMs[nearestOldIdx] - targetLangOldMs);
                if (nearestOldDistMs > toleranceMs) { continue; }

                ssimOld = this.ComputeSsim(srcFrames[i], langOldFrames[nearestOldIdx]);

                if (ssimOld < 0.5)
                {
                    if (consecutiveBad == 0)
                    {
                        crossoverIdx = i;
                    }
                    consecutiveBad++;

                    if (consecutiveBad >= this._daConfig.LinearScanConfirmFrames)
                    {
                        // Posizione reale dal pts del frame sorgente
                        result = sourceTimestampsMs[crossoverIdx] / 1000.0;
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione lineare: crossover confermato a src " + result.ToString("F2", CultureInfo.InvariantCulture) + "s (" + consecutiveBad + " frame old<0.5)");
                        return result;
                    }
                }
                else
                {
                    consecutiveBad = 0;
                    crossoverIdx = -1;
                }
            }

            // Se non confermato, usa il punto della binaria
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    Scansione lineare: conferma non raggiunta, uso punto binaria");
            return result;
        }

        #endregion

        #region Metodi privati — Fase 4: Verifica globale

        /// <summary>
        /// Wrapper diagnostico per estrazioni frame DeepAnalysis
        /// </summary>
        private void ExtractDeepSegment(string filePath, int startMs, double durationSec, double targetFps, bool geometryCropToFourThree, out List<byte[]> frames, out double[] timestampsMs)
        {
            this.ExtractDeepSegment(filePath, startMs, durationSec, targetFps, geometryCropToFourThree, this.ResolveManualAnalysisCrop(filePath), out frames, out timestampsMs);
        }

        /// <summary>
        /// Wrapper diagnostico per estrazioni frame DeepAnalysis con crop manuale esplicito
        /// </summary>
        private void ExtractDeepSegment(string filePath, int startMs, double durationSec, double targetFps, bool geometryCropToFourThree, string manualCropPx, out List<byte[]> frames, out double[] timestampsMs)
        {
            Stopwatch stopwatch = new Stopwatch();

            stopwatch.Start();
            base.ExtractSegment(filePath, startMs, durationSec, targetFps, geometryCropToFourThree, manualCropPx, out frames, out timestampsMs);
            stopwatch.Stop();

            lock (this._performanceDiagnosticsLock)
            {
                if (this._performanceDiagnostics != null)
                {
                    this._performanceDiagnostics.SegmentExtractCalls++;
                    this._performanceDiagnostics.SegmentExtractMs += stopwatch.ElapsedMilliseconds;
                }
            }
        }

        /// <summary>
        /// Verifica locale una transizione confrontando vecchio e nuovo offset prima/dopo il breakpoint
        /// </summary>
        private DeepAnalysisLocalVerificationDiagnostic VerifyTransitionLocal(string sourceFile, string langFile, double crossoverSrcSec, double oldOffsetSec, double newOffsetSec, double inverseRatio)
        {
            DeepAnalysisLocalVerificationDiagnostic result = new DeepAnalysisLocalVerificationDiagnostic();
            double offsetDeltaSec = newOffsetSec - oldOffsetSec;
            double transitionDurationSec = Math.Abs(offsetDeltaSec);
            bool insertSilenceTransition = this._currentAnalysisUsesTimelineMap && offsetDeltaSec > 0.0;
            bool timelineCutTransition = this._currentAnalysisUsesTimelineMap && offsetDeltaSec < 0.0;
            double beforeSrcSec = crossoverSrcSec - 1.5;
            double afterSrcSec = crossoverSrcSec + 1.5;
            double forwardSrcSec = crossoverSrcSec + Math.Max(4.0, transitionDurationSec + 2.0);
            double oldTotal;
            double newTotal;
            bool beforeStable;
            bool afterOrForwardImproved;
            bool overallImproved;
            bool visualVerified;
            bool afterIndeterminate;
            bool postVisualStrong;
            bool postVisualConsistent;
            bool beforeSsimStable;
            bool beforeSsimStrictStable;
            bool beforeTimelineCutIndeterminate;
            bool beforeMseStable;
            bool afterSsimImproved;
            bool forwardSsimImproved;
            bool afterSsimNotWorse;
            bool forwardSsimNotWorse;
            bool postSsimStrong;
            bool postSsimConsistent;
            bool postSsimRejects;
            bool postSsimAvailable;
            bool ssimPostCanDefer;
            bool ssimForwardCanDefer;
            bool msePostCanDefer;
            bool mseForwardCanDefer;
            bool mseIndeterminateCanDefer;
            bool mseVisualPostCanDefer;
            bool mseVisualVerified;
            bool mseAfterNotWorse;
            bool mseForwardImproved;
            bool beforeSsimNewBetter;
            bool timelineCutBeforeSsimStable;
            bool timelineCutLocalBeforeNewBetter;
            bool timelineCutLateGuardNewBetter;
            bool timelineCutBoundaryCanDefer;
            bool timelineCutAfterNotBlocking;
            double timelineCutLateGuardSrcSec;
            double timelineCutLateGuardOldMse;
            double timelineCutLateGuardOldSsim;
            double timelineCutLateGuardNewMse;
            double timelineCutLateGuardNewSsim;
            double afterForwardOldTotal;
            double afterForwardNewTotal;
            double afterForwardImprovementRatio;
            double beforeOldMse;
            double beforeOldSsim;
            double beforeNewMse;
            double beforeNewSsim;
            double afterOldMse;
            double afterOldSsim;
            double afterNewMse;
            double afterNewSsim;
            double forwardOldMse;
            double forwardOldSsim;
            double forwardNewMse;
            double forwardNewSsim;
            if (this._currentAnalysisUsesTimelineMap && offsetDeltaSec < 0.0)
            {
                // Nei CUT timeline il frame immediatamente prima puo' cadere in una zona ambigua: il veto old-before va misurato piu' indietro
                beforeSrcSec = crossoverSrcSec - Math.Max(4.0, transitionDurationSec + 2.0);
                forwardSrcSec = crossoverSrcSec + Math.Max(LOCAL_TIMELINE_CUT_FORWARD_SEC, transitionDurationSec + 2.0);
            }

            if (beforeSrcSec < 0.0) { beforeSrcSec = 0.0; }
            if (insertSilenceTransition)
            {
                // In INSERT_SILENCE il crossover e' il punto operativo: il nuovo offset diventa verificabile solo dopo la durata inserita
                afterSrcSec = crossoverSrcSec + transitionDurationSec + 2.0;
                forwardSrcSec = crossoverSrcSec + transitionDurationSec + Math.Max(8.0, transitionDurationSec + 2.0);
            }

            result.BeforeSrcSec = beforeSrcSec;
            result.AfterSrcSec = afterSrcSec;
            result.ForwardSrcSec = forwardSrcSec;

            // Calcola SSIM e MSE sugli stessi campioni per confrontare vecchio e nuovo offset senza doppie estrazioni
            this._visualFrameAnalyzer.TryComputeLocalVisualScoreAt(sourceFile, langFile, beforeSrcSec, oldOffsetSec, inverseRatio, this._daConfig.CoarseFps, this._geometryCropSourceToFourThree, this._geometryCropLanguageToFourThree, out beforeOldMse, out beforeOldSsim, result.VisualSamples, "before", "old");
            this._visualFrameAnalyzer.TryComputeLocalVisualScoreAt(sourceFile, langFile, beforeSrcSec, newOffsetSec, inverseRatio, this._daConfig.CoarseFps, this._geometryCropSourceToFourThree, this._geometryCropLanguageToFourThree, out beforeNewMse, out beforeNewSsim, result.VisualSamples, "before", "new");
            this._visualFrameAnalyzer.TryComputeLocalVisualScoreAt(sourceFile, langFile, afterSrcSec, oldOffsetSec, inverseRatio, this._daConfig.CoarseFps, this._geometryCropSourceToFourThree, this._geometryCropLanguageToFourThree, out afterOldMse, out afterOldSsim, result.VisualSamples, "after", "old");
            this._visualFrameAnalyzer.TryComputeLocalVisualScoreAt(sourceFile, langFile, afterSrcSec, newOffsetSec, inverseRatio, this._daConfig.CoarseFps, this._geometryCropSourceToFourThree, this._geometryCropLanguageToFourThree, out afterNewMse, out afterNewSsim, result.VisualSamples, "after", "new");
            this._visualFrameAnalyzer.TryComputeLocalVisualScoreAt(sourceFile, langFile, forwardSrcSec, oldOffsetSec, inverseRatio, this._daConfig.CoarseFps, this._geometryCropSourceToFourThree, this._geometryCropLanguageToFourThree, out forwardOldMse, out forwardOldSsim, result.VisualSamples, "forward", "old");
            this._visualFrameAnalyzer.TryComputeLocalVisualScoreAt(sourceFile, langFile, forwardSrcSec, newOffsetSec, inverseRatio, this._daConfig.CoarseFps, this._geometryCropSourceToFourThree, this._geometryCropLanguageToFourThree, out forwardNewMse, out forwardNewSsim, result.VisualSamples, "forward", "new");
            result.BeforeOldMse = beforeOldMse;
            result.BeforeNewMse = beforeNewMse;
            result.AfterOldMse = afterOldMse;
            result.AfterNewMse = afterNewMse;
            result.ForwardOldMse = forwardOldMse;
            result.ForwardNewMse = forwardNewMse;
            result.BeforeOldSsim = beforeOldSsim;
            result.BeforeNewSsim = beforeNewSsim;
            result.AfterOldSsim = afterOldSsim;
            result.AfterNewSsim = afterNewSsim;
            result.ForwardOldSsim = forwardOldSsim;
            result.ForwardNewSsim = forwardNewSsim;

            if (insertSilenceTransition)
            {
                oldTotal = this.SumValidMse(result.AfterOldMse, result.ForwardOldMse, double.MaxValue);
                newTotal = this.SumValidMse(result.AfterNewMse, result.ForwardNewMse, double.MaxValue);
            }
            else
            {
                oldTotal = this.SumValidMse(result.BeforeOldMse, result.AfterOldMse, result.ForwardOldMse);
                newTotal = this.SumValidMse(result.BeforeNewMse, result.AfterNewMse, result.ForwardNewMse);
            }

            result.ImprovementRatio = this.ComputeSafeImprovementRatio(oldTotal, newTotal);
            result.ForwardImprovementRatio = this.ComputeSafeImprovementRatio(result.ForwardOldMse, result.ForwardNewMse);
            beforeSsimStable = result.BeforeOldSsim + LOCAL_SSIM_TIE_MARGIN >= result.BeforeNewSsim;
            beforeSsimStrictStable = result.BeforeOldSsim + 0.001 >= result.BeforeNewSsim;
            timelineCutBeforeSsimStable = timelineCutTransition && beforeSsimStrictStable && result.BeforeOldSsim >= 0.90 && result.BeforeOldSsim > result.BeforeNewSsim + LOCAL_SSIM_CLEAR_MARGIN;
            beforeMseStable = result.BeforeOldMse <= result.BeforeNewMse * 1.02;
            beforeSsimNewBetter = result.BeforeNewSsim > result.BeforeOldSsim + LOCAL_SSIM_CLEAR_MARGIN;
            afterSsimImproved = result.AfterNewSsim > result.AfterOldSsim + LOCAL_SSIM_CLEAR_MARGIN;
            forwardSsimImproved = result.ForwardNewSsim > result.ForwardOldSsim + LOCAL_SSIM_CLEAR_MARGIN;
            afterSsimNotWorse = result.AfterNewSsim + LOCAL_SSIM_TIE_MARGIN >= result.AfterOldSsim;
            forwardSsimNotWorse = result.ForwardNewSsim + LOCAL_SSIM_TIE_MARGIN >= result.ForwardOldSsim;
            postSsimStrong = (afterSsimImproved && forwardSsimNotWorse) || (forwardSsimImproved && afterSsimNotWorse);
            postSsimConsistent = afterSsimNotWorse && forwardSsimNotWorse && (afterSsimImproved || forwardSsimImproved);
            postSsimAvailable = result.AfterOldSsim > 0.0 && result.AfterNewSsim > 0.0 && result.ForwardOldSsim > 0.0 && result.ForwardNewSsim > 0.0;
            postSsimRejects = postSsimAvailable && !afterSsimNotWorse && !forwardSsimNotWorse;
            this.VerifyAudioTransitionLocal(sourceFile, langFile, crossoverSrcSec, oldOffsetSec, newOffsetSec, inverseRatio, result);
            if (timelineCutTransition && !result.AudioVerified)
            {
                timelineCutLocalBeforeNewBetter = beforeSsimNewBetter || (!timelineCutBeforeSsimStable && result.BeforeNewMse < result.BeforeOldMse * 0.75);
                timelineCutLateGuardSrcSec = crossoverSrcSec - Math.Max(LOCAL_TIMELINE_CUT_FORWARD_SEC, transitionDurationSec + 2.0);
                if (timelineCutLateGuardSrcSec < 0.0) { timelineCutLateGuardSrcSec = 0.0; }
                this._visualFrameAnalyzer.TryComputeLocalVisualScoreAt(sourceFile, langFile, timelineCutLateGuardSrcSec, oldOffsetSec, inverseRatio, this._daConfig.CoarseFps, this._geometryCropSourceToFourThree, this._geometryCropLanguageToFourThree, out timelineCutLateGuardOldMse, out timelineCutLateGuardOldSsim, result.VisualSamples, "lateGuard", "old");
                this._visualFrameAnalyzer.TryComputeLocalVisualScoreAt(sourceFile, langFile, timelineCutLateGuardSrcSec, newOffsetSec, inverseRatio, this._daConfig.CoarseFps, this._geometryCropSourceToFourThree, this._geometryCropLanguageToFourThree, out timelineCutLateGuardNewMse, out timelineCutLateGuardNewSsim, result.VisualSamples, "lateGuard", "new");
                timelineCutLateGuardNewBetter = timelineCutLateGuardNewSsim > timelineCutLateGuardOldSsim + LOCAL_SSIM_CLEAR_MARGIN || timelineCutLateGuardNewMse < timelineCutLateGuardOldMse * 0.75;
                if (timelineCutLocalBeforeNewBetter || timelineCutLateGuardNewBetter)
                {
                    result.Verified = false;
                    result.CanDeferToGlobalVerification = false;
                    return result;
                }
            }

            // SSIM decide quando il segnale e' chiaro; MSE resta fallback quando SSIM e' saturo o quasi pari
            if (this._currentAnalysisUsesTimelineMap)
            {
                if (insertSilenceTransition)
                {
                    beforeStable = beforeSsimStable || result.BeforeOldMse <= result.BeforeNewMse || result.ImprovementRatio >= 2.0;
                    afterOrForwardImproved = result.AfterNewMse < result.AfterOldMse * 0.995 || result.ForwardImprovementRatio >= LOCAL_FORWARD_STRONG_RATIO;
                    overallImproved = result.ImprovementRatio >= 1.005 || result.ForwardImprovementRatio >= LOCAL_FORWARD_STRONG_RATIO;
                    afterIndeterminate = result.AfterNewMse <= result.AfterOldMse * 1.02 && result.ForwardNewMse <= result.ForwardOldMse * 1.02;
                    postVisualStrong = result.AfterNewMse <= result.AfterOldMse * 1.02 && result.ForwardImprovementRatio >= LOCAL_FORWARD_STRONG_RATIO;
                    postVisualConsistent = result.AfterNewMse < result.AfterOldMse && result.ForwardNewMse <= result.ForwardOldMse * 1.02 && result.ImprovementRatio >= 1.10;
                    ssimPostCanDefer = transitionDurationSec >= 0.5 && (postSsimStrong || postSsimConsistent);
                    mseForwardCanDefer = beforeStable && result.ForwardImprovementRatio >= LOCAL_FORWARD_STRONG_RATIO && result.ImprovementRatio >= 1.005;
                    mseIndeterminateCanDefer = transitionDurationSec >= 0.5 && beforeStable && afterIndeterminate && result.ImprovementRatio >= 0.99;
                    mseVisualPostCanDefer = transitionDurationSec >= 0.5 && (postVisualStrong || postVisualConsistent);
                    msePostCanDefer = mseForwardCanDefer || mseIndeterminateCanDefer || mseVisualPostCanDefer;
                    mseAfterNotWorse = result.ForwardNewMse <= result.ForwardOldMse * 1.02;
                    mseVisualVerified = afterOrForwardImproved && overallImproved && mseAfterNotWorse;
                    visualVerified = beforeStable && (postSsimStrong || mseVisualVerified);
                    result.CanDeferToGlobalVerification = !result.AudioRejected && (result.AudioVerified || visualVerified || (!postSsimRejects && (ssimPostCanDefer || msePostCanDefer)));
                    result.Verified = !result.AudioRejected && (visualVerified || result.AudioVerified);
                }
                else
                {
                    beforeTimelineCutIndeterminate = timelineCutTransition && Math.Abs(result.BeforeNewSsim - result.BeforeOldSsim) <= LOCAL_SSIM_TIE_MARGIN && result.BeforeNewMse <= result.BeforeOldMse * 1.02;
                    beforeStable = transitionDurationSec > 1.5 || timelineCutBeforeSsimStable || beforeTimelineCutIndeterminate || (beforeSsimStrictStable && beforeMseStable) || result.BeforeOldMse <= result.BeforeNewMse * 0.90;
                    afterForwardOldTotal = this.SumValidMse(result.AfterOldMse, result.ForwardOldMse, double.MaxValue);
                    afterForwardNewTotal = this.SumValidMse(result.AfterNewMse, result.ForwardNewMse, double.MaxValue);
                    afterForwardImprovementRatio = this.ComputeSafeImprovementRatio(afterForwardOldTotal, afterForwardNewTotal);
                    postVisualConsistent = result.AfterNewMse <= result.AfterOldMse * 1.02 && result.ForwardNewMse <= result.ForwardOldMse * 1.02 && result.ImprovementRatio >= 1.10;
                    timelineCutAfterNotBlocking = result.AfterNewSsim <= 0.0 || afterSsimNotWorse || result.AfterNewMse <= result.AfterOldMse * 1.10;
                    timelineCutBoundaryCanDefer = timelineCutTransition && transitionDurationSec >= 0.5 && beforeStable && timelineCutAfterNotBlocking && !postSsimRejects;
                    ssimPostCanDefer = transitionDurationSec >= 0.5 && beforeStable && postSsimConsistent;
                    ssimForwardCanDefer = transitionDurationSec >= 0.5 && timelineCutTransition && beforeStable && timelineCutAfterNotBlocking && forwardSsimImproved;
                    mseForwardCanDefer = beforeStable && result.ForwardImprovementRatio >= LOCAL_FORWARD_STRONG_RATIO && afterForwardImprovementRatio >= 1.10;
                    mseVisualPostCanDefer = transitionDurationSec >= 0.5 && beforeStable && postVisualConsistent;
                    msePostCanDefer = mseForwardCanDefer || mseVisualPostCanDefer;
                    mseAfterNotWorse = result.AfterNewMse <= result.AfterOldMse * 1.02;
                    mseForwardImproved = result.ForwardNewMse < result.ForwardOldMse && afterForwardImprovementRatio >= 1.10;
                    mseVisualVerified = mseAfterNotWorse && mseForwardImproved;
                    visualVerified = beforeStable && (postSsimStrong || mseVisualVerified);
                    result.CanDeferToGlobalVerification = !result.AudioRejected && (result.AudioVerified || visualVerified || (!postSsimRejects && (ssimPostCanDefer || ssimForwardCanDefer || msePostCanDefer || timelineCutBoundaryCanDefer)));
                    result.Verified = !result.AudioRejected && (visualVerified || result.AudioVerified);
                }
            }
            else
            {
                result.Verified = beforeSsimStable && postSsimStrong;
            }

            return result;
        }

        /// <summary>
        /// Verifica la transizione usando la traccia audio comune quando disponibile
        /// </summary>
        private void VerifyAudioTransitionLocal(string sourceFile, string langFile, double crossoverSrcSec, double oldOffsetSec, double newOffsetSec, double inverseRatio, DeepAnalysisLocalVerificationDiagnostic result)
        {
            double transitionDurationSec = Math.Abs(newOffsetSec - oldOffsetSec);
            double audioForwardSrcSec = crossoverSrcSec + transitionDurationSec + 12.0;
            bool beforeAvailable;
            bool afterAvailable;
            bool forwardAvailable;
            bool beforeStable;
            bool afterImproved;
            bool forwardImproved;
            bool afterRejected;
            bool forwardRejected;

            if (!this._currentAnalysisUsesTimelineMap || this._currentTrackPolicy == null || !this._currentTrackPolicy.AudioValidationAvailable)
            {
                return;
            }

            if (audioForwardSrcSec < result.ForwardSrcSec)
            {
                audioForwardSrcSec = result.ForwardSrcSec;
            }

            beforeAvailable = this.TryScoreAudioOffsetPair(sourceFile, langFile, result.BeforeSrcSec, oldOffsetSec, newOffsetSec, inverseRatio, out double beforeOldScore, out double beforeNewScore);
            afterAvailable = this.TryScoreAudioOffsetPair(sourceFile, langFile, result.AfterSrcSec, oldOffsetSec, newOffsetSec, inverseRatio, out double afterOldScore, out double afterNewScore);
            forwardAvailable = this.TryScoreAudioOffsetPair(sourceFile, langFile, audioForwardSrcSec, oldOffsetSec, newOffsetSec, inverseRatio, out double forwardOldScore, out double forwardNewScore);

            result.AudioBeforeOldScore = beforeOldScore;
            result.AudioBeforeNewScore = beforeNewScore;
            result.AudioAfterOldScore = afterOldScore;
            result.AudioAfterNewScore = afterNewScore;
            result.AudioForwardOldScore = forwardOldScore;
            result.AudioForwardNewScore = forwardNewScore;

            if (!afterAvailable && !forwardAvailable)
            {
                return;
            }

            beforeStable = !beforeAvailable || beforeOldScore + LOCAL_AUDIO_VERIFY_BEFORE_TOLERANCE >= beforeNewScore;
            afterImproved = afterAvailable && afterNewScore >= LOCAL_AUDIO_VERIFY_MIN_SCORE && afterNewScore > afterOldScore + LOCAL_AUDIO_VERIFY_MIN_MARGIN;
            forwardImproved = forwardAvailable && forwardNewScore >= LOCAL_AUDIO_VERIFY_MIN_SCORE && forwardNewScore > forwardOldScore + LOCAL_AUDIO_VERIFY_MIN_MARGIN;
            afterRejected = afterAvailable && afterOldScore >= LOCAL_AUDIO_REJECT_MIN_SCORE && afterOldScore > afterNewScore + LOCAL_AUDIO_REJECT_MIN_MARGIN;
            forwardRejected = forwardAvailable && forwardOldScore >= LOCAL_AUDIO_REJECT_MIN_SCORE && forwardOldScore > forwardNewScore + LOCAL_AUDIO_REJECT_MIN_MARGIN;
            result.AudioVerified = beforeStable && (afterImproved || forwardImproved);
            result.AudioRejected = beforeStable && afterRejected && forwardRejected;
        }

        /// <summary>
        /// Confronta un punto audio source contro vecchio e nuovo offset
        /// </summary>
        private bool TryScoreAudioOffsetPair(string sourceFile, string langFile, double srcSec, double oldOffsetSec, double newOffsetSec, double inverseRatio, out double oldScore, out double newScore)
        {
            double sourceStartSec = srcSec - (LOCAL_AUDIO_VERIFY_WINDOW_SEC / 2.0);
            double oldLanguageStartSec;
            double newLanguageStartSec;
            double[] sourceEnvelope;
            double[] oldEnvelope;
            double[] newEnvelope;
            int compareCount;
            oldScore = 0.0;
            newScore = 0.0;

            if (sourceStartSec < 0.0)
            {
                sourceStartSec = 0.0;
            }

            oldLanguageStartSec = sourceStartSec - oldOffsetSec;
            newLanguageStartSec = sourceStartSec - newOffsetSec;
            if (Math.Abs(inverseRatio - 1.0) > 0.0001)
            {
                oldLanguageStartSec = oldLanguageStartSec * inverseRatio;
                newLanguageStartSec = newLanguageStartSec * inverseRatio;
            }

            if (oldLanguageStartSec < 0.0 || newLanguageStartSec < 0.0)
            {
                return false;
            }

            sourceEnvelope = this.ExtractAudioEnvelope(sourceFile, sourceStartSec, LOCAL_AUDIO_VERIFY_WINDOW_SEC, LOCAL_AUDIO_VERIFY_WINDOW_MS);
            oldEnvelope = this.ExtractAudioEnvelope(langFile, oldLanguageStartSec, LOCAL_AUDIO_VERIFY_WINDOW_SEC, LOCAL_AUDIO_VERIFY_WINDOW_MS);
            newEnvelope = this.ExtractAudioEnvelope(langFile, newLanguageStartSec, LOCAL_AUDIO_VERIFY_WINDOW_SEC, LOCAL_AUDIO_VERIFY_WINDOW_MS);

            if (sourceEnvelope == null || oldEnvelope == null || newEnvelope == null)
            {
                return false;
            }

            compareCount = Math.Min(sourceEnvelope.Length, Math.Min(oldEnvelope.Length, newEnvelope.Length));
            if (compareCount < LOCAL_AUDIO_VERIFY_MIN_WINDOWS)
            {
                return false;
            }

            oldScore = this._audioEnvelopeService.ScoreWindow(sourceEnvelope, oldEnvelope, 0, 0, compareCount);
            newScore = this._audioEnvelopeService.ScoreWindow(sourceEnvelope, newEnvelope, 0, 0, compareCount);
            return true;
        }

        /// <summary>
        /// Calcola un ratio MSE evitando divisioni instabili su match perfetti
        /// </summary>
        private double ComputeSafeImprovementRatio(double oldValue, double newValue)
        {
            double denominator;
            if (oldValue <= 0.0 || oldValue >= double.MaxValue || newValue >= double.MaxValue)
            {
                return 0.0;
            }

            denominator = newValue;
            if (denominator < LOCAL_MSE_RATIO_EPSILON)
            {
                denominator = LOCAL_MSE_RATIO_EPSILON;
            }

            return oldValue / denominator;
        }

        /// <summary>
        /// Somma MSE validi ignorando valori non calcolabili
        /// </summary>
        private double SumValidMse(double a, double b, double c)
        {
            double result = 0.0;
            if (a < double.MaxValue) { result += a; }
            if (b < double.MaxValue) { result += b; }
            if (c < double.MaxValue) { result += c; }
            return result;
        }

        #endregion
    }
}
