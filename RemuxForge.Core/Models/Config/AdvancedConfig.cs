using System;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Backend SIFT disponibile per le analisi visuali
    /// </summary>
    public enum SiftBackendKind
    {
        /// <summary>
        /// Valore assente o non riconosciuto
        /// </summary>
        Unknown,

        /// <summary>
        /// Implementazione OpenCV eseguita sulla CPU
        /// </summary>
        Cpu,

        /// <summary>
        /// Implementazione RemuxForge.Vulkan eseguita sulla GPU
        /// </summary>
        Vulkan
    }

    /// <summary>
    /// Configurazione parametri base sincronizzazione video (VideoSyncServiceBase)
    /// </summary>
    public class VideoSyncConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public VideoSyncConfig()
        {
            this.FrameWidth = 320;
            this.FrameHeight = 240;
            this.NumCheckPoints = 9;
            this.VerifySourceDurationSec = 10;
            this.VerifyLangDurationSec = 15;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Larghezza frame di analisi in pixel
        /// </summary>
        public int FrameWidth { get; set; }

        /// <summary>
        /// Altezza frame di analisi in pixel
        /// </summary>
        public int FrameHeight { get; set; }

        /// <summary>
        /// Numero di punti di verifica distribuiti nel video
        /// </summary>
        public int NumCheckPoints { get; set; }

        /// <summary>
        /// Durata in secondi dell'estrazione source per verifica
        /// </summary>
        public int VerifySourceDurationSec { get; set; }

        /// <summary>
        /// Durata in secondi dell'estrazione lang per verifica
        /// </summary>
        public int VerifyLangDurationSec { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione parametri correzione velocità (SpeedCorrectionService)
    /// </summary>
    public class SpeedCorrectionConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public SpeedCorrectionConfig()
        {
            this.SourceStartSec = 0;
            this.SourceDurationSec = 300;
            this.LangDurationSec = 375;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Secondo di inizio estrazione source
        /// </summary>
        public int SourceStartSec { get; set; }

        /// <summary>
        /// Durata in secondi dell'estrazione source
        /// </summary>
        public int SourceDurationSec { get; set; }

        /// <summary>
        /// Durata in secondi dell'estrazione lang
        /// </summary>
        public int LangDurationSec { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione parametri sincronizzazione frame (FrameSyncService)
    /// </summary>
    public class FrameSyncConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public FrameSyncConfig()
        {
            this.MinDurationMs = 10000;
            this.SourceStartSec = 1;
            this.SourceDurationSec = 120;
            this.LangDurationSec = 180;
            this.MinValidPoints = 5;
            this.FinalMinConfidence = 0.35;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Durata minima video in millisecondi per procedere con sync
        /// </summary>
        public int MinDurationMs { get; set; }

        /// <summary>
        /// Secondo di inizio estrazione source
        /// </summary>
        public int SourceStartSec { get; set; }

        /// <summary>
        /// Durata in secondi dell'estrazione source
        /// </summary>
        public int SourceDurationSec { get; set; }

        /// <summary>
        /// Durata in secondi dell'estrazione lang
        /// </summary>
        public int LangDurationSec { get; set; }

        /// <summary>
        /// Numero minimo di punti validi richiesti
        /// </summary>
        public int MinValidPoints { get; set; }

        /// <summary>
        /// Confidence finale minima per applicare offset
        /// </summary>
        public double FinalMinConfidence { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione della pipeline DeepAnalysis SIFT
    /// </summary>
    public class DeepAnalysisConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public DeepAnalysisConfig()
        {
            this.SceneExtractTimeoutMs = 600000;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Timeout in millisecondi per estrazione scene con ffmpeg
        /// </summary>
        public int SceneExtractTimeoutMs { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione parametri riscrittura sottotitoli
    /// </summary>
    public class SubtitleEditConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public SubtitleEditConfig()
        {
            this.FfmpegTimeoutMs = 300000;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Timeout singolo comando ffmpeg in millisecondi
        /// </summary>
        public int FfmpegTimeoutMs { get; set; }

        #endregion
    }

    /// <summary>
    /// Configurazione parametri ffmpeg (accelerazione hardware)
    /// </summary>
    public class FfmpegConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public FfmpegConfig()
        {
            this.HardwareAcceleration = false;
            this.HardwareAccelerationMethod = "";
            this.FrameExtractionTimeoutMs = 120000;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Abilita accelerazione hardware ffmpeg con il metodo selezionato
        /// </summary>
        public bool HardwareAcceleration { get; set; }

        /// <summary>
        /// Metodo hardware ffmpeg verificato e selezionato dall'utente
        /// </summary>
        public string HardwareAccelerationMethod { get; set; }

        /// <summary>
        /// Timeout singola estrazione frame rawvideo in millisecondi
        /// </summary>
        public int FrameExtractionTimeoutMs { get; set; }

        /// <summary>
        /// Verifica che il metodo hardware sia un identificatore ffmpeg esplicito e sicuro
        /// </summary>
        /// <param name="method">Metodo da validare</param>
        /// <returns>True per un identificatore esplicito valido</returns>
        public static bool IsValidHardwareAccelerationMethod(string method)
        {
            if (string.IsNullOrEmpty(method) || method == "auto" || method == "none")
                return false;

            for (int i = 0; i < method.Length; i++)
            {
                char current = method[i];
                if ((current < 'a' || current > 'z') && (current < '0' || current > '9') && current != '_')
                    return false;
            }

            return true;
        }

        #endregion
    }

    /// <summary>
    /// Contenitore configurazione avanzata con tutte le sotto-sezioni
    /// </summary>
    public class AdvancedConfig
    {
        #region Costanti

        /// <summary>
        /// Valore persistito del backend CPU
        /// </summary>
        private const string SIFT_BACKEND_CPU = "cpu";

        /// <summary>
        /// Valore persistito del backend Vulkan
        /// </summary>
        private const string SIFT_BACKEND_VULKAN = "vulkan";

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default per tutte le sotto-sezioni
        /// </summary>
        public AdvancedConfig()
        {
            this.SiftBackend = SIFT_BACKEND_CPU;
            this.VideoSync = new VideoSyncConfig();
            this.SpeedCorrection = new SpeedCorrectionConfig();
            this.FrameSync = new FrameSyncConfig();
            this.DeepAnalysis = new DeepAnalysisConfig();
            this.SubtitleEdit = new SubtitleEditConfig();
            this.Ffmpeg = new FfmpegConfig();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Converte il valore persistito nel backend operativo
        /// </summary>
        /// <returns>Backend SIFT configurato oppure <see cref="SiftBackendKind.Unknown"/></returns>
        public SiftBackendKind GetSiftBackendKind()
        {
            TryParseSiftBackend(this.SiftBackend, out SiftBackendKind backend);
            return backend;
        }

        /// <summary>
        /// Imposta il backend operativo usando il valore persistito canonico
        /// </summary>
        /// <param name="backend">Backend SIFT da configurare</param>
        public void SetSiftBackendKind(SiftBackendKind backend)
        {
            this.SiftBackend = GetSiftBackendValue(backend);
        }

        /// <summary>
        /// Converte un valore persistito nel relativo enum operativo
        /// </summary>
        /// <param name="value">Valore letto dalla configurazione o dalla UI</param>
        /// <param name="backend">Backend SIFT riconosciuto</param>
        /// <returns>True se il valore identifica un backend supportato</returns>
        public static bool TryParseSiftBackend(string value, out SiftBackendKind backend)
        {
            if (string.Equals(value, SIFT_BACKEND_CPU, StringComparison.OrdinalIgnoreCase))
            {
                backend = SiftBackendKind.Cpu;
                return true;
            }
            if (string.Equals(value, SIFT_BACKEND_VULKAN, StringComparison.OrdinalIgnoreCase))
            {
                backend = SiftBackendKind.Vulkan;
                return true;
            }

            backend = SiftBackendKind.Unknown;
            return false;
        }

        /// <summary>
        /// Restituisce il valore persistibile canonico di un backend operativo
        /// </summary>
        /// <param name="backend">Backend SIFT operativo</param>
        /// <returns>Valore in minuscolo usato da JSON e UI</returns>
        public static string GetSiftBackendValue(SiftBackendKind backend)
        {
            switch (backend)
            {
                case SiftBackendKind.Cpu:
                    return SIFT_BACKEND_CPU;
                case SiftBackendKind.Vulkan:
                    return SIFT_BACKEND_VULKAN;
                default:
                    throw new ArgumentOutOfRangeException(nameof(backend));
            }
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Backend SIFT condiviso da FrameSync, SpeedCorrection e DeepAnalysis
        /// </summary>
        public string SiftBackend { get; set; }

        /// <summary>
        /// Parametri base sincronizzazione video
        /// </summary>
        public VideoSyncConfig VideoSync { get; set; }

        /// <summary>
        /// Parametri correzione velocità
        /// </summary>
        public SpeedCorrectionConfig SpeedCorrection { get; set; }

        /// <summary>
        /// Parametri sincronizzazione frame
        /// </summary>
        public FrameSyncConfig FrameSync { get; set; }

        /// <summary>
        /// Parametri deep analysis
        /// </summary>
        public DeepAnalysisConfig DeepAnalysis { get; set; }

        /// <summary>
        /// Parametri riscrittura sottotitoli
        /// </summary>
        public SubtitleEditConfig SubtitleEdit { get; set; }

        /// <summary>
        /// Parametri ffmpeg
        /// </summary>
        public FfmpegConfig Ffmpeg { get; set; }

        #endregion
    }
}
