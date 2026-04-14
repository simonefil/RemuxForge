using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RemuxForge.Core
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
        /// Indice file lingua: mappa episodeId a percorso completo
        /// </summary>
        private Dictionary<string, string> _languageIndex;

        /// <summary>
        /// Cache info file MKV per evitare letture ripetute
        /// </summary>
        private Dictionary<string, MkvFileInfo> _fileInfoCache;

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
            this._languageIndex = new Dictionary<string, string>();
            this._fileInfoCache = new Dictionary<string, MkvFileInfo>();
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
            bool success = true;
            Regex langRegex = new Regex(@"^[a-z]{2,3}$");
            List<string> suggestions = null;
            string[] patterns = null;
            MkvToolsService tempService = null;
            FfmpegProvider ffmpegProvider = null;

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
            this._needsRemux = (this._needsMerge || this._needsFilter || this._opts.ConvertFormat.Length > 0);
            this._needsEncode = (this._opts.EncodingProfileName.Length > 0);

            // Modalita' singola sorgente per merge
            if (this._needsMerge && this._opts.LanguageFolder.Length == 0 && this._opts.SourceFolder.Length > 0)
            {
                this._opts.LanguageFolder = this._opts.SourceFolder;
            }

            // Verifica parametro obbligatorio: source folder
            if (this._opts.SourceFolder.Length == 0)
            {
                this.Log(LogSection.Config, LogLevel.Error, "Errore: parametro obbligatorio mancante (source)");
                success = false;
            }

            // Verifica almeno un'operazione configurata
            if (success && !this._needsRemux && !this._needsEncode)
            {
                this.Log(LogSection.Config, LogLevel.Error, "Errore: nessuna operazione configurata. Specificare almeno una tra: lingua target, filtri tracce, conversione audio, profilo encoding");
                success = false;
            }

            // Valida esistenza cartella sorgente
            if (success && !Directory.Exists(this._opts.SourceFolder))
            {
                this.Log(LogSection.Config, LogLevel.Error, "Errore: cartella sorgente non trovata: " + this._opts.SourceFolder);
                success = false;
            }

            // Valida esistenza cartella lingua (solo se merge attivo)
            if (success && this._needsMerge && !Directory.Exists(this._opts.LanguageFolder))
            {
                this.Log(LogSection.Config, LogLevel.Error, "Errore: cartella lingua non trovata: " + this._opts.LanguageFolder);
                success = false;
            }

            // Valida formato codice lingua (solo se merge attivo)
            if (success && this._needsMerge)
            {
                for (int i = 0; i < this._opts.TargetLanguage.Count && success; i++)
                {
                    if (!langRegex.IsMatch(this._opts.TargetLanguage[i].ToLower()))
                    {
                        this.Log(LogSection.Config, LogLevel.Error, "Errore: lingua non valida '" + this._opts.TargetLanguage[i] + "'. Usa codice ISO 639-2");
                        success = false;
                    }
                }
            }

            // Valida lingue target contro lista ISO 639-2 (solo se merge attivo)
            if (success && this._needsMerge)
            {
                for (int i = 0; i < this._opts.TargetLanguage.Count && success; i++)
                {
                    if (!LanguageValidator.IsValid(this._opts.TargetLanguage[i]))
                    {
                        this.Log(LogSection.Config, LogLevel.Error, "Errore: lingua '" + this._opts.TargetLanguage[i] + "' non riconosciuta");
                        suggestions = LanguageValidator.GetSimilar(this._opts.TargetLanguage[i], 3);
                        if (suggestions.Count > 0)
                        {
                            this.Log(LogSection.Config, LogLevel.Info, "Forse intendevi: " + string.Join(", ", suggestions) + "?");
                        }
                        success = false;
                    }
                }
            }

            // Valida KeepSourceAudioLangs
            if (success)
            {
                for (int i = 0; i < this._opts.KeepSourceAudioLangs.Count && success; i++)
                {
                    if (!LanguageValidator.IsValid(this._opts.KeepSourceAudioLangs[i]))
                    {
                        this.Log(LogSection.Config, LogLevel.Error, "Errore: lingua '" + this._opts.KeepSourceAudioLangs[i] + "' in keep-source-audio non riconosciuta");
                        success = false;
                    }
                }
            }

            // Valida KeepSourceSubtitleLangs
            if (success)
            {
                for (int i = 0; i < this._opts.KeepSourceSubtitleLangs.Count && success; i++)
                {
                    if (!LanguageValidator.IsValid(this._opts.KeepSourceSubtitleLangs[i]))
                    {
                        this.Log(LogSection.Config, LogLevel.Error, "Errore: lingua '" + this._opts.KeepSourceSubtitleLangs[i] + "' in keep-source-subs non riconosciuta");
                        success = false;
                    }
                }
            }

            // Valida codec audio importate
            if (success)
            {
                for (int i = 0; i < this._opts.AudioCodec.Count && success; i++)
                {
                    patterns = CodecMapping.GetCodecPatterns(this._opts.AudioCodec[i]);
                    if (patterns == null)
                    {
                        this.Log(LogSection.Config, LogLevel.Error, "Errore: codec '" + this._opts.AudioCodec[i] + "' non riconosciuto. Validi: " + CodecMapping.GetAllCodecNames());
                        success = false;
                    }
                }
            }

            // Valida codec audio sorgente
            if (success)
            {
                for (int i = 0; i < this._opts.KeepSourceAudioCodec.Count && success; i++)
                {
                    patterns = CodecMapping.GetCodecPatterns(this._opts.KeepSourceAudioCodec[i]);
                    if (patterns == null)
                    {
                        this.Log(LogSection.Config, LogLevel.Error, "Errore: codec '" + this._opts.KeepSourceAudioCodec[i] + "' in keep-source-audio-codec non riconosciuto");
                        success = false;
                    }
                }
            }

            // Valida mutua esclusione SubOnly e AudioOnly
            if (success && this._opts.SubOnly && this._opts.AudioOnly)
            {
                this.Log(LogSection.Config, LogLevel.Error, "Errore: sub-only e audio-only non possono essere usati insieme");
                success = false;
            }

            // Valida modalita' output
            if (success && this._opts.Overwrite && this._opts.DestinationFolder.Length > 0)
            {
                this.Log(LogSection.Config, LogLevel.Error, "Errore: overwrite e destination non possono essere usati insieme");
                success = false;
            }
            if (success && !this._opts.Overwrite && this._opts.DestinationFolder.Length == 0)
            {
                // Se solo encode senza remux, overwrite implicito (encode in-place)
                if (this._needsEncode && !this._needsRemux)
                {
                    this._opts.Overwrite = true;
                    this.Log(LogSection.Config, LogLevel.Info, "Encode-only: sovrascrivi sorgente (overwrite implicito)");
                }
                else
                {
                    this.Log(LogSection.Config, LogLevel.Error, "Errore: specificare destination oppure overwrite");
                    success = false;
                }
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

                // Risolvi mkvmerge: se l'utente non ha specificato un path, usa auto-find
                if (this._opts.MkvMergePath == "mkvmerge")
                {
                    MkvMergeProvider mkvProvider = new MkvMergeProvider();
                    if (mkvProvider.Resolve())
                    {
                        this._opts.MkvMergePath = mkvProvider.MkvMergePath;
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
                    ffmpegProvider = new FfmpegProvider(AppSettingsService.Instance.ConfigFolder);
                    if (ffmpegProvider.Resolve())
                    {
                        this._ffmpegPath = ffmpegProvider.FfmpegPath;
                        this.Log(LogSection.Config, LogLevel.Success, "Trovato ffmpeg: " + this._ffmpegPath);
                    }
                    else if (this._opts.FrameSync || this._opts.ConvertFormat.Length > 0 || this._opts.EncodingProfileName.Length > 0)
                    {
                        // ffmpeg richiesto per frame-sync, conversione audio o encoding video
                        string reason = this._opts.FrameSync ? "frame-sync" : (this._opts.EncodingProfileName.Length > 0 ? "encoding video" : "conversione audio");
                        this.Log(LogSection.Config, LogLevel.Error, "ffmpeg non trovato e impossibile scaricarlo. Necessario per " + reason);
                        success = false;
                    }

                    // Crea servizio frame-sync
                    if (success && this._opts.FrameSync && this._ffmpegPath.Length > 0)
                    {
                        this._frameSyncService = new FrameSyncService(this._ffmpegPath);
                        this._frameSyncService.SetCropFlags(this._opts.CropSourceTo43, this._opts.CropLangTo43);
                    }

                    // Log impostazioni conversione se attiva
                    if (success && this._opts.ConvertFormat.Length > 0)
                    {
                        this.Log(LogSection.Config, LogLevel.Phase, "Conversione audio attiva: " + this._opts.ConvertFormat.ToUpper());
                        if (string.Equals(this._opts.ConvertFormat, "flac", StringComparison.OrdinalIgnoreCase))
                        {
                            this.Log(LogSection.Config, LogLevel.Debug, "  FLAC compression level: " + AppSettingsService.Instance.Settings.Flac.CompressionLevel);
                        }
                        else
                        {
                            this.Log(LogSection.Config, LogLevel.Debug, "  Opus bitrate: mono=" + AppSettingsService.Instance.Settings.Opus.Bitrate.Mono + "k, stereo=" + AppSettingsService.Instance.Settings.Opus.Bitrate.Stereo + "k, 5.1=" + AppSettingsService.Instance.Settings.Opus.Bitrate.Surround51 + "k, 7.1=" + AppSettingsService.Instance.Settings.Opus.Bitrate.Surround71 + "k");
                        }
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
                    this._languageIndex.Clear();
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

            List<FileProcessingRecord> records = new List<FileProcessingRecord>();

            // Trova file sorgente
            string extList = string.Join(", ", this._opts.FileExtensions);
            List<string> sourceFiles = this.FindVideoFiles(this._opts.SourceFolder, this._opts.FileExtensions, this._opts.Recursive);
            this.Log(LogSection.General, LogLevel.Success, "Trovati " + sourceFiles.Count + " file sorgente (" + extList + ")");

            // Costruisci indice file lingua (solo se merge attivo)
            if (this._needsMerge)
            {
                this.Log(LogSection.General, LogLevel.Info, "Indicizzazione cartella lingua...");
                List<string> languageFiles = this.FindVideoFiles(this._opts.LanguageFolder, this._opts.FileExtensions, this._opts.Recursive);
                this._languageIndex.Clear();

                for (int i = 0; i < languageFiles.Count; i++)
                {
                    string langFileName = Path.GetFileName(languageFiles[i]);
                    string langEpisodeId = this.GetEpisodeIdentifier(langFileName, this._opts.MatchPattern);
                    if (langEpisodeId.Length > 0)
                    {
                        this._languageIndex[langEpisodeId] = languageFiles[i];
                    }
                }

                this.Log(LogSection.General, LogLevel.Success, "Indicizzati " + this._languageIndex.Count + " file lingua");
            }

            // Crea record per ogni file sorgente
            for (int i = 0; i < sourceFiles.Count; i++)
            {
                string sourceFilePath = sourceFiles[i];
                string sourceFileName = Path.GetFileName(sourceFilePath);

                FileProcessingRecord record = new FileProcessingRecord();
                record.SourceFileName = sourceFileName;
                record.SourceFilePath = sourceFilePath;

                // Dimensione file sorgente
                FileInfo sourceFileInfo = new FileInfo(sourceFilePath);
                record.SourceSize = sourceFileInfo.Length;

                // Estrai ID episodio
                string episodeId = this.GetEpisodeIdentifier(sourceFileName, this._opts.MatchPattern);

                if (this._needsMerge)
                {
                    // Merge attivo: richiede episode ID e match con file lingua
                    if (episodeId.Length == 0)
                    {
                        record.SkipReason = "No episode ID";
                        record.Status = FileStatus.Skipped;
                        records.Add(record);
                        continue;
                    }

                    record.EpisodeId = episodeId;

                    if (!this._languageIndex.ContainsKey(episodeId))
                    {
                        record.SkipReason = "No match";
                        record.Status = FileStatus.Skipped;
                        records.Add(record);
                        continue;
                    }

                    string languageFilePath = this._languageIndex[episodeId];
                    record.LangFileName = Path.GetFileName(languageFilePath);
                    record.LangFilePath = languageFilePath;

                    // Dimensione file lingua
                    FileInfo langFileInfo = new FileInfo(languageFilePath);
                    record.LangSize = langFileInfo.Length;
                }
                else
                {
                    // Senza merge: ogni file sorgente e' valido
                    if (episodeId.Length > 0)
                    {
                        record.EpisodeId = episodeId;
                    }
                    else
                    {
                        record.EpisodeId = sourceFileName;
                    }
                }

                // Record pronto per analisi
                record.Status = FileStatus.Pending;
                records.Add(record);
            }

            return records;
        }

        /// <summary>
        /// Analizza un singolo file: rilevamento velocita' e frame-sync
        /// </summary>
        /// <param name="record">Record del file da analizzare</param>
        public void AnalyzeFile(FileProcessingRecord record)
        {
            MkvFileInfo sourceInfo = null;
            MkvFileInfo langInfo = null;
            List<TrackInfo> sourceTracks = null;
            List<TrackInfo> langTracks = null;
            int syncOffset = 0;
            bool speedCorrectionActive = false;
            double detectedSourceFps = 0.0;
            double detectedLangFps = 0.0;
            bool speedMismatch = false;
            bool softTelecine = false;
            string ffmpegPath = "";
            FfmpegProvider ffmpegProvider = null;
            long sourceDefaultDuration = 0;
            long langDefaultDuration = 0;
            int sourceDurationMs = 0;
            SpeedCorrectionService speedService = null;
            bool speedOk = false;
            int frameSyncOffset = 0;
            bool done = false;

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
                this.SetupLogRedirect(record);

                // Aggiorna stato
                record.Status = FileStatus.Analyzing;
                if (this.OnFileUpdated != null)
                {
                    this.OnFileUpdated(record);
                }

                ConsoleHelper.Write(LogSection.General, LogLevel.Header, "Analisi: " + record.SourceFileName);
                ConsoleHelper.Write(LogSection.General, LogLevel.Debug, "  ID Episodio: " + record.EpisodeId);

                // Ottieni info file sorgente
                sourceInfo = this.GetCachedFileInfo(record.SourceFilePath);
                sourceTracks = (sourceInfo != null) ? sourceInfo.Tracks : null;

                // Popola lingue e tracce sorgente nel record
                record.SourceAudioLangs = this.GetAudioLanguages(sourceTracks);
                record.SourceSubLangs = this.GetSubtitleLanguages(sourceTracks);
                record.SourceAudioTracks = this.FilterTracksByType(sourceTracks, "audio");
                record.SourceSubTracks = this.FilterTracksByType(sourceTracks, "subtitles");

                if (this._needsMerge)
                {
                    // Merge attivo: leggi anche file lingua
                    ConsoleHelper.Write(LogSection.General, LogLevel.Info, "  Match: " + record.LangFileName);

                    langInfo = this.GetCachedFileInfo(record.LangFilePath);
                    langTracks = (langInfo != null) ? langInfo.Tracks : null;

                    record.LangAudioLangs = this.GetAudioLanguages(langTracks);
                    record.LangSubLangs = this.GetSubtitleLanguages(langTracks);

                    if (langTracks == null)
                    {
                        ConsoleHelper.Write(LogSection.General, LogLevel.Error, "  Impossibile leggere info tracce file lingua");
                        record.ErrorMessage = "Impossibile leggere tracce file lingua";
                        record.Status = FileStatus.Error;
                        if (this.OnFileUpdated != null)
                        {
                            this.OnFileUpdated(record);
                        }
                        this.ClearLogRedirect();
                        done = true;
                    }
                }
                else
                {
                    // Senza merge: analisi ridotta, passa direttamente ad Analyzed
                    record.SyncOffsetMs = 0;
                    record.AudioDelayApplied = 0;
                    record.SubDelayApplied = 0;
                    record.Status = FileStatus.Analyzed;
                    ConsoleHelper.Write(LogSection.General, LogLevel.Success, "  Analisi completata (no merge)");

                    if (this.OnFileUpdated != null)
                    {
                        this.OnFileUpdated(record);
                    }
                    this.ClearLogRedirect();
                    done = true;
                }
            }

            // Rilevamento automatico mismatch velocita' (se abilitato)
            if (!done && sourceInfo != null && langInfo != null && this._opts.SpeedCorrection)
            {
                speedMismatch = SpeedCorrectionService.DetectSpeedMismatch(sourceInfo, langInfo, out detectedSourceFps, out detectedLangFps);

                // Rileva soft telecine: fps diversi ma speed correction non necessaria (durata container uguale)
                if (!speedMismatch && detectedSourceFps > 0.0 && detectedLangFps > 0.0 && Math.Abs(detectedSourceFps - detectedLangFps) > 0.1)
                {
                    softTelecine = true;
                }

                if (speedMismatch)
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Phase, "  Mismatch velocita': source " + detectedSourceFps.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "fps, lang " + detectedLangFps.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "fps");

                    // Risolvi ffmpeg se non ancora disponibile
                    ffmpegPath = this._ffmpegPath;
                    if (ffmpegPath.Length == 0)
                    {
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  Risoluzione ffmpeg per frame matching...");
                        ffmpegProvider = new FfmpegProvider(AppSettingsService.Instance.ConfigFolder);
                        if (ffmpegProvider.Resolve())
                        {
                            ffmpegPath = ffmpegProvider.FfmpegPath;
                            this._ffmpegPath = ffmpegPath;
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Success, "  ffmpeg trovato: " + ffmpegPath);
                        }
                        else
                        {
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  ffmpeg non disponibile, correzione velocita' saltata");
                        }
                    }

                    if (speedMismatch && ffmpegPath.Length > 0)
                    {
                        // Trova default_duration per tracce video
                        sourceDefaultDuration = Utils.GetVideoDefaultDuration(sourceInfo.Tracks);
                        langDefaultDuration = Utils.GetVideoDefaultDuration(langInfo.Tracks);

                        // Durata sorgente in ms dal container
                        sourceDurationMs = (int)(sourceInfo.ContainerDurationNs / 1000000);

                        speedService = new SpeedCorrectionService(ffmpegPath);
                        speedService.SetCropFlags(this._opts.CropSourceTo43, this._opts.CropLangTo43);
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
                        }
                        else
                        {
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, "  Correzione velocita' fallita");
                            record.ErrorMessage = "Speed correction fallita";
                            record.Status = FileStatus.Error;
                            if (this.OnFileUpdated != null)
                            {
                                this.OnFileUpdated(record);
                            }
                            this.ClearLogRedirect();
                            done = true;
                        }
                    }
                }
            }

            // Deep analysis: modalita' avanzata per file con edit diversi
            if (!done && !speedCorrectionActive && this._opts.DeepAnalysis)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Avvio deep analysis...");

                // Risolvi ffmpeg se non ancora disponibile
                ffmpegPath = this._ffmpegPath;
                if (ffmpegPath.Length == 0)
                {
                    ffmpegProvider = new FfmpegProvider(AppSettingsService.Instance.ConfigFolder);
                    if (ffmpegProvider.Resolve())
                    {
                        ffmpegPath = ffmpegProvider.FfmpegPath;
                        this._ffmpegPath = ffmpegPath;
                    }
                }

                if (ffmpegPath.Length == 0)
                {
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  ffmpeg non disponibile");
                    record.ErrorMessage = "ffmpeg non disponibile per deep analysis";
                    record.Status = FileStatus.Error;
                    if (this.OnFileUpdated != null) { this.OnFileUpdated(record); }
                    this.ClearLogRedirect();
                    done = true;
                }

                if (!done)
                {
                    // Recupera default_duration se non ancora estratta
                    if (sourceDefaultDuration == 0 && sourceInfo != null)
                    {
                        sourceDefaultDuration = Utils.GetVideoDefaultDuration(sourceInfo.Tracks);
                    }
                    if (langDefaultDuration == 0 && langInfo != null)
                    {
                        langDefaultDuration = Utils.GetVideoDefaultDuration(langInfo.Tracks);
                    }
                    if (sourceDurationMs == 0 && sourceInfo != null && sourceInfo.ContainerDurationNs > 0)
                    {
                        sourceDurationMs = (int)(sourceInfo.ContainerDurationNs / 1000000);
                    }

                    if (sourceDefaultDuration > 0 && langDefaultDuration > 0 && sourceDurationMs > 0)
                    {
                        // Soft telecine: forza durate uguali per evitare stretch errato
                        long effectiveLangDuration = softTelecine ? sourceDefaultDuration : langDefaultDuration;

                        DeepAnalysisService deepService = new DeepAnalysisService(ffmpegPath);
                        deepService.SetCropFlags(this._opts.CropSourceTo43, this._opts.CropLangTo43);
                        EditMap editMap = deepService.Analyze(record.SourceFilePath, record.LangFilePath, sourceDefaultDuration, effectiveLangDuration, sourceDurationMs);

                        record.DeepAnalysisTimeMs = deepService.AnalysisTimeMs;

                        if (editMap != null)
                        {
                            record.DeepAnalysisMap = editMap;
                            record.DeepAnalysisApplied = true;
                            syncOffset = editMap.InitialDelayMs;

                            if (editMap.StretchFactor.Length > 0)
                            {
                                record.StretchFactor = editMap.StretchFactor;
                                record.SpeedCorrectionApplied = true;
                            }

                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Success, "  Completata: " + editMap.Operations.Count + " operazioni, delay iniziale " + editMap.InitialDelayMs + "ms (" + deepService.AnalysisTimeMs + "ms)");
                        }
                        else
                        {
                            // Fallback: deep analysis fallita, prosegui con frame-sync o delay semplice
                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Deep analysis fallita, fallback ad analisi standard");
                        }
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Dati video insufficienti, fallback ad analisi standard");
                    }
                }
            }

            // Frame-sync solo se non in correzione velocita'
            if (!done && !speedCorrectionActive && this._opts.FrameSync && this._frameSyncService != null)
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, "  Sincronizzazione tramite confronto visivo...");

                frameSyncOffset = this._frameSyncService.RefineOffset(record.SourceFilePath, record.LangFilePath);
                record.FrameSyncTimeMs = this._frameSyncService.FrameSyncTimeMs;

                if (frameSyncOffset != int.MinValue)
                {
                    syncOffset = frameSyncOffset;
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Success, "  Offset: " + Utils.FormatDelay(frameSyncOffset) + " (tempo: " + this._frameSyncService.FrameSyncTimeMs + "ms)");
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Dettaglio: " + this._frameSyncService.GetDetailSummary());
                }
                else if (detectedSourceFps > 0.0 && detectedLangFps > 0.0 && !speedMismatch)
                {
                    // Frame-sync fallito ma FPS diversi classificati come telecine:
                    // probabile falso telecine (durate uguali per coincidenza), ritenta con speed correction
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  Fallito con telecine presunto, ritento con speed correction forzata...");

                    // Risolvi ffmpeg se necessario
                    ffmpegPath = this._ffmpegPath;
                    if (ffmpegPath.Length == 0)
                    {
                        ffmpegProvider = new FfmpegProvider(AppSettingsService.Instance.ConfigFolder);
                        if (ffmpegProvider.Resolve())
                        {
                            ffmpegPath = ffmpegProvider.FfmpegPath;
                            this._ffmpegPath = ffmpegPath;
                        }
                    }

                    if (ffmpegPath.Length > 0 && sourceDefaultDuration == 0)
                    {
                        // Recupera default_duration se non ancora estratta (non era passato dal blocco speedMismatch)
                        sourceDefaultDuration = Utils.GetVideoDefaultDuration(sourceInfo.Tracks);
                        langDefaultDuration = Utils.GetVideoDefaultDuration(langInfo.Tracks);
                        sourceDurationMs = (int)(sourceInfo.ContainerDurationNs / 1000000);
                    }

                    if (ffmpegPath.Length > 0 && sourceDefaultDuration > 0 && langDefaultDuration > 0)
                    {
                        speedService = new SpeedCorrectionService(ffmpegPath);
                        speedService.SetCropFlags(this._opts.CropSourceTo43, this._opts.CropLangTo43);
                        speedOk = speedService.FindDelayAndVerify(record.SourceFilePath, record.LangFilePath, sourceDefaultDuration, langDefaultDuration, sourceDurationMs);

                        record.SpeedCorrectionTimeMs = speedService.ExecutionTimeMs;

                        if (speedOk)
                        {
                            syncOffset = speedService.SyncDelayMs;
                            record.StretchFactor = speedService.StretchFactor;
                            record.SpeedCorrectionApplied = true;
                            speedCorrectionActive = true;

                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Success, "  Correzione forzata: delay=" + speedService.InitialDelayMs + "ms, sync=" + speedService.SyncDelayMs + "ms, stretch=" + speedService.StretchFactor + " (" + speedService.ExecutionTimeMs + "ms)");
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Verifica: " + speedService.GetDetailSummary());
                        }
                        else
                        {
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Error, "  Correzione velocita' forzata fallita");
                            record.ErrorMessage = "Frame sync e speed correction falliti";
                            record.Status = FileStatus.Error;
                            if (this.OnFileUpdated != null)
                            {
                                this.OnFileUpdated(record);
                            }
                            this.ClearLogRedirect();
                            done = true;
                        }
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Error, "  Sincronizzazione fallita (ffmpeg o default_duration non disponibili per retry)");
                        record.ErrorMessage = "Frame sync fallito";
                        record.Status = FileStatus.Error;
                        if (this.OnFileUpdated != null)
                        {
                            this.OnFileUpdated(record);
                        }
                        this.ClearLogRedirect();
                        done = true;
                    }
                }
                else
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Error, "  Sincronizzazione fallita");
                    record.ErrorMessage = "Frame sync fallito";
                    record.Status = FileStatus.Error;
                    if (this.OnFileUpdated != null)
                    {
                        this.OnFileUpdated(record);
                    }
                    this.ClearLogRedirect();
                    done = true;
                }
            }

            if (!done)
            {
                // Salva offset sync e calcola delay effettivi
                record.SyncOffsetMs = syncOffset;
                record.AudioDelayApplied = syncOffset + this._opts.AudioDelay + record.ManualAudioDelayMs;
                record.SubDelayApplied = syncOffset + this._opts.SubtitleDelay + record.ManualSubDelayMs;

                // Analisi completata
                record.Status = FileStatus.Analyzed;
                ConsoleHelper.Write(LogSection.General, LogLevel.Success, "  Analisi completata: delay audio " + Utils.FormatDelay(record.AudioDelayApplied) + ", sub " + Utils.FormatDelay(record.SubDelayApplied));

                // Costruisci comando mkvmerge preview
                this.BuildMergeCommand(record);

                if (this.OnFileUpdated != null)
                {
                    this.OnFileUpdated(record);
                }

                this.ClearLogRedirect();
            }
        }

        /// <summary>
        /// Costruisce il comando mkvmerge e lo salva nel record
        /// </summary>
        /// <param name="record">Record del file</param>
        public void BuildMergeCommand(FileProcessingRecord record)
        {
            int effectiveAudioDelay = record.SyncOffsetMs + this._opts.AudioDelay + record.ManualAudioDelayMs;
            int effectiveSubDelay = record.SyncOffsetMs + this._opts.SubtitleDelay + record.ManualSubDelayMs;
            string stretchFactor = record.StretchFactor;
            MkvFileInfo sourceInfo = null;
            List<TrackInfo> sourceTracks = null;
            List<TrackInfo> langTracks = null;
            List<int> sourceAudioIds = new List<int>();
            List<int> sourceSubIds = new List<int>();
            List<TrackInfo> audioTracks = new List<TrackInfo>();
            List<TrackInfo> subtitleTracks = new List<TrackInfo>();
            List<TrackInfo> foundAudio = null;
            List<TrackInfo> foundSubs = null;
            string tl = "";
            string outputPath = "";
            List<string> mergeArgs = null;

            // Ottieni info tracce sorgente
            sourceInfo = this.GetCachedFileInfo(record.SourceFilePath);
            sourceTracks = (sourceInfo != null) ? sourceInfo.Tracks : null;

            // Ottieni info tracce lingua (solo se merge attivo)
            if (this._needsMerge && record.LangFilePath.Length > 0)
            {
                MkvFileInfo langInfo = this.GetCachedFileInfo(record.LangFilePath);
                langTracks = (langInfo != null) ? langInfo.Tracks : null;
            }

            // Procedi solo se le tracce sorgente sono disponibili
            if (sourceTracks != null)
            {
                // ID tracce sorgente da mantenere
                if (this._filterSourceAudio)
                {
                    sourceAudioIds = this._mkvService.GetSourceTrackIds(sourceTracks, "audio", this._opts.KeepSourceAudioLangs, this._sourceAudioCodecPatterns);
                }
                if (this._filterSourceSubs)
                {
                    sourceSubIds = this._mkvService.GetSourceTrackIds(sourceTracks, "subtitles", this._opts.KeepSourceSubtitleLangs, null);
                }

                // Tracce lingua filtrate per target (solo in merge mode)
                if (this._needsMerge && langTracks != null)
                {
                    for (int t = 0; t < this._opts.TargetLanguage.Count; t++)
                    {
                        tl = this._opts.TargetLanguage[t];
                        if (!this._opts.SubOnly)
                        {
                            foundAudio = this._mkvService.GetFilteredTracks(langTracks, tl, "audio", this._codecPatterns);
                            for (int a = 0; a < foundAudio.Count; a++)
                            {
                                audioTracks.Add(foundAudio[a]);
                            }
                        }
                        if (!this._opts.AudioOnly)
                        {
                            foundSubs = this._mkvService.GetFilteredTracks(langTracks, tl, "subtitles", null);
                            for (int s = 0; s < foundSubs.Count; s++)
                            {
                                subtitleTracks.Add(foundSubs[s]);
                            }
                        }
                    }
                }

                // In merge mode serve almeno una traccia importata, altrimenti basta avere un'operazione remux
                bool hasWork = this._needsMerge ? (audioTracks.Count > 0 || subtitleTracks.Count > 0) : this._needsRemux;

                // Popola dettaglio tracce nel record per display
                record.KeptSourceAudioIds = sourceAudioIds;
                record.KeptSourceSubIds = sourceSubIds;
                record.ImportedAudioTracks = audioTracks;
                record.ImportedSubTracks = subtitleTracks;
                record.DisplayConvertFormat = this._opts.ConvertFormat;

                if (hasWork)
                {
                    // Calcola percorso output per preview (senza effetti collaterali)
                    outputPath = this.ComputeFinalOutputPath(record.SourceFilePath);

                    // Costruisci richiesta merge
                    MergeRequest mergeReq = new MergeRequest();
                    mergeReq.SourceFile = record.SourceFilePath;
                    mergeReq.LanguageFile = this._needsMerge ? record.LangFilePath : "";
                    mergeReq.OutputFile = outputPath;
                    mergeReq.SourceAudioIds = sourceAudioIds;
                    mergeReq.SourceAudioTracks = this.FilterTracksByIds(sourceTracks, sourceAudioIds);
                    mergeReq.SourceSubIds = sourceSubIds;
                    mergeReq.LangAudioTracks = audioTracks;
                    mergeReq.LangSubTracks = subtitleTracks;
                    mergeReq.AudioDelayMs = effectiveAudioDelay;
                    mergeReq.SubDelayMs = effectiveSubDelay;
                    mergeReq.FilterSourceAudio = this._filterSourceAudio;
                    mergeReq.FilterSourceSubs = this._filterSourceSubs;
                    mergeReq.StretchFactor = stretchFactor;
                    mergeReq.ConvertFormat = this._opts.ConvertFormat;
                    mergeReq.RenameAllTracks = this._opts.RenameAllTracks;
                    mergeReq.ConvertedSourceTracks = new Dictionary<int, string>();
                    mergeReq.ConvertedLangTracks = new Dictionary<int, string>();
                    mergeArgs = this._mkvService.BuildMergeArguments(mergeReq);

                    // Formatta comando preview
                    record.MergeCommand = this._mkvService.FormatMergeCommand(mergeArgs);
                }
            }
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
                finalOutput = this.ExecuteRemuxPhase(record, sourceTracks, effectiveAudioDelay, effectiveSubDelay);
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
                this.RunEncodingAndRecord(record, finalOutput);
            }

            // Notifica e cleanup
            if (started)
            {
                if (this.OnFileUpdated != null)
                {
                    this.OnFileUpdated(record);
                }
                this.ClearLogRedirect();
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
        /// Invalida la cache info file per forzare una rilettura
        /// </summary>
        public void ClearFileInfoCache()
        {
            this._fileInfoCache.Clear();
        }

        /// <summary>
        /// Genera la descrizione degli step della pipeline dalla configurazione
        /// </summary>
        /// <param name="opts">Opzioni di configurazione</param>
        /// <returns>Lista ordinata degli step della pipeline</returns>
        public static List<string> GetPipelineSteps(Options opts)
        {
            List<string> steps = new List<string>();
            bool needsMerge = (opts.TargetLanguage.Count > 0);
            bool needsFilter = (opts.KeepSourceAudioLangs.Count > 0 || opts.KeepSourceAudioCodec.Count > 0 || opts.KeepSourceSubtitleLangs.Count > 0);
            bool needsConvert = (opts.ConvertFormat.Length > 0);
            bool needsRemux = (needsMerge || needsFilter || needsConvert);
            bool needsEncode = (opts.EncodingProfileName.Length > 0);

            // Step 1: Scan sempre presente
            steps.Add("Scan file sorgente");

            // Step 2: Match lingua (solo merge)
            if (needsMerge)
            {
                steps.Add("Match file lingua");
            }

            // Step 3: Analisi sync (solo merge)
            if (needsMerge)
            {
                string analyzeDetail = "Analisi";
                if (opts.DeepAnalysis) { analyzeDetail += " + deep analysis"; }
                else if (opts.FrameSync) { analyzeDetail += " + frame-sync"; }
                steps.Add(analyzeDetail);
            }

            // Step 4: Conversione audio
            if (needsConvert)
            {
                steps.Add("Conversione audio (" + opts.ConvertFormat.ToUpper() + ")");
            }

            // Step 5: Remux
            if (needsRemux)
            {
                string remuxDetail = "Remux";
                if (needsMerge && needsFilter) { remuxDetail += " (merge + filtra)"; }
                else if (needsMerge) { remuxDetail += " (merge tracce)"; }
                else if (needsFilter) { remuxDetail += " (filtra sorgente)"; }
                steps.Add(remuxDetail);
            }

            // Step 6: Encoding video
            if (needsEncode)
            {
                EncodingProfile profile = AppSettingsService.Instance.GetProfile(opts.EncodingProfileName);
                string encDetail = "Encoding video";
                if (profile != null) { encDetail += " (" + profile.Codec + " " + profile.RateMode + " " + profile.CrfQp + ")"; }
                else { encDetail += " (" + opts.EncodingProfileName + ")"; }
                steps.Add(encDetail);
            }

            // Se nessuna operazione configurata
            if (!needsRemux && !needsEncode)
            {
                steps.Add("(nessuna operazione configurata)");
            }

            return steps;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Calcola le lingue audio e sottotitoli del file risultante
        /// </summary>
        /// <param name="allTracks">Lista completa delle tracce</param>
        /// <param name="ids">Lista di ID da filtrare</param>
        /// <returns>Lista di TrackInfo corrispondenti agli ID</returns>
        private List<TrackInfo> FilterTracksByIds(List<TrackInfo> allTracks, List<int> ids)
        {
            List<TrackInfo> result = new List<TrackInfo>();

            for (int i = 0; i < allTracks.Count; i++)
            {
                if (ids.Contains(allTracks[i].Id))
                {
                    result.Add(allTracks[i]);
                }
            }

            return result;
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
            List<string> resultAudioLangs = new List<string>();
            List<string> resultSubLangs = new List<string>();
            string lang = "";
            string srcLang = "";
            bool keepThis = false;

            // Audio dal sorgente
            if (!this._filterSourceAudio)
            {
                for (int i = 0; i < record.SourceAudioLangs.Count; i++)
                {
                    if (!resultAudioLangs.Contains(record.SourceAudioLangs[i]))
                    {
                        resultAudioLangs.Add(record.SourceAudioLangs[i]);
                    }
                }
            }
            else if (sourceTracks != null)
            {
                for (int i = 0; i < sourceTracks.Count; i++)
                {
                    if (!string.Equals(sourceTracks[i].Type, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (!sourceAudioIds.Contains(sourceTracks[i].Id))
                    {
                        continue;
                    }
                    lang = sourceTracks[i].Language.Length > 0 ? sourceTracks[i].Language : "und";
                    if (!resultAudioLangs.Contains(lang))
                    {
                        resultAudioLangs.Add(lang);
                    }
                }
            }

            // Audio importate dal file lingua
            for (int i = 0; i < audioTracks.Count; i++)
            {
                lang = audioTracks[i].Language.Length > 0 ? audioTracks[i].Language : "und";
                if (!resultAudioLangs.Contains(lang))
                {
                    resultAudioLangs.Add(lang);
                }
            }

            // Sottotitoli dal sorgente
            if (!this._filterSourceSubs)
            {
                for (int i = 0; i < record.SourceSubLangs.Count; i++)
                {
                    if (!resultSubLangs.Contains(record.SourceSubLangs[i]))
                    {
                        resultSubLangs.Add(record.SourceSubLangs[i]);
                    }
                }
            }
            else
            {
                for (int i = 0; i < record.SourceSubLangs.Count; i++)
                {
                    srcLang = record.SourceSubLangs[i];
                    keepThis = false;
                    for (int k = 0; k < this._opts.KeepSourceSubtitleLangs.Count; k++)
                    {
                        if (string.Equals(srcLang, this._opts.KeepSourceSubtitleLangs[k], StringComparison.OrdinalIgnoreCase))
                        {
                            keepThis = true;
                            break;
                        }
                    }
                    if (keepThis && !resultSubLangs.Contains(srcLang))
                    {
                        resultSubLangs.Add(srcLang);
                    }
                }
            }

            // Sottotitoli importati dal file lingua
            for (int i = 0; i < subtitleTracks.Count; i++)
            {
                lang = subtitleTracks[i].Language.Length > 0 ? subtitleTracks[i].Language : "und";
                if (!resultSubLangs.Contains(lang))
                {
                    resultSubLangs.Add(lang);
                }
            }

            record.ResultAudioLangs = resultAudioLangs;
            record.ResultSubLangs = resultSubLangs;
        }

        /// <summary>
        /// Esegue la fase remux: filtro tracce, raccolta lingua, conversione, merge
        /// </summary>
        /// <param name="record">Record del file</param>
        /// <param name="sourceTracks">Tracce file sorgente</param>
        /// <param name="effectiveAudioDelay">Delay audio effettivo in ms</param>
        /// <param name="effectiveSubDelay">Delay sottotitoli effettivo in ms</param>
        /// <returns>Percorso output finale o stringa vuota se errore</returns>
        private string ExecuteRemuxPhase(FileProcessingRecord record, List<TrackInfo> sourceTracks, int effectiveAudioDelay, int effectiveSubDelay)
        {
            string finalOutput = "";
            string tempOutput = "";
            List<int> sourceAudioIds = new List<int>();
            List<int> sourceSubIds = new List<int>();
            List<TrackInfo> audioTracks = new List<TrackInfo>();
            List<TrackInfo> subtitleTracks = new List<TrackInfo>();
            List<TrackInfo> langTracks = null;
            Dictionary<int, string> convertedSourceTracks = new Dictionary<int, string>();
            Dictionary<int, string> convertedLangTracks = new Dictionary<int, string>();
            Dictionary<int, string> processedLangSubTracks = new Dictionary<int, string>();
            HashSet<int> codecConvertedLangIds = new HashSet<int>();
            string stretchFactor = record.StretchFactor;
            List<string> mergeArgs = null;
            string delayInfo = "";
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
            // Conversione tracce lossless se attiva
            if (!done && this._opts.ConvertFormat.Length > 0 && this._ffmpegPath.Length > 0)
            {
                this.ConvertLosslessTracks(record, sourceTracks, sourceAudioIds, audioTracks, convertedSourceTracks, convertedLangTracks);

                // Salva ID tracce effettivamente convertite di codec (serve per rinominarle col formato target)
                foreach (int convertedId in convertedLangTracks.Keys)
                {
                    codecConvertedLangIds.Add(convertedId);
                }
            }

            // Deep analysis: applica EditMap alle tracce lang (taglia-cuci)
            if (!done && record.DeepAnalysisApplied && record.DeepAnalysisMap != null && record.DeepAnalysisMap.Operations.Count > 0 && this._ffmpegPath.Length > 0)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Applicazione taglia-cuci alle tracce lang...");

                string tempFolder = AppSettingsService.Instance.GetTempFolder();
                TrackSplitService splitService = new TrackSplitService(this._ffmpegPath, tempFolder);
                string splitLabel = Path.GetFileNameWithoutExtension(record.LangFilePath);
                string processedFile = "";

                // Processa tracce audio lang
                for (int a = 0; a < audioTracks.Count; a++)
                {
                    // Se la traccia e' gia' stata convertita (lossless), processa il file convertito
                    string audioInput = record.LangFilePath;
                    int audioTrackId = audioTracks[a].Id;

                    if (convertedLangTracks.ContainsKey(audioTrackId))
                    {
                        // Traccia gia' convertita: applica taglia-cuci al file convertito (track 0)
                        // Usa il codec convertito per generare silenzi compatibili
                        audioInput = convertedLangTracks[audioTrackId];
                        processedFile = splitService.ApplyEditMap(audioInput, 0, "audio", this._opts.ConvertFormat, audioTracks[a].Channels, audioTracks[a].SamplingFrequency, record.DeepAnalysisMap, splitLabel);
                    }
                    else
                    {
                        processedFile = splitService.ApplyEditMap(audioInput, audioTrackId, "audio", audioTracks[a].Codec, audioTracks[a].Channels, audioTracks[a].SamplingFrequency, record.DeepAnalysisMap, splitLabel);
                    }

                    if (processedFile.Length > 0)
                    {
                        // Sostituisci traccia convertita con quella processata
                        if (convertedLangTracks.ContainsKey(audioTrackId))
                        {
                            // Elimina il file convertito originale, sostituisci col processato
                            FileHelper.DeleteTempFile(convertedLangTracks[audioTrackId]);
                        }
                        convertedLangTracks[audioTrackId] = processedFile;
                    }
                    // Se processedFile vuoto: fallback, la traccia resta col solo delay iniziale
                }

                // Processa tracce sub lang
                for (int s = 0; s < subtitleTracks.Count; s++)
                {
                    processedFile = splitService.ApplyEditMap(record.LangFilePath, subtitleTracks[s].Id, "subtitles", subtitleTracks[s].Codec, 0, 0, record.DeepAnalysisMap, splitLabel);

                    if (processedFile.Length > 0)
                    {
                        processedLangSubTracks[subtitleTracks[s].Id] = processedFile;
                    }
                }

                // Se deep analysis ha operazioni, lo stretch e' gia' applicato nel file processato
                stretchFactor = "";
            }

            // Costruzione e esecuzione merge
            if (!done)
            {
                this.PrepareOutputPaths(record.SourceFilePath, out tempOutput, out finalOutput);

                MergeRequest mergeReq = new MergeRequest();
                mergeReq.SourceFile = record.SourceFilePath;
                mergeReq.LanguageFile = this._needsMerge ? record.LangFilePath : "";
                mergeReq.OutputFile = tempOutput;
                mergeReq.SourceAudioIds = sourceAudioIds;
                // Se nessun filtro attivo, passa tutte le tracce audio source per rename
                if (sourceAudioIds.Count > 0)
                {
                    mergeReq.SourceAudioTracks = this.FilterTracksByIds(sourceTracks, sourceAudioIds);
                }
                else if (this._opts.RenameAllTracks && sourceTracks != null)
                {
                    mergeReq.SourceAudioTracks = this.FilterTracksByType(sourceTracks, "audio");
                }
                mergeReq.SourceSubIds = sourceSubIds;
                mergeReq.LangAudioTracks = audioTracks;
                mergeReq.LangSubTracks = subtitleTracks;
                mergeReq.AudioDelayMs = effectiveAudioDelay;
                mergeReq.SubDelayMs = effectiveSubDelay;
                mergeReq.FilterSourceAudio = this._filterSourceAudio || convertedSourceTracks.Count > 0;
                mergeReq.FilterSourceSubs = this._filterSourceSubs;
                mergeReq.StretchFactor = stretchFactor;
                mergeReq.ConvertFormat = this._opts.ConvertFormat;
                mergeReq.RenameAllTracks = this._opts.RenameAllTracks;
                mergeReq.ConvertedSourceTracks = convertedSourceTracks;
                mergeReq.ConvertedLangTracks = convertedLangTracks;
                mergeReq.CodecConvertedLangIds = codecConvertedLangIds;
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
                record.DisplayConvertFormat = this._opts.ConvertFormat;

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
                this.RunMergeAndRecord(record, mergeArgs, tempOutput, finalOutput);

            }
            }
            finally
            {
                // Cleanup file convertiti temporanei
                foreach (KeyValuePair<int, string> kvp in convertedSourceTracks) { FileHelper.DeleteTempFile(kvp.Value); }
                foreach (KeyValuePair<int, string> kvp in convertedLangTracks) { FileHelper.DeleteTempFile(kvp.Value); }
                foreach (KeyValuePair<int, string> kvp in processedLangSubTracks) { FileHelper.DeleteTempFile(kvp.Value); }
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
            string finalOutput = "";
            string tempOutput = "";

            ConsoleHelper.Write(LogSection.Encode, LogLevel.Header, "Encode: " + record.SourceFileName);

            this.PrepareOutputPaths(record.SourceFilePath, out tempOutput, out finalOutput);

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
        /// Raccoglie tracce audio e sottotitoli dal file lingua
        /// </summary>
        /// <param name="record">Record del file</param>
        /// <param name="audioTracks">Tracce audio trovate (output)</param>
        /// <param name="subtitleTracks">Tracce sottotitoli trovate (output)</param>
        /// <returns>Lista tracce lingua o null se errore lettura</returns>
        private List<TrackInfo> CollectLanguageTracks(FileProcessingRecord record, out List<TrackInfo> audioTracks, out List<TrackInfo> subtitleTracks)
        {
            MkvFileInfo langInfo = null;
            List<TrackInfo> langTracks = null;
            List<TrackInfo> foundAudio = null;
            List<TrackInfo> foundSubs = null;
            string tl = "";

            audioTracks = new List<TrackInfo>();
            subtitleTracks = new List<TrackInfo>();

            langInfo = this.GetCachedFileInfo(record.LangFilePath);
            langTracks = (langInfo != null) ? langInfo.Tracks : null;

            if (langTracks == null)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Error, "  Impossibile leggere info tracce file lingua");
                record.ErrorMessage = "Impossibile leggere tracce file lingua";
                record.Status = FileStatus.Error;
            }
            else
            {
                for (int t = 0; t < this._opts.TargetLanguage.Count; t++)
                {
                    tl = this._opts.TargetLanguage[t];

                    // Tracce audio (a meno che SubOnly)
                    if (!this._opts.SubOnly)
                    {
                        foundAudio = this._mkvService.GetFilteredTracks(langTracks, tl, "audio", this._codecPatterns);
                        for (int a = 0; a < foundAudio.Count; a++)
                        {
                            audioTracks.Add(foundAudio[a]);
                        }
                    }

                    // Tracce sottotitoli (a meno che AudioOnly)
                    if (!this._opts.AudioOnly)
                    {
                        foundSubs = this._mkvService.GetFilteredTracks(langTracks, tl, "subtitles", null);
                        for (int s = 0; s < foundSubs.Count; s++)
                        {
                            subtitleTracks.Add(foundSubs[s]);
                        }
                    }
                }
            }

            return langTracks;
        }

        /// <summary>
        /// Converte tracce audio lossless nel formato specificato
        /// </summary>
        /// <param name="record">Record del file</param>
        /// <param name="sourceTracks">Tracce file sorgente</param>
        /// <param name="sourceAudioIds">ID tracce audio sorgente da mantenere</param>
        /// <param name="audioTracks">Tracce audio lingua importate</param>
        /// <param name="convertedSourceTracks">Mappa ID traccia sorgente a percorso file convertito (output)</param>
        /// <param name="convertedLangTracks">Mappa ID traccia lingua a percorso file convertito (output)</param>
        private void ConvertLosslessTracks(FileProcessingRecord record, List<TrackInfo> sourceTracks, List<int> sourceAudioIds, List<TrackInfo> audioTracks, Dictionary<int, string> convertedSourceTracks, Dictionary<int, string> convertedLangTracks)
        {
            string episodeLabel = record.EpisodeId.Length > 0 ? record.EpisodeId : "track";
            AudioConversionService convService = new AudioConversionService(this._ffmpegPath, AppSettingsService.Instance.GetTempFolder(), this._opts.ConvertFormat);
            string convertedFile = "";

            // Converti tracce audio sorgente
            if (sourceTracks != null)
            {
                // Se nessun filtro attivo, popola sourceAudioIds con tutte le tracce audio
                if (!this._filterSourceAudio)
                {
                    for (int i = 0; i < sourceTracks.Count; i++)
                    {
                        if (string.Equals(sourceTracks[i].Type, "audio", StringComparison.OrdinalIgnoreCase))
                        {
                            sourceAudioIds.Add(sourceTracks[i].Id);
                        }
                    }
                }

                for (int i = 0; i < sourceTracks.Count; i++)
                {
                    TrackInfo srcTrack = sourceTracks[i];
                    if (!string.Equals(srcTrack.Type, "audio", StringComparison.OrdinalIgnoreCase)) { continue; }
                    if (!sourceAudioIds.Contains(srcTrack.Id)) { continue; }
                    if (!CodecMapping.IsConvertibleLossless(srcTrack, this._opts.ConvertFormat)) { continue; }

                    ConsoleHelper.Write(LogSection.Conv, LogLevel.Phase, "  Sorgente traccia " + srcTrack.Id + " (" + srcTrack.Codec + " " + srcTrack.Channels + "ch)");
                    convertedFile = convService.ConvertTrack(record.SourceFilePath, srcTrack.Id, srcTrack.Channels, "src_" + episodeLabel);
                    if (convertedFile.Length > 0) { convertedSourceTracks[srcTrack.Id] = convertedFile; }
                    else { ConsoleHelper.Write(LogSection.Conv, LogLevel.Warning, "  Fallback: traccia " + srcTrack.Id + " non convertita, uso originale"); }
                }
            }

            // Converti tracce audio lingua importate
            for (int i = 0; i < audioTracks.Count; i++)
            {
                TrackInfo langTrack = audioTracks[i];
                if (!CodecMapping.IsConvertibleLossless(langTrack, this._opts.ConvertFormat)) { continue; }

                ConsoleHelper.Write(LogSection.Conv, LogLevel.Phase, "  Lingua traccia " + langTrack.Id + " (" + langTrack.Codec + " " + langTrack.Channels + "ch)");
                convertedFile = convService.ConvertTrack(record.LangFilePath, langTrack.Id, langTrack.Channels, "lang_" + episodeLabel);
                if (convertedFile.Length > 0) { convertedLangTracks[langTrack.Id] = convertedFile; }
                else { ConsoleHelper.Write(LogSection.Conv, LogLevel.Warning, "  Fallback: traccia " + langTrack.Id + " non convertita, uso originale"); }
            }

            if (convertedSourceTracks.Count > 0 || convertedLangTracks.Count > 0)
            {
                ConsoleHelper.Write(LogSection.Conv, LogLevel.Success, "  Convertite " + (convertedSourceTracks.Count + convertedLangTracks.Count) + " tracce");
            }
        }

        /// <summary>
        /// Calcola percorsi output e crea directory necessarie per esecuzione
        /// </summary>
        /// <param name="sourceFilePath">Percorso file sorgente</param>
        /// <param name="tempOutput">Percorso output temporaneo (output)</param>
        /// <param name="finalOutput">Percorso output finale (output)</param>
        private void PrepareOutputPaths(string sourceFilePath, out string tempOutput, out string finalOutput)
        {
            string sourceDir = "";
            string sourceNameNoExt = "";
            string normalizedSource = "";
            string normalizedFolder = "";
            string relativePath = "";
            string destDir = "";

            if (this._opts.Overwrite)
            {
                sourceDir = Path.GetDirectoryName(sourceFilePath);
                sourceNameNoExt = Path.GetFileNameWithoutExtension(sourceFilePath);
                tempOutput = Path.Combine(sourceDir, sourceNameNoExt + "_TEMP.mkv");
                finalOutput = sourceFilePath;
            }
            else
            {
                normalizedSource = this.NormalizePath(sourceFilePath);
                normalizedFolder = this.NormalizePath(this._opts.SourceFolder);
                relativePath = normalizedSource.Substring(normalizedFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                finalOutput = Path.Combine(this._opts.DestinationFolder, relativePath);
                tempOutput = finalOutput;

                destDir = Path.GetDirectoryName(finalOutput);
                if (!Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
            }
        }

        /// <summary>
        /// Calcola percorso output finale senza effetti collaterali (per preview)
        /// </summary>
        /// <param name="sourceFilePath">Percorso file sorgente</param>
        /// <returns>Percorso output finale</returns>
        private string ComputeFinalOutputPath(string sourceFilePath)
        {
            string finalOutput = "";
            string normalizedSource = "";
            string normalizedFolder = "";
            string relativePath = "";

            if (this._opts.Overwrite)
            {
                finalOutput = sourceFilePath;
            }
            else
            {
                normalizedSource = this.NormalizePath(sourceFilePath);
                normalizedFolder = this.NormalizePath(this._opts.SourceFolder);
                relativePath = normalizedSource.Substring(normalizedFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                finalOutput = Path.Combine(this._opts.DestinationFolder, relativePath);
            }

            return finalOutput;
        }

        /// <summary>
        /// Esegue merge mkvmerge e registra risultato nel record
        /// </summary>
        /// <param name="record">Record elaborazione corrente</param>
        /// <param name="mergeArgs">Argomenti merge</param>
        /// <param name="tempOutput">Percorso output temporaneo</param>
        /// <param name="finalOutput">Percorso output finale</param>
        private void RunMergeAndRecord(FileProcessingRecord record, List<string> mergeArgs, string tempOutput, string finalOutput)
        {
            Stopwatch mergeStopwatch = null;
            string mergeOutput = "";
            int exitCode = 0;
            FileInfo resultFileInfo = null;

            if (this._opts.DryRun)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Phase, "  [DRY-RUN] " + this._mkvService.FormatMergeCommand(mergeArgs));
                record.Success = true;
                record.Status = FileStatus.Done;
            }
            else
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Info, "  Unione in corso...");

                // Misura tempo merge
                mergeStopwatch = new Stopwatch();
                mergeStopwatch.Start();

                exitCode = this._mkvService.ExecuteMerge(mergeArgs, out mergeOutput);

                mergeStopwatch.Stop();
                record.MergeTimeMs = mergeStopwatch.ElapsedMilliseconds;

                // Exit code 0 e 1 sono entrambi considerati successo da mkvmerge
                if (exitCode == 0 || exitCode == 1)
                {
                    ConsoleHelper.Write(LogSection.Merge, LogLevel.Success, "  Unione completata (" + record.MergeTimeMs + "ms)");

                    // Gestisci modalita' overwrite
                    if (this._opts.Overwrite)
                    {
                        File.Delete(finalOutput);
                        File.Move(tempOutput, finalOutput);
                        ConsoleHelper.Write(LogSection.Merge, LogLevel.Success, "  File originale sostituito");
                    }

                    // Dimensione file risultato
                    if (File.Exists(finalOutput))
                    {
                        resultFileInfo = new FileInfo(finalOutput);
                        record.ResultSize = resultFileInfo.Length;
                    }

                    record.Success = true;
                    record.Status = FileStatus.Done;
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Merge, LogLevel.Error, "  mkvmerge fallito con codice " + exitCode);
                    if (mergeOutput.Length > 0)
                    {
                        ConsoleHelper.Write(LogSection.Merge, LogLevel.Error, "  Output: " + mergeOutput);
                    }

                    // Pulisci output temporaneo fallito
                    FileHelper.DeleteTempFile(tempOutput);

                    record.ErrorMessage = "Merge fallito: codice " + exitCode;
                    record.Status = FileStatus.Error;
                }
            }
        }

        /// <summary>
        /// Esegue encoding video post-merge e aggiorna il record
        /// </summary>
        /// <param name="record">Record da aggiornare</param>
        /// <param name="mergedFile">Percorso file MKV risultante dal merge</param>
        private void RunEncodingAndRecord(FileProcessingRecord record, string mergedFile)
        {
            EncodingProfile profile = null;
            VideoEncodingService encService = null;
            Stopwatch encStopwatch = null;
            FileInfo encodedInfo = null;
            bool encSuccess = false;

            // Cerca il profilo per nome
            profile = AppSettingsService.Instance.GetProfile(this._opts.EncodingProfileName);
            if (profile == null)
            {
                ConsoleHelper.Write(LogSection.Encode, LogLevel.Warning, "  Profilo '" + this._opts.EncodingProfileName + "' non trovato, encoding saltato");
                return;
            }

            // Aggiorna stato
            record.Status = FileStatus.Encoding;
            record.EncodingProfileName = profile.Name;
            if (this.OnFileUpdated != null)
            {
                this.OnFileUpdated(record);
            }

            // Costruisci comando leggibile per il record
            encService = new VideoEncodingService(this._ffmpegPath);
            record.EncodingCommand = encService.BuildCommandString(mergedFile, mergedFile, profile);

            ConsoleHelper.Write(LogSection.Encode, LogLevel.Info, "  Encoding con profilo '" + profile.Name + "' (" + profile.Codec + ")...");

            // Misura tempo encoding
            encStopwatch = new Stopwatch();
            encStopwatch.Start();

            // Esegui encoding con forward del progresso
            encSuccess = encService.Encode(mergedFile, mergedFile, profile, (string line) =>
            {
                ConsoleHelper.Write(LogSection.Encode, LogLevel.Debug, "  " + line);
            });

            encStopwatch.Stop();
            record.EncodingTimeMs = encStopwatch.ElapsedMilliseconds;

            if (encSuccess)
            {
                // Aggiorna dimensione file encoded
                if (File.Exists(mergedFile))
                {
                    encodedInfo = new FileInfo(mergedFile);
                    record.EncodedSize = encodedInfo.Length;
                }

                ConsoleHelper.Write(LogSection.Encode, LogLevel.Success, "  Encoding completato (" + record.EncodingTimeMs + "ms)");
                record.Success = true;
                record.Status = FileStatus.Done;
            }
            else
            {
                ConsoleHelper.Write(LogSection.Encode, LogLevel.Error, "  Encoding fallito");
                record.ErrorMessage = "Encoding fallito con profilo " + profile.Name;
                record.Status = FileStatus.Error;
            }
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
        /// Estrae l'identificatore episodio da un nome file usando il pattern regex
        /// </summary>
        /// <param name="fileName">Nome file da cui estrarre</param>
        /// <param name="pattern">Pattern regex con gruppi di cattura</param>
        /// <returns>Identificatore episodio o stringa vuota</returns>
        private string GetEpisodeIdentifier(string fileName, string pattern)
        {
            string result = "";

            Match match = Regex.Match(fileName, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                if (match.Groups.Count > 1)
                {
                    // Unisci tutti i gruppi di cattura con underscore
                    StringBuilder sb = new StringBuilder();
                    for (int g = 1; g < match.Groups.Count; g++)
                    {
                        if (g > 1)
                        {
                            sb.Append("_");
                        }
                        sb.Append(match.Groups[g].Value);
                    }
                    result = sb.ToString();
                }
                else
                {
                    // Nessun gruppo di cattura, usa il match completo
                    result = match.Value;
                }
            }

            return result;
        }

        /// <summary>
        /// Cerca file video in una cartella per estensione
        /// </summary>
        /// <param name="folder">Cartella dove cercare</param>
        /// <param name="extensions">Lista estensioni senza punto</param>
        /// <param name="recursive">Se cercare nelle sottocartelle</param>
        /// <returns>Lista percorsi completi ai file trovati</returns>
        private List<string> FindVideoFiles(string folder, List<string> extensions, bool recursive)
        {
            List<string> files = new List<string>();
            SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            for (int e = 0; e < extensions.Count; e++)
            {
                string pattern = "*." + extensions[e];
                string[] found = Directory.GetFiles(folder, pattern, searchOption);
                for (int i = 0; i < found.Length; i++)
                {
                    files.Add(found[i]);
                }
            }

            return files;
        }

        /// <summary>
        /// Filtra le tracce per tipo (audio, subtitles, video)
        /// </summary>
        /// <param name="tracks">Lista completa tracce</param>
        /// <param name="trackType">Tipo traccia da filtrare</param>
        /// <returns>Lista tracce del tipo specificato</returns>
        private List<TrackInfo> FilterTracksByType(List<TrackInfo> tracks, string trackType)
        {
            List<TrackInfo> result = new List<TrackInfo>();

            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (string.Equals(tracks[i].Type, trackType, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(tracks[i]);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Estrae le lingue uniche delle tracce audio
        /// </summary>
        /// <param name="tracks">Lista tracce</param>
        /// <returns>Lista codici lingua unici</returns>
        private List<string> GetAudioLanguages(List<TrackInfo> tracks)
        {
            List<string> langs = new List<string>();

            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (string.Equals(tracks[i].Type, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        string lang = tracks[i].Language.Length > 0 ? tracks[i].Language : "und";
                        if (!langs.Contains(lang))
                        {
                            langs.Add(lang);
                        }
                    }
                }
            }

            return langs;
        }

        /// <summary>
        /// Estrae le lingue uniche delle tracce sottotitoli
        /// </summary>
        /// <param name="tracks">Lista tracce</param>
        /// <returns>Lista codici lingua unici</returns>
        private List<string> GetSubtitleLanguages(List<TrackInfo> tracks)
        {
            List<string> langs = new List<string>();

            if (tracks != null)
            {
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (string.Equals(tracks[i].Type, "subtitles", StringComparison.OrdinalIgnoreCase))
                    {
                        string lang = tracks[i].Language.Length > 0 ? tracks[i].Language : "und";
                        if (!langs.Contains(lang))
                        {
                            langs.Add(lang);
                        }
                    }
                }
            }

            return langs;
        }

        /// <summary>
        /// Imposta il redirect log di ConsoleHelper verso il record e l'evento
        /// </summary>
        /// <param name="record">Record in cui salvare i log</param>
        private void SetupLogRedirect(FileProcessingRecord record)
        {
            bool inCallback = false;
            ConsoleHelper.SetLogCallback((LogSection section, LogLevel level, string text) =>
            {
                // Guard contro ri-entranza (OnLogMessage potrebbe chiamare Write)
                if (inCallback)
                {
                    // Ri-entranza: scrivi direttamente su console
                    ConsoleColor color = ConsoleHelper.MapLevelToColor(level);
                    ConsoleColor original = Console.ForegroundColor;
                    Console.ForegroundColor = color;
                    Console.WriteLine(text);
                    Console.ForegroundColor = original;
                    return;
                }
                inCallback = true;

                // Salva nel log del record con prefisso sezione
                record.AnalysisLog.Add(ConsoleHelper.FormatSectionPrefix(section) + text);
                // Invia all'evento per UI (TUI/CLI)
                if (this.OnLogMessage != null)
                {
                    this.OnLogMessage(section, level, text);
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
            MkvFileInfo info = null;

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
        /// Opzioni correnti di configurazione
        /// </summary>
        public Options CurrentOptions { get { return this._opts; } }

        /// <summary>
        /// Pattern codec risolti per filtro tracce lingua importate
        /// </summary>
        public string[] CodecPatterns { get { return this._codecPatterns; } }

        #endregion
    }
}
