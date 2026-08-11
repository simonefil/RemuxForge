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
using System.Threading;

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
        /// <param name="cancellationToken">Token di annullamento cooperativo</param>
        public void AnalyzeFile(FileProcessingRecord record, CancellationToken cancellationToken = default)
        {
            MkvFileInfo sourceInfo = null;
            MkvFileInfo langInfo = null;
            List<TrackInfo> sourceTracks;
            List<TrackInfo> langTracks;
            int syncOffset = 0;
            bool speedCorrectionActive = false;
            string speedCorrectionMode;
            string deepManualStretchFactor;
            string ffmpegPath;
            int sourceDurationMs = 0;
            SpeedCorrectionService speedService;
            bool speedOk;
            int frameSyncOffset;
            bool done = false;
            FrameSyncTimingInfo frameSyncTiming;
            VideoTimingInfo sourceTiming = null;
            // Ignora record non pendenti
            if (record.Status != FileStatus.Pending && record.Status != FileStatus.Error)
            {
                done = true;
            }

            if (!done)
            {
                // Elimina ogni risultato della precedente analisi prima di un nuovo tentativo
                record.ResetDerivedState();

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
                    speedOk = speedService.FindDelayAndVerifyManual(record.SourceFilePath, record.LangFilePath, this._opts.ManualStretchFactor);
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
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, "  Correzione velocità manuale fallita: " + speedService.GetDetailSummary());
                        done = this.FailAndFinalizeRecord(record, "Speed correction manuale fallita: " + speedService.GetDetailSummary());
                    }
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, "  ffmpeg non disponibile per speed correction manuale");
                    ConsoleHelper.Progress(LogSection.Speed, 72, "Speed: non applicata");
                    done = this.FailAndFinalizeRecord(record, "ffmpeg non disponibile per speed correction manuale");
                }
            }

            // Deep analysis: modalità avanzata per file con edit diversi
            if (!done && !speedCorrectionActive && this._opts.DeepAnalysis)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Avvio DeepAnalysis...");
                ConsoleHelper.Progress(LogSection.Deep, 8, "Deep: avvio");
                deepManualStretchFactor = "";

                if (speedCorrectionMode == Options.SPEED_CORRECTION_MANUAL)
                {
                    deepManualStretchFactor = this._opts.ManualStretchFactor;
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Stretch manuale DeepAnalysis: " + deepManualStretchFactor);
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
                    if (sourceDurationMs == 0 && sourceTiming != null && sourceTiming.DurationMs > 0.0)
                    {
                        // DeepAnalysis lavora sulla timeline video/common-track; la durata container può essere gonfiata da tracce non importate
                        sourceDurationMs = (int)Math.Round(sourceTiming.DurationMs);
                    }
                    if (sourceDurationMs == 0 && sourceInfo != null && sourceInfo.ContainerDurationNs > 0)
                    {
                        sourceDurationMs = (int)(sourceInfo.ContainerDurationNs / 1000000);
                    }

                    if (!done && sourceDurationMs > 0)
                    {
                        EditMap editMap;
                        DeepAnalysisService deepService = new DeepAnalysisService(ffmpegPath, this._toolPathResolver);
                        editMap = deepService.Analyze(record.SourceFilePath, record.LangFilePath, deepManualStretchFactor, this._opts.AnalysisCropSourcePx, this._opts.AnalysisCropLanguagePx, cancellationToken);
                        record.DeepAnalysisResult = deepService.LastResult;
                        record.DeepAnalysisTimeMs = deepService.LastResult != null ? deepService.LastResult.TotalElapsedMs : 0;

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

                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Success, "  Completata: " + editMap.Operations.Count + " operazioni, delay iniziale " + editMap.InitialDelayMs + "ms (" + record.DeepAnalysisTimeMs + "ms)");
                            ConsoleHelper.Progress(LogSection.Deep, 90, "Deep: diagnostica");
                            this._diagnosticsWriter.WriteDeepAnalysisIfEnabled(record, this._opts);
                        }
                        else
                        {
                            string deepRejectReason = record.DeepAnalysisResult != null && !string.IsNullOrEmpty(record.DeepAnalysisResult.RejectReason) ? record.DeepAnalysisResult.RejectReason : "elaborazione bloccata";
                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  Deep analysis fallita: " + deepRejectReason);
                            ConsoleHelper.Progress(LogSection.Deep, 98, "Deep: errore");
                            this._diagnosticsWriter.WriteDeepAnalysisIfEnabled(record, this._opts);
                            done = this.FailAndFinalizeRecord(record, "Deep analysis fallita: " + deepRejectReason);
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
