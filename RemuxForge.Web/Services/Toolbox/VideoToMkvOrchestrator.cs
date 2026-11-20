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
        /// Converte i file in attesa, falliti o interrotti; quelli già convertiti, anche con avvisi, restano come sono. File e uscita
        /// del lotto si fissano sotto lo stesso lock della riserva: dopo, elenco e uscita non cambiano più il lotto
        /// </summary>
        public override void Start()
        {
            lock (this.StateLock)
            {
                if (!this.CanStart)
                    return;

                List<VideoToMkvRecord> batch = this.Records.FindAll(r => !r.Result.IsDone);
                string outputFolder = this.GetBatchOutputFolder();
                this.TryRunExclusive(() => this.ConvertWorker(batch, outputFolder));
            }
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
        /// <param name="batch">File del lotto, fissati alla riserva</param>
        /// <param name="outputFolder">Cartella di uscita del lotto, vuota per scrivere accanto agli originali</param>
        private void ConvertWorker(List<VideoToMkvRecord> batch, string outputFolder)
        {
            VideoToMkvService service = null;
            CancellationToken cancellation = this.OperationCancellationToken;
            ToolboxBatchSummary summary = new ToolboxBatchSummary(this.TextPrefix, batch.Count);

            this.RunBatch(batch, outputFolder, summary,
                () => AppText.Count("web.toolbox.videoToMkv.batchStarted", batch.Count, outputFolder.Length > 0 ? outputFolder : AppText.T("web.toolbox.output.nextToSource")),
                () =>
                {
                    service = new VideoToMkvService(AppSettingsService.Instance.Settings.Tools.MkvMergePath);
                    batch.ForEach(r => r.Result = new ToolboxItemResult());
                },
                record => record.Result = new ToolboxItemResult(ToolboxItemStatus.Running),
                (record, allocator, onProgress) =>
                {
                    ToolboxItemResult result;

                    try
                    {
                        result = service.Convert(record.InputPath, outputFolder, allocator, cancellation, onProgress);
                    }
                    catch (Exception ex)
                    {
                        result = new ToolboxItemResult();
                        this.ApplyException(result, ex);
                    }

                    this.RecordResult(result, Path.GetFileName(record.InputPath), "web.toolbox.videoToMkv.fileConverted", summary, () => record.Result = result);
                });
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
