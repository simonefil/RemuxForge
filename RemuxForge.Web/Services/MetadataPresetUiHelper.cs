using RemuxForge.Core.Localization;
using RemuxForge.Core.Metadata;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Helper UI per opzioni, label e normalizzazione leggera del builder preset metadata
    /// </summary>
    public static class MetadataPresetUiHelper
    {
        #region Metodi pubblici

        /// <summary>
        /// Normalizza il preset prima di usarlo nel builder
        /// </summary>
        /// <param name="preset">Preset da normalizzare</param>
        /// <param name="includeAdvancedFields">Indica se includere campi avanzati nelle operazioni</param>
        public static void EnsurePreset(MkvMetadataPreset preset, bool includeAdvancedFields)
        {
            if (preset == null)
                return;

            if (preset.Rules == null)
                preset.Rules = new List<MkvMetadataRule>();

            for (int i = 0; i < preset.Rules.Count; i++)
                EnsureRule(preset.Rules[i], includeAdvancedFields);
        }

        /// <summary>
        /// Normalizza una regola prima di usarla nel builder
        /// </summary>
        /// <param name="rule">Regola da normalizzare</param>
        /// <param name="includeAdvancedFields">Indica se includere campi avanzati nelle operazioni</param>
        public static void EnsureRule(MkvMetadataRule rule, bool includeAdvancedFields)
        {
            if (rule == null)
                return;

            if (rule.Description == null)
                rule.Description = "";
            if (rule.When == null)
                rule.When = new MkvMetadataRuleWhen();
            if (rule.When.All == null)
                rule.When.All = new List<MkvMetadataRuleConditionNode>();

            for (int i = 0; i < rule.When.All.Count; i++)
                EnsureNode(rule, rule.When.All[i]);

            if (rule.Operations == null)
                rule.Operations = new List<MkvMetadataOperation>();

            for (int i = 0; i < rule.Operations.Count; i++)
                EnsureOperation(rule.TargetScope, rule.Operations[i], includeAdvancedFields);
        }

        /// <summary>
        /// Normalizza un nodo condizione
        /// </summary>
        /// <param name="rule">Regola proprietaria</param>
        /// <param name="node">Nodo da normalizzare</param>
        public static void EnsureNode(MkvMetadataRule rule, MkvMetadataRuleConditionNode node)
        {
            if (node == null)
                return;

            MkvMetadataTargetScope scope = rule != null ? rule.TargetScope : MkvMetadataTargetScope.Audio;
            if (node.NodeType == MkvMetadataRuleConditionNodeType.Field)
            {
                if (node.Field == null)
                    node.Field = new MkvMetadataFieldCondition();

                node.TrackComparison = null;
                node.TrackGroupCount = null;
                node.Alternative = null;
                EnsureFieldCondition(scope, node.Field);
            }
            else if (node.NodeType == MkvMetadataRuleConditionNodeType.TrackComparison)
            {
                if (node.TrackComparison == null)
                    node.TrackComparison = new MkvMetadataTrackComparisonCondition();

                node.Field = null;
                node.TrackGroupCount = null;
                node.Alternative = null;
                EnsureTrackComparisonCondition(scope, node.TrackComparison);
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
                for (int i = 0; i < node.Alternative.Any.Count; i++)
                    EnsureNode(rule, node.Alternative.Any[i]);
            }
        }

        /// <summary>
        /// Normalizza una condizione su campo
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <param name="condition">Condizione da normalizzare</param>
        public static void EnsureFieldCondition(MkvMetadataTargetScope scope, MkvMetadataFieldCondition condition)
        {
            if (condition == null)
                return;

            if (condition.FieldKey == null || !ContainsField(GetReadableFieldsForCondition(scope), condition.FieldKey))
                condition.FieldKey = GetDefaultFieldConditionField(scope);

            if (condition.Value == null)
                condition.Value = "";
            if (condition.Values == null)
                condition.Values = new List<string>();
            if (condition.FromValue == null)
                condition.FromValue = "";
            if (condition.ToValue == null)
                condition.ToValue = "";
            if (condition.Unit == null)
                condition.Unit = "";

            List<MkvMetadataConditionOperator> operators = GetFieldOperatorOptions(condition.FieldKey);
            if (!operators.Contains(condition.Operator))
                condition.Operator = operators.Count > 0 ? operators[0] : MkvMetadataConditionOperator.Equals;
        }

        /// <summary>
        /// Normalizza una condizione di confronto tracce
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <param name="condition">Condizione da normalizzare</param>
        public static void EnsureTrackComparisonCondition(MkvMetadataTargetScope scope, MkvMetadataTrackComparisonCondition condition)
        {
            if (condition == null)
                return;

            if (condition.FieldKey == null || !ContainsField(GetTrackComparableFields(scope), condition.FieldKey))
                condition.FieldKey = GetDefaultTrackComparisonField(scope);

            List<MkvMetadataTrackComparisonRelation> relations = GetTrackComparisonRelations(condition.FieldKey);
            if (!relations.Contains(condition.Relation))
                condition.Relation = relations.Count > 0 ? relations[0] : MkvMetadataTrackComparisonRelation.Largest;
            if (condition.Rank <= 0)
                condition.Rank = 1;
        }

        /// <summary>
        /// Normalizza un'operazione THEN
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <param name="operation">Operazione da normalizzare</param>
        /// <param name="includeAdvancedFields">Indica se includere campi avanzati</param>
        public static void EnsureOperation(MkvMetadataTargetScope scope, MkvMetadataOperation operation, bool includeAdvancedFields)
        {
            if (operation == null)
                return;

            List<MkvMetadataOperationType> operationTypes = GetOperationTypeOptions(scope);
            if (!operationTypes.Contains(operation.Type))
                operation.Type = MkvMetadataOperationType.SetField;

            if (operation.FieldKey == null)
                operation.FieldKey = "";
            if (operation.Value == null)
                operation.Value = "";
            if (operation.TagKey == null)
                operation.TagKey = "";

            List<string> tagNames = GetTagNames();
            if (operation.TagKey.Length == 0 && tagNames.Count > 0)
                operation.TagKey = tagNames[0];

            if (RequiresEditableField(operation.Type) && !ContainsField(GetEditableFields(scope, operation, includeAdvancedFields), operation.FieldKey))
            {
                List<MetadataFieldDefinition> fields = GetEditableFields(scope, operation, includeAdvancedFields);
                operation.FieldKey = fields.Count > 0 ? fields[0].Key : "";
            }
        }

        /// <summary>
        /// Crea una condizione su campo con default coerenti allo scope
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <returns>Nodo condizione</returns>
        public static MkvMetadataRuleConditionNode CreateFieldConditionNode(MkvMetadataTargetScope scope)
        {
            MkvMetadataRuleConditionNode node = new MkvMetadataRuleConditionNode();
            node.NodeType = MkvMetadataRuleConditionNodeType.Field;
            node.Field = new MkvMetadataFieldCondition();
            node.Field.FieldKey = GetDefaultFieldConditionField(scope);
            EnsureFieldCondition(scope, node.Field);
            return node;
        }

        /// <summary>
        /// Crea una condizione confronto tracce con default coerenti allo scope
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <returns>Nodo condizione</returns>
        public static MkvMetadataRuleConditionNode CreateTrackComparisonNode(MkvMetadataTargetScope scope)
        {
            MkvMetadataRuleConditionNode node = new MkvMetadataRuleConditionNode();
            node.NodeType = MkvMetadataRuleConditionNodeType.TrackComparison;
            node.Field = null;
            node.TrackComparison = new MkvMetadataTrackComparisonCondition();
            node.TrackComparison.FieldKey = GetDefaultTrackComparisonField(scope);
            EnsureTrackComparisonCondition(scope, node.TrackComparison);
            return node;
        }

        /// <summary>
        /// Crea una condizione conteggio tracce
        /// </summary>
        /// <returns>Nodo condizione</returns>
        public static MkvMetadataRuleConditionNode CreateTrackGroupCountNode()
        {
            MkvMetadataRuleConditionNode node = new MkvMetadataRuleConditionNode();
            node.NodeType = MkvMetadataRuleConditionNodeType.TrackGroupCount;
            node.Field = null;
            node.TrackGroupCount = new MkvMetadataTrackGroupCountCondition();
            return node;
        }

        /// <summary>
        /// Restituisce i campi leggibili disponibili per una condizione
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <returns>Campi leggibili</returns>
        public static List<MetadataFieldDefinition> GetReadableFieldsForCondition(MkvMetadataTargetScope scope)
        {
            List<MetadataFieldDefinition> all = MetadataFieldRegistry.GetAll();
            List<MetadataFieldDefinition> result = new List<MetadataFieldDefinition>();
            for (int i = 0; i < all.Count; i++)
            {
                if (!all[i].IsReadable)
                    continue;

                if (MetadataScopeHelper.IsFieldReadableInScope(all[i], scope))
                    result.Add(all[i]);
            }

            SortByLabel(result, GetFieldLabel);
            return result;
        }

        /// <summary>
        /// Restituisce i campi confrontabili tra tracce
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <returns>Campi confrontabili</returns>
        public static List<MetadataFieldDefinition> GetTrackComparableFields(MkvMetadataTargetScope scope)
        {
            List<MetadataFieldDefinition> fields = GetReadableFieldsForCondition(scope);
            List<MetadataFieldDefinition> result = new List<MetadataFieldDefinition>();
            for (int i = 0; i < fields.Count; i++)
            {
                if (MetadataScopeHelper.IsTrackFieldInScope(fields[i], scope) && fields[i].ValueType != MetadataFieldValueType.Boolean)
                    result.Add(fields[i]);
            }

            SortByLabel(result, GetFieldLabel);
            return result;
        }

        /// <summary>
        /// Restituisce i campi editabili per una operazione
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <param name="operation">Operazione corrente</param>
        /// <param name="includeAdvancedFields">Indica se includere campi avanzati</param>
        /// <returns>Campi editabili</returns>
        public static List<MetadataFieldDefinition> GetEditableFields(MkvMetadataTargetScope scope, MkvMetadataOperation operation, bool includeAdvancedFields)
        {
            List<MetadataFieldDefinition> fields = MetadataFieldRegistry.GetEditable(scope, includeAdvancedFields);
            List<MetadataFieldDefinition> result = new List<MetadataFieldDefinition>();
            MkvMetadataOperationType operationType = operation != null ? operation.Type : MkvMetadataOperationType.SetField;
            for (int i = 0; i < fields.Count; i++)
            {
                if (operationType == MkvMetadataOperationType.ClearField && !fields[i].IsClearable)
                    continue;
                if (operationType == MkvMetadataOperationType.SetExclusiveFlag && fields[i].ValueType != MetadataFieldValueType.Boolean)
                    continue;

                result.Add(fields[i]);
            }

            SortByLabel(result, GetFieldLabel);
            return result;
        }

        /// <summary>
        /// Restituisce gli operatori disponibili per un campo
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Operatori compatibili</returns>
        public static List<MkvMetadataConditionOperator> GetFieldOperatorOptions(string fieldKey)
        {
            MetadataFieldDefinition field;
            if (!MetadataFieldRegistry.TryGet(fieldKey, out field))
                return GetTextOperatorOptions();
            if (field.ValueType == MetadataFieldValueType.Boolean)
            {
                List<MkvMetadataConditionOperator> result = new List<MkvMetadataConditionOperator> { MkvMetadataConditionOperator.IsTrue, MkvMetadataConditionOperator.IsFalse };
                SortByLabel(result, GetConditionOperatorLabel);
                return result;
            }
            if (MetadataValueNormalizer.IsNumericValueType(field.ValueType))
                return GetNumericOperatorOptions(true);

            return GetTextOperatorOptions();
        }

        /// <summary>
        /// Restituisce gli operatori numerici
        /// </summary>
        /// <param name="includeBetween">Indica se includere operatori range</param>
        /// <returns>Operatori numerici</returns>
        public static List<MkvMetadataConditionOperator> GetNumericOperatorOptions(bool includeBetween)
        {
            List<MkvMetadataConditionOperator> result = new List<MkvMetadataConditionOperator>
            {
                MkvMetadataConditionOperator.Equals,
                MkvMetadataConditionOperator.NotEquals,
                MkvMetadataConditionOperator.GreaterThan,
                MkvMetadataConditionOperator.GreaterOrEqual,
                MkvMetadataConditionOperator.LessThan,
                MkvMetadataConditionOperator.LessOrEqual
            };
            if (includeBetween)
            {
                result.Add(MkvMetadataConditionOperator.Between);
                result.Add(MkvMetadataConditionOperator.NotBetween);
            }
            result.Add(MkvMetadataConditionOperator.IsEmpty);
            result.Add(MkvMetadataConditionOperator.IsNotEmpty);
            SortByLabel(result, GetConditionOperatorLabel);
            return result;
        }

        /// <summary>
        /// Restituisce le relazioni confronto tracce disponibili per un campo
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Relazioni disponibili</returns>
        public static List<MkvMetadataTrackComparisonRelation> GetTrackComparisonRelations(string fieldKey)
        {
            MetadataFieldDefinition field;
            if (MetadataFieldRegistry.TryGet(fieldKey, out field) && !MetadataValueNormalizer.IsNumericValueType(field.ValueType))
            {
                List<MkvMetadataTrackComparisonRelation> textResult = new List<MkvMetadataTrackComparisonRelation>
                {
                    MkvMetadataTrackComparisonRelation.EqualsAny,
                    MkvMetadataTrackComparisonRelation.NotEqualsAll
                };
                SortByLabel(textResult, GetTrackComparisonRelationLabel);
                return textResult;
            }

            List<MkvMetadataTrackComparisonRelation> result = new List<MkvMetadataTrackComparisonRelation>
            {
                MkvMetadataTrackComparisonRelation.Largest,
                MkvMetadataTrackComparisonRelation.Smallest,
                MkvMetadataTrackComparisonRelation.GreaterThanAll,
                MkvMetadataTrackComparisonRelation.GreaterOrEqualAll,
                MkvMetadataTrackComparisonRelation.LessThanAll,
                MkvMetadataTrackComparisonRelation.LessOrEqualAll,
                MkvMetadataTrackComparisonRelation.Rank
            };
            SortByLabel(result, GetTrackComparisonRelationLabel);
            return result;
        }

        /// <summary>
        /// Restituisce i gruppi tracce disponibili
        /// </summary>
        /// <returns>Gruppi tracce</returns>
        public static List<MkvMetadataTrackGroup> GetTrackGroupOptions()
        {
            List<MkvMetadataTrackGroup> result = new List<MkvMetadataTrackGroup>
            {
                MkvMetadataTrackGroup.SameLanguage,
                MkvMetadataTrackGroup.SameFormat,
                MkvMetadataTrackGroup.SameLanguageAndFormat,
                MkvMetadataTrackGroup.AllInScope
            };
            SortByLabel(result, GetTrackGroupLabel);
            return result;
        }

        /// <summary>
        /// Restituisce i gruppi esclusivi disponibili
        /// </summary>
        /// <returns>Gruppi esclusivi</returns>
        public static List<MkvMetadataExclusiveGroup> GetExclusiveGroupOptions()
        {
            List<MkvMetadataExclusiveGroup> result = new List<MkvMetadataExclusiveGroup>
            {
                MkvMetadataExclusiveGroup.SameLanguage,
                MkvMetadataExclusiveGroup.SameFormat,
                MkvMetadataExclusiveGroup.SameLanguageAndFormat,
                MkvMetadataExclusiveGroup.AllInScope
            };
            SortByLabel(result, GetExclusiveGroupLabel);
            return result;
        }

        /// <summary>
        /// Restituisce gli scope regola disponibili
        /// </summary>
        /// <returns>Scope regola</returns>
        public static List<MkvMetadataTargetScope> GetScopeOptions()
        {
            List<MkvMetadataTargetScope> result = new List<MkvMetadataTargetScope>
            {
                MkvMetadataTargetScope.Container,
                MkvMetadataTargetScope.Video,
                MkvMetadataTargetScope.Audio,
                MkvMetadataTargetScope.Subtitle
            };
            SortByLabel(result, GetScopeLabel);
            return result;
        }

        /// <summary>
        /// Restituisce i tipi operazione disponibili
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <returns>Tipi operazione disponibili</returns>
        public static List<MkvMetadataOperationType> GetOperationTypeOptions(MkvMetadataTargetScope scope)
        {
            List<MkvMetadataOperationType> result = new List<MkvMetadataOperationType>();
            bool isContainer = scope == MkvMetadataTargetScope.Container;

            result.Add(MkvMetadataOperationType.SetField);
            result.Add(MkvMetadataOperationType.ClearField);
            if (!isContainer)
            {
                result.Add(MkvMetadataOperationType.SetExclusiveFlag);
                result.Add(MkvMetadataOperationType.RemoveTrack);
            }
            result.Add(MkvMetadataOperationType.SetTagField);
            result.Add(MkvMetadataOperationType.ClearTagField);
            result.Add(MkvMetadataOperationType.ClearTags);
            result.Add(MkvMetadataOperationType.AddOrUpdateTrackStatisticsTags);
            result.Add(MkvMetadataOperationType.DeleteTrackStatisticsTags);
            SortByLabel(result, GetOperationTypeLabel);
            return result;
        }

        /// <summary>
        /// Restituisce i tag editabili
        /// </summary>
        /// <returns>Nomi tag ordinati</returns>
        public static List<string> GetTagNames()
        {
            List<string> result = MetadataTagRegistry.GetEditableTagNames();
            result.Sort(StringComparer.CurrentCultureIgnoreCase);
            return result;
        }

        /// <summary>
        /// Indica se una condizione richiede un valore
        /// </summary>
        /// <param name="conditionOperator">Operatore condizione</param>
        /// <returns>Vero se serve un valore</returns>
        public static bool OperatorNeedsValue(MkvMetadataConditionOperator conditionOperator)
        {
            return conditionOperator != MkvMetadataConditionOperator.IsEmpty &&
                conditionOperator != MkvMetadataConditionOperator.IsNotEmpty &&
                conditionOperator != MkvMetadataConditionOperator.IsTrue &&
                conditionOperator != MkvMetadataConditionOperator.IsFalse;
        }

        /// <summary>
        /// Indica se il campo richiede selezione unità
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Vero se il campo usa unità</returns>
        public static bool ShouldShowUnit(string fieldKey)
        {
            MetadataFieldDefinition field;
            return MetadataFieldRegistry.TryGet(fieldKey, out field) && (field.ValueType == MetadataFieldValueType.Bytes || field.ValueType == MetadataFieldValueType.Duration);
        }

        /// <summary>
        /// Restituisce le unità disponibili per un campo
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Unità disponibili</returns>
        public static List<string> GetUnits(string fieldKey)
        {
            MetadataFieldDefinition field;
            List<string> result = new List<string>();
            result.Add("");
            if (!MetadataFieldRegistry.TryGet(fieldKey, out field))
                return result;

            if (field.ValueType == MetadataFieldValueType.Bytes)
            {
                result.Add("KB");
                result.Add("MB");
                result.Add("GB");
                result.Add("KiB");
                result.Add("MiB");
                result.Add("GiB");
            }
            if (field.ValueType == MetadataFieldValueType.Duration)
            {
                result.Add("ms");
                result.Add("s");
                result.Add("min");
                result.Add("h");
            }

            return result;
        }

        /// <summary>
        /// Restituisce il tipo input HTML adatto al campo
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Tipo input</returns>
        public static string GetInputType(string fieldKey)
        {
            MetadataFieldDefinition field;
            if (!MetadataFieldRegistry.TryGet(fieldKey, out field))
                return "text";
            if (MetadataValueNormalizer.IsNumericValueType(field.ValueType))
                return "number";

            return "text";
        }

        /// <summary>
        /// Indica se uno scope può usare condizioni tra tracce
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <returns>Vero se lo scope è una traccia</returns>
        public static bool CanUseTrackConditions(MkvMetadataTargetScope scope)
        {
            return scope != MkvMetadataTargetScope.Container;
        }

        /// <summary>
        /// Indica se una traccia appartiene allo scope
        /// </summary>
        /// <param name="track">Traccia</param>
        /// <param name="scope">Ambito regola</param>
        /// <returns>Vero se appartiene allo scope</returns>
        /// <summary>
        /// Restituisce label scope
        /// </summary>
        /// <param name="scope">Scope</param>
        /// <returns>Label localizzata</returns>
        public static string GetScopeLabel(MkvMetadataTargetScope scope)
        {
            if (scope == MkvMetadataTargetScope.Container)
                return AppText.T("web.metadata.scope.container");
            if (scope == MkvMetadataTargetScope.Video)
                return AppText.T("web.metadata.scope.video");
            if (scope == MkvMetadataTargetScope.Audio)
                return AppText.T("web.metadata.scope.audio");
            if (scope == MkvMetadataTargetScope.Subtitle)
                return AppText.T("web.metadata.scope.subtitle");

            return scope.ToString();
        }

        /// <summary>
        /// Restituisce label settore campo
        /// </summary>
        /// <param name="sector">Settore</param>
        /// <returns>Label localizzata</returns>
        public static string GetSectorLabel(MetadataFieldSector sector)
        {
            return AppText.T("web.metadata.fieldSector." + sector.ToString());
        }

        /// <summary>
        /// Restituisce label operatore condizione
        /// </summary>
        /// <param name="op">Operatore</param>
        /// <returns>Label localizzata</returns>
        public static string GetConditionOperatorLabel(MkvMetadataConditionOperator op)
        {
            return AppText.T("web.metadata.conditionOperator." + op.ToString());
        }

        /// <summary>
        /// Restituisce label tipo operazione
        /// </summary>
        /// <param name="operationType">Tipo operazione</param>
        /// <returns>Label localizzata</returns>
        public static string GetOperationTypeLabel(MkvMetadataOperationType operationType)
        {
            return AppText.T("web.metadata.operation." + operationType.ToString());
        }

        /// <summary>
        /// Restituisce label relazione confronto tracce
        /// </summary>
        /// <param name="relation">Relazione</param>
        /// <returns>Label localizzata</returns>
        public static string GetTrackComparisonRelationLabel(MkvMetadataTrackComparisonRelation relation)
        {
            return AppText.T("web.metadata.trackComparison." + relation.ToString());
        }

        /// <summary>
        /// Restituisce label gruppo tracce
        /// </summary>
        /// <param name="group">Gruppo</param>
        /// <returns>Label localizzata</returns>
        public static string GetTrackGroupLabel(MkvMetadataTrackGroup group)
        {
            return AppText.T("web.metadata.trackGroup." + group.ToString());
        }

        /// <summary>
        /// Restituisce label gruppo esclusivo
        /// </summary>
        /// <param name="group">Gruppo</param>
        /// <returns>Label localizzata</returns>
        public static string GetExclusiveGroupLabel(MkvMetadataExclusiveGroup group)
        {
            return AppText.T("web.metadata.exclusiveGroup." + group.ToString());
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Restituisce gli operatori testuali
        /// </summary>
        /// <returns>Operatori testuali</returns>
        private static List<MkvMetadataConditionOperator> GetTextOperatorOptions()
        {
            List<MkvMetadataConditionOperator> result = new List<MkvMetadataConditionOperator>
            {
                MkvMetadataConditionOperator.Equals,
                MkvMetadataConditionOperator.NotEquals,
                MkvMetadataConditionOperator.Contains,
                MkvMetadataConditionOperator.NotContains,
                MkvMetadataConditionOperator.StartsWith,
                MkvMetadataConditionOperator.EndsWith,
                MkvMetadataConditionOperator.Regex,
                MkvMetadataConditionOperator.NotRegex,
                MkvMetadataConditionOperator.InList,
                MkvMetadataConditionOperator.NotInList,
                MkvMetadataConditionOperator.IsEmpty,
                MkvMetadataConditionOperator.IsNotEmpty
            };
            SortByLabel(result, GetConditionOperatorLabel);
            return result;
        }

        /// <summary>
        /// Restituisce il campo condizione predefinito per scope
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <returns>Chiave campo</returns>
        private static string GetDefaultFieldConditionField(MkvMetadataTargetScope scope)
        {
            List<MetadataFieldDefinition> fields = GetReadableFieldsForCondition(scope);
            string preferred = scope == MkvMetadataTargetScope.Container ? "container_title" :
                scope == MkvMetadataTargetScope.Video ? "video_format" :
                scope == MkvMetadataTargetScope.Audio ? "audio_language" : "subtitle_language";

            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Key == preferred)
                    return fields[i].Key;
            }

            return fields.Count > 0 ? fields[0].Key : "";
        }

        /// <summary>
        /// Restituisce il campo confronto tracce predefinito per scope
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <returns>Chiave campo</returns>
        private static string GetDefaultTrackComparisonField(MkvMetadataTargetScope scope)
        {
            List<MetadataFieldDefinition> fields = GetTrackComparableFields(scope);
            string preferred = scope == MkvMetadataTargetScope.Video ? "video_stream_size" :
                scope == MkvMetadataTargetScope.Audio ? "audio_stream_size" : "subtitle_stream_size";

            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Key == preferred)
                    return fields[i].Key;
            }

            return fields.Count > 0 ? fields[0].Key : "";
        }

        /// <summary>
        /// Indica se l'operazione richiede un campo editabile
        /// </summary>
        /// <param name="operationType">Tipo operazione</param>
        /// <returns>Vero se richiede campo</returns>
        private static bool RequiresEditableField(MkvMetadataOperationType operationType)
        {
            return operationType == MkvMetadataOperationType.SetField ||
                operationType == MkvMetadataOperationType.ClearField ||
                operationType == MkvMetadataOperationType.SetExclusiveFlag;
        }

        /// <summary>
        /// Verifica se una lista contiene un campo
        /// </summary>
        /// <param name="fields">Lista campi</param>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Vero se trovato</returns>
        private static bool ContainsField(List<MetadataFieldDefinition> fields, string fieldKey)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Key == fieldKey)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Ordina una lista usando una label
        /// </summary>
        /// <typeparam name="T">Tipo valore</typeparam>
        /// <param name="items">Elementi da ordinare</param>
        /// <param name="labelSelector">Funzione label</param>
        private static void SortByLabel<T>(List<T> items, Func<T, string> labelSelector)
        {
            items.Sort((a, b) => string.Compare(labelSelector(a), labelSelector(b), StringComparison.CurrentCultureIgnoreCase));
        }

        /// <summary>
        /// Restituisce la label di un campo
        /// </summary>
        /// <param name="field">Campo</param>
        /// <returns>Label campo</returns>
        private static string GetFieldLabel(MetadataFieldDefinition field)
        {
            return field != null ? field.Label : "";
        }

        #endregion
    }
}
