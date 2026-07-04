using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Valutatore seriale regole Metadata
    /// </summary>
    public class MetadataPipelineEvaluator
    {
        #region Variabili di classe

        /// <summary>
        /// Motore espressioni usato da condizioni e operazioni
        /// </summary>
        private readonly MetadataExpressionEngine _expressionEngine;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataPipelineEvaluator()
        {
            this._expressionEngine = new MetadataExpressionEngine();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Analizza un record con un preset di regole
        /// </summary>
        /// <param name="record">Record metadata</param>
        /// <param name="preset">Preset regole</param>
        /// <param name="outputPolicy">Output policy runtime</param>
        public void AnalyzeRecord(MkvMetadataRecord record, MkvMetadataPreset preset, MkvMetadataOutputPolicy outputPolicy)
        {
            List<MkvMetadataTrackInfo> removedTracks = new List<MkvMetadataTrackInfo>();
            int matchCount = 0;

            // Riparte dallo snapshot originale per rendere deterministica ogni nuova analisi
            if (record.OriginalFileInfo != null && !string.IsNullOrEmpty(record.OriginalFileInfo.FilePath))
                record.FileInfo = MetadataModelCloner.CloneFileInfo(record.OriginalFileInfo);

            record.Changes.Clear();
            record.MatchCount = 0;
            record.ChangeCount = 0;
            record.ExecutionMode = MkvMetadataExecutionMode.NoOp;
            record.CommandPreview = "";

            if (preset == null || preset.Rules == null)
            {
                record.AnalysisStatus = MkvMetadataAnalysisStatus.Analyzed;
                return;
            }

            // Le regole sono seriali e ogni regola vede lo stato prodotto dalle precedenti
            for (int i = 0; i < preset.Rules.Count; i++)
            {
                MkvMetadataRule rule = preset.Rules[i];
                if (rule == null || !rule.Enabled)
                    continue;

                matchCount += this.EvaluateRule(record, rule, removedTracks);
            }

            record.MatchCount = matchCount;
            record.ChangeCount = record.Changes.Count;
            record.ExecutionMode = DetermineExecutionMode(record.Changes, outputPolicy);
            record.AnalysisStatus = MkvMetadataAnalysisStatus.Analyzed;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Valuta una regola su container o tracce compatibili
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="rule">Regola da valutare</param>
        /// <param name="removedTracks">Tracce rimosse da regole precedenti</param>
        /// <returns>Numero match prodotti dalla regola</returns>
        private int EvaluateRule(MkvMetadataRecord record, MkvMetadataRule rule, List<MkvMetadataTrackInfo> removedTracks)
        {
            int matches = 0;

            if (rule.TargetScope == MkvMetadataTargetScope.Container)
            {
                if (this.AreConditionsMatched(record, null, rule, removedTracks))
                {
                    matches++;
                    this.ApplyOperations(record, null, rule, removedTracks);
                }

                return matches;
            }

            for (int i = 0; i < record.FileInfo.Tracks.Count; i++)
            {
                MkvMetadataTrackInfo track = record.FileInfo.Tracks[i];
                if (removedTracks.Contains(track))
                    continue;

                if (MetadataScopeHelper.ScopeFromTrack(track) != rule.TargetScope)
                    continue;

                if (this.AreConditionsMatched(record, track, rule, removedTracks))
                {
                    matches++;
                    this.ApplyOperations(record, track, rule, removedTracks);
                }
            }

            return matches;
        }

        /// <summary>
        /// Verifica le condizioni root della regola in AND
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente o null per container</param>
        /// <param name="rule">Regola da valutare</param>
        /// <param name="removedTracks">Tracce rimosse da regole precedenti</param>
        /// <returns>True se tutte le condizioni root sono matchate</returns>
        private bool AreConditionsMatched(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataRule rule, List<MkvMetadataTrackInfo> removedTracks)
        {
            if (rule.When == null || rule.When.All == null || rule.When.All.Count == 0)
                return true;

            for (int i = 0; i < rule.When.All.Count; i++)
            {
                if (!this.IsConditionNodeMatched(record, track, rule.When.All[i], removedTracks))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Valuta un nodo condizione della mini-grammar metadata
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente o null per container</param>
        /// <param name="node">Nodo condizione</param>
        /// <param name="removedTracks">Tracce rimosse da regole precedenti</param>
        /// <returns>True se il nodo è matchato</returns>
        private bool IsConditionNodeMatched(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataRuleConditionNode node, List<MkvMetadataTrackInfo> removedTracks)
        {
            if (node == null)
                return true;

            if (node.NodeType == MkvMetadataRuleConditionNodeType.AlternativeAny)
                return this.IsAlternativeMatched(record, track, node.Alternative, removedTracks);

            if (node.NodeType == MkvMetadataRuleConditionNodeType.TrackComparison)
                return this.IsTrackComparisonMatched(record, track, node.TrackComparison, removedTracks);

            if (node.NodeType == MkvMetadataRuleConditionNodeType.TrackGroupCount)
                return this.IsTrackGroupCountMatched(record, track, node.TrackGroupCount, removedTracks);

            return this.IsFieldConditionMatched(record, track, node.Field);
        }

        /// <summary>
        /// Valuta un blocco OR alternativo
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente o null per container</param>
        /// <param name="block">Blocco alternativo</param>
        /// <param name="removedTracks">Tracce rimosse da regole precedenti</param>
        /// <returns>True se almeno un nodo interno è matchato</returns>
        private bool IsAlternativeMatched(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataAlternativeAnyBlock block, List<MkvMetadataTrackInfo> removedTracks)
        {
            if (block == null || block.Any == null || block.Any.Count == 0)
                return true;

            for (int i = 0; i < block.Any.Count; i++)
            {
                if (this.IsConditionNodeMatched(record, track, block.Any[i], removedTracks))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Valuta una condizione su campo metadata
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente o null per container</param>
        /// <param name="condition">Condizione campo</param>
        /// <returns>True se la condizione è matchata</returns>
        private bool IsFieldConditionMatched(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataFieldCondition condition)
        {
            string left;
            string right;
            string from;
            string to;
            double leftNumber;

            if (condition == null)
                return true;

            left = this.GetConditionFieldValue(record, track, condition.FieldKey);
            if (condition.Operator == MkvMetadataConditionOperator.InList)
                return this.IsInValueList(left, condition.Values, record, track);

            if (condition.Operator == MkvMetadataConditionOperator.NotInList)
                return !this.IsInValueList(left, condition.Values, record, track);

            if (condition.Operator == MkvMetadataConditionOperator.Between || condition.Operator == MkvMetadataConditionOperator.NotBetween)
            {
                leftNumber = MetadataValueNormalizer.ParseDoubleWithUnit(left);
                from = this.ResolveConditionScalar(condition.FromValue, condition.Unit, record, track);
                to = this.ResolveConditionScalar(condition.ToValue, condition.Unit, record, track);

                if (condition.Operator == MkvMetadataConditionOperator.Between)
                    return IsBetween(leftNumber, from, to);

                return !IsBetween(leftNumber, from, to);
            }

            right = this.ResolveConditionScalar(condition.Value, condition.Unit, record, track);
            return Compare(left, right, condition.Operator);
        }

        /// <summary>
        /// Valuta una condizione di confronto tra tracce dello stesso scope
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente</param>
        /// <param name="condition">Condizione confronto tracce</param>
        /// <param name="removedTracks">Tracce rimosse da regole precedenti</param>
        /// <returns>True se il confronto è matchato</returns>
        private bool IsTrackComparisonMatched(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataTrackComparisonCondition condition, List<MkvMetadataTrackInfo> removedTracks)
        {
            List<MkvMetadataTrackInfo> group;
            double currentValue;
            double otherValue;
            double boundaryValue = 0.0;
            string currentText;
            string otherText;
            bool boundaryFound = false;
            bool isLargest;
            bool isSmallest;
            int rank = 1;

            if (condition == null || track == null)
                return false;

            group = this.GetTrackGroup(record.FileInfo, track, condition.Group, removedTracks);
            currentValue = MetadataValueNormalizer.ParseDoubleWithUnit(this.GetFieldValue(record.FileInfo, track, condition.FieldKey));
            currentText = this.GetFieldValue(record.FileInfo, track, condition.FieldKey);

            isLargest = condition.Relation == MkvMetadataTrackComparisonRelation.Largest;
            isSmallest = condition.Relation == MkvMetadataTrackComparisonRelation.Smallest;
            if (isLargest || isSmallest)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    otherValue = MetadataValueNormalizer.ParseDoubleWithUnit(this.GetFieldValue(record.FileInfo, group[i], condition.FieldKey));
                    if (!boundaryFound || (isLargest && otherValue > boundaryValue) || (isSmallest && otherValue < boundaryValue))
                    {
                        boundaryValue = otherValue;
                        boundaryFound = true;
                    }
                }

                return boundaryFound && currentValue == boundaryValue;
            }

            if (condition.Relation == MkvMetadataTrackComparisonRelation.Rank)
            {
                for (int i = 0; i < group.Count; i++)
                {
                    otherValue = MetadataValueNormalizer.ParseDoubleWithUnit(this.GetFieldValue(record.FileInfo, group[i], condition.FieldKey));
                    if (otherValue > currentValue)
                        rank++;
                }

                return rank == condition.Rank;
            }

            for (int i = 0; i < group.Count; i++)
            {
                MkvMetadataTrackInfo other = group[i];
                if (other == track)
                    continue;

                otherText = this.GetFieldValue(record.FileInfo, other, condition.FieldKey);
                otherValue = MetadataValueNormalizer.ParseDoubleWithUnit(otherText);
                if (condition.Relation == MkvMetadataTrackComparisonRelation.EqualsAny && string.Equals(currentText, otherText, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (condition.Relation == MkvMetadataTrackComparisonRelation.NotEqualsAll && string.Equals(currentText, otherText, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (condition.Relation == MkvMetadataTrackComparisonRelation.GreaterThanAll && currentValue <= otherValue)
                    return false;

                if (condition.Relation == MkvMetadataTrackComparisonRelation.GreaterOrEqualAll && currentValue < otherValue)
                    return false;

                if (condition.Relation == MkvMetadataTrackComparisonRelation.LessThanAll && currentValue >= otherValue)
                    return false;

                if (condition.Relation == MkvMetadataTrackComparisonRelation.LessOrEqualAll && currentValue > otherValue)
                    return false;
            }

            return condition.Relation != MkvMetadataTrackComparisonRelation.EqualsAny;
        }

        /// <summary>
        /// Valuta una condizione sul conteggio delle tracce nel gruppo corrente
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente</param>
        /// <param name="condition">Condizione conteggio gruppo</param>
        /// <param name="removedTracks">Tracce rimosse da regole precedenti</param>
        /// <returns>True se il conteggio è matchato</returns>
        private bool IsTrackGroupCountMatched(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataTrackGroupCountCondition condition, List<MkvMetadataTrackInfo> removedTracks)
        {
            List<MkvMetadataTrackInfo> group;
            int count;

            if (condition == null || track == null)
                return false;

            group = this.GetTrackGroup(record.FileInfo, track, condition.Group, removedTracks);
            count = group.Count;
            return Compare(count.ToString(CultureInfo.InvariantCulture), condition.Value.ToString(CultureInfo.InvariantCulture), condition.Operator);
        }

        /// <summary>
        /// Risolve valore condizione applicando template e unità selezionata
        /// </summary>
        /// <param name="value">Valore condizione</param>
        /// <param name="unit">Unità opzionale</param>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente o null per container</param>
        /// <returns>Valore scalare pronto per il confronto</returns>
        private string ResolveConditionScalar(string value, string unit, MkvMetadataRecord record, MkvMetadataTrackInfo track)
        {
            string text = value != null ? value : "";
            string resolved = text;
            string suffix = unit != null ? unit.Trim() : "";

            if (text.IndexOf('[', StringComparison.Ordinal) >= 0 || text.IndexOf('{', StringComparison.Ordinal) >= 0)
                resolved = this._expressionEngine.Evaluate(text, record.FileInfo, track, record.OriginalFileInfo, this.FindOriginalTrack(record, track));

            if (!string.IsNullOrEmpty(resolved.Trim()) && !string.IsNullOrEmpty(suffix) && !MetadataValueNormalizer.HasExplicitUnit(resolved))
                return resolved + " " + suffix;

            return resolved;
        }

        /// <summary>
        /// Verifica se un valore è presente nella lista della condizione
        /// </summary>
        /// <param name="left">Valore corrente</param>
        /// <param name="values">Valori candidati</param>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente o null per container</param>
        /// <returns>True se il valore corrente è nella lista</returns>
        private bool IsInValueList(string left, List<string> values, MkvMetadataRecord record, MkvMetadataTrackInfo track)
        {
            if (values == null)
                return false;

            for (int i = 0; i < values.Count; i++)
            {
                string resolved = this.ResolveConditionScalar(values[i], "", record, track);
                if (string.Equals(left != null ? left.Trim() : "", resolved.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Costruisce il gruppo tracce confrontabile con la traccia corrente
        /// </summary>
        /// <param name="fileInfo">Info file metadata</param>
        /// <param name="current">Traccia corrente</param>
        /// <param name="group">Tipo gruppo richiesto</param>
        /// <param name="removedTracks">Tracce rimosse da regole precedenti</param>
        /// <returns>Tracce del gruppo, inclusa la traccia corrente</returns>
        private List<MkvMetadataTrackInfo> GetTrackGroup(MkvMetadataFileInfo fileInfo, MkvMetadataTrackInfo current, MkvMetadataTrackGroup group, List<MkvMetadataTrackInfo> removedTracks)
        {
            List<MkvMetadataTrackInfo> result = new List<MkvMetadataTrackInfo>();
            if (fileInfo == null || current == null)
                return result;

            for (int i = 0; i < fileInfo.Tracks.Count; i++)
            {
                MkvMetadataTrackInfo other = fileInfo.Tracks[i];
                if (removedTracks.Contains(other))
                    continue;

                if (IsTrackGroupCandidate(current, other, group))
                    result.Add(other);
            }

            return result;
        }

        /// <summary>
        /// Applica tutte le operazioni THEN della regola matchata
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente o null per container</param>
        /// <param name="rule">Regola matchata</param>
        /// <param name="removedTracks">Tracce rimosse da regole precedenti</param>
        private void ApplyOperations(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataRule rule, List<MkvMetadataTrackInfo> removedTracks)
        {
            if (rule.Operations == null)
                return;

            for (int i = 0; i < rule.Operations.Count; i++)
            {
                MkvMetadataOperation operation = rule.Operations[i];
                if (operation.Type == MkvMetadataOperationType.SetField)
                {
                    this.ApplySetField(record, track, rule, operation);
                }
                else if (operation.Type == MkvMetadataOperationType.ClearField)
                {
                    this.ApplyClearField(record, track, rule, operation);
                }
                else if (operation.Type == MkvMetadataOperationType.SetExclusiveFlag && track != null)
                {
                    this.ApplySetExclusiveFlag(record, track, rule, operation);
                }
                else if (operation.Type == MkvMetadataOperationType.RemoveTrack && track != null)
                {
                    this.ApplyRemoveTrack(record, track, rule, removedTracks);
                }
                else if (operation.Type == MkvMetadataOperationType.SetTagField || operation.Type == MkvMetadataOperationType.ClearTagField || operation.Type == MkvMetadataOperationType.ClearTags)
                {
                    this.ApplyTagOperation(record, track, rule, operation, removedTracks);
                }
                else if (operation.Type == MkvMetadataOperationType.AddOrUpdateTrackStatisticsTags || operation.Type == MkvMetadataOperationType.DeleteTrackStatisticsTags)
                {
                    this.ApplyStatisticsTagOperation(record, rule, operation);
                }
            }
        }

        /// <summary>
        /// Applica un set/replace campo metadata
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente o null per container</param>
        /// <param name="rule">Regola matchata</param>
        /// <param name="operation">Operazione set field</param>
        private void ApplySetField(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataRule rule, MkvMetadataOperation operation)
        {
            MetadataFieldDefinition field;
            string errorMessage;
            string before;
            string after;

            if (!MetadataFieldRegistry.TryGet(operation.FieldKey, out field) ||
                MetadataFieldRegistry.IsBlockedForWrite(operation.FieldKey) ||
                !MetadataFieldRegistry.IsScopeCompatible(operation.FieldKey, MetadataScopeHelper.ScopeFromTrack(track)))
            {
                throw new InvalidOperationException(AppText.F("metadata.error.fieldNotWritable", operation.FieldKey));
            }

            before = this.GetFieldValue(record.FileInfo, track, operation.FieldKey);
            after = this._expressionEngine.Evaluate(operation.Value, record.FileInfo, track, record.OriginalFileInfo, this.FindOriginalTrack(record, track));
            if (!MetadataFieldRegistry.ValidateWritableValue(operation.FieldKey, after, field.IsClearable, out after, out errorMessage))
                throw new InvalidOperationException(errorMessage);

            if (before == after)
                return;

            this.SetFieldValue(record.FileInfo, track, operation.FieldKey, after);
            record.Changes.Add(CreateChange(rule, operation, field, track, before, after, false));
        }

        /// <summary>
        /// Applica una cancellazione campo metadata
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente o null per container</param>
        /// <param name="rule">Regola matchata</param>
        /// <param name="operation">Operazione clear field</param>
        private void ApplyClearField(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataRule rule, MkvMetadataOperation operation)
        {
            MetadataFieldDefinition field;
            string before;

            if (!MetadataFieldRegistry.TryGet(operation.FieldKey, out field) ||
                MetadataFieldRegistry.IsBlockedForWrite(operation.FieldKey) ||
                !MetadataFieldRegistry.IsScopeCompatible(operation.FieldKey, MetadataScopeHelper.ScopeFromTrack(track)) ||
                !field.IsClearable)
            {
                throw new InvalidOperationException(AppText.F("metadata.error.fieldNotClearable", operation.FieldKey));
            }

            before = this.GetFieldValue(record.FileInfo, track, operation.FieldKey);
            if (string.IsNullOrEmpty(before))
                return;

            this.SetFieldValue(record.FileInfo, track, operation.FieldKey, "");
            record.Changes.Add(CreateChange(rule, operation, field, track, before, "", false));
        }

        /// <summary>
        /// Imposta un flag booleano esclusivo e spegne le tracce concorrenti nel gruppo
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente</param>
        /// <param name="rule">Regola matchata</param>
        /// <param name="operation">Operazione flag esclusivo</param>
        private void ApplySetExclusiveFlag(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataRule rule, MkvMetadataOperation operation)
        {
            MetadataFieldDefinition field;

            if (!MetadataFieldRegistry.TryGet(operation.FieldKey, out field) ||
                MetadataFieldRegistry.IsBlockedForWrite(operation.FieldKey) ||
                !MetadataFieldRegistry.IsScopeCompatible(operation.FieldKey, MetadataScopeHelper.ScopeFromTrack(track)) ||
                field.ValueType != MetadataFieldValueType.Boolean)
            {
                throw new InvalidOperationException(AppText.F("metadata.error.invalidExclusiveFlagField", operation.FieldKey));
            }

            this.ApplyBooleanFieldChange(record, track, rule, operation, field, "1");

            for (int i = 0; i < record.FileInfo.Tracks.Count; i++)
            {
                MkvMetadataTrackInfo other = record.FileInfo.Tracks[i];
                if (other == track)
                    continue;

                if (!IsExclusiveGroupCandidate(track, other, operation.ExclusiveGroup))
                    continue;

                this.ApplyBooleanFieldChange(record, other, rule, operation, field, "0");
            }
        }

        /// <summary>
        /// Applica una modifica a campo booleano normalizzando il formato MKV
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia target</param>
        /// <param name="rule">Regola matchata</param>
        /// <param name="operation">Operazione originale</param>
        /// <param name="field">Definizione campo</param>
        /// <param name="value">Valore booleano target</param>
        private void ApplyBooleanFieldChange(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataRule rule, MkvMetadataOperation operation, MetadataFieldDefinition field, string value)
        {
            string before = MetadataValueNormalizer.NormalizeBoolean(this.GetFieldValue(record.FileInfo, track, operation.FieldKey));
            string after = MetadataValueNormalizer.NormalizeBoolean(value);
            if (before == after)
                return;

            this.SetFieldValue(record.FileInfo, track, operation.FieldKey, after);
            record.Changes.Add(CreateChange(rule, operation, field, track, before, after, false));
        }

        /// <summary>
        /// Aggiunge una sola operazione statistics tag per record
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="rule">Regola matchata</param>
        /// <param name="operation">Operazione statistics tag</param>
        private void ApplyStatisticsTagOperation(MkvMetadataRecord record, MkvMetadataRule rule, MkvMetadataOperation operation)
        {
            for (int i = 0; i < record.Changes.Count; i++)
            {
                if (record.Changes[i].OperationType == operation.Type)
                    return;
            }

            MkvMetadataChange change = new MkvMetadataChange();
            change.RuleDescription = rule.Description;
            change.Scope = MkvMetadataTargetScope.Container;
            change.OperationType = operation.Type;
            change.RequiresRemux = false;
            change.Message = operation.Type == MkvMetadataOperationType.AddOrUpdateTrackStatisticsTags
                ? AppText.T("metadata.change.addUpdateStatistics")
                : AppText.T("metadata.change.deleteStatistics");
            record.Changes.Add(change);
        }

        /// <summary>
        /// Applica operazioni sui tag gestiti dalla UI
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente o null per container</param>
        /// <param name="rule">Regola matchata</param>
        /// <param name="operation">Operazione tag</param>
        /// <param name="removedTracks">Tracce rimosse da regole precedenti</param>
        private void ApplyTagOperation(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataRule rule, MkvMetadataOperation operation, List<MkvMetadataTrackInfo> removedTracks)
        {
            string tagKey = operation.TagKey != null ? operation.TagKey.Trim() : "";
            string after = "";
            List<MkvMetadataTrackInfo> targetTracks;

            if (operation.Type != MkvMetadataOperationType.ClearTags && string.IsNullOrEmpty(tagKey))
                throw new InvalidOperationException(AppText.T("metadata.error.tagKeyRequired"));

            if (operation.Type == MkvMetadataOperationType.ClearTags && !operation.ClearTagsConfirmed)
                throw new InvalidOperationException(AppText.T("metadata.error.clearTagsNeedsConfirmation"));

            if (operation.Type == MkvMetadataOperationType.SetTagField)
                after = this._expressionEngine.Evaluate(operation.Value, record.FileInfo, track, record.OriginalFileInfo, this.FindOriginalTrack(record, track));

            if (operation.TagTarget == MkvMetadataTagTarget.File || (operation.TagTarget == MkvMetadataTagTarget.Current && track == null))
            {
                AddContainerTagChange(record, rule, operation, tagKey, after);
                return;
            }

            targetTracks = new List<MkvMetadataTrackInfo>();
            if (operation.TagTarget == MkvMetadataTagTarget.AllTracks)
            {
                for (int i = 0; i < record.FileInfo.Tracks.Count; i++)
                {
                    if (!removedTracks.Contains(record.FileInfo.Tracks[i]))
                        targetTracks.Add(record.FileInfo.Tracks[i]);
                }
            }
            else if (track != null)
            {
                targetTracks.Add(track);
            }
            else
            {
                AddContainerTagChange(record, rule, operation, tagKey, after);
                return;
            }

            for (int i = 0; i < targetTracks.Count; i++)
            {
                if (operation.Type == MkvMetadataOperationType.ClearTags)
                {
                    AddClearTagsPreviewChanges(record, targetTracks[i], rule, operation);
                }
                else
                {
                    record.Changes.Add(CreateTagChange(record, targetTracks[i], rule, operation, tagKey, after));
                }
            }
        }

        /// <summary>
        /// Aggiunge modifica tag a livello container
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="rule">Regola matchata</param>
        /// <param name="operation">Operazione tag</param>
        /// <param name="tagKey">Chiave tag</param>
        /// <param name="after">Valore dopo</param>
        private static void AddContainerTagChange(MkvMetadataRecord record, MkvMetadataRule rule, MkvMetadataOperation operation, string tagKey, string after)
        {
            if (operation.Type == MkvMetadataOperationType.ClearTags)
                AddClearTagsPreviewChanges(record, null, rule, operation);
            else
                record.Changes.Add(CreateTagChange(record, null, rule, operation, tagKey, after));
        }

        /// <summary>
        /// Crea preview di cancellazione per tutti i tag gestiti dalla UI
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia target o null per container</param>
        /// <param name="rule">Regola matchata</param>
        /// <param name="operation">Operazione clear tags</param>
        private static void AddClearTagsPreviewChanges(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataRule rule, MkvMetadataOperation operation)
        {
            Dictionary<string, string> tags = track != null ? track.Tags : record.FileInfo.Tags;
            List<string> keys = new List<string>();

            foreach (KeyValuePair<string, string> pair in tags)
            {
                if (MetadataTagRegistry.IsAllowed(pair.Key))
                    keys.Add(pair.Key);
            }

            for (int i = 0; i < keys.Count; i++)
            {
                MkvMetadataOperation clearOperation = new MkvMetadataOperation();
                clearOperation.Type = MkvMetadataOperationType.ClearTagField;
                clearOperation.TagTarget = operation.TagTarget;
                clearOperation.TagKey = keys[i];
                record.Changes.Add(CreateTagChange(record, track, rule, clearOperation, keys[i], ""));
            }
        }

        /// <summary>
        /// Crea una modifica preview per un tag gestito
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia target o null per container</param>
        /// <param name="rule">Regola matchata</param>
        /// <param name="operation">Operazione tag</param>
        /// <param name="tagKey">Chiave tag</param>
        /// <param name="after">Valore dopo</param>
        /// <returns>Modifica tag</returns>
        private static MkvMetadataChange CreateTagChange(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataRule rule, MkvMetadataOperation operation, string tagKey, string after)
        {
            MkvMetadataChange change = new MkvMetadataChange();
            MetadataTagDefinition tag;
            string errorMessage;
            string before = FindCurrentTagValue(record, track, tagKey);
            MkvMetadataTargetScope scope = MetadataScopeHelper.ScopeFromTrack(track);

            if (operation.Type == MkvMetadataOperationType.SetTagField)
            {
                if (!MetadataTagRegistry.TryGet(tagKey, out tag))
                    throw new InvalidOperationException(AppText.F("metadata.validation.tagNotWritable", tagKey));

                if (!MetadataTagRegistry.ValidateWritableValue(tagKey, scope, after, tag.IsClearable, out after, out errorMessage))
                    throw new InvalidOperationException(errorMessage);
            }
            else if (operation.Type == MkvMetadataOperationType.ClearTagField && !MetadataTagRegistry.ValidateWritable(tagKey, scope, out errorMessage))
            {
                throw new InvalidOperationException(errorMessage);
            }

            change.RuleDescription = rule.Description;
            change.Scope = scope;
            change.TrackSelector = track != null ? track.TrackSelector : "";
            change.TrackKind = track != null ? track.TrackKind : "";
            change.TrackUniqueId = track != null ? track.TrackUniqueId : "";
            change.FieldKey = tagKey;
            change.BeforeValue = before;
            change.AfterValue = after;
            change.OperationType = operation.Type;
            change.RequiresRemux = false;
            ApplyTagStateChange(record, track, operation, tagKey, after);

            if (operation.Type == MkvMetadataOperationType.SetTagField)
            {
                change.Message = AppText.F("metadata.change.tagSet", tagKey, after);
            }
            else if (operation.Type == MkvMetadataOperationType.ClearTagField)
            {
                change.Message = AppText.F("metadata.change.tagClear", tagKey);
            }
            else
            {
                change.Message = track != null ? AppText.F("metadata.change.clearManagedTrackTags", track.TrackSelector) : AppText.T("metadata.change.clearManagedFileTags");
            }

            return change;
        }

        /// <summary>
        /// Cerca il valore tag corrente prima della modifica preview
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia target o null per container</param>
        /// <param name="tagKey">Chiave tag</param>
        /// <returns>Valore tag corrente o stringa vuota</returns>
        private static string FindCurrentTagValue(MkvMetadataRecord record, MkvMetadataTrackInfo track, string tagKey)
        {
            Dictionary<string, string> tags;
            string value;
            string key = tagKey != null ? tagKey.Trim().ToUpperInvariant() : "";

            if (string.IsNullOrEmpty(key))
                return "";

            tags = track != null ? track.Tags : record != null && record.FileInfo != null ? record.FileInfo.Tags : null;
            if (tags != null && tags.TryGetValue(key, out value))
                return value != null ? value : "";

            return "";
        }

        /// <summary>
        /// Aggiorna lo stato tag in memoria per le regole successive
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia target o null per container</param>
        /// <param name="operation">Operazione tag</param>
        /// <param name="tagKey">Chiave tag</param>
        /// <param name="after">Valore dopo</param>
        private static void ApplyTagStateChange(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataOperation operation, string tagKey, string after)
        {
            Dictionary<string, string> tags = track != null ? track.Tags : record.FileInfo.Tags;
            string key = tagKey != null ? tagKey.Trim().ToUpperInvariant() : "";

            if (operation.Type == MkvMetadataOperationType.SetTagField && !string.IsNullOrEmpty(key))
            {
                tags[key] = after != null ? after : "";
            }
            else if (operation.Type == MkvMetadataOperationType.ClearTagField && !string.IsNullOrEmpty(key))
            {
                tags.Remove(key);
            }
            else if (operation.Type == MkvMetadataOperationType.ClearTags)
            {
                RemoveManagedTagState(tags);
            }
        }

        /// <summary>
        /// Rimuove dallo stato in memoria tutti i tag gestiti dalla UI
        /// </summary>
        /// <param name="tags">Dizionario tag target</param>
        private static void RemoveManagedTagState(Dictionary<string, string> tags)
        {
            List<string> managed = MetadataTagRegistry.GetEditableTagNames();
            for (int i = 0; i < managed.Count; i++)
            {
                tags.Remove(managed[i].ToUpperInvariant());
            }
        }

        /// <summary>
        /// Segna una traccia come rimossa nella simulazione pipeline
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia da rimuovere</param>
        /// <param name="rule">Regola matchata</param>
        /// <param name="removedTracks">Tracce rimosse da regole precedenti</param>
        private void ApplyRemoveTrack(MkvMetadataRecord record, MkvMetadataTrackInfo track, MkvMetadataRule rule, List<MkvMetadataTrackInfo> removedTracks)
        {
            if (!removedTracks.Contains(track))
                removedTracks.Add(track);

            MkvMetadataChange change = new MkvMetadataChange();
            change.RuleDescription = rule.Description;
            change.Scope = MetadataScopeHelper.ScopeFromTrack(track);
            change.TrackSelector = track.TrackSelector;
            change.TrackKind = track.TrackKind;
            change.TrackUniqueId = track.TrackUniqueId;
            change.OperationType = MkvMetadataOperationType.RemoveTrack;
            change.RequiresRemux = true;
            change.Message = AppText.F("metadata.change.removeTrack", track.TrackSelector);
            record.Changes.Add(change);
        }

        /// <summary>
        /// Legge un campo metadata da traccia, container o token expression
        /// </summary>
        /// <param name="fileInfo">Info file metadata</param>
        /// <param name="track">Traccia corrente o null per container</param>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Valore campo o stringa vuota</returns>
        private string GetFieldValue(MkvMetadataFileInfo fileInfo, MkvMetadataTrackInfo track, string fieldKey)
        {
            string result;
            string key = fieldKey != null ? fieldKey.Trim() : "";

            if (track != null && track.Fields.TryGetValue(key, out result))
            {
                return result != null ? result : "";
            }

            if (fileInfo != null && fileInfo.Fields.TryGetValue(key, out result))
            {
                return result != null ? result : "";
            }

            return this._expressionEngine.ResolveToken(key, fileInfo, track);
        }

        /// <summary>
        /// Legge un campo condizione supportando prefissi original/current/mi
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente o null per container</param>
        /// <param name="fieldKey">Chiave campo condizione</param>
        /// <returns>Valore campo condizione</returns>
        private string GetConditionFieldValue(MkvMetadataRecord record, MkvMetadataTrackInfo track, string fieldKey)
        {
            string key = fieldKey != null ? fieldKey.Trim() : "";
            if (key.StartsWith("original.", StringComparison.OrdinalIgnoreCase) || key.StartsWith("current.", StringComparison.OrdinalIgnoreCase) || key.StartsWith("mi.", StringComparison.OrdinalIgnoreCase))
            {
                return this._expressionEngine.ResolveToken(key, record.FileInfo, track, record.OriginalFileInfo, this.FindOriginalTrack(record, track));
            }

            return this.GetFieldValue(record.FileInfo, track, key);
        }

        /// <summary>
        /// Scrive un campo metadata aggiornando anche proprietà denormalizzate usate dalla UI
        /// </summary>
        /// <param name="fileInfo">Info file metadata</param>
        /// <param name="track">Traccia target o null per container</param>
        /// <param name="fieldKey">Chiave campo</param>
        /// <param name="value">Valore nuovo</param>
        private void SetFieldValue(MkvMetadataFileInfo fileInfo, MkvMetadataTrackInfo track, string fieldKey, string value)
        {
            if (track != null)
            {
                track.Fields[fieldKey] = value;
                if (fieldKey.EndsWith("_title", StringComparison.OrdinalIgnoreCase))
                    track.Title = value;
                else if (fieldKey.EndsWith("_language", StringComparison.OrdinalIgnoreCase))
                    track.Language = value;
                else if (fieldKey.EndsWith("_language_ietf", StringComparison.OrdinalIgnoreCase))
                    track.LanguageIetf = value;

                return;
            }

            fileInfo.Fields[fieldKey] = value;
            if (fieldKey == "container_title")
                fileInfo.ContainerTitle = value;
        }

        /// <summary>
        /// Crea una modifica campo metadata
        /// </summary>
        /// <param name="rule">Regola matchata</param>
        /// <param name="operation">Operazione applicata</param>
        /// <param name="field">Definizione campo</param>
        /// <param name="track">Traccia target o null per container</param>
        /// <param name="before">Valore prima</param>
        /// <param name="after">Valore dopo</param>
        /// <param name="requiresRemux">True se l'operazione richiede remux</param>
        /// <returns>Modifica campo</returns>
        private static MkvMetadataChange CreateChange(MkvMetadataRule rule, MkvMetadataOperation operation, MetadataFieldDefinition field, MkvMetadataTrackInfo track, string before, string after, bool requiresRemux)
        {
            MkvMetadataChange change = new MkvMetadataChange();
            change.RuleDescription = rule.Description;
            change.Scope = MetadataScopeHelper.ScopeFromTrack(track);
            change.TrackSelector = track != null ? track.TrackSelector : "";
            change.TrackKind = track != null ? track.TrackKind : "";
            change.TrackUniqueId = track != null ? track.TrackUniqueId : "";
            change.FieldKey = operation.FieldKey;
            change.MkvPropEditProperty = field.MkvPropEditProperty;
            change.BeforeValue = before != null ? before : "";
            change.AfterValue = after != null ? after : "";
            change.OperationType = operation.Type;
            change.RequiresRemux = requiresRemux || field.RequiresRemux;
            change.Message = AppText.F("metadata.change.field", field.Label, change.BeforeValue, change.AfterValue);
            return change;
        }

        /// <summary>
        /// Determina il motore necessario per applicare le modifiche
        /// </summary>
        /// <param name="changes">Modifiche prodotte dall'analisi</param>
        /// <param name="outputPolicy">Policy output runtime</param>
        /// <returns>Modalità esecuzione metadata</returns>
        private static MkvMetadataExecutionMode DetermineExecutionMode(List<MkvMetadataChange> changes, MkvMetadataOutputPolicy outputPolicy)
        {
            bool requiresRemux = false;
            if (changes == null || changes.Count == 0)
                return MkvMetadataExecutionMode.NoOp;

            for (int i = 0; i < changes.Count; i++)
            {
                if (changes[i].RequiresRemux)
                {
                    requiresRemux = true;
                    break;
                }
            }

            if (requiresRemux)
                return MkvMetadataExecutionMode.MkvMerge;

            return outputPolicy == MkvMetadataOutputPolicy.OutputPath ? MkvMetadataExecutionMode.CopyPropEdit : MkvMetadataExecutionMode.PropEdit;
        }

        /// <summary>
        /// Confronta due valori con l'operatore metadata richiesto
        /// </summary>
        /// <param name="left">Valore sinistro</param>
        /// <param name="right">Valore destro</param>
        /// <param name="op">Operatore condizione</param>
        /// <returns>True se il confronto è soddisfatto</returns>
        private static bool Compare(string left, string right, MkvMetadataConditionOperator op)
        {
            if (op == MkvMetadataConditionOperator.IsEmpty)
                return string.IsNullOrEmpty(left != null ? left.Trim() : null);

            if (op == MkvMetadataConditionOperator.IsNotEmpty)
                return !string.IsNullOrEmpty(left != null ? left.Trim() : null);

            left = left != null ? left : "";
            right = right != null ? right : "";

            if (op == MkvMetadataConditionOperator.Equals)
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            if (op == MkvMetadataConditionOperator.NotEquals)
                return !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            if (op == MkvMetadataConditionOperator.Contains)
                return left.IndexOf(right, StringComparison.OrdinalIgnoreCase) >= 0;
            if (op == MkvMetadataConditionOperator.NotContains)
                return left.IndexOf(right, StringComparison.OrdinalIgnoreCase) < 0;
            if (op == MkvMetadataConditionOperator.StartsWith)
                return left.StartsWith(right, StringComparison.OrdinalIgnoreCase);
            if (op == MkvMetadataConditionOperator.EndsWith)
                return left.EndsWith(right, StringComparison.OrdinalIgnoreCase);
            if (op == MkvMetadataConditionOperator.Regex)
                return IsRegexMatch(left, right);
            if (op == MkvMetadataConditionOperator.NotRegex)
                return !IsRegexMatch(left, right);
            if (op == MkvMetadataConditionOperator.IsTrue)
                return MetadataValueNormalizer.IsTruthy(left);

            if (op == MkvMetadataConditionOperator.IsFalse)
                return !MetadataValueNormalizer.IsTruthy(left);

            double leftNumber = MetadataValueNormalizer.ParseDoubleWithUnit(left);
            double rightNumber = MetadataValueNormalizer.ParseDoubleWithUnit(right);
            if (op == MkvMetadataConditionOperator.GreaterThan)
                return leftNumber > rightNumber;
            if (op == MkvMetadataConditionOperator.GreaterOrEqual)
                return leftNumber >= rightNumber;
            if (op == MkvMetadataConditionOperator.LessThan)
                return leftNumber < rightNumber;
            if (op == MkvMetadataConditionOperator.LessOrEqual)
                return leftNumber <= rightNumber;

            return false;
        }

        /// <summary>
        /// Verifica inclusione numerica tra minimo e massimo normalizzati
        /// </summary>
        /// <param name="left">Valore corrente già normalizzato</param>
        /// <param name="minText">Valore minimo testuale</param>
        /// <param name="maxText">Valore massimo testuale</param>
        /// <returns>True se il valore è dentro il range</returns>
        private static bool IsBetween(double left, string minText, string maxText)
        {
            double min = MetadataValueNormalizer.ParseDoubleWithUnit(minText);
            double max = MetadataValueNormalizer.ParseDoubleWithUnit(maxText);
            if (min > max)
            {
                (min, max) = (max, min);
            }

            return left >= min && left <= max;
        }

        /// <summary>
        /// Verifica se una traccia appartiene al gruppo confronto della traccia corrente
        /// </summary>
        /// <param name="current">Traccia corrente</param>
        /// <param name="other">Traccia candidata</param>
        /// <param name="group">Gruppo confronto</param>
        /// <returns>True se la traccia candidata appartiene al gruppo</returns>
        private static bool IsTrackGroupCandidate(MkvMetadataTrackInfo current, MkvMetadataTrackInfo other, MkvMetadataTrackGroup group)
        {
            bool sameLanguage;
            bool sameFormat;

            if (!string.Equals(current.TrackKind, other.TrackKind, StringComparison.OrdinalIgnoreCase))
                return false;

            sameLanguage = string.Equals(current.Language, other.Language, StringComparison.OrdinalIgnoreCase);
            sameFormat = string.Equals(current.Format, other.Format, StringComparison.OrdinalIgnoreCase);

            if (group == MkvMetadataTrackGroup.AllInScope)
                return true;

            if (group == MkvMetadataTrackGroup.SameLanguage)
                return sameLanguage;

            if (group == MkvMetadataTrackGroup.SameFormat)
                return sameFormat;

            if (group == MkvMetadataTrackGroup.SameLanguageAndFormat)
                return sameLanguage && sameFormat;

            return true;
        }

        /// <summary>
        /// Verifica se una traccia appartiene al gruppo esclusivo della traccia corrente
        /// </summary>
        /// <param name="current">Traccia corrente</param>
        /// <param name="other">Traccia candidata</param>
        /// <param name="group">Gruppo esclusività</param>
        /// <returns>True se la traccia candidata deve essere aggiornata dal flag esclusivo</returns>
        private static bool IsExclusiveGroupCandidate(MkvMetadataTrackInfo current, MkvMetadataTrackInfo other, MkvMetadataExclusiveGroup group)
        {
            MkvMetadataTrackGroup trackGroup = MkvMetadataTrackGroup.AllInScope;

            if (group == MkvMetadataExclusiveGroup.SameLanguage)
                trackGroup = MkvMetadataTrackGroup.SameLanguage;
            else if (group == MkvMetadataExclusiveGroup.SameFormat)
                trackGroup = MkvMetadataTrackGroup.SameFormat;
            else if (group == MkvMetadataExclusiveGroup.SameLanguageAndFormat)
                trackGroup = MkvMetadataTrackGroup.SameLanguageAndFormat;

            return IsTrackGroupCandidate(current, other, trackGroup);
        }

        /// <summary>
        /// Trova nello snapshot originale la traccia corrispondente alla traccia corrente
        /// </summary>
        /// <param name="record">Record metadata corrente</param>
        /// <param name="track">Traccia corrente</param>
        /// <returns>Traccia originale corrispondente o null</returns>
        private MkvMetadataTrackInfo FindOriginalTrack(MkvMetadataRecord record, MkvMetadataTrackInfo track)
        {
            MkvMetadataTrackInfo fallback = null;

            if (record == null || record.OriginalFileInfo == null || track == null)
                return null;

            for (int i = 0; i < record.OriginalFileInfo.Tracks.Count; i++)
            {
                MkvMetadataTrackInfo candidate = record.OriginalFileInfo.Tracks[i];
                if (string.Equals(candidate.TrackSelector, track.TrackSelector, StringComparison.OrdinalIgnoreCase))
                    return candidate;

                if (fallback == null && string.Equals(candidate.TrackKind, track.TrackKind, StringComparison.OrdinalIgnoreCase) && candidate.TypeIndex == track.TypeIndex)
                    fallback = candidate;
            }

            return fallback;
        }

        /// <summary>
        /// Esegue match regex ignorando pattern invalidi
        /// </summary>
        /// <param name="left">Valore corrente</param>
        /// <param name="pattern">Pattern regex</param>
        /// <returns>True se la regex è valida e matcha</returns>
        private static bool IsRegexMatch(string left, string pattern)
        {
            try
            {
                return Regex.IsMatch(left, pattern, RegexOptions.IgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        #endregion
    }
}
