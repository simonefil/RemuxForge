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
        /// Avvisi di mkvmerge riportati nel log per ogni file
        /// </summary>
        private const int LOGGED_WARNINGS = 3;

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
        /// Evento emesso a fine lotto con i conteggi di file convertiti, falliti e saltati, e se il lotto è stato fermato
        /// </summary>
        public event Action<int, int, int, bool> OnBatchCompleted;

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
        /// Converte i file in attesa, falliti o interrotti; quelli già convertiti o saltati restano come sono
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
            string outputFolder;
            int converted = 0;
            int failed = 0;
            int skipped = 0;

            this.SetBusy(true, AppText.T("web.toolbox.videoToMkv.progress"));
            try
            {
                lock (this.StateLock)
                {
                    outputFolder = this._useOutputFolder ? this._outputFolder : "";
                    foreach (VideoToMkvRecord record in this._records)
                    {
                        inputs.Add(record.InputPath);
                        if (record.Status != VideoToMkvStatus.Converted && record.Status != VideoToMkvStatus.Skipped)
                            targets.Add(record);
                    }
                }

                // Gli input dell'intero elenco restano vietati come uscita, anche quelli già convertiti
                OutputPathAllocator allocator = new OutputPathAllocator(inputs);
                VideoToMkvService service = new VideoToMkvService(AppSettingsService.Instance.Settings.Tools.MkvMergePath);
                this.AppendLog(AppText.F("web.toolbox.videoToMkv.batchStarted", targets.Count, outputFolder.Length > 0 ? outputFolder : AppText.T("web.toolbox.videoToMkv.outputNextToSource")));

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

                    lock (this.StateLock)
                    {
                        record.Status = result.Status;
                        record.OutputPath = result.OutputPath;
                        // Riuscita con avvisi: la riga mostra il primo e quanti altri ce ne sono
                        if (result.Message.Length > 0 || result.Warnings.Count == 0)
                            record.Message = result.Message;
                        else
                            record.Message = result.Warnings.Count > 1 ? AppText.F("web.toolbox.videoToMkv.warningsSummary", result.Warnings[0], result.Warnings.Count - 1) : result.Warnings[0];
                    }

                    // mkvmerge ripete lo stesso avviso per ogni pacchetto: il log ne riporta i primi e il conteggio degli altri
                    for (int w = 0; w < result.Warnings.Count && w < LOGGED_WARNINGS; w++)
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileWarning", name, result.Warnings[w]));
                    if (result.Warnings.Count > LOGGED_WARNINGS)
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.moreWarnings", name, result.Warnings.Count - LOGGED_WARNINGS));

                    if (result.Status == VideoToMkvStatus.Converted)
                    {
                        converted++;
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileConverted", name, result.OutputPath));
                    }
                    else if (result.Status == VideoToMkvStatus.Skipped)
                    {
                        skipped++;
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileSkipped", name, result.Message));
                    }
                    else if (result.Status == VideoToMkvStatus.Failed)
                    {
                        failed++;
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileFailed", name, result.Message));
                    }
                    else
                    {
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileStopped", name));
                    }

                    this.NotifyRecordsChanged();
                }

                this.AppendLog(AppText.F(this.StopRequested ? "web.toolbox.videoToMkv.batchStopped" : "web.toolbox.videoToMkv.batchCompleted", converted, failed, skipped));
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.toolbox.videoToMkv.batchError", ex.Message));
            }
            finally
            {
                bool stopped = this.StopRequested;
                this.SetBusy(false, "");
                this.OnBatchCompleted?.Invoke(converted, failed, skipped, stopped);
            }
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
        /// True se c'è almeno un file da convertire e l'uscita è completa
        /// </summary>
        public bool CanStart
        {
            get
            {
                lock (this.StateLock)
                {
                    return !this.IsBusy && (!this._useOutputFolder || this._outputFolder.Length > 0) &&
                        this._records.Exists(r => r.Status != VideoToMkvStatus.Converted && r.Status != VideoToMkvStatus.Skipped);
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
        /// Avvisi di mkvmerge, oppure motivo del salto, dell'errore o dell'interruzione
        /// </summary>
        public string Message { get; set; }

        #endregion
    }
}
