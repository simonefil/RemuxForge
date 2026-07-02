using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Metadata;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using RemuxForge.Web.Components.Shared;
using RemuxForge.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RemuxForge.Web.Components.Pages
{
    /// <summary>
    /// Pagina principale Dashboard - dashboard operativa
    /// </summary>
    public partial class Dashboard : IAsyncDisposable
    {
        #region Servizi iniettati

        /// <summary>
        /// Runtime JS per interop
        /// </summary>
        [Inject]
        private IJSRuntime JsRuntime { get; set; }

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Lista record episodi correnti (letta dall'orchestratore)
        /// </summary>
        private List<FileProcessingRecord> _records;

        /// <summary>
        /// Lista record split correnti
        /// </summary>
        private List<MkvSplitRecord> _splitRecords;

        /// <summary>
        /// Lista record metadata correnti
        /// </summary>
        private List<MkvMetadataRecord> _metadataRecords;

        /// <summary>
        /// Lista preset metadata disponibili
        /// </summary>
        private List<string> _metadataPresetFiles;

        /// <summary>
        /// Record selezionato per il pannello dettaglio
        /// </summary>
        private FileProcessingRecord _selectedRecord;

        /// <summary>
        /// Record split selezionato
        /// </summary>
        private MkvSplitRecord _selectedSplitRecord;

        /// <summary>
        /// Record metadata selezionato
        /// </summary>
        private MkvMetadataRecord _selectedMetadataRecord;

        /// <summary>
        /// Indici episodi selezionati in modalità multi-select
        /// </summary>
        private List<int> _selectedIndices;

        /// <summary>
        /// Anchor per selezione range con Shift
        /// </summary>
        private int _selectionAnchorIndex;

        /// <summary>
        /// Tema corrente
        /// </summary>
        private string _currentTheme;

        /// <summary>
        /// Lingua corrente
        /// </summary>
        private string _currentLanguage;

        /// <summary>
        /// Modalità corrente UI
        /// </summary>
        private string _currentMode;

        /// <summary>
        /// Flag: mostra dialog configurazione
        /// </summary>
        private bool _showConfig;

        /// <summary>
        /// Flag: mostra dialog preset metadata
        /// </summary>
        private bool _showMetadataPreset;

        /// <summary>
        /// Flag: mostra browser path metadata
        /// </summary>
        private bool _showMetadataPathBrowse;

        /// <summary>
        /// Campo metadata in modifica tramite browser path
        /// </summary>
        private int _metadataBrowseFieldIndex;

        /// <summary>
        /// Percorso iniziale browser path metadata
        /// </summary>
        private string _metadataBrowseInitialPath;

        /// <summary>
        /// True se il browser metadata deve mostrare i file
        /// </summary>
        private bool _metadataBrowseShowFiles;

        /// <summary>
        /// True se il browser metadata permette la selezione della cartella corrente
        /// </summary>
        private bool _metadataBrowseAllowCurrentFolderSelection;

        /// <summary>
        /// Flag: mostra dettaglio metadata mappato
        /// </summary>
        private bool _showMetadataMappedInfo;

        /// <summary>
        /// True se il dettaglio mappato deve mostrare la simulazione
        /// </summary>
        private bool _metadataMappedInfoSimulated;

        /// <summary>
        /// Flag: mostra finestra rename metadata
        /// </summary>
        private bool _showMetadataRename;

        /// <summary>
        /// Flag: mostra dialog percorsi tool
        /// </summary>
        private bool _showToolPaths;

        /// <summary>
        /// Flag: mostra dialog impostazioni audio
        /// </summary>
        private bool _showAudioSettings;

        /// <summary>
        /// Flag: mostra dialog impostazioni avanzate
        /// </summary>
        private bool _showAdvancedSettings;

        /// <summary>
        /// Flag: mostra dialog delay
        /// </summary>
        private bool _showDelay;

        /// <summary>
        /// Flag: mostra dialog profili encoding
        /// </summary>
        private bool _showEncodingProfiles;

        /// <summary>
        /// Flag: mostra dialog pipeline
        /// </summary>
        private bool _showPipeline;

        /// <summary>
        /// Flag: mostra dialog info
        /// </summary>
        private bool _showInfo;

        /// <summary>
        /// Flag: mostra context menu episodio
        /// </summary>
        private bool _showContextMenu;

        /// <summary>
        /// Voci del context menu corrente
        /// </summary>
        private List<string> _contextMenuItems;

        /// <summary>
        /// Azioni corrispondenti alle voci del context menu
        /// </summary>
        private List<Action> _contextMenuActions;

        /// <summary>
        /// Voce attiva nel context menu per navigazione tastiera
        /// </summary>
        private int _contextMenuSelectedIndex;

        /// <summary>
        /// Coordinata X del context menu (pixel dal bordo sinistro viewport)
        /// </summary>
        private double _contextMenuX;

        /// <summary>
        /// Coordinata Y del context menu (pixel dal bordo superiore viewport)
        /// </summary>
        private double _contextMenuY;

        /// <summary>
        /// Flag: mostra dialog mediainfo
        /// </summary>
        private bool _showMediaInfo;

        /// <summary>
        /// Titolo dialog mediainfo
        /// </summary>
        private string _mediaInfoTitle;

        /// <summary>
        /// Report mediainfo testuale
        /// </summary>
        private string _mediaInfoReport;

        /// <summary>
        /// Modulo JS interop importato
        /// </summary>
        private IJSObjectReference _jsModule;

        /// <summary>
        /// Riferimento .NET per callback da JS
        /// </summary>
        private DotNetObjectReference<Dashboard> _dotNetRef;

        /// <summary>
        /// Riferimento menu bar per navigazione tastiera
        /// </summary>
        private MenuBarComponent _menuBar;

        #endregion

        #region Lifecycle

        /// <summary>
        /// Inizializzazione componente
        /// </summary>
        protected override void OnInitialized()
        {
            this._currentTheme = AppSettingsService.Instance.Settings.Ui.Theme;
            this._currentLanguage = AppText.NormalizeLanguage(AppSettingsService.Instance.Settings.Ui.Language);
            if (this._currentLanguage.Length == 0)
            {
                this._currentLanguage = AppText.LANG_EN;
            }
            this._currentMode = AppSettingsService.Instance.Settings.Ui.LastMode;
            if (this._currentMode != Options.MODE_REMUX && this._currentMode != Options.MODE_SPLIT && this._currentMode != Options.MODE_METADATA)
            {
                this._currentMode = Options.MODE_REMUX;
            }
            this._showConfig = false;
            this._showMetadataPreset = false;
            this._showMetadataPathBrowse = false;
            this._metadataBrowseFieldIndex = -1;
            this._metadataBrowseInitialPath = "";
            this._metadataBrowseShowFiles = false;
            this._metadataBrowseAllowCurrentFolderSelection = true;
            this._showMetadataMappedInfo = false;
            this._metadataMappedInfoSimulated = false;
            this._showMetadataRename = false;
            this._showToolPaths = false;
            this._showAudioSettings = false;
            this._showAdvancedSettings = false;
            this._showDelay = false;
            this._showEncodingProfiles = false;
            this._showPipeline = false;
            this._showInfo = false;
            this._showContextMenu = false;
            this._contextMenuItems = new List<string>();
            this._contextMenuActions = new List<Action>();
            this._showMediaInfo = false;
            this._mediaInfoTitle = "";
            this._mediaInfoReport = "";
            this._selectedIndices = new List<int>();
            this._selectionAnchorIndex = -1;
            this._contextMenuSelectedIndex = 0;

            // Carica stato corrente dall'orchestratore
            this._records = this.Orchestrator.GetRecords();
            this._splitRecords = this.SplitOrchestrator.GetRecords();
            this._metadataRecords = this.MetadataOrchestrator.GetRecords();
            this._metadataPresetFiles = this.MetadataOrchestrator.GetPresetFiles();
            this.SyncSelectedFromOrchestrator();
            this.SyncSelectedFromSplitOrchestrator();
            this.SyncSelectedFromMetadataOrchestrator();

            // Sottoscrivi eventi orchestratore
            this.Orchestrator.OnLog += this.HandleLog;
            this.Orchestrator.OnRecordsChanged += this.HandleRecordsChanged;
            this.Orchestrator.OnProgressChanged += this.HandleProgressChanged;
            this.SplitOrchestrator.OnLog += this.HandleLog;
            this.SplitOrchestrator.OnRecordsChanged += this.HandleSplitRecordsChanged;
            this.SplitOrchestrator.OnProgressChanged += this.HandleProgressChanged;
            this.MetadataOrchestrator.OnLog += this.HandleLog;
            this.MetadataOrchestrator.OnRecordsChanged += this.HandleMetadataRecordsChanged;
            this.MetadataOrchestrator.OnProgressChanged += this.HandleProgressChanged;
        }

        /// <summary>
        /// Importa modulo JS e inizializza tastiera e tema dopo il primo render
        /// </summary>
        /// <param name="firstRender">True se primo render</param>
        protected override async System.Threading.Tasks.Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                // Importa modulo JS interop
                this._jsModule = await this.JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");

                // Cattura tastiera via JS
                this._dotNetRef = DotNetObjectReference.Create(this);
                await this._jsModule.InvokeVoidAsync("captureKeyboard", this._dotNetRef);

                // Carica tema da AppSettings e applica via JS
                this._currentTheme = AppSettingsService.Instance.Settings.Ui.Theme;
                await this._jsModule.InvokeVoidAsync("setTheme", AppSettingsService.Instance.Settings.Ui.Theme);
                await this._jsModule.InvokeVoidAsync("setLanguage", this._currentLanguage);
                this.StateHasChanged();
            }
        }

        /// <summary>
        /// Cleanup sottoscrizioni e risorse JS
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            // Rimuovi sottoscrizioni eventi
            if (this.Orchestrator != null)
            {
                this.Orchestrator.OnLog -= this.HandleLog;
                this.Orchestrator.OnRecordsChanged -= this.HandleRecordsChanged;
                this.Orchestrator.OnProgressChanged -= this.HandleProgressChanged;
                this.SplitOrchestrator.OnLog -= this.HandleLog;
                this.SplitOrchestrator.OnRecordsChanged -= this.HandleSplitRecordsChanged;
                this.SplitOrchestrator.OnProgressChanged -= this.HandleProgressChanged;
                this.MetadataOrchestrator.OnLog -= this.HandleLog;
                this.MetadataOrchestrator.OnRecordsChanged -= this.HandleMetadataRecordsChanged;
                this.MetadataOrchestrator.OnProgressChanged -= this.HandleProgressChanged;
            }

            // Dispose riferimento .NET per JS interop
            if (this._dotNetRef != null)
            {
                this._dotNetRef.Dispose();
            }

            // Rilascia handler tastiera e dispose modulo JS
            if (this._jsModule != null)
            {
                try
                {
                    await this._jsModule.InvokeVoidAsync("releaseKeyboard");
                }
                catch
                {
                    // Ignora errori durante dispose (circuito chiuso)
                }

                try
                {
                    await this._jsModule.DisposeAsync();
                }
                catch
                {
                    // Ignora errori durante dispose (circuito chiuso)
                }
            }
        }

        #endregion

        #region Gestori eventi

        /// <summary>
        /// Gestisce messaggio log dall'orchestratore
        /// </summary>
        /// <param name="message">Messaggio log</param>
        private void HandleLog(string message)
        {
            // Il log è già accumulato nell'orchestratore, forza solo il re-render
            this.InvokeAsync(() => this.StateHasChanged());
        }

        /// <summary>
        /// Gestisce aggiornamento record dall'orchestratore
        /// </summary>
        private void HandleRecordsChanged()
        {
            this.InvokeAsync(() =>
            {
                this._records = this.Orchestrator.GetRecords();
                this.NormalizeSelection();
                this.SyncSelectedFromOrchestrator();
                this.StateHasChanged();
            });
        }

        /// <summary>
        /// Gestisce aggiornamento record split
        /// </summary>
        private void HandleSplitRecordsChanged()
        {
            this.InvokeAsync(() =>
            {
                this._splitRecords = this.SplitOrchestrator.GetRecords();
                this.SyncSelectedFromSplitOrchestrator();
                this.StateHasChanged();
            });
        }

        /// <summary>
        /// Gestisce aggiornamento record metadata
        /// </summary>
        private void HandleMetadataRecordsChanged()
        {
            this.InvokeAsync(() =>
            {
                this._metadataRecords = this.MetadataOrchestrator.GetRecords();
                this.SyncSelectedFromMetadataOrchestrator();
                this.StateHasChanged();
            });
        }

        /// <summary>
        /// Gestisce aggiornamento avanzamento dall'orchestratore
        /// </summary>
        private void HandleProgressChanged()
        {
            this.InvokeAsync(() => this.StateHasChanged());
        }

        /// <summary>
        /// Gestisce scorciatoie da tastiera (invocato da JS interop)
        /// </summary>
        /// <param name="key">Tasto premuto</param>
        /// <param name="ctrl">Flag Ctrl</param>
        /// <param name="shift">Flag Shift</param>
        /// <param name="alt">Flag Alt</param>
        [JSInvokable("OnKeyDown")]
        public void HandleKeyDown(string key, bool ctrl, bool shift, bool alt)
        {
            if (this.IsBlockingDialogOpen())
            {
                if (key == "Escape")
                {
                    this.CloseAllDialogs();
                    this.StateHasChanged();
                }

                return;
            }

            if (this._showContextMenu && this.HandleContextMenuKey(key))
            {
                this.StateHasChanged();
                return;
            }

            if (this._menuBar != null && this._menuBar.HandleKeyboardKey(key, ctrl, shift, alt))
            {
                this.StateHasChanged();
                return;
            }

            if (this._currentMode == Options.MODE_SPLIT)
            {
                if (key == "F2") { this.ShowConfig(); }
                else if (key == "F5") { this.DoScan(); }
                else if (key == "F10") { this.DoMergeAll(); }
                else if (key == "F12") { this.DoStop(); }
                else if (key == "Escape") { this.CloseAllDialogs(); }
                else if (key == "ArrowUp") { this.MoveSplitSelection(-1); }
                else if (key == "ArrowDown") { this.MoveSplitSelection(1); }
                else if (key == "Home") { this.SelectSplitRow(0); }
                else if (key == "End") { this.SelectSplitRow(this._splitRecords.Count - 1); }
            }
            else if (this._currentMode == Options.MODE_METADATA)
            {
                if (key == "F2") { this.ShowMetadataInputPicker(); }
                else if (key == "F3") { this.ShowMetadataPreset(); }
                else if (key == "F5") { this.DoScan(); }
                else if (key == "F6") { this.DoAnalyzeAll(); }
                else if (key == "F9") { this.DoMergeSelected(); }
                else if (key == "F10") { this.DoMergeAll(); }
                else if (key == "F11") { this.ShowMetadataRename(); }
                else if (key == "F12") { this.DoStop(); }
                else if (ctrl && string.Equals(key, "l", StringComparison.OrdinalIgnoreCase)) { this.DoClear(); }
                else if (key == "Escape") { this.CloseAllDialogs(); }
                else if (key == "ArrowUp") { this.MoveMetadataSelection(-1); }
                else if (key == "ArrowDown") { this.MoveMetadataSelection(1); }
                else if (key == "Home") { this.SelectMetadataRow(0); }
                else if (key == "End") { this.SelectMetadataRow(this._metadataRecords.Count - 1); }
            }
            else
            {
                if (key == "F2") { this.ShowConfig(); }
                else if (key == "F5") { this.DoScan(); }
                else if (key == "F6") { this.DoAnalyzeSelected(); }
                else if (key == "F7") { this.DoAnalyzeAll(); }
                else if (key == "F8") { this.DoToggleSkip(); }
                else if (key == "F9") { this.DoMergeSelected(); }
                else if (key == "F10") { this.DoMergeAll(); }
                else if (key == "F12") { this.DoStop(); }
                else if (key == "Enter") { this.ShowContextMenuForSelected(); }
                else if (key == "Escape") { this.CloseAllDialogs(); }
                else if (ctrl && string.Equals(key, "a", StringComparison.OrdinalIgnoreCase)) { this.SelectAllRows(); }
                else if (key == "ArrowUp") { this.MoveSelection(-1, shift, ctrl); }
                else if (key == "ArrowDown") { this.MoveSelection(1, shift, ctrl); }
                else if (key == "Home") { this.SelectIndexFromKeyboard(0, shift, ctrl); }
                else if (key == "End") { this.SelectIndexFromKeyboard(this._records.Count - 1, shift, ctrl); }
                else if (key == "PageUp") { this.MoveSelection(-10, shift, ctrl); }
                else if (key == "PageDown") { this.MoveSelection(10, shift, ctrl); }
                else if (key == " ") { this.ToggleFocusedSelection(); }
            }

            this.StateHasChanged();
        }

        /// <summary>
        /// Seleziona riga nella tabella episodi
        /// </summary>
        /// <param name="args">Indice riga e modifier tastiera</param>
        private void SelectRow((int Index, bool Ctrl, bool Shift) args)
        {
            this.ApplyRowSelection(args.Index, args.Ctrl, args.Shift);
        }

        /// <summary>
        /// Seleziona riga split
        /// </summary>
        /// <param name="index">Indice riga</param>
        private void SelectSplitRow(int index)
        {
            this.SplitOrchestrator.SelectedIndex = index;
            if (index >= 0 && index < this._splitRecords.Count)
            {
                this._selectedSplitRecord = this._splitRecords[index];
                this.ScrollSplitRowIntoView(index);
            }
            else
            {
                this._selectedSplitRecord = null;
            }
        }

        /// <summary>
        /// Seleziona riga metadata
        /// </summary>
        /// <param name="index">Indice riga</param>
        private void SelectMetadataRow(int index)
        {
            this.MetadataOrchestrator.SelectedIndex = index;
            if (index >= 0 && index < this._metadataRecords.Count)
            {
                this._selectedMetadataRecord = this._metadataRecords[index];
                this.ScrollMetadataRowIntoView(index);
            }
            else
            {
                this._selectedMetadataRecord = null;
            }
        }

        /// <summary>
        /// Applica selezione mouse stile file explorer
        /// </summary>
        /// <param name="index">Indice riga</param>
        /// <param name="ctrl">True se selezione additiva/toggle</param>
        /// <param name="shift">True se selezione range</param>
        private void ApplyRowSelection(int index, bool ctrl, bool shift)
        {
            if (index < 0 || index >= this._records.Count)
            {
                return;
            }

            if (shift)
            {
                if (this._selectionAnchorIndex < 0 || this._selectionAnchorIndex >= this._records.Count)
                {
                    this._selectionAnchorIndex = this.Orchestrator.SelectedIndex >= 0 ? this.Orchestrator.SelectedIndex : index;
                }

                if (!ctrl)
                {
                    this._selectedIndices.Clear();
                }

                this.AddSelectionRange(this._selectionAnchorIndex, index);
            }
            else if (ctrl)
            {
                if (this.IsRowSelected(index))
                {
                    this._selectedIndices.Remove(index);
                }
                else
                {
                    this._selectedIndices.Add(index);
                    this.SortSelectedIndices();
                }

                this._selectionAnchorIndex = index;
            }
            else
            {
                this._selectedIndices.Clear();
                this._selectedIndices.Add(index);
                this._selectionAnchorIndex = index;
            }

            this.SetFocusedRow(index);
        }

        /// <summary>
        /// Aggiorna focus riga mantenendo invariata la selezione multi
        /// </summary>
        /// <param name="index">Indice riga</param>
        private void SetFocusedRow(int index)
        {
            this.Orchestrator.SelectedIndex = index;

            if (index >= 0 && index < this._records.Count)
            {
                this._selectedRecord = this._records[index];
                this.ScrollEpisodeRowIntoView(index);
            }
            else
            {
                this._selectedRecord = null;
            }
        }

        /// <summary>
        /// Muove la selezione da tastiera
        /// </summary>
        /// <param name="delta">Spostamento relativo</param>
        /// <param name="shift">True se estende range</param>
        /// <param name="ctrl">True se muove solo focus</param>
        private void MoveSelection(int delta, bool shift, bool ctrl)
        {
            int currentIndex = this.Orchestrator.SelectedIndex;
            int targetIndex;

            if (this._records.Count == 0)
            {
                return;
            }

            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            targetIndex = currentIndex + delta;
            this.SelectIndexFromKeyboard(targetIndex, shift, ctrl);
        }

        /// <summary>
        /// Seleziona un indice da tastiera applicando modifier
        /// </summary>
        /// <param name="index">Indice richiesto</param>
        /// <param name="shift">True se estende range</param>
        /// <param name="ctrl">True se muove solo focus</param>
        private void SelectIndexFromKeyboard(int index, bool shift, bool ctrl)
        {
            int targetIndex = index;

            if (this._records.Count == 0)
            {
                return;
            }

            if (targetIndex < 0) { targetIndex = 0; }
            if (targetIndex >= this._records.Count) { targetIndex = this._records.Count - 1; }

            if (ctrl && !shift)
            {
                this.SetFocusedRow(targetIndex);
                return;
            }

            this.ApplyRowSelection(targetIndex, ctrl, shift);
        }

        /// <summary>
        /// Muove selezione split con frecce
        /// </summary>
        /// <param name="delta">Spostamento relativo</param>
        private void MoveSplitSelection(int delta)
        {
            int currentIndex = this.SplitOrchestrator.SelectedIndex;
            int targetIndex;

            if (this._splitRecords.Count == 0)
            {
                return;
            }

            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            targetIndex = currentIndex + delta;
            if (targetIndex < 0) { targetIndex = 0; }
            if (targetIndex >= this._splitRecords.Count) { targetIndex = this._splitRecords.Count - 1; }
            this.SelectSplitRow(targetIndex);
        }

        /// <summary>
        /// Muove selezione metadata con frecce
        /// </summary>
        /// <param name="delta">Spostamento relativo</param>
        private void MoveMetadataSelection(int delta)
        {
            int currentIndex = this.MetadataOrchestrator.SelectedIndex;
            int targetIndex;

            if (this._metadataRecords.Count == 0)
            {
                return;
            }

            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            targetIndex = currentIndex + delta;
            if (targetIndex < 0) { targetIndex = 0; }
            if (targetIndex >= this._metadataRecords.Count) { targetIndex = this._metadataRecords.Count - 1; }
            this.SelectMetadataRow(targetIndex);
        }

        /// <summary>
        /// Seleziona tutti gli episodi
        /// </summary>
        private void SelectAllRows()
        {
            this._selectedIndices.Clear();
            for (int i = 0; i < this._records.Count; i++)
            {
                this._selectedIndices.Add(i);
            }

            if (this._records.Count > 0 && this.Orchestrator.SelectedIndex < 0)
            {
                this.SetFocusedRow(0);
                this._selectionAnchorIndex = 0;
            }
        }

        /// <summary>
        /// Toggle selezione della riga con focus
        /// </summary>
        private void ToggleFocusedSelection()
        {
            int index = this.Orchestrator.SelectedIndex;
            if (index < 0 || index >= this._records.Count)
            {
                return;
            }

            if (this.IsRowSelected(index))
            {
                this._selectedIndices.Remove(index);
            }
            else
            {
                this._selectedIndices.Add(index);
                this.SortSelectedIndices();
            }

            this._selectionAnchorIndex = index;
        }

        /// <summary>
        /// Aggiunge range selezione inclusivo
        /// </summary>
        private void AddSelectionRange(int startIndex, int endIndex)
        {
            int first = Math.Min(startIndex, endIndex);
            int last = Math.Max(startIndex, endIndex);

            for (int i = first; i <= last; i++)
            {
                if (!this.IsRowSelected(i))
                {
                    this._selectedIndices.Add(i);
                }
            }

            this.SortSelectedIndices();
        }

        /// <summary>
        /// True se una riga è nella selezione multi
        /// </summary>
        private bool IsRowSelected(int index)
        {
            bool result = false;

            for (int i = 0; i < this._selectedIndices.Count; i++)
            {
                if (this._selectedIndices[i] == index)
                {
                    result = true;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Ordina gli indici selezionati
        /// </summary>
        private void SortSelectedIndices()
        {
            this._selectedIndices.Sort();
        }

        /// <summary>
        /// Restituisce gli indici su cui applicare un'azione selezionata
        /// </summary>
        private List<int> GetActionSelectionIndices()
        {
            List<int> result = new List<int>();

            for (int i = 0; i < this._selectedIndices.Count; i++)
            {
                if (this._selectedIndices[i] >= 0 && this._selectedIndices[i] < this._records.Count && !result.Contains(this._selectedIndices[i]))
                {
                    result.Add(this._selectedIndices[i]);
                }
            }

            if (result.Count == 0 && this.Orchestrator.SelectedIndex >= 0 && this.Orchestrator.SelectedIndex < this._records.Count)
            {
                result.Add(this.Orchestrator.SelectedIndex);
            }

            result.Sort();
            return result;
        }

        /// <summary>
        /// Rimuove selezioni non più valide dopo refresh record
        /// </summary>
        private void NormalizeSelection()
        {
            for (int i = this._selectedIndices.Count - 1; i >= 0; i--)
            {
                if (this._selectedIndices[i] < 0 || this._selectedIndices[i] >= this._records.Count)
                {
                    this._selectedIndices.RemoveAt(i);
                }
            }

            if (this._selectionAnchorIndex >= this._records.Count)
            {
                this._selectionAnchorIndex = this._records.Count - 1;
            }
        }

        /// <summary>
        /// True se c'è un dialog modale aperto che deve bloccare scorciatoie tabella
        /// </summary>
        private bool IsBlockingDialogOpen()
        {
            return this._showConfig || this._showMetadataPathBrowse || this._showMetadataPreset || this._showMetadataMappedInfo || this._showMetadataRename || this._showToolPaths || this._showAudioSettings || this._showAdvancedSettings || this._showDelay || this._showEncodingProfiles || this._showPipeline || this._showInfo || this._showMediaInfo;
        }

        /// <summary>
        /// Gestisce tastiera context menu
        /// </summary>
        /// <param name="key">Tasto</param>
        /// <returns>True se gestito</returns>
        private bool HandleContextMenuKey(string key)
        {
            bool result = false;

            if (key == "Escape")
            {
                this.CloseContextMenu();
                result = true;
            }
            else if (key == "ArrowDown")
            {
                if (this._contextMenuItems.Count > 0)
                {
                    this._contextMenuSelectedIndex++;
                    if (this._contextMenuSelectedIndex >= this._contextMenuItems.Count) { this._contextMenuSelectedIndex = 0; }
                }

                result = true;
            }
            else if (key == "ArrowUp")
            {
                if (this._contextMenuItems.Count > 0)
                {
                    this._contextMenuSelectedIndex--;
                    if (this._contextMenuSelectedIndex < 0) { this._contextMenuSelectedIndex = this._contextMenuItems.Count - 1; }
                }

                result = true;
            }
            else if (key == "Enter" || key == " ")
            {
                this.HandleContextMenuSelect(this._contextMenuSelectedIndex);
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Scorre la riga episodio selezionata dentro la viewport tabella
        /// </summary>
        /// <param name="index">Indice riga</param>
        private void ScrollEpisodeRowIntoView(int index)
        {
            if (this._jsModule == null)
            {
                return;
            }

            try
            {
                _ = this._jsModule.InvokeVoidAsync("scrollEpisodeRowIntoView", index);
            }
            catch
            {
                // Ignora errori JS se il circuito si sta chiudendo
            }
        }

        /// <summary>
        /// Scorre la riga split selezionata dentro la viewport tabella
        /// </summary>
        /// <param name="index">Indice riga</param>
        private void ScrollSplitRowIntoView(int index)
        {
            if (this._jsModule == null)
            {
                return;
            }

            try
            {
                _ = this._jsModule.InvokeVoidAsync("scrollSplitRowIntoView", index);
            }
            catch
            {
                // Ignora errori JS se il circuito si sta chiudendo
            }
        }

        /// <summary>
        /// Scorre la riga metadata selezionata dentro la viewport tabella
        /// </summary>
        /// <param name="index">Indice riga</param>
        private void ScrollMetadataRowIntoView(int index)
        {
            if (this._jsModule == null)
            {
                return;
            }

            try
            {
                _ = this._jsModule.InvokeVoidAsync("scrollMetadataRowIntoView", index);
            }
            catch
            {
                // Ignora errori JS se il circuito si sta chiudendo
            }
        }

        /// <summary>
        /// Mostra context menu per l'episodio all'indice specificato
        /// </summary>
        /// <param name="args">Tupla (indice riga, clientX, clientY)</param>
        private void ShowContextMenu((int Index, double X, double Y) args)
        {
            if (!this.IsRowSelected(args.Index))
            {
                this.ApplyRowSelection(args.Index, false, false);
            }
            else
            {
                this.SetFocusedRow(args.Index);
            }

            if (this._selectedRecord == null) { return; }

            this._contextMenuX = args.X;
            this._contextMenuY = args.Y;
            this.BuildContextMenu(this._selectedRecord);
            this._contextMenuSelectedIndex = 0;
            this._showContextMenu = true;
        }

        /// <summary>
        /// Mostra context menu per l'episodio selezionato (da Enter)
        /// </summary>
        private void ShowContextMenuForSelected()
        {
            if (this._selectedRecord == null) { return; }

            // Da tastiera: posiziona al centro dello schermo
            this._contextMenuX = 400;
            this._contextMenuY = 300;
            this.BuildContextMenu(this._selectedRecord);
            this._contextMenuSelectedIndex = 0;
            this._showContextMenu = true;
        }

        /// <summary>
        /// Costruisce le voci del context menu in base al record
        /// </summary>
        /// <param name="record">Record episodio</param>
        private void BuildContextMenu(FileProcessingRecord record)
        {
            // Verifica disponibilita' mediainfo
            bool mediaInfoAvailable = (AppSettingsService.Instance.Settings.Tools.MediaInfoPath.Length > 0
                && System.IO.File.Exists(AppSettingsService.Instance.Settings.Tools.MediaInfoPath)
                && MediaInfoProvider.IsCliExecutablePath(AppSettingsService.Instance.Settings.Tools.MediaInfoPath));

            this._contextMenuItems = new List<string>();
            this._contextMenuActions = new List<Action>();

            // Delay: sempre visibile
            this._contextMenuItems.Add(AppText.T("web.context.delay"));
            this._contextMenuActions.Add(() =>
            {
                this._showContextMenu = false;
                this._showDelay = true;
            });

            // MediaInfo sorgente
            if (mediaInfoAvailable && record.SourceFilePath.Length > 0 && System.IO.File.Exists(record.SourceFilePath))
            {
                this._contextMenuItems.Add(AppText.T("web.context.mediaInfoSource"));
                this._contextMenuActions.Add(() => { this.OpenMediaInfo(record.SourceFilePath, AppText.F("web.mediaInfo.sourceTitle", record.SourceFileName)); });
            }

            // MediaInfo lingua
            if (mediaInfoAvailable && record.LangFilePath.Length > 0 && System.IO.File.Exists(record.LangFilePath))
            {
                this._contextMenuItems.Add(AppText.T("web.context.mediaInfoLanguage"));
                this._contextMenuActions.Add(() => { this.OpenMediaInfo(record.LangFilePath, AppText.F("web.mediaInfo.languageTitle", record.LangFileName)); });
            }

            // MediaInfo risultato
            if (mediaInfoAvailable && record.ResultFilePath.Length > 0 && System.IO.File.Exists(record.ResultFilePath))
            {
                this._contextMenuItems.Add(AppText.T("web.context.mediaInfoResult"));
                this._contextMenuActions.Add(() => { this.OpenMediaInfo(record.ResultFilePath, AppText.F("web.mediaInfo.resultTitle", record.ResultFileName)); });
            }
        }

        /// <summary>
        /// Gestisce selezione voce dal context menu
        /// </summary>
        /// <param name="index">Indice voce selezionata</param>
        private void HandleContextMenuSelect(int index)
        {
            this._showContextMenu = false;

            if (index >= 0 && index < this._contextMenuActions.Count)
            {
                this._contextMenuActions[index]();
            }
        }

        /// <summary>
        /// Aggiorna voce attiva del context menu da hover mouse
        /// </summary>
        /// <param name="index">Indice voce attiva</param>
        private void SetContextMenuActiveIndex(int index)
        {
            this._contextMenuSelectedIndex = index;
        }

        /// <summary>
        /// Chiude context menu
        /// </summary>
        private void CloseContextMenu()
        {
            this._showContextMenu = false;
        }

        /// <summary>
        /// Genera report mediainfo e mostra dialog
        /// </summary>
        /// <param name="filePath">Percorso file da analizzare</param>
        /// <param name="title">Titolo del dialog</param>
        private void OpenMediaInfo(string filePath, string title)
        {
            this._showContextMenu = false;

            MediaInfoService miService = new MediaInfoService(AppSettingsService.Instance.Settings.Tools.MediaInfoPath);
            this._mediaInfoReport = miService.GetReport(filePath);
            this._mediaInfoTitle = title;
            this._showMediaInfo = true;
        }

        /// <summary>
        /// Chiude dialog mediainfo
        /// </summary>
        private void CloseMediaInfo()
        {
            this._showMediaInfo = false;
        }

        /// <summary>
        /// Sincronizza il record selezionato leggendo l'indice dall'orchestratore
        /// </summary>
        private void SyncSelectedFromOrchestrator()
        {
            int index = this.Orchestrator.SelectedIndex;

            if (index >= 0 && index < this._records.Count)
            {
                this._selectedRecord = this._records[index];
            }
            else
            {
                this._selectedRecord = null;
            }
        }

        /// <summary>
        /// Sincronizza record split selezionato
        /// </summary>
        private void SyncSelectedFromSplitOrchestrator()
        {
            int index = this.SplitOrchestrator.SelectedIndex;

            if (index >= 0 && index < this._splitRecords.Count)
            {
                this._selectedSplitRecord = this._splitRecords[index];
            }
            else
            {
                this._selectedSplitRecord = null;
            }
        }

        /// <summary>
        /// Sincronizza record metadata selezionato
        /// </summary>
        private void SyncSelectedFromMetadataOrchestrator()
        {
            int index = this.MetadataOrchestrator.SelectedIndex;

            if (index >= 0 && index < this._metadataRecords.Count)
            {
                this._selectedMetadataRecord = this._metadataRecords[index];
            }
            else
            {
                this._selectedMetadataRecord = null;
            }
        }

        /// <summary>
        /// Restituisce log della modalità corrente
        /// </summary>
        /// <returns>Log corrente</returns>
        private string GetCurrentLogText()
        {
            if (this._currentMode == Options.MODE_SPLIT)
            {
                return this.SplitOrchestrator.LogText;
            }

            if (this._currentMode == Options.MODE_METADATA)
            {
                return this.MetadataOrchestrator.LogText;
            }

            return this.Orchestrator.LogText;
        }

        /// <summary>
        /// Restituisce progress della modalità corrente
        /// </summary>
        /// <returns>Progress corrente</returns>
        private ProcessingProgressState GetCurrentProgress()
        {
            if (this._currentMode == Options.MODE_SPLIT)
            {
                return this.SplitOrchestrator.Progress;
            }

            if (this._currentMode == Options.MODE_METADATA)
            {
                return this.MetadataOrchestrator.Progress;
            }

            return this.Orchestrator.Progress;
        }

        /// <summary>
        /// Costruisce riepilogo tracce metadata
        /// </summary>
        /// <param name="record">Record metadata</param>
        /// <returns>Riepilogo tracce</returns>
        private string BuildMetadataTrackSummary(MkvMetadataRecord record)
        {
            int video = 0;
            int audio = 0;
            int subtitles = 0;

            if (record == null || record.FileInfo == null || record.FileInfo.Tracks == null)
            {
                return "";
            }

            for (int i = 0; i < record.FileInfo.Tracks.Count; i++)
            {
                if (record.FileInfo.Tracks[i].TrackKind == "video") { video++; }
                else if (record.FileInfo.Tracks[i].TrackKind == "audio") { audio++; }
                else if (record.FileInfo.Tracks[i].TrackKind == "subtitles") { subtitles++; }
            }

            return video + "V " + audio + "A " + subtitles + "S";
        }

        /// <summary>
        /// Costruisce riepilogo video metadata
        /// </summary>
        /// <param name="record">Record metadata</param>
        /// <param name="simulated">True per stato simulato</param>
        /// <returns>Riepilogo video</returns>
        private string BuildMetadataVideoSummary(MkvMetadataRecord record, bool simulated)
        {
            MkvMetadataFileInfo info = GetMetadataInfo(record, simulated);
            MkvMetadataTrackInfo video = null;

            if (info == null || info.Tracks == null)
            {
                return "-";
            }

            for (int i = 0; i < info.Tracks.Count; i++)
            {
                if (info.Tracks[i].TrackKind == "video")
                {
                    video = info.Tracks[i];
                    break;
                }
            }

            if (video == null)
            {
                return "-";
            }

            return BuildMetadataTrackVideoSummary(video);
        }

        /// <summary>
        /// Restituisce info metadata attuali o simulate
        /// </summary>
        /// <param name="record">Record metadata</param>
        /// <param name="simulated">True per stato simulato</param>
        /// <returns>Info metadata</returns>
        private static MkvMetadataFileInfo GetMetadataInfo(MkvMetadataRecord record, bool simulated)
        {
            if (record == null)
            {
                return null;
            }

            if (simulated)
            {
                return record.FileInfo;
            }

            return record.OriginalFileInfo != null && record.OriginalFileInfo.Tracks != null && record.OriginalFileInfo.Tracks.Count > 0
                ? record.OriginalFileInfo
                : record.FileInfo;
        }

        /// <summary>
        /// Costruisce riepilogo compatto della traccia video
        /// </summary>
        /// <param name="track">Traccia video</param>
        /// <returns>Riepilogo</returns>
        private static string BuildMetadataTrackVideoSummary(MkvMetadataTrackInfo track)
        {
            string result = GetMetadataFieldValue(track, "video_format");
            string width = GetMetadataFieldValue(track, "video_width");
            string height = GetMetadataFieldValue(track, "video_height");
            string fps = GetMetadataFieldValue(track, "video_fps");
            string bitDepth = GetMetadataFieldValue(track, "video_bitdepth");
            string hdr = GetMetadataFieldValue(track, "video_hdr_format");

            if (result.Length == 0) { result = track.Format; }
            if (width.Length > 0 && height.Length > 0) { AddMetadataSummaryPart(ref result, width + "x" + height); }
            if (fps.Length > 0) { AddMetadataSummaryPart(ref result, fps + "fps"); }
            if (bitDepth.Length > 0) { AddMetadataSummaryPart(ref result, bitDepth + "bit"); }
            if (hdr.Length > 0) { AddMetadataSummaryPart(ref result, hdr); }

            return result.Length > 0 ? result : "-";
        }

        /// <summary>
        /// Restituisce campo metadata da traccia
        /// </summary>
        private static string GetMetadataFieldValue(MkvMetadataTrackInfo track, string key)
        {
            string result;
            if (track != null && track.Fields != null && track.Fields.TryGetValue(key, out result))
            {
                return result != null ? result : "";
            }

            return "";
        }

        /// <summary>
        /// Aggiunge parte al riepilogo metadata
        /// </summary>
        private static void AddMetadataSummaryPart(ref string value, string part)
        {
            if (part == null || part.Length == 0)
            {
                return;
            }

            if (value.Length > 0)
            {
                value += " ";
            }

            value += part;
        }

        /// <summary>
        /// Costruisce testo preview prima/dopo per una modifica metadata
        /// </summary>
        /// <param name="change">Modifica</param>
        /// <returns>Testo preview</returns>
        private string BuildMetadataChangeText(MkvMetadataChange change)
        {
            if (change == null)
            {
                return "";
            }

            if (change.FieldKey != null && change.FieldKey.Length > 0 &&
                (change.OperationType == MkvMetadataOperationType.SetField ||
                 change.OperationType == MkvMetadataOperationType.ClearField ||
                 change.OperationType == MkvMetadataOperationType.SetExclusiveFlag ||
                 change.OperationType == MkvMetadataOperationType.SetTagField ||
                 change.OperationType == MkvMetadataOperationType.ClearTagField))
            {
                return change.FieldKey + ": " + FormatMetadataPreviewValue(change.BeforeValue) + " -> " + FormatMetadataPreviewValue(change.AfterValue);
            }

            return change.Message;
        }

        /// <summary>
        /// Formatta valore preview metadata
        /// </summary>
        /// <param name="value">Valore</param>
        /// <returns>Valore leggibile</returns>
        private static string FormatMetadataPreviewValue(string value)
        {
            if (value == null || value.Length == 0)
            {
                return "''";
            }

            return "'" + value + "'";
        }

        #endregion

        #region Azioni

        /// <summary>
        /// Cambia modalità UI e salva preferenza
        /// </summary>
        /// <param name="mode">Modalità richiesta</param>
        private void SwitchMode(string mode)
        {
            if (mode != Options.MODE_REMUX && mode != Options.MODE_SPLIT && mode != Options.MODE_METADATA)
            {
                return;
            }

            this._currentMode = mode;
            AppSettingsService.Instance.Settings.Ui.LastMode = mode;
            AppSettingsService.Instance.Save();
        }

        /// <summary>
        /// Applica configurazione split rapida
        /// </summary>
        private bool ApplySplitConfig()
        {
            string errorMessage;
            Options opts = this.SplitOrchestrator.CurrentOptions;
            opts.Mode = Options.MODE_SPLIT;
            opts.Split.SourcePath = opts.SourceFolder;
            if (!this.SplitOrchestrator.ApplyOptions(opts, out errorMessage) && errorMessage.Length > 0)
            {
                this.SplitOrchestrator.Log(errorMessage);
                this.StateHasChanged();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Esegue scan cartelle
        /// </summary>
        private void DoScan()
        {
            if (this._currentMode == Options.MODE_METADATA)
            {
                if (this.MetadataOrchestrator.CurrentOptions.Metadata.SourcePath.Length == 0)
                {
                    this.ShowMetadataInputPicker();
                    return;
                }

                if (!this.MetadataOrchestrator.IsBusy)
                {
                    this.MetadataOrchestrator.Scan();
                }
                return;
            }

            if (this._currentMode == Options.MODE_SPLIT)
            {
                if (!this.ApplySplitConfig())
                {
                    return;
                }

                if (!this.SplitOrchestrator.IsBusy)
                {
                    this.SplitOrchestrator.Scan();
                }
                return;
            }

            if (!this.Orchestrator.IsBusy)
            {
                this.Orchestrator.Scan();
            }
            else
            {
                this.Orchestrator.Log(AppText.T("web.log.scanBusy"));
            }
        }

        /// <summary>
        /// Analizza episodio selezionato
        /// </summary>
        private void DoAnalyzeSelected()
        {
            List<int> selectedIndices;
            if (this._currentMode == Options.MODE_METADATA)
            {
                this.DoAnalyzeAll();
                return;
            }

            if (this._currentMode == Options.MODE_SPLIT)
            {
                return;
            }

            selectedIndices = this.GetActionSelectionIndices();
            if (!this.Orchestrator.IsBusy && selectedIndices.Count > 1)
            {
                this.Orchestrator.AnalyzeFiles(selectedIndices);
            }
            else if (!this.Orchestrator.IsBusy && selectedIndices.Count == 1)
            {
                this.Orchestrator.AnalyzeFile(selectedIndices[0]);
            }
            else if (this.Orchestrator.IsBusy)
            {
                this.Orchestrator.Log(AppText.T("web.log.analyzeBusy"));
            }
            else
            {
                this.Orchestrator.Log(AppText.T("web.log.selectEpisodeAnalyze"));
            }
        }

        /// <summary>
        /// Analizza tutti gli episodi pendenti
        /// </summary>
        private void DoAnalyzeAll()
        {
            if (this._currentMode == Options.MODE_METADATA)
            {
                if (!this.MetadataOrchestrator.IsBusy)
                {
                    this.MetadataOrchestrator.AnalyzeAll();
                }
                return;
            }

            if (this._currentMode == Options.MODE_SPLIT)
            {
                return;
            }

            if (!this.Orchestrator.IsBusy)
            {
                this.Orchestrator.AnalyzeAll();
            }
            else
            {
                this.Orchestrator.Log(AppText.T("web.log.analyzeBatchBusy"));
            }
        }

        /// <summary>
        /// Alterna stato skip episodio selezionato
        /// </summary>
        private void DoToggleSkip()
        {
            List<int> selectedIndices;
            if (this._currentMode == Options.MODE_METADATA)
            {
                return;
            }

            if (this._currentMode == Options.MODE_SPLIT)
            {
                return;
            }

            selectedIndices = this.GetActionSelectionIndices();
            if (selectedIndices.Count > 1)
            {
                this.Orchestrator.ToggleSkip(selectedIndices);
            }
            else if (selectedIndices.Count == 1)
            {
                this.Orchestrator.ToggleSkip(selectedIndices[0]);
            }
            else
            {
                this.Orchestrator.Log(AppText.T("web.log.selectEpisodeSkip"));
            }
        }

        /// <summary>
        /// Esegue merge episodio selezionato
        /// </summary>
        private void DoMergeSelected()
        {
            List<int> selectedIndices;
            if (this._currentMode == Options.MODE_METADATA)
            {
                if (!this.MetadataOrchestrator.IsBusy)
                {
                    this.MetadataOrchestrator.ApplySelected(this.MetadataOrchestrator.SelectedIndex);
                }
                return;
            }

            if (this._currentMode == Options.MODE_SPLIT)
            {
                this.DoMergeAll();
                return;
            }

            selectedIndices = this.GetActionSelectionIndices();
            if (!this.Orchestrator.IsBusy && selectedIndices.Count > 1)
            {
                this.Orchestrator.MergeFiles(selectedIndices);
            }
            else if (!this.Orchestrator.IsBusy && selectedIndices.Count == 1)
            {
                this.Orchestrator.MergeFile(selectedIndices[0]);
            }
            else if (this.Orchestrator.IsBusy)
            {
                this.Orchestrator.Log(AppText.T("web.log.mergeBusy"));
            }
            else
            {
                this.Orchestrator.Log(AppText.T("web.log.selectEpisodeProcess"));
            }
        }

        /// <summary>
        /// Esegue merge di tutti gli episodi analizzati
        /// </summary>
        private void DoMergeAll()
        {
            if (this._currentMode == Options.MODE_METADATA)
            {
                if (!this.MetadataOrchestrator.IsBusy)
                {
                    this.MetadataOrchestrator.ApplyAll();
                }
                return;
            }

            if (this._currentMode == Options.MODE_SPLIT)
            {
                if (!this.ApplySplitConfig())
                {
                    return;
                }

                if (!this.SplitOrchestrator.IsBusy)
                {
                    this.SplitOrchestrator.SplitAll();
                }
                return;
            }

            if (!this.Orchestrator.IsBusy)
            {
                this.Orchestrator.MergeAll();
            }
            else
            {
                this.Orchestrator.Log(AppText.T("web.log.mergeBatchBusy"));
            }
        }

        /// <summary>
        /// Richiede stop cooperativo dell'operazione corrente
        /// </summary>
        private void DoStop()
        {
            if (this._currentMode == Options.MODE_METADATA)
            {
                this.MetadataOrchestrator.Stop();
                return;
            }

            if (this._currentMode == Options.MODE_SPLIT)
            {
                this.SplitOrchestrator.Stop();
                return;
            }

            if (this.Orchestrator.IsBusy)
            {
                this.Orchestrator.RequestStop();
            }
            else
            {
                this.Orchestrator.Log(AppText.T("web.log.noOperationToStop"));
            }
        }

        /// <summary>
        /// Mostra dialog configurazione
        /// </summary>
        private void ShowConfig()
        {
            if (this._currentMode == Options.MODE_METADATA)
            {
                this.ShowMetadataInputPicker();
                return;
            }

            this._showConfig = true;
        }

        /// <summary>
        /// Chiude dialog configurazione
        /// </summary>
        private void CloseConfig()
        {
            this._showConfig = false;
        }

        /// <summary>
        /// Apre il picker input metadata
        /// </summary>
        private void ShowMetadataInputPicker()
        {
            this.BrowseMetadataPath(0, true, true);
        }

        /// <summary>
        /// Mostra dialog preset metadata
        /// </summary>
        private void ShowMetadataPreset()
        {
            this._metadataPresetFiles = this.MetadataOrchestrator.GetPresetFiles();
            this._showMetadataPreset = true;
        }

        /// <summary>
        /// Chiude dialog preset metadata
        /// </summary>
        private void CloseMetadataPreset()
        {
            this._showMetadataPreset = false;
            this._metadataPresetFiles = this.MetadataOrchestrator.GetPresetFiles();
        }

        /// <summary>
        /// Mostra dettaglio metadata mappato
        /// </summary>
        /// <param name="simulated">True per dettaglio simulato</param>
        private void ShowMetadataMappedInfo(bool simulated)
        {
            this._metadataMappedInfoSimulated = simulated;
            this._showMetadataMappedInfo = true;
        }

        /// <summary>
        /// Chiude dettaglio metadata mappato
        /// </summary>
        private void CloseMetadataMappedInfo()
        {
            this._showMetadataMappedInfo = false;
        }

        /// <summary>
        /// Applica preset metadata selezionato
        /// </summary>
        /// <param name="presetPath">Percorso preset</param>
        private void ApplyMetadataPreset(string presetPath)
        {
            Options opts = this.MetadataOrchestrator.CurrentOptions;
            string errorMessage;

            opts.Metadata.PresetPath = presetPath != null ? presetPath : "";
            if (this.MetadataOrchestrator.ApplyOptions(opts, out errorMessage))
            {
                this._showMetadataPreset = false;
                this._metadataPresetFiles = this.MetadataOrchestrator.GetPresetFiles();
            }
            else if (errorMessage.Length > 0)
            {
                this.MetadataOrchestrator.Log(errorMessage);
            }
        }

        /// <summary>
        /// Cambia preset metadata dal dropdown principale
        /// </summary>
        /// <param name="args">Evento change</param>
        private void ChangeMetadataPreset(ChangeEventArgs args)
        {
            this.ApplyMetadataPreset(args.Value != null ? args.Value.ToString() : "");
        }

        /// <summary>
        /// Cambia input metadata dalla toolbar
        /// </summary>
        /// <param name="args">Evento change</param>
        private void ChangeMetadataSourcePath(ChangeEventArgs args)
        {
            MkvMetadataOptions metadata = this.GetMetadataOptions();
            metadata.SourcePath = args.Value != null ? args.Value.ToString().Trim() : "";
            this.ApplyMetadataRuntimeOptions(true);
        }

        /// <summary>
        /// Cambia output metadata dalla toolbar
        /// </summary>
        /// <param name="args">Evento change</param>
        private void ChangeMetadataOutputDir(ChangeEventArgs args)
        {
            MkvMetadataOptions metadata = this.GetMetadataOptions();
            metadata.OutputDir = args.Value != null ? args.Value.ToString().Trim() : "";
            this.ApplyMetadataRuntimeOptions(false);
        }

        /// <summary>
        /// Cambia policy output metadata dalla toolbar
        /// </summary>
        /// <param name="args">Evento change</param>
        private void ChangeMetadataOutputPolicy(ChangeEventArgs args)
        {
            MkvMetadataOptions metadata = this.GetMetadataOptions();
            string value = args.Value != null ? args.Value.ToString() : "";
            metadata.OutputPolicy = value == "output" ? MkvMetadataOutputPolicy.OutputPath : MkvMetadataOutputPolicy.Overwrite;
            this.ApplyMetadataRuntimeOptions(false);
        }

        /// <summary>
        /// Alterna scan ricorsivo metadata
        /// </summary>
        private void ToggleMetadataRecursive()
        {
            MkvMetadataOptions metadata = this.GetMetadataOptions();
            metadata.Recursive = !metadata.Recursive;
            this.ApplyMetadataRuntimeOptions(true);
        }

        /// <summary>
        /// Alterna preservazione cartelle metadata
        /// </summary>
        private void ToggleMetadataPreserveFolderStructure()
        {
            MkvMetadataOptions metadata = this.GetMetadataOptions();
            metadata.PreserveFolderStructure = !metadata.PreserveFolderStructure;
            this.ApplyMetadataRuntimeOptions(false);
        }

        /// <summary>
        /// Restituisce valore select policy output metadata
        /// </summary>
        /// <returns>Valore select policy</returns>
        private string GetMetadataOutputPolicyValue()
        {
            return this.GetMetadataOptions().OutputPolicy == MkvMetadataOutputPolicy.OutputPath ? "output" : "overwrite";
        }

        /// <summary>
        /// Apre browser path per input o output metadata
        /// </summary>
        /// <param name="fieldIndex">Indice campo: 0 input, 1 output</param>
        /// <param name="showFiles">True per mostrare file</param>
        /// <param name="allowCurrentFolderSelection">True per permettere cartella corrente</param>
        private void BrowseMetadataPath(int fieldIndex, bool showFiles, bool allowCurrentFolderSelection)
        {
            MkvMetadataOptions metadata = this.GetMetadataOptions();

            this._metadataBrowseFieldIndex = fieldIndex;
            this._metadataBrowseShowFiles = showFiles;
            this._metadataBrowseAllowCurrentFolderSelection = allowCurrentFolderSelection;
            if (fieldIndex == 0)
                this._metadataBrowseInitialPath = metadata.SourcePath;
            else if (fieldIndex == 1)
                this._metadataBrowseInitialPath = metadata.OutputDir.Length > 0 ? metadata.OutputDir : this.MetadataOrchestrator.CurrentOptions.DestinationFolder;
            else
                this._metadataBrowseInitialPath = "";

            this._showMetadataPathBrowse = true;
        }

        /// <summary>
        /// Chiude browser path metadata
        /// </summary>
        private void CloseMetadataPathBrowse()
        {
            this._showMetadataPathBrowse = false;
        }

        /// <summary>
        /// Applica path selezionato dal browser metadata
        /// </summary>
        /// <param name="selectedPath">Percorso selezionato</param>
        private void ApplyMetadataPathBrowse(string selectedPath)
        {
            this._showMetadataPathBrowse = false;
            if (string.IsNullOrEmpty(selectedPath))
                return;

            MkvMetadataOptions metadata = this.GetMetadataOptions();
            if (this._metadataBrowseFieldIndex == 0)
            {
                metadata.SourcePath = selectedPath;
                this.ApplyMetadataRuntimeOptions(true);
            }
            else if (this._metadataBrowseFieldIndex == 1)
            {
                metadata.OutputPolicy = MkvMetadataOutputPolicy.OutputPath;
                metadata.OutputDir = selectedPath;
                this.ApplyMetadataRuntimeOptions(false);
            }
        }

        /// <summary>
        /// Restituisce opzioni metadata correnti garantendo istanza valida
        /// </summary>
        /// <returns>Opzioni metadata correnti</returns>
        private MkvMetadataOptions GetMetadataOptions()
        {
            Options opts = this.MetadataOrchestrator.CurrentOptions;
            if (opts.Metadata == null)
                opts.Metadata = new MkvMetadataOptions();

            return opts.Metadata;
        }

        /// <summary>
        /// Applica opzioni runtime metadata modificate dalla toolbar
        /// </summary>
        /// <param name="clearRecords">True per svuotare record già scansionati</param>
        private void ApplyMetadataRuntimeOptions(bool clearRecords)
        {
            Options opts = this.MetadataOrchestrator.CurrentOptions;
            MkvMetadataOptions metadata = this.GetMetadataOptions();
            string errorMessage;

            metadata.SourcePath = metadata.SourcePath != null ? metadata.SourcePath.Trim() : "";
            metadata.OutputDir = metadata.OutputDir != null ? metadata.OutputDir.Trim() : "";
            metadata.DryRun = false;
            opts.Mode = Options.MODE_METADATA;
            opts.SourceFolder = metadata.SourcePath;
            opts.DestinationFolder = metadata.OutputDir;
            opts.Recursive = metadata.Recursive;
            opts.DryRun = false;
            opts.Overwrite = metadata.OutputPolicy == MkvMetadataOutputPolicy.Overwrite;

            if (this.MetadataOrchestrator.ApplyOptions(opts, out errorMessage))
            {
                if (clearRecords)
                    this.MetadataOrchestrator.Clear();
            }
            else if (errorMessage.Length > 0)
            {
                this.MetadataOrchestrator.Log(errorMessage);
            }
        }

        /// <summary>
        /// Clear modalità corrente
        /// </summary>
        private void DoClear()
        {
            if (this._currentMode == Options.MODE_METADATA)
            {
                this.MetadataOrchestrator.Clear();
                this.ShowMetadataInputPicker();
            }
        }

        /// <summary>
        /// Mostra rinomina avanzata Metadata
        /// </summary>
        private void ShowMetadataRename()
        {
            if (this._currentMode == Options.MODE_METADATA)
            {
                if (this._metadataRecords.Count == 0)
                {
                    this.MetadataOrchestrator.Log(AppText.T("web.metadata.renameNoScannedFiles"));
                    this.ShowMetadataInputPicker();
                    return;
                }

                this._showMetadataRename = true;
            }
        }

        /// <summary>
        /// Chiude rinomina avanzata Metadata
        /// </summary>
        /// <param name="renamed">True se sono stati rinominati file</param>
        private void CloseMetadataRename(bool renamed)
        {
            this._showMetadataRename = false;
            if (renamed)
            {
                this.MetadataOrchestrator.Log(AppText.T("web.metadata.renameCompletedRefresh"));
                this.MetadataOrchestrator.Scan();
            }
        }

        /// <summary>
        /// Applica configurazione e reinizializza pipeline
        /// </summary>
        /// <param name="opts">Nuove opzioni</param>
        private void ApplyConfig(Options opts)
        {
            string errorMessage;

            if (this._currentMode == Options.MODE_SPLIT)
            {
                if (this.SplitOrchestrator.ApplyOptions(opts, out errorMessage))
                {
                    this._showConfig = false;
                }
                else if (errorMessage.Length > 0)
                {
                    this.SplitOrchestrator.Log(errorMessage);
                }
            }
            else if (this.Orchestrator.ApplyOptions(opts, out errorMessage))
            {
                this._showConfig = false;
            }
            else if (errorMessage.Length > 0)
            {
                this.Orchestrator.Log(errorMessage);
            }
        }

        /// <summary>
        /// Mostra dialog percorsi tool
        /// </summary>
        private void ShowToolPaths()
        {
            this._showToolPaths = true;
        }

        /// <summary>
        /// Chiude dialog percorsi tool
        /// </summary>
        private void CloseToolPaths()
        {
            this._showToolPaths = false;
        }

        /// <summary>
        /// Mostra dialog impostazioni audio
        /// </summary>
        private void ShowAudioSettings()
        {
            this._showAudioSettings = true;
        }

        /// <summary>
        /// Chiude dialog impostazioni audio
        /// </summary>
        private void CloseAudioSettings()
        {
            this._showAudioSettings = false;
        }

        /// <summary>
        /// Mostra dialog impostazioni avanzate
        /// </summary>
        private void ShowAdvancedSettings()
        {
            this._showAdvancedSettings = true;
        }

        /// <summary>
        /// Chiude dialog impostazioni avanzate
        /// </summary>
        private void CloseAdvancedSettings()
        {
            this._showAdvancedSettings = false;
        }

        /// <summary>
        /// Chiude dialog delay
        /// </summary>
        private void CloseDelay()
        {
            this._showDelay = false;
        }

        /// <summary>
        /// Applica delay per-file
        /// </summary>
        /// <param name="delays">Tupla (audioDelay, subDelay) in ms</param>
        private void ApplyDelay((int, int) delays)
        {
            this._showDelay = false;

            if (this.Orchestrator.SelectedIndex >= 0)
            {
                this.Orchestrator.UpdateDelay(this.Orchestrator.SelectedIndex, delays.Item1, delays.Item2);
            }
        }

        /// <summary>
        /// Mostra dialog info
        /// </summary>
        private void ShowInfo()
        {
            this._showInfo = true;
        }

        /// <summary>
        /// Chiude dialog info
        /// </summary>
        private void CloseInfo()
        {
            this._showInfo = false;
        }

        /// <summary>
        /// Mostra dialog profili encoding
        /// </summary>
        private void ShowEncodingProfiles()
        {
            this._showEncodingProfiles = true;
        }

        /// <summary>
        /// Chiude dialog profili encoding
        /// </summary>
        private void CloseEncodingProfiles()
        {
            this._showEncodingProfiles = false;
        }

        /// <summary>
        /// Mostra dialog pipeline
        /// </summary>
        private void ShowPipeline()
        {
            this._showPipeline = true;
        }

        /// <summary>
        /// Chiude dialog pipeline
        /// </summary>
        private void ClosePipeline()
        {
            this._showPipeline = false;
        }

        /// <summary>
        /// Chiude tutti i dialog aperti
        /// </summary>
        private void CloseAllDialogs()
        {
            this._showConfig = false;
            this._showMetadataPathBrowse = false;
            this._showMetadataPreset = false;
            this._showMetadataMappedInfo = false;
            this._showMetadataRename = false;
            this._showToolPaths = false;
            this._showAudioSettings = false;
            this._showAdvancedSettings = false;
            this._showDelay = false;
            this._showEncodingProfiles = false;
            this._showPipeline = false;
            this._showInfo = false;
            this._showContextMenu = false;
            this._showMediaInfo = false;
        }

        /// <summary>
        /// Cambia tema via modulo JS interop e salva in AppSettings
        /// </summary>
        /// <param name="theme">Nome tema kebab-case</param>
        private void ChangeTheme(string theme)
        {
            this._currentTheme = theme;

            // Salva in AppSettings
            AppSettingsService.Instance.Settings.Ui.Theme = theme;
            AppSettingsService.Instance.Save();

            if (this._jsModule != null)
            {
                try
                {
                    _ = this._jsModule.InvokeVoidAsync("setTheme", theme);
                }
                catch
                {
                    // Ignora errori JS durante dispose
                }
            }
        }

        /// <summary>
        /// Cambia lingua UI e salva in AppSettings
        /// </summary>
        /// <param name="language">Codice lingua</param>
        private void ChangeLanguage(string language)
        {
            string normalizedLanguage = AppText.NormalizeLanguage(language);
            if (normalizedLanguage.Length == 0)
            {
                return;
            }

            this._currentLanguage = normalizedLanguage;

            // Salva in AppSettings
            AppSettingsService.Instance.Settings.Ui.Language = normalizedLanguage;
            AppSettingsService.Instance.Save();

            // Aggiorna il catalogo testi della sessione corrente
            AppText.Initialize(normalizedLanguage, normalizedLanguage);

            if (this._jsModule != null)
            {
                try
                {
                    _ = this._jsModule.InvokeVoidAsync("setLanguage", normalizedLanguage);
                }
                catch
                {
                    // Ignora errori JS durante dispose
                }
            }

            this.StateHasChanged();
        }

        #endregion
    }
}
