namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Riga preview Advanced Rename
    /// </summary>
    public class RenamePreviewItem
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public RenamePreviewItem()
        {
            this.OriginalName = "";
            this.OriginalFullPath = "";
            this.NewName = "";
            this.TargetFullPath = "";
            this.HasConflict = false;
            this.HasError = false;
            this.ErrorMessage = "";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Nome file originale
        /// </summary>
        public string OriginalName { get; set; }

        /// <summary>
        /// Percorso completo originale
        /// </summary>
        public string OriginalFullPath { get; set; }

        /// <summary>
        /// Nome file calcolato
        /// </summary>
        public string NewName { get; set; }

        /// <summary>
        /// Percorso finale calcolato
        /// </summary>
        public string TargetFullPath { get; set; }

        /// <summary>
        /// True se esiste collisione con un'altra preview
        /// </summary>
        public bool HasConflict { get; set; }

        /// <summary>
        /// True se il nome è invalido o il target è occupato
        /// </summary>
        public bool HasError { get; set; }

        /// <summary>
        /// Dettaglio errore preview
        /// </summary>
        public string ErrorMessage { get; set; }

        #endregion
    }
}
