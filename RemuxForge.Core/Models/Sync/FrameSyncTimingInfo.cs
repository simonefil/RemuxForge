namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Timing diagnostici della sincronizzazione FrameSync SIFT
    /// </summary>
    public class FrameSyncTimingInfo
    {
        #region Proprietà

        /// <summary>
        /// Tempo totale FrameSync
        /// </summary>
        public long TotalMs { get; set; }

        /// <summary>
        /// Tempo di lettura delle informazioni video
        /// </summary>
        public long VideoInfoMs { get; set; }

        /// <summary>
        /// Tempo di analisi della geometria
        /// </summary>
        public long GeometryMs { get; set; }

        /// <summary>
        /// Tempo totale della ricerca iniziale
        /// </summary>
        public long InitialSearchMs { get; set; }

        /// <summary>
        /// Tempo di estrazione delle ancore iniziali
        /// </summary>
        public long InitialExtractMs { get; set; }

        /// <summary>
        /// Tempo di matching NxM e risoluzione iniziale
        /// </summary>
        public long InitialMatchMs { get; set; }

        /// <summary>
        /// Numero di coppie elaborate dalla ricerca iniziale
        /// </summary>
        public long InitialPairCount { get; set; }

        /// <summary>
        /// Tempo totale dei checkpoint
        /// </summary>
        public long CheckpointsMs { get; set; }

        /// <summary>
        /// Tempo di estrazione complessivo dei checkpoint
        /// </summary>
        public long CheckpointExtractMs { get; set; }

        /// <summary>
        /// Tempo di matching complessivo dei checkpoint
        /// </summary>
        public long CheckpointMatchMs { get; set; }

        /// <summary>
        /// Numero di coppie elaborate complessivamente nei checkpoint
        /// </summary>
        public long CheckpointPairCount { get; set; }





        #endregion
    }
}
