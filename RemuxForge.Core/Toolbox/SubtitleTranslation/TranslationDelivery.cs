using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Esito della lettura di una risposta di una chiamata con consegna da validare: senza errori la consegna è valida
    /// </summary>
    public class TranslationDelivery
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationDelivery()
        {
            this.Errors = new List<string>();
            this.WebSearchCalls = 0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Errori della consegna respinta, vuoto con la consegna valida
        /// </summary>
        public List<string> Errors { get; set; }

        /// <summary>
        /// Item web_search_call ricevuti
        /// </summary>
        public int WebSearchCalls { get; set; }

        #endregion
    }
}
