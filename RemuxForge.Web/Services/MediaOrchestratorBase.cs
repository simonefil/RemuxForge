using System;
using System.IO;

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
        private string _logText;
        private int _selectedIndex;

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
        /// <param name="initialLog">Testo iniziale del log</param>
        /// <param name="timestampLog">True per anteporre l'orario alle nuove righe</param>
        protected MediaOrchestratorBase(string initialLog, bool timestampLog)
        {
            this._stateLock = new object();
            this._timestampLog = timestampLog;
            this._progress = new ProcessingProgressState();
            this._isBusy = false;
            this._stopRequested = false;
            this._logText = initialLog != null ? initialLog : "";
            this._selectedIndex = -1;
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
                    this._logText = this._logText.Substring(this._logText.Length - LOG_MAX_LENGTH);
            }

            this.OnLog?.Invoke(message);
        }

        /// <summary>
        /// Aggiorna busy e stato base del progresso
        /// </summary>
        /// <param name="busy">True se è in corso un'operazione</param>
        /// <param name="operation">Descrizione dell'operazione</param>
        protected void SetBusy(bool busy, string operation)
        {
            lock (this._stateLock)
            {
                this._stopRequested = false;
                this._isBusy = busy;
                this._progress.IsActive = busy;
                this._progress.Operation = operation;
                this._progress.CurrentStatus = busy ? operation : "";
                this._progress.CurrentIndeterminate = busy;
                this._progress.GlobalIndeterminate = busy;
            }
            this.NotifyProgressChanged();
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
        /// <param name="filePath">File appena letto</param>
        /// <param name="count">Numero di file letti</param>
        protected void ReportScanProgress(string filePath, int count)
        {
            lock (this._stateLock)
            {
                this._progress.CurrentEpisode = Path.GetFileName(filePath);
                this._progress.CurrentIndex = count;
                this._progress.Completed = count;
                this._progress.GlobalIndeterminate = true;
                this._progress.CurrentIndeterminate = true;
            }
            this.NotifyProgressChanged();
        }

        /// <summary>
        /// Aggiorna la descrizione della fase corrente
        /// </summary>
        /// <param name="status">Descrizione della fase</param>
        protected void ReportPhase(string status)
        {
            lock (this._stateLock)
            {
                this._progress.CurrentStatus = status != null ? status : "";
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
                    return this._logText;
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
