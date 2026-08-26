using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Modo temporale SIFT candidato per l'offset FrameSync
    /// </summary>
    public class FrameSyncCandidate
    {
        #region Costruttore

        /// <summary>
        /// Inizializza il candidato con backend e stato diagnostico vuoti
        /// </summary>
        public FrameSyncCandidate()
        {
            this.Backend = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Offset da applicare alla timeline language in millisecondi
        /// </summary>
        public int OffsetMs { get; set; }

        /// <summary>
        /// Backend SIFT che ha prodotto le evidenze
        /// </summary>
        public string Backend { get; set; }

        /// <summary>
        /// Numero di coppie elaborate dal matcher
        /// </summary>
        public long ProcessedPairCount { get; set; }

        /// <summary>
        /// Numero di coppie accettate geometricamente nel modo
        /// </summary>
        public int AcceptedPairCount { get; set; }

        /// <summary>
        /// Numero di coppie reciprocamente univoche nel percorso monotono
        /// </summary>
        public int StrongPairCount { get; set; }


        /// <summary>
        /// Copertura temporale source del percorso monotono
        /// </summary>
        public double SourceCoverageMs { get; set; }

        /// <summary>
        /// Copertura temporale language del percorso monotono
        /// </summary>
        public double LanguageCoverageMs { get; set; }

        /// <summary>
        /// Confidence SIFT media del percorso monotono
        /// </summary>
        public double MeanScore { get; set; }

        /// <summary>
        /// Deviazione assoluta mediana degli offset del percorso
        /// </summary>
        public double DispersionMs { get; set; }

        #endregion
    }

    /// <summary>
    /// Risultato della ricerca iniziale SIFT FrameSync
    /// </summary>
    public class FrameSyncInitialResult
    {
        #region Costruttore

        /// <summary>
        /// Inizializza il risultato senza candidati
        /// </summary>
        public FrameSyncInitialResult()
        {
            this.Candidates = new List<FrameSyncCandidate>();
            this.FailureReason = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Indica che la ricerca iniziale ha prodotto un modo applicabile
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Miglior candidato iniziale
        /// </summary>
        public FrameSyncCandidate BestCandidate { get; set; }

        /// <summary>
        /// Modi temporali ordinati per supporto monotono
        /// </summary>
        public List<FrameSyncCandidate> Candidates { get; set; }

        /// <summary>
        /// Motivo localizzato del rifiuto
        /// </summary>
        public string FailureReason { get; set; }

        #endregion
    }
}
