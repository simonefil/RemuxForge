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
        /// Scarica una copertina JPEG o PNG nella cartella temporanea, con l'estensione del formato letto dal contenuto
        /// </summary>
        /// <param name="url">Indirizzo dell'immagine</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Percorso del file scaricato, vuoto se l'immagine non è né JPEG né PNG</returns>
        public async Task<string> DownloadCoverAsync(string url, CancellationToken cancellationToken)
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

            // L'allegato si chiama cover.jpg o cover.png: il formato si legge dai primi byte, non dall'indirizzo
            string extension;
            if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
                extension = ".jpg";
            else if (content.Length >= 8 && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47 && content[4] == 0x0D && content[5] == 0x0A && content[6] == 0x1A && content[7] == 0x0A)
                extension = ".png";
            else
                return "";

            string path = Path.Combine(AppSettingsService.Instance.GetTempFolder(), "remuxforge-cover-" + Guid.NewGuid().ToString("N") + extension);
            await File.WriteAllBytesAsync(path, content, cancellationToken);
            return path;
        }

        #endregion
    }
}
