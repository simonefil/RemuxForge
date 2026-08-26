namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Profilo geometrico video rilevante per normalizzazione e diagnostica frame-sync
    /// </summary>
    public class VideoGeometryProfile
    {
        /// <summary>
        /// Percorso file
        /// </summary>
        public string FilePath;

        /// <summary>
        /// Larghezza coded
        /// </summary>
        public int Width;

        /// <summary>
        /// Altezza coded
        /// </summary>
        public int Height;

        /// <summary>
        /// Numeratore sample aspect ratio
        /// </summary>
        public int SarNum;

        /// <summary>
        /// Denominatore sample aspect ratio
        /// </summary>
        public int SarDen;

        /// <summary>
        /// Numeratore display aspect ratio, se dichiarato
        /// </summary>
        public int DarNum;

        /// <summary>
        /// Denominatore display aspect ratio, se dichiarato
        /// </summary>
        public int DarDen;

        /// <summary>
        /// Larghezza dopo applicazione SAR
        /// </summary>
        public int DisplayWidth;

        /// <summary>
        /// Altezza display
        /// </summary>
        public int DisplayHeight;

        /// <summary>
        /// Aspect ratio display normalizzato
        /// </summary>
        public double DisplayAspect;

    }
}
