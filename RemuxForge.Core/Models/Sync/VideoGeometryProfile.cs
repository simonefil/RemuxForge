using System.Globalization;

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

        /// <summary>
        /// True se è stato rilevato auto-crop bordi neri
        /// </summary>
        public bool HasBlackBorderCrop;

        /// <summary>
        /// Crop sinistro rilevato su frame normalizzati
        /// </summary>
        public int CropLeft;

        /// <summary>
        /// Crop destro rilevato su frame normalizzati
        /// </summary>
        public int CropRight;

        /// <summary>
        /// Crop superiore rilevato su frame normalizzati
        /// </summary>
        public int CropTop;

        /// <summary>
        /// Crop inferiore rilevato su frame normalizzati
        /// </summary>
        public int CropBottom;

        /// <summary>
        /// Formatta il profilo per log compatto
        /// </summary>
        /// <returns>Stringa diagnostica compatta</returns>
        public string ToShortString()
        {
            string dar = this.DarNum > 0 && this.DarDen > 0 ? this.DarNum + ":" + this.DarDen : "-";
            string crop = this.HasBlackBorderCrop ? ", crop=L" + this.CropLeft + " R" + this.CropRight + " T" + this.CropTop + " B" + this.CropBottom : "";

            return this.Width + "x" + this.Height + ", SAR " + this.SarNum + ":" + this.SarDen + ", DAR " + dar + ", display " + this.DisplayWidth + "x" + this.DisplayHeight + " (" + this.DisplayAspect.ToString("F3", CultureInfo.InvariantCulture) + ")" + crop;
        }
    }
}
