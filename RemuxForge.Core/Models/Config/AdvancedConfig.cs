using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
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
            this.SsimThreshold = 0.55;
            this.SsimMaxThreshold = 0.999;
            this.NumCheckPoints = 9;
            this.MinValidPoints = 5;
            this.SceneCutThreshold = 50.0;
            this.CutHalfWindow = 5;
            this.CutSignatureLength = 10;
            this.FingerprintCorrelationThreshold = 0.80;
            this.MinSceneCuts = 3;
            this.MinCutSpacingFrames = 24;
            this.VerifySourceDurationSec = 10;
            this.VerifyLangDurationSec = 15;
            this.VerifySourceRetrySec = 20;
            this.VerifyLangRetrySec = 30;
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
        /// Soglia SSIM minima per considerare due frame corrispondenti
        /// </summary>
        public double SsimThreshold { get; set; }

        /// <summary>
        /// Soglia SSIM massima per escludere frame troppo simili (scene statiche)
        /// </summary>
        public double SsimMaxThreshold { get; set; }

        /// <summary>
        /// Numero di punti di verifica distribuiti nel video
        /// </summary>
        public int NumCheckPoints { get; set; }

        /// <summary>
        /// Numero minimo di punti validi richiesti per confermare la sincronizzazione
        /// </summary>
        public int MinValidPoints { get; set; }

        /// <summary>
        /// Soglia differenza media pixel per rilevare un cambio scena
        /// </summary>
        public double SceneCutThreshold { get; set; }

        /// <summary>
        /// Metà della finestra di frame intorno a un taglio scena
        /// </summary>
        public int CutHalfWindow { get; set; }

        /// <summary>
        /// Lunghezza della firma di taglio scena in frame
        /// </summary>
        public int CutSignatureLength { get; set; }

        /// <summary>
        /// Soglia minima di correlazione Pearson per match fingerprint
        /// </summary>
        public double FingerprintCorrelationThreshold { get; set; }

        /// <summary>
        /// Numero minimo di tagli scena richiesti per procedere
        /// </summary>
        public int MinSceneCuts { get; set; }

        /// <summary>
        /// Distanza minima in frame tra due tagli scena consecutivi
        /// </summary>
        public int MinCutSpacingFrames { get; set; }

        /// <summary>
        /// Durata in secondi dell'estrazione source per verifica
        /// </summary>
        public int VerifySourceDurationSec { get; set; }

        /// <summary>
        /// Durata in secondi dell'estrazione lang per verifica
        /// </summary>
        public int VerifyLangDurationSec { get; set; }

        /// <summary>
        /// Durata in secondi dell'estrazione source per retry verifica
        /// </summary>
        public int VerifySourceRetrySec { get; set; }

        /// <summary>
        /// Durata in secondi dell'estrazione lang per retry verifica
        /// </summary>
        public int VerifyLangRetrySec { get; set; }

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
            this.SiftBackend = "cpu";
            this.SourceStartSec = 0;
            this.SourceDurationSec = 300;
            this.LangDurationSec = 375;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Backend SIFT: cpu oppure vulkan
        /// </summary>
        public string SiftBackend { get; set; }

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
            this.GroupingToleranceFrames = 1;
            this.MinEdgeCorrelation = 0.70;
            this.MinBlockCorrelation = 0.72;
            this.MinMotionCorrelation = 0.58;
            this.MinBlurredCorrelation = 0.70;
            this.MinHashSimilarity = 0.78;
            this.MinDescriptorVotes = 2;
            this.InitialMinMatchedCuts = 3;
            this.InitialMinScore = 0.62;
            this.CheckpointMinScore = 0.58;
            this.FinalMinConfidence = 0.35;
            this.InitialCheckpointDriftPenaltyFrames = 3;
            this.InitialCheckpointDriftRejectFrames = 12;
            this.InitialMinMargin = 0.05;
            this.CheckpointMinMargin = 0.04;
            this.StaticSegmentVarianceThreshold = 8.0;
            this.BlackFrameRatioThreshold = 0.92;
            this.AudioGlobalEnabled = true;
            this.AudioGlobalSampleRate = 8000;
            this.AudioGlobalWindowMs = 50;
            this.AudioGlobalSearchRangeMs = 30000;
            this.AudioGlobalCoarseStepMs = 100;
            this.AudioGlobalMinScore = 0.62;
            this.AudioGlobalMinMargin = 0.04;
            this.AudioGlobalMinCoverage = 0.55;
            this.AudioGlobalConfirmToleranceFrames = 2;
            this.AudioGlobalRejectToleranceFrames = 8;
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
        /// Tolleranza raggruppamento offset in frame (1 = 1 frame, 2 = 2 frame)
        /// </summary>
        public int GroupingToleranceFrames { get; set; }

        /// <summary>
        /// Correlazione minima edge per voto descriptor
        /// </summary>
        public double MinEdgeCorrelation { get; set; }

        /// <summary>
        /// Correlazione minima block fingerprint per voto descriptor
        /// </summary>
        public double MinBlockCorrelation { get; set; }

        /// <summary>
        /// Correlazione minima block-motion per voto descriptor
        /// </summary>
        public double MinMotionCorrelation { get; set; }

        /// <summary>
        /// Correlazione minima blur/denoise per voto descriptor
        /// </summary>
        public double MinBlurredCorrelation { get; set; }

        /// <summary>
        /// Similarità minima hash percettivo per voto descriptor
        /// </summary>
        public double MinHashSimilarity { get; set; }

        /// <summary>
        /// Numero minimo di descriptor concordanti
        /// </summary>
        public int MinDescriptorVotes { get; set; }

        /// <summary>
        /// Numero minimo di tagli verificati richiesti solo per il candidato iniziale
        /// </summary>
        public int InitialMinMatchedCuts { get; set; }

        /// <summary>
        /// Score minimo candidato iniziale
        /// </summary>
        public double InitialMinScore { get; set; }

        /// <summary>
        /// Score minimo checkpoint
        /// </summary>
        public double CheckpointMinScore { get; set; }

        /// <summary>
        /// Confidence finale minima per applicare offset
        /// </summary>
        public double FinalMinConfidence { get; set; }

        /// <summary>
        /// Delta initial/checkpoint in frame oltre cui loggare e penalizzare la confidence
        /// </summary>
        public int InitialCheckpointDriftPenaltyFrames { get; set; }

        /// <summary>
        /// Delta initial/checkpoint in frame oltre cui il risultato è troppo sospetto
        /// </summary>
        public int InitialCheckpointDriftRejectFrames { get; set; }

        /// <summary>
        /// Margine minimo tra primo e secondo candidato iniziale
        /// </summary>
        public double InitialMinMargin { get; set; }

        /// <summary>
        /// Margine minimo tra primo e secondo candidato checkpoint
        /// </summary>
        public double CheckpointMinMargin { get; set; }

        /// <summary>
        /// Varianza sotto cui un segmento è considerato statico/piatto
        /// </summary>
        public double StaticSegmentVarianceThreshold { get; set; }

        /// <summary>
        /// Rapporto pixel scuri sopra cui un segmento è considerato nero
        /// </summary>
        public double BlackFrameRatioThreshold { get; set; }

        /// <summary>
        /// Abilita fingerprint audio globale come fallback/metrica di consenso
        /// </summary>
        public bool AudioGlobalEnabled { get; set; }

        /// <summary>
        /// Sample rate PCM usato per fingerprint audio
        /// </summary>
        public int AudioGlobalSampleRate { get; set; }

        /// <summary>
        /// Finestra fingerprint audio in millisecondi
        /// </summary>
        public int AudioGlobalWindowMs { get; set; }

        /// <summary>
        /// Range massimo offset audio globale
        /// </summary>
        public int AudioGlobalSearchRangeMs { get; set; }

        /// <summary>
        /// Step coarse offset audio globale
        /// </summary>
        public int AudioGlobalCoarseStepMs { get; set; }

        /// <summary>
        /// Score minimo audio globale
        /// </summary>
        public double AudioGlobalMinScore { get; set; }

        /// <summary>
        /// Margine minimo audio globale
        /// </summary>
        public double AudioGlobalMinMargin { get; set; }

        /// <summary>
        /// Copertura minima audio globale
        /// </summary>
        public double AudioGlobalMinCoverage { get; set; }

        /// <summary>
        /// Delta audio/video in frame entro cui l'audio conferma un initial debole
        /// </summary>
        public int AudioGlobalConfirmToleranceFrames { get; set; }

        /// <summary>
        /// Delta audio/video in frame oltre cui l'audio boccia un initial debole
        /// </summary>
        public int AudioGlobalRejectToleranceFrames { get; set; }

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
            this.SiftBackend = "cpu";
            this.SceneExtractTimeoutMs = 600000;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Backend SIFT: cpu oppure vulkan
        /// </summary>
        public string SiftBackend { get; set; }

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
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default per tutte le sotto-sezioni
        /// </summary>
        public AdvancedConfig()
        {
            this.VideoSync = new VideoSyncConfig();
            this.SpeedCorrection = new SpeedCorrectionConfig();
            this.FrameSync = new FrameSyncConfig();
            this.DeepAnalysis = new DeepAnalysisConfig();
            this.SubtitleEdit = new SubtitleEditConfig();
            this.Ffmpeg = new FfmpegConfig();
        }

        #endregion

        #region Proprietà

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
