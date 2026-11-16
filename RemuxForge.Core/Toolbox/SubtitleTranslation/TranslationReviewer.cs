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
    /// Revisione AI di un job: per ogni blocco scelto una diagnosi D1 senza ritentativi, poi D2 sulle battute diagnosticate e
    /// su quelle da rivedere, coi gruppi interi. La diagnosi si salva, anche
    /// quella non valida, e alla ripresa il blocco riparte da D2; i blocchi di un file vanno in sequenza
    /// </summary>
    public sealed class TranslationReviewer : TranslationPhaseRunner
    {
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
        public TranslationReviewer(TranslationJobStore store, TranslationJob job, List<TranslationRunFile> files, List<TranslationCallRecord> calls, object stateLock)
            : base(store, job, files, calls, stateLock)
        {
            this._diagnosisInstructions = TranslationRequestBuilder.CreateDiagnosisInstructions(job.Glossary.SourceLanguage, job.Glossary.TargetLanguage);
            this._correctionInstructions = TranslationRequestBuilder.CreateCorrectionInstructions(job.Glossary.SourceLanguage, job.Glossary.TargetLanguage);
            this._updated = new List<KeyValuePair<GlossaryTerm, Regex>>();
            string language = LanguageValidator.NormalizeToIso6392(job.Glossary.SourceLanguage);
            foreach (GlossaryTerm term in job.Glossary.Terms)
            {
                string word = TranslationValidator.StripArticle(term.Source, language);
                if (term.Origin == GlossaryOrigin.NewTerm && !string.IsNullOrEmpty(word))
                    this._updated.Add(new KeyValuePair<GlossaryTerm, Regex>(term, TranslationValidator.WordPattern(word)));
            }

            lock (stateLock)
            {
                foreach (TranslationRunFile file in files)
                {
                    TranslationJobFileState state = file.State;
                    if (state.ReviewBlocks != null)
                        continue;

                    // Blocchi del piano finale, dimezzamenti compresi; nella mirata solo quelli con almeno una battuta segnalata
                    state.ReviewBlocks = new List<TranslationReviewBlock>();
                    foreach (TranslationBlock block in state.Blocks)
                    {
                        if (block.Status == TranslationBlockStatus.Split)
                            continue;
                        bool signalled = state.Translations.Exists(t => t.Id >= block.FirstId && t.Id <= block.LastId && (t.Status == TranslatedCueStatus.ToReview
                            || t.Signals.Contains(TranslationCueSignal.Retried) || t.Signals.Contains(TranslationCueSignal.Glossary)
                            || t.Signals.Contains(TranslationCueSignal.NewTerm) || t.Signals.Contains(TranslationCueSignal.RewrittenByTerms)));
                        if (job.ReviewMode == TranslationReviewMode.Full || signalled)
                            state.ReviewBlocks.Add(new TranslationReviewBlock { BlockIndex = block.Index, FirstId = block.FirstId, LastId = block.LastId });
                    }
                }
            }
        }

        #endregion

        #region Metodi protected

        /// <inheritdoc />
        protected override bool HasWork(TranslationRunFile file)
        {
            return file.State.ReviewBlocks.Exists(b => b.Status == TranslationReviewBlockStatus.Pending || b.Status == TranslationReviewBlockStatus.Diagnosed
                || b.Status == TranslationReviewBlockStatus.DiagnosisFailed);
        }

        /// <inheritdoc />
        protected override bool IsFinished(TranslationRunFile file)
        {
            return !this.HasWork(file);
        }

        /// <summary>
        /// Rivede i blocchi da fare del file uno alla volta; ogni blocco si salva dopo D1 e dopo D2
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
                    block = state.ReviewBlocks.Find(b => b.Status == TranslationReviewBlockStatus.Pending || b.Status == TranslationReviewBlockStatus.Diagnosed
                        || b.Status == TranslationReviewBlockStatus.DiagnosisFailed);
                    number = state.ReviewBlocks.IndexOf(block) + 1;
                    count = state.ReviewBlocks.Count;
                }
                if (block == null)
                    break;
                if (this.IsPaused)
                    return;

                cancellation.ThrowIfCancellationRequested();
                this.ReportProgress(file.FileName, number, count);
                List<FrozenCue> cues = state.Cues.FindAll(c => c.Id >= block.FirstId && c.Id <= block.LastId);
                List<int> ids = cues.ConvertAll(c => c.Id);
                Dictionary<int, string> current = new Dictionary<int, string>();
                Dictionary<int, TranslatedCueState> translations = new Dictionary<int, TranslatedCueState>();
                lock (this._lock)
                {
                    foreach (TranslatedCueState translation in state.Translations)
                    {
                        if (translation.Id < block.FirstId || translation.Id > block.LastId)
                            continue;
                        translations[translation.Id] = translation;
                        if (translation.Text != null)
                            current[translation.Id] = translation.Text;
                    }
                }

                List<TranslationReviewIssue> issues;
                bool failed = block.Status == TranslationReviewBlockStatus.DiagnosisFailed;
                if (block.Status != TranslationReviewBlockStatus.Pending)
                {
                    issues = block.Issues;
                }
                else
                {
                    // D1: una chiamata sola; senza una consegna valida il blocco passa alla correzione delle sole da rivedere
                    Dictionary<int, List<string>> signals = new Dictionary<int, List<string>>();
                    foreach (TranslatedCueState translation in translations.Values)
                    {
                        // Un segnale per riga, nella forma CODICE: dettaglio
                        List<string> lines = new List<string>();
                        foreach (CueRejection rejection in translation.Rejections)
                        {
                            if (translation.Status == TranslatedCueStatus.Accepted)
                                lines.Add("RETRIED: attempt " + rejection.Attempt.ToString(CultureInfo.InvariantCulture) + " rejected with " + string.Join(", ", ReadCodes(rejection.Reasons)));
                        }
                        if (translation.Status == TranslatedCueStatus.ToReview)
                        {
                            List<string> codes = translation.Rejections.Count > 0 ? ReadCodes(translation.Rejections[translation.Rejections.Count - 1].Reasons) : new List<string>();
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

                    DeliveryWork diagnosis = new DeliveryWork(block.BlockIndex);
                    TranslationModelRequest request = this.CreateRequest(TranslationCallKind.D1, state.FileId, this._job.Models.Review, this._job.ReasoningEfforts.Review,
                        this._diagnosisInstructions, context.Prefix, TranslationRequestBuilder.CreateDiagnosisTail(cues, current, signals), TranslationRequestBuilder.CreateDiagnosisTools());
                    KeyValuePair<AiResponseResult, TranslationCallRecord> sent = await this.SendAsync(request, state.FileId, diagnosis, ids, null, provider, cancellation);
                    AiResponseResult response = sent.Key;
                    TranslationCallRecord record = sent.Value;
                    record.Usage = new TranslationCallUsage(response.Usage, 0);
                    issues = new List<TranslationReviewIssue>();
                    string invalid = null;
                    if (response.Status == AiResponseStatus.Incomplete)
                    {
                        record.ResponseStatus = TranslationCallResponseStatus.Incomplete;
                        record.IncompleteReason = response.IncompleteReason;
                        record.Outcome = TranslationCallOutcome.PartiallyRejected;
                        failed = true;
                    }
                    else
                    {
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
                            invalid = "ARGS_INVALID: no submit_diagnosis call with an issues array.";
                        }
                        else
                        {
                            // Problemi fuori dal blocco, fuori enum o senza spiegazione si scartano
                            foreach (JsonNode node in entries)
                            {
                                JsonObject entry = node as JsonObject;
                                int id;
                                if (entry == null || !(entry["id"] is JsonValue idValue) || !idValue.TryGetValue(out id) || !ids.Contains(id))
                                    continue;
                                string category = AiOAuthFlow.GetString(entry, "category");
                                string severity = AiOAuthFlow.GetString(entry, "severity");
                                string explanation = (AiOAuthFlow.GetString(entry, "explanation") ?? "").Trim();
                                if (!((IList<string>)TranslationRequestBuilder.DIAGNOSIS_CATEGORIES).Contains(category) || !((IList<string>)TranslationRequestBuilder.DIAGNOSIS_SEVERITIES).Contains(severity)
                                    || explanation.Length == 0)
                                    continue;
                                string suggestion = AiOAuthFlow.GetString(entry, "suggestion");
                                issues.Add(new TranslationReviewIssue { Id = id, Category = category, Severity = severity, Explanation = explanation, Suggestion = string.IsNullOrEmpty(suggestion) ? null : suggestion });
                            }
                        }

                        failed = invalid != null;
                        record.Outcome = failed ? TranslationCallOutcome.PartiallyRejected : TranslationCallOutcome.Accepted;
                        record.Issues = failed ? null : issues.Count;
                        if (failed)
                            record.Rejections.Add(new TranslationCallRejection { Id = null, Reasons = new List<string> { invalid } });
                    }
                    this.RecordCall(record, diagnosis);

                    // Diagnosi salvata anche non valida: uno Stop durante D2 non la fa ripagare
                    lock (this._lock)
                    {
                        block.Issues = issues;
                        block.Status = failed ? TranslationReviewBlockStatus.DiagnosisFailed : TranslationReviewBlockStatus.Diagnosed;
                        block.CallSeqs.AddRange(diagnosis.CallSeqs);
                        this.SaveFile(state);
                    }
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
                DeliveryWork correction = new DeliveryWork(block.BlockIndex);
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
                    }

                    block.Status = failed ? TranslationReviewBlockStatus.Failed : TranslationReviewBlockStatus.Done;
                    block.CallSeqs.AddRange(correction.CallSeqs);
                    this.SaveFile(state);
                }

                this.RaiseLog(AppText.F("toolbox.subtitleTranslation.review.blockSaved", file.FileName, number, count, issues.Count, corrected));
                this.ReportProgress(file.FileName, number, count);
                this.RaiseChanged();
            }

            int found = 0;
            int changed = 0;
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
                    done += file.State.ReviewBlocks.FindAll(b => b.Status == TranslationReviewBlockStatus.Done || b.Status == TranslationReviewBlockStatus.Failed).Count;
                }
            }

            this.RaiseProgress(done, total, fileName, number, count);
        }

        /// <summary>
        /// Codici degli errori dalle righe del validatore «id CODICE: messaggio»
        /// </summary>
        /// <param name="reasons">Righe di un rifiuto</param>
        /// <returns>Codici distinti nell'ordine</returns>
        private static List<string> ReadCodes(List<string> reasons)
        {
            List<string> codes = new List<string>();
            foreach (string reason in reasons)
            {
                int space = reason.IndexOf(' ');
                int colon = reason.IndexOf(':');
                string code = colon < 0 ? reason : space >= 0 && space < colon ? reason.Substring(space + 1, colon - space - 1) : reason.Substring(0, colon);
                if (!codes.Contains(code))
                    codes.Add(code);
            }

            return codes;
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
