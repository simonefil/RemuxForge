using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Toolbox.Common;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Web.Services.Toolbox
{
    /// <summary>
    /// Base degli orchestratori delle card che elaborano un elenco di file in lotto: uscita accanto agli originali o in una cartella,
    /// avvio, stop e chiusura del lotto. I testi propri della card stanno sotto un prefisso comune (ready, start, stopRequested,
    /// batchCompleted, batchStopped, notifyTitle, notifyStoppedTitle, hintNextToSource, hintFolder, folderPlaceholder)
    /// </summary>
    public abstract class ToolboxOrchestratorBase : MediaOrchestratorBase
    {
        #region Variabili di classe

        /// <summary>
        /// True per scrivere nella cartella scelta, false per scrivere accanto agli originali
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
        public event Action<ToolboxBatchSummary> OnBatchCompleted;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="textPrefix">Prefisso delle chiavi dei testi della card</param>
        protected ToolboxOrchestratorBase(string textPrefix) : base(textPrefix + ".ready", true)
        {
            this.TextPrefix = textPrefix;
            this._useOutputFolder = false;
            this._outputFolder = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Imposta dove scrivere i file prodotti
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
        /// Avvia il lotto sugli elementi non ancora riusciti, se CanStart lo permette
        /// </summary>
        public abstract void Start();

        /// <summary>
        /// Interrompe l'operazione: lo strumento si ferma e quello che è in corso non lascia file
        /// </summary>
        public override void Stop()
        {
            // Il token prima del flag: chi trova lo strumento interrotto deve trovare già l'annullamento
            lock (this.StateLock)
            {
                this.OperationCancellationState?.Cancel();
            }
            this.RequestStop(AppText.T(this.TextPrefix + ".stopRequested"));
        }

        #endregion

        #region Metodi protected

        /// <summary>
        /// Motivo per cui l'uscita scelta non permette di avviare il lotto; da chiamare sotto StateLock
        /// </summary>
        /// <returns>Motivo, vuoto se l'uscita va bene</returns>
        protected string GetOutputFolderError()
        {
            return this._useOutputFolder ? OutputPathAllocator.ValidateFolder(this._outputFolder) : "";
        }

        /// <summary>
        /// Cartella in cui il lotto scrive, vuota per accanto agli originali; da chiamare sotto StateLock
        /// </summary>
        /// <returns>Cartella di uscita del lotto</returns>
        protected string GetBatchOutputFolder()
        {
            return this._useOutputFolder ? this._outputFolder : "";
        }

        /// <summary>
        /// Chiude il lotto: registra lo stop, scrive il riepilogo nel log, libera la card e avvisa la pagina
        /// </summary>
        /// <param name="summary">Riepilogo del lotto</param>
        protected void FinishBatch(ToolboxBatchSummary summary)
        {
            summary.Stopped = this.StopRequested;
            this.AppendLog(AppText.F(this.TextPrefix + (summary.Stopped ? ".batchStopped" : ".batchCompleted"), summary.Describe()));
            this.SetBusy(false, "");
            this.OnBatchCompleted?.Invoke(summary);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Prefisso delle chiavi dei testi della card
        /// </summary>
        public string TextPrefix { get; }

        /// <summary>
        /// True per scrivere nella cartella scelta
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
        /// True se c'è qualcosa da elaborare e l'uscita è valida
        /// </summary>
        public abstract bool CanStart { get; }

        #endregion
    }

    /// <summary>
    /// Base degli orchestratori a elenco di file: un file già presente non si ripete, e l'elenco cambia solo fuori dal lotto
    /// </summary>
    /// <typeparam name="TRecord">Riga dell'elenco</typeparam>
    public abstract class ToolboxOrchestratorBase<TRecord> : ToolboxOrchestratorBase where TRecord : ToolboxRecord
    {
        #region Variabili di classe

        /// <summary>
        /// File dell'elenco, nell'ordine in cui sono stati aggiunti
        /// </summary>
        private readonly List<TRecord> _records;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="textPrefix">Prefisso delle chiavi dei testi della card</param>
        protected ToolboxOrchestratorBase(string textPrefix) : base(textPrefix)
        {
            this._records = new List<TRecord>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Accoda file all'elenco; un file già presente non si ripete, ma la card può rimetterlo in coda. Controllo del lotto,
        /// accodamento e avvio del lavoro sui file accodati stanno sotto lo stesso lock: un lotto avviato da un'altra scheda non
        /// può infilarsi fra l'uno e l'altro
        /// </summary>
        /// <param name="paths">Percorsi scelti</param>
        public void AddFiles(IEnumerable<string> paths)
        {
            int added = 0;
            int requeued = 0;
            int duplicates = 0;

            lock (this.StateLock)
            {
                Dictionary<PathIdentity, TRecord> known = new Dictionary<PathIdentity, TRecord>();

                if (this.IsBusy)
                    return;

                foreach (TRecord record in this._records)
                    known[new PathIdentity(record.InputPath)] = record;

                foreach (string path in paths)
                {
                    PathIdentity identity = new PathIdentity(path);
                    TRecord existing;

                    if (known.TryGetValue(identity, out existing))
                    {
                        // Il file rimesso in coda può essere cambiato sul disco: la dimensione si rilegge con lui
                        if (this.Requeue(existing))
                        {
                            FileInfo current = new FileInfo(existing.InputPath);
                            existing.Size = current.Exists ? current.Length : 0;
                            requeued++;
                        }
                        else
                            duplicates++;
                        continue;
                    }

                    FileInfo info = new FileInfo(path);
                    known[identity] = this.CreateRecord(info.FullName, info.Exists ? info.Length : 0);
                    this._records.Add(known[identity]);
                    added++;
                }

                if (added + requeued > 0)
                    this.OnFilesAdded();
            }

            if (added > 0 || duplicates > 0 || requeued == 0)
                this.AppendLog(duplicates > 0 ? AppText.F("web.toolbox.list.filesAddedWithDuplicates", added, duplicates) : AppText.Count("web.toolbox.list.filesAdded", added));
            if (requeued > 0)
                this.AppendLog(AppText.Count("web.toolbox.list.filesRequeued", requeued));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Toglie un file dall'elenco
        /// </summary>
        /// <param name="record">File da togliere</param>
        public void Remove(TRecord record)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;

                this._records.Remove(record);
                this.OnRecordsRemoved();
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Svuota l'elenco
        /// </summary>
        public void Clear()
        {
            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;

                this._records.Clear();
                this.OnRecordsRemoved();
            }

            this.AppendLog(AppText.T("web.toolbox.list.cleared"));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Restituisce una copia dell'elenco
        /// </summary>
        /// <returns>File dell'elenco</returns>
        public List<TRecord> GetRecords()
        {
            lock (this.StateLock)
            {
                return new List<TRecord>(this._records);
            }
        }

        #endregion

        #region Metodi protected

        /// <summary>
        /// Crea la riga di un file appena accodato
        /// </summary>
        /// <param name="inputPath">Percorso completo del file</param>
        /// <param name="size">Dimensione in byte, 0 se il file non esiste</param>
        /// <returns>Riga dell'elenco</returns>
        protected abstract TRecord CreateRecord(string inputPath, long size);

        /// <summary>
        /// Rimette in coda un file già in elenco che si aggiunge di nuovo, ad esempio per rileggerlo; chiamato sotto StateLock
        /// </summary>
        /// <param name="record">File già in elenco</param>
        /// <returns>True se il file torna in coda, false se resta un doppione</returns>
        protected virtual bool Requeue(TRecord record)
        {
            return false;
        }

        /// <summary>
        /// Chiamato sotto StateLock, fuori dal lotto, dopo che almeno un file è entrato nell'elenco o è tornato in coda: il lavoro che
        /// avvia si riserva prima che un lotto possa partire
        /// </summary>
        protected virtual void OnFilesAdded()
        {
        }

        /// <summary>
        /// Chiamato sotto StateLock dopo che uno o più file sono usciti dall'elenco
        /// </summary>
        protected virtual void OnRecordsRemoved()
        {
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Elenco vivo dei file; da usare sotto StateLock
        /// </summary>
        protected List<TRecord> Records
        {
            get { return this._records; }
        }

        #endregion
    }

    /// <summary>
    /// File nell'elenco di una card a lotto
    /// </summary>
    public abstract class ToolboxRecord
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="inputPath">Percorso completo del file</param>
        /// <param name="size">Dimensione in byte</param>
        protected ToolboxRecord(string inputPath, long size)
        {
            this.InputPath = inputPath;
            this.Size = size;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percorso completo del file
        /// </summary>
        public string InputPath { get; }

        /// <summary>
        /// Dimensione in byte, riletta quando il file torna in coda
        /// </summary>
        public long Size { get; set; }

        #endregion
    }

    /// <summary>
    /// Riepilogo di un lotto: lo descrive il log e lo riassume il toast di fine lotto
    /// </summary>
    public abstract class ToolboxBatchSummary
    {
        #region Metodi pubblici

        /// <summary>
        /// Descrive gli esiti del lotto
        /// </summary>
        /// <returns>Testo del riepilogo</returns>
        public abstract string Describe();

        #endregion

        #region Proprietà

        /// <summary>
        /// Elementi del lotto, elaborati o no
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// True se il lotto è stato fermato
        /// </summary>
        public bool Stopped { get; set; }

        /// <summary>
        /// True se ogni elemento del lotto è riuscito senza avvisi
        /// </summary>
        public abstract bool AllSucceeded { get; }

        /// <summary>
        /// True se ci sono errori e nessun elemento è riuscito
        /// </summary>
        public abstract bool NothingSucceeded { get; }

        #endregion
    }
}
