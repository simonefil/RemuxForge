using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Servizio preset JSON regole per modalità Metadata
    /// </summary>
    public class MetadataPresetService
    {
        #region Costanti

        /// <summary>
        /// Versione schema preset metadata supportata
        /// </summary>
        private const int CURRENT_SCHEMA_VERSION = 4;

        /// <summary>
        /// Operazioni tolte dai preset: i capitoli si editano solo a mano, l'ordine delle
        /// tracce si imposta con la posizione esplicita nelle regole di traccia
        /// </summary>
        private static readonly string[] REMOVED_OPERATION_TYPES = new string[] { "RenameChapters", "ClearChapters", "SetTrackOrder" };

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Cartella fissa dei preset metadata
        /// </summary>
        private readonly string _presetFolder;

        /// <summary>
        /// Avvisi dell'ultimo caricamento: operazioni non piu' supportate scartate dal preset
        /// </summary>
        private List<string> _lastLoadWarnings = new List<string>();

        #endregion

        #region Proprieta

        /// <summary>
        /// Avvisi dell'ultimo caricamento, vuota se il preset e' stato letto intero
        /// </summary>
        public List<string> LastLoadWarnings
        {
            get { return this._lastLoadWarnings; }
        }

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="configFolder">Cartella configurazione RemuxForge</param>
        public MetadataPresetService(string configFolder)
        {
            string root = configFolder != null ? configFolder : "";
            this._presetFolder = Path.Combine(root, "presets", "metadata");
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce cartella preset metadata
        /// </summary>
        /// <returns>Cartella preset</returns>
        public string GetPresetFolder()
        {
            return this._presetFolder;
        }

        /// <summary>
        /// Elenca preset disponibili
        /// </summary>
        /// <returns>Lista percorsi preset</returns>
        public List<string> ListPresetFiles()
        {
            List<string> result = new List<string>();

            if (!Directory.Exists(this._presetFolder))
                return result;

            string[] files = Directory.GetFiles(this._presetFolder, "*.json", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                result.Add(files[i]);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// Carica un preset da file JSON
        /// </summary>
        /// <param name="filePath">Percorso preset</param>
        /// <returns>Preset</returns>
        public MkvMetadataPreset Load(string filePath)
        {
            if (string.IsNullOrEmpty(filePath != null ? filePath.Trim() : null))
                throw new ArgumentException(AppText.T("metadata.preset.emptyPath"), nameof(filePath));

            return this.LoadFromJson(File.ReadAllText(filePath));
        }

        /// <summary>
        /// Carica un preset dal contenuto JSON
        /// </summary>
        /// <param name="json">Contenuto JSON del preset</param>
        /// <returns>Preset</returns>
        public MkvMetadataPreset LoadFromJson(string json)
        {
            MkvMetadataPreset preset;
            JsonSerializerOptions options;
            JsonNode root;

            this._lastLoadWarnings = new List<string>();
            if (string.IsNullOrEmpty(json != null ? json.Trim() : null))
                throw new ArgumentException(AppText.T("metadata.preset.invalid"), nameof(json));

            options = CreateSerializerOptions();
            root = JsonNode.Parse(json);
            if (root == null)
                throw new InvalidOperationException(AppText.T("metadata.preset.invalid"));

            // Le operazioni tolte vanno scartate prima di deserializzare: il convertitore
            // degli enum rifiuterebbe il loro Type e con esso l'intero preset
            RemoveLegacyOperations(root, this._lastLoadWarnings);
            preset = root.Deserialize<MkvMetadataPreset>(options);
            if (preset == null)
                throw new InvalidOperationException(AppText.T("metadata.preset.invalid"));

            NormalizePreset(preset);
            MkvMetadataPresetValidationResult validation = Validate(preset);
            if (!validation.IsValid)
                throw new InvalidOperationException(validation.ErrorMessage);

            return preset;
        }

        /// <summary>
        /// Salva un preset JSON su file
        /// </summary>
        /// <param name="preset">Preset da salvare</param>
        /// <param name="filePath">Percorso output</param>
        public void Save(MkvMetadataPreset preset, string filePath)
        {
            string json = this.SerializeToJson(preset);
            string folder = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// Serializza un preset nel contenuto JSON
        /// </summary>
        /// <param name="preset">Preset da serializzare</param>
        /// <returns>Contenuto JSON</returns>
        public string SerializeToJson(MkvMetadataPreset preset)
        {
            MkvMetadataPresetValidationResult validation;
            JsonSerializerOptions options;

            if (preset == null)
                throw new ArgumentNullException(nameof(preset));

            NormalizePreset(preset);
            validation = Validate(preset);
            if (!validation.IsValid)
                throw new InvalidOperationException(validation.ErrorMessage);

            options = CreateSerializerOptions();
            return JsonSerializer.Serialize(preset, options);
        }

        /// <summary>
        /// Valida un preset metadata
        /// </summary>
        /// <param name="preset">Preset</param>
        /// <returns>Risultato validazione</returns>
        public static MkvMetadataPresetValidationResult Validate(MkvMetadataPreset preset)
        {
            MkvMetadataPresetValidationResult result = new MkvMetadataPresetValidationResult();

            if (preset == null)
            {
                result.AddError(AppText.T("metadata.preset.nullPreset"));
                return result;
            }

            if (preset.SchemaVersion != CURRENT_SCHEMA_VERSION)
                result.AddError(AppText.F("metadata.preset.unsupportedSchema", preset.SchemaVersion));

            if (preset.Rules == null)
            {
                result.AddError(AppText.T("metadata.preset.missingRuleList"));
                return result;
            }

            ValidateRules(preset.Rules, result);
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Scarta dal JSON le operazioni non piu' supportate e annota regola per regola cosa e' stato tolto
        /// </summary>
        /// <param name="root">Radice JSON del preset</param>
        /// <param name="warnings">Avvisi da popolare</param>
        private static void RemoveLegacyOperations(JsonNode root, List<string> warnings)
        {
            JsonObject presetObject = root as JsonObject;
            JsonArray rules = presetObject != null ? GetPropertyIgnoreCase(presetObject, "Rules") as JsonArray : null;
            if (rules == null)
                return;

            for (int ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
            {
                JsonObject rule = rules[ruleIndex] as JsonObject;
                JsonArray operations = rule != null ? GetPropertyIgnoreCase(rule, "Operations") as JsonArray : null;
                if (operations == null)
                    continue;

                JsonValue descriptionValue = GetPropertyIgnoreCase(rule, "Description") as JsonValue;
                string description;
                if (descriptionValue == null || !descriptionValue.TryGetValue<string>(out description))
                    description = "";

                int operationIndex = 0;
                while (operationIndex < operations.Count)
                {
                    JsonObject operation = operations[operationIndex] as JsonObject;
                    JsonValue typeValue = operation != null ? GetPropertyIgnoreCase(operation, "Type") as JsonValue : null;
                    string type;
                    int removedIndex = -1;
                    if (typeValue != null && typeValue.TryGetValue<string>(out type))
                    {
                        for (int i = 0; i < REMOVED_OPERATION_TYPES.Length && removedIndex < 0; i++)
                        {
                            if (string.Equals(REMOVED_OPERATION_TYPES[i], type, StringComparison.OrdinalIgnoreCase))
                                removedIndex = i;
                        }
                    }

                    if (removedIndex < 0)
                    {
                        operationIndex++;
                        continue;
                    }

                    warnings.Add(AppText.F("metadata.preset.legacyOperationRemoved", ruleIndex + 1, description, REMOVED_OPERATION_TYPES[removedIndex]));
                    operations.RemoveAt(operationIndex);
                }
            }
        }

        /// <summary>
        /// Legge una proprieta' JSON ignorando maiuscole e minuscole, come fa il deserializzatore
        /// </summary>
        /// <param name="source">Oggetto JSON</param>
        /// <param name="name">Nome proprieta'</param>
        /// <returns>Valore trovato o null</returns>
        private static JsonNode GetPropertyIgnoreCase(JsonObject source, string name)
        {
            foreach (KeyValuePair<string, JsonNode> property in source)
            {
                if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
            }

            return null;
        }

        /// <summary>
        /// Crea le opzioni JSON usate dai preset metadata
        /// </summary>
        /// <returns>Opzioni serializzatore JSON</returns>
        private static JsonSerializerOptions CreateSerializerOptions()
        {
            JsonSerializerOptions options = new JsonSerializerOptions();
            options.WriteIndented = true;
            options.PropertyNameCaseInsensitive = true;
            options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.Converters.Add(new JsonStringEnumConverter(null, false));
            return options;
        }

        /// <summary>
        /// Normalizza un preset deserializzato o generato dalla UI
        /// </summary>
        /// <param name="preset">Preset da normalizzare</param>
        private static void NormalizePreset(MkvMetadataPreset preset)
        {
            if (string.IsNullOrEmpty(preset.Name))
                preset.Name = "";

            if (string.IsNullOrEmpty(preset.Description))
                preset.Description = "";

            if (MetadataUiCatalog.PresetUsesAdvancedFields(preset))
                preset.ShowAdvancedFields = true;

            if (preset.Rules == null)
                preset.Rules = new List<MkvMetadataRule>();

            for (int i = 0; i < preset.Rules.Count; i++)
            {
                NormalizeRule(preset.Rules[i]);
            }
        }

        /// <summary>
        /// Normalizza una regola del preset
        /// </summary>
        /// <param name="rule">Regola da normalizzare</param>
        private static void NormalizeRule(MkvMetadataRule rule)
        {
            if (rule == null)
                return;

            if (string.IsNullOrEmpty(rule.Description))
                rule.Description = "";

            if (rule.When == null)
                rule.When = new MkvMetadataRuleWhen();

            if (rule.When.All == null)
                rule.When.All = new List<MkvMetadataRuleConditionNode>();

            NormalizeConditionNodes(rule.When.All);

            if (rule.Operations == null)
                rule.Operations = new List<MkvMetadataOperation>();

            for (int i = 0; i < rule.Operations.Count; i++)
            {
                NormalizeOperation(rule.Operations[i]);
            }
        }

        /// <summary>
        /// Normalizza ricorsivamente i nodi condizione della regola
        /// </summary>
        /// <param name="nodes">Nodi condizione da normalizzare</param>
        private static void NormalizeConditionNodes(List<MkvMetadataRuleConditionNode> nodes)
        {
            if (nodes == null)
                return;

            for (int i = 0; i < nodes.Count; i++)
            {
                MkvMetadataRuleConditionNode node = nodes[i];
                if (node == null)
                    continue;

                if (node.NodeType == MkvMetadataRuleConditionNodeType.Field)
                {
                    if (node.Field == null)
                        node.Field = new MkvMetadataFieldCondition();

                    if (string.IsNullOrEmpty(node.Field.FieldKey))
                        node.Field.FieldKey = "";

                    if (string.IsNullOrEmpty(node.Field.Value))
                        node.Field.Value = "";

                    if (node.Field.Values == null)
                        node.Field.Values = new List<string>();

                    if (string.IsNullOrEmpty(node.Field.FromValue))
                        node.Field.FromValue = "";

                    if (string.IsNullOrEmpty(node.Field.ToValue))
                        node.Field.ToValue = "";

                    if (string.IsNullOrEmpty(node.Field.Unit))
                        node.Field.Unit = "";

                    node.TrackComparison = null;
                    node.TrackGroupCount = null;
                    node.Alternative = null;
                }
                else if (node.NodeType == MkvMetadataRuleConditionNodeType.TrackComparison)
                {
                    if (node.TrackComparison == null)
                        node.TrackComparison = new MkvMetadataTrackComparisonCondition();

                    if (string.IsNullOrEmpty(node.TrackComparison.FieldKey))
                        node.TrackComparison.FieldKey = "";

                    node.Field = null;
                    node.TrackGroupCount = null;
                    node.Alternative = null;
                }
                else if (node.NodeType == MkvMetadataRuleConditionNodeType.TrackGroupCount)
                {
                    if (node.TrackGroupCount == null)
                        node.TrackGroupCount = new MkvMetadataTrackGroupCountCondition();

                    node.Field = null;
                    node.TrackComparison = null;
                    node.Alternative = null;
                }
                else if (node.NodeType == MkvMetadataRuleConditionNodeType.AlternativeAny)
                {
                    if (node.Alternative == null)
                        node.Alternative = new MkvMetadataAlternativeAnyBlock();

                    if (node.Alternative.Any == null)
                        node.Alternative.Any = new List<MkvMetadataRuleConditionNode>();

                    node.Field = null;
                    node.TrackComparison = null;
                    node.TrackGroupCount = null;
                    NormalizeConditionNodes(node.Alternative.Any);
                }
                else
                {
                    node.Field = null;
                    node.TrackComparison = null;
                    node.TrackGroupCount = null;
                    node.Alternative = null;
                }
            }
        }

        /// <summary>
        /// Normalizza una operazione della regola
        /// </summary>
        /// <param name="operation">Operazione da normalizzare</param>
        private static void NormalizeOperation(MkvMetadataOperation operation)
        {
            if (operation == null)
                return;

            if (string.IsNullOrEmpty(operation.FieldKey))
                operation.FieldKey = "";

            if (string.IsNullOrEmpty(operation.Value))
                operation.Value = "";

            if (string.IsNullOrEmpty(operation.TagKey))
                operation.TagKey = "";
        }

        /// <summary>
        /// Valida tutte le regole del preset
        /// </summary>
        /// <param name="rules">Regole da validare</param>
        /// <param name="result">Risultato validazione da popolare</param>
        private static void ValidateRules(List<MkvMetadataRule> rules, MkvMetadataPresetValidationResult result)
        {
            Dictionary<string, int> descriptions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < rules.Count; i++)
            {
                ValidateRule(rules[i], i, result);
                if (rules[i] != null && !string.IsNullOrEmpty(rules[i].Description != null ? rules[i].Description.Trim() : null))
                {
                    string key = rules[i].Description.Trim();
                    if (descriptions.ContainsKey(key))
                    {
                        result.AddWarning(AppText.F("metadata.preset.duplicateDescription", key));
                    }
                    else
                    {
                        descriptions[key] = i;
                    }
                }
            }
        }

        /// <summary>
        /// Valida una singola regola del preset
        /// </summary>
        /// <param name="rule">Regola da validare</param>
        /// <param name="index">Indice regola</param>
        /// <param name="result">Risultato validazione da popolare</param>
        private static void ValidateRule(MkvMetadataRule rule, int index, MkvMetadataPresetValidationResult result)
        {
            if (rule == null)
            {
                result.AddError(AppText.F("metadata.preset.nullRule", index));
                return;
            }

            if (string.IsNullOrEmpty(rule.Description != null ? rule.Description.Trim() : null))
                result.AddError(AppText.F("metadata.preset.missingRuleDescription", index));

            if (rule.When != null && rule.When.All != null)
                ValidateConditionNodes(rule.When.All, rule.TargetScope, AppText.F("metadata.preset.ruleWhenPath", index), result);

            if (rule.Operations == null || rule.Operations.Count == 0)
            {
                result.AddWarning(AppText.F("metadata.preset.ruleWithoutOperations", index));
                return;
            }

            for (int i = 0; i < rule.Operations.Count; i++)
            {
                ValidateOperation(rule, rule.Operations[i], index, i, result);
            }
        }

        /// <summary>
        /// Valida una lista di nodi condizione
        /// </summary>
        /// <param name="nodes">Nodi condizione da validare</param>
        /// <param name="scope">Scope target della regola</param>
        /// <param name="path">Percorso diagnostico</param>
        /// <param name="result">Risultato validazione da popolare</param>
        private static void ValidateConditionNodes(List<MkvMetadataRuleConditionNode> nodes, MkvMetadataTargetScope scope, string path, MkvMetadataPresetValidationResult result)
        {
            if (nodes == null)
                return;

            for (int i = 0; i < nodes.Count; i++)
            {
                ValidateConditionNode(nodes[i], scope, path + "." + i.ToString(), result);
            }
        }

        /// <summary>
        /// Valida un nodo condizione in base al tipo dichiarato
        /// </summary>
        /// <param name="node">Nodo condizione</param>
        /// <param name="scope">Scope target della regola</param>
        /// <param name="path">Percorso diagnostico</param>
        /// <param name="result">Risultato validazione da popolare</param>
        private static void ValidateConditionNode(MkvMetadataRuleConditionNode node, MkvMetadataTargetScope scope, string path, MkvMetadataPresetValidationResult result)
        {
            if (node == null)
            {
                result.AddError(AppText.F("metadata.preset.nullCondition", path));
                return;
            }

            if (node.NodeType == MkvMetadataRuleConditionNodeType.AlternativeAny)
            {
                ValidateConditionNodes(node.Alternative != null ? node.Alternative.Any : null, scope, path + ".any", result);
            }
            else if (node.NodeType == MkvMetadataRuleConditionNodeType.Field)
            {
                ValidateFieldCondition(node.Field, scope, path, result);
            }
            else if (node.NodeType == MkvMetadataRuleConditionNodeType.TrackComparison)
            {
                ValidateTrackComparison(node.TrackComparison, scope, path, result);
            }
            else if (node.NodeType == MkvMetadataRuleConditionNodeType.TrackGroupCount)
            {
                if (scope == MkvMetadataTargetScope.Container)
                    result.AddError(AppText.F("metadata.preset.trackCountInvalidForFileScope", path));
            }
        }

        /// <summary>
        /// Valida una condizione su campo metadata
        /// </summary>
        /// <param name="condition">Condizione campo</param>
        /// <param name="scope">Scope target della regola</param>
        /// <param name="path">Percorso diagnostico</param>
        /// <param name="result">Risultato validazione da popolare</param>
        private static void ValidateFieldCondition(MkvMetadataFieldCondition condition, MkvMetadataTargetScope scope, string path, MkvMetadataPresetValidationResult result)
        {
            MetadataExpressionEngine expressionEngine = new MetadataExpressionEngine();
            MetadataFieldDefinition field;

            if (condition == null)
            {
                result.AddError(AppText.F("metadata.preset.nullCondition", path));
                return;
            }

            if (string.IsNullOrEmpty(condition.FieldKey != null ? condition.FieldKey.Trim() : null))
            {
                result.AddError(AppText.F("metadata.preset.missingConditionField", path));
            }
            else if (!TryGetConditionField(condition.FieldKey, out field))
            {
                result.AddError(AppText.F("metadata.preset.unknownConditionField", path, condition.FieldKey));
            }
            else if (!field.IsReadable || field.Visibility == MetadataFieldVisibility.Hidden || !MetadataScopeHelper.IsFieldReadableInScope(field, scope))
            {
                result.AddError(AppText.F("metadata.preset.conditionScopeMismatch", path, condition.FieldKey, scope));
            }

            ValidateFieldValue(condition, path, expressionEngine, result);
        }

        /// <summary>
        /// Valida una condizione di confronto tra tracce
        /// </summary>
        /// <param name="condition">Condizione confronto tracce</param>
        /// <param name="scope">Scope target della regola</param>
        /// <param name="path">Percorso diagnostico</param>
        /// <param name="result">Risultato validazione da popolare</param>
        private static void ValidateTrackComparison(MkvMetadataTrackComparisonCondition condition, MkvMetadataTargetScope scope, string path, MkvMetadataPresetValidationResult result)
        {
            MetadataFieldDefinition field;
            if (scope == MkvMetadataTargetScope.Container)
            {
                result.AddError(AppText.F("metadata.preset.trackCompareInvalidForFileScope", path));
                return;
            }

            if (condition == null || string.IsNullOrEmpty(condition.FieldKey != null ? condition.FieldKey.Trim() : null))
            {
                result.AddError(AppText.F("metadata.preset.missingConditionField", path));
            }
            else if (!MetadataFieldRegistry.TryGet(condition.FieldKey, out field))
            {
                result.AddError(AppText.F("metadata.preset.unknownConditionField", path, condition.FieldKey));
            }
            else if (!field.IsReadable || field.Visibility == MetadataFieldVisibility.Hidden || !MetadataScopeHelper.IsTrackFieldInScope(field, scope) || field.ValueType == MetadataFieldValueType.Boolean)
            {
                result.AddError(AppText.F("metadata.preset.conditionScopeMismatch", path, condition.FieldKey, scope));
            }

            if (condition != null && condition.Relation == MkvMetadataTrackComparisonRelation.Rank && condition.Rank <= 0)
                result.AddError(AppText.F("metadata.preset.invalidRank", path));
        }

        /// <summary>
        /// Valida i valori usati da una condizione su campo
        /// </summary>
        /// <param name="condition">Condizione campo</param>
        /// <param name="path">Percorso diagnostico</param>
        /// <param name="expressionEngine">Motore espressioni usato per validare i template</param>
        /// <param name="result">Risultato validazione da popolare</param>
        private static void ValidateFieldValue(MkvMetadataFieldCondition condition, string path, MetadataExpressionEngine expressionEngine, MkvMetadataPresetValidationResult result)
        {
            switch (condition.Operator)
            {
                case MkvMetadataConditionOperator.Regex:
                case MkvMetadataConditionOperator.NotRegex:
                    try
                    {
                        _ = new Regex(condition.Value != null ? condition.Value : "");
                    }
                    catch (ArgumentException ex)
                    {
                        result.AddError(AppText.F("metadata.preset.invalidRegex", path, ex.Message));
                    }
                    break;

                case MkvMetadataConditionOperator.InList:
                case MkvMetadataConditionOperator.NotInList:
                    if (condition.Values == null || condition.Values.Count == 0)
                        result.AddError(AppText.F("metadata.preset.listNeedsValues", path));

                    if (condition.Values != null)
                    {
                        for (int i = 0; i < condition.Values.Count; i++)
                        {
                            AddExpressionErrors(expressionEngine.Validate(condition.Values[i]), path + ".values." + i.ToString(), result);
                        }
                    }
                    break;

                case MkvMetadataConditionOperator.Between:
                case MkvMetadataConditionOperator.NotBetween:
                    if (string.IsNullOrEmpty(condition.FromValue != null ? condition.FromValue.Trim() : null) || string.IsNullOrEmpty(condition.ToValue != null ? condition.ToValue.Trim() : null))
                        result.AddError(AppText.F("metadata.preset.rangeNeedsValues", path));

                    AddExpressionErrors(expressionEngine.Validate(condition.FromValue), path + ".from", result);
                    AddExpressionErrors(expressionEngine.Validate(condition.ToValue), path + ".to", result);
                    break;

                case MkvMetadataConditionOperator.IsEmpty:
                case MkvMetadataConditionOperator.IsNotEmpty:
                case MkvMetadataConditionOperator.IsTrue:
                case MkvMetadataConditionOperator.IsFalse:
                    break;

                default:
                    AddExpressionErrors(expressionEngine.Validate(condition.Value), path, result);
                    break;
            }
        }

        /// <summary>
        /// Valida una operazione definita nella regola
        /// </summary>
        /// <param name="rule">Regola proprietaria</param>
        /// <param name="operation">Operazione da validare</param>
        /// <param name="ruleIndex">Indice regola</param>
        /// <param name="operationIndex">Indice operazione</param>
        /// <param name="result">Risultato validazione da popolare</param>
        private static void ValidateOperation(MkvMetadataRule rule, MkvMetadataOperation operation, int ruleIndex, int operationIndex, MkvMetadataPresetValidationResult result)
        {
            string errorMessage;
            MetadataExpressionEngine expressionEngine = new MetadataExpressionEngine();

            if (operation == null)
            {
                result.AddError(AppText.F("metadata.preset.nullOperation", ruleIndex, operationIndex));
                return;
            }

            if (operation.Type == MkvMetadataOperationType.SetField || operation.Type == MkvMetadataOperationType.ClearField || operation.Type == MkvMetadataOperationType.SetExclusiveFlag)
            {
                if (!MetadataFieldRegistry.ValidateWritable(operation.FieldKey, out errorMessage))
                {
                    result.AddError(AppText.F("metadata.preset.operationError", ruleIndex, operationIndex, errorMessage));
                }
                else if (!MetadataFieldRegistry.IsScopeCompatible(operation.FieldKey, rule.TargetScope))
                {
                    result.AddError(AppText.F("metadata.preset.operationScopeMismatch", ruleIndex, operationIndex, rule.TargetScope));
                }
            }

            if (operation.Type == MkvMetadataOperationType.SetExclusiveFlag)
            {
                MetadataFieldDefinition field;
                if (MetadataFieldRegistry.TryGet(operation.FieldKey, out field) && field.ValueType != MetadataFieldValueType.Boolean)
                    result.AddError(AppText.F("metadata.preset.exclusiveFlagRequiresBoolean", ruleIndex, operationIndex));
            }

            if (operation.Type == MkvMetadataOperationType.ClearField)
            {
                MetadataFieldDefinition field;
                if (MetadataFieldRegistry.TryGet(operation.FieldKey, out field) && !field.IsClearable)
                    result.AddError(AppText.F("metadata.preset.fieldNotClearable", ruleIndex, operationIndex));
            }

            if (operation.Type == MkvMetadataOperationType.SetTagField || operation.Type == MkvMetadataOperationType.ClearTagField)
            {
                MkvMetadataTargetScope tagScope = MetadataUiCatalog.GetTagTargetScope(rule.TargetScope, operation.TagTarget);
                MetadataTagDefinition tag;
                if (!MetadataTagRegistry.ValidateWritable(operation.TagKey, tagScope, out errorMessage))
                    result.AddError(AppText.F("metadata.preset.operationError", ruleIndex, operationIndex, errorMessage));
                else if (operation.Type == MkvMetadataOperationType.ClearTagField && MetadataTagRegistry.TryGet(operation.TagKey, out tag) && !tag.IsClearable)
                    result.AddError(AppText.F("metadata.preset.operationError", ruleIndex, operationIndex, AppText.F("web.metadata.manualEdit.fieldNotClearable", tag.Label)));
            }

            if (operation.Type == MkvMetadataOperationType.SetTagField || operation.Type == MkvMetadataOperationType.ClearTagField)
            {
                bool knownLevel = operation.TagTargetTypeValue == MetadataTagTargetLevels.TRACK ||
                    operation.TagTargetTypeValue == MetadataTagTargetLevels.EPISODE ||
                    operation.TagTargetTypeValue == MetadataTagTargetLevels.SEASON ||
                    operation.TagTargetTypeValue == MetadataTagTargetLevels.COLLECTION;

                if (!knownLevel)
                    result.AddError(AppText.F("metadata.preset.tagLevelUnknown", ruleIndex, operationIndex, operation.TagTargetTypeValue));
            }

            if (operation.Type == MkvMetadataOperationType.ClearTags && !operation.ClearTagsConfirmed)
                result.AddError(AppText.F("metadata.preset.clearTagsNeedsConfirmation", ruleIndex, operationIndex));

            if (operation.Type == MkvMetadataOperationType.RemoveTrack && rule.TargetScope == MkvMetadataTargetScope.Container)
                result.AddError(AppText.F("metadata.preset.removeTrackRequiresTrackScope", ruleIndex, operationIndex));

            if (operation.Type == MkvMetadataOperationType.SetAttachment || operation.Type == MkvMetadataOperationType.DeleteAttachment)
            {
                if (rule.TargetScope != MkvMetadataTargetScope.Container)
                    result.AddError(AppText.F("metadata.preset.attachmentRequiresContainerScope", ruleIndex, operationIndex));

                if (operation.Type == MkvMetadataOperationType.SetAttachment && string.IsNullOrEmpty(operation.AttachmentSourcePath))
                    result.AddError(AppText.F("metadata.preset.attachmentSourceRequired", ruleIndex, operationIndex));

                // Senza nome esplicito quello dell'allegato lo decide il file sorgente,
                // quindi manca solo se manca anche la sorgente da cui ricavarlo
                if (operation.Type == MkvMetadataOperationType.DeleteAttachment && string.IsNullOrEmpty(operation.AttachmentName))
                    result.AddError(AppText.F("metadata.preset.attachmentNameRequired", ruleIndex, operationIndex));

                AddExpressionErrors(expressionEngine.Validate(operation.AttachmentSourcePath), AppText.F("metadata.preset.ruleOperationPath", ruleIndex, operationIndex), result);
            }

            // I capitoli si modificano solo dall'editor manuale, file per file
            if (operation.Type == MkvMetadataOperationType.EditChapters)
                result.AddError(AppText.F("metadata.preset.editChaptersManualOnly", ruleIndex, operationIndex));

            if (operation.Type == MkvMetadataOperationType.SetField ||
                operation.Type == MkvMetadataOperationType.SetTagField)
                AddExpressionErrors(expressionEngine.Validate(operation.Value), AppText.F("metadata.preset.ruleOperationPath", ruleIndex, operationIndex), result);
        }

        /// <summary>
        /// Aggiunge gli errori espressione al risultato preset
        /// </summary>
        /// <param name="errors">Errori espressione</param>
        /// <param name="path">Percorso diagnostico</param>
        /// <param name="result">Risultato validazione da popolare</param>
        private static void AddExpressionErrors(List<string> errors, string path, MkvMetadataPresetValidationResult result)
        {
            for (int i = 0; i < errors.Count; i++)
            {
                result.AddError(AppText.F("metadata.preset.pathError", path, errors[i]));
            }
        }

        /// <summary>
        /// Cerca un campo condizione rimuovendo eventuale prefisso temporale
        /// </summary>
        /// <param name="fieldKey">Chiave campo della condizione</param>
        /// <param name="field">Campo trovato</param>
        /// <returns>Vero se il campo esiste nel registro</returns>
        private static bool TryGetConditionField(string fieldKey, out MetadataFieldDefinition field)
        {
            string key = fieldKey != null ? fieldKey.Trim() : "";
            if (key.StartsWith("original.", StringComparison.OrdinalIgnoreCase) || key.StartsWith("current.", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(key.IndexOf(".", StringComparison.Ordinal) + 1);

            return MetadataFieldRegistry.TryGet(key, out field);
        }

        #endregion
    }
}
