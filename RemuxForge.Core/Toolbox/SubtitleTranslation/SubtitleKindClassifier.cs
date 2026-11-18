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
    /// Riconoscimento del tipo delle unità di un progetto: ogni stile ASS/SSA e, nei file SRT, ogni tratta ripetuta fra i file.
    /// Il codice decide da solo l'unità che non ha nessuna lettera nella scrittura dominante del progetto: non è nella lingua
    /// d'origine. Le altre vanno al modello in una chiamata sola, con gli indizi misurati e alcune righe d'esempio, e il tool
    /// della consegna ammette solo le unità chieste
    /// </summary>
    public class SubtitleKindClassifier
    {
        #region Costanti

        /// <summary>
        /// Nome del tool di consegna
        /// </summary>
        public const string SUBMIT_TOOL = "submit_kinds";

        /// <summary>
        /// Righe d'esempio al massimo per unità, prese a intervalli regolari
        /// </summary>
        public const int SAMPLE_LINES = 8;

        /// <summary>
        /// Messaggio di consegna mancante
        /// </summary>
        private const string MISSING_SUBMISSION = "This is not a chat: nobody will answer questions. Call submit_kinds with one kind for every unit.";

        /// <summary>
        /// Template delle instructions
        /// </summary>
        private const string INSTRUCTIONS_TEMPLATE = "You classify the text units of the subtitle files of one project before they are translated from {source_language}. A unit is an ASS/SSA style, or in SRT files a stretch of lines repeated across the episode files.\n" +
            "This is not a chat: nobody reads plain text or answers questions. Deliver only by calling submit_kinds with exactly one kind for every unit. If the call returns errors, fix them and call submit_kinds again with the complete delivery.\n" +
            "\n" +
            "Kinds\n" +
            "- dialogue: spoken lines, narration, recaps and previews written in {source_language}.\n" +
            "- song: lyrics of an opening, ending or insert song written in {source_language}; they are translated as lyrics.\n" +
            "- sign: on-screen text written in {source_language}: signs, captions, titles, notes and translator notes.\n" +
            "- not_source_language: text that is not written in {source_language}, such as romanized lyrics (romaji) or lines in another language; it is never translated and stays as it is.\n" +
            "\n" +
            "Evidence for each unit\n" +
            "- lines: lines with readable text.\n" +
            "- karaoke_share: share of the lines with karaoke timing or a karaoke effect.\n" +
            "- positioned_share: share of the lines with an explicit position on screen.\n" +
            "- repeated_share: share of the lines inside stretches repeated across episode files, like an opening or a recap.\n" +
            "- files: episode files where the unit appears, out of the project files.\n" +
            "- samples: lines of the unit taken at regular intervals.\n" +
            "\n" +
            "Rules\n" +
            "- Read the samples first: the language of the text decides not_source_language before any other evidence.\n" +
            "- A unit whose lines are in different kinds takes the kind of most of its lines.";

        /// <summary>
        /// Descrizione del tool di consegna
        /// </summary>
        private const string TOOL_DESCRIPTION = "Submit the kind of every unit of the project. The code validates it: if invalid you receive the errors and must submit all the units again.";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Valori di kind nello schema, nell'ordine di TranslationStyleKind
        /// </summary>
        private static readonly string[] s_kinds = new string[] { "dialogue", "song", "sign", "not_source_language" };

        #endregion

        #region Variabili di istanza

        /// <summary>
        /// Lingua d'origine del progetto
        /// </summary>
        private readonly string _sourceLanguage;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="sourceLanguage">Lingua d'origine del progetto</param>
        public SubtitleKindClassifier(string sourceLanguage)
        {
            this._sourceLanguage = sourceLanguage ?? "";
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

            // Stili ASS/SSA sommati sui file, nell'ordine della prima riga
            foreach (KeyValuePair<string, SubtitleTranslationDocument> file in files)
            {
                foreach (SubtitleStyleUsage usage in file.Value.Styles)
                {
                    if (usage.Texts.Count == 0)
                        continue;

                    SubtitleKindUnit unit = plan.Units.Find(u => !u.IsStretch && u.Name == usage.Name);
                    if (unit == null)
                    {
                        unit = new SubtitleKindUnit(usage.Name, false);
                        plan.Units.Add(unit);
                    }
                    unit.Texts.AddRange(usage.Texts);
                    unit.KaraokeLines += usage.KaraokeLines;
                    unit.PositionLines += usage.PositionLines;
                    unit.Files++;
                    foreach (SubtitleCue cue in file.Value.Cues)
                    {
                        if (!cue.Id.HasValue || cue.Style != usage.Name)
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

                SubtitleKindUnit unit = new SubtitleKindUnit("", true);
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
                            script = "latin";
                        else if ((c >= 0x0370 && c <= 0x03FF) || (c >= 0x1F00 && c <= 0x1FFF))
                            script = "greek";
                        else if (c >= 0x0400 && c <= 0x052F)
                            script = "cyrillic";
                        else if ((c >= 0x3040 && c <= 0x30FF) || (c >= 0x31F0 && c <= 0x31FF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0x4E00 && c <= 0x9FFF) ||
                            (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFF66 && c <= 0xFF9F) || c >= 0x20000)
                            script = "cjk";
                        else if ((c >= 0x1100 && c <= 0x11FF) || (c >= 0x3130 && c <= 0x318F) || (c >= 0xAC00 && c <= 0xD7AF))
                            script = "hangul";
                        else
                            script = "block" + (c >> 7).ToString(CultureInfo.InvariantCulture);
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

            JsonArray asked = new JsonArray();
            for (int i = 0; i < plan.Units.Count; i++)
            {
                SubtitleKindUnit unit = plan.Units[i];
                unit.Id = "u" + (i + 1).ToString(CultureInfo.InvariantCulture);
                if (scripts[i].Count > 0 && !scripts[i].ContainsKey(dominant))
                {
                    unit.Kind = TranslationStyleKind.NotSourceLanguage;
                    continue;
                }

                JsonArray samples = new JsonArray();
                int count = Math.Min(SAMPLE_LINES, unit.Texts.Count);
                for (int s = 0; s < count; s++)
                    samples.Add(unit.Texts[(int)((long)s * unit.Texts.Count / count)]);
                JsonObject item = new JsonObject { ["unit"] = unit.Id, ["type"] = unit.IsStretch ? "repeated_srt_stretch" : "style" };
                if (!unit.IsStretch)
                {
                    item["name"] = unit.Name;
                    item["lines"] = unit.Texts.Count;
                    item["karaoke_share"] = Math.Round((double)unit.KaraokeLines / unit.Texts.Count, 2);
                    item["positioned_share"] = Math.Round((double)unit.PositionLines / unit.Texts.Count, 2);
                    item["repeated_share"] = unit.TranslatableLines > 0 ? Math.Round((double)unit.RepeatedLines / unit.TranslatableLines, 2) : 0;
                }
                else
                {
                    item["lines"] = unit.Texts.Count;
                }
                item["files"] = unit.Files.ToString(CultureInfo.InvariantCulture) + "/" + files.Count.ToString(CultureInfo.InvariantCulture);
                item["samples"] = samples;
                asked.Add(item);
                plan.AskedIds.Add(unit.Id);
            }

            if (asked.Count == 0)
                return plan;

            StringBuilder message = new StringBuilder();
            message.Append("SOURCE LANGUAGE: ").Append(this._sourceLanguage).Append('\n');
            if (!string.IsNullOrWhiteSpace(context))
                message.Append("USER CONTEXT:\n").Append(context.Trim()).Append('\n');
            message.Append("PROJECT FILES: ").Append(files.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            message.Append("UNITS:\n").Append(TranslationRequestBuilder.ToModelJson(asked));
            plan.Message = message.ToString();
            plan.Tool = new JsonObject
            {
                ["type"] = "function",
                ["name"] = SUBMIT_TOOL,
                ["description"] = TOOL_DESCRIPTION,
                ["strict"] = true,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["units"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["unit"] = new JsonObject { ["type"] = "string", ["enum"] = GlossaryBuilder.CreateArray(plan.AskedIds) },
                                    ["kind"] = new JsonObject { ["type"] = "string", ["enum"] = GlossaryBuilder.CreateArray(s_kinds) }
                                },
                                ["required"] = GlossaryBuilder.CreateArray(new string[] { "unit", "kind" }),
                                ["additionalProperties"] = false
                            }
                        }
                    },
                    ["required"] = GlossaryBuilder.CreateArray(new string[] { "units" }),
                    ["additionalProperties"] = false
                }
            };
            return plan;
        }

        /// <summary>
        /// Legge una risposta: accoda all'ingresso gli item ricevuti e, senza consegna valida, gli esiti per il giro di correzione.
        /// Con la consegna valida le unità chieste prendono il loro tipo
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
                if (AiOAuthFlow.GetString(item, "type") == "function_call")
                    calls.Add(item);
            }

            if (calls.Count == 0)
            {
                delivery.Errors.Add("NO_SUBMISSION: " + MISSING_SUBMISSION);
                input.Add(TranslationRequestBuilder.CreateUserMessage(MISSING_SUBMISSION));
                return delivery;
            }

            Dictionary<string, TranslationStyleKind> accepted = null;
            List<KeyValuePair<string, string>> outputs = new List<KeyValuePair<string, string>>();
            foreach (JsonObject call in calls)
            {
                List<string> errors = new List<string>();
                Dictionary<string, TranslationStyleKind> kinds = new Dictionary<string, TranslationStyleKind>(StringComparer.Ordinal);
                if (accepted != null)
                {
                    errors.Add("DUPLICATE_SUBMISSION: only one submission is used; this one was ignored.");
                }
                else if (AiOAuthFlow.GetString(call, "name") != SUBMIT_TOOL)
                {
                    errors.Add("ARGS_INVALID: unknown tool '" + AiOAuthFlow.GetString(call, "name") + "'.");
                }
                else
                {
                    JsonArray units = null;
                    try
                    {
                        units = (JsonNode.Parse(AiOAuthFlow.GetString(call, "arguments") ?? "null") as JsonObject)?["units"] as JsonArray;
                    }
                    catch (JsonException)
                    {
                        units = null;
                    }

                    if (units == null)
                        errors.Add("ARGS_INVALID: arguments are not an object with the units list.");
                    for (int i = 0; units != null && i < units.Count; i++)
                    {
                        JsonObject unit = units[i] as JsonObject;
                        string id = unit != null ? AiOAuthFlow.GetString(unit, "unit") : null;
                        int kind = Array.IndexOf(s_kinds, unit != null ? AiOAuthFlow.GetString(unit, "kind") : null);
                        if (id == null || !plan.AskedIds.Contains(id) || kind < 0)
                        {
                            errors.Add("ARGS_INVALID units[" + i.ToString(CultureInfo.InvariantCulture) + "]: unknown unit or kind.");
                        }
                        else if (!kinds.TryAdd(id, (TranslationStyleKind)kind))
                        {
                            errors.Add("UNIT_DUPLICATE " + id + ": the unit has more than one kind; deliver it once.");
                        }
                    }
                    foreach (string id in plan.AskedIds)
                    {
                        if (units != null && !kinds.ContainsKey(id))
                            errors.Add("UNIT_MISSING " + id + ": no kind delivered for this unit.");
                    }

                    if (errors.Count == 0)
                        accepted = kinds;
                    else
                        delivery.Errors = errors;
                }

                outputs.Add(new KeyValuePair<string, string>(AiOAuthFlow.GetString(call, "call_id"),
                    new JsonObject { ["valid"] = errors.Count == 0, ["errors"] = GlossaryBuilder.CreateArray(errors) }.ToJsonString()));
            }

            if (accepted != null)
            {
                delivery.Errors.Clear();
                foreach (SubtitleKindUnit unit in plan.Units)
                {
                    TranslationStyleKind kind;
                    if (accepted.TryGetValue(unit.Id, out kind))
                        unit.Kind = kind;
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
            this.Message = null;
            this.Tool = null;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Porta i tipi riconosciuti nella tabella degli stili e crea le tratte SRT del progetto
        /// </summary>
        /// <param name="styles">Stili del progetto, che prendono il tipo della loro unità</param>
        /// <returns>Tratte SRT col tipo, vuoto se non ce ne sono</returns>
        public List<TranslationProjectStretch> Apply(List<TranslationProjectStyle> styles)
        {
            List<TranslationProjectStretch> stretches = new List<TranslationProjectStretch>();
            foreach (SubtitleKindUnit unit in this.Units)
            {
                if (unit.IsStretch)
                {
                    stretches.Add(new TranslationProjectStretch { Lines = new List<string>(unit.Texts), Kind = unit.Kind.Value });
                    continue;
                }

                TranslationProjectStyle style = styles.Find(s => s.Name == unit.Name);
                if (style != null)
                    style.Kind = unit.Kind;
            }

            return stretches;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Unità del progetto: gli stili nell'ordine della prima riga, poi le tratte SRT
        /// </summary>
        public List<SubtitleKindUnit> Units { get; }

        /// <summary>
        /// Unità chieste al modello, quelle che il codice non ha deciso
        /// </summary>
        public List<string> AskedIds { get; }

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
    /// Unità del riconoscimento: uno stile ASS/SSA sommato sui file o una tratta SRT ripetuta fra i file
    /// </summary>
    public class SubtitleKindUnit
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="name">Nome dello stile, vuoto per una tratta</param>
        /// <param name="isStretch">True per una tratta SRT</param>
        public SubtitleKindUnit(string name, bool isStretch)
        {
            this.Id = "";
            this.Name = name;
            this.IsStretch = isStretch;
            this.Texts = new List<string>();
            this.Kind = null;
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
        public string Name { get; }

        /// <summary>
        /// True per una tratta SRT
        /// </summary>
        public bool IsStretch { get; }

        /// <summary>
        /// Testi leggibili delle righe: dello stile nell'ordine dei file, della tratta nell'ordine di tempo
        /// </summary>
        public List<string> Texts { get; }

        /// <summary>
        /// Righe con karaoke o Effect del templater
        /// </summary>
        public int KaraokeLines { get; set; }

        /// <summary>
        /// Righe con posizione
        /// </summary>
        public int PositionLines { get; set; }

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
        /// Tipo deciso dal codice o consegnato dal modello, null finché manca
        /// </summary>
        public TranslationStyleKind? Kind { get; set; }

        #endregion
    }
}
