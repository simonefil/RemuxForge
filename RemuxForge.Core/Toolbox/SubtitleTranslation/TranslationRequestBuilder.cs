using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Parti comuni delle richieste della traduzione: formato a record delle battute, regole per lingua d'arrivo, nomi inglesi
    /// delle lingue nei template e serializzazione JSON leggibile del testo mandato al modello
    /// </summary>
    public static class TranslationRequestBuilder
    {
        #region Costanti

        /// <summary>
        /// Blocco «Record format», identico in tutte le instructions che lo usano
        /// </summary>
        public const string RECORD_FORMAT = "Record format\n" +
            "- A source cue is a record. Its header is \"<id>\", followed by \" [gN]\" for a split sentence and by \" [song]\" for song lyrics or \" [sign]\" for on-screen text such as signs and titles; a header without either is dialogue. Then a tab and the first line of the cue. Each further line of the same cue starts with a tab.\n" +
            "- A translation record has the same header followed by \" =>\", then a tab and the translated text, with the same continuation lines. A header with nothing after \" =>\" means the cue has no accepted translation yet.\n" +
            "- A signal record (review only) has the header \"<id> !\", then a tab and one signal per line.\n" +
            "- In submit_translation, a line break inside a cue is \"\\n\" inside the JSON string.";

        /// <summary>
        /// Tool di consegna della traduzione
        /// </summary>
        public const string SUBMIT_TRANSLATION_TOOL = "submit_translation";

        /// <summary>
        /// Tool di consegna della diagnosi
        /// </summary>
        public const string SUBMIT_DIAGNOSIS_TOOL = "submit_diagnosis";

        /// <summary>
        /// Coppie accettate mostrate nella coda prima dell'intervallo richiesto
        /// </summary>
        public const int TAIL_PAIRS = 15;

        /// <summary>
        /// Passo dell'analisi sulle forme di cortesia per le lingue senza forme grammaticali
        /// </summary>
        private const string GENERIC_ADDRESS_PROCEDURE = "Describe in register_rules how formal each recurring character is with the others.";

        /// <summary>
        /// Regola di traduzione sulle forme di cortesia per le lingue senza forme grammaticali
        /// </summary>
        private const string GENERIC_ADDRESS_RULE = "Keep the register of each speaker as described in register_rules.";

        /// <summary>
        /// Instructions di B, B′ e B″: un solo testo, perché il ritentativo trovi la cache del prefisso
        /// </summary>
        private const string TRANSLATION_INSTRUCTIONS = "You are a professional subtitle translator from {source_language} to {target_language}.\n" +
            "The first message gives the user's context for the series when there is one, the approved glossary and the full source episode. The last message shows the latest accepted translations and names the cue ids to translate now.\n" +
            "This is not a chat: nobody reads plain text. Deliver only by calling submit_translation once, with one entry per requested id in ascending order.\n" +
            "When the last message lists rejected ids with error codes, deliver only the requested ids, fixing every listed error.\n" +
            "Report in new_terms every name or recurring term that is not among the glossary terms and that you had to render or keep, with the rendering you used and its kind. Set invariable to true for names that never inflect and must appear verbatim. Leave new_terms empty when there are none.\n" +
            "\n" +
            "{record_format}\n" +
            "\n" +
            "{translation_rules}";

        /// <summary>
        /// Blocco «Translation rules», lo stesso in tutte le chiamate che traducono o riscrivono
        /// </summary>
        private const string TRANSLATION_RULES = "Translation rules\n" +
            "\n" +
            "Cues\n" +
            "- Deliver exactly one text for every requested id. Never merge, split, skip or add cues.\n" +
            "- Line breaks are part of the cue text. Break lines at natural points in {target_language}. In a cue with two speakers each speaker keeps its own line and its leading dash.\n" +
            "- A formatted span appears as ⟦n text⟧, numbered from 1 within each cue. Return every span of the source cue exactly once, with the same number and in the same order, around the {target_language} words that correspond to the formatted words. Spans never nest and stay in their own cue.\n" +
            "- Write plain text: no markup, no tags, no braces, no angle brackets, no notes or explanations.\n" +
            "\n" +
            "Split sentences\n" +
            "- Cues marked [gN] form one sentence split across consecutive cues (group N). Translate the whole group as one sentence in natural {target_language}, then redistribute it over the same ids in order: every cue gets a non-empty, contiguous part of the sentence, roughly proportional to the source cue, broken at natural points. Words may move between cues of the same group; meaning stays inside the group.\n" +
            "\n" +
            "Songs and signs\n" +
            "- Cues marked [song] are lyrics: translate them as lyrics in {target_language}, keeping the meaning and the line breaks of each source cue.\n" +
            "- Cues marked [sign] are on-screen text: translate them short, as a sign or title in {target_language} would read.\n" +
            "\n" +
            "Editorial rules\n" +
            "- Write natural, idiomatic {target_language} subtitles. Render the dramatic function of each line rather than its words: jokes, idioms, insults, threats, swearing and crude words keep their effect and their strength, and every swear word or crude word of the source gets an equally crude word in the translation rather than a neutral paraphrase, with no softening or censorship.\n" +
            "- Infer from the context and the glossary who speaks to whom, how many people are addressed, and the gender of speakers and addressees.\n" +
            "- {address_rule}\n" +
            "- Use the renderings of the glossary terms. Inflect a rendering only where {target_language} grammar requires it and the term is not marked invariable.\n" +
            "- Keep honorifics and name suffixes that appear in the source unless the glossary gives a rendering for them.\n" +
            "- Keep dialogue dashes and continuation ellipses where the source has them.\n" +
            "- Keep the subtitle concise: say what the line says, with no padding.";

        /// <summary>
        /// Instructions di C: riscrittura delle battute coinvolte dai termini nuovi approvati
        /// </summary>
        private const string REWRITE_INSTRUCTIONS = "You apply updated glossary terms to {target_language} subtitles already translated from {source_language}.\n" +
            "The first message gives the user's context for the series when there is one, the updated glossary and the full source episode. The last message lists the updated renderings and, for every cue to rewrite, its source and its current translation.\n" +
            "This is not a chat: nobody reads plain text. Deliver only by calling submit_translation once, with one entry per requested id in ascending order, and new_terms empty.\n" +
            "For each cue change only what the updated renderings require: the term itself and the words that agree with it. Keep everything else as it is.\n" +
            "When the last message lists rejected ids with error codes, deliver only the requested ids, fixing every listed error.\n" +
            "\n" +
            "{record_format}\n" +
            "\n" +
            "{translation_rules}";

        /// <summary>
        /// Instructions di D1: diagnosi di un blocco tradotto
        /// </summary>
        private const string DIAGNOSIS_INSTRUCTIONS = "You review {target_language} subtitles translated from {source_language}, cue by cue, with the source beside them.\n" +
            "The first message gives the user's context for the series when there is one, the approved glossary and the full source episode. The last message gives one block: for every cue its source record, its current translation record and, when the code noticed something, a signal record.\n" +
            "This is not a chat: nobody reads plain text. Deliver only by calling submit_diagnosis once. Deliver an empty issues list when the block needs no change.\n" +
            "\n" +
            "Report real problems only, with these categories:\n" +
            "- accuracy: meaning changed, mistranslation, false friend, literal calque, a set phrase rendered with the wrong sense;\n" +
            "- omission: content of the source lost, or content added;\n" +
            "- fluency: unidiomatic or ungrammatical {target_language};\n" +
            "- terminology: a rendering of the glossary terms missing or misused;\n" +
            "- register: address form against the glossary, wrong gender or number of speakers or addressees, insults, threats, swearing or crude language of the source rendered weaker, more formal, censored or dropped (a translation as strong as the source or stronger is not an issue);\n" +
            "- consistency: the same thing rendered differently within the block or against the glossary;\n" +
            "- split_sentence: a [gN] group badly redistributed over its cues;\n" +
            "- formatting: a ⟦n text⟧ span around the wrong words;\n" +
            "- untranslated: {source_language} text left by mistake, apart from names and terms the glossary keeps.\n" +
            "\n" +
            "Signals point to cues worth checking. A signalled cue can be correct, and a correct cue stays as it is: report an issue only when a change makes the subtitle better. A cue with no accepted translation needs no issue: the correction step translates it.\n" +
            "For each issue give the cue id, the category, the severity (minor, major, critical), a short explanation in {notes_language} and, when you can, a suggestion with the full corrected text of the cue.\n" +
            "\n" +
            "{record_format}";

        /// <summary>
        /// Instructions di D2: correzione delle battute diagnosticate e traduzione delle «da rivedere»
        /// </summary>
        private const string CORRECTION_INSTRUCTIONS = "You correct {target_language} subtitles translated from {source_language}, using a list of diagnosed issues and the source.\n" +
            "The first message gives the user's context for the series when there is one, the approved glossary and the full source episode. The last message lists the diagnosed issues, then every cue to deliver with its source and its current translation.\n" +
            "This is not a chat: nobody reads plain text. Deliver only by calling submit_translation once, with one entry per requested id in ascending order, and new_terms empty.\n" +
            "Fix every listed issue and keep the rest of each cue as it is. Translate from scratch every cue with no accepted translation. A listed cue with no issue of its own belongs to a [gN] group: deliver it unchanged unless fixing the group requires a change.\n" +
            "When the last message lists rejected ids with error codes, deliver only the requested ids, fixing every listed error.\n" +
            "\n" +
            "{record_format}\n" +
            "\n" +
            "{translation_rules}";

        /// <summary>
        /// Definizione del tool submit_diagnosis
        /// </summary>
        private const string SUBMIT_DIAGNOSIS_JSON = "{\"type\":\"function\",\"name\":\"submit_diagnosis\",\"strict\":true," +
            "\"description\":\"Report the problems found in the translation of the block. Empty list if there are none.\"," +
            "\"parameters\":{\"type\":\"object\",\"properties\":{" +
            "\"issues\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}," +
            "\"category\":{\"type\":\"string\",\"enum\":[\"accuracy\",\"omission\",\"fluency\",\"terminology\",\"register\",\"consistency\",\"split_sentence\",\"formatting\",\"untranslated\"]}," +
            "\"severity\":{\"type\":\"string\",\"enum\":[\"minor\",\"major\",\"critical\"]}," +
            "\"explanation\":{\"type\":\"string\",\"description\":\"Short explanation for the user.\"}," +
            "\"suggestion\":{\"type\":[\"string\",\"null\"],\"description\":\"Full corrected text of the cue, with line breaks as \\\\n and its ⟦n text⟧ spans; null if you have none.\"}}," +
            "\"required\":[\"id\",\"category\",\"severity\",\"explanation\",\"suggestion\"],\"additionalProperties\":false}}}," +
            "\"required\":[\"issues\"],\"additionalProperties\":false}}";

        /// <summary>
        /// Definizione del tool submit_translation
        /// </summary>
        private const string SUBMIT_TRANSLATION_JSON = "{\"type\":\"function\",\"name\":\"submit_translation\",\"strict\":true," +
            "\"description\":\"Deliver the translation of the requested cue ids: one entry per requested id, in ascending order. The code validates every cue and asks again only for the rejected ones.\"," +
            "\"parameters\":{\"type\":\"object\",\"properties\":{" +
            "\"translations\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}," +
            "\"text\":{\"type\":\"string\",\"description\":\"Translated cue text. Line breaks as \\\\n; every ⟦n text⟧ span of the source cue exactly once, in order.\"}}," +
            "\"required\":[\"id\",\"text\"],\"additionalProperties\":false}}," +
            "\"new_terms\":{\"type\":\"array\",\"description\":\"Names or recurring terms NOT among the glossary terms that you had to render, with the rendering you used.\"," +
            "\"items\":{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\"},\"target\":{\"type\":\"string\"}," +
            "\"kind\":{\"type\":\"string\",\"enum\":[\"name\",\"place\",\"organization\",\"title\",\"term\",\"catchphrase\"]}," +
            "\"invariable\":{\"type\":\"boolean\",\"description\":\"True for proper names that never inflect and must appear verbatim.\"},\"note\":{\"type\":[\"string\",\"null\"]}}," +
            "\"required\":[\"source\",\"target\",\"kind\",\"invariable\",\"note\"],\"additionalProperties\":false}}}," +
            "\"required\":[\"translations\",\"new_terms\"],\"additionalProperties\":false}}";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Categorie ammesse di un problema della diagnosi
        /// </summary>
        public static readonly IReadOnlyList<string> DIAGNOSIS_CATEGORIES = new string[] { "accuracy", "omission", "fluency", "terminology", "register", "consistency", "split_sentence", "formatting", "untranslated" };

        /// <summary>
        /// Gravità ammesse di un problema della diagnosi
        /// </summary>
        public static readonly IReadOnlyList<string> DIAGNOSIS_SEVERITIES = new string[] { "minor", "major", "critical" };

        /// <summary>
        /// Forme di cortesia per lingua d'arrivo ISO 639-2; le lingue assenti non hanno forme e il glossario non ha addressing
        /// </summary>
        private static readonly Dictionary<string, string[]> s_addressForms = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "ita", new string[] { "tu", "lei", "voi" } },
            { "fra", new string[] { "tu", "vous" } },
            { "deu", new string[] { "du", "Sie", "ihr" } },
            { "spa", new string[] { "tú", "usted", "vosotros", "ustedes" } }
        };

        /// <summary>
        /// Parte della regola sulle forme di cortesia che dice quali forme vanno a più persone
        /// </summary>
        private static readonly Dictionary<string, string> s_pluralAddressRules = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "ita", "voi addresses more than one person." },
            { "fra", "vous also addresses more than one person." },
            { "deu", "ihr addresses more than one person." },
            { "spa", "vosotros and ustedes address more than one person." }
        };

        /// <summary>
        /// Descrizione dell'enum delle forme nello schema, dove la lingua la fissa
        /// </summary>
        private static readonly Dictionary<string, string> s_addressFormDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "ita", "tu: informal; lei: formal; voi: plural or archaic reverential." }
        };

        /// <summary>
        /// Opzioni del JSON mandato al modello: rientro di uno spazio e caratteri non ASCII leggibili
        /// </summary>
        private static readonly JsonSerializerOptions s_modelJson = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentSize = 1,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive una battuta nel formato a record: testata con id e gruppo, tab, prima riga; le righe seguenti cominciano col tab
        /// </summary>
        /// <param name="cue">Battuta traducibile</param>
        /// <returns>Record della battuta</returns>
        public static string FormatRecord(SubtitleCue cue)
        {
            return FormatRecord(cue.Id.Value, cue.Group, cue.Kind, cue.CleanText);
        }

        /// <summary>
        /// Scrive una battuta congelata nel formato a record
        /// </summary>
        /// <param name="id">Id della battuta</param>
        /// <param name="group">Gruppo di frase spezzata, null fuori dai gruppi</param>
        /// <param name="kind">Tipo della battuta</param>
        /// <param name="text">Testo pulito</param>
        /// <returns>Record della battuta</returns>
        public static string FormatRecord(int id, int? group, SubtitleCueKind kind, string text)
        {
            return FormatHeader(id, group, kind) + "\t" + text.Replace("\n", "\n\t");
        }

        /// <summary>
        /// Scrive un record di traduzione: testata seguita da « =>», poi tab e testo; senza testo resta la sola testata
        /// </summary>
        /// <param name="id">Id della battuta</param>
        /// <param name="group">Gruppo di frase spezzata, null fuori dai gruppi</param>
        /// <param name="kind">Tipo della battuta</param>
        /// <param name="text">Traduzione, null per una battuta senza traduzione accettata</param>
        /// <returns>Record di traduzione</returns>
        public static string FormatTranslationRecord(int id, int? group, SubtitleCueKind kind, string text)
        {
            string header = FormatHeader(id, group, kind) + " =>";
            return text == null ? header : header + "\t" + text.Replace("\n", "\n\t");
        }

        /// <summary>
        /// Scrive gli id come intervalli, per esempio 39-98 o 44,61-63
        /// </summary>
        /// <param name="ids">Id, in qualunque ordine</param>
        /// <returns>Intervalli in ordine crescente</returns>
        public static string FormatRanges(IReadOnlyList<int> ids)
        {
            List<int> sorted = new List<int>(ids);
            sorted.Sort();
            StringBuilder ranges = new StringBuilder();
            for (int i = 0; i < sorted.Count; i++)
            {
                int start = i;
                while (i + 1 < sorted.Count && sorted[i + 1] == sorted[i] + 1)
                    i++;
                if (ranges.Length > 0)
                    ranges.Append(',');
                ranges.Append(sorted[start].ToString(CultureInfo.InvariantCulture));
                if (i > start)
                    ranges.Append('-').Append(sorted[i].ToString(CultureInfo.InvariantCulture));
            }

            return ranges.ToString();
        }

        /// <summary>
        /// Scrive il sorgente numerato: i record di tutte le battute traducibili, in ordine di id
        /// </summary>
        /// <param name="cues">Battute del file, escluse e copie comprese</param>
        /// <returns>Sorgente numerato, una riga o più per battuta</returns>
        public static string FormatSource(IReadOnlyList<SubtitleCue> cues)
        {
            StringBuilder source = new StringBuilder();
            foreach (SubtitleCue cue in cues)
            {
                if (!cue.Id.HasValue)
                    continue;

                if (source.Length > 0)
                    source.Append('\n');
                source.Append(FormatRecord(cue));
            }

            return source.ToString();
        }

        /// <summary>
        /// Nome inglese di una lingua per i template
        /// </summary>
        /// <param name="language">Codice ISO 639-2</param>
        /// <returns>Nome inglese, oppure il codice se il runtime non conosce la lingua</returns>
        public static string GetEnglishName(string language)
        {
            string code = LanguageValidator.NormalizeToIso6392(language);
            foreach (CultureInfo culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                if (string.Equals(culture.ThreeLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(culture.Name))
                    return culture.EnglishName;
            }

            return code;
        }

        /// <summary>
        /// Nome inglese della lingua dell'interfaccia, in cui il modello scrive i testi che legge l'utente
        /// </summary>
        /// <returns>Nome inglese della lingua dell'app</returns>
        public static string GetNotesLanguage()
        {
            return CultureInfo.GetCultureInfo(AppText.Language).EnglishName;
        }

        /// <summary>
        /// Forme di cortesia della lingua d'arrivo
        /// </summary>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        /// <returns>Forme, null per una lingua senza forme grammaticali</returns>
        public static string[] GetAddressForms(string targetLanguage)
        {
            string[] forms;
            return s_addressForms.TryGetValue(LanguageValidator.NormalizeToIso6392(targetLanguage), out forms) ? forms : null;
        }

        /// <summary>
        /// Descrizione delle forme di cortesia nello schema del glossario
        /// </summary>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        /// <returns>Descrizione, vuota dove la lingua non la fissa</returns>
        public static string GetAddressFormDescription(string targetLanguage)
        {
            string description;
            return s_addressFormDescriptions.TryGetValue(LanguageValidator.NormalizeToIso6392(targetLanguage), out description) ? description : "";
        }

        /// <summary>
        /// Passo dell'analisi sulle forme di cortesia della lingua d'arrivo
        /// </summary>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        /// <returns>Testo del passo per {address_procedure}</returns>
        public static string GetAddressProcedure(string targetLanguage)
        {
            string[] forms = GetAddressForms(targetLanguage);
            if (forms == null)
                return GENERIC_ADDRESS_PROCEDURE;

            // Articolo inglese davanti al nome della lingua: an Italian, a German
            string name = GetEnglishName(targetLanguage);
            string article = "AEIOU".IndexOf(char.ToUpperInvariant(name[0])) >= 0 ? "an " : "a ";
            return "Decide the address form (" + string.Join(", ", forms) + ") between the recurring characters as " + article + name + " adaptation would, and fill addressing.";
        }

        /// <summary>
        /// Instructions di B, B′ e B″ coi segnaposto risolti: uguali per tutto il job
        /// </summary>
        /// <param name="sourceLanguage">Lingua d'origine ISO 639-2</param>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        /// <returns>Testo delle instructions</returns>
        public static string CreateTranslationInstructions(string sourceLanguage, string targetLanguage)
        {
            return FillInstructions(TRANSLATION_INSTRUCTIONS, sourceLanguage, targetLanguage);
        }

        /// <summary>
        /// Instructions di C coi segnaposto risolti
        /// </summary>
        /// <param name="sourceLanguage">Lingua d'origine ISO 639-2</param>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        /// <returns>Testo delle instructions</returns>
        public static string CreateRewriteInstructions(string sourceLanguage, string targetLanguage)
        {
            return FillInstructions(REWRITE_INSTRUCTIONS, sourceLanguage, targetLanguage);
        }

        /// <summary>
        /// Instructions di D1 coi segnaposto risolti, con le spiegazioni nella lingua dell'interfaccia
        /// </summary>
        /// <param name="sourceLanguage">Lingua d'origine ISO 639-2</param>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        /// <returns>Testo delle instructions</returns>
        public static string CreateDiagnosisInstructions(string sourceLanguage, string targetLanguage)
        {
            return FillInstructions(DIAGNOSIS_INSTRUCTIONS.Replace("{notes_language}", GetNotesLanguage()), sourceLanguage, targetLanguage);
        }

        /// <summary>
        /// Instructions di D2 coi segnaposto risolti
        /// </summary>
        /// <param name="sourceLanguage">Lingua d'origine ISO 639-2</param>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        /// <returns>Testo delle instructions</returns>
        public static string CreateCorrectionInstructions(string sourceLanguage, string targetLanguage)
        {
            return FillInstructions(CORRECTION_INSTRUCTIONS, sourceLanguage, targetLanguage);
        }

        /// <summary>
        /// Tool offerto da D1
        /// </summary>
        /// <returns>Array nuovo con submit_diagnosis</returns>
        public static JsonArray CreateDiagnosisTools()
        {
            return new JsonArray(JsonNode.Parse(SUBMIT_DIAGNOSIS_JSON));
        }

        /// <summary>
        /// Scrive un record di segnali: testata «id !», tab e un segnale per riga
        /// </summary>
        /// <param name="id">Id della battuta</param>
        /// <param name="signals">Segnali nella forma CODICE: dettaglio</param>
        /// <returns>Record dei segnali</returns>
        public static string FormatSignalRecord(int id, IReadOnlyList<string> signals)
        {
            return id.ToString(CultureInfo.InvariantCulture) + " !\t" + string.Join("\n\t", signals);
        }

        /// <summary>
        /// Tool offerti da B, B′, B″, C e D2, sempre nello stesso ordine
        /// </summary>
        /// <returns>Array nuovo con submit_translation</returns>
        public static JsonArray CreateTranslationTools()
        {
            return new JsonArray(JsonNode.Parse(SUBMIT_TRANSLATION_JSON));
        }

        /// <summary>
        /// Messaggio di prefisso: contesto della serie, glossario del job e sorgente numerato del file, uguale byte per byte fra le
        /// chiamate di un file con lo stesso contesto e lo stesso glossario. Prima ciò che vale per tutto il progetto, poi ciò che
        /// vale per il file: il contesto modificato apre semplicemente un prefisso nuovo
        /// </summary>
        /// <param name="context">Campo serie/contesto del progetto, vuoto se l'utente non l'ha scritto</param>
        /// <param name="glossary">Glossario del job: congelato in traduzione, aggiornato dai termini nuovi dopo</param>
        /// <param name="fileName">Nome del file sorgente</param>
        /// <param name="cues">Battute congelate del file, senza le copie dei cartelli in movimento</param>
        /// <returns>Testo del primo messaggio</returns>
        public static string CreateTranslationPrefix(string context, Glossary glossary, string fileName, IReadOnlyList<FrozenCue> cues)
        {
            GlossaryBuilder builder = new GlossaryBuilder(glossary.SourceLanguage, glossary.TargetLanguage);
            StringBuilder message = new StringBuilder();
            string trimmed = (context ?? "").Trim();
            if (trimmed.Length > 0)
                message.Append("SERIES CONTEXT (from the user):\n").Append(trimmed).Append("\n\n");
            message.Append("GLOSSARY (approved):\n");
            message.Append(ToModelJson(builder.ToToolJson(glossary, true)));
            message.Append("\n\nSOURCE EPISODE: ").Append(fileName).Append(" (").Append(cues.Count.ToString(CultureInfo.InvariantCulture)).Append(" cues)");
            foreach (FrozenCue cue in cues)
                message.Append('\n').Append(FormatRecord(cue.Id, cue.Group, cue.Kind, cue.Text));

            return message.ToString();
        }

        /// <summary>
        /// Coda di B, B′ e B″: coppie accettate prima dell'intervallo, id richiesti coi gruppi e, per il ritentativo, gli scarti
        /// </summary>
        /// <param name="accepted">Ultime battute accettate prima del primo id richiesto, con la traduzione</param>
        /// <param name="requested">Battute richieste, in ordine di id</param>
        /// <param name="rejected">Righe del validatore della consegna precedente, vuote per B e B″</param>
        /// <returns>Testo dell'ultimo messaggio</returns>
        public static string CreateTranslationTail(IReadOnlyList<KeyValuePair<FrozenCue, string>> accepted, IReadOnlyList<FrozenCue> requested, IReadOnlyList<string> rejected)
        {
            StringBuilder message = new StringBuilder();
            if (accepted.Count > 0)
            {
                message.Append("ACCEPTED TRANSLATIONS BEFORE THIS RANGE (latest ").Append(TAIL_PAIRS.ToString(CultureInfo.InvariantCulture)).Append("):");
                foreach (KeyValuePair<FrozenCue, string> pair in accepted)
                {
                    message.Append('\n').Append(FormatRecord(pair.Key.Id, pair.Key.Group, pair.Key.Kind, pair.Key.Text));
                    message.Append('\n').Append(FormatTranslationRecord(pair.Key.Id, pair.Key.Group, pair.Key.Kind, pair.Value));
                }

                message.Append("\n\n");
            }

            AppendRequested(message, "TRANSLATE", requested);
            AppendRejected(message, rejected);
            return message.ToString();
        }

        /// <summary>
        /// Coda di C: rese aggiornate, id da riscrivere coi gruppi e battute con la traduzione attuale; nel ritentativo gli scarti
        /// </summary>
        /// <param name="terms">Termini approvati al passo dei termini nuovi, originale e resa</param>
        /// <param name="requested">Battute da riscrivere, in ordine di id</param>
        /// <param name="current">Traduzione attuale per id</param>
        /// <param name="rejected">Righe del validatore della consegna precedente, vuote alla prima consegna</param>
        /// <returns>Testo dell'ultimo messaggio</returns>
        public static string CreateRewriteTail(IReadOnlyList<KeyValuePair<string, string>> terms, IReadOnlyList<FrozenCue> requested, IReadOnlyDictionary<int, string> current,
            IReadOnlyList<string> rejected)
        {
            StringBuilder message = new StringBuilder("UPDATED TERMS (apply these renderings):");
            foreach (KeyValuePair<string, string> term in terms)
                message.Append('\n').Append(term.Key).Append(" => ").Append(term.Value);

            message.Append("\n\n");
            AppendRequested(message, "REWRITE", requested);
            AppendCurrent(message, requested, current);
            AppendRejected(message, rejected);
            return message.ToString();
        }

        /// <summary>
        /// Coda di D1: id del blocco coi gruppi e, per ogni battuta, sorgente, traduzione attuale e segnali
        /// </summary>
        /// <param name="cues">Battute del blocco, in ordine di id</param>
        /// <param name="current">Traduzione attuale per id, null per una battuta senza testo accettato</param>
        /// <param name="signals">Segnali per id, assenti per le battute senza segnali</param>
        /// <returns>Testo dell'ultimo messaggio</returns>
        public static string CreateDiagnosisTail(IReadOnlyList<FrozenCue> cues, IReadOnlyDictionary<int, string> current, IReadOnlyDictionary<int, List<string>> signals)
        {
            StringBuilder message = new StringBuilder();
            AppendRequested(message, "REVIEW", cues);
            message.Append('\n');
            foreach (FrozenCue cue in cues)
            {
                string text;
                current.TryGetValue(cue.Id, out text);
                message.Append('\n').Append(FormatRecord(cue.Id, cue.Group, cue.Kind, cue.Text));
                message.Append('\n').Append(FormatTranslationRecord(cue.Id, cue.Group, cue.Kind, text));
                List<string> cueSignals;
                if (signals.TryGetValue(cue.Id, out cueSignals) && cueSignals.Count > 0)
                    message.Append('\n').Append(FormatSignalRecord(cue.Id, cueSignals));
            }

            return message.ToString();
        }

        /// <summary>
        /// Coda di D2: problemi diagnosticati, id da consegnare coi gruppi e battute con la traduzione attuale; nel ritentativo gli scarti
        /// </summary>
        /// <param name="issues">Problemi della diagnosi sulle battute richieste</param>
        /// <param name="requested">Battute da consegnare, in ordine di id</param>
        /// <param name="current">Traduzione attuale per id, null per una battuta senza testo accettato</param>
        /// <param name="rejected">Righe del validatore della consegna precedente, vuote alla prima consegna</param>
        /// <returns>Testo dell'ultimo messaggio</returns>
        public static string CreateCorrectionTail(IReadOnlyList<TranslationReviewIssue> issues, IReadOnlyList<FrozenCue> requested, IReadOnlyDictionary<int, string> current,
            IReadOnlyList<string> rejected)
        {
            StringBuilder message = new StringBuilder("DIAGNOSED ISSUES:");
            foreach (TranslationReviewIssue issue in issues)
            {
                message.Append('\n').Append(issue.Id.ToString(CultureInfo.InvariantCulture)).Append(" [").Append(issue.Category).Append('/').Append(issue.Severity).Append("] ").Append(issue.Explanation);
                if (!string.IsNullOrEmpty(issue.Suggestion))
                    message.Append("\n\tsuggestion: ").Append(issue.Suggestion.Replace("\n", "\n\t"));
            }

            message.Append("\n\n");
            AppendRequested(message, "DELIVER", requested);
            AppendCurrent(message, requested, current);
            AppendRejected(message, rejected);
            return message.ToString();
        }

        /// <summary>
        /// Serializza un nodo JSON per il testo dei messaggi al modello
        /// </summary>
        /// <param name="node">Nodo da scrivere</param>
        /// <returns>JSON rientrato di uno spazio, con accenti e segni leggibili</returns>
        public static string ToModelJson(JsonNode node)
        {
            return node.ToJsonString(s_modelJson);
        }

        /// <summary>
        /// Crea un messaggio user della Responses API
        /// </summary>
        /// <param name="text">Testo del messaggio</param>
        /// <returns>Item del messaggio</returns>
        public static JsonObject CreateUserMessage(string text)
        {
            return new JsonObject { ["role"] = "user", ["content"] = text };
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Risolve i segnaposto comuni delle instructions: formato a record, regole di traduzione con le forme di cortesia, lingue
        /// </summary>
        /// <param name="template">Template delle instructions</param>
        /// <param name="sourceLanguage">Lingua d'origine ISO 639-2</param>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        /// <returns>Testo delle instructions</returns>
        private static string FillInstructions(string template, string sourceLanguage, string targetLanguage)
        {
            // Regola sulle forme di cortesia: quelle della lingua d'arrivo, o il registro di ogni personaggio dove la lingua non ne ha
            string[] forms = GetAddressForms(targetLanguage);
            string addressRule = forms == null ? GENERIC_ADDRESS_RULE : "Use the address form (" + string.Join(", ", forms) + ") the glossary gives for each speaker and addressee; " +
                s_pluralAddressRules[LanguageValidator.NormalizeToIso6392(targetLanguage)];
            string rules = TRANSLATION_RULES.Replace("{address_rule}", addressRule);
            return template.Replace("{record_format}", RECORD_FORMAT).Replace("{translation_rules}", rules)
                .Replace("{source_language}", GetEnglishName(sourceLanguage)).Replace("{target_language}", GetEnglishName(targetLanguage));
        }

        /// <summary>
        /// Aggiunge la riga degli id richiesti col loro numero e, se ce ne sono, quella dei gruppi di frase spezzata
        /// </summary>
        /// <param name="message">Coda in costruzione</param>
        /// <param name="verb">TRANSLATE, REWRITE, REVIEW o DELIVER</param>
        /// <param name="requested">Battute richieste, in ordine di id</param>
        private static void AppendRequested(StringBuilder message, string verb, IReadOnlyList<FrozenCue> requested)
        {
            List<int> ids = new List<int>();
            List<int> groups = new List<int>();
            foreach (FrozenCue cue in requested)
            {
                ids.Add(cue.Id);
                if (cue.Group.HasValue && !groups.Contains(cue.Group.Value))
                    groups.Add(cue.Group.Value);
            }

            message.Append(verb).Append(" cue ids ").Append(FormatRanges(ids)).Append(" (").Append(ids.Count.ToString(CultureInfo.InvariantCulture)).Append(" cues).");
            if (groups.Count > 0)
            {
                List<string> parts = new List<string>();
                foreach (int group in groups)
                {
                    List<string> members = new List<string>();
                    foreach (FrozenCue cue in requested)
                    {
                        if (cue.Group == group)
                            members.Add(cue.Id.ToString(CultureInfo.InvariantCulture));
                    }

                    parts.Add("g" + group.ToString(CultureInfo.InvariantCulture) + " = " + string.Join(",", members));
                }

                message.Append("\nSplit-sentence groups in this range: ").Append(string.Join("; ", parts));
            }
        }

        /// <summary>
        /// Aggiunge la sezione delle traduzioni attuali: per ogni battuta il record sorgente e quello di traduzione
        /// </summary>
        /// <param name="message">Coda in costruzione</param>
        /// <param name="requested">Battute richieste, in ordine di id</param>
        /// <param name="current">Traduzione attuale per id, null o assente per una battuta senza testo accettato</param>
        private static void AppendCurrent(StringBuilder message, IReadOnlyList<FrozenCue> requested, IReadOnlyDictionary<int, string> current)
        {
            message.Append("\n\nCURRENT TRANSLATIONS:");
            foreach (FrozenCue cue in requested)
            {
                string text;
                current.TryGetValue(cue.Id, out text);
                message.Append('\n').Append(FormatRecord(cue.Id, cue.Group, cue.Kind, cue.Text));
                message.Append('\n').Append(FormatTranslationRecord(cue.Id, cue.Group, cue.Kind, text));
            }
        }

        /// <summary>
        /// Aggiunge in fondo gli scarti della consegna precedente, se ce ne sono
        /// </summary>
        /// <param name="message">Coda in costruzione</param>
        /// <param name="rejected">Righe del validatore</param>
        private static void AppendRejected(StringBuilder message, IReadOnlyList<string> rejected)
        {
            if (rejected.Count > 0)
                message.Append("\n\nREJECTED in the previous delivery of these ids:\n").Append(string.Join("\n", rejected));
        }

        /// <summary>
        /// Testata di un record: id, nei gruppi [gN], per canzoni e cartelli [song] o [sign]; il dialogo non ha segno, così i
        /// record dei job salvati prima dei tipi restano uguali
        /// </summary>
        /// <param name="id">Id della battuta</param>
        /// <param name="group">Gruppo di frase spezzata, null fuori dai gruppi</param>
        /// <param name="kind">Tipo della battuta</param>
        /// <returns>Testata</returns>
        private static string FormatHeader(int id, int? group, SubtitleCueKind kind)
        {
            string header = id.ToString(CultureInfo.InvariantCulture);
            if (group.HasValue)
                header += " [g" + group.Value.ToString(CultureInfo.InvariantCulture) + "]";
            if (kind == SubtitleCueKind.Song)
                header += " [song]";
            else if (kind == SubtitleCueKind.Sign)
                header += " [sign]";

            return header;
        }

        #endregion
    }
}
