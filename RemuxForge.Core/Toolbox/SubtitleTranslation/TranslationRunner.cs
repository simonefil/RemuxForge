using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Fase di traduzione di un job in due giri coi file in parallelo fino al tetto: prima le tratte condivise, ognuna nel file della
    /// sua prima occorrenza e copiata in tutte le altre, poi i blocchi di ogni file in sequenza, senza le battute delle tratte. Per
    /// ogni blocco o tratta B, poi B′ sulle sole battute respinte e B″ sulle metà dopo una risposta troncata; l'unità si salva solo
    /// intera. Stop scarta l'unità in corso, limite d'uso ed errori fermano i file fra un'unità e l'altra
    /// </summary>
    public sealed class TranslationRunner : TranslationPhaseRunner
    {
        #region Costanti

        /// <summary>
        /// Obiettivo minimo del blocco dopo i dimezzamenti
        /// </summary>
        public const int MIN_BLOCK_SIZE = 18;

        /// <summary>
        /// Parte dell'ingresso servita dalla cache nella stima, nelle richieste che la trovano
        /// </summary>
        private const double ESTIMATE_CACHED_SHARE = 0.8;

        /// <summary>
        /// Blocchi di un file prima che la stima conti la cache con la API key, che non prepara la parte fissa
        /// </summary>
        private const int ESTIMATE_UNCACHED_BLOCKS = 2;

        /// <summary>
        /// Caratteri attorno al testo di ogni battuta negli argomenti consegnati, per la stima dell'uscita
        /// </summary>
        private const int ESTIMATE_ENTRY_CHARS = 24;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Instructions di B, B′ e B″, uguali per tutto il job
        /// </summary>
        private readonly string _instructions;

        /// <summary>
        /// True nel primo giro, quello delle tratte condivise
        /// </summary>
        private bool _stretches;

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
        /// <param name="context">Campo serie/contesto del progetto, letto all'avvio della fase</param>
        public TranslationRunner(TranslationJobStore store, TranslationJob job, List<TranslationRunFile> files, List<TranslationCallRecord> calls, object stateLock, string context)
            : base(store, job, files, calls, stateLock, context)
        {
            this._instructions = TranslationRequestBuilder.CreateTranslationInstructions(job.Glossary.SourceLanguage, job.Glossary.TargetLanguage);
            this._stretches = false;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Stato iniziale di un file del job: nome e sorgente numerato congelati in ordine di tempo, col tipo di ogni battuta, e piano
        /// dei blocchi con obiettivo e tetto di partenza sulle battute fuori dalle tratte condivise
        /// </summary>
        /// <param name="file">File del progetto, con le impronte della preparazione</param>
        /// <param name="document">Documento preparato del file</param>
        /// <param name="shared">Id delle battute del file che stanno in una tratta condivisa</param>
        /// <returns>Stato del file da salvare</returns>
        public static TranslationJobFileState CreateFileState(TranslationProjectFile file, SubtitleTranslationDocument document, ICollection<int> shared)
        {
            TranslationJobFileState state = new TranslationJobFileState();
            state.FileId = file.Id;
            state.SourceFileName = Path.GetFileName(file.Path);
            state.SourceSha256 = file.SourceSha256;
            state.NumberedSourceSha256 = file.Preparation.NumberedSourceSha256;
            state.PreparerVersion = file.Preparation.PreparerVersion;
            state.Encoding = file.Encoding;
            List<SubtitleCue> timed = new List<SubtitleCue>(document.Cues);
            timed.Sort(SubtitleCue.CompareByTime);
            List<SubtitleCue> own = new List<SubtitleCue>();
            foreach (SubtitleCue cue in timed)
            {
                if (!cue.Id.HasValue)
                    continue;
                state.Cues.Add(new FrozenCue { Id = cue.Id.Value, Text = cue.CleanText, Kind = cue.Kind });
                if (!shared.Contains(cue.Id.Value))
                    own.Add(cue);
            }

            foreach (IReadOnlyList<SubtitleCue> planned in new SubtitleBlockPlanner(own).PlanBlocks(state.TargetBlockSize, state.BlockCap))
                state.Blocks.Add(CreateBlock(state.Blocks.Count + 1, planned, null));

            return state;
        }

        /// <summary>
        /// Stima della traduzione per caratteri, come il giro la farebbe: gli stati dei file preparati come all'avvio, con le battute
        /// delle tratte condivise fuori dai blocchi, e ogni tratta contata una volta sola nel file della prima occorrenza. Ogni
        /// consegna manda il prefisso intero, la coda con le battute prima di quelle chieste e argomenti lunghi quanto il sorgente.
        /// La revisione completa si stima con una D1 e una D2 su ogni blocco e su ogni tratta. La cache segue il provider: con
        /// l'account ChatGPT una sessione di più richieste paga una volta la parte fissa senza coda e poi la trova in cache dalla
        /// prima richiesta, D2 dalla seconda perché prepara la parte fissa solo lì; una sessione di una richiesta non la trova. Con
        /// la API key la cache vale dal terzo blocco del file
        /// </summary>
        /// <param name="glossary">Glossario approvato</param>
        /// <param name="context">Campo serie/contesto del progetto</param>
        /// <param name="files">Stati iniziali dei file del job, coi documenti e i nomi</param>
        /// <param name="stretches">Tratte condivise del job</param>
        /// <param name="reviewMode">Modalità della revisione AI</param>
        /// <param name="accessMode">Accesso del giro, che decide se il provider prepara la parte fissa in cache</param>
        /// <returns>Stima della traduzione e della revisione</returns>
        public static TranslationEstimate Estimate(Glossary glossary, string context, IReadOnlyList<TranslationRunFile> files, IReadOnlyList<SharedStretch> stretches, TranslationReviewMode reviewMode,
            AiConnectionMode accessMode)
        {
            bool primed = accessMode == AiConnectionMode.ChatGptAccount;
            TranslationEstimate estimate = new TranslationEstimate();
            string instructions = TranslationRequestBuilder.CreateTranslationInstructions(glossary.SourceLanguage, glossary.TargetLanguage);
            string tools = TranslationRequestBuilder.CreateTranslationTools().ToJsonString();
            string diagnosis = TranslationRequestBuilder.CreateDiagnosisInstructions(glossary.SourceLanguage, glossary.TargetLanguage) + TranslationRequestBuilder.CreateDiagnosisTools().ToJsonString();
            string correction = TranslationRequestBuilder.CreateCorrectionInstructions(glossary.SourceLanguage, glossary.TargetLanguage) + tools;
            long reviewInput = 0;
            long reviewCached = 0;
            long reviewOutput = 0;
            estimate.Files = files.Count;

            foreach (TranslationRunFile file in files)
            {
                TranslationJobFileState state = file.State;
                HashSet<int> copies = SharedStretch.MovingSignCopies(stretches, state.FileId);
                string prefix = TranslationRequestBuilder.CreateTranslationPrefix(context, glossary, file.FileName, state.Cues.FindAll(c => !copies.Contains(c.Id)));
                int prefixTokens = GlossaryBuilder.EstimateTokens(instructions + tools + prefix);
                int diagnosisTokens = GlossaryBuilder.EstimateTokens(diagnosis + prefix);
                int correctionTokens = GlossaryBuilder.EstimateTokens(correction + prefix);

                // Consegne del file: i blocchi senza le battute delle tratte, poi le tratte di cui il file ha la prima occorrenza
                Dictionary<int, SharedStretch> shared = SharedStretch.MapFile(stretches, state.FileId);
                List<List<FrozenCue>> units = new List<List<FrozenCue>>();
                foreach (TranslationBlock block in state.Blocks)
                    units.Add(state.GetSpan(block.FirstId, block.LastId).FindAll(c => !shared.ContainsKey(c.Id)));
                foreach (SharedStretch stretch in stretches)
                {
                    if (stretch.Occurrences[0].FileId == state.FileId)
                        units.Add(state.Cues.FindAll(c => stretch.Occurrences[0].Ids.Contains(c.Id)));
                }

                // Sessioni B e D1 di più richieste: la parte fissa senza coda prima della prima, D2 prima della seconda
                bool warm = primed && units.Count > 1;
                if (warm)
                {
                    estimate.InputTokens += prefixTokens;
                    reviewInput += diagnosisTokens + correctionTokens;
                }

                for (int u = 0; u < units.Count; u++)
                {
                    // Coppie della coda: le battute prima della consegna con una traduzione lunga quanto il sorgente
                    List<FrozenCue> requested = units[u];
                    int start = state.Cues.FindIndex(c => c.Id == requested[0].Id);
                    List<KeyValuePair<FrozenCue, string>> pairs = new List<KeyValuePair<FrozenCue, string>>();
                    for (int i = Math.Max(0, start - TranslationRequestBuilder.TAIL_PAIRS); i < start; i++)
                        pairs.Add(new KeyValuePair<FrozenCue, string>(state.Cues[i], state.Cues[i].Text));
                    int unitTokens = prefixTokens + GlossaryBuilder.EstimateTokens(TranslationRequestBuilder.CreateTranslationTail(pairs, requested, new string[0]));

                    int outputChars = 0;
                    Dictionary<int, string> translated = new Dictionary<int, string>();
                    foreach (FrozenCue cue in requested)
                    {
                        outputChars += cue.Text.Length + ESTIMATE_ENTRY_CHARS;
                        translated[cue.Id] = cue.Text;
                    }
                    int outputTokens = (outputChars + GlossaryBuilder.CHARS_PER_TOKEN - 1) / GlossaryBuilder.CHARS_PER_TOKEN;

                    estimate.Cues += requested.Count;
                    estimate.Blocks++;
                    estimate.InputTokens += unitTokens;
                    estimate.OutputTokens += outputTokens;
                    bool cached = warm || (!primed && u >= ESTIMATE_UNCACHED_BLOCKS);
                    if (cached)
                        estimate.CachedTokens += (long)(unitTokens * ESTIMATE_CACHED_SHARE);

                    // Revisione completa, al massimo: D1 sulla consegna con le traduzioni e un suggerimento per battuta, D2 su tutte le sue
                    // battute, su chiavi separate
                    int diagnosisUnit = diagnosisTokens + GlossaryBuilder.EstimateTokens(TranslationRequestBuilder.CreateDiagnosisTail(requested, translated, new Dictionary<int, List<string>>()));
                    int correctionUnit = correctionTokens + GlossaryBuilder.EstimateTokens(TranslationRequestBuilder.CreateCorrectionTail(new TranslationReviewIssue[0], requested, translated, new string[0]));
                    reviewInput += diagnosisUnit + correctionUnit;
                    reviewOutput += 2 * outputTokens;
                    if (cached)
                        reviewCached += (long)(diagnosisUnit * ESTIMATE_CACHED_SHARE);
                    if (primed ? warm && u >= 1 : cached)
                        reviewCached += (long)(correctionUnit * ESTIMATE_CACHED_SHARE);
                }
            }

            bool full = reviewMode == TranslationReviewMode.Full;
            estimate.ReviewInputTokens = full ? reviewInput : null;
            estimate.ReviewCachedTokens = full ? reviewCached : null;
            estimate.ReviewOutputTokens = full ? reviewOutput : null;
            return estimate;
        }

        /// <summary>
        /// Traduce prima le tratte condivise da fare, poi, senza fermate, i blocchi dei file: le copie delle tratte ci sono già
        /// quando un file chiede la coda delle traduzioni accettate
        /// </summary>
        /// <param name="provider">Provider condiviso da tutti i file, con la pausa comune dei 429</param>
        /// <param name="maxParallel">File in parallelo</param>
        /// <param name="cancellation">Annullamento di Stop</param>
        /// <returns>Fermata, null se nessun file si è fermato</returns>
        public override async Task<TranslationStop> RunAsync(ITranslationModelProvider provider, int maxParallel, CancellationToken cancellation)
        {
            this._stretches = true;
            TranslationStop stop = await base.RunAsync(provider, maxParallel, cancellation);
            if (stop != null)
                return stop;

            this._stretches = false;
            return await base.RunAsync(provider, maxParallel, cancellation);
        }

        #endregion

        #region Metodi protected

        /// <inheritdoc />
        protected override bool HasWork(TranslationRunFile file)
        {
            if (this._stretches)
            {
                lock (this._lock)
                {
                    return this._job.SharedStretches.Exists(s => s.Status == TranslationBlockStatus.Pending && s.Occurrences[0].FileId == file.State.FileId);
                }
            }

            return file.State.TranslationStatus != TranslationFileStatus.Completed && file.State.TranslationStatus != TranslationFileStatus.Blocked;
        }

        /// <inheritdoc />
        protected override bool IsFinished(TranslationRunFile file)
        {
            return this._stretches ? !this.HasWork(file) : file.State.TranslationStatus == TranslationFileStatus.Completed;
        }

        /// <summary>
        /// Nel primo giro traduce una alla volta le tratte di cui il file ha la prima occorrenza: ognuna in una consegna sola, con
        /// prefisso e validatore del file, che si dimezza da sé dopo una risposta troncata; a tratta finita la traduzione si copia
        /// in tutte le occorrenze. Nel secondo giro rimette le copie delle tratte tradotte e traduce i blocchi da fare del file uno
        /// alla volta. Ogni blocco si salva intero; dopo una risposta troncata dal tetto dei token in uscita si divide in due metà,
        /// obiettivo e tetto del file si dimezzano e i blocchi che restano si ripianificano; gli altri motivi d'incompletezza lasciano
        /// le battute da rivedere e non toccano il piano del file. Senza blocchi da fare il file si chiude
        /// anche dopo uno Stop o una fermata, se nessuna sua tratta resta da tradurre
        /// </summary>
        /// <param name="file">File del job</param>
        /// <param name="provider">Provider condiviso</param>
        /// <param name="cancellation">Annullamento di Stop</param>
        /// <returns>Task del file</returns>
        protected override async Task ProcessFileAsync(TranslationRunFile file, ITranslationModelProvider provider, CancellationToken cancellation)
        {
            FileContext context = this.CreateContext(file);
            while (this._stretches)
            {
                SharedStretch stretch;
                int number;
                int count;
                lock (this._lock)
                {
                    List<SharedStretch> owned = this._job.SharedStretches.FindAll(s => s.Occurrences[0].FileId == file.State.FileId);
                    stretch = owned.Find(s => s.Status == TranslationBlockStatus.Pending);
                    number = owned.IndexOf(stretch) + 1;
                    count = owned.Count;
                }
                if (stretch == null || this.IsPaused)
                    return;

                cancellation.ThrowIfCancellationRequested();
                this.ReportProgress(file.FileName, number, count);
                DeliveryWork stretchWork = new DeliveryWork(null);
                List<int> lines = new List<int>(stretch.Occurrences[0].Ids);
                await this.DeliverAsync(context, stretchWork, this.CreatePlan(context, stretchWork), lines, TranslationCallKind.B, 0, new string[0], false, provider, cancellation);

                int files;
                int accepted = 0;
                lock (this._lock)
                {
                    // Esiti con i segnali come per un blocco, nella tratta riga per riga e poi in ogni occorrenza dei file del giro
                    stretch.Translations = new List<TranslatedCueState>();
                    foreach (int id in lines)
                    {
                        TranslatedCueState translation = stretchWork.Get(id);
                        if (translation.Rejections.Count > 0)
                            translation.Signals.Add(TranslationCueSignal.Retried);
                        if (translation.GlossaryWarnings.Count > 0)
                            translation.Signals.Add(TranslationCueSignal.Glossary);
                        if (translation.Status == TranslatedCueStatus.Accepted)
                            accepted++;
                        stretch.Translations.Add(translation.Clone(stretch.Translations.Count));
                    }
                    stretch.Status = TranslationBlockStatus.Done;
                    stretch.CallSeqs.AddRange(stretchWork.CallSeqs);

                    // Prima il file della tratta, poi la tratta nel job, poi le copie negli altri file: dopo una chiusura brusca la tratta
                    // non salvata si rifà, e il secondo giro rimette le copie
                    stretch.CopyInto(file.State);
                    this._store.SaveFile(this._job.Id, file.State);
                    this._store.Save(this._job);
                    HashSet<string> occurring = new HashSet<string>();
                    foreach (SharedStretchOccurrence occurrence in stretch.Occurrences)
                        occurring.Add(occurrence.FileId);
                    foreach (TranslationRunFile other in this._files)
                    {
                        if (other == file || !occurring.Contains(other.State.FileId))
                            continue;
                        stretch.CopyInto(other.State);
                        this._store.SaveFile(this._job.Id, other.State);
                    }
                    files = occurring.Count;
                }

                this.RaiseLog(AppText.F("toolbox.subtitleTranslation.run.stretchSaved", file.FileName, number, count, lines.Count, files, accepted, lines.Count - accepted));
                this.ReportProgress(file.FileName, number, count);
                this.RaiseChanged();
            }

            // Copie delle tratte già tradotte, anche per un file che era bloccato quando la tratta si è salvata
            lock (this._lock)
            {
                if (file.State.TranslationStatus == TranslationFileStatus.Pending)
                    file.State.TranslationStatus = TranslationFileStatus.InProgress;
                foreach (SharedStretch stretch in this._job.SharedStretches)
                {
                    if (stretch.Status == TranslationBlockStatus.Done)
                        stretch.CopyInto(file.State);
                }
            }
            this.RaiseChanged();

            while (true)
            {
                TranslationBlock block;
                lock (this._lock)
                {
                    block = file.State.Blocks.Find(b => b.Status == TranslationBlockStatus.Pending);
                }
                if (block != null && this.IsPaused)
                    return;

                if (block == null)
                    break;

                cancellation.ThrowIfCancellationRequested();
                TranslationJobFileState state = file.State;
                DeliveryWork work = new DeliveryWork(block.Index);
                List<int> ids = new List<int>();
                foreach (FrozenCue cue in state.GetSpan(block.FirstId, block.LastId))
                {
                    if (!context.Shared.ContainsKey(cue.Id))
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
                bool incomplete = await this.DeliverAsync(context, work, this.CreatePlan(context, work), ids, kind, 0, new string[0], true, provider, cancellation);
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
                        List<int> after = new List<int>();
                        int last = state.Cues.FindIndex(c => c.Id == second.LastId);
                        foreach (FrozenCue cue in state.Cues.GetRange(last + 1, state.Cues.Count - last - 1))
                        {
                            if (!context.Shared.ContainsKey(cue.Id))
                                after.Add(cue.Id);
                        }
                        List<SubtitleCue> remaining = context.GetLiveCues(after);

                        next += 2;
                        foreach (IReadOnlyList<SubtitleCue> planned in new SubtitleBlockPlanner(remaining).PlanBlocks(state.TargetBlockSize, state.BlockCap))
                        {
                            next++;
                            state.Blocks.Add(CreateBlock(next, planned, null));
                        }
                    }
                    else
                    {
                        // Una battuta sola troncata non si divide più: resta da rivedere senza testo
                        if (incomplete)
                        {
                            foreach (int id in ids)
                                work.MarkToReview(id, REASON_INCOMPLETE);
                        }

                        // Esiti del blocco nello stato del file
                        foreach (TranslatedCueState translation in work.Results.Values)
                        {
                            state.Translations.RemoveAll(t => t.Id == translation.Id);
                            if (translation.Rejections.Count > 0)
                                translation.Signals.Add(TranslationCueSignal.Retried);
                            if (translation.GlossaryWarnings.Count > 0)
                                translation.Signals.Add(TranslationCueSignal.Glossary);
                            state.Translations.Add(translation);
                        }

                        state.Translations.Sort((a, b) => a.Id.CompareTo(b.Id));
                        block.Status = TranslationBlockStatus.Done;
                    }

                    this.SaveFile(state);
                }

                if (halves != null && halves.Count == 2)
                {
                    this.RaiseLog(AppText.F("toolbox.subtitleTranslation.run.blockReduced", file.FileName, number, halves[0].Count, halves[1].Count));
                }
                else
                {
                    List<string> reasons = new List<string>();
                    foreach (string code in work.FirstReasons)
                        reasons.Add(AppText.T(TranslationValidationIssue.REASON_KEY_PREFIX + code));
                    this.RaiseLog(AppText.F(work.FirstRejected > 0 ? "toolbox.subtitleTranslation.run.blockSavedRejected" : "toolbox.subtitleTranslation.run.blockSaved",
                        file.FileName, number, count, work.FirstAccepted, work.FirstRejected, string.Join(", ", reasons)));
                }

                this.ReportProgress(file.FileName, number, count);
                this.RaiseChanged();
            }

            lock (this._lock)
            {
                // Una tratta che il file della prima occorrenza, bloccato, non ha potuto tradurre tiene il file aperto
                if (this._job.SharedStretches.Exists(s => s.Status == TranslationBlockStatus.Pending && s.Occurrences.Exists(o => o.FileId == file.State.FileId)))
                {
                    this.SaveFile(file.State);
                    return;
                }

                file.State.TranslationStatus = TranslationFileStatus.Completed;
                this.SaveFile(file.State);
            }
            this.RaiseLog(AppText.F("toolbox.subtitleTranslation.run.fileCompleted", file.FileName));
            this.RaiseChanged();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Piano delle consegne di un blocco o di una tratta del file: la coda porta le coppie accettate che vengono prima della
        /// prima battuta da consegnare nell'ordine delle battute congelate, dallo stato salvato, copie delle tratte comprese, e
        /// dall'unità in corso. Le copie di una tratta del file stesso, come i fotogrammi di un cartello in movimento, restano fuori
        /// </summary>
        /// <param name="context">File con prefisso e battute</param>
        /// <param name="work">Esiti dell'unità in memoria</param>
        /// <returns>Piano delle consegne</returns>
        private DeliveryPlan CreatePlan(FileContext context, DeliveryWork work)
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
                Instructions = this._instructions,
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
        /// Avvisa dell'avanzamento coi blocchi fatti e totali di tutti i file e con le tratte condivise
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
                foreach (SharedStretch stretch in this._job.SharedStretches)
                {
                    total++;
                    if (stretch.Status == TranslationBlockStatus.Done)
                        done++;
                }
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

            this.RaiseProgress(done, total, fileName, number, count);
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

        #region Proprietà

        /// <summary>
        /// True nel primo giro, quello delle tratte condivise: l'avanzamento conta tratte, non blocchi
        /// </summary>
        public bool SharedStretchPass
        {
            get { return this._stretches; }
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
        /// Battute da tradurre, quelle delle tratte condivise una volta sola
        /// </summary>
        public int Cues { get; set; }

        /// <summary>
        /// Consegne del piano: blocchi e tratte condivise
        /// </summary>
        public int Blocks { get; set; }

        /// <summary>
        /// Token in ingresso, cache compresa
        /// </summary>
        public long InputTokens { get; set; }

        /// <summary>
        /// Token in ingresso serviti dalla cache
        /// </summary>
        public long CachedTokens { get; set; }

        /// <summary>
        /// Token in uscita
        /// </summary>
        public long OutputTokens { get; set; }

        /// <summary>
        /// Token in ingresso della revisione AI completa, cache compresa; null senza revisione
        /// </summary>
        public long? ReviewInputTokens { get; set; }

        /// <summary>
        /// Token in ingresso della revisione AI completa serviti dalla cache, null senza revisione
        /// </summary>
        public long? ReviewCachedTokens { get; set; }

        /// <summary>
        /// Token in uscita della revisione AI completa, null senza revisione
        /// </summary>
        public long? ReviewOutputTokens { get; set; }

        #endregion
    }
}
