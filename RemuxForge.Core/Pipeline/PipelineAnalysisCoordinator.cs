using RemuxForge.Core.Analysis.Deep;
using RemuxForge.Core.Analysis.FrameSync;
using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// Coordina l'analisi di un record della pipeline, dalla lettura dei metadata alla sincronizzazione temporale
    /// </summary>
    public class PipelineAnalysisCoordinator
    {
        #region Variabili di classe

        /// <summary>
        /// Opzioni operative che determinano le fasi e i parametri dell'analisi
        /// </summary>
        private Options _opts;

        /// <summary>
        /// Indica se il flusso deve analizzare anche il file lingua per il merge
        /// </summary>
        private bool _needsMerge;

        /// <summary>
        /// Percorso di ffmpeg disponibile per le analisi che richiedono elaborazione video
        /// </summary>
        private string _ffmpegPath;

        /// <summary>
        /// Servizio per la sincronizzazione basata sul confronto visivo
        /// </summary>
        private FrameSyncService _frameSyncService;

        /// <summary>
        /// Componente che estrae lingue e seleziona tracce per il record
        /// </summary>
        private PipelineTrackMapper _trackMapper;

        /// <summary>
        /// Componente che scrive le diagnostiche opzionali delle analisi
        /// </summary>
        private PipelineDiagnosticsWriter _diagnosticsWriter;

        /// <summary>
        /// Callback che recupera i metadata MKV di un file
        /// </summary>
        private Func<string, MkvFileInfo> _fileInfoProvider;

        /// <summary>
        /// Callback che indirizza i messaggi di log al record in elaborazione
        /// </summary>
        private Action<FileProcessingRecord> _setupLogRedirect;

        /// <summary>
        /// Callback che rimuove il redirect del log al termine dell'analisi
        /// </summary>
        private Action _clearLogRedirect;

        /// <summary>
        /// Callback invocata quando cambia lo stato del record
        /// </summary>
        private Action<FileProcessingRecord> _fileUpdated;

        /// <summary>
        /// Callback che rigenera il comando di merge dopo un'analisi riuscita
        /// </summary>
        private Action<FileProcessingRecord> _buildMergeCommand;

        /// <summary>
        /// Resolver centralizzato dei percorsi degli strumenti esterni
        /// </summary>
        private ToolPathResolverService _toolPathResolver;

        /// <summary>
        /// Resolver della durata e della temporizzazione del video sorgente
        /// </summary>
        private VideoTimingResolver _timingResolver;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza il coordinatore con i servizi e i callback necessari all'analisi
        /// </summary>
        /// <param name="opts">Opzioni operative</param>
        /// <param name="needsMerge">Indica se il file richiede merge o remux con un file lingua</param>
        /// <param name="ffmpegPath">Percorso di ffmpeg già risolto, se disponibile</param>
        /// <param name="frameSyncService">Servizio per la sincronizzazione tramite confronto visivo</param>
        /// <param name="trackMapper">Mapper delle tracce e delle lingue della pipeline</param>
        /// <param name="diagnosticsWriter">Writer delle diagnostiche opzionali</param>
        /// <param name="fileInfoProvider">Provider dei metadata MKV</param>
        /// <param name="setupLogRedirect">Callback per associare il log al record corrente</param>
        /// <param name="clearLogRedirect">Callback per rimuovere l'associazione del log al record</param>
        /// <param name="fileUpdated">Callback per notificare l'aggiornamento del record</param>
        /// <param name="buildMergeCommand">Callback per rigenerare il comando di merge</param>
        /// <param name="toolPathResolver">Resolver degli strumenti esterni, oppure null per usare la configurazione applicativa</param>
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
        /// Restituisce il percorso di ffmpeg attualmente risolto
        /// </summary>
        public string FfmpegPath
        {
            get { return this._ffmpegPath; }
        }

        /// <summary>
        /// Analizza un record applicando speed correction, DeepAnalysis o FrameSync secondo le opzioni configurate
        /// </summary>
        /// <param name="record">Record da analizzare e aggiornare con l'esito dell'elaborazione</param>
        /// <param name="cancellationToken">Token per interrompere cooperativamente le analisi lunghe</param>
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
            // Ignora i record che non sono in uno stato rielaborabile
            if (record.Status != FileStatus.Pending && record.Status != FileStatus.Error)
            {
                done = true;
            }

            if (!done)
            {
                // Pulisce gli esiti derivati per evitare di riutilizzare risultati di un tentativo precedente
                record.ResetDerivedState();

                // Associa il log al record corrente per conservare il contesto dell'analisi
                this._setupLogRedirect(record);

                // Porta il record nello stato di analisi e propaga subito l'aggiornamento
                record.Status = FileStatus.Analyzing;
                if (this._fileUpdated != null)
                {
                    this._fileUpdated(record);
                }

                ConsoleHelper.Write(LogSection.General, LogLevel.Header, "Analisi: " + record.SourceFileName);
                ConsoleHelper.Write(LogSection.General, LogLevel.Debug, "  ID Episodio: " + record.EpisodeId);

                // Carica i metadata del file sorgente per preparare le fasi successive
                sourceInfo = this._fileInfoProvider(record.SourceFilePath);
                sourceTracks = (sourceInfo != null) ? sourceInfo.Tracks : null;

                // Prepara nel record il riepilogo delle lingue e delle tracce sorgente
                record.SourceAudioLangs = this._trackMapper.GetAudioLanguages(sourceTracks);
                record.SourceSubLangs = this._trackMapper.GetSubtitleLanguages(sourceTracks);
                record.SourceAudioTracks = this._trackMapper.FilterTracksByType(sourceTracks, "audio");
                record.SourceSubTracks = this._trackMapper.FilterTracksByType(sourceTracks, "subtitles");

                if (this._needsMerge)
                {
                    // Nel merge serve anche il contesto temporale e delle tracce del file lingua
                    ConsoleHelper.Write(LogSection.General, LogLevel.Info, "  Match: " + record.LangFileName);

                    langInfo = this._fileInfoProvider(record.LangFilePath);
                    langTracks = (langInfo != null) ? langInfo.Tracks : null;
                    sourceTiming = this._timingResolver.Resolve(record.SourceFilePath, sourceInfo);

                    record.LangAudioLangs = this._trackMapper.GetAudioLanguages(langTracks);
                    record.LangSubLangs = this._trackMapper.GetSubtitleLanguages(langTracks);

                    if (langTracks == null)
                    {
                        // Interrompe l'analisi perché senza metadata non è possibile costruire il merge
                        ConsoleHelper.Write(LogSection.General, LogLevel.Error, "  Impossibile leggere info tracce file lingua");
                        done = this.FailAndFinalizeRecord(record, "Impossibile leggere tracce file lingua");
                    }
                }
                else
                {
                    // Senza merge non servono confronti tra file e il record può essere completato subito
                    done = this.MarkAnalyzedAndFinalize(record, 0, false, "  Analisi completata (no merge)");
                }
            }

            speedCorrectionMode = this._opts.SpeedCorrectionMode != null ? this._opts.SpeedCorrectionMode : Options.SPEED_CORRECTION_OFF;

            // Applica la correzione manuale quando non è attiva la deep analysis
            if (!done && sourceInfo != null && langInfo != null && !this._opts.DeepAnalysis && speedCorrectionMode == Options.SPEED_CORRECTION_MANUAL)
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Phase, AppText.F("speed.pipeline.manualStart", this._opts.ManualStretchFactor));
                ConsoleHelper.Progress(LogSection.Speed, 10, AppText.T("speed.pipeline.progressSetup"));

                ffmpegPath = this.ResolveFfmpegForSpeed();
                if (!string.IsNullOrEmpty(ffmpegPath))
                {
                    ConsoleHelper.Progress(LogSection.Speed, 14, AppText.T("speed.pipeline.progressFfmpeg"));
                    this._ffmpegPath = ffmpegPath;
                    if (sourceInfo.ContainerDurationNs > 0)
                    {
                        sourceDurationMs = (int)(sourceInfo.ContainerDurationNs / 1000000);
                    }

                    speedService = new SpeedCorrectionService(ffmpegPath);
                    speedService.SetAnalysisCrop(this._opts.AnalysisCropSourcePx, this._opts.AnalysisCropLanguagePx);
                    ConsoleHelper.Progress(LogSection.Speed, 20, AppText.T("speed.pipeline.progressStretch"));
                    speedOk = speedService.FindDelayAndVerifyManual(record.SourceFilePath, record.LangFilePath, this._opts.ManualStretchFactor);
                    record.SpeedCorrectionTimeMs = speedService.ExecutionTimeMs;

                    if (speedOk)
                    {
                        syncOffset = speedService.SyncDelayMs;
                        record.StretchFactor = speedService.StretchFactor;
                        record.SpeedCorrectionApplied = true;
                        speedCorrectionActive = true;

                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Success, AppText.F("speed.pipeline.manualCompleted", speedService.InitialDelayMs, speedService.SyncDelayMs, speedService.StretchFactor, speedService.ExecutionTimeMs));
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, AppText.F("speed.pipeline.verification", speedService.GetDetailSummary()));
                        ConsoleHelper.Progress(LogSection.Speed, 90, AppText.T("speed.pipeline.progressCompleted"));
                    }
                    else
                    {
                        string speedFailure = AppText.F("speed.pipeline.manualFailed", speedService.GetDetailSummary());
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, speedFailure);
                        done = this.FailAndFinalizeRecord(record, speedFailure);
                    }
                }
                else
                {
                    string ffmpegUnavailable = AppText.T("speed.pipeline.ffmpegUnavailable");
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, ffmpegUnavailable);
                    ConsoleHelper.Progress(LogSection.Speed, 90, AppText.T("speed.pipeline.progressNotApplied"));
                    done = this.FailAndFinalizeRecord(record, ffmpegUnavailable);
                }
            }

            // Avvia la modalità avanzata per gestire file con edit temporali diversi
            if (!done && !speedCorrectionActive && this._opts.DeepAnalysis)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, AppText.T("deep.temporal.pipeline.start"));
                ConsoleHelper.Progress(LogSection.Deep, 8, AppText.T("deep.temporal.pipeline.progressStart"));
                deepManualStretchFactor = "";

                if (speedCorrectionMode == Options.SPEED_CORRECTION_MANUAL)
                {
                    deepManualStretchFactor = this._opts.ManualStretchFactor;
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, AppText.F("deep.temporal.pipeline.manualStretch", deepManualStretchFactor));
                }
                // Risolve ffmpeg solo quando la fase avanzata ne ha effettivamente bisogno
                ffmpegPath = this._ffmpegPath;
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    ffmpegPath = this.ResolveFfmpegForSpeed();
                    this._ffmpegPath = ffmpegPath;
                }

                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    string ffmpegUnavailableReason = AppText.T("deep.temporal.pipeline.ffmpegUnavailable");
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, ffmpegUnavailableReason);
                    ConsoleHelper.Progress(LogSection.Deep, 98, AppText.T("deep.temporal.pipeline.progressError"));
                    done = this.FailAndFinalizeRecord(record, ffmpegUnavailableReason.Trim());
                }

                if (!done)
                {
                    if (sourceDurationMs == 0 && sourceTiming != null && sourceTiming.DurationMs > 0.0)
                    {
                        // Usa la timeline video perché la durata container può includere tracce non importate
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

                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Success, AppText.F("deep.temporal.pipeline.completed", editMap.Operations.Count, editMap.InitialDelayMs, record.DeepAnalysisTimeMs));
                            ConsoleHelper.Progress(LogSection.Deep, 90, AppText.T("deep.temporal.pipeline.progressDiagnostics"));
                            this._diagnosticsWriter.WriteDeepAnalysisIfEnabled(record, this._opts);
                        }
                        else
                        {
                            string deepRejectReason = record.DeepAnalysisResult != null && !string.IsNullOrEmpty(record.DeepAnalysisResult.RejectReason) ? record.DeepAnalysisResult.RejectReason : AppText.T("deep.temporal.pipeline.blocked");
                            string deepFailure = AppText.F("deep.temporal.pipeline.failed", deepRejectReason);
                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, deepFailure);
                            ConsoleHelper.Progress(LogSection.Deep, 98, AppText.T("deep.temporal.pipeline.progressError"));
                            this._diagnosticsWriter.WriteDeepAnalysisIfEnabled(record, this._opts);
                            done = this.FailAndFinalizeRecord(record, deepFailure.Trim());
                        }
                    }
                    else
                    {
                        string insufficientVideoData = AppText.T("deep.temporal.pipeline.insufficientVideoData");
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, insufficientVideoData);
                        ConsoleHelper.Progress(LogSection.Deep, 98, AppText.T("deep.temporal.pipeline.progressError"));
                        done = this.FailAndFinalizeRecord(record, insufficientVideoData.Trim());
                    }
                }
            }

            // Esegue il frame-sync solo se non è già disponibile una correzione temporale
            if (!done && !speedCorrectionActive && this._opts.FrameSync && this._frameSyncService != null)
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, AppText.T("framesync.pipeline.start"));
                ConsoleHelper.Progress(LogSection.FrameSync, 10, AppText.T("framesync.pipeline.progressSetup"));

                frameSyncOffset = this._frameSyncService.RefineOffset(record.SourceFilePath, record.LangFilePath, speedCorrectionMode == Options.SPEED_CORRECTION_MANUAL ? this._opts.ManualStretchFactor : "");
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
                            record.FrameSyncResult.FailureReason = AppText.T("framesync.pipeline.initialNotVerified");
                        }
                        else if (acceptedFrameSyncPoints < AppSettingsService.Instance.Settings.Advanced.FrameSync.MinValidPoints)
                        {
                            record.FrameSyncResult.FailureReason = AppText.T("framesync.pipeline.insufficientPoints");
                        }
                        else
                        {
                            record.FrameSyncResult.FailureReason = AppText.T("framesync.pipeline.insufficientConfidence");
                        }
                    }

                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, AppText.F("framesync.pipeline.notApplicable", acceptedFrameSyncPoints, AppSettingsService.Instance.Settings.Advanced.VideoSync.NumCheckPoints, record.FrameSyncResult != null ? record.FrameSyncResult.Confidence.ToString("P0", System.Globalization.CultureInfo.InvariantCulture) : "0%", AppSettingsService.Instance.Settings.Advanced.FrameSync.FinalMinConfidence.ToString("P0", System.Globalization.CultureInfo.InvariantCulture)));
                    ConsoleHelper.Progress(LogSection.FrameSync, 76, AppText.T("framesync.pipeline.inconclusive"));
                }

                this._diagnosticsWriter.WriteFrameSyncIfEnabled(record, this._opts);

                if (frameSyncAccepted)
                {
                    syncOffset = frameSyncOffset;
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Success, AppText.F("framesync.pipeline.offset", Utils.FormatDelay(frameSyncOffset), this._frameSyncService.FrameSyncTimeMs));
                    if (record.FrameSyncResult != null)
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, AppText.F("framesync.pipeline.confidence", record.FrameSyncResult.Confidence.ToString("P0", System.Globalization.CultureInfo.InvariantCulture)));
                        frameSyncTiming = record.FrameSyncResult.Timing;
                        if (frameSyncTiming != null)
                        {
                            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, AppText.F("framesync.pipeline.timing", frameSyncTiming.VideoInfoMs, frameSyncTiming.GeometryMs, frameSyncTiming.InitialSearchMs, frameSyncTiming.CheckpointsMs, frameSyncTiming.TotalMs));
                            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, AppText.F("framesync.pipeline.pairs", frameSyncTiming.InitialPairCount, frameSyncTiming.CheckpointPairCount));
                        }
                    }
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, AppText.F("framesync.pipeline.detail", this._frameSyncService.GetDetailSummary()));
                    ConsoleHelper.Progress(LogSection.FrameSync, 88, AppText.T("framesync.pipeline.completed"));
                }
                else
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Error, AppText.T("framesync.pipeline.failed"));
                    ConsoleHelper.Progress(LogSection.FrameSync, 76, AppText.T("framesync.pipeline.inconclusive"));
                    done = this.FailAndFinalizeRecord(record, AppText.T("framesync.pipeline.failureReason"));
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
        /// Imposta lo stato di errore del record, notifica l'aggiornamento e chiude il redirect del log
        /// </summary>
        /// <param name="record">Record da portare in errore</param>
        /// <param name="errorMessage">Messaggio da registrare nel record</param>
        /// <returns>True per indicare che il flusso è stato finalizzato</returns>
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
        /// Imposta gli offset risultanti, aggiorna lo stato del record e chiude l'analisi
        /// </summary>
        /// <param name="record">Record da finalizzare</param>
        /// <param name="syncOffset">Offset di sincronizzazione da applicare ad audio e sottotitoli</param>
        /// <param name="buildMergeCommand">Indica se rigenerare il comando di merge</param>
        /// <param name="completionMessage">Messaggio opzionale da scrivere nel log</param>
        /// <returns>True per indicare che il flusso è stato finalizzato</returns>
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
        /// Restituisce il percorso di ffmpeg già disponibile o lo risolve tramite il resolver centrale
        /// </summary>
        /// <returns>Percorso di ffmpeg disponibile oppure stringa vuota se la risoluzione fallisce</returns>
        private string ResolveFfmpegForSpeed()
        {
            string result = this._ffmpegPath;

            if (string.IsNullOrEmpty(result))
            {
                // Se la pipeline non ha ancora un percorso ffmpeg, usa il resolver centrale già configurato
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, AppText.T("speed.pipeline.ffmpegResolving"));
                result = this._toolPathResolver.ResolveFfmpegPath(true, false);
                if (!string.IsNullOrEmpty(result))
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Success, AppText.F("speed.pipeline.ffmpegFound", result));
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, AppText.T("speed.pipeline.ffmpegMissing"));
                }
            }

            return result;
        }

        #endregion
    }
}
