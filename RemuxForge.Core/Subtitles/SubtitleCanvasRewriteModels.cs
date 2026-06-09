namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Piano di riscrittura canvas/coordinate per sottotitoli PGS
    /// </summary>
    internal class PgsCanvasRewritePlan
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public PgsCanvasRewritePlan()
        {
            this.SourceCropMode = "";
            this.LanguageCropMode = "";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Larghezza canvas originale del sottotitolo importato
        /// </summary>
        public int InputCanvasWidth { get; set; }

        /// <summary>
        /// Altezza canvas originale del sottotitolo importato
        /// </summary>
        public int InputCanvasHeight { get; set; }

        /// <summary>
        /// Larghezza canvas finale attesa dal video sorgente/output
        /// </summary>
        public int OutputCanvasWidth { get; set; }

        /// <summary>
        /// Altezza canvas finale attesa dal video sorgente/output
        /// </summary>
        public int OutputCanvasHeight { get; set; }

        /// <summary>
        /// Crop sinistro applicato al video lingua
        /// </summary>
        public int InputCropLeft { get; set; }

        /// <summary>
        /// Crop destro applicato al video lingua
        /// </summary>
        public int InputCropRight { get; set; }

        /// <summary>
        /// Crop superiore applicato al video lingua
        /// </summary>
        public int InputCropTop { get; set; }

        /// <summary>
        /// Crop inferiore applicato al video lingua
        /// </summary>
        public int InputCropBottom { get; set; }

        /// <summary>
        /// Crop sinistro applicato al video sorgente
        /// </summary>
        public int OutputCropLeft { get; set; }

        /// <summary>
        /// Crop destro applicato al video sorgente
        /// </summary>
        public int OutputCropRight { get; set; }

        /// <summary>
        /// Crop superiore applicato al video sorgente
        /// </summary>
        public int OutputCropTop { get; set; }

        /// <summary>
        /// Crop inferiore applicato al video sorgente
        /// </summary>
        public int OutputCropBottom { get; set; }

        /// <summary>
        /// Modalita' crop rilevata per il sorgente
        /// </summary>
        public string SourceCropMode { get; set; }

        /// <summary>
        /// Modalita' crop rilevata per il file lingua
        /// </summary>
        public string LanguageCropMode { get; set; }

        /// <summary>
        /// Offset X da applicare alle coordinate PGS
        /// </summary>
        public int OffsetX
        {
            get { return this.OutputCropLeft - this.InputCropLeft; }
        }

        /// <summary>
        /// Offset Y da applicare alle coordinate PGS
        /// </summary>
        public int OffsetY
        {
            get { return this.OutputCropTop - this.InputCropTop; }
        }

        /// <summary>
        /// Larghezza area attiva lingua dopo crop
        /// </summary>
        public int InputActiveWidth
        {
            get { return this.InputCanvasWidth - this.InputCropLeft - this.InputCropRight; }
        }

        /// <summary>
        /// Altezza area attiva lingua dopo crop
        /// </summary>
        public int InputActiveHeight
        {
            get { return this.InputCanvasHeight - this.InputCropTop - this.InputCropBottom; }
        }

        /// <summary>
        /// Larghezza area attiva source dopo crop
        /// </summary>
        public int OutputActiveWidth
        {
            get { return this.OutputCanvasWidth - this.OutputCropLeft - this.OutputCropRight; }
        }

        /// <summary>
        /// Altezza area attiva source dopo crop
        /// </summary>
        public int OutputActiveHeight
        {
            get { return this.OutputCanvasHeight - this.OutputCropTop - this.OutputCropBottom; }
        }

        #endregion
    }

    /// <summary>
    /// Report sintetico della riscrittura PGS
    /// </summary>
    internal class PgsCanvasRewriteReport
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public PgsCanvasRewriteReport()
        {
            this.ErrorMessage = "";
        }

        #endregion

        #region Proprieta

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
        /// Window definition riscritte
        /// </summary>
        public int WindowDefinitionsRewritten { get; set; }

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

        /// <summary>
        /// Messaggio errore per fallback
        /// </summary>
        public string ErrorMessage { get; set; }

        #endregion
    }
}
