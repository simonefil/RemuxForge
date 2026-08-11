namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Risultato backend-independent del confronto fra feature di due frame
    /// </summary>
    public class FrameFeatureMatchResult
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        public FrameFeatureMatchResult()
        {
            this.BackendName = "";
            this.RejectReason = "";
            this.Homography = new double[0];
        }

        /// <summary>
        /// Backend che ha prodotto il risultato
        /// </summary>
        public string BackendName { get; set; }

        /// <summary>
        /// True quando il match supera tutti i criteri minimi
        /// </summary>
        public bool Accepted { get; set; }

        /// <summary>
        /// Motivo del rifiuto, vuoto per match accettato
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Numero keypoint source
        /// </summary>
        public int SourceKeypointCount { get; set; }

        /// <summary>
        /// Numero keypoint language
        /// </summary>
        public int LanguageKeypointCount { get; set; }

        /// <summary>
        /// Match forward che superano il Lowe ratio test
        /// </summary>
        public int RatioMatchCount { get; set; }

        /// <summary>
        /// Match confermati anche nella direzione inversa
        /// </summary>
        public int ReciprocalMatchCount { get; set; }

        /// <summary>
        /// Match coerenti con l'omografia RANSAC
        /// </summary>
        public int InlierCount { get; set; }

        /// <summary>
        /// Rapporto fra inlier e match reciproci
        /// </summary>
        public double InlierRatio { get; set; }

        /// <summary>
        /// Copertura spaziale degli inlier sul source
        /// </summary>
        public double SourceCoverage { get; set; }

        /// <summary>
        /// Copertura spaziale degli inlier sulla language
        /// </summary>
        public double LanguageCoverage { get; set; }

        /// <summary>
        /// Errore medio di riproiezione degli inlier in pixel
        /// </summary>
        public double MeanReprojectionError { get; set; }

        /// <summary>
        /// Confidence sintetica diagnostica nell'intervallo 0..1
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// Omografia 3x3 row-major, vuota quando non stimabile
        /// </summary>
        public double[] Homography { get; set; }

        /// <summary>
        /// Tick Stopwatch spesi nel matching descriptor bidirezionale
        /// </summary>
        public long DescriptorMatchingTicks { get; set; }

        /// <summary>
        /// Tick Stopwatch spesi nella verifica geometrica
        /// </summary>
        public long GeometryTicks { get; set; }
    }
}
