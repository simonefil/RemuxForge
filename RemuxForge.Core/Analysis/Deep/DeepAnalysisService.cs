using RemuxForge.Core.Analysis.Edit;
using RemuxForge.Core.Analysis.Edit.Extraction;
using RemuxForge.Core.Analysis.Edit.Geometry;
using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Media.Ffmpeg;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Orchestratore della pipeline DeepAnalysis: una passata di estrazione per file, poi pianori, confini e giudizio
    /// </summary>
    public sealed class DeepAnalysisService : VideoSyncServiceBase
    {
        #region Variabili di classe

        /// <summary>
        /// Risolve i percorsi degli strumenti esterni usati dalla pipeline
        /// </summary>
        private readonly ToolPathResolverService _toolPathResolver;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza il servizio con i percorsi necessari alla pipeline DeepAnalysis
        /// </summary>
        /// <param name="ffmpegPath">Percorso FFmpeg risolto</param>
        /// <param name="toolPathResolver">Resolver dei percorsi degli strumenti esterni</param>
        public DeepAnalysisService(string ffmpegPath, ToolPathResolverService toolPathResolver) : base(ffmpegPath, LogSection.Deep)
        {
            if (string.IsNullOrEmpty(ffmpegPath))
                throw new ArgumentException(AppText.T("analysis.sift.missingFfmpegPath"), nameof(ffmpegPath));
            if (toolPathResolver == null)
                throw new ArgumentNullException(nameof(toolPathResolver));
            this._ffmpegPath = ffmpegPath;
            this._toolPathResolver = toolPathResolver;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Esegue la pipeline completa: estrazione, geometria, rilevazione dei pianori, confini, durate e giudizio
        /// </summary>
        /// <param name="sourceFile">Percorso del file video source</param>
        /// <param name="languageFile">Percorso del file video language</param>
        /// <param name="manualStretchFactor">Fattore di stretch manuale oppure stringa vuota per la risoluzione automatica</param>
        /// <param name="sourceCropPx">Crop manuale in pixel per il file source</param>
        /// <param name="languageCropPx">Crop manuale in pixel per il file language</param>
        /// <param name="cancellationToken">Token di annullamento cooperativo</param>
        /// <returns>Mappa di montaggio completa se l'analisi viene accettata, altrimenti null</returns>
        public EditMap Analyze(string sourceFile, string languageFile, string manualStretchFactor, string sourceCropPx, string languageCropPx, CancellationToken cancellationToken = default)
        {
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            DeepAnalysisResult result = new DeepAnalysisResult();
            DeepAnalysisRunDiagnosticsSession diagnostics = null;
            HashBackendBase hashBackend = null;
            this.LastResult = result;

            try
            {
                AdvancedConfig advanced = AppSettingsService.Instance.Settings.Advanced;
                string configuredBackend = AdvancedConfig.GetVisionBackendValue(advanced.GetVisionBackendKind());
                diagnostics = new DeepAnalysisRunDiagnosticsSession(sourceFile, languageFile, sourceCropPx, languageCropPx, manualStretchFactor, configuredBackend);
                result.RunDirectory = diagnostics.DirectoryPath;
                result.BackendName = configuredBackend;
                cancellationToken.ThrowIfCancellationRequested();

                // Il backend si apre prima di decodificare: se non c'è, si dice subito e non
                // dopo due minuti e mezzo di decodifica buttati
                hashBackend = HashBackendBase.Create(advanced.GetVisionBackendKind());
                if (!hashBackend.IsAvailable(out string hashBackendRejectReason))
                    return this.Reject(result, AppText.F("deep.temporal.hashBackend.unavailable", configuredBackend, hashBackendRejectReason));

                // Risolve il fattore manuale prima di allocare le risorse della pipeline
                if (!this.TryResolveStretch(manualStretchFactor, out double languageToSourceStretch, out string stretchFactor, out string rejectReason))
                    return this.Reject(result, rejectReason);
                result.SourceToLanguageScale = 1.0 / languageToSourceStretch;
                result.StretchFactor = stretchFactor;
                cancellationToken.ThrowIfCancellationRequested();

                sourceCropPx = Options.NormalizeAnalysisCropPx(sourceCropPx);
                languageCropPx = Options.NormalizeAnalysisCropPx(languageCropPx);
                cancellationToken.ThrowIfCancellationRequested();

                FfmpegConfig ffmpegConfig = new FfmpegConfig();
                ffmpegConfig.HardwareAcceleration = advanced.Ffmpeg.HardwareAcceleration;
                ffmpegConfig.HardwareAccelerationMethod = advanced.Ffmpeg.HardwareAccelerationMethod;
                ffmpegConfig.FrameExtractionTimeoutMs = Math.Max(advanced.Ffmpeg.FrameExtractionTimeoutMs, advanced.DeepAnalysis.SceneExtractTimeoutMs);
                string ffprobePath = this._toolPathResolver.ResolveFfprobePath(this._ffmpegPath, false);
                string mkvMergePath = this._toolPathResolver.ResolveMkvMergePath(false);
                string mkvExtractPath = this._toolPathResolver.ResolveMkvExtractPath(mkvMergePath, false);

                // La geometria comune si stima dai dati: senza, la misura non degrada, si inverte
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, AppText.T("deep.temporal.log.geometryViewport"));
                ConsoleHelper.Progress(LogSection.Deep, 8, AppText.T("deep.temporal.progress.geometry"));
                diagnostics.Append("phase=geometry");
                FfmpegVideoInfoReader videoInfoReader = new FfmpegVideoInfoReader(this._ffmpegPath, ffmpegConfig, LogSection.Deep);
                if (!videoInfoReader.TryRead(sourceFile, out int sourceDurationMs, out _) ||
                    !videoInfoReader.TryRead(languageFile, out int languageDurationMs, out _))
                    return this.Reject(result, AppText.T("deep.temporal.service.rejected"));
                DHashViewportEstimator viewportEstimator = new DHashViewportEstimator(this._ffmpegPath, ffmpegConfig);
                DHashViewportEstimationResult viewport = viewportEstimator.Estimate(sourceFile, languageFile, sourceCropPx, languageCropPx, sourceDurationMs, languageDurationMs, cancellationToken);
                if (!viewport.Success)
                    return this.Reject(result, viewport.RejectReason);
                FrameGeometry sourceFrameGeometry = viewport.SourceGeometry;
                FrameGeometry languageFrameGeometry = viewport.LanguageGeometry;

                ConsoleHelper.Progress(LogSection.Deep, 14, AppText.T("deep.temporal.progress.geometry"));
                FrameGeometryEstimator geometryEstimator = new FrameGeometryEstimator(this._ffmpegPath, ffmpegConfig, advanced.GetVisionBackendKind(), LogSection.Deep);
                FrameGeometryEstimationResult geometry = geometryEstimator.Estimate(sourceFile, languageFile, sourceCropPx, languageCropPx, sourceDurationMs, sourceFrameGeometry, languageFrameGeometry, cancellationToken);
                result.SourceGeometry = geometry.SourceGeometryInfo;
                result.LanguageGeometry = geometry.LanguageGeometryInfo;
                result.GeometryAlignment = geometry.Alignment;
                if (!geometry.Alignment.Success)
                    return this.Reject(result, geometry.Alignment.RejectReason);
                if (geometry.Alignment.UseAffineDHashViewport)
                    languageFrameGeometry = geometry.AffineLanguageDHashGeometry;

                result.GeometryMatchRate = viewport.MatchRate;
                result.GeometryMedianDistance = viewport.MedianDistance;
                result.Source.CropPx = sourceFrameGeometry.CropPx;
                result.Source.Zoom = sourceFrameGeometry.Zoom;
                result.Source.VerticalShift = sourceFrameGeometry.VerticalShift;
                result.Language.CropPx = languageFrameGeometry.CropPx;
                result.Language.Zoom = languageFrameGeometry.Zoom;
                result.Language.VerticalShift = languageFrameGeometry.VerticalShift;
                if (languageFrameGeometry.UseNormalizedActiveViewport)
                {
                    result.Language.Mode = "affine_source_viewport";
                    result.Language.ViewportLeft = languageFrameGeometry.ViewportLeft;
                    result.Language.ViewportTop = languageFrameGeometry.ViewportTop;
                    result.Language.ViewportRight = languageFrameGeometry.ViewportRight;
                    result.Language.ViewportBottom = languageFrameGeometry.ViewportBottom;
                }
                diagnostics.UpdateConfiguration(sourceCropPx, languageCropPx, stretchFactor, result.SourceToLanguageScale, result.SourceGeometry, result.LanguageGeometry, result.GeometryAlignment, ffmpegConfig, advanced.VideoSync, result.Source, result.Language, result.GeometryMatchRate, result.GeometryMedianDistance, configuredBackend);
                cancellationToken.ThrowIfCancellationRequested();

                // Una sola decodifica lineare per file: da qui escono dHash, luminanza e miniature
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, AppText.T("deep.temporal.log.frameSignals"));
                ConsoleHelper.Progress(LogSection.Deep, 24, AppText.T("deep.temporal.progress.frameSignals"));
                diagnostics.Append("phase=frame-signals");
                FrameSignalExtractor extractor = new FrameSignalExtractor(this._ffmpegPath, ffprobePath, mkvMergePath, mkvExtractPath, ffmpegConfig, hashBackend);
                FrameSignals sourceSignals = extractor.Extract(sourceFile, sourceFrameGeometry, ffmpegConfig.FrameExtractionTimeoutMs, cancellationToken);
                ConsoleHelper.Progress(LogSection.Deep, 44, AppText.T("deep.temporal.progress.frameSignals"));
                FrameSignals languageSignals = extractor.Extract(languageFile, languageFrameGeometry, ffmpegConfig.FrameExtractionTimeoutMs, cancellationToken);
                ConsoleHelper.Progress(LogSection.Deep, 64, AppText.T("deep.temporal.progress.frameSignals"));
                result.Source.FrameCount = sourceSignals.Count;
                result.Language.FrameCount = languageSignals.Count;
                diagnostics.Append("frame-signals source=" + sourceSignals.Count.ToString(CultureInfo.InvariantCulture) + " language=" + languageSignals.Count.ToString(CultureInfo.InvariantCulture));
                cancellationToken.ThrowIfCancellationRequested();

                // L'audio decide dentro il nero e giudica l'esistenza delle operazioni
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, AppText.T("deep.temporal.log.audioEnvelopes"));
                ConsoleHelper.Progress(LogSection.Deep, 68, AppText.T("deep.temporal.progress.audioEnvelopes"));
                diagnostics.Append("phase=audio-envelopes");
                AudioEnvelopePair envelopes = this.BuildAudioEnvelopes(sourceFile, languageFile, ffprobePath, ffmpegConfig, languageToSourceStretch, diagnostics);
                cancellationToken.ThrowIfCancellationRequested();

                ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, AppText.T("deep.temporal.log.globalSolve"));
                ConsoleHelper.Progress(LogSection.Deep, 75, AppText.T("deep.temporal.progress.detection"));
                diagnostics.Append("phase=detection-and-judgement");
                PairSignals pair = new PairSignals(sourceSignals, languageSignals, languageToSourceStretch);
                EditAnalysisOutcome outcome = new EditMapComposer(hashBackend).Compose(pair, envelopes, cancellationToken);

                EditMapConverter converter = new EditMapConverter();
                result.InitialOffsetMs = outcome.InitialOffsetMs;
                result.Coverage = outcome.Coverage;
                result.Plateaus = converter.BuildPlateaus(pair, outcome);
                result.Operations = BuildDiagnostics(outcome.Operations);
                result.RejectedOperations = BuildDiagnostics(outcome.Rejected);
                diagnostics.Append("operations accepted=" + outcome.Operations.Count.ToString(CultureInfo.InvariantCulture) +
                    " rejected=" + outcome.Rejected.Count.ToString(CultureInfo.InvariantCulture) +
                    " coverage=" + outcome.Coverage.ToString("0.0000", CultureInfo.InvariantCulture));

                // Una mappa che non spiega il film non è un risultato con poca confidenza:
                // è un risultato sbagliato, e va rifiutata invece che consegnata
                if (outcome.Coverage < EditAnalysisProfile.COVERAGE_MINIMUM)
                    return this.Reject(result, AppText.F("deep.temporal.service.insufficientCoverage", (outcome.Coverage * 100.0).ToString("0.0", CultureInfo.InvariantCulture), (EditAnalysisProfile.COVERAGE_MINIMUM * 100.0).ToString("0.0", CultureInfo.InvariantCulture)));

                EditMap editMap = converter.Convert(pair, outcome, stretchFactor);
                result.Status = DeepAnalysisStatus.Accepted;
                editMap.AnalysisTimeMs = totalStopwatch.ElapsedMilliseconds;
                ConsoleHelper.Progress(LogSection.Deep, 85, AppText.T("deep.temporal.progress.completed"));
                return editMap;
            }
            catch (OperationCanceledException)
            {
                result.Status = DeepAnalysisStatus.Cancelled;
                result.RejectReason = AppText.T("deep.temporal.service.cancelled");
                throw;
            }
            catch (Exception ex)
            {
                return this.Reject(result, ex.Message);
            }
            finally
            {
                hashBackend?.Dispose();
                totalStopwatch.Stop();
                result.TotalElapsedMs = totalStopwatch.ElapsedMilliseconds;
                result.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
                if (diagnostics != null)
                {
                    try
                    {
                        diagnostics.Complete(result);
                    }
                    catch (Exception ex)
                    {
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "Diagnostica Deep Analysis non completata: " + ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// Ultimo risultato diagnostico dell'analisi, valorizzato anche in caso di rifiuto o errore gestito
        /// </summary>
        public DeepAnalysisResult LastResult { get; private set; }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Converte i candidati in diagnostica serializzabile
        /// </summary>
        /// <param name="operations">Candidati accettati o scartati</param>
        /// <returns>Diagnostica delle operazioni</returns>
        private static List<DeepAnalysisOperationDiagnostic> BuildDiagnostics(IReadOnlyList<EditOperationCandidate> operations)
        {
            List<DeepAnalysisOperationDiagnostic> result = new List<DeepAnalysisOperationDiagnostic>();
            foreach (EditOperationCandidate operation in operations)
            {
                result.Add(new DeepAnalysisOperationDiagnostic
                {
                    Type = operation.Kind == EditOperationKind.InsertSilence ? EditOperation.INSERT_SILENCE : EditOperation.CUT_SEGMENT,
                    SourceTimestampMs = operation.TimestampMs,
                    DurationMs = operation.DurationMs,
                    OffsetBeforeMs = operation.OffsetBeforeMs,
                    OffsetAfterMs = operation.OffsetAfterMs,
                    UncertaintyMs = operation.UncertaintyMs,
                    BoundaryDecidedBy = operation.Boundary.ToString(),
                    RejectReason = operation.RejectReason ?? ""
                });
            }
            return result;
        }

        /// <summary>
        /// Estrae gli inviluppi di energia delle due tracce che condividono la lingua, quando ci sono
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="languageFile">File della copia doppiata</param>
        /// <param name="ffprobePath">Percorso di ffprobe</param>
        /// <param name="ffmpegConfig">Configurazione ffmpeg</param>
        /// <param name="stretch">Fattore di stretch della copia doppiata</param>
        /// <param name="diagnostics">Sessione diagnostica della run</param>
        /// <returns>Inviluppi sulla griglia comune oppure null quando l'audio non è utilizzabile</returns>
        private AudioEnvelopePair BuildAudioEnvelopes(string sourceFile, string languageFile, string ffprobePath, FfmpegConfig ffmpegConfig, double stretch, DeepAnalysisRunDiagnosticsSession diagnostics)
        {
            try
            {
                AudioEnvelopeExtractor audioExtractor = new AudioEnvelopeExtractor(this._ffmpegPath, ffprobePath);
                audioExtractor.ResolveSharedStreams(sourceFile, languageFile, ffmpegConfig.FrameExtractionTimeoutMs, out int sourceStream, out int languageStream);
                diagnostics.Append("audio-streams source=" + sourceStream.ToString(CultureInfo.InvariantCulture) + " language=" + languageStream.ToString(CultureInfo.InvariantCulture));
                AudioEnvelope source = audioExtractor.Extract(sourceFile, sourceStream, ffmpegConfig.FrameExtractionTimeoutMs);
                AudioEnvelope language = audioExtractor.Extract(languageFile, languageStream, ffmpegConfig.FrameExtractionTimeoutMs);
                if (source.Count == 0 || language.Count == 0)
                    return null;
                return new AudioEnvelopePair(source, language, stretch);
            }
            catch (Exception ex)
            {
                // Senza audio la catena resta corretta: perde solo il giudice sull'esistenza delle operazioni
                diagnostics.Append("audio-unavailable " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Imposta lo stato di rifiuto e conserva la motivazione nel risultato diagnostico
        /// </summary>
        /// <param name="result">Risultato diagnostico da aggiornare</param>
        /// <param name="reason">Motivazione del rifiuto oppure stringa vuota per il messaggio predefinito</param>
        /// <returns>Valore null per segnalare che non è disponibile una mappa valida</returns>
        private EditMap Reject(DeepAnalysisResult result, string reason)
        {
            result.Status = DeepAnalysisStatus.Rejected;
            result.RejectReason = string.IsNullOrEmpty(reason) ? AppText.T("deep.temporal.service.rejected") : reason;
            return null;
        }

        /// <summary>
        /// Risolve lo stretch che porta i tempi della copia doppiata nel dominio della sorgente
        /// </summary>
        /// <param name="manualStretchFactor">Fattore di stretch manuale oppure stringa vuota</param>
        /// <param name="languageToSourceStretch">Fattore da applicare ai PTS della copia doppiata</param>
        /// <param name="stretchFactor">Fattore normalizzato da propagare alla EditMap</param>
        /// <param name="rejectReason">Motivazione del rifiuto quando il valore manuale non è valido</param>
        /// <returns>True se lo stretch è valido o non è stato richiesto</returns>
        private bool TryResolveStretch(string manualStretchFactor, out double languageToSourceStretch, out string stretchFactor, out string rejectReason)
        {
            languageToSourceStretch = 1.0;
            stretchFactor = "";
            rejectReason = "";
            if (string.IsNullOrEmpty(manualStretchFactor != null ? manualStretchFactor.Trim() : null))
                return true;

            if (!SpeedCorrectionService.TryParseStretchFactor(manualStretchFactor, out double stretchRatio, out string normalizedManualFactor))
            {
                rejectReason = AppText.F("deep.temporal.service.invalidManualStretch", manualStretchFactor);
                return false;
            }
            if (!double.IsFinite(stretchRatio) || stretchRatio <= 0.0)
            {
                rejectReason = AppText.F("deep.temporal.service.invalidManualScale", manualStretchFactor);
                return false;
            }

            languageToSourceStretch = stretchRatio;
            stretchFactor = normalizedManualFactor;
            return true;
        }

        #endregion

    }
}
