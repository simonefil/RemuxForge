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
            this.MseThreshold = 100.0;
            this.MseMinThreshold = 0.05;
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

        #region Proprieta

        /// <summary>
        /// Larghezza frame di analisi in pixel
        /// </summary>
        public int FrameWidth { get; set; }

        /// <summary>
        /// Altezza frame di analisi in pixel
        /// </summary>
        public int FrameHeight { get; set; }

        /// <summary>
        /// Soglia MSE massima per considerare due frame simili
        /// </summary>
        public double MseThreshold { get; set; }

        /// <summary>
        /// Soglia MSE minima per escludere frame neri o identici
        /// </summary>
        public double MseMinThreshold { get; set; }

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
        /// Meta' della finestra di frame intorno a un taglio scena
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
    /// Configurazione parametri correzione velocita' (SpeedCorrectionService)
    /// </summary>
    public class SpeedCorrectionConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public SpeedCorrectionConfig()
        {
            this.SourceStartSec = 1;
            this.SourceDurationSec = 120;
            this.LangDurationSec = 180;
            this.MinSpeedRatioDiff = 0.001;
            this.MaxDurationDiffTelecine = 0.005;
        }

        #endregion

        #region Proprieta

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
        /// Differenza minima di speed ratio per applicare correzione
        /// </summary>
        public double MinSpeedRatioDiff { get; set; }

        /// <summary>
        /// Differenza massima durata relativa per rilevamento telecine
        /// </summary>
        public double MaxDurationDiffTelecine { get; set; }

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

        #region Proprieta

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
        /// Similarita' minima hash percettivo per voto descriptor
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
        /// Delta initial/checkpoint in frame oltre cui il risultato e' troppo sospetto
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
        /// Varianza sotto cui un segmento e' considerato statico/piatto
        /// </summary>
        public double StaticSegmentVarianceThreshold { get; set; }

        /// <summary>
        /// Rapporto pixel scuri sopra cui un segmento e' considerato nero
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
    /// Configurazione parametri deep analysis (DeepAnalysisService)
    /// </summary>
    public class DeepAnalysisConfig
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con valori di default
        /// </summary>
        public DeepAnalysisConfig()
        {
            this.CoarseFps = 2.0;
            this.DenseScanFps = 1.0;
            this.DenseScanSsimThreshold = 0.5;
            this.DenseScanMinDipFrames = 2;
            this.LinearScanWindowSec = 3.0;
            this.LinearScanConfirmFrames = 5;
            this.VerifyDipSsimThreshold = 0.2;
            this.ProbeMultiMarginsSec = new List<double> { 5.0, 15.0, 25.0 };
            this.ProbeMinConsistentPoints = 2;
            this.OffsetProbeDurationSec = 3.0;
            this.OffsetProbeDeltas = new List<int> { 1000, 2000, 3000, 4000, 5000, -1000, -2000, -3000, -4000, -5000 };
            this.OffsetProbeMinSsim = 0.7;
            this.MinOffsetChangeMs = 500;
            this.MinConsecutiveStable = 5;
            this.SceneThreshold = 0.3;
            this.MatchToleranceMs = 250;
            this.WideProbeToleranceSec = 15.0;
            this.SceneExtractTimeoutMs = 600000;
            this.GlobalVerifyPoints = 30;
            this.GlobalVerifyMinRatio = 0.80;
            this.VerifyMseMultiplier = 3.0;
            this.InitialOffsetRangeSec = 30;
            this.InitialOffsetStepSec = 0.5;
            this.InitialVotingCuts = 50;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// FPS per scansione grossolana (coarse pass)
        /// </summary>
        public double CoarseFps { get; set; }

        /// <summary>
        /// FPS per scansione densa (dense scan)
        /// </summary>
        public double DenseScanFps { get; set; }

        /// <summary>
        /// Soglia SSIM per rilevare dip nella scansione densa
        /// </summary>
        public double DenseScanSsimThreshold { get; set; }

        /// <summary>
        /// Numero minimo di frame consecutivi in dip per confermare un cambio
        /// </summary>
        public int DenseScanMinDipFrames { get; set; }

        /// <summary>
        /// Finestra temporale in secondi per scansione lineare
        /// </summary>
        public double LinearScanWindowSec { get; set; }

        /// <summary>
        /// Numero di frame di conferma per scansione lineare
        /// </summary>
        public int LinearScanConfirmFrames { get; set; }

        /// <summary>
        /// Soglia SSIM per verifica dip (valore basso = dip confermato)
        /// </summary>
        public double VerifyDipSsimThreshold { get; set; }

        /// <summary>
        /// Margini temporali in secondi per probe multi-punto
        /// </summary>
        public List<double> ProbeMultiMarginsSec { get; set; }

        /// <summary>
        /// Numero minimo di punti consistenti nel probe multi-punto
        /// </summary>
        public int ProbeMinConsistentPoints { get; set; }

        /// <summary>
        /// Durata in secondi dell'estrazione per offset probe
        /// </summary>
        public double OffsetProbeDurationSec { get; set; }

        /// <summary>
        /// Delta offset in millisecondi da provare nel probe
        /// </summary>
        public List<int> OffsetProbeDeltas { get; set; }

        /// <summary>
        /// Soglia SSIM minima per accettare un risultato del probe offset
        /// </summary>
        public double OffsetProbeMinSsim { get; set; }

        /// <summary>
        /// Variazione minima offset in millisecondi per considerare un cambio significativo
        /// </summary>
        public int MinOffsetChangeMs { get; set; }

        /// <summary>
        /// Numero minimo di punti consecutivi stabili per confermare un segmento
        /// </summary>
        public int MinConsecutiveStable { get; set; }

        /// <summary>
        /// Soglia scene change passata a ffmpeg (0.0-1.0)
        /// </summary>
        public double SceneThreshold { get; set; }

        /// <summary>
        /// Tolleranza match in millisecondi per corrispondenza scene cut
        /// </summary>
        public int MatchToleranceMs { get; set; }

        /// <summary>
        /// Tolleranza temporale in secondi per probe ampio
        /// </summary>
        public double WideProbeToleranceSec { get; set; }

        /// <summary>
        /// Timeout in millisecondi per estrazione scene con ffmpeg
        /// </summary>
        public int SceneExtractTimeoutMs { get; set; }

        /// <summary>
        /// Numero di punti per verifica globale finale
        /// </summary>
        public int GlobalVerifyPoints { get; set; }

        /// <summary>
        /// Rapporto minimo punti validi su totale per verifica globale
        /// </summary>
        public double GlobalVerifyMinRatio { get; set; }

        /// <summary>
        /// Moltiplicatore MSE per soglia dinamica nella verifica
        /// </summary>
        public double VerifyMseMultiplier { get; set; }

        /// <summary>
        /// Range iniziale in secondi per ricerca offset
        /// </summary>
        public int InitialOffsetRangeSec { get; set; }

        /// <summary>
        /// Step in secondi per ricerca offset iniziale
        /// </summary>
        public double InitialOffsetStepSec { get; set; }

        /// <summary>
        /// Numero di tagli scena per voting iniziale
        /// </summary>
        public int InitialVotingCuts { get; set; }

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

        #region Proprieta

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
            this.FrameExtractionTimeoutMs = 120000;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Abilita accelerazione hardware ffmpeg (-hwaccel auto)
        /// </summary>
        public bool HardwareAcceleration { get; set; }

        /// <summary>
        /// Timeout singola estrazione frame rawvideo in millisecondi
        /// </summary>
        public int FrameExtractionTimeoutMs { get; set; }

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

        #region Proprieta

        /// <summary>
        /// Parametri base sincronizzazione video
        /// </summary>
        public VideoSyncConfig VideoSync { get; set; }

        /// <summary>
        /// Parametri correzione velocita'
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
