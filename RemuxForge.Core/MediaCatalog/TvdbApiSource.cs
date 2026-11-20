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
        public const string ARTWORK_BASE_URL = "https://artworks.thetvdb.com";

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
        /// Serializza il login
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
        /// Codici lingua accettati da TVDB, null finché non letti; richieste concorrenti al primo uso possono leggerli più volte
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
                hit.Kind = kind;
                hit.Title = PickFromMap(item["translations"] as JsonObject, tvdbLanguage, name);
                hit.OriginalTitle = hit.Title == name ? "" : name;
                hit.Year = ReadPositiveInt(item["year"]);
                hit.Overview = PickFromMap(item["overviews"] as JsonObject, tvdbLanguage, ReadString(item["overview"]));

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
        /// Serie con le stagioni dell'ordine ufficiale; TVDB non indica quanti episodi ha ogni stagione
        /// </summary>
        /// <param name="key">ID TVDB della serie</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Serie</returns>
        public override async Task<MediaCatalogSeries> GetSeriesAsync(string key, string language, CancellationToken cancellationToken)
        {
            string id = ParseKey(key);
            string tvdbLanguage = await this.ResolveLanguageAsync(language, cancellationToken);
            JsonNode root = await this.GetJsonAsync(API_BASE_URL + "/series/" + id + "/extended?meta=translations&short=true", cancellationToken);
            JsonNode data = root["data"];
            if (data == null)
                throw this.CreateFormatError("series.data");

            MediaCatalogSeries series = new MediaCatalogSeries();
            series.Key = key;
            series.Id = ReadLong(data["id"]);
            series.Title = PickTranslation(data["translations"] as JsonObject, "nameTranslations", "name", tvdbLanguage, ReadString(data["name"]));
            series.Year = ReadPositiveInt(data["year"]);
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
            return series;
        }

        /// <summary>
        /// Episodi delle stagioni scelte nell'ordine ufficiale, con numeri, date e testi dagli elenchi: lingua chiesta, poi inglese, poi originale
        /// </summary>
        /// <param name="series">Serie letta da GetSeriesAsync</param>
        /// <param name="seasonNumbers">Numeri delle stagioni scelte</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Episodi ordinati per stagione e numero, senza persone</returns>
        public override async Task<List<MediaCatalogEpisode>> GetEpisodesAsync(MediaCatalogSeries series, List<int> seasonNumbers, string language, CancellationToken cancellationToken)
        {
            List<MediaCatalogEpisode> result = new List<MediaCatalogEpisode>();
            string id = this.ParseKey(series.Key);
            string tvdbLanguage = await this.ResolveLanguageAsync(language, cancellationToken);
            string listUrl = API_BASE_URL + "/series/" + id + "/episodes/" + SEASON_TYPE;

            // L'elenco in una lingua copre sempre tutta la serie: il filtro per stagione vale solo sull'elenco senza lingua
            foreach (JsonNode item in await this.ReadEpisodeListAsync(listUrl + "/" + tvdbLanguage + "?", cancellationToken))
            {
                MediaCatalogEpisode episode = new MediaCatalogEpisode();
                episode.Id = ReadLong(item["id"]);
                episode.SeasonNumber = (int)(ReadLong(item["seasonNumber"]) ?? 0);
                episode.EpisodeNumber = (int)(ReadLong(item["number"]) ?? 0);
                episode.AbsoluteNumber = ReadPositiveInt(item["absoluteNumber"]);
                episode.Title = ReadString(item["name"]);
                episode.Overview = ReadString(item["overview"]);
                episode.AiredDate = ReadDate(item["aired"]);
                if (seasonNumbers.Contains(episode.SeasonNumber))
                    result.Add(episode);
            }

            // Dove la lingua chiesta non ha il testo, l'inglese; dove manca anche quello, l'elenco delle stagioni nella lingua originale
            if (tvdbLanguage != FALLBACK_LANGUAGE && result.Exists(episode => episode.Title.Length == 0 || episode.Overview.Length == 0))
                FillMissingTexts(result, await this.ReadEpisodeListAsync(listUrl + "/" + FALLBACK_LANGUAGE + "?", cancellationToken));

            foreach (int seasonNumber in seasonNumbers)
            {
                if (result.Exists(episode => episode.SeasonNumber == seasonNumber && (episode.Title.Length == 0 || episode.Overview.Length == 0)))
                    FillMissingTexts(result, await this.ReadEpisodeListAsync(listUrl + "?season=" + seasonNumber.ToString(CultureInfo.InvariantCulture) + "&", cancellationToken));
            }

            result.Sort((a, b) => a.SeasonNumber != b.SeasonNumber ? a.SeasonNumber.CompareTo(b.SeasonNumber) : a.EpisodeNumber.CompareTo(b.EpisodeNumber));
            return result;
        }

        /// <summary>
        /// Completa sul posto le persone di ogni episodio dall'episodio extended, una richiesta per episodio,
        /// e il numero assoluto dall'ordine assoluto quando un episodio regolare non lo porta
        /// </summary>
        /// <param name="series">Serie letta da GetSeriesAsync</param>
        /// <param name="episodes">Episodi da completare</param>
        /// <param name="language">Ignorato: i testi arrivano già dagli elenchi</param>
        /// <param name="progress">Avanzamento in episodi, può essere null</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        public override async Task CompleteEpisodesAsync(MediaCatalogSeries series, List<MediaCatalogEpisode> episodes, string language, IProgress<MediaCatalogProgress> progress, CancellationToken cancellationToken)
        {
            string id = this.ParseKey(series.Key);
            int completed = 0;

            // L'ordine assoluto non comprende gli speciali, che nell'ordine ufficiale hanno assoluto 0: si legge solo per un episodio regolare senza numero
            if (episodes.Exists(episode => episode.SeasonNumber > 0 && !episode.AbsoluteNumber.HasValue))
            {
                Dictionary<long, int> absoluteNumbers = new Dictionary<long, int>();
                foreach (JsonNode item in await this.ReadEpisodeListAsync(API_BASE_URL + "/series/" + id + "/episodes/absolute?", cancellationToken))
                {
                    int? number = ReadPositiveInt(item["number"]);
                    if (number.HasValue)
                        absoluteNumbers[ReadLong(item["id"]).Value] = number.Value;
                }

                foreach (MediaCatalogEpisode episode in episodes)
                {
                    int absolute;
                    if (!episode.AbsoluteNumber.HasValue && absoluteNumbers.TryGetValue(episode.Id.Value, out absolute))
                        episode.AbsoluteNumber = absolute;
                }
            }

            if (progress != null)
                progress.Report(new MediaCatalogProgress(0, episodes.Count));

            // Un episodio per volta, per non caricare il servizio
            foreach (MediaCatalogEpisode episode in episodes)
            {
                JsonNode root = await this.GetJsonAsync(API_BASE_URL + "/episodes/" + episode.Id.Value.ToString(CultureInfo.InvariantCulture) + "/extended", cancellationToken);
                episode.Directors = new List<string>();
                episode.Writers = new List<string>();
                episode.Actors = new List<string>();
                ReadPeople(root["data"]?["characters"] as JsonArray, episode.Directors, episode.Writers, episode.Actors);
                completed++;
                if (progress != null)
                    progress.Report(new MediaCatalogProgress(completed, episodes.Count));
            }
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
            JsonObject translations = data["translations"] as JsonObject;
            movie.Key = key;
            movie.Id = ReadLong(data["id"]);
            movie.Title = PickTranslation(translations, "nameTranslations", "name", tvdbLanguage, ReadString(data["name"]));
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
                throw new MediaCatalogException(AppText.F("mediaCatalog.error.notConfigured", this.DisplayName));

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
        /// Legge tutte le pagine di un elenco episodi, fino alla pagina che non ne indica una successiva
        /// </summary>
        /// <param name="url">Indirizzo dell'elenco che termina con «?» o «&», a cui si aggiunge il numero di pagina</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Episodi base con ID</returns>
        private async Task<List<JsonNode>> ReadEpisodeListAsync(string url, CancellationToken cancellationToken)
        {
            List<JsonNode> result = new List<JsonNode>();
            int page = 0;

            while (true)
            {
                JsonNode root = await this.GetJsonAsync(url + "page=" + page.ToString(CultureInfo.InvariantCulture), cancellationToken);
                JsonArray episodes = root["data"]?["episodes"] as JsonArray;
                if (episodes == null || episodes.Count == 0)
                    return result;

                foreach (JsonNode episode in episodes)
                {
                    if (episode != null && ReadLong(episode["id"]).HasValue)
                        result.Add(episode);
                }

                if (ReadString(root["links"]?["next"]).Length == 0)
                    return result;

                page++;
            }
        }

        /// <summary>
        /// Completa titoli e sinossi vuoti con quelli di un altro elenco, per ID episodio
        /// </summary>
        /// <param name="episodes">Episodi da completare</param>
        /// <param name="list">Elenco episodi base</param>
        private static void FillMissingTexts(List<MediaCatalogEpisode> episodes, List<JsonNode> list)
        {
            foreach (JsonNode item in list)
            {
                long? id = ReadLong(item["id"]);
                MediaCatalogEpisode episode = episodes.Find(e => e.Id == id);
                if (episode == null)
                    continue;

                if (episode.Title.Length == 0)
                    episode.Title = ReadString(item["name"]);
                if (episode.Overview.Length == 0)
                    episode.Overview = ReadString(item["overview"]);
            }
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
                throw new MediaCatalogException(AppText.F("mediaCatalog.error.notFound", this.DisplayName));

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
