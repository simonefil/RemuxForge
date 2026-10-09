using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Tools;
using RemuxForge.Core.Splitting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text.Json;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Orchestratore WebUI per modalità split
    /// </summary>
    public partial class SplitOrchestrator : MediaOrchestratorBase, IMediaSourceResolver
    {
        #region Variabili statiche

        /// <summary>
        /// Opzioni JSON usate per la copia profonda via serializzazione
        /// </summary>
        private static readonly System.Text.Json.JsonSerializerOptions s_copyOptions = new System.Text.Json.JsonSerializerOptions {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Opzioni split correnti
        /// </summary>
        private Options _options;

        /// <summary>
        /// Record split correnti
        /// </summary>
        private List<MkvSplitRecord> _records;

        /// <summary>
        /// Revisione delle opzioni, incrementata a ogni applicazione
        /// </summary>
        private long _optionsRevision;

        /// <summary>
        /// Montaggi applicati, indicizzati per file di ingresso
        /// </summary>
        private readonly Dictionary<string, AppliedMontage> _applied = new Dictionary<string, AppliedMontage>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Sessioni editor aperte, indicizzate per identificativo di sessione
        /// </summary>
        private readonly Dictionary<Guid, EditorSession> _sessions = new Dictionary<Guid, EditorSession>();

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public SplitOrchestrator() : base("web.split.ready", false)
        {
            this._options = new Options();
            this._options.Mode = Options.MODE_SPLIT;
            this._records = new List<MkvSplitRecord>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Opzioni correnti
        /// </summary>
        public Options CurrentOptions
        {
            get
            {
                lock (this.StateLock) { return CloneOptions(this._options); }
            }
        }

        /// <summary>
        /// Sovrascrittura consentita dalle opzioni Split
        /// </summary>
        public bool JoinForce
        {
            get
            {
                lock (this.StateLock) { return this._options != null && this._options.Split.Force; }
            }
        }

        /// <summary>
        /// True quando una sorgente Split è stata configurata
        /// </summary>
        public bool HasSource
        {
            get
            {
                lock (this.StateLock) { return this._options != null && this._options.Split != null && !string.IsNullOrEmpty(this._options.Split.SourcePath); }
            }
        }

        #endregion

        #region Eventi

        /// <summary>
        /// Evento fine analisi, con il riassunto dell'esito
        /// </summary>
        public event Action<OperationSummary> OnAnalysisCompleted;

        /// <summary>
        /// Evento fine split, con il riassunto dell'esito
        /// </summary>
        public event Action<OperationSummary> OnSplitCompleted;

        /// <summary>
        /// Evento emesso quando un'operazione non può partire
        /// </summary>
        public event Action<string> OnOperationFailed;

        /// <summary>
        /// Evento fine unione parti, con l'esito
        /// </summary>
        public event Action<SplitJoinResult> OnJoinCompleted;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Applica opzioni split
        /// </summary>
        /// <param name="options">Opzioni da applicare</param>
        /// <param name="errorMessage">Messaggio di errore, vuoto se l'applicazione riesce</param>
        /// <returns>True se le opzioni sono state applicate</returns>
        public bool ApplyOptions(Options options, out string errorMessage)
        {
            OptionsValidationResult validation;
            errorMessage = "";
            if (options == null)
            {
                errorMessage = AppText.T("validation.invalidConfig");
                return false;
            }

            options.Mode = Options.MODE_SPLIT;
            options.Split.SourcePath = options.SourceFolder;
            validation = OptionsValidator.Validate(options, false, false);
            if (!validation.IsValid)
            {
                errorMessage = string.Join("\n", validation.Errors);
                return false;
            }

            lock (this.StateLock)
            {
                if (this.BusyState)
                {
                    errorMessage = AppText.T("split.montage.busy");
                    return false;
                }
                this._options = CloneOptions(options);
                this._optionsRevision++;
                foreach (MkvSplitRecord record in this._records)
                    if (!record.IsOverride)
                    {
                        this._applied.Remove(record.InputFile);
                        record.Plan = null;
                        record.MontageProjection = null;
                        record.Status = MkvSplitStatus.Pending;
                        record.Success = false;
                        record.OutputResults.Clear();
                    }
            }
            this.AppendLog(AppText.T("web.split.configApplied"));
            return true;
        }

        /// <summary>
        /// Esegue scan della sorgente
        /// </summary>
        public void Scan()
        {
            lock (this.StateLock)
            {
                if (this.BusyState) return;
                this.BusyState = true;
            }

            Thread thread = new Thread(this.ScanWorker);
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Costruisce il piano dei record indicati
        /// </summary>
        /// <param name="indices">Indici da analizzare, null per tutti</param>
        public void Analyze(List<int> indices)
        {
            List<int> targets;

            if (this.BusyState)
            {
                this.RejectOperation(AppText.T("web.split.analyzeBusy"));
                return;
            }

            targets = this.ResolveTargets(indices);
            if (targets.Count == 0)
            {
                this.RejectOperation(AppText.T("web.split.noAnalyzeTargets"));
                return;
            }

            Thread thread = new Thread(() => this.AnalyzeWorker(targets));
            lock (this.StateLock)
            {
                if (this.BusyState) return;
                this.BusyState = true;
            }
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Esegue lo split dei record indicati
        /// </summary>
        /// <param name="indices">Indici da tagliare, null per tutti</param>
        public void Split(List<int> indices)
        {
            List<int> targets;

            if (this.BusyState)
            {
                this.RejectOperation(AppText.T("web.split.splitBusy"));
                return;
            }

            targets = this.ResolveTargets(indices);
            if (targets.Count == 0)
            {
                this.RejectOperation(AppText.T("web.split.noSplitTargets"));
                return;
            }

            Thread thread = new Thread(() => this.SplitWorker(targets));
            lock (this.StateLock)
            {
                if (this.BusyState) return;
                this.BusyState = true;
            }
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Esegue split di tutti i record
        /// </summary>
        public void SplitAll()
        {
            this.Split(null);
        }

        /// <summary>
        /// Legge le parti indicate in background, senza occupare l'orchestrator: il piano si costruisce poi con MkvSplitJoinService
        /// </summary>
        /// <param name="paths">File da leggere</param>
        /// <param name="cancellation">Annullamento della lettura</param>
        /// <returns>Parti lette, nello stesso ordine</returns>
        public async Task<List<MkvSplitJoinPart>> ReadJoinPartsAsync(List<string> paths, CancellationToken cancellation)
        {
            return await Task.Run(() =>
            {
                MkvSplitExternalTools.Instance.ResolveBinaries();
                MkvSplitJoinService service = new MkvSplitJoinService();
                List<MkvSplitJoinPart> parts = new List<MkvSplitJoinPart>();
                foreach (string path in paths)
                {
                    cancellation.ThrowIfCancellationRequested();
                    parts.Add(service.ReadPart(path));
                }
                return parts;
            }, cancellation);
        }

        /// <summary>
        /// Cartella dell'output unito: quella configurata per Split, altrimenti quella della prima parte (C12)
        /// </summary>
        /// <param name="firstPartPath">Percorso della prima parte</param>
        /// <returns>Cartella di destinazione</returns>
        public string GetJoinOutputDir(string firstPartPath)
        {
            lock (this.StateLock)
            {
                return MkvSplitJoinService.ResolveOutputDir(this._options?.Split.OutputDir, firstPartPath);
            }
        }

        /// <summary>
        /// Avvia l'unione in background; l'esito arriva con OnJoinCompleted
        /// </summary>
        /// <param name="plan">Piano valido</param>
        /// <param name="fileName">Nome file scelto</param>
        public void Join(MkvSplitJoinPlan plan, string fileName)
        {
            if (this.BusyState)
            {
                this.RejectOperation(AppText.T("web.splitJoin.busy"));
                return;
            }
            if (plan == null || !plan.IsValid)
            {
                this.RejectOperation(plan == null ? AppText.T("split.join.tooFewParts") : string.Join(" ", plan.Issues));
                return;
            }

            Thread thread = new Thread(() => this.JoinWorker(plan, fileName));
            lock (this.StateLock)
            {
                if (this.BusyState) return;
                this.BusyState = true;
            }
            thread.IsBackground = true;
            thread.Start();
        }

        /// <summary>
        /// Richiede stop cooperativo
        /// </summary>
        public void Stop()
        {
            this.RequestStop(AppText.T("web.split.stopRequested"));
        }

        /// <summary>
        /// Svuota la modalità: elenco file, montaggi applicati, sessioni editor, configurazione e log
        /// </summary>
        /// <returns>True se lo stato è stato svuotato, false durante un'operazione</returns>
        public bool Clear()
        {
            lock (this.StateLock)
            {
                if (this.BusyState) return false;
                this._records.Clear();
                this._applied.Clear();
                this._sessions.Clear();
                this._options = new Options { Mode = Options.MODE_SPLIT };
                this._optionsRevision++;
                this.ResetIdleWorkState();
            }
            this.NotifyRecordsChanged();
            this.NotifyProgressChanged();
            return true;
        }

        /// <summary>
        /// Esclude o reinclude i record indicati
        /// </summary>
        /// <param name="indices">Indici da aggiornare</param>
        /// <param name="skipped">True per escludere</param>
        public void SetSkipped(List<int> indices, bool skipped)
        {
            lock (this.StateLock)
            {
                if (this.BusyState) return;
                foreach (int index in indices)
                {
                    if (index < 0 || index >= this._records.Count)
                        continue;

                    this._records[index].Skipped = skipped;
                    this._records[index].Status = skipped ? MkvSplitStatus.Skipped : MkvSplitStatus.Pending;
                }
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Numero di file che hanno segmenti costruiti nell'editor
        /// </summary>
        /// <returns>Conteggio degli override attivi</returns>
        public int CountOverrides()
        {
            lock (this.StateLock)
            {
                return this._records.Count(record => record.IsOverride);
            }
        }

        /// <summary>
        /// Restituisce copia dell'elenco record
        /// </summary>
        /// <returns>Elenco record</returns>
        public List<MkvSplitRecord> GetRecords()
        {
            lock (this.StateLock)
            {
                // Copia superficiale: la copia profonda serializzerebbe PTS e keyframe di ogni record a ogni notifica
                return new List<MkvSplitRecord>(this._records);
            }
        }

        /// <summary>
        /// Prepara il draft senza avviare export; la sessione resta legata all'identità del media.
        /// </summary>
        /// <param name="recordIndex">Indice del record da aprire nell'editor</param>
        /// <param name="cancellation">Annullamento della preparazione</param>
        /// <returns>Esito dell'apertura con lo snapshot dell'editor</returns>
        public async Task<SplitEditorOpenResult> OpenEditorAsync(int recordIndex, CancellationToken cancellation)
        {
            MkvSplitRecord record;
            Options options;
            long revision;
            lock (this.StateLock)
            {
                if (this.BusyState) return new SplitEditorOpenResult { Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic("busy") } };
                record = this.GetRecordAt(recordIndex);
                if (record == null) return new SplitEditorOpenResult { Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic("conflict") } };
                options = CloneOptions(this._options);
                revision = this._optionsRevision;
                this.BusyState = true;
            }
            try
            {
                cancellation.ThrowIfCancellationRequested();
                MkvSplitSourceIdentity source = MkvSplitSourceIdentity.FromFile(record.InputFile);
                MkvSplitAnalysis analysis = await Task.Run(() =>
                {
                    MkvSplitExternalTools.Instance.ResolveBinaries();
                    return MkvSplitAnalysisCache.Instance.GetOrBuild(record.InputFile);
                }, cancellation);
                cancellation.ThrowIfCancellationRequested();
                MkvFileInfo sourceInfo = await Task.Run(() => MkvSplitExternalTools.Instance.GetFileInfo(record.InputFile), cancellation);
                if (sourceInfo?.Tracks == null || !sourceInfo.Tracks.Any(track => track.Type == "video"))
                    throw new InvalidOperationException(AppText.T("split.montage.trackInventory"));
                cancellation.ThrowIfCancellationRequested();
                MkvSplitDocumentService service = new MkvSplitDocumentService();
                MkvSplitOptions split = CloneSplitOptionsFor(options);
                MkvSplitDocument document;
                long appliedRevision;
                lock (this.StateLock)
                {
                    if (revision != this._optionsRevision || !source.Matches(MkvSplitSourceIdentity.FromFile(record.InputFile))
                        || !this._records.Any(item => item.RecordId == record.RecordId))
                        return new SplitEditorOpenResult { Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic("conflict") } };
                    if (this._applied.TryGetValue(record.InputFile, out AppliedMontage applied) && applied.Document.Source.Matches(source))
                    {
                        document = applied.Document.Clone();
                        appliedRevision = applied.Revision;
                    }
                    else
                    {
                        document = null;
                        appliedRevision = 0;
                    }
                }
                if (document == null)
                {
                    MkvSplitPlan legacy = split.Manual ? null : await Task.Run(() => new MkvSplitPlanner().BuildPlan(split, record.InputFile, null), cancellation);
                    document = split.Manual ? service.CreateFullSource(source, analysis) : service.FromLegacyPlan(legacy, source);
                }
                cancellation.ThrowIfCancellationRequested();
                Guid sessionId = Guid.NewGuid();
                lock (this.StateLock)
                {
                    if (!source.Matches(MkvSplitSourceIdentity.FromFile(record.InputFile)) || revision != this._optionsRevision)
                        return new SplitEditorOpenResult { Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic("conflict") } };
                    record.SourceInfo = Copy(sourceInfo);
                    this._sessions.Add(sessionId, new EditorSession { RecordId = record.RecordId, Source = source.Clone(), DocumentId = document.Id,
                        Analysis = analysis, SourceInfo = sourceInfo });
                }
                return new SplitEditorOpenResult { Snapshot = new SplitEditorSnapshot {
                    SessionId = sessionId,
                    ExpectedAppliedRevision = appliedRevision, ExpectedOptionsRevision = revision, Document = document,
                    Analysis = Copy(analysis), SourceInfo = Copy(sourceInfo), Options = Copy(split) } };
            }
            catch (OperationCanceledException) { return new SplitEditorOpenResult(); }
            catch (Exception exception) { return new SplitEditorOpenResult { Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic("preparationFailed", exception.Message) } }; }
            finally
            {
                lock (this.StateLock) { this.BusyState = false; }
                this.NotifyProgressChanged();
            }
        }

        /// <summary>
        /// Chiude una sessione editor
        /// </summary>
        /// <param name="sessionId">Identificativo della sessione</param>
        public void CloseEditor(Guid sessionId)
        {
            lock (this.StateLock) { this._sessions.Remove(sessionId); }
        }

        /// <summary>
        /// Applica il draft dell'editor al record della sessione
        /// </summary>
        /// <param name="request">Richiesta di applicazione</param>
        /// <param name="cancellation">Annullamento dell'applicazione</param>
        /// <returns>Esito dell'applicazione</returns>
        public Task<SplitApplyResult> ApplyDraftAsync(SplitApplyRequest request, CancellationToken cancellation)
        {
            return this.ApplyCandidateAsync(request, false, cancellation);
        }

        /// <summary>
        /// Reset esplicito: in manuale ripristina il sorgente intero, in batch rigenera la regola.
        /// </summary>
        /// <param name="sessionId">Identificativo della sessione</param>
        /// <param name="cancellation">Annullamento del reset</param>
        /// <returns>Esito dell'applicazione</returns>
        public Task<SplitApplyResult> ReapplyRuleAsync(Guid sessionId, CancellationToken cancellation)
        {
            lock (this.StateLock)
            {
                if (!this._sessions.TryGetValue(sessionId, out EditorSession session)) return Task.FromResult(ApplyFailure(SplitApplyStatus.Conflict, "conflict"));
                this._applied.TryGetValue(session.Source.FullPath, out AppliedMontage applied);
                return this.ApplyCandidateAsync(new SplitApplyRequest { SessionId = sessionId, ExpectedAppliedRevision = applied?.Revision ?? 0,
                    ExpectedOptionsRevision = this._optionsRevision, DraftRevision = session.LastDraftRevision + 1 }, true, cancellation);
            }
        }

        /// <summary>
        /// Calcola il documento della regola per il draft dell'editor: in manuale il sorgente intero, in batch la regola corrente.
        /// Il record resta invariato; l'applicazione passa da ApplyDraftAsync.
        /// </summary>
        /// <param name="sessionId">Identificativo della sessione</param>
        /// <param name="expectedOptionsRevision">Revisione delle opzioni vista dall'editor</param>
        /// <param name="cancellation">Annullamento del calcolo</param>
        /// <returns>Documento della regola con l'identità del draft della sessione</returns>
        public async Task<SplitRuleDocumentResult> BuildRuleDocumentAsync(Guid sessionId, long expectedOptionsRevision, CancellationToken cancellation)
        {
            EditorSession session;
            MkvSplitRecord record;
            MkvSplitOptions options;
            lock (this.StateLock)
            {
                if (!this._sessions.TryGetValue(sessionId, out session) || expectedOptionsRevision != this._optionsRevision) return RuleFailure("conflict");
                if (this.BusyState) return RuleFailure("busy");
                record = this._records.Find(item => item.RecordId == session.RecordId);
                if (record == null) return RuleFailure("conflict");
                options = CloneSplitOptionsFor(this._options);
            }
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (!session.Source.Matches(MkvSplitSourceIdentity.FromFile(record.InputFile))) return RuleFailure("sourceChanged");
                MkvSplitPlan legacy = null;
                if (!options.Manual)
                {
                    legacy = await Task.Run(() => new MkvSplitPlanner().BuildPlan(options, record.InputFile, null), cancellation);
                    if (!legacy.IsValid) return RuleFailure("preparationFailed", legacy.ErrorMessage);
                }
                cancellation.ThrowIfCancellationRequested();
                MkvSplitDocument document = new MkvSplitDocumentService().CreateRuleDocument(legacy, session.Source, session.Analysis, session.DocumentId);
                lock (this.StateLock)
                {
                    if (!this._sessions.TryGetValue(sessionId, out EditorSession current) || !ReferenceEquals(current, session) || expectedOptionsRevision != this._optionsRevision)
                        return RuleFailure("conflict");
                    // Applicare la regola senza modifiche equivale al reset: il record torna a seguire la configurazione globale
                    session.RuleDocument = JsonSerializer.Serialize(document);
                }
                return new SplitRuleDocumentResult { Document = document };
            }
            catch (OperationCanceledException) { return new SplitRuleDocumentResult(); }
            catch (Exception exception) { return RuleFailure("preparationFailed", exception.Message); }
        }

        /// <summary>
        /// Risolutore immutabile per una richiesta preview/audio vincolata alla sessione.
        /// </summary>
        /// <param name="sessionId">Identificativo della sessione</param>
        /// <returns>Risolutore della sessione, null se la sessione non è più valida</returns>
        public IMediaSourceResolver ResolveEditorMedia(Guid sessionId)
        {
            lock (this.StateLock)
            {
                if (!this._sessions.TryGetValue(sessionId, out EditorSession session)) return null;
                MkvSplitRecord record = this._records.Find(item => item.RecordId == session.RecordId);
                if (record == null || !session.Source.Matches(MkvSplitSourceIdentity.FromFile(session.Source.FullPath))) return null;
                return new SessionMediaResolver(new MediaSource(session.Source.FullPath,
                    Copy(session.SourceInfo.Tracks.FindAll(track => track.Type == "audio"))));
            }
        }

        /// <summary>
        /// Indica se lo scope split espone il lato richiesto
        /// </summary>
        /// <param name="side">Nome del lato</param>
        /// <returns>True solo per input: lo split lavora su un file solo</returns>
        public bool SupportsSide(string side)
        {
            return string.Equals(side, "input", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Risolve il file di ingresso di un record
        /// </summary>
        /// <param name="recordIndex">Indice del record</param>
        /// <param name="side">Nome del lato</param>
        /// <returns>Sorgente multimediale, null se il record non esiste</returns>
        public MediaSource ResolveMediaSource(int recordIndex, string side)
        {
            MkvSplitRecord record = this.GetRecordAt(recordIndex);
            if (record == null || !this.SupportsSide(side))
                return null;
            List<TrackInfo> tracks = record.SourceInfo != null ? record.SourceInfo.Tracks.FindAll(track => string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase)) : new List<TrackInfo>();
            return new MediaSource(record.InputFile, tracks);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Worker unione parti: riconvalida l'output, esegue e pubblica
        /// </summary>
        /// <param name="plan">Piano valido</param>
        /// <param name="fileName">Nome file scelto</param>
        private void JoinWorker(MkvSplitJoinPlan plan, string fileName)
        {
            SplitJoinResult result = new SplitJoinResult();

            this.SetBusy(true, AppText.T("web.progress.join"));
            this.StopRequested = false;
            ProcessRunner.SetStopRequestedCallback(this.IsStopRequested);
            ConsoleHelper.SetLogCallback((section, _, text) =>
            {
                string prefix = ConsoleHelper.FormatSectionPrefix(section);
                this.AppendLog(!string.IsNullOrEmpty(prefix) ? prefix + text : text);
            });

            try
            {
                MkvSplitExternalTools.Instance.ResolveBinaries();
                MkvSplitJoinService service = new MkvSplitJoinService();
                string outputDir = this.GetJoinOutputDir(plan.Parts[0].FilePath);
                bool force = this.JoinForce;

                // Fra l'anteprima e l'avvio l'output può essere comparso: si riconvalida qui
                List<string> issues = service.ValidateOutput(plan, outputDir, fileName, force, out string outputPath);
                if (issues.Count > 0)
                    throw new InvalidOperationException(string.Join(" ", issues));

                result.OutputPath = outputPath;
                this.ReportProgress(0, 1, Path.GetFileName(outputPath));
                service.Execute(plan, outputPath, force, this.IsStopRequested);
                result.Success = true;
            }
            catch (Exception ex)
            {
                result.Stopped = ex is OperationCanceledException || this.StopRequested;
                result.Message = result.Stopped ? AppText.T("web.split.stopRequested") : ex.Message;
                this.AppendLog(result.Stopped ? AppText.T("web.splitJoin.stopped") : AppText.F("web.splitJoin.failed", ex.Message));
            }
            finally
            {
                ConsoleHelper.ClearLogCallback();
                this.SetBusy(false, "");
                this.OnJoinCompleted?.Invoke(result);
            }
        }

        /// <summary>
        /// Worker scan
        /// </summary>
        private void ScanWorker()
        {
            this.SetBusy(true, AppText.T("web.progress.scanSplit"));
            try
            {
                List<MkvSplitRecord> scanned = this.ScanSource();
                lock (this.StateLock)
                {
                    foreach (MkvSplitRecord record in scanned)
                    {
                        MkvSplitRecord previous = this._records.Find(item => string.Equals(item.InputFile, record.InputFile, StringComparison.OrdinalIgnoreCase));
                        if (previous != null && this._applied.TryGetValue(record.InputFile, out AppliedMontage applied)
                            && applied.Document.Source.Matches(MkvSplitSourceIdentity.FromFile(record.InputFile)))
                        {
                            record.RecordId = previous.RecordId;
                            record.Plan = previous.Plan;
                            record.MontageProjection = previous.MontageProjection;
                            record.OutputResults = previous.OutputResults;
                            record.IsOverride = previous.IsOverride;
                            record.Segments = previous.Segments;
                            record.Status = previous.Status;
                            record.Success = previous.Success;
                            record.Skipped = previous.Skipped;
                            record.ErrorMessage = previous.ErrorMessage;
                        }
                        else this._applied.Remove(record.InputFile);
                    }
                    this._records = scanned;
                    this.SelectedIndexState = scanned.Count > 0 ? 0 : -1;
                    foreach (string path in this._applied.Keys.ToList())
                        if (!scanned.Any(record => string.Equals(record.InputFile, path, StringComparison.OrdinalIgnoreCase))) this._applied.Remove(path);
                }
                this.AppendLog(AppText.F("web.split.scanCompleted", scanned.Count));
                this.NotifyRecordsChanged();
            }
            catch (Exception ex)
            {
                this.AppendLog(AppText.F("web.split.scanError", ex.Message));
            }
            this.SetBusy(false, "");
        }

        /// <summary>
        /// Worker di analisi: costruisce il piano di ogni record indicato
        /// </summary>
        /// <param name="targets">Indici dei record da analizzare</param>
        private void AnalyzeWorker(List<int> targets)
        {
            MkvSplitPlanner planner = new MkvSplitPlanner();
            MkvSplitRecord record;
            MkvSplitPlan plan;
            Options options;
            OperationSummary summary = null;
            int plannedCount = 0;
            int invalidCount = 0;
            int warningFiles = 0;

            this.SetBusy(true, AppText.T("web.progress.analyzeSplit"));
            this.StopRequested = false;
            ProcessRunner.SetStopRequestedCallback(this.IsStopRequested);
            ConsoleHelper.SetLogCallback((section, _, text) =>
            {
                string prefix = ConsoleHelper.FormatSectionPrefix(section);
                this.AppendLog(!string.IsNullOrEmpty(prefix) ? prefix + text : text);
            });

            try
            {
                MkvSplitExternalTools.Instance.ResolveBinaries();
                options = this._options;
                for (int i = 0; i < targets.Count; i++)
                {
                    if (this.StopRequested)
                    {
                        this.SetRecordStatus(targets[i], MkvSplitStatus.Stopped, AppText.T("web.split.stopRequested"));
                        break;
                    }

                    // Il try sta dentro il ciclo: un file che esplode non deve abortire il
                    // batch ne' marcare Error gli altri record, come gia' fa la CLI
                    try
                    {
                        record = this.GetRecordAt(targets[i]);
                        if (record == null || record.Skipped)
                            continue;

                        this.ReportProgress(i, targets.Count, Path.GetFileName(record.InputFile));
                        this.SetRecordStatus(targets[i], MkvSplitStatus.Analyzing, "");

                        MkvSplitExecutionPlan montage = this.PrepareMontageForRecord(record, options);
                        plan = LegacySummary(montage);
                        planner.PrintPlan(plan);

                        lock (this.StateLock)
                        {
                            record.Plan = plan;
                            record.Segments = plan.Segments;
                            // Un montaggio con tutti gli output gia' prodotti resta Done, come impostato da CommitMontage
                            if (plan.IsValid) { record.Status = record.Success ? MkvSplitStatus.Done : MkvSplitStatus.Planned; }
                            else { record.Status = plan.Mode == MkvSplitMode.Manual ? MkvSplitStatus.Undefined : MkvSplitStatus.PlanInvalid; }
                            record.ErrorMessage = plan.IsValid ? "" : plan.ErrorMessage;
                        }

                        if (plan.IsValid)
                        {
                            plannedCount++;
                        }
                        else
                        {
                            invalidCount++;
                        }
                        if (plan.Warnings.Count > 0) { warningFiles++; }
                        this.NotifyRecordsChanged();
                    }
                    catch (Exception ex)
                    {
                        invalidCount++;
                        this.SetRecordStatus(targets[i], MkvSplitStatus.Error, ex.Message);
                        this.AppendLog(AppText.F("web.split.scanError", ex.Message));
                    }
                }

                summary = new OperationSummary(plannedCount, invalidCount, warningFiles, this.StopRequested);
                this.AppendLog(AppText.F("web.split.analyzeCompleted", plannedCount, invalidCount));
            }
            catch (Exception ex)
            {
                summary = new OperationSummary(plannedCount, invalidCount + 1, warningFiles, false);
                this.AppendLog(AppText.F("web.split.scanError", ex.Message));
            }
            finally
            {
                ConsoleHelper.ClearLogCallback();
                this.SetBusy(false, "");
                this.NotifyRecordsChanged();
                this.OnAnalysisCompleted?.Invoke(summary);
            }
        }

        /// <summary>
        /// Worker di split: esegue il piano dei record indicati
        /// </summary>
        /// <param name="targets">Indici dei record da tagliare</param>
        private void SplitWorker(List<int> targets)
        {
            MkvSplitPipeline pipeline = new MkvSplitPipeline();
            MkvSplitPlanner planner = new MkvSplitPlanner();
            MkvSplitRecord record;
            MkvSplitPlan plan;
            Options options;
            OperationSummary summary = null;
            int successCount = 0;
            int errorCount = 0;
            int skippedCount = 0;
            int exitCode;

            this.SetBusy(true, AppText.T("web.progress.split"));
            this.StopRequested = false;
            ProcessRunner.SetStopRequestedCallback(this.IsStopRequested);
            ConsoleHelper.SetLogCallback((section, _, text) =>
            {
                string prefix = ConsoleHelper.FormatSectionPrefix(section);
                this.AppendLog(!string.IsNullOrEmpty(prefix) ? prefix + text : text);
            });

            try
            {
                MkvSplitExternalTools.Instance.ResolveBinaries();
                options = this._options;
                for (int i = 0; i < targets.Count; i++)
                {
                    if (this.StopRequested)
                    {
                        this.SetRecordStatus(targets[i], MkvSplitStatus.Stopped, AppText.T("web.split.stopRequested"));
                        break;
                    }

                    // Il try sta dentro il ciclo: un file che esplode non deve abortire il
                    // batch ne' marcare Error gli altri record, come gia' fa la CLI
                    try
                    {
                        record = this.GetRecordAt(targets[i]);
                        if (record == null || record.Skipped)
                        {
                            skippedCount++;
                            continue;
                        }

                        this.ReportProgress(i, targets.Count, Path.GetFileName(record.InputFile));

                        // Un file mai analizzato riceve il suo piano adesso: lo split non ricostruisce mai i segmenti da sé
                        AppliedMontage applied;
                        lock (this.StateLock) { this._applied.TryGetValue(record.InputFile, out applied); }
                        MkvSplitExecutionPlan montage = applied?.Plan;
                        if (montage == null || applied.OptionsRevision != this._optionsRevision)
                        {
                            this.SetRecordStatus(targets[i], MkvSplitStatus.Analyzing, "");
                            montage = this.PrepareMontageForRecord(record, options);
                        }
                        plan = LegacySummary(montage);

                        if (!plan.IsValid)
                        {
                            skippedCount++;
                            this.SetRecordStatus(targets[i], plan.Mode == MkvSplitMode.Manual ? MkvSplitStatus.Undefined : MkvSplitStatus.PlanInvalid, plan.ErrorMessage);
                            this.AppendLog(AppText.F("split.plan.invalid", plan.ErrorMessage));
                            continue;
                        }

                        this.SetRecordStatus(targets[i], MkvSplitStatus.Running, "");
                        lock (this.StateLock)
                        {
                            List<MkvSplitDiagnostic> collisions = this.GlobalCollisions(record, montage);
                            if (collisions.Count > 0) throw new InvalidOperationException(string.Join(" ", collisions.Select(item => item.Message)));
                        }
                        MkvSplitExecutionPlan pending = new MkvSplitExecutionPlan { Document = montage.Document, Analysis = montage.Analysis,
                            Projection = montage.Projection, Tracks = montage.Tracks, Subtitles = montage.Subtitles,
                            // Senza Sovrascrivi gli output gia' prodotti non si rifanno; con Sovrascrivi si rigenerano tutti
                            Outputs = montage.Outputs.Where(output => options.Split.Force || !record.OutputResults.Any(state => state.OutputId == output.OutputId
                                && state.Status == MkvSplitOutputExecutionStatus.Done)).ToList() };
                        MkvSplitMontageExecutionResult execution = pipeline.ExecutePlan(pending, CloneSplitOptionsFor(options), this.IsStopRequested);
                        lock (this.StateLock)
                            foreach (MkvSplitOutputExecutionResult state in execution.Outputs)
                            {
                                record.OutputResults.RemoveAll(item => item.OutputId == state.OutputId);
                                record.OutputResults.Add(state);
                            }
                        exitCode = execution.ExitCode;
                        if (this.StopRequested)
                        {
                            this.UpdateRecord(targets[i], MkvSplitStatus.Stopped, false, AppText.T("web.split.stopRequested"), plan.Segments);
                            break;
                        }
                        if (exitCode == 0)
                        {
                            successCount++;
                            this.UpdateRecord(targets[i], options.Split.DryRun ? MkvSplitStatus.Planned : MkvSplitStatus.Done, !options.Split.DryRun, "", plan.Segments);
                        }
                        else
                        {
                            errorCount++;
                            this.UpdateRecord(targets[i], MkvSplitStatus.Error, false, AppText.T("split.error.generic"), plan.Segments);
                        }
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        this.UpdateRecord(targets[i], MkvSplitStatus.Error, false, ex.Message, null);
                        this.AppendLog(AppText.F("cli.splitError", ex.Message));
                    }
                }

                summary = new OperationSummary(successCount, errorCount, skippedCount, this.StopRequested);
                if (errorCount == 0 && !this.StopRequested)
                {
                    this.AppendLog(AppText.F("web.split.completed", successCount));
                }
                else if (this.StopRequested)
                {
                    this.AppendLog(AppText.F("web.split.stoppedSummary", successCount, errorCount));
                }
                else
                {
                    this.AppendLog(AppText.F("web.split.errorSummary", successCount, errorCount));
                }
            }
            catch (Exception ex)
            {
                summary = new OperationSummary(successCount, errorCount + 1, skippedCount, false);
                this.MarkRecords(MkvSplitStatus.Error, false, ex.Message);
                this.AppendLog(AppText.F("cli.splitError", ex.Message));
            }
            finally
            {
                ConsoleHelper.ClearLogCallback();
                this.SetBusy(false, "");
                this.NotifyRecordsChanged();
                this.OnSplitCompleted?.Invoke(summary);
            }
        }

        /// <summary>
        /// Risolve gli indici bersaglio di un'operazione
        /// </summary>
        /// <param name="indices">Indici richiesti, null per tutti</param>
        /// <returns>Indici validi in ordine crescente</returns>
        private List<int> ResolveTargets(List<int> indices)
        {
            List<int> result = new List<int>();

            lock (this.StateLock)
            {
                if (indices == null)
                {
                    for (int i = 0; i < this._records.Count; i++)
                    {
                        result.Add(i);
                    }
                    return result;
                }

                foreach (int index in indices)
                {
                    if (index >= 0 && index < this._records.Count && !result.Contains(index))
                    {
                        result.Add(index);
                    }
                }
            }

            result.Sort();
            return result;
        }

        /// <summary>
        /// Registra e notifica un'operazione che non può partire
        /// </summary>
        /// <param name="message">Motivo del rifiuto</param>
        private void RejectOperation(string message)
        {
            this.AppendLog(message);
            this.OnOperationFailed?.Invoke(message);
        }

        /// <summary>
        /// Restituisce il record all'indice indicato
        /// </summary>
        /// <param name="index">Indice richiesto</param>
        /// <returns>Record oppure null</returns>
        private MkvSplitRecord GetRecordAt(int index)
        {
            lock (this.StateLock)
            {
                return index >= 0 && index < this._records.Count ? this._records[index] : null;
            }
        }

        /// <summary>
        /// Aggiorna stato ed errore di un record senza toccarne i segmenti
        /// </summary>
        /// <param name="index">Indice del record</param>
        /// <param name="status">Nuovo stato</param>
        /// <param name="errorMessage">Messaggio di errore o stringa vuota</param>
        private void SetRecordStatus(int index, MkvSplitStatus status, string errorMessage)
        {
            lock (this.StateLock)
            {
                if (index < 0 || index >= this._records.Count)
                    return;

                this._records[index].Status = status;
                this._records[index].ErrorMessage = errorMessage != null ? errorMessage : "";
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Clona le opzioni split correnti
        /// </summary>
        /// <param name="options">Opzioni correnti</param>
        /// <returns>Opzioni split del file</returns>
        private static MkvSplitOptions CloneSplitOptionsFor(Options options)
        {
            MkvSplitOptions result = new MkvSplitOptions();
            result.SourcePath = options.Split.SourcePath;
            result.OutputDir = options.Split.OutputDir;
            result.Pattern = options.Split.Pattern;
            result.Ranges = options.Split.Ranges;
            result.SplitAt = options.Split.SplitAt;
            result.TrimStart = options.Split.TrimStart;
            result.TrimEnd = options.Split.TrimEnd;
            result.ChaptersEach = options.Split.ChaptersEach;
            result.ChaptersPerEpisode = options.Split.ChaptersPerEpisode;
            result.Manual = options.Split.Manual;
            result.OutputTemplate = options.Split.OutputTemplate;
            result.StartNumber = options.Split.StartNumber;
            result.Snap = options.Split.Snap;
            result.Force = options.Split.Force;
            result.DryRun = options.Split.DryRun;
            return result;
        }

        /// <summary>
        /// Scansiona source file/cartella
        /// </summary>
        /// <returns>Record creati, ordinati per percorso</returns>
        private List<MkvSplitRecord> ScanSource()
        {
            List<MkvSplitRecord> result = new List<MkvSplitRecord>();
            ToolPathResolverService resolver = new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder);
            string mkvMergePath = resolver.ResolveMkvMergePath(false);
            string source = this._options.Split.SourcePath;
            SearchOption searchOption = this._options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            if (string.IsNullOrEmpty(source))
            {
                throw new InvalidOperationException(AppText.T("web.split.configureSource"));
            }

            if (File.Exists(source))
            {
                result.Add(this.CreateRecord(Path.GetFullPath(source), mkvMergePath));
            }
            else if (Directory.Exists(source))
            {
                for (int i = 0; i < this._options.FileExtensions.Count; i++)
                {
                    foreach (string file in Directory.GetFiles(source, "*." + this._options.FileExtensions[i].TrimStart('.'), searchOption))
                    {
                        result.Add(this.CreateRecord(Path.GetFullPath(file), mkvMergePath));
                    }
                }
                result.Sort((a, b) => string.Compare(a.InputFile, b.InputFile, StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                throw new FileNotFoundException(AppText.F("validation.splitSourceNotFound", source), source);
            }

            return result;
        }

        /// <summary>
        /// Crea record split
        /// </summary>
        /// <param name="file">File sorgente</param>
        /// <param name="mkvMergePath">Percorso di mkvmerge, vuoto se non risolto</param>
        /// <returns>Record con dimensione e info di contenitore già lette</returns>
        private MkvSplitRecord CreateRecord(string file, string mkvMergePath)
        {
            MkvSplitRecord record = new MkvSplitRecord();
            record.InputFile = file;
            record.Status = MkvSplitStatus.Pending;
            record.SourceSize = new FileInfo(file).Length;

            // Contenitore e tracce si leggono già allo scan: il dettaglio non resta vuoto in attesa dell'analisi
            try
            {
                record.SourceInfo = !string.IsNullOrEmpty(mkvMergePath) ? new MkvToolsService(mkvMergePath).GetFileInfo(file) : null;
            }
            catch (Exception)
            {
                record.SourceInfo = null;
            }

            return record;
        }

        /// <summary>
        /// Marca tutti i record
        /// </summary>
        /// <param name="status">Stato da assegnare</param>
        /// <param name="success">Esito da assegnare</param>
        /// <param name="errorMessage">Messaggio di errore da assegnare</param>
        private void MarkRecords(MkvSplitStatus status, bool success, string errorMessage)
        {
            lock (this.StateLock)
            {
                for (int i = 0; i < this._records.Count; i++)
                {
                    this._records[i].Status = status;
                    this._records[i].Success = success;
                    this._records[i].ErrorMessage = errorMessage;
                }
            }
        }

        /// <summary>
        /// Aggiorna un singolo record split
        /// </summary>
        /// <param name="index">Indice del record</param>
        /// <param name="status">Nuovo stato</param>
        /// <param name="success">Esito dell'operazione</param>
        /// <param name="errorMessage">Messaggio di errore o stringa vuota</param>
        /// <param name="segments">Segmenti da assegnare, null per lasciare invariati quelli correnti</param>
        private void UpdateRecord(int index, MkvSplitStatus status, bool success, string errorMessage, List<MkvSplitSegment> segments)
        {
            lock (this.StateLock)
            {
                if (index < 0 || index >= this._records.Count)
                {
                    return;
                }

                this._records[index].Status = status;
                this._records[index].Success = success;
                this._records[index].ErrorMessage = errorMessage != null ? errorMessage : "";
                if (segments != null)
                {
                    this._records[index].Segments = new List<MkvSplitSegment>(segments);
                }
            }

            this.NotifyRecordsChanged();
        }

        /// <summary>
        /// Copia profonda di un valore tramite serializzazione JSON
        /// </summary>
        /// <typeparam name="T">Tipo del valore</typeparam>
        /// <param name="value">Valore da copiare</param>
        /// <returns>Copia indipendente, oppure il default del tipo se il valore è null</returns>
        private static T Copy<T>(T value)
        {
            return value == null ? default : System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(value, s_copyOptions), s_copyOptions);
        }

        /// <summary>
        /// Copia profonda delle opzioni
        /// </summary>
        /// <param name="options">Opzioni da copiare</param>
        /// <returns>Copia indipendente delle opzioni</returns>
        private static Options CloneOptions(Options options)
        {
            return Copy(options);
        }

        /// <summary>
        /// Crea una diagnostica bloccante dell'editor
        /// </summary>
        /// <param name="code">Codice della diagnostica</param>
        /// <param name="message">Messaggio esplicito, null per usare il testo localizzato del codice</param>
        /// <returns>Diagnostica con severità errore</returns>
        private static MkvSplitDiagnostic EditorDiagnostic(string code, string message = null)
        {
            return new MkvSplitDiagnostic { Code = code, Message = message ?? AppText.T("split.montage." + code), Severity = MkvSplitDiagnosticSeverity.Error };
        }

        /// <summary>
        /// Crea un esito di calcolo della regola fallito con una sola diagnostica
        /// </summary>
        /// <param name="code">Codice della diagnostica</param>
        /// <param name="message">Messaggio esplicito, null per usare il testo localizzato del codice</param>
        /// <returns>Esito di calcolo fallito</returns>
        private static SplitRuleDocumentResult RuleFailure(string code, string message = null)
        {
            return new SplitRuleDocumentResult { Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic(code, message) } };
        }

        /// <summary>
        /// Crea un esito di applicazione fallita con una sola diagnostica
        /// </summary>
        /// <param name="status">Stato dell'esito</param>
        /// <param name="code">Codice della diagnostica</param>
        /// <param name="message">Messaggio esplicito, null per usare il testo localizzato del codice</param>
        /// <returns>Esito di applicazione fallita</returns>
        private static SplitApplyResult ApplyFailure(SplitApplyStatus status, string code, string message = null)
        {
            return new SplitApplyResult { Status = status, Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic(code, message) } };
        }

        /// <summary>
        /// Valida e applica un documento candidato, oppure rigenera quello di partenza in caso di reset
        /// </summary>
        /// <param name="request">Richiesta di applicazione</param>
        /// <param name="reset">True per rigenerare il documento invece di usare quello della richiesta</param>
        /// <param name="cancellation">Annullamento dell'applicazione</param>
        /// <returns>Esito dell'applicazione</returns>
        private async Task<SplitApplyResult> ApplyCandidateAsync(SplitApplyRequest request, bool reset, CancellationToken cancellation)
        {
            EditorSession session;
            MkvSplitRecord record;
            MkvSplitOptions options;
            MkvSplitDocument candidate;
            lock (this.StateLock)
            {
                if (request == null || !this._sessions.TryGetValue(request.SessionId, out session)) return ApplyFailure(SplitApplyStatus.Conflict, "conflict");
                request = Copy(request);
                if (this.BusyState) return ApplyFailure(SplitApplyStatus.Busy, "busy");
                record = this._records.Find(item => item.RecordId == session.RecordId);
                this._applied.TryGetValue(session.Source.FullPath, out AppliedMontage applied);
                if (record == null || request.ExpectedOptionsRevision != this._optionsRevision || request.ExpectedAppliedRevision != (applied?.Revision ?? 0)
                    || request.DraftRevision < session.LastDraftRevision) return ApplyFailure(SplitApplyStatus.Conflict, "conflict");
                if (!reset && (request.Document == null || request.Document.Id != session.DocumentId || !session.Source.Matches(request.Document.Source)))
                    return ApplyFailure(SplitApplyStatus.Invalid, "invalidDocument");
                candidate = request.Document?.Clone();
                options = CloneSplitOptionsFor(this._options);
                session.LastDraftRevision = request.DraftRevision;
            }
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (!session.Source.Matches(MkvSplitSourceIdentity.FromFile(record.InputFile))) return ApplyFailure(SplitApplyStatus.Conflict, "sourceChanged");
                MkvSplitDocumentService service = new MkvSplitDocumentService();
                if (reset)
                {
                    if (options.Manual) candidate = service.CreateFullSource(session.Source, session.Analysis);
                    else
                    {
                        MkvSplitPlan legacy = await Task.Run(() => new MkvSplitPlanner().BuildPlan(options, record.InputFile, null), cancellation);
                        if (!legacy.IsValid) return ApplyFailure(SplitApplyStatus.Invalid, "preparationFailed", legacy.ErrorMessage);
                        candidate = service.FromLegacyPlan(legacy, session.Source);
                    }
                    candidate.Id = session.DocumentId;
                }
                MkvSplitExecutionPlan plan = await Task.Run(() => service.BuildExecutionPlan(candidate, session.Analysis, options), cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (!plan.IsValid) return new SplitApplyResult { Status = SplitApplyStatus.Invalid, Diagnostics = plan.Projection.Diagnostics };
                SplitApplyResult response;
                lock (this.StateLock)
                {
                    if (this.BusyState) return ApplyFailure(SplitApplyStatus.Busy, "busy");
                    this._applied.TryGetValue(record.InputFile, out AppliedMontage previous);
                    if (!this._sessions.TryGetValue(request.SessionId, out EditorSession current) || !ReferenceEquals(current, session)
                        || current.LastDraftRevision != request.DraftRevision || this._optionsRevision != request.ExpectedOptionsRevision
                        || (previous?.Revision ?? 0) != request.ExpectedAppliedRevision || !this._records.Any(item => item.RecordId == session.RecordId)
                        || !session.Source.Matches(MkvSplitSourceIdentity.FromFile(record.InputFile)))
                        return ApplyFailure(SplitApplyStatus.Conflict, "conflict");
                    List<MkvSplitDiagnostic> collisions = this.GlobalCollisions(record, plan);
                    if (collisions.Count > 0) return new SplitApplyResult { Status = SplitApplyStatus.Invalid, Diagnostics = collisions };
                    long revision = (previous?.Revision ?? 0) + 1;
                    bool customized = !reset && (session.RuleDocument == null || JsonSerializer.Serialize(candidate) != session.RuleDocument);
                    this.CommitMontage(record, candidate, plan, revision, customized);
                    response = new SplitApplyResult { Status = SplitApplyStatus.Applied, AppliedRevision = revision, OptionsRevision = this._optionsRevision };
                }
                this.NotifyRecordsChanged();
                return response;
            }
            catch (OperationCanceledException) { return new SplitApplyResult { Status = SplitApplyStatus.Cancelled }; }
            catch (Exception exception) { return ApplyFailure(SplitApplyStatus.Invalid, "preparationFailed", exception.Message); }
        }

        /// <summary>
        /// Rileva collisioni di nome fra gli output del piano e i file già riservati dagli altri record
        /// </summary>
        /// <param name="owner">Record proprietario del piano</param>
        /// <param name="plan">Piano da verificare</param>
        /// <returns>Diagnostiche di collisione, vuote se non ce ne sono</returns>
        private List<MkvSplitDiagnostic> GlobalCollisions(MkvSplitRecord owner, MkvSplitExecutionPlan plan)
        {
            HashSet<string> reserved = new HashSet<string>(this._records.Select(record => Path.GetFullPath(record.InputFile)), StringComparer.OrdinalIgnoreCase);
            foreach (MkvSplitRecord record in this._records.Where(record => record.RecordId != owner.RecordId))
            {
                if (this._applied.TryGetValue(record.InputFile, out AppliedMontage applied))
                    foreach (MkvSplitExecutionOutput output in applied.Plan.Outputs) reserved.Add(output.Projection.FullPath);
                else if (record.Plan != null)
                    foreach (MkvSplitSegment segment in record.Plan.Segments) reserved.Add(Path.GetFullPath(Path.Combine(record.Plan.OutputDir, segment.File)));
            }
            List<MkvSplitDiagnostic> errors = new List<MkvSplitDiagnostic>();
            foreach (MkvSplitExecutionOutput output in plan.Outputs)
                if (!reserved.Add(output.Projection.FullPath)) errors.Add(new MkvSplitDiagnostic { Code = "nameCollision", OutputId = output.OutputId, Severity = MkvSplitDiagnosticSeverity.Error,
                    Message = AppText.F("split.montage.nameCollision", output.Projection.FullPath) });
            return errors;
        }

        /// <summary>
        /// Registra il montaggio applicato e aggiorna il record, conservando lo stato degli output invariati
        /// </summary>
        /// <param name="record">Record da aggiornare</param>
        /// <param name="document">Documento applicato</param>
        /// <param name="plan">Piano di esecuzione del documento</param>
        /// <param name="revision">Nuova revisione applicata</param>
        /// <param name="customized">True se il montaggio è stato personalizzato nell'editor</param>
        private void CommitMontage(MkvSplitRecord record, MkvSplitDocument document, MkvSplitExecutionPlan plan, long revision, bool customized)
        {
            this._applied.TryGetValue(record.InputFile, out AppliedMontage previous);
            List<MkvSplitOutputExecutionResult> retained = new List<MkvSplitOutputExecutionResult>();
            foreach (MkvSplitExecutionOutput output in plan.Outputs)
            {
                MkvSplitExecutionOutput before = previous?.Plan.Outputs.Find(item => item.OutputId == output.OutputId);
                MkvSplitOutputExecutionResult state = record.OutputResults.Find(item => item.OutputId == output.OutputId);
                if (before != null && state != null && before.Projection.FullPath == output.Projection.FullPath
                    && before.Clips.Select(item => (item.Segment.StartFrame, item.Segment.FrameCount)).SequenceEqual(output.Clips.Select(item => (item.Segment.StartFrame, item.Segment.FrameCount))))
                    retained.Add(state);
                else retained.Add(new MkvSplitOutputExecutionResult { OutputId = output.OutputId });
            }
            this._applied[record.InputFile] = new AppliedMontage { Document = document.Clone(), Plan = plan, Revision = revision, OptionsRevision = this._optionsRevision };
            record.OutputResults = retained;
            record.MontageProjection = Copy(plan.Projection);
            record.Plan = LegacySummary(plan);
            record.Segments = record.Plan.Segments;
            record.IsOverride = customized;
            record.Status = plan.IsValid ? MkvSplitStatus.Planned : MkvSplitStatus.PlanInvalid;
            record.Success = retained.Count > 0 && retained.All(item => item.Status == MkvSplitOutputExecutionStatus.Done || item.Status == MkvSplitOutputExecutionStatus.ExistsSkipped);
            if (record.Success) record.Status = MkvSplitStatus.Done;
            record.ErrorMessage = plan.IsValid ? "" : string.Join(" ", plan.Projection.Diagnostics.Select(item => item.Message));
        }

        /// <summary>
        /// Riassume un piano di esecuzione nel formato di piano legacy usato da griglia e CLI
        /// </summary>
        /// <param name="plan">Piano di esecuzione</param>
        /// <returns>Piano legacy equivalente</returns>
        private static MkvSplitPlan LegacySummary(MkvSplitExecutionPlan plan)
        {
            MkvSplitPlan legacy = new MkvSplitPlan { InputFile = plan.Document.Source.FullPath, Mode = plan.Document.OriginMode,
                Duration = plan.Analysis.Duration, SourcePts = plan.Analysis.SourcePts,
                FrameCount = plan.Analysis.SourcePts.Length, Chapters = plan.Analysis.Chapters, FrameRateMode = plan.Analysis.FrameRateMode,
                VideoParams = plan.Analysis.VideoParams, IsValid = plan.IsValid,
                ErrorMessage = string.Join(" ", plan.Projection.Diagnostics.Select(item => item.Message)) };
            foreach (MkvSplitOutputProjection output in plan.Projection.Outputs)
                legacy.Segments.Add(new MkvSplitSegment { Num = legacy.Segments.Count + 1, Episode = legacy.Segments.Count + 1,
                    File = output.FileName, StartFrame = output.Clips.FirstOrDefault()?.StartFrame ?? 0, FrameCount = output.FrameCount,
                    StartTs = 0, EndTs = output.DurationSeconds, Chapters = output.Chapters, OutputState = output.OutputState });
            legacy.OutputDir = plan.Outputs.Count > 0 ? Path.GetDirectoryName(plan.Outputs[0].Projection.FullPath) : "";
            legacy.DiscardedFrames = legacy.FrameCount - plan.Projection.CoveredSourceFrames;
            legacy.Coverage = legacy.DiscardedFrames == 0 ? MkvSplitCoverage.Partition : MkvSplitCoverage.Extract;
            return legacy;
        }

        /// <summary>
        /// Prepara e applica il montaggio di un record per analisi e split, riusando quello già applicato se presente
        /// </summary>
        /// <param name="record">Record da preparare</param>
        /// <param name="options">Opzioni correnti</param>
        /// <returns>Piano di esecuzione del record</returns>
        private MkvSplitExecutionPlan PrepareMontageForRecord(MkvSplitRecord record, Options options)
        {
            MkvSplitDocumentService service = new MkvSplitDocumentService();
            MkvSplitOptions split = CloneSplitOptionsFor(options);
            MkvSplitSourceIdentity source = MkvSplitSourceIdentity.FromFile(record.InputFile);
            MkvSplitAnalysis analysis = MkvSplitAnalysisCache.Instance.GetOrBuild(record.InputFile);
            MkvSplitDocument document;
            long revision;
            lock (this.StateLock)
            {
                this._applied.TryGetValue(record.InputFile, out AppliedMontage previous);
                if (previous != null && !source.Matches(previous.Document.Source))
                    throw new InvalidOperationException(AppText.T("split.montage.sourceChanged"));
                document = previous?.Document.Clone();
                revision = previous?.Revision ?? 0;
            }
            if (document == null)
            {
                MkvSplitPlan legacy = split.Manual ? null : new MkvSplitPlanner().BuildPlan(split, record.InputFile, null);
                if (legacy != null && !legacy.IsValid) throw new InvalidOperationException(legacy.ErrorMessage);
                document = split.Manual ? service.CreateFullSource(source, analysis) : service.FromLegacyPlan(legacy, source);
            }
            MkvSplitExecutionPlan plan = service.BuildExecutionPlan(document, analysis, split);
            lock (this.StateLock)
            {
                if (!source.Matches(MkvSplitSourceIdentity.FromFile(record.InputFile))) throw new InvalidOperationException(AppText.T("split.montage.sourceChanged"));
                plan.Projection.Diagnostics.AddRange(this.GlobalCollisions(record, plan));
                if (plan.IsValid) this.CommitMontage(record, document, plan, revision + 1, record.IsOverride);
                else
                {
                    record.MontageProjection = Copy(plan.Projection);
                    record.Plan = LegacySummary(plan);
                    record.Segments = record.Plan.Segments;
                    record.Status = MkvSplitStatus.PlanInvalid;
                    record.ErrorMessage = record.Plan.ErrorMessage;
                }
            }
            return plan;
        }

        #endregion

        #region Tipi annidati

        /// <summary>
        /// Riassunto dell'esito di un'operazione, trasportato dagli eventi di fine operazione
        /// </summary>
        public class OperationSummary
        {
            #region Costruttore

            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="succeeded">File conclusi con successo</param>
            /// <param name="failed">File falliti</param>
            /// <param name="skipped">File saltati, oppure con avvisi nel caso dell'analisi</param>
            /// <param name="stopped">True se l'operazione è stata interrotta</param>
            public OperationSummary(int succeeded, int failed, int skipped, bool stopped)
            {
                this.Succeeded = succeeded;
                this.Failed = failed;
                this.Skipped = skipped;
                this.Stopped = stopped;
            }

            #endregion

            #region Proprietà

            /// <summary>
            /// File conclusi con successo
            /// </summary>
            public int Succeeded { get; private set; }

            /// <summary>
            /// File falliti
            /// </summary>
            public int Failed { get; private set; }

            /// <summary>
            /// File saltati, oppure con avvisi nel caso dell'analisi
            /// </summary>
            public int Skipped { get; private set; }

            /// <summary>
            /// True se l'operazione è stata interrotta
            /// </summary>
            public bool Stopped { get; private set; }

            #endregion
        }

        /// <summary>
        /// Montaggio applicato a un file, con il piano e le revisioni da cui deriva
        /// </summary>
        private class AppliedMontage
        {
            #region Variabili di classe

            /// <summary>
            /// Documento applicato
            /// </summary>
            public MkvSplitDocument Document;

            /// <summary>
            /// Piano di esecuzione del documento
            /// </summary>
            public MkvSplitExecutionPlan Plan;

            /// <summary>
            /// Revisione applicata
            /// </summary>
            public long Revision;

            /// <summary>
            /// Revisione delle opzioni al momento dell'applicazione
            /// </summary>
            public long OptionsRevision;

            #endregion
        }

        /// <summary>
        /// Stato di una sessione editor aperta
        /// </summary>
        private class EditorSession
        {
            #region Variabili di classe

            /// <summary>
            /// Identificativo del record aperto
            /// </summary>
            public Guid RecordId;

            /// <summary>
            /// Identità del sorgente al momento dell'apertura
            /// </summary>
            public MkvSplitSourceIdentity Source;

            /// <summary>
            /// Identificativo del documento in modifica
            /// </summary>
            public Guid DocumentId;

            /// <summary>
            /// Analisi del sorgente
            /// </summary>
            public MkvSplitAnalysis Analysis;

            /// <summary>
            /// Informazioni di contenitore del sorgente
            /// </summary>
            public MkvFileInfo SourceInfo;

            /// <summary>
            /// Ultima revisione di draft ricevuta, -1 se nessuna
            /// </summary>
            public long LastDraftRevision = -1;

            /// <summary>
            /// Documento della regola consegnato all'editor serializzato, null se mai richiesto
            /// </summary>
            public string RuleDocument;

            #endregion
        }

        /// <summary>
        /// Risolutore media vincolato al sorgente di una sessione editor
        /// </summary>
        private class SessionMediaResolver : IMediaSourceResolver
        {
            #region Variabili di classe

            /// <summary>
            /// Sorgente esposta dal risolutore
            /// </summary>
            private readonly MediaSource _source;

            #endregion

            #region Costruttore

            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="source">Sorgente da esporre</param>
            public SessionMediaResolver(MediaSource source)
            {
                this._source = source;
            }

            #endregion

            #region Metodi pubblici

            /// <summary>
            /// Indica se il lato richiesto è esposto
            /// </summary>
            /// <param name="side">Nome del lato</param>
            /// <returns>True solo per input</returns>
            public bool SupportsSide(string side)
            {
                return string.Equals(side, "input", StringComparison.OrdinalIgnoreCase);
            }

            /// <summary>
            /// Risolve la sorgente della sessione
            /// </summary>
            /// <param name="recordIndex">Indice del record, ignorato</param>
            /// <param name="side">Nome del lato</param>
            /// <returns>Sorgente della sessione, null se il lato non è esposto</returns>
            public MediaSource ResolveMediaSource(int recordIndex, string side)
            {
                return this.SupportsSide(side) ? this._source : null;
            }

            #endregion
        }

        #endregion
    }
}
