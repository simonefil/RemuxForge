using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Toolbox.Common;
using RemuxForge.Core.Toolbox.VideoToMkv;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemuxForge.Web.Services.Toolbox
{
    /// <summary>
    /// Orchestratore WebUI della conversione di file video in Matroska: elenco dei file, uscita scelta e lotto in background
    /// </summary>
    public sealed class VideoToMkvOrchestrator : ToolboxOrchestratorBase<VideoToMkvRecord>
    {
        #region Costanti

        /// <summary>
        /// Avvisi di mkvmerge riportati per esteso nel log e nel tooltip della riga, per ogni file
        /// </summary>
        private const int LISTED_WARNINGS = 5;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public VideoToMkvOrchestrator() : base("web.toolbox.videoToMkv")
        {
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Converte i file in attesa, falliti o interrotti; quelli già convertiti, anche con avvisi, restano come sono
        /// </summary>
        public override void Start()
        {
            if (this.CanStart)
                this.TryRunExclusive(this.ConvertWorker);
        }

        #endregion

        #region Metodi protected

        /// <inheritdoc />
        protected override VideoToMkvRecord CreateRecord(string inputPath, long size)
        {
            return new VideoToMkvRecord(inputPath, size);
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
            ToolboxBatchSummary summary = new ToolboxBatchSummary(this.TextPrefix);
            CancellationToken cancellation = this.OperationCancellationToken;
            string outputFolder;
            string folderError;

            this.SetBusy(true, AppText.T("web.toolbox.videoToMkv.progress"));
            try
            {
                lock (this.StateLock)
                {
                    outputFolder = this.GetBatchOutputFolder();
                    folderError = this.GetOutputFolderError();
                    foreach (VideoToMkvRecord record in this.Records)
                    {
                        inputs.Add(record.InputPath);
                        if (!record.Result.IsDone)
                            targets.Add(record);
                    }
                }

                // La cartella può sparire fra la scelta e l'avvio: il lotto non parte e nessun file cambia stato
                if (folderError.Length > 0)
                    throw new InvalidOperationException(folderError);

                // Gli input dell'intero elenco restano vietati come uscita, anche quelli già convertiti
                OutputPathAllocator allocator = new OutputPathAllocator(inputs);
                VideoToMkvService service = new VideoToMkvService(AppSettingsService.Instance.Settings.Tools.MkvMergePath);
                this.AppendLog(AppText.Count("web.toolbox.videoToMkv.batchStarted", targets.Count, outputFolder.Length > 0 ? outputFolder : AppText.T("web.toolbox.output.nextToSource")));

                for (int i = 0; i < targets.Count; i++)
                {
                    VideoToMkvRecord record = targets[i];
                    string name = Path.GetFileName(record.InputPath);
                    ToolboxItemResult result;

                    if (this.StopRequested)
                        break;

                    this.ReportProgress(i, targets.Count, name);
                    lock (this.StateLock)
                    {
                        record.Result = new ToolboxItemResult(ToolboxItemStatus.Running);
                    }
                    this.NotifyRecordsChanged();

                    // Il try sta dentro il ciclo: un file illeggibile o una cartella non scrivibile fermano quel file soltanto
                    try
                    {
                        result = service.Convert(record.InputPath, outputFolder, allocator, cancellation, percent =>
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
                        result = new ToolboxItemResult(this.IsOperationStopped(ex) ? ToolboxItemStatus.Stopped : ToolboxItemStatus.Failed);
                        result.Message = ex.Message;
                    }

                    // mkvmerge ripete lo stesso avviso per ogni pacchetto: log e tooltip ne riportano i primi e il conteggio degli altri
                    List<string> listed = result.Warnings.GetRange(0, Math.Min(result.Warnings.Count, LISTED_WARNINGS));
                    if (result.Warnings.Count > LISTED_WARNINGS)
                        listed.Add(AppText.Count("web.toolbox.videoToMkv.tooltipMoreWarnings", result.Warnings.Count - LISTED_WARNINGS));

                    string failure = result.Details.Length > 0 ? result.Details : result.Message;
                    if (result.Details.Length == 0)
                        result.Details = string.Join(Environment.NewLine, listed);
                    lock (this.StateLock)
                    {
                        record.Result = result;
                    }

                    for (int w = 0; w < result.Warnings.Count && w < LISTED_WARNINGS; w++)
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileWarning", name, result.Warnings[w]));
                    if (result.Warnings.Count > LISTED_WARNINGS)
                        this.AppendLog(AppText.Count("web.toolbox.videoToMkv.moreWarnings", result.Warnings.Count - LISTED_WARNINGS, name));

                    summary.Add(result.Status);
                    if (result.IsDone)
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileConverted", name, result.OutputPath));
                    else if (result.Status == ToolboxItemStatus.Failed)
                        this.AppendLog(AppText.F("web.toolbox.videoToMkv.fileFailed", name, failure));
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
                summary.Total = targets.Count;
                this.FinishBatch(summary);
            }
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True se c'è almeno un file da convertire e l'uscita è valida
        /// </summary>
        public override bool CanStart
        {
            get
            {
                lock (this.StateLock)
                {
                    return !this.IsBusy && this.GetOutputFolderError().Length == 0 && this.Records.Exists(r => !r.Result.IsDone);
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// File nell'elenco della conversione in Matroska
    /// </summary>
    public sealed class VideoToMkvRecord : ToolboxRecord
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="inputPath">Percorso completo del file</param>
        /// <param name="size">Dimensione in byte</param>
        public VideoToMkvRecord(string inputPath, long size) : base(inputPath, size)
        {
            this.Result = new ToolboxItemResult();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Esito della conversione: in riga il Matroska scritto o il motivo breve, nel tooltip gli errori di mkvmerge così come li ha
        /// scritti oppure i suoi avvisi
        /// </summary>
        public ToolboxItemResult Result { get; set; }

        #endregion
    }
}
