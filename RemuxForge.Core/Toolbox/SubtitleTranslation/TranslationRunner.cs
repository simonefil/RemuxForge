using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Fase di traduzione di un job: i file in parallelo fino al tetto, i blocchi di un file in sequenza. Per ogni blocco B, poi B′
    /// sulle sole battute respinte e B″ sulle metà dopo una risposta troncata; il blocco si salva solo intero. Stop scarta il blocco
    /// in corso, limite d'uso ed errori fermano i file fra un blocco e l'altro
    /// </summary>
    public sealed class TranslationRunner : TranslationPhaseRunner
    {
        #region Costanti

        /// <summary>
        /// Obiettivo minimo del blocco dopo i dimezzamenti
        /// </summary>
        public const int MIN_BLOCK_SIZE = 18;

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
        /// Instructions di B, B′ e B″, uguali per tutto il job
        /// </summary>
        private readonly string _instructions;

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
            : base(store, job, files, calls, stateLock)
        {
            this._instructions = TranslationRequestBuilder.CreateTranslationInstructions(job.Glossary.SourceLanguage, job.Glossary.TargetLanguage);
        }

        #endregion

        #region Metodi pubblici

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
        /// con le coppie prima del blocco e gli argomenti consegnati lunghi quanto il sorgente. La revisione completa si stima
        /// allo stesso modo, con una D1 e una D2 su tutte le battute di ogni blocco
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
            string diagnosis = TranslationRequestBuilder.CreateDiagnosisInstructions(glossary.SourceLanguage, glossary.TargetLanguage) + TranslationRequestBuilder.CreateDiagnosisTools().ToJsonString();
            string correction = TranslationRequestBuilder.CreateCorrectionInstructions(glossary.SourceLanguage, glossary.TargetLanguage) + tools;
            long cached = 0;
            long reviewInput = 0;
            long reviewCached = 0;
            long reviewOutput = 0;
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
                int diagnosisTokens = GlossaryBuilder.EstimateTokens(diagnosis + prefix);
                int correctionTokens = GlossaryBuilder.EstimateTokens(correction + prefix);
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
                    Dictionary<int, string> translated = new Dictionary<int, string>();
                    foreach (FrozenCue cue in requested)
                    {
                        outputChars += cue.Text.Length + ESTIMATE_ENTRY_CHARS;
                        translated[cue.Id] = cue.Text;
                    }
                    int outputTokens = (outputChars + GlossaryBuilder.CHARS_PER_TOKEN - 1) / GlossaryBuilder.CHARS_PER_TOKEN;

                    estimate.InputTokens += blockTokens;
                    estimate.OutputTokens += outputTokens;
                    if (b >= ESTIMATE_UNCACHED_BLOCKS)
                        cached += (long)(blockTokens * ESTIMATE_CACHED_SHARE);

                    // Revisione completa, al massimo: D1 sul blocco con le traduzioni e un suggerimento per battuta, D2 su tutte le
                    // battute del blocco; la cache come per la traduzione, con D1 e D2 su chiavi separate
                    int reviewTokens = diagnosisTokens + GlossaryBuilder.EstimateTokens(TranslationRequestBuilder.CreateDiagnosisTail(requested, translated, new Dictionary<int, List<string>>()))
                        + correctionTokens + GlossaryBuilder.EstimateTokens(TranslationRequestBuilder.CreateCorrectionTail(new TranslationReviewIssue[0], requested, translated, new string[0]));
                    reviewInput += reviewTokens;
                    reviewOutput += 2 * outputTokens;
                    if (b >= ESTIMATE_UNCACHED_BLOCKS)
                        reviewCached += (long)(reviewTokens * ESTIMATE_CACHED_SHARE);
                    start += blocks[b].Count;
                }
            }

            estimate.CachedTokens = cache ? cached : null;
            bool full = reviewMode == TranslationReviewMode.Full;
            estimate.ReviewInputTokens = full ? reviewInput : null;
            estimate.ReviewCachedTokens = full && cache ? reviewCached : null;
            estimate.ReviewOutputTokens = full ? reviewOutput : null;
            return estimate;
        }

        #endregion

        #region Metodi protected

        /// <inheritdoc />
        protected override bool HasWork(TranslationRunFile file)
        {
            return file.State.TranslationStatus != TranslationFileStatus.Completed && file.State.TranslationStatus != TranslationFileStatus.Blocked;
        }

        /// <inheritdoc />
        protected override bool IsFinished(TranslationRunFile file)
        {
            return file.State.TranslationStatus == TranslationFileStatus.Completed;
        }

        /// <summary>
        /// Traduce i blocchi da fare del file uno alla volta. Ogni blocco si salva intero; dopo una risposta troncata si divide in
        /// due metà, obiettivo e tetto del file si dimezzano e i blocchi che restano si ripianificano. Senza blocchi da fare il file
        /// si chiude anche dopo uno Stop o una fermata
        /// </summary>
        /// <param name="file">File del job</param>
        /// <param name="provider">Provider condiviso</param>
        /// <param name="cancellation">Annullamento di Stop</param>
        /// <returns>Task del file</returns>
        protected override async Task ProcessFileAsync(TranslationRunFile file, ITranslationModelProvider provider, CancellationToken cancellation)
        {
            lock (this._lock)
            {
                if (file.State.TranslationStatus == TranslationFileStatus.Pending)
                    file.State.TranslationStatus = TranslationFileStatus.InProgress;
            }
            this.RaiseChanged();

            FileContext context = this.CreateContext(file);
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

                // Coda: coppie accettate prima del primo id da consegnare, dallo stato salvato e dal blocco in corso
                DeliveryPlan plan = new DeliveryPlan
                {
                    RetryKind = TranslationCallKind.BRetry,
                    ReducedKind = TranslationCallKind.BReduced,
                    Model = this._job.Models.Translation,
                    ReasoningEffort = this._job.ReasoningEfforts.Translation,
                    Instructions = this._instructions,
                    CollectNewTerms = true,
                    Tail = (requested, rejected) =>
                    {
                        int firstId = requested[0].Id;
                        SortedDictionary<int, string> accepted = new SortedDictionary<int, string>();
                        lock (this._lock)
                        {
                            foreach (TranslatedCueState translation in state.Translations)
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

                        return TranslationRequestBuilder.CreateTranslationTail(pairs, requested, rejected);
                    }
                };

                TranslationCallKind kind = block.SplitFrom.HasValue ? TranslationCallKind.BReduced : TranslationCallKind.B;
                bool incomplete = await this.DeliverAsync(context, work, plan, ids, kind, 0, new string[0], true, provider, cancellation);
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

                    this.SaveFile(state);
                }

                if (halves != null && halves.Count == 2)
                {
                    this.RaiseLog(AppText.F("toolbox.subtitleTranslation.run.blockReduced", file.FileName, number, halves[0].Count, halves[1].Count));
                }
                else
                {
                    this.RaiseLog(AppText.F("toolbox.subtitleTranslation.run.blockSaved", file.FileName, number, count, work.FirstAccepted, work.FirstRejected,
                        work.FirstReasons.Count > 0 ? string.Join(", ", work.FirstReasons) : "-"));
                }

                this.ReportProgress(file.FileName, number, count);
                this.RaiseChanged();
            }

            lock (this._lock)
            {
                file.State.TranslationStatus = TranslationFileStatus.Completed;
                this.SaveFile(file.State);
            }
            this.RaiseLog(AppText.F("toolbox.subtitleTranslation.run.fileCompleted", file.FileName));
            this.RaiseChanged();
        }

        #endregion

        #region Metodi privati

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
        /// Token in ingresso della revisione AI completa, cache compresa; null per la mirata che dipende dai problemi trovati
        /// </summary>
        public long? ReviewInputTokens { get; set; }

        /// <summary>
        /// Token in ingresso della revisione AI completa serviti dalla cache, null per la mirata o se l'accesso non la misura
        /// </summary>
        public long? ReviewCachedTokens { get; set; }

        /// <summary>
        /// Token in uscita della revisione AI completa, null per la mirata
        /// </summary>
        public long? ReviewOutputTokens { get; set; }

        #endregion
    }
}
