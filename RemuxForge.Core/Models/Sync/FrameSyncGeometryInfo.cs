namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Informazioni geometria video usate dalla diagnostica frame-sync
    /// </summary>
    public class FrameSyncGeometryInfo
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public FrameSyncGeometryInfo()
        {
            this.FilePath = "";
            this.SarNum = 1;
            this.SarDen = 1;
            this.ManualAnalysisCropPx = "";
            this.CropMode = "none";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Path file analizzato
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Larghezza codificata
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// Altezza codificata
        /// </summary>
        public int Height { get; set; }

        /// <summary>
        /// Numeratore sample aspect ratio
        /// </summary>
        public int SarNum { get; set; }

        /// <summary>
        /// Denominatore sample aspect ratio
        /// </summary>
        public int SarDen { get; set; }

        /// <summary>
        /// Numeratore display aspect ratio
        /// </summary>
        public int DarNum { get; set; }

        /// <summary>
        /// Denominatore display aspect ratio
        /// </summary>
        public int DarDen { get; set; }

        /// <summary>
        /// Larghezza display calcolata
        /// </summary>
        public int DisplayWidth { get; set; }

        /// <summary>
        /// Altezza display calcolata
        /// </summary>
        public int DisplayHeight { get; set; }

        /// <summary>
        /// Aspect ratio display
        /// </summary>
        public double DisplayAspect { get; set; }

        /// <summary>
        /// True se è stato rilevato autocrop bordi neri
        /// </summary>
        public bool HasBlackBorderCrop { get; set; }

        /// <summary>
        /// Crop sinistro rilevato
        /// </summary>
        public int CropLeft { get; set; }

        /// <summary>
        /// Crop destro rilevato
        /// </summary>
        public int CropRight { get; set; }

        /// <summary>
        /// Crop superiore rilevato
        /// </summary>
        public int CropTop { get; set; }

        /// <summary>
        /// Crop inferiore rilevato
        /// </summary>
        public int CropBottom { get; set; }

        /// <summary>
        /// True se la geometria suggerisce normalizzazione 4:3
        /// </summary>
        public bool GeometryCropToFourThree { get; set; }

        /// <summary>
        /// Crop manuale di analisi applicato nel formato L:R:T:B
        /// </summary>
        public string ManualAnalysisCropPx { get; set; }

        /// <summary>
        /// Descrizione modalità crop applicata
        /// </summary>
        public string CropMode { get; set; }

        #endregion
    }
}
