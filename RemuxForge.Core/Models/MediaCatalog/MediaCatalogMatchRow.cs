using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Riga dell'abbinamento fra file locali ed episodi del catalogo: un file, uno o più episodi consecutivi, o uno dei due soltanto
    /// </summary>
    public class MediaCatalogMatchRow
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="filePath">Percorso del file, vuoto se la riga non ha file</param>
        /// <param name="episodes">Episodi della riga, vuota se la riga non ha episodi</param>
        public MediaCatalogMatchRow(string filePath, List<MediaCatalogEpisode> episodes)
        {
            this.FilePath = filePath;
            this.Episodes = episodes;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percorso del file, vuoto se la riga non ha file
        /// </summary>
        public string FilePath { get; }

        /// <summary>
        /// Episodi consecutivi della stessa stagione, vuota se la riga non ha episodi
        /// </summary>
        public List<MediaCatalogEpisode> Episodes { get; set; }

        #endregion
    }
}
