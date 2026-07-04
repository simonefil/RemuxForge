using RemuxForge.Core.Models;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Contesto comune della riscrittura canvas sottotitoli
    /// </summary>
    internal class SubtitleCanvasRewriteContext
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public SubtitleCanvasRewriteContext()
        {
            this.SourceCropMode = "";
            this.LanguageCropMode = "";
            this.FfmpegPath = "";
            this.TempFolder = "";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Record elaborazione corrente
        /// </summary>
        public FileProcessingRecord Record { get; set; }

        /// <summary>
        /// Opzioni correnti
        /// </summary>
        public Options Options { get; set; }

        /// <summary>
        /// Trasformazione geometrica base video
        /// </summary>
        public SubtitleCanvasTransform Transform { get; set; }

        /// <summary>
        /// Modalità crop rilevata per il sorgente
        /// </summary>
        public string SourceCropMode { get; set; }

        /// <summary>
        /// Modalità crop rilevata per il file lingua
        /// </summary>
        public string LanguageCropMode { get; set; }

        /// <summary>
        /// Percorso ffmpeg
        /// </summary>
        public string FfmpegPath { get; set; }

        /// <summary>
        /// Cartella temporanea
        /// </summary>
        public string TempFolder { get; set; }

        #endregion
    }
}
