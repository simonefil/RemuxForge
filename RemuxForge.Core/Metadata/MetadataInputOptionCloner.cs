using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Clona opzioni input metadata evitando condivisione mutabile tra cataloghi e UI
    /// </summary>
    public static class MetadataInputOptionCloner
    {
        #region Metodi pubblici

        /// <summary>
        /// Clona una lista di opzioni input
        /// </summary>
        /// <param name="source">Opzioni sorgente</param>
        /// <returns>Opzioni clonate</returns>
        public static List<MetadataInputOption> CloneList(List<MetadataInputOption> source)
        {
            List<MetadataInputOption> result = new List<MetadataInputOption>();
            if (source == null)
                return result;

            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] == null)
                    continue;

                MetadataInputOption option = new MetadataInputOption();
                option.Value = source[i].Value;
                option.Label = source[i].Label;
                option.Description = source[i].Description;
                result.Add(option);
            }

            return result;
        }

        #endregion
    }
}
