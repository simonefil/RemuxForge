using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Risultato completo della sincronizzazione frame-sync
    /// </summary>
    public class FrameSyncResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public FrameSyncResult()
        {
            this.OffsetMs = int.MinValue;
            this.InitialToFinalDeltaMs = int.MinValue;
            this.FailureReason = "";
            this.Timing = new FrameSyncTimingInfo();
            this.Initial = new FrameSyncInitialResult();
            this.Points = new List<FrameSyncPointResult>();
            this.PrecisionCheckpointPercent = -1;
            this.GeometryAlignment = new VisualGeometryAlignment();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True se il frame-sync ha prodotto un offset applicabile
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// True se il risultato è ambiguo e non deve essere applicato
        /// </summary>
        public bool Ambiguous { get; set; }

        /// <summary>
        /// Offset finale da applicare in millisecondi
        /// </summary>
        public int OffsetMs { get; set; }

        /// <summary>
        /// Confidence finale normalizzata 0..1
        /// </summary>
        public double Confidence { get; set; }

        /// <summary>
        /// Distanza assoluta tra delay iniziale e cluster finale checkpoint
        /// </summary>
        public int InitialToFinalDeltaMs { get; set; }

        /// <summary>
        /// Motivo del fallimento o dell'ambiguità
        /// </summary>
        public string FailureReason { get; set; }

        /// <summary>
        /// Geometria rilevata per il file sorgente
        /// </summary>
        public FrameSyncGeometryInfo SourceGeometry { get; set; }

        /// <summary>
        /// Geometria rilevata per il file lingua
        /// </summary>
        public FrameSyncGeometryInfo LanguageGeometry { get; set; }

        /// <summary>
        /// Trasformazione globale dall'area attiva language all'area attiva source
        /// </summary>
        public VisualGeometryAlignment GeometryAlignment { get; set; }

        /// <summary>
        /// Timing diagnostici delle fasi frame-sync
        /// </summary>
        public FrameSyncTimingInfo Timing { get; set; }

        /// <summary>
        /// Risultato della ricerca iniziale
        /// </summary>
        public FrameSyncInitialResult Initial { get; set; }

        /// <summary>
        /// Risultati dei checkpoint di verifica
        /// </summary>
        public List<FrameSyncPointResult> Points { get; set; }

        /// <summary>
        /// Candidato full-rate che determina l'offset finale al frame
        /// </summary>
        public FrameSyncCandidate PrecisionCandidate { get; set; }

        /// <summary>
        /// Percentuale del checkpoint usato per il refinement full-rate
        /// </summary>
        public int PrecisionCheckpointPercent { get; set; }

        #endregion
    }
}
