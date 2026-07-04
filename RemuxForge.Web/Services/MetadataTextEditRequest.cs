using System;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Richiesta UI per aprire l'editor testo espanso metadata
    /// </summary>
    public class MetadataTextEditRequest
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataTextEditRequest()
        {
            this.Title = "";
            this.Value = "";
            this.Commit = null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Titolo dialog
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Valore iniziale
        /// </summary>
        public string Value { get; set; }

        /// <summary>
        /// Callback applicazione valore
        /// </summary>
        public Action<string> Commit { get; set; }

        #endregion
    }
}
