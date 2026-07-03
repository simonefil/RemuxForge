using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
        private const int CURRENT_SCHEMA_VERSION = 3;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Cartella fissa dei preset metadata
        /// </summary>
        private readonly string _presetFolder;

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
        /// Carica un preset da JSON
        /// </summary>
        /// <param name="filePath">Percorso preset</param>
        /// <returns>Preset</returns>
        public MkvMetadataPreset Load(string filePath)
        {
            string json;
            MkvMetadataPreset preset;
            JsonSerializerOptions options;

            if (filePath == null || filePath.Trim().Length == 0)
                throw new ArgumentException(AppText.T("metadata.preset.emptyPath"), nameof(filePath));

            json = File.ReadAllText(filePath);
            options = CreateSerializerOptions();
            preset = JsonSerializer.Deserialize<MkvMetadataPreset>(json, options);
            if (preset == null)
                throw new InvalidOperationException(AppText.T("metadata.preset.invalid"));

            NormalizePreset(preset);
            MkvMetadataPresetValidationResult validation = Validate(preset);
            if (!validation.IsValid)
                throw new InvalidOperationException(validation.ErrorMessage);

            return preset;
        }

        /// <summary>
        /// Salva un preset JSON
        /// </summary>
        /// <param name="preset">Preset da salvare</param>
        /// <param name="filePath">Percorso output</param>
        public void Save(MkvMetadataPreset preset, string filePath)
        {
            JsonSerializerOptions options;
            MkvMetadataPresetValidationResult validation;
            string json;

            if (preset == null)
                throw new ArgumentNullException(nameof(preset));

            NormalizePreset(preset);
            validation = Validate(preset);
            if (!validation.IsValid)
                throw new InvalidOperationException(validation.ErrorMessage);

            string folder = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            options = CreateSerializerOptions();
            json = JsonSerializer.Serialize(preset, options);
            File.WriteAllText(filePath, json);
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
            if (preset.Name == null)
                preset.Name = "";

            if (preset.Description == null)
                preset.Description = "";

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

            if (rule.Description == null)
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

                    if (node.Field.FieldKey == null)
                        node.Field.FieldKey = "";

                    if (node.Field.Value == null)
                        node.Field.Value = "";

                    if (node.Field.Values == null)
                        node.Field.Values = new List<string>();

                    if (node.Field.FromValue == null)
                        node.Field.FromValue = "";

                    if (node.Field.ToValue == null)
                        node.Field.ToValue = "";

                    if (node.Field.Unit == null)
                        node.Field.Unit = "";

                    node.TrackComparison = null;
                    node.TrackGroupCount = null;
                    node.Alternative = null;
                }
                else if (node.NodeType == MkvMetadataRuleConditionNodeType.TrackComparison)
                {
                    if (node.TrackComparison == null)
                        node.TrackComparison = new MkvMetadataTrackComparisonCondition();

                    if (node.TrackComparison.FieldKey == null)
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

            if (operation.FieldKey == null)
                operation.FieldKey = "";

            if (operation.Value == null)
                operation.Value = "";

            if (operation.TagKey == null)
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
                if (rules[i] != null && rules[i].Description != null && rules[i].Description.Trim().Length > 0)
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

            if (rule.Description == null || rule.Description.Trim().Length == 0)
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

            if (condition.FieldKey == null || condition.FieldKey.Trim().Length == 0)
            {
                result.AddError(AppText.F("metadata.preset.missingConditionField", path));
            }
            else if (!TryGetConditionField(condition.FieldKey, out field))
            {
                result.AddError(AppText.F("metadata.preset.unknownConditionField", path, condition.FieldKey));
            }
            else if (!MetadataScopeHelper.IsFieldReadableInScope(field, scope))
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

            if (condition == null || condition.FieldKey == null || condition.FieldKey.Trim().Length == 0)
            {
                result.AddError(AppText.F("metadata.preset.missingConditionField", path));
            }
            else if (!MetadataFieldRegistry.TryGet(condition.FieldKey, out field))
            {
                result.AddError(AppText.F("metadata.preset.unknownConditionField", path, condition.FieldKey));
            }
            else if (!MetadataScopeHelper.IsTrackFieldInScope(field, scope) || field.ValueType == MetadataFieldValueType.Boolean)
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
                    if ((condition.FromValue == null || condition.FromValue.Trim().Length == 0) || (condition.ToValue == null || condition.ToValue.Trim().Length == 0))
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
                if (!MetadataTagRegistry.IsAllowed(operation.TagKey))
                    result.AddError(AppText.F("metadata.preset.unsupportedUiTag", ruleIndex, operationIndex, operation.TagKey));
            }

            if (operation.Type == MkvMetadataOperationType.ClearTags && !operation.ClearTagsConfirmed)
                result.AddError(AppText.F("metadata.preset.clearTagsNeedsConfirmation", ruleIndex, operationIndex));

            if (operation.Type == MkvMetadataOperationType.RemoveTrack && rule.TargetScope == MkvMetadataTargetScope.Container)
                result.AddError(AppText.F("metadata.preset.removeTrackRequiresTrackScope", ruleIndex, operationIndex));

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
