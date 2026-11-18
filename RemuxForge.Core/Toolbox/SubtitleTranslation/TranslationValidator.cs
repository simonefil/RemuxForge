using RemuxForge.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Validazione deterministica delle traduzioni consegnate dal modello e delle modifiche dell'utente: forma della consegna,
    /// id, testo, segni dei tratti formattati, caratteri riservati del formato, gruppi di frase spezzata e termini del glossario
    /// </summary>
    public class TranslationValidator
    {
        #region Costanti

        /// <summary>Nessuna chiamata submit_translation nella risposta</summary>
        public const string NO_SUBMISSION = "NO_SUBMISSION";

        /// <summary>Argomenti non JSON, non oggetto o senza l'array translations</summary>
        public const string ARGS_INVALID = "ARGS_INVALID";

        /// <summary>Elemento di translations senza id intero o senza testo</summary>
        public const string ENTRY_INVALID = "ENTRY_INVALID";

        /// <summary>Id non richiesto</summary>
        public const string ID_OUT_OF_RANGE = "ID_OUT_OF_RANGE";

        /// <summary>Id richiesto assente dalla consegna</summary>
        public const string ID_MISSING = "ID_MISSING";

        /// <summary>Id consegnato più volte</summary>
        public const string ID_DUPLICATE = "ID_DUPLICATE";

        /// <summary>Testo vuoto o senza parole</summary>
        public const string TEXT_EMPTY = "TEXT_EMPTY";

        /// <summary>Segni ⟦⟧ non ben formati</summary>
        public const string MARK_MALFORMED = "MARK_MALFORMED";

        /// <summary>Segno del sorgente assente</summary>
        public const string MARK_MISSING = "MARK_MISSING";

        /// <summary>Segno che il sorgente non ha, o ripetuto</summary>
        public const string MARK_EXTRA = "MARK_EXTRA";

        /// <summary>Segni fuori dall'ordine del sorgente</summary>
        public const string MARK_ORDER = "MARK_ORDER";

        /// <summary>Segno senza testo</summary>
        public const string MARK_EMPTY = "MARK_EMPTY";

        /// <summary>Carattere che il formato interpreterebbe</summary>
        public const string RESERVED_CHAR = "RESERVED_CHAR";

        /// <summary>Battuta valida di un gruppo con un'altra battuta respinta</summary>
        public const string GROUP_INCOMPLETE = "GROUP_INCOMPLETE";

        /// <summary>Resa del glossario assente nella traduzione</summary>
        public const string GLOSSARY_MISSING = "GLOSSARY_MISSING";

        /// <summary>
        /// Prefisso della chiave dei messaggi della UI
        /// </summary>
        public const string UI_KEY_PREFIX = "toolbox.subtitleTranslation.validation.";

        /// <summary>
        /// Nome del tool di consegna
        /// </summary>
        private const string SUBMIT_TOOL = "submit_translation";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Articoli tolti all'inizio di termine e resa, per lingua ISO 639-2; quelli con l'apostrofo si attaccano alla parola
        /// </summary>
        private static readonly Dictionary<string, string[]> s_articles = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "eng", new string[] { "the", "a", "an" } },
            { "ita", new string[] { "il", "lo", "la", "i", "gli", "le", "l'", "un", "uno", "una", "un'" } },
            { "fra", new string[] { "le", "la", "les", "l'", "un", "une", "des" } },
            { "spa", new string[] { "el", "la", "los", "las", "un", "una", "unos", "unas" } },
            { "deu", new string[] { "der", "die", "das", "den", "dem", "des", "ein", "eine", "einen", "einem", "einer", "eines" } },
            { "por", new string[] { "o", "a", "os", "as", "um", "uma", "uns", "umas" } }
        };

        /// <summary>
        /// Sintassi dei segni ben formati
        /// </summary>
        private static readonly Regex s_markSyntax = new Regex("⟦[0-9]+ |⟧", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Sintassi dei segni anche malformati, per giudicare se restano parole
        /// </summary>
        private static readonly Regex s_anyMarkSyntax = new Regex("⟦[0-9]* ?|⟧", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Caratteri delle scritture che non separano le parole con spazi (ideogrammi, kana, thai, lao, khmer, birmano, tibetano)
        /// </summary>
        private static readonly Regex s_unspacedScript = new Regex(@"[\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}\p{IsCJKCompatibilityIdeographs}"
            + @"\p{IsHiragana}\p{IsKatakana}\p{IsKatakanaPhoneticExtensions}\p{IsThai}\p{IsLao}\p{IsKhmer}\p{IsMyanmar}\p{IsTibetan}]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Parola di una resa: lettere, segni diacritici e cifre a partire da una lettera o una cifra
        /// </summary>
        private static readonly Regex s_renderingWord = new Regex(@"[\p{L}\p{N}][\p{L}\p{M}\p{N}]*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Formato del file, per i caratteri riservati e le righe vuote
        /// </summary>
        private readonly SubtitleTranslationFormat _format;

        /// <summary>
        /// Termini del glossario pronti per il confronto
        /// </summary>
        private readonly List<TermPatterns> _terms;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="format">Formato del file</param>
        /// <param name="sourceLanguage">Lingua d'origine</param>
        /// <param name="targetLanguage">Lingua d'arrivo</param>
        /// <param name="glossary">Termini del glossario in uso</param>
        public TranslationValidator(SubtitleTranslationFormat format, string sourceLanguage, string targetLanguage, IReadOnlyList<GlossaryTerm> glossary)
        {
            this._format = format;
            this._terms = new List<TermPatterns>();
            string source = LanguageValidator.NormalizeToIso6392(sourceLanguage);
            string target = LanguageValidator.NormalizeToIso6392(targetLanguage);
            foreach (GlossaryTerm term in glossary)
            {
                string sourceWord = StripArticle(term.Source, source);
                string targetWord = StripArticle(term.Target, target);
                if (string.IsNullOrEmpty(sourceWord) || string.IsNullOrEmpty(targetWord))
                    continue;
                this._terms.Add(new TermPatterns(term.Source, term.Target, WordPattern(sourceWord), RenderingPattern(targetWord, term.Invariable)));
            }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Valida una consegna di submit_translation sulle battute richieste
        /// </summary>
        /// <param name="requestedIds">Id richiesti</param>
        /// <param name="cues">Battute che contengono gli id richiesti, con gruppi e ricette</param>
        /// <param name="submissions">Argomenti delle chiamate submit_translation della risposta, nell'ordine; si usa la prima leggibile</param>
        /// <returns>Esito per ogni id richiesto e problemi degli elementi scartati</returns>
        public TranslationValidationResult Validate(IReadOnlyList<int> requestedIds, IReadOnlyList<SubtitleCue> cues, IReadOnlyList<string> submissions)
        {
            Dictionary<int, SubtitleCue> byId = new Dictionary<int, SubtitleCue>();
            foreach (SubtitleCue cue in cues)
            {
                if (cue.Id.HasValue)
                    byId[cue.Id.Value] = cue;
            }
            List<int> requested = new List<int>(requestedIds);
            requested.Sort();
            HashSet<int> requestedSet = new HashSet<int>(requested);
            foreach (int id in requested)
            {
                if (!byId.ContainsKey(id))
                    throw new ArgumentException("La battuta richiesta " + id + " non è fra le battute date", nameof(requestedIds));
            }

            // Intervalli degli id richiesti nella forma 1-60,62
            string ranges = TranslationRequestBuilder.FormatRanges(requested);

            // Consegna: la prima chiamata con un oggetto JSON e l'array translations
            string deliveryCode = NO_SUBMISSION;
            string deliveryMessage = "no " + SUBMIT_TOOL + " call was received.";
            List<TranslationValidationIssue> entryIssues = new List<TranslationValidationIssue>();
            Dictionary<int, List<string>> delivered = new Dictionary<int, List<string>>();
            if (submissions != null && submissions.Count > 0)
            {
                deliveryCode = ARGS_INVALID;
                deliveryMessage = "arguments are not a JSON object with a translations array.";
            }
            for (int s = 0; submissions != null && s < submissions.Count && !string.IsNullOrEmpty(deliveryCode); s++)
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(submissions[s] ?? ""))
                    {
                        JsonElement translations;
                        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("translations", out translations)
                            || translations.ValueKind != JsonValueKind.Array)
                            continue;

                        deliveryCode = "";
                        int index = 0;
                        foreach (JsonElement entry in translations.EnumerateArray())
                        {
                            JsonElement idElement;
                            JsonElement textElement;
                            int id;
                            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out idElement) || idElement.ValueKind != JsonValueKind.Number
                                || !idElement.TryGetInt32(out id) || !entry.TryGetProperty("text", out textElement) || textElement.ValueKind != JsonValueKind.String)
                            {
                                entryIssues.Add(Issue(null, ENTRY_INVALID, IssueSeverity.Error,
                                    "translations[" + index + "] has no integer id or no string text.", "index", index.ToString(CultureInfo.InvariantCulture)));
                            }
                            else if (!requestedSet.Contains(id))
                            {
                                entryIssues.Add(Issue(null, ID_OUT_OF_RANGE, IssueSeverity.Error,
                                    "cue " + id + " was not requested; requested ids: " + ranges + ".", "id", id.ToString(CultureInfo.InvariantCulture)));
                            }
                            else
                            {
                                List<string> texts;
                                if (!delivered.TryGetValue(id, out texts))
                                {
                                    texts = new List<string>();
                                    delivered[id] = texts;
                                }
                                texts.Add(textElement.GetString());
                            }
                            index++;
                        }
                    }
                }
                catch (JsonException)
                {
                    // Argomenti non JSON: si passa alla chiamata seguente
                }
            }

            // Battute: id, testo, segni, caratteri riservati; l'esito tiene la lista a cui si aggiungono gruppo e glossario
            Dictionary<int, List<TranslationValidationIssue>> issues = new Dictionary<int, List<TranslationValidationIssue>>();
            Dictionary<int, CueValidationResult> results = new Dictionary<int, CueValidationResult>();
            foreach (int id in requested)
            {
                List<TranslationValidationIssue> cueIssues = new List<TranslationValidationIssue>();
                issues[id] = cueIssues;
                List<string> texts;
                string text = null;
                if (!string.IsNullOrEmpty(deliveryCode))
                {
                    cueIssues.Add(Issue(id, deliveryCode, IssueSeverity.Error, deliveryMessage));
                }
                else if (!delivered.TryGetValue(id, out texts))
                {
                    cueIssues.Add(Issue(id, ID_MISSING, IssueSeverity.Error, "cue " + id + " is missing from the delivery."));
                }
                else if (texts.Count > 1)
                {
                    cueIssues.Add(Issue(id, ID_DUPLICATE, IssueSeverity.Error, "cue " + id + " was delivered " + texts.Count + " times; deliver it once."));
                }
                else
                {
                    text = this.ValidateText(byId[id], texts[0], cueIssues);
                }
                results[id] = new CueValidationResult(id, text, cueIssues);
            }

            // Gruppi: una battuta respinta per un errore suo rimanda tutto il gruppo, e il messaggio cita lei
            HashSet<int> rejected = new HashSet<int>();
            foreach (int id in requested)
            {
                if (results[id].HasErrors)
                    rejected.Add(id);
            }
            foreach (int id in requested)
            {
                SubtitleCue cue = byId[id];
                if (!cue.Group.HasValue || rejected.Contains(id))
                    continue;
                foreach (int other in requested)
                {
                    if (other != id && byId[other].Group == cue.Group && rejected.Contains(other))
                    {
                        issues[id].Add(Issue(id, GROUP_INCOMPLETE, IssueSeverity.Error,
                            "cue " + other + " of group g" + cue.Group.Value + " was rejected; deliver the whole group again.",
                            "group", cue.Group.Value.ToString(CultureInfo.InvariantCulture)));
                        break;
                    }
                }
            }

            // Glossario sulle battute valide: un gruppo si confronta intero
            HashSet<int> checkedIds = new HashSet<int>();
            foreach (int id in requested)
            {
                if (checkedIds.Contains(id) || results[id].HasErrors)
                    continue;
                List<SubtitleCue> scope = new List<SubtitleCue>();
                List<string> scopeTexts = new List<string>();
                foreach (int member in requested)
                {
                    if (member == id || (byId[id].Group.HasValue && byId[member].Group == byId[id].Group))
                    {
                        scope.Add(byId[member]);
                        scopeTexts.Add(results[member].Text);
                        checkedIds.Add(member);
                    }
                }
                this.AddGlossaryWarnings(scope, scopeTexts, issues);
            }

            List<CueValidationResult> ordered = new List<CueValidationResult>();
            foreach (int id in requested)
                ordered.Add(results[id]);

            return new TranslationValidationResult(ordered, entryIssues);
        }

        /// <summary>
        /// Valida il testo scritto dall'utente per una battuta: stessi controlli della consegna, senza quelli sugli id e sul gruppo
        /// </summary>
        /// <param name="cue">Battuta modificata</param>
        /// <param name="text">Testo scritto dall'utente</param>
        /// <returns>Esito della battuta; con un errore la modifica non si salva</returns>
        public CueValidationResult ValidateEdit(SubtitleCue cue, string text)
        {
            List<TranslationValidationIssue> cueIssues = new List<TranslationValidationIssue>();
            string normalizedText = this.ValidateText(cue, text ?? "", cueIssues);
            CueValidationResult result = new CueValidationResult(cue.Id.Value, normalizedText, cueIssues);
            if (!result.HasErrors)
            {
                Dictionary<int, List<TranslationValidationIssue>> issues = new Dictionary<int, List<TranslationValidationIssue>>();
                issues[cue.Id.Value] = cueIssues;
                this.AddGlossaryWarnings(new List<SubtitleCue> { cue }, new List<string> { normalizedText }, issues);
            }

            return result;
        }

        #endregion

        #region Metodi interni

        /// <summary>
        /// Toglie l'articolo iniziale di un termine con l'elenco della sua lingua
        /// </summary>
        /// <param name="term">Termine o resa</param>
        /// <param name="language">Lingua ISO 639-2</param>
        /// <returns>Parola da cercare</returns>
        internal static string StripArticle(string term, string language)
        {
            string word = (term ?? "").Trim().Replace('’', '\'');
            string[] articles;
            if (!s_articles.TryGetValue(language, out articles))
                return word;

            foreach (string article in articles)
            {
                string head = article.EndsWith("'", StringComparison.Ordinal) ? article : article + " ";
                if (word.Length > head.Length && word.StartsWith(head, StringComparison.OrdinalIgnoreCase))
                    return word.Substring(head.Length).Trim();
            }

            return word;
        }

        /// <summary>
        /// Espressione che trova un termine del sorgente a parola intera. Un termine con almeno una maiuscola è un nome proprio o
        /// un termine che la serie scrive così, e si cerca con le maiuscole esatte: «Light» non trova «turn on the light», «Will»
        /// non trova «I will». Un termine tutto minuscolo si cerca senza distinguere le maiuscole, così lo trova anche a inizio
        /// frase. Nelle scritture senza maiuscole le due regole coincidono. Resta preso il nome che è anche una parola comune
        /// quando questa apre la frase («Light the fire»): distinguerli vorrebbe un vocabolario della lingua
        /// </summary>
        /// <param name="word">Termine senza articolo</param>
        /// <returns>Espressione</returns>
        internal static Regex WordPattern(string word)
        {
            RegexOptions options = string.Equals(word, word.ToLowerInvariant(), StringComparison.Ordinal)
                ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                : RegexOptions.CultureInvariant;

            return new Regex(@"(?<![\p{L}\p{M}\p{N}])" + Regex.Escape(word) + @"(?![\p{L}\p{M}\p{N}])", options);
        }

        /// <summary>
        /// Espressione che trova la resa di un termine nella traduzione, da usare sul testo in forma NFD. Nel dubbio la resa
        /// conta come presente: una resa mancata la trova la revisione, mentre una resa presente data per assente manda la
        /// battuta a una riscrittura a pagamento che può guastarla. Le maiuscole non contano mai, perché la resa a inizio
        /// frase la prende.
        /// - Scritture senza spazi (ideogrammi, kana, thai...): la resa si cerca contenuta nel testo, perché lì il confine di
        ///   parola non si vede e i nomi non si flettono.
        /// - Forma fissa: la resa esatta. Il confine di parola vale sui lati che finiscono con una lettera o una cifra, così
        ///   «-kun» si trova in «Light-kun».
        /// - Resa che si flette: in ogni lingua la flessione cambia soprattutto la fine della parola e a volte un diacritico
        ///   (Titano/Titani, titán/titanes, Haus/Häuser, Москва/Москвы). Senza tabelle per lingua, ogni parola di almeno cinque
        ///   lettere vale come tema: la parola senza le ultime due lettere (una se ne ha cinque), seguita da al più tre lettere
        ///   oltre a quelle tolte, senza contare i diacritici. Le parole più corte sono articoli, preposizioni o forme
        ///   irregolari (del/dei, Dio/Dei): accanto a una parola lunga che àncora la resa valgono come una parola qualsiasi; in
        ///   una resa di sole parole corte restano esatte, perché un tema di due o tre lettere troverebbe di tutto. Fra le parole
        ///   vale qualsiasi separatore (dell'Anima/delle Anime) e la punteggiatura in testa e in coda alla resa non conta
        /// </summary>
        /// <param name="word">Resa senza articolo</param>
        /// <param name="invariable">True per una resa in forma fissa</param>
        /// <returns>Espressione</returns>
        internal static Regex RenderingPattern(string word, bool invariable)
        {
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            string decomposed = word.Normalize(NormalizationForm.FormD);
            if (s_unspacedScript.IsMatch(decomposed))
                return new Regex(Regex.Escape(decomposed), options);

            // Parole della resa senza diacritici; una di almeno cinque lettere àncora la resa
            List<string> words = new List<string>();
            bool anchored = false;
            foreach (Match match in s_renderingWord.Matches(decomposed))
            {
                string bare = Regex.Replace(match.Value, @"\p{M}", "");
                words.Add(bare);
                anchored |= bare.Length >= 5;
            }

            if (invariable || words.Count == 0)
            {
                string composed = word.Normalize(NormalizationForm.FormC);
                return new Regex((char.IsLetterOrDigit(composed[0]) ? @"(?<![\p{L}\p{M}\p{N}])" : "") + Regex.Escape(decomposed)
                    + (char.IsLetterOrDigit(composed[composed.Length - 1]) ? @"(?![\p{L}\p{M}\p{N}])" : ""), options);
            }

            StringBuilder pattern = new StringBuilder(@"(?<![\p{L}\p{M}\p{N}])");
            for (int w = 0; w < words.Count; w++)
            {
                if (w > 0)
                    pattern.Append(@"[^\p{L}\p{M}\p{N}]+");
                string bare = words[w];
                if (anchored && bare.Length < 5)
                {
                    pattern.Append(@"[\p{L}\p{M}\p{N}]+");
                    continue;
                }

                int cut = bare.Length < 5 ? 0 : Math.Min(2, bare.Length - 4);
                foreach (char letter in bare.Substring(0, bare.Length - cut))
                    pattern.Append(Regex.Escape(letter.ToString())).Append(@"\p{M}*");
                if (cut > 0)
                    pattern.Append(@"(?:[\p{L}\p{N}]\p{M}*){0,").Append((cut + 3).ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            pattern.Append(@"(?![\p{L}\p{M}\p{N}])");

            return new Regex(pattern.ToString(), options);
        }

        /// <summary>
        /// Testo su cui si confrontano i termini: senza la sintassi dei segni, a capo come spazi, apostrofo dritto
        /// </summary>
        /// <param name="text">Testo pulito o consegnato</param>
        /// <returns>Testo per il confronto</returns>
        internal static string GlossaryPlain(string text)
        {
            return s_markSyntax.Replace(text, "").Replace('\n', ' ').Replace('’', '\'');
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Normalizza il testo consegnato e ne controlla testo, segni e caratteri riservati
        /// </summary>
        /// <param name="cue">Battuta sorgente</param>
        /// <param name="text">Testo consegnato</param>
        /// <param name="issues">Esiti della battuta, a cui si aggiungono gli errori</param>
        /// <returns>Testo normalizzato</returns>
        private string ValidateText(SubtitleCue cue, string text, List<TranslationValidationIssue> issues)
        {
            int id = cue.Id.Value;

            // Normalizzazione senza errore; il testo identico al sorgente resta com'è perché si ricomponga identico
            string normalizedText = text;
            if (!string.Equals(text, cue.CleanText, StringComparison.Ordinal))
            {
                string[] sourceLines = cue.CleanText.Split('\n');
                int allowedEmpty = 0;
                for (int l = 1; this._format != SubtitleTranslationFormat.Srt && l < sourceLines.Length - 1; l++)
                {
                    if (string.IsNullOrEmpty(sourceLines[l].Trim()))
                        allowedEmpty++;
                }
                List<string> lines = new List<string>();
                int pendingEmpty = 0;
                foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                    {
                        if (lines.Count > 0)
                            pendingEmpty++;
                        continue;
                    }
                    while (pendingEmpty > 0 && allowedEmpty > 0)
                    {
                        lines.Add("");
                        pendingEmpty--;
                        allowedEmpty--;
                    }
                    pendingEmpty = 0;
                    lines.Add(trimmed);
                }
                normalizedText = string.Join("\n", lines).Normalize(NormalizationForm.FormC);
            }

            if (string.IsNullOrEmpty(s_anyMarkSyntax.Replace(normalizedText, "").Trim()))
                issues.Add(Issue(id, TEXT_EMPTY, IssueSeverity.Error, "cue " + id + " has no text."));

            // Segni: una forma sbagliata salta gli altri controlli dei segni
            List<int> found = new List<int>();
            List<int> empty = new List<int>();
            string malformed = null;
            int open = -1;
            int openNumber = 0;
            int contentStart = 0;
            for (int i = 0; i < normalizedText.Length && malformed == null; i++)
            {
                if (normalizedText[i] == SubtitleCueCleaner.MARK_OPEN)
                {
                    int digits = i + 1;
                    while (digits < normalizedText.Length && normalizedText[digits] >= '0' && normalizedText[digits] <= '9')
                        digits++;
                    int close = normalizedText.IndexOf(SubtitleCueCleaner.MARK_CLOSE, i);
                    int number;
                    if (open >= 0 || digits == i + 1 || digits >= normalizedText.Length || normalizedText[digits] != ' '
                        || !int.TryParse(normalizedText.Substring(i + 1, digits - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                    {
                        int from = open >= 0 ? open : i;
                        malformed = close >= 0 ? normalizedText.Substring(from, close - from + 1) : normalizedText.Substring(from);
                        continue;
                    }
                    open = i;
                    openNumber = number;
                    contentStart = digits + 1;
                    i = digits;
                }
                else if (normalizedText[i] == SubtitleCueCleaner.MARK_CLOSE)
                {
                    if (open < 0)
                    {
                        int previous = normalizedText.LastIndexOf(SubtitleCueCleaner.MARK_CLOSE, Math.Max(0, i - 1));
                        int from = previous >= 0 && previous < i ? previous + 1 : 0;
                        malformed = normalizedText.Substring(from, i - from + 1);
                        continue;
                    }
                    if (string.IsNullOrEmpty(normalizedText.Substring(contentStart, i - contentStart).Trim()))
                        empty.Add(openNumber);
                    found.Add(openNumber);
                    open = -1;
                }
            }
            if (malformed == null && open >= 0)
                malformed = normalizedText.Substring(open);

            if (malformed != null)
            {
                issues.Add(Issue(id, MARK_MALFORMED, IssueSeverity.Error, "\"" + malformed + "\" is not a span of the form ⟦n text⟧."));
            }
            else
            {
                List<int> expected = new List<int>();
                foreach (MarkRecipe mark in cue.Recipe.Marks)
                    expected.Add(mark.Number);
                string expectedList = SpanList(expected);
                bool complete = true;
                foreach (int number in expected)
                {
                    if (!found.Contains(number))
                    {
                        complete = false;
                        issues.Add(Issue(id, MARK_MISSING, IssueSeverity.Error, "span ⟦" + number + "⟧ is missing; the source cue has spans " + expectedList + ".",
                            "n", number.ToString(CultureInfo.InvariantCulture)));
                    }
                }
                HashSet<int> seen = new HashSet<int>();
                foreach (int number in found)
                {
                    if (!expected.Contains(number) || !seen.Add(number))
                    {
                        complete = false;
                        issues.Add(Issue(id, MARK_EXTRA, IssueSeverity.Error,
                            "span ⟦" + number + "⟧ is not in the source cue (or appears twice); the source cue has spans " + expectedList + ".",
                            "n", number.ToString(CultureInfo.InvariantCulture)));
                    }
                }
                string foundList = SpanList(found);
                if (complete && foundList != expectedList)
                {
                    issues.Add(Issue(id, MARK_ORDER, IssueSeverity.Error, "spans appear as " + foundList + "; the source order is " + expectedList + ".",
                        "expected", expectedList, "found", foundList));
                }
                foreach (int number in empty)
                {
                    issues.Add(Issue(id, MARK_EMPTY, IssueSeverity.Error, "span ⟦" + number + "⟧ is empty; put the translated words inside it.",
                        "n", number.ToString(CultureInfo.InvariantCulture)));
                }
            }

            // Caratteri riservati che il testo pulito del sorgente non ha
            string reserved = this._format == SubtitleTranslationFormat.Srt ? "{}\\<>" : "{}\\";
            foreach (char c in reserved)
            {
                if (normalizedText.IndexOf(c) >= 0 && cue.CleanText.IndexOf(c) < 0)
                {
                    string value = c.ToString();
                    issues.Add(Issue(id, RESERVED_CHAR, IssueSeverity.Error, "\"" + value + "\" is markup in this subtitle format; write plain text.", "char", value));
                }
            }

            return normalizedText;
        }

        /// <summary>
        /// Aggiunge gli avvisi del glossario a un gruppo o a una battuta sola: il termine si cerca nel sorgente unito, la resa nella
        /// traduzione unita, e l'avviso va sulle battute il cui sorgente contiene il termine, sulla prima se il termine le scavalca
        /// </summary>
        /// <param name="scope">Battute confrontate insieme, in ordine di id</param>
        /// <param name="texts">Testi normalizzati delle battute</param>
        /// <param name="issues">Esiti per id</param>
        private void AddGlossaryWarnings(List<SubtitleCue> scope, List<string> texts, Dictionary<int, List<TranslationValidationIssue>> issues)
        {
            StringBuilder source = new StringBuilder();
            StringBuilder translation = new StringBuilder();
            string[] sources = new string[scope.Count];
            for (int i = 0; i < scope.Count; i++)
            {
                sources[i] = GlossaryPlain(scope[i].CleanText);
                source.Append(sources[i]).Append(' ');
                translation.Append(GlossaryPlain(texts[i])).Append(' ');
            }
            string joinedSource = source.ToString();
            string joinedTranslation = translation.ToString().Normalize(NormalizationForm.FormD);

            foreach (TermPatterns term in this._terms)
            {
                if (!term.SourcePattern.IsMatch(joinedSource) || term.TargetPattern.IsMatch(joinedTranslation))
                    continue;
                List<int> holders = new List<int>();
                for (int i = 0; i < scope.Count; i++)
                {
                    if (term.SourcePattern.IsMatch(sources[i]))
                        holders.Add(scope[i].Id.Value);
                }
                if (holders.Count == 0)
                    holders.Add(scope[0].Id.Value);
                foreach (int id in holders)
                {
                    issues[id].Add(Issue(id, GLOSSARY_MISSING, IssueSeverity.Warning,
                        "source has \"" + term.Source + "\", translation lacks \"" + term.Target + "\"", "source", term.Source, "target", term.Target));
                }
            }
        }

        /// <summary>
        /// Crea un esito con la chiave del messaggio della UI e i valori dei suoi segnaposto
        /// </summary>
        /// <param name="id">Battuta, null per un elemento scartato</param>
        /// <param name="code">Codice</param>
        /// <param name="severity">Gravità</param>
        /// <param name="message">Messaggio per il modello</param>
        /// <param name="args">Coppie nome, valore dei segnaposto della UI</param>
        /// <returns>Esito</returns>
        private static TranslationValidationIssue Issue(int? id, string code, IssueSeverity severity, string message, params string[] args)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < args.Length; i += 2)
                values[args[i]] = args[i + 1];

            return new TranslationValidationIssue(id, code, severity, message, UI_KEY_PREFIX + code, values);
        }

        /// <summary>
        /// Elenco dei segni nella forma «⟦1⟧, ⟦2⟧», «none» se vuoto
        /// </summary>
        /// <param name="numbers">Numeri dei segni</param>
        /// <returns>Elenco per i messaggi</returns>
        private static string SpanList(List<int> numbers)
        {
            if (numbers.Count == 0)
                return "none";

            StringBuilder list = new StringBuilder();
            foreach (int number in numbers)
            {
                if (list.Length > 0)
                    list.Append(", ");
                list.Append('⟦').Append(number.ToString(CultureInfo.InvariantCulture)).Append('⟧');
            }

            return list.ToString();
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Termine del glossario con le espressioni per il confronto
        /// </summary>
        /// <param name="Source">Termine del sorgente come nel glossario</param>
        /// <param name="Target">Resa come nel glossario</param>
        /// <param name="SourcePattern">Termine senza articolo, a parola intera</param>
        /// <param name="TargetPattern">Resa senza articolo, flessa se non è in forma fissa, sul testo NFD</param>
        private sealed record TermPatterns(string Source, string Target, Regex SourcePattern, Regex TargetPattern);

        #endregion
    }
}
