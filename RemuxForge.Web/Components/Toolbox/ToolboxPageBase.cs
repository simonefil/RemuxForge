using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using RemuxForge.Web.Services;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RemuxForge.Web.Components.Toolbox
{
    /// <summary>
    /// Base delle pagine dei tool aperte dalle card: la pagina disegna il workspace e fornisce i suoi comandi, mentre header, menu,
    /// toolbar, log, avanzamento e tastiera restano della dashboard
    /// </summary>
    public abstract class ToolboxPageBase : ComponentBase
    {
        #region Costanti

        /// <summary>
        /// Posizione dei toast delle pagine: in basso a destra, sopra il log, per non coprire uscita e risultati. Le parole lunghe
        /// come i percorsi vanno a capo per restare nel toast
        /// </summary>
        protected const string TOAST_STYLE = "position: fixed; inset-block-start: auto; inset-block-end: 9rem; inset-inline-end: 1rem; margin: 0; overflow-wrap: anywhere";

        #endregion

        #region Parametri

        /// <summary>
        /// Modulo JS interop della dashboard, per i dialog
        /// </summary>
        [Parameter] public IJSObjectReference JsModule { get; set; }

        /// <summary>
        /// Avvisa la dashboard che i comandi della pagina vanno ridisegnati
        /// </summary>
        [Parameter] public EventCallback OnStateChanged { get; set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Comandi della pagina per menu, toolbar, status bar e tastiera
        /// </summary>
        /// <param name="busy">True se una card sta eseguendo un'operazione</param>
        /// <returns>Comandi del tool, senza quelli comuni della dashboard</returns>
        public abstract IReadOnlyList<UiCommandDefinition> BuildUiCommands(bool busy);

        #endregion

        #region Metodi protected

        /// <summary>
        /// Al primo disegno la dashboard conosce la pagina e ridisegna menu e toolbar con i suoi comandi
        /// </summary>
        /// <param name="firstRender">True al primo disegno</param>
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
                await this.OnStateChanged.InvokeAsync();
        }

        #endregion
    }
}
