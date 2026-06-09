using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Orchestratore WebUI per modalita' split
    /// </summary>
    public class SplitOrchestrator
    {
        #region Variabili di classe

        private Options _options;
        private List<MkvSplitRecord> _records;
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
        public SplitOrchestrator()
        {
            this._options = new Options();
            this._options.Mode = Options.MODE_SPLIT;
            this._records = new List<MkvSplitRecord>();
            this._lock = new object();
            this._progress = new ProcessingProgressState();
            this._isBusy = false;
            this._stopRequested = false;
            this._logText = AppText.T("web.split.ready");
            this._selectedIndex = -1;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Applica opzioni split
        /// </summary>
        public bool ApplyOptions(Options options, out string errorMessage)
        {
            OptionsValidationResult validation;
            errorMessage = "";
            if (options == null)
            {
                errorMessage = AppText.T("validation.invalidConfig");
                return false;
            }

            options.Mode = Options.MODE_SPLIT;
            options.Split.SourcePath = options.SourceFolder;
            validation = OptionsValidator.Validate(options, false, false);
            if (!validation.IsValid)
            {
                errorMessage = string.Join("\n", validation.Errors);
                return false;
            }

            lock (this._lock)
            {
                this._options = options;
            }
            this.AppendLog(AppText.T("web.split.configApplied"));
            return true;
        }

        /// <summary>
        /// Esegue scan della sorgente
        /// </summary>
        public void Scan()
        {
            if (this._isBusy) { return; }
            Thread thread = new Thread(this.ScanWorker);
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Esegue split di tutti i record
        /// </summary>
        public void SplitAll()
        {
            if (this._isBusy) { return; }
            Thread thread = new Thread(this.SplitAllWorker);
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Richiede stop cooperativo
        /// </summary>
        public void Stop()
        {
            this._stopRequested = true;
            this.AppendLog(AppText.T("web.split.stopRequested"));
        }

        /// <summary>
        /// Scrive una riga nel log split
        /// </summary>
        /// <param name="message">Messaggio</param>
        public void Log(string message)
        {
            this.AppendLog(message);
        }

        /// <summary>
        /// Restituisce copia record
        /// </summary>
        public List<MkvSplitRecord> GetRecords()
        {
            lock (this._lock)
            {
                return new List<MkvSplitRecord>(this._records);
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Worker scan
        /// </summary>
        private void ScanWorker()
        {
            this.SetBusy(true, AppText.T("web.progress.scanSplit"));
            try
            {
                List<MkvSplitRecord> scanned = this.ScanSource();
                lock (this._lock)
                {
                    this._records = scanned;
                    this._selectedIndex = scanned.Count > 0 ? 0 : -1;
                }
                this.AppendLog(AppText.F("web.split.scanCompleted", scanned.Count));
                this.OnRecordsChanged?.Invoke();
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.split.scanError", ex.Message));
            }
            this.SetBusy(false, "");
        }

        /// <summary>
        /// Worker split all
        /// </summary>
        private void SplitAllWorker()
        {
            List<MkvSplitRecord> records;
            MkvSplitExecutionResult result;
            int successCount = 0;
            int errorCount = 0;
            this.SetBusy(true, AppText.T("web.progress.split"));
            this._stopRequested = false;
            ProcessRunner.SetStopRequestedCallback(this.IsStopRequested);
            ConsoleHelper.SetLogCallback((section, _, text) =>
            {
                string prefix = ConsoleHelper.FormatSectionPrefix(section);
                this.AppendLog(prefix.Length > 0 ? prefix + text : text);
            });

            try
            {
                MkvSplitPipeline pipeline = new MkvSplitPipeline();
                records = this.ScanSource();
                lock (this._lock)
                {
                    this._records = records;
                    this._selectedIndex = records.Count > 0 ? 0 : -1;
                }
                this.OnRecordsChanged?.Invoke();

                for (int i = 0; i < records.Count; i++)
                {
                    if (this._stopRequested)
                    {
                        this.UpdateRecord(i, AppText.T("web.split.status.stopped"), false, AppText.T("web.split.stopRequested"), null);
                        break;
                    }

                    this.UpdateRecord(i, AppText.T("web.split.status.running"), false, "", null);
                    result = pipeline.ExecuteFile(this._options, records[i].InputFile, records.Count > 1);
                    if (result.ExitCode == 0)
                    {
                        successCount++;
                        this.UpdateRecord(i, AppText.T("web.split.status.done"), true, "", result.Segments);
                    }
                    else
                    {
                        errorCount++;
                        this.UpdateRecord(i, AppText.T("web.split.status.error"), false, result.ErrorMessage, result.Segments);
                    }
                }

                if (errorCount == 0 && !this._stopRequested)
                {
                    this.AppendLog(AppText.F("web.split.completed", successCount));
                }
                else if (this._stopRequested)
                {
                    this.AppendLog(AppText.F("web.split.stoppedSummary", successCount, errorCount));
                }
                else
                {
                    this.AppendLog(AppText.F("web.split.errorSummary", successCount, errorCount));
                }
            }
            catch (Exception ex)
            {
                this.MarkRecords(AppText.T("web.split.status.error"), false, ex.Message);
                this.AppendLog(AppText.F("cli.splitError", ex.Message));
            }
            finally
            {
                ConsoleHelper.ClearLogCallback();
                this.SetBusy(false, "");
                this.OnRecordsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Scansiona source file/cartella
        /// </summary>
        private List<MkvSplitRecord> ScanSource()
        {
            List<MkvSplitRecord> result = new List<MkvSplitRecord>();
            string source = this._options.Split.SourcePath;
            SearchOption searchOption = this._options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            if (source.Length == 0)
            {
                throw new InvalidOperationException(AppText.T("web.split.configureSource"));
            }

            if (File.Exists(source))
            {
                result.Add(this.CreateRecord(Path.GetFullPath(source)));
            }
            else if (Directory.Exists(source))
            {
                for (int i = 0; i < this._options.FileExtensions.Count; i++)
                {
                    foreach (string file in Directory.GetFiles(source, "*." + this._options.FileExtensions[i].TrimStart('.'), searchOption))
                    {
                        result.Add(this.CreateRecord(Path.GetFullPath(file)));
                    }
                }
                result.Sort((a, b) => string.Compare(a.InputFile, b.InputFile, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                throw new FileNotFoundException(AppText.F("validation.splitSourceNotFound", source), source);
            }

            return result;
        }

        /// <summary>
        /// Crea record split
        /// </summary>
        private MkvSplitRecord CreateRecord(string file)
        {
            MkvSplitRecord record = new MkvSplitRecord();
            record.InputFile = file;
            record.Status = AppText.T("web.split.status.pending");
            return record;
        }

        /// <summary>
        /// Marca tutti i record
        /// </summary>
        private void MarkRecords(string status, bool success, string errorMessage)
        {
            lock (this._lock)
            {
                for (int i = 0; i < this._records.Count; i++)
                {
                    this._records[i].Status = status;
                    this._records[i].Success = success;
                    this._records[i].ErrorMessage = errorMessage;
                }
            }
        }

        /// <summary>
        /// Aggiorna un singolo record split
        /// </summary>
        private void UpdateRecord(int index, string status, bool success, string errorMessage, List<MkvSplitSegment> segments)
        {
            lock (this._lock)
            {
                if (index < 0 || index >= this._records.Count)
                {
                    return;
                }

                this._records[index].Status = status;
                this._records[index].Success = success;
                this._records[index].ErrorMessage = errorMessage != null ? errorMessage : "";
                if (segments != null)
                {
                    this._records[index].Segments = new List<MkvSplitSegment>(segments);
                }
            }

            this.OnRecordsChanged?.Invoke();
        }

        /// <summary>
        /// Aggiorna busy/progress
        /// </summary>
        private void SetBusy(bool busy, string operation)
        {
            this._isBusy = busy;
            this._progress.IsActive = busy;
            this._progress.Operation = operation;
            this._progress.CurrentStatus = busy ? operation : "";
            this._progress.CurrentIndeterminate = busy;
            this._progress.GlobalIndeterminate = busy;
            this.OnProgressChanged?.Invoke();
        }

        /// <summary>
        /// True se stop richiesto
        /// </summary>
        private bool IsStopRequested()
        {
            return this._stopRequested;
        }

        /// <summary>
        /// Appende log
        /// </summary>
        private void AppendLog(string text)
        {
            lock (this._lock)
            {
                this._logText += Environment.NewLine + text;
                if (this._logText.Length > LOG_MAX_LENGTH)
                {
                    this._logText = this._logText.Substring(this._logText.Length - LOG_MAX_LENGTH);
                }
            }
            this.OnLog?.Invoke(text);
        }

        #endregion

        #region Proprieta

        /// <summary>Opzioni correnti</summary>
        public Options CurrentOptions { get { return this._options; } }

        /// <summary>Log corrente</summary>
        public string LogText { get { return this._logText; } }

        /// <summary>Progress corrente</summary>
        public ProcessingProgressState Progress { get { return this._progress.Clone(); } }

        /// <summary>Indice selezionato</summary>
        public int SelectedIndex
        {
            get { return this._selectedIndex; }
            set { this._selectedIndex = value; }
        }

        /// <summary>True se busy</summary>
        public bool IsBusy { get { return this._isBusy; } }

        #endregion
    }
}
