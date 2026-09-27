using Microsoft.Extensions.Hosting;
using RemuxForge.Core.Infrastructure;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Controlla su GitHub se esiste una release più recente della versione in esecuzione
    /// </summary>
    public class UpdateCheckService : BackgroundService
    {
        #region Costanti

        /// <summary>Endpoint GitHub dell'ultima release pubblicata, bozze e pre-release escluse</summary>
        private const string LATEST_RELEASE_URL = "https://api.github.com/repos/simonefil/RemuxForge/releases/latest";

        /// <summary>Intervallo fra due controlli</summary>
        private static readonly TimeSpan CHECK_INTERVAL = TimeSpan.FromHours(1);

        #endregion

        #region Variabili di classe

        private readonly HttpClient _httpClient;
        private volatile string _latestVersion;
        private volatile string _releaseUrl;

        #endregion

        #region Eventi

        /// <summary>Evento emesso quando cambia la release disponibile</summary>
        public event Action OnUpdateChanged;

        #endregion

        #region Costruttore

        /// <summary>
        /// Prepara il client HTTP con le intestazioni richieste dall'API GitHub
        /// </summary>
        public UpdateCheckService()
        {
            this._httpClient = new HttpClient();
            this._httpClient.Timeout = TimeSpan.FromSeconds(20);
            this._httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RemuxForge", NormalizeVersion(Utils.GetVersion())));
            this._httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            this._latestVersion = "";
            this._releaseUrl = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Versione della release più recente, vuota finché non ne esiste una maggiore della corrente
        /// </summary>
        public string LatestVersion { get { return this._latestVersion; } }

        /// <summary>
        /// Pagina GitHub della release più recente
        /// </summary>
        public string ReleaseUrl { get { return this._releaseUrl; } }

        /// <summary>
        /// True quando su GitHub esiste una release maggiore della versione in esecuzione
        /// </summary>
        public bool IsUpdateAvailable { get { return this._latestVersion.Length > 0 && this._releaseUrl.Length > 0; } }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Controlla subito all'avvio e poi a intervalli regolari
        /// </summary>
        /// <param name="stoppingToken">Token di arresto dell'host</param>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await this.CheckAsync(stoppingToken);
                try
                {
                    await Task.Delay(CHECK_INTERVAL, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Legge l'ultima release e la confronta con la versione corrente; un errore di rete lascia lo stato invariato
        /// </summary>
        /// <param name="cancellationToken">Token di arresto</param>
        private async Task CheckAsync(CancellationToken cancellationToken)
        {
            string tagName;
            string htmlUrl;
            Version latest;
            Version current;

            try
            {
                using HttpResponseMessage response = await this._httpClient.GetAsync(LATEST_RELEASE_URL, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return;

                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                tagName = document.RootElement.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() : null;
                htmlUrl = document.RootElement.TryGetProperty("html_url", out JsonElement urlElement) ? urlElement.GetString() : null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is JsonException)
            {
                return;
            }

            if (string.IsNullOrEmpty(tagName) || string.IsNullOrEmpty(htmlUrl) ||
                !Version.TryParse(NormalizeVersion(tagName), out latest) || !Version.TryParse(NormalizeVersion(Utils.GetVersion()), out current))
                return;

            string latestVersion = latest > current ? NormalizeVersion(tagName) : "";
            string releaseUrl = latest > current ? htmlUrl : "";
            if (string.Equals(latestVersion, this._latestVersion, StringComparison.Ordinal) && string.Equals(releaseUrl, this._releaseUrl, StringComparison.Ordinal))
                return;

            this._latestVersion = latestVersion;
            this._releaseUrl = releaseUrl;
            this.OnUpdateChanged?.Invoke();
        }

        /// <summary>
        /// Riduce tag e versione informativa alla parte numerica: v5.6.1 e 5.6.1+abc diventano 5.6.1
        /// </summary>
        /// <param name="version">Tag o versione da normalizzare</param>
        /// <returns>Versione numerica</returns>
        private static string NormalizeVersion(string version)
        {
            string result = version != null ? version.Trim() : "";
            int suffix = result.IndexOfAny(new char[] { '+', '-' });
            if (suffix >= 0)
                result = result.Substring(0, suffix);
            if (result.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                result = result.Substring(1);

            return result;
        }

        #endregion
    }
}
