using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;
using RemuxForge.Web.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Web.Components.Shared
{
    /// <summary>
    /// Base degli editor a pagina intera che leggono il media di una sessione: servizi comuni, riferimento .NET passato al JS,
    /// notifica del temporaneo mancante e rilascio finale di riferimento e cancellazione
    /// </summary>
    /// <typeparam name="TEditor">Editor concreto, esposto al JS con i suoi metodi JSInvokable</typeparam>
    public abstract class MediaEditorPageBase<TEditor> : ComponentBase, IAsyncDisposable where TEditor : MediaEditorPageBase<TEditor>
    {
        #region Servizi iniettati

        /// <summary>
        /// Runtime JS per i moduli dell'editor
        /// </summary>
        [Inject]
        protected IJSRuntime JsRuntime { get; set; }

        /// <summary>
        /// Servizio delle notifiche
        /// </summary>
        [Inject]
        protected NotificationService NotificationService { get; set; }

        /// <summary>
        /// Servizio dei menu contestuali
        /// </summary>
        [Inject]
        protected ContextMenuService ContextMenuService { get; set; }

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Notifica del temporaneo della sessione mancante, mostrata una volta sola
        /// </summary>
        private readonly SessionMediaMissingNotice _sessionMediaNotice = new SessionMediaMissingNotice();

        /// <summary>
        /// Riferimento .NET passato al JS, creato al primo uso
        /// </summary>
        private DotNetObjectReference<TEditor> _dotNetReference;

        #endregion

        #region Proprietà

        /// <summary>
        /// Cancellazione legata alla vita dell'editor: annulla indicizzazione, decode e letture in corso
        /// </summary>
        protected CancellationTokenSource Lifetime { get; } = new CancellationTokenSource();

        /// <summary>
        /// Riferimento .NET passato al JS, creato al primo uso e rilasciato con l'editor
        /// </summary>
        protected DotNetObjectReference<TEditor> DotNetReference
        {
            get { return this._dotNetReference ??= DotNetObjectReference.Create((TEditor)this); }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Riceve da JavaScript la risposta 409 di un'anteprima o di un'immagine audio: manca il temporaneo della sessione.
        /// Si notifica una volta sola; l'anteprima fallita la gestisce l'editor
        /// </summary>
        /// <param name="side">Lato dell'anteprima fallita, null per un'immagine audio</param>
        /// <param name="message">Messaggio localizzato del server</param>
        [JSInvokable]
        public Task OnSessionMediaMissing(string side, string message)
        {
            return this.InvokeAsync(() =>
            {
                this._sessionMediaNotice.Notify(this.NotificationService, message);
                if (side != null)
                    this.OnPreviewMediaMissing(side);
            });
        }

        /// <summary>
        /// Rilascia le risorse dell'editor, poi riferimento .NET e cancellazione
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await this.DisposeEditorAsync();
            this._dotNetReference?.Dispose();
            this.Lifetime.Dispose();
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Segna l'anteprima di un lato come fallita per il temporaneo mancante
        /// </summary>
        /// <param name="side">Lato dell'anteprima fallita</param>
        protected abstract void OnPreviewMediaMissing(string side);

        /// <summary>
        /// Rilascia sessione, oggetti JS e iscrizioni dell'editor; riferimento .NET e cancellazione li rilascia la base dopo
        /// </summary>
        /// <returns>Task del rilascio</returns>
        protected abstract Task DisposeEditorAsync();

        /// <summary>
        /// Rilascia gli oggetti JS dell'editor, ognuno con il suo dispose, poi il modulo. Un'eccezione JS ferma solo il rilascio
        /// dell'oggetto che l'ha sollevata; le altre eccezioni si propagano
        /// </summary>
        /// <param name="module">Modulo JS dell'editor, null se non è stato caricato</param>
        /// <param name="objects">Oggetti JS creati dal modulo, null quelli non creati</param>
        /// <returns>Task del rilascio</returns>
        protected static async Task ReleaseJsAsync(IJSObjectReference module, params IJSObjectReference[] objects)
        {
            foreach (IJSObjectReference js in objects)
            {
                if (js != null)
                {
                    try
                    {
                        await js.InvokeVoidAsync("dispose");
                        await js.DisposeAsync();
                    }
                    catch (JSException)
                    {
                    }
                }
            }
            if (module != null)
            {
                try
                {
                    await module.DisposeAsync();
                }
                catch (JSException)
                {
                }
            }
        }

        #endregion
    }
}
