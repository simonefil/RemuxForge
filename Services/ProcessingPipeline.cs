using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MergeLanguageTracks
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
        /// Indice file lingua: mappa episodeId a percorso completo
        /// </summary>
        private Dictionary<string, string> _languageIndex;

        /// <summary>
        /// Cache info file MKV per evitare letture ripetute
        /// </summary>
        private Dictionary<string, MkvFileInfo> _fileInfoCache;

        #endregion

        #region Eventi

        /// <summary>
        /// Evento emesso per ogni messaggio di log durante elaborazione
        /// </summary>
        public event Action<string, ConsoleColor> OnLogMessage;

        /// <summary>
        /// Evento emesso quando un record file viene aggiornato
        /// </summary>
        public event Action<FileProcessingRecord> OnFileUpdated;

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
            this._languageIndex = new Dictionary<string, string>();
            this._fileInfoCache = new Dictionary<string, MkvFileInfo>();
        }

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
            string appDir = "";
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

            // Modalita' singola sorgente
            if (this._opts.LanguageFolder.Length == 0 && this._opts.SourceFolder.Length > 0)
            {
                this._opts.LanguageFolder = this._opts.SourceFolder;
            }

            // Cartella tools di default
            if (this._opts.ToolsFolder.Length == 0)
            {
                appDir = AppContext.BaseDirectory;
                this._opts.ToolsFolder = Path.Combine(appDir, "tools");
            }

            // Verifica parametri obbligatori
            if (this._opts.SourceFolder.Length == 0 || this._opts.TargetLanguage.Count == 0)
            {
                this.Log("Errore: parametri obbligatori mancanti (source e target-language)", ConsoleColor.Red);
                success = false;
            }

            // Valida esistenza cartella sorgente
            if (success && !Directory.Exists(this._opts.SourceFolder))
            {
                this.Log("Errore: cartella sorgente non trovata: " + this._opts.SourceFolder, ConsoleColor.Red);
                success = false;
            }

            // Valida esistenza cartella lingua
            if (success && !Directory.Exists(this._opts.LanguageFolder))
            {
                this.Log("Errore: cartella lingua non trovata: " + this._opts.LanguageFolder, ConsoleColor.Red);
                success = false;
            }

            // Valida formato codice lingua
            if (success)
            {
                for (int i = 0; i < this._opts.TargetLanguage.Count && success; i++)
                {
                    if (!langRegex.IsMatch(this._opts.TargetLanguage[i].ToLower()))
                    {
                        this.Log("Errore: lingua non valida '" + this._opts.TargetLanguage[i] + "'. Usa codice ISO 639-2", ConsoleColor.Red);
                        success = false;
                    }
                }
            }

            // Valida lingue target contro lista ISO 639-2
            if (success)
            {
                for (int i = 0; i < this._opts.TargetLanguage.Count && success; i++)
                {
                    if (!LanguageValidator.IsValid(this._opts.TargetLanguage[i]))
                    {
                        this.Log("Errore: lingua '" + this._opts.TargetLanguage[i] + "' non riconosciuta", ConsoleColor.Red);
                        suggestions = LanguageValidator.GetSimilar(this._opts.TargetLanguage[i], 3);
                        if (suggestions.Count > 0)
                        {
                            this.Log("Forse intendevi: " + string.Join(", ", suggestions) + "?", ConsoleColor.Yellow);
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
                        this.Log("Errore: lingua '" + this._opts.KeepSourceAudioLangs[i] + "' in keep-source-audio non riconosciuta", ConsoleColor.Red);
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
                        this.Log("Errore: lingua '" + this._opts.KeepSourceSubtitleLangs[i] + "' in keep-source-subs non riconosciuta", ConsoleColor.Red);
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
                        this.Log("Errore: codec '" + this._opts.AudioCodec[i] + "' non riconosciuto. Validi: " + CodecMapping.GetAllCodecNames(), ConsoleColor.Red);
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
                        this.Log("Errore: codec '" + this._opts.KeepSourceAudioCodec[i] + "' in keep-source-audio-codec non riconosciuto", ConsoleColor.Red);
                        success = false;
                    }
                }
            }

            // Valida mutua esclusione SubOnly e AudioOnly
            if (success && this._opts.SubOnly && this._opts.AudioOnly)
            {
                this.Log("Errore: sub-only e audio-only non possono essere usati insieme", ConsoleColor.Red);
                success = false;
            }

            // Valida modalita' output
            if (success && this._opts.Overwrite && this._opts.DestinationFolder.Length > 0)
            {
                this.Log("Errore: overwrite e destination non possono essere usati insieme", ConsoleColor.Red);
                success = false;
            }
            if (success && !this._opts.Overwrite && this._opts.DestinationFolder.Length == 0)
            {
                this.Log("Errore: specificare destination oppure overwrite", ConsoleColor.Red);
                success = false;
            }

            if (success)
            {
                // Crea cartella destinazione se necessario
                if (!this._opts.Overwrite && !Directory.Exists(this._opts.DestinationFolder))
                {
                    this.Log("Creazione cartella destinazione: " + this._opts.DestinationFolder, ConsoleColor.Yellow);
                    Directory.CreateDirectory(this._opts.DestinationFolder);
                }

                // Risolvi pattern codec
                this._codecPatterns = this.ResolveCodecPatterns(this._opts.AudioCodec);
                this._sourceAudioCodecPatterns = this.ResolveCodecPatterns(this._opts.KeepSourceAudioCodec);

                // Flag filtraggio tracce sorgente
                this._filterSourceAudio = (this._opts.KeepSourceAudioLangs.Count > 0 || this._opts.KeepSourceAudioCodec.Count > 0);
                this._filterSourceSubs = (this._opts.KeepSourceSubtitleLangs.Count > 0);

                // Verifica mkvmerge
                tempService = new MkvToolsService(this._opts.MkvMergePath);
                if (!tempService.VerifyMkvMerge())
                {
                    this.Log("mkvmerge non trovato. Installa MKVToolNix o specifica il percorso", ConsoleColor.Red);
                    success = false;
                }
                else
                {
                    this._mkvService = tempService;
                    this.Log("Trovato mkvmerge: " + this._opts.MkvMergePath, ConsoleColor.Green);

                    // Risolvi ffmpeg (tentato sempre per supportare speed correction automatica)
                    ffmpegProvider = new FfmpegProvider(this._opts.ToolsFolder);
                    if (ffmpegProvider.Resolve())
                    {
                        this._ffmpegPath = ffmpegProvider.FfmpegPath;
                        this.Log("Trovato ffmpeg: " + this._ffmpegPath, ConsoleColor.Green);
                    }
                    else if (this._opts.FrameSync)
                    {
                        // ffmpeg richiesto per frame-sync
                        this.Log("ffmpeg non trovato e impossibile scaricarlo. Necessario per frame-sync", ConsoleColor.Red);
                        success = false;
                    }

                    // Crea servizio frame-sync
                    if (success && this._opts.FrameSync && this._ffmpegPath.Length > 0)
                    {
                        this._frameSyncService = new FrameSyncService(this._ffmpegPath);
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
            List<FileProcessingRecord> records = new List<FileProcessingRecord>();

            // Trova file sorgente
            string extList = string.Join(", ", this._opts.FileExtensions);
            List<string> sourceFiles = this.FindVideoFiles(this._opts.SourceFolder, this._opts.FileExtensions, this._opts.Recursive);
            this.Log("Trovati " + sourceFiles.Count + " file sorgente (" + extList + ")", ConsoleColor.Green);

            // Costruisci indice file lingua
            this.Log("Indicizzazione cartella lingua...", ConsoleColor.Yellow);
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

            this.Log("Indicizzati " + this._languageIndex.Count + " file lingua", ConsoleColor.Green);

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

                if (episodeId.Length == 0)
                {
                    // Nessun ID episodio estratto
                    record.SkipReason = "No episode ID";
                    record.Status = FileStatus.Skipped;
                    records.Add(record);
                    continue;
                }

                record.EpisodeId = episodeId;

                // Trova file lingua corrispondente
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

                ConsoleHelper.WriteWhite("Analisi: " + record.SourceFileName);
                ConsoleHelper.WriteDarkGray("  ID Episodio: " + record.EpisodeId);
                ConsoleHelper.WriteDarkCyan("  Match: " + record.LangFileName);

                // Ottieni info file da mkvmerge
                sourceInfo = this.GetCachedFileInfo(record.SourceFilePath);
                langInfo = this.GetCachedFileInfo(record.LangFilePath);

                sourceTracks = (sourceInfo != null) ? sourceInfo.Tracks : null;
                langTracks = (langInfo != null) ? langInfo.Tracks : null;

                // Popola lingue nel record
                record.SourceAudioLangs = this.GetAudioLanguages(sourceTracks);
                record.SourceSubLangs = this.GetSubtitleLanguages(sourceTracks);
                record.LangAudioLangs = this.GetAudioLanguages(langTracks);
                record.LangSubLangs = this.GetSubtitleLanguages(langTracks);

                if (langTracks == null)
                {
                    ConsoleHelper.WriteRed("  Impossibile leggere info tracce file lingua");
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

            // Rilevamento automatico mismatch velocita'
            if (!done && sourceInfo != null && langInfo != null)
            {
                speedMismatch = SpeedCorrectionService.DetectSpeedMismatch(sourceInfo, langInfo, out detectedSourceFps, out detectedLangFps);

                if (speedMismatch)
                {
                    ConsoleHelper.WriteCyan("  [SPEED] Mismatch velocita': source " + detectedSourceFps.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "fps, lang " + detectedLangFps.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + "fps");

                    // Risolvi ffmpeg se non ancora disponibile
                    ffmpegPath = this._ffmpegPath;
                    if (ffmpegPath.Length == 0)
                    {
                        ConsoleHelper.WriteDarkYellow("  [SPEED] Risoluzione ffmpeg per frame matching...");
                        ffmpegProvider = new FfmpegProvider(this._opts.ToolsFolder);
                        if (ffmpegProvider.Resolve())
                        {
                            ffmpegPath = ffmpegProvider.FfmpegPath;
                            this._ffmpegPath = ffmpegPath;
                            ConsoleHelper.WriteGreen("  [SPEED] ffmpeg trovato: " + ffmpegPath);
                        }
                        else
                        {
                            ConsoleHelper.WriteWarning("  [SPEED] ffmpeg non disponibile, correzione velocita' saltata");
                        }
                    }

                    if (ffmpegPath.Length > 0)
                    {
                        // Trova default_duration per tracce video
                        for (int t = 0; t < sourceInfo.Tracks.Count; t++)
                        {
                            if (string.Equals(sourceInfo.Tracks[t].Type, "video", StringComparison.OrdinalIgnoreCase) && sourceInfo.Tracks[t].DefaultDurationNs > 0)
                            {
                                sourceDefaultDuration = sourceInfo.Tracks[t].DefaultDurationNs;
                                break;
                            }
                        }
                        for (int t = 0; t < langInfo.Tracks.Count; t++)
                        {
                            if (string.Equals(langInfo.Tracks[t].Type, "video", StringComparison.OrdinalIgnoreCase) && langInfo.Tracks[t].DefaultDurationNs > 0)
                            {
                                langDefaultDuration = langInfo.Tracks[t].DefaultDurationNs;
                                break;
                            }
                        }

                        // Durata sorgente in ms dal container
                        sourceDurationMs = (int)(sourceInfo.ContainerDurationNs / 1000000);

                        speedService = new SpeedCorrectionService(ffmpegPath);
                        speedOk = speedService.FindDelayAndVerify(record.SourceFilePath, record.LangFilePath, sourceDefaultDuration, langDefaultDuration, sourceDurationMs);

                        record.SpeedCorrectionTimeMs = speedService.ExecutionTimeMs;

                        if (speedOk)
                        {
                            syncOffset = speedService.SyncDelayMs;
                            record.StretchFactor = speedService.StretchFactor;
                            record.SpeedCorrectionApplied = true;
                            speedCorrectionActive = true;

                            ConsoleHelper.WriteGreen("  [SPEED] Correzione: delay=" + speedService.InitialDelayMs + "ms, sync=" + speedService.SyncDelayMs + "ms, stretch=" + speedService.StretchFactor + " (" + speedService.ExecutionTimeMs + "ms)");
                            ConsoleHelper.WriteDarkGray("  [SPEED] Verifica: " + speedService.GetDetailSummary());
                        }
                        else
                        {
                            ConsoleHelper.WriteRed("  [SPEED] Correzione velocita' fallita");
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

            // Frame-sync solo se non in correzione velocita'
            if (!done && !speedCorrectionActive && this._opts.FrameSync && this._frameSyncService != null)
            {
                ConsoleHelper.WriteCyan("  [FRAME-SYNC] Sincronizzazione tramite confronto visivo...");

                frameSyncOffset = this._frameSyncService.RefineOffset(record.SourceFilePath, record.LangFilePath);
                record.FrameSyncTimeMs = this._frameSyncService.FrameSyncTimeMs;

                if (frameSyncOffset != int.MinValue)
                {
                    syncOffset = frameSyncOffset;
                    ConsoleHelper.WriteGreen("  [FRAME-SYNC] Offset: " + this.FormatDelay(frameSyncOffset) + " (tempo: " + this._frameSyncService.FrameSyncTimeMs + "ms)");
                    ConsoleHelper.WriteDarkGray("  [FRAME-SYNC] Dettaglio: " + this._frameSyncService.GetDetailSummary());
                }
                else
                {
                    ConsoleHelper.WriteRed("  [FRAME-SYNC] Sincronizzazione fallita");
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
                ConsoleHelper.WriteGreen("  Analisi completata: delay audio " + this.FormatDelay(record.AudioDelayApplied) + ", sub " + this.FormatDelay(record.SubDelayApplied));

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
            MkvFileInfo langInfo = null;
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
            string normalizedSource = "";
            string normalizedFolder = "";
            string relativePath = "";
            string sourceDir = "";
            string sourceNameNoExt = "";
            string sourceExt = "";
            List<string> mergeArgs = null;
            string cmdArgs = "";
            string arg = "";
            bool needsQuote = false;

            // Ottieni info tracce
            sourceInfo = this.GetCachedFileInfo(record.SourceFilePath);
            langInfo = this.GetCachedFileInfo(record.LangFilePath);
            sourceTracks = (sourceInfo != null) ? sourceInfo.Tracks : null;
            langTracks = (langInfo != null) ? langInfo.Tracks : null;

            // Procedi solo se entrambe le info tracce sono disponibili
            if (langTracks != null && sourceTracks != null)
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

                // Tracce lingua filtrate per target
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

                // Procedi solo se almeno una traccia trovata
                if (audioTracks.Count > 0 || subtitleTracks.Count > 0)
                {
                    // Calcola percorso output
                    if (this._opts.Overwrite)
                    {
                        outputPath = record.SourceFilePath;
                    }
                    else if (this._opts.DestinationFolder.Length > 0)
                    {
                        normalizedSource = this.NormalizePath(record.SourceFilePath);
                        normalizedFolder = this.NormalizePath(this._opts.SourceFolder);
                        relativePath = normalizedSource.Substring(normalizedFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        outputPath = Path.Combine(this._opts.DestinationFolder, relativePath);
                    }
                    else
                    {
                        sourceDir = Path.GetDirectoryName(record.SourceFilePath);
                        sourceNameNoExt = Path.GetFileNameWithoutExtension(record.SourceFilePath);
                        sourceExt = Path.GetExtension(record.SourceFilePath);
                        outputPath = Path.Combine(sourceDir, sourceNameNoExt + "_merged" + sourceExt);
                    }

                    // Costruisci richiesta merge
                    MergeRequest mergeReq = new MergeRequest();
                    mergeReq.SourceFile = record.SourceFilePath;
                    mergeReq.LanguageFile = record.LangFilePath;
                    mergeReq.OutputFile = outputPath;
                    mergeReq.SourceAudioIds = sourceAudioIds;
                    mergeReq.SourceSubIds = sourceSubIds;
                    mergeReq.LangAudioTracks = audioTracks;
                    mergeReq.LangSubTracks = subtitleTracks;
                    mergeReq.AudioDelayMs = effectiveAudioDelay;
                    mergeReq.SubDelayMs = effectiveSubDelay;
                    mergeReq.FilterSourceAudio = this._filterSourceAudio;
                    mergeReq.FilterSourceSubs = this._filterSourceSubs;
                    mergeReq.StretchFactor = stretchFactor;
                    mergeArgs = this._mkvService.BuildMergeArguments(mergeReq);

                    // Formatta comando
                    for (int i = 0; i < mergeArgs.Count; i++)
                    {
                        arg = mergeArgs[i];
                        needsQuote = arg.Contains(" ") || arg.Contains("\\") || arg.Contains("/");
                        if (i > 0) cmdArgs += " ";
                        if (needsQuote) cmdArgs += "\"" + arg + "\"";
                        else cmdArgs += arg;
                    }
                    record.MergeCommand = this._opts.MkvMergePath + " " + cmdArgs;
                }
            }
        }

        /// <summary>
        /// Esegue il merge di un singolo file
        /// </summary>
        /// <param name="record">Record del file da unire</param>
        public void MergeFile(FileProcessingRecord record)
        {
            bool done = false;
            int effectiveAudioDelay = 0;
            int effectiveSubDelay = 0;
            string stretchFactor = "";
            MkvFileInfo sourceInfo = null;
            MkvFileInfo langInfo = null;
            List<TrackInfo> sourceTracks = null;
            List<TrackInfo> langTracks = null;
            List<int> sourceAudioIds = new List<int>();
            List<int> sourceSubIds = new List<int>();
            List<TrackInfo> audioTracks = new List<TrackInfo>();
            List<TrackInfo> subtitleTracks = new List<TrackInfo>();
            List<TrackInfo> foundAudio = null;
            List<TrackInfo> foundSubs = null;
            string tl = "";
            string tempOutput = "";
            string finalOutput = "";
            string sourceDir = "";
            string sourceNameNoExt = "";
            string normalizedSource = "";
            string normalizedFolder = "";
            string relativePath = "";
            string destDir = "";
            List<string> mergeArgs = null;
            string delayInfo = "";

            // Verifica stato
            if (record.Status != FileStatus.Analyzed)
            {
                done = true;
            }

            if (!done)
            {
                // Imposta redirect log
                this.SetupLogRedirect(record);

                // Aggiorna stato
                record.Status = FileStatus.Processing;
                if (this.OnFileUpdated != null)
                {
                    this.OnFileUpdated(record);
                }

                // Ricalcola delay effettivi (l'utente potrebbe aver modificato i delay manuali)
                effectiveAudioDelay = record.SyncOffsetMs + this._opts.AudioDelay + record.ManualAudioDelayMs;
                effectiveSubDelay = record.SyncOffsetMs + this._opts.SubtitleDelay + record.ManualSubDelayMs;
                record.AudioDelayApplied = effectiveAudioDelay;
                record.SubDelayApplied = effectiveSubDelay;
                stretchFactor = record.StretchFactor;

                ConsoleHelper.WriteWhite("Merge: " + record.SourceFileName);

                // Ottieni info file
                sourceInfo = this.GetCachedFileInfo(record.SourceFilePath);
                langInfo = this.GetCachedFileInfo(record.LangFilePath);
                sourceTracks = (sourceInfo != null) ? sourceInfo.Tracks : null;
                langTracks = (langInfo != null) ? langInfo.Tracks : null;

                if (langTracks == null)
                {
                    // Tracce non leggibili
                    ConsoleHelper.WriteRed("  Impossibile leggere info tracce");
                    record.ErrorMessage = "Impossibile leggere tracce";
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
                // Ottieni ID tracce sorgente da mantenere
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

                // Raccogli tracce dal file lingua per tutte le lingue target
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

                // Nessuna traccia trovata
                if (audioTracks.Count == 0 && subtitleTracks.Count == 0)
                {
                    ConsoleHelper.WriteYellow("  Nessuna traccia corrispondente trovata");
                    record.SkipReason = "No matching tracks";
                    record.ErrorMessage = "Nessuna traccia corrispondente";
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
                // Determina percorso output
                if (this._opts.Overwrite)
                {
                    // File temporaneo nella stessa cartella, poi sostituisce originale
                    sourceDir = Path.GetDirectoryName(record.SourceFilePath);
                    sourceNameNoExt = Path.GetFileNameWithoutExtension(record.SourceFilePath);
                    tempOutput = Path.Combine(sourceDir, sourceNameNoExt + "_TEMP.mkv");
                    finalOutput = record.SourceFilePath;
                }
                else
                {
                    // Preserva struttura directory nella destinazione
                    normalizedSource = this.NormalizePath(record.SourceFilePath);
                    normalizedFolder = this.NormalizePath(this._opts.SourceFolder);
                    relativePath = normalizedSource.Substring(normalizedFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    finalOutput = Path.Combine(this._opts.DestinationFolder, relativePath);
                    tempOutput = finalOutput;

                    // Crea sottodirectory destinazione se necessario
                    destDir = Path.GetDirectoryName(finalOutput);
                    if (!Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }
                }

                // Costruisci richiesta merge
                MergeRequest mergeReq = new MergeRequest();
                mergeReq.SourceFile = record.SourceFilePath;
                mergeReq.LanguageFile = record.LangFilePath;
                mergeReq.OutputFile = tempOutput;
                mergeReq.SourceAudioIds = sourceAudioIds;
                mergeReq.SourceSubIds = sourceSubIds;
                mergeReq.LangAudioTracks = audioTracks;
                mergeReq.LangSubTracks = subtitleTracks;
                mergeReq.AudioDelayMs = effectiveAudioDelay;
                mergeReq.SubDelayMs = effectiveSubDelay;
                mergeReq.FilterSourceAudio = this._filterSourceAudio;
                mergeReq.FilterSourceSubs = this._filterSourceSubs;
                mergeReq.StretchFactor = stretchFactor;
                mergeArgs = this._mkvService.BuildMergeArguments(mergeReq);

                // Aggiorna comando nel record (delay manuali potrebbero essere cambiati)
                this.BuildMergeCommand(record);

                record.ResultFileName = Path.GetFileName(finalOutput);

                // Calcola lingue risultato
                this.PopulateResultLanguages(record, sourceTracks, sourceAudioIds, audioTracks, subtitleTracks);

                // Log info merge
                ConsoleHelper.WriteDarkGray("  Output: " + finalOutput);
                delayInfo = "  Delay: Audio " + this.FormatDelay(effectiveAudioDelay) + ", Sub " + this.FormatDelay(effectiveSubDelay);
                if (stretchFactor.Length > 0)
                {
                    delayInfo += ", stretch: " + stretchFactor;
                }
                ConsoleHelper.WriteDarkGray(delayInfo);

                // Esegui merge e registra risultato
                this.RunMergeAndRecord(record, mergeArgs, tempOutput, finalOutput);

                if (this.OnFileUpdated != null)
                {
                    this.OnFileUpdated(record);
                }

                this.ClearLogRedirect();
            }
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

        #endregion

        #region Metodi privati

        /// <summary>
        /// Calcola le lingue audio e sottotitoli del file risultante
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
                ConsoleHelper.WriteCyan("  [DRY-RUN] " + this._mkvService.FormatMergeCommand(mergeArgs));
                record.Success = true;
                record.Status = FileStatus.Done;
            }
            else
            {
                ConsoleHelper.WriteYellow("  Unione in corso...");

                // Misura tempo merge
                mergeStopwatch = new Stopwatch();
                mergeStopwatch.Start();

                exitCode = this._mkvService.ExecuteMerge(mergeArgs, out mergeOutput);

                mergeStopwatch.Stop();
                record.MergeTimeMs = mergeStopwatch.ElapsedMilliseconds;

                // Exit code 0 e 1 sono entrambi considerati successo da mkvmerge
                if (exitCode == 0 || exitCode == 1)
                {
                    ConsoleHelper.WriteGreen("  Unione completata (" + record.MergeTimeMs + "ms)");

                    // Gestisci modalita' overwrite
                    if (this._opts.Overwrite)
                    {
                        File.Delete(finalOutput);
                        File.Move(tempOutput, finalOutput);
                        ConsoleHelper.WriteGreen("  File originale sostituito");
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
                    ConsoleHelper.WriteRed("  mkvmerge fallito con codice " + exitCode);
                    if (mergeOutput.Length > 0)
                    {
                        ConsoleHelper.WriteDarkRed("  Output: " + mergeOutput);
                    }

                    // Pulisci output temporaneo fallito
                    if (File.Exists(tempOutput))
                    {
                        // Cleanup best-effort, errore ignorato
                        try { File.Delete(tempOutput); } catch { }
                    }

                    record.ErrorMessage = "Merge fallito: codice " + exitCode;
                    record.Status = FileStatus.Error;
                }
            }
        }

        /// <summary>
        /// Invia un messaggio di log tramite l'evento OnLogMessage
        /// </summary>
        /// <param name="text">Testo del messaggio</param>
        /// <param name="color">Colore del messaggio</param>
        private void Log(string text, ConsoleColor color)
        {
            if (this.OnLogMessage != null)
            {
                this.OnLogMessage(text, color);
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
        /// Formatta un valore di ritardo in millisecondi per visualizzazione
        /// </summary>
        /// <param name="delayMs">Ritardo in millisecondi</param>
        /// <returns>Stringa formattata con segno</returns>
        private string FormatDelay(int delayMs)
        {
            string result = "0ms";

            if (delayMs > 0)
            {
                result = "+" + delayMs + "ms";
            }
            else if (delayMs < 0)
            {
                result = delayMs + "ms";
            }

            return result;
        }

        /// <summary>
        /// Imposta il redirect log di ConsoleHelper verso il record e l'evento
        /// </summary>
        /// <param name="record">Record in cui salvare i log</param>
        private void SetupLogRedirect(FileProcessingRecord record)
        {
            ConsoleHelper.SetLogCallback((string text, ConsoleColor color) =>
            {
                // Salva nel log del record
                record.AnalysisLog.Add(text);
                // Invia all'evento per la TUI
                this.Log(text, color);
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

        #endregion
    }
}
