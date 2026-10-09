using Microsoft.JSInterop;
using Radzen;
using RemuxForge.Core.Localization;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RemuxForge.Web.Components.Shared
{
    /// <summary>
    /// Opzioni e aperture condivise dei dialog applicativi ospitati dal DialogService Radzen
    /// </summary>
    public static class AppDialogs
    {
        #region Costanti

        /// <summary>
        /// Larghezza dei dialog compatti
        /// </summary>
        public const string WIDTH_COMPACT = "min(34rem, calc(100vw - 4rem))";

        /// <summary>
        /// Larghezza dei dialog standard
        /// </summary>
        public const string WIDTH_STANDARD = "min(68rem, calc(100vw - 4rem))";

        /// <summary>
        /// Larghezza dei dialog medi
        /// </summary>
        public const string WIDTH_MEDIUM = "min(56rem, calc(100vw - 4rem))";

        /// <summary>
        /// Larghezza delle impostazioni AI
        /// </summary>
        public const string WIDTH_AI_SETTINGS = "min(90ch, calc(100vw - 4rem))";

        /// <summary>
        /// Larghezza del wizard preset con AI
        /// </summary>
        public const string WIDTH_AI_WIZARD = "min(140ch, calc(100vw - 4rem))";

        /// <summary>
        /// Larghezza dei dialog ampi
        /// </summary>
        public const string WIDTH_WIDE = "min(78rem, calc(100vw - 4rem))";

        /// <summary>
        /// Larghezza dei dialog a tutto schermo
        /// </summary>
        public const string WIDTH_FULL = "calc(100vw - 4rem)";

        /// <summary>
        /// Altezza dei dialog a tutto schermo
        /// </summary>
        public const string HEIGHT_FULL = "calc(100vh - 4rem)";

        /// <summary>
        /// Larghezza dell'esempio di una funzione metadata
        /// </summary>
        public const string WIDTH_FUNCTION_EXAMPLE = "min(76ch, calc(100vw - 4rem))";

        /// <summary>
        /// Larghezza dell'editor di testo espanso
        /// </summary>
        public const string WIDTH_TEXT_EDITOR = "min(112ch, calc(100vw - 4rem))";

        /// <summary>
        /// Larghezza dei dialog di conferma
        /// </summary>
        public const string WIDTH_CONFIRM = "min(44rem, calc(100vw - 4rem))";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Crea le opzioni comuni dei dialog applicativi
        /// </summary>
        /// <param name="width">Larghezza CSS</param>
        /// <param name="height">Altezza CSS, null per adattarla al contenuto</param>
        /// <param name="jsModule">Modulo JS che porta il focus nel contenuto; senza modulo il focus lo porta Radzen</param>
        /// <param name="cssClass">Classe CSS aggiuntiva del dialog</param>
        /// <returns>Opzioni del dialog</returns>
        public static DialogOptions CreateOptions(string width, string height, IJSObjectReference jsModule, string cssClass = null)
        {
            DialogOptions options = new DialogOptions();
            options.Width = width;
            options.Height = height;
            options.CloseDialogOnOverlayClick = false;
            options.CloseDialogOnEsc = true;
            options.AutoFocusFirstElement = jsModule == null;
            options.CssClass = string.IsNullOrEmpty(cssClass) ? "rf-dialog-host" : "rf-dialog-host " + cssClass;
            options.ContentCssClass = "rf-dialog-content";
            options.CloseAriaLabel = AppText.T("web.common.close");
            return options;
        }

        /// <summary>
        /// Apre un contenuto nel dialog passando modulo JS e opzioni ospitanti
        /// </summary>
        /// <typeparam name="T">Componente contenuto</typeparam>
        /// <param name="dialogService">Servizio dialog</param>
        /// <param name="title">Titolo del dialog</param>
        /// <param name="parameters">Parametri del contenuto</param>
        /// <param name="options">Opzioni del dialog</param>
        /// <param name="jsModule">Modulo JS interop</param>
        /// <returns>Esito restituito dal contenuto</returns>
        public static async Task<object> OpenAsync<T>(DialogService dialogService, string title, Dictionary<string, object> parameters, DialogOptions options, IJSObjectReference jsModule) where T : AppDialogContentBase
        {
            Dictionary<string, object> values = parameters != null ? new Dictionary<string, object>(parameters) : new Dictionary<string, object>();
            values[nameof(AppDialogContentBase.JsModule)] = jsModule;
            values[nameof(AppDialogContentBase.HostOptions)] = options;
            object result = await dialogService.OpenAsync<T>(title, values, options);
            return result;
        }

        /// <summary>
        /// Apre il browser di file e cartelle
        /// </summary>
        /// <param name="dialogService">Servizio dialog</param>
        /// <param name="jsModule">Modulo JS interop</param>
        /// <param name="initialPath">Percorso iniziale</param>
        /// <param name="showFiles">True per mostrare e selezionare i file</param>
        /// <param name="allowCurrentFolderSelection">True per confermare la cartella corrente</param>
        /// <param name="allowedExtensions">Estensioni ammesse senza punto, null per tutti i file</param>
        /// <returns>Percorso scelto, null se annullato</returns>
        public static async Task<string> BrowseAsync(DialogService dialogService, IJSObjectReference jsModule, string initialPath, bool showFiles, bool allowCurrentFolderSelection, List<string> allowedExtensions)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            parameters.Add(nameof(FolderBrowserDialog.InitialPath), initialPath);
            parameters.Add(nameof(FolderBrowserDialog.ShowFiles), showFiles);
            parameters.Add(nameof(FolderBrowserDialog.AllowCurrentFolderSelection), allowCurrentFolderSelection);
            parameters.Add(nameof(FolderBrowserDialog.AllowedExtensions), allowedExtensions);

            object result = await OpenAsync<FolderBrowserDialog>(dialogService, FolderBrowserDialog.GetTitle(showFiles, allowCurrentFolderSelection), parameters,
                CreateOptions("80vw", "70vh", jsModule, "rf-browse-dialog"), jsModule);
            return result as string;
        }

        /// <summary>
        /// Apre il report MediaInfo
        /// </summary>
        /// <param name="dialogService">Servizio dialog</param>
        /// <param name="jsModule">Modulo JS interop</param>
        /// <param name="title">Nome del file o sorgente del report</param>
        /// <param name="report">Report testuale</param>
        public static async Task OpenMediaInfoAsync(DialogService dialogService, IJSObjectReference jsModule, string title, string report)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            parameters.Add(nameof(MediaInfoDialogComponent.Report), report);

            await OpenAsync<MediaInfoDialogComponent>(dialogService, AppText.F("web.mediaInfo.title", title), parameters,
                CreateOptions(WIDTH_WIDE, null, jsModule), jsModule);
        }

        /// <summary>
        /// Apre l'editor dei profili di encoding
        /// </summary>
        /// <param name="dialogService">Servizio dialog</param>
        /// <param name="jsModule">Modulo JS interop</param>
        /// <returns>Nome dell'ultimo profilo creato o modificato al salvataggio, null se annullato</returns>
        public static async Task<string> OpenEncodingProfilesAsync(DialogService dialogService, IJSObjectReference jsModule)
        {
            object result = await OpenAsync<EncodingProfilesDialog>(dialogService, AppText.T("web.encodingProfiles.title"), null,
                CreateOptions(WIDTH_STANDARD, null, jsModule), jsModule);
            return result as string;
        }

        /// <summary>
        /// Chiede una conferma con un dialog Radzen
        /// </summary>
        /// <param name="dialogService">Servizio dialog</param>
        /// <param name="title">Titolo</param>
        /// <param name="message">Messaggio, gli a capo vengono mostrati</param>
        /// <param name="okText">Testo del pulsante di conferma</param>
        /// <param name="cancelText">Testo del pulsante di annullamento</param>
        /// <returns>True se confermato</returns>
        public static async Task<bool> ConfirmAsync(DialogService dialogService, string title, string message, string okText, string cancelText)
        {
            ConfirmOptions options = new ConfirmOptions();
            options.OkButtonText = okText;
            options.CancelButtonText = cancelText;
            options.Width = WIDTH_CONFIRM;
            options.CloseDialogOnOverlayClick = false;
            options.CloseDialogOnEsc = true;
            options.CssClass = "rf-confirm";
            options.CloseAriaLabel = AppText.T("web.common.close");
            bool? result = await dialogService.Confirm(message, title, options);
            return result == true;
        }

        #endregion
    }
}
