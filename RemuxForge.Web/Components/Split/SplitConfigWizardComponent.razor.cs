using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Radzen;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting;
using RemuxForge.Web.Components.Remux;
using RemuxForge.Web.Components.Shared;
using RemuxForge.Web.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RemuxForge.Web.Components.Split
{
    /// <summary>
    /// Wizard di configurazione Split: Sorgente, Taglio, Nomi dei file, Destinazione e Riepilogo
    /// </summary>
    public partial class SplitConfigWizardComponent : IWizardFieldHost
    {
        #region Costanti

        /// <summary>Chiavi dei passi, nell'ordine della barra laterale</summary>
        private static readonly string[] Sections = { "source", "cut", "naming", "destination", "summary" };

        /// <summary>Indice del Riepilogo</summary>
        private const int LastSection = 4;

        /// <summary>Allineamenti nell'ordine del menu</summary>
        private static readonly MkvSplitSnapMode[] SnapModes = { MkvSplitSnapMode.Nearest, MkvSplitSnapMode.Before, MkvSplitSnapMode.After, MkvSplitSnapMode.Off };

        #endregion

        #region Parametri

        /// <summary>Opzioni Split correnti</summary>
        [Parameter] public Options Options { get; set; }

        /// <summary>Modulo JS per focus del dialog e dei campi</summary>
        [Parameter] public IJSObjectReference JsModule { get; set; }

        /// <summary>Opzioni del dialog ospite</summary>
        [Parameter] public DialogOptions HostOptions { get; set; }

        /// <summary>Applica le opzioni e avvia la preparazione dei piani; restituisce l'errore, vuoto se riuscito</summary>
        [Parameter] public Func<Options, string> ApplyConfiguration { get; set; }

        [Inject] private DialogService DialogService { get; set; }

        #endregion

        #region Variabili di classe

        /// <summary>Errori correnti, per campo e passo</summary>
        private List<PipelineInitializationIssue> _errors = new List<PipelineInitializationIssue>();

        /// <summary>Riferimenti delle card della modalità</summary>
        private readonly ElementReference[] _modeCardRefs = new ElementReference[SplitCutModes.Cards.Length];

        /// <summary>Contenitore del wizard</summary>
        private ElementReference _content;

        /// <summary>Sessione di focus del dialog</summary>
        private Task<IJSObjectReference> _focusSession;

        /// <summary>Passo aperto</summary>
        private int _section;

        /// <summary>Titolo e testo del pannello help</summary>
        private string _helpTitle = "", _helpText = "";

        /// <summary>Applicazione in corso</summary>
        private bool _applying;

        /// <summary>Componente rilasciato</summary>
        private bool _disposed;

        /// <summary>Card da mettere a fuoco dopo il render</summary>
        private int? _focusModeCard;

        /// <summary>Campo da mettere a fuoco dopo il render</summary>
        private string _focusField;

        #endregion

        #region Proprietà

        /// <summary>Bozza della configurazione</summary>
        public SplitConfigurationDraft Draft { get; private set; }

        /// <summary>Tipo di righe della modalità corrente</summary>
        private string RuleMode => SplitConfigurationDraft.RuleMode(this.Draft.CutMode);

        /// <summary>Campo validato della modalità a righe</summary>
        private string RuleField => SplitConfigurationDraft.RuleField(this.Draft.CutMode);

        /// <summary>Chiave di etichetta e help della modalità a righe</summary>
        private string RuleHelpKey => this.Draft.CutMode switch
        {
            SplitCutModes.PATTERN => "pattern",
            SplitCutModes.RANGES => "ranges",
            _ => "splitAt"
        };

        /// <summary>Schema predefinito della modalità corrente</summary>
        private string DefaultTemplate => MkvSplitSegmentService.DefaultTemplate(SplitCutModes.CoreMode(this.Draft.CutMode));

        /// <summary>Il riquadro in alto elenca gli errori degli altri passi e quelli generali</summary>
        private IEnumerable<PipelineInitializationIssue> AlertErrors => this._errors.Where(e => IssueSection(e) != this._section || string.IsNullOrEmpty(e.Field));

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Mostra nel pannello help la voce del campo: il titolo è l'etichetta del campo
        /// </summary>
        /// <param name="key">Chiave del campo</param>
        public void SetHelp(string key)
        {
            if (key == "mode" && !string.IsNullOrEmpty(this.Draft.CutMode))
            {
                this.SetModeHelp(this.Draft.CutMode);
                return;
            }
            this._helpTitle = L(key);
            this._helpText = T("help." + key);
            if (!this._disposed)
                _ = this.InvokeAsync(this.StateHasChanged);
        }

        /// <summary>
        /// Errori da mostrare sotto il campo nel passo aperto
        /// </summary>
        /// <param name="field">Campo validato</param>
        /// <returns>Errori del campo</returns>
        public IEnumerable<PipelineInitializationIssue> FieldErrors(string field)
        {
            return this._errors.Where(e => !string.IsNullOrEmpty(field) && e.Field == field && IssueSection(e) == this._section);
        }

        /// <summary>
        /// Rilascia la sessione di focus
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            this._disposed = true;
            if (this._focusSession == null)
                return;
            try
            {
                IJSObjectReference session = await this._focusSession;
                await session.InvokeVoidAsync("dispose");
                await session.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
            catch (ObjectDisposedException) { }
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Prepara la bozza e il dialog ospite
        /// </summary>
        protected override void OnInitialized()
        {
            this.Draft = new SplitConfigurationDraft(this.Options);
            if (this.HostOptions != null)
            {
                this.HostOptions.CanClose = () => Task.FromResult(!this._applying);
                this.HostOptions.AutoFocusFirstElement = this.JsModule == null;
            }
            this.SetHelp("source");
        }

        /// <summary>
        /// Focus iniziale, della card scelta con le frecce e del campo indicato da Modifica
        /// </summary>
        /// <param name="firstRender">True al primo render</param>
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && this.JsModule != null)
            {
                this._focusSession = this.JsModule.InvokeAsync<IJSObjectReference>("focusRemuxDialog", this._content).AsTask();
                await this._focusSession;
            }
            if (this._focusModeCard is int card)
            {
                this._focusModeCard = null;
                await this._modeCardRefs[card].FocusAsync();
            }
            if (this._focusField is string field && this.JsModule != null)
            {
                this._focusField = null;
                await this.JsModule.InvokeAsync<bool>("focusRemuxField", this._content, field);
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>Testo localizzato del wizard</summary>
        private static string T(string key) => AppText.T("web.splitWizard." + key);

        /// <summary>Etichetta di un campo, la stessa usata come titolo del suo help</summary>
        private static string L(string key) => T("label." + key);

        /// <summary>Valore di aria-invalid del campo</summary>
        private string Invalid(string field) => this.FieldErrors(field).Any() ? "true" : null;

        /// <summary>Passo a cui appartiene un errore, -1 per gli errori generali</summary>
        private static int IssueSection(PipelineInitializationIssue issue) => issue.Section switch
        {
            SplitConfigurationDraft.SECTION_SOURCE => 0,
            SplitConfigurationDraft.SECTION_CUT => 1,
            SplitConfigurationDraft.SECTION_NAMING => 2,
            SplitConfigurationDraft.SECTION_DESTINATION => 3,
            _ => -1
        };

        /// <summary>True se il passo ha errori aperti</summary>
        private bool SectionHasErrors(int index) => this._errors.Any(e => IssueSection(e) == index);

        /// <summary>
        /// Rivalida un passo e ne sostituisce gli errori; il disco si legge solo per Sorgente e Destinazione
        /// </summary>
        /// <param name="section">Passo da rivalidare</param>
        private void RevalidateSection(int section)
        {
            bool checkDisk = section == 0 || section == 3;
            List<PipelineInitializationIssue> errors = this.Draft.Validate(checkDisk);
            this._errors.RemoveAll(e => IssueSection(e) == section);
            this._errors.AddRange(errors.Where(e => IssueSection(e) == section));
        }

        /// <summary>
        /// Applica una modifica; gli errori del passo aperto seguono subito la correzione
        /// </summary>
        /// <param name="change">Modifica da applicare</param>
        private void Edit(Action change)
        {
            if (this._applying)
                return;
            change();
            this._errors.RemoveAll(e => IssueSection(e) < 0);
            if (this.SectionHasErrors(this._section))
                this.RevalidateSection(this._section);
        }

        /// <summary>
        /// Cambia sorgente: con un file la ricerca nelle sottocartelle non si applica e si spegne
        /// </summary>
        /// <param name="value">Percorso scritto</param>
        private void ChangeSource(string value) => this.Edit(() =>
        {
            this.Draft.SetSource(value);
            if (this.Draft.SourceIsFile)
                this.Draft.Recursive = false;
        });

        /// <summary>
        /// Sceglie la modalità di taglio
        /// </summary>
        /// <param name="mode">Modalità</param>
        private void SetCutMode(string mode) => this.Edit(() =>
        {
            this.Draft.SetCutMode(mode);
            // I campi della modalità precedente non esistono più: i loro errori non vanno trascinati
            this._errors.RemoveAll(e => IssueSection(e) == 1 && e.Field != "CutMode" && e.Field != "Snap");
            this.SetModeHelp(mode);
        });

        /// <summary>
        /// Help della modalità: titolo della card e descrizione estesa
        /// </summary>
        /// <param name="mode">Modalità</param>
        private void SetModeHelp(string mode)
        {
            this._helpTitle = SplitCutModes.Label(mode);
            this._helpText = T("help.mode." + mode);
            if (!this._disposed)
                _ = this.InvokeAsync(this.StateHasChanged);
        }

        /// <summary>
        /// Tabindex delle card: entra nel gruppo sulla card scelta, oppure sulla prima
        /// </summary>
        /// <param name="index">Indice della card</param>
        /// <returns>0 per la card raggiungibile con Tab, -1 per le altre</returns>
        private int ModeCardTabIndex(int index)
        {
            int selected = Array.FindIndex(SplitCutModes.Cards, card => card.Mode == this.Draft.CutMode);
            return index == Math.Max(0, selected) ? 0 : -1;
        }

        /// <summary>
        /// Frecce sulle card: si comportano come un gruppo di radio button
        /// </summary>
        /// <param name="e">Tasto premuto</param>
        /// <param name="index">Card con il focus</param>
        private void ModeCardKey(KeyboardEventArgs e, int index)
        {
            int step = e.Key switch { "ArrowRight" or "ArrowDown" => 1, "ArrowLeft" or "ArrowUp" => -1, _ => 0 };
            if (step == 0 || this._applying)
                return;
            int next = (index + step + SplitCutModes.Cards.Length) % SplitCutModes.Cards.Length;
            this.SetCutMode(SplitCutModes.Cards[next].Mode);
            this._focusModeCard = next;
        }

        /// <summary>
        /// Etichetta dell'allineamento
        /// </summary>
        /// <param name="snap">Allineamento</param>
        /// <returns>Testo localizzato</returns>
        private static string SnapLabel(MkvSplitSnapMode snap) => AppText.T(snap switch
        {
            MkvSplitSnapMode.Before => "web.config.snap.before",
            MkvSplitSnapMode.After => "web.config.snap.after",
            MkvSplitSnapMode.Off => "web.config.snap.off",
            _ => "web.config.snap.nearest"
        });

        /// <summary>
        /// Apre un passo
        /// </summary>
        /// <param name="index">Passo</param>
        private void Navigate(int index)
        {
            if (index < 0 || index >= Sections.Length || this._applying)
                return;
            this._section = index;
            this.SetHelp(index switch { 0 => "source", 1 => "mode", 2 => "template", 3 => "outputDir", _ => "summary" });
        }

        /// <summary>
        /// Porta al passo e al campo dell'errore
        /// </summary>
        /// <param name="issue">Errore scelto</param>
        private void GoToError(PipelineInitializationIssue issue)
        {
            int section = IssueSection(issue);
            if (this._applying || section < 0)
                return;
            this._section = section;
            this.SetHelp("errors");
            if (!string.IsNullOrEmpty(issue.Field))
                this._focusField = issue.Field;
        }

        /// <summary>
        /// Avanti valida il passo aperto; dal Riepilogo valida tutto e prepara i piani
        /// </summary>
        private void Next()
        {
            if (this._applying)
                return;
            if (this._section < LastSection)
            {
                this.RevalidateSection(this._section);
                if (!this.SectionHasErrors(this._section))
                    this.Navigate(this._section + 1);
                return;
            }

            this._errors = this.Draft.Validate(true);
            if (this._errors.Count > 0)
            {
                this.SetHelp("errors");
                return;
            }

            Options options = this.Draft.BuildOptions();
            OptionsValidationResult validation = OptionsValidator.Validate(options, false, false);
            if (!validation.IsValid)
            {
                this._errors = validation.Errors.Select(message => new PipelineInitializationIssue("options", message, "", "Configuration")).ToList();
                this.SetHelp("errors");
                return;
            }

            this._applying = true;
            try
            {
                string error = this.ApplyConfiguration?.Invoke(options) ?? "";
                if (!string.IsNullOrEmpty(error))
                {
                    this._errors = new List<PipelineInitializationIssue> { new PipelineInitializationIssue("apply", error, "", "Configuration") };
                    this.SetHelp("errors");
                    return;
                }
            }
            finally
            {
                this._applying = false;
            }
            this.DialogService.Close(true);
        }

        /// <summary>
        /// Sceglie sorgente o destinazione con il browser interno
        /// </summary>
        /// <param name="source">True per la sorgente, false per la destinazione</param>
        private async Task BrowseAsync(bool source)
        {
            if (this._applying)
                return;
            string path = await AppDialogs.BrowseAsync(this.DialogService, this.JsModule, source ? this.Draft.Source : this.Draft.OutputDir, source, true, this.Draft.ExtensionList());
            if (path == null || this._disposed)
                return;
            if (source)
                this.ChangeSource(path);
            else
                this.Edit(() => this.Draft.OutputDir = path);
        }

        /// <summary>
        /// Chiude senza applicare
        /// </summary>
        private void Cancel()
        {
            if (this._applying)
                return;
            this.DialogService.Close(false);
        }

        /// <summary>
        /// Riepilogo per passo: etichetta e valore leggibile
        /// </summary>
        /// <returns>Righe del riepilogo</returns>
        private IEnumerable<(int Section, string Label, string Value)> SummaryRows()
        {
            string yes = AppText.T("web.common.yes");
            string no = AppText.T("web.common.no");
            string source = string.IsNullOrWhiteSpace(this.Draft.Source) ? "—" : this.Draft.Source.Trim();
            if (!this.Draft.SourceIsFile)
                source += Environment.NewLine + L("recursive") + ": " + (this.Draft.Recursive ? yes : no) +
                    " · " + L("extensions") + ": " + string.Join(", ", this.Draft.ExtensionList());
            yield return (0, T("section.source"), source);
            yield return (1, T("section.cut"), this.CutSummary() + Environment.NewLine + L("snap") + ": " + SnapLabel(this.Draft.Snap));
            string template = string.IsNullOrWhiteSpace(this.Draft.Template) ? this.DefaultTemplate : this.Draft.Template.Trim();
            yield return (2, T("section.naming"), template + Environment.NewLine + L("startNumber") + ": " + (this.Draft.StartNumber?.ToString() ?? "—"));
            string output = string.IsNullOrWhiteSpace(this.Draft.OutputDir) ? T("placeholder.besideSource") : this.Draft.OutputDir.Trim();
            yield return (3, T("section.destination"), output + Environment.NewLine + L("force") + ": " + (this.Draft.Force ? yes : no));
        }

        /// <summary>
        /// Modalità e parametri della modalità, in forma leggibile
        /// </summary>
        /// <returns>Testo del riepilogo del taglio</returns>
        private string CutSummary()
        {
            if (string.IsNullOrEmpty(this.Draft.CutMode))
                return "—";
            string label = SplitCutModes.Label(this.Draft.CutMode);
            if (this.Draft.CutMode == SplitCutModes.CHAPTERS_PER_EPISODE)
                return label + " · " + L("chaptersPerEpisode") + ": " + (this.Draft.ChaptersPerEpisode?.ToString() ?? "—");
            if (this.Draft.CutMode == SplitCutModes.TRIM)
            {
                string start = string.IsNullOrWhiteSpace(this.Draft.TrimStart) ? T("placeholder.fileStart") : this.Draft.TrimStart.Trim();
                string end = string.IsNullOrWhiteSpace(this.Draft.TrimEnd) ? T("placeholder.fileEnd") : this.Draft.TrimEnd.Trim();
                return label + " · " + start + " – " + end;
            }
            if (this.RuleMode.Length == 0)
                return label;
            List<SplitRuleRow> rows = SplitRuleSyntax.Parse(this.RuleMode, this.Draft.RuleValue());
            string values = string.Join(", ", rows.Select(row => this.RuleMode == SplitRuleSyntax.RANGES ? row.Start + " – " + row.End : row.Start));
            return label + " · " + (values.Length == 0 ? "—" : values);
        }

        #endregion
    }
}
