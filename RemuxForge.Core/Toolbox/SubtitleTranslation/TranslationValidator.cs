using RemuxForge.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Validazione deterministica delle traduzioni consegnate dal modello e delle modifiche dell'utente: forma della consegna,
    /// id, testo, a capo, segni dei tratti formattati, caratteri riservati del formato e termini del glossario; i caratteri
    /// invisibili spuri e, a richiesta, segni e caratteri riservati sbagliati li ripara il codice
    /// </summary>
    public class TranslationValidator
    {
        #region Costanti

        /// <summary>Nessuna chiamata al tool di consegna nella risposta</summary>
        public const string NO_SUBMISSION = "NO_SUBMISSION";

        /// <summary>Argomenti non JSON, non oggetto o senza i campi del tool, oppure tool sconosciuto</summary>
        public const string ARGS_INVALID = "ARGS_INVALID";

        /// <summary>Consegna in più dopo quella usata</summary>
        public const string DUPLICATE_SUBMISSION = "DUPLICATE_SUBMISSION";

        /// <summary>Elemento di translations o signs senza id intero o senza testo</summary>
        public const string ENTRY_INVALID = "ENTRY_INVALID";

        /// <summary>Id non richiesto nell'elenco che lo porta: un cartello in translations, un dialogo o una canzone in signs, o un id fuori dalla richiesta</summary>
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

        /// <summary>Meno a capo del sorgente in un cartello consegnato insieme ad altri cartelli</summary>
        public const string LINE_BREAK_MISSING = "LINE_BREAK_MISSING";

        /// <summary>Carattere che il formato interpreterebbe</summary>
        public const string RESERVED_CHAR = "RESERVED_CHAR";

        /// <summary>Carattere invisibile di formato (spazio a larghezza zero e simili) che il sorgente non ha, tolto dal codice</summary>
        public const string INVISIBLE_CHAR = "INVISIBLE_CHAR";

        /// <summary>Resa del glossario assente nella traduzione</summary>
        public const string GLOSSARY_MISSING = "GLOSSARY_MISSING";

        /// <summary>
        /// Prefisso della chiave dei messaggi della UI
        /// </summary>
        private const string UI_KEY_PREFIX = "toolbox.subtitleTranslation.validation.";

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
        /// Controlli invisibili di a capo e di legatura: spazio a larghezza zero, ZWNJ, ZWJ, word joiner, ZWNBSP
        /// </summary>
        private static readonly HashSet<char> s_lineControls = new HashSet<char> { '\u200B', '\u200C', '\u200D', '\u2060', '\uFEFF' };

        /// <summary>
        /// Controlli invisibili di direzione: LRM, RLM, ALM, incorporamenti, sostituzioni e isolamenti bidi
        /// </summary>
        private static readonly HashSet<char> s_directionControls = new HashSet<char>
        {
            '\u200E', '\u200F', '\u061C', '\u202A', '\u202B', '\u202C', '\u202D', '\u202E', '\u2066', '\u2067', '\u2068', '\u2069'
        };

        /// <summary>
        /// Apertura di un segno in fondo a una riga
        /// </summary>
        private static readonly Regex s_markOpenAtEnd = new Regex("⟦[0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        /// <param name="glossary">Glossario in uso</param>
        public TranslationValidator(SubtitleTranslationFormat format, Glossary glossary)
        {
            this._format = format;
            this._terms = CreateTermPatterns(glossary);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Testi che contengono nel sorgente almeno un termine del glossario, trovato come negli avvisi del glossario
        /// </summary>
        /// <param name="glossary">Glossario dei termini cercati</param>
        /// <param name="texts">Testi puliti delle battute sorgenti</param>
        /// <returns>Indici dei testi col termine</returns>
        public static List<int> FindTermTexts(Glossary glossary, IReadOnlyList<string> texts)
        {
            List<TermPatterns> terms = CreateTermPatterns(glossary);
            List<int> found = new List<int>();
            for (int i = 0; i < texts.Count; i++)
            {
                string source = GlossaryPlain(texts[i]);
                if (terms.Exists(term => term.SourcePattern.IsMatch(source)))
                    found.Add(i);
            }

            return found;
        }

        /// <summary>
        /// Valida una consegna di submit_translation sulle battute richieste
        /// </summary>
        /// <param name="requestedIds">Id richiesti in ordine di tempo, l'ordine dell'esito e dei ritentativi</param>
        /// <param name="cues">Battute che contengono gli id richiesti, con le ricette</param>
        /// <param name="submissions">Argomenti delle chiamate submit_translation della risposta, nell'ordine; si usa la prima leggibile. Dialoghi
        /// e canzoni stanno in translations, i cartelli in signs: una voce nell'elenco sbagliato si scarta e la sua battuta resta mancante</param>
        /// <param name="repairIds">Id che hanno già avuto il loro ritentativo: segni, caratteri riservati e consegne doppie si riparano
        /// invece di respingerli. Senza la forma dei segni, o coi segni fuori ordine, resta il testo senza formattazione; altrimenti si
        /// tolgono i segni in più e quelli vuoti, e un segno mancante resta senza la sua formattazione. Di una consegna doppia vale la
        /// prima. Le riparazioni restano come avvisi</param>
        /// <returns>Esito per ogni id richiesto e problemi senza id degli elementi scartati</returns>
        public TranslationValidationResult Validate(IReadOnlyList<int> requestedIds, IReadOnlyList<SubtitleCue> cues, IReadOnlyList<string> submissions,
            IReadOnlyCollection<int> repairIds)
        {
            Dictionary<int, SubtitleCue> byId = new Dictionary<int, SubtitleCue>();
            foreach (SubtitleCue cue in cues)
            {
                if (cue.Id.HasValue)
                    byId[cue.Id.Value] = cue;
            }
            List<int> requested = new List<int>(requestedIds);
            foreach (int id in requested)
            {
                if (!byId.ContainsKey(id))
                    throw new ArgumentException("La battuta richiesta " + id + " non è fra le battute date", nameof(requestedIds));
            }

            // Id richiesti di ciascun elenco, coi loro intervalli nella forma 1-60,62
            List<int> requestedSigns = requested.FindAll(id => byId[id].Kind == SubtitleCueKind.Sign);
            Dictionary<string, HashSet<int>> requestedSets = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal)
            {
                { TranslationRequestBuilder.TRANSLATIONS_LIST, new HashSet<int>(requested.FindAll(id => byId[id].Kind != SubtitleCueKind.Sign)) },
                { TranslationRequestBuilder.SIGNS_LIST, new HashSet<int>(requestedSigns) }
            };

            // Consegna: la prima chiamata con un oggetto JSON e i due array
            string deliveryCode = NO_SUBMISSION;
            string deliveryMessage = "no " + TranslationRequestBuilder.SUBMIT_TRANSLATION_TOOL + " call was received.";
            List<TranslationValidationIssue> entryIssues = new List<TranslationValidationIssue>();
            Dictionary<int, List<string>> delivered = new Dictionary<int, List<string>>();
            if (submissions != null && submissions.Count > 0)
            {
                deliveryCode = ARGS_INVALID;
                deliveryMessage = "arguments are not a JSON object with a " + TranslationRequestBuilder.TRANSLATIONS_LIST + " array and a " + TranslationRequestBuilder.SIGNS_LIST + " array.";
            }
            for (int s = 0; submissions != null && s < submissions.Count && !string.IsNullOrEmpty(deliveryCode); s++)
            {
                try
                {
                    using (JsonDocument document = JsonDocument.Parse(submissions[s] ?? ""))
                    {
                        JsonElement translations;
                        JsonElement signs;
                        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(TranslationRequestBuilder.TRANSLATIONS_LIST, out translations)
                            || translations.ValueKind != JsonValueKind.Array || !document.RootElement.TryGetProperty(TranslationRequestBuilder.SIGNS_LIST, out signs) || signs.ValueKind != JsonValueKind.Array)
                            continue;

                        deliveryCode = "";
                        foreach (KeyValuePair<string, JsonElement> list in new KeyValuePair<string, JsonElement>[] { new KeyValuePair<string, JsonElement>(TranslationRequestBuilder.TRANSLATIONS_LIST, translations), new KeyValuePair<string, JsonElement>(TranslationRequestBuilder.SIGNS_LIST, signs) })
                        {
                            HashSet<int> listIds = requestedSets[list.Key];
                            int index = 0;
                            foreach (JsonElement entry in list.Value.EnumerateArray())
                            {
                                JsonElement idElement;
                                JsonElement textElement;
                                int id;
                                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out idElement) || idElement.ValueKind != JsonValueKind.Number
                                    || !idElement.TryGetInt32(out id) || !entry.TryGetProperty("text", out textElement) || textElement.ValueKind != JsonValueKind.String)
                                {
                                    entryIssues.Add(Issue(null, ENTRY_INVALID, IssueSeverity.Error,
                                        list.Key + "[" + index + "] has no integer id or no string text.", "index", index.ToString(CultureInfo.InvariantCulture)));
                                }
                                else if (!listIds.Contains(id))
                                {
                                    entryIssues.Add(Issue(null, ID_OUT_OF_RANGE, IssueSeverity.Error,
                                        "cue " + id + " was not requested in " + list.Key + "; requested ids there: " + (listIds.Count > 0 ? TranslationRequestBuilder.FormatRanges(listIds.ToList()) : "none") + ".",
                                        "id", id.ToString(CultureInfo.InvariantCulture)));
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
                }
                catch (JsonException)
                {
                    // Argomenti non JSON: si passa alla chiamata seguente
                }
            }

            // Battute: id, testo, segni, caratteri riservati; l'esito tiene la lista a cui si aggiunge il glossario
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
                else if (texts.Count > 1 && !repairIds.Contains(id))
                {
                    cueIssues.Add(Issue(id, ID_DUPLICATE, IssueSeverity.Error, "cue " + id + " was delivered " + texts.Count + " times; deliver it once."));
                }
                else
                {
                    // Nella riparazione di una battuta consegnata più volte vale la prima consegna
                    if (texts.Count > 1)
                        cueIssues.Add(Issue(id, ID_DUPLICATE, IssueSeverity.Warning, "cue " + id + " was delivered " + texts.Count + " times; the first one was kept."));
                    text = this.ValidateText(byId[id], texts[0], cueIssues, true, repairIds.Contains(id));

                    // A capo, solo sui cartelli: dialoghi e canzoni vanno a capo da soli nel sottotitolo. Un cartello con meno righe del
                    // sorgente è di solito la prima riga di un cartello consegnata da sola, coi cartelli dopo slittati sugli id sbagliati.
                    // Da solo un cartello non ha vicini su cui slittare
                    int sourceBreaks = byId[id].CleanText.Count(c => c == '\n');
                    int breaks = text.Count(c => c == '\n');
                    if (byId[id].Kind == SubtitleCueKind.Sign && requestedSigns.Count > 1 && breaks < sourceBreaks && !cueIssues.Exists(issue => issue.Code == TEXT_EMPTY))
                    {
                        cueIssues.Add(Issue(id, LINE_BREAK_MISSING, IssueSeverity.Error, "the source of [sign] cue " + id + " has " + sourceBreaks + " line break(s), the translation " + breaks
                            + ". A [sign] cue keeps at least the line breaks of its source; one with fewer is usually the first line of the cue delivered alone, with the next [sign] translations shifted onto the wrong ids, "
                            + "so this cue and every [sign] cue after it are requested again.",
                            "expected", sourceBreaks.ToString(CultureInfo.InvariantCulture), "found", breaks.ToString(CultureInfo.InvariantCulture)));
                    }
                }
                results[id] = new CueValidationResult(id, text, cueIssues);
            }

            // Glossario sulle battute valide
            foreach (int id in requested)
            {
                if (!results[id].HasErrors)
                    this.AddGlossaryWarnings(byId[id], results[id].Text, issues[id]);
            }

            List<CueValidationResult> ordered = new List<CueValidationResult>();
            foreach (int id in requested)
                ordered.Add(results[id]);

            return new TranslationValidationResult(ordered, entryIssues);
        }

        /// <summary>
        /// Valida il testo scritto dall'utente per una battuta: stessi controlli della consegna, senza quelli sugli id
        /// </summary>
        /// <param name="cue">Battuta modificata</param>
        /// <param name="text">Testo scritto dall'utente</param>
        /// <returns>Esito della battuta; con un errore la modifica non si salva</returns>
        public CueValidationResult ValidateEdit(SubtitleCue cue, string text)
        {
            List<TranslationValidationIssue> cueIssues = new List<TranslationValidationIssue>();
            string normalizedText = this.ValidateText(cue, text ?? "", cueIssues, false, false);
            CueValidationResult result = new CueValidationResult(cue.Id.Value, normalizedText, cueIssues);
            if (!result.HasErrors)
                this.AddGlossaryWarnings(cue, normalizedText, cueIssues);

            return result;
        }

        /// <summary>
        /// Precompila «Maiuscole esatte» dei termini del glossario che non l'hanno ancora; una voce che l'ha già resta com'è, così
        /// la scelta dell'utente non si perde. Che un termine sia un nome proprio lo dicono le battute sorgenti, non le maiuscole
        /// del glossario: l'analisi scrive spesso con l'iniziale maiuscola anche i nomi comuni.
        /// - Voce di tipo Titolo: maiuscole esatte, perché vale solo l'uso come titolo o vocativo, maiuscolo a metà frase («Yes,
        ///   Doctor», «Doctor Smith»); «a doctor» non è il titolo.
        /// - Termine scritto con le sue maiuscole in mezzo alla frase: è un nome proprio e ha le maiuscole esatte. Un nome che è
        ///   anche una parola comune («Rose») si trova come nome, e «a red rose» non lo è; se la serie usa la stessa parola anche
        ///   minuscola, quella è la parola comune e resta fuori.
        /// - Altrimenti maiuscole libere: scritto minuscolo in mezzo alla frase è un nome comune, e senza nessuna occorrenza in
        ///   mezzo alla frase non c'è prova; un termine mancato lo trova la revisione.
        /// - L'inizio della battuta, di una riga o di una frase (dopo . ! ? …) non prova nulla, perché lì la maiuscola la vuole
        ///   la frase; restano presi i nomi propri che sono anche parole comuni quando queste aprono la frase («Light the fire»)
        /// </summary>
        /// <param name="glossary">Glossario, per la lingua d'origine e i termini</param>
        /// <param name="sourceTexts">Testi puliti delle battute sorgenti</param>
        /// <returns>True se almeno una voce ha preso il valore</returns>
        public static bool FillExactCase(Glossary glossary, IEnumerable<string> sourceTexts)
        {
            const string sentenceBreaks = ".!?…\n";
            string language = LanguageValidator.NormalizeToIso6392(glossary.SourceLanguage);
            List<string> texts = null;
            bool filled = false;
            foreach (GlossaryTerm term in glossary.Terms)
            {
                if (term.ExactCase.HasValue)
                    continue;

                filled = true;
                term.ExactCase = false;
                string word = StripArticle(term.Source, language);
                if (term.Kind == GlossaryTermKind.Title)
                {
                    term.ExactCase = true;
                    continue;
                }
                if (string.IsNullOrEmpty(word) || string.Equals(word, word.ToLowerInvariant(), StringComparison.Ordinal))
                    continue;

                // I testi si preparano alla prima voce da precompilare
                if (texts == null)
                {
                    texts = new List<string>();
                    foreach (string text in sourceTexts)
                        texts.Add(SubtitleCueCleaner.RemoveMarks(text).Replace('’', '\''));
                }

                // Il termine con le sue maiuscole, preceduto da una parola senza un fine frase in mezzo
                Regex pattern = WordPattern(word, true);
                foreach (string text in texts)
                {
                    foreach (Match match in pattern.Matches(text))
                    {
                        int before = match.Index - 1;
                        while (before >= 0 && !char.IsLetterOrDigit(text[before]) && sentenceBreaks.IndexOf(text[before]) < 0)
                            before--;
                        if (before >= 0 && sentenceBreaks.IndexOf(text[before]) < 0)
                            term.ExactCase = true;
                    }
                }
            }

            return filled;
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
        /// Espressione che trova una parola intera
        /// </summary>
        /// <param name="word">Parola</param>
        /// <param name="exactCase">True per le maiuscole esatte, false per non distinguerle</param>
        /// <returns>Espressione</returns>
        internal static Regex WordPattern(string word, bool exactCase)
        {
            return new Regex(@"(?<![\p{L}\p{M}\p{N}])" + Regex.Escape(word) + @"(?![\p{L}\p{M}\p{N}])",
                exactCase ? RegexOptions.CultureInvariant : RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>
        /// Testo su cui si confrontano i termini: senza la sintassi dei segni, a capo come spazi, apostrofo dritto
        /// </summary>
        /// <param name="text">Testo pulito o consegnato</param>
        /// <returns>Testo per il confronto</returns>
        internal static string GlossaryPlain(string text)
        {
            return SubtitleCueCleaner.RemoveMarks(text).Replace('\n', ' ').Replace('’', '\'');
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Termini del glossario pronti per il confronto, senza quelli che senza articolo restano vuoti nel sorgente o nella resa
        /// </summary>
        /// <param name="glossary">Glossario</param>
        /// <returns>Termini con le espressioni</returns>
        private static List<TermPatterns> CreateTermPatterns(Glossary glossary)
        {
            List<TermPatterns> terms = new List<TermPatterns>();
            string source = LanguageValidator.NormalizeToIso6392(glossary.SourceLanguage);
            string target = LanguageValidator.NormalizeToIso6392(glossary.TargetLanguage);
            foreach (GlossaryTerm term in glossary.Terms)
            {
                string sourceWord = StripArticle(term.Source, source);
                string targetWord = StripArticle(term.Target, target);
                if (string.IsNullOrEmpty(sourceWord) || string.IsNullOrEmpty(targetWord))
                    continue;
                terms.Add(new TermPatterns(term.Source, term.Target, WordPattern(sourceWord, term.ExactCase == true), RenderingPattern(targetWord, term.Invariable)));
            }

            return terms;
        }

        /// <summary>
        /// Normalizza il testo consegnato e ne controlla testo, segni e caratteri riservati; nel testo del modello toglie i caratteri
        /// invisibili spuri e, nella riparazione, i segni sbagliati e i caratteri riservati, con un avviso per ognuno
        /// </summary>
        /// <param name="cue">Battuta sorgente</param>
        /// <param name="text">Testo consegnato</param>
        /// <param name="issues">Esiti della battuta, a cui si aggiungono errori e avvisi</param>
        /// <param name="delivered">Vero per il testo consegnato dal modello, dove \N e \n scritti sono a capo; falso per quello
        /// scritto dall'utente o ricomposto dal codice (sostituzione dei termini), che non si controlla sui caratteri invisibili</param>
        /// <param name="repair">Vero per riparare segni, caratteri riservati e consegne doppie invece di respingerli</param>
        /// <returns>Testo normalizzato, riparato dove serve</returns>
        private string ValidateText(SubtitleCue cue, string text, List<TranslationValidationIssue> issues, bool delivered, bool repair)
        {
            int id = cue.Id.Value;
            IssueSeverity markSeverity = repair ? IssueSeverity.Warning : IssueSeverity.Error;

            // Il modello scrive gli a capo come \N, come nei record, come a capo veri o come \n scritto, il ritorno a capo dell'ASS:
            // valgono tutti, e la barra rovesciata è comunque riservata nel testo dei formati
            if (delivered)
                text = text.Replace(TranslationRequestBuilder.LINE_BREAK_MARK, "\n").Replace("\\n", "\n");

            // Caratteri invisibili spuri nella consegna del modello, che il sorgente e le rese dei termini del glossario presenti nella
            // battuta non hanno (Unicode Standard, capitolo 23.2 Layout Controls): si tolgono. In un testo le cui lettere sono tutte
            // latine, greche o cirilliche nessun carattere di formato serve: ognuno è spurio, dovunque stia. Nelle altre scritture lo
            // spazio a larghezza zero, il word joiner e lo ZWNBSP segnano o bloccano un a capo dove le parole non hanno spazi (thai,
            // khmer, birmano, giapponese), ZWJ e ZWNJ governano legature e forme delle lettere nelle scritture corsive e indiane:
            // spuri solo fra due lettere latine, greche o cirilliche (anche al di là di altri invisibili); i segni di direzione e i
            // controlli bidi ordinano il testo bidirezionale: spuri solo senza caratteri da destra a sinistra; gli altri, come i segni
            // arabi che si vedono, non contano
            if (delivered)
            {
                HashSet<char> reported = new HashSet<char>();
                StringBuilder visible = new StringBuilder();
                Func<char, bool> spacedLetter = letter => char.IsLetter(letter) && (letter < '\u0530' || (letter >= '\u1E00' && letter < '\u2000'));
                bool spacedOnly = text.Any(char.IsLetter) && text.Where(char.IsLetter).All(spacedLetter);
                bool rightToLeft = text.Any(c => (c >= '\u0590' && c <= '\u08FF') || (c >= '\uFB1D' && c <= '\uFDFF') || (c >= '\uFE70' && c <= '\uFEFE'));
                List<string> renderings = this._terms.FindAll(term => term.SourcePattern.IsMatch(GlossaryPlain(cue.CleanText))).ConvertAll(term => term.Target);
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (char.GetUnicodeCategory(c) != UnicodeCategory.Format || cue.CleanText.IndexOf(c) >= 0 || renderings.Exists(target => target.IndexOf(c) >= 0))
                    {
                        visible.Append(c);
                        continue;
                    }
                    int before = i - 1;
                    while (before >= 0 && s_lineControls.Contains(text[before]))
                        before--;
                    int after = i + 1;
                    while (after < text.Length && s_lineControls.Contains(text[after]))
                        after++;
                    string value = "U+" + ((int)c).ToString("X4", CultureInfo.InvariantCulture);
                    string reason = null;
                    if (spacedOnly)
                        reason = value + " is an invisible character the source cue does not have; it was removed.";
                    else if (s_lineControls.Contains(c) && before >= 0 && after < text.Length && spacedLetter(text[before]) && spacedLetter(text[after]))
                        reason = value + " is an invisible character the source cue does not have between two Latin, Greek or Cyrillic letters; it was removed from between those letters.";
                    else if (s_directionControls.Contains(c) && !rightToLeft)
                        reason = value + " is a direction mark the source cue does not have, in a text without right-to-left letters; it was removed.";

                    if (reason == null)
                    {
                        visible.Append(c);
                        continue;
                    }
                    if (reported.Add(c))
                        issues.Add(Issue(id, INVISIBLE_CHAR, IssueSeverity.Warning, reason, "char", value));
                }
                text = visible.ToString();
            }

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

                    // Un segno aperto in fondo alla riga, che il sorgente ha quando il tratto comincia con l'a capo, tiene il suo spazio
                    lines.Add(s_markOpenAtEnd.IsMatch(trimmed) ? trimmed + " " : trimmed);
                }
                normalizedText = string.Join("\n", lines).Normalize(NormalizationForm.FormC);
            }

            // Segni: una forma sbagliata salta gli altri controlli dei segni
            List<int> expected = new List<int>();
            foreach (MarkRecipe mark in cue.Recipe.Marks)
                expected.Add(mark.Number);
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
                    if (!normalizedText.Substring(contentStart, i - contentStart).Any(c => !char.IsWhiteSpace(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format))
                        empty.Add(openNumber);
                    found.Add(openNumber);
                    open = -1;
                }
            }
            if (malformed == null && open >= 0)
                malformed = normalizedText.Substring(open);

            bool unordered = false;
            int markIssues = issues.Count;
            if (malformed != null)
            {
                issues.Add(Issue(id, MARK_MALFORMED, markSeverity, "\"" + malformed + "\" is not a span of the form ⟦n text⟧."));
            }
            else
            {
                string expectedList = SpanList(expected);
                bool complete = true;
                foreach (int number in expected)
                {
                    if (!found.Contains(number))
                    {
                        complete = false;
                        issues.Add(Issue(id, MARK_MISSING, markSeverity, "span ⟦" + number + "⟧ is missing; the source cue has spans " + expectedList + ".",
                            "n", number.ToString(CultureInfo.InvariantCulture)));
                    }
                }
                HashSet<int> seen = new HashSet<int>();
                foreach (int number in found)
                {
                    if (!expected.Contains(number) || !seen.Add(number))
                    {
                        complete = false;
                        issues.Add(Issue(id, MARK_EXTRA, markSeverity,
                            "span ⟦" + number + "⟧ is not in the source cue (or appears twice); the source cue has spans " + expectedList + ".",
                            "n", number.ToString(CultureInfo.InvariantCulture)));
                    }
                }
                string foundList = SpanList(found);
                if (complete && foundList != expectedList)
                {
                    unordered = true;
                    issues.Add(Issue(id, MARK_ORDER, markSeverity, "spans appear as " + foundList + "; the source order is " + expectedList + ".",
                        "expected", expectedList, "found", foundList));
                }
                foreach (int number in empty)
                {
                    issues.Add(Issue(id, MARK_EMPTY, markSeverity, "span ⟦" + number + "⟧ is empty; put the translated words inside it.",
                        "n", number.ToString(CultureInfo.InvariantCulture)));
                }
            }

            // Riparazione dei segni: senza la loro forma o fuori ordine resta il testo senza formattazione; altrimenti restano la prima
            // occorrenza non vuota di ogni segno del sorgente e il testo degli altri, e un segno mancante resta senza formattazione
            if (repair && issues.Count > markIssues)
            {
                StringBuilder repaired = new StringBuilder();
                HashSet<int> kept = new HashSet<int>();
                for (int i = 0; i < normalizedText.Length; i++)
                {
                    if (malformed != null || unordered || normalizedText[i] != SubtitleCueCleaner.MARK_OPEN)
                    {
                        repaired.Append(normalizedText[i]);
                        continue;
                    }
                    int digits = i + 1;
                    while (normalizedText[digits] != ' ')
                        digits++;
                    int close = normalizedText.IndexOf(SubtitleCueCleaner.MARK_CLOSE, digits);
                    string content = normalizedText.Substring(digits + 1, close - digits - 1);
                    int number = int.Parse(normalizedText.Substring(i + 1, digits - i - 1), NumberStyles.None, CultureInfo.InvariantCulture);
                    if (expected.Contains(number) && !empty.Contains(number) && kept.Add(number))
                        repaired.Append(normalizedText, i, close - i + 1);
                    else
                        repaired.Append(content);
                    i = close;
                }
                normalizedText = malformed != null || unordered ? s_anyMarkSyntax.Replace(repaired.ToString(), "") : repaired.ToString();
            }

            // Caratteri riservati che il testo pulito del sorgente non ha; la riparazione toglie i blocchi di tag interi, come
            // {\i1} o <i>, e poi i caratteri rimasti
            string reserved = this._format == SubtitleTranslationFormat.Srt ? "{}\\<>" : "{}\\";
            List<string> spurious = new List<string>();
            foreach (char c in reserved)
            {
                if (normalizedText.IndexOf(c) >= 0 && cue.CleanText.IndexOf(c) < 0)
                {
                    string value = c.ToString();
                    spurious.Add(value);
                    issues.Add(Issue(id, RESERVED_CHAR, markSeverity, "\"" + value + "\" is markup in this subtitle format; write plain text.", "char", value));
                }
            }
            if (repair && spurious.Count > 0)
            {
                if (spurious.Contains("{") && spurious.Contains("}"))
                    normalizedText = Regex.Replace(normalizedText, @"\{[^{}]*\}", "");
                if (spurious.Contains("<") && spurious.Contains(">"))
                    normalizedText = Regex.Replace(normalizedText, @"<[^<>]*>", "");
                foreach (string value in spurious)
                    normalizedText = normalizedText.Replace(value, "");
            }

            // Dopo una riparazione restano gli spazi del testo tolto: righe senza spazi doppi né ai bordi, salvo lo spazio di un segno
            // aperto in fondo alla riga
            if (repair)
            {
                normalizedText = string.Join("\n", normalizedText.Split('\n').Select(line =>
                {
                    string trimmed = Regex.Replace(line, " {2,}", " ").Trim();
                    return s_markOpenAtEnd.IsMatch(trimmed) ? trimmed + " " : trimmed;
                }));
            }

            // Vuoto anche quando restano solo spazi e caratteri invisibili di formato
            if (!s_anyMarkSyntax.Replace(normalizedText, "").Any(c => !char.IsWhiteSpace(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format))
                issues.Add(Issue(id, TEXT_EMPTY, IssueSeverity.Error, "cue " + id + " has no text."));

            return normalizedText;
        }

        /// <summary>
        /// Aggiunge gli avvisi del glossario a una battuta: un termine del sorgente senza la sua resa nella traduzione
        /// </summary>
        /// <param name="cue">Battuta sorgente</param>
        /// <param name="text">Testo normalizzato della traduzione</param>
        /// <param name="issues">Esiti della battuta</param>
        private void AddGlossaryWarnings(SubtitleCue cue, string text, List<TranslationValidationIssue> issues)
        {
            string source = GlossaryPlain(cue.CleanText);
            string translation = GlossaryPlain(text).Normalize(NormalizationForm.FormD);
            foreach (TermPatterns term in this._terms)
            {
                if (term.SourcePattern.IsMatch(source) && !term.TargetPattern.IsMatch(translation))
                {
                    issues.Add(Issue(cue.Id.Value, GLOSSARY_MISSING, IssueSeverity.Warning,
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

        /// <summary>
        /// Espressione che trova la resa di un termine nella traduzione, da usare sul testo in forma NFD. Nel dubbio la resa
        /// conta come presente: una resa mancata la trova la revisione, mentre una resa presente data per assente segnala la
        /// battuta a torto. Le maiuscole non contano mai, perché la resa a inizio frase la prende.
        /// - Scritture senza spazi (ideogrammi, kana, thai...): la resa si cerca contenuta nel testo, perché lì il confine di
        ///   parola non si vede e i nomi non si flettono.
        /// - Forma fissa: la resa esatta. Il confine di parola vale sui lati che finiscono con una lettera o una cifra, così
        ///   «-kun» si trova in «Yuki-kun».
        /// - Resa che si flette: la corrispondenza «50% prefix» di memoQ e Phrase. Ogni parola della resa trova una parola
        ///   intera della traduzione che comincia con la sua prima metà, arrotondata per eccesso, senza contare maiuscole e
        ///   diacritici: «Titano» trova «Titani», «titán» trova «titanes», «Haus» trova «Häuser». Fra le parole vale qualsiasi
        ///   separatore (dell'Anima/delle Anime) e la punteggiatura in testa e in coda alla resa non conta
        /// </summary>
        /// <param name="word">Resa senza articolo</param>
        /// <param name="invariable">True per una resa in forma fissa</param>
        /// <returns>Espressione</returns>
        private static Regex RenderingPattern(string word, bool invariable)
        {
            RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            string decomposed = word.Normalize(NormalizationForm.FormD);
            if (s_unspacedScript.IsMatch(decomposed))
                return new Regex(Regex.Escape(decomposed), options);

            // Parole della resa senza diacritici
            List<string> words = new List<string>();
            foreach (Match match in s_renderingWord.Matches(decomposed))
                words.Add(Regex.Replace(match.Value, @"\p{M}", ""));

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
                foreach (char letter in words[w].Substring(0, (words[w].Length + 1) / 2))
                    pattern.Append(Regex.Escape(letter.ToString())).Append(@"\p{M}*");
                pattern.Append(@"[\p{L}\p{M}\p{N}]*");
            }
            pattern.Append(@"(?![\p{L}\p{M}\p{N}])");

            return new Regex(pattern.ToString(), options);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Termine del glossario con le espressioni per il confronto
        /// </summary>
        /// <param name="Source">Termine del sorgente come nel glossario</param>
        /// <param name="Target">Resa come nel glossario</param>
        /// <param name="SourcePattern">Termine senza articolo, a parola intera, con le maiuscole esatte se la voce le chiede</param>
        /// <param name="TargetPattern">Resa senza articolo, flessa se non è in forma fissa, sul testo NFD</param>
        private sealed record TermPatterns(string Source, string Target, Regex SourcePattern, Regex TargetPattern);

        #endregion
    }
}
