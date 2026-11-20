using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Modifiche dirette da scrivere su un file, fuori dal giro dei preset
    /// </summary>
    public class MetadataDirectEdit
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="inputFile">File del record da modificare</param>
        /// <param name="changes">Modifiche</param>
        public MetadataDirectEdit(string inputFile, List<MkvMetadataChange> changes)
        {
            this.InputFile = inputFile;
            this.Changes = changes;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// File del record da modificare
        /// </summary>
        public string InputFile { get; }

        /// <summary>
        /// Modifiche
        /// </summary>
        public List<MkvMetadataChange> Changes { get; }

        #endregion
    }
}
