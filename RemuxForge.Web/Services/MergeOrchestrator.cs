using RemuxForge.Core.Audio;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Pipeline;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Orchestratore singleton che gestisce il ProcessingPipeline per la WebUI
    /// </summary>
    public class MergeOrchestrator : MediaOrchestratorBase, IMediaSourceResolver
    {
        #region Variabili di classe

        /// <summary>
        /// Pipeline di elaborazione
        /// </summary>
        private ProcessingPipeline _pipeline;

        /// <summary>
        /// Lista dei record file correnti
        /// </summary>
        private List<FileProcessingRecord> _records;

        /// <summary>
        /// Numero di file sorgente trovati dall'ultimo scan, comprese le righe scartate perche' senza file lingua
        /// </summary>
        private int _scannedFileCount;

        /// <summary>
        /// Opzioni correnti
        /// </summary>
        private Options _options;

        /// <summary>
        /// Sorgente di cancellazione posseduta dall'operazione corrente
        /// </summary>
        private CancellationTokenSource _operationCancellation;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MergeOrchestrator() : base(AppText.T("web.merge.ready"), false)
        {
            this._pipeline = new ProcessingPipeline();
            this._records = new List<FileProcessingRecord>();
            this._scannedFileCount = 0;
            this._options = new Options();
            this._operationCancellation = null;

            // Abilita file log se configurato via env var
            string logFilePath = Environment.GetEnvironmentVariable("REMUXFORGE_LOG_FILE");
            if (!string.IsNullOrEmpty(logFilePath))
            {
                ConsoleHelper.EnableFileLog(logFilePath);
            }

            // Collega eventi pipeline
            this._pipeline.OnLogMessage += (section, _, text) =>
            {
                // Formatta testo con prefisso sezione
                string prefix = ConsoleHelper.FormatSectionPrefix(section);
                string formatted = !string.IsNullOrEmpty(prefix) ? prefix + text : text;
                this.AppendLog(formatted);
            };

            this._pipeline.OnFileUpdated += _ =>
            {
                this.NotifyRecordsChanged();
            };

            ConsoleHelper.SetProgressCallback((section, percent, status) =>
            {
                this.UpdateProgressFromPipelineStep(section, percent, status);
            });

            ProcessRunner.SetStopRequestedCallback(this.IsStopRequested);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Salva le opzioni correnti e le applica subito alla pipeline quando possibile
        /// </summary>
        /// <param name="opts">Opzioni di configurazione</param>
        /// <param name="errorMessage">Messaggio di errore, vuoto se applicate</param>
        /// <returns>True se le opzioni sono state applicate</returns>
        public bool ApplyOptions(Options opts, out string errorMessage)
        {
            bool result = false;
            bool scanInputsChanged;
            bool analysisOptionsChanged;
            bool renderOptionsChanged;
            int resetCount;
            int refreshedCount;
            Options previousOptions;
            errorMessage = "";
            if (opts == null)
            {
                errorMessage = AppText.T("validation.invalidConfig");
                return result;
            }

            if (this.BusyState)
            {
                errorMessage = AppText.T("web.merge.busyRetry");
                return result;
            }

            lock (this.StateLock)
            {
                previousOptions = this._options;
                scanInputsChanged = this.ScanInputsChanged(previousOptions, opts);
                analysisOptionsChanged = scanInputsChanged || this.AnalysisOptionsChanged(previousOptions, opts);
                renderOptionsChanged = !scanInputsChanged && !analysisOptionsChanged && this.RenderOptionsChanged(previousOptions, opts);
            }

            if (!string.IsNullOrEmpty(opts.SourceFolder))
            {
                result = this._pipeline.Initialize(opts);
                if (!result)
                {
                    errorMessage = AppText.T("web.merge.configNotApplicable");
                }
            }
            else
            {
                result = true;
            }

            if (result)
            {
                if (scanInputsChanged)
                {
                    lock (this.StateLock)
                    {
                        this._options = opts;
                        this._records.Clear();
                        this._scannedFileCount = 0;
                        this.SelectedIndexState = -1;
                    }
                    this.AppendLog(AppText.T("web.merge.configAppliedScanInvalidated"));
                }
                else if (analysisOptionsChanged)
                {
                    lock (this.StateLock)
                    {
                        this._options = opts;
                        resetCount = this.ResetAnalyzedRecordsAfterConfigChange();
                    }

                    this.AppendLog(resetCount > 0
                        ? AppText.F("web.merge.configAppliedAnalysisReset", resetCount)
                        : AppText.T("web.merge.configApplied"));
                }
                else if (renderOptionsChanged)
                {
                    lock (this.StateLock)
                    {
                        this._options = opts;
                    }
                    refreshedCount = this.RefreshAnalyzedRecordsAfterRenderChange();
                    this.AppendLog(refreshedCount > 0
                        ? AppText.F("web.merge.configAppliedPreviewRefreshed", refreshedCount)
                        : AppText.T("web.merge.configApplied"));
                }
                else
                {
                    lock (this.StateLock)
                    {
                        this._options = opts;
                    }

                    this.AppendLog(AppText.T("web.merge.configApplied"));
                }
                this.NotifyRecordsChanged();
            }

            return result;
        }

        /// <summary>
        /// Esegue scan delle cartelle in background (come flusso CLI: check opts + Initialize + ScanFiles)
        /// </summary>
        public void Scan()
        {
            // Verifica parametro obbligatorio: source folder
            if (string.IsNullOrEmpty(this._options.SourceFolder))
            {
                this.AppendLog(AppText.T("web.merge.configureSourceFirst"));
                return;
            }
            if (!this.TryBeginOperation())
                return;

            Thread thread = new Thread(() =>
            {
                try
                {
                    this.BeginProgress(AppText.T("web.progress.scan"), 0, true);

                    try
                    {
                        this.UpdateProgress("", 0, 0, 0, AppText.T("web.progress.initialization"), true, true);

                        // Inizializza pipeline con opzioni correnti (come flusso CLI)
                        if (!this._pipeline.Initialize(this._options))
                        {
                            this.AppendLog(AppText.T("web.merge.pipelineInitError"));
                            this.CompleteProgress(AppText.T("web.progress.initializationError"));
                            return;
                        }

                        this.UpdateProgress("", 0, 0, 30, AppText.T("web.progress.scanFiles"), true, true);

                        // Scan
                        List<FileProcessingRecord> scanned = this._pipeline.ScanFiles();

                        // Ordina per EpisodeId (come flusso CLI)
                        scanned.Sort((a, b) => string.Compare(a.EpisodeId, b.EpisodeId, StringComparison.OrdinalIgnoreCase));

                        // I file senza corrispondenza lingua non entrano nella lista esposta alla UI:
                        // resta il solo conteggio totale per il contatore degli abbinamenti
                        List<FileProcessingRecord> matched = new List<FileProcessingRecord>();
                        for (int i = 0; i < scanned.Count; i++)
                        {
                            if (string.IsNullOrEmpty(scanned[i].LangFilePath))
                                continue;

                            matched.Add(scanned[i]);
                        }

                        lock (this.StateLock)
                        {
                            this._records = matched;
                            this._scannedFileCount = scanned.Count;
                        }

                        // Conta file pronti e saltati
                        int pending = 0;
                        int skipped = 0;
                        for (int i = 0; i < scanned.Count; i++)
                        {
                            if (scanned[i].Status == FileStatus.Pending)
                                pending++;
                            else if (scanned[i].Status == FileStatus.Skipped)
                                skipped++;
                        }

                        this.NotifyRecordsChanged();
                        this.AppendLog(AppText.F("web.merge.scanCompleted", scanned.Count, pending, skipped));
                        this.CompleteProgress(AppText.T("web.progress.scanCompleted"));
                    }
                    catch (Exception ex)
                    {
                        this.AppendLog(AppText.F("web.merge.scanError", ex.Message));
                        this.CompleteProgress(AppText.T("web.progress.scanError"));
                    }

                }
                finally
                {
                    this.SetBusy(false);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Analizza un singolo episodio in background
        /// </summary>
        /// <param name="index">Indice del record nella lista</param>
        public void AnalyzeFile(int index)
        {
            FileProcessingRecord record = this.GetRecord(index);

            if (record == null || !this.TryBeginOperation())
            {
                return;
            }

            Thread thread = new Thread(() =>
            {
                try
                {
                    this.BeginProgress(AppText.T("web.progress.analyzeEpisode"), 1, false);

                    try
                    {
                        this.UpdateProgress(record.EpisodeId, 1, 0, 5, AppText.T("web.progress.analysis"), false, false);
                        this._pipeline.AnalyzeFile(record, this.GetOperationCancellationToken());
                        this.NotifyRecordsChanged();
                        this.UpdateProgress(record.EpisodeId, 1, 1, 100, AppText.T("web.progress.completed"), false, false);
                        this.CompleteProgress(AppText.T("web.progress.analysisCompleted"));
                    }
                    catch (OperationCanceledException)
                    {
                        this.AppendLog(AppText.T("web.merge.analysisSelectionStopped"));
                        this.CompleteProgress(AppText.T("web.progress.analysisStopped"));
                    }
                    catch (Exception ex)
                    {
                        this.AppendLog(AppText.F("web.merge.analysisError", ex.Message));
                        this.CompleteProgress(AppText.T("web.progress.analysisError"));
                    }
                }
                finally
                {
                    this.SetBusy(false);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Analizza una selezione di episodi in background
        /// </summary>
        /// <param name="indices">Indici dei record da analizzare</param>
        public void AnalyzeFiles(List<int> indices)
        {
            List<FileProcessingRecord> selected = this.GetRecordsByIndices(indices);

            if (selected.Count == 0 || !this.TryBeginOperation())
            {
                return;
            }

            Thread thread = new Thread(() =>
            {
                try
                {
                    bool stopped = false;
                    this.BeginProgress(AppText.T("web.progress.analyzeSelection"), selected.Count, false);

                    try
                    {
                        for (int i = 0; i < selected.Count; i++)
                        {
                            if (this.IsStopRequested())
                            {
                                stopped = true;
                                this.AppendLog(AppText.T("web.merge.analysisSelectionStopped"));
                                this.CompleteProgress(AppText.T("web.progress.analysisStopped"));
                                break;
                            }

                            this.UpdateProgress(selected[i].EpisodeId, i + 1, i, 5, AppText.T("web.progress.analysis"), false, false);
                            this._pipeline.AnalyzeFile(selected[i], this.GetOperationCancellationToken());
                            this.NotifyRecordsChanged();
                            this.UpdateProgress(selected[i].EpisodeId, i + 1, i + 1, 100, AppText.T("web.progress.completed"), false, false);
                        }

                        if (!stopped)
                        {
                            this.CompleteProgress(AppText.T("web.progress.analysisSelectionCompleted"));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        this.AppendLog(AppText.T("web.merge.analysisSelectionStopped"));
                        this.CompleteProgress(AppText.T("web.progress.analysisStopped"));
                    }
                    catch (Exception ex)
                    {
                        this.AppendLog(AppText.F("web.merge.analysisSelectionError", ex.Message));
                        this.CompleteProgress(AppText.T("web.progress.analysisSelectionError"));
                    }
                }
                finally
                {
                    this.SetBusy(false);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Analizza tutti gli episodi pendenti in background
        /// </summary>
        public void AnalyzeAll()
        {
            if (!this.TryBeginOperation())
            {
                return;
            }

            Thread thread = new Thread(() =>
            {
                try
                {
                    List<FileProcessingRecord> snapshot;
                    List<FileProcessingRecord> pending = new List<FileProcessingRecord>();
                    bool stopped = false;
                    lock (this.StateLock)
                    {
                        snapshot = new List<FileProcessingRecord>(this._records);
                    }

                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        // Includi anche file in errore per ritentare (come flusso CLI)
                        if (snapshot[i].Status == FileStatus.Pending || snapshot[i].Status == FileStatus.Error)
                        {
                            pending.Add(snapshot[i]);
                        }
                    }

                    this.BeginProgress(AppText.T("web.progress.analyzeBatch"), pending.Count, false);

                    try
                    {
                        for (int i = 0; i < pending.Count; i++)
                        {
                            if (this.IsStopRequested())
                            {
                                stopped = true;
                                this.AppendLog(AppText.T("web.merge.analysisBatchStopped"));
                                this.CompleteProgress(AppText.T("web.progress.analysisStopped"));
                                break;
                            }

                            this.UpdateProgress(pending[i].EpisodeId, i + 1, i, 5, AppText.T("web.progress.analysis"), false, false);
                            this._pipeline.AnalyzeFile(pending[i], this.GetOperationCancellationToken());
                            this.NotifyRecordsChanged();
                            this.UpdateProgress(pending[i].EpisodeId, i + 1, i + 1, 100, AppText.T("web.progress.completed"), false, false);
                        }

                        if (!stopped)
                        {
                            this.CompleteProgress(AppText.T("web.progress.analysisBatchCompleted"));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        this.AppendLog(AppText.T("web.merge.analysisBatchStopped"));
                        this.CompleteProgress(AppText.T("web.progress.analysisStopped"));
                    }
                    catch (Exception ex)
                    {
                        this.AppendLog(AppText.F("web.merge.analysisBatchError", ex.Message));
                        this.CompleteProgress(AppText.T("web.progress.analysisBatchError"));
                    }
                }
                finally
                {
                    this.SetBusy(false);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Esegue merge di un singolo episodio in background
        /// </summary>
        /// <param name="index">Indice del record nella lista</param>
        public void MergeFile(int index)
        {
            FileProcessingRecord record = this.GetRecord(index);

            if (record == null || !this.TryBeginOperation())
            {
                return;
            }

            Thread thread = new Thread(() =>
            {
                try
                {
                    this.BeginProgress(AppText.T("web.progress.mergeEpisode"), 1, false);

                    try
                    {
                        this.UpdateProgress(record.EpisodeId, 1, 0, 10, AppText.T("web.progress.merge"), false, false);
                        this._pipeline.MergeFile(record);
                        this.NotifyRecordsChanged();
                        this.UpdateProgress(record.EpisodeId, 1, 1, 100, AppText.T("web.progress.completed"), false, false);
                        this.CompleteProgress(AppText.T("web.progress.mergeCompleted"));
                    }
                    catch (Exception ex)
                    {
                        this.AppendLog(AppText.F("web.merge.mergeError", ex.Message));
                        this.CompleteProgress(AppText.T("web.progress.mergeError"));
                    }

                }
                finally
                {
                    this.SetBusy(false);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Esegue merge di una selezione di episodi in background
        /// </summary>
        /// <param name="indices">Indici dei record da processare</param>
        public void MergeFiles(List<int> indices)
        {
            List<FileProcessingRecord> selected = this.GetRecordsByIndices(indices);

            if (selected.Count == 0 || !this.TryBeginOperation())
            {
                return;
            }

            Thread thread = new Thread(() =>
            {
                try
                {
                    bool stopped = false;
                    this.BeginProgress(AppText.T("web.progress.mergeSelection"), selected.Count, false);

                    try
                    {
                        for (int i = 0; i < selected.Count; i++)
                        {
                            if (this.IsStopRequested())
                            {
                                stopped = true;
                                this.AppendLog(AppText.T("web.merge.mergeSelectionStopped"));
                                this.CompleteProgress(AppText.T("web.progress.mergeStopped"));
                                break;
                            }

                            this.UpdateProgress(selected[i].EpisodeId, i + 1, i, 10, AppText.T("web.progress.merge"), false, false);
                            this._pipeline.MergeFile(selected[i]);
                            this.NotifyRecordsChanged();
                            this.UpdateProgress(selected[i].EpisodeId, i + 1, i + 1, 100, AppText.T("web.progress.completed"), false, false);
                        }

                        if (!stopped)
                        {
                            this.AppendLog(AppText.T("web.merge.mergeSelectionCompleted"));
                            this.CompleteProgress(AppText.T("web.progress.mergeSelectionCompleted"));
                        }
                    }
                    catch (Exception ex)
                    {
                        this.AppendLog(AppText.F("web.merge.mergeSelectionError", ex.Message));
                        this.CompleteProgress(AppText.T("web.progress.mergeSelectionError"));
                    }

                }
                finally
                {
                    this.SetBusy(false);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Esegue merge di tutti gli episodi analizzati in background
        /// </summary>
        public void MergeAll()
        {
            if (!this.TryBeginOperation())
            {
                return;
            }

            Thread thread = new Thread(() =>
            {
                try
                {
                    List<FileProcessingRecord> snapshot;
                    List<FileProcessingRecord> analyzed = new List<FileProcessingRecord>();
                    bool stopped = false;
                    lock (this.StateLock)
                    {
                        snapshot = new List<FileProcessingRecord>(this._records);
                    }

                    for (int i = 0; i < snapshot.Count; i++)
                    {
                        if (snapshot[i].Status == FileStatus.Analyzed)
                        {
                            analyzed.Add(snapshot[i]);
                        }
                    }

                    this.BeginProgress(AppText.T("web.progress.mergeBatch"), analyzed.Count, false);

                    try
                    {
                        for (int i = 0; i < analyzed.Count; i++)
                        {
                            if (this.IsStopRequested())
                            {
                                stopped = true;
                                this.AppendLog(AppText.T("web.merge.mergeBatchStopped"));
                                this.CompleteProgress(AppText.T("web.progress.mergeStopped"));
                                break;
                            }

                            this.UpdateProgress(analyzed[i].EpisodeId, i + 1, i, 10, AppText.T("web.progress.merge"), false, false);
                            this._pipeline.MergeFile(analyzed[i]);
                            this.NotifyRecordsChanged();
                            this.UpdateProgress(analyzed[i].EpisodeId, i + 1, i + 1, 100, AppText.T("web.progress.completed"), false, false);
                        }

                        if (!stopped)
                        {
                            this.AppendLog(AppText.T("web.merge.mergeBatchCompleted"));
                            this.CompleteProgress(AppText.T("web.progress.mergeBatchCompleted"));
                        }
                    }
                    catch (Exception ex)
                    {
                        this.AppendLog(AppText.F("web.merge.mergeBatchError", ex.Message));
                        this.CompleteProgress(AppText.T("web.progress.mergeBatchError"));
                    }

                }
                finally
                {
                    this.SetBusy(false);
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Alterna lo stato skip di un episodio
        /// </summary>
        /// <param name="index">Indice del record nella lista</param>
        public void ToggleSkip(int index)
        {
            if (this.BusyState)
                return;
            FileProcessingRecord record = this.GetRecord(index);

            if (record == null)
            {
                return;
            }

            this.ToggleSkipInternal(record);
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Alterna lo stato skip di una selezione di episodi
        /// </summary>
        /// <param name="indices">Indici dei record nella lista</param>
        public void ToggleSkip(List<int> indices)
        {
            if (this.BusyState)
                return;
            List<FileProcessingRecord> selected = this.GetRecordsByIndices(indices);

            if (selected.Count == 0)
            {
                return;
            }

            for (int i = 0; i < selected.Count; i++)
            {
                this.ToggleSkipInternal(selected[i]);
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Aggiorna il delay manuale di un episodio
        /// </summary>
        /// <param name="index">Indice del record</param>
        /// <param name="audioDelayMs">Delay audio in ms</param>
        /// <param name="subDelayMs">Delay sottotitoli in ms</param>
        public void UpdateDelay(int index, int audioDelayMs, int subDelayMs)
        {
            if (this.BusyState)
                return;
            FileProcessingRecord record = this.GetRecord(index);

            if (record == null)
            {
                return;
            }

            record.ManualAudioDelayMs = audioDelayMs;
            record.ManualSubDelayMs = subDelayMs;
            this._pipeline.RecalculateDelays(record);
            this._pipeline.BuildMergeCommand(record);
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Valida e applica atomicamente una EditMap manuale al record corrente
        /// </summary>
        /// <param name="index">Indice del record nella lista corrente</param>
        /// <param name="expectedEpisodeId">Identità episodio vista all'apertura della dialog</param>
        /// <param name="expectedSourcePath">Path Source visto all'apertura della dialog</param>
        /// <param name="expectedLanguagePath">Path Language visto all'apertura della dialog</param>
        /// <param name="editedMap">Copia della mappa modificata</param>
        /// <param name="sourceDurationMs">Durata indicizzata Source</param>
        /// <param name="languageDurationMs">Durata indicizzata Language</param>
        /// <param name="sourceTailToleranceMs">Durata dell'ultimo frame Source per riconoscere lo scope Tail</param>
        /// <param name="errorMessage">Errore di validazione o applicazione</param>
        /// <returns>True quando il record è stato sostituito con la versione ricalcolata</returns>
        public bool UpdateEditMap(int index, string expectedEpisodeId, string expectedSourcePath, string expectedLanguagePath, EditMap editedMap, double sourceDurationMs, double languageDurationMs, double sourceTailToleranceMs, out string errorMessage)
        {
            errorMessage = "";
            if (editedMap == null || editedMap.Operations == null || editedMap.Operations.Count == 0)
            {
                errorMessage = AppText.T("web.editMap.mapRequired");
                return false;
            }
            if (!double.IsFinite(sourceDurationMs) || sourceDurationMs <= 0.0 || !double.IsFinite(languageDurationMs) || languageDurationMs <= 0.0)
            {
                errorMessage = AppText.T("web.editMap.indexesUnavailable");
                return false;
            }
            if (!this.TryBeginOperation())
            {
                errorMessage = AppText.T("web.merge.busyRetry");
                return false;
            }

            try
            {
                FileProcessingRecord original;
                lock (this.StateLock)
                {
                    original = index >= 0 && index < this._records.Count ? this._records[index] : null;
                }
                if (original == null || !string.Equals(original.EpisodeId, expectedEpisodeId, StringComparison.Ordinal) || !string.Equals(original.SourceFilePath, expectedSourcePath, StringComparison.Ordinal) || !string.Equals(original.LangFilePath, expectedLanguagePath, StringComparison.Ordinal))
                {
                    errorMessage = AppText.T("web.editMap.recordChanged");
                    return false;
                }
                if (original.Status == FileStatus.Done || original.Status == FileStatus.Processing || original.Status == FileStatus.Skipped)
                {
                    errorMessage = AppText.T("web.editMap.stateNotEditable");
                    return false;
                }
                if (!File.Exists(original.SourceFilePath) || !File.Exists(original.LangFilePath))
                {
                    errorMessage = AppText.T("web.editMap.filesUnavailable");
                    return false;
                }

                EditMapProjection projection = EditMapTimelineHelper.BuildProjection(EditMapTimelineHelper.Clone(editedMap), sourceDurationMs, languageDurationMs, sourceTailToleranceMs);
                if (!projection.Validation.IsValid)
                {
                    errorMessage = AppText.F("web.editMap.structuralErrorCount", projection.Validation.Errors.Count);
                    return false;
                }

                FileProcessingRecord updated = this.CloneRecord(original);
                updated.DeepAnalysisMap = EditMapTimelineHelper.Clone(projection.Map);
                updated.DeepAnalysisApplied = true;
                updated.DeepAnalysisMapManuallyEdited = true;
                updated.StretchFactor = projection.Map.StretchFactor;
                updated.SpeedCorrectionApplied = !string.IsNullOrEmpty(projection.Map.StretchFactor);
                updated.SyncOffsetMs = projection.Map.InitialDelayMs;
                updated.Status = FileStatus.Analyzed;
                updated.ErrorMessage = "";
                this._pipeline.RecalculateDelays(updated);
                this._pipeline.BuildMergeCommand(updated);
                if (updated.Status != FileStatus.Analyzed)
                {
                    errorMessage = !string.IsNullOrEmpty(updated.ErrorMessage) ? updated.ErrorMessage : AppText.T("web.editMap.rebuildFailed");
                    return false;
                }

                lock (this.StateLock)
                {
                    if (index < 0 || index >= this._records.Count || !object.ReferenceEquals(this._records[index], original))
                    {
                        errorMessage = AppText.T("web.editMap.changedWhileApplying");
                        return false;
                    }
                    this._records[index] = updated;
                }

                this.AppendLog(AppText.F("web.editMap.appliedLog", updated.EpisodeId, updated.DeepAnalysisMap.Operations.Count));
                this.NotifyRecordsChanged();
                return true;
            }
            finally
            {
                this.SetBusy(false);
            }
        }

        /// <summary>
        /// Richiede stop cooperativo dell'operazione corrente
        /// </summary>
        public void RequestStop()
        {
            lock (this.StateLock)
            {
                this.StopRequested = true;
                if (this._operationCancellation != null)
                    this._operationCancellation.Cancel();
            }

            this.AppendLog(AppText.T("web.merge.stopRequested"));
        }

        /// <summary>
        /// Numero di file sorgente trovati dall'ultimo scan, comprese le righe scartate perche' senza file lingua
        /// </summary>
        public int ScannedFileCount
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._scannedFileCount;
                }
            }
        }

        /// <summary>
        /// Restituisce una copia della lista record corrente
        /// </summary>
        /// <returns>Lista di record</returns>
        public List<FileProcessingRecord> GetRecords()
        {
            List<FileProcessingRecord> result;
            lock (this.StateLock)
            {
                result = new List<FileProcessingRecord>();
                for (int i = 0; i < this._records.Count; i++)
                {
                    result.Add(this.CloneRecord(this._records[i]));
                }
            }

            return result;
        }

        /// <summary>
        /// Indica se lo scope remux espone il lato richiesto
        /// </summary>
        /// <param name="side">Nome del lato</param>
        /// <returns>True per source e language</returns>
        public bool SupportsSide(string side)
        {
            return string.Equals(side, "source", StringComparison.OrdinalIgnoreCase) || string.Equals(side, "language", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Risolve il file sorgente o quello di lingua di un record
        /// </summary>
        /// <param name="recordIndex">Indice del record</param>
        /// <param name="side">Nome del lato</param>
        /// <returns>Sorgente multimediale, null se il record non esiste</returns>
        public MediaSource ResolveMediaSource(int recordIndex, string side)
        {
            FileProcessingRecord record = this.GetRecord(recordIndex);
            if (record == null)
                return null;
            if (string.Equals(side, "source", StringComparison.OrdinalIgnoreCase))
                return new MediaSource(record.SourceFilePath, record.SourceAudioTracks);
            if (string.Equals(side, "language", StringComparison.OrdinalIgnoreCase))
                return new MediaSource(record.LangFilePath, record.ImportedAudioTracks);
            return null;
        }

        /// <summary>
        /// Restituisce un record per indice
        /// </summary>
        /// <param name="index">Indice nella lista</param>
        /// <returns>Record o null se indice non valido</returns>
        public FileProcessingRecord GetRecord(int index)
        {
            FileProcessingRecord result = null;
            lock (this.StateLock)
            {
                if (index >= 0 && index < this._records.Count)
                {
                    result = this._records[index];
                }
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Restituisce record originali per una lista di indici, senza duplicati
        /// </summary>
        /// <param name="indices">Indici richiesti</param>
        /// <returns>Lista record originali</returns>
        private List<FileProcessingRecord> GetRecordsByIndices(List<int> indices)
        {
            List<FileProcessingRecord> result = new List<FileProcessingRecord>();

            if (indices == null)
            {
                return result;
            }

            lock (this.StateLock)
            {
                for (int i = 0; i < indices.Count; i++)
                {
                    if (indices[i] < 0 || indices[i] >= this._records.Count)
                    {
                        continue;
                    }

                    if (!result.Contains(this._records[indices[i]]))
                    {
                        result.Add(this._records[indices[i]]);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Verifica se la nuova configurazione richiede un nuovo scan
        /// </summary>
        private bool ScanInputsChanged(Options previousOptions, Options newOptions)
        {
            bool result = false;

            if (previousOptions == null || newOptions == null)
            {
                return true;
            }

            if (!string.Equals(previousOptions.SourceFolder, newOptions.SourceFolder, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.LanguageFolder, newOptions.LanguageFolder, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.MatchPattern, newOptions.MatchPattern, StringComparison.Ordinal) ||
                previousOptions.Recursive != newOptions.Recursive ||
                !this.StringListsEqual(previousOptions.FileExtensions, newOptions.FileExtensions))
            {
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Verifica se la nuova configurazione invalida realmente i risultati di analisi
        /// </summary>
        private bool AnalysisOptionsChanged(Options previousOptions, Options newOptions)
        {
            bool result = false;

            if (previousOptions == null || newOptions == null)
            {
                return true;
            }

            if (!this.StringListsEqual(previousOptions.TargetLanguage, newOptions.TargetLanguage) ||
                !this.StringListsEqual(previousOptions.AudioCodec, newOptions.AudioCodec) ||
                previousOptions.SubOnly != newOptions.SubOnly ||
                previousOptions.AudioOnly != newOptions.AudioOnly ||
                previousOptions.FrameSync != newOptions.FrameSync ||
                previousOptions.DeepAnalysis != newOptions.DeepAnalysis ||
                !string.Equals(previousOptions.SpeedCorrectionMode, newOptions.SpeedCorrectionMode, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.ManualStretchFactor, newOptions.ManualStretchFactor, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.AnalysisCropSourcePx, newOptions.AnalysisCropSourcePx, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.AnalysisCropLanguagePx, newOptions.AnalysisCropLanguagePx, StringComparison.Ordinal))
            {
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Verifica se la nuova configurazione richiede soltanto di ricostruire render e preview
        /// </summary>
        private bool RenderOptionsChanged(Options previousOptions, Options newOptions)
        {
            bool result = false;

            if (previousOptions == null || newOptions == null)
            {
                return true;
            }

            if (!this.StringListsEqual(previousOptions.KeepSourceAudioLangs, newOptions.KeepSourceAudioLangs) ||
                !this.StringListsEqual(previousOptions.KeepSourceAudioCodec, newOptions.KeepSourceAudioCodec) ||
                !this.StringListsEqual(previousOptions.KeepSourceSubtitleLangs, newOptions.KeepSourceSubtitleLangs) ||
                previousOptions.SubtitleCanvasRewrite != newOptions.SubtitleCanvasRewrite ||
                previousOptions.AudioDelay != newOptions.AudioDelay ||
                previousOptions.SubtitleDelay != newOptions.SubtitleDelay ||
                previousOptions.AudioSourceFillThresholdMs != newOptions.AudioSourceFillThresholdMs ||
                previousOptions.AudioSourceFillStart != newOptions.AudioSourceFillStart ||
                previousOptions.AudioSourceFillEnd != newOptions.AudioSourceFillEnd ||
                previousOptions.AudioSourceFillInsertSilence != newOptions.AudioSourceFillInsertSilence ||
                previousOptions.Overwrite != newOptions.Overwrite ||
                !string.Equals(previousOptions.AudioSourceFillLanguage, newOptions.AudioSourceFillLanguage, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.DestinationFolder, newOptions.DestinationFolder, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.AudioFormat, newOptions.AudioFormat, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.AudioProcessingScope, newOptions.AudioProcessingScope, StringComparison.Ordinal) ||
                previousOptions.AudioDownsample24To16 != newOptions.AudioDownsample24To16 ||
                previousOptions.AudioPeakNormalize != newOptions.AudioPeakNormalize ||
                Math.Abs(previousOptions.AudioPeakTargetDb - newOptions.AudioPeakTargetDb) > 0.0001 ||
                !string.Equals(previousOptions.EncodingProfileName, newOptions.EncodingProfileName, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.MkvMergePath, newOptions.MkvMergePath, StringComparison.Ordinal))
            {
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Ricostruisce delay, piano audio e comando senza cancellare i risultati di analisi
        /// </summary>
        private int RefreshAnalyzedRecordsAfterRenderChange()
        {
            List<FileProcessingRecord> records;
            int result = 0;

            lock (this.StateLock)
            {
                records = new List<FileProcessingRecord>(this._records);
            }

            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].Status != FileStatus.Analyzed &&
                    !(records[i].Status == FileStatus.Error && records[i].DeepAnalysisApplied))
                {
                    continue;
                }

                if (records[i].Status == FileStatus.Error)
                {
                    records[i].Status = FileStatus.Analyzed;
                    records[i].ErrorMessage = "";
                }

                this._pipeline.RecalculateDelays(records[i]);
                this._pipeline.BuildMergeCommand(records[i]);
                result++;
            }

            return result;
        }

        /// <summary>
        /// Confronta due liste stringa preservando ordine e valori
        /// </summary>
        private bool StringListsEqual(List<string> left, List<string> right)
        {
            bool result = false;

            if (left == null || right == null)
            {
                return left == right;
            }

            if (left.Count == right.Count)
            {
                result = true;
                for (int i = 0; i < left.Count; i++)
                {
                    if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                    {
                        result = false;
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Scarta analisi e preview calcolate con una configurazione precedente
        /// </summary>
        private int ResetAnalyzedRecordsAfterConfigChange()
        {
            int result = 0;

            for (int i = 0; i < this._records.Count; i++)
            {
                if (this._records[i].Status == FileStatus.Done || this._records[i].Status == FileStatus.Skipped)
                {
                    continue;
                }

                this.ResetRecordAnalysisState(this._records[i]);
                result++;
            }

            return result;
        }

        /// <summary>
        /// Ripulisce i dati derivati da analisi/merge lasciando intatti file e delay manuali
        /// </summary>
        private void ResetRecordAnalysisState(FileProcessingRecord record)
        {
            record.ResetDerivedState();

            if (this._options.TargetLanguage.Count == 0 || !string.IsNullOrEmpty(record.LangFilePath))
            {
                record.Status = FileStatus.Pending;
            }
            else
            {
                record.Status = FileStatus.Skipped;
                record.SkipReason = AppText.T("web.merge.skipNoMatch");
            }
        }

        /// <summary>
        /// Applica la logica skip/unskip su un record
        /// </summary>
        /// <param name="record">Record da aggiornare</param>
        private void ToggleSkipInternal(FileProcessingRecord record)
        {
            if (record.Status == FileStatus.Skipped)
            {
                // In merge mode, consenti unskip solo se c'è un file lingua associato
                if (this._options.TargetLanguage.Count == 0 || !string.IsNullOrEmpty(record.LangFilePath))
                {
                    record.Status = FileStatus.Pending;
                    record.SkipReason = "";
                }
            }
            else if (record.Status == FileStatus.Pending || record.Status == FileStatus.Analyzed || record.Status == FileStatus.Error)
            {
                record.Status = FileStatus.Skipped;
                record.SkipReason = AppText.T("web.merge.skipByUser");
            }
        }

        /// <summary>
        /// Imposta lo stato busy e notifica
        /// </summary>
        /// <param name="busy">Stato busy</param>
        private void SetBusy(bool busy)
        {
            CancellationTokenSource cancellation = null;
            lock (this.StateLock)
            {
                this.BusyState = busy;
                this.StopRequested = false;
                if (!busy)
                {
                    cancellation = this._operationCancellation;
                    this._operationCancellation = null;
                }
            }
            if (cancellation != null)
                cancellation.Dispose();

            this.NotifyProgressChanged();
        }

        /// <summary>
        /// Riserva atomicamente l'esecuzione di una sola operazione in background
        /// </summary>
        /// <returns>True se il chiamante ha acquisito la riserva</returns>
        private bool TryBeginOperation()
        {
            lock (this.StateLock)
            {
                if (this.BusyState)
                    return false;
                this.BusyState = true;
                this.StopRequested = false;
                this._operationCancellation = new CancellationTokenSource();
                return true;
            }
        }

        /// <summary>
        /// Restituisce il token dell'operazione correntemente riservata
        /// </summary>
        /// <returns>Token cooperativo oppure token non cancellabile</returns>
        private CancellationToken GetOperationCancellationToken()
        {
            lock (this.StateLock)
            {
                return this._operationCancellation != null ? this._operationCancellation.Token : CancellationToken.None;
            }
        }

        /// <summary>
        /// Crea uno snapshot UI di un record senza condividere liste mutabili
        /// </summary>
        /// <param name="record">Record originale</param>
        /// <returns>Copia per lettura UI</returns>
        private FileProcessingRecord CloneRecord(FileProcessingRecord record)
        {
            FileProcessingRecord result = new FileProcessingRecord();

            result.EpisodeId = record.EpisodeId;
            result.SourceFileName = record.SourceFileName;
            result.SourceSize = record.SourceSize;
            result.SourceAudioLangs = new List<string>(record.SourceAudioLangs);
            result.SourceSubLangs = new List<string>(record.SourceSubLangs);
            result.LangFileName = record.LangFileName;
            result.LangSize = record.LangSize;
            result.LangAudioLangs = new List<string>(record.LangAudioLangs);
            result.LangSubLangs = new List<string>(record.LangSubLangs);
            result.ResultFileName = record.ResultFileName;
            result.ResultSize = record.ResultSize;
            result.ResultAudioLangs = new List<string>(record.ResultAudioLangs);
            result.ResultSubLangs = new List<string>(record.ResultSubLangs);
            result.AudioDelayApplied = record.AudioDelayApplied;
            result.SubDelayApplied = record.SubDelayApplied;
            result.FrameSyncTimeMs = record.FrameSyncTimeMs;
            result.FrameSyncResult = record.FrameSyncResult;
            result.MergeTimeMs = record.MergeTimeMs;
            result.Success = record.Success;
            result.SpeedCorrectionTimeMs = record.SpeedCorrectionTimeMs;
            result.StretchFactor = record.StretchFactor;
            result.SpeedCorrectionApplied = record.SpeedCorrectionApplied;
            result.SkipReason = record.SkipReason;
            result.Status = record.Status;
            result.ManualAudioDelayMs = record.ManualAudioDelayMs;
            result.ManualSubDelayMs = record.ManualSubDelayMs;
            result.AnalysisLog = new List<string>(record.AnalysisLog);
            result.ErrorMessage = record.ErrorMessage;
            result.SourceFilePath = record.SourceFilePath;
            result.LangFilePath = record.LangFilePath;
            result.SyncOffsetMs = record.SyncOffsetMs;
            result.MergeCommand = record.MergeCommand;
            result.EncodingProfileName = record.EncodingProfileName;
            result.EncodingTimeMs = record.EncodingTimeMs;
            result.EncodedSize = record.EncodedSize;
            result.EncodingCommand = record.EncodingCommand;
            result.ResultFilePath = record.ResultFilePath;
            result.SourceAudioTracks = new List<TrackInfo>(record.SourceAudioTracks);
            result.SourceSubTracks = new List<TrackInfo>(record.SourceSubTracks);
            result.KeptSourceAudioIds = new List<int>(record.KeptSourceAudioIds);
            result.KeptSourceSubIds = new List<int>(record.KeptSourceSubIds);
            result.ImportedAudioTracks = new List<TrackInfo>(record.ImportedAudioTracks);
            result.ImportedSubTracks = new List<TrackInfo>(record.ImportedSubTracks);
            result.DisplayAudioFormat = record.DisplayAudioFormat;
            result.DeepAnalysisMap = EditMapTimelineHelper.Clone(record.DeepAnalysisMap);
            result.DeepAnalysisTimeMs = record.DeepAnalysisTimeMs;
            result.DeepAnalysisApplied = record.DeepAnalysisApplied;
            result.DeepAnalysisMapManuallyEdited = record.DeepAnalysisMapManuallyEdited;
            result.DeepAnalysisResult = record.DeepAnalysisResult;
            result.AudioProcessingPreview = this.CloneAudioProcessingPlan(record.AudioProcessingPreview);

            return result;
        }

        /// <summary>
        /// Clona il piano audio preview evitando condivisione delle liste mutabili
        /// </summary>
        /// <param name="source">Piano audio originale</param>
        /// <returns>Copia del piano o null</returns>
        private AudioProcessingPlan CloneAudioProcessingPlan(AudioProcessingPlan source)
        {
            AudioProcessingPlan result;
            if (source == null)
            {
                return null;
            }

            result = new AudioProcessingPlan();
            for (int i = 0; i < source.SourceTracks.Count; i++)
            {
                result.SourceTracks.Add(this.CloneAudioTrackProcessingPlan(source.SourceTracks[i]));
            }
            for (int i = 0; i < source.LangTracks.Count; i++)
            {
                result.LangTracks.Add(this.CloneAudioTrackProcessingPlan(source.LangTracks[i]));
            }

            return result;
        }

        /// <summary>
        /// Clona il piano di una traccia audio
        /// </summary>
        /// <param name="source">Piano traccia originale</param>
        /// <returns>Copia del piano traccia</returns>
        private AudioTrackProcessingPlan CloneAudioTrackProcessingPlan(AudioTrackProcessingPlan source)
        {
            AudioTrackProcessingPlan result = new AudioTrackProcessingPlan();
            result.IsSource = source.IsSource;
            result.Track = source.Track;
            result.GenericProcessing = source.GenericProcessing;
            result.GenericRenderRequired = source.GenericRenderRequired;
            result.TimelinePolicyRenderRequired = source.TimelinePolicyRenderRequired;
            result.StretchRender = source.StretchRender;
            result.StretchFactor = source.StretchFactor;
            result.StretchRatio = source.StretchRatio;
            result.AudioTempo = source.AudioTempo;
            result.AudioTempoFilter = source.AudioTempoFilter;
            result.InitialTimelineOffsetMs = source.InitialTimelineOffsetMs;
            result.DeepEditRender = source.DeepEditRender;
            result.SourceFillConfigured = source.SourceFillConfigured;
            result.SourceFillHasWork = source.SourceFillHasWork;
            result.ActualSourceFill = source.ActualSourceFill;
            result.RenderRequired = source.RenderRequired;
            result.BypassAudioDelay = source.BypassAudioDelay;
            result.SourceFillTrack = source.SourceFillTrack;
            result.SourceFillPlan = this.CloneAudioSourceFillPlan(source.SourceFillPlan);
            result.ErrorMessage = source.ErrorMessage;
            return result;
        }

        /// <summary>
        /// Clona il dettaglio source-fill del piano audio
        /// </summary>
        /// <param name="source">Piano source-fill originale</param>
        /// <returns>Copia del piano source-fill o null</returns>
        private AudioSourceFillPlan CloneAudioSourceFillPlan(AudioSourceFillPlan source)
        {
            AudioSourceFillPlan result;
            if (source == null)
            {
                return null;
            }

            result = new AudioSourceFillPlan();
            result.StartFillMs = source.StartFillMs;
            result.EndFillMs = source.EndFillMs;
            result.SourceDurationMs = source.SourceDurationMs;
            result.StretchRatio = source.StretchRatio;
            result.LangTempo = source.LangTempo;
            result.SourceInitialTimelineOffsetMs = source.SourceInitialTimelineOffsetMs;
            result.LangInitialTimelineOffsetMs = source.LangInitialTimelineOffsetMs;
            result.InitialSilenceMs = source.InitialSilenceMs;
            result.InitialTrimMs = source.InitialTrimMs;
            result.InsertOperations = new List<EditOperation>(source.InsertOperations);
            result.SourceFilledOperations = new List<EditOperation>(source.SourceFilledOperations);
            return result;
        }

        /// <summary>
        /// Inizializza lo stato avanzamento
        /// </summary>
        /// <param name="operation">Nome operazione</param>
        /// <param name="total">Numero totale elementi</param>
        /// <param name="indeterminate">True se durata non determinabile</param>
        private void BeginProgress(string operation, int total, bool indeterminate)
        {
            lock (this.StateLock)
            {
                this.ProgressState.IsActive = true;
                this.ProgressState.Operation = operation;
                this.ProgressState.CurrentEpisode = "";
                this.ProgressState.CurrentStatus = "";
                this.ProgressState.CurrentIndex = 0;
                this.ProgressState.Total = total;
                this.ProgressState.Completed = 0;
                this.ProgressState.CurrentPercent = 0;
                this.ProgressState.GlobalPercent = 0;
                this.ProgressState.CurrentIndeterminate = indeterminate;
                this.ProgressState.GlobalIndeterminate = indeterminate || total <= 0;
            }

            this.NotifyProgressChanged();
        }

        /// <summary>
        /// Aggiorna lo stato avanzamento
        /// </summary>
        private void UpdateProgress(string currentEpisode, int currentIndex, int completed, int currentPercent, string currentStatus, bool currentIndeterminate, bool globalIndeterminate)
        {
            int globalPercent = 0;
            lock (this.StateLock)
            {
                if (this.ProgressState.Total > 0)
                {
                    globalPercent = completed * 100 / this.ProgressState.Total;
                }

                this.ProgressState.CurrentEpisode = currentEpisode != null ? currentEpisode : "";
                this.ProgressState.CurrentStatus = currentStatus != null ? currentStatus : "";
                this.ProgressState.CurrentIndex = currentIndex;
                this.ProgressState.Completed = completed;
                this.ProgressState.CurrentPercent = this.ClampPercent(currentPercent);
                this.ProgressState.GlobalPercent = this.ClampPercent(globalPercent);
                this.ProgressState.CurrentIndeterminate = currentIndeterminate;
                this.ProgressState.GlobalIndeterminate = globalIndeterminate || this.ProgressState.Total <= 0;
            }

            this.NotifyProgressChanged();
        }

        /// <summary>
        /// Marca lo stato avanzamento come completato
        /// </summary>
        /// <param name="status">Stato finale</param>
        private void CompleteProgress(string status)
        {
            lock (this.StateLock)
            {
                this.ProgressState.IsActive = false;
                this.ProgressState.CurrentStatus = status != null ? status : "";
                this.ProgressState.CurrentPercent = 100;
                this.ProgressState.CurrentIndeterminate = false;
                this.ProgressState.GlobalIndeterminate = false;

                if (this.ProgressState.Total > 0)
                {
                    this.ProgressState.Completed = this.ProgressState.Total;
                    this.ProgressState.GlobalPercent = 100;
                }
            }

            this.NotifyProgressChanged();
        }

        /// <summary>
        /// Aggiorna il progresso episodio usando substep strutturati del Core
        /// </summary>
        private void UpdateProgressFromPipelineStep(LogSection section, int percent, string status)
        {
            int mappedPercent = this.MapPipelineStepPercent(section, percent);

            if (!this.BusyState)
            {
                return;
            }

            this.UpdateCurrentProgress(mappedPercent, status);
        }

        /// <summary>
        /// Mappa percentuali locali dei servizi su una progressione episodio stabile
        /// </summary>
        private int MapPipelineStepPercent(LogSection section, int percent)
        {
            int clamped = this.ClampPercent(percent);
            int result = clamped;

            if (section == LogSection.Speed || section == LogSection.FrameSync || section == LogSection.Deep)
            {
                result = 5 + clamped * 85 / 100;
            }
            else if (section == LogSection.Conv)
            {
                result = 10 + clamped * 50 / 100;
            }
            else if (section == LogSection.Merge)
            {
                result = 60 + clamped * 40 / 100;
            }

            return this.ClampPercent(result);
        }

        /// <summary>
        /// Aggiorna solo la barra episodio mantenendo globale e contatori batch
        /// </summary>
        private void UpdateCurrentProgress(int currentPercent, string currentStatus)
        {
            int globalPercent;
            lock (this.StateLock)
            {
                if (!this.ProgressState.IsActive)
                {
                    return;
                }

                if (currentPercent < this.ProgressState.CurrentPercent)
                {
                    currentPercent = this.ProgressState.CurrentPercent;
                }

                this.ProgressState.CurrentPercent = this.ClampPercent(currentPercent);
                this.ProgressState.CurrentStatus = currentStatus != null ? currentStatus : "";
                this.ProgressState.CurrentIndeterminate = false;

                if (this.ProgressState.Total > 0)
                {
                    globalPercent = ((this.ProgressState.Completed * 100) + this.ProgressState.CurrentPercent) / this.ProgressState.Total;
                    this.ProgressState.GlobalPercent = this.ClampPercent(globalPercent);
                    this.ProgressState.GlobalIndeterminate = false;
                }
            }

            this.NotifyProgressChanged();
        }

        /// <summary>
        /// Limita una percentuale al range valido
        /// </summary>
        private int ClampPercent(int value)
        {
            if (value < 0)
            {
                return 0;
            }

            if (value > 100)
            {
                return 100;
            }

            return value;
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
