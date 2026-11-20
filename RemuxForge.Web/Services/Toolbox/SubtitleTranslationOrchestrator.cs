using RemuxForge.Core.Ai;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Toolbox.Common;
using RemuxForge.Core.Toolbox.SubtitleTranslation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Web.Services.Toolbox
{
    /// <summary>
    /// Card «Traduzione sottotitoli AI»: elenco dei progetti, passo Progetto con i file preparati e il glossario caricato, analisi
    /// A1/A2 in background col glossario proposto, approvazione ed export del glossario, job di traduzione a blocchi con ripresa,
    /// archivio del progetto, registri delle chiamate
    /// </summary>
    public sealed class SubtitleTranslationOrchestrator : ToolboxOrchestratorBase<TranslationFileRecord>
    {
        #region Costanti

        /// <summary>
        /// Prefisso delle chiavi di testo della card
        /// </summary>
        public const string TEXT_PREFIX = "web.toolbox.subtitleTranslation";

        /// <summary>
        /// Estensione del file di «Esporta glossario»
        /// </summary>
        private const string GLOSSARY_EXTENSION = ".glossary.xlsx";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Progetti su disco
        /// </summary>
        private readonly TranslationProjectStore _store;

        /// <summary>
        /// Job su disco
        /// </summary>
        private readonly TranslationJobStore _jobStore;

        /// <summary>
        /// Esporta e importa progetti
        /// </summary>
        private readonly TranslationProjectArchive _archive;

        /// <summary>
        /// Provider del modello, registrato o reale
        /// </summary>
        private readonly TranslationModelProviderFactory _providerFactory;

        /// <summary>
        /// Configurazione dell'accesso AI
        /// </summary>
        private readonly AiCredentialService _credentialService;

        /// <summary>
        /// Documenti preparati dei file del progetto aperto, per identificativo
        /// </summary>
        private readonly Dictionary<string, SubtitleTranslationDocument> _documents;

        /// <summary>
        /// Registro delle chiamate del progetto aperto
        /// </summary>
        private readonly List<TranslationCallRecord> _calls;

        /// <summary>
        /// Stati dei file del job attivo, per identificativo
        /// </summary>
        private readonly Dictionary<string, TranslationJobFileState> _jobFiles;

        /// <summary>
        /// Registro delle chiamate del job attivo
        /// </summary>
        private readonly List<TranslationCallRecord> _jobCalls;

        /// <summary>
        /// Progetto aperto, null nell'elenco dei progetti
        /// </summary>
        private TranslationProject _project;

        /// <summary>
        /// Job attivo del progetto aperto, null prima dell'avvio del giro
        /// </summary>
        private TranslationJob _job;

        /// <summary>
        /// Frase dell'attesa durante l'analisi
        /// </summary>
        private string _phrase;

        /// <summary>
        /// Riga secondaria dell'attesa durante l'analisi
        /// </summary>
        private string _detail;

        /// <summary>
        /// Chiamate al modello in corso, anche più di una coi file in parallelo
        /// </summary>
        private readonly Dictionary<TranslationModelRequest, LiveCall> _liveCalls;

        /// <summary>
        /// Chiamata in corso con l'evento più recente, null senza chiamate
        /// </summary>
        private LiveCall _liveLatest;

        /// <summary>
        /// Ultima stima della traduzione, valida finché il progetto non cambia
        /// </summary>
        private TranslationEstimate _translationEstimate;

        /// <summary>
        /// Stato del progetto a cui si riferisce la stima della traduzione
        /// </summary>
        private string _translationEstimateKey;

        #endregion

        #region Eventi

        /// <summary>
        /// Analisi conclusa o fermata: l'esito sta nell'analisi del progetto
        /// </summary>
        public event Action<TranslationAnalysis> OnAnalysisFinished;

        /// <summary>
        /// L'accesso ha rifiutato la ricerca web e l'analisi prosegue senza
        /// </summary>
        public event Action OnWebSearchRejected;

        /// <summary>
        /// Una fase del giro è finita, o il giro si è fermato nella fase
        /// </summary>
        public event Action<TranslationPhaseResult> OnPhaseFinished;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="store">Progetti su disco</param>
        /// <param name="jobStore">Job su disco</param>
        /// <param name="providerFactory">Provider del modello</param>
        /// <param name="credentialService">Configurazione dell'accesso AI</param>
        public SubtitleTranslationOrchestrator(TranslationProjectStore store, TranslationJobStore jobStore, TranslationModelProviderFactory providerFactory, AiCredentialService credentialService) :
            base(TEXT_PREFIX)
        {
            this._store = store;
            this._jobStore = jobStore;
            this._archive = new TranslationProjectArchive(store, jobStore, AppSettingsService.Instance.ConfigFolder);
            this._jobFiles = new Dictionary<string, TranslationJobFileState>(StringComparer.Ordinal);
            this._jobCalls = new List<TranslationCallRecord>();
            this._job = null;
            this._providerFactory = providerFactory;
            this._credentialService = credentialService;
            this._documents = new Dictionary<string, SubtitleTranslationDocument>(StringComparer.Ordinal);
            this._calls = new List<TranslationCallRecord>();
            this._project = null;
            this._phrase = "";
            this._detail = "";
            this._liveCalls = new Dictionary<TranslationModelRequest, LiveCall>();
            this._liveLatest = null;
            this._translationEstimate = null;
            this._translationEstimateKey = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Elenca i progetti salvati, dal più recente; un'analisi rimasta in corso da una chiusura dell'app diventa fermata, tranne
        /// quella del progetto aperto, che vive in memoria
        /// </summary>
        /// <returns>Progetti</returns>
        public List<TranslationProject> ListProjects()
        {
            List<TranslationProject> projects = this._store.List();
            lock (this.StateLock)
            {
                foreach (TranslationProject project in projects)
                {
                    if (this._project == null || this._project.Id != project.Id)
                        this.StopStaleAnalysis(project);
                }
            }

            return projects;
        }

        /// <summary>
        /// Apre un progetto nuovo, salvato alla prima modifica, con modello e livello predefiniti per tutti i passi
        /// </summary>
        public void NewProject()
        {
            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;

                this.Records.Clear();
                this._documents.Clear();
                this._calls.Clear();
                this.ClearJob();
                this._project = this._store.Create();
                this._project.Models = new TranslationPhaseModels
                {
                    Analysis = TranslationPhaseModels.DEFAULT_MODEL,
                    Translation = TranslationPhaseModels.DEFAULT_MODEL,
                    Review = TranslationPhaseModels.DEFAULT_MODEL
                };
                this._project.ReasoningEfforts = new TranslationPhaseModels
                {
                    Analysis = TranslationPhaseModels.DEFAULT_REASONING_EFFORT,
                    Translation = TranslationPhaseModels.DEFAULT_REASONING_EFFORT,
                    Review = TranslationPhaseModels.DEFAULT_REASONING_EFFORT
                };
            }

            this.AppendLog(AppText.T(TEXT_PREFIX + ".projectCreated"));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Apre un progetto salvato e ne prepara i file, con la tabella degli stili allineata ai file prima dell'analisi; un'analisi
        /// rimasta in corso da una chiusura dell'app diventa fermata, e
        /// un file bloccato dall'analisi che è cambiato sul disco resta rifiutato col motivo. Col job si leggono gli stati dei file e
        /// il registro, e un file da tradurre col sorgente mancante o cambiato diventa bloccato
        /// </summary>
        /// <param name="id">Identificativo del progetto</param>
        public void OpenProject(string id)
        {
            TranslationProject project = this._store.Load(id);
            List<TranslationCallRecord> calls = this._store.ReadCalls(id);
            TranslationJob job = this.LoadJob(project);
            Dictionary<string, TranslationJobFileState> jobFiles = new Dictionary<string, TranslationJobFileState>(StringComparer.Ordinal);
            List<TranslationCallRecord> jobCalls = new List<TranslationCallRecord>();
            if (job != null)
            {
                foreach (string fileId in job.FileIds)
                    jobFiles[fileId] = this._jobStore.LoadFile(job.Id, fileId);
                jobCalls = this._jobStore.ReadCalls(job.Id);
            }

            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;

                this.StopStaleAnalysis(project);
                this.Records.Clear();
                this._documents.Clear();
                this._calls.Clear();
                this._calls.AddRange(calls);
                this.ClearJob();
                this._project = project;
                this._job = job;
                foreach (KeyValuePair<string, TranslationJobFileState> state in jobFiles)
                    this._jobFiles[state.Key] = state.Value;
                this._jobCalls.AddRange(jobCalls);

                foreach (TranslationProjectFile file in project.Files)
                {
                    this.Records.Add(new TranslationFileRecord(file));
                    this.PrepareFile(file, project.Analysis != null);
                }
                if (this.SyncStyles())
                    this._store.Save(project);
                if (job != null)
                {
                    // Una chiusura brusca fra il salvataggio dell'ultimo file e quello del job lascia in pausa un job già concluso
                    if (job.Phase == TranslationJobPhase.Translation && job.Status == TranslationJobStatus.Paused &&
                        job.FileIds.TrueForAll(fileId => this._jobFiles[fileId].TranslationStatus == TranslationFileStatus.Completed))
                    {
                        this.FinishTranslation();
                        job.Status = TranslationJobStatus.Idle;
                        job.Pause = null;
                        this._jobStore.Save(job);
                        this._store.Save(project);
                    }
                    this.CheckJobSources();
                }
            }

            this.AppendLog(AppText.F(TEXT_PREFIX + ".projectOpened", DisplayName(project)));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Torna all'elenco dei progetti
        /// </summary>
        public void CloseProject()
        {
            lock (this.StateLock)
            {
                if (this.IsBusy)
                    return;

                this.Records.Clear();
                this._documents.Clear();
                this._calls.Clear();
                this.ClearJob();
                this._project = null;
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Cancella un progetto con tutto il suo stato su disco; mai con la card occupata né il progetto aperto
        /// </summary>
        /// <param name="project">Progetto dell'elenco</param>
        /// <returns>True se il progetto è cancellato</returns>
        public bool DeleteProject(TranslationProject project)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy || (this._project != null && this._project.Id == project.Id))
                    return false;

                foreach (string jobId in project.JobIds)
                    this._jobStore.Delete(jobId);
                this._jobStore.DeleteOfProject(project.Id);
                this._store.Delete(project.Id);
            }

            this.AppendLog(AppText.F(TEXT_PREFIX + ".projectDeleted", DisplayName(project)));
            this.NotifyRecordsChanged();
            return true;
        }

        /// <summary>
        /// Cambia nome, lingue e contesto del progetto; le lingue si bloccano con l'analisi
        /// </summary>
        /// <param name="name">Nome</param>
        /// <param name="sourceLanguage">Lingua d'origine ISO 639-2</param>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        /// <param name="context">Campo serie/contesto</param>
        public void SetProjectData(string name, string sourceLanguage, string targetLanguage, string context)
        {
            lock (this.StateLock)
            {
                if (this._project == null || this.IsBusy)
                    return;

                bool locked = this._project.Analysis != null || this._project.Glossary != null;
                this._project.Name = (name ?? "").Trim();
                this._project.Context = context ?? "";
                if (!locked)
                {
                    this._project.SourceLanguage = sourceLanguage ?? "";
                    this._project.TargetLanguage = targetLanguage ?? "";
                }

                this._store.Save(this._project);
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Cambia la modalità della revisione AI, fino all'avvio del giro
        /// </summary>
        /// <param name="mode">Nessuna o completa</param>
        public void SetReviewMode(TranslationReviewMode mode)
        {
            lock (this.StateLock)
            {
                if (this._project == null || this.IsBusy || this._job != null)
                    return;

                this._project.Options.ReviewMode = mode;
                this._store.Save(this._project);
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Cambia la codifica di un file non UTF-8 e lo prepara di nuovo
        /// </summary>
        /// <param name="record">Riga del file</param>
        /// <param name="encoding">Codifica scelta</param>
        public void SetEncoding(TranslationFileRecord record, string encoding)
        {
            lock (this.StateLock)
            {
                if (this._project == null || this.IsBusy || this.FilesLocked)
                    return;

                record.File.Encoding = string.IsNullOrEmpty(encoding) ? SubtitleTranslationDocument.DEFAULT_ENCODING : encoding;
                this.PrepareFile(record.File, false);
                this.SyncStyles();
                this._store.Save(this._project);
            }

            this.AppendLog(AppText.F(TEXT_PREFIX + ".encodingChanged", Path.GetFileName(record.InputPath), record.File.Encoding));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Elenca i modelli dell'accesso
        /// </summary>
        /// <returns>Modelli con i livelli di ragionamento</returns>
        public Task<List<AiModelInfo>> ListModelsAsync()
        {
            return this._providerFactory.Create(null).ListModelsAsync(CancellationToken.None);
        }

        /// <summary>
        /// Salva modelli e livelli di ragionamento dei passi e i file tradotti in parallelo. Col job i modelli e i livelli di
        /// traduzione e revisione restano quelli congelati nel job e non cambiano; l'analisi e i file in parallelo sì.
        /// Il log riporta i modelli solo quando uno cambia, con quelli che il giro usa davvero
        /// </summary>
        /// <param name="models">Modello per passo</param>
        /// <param name="efforts">Livello per passo</param>
        /// <param name="parallelFiles">File in parallelo, da TranslationProjectOptions.MIN_PARALLEL_FILES a MAX_PARALLEL_FILES</param>
        public void SetModels(TranslationPhaseModels models, TranslationPhaseModels efforts, int parallelFiles)
        {
            TranslationPhaseModels used;
            bool changed;
            lock (this.StateLock)
            {
                if (this._project == null || this.IsBusy || parallelFiles < TranslationProjectOptions.MIN_PARALLEL_FILES || parallelFiles > TranslationProjectOptions.MAX_PARALLEL_FILES)
                    return;

                if (this._job != null)
                {
                    TranslationPhaseModels current = this._project.Models;
                    TranslationPhaseModels currentEfforts = this._project.ReasoningEfforts;
                    models = new TranslationPhaseModels { Analysis = models.Analysis, Translation = current.Translation, Review = current.Review };
                    efforts = new TranslationPhaseModels { Analysis = efforts.Analysis, Translation = currentEfforts.Translation, Review = currentEfforts.Review };
                }

                TranslationPhaseModels savedModels = this._project.Models;
                TranslationPhaseModels savedEfforts = this._project.ReasoningEfforts;
                changed = models.Analysis != savedModels.Analysis || models.Translation != savedModels.Translation || models.Review != savedModels.Review ||
                    efforts.Analysis != savedEfforts.Analysis || efforts.Translation != savedEfforts.Translation || efforts.Review != savedEfforts.Review;
                used = this._job == null ? models
                    : new TranslationPhaseModels { Analysis = models.Analysis, Translation = this._job.Models.Translation, Review = this._job.Models.Review };
                this._project.Models = models;
                this._project.ReasoningEfforts = efforts;
                this._project.Options.MaxParallelFiles = parallelFiles;
                this._store.Save(this._project);
            }

            if (changed)
                this.AppendLog(AppText.F(TEXT_PREFIX + ".modelsChanged", used.Analysis, used.Translation, used.Review));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Avvia l'analisi, o la riprende dalle chiamate mancanti dopo una fermata o un errore
        /// </summary>
        public override void Start()
        {
            lock (this.StateLock)
            {
                if (!this.CanStart)
                    return;

                this.TryRunExclusive(() => this.AnalyzeAsync().GetAwaiter().GetResult());
            }
        }

        /// <summary>
        /// Carica un glossario .xlsx come già deciso: sostituisce quello caricato prima e, con un'analisi conclusa, rifà la fusione
        /// senza chiamate sulle proposte dell'analisi, comprese quelle scartate dal caricamento precedente
        /// </summary>
        /// <param name="path">File .xlsx</param>
        /// <returns>Esito della lettura</returns>
        public GlossaryImportResult ImportGlossary(string path)
        {
            GlossaryImportResult result;
            string name = Path.GetFileName(path);
            byte[] bytes;
            int discarded = 0;

            lock (this.StateLock)
            {
                if (this._project == null || this.IsBusy || !this.CanImportGlossary)
                    return new GlossaryImportResult();

                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (Exception ex)
                {
                    if (!(ex is IOException || ex is UnauthorizedAccessException))
                        throw;
                    return new GlossaryImportResult().Fail(GlossaryFile.FILE_INVALID, "", ex.Message);
                }

                result = GlossaryFile.Import(bytes, this._project.SourceLanguage, this._project.TargetLanguage);
                if (result.Glossary == null)
                    return result;

                GlossaryBuilder builder = new GlossaryBuilder(this._project.SourceLanguage, this._project.TargetLanguage);
                Glossary basis = result.Glossary;
                bool analyzed = this._project.Glossary != null && this._project.Analysis != null && this._project.Analysis.Status == TranslationAnalysisStatus.Completed;
                Glossary current = analyzed ? this.RestoreAnalysisProposals(builder) : null;

                // Le voci dell'utente restano, tranne quelle che il file porta con la stessa chiave: vince la voce caricata
                if (current != null)
                {
                    basis.Terms.AddRange(current.Terms.FindAll(t => t.Origin == GlossaryOrigin.User && !basis.Terms.Exists(b => builder.TermKey(b.Source) == builder.TermKey(t.Source))));
                    basis.Characters.AddRange(current.Characters.FindAll(c => c.Origin == GlossaryOrigin.User &&
                        !basis.Characters.Exists(b => GlossaryBuilder.CharacterKey(b.Name) == GlossaryBuilder.CharacterKey(c.Name))));
                    basis.Addressing.AddRange(current.Addressing.FindAll(a => a.Origin == GlossaryOrigin.User &&
                        !basis.Addressing.Exists(b => GlossaryBuilder.AddressingKey(b.Speaker, b.Addressee) == GlossaryBuilder.AddressingKey(a.Speaker, a.Addressee))));
                }

                Glossary next = basis;
                if (current != null)
                {
                    Glossary delivered = new Glossary();
                    delivered.Series = current.Series;
                    delivered.Tone = current.Tone;
                    delivered.RegisterRules = current.RegisterRules;
                    delivered.Terms.AddRange(current.Terms.FindAll(t => t.Origin == GlossaryOrigin.Analysis));
                    delivered.Characters.AddRange(current.Characters.FindAll(c => c.Origin == GlossaryOrigin.Analysis));
                    delivered.Addressing.AddRange(current.Addressing.FindAll(a => a.Origin == GlossaryOrigin.Analysis));
                    this._project.Analysis.DiscardedForImport = new List<GlossaryDiscard>();
                    next = builder.MergeWithImported(delivered, basis, this._project.Analysis.DiscardedForImport);
                    discarded = this._project.Analysis.DiscardedForImport.FindAll(d => d.Kind == GlossaryEntryKind.Term || d.Kind == GlossaryEntryKind.Character ||
                        d.Kind == GlossaryEntryKind.Addressing).Count;
                }

                next.SourceLanguage = this._project.SourceLanguage;
                next.TargetLanguage = this._project.TargetLanguage;
                next.Revision = this._project.Glossary != null ? this._project.Glossary.Revision : 0;
                next.Status = GlossaryStatus.Draft;
                builder.SetEpisodes(next, this.GetEpisodes(), this.KindsSettled);
                this._project.Glossary = next;
                this._project.ImportedGlossary = new ImportedGlossaryInfo
                {
                    FileName = name,
                    Sha256 = TranslationFilePreparer.ComputeSha256(bytes),
                    ImportedAt = DateTimeOffset.Now,
                    TermCount = result.Glossary.Terms.FindAll(t => t.Origin == GlossaryOrigin.Import).Count,
                    CharacterCount = result.Glossary.Characters.FindAll(c => c.Origin == GlossaryOrigin.Import).Count,
                    AddressingCount = result.Glossary.Addressing.FindAll(a => a.Origin == GlossaryOrigin.Import).Count
                };
                this._store.Save(this._project);
            }

            if (discarded > 0)
                this.AppendLog(AppText.Count(TEXT_PREFIX + ".discardedForImport", discarded));
            this.AppendLog(AppText.F(TEXT_PREFIX + ".glossaryImported", name, this._project.ImportedGlossary.TermCount, this._project.ImportedGlossary.CharacterCount));
            this.NotifyRecordsChanged();
            return result;
        }

        /// <summary>
        /// Toglie dal glossario del progetto le voci caricate. Senza analisi conclusa il glossario era solo quello caricato e se ne
        /// va con i suoi testi; con l'analisi conclusa tornano le proposte e i testi che il caricamento aveva scartato
        /// </summary>
        public void RemoveImportedGlossary()
        {
            lock (this.StateLock)
            {
                if (this._project == null || this.IsBusy || this._project.ImportedGlossary == null || !this.CanImportGlossary)
                    return;

                if (this._project.Analysis == null || this._project.Analysis.Status != TranslationAnalysisStatus.Completed)
                {
                    this._project.Glossary = null;
                }
                else
                {
                    GlossaryBuilder builder = new GlossaryBuilder(this._project.SourceLanguage, this._project.TargetLanguage);
                    Glossary restored = this.RestoreAnalysisProposals(builder);
                    restored.Status = GlossaryStatus.Draft;
                    builder.SetEpisodes(restored, this.GetEpisodes(), this.KindsSettled);
                    this._project.Glossary = restored;
                    this._project.Analysis.DiscardedForImport = new List<GlossaryDiscard>();
                }

                this._project.ImportedGlossary = null;
                this._store.Save(this._project);
            }

            this.AppendLog(AppText.T(TEXT_PREFIX + ".glossaryRemoved"));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Salva le modifiche fatte al glossario dalla pagina; un glossario approvato torna in bozza e le voci rimaste senza le
        /// maiuscole esatte le prendono dalle battute. Dopo l'avvio del giro il glossario resta com'è, salvo nella pausa della
        /// traduzione
        /// </summary>
        /// <param name="change">Modifica, eseguita sotto il lock dello stato</param>
        public void ChangeGlossary(Action<Glossary> change)
        {
            lock (this.StateLock)
            {
                if (this._project == null || this._project.Glossary == null || this.IsBusy || this.GlossaryLocked)
                    return;

                change(this._project.Glossary);
                this._project.Glossary.Status = GlossaryStatus.Draft;
                new GlossaryBuilder(this._project.SourceLanguage, this._project.TargetLanguage).SetEpisodes(this._project.Glossary, this.GetEpisodes(), this.KindsSettled);
                this._store.Save(this._project);
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Approva il glossario, abilita il passo Traduzione e avvia il giro. Un glossario già approvato senza job, rimasto da una
        /// chiusura dell'app o da un avvio non riuscito, avvia solo il giro. Nella pausa della traduzione il glossario cambiato
        /// passa al job, le battute tradotte coi termini cambiati tornano da fare e il giro riprende. Gli stati dei file si salvano
        /// prima del job e il progetto per ultimo: dopo una chiusura brusca il glossario resta da approvare e l'approvazione si rifà
        /// </summary>
        public void ApproveGlossary()
        {
            bool approved = false;
            int? reopened = null;
            lock (this.StateLock)
            {
                if (!this.CanApprove)
                    return;

                Glossary glossary = this._project.Glossary;
                if (glossary.Status == GlossaryStatus.Draft)
                {
                    glossary.Status = GlossaryStatus.Approved;
                    glossary.Revision++;
                    glossary.ApprovedAt = DateTimeOffset.Now;
                    if (this._project.ReachedStep < TranslationProjectStep.Translation)
                        this._project.ReachedStep = TranslationProjectStep.Translation;
                    if (this._job != null)
                    {
                        reopened = TranslationRunner.Reopen(this._job, this._jobFiles.Values, glossary);
                        this._job.Glossary = TranslationJsonFile.Clone(glossary);
                        foreach (string fileId in this._job.FileIds)
                            this._jobStore.SaveFile(this._job.Id, this._jobFiles[fileId]);
                        this._jobStore.Save(this._job);
                    }
                    this._store.Save(this._project);
                    approved = true;
                }
            }

            if (approved)
            {
                this.AppendLog(AppText.F(TEXT_PREFIX + ".glossaryApproved", this._project.Glossary.Revision));
                if (reopened.HasValue)
                    this.AppendLog(AppText.Count(TEXT_PREFIX + ".glossaryReopened", reopened.Value));
                this.NotifyRecordsChanged();
            }
            this.StartJob();
        }

        /// <summary>
        /// Scrive il glossario corrente in un file .xlsx nella cartella scelta, senza sovrascrivere: la copia si prende sotto il
        /// lock, la scrittura del file sta fuori
        /// </summary>
        /// <param name="folder">Cartella di destinazione</param>
        /// <returns>File scritto</returns>
        public string ExportGlossary(string folder)
        {
            Glossary glossary;
            string name;

            lock (this.StateLock)
            {
                if (this._project == null || this._project.Glossary == null)
                    return "";

                glossary = TranslationJsonFile.Clone(this._project.Glossary);
                name = DisplayName(this._project);
            }

            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');

            string path = new OutputPathAllocator(new string[0]).Reserve(Path.Combine(folder, name + GLOSSARY_EXTENSION));
            using (StagedOutputFile staged = new StagedOutputFile(path))
            {
                GlossaryFile.Export(glossary, staged.TempPath);
                staged.Commit();
            }

            this.AppendLog(AppText.F(TEXT_PREFIX + ".glossaryExported", path));
            return path;
        }

        /// <summary>
        /// Testata del job attivo di un progetto; un job lasciato da una chiusura dell'app torna in uno stato da cui il giro
        /// prosegue, tranne quello che la card sta traducendo. Un job illeggibile o di una versione più recente dà l'eccezione col
        /// motivo
        /// </summary>
        /// <param name="project">Progetto</param>
        /// <returns>Job, null senza job</returns>
        public TranslationJob LoadJob(TranslationProject project)
        {
            if (string.IsNullOrEmpty(project.ActiveJobId))
                return null;

            lock (this.StateLock)
            {
                if (this._job != null && this._job.Id == project.ActiveJobId)
                    return this._job;
            }

            TranslationJob job = this._jobStore.Load(project.ActiveJobId);
            lock (this.StateLock)
            {
                if (!this.IsBusy && job.RecoverAfterCrash())
                    this._jobStore.Save(job);
            }

            return job;
        }

        /// <summary>
        /// True se un job dell'elenco ha file non conclusi, bloccati compresi: la ripresa ricontrolla i sorgenti. Dagli stati dei file
        /// salvati; uno stato illeggibile dà l'eccezione col motivo
        /// </summary>
        /// <param name="job">Job attivo di un progetto dell'elenco</param>
        /// <returns>True se resta lavoro che «Riprendi» può fare</returns>
        public bool HasFilesToTranslate(TranslationJob job)
        {
            foreach (string fileId in job.FileIds)
            {
                TranslationFileStatus status = this._jobStore.LoadFile(job.Id, fileId).TranslationStatus;
                if (status != TranslationFileStatus.Completed)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Avvia il giro col glossario approvato congelato nel job e la modalità della revisione AI, o lo riprende nella fase in
        /// cui si è fermato: traduzione dal primo blocco da fare, revisione dai blocchi da rivedere.
        /// La ripresa ricontrolla i sorgenti, e una traduzione coi soli file bloccati non parte
        /// </summary>
        public void StartJob()
        {
            TranslationJob blocked = null;
            lock (this.StateLock)
            {
                if (!this.CanStartJob)
                    return;

                if (this._job == null)
                {
                    this.CreateJob();
                }
                else
                {
                    this.CheckJobSources();
                    if (this._job.Phase == TranslationJobPhase.Translation && !this._job.FileIds.Exists(id =>
                        this._jobFiles[id].TranslationStatus != TranslationFileStatus.Completed && this._jobFiles[id].TranslationStatus != TranslationFileStatus.Blocked))
                        blocked = this._job;
                }

                if (blocked == null)
                    this.TryRunExclusive(() => this.RunJobAsync().GetAwaiter().GetResult());
            }

            if (blocked != null)
            {
                this.NotifyRecordsChanged();
                this.OnPhaseFinished?.Invoke(new TranslationPhaseResult { Job = blocked, Phase = TranslationJobPhase.Translation });
            }
        }

        /// <summary>
        /// Ricomincia da capo un file bloccato dal sorgente cambiato, finché il giro è nella traduzione: scarta il suo stato, lo
        /// riprepara e ricalcola l'hash. Il job si salva prima dello stato: dopo una chiusura brusca fra i due il file resta bloccato
        /// e si ricomincia di nuovo
        /// </summary>
        /// <param name="fileId">File del job</param>
        public void RestartFile(string fileId)
        {
            string name;
            lock (this.StateLock)
            {
                TranslationJobFileState state;
                if (this._project == null || this._job == null || this.IsBusy || this._job.Phase != TranslationJobPhase.Translation || !this._jobFiles.TryGetValue(fileId, out state) ||
                    state.TranslationStatus != TranslationFileStatus.Blocked || state.BlockedReason != TranslationFilePreparer.REJECTION_SOURCE_CHANGED)
                    return;

                TranslationProjectFile file = this._project.Files.Find(f => f.Id == fileId);
                SubtitleTranslationDocument document = TranslationFilePreparer.Prepare(file, this._project.Styles, this._project.Stretches, false);
                if (document == null)
                {
                    state.BlockedReason = file.RejectedReason;
                    this._jobStore.SaveFile(this._job.Id, state);
                    return;
                }

                // Le tratte perdono le occorrenze del file vecchio e cercano quelle del nuovo, che prende le copie già tradotte
                this._documents[fileId] = document;
                SharedStretchDetector.Detach(this._job.SharedStretches, fileId);
                SharedStretchDetector.Attach(this._job.SharedStretches, fileId, document);
                state = TranslationRunner.CreateFileState(file, document, SharedStretch.MapFile(this._job.SharedStretches, fileId).Keys);
                foreach (SharedStretch stretch in this._job.SharedStretches)
                {
                    if (stretch.Status == TranslationBlockStatus.Done)
                        stretch.CopyInto(state);
                }
                this._jobFiles[fileId] = state;
                this._jobStore.Save(this._job);
                this._jobStore.SaveFile(this._job.Id, state);
                this._store.Save(this._project);
                name = Path.GetFileName(file.Path);
            }

            this.AppendLog(AppText.F(TEXT_PREFIX + ".fileRestarted", name));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Toglie dal job un file bloccato, mai l'ultimo; con tutti gli altri file conclusi la traduzione è finita e il giro
        /// riparte con la revisione AI. Le tratte di cui il file aveva la prima occorrenza passano agli altri file con la revisione
        /// che resta da fare. Il job si salva senza il file prima che il suo stato si cancelli: una chiusura brusca lascia al più
        /// uno stato che nessuno legge
        /// </summary>
        /// <param name="fileId">File del job</param>
        public void ExcludeFile(string fileId)
        {
            string name;
            bool review = false;
            lock (this.StateLock)
            {
                TranslationJobFileState state;
                if (this._project == null || this._job == null || this.IsBusy || this._job.FileIds.Count < 2 || !this._jobFiles.TryGetValue(fileId, out state) ||
                    state.TranslationStatus != TranslationFileStatus.Blocked || this._project.Glossary.Status != GlossaryStatus.Approved)
                    return;

                this._job.FileIds.Remove(fileId);
                this._jobFiles.Remove(fileId);
                Dictionary<int, string> masters = new Dictionary<int, string>();
                foreach (SharedStretch stretch in this._job.SharedStretches)
                    masters[stretch.Index] = stretch.Occurrences[0].FileId;
                SharedStretchDetector.Detach(this._job.SharedStretches, fileId);

                // Una tratta con un'altra prima occorrenza, promossa o nata dal taglio, va a un file che l'aveva solo in copia. Il
                // file prende il blocco di revisione concluso del file tolto; un blocco da fare lo sceglie la revisione AI, anche a
                // revisione AI finita, e allora il giro torna alla revisione AI e la revisione dell'utente va approvata di nuovo. Gli
                // stati dei file si salvano prima del job
                foreach (SharedStretch stretch in this._job.SharedStretches)
                {
                    string before;
                    TranslationJobFileState master = this._jobFiles[stretch.Occurrences[0].FileId];
                    if (masters.TryGetValue(stretch.Index, out before) && before == master.FileId)
                        continue;

                    TranslationReviewBlock previous = state.ReviewBlocks != null ? state.ReviewBlocks.Find(b => b.SharedStretch == stretch.Index) : null;
                    if (master.ReviewBlocks != null && previous != null && previous.Status == TranslationReviewBlockStatus.Done
                        && !master.ReviewBlocks.Exists(b => b.SharedStretch == stretch.Index))
                    {
                        List<int> lines = stretch.Occurrences[0].Ids;
                        master.ReviewBlocks.Add(new TranslationReviewBlock
                        {
                            SharedStretch = stretch.Index,
                            FirstId = lines[0],
                            LastId = lines[lines.Count - 1],
                            Status = previous.Status,
                            CallSeqs = new List<int>(previous.CallSeqs)
                        });
                    }
                    if (this._job.Phase == TranslationJobPhase.UserReview && master.ReviewBlocks != null && TranslationReviewer.AddStretchBlocks(this._job, master))
                    {
                        this._job.Phase = TranslationJobPhase.Review;
                        this._job.Exported = false;
                        if (this._project.ReachedStep > TranslationProjectStep.Review)
                            this._project.ReachedStep = TranslationProjectStep.Review;
                        review = true;
                    }
                    this._jobStore.SaveFile(this._job.Id, master);
                }
                if (this._job.Phase == TranslationJobPhase.Translation && this._job.FileIds.TrueForAll(id => this._jobFiles[id].TranslationStatus == TranslationFileStatus.Completed))
                {
                    this.FinishTranslation();
                    this._job.Status = TranslationJobStatus.Idle;
                    this._job.Pause = null;
                    review = true;
                }
                this._jobStore.Save(this._job);
                this._store.Save(this._project);
                this._jobStore.DeleteFile(this._job.Id, fileId);
                name = Path.GetFileName(this._project.Files.Find(f => f.Id == fileId).Path);
            }

            this.AppendLog(AppText.F(TEXT_PREFIX + ".fileExcluded", name));
            this.NotifyRecordsChanged();
            if (review)
                this.StartJob();
        }

        /// <summary>
        /// Righe della griglia della revisione di un file: tempi, originale, traduzione, stato e avvisi di ogni battuta da tradurre
        /// </summary>
        /// <param name="fileId">File del job</param>
        /// <returns>Righe in ordine di tempo, vuote senza job o senza il documento del file</returns>
        public List<TranslationReviewRow> GetReviewRows(string fileId)
        {
            List<TranslationReviewRow> rows = new List<TranslationReviewRow>();
            lock (this.StateLock)
            {
                TranslationJobFileState state;
                SubtitleTranslationDocument document;
                if (this._job == null || fileId == null || !this._jobFiles.TryGetValue(fileId, out state) || !this._documents.TryGetValue(fileId, out document))
                    return rows;

                Dictionary<int, TranslatedCueState> translations = new Dictionary<int, TranslatedCueState>();
                foreach (TranslatedCueState translation in state.Translations)
                    translations[translation.Id] = translation;
                Dictionary<int, SharedStretch> shared = SharedStretch.MapFile(this._job.SharedStretches, fileId);

                List<SubtitleCue> timed = new List<SubtitleCue>(document.Cues);
                timed.Sort(SubtitleCue.CompareByTime);
                foreach (SubtitleCue cue in timed)
                {
                    if (!cue.Id.HasValue)
                        continue;

                    TranslatedCueState translation;
                    translations.TryGetValue(cue.Id.Value, out translation);
                    SharedStretch stretch;
                    TranslationReviewRow row = new TranslationReviewRow();
                    row.Id = cue.Id.Value;
                    row.StartMs = cue.StartMs;
                    row.EndMs = cue.EndMs;
                    row.Source = cue.CleanText;
                    row.Text = translation != null ? translation.Text : null;
                    row.ToReview = translation == null || translation.Status == TranslatedCueStatus.ToReview;

                    // Motivo dello stato da rivedere in parole dell'utente, il più recente: quello del servizio, una parola sola, o i
                    // codici dell'ultimo rifiuto del validatore; poi la revisione AI non riuscita sul blocco della battuta
                    if (row.ToReview && translation != null)
                    {
                        List<string> codes = translation.GetReasonCodes();
                        row.ToReviewReason = codes.Count > 0 ? string.Join(" · ", codes.ConvertAll(c => AppText.T(TranslationValidationIssue.REASON_KEY_PREFIX + c))) : null;
                    }
                    if (translation != null && translation.NotReviewedReason != null)
                    {
                        string reason = AppText.F(TEXT_PREFIX + ".reviewNotDoneReason", AppText.T(TranslationValidationIssue.REASON_KEY_PREFIX + translation.NotReviewedReason));
                        row.ToReview = true;
                        row.ToReviewReason = row.ToReviewReason != null ? row.ToReviewReason + " · " + reason : reason;
                    }
                    row.Signals = translation != null ? new List<TranslationCueSignal>(translation.Signals) : new List<TranslationCueSignal>();
                    row.CorrectionBefore = translation != null && translation.AiCorrection != null ? translation.AiCorrection.Before : null;
                    row.CorrectionReason = translation != null && translation.AiCorrection != null ? translation.AiCorrection.Explanation : null;
                    row.SharedStretch = shared.TryGetValue(cue.Id.Value, out stretch) ? stretch.Index : null;
                    rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// Salva la modifica a mano di una battuta: stesso validatore delle consegne, avvisi del glossario ricalcolati, segnale
        /// «Modificata a mano» al posto della correzione della revisione AI; un testo con errori non si salva. In una tratta
        /// condivisa la modifica va in tutte le copie. Il lavoro esportato torna da rivedere finché non si esporta di nuovo
        /// </summary>
        /// <param name="fileId">File del job</param>
        /// <param name="id">Battuta</param>
        /// <param name="text">Testo scritto dall'utente</param>
        /// <returns>Messaggi degli errori, vuoto se la modifica è salvata o non cambia il testo</returns>
        public string EditCue(string fileId, int id, string text)
        {
            lock (this.StateLock)
            {
                TranslationJobFileState state;
                SubtitleTranslationDocument document;
                if (!this.CanEditCues || !this._jobFiles.TryGetValue(fileId, out state) || !this._documents.TryGetValue(fileId, out document))
                    return "";

                SubtitleCue cue = null;
                foreach (SubtitleCue candidate in document.Cues)
                {
                    if (candidate.Id == id)
                        cue = candidate;
                }
                CueValidationResult result = new TranslationValidator(document.Format, this._job.Glossary).ValidateEdit(cue, text);
                if (result.HasErrors)
                {
                    List<string> errors = new List<string>();
                    foreach (TranslationValidationIssue issue in result.Issues)
                    {
                        if (issue.Severity == IssueSeverity.Error)
                            errors.Add(AppText.F(issue.UiKey, new List<string>(issue.Args.Values).ToArray()));
                    }
                    return string.Join(" · ", errors);
                }

                TranslatedCueState translation = state.Translations.Find(t => t.Id == id);
                if (translation == null)
                {
                    translation = new TranslatedCueState { Id = id, Status = TranslatedCueStatus.ToReview };
                    state.Translations.Add(translation);
                    state.Translations.Sort((a, b) => a.Id.CompareTo(b.Id));
                }
                if (string.Equals(translation.Text, result.Text, StringComparison.Ordinal))
                    return "";

                translation.SetText(result.Text, result.GetGlossaryWarnings());
                if (!translation.Signals.Contains(TranslationCueSignal.EditedByUser))
                    translation.Signals.Add(TranslationCueSignal.EditedByUser);
                this._job.Exported = false;

                // Il testo è dell'utente: la correzione e la revisione AI non riuscita non lo descrivono più, e la tratta le perde in
                // tutte le copie
                translation.AiCorrection = null;
                translation.NotReviewedReason = null;
                translation.Signals.Remove(TranslationCueSignal.CorrectedByAi);
                SharedStretch stretch;
                if (SharedStretch.MapFile(this._job.SharedStretches, fileId).TryGetValue(id, out stretch))
                {
                    foreach (TranslationJobFileState other in stretch.Spread(state, id, this._jobFiles.Values))
                    {
                        if (other != state)
                            this._jobStore.SaveFile(this._job.Id, other);
                    }
                }
                this._jobStore.SaveFile(this._job.Id, state);
                this._jobStore.Save(this._job);
            }

            this.NotifyRecordsChanged();
            return "";
        }

        /// <summary>
        /// Approva la revisione dell'utente: il progetto raggiunge il passo Esporta
        /// </summary>
        public void ApproveReview()
        {
            lock (this.StateLock)
            {
                if (!this.CanEditCues)
                    return;

                if (this._project.ReachedStep < TranslationProjectStep.Export)
                    this._project.ReachedStep = TranslationProjectStep.Export;
                this._store.Save(this._project);
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Righe della griglia del passo Esporta: nome d'uscita voluto con la barra Uscita, battute tradotte e senza testo tradotto
        /// </summary>
        /// <returns>Righe nell'ordine del job, vuote senza job</returns>
        public List<TranslationExportRow> GetExportRows()
        {
            List<TranslationExportRow> rows = new List<TranslationExportRow>();
            lock (this.StateLock)
            {
                if (this._project == null || this._job == null)
                    return rows;

                string folder = this.GetBatchOutputFolder();
                foreach (string fileId in this._job.FileIds)
                {
                    TranslationJobFileState state = this._jobFiles[fileId];
                    TranslationProjectFile file = this._project.Files.Find(f => f.Id == fileId);
                    TranslationExportRow row = new TranslationExportRow();
                    row.FileId = fileId;
                    row.FileName = Path.GetFileName(file.Path);
                    row.OutputPath = TranslationExporter.GetOutputPath(file.Path, this._project.SourceLanguage, this._project.TargetLanguage, folder);
                    row.Translated = state.Translations.FindAll(t => t.Text != null).Count;
                    row.Untranslated = state.Cues.Count - row.Translated;
                    row.Error = this._documents.ContainsKey(fileId) ? "" : AppText.T(TEXT_PREFIX + ".fileRejected." + file.RejectedReason);
                    rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// Scrive i sottotitoli tradotti con la barra Uscita, con la card occupata: il file dell'export precedente, non cambiato, si
        /// sovrascrive, un altro nome occupato prende il suffisso prima della lingua; un file non scritto tiene il motivo nella sua
        /// riga e gli altri proseguono. Con tutti i file scritti il lavoro è esportato. Le battute restano modificabili, e un nuovo
        /// export riscrive i file
        /// </summary>
        /// <returns>Righe coi percorsi scritti o il motivo, null se l'export non può partire</returns>
        public List<TranslationExportRow> ExportSubtitles()
        {
            List<TranslationExportRow> rows;
            List<TranslationRunFile> files = new List<TranslationRunFile>();
            List<string> inputs = new List<string>();
            string jobId;
            lock (this.StateLock)
            {
                if (!this.CanEditCues || !string.IsNullOrEmpty(this.OutputFolderError))
                    return null;

                jobId = this._job.Id;
                rows = this.GetExportRows();
                foreach (TranslationProjectFile file in this._project.Files)
                    inputs.Add(file.Path);
                foreach (TranslationExportRow row in rows)
                {
                    SubtitleTranslationDocument document;
                    files.Add(this._documents.TryGetValue(row.FileId, out document) ? new TranslationRunFile(this._jobFiles[row.FileId], document, row.FileName) : null);
                }
                this.BusyState = true;
            }

            this.SetBusy(true, AppText.T(TEXT_PREFIX + ".subtitlesExporting"));
            try
            {
                OutputPathAllocator allocator = new OutputPathAllocator(inputs);
                int written = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (files[i] == null)
                        continue;
                    try
                    {
                        List<string> warnings = new List<string>();
                        rows[i].OutputPath = TranslationExporter.Export(files[i], rows[i].OutputPath, allocator, warnings);
                        lock (this.StateLock)
                        {
                            this._jobStore.SaveFile(jobId, files[i].State);
                        }
                        rows[i].Written = true;
                        written++;
                        this.AppendLog(AppText.F(TEXT_PREFIX + ".subtitleExported", rows[i].FileName, rows[i].OutputPath));
                        foreach (string warning in warnings)
                            this.AppendLog(warning);
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is IOException || ex is UnauthorizedAccessException))
                            throw;
                        rows[i].Error = ex.Message;
                        this.AppendLog(AppText.F(TEXT_PREFIX + ".subtitleNotExported", rows[i].FileName, ex.Message));
                    }
                }

                // Esportato solo con tutti i file scritti
                lock (this.StateLock)
                {
                    this._job.Exported = written == rows.Count;
                    this._jobStore.Save(this._job);
                }
                this.AppendLog(AppText.Count(TEXT_PREFIX + ".subtitlesExported", written));
                return rows;
            }
            finally
            {
                this.SetBusy(false, "");
                this.NotifyRecordsChanged();
            }
        }

        /// <summary>
        /// True se il progetto si può esportare: fermato da riprendere o nella revisione dell'utente, con la card libera e senza
        /// file bloccati, perché l'archivio porta tutti i sorgenti. Gli stati dei file vengono dal job aperto o dal disco; uno stato
        /// illeggibile non si esporta
        /// </summary>
        /// <param name="project">Progetto aperto o dell'elenco</param>
        /// <param name="job">Job attivo del progetto</param>
        /// <returns>True se «Esporta progetto» è attivo</returns>
        public bool CanExportProject(TranslationProject project, TranslationJob job)
        {
            if (project == null || job == null || this.IsBusy ||
                !((job.Status == TranslationJobStatus.Paused) || (job.Phase == TranslationJobPhase.UserReview && job.Status == TranslationJobStatus.Idle)))
                return false;

            lock (this.StateLock)
            {
                if (this._job != null && this._job.Id == job.Id)
                    return !this.HasBlockedFiles;
            }

            try
            {
                return !job.FileIds.Exists(id => this._jobStore.LoadFile(job.Id, id).TranslationStatus == TranslationFileStatus.Blocked);
            }
            catch (Exception ex)
            {
                if (!(ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException))
                    throw;
                return false;
            }
        }

        /// <summary>
        /// Scrive l'archivio del progetto nella cartella scelta, con la card occupata durante la scrittura
        /// </summary>
        /// <param name="project">Progetto aperto o dell'elenco</param>
        /// <param name="folder">Cartella di destinazione</param>
        /// <returns>File scritto, vuoto se il progetto non si può esportare</returns>
        public string ExportProject(TranslationProject project, string folder)
        {
            lock (this.StateLock)
            {
                if (!this.CanExportProject(project, this.LoadJob(project)))
                    return "";
                this.BusyState = true;
            }

            this.SetBusy(true, AppText.T(TEXT_PREFIX + ".archiveExporting"));
            try
            {
                string path = this._archive.Export(this._store.Load(project.Id), folder, DisplayName(project));
                this.AppendLog(AppText.F(TEXT_PREFIX + ".projectExported", path));
                return path;
            }
            finally
            {
                this.SetBusy(false, "");
                this.NotifyRecordsChanged();
            }
        }

        /// <summary>
        /// Controlla un archivio prima dell'import: il primo controllo che fallisce dà l'eccezione col motivo
        /// </summary>
        /// <param name="zipPath">Archivio scelto</param>
        /// <returns>Contenuto controllato, col progetto già presente se c'è</returns>
        public TranslationArchiveInspection InspectArchive(string zipPath)
        {
            return this._archive.Inspect(zipPath);
        }

        /// <summary>
        /// Importa un archivio dall'elenco dei progetti: sorgenti nella cartella scelta, progetto e job nella configurazione
        /// </summary>
        /// <param name="zipPath">Archivio scelto</param>
        /// <param name="sourceFolder">Cartella dei sorgenti</param>
        /// <param name="replaceExisting">True se l'utente ha confermato la sostituzione del progetto presente</param>
        /// <returns>Progetto importato, null con la card occupata o un progetto aperto</returns>
        public TranslationProject ImportProject(string zipPath, string sourceFolder, bool replaceExisting)
        {
            lock (this.StateLock)
            {
                if (this.IsBusy || this._project != null)
                    return null;
                this.BusyState = true;
            }

            this.SetBusy(true, AppText.T(TEXT_PREFIX + ".archiveImporting"));
            try
            {
                TranslationProject project = this._archive.Import(zipPath, sourceFolder, replaceExisting);
                this.AppendLog(AppText.F(TEXT_PREFIX + ".projectImported", DisplayName(project), sourceFolder));
                return project;
            }
            finally
            {
                this.SetBusy(false, "");
                this.NotifyRecordsChanged();
            }
        }

        /// <summary>
        /// Stima della traduzione prima dell'approvazione del glossario, che avvia il giro, per caratteri come le chiamate B la
        /// manderebbero, con le tratte condivise contate una volta sola e la cache come la usa l'accesso configurato
        /// </summary>
        /// <returns>Stima, null col job già creato, senza glossario o con file non preparati</returns>
        public TranslationEstimate EstimateTranslation()
        {
            lock (this.StateLock)
            {
                if (this._project == null || this._job != null || this._project.Glossary == null || this._documents.Count != this._project.Files.Count)
                    return null;

                AiConnectionMode accessMode = this.AccessMode;
                string key = this._project.Id + "|" + this._project.UpdatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + accessMode;
                if (this._translationEstimate != null && this._translationEstimateKey == key)
                    return this._translationEstimate;

                // Tratte e stati dei file come li crea l'avvio del giro, senza salvarli
                List<SharedStretch> stretches = SharedStretchDetector.Detect(this.GetDocuments());
                List<TranslationRunFile> files = new List<TranslationRunFile>();
                foreach (TranslationProjectFile file in this._project.Files)
                    files.Add(new TranslationRunFile(TranslationRunner.CreateFileState(file, this._documents[file.Id], SharedStretch.MapFile(stretches, file.Id).Keys), this._documents[file.Id],
                        Path.GetFileName(file.Path)));

                this._translationEstimate = TranslationRunner.Estimate(this._project.Glossary, this._project.Context, files, stretches, this._project.Options.ReviewMode, accessMode);
                this._translationEstimateKey = key;
                return this._translationEstimate;
            }
        }

        /// <summary>
        /// Righe della griglia del passo Traduzione: stato, blocchi, battute, segnali e token per file del job
        /// </summary>
        /// <returns>Righe nell'ordine del job, vuote senza job</returns>
        public List<TranslationJobFileRow> GetTranslationRows()
        {
            List<TranslationJobFileRow> rows = new List<TranslationJobFileRow>();
            lock (this.StateLock)
            {
                if (this._project == null || this._job == null)
                    return rows;

                foreach (string fileId in this._job.FileIds)
                {
                    TranslationJobFileState state = this._jobFiles[fileId];
                    TranslationJobFileRow row = new TranslationJobFileRow();
                    row.FileId = fileId;
                    row.FileName = Path.GetFileName(this._project.Files.Find(f => f.Id == fileId).Path);
                    row.Status = state.TranslationStatus;
                    row.BlockedReason = state.BlockedReason;
                    row.CuesTotal = state.Cues.Count;
                    foreach (TranslationBlock block in state.Blocks)
                    {
                        if (block.Status == TranslationBlockStatus.Split)
                            continue;
                        row.BlocksTotal++;
                        if (block.Status == TranslationBlockStatus.Done)
                            row.BlocksDone++;
                    }

                    foreach (TranslatedCueState translation in state.Translations)
                    {
                        if (translation.Status == TranslatedCueStatus.Accepted)
                        {
                            row.CuesDone++;
                        }
                        else
                        {
                            row.ToReview++;
                        }

                        if (translation.Signals.Contains(TranslationCueSignal.Retried))
                            row.Retried++;
                    }

                    // Token reali dal registro: una misura che manca su tutte le righe del file resta non disponibile
                    foreach (TranslationCallRecord call in this._jobCalls)
                    {
                        if (call.FileId != fileId || call.Usage == null)
                            continue;
                        if (call.Usage.InputTokens.HasValue)
                            row.InputTokens = (row.InputTokens ?? 0) + call.Usage.InputTokens.Value;
                        if (call.Usage.CachedTokens.HasValue)
                            row.CachedTokens = (row.CachedTokens ?? 0) + call.Usage.CachedTokens.Value;
                        if (call.Usage.OutputTokens.HasValue)
                            row.OutputTokens = (row.OutputTokens ?? 0) + call.Usage.OutputTokens.Value;
                    }
                    rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// Righe del registro delle chiamate del job attivo
        /// </summary>
        /// <returns>Chiamate in ordine di registro</returns>
        public List<TranslationCallRecord> GetJobCalls()
        {
            lock (this.StateLock)
            {
                return new List<TranslationCallRecord>(this._jobCalls);
            }
        }

        /// <summary>
        /// Chiamate del giro scartate da Stop, limite d'uso o chiusura: quelle che nessuna unità salvata riporta, cioè un blocco
        /// tradotto o diviso, la diagnosi o la correzione di un blocco della revisione.
        /// Durante il giro contano solo quelle prima dell'ultima chiamata salvata del file, perché l'unità in corso non è ancora
        /// salvata
        /// </summary>
        /// <returns>Numeri delle chiamate scartate</returns>
        public HashSet<int> GetDiscardedCalls()
        {
            HashSet<int> discarded = new HashSet<int>();
            lock (this.StateLock)
            {
                if (this._job == null)
                    return discarded;

                Dictionary<string, int> lastSaved = new Dictionary<string, int>(StringComparer.Ordinal);
                HashSet<int> saved = new HashSet<int>();
                foreach (SharedStretch stretch in this._job.SharedStretches)
                    saved.UnionWith(stretch.CallSeqs);
                foreach (TranslationJobFileState state in this._jobFiles.Values)
                {
                    List<int> seqs = new List<int>();
                    foreach (TranslationBlock block in state.Blocks)
                    {
                        if (block.Status != TranslationBlockStatus.Pending)
                            seqs.AddRange(block.CallSeqs);
                    }
                    foreach (TranslationReviewBlock block in state.ReviewBlocks ?? new List<TranslationReviewBlock>())
                        seqs.AddRange(block.CallSeqs);

                    int last = 0;
                    foreach (int seq in seqs)
                    {
                        saved.Add(seq);
                        last = Math.Max(last, seq);
                    }
                    lastSaved[state.FileId] = last;
                }

                bool running = this._job.Status == TranslationJobStatus.Running;
                foreach (TranslationCallRecord call in this._jobCalls)
                {
                    if (saved.Contains(call.CallSeq))
                        continue;
                    int fileLast;
                    if (running && (call.FileId == null || !lastSaved.TryGetValue(call.FileId, out fileLast) || call.CallSeq > fileLast))
                        continue;
                    discarded.Add(call.CallSeq);
                }
            }

            return discarded;
        }

        /// <summary>
        /// Righe del registro delle chiamate del progetto aperto
        /// </summary>
        /// <returns>Chiamate in ordine di registro</returns>
        public List<TranslationCallRecord> GetCalls()
        {
            lock (this.StateLock)
            {
                return new List<TranslationCallRecord>(this._calls);
            }
        }

        /// <summary>
        /// Nome del progetto per elenchi, log e file, con un ripiego per il progetto senza nome
        /// </summary>
        /// <param name="project">Progetto</param>
        /// <returns>Nome da mostrare</returns>
        public static string DisplayName(TranslationProject project)
        {
            return string.IsNullOrEmpty(project.Name) ? AppText.F(TEXT_PREFIX + ".unnamedProject", project.CreatedAt.ToLocalTime().ToString("g")) : project.Name;
        }

        #endregion

        #region Metodi protected

        /// <summary>
        /// Crea la riga e il file del progetto per un sottotitolo scelto
        /// </summary>
        /// <param name="inputPath">Percorso del file</param>
        /// <param name="size">Dimensione</param>
        /// <returns>Riga del file</returns>
        protected override TranslationFileRecord CreateRecord(string inputPath, long size)
        {
            // Un'estensione che non è un sottotitolo resta nella riga come rifiuto, alla preparazione
            SubtitleTranslationFormat format;
            TranslationFilePreparer.TryGetFormat(inputPath, out format);

            TranslationProjectFile file = new TranslationProjectFile();
            file.Id = "f" + this._project.NextFileNumber.ToString(CultureInfo.InvariantCulture);
            file.Path = inputPath;
            file.Format = format;
            file.SizeBytes = size;
            file.AddedAt = DateTimeOffset.Now;
            this._project.NextFileNumber++;
            this._project.Files.Add(file);
            return new TranslationFileRecord(file);
        }

        /// <summary>
        /// Prepara i file senza documento, quelli appena aggiunti e quelli rifiutati prima, allinea la tabella degli stili e salva
        /// il progetto
        /// </summary>
        protected override void OnFilesAdded()
        {
            foreach (TranslationFileRecord record in this.Records)
            {
                if (!this._documents.ContainsKey(record.File.Id))
                    this.PrepareFile(record.File, false);
            }

            this.SyncStyles();
            this._store.Save(this._project);
        }

        /// <summary>
        /// Allinea i file del progetto alle righe rimaste, e la tabella degli stili ai file, e salva
        /// </summary>
        protected override void OnRecordsRemoved()
        {
            this._project.Files.RemoveAll(f => !this.Records.Exists(r => ReferenceEquals(r.File, f)));
            foreach (string id in new List<string>(this._documents.Keys))
            {
                if (!this._project.Files.Exists(f => f.Id == id))
                    this._documents.Remove(id);
            }

            this.SyncStyles();
            this._store.Save(this._project);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Analisi del glossario, nel thread dell'operazione: le A1 mancanti, la fusione del codice, la A2 sui conflitti e la
        /// fusione col glossario caricato. Il thread dedicato è il confine fra il worker sincrono e le chiamate asincrone: nessuna
        /// eccezione ne esce, e la card si libera sempre
        /// </summary>
        /// <returns>Task dell'analisi</returns>
        private async Task AnalyzeAsync()
        {
            TranslationProject project = null;
            TranslationAnalysis analysis = null;
            ITranslationModelProvider provider = null;
            bool webSearch = false;

            this.SetBusy(true, AppText.T(TEXT_PREFIX + ".analysisProgress"));
            try
            {
                List<GlossaryEpisode> episodes;
                bool classify;
                lock (this.StateLock)
                {
                    project = this._project;
                    classify = project.Stretches == null;
                    if (project.Analysis == null)
                        project.Analysis = new TranslationAnalysis();
                    analysis = project.Analysis;
                    analysis.Status = TranslationAnalysisStatus.Running;
                    analysis.Stop = null;
                    if (project.ReachedStep < TranslationProjectStep.Glossary)
                        project.ReachedStep = TranslationProjectStep.Glossary;
                    this._phrase = AppText.T(TEXT_PREFIX + ".analysisPreparing");
                    this._detail = "";
                    this._store.Save(project);
                }
                this.NotifyRecordsChanged();

                CancellationToken cancellation = this.OperationCancellationToken;
                provider = this._providerFactory.Create(analysis.WebSearchRejected ? new string[] { TranslationProviderCapabilities.WEB_SEARCH } : null);
                provider.CallProgress += this.HandleCallProgress;
                provider.CallFinished += this.HandleCallFinished;
                webSearch = provider.Capabilities.WebSearch;

                // I tipi prima di tutto il resto: il glossario legge solo le battute da tradurre
                if (classify)
                    await this.ClassifyAsync(provider, cancellation);

                GlossaryBuilder builder = new GlossaryBuilder(project.SourceLanguage, project.TargetLanguage);
                AnalysisPlan plan;
                lock (this.StateLock)
                {
                    episodes = this.GetEpisodes();
                    plan = this.PlanAnalysis(builder, episodes);

                    // Parziali fatti su ingressi che non ci sono più, come contesto o glossario caricato cambiati o il passaggio fra
                    // chiamata unica e una per file: si scartano e la ripresa li rifà
                    analysis.Partials.RemoveAll(p => !plan.Units.Exists(u =>
                        (u.Count == 1 ? u[0].FileId : TranslationSessionKey.NO_FILE) == p.FileId && plan.Fingerprints[plan.Units.IndexOf(u)] == p.InputSha256));
                }

                // Stima delle A1 rifatta sui tipi appena riconosciuti, che tolgono le battute da non tradurre
                if (classify)
                {
                    string tools = builder.CreateTools(webSearch).ToJsonString();
                    long tokens = 0;
                    for (int i = 0; i < plan.Units.Count; i++)
                        tokens += TranslationRequestBuilder.EstimateTokens(builder.AnalysisInstructions + tools + plan.Messages[i]);
                    this.AppendLog(AppText.F(TEXT_PREFIX + ".analysisEstimateAfterKinds", plan.Units.Count, tokens.ToString("N0", CultureInfo.CurrentCulture)));
                }

                for (int i = 0; i < plan.Units.Count; i++)
                {
                    List<GlossaryEpisode> unit = plan.Units[i];
                    string fileId = unit.Count == 1 ? unit[0].FileId : TranslationSessionKey.NO_FILE;
                    if (analysis.Partials.Exists(p => p.FileId == fileId))
                        continue;

                    cancellation.ThrowIfCancellationRequested();
                    string fileName = unit.Count == 1 ? unit[0].FileName : AppText.Count(TEXT_PREFIX + ".filesCount", unit.Count);
                    lock (this.StateLock)
                    {
                        this._phrase = AppText.F(TEXT_PREFIX + ".analysisEpisode", i + 1, plan.Units.Count);
                        this._detail = project.Models.Analysis + (webSearch ? " · " + AppText.T(TEXT_PREFIX + ".webSearchActive") : "");
                    }
                    this.ReportProgress(i, plan.Units.Count, fileName);
                    this.AppendLog(AppText.F(TEXT_PREFIX + ".analysisStarted", fileName));

                    Glossary partial = (await this.RunDeliveryCallsAsync(provider, TranslationCallKind.A1, fileId, plan.Cues[i], builder.AnalysisInstructions, plan.Messages[i],
                        builder.CreateTools(true), false, (response, input) => builder.ReadResponse(response, input, null), cancellation)).Glossary;

                    // La ricerca rifiutata resta spenta per il resto dell'analisi, anche alla ripresa
                    if (webSearch && !provider.Capabilities.WebSearch)
                    {
                        webSearch = false;
                        this.NoteWebSearchRejected(analysis);
                    }

                    lock (this.StateLock)
                    {
                        analysis.Partials.Add(new GlossaryPartial { FileId = fileId, Glossary = partial, InputSha256 = plan.Fingerprints[i] });
                        this._store.Save(project);
                    }
                    this.AppendLog(AppText.F(TEXT_PREFIX + ".partialDelivered", fileName, partial.Terms.Count, partial.Characters.Count));
                }

                // Fusione dei parziali in ordine di progetto, poi A2 solo sui conflitti
                List<GlossaryPartial> partials = new List<GlossaryPartial>();
                foreach (List<GlossaryEpisode> unit in plan.Units)
                {
                    string fileId = unit.Count == 1 ? unit[0].FileId : TranslationSessionKey.NO_FILE;
                    partials.Add(analysis.Partials.Find(p => p.FileId == fileId));
                }

                GlossaryMergeResult merge = builder.MergePartials(partials, episodes, plan.Candidates);
                Glossary merged = merge.Agreed;
                int calls = plan.Units.Count;
                if (merge.HasConflicts)
                {
                    int conflicts = merge.TermConflicts.Count + merge.CharacterConflicts.Count + merge.AddressingConflicts.Count;
                    calls++;
                    lock (this.StateLock)
                    {
                        this._phrase = AppText.T(TEXT_PREFIX + ".analysisMerge");
                        this._detail = project.Models.Analysis + " · " + AppText.Count(TEXT_PREFIX + ".conflictsCount", conflicts);
                    }
                    this.ReportProgress(plan.Units.Count, calls, AppText.T(TEXT_PREFIX + ".analysisMerge"));
                    this.AppendLog(AppText.Count(TEXT_PREFIX + ".mergeStarted", conflicts));

                    string message = builder.CreateMergeMessage(project.Context, plan.Loaded, plan.Candidates, episodes, merge, partials);
                    Glossary resolution = (await this.RunDeliveryCallsAsync(provider, TranslationCallKind.A2, TranslationSessionKey.NO_FILE, null, builder.MergeInstructions, message,
                        builder.CreateTools(false), false, (response, input) => builder.ReadResponse(response, input, merge), cancellation)).Glossary;
                    int outside;
                    merged = builder.ApplyResolution(merge, resolution, out outside);
                    if (outside > 0)
                        this.AppendLog(AppText.Count(TEXT_PREFIX + ".mergeOutside", outside));
                }

                lock (this.StateLock)
                {
                    List<GlossaryDiscard> discarded = new List<GlossaryDiscard>();
                    Glossary glossary = builder.MergeWithImported(merged, project.Glossary, discarded);
                    glossary.SourceLanguage = project.SourceLanguage;
                    glossary.TargetLanguage = project.TargetLanguage;
                    builder.SetEpisodes(glossary, episodes, true);
                    project.Glossary = glossary;
                    analysis.DiscardedForImport = discarded;
                    analysis.Status = TranslationAnalysisStatus.Completed;
                    analysis.CompletedAt = DateTimeOffset.Now;
                    this._store.Save(project);
                }

                // Tutte le chiamate fatte: la barra arriva in fondo coi conteggi veri
                this.ReportProgress(calls, calls, "");
                int entries = analysis.DiscardedForImport.FindAll(d => d.Kind == GlossaryEntryKind.Term || d.Kind == GlossaryEntryKind.Character || d.Kind == GlossaryEntryKind.Addressing).Count;
                if (entries > 0)
                    this.AppendLog(AppText.Count(TEXT_PREFIX + ".discardedForImport", entries));
                this.AppendLog(AppText.F(TEXT_PREFIX + ".glossaryProposed", project.Glossary.Terms.Count, project.Glossary.Characters.Count, project.Glossary.Addressing.Count));
            }
            catch (Exception ex)
            {
                // Le eccezioni che non sono dell'accesso restano errori del servizio: il worker non deve chiudere l'app
                TranslationStop stop = TranslationStop.Create(ex, this.StopRequested || this.OperationCancellationToken.IsCancellationRequested);

                // L'accesso può aver spento la ricerca web prima dell'errore: alla ripresa resta spenta
                if (provider != null && webSearch && !provider.Capabilities.WebSearch)
                    this.NoteWebSearchRejected(analysis);

                if (analysis != null)
                {
                    lock (this.StateLock)
                    {
                        analysis.Status = TranslationAnalysisStatus.Stopped;
                        analysis.Stop = stop;
                    }

                    // Un salvataggio non riuscito non deve chiudere l'app: la fermata resta in memoria e all'apertura diventa Crash
                    try
                    {
                        lock (this.StateLock)
                        {
                            this._store.Save(project);
                        }
                    }
                    catch (Exception saveError)
                    {
                        this.AppendLog(AppText.F(TEXT_PREFIX + ".projectNotSaved", saveError.Message));
                    }
                }
                this.AppendLog(stop.Reason == TranslationStopReason.Stop ? AppText.T(TEXT_PREFIX + ".analysisStopped") : AppText.F(TEXT_PREFIX + ".analysisFailed", stop.Message));
            }
            finally
            {
                this.SetBusy(false, "");
                this.NotifyRecordsChanged();
                if (analysis != null)
                    this.OnAnalysisFinished?.Invoke(analysis);
            }
        }

        /// <summary>
        /// Crea il job col glossario approvato congelato, i modelli, la modalità della revisione AI, le tratte fra i file e lo stato
        /// di ogni file; chiamato sotto StateLock
        /// </summary>
        private void CreateJob()
        {
            TranslationJob job = this._jobStore.Create(this._project.Id);
            job.AccessMode = this.AccessMode;
            job.Models = new TranslationPhaseModels
            {
                Analysis = this._project.Models.Analysis,
                Translation = this._project.Models.Translation,
                Review = this._project.Models.Review
            };
            job.ReasoningEfforts = new TranslationPhaseModels
            {
                Analysis = this._project.ReasoningEfforts.Analysis,
                Translation = this._project.ReasoningEfforts.Translation,
                Review = this._project.ReasoningEfforts.Review
            };
            job.Glossary = TranslationJsonFile.Clone(this._project.Glossary);
            job.GlossaryRevisionAtStart = this._project.Glossary.Revision;
            job.ReviewMode = this._project.Options.ReviewMode;
            job.Status = TranslationJobStatus.Idle;
            job.SharedStretches = SharedStretchDetector.Detect(this.GetDocuments());
            foreach (TranslationProjectFile file in this._project.Files)
            {
                job.FileIds.Add(file.Id);
                this._jobFiles[file.Id] = TranslationRunner.CreateFileState(file, this._documents[file.Id], SharedStretch.MapFile(job.SharedStretches, file.Id).Keys);
            }

            // Prima la testata, che dice il progetto del job, poi gli stati dei file e infine il progetto: una chiusura brusca
            // lascia al più un job che il progetto non riporta, e la cancellazione del progetto lo trova dalla testata
            this._jobStore.Save(job);
            foreach (string fileId in job.FileIds)
                this._jobStore.SaveFile(job.Id, this._jobFiles[fileId]);

            this._job = job;
            this._project.ActiveJobId = job.Id;
            this._project.JobIds.Add(job.Id);
            this._store.Save(this._project);
        }

        /// <summary>
        /// Riconoscimento dei tipi del progetto, primo passo dell'analisi: sui file letti
        /// senza tipi il codice decide le unità senza lettere nella scrittura del progetto e il modello dell'analisi le altre in una
        /// chiamata A0; poi stili e tratte prendono i tipi, i file si ripreparano e il glossario prende le «Maiuscole esatte» che
        /// mancano
        /// </summary>
        /// <param name="provider">Provider del modello</param>
        /// <param name="cancellation">Annullamento dell'operazione</param>
        /// <returns>Task del riconoscimento</returns>
        private async Task ClassifyAsync(ITranslationModelProvider provider, CancellationToken cancellation)
        {
            SubtitleKindClassifier classifier;
            SubtitleKindPlan plan;
            lock (this.StateLock)
            {
                classifier = new SubtitleKindClassifier(this._project.SourceLanguage);
                plan = classifier.Plan(this.GetDocuments(), this._project.Context);
                this._phrase = AppText.T(TEXT_PREFIX + ".classificationProgress");
                this._detail = plan.Message != null ? this._project.Models.Analysis : "";
            }
            this.NotifyRecordsChanged();

            if (plan.Message != null)
            {
                await this.RunDeliveryCallsAsync(provider, TranslationCallKind.A0, TranslationSessionKey.NO_FILE, null, classifier.Instructions, plan.Message,
                    new JsonArray(plan.Tool.DeepClone()), true, (response, input) => classifier.ReadResponse(response, input, plan), cancellation);
            }

            lock (this.StateLock)
            {
                this._project.Stretches = plan.Apply(this._project.Styles);
                foreach (TranslationProjectFile file in this._project.Files)
                    this.PrepareFile(file, false);
                if (this._project.Glossary != null)
                    new GlossaryBuilder(this._project.SourceLanguage, this._project.TargetLanguage).SetEpisodes(this._project.Glossary, this.GetEpisodes(), true);
                this._store.Save(this._project);
            }

            // Unità per tipo: una tratta con tratti di tipo diverso conta per ognuno dei suoi tipi
            int[] counts = new int[Enum.GetValues<TranslationStyleKind>().Length];
            foreach (SubtitleKindUnit unit in plan.Units)
            {
                foreach (TranslationStyleKind kind in new HashSet<TranslationStyleKind>(unit.LineKinds ?? new List<TranslationStyleKind> { unit.Kind.Value }))
                    counts[(int)kind]++;
            }
            this.AppendLog(AppText.F(TEXT_PREFIX + ".classificationDone", counts[(int)TranslationStyleKind.Dialogue], counts[(int)TranslationStyleKind.Song],
                counts[(int)TranslationStyleKind.Sign], counts[(int)TranslationStyleKind.NotTranslatable]));
        }

        /// <summary>
        /// Giro del job, nel thread dell'operazione: dalla fase in cui si trova, traduzione e revisione AI si susseguono; si ferma
        /// alla revisione dell'utente o a una fermata, con lo stato del job e il motivo. Nessuna eccezione ne esce, e la card si
        /// libera sempre
        /// </summary>
        /// <returns>Task del giro</returns>
        private async Task RunJobAsync()
        {
            TranslationJob job = null;
            TranslationStop stop = null;
            ITranslationModelProvider provider = null;
            TranslationJobPhase phase = TranslationJobPhase.Translation;

            this.SetBusy(true, AppText.T(TEXT_PREFIX + ".jobProgress"));
            try
            {
                List<TranslationRunFile> files = new List<TranslationRunFile>();
                int parallel;
                string context;
                lock (this.StateLock)
                {
                    job = this._job;
                    phase = job.Phase;
                    job.Status = TranslationJobStatus.Running;
                    job.Pause = null;
                    job.AccessMode = this.AccessMode;
                    parallel = this._project.Options.MaxParallelFiles;
                    context = this._project.Context;
                    foreach (string fileId in job.FileIds)
                    {
                        TranslationJobFileState state = this._jobFiles[fileId];
                        SubtitleTranslationDocument document;
                        if (state.TranslationStatus != TranslationFileStatus.Blocked && this._documents.TryGetValue(fileId, out document))
                            files.Add(new TranslationRunFile(state, document, Path.GetFileName(this._project.Files.Find(f => f.Id == fileId).Path)));
                    }
                    this._jobStore.Save(job);
                }
                this.NotifyRecordsChanged();

                // Un solo provider per tutti i file e tutte le fasi: la pausa dei 429 vale per tutti
                provider = this._providerFactory.Create(job.DisabledCapabilities);
                provider.CallProgress += this.HandleCallProgress;
                provider.CallFinished += this.HandleCallFinished;
                while (stop == null)
                {
                    if (phase == TranslationJobPhase.Translation)
                    {
                        this.AppendLog(AppText.Count(TEXT_PREFIX + ".translationStarted", files.FindAll(f => f.State.TranslationStatus != TranslationFileStatus.Completed).Count, parallel));
                        int stretches;
                        lock (this.StateLock)
                        {
                            stretches = job.SharedStretches.FindAll(s => s.Status == TranslationBlockStatus.Pending).Count;
                        }
                        if (stretches > 0)
                            this.AppendLog(AppText.Count(TEXT_PREFIX + ".sharedStretchesStarted", stretches));
                        TranslationRunner runner = new TranslationRunner(this._jobStore, job, files, this._jobCalls, this.StateLock, context);
                        this.AttachRunner(runner, TEXT_PREFIX + ".jobProgress", TEXT_PREFIX + ".blockOfFile", null);
                        stop = await runner.RunAsync(provider, parallel, this.OperationCancellationToken);
                        if (stop != null)
                            break;

                        bool completed;
                        lock (this.StateLock)
                        {
                            completed = job.FileIds.TrueForAll(id => this._jobFiles[id].TranslationStatus == TranslationFileStatus.Completed);
                            if (completed)
                            {
                                this.FinishTranslation();
                                this._jobStore.Save(job);
                                this._store.Save(this._project);
                            }
                        }
                        if (!completed)
                        {
                            // File bloccati: la traduzione resta da finire, il giro si ferma senza fermata
                            this.AppendLog(AppText.T(TEXT_PREFIX + ".translationBlocked"));
                            this.OnPhaseFinished?.Invoke(new TranslationPhaseResult { Job = job, Phase = TranslationJobPhase.Translation });
                            break;
                        }
                        this.AppendLog(AppText.T(TEXT_PREFIX + ".translationCompleted"));
                        this.OnPhaseFinished?.Invoke(new TranslationPhaseResult { Job = job, Phase = TranslationJobPhase.Translation });
                        phase = job.Phase;
                    }
                    else
                    {
                        // Blocchi scelti una volta e salvati: alla ripresa la scelta non cambia, salvo i blocchi delle tratte passate a un
                        // altro file
                        TranslationReviewer reviewer = new TranslationReviewer(this._jobStore, job, files, this._jobCalls, this.StateLock, context);
                        lock (this.StateLock)
                        {
                            if (this._project.ReachedStep < TranslationProjectStep.Review)
                                this._project.ReachedStep = TranslationProjectStep.Review;
                            foreach (TranslationRunFile file in files)
                                this._jobStore.SaveFile(job.Id, file.State);
                            this._store.Save(this._project);
                        }

                        int blocks = reviewer.BlockCount;
                        if (blocks > 0)
                        {
                            this.AppendLog(AppText.Count(TEXT_PREFIX + ".reviewStarted", blocks));
                            this.AttachRunner(reviewer, TEXT_PREFIX + ".reviewPhrase", TEXT_PREFIX + ".blockOfFile", job.Models.Review);
                            stop = await reviewer.RunAsync(provider, parallel, this.OperationCancellationToken);
                            if (stop != null)
                                break;
                        }

                        // Fase finita solo con tutti i file del job; la conclusione dice le battute che la revisione AI non ha potuto rivedere
                        int corrected = 0;
                        int notReviewed = 0;
                        bool reviewed;
                        lock (this.StateLock)
                        {
                            reviewed = job.FileIds.TrueForAll(id => this._jobFiles[id].ReviewBlocks != null
                                && !this._jobFiles[id].ReviewBlocks.Exists(b => b.Status == TranslationReviewBlockStatus.Pending || b.Status == TranslationReviewBlockStatus.Diagnosed));
                            if (reviewed)
                            {
                                foreach (TranslationRunFile file in files)
                                {
                                    corrected += file.State.Translations.FindAll(t => t.Signals.Contains(TranslationCueSignal.CorrectedByAi)).Count;
                                    notReviewed += file.State.Translations.FindAll(t => t.NotReviewedReason != null).Count;
                                }
                                job.Phase = TranslationJobPhase.UserReview;
                                this._jobStore.Save(job);
                            }
                        }
                        if (!reviewed)
                        {
                            this.AppendLog(AppText.T(TEXT_PREFIX + ".reviewBlocked"));
                            this.OnPhaseFinished?.Invoke(new TranslationPhaseResult { Job = job, Phase = TranslationJobPhase.Review });
                            break;
                        }
                        this.AppendLog(blocks == 0 ? AppText.T(TEXT_PREFIX + ".reviewNothing") : notReviewed > 0
                            ? AppText.Count(TEXT_PREFIX + ".reviewCompletedPartial", notReviewed, AppText.Count(TEXT_PREFIX + ".correctedCount", corrected))
                            : AppText.Count(TEXT_PREFIX + ".reviewCompleted", corrected));
                        this.OnPhaseFinished?.Invoke(new TranslationPhaseResult { Job = job, Phase = TranslationJobPhase.Review, ReviewBlocks = blocks, Corrected = corrected, NotReviewed = notReviewed });
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Le eccezioni che non sono dell'accesso restano errori del servizio: il worker non deve chiudere l'app
                stop = TranslationStop.Create(ex, this.StopRequested || this.OperationCancellationToken.IsCancellationRequested);
            }
            finally
            {
                if (job != null)
                {
                    lock (this.StateLock)
                    {
                        if (provider != null)
                            job.DisabledCapabilities = provider.Capabilities.DisabledCapabilities;
                        // Nella revisione dell'utente il giro aspetta l'utente: un errore dopo il passaggio di fase non lascia il job
                        // fermato
                        phase = job.Phase;
                        bool waiting = phase == TranslationJobPhase.UserReview;
                        job.Status = stop != null && !waiting ? TranslationJobStatus.Paused : TranslationJobStatus.Idle;
                        job.Pause = waiting ? null : stop;
                        this._phrase = "";
                        this._detail = "";

                        // Una fase finita lascia la barra piena; Stop e pausa lasciano il punto raggiunto
                        if (stop == null)
                            this.ProgressState.CurrentPercent = 100;
                    }

                    // Un salvataggio non riuscito non deve chiudere l'app: la fermata resta in memoria e all'apertura diventa Crash
                    try
                    {
                        lock (this.StateLock)
                        {
                            this._jobStore.Save(job);
                        }
                    }
                    catch (Exception saveError)
                    {
                        this.AppendLog(AppText.F(TEXT_PREFIX + ".projectNotSaved", saveError.Message));
                    }

                    if (stop != null)
                    {
                        string name = phase == TranslationJobPhase.Review ? "review" : "translation";
                        this.AppendLog(stop.Reason == TranslationStopReason.Stop ? AppText.T(TEXT_PREFIX + "." + name + "Stopped") : AppText.F(TEXT_PREFIX + "." + name + "Paused", stop.Message));
                    }
                }

                this.SetBusy(false, "");
                this.NotifyRecordsChanged();
                if (job != null && stop != null)
                    this.OnPhaseFinished?.Invoke(new TranslationPhaseResult { Job = job, Phase = phase, Stop = stop });
            }
        }

        /// <summary>
        /// Lega una fase al log, alla pagina e alle barre: l'operazione della fase, la Globale con le unità fatte, la File con l'unità
        /// del file, o la tratta ripetuta nel primo giro della traduzione; la revisione mostra anche blocco e modello nell'attesa
        /// </summary>
        /// <param name="runner">Fase del giro</param>
        /// <param name="operationKey">Chiave del testo dell'operazione della fase</param>
        /// <param name="unitKey">Chiave del testo dell'unità del file</param>
        /// <param name="model">Modello per la riga dell'attesa, null fuori dalla revisione</param>
        private void AttachRunner(TranslationPhaseRunner runner, string operationKey, string unitKey, string model)
        {
            lock (this.StateLock)
            {
                this.ProgressState.Operation = AppText.T(operationKey);
                this.ProgressState.CurrentStatus = this.ProgressState.Operation;
            }
            runner.Log += this.AppendLog;
            runner.Changed += this.NotifyRecordsChanged;
            runner.Progress += (done, total, fileName, number, count) =>
            {
                lock (this.StateLock)
                {
                    this.ProgressState.Total = total;
                    this.ProgressState.Completed = done;
                    this.ProgressState.CurrentIndex = done;
                    this.ProgressState.GlobalPercent = total > 0 ? done * 100 / total : 0;
                    this.ProgressState.GlobalIndeterminate = false;
                    TranslationRunner translation = runner as TranslationRunner;
                    this.ProgressState.CurrentEpisode = AppText.F(translation != null && translation.SharedStretchPass ? TEXT_PREFIX + ".stretchOfFile" : unitKey, number, count, fileName);
                    this.ProgressState.CurrentPercent = count > 0 ? (number - 1) * 100 / count : 0;
                    this.ProgressState.CurrentIndeterminate = false;
                    if (model != null)
                    {
                        this._phrase = AppText.T(TEXT_PREFIX + ".reviewPhrase");
                        this._detail = AppText.F(TEXT_PREFIX + ".reviewDetail", number, count, fileName, model);
                    }
                }
                this.NotifyProgressChanged();
            };
        }

        /// <summary>
        /// Chiude la traduzione conclusa: il giro passa alla revisione AI, e il passo raggiunto segue la fase; chiamato sotto
        /// StateLock
        /// </summary>
        private void FinishTranslation()
        {
            this._job.Phase = TranslationJobPhase.Review;
            if (this._project.ReachedStep < TranslationProjectStep.Review)
                this._project.ReachedStep = TranslationProjectStep.Review;
        }

        /// <summary>
        /// Rilegge i sorgenti di tutti i file del job, conclusi compresi: un file mancante o cambiato diventa bloccato col motivo,
        /// in ogni fase, e il giro non va oltre finché resta; uno che torna con l'hash del job riparte nella traduzione, o torna
        /// concluso dopo la traduzione; chiamato sotto StateLock
        /// </summary>
        private void CheckJobSources()
        {
            foreach (string fileId in this._job.FileIds)
            {
                TranslationJobFileState state = this._jobFiles[fileId];
                TranslationProjectFile file = this._project.Files.Find(f => f.Id == fileId);
                this.PrepareFile(file, true);
                if (!this._documents.ContainsKey(fileId))
                {
                    if (state.TranslationStatus == TranslationFileStatus.Blocked && state.BlockedReason == file.RejectedReason)
                        continue;
                    state.TranslationStatus = TranslationFileStatus.Blocked;
                    state.BlockedReason = file.RejectedReason;
                }
                else if (state.TranslationStatus == TranslationFileStatus.Blocked)
                {
                    state.TranslationStatus = this._job.Phase != TranslationJobPhase.Translation ? TranslationFileStatus.Completed
                        : state.Blocks.Exists(b => b.Status == TranslationBlockStatus.Done) ? TranslationFileStatus.InProgress : TranslationFileStatus.Pending;
                    state.BlockedReason = "";
                }
                else
                {
                    continue;
                }

                this._jobStore.SaveFile(this._job.Id, state);
            }
        }

        /// <summary>
        /// Ferma e salva un'analisi rimasta in corso da una chiusura dell'app; chiamato sotto StateLock, mai sul progetto aperto
        /// </summary>
        /// <param name="project">Progetto letto da disco</param>
        private void StopStaleAnalysis(TranslationProject project)
        {
            if (project.Analysis == null || project.Analysis.Status != TranslationAnalysisStatus.Running)
                return;

            project.Analysis.Status = TranslationAnalysisStatus.Stopped;
            project.Analysis.Stop = new TranslationStop { Reason = TranslationStopReason.Crash };
            this._store.Save(project);
        }

        /// <summary>
        /// Dimentica il job del progetto chiuso; chiamato sotto StateLock
        /// </summary>
        private void ClearJob()
        {
            this._job = null;
            this._jobFiles.Clear();
            this._jobCalls.Clear();
        }

        /// <summary>
        /// Chiamate A1 dell'analisi: una sola con tutti i file quando l'ingresso stimato sta sotto la soglia, altrimenti una per
        /// file, coi candidati ricorrenti e il glossario caricato, e l'impronta degli ingressi di ognuna; chiamato sotto StateLock
        /// </summary>
        /// <param name="builder">Richieste del glossario</param>
        /// <param name="episodes">File preparati del progetto</param>
        /// <returns>Unità, messaggi e battute di ogni A1</returns>
        private AnalysisPlan PlanAnalysis(GlossaryBuilder builder, List<GlossaryEpisode> episodes)
        {
            TranslationProject project = this._project;
            AnalysisPlan plan = new AnalysisPlan();
            List<KeyValuePair<string, IReadOnlyList<SubtitleCue>>> sources = new List<KeyValuePair<string, IReadOnlyList<SubtitleCue>>>();
            foreach (GlossaryEpisode episode in episodes)
                sources.Add(new KeyValuePair<string, IReadOnlyList<SubtitleCue>>(episode.FileId, episode.Cues));
            plan.Candidates = new RecurringTermExtractor().Extract(sources, project.SourceLanguage);

            // Il glossario caricato arriva al modello come già deciso: solo le sue voci, con i testi del file
            if (project.ImportedGlossary != null && project.Glossary != null)
            {
                plan.Loaded = new Glossary();
                plan.Loaded.Series = project.Glossary.Series;
                plan.Loaded.Tone = project.Glossary.Tone;
                plan.Loaded.RegisterRules = project.Glossary.RegisterRules;
                plan.Loaded.Terms.AddRange(project.Glossary.Terms.FindAll(t => t.Origin == GlossaryOrigin.Import));
                plan.Loaded.Characters.AddRange(project.Glossary.Characters.FindAll(c => c.Origin == GlossaryOrigin.Import));
                plan.Loaded.Addressing.AddRange(project.Glossary.Addressing.FindAll(a => a.Origin == GlossaryOrigin.Import));
            }

            string allMessage = builder.CreateAnalysisMessage(project.Context, plan.Loaded, plan.Candidates, episodes, episodes);
            if (episodes.Count == 1 || TranslationRequestBuilder.EstimateTokens(builder.AnalysisInstructions + allMessage) < GlossaryBuilder.SINGLE_ANALYSIS_TOKENS)
            {
                plan.Units.Add(episodes);
                plan.Messages.Add(allMessage);
            }
            else
            {
                foreach (GlossaryEpisode episode in episodes)
                {
                    List<GlossaryEpisode> unit = new List<GlossaryEpisode> { episode };
                    plan.Units.Add(unit);
                    plan.Messages.Add(builder.CreateAnalysisMessage(project.Context, plan.Loaded, plan.Candidates, episodes, unit));
                }
            }

            foreach (string message in plan.Messages)
                plan.Fingerprints.Add(TranslationFilePreparer.ComputeSha256(Encoding.UTF8.GetBytes(builder.AnalysisInstructions + "\n" + message)));

            foreach (List<GlossaryEpisode> unit in plan.Units)
            {
                int cues = 0;
                foreach (GlossaryEpisode episode in unit)
                {
                    foreach (SubtitleCue cue in episode.Cues)
                    {
                        if (cue.Id.HasValue)
                            cues++;
                    }
                }
                plan.Cues.Add(cues);
            }

            return plan;
        }

        /// <summary>
        /// Evento dello stream di una chiamata: la chiamata diventa quella mostrata nell'attesa, e la pagina si aggiorna solo se il
        /// testo cambia
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        /// <param name="stream">Stato dello stream</param>
        private void HandleCallProgress(TranslationModelRequest request, AiStreamProgress stream)
        {
            string text = stream.Describe();
            bool changed;
            lock (this.StateLock)
            {
                LiveCall call;
                if (!this._liveCalls.TryGetValue(request, out call))
                {
                    TranslationProjectFile file = this._project != null ? this._project.Files.Find(f => f.Id == request.FileId) : null;
                    call = new LiveCall(file != null ? Path.GetFileName(file.Path) : "", request.Request.Model);
                    this._liveCalls[request] = call;
                }

                changed = !ReferenceEquals(this._liveLatest, call) || !string.Equals(call.Text, text, StringComparison.Ordinal);
                call.Text = text;
                call.UpdatedAt = DateTime.UtcNow;
                this._liveLatest = call;
                this.ProgressState.CurrentStatus = this.DescribeLiveCall();
            }

            if (changed)
                this.NotifyProgressChanged();
        }

        /// <summary>
        /// Fine di una chiamata: se era quella mostrata, l'attesa passa alla chiamata in corso con l'evento più recente; in ogni caso
        /// la riga si aggiorna, perché il nome del file compare solo con più chiamate in corso
        /// </summary>
        /// <param name="request">Chiamata della traduzione</param>
        private void HandleCallFinished(TranslationModelRequest request)
        {
            lock (this.StateLock)
            {
                LiveCall call;
                if (!this._liveCalls.Remove(request, out call))
                    return;

                if (ReferenceEquals(this._liveLatest, call))
                {
                    this._liveLatest = null;
                    foreach (LiveCall running in this._liveCalls.Values)
                    {
                        if (this._liveLatest == null || running.UpdatedAt > this._liveLatest.UpdatedAt)
                            this._liveLatest = running;
                    }
                }

                this.ProgressState.CurrentStatus = this._liveLatest != null ? this.DescribeLiveCall() : this.ProgressState.Operation;
            }

            this.NotifyProgressChanged();
        }

        /// <summary>
        /// Testo della chiamata mostrata: evento dello stream e modello, col nome del file quando le chiamate in corso sono più di
        /// una; chiamato sotto StateLock
        /// </summary>
        /// <returns>Testo per l'attesa e per la barra del file</returns>
        private string DescribeLiveCall()
        {
            string file = this._liveCalls.Count > 1 && this._liveLatest.FileName.Length > 0 ? this._liveLatest.FileName + ": " : "";
            return file + this._liveLatest.Text + " · " + this._liveLatest.Model;
        }

        /// <summary>
        /// Segna la ricerca web rifiutata dall'accesso: resta spenta per il resto dell'analisi, anche alla ripresa
        /// </summary>
        /// <param name="analysis">Analisi in corso</param>
        private void NoteWebSearchRejected(TranslationAnalysis analysis)
        {
            lock (this.StateLock)
            {
                if (analysis.WebSearchRejected)
                    return;
                analysis.WebSearchRejected = true;
            }

            this.AppendLog(AppText.T(TEXT_PREFIX + ".webSearchRejected"));
            this.OnWebSearchRejected?.Invoke();
        }

        /// <summary>
        /// Sequenza di chiamate di A0, A1 o A2 col modello dell'analisi: al massimo tre richieste con risposta, coi giri di
        /// correzione, e una riga di registro per tentativo di trasporto
        /// </summary>
        /// <typeparam name="T">Esito della lettura di una risposta</typeparam>
        /// <param name="provider">Provider del modello</param>
        /// <param name="kind">A0, A1 o A2</param>
        /// <param name="fileId">File della chiamata, TranslationSessionKey.NO_FILE su più file</param>
        /// <param name="cues">Battute mandate, null se la chiamata non ne manda</param>
        /// <param name="instructions">Instructions della chiamata</param>
        /// <param name="message">Messaggio iniziale</param>
        /// <param name="tools">Tool offerti</param>
        /// <param name="forceTool">True per forzare il tool della consegna, l'ultimo dei tool</param>
        /// <param name="read">Lettura di una risposta, che accoda all'ingresso gli item e gli esiti per il giro di correzione</param>
        /// <param name="cancellation">Annullamento dell'operazione</param>
        /// <returns>Consegna valida</returns>
        private async Task<T> RunDeliveryCallsAsync<T>(ITranslationModelProvider provider, TranslationCallKind kind, string fileId, int? cues, string instructions, string message,
            JsonArray tools, bool forceTool, Func<AiResponseResult, JsonArray, T> read, CancellationToken cancellation) where T : TranslationDelivery
        {
            JsonArray input = new JsonArray(TranslationRequestBuilder.CreateUserMessage(message));
            List<string> errors = new List<string>();

            for (int round = 1; round <= TranslationRequestBuilder.MAX_ROUNDS; round++)
            {
                int currentRound = round;
                TranslationModelRequest request = new TranslationModelRequest();
                request.Kind = kind;
                request.ProjectId = this._project.Id;
                request.FileId = fileId;
                request.Request.Model = this._project.Models.Analysis;
                request.Request.ReasoningEffort = this._project.ReasoningEfforts.Analysis ?? "";
                request.Request.Instructions = instructions;
                request.Request.Input = input;
                request.Request.Tools = tools.DeepClone().AsArray();
                if (forceTool)
                    request.Request.ToolChoice = TranslationRequestBuilder.CreateForcedToolChoice(tools, tools.Count - 1);

                // I tentativi falliti si registrano subito; quello riuscito dopo la validazione, con l'esito della consegna
                // Anche il tentativo interrotto da Stop ha la sua riga, senza tipo d'errore: il consumo è reale
                request.TrackAttempts(attempt =>
                {
                    TranslationCallRecord failed = this.CreateCallRecord(attempt, kind, fileId, cues, currentRound);
                    failed.MarkFailed(attempt.Error);
                    this.RecordCall(failed);
                });

                AiResponseResult response = await provider.SendAsync(request, cancellation);
                TranslationCallRecord record = this.CreateCallRecord(request.DeliveredAttempt, kind, fileId, cues, round);
                record.HttpStatus = response.HttpStatus;

                if (response.Status == AiResponseStatus.Incomplete)
                {
                    record.MarkIncomplete(response, TranslationCallOutcome.Reduced);
                    this.RecordCall(record);
                    throw new AiServiceException(AiErrorKind.InvalidResponse, AppText.T("ai.error.incomplete"));
                }

                T delivery = read(response, input);
                record.Usage = new TranslationCallUsage(response.Usage, delivery.WebSearchCalls);
                record.Outcome = delivery.Errors.Count == 0 ? TranslationCallOutcome.Accepted : TranslationCallOutcome.PartiallyRejected;
                if (delivery.Errors.Count > 0)
                    record.Rejections.Add(new TranslationCallRejection { Reasons = delivery.Errors });
                this.RecordCall(record);

                if (delivery.WebSearchCalls > 0)
                {
                    lock (this.StateLock)
                    {
                        this._project.Analysis.WebSearchUsed = true;
                    }
                }
                if (delivery.Errors.Count == 0)
                    return delivery;

                errors = delivery.Errors;
                this.AppendLog(AppText.F(TEXT_PREFIX + ".deliveryRejected", AppText.T(TEXT_PREFIX + ".calls.kind." + kind), round,
                    string.Join(", ", TranslationValidationIssue.ReadCodes(errors).ConvertAll(c => AppText.T(TranslationValidationIssue.REASON_KEY_PREFIX + c)))));
            }

            throw new AiServiceException(AiErrorKind.InvalidResponse, AppText.F(TEXT_PREFIX + ".deliveryInvalid", TranslationRequestBuilder.MAX_ROUNDS,
                string.Join(", ", TranslationValidationIssue.ReadCodes(errors).ConvertAll(c => AppText.T(TranslationValidationIssue.REASON_KEY_PREFIX + c)))));
        }

        /// <summary>
        /// Riga del registro di un tentativo di trasporto
        /// </summary>
        /// <param name="attempt">Tentativo arrivato al servizio</param>
        /// <param name="kind">Tipo di chiamata</param>
        /// <param name="fileId">File della chiamata</param>
        /// <param name="cues">Battute mandate</param>
        /// <param name="round">Giro di correzione</param>
        /// <returns>Riga da completare con l'esito</returns>
        private TranslationCallRecord CreateCallRecord(TranslationTransportAttempt attempt, TranslationCallKind kind, string fileId, int? cues, int round)
        {
            TranslationCallRecord record = TranslationCallRecord.FromAttempt(attempt, kind, fileId == TranslationSessionKey.NO_FILE ? null : fileId);
            record.RequestedIds = cues;
            record.Round = round;
            return record;
        }

        /// <summary>
        /// Accoda una riga al registro delle chiamate del progetto, su disco e nel pannello
        /// </summary>
        /// <param name="record">Riga da registrare</param>
        private void RecordCall(TranslationCallRecord record)
        {
            lock (this.StateLock)
            {
                record.CallSeq = this._calls.Count > 0 ? this._calls[this._calls.Count - 1].CallSeq + 1 : 1;
                this._store.AppendCall(this._project.Id, record);
                this._calls.Add(record);
                this._project.Analysis.CallSeqs.Add(record.CallSeq);
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Glossario del progetto senza le voci caricate, con le proposte dell'analisi scartate dal caricamento rimesse al loro
        /// posto e i testi proposti al posto di quelli del file; chiamato sotto StateLock
        /// </summary>
        /// <param name="builder">Chiavi delle voci</param>
        /// <returns>Glossario nuovo, quello del progetto resta com'è</returns>
        private Glossary RestoreAnalysisProposals(GlossaryBuilder builder)
        {
            Glossary current = this._project.Glossary;
            Glossary restored = new Glossary();
            restored.Revision = current.Revision;
            restored.SourceLanguage = current.SourceLanguage;
            restored.TargetLanguage = current.TargetLanguage;
            restored.Series = current.Series;
            restored.Tone = current.Tone;
            restored.RegisterRules = current.RegisterRules;
            restored.Terms.AddRange(current.Terms.FindAll(t => t.Origin != GlossaryOrigin.Import));
            restored.Characters.AddRange(current.Characters.FindAll(c => c.Origin != GlossaryOrigin.Import));
            restored.Addressing.AddRange(current.Addressing.FindAll(a => a.Origin != GlossaryOrigin.Import));

            // Una proposta torna solo se la sua voce non c'è già, ad esempio aggiunta a mano dopo il caricamento
            foreach (GlossaryDiscard discard in this._project.Analysis.DiscardedForImport)
            {
                if (discard.Term != null && !restored.Terms.Exists(t => builder.TermKey(t.Source) == builder.TermKey(discard.Term.Source)))
                {
                    restored.Terms.Add(discard.Term);
                }
                else if (discard.Character != null && !restored.Characters.Exists(c => GlossaryBuilder.CharacterKey(c.Name) == GlossaryBuilder.CharacterKey(discard.Character.Name)))
                {
                    restored.Characters.Add(discard.Character);
                }
                else if (discard.Addressing != null &&
                    !restored.Addressing.Exists(a => GlossaryBuilder.AddressingKey(a.Speaker, a.Addressee) == GlossaryBuilder.AddressingKey(discard.Addressing.Speaker, discard.Addressing.Addressee)))
                {
                    restored.Addressing.Add(discard.Addressing);
                }
                else if (discard.Kind == GlossaryEntryKind.Series)
                {
                    restored.Series = discard.Delivered;
                }
                else if (discard.Kind == GlossaryEntryKind.Tone)
                {
                    restored.Tone = discard.Delivered;
                }
                else if (discard.Kind == GlossaryEntryKind.RegisterRules)
                {
                    restored.RegisterRules = discard.Delivered;
                }
            }

            return restored;
        }

        /// <summary>
        /// Prepara un file del progetto coi tipi riconosciuti e ne tiene il documento; un file bloccato tiene le impronte salvate
        /// </summary>
        /// <param name="file">File del progetto</param>
        /// <param name="locked">True con l'analisi partita e i tipi già riconosciuti</param>
        private void PrepareFile(TranslationProjectFile file, bool locked)
        {
            SubtitleTranslationDocument document = TranslationFilePreparer.Prepare(file, this._project.Styles, this._project.Stretches, locked);
            if (document != null)
            {
                this._documents[file.Id] = document;
            }
            else
            {
                this._documents.Remove(file.Id);
            }
        }

        /// <summary>
        /// Allinea la tabella degli stili ai file letti finché i tipi non sono riconosciuti: una combinazione nuova entra senza tipo,
        /// una che non compare più esce solo con tutti i file letti, perché un file rifiutato per poco non la perda
        /// </summary>
        /// <returns>True se la tabella è cambiata</returns>
        private bool SyncStyles()
        {
            if (this._project.Stretches != null)
                return false;

            List<SubtitleStyleUsage> usages = this.GetStyleUsages();
            bool changed = false;
            foreach (SubtitleStyleUsage usage in usages)
            {
                if (this._project.Styles.Exists(s => usage.Is(s.Name, s.Actor, s.Effect)))
                    continue;

                this._project.Styles.Add(new TranslationProjectStyle { Name = usage.Name, Actor = usage.Actor, Effect = usage.Effect });
                changed = true;
            }
            if (this._documents.Count == this._project.Files.Count)
                changed |= this._project.Styles.RemoveAll(s => !usages.Exists(u => u.Is(s.Name, s.Actor, s.Effect))) > 0;

            return changed;
        }

        /// <summary>
        /// Uso di ogni combinazione di stile, attore ed effetto sommato sui file letti, nell'ordine dei file e della prima riga
        /// </summary>
        /// <returns>Combinazioni coi totali</returns>
        private List<SubtitleStyleUsage> GetStyleUsages()
        {
            List<SubtitleStyleUsage> usages = new List<SubtitleStyleUsage>();
            foreach (TranslationProjectFile file in this._project.Files)
            {
                SubtitleTranslationDocument document;
                if (!this._documents.TryGetValue(file.Id, out document))
                    continue;

                foreach (SubtitleStyleUsage usage in document.Styles)
                {
                    SubtitleStyleUsage total = usages.Find(u => u.Is(usage.Name, usage.Actor, usage.Effect));
                    if (total == null)
                    {
                        total = new SubtitleStyleUsage(usage.Name, usage.Actor, usage.Effect);
                        usages.Add(total);
                    }
                    total.Texts.AddRange(usage.Texts);
                }
            }

            return usages;
        }

        /// <summary>
        /// Documenti dei file preparati, nell'ordine del progetto
        /// </summary>
        /// <returns>Identificativo e documento di ogni file valido</returns>
        private List<KeyValuePair<string, SubtitleTranslationDocument>> GetDocuments()
        {
            List<KeyValuePair<string, SubtitleTranslationDocument>> documents = new List<KeyValuePair<string, SubtitleTranslationDocument>>();
            foreach (TranslationProjectFile file in this._project.Files)
            {
                SubtitleTranslationDocument document;
                if (this._documents.TryGetValue(file.Id, out document))
                    documents.Add(new KeyValuePair<string, SubtitleTranslationDocument>(file.Id, document));
            }

            return documents;
        }

        /// <summary>
        /// File preparati del progetto, nell'ordine del progetto, con le etichette dei messaggi
        /// </summary>
        /// <returns>File validi</returns>
        private List<GlossaryEpisode> GetEpisodes()
        {
            List<GlossaryEpisode> episodes = new List<GlossaryEpisode>();
            foreach (TranslationProjectFile file in this._project.Files)
            {
                SubtitleTranslationDocument document;
                if (this._documents.TryGetValue(file.Id, out document))
                    episodes.Add(new GlossaryEpisode(file.Id, "ep" + (episodes.Count + 1).ToString(CultureInfo.InvariantCulture), Path.GetFileName(file.Path), document.Cues));
            }

            return episodes;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Progetto aperto, null nell'elenco dei progetti
        /// </summary>
        public TranslationProject Project
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._project;
                }
            }
        }

        /// <summary>
        /// L'elenco dei file cambia solo con un progetto aperto e prima dell'analisi
        /// </summary>
        protected override bool AcceptsListChanges
        {
            get { return this._project != null && this._project.Analysis == null; }
        }

        /// <summary>
        /// True con i tipi del progetto riconosciuti; chiamato sotto StateLock
        /// </summary>
        private bool KindsSettled
        {
            get
            {
                return this._project.Stretches != null;
            }
        }

        /// <summary>
        /// True quando l'analisi è partita: la lista dei file e le lingue non cambiano più
        /// </summary>
        public bool FilesLocked
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._project != null && this._project.Analysis != null;
                }
            }
        }

        /// <summary>
        /// True se l'analisi può partire, riprendere o riprovare: con tutti i file del progetto letti, anche alla ripresa
        /// </summary>
        public override bool CanStart
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._project != null && !this.IsBusy && !string.IsNullOrEmpty(this._project.SourceLanguage) && !string.IsNullOrEmpty(this._project.TargetLanguage) &&
                        this._documents.Count > 0 && !this._project.Files.Exists(f => !string.IsNullOrEmpty(f.RejectedReason)) &&
                        (this._project.Analysis == null || this._project.Analysis.Status == TranslationAnalysisStatus.Stopped);
                }
            }
        }

        /// <summary>
        /// True se il glossario si può caricare o togliere: fino all'approvazione e prima del giro, con le lingue scelte
        /// </summary>
        public bool CanImportGlossary
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._project != null && this._job == null && !string.IsNullOrEmpty(this._project.SourceLanguage) && !string.IsNullOrEmpty(this._project.TargetLanguage) &&
                        (this._project.Glossary == null || this._project.Glossary.Status != GlossaryStatus.Approved) &&
                        (this._project.Analysis == null || this._project.Analysis.Status != TranslationAnalysisStatus.Running);
                }
            }
        }

        /// <summary>
        /// True se il glossario proposto si può approvare, o quello approvato avviare, finché il giro non c'è, o se quello cambiato
        /// nella pausa della traduzione si può approvare di nuovo: nessun termine con originale o resa vuota
        /// </summary>
        public bool CanApprove
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._project != null && !this.IsBusy && this._project.Analysis != null && this._project.Analysis.Status == TranslationAnalysisStatus.Completed &&
                        this._project.Glossary != null && !this.GlossaryLocked && (this._job == null || this._project.Glossary.Status == GlossaryStatus.Draft) &&
                        !this._project.Glossary.Terms.Exists(t => string.IsNullOrWhiteSpace(t.Source) || string.IsNullOrWhiteSpace(t.Target));
                }
            }
        }

        /// <summary>
        /// True se il glossario non si modifica: col giro avviato, salvo nella pausa della traduzione
        /// </summary>
        public bool GlossaryLocked
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._job != null && !(this._job.Phase == TranslationJobPhase.Translation && this._job.Status == TranslationJobStatus.Paused);
                }
            }
        }

        /// <summary>
        /// Job attivo del progetto aperto, null prima dell'avvio del giro
        /// </summary>
        public TranslationJob Job
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._job;
                }
            }
        }

        /// <summary>
        /// True se il giro può partire o riprendere: glossario approvato e file preparati; col job, la traduzione con file non
        /// conclusi, anche bloccati, o la revisione AI senza lavoro in corso né file bloccati
        /// </summary>
        public bool CanStartJob
        {
            get
            {
                lock (this.StateLock)
                {
                    if (this._project == null || this.IsBusy || this._project.Glossary == null || this._project.Glossary.Status != GlossaryStatus.Approved)
                        return false;

                    if (this._job == null)
                        return this._documents.Count == this._project.Files.Count && this._project.Files.Count > 0;

                    if (this._job.Phase == TranslationJobPhase.Review)
                        return this._job.Status != TranslationJobStatus.Running && !this.HasBlockedFiles;

                    return this._job.Phase == TranslationJobPhase.Translation && this._job.FileIds.Exists(id => this._jobFiles[id].TranslationStatus != TranslationFileStatus.Completed);
                }
            }
        }

        /// <summary>
        /// True se un file del job è bloccato dal sorgente mancante o cambiato: dopo la traduzione il giro aspetta che l'utente lo
        /// ricominci o lo tolga
        /// </summary>
        public bool HasBlockedFiles
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._job != null && this._job.FileIds.Exists(id => this._jobFiles[id].TranslationStatus == TranslationFileStatus.Blocked);
                }
            }
        }

        /// <summary>
        /// True nella revisione dell'utente con la card libera: le battute si modificano e i sottotitoli si esportano
        /// </summary>
        public bool CanEditCues
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._job != null && !this.IsBusy && this._job.Phase == TranslationJobPhase.UserReview && this._job.Status == TranslationJobStatus.Idle;
                }
            }
        }

        /// <summary>
        /// Modalità dell'accesso configurato
        /// </summary>
        public AiConnectionMode AccessMode
        {
            get { return this._credentialService.Load().Mode; }
        }

        /// <summary>
        /// True se il giro può chiamare il modello con l'accesso configurato
        /// </summary>
        public bool IsAccessConfigured
        {
            get { return this._providerFactory.IsAccessConfigured; }
        }

        /// <summary>
        /// Frase dell'attesa durante l'analisi
        /// </summary>
        public string Phrase
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._phrase;
                }
            }
        }

        /// <summary>
        /// Riga secondaria dell'attesa: con una chiamata in corso il suo ultimo evento e il modello, preceduti dal file quando le
        /// chiamate in corso sono più di una
        /// </summary>
        public string Detail
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._liveLatest == null ? this._detail : this.DescribeLiveCall();
                }
            }
        }

        /// <summary>
        /// Inizio della chiamata mostrata nell'attesa, in UTC; null senza chiamate in corso
        /// </summary>
        public DateTime? CallStartedAt
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._liveLatest != null ? this._liveLatest.StartedAt : null;
                }
            }
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Chiamata al modello in corso, per la riga secondaria dell'attesa
        /// </summary>
        private sealed class LiveCall
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="fileName">Nome del file, vuoto per le chiamate su più file</param>
            /// <param name="model">Modello</param>
            public LiveCall(string fileName, string model)
            {
                this.FileName = fileName;
                this.Model = model;
                this.StartedAt = DateTime.UtcNow;
                this.UpdatedAt = this.StartedAt;
                this.Text = "";
            }

            /// <summary>
            /// Nome del file, vuoto per le chiamate su più file
            /// </summary>
            public string FileName { get; }

            /// <summary>
            /// Modello
            /// </summary>
            public string Model { get; }

            /// <summary>
            /// Inizio della chiamata, in UTC
            /// </summary>
            public DateTime StartedAt { get; }

            /// <summary>
            /// Ultimo evento, in UTC
            /// </summary>
            public DateTime UpdatedAt { get; set; }

            /// <summary>
            /// Ultimo evento nella lingua dell'interfaccia
            /// </summary>
            public string Text { get; set; }
        }

        /// <summary>
        /// Chiamate A1 di un'analisi: per ogni unità i file, il messaggio e le battute mandate
        /// </summary>
        private sealed class AnalysisPlan
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            public AnalysisPlan()
            {
                this.Candidates = new List<RecurringCandidate>();
                this.Loaded = null;
                this.Units = new List<List<GlossaryEpisode>>();
                this.Messages = new List<string>();
                this.Fingerprints = new List<string>();
                this.Cues = new List<int>();
            }

            /// <summary>
            /// Candidati ricorrenti di tutti i file
            /// </summary>
            public List<RecurringCandidate> Candidates { get; set; }

            /// <summary>
            /// Voci e testi del glossario caricato, null senza
            /// </summary>
            public Glossary Loaded { get; set; }

            /// <summary>
            /// File di ogni A1
            /// </summary>
            public List<List<GlossaryEpisode>> Units { get; }

            /// <summary>
            /// Messaggio di ogni A1
            /// </summary>
            public List<string> Messages { get; }

            /// <summary>
            /// SHA-256 di instructions e messaggio di ogni A1: un parziale vale solo se è stato fatto sugli stessi ingressi
            /// </summary>
            public List<string> Fingerprints { get; }

            /// <summary>
            /// Battute mandate da ogni A1
            /// </summary>
            public List<int> Cues { get; }
        }

        #endregion
    }

    /// <summary>
    /// Riga della griglia del passo Traduzione: un file del job
    /// </summary>
    public sealed class TranslationJobFileRow
    {
        #region Proprietà

        /// <summary>
        /// File del job
        /// </summary>
        public string FileId { get; set; }

        /// <summary>
        /// Nome del file sorgente
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// Stato della traduzione del file
        /// </summary>
        public TranslationFileStatus Status { get; set; }

        /// <summary>
        /// Codice del motivo del blocco
        /// </summary>
        public string BlockedReason { get; set; }

        /// <summary>
        /// Blocchi salvati
        /// </summary>
        public int BlocksDone { get; set; }

        /// <summary>
        /// Blocchi del piano, dimezzati esclusi
        /// </summary>
        public int BlocksTotal { get; set; }

        /// <summary>
        /// Battute accettate
        /// </summary>
        public int CuesDone { get; set; }

        /// <summary>
        /// Battute da tradurre
        /// </summary>
        public int CuesTotal { get; set; }

        /// <summary>
        /// Battute respinte e poi accettate o da rivedere
        /// </summary>
        public int Retried { get; set; }

        /// <summary>
        /// Battute da rivedere
        /// </summary>
        public int ToReview { get; set; }

        /// <summary>
        /// Token in ingresso reali, null se il registro non li ha
        /// </summary>
        public long? InputTokens { get; set; }

        /// <summary>
        /// Token in cache reali, null se il registro non li ha
        /// </summary>
        public long? CachedTokens { get; set; }

        /// <summary>
        /// Token in uscita reali, null se il registro non li ha
        /// </summary>
        public long? OutputTokens { get; set; }

        #endregion
    }

    /// <summary>
    /// Fine di una fase del giro, o fermata del giro nella fase del job
    /// </summary>
    public sealed class TranslationPhaseResult
    {
        #region Proprietà

        /// <summary>
        /// Job del giro
        /// </summary>
        public TranslationJob Job { get; set; }

        /// <summary>
        /// Fase finita o fermata
        /// </summary>
        public TranslationJobPhase Phase { get; set; }

        /// <summary>
        /// Blocchi scelti dalla revisione AI
        /// </summary>
        public int ReviewBlocks { get; set; }

        /// <summary>
        /// Battute corrette dalla revisione AI
        /// </summary>
        public int Corrected { get; set; }

        /// <summary>
        /// Battute che la revisione AI non ha potuto rivedere
        /// </summary>
        public int NotReviewed { get; set; }

        /// <summary>
        /// Fermata del giro nella fase, null per la fase conclusa
        /// </summary>
        public TranslationStop Stop { get; set; }

        #endregion
    }

    /// <summary>
    /// Riga della griglia della revisione: una battuta da tradurre con lo stato della traduzione
    /// </summary>
    public sealed class TranslationReviewRow
    {
        #region Proprietà

        /// <summary>
        /// Id della battuta
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Inizio in millisecondi
        /// </summary>
        public long StartMs { get; set; }

        /// <summary>
        /// Fine in millisecondi
        /// </summary>
        public long EndMs { get; set; }

        /// <summary>
        /// Testo pulito del sorgente
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// Traduzione, null senza testo tradotto
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// True per una battuta da rivedere, senza traduzione o non rivista dalla revisione AI
        /// </summary>
        public bool ToReview { get; set; }

        /// <summary>
        /// Motivo dello stato da rivedere per l'utente, revisione AI non riuscita compresa; null fuori da quello stato o senza
        /// esito della traduzione
        /// </summary>
        public string ToReviewReason { get; set; }

        /// <summary>
        /// Segnali della battuta
        /// </summary>
        public List<TranslationCueSignal> Signals { get; set; }

        /// <summary>
        /// Testo prima della correzione della revisione AI, null se nessuno
        /// </summary>
        public string CorrectionBefore { get; set; }

        /// <summary>
        /// Motivo della correzione della revisione AI, null se nessuno
        /// </summary>
        public string CorrectionReason { get; set; }

        /// <summary>
        /// Numero della tratta ripetuta fra i file a cui la battuta appartiene, null fuori dalle tratte: la traduzione è la stessa in
        /// tutte le occorrenze e una modifica le cambia tutte
        /// </summary>
        public int? SharedStretch { get; set; }

        #endregion
    }

    /// <summary>
    /// Riga della griglia del passo Esporta: un file del job con l'uscita
    /// </summary>
    public sealed class TranslationExportRow
    {
        #region Proprietà

        /// <summary>
        /// File del job
        /// </summary>
        public string FileId { get; set; }

        /// <summary>
        /// Nome del file sorgente
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// Percorso voluto prima dell'export, percorso scritto dopo
        /// </summary>
        public string OutputPath { get; set; }

        /// <summary>
        /// True se il file è scritto
        /// </summary>
        public bool Written { get; set; }

        /// <summary>
        /// Battute col testo tradotto
        /// </summary>
        public int Translated { get; set; }

        /// <summary>
        /// Battute senza testo tradotto, che escono col testo originale
        /// </summary>
        public int Untranslated { get; set; }

        /// <summary>
        /// Motivo per cui il file non si scrive, vuoto se nessuno
        /// </summary>
        public string Error { get; set; }

        #endregion
    }

    /// <summary>
    /// Riga della griglia dei file: un file del progetto
    /// </summary>
    public sealed class TranslationFileRecord : ToolboxRecord
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="file">File del progetto</param>
        public TranslationFileRecord(TranslationProjectFile file) : base(file.Path, file.SizeBytes)
        {
            this.File = file;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// File del progetto
        /// </summary>
        public TranslationProjectFile File { get; }

        #endregion
    }
}
