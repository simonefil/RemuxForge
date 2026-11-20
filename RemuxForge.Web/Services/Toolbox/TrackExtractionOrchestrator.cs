using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Toolbox.Common;
using RemuxForge.Core.Toolbox.TrackExtraction;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemuxForge.Web.Services.Toolbox
{
    /// <summary>
    /// Vista con cui si scelgono le tracce da estrarre
    /// </summary>
    public enum TrackExtractionView
    {
        /// <summary>Tracce spuntate una per una, file per file</summary>
        ByFile,

        /// <summary>Tracce scelte per tipo, lingua e formato su tutti i file</summary>
        ByType
    }

    /// <summary>
    /// Orchestratore WebUI dell'estrazione di tracce: elenco dei video con le loro tracce, scelta per file o per tipo, uscita e
    /// lotto in background. Le due viste conservano ciascuna la sua scelta; si estrae quella della vista aperta
    /// </summary>
    public sealed class TrackExtractionOrchestrator : ToolboxOrchestratorBase<TrackExtractionFile>
    {
        #region Variabili di classe

        /// <summary>
        /// Tipi scelti nella vista per tipo; vuoto = tutti
        /// </summary>
        private readonly List<TrackExtractionKind> _kinds;

        /// <summary>
        /// Lingue scelte nella vista per tipo; vuoto = tutte
        /// </summary>
        private readonly List<string> _languages;

        /// <summary>
        /// Formati scelti nella vista per tipo; vuoto = tutti
        /// </summary>
        private readonly List<string> _formats;

        /// <summary>
        /// Vista aperta, la cui scelta si estrae
        /// </summary>
        private TrackExtractionView _view;


        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TrackExtractionOrchestrator() : base("web.toolbox.trackExtraction")
        {
            this._kinds = new List<TrackExtractionKind>();
            this._languages = new List<string>();
            this._formats = new List<string>();
            this._view = TrackExtractionView.ByFile;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Apre una vista; la scelta delle due viste resta com'era. Durante il lotto la vista non cambia
        /// </summary>
        /// <param name="view">Vista da aprire</param>
        public void SetView(TrackExtractionView view)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;
                this._view = view;
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Spunta o toglie una traccia nella vista per file; le tracce che non si estraggono restano senza spunta
        /// </summary>
        /// <param name="track">Traccia</param>
        /// <param name="isChecked">True per spuntarla</param>
        public void SetChecked(TrackExtractionTrack track, bool isChecked)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy || !track.Track.IsExtractable)
                    return;
                track.IsChecked = isChecked;
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Spunta o toglie tutte le tracce estraibili di un video nella vista per file
        /// </summary>
        /// <param name="file">Video</param>
        /// <param name="isChecked">True per spuntarle</param>
        public void SetFileChecked(TrackExtractionFile file, bool isChecked)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;
                foreach (TrackExtractionTrack track in file.Tracks)
                    track.IsChecked = isChecked && track.Track.IsExtractable;
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Imposta i filtri della vista per tipo; lingue e formati che con i tipi scelti non esistono più si tolgono. Durante il lotto
        /// i filtri non cambiano
        /// </summary>
        /// <param name="kinds">Tipi scelti, vuoto = tutti</param>
        /// <param name="languages">Lingue scelte, vuoto = tutte</param>
        /// <param name="formats">Formati scelti, vuoto = tutti</param>
        public void SetFilters(IEnumerable<TrackExtractionKind> kinds, IEnumerable<string> languages, IEnumerable<string> formats)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;
                this._kinds.Clear();
                this._kinds.AddRange(kinds);
                this._languages.Clear();
                this._languages.AddRange(languages);
                this._formats.Clear();
                this._formats.AddRange(formats);
                this.PruneFilters();
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Estrae le tracce scelte nella vista aperta che non sono già state estratte. Tracce e uscita del lotto si fissano sotto lo
        /// stesso lock della riserva: dopo, scelte e uscita non cambiano più il lotto
        /// </summary>
        public override void Start()
        {
            lock (this.StateLock)
            {
                if (!this.CanStart)
                    return;

                List<TrackExtractionTrack> batch = this.SelectTracks().FindAll(t => !t.Result.IsDone);
                string outputFolder = this.GetBatchOutputFolder();
                this.TryRunExclusive(() => this.ExtractWorker(batch, outputFolder));
            }
        }

        /// <summary>
        /// Tracce scelte nella vista aperta, nell'ordine dei video e delle tracce, già estratte comprese
        /// </summary>
        /// <returns>Tracce scelte</returns>
        public List<TrackExtractionTrack> GetSelection()
        {
            lock (this.StateLock)
            {
                return this.SelectTracks();
            }
        }

        /// <summary>
        /// Nome che avrà ogni traccia scelta e non ancora estratta, calcolato con la stessa regola e lo stesso ordine dell'estrazione
        /// sui file presenti adesso
        /// </summary>
        /// <returns>Percorso previsto per traccia; durante il lotto resta quello calcolato prima dell'avvio</returns>
        public Dictionary<TrackExtractionTrack, string> PlanOutputs()
        {
            return this.GetPlan(this.SelectTracks, t => t.Result,
                (track, outputFolder, allocator) => allocator.Reserve(TrackExtractionService.GetDesiredPath(track.File.InputPath, outputFolder, track.Track)));
        }

        #endregion

        #region Metodi protected

        /// <inheritdoc />
        protected override TrackExtractionFile CreateRecord(string inputPath, long size)
        {
            return new TrackExtractionFile(inputPath, size);
        }

        /// <summary>
        /// Rimette in coda per la lettura un video illeggibile o non letto che si aggiunge di nuovo
        /// </summary>
        /// <param name="record">Video già in elenco</param>
        /// <returns>True se il video torna in lettura</returns>
        protected override bool Requeue(TrackExtractionFile record)
        {
            if (record.State != TrackExtractionFileState.Unreadable && record.State != TrackExtractionFileState.NotRead)
                return false;

            record.SetReading();
            return true;
        }

        /// <summary>
        /// Legge in background le tracce dei video accodati; la riserva avviene sotto il lock dell'accodamento e fuori dal lotto,
        /// quindi riesce sempre
        /// </summary>
        protected override void OnFilesAdded()
        {
            this.TryRunExclusive(this.ReadWorker);
        }

        /// <summary>
        /// Toglie dai filtri lingue e formati rimasti senza tracce
        /// </summary>
        protected override void OnRecordsRemoved()
        {
            this.PruneFilters();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Worker della lettura: legge le tracce dei video accodati e di quelli rimasti non letti, uno alla volta; un video
        /// illeggibile resta in elenco con il motivo. Lo stop interrompe anche lo strumento al lavoro, e i video non letti restano
        /// tali fino al prossimo Aggiungi file
        /// </summary>
        private void ReadWorker()
        {
            List<TrackExtractionFile> targets = new List<TrackExtractionFile>();
            CancellationToken cancellation = this.OperationCancellationToken;
            int read = 0;
            int unreadable = 0;

            this.SetBusy(true, AppText.T("web.toolbox.trackExtraction.reading"));
            try
            {
                lock (this.StateLock)
                {
                    foreach (TrackExtractionFile file in this.Records)
                    {
                        if (file.State == TrackExtractionFileState.Reading || file.State == TrackExtractionFileState.NotRead)
                        {
                            file.SetReading();
                            targets.Add(file);
                        }
                    }
                }
                this.NotifyRecordsChanged();

                TrackExtractionService service = this.CreateService();
                for (int i = 0; i < targets.Count; i++)
                {
                    TrackExtractionFile file = targets[i];
                    TrackExtractionSource source = null;
                    string error = "";

                    if (this.StopRequested)
                        break;

                    this.ReportProgress(i, targets.Count, Path.GetFileName(file.InputPath));
                    try
                    {
                        source = service.ReadTracks(file.InputPath, cancellation);
                    }
                    catch (Exception ex) when (this.IsOperationStopped(ex))
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        error = ex.Message;
                    }

                    // Il video letto conta per intero nell'avanzamento: a fine lettura la barra è piena
                    lock (this.StateLock)
                    {
                        if (source != null)
                            file.SetSource(source);
                        else
                            file.SetUnreadable(error);
                        this.ProgressState.GlobalPercent = (i + 1) * 100 / targets.Count;
                    }
                    this.NotifyProgressChanged();
                    if (source != null)
                        read++;
                    else
                    {
                        unreadable++;
                        this.AppendLog(AppText.F("web.toolbox.trackExtraction.fileUnreadable", Path.GetFileName(file.InputPath), error));
                    }
                    this.NotifyRecordsChanged();
                }
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.toolbox.trackExtraction.readError", ex.Message));
            }
            finally
            {
                // Un video non raggiunto o interrotto dallo stop, o lasciato da un errore, non è illeggibile: resta da leggere
                lock (this.StateLock)
                {
                    foreach (TrackExtractionFile file in targets)
                    {
                        if (file.State == TrackExtractionFileState.Reading)
                            file.SetNotRead();
                    }
                    this.PruneFilters();
                }

                if (read == targets.Count)
                    this.AppendLog(AppText.Count("web.toolbox.trackExtraction.readCompleted", read));
                else if (read + unreadable == targets.Count)
                    this.AppendLog(AppText.F("web.toolbox.trackExtraction.readCompletedWithErrors", read, unreadable));
                else
                    this.AppendLog(AppText.F("web.toolbox.trackExtraction.readCompletedPartially", read, unreadable, targets.Count - read - unreadable));
                this.SetBusy(false, "");
                this.NotifyRecordsChanged();
            }
        }

        /// <summary>
        /// Worker dell'estrazione: un video alla volta, tutte le sue tracce scelte insieme; un errore non ferma le altre tracce né gli
        /// altri video. Gli avvisi che non nominano una traccia riguardano il video: vanno nel log una volta e non toccano gli esiti
        /// </summary>
        /// <param name="batch">Tracce del lotto, fissate alla riserva</param>
        /// <param name="outputFolder">Cartella di uscita del lotto, vuota per accanto ai video</param>
        private void ExtractWorker(List<TrackExtractionTrack> batch, string outputFolder)
        {
            List<TrackExtractionFile> files = new List<TrackExtractionFile>();
            Dictionary<TrackExtractionFile, List<TrackExtractionTrack>> tracksByFile = new Dictionary<TrackExtractionFile, List<TrackExtractionTrack>>();
            TrackExtractionService service = null;
            TrackExtractionBatchSummary summary = new TrackExtractionBatchSummary(this.TextPrefix, batch.Count);

            this.RunBatch(files, outputFolder, summary,
                () => AppText.F("web.toolbox.trackExtraction.batchStarted", AppText.Count("web.toolbox.trackExtraction.tracksCount", batch.Count), AppText.Count("web.toolbox.trackExtraction.filesCount", files.Count),
                    outputFolder.Length > 0 ? outputFolder : AppText.T("web.toolbox.output.nextToSource")),
                () =>
                {
                    service = this.CreateService();
                    foreach (TrackExtractionTrack track in batch)
                    {
                        if (!tracksByFile.ContainsKey(track.File))
                        {
                            files.Add(track.File);
                            tracksByFile[track.File] = new List<TrackExtractionTrack>();
                        }
                        tracksByFile[track.File].Add(track);
                    }
                    batch.ForEach(t => t.Reset(ToolboxItemStatus.Pending));
                },
                file => tracksByFile[file].ForEach(t => t.Reset(ToolboxItemStatus.Running)),
                (file, allocator, onProgress) =>
                {
                    List<TrackExtractionTrack> tracks = tracksByFile[file];
                    List<ExtractableTrack> chosen = tracks.ConvertAll(t => t.Track);
                    List<string> fileWarnings = new List<string>();
                    string name = Path.GetFileName(file.InputPath);
                    List<TrackExtractionResult> results;

                    try
                    {
                        results = service.Extract(file.Source, chosen, outputFolder, allocator, onProgress, fileWarnings);
                    }
                    catch (Exception ex)
                    {
                        results = chosen.ConvertAll(t => new TrackExtractionResult(t));
                        results.ForEach(r => this.ApplyException(r, ex));
                    }

                    this.LogWarnings(name, fileWarnings);
                    for (int t = 0; t < tracks.Count; t++)
                    {
                        TrackExtractionTrack track = tracks[t];
                        TrackExtractionResult result = results[t];
                        bool convertsToSrt = track.Track.Format.ConvertsToSrt;

                        this.RecordResult(result, AppText.F("web.toolbox.trackExtraction.trackLabel", name, track.Track.Number, track.Track.Format.Name, track.Track.Language),
                            convertsToSrt ? "web.toolbox.trackExtraction.trackConverted" : "web.toolbox.trackExtraction.trackExtracted", summary, () => track.Result = result);
                        if (convertsToSrt && result.IsDone)
                            summary.AddConvertedToSrt();
                    }
                });
        }

        /// <summary>
        /// Servizio sugli strumenti configurati adesso, letti al momento dell'uso come nelle altre modalità
        /// </summary>
        /// <returns>Servizio di estrazione</returns>
        private TrackExtractionService CreateService()
        {
            return new TrackExtractionService(AppSettingsService.Instance.Settings.Tools.MkvMergePath, AppSettingsService.Instance.Settings.Tools.MkvExtractPath,
                AppSettingsService.Instance.Settings.Tools.FfprobePath, AppSettingsService.Instance.Settings.Tools.FfmpegPath);
        }

        /// <summary>
        /// Tracce scelte nella vista aperta, nell'ordine dei video e delle tracce; da chiamare sotto StateLock
        /// </summary>
        /// <returns>Tracce estraibili scelte</returns>
        private List<TrackExtractionTrack> SelectTracks()
        {
            List<TrackExtractionTrack> selection = new List<TrackExtractionTrack>();

            foreach (TrackExtractionFile file in this.Records)
            {
                foreach (TrackExtractionTrack track in file.Tracks)
                {
                    if (track.Track.IsExtractable && (this._view == TrackExtractionView.ByFile ? track.IsChecked :
                        (this._kinds.Count == 0 || this._kinds.Contains(track.Track.Kind)) &&
                        (this._languages.Count == 0 || this._languages.Contains(track.Track.Language)) &&
                        (this._formats.Count == 0 || this._formats.Contains(track.Track.Format.Name))))
                        selection.Add(track);
                }
            }

            return selection;
        }

        /// <summary>
        /// Lingue e formati presenti fra le tracce estraibili dei tipi scelti, in ordine alfabetico; da chiamare sotto StateLock
        /// </summary>
        /// <param name="languages">Lingue presenti</param>
        /// <param name="formats">Formati presenti</param>
        private void CollectFilterValues(out List<string> languages, out List<string> formats)
        {
            SortedSet<string> languageSet = new SortedSet<string>(StringComparer.Ordinal);
            SortedSet<string> formatSet = new SortedSet<string>(StringComparer.Ordinal);

            foreach (TrackExtractionFile file in this.Records)
            {
                foreach (TrackExtractionTrack track in file.Tracks)
                {
                    if (track.Track.IsExtractable && (this._kinds.Count == 0 || this._kinds.Contains(track.Track.Kind)))
                    {
                        languageSet.Add(track.Track.Language);
                        formatSet.Add(track.Track.Format.Name);
                    }
                }
            }

            languages = new List<string>(languageSet);
            formats = new List<string>(formatSet);
        }

        /// <summary>
        /// Toglie dai filtri le lingue e i formati che fra i video e i tipi scelti non esistono più; da chiamare sotto StateLock
        /// </summary>
        private void PruneFilters()
        {
            List<string> languages;
            List<string> formats;

            this.CollectFilterValues(out languages, out formats);
            this._languages.RemoveAll(l => !languages.Contains(l));
            this._formats.RemoveAll(f => !formats.Contains(f));
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Vista aperta
        /// </summary>
        public TrackExtractionView View
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._view;
                }
            }
        }

        /// <summary>
        /// Tipi scelti nella vista per tipo, vuoto = tutti
        /// </summary>
        public List<TrackExtractionKind> Kinds
        {
            get
            {
                lock (this.StateLock)
                {
                    return new List<TrackExtractionKind>(this._kinds);
                }
            }
        }

        /// <summary>
        /// Lingue scelte nella vista per tipo, vuoto = tutte
        /// </summary>
        public List<string> Languages
        {
            get
            {
                lock (this.StateLock)
                {
                    return new List<string>(this._languages);
                }
            }
        }

        /// <summary>
        /// Formati scelti nella vista per tipo, vuoto = tutti
        /// </summary>
        public List<string> Formats
        {
            get
            {
                lock (this.StateLock)
                {
                    return new List<string>(this._formats);
                }
            }
        }

        /// <summary>
        /// Lingue presenti fra le tracce estraibili dei tipi scelti, i valori della tendina Lingua
        /// </summary>
        public List<string> AvailableLanguages
        {
            get
            {
                lock (this.StateLock)
                {
                    this.CollectFilterValues(out List<string> languages, out _);
                    return languages;
                }
            }
        }

        /// <summary>
        /// Formati presenti fra le tracce estraibili dei tipi scelti, i valori della tendina Formato
        /// </summary>
        public List<string> AvailableFormats
        {
            get
            {
                lock (this.StateLock)
                {
                    this.CollectFilterValues(out _, out List<string> formats);
                    return formats;
                }
            }
        }

        /// <summary>
        /// True se nella vista aperta c'è almeno una traccia scelta da estrarre e l'uscita è valida
        /// </summary>
        public override bool CanStart
        {
            get
            {
                lock (this.StateLock)
                {
                    return !this.IsBusy && this.GetOutputFolderError().Length == 0 && this.SelectTracks().Exists(t => !t.Result.IsDone);
                }
            }
        }

        #endregion
    }

    /// <summary>
    /// Stato della lettura delle tracce di un video
    /// </summary>
    public enum TrackExtractionFileState
    {
        /// <summary>Tracce in lettura</summary>
        Reading,

        /// <summary>Tracce lette</summary>
        Read,

        /// <summary>Lo strumento non legge il video</summary>
        Unreadable,

        /// <summary>Lettura interrotta o non raggiunta: si riprende al prossimo Aggiungi file</summary>
        NotRead
    }

    /// <summary>
    /// Esito complessivo di un video, ricavato dalle sue tracce
    /// </summary>
    public enum TrackExtractionFileOutcome
    {
        /// <summary>Tracce in lettura</summary>
        Reading,

        /// <summary>Lo strumento non legge il video</summary>
        Unreadable,

        /// <summary>Lettura interrotta o non raggiunta</summary>
        NotRead,

        /// <summary>Nessuna traccia ancora elaborata</summary>
        Pending,

        /// <summary>Estrazione in corso</summary>
        Running,

        /// <summary>Tracce elaborate tutte estratte</summary>
        Extracted,

        /// <summary>Tracce elaborate tutte estratte, qualcuna con avvisi</summary>
        ExtractedWithWarnings,

        /// <summary>Qualche traccia estratta e qualcuna no</summary>
        Partial,

        /// <summary>Nessuna traccia elaborata estratta</summary>
        Failed,

        /// <summary>Estrazione interrotta dallo stop</summary>
        Stopped
    }

    /// <summary>
    /// Video nell'elenco dell'estrazione, con le sue tracce
    /// </summary>
    public sealed class TrackExtractionFile : ToolboxRecord
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="inputPath">Percorso completo del video</param>
        /// <param name="size">Dimensione in byte</param>
        public TrackExtractionFile(string inputPath, long size) : base(inputPath, size)
        {
            this.State = TrackExtractionFileState.Reading;
            this.Error = "";
            this.Tracks = new List<TrackExtractionTrack>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Registra le tracce lette
        /// </summary>
        /// <param name="source">Video letto</param>
        public void SetSource(TrackExtractionSource source)
        {
            this.Source = source;
            this.Tracks.Clear();
            foreach (ExtractableTrack track in source.Tracks)
                this.Tracks.Add(new TrackExtractionTrack(this, track));
            this.State = TrackExtractionFileState.Read;
        }

        /// <summary>
        /// Registra che le tracce non si leggono
        /// </summary>
        /// <param name="error">Motivo</param>
        public void SetUnreadable(string error)
        {
            this.Error = error;
            this.State = TrackExtractionFileState.Unreadable;
        }

        /// <summary>
        /// Registra che la lettura è stata interrotta o non è arrivata al video
        /// </summary>
        public void SetNotRead()
        {
            this.Error = AppText.T("web.toolbox.trackExtraction.readStopped");
            this.State = TrackExtractionFileState.NotRead;
        }

        /// <summary>
        /// Rimette il video in lettura
        /// </summary>
        public void SetReading()
        {
            this.Error = "";
            this.State = TrackExtractionFileState.Reading;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Stato della lettura delle tracce
        /// </summary>
        public TrackExtractionFileState State { get; private set; }

        /// <summary>
        /// Perché le tracce non si leggono o non sono state lette, vuoto se lette o in lettura
        /// </summary>
        public string Error { get; private set; }

        /// <summary>
        /// Video letto, null finché le tracce non sono lette
        /// </summary>
        public TrackExtractionSource Source { get; private set; }

        /// <summary>
        /// Tracce del video, video comprese, nell'ordine del file
        /// </summary>
        public List<TrackExtractionTrack> Tracks { get; }

        /// <summary>
        /// Esito complessivo, dalle tracce già elaborate: in corso se una è in corso, poi interrotto, poi estratto, in parte o errore
        /// </summary>
        public TrackExtractionFileOutcome Outcome
        {
            get
            {
                int extracted = 0;
                int warnings = 0;
                int failed = 0;
                bool stopped = false;

                if (this.State != TrackExtractionFileState.Read)
                {
                    return this.State switch
                    {
                        TrackExtractionFileState.Reading => TrackExtractionFileOutcome.Reading,
                        TrackExtractionFileState.NotRead => TrackExtractionFileOutcome.NotRead,
                        _ => TrackExtractionFileOutcome.Unreadable
                    };
                }

                foreach (TrackExtractionTrack track in this.Tracks)
                {
                    if (track.Result.Status == ToolboxItemStatus.Running)
                        return TrackExtractionFileOutcome.Running;
                    if (track.Result.Status == ToolboxItemStatus.Done)
                        extracted++;
                    else if (track.Result.Status == ToolboxItemStatus.DoneWithWarnings)
                        warnings++;
                    else if (track.Result.Status == ToolboxItemStatus.Failed)
                        failed++;
                    else if (track.Result.Status == ToolboxItemStatus.Stopped)
                        stopped = true;
                }

                if (stopped)
                    return TrackExtractionFileOutcome.Stopped;
                if (failed > 0)
                    return extracted + warnings > 0 ? TrackExtractionFileOutcome.Partial : TrackExtractionFileOutcome.Failed;
                if (warnings > 0)
                    return TrackExtractionFileOutcome.ExtractedWithWarnings;
                return extracted > 0 ? TrackExtractionFileOutcome.Extracted : TrackExtractionFileOutcome.Pending;
            }
        }

        #endregion
    }

    /// <summary>
    /// Traccia di un video nell'elenco dell'estrazione, con la sua spunta nella vista per file e l'esito
    /// </summary>
    public sealed class TrackExtractionTrack
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="file">Video a cui appartiene</param>
        /// <param name="track">Traccia letta</param>
        public TrackExtractionTrack(TrackExtractionFile file, ExtractableTrack track)
        {
            this.File = file;
            this.Track = track;
            this.Reset(ToolboxItemStatus.Pending);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Porta la traccia a uno stato senza esito: uscita, motivo e dettagli si azzerano
        /// </summary>
        /// <param name="status">Stato da impostare</param>
        public void Reset(ToolboxItemStatus status)
        {
            this.Result = new ToolboxItemResult(status);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Video a cui appartiene
        /// </summary>
        public TrackExtractionFile File { get; }

        /// <summary>
        /// Traccia letta
        /// </summary>
        public ExtractableTrack Track { get; }

        /// <summary>
        /// True se spuntata nella vista per file
        /// </summary>
        public bool IsChecked { get; set; }

        /// <summary>
        /// Esito dell'estrazione: in riga il file scritto o il motivo breve, nel tooltip gli errori dello strumento così come li ha
        /// scritti oppure i suoi avvisi
        /// </summary>
        public ToolboxItemResult Result { get; set; }

        #endregion
    }

    /// <summary>
    /// Riepilogo di un lotto di estrazione, contato in tracce, con quante delle estratte sono state convertite in SRT
    /// </summary>
    public sealed class TrackExtractionBatchSummary : ToolboxBatchSummary
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="textPrefix">Prefisso delle chiavi dei testi della card</param>
        /// <param name="total">Tracce del lotto</param>
        public TrackExtractionBatchSummary(string textPrefix, int total) : base(textPrefix, total)
        {
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Conta una traccia estratta convertendo il Timed Text in SRT
        /// </summary>
        public void AddConvertedToSrt()
        {
            this.ConvertedToSrt++;
        }

        /// <summary>
        /// Descrive i conteggi diversi da zero, oppure che nessuna traccia è stata elaborata, e quante delle estratte sono state
        /// convertite in SRT
        /// </summary>
        /// <returns>Testo del riepilogo</returns>
        public override string Describe()
        {
            string counts = base.Describe();
            List<string> parts = new List<string> { counts.Length > 0 ? counts : AppText.T("web.toolbox.trackExtraction.summaryNothing") };

            if (this.ConvertedToSrt > 0)
                parts.Add(AppText.Count("web.toolbox.trackExtraction.summaryConverted", this.ConvertedToSrt));

            return string.Join(", ", parts);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Tracce estratte convertendo il Timed Text in SRT
        /// </summary>
        public int ConvertedToSrt { get; private set; }

        #endregion
    }
}
