using RemuxForge.Core.Ai;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Fase del job con chiamate al modello per file: lo scheduler coi file in parallelo fino al tetto e la consegna di un gruppo di
    /// battute con la validazione, i ritentativi sulle sole respinte e il dimezzamento dopo una risposta troncata. Il limite d'uso e
    /// gli errori fermano gli altri file fra un'unità salvata e l'altra; Stop scarta l'unità in corso
    /// </summary>
    public abstract class TranslationPhaseRunner
    {
        #region Costanti

        /// <summary>
        /// Ritentativi di una battuta respinta prima di lasciarla com'era
        /// </summary>
        public const int MAX_RETRIES = 2;

        /// <summary>
        /// Motivo dello stato da rivedere per una risposta troncata su battute che non si dividono più, o incompleta per un motivo
        /// che il servizio non spiega
        /// </summary>
        public const string REASON_INCOMPLETE = "incomplete";

        /// <summary>
        /// Motivo dello stato da rivedere per le battute di una risposta fermata dal filtro dei contenuti del servizio
        /// </summary>
        public const string REASON_CONTENT_FILTER = "content_filter";

        /// <summary>
        /// Motivo di incompletezza del servizio per il tetto dei token in uscita: l'unico che si risolve con consegne più piccole
        /// </summary>
        protected const string INCOMPLETE_MAX_OUTPUT_TOKENS = "max_output_tokens";

        /// <summary>
        /// Motivo di incompletezza del servizio per il filtro dei contenuti
        /// </summary>
        protected const string INCOMPLETE_CONTENT_FILTER = "content_filter";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Job su disco
        /// </summary>
        protected readonly TranslationJobStore _store;

        /// <summary>
        /// Testata del job
        /// </summary>
        protected readonly TranslationJob _job;

        /// <summary>
        /// File del job con documento e nome
        /// </summary>
        protected readonly List<TranslationRunFile> _files;

        /// <summary>
        /// Lock dello stato condiviso con chi legge job, file e registro
        /// </summary>
        protected readonly object _lock;

        /// <summary>
        /// Registro delle chiamate del job, condiviso con la pagina
        /// </summary>
        private readonly List<TranslationCallRecord> _calls;

        /// <summary>
        /// Campo serie/contesto del progetto, in testa al prefisso di ogni file
        /// </summary>
        private readonly string _context;

        /// <summary>
        /// Instructions di traduzione, ritentativo e blocco ridotto, uguali per tutto il job
        /// </summary>
        private readonly string _translationInstructions;

        /// <summary>
        /// Prima fermata di un file per limite d'uso o errore: gli altri file non avviano unità nuove
        /// </summary>
        private TranslationStop _pause;

        #endregion

        #region Eventi

        /// <summary>
        /// Riga del log comune
        /// </summary>
        public event Action<string> Log;

        /// <summary>
        /// Stato di job, file o registro cambiato
        /// </summary>
        public event Action Changed;

        /// <summary>
        /// Avvisa che il lavoro va avanti: unità fatte e totali, file, unità del file e unità del file in tutto
        /// </summary>
        public event Action<int, int, string, int, int> Progress;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="store">Job su disco</param>
        /// <param name="job">Testata del job</param>
        /// <param name="files">File del job, nell'ordine del progetto</param>
        /// <param name="calls">Registro delle chiamate del job</param>
        /// <param name="stateLock">Lock dello stato condiviso</param>
        /// <param name="context">Campo serie/contesto del progetto, letto all'avvio della fase</param>
        protected TranslationPhaseRunner(TranslationJobStore store, TranslationJob job, List<TranslationRunFile> files, List<TranslationCallRecord> calls, object stateLock, string context)
        {
            this._store = store;
            this._job = job;
            this._files = files;
            this._calls = calls;
            this._lock = stateLock;
            this._context = context;
            this._translationInstructions = TranslationRequestBuilder.CreateTranslationInstructions(job.Glossary.SourceLanguage, job.Glossary.TargetLanguage);
            this._pause = null;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Lavora i file che hanno ancora lavoro: al più maxParallel insieme, ognuno a unità in sequenza
        /// </summary>
        /// <param name="provider">Provider condiviso da tutti i file, con la pausa comune dei 429</param>
        /// <param name="maxParallel">File in parallelo</param>
        /// <param name="cancellation">Annullamento di Stop</param>
        /// <returns>Fermata, null se nessun file si è fermato</returns>
        public virtual async Task<TranslationStop> RunAsync(ITranslationModelProvider provider, int maxParallel, CancellationToken cancellation)
        {
            Queue<TranslationRunFile> queue = new Queue<TranslationRunFile>();
            foreach (TranslationRunFile file in this._files)
            {
                if (this.HasWork(file))
                    queue.Enqueue(file);
            }

            int workers = Math.Max(TranslationProjectOptions.MIN_PARALLEL_FILES, Math.Min(Math.Min(maxParallel, TranslationProjectOptions.MAX_PARALLEL_FILES), queue.Count));
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < workers; i++)
            {
                // Lavoratore dello scheduler: prende il file seguente finché non c'è una fermata
                tasks.Add(Task.Run(async () =>
                {
                    while (true)
                    {
                        TranslationRunFile file;
                        lock (this._lock)
                        {
                            if (this._pause != null || cancellation.IsCancellationRequested || queue.Count == 0)
                                return;
                            file = queue.Dequeue();
                        }

                        try
                        {
                            await this.ProcessFileAsync(file, provider, cancellation);
                        }
                        catch (Exception ex)
                        {
                            // L'unità in corso si scarta: il file resta com'era all'ultima unità salvata
                            if (!(ex is AiServiceException || ex is OperationCanceledException))
                                ConsoleHelper.Write(LogSection.General, LogLevel.Error, ex.ToString());
                            bool stopped = cancellation.IsCancellationRequested;
                            TranslationStop stop = TranslationStop.Create(ex, stopped);
                            lock (this._lock)
                            {
                                if (this._pause == null && !stopped)
                                    this._pause = stop;
                            }

                            if (!stopped)
                                this.RaiseLog(AppText.F("toolbox.subtitleTranslation.run.filePaused", file.FileName, stop.Message));
                            return;
                        }
                    }
                }));
            }
            await Task.WhenAll(tasks);

            // Stop vince sulle altre fermate, l'unità in volo dell'altro file è stata scartata; con tutti i file conclusi la fase è finita
            bool unfinished;
            lock (this._lock)
            {
                unfinished = this._files.Exists(f => !this.IsFinished(f));
            }
            if (cancellation.IsCancellationRequested && unfinished)
                return new TranslationStop { Reason = TranslationStopReason.Stop, At = DateTimeOffset.Now };

            return this._pause;
        }

        #endregion

        #region Metodi protected

        /// <summary>
        /// True se il file ha ancora lavoro che lo scheduler deve prendere
        /// </summary>
        /// <param name="file">File del job</param>
        /// <returns>True se il file va in coda</returns>
        protected abstract bool HasWork(TranslationRunFile file);

        /// <summary>
        /// True se il file ha finito la fase; uno Stop con file non finiti ferma la fase
        /// </summary>
        /// <param name="file">File del job</param>
        /// <returns>True a file finito</returns>
        protected abstract bool IsFinished(TranslationRunFile file);

        /// <summary>
        /// Lavora un file fino alla fine o a una fermata degli altri file; un'eccezione ferma la fase
        /// </summary>
        /// <param name="file">File del job</param>
        /// <param name="provider">Provider condiviso</param>
        /// <param name="cancellation">Annullamento di Stop</param>
        /// <returns>Task del file</returns>
        protected abstract Task ProcessFileAsync(TranslationRunFile file, ITranslationModelProvider provider, CancellationToken cancellation);

        /// <summary>
        /// Contesto di un file per le chiamate: prefisso col contesto del progetto e il glossario del job, validatore coi suoi
        /// termini e battute delle tratte condivise
        /// </summary>
        /// <param name="file">File del job</param>
        /// <returns>Contesto del file</returns>
        protected FileContext CreateContext(TranslationRunFile file)
        {
            Dictionary<int, SharedStretch> shared;
            HashSet<int> copies;
            lock (this._lock)
            {
                shared = SharedStretch.MapFile(this._job.SharedStretches, file.State.FileId);
                copies = SharedStretch.MovingSignCopies(this._job.SharedStretches, file.State.FileId);
            }

            // Il sorgente numerato del prefisso conta una volta ogni cartello in movimento: i suoi ridisegni sono copie
            return new FileContext(file, TranslationRequestBuilder.CreateTranslationPrefix(this._context, this._job.Glossary, file.State.SourceFileName,
                file.State.Cues.FindAll(c => !copies.Contains(c.Id))),
                new TranslationValidator(file.Document.Format, this._job.Glossary), shared);
        }

        /// <summary>
        /// Consegne di un gruppo di id: la prima, poi i ritentativi sulle sole respinte. Un cartello con meno a capo del sorgente
        /// segna di solito una consegna slittata nell'elenco dei cartelli: si tengono i cartelli prima di lui e si richiedono da lui
        /// alla fine, mentre dialoghi e canzoni, nell'altro elenco, si giudicano; se lo stesso cartello ci ricade, si chiede da solo
        /// e i cartelli dopo diventano una consegna a sé. Una battuta respinta per altro ha fino a due ritentativi, poi si chiede da
        /// sola; segni, caratteri riservati e consegne doppie dopo il primo ritentativo si riparano. Un cartello solo fra quelli
        /// chiesti si accetta con gli a capo che ha, e resta da rivedere solo se non ha testo.
        /// Una risposta troncata dal tetto dei token in uscita torna al chiamante se è la prima consegna con whole vero, che divide
        /// l'unità e rimpicciolisce i blocchi seguenti del file; altrimenti si dimezzano le battute in attesa. Una risposta incompleta
        /// per un altro motivo non dipende dalla lunghezza: non si divide e non cambia i blocchi del file, e le battute in attesa
        /// restano da rivedere col motivo, filtro dei contenuti o incompleta
        /// </summary>
        /// <param name="context">File con prefisso e validatore</param>
        /// <param name="work">Esiti dell'unità in memoria</param>
        /// <param name="plan">Tipi, modello, instructions e coda delle chiamate</param>
        /// <param name="ids">Id da consegnare</param>
        /// <param name="kind">Tipo della prima consegna</param>
        /// <param name="attempt">Ritentativi già fatti</param>
        /// <param name="rejectedLines">Righe degli errori della consegna precedente per la prima richiesta, vuote per una consegna nuova</param>
        /// <param name="whole">True per la prima consegna di un'unità che si divide dal chiamante</param>
        /// <param name="provider">Provider condiviso</param>
        /// <param name="cancellation">Annullamento di Stop</param>
        /// <returns>True se la prima consegna con whole è tornata troncata dal tetto dei token in uscita</returns>
        protected async Task<bool> DeliverAsync(FileContext context, DeliveryWork work, DeliveryPlan plan, List<int> ids, TranslationCallKind kind, int attempt,
            IReadOnlyList<string> rejectedLines, bool whole, ITranslationModelProvider provider, CancellationToken cancellation)
        {
            List<int> pending = ids;
            TranslationCallKind currentKind = kind;
            int currentAttempt = attempt;
            bool first = whole;
            HashSet<int> restarted = new HashSet<int>();
            List<KeyValuePair<List<int>, IReadOnlyList<string>>> apart = new List<KeyValuePair<List<int>, IReadOnlyList<string>>>();

            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                List<FrozenCue> requestedCues = new List<FrozenCue>();
                foreach (int id in pending)
                    requestedCues.Add(context.Frozen[id]);

                TranslationModelRequest request = this.CreateRequest(currentKind, context.File.State.FileId, plan.Model, plan.ReasoningEffort, plan.Instructions, context.Prefix,
                    plan.Tail(requestedCues, rejectedLines), TranslationRequestBuilder.CreateTranslationTools());
                request.PrefixReused = plan.PrefixReused;
                int? requestAttempt = currentAttempt > 0 && currentKind == plan.RetryKind ? currentAttempt : null;

                foreach (int id in pending)
                    work.Get(id).Attempts++;

                KeyValuePair<AiResponseResult, TranslationCallRecord> sent = await this.SendAsync(request, context.File.State.FileId, work, pending, requestAttempt, provider, cancellation);
                AiResponseResult response = sent.Key;
                TranslationCallRecord record = sent.Value;

                if (response.Status == AiResponseStatus.Incomplete)
                {
                    bool length = response.IncompleteReason == INCOMPLETE_MAX_OUTPUT_TOKENS;
                    record.ResponseStatus = TranslationCallResponseStatus.Incomplete;
                    record.IncompleteReason = response.IncompleteReason;
                    record.Outcome = TranslationCallOutcome.Reduced;
                    record.Usage = new TranslationCallUsage(response.Usage, 0);
                    this.RecordCall(record, work);
                    if (length && first)
                        return true;

                    // Troppo lunga: le accettate restano, le battute in attesa si dimezzano. Incompleta per altro: restano da rivedere
                    List<List<int>> parts = new List<List<int>>();
                    if (length)
                    {
                        List<SubtitleCue> live = context.GetLiveCues(pending);
                        foreach (IReadOnlyList<SubtitleCue> half in new SubtitleBlockPlanner(live).SplitBlock(live))
                        {
                            List<int> halfIds = new List<int>();
                            foreach (SubtitleCue cue in half)
                                halfIds.Add(cue.Id.Value);
                            parts.Add(halfIds);
                        }
                    }

                    if (parts.Count < 2)
                    {
                        foreach (int id in pending)
                            work.MarkToReview(id, response.IncompleteReason == INCOMPLETE_CONTENT_FILTER ? REASON_CONTENT_FILTER : REASON_INCOMPLETE);
                        break;
                    }

                    this.RaiseLog(AppText.F("toolbox.subtitleTranslation.run.retryReduced", context.File.FileName, parts[0].Count, parts[1].Count));
                    foreach (List<int> part in parts)
                    {
                        await this.DeliverAsync(context, work, plan, part, plan.ReducedKind, currentAttempt, LinesOf(rejectedLines, part), false, provider, cancellation);
                    }
                    break;
                }

                List<string> submissions = new List<string>();
                foreach (JsonObject item in response.Items)
                {
                    if (AiOAuthFlow.GetString(item, "type") == "function_call" && AiOAuthFlow.GetString(item, "name") == TranslationRequestBuilder.SUBMIT_TRANSLATION_TOOL)
                        submissions.Add(AiOAuthFlow.GetString(item, "arguments"));
                }

                // Segni, caratteri riservati e consegne doppie si riparano sulle battute che hanno già avuto un ritentativo
                TranslationValidationResult result = context.Validator.Validate(pending, context.GetLiveCues(pending), submissions,
                    pending.FindAll(id => work.Get(id).Rejections.Count > 0));

                // Primo cartello con meno a capo del sorgente nell'ordine crescente di id, quello in cui il modello consegna: i cartelli
                // dopo di lui, slittati nel loro elenco, si richiedono senza giudicarli
                int cut = result.Cues.Where(cue => cue.Issues.Any(issue => issue.Code == TranslationValidator.LINE_BREAK_MISSING)).Select(cue => cue.Id)
                    .DefaultIfEmpty(int.MaxValue).Min();
                List<int> shifted = pending.FindAll(id => id > cut && context.GetLiveCue(id).Kind == SubtitleCueKind.Sign);
                List<CueValidationResult> judged = result.Cues.Where(cue => !shifted.Contains(cue.Id)).ToList();
                IReadOnlyList<string> modelLines = result.ToModelLines();

                List<int> rejected = new List<int>();
                SortedSet<string> reasons = new SortedSet<string>(StringComparer.Ordinal);
                record.Usage = new TranslationCallUsage(response.Usage, 0);
                foreach (CueValidationResult cue in judged)
                {
                    if (!cue.HasErrors)
                        continue;

                    rejected.Add(cue.Id);
                    List<string> lines = new List<string>();
                    foreach (TranslationValidationIssue issue in cue.Issues)
                    {
                        if (issue.Severity != IssueSeverity.Error)
                            continue;
                        lines.Add(issue.ToModelLine());
                        reasons.Add(issue.Code);
                    }
                    record.Rejections.Add(new TranslationCallRejection { Id = cue.Id, Reasons = lines });
                }
                record.Accepted = judged.Count - rejected.Count;
                record.Rejected = rejected.Count;
                record.Outcome = rejected.Count == 0 && shifted.Count == 0 ? TranslationCallOutcome.Accepted : TranslationCallOutcome.PartiallyRejected;
                int callSeq = this.RecordCall(record, work);

                List<int> retry = new List<int>();
                foreach (CueValidationResult cue in judged)
                {
                    TranslatedCueState state = work.Get(cue.Id);
                    if (cue.HasErrors)
                    {
                        state.Rejections.Add(new CueRejection { CallSeq = callSeq, Attempt = currentAttempt + 1, Reasons = record.Rejections.Find(r => r.Id == cue.Id).Reasons });
                        bool lineBreak = cue.Issues.Any(issue => issue.Code == TranslationValidator.LINE_BREAK_MISSING);
                        if (lineBreak && !restarted.Add(cue.Id))
                        {
                            // Ricaduta sullo stesso cartello: lui da solo, i cartelli dopo come consegna a sé
                            apart.Add(new KeyValuePair<List<int>, IReadOnlyList<string>>(new List<int> { cue.Id }, LinesOf(modelLines, new List<int> { cue.Id })));
                            if (shifted.Count > 0)
                                apart.Add(new KeyValuePair<List<int>, IReadOnlyList<string>>(shifted, LinesOf(modelLines, shifted)));
                            shifted = new List<int>();
                        }
                        else if (lineBreak || state.Rejections.Count <= MAX_RETRIES)
                        {
                            retry.Add(cue.Id);
                        }
                        else if (pending.Count > 1)
                        {
                            apart.Add(new KeyValuePair<List<int>, IReadOnlyList<string>>(new List<int> { cue.Id }, LinesOf(modelLines, new List<int> { cue.Id })));
                        }
                        else
                        {
                            // Nessun testo inventato: una battuta da sola che anche dopo i ritentativi non ha un testo valido resta
                            // senza testo nuovo coi suoi ultimi errori
                            work.MarkToReview(cue.Id, string.Join(" ", record.Rejections.Find(r => r.Id == cue.Id).Reasons));
                        }
                        continue;
                    }

                    // Testo validato accettato, con gli avvisi del glossario e le riparazioni del codice
                    state.Status = TranslatedCueStatus.Accepted;
                    state.Text = cue.Text;
                    state.ToReviewReason = null;
                    state.AcceptedCallSeq = callSeq;
                    state.GlossaryWarnings = cue.GetGlossaryWarnings();
                    state.Repairs = cue.Issues.Where(issue => issue.Severity == IssueSeverity.Warning && issue.Code != TranslationValidator.GLOSSARY_MISSING)
                        .Select(issue => issue.ToModelLine()).ToList();
                    work.Accepted[cue.Id] = cue.Text;
                }
                retry.AddRange(shifted);

                if (currentKind == plan.RetryKind && currentAttempt > 0)
                {
                    this.RaiseLog(AppText.F("toolbox.subtitleTranslation.run.retrySaved", context.File.FileName, currentAttempt, record.Accepted, record.Rejected));
                }
                else if (first)
                {
                    // Esito della prima consegna per la riga del log dell'unità
                    work.FirstAccepted = record.Accepted.Value;
                    work.FirstRejected = record.Rejected.Value;
                    work.FirstReasons.UnionWith(reasons);
                }

                if (retry.Count == 0)
                    break;

                currentAttempt++;
                pending = pending.FindAll(id => retry.Contains(id));
                rejectedLines = LinesOf(modelLines, retry);
                currentKind = plan.RetryKind;
                first = false;
            }

            // Consegne a sé, dopo le battute rimaste nel gruppo, ciascuna con gli errori delle sue battute
            foreach (KeyValuePair<List<int>, IReadOnlyList<string>> part in apart)
                await this.DeliverAsync(context, work, plan, part.Key, plan.RetryKind, currentAttempt + 1, part.Value, false, provider, cancellation);

            return false;
        }

        /// <summary>
        /// Piano delle consegne di traduzione di un blocco, di una tratta del file o di una battuta da rivedere: la coda porta le
        /// coppie accettate che vengono prima della prima battuta da consegnare nell'ordine delle battute congelate, dallo stato
        /// salvato, copie delle tratte comprese, e dall'unità in corso. Le copie di una tratta del file stesso, come i fotogrammi di
        /// un cartello in movimento, restano fuori
        /// </summary>
        /// <param name="context">File con prefisso e battute</param>
        /// <param name="work">Esiti dell'unità in memoria</param>
        /// <returns>Piano delle consegne</returns>
        protected DeliveryPlan CreateTranslationPlan(FileContext context, DeliveryWork work)
        {
            TranslationJobFileState state = context.File.State;

            // Tratte di cui il file ha la prima occorrenza e blocchi ancora da fare, questa unità compresa: stessa sessione B
            int units;
            lock (this._lock)
            {
                units = state.Blocks.FindAll(b => b.Status == TranslationBlockStatus.Pending).Count
                    + this._job.SharedStretches.FindAll(s => s.Status == TranslationBlockStatus.Pending && s.Occurrences[0].FileId == state.FileId).Count;
            }

            return new DeliveryPlan
            {
                RetryKind = TranslationCallKind.BRetry,
                ReducedKind = TranslationCallKind.BReduced,
                Model = this._job.Models.Translation,
                ReasoningEffort = this._job.ReasoningEfforts.Translation,
                Instructions = this._translationInstructions,
                PrefixReused = units > 1,
                Tail = (requested, rejected) =>
                {
                    Dictionary<int, string> accepted = new Dictionary<int, string>();
                    lock (this._lock)
                    {
                        foreach (TranslatedCueState translation in state.Translations)
                        {
                            if (translation.Status == TranslatedCueStatus.Accepted && translation.Text != null)
                                accepted[translation.Id] = translation.Text;
                        }
                    }
                    foreach (KeyValuePair<int, string> pair in work.Accepted)
                        accepted[pair.Key] = pair.Value;

                    List<KeyValuePair<FrozenCue, string>> pairs = new List<KeyValuePair<FrozenCue, string>>();
                    foreach (FrozenCue cue in state.Cues.GetRange(0, state.Cues.FindIndex(c => c.Id == requested[0].Id)))
                    {
                        string text;
                        SharedStretch stretch;
                        if (accepted.TryGetValue(cue.Id, out text) && !(context.Shared.TryGetValue(cue.Id, out stretch)
                            && stretch.Occurrences[0].FileId == state.FileId && !stretch.IsMaster(state.FileId, cue.Id)))
                            pairs.Add(new KeyValuePair<FrozenCue, string>(cue, text));
                    }
                    if (pairs.Count > TranslationRequestBuilder.TAIL_PAIRS)
                        pairs.RemoveRange(0, pairs.Count - TranslationRequestBuilder.TAIL_PAIRS);

                    return TranslationRequestBuilder.CreateTranslationTail(pairs, requested, rejected);
                }
            };
        }

        /// <summary>
        /// Richiesta di una chiamata: prefisso del file e coda in messaggi nuovi, perché un nodo JSON appartiene a un solo array, e
        /// tool_choice sul tool della fase, uguale in tutte le richieste della sessione
        /// </summary>
        /// <param name="kind">Tipo di chiamata</param>
        /// <param name="fileId">File</param>
        /// <param name="model">Modello della fase</param>
        /// <param name="reasoningEffort">Livello di ragionamento della fase, null per quello del modello</param>
        /// <param name="instructions">Instructions della fase</param>
        /// <param name="prefix">Messaggio di prefisso del file</param>
        /// <param name="tail">Ultimo messaggio</param>
        /// <param name="tools">Tool offerti</param>
        /// <returns>Richiesta da mandare</returns>
        protected TranslationModelRequest CreateRequest(TranslationCallKind kind, string fileId, string model, string reasoningEffort, string instructions, string prefix, string tail, JsonArray tools)
        {
            TranslationModelRequest request = new TranslationModelRequest();
            request.Kind = kind;
            request.ProjectId = this._job.ProjectId;
            request.FileId = fileId;
            request.Request.Model = model;
            request.Request.ReasoningEffort = reasoningEffort ?? "";
            request.Request.Instructions = instructions;
            request.Request.Input = new JsonArray(TranslationRequestBuilder.CreateUserMessage(prefix), TranslationRequestBuilder.CreateUserMessage(tail));
            request.Request.Tools = tools;

            // Ogni fase offre un tool solo e lo forza: una risposta in testo libero non consegna nulla e si ripagherebbe intera
            request.Request.ToolChoice = new JsonObject { ["type"] = "function", ["name"] = tools[0]["name"].DeepClone() };
            return request;
        }

        /// <summary>
        /// Manda una richiesta: i tentativi di trasporto falliti si registrano subito, quello riuscito torna da completare con l'esito
        /// </summary>
        /// <param name="request">Richiesta</param>
        /// <param name="fileId">File</param>
        /// <param name="work">Unità della chiamata</param>
        /// <param name="ids">Id richiesti</param>
        /// <param name="attempt">Ritentativo, null per la prima consegna</param>
        /// <param name="provider">Provider condiviso</param>
        /// <param name="cancellation">Annullamento di Stop</param>
        /// <returns>Risposta e riga del registro del tentativo riuscito, non ancora registrata</returns>
        protected async Task<KeyValuePair<AiResponseResult, TranslationCallRecord>> SendAsync(TranslationModelRequest request, string fileId, DeliveryWork work, List<int> ids, int? attempt,
            ITranslationModelProvider provider, CancellationToken cancellation)
        {
            TranslationTransportAttempt delivered = null;
            request.AttemptFinished = transport =>
            {
                if (transport.Error == null && !transport.Stopped)
                {
                    delivered = transport;
                    return;
                }

                TranslationCallRecord failed = this.CreateCallRecord(transport, request.Kind, fileId, work.BlockIndex, ids, attempt);
                failed.ResponseStatus = TranslationCallResponseStatus.Error;
                failed.Outcome = TranslationCallOutcome.Error;
                if (transport.Error != null)
                {
                    failed.HttpStatus = transport.Error.HttpStatus;
                    failed.ErrorKind = transport.Error.Kind;
                    failed.ServiceCode = string.IsNullOrEmpty(transport.Error.ServiceCode) ? null : transport.Error.ServiceCode;
                    failed.Usage = transport.Error.Usage != null ? new TranslationCallUsage(transport.Error.Usage, 0) : null;
                }
                this.RecordCall(failed, work);
            };

            AiResponseResult response = await provider.SendAsync(request, cancellation);
            TranslationCallRecord record = this.CreateCallRecord(delivered, request.Kind, fileId, work.BlockIndex, ids, attempt);
            record.HttpStatus = response.HttpStatus;
            return new KeyValuePair<AiResponseResult, TranslationCallRecord>(response, record);
        }

        /// <summary>
        /// Accoda una riga al registro del job, su disco e nel pannello, e la lega all'unità
        /// </summary>
        /// <param name="record">Riga da registrare</param>
        /// <param name="work">Unità della chiamata</param>
        /// <returns>Progressivo assegnato</returns>
        protected int RecordCall(TranslationCallRecord record, DeliveryWork work)
        {
            lock (this._lock)
            {
                record.CallSeq = this._calls.Count > 0 ? this._calls[this._calls.Count - 1].CallSeq + 1 : 1;
                this._store.AppendCall(this._job.Id, record);
                this._calls.Add(record);
                work.CallSeqs.Add(record.CallSeq);
            }

            this.RaiseChanged();
            return record.CallSeq;
        }

        /// <summary>
        /// Salva lo stato del file e poi la testata del job; chiamato sotto il lock
        /// </summary>
        /// <param name="state">Stato del file</param>
        protected void SaveFile(TranslationJobFileState state)
        {
            this._store.SaveFile(this._job.Id, state);
            this._store.Save(this._job);
        }

        /// <summary>
        /// Avvisa di una riga del log
        /// </summary>
        /// <param name="line">Riga</param>
        protected void RaiseLog(string line)
        {
            this.Log?.Invoke(line);
        }

        /// <summary>
        /// Avvisa che lo stato è cambiato
        /// </summary>
        protected void RaiseChanged()
        {
            this.Changed?.Invoke();
        }

        /// <summary>
        /// Avvisa dell'avanzamento
        /// </summary>
        /// <param name="done">Unità fatte di tutti i file</param>
        /// <param name="total">Unità di tutti i file</param>
        /// <param name="fileName">File in lavoro</param>
        /// <param name="number">Unità del file</param>
        /// <param name="count">Unità del file in tutto</param>
        protected void RaiseProgress(int done, int total, string fileName, int number, int count)
        {
            this.Progress?.Invoke(done, total, fileName, number, count);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Righe degli errori che riguardano le battute date: quelle senza id e quelle con l'id di una delle battute
        /// </summary>
        /// <param name="lines">Righe per il modello «id CODICE: messaggio» o «CODICE: messaggio»</param>
        /// <param name="ids">Battute</param>
        /// <returns>Righe nell'ordine</returns>
        private static List<string> LinesOf(IReadOnlyList<string> lines, List<int> ids)
        {
            List<string> kept = new List<string>();
            foreach (string line in lines)
            {
                int space = line.IndexOf(' ');
                int id;
                if (space < 0 || !int.TryParse(line.Substring(0, space), NumberStyles.None, CultureInfo.InvariantCulture, out id) || ids.Contains(id))
                    kept.Add(line);
            }

            return kept;
        }

        /// <summary>
        /// Riga del registro di un tentativo di trasporto
        /// </summary>
        /// <param name="attempt">Tentativo arrivato al servizio</param>
        /// <param name="kind">Tipo di chiamata</param>
        /// <param name="fileId">File</param>
        /// <param name="blockIndex">Blocco, null fuori dai blocchi</param>
        /// <param name="ids">Id richiesti</param>
        /// <param name="retry">Ritentativo, null per la prima consegna</param>
        /// <returns>Riga da completare con l'esito</returns>
        private TranslationCallRecord CreateCallRecord(TranslationTransportAttempt attempt, TranslationCallKind kind, string fileId, int? blockIndex, List<int> ids, int? retry)
        {
            TranslationCallRecord record = new TranslationCallRecord();
            record.Kind = kind;
            record.FileId = fileId;
            record.BlockIndex = blockIndex;
            record.FirstId = ids[0];
            record.LastId = ids[ids.Count - 1];
            record.RequestedIds = ids.Count;
            record.Attempt = retry;
            record.TransportAttempt = attempt.Number;
            record.Model = attempt.Model;
            record.ReasoningEffort = attempt.ReasoningEffort;
            record.SessionId = attempt.SessionId;
            record.ServerCacheKey = attempt.Result != null && !string.IsNullOrEmpty(attempt.Result.PromptCacheKey) ? attempt.Result.PromptCacheKey : null;
            record.StartedAt = attempt.StartedAt;
            record.DurationMs = attempt.DurationMs;
            return record;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True se un file si è fermato per limite d'uso o errore: gli altri non avviano unità nuove
        /// </summary>
        protected bool IsPaused
        {
            get
            {
                lock (this._lock)
                {
                    return this._pause != null;
                }
            }
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// File in lavoro: prefisso calcolato una volta, validatore e battute per id
        /// </summary>
        protected sealed class FileContext
        {
            #region Variabili di classe

            /// <summary>
            /// Battute vive del documento per id
            /// </summary>
            private readonly Dictionary<int, SubtitleCue> _live;

            #endregion

            #region Costruttore

            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="file">File del job</param>
            /// <param name="prefix">Messaggio di prefisso</param>
            /// <param name="validator">Validatore del file</param>
            /// <param name="shared">Tratta condivisa per id delle battute del file che ne fanno parte</param>
            public FileContext(TranslationRunFile file, string prefix, TranslationValidator validator, Dictionary<int, SharedStretch> shared)
            {
                this.File = file;
                this.Prefix = prefix;
                this.Validator = validator;
                this.Shared = shared;
                this.Frozen = new Dictionary<int, FrozenCue>();
                foreach (FrozenCue cue in file.State.Cues)
                    this.Frozen[cue.Id] = cue;
                this._live = new Dictionary<int, SubtitleCue>();
                foreach (SubtitleCue cue in file.Document.Cues)
                {
                    if (cue.Id.HasValue)
                        this._live[cue.Id.Value] = cue;
                }
            }

            #endregion

            #region Metodi pubblici

            /// <summary>
            /// Battute del documento con gli id dati, per il validatore e il pianificatore
            /// </summary>
            /// <param name="ids">Id in ordine</param>
            /// <returns>Battute vive</returns>
            public List<SubtitleCue> GetLiveCues(List<int> ids)
            {
                List<SubtitleCue> cues = new List<SubtitleCue>();
                foreach (int id in ids)
                    cues.Add(this._live[id]);
                return cues;
            }

            /// <summary>
            /// Battuta viva del documento
            /// </summary>
            /// <param name="id">Id della battuta</param>
            /// <returns>Battuta</returns>
            public SubtitleCue GetLiveCue(int id)
            {
                return this._live[id];
            }

            #endregion

            #region Proprietà

            /// <summary>
            /// File del job
            /// </summary>
            public TranslationRunFile File { get; }

            /// <summary>
            /// Messaggio di prefisso, uguale byte per byte in tutte le chiamate del file nella fase: col contesto letto all'avvio
            /// della fase e il glossario del job
            /// </summary>
            public string Prefix { get; }

            /// <summary>
            /// Validatore del file
            /// </summary>
            public TranslationValidator Validator { get; }

            /// <summary>
            /// Battute congelate per id
            /// </summary>
            public Dictionary<int, FrozenCue> Frozen { get; }

            /// <summary>
            /// Tratta condivisa per id delle battute del file che ne fanno parte
            /// </summary>
            public Dictionary<int, SharedStretch> Shared { get; }

            #endregion
        }

        /// <summary>
        /// Tipi, modello, instructions e coda delle consegne di un'unità
        /// </summary>
        protected sealed class DeliveryPlan
        {
            /// <summary>
            /// Tipo dei ritentativi
            /// </summary>
            public TranslationCallKind RetryKind { get; init; }

            /// <summary>
            /// Tipo delle consegne ridotte dopo una risposta incompleta
            /// </summary>
            public TranslationCallKind ReducedKind { get; init; }

            /// <summary>
            /// Modello della fase
            /// </summary>
            public string Model { get; init; }

            /// <summary>
            /// Livello di ragionamento della fase
            /// </summary>
            public string ReasoningEffort { get; init; }

            /// <summary>
            /// Instructions della fase
            /// </summary>
            public string Instructions { get; init; }

            /// <summary>
            /// Coda per le battute richieste e le righe degli scarti della consegna precedente
            /// </summary>
            public Func<IReadOnlyList<FrozenCue>, IReadOnlyList<string>, string> Tail { get; init; }

            /// <summary>
            /// True se il piano prevede altre unità della stessa sessione dopo questa
            /// </summary>
            public bool PrefixReused { get; init; }
        }

        /// <summary>
        /// Esiti di un'unità in memoria: si salvano solo a unità finita, uno Stop li scarta
        /// </summary>
        protected sealed class DeliveryWork
        {
            #region Costruttore

            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="blockIndex">Blocco dell'unità, null fuori dai blocchi</param>
            public DeliveryWork(int? blockIndex)
            {
                this.BlockIndex = blockIndex;
                this.Results = new SortedDictionary<int, TranslatedCueState>();
                this.Accepted = new SortedDictionary<int, string>();
                this.CallSeqs = new List<int>();
                this.FirstReasons = new SortedSet<string>(StringComparer.Ordinal);
            }

            #endregion

            #region Metodi pubblici

            /// <summary>
            /// Lascia la battuta senza testo nuovo
            /// </summary>
            /// <param name="id">Id della battuta</param>
            /// <param name="reason">Ultimi errori o incomplete</param>
            public void MarkToReview(int id, string reason)
            {
                TranslatedCueState state = this.Get(id);
                state.Status = TranslatedCueStatus.ToReview;
                state.Text = null;
                state.ToReviewReason = reason;
                state.AcceptedCallSeq = null;
                this.Accepted.Remove(id);
            }

            /// <summary>
            /// Esito della battuta, creato alla prima consegna
            /// </summary>
            /// <param name="id">Id della battuta</param>
            /// <returns>Esito in memoria</returns>
            public TranslatedCueState Get(int id)
            {
                TranslatedCueState state;
                if (!this.Results.TryGetValue(id, out state))
                {
                    state = new TranslatedCueState();
                    state.Id = id;
                    state.Status = TranslatedCueStatus.ToReview;
                    this.Results[id] = state;
                }
                return state;
            }

            #endregion

            #region Proprietà

            /// <summary>
            /// Blocco dell'unità, null fuori dai blocchi
            /// </summary>
            public int? BlockIndex { get; }

            /// <summary>
            /// Esiti per id
            /// </summary>
            public SortedDictionary<int, TranslatedCueState> Results { get; }

            /// <summary>
            /// Testi accettati nell'unità, per le coppie della coda dei ritentativi
            /// </summary>
            public SortedDictionary<int, string> Accepted { get; }

            /// <summary>
            /// Chiamate dell'unità
            /// </summary>
            public List<int> CallSeqs { get; }

            /// <summary>
            /// Accettate della prima consegna
            /// </summary>
            public int FirstAccepted { get; set; }

            /// <summary>
            /// Respinte della prima consegna
            /// </summary>
            public int FirstRejected { get; set; }

            /// <summary>
            /// Codici dei motivi della prima consegna
            /// </summary>
            public SortedSet<string> FirstReasons { get; }

            #endregion
        }

        #endregion
    }
}
