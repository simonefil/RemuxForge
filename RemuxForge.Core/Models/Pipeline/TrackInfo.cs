namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Informazioni di una singola traccia (audio, video o sottotitoli) di un container MKV
    /// </summary>
    public class TrackInfo
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TrackInfo()
        {
            this.Type = "";
            this.Codec = "";
            this.Language = "";
            this.LanguageIetf = "";
            this.Name = "";
            this.DefaultTrack = false;
            this.ForcedTrack = false;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Identificatore della traccia all'interno del container MKV
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Tipo della traccia: "audio", "video" o "subtitles"
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// Codec utilizzato per la traccia, come riportato da mkvmerge
        /// </summary>
        public string Codec { get; set; }

        /// <summary>
        /// Codice lingua ISO 639-2 della traccia
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// Tag lingua IETF/BCP 47 della traccia, se disponibile
        /// </summary>
        public string LanguageIetf { get; set; }

        /// <summary>
        /// Nome visualizzato della traccia, se impostato
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Indica se la traccia è marcata come default nel container
        /// </summary>
        public bool DefaultTrack { get; set; }

        /// <summary>
        /// Indica se la traccia è marcata come forced nel container
        /// </summary>
        public bool ForcedTrack { get; set; }

        /// <summary>
        /// Durata predefinita in nanosecondi per frame della traccia video
        /// </summary>
        public long DefaultDurationNs { get; set; }

        /// <summary>
        /// Numero frame video riportato dai tag statistici, se disponibile
        /// </summary>
        public long VideoFrameCount { get; set; }

        /// <summary>
        /// Durata traccia in nanosecondi riportata dai tag statistici, se disponibile
        /// </summary>
        public long TrackDurationNs { get; set; }

        /// <summary>
        /// Primo timestamp della traccia nel container in nanosecondi
        /// </summary>
        public long MinimumTimestampNs { get; set; }

        /// <summary>
        /// Numero di canali audio della traccia
        /// </summary>
        public int Channels { get; set; }

        /// <summary>
        /// Bit per campione audio (es. 16, 24, 32)
        /// </summary>
        public int BitsPerSample { get; set; }

        /// <summary>
        /// Frequenza di campionamento audio in Hz (es. 44100, 48000, 96000)
        /// </summary>
        public int SamplingFrequency { get; set; }

        /// <summary>
        /// Bitrate audio in bit/s, se disponibile
        /// </summary>
        public int Bitrate { get; set; }

        #endregion
    }
}
