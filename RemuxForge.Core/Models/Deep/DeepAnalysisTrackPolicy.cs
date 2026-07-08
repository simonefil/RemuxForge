namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Policy delle tracce che DeepAnalysis può usare per validazione audio
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
            this.SourceFineTuneAudioAvailable = false;
            this.LanguageFineTuneAudioAvailable = false;
            this.TrackLanguage = "";
            this.SourceTrackName = "";
            this.LanguageTrackName = "";
            this.RejectReason = "";
            this.SourceFineTuneRejectReason = "";
            this.LanguageFineTuneRejectReason = "";
            this.SourceFineTuneTrackName = "";
            this.LanguageFineTuneTrackName = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True se esiste una coppia audio comune consentita dall'output
        /// </summary>
        public bool AudioValidationAvailable { get; set; }

        /// <summary>
        /// True se esiste una traccia audio source usabile come reference per fine tuning audio
        /// </summary>
        public bool SourceFineTuneAudioAvailable { get; set; }

        /// <summary>
        /// True se esiste una traccia audio language finale usabile per fine tuning audio
        /// </summary>
        public bool LanguageFineTuneAudioAvailable { get; set; }

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
        /// Indice ffmpeg della traccia audio source da usare per fine tuning
        /// </summary>
        public int SourceFineTuneAudioStreamIndex { get; set; }

        /// <summary>
        /// Indice ffmpeg della traccia audio language da usare per fine tuning
        /// </summary>
        public int LanguageFineTuneAudioStreamIndex { get; set; }

        /// <summary>
        /// ID MKV della traccia audio source
        /// </summary>
        public int SourceTrackId { get; set; }

        /// <summary>
        /// ID MKV della traccia audio language
        /// </summary>
        public int LanguageTrackId { get; set; }

        /// <summary>
        /// ID MKV della traccia audio source da usare per fine tuning
        /// </summary>
        public int SourceFineTuneTrackId { get; set; }

        /// <summary>
        /// ID MKV della traccia audio language da usare per fine tuning
        /// </summary>
        public int LanguageFineTuneTrackId { get; set; }

        /// <summary>
        /// Nome traccia audio source
        /// </summary>
        public string SourceTrackName { get; set; }

        /// <summary>
        /// Nome traccia audio language
        /// </summary>
        public string LanguageTrackName { get; set; }

        /// <summary>
        /// Nome traccia audio source usata per fine tuning
        /// </summary>
        public string SourceFineTuneTrackName { get; set; }

        /// <summary>
        /// Nome traccia audio language usata per fine tuning
        /// </summary>
        public string LanguageFineTuneTrackName { get; set; }

        /// <summary>
        /// Motivo per cui la validazione audio non è disponibile
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Motivo per cui il fine tuning audio source non è disponibile
        /// </summary>
        public string SourceFineTuneRejectReason { get; set; }

        /// <summary>
        /// Motivo per cui il fine tuning audio language non è disponibile
        /// </summary>
        public string LanguageFineTuneRejectReason { get; set; }

        #endregion
    }
}
