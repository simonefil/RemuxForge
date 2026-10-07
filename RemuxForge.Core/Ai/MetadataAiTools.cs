using RemuxForge.Core.Metadata;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemuxForge.Core.Ai
{
    /// <summary>
    /// Tool del modello per la generazione preset Metadata: panoramica, interrogazione dei file e invio del preset
    /// </summary>
    public class MetadataAiTools
    {
        #region Costanti

        /// <summary>
        /// Namespace dei tool nella richiesta
        /// </summary>
        public const string TOOL_NAMESPACE = "remuxforge";

        /// <summary>
        /// Nome del tool di interrogazione file
        /// </summary>
        public const string QUERY_FILES_TOOL = "query_files";

        /// <summary>
        /// Nome del tool di invio preset
        /// </summary>
        public const string SUBMIT_PRESET_TOOL = "submit_preset";

        /// <summary>
        /// Pseudo campo che restituisce i tag Matroska dell'elemento
        /// </summary>
        private const string TAGS_FIELD = "tags";

        /// <summary>
        /// Valori distinti mostrati per campo nella panoramica
        /// </summary>
        private const int OVERVIEW_MAX_VALUES = 15;

        /// <summary>
        /// Righe restituite di default da query_files
        /// </summary>
        private const int QUERY_DEFAULT_LIMIT = 50;

        /// <summary>
        /// Righe massime restituite da query_files
        /// </summary>
        private const int QUERY_MAX_LIMIT = 200;

        /// <summary>
        /// Versione schema imposta ai preset inviati
        /// </summary>
        private const int PRESET_SCHEMA_VERSION = 4;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Record dei file caricati, in sola lettura
        /// </summary>
        private readonly List<MkvMetadataRecord> _records;

        /// <summary>
        /// Valutatore usato per leggere i campi e applicare i filtri
        /// </summary>
        private readonly MetadataPipelineEvaluator _evaluator;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="records">Record dei file caricati</param>
        public MetadataAiTools(List<MkvMetadataRecord> records)
        {
            this._records = records != null ? records : new List<MkvMetadataRecord>();
            this._evaluator = new MetadataPipelineEvaluator();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce le definizioni dei tool raggruppate nel namespace
        /// </summary>
        /// <returns>Array tools della richiesta</returns>
        public static JsonArray BuildToolDefinitions()
        {
            JsonArray scopes = new JsonArray("file", "video", "audio", "subtitle");
            JsonArray operators = new JsonArray();
            foreach (string name in Enum.GetNames(typeof(MkvMetadataConditionOperator)))
                operators.Add(name);

            JsonObject condition = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["FieldKey"] = new JsonObject { ["type"] = "string" },
                    ["Operator"] = new JsonObject { ["type"] = "string", ["enum"] = operators },
                    ["Value"] = new JsonObject { ["type"] = "string" },
                    ["Values"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                    ["FromValue"] = new JsonObject { ["type"] = "string" },
                    ["ToValue"] = new JsonObject { ["type"] = "string" },
                    ["Unit"] = new JsonObject { ["type"] = "string" }
                },
                ["required"] = new JsonArray("FieldKey", "Operator"),
                ["additionalProperties"] = false
            };

            JsonObject queryFiles = new JsonObject
            {
                ["type"] = "function",
                ["name"] = QUERY_FILES_TOOL,
                ["description"] = "Read normalized metadata of the loaded files. scope 'file' returns one row per file (file and container fields); 'video', 'audio', 'subtitle' return one row per track of that type, with file and container fields also readable. Use the pseudo field 'tags' to read the Matroska tags of the row. 'where' filters rows with the same semantics as preset field conditions (all in AND). mode 'distinct' returns distinct values with counts per field instead of rows.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["scope"] = new JsonObject { ["type"] = "string", ["enum"] = scopes },
                        ["fields"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Field keys to return; prefix 'original.' reads the value before any change." },
                        ["where"] = new JsonObject { ["type"] = "array", ["items"] = condition },
                        ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("rows", "distinct") },
                        ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "Rows (or distinct values per field) to return, default 50, max 200." },
                        ["offset"] = new JsonObject { ["type"] = "integer" }
                    },
                    ["required"] = new JsonArray("scope", "fields"),
                    ["additionalProperties"] = false
                }
            };

            JsonObject submitPreset = new JsonObject
            {
                ["type"] = "function",
                ["name"] = SUBMIT_PRESET_TOOL,
                ["description"] = "Submit the complete preset JSON. The backend validates it: if invalid you receive the errors with their rule and operation index (0-based) and must fix and resubmit the whole preset; if valid the work is finished.",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["preset_json"] = new JsonObject { ["type"] = "string", ["description"] = "The full preset as a JSON string." }
                    },
                    ["required"] = new JsonArray("preset_json"),
                    ["additionalProperties"] = false
                }
            };

            return new JsonArray(new JsonObject
            {
                ["type"] = "namespace",
                ["name"] = TOOL_NAMESPACE,
                ["description"] = "RemuxForge Metadata editor: inspect the loaded files and submit the preset.",
                ["tools"] = new JsonArray(queryFiles, submitPreset)
            });
        }

        /// <summary>
        /// Costruisce la panoramica iniziale: conteggi e valori distinti dei campi principali
        /// </summary>
        /// <returns>Panoramica JSON</returns>
        public string BuildOverview()
        {
            JsonObject result = new JsonObject();
            JsonObject trackCounts = new JsonObject();
            JsonObject fields = new JsonObject();
            JsonObject tags = new JsonObject();
            MkvMetadataTargetScope[] scopes = new MkvMetadataTargetScope[] { MkvMetadataTargetScope.Container, MkvMetadataTargetScope.Video, MkvMetadataTargetScope.Audio, MkvMetadataTargetScope.Subtitle };

            result["file_count"] = this._records.Count;
            for (int s = 0; s < scopes.Length; s++)
            {
                List<RowContext> rows = this.GetRows(scopes[s]);
                string scopeName = GetScopeName(scopes[s]);
                JsonObject scopeFields = new JsonObject();
                SortedDictionary<string, int> tagNames = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                if (scopes[s] != MkvMetadataTargetScope.Container)
                    trackCounts[scopeName] = rows.Count;

                List<MetadataFieldDefinition> definitions = MetadataFieldRegistry.GetAll();
                for (int f = 0; f < definitions.Count; f++)
                {
                    MetadataFieldDefinition field = definitions[f];
                    if (!field.IsReadable || field.Visibility != MetadataFieldVisibility.Primary || !IsFieldOwnedByScope(field, scopes[s]))
                        continue;

                    JsonArray values = this.BuildDistinctValues(rows, field.Key, OVERVIEW_MAX_VALUES);
                    if (values.Count > 0)
                        scopeFields[field.Key] = values;
                }

                for (int r = 0; r < rows.Count; r++)
                {
                    foreach (KeyValuePair<string, string> tag in GetTags(rows[r]))
                        tagNames[tag.Key] = tagNames.ContainsKey(tag.Key) ? tagNames[tag.Key] + 1 : 1;
                }

                fields[scopeName] = scopeFields;
                if (tagNames.Count > 0)
                {
                    JsonObject scopeTags = new JsonObject();
                    foreach (KeyValuePair<string, int> tag in tagNames)
                        scopeTags[tag.Key] = tag.Value;
                    tags[scopeName] = scopeTags;
                }
            }

            result["track_counts"] = trackCounts;
            result["primary_field_values"] = fields;
            result["tag_names_with_counts"] = tags;
            return result.ToJsonString();
        }

        /// <summary>
        /// Esegue query_files con gli argomenti inviati dal modello
        /// </summary>
        /// <param name="argumentsJson">Argomenti JSON della chiamata</param>
        /// <returns>Risultato JSON per il modello</returns>
        public string QueryFiles(string argumentsJson)
        {
            JsonObject arguments = AiOAuthFlow.TryParseObject(argumentsJson);
            List<string> errors = new List<string>();
            MkvMetadataTargetScope scope;
            List<string> fields = new List<string>();
            List<MkvMetadataFieldCondition> conditions = new List<MkvMetadataFieldCondition>();
            List<RowContext> rows;
            List<RowContext> matched = new List<RowContext>();
            int limit;
            int offset;

            if (arguments == null)
                return CreateErrorOutput(new List<string> { "Arguments are not a JSON object." });

            if (!TryParseScope(AiOAuthFlow.GetString(arguments, "scope"), out scope))
                errors.Add("Unknown scope '" + AiOAuthFlow.GetString(arguments, "scope") + "'. Use file, video, audio or subtitle.");

            JsonArray fieldArray = arguments["fields"] as JsonArray;
            if (fieldArray == null || fieldArray.Count == 0)
                errors.Add("'fields' must list at least one field key.");
            else
            {
                for (int i = 0; i < fieldArray.Count; i++)
                {
                    string key = fieldArray[i] != null ? fieldArray[i].ToString().Trim() : "";
                    string error = ValidateQueryField(key, scope, true);
                    if (error.Length > 0)
                        errors.Add(error);
                    else
                        fields.Add(key);
                }
            }

            JsonArray whereArray = arguments["where"] as JsonArray;
            if (whereArray != null)
            {
                for (int i = 0; i < whereArray.Count; i++)
                {
                    MkvMetadataFieldCondition condition;
                    string error = TryParseCondition(whereArray[i] as JsonObject, scope, out condition);
                    if (error.Length > 0)
                        errors.Add("where[" + i + "]: " + error);
                    else
                        conditions.Add(condition);
                }
            }

            if (errors.Count > 0)
                return CreateErrorOutput(errors);

            limit = Math.Clamp(GetInt(arguments, "limit", QUERY_DEFAULT_LIMIT), 1, QUERY_MAX_LIMIT);
            offset = Math.Max(0, GetInt(arguments, "offset", 0));
            rows = this.GetRows(scope);
            for (int i = 0; i < rows.Count; i++)
            {
                bool match = true;
                for (int c = 0; c < conditions.Count && match; c++)
                    match = this._evaluator.IsFieldConditionMatched(rows[i].Record, rows[i].Track, conditions[c]);

                if (match)
                    matched.Add(rows[i]);
            }

            JsonObject result = new JsonObject();
            result["matched"] = matched.Count;
            if (AiOAuthFlow.GetString(arguments, "mode") == "distinct")
            {
                JsonObject distinct = new JsonObject();
                for (int f = 0; f < fields.Count; f++)
                    distinct[fields[f]] = this.BuildDistinctValues(matched, fields[f], limit);

                result["distinct"] = distinct;
                return result.ToJsonString();
            }

            JsonArray outputRows = new JsonArray();
            for (int i = offset; i < matched.Count && outputRows.Count < limit; i++)
            {
                JsonObject row = new JsonObject();
                row["file_index"] = matched[i].FileIndex;
                row["file_name"] = matched[i].Record.FileInfo.FileName;
                if (matched[i].Track != null)
                    row["track"] = matched[i].Track.TrackSelector;

                for (int f = 0; f < fields.Count; f++)
                {
                    if (string.Equals(fields[f], TAGS_FIELD, StringComparison.OrdinalIgnoreCase))
                    {
                        JsonObject tagObject = new JsonObject();
                        foreach (KeyValuePair<string, string> tag in GetTags(matched[i]))
                            tagObject[tag.Key] = tag.Value;
                        row[TAGS_FIELD] = tagObject;
                    }
                    else
                    {
                        row[fields[f]] = this.ReadField(matched[i], fields[f]);
                    }
                }

                outputRows.Add(row);
            }

            result["offset"] = offset;
            result["returned"] = outputRows.Count;
            result["rows"] = outputRows;
            return result.ToJsonString();
        }

        /// <summary>
        /// Valida il preset inviato dal modello
        /// </summary>
        /// <param name="argumentsJson">Argomenti JSON della chiamata</param>
        /// <param name="outcome">Esito: preset valido o errori</param>
        /// <returns>Risultato JSON per il modello</returns>
        public string SubmitPreset(string argumentsJson, out AiPresetGenerationResult outcome)
        {
            JsonObject arguments = AiOAuthFlow.TryParseObject(argumentsJson);
            string presetJson = arguments != null ? AiOAuthFlow.GetString(arguments, "preset_json") : "";
            MetadataPresetService presetService = new MetadataPresetService("");
            JsonObject presetNode;
            MkvMetadataPreset preset;

            outcome = new AiPresetGenerationResult();
            presetNode = AiOAuthFlow.TryParseObject(presetJson);
            if (presetNode == null)
            {
                outcome.Errors.Add("preset_json is not a valid JSON object.");
                return CreateErrorOutput(outcome.Errors);
            }

            // La versione schema non è una scelta del modello: vale sempre quella corrente
            List<string> versionKeys = new List<string>();
            foreach (KeyValuePair<string, JsonNode> property in presetNode)
            {
                if (string.Equals(property.Key, "SchemaVersion", StringComparison.OrdinalIgnoreCase))
                    versionKeys.Add(property.Key);
            }
            for (int i = 0; i < versionKeys.Count; i++)
                presetNode.Remove(versionKeys[i]);
            presetNode["SchemaVersion"] = PRESET_SCHEMA_VERSION;

            try
            {
                preset = presetService.LoadFromJson(presetNode.ToJsonString());
                outcome.PresetJson = presetService.SerializeToJson(preset);
            }
            catch (JsonException ex)
            {
                outcome.Errors.Add(ex.Message);
                return CreateErrorOutput(outcome.Errors);
            }
            catch (InvalidOperationException ex)
            {
                outcome.Errors.AddRange(ex.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                return CreateErrorOutput(outcome.Errors);
            }
            catch (ArgumentException ex)
            {
                outcome.Errors.Add(ex.Message);
                return CreateErrorOutput(outcome.Errors);
            }

            outcome.Success = true;
            outcome.Preset = preset;
            outcome.Warnings.AddRange(presetService.LastLoadWarnings);
            outcome.Warnings.AddRange(MetadataPresetService.Validate(preset).Warnings);

            JsonObject result = new JsonObject();
            result["valid"] = true;
            JsonArray warnings = new JsonArray();
            for (int i = 0; i < outcome.Warnings.Count; i++)
                warnings.Add(outcome.Warnings[i]);
            result["warnings"] = warnings;
            return result.ToJsonString();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Elenca le righe dello scope: un file per Container, una traccia per gli scope traccia
        /// </summary>
        /// <param name="scope">Scope</param>
        /// <returns>Righe</returns>
        private List<RowContext> GetRows(MkvMetadataTargetScope scope)
        {
            List<RowContext> result = new List<RowContext>();

            for (int i = 0; i < this._records.Count; i++)
            {
                MkvMetadataRecord record = this._records[i];
                if (record == null || record.FileInfo == null)
                    continue;

                if (scope == MkvMetadataTargetScope.Container)
                {
                    result.Add(new RowContext(i, record, null));
                    continue;
                }

                for (int t = 0; t < record.FileInfo.Tracks.Count; t++)
                {
                    if (MetadataScopeHelper.ScopeFromTrack(record.FileInfo.Tracks[t]) == scope)
                        result.Add(new RowContext(i, record, record.FileInfo.Tracks[t]));
                }
            }

            return result;
        }

        /// <summary>
        /// Costruisce i valori distinti di un campo con i conteggi, ordinati per frequenza
        /// </summary>
        /// <param name="rows">Righe</param>
        /// <param name="fieldKey">Campo</param>
        /// <param name="maxValues">Valori massimi</param>
        /// <returns>Array di valori con conteggio, più l'eventuale resto</returns>
        private JsonArray BuildDistinctValues(List<RowContext> rows, string fieldKey, int maxValues)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);
            List<string> order = new List<string>();
            JsonArray result = new JsonArray();

            for (int i = 0; i < rows.Count; i++)
            {
                string value = string.Equals(fieldKey, TAGS_FIELD, StringComparison.OrdinalIgnoreCase) ? string.Join(", ", GetTags(rows[i]).Keys) : this.ReadField(rows[i], fieldKey);
                if (string.IsNullOrEmpty(value))
                    continue;

                if (!counts.ContainsKey(value))
                {
                    counts[value] = 0;
                    order.Add(value);
                }

                counts[value]++;
            }

            order.Sort((left, right) => counts[right] != counts[left] ? counts[right].CompareTo(counts[left]) : string.CompareOrdinal(left, right));
            for (int i = 0; i < order.Count && i < maxValues; i++)
                result.Add(new JsonObject { ["value"] = order[i], ["count"] = counts[order[i]] });

            if (order.Count > maxValues)
                result.Add(new JsonObject { ["more_distinct_values"] = order.Count - maxValues });

            return result;
        }

        /// <summary>
        /// Legge un campo della riga con la stessa logica delle condizioni preset
        /// </summary>
        /// <param name="row">Riga</param>
        /// <param name="fieldKey">Campo, anche con prefisso original.</param>
        /// <returns>Valore</returns>
        private string ReadField(RowContext row, string fieldKey)
        {
            return this._evaluator.GetConditionFieldValue(row.Record, row.Track, fieldKey);
        }

        /// <summary>
        /// Restituisce i tag della riga: della traccia, oppure del contenitore con i livelli diversi da 50 come livello:NOME
        /// </summary>
        /// <param name="row">Riga</param>
        /// <returns>Tag ordinati per nome</returns>
        private static SortedDictionary<string, string> GetTags(RowContext row)
        {
            SortedDictionary<string, string> result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> source = row.Track != null ? row.Track.Tags : row.Record.FileInfo.Tags;

            if (source != null)
            {
                foreach (KeyValuePair<string, string> tag in source)
                    result[tag.Key] = tag.Value != null ? tag.Value : "";
            }

            if (row.Track == null && row.Record.FileInfo.LeveledTags != null)
            {
                foreach (KeyValuePair<string, string> tag in row.Record.FileInfo.LeveledTags)
                    result[tag.Key] = tag.Value != null ? tag.Value : "";
            }

            return result;
        }

        /// <summary>
        /// Valida una chiave campo richiesta dal modello per lo scope
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <param name="scope">Scope della query</param>
        /// <param name="allowTags">True se lo pseudo campo tags è ammesso</param>
        /// <returns>Errore o stringa vuota</returns>
        private static string ValidateQueryField(string fieldKey, MkvMetadataTargetScope scope, bool allowTags)
        {
            MetadataFieldDefinition field;
            string key = fieldKey;

            if (allowTags && string.Equals(key, TAGS_FIELD, StringComparison.OrdinalIgnoreCase))
                return "";

            if (key.StartsWith("original.", StringComparison.OrdinalIgnoreCase))
                key = key.Substring("original.".Length);

            if (!MetadataFieldRegistry.TryGet(key, out field) || !field.IsReadable || field.Visibility == MetadataFieldVisibility.Hidden)
                return "Unknown field '" + fieldKey + "'.";

            if (!MetadataScopeHelper.IsFieldReadableInScope(field, scope))
                return "Field '" + fieldKey + "' is not readable in scope '" + GetScopeName(scope) + "'.";

            return "";
        }

        /// <summary>
        /// Converte un filtro del modello nella condizione campo dei preset
        /// </summary>
        /// <param name="source">Filtro JSON</param>
        /// <param name="scope">Scope della query</param>
        /// <param name="condition">Condizione risultante</param>
        /// <returns>Errore o stringa vuota</returns>
        private static string TryParseCondition(JsonObject source, MkvMetadataTargetScope scope, out MkvMetadataFieldCondition condition)
        {
            MkvMetadataConditionOperator conditionOperator;
            condition = new MkvMetadataFieldCondition();

            if (source == null)
                return "condition is not an object.";

            condition.FieldKey = AiOAuthFlow.GetString(source, "FieldKey").Trim();
            string fieldError = ValidateQueryField(condition.FieldKey, scope, false);
            if (fieldError.Length > 0)
                return fieldError;

            if (!Enum.TryParse(AiOAuthFlow.GetString(source, "Operator"), false, out conditionOperator) || !Enum.IsDefined(typeof(MkvMetadataConditionOperator), conditionOperator))
                return "Unknown operator '" + AiOAuthFlow.GetString(source, "Operator") + "'.";

            condition.Operator = conditionOperator;
            condition.Value = AiOAuthFlow.GetString(source, "Value");
            condition.FromValue = AiOAuthFlow.GetString(source, "FromValue");
            condition.ToValue = AiOAuthFlow.GetString(source, "ToValue");
            condition.Unit = AiOAuthFlow.GetString(source, "Unit");

            JsonArray values = source["Values"] as JsonArray;
            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    if (values[i] != null)
                        condition.Values.Add(values[i].ToString());
                }
            }

            return "";
        }

        /// <summary>
        /// Indica se il campo appartiene allo scope nella panoramica: file e container nel Container, campi traccia nel loro tipo
        /// </summary>
        /// <param name="field">Campo</param>
        /// <param name="scope">Scope</param>
        /// <returns>True se appartiene</returns>
        private static bool IsFieldOwnedByScope(MetadataFieldDefinition field, MkvMetadataTargetScope scope)
        {
            if (scope == MkvMetadataTargetScope.Container)
                return field.Sector == MetadataFieldSector.File || field.Sector == MetadataFieldSector.Container;

            return MetadataScopeHelper.IsTrackFieldInScope(field, scope);
        }

        /// <summary>
        /// Converte il nome scope del tool nello scope dei preset
        /// </summary>
        /// <param name="value">Nome scope</param>
        /// <param name="scope">Scope risultante</param>
        /// <returns>True se riconosciuto</returns>
        private static bool TryParseScope(string value, out MkvMetadataTargetScope scope)
        {
            switch (value)
            {
                case "file":
                    scope = MkvMetadataTargetScope.Container;
                    return true;
                case "video":
                    scope = MkvMetadataTargetScope.Video;
                    return true;
                case "audio":
                    scope = MkvMetadataTargetScope.Audio;
                    return true;
                case "subtitle":
                    scope = MkvMetadataTargetScope.Subtitle;
                    return true;
                default:
                    scope = MkvMetadataTargetScope.Container;
                    return false;
            }
        }

        /// <summary>
        /// Restituisce il nome scope usato dai tool
        /// </summary>
        /// <param name="scope">Scope</param>
        /// <returns>Nome scope</returns>
        private static string GetScopeName(MkvMetadataTargetScope scope)
        {
            switch (scope)
            {
                case MkvMetadataTargetScope.Video:
                    return "video";
                case MkvMetadataTargetScope.Audio:
                    return "audio";
                case MkvMetadataTargetScope.Subtitle:
                    return "subtitle";
                default:
                    return "file";
            }
        }

        /// <summary>
        /// Legge un intero dagli argomenti con valore predefinito
        /// </summary>
        /// <param name="source">Argomenti</param>
        /// <param name="name">Nome proprietà</param>
        /// <param name="defaultValue">Valore predefinito</param>
        /// <returns>Valore</returns>
        private static int GetInt(JsonObject source, string name, int defaultValue)
        {
            JsonValue value = source[name] as JsonValue;
            int result;
            if (value != null && value.TryGetValue<int>(out result))
                return result;

            return defaultValue;
        }

        /// <summary>
        /// Crea l'output di errore per il modello
        /// </summary>
        /// <param name="errors">Errori</param>
        /// <returns>JSON di errore</returns>
        private static string CreateErrorOutput(List<string> errors)
        {
            JsonArray list = new JsonArray();
            for (int i = 0; i < errors.Count; i++)
                list.Add(errors[i]);

            return new JsonObject { ["valid"] = false, ["errors"] = list }.ToJsonString();
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Riga interrogabile: file con traccia opzionale
        /// </summary>
        private sealed class RowContext
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="fileIndex">Indice del file nell'elenco caricato</param>
            /// <param name="record">Record del file</param>
            /// <param name="track">Traccia, null per le righe file</param>
            public RowContext(int fileIndex, MkvMetadataRecord record, MkvMetadataTrackInfo track)
            {
                this.FileIndex = fileIndex;
                this.Record = record;
                this.Track = track;
            }

            /// <summary>
            /// Indice del file nell'elenco caricato
            /// </summary>
            public int FileIndex { get; }

            /// <summary>
            /// Record del file
            /// </summary>
            public MkvMetadataRecord Record { get; }

            /// <summary>
            /// Traccia, null per le righe file
            /// </summary>
            public MkvMetadataTrackInfo Track { get; }
        }

        #endregion
    }
}
