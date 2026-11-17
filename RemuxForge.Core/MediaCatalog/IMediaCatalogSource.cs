using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.MediaCatalog
{
    /// <summary>
    /// Fonte di un catalogo media: TVDB sito, TVDB API, TMDB API.
    /// Regole valide per ogni implementazione:
    /// - è un singleton condiviso dall'app, quindi thread-safe;
    /// - language è il codice ISO 639-2/T dell'app (ita, eng, jpn...): la conversione al formato del catalogo la fa la fonte;
    /// - ogni testo è nella lingua chiesta, altrimenti in inglese, altrimenti nell'originale: il ripiego lo fa la fonte, campo per campo;
    /// - la cancellazione esce come OperationCanceledException, ogni altro errore come MediaCatalogException;
    /// - nessun riferimento restituito è null: stringhe e liste vuote per i dati mancanti.
    /// </summary>
    public interface IMediaCatalogSource
    {
        /// <summary>
        /// Catalogo servito dalla fonte
        /// </summary>
        MediaCatalogProvider Provider { get; }

        /// <summary>
        /// Cerca per titolo, risultati nell'ordine di rilevanza del catalogo, al massimo 20
        /// </summary>
        /// <param name="kind">Serie o film</param>
        /// <param name="query">Testo cercato</param>
        /// <param name="year">Anno di prima uscita, null per non filtrare</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Risultati</returns>
        Task<List<MediaCatalogSearchHit>> SearchAsync(MediaCatalogKind kind, string query, int? year, string language, CancellationToken cancellationToken);

        /// <summary>
        /// Serie completa nell'ordine ufficiale, con ogni episodio già completo di testi, numeri e persone
        /// </summary>
        /// <param name="key">Chiave del risultato di ricerca</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="progress">Avanzamento in episodi, può essere null</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Serie</returns>
        Task<MediaCatalogSeries> GetSeriesAsync(string key, string language, IProgress<MediaCatalogProgress> progress, CancellationToken cancellationToken);

        /// <summary>
        /// Film completo
        /// </summary>
        /// <param name="key">Chiave del risultato di ricerca</param>
        /// <param name="language">Codice ISO 639-2/T dei testi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Film</returns>
        Task<MediaCatalogMovie> GetMovieAsync(string key, string language, CancellationToken cancellationToken);
    }
}
