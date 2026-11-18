using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Catalogo da cui proviene il dato: decide quali tag ID si scrivono
    /// </summary>
    public enum MediaCatalogProvider
    {
        /// <summary>TheTVDB: tag TVDB e TVDB2</summary>
        Tvdb,

        /// <summary>The Movie Database: tag TMDB</summary>
        Tmdb
    }

    /// <summary>
    /// Tipo di titolo cercato
    /// </summary>
    public enum MediaCatalogKind
    {
        /// <summary>Serie TV o anime</summary>
        Series,

        /// <summary>Film</summary>
        Movie
    }

    /// <summary>
    /// Natura dell'errore di una fonte, per il messaggio mostrato all'utente
    /// </summary>
    public enum MediaCatalogErrorKind
    {
        /// <summary>Credenziali della fonte non inserite</summary>
        NotConfigured,

        /// <summary>Credenziali rifiutate dal catalogo</summary>
        Unauthorized,

        /// <summary>Chiave inesistente sul catalogo</summary>
        NotFound,

        /// <summary>Rete irraggiungibile, timeout, servizio non disponibile o limite di frequenza dopo i tentativi</summary>
        Network,

        /// <summary>Risposta arrivata ma non interpretabile</summary>
        Format
    }

    /// <summary>
    /// Risultato di una ricerca su un catalogo media
    /// </summary>
    public class MediaCatalogSearchHit
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MediaCatalogSearchHit()
        {
            this.Key = "";
            this.Title = "";
            this.OriginalTitle = "";
            this.Overview = "";
            this.PosterUrl = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Chiave opaca della fonte da ripassare a GetSeriesAsync o GetMovieAsync: URL per il sito, ID per le API
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// ID numerico sul catalogo, null se la fonte non lo conosce
        /// </summary>
        public long? Id { get; set; }

        /// <summary>
        /// Tipo del titolo
        /// </summary>
        public MediaCatalogKind Kind { get; set; }

        /// <summary>
        /// Titolo nella lingua chiesta, con ripiego
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Titolo nella lingua originale, vuoto se coincide con Title
        /// </summary>
        public string OriginalTitle { get; set; }

        /// <summary>
        /// Anno di prima uscita
        /// </summary>
        public int? Year { get; set; }

        /// <summary>
        /// Sinossi nella lingua chiesta, con ripiego
        /// </summary>
        public string Overview { get; set; }

        /// <summary>
        /// URL assoluto del poster, vuoto se assente
        /// </summary>
        public string PosterUrl { get; set; }

        #endregion
    }

    /// <summary>
    /// Serie con l'elenco delle stagioni; gli episodi si leggono a parte, per le stagioni scelte
    /// </summary>
    public class MediaCatalogSeries
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MediaCatalogSeries()
        {
            this.Key = "";
            this.Title = "";
            this.OriginalTitle = "";
            this.Overview = "";
            this.Genres = new List<string>();
            this.PosterUrl = "";
            this.Seasons = new List<MediaCatalogSeason>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Chiave opaca della fonte
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// ID numerico sul catalogo, null se la fonte non lo conosce
        /// </summary>
        public long? Id { get; set; }

        /// <summary>
        /// Titolo nella lingua chiesta, con ripiego
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Titolo nella lingua originale, vuoto se coincide con Title
        /// </summary>
        public string OriginalTitle { get; set; }

        /// <summary>
        /// Anno della prima messa in onda
        /// </summary>
        public int? Year { get; set; }

        /// <summary>
        /// Sinossi nella lingua chiesta, con ripiego
        /// </summary>
        public string Overview { get; set; }

        /// <summary>
        /// Generi nella lingua che il catalogo fornisce
        /// </summary>
        public List<string> Genres { get; set; }

        /// <summary>
        /// URL assoluto del poster della serie, vuoto se assente
        /// </summary>
        public string PosterUrl { get; set; }

        /// <summary>
        /// Stagioni ordinate per numero; 0 = speciali
        /// </summary>
        public List<MediaCatalogSeason> Seasons { get; set; }

        #endregion
    }

    /// <summary>
    /// Stagione di una serie
    /// </summary>
    public class MediaCatalogSeason
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MediaCatalogSeason()
        {
            this.PosterUrl = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Numero della stagione; 0 = speciali
        /// </summary>
        public int Number { get; set; }

        /// <summary>
        /// Episodi della stagione dichiarati dal catalogo, null se la fonte non li indica con l'elenco delle stagioni
        /// </summary>
        public int? EpisodeCount { get; set; }

        /// <summary>
        /// URL assoluto del poster della stagione, vuoto se assente; una fonte può conoscerlo solo dopo aver letto gli episodi della stagione
        /// </summary>
        public string PosterUrl { get; set; }

        #endregion
    }

    /// <summary>
    /// Episodio di una serie
    /// </summary>
    public class MediaCatalogEpisode
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MediaCatalogEpisode()
        {
            this.Title = "";
            this.Overview = "";
            this.Directors = new List<string>();
            this.Writers = new List<string>();
            this.Actors = new List<string>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// ID numerico dell'episodio sul catalogo, null se la fonte non lo conosce
        /// </summary>
        public long? Id { get; set; }

        /// <summary>
        /// Numero della stagione; 0 = speciali
        /// </summary>
        public int SeasonNumber { get; set; }

        /// <summary>
        /// Numero dell'episodio nella stagione
        /// </summary>
        public int EpisodeNumber { get; set; }

        /// <summary>
        /// Numero assoluto, null se il catalogo non lo ha
        /// </summary>
        public int? AbsoluteNumber { get; set; }

        /// <summary>
        /// Titolo nella lingua chiesta, con ripiego
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Sinossi nella lingua chiesta, con ripiego
        /// </summary>
        public string Overview { get; set; }

        /// <summary>
        /// Data della prima messa in onda, solo la parte data
        /// </summary>
        public DateTime? AiredDate { get; set; }

        /// <summary>
        /// Durata in minuti
        /// </summary>
        public int? RuntimeMinutes { get; set; }

        /// <summary>
        /// Registi
        /// </summary>
        public List<string> Directors { get; set; }

        /// <summary>
        /// Sceneggiatori
        /// </summary>
        public List<string> Writers { get; set; }

        /// <summary>
        /// Attori nell'ordine dei crediti
        /// </summary>
        public List<string> Actors { get; set; }

        #endregion
    }

    /// <summary>
    /// Film
    /// </summary>
    public class MediaCatalogMovie
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MediaCatalogMovie()
        {
            this.Key = "";
            this.Title = "";
            this.OriginalTitle = "";
            this.Overview = "";
            this.Genres = new List<string>();
            this.Directors = new List<string>();
            this.Writers = new List<string>();
            this.Actors = new List<string>();
            this.PosterUrl = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Chiave opaca della fonte
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// ID numerico sul catalogo, null se la fonte non lo conosce
        /// </summary>
        public long? Id { get; set; }

        /// <summary>
        /// Titolo nella lingua chiesta, con ripiego
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Titolo nella lingua originale, vuoto se coincide con Title
        /// </summary>
        public string OriginalTitle { get; set; }

        /// <summary>
        /// Anno di uscita
        /// </summary>
        public int? Year { get; set; }

        /// <summary>
        /// Sinossi nella lingua chiesta, con ripiego
        /// </summary>
        public string Overview { get; set; }

        /// <summary>
        /// Prima uscita, solo la parte data
        /// </summary>
        public DateTime? ReleaseDate { get; set; }

        /// <summary>
        /// Generi nella lingua che il catalogo fornisce
        /// </summary>
        public List<string> Genres { get; set; }

        /// <summary>
        /// Registi
        /// </summary>
        public List<string> Directors { get; set; }

        /// <summary>
        /// Sceneggiatori
        /// </summary>
        public List<string> Writers { get; set; }

        /// <summary>
        /// Attori nell'ordine dei crediti
        /// </summary>
        public List<string> Actors { get; set; }

        /// <summary>
        /// URL assoluto del poster, vuoto se assente
        /// </summary>
        public string PosterUrl { get; set; }

        #endregion
    }

    /// <summary>
    /// Avanzamento del caricamento degli episodi delle stagioni scelte
    /// </summary>
    public record MediaCatalogProgress(int Completed, int Total);

    /// <summary>
    /// Unico errore che una fonte di catalogo lascia uscire, oltre alla cancellazione
    /// </summary>
    public class MediaCatalogException : Exception
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="kind">Natura dell'errore</param>
        /// <param name="message">Messaggio localizzato</param>
        public MediaCatalogException(MediaCatalogErrorKind kind, string message) : base(message)
        {
            this.Kind = kind;
        }

        /// <summary>
        /// Costruttore con eccezione interna
        /// </summary>
        /// <param name="kind">Natura dell'errore</param>
        /// <param name="message">Messaggio localizzato</param>
        /// <param name="innerException">Eccezione originale</param>
        public MediaCatalogException(MediaCatalogErrorKind kind, string message, Exception innerException) : base(message, innerException)
        {
            this.Kind = kind;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Natura dell'errore
        /// </summary>
        public MediaCatalogErrorKind Kind { get; }

        #endregion
    }
}
