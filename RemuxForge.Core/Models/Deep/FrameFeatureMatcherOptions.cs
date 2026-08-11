using System;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Parametri condivisi dai matcher SIFT CPU e Vulkan
    /// </summary>
    public class FrameFeatureMatcherOptions
    {
        /// <summary>
        /// Costruttore con i valori predefiniti condivisi dai backend
        /// </summary>
        public FrameFeatureMatcherOptions()
        {
            this.MaxFeatures = 2000;
            this.OctaveLayers = 3;
            this.DoubleInput = true;
            this.ContrastThreshold = 0.04;
            this.EdgeThreshold = 10.0;
            this.Sigma = 1.6;
            this.LoweRatio = 0.75;
            this.MinKeypoints = 20;
            this.MinReciprocalMatches = 8;
            this.MinInliers = 6;
            this.MinInlierRatio = 0.40;
            this.MinCoverage = 0.02;
            this.RansacReprojectionThreshold = 3.0;
            this.MaxMeanReprojectionError = 2.5;
            this.MinHomographyAreaRatio = 0.20;
            this.MaxHomographyAreaRatio = 5.0;
        }

        /// <summary>
        /// Numero massimo di feature SIFT per frame
        /// </summary>
        public int MaxFeatures { get; set; }

        /// <summary>
        /// Numero di layer per ottava SIFT
        /// </summary>
        public int OctaveLayers { get; set; }

        /// <summary>
        /// Raddoppia la risoluzione prima della piramide SIFT
        /// </summary>
        public bool DoubleInput { get; set; }

        /// <summary>
        /// Soglia contrasto SIFT
        /// </summary>
        public double ContrastThreshold { get; set; }

        /// <summary>
        /// Soglia edge SIFT
        /// </summary>
        public double EdgeThreshold { get; set; }

        /// <summary>
        /// Sigma iniziale SIFT
        /// </summary>
        public double Sigma { get; set; }

        /// <summary>
        /// Rapporto massimo del Lowe ratio test
        /// </summary>
        public double LoweRatio { get; set; }

        /// <summary>
        /// Numero minimo di keypoint richiesto su ogni frame
        /// </summary>
        public int MinKeypoints { get; set; }

        /// <summary>
        /// Numero minimo di match che superano ratio test e cross-check
        /// </summary>
        public int MinReciprocalMatches { get; set; }

        /// <summary>
        /// Numero minimo di inlier geometrici
        /// </summary>
        public int MinInliers { get; set; }

        /// <summary>
        /// Rapporto minimo fra inlier e match reciproci
        /// </summary>
        public double MinInlierRatio { get; set; }

        /// <summary>
        /// Copertura spaziale minima degli inlier su entrambi i frame
        /// </summary>
        public double MinCoverage { get; set; }

        /// <summary>
        /// Errore massimo RANSAC in pixel
        /// </summary>
        public double RansacReprojectionThreshold { get; set; }

        /// <summary>
        /// Errore medio massimo degli inlier riproiettati
        /// </summary>
        public double MaxMeanReprojectionError { get; set; }

        /// <summary>
        /// Rapporto minimo fra area proiettata e area source
        /// </summary>
        public double MinHomographyAreaRatio { get; set; }

        /// <summary>
        /// Rapporto massimo fra area proiettata e area source
        /// </summary>
        public double MaxHomographyAreaRatio { get; set; }

        #region Metodi interni

        /// <summary>
        /// Valida i parametri condivisi dai backend CPU e Vulkan
        /// </summary>
        internal void Validate()
        {
            if (this.MaxFeatures < 1)
                throw new ArgumentOutOfRangeException(nameof(this.MaxFeatures));
            if (this.OctaveLayers < 1)
                throw new ArgumentOutOfRangeException(nameof(this.OctaveLayers));
            if (this.ContrastThreshold <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(this.ContrastThreshold));
            if (this.EdgeThreshold <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(this.EdgeThreshold));
            if (this.Sigma <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(this.Sigma));
            if (this.LoweRatio <= 0.0 || this.LoweRatio >= 1.0)
                throw new ArgumentOutOfRangeException(nameof(this.LoweRatio));
            if (this.MinKeypoints < 1 || this.MinReciprocalMatches < 4 || this.MinInliers < 4)
                throw new ArgumentOutOfRangeException(nameof(this.MinKeypoints));
            if (this.MinInlierRatio <= 0.0 || this.MinInlierRatio > 1.0)
                throw new ArgumentOutOfRangeException(nameof(this.MinInlierRatio));
            if (this.MinCoverage < 0.0 || this.MinCoverage > 1.0)
                throw new ArgumentOutOfRangeException(nameof(this.MinCoverage));
            if (this.RansacReprojectionThreshold <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(this.RansacReprojectionThreshold));
            if (this.MaxMeanReprojectionError <= 0.0 || this.MaxMeanReprojectionError > this.RansacReprojectionThreshold)
                throw new ArgumentOutOfRangeException(nameof(this.MaxMeanReprojectionError));
            if (this.MinHomographyAreaRatio <= 0.0 || this.MaxHomographyAreaRatio <= this.MinHomographyAreaRatio)
                throw new ArgumentOutOfRangeException(nameof(this.MinHomographyAreaRatio));
        }

        #endregion
    }
}
