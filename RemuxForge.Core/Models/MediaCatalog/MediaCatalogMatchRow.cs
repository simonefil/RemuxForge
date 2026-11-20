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
            this.OriginFiles = new Dictionary<MediaCatalogEpisode, string>();
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

        /// <summary>
        /// File con cui erano abbinati gli episodi prima di un'unione o di uno spostamento; gli episodi mai spostati non ci sono
        /// </summary>
        public Dictionary<MediaCatalogEpisode, string> OriginFiles { get; set; }

        #endregion
    }
}
