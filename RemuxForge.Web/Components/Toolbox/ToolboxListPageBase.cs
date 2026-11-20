using Microsoft.AspNetCore.Components;
using Radzen;
using RemuxForge.Web.Components.Shared;
using RemuxForge.Web.Services.Toolbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace RemuxForge.Web.Components.Toolbox
{
    /// <summary>
    /// Base delle pagine delle card a elenco di file: scelta dei file nel browser, ridisegno quando l'orchestratore cambia e toast
    /// sopra il log
    /// </summary>
    /// <typeparam name="TOrchestrator">Orchestratore della card</typeparam>
    /// <typeparam name="TRecord">Riga dell'elenco</typeparam>
    public abstract class ToolboxListPageBase<TOrchestrator, TRecord> : ToolboxPageBase, IDisposable
        where TOrchestrator : ToolboxListOrchestratorBase<TRecord>
        where TRecord : ToolboxRecord
    {
        #region Costanti

        /// <summary>
        /// Posizione dei toast delle pagine: in basso a destra, sopra il log, per non coprire uscita e risultati. Le parole lunghe
        /// come i percorsi vanno a capo per restare nel toast
        /// </summary>
        private const string TOAST_STYLE = "position: fixed; inset-block-start: auto; inset-block-end: 9rem; inset-inline-end: 1rem; margin: 0; overflow-wrap: anywhere";

        #endregion

        #region Servizi iniettati

        /// <summary>
        /// Orchestratore della card, singleton che sopravvive alla pagina
        /// </summary>
        [Inject] protected TOrchestrator Orchestrator { get; set; }

        /// <summary>
        /// Servizio dei dialog, per il browser dei file
        /// </summary>
        [Inject] protected DialogService DialogService { get; set; }

        /// <summary>
        /// Servizio dei toast
        /// </summary>
        [Inject] protected NotificationService NotificationService { get; set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Toglie le sottoscrizioni: la pagina rinasce a ogni apertura della card, l'orchestratore resta
        /// </summary>
        public virtual void Dispose()
        {
            this.Orchestrator.OnRecordsChanged -= this.HandleRecordsChanged;
        }

        #endregion

        #region Metodi protected

        /// <inheritdoc />
        protected override void OnInitialized()
        {
            this.Orchestrator.OnRecordsChanged += this.HandleRecordsChanged;
        }

        /// <summary>
        /// Chiamato sul circuito quando cambiano elenco, scelte o uscita, prima del ridisegno: la pagina con viste proprie le rilegge
        /// </summary>
        protected virtual void OnOrchestratorChanged()
        {
        }

        /// <summary>
        /// Accoda i file scelti nel browser, partendo dalla cartella dell'ultimo file dell'elenco
        /// </summary>
        protected Task AddFilesAsync()
        {
            List<TRecord> records = this.Orchestrator.GetRecords();
            return this.BrowseAndAddFilesAsync(this.FileExtensions, this.Orchestrator.AddFiles, records.Count > 0 ? records[records.Count - 1].InputPath : "");
        }

        /// <summary>
        /// Accoda a un elenco della card i file scelti nel browser, partendo dalla cartella di un file già scelto
        /// </summary>
        /// <param name="extensions">Estensioni proposte dal browser</param>
        /// <param name="add">Accoda i percorsi scelti</param>
        /// <param name="lastPath">File dalla cui cartella parte il browser, vuoto per la cartella predefinita</param>
        protected async Task BrowseAndAddFilesAsync(List<string> extensions, Action<List<string>> add, string lastPath)
        {
            string initialPath = lastPath.Length > 0 ? Path.GetDirectoryName(lastPath) : "";
            List<string> paths = await AppDialogs.BrowseFilesAsync(this.DialogService, this.JsModule, initialPath, extensions);

            if (paths.Count > 0)
                add(paths);
        }

        /// <summary>
        /// Toast in basso a destra, sopra il log: un esito riuscito resta meno degli altri
        /// </summary>
        /// <param name="severity">Gravità</param>
        /// <param name="summary">Titolo</param>
        /// <param name="detail">Testo</param>
        protected void Notify(NotificationSeverity severity, string summary, string detail)
        {
            this.NotificationService.Notify(new NotificationMessage
            {
                Severity = severity,
                Summary = summary,
                Detail = detail,
                Duration = severity == NotificationSeverity.Success ? 6000 : 8000,
                Style = TOAST_STYLE
            });
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Ridisegna pagina e comandi della dashboard quando cambiano elenco, scelte o uscita
        /// </summary>
        private void HandleRecordsChanged()
        {
            this.InvokeAsync(() =>
            {
                this.OnOrchestratorChanged();
                return this.OnStateChanged.InvokeAsync();
            });
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Estensioni proposte dal browser dei file
        /// </summary>
        protected abstract List<string> FileExtensions { get; }

        #endregion
    }
}
