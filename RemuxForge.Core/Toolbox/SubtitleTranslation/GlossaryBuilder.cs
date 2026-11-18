using RemuxForge.Core.Ai;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Chiamate del glossario: instructions, tool e messaggi di A1 e A2, validazione della consegna di submit_glossary con la
    /// conversione nella forma del progetto, fusione dei glossari parziali e fusione col glossario caricato
    /// </summary>
    public class GlossaryBuilder
    {
        #region Costanti

        /// <summary>
        /// Nome del tool di consegna
        /// </summary>
        public const string SUBMIT_TOOL = "submit_glossary";

        /// <summary>
        /// Richieste con risposta al massimo per una sequenza di A0, A1 o A2
        /// </summary>
        public const int MAX_ROUNDS = 3;

        /// <summary>
        /// Token stimati sotto cui tutti i file vanno in una sola A1
        /// </summary>
        public const int SINGLE_ANALYSIS_TOKENS = 30000;

        /// <summary>
        /// Caratteri per token nella stima dell'ingresso
        /// </summary>
        public const int CHARS_PER_TOKEN = 4;

        /// <summary>
        /// Messaggio di consegna mancante
        /// </summary>
        private const string MISSING_SUBMISSION = "This is not a chat: nobody will answer questions. Make reasonable choices, then call submit_glossary with the complete glossary.";

        /// <summary>
        /// Template delle instructions di A1
        /// </summary>
        private const string ANALYSIS_TEMPLATE = "You prepare the glossary used to translate the subtitles of a series from {source_language} to {target_language}, episode by episode, with consistent names, terms and register. The glossary holds the terms, the characters, the addressing between characters, the tone and the register rules of the series.\n" +
            "This is not a chat: nobody reads plain text or answers questions. Deliver only by calling submit_glossary with the complete glossary. If the call returns errors, fix them and call submit_glossary again with the complete glossary.\n" +
            "\n" +
            "Input\n" +
            "- The user message gives the user's context for the series, the recurring candidates extracted by code with their counts per episode file, and the full source of one or more episodes, one record per cue.\n" +
            "- When the user loaded a glossary, the user message also gives it as LOADED GLOSSARY. Its terms, characters and addressing entries are already decided.\n" +
            "\n" +
            "Procedure\n" +
            "1. Read every episode in full.\n" +
            "2. When web search is available, use it to identify the series and to confirm the official {target_language} renderings (official dub or subtitles) of names, places, organizations, titles and recurring terms, and the genders and relationships of the characters.\n" +
            "3. Put in terms every proper name and recurring term of the candidate list that really is a name or a term, plus the ones you find in the episodes: places, organizations, titles, songs, recaps and previews, techniques, nicknames, forms of address and catchphrases. Decide each {target_language} rendering. Names usually stay as they are; titles, techniques, nicknames and catchphrases take the rendering an official {target_language} adaptation uses or would use.\n" +
            "4. Describe every recurring character: gender, role, relationships.\n" +
            "5. {address_procedure}\n" +
            "6. Describe the tone of the dialogue and how to render it in {target_language}.\n" +
            "\n" +
            "Rules\n" +
            "- Write source as it appears in the episodes and target as it must appear in the translation, both without a leading article.\n" +
            "- target equals source when the term stays untranslated. Set invariable to true for names that never inflect and must appear verbatim.\n" +
            "- When several renderings are defensible, choose one and list the alternatives in note.\n" +
            "- Write series, tone, register_rules, note, role and relationships in {notes_language}, as plain text: no citations, links or source markers.\n" +
            "- The LOADED GLOSSARY entries are fixed. Deliver only the terms, characters and addressing entries it lacks. Use its renderings as the reference for the new ones. Keep series, tone and register_rules consistent with it.\n" +
            "- With a LOADED GLOSSARY the complete glossary you deliver is series, tone, register_rules and the missing entries only; empty lists are valid when nothing is missing.\n" +
            "\n" +
            "{record_format}";

        /// <summary>
        /// Template delle instructions di A2
        /// </summary>
        private const string MERGE_TEMPLATE = "You resolve the conflicts between partial glossaries of one series, translated from {source_language} to {target_language}. Each partial glossary was prepared from one episode file. The code already merged the entries on which the episodes agree.\n" +
            "This is not a chat: nobody reads plain text or answers questions. Deliver only by calling submit_glossary. If the call returns errors, fix them and call submit_glossary again with the complete delivery.\n" +
            "\n" +
            "Input\n" +
            "- AGREED ENTRIES are already decided: do not deliver them again.\n" +
            "- CONFLICTS lists, for each term, character or addressing pair, the variants the episodes proposed, with the episodes that proposed each one.\n" +
            "- PARTIAL TEXTS gives series, tone and register_rules of each partial glossary.\n" +
            "\n" +
            "Rules\n" +
            "- Deliver exactly one entry for every conflict, and nothing else in terms, characters and addressing.\n" +
            "- For a term, keep the variant a note marks as official; otherwise the one more episodes proposed, with the candidate counts to break ties. List the discarded renderings in note.\n" +
            "- For a character, choose the gender the episodes support best and merge roles and relationships, keeping the most specific information.\n" +
            "- For an addressing pair, keep the form most episodes use and describe the exception in note.\n" +
            "- Merge series, tone and register_rules of the partial texts into one coherent text each, consistent with the AGREED ENTRIES.\n" +
            "- Write source and target without a leading article. Write series, tone, register_rules, note, role and relationships in {notes_language}, as plain text: no citations, links or source markers.\n" +
            "- The LOADED GLOSSARY entries, when present, are fixed as well: keep the texts consistent with them.";

        /// <summary>
        /// Descrizione del tool di consegna
        /// </summary>
        private const string TOOL_DESCRIPTION = "Submit the complete glossary used to translate every episode consistently: terms, characters, addressing, tone and register rules. When the user message has a LOADED GLOSSARY, the terms, characters and addressing lists hold only the entries it lacks. When it has CONFLICTS, they hold exactly one entry per conflict. The code validates it: if invalid you receive the errors with their path and must submit the whole glossary again.";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Valori di kind nello schema
        /// </summary>
        private static readonly string[] s_termKinds = new string[] { "name", "place", "organization", "title", "term", "catchphrase" };

        /// <summary>
        /// Valori di gender nello schema
        /// </summary>
        private static readonly string[] s_genders = new string[] { "male", "female", "other", "unknown" };

        /// <summary>
        /// Marcatori di citazione della ricerca web: ogni tratto da U+E200 a U+E201 compreso
        /// </summary>
        private static readonly Regex s_citation = new Regex("\\uE200[^\\uE201]*\\uE201", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Lingua d'origine ISO 639-2
        /// </summary>
        private readonly string _sourceLanguage;

        /// <summary>
        /// Forme di cortesia della lingua d'arrivo, null senza forme
        /// </summary>
        private readonly string[] _addressForms;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="sourceLanguage">Lingua d'origine ISO 639-2</param>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        public GlossaryBuilder(string sourceLanguage, string targetLanguage)
        {
            this._sourceLanguage = LanguageValidator.NormalizeToIso6392(sourceLanguage);
            this._addressForms = TranslationRequestBuilder.GetAddressForms(targetLanguage);

            string source = TranslationRequestBuilder.GetEnglishName(sourceLanguage);
            string target = TranslationRequestBuilder.GetEnglishName(targetLanguage);
            string notes = TranslationRequestBuilder.GetNotesLanguage();

            this.AnalysisInstructions = ANALYSIS_TEMPLATE.Replace("{source_language}", source).Replace("{target_language}", target)
                .Replace("{notes_language}", notes).Replace("{address_procedure}", TranslationRequestBuilder.GetAddressProcedure(targetLanguage))
                .Replace("{record_format}", TranslationRequestBuilder.RECORD_FORMAT);
            this.MergeInstructions = MERGE_TEMPLATE.Replace("{source_language}", source).Replace("{target_language}", target).Replace("{notes_language}", notes);

            // Definizione di submit_glossary: addressing c'è solo per le lingue con forme di cortesia
            string formDescription = TranslationRequestBuilder.GetAddressFormDescription(targetLanguage);
            JsonObject properties = new JsonObject
            {
                ["series"] = new JsonObject { ["type"] = "string", ["description"] = "Series title, year and official title in the target language if any." },
                ["tone"] = new JsonObject { ["type"] = "string", ["description"] = "Tone and style of the dialogue, and how to render it in the target language." },
                ["register_rules"] = new JsonObject { ["type"] = "string", ["description"] = "General rules for forms of address and register in the target language." },
                ["terms"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["source"] = new JsonObject { ["type"] = "string", ["description"] = "Term as it appears in the source, without a leading article." },
                            ["target"] = new JsonObject { ["type"] = "string", ["description"] = "Rendering in the target language, without a leading article; identical to source if it stays untranslated." },
                            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = CreateArray(s_termKinds) },
                            ["invariable"] = new JsonObject { ["type"] = "boolean", ["description"] = "True for proper names that never inflect and must appear verbatim." },
                            ["note"] = new JsonObject { ["type"] = CreateArray(new string[] { "string", "null" }), ["description"] = "Usage hints, inflection, alternatives considered; null if none." }
                        },
                        ["required"] = CreateArray(new string[] { "source", "target", "kind", "invariable", "note" }),
                        ["additionalProperties"] = false
                    }
                },
                ["characters"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject { ["type"] = "string" },
                            ["gender"] = new JsonObject { ["type"] = "string", ["enum"] = CreateArray(s_genders) },
                            ["role"] = new JsonObject { ["type"] = "string" },
                            ["relationships"] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = CreateArray(new string[] { "name", "gender", "role", "relationships" }),
                        ["additionalProperties"] = false
                    }
                }
            };
            List<string> required = new List<string> { "series", "tone", "register_rules", "terms", "characters" };

            if (this._addressForms != null)
            {
                JsonObject form = new JsonObject { ["type"] = "string", ["enum"] = CreateArray(this._addressForms) };
                if (!string.IsNullOrEmpty(formDescription))
                    form["description"] = formDescription;

                properties["addressing"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["speaker"] = new JsonObject { ["type"] = "string" },
                            ["addressee"] = new JsonObject { ["type"] = "string" },
                            ["form"] = form,
                            ["note"] = new JsonObject { ["type"] = CreateArray(new string[] { "string", "null" }) }
                        },
                        ["required"] = CreateArray(new string[] { "speaker", "addressee", "form", "note" }),
                        ["additionalProperties"] = false
                    }
                };
                required.Add("addressing");
            }

            this.Tool = new JsonObject
            {
                ["type"] = "function",
                ["name"] = SUBMIT_TOOL,
                ["description"] = TOOL_DESCRIPTION,
                ["strict"] = true,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = CreateArray(required),
                    ["additionalProperties"] = false
                }
            };
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Tool offerti a una chiamata del glossario
        /// </summary>
        /// <param name="webSearch">True per offrire anche la ricerca web, solo in A1</param>
        /// <returns>Elenco dei tool, sempre nello stesso ordine</returns>
        public JsonArray CreateTools(bool webSearch)
        {
            JsonArray tools = new JsonArray();
            if (webSearch)
                tools.Add(new JsonObject { ["type"] = TranslationProviderCapabilities.WEB_SEARCH });
            tools.Add(this.Tool.DeepClone());
            return tools;
        }

        /// <summary>
        /// Messaggio iniziale di A1
        /// </summary>
        /// <param name="context">Campo serie/contesto</param>
        /// <param name="loaded">Glossario caricato, null senza</param>
        /// <param name="candidates">Candidati ricorrenti di tutto il progetto</param>
        /// <param name="episodes">Tutti i file del progetto, per le colonne dei candidati</param>
        /// <param name="analysed">File mandati in questa chiamata</param>
        /// <returns>Testo del messaggio</returns>
        public string CreateAnalysisMessage(string context, Glossary loaded, List<RecurringCandidate> candidates, IReadOnlyList<GlossaryEpisode> episodes, IReadOnlyList<GlossaryEpisode> analysed)
        {
            StringBuilder message = this.CreateMessageHead(context, loaded, candidates, episodes);
            foreach (GlossaryEpisode episode in analysed)
            {
                int cues = 0;
                foreach (SubtitleCue cue in episode.Cues)
                {
                    if (cue.Id.HasValue)
                        cues++;
                }

                message.Append("\n\nEPISODE FILE ").Append(episode.Label).Append(": ").Append(episode.FileName).Append(" (")
                    .Append(cues.ToString(CultureInfo.InvariantCulture)).Append(" cues)\n")
                    .Append(TranslationRequestBuilder.FormatSource(episode.Cues));
            }

            return message.ToString();
        }

        /// <summary>
        /// Messaggio iniziale di A2
        /// </summary>
        /// <param name="context">Campo serie/contesto</param>
        /// <param name="loaded">Glossario caricato, null senza</param>
        /// <param name="candidates">Candidati ricorrenti di tutto il progetto</param>
        /// <param name="episodes">Tutti i file del progetto</param>
        /// <param name="merge">Fusione del codice con i conflitti</param>
        /// <param name="partials">Glossari parziali delle A1, in ordine di progetto</param>
        /// <returns>Testo del messaggio</returns>
        public string CreateMergeMessage(string context, Glossary loaded, List<RecurringCandidate> candidates, IReadOnlyList<GlossaryEpisode> episodes, GlossaryMergeResult merge, IReadOnlyList<GlossaryPartial> partials)
        {
            StringBuilder message = this.CreateMessageHead(context, loaded, candidates, episodes);
            message.Append("\n\nAGREED ENTRIES (merged by code; already decided):\n").Append(TranslationRequestBuilder.ToModelJson(this.ToToolJson(merge.Agreed, false)));
            message.Append("\n\nCONFLICTS (variants proposed by the episodes):\n").Append(TranslationRequestBuilder.ToModelJson(merge.Conflicts));

            foreach (GlossaryPartial partial in partials)
            {
                GlossaryEpisode episode = FindEpisode(episodes, partial.FileId);
                JsonObject texts = new JsonObject
                {
                    ["series"] = partial.Glossary.Series,
                    ["tone"] = partial.Glossary.Tone,
                    ["register_rules"] = partial.Glossary.RegisterRules
                };
                message.Append("\n\nPARTIAL TEXTS ").Append(episode != null ? episode.Label + ": " + episode.FileName : partial.FileId).Append('\n')
                    .Append(TranslationRequestBuilder.ToModelJson(texts));
            }

            return message.ToString();
        }

        /// <summary>
        /// Legge una risposta di A1 o A2: accoda all'ingresso gli item ricevuti e, senza consegna valida, gli esiti per il giro di
        /// correzione
        /// </summary>
        /// <param name="response">Risposta del servizio</param>
        /// <param name="input">Ingresso della sequenza, a cui si accodano item ed esiti</param>
        /// <param name="merge">Fusione con i conflitti per A2, null per A1</param>
        /// <returns>Consegna valida oppure gli errori dell'ultima consegna</returns>
        public GlossaryDelivery ReadResponse(AiResponseResult response, JsonArray input, GlossaryMergeResult merge)
        {
            GlossaryDelivery delivery = new GlossaryDelivery();
            List<JsonObject> calls = new List<JsonObject>();

            // Ogni item torna invariato al giro successivo: senza stato lato server è l'unica memoria del modello
            foreach (JsonObject item in response.Items)
            {
                input.Add(item.DeepClone());
                if (AiOAuthFlow.GetString(item, "type") == "function_call")
                {
                    calls.Add(item);
                }
                else if (AiOAuthFlow.GetString(item, "type") == "web_search_call")
                {
                    delivery.WebSearchCalls++;
                }
            }

            if (calls.Count == 0)
            {
                delivery.Errors.Add("NO_SUBMISSION: " + MISSING_SUBMISSION);
                input.Add(TranslationRequestBuilder.CreateUserMessage(MISSING_SUBMISSION));
                return delivery;
            }

            List<KeyValuePair<string, string>> outputs = new List<KeyValuePair<string, string>>();
            foreach (JsonObject call in calls)
            {
                List<string> errors = new List<string>();
                if (delivery.Glossary != null)
                {
                    errors.Add("DUPLICATE_SUBMISSION: only one submission is used; this one was ignored.");
                }
                else if (AiOAuthFlow.GetString(call, "name") != SUBMIT_TOOL)
                {
                    errors.Add("ARGS_INVALID: unknown tool '" + AiOAuthFlow.GetString(call, "name") + "'.");
                }
                else
                {
                    Glossary glossary = this.Validate(AiOAuthFlow.GetString(call, "arguments"), merge, errors);
                    if (errors.Count == 0)
                    {
                        delivery.Glossary = glossary;
                    }
                    else
                    {
                        delivery.Errors = errors;
                    }
                }

                outputs.Add(new KeyValuePair<string, string>(AiOAuthFlow.GetString(call, "call_id"), new JsonObject { ["valid"] = errors.Count == 0, ["errors"] = CreateArray(errors) }.ToJsonString()));
            }

            // Con una consegna valida la sequenza è finita: gli esiti servono solo al giro successivo
            if (delivery.Glossary != null)
            {
                delivery.Errors.Clear();
                return delivery;
            }

            foreach (KeyValuePair<string, string> output in outputs)
                input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = output.Key, ["output"] = output.Value });

            return delivery;
        }

        /// <summary>
        /// Valida gli argomenti di submit_glossary e li converte nella forma del progetto
        /// </summary>
        /// <param name="arguments">Argomenti JSON della chiamata</param>
        /// <param name="merge">Fusione con i conflitti per A2, null per A1</param>
        /// <param name="errors">Righe d'errore, una per problema</param>
        /// <returns>Glossario convertito, valido solo senza errori</returns>
        public Glossary Validate(string arguments, GlossaryMergeResult merge, List<string> errors)
        {
            Glossary glossary = new Glossary();
            JsonObject root;
            try
            {
                root = JsonNode.Parse(string.IsNullOrEmpty(arguments) ? "null" : arguments) as JsonObject;
            }
            catch (JsonException)
            {
                root = null;
            }

            if (root == null)
            {
                errors.Add("ARGS_INVALID: arguments are not a JSON object.");
                return glossary;
            }

            glossary.Series = ReadString(root, "series", "series", true, true, errors);
            glossary.Tone = ReadString(root, "tone", "tone", true, false, errors);
            glossary.RegisterRules = ReadString(root, "register_rules", "register_rules", true, false, errors);

            JsonArray terms = ReadArray(root, "terms", errors);
            Dictionary<string, string> seenTerms = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; terms != null && i < terms.Count; i++)
            {
                string path = "terms[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                JsonObject item = ReadItem(terms[i], path, errors);
                if (item == null)
                    continue;

                GlossaryTerm term = new GlossaryTerm();
                term.Source = ReadString(item, "source", path + ".source", true, true, errors);
                term.Target = ReadString(item, "target", path + ".target", true, true, errors);
                term.Kind = (GlossaryTermKind)ReadEnum(item, "kind", path + ".kind", s_termKinds, errors);
                JsonNode invariable;
                if (!item.TryGetPropertyValue("invariable", out invariable) || invariable == null)
                {
                    errors.Add("GLOSSARY_FIELD_MISSING " + path + ".invariable: required field is missing.");
                }
                else if (invariable.GetValueKind() != JsonValueKind.True && invariable.GetValueKind() != JsonValueKind.False)
                {
                    errors.Add("GLOSSARY_TYPE " + path + ".invariable: expected boolean.");
                }
                else
                {
                    term.Invariable = invariable.GetValueKind() == JsonValueKind.True;
                }
                term.Note = ReadString(item, "note", path + ".note", false, false, errors);
                term.Origin = GlossaryOrigin.Analysis;
                AddDuplicateCheck(seenTerms, this.TermKey(term.Source), path + ".source", errors);
                glossary.Terms.Add(term);
            }

            JsonArray characters = ReadArray(root, "characters", errors);
            Dictionary<string, string> seenCharacters = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; characters != null && i < characters.Count; i++)
            {
                string path = "characters[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                JsonObject item = ReadItem(characters[i], path, errors);
                if (item == null)
                    continue;

                GlossaryCharacter character = new GlossaryCharacter();
                character.Name = ReadString(item, "name", path + ".name", true, true, errors);
                character.Gender = (CharacterGender)ReadEnum(item, "gender", path + ".gender", s_genders, errors);
                character.Role = ReadString(item, "role", path + ".role", true, false, errors);
                character.Relationships = ReadString(item, "relationships", path + ".relationships", true, false, errors);
                character.Origin = GlossaryOrigin.Analysis;
                AddDuplicateCheck(seenCharacters, CharacterKey(character.Name), path + ".name", errors);
                glossary.Characters.Add(character);
            }

            // Le lingue senza forme di cortesia non hanno addressing nello schema: se arriva lo stesso si ignora
            JsonArray addressing = this._addressForms != null ? ReadArray(root, "addressing", errors) : null;
            Dictionary<string, string> seenAddressing = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; addressing != null && i < addressing.Count; i++)
            {
                string path = "addressing[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                JsonObject item = ReadItem(addressing[i], path, errors);
                if (item == null)
                    continue;

                GlossaryAddressing pair = new GlossaryAddressing();
                pair.Speaker = ReadString(item, "speaker", path + ".speaker", true, true, errors);
                pair.Addressee = ReadString(item, "addressee", path + ".addressee", true, true, errors);
                int form = ReadEnum(item, "form", path + ".form", this._addressForms, errors);
                pair.Form = this._addressForms[form];
                pair.Note = ReadString(item, "note", path + ".note", false, false, errors);
                pair.Origin = GlossaryOrigin.Analysis;
                AddDuplicateCheck(seenAddressing, AddressingKey(pair.Speaker, pair.Addressee), path + ".speaker", errors);
                glossary.Addressing.Add(pair);
            }

            // A2: un conflitto senza voce consegnata
            if (merge != null)
            {
                foreach (JsonNode conflict in merge.Conflicts["terms"].AsArray())
                {
                    string source = AiOAuthFlow.GetString(conflict.AsObject(), "source");
                    if (!glossary.Terms.Exists(t => this.TermKey(t.Source) == this.TermKey(source)))
                        errors.Add("GLOSSARY_CONFLICT_MISSING terms: no entry for conflict \"" + source + "\".");
                }

                foreach (JsonNode conflict in merge.Conflicts["characters"].AsArray())
                {
                    string name = AiOAuthFlow.GetString(conflict.AsObject(), "name");
                    if (!glossary.Characters.Exists(c => CharacterKey(c.Name) == CharacterKey(name)))
                        errors.Add("GLOSSARY_CONFLICT_MISSING characters: no entry for conflict \"" + name + "\".");
                }

                JsonArray pairs = merge.Conflicts["addressing"] as JsonArray;
                for (int i = 0; pairs != null && i < pairs.Count; i++)
                {
                    string speaker = AiOAuthFlow.GetString(pairs[i].AsObject(), "speaker");
                    string addressee = AiOAuthFlow.GetString(pairs[i].AsObject(), "addressee");
                    if (!glossary.Addressing.Exists(a => AddressingKey(a.Speaker, a.Addressee) == AddressingKey(speaker, addressee)))
                        errors.Add("GLOSSARY_CONFLICT_MISSING addressing: no entry for conflict \"" + speaker + " → " + addressee + "\".");
                }
            }

            return glossary;
        }

        /// <summary>
        /// Fonde i glossari parziali delle A1: le voci su cui i file concordano diventano una voce, le altre sono conflitti per A2
        /// </summary>
        /// <param name="partials">Glossari parziali in ordine di progetto</param>
        /// <param name="episodes">Tutti i file del progetto</param>
        /// <param name="candidates">Candidati ricorrenti, per scegliere ruolo e rapporti dei personaggi</param>
        /// <returns>Voci concordi e conflitti</returns>
        public GlossaryMergeResult MergePartials(IReadOnlyList<GlossaryPartial> partials, IReadOnlyList<GlossaryEpisode> episodes, List<RecurringCandidate> candidates)
        {
            GlossaryMergeResult merge = new GlossaryMergeResult();
            JsonArray termConflicts = new JsonArray();
            JsonArray characterConflicts = new JsonArray();
            JsonArray addressingConflicts = new JsonArray();

            // Termini: stessa chiave e stessa resa, tipo e invariabilità
            Dictionary<string, List<KeyValuePair<GlossaryTerm, string>>> terms = new Dictionary<string, List<KeyValuePair<GlossaryTerm, string>>>(StringComparer.Ordinal);
            List<string> termOrder = new List<string>();
            foreach (GlossaryPartial partial in partials)
            {
                foreach (GlossaryTerm term in partial.Glossary.Terms)
                    AddProposal(terms, termOrder, this.TermKey(term.Source), term, partial.FileId);
            }

            foreach (string key in termOrder)
            {
                List<KeyValuePair<string, List<KeyValuePair<GlossaryTerm, string>>>> variants = GroupVariants(terms[key], t => t.Target.Trim() + "\u0001" + t.Kind.ToString() + "\u0001" + t.Invariable.ToString());
                if (variants.Count == 1)
                {
                    GlossaryTerm single = variants[0].Value[0].Key;
                    GlossaryTerm joined = new GlossaryTerm { Source = single.Source, Target = single.Target, Kind = single.Kind, Invariable = single.Invariable, Origin = single.Origin };
                    joined.Note = JoinNotes(variants[0].Value, t => t.Note);
                    merge.Agreed.Terms.Add(joined);
                    continue;
                }

                JsonArray list = new JsonArray();
                foreach (KeyValuePair<string, List<KeyValuePair<GlossaryTerm, string>>> variant in variants)
                {
                    GlossaryTerm term = variant.Value[0].Key;
                    JsonObject entry = new JsonObject { ["target"] = term.Target, ["kind"] = term.Kind.ToString().ToLowerInvariant(), ["invariable"] = term.Invariable };
                    string note = JoinNotes(variant.Value, t => t.Note);
                    if (!string.IsNullOrEmpty(note))
                        entry["note"] = note;
                    entry["episodes"] = CreateArray(GetLabels(variant.Value, episodes));
                    list.Add(entry);
                }

                termConflicts.Add(new JsonObject { ["source"] = terms[key][0].Key.Source, ["variants"] = list });
                merge.TermConflicts.Add(key);
            }

            // Personaggi: stesso nome e stesso genere
            Dictionary<string, List<KeyValuePair<GlossaryCharacter, string>>> characters = new Dictionary<string, List<KeyValuePair<GlossaryCharacter, string>>>(StringComparer.Ordinal);
            List<string> characterOrder = new List<string>();
            foreach (GlossaryPartial partial in partials)
            {
                foreach (GlossaryCharacter character in partial.Glossary.Characters)
                    AddProposal(characters, characterOrder, CharacterKey(character.Name), character, partial.FileId);
            }

            foreach (string key in characterOrder)
            {
                List<KeyValuePair<string, List<KeyValuePair<GlossaryCharacter, string>>>> variants = GroupVariants(characters[key], c => c.Gender.ToString());
                if (variants.Count == 1)
                {
                    // Ruolo e rapporti dal file in cui il nome ha più occorrenze fra i candidati
                    RecurringCandidate candidate = candidates.Find(c => string.Equals(c.Term, variants[0].Value[0].Key.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                    KeyValuePair<GlossaryCharacter, string> best = variants[0].Value[0];
                    foreach (KeyValuePair<GlossaryCharacter, string> proposal in variants[0].Value)
                    {
                        if (candidate != null && candidate.CountIn(proposal.Value) > candidate.CountIn(best.Value))
                            best = proposal;
                    }

                    merge.Agreed.Characters.Add(new GlossaryCharacter { Name = best.Key.Name, Gender = best.Key.Gender, Role = best.Key.Role, Relationships = best.Key.Relationships, Origin = best.Key.Origin });
                    continue;
                }

                JsonArray list = new JsonArray();
                foreach (KeyValuePair<string, List<KeyValuePair<GlossaryCharacter, string>>> variant in variants)
                {
                    GlossaryCharacter character = variant.Value[0].Key;
                    list.Add(new JsonObject
                    {
                        ["gender"] = character.Gender.ToString().ToLowerInvariant(),
                        ["role"] = character.Role,
                        ["relationships"] = character.Relationships,
                        ["episodes"] = CreateArray(GetLabels(variant.Value, episodes))
                    });
                }

                characterConflicts.Add(new JsonObject { ["name"] = characters[key][0].Key.Name, ["variants"] = list });
                merge.CharacterConflicts.Add(key);
            }

            // Forme di cortesia: stessa coppia e stessa forma
            Dictionary<string, List<KeyValuePair<GlossaryAddressing, string>>> addressing = new Dictionary<string, List<KeyValuePair<GlossaryAddressing, string>>>(StringComparer.Ordinal);
            List<string> addressingOrder = new List<string>();
            foreach (GlossaryPartial partial in partials)
            {
                foreach (GlossaryAddressing pair in partial.Glossary.Addressing)
                    AddProposal(addressing, addressingOrder, AddressingKey(pair.Speaker, pair.Addressee), pair, partial.FileId);
            }

            foreach (string key in addressingOrder)
            {
                List<KeyValuePair<string, List<KeyValuePair<GlossaryAddressing, string>>>> variants = GroupVariants(addressing[key], a => a.Form);
                if (variants.Count == 1)
                {
                    GlossaryAddressing single = variants[0].Value[0].Key;
                    GlossaryAddressing joined = new GlossaryAddressing { Speaker = single.Speaker, Addressee = single.Addressee, Form = single.Form, Origin = single.Origin };
                    joined.Note = JoinNotes(variants[0].Value, a => a.Note);
                    merge.Agreed.Addressing.Add(joined);
                    continue;
                }

                JsonArray list = new JsonArray();
                foreach (KeyValuePair<string, List<KeyValuePair<GlossaryAddressing, string>>> variant in variants)
                {
                    JsonObject entry = new JsonObject { ["form"] = variant.Value[0].Key.Form };
                    string note = JoinNotes(variant.Value, a => a.Note);
                    if (!string.IsNullOrEmpty(note))
                        entry["note"] = note;
                    entry["episodes"] = CreateArray(GetLabels(variant.Value, episodes));
                    list.Add(entry);
                }

                GlossaryAddressing first = addressing[key][0].Key;
                addressingConflicts.Add(new JsonObject { ["speaker"] = first.Speaker, ["addressee"] = first.Addressee, ["variants"] = list });
                merge.AddressingConflicts.Add(key);
            }

            // Senza A2 i testi vengono dal glossario parziale del primo file
            if (partials.Count > 0)
            {
                merge.Agreed.Series = partials[0].Glossary.Series;
                merge.Agreed.Tone = partials[0].Glossary.Tone;
                merge.Agreed.RegisterRules = partials[0].Glossary.RegisterRules;
            }

            merge.Conflicts = new JsonObject { ["terms"] = termConflicts, ["characters"] = characterConflicts };
            if (this._addressForms != null)
                merge.Conflicts["addressing"] = addressingConflicts;

            return merge;
        }

        /// <summary>
        /// Applica la consegna di A2 alle voci concordi: entrano solo le voci dei conflitti, le altre si scartano
        /// </summary>
        /// <param name="merge">Fusione del codice</param>
        /// <param name="delivered">Consegna valida di A2</param>
        /// <param name="discarded">Voci consegnate fuori dai conflitti</param>
        /// <returns>Glossario fuso</returns>
        public Glossary ApplyResolution(GlossaryMergeResult merge, Glossary delivered, out int discarded)
        {
            Glossary result = merge.Agreed;
            discarded = 0;

            foreach (GlossaryTerm term in delivered.Terms)
            {
                if (merge.TermConflicts.Contains(this.TermKey(term.Source)))
                {
                    result.Terms.Add(term);
                }
                else
                {
                    discarded++;
                }
            }

            foreach (GlossaryCharacter character in delivered.Characters)
            {
                if (merge.CharacterConflicts.Contains(CharacterKey(character.Name)))
                {
                    result.Characters.Add(character);
                }
                else
                {
                    discarded++;
                }
            }

            foreach (GlossaryAddressing pair in delivered.Addressing)
            {
                if (merge.AddressingConflicts.Contains(AddressingKey(pair.Speaker, pair.Addressee)))
                {
                    result.Addressing.Add(pair);
                }
                else
                {
                    discarded++;
                }
            }

            result.Series = delivered.Series;
            result.Tone = delivered.Tone;
            result.RegisterRules = delivered.RegisterRules;
            return result;
        }

        /// <summary>
        /// Fonde la consegna dell'analisi col glossario corrente: le voci caricate e quelle dell'utente restano intatte e una
        /// proposta sulla stessa voce si scarta intera, così nessuna voce compare due volte. Un testo del glossario corrente vince su quello proposto, che si
        /// scarta allo stesso modo
        /// </summary>
        /// <param name="delivered">Voci dell'analisi</param>
        /// <param name="current">Glossario corrente del progetto, null senza</param>
        /// <param name="discarded">Proposte scartate perché la voce è già nel glossario</param>
        /// <returns>Glossario in bozza</returns>
        public Glossary MergeWithImported(Glossary delivered, Glossary current, List<GlossaryDiscard> discarded)
        {
            Glossary result = new Glossary();
            HashSet<string> termKeys = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> characterKeys = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> addressingKeys = new HashSet<string>(StringComparer.Ordinal);

            if (current != null)
            {
                foreach (GlossaryTerm term in current.Terms)
                {
                    if (term.Origin == GlossaryOrigin.Analysis)
                        continue;
                    result.Terms.Add(term);
                    termKeys.Add(this.TermKey(term.Source));
                }

                foreach (GlossaryCharacter character in current.Characters)
                {
                    if (character.Origin == GlossaryOrigin.Analysis)
                        continue;
                    result.Characters.Add(character);
                    characterKeys.Add(CharacterKey(character.Name));
                }

                foreach (GlossaryAddressing pair in current.Addressing)
                {
                    if (pair.Origin == GlossaryOrigin.Analysis)
                        continue;
                    result.Addressing.Add(pair);
                    addressingKeys.Add(AddressingKey(pair.Speaker, pair.Addressee));
                }
            }

            foreach (GlossaryTerm term in delivered.Terms)
            {
                if (termKeys.Contains(this.TermKey(term.Source)))
                {
                    discarded.Add(new GlossaryDiscard { Kind = GlossaryEntryKind.Term, Key = term.Source, Delivered = term.Target, Term = term });
                }
                else
                {
                    result.Terms.Add(term);
                }
            }

            foreach (GlossaryCharacter character in delivered.Characters)
            {
                if (characterKeys.Contains(CharacterKey(character.Name)))
                {
                    discarded.Add(new GlossaryDiscard { Kind = GlossaryEntryKind.Character, Key = character.Name, Delivered = character.Gender.ToString(), Character = character });
                }
                else
                {
                    result.Characters.Add(character);
                }
            }

            foreach (GlossaryAddressing pair in delivered.Addressing)
            {
                if (addressingKeys.Contains(AddressingKey(pair.Speaker, pair.Addressee)))
                {
                    discarded.Add(new GlossaryDiscard { Kind = GlossaryEntryKind.Addressing, Key = pair.Speaker + " → " + pair.Addressee, Delivered = pair.Form, Addressing = pair });
                }
                else
                {
                    result.Addressing.Add(pair);
                }
            }

            // I testi del glossario caricato valgono quando non sono vuoti; quello proposto resta fra gli scarti, anche vuoto, per
            // tornare quando il caricato si toglie
            string[] currentTexts = current != null ? new string[] { current.Series, current.Tone, current.RegisterRules } : new string[3];
            string[] deliveredTexts = new string[] { delivered.Series, delivered.Tone, delivered.RegisterRules };
            GlossaryEntryKind[] textKinds = new GlossaryEntryKind[] { GlossaryEntryKind.Series, GlossaryEntryKind.Tone, GlossaryEntryKind.RegisterRules };
            string[] texts = new string[3];
            for (int i = 0; i < texts.Length; i++)
            {
                texts[i] = string.IsNullOrEmpty(currentTexts[i]) ? deliveredTexts[i] ?? "" : currentTexts[i];
                if (!string.IsNullOrEmpty(currentTexts[i]))
                    discarded.Add(new GlossaryDiscard { Kind = textKinds[i], Delivered = deliveredTexts[i] ?? "" });
            }

            result.Series = texts[0];
            result.Tone = texts[1];
            result.RegisterRules = texts[2];
            result.Revision = current != null ? current.Revision : 0;
            result.Status = GlossaryStatus.Draft;
            return result;
        }

        /// <summary>
        /// Ricalcola i file in cui compare ogni termine, con la parola intera senza articolo e senza distinguere maiuscole, e
        /// precompila «Maiuscole esatte» delle voci che non l'hanno ancora quando le battute hanno già i tipi riconosciuti
        /// </summary>
        /// <param name="glossary">Glossario da aggiornare</param>
        /// <param name="episodes">File del progetto</param>
        /// <param name="exactCase">True per precompilare «Maiuscole esatte»: false finché il riconoscimento dei tipi deve ancora
        /// togliere le righe che non sono nella lingua d'origine</param>
        public void SetEpisodes(Glossary glossary, IReadOnlyList<GlossaryEpisode> episodes, bool exactCase)
        {
            List<KeyValuePair<string, string>> texts = new List<KeyValuePair<string, string>>();
            List<string> cueTexts = new List<string>();
            foreach (GlossaryEpisode episode in episodes)
            {
                StringBuilder text = new StringBuilder();
                foreach (SubtitleCue cue in episode.Cues)
                {
                    if (!cue.Id.HasValue)
                        continue;
                    text.Append(TranslationValidator.GlossaryPlain(cue.CleanText)).Append('\n');
                    cueTexts.Add(cue.CleanText);
                }
                texts.Add(new KeyValuePair<string, string>(episode.FileId, text.ToString()));
            }
            if (exactCase)
                TranslationValidator.FillExactCase(glossary, new NewTermDecision[0], cueTexts);

            foreach (GlossaryTerm term in glossary.Terms)
            {
                term.Episodes = new List<string>();
                string word = TranslationValidator.StripArticle(term.Source, this._sourceLanguage);
                if (string.IsNullOrEmpty(word))
                    continue;

                Regex pattern = TranslationValidator.WordPattern(word, false);
                foreach (KeyValuePair<string, string> text in texts)
                {
                    if (pattern.IsMatch(text.Value))
                        term.Episodes.Add(text.Key);
                }
            }
        }

        /// <summary>
        /// Glossario nella forma del tool, per i messaggi al modello: nomi snake case, enum minuscoli, note assenti se vuote,
        /// niente campi dell'app
        /// </summary>
        /// <param name="glossary">Glossario</param>
        /// <param name="includeTexts">True per series, tone e register_rules in testa</param>
        /// <returns>Oggetto JSON</returns>
        public JsonObject ToToolJson(Glossary glossary, bool includeTexts)
        {
            JsonObject result = new JsonObject();
            JsonArray terms = new JsonArray();
            JsonArray characters = new JsonArray();
            JsonArray addressing = new JsonArray();

            if (includeTexts)
            {
                result["series"] = glossary.Series;
                result["tone"] = glossary.Tone;
                result["register_rules"] = glossary.RegisterRules;
            }

            foreach (GlossaryTerm term in glossary.Terms)
            {
                JsonObject entry = new JsonObject { ["source"] = term.Source, ["target"] = term.Target, ["kind"] = term.Kind.ToString().ToLowerInvariant(), ["invariable"] = term.Invariable };
                if (!string.IsNullOrEmpty(term.Note))
                    entry["note"] = term.Note;
                terms.Add(entry);
            }

            foreach (GlossaryCharacter character in glossary.Characters)
            {
                characters.Add(new JsonObject
                {
                    ["name"] = character.Name,
                    ["gender"] = character.Gender.ToString().ToLowerInvariant(),
                    ["role"] = character.Role,
                    ["relationships"] = character.Relationships
                });
            }

            foreach (GlossaryAddressing pair in glossary.Addressing)
            {
                JsonObject entry = new JsonObject { ["speaker"] = pair.Speaker, ["addressee"] = pair.Addressee, ["form"] = pair.Form };
                if (!string.IsNullOrEmpty(pair.Note))
                    entry["note"] = pair.Note;
                addressing.Add(entry);
            }

            result["terms"] = terms;
            result["characters"] = characters;
            if (this._addressForms != null)
                result["addressing"] = addressing;

            return result;
        }

        /// <summary>
        /// Chiave di un termine per fusioni e conflitti: minuscolo, senza articolo iniziale della lingua d'origine
        /// </summary>
        /// <param name="source">Termine</param>
        /// <returns>Chiave</returns>
        public string TermKey(string source)
        {
            return TranslationValidator.StripArticle(source, this._sourceLanguage).ToLowerInvariant();
        }

        /// <summary>
        /// Chiave di un personaggio
        /// </summary>
        /// <param name="name">Nome</param>
        /// <returns>Chiave</returns>
        public static string CharacterKey(string name)
        {
            return (name ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Chiave di una coppia di forme di cortesia
        /// </summary>
        /// <param name="speaker">Chi parla</param>
        /// <param name="addressee">A chi</param>
        /// <returns>Chiave</returns>
        public static string AddressingKey(string speaker, string addressee)
        {
            return CharacterKey(speaker) + "\u0001" + CharacterKey(addressee);
        }

        /// <summary>
        /// Stima i token di un testo mandato al modello
        /// </summary>
        /// <param name="text">Testo</param>
        /// <returns>Token stimati</returns>
        public static int EstimateTokens(string text)
        {
            return (text.Length + CHARS_PER_TOKEN - 1) / CHARS_PER_TOKEN;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Intestazione comune dei messaggi di A1 e A2: contesto, glossario caricato e candidati
        /// </summary>
        /// <param name="context">Campo serie/contesto</param>
        /// <param name="loaded">Glossario caricato, null senza</param>
        /// <param name="candidates">Candidati ricorrenti</param>
        /// <param name="episodes">Tutti i file del progetto, una colonna ciascuno</param>
        /// <returns>Messaggio in costruzione</returns>
        private StringBuilder CreateMessageHead(string context, Glossary loaded, List<RecurringCandidate> candidates, IReadOnlyList<GlossaryEpisode> episodes)
        {
            StringBuilder message = new StringBuilder();
            string trimmed = (context ?? "").Trim();
            message.Append("SERIES CONTEXT (from the user):\n").Append(string.IsNullOrEmpty(trimmed) ? "(none)" : trimmed);

            if (loaded != null)
                message.Append("\n\nLOADED GLOSSARY (already decided; deliver only the missing entries):\n").Append(TranslationRequestBuilder.ToModelJson(this.ToToolJson(loaded, true)));

            message.Append("\n\nRECURRING CANDIDATES (extracted by code; occurrences per episode file):\nterm");
            foreach (GlossaryEpisode episode in episodes)
                message.Append('\t').Append(episode.Label);
            foreach (RecurringCandidate candidate in candidates)
            {
                message.Append('\n').Append(candidate.Term);
                foreach (GlossaryEpisode episode in episodes)
                    message.Append('\t').Append(candidate.CountIn(episode.FileId).ToString(CultureInfo.InvariantCulture));
            }

            return message;
        }

        /// <summary>
        /// Legge una stringa dell'argomento: senza marcatori di citazione e senza spazi agli estremi
        /// </summary>
        /// <param name="source">Oggetto</param>
        /// <param name="name">Proprietà</param>
        /// <param name="path">Percorso per gli errori</param>
        /// <param name="required">True per un campo obbligatorio</param>
        /// <param name="notEmpty">True per un campo che non può essere vuoto</param>
        /// <param name="errors">Righe d'errore</param>
        /// <returns>Valore, vuoto se assente o del tipo sbagliato</returns>
        private static string ReadString(JsonObject source, string name, string path, bool required, bool notEmpty, List<string> errors)
        {
            JsonNode node;
            if (!source.TryGetPropertyValue(name, out node) || node == null)
            {
                if (required)
                    errors.Add("GLOSSARY_FIELD_MISSING " + path + ": required field is missing.");
                return "";
            }

            if (node.GetValueKind() != JsonValueKind.String)
            {
                errors.Add("GLOSSARY_TYPE " + path + ": expected string.");
                return "";
            }

            string value = s_citation.Replace(node.GetValue<string>(), "").Trim();
            if (notEmpty && string.IsNullOrEmpty(value))
                errors.Add("GLOSSARY_EMPTY " + path + ": must not be empty.");

            return value;
        }

        /// <summary>
        /// Legge un elenco obbligatorio della radice
        /// </summary>
        /// <param name="root">Argomenti</param>
        /// <param name="name">Proprietà</param>
        /// <param name="errors">Righe d'errore</param>
        /// <returns>Elenco, null se assente o del tipo sbagliato</returns>
        private static JsonArray ReadArray(JsonObject root, string name, List<string> errors)
        {
            JsonNode node;
            if (!root.TryGetPropertyValue(name, out node) || node == null)
            {
                errors.Add("GLOSSARY_FIELD_MISSING " + name + ": required field is missing.");
                return null;
            }

            if (node.GetValueKind() != JsonValueKind.Array)
            {
                errors.Add("GLOSSARY_TYPE " + name + ": expected array.");
                return null;
            }

            return node.AsArray();
        }

        /// <summary>
        /// Legge un elemento di un elenco
        /// </summary>
        /// <param name="node">Elemento</param>
        /// <param name="path">Percorso per gli errori</param>
        /// <param name="errors">Righe d'errore</param>
        /// <returns>Oggetto, null se del tipo sbagliato</returns>
        private static JsonObject ReadItem(JsonNode node, string path, List<string> errors)
        {
            if (node == null || node.GetValueKind() != JsonValueKind.Object)
            {
                errors.Add("GLOSSARY_TYPE " + path + ": expected object.");
                return null;
            }

            return node.AsObject();
        }

        /// <summary>
        /// Legge un valore enum obbligatorio
        /// </summary>
        /// <param name="source">Oggetto</param>
        /// <param name="name">Proprietà</param>
        /// <param name="path">Percorso per gli errori</param>
        /// <param name="values">Valori ammessi nell'ordine dell'enum</param>
        /// <param name="errors">Righe d'errore</param>
        /// <returns>Posizione del valore, zero se non valido</returns>
        private static int ReadEnum(JsonObject source, string name, string path, string[] values, List<string> errors)
        {
            int before = errors.Count;
            string value = ReadString(source, name, path, true, false, errors);
            if (errors.Count > before)
                return 0;

            int index = Array.IndexOf(values, value);
            if (index < 0)
            {
                errors.Add("GLOSSARY_ENUM " + path + ": '" + value + "' is not one of " + string.Join(", ", values) + ".");
                return 0;
            }

            return index;
        }

        /// <summary>
        /// Registra la chiave di una voce e segnala il doppione
        /// </summary>
        /// <param name="seen">Chiavi già viste con il loro percorso</param>
        /// <param name="key">Chiave della voce</param>
        /// <param name="path">Percorso della voce</param>
        /// <param name="errors">Righe d'errore</param>
        private static void AddDuplicateCheck(Dictionary<string, string> seen, string key, string path, List<string> errors)
        {
            if (string.IsNullOrEmpty(key))
                return;

            string other;
            if (seen.TryGetValue(key, out other))
            {
                errors.Add("GLOSSARY_DUPLICATE " + path + ": duplicates " + other + ".");
            }
            else
            {
                seen[key] = path;
            }
        }

        /// <summary>
        /// Aggiunge una proposta di un glossario parziale al suo gruppo
        /// </summary>
        /// <typeparam name="T">Tipo della voce</typeparam>
        /// <param name="groups">Proposte per chiave</param>
        /// <param name="order">Chiavi nell'ordine della prima proposta</param>
        /// <param name="key">Chiave della voce</param>
        /// <param name="entry">Voce proposta</param>
        /// <param name="fileId">File del glossario parziale</param>
        private static void AddProposal<T>(Dictionary<string, List<KeyValuePair<T, string>>> groups, List<string> order, string key, T entry, string fileId)
        {
            List<KeyValuePair<T, string>> proposals;
            if (!groups.TryGetValue(key, out proposals))
            {
                proposals = new List<KeyValuePair<T, string>>();
                groups[key] = proposals;
                order.Add(key);
            }

            proposals.Add(new KeyValuePair<T, string>(entry, fileId));
        }

        /// <summary>
        /// Raggruppa le proposte di una voce per variante, nell'ordine della prima proposta
        /// </summary>
        /// <typeparam name="T">Tipo della voce</typeparam>
        /// <param name="proposals">Proposte con il loro file</param>
        /// <param name="variant">Valori che devono coincidere</param>
        /// <returns>Varianti con le loro proposte</returns>
        private static List<KeyValuePair<string, List<KeyValuePair<T, string>>>> GroupVariants<T>(List<KeyValuePair<T, string>> proposals, Func<T, string> variant)
        {
            List<KeyValuePair<string, List<KeyValuePair<T, string>>>> variants = new List<KeyValuePair<string, List<KeyValuePair<T, string>>>>();
            foreach (KeyValuePair<T, string> proposal in proposals)
            {
                string value = variant(proposal.Key);
                int index = variants.FindIndex(v => v.Key == value);
                if (index < 0)
                {
                    variants.Add(new KeyValuePair<string, List<KeyValuePair<T, string>>>(value, new List<KeyValuePair<T, string>> { proposal }));
                }
                else
                {
                    variants[index].Value.Add(proposal);
                }
            }

            return variants;
        }

        /// <summary>
        /// Unisce le note diverse delle proposte con «; »
        /// </summary>
        /// <typeparam name="T">Tipo della voce</typeparam>
        /// <param name="proposals">Proposte</param>
        /// <param name="note">Nota della voce</param>
        /// <returns>Note unite</returns>
        private static string JoinNotes<T>(List<KeyValuePair<T, string>> proposals, Func<T, string> note)
        {
            List<string> notes = new List<string>();
            foreach (KeyValuePair<T, string> proposal in proposals)
            {
                string value = (note(proposal.Key) ?? "").Trim();
                if (!string.IsNullOrEmpty(value) && !notes.Contains(value))
                    notes.Add(value);
            }

            return string.Join("; ", notes);
        }

        /// <summary>
        /// Etichette ep1, ep2… dei file che hanno proposto una variante
        /// </summary>
        /// <typeparam name="T">Tipo della voce</typeparam>
        /// <param name="proposals">Proposte della variante</param>
        /// <param name="episodes">Tutti i file del progetto</param>
        /// <returns>Etichette senza doppioni</returns>
        private static List<string> GetLabels<T>(List<KeyValuePair<T, string>> proposals, IReadOnlyList<GlossaryEpisode> episodes)
        {
            List<string> labels = new List<string>();
            foreach (KeyValuePair<T, string> proposal in proposals)
            {
                GlossaryEpisode episode = FindEpisode(episodes, proposal.Value);
                string label = episode != null ? episode.Label : proposal.Value;
                if (!labels.Contains(label))
                    labels.Add(label);
            }

            return labels;
        }

        /// <summary>
        /// Cerca un file del progetto per identificativo
        /// </summary>
        /// <param name="episodes">File del progetto</param>
        /// <param name="fileId">Identificativo</param>
        /// <returns>File, null se assente</returns>
        private static GlossaryEpisode FindEpisode(IReadOnlyList<GlossaryEpisode> episodes, string fileId)
        {
            foreach (GlossaryEpisode episode in episodes)
            {
                if (episode.FileId == fileId)
                    return episode;
            }

            return null;
        }

        /// <summary>
        /// Crea un elenco JSON di stringhe
        /// </summary>
        /// <param name="values">Valori</param>
        /// <returns>Elenco JSON</returns>
        internal static JsonArray CreateArray(IEnumerable<string> values)
        {
            JsonArray array = new JsonArray();
            foreach (string value in values)
                array.Add(value);
            return array;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Instructions di A1
        /// </summary>
        public string AnalysisInstructions { get; }

        /// <summary>
        /// Instructions di A2
        /// </summary>
        public string MergeInstructions { get; }

        /// <summary>
        /// Definizione di submit_glossary per la lingua d'arrivo
        /// </summary>
        private JsonObject Tool { get; }

        #endregion
    }

    /// <summary>
    /// File del progetto nei messaggi del glossario
    /// </summary>
    public class GlossaryEpisode
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="fileId">Identificativo del file nel progetto</param>
        /// <param name="label">Etichetta nei messaggi: ep1, ep2…</param>
        /// <param name="fileName">Nome del file</param>
        /// <param name="cues">Battute del file</param>
        public GlossaryEpisode(string fileId, string label, string fileName, IReadOnlyList<SubtitleCue> cues)
        {
            this.FileId = fileId;
            this.Label = label;
            this.FileName = fileName;
            this.Cues = cues;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Identificativo del file nel progetto
        /// </summary>
        public string FileId { get; }

        /// <summary>
        /// Etichetta nei messaggi
        /// </summary>
        public string Label { get; }

        /// <summary>
        /// Nome del file
        /// </summary>
        public string FileName { get; }

        /// <summary>
        /// Battute del file
        /// </summary>
        public IReadOnlyList<SubtitleCue> Cues { get; }

        #endregion
    }

    /// <summary>
    /// Esito della lettura di una risposta di A1 o A2
    /// </summary>
    public class GlossaryDelivery : TranslationDelivery
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public GlossaryDelivery()
        {
            this.Glossary = null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Glossario consegnato, null senza consegna valida
        /// </summary>
        public Glossary Glossary { get; set; }

        #endregion
    }

    /// <summary>
    /// Fusione del codice dei glossari parziali: voci concordi e conflitti per A2
    /// </summary>
    public class GlossaryMergeResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public GlossaryMergeResult()
        {
            this.Agreed = new Glossary();
            this.Conflicts = new JsonObject();
            this.TermConflicts = new HashSet<string>(StringComparer.Ordinal);
            this.CharacterConflicts = new HashSet<string>(StringComparer.Ordinal);
            this.AddressingConflicts = new HashSet<string>(StringComparer.Ordinal);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Voci concordi, con i testi del primo file
        /// </summary>
        public Glossary Agreed { get; }

        /// <summary>
        /// Conflitti nella forma del messaggio di A2
        /// </summary>
        public JsonObject Conflicts { get; set; }

        /// <summary>
        /// Chiavi dei termini in conflitto
        /// </summary>
        public HashSet<string> TermConflicts { get; }

        /// <summary>
        /// Chiavi dei personaggi in conflitto
        /// </summary>
        public HashSet<string> CharacterConflicts { get; }

        /// <summary>
        /// Chiavi delle coppie in conflitto
        /// </summary>
        public HashSet<string> AddressingConflicts { get; }

        /// <summary>
        /// True se restano conflitti da mandare ad A2
        /// </summary>
        public bool HasConflicts
        {
            get { return this.TermConflicts.Count > 0 || this.CharacterConflicts.Count > 0 || this.AddressingConflicts.Count > 0; }
        }

        #endregion
    }
}
