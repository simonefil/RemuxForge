using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.MediaCatalog
{
    /// <summary>
    /// Scarica le copertine del catalogo in file temporanei; chi le usa le cancella dopo la scrittura
    /// </summary>
    public class MediaCatalogImageDownloader
    {
        #region Variabili di classe

        /// <summary>
        /// Client HTTP condiviso
        /// </summary>
        private readonly HttpClient _httpClient;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MediaCatalogImageDownloader()
        {
            this._httpClient = new HttpClient();
            this._httpClient.Timeout = TimeSpan.FromSeconds(60);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scarica un'immagine JPEG nella cartella temporanea
        /// </summary>
        /// <param name="url">Indirizzo dell'immagine</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Percorso del file scaricato, vuoto se l'immagine non è un JPEG</returns>
        public async Task<string> DownloadJpegAsync(string url, CancellationToken cancellationToken)
        {
            byte[] content;

            try
            {
                content = await this._httpClient.GetByteArrayAsync(url, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new MediaCatalogException(MediaCatalogErrorKind.Network, AppText.F("mediaCatalog.error.coverDownload", ex.Message), ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MediaCatalogException(MediaCatalogErrorKind.Network, AppText.F("mediaCatalog.error.coverDownload", ex.Message), ex);
            }

            // Il nome dell'allegato è cover.jpg: un formato diverso non si allega con quel nome
            if (content.Length < 3 || content[0] != 0xFF || content[1] != 0xD8 || content[2] != 0xFF)
                return "";

            string path = Path.Combine(AppSettingsService.Instance.GetTempFolder(), "remuxforge-cover-" + Guid.NewGuid().ToString("N") + ".jpg");
            await File.WriteAllBytesAsync(path, content, cancellationToken);
            return path;
        }

        #endregion
    }
}
