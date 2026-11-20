using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Esito dell'abbinamento al catalogo aperto dalla rinomina: titolo abbinato e valori dei segnaposto per file
    /// </summary>
    public class MediaCatalogRenameMatch
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="kind">Serie o film</param>
        /// <param name="title">Titolo abbinato, per la descrizione del metodo</param>
        public MediaCatalogRenameMatch(MediaCatalogKind kind, string title)
        {
            this.Kind = kind;
            this.Title = title;
            this.Values = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Serie o film
        /// </summary>
        public MediaCatalogKind Kind { get; }

        /// <summary>
        /// Titolo abbinato
        /// </summary>
        public string Title { get; }

        /// <summary>
        /// Valori dei segnaposto per percorso completo del file abbinato
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> Values { get; }

        #endregion
    }
}
