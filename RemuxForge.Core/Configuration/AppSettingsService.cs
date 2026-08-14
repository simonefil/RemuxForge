using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RemuxForge.Core.Configuration
{
    /// <summary>
    /// Servizio singleton per gestione impostazioni applicazione
    /// </summary>
    public class AppSettingsService
    {
        #region Costanti

        /// <summary>
        /// Nome della cartella di configurazione nascosta
        /// </summary>
        private const string CONFIG_FOLDER_NAME = ".remux-forge";

        /// <summary>
        /// Nome del file di configurazione
        /// </summary>
        private const string CONFIG_FILE_NAME = "appsettings.json";

        /// <summary>
        /// Nome della sottocartella per file temporanei di conversione
        /// </summary>
        public const string TEMP_FOLDER_NAME = "temp";

        /// <summary>
        /// Nome della variabile d'ambiente per override della cartella dati
        /// </summary>
        private const string DATA_DIR_ENV_VAR = "REMUXFORGE_DATA_DIR";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Istanza singleton
        /// </summary>
        private static AppSettingsService s_instance;

        /// <summary>
        /// Sincronizza la creazione dell'istanza singleton
        /// </summary>
        private static readonly object s_instanceLock = new object();

        /// <summary>
        /// Modello impostazioni correnti
        /// </summary>
        private AppSettingsModel _model;

        /// <summary>
        /// Percorso completo della cartella .remux-forge
        /// </summary>
        private readonly string _configFolder;

        /// <summary>
        /// Percorso completo del file appsettings.json
        /// </summary>
        private readonly string _configFilePath;

        /// <summary>
        /// Serializza lettura e scrittura del file di configurazione
        /// </summary>
        private readonly object _fileLock;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore privato singleton: calcola percorsi e crea model di default
        /// </summary>
        private AppSettingsService()
        {
            string envDataDir = Environment.GetEnvironmentVariable(DATA_DIR_ENV_VAR);
            string baseDir = (!string.IsNullOrEmpty(envDataDir)) ? envDataDir : AppContext.BaseDirectory;
            this._configFolder = Path.Combine(baseDir, CONFIG_FOLDER_NAME);
            this._configFilePath = Path.Combine(this._configFolder, CONFIG_FILE_NAME);
            this._model = new AppSettingsModel();
            this._fileLock = new object();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Istanza singleton del servizio
        /// </summary>
        public static AppSettingsService Instance
        {
            get
            {
                lock (s_instanceLock)
                {
                    if (s_instance == null)
                        s_instance = new AppSettingsService();
                    return s_instance;
                }
            }
        }

        /// <summary>
        /// Modello impostazioni correnti
        /// </summary>
        public AppSettingsModel Settings { get { return this._model; } }

        /// <summary>
        /// Percorso della cartella .remux-forge
        /// </summary>
        public string ConfigFolder { get { return this._configFolder; } }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Inizializza la cartella .remux-forge e carica le impostazioni
        /// </summary>
        /// <returns>True se le impostazioni sono state caricate o create con successo</returns>
        public bool Initialize()
        {
            bool success;
            // Crea cartella .remux-forge se non esiste
            if (!Directory.Exists(this._configFolder))
            {
                Directory.CreateDirectory(this._configFolder);

                // Su Windows imposta attributo nascosto
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    DirectoryInfo dirInfo = new DirectoryInfo(this._configFolder);
                    dirInfo.Attributes |= FileAttributes.Hidden;
                }
            }

            // Percorso default per cartella temp
            string defaultTempFolder = Path.Combine(this._configFolder, TEMP_FOLDER_NAME);

            // Carica o crea file impostazioni
            if (File.Exists(this._configFilePath))
            {
                // Leggi contenuto originale per confronto smart merge
                string originalJson = File.ReadAllText(this._configFilePath);
                success = this.Load();

                // Precompila TempFolder se vuoto
                if (string.IsNullOrEmpty(this._model.Tools.TempFolder))
                {
                    this._model.Tools.TempFolder = defaultTempFolder;
                }

                if (success)
                {
                    // Confronta JSON originale con model serializzato
                    // Se diversi, riscrive il file (campi nuovi, valori sanitizzati)
                    JsonSerializerOptions serOptions = new JsonSerializerOptions();
                    serOptions.WriteIndented = true;
                    string newJson = JsonSerializer.Serialize(this._model, serOptions);

                    if (newJson != originalJson)
                    {
                        this.Save();
                    }
                }
            }
            else
            {
                // Crea con valori di default e precompila TempFolder
                this._model = new AppSettingsModel();
                this._model.Tools.TempFolder = defaultTempFolder;
                success = this.Save();
            }

            return success;
        }

        /// <summary>
        /// Carica le impostazioni dal file appsettings.json
        /// </summary>
        /// <returns>True se il caricamento è riuscito</returns>
        public bool Load()
        {
            bool success = false;
            string json;
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.PropertyNameCaseInsensitive = true;

            try
            {
                lock (this._fileLock)
                {
                    json = File.ReadAllText(this._configFilePath);

                    // Se deserializzazione restituisce null, ricrea model default
                    this._model = JsonSerializer.Deserialize<AppSettingsModel>(json, options) ?? new AppSettingsModel();

                    this.MigrateSubtitleEditConfig(json);
                    this.MigrateSiftBackendConfig(json);

                    // Assicura che sotto-oggetti non siano null
                    this.EnsureNotNull();

                    // Sanitizzazione post-caricamento: clamp range e correggi valori invalidi
                    this.Sanitize();

                    // Rimuovi profili senza nome
                    this.RemoveEmptyProfiles();
                }

                success = true;
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Warning, AppText.F("settings.loadError", ex.Message));
                lock (this._fileLock)
                    this._model = new AppSettingsModel();
            }

            return success;
        }

        /// <summary>
        /// Salva le impostazioni correnti su appsettings.json
        /// </summary>
        /// <returns>True se il salvataggio è riuscito</returns>
        public bool Save()
        {
            bool success = false;
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.WriteIndented = true;
            string json;
            string tempFilePath = this._configFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                lock (this._fileLock)
                {
                    json = JsonSerializer.Serialize(this._model, options);
                    using (FileStream stream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(json);
                        writer.Flush();
                        stream.Flush(true);
                    }
                    File.Move(tempFilePath, this._configFilePath, true);
                }
                success = true;
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Warning, AppText.F("settings.saveError", ex.Message));
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFilePath))
                        File.Delete(tempFilePath);
                }
                catch (IOException)
                {
                    // Il salvataggio ha già registrato l'errore principale; la pulizia resta best-effort
                }
                catch (UnauthorizedAccessException)
                {
                    // Il salvataggio ha già registrato l'errore principale; la pulizia resta best-effort
                }
            }

            return success;
        }

        /// <summary>
        /// Valida le impostazioni audio (FLAC, Opus, AAC e AC-3)
        /// </summary>
        /// <param name="errorMessage">Messaggio di errore, vuoto se valido</param>
        /// <returns>True se tutti i valori audio sono validi</returns>
        public bool ValidateAudio(out string errorMessage)
        {
            List<string> errors = new List<string>();
            bool result;
            string validAc3Bitrates;

            // Validazione FLAC compression level
            if (this._model.Flac.CompressionLevel < AppSettingsModel.FLAC_COMPRESSION_MIN || this._model.Flac.CompressionLevel > AppSettingsModel.FLAC_COMPRESSION_MAX)
            {
                errors.Add(AppText.F("settings.validation.range", "FLAC CompressionLevel", AppSettingsModel.FLAC_COMPRESSION_MIN, AppSettingsModel.FLAC_COMPRESSION_MAX));
            }

            // Validazione Opus bitrate mono
            if (this._model.Opus.Bitrate.Mono < AppSettingsModel.OPUS_BITRATE_MIN || this._model.Opus.Bitrate.Mono > AppSettingsModel.OPUS_BITRATE_MAX)
            {
                errors.Add(AppText.F("settings.validation.rangeKbps", "Opus bitrate Mono", AppSettingsModel.OPUS_BITRATE_MIN, AppSettingsModel.OPUS_BITRATE_MAX));
            }

            // Validazione Opus bitrate stereo
            if (this._model.Opus.Bitrate.Stereo < AppSettingsModel.OPUS_BITRATE_MIN || this._model.Opus.Bitrate.Stereo > AppSettingsModel.OPUS_BITRATE_MAX)
            {
                errors.Add(AppText.F("settings.validation.rangeKbps", "Opus bitrate Stereo", AppSettingsModel.OPUS_BITRATE_MIN, AppSettingsModel.OPUS_BITRATE_MAX));
            }

            // Validazione Opus bitrate surround 5.1
            if (this._model.Opus.Bitrate.Surround51 < AppSettingsModel.OPUS_BITRATE_MIN || this._model.Opus.Bitrate.Surround51 > AppSettingsModel.OPUS_BITRATE_MAX)
            {
                errors.Add(AppText.F("settings.validation.rangeKbps", "Opus bitrate Surround 5.1", AppSettingsModel.OPUS_BITRATE_MIN, AppSettingsModel.OPUS_BITRATE_MAX));
            }

            // Validazione Opus bitrate surround 7.1
            if (this._model.Opus.Bitrate.Surround71 < AppSettingsModel.OPUS_BITRATE_MIN || this._model.Opus.Bitrate.Surround71 > AppSettingsModel.OPUS_BITRATE_MAX)
            {
                errors.Add(AppText.F("settings.validation.rangeKbps", "Opus bitrate Surround 7.1", AppSettingsModel.OPUS_BITRATE_MIN, AppSettingsModel.OPUS_BITRATE_MAX));
            }

            // Validazione AAC bitrate mono
            if (this._model.Aac.Bitrate.Mono < AppSettingsModel.AAC_BITRATE_MIN || this._model.Aac.Bitrate.Mono > AppSettingsModel.AAC_BITRATE_MAX)
            {
                errors.Add(AppText.F("settings.validation.rangeKbps", "AAC bitrate Mono", AppSettingsModel.AAC_BITRATE_MIN, AppSettingsModel.AAC_BITRATE_MAX));
            }

            // Validazione AAC bitrate stereo
            if (this._model.Aac.Bitrate.Stereo < AppSettingsModel.AAC_BITRATE_MIN || this._model.Aac.Bitrate.Stereo > AppSettingsModel.AAC_BITRATE_MAX)
            {
                errors.Add(AppText.F("settings.validation.rangeKbps", "AAC bitrate Stereo", AppSettingsModel.AAC_BITRATE_MIN, AppSettingsModel.AAC_BITRATE_MAX));
            }

            // Validazione AAC bitrate surround 5.1
            if (this._model.Aac.Bitrate.Surround51 < AppSettingsModel.AAC_BITRATE_MIN || this._model.Aac.Bitrate.Surround51 > AppSettingsModel.AAC_BITRATE_MAX)
            {
                errors.Add(AppText.F("settings.validation.rangeKbps", "AAC bitrate Surround 5.1", AppSettingsModel.AAC_BITRATE_MIN, AppSettingsModel.AAC_BITRATE_MAX));
            }

            // Validazione AAC bitrate surround 7.1
            if (this._model.Aac.Bitrate.Surround71 < AppSettingsModel.AAC_BITRATE_MIN || this._model.Aac.Bitrate.Surround71 > AppSettingsModel.AAC_BITRATE_MAX)
            {
                errors.Add(AppText.F("settings.validation.rangeKbps", "AAC bitrate Surround 7.1", AppSettingsModel.AAC_BITRATE_MIN, AppSettingsModel.AAC_BITRATE_MAX));
            }

            validAc3Bitrates = string.Join(", ", AppSettingsModel.AC3_VALID_BITRATES_KBPS);
            if (!this.IsValidAc3Bitrate(this._model.Ac3.Bitrate.Mono))
            {
                errors.Add(AppText.F("settings.validation.ac3Bitrate", "AC-3 bitrate Mono", validAc3Bitrates));
            }
            if (!this.IsValidAc3Bitrate(this._model.Ac3.Bitrate.Stereo))
            {
                errors.Add(AppText.F("settings.validation.ac3Bitrate", "AC-3 bitrate Stereo", validAc3Bitrates));
            }
            if (!this.IsValidAc3Bitrate(this._model.Ac3.Bitrate.Surround51))
            {
                errors.Add(AppText.F("settings.validation.ac3Bitrate", "AC-3 bitrate Surround 5.1", validAc3Bitrates));
            }

            // Componi messaggio errore
            result = (errors.Count == 0);
            errorMessage = result ? "" : string.Join("\n", errors);

            return result;
        }

        /// <summary>
        /// Valida i percorsi dei tool esterni (trim + verifica esistenza)
        /// </summary>
        /// <param name="errorMessage">Messaggio di errore, vuoto se valido</param>
        /// <returns>True se tutti i percorsi sono validi</returns>
        public bool ValidateToolPaths(out string errorMessage)
        {
            List<string> errors = new List<string>();
            bool result;
            // Trim percorsi
            this._model.Tools.MkvMergePath = this._model.Tools.MkvMergePath.Trim();
            this._model.Tools.MkvExtractPath = this._model.Tools.MkvExtractPath.Trim();
            this._model.Tools.MkvPropEditPath = this._model.Tools.MkvPropEditPath.Trim();
            this._model.Tools.FfmpegPath = this._model.Tools.FfmpegPath.Trim();
            this._model.Tools.FfprobePath = this._model.Tools.FfprobePath.Trim();
            this._model.Tools.MediaInfoPath = this._model.Tools.MediaInfoPath.Trim();

            // Verifica esistenza mkvmerge
            if (!string.IsNullOrEmpty(this._model.Tools.MkvMergePath) && !File.Exists(this._model.Tools.MkvMergePath))
            {
                errors.Add(AppText.F("settings.validation.toolPathNotFound", "mkvmerge", this._model.Tools.MkvMergePath));
            }

            // Verifica esistenza mkvextract
            if (!string.IsNullOrEmpty(this._model.Tools.MkvExtractPath) && !File.Exists(this._model.Tools.MkvExtractPath))
            {
                errors.Add(AppText.F("settings.validation.toolPathNotFound", "mkvextract", this._model.Tools.MkvExtractPath));
            }

            // Verifica esistenza mkvpropedit
            if (!string.IsNullOrEmpty(this._model.Tools.MkvPropEditPath) && !File.Exists(this._model.Tools.MkvPropEditPath))
            {
                errors.Add(AppText.F("settings.validation.toolPathNotFound", "mkvpropedit", this._model.Tools.MkvPropEditPath));
            }

            // Verifica esistenza ffmpeg
            if (!string.IsNullOrEmpty(this._model.Tools.FfmpegPath) && !File.Exists(this._model.Tools.FfmpegPath))
            {
                errors.Add(AppText.F("settings.validation.toolPathNotFound", "ffmpeg", this._model.Tools.FfmpegPath));
            }

            // Verifica esistenza ffprobe
            if (!string.IsNullOrEmpty(this._model.Tools.FfprobePath) && !File.Exists(this._model.Tools.FfprobePath))
            {
                errors.Add(AppText.F("settings.validation.toolPathNotFound", "ffprobe", this._model.Tools.FfprobePath));
            }

            // Verifica esistenza mediainfo
            if (!string.IsNullOrEmpty(this._model.Tools.MediaInfoPath) && !File.Exists(this._model.Tools.MediaInfoPath))
            {
                errors.Add(AppText.F("settings.validation.toolPathNotFound", "mediainfo", this._model.Tools.MediaInfoPath));
            }
            else if (!string.IsNullOrEmpty(this._model.Tools.MediaInfoPath) && !MediaInfoProvider.IsCliExecutablePath(this._model.Tools.MediaInfoPath))
            {
                errors.Add(AppText.T("settings.validation.mediaInfoCliPath"));
            }

            // Componi messaggio errore
            result = (errors.Count == 0);
            errorMessage = result ? "" : string.Join("\n", errors);

            return result;
        }

        /// <summary>
        /// Restituisce il percorso della cartella per file temporanei di conversione
        /// </summary>
        /// <returns>Percorso cartella temp, creata se non esistente</returns>
        public string GetTempFolder()
        {
            // Usa il percorso configurato in appsettings, fallback a default
            string tempFolder = this._model.Tools.TempFolder;
            if (string.IsNullOrEmpty(tempFolder))
            {
                tempFolder = Path.Combine(this._configFolder, TEMP_FOLDER_NAME);
            }

            if (!Directory.Exists(tempFolder))
            {
                Directory.CreateDirectory(tempFolder);
            }

            return tempFolder;
        }

        /// <summary>
        /// Restituisce il bitrate Opus appropriato in base al numero di canali
        /// </summary>
        /// <param name="channels">Numero di canali audio</param>
        /// <returns>Bitrate in kbps</returns>
        public int GetOpusBitrateForChannels(int channels)
        {
            int bitrate;

            if (channels <= 1)
            {
                bitrate = this._model.Opus.Bitrate.Mono;
            }
            else if (channels <= 2)
            {
                bitrate = this._model.Opus.Bitrate.Stereo;
            }
            else if (channels <= 6)
            {
                bitrate = this._model.Opus.Bitrate.Surround51;
            }
            else
            {
                bitrate = this._model.Opus.Bitrate.Surround71;
            }

            return bitrate;
        }

        /// <summary>
        /// Restituisce il bitrate AAC appropriato in base al numero di canali
        /// </summary>
        /// <param name="channels">Numero di canali audio</param>
        /// <returns>Bitrate in kbps</returns>
        public int GetAacBitrateForChannels(int channels)
        {
            int bitrate;

            if (channels <= 1)
            {
                bitrate = this._model.Aac.Bitrate.Mono;
            }
            else if (channels <= 2)
            {
                bitrate = this._model.Aac.Bitrate.Stereo;
            }
            else if (channels <= 6)
            {
                bitrate = this._model.Aac.Bitrate.Surround51;
            }
            else
            {
                bitrate = this._model.Aac.Bitrate.Surround71;
            }

            return bitrate;
        }

        /// <summary>
        /// Restituisce il bitrate AC-3 appropriato in base al numero di canali
        /// </summary>
        /// <param name="channels">Numero di canali audio sorgente</param>
        /// <returns>Bitrate in kbps</returns>
        public int GetAc3BitrateForChannels(int channels)
        {
            int bitrate;

            if (channels <= 1)
            {
                bitrate = this._model.Ac3.Bitrate.Mono;
            }
            else if (channels <= 2)
            {
                bitrate = this._model.Ac3.Bitrate.Stereo;
            }
            else
            {
                bitrate = this._model.Ac3.Bitrate.Surround51;
            }

            return bitrate;
        }

        /// <summary>
        /// Restituisce un profilo di encoding per nome
        /// </summary>
        /// <param name="name">Nome del profilo</param>
        /// <returns>Profilo trovato, null se non esiste</returns>
        public EncodingProfile GetProfile(string name)
        {
            EncodingProfile result = null;
            for (int i = 0; i < this._model.EncodingProfiles.Count; i++)
            {
                if (this._model.EncodingProfiles[i] != null && this._model.EncodingProfiles[i].Name == name)
                {
                    result = this._model.EncodingProfiles[i];
                    break;
                }
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Assicura che tutti i sotto-oggetti del model non siano null
        /// </summary>
        private void EnsureNotNull()
        {
            if (this._model.Tools == null)
                this._model.Tools = new ToolsConfig();

            if (this._model.Flac == null)
                this._model.Flac = new FlacConfig();

            if (this._model.Opus == null)
                this._model.Opus = new OpusConfig();

            if (this._model.Opus.Bitrate == null)
                this._model.Opus.Bitrate = new OpusBitrateConfig();

            if (this._model.Aac == null)
                this._model.Aac = new AacConfig();

            if (this._model.Aac.Bitrate == null)
                this._model.Aac.Bitrate = new AacBitrateConfig();

            if (this._model.Ac3 == null)
                this._model.Ac3 = new Ac3Config();

            if (this._model.Ac3.Bitrate == null)
                this._model.Ac3.Bitrate = new Ac3BitrateConfig();

            if (this._model.Ui == null)
                this._model.Ui = new UiConfig();

            if (this._model.EncodingProfiles == null)
                this._model.EncodingProfiles = new List<EncodingProfile>();

            // Assicura stringhe non null nei percorsi tool
            if (this._model.Tools.MkvMergePath == null)
                this._model.Tools.MkvMergePath = "";

            if (this._model.Tools.MkvExtractPath == null)
                this._model.Tools.MkvExtractPath = "";

            if (this._model.Tools.MkvPropEditPath == null)
                this._model.Tools.MkvPropEditPath = "";

            if (this._model.Tools.FfmpegPath == null)
                this._model.Tools.FfmpegPath = "";

            if (this._model.Tools.FfprobePath == null)
                this._model.Tools.FfprobePath = "";

            if (this._model.Tools.MediaInfoPath == null)
                this._model.Tools.MediaInfoPath = "";

            if (this._model.Tools.TempFolder == null)
                this._model.Tools.TempFolder = "";

            if (this._model.Ui.Theme == null)
                this._model.Ui.Theme = "nord";

            if (this._model.Ui.LastMode == null)
                this._model.Ui.LastMode = Options.MODE_REMUX;

            if (this._model.Ui.Language == null)
                this._model.Ui.Language = AppText.LANG_EN;

            // Assicura sotto-oggetti Advanced non null
            if (this._model.Advanced == null)
                this._model.Advanced = new AdvancedConfig();

            if (this._model.Advanced.VideoSync == null)
                this._model.Advanced.VideoSync = new VideoSyncConfig();

            if (this._model.Advanced.SpeedCorrection == null)
                this._model.Advanced.SpeedCorrection = new SpeedCorrectionConfig();

            if (this._model.Advanced.FrameSync == null)
                this._model.Advanced.FrameSync = new FrameSyncConfig();

            if (this._model.Advanced.DeepAnalysis == null)
                this._model.Advanced.DeepAnalysis = new DeepAnalysisConfig();

            if (this._model.Advanced.SubtitleEdit == null)
                this._model.Advanced.SubtitleEdit = new SubtitleEditConfig();

            if (this._model.Advanced.Ffmpeg == null)
                this._model.Advanced.Ffmpeg = new FfmpegConfig();

            if (this._model.Advanced.Ffmpeg.HardwareAccelerationMethod == null)
                this._model.Advanced.Ffmpeg.HardwareAccelerationMethod = "";

        }

        /// <summary>
        /// Migra la vecchia sezione Advanced.TrackSplit nella sezione SubtitleEdit
        /// </summary>
        /// <param name="json">JSON originale delle impostazioni</param>
        private void MigrateSubtitleEditConfig(string json)
        {
            JsonDocument document = null;

            if (this._model == null || this._model.Advanced == null)
            {
                return;
            }

            try
            {
                document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("Advanced", out JsonElement advancedElement) &&
                    !advancedElement.TryGetProperty("SubtitleEdit", out _) &&
                    advancedElement.TryGetProperty("TrackSplit", out JsonElement trackSplitElement) &&
                    trackSplitElement.TryGetProperty("FfmpegTimeoutMs", out JsonElement timeoutElement) &&
                    timeoutElement.TryGetInt32(out int timeoutMs))
                {
                    if (this._model.Advanced.SubtitleEdit == null)
                        this._model.Advanced.SubtitleEdit = new SubtitleEditConfig();
                    this._model.Advanced.SubtitleEdit.FfmpegTimeoutMs = timeoutMs;
                }
            }
            catch
            {
                // Migrazione best-effort: in caso di JSON non atteso resta il default
            }
            finally
            {
                if (document != null)
                    document.Dispose();
            }
        }

        /// <summary>
        /// Migra il backend SIFT dalle vecchie sezioni specifiche alla configurazione condivisa
        /// </summary>
        /// <param name="json">JSON originale delle impostazioni</param>
        private void MigrateSiftBackendConfig(string json)
        {
            JsonDocument document = null;

            if (this._model == null || this._model.Advanced == null)
                return;

            try
            {
                document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("Advanced", out JsonElement advancedElement) ||
                    advancedElement.TryGetProperty("SiftBackend", out _))
                {
                    return;
                }

                string siftBackend = this.ReadNestedSiftBackend(advancedElement, "DeepAnalysis");
                if (string.IsNullOrEmpty(siftBackend))
                    siftBackend = this.ReadNestedSiftBackend(advancedElement, "SpeedCorrection");
                if (!string.IsNullOrEmpty(siftBackend))
                    this._model.Advanced.SiftBackend = siftBackend;
            }
            catch
            {
                // Migrazione best-effort: in caso di JSON non atteso resta il default
            }
            finally
            {
                if (document != null)
                    document.Dispose();
            }
        }

        /// <summary>
        /// Legge il backend SIFT da una vecchia sezione Advanced
        /// </summary>
        /// <param name="advancedElement">Sezione Advanced originale</param>
        /// <param name="sectionName">Nome della sezione precedente</param>
        /// <returns>Backend configurato oppure stringa vuota</returns>
        private string ReadNestedSiftBackend(JsonElement advancedElement, string sectionName)
        {
            if (advancedElement.TryGetProperty(sectionName, out JsonElement sectionElement) &&
                sectionElement.TryGetProperty("SiftBackend", out JsonElement backendElement) &&
                backendElement.ValueKind == JsonValueKind.String)
            {
                return backendElement.GetString() ?? "";
            }

            return "";
        }

        /// <summary>
        /// Sanitizzazione silenziosa post-caricamento: clamp range e correggi valori invalidi
        /// </summary>
        private void Sanitize()
        {
            // Clamp FLAC compression level
            if (this._model.Flac.CompressionLevel < AppSettingsModel.FLAC_COMPRESSION_MIN)
            {
                this._model.Flac.CompressionLevel = AppSettingsModel.FLAC_COMPRESSION_MIN;
            }
            if (this._model.Flac.CompressionLevel > AppSettingsModel.FLAC_COMPRESSION_MAX)
            {
                this._model.Flac.CompressionLevel = AppSettingsModel.FLAC_COMPRESSION_MAX;
            }

            // Clamp Opus bitrate
            this._model.Opus.Bitrate.Mono = this.ClampBitrate(this._model.Opus.Bitrate.Mono);
            this._model.Opus.Bitrate.Stereo = this.ClampBitrate(this._model.Opus.Bitrate.Stereo);
            this._model.Opus.Bitrate.Surround51 = this.ClampBitrate(this._model.Opus.Bitrate.Surround51);
            this._model.Opus.Bitrate.Surround71 = this.ClampBitrate(this._model.Opus.Bitrate.Surround71);

            // Clamp AAC bitrate
            this._model.Aac.Bitrate.Mono = this.ClampAacBitrate(this._model.Aac.Bitrate.Mono);
            this._model.Aac.Bitrate.Stereo = this.ClampAacBitrate(this._model.Aac.Bitrate.Stereo);
            this._model.Aac.Bitrate.Surround51 = this.ClampAacBitrate(this._model.Aac.Bitrate.Surround51);
            this._model.Aac.Bitrate.Surround71 = this.ClampAacBitrate(this._model.Aac.Bitrate.Surround71);

            // Clamp AC-3 bitrate su valori discreti validi
            this._model.Ac3.Bitrate.Mono = this.ClampAc3Bitrate(this._model.Ac3.Bitrate.Mono);
            this._model.Ac3.Bitrate.Stereo = this.ClampAc3Bitrate(this._model.Ac3.Bitrate.Stereo);
            this._model.Ac3.Bitrate.Surround51 = this.ClampAc3Bitrate(this._model.Ac3.Bitrate.Surround51);

            // Validazione tema: se non è tra quelli validi, reset a "nord"
            bool themeValid = false;
            for (int i = 0; i < AppSettingsModel.VALID_THEMES.Length; i++)
            {
                if (AppSettingsModel.VALID_THEMES[i] == this._model.Ui.Theme)
                {
                    themeValid = true;
                    break;
                }
            }
            if (!themeValid)
            {
                this._model.Ui.Theme = "nord";
            }

            if (this._model.Ui.LastMode != Options.MODE_REMUX && this._model.Ui.LastMode != Options.MODE_SPLIT && this._model.Ui.LastMode != Options.MODE_METADATA)
            {
                this._model.Ui.LastMode = Options.MODE_REMUX;
            }

            string normalizedLanguage = AppText.NormalizeLanguage(this._model.Ui.Language);
            this._model.Ui.Language = !string.IsNullOrEmpty(normalizedLanguage) ? normalizedLanguage : AppText.LANG_EN;

            // Sanitizzazione Advanced — VideoSync
            VideoSyncConfig vs = this._model.Advanced.VideoSync;
            vs.FrameWidth = this.ClampInt(vs.FrameWidth, 64, 1920);
            vs.FrameHeight = this.ClampInt(vs.FrameHeight, 64, 1080);
            vs.NumCheckPoints = this.ClampInt(vs.NumCheckPoints, 1, 1000);
            vs.VerifySourceDurationSec = this.ClampInt(vs.VerifySourceDurationSec, 1, 3600);
            vs.VerifyLangDurationSec = this.ClampInt(vs.VerifyLangDurationSec, 1, 3600);

            // Sanitizzazione Advanced — SpeedCorrection
            SpeedCorrectionConfig sc = this._model.Advanced.SpeedCorrection;
            sc.SourceStartSec = this.ClampInt(sc.SourceStartSec, 0, 3600);
            sc.SourceDurationSec = this.ClampInt(sc.SourceDurationSec, 1, 3600);
            sc.LangDurationSec = this.ClampInt(sc.LangDurationSec, 1, 3600);

            // Sanitizzazione Advanced — FrameSync
            FrameSyncConfig fs = this._model.Advanced.FrameSync;
            fs.MinDurationMs = this.ClampInt(fs.MinDurationMs, 1000, 600000);
            fs.SourceStartSec = this.ClampInt(fs.SourceStartSec, 0, 3600);
            fs.SourceDurationSec = this.ClampInt(fs.SourceDurationSec, 1, 3600);
            fs.LangDurationSec = this.ClampInt(fs.LangDurationSec, 1, 3600);
            fs.MinValidPoints = this.ClampInt(fs.MinValidPoints, 1, 1000);
            fs.FinalMinConfidence = this.ClampDouble(fs.FinalMinConfidence, 0.0, 1.0);
            if (!AdvancedConfig.TryParseSiftBackend(this._model.Advanced.SiftBackend, out SiftBackendKind siftBackend))
                siftBackend = SiftBackendKind.Cpu;
            this._model.Advanced.SetSiftBackendKind(siftBackend);

            // Sanitizzazione Advanced — DeepAnalysis
            DeepAnalysisConfig da = this._model.Advanced.DeepAnalysis;
            da.SceneExtractTimeoutMs = this.ClampInt(da.SceneExtractTimeoutMs, 1000, 3600000);

            // Sanitizzazione Advanced — SubtitleEdit
            SubtitleEditConfig subtitleEdit = this._model.Advanced.SubtitleEdit;
            subtitleEdit.FfmpegTimeoutMs = this.ClampInt(subtitleEdit.FfmpegTimeoutMs, 1000, 3600000);
            FfmpegConfig ffmpeg = this._model.Advanced.Ffmpeg;
            ffmpeg.FrameExtractionTimeoutMs = this.ClampInt(ffmpeg.FrameExtractionTimeoutMs, 1000, 3600000);
            ffmpeg.HardwareAccelerationMethod = ffmpeg.HardwareAccelerationMethod.Trim().ToLowerInvariant();
            if (!FfmpegConfig.IsValidHardwareAccelerationMethod(ffmpeg.HardwareAccelerationMethod))
            {
                ffmpeg.HardwareAcceleration = false;
                ffmpeg.HardwareAccelerationMethod = "";
            }

        }

        /// <summary>
        /// Limita un valore bitrate entro il range consentito
        /// </summary>
        /// <param name="value">Valore da limitare</param>
        /// <returns>Valore limitato nel range</returns>
        private int ClampBitrate(int value)
        {
            int result = value;

            if (result < AppSettingsModel.OPUS_BITRATE_MIN)
            {
                result = AppSettingsModel.OPUS_BITRATE_MIN;
            }
            if (result > AppSettingsModel.OPUS_BITRATE_MAX)
            {
                result = AppSettingsModel.OPUS_BITRATE_MAX;
            }

            return result;
        }

        /// <summary>
        /// Limita un bitrate AAC entro il range consentito
        /// </summary>
        /// <param name="value">Valore da limitare</param>
        /// <returns>Valore limitato nel range AAC</returns>
        private int ClampAacBitrate(int value)
        {
            int result = value;

            if (result < AppSettingsModel.AAC_BITRATE_MIN)
            {
                result = AppSettingsModel.AAC_BITRATE_MIN;
            }
            if (result > AppSettingsModel.AAC_BITRATE_MAX)
            {
                result = AppSettingsModel.AAC_BITRATE_MAX;
            }

            return result;
        }

        /// <summary>
        /// Verifica se un bitrate AC-3 è tra quelli previsti dallo standard
        /// </summary>
        /// <param name="value">Bitrate in kbps da verificare</param>
        /// <returns>True se il bitrate è valido</returns>
        private bool IsValidAc3Bitrate(int value)
        {
            for (int i = 0; i < AppSettingsModel.AC3_VALID_BITRATES_KBPS.Length; i++)
            {
                if (AppSettingsModel.AC3_VALID_BITRATES_KBPS[i] == value)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Limita un bitrate AC-3 al valore valido più vicino
        /// </summary>
        /// <param name="value">Bitrate in kbps da limitare</param>
        /// <returns>Bitrate AC-3 valido più vicino</returns>
        private int ClampAc3Bitrate(int value)
        {
            int nearest = AppSettingsModel.AC3_VALID_BITRATES_KBPS[0];
            int nearestDelta = Math.Abs(value - nearest);

            for (int i = 1; i < AppSettingsModel.AC3_VALID_BITRATES_KBPS.Length; i++)
            {
                int candidate = AppSettingsModel.AC3_VALID_BITRATES_KBPS[i];
                int delta = Math.Abs(value - candidate);
                if (delta < nearestDelta)
                {
                    nearest = candidate;
                    nearestDelta = delta;
                }
            }

            return nearest;
        }

        /// <summary>
        /// Limita un valore intero entro un range
        /// </summary>
        /// <param name="value">Valore da limitare</param>
        /// <param name="min">Minimo consentito</param>
        /// <param name="max">Massimo consentito</param>
        /// <returns>Valore limitato nel range</returns>
        private int ClampInt(int value, int min, int max)
        {
            int result = value;

            if (result < min)
                result = min;

            if (result > max)
                result = max;

            return result;
        }

        /// <summary>
        /// Limita un valore double entro un range
        /// </summary>
        /// <param name="value">Valore da limitare</param>
        /// <param name="min">Minimo consentito</param>
        /// <param name="max">Massimo consentito</param>
        /// <returns>Valore limitato nel range</returns>
        private double ClampDouble(double value, double min, double max)
        {
            double result = value;

            if (result < min)
                result = min;

            if (result > max)
                result = max;

            return result;
        }

        /// <summary>
        /// Rimuove profili di encoding con nome vuoto dalla lista
        /// </summary>
        private void RemoveEmptyProfiles()
        {
            int i = 0;
            while (i < this._model.EncodingProfiles.Count)
            {
                if (this._model.EncodingProfiles[i] == null || string.IsNullOrEmpty(this._model.EncodingProfiles[i].Name))
                {
                    this._model.EncodingProfiles.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
        }

        #endregion
    }
}
