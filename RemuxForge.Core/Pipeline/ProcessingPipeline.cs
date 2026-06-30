using RemuxForge.Core.Analysis.FrameSync;
using RemuxForge.Core.Audio;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using RemuxForge.Core.Subtitles;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// Pipeline di elaborazione con fasi scan, analisi e merge
    /// </summary>
    public class ProcessingPipeline
    {
        #region Variabili di classe

        /// <summary>
        /// Opzioni correnti di configurazione
        /// </summary>
        private Options _opts;

        /// <summary>
        /// Servizio MKV tools per operazioni mkvmerge
        /// </summary>
        private MkvToolsService _mkvService;

        /// <summary>
        /// Servizio frame sync per sincronizzazione tramite confronto visivo
        /// </summary>
        private FrameSyncService _frameSyncService;

        /// <summary>
        /// Percorso risolto di ffmpeg
        /// </summary>
        private string _ffmpegPath;

        /// <summary>
        /// Resolver centralizzato per i binari esterni
        /// </summary>
        private ToolPathResolverService _toolPathResolver;

        /// <summary>
        /// Pattern codec risolti per filtro tracce lingua importate
        /// </summary>
        private string[] _codecPatterns;

        /// <summary>
        /// Pattern codec risolti per filtro tracce audio sorgente
        /// </summary>
        private string[] _sourceAudioCodecPatterns;

        /// <summary>
        /// Flag: filtrare tracce audio sorgente
        /// </summary>
        private bool _filterSourceAudio;

        /// <summary>
        /// Flag: filtrare tracce sottotitoli sorgente
        /// </summary>
        private bool _filterSourceSubs;

        /// <summary>
        /// Flag: fase merge attiva (aggiungere tracce da file lingua)
        /// </summary>
        private bool _needsMerge;

        /// <summary>
        /// Flag: fase filtro attiva (rimuovere tracce sorgente)
        /// </summary>
        private bool _needsFilter;

        /// <summary>
        /// Flag: fase remux attiva (merge o filtro o conversione audio)
        /// </summary>
        private bool _needsRemux;

        /// <summary>
        /// Flag: fase encoding video attiva
        /// </summary>
        private bool _needsEncode;

        /// <summary>
        /// Cache info file MKV per evitare letture ripetute
        /// </summary>
        private Dictionary<string, MkvFileInfo> _fileInfoCache;

        /// <summary>
        /// Mapper tracce/lingue pipeline
        /// </summary>
        private PipelineTrackMapper _trackMapper;

        /// <summary>
        /// Writer diagnostiche opzionali pipeline
        /// </summary>
        private PipelineDiagnosticsWriter _diagnosticsWriter;

        /// <summary>
        /// Gestore output, merge ed encoding
        /// </summary>
        private PipelineOutputManager _outputManager;

        /// <summary>
        /// Applicatore EditMap deep-analysis per tracce importate
        /// </summary>
        private PipelineDeepEditApplier _deepEditApplier;

        /// <summary>
        /// Builder preview comando merge
        /// </summary>
        private PipelineMergePreviewBuilder _mergePreviewBuilder;

        /// <summary>
        /// Builder richieste audio condiviso da preview e processing
        /// </summary>
        private PipelineAudioProcessingRequestBuilder _audioRequestBuilder;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public ProcessingPipeline()
        {
            this._opts = null;
            this._mkvService = null;
            this._frameSyncService = null;
            this._ffmpegPath = "";
            this._codecPatterns = null;
            this._sourceAudioCodecPatterns = null;
            this._filterSourceAudio = false;
            this._filterSourceSubs = false;
            this._needsMerge = false;
            this._needsFilter = false;
            this._needsRemux = false;
            this._needsEncode = false;
            this._fileInfoCache = new Dictionary<string, MkvFileInfo>();
            this._trackMapper = new PipelineTrackMapper();
            this._diagnosticsWriter = new PipelineDiagnosticsWriter();
            this._outputManager = new PipelineOutputManager();
            this._deepEditApplier = new PipelineDeepEditApplier();
            this._mergePreviewBuilder = new PipelineMergePreviewBuilder(this._trackMapper, this._outputManager);
            this._audioRequestBuilder = new PipelineAudioProcessingRequestBuilder();
            this._toolPathResolver = new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder);
        }

        #endregion

        #region Eventi

        /// <summary>
        /// Evento emesso per ogni messaggio di log durante elaborazione
        /// </summary>
        public event Action<LogSection, LogLevel, string> OnLogMessage;

        /// <summary>
        /// Evento emesso quando un record file viene aggiornato
        /// </summary>
        public event Action<FileProcessingRecord> OnFileUpdated;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Inizializza il pipeline con le opzioni fornite
        /// </summary>
        /// <param name="opts">Opzioni di configurazione</param>
        /// <returns>True se inizializzazione completata con successo</returns>
        public bool Initialize(Options opts)
        {
            bool success;
            OptionsValidationResult validation;
            MkvToolsService tempService;
            this._opts = opts;

            // Normalizza percorsi
            if (this._opts.SourceFolder.Length > 0)
            {
                this._opts.SourceFolder = this.NormalizePath(this._opts.SourceFolder);
            }
            if (this._opts.LanguageFolder.Length > 0)
            {
                this._opts.LanguageFolder = this.NormalizePath(this._opts.LanguageFolder);
            }
            if (this._opts.DestinationFolder.Length > 0)
            {
                this._opts.DestinationFolder = this.NormalizePath(this._opts.DestinationFolder);
            }

            // Determina modalita' operative
            this._needsMerge = (this._opts.TargetLanguage.Count > 0);
            this._needsFilter = (this._opts.KeepSourceAudioLangs.Count > 0 || this._opts.KeepSourceAudioCodec.Count > 0 || this._opts.KeepSourceSubtitleLangs.Count > 0);
            this._needsRemux = (this._needsMerge || this._needsFilter || this._opts.AudioFormat.Length > 0);
            this._needsEncode = (this._opts.EncodingProfileName.Length > 0);

            // Modalita' singola sorgente per merge
            if (this._needsMerge && this._opts.LanguageFolder.Length == 0 && this._opts.SourceFolder.Length > 0)
            {
                this._opts.LanguageFolder = this._opts.SourceFolder;
            }

            validation = OptionsValidator.Validate(this._opts, true, true);
            if (!validation.IsValid)
            {
                for (int i = 0; i < validation.Errors.Count; i++)
                {
                    this.Log(LogSection.Config, LogLevel.Error, "Errore: " + validation.Errors[i]);
                }
                for (int i = 0; i < validation.Warnings.Count; i++)
                {
                    this.Log(LogSection.Config, LogLevel.Info, validation.Warnings[i]);
                }
                return false;
            }

            success = true;
            if (success && !this._opts.Overwrite && this._opts.DestinationFolder.Length == 0 && this._needsEncode && !this._needsRemux)
            {
                this._opts.Overwrite = true;
                this.Log(LogSection.Config, LogLevel.Info, "Encode-only: sovrascrivi sorgente (overwrite implicito)");
            }

            if (success)
            {
                // Crea cartella destinazione se necessario
                if (!this._opts.Overwrite && !Directory.Exists(this._opts.DestinationFolder))
                {
                    this.Log(LogSection.Config, LogLevel.Info, "Creazione cartella destinazione: " + this._opts.DestinationFolder);
                    Directory.CreateDirectory(this._opts.DestinationFolder);
                }

                // Risolvi pattern codec
                this._codecPatterns = this.ResolveCodecPatterns(this._opts.AudioCodec);
                this._sourceAudioCodecPatterns = this.ResolveCodecPatterns(this._opts.KeepSourceAudioCodec);

                // Flag filtraggio tracce sorgente
                this._filterSourceAudio = (this._opts.KeepSourceAudioLangs.Count > 0 || this._opts.KeepSourceAudioCodec.Count > 0);
                this._filterSourceSubs = (this._opts.KeepSourceSubtitleLangs.Count > 0);

                // Risolvi mkvmerge se manca, e' relativo o il path salvato non e' un CLI valido
                if (this._opts.MkvMergePath.Length == 0 || this._opts.MkvMergePath == "mkvmerge" || !this._toolPathResolver.IsMkvMergeExecutablePath(this._opts.MkvMergePath))
                {
                    string resolvedMkvPath = this._toolPathResolver.ResolveMkvMergePath(true);
                    if (resolvedMkvPath.Length > 0)
                    {
                        this._opts.MkvMergePath = resolvedMkvPath;
                    }
                }

                // Verifica mkvmerge
                tempService = new MkvToolsService(this._opts.MkvMergePath);
                if (!tempService.VerifyMkvMerge())
                {
                    this.Log(LogSection.Config, LogLevel.Error, "mkvmerge non trovato. Installa MKVToolNix o specifica il percorso");
                    success = false;
                }
                else
                {
                    this._mkvService = tempService;
                    this.Log(LogSection.Config, LogLevel.Success, "Trovato mkvmerge: " + this._opts.MkvMergePath);

                    // Risolvi ffmpeg (tentato sempre per supportare speed correction automatica)
                    this._ffmpegPath = this._toolPathResolver.ResolveFfmpegPath(true, true, !this._opts.DryRun && this._opts.AudioDownsample24To16);
                    if (this._ffmpegPath.Length > 0)
                    {
                        this.Log(LogSection.Config, LogLevel.Success, "Trovato ffmpeg: " + this._ffmpegPath);
                        string ffmpegVersion = FfmpegProvider.ReadVersionLine(this._ffmpegPath);
                        if (ffmpegVersion.Length > 0)
                        {
                            this.Log(LogSection.Config, LogLevel.Debug, "  " + ffmpegVersion);
                        }
                    }
                    else if (this._opts.FrameSync || this._opts.DeepAnalysis || (!this._opts.DryRun && this._opts.AudioFormat.Length > 0) || this._opts.EncodingProfileName.Length > 0 || (!this._opts.DryRun && this._opts.AudioSourceFillThresholdMs > 0))
                    {
                        // ffmpeg richiesto per analisi sync, conversione audio, audio source fill o encoding video
                        string reason = this._opts.FrameSync ? "frame-sync" : (this._opts.DeepAnalysis ? "deep analysis" : (this._opts.AudioSourceFillThresholdMs > 0 ? "audio source fill" : (this._opts.EncodingProfileName.Length > 0 ? "encoding video" : "processing audio")));
                        this.Log(LogSection.Config, LogLevel.Error, "ffmpeg non trovato e impossibile scaricarlo. Necessario per " + reason);
                        success = false;
                    }

                    if (success && !this._opts.DryRun && this._opts.AudioDownsample24To16 && this._ffmpegPath.Length > 0 && !FfmpegProvider.SupportsLibSoxr(this._ffmpegPath))
                    {
                        this.Log(LogSection.Config, LogLevel.Error, "ffmpeg non supporta libsoxr: 24bit -> 16bit richiede una build con --enable-libsoxr");
                        success = false;
                    }

                    // Crea servizio frame-sync
                    if (success && this._opts.FrameSync && this._ffmpegPath.Length > 0)
                    {
                        this._frameSyncService = new FrameSyncService(this._ffmpegPath);
                        this._frameSyncService.SetAnalysisCrop(this._opts.AnalysisCropSourcePx, this._opts.AnalysisCropLanguagePx);
                    }

                    // Log impostazioni conversione se attiva
                    if (success && this._opts.AudioFormat.Length > 0)
                    {
                        this.Log(LogSection.Config, LogLevel.Phase, "Processing audio attivo: " + this._opts.AudioFormat.ToUpper() + " (" + this._opts.AudioProcessingScope + ")");
                        if (string.Equals(this._opts.AudioFormat, "flac", StringComparison.OrdinalIgnoreCase))
                        {
                            this.Log(LogSection.Config, LogLevel.Debug, "  FLAC compression level: " + AppSettingsService.Instance.Settings.Flac.CompressionLevel);
                        }
                        else if (string.Equals(this._opts.AudioFormat, "aac", StringComparison.OrdinalIgnoreCase))
                        {
                            this.Log(LogSection.Config, LogLevel.Debug, "  AAC bitrate: mono=" + AppSettingsService.Instance.Settings.Aac.Bitrate.Mono + "k, stereo=" + AppSettingsService.Instance.Settings.Aac.Bitrate.Stereo + "k, 5.1=" + AppSettingsService.Instance.Settings.Aac.Bitrate.Surround51 + "k, 7.1=" + AppSettingsService.Instance.Settings.Aac.Bitrate.Surround71 + "k");
                        }
                        else if (string.Equals(this._opts.AudioFormat, "opus", StringComparison.OrdinalIgnoreCase))
                        {
                            this.Log(LogSection.Config, LogLevel.Debug, "  Opus bitrate: mono=" + AppSettingsService.Instance.Settings.Opus.Bitrate.Mono + "k, stereo=" + AppSettingsService.Instance.Settings.Opus.Bitrate.Stereo + "k, 5.1=" + AppSettingsService.Instance.Settings.Opus.Bitrate.Surround51 + "k, 7.1=" + AppSettingsService.Instance.Settings.Opus.Bitrate.Surround71 + "k");
                        }
                    }

                    if (success && this._opts.AudioSourceFillThresholdMs > 0)
                    {
                        this.Log(LogSection.Config, LogLevel.Phase, "Audio source fill attivo: soglia " + this._opts.AudioSourceFillThresholdMs + "ms, sorgente " + this._opts.AudioSourceFillLanguage);
                    }

                    // Log profilo encoding video se attivo
                    if (success && this._opts.EncodingProfileName.Length > 0)
                    {
                        EncodingProfile encProfile = AppSettingsService.Instance.GetProfile(this._opts.EncodingProfileName);
                        if (encProfile != null)
                        {
                            this.Log(LogSection.Config, LogLevel.Phase, "Encoding video attivo: profilo '" + encProfile.Name + "' (" + encProfile.Codec + ")");
                        }
                        else
                        {
                            this.Log(LogSection.Config, LogLevel.Info, "Attenzione: profilo encoding '" + this._opts.EncodingProfileName + "' non trovato");
                        }
                    }

                    // Pulisci cache da inizializzazioni precedenti
                    this._fileInfoCache.Clear();
                }
            }

            return success;
        }

        /// <summary>
        /// Scansiona le cartelle e crea la lista di record
        /// </summary>
        /// <returns>Lista di record per i file trovati</returns>
        public List<FileProcessingRecord> ScanFiles()
        {
            ConsoleHelper.ResetFileLog();
            PipelineFileScanner scanner = new PipelineFileScanner(this.Log);
            return scanner.Scan(this._opts, this._needsMerge);
        }

        /// <summary>
        /// Analizza un singolo file: rilevamento velocita' e frame-sync
        /// </summary>
        /// <param name="record">Record del file da analizzare</param>
        public void AnalyzeFile(FileProcessingRecord record)
        {
            PipelineAnalysisCoordinator coordinator = new PipelineAnalysisCoordinator(this._opts, this._needsMerge, this._ffmpegPath, this._frameSyncService, this._trackMapper, this._diagnosticsWriter, this.GetCachedFileInfo, this.SetupLogRedirect, this.ClearLogRedirect, this.OnFileUpdated, this.BuildMergeCommand, this._toolPathResolver);
            coordinator.AnalyzeFile(record);
            this._ffmpegPath = coordinator.FfmpegPath;
        }

        /// <summary>
        /// Costruisce il comando mkvmerge e lo salva nel record
        /// </summary>
        /// <param name="record">Record del file</param>
        public void BuildMergeCommand(FileProcessingRecord record)
        {
            this._mergePreviewBuilder.Build(record, this._opts, this._mkvService, this.GetCachedFileInfo, this._needsMerge, this._needsRemux, this._filterSourceAudio, this._filterSourceSubs, this._codecPatterns, this._sourceAudioCodecPatterns, this._ffmpegPath);
        }

        /// <summary>
        /// Esegue il processing di un singolo file (remux e/o encoding)
        /// </summary>
        /// <param name="record">Record del file da elaborare</param>
        public void ProcessFile(FileProcessingRecord record)
        {
            bool done = false;
            bool started = false;
            string finalOutput = "";
            MkvFileInfo sourceInfo = null;
            List<TrackInfo> sourceTracks = null;
            int effectiveAudioDelay = 0;
            int effectiveSubDelay = 0;
            // Verifica stato
            if (record.Status != FileStatus.Analyzed)
            {
                done = true;
            }

            try
            {
                // Setup
                if (!done)
                {
                    started = true;
                    this.SetupLogRedirect(record);

                    // Aggiorna stato
                    record.Status = FileStatus.Processing;
                    if (this.OnFileUpdated != null)
                    {
                        this.OnFileUpdated(record);
                    }

                    // Ricalcola delay effettivi
                    effectiveAudioDelay = record.SyncOffsetMs + this._opts.AudioDelay + record.ManualAudioDelayMs;
                    effectiveSubDelay = record.SyncOffsetMs + this._opts.SubtitleDelay + record.ManualSubDelayMs;
                    record.AudioDelayApplied = effectiveAudioDelay;
                    record.SubDelayApplied = effectiveSubDelay;

                    // Ottieni info file sorgente
                    sourceInfo = this.GetCachedFileInfo(record.SourceFilePath);
                    sourceTracks = (sourceInfo != null) ? sourceInfo.Tracks : null;
                }

                // Fase remux (merge e/o filtro tracce)
                if (!done && this._needsRemux)
                {
                    finalOutput = this.ExecuteRemuxPhase(record, sourceInfo, effectiveAudioDelay, effectiveSubDelay);
                    if (record.Status == FileStatus.Error)
                    {
                        done = true;
                    }
                }

                // Fase encode-only (senza remux)
                if (!done && !this._needsRemux && this._needsEncode)
                {
                    finalOutput = this.ExecuteEncodeOnlyPhase(record, sourceTracks);
                }

                // Fase encoding video
                // Entra se: remux completato (Done) oppure encode-only (Processing), mai in dry-run
                if (!done && !this._opts.DryRun && (record.Status == FileStatus.Done || record.Status == FileStatus.Processing) && this._needsEncode && this._ffmpegPath.Length > 0)
                {
                    this._outputManager.RunEncodingAndRecord(record, finalOutput, this._opts, this._ffmpegPath, this.OnFileUpdated);
                }

            }
            finally
            {
                // Notifica e cleanup garantito anche in caso di errore
                if (started)
                {
                    if (this.OnFileUpdated != null)
                    {
                        this.OnFileUpdated(record);
                    }
                    this.ClearLogRedirect();
                }
            }
        }

        /// <summary>
        /// Wrapper retrocompatibile per ProcessFile
        /// </summary>
        /// <param name="record">Record del file da elaborare</param>
        public void MergeFile(FileProcessingRecord record)
        {
            this.ProcessFile(record);
        }

        /// <summary>
        /// Ricalcola i delay effettivi per un record
        /// </summary>
        /// <param name="record">Record da aggiornare</param>
        public void RecalculateDelays(FileProcessingRecord record)
        {
            record.AudioDelayApplied = record.SyncOffsetMs + this._opts.AudioDelay + record.ManualAudioDelayMs;
            record.SubDelayApplied = record.SyncOffsetMs + this._opts.SubtitleDelay + record.ManualSubDelayMs;
        }

        /// <summary>
        /// Genera la descrizione degli step della pipeline dalla configurazione
        /// </summary>
        /// <param name="opts">Opzioni di configurazione</param>
        /// <returns>Lista ordinata degli step della pipeline</returns>
        public static List<string> GetPipelineSteps(Options opts)
        {
            List<string> steps = new List<string>();

            if (opts.Mode == Options.MODE_SPLIT)
            {
                return GetSplitPipelineSteps(opts);
            }

            bool needsMerge = (opts.TargetLanguage.Count > 0);
            bool needsFilter = (opts.KeepSourceAudioLangs.Count > 0 || opts.KeepSourceAudioCodec.Count > 0 || opts.KeepSourceSubtitleLangs.Count > 0);
            bool needsConvert = (opts.AudioFormat.Length > 0);
            bool needsAudioSourceFill = opts.AudioSourceFillThresholdMs > 0;
            bool needsRemux = (needsMerge || needsFilter || needsConvert || needsAudioSourceFill);
            bool needsEncode = (opts.EncodingProfileName.Length > 0);

            steps.Add(AppText.T("web.pipeline.step.scanSource"));

            if (needsMerge)
            {
                steps.Add(AppText.T("web.pipeline.step.matchLanguage"));
            }

            if (needsMerge)
            {
                string analyzeDetail = AppText.T("web.pipeline.step.analysis");
                if (opts.DeepAnalysis) { analyzeDetail = AppText.T("web.pipeline.step.analysisDeep"); }
                else if (opts.FrameSync) { analyzeDetail = AppText.T("web.pipeline.step.analysisFrameSync"); }
                steps.Add(analyzeDetail);
            }

            if (needsConvert)
            {
                steps.Add(AppText.F("web.pipeline.step.processingAudio", opts.AudioFormat.ToUpperInvariant()));
            }

            if (needsAudioSourceFill)
            {
                steps.Add(AppText.T("web.pipeline.step.audioSourceFill"));
            }

            if (needsRemux)
            {
                string remuxDetail = AppText.T("web.pipeline.step.remux");
                if (needsMerge && needsFilter) { remuxDetail = AppText.T("web.pipeline.step.remuxMergeFilter"); }
                else if (needsMerge) { remuxDetail = AppText.T("web.pipeline.step.remuxMergeTracks"); }
                else if (needsFilter) { remuxDetail = AppText.T("web.pipeline.step.remuxFilterSource"); }
                steps.Add(remuxDetail);
            }

            if (needsEncode)
            {
                EncodingProfile profile = AppSettingsService.Instance.GetProfile(opts.EncodingProfileName);
                string encDetail;
                encDetail = profile != null ? AppText.F("web.pipeline.step.encodingVideoProfile", profile.Codec, profile.RateMode, profile.CrfQp) : AppText.F("web.pipeline.step.encodingVideoName", opts.EncodingProfileName);
                steps.Add(encDetail);
            }

            if (!needsRemux && !needsEncode)
            {
                steps.Add(AppText.T("web.pipeline.step.noOperation"));
            }

            return steps;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Genera gli step della pipeline split dalla configurazione
        /// </summary>
        /// <param name="opts">Opzioni di configurazione</param>
        /// <returns>Lista ordinata degli step split</returns>
        private static List<string> GetSplitPipelineSteps(Options opts)
        {
            List<string> steps = new List<string>();
            string sourceKind = AppText.T("web.pipeline.sourceKind.source");
            string modeDetail;

            if (opts.Split.SourcePath.Length > 0)
            {
                if (Directory.Exists(opts.Split.SourcePath)) { sourceKind = AppText.T("web.pipeline.sourceKind.sourceFolder"); }
                else if (File.Exists(opts.Split.SourcePath)) { sourceKind = AppText.T("web.pipeline.sourceKind.sourceFile"); }
            }

            steps.Add(AppText.F("web.pipeline.step.splitScanInput", sourceKind));
            steps.Add(AppText.T("web.pipeline.step.extractChapters"));
            steps.Add(opts.Split.SourceRaw.Length > 0
                ? AppText.T("web.pipeline.step.extractPtsRaw")
                : AppText.T("web.pipeline.step.extractPtsInput"));
            steps.Add(AppText.T("web.pipeline.step.countVerifyFrames"));

            if (opts.Split.Pattern.Length > 0) { modeDetail = AppText.T("web.pipeline.mode.chapterPattern"); }
            else if (opts.Split.Ranges.Length > 0) { modeDetail = AppText.T("web.pipeline.mode.explicitRanges"); }
            else if (opts.Split.SplitAt.Length > 0) { modeDetail = AppText.T("web.pipeline.mode.splitAt"); }
            else if (opts.Split.TrimStart.Length > 0 || opts.Split.TrimEnd.Length > 0) { modeDetail = AppText.T("web.pipeline.mode.trim"); }
            else if (opts.Split.ChaptersEach) { modeDetail = AppText.T("web.pipeline.mode.chaptersEach"); }
            else { modeDetail = AppText.T("web.pipeline.mode.notConfigured"); }
            steps.Add(AppText.F("web.pipeline.step.buildSegments", modeDetail));

            if (opts.Split.DryRun || opts.DryRun)
            {
                steps.Add(AppText.T("web.pipeline.step.dryRun"));
                return steps;
            }

            steps.Add(AppText.T("web.pipeline.step.readVideoCodec"));
            if (opts.Split.Snap != MkvSplitSnapMode.Off && opts.Split.SourceRaw.Length == 0)
            {
                steps.Add(AppText.F("web.pipeline.step.fastSnap", opts.Split.Snap.ToString().ToLowerInvariant()));
            }
            else
            {
                steps.Add(AppText.T("web.pipeline.step.slowPath"));
            }
            steps.Add(AppText.T("web.pipeline.step.remuxAudioSubsChapters"));

            return steps;
        }

        /// <summary>
        /// Popola le lingue risultato nel record
        /// </summary>
        /// <param name="record">Record elaborazione corrente</param>
        /// <param name="sourceTracks">Tracce file sorgente</param>
        /// <param name="sourceAudioIds">ID tracce audio sorgente da mantenere</param>
        /// <param name="audioTracks">Tracce audio importate</param>
        /// <param name="subtitleTracks">Tracce sottotitoli importate</param>
        private void PopulateResultLanguages(FileProcessingRecord record, List<TrackInfo> sourceTracks, List<int> sourceAudioIds, List<TrackInfo> audioTracks, List<TrackInfo> subtitleTracks)
        {
            this._trackMapper.PopulateResultLanguages(record, sourceTracks, sourceAudioIds, audioTracks, subtitleTracks, this._filterSourceAudio, this._filterSourceSubs, this._opts);
        }

        /// <summary>
        /// Garantisce che il merge mantenga anche le tracce source non renderizzate quando almeno una source viene processata
        /// </summary>
        /// <param name="sourceTracks">Tracce file sorgente</param>
        /// <param name="sourceAudioIds">ID audio sorgente da aggiornare</param>
        /// <param name="convertedSourceTracks">Tracce source processate come file separati</param>
        private void EnsureSourceAudioIdsForProcessedTracks(List<TrackInfo> sourceTracks, List<int> sourceAudioIds, Dictionary<int, string> convertedSourceTracks)
        {
            if (convertedSourceTracks == null || convertedSourceTracks.Count == 0)
            {
                return;
            }

            if (!this._filterSourceAudio && sourceTracks != null)
            {
                /*
                 * SOURCE AUDIO NON FILTRATO
                 */
                for (int i = 0; i < sourceTracks.Count; i++)
                {
                    if (string.Equals(sourceTracks[i].Type, "audio", StringComparison.OrdinalIgnoreCase) && !sourceAudioIds.Contains(sourceTracks[i].Id))
                    {
                        sourceAudioIds.Add(sourceTracks[i].Id);
                    }
                }
                return;
            }

            foreach (int sourceTrackId in convertedSourceTracks.Keys)
            {
                if (!sourceAudioIds.Contains(sourceTrackId))
                {
                    sourceAudioIds.Add(sourceTrackId);
                }
            }
        }

        /// <summary>
        /// Esegue la fase remux: filtro tracce, raccolta lingua, conversione, merge
        /// </summary>
        /// <param name="record">Record del file</param>
        /// <param name="sourceInfo">Info complete file sorgente (tracce, titolo container)</param>
        /// <param name="effectiveAudioDelay">Delay audio effettivo in ms</param>
        /// <param name="effectiveSubDelay">Delay sottotitoli effettivo in ms</param>
        /// <returns>Percorso output finale o stringa vuota se errore</returns>
        private string ExecuteRemuxPhase(FileProcessingRecord record, MkvFileInfo sourceInfo, int effectiveAudioDelay, int effectiveSubDelay)
        {
            List<TrackInfo> sourceTracks = (sourceInfo != null) ? sourceInfo.Tracks : null;
            string finalOutput = "";
            string tempOutput;
            List<int> sourceAudioIds = new List<int>();
            List<int> sourceSubIds = new List<int>();
            List<TrackInfo> audioTracks = new List<TrackInfo>();
            List<TrackInfo> subtitleTracks = new List<TrackInfo>();
            MkvFileInfo langInfo = null;
            List<TrackInfo> langTracks;
            Dictionary<int, string> convertedSourceTracks = new Dictionary<int, string>();
            Dictionary<int, string> convertedLangTracks = new Dictionary<int, string>();
            Dictionary<int, string> processedLangSubTracks = new Dictionary<int, string>();
            HashSet<int> audioDelayBypassedLangIds = new HashSet<int>();
            Dictionary<int, TrackInfo> processedSourceAudioInfo = new Dictionary<int, TrackInfo>();
            Dictionary<int, TrackInfo> processedLangAudioInfo = new Dictionary<int, TrackInfo>();
            string stretchFactor = record.StretchFactor;
            List<string> mergeArgs;
            string delayInfo;
            bool done = false;
            ConsoleHelper.Write(LogSection.Merge, LogLevel.Header, "Remux: " + record.SourceFileName);

            // Filtro tracce sorgente
            if (sourceTracks != null)
            {
                if (this._filterSourceAudio)
                {
                    sourceAudioIds = this._mkvService.GetSourceTrackIds(sourceTracks, "audio", this._opts.KeepSourceAudioLangs, this._sourceAudioCodecPatterns);
                }
                if (this._filterSourceSubs)
                {
                    sourceSubIds = this._mkvService.GetSourceTrackIds(sourceTracks, "subtitles", this._opts.KeepSourceSubtitleLangs, null);
                }
            }

            // Raccogli tracce dal file lingua (solo se merge attivo)
            if (this._needsMerge)
            {
                langInfo = this.GetCachedFileInfo(record.LangFilePath);
                langTracks = this.CollectLanguageTracks(record, out audioTracks, out subtitleTracks);

                if (langTracks == null)
                {
                    // Errore lettura tracce lingua
                    done = true;
                }
                else if (audioTracks.Count == 0 && subtitleTracks.Count == 0)
                {
                    // Nessuna traccia corrispondente
                    ConsoleHelper.Write(LogSection.Merge, LogLevel.Info, "  Nessuna traccia corrispondente trovata");
                    record.SkipReason = "No matching tracks";
                    record.ErrorMessage = "Nessuna traccia corrispondente";
                    record.Status = FileStatus.Error;
                    done = true;
                }
            }

            // Conversione, deep analysis e merge con garanzia cleanup file temporanei
            try
            {
                if (!done && (this._opts.AudioProcessingScope != "disabled" ||
                    this._opts.AudioSourceFillThresholdMs > 0 ||
                    (record.DeepAnalysisApplied && record.DeepAnalysisMap != null && record.DeepAnalysisMap.Operations.Count > 0 && !this._opts.SubOnly)))
                {
                    AudioProcessingRequest audioRequest = this._audioRequestBuilder.Build(record, this._opts, sourceInfo, langInfo, sourceTracks, sourceAudioIds, audioTracks, this._needsMerge, this._filterSourceAudio, effectiveAudioDelay);
                    if (this._opts.AudioFormat.Length == 0 && (audioRequest.SourceTracksToProcess.Count > 0 || audioRequest.LangTracksToProcess.Count > 0))
                    {
                        record.ErrorMessage = "Processing audio richiesto ma formato audio non impostato";
                        record.Status = FileStatus.Error;
                        done = true;
                    }
                    else if (this._opts.DryRun)
                    {
                        AudioProcessingPlan audioPlan = this.BuildAudioProcessingPlan(audioRequest, true);
                        AudioProcessingDryRunHelper.AddPlaceholders(audioPlan, this._opts, convertedSourceTracks, convertedLangTracks, processedSourceAudioInfo, processedLangAudioInfo, audioDelayBypassedLangIds);
                        if (audioDelayBypassedLangIds.Count > 0)
                        {
                            effectiveAudioDelay = 0;
                            record.AudioDelayApplied = effectiveAudioDelay;
                        }
                        if (convertedSourceTracks.Count > 0)
                        {
                            this.EnsureSourceAudioIdsForProcessedTracks(sourceTracks, sourceAudioIds, convertedSourceTracks);
                        }
                    }
                    else
                    {
                        audioRequest.Plan = this.BuildAudioProcessingPlan(audioRequest, true);
                        AudioProcessingService audioService = new AudioProcessingService(this._ffmpegPath, AppSettingsService.Instance.GetTempFolder(), this._mkvService);
                        AudioProcessingResult audioResult = audioService.Process(audioRequest);
                        if (audioResult.Success)
                        {
                            convertedSourceTracks = audioResult.SourceOutputFiles;
                            convertedLangTracks = audioResult.LangOutputFiles;
                            processedSourceAudioInfo = audioResult.SourceOutputInfo;
                            processedLangAudioInfo = audioResult.LangOutputInfo;
                            audioDelayBypassedLangIds = audioResult.AudioDelayBypassedLangIds;
                            effectiveAudioDelay = audioResult.EffectiveAudioDelayMs;
                            record.AudioDelayApplied = effectiveAudioDelay;

                            if (convertedSourceTracks.Count > 0)
                            {
                                this.EnsureSourceAudioIdsForProcessedTracks(sourceTracks, sourceAudioIds, convertedSourceTracks);
                            }
                        }
                        else
                        {
                            done = true;
                        }
                    }
                }

                // Deep analysis: applica EditMap solo ai sottotitoli. Audio e stretch restano gestiti da AudioProcessingService/mkvmerge.
                if (!done && this._deepEditApplier.ApplySubtitles(record, subtitleTracks, processedLangSubTracks, this._opts, this._ffmpegPath))
                {
                }
                else if (!done && record.Status == FileStatus.Error)
                {
                    done = true;
                }

                // Subtitle canvas rewrite: lavora dopo il timeline edit per rispettare eventuali cut/insert gia' applicati.
                if (!done && this._opts.SubtitleCanvasRewrite)
                {
                    SubtitleCanvasRewriteService subtitleCanvasService = new SubtitleCanvasRewriteService(this._toolPathResolver);
                    subtitleCanvasService.ProcessImportedSubtitles(
                        record,
                        subtitleTracks,
                        processedLangSubTracks,
                        this._opts,
                        this._ffmpegPath,
                        AppSettingsService.Instance.GetTempFolder());
                }

                // Costruzione e esecuzione merge
                if (!done)
                {
                    this._outputManager.PrepareOutputPaths(record.SourceFilePath, this._opts, out tempOutput, out finalOutput);

                    MergeRequest mergeReq = new MergeRequest();
                    mergeReq.SourceFile = record.SourceFilePath;
                    mergeReq.LanguageFile = this._needsMerge ? record.LangFilePath : "";
                    mergeReq.OutputFile = tempOutput;
                    mergeReq.SourceAudioIds = sourceAudioIds;
                    if (sourceAudioIds.Count > 0)
                    {
                        mergeReq.SourceAudioTracks = this._trackMapper.FilterTracksByIds(sourceTracks, sourceAudioIds);
                    }
                    mergeReq.SourceSubIds = sourceSubIds;
                    mergeReq.LangAudioTracks = audioTracks;
                    mergeReq.LangSubTracks = subtitleTracks;
                    mergeReq.AudioDelayMs = effectiveAudioDelay;
                    mergeReq.SubDelayMs = effectiveSubDelay;
                    mergeReq.FilterSourceAudio = this._filterSourceAudio || convertedSourceTracks.Count > 0;
                    mergeReq.FilterSourceSubs = this._filterSourceSubs;
                    mergeReq.StretchFactor = stretchFactor;
                    mergeReq.AudioFormat = this._opts.AudioFormat;
                    mergeReq.SourceTitle = (sourceInfo != null) ? sourceInfo.ContainerTitle : "";
                    mergeReq.ConvertedSourceTracks = convertedSourceTracks;
                    mergeReq.ConvertedLangTracks = convertedLangTracks;
                    mergeReq.AudioDelayBypassedLangIds = audioDelayBypassedLangIds;
                    mergeReq.ProcessedSourceAudioInfo = processedSourceAudioInfo;
                    mergeReq.ProcessedLangAudioInfo = processedLangAudioInfo;
                    mergeReq.ProcessedLangSubTracks = processedLangSubTracks;
                    mergeArgs = this._mkvService.BuildMergeArguments(mergeReq);

                    // Aggiorna comando nel record dai mergeArgs effettivi
                    record.MergeCommand = this._mkvService.FormatMergeCommand(mergeArgs);
                    record.ResultFileName = Path.GetFileName(finalOutput);
                    record.ResultFilePath = finalOutput;

                    // Popola dettaglio tracce nel record per display
                    record.KeptSourceAudioIds = sourceAudioIds;
                    record.KeptSourceSubIds = sourceSubIds;
                    record.ImportedAudioTracks = audioTracks;
                    record.ImportedSubTracks = subtitleTracks;
                    record.DisplayAudioFormat = this._opts.AudioFormat;

                    // Calcola lingue risultato
                    this.PopulateResultLanguages(record, sourceTracks, sourceAudioIds, audioTracks, subtitleTracks);

                    // Log info
                    ConsoleHelper.Write(LogSection.Merge, LogLevel.Debug, "  Output: " + finalOutput);
                    if (this._needsMerge)
                    {
                        delayInfo = "  Delay: Audio " + Utils.FormatDelay(effectiveAudioDelay) + ", Sub " + Utils.FormatDelay(effectiveSubDelay);
                        if (stretchFactor.Length > 0) { delayInfo += ", stretch: " + stretchFactor; }
                        ConsoleHelper.Write(LogSection.Merge, LogLevel.Debug, delayInfo);
                    }

                    // Esegui merge e registra risultato
                    this._outputManager.RunMergeAndRecord(record, mergeArgs, tempOutput, finalOutput, this._opts, this._mkvService);

                }
            }
            finally
            {
                // Cleanup file convertiti temporanei
                foreach (KeyValuePair<int, string> kvp in convertedSourceTracks) { FileHelper.DeleteTempFile(kvp.Value); }
                foreach (KeyValuePair<int, string> kvp in convertedLangTracks) { FileHelper.DeleteTempFile(kvp.Value); }
                foreach (KeyValuePair<int, string> kvp in processedLangSubTracks) { this.DeleteProcessedSubtitleFile(kvp.Value); }
            }

            return finalOutput;
        }

        /// <summary>
        /// Esegue la fase encode-only: prepara file e stato per encoding
        /// </summary>
        /// <param name="record">Record del file</param>
        /// <param name="sourceTracks">Tracce file sorgente</param>
        /// <returns>Percorso output finale</returns>
        private string ExecuteEncodeOnlyPhase(FileProcessingRecord record, List<TrackInfo> sourceTracks)
        {
            string finalOutput;
            ConsoleHelper.Write(LogSection.Encode, LogLevel.Header, "Encode: " + record.SourceFileName);

            this._outputManager.PrepareOutputPaths(record.SourceFilePath, this._opts, out _, out finalOutput);

            // Per encode-only non-overwrite, copia file in destinazione
            if (!this._opts.Overwrite)
            {
                ConsoleHelper.Write(LogSection.Encode, LogLevel.Debug, "  Copia in destinazione...");
                File.Copy(record.SourceFilePath, finalOutput, true);
            }

            record.ResultFileName = Path.GetFileName(finalOutput);

            // Popola lingue risultato (stesso file sorgente, nessuna traccia importata)
            this.PopulateResultLanguages(record, sourceTracks, new List<int>(), new List<TrackInfo>(), new List<TrackInfo>());

            if (this._opts.DryRun)
            {
                ConsoleHelper.Write(LogSection.Encode, LogLevel.Phase, "  [DRY-RUN] Encoding: " + record.SourceFileName);
                record.Success = true;
                record.Status = FileStatus.Done;
            }
            else
            {
                record.Status = FileStatus.Processing;
            }

            return finalOutput;
        }

        /// <summary>
        /// Calcola e salva sul record il piano audio per preview e dry-run
        /// </summary>
        /// <param name="request">Richiesta audio corrente</param>
        /// <param name="probeMissingDurations">True per usare ffmpeg quando mancano durate metadata</param>
        /// <returns>Piano audio calcolato</returns>
        private AudioProcessingPlan BuildAudioProcessingPlan(AudioProcessingRequest request, bool probeMissingDurations)
        {
            AudioProcessingPlanner planner = new AudioProcessingPlanner(this._mkvService, this._ffmpegPath);
            AudioProcessingPlan result = planner.BuildPlan(request, probeMissingDurations);
            request.Plan = result;
            request.Record.AudioProcessingPreview = result;
            return result;
        }

        /// <summary>
        /// Raccoglie tracce audio e sottotitoli dal file lingua
        /// </summary>
        /// <param name="record">Record del file</param>
        /// <param name="audioTracks">Tracce audio trovate (output)</param>
        /// <param name="subtitleTracks">Tracce sottotitoli trovate (output)</param>
        /// <returns>Lista tracce lingua o null se errore lettura</returns>
        private List<TrackInfo> CollectLanguageTracks(FileProcessingRecord record, out List<TrackInfo> audioTracks, out List<TrackInfo> subtitleTracks)
        {
            MkvFileInfo langInfo;
            List<TrackInfo> langTracks;
            langInfo = this.GetCachedFileInfo(record.LangFilePath);
            langTracks = (langInfo != null) ? langInfo.Tracks : null;
            return this._trackMapper.CollectLanguageTracks(record, langTracks, this._mkvService, this._opts, this._codecPatterns, out audioTracks, out subtitleTracks);
        }

        /// <summary>
        /// Invia un messaggio di log tramite l'evento OnLogMessage
        /// </summary>
        /// <param name="section">Sezione operativa del messaggio</param>
        /// <param name="level">Livello di severita'</param>
        /// <param name="text">Testo del messaggio</param>
        private void Log(LogSection section, LogLevel level, string text)
        {
            if (this.OnLogMessage != null)
            {
                this.OnLogMessage(section, level, text);
            }
        }

        /// <summary>
        /// Cancella un sottotitolo processato e gli eventuali sidecar muxabili
        /// </summary>
        /// <param name="filePath">Percorso file principale</param>
        private void DeleteProcessedSubtitleFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            FileHelper.DeleteTempFile(filePath);
            if (string.Equals(Path.GetExtension(filePath), ".idx", StringComparison.OrdinalIgnoreCase))
            {
                FileHelper.DeleteTempFile(Path.ChangeExtension(filePath, ".sub"));
            }
        }

        /// <summary>
        /// Normalizza un percorso risolvendolo alla forma assoluta
        /// </summary>
        /// <param name="path">Percorso da normalizzare</param>
        /// <returns>Percorso assoluto normalizzato</returns>
        private string NormalizePath(string path)
        {
            string result = path;

            if (path.Length > 0)
            {
                result = Path.GetFullPath(path);
                result = result.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return result;
        }

        /// <summary>
        /// Imposta il redirect log di ConsoleHelper verso il record e l'evento
        /// </summary>
        /// <param name="record">Record in cui salvare i log</param>
        private void SetupLogRedirect(FileProcessingRecord record)
        {
            bool inCallback = false;
            ConsoleHelper.SetLogCallback((section, level, text) =>
            {
                // Guard contro ri-entranza (OnLogMessage potrebbe chiamare Write)
                if (inCallback)
                {
                    // Evita fallback console nella WebUI: il messaggio rientrante viene scartato
                    return;
                }
                inCallback = true;

                if (level != LogLevel.Debug)
                {
                    // Salva nel log del record con prefisso sezione
                    record.AnalysisLog.Add(ConsoleHelper.FormatSectionPrefix(section) + text);
                    // Invia all'evento per UI (CLI/WebUI)
                    if (this.OnLogMessage != null)
                    {
                        this.OnLogMessage(section, level, text);
                    }
                }

                inCallback = false;
            });
        }

        /// <summary>
        /// Rimuove il redirect log di ConsoleHelper
        /// </summary>
        private void ClearLogRedirect()
        {
            ConsoleHelper.ClearLogCallback();
        }

        /// <summary>
        /// Risolve i pattern codec da una lista di nomi codec
        /// </summary>
        /// <param name="codecNames">Lista nomi codec</param>
        /// <returns>Array di pattern risolti o null</returns>
        private string[] ResolveCodecPatterns(List<string> codecNames)
        {
            string[] result = null;

            if (codecNames.Count > 0)
            {
                List<string> allPatterns = new List<string>();
                for (int c = 0; c < codecNames.Count; c++)
                {
                    string[] patterns = CodecMapping.GetCodecPatterns(codecNames[c]);
                    if (patterns != null)
                    {
                        for (int p = 0; p < patterns.Length; p++)
                        {
                            if (!allPatterns.Contains(patterns[p]))
                            {
                                allPatterns.Add(patterns[p]);
                            }
                        }
                    }
                }
                if (allPatterns.Count > 0)
                {
                    result = allPatterns.ToArray();
                }
            }

            return result;
        }

        /// <summary>
        /// Ottieni MkvFileInfo da cache o tramite mkvmerge
        /// </summary>
        /// <param name="filePath">Percorso file MKV</param>
        /// <returns>Informazioni file o null se errore</returns>
        private MkvFileInfo GetCachedFileInfo(string filePath)
        {
            MkvFileInfo info;
            if (this._fileInfoCache.ContainsKey(filePath))
            {
                info = this._fileInfoCache[filePath];
            }
            else
            {
                info = this._mkvService.GetFileInfo(filePath);
                if (info != null)
                {
                    this._fileInfoCache[filePath] = info;
                }
            }

            return info;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Pattern codec risolti per filtro tracce lingua importate
        /// </summary>
        public string[] CodecPatterns { get { return this._codecPatterns; } }

        #endregion
    }
}
