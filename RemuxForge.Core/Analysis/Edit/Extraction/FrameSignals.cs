namespace RemuxForge.Core.Analysis.Edit.Extraction
{
    /// <summary>
    /// Geometria di normalizzazione applicata prima di ridurre il fotogramma al quadrato di analisi
    /// </summary>
    internal class FrameGeometry
    {
        #region Costruttore

        /// <summary>
        /// Costruttore con la geometria neutra
        /// </summary>
        public FrameGeometry()
        {
            this.CropPx = "";
            this.Zoom = 1.0;
            this.ViewportRight = 1.0;
            this.ViewportBottom = 1.0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Crop in pixel nel formato accettato da Options.TryParseAnalysisCropPx
        /// </summary>
        public string CropPx { get; set; }

        /// <summary>
        /// True quando la normalizzazione deve partire dal quadrato centrale
        /// </summary>
        public bool UseCentralSquare { get; set; }

        /// <summary>
        /// Frazione del quadrato centrale conservata dal descrittore dHash
        /// </summary>
        public double Zoom { get; set; }

        /// <summary>
        /// Traslazione verticale espressa come frazione del lato del quadrato
        /// </summary>
        public double VerticalShift { get; set; }

        /// <summary>
        /// True quando il crop successivo è espresso nello spazio normalizzato dell'area attiva
        /// </summary>
        public bool UseNormalizedActiveViewport { get; set; }

        /// <summary>
        /// Estremo sinistro normalizzato del viewport nell'area attiva
        /// </summary>
        public double ViewportLeft { get; set; }

        /// <summary>
        /// Estremo superiore normalizzato del viewport nell'area attiva
        /// </summary>
        public double ViewportTop { get; set; }

        /// <summary>
        /// Estremo destro normalizzato del viewport nell'area attiva
        /// </summary>
        public double ViewportRight { get; set; }

        /// <summary>
        /// Estremo inferiore normalizzato del viewport nell'area attiva
        /// </summary>
        public double ViewportBottom { get; set; }

        #endregion
    }

    /// <summary>
    /// Segnali per fotogramma estratti in una sola passata di decodifica
    /// </summary>
    internal class FrameSignals
    {
        #region Costanti

        /// <summary>
        /// Lato del quadrato grigio di analisi: 72 = 8*9 = 9*8 = 12*6
        /// </summary>
        public const int SIDE = 72;

        /// <summary>
        /// Lato della miniatura usata per informatività e stacchi
        /// </summary>
        public const int THUMB_SIDE = 12;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore che prende possesso degli array già dimensionati sul numero di fotogrammi
        /// </summary>
        /// <param name="ptsMs">PTS di contenitore in millisecondi</param>
        /// <param name="hash0">dHash orizzontale a 64 bit</param>
        /// <param name="hash1">dHash verticale a 64 bit</param>
        /// <param name="lumaMean">Media di luminanza del quadrato di analisi</param>
        /// <param name="thumbStd">Deviazione standard della miniatura 12x12</param>
        /// <param name="thumbPixels">Miniature 12x12 concatenate</param>
        public FrameSignals(double[] ptsMs, ulong[] hash0, ulong[] hash1, float[] lumaMean, float[] thumbStd, byte[] thumbPixels)
        {
            this.PtsMs = ptsMs;
            this.Hash0 = hash0;
            this.Hash1 = hash1;
            this.LumaMean = lumaMean;
            this.ThumbStd = thumbStd;
            this.ThumbPixels = thumbPixels;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// PTS di contenitore in millisecondi, autorità unica del dominio temporale
        /// </summary>
        public double[] PtsMs { get; private set; }

        /// <summary>
        /// dHash orizzontale: gradienti fra colonne adiacenti di una griglia 8x9
        /// </summary>
        public ulong[] Hash0 { get; private set; }

        /// <summary>
        /// dHash verticale: gradienti fra righe adiacenti di una griglia 9x8
        /// </summary>
        public ulong[] Hash1 { get; private set; }

        /// <summary>
        /// Media di luminanza per fotogramma, sotto 2.0 il fotogramma è nero pieno
        /// </summary>
        public float[] LumaMean { get; private set; }

        /// <summary>
        /// Deviazione standard della miniatura, misura di quanta informazione porta il fotogramma
        /// </summary>
        public float[] ThumbStd { get; private set; }

        /// <summary>
        /// Miniature 12x12 concatenate, 144 byte per fotogramma
        /// </summary>
        public byte[] ThumbPixels { get; private set; }

        /// <summary>
        /// Numero di fotogrammi indicizzati
        /// </summary>
        public int Count
        {
            get { return this.PtsMs.Length; }
        }

        #endregion
    }
}
