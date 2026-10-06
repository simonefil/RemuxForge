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

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Orchestratore WebUI per modalità split
    /// </summary>
    public partial class SplitOrchestrator : MediaOrchestratorBase, IMediaSourceResolver
    {
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

            /// <summary>File conclusi con successo</summary>
            public int Succeeded { get; private set; }

            /// <summary>File falliti</summary>
            public int Failed { get; private set; }

            /// <summary>File saltati, oppure con avvisi nel caso dell'analisi</summary>
            public int Skipped { get; private set; }

            /// <summary>True se l'operazione è stata interrotta</summary>
            public bool Stopped { get; private set; }

            #endregion
        }

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

        private long _optionsRevision;
        private readonly Dictionary<string, AppliedMontage> _applied = new Dictionary<string, AppliedMontage>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Guid, EditorSession> _sessions = new Dictionary<Guid, EditorSession>();
        private static readonly System.Text.Json.JsonSerializerOptions s_copyOptions = new System.Text.Json.JsonSerializerOptions {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

        private class AppliedMontage
        {
            public MkvSplitDocument Document;
            public MkvSplitExecutionPlan Plan;
            public long Revision;
            public long OptionsRevision;
        }

        private class EditorSession
        {
            public Guid RecordId;
            public MkvSplitSourceIdentity Source;
            public Guid DocumentId;
            public MkvSplitAnalysis Analysis;
            public MkvFileInfo SourceInfo;
            public long LastDraftRevision = -1;
        }

        private static T Copy<T>(T value)
        {
            return value == null ? default : System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(value, s_copyOptions), s_copyOptions);
        }

        private static Options CloneOptions(Options options) { return Copy(options); }

        private static MkvSplitDiagnostic EditorDiagnostic(string code, string message = null)
        {
            return new MkvSplitDiagnostic { Code = code, Message = message ?? AppText.T("split.montage." + code), Severity = MkvSplitDiagnosticSeverity.Error };
        }

        /// <summary>Prepara il draft senza avviare export; la sessione resta legata all'identità del media.</summary>
        public async Task<SplitEditorOpenResult> OpenEditorAsync(int recordIndex, CancellationToken cancellation)
        {
            MkvSplitRecord record;
            Options options;
            long revision;
            lock (this.StateLock)
            {
                if (this.BusyState) return new SplitEditorOpenResult { Status = SplitApplyStatus.Busy, Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic("busy") } };
                record = this.GetRecordAt(recordIndex);
                if (record == null) return new SplitEditorOpenResult { Status = SplitApplyStatus.Conflict, Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic("conflict") } };
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
                MkvSplitOptions split = CloneSplitOptionsFor(options, record.InputFile);
                MkvSplitDocument document;
                long appliedRevision;
                lock (this.StateLock)
                {
                    if (revision != this._optionsRevision || !source.Matches(MkvSplitSourceIdentity.FromFile(record.InputFile))
                        || !this._records.Any(item => item.RecordId == record.RecordId))
                        return new SplitEditorOpenResult { Status = SplitApplyStatus.Conflict, Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic("conflict") } };
                    if (this._applied.TryGetValue(record.InputFile, out AppliedMontage applied) && applied.Document.Source.Matches(source))
                    {
                        document = applied.Document.Clone();
                        appliedRevision = applied.Revision;
                    }
                    else { document = null; appliedRevision = 0; }
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
                        return new SplitEditorOpenResult { Status = SplitApplyStatus.Conflict, Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic("conflict") } };
                    record.SourceInfo = Copy(sourceInfo);
                    this._sessions.Add(sessionId, new EditorSession { RecordId = record.RecordId, Source = source.Clone(), DocumentId = document.Id,
                        Analysis = analysis, SourceInfo = sourceInfo });
                }
                return new SplitEditorOpenResult { Status = SplitApplyStatus.Applied, Snapshot = new SplitEditorSnapshot {
                    SessionId = sessionId, RecordId = record.RecordId, SourceIdentity = source.Clone(),
                    ExpectedAppliedRevision = appliedRevision, ExpectedOptionsRevision = revision, Document = document,
                    Analysis = Copy(analysis), SourceInfo = Copy(sourceInfo), Options = Copy(split) } };
            }
            catch (OperationCanceledException) { return new SplitEditorOpenResult { Status = SplitApplyStatus.Cancelled }; }
            catch (Exception exception) { return new SplitEditorOpenResult { Status = SplitApplyStatus.Invalid, Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic("preparationFailed", exception.Message) } }; }
            finally { lock (this.StateLock) { this.BusyState = false; } this.NotifyProgressChanged(); }
        }

        public void CloseEditor(Guid sessionId)
        {
            lock (this.StateLock) { this._sessions.Remove(sessionId); }
        }

        public Task<SplitApplyResult> ApplyDraftAsync(SplitApplyRequest request, CancellationToken cancellation)
        {
            return this.ApplyCandidateAsync(request, false, cancellation);
        }

        /// <summary>Reset esplicito: in manuale ripristina il sorgente intero, in batch rigenera la regola.</summary>
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

        private static SplitApplyResult ApplyFailure(SplitApplyStatus status, string code, string message = null)
        {
            return new SplitApplyResult { Status = status, Diagnostics = new List<MkvSplitDiagnostic> { EditorDiagnostic(code, message) } };
        }

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
                options = CloneSplitOptionsFor(this._options, record.InputFile);
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
                if (!plan.IsValid) return new SplitApplyResult { Status = SplitApplyStatus.Invalid, Diagnostics = plan.Projection.Diagnostics, Plan = Copy(plan) };
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
                    this.CommitMontage(record, candidate, plan, revision, !reset);
                    response = new SplitApplyResult { Status = SplitApplyStatus.Applied, AppliedRevision = revision, OptionsRevision = this._optionsRevision, Plan = Copy(plan) };
                }
                this.NotifyRecordsChanged();
                return response;
            }
            catch (OperationCanceledException) { return new SplitApplyResult { Status = SplitApplyStatus.Cancelled }; }
            catch (Exception exception) { return ApplyFailure(SplitApplyStatus.Invalid, "preparationFailed", exception.Message); }
        }

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
                else retained.Add(new MkvSplitOutputExecutionResult { OutputId = output.OutputId, FullPath = output.Projection.FullPath });
            }
            this._applied[record.InputFile] = new AppliedMontage { Document = document.Clone(), Plan = plan, Revision = revision, OptionsRevision = this._optionsRevision };
            record.OutputResults = retained;
            record.MontageProjection = Copy(plan.Projection);
            record.Plan = LegacySummary(plan, customized);
            record.Segments = record.Plan.Segments;
            record.IsOverride = customized;
            record.Status = plan.IsValid ? MkvSplitStatus.Planned : MkvSplitStatus.PlanInvalid;
            record.Success = retained.Count > 0 && retained.All(item => item.Status == MkvSplitOutputExecutionStatus.Done || item.Status == MkvSplitOutputExecutionStatus.ExistsSkipped);
            if (record.Success) record.Status = MkvSplitStatus.Done;
            record.ErrorMessage = plan.IsValid ? "" : string.Join(" ", plan.Projection.Diagnostics.Select(item => item.Message));
        }

        private static MkvSplitPlan LegacySummary(MkvSplitExecutionPlan plan, bool customized)
        {
            MkvSplitPlan legacy = new MkvSplitPlan { InputFile = plan.Document.Source.FullPath, Mode = plan.Document.OriginMode,
                IsOverride = customized, Duration = plan.Analysis.Duration, SourcePts = plan.Analysis.SourcePts,
                FrameCount = plan.Analysis.SourcePts.Length, Chapters = plan.Analysis.Chapters, FrameRateMode = plan.Analysis.FrameRateMode,
                VideoParams = plan.Analysis.VideoParams, IsValid = plan.IsValid,
                ErrorMessage = string.Join(" ", plan.Projection.Diagnostics.Select(item => item.Message)),
                KeyframeIndexes = plan.Analysis.KeyFlags.Select((item, index) => (item, index)).Where(pair => pair.item.Key).Select(pair => pair.index).ToArray() };
            foreach (MkvSplitOutputProjection output in plan.Projection.Outputs)
                legacy.Segments.Add(new MkvSplitSegment { Num = legacy.Segments.Count + 1, Episode = legacy.Segments.Count + 1,
                    File = output.FileName, StartFrame = output.Clips.FirstOrDefault()?.StartFrame ?? 0, FrameCount = output.FrameCount,
                    StartTs = 0, EndTs = output.DurationSeconds, Chapters = output.Chapters, OutputState = output.OutputState });
            legacy.OutputDir = plan.Outputs.Count > 0 ? Path.GetDirectoryName(plan.Outputs[0].Projection.FullPath) : "";
            legacy.DiscardedFrames = legacy.FrameCount - plan.Projection.CoveredSourceFrames;
            return legacy;
        }

        private MkvSplitExecutionPlan PrepareMontageForRecord(MkvSplitRecord record, Options options)
        {
            MkvSplitDocumentService service = new MkvSplitDocumentService();
            MkvSplitOptions split = CloneSplitOptionsFor(options, record.InputFile);
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
                    record.Plan = LegacySummary(plan, record.IsOverride);
                    record.Segments = record.Plan.Segments;
                    record.Status = MkvSplitStatus.PlanInvalid;
                    record.ErrorMessage = record.Plan.ErrorMessage;
                }
            }
            return plan;
        }

        /// <summary>Risolutore immutabile per una richiesta preview/audio vincolata alla sessione.</summary>
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

        private class SessionMediaResolver : IMediaSourceResolver
        {
            private readonly MediaSource _source;
            public SessionMediaResolver(MediaSource source) { this._source = source; }
            public bool SupportsSide(string side) { return string.Equals(side, "input", StringComparison.OrdinalIgnoreCase); }
            public MediaSource ResolveMediaSource(int recordIndex, string side) { return this.SupportsSide(side) ? this._source : null; }
        }

        #endregion

        #region Eventi

        /// <summary>Evento fine analisi, con il riassunto dell'esito</summary>
        public event Action<OperationSummary> OnAnalysisCompleted;

        /// <summary>Evento fine split, con il riassunto dell'esito</summary>
        public event Action<OperationSummary> OnSplitCompleted;

        /// <summary>Evento emesso quando un'operazione non può partire</summary>
        public event Action<string> OnOperationFailed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public SplitOrchestrator() : base(AppText.T("web.split.ready"), false)
        {
            this._options = new Options();
            this._options.Mode = Options.MODE_SPLIT;
            this._records = new List<MkvSplitRecord>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Applica opzioni split
        /// </summary>
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
                if (this.BusyState) { errorMessage = AppText.T("split.montage.busy"); return false; }
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
            lock (this.StateLock) { if (this.BusyState) return; this.BusyState = true; }
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
            lock (this.StateLock) { if (this.BusyState) return; this.BusyState = true; }
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
        /// Richiede stop cooperativo
        /// </summary>
        public void Stop()
        {
            this.RequestStop(AppText.T("web.split.stopRequested"));
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
        /// Restituisce copia record
        /// </summary>
        /// <summary>
        /// Proietta il documento personalizzato nei segmenti legacy, solo per output a clip singola.
        /// Per un montaggio multiclip usare OpenEditorAsync: non è rappresentabile senza perderne la semantica.
        /// </summary>
        /// <param name="index">Indice del record</param>
        /// <returns>Segmenti dell'editor oppure null</returns>
        public List<MkvSplitOverrideSegment> GetOverride(int index)
        {
            lock (this.StateLock)
            {
                MkvSplitRecord record = this.GetRecordAt(index);
                if (record == null || !record.IsOverride || !this._applied.TryGetValue(record.InputFile, out AppliedMontage applied)) return null;
                if (applied.Document.Outputs.Any(output => output.Clips.Count != 1))
                    throw new NotSupportedException(AppText.T("split.montage.legacyMontageUnavailable"));
                return applied.Document.Outputs.Select(output => new MkvSplitOverrideSegment {
                    StartFrame = output.Clips[0].StartFrame,
                    FrameCount = output.Clips[0].EndFrameExclusive - output.Clips[0].StartFrame, Excluded = false }).ToList();
            }
        }

        /// <summary>
        /// Sostituisce i segmenti di un record con quelli costruiti nell'editor e ne ricostruisce il piano
        /// </summary>
        /// <param name="index">Indice del record</param>
        /// <param name="segments">Segmenti dell'editor</param>
        public void SetOverride(int index, List<MkvSplitOverrideSegment> segments)
        {
            if (segments == null) return;
            SplitEditorOpenResult opened = this.OpenEditorAsync(index, CancellationToken.None).GetAwaiter().GetResult();
            if (opened.Snapshot == null) return;
            try
            {
                MkvSplitDocument document = new MkvSplitDocumentService().FromLegacyOverride(
                    this.GetRecordAt(index).Plan ?? new MkvSplitPlan { Mode = opened.Snapshot.Document.OriginMode }, segments, opened.Snapshot.SourceIdentity);
                document.Id = opened.Snapshot.Document.Id;
                SplitApplyResult applied = this.ApplyDraftAsync(new SplitApplyRequest { SessionId = opened.Snapshot.SessionId,
                    ExpectedAppliedRevision = opened.Snapshot.ExpectedAppliedRevision, ExpectedOptionsRevision = opened.Snapshot.ExpectedOptionsRevision,
                    DraftRevision = 1, Document = document }, CancellationToken.None).GetAwaiter().GetResult();
                if (applied.Status != SplitApplyStatus.Applied) this.RejectOperation(string.Join(" ", applied.Diagnostics.Select(item => item.Message)));
            }
            finally { this.CloseEditor(opened.Snapshot.SessionId); }
        }

        /// <summary>
        /// Riporta un record sotto la configurazione globale, scartando i segmenti dell'editor
        /// </summary>
        /// <param name="index">Indice del record</param>
        public void ClearOverride(int index)
        {
            SplitEditorOpenResult opened = this.OpenEditorAsync(index, CancellationToken.None).GetAwaiter().GetResult();
            if (opened.Snapshot == null) return;
            try { this.ReapplyRuleAsync(opened.Snapshot.SessionId, CancellationToken.None).GetAwaiter().GetResult(); }
            finally { this.CloseEditor(opened.Snapshot.SessionId); }
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

        public List<MkvSplitRecord> GetRecords()
        {
            lock (this.StateLock)
            {
                return Copy(this._records);
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Ricostruisce il piano di un record dopo un cambio di override
        /// </summary>
        /// <param name="index">Indice del record</param>
        /// <param name="segments">Segmenti dell'editor, null per tornare alla configurazione globale</param>
        private void RebuildPlan(int index, List<MkvSplitOverrideSegment> segments)
        {
            MkvSplitRecord record = this.GetRecordAt(index);
            MkvSplitPlanner planner = new MkvSplitPlanner();
            MkvSplitPlan plan;

            if (record == null) { return; }
            try
            {
                plan = planner.BuildPlan(CloneSplitOptionsFor(this._options, record.InputFile), record.InputFile, null, segments);
            }
            catch (Exception ex)
            {
                this.SetRecordStatus(index, MkvSplitStatus.Error, ex.Message);
                return;
            }

            lock (this.StateLock)
            {
                record.Plan = plan;
                record.Segments = plan.Segments;
                record.IsOverride = segments != null;
                record.Status = plan.IsValid ? MkvSplitStatus.Planned : (plan.Mode == MkvSplitMode.Manual ? MkvSplitStatus.Undefined : MkvSplitStatus.PlanInvalid);
                record.ErrorMessage = plan.IsValid ? "" : plan.ErrorMessage;
            }
            this.NotifyRecordsChanged();
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
                        plan = LegacySummary(montage, record.IsOverride);
                        planner.PrintPlan(plan);

                        lock (this.StateLock)
                        {
                            record.Plan = plan;
                            record.Segments = plan.Segments;
                            record.Status = plan.IsValid ? MkvSplitStatus.Planned : (plan.Mode == MkvSplitMode.Manual ? MkvSplitStatus.Undefined : MkvSplitStatus.PlanInvalid);
                            record.ErrorMessage = plan.IsValid ? "" : plan.ErrorMessage;
                        }

                        if (plan.IsValid) { plannedCount++; } else { invalidCount++; }
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
                        plan = LegacySummary(montage, record.IsOverride);

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
                            Outputs = montage.Outputs.Where(output => !record.OutputResults.Any(state => state.OutputId == output.OutputId
                                && state.Status == MkvSplitOutputExecutionStatus.Done)).ToList() };
                        MkvSplitMontageExecutionResult execution = pipeline.ExecutePlan(pending, CloneSplitOptionsFor(options, record.InputFile), this.IsStopRequested);
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
        /// Clona le opzioni split per un singolo file
        /// </summary>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="inputFile">File da elaborare</param>
        /// <returns>Opzioni split del file</returns>
        private static MkvSplitOptions CloneSplitOptionsFor(Options options, string inputFile)
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
            result.InputFile = inputFile;
            return result;
        }

        /// <summary>
        /// Scansiona source file/cartella
        /// </summary>
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

        #endregion
    }
}
