using RemuxForge.Core.Ai;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Fase di traduzione di un job: i file in parallelo fino al tetto, i blocchi di un file in sequenza. Per ogni blocco B, poi B′
    /// sulle sole battute respinte e B″ sulle metà dopo una risposta troncata; il blocco si salva solo intero. Stop scarta il blocco
    /// in corso, limite d'uso ed errori fermano i file fra un blocco e l'altro
    /// </summary>
    public sealed class TranslationRunner
    {
        #region Costanti

        /// <summary>
        /// Ritentativi di una battuta respinta prima dello stato da rivedere
        /// </summary>
        public const int MAX_RETRIES = 2;

        /// <summary>
        /// Obiettivo minimo del blocco dopo i dimezzamenti
        /// </summary>
        public const int MIN_BLOCK_SIZE = 18;

        /// <summary>
        /// Motivo dello stato da rivedere per una risposta troncata su un blocco che non si divide più
        /// </summary>
        public const string REASON_INCOMPLETE = "incomplete";

        /// <summary>
        /// Parte dell'ingresso servita dalla cache nella stima, dal terzo blocco di un file in poi
        /// </summary>
        private const double ESTIMATE_CACHED_SHARE = 0.8;

        /// <summary>
        /// Blocchi di un file prima che la stima conti la cache
        /// </summary>
        private const int ESTIMATE_UNCACHED_BLOCKS = 2;

        /// <summary>
        /// Caratteri attorno al testo di ogni battuta negli argomenti consegnati, per la stima dell'uscita
        /// </summary>
        private const int ESTIMATE_ENTRY_CHARS = 24;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Job su disco
        /// </summary>
        private readonly TranslationJobStore _store;

        /// <summary>
        /// Testata del job
        /// </summary>
        private readonly TranslationJob _job;

        /// <summary>
        /// File del job con documento e nome
        /// </summary>
        private readonly List<TranslationRunFile> _files;

        /// <summary>
        /// Registro delle chiamate del job, condiviso con la pagina
        /// </summary>
        private readonly List<TranslationCallRecord> _calls;

        /// <summary>
        /// Lock dello stato condiviso con chi legge job, file e registro
        /// </summary>
        private readonly object _lock;

        /// <summary>
        /// Instructions di B, B′ e B″, uguali per tutto il job
        /// </summary>
        private readonly string _instructions;

        /// <summary>
        /// Termini del glossario congelato per il validatore
        /// </summary>
        private readonly List<KeyValuePair<string, string>> _terms;

        /// <summary>
        /// Prima fermata di un file per limite d'uso o errore: gli altri file non avviano blocchi nuovi
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
        /// Blocco iniziato o salvato: blocchi fatti, blocchi totali, file, blocco del file e blocchi del file
        /// </summary>
        public event Action<int, int, string, int, int> Progress;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="store">Job su disco</param>
        /// <param name="job">Testata del job</param>
        /// <param name="files">File del job da tradurre, nell'ordine del progetto</param>
        /// <param name="calls">Registro delle chiamate del job</param>
        /// <param name="stateLock">Lock dello stato condiviso</param>
        public TranslationRunner(TranslationJobStore store, TranslationJob job, List<TranslationRunFile> files, List<TranslationCallRecord> calls, object stateLock)
        {
            this._store = store;
            this._job = job;
            this._files = files;
            this._calls = calls;
            this._lock = stateLock;
            this._instructions = TranslationRequestBuilder.CreateTranslationInstructions(job.Glossary.SourceLanguage, job.Glossary.TargetLanguage);
            this._terms = new List<KeyValuePair<string, string>>();
            foreach (GlossaryTerm term in job.Glossary.Terms)
                this._terms.Add(new KeyValuePair<string, string>(term.Source, term.Target));
            this._pause = null;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Traduce i file non conclusi: al più maxParallel file insieme, ognuno a blocchi in sequenza. Ogni blocco si salva intero;
        /// dopo una risposta troncata si divide in due metà, obiettivo e tetto del file si dimezzano e i blocchi che restano si
        /// ripianificano
        /// </summary>
        /// <param name="provider">Provider condiviso da tutti i file, con la pausa comune dei 429</param>
        /// <param name="maxParallel">File in parallelo</param>
        /// <param name="cancellation">Annullamento di Stop</param>
        /// <returns>Fermata, null se nessun file si è fermato</returns>
        public async Task<TranslationStop> RunAsync(ITranslationModelProvider provider, int maxParallel, CancellationToken cancellation)
        {
            Queue<TranslationRunFile> queue = new Queue<TranslationRunFile>();
            foreach (TranslationRunFile file in this._files)
            {
                if (file.State.TranslationStatus != TranslationFileStatus.Completed && file.State.TranslationStatus != TranslationFileStatus.Blocked)
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
                            lock (this._lock)
                            {
                                if (file.State.TranslationStatus == TranslationFileStatus.Pending)
                                    file.State.TranslationStatus = TranslationFileStatus.InProgress;
                            }
                            this.Changed?.Invoke();

                            FileContext context = new FileContext(file, TranslationRequestBuilder.CreateTranslationPrefix(this._job.Glossary, file.State.SourceFileName, file.State.Cues),
                                new TranslationValidator(file.Document.Format, this._job.Glossary.SourceLanguage, this._job.Glossary.TargetLanguage, this._terms));

                            // Blocchi da fare uno alla volta; senza blocchi da fare il file si chiude anche dopo uno Stop o una fermata
                            while (true)
                            {
                                TranslationBlock block;
                                lock (this._lock)
                                {
                                    block = file.State.Blocks.Find(b => b.Status == TranslationBlockStatus.Pending);
                                    if (block != null && this._pause != null)
                                        return;
                                }

                                if (block == null)
                                    break;

                                cancellation.ThrowIfCancellationRequested();
                                TranslationJobFileState state = file.State;
                                BlockWork work = new BlockWork(block);
                                List<int> ids = new List<int>();
                                foreach (FrozenCue cue in state.Cues)
                                {
                                    if (cue.Id >= block.FirstId && cue.Id <= block.LastId)
                                        ids.Add(cue.Id);
                                }

                                int number;
                                int count;
                                lock (this._lock)
                                {
                                    List<TranslationBlock> planned = state.Blocks.FindAll(b => b.Status != TranslationBlockStatus.Split);
                                    number = planned.IndexOf(block) + 1;
                                    count = planned.Count;
                                }
                                this.ReportProgress(file.FileName, number, count);

                                TranslationCallKind kind = block.SplitFrom.HasValue ? TranslationCallKind.BReduced : TranslationCallKind.B;
                                bool incomplete = await this.TranslateIdsAsync(context, work, ids, kind, 0, true, provider, cancellation);
                                List<SubtitleCue> live = context.GetLiveCues(ids);
                                IReadOnlyList<IReadOnlyList<SubtitleCue>> halves = incomplete ? new SubtitleBlockPlanner(live).SplitBlock(live) : null;

                                lock (this._lock)
                                {
                                    block.CallSeqs.AddRange(work.CallSeqs);
                                    if (halves != null && halves.Count == 2)
                                    {
                                        // Blocco diviso in due metà; target e tetto dimezzati valgono anche per i blocchi successivi, ripianificati
                                        int next = 0;
                                        foreach (TranslationBlock existing in state.Blocks)
                                            next = Math.Max(next, existing.Index);

                                        block.Status = TranslationBlockStatus.Split;
                                        int position = state.Blocks.IndexOf(block);
                                        TranslationBlock first = CreateBlock(next + 1, halves[0], block.Index);
                                        TranslationBlock second = CreateBlock(next + 2, halves[1], block.Index);
                                        state.Blocks.Insert(position + 1, first);
                                        state.Blocks.Insert(position + 2, second);
                                        state.TargetBlockSize = Math.Max(MIN_BLOCK_SIZE, state.TargetBlockSize / 2);
                                        state.BlockCap = Math.Max(state.TargetBlockSize, state.BlockCap / 2);

                                        // I blocchi dopo le metà non sono ancora tradotti: si rifanno coi valori nuovi
                                        state.Blocks.RemoveRange(position + 3, state.Blocks.Count - position - 3);
                                        List<SubtitleCue> remaining = new List<SubtitleCue>();
                                        foreach (SubtitleCue cue in file.Document.Cues)
                                        {
                                            if (cue.Id.HasValue && cue.Id.Value > second.LastId)
                                                remaining.Add(cue);
                                        }

                                        next += 2;
                                        foreach (IReadOnlyList<SubtitleCue> planned in new SubtitleBlockPlanner(remaining).PlanBlocks(state.TargetBlockSize, state.BlockCap))
                                        {
                                            next++;
                                            state.Blocks.Add(CreateBlock(next, planned, null));
                                        }
                                    }
                                    else
                                    {
                                        // Una battuta o un solo gruppo troncati non si dividono più: restano da rivedere senza testo
                                        if (incomplete)
                                        {
                                            foreach (int id in ids)
                                                work.MarkToReview(id, REASON_INCOMPLETE);
                                        }

                                        // Esiti del blocco nello stato del file
                                        state.CollectedNewTerms.AddRange(work.NewTerms);
                                        HashSet<int> withTerms = new HashSet<int>();
                                        foreach (CollectedNewTerm term in state.CollectedNewTerms)
                                            withTerms.UnionWith(term.CueIds);

                                        foreach (TranslatedCueState translation in work.Results.Values)
                                        {
                                            state.Translations.RemoveAll(t => t.Id == translation.Id);
                                            if (translation.Rejections.Count > 0)
                                                translation.Signals.Add(TranslationCueSignal.Retried);
                                            if (translation.GlossaryWarnings.Count > 0)
                                                translation.Signals.Add(TranslationCueSignal.Glossary);
                                            state.Translations.Add(translation);
                                        }

                                        foreach (TranslatedCueState translation in state.Translations)
                                        {
                                            if (withTerms.Contains(translation.Id) && !translation.Signals.Contains(TranslationCueSignal.NewTerm))
                                                translation.Signals.Add(TranslationCueSignal.NewTerm);
                                        }
                                        state.Translations.Sort((a, b) => a.Id.CompareTo(b.Id));
                                        block.Status = TranslationBlockStatus.Done;
                                    }

                                    this._store.SaveFile(this._job.Id, state);
                                    this._store.Save(this._job);
                                }

                                if (halves != null && halves.Count == 2)
                                    this.Log?.Invoke(AppText.F("toolbox.subtitleTranslation.run.blockReduced", file.FileName, number, halves[0].Count, halves[1].Count));
                                else
                                    this.Log?.Invoke(AppText.F("toolbox.subtitleTranslation.run.blockSaved", file.FileName, number, count, work.FirstAccepted, work.FirstRejected,
                                        work.FirstReasons.Count > 0 ? string.Join(", ", work.FirstReasons) : "-"));

                                this.ReportProgress(file.FileName, number, count);
                                this.Changed?.Invoke();
                            }

                            lock (this._lock)
                            {
                                file.State.TranslationStatus = TranslationFileStatus.Completed;
                                this._store.SaveFile(this._job.Id, file.State);
                                this._store.Save(this._job);
                            }
                            this.Log?.Invoke(AppText.F("toolbox.subtitleTranslation.run.fileCompleted", file.FileName));
                            this.Changed?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            // Il blocco in corso si scarta: il file resta com'era all'ultimo blocco salvato
                            bool stopped = cancellation.IsCancellationRequested;
                            TranslationStop stop = TranslationStop.Create(ex, stopped);
                            lock (this._lock)
                            {
                                if (this._pause == null && !stopped)
                                    this._pause = stop;
                            }

                            if (!stopped)
                                this.Log?.Invoke(AppText.F("toolbox.subtitleTranslation.run.filePaused", file.FileName, stop.Message));
                            return;
                        }
                    }
                }));
            }
            await Task.WhenAll(tasks);

            // Stop vince sulle altre fermate, il blocco in volo dell'altro file è stato scartato; con tutti i file conclusi il giro è finito
            bool unfinished;
            lock (this._lock)
            {
                unfinished = this._files.Exists(f => f.State.TranslationStatus != TranslationFileStatus.Completed);
            }
            if (cancellation.IsCancellationRequested && unfinished)
                return new TranslationStop { Reason = TranslationStopReason.Stop, At = DateTimeOffset.Now };

            return this._pause;
        }

        /// <summary>
        /// Stato iniziale di un file del job: nome e sorgente numerato congelati e piano dei blocchi con obiettivo e tetto di partenza
        /// </summary>
        /// <param name="file">File del progetto, con le impronte della preparazione</param>
        /// <param name="document">Documento preparato del file</param>
        /// <returns>Stato del file da salvare</returns>
        public static TranslationJobFileState CreateFileState(TranslationProjectFile file, SubtitleTranslationDocument document)
        {
            TranslationJobFileState state = new TranslationJobFileState();
            state.FileId = file.Id;
            state.SourceFileName = Path.GetFileName(file.Path);
            state.SourceSha256 = file.SourceSha256;
            state.NumberedSourceSha256 = file.Preparation.NumberedSourceSha256;
            state.PreparerVersion = file.Preparation.PreparerVersion;
            state.Encoding = file.Encoding;
            foreach (SubtitleCue cue in document.Cues)
            {
                if (cue.Id.HasValue)
                    state.Cues.Add(new FrozenCue { Id = cue.Id.Value, Group = cue.Group, Text = cue.CleanText });
            }

            foreach (IReadOnlyList<SubtitleCue> planned in new SubtitleBlockPlanner(document.Cues).PlanBlocks(state.TargetBlockSize, state.BlockCap))
                state.Blocks.Add(CreateBlock(state.Blocks.Count + 1, planned, null));

            return state;
        }

        /// <summary>
        /// Stima della traduzione per caratteri, come le chiamate B la manderebbero: il prefisso intero a ogni blocco, la coda
        /// con le coppie prima del blocco e gli argomenti consegnati lunghi quanto il sorgente
        /// </summary>
        /// <param name="glossary">Glossario approvato</param>
        /// <param name="files">Nome e battute preparate di ogni file</param>
        /// <param name="cache">True se l'accesso misura la cache del prefisso</param>
        /// <param name="reviewMode">Modalità della revisione AI</param>
        /// <returns>Stima della traduzione e della revisione</returns>
        public static TranslationEstimate Estimate(Glossary glossary, IReadOnlyList<KeyValuePair<string, IReadOnlyList<SubtitleCue>>> files, bool cache, TranslationReviewMode reviewMode)
        {
            TranslationEstimate estimate = new TranslationEstimate();
            string instructions = TranslationRequestBuilder.CreateTranslationInstructions(glossary.SourceLanguage, glossary.TargetLanguage);
            string tools = TranslationRequestBuilder.CreateTranslationTools().ToJsonString();
            long cached = 0;
            long review = 0;
            estimate.Files = files.Count;

            foreach (KeyValuePair<string, IReadOnlyList<SubtitleCue>> file in files)
            {
                List<FrozenCue> cues = new List<FrozenCue>();
                foreach (SubtitleCue cue in file.Value)
                {
                    if (cue.Id.HasValue)
                        cues.Add(new FrozenCue { Id = cue.Id.Value, Group = cue.Group, Text = cue.CleanText });
                }

                string prefix = TranslationRequestBuilder.CreateTranslationPrefix(glossary, file.Key, cues);
                int prefixTokens = GlossaryBuilder.EstimateTokens(instructions + tools + prefix);
                IReadOnlyList<IReadOnlyList<SubtitleCue>> blocks = new SubtitleBlockPlanner(file.Value).PlanBlocks(SubtitleBlockPlanner.DEFAULT_TARGET, SubtitleBlockPlanner.DEFAULT_CAP);
                estimate.Cues += cues.Count;
                estimate.Blocks += blocks.Count;

                int start = 0;
                for (int b = 0; b < blocks.Count; b++)
                {
                    // Coppie della coda: le battute prima del blocco con una traduzione lunga quanto il sorgente
                    List<KeyValuePair<FrozenCue, string>> pairs = new List<KeyValuePair<FrozenCue, string>>();
                    for (int i = Math.Max(0, start - TranslationRequestBuilder.TAIL_PAIRS); i < start; i++)
                        pairs.Add(new KeyValuePair<FrozenCue, string>(cues[i], cues[i].Text));
                    List<FrozenCue> requested = cues.GetRange(start, blocks[b].Count);
                    int blockTokens = prefixTokens + GlossaryBuilder.EstimateTokens(TranslationRequestBuilder.CreateTranslationTail(pairs, requested, new string[0]));

                    int outputChars = 0;
                    StringBuilder records = new StringBuilder();
                    foreach (FrozenCue cue in requested)
                    {
                        outputChars += cue.Text.Length + ESTIMATE_ENTRY_CHARS;
                        records.Append(TranslationRequestBuilder.FormatRecord(cue.Id, cue.Group, cue.Text)).Append('\n');
                        records.Append(TranslationRequestBuilder.FormatTranslationRecord(cue.Id, cue.Group, cue.Text)).Append('\n');
                    }

                    estimate.InputTokens += blockTokens;
                    estimate.OutputTokens += (outputChars + GlossaryBuilder.CHARS_PER_TOKEN - 1) / GlossaryBuilder.CHARS_PER_TOKEN;
                    if (b >= ESTIMATE_UNCACHED_BLOCKS)
                        cached += (long)(blockTokens * ESTIMATE_CACHED_SHARE);

                    // Revisione completa: una diagnosi per blocco col prefisso del file e il blocco con le traduzioni
                    review += GlossaryBuilder.EstimateTokens(prefix) + GlossaryBuilder.EstimateTokens(records.ToString());
                    start += blocks[b].Count;
                }
            }

            estimate.CachedTokens = cache ? cached : null;
            estimate.ReviewInputTokens = reviewMode == TranslationReviewMode.Full ? review : null;
            return estimate;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Chiamate di un gruppo di id: la consegna, poi fino a due ritentativi sulle sole respinte, coi gruppi interi. Una risposta
        /// troncata della prima consegna del blocco torna al chiamante; dentro i ritentativi si dimezzano le sole battute in attesa
        /// </summary>
        /// <param name="context">File con prefisso e validatore</param>
        /// <param name="work">Esiti del blocco in memoria</param>
        /// <param name="ids">Id da consegnare</param>
        /// <param name="kind">B, BRetry o BReduced</param>
        /// <param name="attempt">Ritentativi già fatti nel blocco</param>
        /// <param name="whole">True per la prima consegna del blocco intero</param>
        /// <param name="provider">Provider condiviso</param>
        /// <param name="cancellation">Annullamento di Stop</param>
        /// <returns>True se la prima consegna del blocco è tornata troncata</returns>
        private async Task<bool> TranslateIdsAsync(FileContext context, BlockWork work, List<int> ids, TranslationCallKind kind, int attempt, bool whole,
            ITranslationModelProvider provider, CancellationToken cancellation)
        {
            List<int> pending = ids;
            IReadOnlyList<string> rejectedLines = new string[0];
            TranslationCallKind currentKind = kind;
            int currentAttempt = attempt;
            bool first = whole;

            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                // Richiesta: prefisso del file e coda con le coppie accettate prima del primo id da consegnare
                int firstId = pending[0];
                SortedDictionary<int, string> accepted = new SortedDictionary<int, string>();
                lock (this._lock)
                {
                    foreach (TranslatedCueState translation in context.File.State.Translations)
                    {
                        if (translation.Status == TranslatedCueStatus.Accepted && translation.Text != null && translation.Id < firstId)
                            accepted[translation.Id] = translation.Text;
                    }
                }
                foreach (KeyValuePair<int, string> pair in work.Accepted)
                {
                    if (pair.Key < firstId)
                        accepted[pair.Key] = pair.Value;
                }

                List<KeyValuePair<FrozenCue, string>> pairs = new List<KeyValuePair<FrozenCue, string>>();
                foreach (KeyValuePair<int, string> pair in accepted)
                    pairs.Add(new KeyValuePair<FrozenCue, string>(context.Frozen[pair.Key], pair.Value));
                if (pairs.Count > TranslationRequestBuilder.TAIL_PAIRS)
                    pairs.RemoveRange(0, pairs.Count - TranslationRequestBuilder.TAIL_PAIRS);

                List<FrozenCue> requestedCues = new List<FrozenCue>();
                foreach (int id in pending)
                    requestedCues.Add(context.Frozen[id]);

                // Messaggi nuovi a ogni richiesta: un nodo JSON appartiene a un solo array
                TranslationModelRequest request = new TranslationModelRequest();
                request.Kind = currentKind;
                request.ProjectId = this._job.ProjectId;
                request.FileId = context.File.State.FileId;
                request.Request.Model = this._job.Models.Translation;
                request.Request.ReasoningEffort = this._job.ReasoningEfforts.Translation ?? "";
                request.Request.Instructions = this._instructions;
                request.Request.Input = new JsonArray(TranslationRequestBuilder.CreateUserMessage(context.Prefix),
                    TranslationRequestBuilder.CreateUserMessage(TranslationRequestBuilder.CreateTranslationTail(pairs, requestedCues, rejectedLines)));
                request.Request.Tools = TranslationRequestBuilder.CreateTranslationTools();
                TranslationTransportAttempt delivered = null;
                TranslationCallKind requestKind = currentKind;
                int? requestAttempt = currentKind == TranslationCallKind.BRetry ? currentAttempt : null;
                List<int> requested = pending;

                // I tentativi falliti si registrano subito; quello riuscito dopo la validazione, con l'esito della consegna
                request.AttemptFinished = transport =>
                {
                    if (transport.Error == null && !transport.Stopped)
                    {
                        delivered = transport;
                        return;
                    }

                    TranslationCallRecord failed = this.CreateCallRecord(transport, requestKind, context.File.State.FileId, work.Block.Index, requested, requestAttempt);
                    failed.ResponseStatus = TranslationCallResponseStatus.Error;
                    failed.Outcome = TranslationCallOutcome.Error;
                    if (transport.Error != null)
                    {
                        failed.HttpStatus = transport.Error.HttpStatus;
                        failed.ErrorKind = transport.Error.Kind;
                        failed.ServiceCode = string.IsNullOrEmpty(transport.Error.ServiceCode) ? null : transport.Error.ServiceCode;
                    }
                    this.RecordCall(failed, work);
                };

                foreach (int id in pending)
                    work.Get(id).Attempts++;

                AiResponseResult response = await provider.SendAsync(request, cancellation);
                TranslationCallRecord record = this.CreateCallRecord(delivered, currentKind, context.File.State.FileId, work.Block.Index, pending, requestAttempt);
                record.HttpStatus = response.HttpStatus;

                if (response.Status == AiResponseStatus.Incomplete)
                {
                    record.ResponseStatus = TranslationCallResponseStatus.Incomplete;
                    record.IncompleteReason = response.IncompleteReason;
                    record.Outcome = TranslationCallOutcome.Reduced;
                    record.Usage = new TranslationCallUsage(response.Usage, 0);
                    this.RecordCall(record, work);
                    if (first)
                        return true;

                    // Troncata dentro i ritentativi: le accettate restano, le battute in attesa si dimezzano
                    List<SubtitleCue> live = context.GetLiveCues(pending);
                    IReadOnlyList<IReadOnlyList<SubtitleCue>> halves = new SubtitleBlockPlanner(live).SplitBlock(live);
                    if (halves.Count < 2)
                    {
                        foreach (int id in pending)
                            work.MarkToReview(id, REASON_INCOMPLETE);
                        return false;
                    }

                    this.Log?.Invoke(AppText.F("toolbox.subtitleTranslation.run.retryReduced", context.File.FileName, halves[0].Count, halves[1].Count));
                    foreach (IReadOnlyList<SubtitleCue> half in halves)
                    {
                        List<int> halfIds = new List<int>();
                        foreach (SubtitleCue cue in half)
                            halfIds.Add(cue.Id.Value);
                        await this.TranslateIdsAsync(context, work, halfIds, TranslationCallKind.BReduced, currentAttempt, false, provider, cancellation);
                    }
                    return false;
                }

                List<string> submissions = new List<string>();
                foreach (JsonObject item in response.Items)
                {
                    if (AiOAuthFlow.GetString(item, "type") == "function_call" && AiOAuthFlow.GetString(item, "name") == TranslationRequestBuilder.SUBMIT_TRANSLATION_TOOL)
                        submissions.Add(AiOAuthFlow.GetString(item, "arguments"));
                }

                TranslationValidationResult result = context.Validator.Validate(pending, context.GetLiveCues(pending), submissions);
                // Termini nuovi della prima consegna leggibile, con le battute richieste il cui sorgente contiene il termine
                List<CollectedNewTerm> terms = new List<CollectedNewTerm>();
                foreach (string submission in submissions)
                {
                    JsonObject arguments;
                    try
                    {
                        arguments = JsonNode.Parse(submission ?? "") as JsonObject;
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (arguments == null || !(arguments["translations"] is JsonArray))
                        continue;

                    JsonArray entries = arguments["new_terms"] as JsonArray;
                    for (int i = 0; entries != null && i < entries.Count; i++)
                    {
                        JsonObject entry = entries[i] as JsonObject;
                        string source = entry != null ? AiOAuthFlow.GetString(entry, "source") : "";
                        string target = entry != null ? AiOAuthFlow.GetString(entry, "target") : "";
                        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                            continue;

                        CollectedNewTerm term = new CollectedNewTerm();
                        term.Source = source.Trim();
                        term.Target = target.Trim();
                        term.Note = AiOAuthFlow.GetString(entry, "note") ?? "";
                        term.BlockIndex = work.Block.Index;
                        string word = TranslationValidator.StripArticle(term.Source, LanguageValidator.NormalizeToIso6392(this._job.Glossary.SourceLanguage));
                        if (!string.IsNullOrEmpty(word))
                        {
                            Regex pattern = TranslationValidator.WordPattern(word);
                            foreach (int id in pending)
                            {
                                if (pattern.IsMatch(TranslationValidator.GlossaryPlain(context.Frozen[id].Text)))
                                    term.CueIds.Add(id);
                            }
                        }
                        terms.Add(term);
                    }

                    // Come il validatore, vale la prima consegna con l'array delle traduzioni
                    break;
                }

                List<int> rejected = new List<int>();
                SortedSet<string> reasons = new SortedSet<string>(StringComparer.Ordinal);
                record.Usage = new TranslationCallUsage(response.Usage, 0);
                record.NewTerms = terms.Count;
                foreach (CueValidationResult cue in result.Cues)
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
                record.Accepted = result.Cues.Count - rejected.Count;
                record.Rejected = rejected.Count;
                record.Outcome = rejected.Count == 0 ? TranslationCallOutcome.Accepted : TranslationCallOutcome.PartiallyRejected;
                int callSeq = this.RecordCall(record, work);

                foreach (CollectedNewTerm term in terms)
                {
                    term.CallSeq = callSeq;
                    work.NewTerms.Add(term);
                }

                foreach (CueValidationResult cue in result.Cues)
                {
                    TranslatedCueState state = work.Get(cue.Id);
                    if (cue.HasErrors)
                    {
                        state.Rejections.Add(new CueRejection { CallSeq = callSeq, Attempt = currentAttempt + 1, Reasons = record.Rejections.Find(r => r.Id == cue.Id).Reasons });
                        continue;
                    }

                    // Testo validato accettato, con gli avvisi del glossario
                    state.Status = TranslatedCueStatus.Accepted;
                    state.Text = cue.Text;
                    state.ToReviewReason = null;
                    state.AcceptedCallSeq = callSeq;
                    state.Version = 1;
                    state.GlossaryWarnings.Clear();
                    foreach (TranslationValidationIssue issue in cue.Issues)
                    {
                        if (issue.Code == TranslationValidator.GLOSSARY_MISSING)
                            state.GlossaryWarnings.Add(new GlossaryWarning { Source = issue.Args["source"], Target = issue.Args["target"] });
                    }
                    work.Accepted[cue.Id] = cue.Text;
                }

                if (currentKind == TranslationCallKind.BRetry)
                {
                    this.Log?.Invoke(AppText.F("toolbox.subtitleTranslation.run.retrySaved", context.File.FileName, currentAttempt, record.Accepted, record.Rejected));
                }
                else if (first)
                {
                    // Esito della prima consegna per la riga del log del blocco
                    work.FirstAccepted = record.Accepted.Value;
                    work.FirstRejected = record.Rejected.Value;
                    work.FirstReasons.UnionWith(reasons);
                }

                if (rejected.Count == 0)
                    return false;

                currentAttempt++;
                if (currentAttempt > MAX_RETRIES)
                {
                    // Nessun testo inventato: le battute respinte dopo i ritentativi restano da rivedere coi loro ultimi errori
                    foreach (int id in rejected)
                        work.MarkToReview(id, string.Join(" ", record.Rejections.Find(r => r.Id == id).Reasons));
                    return false;
                }

                pending = rejected;
                rejectedLines = result.ToModelLines();
                currentKind = TranslationCallKind.BRetry;
                first = false;
            }
        }

        /// <summary>
        /// Riga del registro di un tentativo di trasporto di B, B′ o B″
        /// </summary>
        /// <param name="attempt">Tentativo arrivato al servizio</param>
        /// <param name="kind">Tipo di chiamata</param>
        /// <param name="fileId">File</param>
        /// <param name="blockIndex">Blocco</param>
        /// <param name="ids">Id richiesti</param>
        /// <param name="retry">Ritentativo, null per B e B″</param>
        /// <returns>Riga da completare con l'esito</returns>
        private TranslationCallRecord CreateCallRecord(TranslationTransportAttempt attempt, TranslationCallKind kind, string fileId, int blockIndex, List<int> ids, int? retry)
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
            record.StartedAt = attempt.StartedAt;
            record.DurationMs = attempt.DurationMs;
            return record;
        }

        /// <summary>
        /// Accoda una riga al registro del job, su disco e nel pannello, e la lega al blocco
        /// </summary>
        /// <param name="record">Riga da registrare</param>
        /// <param name="work">Blocco della chiamata</param>
        /// <returns>Progressivo assegnato</returns>
        private int RecordCall(TranslationCallRecord record, BlockWork work)
        {
            lock (this._lock)
            {
                record.CallSeq = this._calls.Count > 0 ? this._calls[this._calls.Count - 1].CallSeq + 1 : 1;
                this._store.AppendCall(this._job.Id, record);
                this._calls.Add(record);
                work.CallSeqs.Add(record.CallSeq);
            }

            this.Changed?.Invoke();
            return record.CallSeq;
        }

        /// <summary>
        /// Avvisa dell'avanzamento coi blocchi fatti e totali di tutti i file
        /// </summary>
        /// <param name="fileName">File del blocco</param>
        /// <param name="number">Blocco del file</param>
        /// <param name="count">Blocchi del file</param>
        private void ReportProgress(string fileName, int number, int count)
        {
            int done = 0;
            int total = 0;
            lock (this._lock)
            {
                foreach (TranslationRunFile file in this._files)
                {
                    foreach (TranslationBlock block in file.State.Blocks)
                    {
                        if (block.Status == TranslationBlockStatus.Split)
                            continue;
                        total++;
                        if (block.Status == TranslationBlockStatus.Done)
                            done++;
                    }
                }
            }

            this.Progress?.Invoke(done, total, fileName, number, count);
        }

        /// <summary>
        /// Crea un blocco del piano da battute consecutive
        /// </summary>
        /// <param name="index">Identificativo del blocco nel file</param>
        /// <param name="cues">Battute del blocco</param>
        /// <param name="splitFrom">Blocco dimezzato da cui viene, null per un blocco del piano</param>
        /// <returns>Blocco da fare</returns>
        private static TranslationBlock CreateBlock(int index, IReadOnlyList<SubtitleCue> cues, int? splitFrom)
        {
            TranslationBlock block = new TranslationBlock();
            block.Index = index;
            block.FirstId = cues[0].Id.Value;
            block.LastId = cues[cues.Count - 1].Id.Value;
            block.CueCount = cues.Count;
            block.SplitFrom = splitFrom;
            return block;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// File in traduzione: prefisso calcolato una volta, validatore e battute per id
        /// </summary>
        private sealed class FileContext
        {
            /// <summary>
            /// Battute vive del documento per id
            /// </summary>
            private readonly Dictionary<int, SubtitleCue> _live;

            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="file">File del job</param>
            /// <param name="prefix">Messaggio di prefisso</param>
            /// <param name="validator">Validatore del file</param>
            public FileContext(TranslationRunFile file, string prefix, TranslationValidator validator)
            {
                this.File = file;
                this.Prefix = prefix;
                this.Validator = validator;
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
            /// File del job
            /// </summary>
            public TranslationRunFile File { get; }

            /// <summary>
            /// Messaggio di prefisso, uguale byte per byte in tutte le chiamate del file
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
        }

        /// <summary>
        /// Esiti di un blocco in memoria: si salvano solo a blocco finito, uno Stop li scarta
        /// </summary>
        private sealed class BlockWork
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="block">Blocco in corso</param>
            public BlockWork(TranslationBlock block)
            {
                this.Block = block;
                this.Results = new SortedDictionary<int, TranslatedCueState>();
                this.Accepted = new SortedDictionary<int, string>();
                this.NewTerms = new List<CollectedNewTerm>();
                this.CallSeqs = new List<int>();
                this.FirstReasons = new SortedSet<string>(StringComparer.Ordinal);
            }

            /// <summary>
            /// Lascia la battuta da rivedere senza testo
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
                state.Version = 0;
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

            /// <summary>
            /// Blocco in corso
            /// </summary>
            public TranslationBlock Block { get; }

            /// <summary>
            /// Esiti per id
            /// </summary>
            public SortedDictionary<int, TranslatedCueState> Results { get; }

            /// <summary>
            /// Testi accettati nel blocco, per le coppie della coda dei ritentativi
            /// </summary>
            public SortedDictionary<int, string> Accepted { get; }

            /// <summary>
            /// Termini nuovi segnalati nel blocco
            /// </summary>
            public List<CollectedNewTerm> NewTerms { get; }

            /// <summary>
            /// Chiamate del blocco
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
        }

        #endregion
    }

    /// <summary>
    /// File di un job in traduzione: stato su disco, documento preparato e nome per prefisso e log
    /// </summary>
    public sealed class TranslationRunFile
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="state">Stato del file nel job</param>
        /// <param name="document">Documento preparato, con l'hash del job</param>
        /// <param name="fileName">Nome del file sorgente</param>
        public TranslationRunFile(TranslationJobFileState state, SubtitleTranslationDocument document, string fileName)
        {
            this.State = state;
            this.Document = document;
            this.FileName = fileName;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Stato del file nel job
        /// </summary>
        public TranslationJobFileState State { get; }

        /// <summary>
        /// Documento preparato
        /// </summary>
        public SubtitleTranslationDocument Document { get; }

        /// <summary>
        /// Nome del file sorgente
        /// </summary>
        public string FileName { get; }

        #endregion
    }

    /// <summary>
    /// Stima della traduzione prima di «Avvia traduzione», in token per caratteri
    /// </summary>
    public sealed class TranslationEstimate
    {
        #region Proprietà

        /// <summary>
        /// File del job
        /// </summary>
        public int Files { get; set; }

        /// <summary>
        /// Battute da tradurre
        /// </summary>
        public int Cues { get; set; }

        /// <summary>
        /// Blocchi del piano
        /// </summary>
        public int Blocks { get; set; }

        /// <summary>
        /// Token in ingresso, cache compresa
        /// </summary>
        public long InputTokens { get; set; }

        /// <summary>
        /// Token in ingresso serviti dalla cache, null se l'accesso non la misura
        /// </summary>
        public long? CachedTokens { get; set; }

        /// <summary>
        /// Token in uscita
        /// </summary>
        public long OutputTokens { get; set; }

        /// <summary>
        /// Token in ingresso della revisione AI completa, null per la mirata che dipende dai segnali della traduzione
        /// </summary>
        public long? ReviewInputTokens { get; set; }

        #endregion
    }
}
