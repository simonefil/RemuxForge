using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Tools;
using RemuxForge.Core.Splitting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Orchestratore WebUI per modalità split
    /// </summary>
    public class SplitOrchestrator : MediaOrchestratorBase, IMediaSourceResolver
    {
        #region Tipi annidati

        /// <summary>
        /// Riassunto dell'esito di un'operazione, trasportato dagli eventi di fine operazione
        /// </summary>
        public class OperationSummary
        {
            #region Costruttore

            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="succeeded">File conclusi con successo</param>
            /// <param name="failed">File falliti</param>
            /// <param name="skipped">File saltati, oppure con avvisi nel caso dell'analisi</param>
            /// <param name="stopped">True se l'operazione è stata interrotta</param>
            public OperationSummary(int succeeded, int failed, int skipped, bool stopped)
            {
                this.Succeeded = succeeded;
                this.Failed = failed;
                this.Skipped = skipped;
                this.Stopped = stopped;
            }

            #endregion

            #region Proprietà

            /// <summary>File conclusi con successo</summary>
            public int Succeeded { get; private set; }

            /// <summary>File falliti</summary>
            public int Failed { get; private set; }

            /// <summary>File saltati, oppure con avvisi nel caso dell'analisi</summary>
            public int Skipped { get; private set; }

            /// <summary>True se l'operazione è stata interrotta</summary>
            public bool Stopped { get; private set; }

            #endregion
        }

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Opzioni split correnti
        /// </summary>
        private Options _options;

        /// <summary>
        /// Record split correnti
        /// </summary>
        private List<MkvSplitRecord> _records;

        /// <summary>
        /// Segmenti costruiti nell'editor, per percorso del file: vivono nella sessione e un nuovo scan li azzera
        /// </summary>
        private Dictionary<string, List<MkvSplitOverrideSegment>> _overrides;

        #endregion

        #region Eventi

        /// <summary>Evento fine analisi, con il riassunto dell'esito</summary>
        public event Action<OperationSummary> OnAnalysisCompleted;

        /// <summary>Evento fine split, con il riassunto dell'esito</summary>
        public event Action<OperationSummary> OnSplitCompleted;

        /// <summary>Evento emesso quando un'operazione non può partire</summary>
        public event Action<string> OnOperationFailed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public SplitOrchestrator() : base(AppText.T("web.split.ready"), false)
        {
            this._options = new Options();
            this._options.Mode = Options.MODE_SPLIT;
            this._records = new List<MkvSplitRecord>();
            this._overrides = new Dictionary<string, List<MkvSplitOverrideSegment>>(StringComparer.OrdinalIgnoreCase);
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

            lock (this.StateLock)
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
            if (this.BusyState)
                return;

            Thread thread = new Thread(this.ScanWorker);
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Costruisce il piano dei record indicati
        /// </summary>
        /// <param name="indices">Indici da analizzare, null per tutti</param>
        public void Analyze(List<int> indices)
        {
            List<int> targets;

            if (this.BusyState)
            {
                this.RejectOperation(AppText.T("web.split.analyzeBusy"));
                return;
            }

            targets = this.ResolveTargets(indices);
            if (targets.Count == 0)
            {
                this.RejectOperation(AppText.T("web.split.noAnalyzeTargets"));
                return;
            }

            Thread thread = new Thread(() => this.AnalyzeWorker(targets));
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Esegue lo split dei record indicati
        /// </summary>
        /// <param name="indices">Indici da tagliare, null per tutti</param>
        public void Split(List<int> indices)
        {
            List<int> targets;

            if (this.BusyState)
            {
                this.RejectOperation(AppText.T("web.split.splitBusy"));
                return;
            }

            targets = this.ResolveTargets(indices);
            if (targets.Count == 0)
            {
                this.RejectOperation(AppText.T("web.split.noSplitTargets"));
                return;
            }

            Thread thread = new Thread(() => this.SplitWorker(targets));
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Esegue split di tutti i record
        /// </summary>
        public void SplitAll()
        {
            this.Split(null);
        }

        /// <summary>
        /// Richiede stop cooperativo
        /// </summary>
        public void Stop()
        {
            this.RequestStop(AppText.T("web.split.stopRequested"));
        }

        /// <summary>
        /// Esclude o reinclude i record indicati
        /// </summary>
        /// <param name="indices">Indici da aggiornare</param>
        /// <param name="skipped">True per escludere</param>
        public void SetSkipped(List<int> indices, bool skipped)
        {
            lock (this.StateLock)
            {
                foreach (int index in indices)
                {
                    if (index < 0 || index >= this._records.Count)
                        continue;

                    this._records[index].Skipped = skipped;
                    this._records[index].Status = skipped ? MkvSplitStatus.Skipped : MkvSplitStatus.Pending;
                }
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Restituisce copia record
        /// </summary>
        /// <summary>
        /// Restituisce i segmenti dell'editor per un record, null quando comanda la configurazione globale
        /// </summary>
        /// <param name="index">Indice del record</param>
        /// <returns>Segmenti dell'editor oppure null</returns>
        public List<MkvSplitOverrideSegment> GetOverride(int index)
        {
            MkvSplitRecord record = this.GetRecordAt(index);
            List<MkvSplitOverrideSegment> stored;

            if (record == null) { return null; }
            lock (this.StateLock)
            {
                return this._overrides.TryGetValue(record.InputFile, out stored) ? stored : null;
            }
        }

        /// <summary>
        /// Sostituisce i segmenti di un record con quelli costruiti nell'editor e ne ricostruisce il piano
        /// </summary>
        /// <param name="index">Indice del record</param>
        /// <param name="segments">Segmenti dell'editor</param>
        public void SetOverride(int index, List<MkvSplitOverrideSegment> segments)
        {
            MkvSplitRecord record = this.GetRecordAt(index);

            if (record == null || segments == null) { return; }
            lock (this.StateLock)
            {
                this._overrides[record.InputFile] = segments;
            }
            this.RebuildPlan(index, segments);
        }

        /// <summary>
        /// Riporta un record sotto la configurazione globale, scartando i segmenti dell'editor
        /// </summary>
        /// <param name="index">Indice del record</param>
        public void ClearOverride(int index)
        {
            MkvSplitRecord record = this.GetRecordAt(index);

            if (record == null) { return; }
            lock (this.StateLock)
            {
                if (!this._overrides.Remove(record.InputFile)) { return; }
            }
            this.RebuildPlan(index, null);
        }

        /// <summary>
        /// Numero di file che hanno segmenti costruiti nell'editor
        /// </summary>
        /// <returns>Conteggio degli override attivi</returns>
        public int CountOverrides()
        {
            lock (this.StateLock)
            {
                return this._overrides.Count;
            }
        }

        public List<MkvSplitRecord> GetRecords()
        {
            lock (this.StateLock)
            {
                return new List<MkvSplitRecord>(this._records);
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Ricostruisce il piano di un record dopo un cambio di override
        /// </summary>
        /// <param name="index">Indice del record</param>
        /// <param name="segments">Segmenti dell'editor, null per tornare alla configurazione globale</param>
        private void RebuildPlan(int index, List<MkvSplitOverrideSegment> segments)
        {
            MkvSplitRecord record = this.GetRecordAt(index);
            MkvSplitPlanner planner = new MkvSplitPlanner();
            MkvSplitPlan plan;

            if (record == null) { return; }
            try
            {
                plan = planner.BuildPlan(CloneSplitOptionsFor(this._options, record.InputFile), record.InputFile, null, segments);
            }
            catch (Exception ex)
            {
                this.SetRecordStatus(index, MkvSplitStatus.Error, ex.Message);
                return;
            }

            lock (this.StateLock)
            {
                record.Plan = plan;
                record.Segments = plan.Segments;
                record.IsOverride = segments != null;
                record.Status = plan.IsValid ? MkvSplitStatus.Planned : (plan.Mode == MkvSplitMode.Manual ? MkvSplitStatus.Undefined : MkvSplitStatus.PlanInvalid);
                record.ErrorMessage = plan.IsValid ? "" : plan.ErrorMessage;
            }
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Worker scan
        /// </summary>
        private void ScanWorker()
        {
            this.SetBusy(true, AppText.T("web.progress.scanSplit"));
            try
            {
                List<MkvSplitRecord> scanned = this.ScanSource();
                lock (this.StateLock)
                {
                    this._records = scanned;
                    this.SelectedIndexState = scanned.Count > 0 ? 0 : -1;
                    this._overrides.Clear();
                }
                this.AppendLog(AppText.F("web.split.scanCompleted", scanned.Count));
                this.NotifyRecordsChanged();
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.split.scanError", ex.Message));
            }
            this.SetBusy(false, "");
        }

        /// <summary>
        /// Worker di analisi: costruisce il piano di ogni record indicato
        /// </summary>
        /// <param name="targets">Indici dei record da analizzare</param>
        private void AnalyzeWorker(List<int> targets)
        {
            MkvSplitPlanner planner = new MkvSplitPlanner();
            MkvSplitRecord record;
            MkvSplitPlan plan;
            Options options;
            OperationSummary summary = null;
            int plannedCount = 0;
            int invalidCount = 0;
            int warningFiles = 0;

            this.SetBusy(true, AppText.T("web.progress.analyzeSplit"));
            this.StopRequested = false;
            ProcessRunner.SetStopRequestedCallback(this.IsStopRequested);
            ConsoleHelper.SetLogCallback((section, _, text) =>
            {
                string prefix = ConsoleHelper.FormatSectionPrefix(section);
                this.AppendLog(!string.IsNullOrEmpty(prefix) ? prefix + text : text);
            });

            try
            {
                MkvSplitExternalTools.Instance.ResolveBinaries();
                options = this._options;
                for (int i = 0; i < targets.Count; i++)
                {
                    if (this.StopRequested)
                    {
                        this.SetRecordStatus(targets[i], MkvSplitStatus.Stopped, AppText.T("web.split.stopRequested"));
                        break;
                    }

                    // Il try sta dentro il ciclo: un file che esplode non deve abortire il
                    // batch ne' marcare Error gli altri record, come gia' fa la CLI
                    try
                    {
                        record = this.GetRecordAt(targets[i]);
                        if (record == null || record.Skipped)
                            continue;

                        this.ReportProgress(i, targets.Count, Path.GetFileName(record.InputFile));
                        this.SetRecordStatus(targets[i], MkvSplitStatus.Analyzing, "");

                        plan = planner.BuildPlan(CloneSplitOptionsFor(options, record.InputFile), record.InputFile, status => this.ReportPhase(status), this.GetOverride(targets[i]));
                        planner.PrintPlan(plan);

                        lock (this.StateLock)
                        {
                            record.Plan = plan;
                            record.Segments = plan.Segments;
                            record.Status = plan.IsValid ? MkvSplitStatus.Planned : (plan.Mode == MkvSplitMode.Manual ? MkvSplitStatus.Undefined : MkvSplitStatus.PlanInvalid);
                            record.ErrorMessage = plan.IsValid ? "" : plan.ErrorMessage;
                        }

                        if (plan.IsValid) { plannedCount++; } else { invalidCount++; }
                        if (plan.Warnings.Count > 0) { warningFiles++; }
                        this.NotifyRecordsChanged();
                    }
                    catch (Exception ex)
                    {
                        invalidCount++;
                        this.SetRecordStatus(targets[i], MkvSplitStatus.Error, ex.Message);
                        this.AppendLog(AppText.F("web.split.scanError", ex.Message));
                    }
                }

                summary = new OperationSummary(plannedCount, invalidCount, warningFiles, this.StopRequested);
                this.AppendLog(AppText.F("web.split.analyzeCompleted", plannedCount, invalidCount));
            }
            catch (Exception ex)
            {
                summary = new OperationSummary(plannedCount, invalidCount + 1, warningFiles, false);
                this.AppendLog(AppText.F("web.split.scanError", ex.Message));
            }
            finally
            {
                ConsoleHelper.ClearLogCallback();
                this.SetBusy(false, "");
                this.NotifyRecordsChanged();
                this.OnAnalysisCompleted?.Invoke(summary);
            }
        }

        /// <summary>
        /// Worker di split: esegue il piano dei record indicati
        /// </summary>
        /// <param name="targets">Indici dei record da tagliare</param>
        private void SplitWorker(List<int> targets)
        {
            MkvSplitPipeline pipeline = new MkvSplitPipeline();
            MkvSplitPlanner planner = new MkvSplitPlanner();
            MkvSplitRecord record;
            MkvSplitPlan plan;
            Options options;
            OperationSummary summary = null;
            int successCount = 0;
            int errorCount = 0;
            int skippedCount = 0;
            int exitCode;

            this.SetBusy(true, AppText.T("web.progress.split"));
            this.StopRequested = false;
            ProcessRunner.SetStopRequestedCallback(this.IsStopRequested);
            ConsoleHelper.SetLogCallback((section, _, text) =>
            {
                string prefix = ConsoleHelper.FormatSectionPrefix(section);
                this.AppendLog(!string.IsNullOrEmpty(prefix) ? prefix + text : text);
            });

            try
            {
                MkvSplitExternalTools.Instance.ResolveBinaries();
                options = this._options;
                for (int i = 0; i < targets.Count; i++)
                {
                    if (this.StopRequested)
                    {
                        this.SetRecordStatus(targets[i], MkvSplitStatus.Stopped, AppText.T("web.split.stopRequested"));
                        break;
                    }

                    // Il try sta dentro il ciclo: un file che esplode non deve abortire il
                    // batch ne' marcare Error gli altri record, come gia' fa la CLI
                    try
                    {
                        record = this.GetRecordAt(targets[i]);
                        if (record == null || record.Skipped)
                        {
                            skippedCount++;
                            continue;
                        }

                        this.ReportProgress(i, targets.Count, Path.GetFileName(record.InputFile));

                        // Un file mai analizzato riceve il suo piano adesso: lo split non ricostruisce mai i segmenti da sé
                        plan = record.Plan;
                        if (plan == null)
                        {
                            this.SetRecordStatus(targets[i], MkvSplitStatus.Analyzing, "");
                            plan = planner.BuildPlan(CloneSplitOptionsFor(options, record.InputFile), record.InputFile, status => this.ReportPhase(status), this.GetOverride(targets[i]));
                            lock (this.StateLock)
                            {
                                record.Plan = plan;
                                record.Segments = plan.Segments;
                                record.IsOverride = plan.IsOverride;
                            }
                        }

                        if (!plan.IsValid)
                        {
                            skippedCount++;
                            this.SetRecordStatus(targets[i], plan.Mode == MkvSplitMode.Manual ? MkvSplitStatus.Undefined : MkvSplitStatus.PlanInvalid, plan.ErrorMessage);
                            this.AppendLog(AppText.F("split.plan.invalid", plan.ErrorMessage));
                            continue;
                        }

                        this.SetRecordStatus(targets[i], MkvSplitStatus.Running, "");
                        exitCode = pipeline.ExecutePlan(plan, CloneSplitOptionsFor(options, record.InputFile));
                        if (exitCode == 0)
                        {
                            successCount++;
                            this.UpdateRecord(targets[i], MkvSplitStatus.Done, true, "", plan.Segments);
                        }
                        else
                        {
                            errorCount++;
                            this.UpdateRecord(targets[i], MkvSplitStatus.Error, false, AppText.T("split.error.generic"), plan.Segments);
                        }
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        this.UpdateRecord(targets[i], MkvSplitStatus.Error, false, ex.Message, null);
                        this.AppendLog(AppText.F("cli.splitError", ex.Message));
                    }
                }

                summary = new OperationSummary(successCount, errorCount, skippedCount, this.StopRequested);
                if (errorCount == 0 && !this.StopRequested)
                {
                    this.AppendLog(AppText.F("web.split.completed", successCount));
                }
                else if (this.StopRequested)
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
                summary = new OperationSummary(successCount, errorCount + 1, skippedCount, false);
                this.MarkRecords(MkvSplitStatus.Error, false, ex.Message);
                this.AppendLog(AppText.F("cli.splitError", ex.Message));
            }
            finally
            {
                ConsoleHelper.ClearLogCallback();
                this.SetBusy(false, "");
                this.NotifyRecordsChanged();
                this.OnSplitCompleted?.Invoke(summary);
            }
        }

        /// <summary>
        /// Risolve gli indici bersaglio di un'operazione
        /// </summary>
        /// <param name="indices">Indici richiesti, null per tutti</param>
        /// <returns>Indici validi in ordine crescente</returns>
        private List<int> ResolveTargets(List<int> indices)
        {
            List<int> result = new List<int>();

            lock (this.StateLock)
            {
                if (indices == null)
                {
                    for (int i = 0; i < this._records.Count; i++)
                    {
                        result.Add(i);
                    }
                    return result;
                }

                foreach (int index in indices)
                {
                    if (index >= 0 && index < this._records.Count && !result.Contains(index))
                    {
                        result.Add(index);
                    }
                }
            }

            result.Sort();
            return result;
        }

        /// <summary>
        /// Registra e notifica un'operazione che non può partire
        /// </summary>
        /// <param name="message">Motivo del rifiuto</param>
        private void RejectOperation(string message)
        {
            this.AppendLog(message);
            this.OnOperationFailed?.Invoke(message);
        }

        /// <summary>
        /// Indica se lo scope split espone il lato richiesto
        /// </summary>
        /// <param name="side">Nome del lato</param>
        /// <returns>True solo per input: lo split lavora su un file solo</returns>
        public bool SupportsSide(string side)
        {
            return string.Equals(side, "input", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Risolve il file di ingresso di un record
        /// </summary>
        /// <param name="recordIndex">Indice del record</param>
        /// <param name="side">Nome del lato</param>
        /// <returns>Sorgente multimediale, null se il record non esiste</returns>
        public MediaSource ResolveMediaSource(int recordIndex, string side)
        {
            MkvSplitRecord record = this.GetRecordAt(recordIndex);
            if (record == null || !this.SupportsSide(side))
                return null;
            List<TrackInfo> tracks = record.SourceInfo != null ? record.SourceInfo.Tracks.FindAll(track => string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase)) : new List<TrackInfo>();
            return new MediaSource(record.InputFile, tracks);
        }

        /// <summary>
        /// Restituisce il record all'indice indicato
        /// </summary>
        /// <param name="index">Indice richiesto</param>
        /// <returns>Record oppure null</returns>
        private MkvSplitRecord GetRecordAt(int index)
        {
            lock (this.StateLock)
            {
                return index >= 0 && index < this._records.Count ? this._records[index] : null;
            }
        }

        /// <summary>
        /// Aggiorna stato ed errore di un record senza toccarne i segmenti
        /// </summary>
        /// <param name="index">Indice del record</param>
        /// <param name="status">Nuovo stato</param>
        /// <param name="errorMessage">Messaggio di errore o stringa vuota</param>
        private void SetRecordStatus(int index, MkvSplitStatus status, string errorMessage)
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
        /// Clona le opzioni split per un singolo file
        /// </summary>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="inputFile">File da elaborare</param>
        /// <returns>Opzioni split del file</returns>
        private static MkvSplitOptions CloneSplitOptionsFor(Options options, string inputFile)
        {
            MkvSplitOptions result = new MkvSplitOptions();
            result.SourcePath = options.Split.SourcePath;
            result.OutputDir = options.Split.OutputDir;
            result.Pattern = options.Split.Pattern;
            result.Ranges = options.Split.Ranges;
            result.SplitAt = options.Split.SplitAt;
            result.TrimStart = options.Split.TrimStart;
            result.TrimEnd = options.Split.TrimEnd;
            result.ChaptersEach = options.Split.ChaptersEach;
            result.ChaptersPerEpisode = options.Split.ChaptersPerEpisode;
            result.Manual = options.Split.Manual;
            result.OutputTemplate = options.Split.OutputTemplate;
            result.StartNumber = options.Split.StartNumber;
            result.Snap = options.Split.Snap;
            result.Force = options.Split.Force;
            result.DryRun = options.Split.DryRun;
            result.InputFile = inputFile;
            return result;
        }

        /// <summary>
        /// Scansiona source file/cartella
        /// </summary>
        private List<MkvSplitRecord> ScanSource()
        {
            List<MkvSplitRecord> result = new List<MkvSplitRecord>();
            ToolPathResolverService resolver = new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder);
            string mkvMergePath = resolver.ResolveMkvMergePath(false);
            string source = this._options.Split.SourcePath;
            SearchOption searchOption = this._options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            if (string.IsNullOrEmpty(source))
            {
                throw new InvalidOperationException(AppText.T("web.split.configureSource"));
            }

            if (File.Exists(source))
            {
                result.Add(this.CreateRecord(Path.GetFullPath(source), mkvMergePath));
            }
            else if (Directory.Exists(source))
            {
                for (int i = 0; i < this._options.FileExtensions.Count; i++)
                {
                    foreach (string file in Directory.GetFiles(source, "*." + this._options.FileExtensions[i].TrimStart('.'), searchOption))
                    {
                        result.Add(this.CreateRecord(Path.GetFullPath(file), mkvMergePath));
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
        /// <param name="file">File sorgente</param>
        /// <param name="mkvMergePath">Percorso di mkvmerge, vuoto se non risolto</param>
        /// <returns>Record con dimensione e info di contenitore già lette</returns>
        private MkvSplitRecord CreateRecord(string file, string mkvMergePath)
        {
            MkvSplitRecord record = new MkvSplitRecord();
            record.InputFile = file;
            record.Status = MkvSplitStatus.Pending;
            record.SourceSize = new FileInfo(file).Length;

            // Contenitore e tracce si leggono già allo scan: il dettaglio non resta vuoto in attesa dell'analisi
            try
            {
                record.SourceInfo = !string.IsNullOrEmpty(mkvMergePath) ? new MkvToolsService(mkvMergePath).GetFileInfo(file) : null;
            }
            catch (Exception)
            {
                record.SourceInfo = null;
            }

            return record;
        }

        /// <summary>
        /// Marca tutti i record
        /// </summary>
        private void MarkRecords(MkvSplitStatus status, bool success, string errorMessage)
        {
            lock (this.StateLock)
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
        private void UpdateRecord(int index, MkvSplitStatus status, bool success, string errorMessage, List<MkvSplitSegment> segments)
        {
            lock (this.StateLock)
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

            this.NotifyRecordsChanged();
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
