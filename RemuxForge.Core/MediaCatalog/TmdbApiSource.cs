using RemuxForge.Core.Configuration;
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
    /// Fonte The Movie Database tramite API v3, con la credenziale dell'utente: API Read Access Token oppure API key
    /// </summary>
    public class TmdbApiSource : MediaCatalogApiSourceBase
    {
        #region Costanti

        /// <summary>
        /// Indirizzo base dell'API v3
        /// </summary>
        private const string API_BASE_URL = "https://api.themoviedb.org/3";

        /// <summary>
        /// Indirizzo base dei poster alla dimensione originale
        /// </summary>
        private const string IMAGE_BASE_URL = "https://image.tmdb.org/t/p/original";

        /// <summary>
        /// Lingua di ripiego dei testi
        /// </summary>
        private const string FALLBACK_LANGUAGE = "en";

        /// <summary>
        /// Risultati massimi di una ricerca
        /// </summary>
        private const int SEARCH_LIMIT = 20;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Legge la credenziale corrente dalle impostazioni
        /// </summary>
        private readonly Func<string> _credentialProvider;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="credentialProvider">Lettura della credenziale TMDB dell'utente dalle impostazioni</param>
        public TmdbApiSource(Func<string> credentialProvider)
        {
            this._credentialProvider = credentialProvider;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Catalogo servito dalla fonte
        /// </summary>
        public override MediaCatalogProvider Provider { get { return MediaCatalogProvider.Tmdb; } }

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
            bool movie = kind == MediaCatalogKind.Movie;
            string url = API_BASE_URL + (movie ? "/search/movie" : "/search/tv") + "?query=" + Uri.EscapeDataString(query ?? "") + "&include_adult=false&language=" + ToTmdbLanguage(language);
            if (year.HasValue)
                url += (movie ? "&primary_release_year=" : "&first_air_date_year=") + year.Value.ToString(CultureInfo.InvariantCulture);

            JsonNode root = await this.GetJsonAsync(url, cancellationToken);
            if (root["results"] is not JsonArray results)
                return result;

            foreach (JsonNode item in results)
            {
                long? id = item != null ? ReadLong(item["id"]) : null;
                if (!id.HasValue)
                    continue;

                MediaCatalogSearchHit hit = new MediaCatalogSearchHit();
                hit.Key = id.Value.ToString(CultureInfo.InvariantCulture);
                hit.Id = id;
                hit.Kind = kind;
                hit.Title = ReadString(item[movie ? "title" : "name"]);
                string original = ReadString(item[movie ? "original_title" : "original_name"]);
                hit.OriginalTitle = original == hit.Title ? "" : original;
                hit.Year = ReadDate(item[movie ? "release_date" : "first_air_date"])?.Year;
                hit.Overview = ReadString(item["overview"]);
                hit.PosterUrl = ToImageUrl(ReadString(item["poster_path"]));
                result.Add(hit);
                if (result.Count == SEARCH_LIMIT)
                    break;
            }

            return result;
        }

        /// <summary>
        /// Serie completa nell'ordine di messa in onda, con testi e persone di ogni episodio
        /// </summary>
        /// <param name="key">ID TMDB della serie</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="progress">Avanzamento in episodi, può essere null</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Serie</returns>
        public override async Task<MediaCatalogSeries> GetSeriesAsync(string key, string language, IProgress<MediaCatalogProgress> progress, CancellationToken cancellationToken)
        {
            string id = this.ParseKey(key);
            string tmdbLanguage = ToTmdbLanguage(language);
            JsonNode data = await this.GetJsonAsync(API_BASE_URL + "/tv/" + id + "?language=" + tmdbLanguage + "&append_to_response=translations", cancellationToken);

            MediaCatalogSeries series = new MediaCatalogSeries();
            string original = ReadString(data["original_name"]);
            series.Key = key;
            series.Id = ReadLong(data["id"]);
            series.Title = PickText(ReadString(data["name"]), data, "name", original);
            series.OriginalTitle = series.Title == original ? "" : original;
            series.Year = ReadDate(data["first_air_date"])?.Year;
            series.Overview = PickText(ReadString(data["overview"]), data, "overview", "");
            series.PosterUrl = ToImageUrl(ReadString(data["poster_path"]));
            ReadGenres(data["genres"] as JsonArray, series.Genres);

            int total = 0;
            HashSet<int> seasonNumbers = new HashSet<int>();
            if (data["seasons"] is JsonArray seasons)
            {
                foreach (JsonNode season in seasons)
                {
                    long? number = season != null ? ReadLong(season["season_number"]) : null;
                    if (!number.HasValue || number.Value < 0 || !seasonNumbers.Add((int)number.Value))
                        continue;

                    MediaCatalogSeason item = new MediaCatalogSeason();
                    item.Number = (int)number.Value;
                    item.PosterUrl = ToImageUrl(ReadString(season["poster_path"]));
                    series.Seasons.Add(item);
                    total += (int)(ReadLong(season["episode_count"]) ?? 0);
                }
            }
            series.Seasons.Sort((a, b) => a.Number.CompareTo(b.Number));

            List<MediaCatalogEpisode>[] seasonEpisodes = new List<MediaCatalogEpisode>[series.Seasons.Count];
            int completed = 0;
            if (progress != null)
                progress.Report(new MediaCatalogProgress(0, total));

            await Parallel.ForEachAsync(Enumerable.Range(0, series.Seasons.Count), new ParallelOptions { MaxDegreeOfParallelism = EPISODE_PARALLELISM, CancellationToken = cancellationToken }, async (index, token) =>
            {
                seasonEpisodes[index] = await this.ReadSeasonAsync(id, series.Seasons[index].Number, tmdbLanguage, token);
                int done = Interlocked.Add(ref completed, seasonEpisodes[index].Count);
                if (progress != null)
                    progress.Report(new MediaCatalogProgress(done, Math.Max(done, total)));
            });

            foreach (List<MediaCatalogEpisode> episodes in seasonEpisodes)
            {
                series.Episodes.AddRange(episodes);
            }
            series.Episodes.Sort((a, b) => a.SeasonNumber != b.SeasonNumber ? a.SeasonNumber.CompareTo(b.SeasonNumber) : a.EpisodeNumber.CompareTo(b.EpisodeNumber));
            return series;
        }

        /// <summary>
        /// Film completo
        /// </summary>
        /// <param name="key">ID TMDB del film</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Film</returns>
        public override async Task<MediaCatalogMovie> GetMovieAsync(string key, string language, CancellationToken cancellationToken)
        {
            string id = this.ParseKey(key);
            JsonNode data = await this.GetJsonAsync(API_BASE_URL + "/movie/" + id + "?language=" + ToTmdbLanguage(language) + "&append_to_response=credits,translations", cancellationToken);

            MediaCatalogMovie movie = new MediaCatalogMovie();
            string original = ReadString(data["original_title"]);
            movie.Key = key;
            movie.Id = ReadLong(data["id"]);
            movie.Title = PickText(ReadString(data["title"]), data, "title", original);
            movie.OriginalTitle = movie.Title == original ? "" : original;
            movie.ReleaseDate = ReadDate(data["release_date"]);
            movie.Year = movie.ReleaseDate?.Year;
            movie.Overview = PickText(ReadString(data["overview"]), data, "overview", "");
            movie.PosterUrl = ToImageUrl(ReadString(data["poster_path"]));
            ReadGenres(data["genres"] as JsonArray, movie.Genres);
            ReadCrew(data["credits"]?["crew"] as JsonArray, movie.Directors, movie.Writers);
            ReadCast(data["credits"]?["cast"] as JsonArray, movie.Actors);
            return movie;
        }

        #endregion

        #region Metodi protected

        /// <summary>
        /// La credenziale TMDB non scade: un 401 è una credenziale sbagliata
        /// </summary>
        protected override bool CanRenewAuthorization { get { return false; } }

        /// <summary>
        /// Mette la credenziale sulla richiesta: il Read Access Token (JWT) va nell'header Bearer, l'API key nel parametro api_key
        /// </summary>
        /// <param name="request">Richiesta da autorizzare</param>
        /// <param name="renew">Ignorato: la credenziale non si rinnova</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        protected override Task AuthorizeAsync(HttpRequestMessage request, bool renew, CancellationToken cancellationToken)
        {
            string credential = (this._credentialProvider() ?? "").Trim();
            if (credential.Length == 0)
                throw new MediaCatalogException(MediaCatalogErrorKind.NotConfigured, AppText.F("mediaCatalog.error.notConfigured", this.DisplayName));

            if (credential.Contains('.'))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            else
                request.RequestUri = new Uri(request.RequestUri.AbsoluteUri + (request.RequestUri.Query.Length > 0 ? "&" : "?") + "api_key=" + Uri.EscapeDataString(credential));

            return Task.CompletedTask;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Episodi di una stagione con testi, troupe, cast regolare della stagione e guest star; i testi mancanti si completano in inglese
        /// </summary>
        /// <param name="id">ID della serie</param>
        /// <param name="seasonNumber">Numero della stagione</param>
        /// <param name="tmdbLanguage">Codice lingua TMDB</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Episodi della stagione</returns>
        private async Task<List<MediaCatalogEpisode>> ReadSeasonAsync(string id, int seasonNumber, string tmdbLanguage, CancellationToken cancellationToken)
        {
            List<MediaCatalogEpisode> result = new List<MediaCatalogEpisode>();
            string seasonUrl = API_BASE_URL + "/tv/" + id + "/season/" + seasonNumber.ToString(CultureInfo.InvariantCulture);
            JsonNode data = await this.GetJsonOrNullAsync(seasonUrl + "?language=" + tmdbLanguage + "&append_to_response=credits", cancellationToken);
            if (data == null || data["episodes"] is not JsonArray episodes)
                return result;

            // Il cast regolare è della stagione; ogni episodio aggiunge le sue guest star
            List<string> regularCast = new List<string>();
            ReadCast(data["credits"]?["cast"] as JsonArray, regularCast);

            bool missingText = false;
            foreach (JsonNode item in episodes)
            {
                if (item == null)
                    continue;

                MediaCatalogEpisode episode = new MediaCatalogEpisode();
                episode.Id = ReadLong(item["id"]);
                episode.SeasonNumber = seasonNumber;
                episode.EpisodeNumber = (int)(ReadLong(item["episode_number"]) ?? 0);
                episode.Title = ReadString(item["name"]);
                episode.Overview = ReadString(item["overview"]);
                episode.AiredDate = ReadDate(item["air_date"]);
                episode.RuntimeMinutes = ReadPositiveInt(item["runtime"]);
                ReadCrew(item["crew"] as JsonArray, episode.Directors, episode.Writers);
                episode.Actors.AddRange(regularCast);
                ReadCast(item["guest_stars"] as JsonArray, episode.Actors);
                missingText |= episode.Title.Length == 0 || episode.Overview.Length == 0;
                result.Add(episode);
            }

            if (missingText && tmdbLanguage != FALLBACK_LANGUAGE)
            {
                JsonNode english = await this.GetJsonOrNullAsync(seasonUrl + "?language=" + FALLBACK_LANGUAGE, cancellationToken);
                if (english != null && english["episodes"] is JsonArray englishEpisodes)
                {
                    foreach (JsonNode item in englishEpisodes)
                    {
                        long? episodeId = item != null ? ReadLong(item["id"]) : null;
                        MediaCatalogEpisode episode = episodeId.HasValue ? result.Find(e => e.Id == episodeId) : null;
                        if (episode == null)
                            continue;

                        if (episode.Title.Length == 0)
                            episode.Title = ReadString(item["name"]);
                        if (episode.Overview.Length == 0)
                            episode.Overview = ReadString(item["overview"]);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Chiave TMDB come ID numerico per i percorsi
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
        /// Converte la lingua dell'app nel codice ISO 639-1 di TMDB; inglese se non ne ha uno
        /// </summary>
        /// <param name="language">Codice ISO 639-2/T</param>
        /// <returns>Codice lingua TMDB</returns>
        private static string ToTmdbLanguage(string language)
        {
            string code = LanguageValidator.ToIso6391(language);
            return code.Length > 0 ? code : FALLBACK_LANGUAGE;
        }

        /// <summary>
        /// Testo nella lingua chiesta, poi in inglese dalle traduzioni del record, poi il valore originale
        /// </summary>
        /// <param name="requested">Valore restituito nella lingua chiesta</param>
        /// <param name="record">Record con append_to_response=translations</param>
        /// <param name="field">Campo nei dati della traduzione: name, title o overview</param>
        /// <param name="original">Valore nella lingua originale</param>
        /// <returns>Testo</returns>
        private static string PickText(string requested, JsonNode record, string field, string original)
        {
            if (requested.Length > 0)
                return requested;

            if (record["translations"]?["translations"] is JsonArray translations)
            {
                foreach (JsonNode translation in translations)
                {
                    if (translation != null && ReadString(translation["iso_639_1"]) == FALLBACK_LANGUAGE)
                    {
                        string text = ReadString(translation["data"]?[field]);
                        if (text.Length > 0)
                            return text;
                    }
                }
            }

            return original;
        }

        /// <summary>
        /// Nomi dei generi
        /// </summary>
        /// <param name="genres">Array genres</param>
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
        /// Registi (job Director) e sceneggiatori (dipartimento Writing) da una troupe
        /// </summary>
        /// <param name="crew">Array crew</param>
        /// <param name="directors">Registi</param>
        /// <param name="writers">Sceneggiatori</param>
        private static void ReadCrew(JsonArray crew, List<string> directors, List<string> writers)
        {
            if (crew == null)
                return;

            foreach (JsonNode member in crew)
            {
                if (member == null)
                    continue;

                string name = ReadString(member["name"]);
                if (ReadString(member["job"]) == "Director")
                    AddDistinct(directors, name);
                else if (ReadString(member["department"]) == "Writing")
                    AddDistinct(writers, name);
            }
        }

        /// <summary>
        /// Attori di un cast nell'ordine dei crediti, aggiunti in coda a quelli già presenti
        /// </summary>
        /// <param name="cast">Array cast o guest_stars</param>
        /// <param name="actors">Attori</param>
        private static void ReadCast(JsonArray cast, List<string> actors)
        {
            if (cast == null)
                return;

            List<JsonNode> sorted = new List<JsonNode>();
            foreach (JsonNode member in cast)
            {
                if (member != null)
                    sorted.Add(member);
            }
            sorted.Sort((a, b) => (ReadLong(a["order"]) ?? long.MaxValue).CompareTo(ReadLong(b["order"]) ?? long.MaxValue));

            foreach (JsonNode member in sorted)
            {
                AddDistinct(actors, ReadString(member["name"]));
            }
        }

        /// <summary>
        /// URL assoluto di un'immagine TMDB, vuoto se assente
        /// </summary>
        /// <param name="path">Percorso restituito dall'API</param>
        /// <returns>URL assoluto</returns>
        private static string ToImageUrl(string path)
        {
            return path.Length > 0 ? IMAGE_BASE_URL + path : "";
        }

        #endregion
    }
}
