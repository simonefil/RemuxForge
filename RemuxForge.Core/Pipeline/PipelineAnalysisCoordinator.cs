using RemuxForge.Core.Analysis.Deep;
using RemuxForge.Core.Analysis.FrameSync;
using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Audio;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.IO;
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

        /// <summary>Notifica i media disponibili prima dell'analisi temporale</summary>
        public Action<FileProcessingRecord, MkvFileInfo, CancellationToken> MediaReady { get; set; }

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
            // Ignora i record che non sono in uno stato rielaborabile
            if (record.Status != FileStatus.Pending && record.Status != FileStatus.Error)
            {
                return;
            }

            AnalysisRun run = new AnalysisRun(record, this._opts.SpeedCorrectionMode != null ? this._opts.SpeedCorrectionMode : Options.SPEED_CORRECTION_OFF, cancellationToken);
            if (this.ReadTrackInfo(run))
            {
                return;
            }

            bool comparesFrames = this._opts.DeepAnalysis || this._opts.FrameSync;
            bool readsVideo = run.SourceInfo != null && run.LangInfo != null && (comparesFrames || run.SpeedCorrectionMode == Options.SPEED_CORRECTION_MANUAL);
            if (readsVideo && comparesFrames && this.RejectInterlacedVideo(run))
            {
                return;
            }

            // Le analisi leggono i fotogrammi solo da Matroska: gli altri container passano da un temporaneo rilasciato a fine analisi (D12),
            // che resta a chi tiene già lo stesso file: l'elaborazione che segue nella CLI, un editor EditMap aperto
            using NormalizedMediaPair media = readsVideo ? NormalizedMediaStore.Instance.AcquirePair(record.SourceFilePath, record.LangFilePath, false, this._opts.MkvMergePath) : null;
            run.Media = media;
            if (media != null)
            {
                string mediaError = media.Open(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (mediaError.Length > 0)
                {
                    ConsoleHelper.Write(LogSection.General, LogLevel.Error, mediaError);
                    this.FailAndFinalizeRecord(record, mediaError, true);
                    return;
                }
            }

            if (run.SourceInfo != null && run.LangInfo != null)
                this.MediaReady?.Invoke(record, run.LangInfo, cancellationToken);

            // Applica la correzione manuale quando non è attiva la deep analysis
            if (run.SourceInfo != null && run.LangInfo != null && !this._opts.DeepAnalysis && run.SpeedCorrectionMode == Options.SPEED_CORRECTION_MANUAL && this.ApplyManualSpeed(run))
            {
                return;
            }

            // Avvia la modalità avanzata per gestire file con edit temporali diversi
            if (!run.SpeedCorrectionActive && this._opts.DeepAnalysis && this.RunDeepAnalysis(run))
            {
                return;
            }

            // Esegue il frame-sync solo se non è già disponibile una correzione temporale
            if (!run.SpeedCorrectionActive && this._opts.FrameSync && this._frameSyncService != null && this.RunFrameSync(run))
            {
                return;
            }

            this.MarkAnalyzedAndFinalize(
                record,
                run.SyncOffset,
                true,
                AppText.F("remux.pipeline.analysisCompleted", Utils.FormatDelay(run.SyncOffset + this._opts.AudioDelay + record.ManualAudioDelayMs), Utils.FormatDelay(run.SyncOffset + this._opts.SubtitleDelay + record.ManualSubDelayMs)));
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Porta il record in analisi e legge i metadata dei file; senza merge l'analisi finisce qui
        /// </summary>
        /// <param name="run">Analisi in corso, riceve metadata e temporizzazione del sorgente</param>
        /// <returns>True se il record è stato finalizzato</returns>
        private bool ReadTrackInfo(AnalysisRun run)
        {
            FileProcessingRecord record = run.Record;

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

            ConsoleHelper.Write(LogSection.General, LogLevel.Header, AppText.F("remux.pipeline.analysisHeader", record.SourceFileName));
            ConsoleHelper.Write(LogSection.General, LogLevel.Debug, AppText.F("remux.pipeline.episodeId", record.EpisodeId));

            // Carica i metadata del file sorgente per preparare le fasi successive
            run.SourceInfo = this._fileInfoProvider(record.SourceFilePath);
            List<TrackInfo> sourceTracks = (run.SourceInfo != null) ? run.SourceInfo.Tracks : null;

            // Prepara nel record il riepilogo delle lingue e delle tracce sorgente
            record.SourceAudioLangs = this._trackMapper.GetAudioLanguages(sourceTracks);
            record.SourceSubLangs = this._trackMapper.GetSubtitleLanguages(sourceTracks);
            record.SourceAudioTracks = this._trackMapper.FilterTracksByType(sourceTracks, "audio");
            record.SourceSubTracks = this._trackMapper.FilterTracksByType(sourceTracks, "subtitles");

            if (!this._needsMerge)
            {
                // Senza merge non servono confronti tra file e il record può essere completato subito
                return this.MarkAnalyzedAndFinalize(record, 0, false, AppText.T("remux.pipeline.analysisCompletedNoMerge"));
            }

            // Nel merge serve anche il contesto temporale e delle tracce del file lingua
            ConsoleHelper.Write(LogSection.General, LogLevel.Info, AppText.F("remux.pipeline.matchedLang", record.LangFileName));

            run.LangInfo = this._fileInfoProvider(record.LangFilePath);
            List<TrackInfo> langTracks = (run.LangInfo != null) ? run.LangInfo.Tracks : null;
            run.SourceTiming = this._timingResolver.Resolve(record.SourceFilePath, run.SourceInfo);

            record.LangAudioLangs = this._trackMapper.GetAudioLanguages(langTracks);
            record.LangSubLangs = this._trackMapper.GetSubtitleLanguages(langTracks);

            if (langTracks == null)
            {
                // Interrompe l'analisi perché senza metadata non è possibile costruire il merge
                ConsoleHelper.Write(LogSection.General, LogLevel.Error, AppText.T("remux.pipeline.langTrackInfoReadFailed"));
                return this.FailAndFinalizeRecord(record, AppText.T("remux.pipeline.langTracksReadFailed"), false);
            }

            return false;
        }

        /// <summary>
        /// FrameSync e Deep confrontano fotogrammi: un video interlacciato non ha un fotogramma confrontabile (D06)
        /// </summary>
        /// <param name="run">Analisi in corso</param>
        /// <returns>True se il record è stato finalizzato in errore</returns>
        private bool RejectInterlacedVideo(AnalysisRun run)
        {
            MediaInfoService mediaInfo = new MediaInfoService(this._toolPathResolver.ResolveMediaInfoPath(false));
            string[] files = new string[] { run.Record.SourceFilePath, run.Record.LangFilePath };
            for (int i = 0; i < files.Length; i++)
            {
                // Una scansione non letta non vale progressivo: il file va in errore
                string scanError = !mediaInfo.TryGetVideoScanType(files[i], out VideoScanType scanType) ? AppText.F("remux.pipeline.scanTypeReadFailed", Path.GetFileName(files[i])) :
                    scanType.IsInterlaced ? AppText.F("remux.pipeline.interlacedUnsupported", Path.GetFileName(files[i]), scanType) : "";
                if (scanError.Length > 0)
                {
                    ConsoleHelper.Write(LogSection.General, LogLevel.Error, scanError);
                    return this.FailAndFinalizeRecord(run.Record, scanError, true);
                }
            }

            return false;
        }

        /// <summary>
        /// Correzione di velocità con il fattore manuale, quando non è attiva la deep analysis
        /// </summary>
        /// <param name="run">Analisi in corso con i Matroska aperti; riceve offset e correzione attiva</param>
        /// <returns>True se il record è stato finalizzato in errore</returns>
        private bool ApplyManualSpeed(AnalysisRun run)
        {
            FileProcessingRecord record = run.Record;
            ConsoleHelper.Write(LogSection.Speed, LogLevel.Phase, AppText.F("speed.pipeline.manualStart", this._opts.ManualStretchFactor));
            ConsoleHelper.Progress(LogSection.Speed, 10, AppText.T("speed.pipeline.progressSetup"));

            string ffmpegPath = this.ResolveFfmpegForSpeed();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                string ffmpegUnavailable = AppText.T("speed.pipeline.ffmpegUnavailable");
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, ffmpegUnavailable);
                ConsoleHelper.Progress(LogSection.Speed, 90, AppText.T("speed.pipeline.progressNotApplied"));
                return this.FailAndFinalizeRecord(record, ffmpegUnavailable, true);
            }

            ConsoleHelper.Progress(LogSection.Speed, 14, AppText.T("speed.pipeline.progressFfmpeg"));
            this._ffmpegPath = ffmpegPath;

            SpeedCorrectionService speedService = new SpeedCorrectionService(ffmpegPath);
            speedService.SetAnalysisCrop(this._opts.AnalysisCropSourcePx, this._opts.AnalysisCropLanguagePx);
            ConsoleHelper.Progress(LogSection.Speed, 20, AppText.T("speed.pipeline.progressStretch"));
            bool speedOk = speedService.FindDelayAndVerifyManual(run.Media.Source.Path, run.Media.Lang.Path, this._opts.ManualStretchFactor);
            record.SpeedCorrectionTimeMs = speedService.ExecutionTimeMs;

            if (!speedOk)
            {
                string speedFailure = AppText.F("speed.pipeline.manualFailed", speedService.GetDetailSummary());
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, speedFailure);
                return this.FailAndFinalizeRecord(record, speedFailure, true);
            }

            run.SyncOffset = speedService.SyncDelayMs;
            record.StretchFactor = speedService.StretchFactor;
            record.SpeedCorrectionApplied = true;
            run.SpeedCorrectionActive = true;

            ConsoleHelper.Write(LogSection.Speed, LogLevel.Success, AppText.F("speed.pipeline.manualCompleted", speedService.InitialDelayMs, speedService.SyncDelayMs, speedService.StretchFactor, speedService.ExecutionTimeMs));
            ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, AppText.F("speed.pipeline.verification", speedService.GetDetailSummary()));
            ConsoleHelper.Progress(LogSection.Speed, 90, AppText.T("speed.pipeline.progressCompleted"));
            return false;
        }

        /// <summary>
        /// Deep analysis: mappa delle operazioni fra Source e Lang, con il fattore manuale se richiesto
        /// </summary>
        /// <param name="run">Analisi in corso con i Matroska aperti; riceve l'offset iniziale della mappa</param>
        /// <returns>True se il record è stato finalizzato in errore</returns>
        private bool RunDeepAnalysis(AnalysisRun run)
        {
            FileProcessingRecord record = run.Record;
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, AppText.T("deep.temporal.pipeline.start"));
            ConsoleHelper.Progress(LogSection.Deep, 8, AppText.T("deep.temporal.pipeline.progressStart"));
            string deepManualStretchFactor = "";

            if (run.SpeedCorrectionMode == Options.SPEED_CORRECTION_MANUAL)
            {
                deepManualStretchFactor = this._opts.ManualStretchFactor;
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, AppText.F("deep.temporal.pipeline.manualStretch", deepManualStretchFactor));
            }
            // Risolve ffmpeg solo quando la fase avanzata ne ha effettivamente bisogno
            string ffmpegPath = this._ffmpegPath;
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
                return this.FailAndFinalizeRecord(record, ffmpegUnavailableReason.Trim(), true);
            }

            // Usa la timeline video perché la durata container può includere tracce non importate
            int sourceDurationMs = 0;
            if (run.SourceTiming != null && run.SourceTiming.DurationMs > 0.0)
            {
                sourceDurationMs = (int)Math.Round(run.SourceTiming.DurationMs);
            }
            if (sourceDurationMs == 0 && run.SourceInfo != null && run.SourceInfo.ContainerDurationNs > 0)
            {
                sourceDurationMs = (int)(run.SourceInfo.ContainerDurationNs / 1000000);
            }

            if (sourceDurationMs <= 0)
            {
                string insufficientVideoData = AppText.T("deep.temporal.pipeline.insufficientVideoData");
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, insufficientVideoData);
                ConsoleHelper.Progress(LogSection.Deep, 98, AppText.T("deep.temporal.pipeline.progressError"));
                return this.FailAndFinalizeRecord(record, insufficientVideoData.Trim(), true);
            }

            EditMap editMap;
            DeepAnalysisService deepService = new DeepAnalysisService(ffmpegPath, this._toolPathResolver);
            try
            {
                bool explicitAudio = record.ExplicitTrackSelection != null;
                List<TrackInfo> selectedSource = PipelineTrackSelectionResolver.ResolveDisplayAudio(record, true);
                List<TrackInfo> languageInventory = this._trackMapper.FilterTracksByType(run.LangInfo.Tracks, "audio");
                List<TrackInfo> selectedLanguage = PipelineTrackSelectionResolver.ResolveDisplayAudio(record, false, languageInventory);
                int? sourceStream = selectedSource.Count > 0 ? record.SourceAudioTracks.FindIndex(track => track.Id == selectedSource[0].Id) : null;
                int? languageStream = selectedLanguage.Count > 0 ? languageInventory.FindIndex(track => track.Id == selectedLanguage[0].Id) : null;
                string sourceLanguage = selectedSource.Count > 0 ? LanguageValidator.NormalizeToIso6392(selectedSource[0].Language) : "";
                string langLanguage = selectedLanguage.Count > 0 ? LanguageValidator.NormalizeToIso6392(selectedLanguage[0].Language) : "";
                bool sharedLanguage = !string.IsNullOrEmpty(sourceLanguage) && sourceLanguage != "und" && sourceLanguage != "mul" && sourceLanguage != "zxx" &&
                    string.Equals(sourceLanguage, langLanguage, StringComparison.OrdinalIgnoreCase);
                editMap = deepService.Analyze(run.Media.Source.Path, run.Media.Lang.Path, deepManualStretchFactor, this._opts.AnalysisCropSourcePx, this._opts.AnalysisCropLanguagePx, run.CancellationToken,
                    explicitAudio, sourceStream, languageStream, sharedLanguage);
            }
            catch (OperationCanceledException)
            {
                record.DeepAnalysisResult = deepService.LastResult;
                record.DeepAnalysisTimeMs = deepService.LastResult != null ? deepService.LastResult.TotalElapsedMs : 0;
                this._diagnosticsWriter.WriteDeepAnalysisIfEnabled(record, this._opts);
                throw;
            }
            record.DeepAnalysisResult = deepService.LastResult;
            record.DeepAnalysisTimeMs = deepService.LastResult != null ? deepService.LastResult.TotalElapsedMs : 0;

            if (editMap == null)
            {
                record.DeepAnalysisMap = deepService.LastCandidateMap;
                record.DeepAnalysisApplied = false;
                AudioProcessingPlanner.ApplySourceFillGain(record.DeepAnalysisMap, this._opts, !string.IsNullOrEmpty(record.DeepAnalysisMap?.StretchFactor) ? record.DeepAnalysisMap.StretchFactor : record.StretchFactor);
                string deepRejectReason = record.DeepAnalysisResult != null && !string.IsNullOrEmpty(record.DeepAnalysisResult.RejectReason) ? record.DeepAnalysisResult.RejectReason : AppText.T("deep.temporal.pipeline.blocked");
                string deepFailure = AppText.F("deep.temporal.pipeline.failed", deepRejectReason);
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, deepFailure);
                ConsoleHelper.Progress(LogSection.Deep, 98, AppText.T("deep.temporal.pipeline.progressError"));
                this._diagnosticsWriter.WriteDeepAnalysisIfEnabled(record, this._opts);
                return this.FailAndFinalizeRecord(record, deepFailure.Trim(), true);
            }

            record.DeepAnalysisMap = editMap;
            record.DeepAnalysisApplied = true;
            run.SyncOffset = editMap.InitialDelayMs;

            if (!string.IsNullOrEmpty(editMap.StretchFactor))
            {
                record.StretchFactor = editMap.StretchFactor;
                record.SpeedCorrectionApplied = true;
            }
            AudioProcessingPlanner.ApplySourceFillGain(editMap, this._opts, record.StretchFactor);

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Success, AppText.F("deep.temporal.pipeline.completed", editMap.Operations.Count, editMap.InitialDelayMs, record.DeepAnalysisTimeMs));
            ConsoleHelper.Progress(LogSection.Deep, 90, AppText.T("deep.temporal.pipeline.progressDiagnostics"));
            this._diagnosticsWriter.WriteDeepAnalysisIfEnabled(record, this._opts);
            return false;
        }

        /// <summary>
        /// FrameSync: offset raffinato sui fotogrammi, accettato solo con abbastanza punti e confidenza
        /// </summary>
        /// <param name="run">Analisi in corso con i Matroska aperti; riceve l'offset accettato</param>
        /// <returns>True se il record è stato finalizzato in errore</returns>
        private bool RunFrameSync(AnalysisRun run)
        {
            FileProcessingRecord record = run.Record;
            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, AppText.T("framesync.pipeline.start"));
            ConsoleHelper.Progress(LogSection.FrameSync, 10, AppText.T("framesync.pipeline.progressSetup"));

            int frameSyncOffset = this._frameSyncService.RefineOffset(run.Media.Source.Path, run.Media.Lang.Path, run.SpeedCorrectionMode == Options.SPEED_CORRECTION_MANUAL ? this._opts.ManualStretchFactor : "");
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
                run.SyncOffset = frameSyncOffset;
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Success, AppText.F("framesync.pipeline.offset", Utils.FormatDelay(frameSyncOffset), this._frameSyncService.FrameSyncTimeMs));
                if (record.FrameSyncResult != null)
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, AppText.F("framesync.pipeline.confidence", record.FrameSyncResult.Confidence.ToString("P0", System.Globalization.CultureInfo.InvariantCulture)));
                    FrameSyncTimingInfo frameSyncTiming = record.FrameSyncResult.Timing;
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
                return this.FailAndFinalizeRecord(record, AppText.T("framesync.pipeline.failureReason"), true);
            }

            return false;
        }

        /// <summary>
        /// Imposta lo stato di errore del record, notifica l'aggiornamento e chiude il redirect del log
        /// </summary>
        /// <param name="record">Record da portare in errore</param>
        /// <param name="errorMessage">Messaggio da registrare nel record</param>
        /// <param name="buildMergePreview">Indica se ricostruire l'anteprima di merge prima di chiudere in errore</param>
        /// <returns>True per indicare che il flusso è stato finalizzato</returns>
        private bool FailAndFinalizeRecord(FileProcessingRecord record, string errorMessage, bool buildMergePreview)
        {
            if (record != null)
            {
                // Anche un record fallito deve restare modificabile a mano nell'editor EditMap, che ha bisogno delle
                // tracce lingua e del piano audio: lo stato va portato in errore prima, perche' l'anteprima si costruisce
                // solo su record analizzati o falliti, e il messaggio va scritto dopo, perche' il piano audio lo azzera
                record.Status = FileStatus.Error;

                if (buildMergePreview)
                {
                    this._buildMergeCommand(record);
                }

                record.ErrorMessage = errorMessage;
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

        /// <summary>
        /// Stato di un'analisi in corso, passato fra le fasi di AnalyzeFile
        /// </summary>
        private sealed class AnalysisRun
        {
            /// <summary>
            /// Prepara l'analisi di un record
            /// </summary>
            /// <param name="record">Record analizzato</param>
            /// <param name="speedCorrectionMode">Modalità di correzione della velocità, off se non configurata</param>
            /// <param name="cancellationToken">Token per interrompere le analisi lunghe</param>
            public AnalysisRun(FileProcessingRecord record, string speedCorrectionMode, CancellationToken cancellationToken)
            {
                this.Record = record;
                this.SpeedCorrectionMode = speedCorrectionMode;
                this.CancellationToken = cancellationToken;
            }

            /// <summary>
            /// Record analizzato
            /// </summary>
            public FileProcessingRecord Record { get; }

            /// <summary>
            /// Modalità di correzione della velocità
            /// </summary>
            public string SpeedCorrectionMode { get; }

            /// <summary>
            /// Token per interrompere le analisi lunghe
            /// </summary>
            public CancellationToken CancellationToken { get; }

            /// <summary>
            /// Metadata del sorgente, null se non leggibili
            /// </summary>
            public MkvFileInfo SourceInfo { get; set; }

            /// <summary>
            /// Metadata del file lingua, null senza merge o se non leggibili
            /// </summary>
            public MkvFileInfo LangInfo { get; set; }

            /// <summary>
            /// Temporizzazione video del sorgente, letta solo nel merge
            /// </summary>
            public VideoTimingInfo SourceTiming { get; set; }

            /// <summary>
            /// Coppia Matroska da cui le analisi leggono i fotogrammi, null se nessuna analisi legge il video
            /// </summary>
            public NormalizedMediaPair Media { get; set; }

            /// <summary>
            /// Offset di sincronizzazione trovato dall'analisi
            /// </summary>
            public int SyncOffset { get; set; }

            /// <summary>
            /// Vero quando la correzione manuale della velocità ha già fissato l'offset
            /// </summary>
            public bool SpeedCorrectionActive { get; set; }
        }

        #endregion
    }
}
