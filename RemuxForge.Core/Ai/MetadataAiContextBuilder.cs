using RemuxForge.Core.Metadata;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace RemuxForge.Core.Ai
{
    /// <summary>
    /// Costruisce le istruzioni del modello per la generazione preset Metadata a partire dai registri del codice
    /// </summary>
    public static class MetadataAiContextBuilder
    {
        #region Costanti

        /// <summary>
        /// Esempi di regole tratti dal preset di test end-to-end
        /// </summary>
        private const string EXAMPLE_RULES = @"[
  {
    ""Description"": ""Rebuild container title from file name"",
    ""Enabled"": true,
    ""TargetScope"": ""Container"",
    ""When"": { ""All"": [
      { ""NodeType"": ""Field"", ""Field"": { ""FieldKey"": ""container_title"", ""Operator"": ""IsEmpty"" } },
      { ""NodeType"": ""AlternativeAny"", ""Alternative"": { ""Any"": [
        { ""NodeType"": ""Field"", ""Field"": { ""FieldKey"": ""file_extension"", ""Operator"": ""Equals"", ""Value"": "".mkv"" } },
        { ""NodeType"": ""Field"", ""Field"": { ""FieldKey"": ""file_name"", ""Operator"": ""EndsWith"", ""Value"": "".mkv"" } }
      ] } }
    ] },
    ""Operations"": [
      { ""Type"": ""SetField"", ""FieldKey"": ""container_title"", ""Value"": ""{[file_name]:TrimEnd(4):Replace(\""_\"", \""-\""):NormalizeSpaces()}"" }
    ]
  },
  {
    ""Description"": ""Largest Italian audio becomes the only default"",
    ""Enabled"": true,
    ""TargetScope"": ""Audio"",
    ""When"": { ""All"": [
      { ""NodeType"": ""Field"", ""Field"": { ""FieldKey"": ""audio_language"", ""Operator"": ""Equals"", ""Value"": ""ita"" } },
      { ""NodeType"": ""TrackComparison"", ""TrackComparison"": { ""FieldKey"": ""audio_stream_size"", ""Relation"": ""Largest"", ""Group"": ""AllInScope"", ""Rank"": 1 } },
      { ""NodeType"": ""TrackGroupCount"", ""TrackGroupCount"": { ""Group"": ""AllInScope"", ""Operator"": ""GreaterOrEqual"", ""Value"": 2 } }
    ] },
    ""Operations"": [
      { ""Type"": ""SetExclusiveFlag"", ""FieldKey"": ""audio_default"", ""ExclusiveGroup"": ""AllInScope"" }
    ]
  },
  {
    ""Description"": ""Name the largest PGS subtitle per language as Full"",
    ""Enabled"": true,
    ""TargetScope"": ""Subtitle"",
    ""When"": { ""All"": [
      { ""NodeType"": ""Field"", ""Field"": { ""FieldKey"": ""subtitle_format"", ""Operator"": ""Equals"", ""Value"": ""PGS"" } },
      { ""NodeType"": ""Field"", ""Field"": { ""FieldKey"": ""subtitle_stream_size"", ""Operator"": ""GreaterThan"", ""Value"": ""1"", ""Unit"": ""MB"" } },
      { ""NodeType"": ""TrackComparison"", ""TrackComparison"": { ""FieldKey"": ""subtitle_stream_size"", ""Relation"": ""Largest"", ""Group"": ""SameLanguageAndFormat"", ""Rank"": 1 } }
    ] },
    ""Operations"": [
      { ""Type"": ""SetField"", ""FieldKey"": ""subtitle_title"", ""Value"": ""{[subtitle_language]:ToUpper()} - Full"" },
      { ""Type"": ""SetTagField"", ""TagKey"": ""LANGUAGE"", ""TagTarget"": ""CurrentTrack"", ""Value"": ""[subtitle_language]"" }
    ]
  },
  {
    ""Description"": ""Remove the second full English subtitle"",
    ""Enabled"": true,
    ""TargetScope"": ""Subtitle"",
    ""When"": { ""All"": [
      { ""NodeType"": ""Field"", ""Field"": { ""FieldKey"": ""subtitle_language"", ""Operator"": ""Equals"", ""Value"": ""eng"" } },
      { ""NodeType"": ""Field"", ""Field"": { ""FieldKey"": ""original.subtitle_title"", ""Operator"": ""Equals"", ""Value"": ""Full"" } },
      { ""NodeType"": ""TrackComparison"", ""TrackComparison"": { ""FieldKey"": ""subtitle_stream_size"", ""Relation"": ""Rank"", ""Group"": ""SameLanguageAndFormat"", ""Rank"": 2 } }
    ] },
    ""Operations"": [ { ""Type"": ""RemoveTrack"" } ]
  }
]";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce le istruzioni di sistema del modello
        /// </summary>
        /// <returns>Istruzioni in inglese</returns>
        public static string BuildInstructions()
        {
            StringBuilder text = new StringBuilder();

            AppendRole(text);
            AppendSchema(text);
            AppendSemantics(text);
            AppendOperators(text);
            AppendTemplates(text);
            AppendFields(text);
            AppendTags(text);
            text.AppendLine("## Examples of valid rules");
            text.AppendLine(EXAMPLE_RULES);
            return text.ToString();
        }

        /// <summary>
        /// Costruisce il messaggio utente iniziale con richiesta e panoramica dei file
        /// </summary>
        /// <param name="userRequest">Richiesta dell'utente</param>
        /// <param name="overviewJson">Panoramica dei file caricati</param>
        /// <returns>Messaggio utente</returns>
        public static string BuildUserMessage(string userRequest, string overviewJson)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("User request:");
            text.AppendLine(userRequest != null ? userRequest.Trim() : "");
            text.AppendLine();
            text.AppendLine("Overview of the loaded files (distinct values of the primary fields, most frequent first):");
            text.AppendLine(overviewJson);
            return text.ToString();
        }

        /// <summary>
        /// Messaggio inviato quando il modello risponde senza chiamare submit_preset
        /// </summary>
        /// <returns>Messaggio utente</returns>
        public static string BuildMissingSubmissionMessage()
        {
            return "This is not a chat: nobody will answer questions. Make reasonable choices, then call submit_preset with the complete preset JSON.";
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Aggiunge ruolo, procedura e regole di output
        /// </summary>
        /// <param name="text">Testo in costruzione</param>
        private static void AppendRole(StringBuilder text)
        {
            text.AppendLine("You create Metadata presets for RemuxForge, a tool that edits Matroska (MKV) metadata with mkvpropedit and mkvmerge.");
            text.AppendLine("The user describes in natural language the rules to apply to the loaded files. You receive an overview of those files and you must return a preset that implements the request.");
            text.AppendLine();
            text.AppendLine("Procedure:");
            text.AppendLine("1. Read the request and the overview. Use remuxforge.query_files to check real values (formats, titles, languages, sizes, tags) before writing conditions: conditions must match the values as they appear in the files.");
            text.AppendLine("2. Build the preset and call remuxforge.submit_preset with the complete JSON.");
            text.AppendLine("3. If submit_preset returns errors, fix them and submit the whole preset again. When it returns valid:true you are done: stop calling tools.");
            text.AppendLine();
            text.AppendLine("Rules:");
            text.AppendLine("- This is not a chat: never ask questions, never answer with plain text instead of submitting. When the request is ambiguous choose the most reasonable interpretation and state it in the rule Description.");
            text.AppendLine("- Write the preset Description and every rule Description in the same language as the user request. Rule Descriptions must be unique and say what the rule does.");
            text.AppendLine("- Use only the field keys, tag names, enum values and functions listed below. Do not invent properties.");
            text.AppendLine("- Prefer few, clear rules. Do not add changes the user did not ask for.");
            text.AppendLine("- Avoid destructive operations (RemoveTrack, ClearTags, DeleteAttachment) unless the user explicitly asks for them.");
            text.AppendLine();
        }

        /// <summary>
        /// Aggiunge lo schema JSON del preset con i valori enum
        /// </summary>
        /// <param name="text">Testo in costruzione</param>
        private static void AppendSchema(StringBuilder text)
        {
            text.AppendLine("## Preset JSON schema");
            text.AppendLine("Property names are PascalCase, enum values are strings. Omitted properties take their default. SchemaVersion is always 4.");
            text.AppendLine("Preset: { \"SchemaVersion\": 4, \"Name\": string, \"Description\": string, \"Rules\": [Rule] }");
            text.AppendLine("Rule: { \"Description\": string (required), \"Enabled\": true, \"TargetScope\": TargetScope, \"When\": { \"All\": [ConditionNode] }, \"Operations\": [Operation] }");
            text.AppendLine("ConditionNode, by NodeType:");
            text.AppendLine("- { \"NodeType\": \"Field\", \"Field\": { \"FieldKey\", \"Operator\", \"Value\", \"Values\": [string], \"FromValue\", \"ToValue\", \"Unit\" } }");
            text.AppendLine("- { \"NodeType\": \"TrackComparison\", \"TrackComparison\": { \"FieldKey\", \"Relation\": TrackComparisonRelation, \"Group\": TrackGroup, \"Rank\": int } }");
            text.AppendLine("- { \"NodeType\": \"TrackGroupCount\", \"TrackGroupCount\": { \"Group\": TrackGroup, \"Operator\": numeric operator, \"Value\": int } }");
            text.AppendLine("- { \"NodeType\": \"AlternativeAny\", \"Alternative\": { \"Any\": [ConditionNode] } }");
            text.AppendLine("Operation: { \"Type\": OperationType, \"FieldKey\", \"Value\", \"TagKey\", \"TagTarget\": TagTarget, \"TagTargetTypeValue\": int, \"ClearTagsConfirmed\": bool, \"ExclusiveGroup\": ExclusiveGroup, \"AttachmentSourcePath\", \"AttachmentName\", \"AttachmentMimeType\" }");
            text.AppendLine();
            text.AppendLine("Enums:");
            text.AppendLine("- TargetScope: " + JoinEnum(typeof(MkvMetadataTargetScope)));
            text.AppendLine("- NodeType: " + JoinEnum(typeof(MkvMetadataRuleConditionNodeType)));
            text.AppendLine("- Operator: " + JoinEnum(typeof(MkvMetadataConditionOperator)));
            text.AppendLine("- TrackComparisonRelation: " + JoinEnum(typeof(MkvMetadataTrackComparisonRelation)));
            text.AppendLine("- TrackGroup: " + JoinEnum(typeof(MkvMetadataTrackGroup)));
            text.AppendLine("- OperationType: " + JoinEnumExcept(typeof(MkvMetadataOperationType), MkvMetadataOperationType.EditChapters.ToString()));
            text.AppendLine("- TagTarget: " + JoinEnum(typeof(MkvMetadataTagTarget)));
            text.AppendLine("- ExclusiveGroup: " + JoinEnum(typeof(MkvMetadataExclusiveGroup)));
            text.AppendLine();
        }

        /// <summary>
        /// Aggiunge la semantica di valutazione di regole, condizioni e operazioni
        /// </summary>
        /// <param name="text">Testo in costruzione</param>
        private static void AppendSemantics(StringBuilder text)
        {
            text.AppendLine("## Semantics");
            text.AppendLine("- Rules run in order on every file. Each rule sees the changes made by the previous rules (use the prefix original. in a FieldKey or token to read the value before any change, e.g. original.audio_title).");
            text.AppendLine("- TargetScope Container evaluates the rule once per file. Video, Audio and Subtitle evaluate it once for each track of that type; the operations act on the current track.");
            text.AppendLine("- When.All: every node must match (AND). An empty list always matches. AlternativeAny matches when at least one inner node matches (OR) and can be nested.");
            text.AppendLine("- Field conditions on a track rule can read the track fields of that scope plus all file and container fields. Container rules read only file and container fields.");
            text.AppendLine("- Language fields are compared after normalization to ISO 639-2 (ita, it and Italian match the same value).");
            text.AppendLine("- Value, Values, FromValue and ToValue accept templates. Unit applies to the condition value for sizes, durations and similar fields (e.g. Value \"1\" with Unit \"MB\").");
            text.AppendLine("- TrackComparison (track rules only) compares the current track with a group of tracks of the same scope: Largest/Smallest = maximum/minimum of the group, Rank = position when the group is sorted from the largest (Rank 1 = largest), GreaterThanAll etc. compare with every other track, EqualsAny/NotEqualsAll for text fields. Group SameLanguage, SameFormat, SameLanguageAndFormat or AllInScope.");
            text.AppendLine("- TrackGroupCount (track rules only) compares the number of tracks in the group of the current track with Value.");
            text.AppendLine();
            text.AppendLine("Operations:");
            text.AppendLine("- SetField: FieldKey (editable field of the rule scope) and Value (literal or template). Boolean fields take 1 or 0 (true/false are accepted). Setting video_index/audio_index/subtitle_index moves the track to that 1-based position within its type (requires remux).");
            text.AppendLine("- ClearField: FieldKey of a clearable field.");
            text.AppendLine("- SetExclusiveFlag: FieldKey of a boolean field (e.g. audio_default, subtitle_forced) and ExclusiveGroup: sets it true on the current track and false on the other tracks of the group.");
            text.AppendLine("- RemoveTrack: track rules only, removes the current track (requires remux).");
            text.AppendLine("- AddOrUpdateTrackStatisticsTags / DeleteTrackStatisticsTags: manage the mkvmerge statistics tags of all tracks, no other property.");
            text.AppendLine("- SetTagField / ClearTagField: TagKey (tag name), TagTarget, Value for SetTagField, TagTargetTypeValue = Matroska target level: 70 collection/series, 60 season, 50 episode/movie (default), 30 track. TagTarget Current = the element of the rule, File = the container, CurrentTrack = the current track, AllTracks = every track.");
            text.AppendLine("- ClearTags: deletes all managed tags of the TagTarget element(s), requires ClearTagsConfirmed true.");
            text.AppendLine("- SetAttachment (AttachmentSourcePath, optional AttachmentName and AttachmentMimeType) and DeleteAttachment (AttachmentName): Container rules only.");
            text.AppendLine("- Chapters cannot be edited by presets.");
            text.AppendLine();
        }

        /// <summary>
        /// Aggiunge operatori, relazioni e unità per tipo di valore
        /// </summary>
        /// <param name="text">Testo in costruzione</param>
        private static void AppendOperators(StringBuilder text)
        {
            Dictionary<MetadataFieldValueType, string> sampleFields = new Dictionary<MetadataFieldValueType, string>();
            Dictionary<MetadataFieldInputKind, string> unitFields = new Dictionary<MetadataFieldInputKind, string>();
            List<MetadataFieldDefinition> fields = MetadataFieldRegistry.GetAll();

            for (int i = 0; i < fields.Count; i++)
            {
                if (!sampleFields.ContainsKey(fields[i].ValueType))
                    sampleFields[fields[i].ValueType] = fields[i].Key;
                if (!unitFields.ContainsKey(fields[i].InputKind))
                    unitFields[fields[i].InputKind] = fields[i].Key;
            }

            text.AppendLine("## Operators by value type");
            foreach (KeyValuePair<MetadataFieldValueType, string> sample in sampleFields)
            {
                List<MetadataConditionOperatorItem> operators = MetadataUiCatalog.GetConditionOperatorCatalog(sample.Value);
                List<string> names = new List<string>();
                for (int i = 0; i < operators.Count; i++)
                    names.Add(operators[i].Operator.ToString());

                List<MkvMetadataTrackComparisonRelation> relations = MetadataUiCatalog.GetTrackComparisonRelations(sample.Value);
                List<string> relationNames = new List<string>();
                for (int i = 0; i < relations.Count; i++)
                    relationNames.Add(relations[i].ToString());

                text.Append("- ").Append(sample.Key).Append(": operators ").Append(string.Join(", ", names));
                if (sample.Key != MetadataFieldValueType.Boolean)
                    text.Append("; track comparison relations ").Append(string.Join(", ", relationNames));
                text.AppendLine();
            }

            foreach (KeyValuePair<MetadataFieldInputKind, string> sample in unitFields)
            {
                MetadataInputSchema schema = MetadataUiCatalog.GetFieldInputSchema(sample.Value, MetadataCatalogInputUsage.ConditionValue);
                List<string> units = new List<string>();
                for (int i = 0; i < schema.UnitOptions.Count; i++)
                {
                    if (!string.IsNullOrEmpty(schema.UnitOptions[i].Value))
                        units.Add(schema.UnitOptions[i].Value);
                }

                if (units.Count == 0)
                    continue;

                text.Append("- Units for ").Append(sample.Key).Append(" fields: ").AppendLine(string.Join(", ", units));
            }

            text.AppendLine();
        }

        /// <summary>
        /// Aggiunge la sintassi dei template e il catalogo funzioni
        /// </summary>
        /// <param name="text">Testo in costruzione</param>
        private static void AppendTemplates(StringBuilder text)
        {
            List<MetadataCatalogFunctionItem> functions = MetadataUiCatalog.GetFunctionCatalog();

            text.AppendLine("## Templates");
            text.AppendLine("- [field_key] inserts the value of a field (e.g. \"Audio [audio_language]\"); [original.field_key] reads the value before any change. Square brackets around unknown names stay literal.");
            text.AppendLine("- {expression:Function(args):Function(args)} applies a pipeline of functions to the expression, e.g. {[file_name]:TrimEnd(4)}. Arguments are separated by commas, quote them when they contain commas or colons.");
            text.AppendLine("- An expression cannot contain the characters { or }: a regex quantifier such as \\d{2} breaks the template. Write \\d\\d or use + instead.");
            text.AppendLine("- Double quotes only group an argument and are removed; each argument is trimmed, so leading and trailing spaces inside an argument are lost. Put separators such as \" - \" in the literal text outside the expression instead.");
            text.AppendLine("Functions (call: example -> result):");
            for (int i = 0; i < functions.Count; i++)
            {
                text.Append("- ").Append(functions[i].Call).Append(": ").Append(functions[i].ExampleExpression)
                    .Append(" with \"").Append(functions[i].ExampleInput).Append("\" -> \"").Append(functions[i].ExampleOutput).AppendLine("\"");
            }

            text.AppendLine();
        }

        /// <summary>
        /// Aggiunge il catalogo dei campi
        /// </summary>
        /// <param name="text">Testo in costruzione</param>
        private static void AppendFields(StringBuilder text)
        {
            List<MetadataFieldDefinition> fields = MetadataFieldRegistry.GetAll();

            text.AppendLine("## Fields");
            text.AppendLine("Format: key | value type | sector | flags | unit | allowed values. Flags: editable (usable in SetField), clearable (usable in ClearField), advanced, remux (the change requires a remux). Fields without editable are read-only and usable only in conditions and templates.");
            for (int i = 0; i < fields.Count; i++)
            {
                MetadataFieldDefinition field = fields[i];
                List<string> flags = new List<string>();
                List<string> allowed = new List<string>();

                if (!field.IsReadable || field.Visibility == MetadataFieldVisibility.Hidden)
                    continue;

                if (field.IsEditable && field.EditPolicy != MetadataFieldEditPolicy.Blocked)
                    flags.Add("editable");
                if (field.IsClearable)
                    flags.Add("clearable");
                if (field.EditPolicy == MetadataFieldEditPolicy.Advanced || field.Visibility == MetadataFieldVisibility.Advanced)
                    flags.Add("advanced");
                if (field.RequiresRemux)
                    flags.Add("remux");

                for (int a = 0; a < field.AllowedValues.Count; a++)
                    allowed.Add(field.AllowedValues[a].Value + "=" + field.AllowedValues[a].Label);

                text.Append(field.Key).Append(" | ").Append(field.ValueType).Append(" | ").Append(field.Sector)
                    .Append(" | ").Append(string.Join(",", flags)).Append(" | ").Append(field.Unit)
                    .Append(" | ").AppendLine(string.Join("; ", allowed));
            }

            text.AppendLine();
        }

        /// <summary>
        /// Aggiunge il catalogo dei tag modificabili
        /// </summary>
        /// <param name="text">Testo in costruzione</param>
        private static void AppendTags(StringBuilder text)
        {
            List<string> names = MetadataTagRegistry.GetEditableTagNames();

            text.AppendLine("## Tags (TagKey)");
            text.AppendLine("Format: name | value type | scopes | default TagTargetTypeValue.");
            for (int i = 0; i < names.Count; i++)
            {
                MetadataTagDefinition tag;
                if (!MetadataTagRegistry.TryGet(names[i], out tag))
                    continue;

                List<string> scopes = new List<string>();
                for (int s = 0; s < tag.TargetScopes.Count; s++)
                    scopes.Add(tag.TargetScopes[s].ToString());

                text.Append(tag.Name).Append(" | ").Append(tag.ValueType).Append(" | ").Append(string.Join(",", scopes))
                    .Append(" | ").AppendLine(tag.DefaultTargetTypeValue > 0 ? tag.DefaultTargetTypeValue.ToString() : MetadataTagTargetLevels.EPISODE.ToString());
            }

            text.AppendLine();
        }

        /// <summary>
        /// Elenca i valori di un enum separati da virgola
        /// </summary>
        /// <param name="enumType">Tipo enum</param>
        /// <returns>Valori</returns>
        private static string JoinEnum(Type enumType)
        {
            return string.Join(", ", Enum.GetNames(enumType));
        }

        /// <summary>
        /// Elenca i valori di un enum escludendone uno
        /// </summary>
        /// <param name="enumType">Tipo enum</param>
        /// <param name="excluded">Valore escluso</param>
        /// <returns>Valori</returns>
        private static string JoinEnumExcept(Type enumType, string excluded)
        {
            List<string> names = new List<string>(Enum.GetNames(enumType));
            names.Remove(excluded);
            return string.Join(", ", names);
        }

        #endregion
    }
}
