namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Timing video normalizzato da MediaInfo e metadati container
    /// </summary>
    public class VideoTimingInfo
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public VideoTimingInfo()
        {
            this.FrameRateMode = "";
            this.Reason = "";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Modalità frame rate dichiarata da MediaInfo
        /// </summary>
        public string FrameRateMode { get; set; }

        /// <summary>
        /// FPS nominale dichiarato
        /// </summary>
        public double NominalFps { get; set; }

        /// <summary>
        /// FPS osservato da frame count e durata
        /// </summary>
        public double ObservedFps { get; set; }

        /// <summary>
        /// FPS derivato da default_duration Matroska
        /// </summary>
        public double DefaultDurationFps { get; set; }

        /// <summary>
        /// Numero frame video
        /// </summary>
        public long FrameCount { get; set; }

        /// <summary>
        /// Durata video in millisecondi
        /// </summary>
        public double DurationMs { get; set; }

        /// <summary>
        /// True se MediaInfo è stato interrogato correttamente
        /// </summary>
        public bool IsMediaInfoAvailable { get; set; }

        /// <summary>
        /// True se il video è classificato VFR
        /// </summary>
        public bool IsVariableFrameRate { get; set; }

        /// <summary>
        /// True se default_duration è coerente con durata e frame count
        /// </summary>
        public bool IsDefaultDurationTrusted { get; set; }

        /// <summary>
        /// True se la speed correction automatica può essere applicata
        /// </summary>
        public bool CanAutoSpeedCorrect { get; set; }

        /// <summary>
        /// True se è possibile normalizzare al FPS nominale
        /// </summary>
        public bool CanNormalizeToNominalFps { get; set; }

        /// <summary>
        /// Motivazione della classificazione timing
        /// </summary>
        public string Reason { get; set; }

        #endregion
    }
}
