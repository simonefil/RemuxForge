namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Risultato SIFT della verifica FrameSync in un checkpoint
    /// </summary>
    public class FrameSyncPointResult
    {
        #region Costruttore

        /// <summary>
        /// Inizializza il checkpoint come non risolto
        /// </summary>
        public FrameSyncPointResult()
        {
            this.BestOffsetMs = int.MinValue;
            this.RejectReason = "";
            this.Backend = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percentuale del video verificata
        /// </summary>
        public int CheckpointPercent { get; set; }

        /// <summary>
        /// Offset atteso in millisecondi
        /// </summary>
        public int ExpectedOffsetMs { get; set; }

        /// <summary>
        /// Offset risolto dal percorso monotono locale
        /// </summary>
        public int BestOffsetMs { get; set; }

        /// <summary>
        /// Confidence SIFT media del percorso monotono locale
        /// </summary>
        public double BestScore { get; set; }

        /// <summary>
        /// Dispersione temporale del percorso locale
        /// </summary>
        public double DispersionMs { get; set; }

        /// <summary>
        /// Numero di coppie elaborate nel corridoio locale
        /// </summary>
        public long ProcessedPairCount { get; set; }

        /// <summary>
        /// Numero di coppie accettate geometricamente
        /// </summary>
        public int AcceptedPairCount { get; set; }

        /// <summary>
        /// Numero di coppie forti nel percorso monotono
        /// </summary>
        public int StrongPairCount { get; set; }

        /// <summary>
        /// Copertura temporale source del percorso locale
        /// </summary>
        public double SourceCoverageMs { get; set; }

        /// <summary>
        /// Copertura temporale language del percorso locale
        /// </summary>
        public double LanguageCoverageMs { get; set; }

        /// <summary>
        /// Indica che il checkpoint conferma l'offset costante iniziale
        /// </summary>
        public bool Accepted { get; set; }

        /// <summary>
        /// Motivo localizzato del rifiuto
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Backend SIFT che ha prodotto il risultato
        /// </summary>
        public string Backend { get; set; }

        /// <summary>
        /// Tempo totale del checkpoint
        /// </summary>
        public long TimingMs { get; set; }

        /// <summary>
        /// Tempo di estrazione delle ancore
        /// </summary>
        public long ExtractMs { get; set; }

        /// <summary>
        /// Tempo di matching e risoluzione temporale
        /// </summary>
        public long MatchMs { get; set; }

        #endregion
    }
}
