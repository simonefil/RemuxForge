using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Risultato operativo della mappa timeline-first DeepAnalysis
    /// </summary>
    public class DeepTimelineMapResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepTimelineMapResult()
        {
            this.Success = false;
            this.RejectReason = "";
            this.Regions = new List<OffsetRegion>();
            this.Diagnostic = new DeepAnalysisTimelineMapDiagnostic();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// True se la timeline è stata accettata
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Motivo di rifiuto, se presente
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Regioni offset prodotte dalla timeline
        /// </summary>
        public List<OffsetRegion> Regions { get; set; }

        /// <summary>
        /// Diagnostica completa della mappa timeline
        /// </summary>
        public DeepAnalysisTimelineMapDiagnostic Diagnostic { get; set; }

        #endregion
    }
}
