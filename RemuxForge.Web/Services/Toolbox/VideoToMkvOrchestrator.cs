using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Toolbox.Common;
using RemuxForge.Core.Toolbox.VideoToMkv;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Web.Services.Toolbox
{
    /// <summary>
    /// Orchestratore WebUI della conversione di file video in Matroska: elenco dei file, uscita scelta e lotto in background
    /// </summary>
    public sealed class VideoToMkvOrchestrator : MediaOrchestratorBase
    {
        #region Costanti

        /// <summary>
        /// Avvisi di mkvmerge riportati per esteso nel log e nel tooltip della riga, per ogni file
        /// </summary>
        private const int LISTED_WARNINGS = 5;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// File dell'elenco, nell'ordine in cui sono stati aggiunti
        /// </summary>
        private readonly List<VideoToMkvRecord> _records;

        /// <summary>
        /// True per scrivere i Matroska nella cartella scelta, false per scriverli accanto agli originali
        /// </summary>
        private bool _useOutputFolder;

        /// <summary>
        /// Cartella di uscita scelta
        /// </summary>
        private string _outputFolder;

        #endregion

        #region Eventi

        /// <summary>
        /// Evento emesso a fine lotto con il riepilogo degli esiti
        /// </summary>
        public event Action<VideoToMkvBatchSummary> OnBatchCompleted;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public VideoToMkvOrchestrator() : base("web.toolbox.videoToMkv.ready", true)
        {
            this._records = new List<VideoToMkvRecord>();
            this._useOutputFolder = false;
            this._outputFolder = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Accoda file all'elenco; un file già presente non si ripete
        /// </summary>
        /// <param name="paths">Percorsi scelti</param>
        public void AddFiles(IEnumerable<string> paths)
        {
            int added = 0;
            int duplicates = 0;

            if (this.IsBusy)
                return;

            lock (this.StateLock)
            {
                HashSet<PathIdentity> known = new HashSet<PathIdentity>();
                foreach (VideoToMkvRecord record in this._records)
                    known.Add(new PathIdentity(record.InputPath));

                foreach (string path in paths)
                {
                    if (!known.Add(new PathIdentity(path)))
                    {
                        duplicates++;
                        continue;
                    }

                    FileInfo info = new FileInfo(path);
                    this._records.Add(new VideoToMkvRecord(info.FullName, info.Exists ? info.Length : 0));
                    added++;
                }
            }

            this.AppendLog(duplicates > 0 ? AppText.F("web.toolbox.videoToMkv.filesAddedWithDuplicates", added, duplicates) : AppText.Count("web.toolbox.videoToMkv.filesAdded", added));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Toglie un file dall'elenco
        /// </summary>
        /// <param name="record">File da togliere</param>
        public void Remove(VideoToMkvRecord record)
        {
            if (this.IsBusy)
                return;

            lock (this.StateLock)
            {
                this._records.Remove(record);
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Svuota l'elenco
        /// </summary>
        public void Clear()
        {
            if (this.IsBusy)
                return;

            lock (this.StateLock)
            {
                this._records.Clear();
            }

            this.AppendLog(AppText.T("web.toolbox.videoToMkv.cleared"));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Imposta dove scrivere i Matroska
        /// </summary>
        /// <param name="useOutputFolder">True per la cartella scelta, false per accanto agli originali</param>
        /// <param name="outputFolder">Cartella scelta, conservata anche quando si torna ad accanto agli originali</param>
        public void SetOutput(bool useOutputFolder, string outputFolder)
        {
            lock (this.StateLock)
            {
                this._useOutputFolder = useOutputFolder;
                this._outputFolder = (outputFolder ?? "").Trim();
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Converte i file in attesa, falliti o interrotti; quelli già convertiti, anche con avvisi, o saltati restano come sono
        /// </summary>
        public void Start()
        {
            if (this.CanStart)
                this.TryRunExclusive(this.ConvertWorker);
        }

        /// <summary>
        /// Interrompe la conversione: mkvmerge si ferma e il file in corso non lascia temporanei
        /// </summary>
        public override void Stop()
        {
            // Il token prima del flag: chi trova mkvmerge interrotto deve trovare già l'annullamento
            lock (this.StateLock)
            {
                this.OperationCancellationState?.Cancel();
            }
            this.RequestStop(AppText.T("web.toolbox.videoToMkv.stopRequested"));
        }

        /// <summary>
        /// Restituisce una copia dell'elenco
        /// </summary>
        /// <returns>File dell'elenco</returns>
        public List<VideoToMkvRecord> GetRecords()
        {
            lock (this.StateLock)
            {
                return new List<VideoToMkvRecord>(this._records);
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Worker del lotto: un file alla volta, un errore non ferma gli altri
        /// </summary>
        private void ConvertWorker()
        {
            List<VideoToMkvRecord> targets = new List<VideoToMkvRecord>();
            List<string> inputs = new List<string>();
            VideoToMkvBatchSummary summary = new VideoToMkvBatchSummary();
            string outputFolder;
            string folderError;

            this.SetBusy(true, AppText.T("web.toolbox.videoToMkv.progress"));
            try
            {
                lock (this.StateLock)
                {
                    outputFolder = this._useOutputFolder ? this._outputFolder : "";
                    folderError = this.GetOutputFolderError();
                    foreach (VideoToMkvRecord record in this._records)
                    {
                        inputs.Add(record.InputPath);
                        if (!record.IsDone)
                            targets.Add(record);
                    }
                }

                // La cartella può sparire fra la scelta e l'avvio: il lotto non parte e nessun file cambia stato
                if (folderError.Length > 0)
                    throw new InvalidOperationException(folderError);

                // Gli input dell'intero elenco restano vietati come uscita, anche quelli già convertiti
                OutputPathAllocator allocator = new OutputPathAllocator(inputs);
                VideoToMkvService service = new VideoToMkvService(AppSettingsService.Instance.Settings.Tools.MkvMergePath);
                this.AppendLog(AppText.Count("web.toolbox.videoToMkv.batchStarted", targets.Count, outputFolder.Length > 0 ? outputFolder : AppText.T("web.toolbox.videoToMkv.outputNextToSource")));

                for (int i = 0; i < targets.Count; i++)
                {
                    VideoToMkvRecord record = targets[i];
                    string name = Path.GetFileName(record.InputPath);
                    VideoToMkvResult result;

                    if (this.StopRequested)
                        break;

                    this.ReportProgress(i, targets.Count, name);
                    lock (this.StateLock)
                    {
                        record.Status = VideoToMkvStatus.Running;
                        record.OutputPath = "";
                        record.Message = "";
                        record.Details = "";
                    }
                    this.NotifyRecordsChanged();

                    // Il try sta dentro il ciclo: un file illeggibile o una cartella non scrivibile fermano quel file soltanto
                    try
                    {
                        result = service.Convert(record.InputPath, outputFolder, allocator, percent =>
                        {
                            lock (this.StateLock)
                            {
                                this.ProgressState.CurrentIndeterminate = false;
                                this.ProgressState.CurrentPercent = percent;
                                this.ProgressState.GlobalPercent = (i * 100 + percent) / targets.Count;
                            }
                            this.NotifyProgressChanged();
                        });
                    }
                    catch (Exception ex)
                    {
                        result = new VideoToMkvResult();
                        result.Status = this.IsOperationStopped(ex) ? VideoToMkvStatus.Stopped : VideoToMkvStatus.Failed;
                        result.Message = ex.Message;
                    }

                    // mkvmerge ripete lo stesso avviso per ogni pacchetto: log e tooltip ne riportano i primi e il conteggio degli altri
                    List<string> listed = result.Warnings.GetRange(0, Math.Min(result.Warnings.Count, LISTED_WARNINGS));
                    if (result.Warnings.Count > LISTED_WARNINGS)
                        listed.Add(AppText.Count("web.toolbox.videoToMkv.tooltipMoreWarnings", result.Warnings.Count - LISTED_WARNINGS));

                    lock (this.StateLock)
                    {
                        record.Status = result.Status;
                        record.OutputPath = result.OutputPath;
                        record.Message = result.Message;
                        record.Details = result.Details.Length > 0 ? result.Details : string.Join(Environment.NewLine, listed);
                    }

                    for (int w = 0; w < result.Warnings.Count && w < LISTED_WARNINGS; w++)
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileWarning", name, result.Warnings[w]));
                    if (result.Warnings.Count > LISTED_WARNINGS)
                        this.AppendLog(AppText.Count("web.toolbox.videoToMkv.moreWarnings", result.Warnings.Count - LISTED_WARNINGS, name));

                    summary.Add(result.Status);
                    if (result.Status == VideoToMkvStatus.Converted || result.Status == VideoToMkvStatus.ConvertedWithWarnings)
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileConverted", name, result.OutputPath));
                    else if (result.Status == VideoToMkvStatus.Skipped)
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileSkipped", name, result.Message));
                    else if (result.Status == VideoToMkvStatus.Failed)
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileFailed", name, result.Details.Length > 0 ? result.Details : result.Message));
                    else
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileStopped", name));

                    this.NotifyRecordsChanged();
                }
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.toolbox.videoToMkv.batchError", ex.Message));
            }
            finally
            {
                summary.Stopped = this.StopRequested;
                summary.Total = targets.Count;
                this.AppendLog(AppText.F(summary.Stopped ? "web.toolbox.videoToMkv.batchStopped" : "web.toolbox.videoToMkv.batchCompleted", summary.Describe()));
                this.SetBusy(false, "");
                this.OnBatchCompleted?.Invoke(summary);
            }
        }

        /// <summary>
        /// Motivo per cui l'uscita scelta non permette di convertire; da chiamare sotto StateLock
        /// </summary>
        /// <returns>Motivo, vuoto se l'uscita va bene</returns>
        private string GetOutputFolderError()
        {
            return this._useOutputFolder ? VideoToMkvService.ValidateOutputFolder(this._outputFolder) : "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True per scrivere i Matroska nella cartella scelta
        /// </summary>
        public bool UseOutputFolder
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._useOutputFolder;
                }
            }
        }

        /// <summary>
        /// Cartella di uscita scelta
        /// </summary>
        public string OutputFolder
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._outputFolder;
                }
            }
        }

        /// <summary>
        /// Motivo per cui la cartella di uscita non va bene, vuoto se va bene o se si scrive accanto agli originali
        /// </summary>
        public string OutputFolderError
        {
            get
            {
                lock (this.StateLock)
                {
                    return this.GetOutputFolderError();
                }
            }
        }

        /// <summary>
        /// True se c'è almeno un file da convertire e l'uscita è valida
        /// </summary>
        public bool CanStart
        {
            get
            {
                lock (this.StateLock)
                {
                    return !this.IsBusy && this.GetOutputFolderError().Length == 0 && this._records.Exists(r => !r.IsDone);
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// File nell'elenco della conversione in Matroska
    /// </summary>
    public sealed class VideoToMkvRecord
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="inputPath">Percorso completo del file</param>
        /// <param name="size">Dimensione in byte</param>
        public VideoToMkvRecord(string inputPath, long size)
        {
            this.InputPath = inputPath;
            this.Size = size;
            this.Status = VideoToMkvStatus.Pending;
            this.OutputPath = "";
            this.Message = "";
            this.Details = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percorso completo del file
        /// </summary>
        public string InputPath { get; }

        /// <summary>
        /// Dimensione in byte
        /// </summary>
        public long Size { get; }

        /// <summary>
        /// Stato della conversione
        /// </summary>
        public VideoToMkvStatus Status { get; set; }

        /// <summary>
        /// Matroska scritto, vuoto finché la conversione non riesce
        /// </summary>
        public string OutputPath { get; set; }

        /// <summary>
        /// Motivo del salto, dell'errore o dell'interruzione, in forma breve
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Testo completo per il tooltip: errori di mkvmerge così come li ha scritti, oppure i suoi avvisi
        /// </summary>
        public string Details { get; set; }

        /// <summary>
        /// True se il file non va rielaborato: convertito, anche con avvisi, o saltato
        /// </summary>
        public bool IsDone
        {
            get { return this.Status == VideoToMkvStatus.Converted || this.Status == VideoToMkvStatus.ConvertedWithWarnings || this.Status == VideoToMkvStatus.Skipped; }
        }

        #endregion
    }

    /// <summary>
    /// Riepilogo di un lotto di conversione in Matroska
    /// </summary>
    public sealed class VideoToMkvBatchSummary
    {
        #region Metodi pubblici

        /// <summary>
        /// Conta l'esito di un file elaborato
        /// </summary>
        /// <param name="status">Stato finale del file</param>
        public void Add(VideoToMkvStatus status)
        {
            if (status == VideoToMkvStatus.Converted)
                this.Converted++;
            else if (status == VideoToMkvStatus.ConvertedWithWarnings)
                this.ConvertedWithWarnings++;
            else if (status == VideoToMkvStatus.Skipped)
                this.Skipped++;
            else if (status == VideoToMkvStatus.Failed)
                this.Failed++;
            else
                this.Interrupted++;
        }

        /// <summary>
        /// Descrive i conteggi diversi da zero, nell'ordine convertiti, con avvisi, errori, saltati, interrotti, non elaborati
        /// </summary>
        /// <returns>Testo del riepilogo</returns>
        public string Describe()
        {
            List<string> parts = new List<string>();
            string[] keys = { "summaryConverted", "summaryWithWarnings", "summaryFailed", "summarySkipped", "summaryStopped", "summaryNotProcessed" };
            int[] counts = { this.Converted, this.ConvertedWithWarnings, this.Failed, this.Skipped, this.Interrupted, this.NotProcessed };

            for (int i = 0; i < keys.Length; i++)
            {
                if (counts[i] > 0)
                    parts.Add(AppText.Count("web.toolbox.videoToMkv." + keys[i], counts[i]));
            }

            return string.Join(", ", parts);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// File convertiti senza avvisi
        /// </summary>
        public int Converted { get; private set; }

        /// <summary>
        /// File convertiti con avvisi di mkvmerge
        /// </summary>
        public int ConvertedWithWarnings { get; private set; }

        /// <summary>
        /// File non convertiti per errore
        /// </summary>
        public int Failed { get; private set; }

        /// <summary>
        /// File saltati
        /// </summary>
        public int Skipped { get; private set; }

        /// <summary>
        /// File interrotti dallo stop durante la conversione
        /// </summary>
        public int Interrupted { get; private set; }

        /// <summary>
        /// File del lotto, elaborati o no
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// File del lotto mai avviati, per lo stop o per un errore prima del primo file
        /// </summary>
        public int NotProcessed
        {
            get { return this.Total - this.Converted - this.ConvertedWithWarnings - this.Failed - this.Skipped - this.Interrupted; }
        }

        /// <summary>
        /// True se il lotto è stato fermato
        /// </summary>
        public bool Stopped { get; set; }

        /// <summary>
        /// True se ogni file del lotto è stato convertito senza avvisi
        /// </summary>
        public bool AllSucceeded
        {
            get { return this.Converted > 0 && this.ConvertedWithWarnings + this.Failed + this.Skipped + this.Interrupted + this.NotProcessed == 0 && !this.Stopped; }
        }

        /// <summary>
        /// True se ci sono errori e nessun file è stato convertito
        /// </summary>
        public bool NothingSucceeded
        {
            get { return this.Failed > 0 && this.Converted + this.ConvertedWithWarnings == 0; }
        }

        #endregion
    }
}
