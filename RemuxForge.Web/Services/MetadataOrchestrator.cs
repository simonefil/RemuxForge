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
    /// Orchestratore WebUI per modalita' Metadata
    /// </summary>
    public class MetadataOrchestrator
    {
        #region Variabili di classe

        private Options _options;
        private List<MkvMetadataRecord> _records;
        private object _lock;
        private ProcessingProgressState _progress;
        private volatile bool _isBusy;
        private volatile bool _stopRequested;
        private string _logText;
        private int _selectedIndex;
        private const int LOG_MAX_LENGTH = 500000;

        #endregion

        #region Eventi

        /// <summary>Evento log</summary>
        public event Action<string> OnLog;

        /// <summary>Evento record aggiornati</summary>
        public event Action OnRecordsChanged;

        /// <summary>Evento progress aggiornato</summary>
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

            options.Metadata.SourcePath = options.Metadata.SourcePath.Length > 0 ? options.Metadata.SourcePath : options.SourceFolder;
            options.Metadata.OutputDir = options.Metadata.OutputDir.Length > 0 ? options.Metadata.OutputDir : options.DestinationFolder;
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
            if (this._isBusy) { return; }
            Thread thread = new Thread(this.ScanWorker);
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Esegue analisi Metadata senza scrivere file
        /// </summary>
        public void AnalyzeAll()
        {
            if (this._isBusy) { return; }
            Thread thread = new Thread(this.AnalyzeAllWorker);
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Applica tutti i record analizzati
        /// </summary>
        public void ApplyAll()
        {
            if (this._isBusy) { return; }
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
            if (this._isBusy) { return; }
            Thread thread = new Thread(() => this.ApplyWorker(index));
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

        private void ScanWorker()
        {
            this.SetBusy(true, AppText.T("web.metadata.progress.scan"));
            try
            {
                string mediaInfoPath = AppSettingsService.Instance.Settings.Tools.MediaInfoPath;
                if (mediaInfoPath == null || mediaInfoPath.Length == 0)
                {
                    mediaInfoPath = "mediainfo";
                }

                MetadataMediaInfoReader reader = new MetadataMediaInfoReader(mediaInfoPath);
                MetadataFileScanner scanner = new MetadataFileScanner(reader);
                List<MkvMetadataRecord> records = scanner.Scan(this._options.Metadata.SourcePath, this._options.Metadata.Recursive);

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
                if (this._options.Metadata.PresetPath.Length > 0)
                {
                    MetadataPresetService presetService = new MetadataPresetService(AppSettingsService.Instance.ConfigFolder);
                    preset = presetService.Load(this._options.Metadata.PresetPath);
                    validation = MetadataPresetService.Validate(preset);
                    if (!validation.IsValid)
                    {
                        throw new InvalidOperationException(validation.ErrorMessage);
                    }
                }

                mkvExtractPath = AppSettingsService.Instance.Settings.Tools.MkvExtractPath;
                tagReader = new MetadataExecutionService("", "", mkvExtractPath);

                lock (this._lock)
                {
                    for (int i = 0; i < this._records.Count; i++)
                    {
                        if (this._stopRequested)
                        {
                            break;
                        }

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

        private void ApplyWorker(int selectedIndex)
        {
            MetadataExecutionService executor;
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
                    {
                        continue;
                    }

                    if (this._stopRequested)
                    {
                        break;
                    }

                    if (this._records[i].AnalysisStatus != MkvMetadataAnalysisStatus.Analyzed)
                    {
                        continue;
                    }

                    this.UpdateRecordStatus(i, AppText.T("web.metadata.status.running"), "");
                    MkvMetadataExecutionResult result = executor.Execute(this._records[i], this._options.Metadata);
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

        private void ValidateOutputTargets(int selectedIndex)
        {
            Dictionary<string, string> targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (this._options.Metadata.OutputPolicy != MkvMetadataOutputPolicy.OutputPath)
            {
                return;
            }

            for (int i = 0; i < this._records.Count; i++)
            {
                if (selectedIndex >= 0 && i != selectedIndex)
                {
                    continue;
                }

                MkvMetadataRecord record = this._records[i];
                if (record.AnalysisStatus != MkvMetadataAnalysisStatus.Analyzed || record.ExecutionMode == MkvMetadataExecutionMode.NoOp)
                {
                    continue;
                }

                string outputFile = MetadataExecutionService.BuildOutputFile(record, this._options.Metadata);
                if (targets.ContainsKey(outputFile))
                {
                    throw new InvalidOperationException(AppText.F("metadata.error.outputCollision", outputFile));
                }

                if (File.Exists(outputFile))
                {
                    throw new InvalidOperationException(AppText.F("metadata.error.outputExists", outputFile));
                }

                targets[outputFile] = record.InputFile;
            }
        }

        private void UpdateRecordStatus(int index, string status, string errorMessage)
        {
            lock (this._lock)
            {
                if (index < 0 || index >= this._records.Count)
                {
                    return;
                }

                this._records[index].Status = status;
                this._records[index].ErrorMessage = errorMessage != null ? errorMessage : "";
            }

            this.OnRecordsChanged?.Invoke();
        }

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

        private void AppendLog(string message)
        {
            lock (this._lock)
            {
                if (this._logText.Length > 0)
                {
                    this._logText += Environment.NewLine;
                }
                this._logText += "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
                if (this._logText.Length > LOG_MAX_LENGTH)
                {
                    this._logText = this._logText.Substring(this._logText.Length - LOG_MAX_LENGTH);
                }
            }

            this.OnLog?.Invoke(message);
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Opzioni correnti
        /// </summary>
        public Options CurrentOptions
        {
            get { return this._options; }
        }

        /// <summary>
        /// Stato busy
        /// </summary>
        public bool IsBusy
        {
            get { return this._isBusy; }
        }

        /// <summary>
        /// Log accumulato
        /// </summary>
        public string LogText
        {
            get { return this._logText; }
        }

        /// <summary>
        /// Stato progresso
        /// </summary>
        public ProcessingProgressState Progress
        {
            get { return this._progress; }
        }

        /// <summary>
        /// Indice selezionato
        /// </summary>
        public int SelectedIndex
        {
            get { return this._selectedIndex; }
            set { this._selectedIndex = value; }
        }

        #endregion
    }
}
