using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.MediaCatalog
{
    /// <summary>
    /// Fonte TheTVDB tramite API v4 ufficiale, con la chiave API dell'utente
    /// </summary>
    public class TvdbApiSource : MediaCatalogApiSourceBase
    {
        #region Costanti

        /// <summary>
        /// Indirizzo base dell'API v4
        /// </summary>
        private const string API_BASE_URL = "https://api4.thetvdb.com/v4";

        /// <summary>
        /// Indirizzo base delle immagini quando l'API restituisce un percorso relativo
        /// </summary>
        private const string ARTWORK_BASE_URL = "https://artworks.thetvdb.com";

        /// <summary>
        /// Lingua di ripiego dei testi
        /// </summary>
        private const string FALLBACK_LANGUAGE = "eng";

        /// <summary>
        /// Ordine episodi usato per stagioni e numeri
        /// </summary>
        private const string SEASON_TYPE = "official";

        /// <summary>
        /// Risultati massimi di una ricerca
        /// </summary>
        private const int SEARCH_LIMIT = 20;

        /// <summary>
        /// Risultati chiesti al catalogo: la ricerca è approssimata e il titolo cercato può stare oltre i primi venti
        /// </summary>
        private const int SEARCH_FETCH_LIMIT = 50;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Legge la chiave API corrente dalle impostazioni
        /// </summary>
        private readonly Func<string> _apiKeyProvider;


        /// <summary>
        /// Serializza login e lettura delle lingue
        /// </summary>
        private readonly SemaphoreSlim _sessionLock;

        /// <summary>
        /// Token JWT della sessione, vuoto prima del login
        /// </summary>
        private string _token;

        /// <summary>
        /// Chiave con cui è stato ottenuto il token: se cambia nelle impostazioni si rifà il login
        /// </summary>
        private string _tokenApiKey;

        /// <summary>
        /// Codici lingua accettati da TVDB, null finché non letti
        /// </summary>
        private HashSet<string> _languages;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="apiKeyProvider">Lettura della chiave API dell'utente dalle impostazioni</param>
        public TvdbApiSource(Func<string> apiKeyProvider)
        {
            this._apiKeyProvider = apiKeyProvider;
            this._sessionLock = new SemaphoreSlim(1, 1);
            this._token = "";
            this._tokenApiKey = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Catalogo servito dalla fonte
        /// </summary>
        public override MediaCatalogProvider Provider { get { return MediaCatalogProvider.Tvdb; } }

        /// <summary>
        /// Cerca serie o film per titolo
        /// </summary>
        /// <param name="kind">Serie o film</param>
        /// <param name="query">Testo cercato</param>
        /// <param name="year">Anno di prima uscita, null per non filtrare</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Risultati</returns>
        public override async Task<List<MediaCatalogSearchHit>> SearchAsync(MediaCatalogKind kind, string query, int? year, string language, CancellationToken cancellationToken)
        {
            List<MediaCatalogSearchHit> result = new List<MediaCatalogSearchHit>();
            List<MediaCatalogSearchHit> others = new List<MediaCatalogSearchHit>();
            CompareInfo compare = CultureInfo.InvariantCulture.CompareInfo;
            string text = (query ?? "").Trim();
            string tvdbLanguage = await this.ResolveLanguageAsync(language, cancellationToken);
            string url = API_BASE_URL + "/search?query=" + Uri.EscapeDataString(text) + "&type=" + (kind == MediaCatalogKind.Movie ? "movie" : "series") + "&limit=" + SEARCH_FETCH_LIMIT.ToString(CultureInfo.InvariantCulture);
            if (year.HasValue)
                url += "&year=" + year.Value.ToString(CultureInfo.InvariantCulture);

            JsonNode root = await this.GetJsonAsync(url, cancellationToken);
            JsonArray data = root["data"] as JsonArray;
            if (data == null)
                return result;

            foreach (JsonNode item in data)
            {
                if (item == null)
                    continue;

                string key = ReadString(item["tvdb_id"]);
                if (key.Length == 0)
                    continue;

                // Nella ricerca le traduzioni sono mappe lingua -> testo; name è il titolo nella lingua originale
                string name = ReadString(item["name"]);
                MediaCatalogSearchHit hit = new MediaCatalogSearchHit();
                hit.Key = key;
                hit.Id = ReadLong(item["tvdb_id"]);
                hit.Kind = kind;
                hit.Title = PickFromMap(item["translations"] as JsonObject, tvdbLanguage, name);
                hit.OriginalTitle = hit.Title == name ? "" : name;
                hit.Year = ReadPositiveInt(item["year"]);
                hit.Overview = PickFromMap(item["overviews"] as JsonObject, tvdbLanguage, ReadString(item["overview"]));
                hit.PosterUrl = ToArtworkUrl(ReadString(item["image_url"]));

                // In testa, nell'ordine del catalogo, i titoli che contengono il testo cercato nel nome, in una traduzione o in un alias
                List<string> titles = new List<string> { name };
                if (item["translations"] is JsonObject translations)
                    titles.AddRange(translations.Select(pair => ReadString(pair.Value)));
                if (item["aliases"] is JsonArray aliases)
                    titles.AddRange(aliases.Select(ReadString));

                if (text.Length > 0 && titles.Any(title => compare.IndexOf(title, text, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0))
                    result.Add(hit);
                else
                    others.Add(hit);
            }

            result.AddRange(others);
            return result.Count > SEARCH_LIMIT ? result.GetRange(0, SEARCH_LIMIT) : result;
        }

        /// <summary>
        /// Serie completa nell'ordine ufficiale, con testi e persone di ogni episodio
        /// </summary>
        /// <param name="key">ID TVDB della serie</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="progress">Avanzamento in episodi, può essere null</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Serie</returns>
        public override async Task<MediaCatalogSeries> GetSeriesAsync(string key, string language, IProgress<MediaCatalogProgress> progress, CancellationToken cancellationToken)
        {
            string id = ParseKey(key);
            string tvdbLanguage = await this.ResolveLanguageAsync(language, cancellationToken);
            JsonNode root = await this.GetJsonAsync(API_BASE_URL + "/series/" + id + "/extended?meta=translations&short=true", cancellationToken);
            JsonNode data = root["data"];
            if (data == null)
                throw this.CreateFormatError("series.data");

            MediaCatalogSeries series = new MediaCatalogSeries();
            string name = ReadString(data["name"]);
            JsonObject translations = data["translations"] as JsonObject;
            series.Key = key;
            series.Id = ReadLong(data["id"]);
            series.Title = PickTranslation(translations, "nameTranslations", "name", tvdbLanguage, name);
            series.OriginalTitle = series.Title == name ? "" : name;
            series.Year = ReadPositiveInt(data["year"]);
            series.Overview = PickTranslation(translations, "overviewTranslations", "overview", tvdbLanguage, ReadString(data["overview"]));
            series.PosterUrl = ToArtworkUrl(ReadString(data["image"]));
            ReadGenres(data["genres"] as JsonArray, series.Genres);

            HashSet<int> seasonNumbers = new HashSet<int>();
            if (data["seasons"] is JsonArray seasons)
            {
                foreach (JsonNode season in seasons)
                {
                    long? number = season != null ? ReadLong(season["number"]) : null;
                    if (!number.HasValue || number.Value < 0 || ReadString(season["type"]?["type"]) != SEASON_TYPE || !seasonNumbers.Add((int)number.Value))
                        continue;

                    MediaCatalogSeason item = new MediaCatalogSeason();
                    item.Number = (int)number.Value;
                    item.PosterUrl = ToArtworkUrl(ReadString(season["image"]));
                    series.Seasons.Add(item);
                }
            }
            series.Seasons.Sort((a, b) => a.Number.CompareTo(b.Number));

            List<JsonNode> baseEpisodes = await this.ReadEpisodeListAsync(id, SEASON_TYPE, cancellationToken);
            Dictionary<long, int> absoluteNumbers = null;
            foreach (JsonNode episode in baseEpisodes)
            {
                if (!ReadPositiveInt(episode["absoluteNumber"]).HasValue)
                {
                    // Nell'ordine ufficiale il numero assoluto può mancare: si prende dall'ordine assoluto, dove number è l'assoluto
                    absoluteNumbers = await this.ReadAbsoluteNumbersAsync(id, cancellationToken);
                    break;
                }
            }

            if (progress != null)
                progress.Report(new MediaCatalogProgress(0, baseEpisodes.Count));

            // Un episodio per volta, per non caricare il servizio
            foreach (JsonNode baseEpisode in baseEpisodes)
            {
                series.Episodes.Add(await this.ReadEpisodeAsync(baseEpisode, absoluteNumbers, tvdbLanguage, cancellationToken));
                if (progress != null)
                    progress.Report(new MediaCatalogProgress(series.Episodes.Count, baseEpisodes.Count));
            }
            series.Episodes.Sort((a, b) => a.SeasonNumber != b.SeasonNumber ? a.SeasonNumber.CompareTo(b.SeasonNumber) : a.EpisodeNumber.CompareTo(b.EpisodeNumber));
            return series;
        }

        /// <summary>
        /// Film completo
        /// </summary>
        /// <param name="key">ID TVDB del film</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Film</returns>
        public override async Task<MediaCatalogMovie> GetMovieAsync(string key, string language, CancellationToken cancellationToken)
        {
            string id = ParseKey(key);
            string tvdbLanguage = await this.ResolveLanguageAsync(language, cancellationToken);
            JsonNode root = await this.GetJsonAsync(API_BASE_URL + "/movies/" + id + "/extended?meta=translations", cancellationToken);
            JsonNode data = root["data"];
            if (data == null)
                throw this.CreateFormatError("movie.data");

            MediaCatalogMovie movie = new MediaCatalogMovie();
            string name = ReadString(data["name"]);
            JsonObject translations = data["translations"] as JsonObject;
            movie.Key = key;
            movie.Id = ReadLong(data["id"]);
            movie.Title = PickTranslation(translations, "nameTranslations", "name", tvdbLanguage, name);
            movie.OriginalTitle = movie.Title == name ? "" : name;
            movie.Year = ReadPositiveInt(data["year"]);

            // Il film extended non ha una sinossi di base: solo le traduzioni
            movie.Overview = PickTranslation(translations, "overviewTranslations", "overview", tvdbLanguage, "");
            movie.ReleaseDate = ReadDate(data["first_release"]?["date"]);
            if (!movie.ReleaseDate.HasValue && data["releases"] is JsonArray releases)
            {
                foreach (JsonNode release in releases)
                {
                    DateTime? date = release != null ? ReadDate(release["date"]) : null;
                    if (date.HasValue && (!movie.ReleaseDate.HasValue || date.Value < movie.ReleaseDate.Value))
                        movie.ReleaseDate = date;
                }
            }

            ReadGenres(data["genres"] as JsonArray, movie.Genres);
            ReadPeople(data["characters"] as JsonArray, movie.Directors, movie.Writers, movie.Actors);
            movie.PosterUrl = ToArtworkUrl(ReadString(data["image"]));
            return movie;
        }

        #endregion

        #region Metodi protected

        /// <summary>
        /// Il token della sessione scade dopo un mese: un 401 lo rinnova
        /// </summary>
        protected override bool CanRenewAuthorization { get { return true; } }

        /// <summary>
        /// Mette il token della sessione sulla richiesta, facendo il login se manca, se è da rinnovare o se la chiave è cambiata
        /// </summary>
        /// <param name="request">Richiesta da autorizzare</param>
        /// <param name="renew">Vero dopo un 401</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        protected override async Task AuthorizeAsync(HttpRequestMessage request, bool renew, CancellationToken cancellationToken)
        {
            string apiKey = (this._apiKeyProvider() ?? "").Trim();
            if (apiKey.Length == 0)
                throw new MediaCatalogException(MediaCatalogErrorKind.NotConfigured, AppText.F("mediaCatalog.error.notConfigured", this.DisplayName));

            string staleToken = renew ? this._token : null;
            string token;
            await this._sessionLock.WaitAsync(cancellationToken);
            try
            {
                // Un'altra richiesta può aver già rinnovato il token mentre questa aspettava
                if (this._token.Length == 0 || this._tokenApiKey != apiKey || (renew && this._token == staleToken))
                {
                    JsonObject payload = new JsonObject();
                    payload["apikey"] = apiKey;
                    JsonNode root = await this.PostJsonAsync(API_BASE_URL + "/login", payload, cancellationToken);
                    string newToken = ReadString(root["data"]?["token"]);
                    if (newToken.Length == 0)
                        throw this.CreateFormatError("login.token");

                    this._token = newToken;
                    this._tokenApiKey = apiKey;
                }

                token = this._token;
            }
            finally
            {
                this._sessionLock.Release();
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Converte la lingua dell'app nel codice TVDB, verificato sull'elenco /languages; inglese se TVDB non la conosce
        /// </summary>
        /// <param name="language">Codice ISO 639-2/T</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Codice lingua TVDB</returns>
        private async Task<string> ResolveLanguageAsync(string language, CancellationToken cancellationToken)
        {
            if (this._languages == null)
            {
                JsonNode root = await this.GetJsonAsync(API_BASE_URL + "/languages", cancellationToken);
                HashSet<string> languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (root["data"] is JsonArray data)
                {
                    foreach (JsonNode item in data)
                    {
                        string code = item != null ? ReadString(item["id"]) : "";
                        if (code.Length > 0)
                            languages.Add(code);
                    }
                }

                this._languages = languages;
            }

            // TVDB usa i codici ISO 639-2/T come l'app; "por" è il portoghese europeo, "pt" il brasiliano
            string requested = (language ?? "").Trim().ToLowerInvariant();
            return this._languages.Contains(requested) ? requested : FALLBACK_LANGUAGE;
        }

        /// <summary>
        /// Legge tutte le pagine dell'elenco episodi di un ordine
        /// </summary>
        /// <param name="id">ID della serie</param>
        /// <param name="seasonType">Ordine: official, absolute...</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Episodi base</returns>
        private async Task<List<JsonNode>> ReadEpisodeListAsync(string id, string seasonType, CancellationToken cancellationToken)
        {
            List<JsonNode> result = new List<JsonNode>();
            int page = 0;

            // La dimensione della pagina non è documentata: si legge finché una pagina arriva vuota
            while (true)
            {
                JsonNode root = await this.GetJsonAsync(API_BASE_URL + "/series/" + id + "/episodes/" + seasonType + "?page=" + page.ToString(CultureInfo.InvariantCulture), cancellationToken);
                JsonArray episodes = root["data"]?["episodes"] as JsonArray;
                if (episodes == null || episodes.Count == 0)
                    return result;

                foreach (JsonNode episode in episodes)
                {
                    if (episode != null && ReadLong(episode["id"]).HasValue)
                        result.Add(episode);
                }

                page++;
            }
        }

        /// <summary>
        /// Numeri assoluti per ID episodio dall'ordine assoluto
        /// </summary>
        /// <param name="id">ID della serie</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Numero assoluto per ID episodio</returns>
        private async Task<Dictionary<long, int>> ReadAbsoluteNumbersAsync(string id, CancellationToken cancellationToken)
        {
            Dictionary<long, int> result = new Dictionary<long, int>();
            foreach (JsonNode episode in await this.ReadEpisodeListAsync(id, "absolute", cancellationToken))
            {
                int? number = ReadPositiveInt(episode["number"]);
                if (number.HasValue)
                    result[ReadLong(episode["id"]).Value] = number.Value;
            }

            return result;
        }

        /// <summary>
        /// Completa un episodio con testi nella lingua chiesta e persone dall'episodio extended
        /// </summary>
        /// <param name="baseEpisode">Episodio dell'elenco ufficiale</param>
        /// <param name="absoluteNumbers">Numeri assoluti dall'ordine assoluto, null se l'elenco ufficiale li ha tutti</param>
        /// <param name="tvdbLanguage">Codice lingua TVDB</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Episodio</returns>
        private async Task<MediaCatalogEpisode> ReadEpisodeAsync(JsonNode baseEpisode, Dictionary<long, int> absoluteNumbers, string tvdbLanguage, CancellationToken cancellationToken)
        {
            MediaCatalogEpisode episode = new MediaCatalogEpisode();
            long id = ReadLong(baseEpisode["id"]).Value;
            int absolute;
            episode.Id = id;
            episode.SeasonNumber = (int)(ReadLong(baseEpisode["seasonNumber"]) ?? 0);
            episode.EpisodeNumber = (int)(ReadLong(baseEpisode["number"]) ?? 0);
            episode.AbsoluteNumber = ReadPositiveInt(baseEpisode["absoluteNumber"]);
            if (!episode.AbsoluteNumber.HasValue && absoluteNumbers != null && absoluteNumbers.TryGetValue(id, out absolute))
                episode.AbsoluteNumber = absolute;

            episode.AiredDate = ReadDate(baseEpisode["aired"]);
            episode.RuntimeMinutes = ReadPositiveInt(baseEpisode["runtime"]);

            // L'extended porta in una sola richiesta le traduzioni e le persone dell'episodio
            JsonNode root = await this.GetJsonAsync(API_BASE_URL + "/episodes/" + id.ToString(CultureInfo.InvariantCulture) + "/extended?meta=translations", cancellationToken);
            JsonNode data = root["data"];
            JsonObject translations = data != null ? data["translations"] as JsonObject : null;
            episode.Title = PickTranslation(translations, "nameTranslations", "name", tvdbLanguage, ReadString(baseEpisode["name"]));
            episode.Overview = PickTranslation(translations, "overviewTranslations", "overview", tvdbLanguage, ReadString(baseEpisode["overview"]));
            if (data != null)
                ReadPeople(data["characters"] as JsonArray, episode.Directors, episode.Writers, episode.Actors);

            return episode;
        }

        /// <summary>
        /// Chiave TVDB come ID numerico per i percorsi
        /// </summary>
        /// <param name="key">Chiave del risultato di ricerca</param>
        /// <returns>ID in forma testuale invariante</returns>
        private string ParseKey(string key)
        {
            long id;
            if (!long.TryParse((key ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0)
                throw new MediaCatalogException(MediaCatalogErrorKind.NotFound, AppText.F("mediaCatalog.error.notFound", this.DisplayName));

            return id.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Testo da una mappa lingua -> testo: lingua chiesta, poi inglese, poi il valore di base
        /// </summary>
        /// <param name="map">Mappa delle traduzioni</param>
        /// <param name="language">Codice lingua TVDB</param>
        /// <param name="fallback">Valore di base</param>
        /// <returns>Testo</returns>
        private static string PickFromMap(JsonObject map, string language, string fallback)
        {
            if (map != null)
            {
                string text = ReadString(map[language]);
                if (text.Length == 0)
                    text = ReadString(map[FALLBACK_LANGUAGE]);
                if (text.Length > 0)
                    return text;
            }

            return fallback;
        }

        /// <summary>
        /// Testo dalle traduzioni extended: lingua chiesta, poi inglese, poi il valore di base
        /// </summary>
        /// <param name="translations">Oggetto translations del record</param>
        /// <param name="listName">nameTranslations o overviewTranslations</param>
        /// <param name="field">name o overview</param>
        /// <param name="language">Codice lingua TVDB</param>
        /// <param name="fallback">Valore di base</param>
        /// <returns>Testo</returns>
        private static string PickTranslation(JsonObject translations, string listName, string field, string language, string fallback)
        {
            JsonArray list = translations != null ? translations[listName] as JsonArray : null;
            string english = "";
            if (list != null)
            {
                foreach (JsonNode item in list)
                {
                    if (item == null || (item["isAlias"] is JsonValue alias && alias.TryGetValue(out bool isAlias) && isAlias))
                        continue;

                    string itemLanguage = ReadString(item["language"]);
                    string text = ReadString(item[field]);
                    if (text.Length == 0)
                        continue;

                    if (string.Equals(itemLanguage, language, StringComparison.OrdinalIgnoreCase))
                        return text;

                    if (english.Length == 0 && string.Equals(itemLanguage, FALLBACK_LANGUAGE, StringComparison.OrdinalIgnoreCase))
                        english = text;
                }
            }

            return english.Length > 0 ? english : fallback;
        }

        /// <summary>
        /// Nomi dei generi
        /// </summary>
        /// <param name="genres">Array GenreBaseRecord</param>
        /// <param name="target">Lista da riempire</param>
        private static void ReadGenres(JsonArray genres, List<string> target)
        {
            if (genres == null)
                return;

            foreach (JsonNode genre in genres)
            {
                if (genre != null)
                    AddDistinct(target, ReadString(genre["name"]));
            }
        }

        /// <summary>
        /// Registi, sceneggiatori e attori dai Character, nell'ordine dei crediti
        /// </summary>
        /// <param name="characters">Array Character</param>
        /// <param name="directors">Registi</param>
        /// <param name="writers">Sceneggiatori</param>
        /// <param name="actors">Attori, poi guest star</param>
        private static void ReadPeople(JsonArray characters, List<string> directors, List<string> writers, List<string> actors)
        {
            if (characters == null)
                return;

            List<JsonNode> sorted = new List<JsonNode>();
            foreach (JsonNode character in characters)
            {
                if (character != null)
                    sorted.Add(character);
            }
            sorted.Sort((a, b) => (ReadLong(a["sort"]) ?? long.MaxValue).CompareTo(ReadLong(b["sort"]) ?? long.MaxValue));

            List<string> guestStars = new List<string>();
            foreach (JsonNode character in sorted)
            {
                string person = ReadString(character["personName"]);
                switch (ReadString(character["peopleType"]))
                {
                    case "Director":
                        AddDistinct(directors, person);
                        break;
                    case "Writer":
                        AddDistinct(writers, person);
                        break;
                    case "Actor":
                        AddDistinct(actors, person);
                        break;
                    case "Guest Star":
                        AddDistinct(guestStars, person);
                        break;
                }
            }

            foreach (string guestStar in guestStars)
            {
                AddDistinct(actors, guestStar);
            }
        }

        /// <summary>
        /// URL assoluto di un'immagine TVDB, vuoto se assente
        /// </summary>
        /// <param name="image">URL o percorso</param>
        /// <returns>URL assoluto</returns>
        private static string ToArtworkUrl(string image)
        {
            if (image.Length == 0 || image.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return image;

            return ARTWORK_BASE_URL + (image.StartsWith("/", StringComparison.Ordinal) ? image : "/" + image);
        }

        #endregion
    }
}
