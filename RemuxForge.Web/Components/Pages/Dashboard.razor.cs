using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using RemuxForge.Web.Components.Shared;
using RemuxForge.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;
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

        /// <summary>
        /// Servizio dialog Radzen
        /// </summary>
        [Inject]
        private DialogService DialogService { get; set; }

        /// <summary>
        /// Servizio notifiche Radzen
        /// </summary>
        [Inject]
        private NotificationService NotificationService { get; set; }

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
        /// True quando l'editor visuale dei segmenti Split è aperto
        /// </summary>
        private bool _showSplitEditor;

        /// <summary>
        /// Record Split aperto nell'editor visuale
        /// </summary>
        private MkvSplitRecord _splitEditorRecord;

        /// <summary>
        /// Indice del record Split aperto nell'editor visuale
        /// </summary>
        private int _splitEditorIndex = -1;

        /// <summary>
        /// Segmento su cui aprire l'editor visuale
        /// </summary>
        private int _splitEditorSegmentNum;

        /// <summary>
        /// Segmenti già costruiti nell'editor per il record aperto, null quando comanda la configurazione
        /// </summary>
        private List<MkvSplitOverrideSegment> _splitEditorOverride;

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
        /// Numero del segmento evidenziato nel dettaglio Split
        /// </summary>
        private int _selectedSplitSegmentNum;

        /// <summary>
        /// Record metadata selezionato
        /// </summary>
        private MkvMetadataRecord _selectedMetadataRecord;

        /// <summary>
        /// Indici episodi selezionati in modalità multi-select
        /// </summary>
        private RowSelectionState _selection;

        /// <summary>
        /// Selezione multipla della griglia Split
        /// </summary>
        private RowSelectionState _splitSelection;

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
        /// Indica che la navigazione laterale è compressa
        /// </summary>
        private bool _navigationCollapsed;

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
        /// Estensioni ammesse dal browser metadata
        /// </summary>
        private List<string> _metadataBrowseAllowedExtensions = new List<string> { "mkv" };

        /// <summary>
        /// Flag: mostra dettaglio metadata mappato
        /// </summary>
        private bool _showMetadataMappedInfo;

        /// <summary>
        /// Flag: mostra editor manuale metadata
        /// </summary>
        private bool _showMetadataManualEdit;

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
        /// Flag: mostra editor visuale EditMap
        /// </summary>
        private bool _showEditMapEditor;

        /// <summary>
        /// Snapshot del record aperto nell'editor EditMap
        /// </summary>
        private FileProcessingRecord _editMapRecord;

        /// <summary>
        /// Indice originale del record aperto nell'editor EditMap
        /// </summary>
        private int _editMapRecordIndex;

        /// <summary>
        /// Flag: mostra dialog profili encoding
        /// </summary>
        private bool _showEncodingProfiles;

        /// <summary>
        /// Flag: mostra dialog info
        /// </summary>
        private bool _showInfo;

        /// <summary>
        /// Flag: mostra context menu episodio
        /// </summary>
        private bool _showContextMenu;

        /// <summary>
        /// Comandi del context menu corrente
        /// </summary>
        private List<UiCommandDefinition> _contextMenuCommands;

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

        #region Ciclo di vita

        /// <summary>
        /// Inizializzazione componente
        /// </summary>
        protected override void OnInitialized()
        {
            this._currentTheme = AppSettingsService.Instance.Settings.Ui.Theme;
            this._currentLanguage = AppText.NormalizeLanguage(AppSettingsService.Instance.Settings.Ui.Language);
            if (string.IsNullOrEmpty(this._currentLanguage))
                this._currentLanguage = AppText.LANG_EN;

            this._currentMode = AppSettingsService.Instance.Settings.Ui.LastMode;
            if (this._currentMode != Options.MODE_REMUX && this._currentMode != Options.MODE_SPLIT && this._currentMode != Options.MODE_METADATA)
                this._currentMode = Options.MODE_REMUX;

            this._showConfig = false;
            this._showMetadataPreset = false;
            this._showMetadataPathBrowse = false;
            this._metadataBrowseFieldIndex = -1;
            this._metadataBrowseInitialPath = "";
            this._metadataBrowseShowFiles = false;
            this._metadataBrowseAllowCurrentFolderSelection = true;
            this._showMetadataMappedInfo = false;
            this._showMetadataManualEdit = false;
            this._metadataMappedInfoSimulated = false;
            this._showMetadataRename = false;
            this._showToolPaths = false;
            this._showAudioSettings = false;
            this._showAdvancedSettings = false;
            this._showDelay = false;
            this._showEditMapEditor = false;
            this._editMapRecord = null;
            this._editMapRecordIndex = -1;
            this._showEncodingProfiles = false;
            this._showInfo = false;
            this._showContextMenu = false;
            this._contextMenuCommands = new List<UiCommandDefinition>();
            this._showMediaInfo = false;
            this._mediaInfoTitle = "";
            this._mediaInfoReport = "";
            this._selection = new RowSelectionState();
            this._splitSelection = new RowSelectionState();
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
            this.SplitOrchestrator.OnAnalysisCompleted += this.HandleSplitAnalysisCompleted;
            this.SplitOrchestrator.OnSplitCompleted += this.HandleSplitCompleted;
            this.SplitOrchestrator.OnOperationFailed += this.HandleSplitOperationFailed;
            this.MetadataOrchestrator.OnLog += this.HandleLog;
            this.MetadataOrchestrator.OnRecordsChanged += this.HandleMetadataRecordsChanged;
            this.MetadataOrchestrator.OnProgressChanged += this.HandleProgressChanged;
            this.MetadataOrchestrator.OnAnalysisCompleted += this.HandleMetadataAnalysisCompleted;
            this.MetadataOrchestrator.OnApplyCompleted += this.HandleMetadataApplyCompleted;
            this.MetadataOrchestrator.OnOperationFailed += this.HandleMetadataOperationFailed;
        }

        /// <summary>
        /// Importa modulo JS e inizializza tastiera e tema dopo il primo render
        /// </summary>
        /// <param name="firstRender">True se primo render</param>
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                // Importa modulo JS interop
                this._jsModule = await this.JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/interop.js");

                // Cattura tastiera via JS
                this._dotNetRef = DotNetObjectReference.Create(this);
                await this._jsModule.InvokeVoidAsync("captureKeyboard", this._dotNetRef);

                // Carica tema da AppSettings e applica tramite Radzen
                this._currentTheme = AppSettingsService.Instance.Settings.Ui.Theme;
                this.ThemeService.SetTheme(this._currentTheme);
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
                this.SplitOrchestrator.OnAnalysisCompleted -= this.HandleSplitAnalysisCompleted;
                this.SplitOrchestrator.OnSplitCompleted -= this.HandleSplitCompleted;
                this.SplitOrchestrator.OnOperationFailed -= this.HandleSplitOperationFailed;
                this.MetadataOrchestrator.OnLog -= this.HandleLog;
                this.MetadataOrchestrator.OnRecordsChanged -= this.HandleMetadataRecordsChanged;
                this.MetadataOrchestrator.OnProgressChanged -= this.HandleProgressChanged;
                this.MetadataOrchestrator.OnAnalysisCompleted -= this.HandleMetadataAnalysisCompleted;
                this.MetadataOrchestrator.OnApplyCompleted -= this.HandleMetadataApplyCompleted;
                this.MetadataOrchestrator.OnOperationFailed -= this.HandleMetadataOperationFailed;
            }

            // Dispose riferimento .NET per JS interop
            if (this._dotNetRef != null)
                this._dotNetRef.Dispose();

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
        /// Riassume in un toast l'esito dell'analisi Split e gli avvisi dei piani
        /// </summary>
        /// <param name="summary">Riassunto dell'analisi conclusa</param>
        private void HandleSplitAnalysisCompleted(SplitOrchestrator.OperationSummary summary)
        {
            this.InvokeAsync(() =>
            {
                if (summary == null)
                    return;

                NotificationSeverity severity = summary.Failed > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success;
                this.NotificationService.Notify(severity, AppText.T("web.split.notify.analyzeTitle"), AppText.F("web.split.notify.analyzeBody", summary.Succeeded, summary.Failed), 6000);
                this.NotifySplitWarnings();
                this.StateHasChanged();
            });
        }

        /// <summary>
        /// Riassume in un toast l'esito dello split
        /// </summary>
        /// <param name="summary">Riassunto dello split concluso</param>
        private void HandleSplitCompleted(SplitOrchestrator.OperationSummary summary)
        {
            this.InvokeAsync(() =>
            {
                if (summary == null)
                    return;

                NotificationSeverity severity = summary.Failed > 0 ? NotificationSeverity.Error : (summary.Stopped ? NotificationSeverity.Warning : NotificationSeverity.Success);
                string body = summary.Stopped
                    ? AppText.F("web.split.notify.splitStopped", summary.Succeeded, summary.Failed)
                    : AppText.F("web.split.notify.splitBody", summary.Succeeded, summary.Failed, summary.Skipped);
                this.NotificationService.Notify(severity, AppText.T("web.split.notify.splitTitle"), body, 8000);
                this.StateHasChanged();
            });
        }

        /// <summary>
        /// Notifica un'operazione Split che non può partire
        /// </summary>
        /// <param name="message">Motivo del rifiuto</param>
        private void HandleSplitOperationFailed(string message)
        {
            this.InvokeAsync(() =>
            {
                this.NotificationService.Notify(NotificationSeverity.Warning, AppText.T("web.split.notify.failedTitle"), message, 6000);
                this.StateHasChanged();
            });
        }

        /// <summary>
        /// Riassume gli avvisi dei piani in un solo toast, per categoria
        /// </summary>
        private void NotifySplitWarnings()
        {
            List<MkvSplitRecord> records = this.SplitOrchestrator.GetRecords();
            List<string> categories = new List<string>();
            int files = 0;
            int outputs = 0;
            int collisions = 0;
            int reencodes = 0;

            foreach (MkvSplitRecord record in records)
            {
                if (record.Plan == null || record.Plan.Warnings.Count == 0)
                    continue;
                files++;
                foreach (MkvSplitWarning warning in record.Plan.Warnings)
                {
                    if (warning.Kind == MkvSplitWarningKind.OutputExists) { outputs++; }
                    else if (warning.Kind == MkvSplitWarningKind.NameCollision) { collisions++; }
                    else if (warning.Kind == MkvSplitWarningKind.Reencode) { reencodes++; }
                }
            }

            if (files == 0)
                return;

            if (outputs > 0) { categories.Add(AppText.F("web.split.notify.catOutputs", outputs)); }
            if (collisions > 0) { categories.Add(AppText.F("web.split.notify.catCollisions", collisions)); }
            if (reencodes > 0) { categories.Add(AppText.F("web.split.notify.catReencode", reencodes)); }
            if (categories.Count == 0) { categories.Add(AppText.T("web.split.notify.catOther")); }

            this.NotificationService.Notify(NotificationSeverity.Info, AppText.T("web.split.notify.warningsTitle"), AppText.F("web.split.notify.warningsBody", files, string.Join(", ", categories)), 8000);
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
        /// Notifica l'esito dell'analisi metadata
        /// </summary>
        /// <param name="analyzed">File analizzati</param>
        /// <param name="failed">File falliti</param>
        private void HandleMetadataAnalysisCompleted(int analyzed, int failed)
        {
            this.InvokeAsync(() =>
            {
                NotificationSeverity severity = failed > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success;
                this.NotificationService.Notify(severity, AppText.T("web.metadata.notify.analyzeTitle"), AppText.F("web.metadata.notify.analyzeBody", analyzed, failed), 6000);
                this.StateHasChanged();
            });
        }

        /// <summary>
        /// Notifica l'esito dell'applicazione metadata
        /// </summary>
        /// <param name="applied">File scritti</param>
        /// <param name="failed">File falliti</param>
        private void HandleMetadataApplyCompleted(int applied, int failed)
        {
            this.InvokeAsync(() =>
            {
                NotificationSeverity severity = failed > 0 ? NotificationSeverity.Error : NotificationSeverity.Success;
                this.NotificationService.Notify(severity, AppText.T("web.metadata.notify.applyTitle"), AppText.F("web.metadata.notify.applyBody", applied, failed), 6000);
                this.StateHasChanged();
            });
        }

        /// <summary>
        /// Notifica un errore che ha impedito all'operazione metadata di partire o di concludersi
        /// </summary>
        /// <param name="errorMessage">Messaggio di errore</param>
        private void HandleMetadataOperationFailed(string errorMessage)
        {
            this.InvokeAsync(() =>
            {
                this.NotificationService.Notify(NotificationSeverity.Error, AppText.T("web.metadata.notify.failedTitle"), errorMessage, 10000);
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
        public async Task HandleKeyDownAsync(string key, bool ctrl, bool shift, bool alt)
        {
            if (this.IsBlockingOverlayOpen())
            {
                if (key == "Escape")
                {
                    this.CloseAllDialogs();
                    this.StateHasChanged();
                }

                return;
            }

            if (this._showContextMenu && await this.HandleContextMenuKeyAsync(key))
            {
                this.StateHasChanged();
                return;
            }

            if (this._menuBar != null && await this._menuBar.HandleKeyboardKeyAsync(key, ctrl, shift, alt))
            {
                this.StateHasChanged();
                return;
            }

            if (this.IsAnyBusy() && ((key.StartsWith("F", StringComparison.Ordinal) && key.Length <= 3) || ctrl))
            {
                if (this._currentMode == Options.MODE_SPLIT && key == "F6")
                    this.SplitOrchestrator.Analyze(this.GetSplitActionIndices());
                else if (this._currentMode == Options.MODE_SPLIT && key == "F7")
                    this.SplitOrchestrator.Analyze(null);
                else if (this._currentMode == Options.MODE_SPLIT && key == "F9")
                    this.SplitOrchestrator.Split(this.GetSplitActionIndices());
                else if (this._currentMode == Options.MODE_SPLIT && key == "F10")
                    this.SplitOrchestrator.SplitAll();
                else if (key == "F12")
                    this.DoStop();

                return;
            }

            if (this._currentMode == Options.MODE_SPLIT)
            {
                if (key == "F2")
                    this.ShowConfig();
                else if (key == "F5")
                    this.DoScan();
                else if (key == "F6")
                    this.DoAnalyzeSplitSelected();
                else if (key == "F7")
                    this.DoAnalyzeSplitAll();
                else if (key == "F8")
                    this.ToggleSplitSkip();
                else if (key == "F9")
                    this.DoSplitSelected(false);
                else if (key == "F10")
                    this.DoMergeAll();
                else if (key == "F12")
                    this.DoStop();
                else if (key == "Enter")
                    this.ShowSplitContextMenuForSelected();
                else if (key == "Escape")
                    this.CloseAllDialogs();
                else if (ctrl && string.Equals(key, "a", StringComparison.OrdinalIgnoreCase))
                    this.SelectAllSplitRows();
                else if (key == "ArrowUp")
                    this.MoveSplitSelection(-1, shift, ctrl);
                else if (key == "ArrowDown")
                    this.MoveSplitSelection(1, shift, ctrl);
                else if (key == "Home")
                    this.SelectSplitRowWithModifiers((0, ctrl, shift));
                else if (key == "End")
                    this.SelectSplitRowWithModifiers((this._splitRecords.Count - 1, ctrl, shift));
            }
            else if (this._currentMode == Options.MODE_METADATA)
            {
                if (key == "F3")
                    this.ShowMetadataPreset();
                else if (key == "F4")
                    this.ShowMetadataManualEdit();
                else if (key == "F5")
                    this.DoScan();
                else if (key == "F6")
                    this.DoAnalyzeAll();
                else if (key == "F9")
                    this.DoMergeSelected();
                else if (key == "F10")
                    this.DoMergeAll();
                else if (key == "F11")
                    await this.ShowMetadataRenameAsync();
                else if (key == "F12")
                    this.DoStop();
                else if (ctrl && string.Equals(key, "l", StringComparison.OrdinalIgnoreCase))
                    this.DoClear();
                else if (key == "Escape")
                    this.CloseAllDialogs();
                else if (key == "ArrowUp")
                    this.MoveMetadataSelection(-1);
                else if (key == "ArrowDown")
                    this.MoveMetadataSelection(1);
                else if (key == "Home")
                    this.SelectMetadataRow(0);
                else if (key == "End")
                    this.SelectMetadataRow(this._metadataRecords.Count - 1);
            }
            else
            {
                if (key == "F2")
                    this.ShowConfig();
                else if (key == "F5")
                    this.DoScan();
                else if (key == "F6")
                    this.DoAnalyzeSelected();
                else if (key == "F7")
                    this.DoAnalyzeAll();
                else if (key == "F8")
                    this.DoToggleSkip();
                else if (key == "F9")
                    this.DoMergeSelected();
                else if (key == "F10")
                    this.DoMergeAll();
                else if (key == "F12")
                    this.DoStop();
                else if (key == "Enter")
                    this.ShowContextMenuForSelected();
                else if (key == "Escape")
                    this.CloseAllDialogs();
                else if (ctrl && string.Equals(key, "a", StringComparison.OrdinalIgnoreCase))
                    this.SelectAllRows();
                else if (key == "ArrowUp")
                    this.MoveSelection(-1, shift, ctrl);
                else if (key == "ArrowDown")
                    this.MoveSelection(1, shift, ctrl);
                else if (key == "Home")
                    this.SelectIndexFromKeyboard(0, shift, ctrl);
                else if (key == "End")
                    this.SelectIndexFromKeyboard(this._records.Count - 1, shift, ctrl);
                else if (key == "PageUp")
                    this.MoveSelection(-10, shift, ctrl);
                else if (key == "PageDown")
                    this.MoveSelection(10, shift, ctrl);
                else if (key == " ")
                    this.ToggleFocusedSelection();
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
                _ = this.ScrollSplitRowIntoViewAsync(index);
            }
            else
            {
                this._selectedSplitRecord = null;
            }
        }

        /// <summary>
        /// Notifica un errore con un toast, oltre alla riga di log
        /// </summary>
        /// <param name="message">Messaggio da mostrare</param>
        private void NotifyError(string message)
        {
            this.NotificationService.Notify(NotificationSeverity.Error, AppText.T("web.common.statusError"), message, 8000);
        }

        /// <summary>
        /// Seleziona una riga Split applicando i modifier
        /// </summary>
        /// <param name="args">Indice riga e modifier</param>
        private void SelectSplitRowWithModifiers((int Index, bool Ctrl, bool Shift) args)
        {
            this._splitSelection.Apply(args.Index, args.Ctrl, args.Shift, this._splitRecords.Count, this.SplitOrchestrator.SelectedIndex);
            this.SelectSplitRow(args.Index);
        }

        /// <summary>
        /// Evidenzia un segmento nel pannello di dettaglio Split
        /// </summary>
        /// <param name="segmentNum">Numero del segmento</param>
        private void SelectSplitSegment(int segmentNum)
        {
            this._selectedSplitSegmentNum = segmentNum;
        }

        /// <summary>
        /// Indici Split su cui applicare un'azione
        /// </summary>
        /// <returns>Indici selezionati oppure il solo indice a fuoco</returns>
        private List<int> GetSplitActionIndices()
        {
            return this._splitSelection.GetActionIndices(this._splitRecords.Count, this.SplitOrchestrator.SelectedIndex);
        }

        /// <summary>
        /// Mostra il menu contestuale della griglia Split
        /// </summary>
        /// <param name="args">Indice riga e posizione del puntatore</param>
        private void ShowSplitContextMenu((int Index, double X, double Y) args)
        {
            if (!this._splitSelection.IsSelected(args.Index))
            {
                this._splitSelection.Apply(args.Index, false, false, this._splitRecords.Count, this.SplitOrchestrator.SelectedIndex);
            }

            this.SelectSplitRow(args.Index);
            if (this._selectedSplitRecord == null)
                return;

            this._contextMenuX = args.X;
            this._contextMenuY = args.Y;
            this.BuildSplitContextMenu(this._selectedSplitRecord);
            this._contextMenuSelectedIndex = 0;
            this._showContextMenu = true;
        }

        /// <summary>
        /// Costruisce le voci del menu contestuale Split
        /// </summary>
        /// <param name="record">Record della riga</param>
        private void BuildSplitContextMenu(MkvSplitRecord record)
        {
            bool busy = this.SplitOrchestrator.IsBusy;

            this._contextMenuCommands = new List<UiCommandDefinition>();

            this._contextMenuCommands.Add(new UiCommandDefinition(
                AppText.T("web.menu.split.analyzeSelected"), "F6", "", UiCommandPlacement.ContextMenu, UiCommandMenuSection.None, busy,
                () => { this._showContextMenu = false; this.DoSplitSelected(true); }));

            this._contextMenuCommands.Add(new UiCommandDefinition(
                AppText.T("web.menu.split.splitSelected"), "F9", "", UiCommandPlacement.ContextMenu, UiCommandMenuSection.None, busy,
                () => { this._showContextMenu = false; this.DoSplitSelected(false); }));

            this._contextMenuCommands.Add(new UiCommandDefinition(
                record.Skipped ? AppText.T("web.context.split.include") : AppText.T("web.context.split.skip"), "", "", UiCommandPlacement.ContextMenu, UiCommandMenuSection.None, busy,
                () => { this._showContextMenu = false; this.ToggleSplitSkip(); }));

            this._contextMenuCommands.Add(new UiCommandDefinition(
                AppText.T("web.split.openEditor"), "", "", UiCommandPlacement.ContextMenu, UiCommandMenuSection.None, busy || record.Plan == null,
                () => { this._showContextMenu = false; this.OpenSplitEditor(0); }));

            this._contextMenuCommands.Add(new UiCommandDefinition(
                AppText.T("web.split.clearOverride"), "", "", UiCommandPlacement.ContextMenu, UiCommandMenuSection.None, busy || !record.IsOverride,
                () => { this._showContextMenu = false; this.ClearSplitOverride(); }));
        }

        /// <summary>
        /// Inverte l'esclusione dei record Split selezionati
        /// </summary>
        private void ToggleSplitSkip()
        {
            List<int> targets = this.GetSplitActionIndices();
            bool skip = this._selectedSplitRecord != null && !this._selectedSplitRecord.Skipped;

            this.SplitOrchestrator.SetSkipped(targets, skip);
        }

        /// <summary>
        /// Analizza o taglia i record Split selezionati
        /// </summary>
        /// <param name="analyzeOnly">True per costruire solo il piano</param>
        private void DoSplitSelected(bool analyzeOnly)
        {
            List<int> targets;

            if (!this.ApplySplitConfig())
                return;

            targets = this.GetSplitActionIndices();
            if (analyzeOnly)
            {
                this.SplitOrchestrator.Analyze(targets);
            }
            else
            {
                this.SplitOrchestrator.Split(targets);
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
                _ = this.ScrollMetadataRowIntoViewAsync(index);
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
            this._selection.Apply(index, ctrl, shift, this._records.Count, this.Orchestrator.SelectedIndex);
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
                _ = this.ScrollEpisodeRowIntoViewAsync(index);
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
                return;

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
                return;

            if (targetIndex < 0)
                targetIndex = 0;

            if (targetIndex >= this._records.Count)
                targetIndex = this._records.Count - 1;

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
        private void MoveSplitSelection(int delta, bool shift, bool ctrl)
        {
            int currentIndex = this.SplitOrchestrator.SelectedIndex;
            int targetIndex;

            if (this._splitRecords.Count == 0)
                return;

            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            targetIndex = currentIndex + delta;
            if (targetIndex < 0)
                targetIndex = 0;

            if (targetIndex >= this._splitRecords.Count)
                targetIndex = this._splitRecords.Count - 1;

            // Ctrl senza Shift muove il solo fuoco, come nella griglia Remux
            if (ctrl && !shift)
            {
                this.SelectSplitRow(targetIndex);
                return;
            }

            this.SelectSplitRowWithModifiers((targetIndex, ctrl, shift));
        }

        /// <summary>
        /// Seleziona tutte le righe Split
        /// </summary>
        private void SelectAllSplitRows()
        {
            this._splitSelection.SelectAll(this._splitRecords.Count);

            if (this._splitRecords.Count > 0 && this.SplitOrchestrator.SelectedIndex < 0)
            {
                this.SelectSplitRow(0);
                this._splitSelection.SetAnchor(0);
            }
        }

        /// <summary>
        /// Mostra il menu contestuale Split sulla riga a fuoco
        /// </summary>
        private void ShowSplitContextMenuForSelected()
        {
            if (this._selectedSplitRecord == null)
                return;

            this._contextMenuX = 400;
            this._contextMenuY = 300;
            this.BuildSplitContextMenu(this._selectedSplitRecord);
            this._contextMenuSelectedIndex = 0;
            this._showContextMenu = true;
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
                return;

            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            targetIndex = currentIndex + delta;
            if (targetIndex < 0)
                targetIndex = 0;

            if (targetIndex >= this._metadataRecords.Count)
                targetIndex = this._metadataRecords.Count - 1;

            this.SelectMetadataRow(targetIndex);
        }

        /// <summary>
        /// Seleziona tutti gli episodi
        /// </summary>
        private void SelectAllRows()
        {
            this._selection.SelectAll(this._records.Count);

            if (this._records.Count > 0 && this.Orchestrator.SelectedIndex < 0)
            {
                this.SetFocusedRow(0);
                this._selection.SetAnchor(0);
            }
        }

        /// <summary>
        /// Toggle selezione della riga con focus
        /// </summary>
        private void ToggleFocusedSelection()
        {
            int index = this.Orchestrator.SelectedIndex;
            if (index < 0 || index >= this._records.Count)
                return;

            this._selection.Toggle(index);
        }

        /// <summary>
        /// True se una riga è nella selezione multi
        /// </summary>
        /// <param name="index">Indice riga</param>
        /// <returns>True se la riga è selezionata</returns>
        private bool IsRowSelected(int index)
        {
            return this._selection.IsSelected(index);
        }

        /// <summary>
        /// Restituisce gli indici su cui applicare un'azione selezionata
        /// </summary>
        /// <returns>Indici validi in ordine crescente</returns>
        private List<int> GetActionSelectionIndices()
        {
            return this._selection.GetActionIndices(this._records.Count, this.Orchestrator.SelectedIndex);
        }

        /// <summary>
        /// Rimuove selezioni non più valide dopo refresh record
        /// </summary>
        private void NormalizeSelection()
        {
            this._selection.Normalize(this._records.Count);
            this._splitSelection.Normalize(this._splitRecords != null ? this._splitRecords.Count : 0);
        }

        /// <summary>
        /// True se c'è un overlay o workspace esclusivo che deve bloccare scorciatoie tabella
        /// </summary>
        private bool IsBlockingOverlayOpen()
        {
            return this._showConfig || this._showSplitEditor || this._showMetadataPathBrowse || this._showMetadataPreset || this._showMetadataMappedInfo || this._showMetadataManualEdit || this._showMetadataRename || this._showToolPaths || this._showAudioSettings || this._showAdvancedSettings || this._showDelay || this._showEditMapEditor || this._showEncodingProfiles || this._showInfo || this._showMediaInfo;
        }

        /// <summary>
        /// Gestisce tastiera context menu
        /// </summary>
        /// <param name="key">Tasto</param>
        /// <returns>True se gestito</returns>
        private async Task<bool> HandleContextMenuKeyAsync(string key)
        {
            bool result = false;

            if (key == "Escape")
            {
                this.CloseContextMenu();
                result = true;
            }
            else if (key == "ArrowDown")
            {
                if (this._contextMenuCommands.Count > 0)
                {
                    this._contextMenuSelectedIndex++;
                    if (this._contextMenuSelectedIndex >= this._contextMenuCommands.Count)
                        this._contextMenuSelectedIndex = 0;
                }

                result = true;
            }
            else if (key == "ArrowUp")
            {
                if (this._contextMenuCommands.Count > 0)
                {
                    this._contextMenuSelectedIndex--;
                    if (this._contextMenuSelectedIndex < 0)
                        this._contextMenuSelectedIndex = this._contextMenuCommands.Count - 1;
                }

                result = true;
            }
            else if (key == "Enter" || key == " ")
            {
                await this.HandleContextMenuSelect(this._contextMenuSelectedIndex);
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Scorre la riga episodio selezionata dentro la viewport tabella
        /// </summary>
        /// <param name="index">Indice riga</param>
        private async Task ScrollEpisodeRowIntoViewAsync(int index)
        {
            if (this._jsModule == null)
                return;

            try
            {
                await this._jsModule.InvokeVoidAsync("scrollEpisodeRowIntoView", index);
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
        private async Task ScrollSplitRowIntoViewAsync(int index)
        {
            if (this._jsModule == null)
                return;

            try
            {
                await this._jsModule.InvokeVoidAsync("scrollSplitRowIntoView", index);
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
        private async Task ScrollMetadataRowIntoViewAsync(int index)
        {
            if (this._jsModule == null)
                return;

            try
            {
                await this._jsModule.InvokeVoidAsync("scrollMetadataRowIntoView", index);
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

            if (this._selectedRecord == null)
                return;

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
            if (this._selectedRecord == null)
                return;

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
            string mediaInfoPath = AppSettingsService.Instance.Settings.Tools.MediaInfoPath;
            bool mediaInfoAvailable;

            // Verifica disponibilità mediainfo
            mediaInfoAvailable = !string.IsNullOrEmpty(mediaInfoPath) &&
                System.IO.File.Exists(mediaInfoPath) &&
                MediaInfoProvider.IsCliExecutablePath(mediaInfoPath);

            this._contextMenuCommands = new List<UiCommandDefinition>();

            // Delay: sempre visibile
            this._contextMenuCommands.Add(new UiCommandDefinition(
                AppText.T("web.context.delay"),
                "",
                "",
                UiCommandPlacement.ContextMenu,
                UiCommandMenuSection.None,
                false,
                () =>
                {
                    this._showContextMenu = false;
                    this._showDelay = true;
                }));

            // MediaInfo sorgente
            if (mediaInfoAvailable && !string.IsNullOrEmpty(record.SourceFilePath) && System.IO.File.Exists(record.SourceFilePath))
            {
                this._contextMenuCommands.Add(new UiCommandDefinition(
                    AppText.T("web.context.mediaInfoSource"),
                    "",
                    "",
                    UiCommandPlacement.ContextMenu,
                    UiCommandMenuSection.None,
                    false,
                    () => this.OpenMediaInfo(record.SourceFilePath, AppText.F("web.mediaInfo.sourceTitle", record.SourceFileName))));
            }

            // MediaInfo lingua
            if (mediaInfoAvailable && !string.IsNullOrEmpty(record.LangFilePath) && System.IO.File.Exists(record.LangFilePath))
            {
                this._contextMenuCommands.Add(new UiCommandDefinition(
                    AppText.T("web.context.mediaInfoLanguage"),
                    "",
                    "",
                    UiCommandPlacement.ContextMenu,
                    UiCommandMenuSection.None,
                    false,
                    () => this.OpenMediaInfo(record.LangFilePath, AppText.F("web.mediaInfo.languageTitle", record.LangFileName))));
            }

            // MediaInfo risultato
            if (mediaInfoAvailable && !string.IsNullOrEmpty(record.ResultFilePath) && System.IO.File.Exists(record.ResultFilePath))
            {
                this._contextMenuCommands.Add(new UiCommandDefinition(
                    AppText.T("web.context.mediaInfoResult"),
                    "",
                    "",
                    UiCommandPlacement.ContextMenu,
                    UiCommandMenuSection.None,
                    false,
                    () => this.OpenMediaInfo(record.ResultFilePath, AppText.F("web.mediaInfo.resultTitle", record.ResultFileName))));
            }
        }

        /// <summary>
        /// Gestisce selezione voce dal context menu
        /// </summary>
        /// <param name="index">Indice voce selezionata</param>
        private async Task HandleContextMenuSelect(int index)
        {
            this._showContextMenu = false;

            if (index >= 0 && index < this._contextMenuCommands.Count)
            {
                await this._contextMenuCommands[index].ExecuteAsync();
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
                return this.SplitOrchestrator.LogText;

            if (this._currentMode == Options.MODE_METADATA)
                return this.MetadataOrchestrator.LogText;

            return this.Orchestrator.LogText;
        }

        /// <summary>
        /// Restituisce progress della modalità corrente
        /// </summary>
        /// <returns>Progress corrente</returns>
        private ProcessingProgressState GetCurrentProgress()
        {
            if (this._currentMode == Options.MODE_SPLIT)
                return this.SplitOrchestrator.Progress;

            if (this._currentMode == Options.MODE_METADATA)
                return this.MetadataOrchestrator.Progress;

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
                return "";

            for (int i = 0; i < record.FileInfo.Tracks.Count; i++)
            {
                if (record.FileInfo.Tracks[i].TrackKind == "video")
                    video++;
                else if (record.FileInfo.Tracks[i].TrackKind == "audio")
                    audio++;
                else if (record.FileInfo.Tracks[i].TrackKind == "subtitles")
                    subtitles++;
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
                return "-";

            for (int i = 0; i < info.Tracks.Count; i++)
            {
                if (info.Tracks[i].TrackKind == "video")
                {
                    video = info.Tracks[i];
                    break;
                }
            }

            if (video == null)
                return "-";

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
                return null;

            if (simulated)
                return record.FileInfo;

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

            if (string.IsNullOrEmpty(result))
                result = track.Format;

            if (!string.IsNullOrEmpty(width) && !string.IsNullOrEmpty(height))
                AddMetadataSummaryPart(ref result, width + "x" + height);

            if (!string.IsNullOrEmpty(fps))
                AddMetadataSummaryPart(ref result, fps + "fps");

            if (!string.IsNullOrEmpty(bitDepth))
                AddMetadataSummaryPart(ref result, bitDepth + "bit");

            if (!string.IsNullOrEmpty(hdr))
                AddMetadataSummaryPart(ref result, hdr);

            return !string.IsNullOrEmpty(result) ? result : "-";
        }

        /// <summary>
        /// Restituisce campo metadata da traccia
        /// </summary>
        /// <param name="track">Traccia metadata</param>
        /// <param name="key">Chiave campo</param>
        /// <returns>Valore campo, vuoto se assente</returns>
        private static string GetMetadataFieldValue(MkvMetadataTrackInfo track, string key)
        {
            string result;
            if (track != null && track.Fields != null && track.Fields.TryGetValue(key, out result))
                return result != null ? result : "";

            return "";
        }

        /// <summary>
        /// Aggiunge parte al riepilogo metadata
        /// </summary>
        /// <param name="value">Riepilogo corrente</param>
        /// <param name="part">Parte da aggiungere</param>
        private static void AddMetadataSummaryPart(ref string value, string part)
        {
            if (string.IsNullOrEmpty(part))
                return;

            if (!string.IsNullOrEmpty(value))
            {
                value += " ";
            }

            value += part;
        }

        #endregion

        #region Azioni

        /// <summary>
        /// Restituisce il titolo della modalità corrente
        /// </summary>
        /// <returns>Titolo modalità</returns>
        private string GetCurrentModeTitle()
        {
            if (this._currentMode == Options.MODE_SPLIT)
                return "Split";
            if (this._currentMode == Options.MODE_METADATA)
                return AppText.T("web.metadata.title");

            return "Remux";
        }

        /// <summary>
        /// Restituisce la descrizione della modalità corrente
        /// </summary>
        /// <returns>Descrizione modalità</returns>
        private string GetCurrentModeDescription()
        {
            if (this._currentMode == Options.MODE_SPLIT)
                return AppText.T("web.dashboard.splitDescription");
            if (this._currentMode == Options.MODE_METADATA)
                return AppText.T("web.dashboard.metadataDescription");

            return AppText.T("web.dashboard.remuxDescription");
        }

        /// <summary>
        /// Cambia modalità UI e salva preferenza
        /// </summary>
        /// <param name="mode">Modalità richiesta</param>
        private void SwitchMode(string mode)
        {
            if (mode != Options.MODE_REMUX && mode != Options.MODE_SPLIT && mode != Options.MODE_METADATA)
                return;

            if (this.IsAnyBusy() && mode != this._currentMode)
                return;

            this._currentMode = mode;
            AppSettingsService.Instance.Settings.Ui.LastMode = mode;
            AppSettingsService.Instance.Save();
        }

        /// <summary>
        /// Imposta lo stato compatto della navigazione laterale
        /// </summary>
        /// <param name="collapsed">True per mostrare soltanto le icone</param>
        private void SetNavigationCollapsed(bool collapsed)
        {
            this._navigationCollapsed = collapsed;
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
            if (!this.SplitOrchestrator.ApplyOptions(opts, out errorMessage) && !string.IsNullOrEmpty(errorMessage))
            {
                this.SplitOrchestrator.Log(errorMessage);
                this.NotificationService.Notify(NotificationSeverity.Warning, AppText.T("web.split.notify.configTitle"), errorMessage, 8000);
                this.StateHasChanged();
                return false;
            }

            return true;
        }

        /// <summary>
        /// Costruisce il piano del record split selezionato
        /// </summary>
        private void DoAnalyzeSplitSelected()
        {
            this.DoSplitSelected(true);
        }

        /// <summary>
        /// Costruisce il piano di tutti i record split
        /// </summary>
        private void DoAnalyzeSplitAll()
        {
            if (!this.ApplySplitConfig())
                return;

            this.SplitOrchestrator.Analyze(null);
        }

        /// <summary>
        /// Esegue scan cartelle
        /// </summary>
        private void DoScan()
        {
            if (this._currentMode == Options.MODE_METADATA)
            {
                if (string.IsNullOrEmpty(this.MetadataOrchestrator.CurrentOptions.Metadata.SourcePath))
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
                    return;

                // Un nuovo scan azzera i segmenti costruiti nell'editor: non deve succedere in silenzio
                if (this.SplitOrchestrator.CountOverrides() > 0)
                {
                    _ = this.ConfirmSplitScanAsync();
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
                return;

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
                return;

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
                return;

            if (this._currentMode == Options.MODE_SPLIT)
                return;

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
                    _ = this.ApplyMetadataAsync(this.MetadataOrchestrator.SelectedIndex);
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
                    _ = this.ApplyMetadataAsync(-1);
                }
                return;
            }

            if (this._currentMode == Options.MODE_SPLIT)
            {
                if (!this.ApplySplitConfig())
                    return;

                this.SplitOrchestrator.SplitAll();
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
            if (this.MetadataOrchestrator.IsBusy)
            {
                this.MetadataOrchestrator.Stop();
                return;
            }

            if (this.SplitOrchestrator.IsBusy)
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
        /// Costruisce la definizione condivisa dei comandi della modalità corrente
        /// </summary>
        /// <returns>Comandi per menu, toolbar e status bar</returns>
        private IReadOnlyList<UiCommandDefinition> BuildUiCommands()
        {
            bool busy = this.IsAnyBusy();
            List<UiCommandDefinition> result = new List<UiCommandDefinition>();

            if (this._currentMode == Options.MODE_METADATA)
            {
                this.AddMetadataUiCommands(result, busy);
            }
            else if (this._currentMode == Options.MODE_SPLIT)
            {
                this.AddSplitUiCommands(result, busy);
            }
            else
            {
                this.AddRemuxUiCommands(result, busy);
            }

            result.Add(new UiCommandDefinition(
                AppText.T("web.menu.stop"),
                "F12",
                "stop_circle",
                UiCommandPlacement.Menu | UiCommandPlacement.Toolbar | UiCommandPlacement.Status,
                UiCommandMenuSection.Actions,
                !busy,
                this.DoStop)
            {
                ToolbarLabel = AppText.T("web.status.stop"),
                StatusLabel = AppText.T("web.status.stop"),
                SecondaryToolbar = true,
                DangerToolbar = true,
                ToolbarOrder = 20,
                StatusOrder = 90
            });
            if (this._currentMode != Options.MODE_REMUX)
            {
                result.Add(new UiCommandDefinition(
                    AppText.T("web.menu.toolPaths"),
                    "",
                    "",
                    UiCommandPlacement.Menu,
                    UiCommandMenuSection.Settings,
                    busy,
                    this.ShowToolPaths));
            }

            result.Add(new UiCommandDefinition(
                AppText.T("web.menu.info"),
                "",
                "",
                UiCommandPlacement.Menu,
                UiCommandMenuSection.Help,
                busy,
                this.ShowInfo));

            return result;
        }

        /// <summary>
        /// Esegue un comando condiviso nel coordinator Dashboard
        /// </summary>
        /// <param name="command">Comando richiesto</param>
        private async Task ExecuteUiCommandAsync(UiCommandDefinition command)
        {
            if (command != null)
            {
                await command.ExecuteAsync();
            }
        }

        /// <summary>
        /// Aggiunge i comandi Metadata
        /// </summary>
        /// <param name="commands">Lista destinazione</param>
        /// <param name="busy">Stato operativo globale</param>
        private void AddMetadataUiCommands(List<UiCommandDefinition> commands, bool busy)
        {
            UiCommandPlacement allSurfaces = UiCommandPlacement.Menu | UiCommandPlacement.Toolbar | UiCommandPlacement.Status;

            commands.Add(new UiCommandDefinition(AppText.T("web.menu.metadata.clear"), "Ctrl+L", "", UiCommandPlacement.Menu | UiCommandPlacement.Status, UiCommandMenuSection.File, busy, this.DoClear)
            {
                StatusLabel = AppText.T("web.status.metadata.clear"),
                StatusOrder = 100
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.metadata.loadPreset"), "F3", "rule", allSurfaces, UiCommandMenuSection.File, busy, this.ShowMetadataPreset)
            {
                ToolbarLabel = AppText.T("web.status.metadata.preset"),
                StatusLabel = AppText.T("web.status.metadata.preset"),
                ToolbarOrder = 20,
                StatusOrder = 20
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.metadata.manualEdit"), "F4", "edit_note", allSurfaces, UiCommandMenuSection.Actions, busy, this.ShowMetadataManualEdit)
            {
                ToolbarLabel = AppText.T("web.status.metadata.manualEdit"),
                StatusLabel = AppText.T("web.status.metadata.manualEdit"),
                ToolbarOrder = 40,
                StatusOrder = 30
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.metadata.scanInput"), "F5", "folder_open", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoScan)
            {
                ToolbarLabel = AppText.T("web.status.scan"),
                StatusLabel = AppText.T("web.status.scan"),
                ToolbarOrder = 10,
                StatusOrder = 40
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.metadata.analyze"), "F6", "manage_search", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoAnalyzeAll)
            {
                ToolbarLabel = AppText.T("web.status.metadata.analyze"),
                StatusLabel = AppText.T("web.status.metadata.analyze"),
                ToolbarOrder = 30,
                StatusOrder = 50
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.metadata.applySelected"), "F9", "publish", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoMergeSelected)
            {
                StatusLabel = AppText.T("web.status.metadata.apply"),
                ToolbarOrder = 60,
                StatusOrder = 60
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.metadata.applyAll"), "F10", "done_all", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoMergeAll)
            {
                StatusLabel = AppText.T("web.status.all"),
                ToolbarOrder = 70,
                StatusOrder = 70
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.metadata.advancedRename"), "F11", "drive_file_rename_outline", allSurfaces, UiCommandMenuSection.Actions, busy, null)
            {
                ToolbarLabel = AppText.T("web.status.metadata.rename"),
                StatusLabel = AppText.T("web.status.metadata.rename"),
                ToolbarOrder = 50,
                StatusOrder = 80,
                AsyncCallback = this.ShowMetadataRenameAsync
            });
        }

        /// <summary>
        /// Aggiunge i comandi Split
        /// </summary>
        /// <param name="commands">Lista destinazione</param>
        /// <param name="busy">Stato operativo globale</param>
        private void AddSplitUiCommands(List<UiCommandDefinition> commands, bool busy)
        {
            UiCommandPlacement allSurfaces = UiCommandPlacement.Menu | UiCommandPlacement.Toolbar | UiCommandPlacement.Status;

            commands.Add(new UiCommandDefinition(AppText.T("web.menu.configSplit"), "F2", "settings", allSurfaces, UiCommandMenuSection.File, busy, this.ShowConfig)
            {
                ToolbarLabel = AppText.T("web.status.config"),
                StatusLabel = AppText.T("web.status.config"),
                SecondaryToolbar = true,
                ToolbarOrder = 10,
                StatusOrder = 10
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.scanInput"), "F5", "folder_open", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoScan)
            {
                ToolbarLabel = AppText.T("web.status.scan"),
                StatusLabel = AppText.T("web.status.scan"),
                ToolbarOrder = 10,
                StatusOrder = 20
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.split.analyzeSelected"), "F6", "manage_search", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoAnalyzeSplitSelected)
            {
                ToolbarLabel = AppText.T("web.status.split.analyzeSelected"),
                StatusLabel = AppText.T("web.status.split.analyzeSelected"),
                ToolbarOrder = 15,
                StatusOrder = 22
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.split.analyzeAll"), "F7", "checklist", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoAnalyzeSplitAll)
            {
                ToolbarLabel = AppText.T("web.status.split.analyzeAll"),
                StatusLabel = AppText.T("web.status.split.analyzeAll"),
                ToolbarOrder = 17,
                StatusOrder = 24
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.context.split.skip"), "F8", "block", UiCommandPlacement.Menu | UiCommandPlacement.Status, UiCommandMenuSection.Actions, busy, this.ToggleSplitSkip)
            {
                StatusLabel = AppText.T("web.status.skip"),
                StatusOrder = 25
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.split.splitSelected"), "F9", "content_cut", allSurfaces, UiCommandMenuSection.Actions, busy, () => this.DoSplitSelected(false))
            {
                ToolbarLabel = AppText.T("web.status.split.splitSelected"),
                StatusLabel = AppText.T("web.status.split.splitSelected"),
                ToolbarOrder = 19,
                StatusOrder = 26
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.splitAll"), "F10", "call_split", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoMergeAll)
            {
                ToolbarLabel = AppText.T("web.status.splitAll"),
                StatusLabel = AppText.T("web.status.splitAll"),
                ToolbarOrder = 20,
                StatusOrder = 30
            });
        }

        /// <summary>
        /// Aggiunge i comandi Remux
        /// </summary>
        /// <param name="commands">Lista destinazione</param>
        /// <param name="busy">Stato operativo globale</param>
        private void AddRemuxUiCommands(List<UiCommandDefinition> commands, bool busy)
        {
            UiCommandPlacement allSurfaces = UiCommandPlacement.Menu | UiCommandPlacement.Toolbar | UiCommandPlacement.Status;

            commands.Add(new UiCommandDefinition(AppText.T("web.menu.config"), "F2", "settings", allSurfaces, UiCommandMenuSection.File, busy, this.ShowConfig)
            {
                ToolbarLabel = AppText.T("web.status.config"),
                StatusLabel = AppText.T("web.status.config"),
                SecondaryToolbar = true,
                ToolbarOrder = 10,
                StatusOrder = 10
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.scanFile"), "F5", "folder_open", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoScan)
            {
                ToolbarLabel = AppText.T("web.status.scan"),
                StatusLabel = AppText.T("web.status.scan"),
                ToolbarOrder = 10,
                StatusOrder = 20
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.analyzeSelected"), "F6", "manage_search", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoAnalyzeSelected)
            {
                ToolbarLabel = AppText.T("web.status.analyze"),
                StatusLabel = AppText.T("web.status.analyze"),
                ToolbarOrder = 20,
                StatusOrder = 30
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.analyzeAll"), "F7", "travel_explore", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoAnalyzeAll)
            {
                StatusLabel = AppText.T("web.status.all"),
                ToolbarOrder = 30,
                StatusOrder = 40
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.skipUnskip"), "F8", "skip_next", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoToggleSkip)
            {
                ToolbarLabel = AppText.T("web.status.skip"),
                StatusLabel = AppText.T("web.status.skip"),
                SeparatorBefore = true,
                ToolbarOrder = 40,
                StatusOrder = 50
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.processSelected"), "F9", "merge_type", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoMergeSelected)
            {
                ToolbarLabel = AppText.T("web.status.process"),
                StatusLabel = AppText.T("web.status.process"),
                SeparatorBefore = true,
                ToolbarOrder = 50,
                StatusOrder = 60
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.processAll"), "F10", "done_all", allSurfaces, UiCommandMenuSection.Actions, busy, this.DoMergeAll)
            {
                StatusLabel = AppText.T("web.status.all"),
                ToolbarOrder = 60,
                StatusOrder = 70
            });
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.toolPaths"), "", "", UiCommandPlacement.Menu, UiCommandMenuSection.Settings, busy, this.ShowToolPaths));
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.audio"), "", "", UiCommandPlacement.Menu, UiCommandMenuSection.Settings, busy, this.ShowAudioSettings));
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.advancedSettings"), "", "", UiCommandPlacement.Menu, UiCommandMenuSection.Settings, busy, this.ShowAdvancedSettings));
            commands.Add(new UiCommandDefinition(AppText.T("web.menu.encodingProfiles"), "", "", UiCommandPlacement.Menu, UiCommandMenuSection.Settings, busy, this.ShowEncodingProfiles));
        }

        /// <summary>
        /// Verifica se uno degli orchestrator sta eseguendo un'operazione
        /// </summary>
        /// <returns>True se l'applicazione è occupata</returns>
        private bool IsAnyBusy()
        {
            return this.Orchestrator.IsBusy || this.SplitOrchestrator.IsBusy || this.MetadataOrchestrator.IsBusy;
        }

        /// <summary>
        /// Mostra dialog configurazione
        /// </summary>
        private void ShowConfig()
        {
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
        /// Mostra editor manuale metadata per il file selezionato
        /// </summary>
        private void ShowMetadataManualEdit()
        {
            if (this._currentMode != Options.MODE_METADATA)
                return;

            if (this._selectedMetadataRecord == null)
            {
                this.MetadataOrchestrator.Log(AppText.T("web.dashboard.noFileSelected"));
                return;
            }

            this.MetadataOrchestrator.PopulateSelectedTags(this.MetadataOrchestrator.SelectedIndex);
            this._metadataRecords = this.MetadataOrchestrator.GetRecords();
            this.SyncSelectedFromMetadataOrchestrator();
            this._showMetadataManualEdit = true;
        }

        /// <summary>
        /// Chiude editor manuale metadata
        /// </summary>
        private void CloseMetadataManualEdit()
        {
            this._showMetadataManualEdit = false;
        }

        /// <summary>
        /// Applica modifiche manuali metadata
        /// </summary>
        /// <param name="changes">Modifiche manuali</param>
        private void ApplyMetadataManualEdit(List<MkvMetadataChange> changes)
        {
            this._showMetadataManualEdit = false;
            this.MetadataOrchestrator.ApplyManualChanges(this.MetadataOrchestrator.SelectedIndex, changes);
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
            else if (!string.IsNullOrEmpty(errorMessage))
            {
                this.MetadataOrchestrator.Log(errorMessage);
                this.NotificationService.Notify(NotificationSeverity.Warning, AppText.T("web.metadata.notify.invalidConfigTitle"), errorMessage, 8000);
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
        /// Alterna la sovrascrittura degli output metadata già presenti
        /// </summary>
        private void ToggleMetadataOverwriteOutput()
        {
            MkvMetadataOptions metadata = this.GetMetadataOptions();
            metadata.OverwriteOutput = !metadata.OverwriteOutput;
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
                this._metadataBrowseInitialPath = !string.IsNullOrEmpty(metadata.OutputDir) ? metadata.OutputDir : this.MetadataOrchestrator.CurrentOptions.DestinationFolder;
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
            else if (!string.IsNullOrEmpty(errorMessage))
            {
                this.MetadataOrchestrator.Log(errorMessage);
                this.NotificationService.Notify(NotificationSeverity.Warning, AppText.T("web.metadata.notify.invalidConfigTitle"), errorMessage, 8000);
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
        private async Task ShowMetadataRenameAsync()
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
                Dictionary<string, object> parameters = new Dictionary<string, object>();
                parameters.Add(nameof(MetadataRenamerDialogComponent.Records), this._metadataRecords);

                DialogOptions options = new DialogOptions();
                options.Width = "min(96rem, calc(100vw - 4rem))";
                options.Height = "min(54rem, calc(100vh - 4rem))";
                options.Resizable = true;
                options.Draggable = true;
                options.CloseDialogOnOverlayClick = false;
                options.CssClass = "rf-renamer-dialog";
                options.ContentCssClass = "rf-renamer-dialog-content";

                dynamic result = await this.DialogService.OpenAsync<MetadataRenamerDialogComponent>(
                    AppText.T("web.rename.title"),
                    parameters,
                    options);

                this.CloseMetadataRename(result is bool && result);
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
                else if (!string.IsNullOrEmpty(errorMessage))
                {
                    this.SplitOrchestrator.Log(errorMessage);
                    this.NotificationService.Notify(NotificationSeverity.Error, AppText.T("validation.invalidConfig"), errorMessage, 8000);
                }
            }
            else if (this.Orchestrator.ApplyOptions(opts, out errorMessage))
            {
                this._showConfig = false;
            }
            else if (!string.IsNullOrEmpty(errorMessage))
            {
                this.Orchestrator.Log(errorMessage);
                this.NotificationService.Notify(NotificationSeverity.Error, AppText.T("validation.invalidConfig"), errorMessage, 8000);
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
                this.Orchestrator.UpdateDelay(this.Orchestrator.SelectedIndex, delays.Item1, delays.Item2);
        }

        /// <summary>
        /// Apre l'editor EditMap sul record Remux selezionato
        /// </summary>
        private void ShowEditMapEditor()
        {
            int index = this.Orchestrator.SelectedIndex;
            if (this.Orchestrator.IsBusy || index < 0 || index >= this._records.Count)
                return;

            FileProcessingRecord record = this._records[index];
            if (record == null || string.IsNullOrEmpty(record.SourceFilePath) || string.IsNullOrEmpty(record.LangFilePath) ||
                (record.Status != FileStatus.Pending && record.Status != FileStatus.Analyzed && record.Status != FileStatus.Error))
                return;

            this._editMapRecordIndex = index;
            this._editMapRecord = record;
            this._showEditMapEditor = true;
        }

        /// <summary>
        /// Apre l'editor visuale dei segmenti sul record Split selezionato
        /// </summary>
        /// <param name="segmentNum">Segmento da preselezionare, 0 per il primo</param>
        private void OpenSplitEditor(int segmentNum)
        {
            int index = this.SplitOrchestrator.SelectedIndex;

            if (this.SplitOrchestrator.IsBusy || index < 0 || index >= this._splitRecords.Count)
                return;

            MkvSplitRecord record = this._splitRecords[index];
            if (record == null || record.Plan == null)
            {
                this.NotificationService.Notify(NotificationSeverity.Warning, AppText.T("web.splitEditor.notAnalyzed"), AppText.T("web.splitEditor.analyzeFirst"), 6000);
                return;
            }

            this._splitEditorIndex = index;
            this._splitEditorRecord = record;
            this._splitEditorSegmentNum = segmentNum;
            this._splitEditorOverride = this.SplitOrchestrator.GetOverride(index);
            this._showSplitEditor = true;
        }

        /// <summary>
        /// Chiude l'editor visuale dei segmenti
        /// </summary>
        private void CloseSplitEditor()
        {
            this._showSplitEditor = false;
            this._splitEditorRecord = null;
            this._splitEditorOverride = null;
            this._splitEditorIndex = -1;
            this._splitEditorSegmentNum = 0;
        }

        /// <summary>
        /// Applica i segmenti costruiti nell'editor al record Split aperto
        /// </summary>
        /// <param name="segments">Segmenti costruiti nell'editor</param>
        private void ApplySplitOverride(List<MkvSplitOverrideSegment> segments)
        {
            string fileName = this._splitEditorRecord != null ? System.IO.Path.GetFileName(this._splitEditorRecord.InputFile) : "";

            if (this._splitEditorIndex < 0)
                return;

            this.SplitOrchestrator.SetOverride(this._splitEditorIndex, segments);
            this.NotificationService.Notify(NotificationSeverity.Success, AppText.T("web.split.overrideBadge"), AppText.F("web.split.overrideApplied", fileName), 5000);
            this.CloseSplitEditor();
        }

        /// <summary>
        /// Riporta il record Split selezionato sotto la configurazione globale
        /// </summary>
        private void ClearSplitOverride()
        {
            int index = this._showSplitEditor ? this._splitEditorIndex : this.SplitOrchestrator.SelectedIndex;
            MkvSplitRecord record = index >= 0 && index < this._splitRecords.Count ? this._splitRecords[index] : null;

            if (record == null || !record.IsOverride)
                return;

            this.SplitOrchestrator.ClearOverride(index);
            this.NotificationService.Notify(NotificationSeverity.Info, AppText.T("web.split.clearOverride"), AppText.F("web.split.overrideCleared", System.IO.Path.GetFileName(record.InputFile)), 5000);
            if (this._showSplitEditor)
                this.CloseSplitEditor();
        }

        /// <summary>
        /// Applica i metadata chiedendo conferma quando l'operazione non è reversibile
        /// </summary>
        /// <param name="selectedIndex">Indice record selezionato, oppure -1 per tutti i record</param>
        private async Task ApplyMetadataAsync(int selectedIndex)
        {
            int fileCount;
            int trackRemovalCount;
            bool inPlace;
            string message;

            if (this.MetadataOrchestrator.NeedsApplyConfirmation(selectedIndex, out fileCount, out trackRemovalCount, out inPlace))
            {
                message = inPlace
                    ? AppText.F("web.metadata.confirmOverwrite", fileCount)
                    : AppText.F("web.metadata.confirmApply", fileCount);

                if (trackRemovalCount > 0)
                    message += Environment.NewLine + AppText.F("web.metadata.confirmTrackRemoval", trackRemovalCount);

                if (!await this.JsRuntime.InvokeAsync<bool>("confirm", message))
                    return;
            }

            if (this.MetadataOrchestrator.IsBusy)
                return;

            if (selectedIndex >= 0)
                this.MetadataOrchestrator.ApplySelected(selectedIndex);
            else
                this.MetadataOrchestrator.ApplyAll();
        }

        /// <summary>
        /// Chiede conferma prima di uno scan che azzera i segmenti costruiti nell'editor
        /// </summary>
        private async Task ConfirmSplitScanAsync()
        {
            bool confirmed = await this.JsRuntime.InvokeAsync<bool>("confirm", AppText.F("web.split.scanClearsOverrides", this.SplitOrchestrator.CountOverrides()));

            if (!confirmed)
                return;
            if (!this.SplitOrchestrator.IsBusy)
                this.SplitOrchestrator.Scan();
        }

        /// <summary>
        /// Chiude l'editor EditMap e libera lo snapshot aperto
        /// </summary>
        private void CloseEditMapEditor()
        {
            this._showEditMapEditor = false;
            this._editMapRecord = null;
            this._editMapRecordIndex = -1;
        }

        /// <summary>
        /// Applica la copia validata prodotta dall'editor EditMap
        /// </summary>
        /// <param name="request">Mappa e durate indicizzate</param>
        private void ApplyEditMap((EditMap Map, double SourceDurationMs, double LanguageDurationMs, double SourceTailToleranceMs) request)
        {
            if (this._editMapRecord == null)
                return;

            bool applied = this.Orchestrator.UpdateEditMap(this._editMapRecordIndex, this._editMapRecord.EpisodeId, this._editMapRecord.SourceFilePath, this._editMapRecord.LangFilePath, request.Map, request.SourceDurationMs, request.LanguageDurationMs, request.SourceTailToleranceMs, out string errorMessage);
            if (applied)
            {
                this.CloseEditMapEditor();
            }
            else
            {
                this.NotificationService.Notify(NotificationSeverity.Warning, AppText.T("web.editMap.applyFailed"), errorMessage, 8000);
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
        /// Chiude tutti i dialog aperti
        /// </summary>
        private void CloseAllDialogs()
        {
            if (this._showMetadataRename)
                this.DialogService.Close(false);

            this._showConfig = false;
            this._showMetadataPathBrowse = false;
            this._showMetadataPreset = false;
            this._showMetadataMappedInfo = false;
            this._showMetadataManualEdit = false;
            this._showMetadataRename = false;
            this._showToolPaths = false;
            this._showAudioSettings = false;
            this._showAdvancedSettings = false;
            this._showDelay = false;
            this._showEditMapEditor = false;
            this._editMapRecord = null;
            this._editMapRecordIndex = -1;
            this._showEncodingProfiles = false;
            this._showInfo = false;
            this._showContextMenu = false;
            this._showMediaInfo = false;
        }

        /// <summary>
        /// Cambia tema Radzen e salva in AppSettings
        /// </summary>
        /// <param name="theme">Nome tema kebab-case</param>
        private Task ChangeThemeAsync(string theme)
        {
            this._currentTheme = theme;

            // Salva in AppSettings
            AppSettingsService.Instance.Settings.Ui.Theme = theme;
            AppSettingsService.Instance.Save();
            this.ThemeService.SetTheme(theme);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Cambia lingua UI e salva in AppSettings
        /// </summary>
        /// <param name="language">Codice lingua</param>
        private async Task ChangeLanguageAsync(string language)
        {
            string normalizedLanguage = AppText.NormalizeLanguage(language);
            if (string.IsNullOrEmpty(normalizedLanguage))
                return;

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
                    await this._jsModule.InvokeVoidAsync("setLanguage", normalizedLanguage);
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
