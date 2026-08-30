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
    public class MetadataOrchestrator : MediaOrchestratorBase
    {
        #region Variabili di classe

        /// <summary>
        /// Opzioni correnti Metadata
        /// </summary>
        private Options _options;

        /// <summary>
        /// Record metadata correnti
        /// </summary>
        private List<MkvMetadataRecord> _records;

        #endregion

        #region Eventi

        /// <summary>
        /// Evento emesso a fine analisi con i conteggi di file analizzati e falliti
        /// </summary>
        public event Action<int, int> OnAnalysisCompleted;

        /// <summary>
        /// Evento emesso a fine applicazione con i conteggi di file scritti e falliti
        /// </summary>
        public event Action<int, int> OnApplyCompleted;

        /// <summary>
        /// Evento emesso quando un'operazione non parte o si interrompe per un errore globale
        /// </summary>
        public event Action<string> OnOperationFailed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataOrchestrator() : base(AppText.T("web.metadata.ready"), true)
        {
            this._options = new Options();
            this._options.Mode = Options.MODE_METADATA;
            this._records = new List<MkvMetadataRecord>();
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

            lock (this.StateLock)
            {
                this._options = options;
                this.MarkAnalysisStaleLocked();
            }

            this.AppendLog(AppText.T("web.metadata.configApplied"));
            this.NotifyRecordsChanged();
            return true;
        }

        /// <summary>
        /// Esegue scan input
        /// </summary>
        public void Scan()
        {
            if (this.BusyState)
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
            if (this.BusyState)
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
            if (this.BusyState)
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
            if (this.BusyState)
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

            if (this.BusyState)
                return;

            lock (this.StateLock)
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
                new MetadataContainerReader(AppSettingsService.Instance.Settings.Tools.MkvMergePath, mkvExtractPath).PopulateContainerInfo(record);
                this.NotifyRecordsChanged();
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
            if (this.BusyState)
                return;

            Thread thread = new Thread(() => this.ApplyManualWorker(index, changes));
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Indica se l'applicazione richiede una conferma esplicita e ne descrive la portata
        /// </summary>
        /// <param name="selectedIndex">Indice record selezionato, oppure -1 per tutti i record</param>
        /// <param name="fileCount">Numero di file che verranno scritti</param>
        /// <param name="trackRemovalCount">Numero di tracce che verranno eliminate</param>
        /// <param name="inPlace">True se i file sorgente vengono sovrascritti</param>
        /// <returns>True se serve conferma</returns>
        public bool NeedsApplyConfirmation(int selectedIndex, out int fileCount, out int trackRemovalCount, out bool inPlace)
        {
            MkvMetadataRecord record;

            fileCount = 0;
            trackRemovalCount = 0;
            inPlace = this._options.Metadata.OutputPolicy == MkvMetadataOutputPolicy.Overwrite;

            lock (this.StateLock)
            {
                for (int i = 0; i < this._records.Count; i++)
                {
                    if (selectedIndex >= 0 && i != selectedIndex)
                        continue;

                    record = this._records[i];
                    if (record.AnalysisStatus != MkvMetadataAnalysisStatus.Analyzed || record.ExecutionMode == MkvMetadataExecutionMode.NoOp)
                        continue;

                    fileCount++;
                    for (int j = 0; record.Changes != null && j < record.Changes.Count; j++)
                    {
                        if (record.Changes[j].OperationType == MkvMetadataOperationType.RemoveTrack)
                            trackRemovalCount++;
                    }
                }
            }

            // La sovrascrittura non ha ritorno, e una rimozione di traccia non ce l'ha nemmeno
            // scrivendo su un percorso di output: la traccia nel file prodotto non c'è più
            return fileCount > 0 && (inPlace || trackRemovalCount > 0) && !this._options.Metadata.DryRun;
        }

        /// <summary>
        /// Pulisce stato corrente
        /// </summary>
        public void Clear()
        {
            lock (this.StateLock)
            {
                this._records.Clear();
                this.SelectedIndexState = -1;
            }

            this.AppendLog(AppText.T("web.metadata.stateCleared"));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Richiede stop cooperativo
        /// </summary>
        public void Stop()
        {
            this.RequestStop(AppText.T("web.metadata.stopRequested"));
        }

        /// <summary>
        /// Restituisce copia record
        /// </summary>
        /// <returns>Record correnti</returns>
        public List<MkvMetadataRecord> GetRecords()
        {
            lock (this.StateLock)
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
                records = scanner.Scan(
                    this._options.Metadata.SourcePath,
                    this._options.Metadata.Recursive,
                    (file, count) => this.ReportScanProgress(file, count),
                    () => this.StopRequested);

                lock (this.StateLock)
                {
                    this._records = records;
                    this.SelectedIndexState = records.Count > 0 ? 0 : -1;
                }

                this.AppendLog(AppText.F("web.metadata.scanCompleted", records.Count));
                this.NotifyRecordsChanged();
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
            MetadataContainerReader containerReader;
            MkvMetadataRecord record;
            List<MkvMetadataRecord> targets;
            string mkvExtractPath;
            int analyzedCount = 0;
            int errorCount = 0;

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
                containerReader = new MetadataContainerReader(AppSettingsService.Instance.Settings.Tools.MkvMergePath, mkvExtractPath);
                targets = this.GetRecords();

                for (int i = 0; i < targets.Count; i++)
                {
                    if (this.StopRequested)
                        break;

                    record = targets[i];
                    this.ReportProgress(i, targets.Count, Path.GetFileName(record.InputFile));

                    // Il try sta dentro il ciclo: un file illeggibile va in errore da solo invece
                    // di abortire l'analisi e marcare in errore anche i file sani
                    try
                    {
                        record.Status = MkvMetadataStatus.Analyzing;
                        tagReader.PopulateExistingTags(record);
                        containerReader.PopulateContainerInfo(record);
                        evaluator.AnalyzeRecord(record, preset, this._options.Metadata.OutputPolicy);
                        record.Status = MkvMetadataStatus.Analyzed;
                        record.ErrorMessage = "";
                        analyzedCount++;
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        record.AnalysisStatus = MkvMetadataAnalysisStatus.Error;
                        record.Status = MkvMetadataStatus.Error;
                        record.ErrorMessage = ex.Message;
                        this.AppendLog(AppText.F("web.metadata.recordAnalysisError", Path.GetFileName(record.InputFile), ex.Message));
                    }

                    this.NotifyRecordsChanged();
                }

                this.AppendLog(AppText.F("web.metadata.analysisCompletedCounts", analyzedCount, errorCount));
                this.OnAnalysisCompleted?.Invoke(analyzedCount, errorCount);
                this.NotifyRecordsChanged();
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.metadata.analysisError", ex.Message));
                this.OnOperationFailed?.Invoke(ex.Message);
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
            List<MkvMetadataRecord> targets;
            HashSet<string> skipped;
            string mkvMergePath;
            string mkvPropEditPath;
            string mkvExtractPath;
            int successCount = 0;
            int errorCount = 0;
            int skippedCount = 0;

            this.SetBusy(true, selectedIndex >= 0 ? AppText.T("web.metadata.progress.applySelected") : AppText.T("web.metadata.progress.apply"));
            try
            {
                skipped = this.MarkOutputConflicts(selectedIndex);

                mkvMergePath = AppSettingsService.Instance.Settings.Tools.MkvMergePath;
                mkvPropEditPath = AppSettingsService.Instance.Settings.Tools.MkvPropEditPath;
                mkvExtractPath = AppSettingsService.Instance.Settings.Tools.MkvExtractPath;
                executor = new MetadataExecutionService(mkvMergePath, mkvPropEditPath, mkvExtractPath);

                targets = this.GetRecords();
                for (int i = 0; i < targets.Count; i++)
                {
                    if (selectedIndex >= 0 && i != selectedIndex)
                        continue;

                    if (this.StopRequested)
                        break;

                    if (targets[i].AnalysisStatus != MkvMetadataAnalysisStatus.Analyzed)
                        continue;

                    if (skipped.Contains(targets[i].InputFile))
                    {
                        skippedCount++;
                        continue;
                    }

                    this.ReportProgress(i, targets.Count, Path.GetFileName(targets[i].InputFile));
                    this.UpdateRecordStatus(i, MkvMetadataStatus.Running, "");

                    // Il try sta dentro il ciclo: un file che esplode non deve fermare il lotto
                    try
                    {
                        result = executor.Execute(targets[i], this._options.Metadata);
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        this.UpdateRecordStatus(i, MkvMetadataStatus.Error, ex.Message);
                        this.AppendLog(AppText.F("web.metadata.recordApplyError", Path.GetFileName(targets[i].InputFile), ex.Message));
                        continue;
                    }

                    if (result.ExitCode == 0)
                    {
                        successCount++;
                        this.UpdateRecordStatus(i, result.DryRun ? MkvMetadataStatus.DryRun : MkvMetadataStatus.Completed, "");

                        // Un dry-run non ha scritto niente e lascia il piano spendibile, e un NoOp
                        // non ha toccato il file: consumare il piano vale solo per un'applicazione
                        // vera, perche' rieseguirla su un file gia' modificato risolverebbe i
                        // selector di traccia sul file nuovo, cancellando una traccia diversa
                        if (!result.DryRun && targets[i].ExecutionMode != MkvMetadataExecutionMode.NoOp)
                            this.MarkRecordApplied(i, executor);
                    }
                    else
                    {
                        errorCount++;
                        this.UpdateRecordStatus(i, MkvMetadataStatus.Error, result.ErrorMessage);
                        this.AppendLog(AppText.F("web.metadata.recordApplyError", Path.GetFileName(targets[i].InputFile), result.ErrorMessage));
                    }
                }

                this.AppendLog(AppText.F("web.metadata.applyCompletedCounts", successCount, errorCount, skippedCount));
                this.OnApplyCompleted?.Invoke(successCount, errorCount + skippedCount);
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.metadata.applyError", ex.Message));
                this.OnOperationFailed?.Invoke(ex.Message);
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

                    // I capitoli si riscrivono in blocco e non hanno un campo da validare:
                    // la lista che arriva dall'editor e' gia' quella da scrivere
                    if (change.OperationType == MkvMetadataOperationType.RenameChapters || change.OperationType == MkvMetadataOperationType.ClearChapters)
                        continue;

                    throw new InvalidOperationException(AppText.T("web.metadata.manualEdit.operationNotAllowed"));
                }

                lock (this.StateLock)
                {
                    if (selectedIndex < 0 || selectedIndex >= this._records.Count)
                        throw new InvalidOperationException(AppText.T("web.dashboard.noFileSelected"));

                    sourceRecord = this._records[selectedIndex];
                    sourceRecord.Status = MkvMetadataStatus.Running;
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

                this.NotifyRecordsChanged();

                runtimeOptions = new MkvMetadataOptions();
                runtimeOptions.SourcePath = this._options.Metadata.SourcePath;
                runtimeOptions.PresetPath = this._options.Metadata.PresetPath;
                runtimeOptions.OutputPolicy = this._options.Metadata.OutputPolicy;
                runtimeOptions.OutputDir = this._options.Metadata.OutputDir;
                runtimeOptions.Recursive = this._options.Metadata.Recursive;
                runtimeOptions.PreserveFolderStructure = this._options.Metadata.PreserveFolderStructure;
                runtimeOptions.OverwriteOutput = this._options.Metadata.OverwriteOutput;
                runtimeOptions.DryRun = false;

                outputFile = MetadataExecutionService.BuildOutputFile(manualRecord, runtimeOptions);
                if (runtimeOptions.OutputPolicy == MkvMetadataOutputPolicy.OutputPath && !runtimeOptions.OverwriteOutput && File.Exists(outputFile))
                    throw new InvalidOperationException(AppText.F("metadata.error.outputExists", outputFile));

                executor = new MetadataExecutionService(
                    AppSettingsService.Instance.Settings.Tools.MkvMergePath,
                    AppSettingsService.Instance.Settings.Tools.MkvPropEditPath,
                    AppSettingsService.Instance.Settings.Tools.MkvExtractPath);

                result = executor.Execute(manualRecord, runtimeOptions);
                if (result.ExitCode != 0)
                {
                    this.UpdateRecordStatus(selectedIndex, MkvMetadataStatus.Error, result.ErrorMessage);
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
                        refreshedRecords[0].Status = MkvMetadataStatus.Completed;
                        lock (this.StateLock)
                        {
                            this._records[selectedIndex] = refreshedRecords[0];
                        }

                        this.NotifyRecordsChanged();
                    }
                    else
                    {
                        this.UpdateRecordStatus(selectedIndex, MkvMetadataStatus.Completed, "");
                    }
                }
                else
                {
                    this.UpdateRecordStatus(selectedIndex, MkvMetadataStatus.Completed, "");
                }

                this.AppendLog(AppText.F("web.metadata.manualEdit.completed", result.OutputFile));
            }
            catch (Exception ex)
            {
                this.UpdateRecordStatus(selectedIndex, MkvMetadataStatus.Error, ex.Message);
                this.AppendLog(AppText.F("web.metadata.manualEdit.error", ex.Message));
            }
            finally
            {
                this.SetBusy(false, "");
            }
        }

        /// <summary>
        /// Marca un record come già applicato e ne rilegge lo stato quando il file è stato modificato in place
        /// </summary>
        /// <param name="index">Indice record</param>
        /// <param name="executor">Servizio di esecuzione usato per rileggere i tag</param>
        private void MarkRecordApplied(int index, MetadataExecutionService executor)
        {
            MkvMetadataRecord record;
            MetadataFileScanner scanner;
            List<MkvMetadataRecord> refreshed;
            string mediaInfoPath;

            lock (this.StateLock)
            {
                if (index < 0 || index >= this._records.Count)
                    return;

                record = this._records[index];
                record.AnalysisStatus = MkvMetadataAnalysisStatus.Applied;
            }

            if (this._options.Metadata.OutputPolicy != MkvMetadataOutputPolicy.Overwrite)
            {
                this.NotifyRecordsChanged();
                return;
            }

            // In sovrascrittura il file su disco non è più quello letto da MediaInfo: senza
            // rilettura il pannello continuerebbe a mostrare i valori di prima come se fossero attuali
            try
            {
                mediaInfoPath = AppSettingsService.Instance.Settings.Tools.MediaInfoPath;
                if (string.IsNullOrEmpty(mediaInfoPath))
                    mediaInfoPath = "mediainfo";

                scanner = new MetadataFileScanner(new MetadataMediaInfoReader(mediaInfoPath));
                refreshed = scanner.Scan(record.InputFile, false);
                if (refreshed.Count > 0)
                {
                    executor.PopulateExistingTags(refreshed[0]);
                    refreshed[0].Status = record.Status;
                    refreshed[0].AnalysisStatus = MkvMetadataAnalysisStatus.Applied;
                    refreshed[0].MatchCount = record.MatchCount;
                    refreshed[0].ChangeCount = record.ChangeCount;
                    refreshed[0].ExecutionMode = record.ExecutionMode;

                    lock (this.StateLock)
                    {
                        if (index < this._records.Count)
                            this._records[index] = refreshed[0];
                    }
                }
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.metadata.refreshError", ex.Message));
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Marca i record il cui output è in conflitto e ne restituisce i file da saltare
        /// </summary>
        /// <param name="selectedIndex">Indice record selezionato, oppure -1 per tutti i record</param>
        /// <returns>File di input da saltare</returns>
        private HashSet<string> MarkOutputConflicts(int selectedIndex)
        {
            HashSet<string> skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<MkvMetadataRecord> candidates = new List<MkvMetadataRecord>();
            List<MetadataOutputValidator.MetadataOutputConflict> conflicts;
            List<MkvMetadataRecord> all = this.GetRecords();
            string message;

            for (int i = 0; i < all.Count; i++)
            {
                if (selectedIndex >= 0 && i != selectedIndex)
                    continue;

                candidates.Add(all[i]);
            }

            conflicts = MetadataOutputValidator.Validate(candidates, this._options.Metadata);

            // Un output in conflitto salta quel file soltanto: prima il primo conflitto
            // abortiva l'intero lotto e nessun file veniva scritto
            for (int i = 0; i < conflicts.Count; i++)
            {
                message = MetadataOutputValidator.DescribeConflict(conflicts[i]);
                skipped.Add(conflicts[i].InputFile);
                this.AppendLog(AppText.F("web.metadata.outputConflict", Path.GetFileName(conflicts[i].InputFile), message));

                lock (this.StateLock)
                {
                    for (int j = 0; j < this._records.Count; j++)
                    {
                        if (!string.Equals(this._records[j].InputFile, conflicts[i].InputFile, StringComparison.OrdinalIgnoreCase))
                            continue;

                        this._records[j].Status = MkvMetadataStatus.Skipped;
                        this._records[j].ErrorMessage = message;
                    }
                }
            }

            if (conflicts.Count > 0)
                this.NotifyRecordsChanged();

            return skipped;
        }

        /// <summary>
        /// Aggiorna stato ed errore di un record
        /// </summary>
        /// <param name="index">Indice record</param>
        /// <param name="status">Stato operativo</param>
        /// <param name="errorMessage">Messaggio di errore</param>
        private void UpdateRecordStatus(int index, MkvMetadataStatus status, string errorMessage)
        {
            lock (this.StateLock)
            {
                if (index < 0 || index >= this._records.Count)
                    return;

                this._records[index].Status = status;
                this._records[index].ErrorMessage = errorMessage != null ? errorMessage : "";
            }

            this.NotifyRecordsChanged();
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
                    this._records[i].Status = MkvMetadataStatus.Stale;
                }
            }
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

        #endregion
    }
}
