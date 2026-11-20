using System;

namespace RemuxForge.Core.Infrastructure
{
    /// <summary>
    /// Modelli di testo con tag fra parentesi angolari, condivisi da rinomina e segnaposto del catalogo
    /// </summary>
    public static class TagTemplate
    {
        #region Metodi pubblici

        /// <summary>
        /// Compone un testo sostituendo ogni tag fra parentesi angolari con il valore del risolutore
        /// </summary>
        /// <param name="pattern">Pattern con tag</param>
        /// <param name="resolveTag">Valore di un tag dato il suo contenuto senza parentesi</param>
        /// <returns>Testo composto</returns>
        public static string Expand(string pattern, Func<string, string> resolveTag)
        {
            string result = "";
            int pos = 0;

            while (pos < pattern.Length)
            {
                int tagStart = pattern.IndexOf('<', pos);
                if (tagStart < 0)
                {
                    result = result + pattern.Substring(pos);
                    break;
                }

                if (tagStart > pos)
                    result = result + pattern.Substring(pos, tagStart - pos);

                int tagEnd = pattern.IndexOf('>', tagStart);
                if (tagEnd < 0)
                {
                    result = result + pattern.Substring(tagStart);
                    break;
                }

                result = result + resolveTag(pattern.Substring(tagStart + 1, tagEnd - tagStart - 1));
                pos = tagEnd + 1;
            }

            return result;
        }

        #endregion
    }
}
