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
using System.Linq;
using System.Text.Json;

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
        /// Lock della cache: la WebUI puo' ricostruire un comando di merge mentre un'analisi e' in corso su un altro thread
        /// </summary>
        private object _fileInfoCacheLock = new object();

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

        private bool _isolatedPreparation;
        private System.Threading.CancellationToken _preparationCancellation;

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

        /// <summary>Media pronti per preparazioni parallele, prima dell'analisi temporale</summary>
        public event Action<FileProcessingRecord, List<TrackInfo>, System.Threading.CancellationToken> OnAnalysisMediaReady;

        #endregion

        #region Metodi pubblici

        /// <summary>Opt-in per una pipeline candidata: probe e log locali anche nelle dipendenze init/render.
        /// Disporre prima di collegare la pipeline al lavoro runtime.</summary>
        public IDisposable BeginIsolatedPreparation(Action<LogSection, LogLevel, string> log, System.Threading.CancellationToken cancellationToken = default)
        {
            return new PreparationScope(this, log, cancellationToken);
        }

        private sealed class PreparationScope : IDisposable
        {
            private readonly ProcessingPipeline _owner;
            private readonly bool _previousMode;
            private readonly System.Threading.CancellationToken _previousCancellation;
            private readonly PipelinePreparationContext _context;
            public PreparationScope(ProcessingPipeline owner, Action<LogSection, LogLevel, string> log, System.Threading.CancellationToken cancellation)
            {
                this._owner = owner;
                this._previousMode = owner._isolatedPreparation;
                this._previousCancellation = owner._preparationCancellation;
                owner._isolatedPreparation = true;
                owner._preparationCancellation = cancellation;
                this._context = new PipelinePreparationContext(log, cancellation);
            }
            public void Dispose()
            {
                this._context.Dispose();
                this._owner._isolatedPreparation = this._previousMode;
                this._owner._preparationCancellation = this._previousCancellation;
            }
        }

        /// <summary>Reset degli effetti scan soltanto dopo il commit, mai durante la preparazione candidata.</summary>
        public void ResetScanArtifactsAfterCommit()
        {
            if (this._isolatedPreparation) throw new InvalidOperationException(AppText.T("remuxConfiguration.preparationResetForbidden"));
            ConsoleHelper.ResetFileLog();
            this._diagnosticsWriter.ClearDeepAnalysisDiagnostics();
        }

        /// <summary>Confronta pairing e inventario concreto con lo snapshot prima del commit.
        /// I probe condividono la cache privata della pipeline candidata e propagano gli errori locali.</summary>
        public void ValidatePreparedSnapshot(RemuxPreviewSnapshot snapshot, IEnumerable<FileProcessingRecord> records)
        {
            List<FileProcessingRecord> matched = records.Where(record => !string.IsNullOrEmpty(record.LangFilePath)).ToList();
            HashSet<string> keys = new HashSet<string>(matched.Select(record => RemuxPairTrackSelection.CreatePairKey(record.SourceFilePath, record.LangFilePath)), StringComparer.Ordinal);
            if (snapshot == null || !snapshot.InventoryComplete || matched.Count != snapshot.MatchedPairs ||
                !keys.SetEquals(snapshot.Pairs.Where(pair => pair.IsMatched).Select(pair => pair.PairKey)))
                throw new InvalidOperationException(AppText.T("remuxConfiguration.matchingChanged"));
            foreach (FileProcessingRecord record in matched)
            {
                this._preparationCancellation.ThrowIfCancellationRequested();
                string pairKey = RemuxPairTrackSelection.CreatePairKey(record.SourceFilePath, record.LangFilePath);
                MkvFileInfo source = this.GetCachedFileInfo(record.SourceFilePath);
                MkvFileInfo lang = this.GetCachedFileInfo(record.LangFilePath);
                foreach (RemuxTrackSide side in new[] { RemuxTrackSide.Source, RemuxTrackSide.Lang })
                {
                    MkvFileInfo info = side == RemuxTrackSide.Source ? source : lang;
                    string[] actual = info.Tracks.Where(track => string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(track.Type, "subtitles", StringComparison.OrdinalIgnoreCase))
                        .Select(track => track.Id + ":" + RemuxConfigurationPreviewService.CreateGroupKey(side, track)).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                    string[] expected = snapshot.Groups.Where(group => group.Side == side).SelectMany(group =>
                        group.Members.Where(member => member.PairKey == pairKey).Select(member => member.Track.Id + ":" + group.Key))
                        .OrderBy(value => value, StringComparer.Ordinal).ToArray();
                    if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
                        throw new InvalidOperationException(AppText.F("remuxConfiguration.inventoryChanged", record.SourceFileName));
                }
                PipelineTrackSelectionResolver.Resolve(record, this._opts, source.Tracks, lang.Tracks, this._mkvService, this._codecPatterns, this._sourceAudioCodecPatterns);
            }
        }

        /// <summary>
        /// Inizializza il pipeline con le opzioni fornite
        /// </summary>
        /// <param name="opts">Opzioni di configurazione</param>
        /// <returns>True se inizializzazione completata con successo</returns>
        public bool Initialize(Options opts)
        {
            PipelineInitializationResult result = this.InitializeDetailed(opts);
            foreach (PipelineInitializationLog entry in result.Log)
                this.Log(entry.Section, entry.Level, entry.Message);
            return result.Success;
        }

        /// <summary>Prepara su candidato detached e sostituisce lo stato attivo soltanto dopo tutti i controlli.</summary>
        public PipelineInitializationResult InitializeDetailed(Options opts)
        {
            PipelineInitializationResult result = new PipelineInitializationResult();
            if (opts == null)
            {
                result.AddError(new PipelineInitializationIssue("validation.invalidConfig", AppText.T("validation.invalidConfig"), "", "Configuration"));
                result.AddLog(LogSection.Config, LogLevel.Error, AppText.F("cli.error", AppText.T("validation.invalidConfig")));
                return result;
            }
            try
            {
                Options detached = CloneInitializationOptions(opts);
                ProcessingPipeline candidate = new ProcessingPipeline();
                candidate._preparationCancellation = this._preparationCancellation;
                candidate.OnLogMessage += result.AddLog;
                if (!candidate.InitializeCandidate(detached, result)) return result;
                // Anche il risultato è detached: il chiamante non ottiene un riferimento alle opzioni della pipeline.
                result.CurrentField = "";
                result.CurrentSection = "Configuration";
                Options appliedOptions = CloneInitializationOptions(candidate._opts);
                lock (this._fileInfoCacheLock)
                {
                    this._opts = candidate._opts;
                    this._mkvService = candidate._mkvService;
                    this._frameSyncService = candidate._frameSyncService;
                    this._ffmpegPath = candidate._ffmpegPath;
                    this._toolPathResolver = candidate._toolPathResolver;
                    this._codecPatterns = candidate._codecPatterns;
                    this._sourceAudioCodecPatterns = candidate._sourceAudioCodecPatterns;
                    this._filterSourceAudio = candidate._filterSourceAudio;
                    this._filterSourceSubs = candidate._filterSourceSubs;
                    this._needsMerge = candidate._needsMerge;
                    this._needsFilter = candidate._needsFilter;
                    this._needsRemux = candidate._needsRemux;
                    this._needsEncode = candidate._needsEncode;
                    this._fileInfoCache = candidate._fileInfoCache;
                }
                result.AppliedOptions = appliedOptions;
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.AddError(new PipelineInitializationIssue("initialization.exception", ex.Message,
                    result.CurrentField, result.CurrentSection, true));
                result.AddLog(LogSection.Config, LogLevel.Error, ex.Message);
            }
            return result;
        }

        private static Options CloneInitializationOptions(Options options)
        {
            // Non valutare getter derivati (PairKey/HasLangTracks) prima della validazione dell'input.
            JsonSerializerOptions serializerOptions = new JsonSerializerOptions { IgnoreReadOnlyProperties = true };
            return JsonSerializer.Deserialize<Options>(JsonSerializer.SerializeToUtf8Bytes(options, serializerOptions), serializerOptions);
        }

        private bool InitializeCandidate(Options opts, PipelineInitializationResult result)
        {
            bool success;
            OptionsValidationResult validation;
            MkvToolsService tempService;
            this._opts = opts;

            // Normalizza percorsi
            if (!string.IsNullOrEmpty(this._opts.SourceFolder))
            {
                result.CurrentField = nameof(Options.SourceFolder);
                result.CurrentSection = "Files";
                this._opts.SourceFolder = this.NormalizePath(this._opts.SourceFolder);
            }
            if (!string.IsNullOrEmpty(this._opts.LanguageFolder))
            {
                result.CurrentField = nameof(Options.LanguageFolder);
                result.CurrentSection = "Files";
                this._opts.LanguageFolder = this.NormalizePath(this._opts.LanguageFolder);
            }
            if (!string.IsNullOrEmpty(this._opts.DestinationFolder))
            {
                result.CurrentField = nameof(Options.DestinationFolder);
                result.CurrentSection = "Output";
                this._opts.DestinationFolder = this.NormalizePath(this._opts.DestinationFolder);
            }

            // Determina modalità operative
            result.CurrentField = "";
            result.CurrentSection = "Configuration";
            this._needsMerge = this._opts.ExplicitTrackSelection != null || (this._opts.TargetLanguage?.Count > 0);
            this._needsFilter = (this._opts.KeepSourceAudioLangs?.Count > 0 || this._opts.KeepSourceAudioCodec?.Count > 0 || this._opts.KeepSourceSubtitleLangs?.Count > 0);
            this._needsRemux = (this._needsMerge || this._needsFilter || !string.IsNullOrEmpty(this._opts.AudioFormat));
            this._needsEncode = !string.IsNullOrEmpty(this._opts.EncodingProfileName);

            // Modalità singola sorgente per merge
            if (this._needsMerge && string.IsNullOrEmpty(this._opts.LanguageFolder) && Directory.Exists(this._opts.SourceFolder))
            {
                this._opts.LanguageFolder = this._opts.SourceFolder;
            }

            // Nel wizard il requisito timeline viene verificato dopo il probe autorevole,
            // non dedotto dalle intenzioni dei filtri né da un booleano fornito dal Web.
            validation = OptionsValidator.Validate(this._opts, true, true,
                this._opts.SkipPairsWithoutSelectedLangTracks ? false : null);
            foreach (PipelineInitializationIssue issue in validation.ErrorDetails) result.AddError(issue);
            foreach (PipelineInitializationIssue issue in validation.WarningDetails) result.AddWarning(issue);
            if (!validation.IsValid)
            {
                for (int i = 0; i < validation.Errors.Count; i++)
                {
                    this.Log(LogSection.Config, LogLevel.Error, AppText.F("cli.error", validation.Errors[i]));
                }
                for (int i = 0; i < validation.Warnings.Count; i++)
                {
                    this.Log(LogSection.Config, LogLevel.Info, validation.Warnings[i]);
                }
                return false;
            }

            success = true;
            if (success && !this._opts.Overwrite && string.IsNullOrEmpty(this._opts.DestinationFolder) && this._needsEncode && !this._needsRemux)
            {
                this._opts.Overwrite = true;
                this.Log(LogSection.Config, LogLevel.Info, "Encode-only: sovrascrivi sorgente (overwrite implicito)");
            }

            if (success)
            {
                // Risolvi pattern codec
                result.CurrentField = nameof(Options.AudioCodec);
                result.CurrentSection = "Tracks";
                this._codecPatterns = this.ResolveCodecPatterns(this._opts.AudioCodec);
                result.CurrentField = nameof(Options.KeepSourceAudioCodec);
                this._sourceAudioCodecPatterns = this.ResolveCodecPatterns(this._opts.KeepSourceAudioCodec);

                // Flag filtraggio tracce sorgente
                this._filterSourceAudio = this._opts.ExplicitTrackSelection != null || (this._opts.KeepSourceAudioLangs.Count > 0 || this._opts.KeepSourceAudioCodec.Count > 0);
                this._filterSourceSubs = this._opts.ExplicitTrackSelection != null || (this._opts.KeepSourceSubtitleLangs.Count > 0);

                // Risolvi mkvmerge se manca, è relativo o il path salvato non è un CLI valido
                result.CurrentField = nameof(Options.MkvMergePath);
                result.CurrentSection = "Tools";
                if (string.IsNullOrEmpty(this._opts.MkvMergePath) || this._opts.MkvMergePath == "mkvmerge" || !this._toolPathResolver.IsMkvMergeExecutablePath(this._opts.MkvMergePath))
                {
                    string resolvedMkvPath = this._toolPathResolver.ResolveMkvMergePath(true);
                    if (!string.IsNullOrEmpty(resolvedMkvPath))
                    {
                        this._opts.MkvMergePath = resolvedMkvPath;
                    }
                }

                // Verifica mkvmerge
                tempService = new MkvToolsService(this._opts.MkvMergePath);
                if (!tempService.VerifyMkvMerge())
                {
                    this.Log(LogSection.Config, LogLevel.Error, AppText.T("remuxConfiguration.mkvmergeUnavailable"));
                    result.AddError(new PipelineInitializationIssue("tools.mkvmergeUnavailable", AppText.T("remuxConfiguration.mkvmergeUnavailable"), nameof(Options.MkvMergePath), "Tools", true));
                    success = false;
                }
                else
                {
                    this._mkvService = tempService;
                    this.Log(LogSection.Config, LogLevel.Success, "Trovato mkvmerge: " + this._opts.MkvMergePath);

                    if (this._opts.ExplicitTrackSelection != null && this._opts.AudioSourceFillThresholdMs > 0)
                    {
                        result.CurrentField = nameof(Options.AudioSourceFillLanguage);
                        result.CurrentSection = "Processing";
                        OptionsValidationResult fillValidation = new OptionsValidationResult();
                        using (this.BeginIsolatedPreparation(this.Log, this._preparationCancellation))
                            OptionsValidator.ValidateExplicitSourceFillSelection(this._opts, fillValidation, pair =>
                            {
                                this._preparationCancellation.ThrowIfCancellationRequested();
                                return this.GetCachedFileInfo(pair.SourceFilePath)?.Tracks;
                            });
                        foreach (PipelineInitializationIssue issue in fillValidation.ErrorDetails) result.AddError(issue);
                        foreach (string error in fillValidation.Errors)
                            this.Log(LogSection.Config, LogLevel.Error, AppText.F("cli.error", error));
                        if (!fillValidation.IsValid) return false;
                    }

                    if (this._opts.SkipPairsWithoutSelectedLangTracks && this._needsMerge &&
                        (this._opts.DeepAnalysis || this._opts.SpeedCorrectionMode != Options.SPEED_CORRECTION_OFF))
                    {
                        result.CurrentField = nameof(Options.ExplicitTrackSelection);
                        result.CurrentSection = "Tracks";
                        bool hasSelectedLangAudio = this.HasSelectedLanguageAudio();
                        OptionsValidationResult timelineValidation = new OptionsValidationResult();
                        OptionsValidator.ValidateTimelineAudioProcessing(this._opts, this._needsMerge, timelineValidation, hasSelectedLangAudio);
                        foreach (PipelineInitializationIssue issue in timelineValidation.ErrorDetails) result.AddError(issue);
                        foreach (string error in timelineValidation.Errors)
                            this.Log(LogSection.Config, LogLevel.Error, AppText.F("cli.error", error));
                        if (!timelineValidation.IsValid) return false;
                    }

                    bool manualSpeedCorrection = string.Equals(this._opts.SpeedCorrectionMode, Options.SPEED_CORRECTION_MANUAL, StringComparison.OrdinalIgnoreCase);
                    bool requiresFfmpeg = this._opts.FrameSync || this._opts.DeepAnalysis || manualSpeedCorrection ||
                        (!this._opts.DryRun && !string.IsNullOrEmpty(this._opts.AudioFormat)) ||
                        !string.IsNullOrEmpty(this._opts.EncodingProfileName) ||
                        (!this._opts.DryRun && this._opts.AudioSourceFillThresholdMs > 0);
                    // Risolvi ffmpeg soltanto per le funzionalità che lo utilizzano
                    result.CurrentField = "FfmpegPath";
                    this._ffmpegPath = requiresFfmpeg ? this._toolPathResolver.ResolveFfmpegPath(true, true, !this._opts.DryRun && this._opts.AudioDownsample24To16) : "";
                    if (!string.IsNullOrEmpty(this._ffmpegPath))
                    {
                        this.Log(LogSection.Config, LogLevel.Success, "Trovato ffmpeg: " + this._ffmpegPath);
                        string ffmpegVersion = FfmpegProvider.ReadVersionLine(this._ffmpegPath);
                        if (!string.IsNullOrEmpty(ffmpegVersion))
                        {
                            this.Log(LogSection.Config, LogLevel.Debug, "  " + ffmpegVersion);
                        }
                    }
                    else if (requiresFfmpeg)
                    {
                        // ffmpeg richiesto per analisi sync, conversione audio, audio source fill o encoding video
                        string reason = this._opts.FrameSync ? "FrameSync" : (this._opts.DeepAnalysis ? "Deep Analysis" : (manualSpeedCorrection ? AppText.T("remuxConfiguration.reason.manualSpeed") : (this._opts.AudioSourceFillThresholdMs > 0 ? AppText.T("remuxConfiguration.reason.audioFill") : (!string.IsNullOrEmpty(this._opts.EncodingProfileName) ? AppText.T("remuxConfiguration.reason.videoEncoding") : AppText.T("remuxConfiguration.reason.audioProcessing")))));
                        this.Log(LogSection.Config, LogLevel.Error, AppText.F("remuxConfiguration.ffmpegUnavailable", reason));
                        result.AddError(new PipelineInitializationIssue("tools.ffmpegUnavailable", AppText.F("remuxConfiguration.ffmpegUnavailable", reason), "FfmpegPath", "Tools", true));
                        success = false;
                    }

                    if (success && !this._opts.DryRun && this._opts.AudioDownsample24To16 && !string.IsNullOrEmpty(this._ffmpegPath) && !FfmpegProvider.SupportsLibSoxr(this._ffmpegPath))
                    {
                        this.Log(LogSection.Config, LogLevel.Error, AppText.T("remuxConfiguration.libsoxrUnavailable"));
                        result.AddError(new PipelineInitializationIssue("tools.libsoxrUnavailable", AppText.T("remuxConfiguration.libsoxrUnavailable"), nameof(Options.AudioDownsample24To16), "Processing", true));
                        success = false;
                    }

                    // Crea servizio frame-sync
                    if (success && this._opts.FrameSync && !string.IsNullOrEmpty(this._ffmpegPath))
                    {
                        result.CurrentField = nameof(Options.FrameSync);
                        result.CurrentSection = "Synchronization";
                        this._frameSyncService = new FrameSyncService(this._ffmpegPath, this._toolPathResolver);
                        this._frameSyncService.SetAnalysisCrop(this._opts.AnalysisCropSourcePx, this._opts.AnalysisCropLanguagePx);
                    }

                    // Log impostazioni conversione se attiva
                    result.CurrentField = nameof(Options.AudioFormat);
                    result.CurrentSection = "Processing";
                    if (success && !string.IsNullOrEmpty(this._opts.AudioFormat))
                    {
                        this.Log(LogSection.Config, LogLevel.Phase, "Processing audio attivo: " + Utils.FormatAudioFormat(this._opts.AudioFormat) + " (" + this._opts.AudioProcessingScope + ")");
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
                        else if (string.Equals(this._opts.AudioFormat, "ac3", StringComparison.OrdinalIgnoreCase))
                        {
                            this.Log(LogSection.Config, LogLevel.Debug, "  AC-3 bitrate: mono=" + AppSettingsService.Instance.Settings.Ac3.Bitrate.Mono + "k, stereo=" + AppSettingsService.Instance.Settings.Ac3.Bitrate.Stereo + "k, 5.1=" + AppSettingsService.Instance.Settings.Ac3.Bitrate.Surround51 + "k");
                        }
                    }

                    if (success && this._opts.AudioSourceFillThresholdMs > 0)
                    {
                        this.Log(LogSection.Config, LogLevel.Phase, "Audio source fill attivo: soglia " + this._opts.AudioSourceFillThresholdMs + "ms, sorgente " + this._opts.AudioSourceFillLanguage);
                    }

                    // Log profilo encoding video se attivo
                    if (success && !string.IsNullOrEmpty(this._opts.EncodingProfileName))
                    {
                        result.CurrentField = nameof(Options.EncodingProfileName);
                        EncodingProfile encProfile = AppSettingsService.Instance.GetProfile(this._opts.EncodingProfileName);
                        if (encProfile != null)
                        {
                            this.Log(LogSection.Config, LogLevel.Phase, "Encoding video attivo: profilo '" + encProfile.Name + "' (" + encProfile.Codec + ")");
                        }
                        else
                        {
                            this.Log(LogSection.Config, LogLevel.Info, AppText.F("remuxConfiguration.profileNotFound", this._opts.EncodingProfileName));
                            result.AddWarning(new PipelineInitializationIssue("encoding.profileNotFound", AppText.F("remuxConfiguration.profileNotFound", this._opts.EncodingProfileName), nameof(Options.EncodingProfileName), "Processing"));
                        }
                    }

                    // Solo dopo i controlli strumenti crea la destinazione; eventuali eccezioni non toccano il lavoro attivo.
                    if (success && !this._opts.Overwrite && !Directory.Exists(this._opts.DestinationFolder))
                    {
                        result.CurrentField = nameof(Options.DestinationFolder);
                        result.CurrentSection = "Output";
                        this.Log(LogSection.Config, LogLevel.Info, "Creazione cartella destinazione: " + this._opts.DestinationFolder);
                        Directory.CreateDirectory(this._opts.DestinationFolder);
                    }
                }
            }

            return success;
        }

        /// <summary>Risoluzione autorevole per l'opt-in wizard, senza scan distruttiva o analisi.
        /// Gli inventari rimangono nella cache candidata, riusabile durante il commit.</summary>
        private bool HasSelectedLanguageAudio()
        {
            bool hasAudio = false;
            using (this.BeginIsolatedPreparation(this.Log, this._preparationCancellation))
            {
                PipelineFileScanner scanner = new PipelineFileScanner(this.Log);
                foreach (FileProcessingRecord record in scanner.Scan(this._opts, this._needsMerge, this._preparationCancellation))
                {
                    if (string.IsNullOrEmpty(record.LangFilePath)) continue;
                    this._preparationCancellation.ThrowIfCancellationRequested();
                    if (this._opts.ExplicitTrackSelection != null)
                        record.ExplicitTrackSelection = this._opts.ExplicitTrackSelection.Pairs.FirstOrDefault(pair =>
                            pair.PairKey == RemuxPairTrackSelection.CreatePairKey(record.SourceFilePath, record.LangFilePath));
                    MkvFileInfo info = this.GetCachedFileInfo(record.LangFilePath);
                    if (info?.Tracks == null)
                        throw new InvalidOperationException(AppText.F("remuxConfiguration.langTracksReadFailed", record.LangFilePath));
                    PipelineTrackSelectionResolver.ResolveLanguage(record, this._opts, info.Tracks, this._mkvService, this._codecPatterns,
                        out List<TrackInfo> audio, out _);
                    hasAudio |= audio.Count > 0;
                }
            }
            return hasAudio;
        }

        /// <summary>
        /// Scansiona le cartelle e crea la lista di record
        /// </summary>
        /// <returns>Lista di record per i file trovati</returns>
        public List<FileProcessingRecord> ScanFiles()
        {
            this.ResetScanArtifactsAfterCommit();
            PipelineFileScanner scanner = new PipelineFileScanner(this.Log);
            List<FileProcessingRecord> records = scanner.Scan(this._opts, this._needsMerge);
            this.ApplyTrackSelections(records);
            return records;
        }

        /// <summary>Riapplica input avanzati dopo Apply senza riscan distruttiva; non invalida analisi di coppie invariate.</summary>
        public void ApplyTrackSelections(IEnumerable<FileProcessingRecord> records)
        {
            Dictionary<string, RemuxPairTrackSelection> selections = new Dictionary<string, RemuxPairTrackSelection>(StringComparer.Ordinal);
            if (this._opts.ExplicitTrackSelection != null)
                foreach (RemuxPairTrackSelection selection in this._opts.ExplicitTrackSelection.Pairs) selections.Add(selection.PairKey, selection);
            foreach (FileProcessingRecord record in records)
            {
                if (string.IsNullOrEmpty(record.LangFilePath)) continue;
                RemuxPairTrackSelection selection = null;
                if (this._opts.ExplicitTrackSelection != null && !selections.TryGetValue(RemuxPairTrackSelection.CreatePairKey(record.SourceFilePath, record.LangFilePath), out selection))
                    throw new InvalidOperationException(AppText.F("remuxConfiguration.selectionMissingPairFile", record.SourceFilePath));
                if (selection != null || !this._opts.SkipPairsWithoutSelectedLangTracks)
                    record.ApplyTrackSelection(selection);
                else
                {
                    record.ExplicitTrackSelection = null;
                    this.RefreshTrackSelectionEligibility(record);
                }
            }
        }

        /// <summary>Rivaluta la selezione a regole usando il resolver comune e la cache del lavoro.
        /// Chiamare dopo Apply; false indica skip (anche manuale), non mancato matching.</summary>
        public bool RefreshTrackSelectionEligibility(FileProcessingRecord record)
        {
            if (record.Status == FileStatus.Skipped && !record.SkippedByTrackSelection) return false;
            if (this._opts.SkipPairsWithoutSelectedLangTracks && this._needsMerge &&
                record.ExplicitTrackSelection == null && !string.IsNullOrEmpty(record.LangFilePath))
            {
                MkvFileInfo info = this.GetCachedFileInfo(record.LangFilePath);
                if (info == null) throw new InvalidOperationException(AppText.F("remuxConfiguration.langTracksReadFailed", record.LangFilePath));
                PipelineTrackSelectionResolver.ResolveLanguage(record, this._opts, info.Tracks, this._mkvService, this._codecPatterns,
                    out List<TrackInfo> audio, out List<TrackInfo> subs);
                record.ApplySelectionSkip(audio.Count == 0 && subs.Count == 0);
            }
            return record.Status != FileStatus.Skipped;
        }

        /// <summary>
        /// Analizza un singolo file: rilevamento velocità e frame-sync
        /// </summary>
        /// <param name="record">Record del file da analizzare</param>
        /// <param name="cancellationToken">Token di annullamento cooperativo</param>
        public void AnalyzeFile(FileProcessingRecord record, System.Threading.CancellationToken cancellationToken = default)
        {
            if (this._isolatedPreparation) throw new InvalidOperationException(AppText.T("remuxConfiguration.preparationAnalysisForbidden"));
            cancellationToken.ThrowIfCancellationRequested();
            if (!this.RefreshTrackSelectionEligibility(record))
            {
                this.OnFileUpdated?.Invoke(record);
                return;
            }
            if (record.SkippedByTrackSelection) return;
            PipelineAnalysisCoordinator coordinator = new PipelineAnalysisCoordinator(this._opts, this._needsMerge, this._ffmpegPath, this._frameSyncService, this._trackMapper, this._diagnosticsWriter, this.GetCachedFileInfo, this.SetupLogRedirect, this.ClearLogRedirect, this.OnFileUpdated, this.BuildMergeCommand, this._toolPathResolver);
            if (this.OnAnalysisMediaReady != null)
            {
                coordinator.MediaReady = (current, language, cancellation) =>
                {
                    this._trackMapper.CollectLanguageTracks(current, language.Tracks, this._mkvService, this._opts, this._codecPatterns, out List<TrackInfo> audioTracks, out _);
                    this.OnAnalysisMediaReady?.Invoke(current, audioTracks, cancellation);
                };
            }
            try
            {
                coordinator.AnalyzeFile(record, cancellationToken);
                this._ffmpegPath = coordinator.FfmpegPath;
            }
            catch (System.OperationCanceledException)
            {
                if (record != null)
                {
                    record.RestoreAfterCancelledAnalysis();
                    this.OnFileUpdated?.Invoke(record);
                }

                throw;
            }
            finally
            {
                this.ClearLogRedirect();
            }
        }

        /// <summary>
        /// Costruisce il comando mkvmerge e lo salva nel record
        /// </summary>
        /// <param name="record">Record del file</param>
        public void BuildMergeCommand(FileProcessingRecord record)
        {
            if (!this.RefreshTrackSelectionEligibility(record))
            {
                record.MergeCommand = "";
                return;
            }
            this._mergePreviewBuilder.Build(record, this._opts, this._mkvService, this.GetCachedFileInfo, this._needsMerge, this._needsRemux, this._filterSourceAudio, this._filterSourceSubs, this._codecPatterns, this._sourceAudioCodecPatterns, this._ffmpegPath);
        }

        /// <summary>
        /// Esegue il processing di un singolo file (remux e/o encoding)
        /// </summary>
        /// <param name="record">Record del file da elaborare</param>
        public void ProcessFile(FileProcessingRecord record)
        {
            if (this._isolatedPreparation) throw new InvalidOperationException(AppText.T("remuxConfiguration.preparationOutputForbidden"));
            if (!this.RefreshTrackSelectionEligibility(record))
            {
                this.OnFileUpdated?.Invoke(record);
                return;
            }
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
                    if (record.Status == FileStatus.Error || record.Status == FileStatus.Skipped)
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
                if (!done && !this._opts.DryRun && (record.Status == FileStatus.Done || record.Status == FileStatus.Processing) && this._needsEncode && !string.IsNullOrEmpty(this._ffmpegPath))
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

        #endregion

        #region Metodi privati

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

            // Raccogli tracce dal file lingua (solo se merge attivo)
            if (this._needsMerge)
            {
                langInfo = this.GetCachedFileInfo(record.LangFilePath);
                langTracks = langInfo?.Tracks;
                ResolvedRemuxTracks resolved = PipelineTrackSelectionResolver.Resolve(record, this._opts, sourceTracks, langTracks, this._mkvService, this._codecPatterns, this._sourceAudioCodecPatterns);
                sourceAudioIds = resolved.SourceAudioIds;
                sourceSubIds = resolved.SourceSubIds;
                audioTracks = resolved.LangAudioTracks;
                subtitleTracks = resolved.LangSubTracks;

                if (langTracks == null)
                {
                    // Errore lettura tracce lingua
                    record.ErrorMessage = "Impossibile leggere tracce file lingua";
                    record.Status = FileStatus.Error;
                    done = true;
                }
                else if (audioTracks.Count == 0 && subtitleTracks.Count == 0)
                {
                    // Nessuna traccia corrispondente
                    ConsoleHelper.Write(LogSection.Merge, LogLevel.Info, "  Nessuna traccia corrispondente trovata");
                    if (record.ExplicitTrackSelection != null || this._opts.SkipPairsWithoutSelectedLangTracks) record.ApplySelectionSkip(true);
                    else
                    {
                        record.SkipReason = "No matching tracks";
                        record.ErrorMessage = "Nessuna traccia corrispondente";
                        record.Status = FileStatus.Error;
                    }
                    done = true;
                }
            }
            else
            {
                ResolvedRemuxTracks resolved = PipelineTrackSelectionResolver.Resolve(record, this._opts, sourceTracks, null, this._mkvService, this._codecPatterns, this._sourceAudioCodecPatterns);
                sourceAudioIds = resolved.SourceAudioIds;
                sourceSubIds = resolved.SourceSubIds;
            }

            // Conversione, deep analysis e merge con garanzia cleanup file temporanei
            try
            {
                if (!done && (this._opts.AudioProcessingScope != "disabled" ||
                    this._opts.AudioSourceFillThresholdMs > 0 ||
                    (record.DeepAnalysisApplied && record.DeepAnalysisMap != null && (record.DeepAnalysisMap.Operations.Count > 0 || record.DeepAnalysisMap.LanguageAudioOffsetMs != 0) && audioTracks.Count > 0)))
                {
                    AudioProcessingRequest audioRequest = this._audioRequestBuilder.Build(record, this._opts, sourceInfo, langInfo, sourceTracks, sourceAudioIds, audioTracks, this._needsMerge, this._filterSourceAudio, effectiveAudioDelay);
                    if (string.IsNullOrEmpty(this._opts.AudioFormat) && (audioRequest.SourceTracksToProcess.Count > 0 || audioRequest.LangTracksToProcess.Count > 0))
                    {
                        record.ErrorMessage = "Processing audio richiesto ma formato audio non impostato";
                        record.Status = FileStatus.Error;
                        done = true;
                    }
                    else if (this._opts.DryRun)
                    {
                        AudioProcessingPlan audioPlan = this.BuildAudioProcessingPlan(audioRequest, true);
                        AudioProcessingDryRunHelper.AddPlaceholders(audioPlan, this._opts, convertedSourceTracks, convertedLangTracks, processedSourceAudioInfo, processedLangAudioInfo, audioDelayBypassedLangIds);
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

                // Deep analysis: applica qui la EditMap solo ai sottotitoli; AudioProcessingService materializza EditMap e stretch audio.
                if (!done && this._deepEditApplier.ApplySubtitles(record, subtitleTracks, processedLangSubTracks, this._opts, this._ffmpegPath))
                {
                }
                else if (!done && record.Status == FileStatus.Error)
                {
                    done = true;
                }

                // Subtitle canvas rewrite: lavora dopo il timeline edit per rispettare eventuali cut/insert già applicati.
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
                    mergeReq.SubtitleStretchFactor = stretchFactor;
                    mergeReq.AudioFormat = this._opts.AudioFormat;
                    mergeReq.SourceTitle = (sourceInfo != null) ? sourceInfo.ContainerTitle : "";
                    mergeReq.ConvertedSourceTracks = convertedSourceTracks;
                    mergeReq.ConvertedLangTracks = convertedLangTracks;
                    this.AddRequiredProcessedLangTrackIds(record.AudioProcessingPreview, mergeReq.RequiredProcessedLangTrackIds);
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
                    record.DisplayAudioFormat = Utils.FormatAudioFormat(this._opts.AudioFormat);

                    // Calcola lingue risultato
                    this.PopulateResultLanguages(record, sourceTracks, sourceAudioIds, audioTracks, subtitleTracks);

                    // Log info
                    ConsoleHelper.Write(LogSection.Merge, LogLevel.Debug, "  Output: " + finalOutput);
                    if (this._needsMerge)
                    {
                        delayInfo = "  Delay: Audio " + Utils.FormatDelay(effectiveAudioDelay) + ", Sub " + Utils.FormatDelay(effectiveSubDelay);
                        if (!string.IsNullOrEmpty(stretchFactor))
                            delayInfo += ", stretch: " + stretchFactor;
                        ConsoleHelper.Write(LogSection.Merge, LogLevel.Debug, delayInfo);
                    }

                    // Esegui merge e registra risultato
                    this._outputManager.RunMergeAndRecord(record, mergeArgs, tempOutput, finalOutput, this._opts, this._mkvService);

                }
            }
            finally
            {
                // Cleanup file convertiti temporanei
                foreach (KeyValuePair<int, string> kvp in convertedSourceTracks)
                    FileHelper.DeleteTempFile(kvp.Value);
                foreach (KeyValuePair<int, string> kvp in convertedLangTracks)
                    FileHelper.DeleteTempFile(kvp.Value);
                foreach (KeyValuePair<int, string> kvp in processedLangSubTracks)
                    this.DeleteProcessedSubtitleFile(kvp.Value);
            }

            return finalOutput;
        }

        /// <summary>
        /// Copia nel merge gli ID Language per cui il piano impone un output FFmpeg
        /// </summary>
        private void AddRequiredProcessedLangTrackIds(AudioProcessingPlan plan, HashSet<int> destination)
        {
            if (plan == null || destination == null)
            {
                return;
            }

            for (int i = 0; i < plan.LangTracks.Count; i++)
            {
                if (plan.LangTracks[i].RenderRequired && plan.LangTracks[i].Track != null)
                {
                    destination.Add(plan.LangTracks[i].Track.Id);
                }
            }
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
        /// <param name="level">Livello di severità</param>
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

            if (!string.IsNullOrEmpty(path))
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
            object callbackSync = new object();
            ConsoleHelper.SetLogCallback((section, level, text) =>
            {
                lock (callbackSync)
                {
                    // Guard contro ri-entranza (OnLogMessage potrebbe chiamare Write)
                    if (inCallback)
                    {
                        // Evita fallback console nella WebUI: il messaggio rientrante viene scartato
                        return;
                    }
                    inCallback = true;

                    try
                    {
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
                    }
                    finally
                    {
                        inCallback = false;
                    }
                }
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
            lock (this._fileInfoCacheLock)
            {
                if (this._fileInfoCache.ContainsKey(filePath))
                {
                    return this._fileInfoCache[filePath];
                }
            }

            if (this._isolatedPreparation)
            {
                string error = "";
                info = this._mkvService.GetFileInfoIsolated(filePath, 30000, this._preparationCancellation, message => error = message);
                if (info == null) throw new InvalidOperationException(filePath + ": " + (string.IsNullOrEmpty(error) ? AppText.T("remuxConfiguration.tracksReadFailed") : error));
            }
            else info = this._mkvService.GetFileInfo(filePath);
            if (info != null)
            {
                lock (this._fileInfoCacheLock)
                {
                    this._fileInfoCache[filePath] = info;
                }
            }

            return info;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Pattern codec risolti per filtro tracce lingua importate
        /// </summary>
        public string[] CodecPatterns
        {
            get
            {
                return this._codecPatterns;
            }
        }

        #endregion
    }
}
