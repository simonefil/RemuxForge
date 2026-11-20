using RemuxForge.Core.Ai;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Revisione AI di un job: per ogni blocco scelto una diagnosi D1 con un ritentativo, poi il codice mette nel file il testo
    /// corretto delle segnalazioni di senso e ritraduce da sole le battute da rivedere. Una diagnosi troncata dal tetto dei token
    /// in uscita a entrambe le chiamate si dimezza come una consegna. Ogni parte diagnosticata si salva, e alla ripresa il blocco
    /// riparte dalle parti che restano o dalle correzioni. Una parte che il modello non diagnostica anche dopo i tentativi e i
    /// dimezzamenti, come una risposta fermata dal filtro dei contenuti o senza la chiamata della diagnosi, lascia non riviste col
    /// motivo le sue battute, in tutte le copie per una tratta; le altre parti del blocco e le correzioni vanno avanti, e solo un
    /// errore del servizio ferma la fase. I blocchi di un file vanno in sequenza. Una tratta condivisa si rivede una volta
    /// sola, in un blocco suo nel file della prima occorrenza, e le correzioni passano a tutte le copie; i blocchi del piano
    /// saltano le battute delle tratte
    /// </summary>
    public sealed class TranslationReviewer : TranslationPhaseRunner
    {
        #region Costanti

        /// <summary>
        /// Chiamate D1 di un blocco o di una sua metà prima di dimezzarlo o lasciarne non riviste le battute
        /// </summary>
        private const int DIAGNOSIS_ATTEMPTS = 2;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Instructions di D1
        /// </summary>
        private readonly string _diagnosisInstructions;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore: sceglie i blocchi dei file che non li hanno ancora, nella modalità del job, e quelli delle tratte passate a
        /// un file dopo la scelta
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

            lock (stateLock)
            {
                foreach (TranslationRunFile file in files)
                {
                    // Blocchi del piano finale, dimezzamenti compresi, senza le battute delle tratte; poi un blocco per ogni tratta di
                    // cui il file ha la prima occorrenza. Senza revisione nessuno
                    TranslationJobFileState state = file.State;
                    if (state.ReviewBlocks == null)
                    {
                        state.ReviewBlocks = new List<TranslationReviewBlock>();
                        foreach (TranslationBlock block in state.Blocks)
                        {
                            if (block.Status != TranslationBlockStatus.Split && job.ReviewMode == TranslationReviewMode.Full)
                                state.ReviewBlocks.Add(new TranslationReviewBlock { BlockIndex = block.Index, FirstId = block.FirstId, LastId = block.LastId });
                        }
                    }

                    // Anche nei file coi blocchi già scelti: una tratta passa a un altro file quando quello della prima occorrenza
                    // viene tolto, e il file che la riceve non ha ancora il suo blocco
                    AddStretchBlocks(job, state);
                }
            }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Aggiunge al file i blocchi delle tratte di cui ha la prima occorrenza e che non hanno ancora il loro blocco: tutte nella
        /// completa, nessuna senza revisione. Una tratta passa a un altro file quando quello
        /// della prima occorrenza viene tolto; chiamato sotto il lock dello stato
        /// </summary>
        /// <param name="job">Testata del job</param>
        /// <param name="state">Stato del file</param>
        /// <returns>True se almeno un blocco è stato aggiunto</returns>
        public static bool AddStretchBlocks(TranslationJob job, TranslationJobFileState state)
        {
            bool added = false;
            foreach (SharedStretch stretch in job.SharedStretches)
            {
                List<int> lines = stretch.Occurrences[0].Ids;
                if (stretch.Occurrences[0].FileId == state.FileId && !state.ReviewBlocks.Exists(b => b.SharedStretch == stretch.Index)
                    && job.ReviewMode == TranslationReviewMode.Full)
                {
                    state.ReviewBlocks.Add(new TranslationReviewBlock { SharedStretch = stretch.Index, FirstId = lines[0], LastId = lines[lines.Count - 1] });
                    added = true;
                }
            }

            return added;
        }

        #endregion

        #region Metodi protetti

        /// <inheritdoc />
        protected override bool HasWork(TranslationRunFile file)
        {
            return file.State.ReviewBlocks.Exists(b => b.Status == TranslationReviewBlockStatus.Pending || b.Status == TranslationReviewBlockStatus.Diagnosed);
        }

        /// <inheritdoc />
        protected override bool IsFinished(TranslationRunFile file)
        {
            return !this.HasWork(file);
        }

        /// <summary>
        /// Rivede i blocchi da fare del file uno alla volta; ogni blocco si salva dopo ogni parte di D1 e dopo le correzioni. Una
        /// parte senza diagnosi valida lascia le sue battute non riviste col motivo, e il resto del blocco va avanti fino alle
        /// correzioni
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
                    block = state.ReviewBlocks.Find(b => b.Status == TranslationReviewBlockStatus.Pending || b.Status == TranslationReviewBlockStatus.Diagnosed);
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
                    // D1 con un ritentativo per parte; troncata a entrambe le chiamate si dimezza fino alla battuta, e i
                    // problemi delle parti si sommano
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
                            lines.Add("TO_REVIEW: " + string.Join(", ", translation.GetReasonCodes()));
                        foreach (GlossaryWarning warning in translation.GlossaryWarnings)
                        {
                            // Con la nota del termine, che può restringere dove la resa vale
                            GlossaryTerm term = this._job.Glossary.Terms.Find(t => t.Source == warning.Source && t.Target == warning.Target);
                            string note = term != null && !string.IsNullOrWhiteSpace(term.Note) ? " (note: " + term.Note.Trim().Replace('\n', ' ') + ")" : "";
                            lines.Add("GLOSSARY: source has \"" + warning.Source + "\", translation lacks \"" + warning.Target + "\"" + note);
                        }

                        if (lines.Count > 0)
                            signals[translation.Id] = lines;
                    }

                    // Parti da diagnosticare: il blocco intero, o quelle rimaste da un dimezzamento salvato
                    DeliveryWork diagnosis = new DeliveryWork(unit);
                    List<List<int>> parts;
                    lock (this._lock)
                        parts = block.Parts != null ? new List<List<int>>(block.Parts) : new List<List<int>> { ids };
                    int notReviewed = 0;
                    string failure = null;
                    while (parts.Count > 0)
                    {
                        List<int> partIds = parts[0];
                        List<FrozenCue> partCues = partIds.ConvertAll(id => context.Frozen[id]);
                        List<TranslationReviewIssue> partIssues = null;
                        bool truncated = true;
                        string reason = null;
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
                                reason = response.IncompleteReason == INCOMPLETE_CONTENT_FILTER ? REASON_CONTENT_FILTER : REASON_INCOMPLETE;
                            }
                            else
                            {
                                truncated = false;
                                bool called = false;
                                JsonArray entries = null;
                                foreach (JsonObject item in response.Items)
                                {
                                    if (AiJson.GetString(item, "type") != "function_call" || AiJson.GetString(item, "name") != TranslationRequestBuilder.SUBMIT_DIAGNOSIS_TOOL)
                                        continue;
                                    called = true;
                                    try
                                    {
                                        entries = (JsonNode.Parse(AiJson.GetString(item, "arguments") ?? "") as JsonObject)?["issues"] as JsonArray;
                                    }
                                    catch (JsonException)
                                    {
                                        entries = null;
                                    }
                                    break;
                                }

                                if (entries == null)
                                {
                                    reason = called ? TranslationValidator.ARGS_INVALID : TranslationValidator.NO_SUBMISSION;
                                    record.Outcome = TranslationCallOutcome.PartiallyRejected;
                                    record.Rejections.Add(new TranslationCallRejection { Id = null, Reasons = new List<string> { called
                                        ? TranslationValidator.ARGS_INVALID + ": submit_diagnosis arguments are not a JSON object with an issues array."
                                        : TranslationValidator.NO_SUBMISSION + ": no submit_diagnosis call was received." } });
                                }
                                else
                                {
                                    // Problemi fuori dal blocco, fuori enum, senza spiegazione o senza cosa dicono sorgente e traduzione si scartano
                                    partIssues = new List<TranslationReviewIssue>();
                                    foreach (JsonNode node in entries)
                                    {
                                        JsonObject entry = node as JsonObject;
                                        int id;
                                        if (entry == null || !(entry["id"] is JsonValue idValue) || !idValue.TryGetValue(out id) || !partIds.Contains(id))
                                            continue;
                                        string category = AiJson.GetString(entry, "category");
                                        string severity = AiJson.GetString(entry, "severity");
                                        string explanation = (AiJson.GetString(entry, "explanation") ?? "").Trim();
                                        string sourceSays = (AiJson.GetString(entry, "source_says") ?? "").Trim();
                                        string translationSays = (AiJson.GetString(entry, "translation_says") ?? "").Trim();
                                        if (!((IList<string>)TranslationRequestBuilder.DIAGNOSIS_CATEGORIES).Contains(category) || !((IList<string>)TranslationRequestBuilder.DIAGNOSIS_SEVERITIES).Contains(severity)
                                            || string.IsNullOrEmpty(explanation) || string.IsNullOrEmpty(sourceSays) || string.IsNullOrEmpty(translationSays))
                                            continue;
                                        string speaker = (AiJson.GetString(entry, "speaker") ?? "").Trim();
                                        string suggestion = AiJson.GetString(entry, "suggestion");
                                        partIssues.Add(new TranslationReviewIssue
                                        {
                                            Id = id, Category = category, Severity = severity, SourceSays = sourceSays, TranslationSays = translationSays,
                                            Speaker = speaker.Length > 0 ? speaker : null, Explanation = explanation, Suggestion = string.IsNullOrEmpty(suggestion) ? null : suggestion
                                        });
                                    }
                                    record.Outcome = TranslationCallOutcome.Accepted;
                                    record.Issues = partIssues.Count;
                                }
                            }
                            this.RecordCall(record, diagnosis);
                        }

                        // Troncata a entrambe le chiamate: le metà prendono il posto della parte. Senza metà le battute della parte
                        // restano non riviste col motivo, in tutte le copie per una tratta, e le altre parti vanno avanti
                        parts.RemoveAt(0);
                        HashSet<TranslationJobFileState> marked = new HashSet<TranslationJobFileState>();
                        if (partIssues == null)
                        {
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
                                failure = reason;
                                notReviewed += partIds.Count;
                                lock (this._lock)
                                {
                                    foreach (int id in partIds)
                                    {
                                        TranslatedCueState translation;
                                        if (!translations.TryGetValue(id, out translation) || translation.Signals.Contains(TranslationCueSignal.EditedByUser))
                                            continue;
                                        translation.NotReviewedReason = reason;
                                        if (block.SharedStretch.HasValue)
                                            marked.UnionWith(context.Shared[id].Spread(state, id, this._files.ConvertAll(f => f.State)));
                                    }
                                }
                            }
                            else
                            {
                                this.RaiseLog(AppText.F("toolbox.subtitleTranslation.run.retryReduced", file.FileName, halves[0].Count, halves[1].Count));
                                parts.InsertRange(0, halves);
                            }
                        }

                        // Ogni parte si salva con le sue chiamate: uno Stop o un errore del servizio non la fanno ripagare
                        lock (this._lock)
                        {
                            if (partIssues != null)
                                block.Issues.AddRange(partIssues);
                            block.Parts = parts.Count > 0 ? new List<List<int>>(parts) : null;
                            block.Status = parts.Count == 0 ? TranslationReviewBlockStatus.Diagnosed : TranslationReviewBlockStatus.Pending;
                            block.CallSeqs.AddRange(diagnosis.CallSeqs);
                            diagnosis.CallSeqs.Clear();
                            foreach (TranslationJobFileState other in marked)
                            {
                                if (other != state)
                                    this._store.SaveFile(this._job.Id, other);
                            }
                            this.SaveFile(state);
                        }
                    }

                    if (failure != null)
                        this.RaiseLog(AppText.F("toolbox.subtitleTranslation.review.diagnosisFailed", file.FileName, number, count, notReviewed, AppText.T(TranslationValidationIssue.REASON_KEY_PREFIX + failure)));
                    issues = block.Issues;
                }

                // Testo corretto delle segnalazioni di senso, il genere solo col parlante indicato: per ogni battuta quello dell'ultima
                // segnalazione che lo dà, validato come una consegna e senza riparazioni. Un testo respinto lascia la battuta com'è, e
                // la segnalazione resta nella diagnosi del blocco. Una battuta scritta dall'utente non si corregge; una da rivedere,
                // senza testo, si ritraduce da sola con un ritentativo della traduzione
                List<TranslationReviewIssue> applied = issues.FindAll(i => ((IList<string>)TranslationRequestBuilder.MEANING_CATEGORIES).Contains(i.Category)
                    && (i.Category != TranslationRequestBuilder.GENDER_NUMBER_CATEGORY || i.Speaker != null));
                Dictionary<int, string> suggestions = new Dictionary<int, string>();
                List<int> retranslate = new List<int>();
                lock (this._lock)
                {
                    foreach (TranslationReviewIssue issue in applied)
                    {
                        if (issue.Suggestion != null && current.ContainsKey(issue.Id) && !translations[issue.Id].Signals.Contains(TranslationCueSignal.EditedByUser))
                            suggestions[issue.Id] = issue.Suggestion;
                    }
                    foreach (TranslatedCueState translation in translations.Values)
                    {
                        if (translation.Status == TranslatedCueStatus.ToReview && !translation.Signals.Contains(TranslationCueSignal.EditedByUser))
                            retranslate.Add(translation.Id);
                    }
                }

                Dictionary<int, KeyValuePair<string, List<GlossaryWarning>>> corrections = new Dictionary<int, KeyValuePair<string, List<GlossaryWarning>>>();
                List<int> suggested = state.Cues.FindAll(c => suggestions.ContainsKey(c.Id)).ConvertAll(c => c.Id);
                if (suggested.Count > 0)
                {
                    // Cartelli in signs, dialoghi e canzoni in translations, come nella consegna del modello
                    JsonArray entries = new JsonArray();
                    JsonArray signs = new JsonArray();
                    foreach (int id in suggested)
                        (context.GetLiveCue(id).Kind == SubtitleCueKind.Sign ? signs : entries).Add(new JsonObject { ["id"] = id, ["text"] = suggestions[id] });
                    TranslationValidationResult validation = context.Validator.Validate(suggested, context.GetLiveCues(suggested),
                        new List<string> { new JsonObject { ["translations"] = entries, ["signs"] = signs }.ToJsonString() }, new int[0]);
                    foreach (CueValidationResult cue in validation.Cues)
                    {
                        if (!cue.HasErrors)
                            corrections[cue.Id] = new KeyValuePair<string, List<GlossaryWarning>>(cue.Text, cue.GetGlossaryWarnings());
                    }
                }

                DeliveryWork retry = new DeliveryWork(unit);
                foreach (int id in state.Cues.FindAll(c => retranslate.Contains(c.Id)).ConvertAll(c => c.Id))
                {
                    List<CueRejection> rejections = translations[id].Rejections;
                    await this.DeliverAsync(context, retry, this.CreateTranslationPlan(context, retry), new List<int> { id }, TranslationCallKind.BRetry, 1,
                        rejections.Count > 0 ? rejections[rejections.Count - 1].Reasons : new List<string>(), false, provider, cancellation);
                }
                foreach (TranslatedCueState result in retry.Results.Values)
                {
                    if (result.Status == TranslatedCueStatus.Accepted)
                        corrections[result.Id] = new KeyValuePair<string, List<GlossaryWarning>>(result.Text, result.GlossaryWarnings);
                }

                int corrected = 0;
                lock (this._lock)
                {
                    HashSet<TranslationJobFileState> touched = new HashSet<TranslationJobFileState>();
                    foreach (KeyValuePair<int, KeyValuePair<string, List<GlossaryWarning>>> correction in corrections)
                    {
                        TranslatedCueState translation = translations[correction.Key];
                        if (string.Equals(correction.Value.Key, translation.Text, StringComparison.Ordinal))
                            continue;

                        // Testo di prima e motivo per l'avviso della griglia
                        List<string> reasons = applied.FindAll(i => i.Id == correction.Key).ConvertAll(i => i.Explanation);
                        translation.AiCorrection = new CueAiCorrection { Before = translation.Text, Explanation = reasons.Count > 0 ? string.Join("\n", reasons) : null };
                        translation.SetText(correction.Value.Key, correction.Value.Value);
                        if (!translation.Signals.Contains(TranslationCueSignal.CorrectedByAi))
                            translation.Signals.Add(TranslationCueSignal.CorrectedByAi);
                        corrected++;

                        // Correzione di una tratta: uguale in tutte le copie
                        if (block.SharedStretch.HasValue)
                            touched.UnionWith(context.Shared[correction.Key].Spread(state, correction.Key, this._files.ConvertAll(f => f.State)));
                    }

                    block.Status = TranslationReviewBlockStatus.Done;
                    block.CallSeqs.AddRange(retry.CallSeqs);
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
        /// Avvisa dell'avanzamento coi blocchi conclusi, rivisti o non rivisti, sul totale di tutti i file
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
