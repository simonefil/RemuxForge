using HtmlAgilityPack;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.MediaCatalog
{
    /// <summary>
    /// Fonte del sito pubblico TVDB per IMediaCatalogSource, senza API key
    /// </summary>
    public class TvdbScraperSource : IMediaCatalogSource
    {
        #region Costanti

        /// <summary>
        /// Nome del catalogo nei messaggi all'utente
        /// </summary>
        private static readonly string DISPLAY_NAME = MediaCatalogProviderNames.GetCatalogName(MediaCatalogProvider.Tvdb);

        /// <summary>
        /// Indirizzo della ricerca web del sito
        /// </summary>
        private const string SEARCH_URL = "https://api4.thetvdb.com/web/search/queries";

        /// <summary>
        /// Risultati massimi di una ricerca
        /// </summary>
        private const int SEARCH_LIMIT = 20;

        /// <summary>
        /// Lingua di ripiego dei testi e delle lingue non valide
        /// </summary>
        private const string FALLBACK_LANGUAGE = "eng";

        /// <summary>
        /// Invii di una richiesta, il primo compreso, su errori transitori
        /// </summary>
        private const int MAX_ATTEMPTS = 3;

        /// <summary>
        /// Tempo massimo di un invio, e attesa massima chiesta dal sito con Retry-After
        /// </summary>
        private static readonly TimeSpan REQUEST_TIMEOUT = TimeSpan.FromSeconds(45);

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Client HTTP della fonte; i dati delle singole operazioni rimangono locali ai metodi
        /// </summary>
        private readonly HttpClient _httpClient;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TvdbScraperSource()
        {
            this._httpClient = MediaCatalogHttpClientFactory.Create(REQUEST_TIMEOUT);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Cerca fino a venti titoli nell'ordine di rilevanza del sito; un risultato senza un indirizzo valido si salta
        /// </summary>
        /// <param name="kind">Serie oppure film</param>
        /// <param name="query">Titolo da cercare</param>
        /// <param name="year">Anno richiesto, oppure null</param>
        /// <param name="language">Codice ISO 639-2/T</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Risultati del contratto</returns>
        public async Task<List<MediaCatalogSearchHit>> SearchAsync(MediaCatalogKind kind, string query, int? year, string language, CancellationToken cancellationToken)
        {
            try
            {
                List<MediaCatalogSearchHit> results;
                string type;
                string filters;
                string body;
                string json;

                cancellationToken.ThrowIfCancellationRequested();
                language = ResolveLanguage(language);
                if (string.IsNullOrWhiteSpace(query))
                    throw CreateFormatError("query");

                type = kind == MediaCatalogKind.Movie ? "movie" : "series";
                filters = "type:" + type + " AND NOT is_official=0";
                if (year.HasValue)
                    filters += " AND year:" + year.Value.ToString(CultureInfo.InvariantCulture);

                body = JsonSerializer.Serialize(new
                {
                    requests = new[]
                    {
                        new { indexName = "TVDB", @params = new { query, page = 0, hitsPerPage = SEARCH_LIMIT, filters } }
                    }
                });
                json = await this.RequestAsync(new Uri(SEARCH_URL), body, cancellationToken);
                results = new List<MediaCatalogSearchHit>();

                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    foreach (JsonElement hit in document.RootElement.GetProperty("results")[0].GetProperty("hits").EnumerateArray())
                    {
                        MediaCatalogSearchHit item;
                        string key;
                        string originalLanguage;
                        string originalTitle;

                        cancellationToken.ThrowIfCancellationRequested();
                        if (JsonString(hit, "type") != type)
                            continue;

                        try
                        {
                            key = PageUrl(JsonString(hit, "url"), kind == MediaCatalogKind.Series ? "series" : "movies").AbsoluteUri;
                        }
                        catch (MediaCatalogException)
                        {
                            continue;
                        }

                        originalLanguage = JsonString(hit, "primary_language");
                        originalTitle = JsonTranslation(hit, "translations", originalLanguage);
                        if (string.IsNullOrEmpty(originalTitle))
                            originalTitle = JsonString(hit, "name");

                        item = new MediaCatalogSearchHit
                        {
                            Key = key,
                            Kind = kind,
                            Title = SearchText(hit, "translations", language, originalLanguage, originalTitle),
                            Overview = SearchText(hit, "overviews", language, originalLanguage, JsonString(hit, "overview")),
                            Year = ParseInteger(JsonString(hit, "year"))
                        };
                        item.OriginalTitle = originalTitle == item.Title ? string.Empty : originalTitle;

                        if (year.HasValue && item.Year != year)
                            continue;

                        results.Add(item);
                        if (results.Count == SEARCH_LIMIT)
                            break;
                    }
                }

                return results;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw CatalogError(ex);
            }
        }

        /// <summary>
        /// Carica la scheda della serie con l'elenco delle stagioni ufficiali e il numero di episodi di ognuna
        /// </summary>
        /// <param name="key">URL pubblico restituito dalla ricerca</param>
        /// <param name="language">Codice ISO 639-2/T</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Serie con stagioni ordinate per numero; i poster delle stagioni arrivano con gli episodi</returns>
        public async Task<MediaCatalogSeries> GetSeriesAsync(string key, string language, CancellationToken cancellationToken)
        {
            try
            {
                Uri url;
                HtmlNode root;
                MediaCatalogSeries series;

                cancellationToken.ThrowIfCancellationRequested();
                language = ResolveLanguage(language);
                url = PageUrl(key, "series");
                root = await this.LoadPageAsync(url, cancellationToken);
                if (root.SelectSingleNode("//div[@id='seasons']") == null)
                    throw CreateFormatError("seasons");

                series = new MediaCatalogSeries
                {
                    Key = url.AbsoluteUri,
                    Id = ParseLong(FieldText(root, "TheTVDB.com Series ID")),
                    Year = ParseDate(FieldText(root, "First Aired"))?.Year,
                    Genres = FieldValues(root, "Genres").Select(Text).ToList(),
                    PosterUrl = Poster(root)
                };
                ReadText(root, language, out string title, out _);
                series.Title = title;

                // Riga della tabella: stagione, prima e ultima messa in onda, episodi
                foreach (HtmlNode row in Nodes(root, "//div[@id='seasons-official']//tr[td[1]/a[contains(@href,'/seasons/official/')]]"))
                {
                    Match number;

                    number = Regex.Match(new Uri(url, row.SelectSingleNode("./td[1]/a").GetAttributeValue("href", "")).AbsolutePath, @"/seasons/official/(\d+)/?$");
                    if (number.Success)
                        series.Seasons.Add(new MediaCatalogSeason { Number = int.Parse(number.Groups[1].Value, CultureInfo.InvariantCulture), EpisodeCount = ParseInteger(Text(row.SelectSingleNode("./td[4]"))) });
                }

                series.Seasons = series.Seasons.OrderBy(s => s.Number).ToList();
                return series;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw CatalogError(ex);
            }
        }

        /// <summary>
        /// Carica le pagine delle stagioni scelte, ne completa il poster sulla serie e ne legge gli episodi:
        /// il sito mostra i titoli nella lingua originale, testi tradotti e persone arrivano con CompleteEpisodesAsync
        /// </summary>
        /// <param name="series">Serie letta da GetSeriesAsync</param>
        /// <param name="seasonNumbers">Numeri delle stagioni scelte</param>
        /// <param name="language">Codice ISO 639-2/T</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Episodi ordinati per stagione e numero</returns>
        public async Task<List<MediaCatalogEpisode>> GetEpisodesAsync(MediaCatalogSeries series, List<int> seasonNumbers, string language, CancellationToken cancellationToken)
        {
            try
            {
                Uri url;
                List<MediaCatalogEpisode> result;

                cancellationToken.ThrowIfCancellationRequested();
                url = PageUrl(series.Key, "series");
                result = new List<MediaCatalogEpisode>();

                foreach (int seasonNumber in seasonNumbers)
                {
                    HtmlNode season;

                    season = await this.LoadPageAsync(PageUrl(url.AbsoluteUri + "/seasons/official/" + seasonNumber.ToString(CultureInfo.InvariantCulture), "series", @"/seasons/official/\d+"), cancellationToken);
                    if (season.SelectSingleNode("//div[@id='episodes']") == null)
                        throw CreateFormatError("episodes");

                    foreach (MediaCatalogSeason item in series.Seasons.Where(s => s.Number == seasonNumber))
                        item.PosterUrl = Poster(season);

                    // Riga della tabella: SxxEyy, titolo, prima messa in onda con l'emittente, durata
                    foreach (HtmlNode row in Nodes(season, "//div[@id='episodes']//tr[td[2]/a[contains(@href,'/episodes/')]]"))
                    {
                        Match label;

                        label = Regex.Match(Text(row.SelectSingleNode("./td[1]")), @"^S(\d+)E(\d+)$");
                        if (!label.Success)
                            throw CreateFormatError("episode.number");

                        result.Add(new MediaCatalogEpisode
                        {
                            Id = ParseLong(PageUrl(new Uri(url, row.SelectSingleNode("./td[2]/a").GetAttributeValue("href", "")).AbsoluteUri, "series", @"/episodes/\d+").Segments.Last()),
                            SeasonNumber = int.Parse(label.Groups[1].Value, CultureInfo.InvariantCulture),
                            EpisodeNumber = int.Parse(label.Groups[2].Value, CultureInfo.InvariantCulture),
                            Title = Text(row.SelectSingleNode("./td[2]/a")),
                            AiredDate = ParseDate(Text(row.SelectSingleNode("./td[3]/div[1]")))
                        });
                    }
                }

                return result.OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw CatalogError(ex);
            }
        }

        /// <summary>
        /// Completa sul posto testi, data, durata, numero assoluto e persone di ogni episodio dalla sua pagina, una per volta
        /// </summary>
        /// <param name="series">Serie letta da GetSeriesAsync</param>
        /// <param name="episodes">Episodi letti da GetEpisodesAsync</param>
        /// <param name="language">Codice ISO 639-2/T</param>
        /// <param name="progress">Avanzamento facoltativo in episodi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        public async Task CompleteEpisodesAsync(MediaCatalogSeries series, List<MediaCatalogEpisode> episodes, string language, IProgress<MediaCatalogProgress> progress, CancellationToken cancellationToken)
        {
            try
            {
                Uri url;
                string originalLanguage;
                int completed;

                cancellationToken.ThrowIfCancellationRequested();
                language = ResolveLanguage(language);
                url = PageUrl(series.Key, "series");

                // Le pagine degli episodi non dichiarano la lingua originale: la dichiara solo la scheda della serie
                originalLanguage = OriginalLanguage(await this.LoadPageAsync(url, cancellationToken));
                completed = 0;
                progress?.Report(new MediaCatalogProgress(0, episodes.Count));
                foreach (MediaCatalogEpisode episode in episodes)
                {
                    HtmlNode root;

                    root = await this.LoadPageAsync(new Uri(url.AbsoluteUri + "/episodes/" + episode.Id.Value.ToString(CultureInfo.InvariantCulture)), cancellationToken);
                    episode.AiredDate = ParseDate(FieldText(root, "Originally Aired"));
                    episode.Directors = People(root, "director");
                    episode.Writers = People(root, "writer");
                    episode.Actors = People(root, "actor");
                    ReadText(root, language, out string title, out string overview, originalLanguage);
                    episode.Title = title;
                    episode.Overview = overview;

                    foreach (HtmlNode crumbs in Nodes(root, "//div[contains(concat(' ',normalize-space(@class),' '),' crumbs ')][a[contains(@href,'/seasons/absolute/')]]"))
                    {
                        Match absolute;

                        absolute = Regex.Match(Text(crumbs), @"\bEpisode\s+(\d+)\b");
                        if (absolute.Success)
                            episode.AbsoluteNumber = ParseInteger(absolute.Groups[1].Value);
                    }

                    completed++;
                    progress?.Report(new MediaCatalogProgress(completed, episodes.Count));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw CatalogError(ex);
            }
        }

        /// <summary>
        /// Carica il film limitandosi ai dati del contratto
        /// </summary>
        /// <param name="key">URL pubblico restituito dalla ricerca</param>
        /// <param name="language">Codice ISO 639-2/T</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Film con prima uscita, generi e persone nei crediti</returns>
        public async Task<MediaCatalogMovie> GetMovieAsync(string key, string language, CancellationToken cancellationToken)
        {
            try
            {
                Uri url;
                HtmlNode root;
                MediaCatalogMovie movie;

                cancellationToken.ThrowIfCancellationRequested();
                language = ResolveLanguage(language);
                url = PageUrl(key, "movies");
                root = await this.LoadPageAsync(url, cancellationToken);
                if (root.SelectSingleNode("//div[@id='series_basic_info']") == null)
                    throw CreateFormatError("movie");

                movie = new MediaCatalogMovie
                {
                    Key = url.AbsoluteUri,
                    Id = ParseLong(FieldText(root, "TheTVDB.com Movie ID")),
                    Genres = FieldValues(root, "Genres").Select(Text).ToList(),
                    PosterUrl = Poster(root),
                    Directors = People(root, "director"),
                    Writers = People(root, "writer"),
                    Actors = People(root, "actor")
                };
                ReadText(root, language, out string title, out string overview);
                movie.Title = title;
                movie.Overview = overview;

                foreach (HtmlNode release in FieldValues(root, "Released"))
                {
                    HtmlNode copy;
                    DateTime? date;

                    copy = release.CloneNode(true);
                    foreach (HtmlNode country in Nodes(copy, ".//small").ToArray())
                        country.Remove();

                    date = ParseDate(Text(copy));
                    if (date.HasValue && (!movie.ReleaseDate.HasValue || date < movie.ReleaseDate))
                        movie.ReleaseDate = date;
                }

                movie.Year = movie.ReleaseDate?.Year;
                return movie;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw CatalogError(ex);
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Legge una pagina riconoscibile senza conservare stato condiviso
        /// </summary>
        /// <param name="url">URL già validato</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Radice del documento HTML</returns>
        private async Task<HtmlNode> LoadPageAsync(Uri url, CancellationToken cancellationToken)
        {
            HtmlDocument document;

            document = new HtmlDocument();
            document.LoadHtml(await this.RequestAsync(url, null, cancellationToken));
            if (document.DocumentNode.SelectSingleNode("//h1") == null || document.DocumentNode.SelectSingleNode("//div[@id='general']") == null)
                throw CreateFormatError("page");

            return document.DocumentNode;
        }

        /// <summary>
        /// Esegue GET o POST con timeout e due nuovi tentativi per errori transitori
        /// </summary>
        /// <param name="url">URL della richiesta</param>
        /// <param name="body">JSON POST, oppure null per GET</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Contenuto testuale della risposta</returns>
        private async Task<string> RequestAsync(Uri url, string body, CancellationToken cancellationToken)
        {
            for (int attempt = 0; ; attempt++)
            {
                using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(REQUEST_TIMEOUT);
                    try
                    {
                        using (HttpRequestMessage request = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, url))
                        {
                            if (body != null)
                                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                            using (HttpResponseMessage response = await this._httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                            {
                                if (response.IsSuccessStatusCode)
                                    return await response.Content.ReadAsStringAsync(timeout.Token);

                                if (attempt < MAX_ATTEMPTS - 1 && ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout))
                                {
                                    TimeSpan delay;

                                    delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(attempt + 1);
                                    if (delay >= TimeSpan.Zero && delay <= REQUEST_TIMEOUT)
                                    {
                                        await Task.Delay(delay, cancellationToken);
                                        continue;
                                    }
                                }

                                if (response.StatusCode == HttpStatusCode.NotFound)
                                    throw new MediaCatalogException(AppText.F("mediaCatalog.error.notFound", DISPLAY_NAME));

                                throw new MediaCatalogException(AppText.F("mediaCatalog.error.service", DISPLAY_NAME, (int)response.StatusCode));
                            }
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw;

                        if (attempt == MAX_ATTEMPTS - 1)
                            throw new MediaCatalogException(AppText.F("mediaCatalog.error.network", DISPLAY_NAME, ex.Message), ex);
                    }
                    catch (HttpRequestException)
                    {
                        if (attempt == MAX_ATTEMPTS - 1)
                            throw;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken);
            }
        }

        /// <summary>
        /// Seleziona ogni testo nella lingua richiesta, altrimenti inglese, altrimenti originale
        /// </summary>
        /// <param name="root">Radice della scheda</param>
        /// <param name="language">Lingua richiesta</param>
        /// <param name="title">Titolo selezionato, quello originale se non ce n'è uno tradotto</param>
        /// <param name="overview">Overview selezionata</param>
        /// <param name="originalLanguage">Lingua della serie per pagine episodio che non la dichiarano</param>
        private static void ReadText(HtmlNode root, string language, out string title, out string overview, string originalLanguage = "")
        {
            string originalTitle;

            if (string.IsNullOrEmpty(originalLanguage))
                originalLanguage = OriginalLanguage(root);

            title = string.Empty;
            overview = string.Empty;
            originalTitle = Text(root.SelectSingleNode("//h1"));
            foreach (HtmlNode translation in Nodes(root, "//div[@data-language and @data-title]"))
            {
                if (translation.GetAttributeValue("data-language", "") == originalLanguage)
                    originalTitle = WebUtility.HtmlDecode(translation.GetAttributeValue("data-title", originalTitle));
            }

            foreach (string code in new string[] { language, FALLBACK_LANGUAGE, originalLanguage }.Distinct())
            {
                HtmlNode translation;

                if (string.IsNullOrEmpty(code))
                    continue;

                translation = root.SelectSingleNode("//div[@data-language='" + code + "' and contains(concat(' ',normalize-space(@class),' '),' change_translation_text ')]");
                if (translation == null)
                    continue;

                if (string.IsNullOrEmpty(title))
                    title = WebUtility.HtmlDecode(translation.GetAttributeValue("data-title", ""));

                if (string.IsNullOrEmpty(overview))
                    overview = string.Join("\n\n", Nodes(translation, ".//p").Select(Text).Where(text => !string.IsNullOrEmpty(text)));
            }

            if (string.IsNullOrEmpty(title))
                title = originalTitle;
        }

        /// <summary>
        /// Ricava il codice della lingua originale dalle opzioni di traduzione della pagina
        /// </summary>
        /// <param name="root">Radice della scheda</param>
        /// <returns>Codice TVDB oppure stringa vuota</returns>
        private static string OriginalLanguage(HtmlNode root)
        {
            string label;

            label = FieldText(root, "Original Language");
            foreach (HtmlNode option in Nodes(root, "//span[@data-language and @title]"))
            {
                if (option.GetAttributeValue("title", "").Equals(label, StringComparison.OrdinalIgnoreCase))
                    return option.GetAttributeValue("data-language", "");
            }

            return string.Empty;
        }

        /// <summary>
        /// Applica alla ricerca lo stesso ordine di fallback dei testi delle schede
        /// </summary>
        /// <param name="hit">Risultato JSON</param>
        /// <param name="field">Mappa di titoli o overview</param>
        /// <param name="language">Lingua richiesta</param>
        /// <param name="originalLanguage">Lingua originale</param>
        /// <param name="fallback">Testo originale senza codice lingua</param>
        /// <returns>Testo selezionato oppure stringa vuota</returns>
        private static string SearchText(JsonElement hit, string field, string language, string originalLanguage, string fallback)
        {
            foreach (string code in new string[] { language, FALLBACK_LANGUAGE, originalLanguage }.Distinct())
            {
                string value;

                value = JsonTranslation(hit, field, code);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return fallback;
        }

        /// <summary>
        /// Legge una traduzione dal JSON della ricerca web
        /// </summary>
        /// <param name="hit">Risultato JSON</param>
        /// <param name="field">Mappa da leggere</param>
        /// <param name="language">Lingua desiderata</param>
        /// <returns>Traduzione oppure stringa vuota</returns>
        private static string JsonTranslation(JsonElement hit, string field, string language)
        {
            if (hit.TryGetProperty(field, out JsonElement values) && values.ValueKind == JsonValueKind.Object)
            {
                string value;

                value = JsonString(values, language);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            if (field == "overviews" && hit.TryGetProperty("overview_translated", out JsonElement overviews) && overviews.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in overviews.EnumerateArray())
                {
                    string value;

                    value = entry.GetString() ?? string.Empty;
                    if (value.StartsWith(language + ":", StringComparison.Ordinal))
                        return value.Substring(language.Length + 1).Trim();
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Estrae solo nomi di registi, sceneggiatori o attori nell'ordine dei crediti
        /// </summary>
        /// <param name="root">Radice della pagina</param>
        /// <param name="type">director, writer oppure actor</param>
        /// <returns>Nomi senza duplicati nell'ordine della pagina</returns>
        private static List<string> People(HtmlNode root, string type)
        {
            List<string> names;

            names = new List<string>();
            foreach (HtmlNode name in Nodes(root, "//div[@id='people-" + type + "']//h3 | //div[@id='people-" + type + "']//tr/td[1]/a"))
            {
                HtmlNode copy;
                string value;

                copy = name.CloneNode(true);
                foreach (HtmlNode role in Nodes(copy, ".//small").ToArray())
                    role.Remove();

                value = Text(copy);
                if (!string.IsNullOrEmpty(value) && !names.Contains(value, StringComparer.Ordinal))
                    names.Add(value);
            }

            foreach (HtmlNode row in Nodes(root, "//div[@id='castcrew']/table//tr[td]"))
            {
                string credit;
                string name;

                credit = Text(row.SelectSingleNode("./td[2]")).ToLowerInvariant();
                if (credit != type && !(type == "actor" && (credit == "guest star" || credit == "cast")))
                    continue;

                name = Text(row.SelectSingleNode("./td[1]/a"));
                if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.Ordinal))
                    names.Add(name);
            }

            return names;
        }

        /// <summary>
        /// Valida le chiavi URL della fonte e rimuove query e frammenti
        /// </summary>
        /// <param name="key">URL della risorsa</param>
        /// <param name="section">series oppure movies</param>
        /// <param name="suffix">Percorso interno aggiuntivo ammesso</param>
        /// <returns>URL canonico della pagina</returns>
        private static Uri PageUrl(string key, string section, string suffix = "")
        {
            if (!Uri.TryCreate(key, UriKind.Absolute, out Uri url) || url.Scheme != "https" || !url.IsDefaultPort || !string.IsNullOrEmpty(url.UserInfo) ||
                (url.Host != "thetvdb.com" && url.Host != "www.thetvdb.com") || !Regex.IsMatch(url.AbsolutePath, "^/" + section + "/[^/]+" + suffix + "/?$"))
                throw CreateFormatError("key");

            return new Uri("https://thetvdb.com" + url.AbsolutePath.TrimEnd('/'));
        }

        /// <summary>
        /// Codice ISO 639-2/T dell'app, che il sito TVDB usa così com'è; inglese se non è un codice di tre lettere
        /// </summary>
        /// <param name="language">Lingua dell'applicazione</param>
        /// <returns>Codice lingua normalizzato</returns>
        private static string ResolveLanguage(string language)
        {
            string code = (language ?? "").Trim().ToLowerInvariant();
            return Regex.IsMatch(code, "^[a-z]{3}$") ? code : FALLBACK_LANGUAGE;
        }

        /// <summary>
        /// Legge solo i valori General richiesti dal contratto
        /// </summary>
        /// <param name="root">Radice della pagina</param>
        /// <param name="name">Nome del campo</param>
        /// <returns>Valori nell'ordine della pagina oppure sequenza vuota</returns>
        private static IEnumerable<HtmlNode> FieldValues(HtmlNode root, string name) => Nodes(root, "//div[@id='general']//li[strong[normalize-space(.)='" + name + "']]/span");

        /// <summary>
        /// Legge un campo testuale richiesto dal contratto
        /// </summary>
        /// <param name="root">Radice della pagina</param>
        /// <param name="name">Nome del campo</param>
        /// <returns>Primo valore oppure stringa vuota</returns>
        private static string FieldText(HtmlNode root, string name) => Text(FieldValues(root, name).FirstOrDefault());

        /// <summary>
        /// Legge il poster principale senza estrarre altre immagini
        /// </summary>
        /// <param name="root">Radice della pagina</param>
        /// <returns>URL del poster oppure stringa vuota</returns>
        private static string Poster(HtmlNode root)
        {
            HtmlNode image;

            image = root.SelectSingleNode("//img[contains(concat(' ',normalize-space(@class),' '),' img-mobile-centered ')]");
            return WebUtility.HtmlDecode(image?.GetAttributeValue("data-src", image.GetAttributeValue("src", "")) ?? string.Empty);
        }

        /// <summary>
        /// Converte date complete inglesi o ISO senza inventare giorni per date parziali
        /// </summary>
        /// <param name="text">Testo originale</param>
        /// <returns>Data senza ora oppure null</returns>
        private static DateTime? ParseDate(string text) => DateTime.TryParseExact(text, new string[] { "MMMM d, yyyy", "MMMM dd, yyyy", "yyyy-MM-dd" }, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out DateTime date) ? date.Date : null;

        /// <summary>
        /// Legge un numero iniziale, anche seguito dall'unità di durata
        /// </summary>
        /// <param name="text">Testo originale</param>
        /// <returns>Numero intero oppure null</returns>
        private static int? ParseInteger(string text)
        {
            Match match;

            match = Regex.Match(text, @"^\s*(\d+)(?:\s|$)");
            return match.Success && int.TryParse(match.Groups[1].Value, out int number) ? number : null;
        }

        /// <summary>
        /// Converte un identificatore numerico del catalogo
        /// </summary>
        /// <param name="text">Testo originale</param>
        /// <returns>Identificatore oppure null</returns>
        private static long? ParseLong(string text) => long.TryParse(text, out long number) ? number : null;

        /// <summary>
        /// Legge proprietà JSON testuali o numeriche senza restituire null
        /// </summary>
        /// <param name="element">Oggetto JSON</param>
        /// <param name="name">Nome della proprietà</param>
        /// <returns>Valore testuale oppure stringa vuota</returns>
        private static string JsonString(JsonElement element, string name)
        {
            if (!element.TryGetProperty(name, out JsonElement value))
                return string.Empty;

            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ValueKind == JsonValueKind.Number ? value.ToString() : string.Empty;
        }

        /// <summary>
        /// Valuta XPath restituendo una sequenza vuota in assenza di nodi
        /// </summary>
        /// <param name="root">Nodo di partenza</param>
        /// <param name="xpath">Espressione XPath</param>
        /// <returns>Nodi selezionati</returns>
        private static IEnumerable<HtmlNode> Nodes(HtmlNode root, string xpath) => root.SelectNodes(xpath)?.AsEnumerable() ?? Enumerable.Empty<HtmlNode>();

        /// <summary>
        /// Decodifica entità HTML e normalizza gli spazi
        /// </summary>
        /// <param name="node">Nodo da leggere, anche null</param>
        /// <returns>Testo normalizzato oppure stringa vuota</returns>
        private static string Text(HtmlNode node) => Regex.Replace(WebUtility.HtmlDecode(node?.InnerText ?? string.Empty), @"\s+", " ").Trim();

        /// <summary>
        /// Converte ogni errore non di cancellazione nell'eccezione prevista dal contratto
        /// </summary>
        /// <param name="exception">Errore originale</param>
        /// <returns>Errore classificato della fonte</returns>
        private static MediaCatalogException CatalogError(Exception exception)
        {
            if (exception is MediaCatalogException catalogException)
                return catalogException;

            if (exception is HttpRequestException || exception is System.IO.IOException)
                return new MediaCatalogException(AppText.F("mediaCatalog.error.network", DISPLAY_NAME, exception.Message), exception);

            ConsoleHelper.Write(LogSection.General, LogLevel.Warning, AppText.F("mediaCatalog.error.format", DISPLAY_NAME, exception.Message));
            return new MediaCatalogException(AppText.F("mediaCatalog.error.unreadable", DISPLAY_NAME), exception);
        }

        /// <summary>
        /// Errore di pagina o risposta non interpretabile: il dettaglio va nel log, all'utente il messaggio senza dettaglio
        /// </summary>
        /// <param name="detail">Cosa manca o non torna</param>
        /// <returns>Eccezione da lanciare</returns>
        private static MediaCatalogException CreateFormatError(string detail)
        {
            ConsoleHelper.Write(LogSection.General, LogLevel.Warning, AppText.F("mediaCatalog.error.format", DISPLAY_NAME, detail));
            return new MediaCatalogException(AppText.F("mediaCatalog.error.unreadable", DISPLAY_NAME));
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Catalogo servito dalla fonte
        /// </summary>
        public MediaCatalogProvider Provider => MediaCatalogProvider.Tvdb;

        #endregion

    }
}