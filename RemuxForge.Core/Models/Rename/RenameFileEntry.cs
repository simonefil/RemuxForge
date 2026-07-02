using System;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// File sorgente per Advanced Rename
    /// </summary>
    public class RenameFileEntry
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public RenameFileEntry()
        {
            this.Name = "";
            this.FullPath = "";
            this.LastModified = DateTime.MinValue;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Nome file
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Percorso completo
        /// </summary>
        public string FullPath { get; set; }

        /// <summary>
        /// Data ultima modifica
        /// </summary>
        public DateTime LastModified { get; set; }

        #endregion
    }
}
