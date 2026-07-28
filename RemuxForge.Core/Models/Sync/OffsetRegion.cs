namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Regione con offset costante tra source e lang
    /// </summary>
    public class OffsetRegion
    {
        /// <summary>
        /// Inizio regione nel source in secondi
        /// </summary>
        public double StartSrcSec { get; set; }

        /// <summary>
        /// Fine regione nel source in secondi
        /// </summary>
        public double EndSrcSec { get; set; }

        /// <summary>
        /// Inizio del plateau che supporta realmente questa regione
        /// </summary>
        public double SupportStartSrcSec { get; set; }

        /// <summary>
        /// Fine del plateau che supporta realmente questa regione
        /// </summary>
        public double SupportEndSrcSec { get; set; }

        /// <summary>
        /// Centro del primo anchor affidabile del plateau
        /// </summary>
        public double FirstAnchorSrcSec { get; set; }

        /// <summary>
        /// Centro dell'ultimo anchor affidabile del plateau
        /// </summary>
        public double LastAnchorSrcSec { get; set; }

        /// <summary>
        /// Offset in millisecondi
        /// </summary>
        public double OffsetMs { get; set; }

        /// <summary>
        /// Numero di scene cuts matchati in questa regione
        /// </summary>
        public int MatchCount { get; set; }
    }
}
