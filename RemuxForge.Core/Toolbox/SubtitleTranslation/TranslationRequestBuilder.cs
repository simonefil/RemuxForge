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
            "- A source cue is a record. Its header is \"<id>\" or \"<id> [gN]\", then a tab and the first line of the cue. Each further line of the same cue starts with a tab.\n" +
            "- A translation record has the same header followed by \" =>\", then a tab and the translated text, with the same continuation lines. A header with nothing after \" =>\" means the cue has no accepted translation yet.\n" +
            "- A signal record (review only) has the header \"<id> !\", then a tab and one signal per line.\n" +
            "- In submit_translation, a line break inside a cue is \"\\n\" inside the JSON string.";

        /// <summary>
        /// Passo dell'analisi sulle forme di cortesia per le lingue senza forme grammaticali
        /// </summary>
        private const string GENERIC_ADDRESS_PROCEDURE = "Describe in register_rules how formal each recurring character is with the others.";

        /// <summary>
        /// Regola di traduzione sulle forme di cortesia per le lingue senza forme grammaticali
        /// </summary>
        private const string GENERIC_ADDRESS_RULE = "Keep the register of each speaker as described in register_rules.";

        /// <summary>
        /// Tool di consegna della traduzione
        /// </summary>
        public const string SUBMIT_TRANSLATION_TOOL = "submit_translation";

        /// <summary>
        /// Coppie accettate mostrate nella coda prima dell'intervallo richiesto
        /// </summary>
        public const int TAIL_PAIRS = 15;

        /// <summary>
        /// Instructions di B, B′ e B″: un solo testo, perché il ritentativo trovi la cache del prefisso
        /// </summary>
        private const string TRANSLATION_INSTRUCTIONS = "You are a professional subtitle translator from {source_language} to {target_language}.\n" +
            "The first message gives the approved glossary and the full source episode. The last message shows the latest accepted translations and names the cue ids to translate now.\n" +
            "This is not a chat: nobody reads plain text. Deliver only by calling submit_translation once, with one entry per requested id in ascending order.\n" +
            "When the last message lists rejected ids with error codes, deliver only the requested ids, fixing every listed error.\n" +
            "Report in new_terms every name or recurring term that is not among the glossary terms and that you had to render or keep, with the rendering you used. Leave new_terms empty when there are none.\n" +
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
            "- Line breaks are part of the cue text. Keep the number of lines of the source cue and break each line at a natural point in {target_language}. In a cue with two speakers each speaker keeps its own line and its leading dash.\n" +
            "- A formatted span appears as ⟦n text⟧, numbered from 1 within each cue. Return every span of the source cue exactly once, with the same number and in the same order, around the {target_language} words that correspond to the formatted words. Spans never nest and stay in their own cue.\n" +
            "- Write plain text: no markup, no tags, no braces, no angle brackets, no notes or explanations.\n" +
            "\n" +
            "Split sentences\n" +
            "- Cues marked [gN] form one sentence split across consecutive cues (group N). Translate the whole group as one sentence in natural {target_language}, then redistribute it over the same ids in order: every cue gets a non-empty, contiguous part of the sentence, roughly proportional to the source cue, broken at natural points. Words may move between cues of the same group; meaning stays inside the group.\n" +
            "\n" +
            "Editorial rules\n" +
            "- Write natural, idiomatic {target_language} subtitles. Render the dramatic function of each line rather than its words: jokes, idioms, insults and threats keep their effect and their strength, with no softening or censorship.\n" +
            "- Infer from the context and the glossary who speaks to whom, how many people are addressed, and the gender of speakers and addressees.\n" +
            "- {address_rule}\n" +
            "- Use the renderings of the glossary terms. Inflect a rendering only where {target_language} grammar requires it and the term is not marked invariable.\n" +
            "- Keep honorifics and name suffixes that appear in the source unless the glossary gives a rendering for them.\n" +
            "- Keep dialogue dashes and continuation ellipses where the source has them.\n" +
            "- Keep the subtitle concise: say what the line says, with no padding.";

        /// <summary>
        /// Definizione del tool submit_translation
        /// </summary>
        private const string SUBMIT_TRANSLATION_JSON = "{\"type\":\"function\",\"name\":\"submit_translation\"," +
            "\"description\":\"Deliver the translation of the requested cue ids: one entry per requested id, in ascending order. The code validates every cue and asks again only for the rejected ones.\"," +
            "\"parameters\":{\"type\":\"object\",\"properties\":{" +
            "\"translations\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}," +
            "\"text\":{\"type\":\"string\",\"description\":\"Translated cue text. Line breaks as \\\\n; every ⟦n text⟧ span of the source cue exactly once, in order.\"}}," +
            "\"required\":[\"id\",\"text\"],\"additionalProperties\":false}}," +
            "\"new_terms\":{\"type\":\"array\",\"description\":\"Names or recurring terms NOT among the glossary terms that you had to render, with the rendering you used.\"," +
            "\"items\":{\"type\":\"object\",\"properties\":{\"source\":{\"type\":\"string\"},\"target\":{\"type\":\"string\"},\"note\":{\"type\":\"string\"}}," +
            "\"required\":[\"source\",\"target\"],\"additionalProperties\":false}}}," +
            "\"required\":[\"translations\",\"new_terms\"],\"additionalProperties\":false}}";

        #endregion

        #region Variabili statiche

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
            return FormatRecord(cue.Id.Value, cue.Group, cue.CleanText);
        }

        /// <summary>
        /// Scrive una battuta congelata nel formato a record
        /// </summary>
        /// <param name="id">Id della battuta</param>
        /// <param name="group">Gruppo di frase spezzata, null fuori dai gruppi</param>
        /// <param name="text">Testo pulito</param>
        /// <returns>Record della battuta</returns>
        public static string FormatRecord(int id, int? group, string text)
        {
            return FormatHeader(id, group) + "\t" + text.Replace("\n", "\n\t");
        }

        /// <summary>
        /// Scrive un record di traduzione: testata seguita da « =>», poi tab e testo; senza testo resta la sola testata
        /// </summary>
        /// <param name="id">Id della battuta</param>
        /// <param name="group">Gruppo di frase spezzata, null fuori dai gruppi</param>
        /// <param name="text">Traduzione, null per una battuta senza traduzione accettata</param>
        /// <returns>Record di traduzione</returns>
        public static string FormatTranslationRecord(int id, int? group, string text)
        {
            string header = FormatHeader(id, group) + " =>";
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
            // Regola sulle forme di cortesia: quelle della lingua d'arrivo, o il registro di ogni personaggio dove la lingua non ne ha
            string[] forms = GetAddressForms(targetLanguage);
            string addressRule = forms == null ? GENERIC_ADDRESS_RULE : "Use the address form (" + string.Join(", ", forms) + ") the glossary gives for each speaker and addressee; " +
                s_pluralAddressRules[LanguageValidator.NormalizeToIso6392(targetLanguage)];
            string rules = TRANSLATION_RULES.Replace("{address_rule}", addressRule);
            return TRANSLATION_INSTRUCTIONS.Replace("{record_format}", RECORD_FORMAT).Replace("{translation_rules}", rules)
                .Replace("{source_language}", GetEnglishName(sourceLanguage)).Replace("{target_language}", GetEnglishName(targetLanguage));
        }

        /// <summary>
        /// Tool offerti da B, B′ e B″, sempre nello stesso ordine
        /// </summary>
        /// <returns>Array nuovo con submit_translation</returns>
        public static JsonArray CreateTranslationTools()
        {
            return new JsonArray(JsonNode.Parse(SUBMIT_TRANSLATION_JSON));
        }

        /// <summary>
        /// Messaggio di prefisso: glossario congelato e sorgente numerato intero, uguale byte per byte per tutto il job
        /// </summary>
        /// <param name="glossary">Glossario congelato del job</param>
        /// <param name="fileName">Nome del file sorgente</param>
        /// <param name="cues">Battute congelate del file</param>
        /// <returns>Testo del primo messaggio</returns>
        public static string CreateTranslationPrefix(Glossary glossary, string fileName, IReadOnlyList<FrozenCue> cues)
        {
            GlossaryBuilder builder = new GlossaryBuilder(glossary.SourceLanguage, glossary.TargetLanguage);
            StringBuilder message = new StringBuilder("GLOSSARY (approved):\n");
            message.Append(ToModelJson(builder.ToToolJson(glossary, true)));
            message.Append("\n\nSOURCE EPISODE: ").Append(fileName).Append(" (").Append(cues.Count.ToString(CultureInfo.InvariantCulture)).Append(" cues)");
            foreach (FrozenCue cue in cues)
                message.Append('\n').Append(FormatRecord(cue.Id, cue.Group, cue.Text));

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
                    message.Append('\n').Append(FormatRecord(pair.Key.Id, pair.Key.Group, pair.Key.Text));
                    message.Append('\n').Append(FormatTranslationRecord(pair.Key.Id, pair.Key.Group, pair.Value));
                }
                message.Append("\n\n");
            }

            List<int> ids = new List<int>();
            List<int> groups = new List<int>();
            foreach (FrozenCue cue in requested)
            {
                ids.Add(cue.Id);
                if (cue.Group.HasValue && !groups.Contains(cue.Group.Value))
                    groups.Add(cue.Group.Value);
            }

            message.Append("TRANSLATE cue ids ").Append(FormatRanges(ids)).Append(" (").Append(ids.Count.ToString(CultureInfo.InvariantCulture)).Append(" cues).");
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

            if (rejected.Count > 0)
                message.Append("\n\nREJECTED in the previous delivery of these ids:\n").Append(string.Join("\n", rejected));

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
        /// Testata di un record: id e, nei gruppi, [gN]
        /// </summary>
        /// <param name="id">Id della battuta</param>
        /// <param name="group">Gruppo di frase spezzata, null fuori dai gruppi</param>
        /// <returns>Testata</returns>
        private static string FormatHeader(int id, int? group)
        {
            string header = id.ToString(CultureInfo.InvariantCulture);
            if (group.HasValue)
                header += " [g" + group.Value.ToString(CultureInfo.InvariantCulture) + "]";

            return header;
        }

        #endregion
    }
}
