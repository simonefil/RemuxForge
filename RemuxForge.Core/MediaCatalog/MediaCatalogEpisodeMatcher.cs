using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.MediaCatalog
{
    /// <summary>
    /// Abbinamento fra file locali ed episodi del catalogo: proposta iniziale dal nome dei file e correzioni a mano
    /// </summary>
    public static class MediaCatalogEpisodeMatcher
    {
        #region Variabili di classe

        /// <summary>
        /// SxxEyy nel nome del file, con gli episodi successivi nelle forme E01E02, E01-E02 ed E01-02
        /// </summary>
        private static readonly Regex s_episodePattern = new Regex(@"S(\d{1,3})E(\d{1,4})((?:-?E\d{1,4}|-\d{1,4})*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Numeri degli episodi successivi
        /// </summary>
        private static readonly Regex s_numberPattern = new Regex(@"\d+", RegexOptions.CultureInvariant);

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Proposta iniziale: un file con SxxEyy nel nome prende quell'episodio e i successivi indicati, se esistono e sono consecutivi;
        /// i file senza numeri prendono per posizione gli episodi rimasti liberi, speciali esclusi.
        /// Sotto i file restano gli episodi liberi, gli speciali in fondo
        /// </summary>
        /// <param name="filePaths">File nell'ordine del percorso</param>
        /// <param name="episodes">Episodi del catalogo fra cui abbinare, ordinati per stagione e numero</param>
        /// <returns>Righe dell'abbinamento</returns>
        public static List<MediaCatalogMatchRow> BuildRows(List<string> filePaths, List<MediaCatalogEpisode> episodes)
        {
            List<MediaCatalogMatchRow> rows = new List<MediaCatalogMatchRow>();
            HashSet<MediaCatalogEpisode> used = new HashSet<MediaCatalogEpisode>();
            List<MediaCatalogMatchRow> withoutNumbers = new List<MediaCatalogMatchRow>();
            int nextFree = 0;

            for (int i = 0; i < filePaths.Count; i++)
            {
                MediaCatalogMatchRow row = new MediaCatalogMatchRow(filePaths[i], new List<MediaCatalogEpisode>());
                Match match = s_episodePattern.Match(Path.GetFileName(filePaths[i]));
                rows.Add(row);

                if (!match.Success)
                {
                    withoutNumbers.Add(row);
                    continue;
                }

                int season = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                MediaCatalogEpisode episode = Find(episodes, season, int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
                if (episode == null || used.Contains(episode))
                    continue;

                row.Episodes.Add(episode);
                used.Add(episode);

                // Un numero dopo l'episodio vale solo se è l'episodio successivo: così S01E01-1080p resta un episodio solo
                foreach (Match number in s_numberPattern.Matches(match.Groups[3].Value))
                {
                    MediaCatalogEpisode last = row.Episodes[row.Episodes.Count - 1];
                    MediaCatalogEpisode next = Find(episodes, season, last.EpisodeNumber + 1);
                    if (next == null || used.Contains(next) || int.Parse(number.Value, CultureInfo.InvariantCulture) != next.EpisodeNumber)
                        break;

                    row.Episodes.Add(next);
                    used.Add(next);
                }
            }

            // Gli speciali non si abbinano per posizione: solo con S00Eyy nel nome o a mano
            for (int i = 0; i < withoutNumbers.Count; i++)
            {
                while (nextFree < episodes.Count && (episodes[nextFree].SeasonNumber == 0 || used.Contains(episodes[nextFree])))
                    nextFree++;

                if (nextFree >= episodes.Count)
                    break;

                withoutNumbers[i].Episodes.Add(episodes[nextFree]);
                used.Add(episodes[nextFree]);
            }

            foreach (bool specials in new bool[] { false, true })
            {
                foreach (MediaCatalogEpisode episode in episodes)
                {
                    if ((episode.SeasonNumber == 0) == specials && !used.Contains(episode))
                        rows.Add(new MediaCatalogMatchRow("", new List<MediaCatalogEpisode> { episode }));
                }
            }

            return rows;
        }

        /// <summary>
        /// Stagioni indicate da SxxEyy nel nome dei file
        /// </summary>
        /// <param name="filePaths">File</param>
        /// <returns>Numeri delle stagioni</returns>
        public static HashSet<int> GetSeasonNumbers(List<string> filePaths)
        {
            HashSet<int> result = new HashSet<int>();
            foreach (string filePath in filePaths)
            {
                Match match = s_episodePattern.Match(Path.GetFileName(filePath));
                if (match.Success)
                    result.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }

            return result;
        }

        /// <summary>
        /// Indica se gli episodi della riga possono unirsi a quelli della riga sopra: stessa stagione e numeri consecutivi
        /// </summary>
        /// <param name="rows">Righe</param>
        /// <param name="index">Riga da unire</param>
        /// <returns>True se l'unione è possibile</returns>
        public static bool CanMergeWithPrevious(List<MediaCatalogMatchRow> rows, int index)
        {
            if (index <= 0 || index >= rows.Count || rows[index].Episodes.Count == 0 || rows[index - 1].Episodes.Count == 0)
                return false;

            MediaCatalogEpisode previous = rows[index - 1].Episodes[rows[index - 1].Episodes.Count - 1];
            MediaCatalogEpisode first = rows[index].Episodes[0];
            return previous.SeasonNumber == first.SeasonNumber && first.EpisodeNumber == previous.EpisodeNumber + 1;
        }

        /// <summary>
        /// Unisce gli episodi della riga a quelli della riga sopra, ricordando per ognuno il file con cui era abbinato
        /// </summary>
        /// <param name="rows">Righe</param>
        /// <param name="index">Riga da unire</param>
        public static void MergeWithPrevious(List<MediaCatalogMatchRow> rows, int index)
        {
            if (!CanMergeWithPrevious(rows, index))
                return;

            foreach (MediaCatalogEpisode episode in rows[index].Episodes)
            {
                string origin;
                rows[index - 1].OriginFiles[episode] = rows[index].OriginFiles.TryGetValue(episode, out origin) ? origin : rows[index].FilePath;
            }

            rows[index - 1].Episodes.AddRange(rows[index].Episodes);
            rows[index].Episodes = new List<MediaCatalogEpisode>();
            rows[index].OriginFiles = new Dictionary<MediaCatalogEpisode, string>();
            RemoveEmptyRows(rows);
        }

        /// <summary>
        /// Stacca l'ultimo episodio della riga: torna al file con cui era abbinato prima dell'unione se è ancora senza episodi,
        /// altrimenti al primo file più in basso rimasto senza episodi, altrimenti in una riga nuova senza file
        /// </summary>
        /// <param name="rows">Righe</param>
        /// <param name="index">Riga con più episodi</param>
        public static void Split(List<MediaCatalogMatchRow> rows, int index)
        {
            if (index < 0 || index >= rows.Count || rows[index].Episodes.Count < 2)
                return;

            List<MediaCatalogEpisode> episodes = rows[index].Episodes;
            MediaCatalogEpisode last = episodes[episodes.Count - 1];
            string origin;
            episodes.RemoveAt(episodes.Count - 1);

            MediaCatalogMatchRow target = rows[index].OriginFiles.TryGetValue(last, out origin) && origin.Length > 0
                ? rows.Find(row => row.FilePath == origin && row.Episodes.Count == 0)
                : null;
            if (target == null)
                target = rows.Skip(index + 1).FirstOrDefault(row => row.FilePath.Length > 0 && row.Episodes.Count == 0);

            rows[index].OriginFiles.Remove(last);
            if (target != null)
                target.Episodes = new List<MediaCatalogEpisode> { last };
            else
                rows.Insert(index + 1, new MediaCatalogMatchRow("", new List<MediaCatalogEpisode> { last }));
        }

        /// <summary>
        /// Scambia gli episodi di due righe; i file restano dove sono e ogni episodio porta con sé il file da cui parte,
        /// così Separa lo riporta lì anche se è stato spostato solo per unirlo a un'altra riga
        /// </summary>
        /// <param name="rows">Righe</param>
        /// <param name="from">Riga di partenza</param>
        /// <param name="to">Riga di arrivo</param>
        public static void SwapEpisodes(List<MediaCatalogMatchRow> rows, int from, int to)
        {
            if (from < 0 || to < 0 || from >= rows.Count || to >= rows.Count || from == to)
                return;

            foreach (MediaCatalogMatchRow row in new[] { rows[from], rows[to] })
            {
                foreach (MediaCatalogEpisode episode in row.Episodes)
                {
                    if (row.FilePath.Length > 0 && !row.OriginFiles.ContainsKey(episode))
                        row.OriginFiles[episode] = row.FilePath;
                }
            }

            List<MediaCatalogEpisode> episodes = rows[from].Episodes;
            Dictionary<MediaCatalogEpisode, string> originFiles = rows[from].OriginFiles;
            rows[from].Episodes = rows[to].Episodes;
            rows[from].OriginFiles = rows[to].OriginFiles;
            rows[to].Episodes = episodes;
            rows[to].OriginFiles = originFiles;
            RemoveEmptyRows(rows);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Episodio del catalogo per stagione e numero
        /// </summary>
        /// <param name="episodes">Episodi del catalogo</param>
        /// <param name="season">Stagione</param>
        /// <param name="number">Numero dell'episodio</param>
        /// <returns>Episodio, null se assente</returns>
        private static MediaCatalogEpisode Find(List<MediaCatalogEpisode> episodes, int season, int number)
        {
            foreach (MediaCatalogEpisode episode in episodes)
            {
                if (episode.SeasonNumber == season && episode.EpisodeNumber == number)
                    return episode;
            }

            return null;
        }

        /// <summary>
        /// Toglie le righe rimaste senza file e senza episodi
        /// </summary>
        /// <param name="rows">Righe</param>
        private static void RemoveEmptyRows(List<MediaCatalogMatchRow> rows)
        {
            rows.RemoveAll(row => row.FilePath.Length == 0 && row.Episodes.Count == 0);
        }

        #endregion
    }
}
