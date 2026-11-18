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
using System.Security.Cryptography;
using System.Text.Json;
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
        /// Job attivo del progetto aperto, null prima di «Avvia traduzione»
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
        /// Ultima stima dell'analisi, valida finché il progetto non cambia
        /// </summary>
        private TranslationAnalysisEstimate _estimate;

        /// <summary>
        /// Stato del progetto a cui si riferisce la stima
        /// </summary>
        private string _estimateKey;

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
            this._estimate = null;
            this._estimateKey = "";
            this._translationEstimate = null;
            this._translationEstimateKey = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Elenca i progetti salvati, dal più recente
        /// </summary>
        /// <returns>Progetti</returns>
        public List<TranslationProject> ListProjects()
        {
            return this._store.List();
        }

        /// <summary>
        /// Apre un progetto nuovo, salvato alla prima modifica
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
            }

            this.AppendLog(AppText.T(TEXT_PREFIX + ".projectCreated"));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Apre un progetto salvato e ne prepara i file; un'analisi rimasta in corso da una chiusura dell'app diventa fermata, e
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

                if (project.Analysis != null && project.Analysis.Status == TranslationAnalysisStatus.Running)
                {
                    project.Analysis.Status = TranslationAnalysisStatus.Stopped;
                    project.Analysis.Stop = new TranslationStop { Reason = TranslationStopReason.Crash };
                    this._store.Save(project);
                }

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
                    this.PrepareFile(file);
                }
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
                this._store.Delete(project.Id);
            }

            this.AppendLog(AppText.F(TEXT_PREFIX + ".projectDeleted", DisplayName(project)));
            this.NotifyRecordsChanged();
            return true;
        }

        /// <summary>
        /// Cambia nome, lingue e contesto del progetto; le lingue si bloccano con l'analisi, e la lingua d'origine rifà i gruppi
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
                bool regroup = !locked && !string.Equals(this._project.SourceLanguage, sourceLanguage ?? "", StringComparison.Ordinal);
                this._project.Name = (name ?? "").Trim();
                this._project.Context = context ?? "";
                if (!locked)
                {
                    this._project.SourceLanguage = sourceLanguage ?? "";
                    this._project.TargetLanguage = targetLanguage ?? "";
                }

                if (regroup)
                {
                    foreach (TranslationProjectFile file in this._project.Files)
                        this.PrepareFile(file);
                }

                this._store.Save(this._project);
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Cambia la modalità della revisione AI, fino a «Avvia traduzione»
        /// </summary>
        /// <param name="mode">Mirata o completa</param>
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
                this.PrepareFile(record.File);
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
        /// Capacità dell'accesso attivo, per il passo Progetto
        /// </summary>
        /// <returns>Capacità del provider</returns>
        public TranslationProviderCapabilities GetCapabilities()
        {
            return this._providerFactory.Create(null).Capabilities;
        }

        /// <summary>
        /// Salva modelli e livelli di ragionamento dei passi e i file tradotti in parallelo
        /// </summary>
        /// <param name="models">Modello per passo</param>
        /// <param name="efforts">Livello per passo</param>
        /// <param name="parallelFiles">File in parallelo, da TranslationProjectOptions.MIN_PARALLEL_FILES a MAX_PARALLEL_FILES</param>
        public void SetModels(TranslationPhaseModels models, TranslationPhaseModels efforts, int parallelFiles)
        {
            lock (this.StateLock)
            {
                if (this._project == null || this.IsBusy || parallelFiles < TranslationProjectOptions.MIN_PARALLEL_FILES || parallelFiles > TranslationProjectOptions.MAX_PARALLEL_FILES)
                    return;

                this._project.Models = models;
                this._project.ReasoningEfforts = efforts;
                this._project.Options.MaxParallelFiles = parallelFiles;
                this._store.Save(this._project);
            }

            this.AppendLog(AppText.F(TEXT_PREFIX + ".modelsChanged", models.Analysis, models.Translation, models.NewTerms, models.Review, parallelFiles));
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

                result = GlossaryFile.Import(path, this._project.SourceLanguage, this._project.TargetLanguage);
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
                    int discarded = this._project.Analysis.DiscardedForImport.FindAll(d => d.Kind == GlossaryEntryKind.Term || d.Kind == GlossaryEntryKind.Character ||
                        d.Kind == GlossaryEntryKind.Addressing).Count;
                    if (discarded > 0)
                        this.AppendLog(AppText.Count(TEXT_PREFIX + ".discardedForImport", discarded));
                }

                next.SourceLanguage = this._project.SourceLanguage;
                next.TargetLanguage = this._project.TargetLanguage;
                next.Revision = this._project.Glossary != null ? this._project.Glossary.Revision : 0;
                next.Status = GlossaryStatus.Draft;
                builder.SetEpisodes(next, this.GetEpisodes());
                this._project.Glossary = next;
                this._project.ImportedGlossary = new ImportedGlossaryInfo
                {
                    FileName = name,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    ImportedAt = DateTimeOffset.Now,
                    TermCount = result.Glossary.Terms.FindAll(t => t.Origin == GlossaryOrigin.Import).Count,
                    CharacterCount = result.Glossary.Characters.FindAll(c => c.Origin == GlossaryOrigin.Import).Count,
                    AddressingCount = result.Glossary.Addressing.FindAll(a => a.Origin == GlossaryOrigin.Import).Count
                };
                this._store.Save(this._project);
            }

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
                    builder.SetEpisodes(restored, this.GetEpisodes());
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
        /// Salva le modifiche fatte al glossario dalla pagina; un glossario approvato torna in bozza. Dopo «Avvia traduzione» il
        /// glossario resta com'è
        /// </summary>
        /// <param name="change">Modifica, eseguita sotto il lock dello stato</param>
        public void ChangeGlossary(Action<Glossary> change)
        {
            lock (this.StateLock)
            {
                if (this._project == null || this._project.Glossary == null || this.IsBusy || this._job != null)
                    return;

                change(this._project.Glossary);
                this._project.Glossary.Status = GlossaryStatus.Draft;
                new GlossaryBuilder(this._project.SourceLanguage, this._project.TargetLanguage).SetEpisodes(this._project.Glossary, this.GetEpisodes());
                this._store.Save(this._project);
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Approva il glossario e abilita il passo Traduzione
        /// </summary>
        public void ApproveGlossary()
        {
            lock (this.StateLock)
            {
                if (!this.CanApprove)
                    return;

                Glossary glossary = this._project.Glossary;
                glossary.Status = GlossaryStatus.Approved;
                glossary.Revision++;
                glossary.ApprovedAt = DateTimeOffset.Now;
                if (this._project.ReachedStep < TranslationProjectStep.Translation)
                    this._project.ReachedStep = TranslationProjectStep.Translation;
                this._store.Save(this._project);
            }

            this.AppendLog(AppText.F(TEXT_PREFIX + ".glossaryApproved", this._project.Glossary.Revision));
            this.NotifyRecordsChanged();
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

                glossary = JsonSerializer.Deserialize<Glossary>(JsonSerializer.Serialize(this._project.Glossary));
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
        /// Stima in token delle chiamate A1 che mancano, calcolata per caratteri come l'analisi le manderebbe; la A2 dipende dai
        /// conflitti e non entra nella stima
        /// </summary>
        /// <returns>Stima, null senza file preparati o lingue, con la card occupata o con l'analisi conclusa</returns>
        public TranslationAnalysisEstimate EstimateAnalysis()
        {
            lock (this.StateLock)
            {
                if (this._project == null || this.IsBusy || string.IsNullOrEmpty(this._project.SourceLanguage) || string.IsNullOrEmpty(this._project.TargetLanguage) ||
                    this._documents.Count == 0 || (this._project.Analysis != null && this._project.Analysis.Status == TranslationAnalysisStatus.Completed))
                    return null;

                string key = this._project.Id + "|" + this._project.UpdatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + this._documents.Count.ToString(CultureInfo.InvariantCulture);
                if (this._estimate != null && this._estimateKey == key)
                    return this._estimate;

                GlossaryBuilder builder = new GlossaryBuilder(this._project.SourceLanguage, this._project.TargetLanguage);
                List<GlossaryEpisode> episodes = this.GetEpisodes();
                AnalysisPlan plan = this.PlanAnalysis(builder, episodes);
                TranslationAnalysisEstimate estimate = new TranslationAnalysisEstimate();
                estimate.Files = episodes.Count;
                for (int i = 0; i < plan.Units.Count; i++)
                {
                    string fileId = plan.Units[i].Count == 1 ? plan.Units[i][0].FileId : TranslationSessionKey.NO_FILE;
                    if (this._project.Analysis != null && this._project.Analysis.Partials.Exists(p => p.FileId == fileId))
                        continue;

                    estimate.Calls++;
                    estimate.Cues += plan.Cues[i];
                    estimate.InputTokens += GlossaryBuilder.EstimateTokens(builder.AnalysisInstructions + plan.Messages[i]);
                }

                this._estimate = estimate;
                this._estimateKey = key;
                return estimate;
            }
        }

        /// <summary>
        /// Testata del job attivo di un progetto; un job rimasto in corso da una chiusura dell'app diventa fermato, tranne quello
        /// che la card sta traducendo. Un job illeggibile o di una versione più recente dà l'eccezione col motivo
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
                if (!this.IsBusy && job.Status == TranslationJobStatus.Running)
                {
                    job.Status = TranslationJobStatus.Paused;
                    job.Pause = new TranslationStop { Reason = TranslationStopReason.Crash, At = DateTimeOffset.Now };
                    this._jobStore.Save(job);
                }
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
        /// cui si è fermato: traduzione dal primo blocco da fare, applicazione dai file non conclusi, revisione dai blocchi non rivisti.
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
                    TranslationJob job = this._jobStore.Create(this._project.Id);
                    job.AccessMode = this.AccessMode;
                    job.Models = new TranslationPhaseModels
                    {
                        Analysis = this._project.Models.Analysis,
                        Translation = this._project.Models.Translation,
                        NewTerms = this._project.Models.NewTerms,
                        Review = this._project.Models.Review
                    };
                    job.ReasoningEfforts = new TranslationPhaseModels
                    {
                        Analysis = this._project.ReasoningEfforts.Analysis,
                        Translation = this._project.ReasoningEfforts.Translation,
                        NewTerms = this._project.ReasoningEfforts.NewTerms,
                        Review = this._project.ReasoningEfforts.Review
                    };
                    job.Glossary = TranslationJsonFile.Parse<Glossary>(TranslationJsonFile.Serialize(this._project.Glossary), this._project.Id);
                    job.GlossaryRevisionAtStart = this._project.Glossary.Revision;
                    job.ReviewMode = this._project.Options.ReviewMode;
                    job.Status = TranslationJobStatus.Idle;
                    foreach (TranslationProjectFile file in this._project.Files)
                    {
                        TranslationJobFileState state = TranslationRunner.CreateFileState(file, this._documents[file.Id]);
                        job.FileIds.Add(file.Id);
                        this._jobFiles[file.Id] = state;
                        this._jobStore.SaveFile(job.Id, state);
                    }
                    this._jobStore.Save(job);

                    this._job = job;
                    this._project.ActiveJobId = job.Id;
                    this._project.JobIds.Add(job.Id);
                    this._store.Save(this._project);
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
        /// Ricomincia da capo un file bloccato dal sorgente cambiato: scarta il suo stato, lo riprepara e ricalcola l'hash
        /// </summary>
        /// <param name="fileId">File del job</param>
        public void RestartFile(string fileId)
        {
            string name;
            lock (this.StateLock)
            {
                TranslationJobFileState state;
                if (this._project == null || this._job == null || this.IsBusy || !this._jobFiles.TryGetValue(fileId, out state) ||
                    state.TranslationStatus != TranslationFileStatus.Blocked || state.BlockedReason != TranslationFilePreparer.REJECTION_SOURCE_CHANGED)
                    return;

                TranslationProjectFile file = this._project.Files.Find(f => f.Id == fileId);
                SubtitleTranslationDocument document = TranslationFilePreparer.Prepare(file, this._project.SourceLanguage, false);
                if (document == null)
                {
                    state.BlockedReason = file.RejectedReason;
                    this._jobStore.SaveFile(this._job.Id, state);
                    return;
                }

                this._documents[fileId] = document;
                state = TranslationRunner.CreateFileState(file, document);
                this._jobFiles[fileId] = state;
                this._jobStore.SaveFile(this._job.Id, state);
                this._jobStore.Save(this._job);
                this._store.Save(this._project);
                name = Path.GetFileName(file.Path);
            }

            this.AppendLog(AppText.F(TEXT_PREFIX + ".fileRestarted", name));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Toglie dal job un file bloccato; con tutti gli altri file conclusi la traduzione è finita e il giro prosegue: si ferma
        /// sui termini nuovi da decidere o, senza termini, riparte con la revisione AI
        /// </summary>
        /// <param name="fileId">File del job</param>
        public void ExcludeFile(string fileId)
        {
            string name;
            bool review = false;
            lock (this.StateLock)
            {
                TranslationJobFileState state;
                if (this._project == null || this._job == null || this.IsBusy || !this._jobFiles.TryGetValue(fileId, out state) ||
                    state.TranslationStatus != TranslationFileStatus.Blocked)
                    return;

                this._job.FileIds.Remove(fileId);
                this._jobFiles.Remove(fileId);
                this._jobStore.DeleteFile(this._job.Id, fileId);
                if (this._job.Phase == TranslationJobPhase.Translation && this._job.FileIds.TrueForAll(id => this._jobFiles[id].TranslationStatus == TranslationFileStatus.Completed))
                {
                    this.FinishTranslation();
                    this._job.Status = TranslationJobStatus.Idle;
                    this._job.Pause = null;
                    this._store.Save(this._project);
                    review = this._job.Phase == TranslationJobPhase.Review;
                }
                this._jobStore.Save(this._job);
                name = Path.GetFileName(this._project.Files.Find(f => f.Id == fileId).Path);
            }

            this.AppendLog(AppText.F(TEXT_PREFIX + ".fileExcluded", name));
            this.NotifyRecordsChanged();
            if (review)
                this.StartJob();
        }

        /// <summary>
        /// Salva la decisione su un termine nuovo fatta dalla pagina, finché il giro aspetta i termini
        /// </summary>
        /// <param name="term">Termine del job</param>
        /// <param name="change">Modifica, eseguita sotto il lock dello stato</param>
        public void ChangeNewTerm(NewTermDecision term, Action<NewTermDecision> change)
        {
            lock (this.StateLock)
            {
                if (this._job == null || this.IsBusy || this._job.Phase != TranslationJobPhase.NewTerms || !this._job.NewTerms.Contains(term))
                    return;

                change(term);
                this._jobStore.Save(this._job);
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Aggiunge i termini su «Aggiungi» al glossario del job e a quello del progetto, con una revisione in più, e riprende il
        /// giro: applicazione dei termini, o la revisione AI se sono tutti ignorati. Il progetto si salva prima del job: dopo una
        /// chiusura brusca fra i due la conferma si ripete e il termine già nel progetto prende la decisione ripetuta
        /// </summary>
        public void ConfirmNewTerms()
        {
            int added = 0;
            lock (this.StateLock)
            {
                if (!this.CanConfirmNewTerms)
                    return;

                List<TranslationJobFileState> states = new List<TranslationJobFileState>();
                foreach (string fileId in this._job.FileIds)
                    states.Add(this._jobFiles[fileId]);
                foreach (NewTermDecision decision in this._job.NewTerms)
                {
                    if (decision.Decision != NewTermDecisionKind.Add)
                        continue;
                    this._job.Glossary.Terms.Add(NewTermCollector.CreateTerm(decision, this._job.Glossary, states));
                    this._project.Glossary.Terms.RemoveAll(t => t.Origin == GlossaryOrigin.NewTerm && string.Equals(t.Source, decision.Source, StringComparison.Ordinal));
                    this._project.Glossary.Terms.Add(NewTermCollector.CreateTerm(decision, this._job.Glossary, states));
                    added++;
                }

                if (added > 0)
                {
                    this._job.Glossary.Revision++;
                    this._project.Glossary.Revision++;
                }
                this._job.Phase = added > 0 ? TranslationJobPhase.Applying : TranslationJobPhase.Review;
                if (this._job.Phase == TranslationJobPhase.Review && this._project.ReachedStep < TranslationProjectStep.Review)
                    this._project.ReachedStep = TranslationProjectStep.Review;
                this._store.Save(this._project);
                this._jobStore.Save(this._job);
            }

            this.AppendLog(AppText.Count(TEXT_PREFIX + ".newTermsConfirmed", added));
            this.StartJob();
        }

        /// <summary>
        /// Righe della griglia dell'applicazione dei termini: battute coinvolte e riscritte per file
        /// </summary>
        /// <returns>Righe nell'ordine del job, vuote senza job</returns>
        public List<TermApplicationRow> GetTermApplicationRows()
        {
            List<TermApplicationRow> rows = new List<TermApplicationRow>();
            lock (this.StateLock)
            {
                if (this._project == null || this._job == null)
                    return rows;

                foreach (string fileId in this._job.FileIds)
                {
                    TermApplicationState application = this._jobFiles[fileId].TermApplication;
                    TermApplicationRow row = new TermApplicationRow();
                    row.FileName = Path.GetFileName(this._project.Files.Find(f => f.Id == fileId).Path);
                    row.Status = application != null ? application.Status : TermApplicationStatus.Pending;
                    row.Targets = application != null && application.Status != TermApplicationStatus.Pending ? application.TargetIds.Count : null;
                    row.Rewritten = application != null && application.Status != TermApplicationStatus.Pending ? application.RewrittenIds.Count : null;
                    row.Failed = application != null && application.Status != TermApplicationStatus.Pending ? application.FailedIds.Count : 0;
                    rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// Righe della griglia della revisione di un file: tempi, originale, traduzione, stato e avvisi di ogni battuta da tradurre
        /// </summary>
        /// <param name="fileId">File del job</param>
        /// <returns>Righe in ordine di id, vuote senza job o senza il documento del file</returns>
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
                foreach (SubtitleCue cue in document.Cues)
                {
                    if (!cue.Id.HasValue)
                        continue;

                    TranslatedCueState translation;
                    translations.TryGetValue(cue.Id.Value, out translation);
                    TranslationReviewRow row = new TranslationReviewRow();
                    row.Id = cue.Id.Value;
                    row.StartMs = cue.StartMs;
                    row.EndMs = cue.EndMs;
                    row.Source = cue.CleanText;
                    row.Group = cue.Group;
                    row.Text = translation != null ? translation.Text : null;
                    row.ToReview = translation == null || translation.Status == TranslatedCueStatus.ToReview;
                    row.Signals = translation != null ? new List<TranslationCueSignal>(translation.Signals) : new List<TranslationCueSignal>();
                    row.CorrectionBefore = translation != null && translation.AiCorrection != null ? translation.AiCorrection.Before : null;
                    row.CorrectionReason = translation != null && translation.AiCorrection != null ? translation.AiCorrection.Explanation : null;
                    rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// Salva la modifica a mano di una battuta: stesso validatore delle consegne, avvisi del glossario ricalcolati, segnale
        /// «Modificata a mano» e versione più uno; un testo con errori non si salva
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
                List<KeyValuePair<string, string>> terms = new List<KeyValuePair<string, string>>();
                foreach (GlossaryTerm term in this._job.Glossary.Terms)
                    terms.Add(new KeyValuePair<string, string>(term.Source, term.Target));
                CueValidationResult result = new TranslationValidator(document.Format, this._job.Glossary.SourceLanguage, this._job.Glossary.TargetLanguage, terms).ValidateEdit(cue, text);
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
                this._jobStore.SaveFile(this._job.Id, state);
                this._jobStore.Save(this._job);
            }

            this.NotifyRecordsChanged();
            return "";
        }

        /// <summary>
        /// Apre il passo Esporta dalla revisione dell'utente
        /// </summary>
        public void AdvanceToExport()
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
                    row.OutputPath = TranslationExporter.GetOutputPath(file.Path, this._project.TargetLanguage, folder);
                    row.Translated = state.Translations.FindAll(t => t.Text != null).Count;
                    row.Untranslated = state.Cues.Count - row.Translated;
                    row.Error = this._documents.ContainsKey(fileId) ? "" : AppText.T(TEXT_PREFIX + ".fileRejected." + file.RejectedReason);
                    rows.Add(row);
                }
            }

            return rows;
        }

        /// <summary>
        /// Scrive i sottotitoli tradotti con la barra Uscita, con la card occupata: un nome occupato prende il suffisso, un file
        /// non scritto tiene il motivo nella sua riga e gli altri proseguono. Il progetto resta nella revisione dell'utente
        /// </summary>
        /// <returns>Righe coi percorsi scritti o il motivo, null se l'export non può partire</returns>
        public List<TranslationExportRow> ExportSubtitles()
        {
            List<TranslationExportRow> rows;
            List<TranslationRunFile> files = new List<TranslationRunFile>();
            List<string> inputs = new List<string>();
            lock (this.StateLock)
            {
                if (!this.CanEditCues || !string.IsNullOrEmpty(this.OutputFolderError))
                    return null;

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
                        rows[i].OutputPath = TranslationExporter.Export(files[i], rows[i].OutputPath, allocator);
                        rows[i].Written = true;
                        written++;
                        this.AppendLog(AppText.F(TEXT_PREFIX + ".subtitleExported", rows[i].FileName, rows[i].OutputPath));
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is IOException || ex is UnauthorizedAccessException))
                            throw;
                        rows[i].Error = ex.Message;
                        this.AppendLog(AppText.F(TEXT_PREFIX + ".subtitleNotExported", rows[i].FileName, ex.Message));
                    }
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
        /// True se il progetto si può esportare: fermato da riprendere o nella revisione dell'utente, con la card libera
        /// </summary>
        /// <param name="project">Progetto aperto o dell'elenco</param>
        /// <param name="job">Job attivo del progetto</param>
        /// <returns>True se «Esporta progetto» è attivo</returns>
        public bool CanExportProject(TranslationProject project, TranslationJob job)
        {
            return project != null && job != null && !this.IsBusy &&
                ((job.Status == TranslationJobStatus.Paused) || (job.Phase == TranslationJobPhase.UserReview && job.Status == TranslationJobStatus.Idle));
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
        /// Stima della traduzione prima di «Avvia traduzione», per caratteri come le chiamate B la manderebbero
        /// </summary>
        /// <returns>Stima, null col job già creato, senza glossario approvato o con file non preparati</returns>
        public TranslationEstimate EstimateTranslation()
        {
            lock (this.StateLock)
            {
                if (this._project == null || this._job != null || this._project.Glossary == null || this._project.Glossary.Status != GlossaryStatus.Approved ||
                    this._documents.Count != this._project.Files.Count)
                    return null;

                AiConnectionMode access = this.AccessMode;
                string key = this._project.Id + "|" + this._project.UpdatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + access.ToString();
                if (this._translationEstimate != null && this._translationEstimateKey == key)
                    return this._translationEstimate;

                List<KeyValuePair<string, IReadOnlyList<SubtitleCue>>> files = new List<KeyValuePair<string, IReadOnlyList<SubtitleCue>>>();
                foreach (TranslationProjectFile file in this._project.Files)
                    files.Add(new KeyValuePair<string, IReadOnlyList<SubtitleCue>>(Path.GetFileName(file.Path), this._documents[file.Id].Cues));

                this._translationEstimate = TranslationRunner.Estimate(this._project.Glossary, files, access == AiConnectionMode.ChatGptAccount, this._project.Options.ReviewMode);
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

                    HashSet<string> terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (CollectedNewTerm term in state.CollectedNewTerms)
                        terms.Add(term.Source);
                    row.NewTerms = terms.Count;
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
        /// tradotto o diviso, l'applicazione dei termini di un file, la diagnosi o la correzione di un blocco della revisione.
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
                foreach (TranslationJobFileState state in this._jobFiles.Values)
                {
                    List<int> seqs = new List<int>();
                    foreach (TranslationBlock block in state.Blocks)
                    {
                        if (block.Status != TranslationBlockStatus.Pending)
                            seqs.AddRange(block.CallSeqs);
                    }
                    if (state.TermApplication != null && state.TermApplication.Status != TermApplicationStatus.Pending)
                        seqs.AddRange(state.TermApplication.CallSeqs);
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
        /// Prepara i file senza documento, quelli appena aggiunti e quelli rifiutati prima, e salva il progetto
        /// </summary>
        protected override void OnFilesAdded()
        {
            foreach (TranslationFileRecord record in this.Records)
            {
                if (!this._documents.ContainsKey(record.File.Id))
                    this.PrepareFile(record.File);
            }

            this._store.Save(this._project);
        }

        /// <summary>
        /// Allinea i file del progetto alle righe rimaste e salva
        /// </summary>
        protected override void OnRecordsRemoved()
        {
            this._project.Files.RemoveAll(f => !this.Records.Exists(r => ReferenceEquals(r.File, f)));
            foreach (string id in new List<string>(this._documents.Keys))
            {
                if (!this._project.Files.Exists(f => f.Id == id))
                    this._documents.Remove(id);
            }

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
                lock (this.StateLock)
                {
                    project = this._project;
                    episodes = this.GetEpisodes();
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

                GlossaryBuilder builder = new GlossaryBuilder(project.SourceLanguage, project.TargetLanguage);
                AnalysisPlan plan;
                lock (this.StateLock)
                {
                    plan = this.PlanAnalysis(builder, episodes);
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

                    Glossary partial = await this.RunGlossaryCallsAsync(provider, builder, TranslationCallKind.A1, fileId, plan.Cues[i], builder.AnalysisInstructions, plan.Messages[i], true, null,
                        cancellation);

                    // La ricerca rifiutata resta spenta per il resto dell'analisi, anche alla ripresa
                    if (webSearch && !provider.Capabilities.WebSearch)
                    {
                        webSearch = false;
                        this.NoteWebSearchRejected(analysis);
                    }

                    lock (this.StateLock)
                    {
                        analysis.Partials.Add(new GlossaryPartial { FileId = fileId, Glossary = partial });
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
                    Glossary resolution = await this.RunGlossaryCallsAsync(provider, builder, TranslationCallKind.A2, TranslationSessionKey.NO_FILE, null, builder.MergeInstructions, message, false,
                        merge, cancellation);
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
                    builder.SetEpisodes(glossary, episodes);
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

                    // Un disco che non scrive non deve chiudere l'app: la fermata resta in memoria e all'apertura diventa Crash
                    try
                    {
                        lock (this.StateLock)
                        {
                            this._store.Save(project);
                        }
                    }
                    catch (Exception saveError)
                    {
                        if (!(saveError is IOException || saveError is UnauthorizedAccessException))
                            throw;
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
        /// Giro del job, nel thread dell'operazione: dalla fase in cui si trova, traduzione, applicazione dei termini e revisione
        /// AI si susseguono; si ferma ai termini nuovi da decidere, alla revisione dell'utente o a una fermata, con lo stato del job
        /// e il motivo. Nessuna eccezione ne esce, e la card si libera sempre
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
                lock (this.StateLock)
                {
                    job = this._job;
                    phase = job.Phase;
                    job.Status = TranslationJobStatus.Running;
                    job.Pause = null;
                    job.AccessMode = this.AccessMode;
                    parallel = this._project.Options.MaxParallelFiles;
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
                        TranslationRunner runner = new TranslationRunner(this._jobStore, job, files, this._jobCalls, this.StateLock);
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
                        if (job.NewTerms.Count > 0)
                            this.AppendLog(AppText.Count(TEXT_PREFIX + ".newTermsWaiting", job.NewTerms.Count));
                        this.OnPhaseFinished?.Invoke(new TranslationPhaseResult { Job = job, Phase = TranslationJobPhase.Translation });
                        phase = job.Phase;
                        if (phase == TranslationJobPhase.NewTerms)
                            break;
                    }
                    else if (phase == TranslationJobPhase.Applying)
                    {
                        this.AppendLog(AppText.T(TEXT_PREFIX + ".termsStarted"));
                        TermApplier applier = new TermApplier(this._jobStore, job, files, this._jobCalls, this.StateLock);
                        this.AttachRunner(applier, TEXT_PREFIX + ".termsProgress", TEXT_PREFIX + ".callOfFile", null);
                        stop = await applier.RunAsync(provider, parallel, this.OperationCancellationToken);
                        if (stop != null)
                            break;

                        int rewritten = 0;
                        lock (this.StateLock)
                        {
                            foreach (TranslationRunFile file in files)
                                rewritten += file.State.TermApplication.RewrittenIds.Count;
                            job.Phase = TranslationJobPhase.Review;
                            this._jobStore.Save(job);
                        }
                        this.AppendLog(AppText.Count(TEXT_PREFIX + ".termsApplied", rewritten));
                        this.OnPhaseFinished?.Invoke(new TranslationPhaseResult { Job = job, Phase = TranslationJobPhase.Applying });
                        phase = job.Phase;
                    }
                    else
                    {
                        // Blocchi scelti una volta e salvati: alla ripresa la scelta non cambia
                        TranslationReviewer reviewer = new TranslationReviewer(this._jobStore, job, files, this._jobCalls, this.StateLock);
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

                        int corrected = 0;
                        lock (this.StateLock)
                        {
                            foreach (TranslationRunFile file in files)
                                corrected += file.State.Translations.FindAll(t => t.Signals.Contains(TranslationCueSignal.CorrectedByAi)).Count;
                            job.Phase = TranslationJobPhase.UserReview;
                            this._jobStore.Save(job);
                        }
                        this.AppendLog(blocks > 0 ? AppText.Count(TEXT_PREFIX + ".reviewCompleted", corrected) : AppText.T(TEXT_PREFIX + ".reviewNothing"));
                        this.OnPhaseFinished?.Invoke(new TranslationPhaseResult { Job = job, Phase = TranslationJobPhase.Review, ReviewBlocks = blocks, Corrected = corrected });
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
                        job.Status = stop != null ? TranslationJobStatus.Paused : TranslationJobStatus.Idle;
                        job.Pause = stop;
                        phase = job.Phase;
                        this._phrase = "";
                        this._detail = "";

                        // Una fase finita lascia la barra piena; Stop e pausa lasciano il punto raggiunto
                        if (stop == null)
                            this.ProgressState.CurrentPercent = 100;
                    }

                    // Un disco che non scrive non deve chiudere l'app: la fermata resta in memoria e all'apertura diventa Crash
                    try
                    {
                        lock (this.StateLock)
                        {
                            this._jobStore.Save(job);
                        }
                    }
                    catch (Exception saveError)
                    {
                        if (!(saveError is IOException || saveError is UnauthorizedAccessException))
                            throw;
                        this.AppendLog(AppText.F(TEXT_PREFIX + ".projectNotSaved", saveError.Message));
                    }

                    if (stop != null)
                    {
                        string name = phase == TranslationJobPhase.Applying ? "terms" : phase == TranslationJobPhase.Review ? "review" : "translation";
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
        /// del file; la revisione mostra anche blocco e modello nell'attesa
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
                    this.ProgressState.CurrentEpisode = AppText.F(unitKey, number, count, fileName);
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
        /// Chiude la traduzione conclusa: raccoglie i termini nuovi di tutti i file; con termini il giro aspetta l'utente, senza
        /// passa alla revisione AI, e il passo raggiunto segue la fase; chiamato sotto StateLock
        /// </summary>
        private void FinishTranslation()
        {
            List<TranslationJobFileState> states = new List<TranslationJobFileState>();
            foreach (string fileId in this._job.FileIds)
                states.Add(this._jobFiles[fileId]);

            this._job.NewTerms = NewTermCollector.Collect(this._job.Glossary, states);
            this._job.Phase = this._job.NewTerms.Count > 0 ? TranslationJobPhase.NewTerms : TranslationJobPhase.Review;
            TranslationProjectStep reached = this._job.Phase == TranslationJobPhase.NewTerms ? TranslationProjectStep.NewTerms : TranslationProjectStep.Review;
            if (this._project.ReachedStep < reached)
                this._project.ReachedStep = reached;
        }

        /// <summary>
        /// Rilegge i sorgenti dei file da tradurre del job: un file mancante o cambiato diventa bloccato col motivo, uno che torna
        /// con l'hash del job riparte; chiamato sotto StateLock
        /// </summary>
        private void CheckJobSources()
        {
            foreach (string fileId in this._job.FileIds)
            {
                TranslationJobFileState state = this._jobFiles[fileId];
                if (state.TranslationStatus == TranslationFileStatus.Completed)
                    continue;

                TranslationProjectFile file = this._project.Files.Find(f => f.Id == fileId);
                this.PrepareFile(file);
                if (!this._documents.ContainsKey(fileId))
                {
                    if (state.TranslationStatus == TranslationFileStatus.Blocked && state.BlockedReason == file.RejectedReason)
                        continue;
                    state.TranslationStatus = TranslationFileStatus.Blocked;
                    state.BlockedReason = file.RejectedReason;
                }
                else if (state.TranslationStatus == TranslationFileStatus.Blocked)
                {
                    state.TranslationStatus = state.Blocks.Exists(b => b.Status == TranslationBlockStatus.Done) ? TranslationFileStatus.InProgress : TranslationFileStatus.Pending;
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
        /// file, coi candidati ricorrenti e il glossario caricato; chiamato sotto StateLock
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
            if (episodes.Count == 1 || GlossaryBuilder.EstimateTokens(builder.AnalysisInstructions + allMessage) < GlossaryBuilder.SINGLE_ANALYSIS_TOKENS)
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
        /// Sequenza di chiamate di A1 o A2: al massimo tre richieste con risposta, coi giri di correzione, e una riga di registro
        /// per tentativo di trasporto
        /// </summary>
        /// <param name="provider">Provider del modello</param>
        /// <param name="builder">Richieste e validazione del glossario</param>
        /// <param name="kind">A1 o A2</param>
        /// <param name="fileId">File della chiamata, TranslationSessionKey.NO_FILE su più file</param>
        /// <param name="cues">Battute mandate, null per A2</param>
        /// <param name="instructions">Instructions della chiamata</param>
        /// <param name="message">Messaggio iniziale</param>
        /// <param name="webSearch">True per offrire la ricerca web</param>
        /// <param name="merge">Conflitti per A2, null per A1</param>
        /// <param name="cancellation">Annullamento dell'operazione</param>
        /// <returns>Glossario consegnato</returns>
        private async Task<Glossary> RunGlossaryCallsAsync(ITranslationModelProvider provider, GlossaryBuilder builder, TranslationCallKind kind, string fileId, int? cues, string instructions, string message,
            bool webSearch, GlossaryMergeResult merge, CancellationToken cancellation)
        {
            JsonArray input = new JsonArray(TranslationRequestBuilder.CreateUserMessage(message));
            List<string> errors = new List<string>();

            for (int round = 1; round <= GlossaryBuilder.MAX_ROUNDS; round++)
            {
                TranslationTransportAttempt delivered = null;
                int currentRound = round;
                TranslationModelRequest request = new TranslationModelRequest();
                request.Kind = kind;
                request.ProjectId = this._project.Id;
                request.FileId = fileId;
                request.Request.Model = this._project.Models.Analysis;
                request.Request.ReasoningEffort = this._project.ReasoningEfforts.Analysis ?? "";
                request.Request.Instructions = instructions;
                request.Request.Input = input;
                request.Request.Tools = builder.CreateTools(webSearch);

                // I tentativi falliti si registrano subito; quello riuscito dopo la validazione, con l'esito della consegna
                // Anche il tentativo interrotto da Stop ha la sua riga, senza tipo d'errore: il consumo è reale
                request.AttemptFinished = attempt =>
                {
                    if (attempt.Error == null && !attempt.Stopped)
                    {
                        delivered = attempt;
                        return;
                    }

                    TranslationCallRecord failed = this.CreateCallRecord(attempt, kind, fileId, cues, currentRound);
                    failed.ResponseStatus = TranslationCallResponseStatus.Error;
                    failed.Outcome = TranslationCallOutcome.Error;
                    if (attempt.Error != null)
                    {
                        failed.HttpStatus = attempt.Error.HttpStatus;
                        failed.ErrorKind = attempt.Error.Kind;
                        failed.ServiceCode = string.IsNullOrEmpty(attempt.Error.ServiceCode) ? null : attempt.Error.ServiceCode;
                    }
                    this.RecordCall(failed);
                };

                AiResponseResult response = await provider.SendAsync(request, cancellation);
                TranslationCallRecord record = this.CreateCallRecord(delivered, kind, fileId, cues, round);
                record.HttpStatus = response.HttpStatus;

                if (response.Status == AiResponseStatus.Incomplete)
                {
                    record.ResponseStatus = TranslationCallResponseStatus.Incomplete;
                    record.IncompleteReason = response.IncompleteReason;
                    record.Outcome = TranslationCallOutcome.Reduced;
                    record.Usage = new TranslationCallUsage(response.Usage, 0);
                    this.RecordCall(record);
                    throw new AiServiceException(AiErrorKind.InvalidResponse, AppText.T("ai.error.incomplete"));
                }

                GlossaryDelivery delivery = builder.ReadResponse(response, input, merge);
                record.Usage = new TranslationCallUsage(response.Usage, delivery.WebSearchCalls);
                record.Outcome = delivery.Glossary != null ? TranslationCallOutcome.Accepted : TranslationCallOutcome.PartiallyRejected;
                if (delivery.Glossary == null)
                    record.Rejections.Add(new TranslationCallRejection { Reasons = delivery.Errors });
                this.RecordCall(record);

                if (delivery.WebSearchCalls > 0)
                    this._project.Analysis.WebSearchUsed = true;
                if (delivery.Glossary != null)
                    return delivery.Glossary;

                errors = delivery.Errors;
                this.AppendLog(AppText.F(TEXT_PREFIX + ".deliveryRejected", kind.ToString(), round, string.Join(" ", errors)));
            }

            throw new AiServiceException(AiErrorKind.InvalidResponse, AppText.F(TEXT_PREFIX + ".deliveryInvalid", GlossaryBuilder.MAX_ROUNDS, string.Join(" ", errors)));
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
            TranslationCallRecord record = new TranslationCallRecord();
            record.Kind = kind;
            record.FileId = fileId == TranslationSessionKey.NO_FILE ? null : fileId;
            record.RequestedIds = cues;
            record.Round = round;
            record.TransportAttempt = attempt.Number;
            record.Model = attempt.Model;
            record.ReasoningEffort = attempt.ReasoningEffort;
            record.SessionId = attempt.SessionId;
            record.ServerCacheKey = attempt.Result != null && !string.IsNullOrEmpty(attempt.Result.PromptCacheKey) ? attempt.Result.PromptCacheKey : null;
            record.StartedAt = attempt.StartedAt;
            record.DurationMs = attempt.DurationMs;
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
        /// Prepara un file del progetto e ne tiene il documento; con l'analisi partita il file tiene le impronte salvate
        /// </summary>
        /// <param name="file">File del progetto</param>
        private void PrepareFile(TranslationProjectFile file)
        {
            SubtitleTranslationDocument document = TranslationFilePreparer.Prepare(file, this._project.SourceLanguage, this._project.Analysis != null);
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
        /// True se il glossario si può caricare o togliere: fino all'approvazione, con le lingue scelte
        /// </summary>
        public bool CanImportGlossary
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._project != null && !string.IsNullOrEmpty(this._project.SourceLanguage) && !string.IsNullOrEmpty(this._project.TargetLanguage) &&
                        (this._project.Glossary == null || this._project.Glossary.Status != GlossaryStatus.Approved) &&
                        (this._project.Analysis == null || this._project.Analysis.Status != TranslationAnalysisStatus.Running);
                }
            }
        }

        /// <summary>
        /// True se il glossario proposto si può approvare: nessun termine con originale o resa vuota
        /// </summary>
        public bool CanApprove
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._project != null && !this.IsBusy && this._project.Analysis != null && this._project.Analysis.Status == TranslationAnalysisStatus.Completed &&
                        this._project.Glossary != null && this._project.Glossary.Status == GlossaryStatus.Draft &&
                        !this._project.Glossary.Terms.Exists(t => string.IsNullOrWhiteSpace(t.Source) || string.IsNullOrWhiteSpace(t.Target));
                }
            }
        }

        /// <summary>
        /// Job attivo del progetto aperto, null prima di «Avvia traduzione»
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
        /// conclusi, anche bloccati, l'applicazione dei termini o la revisione AI senza lavoro in corso
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

                    if (this._job.Phase == TranslationJobPhase.Applying || this._job.Phase == TranslationJobPhase.Review)
                        return this._job.Status != TranslationJobStatus.Running;

                    return this._job.Phase == TranslationJobPhase.Translation && this._job.FileIds.Exists(id => this._jobFiles[id].TranslationStatus != TranslationFileStatus.Completed);
                }
            }
        }

        /// <summary>
        /// True se «Conferma e prosegui» è attivo: giro fermo sui termini nuovi, ogni termine aggiunto con una resa
        /// </summary>
        public bool CanConfirmNewTerms
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._job != null && !this.IsBusy && this._job.Phase == TranslationJobPhase.NewTerms && this._job.Status == TranslationJobStatus.Idle &&
                        !this._job.NewTerms.Exists(t => t.Decision == NewTermDecisionKind.Add && string.IsNullOrWhiteSpace(t.ChosenTarget));
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
        /// File delle credenziali dell'accesso
        /// </summary>
        public string CredentialsPath
        {
            get { return Path.Combine(AppSettingsService.Instance.ConfigFolder, AiCredentialService.FILE_NAME); }
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
            /// Battute mandate da ogni A1
            /// </summary>
            public List<int> Cues { get; }
        }

        #endregion
    }

    /// <summary>
    /// Stima dell'analisi prima di «Analizza»: chiamate A1 che mancano e token in ingresso stimati per caratteri
    /// </summary>
    public sealed class TranslationAnalysisEstimate
    {
        #region Proprietà

        /// <summary>
        /// File preparati del progetto
        /// </summary>
        public int Files { get; set; }

        /// <summary>
        /// Battute mandate dalle chiamate che mancano
        /// </summary>
        public int Cues { get; set; }

        /// <summary>
        /// Chiamate A1 che mancano
        /// </summary>
        public int Calls { get; set; }

        /// <summary>
        /// Token in ingresso stimati
        /// </summary>
        public long InputTokens { get; set; }

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
        /// Termini nuovi distinti segnalati
        /// </summary>
        public int NewTerms { get; set; }

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
        /// Fermata del giro nella fase, null per la fase conclusa
        /// </summary>
        public TranslationStop Stop { get; set; }

        #endregion
    }

    /// <summary>
    /// Riga della griglia dell'applicazione dei termini: un file del job
    /// </summary>
    public sealed class TermApplicationRow
    {
        #region Proprietà

        /// <summary>
        /// Nome del file sorgente
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// Stato dell'applicazione
        /// </summary>
        public TermApplicationStatus Status { get; set; }

        /// <summary>
        /// Battute coinvolte, null prima che il file sia concluso
        /// </summary>
        public int? Targets { get; set; }

        /// <summary>
        /// Battute riscritte, null prima che il file sia concluso
        /// </summary>
        public int? Rewritten { get; set; }

        /// <summary>
        /// Riscritture non riuscite, col testo di prima
        /// </summary>
        public int Failed { get; set; }

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
        /// Frase spezzata della battuta, null se nessuna
        /// </summary>
        public int? Group { get; set; }

        /// <summary>
        /// Traduzione, null senza testo tradotto
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// True per una battuta da rivedere o senza traduzione
        /// </summary>
        public bool ToReview { get; set; }

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
