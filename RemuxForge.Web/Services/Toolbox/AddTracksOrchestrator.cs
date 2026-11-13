using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Toolbox.AddTracks;
using RemuxForge.Core.Toolbox.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemuxForge.Web.Services.Toolbox
{
    /// <summary>
    /// Orchestratore WebUI dell'aggiunta di tracce esterne ai video: due elenchi abbinati per posizione (il video i riceve la traccia
    /// i), proprietà della traccia aggiunta uguali per tutte le coppie, uscita in una cartella o sovrascrittura e lotto in background.
    /// L'esito di un video vale per la traccia con cui è stato ottenuto: se l'abbinamento cambia, la coppia torna da elaborare
    /// </summary>
    public sealed class AddTracksOrchestrator : ToolboxOrchestratorBase<AddTracksVideo>
    {
        #region Costanti

        /// <summary>
        /// Avvisi di mkvmerge riportati per esteso nel log e nel tooltip della riga, per ogni video
        /// </summary>
        private const int LISTED_WARNINGS = 5;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// File di tracce, nell'ordine di abbinamento
        /// </summary>
        private readonly List<AddTracksTrackFile> _tracks;

        /// <summary>
        /// Lingua della traccia aggiunta, vuota per lasciare quella del file
        /// </summary>
        private string _language;

        /// <summary>
        /// Nome della traccia aggiunta, vuoto per lasciare quello del file
        /// </summary>
        private string _name;

        /// <summary>
        /// True per segnare predefinita la traccia aggiunta
        /// </summary>
        private bool _isDefault;

        /// <summary>
        /// True per segnare forzata la traccia aggiunta
        /// </summary>
        private bool _isForced;

        /// <summary>
        /// Ritardo della traccia aggiunta in millisecondi
        /// </summary>
        private int _delayMs;

        /// <summary>
        /// Uscita prevista dei video da elaborare, calcolata al primo disegno dopo un cambio; null fino ad allora
        /// </summary>
        private Dictionary<AddTracksVideo, string> _plan;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public AddTracksOrchestrator() : base("web.toolbox.addTracks")
        {
            this._tracks = new List<AddTracksTrackFile>();
            this._language = "";
            this._name = "";

            // La sovrascrittura distrugge l'originale: si parte dalla cartella, scelta di proposito
            this.SetOutput(true, "");

            // Ogni cambio di elenchi, uscita o esiti passa da qui: le uscite previste si ricalcolano al prossimo disegno
            this.OnRecordsChanged += () =>
            {
                lock (this.StateLock)
                {
                    this._plan = null;
                }
            };
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Accoda file di tracce e ne legge il contenuto in background; un file già presente non si ripete, ma se era bloccato o
        /// non letto torna in lettura. Controllo del lotto, accodamento e riserva della lettura stanno sotto lo stesso lock
        /// </summary>
        /// <param name="paths">Percorsi scelti</param>
        public void AddTrackFiles(IEnumerable<string> paths)
        {
            int added = 0;
            int requeued = 0;
            int duplicates = 0;

            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;

                foreach (string path in paths)
                {
                    AddTracksTrackFile existing = this._tracks.Find(t => PathIdentity.IsSame(t.InputPath, path));
                    FileInfo info = new FileInfo(existing != null ? existing.InputPath : path);

                    if (existing == null)
                    {
                        this._tracks.Add(new AddTracksTrackFile(info.FullName, info.Exists ? info.Length : 0));
                        added++;
                    }
                    else if (existing.State == AddTracksTrackState.Blocked || existing.State == AddTracksTrackState.NotRead)
                    {
                        existing.SetReading(info.Exists ? info.Length : 0);
                        requeued++;
                    }
                    else
                        duplicates++;
                }

                this.RealignResults();
                if (added + requeued > 0)
                    this.TryRunExclusive(this.ReadWorker);
            }

            if (added > 0 || duplicates > 0 || requeued == 0)
                this.AppendLog(duplicates > 0 ? AppText.F("web.toolbox.list.filesAddedWithDuplicates", added, duplicates) : AppText.Count("web.toolbox.list.filesAdded", added));
            if (requeued > 0)
                this.AppendLog(AppText.Count("web.toolbox.list.filesRequeued", requeued));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Toglie un file di tracce dall'elenco
        /// </summary>
        /// <param name="track">File da togliere</param>
        public void RemoveTrack(AddTracksTrackFile track)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;

                this._tracks.Remove(track);
                this.RealignResults();
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Sposta un video in un'altra posizione dell'elenco
        /// </summary>
        /// <param name="from">Posizione attuale</param>
        /// <param name="to">Posizione di arrivo</param>
        public void MoveVideo(int from, int to)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy || from < 0 || from >= this.Records.Count || to < 0 || to >= this.Records.Count || from == to)
                    return;

                AddTracksVideo video = this.Records[from];
                this.Records.RemoveAt(from);
                this.Records.Insert(to, video);
                this.RealignResults();
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Sposta un file di tracce in un'altra posizione dell'elenco
        /// </summary>
        /// <param name="from">Posizione attuale</param>
        /// <param name="to">Posizione di arrivo</param>
        public void MoveTrack(int from, int to)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy || from < 0 || from >= this._tracks.Count || to < 0 || to >= this._tracks.Count || from == to)
                    return;

                AddTracksTrackFile track = this._tracks[from];
                this._tracks.RemoveAt(from);
                this._tracks.Insert(to, track);
                this.RealignResults();
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Imposta le proprietà della traccia aggiunta, uguali per tutte le coppie; durante il lotto non cambiano
        /// </summary>
        /// <param name="language">Lingua ISO 639-2, vuota per lasciare quella del file</param>
        /// <param name="name">Nome, vuoto per lasciare quello del file</param>
        /// <param name="isDefault">True per segnarla predefinita</param>
        /// <param name="isForced">True per segnarla forzata</param>
        /// <param name="delayMs">Ritardo in millisecondi</param>
        public void SetProperties(string language, string name, bool isDefault, bool isForced, int delayMs)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;

                this._language = language ?? "";
                this._name = (name ?? "").Trim();
                this._isDefault = isDefault;
                this._isForced = isForced;
                this._delayMs = delayMs;
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Svuota tutti e due gli elenchi
        /// </summary>
        public override void Clear()
        {
            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;

                this._tracks.Clear();
                base.Clear();
            }
        }

        /// <summary>
        /// Restituisce una copia dell'elenco dei file di tracce
        /// </summary>
        /// <returns>File di tracce nell'ordine di abbinamento</returns>
        public List<AddTracksTrackFile> GetTracks()
        {
            lock (this.StateLock)
            {
                return new List<AddTracksTrackFile>(this._tracks);
            }
        }

        /// <summary>
        /// Aggiunge le tracce ai video delle coppie non ancora riuscite. Coppie, proprietà e uscita del lotto si fissano sotto lo
        /// stesso lock della riserva: dopo, gli elenchi e la testata non cambiano più il lotto
        /// </summary>
        public override void Start()
        {
            lock (this.StateLock)
            {
                if (!this.CanStart)
                    return;

                List<KeyValuePair<AddTracksVideo, AddTracksTrackFile>> batch = new List<KeyValuePair<AddTracksVideo, AddTracksTrackFile>>();
                AddedTrackProperties properties = new AddedTrackProperties(this._language, this._name, this._isDefault, this._isForced, this._delayMs);
                string outputFolder = this.GetBatchOutputFolder();

                for (int i = 0; i < this.Records.Count; i++)
                {
                    if (!this.Records[i].IsDone)
                        batch.Add(new KeyValuePair<AddTracksVideo, AddTracksTrackFile>(this.Records[i], this._tracks[i]));
                }
                this.TryRunExclusive(() => this.AddWorker(batch, properties, outputFolder));
            }
        }

        /// <summary>
        /// Uscita che avrà ogni video ancora da elaborare: il file nella cartella, il .mkv accanto a un altro contenitore oppure il
        /// video stesso, sostituito. Stessa regola e stesso ordine del lotto, sui file presenti adesso
        /// </summary>
        /// <returns>Percorso previsto per video; si ricalcola solo dopo un cambio di elenchi, uscita o esiti</returns>
        public Dictionary<AddTracksVideo, string> PlanOutputs()
        {
            lock (this.StateLock)
            {
                if (this._plan != null)
                    return this._plan;

                string outputFolder = this.GetBatchOutputFolder();

                this._plan = new Dictionary<AddTracksVideo, string>();
                if (this.GetOutputFolderError().Length > 0)
                    return this._plan;

                OutputPathAllocator allocator = new OutputPathAllocator(this.CollectInputs());
                foreach (AddTracksVideo video in this.Records)
                {
                    if (!video.IsDone)
                        this._plan[video] = outputFolder.Length == 0 && MediaContainerFormats.IsMatroska(video.InputPath) ? video.InputPath : allocator.Reserve(AddTracksService.GetDesiredPath(video.InputPath, outputFolder));
                }

                return this._plan;
            }
        }

        #endregion

        #region Metodi protected

        /// <inheritdoc />
        protected override AddTracksVideo CreateRecord(string inputPath, long size)
        {
            return new AddTracksVideo(inputPath, size);
        }

        /// <summary>
        /// Un video nuovo sposta gli abbinamenti dei successivi solo se entra in mezzo, ma il controllo vale per tutti
        /// </summary>
        protected override void OnFilesAdded()
        {
            this.RealignResults();
        }

        /// <summary>
        /// Togliere video sposta gli abbinamenti
        /// </summary>
        protected override void OnRecordsRemoved()
        {
            this.RealignResults();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Riporta in attesa i video il cui esito è stato ottenuto con una traccia diversa da quella che ora sta nella loro
        /// posizione, o senza più una traccia accanto: la nuova coppia va elaborata. Da chiamare sotto StateLock
        /// </summary>
        private void RealignResults()
        {
            for (int i = 0; i < this.Records.Count; i++)
            {
                AddTracksVideo video = this.Records[i];
                if (video.Status != AddTracksStatus.Pending && video.Status != AddTracksStatus.Running &&
                    (i >= this._tracks.Count || !PathIdentity.IsSame(video.PairedTrackPath, this._tracks[i].InputPath)))
                    video.Reset();
            }
        }

        /// <summary>
        /// Video e file di tracce dell'elenco, mai usati come uscita; da chiamare sotto StateLock
        /// </summary>
        /// <returns>Percorsi degli ingressi</returns>
        private List<string> CollectInputs()
        {
            List<string> inputs = new List<string>();

            foreach (AddTracksVideo video in this.Records)
                inputs.Add(video.InputPath);
            foreach (AddTracksTrackFile track in this._tracks)
                inputs.Add(track.InputPath);

            return inputs;
        }

        /// <summary>
        /// Worker della lettura: legge i file di tracce accodati e quelli rimasti non letti, uno alla volta; un file che non si può
        /// aggiungere resta in elenco, bloccato, con il motivo. Lo stop interrompe anche mkvmerge, e i file non letti restano tali
        /// fino al prossimo Aggiungi tracce
        /// </summary>
        private void ReadWorker()
        {
            List<AddTracksTrackFile> targets = new List<AddTracksTrackFile>();
            CancellationToken cancellation = this.OperationCancellationToken;
            int ready = 0;
            int blocked = 0;

            this.SetBusy(true, AppText.T("web.toolbox.addTracks.reading"));
            try
            {
                lock (this.StateLock)
                {
                    targets = this._tracks.FindAll(t => t.State == AddTracksTrackState.Reading || t.State == AddTracksTrackState.NotRead);
                    foreach (AddTracksTrackFile track in targets)
                        track.SetReading(track.Size);
                }
                this.NotifyRecordsChanged();

                AddTracksService service = new AddTracksService(AppSettingsService.Instance.Settings.Tools.MkvMergePath);
                for (int i = 0; i < targets.Count; i++)
                {
                    AddTracksTrackFile track = targets[i];
                    ExternalTrack read = null;
                    string error = "";

                    if (this.StopRequested)
                        break;

                    this.ReportProgress(i, targets.Count, Path.GetFileName(track.InputPath));
                    try
                    {
                        read = service.ReadTrackFile(track.InputPath, cancellation);
                    }
                    catch (Exception ex) when (this.IsOperationStopped(ex))
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }

                    lock (this.StateLock)
                    {
                        if (read != null)
                            track.SetReady(read);
                        else
                            track.SetBlocked(error);
                        this.ProgressState.GlobalPercent = (i + 1) * 100 / targets.Count;
                    }
                    this.NotifyProgressChanged();
                    if (read != null)
                        ready++;
                    else
                    {
                        blocked++;
                        this.AppendLog(AppText.F("web.toolbox.addTracks.trackBlocked", Path.GetFileName(track.InputPath), error));
                    }
                    this.NotifyRecordsChanged();
                }
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.toolbox.addTracks.readError", ex.Message));
            }
            finally
            {
                // Un file non raggiunto o interrotto dallo stop non è bloccato: resta da leggere
                lock (this.StateLock)
                {
                    foreach (AddTracksTrackFile track in targets)
                    {
                        if (track.State == AddTracksTrackState.Reading)
                            track.SetNotRead();
                    }
                }

                if (ready == targets.Count)
                    this.AppendLog(AppText.Count("web.toolbox.addTracks.readCompleted", ready));
                else
                    this.AppendLog(AppText.F("web.toolbox.addTracks.readCompletedPartially", ready, blocked, targets.Count - ready - blocked));
                this.SetBusy(false, "");
                this.NotifyRecordsChanged();
            }
        }

        /// <summary>
        /// Worker del lotto: una coppia alla volta, un errore non ferma le altre
        /// </summary>
        /// <param name="batch">Coppie del lotto, fissate alla riserva</param>
        /// <param name="properties">Proprietà della traccia aggiunta, fissate alla riserva</param>
        /// <param name="outputFolder">Cartella di uscita del lotto, vuota per sovrascrivere</param>
        private void AddWorker(List<KeyValuePair<AddTracksVideo, AddTracksTrackFile>> batch, AddedTrackProperties properties, string outputFolder)
        {
            AddTracksBatchSummary summary = new AddTracksBatchSummary();
            CancellationToken cancellation = this.OperationCancellationToken;
            string folderError;
            List<string> inputs;

            this.SetBusy(true, AppText.T("web.toolbox.addTracks.progress"));
            try
            {
                summary.Total = batch.Count;

                // La cartella può sparire fra la scelta e l'avvio: il lotto non parte e nessuna coppia cambia stato
                folderError = outputFolder.Length > 0 ? OutputPathAllocator.ValidateFolder(outputFolder) : "";
                if (folderError.Length > 0)
                    throw new InvalidOperationException(folderError);

                // Le coppie del lotto perdono l'esito del lotto precedente: dopo uno stop quelle non raggiunte tornano in attesa
                lock (this.StateLock)
                {
                    inputs = this.CollectInputs();
                    foreach (KeyValuePair<AddTracksVideo, AddTracksTrackFile> pair in batch)
                        pair.Key.Reset();
                }
                this.NotifyRecordsChanged();

                // Video e tracce dell'intero elenco restano vietati come uscita
                OutputPathAllocator allocator = new OutputPathAllocator(inputs);
                AddTracksService service = new AddTracksService(AppSettingsService.Instance.Settings.Tools.MkvMergePath);
                this.AppendLog(AppText.Count("web.toolbox.addTracks.batchStarted", batch.Count, outputFolder.Length > 0 ? outputFolder : AppText.T("web.toolbox.addTracks.overwrite")));

                for (int i = 0; i < batch.Count; i++)
                {
                    AddTracksVideo video = batch[i].Key;
                    AddTracksTrackFile track = batch[i].Value;
                    string name = Path.GetFileName(video.InputPath);
                    string label = AppText.F("web.toolbox.addTracks.pairLabel", Path.GetFileName(track.InputPath), name);
                    AddTracksResult result;

                    if (this.StopRequested)
                        break;

                    this.ReportProgress(i, batch.Count, name);
                    lock (this.StateLock)
                    {
                        video.Status = AddTracksStatus.Running;
                    }
                    this.NotifyRecordsChanged();

                    // Il try sta dentro il ciclo: un video illeggibile o una cartella non scrivibile fermano quella coppia soltanto
                    try
                    {
                        result = service.Add(video.InputPath, track.Track, properties, outputFolder, allocator, cancellation, percent =>
                        {
                            lock (this.StateLock)
                            {
                                this.ProgressState.CurrentIndeterminate = false;
                                this.ProgressState.CurrentPercent = percent;
                                this.ProgressState.GlobalPercent = (i * 100 + percent) / batch.Count;
                            }
                            this.NotifyProgressChanged();
                        });
                    }
                    catch (Exception ex)
                    {
                        result = new AddTracksResult();
                        result.Status = this.IsOperationStopped(ex) ? AddTracksStatus.Stopped : AddTracksStatus.Failed;
                        result.Message = this.IsOperationStopped(ex) ? AppText.T("toolbox.addTracks.stopped") : ex.Message;
                    }

                    // mkvmerge può ripetere lo stesso avviso: log e tooltip ne riportano i primi e il conteggio degli altri
                    List<string> listed = result.Warnings.GetRange(0, Math.Min(result.Warnings.Count, LISTED_WARNINGS));
                    if (result.Warnings.Count > LISTED_WARNINGS)
                        listed.Add(AppText.Count("web.toolbox.addTracks.tooltipMoreWarnings", result.Warnings.Count - LISTED_WARNINGS));

                    lock (this.StateLock)
                    {
                        video.Status = result.Status;
                        video.OutputPath = result.OutputPath;
                        video.Replaced = result.Replaced;
                        video.Message = result.Message;
                        video.Details = result.Details.Length > 0 ? result.Details : string.Join(Environment.NewLine, listed);
                        video.PairedTrackPath = track.InputPath;

                        // Il video sostituito ora è il file scritto: la dimensione in riga è la sua
                        if (result.Replaced)
                            video.Size = new FileInfo(result.OutputPath).Length;
                    }

                    for (int w = 0; w < result.Warnings.Count && w < LISTED_WARNINGS; w++)
                        this.AppendLog(AppText.F("web.toolbox.addTracks.pairWarning", label, result.Warnings[w]));
                    if (result.Warnings.Count > LISTED_WARNINGS)
                        this.AppendLog(AppText.Count("web.toolbox.addTracks.moreWarnings", result.Warnings.Count - LISTED_WARNINGS, label));

                    summary.Add(result.Status);
                    if (result.Status == AddTracksStatus.Added || result.Status == AddTracksStatus.AddedWithWarnings)
                        this.AppendLog(AppText.F(result.Replaced ? "web.toolbox.addTracks.pairReplaced" : "web.toolbox.addTracks.pairAdded", label, result.OutputPath));
                    else if (result.Status == AddTracksStatus.Failed)
                        this.AppendLog(AppText.F("web.toolbox.addTracks.pairFailed", label, result.Details.Length > 0 ? result.Details : result.Message));
                    else
                        this.AppendLog(AppText.F("web.toolbox.addTracks.pairStopped", label));

                    // La coppia conclusa conta per intero anche quando mkvmerge non ha riportato il cento per cento; quella fermata no
                    if (!this.StopRequested)
                    {
                        lock (this.StateLock)
                        {
                            this.ProgressState.CurrentPercent = 100;
                            this.ProgressState.GlobalPercent = (i + 1) * 100 / batch.Count;
                        }
                        this.NotifyProgressChanged();
                    }
                    this.NotifyRecordsChanged();
                }
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.toolbox.addTracks.batchError", ex.Message));
            }
            finally
            {
                this.FinishBatch(summary);
            }
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Lingua della traccia aggiunta, vuota per lasciare quella del file
        /// </summary>
        public string Language
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._language;
                }
            }
        }

        /// <summary>
        /// Nome della traccia aggiunta, vuoto per lasciare quello del file
        /// </summary>
        public string Name
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._name;
                }
            }
        }

        /// <summary>
        /// True se la traccia aggiunta è predefinita
        /// </summary>
        public bool IsDefault
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._isDefault;
                }
            }
        }

        /// <summary>
        /// True se la traccia aggiunta è forzata
        /// </summary>
        public bool IsForced
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._isForced;
                }
            }
        }

        /// <summary>
        /// Ritardo della traccia aggiunta in millisecondi
        /// </summary>
        public int DelayMs
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._delayMs;
                }
            }
        }

        /// <summary>
        /// Perché le coppie non si possono elaborare: conteggi diversi, file di tracce in lettura, bloccati o non letti; vuoto se
        /// gli elenchi sono pronti. La cartella di uscita ha il suo messaggio sotto l'uscita
        /// </summary>
        public string PairingError
        {
            get
            {
                lock (this.StateLock)
                {
                    int blocked = this._tracks.FindAll(t => t.State == AddTracksTrackState.Blocked).Count;
                    int unread = this._tracks.FindAll(t => t.State == AddTracksTrackState.Reading || t.State == AddTracksTrackState.NotRead).Count;

                    if (this.Records.Count == 0 && this._tracks.Count == 0)
                        return AppText.T("web.toolbox.addTracks.pairingEmpty");
                    if (this.Records.Count != this._tracks.Count)
                        return AppText.T(this.Records.Count > this._tracks.Count ? "web.toolbox.addTracks.pairingMoreVideos" : "web.toolbox.addTracks.pairingMoreTracks");
                    if (unread > 0)
                        return AppText.Count("web.toolbox.addTracks.pairingUnread", unread);
                    if (blocked > 0)
                        return AppText.Count("web.toolbox.addTracks.pairingBlocked", blocked);

                    return "";
                }
            }
        }

        /// <summary>
        /// True se video e tracce sono tanti quanti, tutti i file di tracce sono pronti, l'uscita è valida e almeno una coppia è da
        /// elaborare
        /// </summary>
        public override bool CanStart
        {
            get
            {
                lock (this.StateLock)
                {
                    return !this.IsBusy && this.GetOutputFolderError().Length == 0 && this.PairingError.Length == 0 && this.Records.Exists(v => !v.IsDone);
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// Stato della lettura di un file di tracce
    /// </summary>
    public enum AddTracksTrackState
    {
        /// <summary>In lettura</summary>
        Reading,

        /// <summary>Letto, con una sola traccia aggiungibile</summary>
        Ready,

        /// <summary>Il file non si può aggiungere</summary>
        Blocked,

        /// <summary>Lettura interrotta o non raggiunta: si riprende al prossimo Aggiungi tracce</summary>
        NotRead
    }

    /// <summary>
    /// File di tracce nell'elenco di destra
    /// </summary>
    public sealed class AddTracksTrackFile
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="inputPath">Percorso completo del file</param>
        /// <param name="size">Dimensione in byte</param>
        public AddTracksTrackFile(string inputPath, long size)
        {
            this.InputPath = inputPath;
            this.SetReading(size);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Rimette il file in lettura
        /// </summary>
        /// <param name="size">Dimensione in byte, riletta dal disco</param>
        public void SetReading(long size)
        {
            this.Size = size;
            this.Reason = "";
            this.Track = null;
            this.State = AddTracksTrackState.Reading;
        }

        /// <summary>
        /// Registra la traccia letta
        /// </summary>
        /// <param name="track">Traccia del file</param>
        public void SetReady(ExternalTrack track)
        {
            this.Track = track;
            this.State = AddTracksTrackState.Ready;
        }

        /// <summary>
        /// Registra che il file non si può aggiungere
        /// </summary>
        /// <param name="reason">Motivo</param>
        public void SetBlocked(string reason)
        {
            this.Reason = reason;
            this.State = AddTracksTrackState.Blocked;
        }

        /// <summary>
        /// Registra che la lettura è stata interrotta o non è arrivata al file
        /// </summary>
        public void SetNotRead()
        {
            this.Reason = AppText.T("web.toolbox.addTracks.readStopped");
            this.State = AddTracksTrackState.NotRead;
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
        public long Size { get; private set; }

        /// <summary>
        /// Stato della lettura
        /// </summary>
        public AddTracksTrackState State { get; private set; }

        /// <summary>
        /// Traccia letta, null finché il file non è pronto
        /// </summary>
        public ExternalTrack Track { get; private set; }

        /// <summary>
        /// Perché il file è bloccato o non letto, vuoto altrimenti
        /// </summary>
        public string Reason { get; private set; }

        #endregion
    }

    /// <summary>
    /// Video nell'elenco di sinistra, con l'esito dell'ultima aggiunta
    /// </summary>
    public sealed class AddTracksVideo : ToolboxRecord
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="inputPath">Percorso completo del video</param>
        /// <param name="size">Dimensione in byte</param>
        public AddTracksVideo(string inputPath, long size) : base(inputPath, size)
        {
            this.Reset();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Riporta il video in attesa, senza esito
        /// </summary>
        public void Reset()
        {
            this.Status = AddTracksStatus.Pending;
            this.OutputPath = "";
            this.Replaced = false;
            this.Message = "";
            this.Details = "";
            this.PairedTrackPath = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Stato dell'aggiunta
        /// </summary>
        public AddTracksStatus Status { get; set; }

        /// <summary>
        /// Matroska scritto, vuoto finché l'aggiunta non riesce
        /// </summary>
        public string OutputPath { get; set; }

        /// <summary>
        /// True se il Matroska scritto ha sostituito il video
        /// </summary>
        public bool Replaced { get; set; }

        /// <summary>
        /// Motivo dell'errore o dell'interruzione, in forma breve
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Testo completo per il tooltip: errori di mkvmerge così come li ha scritti, oppure gli avvisi
        /// </summary>
        public string Details { get; set; }

        /// <summary>
        /// File di tracce con cui è stato ottenuto l'esito, vuoto in attesa
        /// </summary>
        public string PairedTrackPath { get; set; }

        /// <summary>
        /// True se la traccia è già stata aggiunta, anche con avvisi, e la coppia non va rielaborata
        /// </summary>
        public bool IsDone
        {
            get { return this.Status == AddTracksStatus.Added || this.Status == AddTracksStatus.AddedWithWarnings; }
        }

        #endregion
    }

    /// <summary>
    /// Riepilogo di un lotto di aggiunta di tracce, contato in coppie
    /// </summary>
    public sealed class AddTracksBatchSummary : ToolboxBatchSummary
    {
        #region Metodi pubblici

        /// <summary>
        /// Conta l'esito di una coppia elaborata
        /// </summary>
        /// <param name="status">Stato finale del video</param>
        public void Add(AddTracksStatus status)
        {
            if (status == AddTracksStatus.Added)
                this.Added++;
            else if (status == AddTracksStatus.AddedWithWarnings)
                this.AddedWithWarnings++;
            else if (status == AddTracksStatus.Failed)
                this.Failed++;
            else
                this.Interrupted++;
        }

        /// <summary>
        /// Descrive i conteggi diversi da zero, nell'ordine aggiunte, con avvisi, errori, interrotte, non elaborate
        /// </summary>
        /// <returns>Testo del riepilogo</returns>
        public override string Describe()
        {
            List<string> parts = new List<string>();
            string[] keys = { "summaryAdded", "summaryWithWarnings", "summaryFailed", "summaryStopped", "summaryNotProcessed" };
            int[] counts = { this.Added, this.AddedWithWarnings, this.Failed, this.Interrupted, this.NotProcessed };

            for (int i = 0; i < keys.Length; i++)
            {
                if (counts[i] > 0)
                    parts.Add(AppText.Count("web.toolbox.addTracks." + keys[i], counts[i]));
            }

            return string.Join(", ", parts);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Coppie riuscite senza avvisi
        /// </summary>
        public int Added { get; private set; }

        /// <summary>
        /// Coppie riuscite con avvisi
        /// </summary>
        public int AddedWithWarnings { get; private set; }

        /// <summary>
        /// Coppie non riuscite per errore
        /// </summary>
        public int Failed { get; private set; }

        /// <summary>
        /// Coppie interrotte dallo stop
        /// </summary>
        public int Interrupted { get; private set; }

        /// <summary>
        /// Coppie del lotto mai avviate, per lo stop o per un errore prima della prima
        /// </summary>
        public int NotProcessed
        {
            get { return this.Total - this.Added - this.AddedWithWarnings - this.Failed - this.Interrupted; }
        }

        /// <summary>
        /// True se ogni coppia del lotto è riuscita senza avvisi
        /// </summary>
        public override bool AllSucceeded
        {
            get { return this.Added > 0 && this.AddedWithWarnings + this.Failed + this.Interrupted + this.NotProcessed == 0 && !this.Stopped; }
        }

        /// <summary>
        /// True se ci sono errori e nessuna coppia è riuscita
        /// </summary>
        public override bool NothingSucceeded
        {
            get { return this.Failed > 0 && this.Added + this.AddedWithWarnings == 0; }
        }

        #endregion
    }
}
