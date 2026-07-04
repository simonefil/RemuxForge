using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Configurazione percorsi tool esterni
    /// </summary>
    public class ToolsConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public ToolsConfig()
        {
            this.MkvMergePath = "";
            this.MkvExtractPath = "";
            this.MkvPropEditPath = "";
            this.FfmpegPath = "";
            this.FfprobePath = "";
            this.MediaInfoPath = "";
            this.TempFolder = "";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Percorso mkvmerge
        /// </summary>
        public string MkvMergePath { get; set; }

        /// <summary>
        /// Percorso mkvextract
        /// </summary>
        public string MkvExtractPath { get; set; }

        /// <summary>
        /// Percorso mkvpropedit
        /// </summary>
        public string MkvPropEditPath { get; set; }

        /// <summary>
        /// Percorso ffmpeg
        /// </summary>
        public string FfmpegPath { get; set; }

        /// <summary>
        /// Percorso ffprobe
        /// </summary>
        public string FfprobePath { get; set; }

        /// <summary>
        /// Percorso mediainfo
        /// </summary>
        public string MediaInfoPath { get; set; }

        /// <summary>
        /// Percorso cartella file temporanei
        /// </summary>
        public string TempFolder { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione compressione FLAC
    /// </summary>
    public class FlacConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public FlacConfig()
        {
            this.CompressionLevel = 8;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Livello compressione (0-12)
        /// </summary>
        public int CompressionLevel { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione bitrate Opus per canale
    /// </summary>
    public class OpusBitrateConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public OpusBitrateConfig()
        {
            this.Mono = 128;
            this.Stereo = 256;
            this.Surround51 = 510;
            this.Surround71 = 768;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Bitrate mono in kbps
        /// </summary>
        public int Mono { get; set; }

        /// <summary>
        /// Bitrate stereo in kbps
        /// </summary>
        public int Stereo { get; set; }

        /// <summary>
        /// Bitrate surround 5.1 in kbps
        /// </summary>
        public int Surround51 { get; set; }

        /// <summary>
        /// Bitrate surround 7.1 in kbps
        /// </summary>
        public int Surround71 { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione codec Opus
    /// </summary>
    public class OpusConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public OpusConfig()
        {
            this.Bitrate = new OpusBitrateConfig();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Bitrate per canale
        /// </summary>
        public OpusBitrateConfig Bitrate { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione bitrate AAC per canale
    /// </summary>
    public class AacBitrateConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public AacBitrateConfig()
        {
            this.Mono = 128;
            this.Stereo = 256;
            this.Surround51 = 768;
            this.Surround71 = 1024;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Bitrate mono in kbps
        /// </summary>
        public int Mono { get; set; }

        /// <summary>
        /// Bitrate stereo in kbps
        /// </summary>
        public int Stereo { get; set; }

        /// <summary>
        /// Bitrate surround 5.1 in kbps
        /// </summary>
        public int Surround51 { get; set; }

        /// <summary>
        /// Bitrate surround 7.1 in kbps
        /// </summary>
        public int Surround71 { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione codec AAC
    /// </summary>
    public class AacConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public AacConfig()
        {
            this.Bitrate = new AacBitrateConfig();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Bitrate per canale
        /// </summary>
        public AacBitrateConfig Bitrate { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione bitrate AC-3 per gruppo canali supportato
    /// </summary>
    public class Ac3BitrateConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default ad alta qualità
        /// </summary>
        public Ac3BitrateConfig()
        {
            this.Mono = 192;
            this.Stereo = 384;
            this.Surround51 = 640;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Bitrate mono in kbps
        /// </summary>
        public int Mono { get; set; }

        /// <summary>
        /// Bitrate stereo in kbps
        /// </summary>
        public int Stereo { get; set; }

        /// <summary>
        /// Bitrate surround 5.1 in kbps, usato anche per downmix da layout superiori
        /// </summary>
        public int Surround51 { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione codec AC-3
    /// </summary>
    public class Ac3Config
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public Ac3Config()
        {
            this.Bitrate = new Ac3BitrateConfig();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Bitrate per gruppo canali
        /// </summary>
        public Ac3BitrateConfig Bitrate { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione interfaccia utente
    /// </summary>
    public class UiConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public UiConfig()
        {
            this.Theme = "nord";
            this.LastMode = Options.MODE_REMUX;
            this.Language = "en";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Tema grafico selezionato (kebab-case)
        /// </summary>
        public string Theme { get; set; }

        /// <summary>
        /// Ultima modalità selezionata nella UI
        /// </summary>
        public string LastMode { get; set; }

        /// <summary>
        /// Lingua UI/CLI selezionata
        /// </summary>
        public string Language { get; set; }

        #endregion
    }

    /// <summary>
    /// Modello impostazioni applicazione per serializzazione JSON
    /// </summary>
    public class AppSettingsModel
    {
        #region Costanti di validazione

        /// <summary>
        /// Livello compressione FLAC minimo
        /// </summary>
        public const int FLAC_COMPRESSION_MIN = 0;

        /// <summary>
        /// Livello compressione FLAC massimo
        /// </summary>
        public const int FLAC_COMPRESSION_MAX = 12;

        /// <summary>
        /// Bitrate Opus minimo in kbps
        /// </summary>
        public const int OPUS_BITRATE_MIN = 64;

        /// <summary>
        /// Bitrate Opus massimo in kbps
        /// </summary>
        public const int OPUS_BITRATE_MAX = 768;

        /// <summary>
        /// Bitrate AAC minimo in kbps
        /// </summary>
        public const int AAC_BITRATE_MIN = 32;

        /// <summary>
        /// Bitrate AAC massimo in kbps
        /// </summary>
        public const int AAC_BITRATE_MAX = 1536;

        /// <summary>
        /// Bitrate AC-3 validi in kbps secondo A/52
        /// </summary>
        public static readonly int[] AC3_VALID_BITRATES_KBPS = new int[] { 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640 };

        /// <summary>
        /// Temi validi
        /// </summary>
        public static readonly string[] VALID_THEMES = { "dark", "nord", "dos-blue", "matrix", "cyberpunk", "solarized-dark", "solarized-light", "cybergum", "everforest" };

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public AppSettingsModel()
        {
            this.Tools = new ToolsConfig();
            this.Flac = new FlacConfig();
            this.Opus = new OpusConfig();
            this.Aac = new AacConfig();
            this.Ac3 = new Ac3Config();
            this.Ui = new UiConfig();
            this.EncodingProfiles = new List<EncodingProfile>();
            this.Advanced = new AdvancedConfig();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Configurazione percorsi tool esterni
        /// </summary>
        public ToolsConfig Tools { get; set; }

        /// <summary>
        /// Configurazione compressione FLAC
        /// </summary>
        public FlacConfig Flac { get; set; }

        /// <summary>
        /// Configurazione codec Opus
        /// </summary>
        public OpusConfig Opus { get; set; }

        /// <summary>
        /// Configurazione codec AAC
        /// </summary>
        public AacConfig Aac { get; set; }

        /// <summary>
        /// Configurazione codec AC-3
        /// </summary>
        public Ac3Config Ac3 { get; set; }

        /// <summary>
        /// Configurazione interfaccia utente
        /// </summary>
        public UiConfig Ui { get; set; }

        /// <summary>
        /// Lista profili di encoding video
        /// </summary>
        public List<EncodingProfile> EncodingProfiles { get; set; }

        /// <summary>
        /// Configurazione avanzata parametri algoritmici
        /// </summary>
        public AdvancedConfig Advanced { get; set; }

        #endregion
    }
}
