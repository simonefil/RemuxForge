namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Risultato della verifica frame-sync in un checkpoint
    /// </summary>
    public class FrameSyncPointResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public FrameSyncPointResult()
        {
            this.CheckpointPercent = 0;
            this.ExpectedOffsetMs = 0;
            this.BestOffsetMs = int.MinValue;
            this.BestScore = 0.0;
            this.BlurScore = 0.0;
            this.SecondBestScore = 0.0;
            this.Margin = 0.0;
            this.DescriptorVotes = 0;
            this.DescriptorAgreement = 0.0;
            this.MotionScore = 0.0;
            this.SourceVariance = 0.0;
            this.LanguageVariance = 0.0;
            this.SourceBlackRatio = 0.0;
            this.LanguageBlackRatio = 0.0;
            this.Accepted = false;
            this.RejectReason = "";
            this.MatchMethod = "";
            this.TimingMs = 0;
            this.ExtractMs = 0;
            this.SceneCutMs = 0;
            this.CandidateMs = 0;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Percentuale del video verificata
        /// </summary>
        public int CheckpointPercent { get; set; }

        /// <summary>
        /// Offset atteso in millisecondi
        /// </summary>
        public int ExpectedOffsetMs { get; set; }

        /// <summary>
        /// Miglior offset trovato in millisecondi
        /// </summary>
        public int BestOffsetMs { get; set; }

        /// <summary>
        /// Score del miglior match
        /// </summary>
        public double BestScore { get; set; }

        /// <summary>
        /// Score luma blur/denoise del miglior match
        /// </summary>
        public double BlurScore { get; set; }

        /// <summary>
        /// Score del secondo miglior match
        /// </summary>
        public double SecondBestScore { get; set; }

        /// <summary>
        /// Margine tra miglior match e secondo miglior match
        /// </summary>
        public double Margin { get; set; }

        /// <summary>
        /// Numero descriptor visuali concordanti nel miglior match
        /// </summary>
        public int DescriptorVotes { get; set; }

        /// <summary>
        /// Quota descriptor visuali concordanti nel miglior match
        /// </summary>
        public double DescriptorAgreement { get; set; }

        /// <summary>
        /// Score movimento a blocchi inter-frame nel miglior match
        /// </summary>
        public double MotionScore { get; set; }

        /// <summary>
        /// Varianza luma media segmento sorgente
        /// </summary>
        public double SourceVariance { get; set; }

        /// <summary>
        /// Varianza luma media segmento lingua
        /// </summary>
        public double LanguageVariance { get; set; }

        /// <summary>
        /// Percentuale pixel molto scuri nel segmento sorgente
        /// </summary>
        public double SourceBlackRatio { get; set; }

        /// <summary>
        /// Percentuale pixel molto scuri nel segmento lingua
        /// </summary>
        public double LanguageBlackRatio { get; set; }

        /// <summary>
        /// True se il checkpoint è accettato
        /// </summary>
        public bool Accepted { get; set; }

        /// <summary>
        /// Motivo rifiuto, se presente
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Metodo usato per il match
        /// </summary>
        public string MatchMethod { get; set; }

        /// <summary>
        /// Tempo totale verifica checkpoint
        /// </summary>
        public long TimingMs { get; set; }

        /// <summary>
        /// Tempo estrazione frame checkpoint
        /// </summary>
        public long ExtractMs { get; set; }

        /// <summary>
        /// Tempo rilevamento scene-cut checkpoint
        /// </summary>
        public long SceneCutMs { get; set; }

        /// <summary>
        /// Tempo build/verifica candidati checkpoint
        /// </summary>
        public long CandidateMs { get; set; }

        #endregion
    }
}
