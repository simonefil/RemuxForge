using Microsoft.AspNetCore.Components;
using Radzen;
using RemuxForge.Core.Localization;
using RemuxForge.Web.Components.Shared;
using RemuxForge.Web.Services;
using RemuxForge.Web.Services.Toolbox;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace RemuxForge.Web.Components.Toolbox
{
    /// <summary>
    /// Base delle pagine delle card a lotto: comandi Aggiungi file, avvio e Svuota elenco, scelta dei file nel browser, ridisegno
    /// quando l'orchestratore cambia e toast di fine lotto. La pagina disegna solo l'elenco
    /// </summary>
    /// <typeparam name="TOrchestrator">Orchestratore della card</typeparam>
    /// <typeparam name="TRecord">Riga dell'elenco</typeparam>
    public abstract class ToolboxBatchPageBase<TOrchestrator, TRecord> : ToolboxPageBase, IDisposable
        where TOrchestrator : ToolboxOrchestratorBase<TRecord>
        where TRecord : ToolboxRecord
    {
        #region Servizi

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

        /// <inheritdoc />
        public override IReadOnlyList<UiCommandDefinition> BuildUiCommands(bool busy)
        {
            UiCommandPlacement allSurfaces = UiCommandPlacement.Menu | UiCommandPlacement.Toolbar | UiCommandPlacement.Status;

            return new List<UiCommandDefinition>
            {
                new UiCommandDefinition(AppText.T(this.AddFilesKey + "Menu"), "F5", "add", allSurfaces, UiCommandMenuSection.File, busy, null)
                {
                    AsyncCallback = this.AddFilesAsync,
                    ToolbarLabel = AppText.T(this.AddFilesKey),
                    StatusLabel = AppText.T(this.AddFilesKey),
                    ToolbarOrder = 10,
                    StatusOrder = 10
                },
                new UiCommandDefinition(AppText.T(this.Orchestrator.TextPrefix + ".start"), "F10", "play_arrow", allSurfaces, UiCommandMenuSection.Actions, busy || !this.Orchestrator.CanStart, this.Orchestrator.Start)
                {
                    ToolbarOrder = 20,
                    StatusOrder = 20
                },
                new UiCommandDefinition(AppText.T("web.toolbox.list.clear"), "Ctrl+L", "clear_all", allSurfaces, UiCommandMenuSection.File, busy || this.IsListEmpty, this.Orchestrator.Clear)
                {
                    SecondaryToolbar = true,
                    ToolbarOrder = 15,
                    StatusOrder = 100
                }
            };
        }

        /// <summary>
        /// Toglie le sottoscrizioni: la pagina rinasce a ogni apertura della card, l'orchestratore resta
        /// </summary>
        public void Dispose()
        {
            this.Orchestrator.OnRecordsChanged -= this.HandleRecordsChanged;
            this.Orchestrator.OnBatchCompleted -= this.HandleBatchCompleted;
        }

        #endregion

        #region Metodi protected

        /// <inheritdoc />
        protected override void OnInitialized()
        {
            this.Orchestrator.OnRecordsChanged += this.HandleRecordsChanged;
            this.Orchestrator.OnBatchCompleted += this.HandleBatchCompleted;
        }

        /// <summary>
        /// Accoda i file scelti nel browser, partendo dalla cartella dell'ultimo file dell'elenco
        /// </summary>
        protected async Task AddFilesAsync()
        {
            List<TRecord> records = this.Orchestrator.GetRecords();
            string initialPath = records.Count > 0 ? Path.GetDirectoryName(records[records.Count - 1].InputPath) : "";
            List<string> paths = await AppDialogs.BrowseFilesAsync(this.DialogService, this.JsModule, initialPath, this.FileExtensions);

            if (paths.Count > 0)
                this.Orchestrator.AddFiles(paths);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Ridisegna pagina e comandi della dashboard quando cambiano elenco, scelte o uscita
        /// </summary>
        private void HandleRecordsChanged()
        {
            this.InvokeAsync(() => this.OnStateChanged.InvokeAsync());
        }

        /// <summary>
        /// Riassume in un toast l'esito del lotto: riuscito se tutto è riuscito senza avvisi, errore se nulla è riuscito, avviso negli
        /// altri casi. Il toast sta in basso a destra, sopra il log, per non coprire uscita e risultati
        /// </summary>
        /// <param name="summary">Riepilogo del lotto</param>
        private void HandleBatchCompleted(ToolboxBatchSummary summary)
        {
            this.InvokeAsync(async () =>
            {
                NotificationSeverity severity = summary.AllSucceeded ? NotificationSeverity.Success : summary.NothingSucceeded ? NotificationSeverity.Error : NotificationSeverity.Warning;
                this.NotificationService.Notify(new NotificationMessage
                {
                    Severity = severity,
                    Summary = AppText.T(this.Orchestrator.TextPrefix + (summary.Stopped ? ".notifyStoppedTitle" : ".notifyTitle")),
                    Detail = summary.Describe(),
                    Duration = severity == NotificationSeverity.Success ? 6000 : 8000,
                    Style = TOAST_STYLE
                });
                await this.OnStateChanged.InvokeAsync();
            });
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Estensioni proposte dal browser dei file
        /// </summary>
        protected abstract List<string> FileExtensions { get; }

        /// <summary>
        /// True se non c'è nulla da svuotare
        /// </summary>
        protected virtual bool IsListEmpty
        {
            get { return this.Orchestrator.GetRecords().Count == 0; }
        }

        /// <summary>
        /// Chiave del testo di Aggiungi file; la voce di menu usa la stessa chiave con il suffisso Menu
        /// </summary>
        protected virtual string AddFilesKey
        {
            get { return "web.toolbox.list.addFiles"; }
        }

        #endregion
    }
}
