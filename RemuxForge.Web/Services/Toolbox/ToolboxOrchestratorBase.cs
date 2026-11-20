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
    /// avvio, stop, esiti e chiusura del lotto. I testi propri della card stanno sotto un prefisso comune (ready, start, stopRequested,
    /// progress, batchError, batchCompleted, batchStopped, notifyTitle, notifyStoppedTitle, folderPlaceholder, itemWarning,
    /// moreWarnings, tooltipMoreWarnings, itemFailed, itemStopped)
    /// </summary>
    public abstract class ToolboxOrchestratorBase : MediaOrchestratorBase
    {
        #region Costanti

        /// <summary>
        /// Avvisi dello strumento riportati per esteso nel log e nel tooltip, per ogni elemento
        /// </summary>
        private const int LISTED_WARNINGS = 5;

        #endregion

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
            this.StopOperation(AppText.T(this.TextPrefix + ".stopRequested"));
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
        /// Esito di un elemento che il servizio non ha restituito: interrotto dallo stop, con il testo dello stop, oppure non riuscito
        /// con il messaggio dell'eccezione
        /// </summary>
        /// <param name="result">Esito da compilare</param>
        /// <param name="exception">Eccezione uscita dal servizio</param>
        protected void ApplyException(ToolboxItemResult result, Exception exception)
        {
            bool stopped = this.IsOperationStopped(exception);

            result.Status = stopped ? ToolboxItemStatus.Stopped : ToolboxItemStatus.Failed;
            result.Message = stopped ? AppText.T("toolbox.output.stopped") : exception.Message;
        }

        /// <summary>
        /// Registra l'esito di un elemento: sul record, nel riepilogo e nel log, con gli avvisi. Senza errori dello strumento il
        /// tooltip della riga mostra gli avvisi
        /// </summary>
        /// <param name="result">Esito dell'elemento</param>
        /// <param name="label">Nome dell'elemento nel log</param>
        /// <param name="doneKey">Chiave della riga del log per l'elemento riuscito, con nome e file scritto</param>
        /// <param name="summary">Riepilogo del lotto</param>
        /// <param name="store">Scrive l'esito sul record; chiamata sotto StateLock</param>
        protected void RecordResult(ToolboxItemResult result, string label, string doneKey, ToolboxBatchSummary summary, Action store)
        {
            List<string> listed = result.Warnings.GetRange(0, Math.Min(result.Warnings.Count, LISTED_WARNINGS));
            string failure = result.Details.Length > 0 ? result.Details : result.Message;

            if (result.Warnings.Count > LISTED_WARNINGS)
                listed.Add(AppText.Count(this.TextPrefix + ".tooltipMoreWarnings", result.Warnings.Count - LISTED_WARNINGS));
            if (result.Details.Length == 0)
                result.Details = string.Join(Environment.NewLine, listed);
            lock (this.StateLock)
            {
                store();
            }

            this.LogWarnings(label, result.Warnings);
            summary.Add(result.Status);
            if (result.IsDone)
                this.AppendLog(AppText.F(doneKey, label, result.OutputPath));
            else if (result.Status == ToolboxItemStatus.Failed)
                this.AppendLog(AppText.F(this.TextPrefix + ".itemFailed", label, failure));
            else
                this.AppendLog(AppText.F(this.TextPrefix + ".itemStopped", label));
        }

        /// <summary>
        /// Scrive nel log gli avvisi dello strumento su un elemento: lo strumento può ripetere lo stesso avviso, quindi il log ne
        /// riporta i primi e il conteggio degli altri
        /// </summary>
        /// <param name="label">Nome dell'elemento nel log</param>
        /// <param name="warnings">Avvisi dello strumento</param>
        protected void LogWarnings(string label, List<string> warnings)
        {
            for (int i = 0; i < warnings.Count && i < LISTED_WARNINGS; i++)
                this.AppendLog(AppText.F(this.TextPrefix + ".itemWarning", label, warnings[i]));
            if (warnings.Count > LISTED_WARNINGS)
                this.AppendLog(AppText.Count(this.TextPrefix + ".moreWarnings", warnings.Count - LISTED_WARNINGS, label));
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

        /// <summary>
        /// Uscita prevista degli elementi ancora da elaborare, calcolata da GetPlan; null finché la card non la chiede
        /// </summary>
        private object _plan;

        /// <summary>
        /// True se dopo il calcolo del piano sono cambiati elenco, scelte, uscita o esiti
        /// </summary>
        private bool _planStale;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="textPrefix">Prefisso delle chiavi dei testi della card</param>
        protected ToolboxOrchestratorBase(string textPrefix) : base(textPrefix)
        {
            this._records = new List<TRecord>();

            // Ogni cambio di elenco, scelte, uscita o esiti passa da qui: il piano si ricalcola alla prossima richiesta
            this.OnRecordsChanged += () =>
            {
                lock (this.StateLock)
                {
                    this._planStale = true;
                }
            };
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
            this.AddPaths(this._records, paths, r => r.InputPath,
                path =>
                {
                    FileInfo info = new FileInfo(path);
                    return this.CreateRecord(info.FullName, info.Exists ? info.Length : 0);
                },
                record =>
                {
                    if (!this.Requeue(record))
                        return false;

                    // Il file rimesso in coda può essere cambiato sul disco: la dimensione si rilegge con lui
                    FileInfo current = new FileInfo(record.InputPath);
                    record.Size = current.Exists ? current.Length : 0;
                    return true;
                },
                this.OnFilesAdded);
        }

        /// <summary>
        /// Toglie un file dall'elenco
        /// </summary>
        /// <param name="record">File da togliere</param>
        public void Remove(TRecord record)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy || !this.AcceptsListChanges)
                    return;

                this._records.Remove(record);
                this.OnRecordsRemoved();
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Svuota l'elenco
        /// </summary>
        public virtual void Clear()
        {
            lock (this.StateLock)
            {
                if (this.IsBusy || !this.AcceptsListChanges)
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
        /// Worker del lotto, un file alla volta: ricontrolla la cartella, che può sparire fra la scelta e l'avvio, e senza cartella il
        /// lotto non parte e nessun elemento cambia stato. Un errore su un file ferma quel file soltanto; il file concluso conta per
        /// intero nell'avanzamento anche quando lo strumento non ha riportato il cento per cento, quello fermato no
        /// </summary>
        /// <param name="files">File del lotto, fissati alla riserva oppure raccolti da prepare</param>
        /// <param name="outputFolder">Cartella di uscita del lotto, fissata alla riserva; vuota per scrivere senza cartella scelta</param>
        /// <param name="summary">Riepilogo del lotto</param>
        /// <param name="startedMessage">Riga del log all'avvio, composta dopo prepare</param>
        /// <param name="prepare">Prepara il lotto, dal servizio agli elementi riportati da elaborare senza l'esito del lotto precedente; chiamata
        /// sotto StateLock. Un suo errore chiude il lotto come gli altri</param>
        /// <param name="markRunning">Segna in corso gli elementi di un file; chiamata sotto StateLock</param>
        /// <param name="process">Elabora un file con l'allocatore del lotto e l'avanzamento del file in percentuale, e ne registra gli esiti</param>
        protected void RunBatch(List<TRecord> files, string outputFolder, ToolboxBatchSummary summary, Func<string> startedMessage, Action prepare, Action<TRecord> markRunning,
            Action<TRecord, OutputPathAllocator, Action<int>> process)
        {
            this.SetBusy(true, AppText.T(this.TextPrefix + ".progress"));
            try
            {
                string folderError = outputFolder.Length > 0 ? OutputPathAllocator.ValidateFolder(outputFolder) : "";
                OutputPathAllocator allocator;

                if (folderError.Length > 0)
                    throw new InvalidOperationException(folderError);

                // Gli ingressi dell'intero elenco restano vietati come uscita
                lock (this.StateLock)
                {
                    allocator = new OutputPathAllocator(this.CollectInputs());
                    prepare();
                }
                this.NotifyRecordsChanged();
                this.AppendLog(startedMessage());

                for (int i = 0; i < files.Count; i++)
                {
                    TRecord file = files[i];
                    int index = i;

                    if (this.StopRequested)
                        break;

                    this.ReportProgress(i, files.Count, Path.GetFileName(file.InputPath));
                    lock (this.StateLock)
                    {
                        markRunning(file);
                    }
                    this.NotifyRecordsChanged();

                    process(file, allocator, percent =>
                    {
                        lock (this.StateLock)
                        {
                            this.ProgressState.CurrentIndeterminate = false;
                            this.ProgressState.CurrentPercent = percent;
                            this.ProgressState.GlobalPercent = (index * 100 + percent) / files.Count;
                        }
                        this.NotifyProgressChanged();
                    });

                    if (!this.StopRequested)
                    {
                        lock (this.StateLock)
                        {
                            this.ProgressState.CurrentPercent = 100;
                            this.ProgressState.GlobalPercent = (i + 1) * 100 / files.Count;
                        }
                        this.NotifyProgressChanged();
                    }
                    this.NotifyRecordsChanged();
                }
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F(this.TextPrefix + ".batchError", ex.Message));
            }
            finally
            {
                this.FinishBatch(summary);
            }
        }

        /// <summary>
        /// Accoda percorsi a un elenco della card; un percorso già presente non si ripete, ma l'elemento può tornare in coda. Controllo
        /// del lotto, accodamento e avvio del lavoro sugli elementi accodati stanno sotto lo stesso lock
        /// </summary>
        /// <typeparam name="TItem">Elemento dell'elenco</typeparam>
        /// <param name="list">Elenco a cui accodare</param>
        /// <param name="paths">Percorsi scelti</param>
        /// <param name="inputPath">Percorso dell'elemento</param>
        /// <param name="create">Crea l'elemento di un percorso nuovo</param>
        /// <param name="requeue">Rimette in coda un elemento già presente; false se resta com'è</param>
        /// <param name="onQueued">Avvia il lavoro sugli elementi accodati o rimessi in coda</param>
        protected void AddPaths<TItem>(List<TItem> list, IEnumerable<string> paths, Func<TItem, string> inputPath, Func<string, TItem> create, Func<TItem, bool> requeue,
            Action onQueued)
        {
            int added = 0;
            int requeued = 0;
            int duplicates = 0;

            lock (this.StateLock)
            {
                Dictionary<PathIdentity, TItem> known = new Dictionary<PathIdentity, TItem>();

                if (this.IsBusy || !this.AcceptsListChanges)
                    return;

                foreach (TItem item in list)
                    known[new PathIdentity(inputPath(item))] = item;

                foreach (string path in paths)
                {
                    PathIdentity identity = new PathIdentity(path);
                    TItem existing;

                    if (known.TryGetValue(identity, out existing))
                    {
                        if (requeue(existing))
                            requeued++;
                        else
                            duplicates++;
                        continue;
                    }

                    known[identity] = create(path);
                    list.Add(known[identity]);
                    added++;
                }

                if (added + requeued > 0)
                    onQueued();
            }

            if (added > 0 || duplicates > 0 || requeued == 0)
                this.AppendLog(duplicates > 0 ? AppText.F("web.toolbox.list.filesAddedWithDuplicates", added, duplicates) : AppText.Count("web.toolbox.list.filesAdded", added));
            if (requeued > 0)
                this.AppendLog(AppText.Count("web.toolbox.list.filesRequeued", requeued));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Uscita prevista di ogni elemento ancora da elaborare, con la stessa regola e lo stesso ordine del lotto sui file presenti
        /// adesso. Si ricalcola solo dopo un cambio di elenco, scelte, uscita o esiti; durante il lotto resta quella calcolata all'avvio,
        /// prima della riserva, che gli elementi in corso mostrano fino all'esito
        /// </summary>
        /// <typeparam name="TItem">Elemento del lotto: il file o la traccia</typeparam>
        /// <param name="items">Elementi del lotto nell'ordine di elaborazione; chiamata sotto StateLock</param>
        /// <param name="result">Esito dell'elemento</param>
        /// <param name="plan">Uscita dell'elemento, dati la cartella del lotto e l'allocatore dei nomi</param>
        /// <returns>Percorso previsto per elemento, vuoto se l'uscita scelta non è valida</returns>
        protected Dictionary<TItem, string> GetPlan<TItem>(Func<List<TItem>> items, Func<TItem, ToolboxItemResult> result, Func<TItem, string, OutputPathAllocator, string> plan)
        {
            lock (this.StateLock)
            {
                if (this._plan is Dictionary<TItem, string> cached && (!this._planStale || this.IsBusy))
                    return cached;

                Dictionary<TItem, string> outputs = new Dictionary<TItem, string>();
                string outputFolder = this.GetBatchOutputFolder();

                this._plan = outputs;
                this._planStale = false;
                if (this.GetOutputFolderError().Length > 0)
                    return outputs;

                OutputPathAllocator allocator = new OutputPathAllocator(this.CollectInputs());
                foreach (TItem item in items())
                {
                    if (!result(item).IsDone)
                        outputs[item] = plan(item, outputFolder, allocator);
                }

                return outputs;
            }
        }

        /// <summary>
        /// File dell'elenco mai usati come uscita; da chiamare sotto StateLock. La card con altri ingressi oltre ai file lo ridefinisce
        /// </summary>
        /// <returns>Percorsi degli ingressi</returns>
        protected virtual List<string> CollectInputs()
        {
            return this._records.ConvertAll(r => r.InputPath);
        }

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
        /// True se l'elenco accetta file aggiunti e tolti; letto sotto StateLock. La card che blocca l'elenco lo ridefinisce
        /// </summary>
        protected virtual bool AcceptsListChanges
        {
            get { return true; }
        }

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
    /// Riepilogo di un lotto, contato per stato degli elementi: lo descrive il log e lo riassume il toast di fine lotto. I testi
    /// stanno sotto il prefisso della card (summaryDone, summaryWithWarnings, summaryFailed, summaryStopped, summaryNotProcessed)
    /// </summary>
    public class ToolboxBatchSummary
    {
        #region Variabili di classe

        /// <summary>
        /// Prefisso delle chiavi dei testi della card
        /// </summary>
        private readonly string _textPrefix;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="textPrefix">Prefisso delle chiavi dei testi della card</param>
        /// <param name="total">Elementi del lotto</param>
        public ToolboxBatchSummary(string textPrefix, int total)
        {
            this._textPrefix = textPrefix;
            this.Total = total;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Conta l'esito di un elemento elaborato
        /// </summary>
        /// <param name="status">Stato finale dell'elemento</param>
        public void Add(ToolboxItemStatus status)
        {
            if (status == ToolboxItemStatus.Done)
                this.Done++;
            else if (status == ToolboxItemStatus.DoneWithWarnings)
                this.DoneWithWarnings++;
            else if (status == ToolboxItemStatus.Failed)
                this.Failed++;
            else
                this.Interrupted++;
        }

        /// <summary>
        /// Descrive i conteggi diversi da zero, nell'ordine riusciti, con avvisi, errori, interrotti, non elaborati
        /// </summary>
        /// <returns>Testo del riepilogo</returns>
        public virtual string Describe()
        {
            List<string> parts = new List<string>();
            string[] keys = { "summaryDone", "summaryWithWarnings", "summaryFailed", "summaryStopped", "summaryNotProcessed" };
            int[] counts = { this.Done, this.DoneWithWarnings, this.Failed, this.Interrupted, this.NotProcessed };

            for (int i = 0; i < keys.Length; i++)
            {
                if (counts[i] > 0)
                    parts.Add(AppText.Count(this._textPrefix + "." + keys[i], counts[i]));
            }

            return string.Join(", ", parts);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Elementi del lotto, elaborati o no
        /// </summary>
        public int Total { get; }

        /// <summary>
        /// True se il lotto è stato fermato
        /// </summary>
        public bool Stopped { get; set; }

        /// <summary>
        /// Elementi riusciti senza avvisi
        /// </summary>
        public int Done { get; private set; }

        /// <summary>
        /// Elementi riusciti con avvisi dello strumento
        /// </summary>
        public int DoneWithWarnings { get; private set; }

        /// <summary>
        /// Elementi non riusciti per errore
        /// </summary>
        public int Failed { get; private set; }

        /// <summary>
        /// Elementi interrotti dallo stop
        /// </summary>
        public int Interrupted { get; private set; }

        /// <summary>
        /// Elementi del lotto mai avviati, per lo stop o per un errore prima del primo
        /// </summary>
        public int NotProcessed
        {
            get { return this.Total - this.Done - this.DoneWithWarnings - this.Failed - this.Interrupted; }
        }

        /// <summary>
        /// True se ogni elemento del lotto è riuscito senza avvisi
        /// </summary>
        public bool AllSucceeded
        {
            get { return this.Done > 0 && this.DoneWithWarnings + this.Failed + this.Interrupted + this.NotProcessed == 0 && !this.Stopped; }
        }

        /// <summary>
        /// True se ci sono errori e nessun elemento è riuscito
        /// </summary>
        public bool NothingSucceeded
        {
            get { return this.Failed > 0 && this.Done + this.DoneWithWarnings == 0; }
        }

        #endregion
    }
}
