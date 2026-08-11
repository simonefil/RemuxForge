using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Sequenza ordinata di ancore SIFT e landmark temporali di una timeline
    /// </summary>
    public class DeepSiftAnchorTimeline
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepSiftAnchorTimeline()
        {
            this.Anchors = new List<DeepSiftVisualAnchor>();
            this.BlackRuns = new List<DeepBlackTimelineRun>();
            this.TimestampBackend = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Ancore in ordine PTS
        /// </summary>
        public List<DeepSiftVisualAnchor> Anchors { get; set; }

        /// <summary>
        /// Black-run rilevate nella stessa scansione
        /// </summary>
        public List<DeepBlackTimelineRun> BlackRuns { get; set; }

        /// <summary>
        /// Backend usato per i timestamp
        /// </summary>
        public string TimestampBackend { get; set; }

        /// <summary>
        /// Numero di frame decodificati prima della selezione delle ancore
        /// </summary>
        public int DecodedFrameCount { get; set; }

        /// <summary>
        /// Numero di frame selezionati come ancore
        /// </summary>
        public int SelectedFrameCount { get; set; }

        /// <summary>
        /// Tempo FFmpeg di decode e preprocess in millisecondi
        /// </summary>
        public long DecodePreprocessMs { get; set; }

        /// <summary>
        /// Tempo di associazione e normalizzazione PTS in millisecondi
        /// </summary>
        public long TimestampIndexMs { get; set; }

        #endregion
    }
}
