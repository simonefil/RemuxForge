using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;
using System;
using System.Threading.Tasks;

namespace RemuxForge.Web.Components.Shared
{
    /// <summary>
    /// Base del contenuto di un dialog Radzen: porta il focus nel dialog all'apertura e lo restituisce all'opener alla chiusura
    /// </summary>
    public abstract class AppDialogContentBase : ComponentBase, IAsyncDisposable
    {
        #region Servizi iniettati

        /// <summary>
        /// Servizio dialog Radzen che ospita il contenuto
        /// </summary>
        [Inject]
        protected DialogService DialogService { get; set; }

        #endregion

        #region Parametri

        /// <summary>
        /// Modulo JS interop per focus e servizi del browser
        /// </summary>
        [Parameter]
        public IJSObjectReference JsModule { get; set; }

        /// <summary>
        /// Opzioni del dialog ospitante, per guardie di chiusura e titolo dinamico
        /// </summary>
        [Parameter]
        public DialogOptions HostOptions { get; set; }

        /// <summary>
        /// Dialog Radzen corrente
        /// </summary>
        [CascadingParameter]
        public Dialog HostDialog { get; set; }

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Radice del contenuto del dialog
        /// </summary>
        protected ElementReference DialogContent;

        /// <summary>
        /// Sessione di focus restituita dal modulo JS
        /// </summary>
        private Task<IJSObjectReference> _focusSession;

        #endregion

        #region Ciclo di vita

        /// <summary>
        /// Porta il focus nel dialog al primo render
        /// </summary>
        /// <param name="firstRender">True al primo render</param>
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && this.JsModule != null)
            {
                this._focusSession = this.JsModule.InvokeAsync<IJSObjectReference>("focusRemuxDialog", this.DialogContent).AsTask();
                try
                {
                    await this._focusSession;
                }
                catch (JSDisconnectedException)
                {
                    // Il circuito si è chiuso durante l'apertura del dialog
                }
            }
        }

        /// <summary>
        /// Restituisce il focus all'opener e rilascia la sessione JS
        /// </summary>
        public virtual async ValueTask DisposeAsync()
        {
            if (this._focusSession == null)
                return;

            try
            {
                IJSObjectReference session = await this._focusSession;
                await session.InvokeVoidAsync("dispose");
                await session.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
            catch (JSException) { }
            catch (ObjectDisposedException) { }
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Chiude il dialog ospitante con l'esito indicato
        /// </summary>
        /// <param name="result">Esito restituito a chi ha aperto il dialog</param>
        protected void CloseDialog(object result = null)
        {
            this.DialogService.Close(result);
        }

        /// <summary>
        /// Aggiorna il titolo del dialog ospitante
        /// </summary>
        /// <param name="title">Nuovo titolo</param>
        protected void SetDialogTitle(string title)
        {
            if (this.HostDialog != null)
                this.HostDialog.Title = title;
        }

        #endregion
    }
}
