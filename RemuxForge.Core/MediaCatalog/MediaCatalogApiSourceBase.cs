using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
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
        /// Ritentativi dopo il primo invio su limite di frequenza o errore transitorio
        /// </summary>
        private const int MAX_RETRIES = 2;

        /// <summary>
        /// Attesa del primo ritentativo; raddoppia a ogni ritentativo
        /// </summary>
        private static readonly TimeSpan RETRY_BASE_DELAY = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Tetto dell'attesa chiesta dal servizio con Retry-After
        /// </summary>
        private static readonly TimeSpan MAX_RETRY_AFTER = TimeSpan.FromSeconds(30);

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
            this._httpClient = new HttpClient();
            this._httpClient.Timeout = TimeSpan.FromSeconds(30);
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

        #region Metodi protected

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
                            RetryConditionHeaderValue retryHeader = response.Headers.RetryAfter;
                            if (retryHeader != null && retryHeader.Delta.HasValue)
                                retryAfter = retryHeader.Delta.Value;
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                    {
                        if (retry < MAX_RETRIES)
                        {
                            await Task.Delay(GetRetryDelay(retry, null), cancellationToken);
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
                        await Task.Delay(GetRetryDelay(retry, retryAfter), cancellationToken);
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
        /// Errore di risposta non interpretabile
        /// </summary>
        /// <param name="detail">Cosa manca o non torna</param>
        /// <returns>Eccezione da lanciare</returns>
        protected MediaCatalogException CreateFormatError(string detail)
        {
            return new MediaCatalogException(AppText.F("mediaCatalog.error.format", this.DisplayName, detail));
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
            if (value.TryGetValue(out string text) && long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out parsed))
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
            if (text.Length >= 10 && DateTime.TryParseExact(text.Substring(0, 10), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date))
                return date;

            return null;
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
        /// Attesa prima di un ritentativo: Retry-After se il servizio lo indica, altrimenti backoff che raddoppia
        /// </summary>
        /// <param name="retry">Ritentativi già fatti</param>
        /// <param name="retryAfter">Attesa chiesta dal servizio</param>
        /// <returns>Attesa</returns>
        private static TimeSpan GetRetryDelay(int retry, TimeSpan? retryAfter)
        {
            if (retryAfter.HasValue)
                return retryAfter.Value < MAX_RETRY_AFTER ? retryAfter.Value : MAX_RETRY_AFTER;

            return TimeSpan.FromTicks(RETRY_BASE_DELAY.Ticks << retry);
        }

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
                throw new MediaCatalogException(AppText.F("mediaCatalog.error.format", displayName, ex.Message), ex);
            }

            throw new MediaCatalogException(AppText.F("mediaCatalog.error.format", displayName, "empty body"));
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome del catalogo nei messaggi all'utente
        /// </summary>
        protected string DisplayName { get { return this.Provider == MediaCatalogProvider.Tvdb ? "TheTVDB" : "TMDB"; } }

        #endregion
    }
}
