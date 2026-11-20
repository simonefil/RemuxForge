using RemuxForge.Core.Ai;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Riconoscimento del tipo delle unità di un progetto: ogni combinazione di stile, attore ed effetto delle righe ASS/SSA e, nei
    /// file SRT, ogni tratta ripetuta fra i file. Il file tradotto è quello sorgente con tradotto solo il testo nella lingua
    /// d'origine. Le righe di un solo carattere (lettere animate una per riga) restano fuori da ogni unità, e il codice decide
    /// da solo le unità che non sono testo da tradurre: quella senza altre righe e quella con lettere ma nessuna nella scrittura
    /// dominante del progetto. Le altre vanno al
    /// modello in una chiamata sola, con gli indizi misurati e righe d'esempio, e il tool della consegna ammette solo le unità
    /// chieste; una tratta SRT riceve un tipo per ogni suo tratto
    /// </summary>
    public class SubtitleKindClassifier
    {
        #region Costanti

        /// <summary>
        /// Nome del tool di consegna
        /// </summary>
        public const string SUBMIT_TOOL = "submit_kinds";

        /// <summary>
        /// Righe d'esempio al massimo per unità ASS/SSA: righe diverse, sparse fra i file dell'unità e dentro ogni file
        /// </summary>
        public const int SAMPLE_LINES = 8;

        /// <summary>
        /// Messaggio di consegna mancante
        /// </summary>
        private const string MISSING_SUBMISSION = "This is not a chat: nobody will answer questions. Call submit_kinds with a kind for every unit.";

        /// <summary>
        /// Template delle instructions
        /// </summary>
        private const string INSTRUCTIONS_TEMPLATE = "You classify the text units of the subtitle files of one project before they are translated from {source_language}. The translated files are the source files with only the {source_language} text translated: everything else stays exactly as it is.\n" +
            "A unit is either a combination of style, actor and effect of the ASS/SSA lines, or in SRT files a stretch of lines repeated across the episode files.\n" +
            "This is not a chat: nobody reads plain text or answers questions. Deliver only by calling submit_kinds with every unit. If the call returns errors, fix them and call submit_kinds again with the complete delivery.\n" +
            "\n" +
            "Kinds\n" +
            "- dialogue: spoken lines, narration, recaps and previews written in {source_language}.\n" +
            "- song: lyrics of an opening, ending or insert song written in {source_language}; they are translated as lyrics.\n" +
            "- sign: on-screen text written in {source_language}: signs, captions, titles, notes and translator notes.\n" +
            "- not_translatable: text that is not {source_language} text to translate: lines in another language or script (romanized lyrics such as romaji, kanji, kana), and fragments of an effect such as single letters or syllables animated one per line. It stays as it is.\n" +
            "\n" +
            "Evidence for each style unit\n" +
            "- style, actor, effect: the fields shared by the lines of the unit.\n" +
            "- lines, distinct_lines: lines with readable text longer than one character, and how many of them differ. A line repeating the text of another at the same times or from the instant it ends, like a layered copy or a frame of an animation, is counted once.\n" +
            "- length: shortest, median and longest line, in characters.\n" +
            "- karaoke_share: share of the lines with karaoke timing or a karaoke effect.\n" +
            "- positioned_share: share of the lines with an explicit position on screen.\n" +
            "- overlapping_share: share of the lines shown at the same time as another line of the unit; max_on_screen: most lines of the unit shown at the same instant. Letters or syllables animated one per line overlap many times.\n" +
            "- repeated_share: share of the lines inside stretches repeated across episode files, like an opening or a recap.\n" +
            "- files: episode files where the unit appears, out of the project files.\n" +
            "- samples: distinct lines of the unit, spread across its files.\n" +
            "\n" +
            "Evidence for each SRT stretch\n" +
            "- files: episode files where the stretch appears, out of the project files.\n" +
            "- lines: every line of the stretch, numbered from 1 in time order.\n" +
            "\n" +
            "Rules\n" +
            "- Read the text first: the language of the text decides not_translatable before any other evidence.\n" +
            "- A style unit whose lines are in different kinds takes the kind of most of its lines.\n" +
            "- An SRT stretch can hold parts of different kinds, like a recap followed by an opening song: deliver segments, each with the number of its first line and its kind, in order, the first starting at line 1. A stretch of one kind is one segment.";

        /// <summary>
        /// Descrizione del tool di consegna
        /// </summary>
        private const string TOOL_DESCRIPTION = "Submit the kind of every unit of the project. The code validates it: if invalid you receive the errors and must submit all the units again.";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Valori di kind nello schema, nell'ordine di TranslationStyleKind
        /// </summary>
        private static readonly string[] s_kinds = new string[] { "dialogue", "song", "sign", "not_translatable" };

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Nome inglese della lingua d'origine del progetto
        /// </summary>
        private readonly string _sourceLanguage;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="sourceLanguage">Lingua d'origine ISO 639-2 del progetto</param>
        public SubtitleKindClassifier(string sourceLanguage)
        {
            this._sourceLanguage = TranslationRequestBuilder.GetEnglishName(sourceLanguage ?? "");
            this.Instructions = INSTRUCTIONS_TEMPLATE.Replace("{source_language}", this._sourceLanguage);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Unità del progetto con gli indizi, la decisione del codice dove basta e il messaggio per il modello. I documenti sono
        /// letti senza tipi: tutte le righe leggibili ci sono
        /// </summary>
        /// <param name="files">Identificativo e documento di ogni file, nell'ordine del progetto</param>
        /// <param name="context">Campo serie/contesto del progetto</param>
        /// <returns>Piano del riconoscimento</returns>
        public SubtitleKindPlan Plan(IReadOnlyList<KeyValuePair<string, SubtitleTranslationDocument>> files, string context)
        {
            SubtitleKindPlan plan = new SubtitleKindPlan();

            // Battute dentro le tratte ripetute fra i file; i cartelli in movimento non sono una ripetizione fra episodi
            List<SharedStretch> stretches = SharedStretchDetector.Detect(files);
            HashSet<string> repeated = new HashSet<string>(StringComparer.Ordinal);
            foreach (SharedStretch stretch in stretches)
            {
                if (stretch.MovingSigns)
                    continue;
                foreach (SharedStretchOccurrence occurrence in stretch.Occurrences)
                {
                    foreach (int id in occurrence.Ids)
                        repeated.Add(occurrence.FileId + "|" + id.ToString(CultureInfo.InvariantCulture));
                }
            }

            // Combinazioni ASS/SSA sommate sui file, nell'ordine della prima riga
            foreach (KeyValuePair<string, SubtitleTranslationDocument> file in files)
            {
                Dictionary<int, SubtitleCue> byPosition = new Dictionary<int, SubtitleCue>();
                foreach (SubtitleCue cue in file.Value.Cues)
                    byPosition[cue.Position] = cue;

                foreach (SubtitleStyleUsage usage in file.Value.Styles)
                {
                    if (usage.Texts.Count == 0)
                        continue;

                    SubtitleKindUnit unit = plan.Units.Find(u => !u.IsStretch && u.Style == usage.Name && u.Actor == usage.Actor && u.Effect == usage.Effect);
                    if (unit == null)
                    {
                        unit = new SubtitleKindUnit(usage.Name, usage.Actor, usage.Effect, false);
                        plan.Units.Add(unit);
                    }
                    unit.KaraokeLines += usage.KaraokeLines;
                    unit.PositionLines += usage.PositionLines;
                    unit.OverlappingLines += usage.OverlappingLines;
                    unit.MaxConcurrent = Math.Max(unit.MaxConcurrent, usage.MaxConcurrent);
                    unit.Files++;
                    // Le righe di un solo carattere restano fuori dall'unità: il documento le lascia già come sono
                    for (int i = 0; i < usage.Texts.Count; i++)
                    {
                        if (new StringInfo(usage.Texts[i]).LengthInTextElements == 1)
                            continue;
                        if (usage.Continued[i])
                            unit.Continued.Add(unit.Texts.Count);
                        unit.Texts.Add(usage.Texts[i]);
                        unit.TextFileIds.Add(file.Key);
                        SubtitleCue cue;
                        if (!byPosition.TryGetValue(usage.Positions[i], out cue) || !cue.Id.HasValue)
                            continue;
                        unit.TranslatableLines++;
                        if (repeated.Contains(file.Key + "|" + cue.Id.Value.ToString(CultureInfo.InvariantCulture)))
                            unit.RepeatedLines++;
                    }
                }
            }

            // Tratte SRT ripetute fra i file
            foreach (SharedStretch stretch in stretches)
            {
                if (stretch.MovingSigns || stretch.Format != SubtitleTranslationFormat.Srt)
                    continue;

                SubtitleKindUnit unit = new SubtitleKindUnit("", "", "", true);
                unit.Texts.AddRange(stretch.Lines);
                unit.TranslatableLines = stretch.Lines.Count;
                unit.RepeatedLines = stretch.Lines.Count;
                HashSet<string> covered = new HashSet<string>(StringComparer.Ordinal);
                foreach (SharedStretchOccurrence occurrence in stretch.Occurrences)
                    covered.Add(occurrence.FileId);
                unit.Files = covered.Count;
                plan.Units.Add(unit);
            }

            // Scrittura dominante del progetto: quella con più lettere su tutte le unità
            Dictionary<string, int> total = new Dictionary<string, int>(StringComparer.Ordinal);
            List<Dictionary<string, int>> scripts = new List<Dictionary<string, int>>();
            foreach (SubtitleKindUnit unit in plan.Units)
            {
                Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (string text in unit.Texts)
                {
                    foreach (Rune rune in text.EnumerateRunes())
                    {
                        if (!Rune.IsLetter(rune))
                            continue;

                        // Blocchi Unicode grossolani: latino, greco, cirillico, CJK con kana, hangul; il resto per blocco di 128
                        int c = rune.Value;
                        string script;
                        if (c <= 0x024F || (c >= 0x1E00 && c <= 0x1EFF) || (c >= 0x2C60 && c <= 0x2C7F) || (c >= 0xA720 && c <= 0xA7FF) || (c >= 0xFF21 && c <= 0xFF5A))
                        {
                            script = "latin";
                        }
                        else if ((c >= 0x0370 && c <= 0x03FF) || (c >= 0x1F00 && c <= 0x1FFF))
                        {
                            script = "greek";
                        }
                        else if (c >= 0x0400 && c <= 0x052F)
                        {
                            script = "cyrillic";
                        }
                        else if ((c >= 0x3040 && c <= 0x30FF) || (c >= 0x31F0 && c <= 0x31FF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF) ||
                            (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF66 && c <= 0xFF9F) || c >= 0x20000)
                        {
                            script = "cjk";
                        }
                        else if ((c >= 0x1100 && c <= 0x11FF) || (c >= 0x3130 && c <= 0x318F) || (c >= 0xAC00 && c <= 0xD7AF))
                        {
                            script = "hangul";
                        }
                        else
                        {
                            script = "block" + (c >> 7).ToString(CultureInfo.InvariantCulture);
                        }
                        counts[script] = counts.GetValueOrDefault(script) + 1;
                        total[script] = total.GetValueOrDefault(script) + 1;
                    }
                }
                scripts.Add(counts);
            }
            string dominant = "";
            foreach (KeyValuePair<string, int> count in total)
            {
                if (dominant.Length == 0 || count.Value > total[dominant])
                    dominant = count.Key;
            }

            JsonArray askedStyles = new JsonArray();
            JsonArray askedStretches = new JsonArray();
            for (int i = 0; i < plan.Units.Count; i++)
            {
                SubtitleKindUnit unit = plan.Units[i];
                unit.Id = "u" + (i + 1).ToString(CultureInfo.InvariantCulture);

                // Lunghezze in caratteri visibili delle righe contate e righe diverse, nell'ordine dei file
                List<int> lengths = new List<int>();
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                List<List<string>> distinct = new List<List<string>>();
                string lastFile = null;
                for (int t = 0; t < unit.Texts.Count; t++)
                {
                    if (!unit.Continued.Contains(t))
                        lengths.Add(new StringInfo(unit.Texts[t]).LengthInTextElements);
                    string fileId = t < unit.TextFileIds.Count ? unit.TextFileIds[t] : "";
                    if (fileId != lastFile || distinct.Count == 0)
                    {
                        distinct.Add(new List<string>());
                        lastFile = fileId;
                    }
                    if (seen.Add(unit.Texts[t]))
                        distinct[distinct.Count - 1].Add(unit.Texts[t]);
                }
                lengths.Sort();

                if (unit.Texts.Count == 0 || (scripts[i].Count > 0 && !scripts[i].ContainsKey(dominant)))
                {
                    unit.Kind = TranslationStyleKind.NotTranslatable;
                    continue;
                }

                plan.AskedIds.Add(unit.Id);
                if (unit.IsStretch)
                {
                    JsonArray numbered = new JsonArray();
                    for (int t = 0; t < unit.Texts.Count; t++)
                        numbered.Add(new JsonObject { ["line"] = t + 1, ["text"] = unit.Texts[t] });
                    askedStretches.Add(new JsonObject
                    {
                        ["unit"] = unit.Id,
                        ["files"] = unit.Files.ToString(CultureInfo.InvariantCulture) + "/" + files.Count.ToString(CultureInfo.InvariantCulture),
                        ["lines"] = numbered
                    });
                    plan.AskedStretchIds.Add(unit.Id);
                    continue;
                }

                // Esempi: a turno fra i file che ne hanno ancora, e dentro ogni file a intervalli regolari fra le righe diverse
                int[] quota = new int[distinct.Count];
                for (int taken = 0, f = 0; taken < SAMPLE_LINES && taken < seen.Count; f = (f + 1) % distinct.Count)
                {
                    if (quota[f] >= distinct[f].Count)
                        continue;
                    quota[f]++;
                    taken++;
                }
                JsonArray samples = new JsonArray();
                for (int f = 0; f < distinct.Count; f++)
                {
                    for (int s = 0; s < quota[f]; s++)
                        samples.Add(distinct[f][(int)((long)s * distinct[f].Count / quota[f])]);
                }

                askedStyles.Add(new JsonObject
                {
                    ["unit"] = unit.Id,
                    ["style"] = unit.Style,
                    ["actor"] = unit.Actor,
                    ["effect"] = unit.Effect,
                    ["lines"] = lengths.Count,
                    ["distinct_lines"] = seen.Count,
                    ["length"] = new JsonObject { ["min"] = lengths[0], ["median"] = lengths[lengths.Count / 2], ["max"] = lengths[lengths.Count - 1] },
                    ["karaoke_share"] = Math.Round((double)unit.KaraokeLines / lengths.Count, 2),
                    ["positioned_share"] = Math.Round((double)unit.PositionLines / lengths.Count, 2),
                    ["overlapping_share"] = Math.Round((double)unit.OverlappingLines / lengths.Count, 2),
                    ["max_on_screen"] = unit.MaxConcurrent,
                    ["repeated_share"] = unit.TranslatableLines > 0 ? Math.Round((double)unit.RepeatedLines / unit.TranslatableLines, 2) : 0,
                    ["files"] = unit.Files.ToString(CultureInfo.InvariantCulture) + "/" + files.Count.ToString(CultureInfo.InvariantCulture),
                    ["samples"] = samples
                });
            }

            if (plan.AskedIds.Count == 0)
                return plan;

            StringBuilder message = new StringBuilder();
            message.Append("SOURCE LANGUAGE: ").Append(this._sourceLanguage).Append('\n');
            if (!string.IsNullOrWhiteSpace(context))
                message.Append("USER CONTEXT:\n").Append(context.Trim()).Append('\n');
            message.Append("PROJECT FILES: ").Append(files.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (askedStyles.Count > 0)
                message.Append("STYLE UNITS:\n").Append(TranslationRequestBuilder.ToModelJson(askedStyles)).Append('\n');
            if (askedStretches.Count > 0)
                message.Append("SRT STRETCHES:\n").Append(TranslationRequestBuilder.ToModelJson(askedStretches)).Append('\n');
            plan.Message = message.ToString();

            JsonObject properties = new JsonObject();
            JsonArray required = new JsonArray();
            List<string> styleIds = plan.AskedIds.FindAll(id => !plan.AskedStretchIds.Contains(id));
            if (styleIds.Count > 0)
            {
                properties["styles"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["unit"] = new JsonObject { ["type"] = "string", ["enum"] = GlossaryBuilder.CreateArray(styleIds) },
                            ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = GlossaryBuilder.CreateArray(s_kinds) }
                        },
                        ["required"] = GlossaryBuilder.CreateArray(new string[] { "unit", "kind" }),
                        ["additionalProperties"] = false
                    }
                };
                required.Add("styles");
            }
            if (plan.AskedStretchIds.Count > 0)
            {
                properties["stretches"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["unit"] = new JsonObject { ["type"] = "string", ["enum"] = GlossaryBuilder.CreateArray(plan.AskedStretchIds) },
                            ["segments"] = new JsonObject
                            {
                                ["type"] = "array",
                                ["items"] = new JsonObject
                                {
                                    ["type"] = "object",
                                    ["properties"] = new JsonObject
                                    {
                                        ["first_line"] = new JsonObject { ["type"] = "integer" },
                                        ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = GlossaryBuilder.CreateArray(s_kinds) }
                                    },
                                    ["required"] = GlossaryBuilder.CreateArray(new string[] { "first_line", "kind" }),
                                    ["additionalProperties"] = false
                                }
                            }
                        },
                        ["required"] = GlossaryBuilder.CreateArray(new string[] { "unit", "segments" }),
                        ["additionalProperties"] = false
                    }
                };
                required.Add("stretches");
            }
            plan.Tool = new JsonObject
            {
                ["type"] = "function",
                ["name"] = SUBMIT_TOOL,
                ["description"] = TOOL_DESCRIPTION,
                ["strict"] = true,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                    ["additionalProperties"] = false
                }
            };
            return plan;
        }

        /// <summary>
        /// Legge una risposta: accoda all'ingresso gli item ricevuti e, senza consegna valida, gli esiti per il giro di correzione.
        /// Con la consegna valida le unità chieste prendono il loro tipo, e ogni riga di una tratta quello del suo tratto
        /// </summary>
        /// <param name="response">Risposta del servizio</param>
        /// <param name="input">Ingresso della sequenza, a cui si accodano item ed esiti</param>
        /// <param name="plan">Piano del riconoscimento</param>
        /// <returns>Consegna valida oppure gli errori dell'ultima consegna</returns>
        public TranslationDelivery ReadResponse(AiResponseResult response, JsonArray input, SubtitleKindPlan plan)
        {
            TranslationDelivery delivery = new TranslationDelivery();
            List<JsonObject> calls = new List<JsonObject>();
            foreach (JsonObject item in response.Items)
            {
                input.Add(item.DeepClone());
                if (AiJson.GetString(item, "type") == "function_call")
                    calls.Add(item);
            }

            if (calls.Count == 0)
            {
                delivery.Errors.Add("NO_SUBMISSION: " + MISSING_SUBMISSION);
                input.Add(TranslationRequestBuilder.CreateUserMessage(MISSING_SUBMISSION));
                return delivery;
            }

            Dictionary<string, List<TranslationStyleKind>> accepted = null;
            List<KeyValuePair<string, string>> outputs = new List<KeyValuePair<string, string>>();
            foreach (JsonObject call in calls)
            {
                List<string> errors = new List<string>();
                Dictionary<string, List<TranslationStyleKind>> kinds = new Dictionary<string, List<TranslationStyleKind>>(StringComparer.Ordinal);
                if (accepted != null)
                {
                    errors.Add("DUPLICATE_SUBMISSION: only one submission is used; this one was ignored.");
                }
                else if (AiJson.GetString(call, "name") != SUBMIT_TOOL)
                {
                    errors.Add("ARGS_INVALID: unknown tool '" + AiJson.GetString(call, "name") + "'.");
                }
                else
                {
                    JsonObject arguments = null;
                    try
                    {
                        arguments = JsonNode.Parse(AiJson.GetString(call, "arguments") ?? "null") as JsonObject;
                    }
                    catch (JsonException)
                    {
                        arguments = null;
                    }

                    JsonArray styles = arguments?["styles"] as JsonArray ?? new JsonArray();
                    JsonArray stretches = arguments?["stretches"] as JsonArray ?? new JsonArray();
                    if (arguments == null)
                        errors.Add("ARGS_INVALID: arguments are not an object with the units.");
                    for (int i = 0; i < styles.Count; i++)
                    {
                        JsonObject unit = styles[i] as JsonObject;
                        string id = unit != null ? AiJson.GetString(unit, "unit") : null;
                        int kind = Array.IndexOf(s_kinds, unit != null ? AiJson.GetString(unit, "kind") : null);
                        if (id == null || !plan.AskedIds.Contains(id) || plan.AskedStretchIds.Contains(id) || kind < 0)
                        {
                            errors.Add("ARGS_INVALID styles[" + i.ToString(CultureInfo.InvariantCulture) + "]: unknown unit or kind.");
                        }
                        else if (!kinds.TryAdd(id, new List<TranslationStyleKind> { (TranslationStyleKind)kind }))
                        {
                            errors.Add("UNIT_DUPLICATE " + id + ": the unit has more than one kind; deliver it once.");
                        }
                    }
                    for (int i = 0; i < stretches.Count; i++)
                    {
                        JsonObject unit = stretches[i] as JsonObject;
                        string id = unit != null ? AiJson.GetString(unit, "unit") : null;
                        JsonArray segments = unit?["segments"] as JsonArray;
                        if (id == null || !plan.AskedStretchIds.Contains(id) || segments == null)
                        {
                            errors.Add("ARGS_INVALID stretches[" + i.ToString(CultureInfo.InvariantCulture) + "]: unknown unit or segments.");
                            continue;
                        }

                        // Tratti in ordine dalla riga 1, ognuno fino al seguente: un tipo per ogni riga della tratta
                        int count = plan.Units.Find(u => u.Id == id).Texts.Count;
                        List<TranslationStyleKind> lineKinds = new List<TranslationStyleKind>();
                        string invalid = segments.Count == 0 ? "no segments" : null;
                        for (int s = 0; s < segments.Count && invalid == null; s++)
                        {
                            JsonObject segment = segments[s] as JsonObject;
                            int kind = Array.IndexOf(s_kinds, segment != null ? AiJson.GetString(segment, "kind") : null);
                            int first = 0;
                            bool numbered = segment?["first_line"] is JsonValue value && value.TryGetValue(out first);
                            if (!numbered || kind < 0)
                                invalid = "segment " + (s + 1).ToString(CultureInfo.InvariantCulture) + " has no valid first_line or kind";
                            else if (first != lineKinds.Count + 1 && (s == 0 || first <= lineKinds.Count))
                                invalid = s == 0 ? "the first segment must start at line 1" : "segment " + (s + 1).ToString(CultureInfo.InvariantCulture) + " does not start after the previous one";
                            else if (first > count)
                                invalid = "segment " + (s + 1).ToString(CultureInfo.InvariantCulture) + " starts after the last line " + count.ToString(CultureInfo.InvariantCulture);
                            else
                            {
                                while (lineKinds.Count < first - 1)
                                    lineKinds.Add(lineKinds[lineKinds.Count - 1]);
                                lineKinds.Add((TranslationStyleKind)kind);
                            }
                        }
                        while (invalid == null && lineKinds.Count < count)
                            lineKinds.Add(lineKinds[lineKinds.Count - 1]);

                        if (invalid != null)
                        {
                            errors.Add("SEGMENTS_INVALID " + id + ": " + invalid + ".");
                        }
                        else if (!kinds.TryAdd(id, lineKinds))
                        {
                            errors.Add("UNIT_DUPLICATE " + id + ": the stretch is delivered more than once; deliver it once.");
                        }
                    }
                    foreach (string id in plan.AskedIds)
                    {
                        if (arguments != null && !kinds.ContainsKey(id) && !errors.Exists(e => e.StartsWith("SEGMENTS_INVALID " + id + ":", StringComparison.Ordinal)))
                            errors.Add("UNIT_MISSING " + id + ": no kind delivered for this unit.");
                    }

                    if (errors.Count == 0)
                        accepted = kinds;
                    else
                        delivery.Errors = errors;
                }

                outputs.Add(new KeyValuePair<string, string>(AiJson.GetString(call, "call_id"),
                    new JsonObject { ["valid"] = errors.Count == 0, ["errors"] = GlossaryBuilder.CreateArray(errors) }.ToJsonString()));
            }

            if (accepted != null)
            {
                delivery.Errors.Clear();
                foreach (SubtitleKindUnit unit in plan.Units)
                {
                    List<TranslationStyleKind> kinds;
                    if (!accepted.TryGetValue(unit.Id, out kinds))
                        continue;
                    if (unit.IsStretch)
                        unit.LineKinds = kinds;
                    else
                        unit.Kind = kinds[0];
                }
                return delivery;
            }

            foreach (KeyValuePair<string, string> output in outputs)
                input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = output.Key, ["output"] = output.Value });

            return delivery;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Instructions della chiamata
        /// </summary>
        public string Instructions { get; }

        #endregion
    }

    /// <summary>
    /// Piano del riconoscimento dei tipi: unità, quelle chieste al modello, messaggio e tool
    /// </summary>
    public class SubtitleKindPlan
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public SubtitleKindPlan()
        {
            this.Units = new List<SubtitleKindUnit>();
            this.AskedIds = new List<string>();
            this.AskedStretchIds = new List<string>();
            this.Message = null;
            this.Tool = null;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Porta i tipi riconosciuti nella tabella degli stili e crea le tratte SRT del progetto
        /// </summary>
        /// <param name="styles">Combinazioni del progetto, che prendono il tipo della loro unità</param>
        /// <returns>Tratte SRT col tipo di ogni riga, vuoto se non ce ne sono</returns>
        public List<TranslationProjectStretch> Apply(List<TranslationProjectStyle> styles)
        {
            List<TranslationProjectStretch> stretches = new List<TranslationProjectStretch>();
            foreach (SubtitleKindUnit unit in this.Units)
            {
                if (unit.IsStretch)
                {
                    TranslationProjectStretch stretch = new TranslationProjectStretch { Lines = new List<string>(unit.Texts) };
                    for (int i = 0; i < unit.Texts.Count; i++)
                        stretch.Kinds.Add(unit.LineKinds != null ? unit.LineKinds[i] : unit.Kind.Value);
                    stretches.Add(stretch);
                    continue;
                }

                TranslationProjectStyle style = styles.Find(s => s.Name == unit.Style && s.Actor == unit.Actor && s.Effect == unit.Effect);
                if (style != null)
                    style.Kind = unit.Kind;
            }

            return stretches;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Unità del progetto: le combinazioni ASS/SSA nell'ordine della prima riga, poi le tratte SRT
        /// </summary>
        public List<SubtitleKindUnit> Units { get; }

        /// <summary>
        /// Unità chieste al modello, quelle che il codice non ha deciso
        /// </summary>
        public List<string> AskedIds { get; }

        /// <summary>
        /// Tratte SRT fra le unità chieste, che ricevono un tipo per tratto
        /// </summary>
        public List<string> AskedStretchIds { get; }

        /// <summary>
        /// Messaggio per il modello, null se il codice ha deciso tutto
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Tool della consegna, null se il codice ha deciso tutto
        /// </summary>
        public JsonObject Tool { get; set; }

        #endregion
    }

    /// <summary>
    /// Unità del riconoscimento: una combinazione di stile, attore ed effetto ASS/SSA sommata sui file o una tratta SRT ripetuta
    /// fra i file
    /// </summary>
    public class SubtitleKindUnit
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="style">Nome dello stile, vuoto per una tratta</param>
        /// <param name="actor">Attore, vuoto per una tratta</param>
        /// <param name="effect">Effetto, vuoto per una tratta</param>
        /// <param name="isStretch">True per una tratta SRT</param>
        public SubtitleKindUnit(string style, string actor, string effect, bool isStretch)
        {
            this.Id = "";
            this.Style = style;
            this.Actor = actor;
            this.Effect = effect;
            this.IsStretch = isStretch;
            this.Texts = new List<string>();
            this.Continued = new HashSet<int>();
            this.TextFileIds = new List<string>();
            this.Kind = null;
            this.LineKinds = null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Identificativo nel messaggio e nel tool
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Nome dello stile, vuoto per una tratta
        /// </summary>
        public string Style { get; }

        /// <summary>
        /// Campo Name (attore) delle righe, vuoto per una tratta
        /// </summary>
        public string Actor { get; }

        /// <summary>
        /// Campo Effect delle righe, vuoto per una tratta
        /// </summary>
        public string Effect { get; }

        /// <summary>
        /// True per una tratta SRT
        /// </summary>
        public bool IsStretch { get; }

        /// <summary>
        /// Testi leggibili delle righe: della combinazione nell'ordine dei file, della tratta nell'ordine di tempo
        /// </summary>
        public List<string> Texts { get; }

        /// <summary>
        /// Indici in Texts delle righe che continuano una riga già contata, fuori dal conto e dalle lunghezze degli indizi
        /// </summary>
        public HashSet<int> Continued { get; }

        /// <summary>
        /// File di ogni riga di Texts per una combinazione, vuoto per una tratta
        /// </summary>
        public List<string> TextFileIds { get; }

        /// <summary>
        /// Righe con karaoke o Effect del templater
        /// </summary>
        public int KaraokeLines { get; set; }

        /// <summary>
        /// Righe con posizione
        /// </summary>
        public int PositionLines { get; set; }

        /// <summary>
        /// Righe sovrapposte nel tempo a un'altra riga dell'unità nello stesso file
        /// </summary>
        public int OverlappingLines { get; set; }

        /// <summary>
        /// Massimo di righe dell'unità sullo schermo nello stesso istante
        /// </summary>
        public int MaxConcurrent { get; set; }

        /// <summary>
        /// Battute traducibili dell'unità
        /// </summary>
        public int TranslatableLines { get; set; }

        /// <summary>
        /// Battute traducibili dentro le tratte ripetute fra i file
        /// </summary>
        public int RepeatedLines { get; set; }

        /// <summary>
        /// File in cui l'unità compare
        /// </summary>
        public int Files { get; set; }

        /// <summary>
        /// Tipo deciso dal codice per l'unità intera o consegnato dal modello per una combinazione, null finché manca
        /// </summary>
        public TranslationStyleKind? Kind { get; set; }

        /// <summary>
        /// Tipo di ogni riga di una tratta consegnato dal modello, null per le combinazioni e per le tratte decise dal codice
        /// </summary>
        public List<TranslationStyleKind> LineKinds { get; set; }

        #endregion
    }
}
