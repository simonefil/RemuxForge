using System;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Target testuale attivo per inserimento token e funzioni metadata
    /// </summary>
    public class MetadataTextInputTarget
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataTextInputTarget()
        {
            this.InputId = "";
            this.Value = "";
            this.Commit = null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Identificativo DOM dell'input
        /// </summary>
        public string InputId { get; set; }

        /// <summary>
        /// Valore corrente dell'input
        /// </summary>
        public string Value { get; set; }

        /// <summary>
        /// Callback applicazione valore
        /// </summary>
        public Action<string> Commit { get; set; }

        #endregion
    }
}
