using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Applicazione dei termini aggiunti al passo dei termini nuovi: per file, le battute accettate col termine nel sorgente e
    /// senza la resa nella traduzione o con una resa segnalata diversa. Il codice sostituisce le rese segnalate dei nomi
    /// invariabili; il resto va a C, coi gruppi interi e al più 80 battute a chiamata. Le copie di una tratta condivisa si
    /// lasciano alla prima occorrenza, che le riscrive tutte. Il file è l'unità che si salva: la fermata di un altro file lo
    /// lascia finire, uno Stop lo rifà
    /// </summary>
    public sealed class TermApplier : TranslationPhaseRunner
    {
        #region Costanti

        /// <summary>
        /// Battute al più in una chiamata C
        /// </summary>
        public const int MAX_REWRITE_CUES = 80;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Termini aggiunti con le espressioni per il confronto e le rese segnalate dal modello
        /// </summary>
        private readonly List<AddedTerm> _added;

        /// <summary>
        /// Coppie originale e resa dei termini aggiunti per la coda di C
        /// </summary>
        private readonly List<KeyValuePair<string, string>> _updated;

        /// <summary>
        /// Instructions di C, uguali per tutto il job
        /// </summary>
        private readonly string _instructions;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="store">Job su disco</param>
        /// <param name="job">Testata del job, col glossario aggiornato e i termini decisi</param>
        /// <param name="files">File del job, nell'ordine del progetto</param>
        /// <param name="calls">Registro delle chiamate del job</param>
        /// <param name="stateLock">Lock dello stato condiviso</param>
        public TermApplier(TranslationJobStore store, TranslationJob job, List<TranslationRunFile> files, List<TranslationCallRecord> calls, object stateLock)
            : base(store, job, files, calls, stateLock)
        {
            string source = LanguageValidator.NormalizeToIso6392(job.Glossary.SourceLanguage);
            string target = LanguageValidator.NormalizeToIso6392(job.Glossary.TargetLanguage);
            this._added = new List<AddedTerm>();
            this._updated = new List<KeyValuePair<string, string>>();
            foreach (NewTermDecision decision in job.NewTerms)
            {
                GlossaryTerm term = decision.Decision == NewTermDecisionKind.Add
                    ? job.Glossary.Terms.Find(t => t.Origin == GlossaryOrigin.NewTerm && string.Equals(t.Source, decision.Source, StringComparison.Ordinal))
                    : null;
                string sourceWord = term != null ? TranslationValidator.StripArticle(term.Source, source) : "";
                string targetWord = term != null ? TranslationValidator.StripArticle(term.Target, target) : "";
                if (string.IsNullOrEmpty(sourceWord) || string.IsNullOrEmpty(targetWord))
                    continue;

                // Resa scelta e rese segnalate diverse, a parola intera con le maiuscole esatte e mai sul numero di un segno; la più
                // lunga prima, così una resa contenuta in un'altra non si trova dentro di lei. Per una resa che si flette, una resa
                // segnalata che è una sua forma flessa o con la maiuscola d'inizio frase non è diversa
                Regex targetPattern = TranslationValidator.RenderingPattern(targetWord, term.Invariable);
                List<string> usages = new List<string>();
                foreach (NewTermRendering rendering in decision.Renderings)
                {
                    string used = rendering.Target.Trim();
                    if (!string.IsNullOrEmpty(used) && !string.Equals(used, term.Target, StringComparison.Ordinal) && !usages.Contains(used)
                        && (term.Invariable || !targetPattern.IsMatch(used.Normalize(NormalizationForm.FormD))))
                        usages.Add(used);
                }
                Regex usage = null;
                if (usages.Count > 0)
                {
                    usages.Add(term.Target);
                    usages.Sort((a, b) => b.Length.CompareTo(a.Length));
                    usage = new Regex(@"(?<![\p{L}\p{M}\p{N}" + SubtitleCueCleaner.MARK_OPEN + "])(?:" + string.Join("|", usages.ConvertAll(u => Regex.Escape(u))) + @")(?![\p{L}\p{M}\p{N}])",
                        RegexOptions.CultureInvariant);
                }

                this._added.Add(new AddedTerm(term, TranslationValidator.WordPattern(sourceWord), targetPattern, usage));
                this._updated.Add(new KeyValuePair<string, string>(term.Source, term.Target));
            }
            this._instructions = TranslationRequestBuilder.CreateRewriteInstructions(job.Glossary.SourceLanguage, job.Glossary.TargetLanguage);
        }

        #endregion

        #region Metodi protected

        /// <inheritdoc />
        protected override bool HasWork(TranslationRunFile file)
        {
            return file.State.TermApplication == null || file.State.TermApplication.Status == TermApplicationStatus.Pending;
        }

        /// <inheritdoc />
        protected override bool IsFinished(TranslationRunFile file)
        {
            return !this.HasWork(file);
        }

        /// <summary>
        /// Riscrive le battute coinvolte del file: sostituzione del codice, poi le C sul resto; salva il file a lavoro finito
        /// </summary>
        /// <param name="file">File del job</param>
        /// <param name="provider">Provider condiviso</param>
        /// <param name="cancellation">Annullamento di Stop</param>
        /// <returns>Task del file</returns>
        protected override async Task ProcessFileAsync(TranslationRunFile file, ITranslationModelProvider provider, CancellationToken cancellation)
        {
            TranslationJobFileState state = file.State;
            FileContext context = this.CreateContext(file);

            // Battute coinvolte e sostituzione del codice, su copie: lo stato cambia solo a file finito
            List<int> targets = new List<int>();
            Dictionary<int, string> current = new Dictionary<int, string>();
            Dictionary<int, CueValidationResult> replaced = new Dictionary<int, CueValidationResult>();
            lock (this._lock)
            {
                foreach (TranslatedCueState translation in state.Translations)
                {
                    if (translation.Text != null)
                        current[translation.Id] = translation.Text;
                    SharedStretch stretch;
                    if (translation.Status != TranslatedCueStatus.Accepted || translation.Text == null
                        || (context.Shared.TryGetValue(translation.Id, out stretch) && !stretch.IsMaster(state.FileId, translation.Id)))
                        continue;

                    // Coinvolta col termine nel sorgente e senza la resa scelta, o con una resa segnalata diversa
                    string source = TranslationValidator.GlossaryPlain(context.Frozen[translation.Id].Text);
                    string plain = TranslationValidator.GlossaryPlain(translation.Text).Normalize(NormalizationForm.FormD);
                    List<AddedTerm> involved = this._added.FindAll(t => t.SourcePattern.IsMatch(source) && (!t.TargetPattern.IsMatch(plain)
                        || (t.Usage != null && t.Usage.Matches(translation.Text).Any(m => !string.Equals(m.Value, t.Term.Target, StringComparison.Ordinal)))));
                    if (involved.Count == 0)
                        continue;

                    targets.Add(translation.Id);
                    string text = translation.Text;
                    bool unresolved = false;
                    foreach (AddedTerm term in involved)
                    {
                        if (!term.Term.Invariable || term.Usage == null || !term.Usage.Matches(text).Any(m => !string.Equals(m.Value, term.Term.Target, StringComparison.Ordinal)))
                        {
                            unresolved = true;
                            break;
                        }
                        text = term.Usage.Replace(text, term.Term.Target.Replace("$", "$$"));
                    }
                    if (unresolved)
                        continue;

                    CueValidationResult result = context.Validator.ValidateEdit(context.GetLiveCue(translation.Id), text);
                    if (!result.HasErrors)
                    {
                        replaced[translation.Id] = result;
                        current[translation.Id] = result.Text;
                    }
                }

                if (state.TermApplication == null)
                    state.TermApplication = new TermApplicationState();
                state.TermApplication.TargetIds = targets;
                state.TermApplication.RewrittenIds = new List<int>();
                state.TermApplication.FailedIds = new List<int>();
            }
            this.RaiseChanged();

            List<int> remaining = targets.FindAll(id => !replaced.ContainsKey(id));
            List<int> ids = ExpandToGroups(state, remaining);
            DeliveryWork work = new DeliveryWork(null);
            DeliveryPlan plan = new DeliveryPlan
            {
                RetryKind = TranslationCallKind.C,
                ReducedKind = TranslationCallKind.C,
                Model = this._job.Models.NewTerms,
                ReasoningEffort = this._job.ReasoningEfforts.NewTerms,
                Instructions = this._instructions,
                CollectNewTerms = false,
                Tail = (requested, rejected) => TranslationRequestBuilder.CreateRewriteTail(this._updated, requested, current, rejected)
            };

            // Chiamate da 80 battute al più, senza spezzare un gruppo
            List<List<int>> chunks = new List<List<int>>();
            for (int i = 0; i < ids.Count;)
            {
                List<int> chunk = new List<int>();
                while (i < ids.Count)
                {
                    int? group = context.Frozen[ids[i]].Group;
                    int end = i + 1;
                    while (group.HasValue && end < ids.Count && context.Frozen[ids[end]].Group == group)
                        end++;
                    if (chunk.Count > 0 && chunk.Count + end - i > MAX_REWRITE_CUES)
                        break;
                    chunk.AddRange(ids.GetRange(i, end - i));
                    i = end;
                }
                chunks.Add(chunk);
            }

            for (int c = 0; c < chunks.Count; c++)
            {
                cancellation.ThrowIfCancellationRequested();
                this.ReportProgress(file.FileName, c + 1, chunks.Count);
                await this.DeliverAsync(context, work, plan, chunks[c], TranslationCallKind.C, 0, new string[0], false, provider, cancellation);
            }

            // Esiti nello stato: la consegna di C vince sulla sostituzione del codice; respinte e troncate tengono il testo di prima.
            // Ogni battuta toccata prende gli avvisi del glossario aggiornato, anche col testo invariato; una da rivedere arrivata
            // in C solo per il suo gruppo resta alla revisione AI
            Dictionary<int, KeyValuePair<string, List<GlossaryWarning>>> texts = new Dictionary<int, KeyValuePair<string, List<GlossaryWarning>>>();
            foreach (KeyValuePair<int, CueValidationResult> pair in replaced)
                texts[pair.Key] = new KeyValuePair<string, List<GlossaryWarning>>(pair.Value.Text, pair.Value.GetGlossaryWarnings());
            int rewritten;
            lock (this._lock)
            {
                foreach (TranslatedCueState result in work.Results.Values)
                {
                    if (result.Status == TranslatedCueStatus.Accepted)
                    {
                        texts[result.Id] = new KeyValuePair<string, List<GlossaryWarning>>(result.Text, result.GlossaryWarnings);
                    }
                    else if (!replaced.ContainsKey(result.Id) && targets.Contains(result.Id))
                    {
                        state.TermApplication.FailedIds.Add(result.Id);
                    }
                }
                foreach (int id in state.TermApplication.FailedIds)
                {
                    string text;
                    if (!current.TryGetValue(id, out text))
                        continue;
                    CueValidationResult check = context.Validator.ValidateEdit(context.GetLiveCue(id), text);
                    if (!check.HasErrors)
                        texts[id] = new KeyValuePair<string, List<GlossaryWarning>>(text, check.GetGlossaryWarnings());
                }

                foreach (KeyValuePair<int, KeyValuePair<string, List<GlossaryWarning>>> pair in texts)
                {
                    TranslatedCueState translation = state.Translations.Find(t => t.Id == pair.Key);
                    if (translation == null || translation.Status != TranslatedCueStatus.Accepted)
                        continue;

                    bool changed = !string.Equals(pair.Value.Key, translation.Text, StringComparison.Ordinal);
                    translation.SetText(pair.Value.Key, pair.Value.Value);
                    if (!changed)
                        continue;

                    if (!translation.Signals.Contains(TranslationCueSignal.RewrittenByTerms))
                        translation.Signals.Add(TranslationCueSignal.RewrittenByTerms);
                    state.TermApplication.RewrittenIds.Add(pair.Key);
                }

                // Battute delle tratte toccate: uguali in tutte le copie
                HashSet<TranslationJobFileState> touched = new HashSet<TranslationJobFileState>();
                foreach (int id in texts.Keys)
                {
                    SharedStretch stretch;
                    if (context.Shared.TryGetValue(id, out stretch))
                        touched.UnionWith(stretch.Spread(state, id, this._files.ConvertAll(f => f.State)));
                }
                foreach (TranslationJobFileState other in touched)
                {
                    if (other != state)
                        this._store.SaveFile(this._job.Id, other);
                }

                state.TermApplication.RewrittenIds.Sort();
                state.TermApplication.FailedIds.Sort();
                state.TermApplication.CallSeqs = new List<int>(work.CallSeqs);
                state.TermApplication.Status = targets.Count > 0 ? TermApplicationStatus.Done : TermApplicationStatus.Skipped;
                rewritten = state.TermApplication.RewrittenIds.Count;
                this.SaveFile(state);
            }

            this.ReportProgress(file.FileName, chunks.Count, chunks.Count);
            this.RaiseLog(AppText.F("toolbox.subtitleTranslation.terms.fileApplied", file.FileName, rewritten));
            this.RaiseChanged();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Avvisa dell'avanzamento coi file conclusi sul totale e le chiamate del file
        /// </summary>
        /// <param name="fileName">File in lavoro</param>
        /// <param name="number">Chiamata del file</param>
        /// <param name="count">Chiamate del file</param>
        private void ReportProgress(string fileName, int number, int count)
        {
            int done;
            lock (this._lock)
            {
                done = this._files.FindAll(f => !this.HasWork(f)).Count;
            }

            this.RaiseProgress(done, this._files.Count, fileName, number, count);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Termine aggiunto con le espressioni del validatore e le rese segnalate dal modello
        /// </summary>
        /// <param name="Term">Voce del glossario</param>
        /// <param name="SourcePattern">Termine senza articolo, a parola intera</param>
        /// <param name="TargetPattern">Resa senza articolo, flessa se non è in forma fissa, sul testo NFD</param>
        /// <param name="Usage">Resa scelta e rese segnalate diverse, la più lunga prima e a maiuscole esatte; null senza rese diverse</param>
        private sealed record AddedTerm(GlossaryTerm Term, Regex SourcePattern, Regex TargetPattern, Regex Usage);

        #endregion
    }
}
