using RemuxForge.Core.Audio;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Pipeline;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Orchestratore singleton che gestisce il ProcessingPipeline per la WebUI
    /// </summary>
    public class MergeOrchestrator
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
        /// Opzioni correnti
        /// </summary>
        private Options _options;

        /// <summary>
        /// Lock per accesso thread-safe ai record
        /// </summary>
        private object _lock;

        /// <summary>
        /// Stato avanzamento operazione corrente
        /// </summary>
        private ProcessingProgressState _progress;

        /// <summary>
        /// Flag: indica se un'operazione e' in corso
        /// </summary>
        private volatile bool _isBusy;

        /// <summary>
        /// Flag: richiesta di stop cooperativo
        /// </summary>
        private volatile bool _stopRequested;

        /// <summary>
        /// Buffer log accumulato
        /// </summary>
        private string _logText;

        /// <summary>
        /// Limite massimo dimensione log in caratteri (~500 KB)
        /// </summary>
        private const int LOG_MAX_LENGTH = 500000;

        /// <summary>
        /// Indice riga selezionata nella tabella episodi
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
        public MergeOrchestrator()
        {
            this._pipeline = new ProcessingPipeline();
            this._records = new List<FileProcessingRecord>();
            this._options = new Options();
            this._lock = new object();
            this._progress = new ProcessingProgressState();
            this._isBusy = false;
            this._stopRequested = false;
            this._logText = AppText.T("web.merge.ready");
            this._selectedIndex = -1;

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
                string formatted = prefix.Length > 0 ? prefix + text : text;
                this.AppendLog(formatted);
            };

            this._pipeline.OnFileUpdated += _ =>
            {
                this.OnRecordsChanged?.Invoke();
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
            bool processingOptionsChanged;
            int resetCount;
            Options previousOptions;
            errorMessage = "";
            resetCount = 0;
            scanInputsChanged = false;
            processingOptionsChanged = false;

            if (opts == null)
            {
                errorMessage = AppText.T("validation.invalidConfig");
                return result;
            }

            if (this._isBusy)
            {
                errorMessage = AppText.T("web.merge.busyRetry");
                return result;
            }

            lock (this._lock)
            {
                previousOptions = this._options;
                scanInputsChanged = this.ScanInputsChanged(previousOptions, opts);
                processingOptionsChanged = scanInputsChanged || this.ProcessingOptionsChanged(previousOptions, opts);
            }

            if (opts.SourceFolder.Length > 0)
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
                    lock (this._lock)
                    {
                        this._options = opts;
                        this._records.Clear();
                        this._selectedIndex = -1;
                    }
                    this.AppendLog(AppText.T("web.merge.configAppliedScanInvalidated"));
                }
                else if (processingOptionsChanged)
                {
                    lock (this._lock)
                    {
                        this._options = opts;
                        resetCount = this.ResetAnalyzedRecordsAfterConfigChange();
                    }

                    this.AppendLog(resetCount > 0
                        ? AppText.F("web.merge.configAppliedAnalysisReset", resetCount)
                        : AppText.T("web.merge.configApplied"));
                }
                else
                {
                    lock (this._lock)
                    {
                        this._options = opts;
                    }

                    this.AppendLog(AppText.T("web.merge.configApplied"));
                }
                this.OnRecordsChanged?.Invoke();
            }

            return result;
        }

        /// <summary>
        /// Esegue scan delle cartelle in background (come flusso CLI: check opts + Initialize + ScanFiles)
        /// </summary>
        public void Scan()
        {
            if (this._isBusy)
            {
                return;
            }

            // Verifica parametro obbligatorio: source folder
            if (this._options.SourceFolder.Length == 0)
            {
                this.AppendLog(AppText.T("web.merge.configureSourceFirst"));
                return;
            }

            Thread thread = new Thread(() =>
            {
                this.SetBusy(true);
                this.BeginProgress(AppText.T("web.progress.scan"), 0, true);

                try
                {
                    this.UpdateProgress("", 0, 0, 0, AppText.T("web.progress.initialization"), true, true);

                    // Inizializza pipeline con opzioni correnti (come flusso CLI)
                    if (!this._pipeline.Initialize(this._options))
                    {
                        this.AppendLog(AppText.T("web.merge.pipelineInitError"));
                        this.CompleteProgress(AppText.T("web.progress.initializationError"));
                        this.SetBusy(false);
                        return;
                    }

                    this.UpdateProgress("", 0, 0, 30, AppText.T("web.progress.scanFiles"), true, true);

                    // Scan
                    List<FileProcessingRecord> scanned = this._pipeline.ScanFiles();

                    // Ordina per EpisodeId (come flusso CLI)
                    scanned.Sort((a, b) => string.Compare(a.EpisodeId, b.EpisodeId, StringComparison.OrdinalIgnoreCase));

                    lock (this._lock)
                    {
                        this._records = scanned;
                    }

                    // Conta file pronti e saltati
                    int pending = 0;
                    int skipped = 0;
                    for (int i = 0; i < scanned.Count; i++)
                    {
                        if (scanned[i].Status == FileStatus.Pending) { pending++; }
                        else if (scanned[i].Status == FileStatus.Skipped) { skipped++; }
                    }

                    this.OnRecordsChanged?.Invoke();
                    this.AppendLog(AppText.F("web.merge.scanCompleted", scanned.Count, pending, skipped));
                    this.CompleteProgress(AppText.T("web.progress.scanCompleted"));
                }
                catch (Exception ex)
                {
                    this.AppendLog(AppText.F("web.merge.scanError", ex.Message));
                    this.CompleteProgress(AppText.T("web.progress.scanError"));
                }

                this.SetBusy(false);
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

            if (record == null || this._isBusy)
            {
                return;
            }

            Thread thread = new Thread(() =>
            {
                this.SetBusy(true);
                this.BeginProgress(AppText.T("web.progress.analyzeEpisode"), 1, false);

                try
                {
                    this.UpdateProgress(record.EpisodeId, 1, 0, 5, AppText.T("web.progress.analysis"), false, false);
                    this._pipeline.AnalyzeFile(record);
                    this.UpdateProgress(record.EpisodeId, 1, 0, 85, AppText.T("web.progress.mergeCommand"), false, false);
                    this._pipeline.BuildMergeCommand(record);
                    this.OnRecordsChanged?.Invoke();
                    this.UpdateProgress(record.EpisodeId, 1, 1, 100, AppText.T("web.progress.completed"), false, false);
                    this.CompleteProgress(AppText.T("web.progress.analysisCompleted"));
                }
                catch (Exception ex)
                {
                    this.AppendLog(AppText.F("web.merge.analysisError", ex.Message));
                    this.CompleteProgress(AppText.T("web.progress.analysisError"));
                }

                this.SetBusy(false);
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

            if (selected.Count == 0 || this._isBusy)
            {
                return;
            }

            Thread thread = new Thread(() =>
            {
                this.SetBusy(true);
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
                        this._pipeline.AnalyzeFile(selected[i]);
                        this.UpdateProgress(selected[i].EpisodeId, i + 1, i, 85, AppText.T("web.progress.mergeCommand"), false, false);
                        this._pipeline.BuildMergeCommand(selected[i]);
                        this.OnRecordsChanged?.Invoke();
                        this.UpdateProgress(selected[i].EpisodeId, i + 1, i + 1, 100, AppText.T("web.progress.completed"), false, false);
                    }

                    if (!stopped)
                    {
                        this.CompleteProgress(AppText.T("web.progress.analysisSelectionCompleted"));
                    }
                }
                catch (Exception ex)
                {
                    this.AppendLog(AppText.F("web.merge.analysisSelectionError", ex.Message));
                    this.CompleteProgress(AppText.T("web.progress.analysisSelectionError"));
                }

                this.SetBusy(false);
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Analizza tutti gli episodi pendenti in background
        /// </summary>
        public void AnalyzeAll()
        {
            if (this._isBusy)
            {
                return;
            }

            Thread thread = new Thread(() =>
            {
                this.SetBusy(true);
                List<FileProcessingRecord> snapshot;
                List<FileProcessingRecord> pending = new List<FileProcessingRecord>();
                bool stopped = false;
                lock (this._lock)
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
                        this._pipeline.AnalyzeFile(pending[i]);
                        this.UpdateProgress(pending[i].EpisodeId, i + 1, i, 85, AppText.T("web.progress.mergeCommand"), false, false);
                        this._pipeline.BuildMergeCommand(pending[i]);
                        this.OnRecordsChanged?.Invoke();
                        this.UpdateProgress(pending[i].EpisodeId, i + 1, i + 1, 100, AppText.T("web.progress.completed"), false, false);
                    }

                    if (!stopped)
                    {
                        this.CompleteProgress(AppText.T("web.progress.analysisBatchCompleted"));
                    }
                }
                catch (Exception ex)
                {
                    this.AppendLog(AppText.F("web.merge.analysisBatchError", ex.Message));
                    this.CompleteProgress(AppText.T("web.progress.analysisBatchError"));
                }

                this.SetBusy(false);
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

            if (record == null || this._isBusy)
            {
                return;
            }

            Thread thread = new Thread(() =>
            {
                this.SetBusy(true);
                this.BeginProgress(AppText.T("web.progress.mergeEpisode"), 1, false);

                try
                {
                    this.UpdateProgress(record.EpisodeId, 1, 0, 10, AppText.T("web.progress.merge"), false, false);
                    this._pipeline.MergeFile(record);
                    this.OnRecordsChanged?.Invoke();
                    this.UpdateProgress(record.EpisodeId, 1, 1, 100, AppText.T("web.progress.completed"), false, false);
                    this.CompleteProgress(AppText.T("web.progress.mergeCompleted"));
                }
                catch (Exception ex)
                {
                    this.AppendLog(AppText.F("web.merge.mergeError", ex.Message));
                    this.CompleteProgress(AppText.T("web.progress.mergeError"));
                }

                this.SetBusy(false);
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

            if (selected.Count == 0 || this._isBusy)
            {
                return;
            }

            Thread thread = new Thread(() =>
            {
                this.SetBusy(true);
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
                        this.OnRecordsChanged?.Invoke();
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

                this.SetBusy(false);
            });
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Esegue merge di tutti gli episodi analizzati in background
        /// </summary>
        public void MergeAll()
        {
            if (this._isBusy)
            {
                return;
            }

            Thread thread = new Thread(() =>
            {
                this.SetBusy(true);
                List<FileProcessingRecord> snapshot;
                List<FileProcessingRecord> analyzed = new List<FileProcessingRecord>();
                bool stopped = false;
                lock (this._lock)
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
                        this.OnRecordsChanged?.Invoke();
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

                this.SetBusy(false);
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
            FileProcessingRecord record = this.GetRecord(index);

            if (record == null)
            {
                return;
            }

            this.ToggleSkipInternal(record);
            this.OnRecordsChanged?.Invoke();
        }

        /// <summary>
        /// Alterna lo stato skip di una selezione di episodi
        /// </summary>
        /// <param name="indices">Indici dei record nella lista</param>
        public void ToggleSkip(List<int> indices)
        {
            List<FileProcessingRecord> selected = this.GetRecordsByIndices(indices);

            if (selected.Count == 0)
            {
                return;
            }

            for (int i = 0; i < selected.Count; i++)
            {
                this.ToggleSkipInternal(selected[i]);
            }

            this.OnRecordsChanged?.Invoke();
        }

        /// <summary>
        /// Aggiorna il delay manuale di un episodio
        /// </summary>
        /// <param name="index">Indice del record</param>
        /// <param name="audioDelayMs">Delay audio in ms</param>
        /// <param name="subDelayMs">Delay sottotitoli in ms</param>
        public void UpdateDelay(int index, int audioDelayMs, int subDelayMs)
        {
            FileProcessingRecord record = this.GetRecord(index);

            if (record == null)
            {
                return;
            }

            record.ManualAudioDelayMs = audioDelayMs;
            record.ManualSubDelayMs = subDelayMs;
            this._pipeline.RecalculateDelays(record);
            this._pipeline.BuildMergeCommand(record);
            this.OnRecordsChanged?.Invoke();
        }

        /// <summary>
        /// Richiede stop cooperativo dell'operazione corrente
        /// </summary>
        public void RequestStop()
        {
            lock (this._lock)
            {
                this._stopRequested = true;
            }

            this.AppendLog(AppText.T("web.merge.stopRequested"));
        }

        /// <summary>
        /// Restituisce una copia della lista record corrente
        /// </summary>
        /// <returns>Lista di record</returns>
        public List<FileProcessingRecord> GetRecords()
        {
            List<FileProcessingRecord> result;
            lock (this._lock)
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
        /// Restituisce un record per indice
        /// </summary>
        /// <param name="index">Indice nella lista</param>
        /// <returns>Record o null se indice non valido</returns>
        public FileProcessingRecord GetRecord(int index)
        {
            FileProcessingRecord result = null;
            lock (this._lock)
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

            lock (this._lock)
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
        /// Verifica se la nuova configurazione invalida analisi e preview esistenti
        /// </summary>
        private bool ProcessingOptionsChanged(Options previousOptions, Options newOptions)
        {
            bool result = false;

            if (previousOptions == null || newOptions == null)
            {
                return true;
            }

            if (!this.StringListsEqual(previousOptions.TargetLanguage, newOptions.TargetLanguage) ||
                !this.StringListsEqual(previousOptions.AudioCodec, newOptions.AudioCodec) ||
                !this.StringListsEqual(previousOptions.KeepSourceAudioLangs, newOptions.KeepSourceAudioLangs) ||
                !this.StringListsEqual(previousOptions.KeepSourceAudioCodec, newOptions.KeepSourceAudioCodec) ||
                !this.StringListsEqual(previousOptions.KeepSourceSubtitleLangs, newOptions.KeepSourceSubtitleLangs) ||
                previousOptions.SubOnly != newOptions.SubOnly ||
                previousOptions.AudioOnly != newOptions.AudioOnly ||
                previousOptions.FrameSync != newOptions.FrameSync ||
                previousOptions.DeepAnalysis != newOptions.DeepAnalysis ||
                previousOptions.SubtitleCanvasRewrite != newOptions.SubtitleCanvasRewrite ||
                previousOptions.AudioDelay != newOptions.AudioDelay ||
                previousOptions.SubtitleDelay != newOptions.SubtitleDelay ||
                previousOptions.AudioSourceFillThresholdMs != newOptions.AudioSourceFillThresholdMs ||
                previousOptions.AudioSourceFillStart != newOptions.AudioSourceFillStart ||
                previousOptions.AudioSourceFillEnd != newOptions.AudioSourceFillEnd ||
                previousOptions.AudioSourceFillInsertSilence != newOptions.AudioSourceFillInsertSilence ||
                previousOptions.Overwrite != newOptions.Overwrite ||
                !string.Equals(previousOptions.AudioSourceFillLanguage, newOptions.AudioSourceFillLanguage, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.SpeedCorrectionMode, newOptions.SpeedCorrectionMode, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.ManualStretchFactor, newOptions.ManualStretchFactor, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.DestinationFolder, newOptions.DestinationFolder, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.AudioFormat, newOptions.AudioFormat, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.AudioProcessingScope, newOptions.AudioProcessingScope, StringComparison.Ordinal) ||
                previousOptions.AudioDownsample24To16 != newOptions.AudioDownsample24To16 ||
                previousOptions.AudioPeakNormalize != newOptions.AudioPeakNormalize ||
                Math.Abs(previousOptions.AudioPeakTargetDb - newOptions.AudioPeakTargetDb) > 0.0001 ||
                !string.Equals(previousOptions.AudioRenameScope, newOptions.AudioRenameScope, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.EncodingProfileName, newOptions.EncodingProfileName, StringComparison.Ordinal) ||
                !string.Equals(previousOptions.MkvMergePath, newOptions.MkvMergePath, StringComparison.Ordinal))
            {
                result = true;
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
            record.ResultFileName = "";
            record.ResultSize = 0;
            record.ResultAudioLangs.Clear();
            record.ResultSubLangs.Clear();
            record.AudioDelayApplied = 0;
            record.SubDelayApplied = 0;
            record.FrameSyncTimeMs = 0;
            record.FrameSyncResult = null;
            record.MergeTimeMs = 0;
            record.SpeedCorrectionTimeMs = 0;
            record.StretchFactor = "";
            record.SpeedCorrectionApplied = false;
            record.Success = false;
            record.SkipReason = "";
            record.AnalysisLog.Clear();
            record.ErrorMessage = "";
            record.SyncOffsetMs = 0;
            record.MergeCommand = "";
            record.EncodingProfileName = "";
            record.EncodingTimeMs = 0;
            record.EncodedSize = 0;
            record.EncodingCommand = "";
            record.ResultFilePath = "";
            record.SourceAudioTracks.Clear();
            record.SourceSubTracks.Clear();
            record.KeptSourceAudioIds.Clear();
            record.KeptSourceSubIds.Clear();
            record.ImportedAudioTracks.Clear();
            record.ImportedSubTracks.Clear();
            record.DisplayAudioFormat = "";
            record.DeepAnalysisMap = null;
            record.DeepAnalysisTimeMs = 0;
            record.DeepAnalysisApplied = false;
            record.AudioProcessingPreview = null;

            if (this._options.TargetLanguage.Count == 0 || record.LangFilePath.Length > 0)
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
                // In merge mode, consenti unskip solo se c'e' un file lingua associato
                if (this._options.TargetLanguage.Count == 0 || record.LangFilePath.Length > 0)
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
            lock (this._lock)
            {
                this._stopRequested = false;
            }

            this._isBusy = busy;
        }

        /// <summary>
        /// True se e' stato richiesto stop cooperativo
        /// </summary>
        private bool IsStopRequested()
        {
            bool result;
            lock (this._lock)
            {
                result = this._stopRequested;
            }

            return result;
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
            result.DeepAnalysisMap = record.DeepAnalysisMap;
            result.DeepAnalysisTimeMs = record.DeepAnalysisTimeMs;
            result.DeepAnalysisApplied = record.DeepAnalysisApplied;
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
            result.InitialSilenceMs = source.InitialSilenceMs;
            result.InitialTrimMs = source.InitialTrimMs;
            result.InsertOperations = new List<EditOperation>(source.InsertOperations);
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
            lock (this._lock)
            {
                this._progress.IsActive = true;
                this._progress.Operation = operation;
                this._progress.CurrentEpisode = "";
                this._progress.CurrentStatus = "";
                this._progress.CurrentIndex = 0;
                this._progress.Total = total;
                this._progress.Completed = 0;
                this._progress.CurrentPercent = 0;
                this._progress.GlobalPercent = 0;
                this._progress.CurrentIndeterminate = indeterminate;
                this._progress.GlobalIndeterminate = indeterminate || total <= 0;
            }

            this.OnProgressChanged?.Invoke();
        }

        /// <summary>
        /// Aggiorna lo stato avanzamento
        /// </summary>
        private void UpdateProgress(string currentEpisode, int currentIndex, int completed, int currentPercent, string currentStatus, bool currentIndeterminate, bool globalIndeterminate)
        {
            int globalPercent = 0;
            lock (this._lock)
            {
                if (this._progress.Total > 0)
                {
                    globalPercent = completed * 100 / this._progress.Total;
                }

                this._progress.CurrentEpisode = currentEpisode != null ? currentEpisode : "";
                this._progress.CurrentStatus = currentStatus != null ? currentStatus : "";
                this._progress.CurrentIndex = currentIndex;
                this._progress.Completed = completed;
                this._progress.CurrentPercent = this.ClampPercent(currentPercent);
                this._progress.GlobalPercent = this.ClampPercent(globalPercent);
                this._progress.CurrentIndeterminate = currentIndeterminate;
                this._progress.GlobalIndeterminate = globalIndeterminate || this._progress.Total <= 0;
            }

            this.OnProgressChanged?.Invoke();
        }

        /// <summary>
        /// Marca lo stato avanzamento come completato
        /// </summary>
        /// <param name="status">Stato finale</param>
        private void CompleteProgress(string status)
        {
            lock (this._lock)
            {
                this._progress.IsActive = false;
                this._progress.CurrentStatus = status != null ? status : "";
                this._progress.CurrentPercent = 100;
                this._progress.CurrentIndeterminate = false;
                this._progress.GlobalIndeterminate = false;

                if (this._progress.Total > 0)
                {
                    this._progress.Completed = this._progress.Total;
                    this._progress.GlobalPercent = 100;
                }
            }

            this.OnProgressChanged?.Invoke();
        }

        /// <summary>
        /// Aggiorna il progresso episodio usando substep strutturati del Core
        /// </summary>
        private void UpdateProgressFromPipelineStep(LogSection section, int percent, string status)
        {
            int mappedPercent = this.MapPipelineStepPercent(section, percent);

            if (!this._isBusy)
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

            if (section == LogSection.Speed)
            {
                result = 5 + clamped * 22 / 100;
            }
            else if (section == LogSection.FrameSync)
            {
                result = 28 + clamped * 60 / 100;
            }
            else if (section == LogSection.Deep)
            {
                result = 28 + clamped * 60 / 100;
            }
            else if (section == LogSection.Conv)
            {
                result = 20 + clamped * 55 / 100;
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
            lock (this._lock)
            {
                if (!this._progress.IsActive)
                {
                    return;
                }

                if (currentPercent < this._progress.CurrentPercent && this._progress.CurrentPercent < 85)
                {
                    currentPercent = this._progress.CurrentPercent;
                }

                this._progress.CurrentPercent = this.ClampPercent(currentPercent);
                this._progress.CurrentStatus = currentStatus != null ? currentStatus : "";
                this._progress.CurrentIndeterminate = false;

                if (this._progress.Total > 0)
                {
                    globalPercent = ((this._progress.Completed * 100) + this._progress.CurrentPercent) / this._progress.Total;
                    this._progress.GlobalPercent = this.ClampPercent(globalPercent);
                    this._progress.GlobalIndeterminate = false;
                }
            }

            this.OnProgressChanged?.Invoke();
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

        /// <summary>
        /// Accoda un messaggio al log e notifica i client connessi
        /// </summary>
        /// <param name="message">Messaggio log</param>
        private void AppendLog(string message)
        {
            lock (this._lock)
            {
                if (this._logText.Length > 0)
                {
                    this._logText = this._logText + "\n" + message;
                }
                else
                {
                    this._logText = message;
                }

                // Tronca log se supera il limite, mantieni la parte piu' recente
                if (this._logText.Length > LOG_MAX_LENGTH)
                {
                    int cutIndex = this._logText.IndexOf('\n', this._logText.Length - LOG_MAX_LENGTH);
                    if (cutIndex >= 0)
                    {
                        this._logText = this._logText.Substring(cutIndex + 1);
                    }
                }
            }

            this.OnLog?.Invoke(message);
        }

        #endregion

        #region Metodi pubblici di stato

        /// <summary>
        /// Accoda un messaggio al log dall'esterno (es. Dashboard)
        /// </summary>
        /// <param name="message">Messaggio log</param>
        public void Log(string message)
        {
            this.AppendLog(message);
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Indica se un'operazione e' in corso
        /// </summary>
        public bool IsBusy { get { return this._isBusy; } }

        /// <summary>
        /// Opzioni correnti
        /// </summary>
        public Options CurrentOptions { get { return this._options; } }

        /// <summary>
        /// Testo log accumulato
        /// </summary>
        public string LogText
        {
            get
            {
                lock (this._lock)
                {
                    return this._logText;
                }
            }
        }

        /// <summary>
        /// Indice riga selezionata
        /// </summary>
        public int SelectedIndex
        {
            get { return this._selectedIndex; }
            set { this._selectedIndex = value; }
        }

        /// <summary>
        /// Stato avanzamento corrente
        /// </summary>
        public ProcessingProgressState Progress
        {
            get
            {
                lock (this._lock)
                {
                    return this._progress.Clone();
                }
            }
        }

        #endregion
    }
}
