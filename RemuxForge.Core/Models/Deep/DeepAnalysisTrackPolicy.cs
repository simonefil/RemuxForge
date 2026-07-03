namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Policy delle tracce che DeepAnalysis puo' usare per validazione audio
    /// </summary>
    public class DeepAnalysisTrackPolicy
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepAnalysisTrackPolicy()
        {
            this.AudioValidationAvailable = false;
            this.TrackLanguage = "";
            this.SourceTrackName = "";
            this.LanguageTrackName = "";
            this.RejectReason = "";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// True se esiste una coppia audio comune consentita dall'output
        /// </summary>
        public bool AudioValidationAvailable { get; set; }

        /// <summary>
        /// Lingua della coppia audio comune
        /// </summary>
        public string TrackLanguage { get; set; }

        /// <summary>
        /// Indice ffmpeg della traccia audio source
        /// </summary>
        public int SourceAudioStreamIndex { get; set; }

        /// <summary>
        /// Indice ffmpeg della traccia audio language
        /// </summary>
        public int LanguageAudioStreamIndex { get; set; }

        /// <summary>
        /// ID MKV della traccia audio source
        /// </summary>
        public int SourceTrackId { get; set; }

        /// <summary>
        /// ID MKV della traccia audio language
        /// </summary>
        public int LanguageTrackId { get; set; }

        /// <summary>
        /// Nome traccia audio source
        /// </summary>
        public string SourceTrackName { get; set; }

        /// <summary>
        /// Nome traccia audio language
        /// </summary>
        public string LanguageTrackName { get; set; }

        /// <summary>
        /// Motivo per cui la validazione audio non e' disponibile
        /// </summary>
        public string RejectReason { get; set; }

        #endregion
    }
}
