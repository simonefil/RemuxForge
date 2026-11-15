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
    /// A1/A2 in background col glossario proposto, approvazione ed export del glossario, registro delle chiamate
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
        /// Progetto aperto, null nell'elenco dei progetti
        /// </summary>
        private TranslationProject _project;

        /// <summary>
        /// Frase dell'attesa durante l'analisi
        /// </summary>
        private string _phrase;

        /// <summary>
        /// Riga secondaria dell'attesa durante l'analisi
        /// </summary>
        private string _detail;

        /// <summary>
        /// Ultima stima dell'analisi, valida finché il progetto non cambia
        /// </summary>
        private TranslationAnalysisEstimate _estimate;

        /// <summary>
        /// Stato del progetto a cui si riferisce la stima
        /// </summary>
        private string _estimateKey;

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

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="store">Progetti su disco</param>
        /// <param name="providerFactory">Provider del modello</param>
        /// <param name="credentialService">Configurazione dell'accesso AI</param>
        public SubtitleTranslationOrchestrator(TranslationProjectStore store, TranslationModelProviderFactory providerFactory, AiCredentialService credentialService) : base(TEXT_PREFIX)
        {
            this._store = store;
            this._providerFactory = providerFactory;
            this._credentialService = credentialService;
            this._documents = new Dictionary<string, SubtitleTranslationDocument>(StringComparer.Ordinal);
            this._calls = new List<TranslationCallRecord>();
            this._project = null;
            this._phrase = "";
            this._detail = "";
            this._estimate = null;
            this._estimateKey = "";
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
                this._project = this._store.Create();
            }

            this.AppendLog(AppText.T(TEXT_PREFIX + ".projectCreated"));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Apre un progetto salvato e ne prepara i file; un'analisi rimasta in corso da una chiusura dell'app diventa fermata, e
        /// un file bloccato dall'analisi che è cambiato sul disco resta rifiutato col motivo
        /// </summary>
        /// <param name="id">Identificativo del progetto</param>
        public void OpenProject(string id)
        {
            TranslationProject project = this._store.Load(id);
            List<TranslationCallRecord> calls = this._store.ReadCalls(id);

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
                this._project = project;
                foreach (TranslationProjectFile file in project.Files)
                {
                    this.Records.Add(new TranslationFileRecord(file));
                    this.PrepareFile(file);
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
        /// Cambia la modalità della revisione AI
        /// </summary>
        /// <param name="mode">Mirata o completa</param>
        public void SetReviewMode(TranslationReviewMode mode)
        {
            lock (this.StateLock)
            {
                if (this._project == null || this.IsBusy)
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
        /// Modello di partenza di un passo: quello globale dell'accesso se c'è fra i modelli, altrimenti il primo
        /// </summary>
        /// <param name="models">Modelli dell'accesso</param>
        /// <returns>Modello, null senza modelli</returns>
        public AiModelInfo GetDefaultModel(List<AiModelInfo> models)
        {
            string global = this._credentialService.Load().Model;
            AiModelInfo model = models.Find(m => m.Id == global);
            return model != null ? model : models.Count > 0 ? models[0] : null;
        }

        /// <summary>
        /// Salva modelli e livelli di ragionamento dei passi
        /// </summary>
        /// <param name="models">Modello per passo</param>
        /// <param name="efforts">Livello per passo</param>
        public void SetModels(TranslationPhaseModels models, TranslationPhaseModels efforts)
        {
            lock (this.StateLock)
            {
                if (this._project == null || this.IsBusy)
                    return;

                this._project.Models = models;
                this._project.ReasoningEfforts = efforts;
                this._store.Save(this._project);
            }

            this.AppendLog(AppText.F(TEXT_PREFIX + ".modelsChanged", models.Analysis, models.Translation, models.NewTerms, models.ResolveReview()));
            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Registra il consenso dedicato alla traduzione
        /// </summary>
        public void AcceptConsent()
        {
            this._credentialService.AcceptTranslationConsent();
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
        /// Salva le modifiche fatte al glossario dalla pagina; un glossario approvato torna in bozza
        /// </summary>
        /// <param name="change">Modifica, eseguita sotto il lock dello stato</param>
        public void ChangeGlossary(Action<Glossary> change)
        {
            lock (this.StateLock)
            {
                if (this._project == null || this._project.Glossary == null || this.IsBusy)
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
                webSearch = provider.Capabilities.WebSearch;

                // Modelli e livelli coi default del catalogo, se il dialog non li ha ancora scelti
                if (string.IsNullOrEmpty(project.Models.Analysis))
                {
                    List<AiModelInfo> models = await provider.ListModelsAsync(cancellation);
                    AiModelInfo model = this.GetDefaultModel(models);
                    if (model == null)
                        throw new AiServiceException(AiErrorKind.NotConfigured, AppText.T(TEXT_PREFIX + ".noModels"));

                    lock (this.StateLock)
                    {
                        project.Models = new TranslationPhaseModels { Analysis = model.Id, Translation = model.Id, NewTerms = model.Id };
                        project.ReasoningEfforts = new TranslationPhaseModels { Analysis = model.DefaultReasoningLevel, Translation = model.DefaultReasoningLevel, NewTerms = model.DefaultReasoningLevel };
                        this._store.Save(project);
                    }
                }

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
                // Le eccezioni che non sono dell'accesso restano errori del servizio: il worker non deve chiudere l'app. Il timeout
                // del client non è uno Stop: lo è solo l'annullamento chiesto dall'utente
                AiServiceException service = ex as AiServiceException;
                TranslationStop stop = new TranslationStop();
                if (this.StopRequested || this.OperationCancellationToken.IsCancellationRequested)
                {
                    stop.Reason = TranslationStopReason.Stop;
                }
                else if (service != null && service.Kind == AiErrorKind.UsageLimit)
                {
                    stop.Reason = TranslationStopReason.UsageLimit;
                    stop.Message = service.Message;
                }
                else if (service != null && (service.Kind == AiErrorKind.NotConfigured || service.Kind == AiErrorKind.SignInRequired || service.Kind == AiErrorKind.InvalidApiKey ||
                    service.Kind == AiErrorKind.InvalidSignIn || service.Kind == AiErrorKind.NotEligible))
                {
                    stop.Reason = TranslationStopReason.AccessError;
                    stop.Message = service.Message;
                }
                else
                {
                    stop.Reason = TranslationStopReason.ServiceError;
                    stop.Message = ex.Message;
                }

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
                    restored.Terms.Add(discard.Term);
                else if (discard.Character != null && !restored.Characters.Exists(c => GlossaryBuilder.CharacterKey(c.Name) == GlossaryBuilder.CharacterKey(discard.Character.Name)))
                    restored.Characters.Add(discard.Character);
                else if (discard.Addressing != null &&
                    !restored.Addressing.Exists(a => GlossaryBuilder.AddressingKey(a.Speaker, a.Addressee) == GlossaryBuilder.AddressingKey(discard.Addressing.Speaker, discard.Addressing.Addressee)))
                    restored.Addressing.Add(discard.Addressing);
                else if (discard.Kind == GlossaryEntryKind.Series)
                    restored.Series = discard.Delivered;
                else if (discard.Kind == GlossaryEntryKind.Tone)
                    restored.Tone = discard.Delivered;
                else if (discard.Kind == GlossaryEntryKind.RegisterRules)
                    restored.RegisterRules = discard.Delivered;
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
                this._documents[file.Id] = document;
            else
                this._documents.Remove(file.Id);
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
        /// True se il consenso dedicato alla traduzione è già stato dato
        /// </summary>
        public bool ConsentAccepted
        {
            get { return this._credentialService.Load().TranslationConsentAccepted; }
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
        /// Riga secondaria dell'attesa durante l'analisi
        /// </summary>
        public string Detail
        {
            get
            {
                lock (this.StateLock)
                {
                    return this._detail;
                }
            }
        }

        #endregion

        #region Classi annidate

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
