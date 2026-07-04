namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Profilo immutabile di estrazione frame usato come chiave per cache e riuso segmenti
    /// </summary>
    public class FrameExtractProfile
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public FrameExtractProfile()
        {
            this.FilePath = "";
            this.StartMs = 0;
            this.DurationSec = 0.0;
            this.TargetFps = 0.0;
            this.GeometryCropToFourThree = false;
            this.ManualAnalysisCropPx = "";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Percorso file video
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Inizio estrazione in millisecondi
        /// </summary>
        public int StartMs { get; set; }

        /// <summary>
        /// Durata estrazione in secondi
        /// </summary>
        public double DurationSec { get; set; }

        /// <summary>
        /// FPS target dell'estrazione
        /// </summary>
        public double TargetFps { get; set; }

        /// <summary>
        /// True se applicare crop 4:3/pillarbox
        /// </summary>
        public bool GeometryCropToFourThree { get; set; }

        /// <summary>
        /// Crop manuale di analisi nel formato L:R:T:B
        /// </summary>
        public string ManualAnalysisCropPx { get; set; }

        /// <summary>
        /// Fine estrazione in millisecondi
        /// </summary>
        public double EndMs
        {
            get { return this.StartMs + (this.DurationSec * 1000.0); }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Verifica se il profilo rappresenta gli stessi parametri di estrazione
        /// </summary>
        public bool SameExtraction(FrameExtractProfile other)
        {
            bool result = false;

            if (other != null &&
                string.Equals(this.FilePath, other.FilePath, System.StringComparison.OrdinalIgnoreCase) &&
                this.StartMs == other.StartMs &&
                System.Math.Abs(this.DurationSec - other.DurationSec) <= 0.0001 &&
                System.Math.Abs(this.TargetFps - other.TargetFps) <= 0.0001 &&
                this.GeometryCropToFourThree == other.GeometryCropToFourThree &&
                string.Equals(this.ManualAnalysisCropPx, other.ManualAnalysisCropPx, System.StringComparison.OrdinalIgnoreCase))
            {
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Verifica se questo profilo è completamente contenuto nel profilo indicato
        /// </summary>
        public bool IsContainedIn(FrameExtractProfile parent)
        {
            bool result = false;

            if (parent != null &&
                string.Equals(this.FilePath, parent.FilePath, System.StringComparison.OrdinalIgnoreCase) &&
                System.Math.Abs(this.TargetFps - parent.TargetFps) <= 0.0001 &&
                this.GeometryCropToFourThree == parent.GeometryCropToFourThree &&
                string.Equals(this.ManualAnalysisCropPx, parent.ManualAnalysisCropPx, System.StringComparison.OrdinalIgnoreCase) &&
                this.StartMs >= parent.StartMs &&
                this.EndMs <= parent.EndMs)
            {
                result = true;
            }

            return result;
        }

        #endregion
    }
}
