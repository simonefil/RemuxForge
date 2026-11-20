using RemuxForge.Core.MediaCatalog;
using RemuxForge.Core.Models;
using System.Net.Http;
using System.Net.Http.Headers;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Fonti dei cataloghi media condivise dall'app: restituisce quella del catalogo scelto, per TheTVDB nella modalità delle impostazioni
    /// </summary>
    public class MediaCatalogSourceProvider
    {
        #region Variabili di classe

        /// <summary>
        /// Impostazioni dei cataloghi
        /// </summary>
        private readonly MediaCatalogSettingsService _settingsService;

        /// <summary>
        /// TheTVDB dal sito
        /// </summary>
        private readonly TvdbScraperSource _tvdbSite;

        /// <summary>
        /// TheTVDB dall'API
        /// </summary>
        private readonly TvdbApiSource _tvdbApi;

        /// <summary>
        /// TMDB dall'API
        /// </summary>
        private readonly TmdbApiSource _tmdbApi;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="settingsService">Impostazioni dei cataloghi</param>
        public MediaCatalogSourceProvider(MediaCatalogSettingsService settingsService)
        {
            HttpClient siteClient = new HttpClient();
            siteClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(new ProductHeaderValue("RemuxForge")));

            this._settingsService = settingsService;
            this._tvdbSite = new TvdbScraperSource(siteClient);
            this._tvdbApi = new TvdbApiSource(() => this._settingsService.Load().TvdbApiKey);
            this._tmdbApi = new TmdbApiSource(() => this._settingsService.Load().TmdbCredential);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Fonte del catalogo indicato
        /// </summary>
        /// <param name="catalog">Catalogo</param>
        /// <returns>Fonte</returns>
        public IMediaCatalogSource GetSource(MediaCatalogProvider catalog)
        {
            if (catalog == MediaCatalogProvider.Tmdb)
                return this._tmdbApi;

            return this._settingsService.Load().TvdbMode == TvdbAccessMode.Site ? this._tvdbSite : this._tvdbApi;
        }

        #endregion
    }
}
