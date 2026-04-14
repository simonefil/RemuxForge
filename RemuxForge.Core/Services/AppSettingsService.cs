using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RemuxForge.Core
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
        /// Modello impostazioni correnti
        /// </summary>
        private AppSettingsModel _model;

        /// <summary>
        /// Percorso completo della cartella .remux-forge
        /// </summary>
        private string _configFolder;

        /// <summary>
        /// Percorso completo del file appsettings.json
        /// </summary>
        private string _configFilePath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore privato singleton: calcola percorsi e crea model di default
        /// </summary>
        private AppSettingsService()
        {
            string envDataDir = Environment.GetEnvironmentVariable(DATA_DIR_ENV_VAR);
            string baseDir = (envDataDir != null && envDataDir.Length > 0) ? envDataDir : AppContext.BaseDirectory;
            this._configFolder = Path.Combine(baseDir, CONFIG_FOLDER_NAME);
            this._configFilePath = Path.Combine(this._configFolder, CONFIG_FILE_NAME);
            this._model = new AppSettingsModel();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Istanza singleton del servizio
        /// </summary>
        public static AppSettingsService Instance
        {
            get
            {
                if (s_instance == null)
                {
                    s_instance = new AppSettingsService();
                }
                return s_instance;
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

        /// <summary>
        /// Percorso del file appsettings.json
        /// </summary>
        public string ConfigFilePath { get { return this._configFilePath; } }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Inizializza la cartella .remux-forge e carica le impostazioni
        /// </summary>
        /// <returns>True se le impostazioni sono state caricate o create con successo</returns>
        public bool Initialize()
        {
            bool success = true;

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
                if (this._model.Tools.TempFolder.Length == 0)
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
        /// <returns>True se il caricamento e' riuscito</returns>
        public bool Load()
        {
            bool success = false;
            string json = "";
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.PropertyNameCaseInsensitive = true;

            try
            {
                json = File.ReadAllText(this._configFilePath);
                this._model = JsonSerializer.Deserialize<AppSettingsModel>(json, options);

                // Se deserializzazione restituisce null, ricrea model default
                if (this._model == null)
                {
                    this._model = new AppSettingsModel();
                }

                // Assicura che sotto-oggetti non siano null
                this.EnsureNotNull();

                // Sanitizzazione post-caricamento: clamp range e correggi valori invalidi
                this.Sanitize();

                // Rimuovi profili senza nome
                this.RemoveEmptyProfiles();

                success = true;
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Warning, "Errore caricamento appsettings.json: " + ex.Message);
                this._model = new AppSettingsModel();
            }

            return success;
        }

        /// <summary>
        /// Salva le impostazioni correnti su appsettings.json
        /// </summary>
        /// <returns>True se il salvataggio e' riuscito</returns>
        public bool Save()
        {
            bool success = false;
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.WriteIndented = true;
            string json = "";

            try
            {
                json = JsonSerializer.Serialize(this._model, options);
                File.WriteAllText(this._configFilePath, json);
                success = true;
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Warning, "Errore salvataggio appsettings.json: " + ex.Message);
            }

            return success;
        }

        /// <summary>
        /// Valida le impostazioni audio (FLAC e Opus)
        /// </summary>
        /// <param name="errorMessage">Messaggio di errore, vuoto se valido</param>
        /// <returns>True se tutti i valori audio sono validi</returns>
        public bool ValidateAudio(out string errorMessage)
        {
            List<string> errors = new List<string>();
            bool result = false;

            // Validazione FLAC compression level
            if (this._model.Flac.CompressionLevel < AppSettingsModel.FLAC_COMPRESSION_MIN || this._model.Flac.CompressionLevel > AppSettingsModel.FLAC_COMPRESSION_MAX)
            {
                errors.Add("Flac CompressionLevel deve essere tra " + AppSettingsModel.FLAC_COMPRESSION_MIN + " e " + AppSettingsModel.FLAC_COMPRESSION_MAX);
            }

            // Validazione Opus bitrate mono
            if (this._model.Opus.Bitrate.Mono < AppSettingsModel.OPUS_BITRATE_MIN || this._model.Opus.Bitrate.Mono > AppSettingsModel.OPUS_BITRATE_MAX)
            {
                errors.Add("Opus bitrate Mono deve essere tra " + AppSettingsModel.OPUS_BITRATE_MIN + " e " + AppSettingsModel.OPUS_BITRATE_MAX + " kbps");
            }

            // Validazione Opus bitrate stereo
            if (this._model.Opus.Bitrate.Stereo < AppSettingsModel.OPUS_BITRATE_MIN || this._model.Opus.Bitrate.Stereo > AppSettingsModel.OPUS_BITRATE_MAX)
            {
                errors.Add("Opus bitrate Stereo deve essere tra " + AppSettingsModel.OPUS_BITRATE_MIN + " e " + AppSettingsModel.OPUS_BITRATE_MAX + " kbps");
            }

            // Validazione Opus bitrate surround 5.1
            if (this._model.Opus.Bitrate.Surround51 < AppSettingsModel.OPUS_BITRATE_MIN || this._model.Opus.Bitrate.Surround51 > AppSettingsModel.OPUS_BITRATE_MAX)
            {
                errors.Add("Opus bitrate Surround 5.1 deve essere tra " + AppSettingsModel.OPUS_BITRATE_MIN + " e " + AppSettingsModel.OPUS_BITRATE_MAX + " kbps");
            }

            // Validazione Opus bitrate surround 7.1
            if (this._model.Opus.Bitrate.Surround71 < AppSettingsModel.OPUS_BITRATE_MIN || this._model.Opus.Bitrate.Surround71 > AppSettingsModel.OPUS_BITRATE_MAX)
            {
                errors.Add("Opus bitrate Surround 7.1 deve essere tra " + AppSettingsModel.OPUS_BITRATE_MIN + " e " + AppSettingsModel.OPUS_BITRATE_MAX + " kbps");
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
            bool result = false;

            // Trim percorsi
            this._model.Tools.MkvMergePath = this._model.Tools.MkvMergePath.Trim();
            this._model.Tools.FfmpegPath = this._model.Tools.FfmpegPath.Trim();
            this._model.Tools.MediaInfoPath = this._model.Tools.MediaInfoPath.Trim();

            // Verifica esistenza mkvmerge
            if (this._model.Tools.MkvMergePath.Length > 0 && !File.Exists(this._model.Tools.MkvMergePath))
            {
                errors.Add("Percorso mkvmerge non trovato: " + this._model.Tools.MkvMergePath);
            }

            // Verifica esistenza ffmpeg
            if (this._model.Tools.FfmpegPath.Length > 0 && !File.Exists(this._model.Tools.FfmpegPath))
            {
                errors.Add("Percorso ffmpeg non trovato: " + this._model.Tools.FfmpegPath);
            }

            // Verifica esistenza mediainfo
            if (this._model.Tools.MediaInfoPath.Length > 0 && !File.Exists(this._model.Tools.MediaInfoPath))
            {
                errors.Add("Percorso mediainfo non trovato: " + this._model.Tools.MediaInfoPath);
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
            if (tempFolder.Length == 0)
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
        /// Pulisce tutti i file temporanei dalla cartella temp
        /// </summary>
        public void CleanupTempFiles()
        {
            string tempFolder = this.GetTempFolder();
            string[] files = null;

            if (Directory.Exists(tempFolder))
            {
                files = Directory.GetFiles(tempFolder);
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        File.Delete(files[i]);
                    }
                    catch
                    {
                        // File in uso o protetto, ignora
                    }
                }
            }
        }

        /// <summary>
        /// Restituisce il bitrate Opus appropriato in base al numero di canali
        /// </summary>
        /// <param name="channels">Numero di canali audio</param>
        /// <returns>Bitrate in kbps</returns>
        public int GetOpusBitrateForChannels(int channels)
        {
            int bitrate = this._model.Opus.Bitrate.Stereo;

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
        /// Restituisce un profilo di encoding per nome
        /// </summary>
        /// <param name="name">Nome del profilo</param>
        /// <returns>Profilo trovato, null se non esiste</returns>
        public EncodingProfile GetProfile(string name)
        {
            EncodingProfile result = null;

            for (int i = 0; i < this._model.EncodingProfiles.Count; i++)
            {
                if (this._model.EncodingProfiles[i].Name == name)
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
            if (this._model.Tools == null) { this._model.Tools = new ToolsConfig(); }
            if (this._model.Flac == null) { this._model.Flac = new FlacConfig(); }
            if (this._model.Opus == null) { this._model.Opus = new OpusConfig(); }
            if (this._model.Opus.Bitrate == null) { this._model.Opus.Bitrate = new OpusBitrateConfig(); }
            if (this._model.Ui == null) { this._model.Ui = new UiConfig(); }
            if (this._model.EncodingProfiles == null) { this._model.EncodingProfiles = new List<EncodingProfile>(); }

            // Assicura stringhe non null nei percorsi tool
            if (this._model.Tools.MkvMergePath == null) { this._model.Tools.MkvMergePath = ""; }
            if (this._model.Tools.FfmpegPath == null) { this._model.Tools.FfmpegPath = ""; }
            if (this._model.Tools.MediaInfoPath == null) { this._model.Tools.MediaInfoPath = ""; }
            if (this._model.Tools.TempFolder == null) { this._model.Tools.TempFolder = ""; }
            if (this._model.Ui.Theme == null) { this._model.Ui.Theme = "nord"; }

            // Assicura sotto-oggetti Advanced non null
            if (this._model.Advanced == null) { this._model.Advanced = new AdvancedConfig(); }
            if (this._model.Advanced.VideoSync == null) { this._model.Advanced.VideoSync = new VideoSyncConfig(); }
            if (this._model.Advanced.SpeedCorrection == null) { this._model.Advanced.SpeedCorrection = new SpeedCorrectionConfig(); }
            if (this._model.Advanced.FrameSync == null) { this._model.Advanced.FrameSync = new FrameSyncConfig(); }
            if (this._model.Advanced.DeepAnalysis == null) { this._model.Advanced.DeepAnalysis = new DeepAnalysisConfig(); }
            if (this._model.Advanced.TrackSplit == null) { this._model.Advanced.TrackSplit = new TrackSplitConfig(); }
            if (this._model.Advanced.Ffmpeg == null) { this._model.Advanced.Ffmpeg = new FfmpegConfig(); }

            // Assicura array DeepAnalysis non null
            if (this._model.Advanced.DeepAnalysis.ProbeMultiMarginsSec == null) { this._model.Advanced.DeepAnalysis.ProbeMultiMarginsSec = new List<double> { 5.0, 15.0, 25.0 }; }
            if (this._model.Advanced.DeepAnalysis.OffsetProbeDeltas == null) { this._model.Advanced.DeepAnalysis.OffsetProbeDeltas = new List<int> { 1000, 2000, 3000, 4000, 5000, -1000, -2000, -3000, -4000, -5000 }; }
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

            // Validazione tema: se non e' tra quelli validi, reset a "nord"
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

            // Sanitizzazione Advanced — VideoSync
            VideoSyncConfig vs = this._model.Advanced.VideoSync;
            vs.FrameWidth = this.ClampInt(vs.FrameWidth, 64, 1920);
            vs.FrameHeight = this.ClampInt(vs.FrameHeight, 64, 1080);
            vs.MseThreshold = this.ClampDouble(vs.MseThreshold, 0.0, 10000.0);
            vs.MseMinThreshold = this.ClampDouble(vs.MseMinThreshold, 0.0, 10000.0);
            vs.SsimThreshold = this.ClampDouble(vs.SsimThreshold, 0.0, 1.0);
            vs.SsimMaxThreshold = this.ClampDouble(vs.SsimMaxThreshold, 0.0, 1.0);
            vs.NumCheckPoints = this.ClampInt(vs.NumCheckPoints, 1, 1000);
            vs.MinValidPoints = this.ClampInt(vs.MinValidPoints, 1, 1000);
            vs.SceneCutThreshold = this.ClampDouble(vs.SceneCutThreshold, 0.0, 10000.0);
            vs.CutHalfWindow = this.ClampInt(vs.CutHalfWindow, 1, 1000);
            vs.CutSignatureLength = this.ClampInt(vs.CutSignatureLength, 2, 1000);
            vs.FingerprintCorrelationThreshold = this.ClampDouble(vs.FingerprintCorrelationThreshold, 0.0, 1.0);
            vs.MinSceneCuts = this.ClampInt(vs.MinSceneCuts, 1, 10000);
            vs.MinCutSpacingFrames = this.ClampInt(vs.MinCutSpacingFrames, 1, 10000);
            vs.VerifySourceDurationSec = this.ClampInt(vs.VerifySourceDurationSec, 1, 3600);
            vs.VerifyLangDurationSec = this.ClampInt(vs.VerifyLangDurationSec, 1, 3600);
            vs.VerifySourceRetrySec = this.ClampInt(vs.VerifySourceRetrySec, 1, 3600);
            vs.VerifyLangRetrySec = this.ClampInt(vs.VerifyLangRetrySec, 1, 3600);

            // Sanitizzazione Advanced — SpeedCorrection
            SpeedCorrectionConfig sc = this._model.Advanced.SpeedCorrection;
            sc.SourceStartSec = this.ClampInt(sc.SourceStartSec, 0, 3600);
            sc.SourceDurationSec = this.ClampInt(sc.SourceDurationSec, 1, 3600);
            sc.LangDurationSec = this.ClampInt(sc.LangDurationSec, 1, 3600);
            sc.MinSpeedRatioDiff = this.ClampDouble(sc.MinSpeedRatioDiff, 0.0001, 1.0);
            sc.MaxDurationDiffTelecine = this.ClampDouble(sc.MaxDurationDiffTelecine, 0.0001, 1.0);

            // Sanitizzazione Advanced — FrameSync
            FrameSyncConfig fs = this._model.Advanced.FrameSync;
            fs.MinDurationMs = this.ClampInt(fs.MinDurationMs, 1000, 600000);
            fs.SourceStartSec = this.ClampInt(fs.SourceStartSec, 0, 3600);
            fs.SourceDurationSec = this.ClampInt(fs.SourceDurationSec, 1, 3600);
            fs.LangDurationSec = this.ClampInt(fs.LangDurationSec, 1, 3600);
            fs.MinValidPoints = this.ClampInt(fs.MinValidPoints, 1, 1000);

            // Sanitizzazione Advanced — DeepAnalysis
            DeepAnalysisConfig da = this._model.Advanced.DeepAnalysis;
            da.CoarseFps = this.ClampDouble(da.CoarseFps, 0.1, 30.0);
            da.DenseScanFps = this.ClampDouble(da.DenseScanFps, 0.1, 30.0);
            da.DenseScanSsimThreshold = this.ClampDouble(da.DenseScanSsimThreshold, 0.0, 1.0);
            da.DenseScanMinDipFrames = this.ClampInt(da.DenseScanMinDipFrames, 1, 1000);
            da.LinearScanWindowSec = this.ClampDouble(da.LinearScanWindowSec, 0.1, 60.0);
            da.LinearScanConfirmFrames = this.ClampInt(da.LinearScanConfirmFrames, 1, 1000);
            da.VerifyDipSsimThreshold = this.ClampDouble(da.VerifyDipSsimThreshold, 0.0, 1.0);
            da.ProbeMinConsistentPoints = this.ClampInt(da.ProbeMinConsistentPoints, 1, 1000);
            da.OffsetProbeDurationSec = this.ClampDouble(da.OffsetProbeDurationSec, 0.1, 60.0);
            da.OffsetProbeMinSsim = this.ClampDouble(da.OffsetProbeMinSsim, 0.0, 1.0);
            da.MinOffsetChangeMs = this.ClampInt(da.MinOffsetChangeMs, 1, 60000);
            da.MinConsecutiveStable = this.ClampInt(da.MinConsecutiveStable, 1, 1000);
            da.SceneThreshold = this.ClampDouble(da.SceneThreshold, 0.0, 1.0);
            da.MatchToleranceMs = this.ClampInt(da.MatchToleranceMs, 1, 60000);
            da.WideProbeToleranceSec = this.ClampDouble(da.WideProbeToleranceSec, 0.1, 120.0);
            da.SceneExtractTimeoutMs = this.ClampInt(da.SceneExtractTimeoutMs, 1000, 3600000);
            da.GlobalVerifyPoints = this.ClampInt(da.GlobalVerifyPoints, 1, 1000);
            da.GlobalVerifyMinRatio = this.ClampDouble(da.GlobalVerifyMinRatio, 0.0, 1.0);
            da.VerifyMseMultiplier = this.ClampDouble(da.VerifyMseMultiplier, 0.1, 100.0);
            da.InitialOffsetRangeSec = this.ClampInt(da.InitialOffsetRangeSec, 1, 3600);
            da.InitialOffsetStepSec = this.ClampDouble(da.InitialOffsetStepSec, 0.01, 60.0);
            da.InitialVotingCuts = this.ClampInt(da.InitialVotingCuts, 1, 10000);

            // Sanitizzazione Advanced — TrackSplit
            TrackSplitConfig ts = this._model.Advanced.TrackSplit;
            ts.FfmpegTimeoutMs = this.ClampInt(ts.FfmpegTimeoutMs, 1000, 3600000);

            // Sanitizzazione array DeepAnalysis: se vuoti, ripristina default
            if (da.ProbeMultiMarginsSec.Count == 0)
            {
                da.ProbeMultiMarginsSec = new List<double> { 5.0, 15.0, 25.0 };
            }
            if (da.OffsetProbeDeltas.Count == 0)
            {
                da.OffsetProbeDeltas = new List<int> { 1000, 2000, 3000, 4000, 5000, -1000, -2000, -3000, -4000, -5000 };
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
        /// Limita un valore intero entro un range
        /// </summary>
        /// <param name="value">Valore da limitare</param>
        /// <param name="min">Minimo consentito</param>
        /// <param name="max">Massimo consentito</param>
        /// <returns>Valore limitato nel range</returns>
        private int ClampInt(int value, int min, int max)
        {
            int result = value;

            if (result < min) { result = min; }
            if (result > max) { result = max; }

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

            if (result < min) { result = min; }
            if (result > max) { result = max; }

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
                if (this._model.EncodingProfiles[i].Name == null || this._model.EncodingProfiles[i].Name.Length == 0)
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
