using RemuxForge.Core.Analysis.Deep;
using RemuxForge.Core.Analysis.FrameSync;
using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// Coordinator analisi pipeline: metadata, speed correction, deep-analysis e frame-sync
    /// </summary>
    public class PipelineAnalysisCoordinator
    {
        #region Variabili di classe

        private Options _opts;
        private bool _needsMerge;
        private string _ffmpegPath;
        private FrameSyncService _frameSyncService;
        private PipelineTrackMapper _trackMapper;
        private PipelineDiagnosticsWriter _diagnosticsWriter;
        private Func<string, MkvFileInfo> _fileInfoProvider;
        private Action<FileProcessingRecord> _setupLogRedirect;
        private Action _clearLogRedirect;
        private Action<FileProcessingRecord> _fileUpdated;
        private Action<FileProcessingRecord> _buildMergeCommand;
        private ToolPathResolverService _toolPathResolver;
        private VideoTimingResolver _timingResolver;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="opts">Opzioni operative</param>
        /// <param name="needsMerge">True se il file richiede merge/remux</param>
        /// <param name="ffmpegPath">Percorso ffmpeg risolto</param>
        /// <param name="frameSyncService">Servizio FrameSync</param>
        /// <param name="trackMapper">Mapper tracce pipeline</param>
        /// <param name="diagnosticsWriter">Writer diagnostiche</param>
        /// <param name="fileInfoProvider">Provider metadata MKV</param>
        /// <param name="setupLogRedirect">Callback setup log record</param>
        /// <param name="clearLogRedirect">Callback reset log record</param>
        /// <param name="fileUpdated">Callback aggiornamento record</param>
        /// <param name="buildMergeCommand">Callback costruzione comando merge</param>
        /// <param name="toolPathResolver">Resolver strumenti esterni</param>
        public PipelineAnalysisCoordinator(Options opts, bool needsMerge, string ffmpegPath, FrameSyncService frameSyncService, PipelineTrackMapper trackMapper, PipelineDiagnosticsWriter diagnosticsWriter, Func<string, MkvFileInfo> fileInfoProvider, Action<FileProcessingRecord> setupLogRedirect, Action clearLogRedirect, Action<FileProcessingRecord> fileUpdated, Action<FileProcessingRecord> buildMergeCommand, ToolPathResolverService toolPathResolver = null)
        {
            this._opts = opts;
            this._needsMerge = needsMerge;
            this._ffmpegPath = ffmpegPath;
            this._frameSyncService = frameSyncService;
            this._trackMapper = trackMapper;
            this._diagnosticsWriter = diagnosticsWriter;
            this._fileInfoProvider = fileInfoProvider;
            this._setupLogRedirect = setupLogRedirect;
            this._clearLogRedirect = clearLogRedirect;
            this._fileUpdated = fileUpdated;
            this._buildMergeCommand = buildMergeCommand;
            this._toolPathResolver = toolPathResolver ?? new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder);
            this._timingResolver = new VideoTimingResolver(this._toolPathResolver);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Percorso ffmpeg attualmente risolto
        /// </summary>
        public string FfmpegPath
        {
            get { return this._ffmpegPath; }
        }

        /// <summary>
        /// Analizza un record applicando speed correction, DeepAnalysis o FrameSync secondo le opzioni
        /// </summary>
        /// <param name="record">Record da analizzare</param>
        public void AnalyzeFile(FileProcessingRecord record)
        {
            MkvFileInfo sourceInfo = null;
            MkvFileInfo langInfo = null;
            List<TrackInfo> sourceTracks;
            List<TrackInfo> langTracks;
            int syncOffset = 0;
            bool speedCorrectionActive = false;
            double detectedSourceFps = 0.0;
            double detectedLangFps = 0.0;
            bool speedMismatch = false;
            bool vfrSuspect;
            string speedCorrectionMode;
            string speedBlockReason;
            bool deepAllowAutoStretch = false;
            string deepManualStretchFactor;
            string ffmpegPath;
            long sourceDefaultDuration = 0;
            long langDefaultDuration = 0;
            int sourceDurationMs = 0;
            SpeedCorrectionService speedService;
            bool speedOk;
            int frameSyncOffset;
            double manualStretchRatio;
            string normalizedManualStretch;
            bool manualStretchIsIdentity;
            bool done = false;
            DeepAnalysisTimingDiagnostic deepTiming;
            DeepAnalysisPerformanceDiagnostic deepPerformance;
            DeepAnalysisTrackPolicy deepTrackPolicy;
            FrameSyncTimingInfo frameSyncTiming;
            VideoTimingInfo sourceTiming = null;
            VideoTimingInfo langTiming = null;
            // Ignora record non pendenti
            if (record.Status != FileStatus.Pending && record.Status != FileStatus.Error)
            {
                done = true;
            }

            if (!done)
            {
                // Pulisci log precedente per ri-analisi
                record.AnalysisLog.Clear();
                record.ErrorMessage = "";

                // Imposta redirect log
                this._setupLogRedirect(record);

                // Aggiorna stato
                record.Status = FileStatus.Analyzing;
                if (this._fileUpdated != null)
                {
                    this._fileUpdated(record);
                }

                ConsoleHelper.Write(LogSection.General, LogLevel.Header, "Analisi: " + record.SourceFileName);
                ConsoleHelper.Write(LogSection.General, LogLevel.Debug, "  ID Episodio: " + record.EpisodeId);

                // Ottieni info file sorgente
                sourceInfo = this._fileInfoProvider(record.SourceFilePath);
                sourceTracks = (sourceInfo != null) ? sourceInfo.Tracks : null;

                // Popola lingue e tracce sorgente nel record
                record.SourceAudioLangs = this._trackMapper.GetAudioLanguages(sourceTracks);
                record.SourceSubLangs = this._trackMapper.GetSubtitleLanguages(sourceTracks);
                record.SourceAudioTracks = this._trackMapper.FilterTracksByType(sourceTracks, "audio");
                record.SourceSubTracks = this._trackMapper.FilterTracksByType(sourceTracks, "subtitles");

                if (this._needsMerge)
                {
                    // Merge attivo: leggi anche file lingua
                    ConsoleHelper.Write(LogSection.General, LogLevel.Info, "  Match: " + record.LangFileName);

                    langInfo = this._fileInfoProvider(record.LangFilePath);
                    langTracks = (langInfo != null) ? langInfo.Tracks : null;
                    sourceTiming = this._timingResolver.Resolve(record.SourceFilePath, sourceInfo);
                    langTiming = this._timingResolver.Resolve(record.LangFilePath, langInfo);

                    record.LangAudioLangs = this._trackMapper.GetAudioLanguages(langTracks);
                    record.LangSubLangs = this._trackMapper.GetSubtitleLanguages(langTracks);

                    if (langTracks == null)
                    {
                        ConsoleHelper.Write(LogSection.General, LogLevel.Error, "  Impossibile leggere info tracce file lingua");
                        done = this.FailAndFinalizeRecord(record, "Impossibile leggere tracce file lingua");
                    }
                }
                else
                {
                    // Senza merge: analisi ridotta, passa direttamente ad Analyzed
                    done = this.MarkAnalyzedAndFinalize(record, 0, false, "  Analisi completata (no merge)");
                }
            }

            speedCorrectionMode = this._opts.SpeedCorrectionMode != null ? this._opts.SpeedCorrectionMode : Options.SPEED_CORRECTION_OFF;

            // Speed correction manuale con stretch factor esplicito
            if (!done && sourceInfo != null && langInfo != null && !this._opts.DeepAnalysis && speedCorrectionMode == Options.SPEED_CORRECTION_MANUAL)
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Phase, "  Speed correction manuale: stretch=" + this._opts.ManualStretchFactor);
                ConsoleHelper.Progress(LogSection.Speed, 10, "Speed: setup");

                ffmpegPath = this.ResolveFfmpegForSpeed();
                if (!string.IsNullOrEmpty(ffmpegPath))
                {
                    ConsoleHelper.Progress(LogSection.Speed, 14, "Speed: ffmpeg");
                    this._ffmpegPath = ffmpegPath;
                    if (sourceDurationMs == 0 && sourceInfo.ContainerDurationNs > 0)
                    {
                        sourceDurationMs = (int)(sourceInfo.ContainerDurationNs / 1000000);
                    }

                    speedService = new SpeedCorrectionService(ffmpegPath);
                    speedService.SetAnalysisCrop(this._opts.AnalysisCropSourcePx, this._opts.AnalysisCropLanguagePx);
                    ConsoleHelper.Progress(LogSection.Speed, 20, "Speed: stretch");
                    speedOk = speedService.FindDelayAndVerifyManual(record.SourceFilePath, record.LangFilePath, this._opts.ManualStretchFactor, sourceDurationMs);
                    record.SpeedCorrectionTimeMs = speedService.ExecutionTimeMs;

                    if (speedOk)
                    {
                        syncOffset = speedService.SyncDelayMs;
                        record.StretchFactor = speedService.StretchFactor;
                        record.SpeedCorrectionApplied = true;
                        speedCorrectionActive = true;

                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Success, "  Correzione manuale: delay=" + speedService.InitialDelayMs + "ms, sync=" + speedService.SyncDelayMs + "ms, stretch=" + speedService.StretchFactor + " (" + speedService.ExecutionTimeMs + "ms)");
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Verifica: " + speedService.GetDetailSummary());
                        ConsoleHelper.Progress(LogSection.Speed, 72, "Speed: completata");
                    }
                    else
                    {
                        manualStretchIsIdentity = SpeedCorrectionService.TryParseStretchFactor(this._opts.ManualStretchFactor, out manualStretchRatio, out normalizedManualStretch) &&
                            Math.Abs(manualStretchRatio - 1.0) < 0.000001;

                        if (manualStretchIsIdentity && this._opts.FrameSync && this._frameSyncService != null)
                        {
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  Correzione manuale non conclusiva, fallback FrameSync per stretch=1...");
                            frameSyncOffset = this._frameSyncService.RefineOffset(record.SourceFilePath, record.LangFilePath);
                            record.FrameSyncTimeMs = this._frameSyncService.FrameSyncTimeMs;
                            record.FrameSyncResult = this._frameSyncService.LastResult;
                            this._diagnosticsWriter.WriteFrameSyncIfEnabled(record, this._opts);

                            if (frameSyncOffset != int.MinValue)
                            {
                                syncOffset = frameSyncOffset;
                                record.StretchFactor = normalizedManualStretch;
                                record.SpeedCorrectionApplied = true;
                                speedCorrectionActive = true;
                                record.SpeedCorrectionTimeMs += this._frameSyncService.FrameSyncTimeMs;

                                ConsoleHelper.Write(LogSection.Speed, LogLevel.Success, "  Correzione manuale: delay=" + Math.Abs(frameSyncOffset) + "ms, sync=" + frameSyncOffset + "ms, stretch=" + normalizedManualStretch + " (" + record.SpeedCorrectionTimeMs + "ms)");
                                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Verifica: fallback FrameSync, " + this._frameSyncService.GetDetailSummary());
                                ConsoleHelper.Progress(LogSection.Speed, 72, "Speed: completata");
                            }
                            else
                            {
                                ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, "  Correzione velocità manuale fallita");
                                done = this.FailAndFinalizeRecord(record, "Speed correction manuale fallita");
                            }
                        }
                        else
                        {
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, "  Correzione velocità manuale fallita");
                            done = this.FailAndFinalizeRecord(record, "Speed correction manuale fallita");
                        }
                    }
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, "  ffmpeg non disponibile per speed correction manuale");
                    ConsoleHelper.Progress(LogSection.Speed, 72, "Speed: non applicata");
                    done = this.FailAndFinalizeRecord(record, "ffmpeg non disponibile per speed correction manuale");
                }
            }

            // Rilevamento automatico mismatch velocità (solo Auto esplicito)
            if (!done && sourceInfo != null && langInfo != null && !this._opts.DeepAnalysis && speedCorrectionMode == Options.SPEED_CORRECTION_AUTO)
            {
                if (sourceTiming == null)
                {
                    sourceTiming = this._timingResolver.Resolve(record.SourceFilePath, sourceInfo);
                }
                if (langTiming == null)
                {
                    langTiming = this._timingResolver.Resolve(record.LangFilePath, langInfo);
                }

                if (sourceTiming == null || langTiming == null || !sourceTiming.CanAutoSpeedCorrect || !langTiming.CanAutoSpeedCorrect)
                {
                    speedBlockReason = "Auto speed correction saltata: source=" + (sourceTiming != null ? sourceTiming.Reason : "timing non disponibile") + ", lang=" + (langTiming != null ? langTiming.Reason : "timing non disponibile");
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  " + speedBlockReason);
                    ConsoleHelper.Progress(LogSection.Speed, 72, "Speed: non applicata");
                }
                else
                {
                    speedMismatch = SpeedCorrectionService.DetectSpeedMismatch(sourceInfo, langInfo, sourceTiming, langTiming, out detectedSourceFps, out detectedLangFps, out vfrSuspect);

                    if (vfrSuspect)
                    {
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  default_duration assente su una traccia video: Auto speed correction saltata, usare stretch manuale");
                        ConsoleHelper.Progress(LogSection.Speed, 72, "Speed: non applicata");
                        speedMismatch = false;
                    }

                    // Se non c'è mismatch reale, Auto non applica stretch
                }

                if (speedMismatch)
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Phase, "  Mismatch velocità: source " + detectedSourceFps.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "fps, lang " + detectedLangFps.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "fps");
                    ConsoleHelper.Progress(LogSection.Speed, 10, "Speed: setup");

                    // Risolvi ffmpeg se non ancora disponibile
                    ffmpegPath = this.ResolveFfmpegForSpeed();
                    this._ffmpegPath = ffmpegPath;

                    if (speedMismatch && !string.IsNullOrEmpty(ffmpegPath))
                    {
                        // Trova default_duration validato per tracce video
                        sourceDefaultDuration = this._timingResolver.GetTrustedDefaultDurationNs(sourceInfo, record.SourceFilePath);
                        langDefaultDuration = this._timingResolver.GetTrustedDefaultDurationNs(langInfo, record.LangFilePath);

                        // Durata sorgente in ms dal container
                        sourceDurationMs = (int)(sourceInfo.ContainerDurationNs / 1000000);

                        speedService = new SpeedCorrectionService(ffmpegPath);
                        speedService.SetAnalysisCrop(this._opts.AnalysisCropSourcePx, this._opts.AnalysisCropLanguagePx);
                        ConsoleHelper.Progress(LogSection.Speed, 20, "Speed: stretch");
                        speedOk = speedService.FindDelayAndVerify(record.SourceFilePath, record.LangFilePath, sourceDefaultDuration, langDefaultDuration, sourceDurationMs);

                        record.SpeedCorrectionTimeMs = speedService.ExecutionTimeMs;

                        if (speedOk)
                        {
                            syncOffset = speedService.SyncDelayMs;
                            record.StretchFactor = speedService.StretchFactor;
                            record.SpeedCorrectionApplied = true;
                            speedCorrectionActive = true;

                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Success, "  Correzione: delay=" + speedService.InitialDelayMs + "ms, sync=" + speedService.SyncDelayMs + "ms, stretch=" + speedService.StretchFactor + " (" + speedService.ExecutionTimeMs + "ms)");
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Verifica: " + speedService.GetDetailSummary());
                            ConsoleHelper.Progress(LogSection.Speed, 72, "Speed: completata");
                        }
                        else
                        {
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, "  Correzione velocità fallita");
                            done = this.FailAndFinalizeRecord(record, "Speed correction fallita");
                        }
                    }
                }
            }

            // Deep analysis: modalità avanzata per file con edit diversi
            if (!done && !speedCorrectionActive && this._opts.DeepAnalysis)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Avvio deep analysis...");
                ConsoleHelper.Progress(LogSection.Deep, 8, "Deep: avvio");
                deepManualStretchFactor = "";

                if (speedCorrectionMode == Options.SPEED_CORRECTION_MANUAL)
                {
                    deepManualStretchFactor = this._opts.ManualStretchFactor;
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Stretch manuale DeepAnalysis: " + deepManualStretchFactor);
                }
                else if (speedCorrectionMode == Options.SPEED_CORRECTION_AUTO)
                {
                    if (sourceTiming == null)
                    {
                        sourceTiming = this._timingResolver.Resolve(record.SourceFilePath, sourceInfo);
                    }
                    if (langTiming == null)
                    {
                        langTiming = this._timingResolver.Resolve(record.LangFilePath, langInfo);
                    }

                    if (sourceTiming == null || langTiming == null || !sourceTiming.CanAutoSpeedCorrect || !langTiming.CanAutoSpeedCorrect)
                    {
                        speedBlockReason = "source=" + (sourceTiming != null ? sourceTiming.Reason : "timing non disponibile") + ", lang=" + (langTiming != null ? langTiming.Reason : "timing non disponibile");
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Stretch automatico DeepAnalysis saltato: " + speedBlockReason);
                    }
                    else
                    {
                        deepAllowAutoStretch = true;
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Stretch automatico DeepAnalysis consentito: MediaInfo conferma CFR");
                    }
                }

                // Risolvi ffmpeg se non ancora disponibile
                ffmpegPath = this._ffmpegPath;
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    ffmpegPath = this.ResolveFfmpegForSpeed();
                    this._ffmpegPath = ffmpegPath;
                }

                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  ffmpeg non disponibile");
                    ConsoleHelper.Progress(LogSection.Deep, 98, "Deep: errore");
                    done = this.FailAndFinalizeRecord(record, "ffmpeg non disponibile per deep analysis");
                }

                if (!done)
                {
                    // Recupera default_duration se non ancora estratta
                    if (sourceDefaultDuration == 0 && sourceInfo != null)
                    {
                        sourceDefaultDuration = this._timingResolver.GetTrustedDefaultDurationNs(sourceInfo, record.SourceFilePath);
                    }
                    if (langDefaultDuration == 0 && langInfo != null)
                    {
                        langDefaultDuration = this._timingResolver.GetTrustedDefaultDurationNs(langInfo, record.LangFilePath);
                    }
                    if (sourceDurationMs == 0 && sourceTiming != null && sourceTiming.DurationMs > 0.0)
                    {
                        // DeepAnalysis lavora sulla timeline video/common-track; la durata container può essere gonfiata da tracce non importate
                        sourceDurationMs = (int)Math.Round(sourceTiming.DurationMs);
                    }
                    if (sourceDurationMs == 0 && sourceInfo != null && sourceInfo.ContainerDurationNs > 0)
                    {
                        sourceDurationMs = (int)(sourceInfo.ContainerDurationNs / 1000000);
                    }

                    if ((sourceDurationMs > 0 && speedCorrectionMode != Options.SPEED_CORRECTION_AUTO) ||
                        (sourceDurationMs > 0 && sourceDefaultDuration > 0 && langDefaultDuration > 0) ||
                        (sourceDurationMs > 0 && speedCorrectionMode == Options.SPEED_CORRECTION_AUTO && !deepAllowAutoStretch))
                    {
                        deepTrackPolicy = this.BuildDeepAnalysisTrackPolicy(sourceInfo, langInfo);
                        DeepAnalysisService deepService = new DeepAnalysisService(ffmpegPath, this._toolPathResolver);
                        deepService.SetAnalysisCrop(this._opts.AnalysisCropSourcePx, this._opts.AnalysisCropLanguagePx);
                        EditMap editMap = deepService.Analyze(record.SourceFilePath, record.LangFilePath, sourceDefaultDuration, langDefaultDuration, sourceDurationMs, deepManualStretchFactor, deepAllowAutoStretch, deepTrackPolicy);

                        record.DeepAnalysisTimeMs = deepService.AnalysisTimeMs;

                        if (editMap != null)
                        {
                            record.DeepAnalysisMap = editMap;
                            record.DeepAnalysisApplied = true;
                            syncOffset = editMap.InitialDelayMs;

                            if (!string.IsNullOrEmpty(editMap.StretchFactor))
                            {
                                record.StretchFactor = editMap.StretchFactor;
                                record.SpeedCorrectionApplied = true;
                            }

                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Success, "  Completata: " + editMap.Operations.Count + " operazioni, delay iniziale " + editMap.InitialDelayMs + "ms (" + deepService.AnalysisTimeMs + "ms)");
                            if (editMap.Diagnostics != null)
                            {
                                deepTiming = editMap.Diagnostics.Timing;
                                if (deepTiming != null)
                                {
                                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Timing: stretch=" + deepTiming.StretchMs + "ms, timeline=" + deepTiming.TimelineMapMs + "ms, transizioni=" + deepTiming.TransitionRefineMs + "ms, verifica=" + deepTiming.GlobalVerifyMs + "ms, totale=" + deepTiming.TotalMs + "ms");
                                }

                                deepPerformance = editMap.Diagnostics.Performance;
                                if (deepPerformance != null)
                                {
                                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Performance: segmenti=" + deepPerformance.SegmentExtractCalls + " (" + deepPerformance.SegmentExtractMs + "ms), audio-envelope=" + deepPerformance.AudioEnvelopeExtractCalls + " (" + deepPerformance.AudioEnvelopeExtractMs + "ms), transizioni=" + deepPerformance.TransitionRefineCount);
                                }
                            }
                            ConsoleHelper.Progress(LogSection.Deep, 90, "Deep: diagnostica");
                            this._diagnosticsWriter.WriteDeepAnalysisIfEnabled(record, this._opts);
                        }
                        else
                        {
                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  Deep analysis fallita: elaborazione bloccata");
                            ConsoleHelper.Progress(LogSection.Deep, 98, "Deep: errore");
                            if (deepService.LastAnalysisMap != null)
                            {
                                record.DeepAnalysisMap = deepService.LastAnalysisMap;
                            }
                            this._diagnosticsWriter.WriteDeepAnalysisIfEnabled(record, this._opts);
                            done = this.FailAndFinalizeRecord(record, "Deep analysis fallita");
                        }
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  Dati video insufficienti per deep analysis");
                        ConsoleHelper.Progress(LogSection.Deep, 98, "Deep: errore");
                        done = this.FailAndFinalizeRecord(record, "Dati video insufficienti per deep analysis");
                    }
                }
            }

            // Frame-sync solo se non in correzione velocità
            if (!done && !speedCorrectionActive && this._opts.FrameSync && this._frameSyncService != null)
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, "  Sincronizzazione tramite confronto visivo...");
                ConsoleHelper.Progress(LogSection.FrameSync, 10, "FrameSync: setup");

                frameSyncOffset = this._frameSyncService.RefineOffset(record.SourceFilePath, record.LangFilePath);
                record.FrameSyncTimeMs = this._frameSyncService.FrameSyncTimeMs;
                record.FrameSyncResult = this._frameSyncService.LastResult;

                int acceptedFrameSyncPoints = 0;
                if (record.FrameSyncResult != null && record.FrameSyncResult.Points != null)
                {
                    for (int p = 0; p < record.FrameSyncResult.Points.Count; p++)
                    {
                        if (record.FrameSyncResult.Points[p].Accepted)
                        {
                            acceptedFrameSyncPoints++;
                        }
                    }
                }

                bool frameSyncAccepted = frameSyncOffset != int.MinValue &&
                    record.FrameSyncResult != null &&
                    record.FrameSyncResult.Success &&
                    record.FrameSyncResult.Initial != null &&
                    record.FrameSyncResult.Initial.Success &&
                    acceptedFrameSyncPoints >= AppSettingsService.Instance.Settings.Advanced.FrameSync.MinValidPoints &&
                    record.FrameSyncResult.Confidence >= AppSettingsService.Instance.Settings.Advanced.FrameSync.FinalMinConfidence;

                if (frameSyncOffset != int.MinValue && !frameSyncAccepted)
                {
                    if (record.FrameSyncResult != null && string.IsNullOrEmpty(record.FrameSyncResult.FailureReason))
                    {
                        if (record.FrameSyncResult.Initial == null || !record.FrameSyncResult.Initial.Success)
                        {
                            record.FrameSyncResult.FailureReason = "Delay iniziale non verificato";
                        }
                        else if (acceptedFrameSyncPoints < AppSettingsService.Instance.Settings.Advanced.FrameSync.MinValidPoints)
                        {
                            record.FrameSyncResult.FailureReason = "Punti validi insufficienti";
                        }
                        else
                        {
                            record.FrameSyncResult.FailureReason = "Confidence finale insufficiente";
                        }
                    }

                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Risultato non applicabile: punti=" + acceptedFrameSyncPoints + "/" + AppSettingsService.Instance.Settings.Advanced.VideoSync.NumCheckPoints + ", confidence=" + (record.FrameSyncResult != null ? record.FrameSyncResult.Confidence.ToString("P0", System.Globalization.CultureInfo.InvariantCulture) : "0%") + ", richiesta=" + AppSettingsService.Instance.Settings.Advanced.FrameSync.FinalMinConfidence.ToString("P0", System.Globalization.CultureInfo.InvariantCulture));
                    ConsoleHelper.Progress(LogSection.FrameSync, 76, "FrameSync: non conclusivo");
                }

                this._diagnosticsWriter.WriteFrameSyncIfEnabled(record, this._opts);

                if (frameSyncAccepted)
                {
                    syncOffset = frameSyncOffset;
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Success, "  Offset: " + Utils.FormatDelay(frameSyncOffset) + " (tempo: " + this._frameSyncService.FrameSyncTimeMs + "ms)");
                    if (record.FrameSyncResult != null)
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Confidence: " + record.FrameSyncResult.Confidence.ToString("P0", System.Globalization.CultureInfo.InvariantCulture));
                        frameSyncTiming = record.FrameSyncResult.Timing;
                        if (frameSyncTiming != null)
                        {
                            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Timing: info=" + frameSyncTiming.VideoInfoMs + "ms, geometria=" + frameSyncTiming.GeometryMs + "ms, iniziale=" + frameSyncTiming.InitialSearchMs + "ms, audio=" + frameSyncTiming.AudioGlobalMs + "ms, checkpoint=" + frameSyncTiming.CheckpointsMs + "ms, totale=" + frameSyncTiming.TotalMs + "ms");
                            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Estrazioni: calls=" + frameSyncTiming.VideoExtractCalls + ", hit=" + frameSyncTiming.VideoExtractCacheHits + ", miss=" + frameSyncTiming.VideoExtractCacheMisses + ", tempo=" + frameSyncTiming.VideoExtractCachedMs + "ms");
                        }
                    }
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Dettaglio: " + this._frameSyncService.GetDetailSummary());
                    ConsoleHelper.Progress(LogSection.FrameSync, 88, "FrameSync: completato");
                }
                else if (speedCorrectionMode == Options.SPEED_CORRECTION_AUTO && detectedSourceFps > 0.0 && detectedLangFps > 0.0 && !speedMismatch)
                {
                    // Frame-sync fallito ma FPS diversi classificati come telecine:
                    // probabile falso telecine (durate uguali per coincidenza), ritenta con speed correction
                    if (SpeedCorrectionService.ShouldBlockAutoForVfr(record.SourceFilePath, record.LangFilePath, out speedBlockReason))
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  Retry speed correction forzata saltato: " + speedBlockReason);
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Error, "  Sincronizzazione fallita");
                        ConsoleHelper.Progress(LogSection.FrameSync, 76, "FrameSync: non conclusivo");
                        done = this.FailAndFinalizeRecord(record, "Frame sync fallito");
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  Fallito con telecine presunto, ritento con speed correction forzata...");

                        // Risolvi ffmpeg se necessario
                        ffmpegPath = this._ffmpegPath;
                        if (string.IsNullOrEmpty(ffmpegPath))
                        {
                            ffmpegPath = this.ResolveFfmpegForSpeed();
                            this._ffmpegPath = ffmpegPath;
                        }

                        if (!string.IsNullOrEmpty(ffmpegPath) && sourceDefaultDuration == 0)
                        {
                            // Recupera default_duration se non ancora estratta (non era passato dal blocco speedMismatch)
                            sourceDefaultDuration = Utils.GetVideoDefaultDuration(sourceInfo.Tracks);
                            langDefaultDuration = Utils.GetVideoDefaultDuration(langInfo.Tracks);
                            sourceDurationMs = (int)(sourceInfo.ContainerDurationNs / 1000000);
                        }

                        if (!string.IsNullOrEmpty(ffmpegPath) && sourceDefaultDuration > 0 && langDefaultDuration > 0)
                        {
                            speedService = new SpeedCorrectionService(ffmpegPath);
                            speedService.SetAnalysisCrop(this._opts.AnalysisCropSourcePx, this._opts.AnalysisCropLanguagePx);
                            ConsoleHelper.Progress(LogSection.Speed, 20, "Speed: stretch");
                            speedOk = speedService.FindDelayAndVerify(record.SourceFilePath, record.LangFilePath, sourceDefaultDuration, langDefaultDuration, sourceDurationMs);

                            record.SpeedCorrectionTimeMs = speedService.ExecutionTimeMs;

                            if (speedOk)
                            {
                                syncOffset = speedService.SyncDelayMs;
                                record.StretchFactor = speedService.StretchFactor;
                                record.SpeedCorrectionApplied = true;

                                ConsoleHelper.Write(LogSection.Speed, LogLevel.Success, "  Correzione forzata: delay=" + speedService.InitialDelayMs + "ms, sync=" + speedService.SyncDelayMs + "ms, stretch=" + speedService.StretchFactor + " (" + speedService.ExecutionTimeMs + "ms)");
                                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Verifica: " + speedService.GetDetailSummary());
                                ConsoleHelper.Progress(LogSection.Speed, 72, "Speed: completata");
                            }
                            else
                            {
                                ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, "  Correzione velocità forzata fallita");
                                done = this.FailAndFinalizeRecord(record, "Frame sync e speed correction falliti");
                            }
                        }
                        else
                        {
                            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Error, "  Sincronizzazione fallita (ffmpeg o default_duration non disponibili per retry)");
                            ConsoleHelper.Progress(LogSection.FrameSync, 76, "FrameSync: non conclusivo");
                            done = this.FailAndFinalizeRecord(record, "Frame sync fallito");
                        }
                    }
                }
                else
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Error, "  Sincronizzazione fallita");
                    ConsoleHelper.Progress(LogSection.FrameSync, 76, "FrameSync: non conclusivo");
                    done = this.FailAndFinalizeRecord(record, "Frame sync fallito");
                }
            }

            if (!done)
            {
                this.MarkAnalyzedAndFinalize(
                    record,
                    syncOffset,
                    true,
                    "  Analisi completata: delay audio " + Utils.FormatDelay(syncOffset + this._opts.AudioDelay + record.ManualAudioDelayMs) + ", sub " + Utils.FormatDelay(syncOffset + this._opts.SubtitleDelay + record.ManualSubDelayMs));
            }
        }


        #endregion

        #region Metodi privati

        /// <summary>
        /// Finalizza un record in errore e notifica aggiornamento
        /// </summary>
        /// <param name="record">Record da aggiornare</param>
        /// <param name="errorMessage">Messaggio errore</param>
        /// <returns>true</returns>
        private bool FailAndFinalizeRecord(FileProcessingRecord record, string errorMessage)
        {
            if (record != null)
            {
                record.ErrorMessage = errorMessage;
                record.Status = FileStatus.Error;
                if (this._fileUpdated != null)
                {
                    this._fileUpdated(record);
                }
            }

            this._clearLogRedirect();

            return true;
        }

        /// <summary>
        /// Finalizza un record analizzato con offset finale e notifica
        /// </summary>
        /// <param name="record">Record da aggiornare</param>
        /// <param name="syncOffset">Offset sincronizzazione applicato</param>
        /// <param name="buildMergeCommand">true per rigenerare preview command</param>
        /// <param name="completionMessage">Messaggio di esito nel log (opzionale)</param>
        /// <returns>true</returns>
        private bool MarkAnalyzedAndFinalize(FileProcessingRecord record, int syncOffset, bool buildMergeCommand, string completionMessage)
        {
            if (record != null)
            {
                record.SyncOffsetMs = syncOffset;
                record.AudioDelayApplied = syncOffset + this._opts.AudioDelay + record.ManualAudioDelayMs;
                record.SubDelayApplied = syncOffset + this._opts.SubtitleDelay + record.ManualSubDelayMs;
                record.Status = FileStatus.Analyzed;
            }

            if (!string.IsNullOrEmpty(completionMessage))
            {
                ConsoleHelper.Write(LogSection.General, LogLevel.Success, completionMessage);
            }

            if (buildMergeCommand)
            {
                this._buildMergeCommand(record);
            }

            if (this._fileUpdated != null)
            {
                this._fileUpdated(record);
            }

            this._clearLogRedirect();

            return true;
        }

        /// <summary>
        /// Costruisce la policy delle tracce audio che DeepAnalysis può usare come conferma locale
        /// </summary>
        /// <param name="sourceInfo">Metadata source</param>
        /// <param name="langInfo">Metadata file lingua</param>
        /// <returns>Policy tracce audio per DeepAnalysis</returns>
        private DeepAnalysisTrackPolicy BuildDeepAnalysisTrackPolicy(MkvFileInfo sourceInfo, MkvFileInfo langInfo)
        {
            DeepAnalysisTrackPolicy result = new DeepAnalysisTrackPolicy();
            List<TrackInfo> sourceFineTuneAudio = new List<TrackInfo>();
            List<TrackInfo> sourceValidationAudio = new List<TrackInfo>();
            List<TrackInfo> languageFineTuneAudio = new List<TrackInfo>();
            List<TrackInfo> languageValidationAudio = new List<TrackInfo>();
            string[] languageCodecPatterns = this.ResolveCodecPatterns(this._opts.AudioCodec);
            TrackInfo sourceTrack;
            TrackInfo sourceFineTuneTrack = null;
            TrackInfo languageFineTuneTrack = null;
            TrackInfo languageTrack;

            if (sourceInfo == null || langInfo == null || sourceInfo.Tracks == null || langInfo.Tracks == null)
            {
                result.RejectReason = "metadata audio non disponibili";
                result.SourceFineTuneRejectReason = "metadata audio source non disponibili";
                result.LanguageFineTuneRejectReason = "metadata audio language non disponibili";
                return result;
            }

            if (this._opts.SubOnly)
            {
                result.RejectReason = "sub-only: nessuna traccia language audio finale";
                result.SourceFineTuneRejectReason = "sub-only: nessun fine tuning audio necessario";
                result.LanguageFineTuneRejectReason = "sub-only: nessuna traccia language audio finale";
                return result;
            }

            for (int i = 0; i < sourceInfo.Tracks.Count; i++)
            {
                sourceTrack = sourceInfo.Tracks[i];
                if (!string.Equals(sourceTrack.Type, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sourceFineTuneAudio.Add(sourceTrack);

                if (!string.IsNullOrEmpty(sourceTrack.Language) || !string.IsNullOrEmpty(sourceTrack.LanguageIetf))
                    sourceValidationAudio.Add(sourceTrack);
            }

            for (int i = 0; i < langInfo.Tracks.Count; i++)
            {
                languageTrack = langInfo.Tracks[i];
                if (!string.Equals(languageTrack.Type, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(languageTrack.Language) || !string.IsNullOrEmpty(languageTrack.LanguageIetf))
                    languageValidationAudio.Add(languageTrack);

                if (!this.IsTrackLanguageInList(languageTrack, this._opts.TargetLanguage))
                {
                    continue;
                }

                if (languageCodecPatterns != null && !CodecMapping.MatchesCodec(languageTrack.Codec, languageCodecPatterns))
                {
                    continue;
                }

                languageFineTuneAudio.Add(languageTrack);
            }

            for (int i = 0; i < sourceFineTuneAudio.Count && sourceFineTuneTrack == null; i++)
            {
                if (sourceFineTuneAudio[i].DefaultTrack)
                {
                    sourceFineTuneTrack = sourceFineTuneAudio[i];
                }
            }

            for (int i = 0; i < sourceFineTuneAudio.Count && sourceFineTuneTrack == null; i++)
            {
                if (!string.IsNullOrEmpty(sourceFineTuneAudio[i].Language) || !string.IsNullOrEmpty(sourceFineTuneAudio[i].LanguageIetf))
                    sourceFineTuneTrack = sourceFineTuneAudio[i];
            }

            if (sourceFineTuneTrack == null && sourceFineTuneAudio.Count > 0)
            {
                sourceFineTuneTrack = sourceFineTuneAudio[0];
            }

            if (sourceFineTuneTrack != null)
            {
                result.SourceFineTuneAudioAvailable = true;
                result.SourceFineTuneTrackId = sourceFineTuneTrack.Id;
                result.SourceFineTuneTrackName = sourceFineTuneTrack.Name;
                result.SourceFineTuneAudioStreamIndex = this.GetAudioStreamIndex(sourceInfo.Tracks, sourceFineTuneTrack.Id);
            }
            else
            {
                result.SourceFineTuneRejectReason = "nessuna traccia source audio disponibile";
            }

            if (languageFineTuneAudio.Count > 0)
            {
                languageFineTuneTrack = languageFineTuneAudio[0];
                result.LanguageFineTuneAudioAvailable = true;
                result.LanguageFineTuneTrackId = languageFineTuneTrack.Id;
                result.LanguageFineTuneTrackName = languageFineTuneTrack.Name;
                result.LanguageFineTuneAudioStreamIndex = this.GetAudioStreamIndex(langInfo.Tracks, languageFineTuneTrack.Id);
            }
            else
            {
                result.LanguageFineTuneRejectReason = "nessuna traccia language audio finale";
            }

            for (int s = 0; s < sourceValidationAudio.Count && !result.AudioValidationAvailable; s++)
            {
                sourceTrack = sourceValidationAudio[s];
                for (int l = 0; l < languageValidationAudio.Count; l++)
                {
                    languageTrack = languageValidationAudio[l];
                    if (!this.IsSameTrackLanguage(sourceTrack, languageTrack))
                    {
                        continue;
                    }

                    result.AudioValidationAvailable = true;
                    result.TrackLanguage = !string.IsNullOrEmpty(sourceTrack.Language) ? sourceTrack.Language : sourceTrack.LanguageIetf;
                    result.SourceTrackId = sourceTrack.Id;
                    result.LanguageTrackId = languageTrack.Id;
                    result.SourceTrackName = sourceTrack.Name;
                    result.LanguageTrackName = languageTrack.Name;
                    result.SourceAudioStreamIndex = this.GetAudioStreamIndex(sourceInfo.Tracks, sourceTrack.Id);
                    result.LanguageAudioStreamIndex = this.GetAudioStreamIndex(langInfo.Tracks, languageTrack.Id);
                    break;
                }
            }

            if (!result.AudioValidationAvailable)
            {
                result.RejectReason = "nessuna coppia audio source/lang con stessa lingua";
            }

            return result;
        }

        /// <summary>
        /// Risolve i pattern codec da una lista di nomi codec
        /// </summary>
        /// <param name="codecNames">Lista nomi codec</param>
        /// <returns>Array pattern o null se non ci sono filtri</returns>
        private string[] ResolveCodecPatterns(List<string> codecNames)
        {
            List<string> patterns = new List<string>();
            string[] codecPatterns;

            if (codecNames == null || codecNames.Count == 0)
            {
                return null;
            }

            for (int i = 0; i < codecNames.Count; i++)
            {
                codecPatterns = CodecMapping.GetCodecPatterns(codecNames[i]);
                if (codecPatterns == null)
                {
                    continue;
                }

                for (int p = 0; p < codecPatterns.Length; p++)
                {
                    if (!patterns.Contains(codecPatterns[p]))
                    {
                        patterns.Add(codecPatterns[p]);
                    }
                }
            }

            return patterns.Count > 0 ? patterns.ToArray() : null;
        }

        /// <summary>
        /// Verifica se una traccia ha una lingua contenuta nella lista
        /// </summary>
        /// <param name="track">Traccia da verificare</param>
        /// <param name="languages">Lista lingue ammesse</param>
        /// <returns>True se la traccia corrisponde</returns>
        private bool IsTrackLanguageInList(TrackInfo track, List<string> languages)
        {
            if (track == null || languages == null || languages.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < languages.Count; i++)
            {
                if (string.Equals(track.Language, languages[i], StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(track.LanguageIetf) && (track.LanguageIetf.StartsWith(languages[i], StringComparison.OrdinalIgnoreCase) || string.Equals(track.LanguageIetf, languages[i], StringComparison.OrdinalIgnoreCase))))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Verifica se due tracce audio hanno la stessa lingua effettiva
        /// </summary>
        /// <param name="sourceTrack">Traccia source</param>
        /// <param name="languageTrack">Traccia language</param>
        /// <returns>True se la lingua coincide</returns>
        private bool IsSameTrackLanguage(TrackInfo sourceTrack, TrackInfo languageTrack)
        {
            string[] sourceLanguages = new string[] { sourceTrack.Language, sourceTrack.LanguageIetf };
            string[] languageLanguages = new string[] { languageTrack.Language, languageTrack.LanguageIetf };

            for (int s = 0; s < sourceLanguages.Length; s++)
            {
                if (string.IsNullOrEmpty(sourceLanguages[s]))
                    continue;

                for (int l = 0; l < languageLanguages.Length; l++)
                {
                    if (string.IsNullOrEmpty(languageLanguages[l]))
                        continue;

                    if (string.Equals(sourceLanguages[s], languageLanguages[l], StringComparison.OrdinalIgnoreCase) ||
                        sourceLanguages[s].StartsWith(languageLanguages[l], StringComparison.OrdinalIgnoreCase) ||
                        languageLanguages[l].StartsWith(sourceLanguages[s], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Converte l'ID traccia MKV nell'indice audio usato da ffmpeg
        /// </summary>
        /// <param name="tracks">Tracce del container</param>
        /// <param name="trackId">ID MKV da cercare</param>
        /// <returns>Indice audio ffmpeg</returns>
        private int GetAudioStreamIndex(List<TrackInfo> tracks, int trackId)
        {
            int audioIndex = 0;

            for (int i = 0; i < tracks.Count; i++)
            {
                if (!string.Equals(tracks[i].Type, "audio", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (tracks[i].Id == trackId)
                {
                    return audioIndex;
                }

                audioIndex++;
            }

            return 0;
        }

        /// <summary>
        /// Risolve ffmpeg per le operazioni di speed/frame matching
        /// </summary>
        /// <returns>Percorso ffmpeg disponibile, oppure stringa vuota</returns>
        private string ResolveFfmpegForSpeed()
        {
            string result = this._ffmpegPath;

            if (string.IsNullOrEmpty(result))
            {
                // Se la pipeline non ha ancora un path ffmpeg, usa il provider centrale già configurato
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  Risoluzione ffmpeg per frame matching...");
                result = this._toolPathResolver.ResolveFfmpegPath(true, false);
                if (!string.IsNullOrEmpty(result))
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Success, "  ffmpeg trovato: " + result);
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  ffmpeg non disponibile");
                }
            }

            return result;
        }

        #endregion
    }
}
