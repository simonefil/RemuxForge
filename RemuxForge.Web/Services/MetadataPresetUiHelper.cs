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

            if (string.IsNullOrEmpty(rule.Description))
                rule.Description = "";
            if (rule.When == null)
                rule.When = new MkvMetadataRuleWhen();
            if (rule.When.All == null)
                rule.When.All = new List<MkvMetadataRuleConditionNode>();

            for (int i = 0; i < rule.When.All.Count; i++)
                EnsureNode(rule, rule.When.All[i], includeAdvancedFields);

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
        /// <param name="includeAdvancedFields">True per includere i campi avanzati</param>
        public static void EnsureNode(MkvMetadataRule rule, MkvMetadataRuleConditionNode node, bool includeAdvancedFields)
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
                EnsureFieldCondition(scope, node.Field, includeAdvancedFields);
            }
            else if (node.NodeType == MkvMetadataRuleConditionNodeType.TrackComparison)
            {
                if (node.TrackComparison == null)
                    node.TrackComparison = new MkvMetadataTrackComparisonCondition();

                node.Field = null;
                node.TrackGroupCount = null;
                node.Alternative = null;
                EnsureTrackComparisonCondition(scope, node.TrackComparison, includeAdvancedFields);
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
                    EnsureNode(rule, node.Alternative.Any[i], includeAdvancedFields);
            }
        }

        /// <summary>
        /// Normalizza una condizione su campo
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <param name="condition">Condizione da normalizzare</param>
        /// <param name="includeAdvancedFields">True per includere i campi avanzati</param>
        public static void EnsureFieldCondition(MkvMetadataTargetScope scope, MkvMetadataFieldCondition condition, bool includeAdvancedFields)
        {
            if (condition == null)
                return;

            if (string.IsNullOrEmpty(condition.FieldKey) || !ContainsField(GetReadableFieldsForCondition(scope, includeAdvancedFields), condition.FieldKey))
                condition.FieldKey = GetDefaultFieldConditionField(scope, includeAdvancedFields);

            if (string.IsNullOrEmpty(condition.Value))
                condition.Value = "";
            if (condition.Values == null)
                condition.Values = new List<string>();
            if (string.IsNullOrEmpty(condition.FromValue))
                condition.FromValue = "";
            if (string.IsNullOrEmpty(condition.ToValue))
                condition.ToValue = "";
            if (string.IsNullOrEmpty(condition.Unit))
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
        /// <param name="includeAdvancedFields">True per includere i campi avanzati</param>
        public static void EnsureTrackComparisonCondition(MkvMetadataTargetScope scope, MkvMetadataTrackComparisonCondition condition, bool includeAdvancedFields)
        {
            if (condition == null)
                return;

            if (string.IsNullOrEmpty(condition.FieldKey) || !ContainsField(GetTrackComparableFields(scope, includeAdvancedFields), condition.FieldKey))
                condition.FieldKey = GetDefaultTrackComparisonField(scope, includeAdvancedFields);

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

            if (string.IsNullOrEmpty(operation.FieldKey))
                operation.FieldKey = "";
            if (string.IsNullOrEmpty(operation.Value))
                operation.Value = "";
            if (string.IsNullOrEmpty(operation.TagKey))
                operation.TagKey = "";

            List<MetadataTagDefinition> tags = GetEditableTags(scope, operation, includeAdvancedFields);
            if ((operation.Type == MkvMetadataOperationType.SetTagField || operation.Type == MkvMetadataOperationType.ClearTagField) && !ContainsTag(tags, operation.TagKey))
                operation.TagKey = tags.Count > 0 ? tags[0].Name : "";

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
        /// <param name="includeAdvancedFields">True per includere i campi avanzati</param>
        /// <returns>Nodo condizione</returns>
        public static MkvMetadataRuleConditionNode CreateFieldConditionNode(MkvMetadataTargetScope scope, bool includeAdvancedFields)
        {
            MkvMetadataRuleConditionNode node = new MkvMetadataRuleConditionNode();
            node.NodeType = MkvMetadataRuleConditionNodeType.Field;
            node.Field = new MkvMetadataFieldCondition();
            node.Field.FieldKey = GetDefaultFieldConditionField(scope, includeAdvancedFields);
            EnsureFieldCondition(scope, node.Field, includeAdvancedFields);
            return node;
        }

        /// <summary>
        /// Crea una condizione confronto tracce con default coerenti allo scope
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <param name="includeAdvancedFields">True per includere i campi avanzati</param>
        /// <returns>Nodo condizione</returns>
        public static MkvMetadataRuleConditionNode CreateTrackComparisonNode(MkvMetadataTargetScope scope, bool includeAdvancedFields)
        {
            MkvMetadataRuleConditionNode node = new MkvMetadataRuleConditionNode();
            node.NodeType = MkvMetadataRuleConditionNodeType.TrackComparison;
            node.Field = null;
            node.TrackComparison = new MkvMetadataTrackComparisonCondition();
            node.TrackComparison.FieldKey = GetDefaultTrackComparisonField(scope, includeAdvancedFields);
            EnsureTrackComparisonCondition(scope, node.TrackComparison, includeAdvancedFields);
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
        /// <param name="includeAdvancedFields">True per includere i campi avanzati</param>
        /// <returns>Campi leggibili</returns>
        public static List<MetadataFieldDefinition> GetReadableFieldsForCondition(MkvMetadataTargetScope scope, bool includeAdvancedFields)
        {
            return MetadataUiCatalog.GetReadableFields(scope, includeAdvancedFields);
        }

        /// <summary>
        /// Restituisce i campi confrontabili tra tracce
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <param name="includeAdvancedFields">True per includere i campi avanzati</param>
        /// <returns>Campi confrontabili</returns>
        public static List<MetadataFieldDefinition> GetTrackComparableFields(MkvMetadataTargetScope scope, bool includeAdvancedFields)
        {
            return MetadataUiCatalog.GetTrackComparableFields(scope, includeAdvancedFields);
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
            MkvMetadataOperationType operationType = operation != null ? operation.Type : MkvMetadataOperationType.SetField;
            return MetadataUiCatalog.GetEditableFields(scope, operationType, includeAdvancedFields);
        }

        /// <summary>
        /// Restituisce gli operatori disponibili per un campo
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Operatori compatibili</returns>
        public static List<MkvMetadataConditionOperator> GetFieldOperatorOptions(string fieldKey)
        {
            List<MetadataConditionOperatorItem> catalog = MetadataUiCatalog.GetConditionOperatorCatalog(fieldKey);
            List<MkvMetadataConditionOperator> result = new List<MkvMetadataConditionOperator>();
            for (int i = 0; i < catalog.Count; i++)
            {
                result.Add(catalog[i].Operator);
            }

            SortByLabel(result, GetConditionOperatorLabel);
            return result;
        }

        /// <summary>
        /// Restituisce gli operatori numerici
        /// </summary>
        /// <param name="includeBetween">Indica se includere operatori range</param>
        /// <returns>Operatori numerici</returns>
        public static List<MkvMetadataConditionOperator> GetNumericOperatorOptions(bool includeBetween)
        {
            List<MkvMetadataConditionOperator> result = MetadataUiCatalog.GetNumericOperators(includeBetween);
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
            List<MkvMetadataTrackComparisonRelation> result = MetadataUiCatalog.GetTrackComparisonRelations(fieldKey);
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
        /// Restituisce i tag editabili per una operazione
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <param name="operation">Operazione corrente</param>
        /// <param name="includeAdvancedFields">Indica se includere tag avanzati</param>
        /// <returns>Tag editabili</returns>
        public static List<MetadataTagDefinition> GetEditableTags(MkvMetadataTargetScope scope, MkvMetadataOperation operation, bool includeAdvancedFields)
        {
            MkvMetadataTagTarget target = operation != null ? operation.TagTarget : MkvMetadataTagTarget.Current;
            return MetadataUiCatalog.GetEditableTagsForOperation(scope, target, includeAdvancedFields);
        }

        /// <summary>
        /// Restituisce schema input per un campo
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <param name="usage">Uso input</param>
        /// <returns>Schema input</returns>
        public static MetadataInputSchema GetFieldInputSchema(string fieldKey, MetadataCatalogInputUsage usage)
        {
            return MetadataUiCatalog.GetFieldInputSchema(fieldKey, usage);
        }

        /// <summary>
        /// Restituisce schema input per un tag
        /// </summary>
        /// <param name="tagKey">Chiave tag</param>
        /// <param name="usage">Uso input</param>
        /// <returns>Schema input</returns>
        public static MetadataInputSchema GetTagInputSchema(string tagKey, MetadataCatalogInputUsage usage)
        {
            return MetadataUiCatalog.GetTagInputSchema(tagKey, usage);
        }

        /// <summary>
        /// Indica se una condizione richiede un valore
        /// </summary>
        /// <param name="conditionOperator">Operatore condizione</param>
        /// <returns>Vero se serve un valore</returns>
        public static bool OperatorNeedsValue(MkvMetadataConditionOperator conditionOperator)
        {
            return MetadataUiCatalog.GetConditionOperatorInfo(conditionOperator).RequiresValue;
        }

        /// <summary>
        /// Indica se una condizione richiede un intervallo
        /// </summary>
        /// <param name="conditionOperator">Operatore condizione</param>
        /// <returns>Vero se serve un range</returns>
        public static bool OperatorNeedsRange(MkvMetadataConditionOperator conditionOperator)
        {
            return MetadataUiCatalog.GetConditionOperatorInfo(conditionOperator).RequiresRange;
        }

        /// <summary>
        /// Indica se una condizione richiede una lista
        /// </summary>
        /// <param name="conditionOperator">Operatore condizione</param>
        /// <returns>Vero se serve una lista</returns>
        public static bool OperatorNeedsList(MkvMetadataConditionOperator conditionOperator)
        {
            return MetadataUiCatalog.GetConditionOperatorInfo(conditionOperator).RequiresList;
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
        /// Restituisce il campo condizione predefinito per scope
        /// </summary>
        /// <param name="scope">Ambito regola</param>
        /// <param name="includeAdvancedFields">Indica se includere campi avanzati</param>
        /// <returns>Chiave campo</returns>
        private static string GetDefaultFieldConditionField(MkvMetadataTargetScope scope, bool includeAdvancedFields)
        {
            List<MetadataFieldDefinition> fields = GetReadableFieldsForCondition(scope, includeAdvancedFields);
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
        /// <param name="includeAdvancedFields">Indica se includere campi avanzati</param>
        /// <returns>Chiave campo</returns>
        private static string GetDefaultTrackComparisonField(MkvMetadataTargetScope scope, bool includeAdvancedFields)
        {
            List<MetadataFieldDefinition> fields = GetTrackComparableFields(scope, includeAdvancedFields);
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
        /// Verifica se una lista contiene un tag
        /// </summary>
        /// <param name="tags">Lista tag</param>
        /// <param name="tagName">Nome tag</param>
        /// <returns>Vero se trovato</returns>
        private static bool ContainsTag(List<MetadataTagDefinition> tags, string tagName)
        {
            for (int i = 0; i < tags.Count; i++)
            {
                if (string.Equals(tags[i].Name, tagName, StringComparison.OrdinalIgnoreCase))
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

        #endregion
    }
}
