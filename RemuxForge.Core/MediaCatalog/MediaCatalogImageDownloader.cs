using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
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
        #region Costanti

        /// <summary>
        /// Dimensione massima di una copertina
        /// </summary>
        private const int MAX_COVER_BYTES = 20 * 1024 * 1024;

        /// <summary>
        /// Tempo massimo dello scaricamento di una copertina, corpo compreso
        /// </summary>
        private static readonly TimeSpan DOWNLOAD_TIMEOUT = TimeSpan.FromSeconds(60);

        /// <summary>
        /// Host da cui i cataloghi servono le immagini, gli unici da cui si scarica
        /// </summary>
        private static readonly string[] s_coverHosts = { new Uri(TvdbApiSource.ARTWORK_BASE_URL).Host, new Uri(TmdbApiSource.IMAGE_BASE_URL).Host };

        #endregion

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
            this._httpClient = MediaCatalogHttpClientFactory.Create(DOWNLOAD_TIMEOUT);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scarica una copertina JPEG o PNG nella cartella temporanea, con l'estensione del formato letto dal contenuto
        /// </summary>
        /// <param name="url">Indirizzo https dell'immagine su un host dei cataloghi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Percorso del file scaricato, vuoto se l'immagine non è né JPEG né PNG</returns>
        public async Task<string> DownloadCoverAsync(string url, CancellationToken cancellationToken)
        {
            Uri address;
            byte[] content;

            if (!Uri.TryCreate(url, UriKind.Absolute, out address) || address.Scheme != Uri.UriSchemeHttps || Array.IndexOf(s_coverHosts, address.Host.ToLowerInvariant()) < 0)
                throw new MediaCatalogException(AppText.F("mediaCatalog.error.coverAddress", url));

            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(DOWNLOAD_TIMEOUT);
                try
                {
                    using (HttpResponseMessage response = await this._httpClient.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                    {
                        response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > MAX_COVER_BYTES)
                            throw new MediaCatalogException(AppText.F("mediaCatalog.error.coverTooLarge", MAX_COVER_BYTES / (1024 * 1024)));

                        using (Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token))
                        using (MemoryStream buffer = new MemoryStream())
                        {
                            byte[] chunk = new byte[81920];
                            int read;

                            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
                            {
                                if (buffer.Length + read > MAX_COVER_BYTES)
                                    throw new MediaCatalogException(AppText.F("mediaCatalog.error.coverTooLarge", MAX_COVER_BYTES / (1024 * 1024)));

                                buffer.Write(chunk, 0, read);
                            }

                            content = buffer.ToArray();
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    throw new MediaCatalogException(AppText.F("mediaCatalog.error.coverDownload", ex.Message), ex);
                }
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
            try
            {
                await File.WriteAllBytesAsync(path, content, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                FileHelper.DeleteTempFile(path);
                throw new MediaCatalogException(AppText.F("mediaCatalog.error.coverDownload", ex.Message), ex);
            }
            catch (OperationCanceledException)
            {
                FileHelper.DeleteTempFile(path);
                throw;
            }

            return path;
        }

        #endregion
    }
}
