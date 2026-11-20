using Radzen;
using RemuxForge.Core.Localization;
using RemuxForge.Web.Services;
using RemuxForge.Web.Services.Toolbox;
using System.Collections.Generic;

namespace RemuxForge.Web.Components.Toolbox
{
    /// <summary>
    /// Base delle pagine delle card a lotto: comandi Aggiungi file, avvio e Svuota elenco e toast di fine lotto. La pagina disegna
    /// solo l'elenco
    /// </summary>
    /// <typeparam name="TOrchestrator">Orchestratore della card</typeparam>
    /// <typeparam name="TRecord">Riga dell'elenco</typeparam>
    public abstract class ToolboxBatchPageBase<TOrchestrator, TRecord> : ToolboxListPageBase<TOrchestrator, TRecord>
        where TOrchestrator : ToolboxBatchOrchestratorBase<TRecord>
        where TRecord : ToolboxRecord
    {
        #region Metodi pubblici

        /// <inheritdoc />
        public override IReadOnlyList<UiCommandDefinition> BuildUiCommands(bool busy)
        {
            UiCommandPlacement allSurfaces = UiCommandPlacement.Menu | UiCommandPlacement.Toolbar;

            return new List<UiCommandDefinition>
            {
                new UiCommandDefinition(AppText.T(this.AddFilesKey + "Menu"), "F5", "add", allSurfaces, UiCommandMenuSection.File, busy, null)
                {
                    AsyncCallback = this.AddFilesAsync,
                    ToolbarLabel = AppText.T(this.AddFilesKey),
                    ToolbarOrder = 10
                },
                new UiCommandDefinition(AppText.T(this.Orchestrator.TextPrefix + ".start"), "F10", "play_arrow", allSurfaces, UiCommandMenuSection.Actions, busy || !this.Orchestrator.CanStart, this.Orchestrator.Start)
                {
                    ToolbarOrder = 20
                },
                new UiCommandDefinition(AppText.T("web.toolbox.list.clear"), "Ctrl+L", "clear_all", allSurfaces, UiCommandMenuSection.File, busy || this.IsListEmpty, this.Orchestrator.Clear)
                {
                    SecondaryToolbar = true,
                    ToolbarOrder = 15
                }
            };
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            base.Dispose();
            this.Orchestrator.OnBatchCompleted -= this.HandleBatchCompleted;
        }

        #endregion

        #region Metodi protected

        /// <inheritdoc />
        protected override void OnInitialized()
        {
            base.OnInitialized();
            this.Orchestrator.OnBatchCompleted += this.HandleBatchCompleted;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Riassume in un toast l'esito del lotto: riuscito se tutto è riuscito senza avvisi, errore se nulla è riuscito, avviso negli
        /// altri casi
        /// </summary>
        /// <param name="summary">Riepilogo del lotto</param>
        private void HandleBatchCompleted(ToolboxBatchSummary summary)
        {
            this.InvokeAsync(async () =>
            {
                NotificationSeverity severity = summary.AllSucceeded ? NotificationSeverity.Success : summary.NothingSucceeded ? NotificationSeverity.Error : NotificationSeverity.Warning;
                this.Notify(severity, AppText.T(this.Orchestrator.TextPrefix + (summary.Stopped ? ".notifyStoppedTitle" : ".notifyTitle")), summary.Describe());
                await this.OnStateChanged.InvokeAsync();
            });
        }

        #endregion

        #region Proprietà

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
