using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Informazioni complete su un file MKV ottenute da mkvmerge -J
    /// </summary>
    public class MkvFileInfo
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvFileInfo()
        {
            this.Tracks = new List<TrackInfo>();
            this.ContainerTitle = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Lista delle tracce contenute nel file MKV
        /// </summary>
        public List<TrackInfo> Tracks { get; set; }

        /// <summary>
        /// Durata del container in nanosecondi, come riportata da mkvmerge -J
        /// </summary>
        public long ContainerDurationNs { get; set; }

        /// <summary>
        /// Titolo del segmento MKV (container title), stringa vuota se assente
        /// </summary>
        public string ContainerTitle { get; set; }

        #endregion
    }
}
