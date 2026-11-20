using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace RemuxForge.Core.MediaCatalog
{
    /// <summary>
    /// Crea i client HTTP di fonti e copertine dei cataloghi, con le stesse intestazioni
    /// </summary>
    public static class MediaCatalogHttpClientFactory
    {
        #region Costanti

        /// <summary>
        /// Prodotto dichiarato nello User-Agent
        /// </summary>
        private const string USER_AGENT_PRODUCT = "RemuxForge";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Client con lo User-Agent di RemuxForge e il tempo massimo indicato
        /// </summary>
        /// <param name="timeout">Tempo massimo di una richiesta</param>
        /// <returns>Client di proprietà del chiamante</returns>
        public static HttpClient Create(TimeSpan timeout)
        {
            HttpClient client = new HttpClient();
            client.Timeout = timeout;
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(new ProductHeaderValue(USER_AGENT_PRODUCT)));
            return client;
        }

        #endregion
    }
}
