using System;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Identificativi stabili delle card della home, salvati nella configurazione come ultima card usata.
    /// Le modalità Remux, Split e Metadata conservano gli identificativi delle modalità CLI
    /// </summary>
    public static class ToolboxCards
    {
        #region Variabili di classe

        /// <summary>
        /// Identificativi di tutte le card
        /// </summary>
        private static readonly string[] s_ids = { Options.MODE_REMUX, Options.MODE_SPLIT, Options.MODE_METADATA };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Verifica che un identificativo appartenga a una card
        /// </summary>
        /// <param name="id">Identificativo da verificare</param>
        /// <returns>True se la card esiste</returns>
        public static bool IsKnown(string id)
        {
            return Array.IndexOf(s_ids, id) >= 0;
        }

        #endregion
    }
}
