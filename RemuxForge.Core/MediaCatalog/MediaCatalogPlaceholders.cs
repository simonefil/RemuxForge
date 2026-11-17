using RemuxForge.Core.Models;
using RemuxForge.Core.Rename;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RemuxForge.Core.MediaCatalog
{
    /// <summary>
    /// Segnaposto dei dati di catalogo nei pattern con la sintassi del rename: Series, Season, Episode, Absolute, Title, Year.
    /// Season, Episode e Absolute accettano la larghezza (Season:2), applicata a ogni numero di un intervallo (01-02)
    /// </summary>
    public static class MediaCatalogPlaceholders
    {
        #region Costanti

        /// <summary>Titolo della serie</summary>
        public const string SERIES = "Series";

        /// <summary>Numero della stagione</summary>
        public const string SEASON = "Season";

        /// <summary>Numero dell'episodio, o intervallo nei file con più episodi</summary>
        public const string EPISODE = "Episode";

        /// <summary>Numero assoluto, o intervallo; vuoto se il catalogo non lo ha</summary>
        public const string ABSOLUTE = "Absolute";

        /// <summary>Titolo dell'episodio o del film</summary>
        public const string TITLE = "Title";

        /// <summary>Anno della serie o del film</summary>
        public const string YEAR = "Year";

        /// <summary>
        /// Separatore dei titoli di un file con più episodi
        /// </summary>
        public const string TITLE_SEPARATOR = " / ";

        /// <summary>
        /// Separatore degli estremi di un intervallo di numeri
        /// </summary>
        private const char RANGE_SEPARATOR = '-';

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Valori dei segnaposto per un file abbinato a uno o più episodi consecutivi
        /// </summary>
        /// <param name="series">Serie</param>
        /// <param name="episodes">Episodi del file, in ordine</param>
        /// <returns>Valori per nome del segnaposto</returns>
        public static Dictionary<string, string> FromEpisodes(MediaCatalogSeries series, List<MediaCatalogEpisode> episodes)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            MediaCatalogEpisode first = episodes[0];
            MediaCatalogEpisode last = episodes[episodes.Count - 1];
            bool absoluteKnown = episodes.All(episode => episode.AbsoluteNumber.HasValue);

            values[SERIES] = series.Title;
            values[SEASON] = first.SeasonNumber.ToString(CultureInfo.InvariantCulture);
            values[EPISODE] = FormatRange(first.EpisodeNumber, last.EpisodeNumber);
            values[ABSOLUTE] = absoluteKnown ? FormatRange(first.AbsoluteNumber.Value, last.AbsoluteNumber.Value) : "";
            values[TITLE] = string.Join(TITLE_SEPARATOR, episodes.Select(episode => episode.Title).Where(title => title.Length > 0));
            values[YEAR] = series.Year.HasValue ? series.Year.Value.ToString(CultureInfo.InvariantCulture) : "";
            return values;
        }

        /// <summary>
        /// Valori dei segnaposto per un film; quelli di serie restano vuoti
        /// </summary>
        /// <param name="movie">Film</param>
        /// <returns>Valori per nome del segnaposto</returns>
        public static Dictionary<string, string> FromMovie(MediaCatalogMovie movie)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            values[SERIES] = "";
            values[SEASON] = "";
            values[EPISODE] = "";
            values[ABSOLUTE] = "";
            values[TITLE] = movie.Title;
            values[YEAR] = movie.Year.HasValue ? movie.Year.Value.ToString(CultureInfo.InvariantCulture) : "";
            return values;
        }

        /// <summary>
        /// Risolve un segnaposto di catalogo
        /// </summary>
        /// <param name="tagContent">Contenuto fra parentesi angolari, ad esempio Season:2</param>
        /// <param name="values">Valori del file</param>
        /// <param name="value">Valore risolto</param>
        /// <returns>True se il segnaposto è di catalogo</returns>
        public static bool TryResolve(string tagContent, Dictionary<string, string> values, out string value)
        {
            string name = tagContent;
            int width = 0;
            int separator = tagContent.IndexOf(':');

            value = "";
            if (separator > 0)
            {
                name = tagContent.Substring(0, separator);
                if (!IsNumeric(name) || !int.TryParse(tagContent.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out width))
                    return false;
            }

            if (!values.TryGetValue(name, out value))
                return false;

            if (width > 0 && value.Length > 0)
                value = string.Join(RANGE_SEPARATOR, value.Split(RANGE_SEPARATOR).Select(number => number.PadLeft(width, '0')));

            return true;
        }

        /// <summary>
        /// Compone un testo dal pattern; i segnaposto sconosciuti restano come sono
        /// </summary>
        /// <param name="pattern">Pattern con segnaposto</param>
        /// <param name="values">Valori del file</param>
        /// <returns>Testo composto</returns>
        public static string Expand(string pattern, Dictionary<string, string> values)
        {
            return RenameEngine.ExpandTags(pattern, tagContent =>
            {
                string value;
                return TryResolve(tagContent, values, out value) ? value : "<" + tagContent + ">";
            });
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Indica se il segnaposto è un numero che accetta la larghezza
        /// </summary>
        /// <param name="name">Nome del segnaposto</param>
        /// <returns>True per Season, Episode e Absolute</returns>
        private static bool IsNumeric(string name)
        {
            return name == SEASON || name == EPISODE || name == ABSOLUTE;
        }

        /// <summary>
        /// Numero singolo o intervallo primo-ultimo
        /// </summary>
        /// <param name="first">Primo numero</param>
        /// <param name="last">Ultimo numero</param>
        /// <returns>Testo del numero o dell'intervallo</returns>
        private static string FormatRange(int first, int last)
        {
            string text = first.ToString(CultureInfo.InvariantCulture);
            return last == first ? text : text + RANGE_SEPARATOR + last.ToString(CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
