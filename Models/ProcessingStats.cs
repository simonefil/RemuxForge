namespace MergeLanguageTracks
{
    public class ProcessingStats
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public ProcessingStats()
        {
            this.Processed = 0;
            this.Skipped = 0;
            this.NoMatch = 0;
            this.NoTracks = 0;
            this.SyncFailed = 0;
            this.Errors = 0;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Numero di file elaborati con successo.
        /// </summary>
        public int Processed { get; set; }

        /// <summary>
        /// Numero di file saltati (nessun ID episodio).
        /// </summary>
        public int Skipped { get; set; }

        /// <summary>
        /// Numero di file senza file lingua corrispondente.
        /// </summary>
        public int NoMatch { get; set; }

        /// <summary>
        /// Numero di file senza tracce corrispondenti.
        /// </summary>
        public int NoTracks { get; set; }

        /// <summary>
        /// Numero di file con sync fallito.
        /// </summary>
        public int SyncFailed { get; set; }

        /// <summary>
        /// Numero di file con errori.
        /// </summary>
        public int Errors { get; set; }

        #endregion
    }
}
