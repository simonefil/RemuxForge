namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Profilo di encoding video post-merge con ffmpeg
    /// </summary>
    public class EncodingProfile
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public EncodingProfile()
        {
            this.Name = "";
            this.Codec = "libx265";
            this.Preset = "medium";
            this.Tune = "default";
            this.Profile = "default";
            this.BitDepth = "10-bit: yuv420p10le";
            this.RateMode = "crf";
            this.CrfQp = 28;
            this.Bitrate = 0;
            this.Passes = 1;
            this.FilmGrain = 0;
            this.FilmGrainDenoise = false;
            this.ExtraParams = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Crea una copia del profilo
        /// </summary>
        /// <returns>Nuova istanza con gli stessi valori</returns>
        public EncodingProfile Clone()
        {
            EncodingProfile copy = new EncodingProfile();
            copy.Name = this.Name;
            copy.Codec = this.Codec;
            copy.Preset = this.Preset;
            copy.Tune = this.Tune;
            copy.Profile = this.Profile;
            copy.BitDepth = this.BitDepth;
            copy.RateMode = this.RateMode;
            copy.CrfQp = this.CrfQp;
            copy.Bitrate = this.Bitrate;
            copy.Passes = this.Passes;
            copy.FilmGrain = this.FilmGrain;
            copy.FilmGrainDenoise = this.FilmGrainDenoise;
            copy.ExtraParams = this.ExtraParams;
            return copy;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Nome del profilo
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Codec video: libx264, libx265, libsvtav1
        /// </summary>
        public string Codec { get; set; }

        /// <summary>
        /// Preset encoder
        /// </summary>
        public string Preset { get; set; }

        /// <summary>
        /// Tune encoder
        /// </summary>
        public string Tune { get; set; }

        /// <summary>
        /// Profilo encoder, solo x264/x265
        /// </summary>
        public string Profile { get; set; }

        /// <summary>
        /// Bit depth e pixel format
        /// </summary>
        public string BitDepth { get; set; }

        /// <summary>
        /// Modalità rate control: crf, qp, bitrate
        /// </summary>
        public string RateMode { get; set; }

        /// <summary>
        /// Valore CRF o QP, usato quando RateMode è crf o qp
        /// </summary>
        public int CrfQp { get; set; }

        /// <summary>
        /// Bitrate target in kbps, usato quando RateMode è bitrate
        /// </summary>
        public int Bitrate { get; set; }

        /// <summary>
        /// Numero di passate, 1 o 2, solo per x264/x265 in modalità bitrate
        /// </summary>
        public int Passes { get; set; }

        /// <summary>
        /// Film grain synthesis, solo svtav1, 0 = disabilitato
        /// </summary>
        public int FilmGrain { get; set; }

        /// <summary>
        /// Film grain denoise, solo svtav1
        /// </summary>
        public bool FilmGrainDenoise { get; set; }

        /// <summary>
        /// Parametri aggiuntivi ffmpeg in formato stringa libera
        /// </summary>
        public string ExtraParams { get; set; }

        #endregion
    }
}
