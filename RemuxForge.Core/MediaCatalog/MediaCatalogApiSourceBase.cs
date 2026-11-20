using RemuxForge.Core.Ai;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.MediaCatalog
{
    /// <summary>
    /// Base delle fonti che parlano con un catalogo via API JSON: invio con ritentativi, autorizzazione e conversione degli errori
    /// </summary>
    public abstract class MediaCatalogApiSourceBase : IMediaCatalogSource
    {
        #region Costanti

        /// <summary>
        /// Risultati massimi di una ricerca
        /// </summary>
        protected const int SEARCH_LIMIT = 20;

        /// <summary>
        /// Ritentativi dopo il primo invio su limite di frequenza o errore transitorio
        /// </summary>
        private const int MAX_RETRIES = 2;

        /// <summary>
        /// Attesa del primo ritentativo; raddoppia a ogni ritentativo
        /// </summary>
        private static readonly TimeSpan RETRY_BASE_DELAY = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Tetto dell'attesa prima di un ritentativo, chiesta dal servizio con Retry-After o calcolata col backoff
        /// </summary>
        private static readonly TimeSpan MAX_RETRY_DELAY = TimeSpan.FromSeconds(30);

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Client HTTP della fonte
        /// </summary>
        private readonly HttpClient _httpClient;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        protected MediaCatalogApiSourceBase()
        {
            this._httpClient = MediaCatalogHttpClientFactory.Create(TimeSpan.FromSeconds(30));
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Catalogo servito dalla fonte
        /// </summary>
        public abstract MediaCatalogProvider Provider { get; }

        /// <summary>
        /// Cerca per titolo
        /// </summary>
        /// <param name="kind">Serie o film</param>
        /// <param name="query">Testo cercato</param>
        /// <param name="year">Anno di prima uscita, null per non filtrare</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Risultati</returns>
        public abstract Task<List<MediaCatalogSearchHit>> SearchAsync(MediaCatalogKind kind, string query, int? year, string language, CancellationToken cancellationToken);

        /// <summary>
        /// Serie con l'elenco delle stagioni, senza episodi
        /// </summary>
        /// <param name="key">Chiave del risultato di ricerca</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Serie</returns>
        public abstract Task<MediaCatalogSeries> GetSeriesAsync(string key, string language, CancellationToken cancellationToken);

        /// <summary>
        /// Episodi delle stagioni scelte, con quanto gli elenchi delle stagioni portano
        /// </summary>
        /// <param name="series">Serie letta da GetSeriesAsync</param>
        /// <param name="seasonNumbers">Numeri delle stagioni scelte</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Episodi ordinati per stagione e numero</returns>
        public abstract Task<List<MediaCatalogEpisode>> GetEpisodesAsync(MediaCatalogSeries series, List<int> seasonNumbers, string language, CancellationToken cancellationToken);

        /// <summary>
        /// Completa sul posto gli episodi da scrivere
        /// </summary>
        /// <param name="series">Serie letta da GetSeriesAsync</param>
        /// <param name="episodes">Episodi letti da GetEpisodesAsync</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="progress">Avanzamento in episodi, può essere null</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        public abstract Task CompleteEpisodesAsync(MediaCatalogSeries series, List<MediaCatalogEpisode> episodes, string language, IProgress<MediaCatalogProgress> progress, CancellationToken cancellationToken);

        /// <summary>
        /// Film completo
        /// </summary>
        /// <param name="key">Chiave del risultato di ricerca</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Film</returns>
        public abstract Task<MediaCatalogMovie> GetMovieAsync(string key, string language, CancellationToken cancellationToken);

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Mette le credenziali sulla richiesta
        /// </summary>
        /// <param name="request">Richiesta da autorizzare</param>
        /// <param name="renew">Vero dopo un 401: le credenziali in memoria vanno rinnovate</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        protected abstract Task AuthorizeAsync(HttpRequestMessage request, bool renew, CancellationToken cancellationToken);

        /// <summary>
        /// Indica se un 401 può dipendere da credenziali in memoria scadute e merita un rinnovo
        /// </summary>
        protected abstract bool CanRenewAuthorization { get; }

        /// <summary>
        /// GET autorizzato con ritentativi; 404 restituisce null
        /// </summary>
        /// <param name="url">URL assoluto</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Radice JSON della risposta, null se la risorsa non esiste</returns>
        protected async Task<JsonNode> GetJsonOrNullAsync(string url, CancellationToken cancellationToken)
        {
            bool renewed = false;
            bool renew = false;
            int retry = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HttpStatusCode status;
                string body;
                TimeSpan? retryAfter = null;

                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    await this.AuthorizeAsync(request, renew, cancellationToken);
                    renew = false;
                    try
                    {
                        using (HttpResponseMessage response = await this._httpClient.SendAsync(request, cancellationToken))
                        {
                            status = response.StatusCode;
                            body = await response.Content.ReadAsStringAsync(cancellationToken);
                            retryAfter = HttpRetryDelay.ReadRetryAfter(response.Headers.RetryAfter);
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                    {
                        if (retry < MAX_RETRIES)
                        {
                            await Task.Delay(HttpRetryDelay.Compute(retry, null, RETRY_BASE_DELAY, MAX_RETRY_DELAY, MAX_RETRY_DELAY, 0), cancellationToken);
                            retry++;
                            continue;
                        }

                        throw new MediaCatalogException(AppText.F("mediaCatalog.error.network", this.DisplayName, ex.Message), ex);
                    }
                }

                if (status == HttpStatusCode.NotFound)
                    return null;

                if (status == HttpStatusCode.Unauthorized)
                {
                    // Un token scaduto si rinnova una volta; un secondo rifiuto sono credenziali sbagliate
                    if (!renewed && this.CanRenewAuthorization)
                    {
                        renewed = true;
                        renew = true;
                        continue;
                    }

                    throw new MediaCatalogException(AppText.F("mediaCatalog.error.unauthorized", this.DisplayName));
                }

                if (status == HttpStatusCode.TooManyRequests || (int)status >= 500)
                {
                    if (retry < MAX_RETRIES)
                    {
                        await Task.Delay(HttpRetryDelay.Compute(retry, retryAfter, RETRY_BASE_DELAY, MAX_RETRY_DELAY, MAX_RETRY_DELAY, 0), cancellationToken);
                        retry++;
                        continue;
                    }

                    throw new MediaCatalogException(AppText.F("mediaCatalog.error.service", this.DisplayName, (int)status));
                }

                if ((int)status < 200 || (int)status >= 300)
                    throw new MediaCatalogException(AppText.F("mediaCatalog.error.service", this.DisplayName, (int)status));

                return ParseJson(body, this.DisplayName);
            }
        }

        /// <summary>
        /// GET autorizzato con ritentativi; la risorsa deve esistere
        /// </summary>
        /// <param name="url">URL assoluto</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Radice JSON della risposta</returns>
        protected async Task<JsonNode> GetJsonAsync(string url, CancellationToken cancellationToken)
        {
            JsonNode root = await this.GetJsonOrNullAsync(url, cancellationToken);
            if (root == null)
                throw new MediaCatalogException(AppText.F("mediaCatalog.error.notFound", this.DisplayName));

            return root;
        }

        /// <summary>
        /// POST JSON senza autorizzazione né ritentativi, per il login
        /// </summary>
        /// <param name="url">URL assoluto</param>
        /// <param name="payload">Corpo JSON</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Radice JSON della risposta</returns>
        protected async Task<JsonNode> PostJsonAsync(string url, JsonObject payload, CancellationToken cancellationToken)
        {
            HttpStatusCode status;
            string body;

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                try
                {
                    using (HttpResponseMessage response = await this._httpClient.SendAsync(request, cancellationToken))
                    {
                        status = response.StatusCode;
                        body = await response.Content.ReadAsStringAsync(cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    throw new MediaCatalogException(AppText.F("mediaCatalog.error.network", this.DisplayName, ex.Message), ex);
                }
            }

            if (status == HttpStatusCode.Unauthorized)
                throw new MediaCatalogException(AppText.F("mediaCatalog.error.unauthorized", this.DisplayName));

            if ((int)status < 200 || (int)status >= 300)
                throw new MediaCatalogException(AppText.F("mediaCatalog.error.service", this.DisplayName, (int)status));

            return ParseJson(body, this.DisplayName);
        }

        /// <summary>
        /// Errore di risposta non interpretabile: il dettaglio va nel log, all'utente il messaggio senza dettaglio
        /// </summary>
        /// <param name="detail">Cosa manca o non torna</param>
        /// <returns>Eccezione da lanciare</returns>
        protected MediaCatalogException CreateFormatError(string detail)
        {
            ConsoleHelper.Write(LogSection.General, LogLevel.Warning, AppText.F("mediaCatalog.error.format", this.DisplayName, detail));
            return new MediaCatalogException(AppText.F("mediaCatalog.error.unreadable", this.DisplayName));
        }

        /// <summary>
        /// Stringa di un nodo JSON, vuota se assente, null o non stringa
        /// </summary>
        /// <param name="node">Nodo</param>
        /// <returns>Testo ripulito dagli spazi</returns>
        protected static string ReadString(JsonNode node)
        {
            if (node is JsonValue value && value.TryGetValue(out string text) && text != null)
                return text.Trim();

            return "";
        }

        /// <summary>
        /// Intero di un nodo JSON numerico o testuale, null se assente o non intero
        /// </summary>
        /// <param name="node">Nodo</param>
        /// <returns>Valore</returns>
        protected static long? ReadLong(JsonNode node)
        {
            if (node is not JsonValue value)
                return null;

            if (value.TryGetValue(out long number))
                return number;

            if (value.TryGetValue(out double real) && real == Math.Floor(real))
                return (long)real;

            long parsed;
            if (value.TryGetValue(out string text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                return parsed;

            return null;
        }

        /// <summary>
        /// Intero positivo a 32 bit di un nodo JSON, null se assente, zero o negativo
        /// </summary>
        /// <param name="node">Nodo</param>
        /// <returns>Valore</returns>
        protected static int? ReadPositiveInt(JsonNode node)
        {
            long? value = ReadLong(node);
            if (value.HasValue && value.Value > 0 && value.Value <= int.MaxValue)
                return (int)value.Value;

            return null;
        }

        /// <summary>
        /// Data «yyyy-MM-dd» (eventualmente seguita da un orario) di un nodo JSON, null se assente o non valida
        /// </summary>
        /// <param name="node">Nodo</param>
        /// <returns>Data senza orario</returns>
        protected static DateTime? ReadDate(JsonNode node)
        {
            string text = ReadString(node);
            DateTime date;
            if (text.Length >= 10 && DateTime.TryParseExact(text.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return date;

            return null;
        }

        /// <summary>
        /// Chiave del risultato di ricerca come ID numerico per i percorsi
        /// </summary>
        /// <param name="key">Chiave del risultato di ricerca</param>
        /// <returns>ID in forma testuale invariante</returns>
        protected string ParseKey(string key)
        {
            long id;
            if (!long.TryParse((key ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0)
                throw new MediaCatalogException(AppText.F("mediaCatalog.error.notFound", this.DisplayName));

            return id.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Aggiunge alla serie una stagione nell'ordine dei numeri; un numero assente, negativo o già presente non aggiunge nulla
        /// </summary>
        /// <param name="series">Serie</param>
        /// <param name="number">Numero della stagione letto dal catalogo</param>
        /// <returns>Stagione aggiunta, null se scartata</returns>
        protected static MediaCatalogSeason AddSeason(MediaCatalogSeries series, long? number)
        {
            if (!number.HasValue || number.Value < 0 || series.Seasons.Exists(item => item.Number == number.Value))
                return null;

            MediaCatalogSeason season = new MediaCatalogSeason();
            int index = series.Seasons.FindIndex(item => item.Number > number.Value);
            season.Number = (int)number.Value;
            series.Seasons.Insert(index >= 0 ? index : series.Seasons.Count, season);
            return season;
        }

        /// <summary>
        /// Ordina gli episodi per stagione e numero
        /// </summary>
        /// <param name="episodes">Episodi</param>
        protected static void SortEpisodes(List<MediaCatalogEpisode> episodes)
        {
            episodes.Sort((a, b) => a.SeasonNumber != b.SeasonNumber ? a.SeasonNumber.CompareTo(b.SeasonNumber) : a.EpisodeNumber.CompareTo(b.EpisodeNumber));
        }

        /// <summary>
        /// Completa titoli e sinossi vuoti con quelli di un altro elenco del catalogo, per ID episodio
        /// </summary>
        /// <param name="episodes">Episodi da completare</param>
        /// <param name="list">Episodi dell'altro elenco, con id, name e overview</param>
        protected static void FillMissingTexts(List<MediaCatalogEpisode> episodes, IEnumerable<JsonNode> list)
        {
            foreach (JsonNode item in list)
            {
                long? id = item != null ? ReadLong(item["id"]) : null;
                MediaCatalogEpisode episode = id.HasValue ? episodes.Find(e => e.Id == id) : null;
                if (episode == null)
                    continue;

                if (episode.Title.Length == 0)
                    episode.Title = ReadString(item["name"]);
                if (episode.Overview.Length == 0)
                    episode.Overview = ReadString(item["overview"]);
            }
        }

        /// <summary>
        /// Nomi dei generi
        /// </summary>
        /// <param name="genres">Array dei generi, con name</param>
        /// <param name="target">Lista da riempire</param>
        protected static void ReadGenres(JsonArray genres, List<string> target)
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
        /// Voci dei crediti nell'ordine indicato dal catalogo; quelle senza ordine in fondo
        /// </summary>
        /// <param name="credits">Array dei crediti</param>
        /// <param name="orderField">Campo dell'ordine</param>
        /// <returns>Voci non nulle ordinate</returns>
        protected static List<JsonNode> SortByCreditOrder(JsonArray credits, string orderField)
        {
            List<JsonNode> sorted = new List<JsonNode>();
            foreach (JsonNode credit in credits)
            {
                if (credit != null)
                    sorted.Add(credit);
            }
            sorted.Sort((a, b) => (ReadLong(a[orderField]) ?? long.MaxValue).CompareTo(ReadLong(b[orderField]) ?? long.MaxValue));
            return sorted;
        }

        /// <summary>
        /// Aggiunge un nome a una lista se non vuoto e non già presente
        /// </summary>
        /// <param name="names">Lista</param>
        /// <param name="name">Nome</param>
        protected static void AddDistinct(List<string> names, string name)
        {
            if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                names.Add(name);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Interpreta il corpo di una risposta riuscita
        /// </summary>
        /// <param name="body">Corpo</param>
        /// <param name="displayName">Nome del catalogo, per il messaggio</param>
        /// <returns>Radice JSON</returns>
        private static JsonNode ParseJson(string body, string displayName)
        {
            try
            {
                JsonNode root = JsonNode.Parse(body);
                if (root != null)
                    return root;
            }
            catch (JsonException ex)
            {
                ConsoleHelper.Write(LogSection.General, LogLevel.Warning, AppText.F("mediaCatalog.error.format", displayName, ex.Message));
                throw new MediaCatalogException(AppText.F("mediaCatalog.error.unreadable", displayName), ex);
            }

            throw new MediaCatalogException(AppText.F("mediaCatalog.error.format", displayName, AppText.T("mediaCatalog.error.emptyBody")));
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome del catalogo nei messaggi all'utente
        /// </summary>
        protected string DisplayName { get { return MediaCatalogProviderNames.GetCatalogName(this.Provider); } }

        #endregion
    }
}
