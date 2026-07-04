using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Metadata;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Orchestratore WebUI per modalità Metadata
    /// </summary>
    public class MetadataOrchestrator
    {
        #region Costanti

        /// <summary>
        /// Limite massimo dimensione log in caratteri
        /// </summary>
        private const int LOG_MAX_LENGTH = 500000;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Opzioni correnti Metadata
        /// </summary>
        private Options _options;

        /// <summary>
        /// Record metadata correnti
        /// </summary>
        private List<MkvMetadataRecord> _records;

        /// <summary>
        /// Lock per accesso thread-safe allo stato
        /// </summary>
        private object _lock;

        /// <summary>
        /// Stato avanzamento operazione corrente
        /// </summary>
        private ProcessingProgressState _progress;

        /// <summary>
        /// True se è in corso un'operazione
        /// </summary>
        private volatile bool _isBusy;

        /// <summary>
        /// True se è stato richiesto lo stop cooperativo
        /// </summary>
        private volatile bool _stopRequested;

        /// <summary>
        /// Buffer log accumulato
        /// </summary>
        private string _logText;

        /// <summary>
        /// Indice record selezionato
        /// </summary>
        private int _selectedIndex;

        #endregion

        #region Eventi

        /// <summary>
        /// Evento emesso per ogni messaggio di log
        /// </summary>
        public event Action<string> OnLog;

        /// <summary>
        /// Evento emesso quando i record vengono aggiornati
        /// </summary>
        public event Action OnRecordsChanged;

        /// <summary>
        /// Evento emesso quando cambia lo stato avanzamento
        /// </summary>
        public event Action OnProgressChanged;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataOrchestrator()
        {
            this._options = new Options();
            this._options.Mode = Options.MODE_METADATA;
            this._records = new List<MkvMetadataRecord>();
            this._lock = new object();
            this._progress = new ProcessingProgressState();
            this._isBusy = false;
            this._stopRequested = false;
            this._logText = AppText.T("web.metadata.ready");
            this._selectedIndex = -1;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Applica opzioni Metadata
        /// </summary>
        /// <param name="options">Opzioni</param>
        /// <param name="errorMessage">Errore</param>
        /// <returns>True se valide</returns>
        public bool ApplyOptions(Options options, out string errorMessage)
        {
            OptionsValidationResult validation;
            errorMessage = "";

            if (options == null)
            {
                errorMessage = AppText.T("validation.invalidConfig");
                return false;
            }

            options.Mode = Options.MODE_METADATA;
            if (options.Metadata == null)
            {
                options.Metadata = new MkvMetadataOptions();
            }

            options.Metadata.SourcePath = !string.IsNullOrEmpty(options.Metadata.SourcePath) ? options.Metadata.SourcePath : options.SourceFolder;
            options.Metadata.OutputDir = !string.IsNullOrEmpty(options.Metadata.OutputDir) ? options.Metadata.OutputDir : options.DestinationFolder;
            options.Metadata.Recursive = options.Recursive;
            options.Metadata.DryRun = options.DryRun;

            validation = OptionsValidator.Validate(options, false, false);
            if (!validation.IsValid)
            {
                errorMessage = validation.ErrorMessage;
                return false;
            }

            lock (this._lock)
            {
                this._options = options;
                this.MarkAnalysisStaleLocked();
            }

            this.AppendLog(AppText.T("web.metadata.configApplied"));
            this.OnRecordsChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Esegue scan input
        /// </summary>
        public void Scan()
        {
            if (this._isBusy)
                return;

            Thread thread = new Thread(this.ScanWorker);
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Esegue analisi Metadata senza scrivere file
        /// </summary>
        public void AnalyzeAll()
        {
            if (this._isBusy)
                return;

            Thread thread = new Thread(this.AnalyzeAllWorker);
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Applica tutti i record analizzati
        /// </summary>
        public void ApplyAll()
        {
            if (this._isBusy)
                return;

            Thread thread = new Thread(() => this.ApplyWorker(-1));
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Applica un singolo record analizzato
        /// </summary>
        /// <param name="index">Indice record</param>
        public void ApplySelected(int index)
        {
            if (this._isBusy)
                return;

            Thread thread = new Thread(() => this.ApplyWorker(index));
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Popola i tag esistenti sul record selezionato
        /// </summary>
        /// <param name="index">Indice record</param>
        public void PopulateSelectedTags(int index)
        {
            MkvMetadataRecord record;
            MetadataExecutionService tagReader;
            string mkvExtractPath;

            if (this._isBusy)
                return;

            lock (this._lock)
            {
                if (index < 0 || index >= this._records.Count)
                    return;

                record = this._records[index];
            }

            try
            {
                mkvExtractPath = AppSettingsService.Instance.Settings.Tools.MkvExtractPath;
                tagReader = new MetadataExecutionService("", "", mkvExtractPath);
                tagReader.PopulateExistingTags(record);
                this.OnRecordsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.metadata.manualEdit.tagLoadError", ex.Message));
            }
        }

        /// <summary>
        /// Applica modifiche manuali metadata a un record
        /// </summary>
        /// <param name="index">Indice record</param>
        /// <param name="changes">Modifiche manuali</param>
        public void ApplyManualChanges(int index, List<MkvMetadataChange> changes)
        {
            if (this._isBusy)
                return;

            Thread thread = new Thread(() => this.ApplyManualWorker(index, changes));
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Pulisce stato corrente
        /// </summary>
        public void Clear()
        {
            lock (this._lock)
            {
                this._records.Clear();
                this._selectedIndex = -1;
            }

            this.AppendLog(AppText.T("web.metadata.stateCleared"));
            this.OnRecordsChanged?.Invoke();
        }

        /// <summary>
        /// Richiede stop cooperativo
        /// </summary>
        public void Stop()
        {
            this._stopRequested = true;
            this.AppendLog(AppText.T("web.metadata.stopRequested"));
        }

        /// <summary>
        /// Scrive log
        /// </summary>
        /// <param name="message">Messaggio</param>
        public void Log(string message)
        {
            this.AppendLog(message);
        }

        /// <summary>
        /// Restituisce copia record
        /// </summary>
        /// <returns>Record correnti</returns>
        public List<MkvMetadataRecord> GetRecords()
        {
            lock (this._lock)
            {
                return new List<MkvMetadataRecord>(this._records);
            }
        }

        /// <summary>
        /// Restituisce preset disponibili
        /// </summary>
        /// <returns>Lista preset</returns>
        public List<string> GetPresetFiles()
        {
            MetadataPresetService service = new MetadataPresetService(AppSettingsService.Instance.ConfigFolder);
            return service.ListPresetFiles();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Worker scansione input Metadata
        /// </summary>
        private void ScanWorker()
        {
            MetadataFileScanner scanner;
            MetadataMediaInfoReader reader;
            List<MkvMetadataRecord> records;
            string mediaInfoPath;

            this.SetBusy(true, AppText.T("web.metadata.progress.scan"));
            try
            {
                mediaInfoPath = AppSettingsService.Instance.Settings.Tools.MediaInfoPath;
                if (string.IsNullOrEmpty(mediaInfoPath))
                    mediaInfoPath = "mediainfo";

                reader = new MetadataMediaInfoReader(mediaInfoPath);
                scanner = new MetadataFileScanner(reader);
                records = scanner.Scan(this._options.Metadata.SourcePath, this._options.Metadata.Recursive);

                lock (this._lock)
                {
                    this._records = records;
                    this._selectedIndex = records.Count > 0 ? 0 : -1;
                }

                this.AppendLog(AppText.F("web.metadata.scanCompleted", records.Count));
                this.OnRecordsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.metadata.scanError", ex.Message));
            }
            finally
            {
                this.SetBusy(false, "");
            }
        }

        /// <summary>
        /// Worker analisi pipeline Metadata
        /// </summary>
        private void AnalyzeAllWorker()
        {
            MkvMetadataPreset preset = null;
            MkvMetadataPresetValidationResult validation;
            MetadataPipelineEvaluator evaluator = new MetadataPipelineEvaluator();
            MetadataExecutionService tagReader;
            string mkvExtractPath;

            this.SetBusy(true, AppText.T("web.metadata.progress.analysis"));
            try
            {
                if (!string.IsNullOrEmpty(this._options.Metadata.PresetPath))
                {
                    MetadataPresetService presetService = new MetadataPresetService(AppSettingsService.Instance.ConfigFolder);
                    preset = presetService.Load(this._options.Metadata.PresetPath);
                    validation = MetadataPresetService.Validate(preset);
                    if (!validation.IsValid)
                        throw new InvalidOperationException(validation.ErrorMessage);
                }

                mkvExtractPath = AppSettingsService.Instance.Settings.Tools.MkvExtractPath;
                tagReader = new MetadataExecutionService("", "", mkvExtractPath);

                lock (this._lock)
                {
                    for (int i = 0; i < this._records.Count; i++)
                    {
                        if (this._stopRequested)
                            break;

                        tagReader.PopulateExistingTags(this._records[i]);
                        evaluator.AnalyzeRecord(this._records[i], preset, this._options.Metadata.OutputPolicy);
                        this._records[i].CommandPreview = MetadataExecutionService.BuildCommandPreview(this._records[i], this._options.Metadata);
                        this._records[i].Status = AppText.T("web.metadata.status.analyzed");
                        this._records[i].ErrorMessage = "";
                    }
                }

                this.AppendLog(AppText.T("web.metadata.analysisCompleted"));
                this.OnRecordsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                this.MarkRecordsError(ex.Message);
                this.AppendLog(AppText.F("web.metadata.analysisError", ex.Message));
            }
            finally
            {
                this.SetBusy(false, "");
            }
        }

        /// <summary>
        /// Worker applicazione record Metadata
        /// </summary>
        /// <param name="selectedIndex">Indice record selezionato, oppure -1 per tutti i record</param>
        private void ApplyWorker(int selectedIndex)
        {
            MetadataExecutionService executor;
            MkvMetadataExecutionResult result;
            string mkvMergePath;
            string mkvPropEditPath;
            string mkvExtractPath;
            int successCount = 0;
            int errorCount = 0;

            this.SetBusy(true, selectedIndex >= 0 ? AppText.T("web.metadata.progress.applySelected") : AppText.T("web.metadata.progress.apply"));
            try
            {
                this.ValidateOutputTargets(selectedIndex);

                mkvMergePath = AppSettingsService.Instance.Settings.Tools.MkvMergePath;
                mkvPropEditPath = AppSettingsService.Instance.Settings.Tools.MkvPropEditPath;
                mkvExtractPath = AppSettingsService.Instance.Settings.Tools.MkvExtractPath;
                executor = new MetadataExecutionService(mkvMergePath, mkvPropEditPath, mkvExtractPath);

                for (int i = 0; i < this._records.Count; i++)
                {
                    if (selectedIndex >= 0 && i != selectedIndex)
                        continue;

                    if (this._stopRequested)
                        break;

                    if (this._records[i].AnalysisStatus != MkvMetadataAnalysisStatus.Analyzed)
                        continue;

                    this.UpdateRecordStatus(i, AppText.T("web.metadata.status.running"), "");
                    result = executor.Execute(this._records[i], this._options.Metadata);
                    if (result.ExitCode == 0)
                    {
                        successCount++;
                        this.UpdateRecordStatus(i, result.DryRun ? AppText.T("web.metadata.status.dryRun") : AppText.T("web.metadata.status.completed"), "");
                    }
                    else
                    {
                        errorCount++;
                        this.UpdateRecordStatus(i, AppText.T("web.metadata.status.error"), result.ErrorMessage);
                    }
                }

                this.AppendLog(AppText.F("web.metadata.applyCompleted", successCount, errorCount));
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.metadata.applyError", ex.Message));
            }
            finally
            {
                this.SetBusy(false, "");
            }
        }

        /// <summary>
        /// Worker applicazione modifiche manuali metadata
        /// </summary>
        /// <param name="selectedIndex">Indice record selezionato</param>
        /// <param name="changes">Modifiche manuali validate dalla UI</param>
        private void ApplyManualWorker(int selectedIndex, List<MkvMetadataChange> changes)
        {
            MkvMetadataRecord sourceRecord;
            MkvMetadataRecord manualRecord;
            MkvMetadataOptions runtimeOptions;
            MkvMetadataExecutionResult result;
            MetadataExecutionService executor;
            MetadataFileScanner scanner;
            List<MkvMetadataRecord> refreshedRecords;
            string mediaInfoPath;
            string outputFile;

            this.SetBusy(true, AppText.T("web.metadata.progress.manualEdit"));
            try
            {
                if (changes == null || changes.Count == 0)
                {
                    this.AppendLog(AppText.T("web.metadata.manualEdit.noChanges"));
                    return;
                }

                for (int i = 0; i < changes.Count; i++)
                {
                    MkvMetadataChange change = changes[i];
                    if (change.RequiresRemux || change.OperationType == MkvMetadataOperationType.RemoveTrack)
                        throw new InvalidOperationException(AppText.T("web.metadata.manualEdit.remuxNotAllowed"));

                    if (change.OperationType == MkvMetadataOperationType.SetTagField || change.OperationType == MkvMetadataOperationType.ClearTagField)
                    {
                        MetadataTagDefinition tag;
                        string normalizedValue;
                        string errorMessage;
                        if (!MetadataTagRegistry.ValidateWritable(change.FieldKey, change.Scope, out errorMessage))
                            throw new InvalidOperationException(errorMessage);

                        if (change.OperationType == MkvMetadataOperationType.SetTagField)
                        {
                            if (!MetadataTagRegistry.TryGet(change.FieldKey, out tag))
                                throw new InvalidOperationException(AppText.F("metadata.validation.tagNotWritable", change.FieldKey));

                            if (!MetadataTagRegistry.ValidateWritableValue(change.FieldKey, change.Scope, change.AfterValue, tag.IsClearable, out normalizedValue, out errorMessage))
                                throw new InvalidOperationException(errorMessage);

                            change.AfterValue = normalizedValue;
                        }

                        continue;
                    }

                    if (change.OperationType == MkvMetadataOperationType.SetField || change.OperationType == MkvMetadataOperationType.ClearField)
                    {
                        MetadataFieldDefinition field;
                        string errorMessage;
                        string normalizedValue;
                        if (!MetadataFieldRegistry.TryGet(change.FieldKey, out field))
                            throw new InvalidOperationException(AppText.F("metadata.validation.unknownField", change.FieldKey));

                        if (!MetadataFieldRegistry.ValidateWritable(change.FieldKey, out errorMessage))
                            throw new InvalidOperationException(errorMessage);

                        if (change.OperationType == MkvMetadataOperationType.ClearField && !field.IsClearable)
                            throw new InvalidOperationException(AppText.F("web.metadata.manualEdit.fieldNotClearable", field.Label));

                        if (change.OperationType == MkvMetadataOperationType.SetField)
                        {
                            if (!MetadataFieldRegistry.ValidateWritableValue(change.FieldKey, change.AfterValue, field.IsClearable, out normalizedValue, out errorMessage))
                                throw new InvalidOperationException(errorMessage);

                            change.AfterValue = normalizedValue;
                        }

                        continue;
                    }

                    throw new InvalidOperationException(AppText.T("web.metadata.manualEdit.operationNotAllowed"));
                }

                lock (this._lock)
                {
                    if (selectedIndex < 0 || selectedIndex >= this._records.Count)
                        throw new InvalidOperationException(AppText.T("web.dashboard.noFileSelected"));

                    sourceRecord = this._records[selectedIndex];
                    sourceRecord.Status = AppText.T("web.metadata.status.running");
                    sourceRecord.ErrorMessage = "";

                    manualRecord = new MkvMetadataRecord();
                    manualRecord.InputFile = sourceRecord.InputFile;
                    manualRecord.FileSize = sourceRecord.FileSize;
                    manualRecord.RelativeFolder = sourceRecord.RelativeFolder;
                    manualRecord.Status = sourceRecord.Status;
                    manualRecord.AnalysisStatus = MkvMetadataAnalysisStatus.Analyzed;
                    manualRecord.ExecutionMode = this._options.Metadata.OutputPolicy == MkvMetadataOutputPolicy.OutputPath ? MkvMetadataExecutionMode.CopyPropEdit : MkvMetadataExecutionMode.PropEdit;
                    manualRecord.FileInfo = MetadataModelCloner.CloneFileInfo(sourceRecord.OriginalFileInfo != null ? sourceRecord.OriginalFileInfo : sourceRecord.FileInfo);
                    manualRecord.OriginalFileInfo = MetadataModelCloner.CloneFileInfo(manualRecord.FileInfo);
                    manualRecord.Changes = new List<MkvMetadataChange>(changes);
                    manualRecord.ChangeCount = changes.Count;
                    manualRecord.MatchCount = 0;
                }

                this.OnRecordsChanged?.Invoke();

                runtimeOptions = new MkvMetadataOptions();
                runtimeOptions.SourcePath = this._options.Metadata.SourcePath;
                runtimeOptions.PresetPath = this._options.Metadata.PresetPath;
                runtimeOptions.OutputPolicy = this._options.Metadata.OutputPolicy;
                runtimeOptions.OutputDir = this._options.Metadata.OutputDir;
                runtimeOptions.Recursive = this._options.Metadata.Recursive;
                runtimeOptions.PreserveFolderStructure = this._options.Metadata.PreserveFolderStructure;
                runtimeOptions.DryRun = false;

                outputFile = MetadataExecutionService.BuildOutputFile(manualRecord, runtimeOptions);
                if (runtimeOptions.OutputPolicy == MkvMetadataOutputPolicy.OutputPath && File.Exists(outputFile))
                    throw new InvalidOperationException(AppText.F("metadata.error.outputExists", outputFile));

                executor = new MetadataExecutionService(
                    AppSettingsService.Instance.Settings.Tools.MkvMergePath,
                    AppSettingsService.Instance.Settings.Tools.MkvPropEditPath,
                    AppSettingsService.Instance.Settings.Tools.MkvExtractPath);

                result = executor.Execute(manualRecord, runtimeOptions);
                if (result.ExitCode != 0)
                {
                    this.UpdateRecordStatus(selectedIndex, AppText.T("web.metadata.status.error"), result.ErrorMessage);
                    this.AppendLog(AppText.F("web.metadata.manualEdit.error", result.ErrorMessage));
                    return;
                }

                if (runtimeOptions.OutputPolicy == MkvMetadataOutputPolicy.Overwrite)
                {
                    mediaInfoPath = AppSettingsService.Instance.Settings.Tools.MediaInfoPath;
                    if (string.IsNullOrEmpty(mediaInfoPath))
                        mediaInfoPath = "mediainfo";

                    scanner = new MetadataFileScanner(new MetadataMediaInfoReader(mediaInfoPath));
                    refreshedRecords = scanner.Scan(manualRecord.InputFile, false);
                    if (refreshedRecords.Count > 0)
                    {
                        executor.PopulateExistingTags(refreshedRecords[0]);
                        refreshedRecords[0].Status = AppText.T("web.metadata.status.completed");
                        lock (this._lock)
                        {
                            this._records[selectedIndex] = refreshedRecords[0];
                        }

                        this.OnRecordsChanged?.Invoke();
                    }
                    else
                    {
                        this.UpdateRecordStatus(selectedIndex, AppText.T("web.metadata.status.completed"), "");
                    }
                }
                else
                {
                    this.UpdateRecordStatus(selectedIndex, AppText.T("web.metadata.status.completed"), "");
                }

                this.AppendLog(AppText.F("web.metadata.manualEdit.completed", result.OutputFile));
            }
            catch (Exception ex)
            {
                this.UpdateRecordStatus(selectedIndex, AppText.T("web.metadata.status.error"), ex.Message);
                this.AppendLog(AppText.F("web.metadata.manualEdit.error", ex.Message));
            }
            finally
            {
                this.SetBusy(false, "");
            }
        }

        /// <summary>
        /// Valida che gli output previsti non collidano e non esistano già
        /// </summary>
        /// <param name="selectedIndex">Indice record selezionato, oppure -1 per tutti i record</param>
        private void ValidateOutputTargets(int selectedIndex)
        {
            Dictionary<string, string> targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            MkvMetadataRecord record;
            string outputFile;

            if (this._options.Metadata.OutputPolicy != MkvMetadataOutputPolicy.OutputPath)
                return;

            for (int i = 0; i < this._records.Count; i++)
            {
                if (selectedIndex >= 0 && i != selectedIndex)
                    continue;

                record = this._records[i];
                if (record.AnalysisStatus != MkvMetadataAnalysisStatus.Analyzed || record.ExecutionMode == MkvMetadataExecutionMode.NoOp)
                    continue;

                outputFile = MetadataExecutionService.BuildOutputFile(record, this._options.Metadata);
                if (targets.ContainsKey(outputFile))
                    throw new InvalidOperationException(AppText.F("metadata.error.outputCollision", outputFile));

                if (File.Exists(outputFile))
                    throw new InvalidOperationException(AppText.F("metadata.error.outputExists", outputFile));

                targets[outputFile] = record.InputFile;
            }
        }

        /// <summary>
        /// Aggiorna stato ed errore di un record
        /// </summary>
        /// <param name="index">Indice record</param>
        /// <param name="status">Stato visualizzato</param>
        /// <param name="errorMessage">Messaggio di errore</param>
        private void UpdateRecordStatus(int index, string status, string errorMessage)
        {
            lock (this._lock)
            {
                if (index < 0 || index >= this._records.Count)
                    return;

                this._records[index].Status = status;
                this._records[index].ErrorMessage = errorMessage != null ? errorMessage : "";
            }

            this.OnRecordsChanged?.Invoke();
        }

        /// <summary>
        /// Segna tutti i record come errore analisi
        /// </summary>
        /// <param name="errorMessage">Messaggio di errore</param>
        private void MarkRecordsError(string errorMessage)
        {
            lock (this._lock)
            {
                for (int i = 0; i < this._records.Count; i++)
                {
                    this._records[i].AnalysisStatus = MkvMetadataAnalysisStatus.Error;
                    this._records[i].Status = AppText.T("web.metadata.status.error");
                    this._records[i].ErrorMessage = errorMessage;
                }
            }

            this.OnRecordsChanged?.Invoke();
        }

        /// <summary>
        /// Segna come stale i record analizzati dopo una modifica opzioni
        /// </summary>
        private void MarkAnalysisStaleLocked()
        {
            for (int i = 0; i < this._records.Count; i++)
            {
                if (this._records[i].AnalysisStatus == MkvMetadataAnalysisStatus.Analyzed)
                {
                    this._records[i].AnalysisStatus = MkvMetadataAnalysisStatus.Stale;
                    this._records[i].Status = AppText.T("web.metadata.status.stale");
                }
            }
        }

        /// <summary>
        /// Aggiorna stato busy e progress corrente
        /// </summary>
        /// <param name="busy">True se è in corso un'operazione</param>
        /// <param name="operation">Descrizione operazione corrente</param>
        private void SetBusy(bool busy, string operation)
        {
            this._stopRequested = false;
            this._isBusy = busy;
            this._progress.IsActive = busy;
            this._progress.Operation = operation;
            this._progress.CurrentStatus = busy ? operation : "";
            this._progress.CurrentIndeterminate = busy;
            this._progress.GlobalIndeterminate = busy;
            this.OnProgressChanged?.Invoke();
        }

        /// <summary>
        /// Aggiunge un messaggio al log interno
        /// </summary>
        /// <param name="message">Messaggio</param>
        private void AppendLog(string message)
        {
            lock (this._lock)
            {
                if (!string.IsNullOrEmpty(this._logText))
                    this._logText += Environment.NewLine;

                this._logText += "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
                if (this._logText.Length > LOG_MAX_LENGTH)
                    this._logText = this._logText.Substring(this._logText.Length - LOG_MAX_LENGTH);
            }

            this.OnLog?.Invoke(message);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Opzioni correnti
        /// </summary>
        public Options CurrentOptions
        {
            get
            {
                return this._options;
            }
        }

        /// <summary>
        /// Stato busy
        /// </summary>
        public bool IsBusy
        {
            get
            {
                return this._isBusy;
            }
        }

        /// <summary>
        /// Log accumulato
        /// </summary>
        public string LogText
        {
            get
            {
                return this._logText;
            }
        }

        /// <summary>
        /// Stato progresso
        /// </summary>
        public ProcessingProgressState Progress
        {
            get
            {
                return this._progress;
            }
        }

        /// <summary>
        /// Indice selezionato
        /// </summary>
        public int SelectedIndex
        {
            get
            {
                return this._selectedIndex;
            }
            set
            {
                this._selectedIndex = value;
            }
        }

        #endregion
    }
}
