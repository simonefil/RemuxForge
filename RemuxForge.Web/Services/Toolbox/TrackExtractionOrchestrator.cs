using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Toolbox.Common;
using RemuxForge.Core.Toolbox.TrackExtraction;
using System;
using System.Collections.Generic;
using System.IO;

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
        #region Costanti

        /// <summary>
        /// Avvisi dello strumento riportati per esteso nel log e nel tooltip della traccia
        /// </summary>
        private const int LISTED_WARNINGS = 5;

        #endregion

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
        /// Apre una vista; la scelta delle due viste resta com'era
        /// </summary>
        /// <param name="view">Vista da aprire</param>
        public void SetView(TrackExtractionView view)
        {
            lock (this.StateLock)
            {
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
            if (this.IsBusy || !track.Track.IsExtractable)
                return;

            lock (this.StateLock)
            {
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
            if (this.IsBusy)
                return;

            lock (this.StateLock)
            {
                foreach (TrackExtractionTrack track in file.Tracks)
                    track.IsChecked = isChecked && track.Track.IsExtractable;
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Imposta i filtri della vista per tipo; lingue e formati che con i tipi scelti non esistono più si tolgono
        /// </summary>
        /// <param name="kinds">Tipi scelti, vuoto = tutti</param>
        /// <param name="languages">Lingue scelte, vuoto = tutte</param>
        /// <param name="formats">Formati scelti, vuoto = tutti</param>
        public void SetFilters(IEnumerable<TrackExtractionKind> kinds, IEnumerable<string> languages, IEnumerable<string> formats)
        {
            lock (this.StateLock)
            {
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
        /// Estrae le tracce scelte nella vista aperta che non sono già state estratte
        /// </summary>
        public override void Start()
        {
            if (this.CanStart)
                this.TryRunExclusive(this.ExtractWorker);
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
        /// <returns>Percorso previsto per traccia</returns>
        public Dictionary<TrackExtractionTrack, string> PlanOutputs()
        {
            Dictionary<TrackExtractionTrack, string> plan = new Dictionary<TrackExtractionTrack, string>();

            lock (this.StateLock)
            {
                List<string> inputs = new List<string>();
                string outputFolder = this.GetBatchOutputFolder();

                if (this.GetOutputFolderError().Length > 0)
                    return plan;

                foreach (TrackExtractionFile file in this.Records)
                    inputs.Add(file.InputPath);

                OutputPathAllocator allocator = new OutputPathAllocator(inputs);
                foreach (TrackExtractionTrack track in this.SelectTracks())
                {
                    if (!track.IsDone)
                        plan[track] = allocator.Reserve(TrackExtractionService.GetDesiredPath(track.File.InputPath, outputFolder, track.Track));
                }
            }

            return plan;
        }

        #endregion

        #region Metodi protected

        /// <inheritdoc />
        protected override TrackExtractionFile CreateRecord(string inputPath, long size)
        {
            return new TrackExtractionFile(inputPath, size);
        }

        /// <summary>
        /// Legge in background le tracce dei video appena accodati
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
        /// Worker della lettura: legge le tracce dei video appena aggiunti, uno alla volta; un video illeggibile resta in elenco con
        /// il motivo
        /// </summary>
        private void ReadWorker()
        {
            List<TrackExtractionFile> targets = new List<TrackExtractionFile>();
            int unreadable = 0;

            this.SetBusy(true, AppText.T("web.toolbox.trackExtraction.reading"));
            try
            {
                lock (this.StateLock)
                {
                    targets.AddRange(this.Records.FindAll(f => f.State == TrackExtractionFileState.Reading));
                }

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
                        source = service.ReadTracks(file.InputPath);
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
                    if (source == null)
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
                // Un video non raggiunto dallo stop o da un errore non ha tracce: resta in elenco con il motivo
                lock (this.StateLock)
                {
                    foreach (TrackExtractionFile file in targets)
                    {
                        if (file.State == TrackExtractionFileState.Reading)
                        {
                            file.SetUnreadable(AppText.T("web.toolbox.trackExtraction.readStopped"));
                            unreadable++;
                        }
                    }
                    this.PruneFilters();
                }

                this.AppendLog(unreadable > 0 ? AppText.F("web.toolbox.trackExtraction.readCompletedWithErrors", targets.Count - unreadable, unreadable) : AppText.Count("web.toolbox.trackExtraction.readCompleted", targets.Count));
                this.SetBusy(false, "");
                this.NotifyRecordsChanged();
            }
        }

        /// <summary>
        /// Worker dell'estrazione: un video alla volta, tutte le sue tracce scelte insieme; un errore non ferma le altre tracce né gli
        /// altri video
        /// </summary>
        private void ExtractWorker()
        {
            List<TrackExtractionFile> files = new List<TrackExtractionFile>();
            Dictionary<TrackExtractionFile, List<TrackExtractionTrack>> tracksByFile = new Dictionary<TrackExtractionFile, List<TrackExtractionTrack>>();
            List<string> inputs = new List<string>();
            TrackExtractionBatchSummary summary = new TrackExtractionBatchSummary();
            string outputFolder;
            string folderError;

            this.SetBusy(true, AppText.T("web.toolbox.trackExtraction.progress"));
            try
            {
                lock (this.StateLock)
                {
                    outputFolder = this.GetBatchOutputFolder();
                    folderError = this.GetOutputFolderError();
                    foreach (TrackExtractionFile file in this.Records)
                        inputs.Add(file.InputPath);
                    foreach (TrackExtractionTrack track in this.SelectTracks())
                    {
                        if (track.IsDone)
                            continue;
                        if (!tracksByFile.ContainsKey(track.File))
                        {
                            files.Add(track.File);
                            tracksByFile[track.File] = new List<TrackExtractionTrack>();
                        }
                        tracksByFile[track.File].Add(track);
                        summary.Total++;
                    }
                }

                // La cartella può sparire fra la scelta e l'avvio: il lotto non parte e nessuna traccia cambia stato
                if (folderError.Length > 0)
                    throw new InvalidOperationException(folderError);

                // Gli input dell'intero elenco restano vietati come uscita
                OutputPathAllocator allocator = new OutputPathAllocator(inputs);
                TrackExtractionService service = this.CreateService();
                this.AppendLog(AppText.F("web.toolbox.trackExtraction.batchStarted", AppText.Count("web.toolbox.trackExtraction.tracksCount", summary.Total), AppText.Count("web.toolbox.trackExtraction.filesCount", files.Count),
                    outputFolder.Length > 0 ? outputFolder : AppText.T("web.toolbox.output.nextToSource")));

                for (int i = 0; i < files.Count; i++)
                {
                    TrackExtractionFile file = files[i];
                    List<TrackExtractionTrack> tracks = tracksByFile[file];
                    List<ExtractableTrack> chosen = tracks.ConvertAll(t => t.Track);
                    string name = Path.GetFileName(file.InputPath);
                    List<TrackExtractionResult> results;

                    if (this.StopRequested)
                        break;

                    this.ReportProgress(i, files.Count, name);
                    lock (this.StateLock)
                    {
                        foreach (TrackExtractionTrack track in tracks)
                            track.Reset(TrackExtractionStatus.Running);
                    }
                    this.NotifyRecordsChanged();

                    // Il try sta dentro il ciclo: un video illeggibile o una cartella non scrivibile fermano quel video soltanto
                    try
                    {
                        results = service.Extract(file.Source, chosen, outputFolder, allocator, percent =>
                        {
                            lock (this.StateLock)
                            {
                                this.ProgressState.CurrentIndeterminate = false;
                                this.ProgressState.CurrentPercent = percent;
                                this.ProgressState.GlobalPercent = (i * 100 + percent) / files.Count;
                            }
                            this.NotifyProgressChanged();
                        });
                    }
                    catch (Exception ex)
                    {
                        bool stopped = this.IsOperationStopped(ex);
                        results = chosen.ConvertAll(t => new TrackExtractionResult(t)
                        {
                            Status = stopped ? TrackExtractionStatus.Stopped : TrackExtractionStatus.Failed,
                            Message = ex.Message
                        });
                    }

                    for (int t = 0; t < tracks.Count; t++)
                    {
                        TrackExtractionTrack track = tracks[t];
                        TrackExtractionResult result = results[t];
                        string label = AppText.F("web.toolbox.trackExtraction.trackLabel", name, track.Track.Number, track.Track.Format.Name, track.Track.Language);

                        // Lo strumento può ripetere lo stesso avviso: log e tooltip ne riportano i primi e il conteggio degli altri
                        List<string> listed = result.Warnings.GetRange(0, Math.Min(result.Warnings.Count, LISTED_WARNINGS));
                        if (result.Warnings.Count > LISTED_WARNINGS)
                            listed.Add(AppText.Count("web.toolbox.trackExtraction.tooltipMoreWarnings", result.Warnings.Count - LISTED_WARNINGS));

                        lock (this.StateLock)
                        {
                            track.Status = result.Status;
                            track.OutputPath = result.OutputPath;
                            track.Message = result.Message;
                            track.Details = result.Details.Length > 0 ? result.Details : string.Join(Environment.NewLine, listed);
                        }

                        for (int w = 0; w < result.Warnings.Count && w < LISTED_WARNINGS; w++)
                            this.AppendLog(AppText.F("web.toolbox.trackExtraction.trackWarning", label, result.Warnings[w]));
                        if (result.Warnings.Count > LISTED_WARNINGS)
                            this.AppendLog(AppText.Count("web.toolbox.trackExtraction.moreWarnings", result.Warnings.Count - LISTED_WARNINGS, label));

                        summary.Add(result.Status, track.Track.Format.ConvertsToSrt);
                        if (result.Status == TrackExtractionStatus.Extracted || result.Status == TrackExtractionStatus.ExtractedWithWarnings)
                            this.AppendLog(AppText.F(track.Track.Format.ConvertsToSrt ? "web.toolbox.trackExtraction.trackConverted" : "web.toolbox.trackExtraction.trackExtracted", label, result.OutputPath));
                        else if (result.Status == TrackExtractionStatus.Failed)
                            this.AppendLog(AppText.F("web.toolbox.trackExtraction.trackFailed", label, result.Details.Length > 0 ? result.Details : result.Message));
                        else
                            this.AppendLog(AppText.F("web.toolbox.trackExtraction.trackStopped", label));
                    }

                    // Il video concluso conta per intero anche quando lo strumento non ha riportato il cento per cento; quello fermato no
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
                this.AppendLog(AppText.F("web.toolbox.trackExtraction.batchError", ex.Message));
            }
            finally
            {
                this.FinishBatch(summary);
            }
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
                    return !this.IsBusy && this.GetOutputFolderError().Length == 0 && this.SelectTracks().Exists(t => !t.IsDone);
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

        /// <summary>Tracce non lette</summary>
        Unreadable
    }

    /// <summary>
    /// Esito complessivo di un video, ricavato dalle sue tracce
    /// </summary>
    public enum TrackExtractionFileOutcome
    {
        /// <summary>Tracce in lettura</summary>
        Reading,

        /// <summary>Tracce non lette</summary>
        Unreadable,

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

        #endregion

        #region Proprietà

        /// <summary>
        /// Stato della lettura delle tracce
        /// </summary>
        public TrackExtractionFileState State { get; private set; }

        /// <summary>
        /// Perché le tracce non si leggono, vuoto se lette o in lettura
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
                    return this.State == TrackExtractionFileState.Reading ? TrackExtractionFileOutcome.Reading : TrackExtractionFileOutcome.Unreadable;

                foreach (TrackExtractionTrack track in this.Tracks)
                {
                    if (track.Status == TrackExtractionStatus.Running)
                        return TrackExtractionFileOutcome.Running;
                    if (track.Status == TrackExtractionStatus.Extracted)
                        extracted++;
                    else if (track.Status == TrackExtractionStatus.ExtractedWithWarnings)
                        warnings++;
                    else if (track.Status == TrackExtractionStatus.Failed)
                        failed++;
                    else if (track.Status == TrackExtractionStatus.Stopped)
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
            this.Reset(TrackExtractionStatus.Pending);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Porta la traccia a uno stato senza esito: uscita, motivo e dettagli si azzerano
        /// </summary>
        /// <param name="status">Stato da impostare</param>
        public void Reset(TrackExtractionStatus status)
        {
            this.Status = status;
            this.OutputPath = "";
            this.Message = "";
            this.Details = "";
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
        /// Stato dell'estrazione
        /// </summary>
        public TrackExtractionStatus Status { get; set; }

        /// <summary>
        /// File scritto, vuoto finché l'estrazione non riesce
        /// </summary>
        public string OutputPath { get; set; }

        /// <summary>
        /// Motivo dell'errore o dell'interruzione, in forma breve
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Testo completo per il tooltip: errori dello strumento così come li ha scritti, oppure i suoi avvisi
        /// </summary>
        public string Details { get; set; }

        /// <summary>
        /// True se la traccia è già estratta, anche con avvisi, e non va rielaborata
        /// </summary>
        public bool IsDone
        {
            get { return this.Status == TrackExtractionStatus.Extracted || this.Status == TrackExtractionStatus.ExtractedWithWarnings; }
        }

        #endregion
    }

    /// <summary>
    /// Riepilogo di un lotto di estrazione, contato in tracce
    /// </summary>
    public sealed class TrackExtractionBatchSummary : ToolboxBatchSummary
    {
        #region Metodi pubblici

        /// <summary>
        /// Conta l'esito di una traccia elaborata
        /// </summary>
        /// <param name="status">Stato finale della traccia</param>
        /// <param name="convertsToSrt">True se la traccia si converte in SRT</param>
        public void Add(TrackExtractionStatus status, bool convertsToSrt)
        {
            if (status == TrackExtractionStatus.Extracted)
                this.Extracted++;
            else if (status == TrackExtractionStatus.ExtractedWithWarnings)
                this.ExtractedWithWarnings++;
            else if (status == TrackExtractionStatus.Failed)
                this.Failed++;
            else
                this.Interrupted++;

            if (convertsToSrt && (status == TrackExtractionStatus.Extracted || status == TrackExtractionStatus.ExtractedWithWarnings))
                this.ConvertedToSrt++;
        }

        /// <summary>
        /// Descrive i conteggi diversi da zero, nell'ordine estratte, con avvisi, errori, interrotte, non elaborate, e quante delle
        /// estratte sono state convertite in SRT
        /// </summary>
        /// <returns>Testo del riepilogo</returns>
        public override string Describe()
        {
            List<string> parts = new List<string>();
            string[] keys = { "summaryExtracted", "summaryWithWarnings", "summaryFailed", "summaryStopped", "summaryNotProcessed" };
            int[] counts = { this.Extracted, this.ExtractedWithWarnings, this.Failed, this.Interrupted, this.NotProcessed };

            for (int i = 0; i < keys.Length; i++)
            {
                if (counts[i] > 0)
                    parts.Add(AppText.Count("web.toolbox.trackExtraction." + keys[i], counts[i]));
            }

            if (parts.Count == 0)
                parts.Add(AppText.T("web.toolbox.trackExtraction.summaryNothing"));
            if (this.ConvertedToSrt > 0)
                parts.Add(AppText.Count("web.toolbox.trackExtraction.summaryConverted", this.ConvertedToSrt));

            return string.Join(", ", parts);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Tracce estratte senza avvisi
        /// </summary>
        public int Extracted { get; private set; }

        /// <summary>
        /// Tracce estratte con avvisi dello strumento
        /// </summary>
        public int ExtractedWithWarnings { get; private set; }

        /// <summary>
        /// Tracce non estratte per errore
        /// </summary>
        public int Failed { get; private set; }

        /// <summary>
        /// Tracce interrotte dallo stop durante l'estrazione
        /// </summary>
        public int Interrupted { get; private set; }

        /// <summary>
        /// Tracce estratte convertendo il Timed Text in SRT
        /// </summary>
        public int ConvertedToSrt { get; private set; }

        /// <summary>
        /// Tracce del lotto mai avviate, per lo stop o per un errore prima del primo video
        /// </summary>
        public int NotProcessed
        {
            get { return this.Total - this.Extracted - this.ExtractedWithWarnings - this.Failed - this.Interrupted; }
        }

        /// <summary>
        /// True se ogni traccia del lotto è stata estratta senza avvisi
        /// </summary>
        public override bool AllSucceeded
        {
            get { return this.Extracted > 0 && this.ExtractedWithWarnings + this.Failed + this.Interrupted + this.NotProcessed == 0 && !this.Stopped; }
        }

        /// <summary>
        /// True se ci sono errori e nessuna traccia è stata estratta
        /// </summary>
        public override bool NothingSucceeded
        {
            get { return this.Failed > 0 && this.Extracted + this.ExtractedWithWarnings == 0; }
        }

        #endregion
    }
}
