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
        #region Variabili statiche

        /// <summary>
        /// Codifiche offerte per i sottotitoli di testo che non sono UTF-8, con il nome che mkvmerge accetta per --sub-charset e la
        /// chiave della regione
        /// </summary>
        private static readonly KeyValuePair<string, string>[] s_subtitleCharsets = new KeyValuePair<string, string>[]
        {
            new KeyValuePair<string, string>("windows-1252", "web.toolbox.addTracks.charsetWestern"),
            new KeyValuePair<string, string>("ISO-8859-1", "web.toolbox.addTracks.charsetWestern"),
            new KeyValuePair<string, string>("ISO-8859-15", "web.toolbox.addTracks.charsetWesternEuro"),
            new KeyValuePair<string, string>("windows-1250", "web.toolbox.addTracks.charsetCentral"),
            new KeyValuePair<string, string>("ISO-8859-2", "web.toolbox.addTracks.charsetCentral"),
            new KeyValuePair<string, string>("windows-1251", "web.toolbox.addTracks.charsetCyrillic"),
            new KeyValuePair<string, string>("KOI8-R", "web.toolbox.addTracks.charsetRussian"),
            new KeyValuePair<string, string>("windows-1253", "web.toolbox.addTracks.charsetGreek"),
            new KeyValuePair<string, string>("ISO-8859-7", "web.toolbox.addTracks.charsetGreek"),
            new KeyValuePair<string, string>("windows-1254", "web.toolbox.addTracks.charsetTurkish"),
            new KeyValuePair<string, string>("ISO-8859-9", "web.toolbox.addTracks.charsetTurkish"),
            new KeyValuePair<string, string>("windows-1257", "web.toolbox.addTracks.charsetBaltic"),
            new KeyValuePair<string, string>("windows-1255", "web.toolbox.addTracks.charsetHebrew"),
            new KeyValuePair<string, string>("windows-1256", "web.toolbox.addTracks.charsetArabic"),
            new KeyValuePair<string, string>("windows-874", "web.toolbox.addTracks.charsetThai"),
            new KeyValuePair<string, string>("windows-1258", "web.toolbox.addTracks.charsetVietnamese"),
            new KeyValuePair<string, string>("Shift_JIS", "web.toolbox.addTracks.charsetJapanese"),
            new KeyValuePair<string, string>("EUC-JP", "web.toolbox.addTracks.charsetJapanese"),
            new KeyValuePair<string, string>("GB18030", "web.toolbox.addTracks.charsetChineseSimplified"),
            new KeyValuePair<string, string>("Big5", "web.toolbox.addTracks.charsetChineseTraditional"),
            new KeyValuePair<string, string>("EUC-KR", "web.toolbox.addTracks.charsetKorean")
        };

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
        /// Codifica dei sottotitoli di testo che non sono UTF-8
        /// </summary>
        private string _charset;

        /// <summary>
        /// Uscita prevista dei video da elaborare, calcolata al primo disegno dopo un cambio; null fino ad allora
        /// </summary>
        private Dictionary<AddTracksVideo, string> _plan;

        /// <summary>
        /// Video sostituiti in questo processo, con i file di tracce già aggiunti e l'ultimo esito: un video sostituito contiene già
        /// la traccia, e riabbinarlo allo stesso file la aggiungerebbe una seconda volta
        /// </summary>
        private readonly List<AddTracksReplacement> _replacements;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public AddTracksOrchestrator() : base("web.toolbox.addTracks")
        {
            this._tracks = new List<AddTracksTrackFile>();
            this._replacements = new List<AddTracksReplacement>();
            this._language = "";
            this._name = "";
            this._charset = AddTracksService.DEFAULT_SUBTITLE_CHARSET;

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

                    if (existing == null)
                    {
                        this._tracks.Add(new AddTracksTrackFile(Path.GetFullPath(path)));
                        added++;
                    }
                    else if (existing.State == AddTracksTrackState.Blocked || existing.State == AddTracksTrackState.NotRead)
                    {
                        existing.SetReading();
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
        /// Sposta un video di qualche posizione, contate dalla posizione che ha al momento dello spostamento
        /// </summary>
        /// <param name="video">Video da spostare</param>
        /// <param name="delta">Posizioni, negative verso l'alto</param>
        /// <returns>Nuova posizione, -1 se il video non si è spostato</returns>
        public int MoveVideo(AddTracksVideo video, int delta)
        {
            return this.Move(this.Records, video, l => l.IndexOf(video) + delta);
        }

        /// <summary>
        /// Porta un video al posto di un altro, come col trascinamento
        /// </summary>
        /// <param name="video">Video da spostare</param>
        /// <param name="target">Video di cui prende il posto</param>
        /// <returns>Nuova posizione, -1 se il video non si è spostato</returns>
        public int MoveVideo(AddTracksVideo video, AddTracksVideo target)
        {
            return this.Move(this.Records, video, l => l.IndexOf(target));
        }

        /// <summary>
        /// Sposta un file di tracce di qualche posizione, contate dalla posizione che ha al momento dello spostamento
        /// </summary>
        /// <param name="track">File da spostare</param>
        /// <param name="delta">Posizioni, negative verso l'alto</param>
        /// <returns>Nuova posizione, -1 se il file non si è spostato</returns>
        public int MoveTrack(AddTracksTrackFile track, int delta)
        {
            return this.Move(this._tracks, track, l => l.IndexOf(track) + delta);
        }

        /// <summary>
        /// Porta un file di tracce al posto di un altro, come col trascinamento
        /// </summary>
        /// <param name="track">File da spostare</param>
        /// <param name="target">File di cui prende il posto</param>
        /// <returns>Nuova posizione, -1 se il file non si è spostato</returns>
        public int MoveTrack(AddTracksTrackFile track, AddTracksTrackFile target)
        {
            return this.Move(this._tracks, track, l => l.IndexOf(target));
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
                // Il nome arriva a ogni tasto: gli spazi in coda si tolgono all'avvio del lotto
                this._name = name ?? "";
                this._isDefault = isDefault;
                this._isForced = isForced;
                this._delayMs = delayMs;
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Imposta la codifica dei sottotitoli di testo che non sono UTF-8: con UTF-8 quei file restano bloccati, con un'altra si
        /// sbloccano; durante il lotto non cambia
        /// </summary>
        /// <param name="charset">Codifica come la nomina mkvmerge</param>
        public void SetCharset(string charset)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;

                this._charset = string.IsNullOrEmpty(charset) ? AddTracksService.DEFAULT_SUBTITLE_CHARSET : charset;
                foreach (AddTracksTrackFile track in this._tracks)
                    track.ApplyCharset(this._charset);
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
            }

            base.Clear();
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

                Dictionary<AddTracksVideo, AddTracksTrackFile> pairs = new Dictionary<AddTracksVideo, AddTracksTrackFile>();
                List<AddTracksVideo> batch = new List<AddTracksVideo>();
                AddedTrackProperties properties = new AddedTrackProperties(this._language, this._name.Trim(), this._isDefault, this._isForced, this._delayMs, this._charset);
                string outputFolder = this.GetBatchOutputFolder();

                for (int i = 0; i < this.Records.Count; i++)
                {
                    if (!this.Records[i].Result.IsDone)
                    {
                        batch.Add(this.Records[i]);
                        pairs[this.Records[i]] = this._tracks[i];
                    }
                }
                this.TryRunExclusive(() => this.AddWorker(batch, pairs, properties, outputFolder));
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
                    if (!video.Result.IsDone)
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

        /// <summary>
        /// Video e file di tracce dell'elenco, mai usati come uscita; da chiamare sotto StateLock
        /// </summary>
        /// <returns>Percorsi degli ingressi</returns>
        protected override List<string> CollectInputs()
        {
            List<string> inputs = base.CollectInputs();

            foreach (AddTracksTrackFile track in this._tracks)
                inputs.Add(track.InputPath);

            return inputs;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Riporta in attesa i video il cui esito è stato ottenuto con una traccia diversa da quella che ora sta nella loro
        /// posizione, o senza più una traccia accanto: la nuova coppia va elaborata. Un video sostituito in questo processo che torna
        /// accanto a un file già aggiunto riprende l'ultimo esito se è la stessa coppia, altrimenti resta bloccato: la traccia c'è
        /// già. Da chiamare sotto StateLock
        /// </summary>
        private void RealignResults()
        {
            for (int i = 0; i < this.Records.Count; i++)
            {
                AddTracksVideo video = this.Records[i];
                string trackPath = i < this._tracks.Count ? this._tracks[i].InputPath : "";
                AddTracksReplacement replacement = this._replacements.Find(r => PathIdentity.IsSame(r.VideoPath, video.InputPath));

                if (video.Result.Status == ToolboxItemStatus.Running)
                    continue;

                if (replacement != null && trackPath.Length > 0 && replacement.TrackPaths.Exists(t => PathIdentity.IsSame(t, trackPath)))
                {
                    if (PathIdentity.IsSame(replacement.LastTrackPath, trackPath))
                        replacement.Restore(video);
                    else
                        video.Block(AppText.T("web.toolbox.addTracks.alreadyAdded"), trackPath);
                }
                else if ((video.Result.Status != ToolboxItemStatus.Pending || video.BlockedReason.Length > 0) && !PathIdentity.IsSame(video.PairedTrackPath, trackPath))
                    video.Reset();
            }
        }

        /// <summary>
        /// Sposta un elemento di un elenco nella posizione indicata, calcolata sotto il lock
        /// </summary>
        /// <param name="list">Elenco dei video o dei file di tracce</param>
        /// <param name="item">Elemento da spostare</param>
        /// <param name="target">Posizione di arrivo, dall'elenco attuale</param>
        /// <returns>Nuova posizione, -1 se l'elemento non si è spostato</returns>
        private int Move<T>(List<T> list, T item, Func<List<T>, int> target)
        {
            int to;

            lock (this.StateLock)
            {
                int from = list.IndexOf(item);

                to = target(list);
                if (this.IsBusy || from < 0 || to < 0 || to >= list.Count || from == to)
                    return -1;

                list.RemoveAt(from);
                list.Insert(to, item);
                this.RealignResults();
            }

            this.NotifyRecordsChanged();
            return to;
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
            bool failed = false;

            this.SetBusy(true, AppText.T("web.toolbox.addTracks.reading"));
            try
            {
                lock (this.StateLock)
                {
                    targets = this._tracks.FindAll(t => t.State == AddTracksTrackState.Reading || t.State == AddTracksTrackState.NotRead);
                    foreach (AddTracksTrackFile track in targets)
                        track.SetReading();
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
                        {
                            track.SetReady(read);
                            track.ApplyCharset(this._charset);
                        }
                        else
                            track.SetBlocked(error);
                        this.ProgressState.GlobalPercent = (i + 1) * 100 / targets.Count;
                    }
                    this.NotifyProgressChanged();
                    if (track.State == AddTracksTrackState.Ready)
                        ready++;
                    else
                    {
                        blocked++;
                        this.AppendLog(AppText.F("web.toolbox.addTracks.trackBlocked", Path.GetFileName(track.InputPath), track.Reason));
                    }
                    this.NotifyRecordsChanged();
                }
            }
            catch (Exception ex)
            {
                failed = true;
                this.AppendLog(AppText.F("web.toolbox.addTracks.readError", ex.Message));
            }
            finally
            {
                // Un file non raggiunto non è bloccato: resta da leggere, e il motivo dice se l'ha fermato lo stop o un errore
                lock (this.StateLock)
                {
                    foreach (AddTracksTrackFile track in targets)
                    {
                        if (track.State == AddTracksTrackState.Reading)
                            track.SetNotRead(AppText.T(failed ? "web.toolbox.addTracks.readFailed" : "web.toolbox.addTracks.readStopped"));
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
        /// Worker del lotto: una coppia alla volta, un errore non ferma le altre. Un video sostituito contiene ora la traccia: si
        /// ricorda, per non aggiungerla una seconda volta
        /// </summary>
        /// <param name="batch">Video del lotto, fissati alla riserva</param>
        /// <param name="pairs">File di tracce di ogni video del lotto, fissati alla riserva</param>
        /// <param name="properties">Proprietà della traccia aggiunta, fissate alla riserva</param>
        /// <param name="outputFolder">Cartella di uscita del lotto, vuota per sovrascrivere</param>
        private void AddWorker(List<AddTracksVideo> batch, Dictionary<AddTracksVideo, AddTracksTrackFile> pairs, AddedTrackProperties properties, string outputFolder)
        {
            AddTracksService service = new AddTracksService(AppSettingsService.Instance.Settings.Tools.MkvMergePath);
            CancellationToken cancellation = this.OperationCancellationToken;
            ToolboxBatchSummary summary = new ToolboxBatchSummary(this.TextPrefix, batch.Count);

            this.RunBatch(batch, outputFolder, summary,
                AppText.Count("web.toolbox.addTracks.batchStarted", batch.Count, outputFolder.Length > 0 ? outputFolder : AppText.T("web.toolbox.addTracks.overwrite")),
                () => batch.ForEach(v => v.Reset()),
                video => video.Result = new ToolboxItemResult(ToolboxItemStatus.Running),
                (video, allocator, onProgress) =>
                {
                    AddTracksTrackFile track = pairs[video];
                    AddTracksResult result;

                    try
                    {
                        result = service.Add(video.InputPath, track.Track, properties, outputFolder, allocator, cancellation, onProgress);
                    }
                    catch (Exception ex)
                    {
                        result = new AddTracksResult();
                        this.ApplyException(result, ex);
                    }

                    this.RecordResult(result, AppText.F("web.toolbox.addTracks.pairLabel", Path.GetFileName(track.InputPath), Path.GetFileName(video.InputPath)),
                        result.Replaced ? "web.toolbox.addTracks.pairReplaced" : "web.toolbox.addTracks.pairAdded", summary, () =>
                        {
                            video.Result = result;
                            video.Replaced = result.Replaced;
                            video.PairedTrackPath = track.InputPath;
                            if (result.Replaced)
                            {
                                AddTracksReplacement replacement = this._replacements.Find(r => PathIdentity.IsSame(r.VideoPath, video.InputPath));
                                if (replacement == null)
                                {
                                    replacement = new AddTracksReplacement(video.InputPath);
                                    this._replacements.Add(replacement);
                                }
                                replacement.Record(video);
                            }
                        });
                });
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
        /// Codifica dei sottotitoli di testo che non sono UTF-8
        /// </summary>
        public string Charset
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._charset;
                }
            }
        }

        /// <summary>
        /// Codifiche della tendina, nella lingua dell'interfaccia: il nome per mkvmerge e l'etichetta con regione e nome
        /// </summary>
        public static List<KeyValuePair<string, string>> CharsetChoices
        {
            get
            {
                List<KeyValuePair<string, string>> choices = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>(AddTracksService.DEFAULT_SUBTITLE_CHARSET, AddTracksService.DEFAULT_SUBTITLE_CHARSET)
                };

                foreach (KeyValuePair<string, string> charset in s_subtitleCharsets)
                    choices.Add(new KeyValuePair<string, string>(charset.Key, AppText.T(charset.Value) + " (" + charset.Key + ")"));

                return choices;
            }
        }

        /// <summary>
        /// Perché le coppie non si possono elaborare: conteggi diversi, file di tracce in lettura, bloccati o non letti; vuoto se
        /// gli elenchi sono pronti o tutti e due vuoti. La cartella di uscita ha il suo messaggio sotto l'uscita
        /// </summary>
        public string PairingError
        {
            get
            {
                lock (this.StateLock)
                {
                    int blocked = this._tracks.FindAll(t => t.State == AddTracksTrackState.Blocked).Count;
                    int wrongCharset = this._tracks.FindAll(t => t.State == AddTracksTrackState.Blocked && t.Track != null && t.Track.NeedsCharset).Count;
                    int unread = this._tracks.FindAll(t => t.State == AddTracksTrackState.Reading || t.State == AddTracksTrackState.NotRead).Count;

                    if (this.Records.Count != this._tracks.Count)
                        return AppText.T(this.Records.Count > this._tracks.Count ? "web.toolbox.addTracks.pairingMoreVideos" : "web.toolbox.addTracks.pairingMoreTracks");
                    if (unread > 0)
                        return AppText.Count("web.toolbox.addTracks.pairingUnread", unread);
                    if (blocked > wrongCharset)
                        return AppText.Count("web.toolbox.addTracks.pairingBlocked", blocked - wrongCharset);
                    if (wrongCharset > 0)
                        return AppText.Count("web.toolbox.addTracks.pairingCharset", wrongCharset);
                    blocked = this.Records.FindAll(v => v.BlockedReason.Length > 0).Count;
                    if (blocked > 0)
                        return AppText.Count("web.toolbox.addTracks.pairingAlreadyAdded", blocked);

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
                    return !this.IsBusy && this.GetOutputFolderError().Length == 0 && this.PairingError.Length == 0 && this.Records.Exists(v => !v.Result.IsDone);
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
        public AddTracksTrackFile(string inputPath)
        {
            this.InputPath = inputPath;
            this.SetReading();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Rimette il file in lettura
        /// </summary>
        public void SetReading()
        {
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
        /// Blocca o sblocca un sottotitolo di testo che non è UTF-8 secondo la codifica scelta: con UTF-8 mkvmerge ne scarterebbe
        /// le battute. Gli altri file non cambiano
        /// </summary>
        /// <param name="charset">Codifica scelta nella testata</param>
        public void ApplyCharset(string charset)
        {
            if (this.Track == null || !this.Track.NeedsCharset)
                return;

            bool blocked = charset == AddTracksService.DEFAULT_SUBTITLE_CHARSET;
            this.Reason = blocked ? AppText.F("toolbox.addTracks.notUtf8", Path.GetFileName(this.InputPath)) : "";
            this.State = blocked ? AddTracksTrackState.Blocked : AddTracksTrackState.Ready;
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
        /// <param name="reason">Perché: lo stop o un errore della lettura</param>
        public void SetNotRead(string reason)
        {
            this.Reason = reason;
            this.State = AddTracksTrackState.NotRead;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percorso completo del file
        /// </summary>
        public string InputPath { get; }

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
            this.Result = new ToolboxItemResult();
            this.Replaced = false;
            this.PairedTrackPath = "";
            this.BlockedReason = "";
        }

        /// <summary>
        /// Blocca il video accanto a un file di tracce che non gli si può aggiungere
        /// </summary>
        /// <param name="reason">Motivo</param>
        /// <param name="trackPath">File di tracce accanto al video</param>
        public void Block(string reason, string trackPath)
        {
            this.Reset();
            this.BlockedReason = reason;
            this.PairedTrackPath = trackPath;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Esito dell'ultima aggiunta: in riga il Matroska scritto o il motivo breve, nel tooltip gli errori di mkvmerge così come li
        /// ha scritti oppure gli avvisi
        /// </summary>
        public ToolboxItemResult Result { get; set; }

        /// <summary>
        /// True se il Matroska scritto ha sostituito il video
        /// </summary>
        public bool Replaced { get; set; }

        /// <summary>
        /// File di tracce con cui è stato ottenuto l'esito, o accanto a cui il video è bloccato; vuoto in attesa
        /// </summary>
        public string PairedTrackPath { get; set; }

        /// <summary>
        /// Perché la coppia non si può elaborare, vuoto se si può
        /// </summary>
        public string BlockedReason { get; private set; }

        #endregion
    }

    /// <summary>
    /// Video sostituito in questo processo: i file di tracce che contiene già e l'ultimo esito, da riprendere quando la stessa coppia
    /// torna in elenco
    /// </summary>
    public sealed class AddTracksReplacement
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="videoPath">Percorso completo del video sostituito</param>
        public AddTracksReplacement(string videoPath)
        {
            this.VideoPath = videoPath;
            this.TrackPaths = new List<string>();
            this.LastTrackPath = "";
            this.LastDetails = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Registra l'esito di una sostituzione riuscita
        /// </summary>
        /// <param name="video">Video con l'esito appena scritto</param>
        public void Record(AddTracksVideo video)
        {
            if (!this.TrackPaths.Exists(t => PathIdentity.IsSame(t, video.PairedTrackPath)))
                this.TrackPaths.Add(video.PairedTrackPath);
            this.LastTrackPath = video.PairedTrackPath;
            this.LastStatus = video.Result.Status;
            this.LastDetails = video.Result.Details;
        }

        /// <summary>
        /// Riporta sul video l'ultimo esito
        /// </summary>
        /// <param name="video">Video, anche riaggiunto, dello stesso percorso</param>
        public void Restore(AddTracksVideo video)
        {
            video.Reset();
            video.Result = new ToolboxItemResult(this.LastStatus)
            {
                OutputPath = video.InputPath,
                Details = this.LastDetails
            };
            video.Replaced = true;
            video.PairedTrackPath = this.LastTrackPath;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percorso completo del video sostituito
        /// </summary>
        public string VideoPath { get; }

        /// <summary>
        /// File di tracce già aggiunti al video
        /// </summary>
        public List<string> TrackPaths { get; }

        /// <summary>
        /// File di tracce dell'ultima sostituzione
        /// </summary>
        public string LastTrackPath { get; private set; }

        /// <summary>
        /// Stato dell'ultima sostituzione
        /// </summary>
        public ToolboxItemStatus LastStatus { get; private set; }

        /// <summary>
        /// Avvisi dell'ultima sostituzione, per il tooltip
        /// </summary>
        public string LastDetails { get; private set; }

        #endregion
    }
}
