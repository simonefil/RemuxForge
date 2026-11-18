using RemuxForge.Core.Ai;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Revisione AI di un job: per ogni blocco scelto una diagnosi D1 con un ritentativo, poi D2 sulle battute diagnosticate e
    /// su quelle da rivedere, coi gruppi interi. Una diagnosi troncata dal tetto dei token in uscita a entrambe le chiamate si
    /// dimezza come una consegna. La diagnosi valida si salva e alla ripresa il blocco riparte da D2; una diagnosi non valida
    /// anche al ritentativo lascia il blocco non rivisto e ferma la fase, e la ripresa la rifà. I blocchi di un file
    /// vanno in sequenza. Una tratta condivisa si rivede una volta sola, in un blocco suo nel file della prima occorrenza, e le
    /// correzioni passano a tutte le copie; i blocchi del piano saltano le battute delle tratte
    /// </summary>
    public sealed class TranslationReviewer : TranslationPhaseRunner
    {
        #region Costanti

        /// <summary>
        /// Chiamate D1 di un blocco o di una sua metà prima di dimezzarlo o lasciarlo non rivisto
        /// </summary>
        private const int DIAGNOSIS_ATTEMPTS = 2;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Instructions di D1
        /// </summary>
        private readonly string _diagnosisInstructions;

        /// <summary>
        /// Instructions di D2
        /// </summary>
        private readonly string _correctionInstructions;

        /// <summary>
        /// Termini aggiunti al passo dei termini nuovi, con l'espressione del termine per il segnale della riscrittura
        /// </summary>
        private readonly List<KeyValuePair<GlossaryTerm, Regex>> _updated;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore: sceglie i blocchi dei file che non li hanno ancora, nella modalità del job
        /// </summary>
        /// <param name="store">Job su disco</param>
        /// <param name="job">Testata del job, col glossario corrente</param>
        /// <param name="files">File del job, nell'ordine del progetto</param>
        /// <param name="calls">Registro delle chiamate del job</param>
        /// <param name="stateLock">Lock dello stato condiviso</param>
        /// <param name="context">Campo serie/contesto del progetto, letto all'avvio della fase</param>
        public TranslationReviewer(TranslationJobStore store, TranslationJob job, List<TranslationRunFile> files, List<TranslationCallRecord> calls, object stateLock, string context)
            : base(store, job, files, calls, stateLock, context)
        {
            this._diagnosisInstructions = TranslationRequestBuilder.CreateDiagnosisInstructions(job.Glossary.SourceLanguage, job.Glossary.TargetLanguage);
            this._correctionInstructions = TranslationRequestBuilder.CreateCorrectionInstructions(job.Glossary.SourceLanguage, job.Glossary.TargetLanguage);
            this._updated = new List<KeyValuePair<GlossaryTerm, Regex>>();
            string language = LanguageValidator.NormalizeToIso6392(job.Glossary.SourceLanguage);
            foreach (GlossaryTerm term in job.Glossary.Terms)
            {
                string word = TranslationValidator.StripArticle(term.Source, language);
                if (term.Origin == GlossaryOrigin.NewTerm && !string.IsNullOrEmpty(word))
                    this._updated.Add(new KeyValuePair<GlossaryTerm, Regex>(term, TranslationValidator.WordPattern(word, term.ExactCase == true)));
            }

            lock (stateLock)
            {
                foreach (TranslationRunFile file in files)
                {
                    TranslationJobFileState state = file.State;
                    if (state.ReviewBlocks != null)
                        continue;

                    // Blocchi del piano finale, dimezzamenti compresi, senza le battute delle tratte; poi un blocco per ogni tratta di
                    // cui il file ha la prima occorrenza. Nella mirata solo quelli con almeno una battuta segnalata
                    Dictionary<int, SharedStretch> shared = SharedStretch.MapFile(job.SharedStretches, state.FileId);
                    Predicate<TranslatedCueState> signalled = t => t.Status == TranslatedCueStatus.ToReview || t.Signals.Contains(TranslationCueSignal.Retried)
                        || t.Signals.Contains(TranslationCueSignal.Glossary) || t.Signals.Contains(TranslationCueSignal.NewTerm) || t.Signals.Contains(TranslationCueSignal.RewrittenByTerms)
                        || t.Signals.Contains(TranslationCueSignal.Echo) || t.Signals.Contains(TranslationCueSignal.Slip);
                    state.ReviewBlocks = new List<TranslationReviewBlock>();
                    foreach (TranslationBlock block in state.Blocks)
                    {
                        if (block.Status == TranslationBlockStatus.Split)
                            continue;
                        HashSet<int> span = new HashSet<int>(state.GetSpan(block.FirstId, block.LastId).ConvertAll(c => c.Id));
                        if (job.ReviewMode == TranslationReviewMode.Full || state.Translations.Exists(t => span.Contains(t.Id) && !shared.ContainsKey(t.Id) && signalled(t)))
                            state.ReviewBlocks.Add(new TranslationReviewBlock { BlockIndex = block.Index, FirstId = block.FirstId, LastId = block.LastId });
                    }
                    foreach (SharedStretch stretch in job.SharedStretches)
                    {
                        List<int> lines = stretch.Occurrences[0].Ids;
                        if (stretch.Occurrences[0].FileId == state.FileId
                            && (job.ReviewMode == TranslationReviewMode.Full || state.Translations.Exists(t => lines.Contains(t.Id) && signalled(t))))
                            state.ReviewBlocks.Add(new TranslationReviewBlock { SharedStretch = stretch.Index, FirstId = lines[0], LastId = lines[lines.Count - 1] });
                    }
                }
            }
        }

        #endregion

        #region Metodi protected

        /// <inheritdoc />
        protected override bool HasWork(TranslationRunFile file)
        {
            return file.State.ReviewBlocks.Exists(b => b.Status != TranslationReviewBlockStatus.Done);
        }

        /// <inheritdoc />
        protected override bool IsFinished(TranslationRunFile file)
        {
            return !this.HasWork(file);
        }

        /// <summary>
        /// Rivede i blocchi da fare del file uno alla volta; ogni blocco si salva dopo D1 e dopo D2. Un blocco senza diagnosi
        /// valida resta non rivisto e ferma il file con l'errore del servizio
        /// </summary>
        /// <param name="file">File del job</param>
        /// <param name="provider">Provider condiviso</param>
        /// <param name="cancellation">Annullamento di Stop</param>
        /// <returns>Task del file</returns>
        protected override async Task ProcessFileAsync(TranslationRunFile file, ITranslationModelProvider provider, CancellationToken cancellation)
        {
            TranslationJobFileState state = file.State;
            FileContext context = this.CreateContext(file);
            while (true)
            {
                TranslationReviewBlock block;
                int number;
                int count;
                lock (this._lock)
                {
                    block = state.ReviewBlocks.Find(b => b.Status != TranslationReviewBlockStatus.Done);
                    number = state.ReviewBlocks.IndexOf(block) + 1;
                    count = state.ReviewBlocks.Count;
                }
                if (block == null)
                    break;
                if (this.IsPaused)
                    return;

                cancellation.ThrowIfCancellationRequested();
                this.ReportProgress(file.FileName, number, count);

                // Un blocco del piano salta le battute delle tratte, quello di una tratta ha solo quelle della sua prima occorrenza
                int? unit = block.SharedStretch.HasValue ? null : block.BlockIndex;
                List<FrozenCue> cues = block.SharedStretch.HasValue
                    ? state.Cues.FindAll(c => context.Shared.TryGetValue(c.Id, out SharedStretch stretch) && stretch.Index == block.SharedStretch && stretch.IsMaster(state.FileId, c.Id))
                    : state.GetSpan(block.FirstId, block.LastId).FindAll(c => !context.Shared.ContainsKey(c.Id));
                List<int> ids = cues.ConvertAll(c => c.Id);
                HashSet<int> inBlock = new HashSet<int>(ids);
                Dictionary<int, string> current = new Dictionary<int, string>();
                Dictionary<int, TranslatedCueState> translations = new Dictionary<int, TranslatedCueState>();
                lock (this._lock)
                {
                    foreach (TranslatedCueState translation in state.Translations)
                    {
                        if (!inBlock.Contains(translation.Id))
                            continue;
                        translations[translation.Id] = translation;
                        if (translation.Text != null)
                            current[translation.Id] = translation.Text;
                    }
                }

                List<TranslationReviewIssue> issues = null;
                if (block.Status == TranslationReviewBlockStatus.Diagnosed)
                {
                    issues = block.Issues;
                }
                else
                {
                    // D1 con un ritentativo, anche per un blocco rimasto non rivisto da un giro precedente; troncata a entrambe le
                    // chiamate si dimezza fino alla battuta o al gruppo, e i problemi delle parti si sommano
                    Dictionary<int, List<string>> signals = new Dictionary<int, List<string>>();
                    foreach (TranslatedCueState translation in translations.Values)
                    {
                        // Un segnale per riga, nella forma CODICE: dettaglio
                        List<string> lines = new List<string>();
                        foreach (CueRejection rejection in translation.Rejections)
                        {
                            if (translation.Status == TranslatedCueStatus.Accepted)
                                lines.Add("RETRIED: attempt " + rejection.Attempt.ToString(CultureInfo.InvariantCulture) + " rejected with " + string.Join(", ", TranslationValidationIssue.ReadCodes(rejection.Reasons)));
                        }
                        if (translation.Status == TranslatedCueStatus.ToReview)
                        {
                            List<string> codes = translation.Rejections.Count > 0 ? TranslationValidationIssue.ReadCodes(translation.Rejections[translation.Rejections.Count - 1].Reasons) : new List<string>();
                            lines.Add("TO_REVIEW: " + (codes.Count > 0 ? string.Join(", ", codes) : translation.ToReviewReason ?? ""));
                        }
                        foreach (GlossaryWarning warning in translation.GlossaryWarnings)
                            lines.Add("GLOSSARY: source has \"" + warning.Source + "\", translation lacks \"" + warning.Target + "\"");
                        foreach (CollectedNewTerm term in state.CollectedNewTerms)
                        {
                            if (term.CueIds.Contains(translation.Id))
                                lines.Add("NEW_TERM: \"" + term.Source + "\" rendered as \"" + term.Target + "\"");
                        }
                        if (translation.Signals.Contains(TranslationCueSignal.RewrittenByTerms))
                        {
                            string source = TranslationValidator.GlossaryPlain(context.Frozen[translation.Id].Text);
                            foreach (KeyValuePair<GlossaryTerm, Regex> term in this._updated)
                            {
                                if (term.Value.IsMatch(source))
                                    lines.Add("REWRITTEN: updated term \"" + term.Key.Source + "\" => \"" + term.Key.Target + "\"");
                            }
                        }

                        if (lines.Count > 0)
                            signals[translation.Id] = lines;
                    }

                    DeliveryWork diagnosis = new DeliveryWork(unit);
                    List<List<int>> parts = new List<List<int>> { ids };
                    List<TranslationReviewIssue> partsIssues = new List<TranslationReviewIssue>();
                    bool diagnosed = true;
                    while (parts.Count > 0)
                    {
                        List<int> partIds = parts[0];
                        parts.RemoveAt(0);
                        List<FrozenCue> partCues = partIds.ConvertAll(id => context.Frozen[id]);
                        List<TranslationReviewIssue> partIssues = null;
                        bool truncated = true;
                        for (int attempt = 0; attempt < DIAGNOSIS_ATTEMPTS && partIssues == null; attempt++)
                        {
                            TranslationModelRequest request = this.CreateRequest(TranslationCallKind.D1, state.FileId, this._job.Models.Review, this._job.ReasoningEfforts.Review,
                                this._diagnosisInstructions, context.Prefix, TranslationRequestBuilder.CreateDiagnosisTail(partCues, current, signals), TranslationRequestBuilder.CreateDiagnosisTools());
                            lock (this._lock)
                                request.PrefixReused = state.ReviewBlocks.FindAll(b => b.Status == TranslationReviewBlockStatus.Pending).Count > 1;
                            KeyValuePair<AiResponseResult, TranslationCallRecord> sent = await this.SendAsync(request, state.FileId, diagnosis, partIds, attempt > 0 ? attempt : null, provider, cancellation);
                            AiResponseResult response = sent.Key;
                            TranslationCallRecord record = sent.Value;
                            record.Usage = new TranslationCallUsage(response.Usage, 0);
                            if (response.Status == AiResponseStatus.Incomplete)
                            {
                                record.ResponseStatus = TranslationCallResponseStatus.Incomplete;
                                record.IncompleteReason = response.IncompleteReason;
                                record.Outcome = TranslationCallOutcome.PartiallyRejected;
                                truncated &= response.IncompleteReason == INCOMPLETE_MAX_OUTPUT_TOKENS;
                            }
                            else
                            {
                                truncated = false;
                                JsonArray entries = null;
                                foreach (JsonObject item in response.Items)
                                {
                                    if (AiOAuthFlow.GetString(item, "type") != "function_call" || AiOAuthFlow.GetString(item, "name") != TranslationRequestBuilder.SUBMIT_DIAGNOSIS_TOOL)
                                        continue;
                                    try
                                    {
                                        entries = (JsonNode.Parse(AiOAuthFlow.GetString(item, "arguments") ?? "") as JsonObject)?["issues"] as JsonArray;
                                    }
                                    catch (JsonException)
                                    {
                                        entries = null;
                                    }
                                    break;
                                }

                                if (entries == null)
                                {
                                    record.Outcome = TranslationCallOutcome.PartiallyRejected;
                                    record.Rejections.Add(new TranslationCallRejection { Id = null, Reasons = new List<string> { TranslationValidator.ARGS_INVALID + ": no submit_diagnosis call with an issues array." } });
                                }
                                else
                                {
                                    // Problemi fuori dal blocco, fuori enum o senza spiegazione si scartano
                                    partIssues = new List<TranslationReviewIssue>();
                                    foreach (JsonNode node in entries)
                                    {
                                        JsonObject entry = node as JsonObject;
                                        int id;
                                        if (entry == null || !(entry["id"] is JsonValue idValue) || !idValue.TryGetValue(out id) || !partIds.Contains(id))
                                            continue;
                                        string category = AiOAuthFlow.GetString(entry, "category");
                                        string severity = AiOAuthFlow.GetString(entry, "severity");
                                        string explanation = (AiOAuthFlow.GetString(entry, "explanation") ?? "").Trim();
                                        if (!((IList<string>)TranslationRequestBuilder.DIAGNOSIS_CATEGORIES).Contains(category) || !((IList<string>)TranslationRequestBuilder.DIAGNOSIS_SEVERITIES).Contains(severity)
                                            || string.IsNullOrEmpty(explanation))
                                            continue;
                                        string suggestion = AiOAuthFlow.GetString(entry, "suggestion");
                                        partIssues.Add(new TranslationReviewIssue { Id = id, Category = category, Severity = severity, Explanation = explanation, Suggestion = string.IsNullOrEmpty(suggestion) ? null : suggestion });
                                    }
                                    record.Outcome = TranslationCallOutcome.Accepted;
                                    record.Issues = partIssues.Count;
                                }
                            }
                            this.RecordCall(record, diagnosis);
                        }

                        if (partIssues != null)
                        {
                            partsIssues.AddRange(partIssues);
                            continue;
                        }

                        List<List<int>> halves = new List<List<int>>();
                        if (truncated)
                        {
                            List<SubtitleCue> live = context.GetLiveCues(partIds);
                            foreach (IReadOnlyList<SubtitleCue> half in new SubtitleBlockPlanner(live).SplitBlock(live))
                            {
                                List<int> halfIds = new List<int>();
                                foreach (SubtitleCue cue in half)
                                    halfIds.Add(cue.Id.Value);
                                halves.Add(halfIds);
                            }
                        }
                        if (halves.Count < 2)
                        {
                            diagnosed = false;
                            break;
                        }

                        this.RaiseLog(AppText.F("toolbox.subtitleTranslation.run.retryReduced", file.FileName, halves[0].Count, halves[1].Count));
                        parts.InsertRange(0, halves);
                    }
                    if (diagnosed)
                        issues = partsIssues;

                    // Diagnosi valida salvata: uno Stop durante D2 non la fa ripagare. Senza, il blocco resta non rivisto con le sue
                    // chiamate e il file si ferma: la ripresa rifà D1
                    lock (this._lock)
                    {
                        block.Issues = issues ?? new List<TranslationReviewIssue>();
                        block.Status = issues != null ? TranslationReviewBlockStatus.Diagnosed : TranslationReviewBlockStatus.Failed;
                        block.CallSeqs.AddRange(diagnosis.CallSeqs);
                        this.SaveFile(state);
                    }
                    if (issues == null)
                        throw new AiServiceException(AiErrorKind.InvalidResponse, AppText.F("toolbox.subtitleTranslation.review.diagnosisFailed", file.FileName, number, count));
                }

                // D2: diagnosticate e da rivedere, coi gruppi interi
                HashSet<int> wanted = new HashSet<int>();
                foreach (TranslationReviewIssue issue in issues)
                    wanted.Add(issue.Id);
                foreach (TranslatedCueState translation in translations.Values)
                {
                    if (translation.Status == TranslatedCueStatus.ToReview)
                        wanted.Add(translation.Id);
                }
                List<int> deliver = ExpandToGroups(state, wanted);

                int corrected = 0;
                DeliveryWork correction = new DeliveryWork(unit);
                if (deliver.Count > 0)
                {
                    DeliveryPlan plan = new DeliveryPlan
                    {
                        RetryKind = TranslationCallKind.D2,
                        ReducedKind = TranslationCallKind.D2,
                        Model = this._job.Models.Review,
                        ReasoningEffort = this._job.ReasoningEfforts.Review,
                        Instructions = this._correctionInstructions,
                        CollectNewTerms = false,
                        Current = current,
                        Tail = (requested, rejected) =>
                        {
                            HashSet<int> requestedIds = new HashSet<int>();
                            foreach (FrozenCue cue in requested)
                                requestedIds.Add(cue.Id);
                            List<TranslationReviewIssue> shown = issues.FindAll(i => requestedIds.Contains(i.Id));
                            return TranslationRequestBuilder.CreateCorrectionTail(shown, requested, current, rejected);
                        }
                    };
                    await this.DeliverAsync(context, correction, plan, deliver, TranslationCallKind.D2, 0, new string[0], false, provider, cancellation);
                }

                lock (this._lock)
                {
                    HashSet<TranslationJobFileState> touched = new HashSet<TranslationJobFileState>();
                    foreach (TranslatedCueState result in correction.Results.Values)
                    {
                        TranslatedCueState translation;
                        if (result.Status != TranslatedCueStatus.Accepted || !translations.TryGetValue(result.Id, out translation)
                            || string.Equals(result.Text, translation.Text, StringComparison.Ordinal))
                            continue;

                        // Testo di prima e motivo per l'avviso della griglia
                        List<string> reasons = issues.FindAll(i => i.Id == result.Id).ConvertAll(i => i.Explanation);
                        translation.AiCorrection = new CueAiCorrection { Before = translation.Text, Explanation = reasons.Count > 0 ? string.Join("\n", reasons) : null };
                        translation.SetText(result.Text, result.GlossaryWarnings);
                        if (!translation.Signals.Contains(TranslationCueSignal.CorrectedByAi))
                            translation.Signals.Add(TranslationCueSignal.CorrectedByAi);
                        corrected++;

                        // Correzione di una tratta: uguale in tutte le copie
                        if (block.SharedStretch.HasValue)
                            touched.UnionWith(context.Shared[result.Id].Spread(state, result.Id, this._files.ConvertAll(f => f.State)));
                    }

                    block.Status = TranslationReviewBlockStatus.Done;
                    block.CallSeqs.AddRange(correction.CallSeqs);
                    foreach (TranslationJobFileState other in touched)
                    {
                        if (other != state)
                            this._store.SaveFile(this._job.Id, other);
                    }
                    this.SaveFile(state);
                }

                this.RaiseLog(AppText.F("toolbox.subtitleTranslation.review.blockSaved", file.FileName, number, count, issues.Count, corrected));
                this.ReportProgress(file.FileName, number, count);
                this.RaiseChanged();
            }

            int found = 0;
            int changed;
            lock (this._lock)
            {
                foreach (TranslationReviewBlock block in state.ReviewBlocks)
                    found += block.Issues.Count;
                changed = state.Translations.FindAll(t => t.Signals.Contains(TranslationCueSignal.CorrectedByAi)).Count;
            }
            this.RaiseLog(AppText.F("toolbox.subtitleTranslation.review.fileReviewed", file.FileName, found, changed));
            this.RaiseChanged();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Avvisa dell'avanzamento coi blocchi rivisti sul totale di tutti i file
        /// </summary>
        /// <param name="fileName">File in lavoro</param>
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
                    total += file.State.ReviewBlocks.Count;
                    done += file.State.ReviewBlocks.FindAll(b => b.Status == TranslationReviewBlockStatus.Done).Count;
                }
            }

            this.RaiseProgress(done, total, fileName, number, count);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Blocchi scelti in tutti i file, rivisti compresi
        /// </summary>
        public int BlockCount
        {
            get
            {
                int count = 0;
                lock (this._lock)
                {
                    foreach (TranslationRunFile file in this._files)
                        count += file.State.ReviewBlocks.Count;
                }

                return count;
            }
        }

        #endregion
    }
}
