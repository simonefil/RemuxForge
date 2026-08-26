using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Trasformazione globale dall'area attiva language all'area attiva source
    /// </summary>
    public class VisualGeometryAlignment
    {
        #region Costruttore

        /// <summary>
        /// Inizializza una trasformazione neutra non ancora verificata
        /// </summary>
        public VisualGeometryAlignment()
        {
            this.BackendName = "";
            this.RejectReason = "";
            this.ScaleX = 1.0;
            this.ScaleY = 1.0;
            this.SourceCommonCropPx = "";
            this.LanguageCommonCropPx = "";
            this.AcceptedMatches = new List<VisualGeometryMatch>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True quando il bootstrap ha raggiunto il quorum geometrico
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Backend SIFT che ha prodotto le corrispondenze
        /// </summary>
        public string BackendName { get; set; }

        /// <summary>
        /// Motivo del rifiuto, vuoto quando la geometria è valida
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Scala residua orizzontale in coordinate normalizzate dell'area attiva
        /// </summary>
        public double ScaleX { get; set; }

        /// <summary>
        /// Scala residua verticale in coordinate normalizzate dell'area attiva
        /// </summary>
        public double ScaleY { get; set; }

        /// <summary>
        /// Traslazione orizzontale in frazioni della larghezza attiva source
        /// </summary>
        public double TranslateX { get; set; }

        /// <summary>
        /// Traslazione verticale in frazioni dell'altezza attiva source
        /// </summary>
        public double TranslateY { get; set; }

        /// <summary>
        /// Dispersione robusta della scala orizzontale
        /// </summary>
        public double ScaleXDispersion { get; set; }

        /// <summary>
        /// Dispersione robusta della scala verticale
        /// </summary>
        public double ScaleYDispersion { get; set; }

        /// <summary>
        /// Dispersione robusta della traslazione orizzontale normalizzata
        /// </summary>
        public double TranslateXDispersion { get; set; }

        /// <summary>
        /// Dispersione robusta della traslazione verticale normalizzata
        /// </summary>
        public double TranslateYDispersion { get; set; }

        /// <summary>
        /// Qualità visuale mediana dopo l'affinamento sui pixel
        /// </summary>
        public double PixelScore { get; set; }

        /// <summary>
        /// Numero di match indipendenti conservati dal consenso
        /// </summary>
        public int AcceptedMatchCount { get; set; }

        /// <summary>
        /// Numero minimo di match richiesto
        /// </summary>
        public int RequiredMatchCount { get; set; }

        /// <summary>
        /// Match indipendenti conservati dal consenso geometrico
        /// </summary>
        public List<VisualGeometryMatch> AcceptedMatches { get; set; }

        /// <summary>
        /// Crop comune source L:R:T:B usato esclusivamente dall'indicizzazione visiva
        /// </summary>
        public string SourceCommonCropPx { get; set; }

        /// <summary>
        /// Crop comune language L:R:T:B usato esclusivamente dall'indicizzazione visiva
        /// </summary>
        public string LanguageCommonCropPx { get; set; }

        /// <summary>
        /// Coppie SIFT elaborate dal backend durante il bootstrap
        /// </summary>
        public long ProcessedPairCount { get; set; }

        /// <summary>
        /// Millisecondi spesi nell'upload Vulkan, zero sul percorso CPU
        /// </summary>
        public long UploadMs { get; set; }

        /// <summary>
        /// Millisecondi spesi nel readback Vulkan, zero sul percorso CPU
        /// </summary>
        public long ReadbackMs { get; set; }

        /// <summary>
        /// Coppie SIFT usate per verificare il contratto dHash
        /// </summary>
        public int DHashContractPairCount { get; set; }

        /// <summary>
        /// Coppie spiegate dai viewport dHash indipendenti
        /// </summary>
        public int IndependentDHashExplainedCount { get; set; }

        /// <summary>
        /// Coppie spiegate portando il language nel viewport source con l'affine globale
        /// </summary>
        public int AffineDHashExplainedCount { get; set; }

        /// <summary>
        /// True quando soltanto il viewport affine rende spiegabile la maggioranza del quorum
        /// </summary>
        public bool UseAffineDHashViewport { get; set; }

        #endregion
    }

    /// <summary>
    /// Evidenza temporale e geometrica conservata dal bootstrap
    /// </summary>
    public class VisualGeometryMatch
    {
        /// <summary>
        /// PTS source della coppia
        /// </summary>
        public double SourcePtsMs { get; set; }

        /// <summary>
        /// PTS language della coppia
        /// </summary>
        public double LanguagePtsMs { get; set; }

        /// <summary>
        /// Confidenza del matcher
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// Scala X locale language-source
        /// </summary>
        public double ScaleX { get; set; }

        /// <summary>
        /// Scala Y locale language-source
        /// </summary>
        public double ScaleY { get; set; }

        /// <summary>
        /// Traslazione X locale normalizzata
        /// </summary>
        public double TranslateX { get; set; }

        /// <summary>
        /// Traslazione Y locale normalizzata
        /// </summary>
        public double TranslateY { get; set; }
    }
}
