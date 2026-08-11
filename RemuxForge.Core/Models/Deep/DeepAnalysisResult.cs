namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Risultato diagnostico completo della pipeline DeepAnalysis
    /// </summary>
    public class DeepAnalysisResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepAnalysisResult()
        {
            this.BackendName = "";
            this.Status = "NotStarted";
            this.RejectReason = "";
            this.StretchFactor = "";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Backend visuale usato per descriptor e matching
        /// </summary>
        public string BackendName { get; set; }

        /// <summary>
        /// Stato stabile Accepted o Rejected
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Motivo del rifiuto fail-closed
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Stretch logico normalizzato, vuoto per identità
        /// </summary>
        public string StretchFactor { get; set; }

        /// <summary>
        /// Scala applicata dalla timeline source alla timeline language
        /// </summary>
        public double SourceToLanguageScale { get; set; }

        /// <summary>
        /// Timeline source globale usata per l'allineamento
        /// </summary>
        public DeepSiftAnchorTimeline SourceTimeline { get; set; }

        /// <summary>
        /// Timeline language globale usata per l'allineamento
        /// </summary>
        public DeepSiftAnchorTimeline LanguageTimeline { get; set; }

        /// <summary>
        /// Geometria source usata per crop e riscrittura canvas sottotitoli
        /// </summary>
        public FrameSyncGeometryInfo SourceGeometry { get; set; }

        /// <summary>
        /// Geometria language usata per crop e riscrittura canvas sottotitoli
        /// </summary>
        public FrameSyncGeometryInfo LanguageGeometry { get; set; }

        /// <summary>
        /// Risultati SIFT prodotti per le sole coppie pianificate dalla pipeline temporale
        /// </summary>
        public DeepSiftBatchMatchResult BatchMatching { get; set; }

        /// <summary>
        /// Percorso monotono globale e margine della seconda alternativa
        /// </summary>
        public DeepSiftTemporalEvidenceResult Alignment { get; set; }

        /// <summary>
        /// Risultato fail-closed della localizzazione dei boundary
        /// </summary>
        public DeepSiftEditMapResult EditMapResult { get; set; }

        /// <summary>
        /// Tempo complessivo della pipeline in millisecondi
        /// </summary>
        public long TotalElapsedMs { get; set; }

        #endregion
    }
}
