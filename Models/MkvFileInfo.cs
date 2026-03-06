using System.Collections.Generic;

namespace MergeLanguageTracks
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
            this.ContainerDurationNs = 0;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Lista delle tracce contenute nel file MKV
        /// </summary>
        public List<TrackInfo> Tracks { get; set; }

        /// <summary>
        /// Durata del container in nanosecondi, come riportata da mkvmerge -J
        /// </summary>
        public long ContainerDurationNs { get; set; }

        #endregion
    }
}
