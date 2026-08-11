namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Run nera raffinata sulla timeline PTS originale, indipendente dagli indici frame locali
    /// </summary>
    public class DeepBlackTimelineRun
    {
        /// <summary>
        /// PTS del primo frame nero in millisecondi
        /// </summary>
        public double StartPtsMs { get; set; }

        /// <summary>
        /// PTS esclusivo del primo frame successivo alla run in millisecondi
        /// </summary>
        public double EndPtsMs { get; set; }

        /// <summary>
        /// Durata PTS osservata della run
        /// </summary>
        public double DurationMs { get; set; }

    }
}
