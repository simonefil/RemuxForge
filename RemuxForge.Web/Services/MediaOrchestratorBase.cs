using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using System;
using System.IO;
using System.Threading;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Stato operativo condiviso dagli orchestrator della WebUI
    /// </summary>
    public abstract class MediaOrchestratorBase
    {
        #region Costanti

        /// <summary>Limite massimo del log accumulato</summary>
        private const int LOG_MAX_LENGTH = 500000;

        #endregion

        #region Variabili di classe

        private readonly object _stateLock;
        private readonly bool _timestampLog;
        private readonly ProcessingProgressState _progress;
        private volatile bool _isBusy;
        private volatile bool _stopRequested;
        private readonly string _initialLogKey;
        private bool _initialLogVisible;
        private string _logText;
        private int _selectedIndex;
        private CancellationTokenSource _operationCancellation;

        #endregion

        #region Eventi

        /// <summary>Evento emesso per ogni messaggio di log</summary>
        public event Action<string> OnLog;

        /// <summary>Evento emesso quando i record vengono aggiornati</summary>
        public event Action OnRecordsChanged;

        /// <summary>Evento emesso quando cambia lo stato avanzamento</summary>
        public event Action OnProgressChanged;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza lo stato condiviso
        /// </summary>
        /// <param name="initialLogKey">Chiave del testo iniziale del log, mostrato nella lingua corrente</param>
        /// <param name="timestampLog">True per anteporre l'orario alle nuove righe</param>
        protected MediaOrchestratorBase(string initialLogKey, bool timestampLog)
        {
            this._stateLock = new object();
            this._timestampLog = timestampLog;
            this._progress = new ProcessingProgressState();
            this._isBusy = false;
            this._stopRequested = false;
            this._initialLogKey = initialLogKey != null ? initialLogKey : "";
            this._initialLogVisible = this._initialLogKey.Length > 0;
            this._logText = "";
            this._selectedIndex = -1;
            this._operationCancellation = null;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Accoda un messaggio al log dall'esterno
        /// </summary>
        /// <param name="message">Messaggio da aggiungere</param>
        public void Log(string message)
        {
            this.AppendLog(message);
        }

        #endregion

        #region Metodi protected

        /// <summary>Primitiva opt-in del reset di Remux e Split; non cambia il reset Metadata.</summary>
        protected void ResetIdleWorkState()
        {
            lock (this._stateLock)
            {
                if (this._isBusy) return;
                this._initialLogVisible = false;
                this._logText = "";
                this._selectedIndex = -1;
                this._stopRequested = false;
                this._progress.IsActive = false;
                this._progress.Operation = "";
                this._progress.CurrentEpisode = "";
                this._progress.CurrentStatus = "";
                this._progress.CurrentIndex = 0;
                this._progress.Total = 0;
                this._progress.Completed = 0;
                this._progress.CurrentPercent = 0;
                this._progress.GlobalPercent = 0;
                this._progress.CurrentIndeterminate = false;
                this._progress.GlobalIndeterminate = false;
            }
        }

        /// <summary>Reset del solo log di presentazione, dopo una scansione candidata già committata.</summary>
        protected void ResetCommittedScanPresentation()
        {
            lock (this._stateLock)
            {
                this._initialLogVisible = false;
                this._logText = "";
            }
        }

        /// <summary>
        /// Accoda un messaggio al log e notifica i client
        /// </summary>
        /// <param name="message">Messaggio da aggiungere</param>
        protected void AppendLog(string message)
        {
            string line = this._timestampLog ? "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message : message;
            lock (this._stateLock)
            {
                if (!string.IsNullOrEmpty(this._logText))
                    this._logText += Environment.NewLine;
                this._logText += line;
                if (this._logText.Length > LOG_MAX_LENGTH)
                {
                    this._initialLogVisible = false;
                    this._logText = this._logText.Substring(this._logText.Length - LOG_MAX_LENGTH);
                }
            }

            this.OnLog?.Invoke(message);
        }

        /// <summary>
        /// Aggiorna busy e stato base del progresso. Lo stop si azzera alla riserva (TryRunExclusive) e al rilascio, non all'avvio
        /// del worker: uno stop richiesto fra la riserva e l'avvio, o fra i passi di una catena, resta valido
        /// </summary>
        /// <param name="busy">True se è in corso un'operazione</param>
        /// <param name="operation">Descrizione dell'operazione</param>
        protected void SetBusy(bool busy, string operation)
        {
            CancellationTokenSource cancellation = null;
            lock (this._stateLock)
            {
                if (!busy)
                    this._stopRequested = false;
                this._isBusy = busy;
                this._progress.IsActive = busy;
                this._progress.Operation = operation;
                this._progress.CurrentStatus = busy ? operation : "";
                this._progress.CurrentIndeterminate = busy;
                this._progress.GlobalIndeterminate = busy;
                if (!busy)
                {
                    cancellation = this._operationCancellation;
                    this._operationCancellation = null;
                }
            }
            cancellation?.Dispose();
            this.NotifyProgressChanged();
        }

        /// <summary>
        /// Riserva atomicamente l'orchestrator e avvia il worker in background: lo stop si azzera e l'annullamento nasce con la
        /// riserva, così uno stop che arriva prima che il worker parta non va perso; lo rilascia SetBusy(false). L'annullamento
        /// governa anche i processi esterni lanciati dal worker, e solo quelli: lo stop di un orchestrator non ferma le operazioni degli altri
        /// </summary>
        /// <param name="worker">Corpo dell'operazione, eseguito nel thread in background</param>
        /// <param name="reserved">Azione eseguita dal chiamante a riserva acquisita, prima dell'avvio del thread</param>
        /// <returns>True se la riserva è stata acquisita e il worker avviato, false se un'operazione è già in corso</returns>
        protected bool TryRunExclusive(ThreadStart worker, Action reserved = null)
        {
            CancellationToken cancellation;
            lock (this._stateLock)
            {
                if (this._isBusy)
                    return false;
                this._isBusy = true;
                this._stopRequested = false;
                this._operationCancellation = new CancellationTokenSource();
                cancellation = this._operationCancellation.Token;
            }

            reserved?.Invoke();
            Thread thread = new Thread(() =>
            {
                ProcessRunner.SetOperationCancellation(cancellation);
                worker();
            });
            thread.IsBackground = true;
            thread.Start();
            return true;
        }

        /// <summary>
        /// Aggiorna il progresso globale sul file corrente
        /// </summary>
        /// <param name="index">Indice zero-based nel lotto</param>
        /// <param name="total">Numero di file del lotto</param>
        /// <param name="fileName">Nome del file corrente</param>
        protected void ReportProgress(int index, int total, string fileName)
        {
            lock (this._stateLock)
            {
                this._progress.CurrentIndex = index + 1;
                this._progress.Total = total;
                this._progress.Completed = index;
                this._progress.CurrentEpisode = fileName;
                this._progress.GlobalIndeterminate = false;
                this._progress.GlobalPercent = total > 0 ? (int)(index * 100.0 / total) : 0;
                this._progress.CurrentIndeterminate = true;
                this._progress.CurrentPercent = 0;
            }
            this.NotifyProgressChanged();
        }

        /// <summary>
        /// Aggiorna il progresso di una scansione senza totale noto
        /// </summary>
        /// <param name="filePath">File appena letto, vuoto prima del primo file</param>
        /// <param name="count">Numero di file letti</param>
        protected void ReportScanProgress(string filePath, int count)
        {
            lock (this._stateLock)
            {
                // Il totale non è noto: totale e percentuale dell'operazione precedente non valgono più
                this._progress.Total = 0;
                this._progress.GlobalPercent = 0;
                this._progress.CurrentEpisode = Path.GetFileName(filePath);
                this._progress.CurrentIndex = count;
                this._progress.Completed = count;
                this._progress.GlobalIndeterminate = true;
                this._progress.CurrentIndeterminate = true;
            }
            this.NotifyProgressChanged();
        }

        /// <summary>
        /// Registra una richiesta di stop e la scrive nel log
        /// </summary>
        /// <param name="message">Messaggio di stop</param>
        protected void RequestStop(string message)
        {
            this._stopRequested = true;
            this.AppendLog(message);
        }

        /// <summary>
        /// True quando è stato richiesto lo stop cooperativo
        /// </summary>
        /// <returns>Stato corrente della richiesta</returns>
        protected bool IsStopRequested()
        {
            return this._stopRequested;
        }

        /// <summary>
        /// True quando un'eccezione del worker è l'effetto dello stop: un annullamento, il token dell'operazione annullato o lo stop
        /// richiesto. Il token può precedere il flag, e chi trova un processo ucciso in quell'istante vede solo il token
        /// </summary>
        /// <param name="exception">Eccezione intercettata dal worker</param>
        /// <returns>True se l'eccezione va trattata come interruzione</returns>
        protected bool IsOperationStopped(Exception exception)
        {
            return exception is OperationCanceledException || this._stopRequested || this.OperationCancellationToken.IsCancellationRequested;
        }

        /// <summary>Notifica che i record sono cambiati</summary>
        protected void NotifyRecordsChanged()
        {
            this.OnRecordsChanged?.Invoke();
        }

        /// <summary>Notifica che il progresso è cambiato</summary>
        protected void NotifyProgressChanged()
        {
            this.OnProgressChanged?.Invoke();
        }

        /// <summary>Lock condiviso con lo stato specifico dell'orchestrator</summary>
        protected object StateLock { get { return this._stateLock; } }

        /// <summary>Stato progresso mutabile per le fasi specializzate</summary>
        protected ProcessingProgressState ProgressState { get { return this._progress; } }

        /// <summary>Valore busy mutabile per le prenotazioni specializzate</summary>
        protected bool BusyState { get { return this._isBusy; } set { this._isBusy = value; } }

        /// <summary>Valore stop mutabile per le operazioni specializzate</summary>
        protected bool StopRequested { get { return this._stopRequested; } set { this._stopRequested = value; } }

        /// <summary>Annullamento dell'operazione riservata, mutabile sotto il lock del chiamante per le prenotazioni specializzate</summary>
        protected CancellationTokenSource OperationCancellationState { get { return this._operationCancellation; } set { this._operationCancellation = value; } }

        /// <summary>Token dell'operazione riservata, non cancellabile quando nessuna operazione è riservata</summary>
        protected CancellationToken OperationCancellationToken
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._operationCancellation != null ? this._operationCancellation.Token : CancellationToken.None;
                }
            }
        }

        /// <summary>Indice selezionato mutabile sotto il lock del chiamante</summary>
        protected int SelectedIndexState { get { return this._selectedIndex; } set { this._selectedIndex = value; } }

        #endregion

        #region Proprietà

        /// <summary>True se è in corso un'operazione</summary>
        public bool IsBusy { get { return this._isBusy; } }

        /// <summary>Testo log accumulato</summary>
        public string LogText
        {
            get
            {
                lock (this._stateLock)
                {
                    // Il testo iniziale segue la lingua corrente finché resta in testa al log
                    if (!this._initialLogVisible)
                        return this._logText;
                    if (string.IsNullOrEmpty(this._logText))
                        return AppText.T(this._initialLogKey);
                    return AppText.T(this._initialLogKey) + Environment.NewLine + this._logText;
                }
            }
        }

        /// <summary>Snapshot dello stato progresso</summary>
        public ProcessingProgressState Progress
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._progress.Clone();
                }
            }
        }

        /// <summary>Indice selezionato</summary>
        public int SelectedIndex
        {
            get
            {
                lock (this._stateLock)
                {
                    return this._selectedIndex;
                }
            }
            set
            {
                lock (this._stateLock)
                {
                    this._selectedIndex = value;
                }
            }
        }

        #endregion
    }
}
