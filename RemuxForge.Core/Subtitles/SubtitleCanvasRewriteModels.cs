using System;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Piano di riscrittura canvas/coordinate per sottotitoli PGS
    /// </summary>
    internal class PgsSubtitleCanvasRewritePlan
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="transform">Trasformazione geometrica</param>
        public PgsSubtitleCanvasRewritePlan(SubtitleCanvasTransform transform)
        {
            this.Transform = transform ?? throw new ArgumentNullException(nameof(transform));
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Trasformazione geometrica del piano
        /// </summary>
        public SubtitleCanvasTransform Transform { get; private set; }

        /// <summary>
        /// True se il piano richiede decoding/scaling/encoding delle bitmap ODS
        /// </summary>
        public bool RequiresBitmapScaling
        {
            get { return this.Transform.RequiresBitmapScaling; }
        }

        #endregion
    }

    /// <summary>
    /// Report sintetico della riscrittura PGS
    /// </summary>
    internal class PgsSubtitleCanvasRewriteReport
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public PgsSubtitleCanvasRewriteReport()
        {
            this.ErrorMessage = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Messaggio errore per fallback
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Display-set letti
        /// </summary>
        public int DisplaySets { get; set; }

        /// <summary>
        /// Segmenti PCS riscritti
        /// </summary>
        public int PcsSegments { get; set; }

        /// <summary>
        /// Segmenti WDS riscritti
        /// </summary>
        public int WdsSegments { get; set; }

        /// <summary>
        /// Coordinate oggetto riscritte nei PCS
        /// </summary>
        public int ObjectCoordinatesRewritten { get; set; }

        /// <summary>
        /// Campi crop oggetto PCS riscritti
        /// </summary>
        public int ObjectCropFieldsRewritten { get; set; }

        /// <summary>
        /// Window definition riscritte
        /// </summary>
        public int WindowDefinitionsRewritten { get; set; }

        /// <summary>
        /// Bitmap oggetto decodificate
        /// </summary>
        public int ObjectBitmapsDecoded { get; set; }

        /// <summary>
        /// Bitmap oggetto scalate
        /// </summary>
        public int ObjectBitmapsScaled { get; set; }

        /// <summary>
        /// Bitmap oggetto ricodificate
        /// </summary>
        public int ObjectBitmapsEncoded { get; set; }

        /// <summary>
        /// Segmenti ODS riscritti
        /// </summary>
        public int OdsSegmentsRewritten { get; set; }

        /// <summary>
        /// Segmenti ODS prodotti in frammenti multipli
        /// </summary>
        public int OdsSegmentsFragmented { get; set; }

        /// <summary>
        /// Warning decoder RLE
        /// </summary>
        public int DecodeWarnings { get; set; }

        /// <summary>
        /// Warning scaling bitmap
        /// </summary>
        public int ScaleWarnings { get; set; }

        /// <summary>
        /// Display-set corretti con clamp locale nel canvas finale
        /// </summary>
        public int DisplaySetsClamped { get; set; }

        /// <summary>
        /// Clamp massimo verso sinistra applicato a un display-set
        /// </summary>
        public int MaxClampLeftPx { get; set; }

        /// <summary>
        /// Clamp massimo verso destra applicato a un display-set
        /// </summary>
        public int MaxClampRightPx { get; set; }

        /// <summary>
        /// Clamp massimo verso l'alto applicato a un display-set
        /// </summary>
        public int MaxClampUpPx { get; set; }

        /// <summary>
        /// Clamp massimo verso il basso applicato a un display-set
        /// </summary>
        public int MaxClampDownPx { get; set; }

        #endregion
    }
}
