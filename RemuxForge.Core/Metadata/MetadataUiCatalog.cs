using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Catalogo UI-ready per metadata, input, tag, token, funzioni e operatori
    /// </summary>
    public static class MetadataUiCatalog
    {
        #region Metodi pubblici

        /// <summary>
        /// Restituisce i campi leggibili per condizioni e token
        /// </summary>
        /// <param name="scope">Scope regola</param>
        /// <param name="includeAdvanced">Vero per includere campi avanzati</param>
        /// <returns>Campi leggibili</returns>
        public static List<MetadataFieldDefinition> GetReadableFields(MkvMetadataTargetScope scope, bool includeAdvanced)
        {
            List<MetadataFieldDefinition> result = MetadataFieldRegistry.GetReadable(scope, includeAdvanced, false);
            result.Sort(CompareFields);
            return result;
        }

        /// <summary>
        /// Restituisce i campi editabili per una operazione
        /// </summary>
        /// <param name="scope">Scope regola</param>
        /// <param name="operationType">Tipo operazione</param>
        /// <param name="includeAdvanced">Vero per includere campi avanzati</param>
        /// <returns>Campi editabili</returns>
        public static List<MetadataFieldDefinition> GetEditableFields(MkvMetadataTargetScope scope, MkvMetadataOperationType operationType, bool includeAdvanced)
        {
            List<MetadataFieldDefinition> fields = MetadataFieldRegistry.GetEditable(scope, includeAdvanced);
            List<MetadataFieldDefinition> result = new List<MetadataFieldDefinition>();
            for (int i = 0; i < fields.Count; i++)
            {
                if (operationType == MkvMetadataOperationType.ClearField && !fields[i].IsClearable)
                    continue;

                if (operationType == MkvMetadataOperationType.SetExclusiveFlag && fields[i].ValueType != MetadataFieldValueType.Boolean)
                    continue;

                result.Add(fields[i]);
            }

            result.Sort(CompareFields);
            return result;
        }

        /// <summary>
        /// Restituisce campi confrontabili tra tracce
        /// </summary>
        /// <param name="scope">Scope regola</param>
        /// <param name="includeAdvanced">Vero per includere campi avanzati</param>
        /// <returns>Campi confrontabili</returns>
        public static List<MetadataFieldDefinition> GetTrackComparableFields(MkvMetadataTargetScope scope, bool includeAdvanced)
        {
            List<MetadataFieldDefinition> fields = GetReadableFields(scope, includeAdvanced);
            List<MetadataFieldDefinition> result = new List<MetadataFieldDefinition>();
            for (int i = 0; i < fields.Count; i++)
            {
                if (MetadataScopeHelper.IsTrackFieldInScope(fields[i], scope) && fields[i].ValueType != MetadataFieldValueType.Boolean)
                    result.Add(fields[i]);
            }

            result.Sort(CompareFields);
            return result;
        }

        /// <summary>
        /// Restituisce schema input per un campo metadata
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <param name="usage">Uso richiesto</param>
        /// <returns>Schema input</returns>
        public static MetadataInputSchema GetFieldInputSchema(string fieldKey, MetadataCatalogInputUsage usage)
        {
            MetadataFieldDefinition field;
            if (!MetadataFieldRegistry.TryGet(fieldKey, out field))
                return CreateTextSchema(usage);

            MetadataInputSchema schema = CreateSchema(field.ValueType, field.InputKind, usage);
            schema.Unit = field.Unit;
            schema.Options = GetOptionsForField(field);
            schema.AllowsEmpty = usage != MetadataCatalogInputUsage.ManualEdit || field.IsClearable;
            return schema;
        }

        /// <summary>
        /// Restituisce schema input per un tag metadata
        /// </summary>
        /// <param name="tagName">Nome tag</param>
        /// <param name="usage">Uso richiesto</param>
        /// <returns>Schema input</returns>
        public static MetadataInputSchema GetTagInputSchema(string tagName, MetadataCatalogInputUsage usage)
        {
            MetadataTagDefinition tag;
            if (!MetadataTagRegistry.TryGet(tagName, out tag))
                return CreateTextSchema(usage);

            MetadataInputSchema schema = CreateSchema(tag.ValueType, tag.InputKind, usage);
            schema.Options = GetOptionsForTag(tag);
            schema.AllowsEmpty = usage != MetadataCatalogInputUsage.ManualEdit || tag.IsClearable;
            return schema;
        }

        /// <summary>
        /// Restituisce tag editabili compatibili con target operazione
        /// </summary>
        /// <param name="ruleScope">Scope regola</param>
        /// <param name="tagTarget">Target tag</param>
        /// <param name="includeAdvanced">Vero per includere tag avanzati</param>
        /// <returns>Tag editabili</returns>
        public static List<MetadataTagDefinition> GetEditableTagsForOperation(MkvMetadataTargetScope ruleScope, MkvMetadataTagTarget tagTarget, bool includeAdvanced)
        {
            MkvMetadataTargetScope scope = GetTagTargetScope(ruleScope, tagTarget);
            return MetadataTagRegistry.GetEditable(scope, includeAdvanced);
        }

        /// <summary>
        /// Risolve lo scope effettivo di un target tag
        /// </summary>
        /// <param name="ruleScope">Scope regola</param>
        /// <param name="tagTarget">Target tag</param>
        /// <returns>Scope tag effettivo</returns>
        public static MkvMetadataTargetScope GetTagTargetScope(MkvMetadataTargetScope ruleScope, MkvMetadataTagTarget tagTarget)
        {
            if (tagTarget == MkvMetadataTagTarget.File)
                return MkvMetadataTargetScope.Container;

            if (tagTarget == MkvMetadataTagTarget.Current || tagTarget == MkvMetadataTagTarget.CurrentTrack || tagTarget == MkvMetadataTagTarget.AllTracks)
                return ruleScope;

            return ruleScope;
        }

        /// <summary>
        /// Restituisce catalogo operatori condizione compatibili con un campo
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Operatori condizione UI-ready</returns>
        public static List<MetadataConditionOperatorItem> GetConditionOperatorCatalog(string fieldKey)
        {
            MetadataFieldDefinition field;
            if (!MetadataFieldRegistry.TryGet(fieldKey, out field))
                return CreateOperatorItems(GetTextOperators());

            if (field.ValueType == MetadataFieldValueType.Boolean)
                return CreateOperatorItems(GetBooleanOperators());

            if (MetadataValueNormalizer.IsNumericValueType(field.ValueType))
                return CreateOperatorItems(GetNumericOperators(true));

            return CreateOperatorItems(GetTextOperators());
        }

        /// <summary>
        /// Restituisce informazioni UI per un operatore condizione
        /// </summary>
        /// <param name="conditionOperator">Operatore condizione</param>
        /// <returns>Informazioni operatore</returns>
        public static MetadataConditionOperatorItem GetConditionOperatorInfo(MkvMetadataConditionOperator conditionOperator)
        {
            return CreateOperatorItem(conditionOperator);
        }

        /// <summary>
        /// Restituisce operatori numerici
        /// </summary>
        /// <param name="includeBetween">Vero per includere range</param>
        /// <returns>Operatori numerici</returns>
        public static List<MkvMetadataConditionOperator> GetNumericOperators(bool includeBetween)
        {
            List<MkvMetadataConditionOperator> result = new List<MkvMetadataConditionOperator>();
            result.Add(MkvMetadataConditionOperator.Equals);
            result.Add(MkvMetadataConditionOperator.NotEquals);
            result.Add(MkvMetadataConditionOperator.GreaterThan);
            result.Add(MkvMetadataConditionOperator.GreaterOrEqual);
            result.Add(MkvMetadataConditionOperator.LessThan);
            result.Add(MkvMetadataConditionOperator.LessOrEqual);
            if (includeBetween)
            {
                result.Add(MkvMetadataConditionOperator.Between);
                result.Add(MkvMetadataConditionOperator.NotBetween);
            }
            result.Add(MkvMetadataConditionOperator.IsEmpty);
            result.Add(MkvMetadataConditionOperator.IsNotEmpty);
            return result;
        }

        /// <summary>
        /// Restituisce relazioni confronto tracce compatibili con un campo
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Relazioni confronto</returns>
        public static List<MkvMetadataTrackComparisonRelation> GetTrackComparisonRelations(string fieldKey)
        {
            MetadataFieldDefinition field;
            List<MkvMetadataTrackComparisonRelation> result = new List<MkvMetadataTrackComparisonRelation>();
            if (MetadataFieldRegistry.TryGet(fieldKey, out field) && !MetadataValueNormalizer.IsNumericValueType(field.ValueType))
            {
                result.Add(MkvMetadataTrackComparisonRelation.EqualsAny);
                result.Add(MkvMetadataTrackComparisonRelation.NotEqualsAll);
                return result;
            }

            result.Add(MkvMetadataTrackComparisonRelation.Largest);
            result.Add(MkvMetadataTrackComparisonRelation.Smallest);
            result.Add(MkvMetadataTrackComparisonRelation.GreaterThanAll);
            result.Add(MkvMetadataTrackComparisonRelation.GreaterOrEqualAll);
            result.Add(MkvMetadataTrackComparisonRelation.LessThanAll);
            result.Add(MkvMetadataTrackComparisonRelation.LessOrEqualAll);
            result.Add(MkvMetadataTrackComparisonRelation.Rank);
            return result;
        }

        /// <summary>
        /// Restituisce token campo disponibili
        /// </summary>
        /// <param name="scope">Scope regola</param>
        /// <param name="includeAdvanced">Vero per includere campi avanzati</param>
        /// <returns>Token campo</returns>
        public static List<MetadataCatalogFieldItem> GetTokenCatalog(MkvMetadataTargetScope scope, bool includeAdvanced)
        {
            List<MetadataFieldDefinition> fields = GetReadableFields(scope, includeAdvanced);
            List<MetadataCatalogFieldItem> result = new List<MetadataCatalogFieldItem>();
            for (int i = 0; i < fields.Count; i++)
            {
                result.Add(CreateFieldItem(fields[i]));
            }

            return result;
        }

        /// <summary>
        /// Restituisce catalogo funzioni expression
        /// </summary>
        /// <returns>Funzioni disponibili</returns>
        public static List<MetadataCatalogFunctionItem> GetFunctionCatalog()
        {
            List<MetadataCatalogFunctionItem> result = new List<MetadataCatalogFunctionItem>();
            result.Add(CreateFunction("Trim", "Trim()", "  ITA Full  ", "{[audio_title]:Trim()}", "ITA Full"));
            result.Add(CreateFunction("TrimEnd", "TrimEnd(4)", "Bleach - 001.mkv", "{[file_name]:TrimEnd(4)}", "Bleach - 001"));
            result.Add(CreateFunction("ToUpper", "ToUpper()", "ita", "{[audio_language]:ToUpper()}", "ITA"));
            result.Add(CreateFunction("ToLower", "ToLower()", "ITA", "{[audio_language]:ToLower()}", "ita"));
            result.Add(CreateFunction("Replace", "Replace(old,new)", "FLAC 2.0", "{[audio_title]:Replace(FLAC,LPCM)}", "LPCM 2.0"));
            result.Add(CreateFunction("RegexReplace", "RegexReplace(pattern,replacement)", "Audio 01", "{[audio_title]:RegexReplace(\\d+,02)}", "Audio 02"));
            result.Add(CreateFunction("Substring", "Substring(0,3)", "Italiano", "{[audio_language]:Substring(0,3)}", "Ita"));
            result.Add(CreateFunction("Left", "Left(3)", "Italiano", "{[audio_language]:Left(3)}", "Ita"));
            result.Add(CreateFunction("Right", "Right(3)", "Italiano", "{[audio_language]:Right(3)}", "ano"));
            result.Add(CreateFunction("NormalizeSpaces", "NormalizeSpaces()", "ITA   Full", "{[subtitle_title]:NormalizeSpaces()}", "ITA Full"));
            result.Add(CreateFunction("Add", "Add(1000)", "24000", "{[audio_bitrate]:Add(1000)}", "25000"));
            result.Add(CreateFunction("Sub", "Sub(1000)", "25000", "{[audio_bitrate]:Sub(1000)}", "24000"));
            result.Add(CreateFunction("Mul", "Mul(2)", "24", "{[video_frame_count]:Mul(2)}", "48"));
            result.Add(CreateFunction("Div", "Div(1000)", "1536000", "{[audio_bitrate]:Div(1000)}", "1536"));
            result.Add(CreateFunction("Round", "Round(1)", "23.976", "{[video_fps]:Round(1)}", "24"));
            result.Add(CreateFunction("Floor", "Floor()", "23.976", "{[video_fps]:Floor()}", "23"));
            result.Add(CreateFunction("Ceil", "Ceil()", "23.001", "{[video_fps]:Ceil()}", "24"));
            result.Add(CreateFunction("Format", "Format(0.#)", "48000", "{[audio_sampling_rate]:Div(1000):Format(0.#)} kHz", "48 kHz"));
            return result;
        }

        /// <summary>
        /// Restituisce help per un campo metadata
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Help contestuale</returns>
        public static MetadataHelpInfo GetFieldHelp(string fieldKey)
        {
            MetadataFieldDefinition field;
            if (!MetadataFieldRegistry.TryGet(fieldKey, out field))
                return GetPresetControlHelp("default");

            MetadataHelpInfo result = new MetadataHelpInfo();
            result.Title = field.Label;
            result.Text = ResolveHelpText(field.HelpKey, "metadata.fieldHelp." + field.Key, field.Description);
            if (string.IsNullOrEmpty(result.Text))
                result.Text = BuildFieldFallbackHelp(field);

            return result;
        }

        /// <summary>
        /// Restituisce help per un tag metadata
        /// </summary>
        /// <param name="tagName">Nome tag</param>
        /// <returns>Help contestuale</returns>
        public static MetadataHelpInfo GetTagHelp(string tagName)
        {
            MetadataTagDefinition tag;
            if (!MetadataTagRegistry.TryGet(tagName, out tag))
                return GetPresetControlHelp("tagKey");

            MetadataHelpInfo result = new MetadataHelpInfo();
            result.Title = tag.Label;
            result.Text = ResolveHelpText(tag.HelpKey, "metadata.tagHelp." + tag.Name, tag.Description);
            if (string.IsNullOrEmpty(result.Text) || string.Equals(result.Text, tag.Name, StringComparison.OrdinalIgnoreCase))
                result.Text = BuildTagFallbackHelp(tag);

            return result;
        }

        /// <summary>
        /// Restituisce help per una funzione expression
        /// </summary>
        /// <param name="functionName">Nome funzione</param>
        /// <returns>Help contestuale</returns>
        public static MetadataHelpInfo GetFunctionHelp(string functionName)
        {
            List<MetadataCatalogFunctionItem> functions = GetFunctionCatalog();
            for (int i = 0; i < functions.Count; i++)
            {
                if (string.Equals(functions[i].Name, functionName, StringComparison.Ordinal))
                {
                    MetadataHelpInfo result = new MetadataHelpInfo();
                    result.Title = functions[i].Call;
                    result.Text = functions[i].Description;
                    result.Example = functions[i].ExampleExpression;
                    return result;
                }
            }

            return GetPresetControlHelp("default");
        }

        /// <summary>
        /// Restituisce help per un controllo generico del preset builder
        /// </summary>
        /// <param name="controlKey">Chiave controllo</param>
        /// <returns>Help contestuale</returns>
        public static MetadataHelpInfo GetPresetControlHelp(string controlKey)
        {
            MetadataHelpInfo result = new MetadataHelpInfo();
            string titleKey = "web.metadata.presetDialog.help." + controlKey + ".title";
            string textKey = "web.metadata.presetDialog.help." + controlKey + ".text";
            result.Title = GetOptionalText(titleKey);
            result.Text = GetOptionalText(textKey);
            if (string.IsNullOrEmpty(result.Title))
                result.Title = AppText.T("web.metadata.presetDialog.help.default.title");
            if (string.IsNullOrEmpty(result.Text))
                result.Text = AppText.T("web.metadata.presetDialog.help.default.text");

            return result;
        }

        /// <summary>
        /// Verifica se una funzione expression è nota
        /// </summary>
        /// <param name="name">Nome funzione</param>
        /// <returns>Vero se nota</returns>
        public static bool IsKnownFunction(string name)
        {
            List<MetadataCatalogFunctionItem> functions = GetFunctionCatalog();
            for (int i = 0; i < functions.Count; i++)
            {
                if (string.Equals(functions[i].Name, name, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Indica se un preset usa campi o tag avanzati
        /// </summary>
        /// <param name="preset">Preset da verificare</param>
        /// <returns>Vero se usa valori avanzati</returns>
        public static bool PresetUsesAdvancedFields(MkvMetadataPreset preset)
        {
            if (preset == null || preset.Rules == null)
                return false;

            for (int i = 0; i < preset.Rules.Count; i++)
            {
                if (RuleUsesAdvancedFields(preset.Rules[i]))
                    return true;
            }

            return false;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Crea item campo per token browser
        /// </summary>
        /// <param name="field">Campo sorgente</param>
        /// <returns>Item campo</returns>
        private static MetadataCatalogFieldItem CreateFieldItem(MetadataFieldDefinition field)
        {
            MetadataCatalogFieldItem item = new MetadataCatalogFieldItem();
            item.Key = field.Key;
            item.Label = field.Label;
            item.Description = !string.IsNullOrEmpty(field.Description) ? field.Description : GetSectorLabel(field.Sector);
            item.Token = "[" + field.Key + "]";
            item.Sector = field.Sector;
            item.TargetScopes = new List<MkvMetadataTargetScope>(field.TargetScopes);
            item.ValueType = field.ValueType;
            item.InputKind = field.InputKind;
            item.Visibility = field.Visibility;
            item.IsEditable = field.IsEditable;
            item.IsClearable = field.IsClearable;
            item.Unit = field.Unit;
            item.SortGroup = field.SortGroup;
            return item;
        }

        /// <summary>
        /// Crea schema testuale di fallback
        /// </summary>
        /// <param name="usage">Uso richiesto</param>
        /// <returns>Schema testo</returns>
        private static MetadataInputSchema CreateTextSchema(MetadataCatalogInputUsage usage)
        {
            return CreateSchema(MetadataFieldValueType.String, MetadataFieldInputKind.Text, usage);
        }

        /// <summary>
        /// Crea schema input a partire da tipo valore e input kind
        /// </summary>
        /// <param name="valueType">Tipo valore</param>
        /// <param name="inputKind">Tipo input</param>
        /// <param name="usage">Uso richiesto</param>
        /// <returns>Schema input</returns>
        private static MetadataInputSchema CreateSchema(MetadataFieldValueType valueType, MetadataFieldInputKind inputKind, MetadataCatalogInputUsage usage)
        {
            MetadataInputSchema schema = new MetadataInputSchema();
            schema.ValueType = valueType;
            schema.InputKind = inputKind;
            schema.HtmlInputType = ResolveHtmlInputType(inputKind);
            schema.Step = ResolveStep(inputKind);
            schema.SupportsExpression = usage == MetadataCatalogInputUsage.ConditionValue || usage == MetadataCatalogInputUsage.OperationValue;
            if (inputKind == MetadataFieldInputKind.Select)
                schema.SupportsExpression = false;

            schema.UnitOptions = CreateUnitOptions(inputKind);
            if (inputKind == MetadataFieldInputKind.Boolean)
                schema.Options = CreateBooleanOptions();
            else if (inputKind == MetadataFieldInputKind.LanguageSelect)
                schema.Options = CreateLanguageOptions();
            else if (inputKind == MetadataFieldInputKind.LanguageIetf)
                schema.Options = CreateLanguageIetfOptions();

            return schema;
        }

        /// <summary>
        /// Restituisce opzioni statiche di un campo
        /// </summary>
        /// <param name="field">Campo sorgente</param>
        /// <returns>Opzioni campo</returns>
        private static List<MetadataInputOption> GetOptionsForField(MetadataFieldDefinition field)
        {
            if (field.AllowedValues != null && field.AllowedValues.Count > 0)
                return MetadataInputOptionCloner.CloneList(field.AllowedValues);

            if (field.InputKind == MetadataFieldInputKind.Boolean)
                return CreateBooleanOptions();

            if (field.InputKind == MetadataFieldInputKind.LanguageSelect)
                return CreateLanguageOptions();

            if (field.InputKind == MetadataFieldInputKind.LanguageIetf)
                return CreateLanguageIetfOptions();

            return new List<MetadataInputOption>();
        }

        /// <summary>
        /// Restituisce opzioni statiche di un tag
        /// </summary>
        /// <param name="tag">Tag sorgente</param>
        /// <returns>Opzioni tag</returns>
        private static List<MetadataInputOption> GetOptionsForTag(MetadataTagDefinition tag)
        {
            if (tag.AllowedValues != null && tag.AllowedValues.Count > 0)
                return MetadataInputOptionCloner.CloneList(tag.AllowedValues);

            if (tag.InputKind == MetadataFieldInputKind.LanguageSelect)
                return CreateLanguageOptions();

            if (tag.InputKind == MetadataFieldInputKind.LanguageIetf)
                return CreateLanguageIetfOptions();

            return new List<MetadataInputOption>();
        }

        /// <summary>
        /// Restituisce tipo input HTML di base
        /// </summary>
        /// <param name="inputKind">Tipo input catalogo</param>
        /// <returns>Tipo input HTML</returns>
        private static string ResolveHtmlInputType(MetadataFieldInputKind inputKind)
        {
            if (inputKind == MetadataFieldInputKind.Number || inputKind == MetadataFieldInputKind.SizeInput || inputKind == MetadataFieldInputKind.DurationInput)
                return "number";

            if (inputKind == MetadataFieldInputKind.Decimal)
                return "number";

            if (inputKind == MetadataFieldInputKind.DateInput)
                return "text";

            return "text";
        }

        /// <summary>
        /// Restituisce step input HTML
        /// </summary>
        /// <param name="inputKind">Tipo input catalogo</param>
        /// <returns>Step input</returns>
        private static string ResolveStep(MetadataFieldInputKind inputKind)
        {
            if (inputKind == MetadataFieldInputKind.Decimal)
                return "any";

            if (inputKind == MetadataFieldInputKind.Number || inputKind == MetadataFieldInputKind.SizeInput || inputKind == MetadataFieldInputKind.DurationInput)
                return "1";

            return "";
        }

        /// <summary>
        /// Crea opzioni unità per campi dimensionali o temporali
        /// </summary>
        /// <param name="inputKind">Tipo input</param>
        /// <returns>Opzioni unità</returns>
        private static List<MetadataInputOption> CreateUnitOptions(MetadataFieldInputKind inputKind)
        {
            List<MetadataInputOption> result = new List<MetadataInputOption>();
            result.Add(new MetadataInputOption("", AppText.T("web.metadata.unit.none")));

            if (inputKind == MetadataFieldInputKind.SizeInput)
            {
                result.Add(new MetadataInputOption("KB", "KB"));
                result.Add(new MetadataInputOption("MB", "MB"));
                result.Add(new MetadataInputOption("GB", "GB"));
                result.Add(new MetadataInputOption("KiB", "KiB"));
                result.Add(new MetadataInputOption("MiB", "MiB"));
                result.Add(new MetadataInputOption("GiB", "GiB"));
            }
            else if (inputKind == MetadataFieldInputKind.DurationInput)
            {
                result.Add(new MetadataInputOption("ms", "ms"));
                result.Add(new MetadataInputOption("s", "s"));
                result.Add(new MetadataInputOption("min", "min"));
                result.Add(new MetadataInputOption("h", "h"));
            }

            return result;
        }

        /// <summary>
        /// Crea opzioni booleane Matroska
        /// </summary>
        /// <returns>Opzioni booleane</returns>
        private static List<MetadataInputOption> CreateBooleanOptions()
        {
            List<MetadataInputOption> result = new List<MetadataInputOption>();
            result.Add(new MetadataInputOption("1", AppText.T("web.common.true")));
            result.Add(new MetadataInputOption("0", AppText.T("web.common.false")));
            return result;
        }

        /// <summary>
        /// Crea opzioni lingua ISO 639-2
        /// </summary>
        /// <returns>Opzioni lingua</returns>
        private static List<MetadataInputOption> CreateLanguageOptions()
        {
            List<string> languages = LanguageValidator.GetAll();
            List<MetadataInputOption> result = new List<MetadataInputOption>();
            for (int i = 0; i < languages.Count; i++)
            {
                result.Add(CreateLanguageOption(languages[i]));
            }

            return result;
        }

        /// <summary>
        /// Crea opzioni comuni BCP 47 per autocomplete libero
        /// </summary>
        /// <returns>Opzioni lingua IETF</returns>
        private static List<MetadataInputOption> CreateLanguageIetfOptions()
        {
            List<MetadataInputOption> result = new List<MetadataInputOption>();
            result.Add(new MetadataInputOption("it", "it"));
            result.Add(new MetadataInputOption("it-IT", "it-IT"));
            result.Add(new MetadataInputOption("en", "en"));
            result.Add(new MetadataInputOption("en-US", "en-US"));
            result.Add(new MetadataInputOption("en-GB", "en-GB"));
            result.Add(new MetadataInputOption("ja", "ja"));
            result.Add(new MetadataInputOption("fr", "fr"));
            result.Add(new MetadataInputOption("de", "de"));
            result.Add(new MetadataInputOption("es", "es"));
            result.Add(new MetadataInputOption("pt-BR", "pt-BR"));
            return result;
        }

        /// <summary>
        /// Crea una opzione lingua ISO 639-2 con label leggibile
        /// </summary>
        /// <param name="code">Codice ISO 639-2</param>
        /// <returns>Opzione lingua</returns>
        private static MetadataInputOption CreateLanguageOption(string code)
        {
            string name = GetOptionalText("metadata.language." + code);
            if (string.IsNullOrEmpty(name))
                return new MetadataInputOption(code, code);

            return new MetadataInputOption(code, name + " (" + code + ")");
        }

        /// <summary>
        /// Crea una definizione funzione UI
        /// </summary>
        /// <param name="name">Nome funzione</param>
        /// <param name="call">Chiamata inseribile</param>
        /// <param name="exampleInput">Input esempio</param>
        /// <param name="exampleExpression">Espressione esempio</param>
        /// <param name="exampleOutput">Output esempio</param>
        /// <returns>Funzione catalogata</returns>
        private static MetadataCatalogFunctionItem CreateFunction(string name, string call, string exampleInput, string exampleExpression, string exampleOutput)
        {
            MetadataCatalogFunctionItem item = new MetadataCatalogFunctionItem();
            item.Name = name;
            item.Call = call;
            item.Description = AppText.T("web.metadata.function." + name + ".description");
            item.ExampleInput = exampleInput;
            item.ExampleExpression = exampleExpression;
            item.ExampleOutput = exampleOutput;
            item.ExampleNotes = "";
            return item;
        }

        /// <summary>
        /// Crea item operatori UI-ready
        /// </summary>
        /// <param name="operators">Operatori dominio</param>
        /// <returns>Operatori UI-ready</returns>
        private static List<MetadataConditionOperatorItem> CreateOperatorItems(List<MkvMetadataConditionOperator> operators)
        {
            List<MetadataConditionOperatorItem> result = new List<MetadataConditionOperatorItem>();
            for (int i = 0; i < operators.Count; i++)
            {
                result.Add(CreateOperatorItem(operators[i]));
            }

            return result;
        }

        /// <summary>
        /// Crea item operatore UI-ready
        /// </summary>
        /// <param name="conditionOperator">Operatore condizione</param>
        /// <returns>Operatore UI-ready</returns>
        private static MetadataConditionOperatorItem CreateOperatorItem(MkvMetadataConditionOperator conditionOperator)
        {
            MetadataConditionOperatorItem item = new MetadataConditionOperatorItem();
            item.Operator = conditionOperator;
            item.Label = AppText.T("web.metadata.conditionOperator." + conditionOperator.ToString());
            item.RequiresRange = conditionOperator == MkvMetadataConditionOperator.Between || conditionOperator == MkvMetadataConditionOperator.NotBetween;
            item.RequiresList = conditionOperator == MkvMetadataConditionOperator.InList || conditionOperator == MkvMetadataConditionOperator.NotInList;
            item.RequiresValue = conditionOperator != MkvMetadataConditionOperator.IsEmpty &&
                conditionOperator != MkvMetadataConditionOperator.IsNotEmpty &&
                conditionOperator != MkvMetadataConditionOperator.IsTrue &&
                conditionOperator != MkvMetadataConditionOperator.IsFalse;
            return item;
        }

        /// <summary>
        /// Risolve help specifico o fallback
        /// </summary>
        /// <param name="helpKey">Chiave help dichiarata</param>
        /// <param name="defaultKey">Chiave help convenzionale</param>
        /// <param name="fallback">Testo fallback</param>
        /// <returns>Testo help</returns>
        private static string ResolveHelpText(string helpKey, string defaultKey, string fallback)
        {
            string text = !string.IsNullOrEmpty(helpKey) ? GetOptionalText(helpKey) : "";
            if (!string.IsNullOrEmpty(text))
                return text;

            text = GetOptionalText(defaultKey);
            if (!string.IsNullOrEmpty(text))
                return text;

            return fallback != null ? fallback : "";
        }

        /// <summary>
        /// Costruisce un help informativo quando il campo non dispone di una descrizione specifica
        /// </summary>
        /// <param name="field">Definizione del campo</param>
        /// <returns>Help localizzato</returns>
        private static string BuildFieldFallbackHelp(MetadataFieldDefinition field)
        {
            string access = field.IsEditable ? AppText.T("web.metadata.help.editable") : AppText.T("web.metadata.help.readOnly");
            return AppText.F("web.metadata.help.fieldFallback", field.Key, GetSectorLabel(field.Sector), GetValueTypeLabel(field.ValueType), access);
        }

        /// <summary>
        /// Costruisce un help informativo quando il tag non dispone di una descrizione specifica
        /// </summary>
        /// <param name="tag">Definizione del tag</param>
        /// <returns>Help localizzato</returns>
        private static string BuildTagFallbackHelp(MetadataTagDefinition tag)
        {
            string clearability = tag.IsClearable ? AppText.T("web.metadata.help.clearable") : AppText.T("web.metadata.help.notClearable");
            return AppText.F("web.metadata.help.tagFallback", tag.Name, GetValueTypeLabel(tag.ValueType), clearability);
        }

        /// <summary>
        /// Restituisce il nome localizzato del tipo valore metadata
        /// </summary>
        /// <param name="valueType">Tipo valore</param>
        /// <returns>Nome localizzato</returns>
        private static string GetValueTypeLabel(MetadataFieldValueType valueType)
        {
            string key = "web.metadata.valueType." + valueType.ToString();
            string result = GetOptionalText(key);
            return !string.IsNullOrEmpty(result) ? result : valueType.ToString();
        }

        /// <summary>
        /// Legge un testo localizzato solo se la chiave esiste
        /// </summary>
        /// <param name="key">Chiave localizzazione</param>
        /// <returns>Testo localizzato o stringa vuota</returns>
        private static string GetOptionalText(string key)
        {
            string text;
            if (string.IsNullOrEmpty(key))
                return "";

            text = AppText.T(key);
            if (text == "[" + key + "]")
                return "";

            return text;
        }

        /// <summary>
        /// Restituisce operatori testuali
        /// </summary>
        /// <returns>Operatori testuali</returns>
        private static List<MkvMetadataConditionOperator> GetTextOperators()
        {
            List<MkvMetadataConditionOperator> result = new List<MkvMetadataConditionOperator>();
            result.Add(MkvMetadataConditionOperator.Equals);
            result.Add(MkvMetadataConditionOperator.NotEquals);
            result.Add(MkvMetadataConditionOperator.Contains);
            result.Add(MkvMetadataConditionOperator.NotContains);
            result.Add(MkvMetadataConditionOperator.StartsWith);
            result.Add(MkvMetadataConditionOperator.EndsWith);
            result.Add(MkvMetadataConditionOperator.Regex);
            result.Add(MkvMetadataConditionOperator.NotRegex);
            result.Add(MkvMetadataConditionOperator.InList);
            result.Add(MkvMetadataConditionOperator.NotInList);
            result.Add(MkvMetadataConditionOperator.IsEmpty);
            result.Add(MkvMetadataConditionOperator.IsNotEmpty);
            return result;
        }

        /// <summary>
        /// Restituisce operatori booleani
        /// </summary>
        /// <returns>Operatori booleani</returns>
        private static List<MkvMetadataConditionOperator> GetBooleanOperators()
        {
            List<MkvMetadataConditionOperator> result = new List<MkvMetadataConditionOperator>();
            result.Add(MkvMetadataConditionOperator.IsTrue);
            result.Add(MkvMetadataConditionOperator.IsFalse);
            return result;
        }

        /// <summary>
        /// Indica se una regola usa campi o tag avanzati
        /// </summary>
        /// <param name="rule">Regola da verificare</param>
        /// <returns>Vero se usa valori avanzati</returns>
        private static bool RuleUsesAdvancedFields(MkvMetadataRule rule)
        {
            if (rule == null)
                return false;

            if (rule.When != null && NodesUseAdvancedFields(rule.When.All))
                return true;

            if (rule.Operations == null)
                return false;

            for (int i = 0; i < rule.Operations.Count; i++)
            {
                if (OperationUsesAdvancedFields(rule.TargetScope, rule.Operations[i]))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Indica se una lista condizioni usa campi avanzati
        /// </summary>
        /// <param name="nodes">Nodi condizione</param>
        /// <returns>Vero se usa advanced</returns>
        private static bool NodesUseAdvancedFields(List<MkvMetadataRuleConditionNode> nodes)
        {
            if (nodes == null)
                return false;

            for (int i = 0; i < nodes.Count; i++)
            {
                if (NodeUsesAdvancedFields(nodes[i]))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Indica se un nodo condizione usa campi avanzati
        /// </summary>
        /// <param name="node">Nodo condizione</param>
        /// <returns>Vero se usa advanced</returns>
        private static bool NodeUsesAdvancedFields(MkvMetadataRuleConditionNode node)
        {
            MetadataFieldDefinition field;
            if (node == null)
                return false;

            if (node.NodeType == MkvMetadataRuleConditionNodeType.Field && node.Field != null && MetadataFieldRegistry.TryGet(node.Field.FieldKey, out field))
                return IsAdvancedField(field);

            if (node.NodeType == MkvMetadataRuleConditionNodeType.TrackComparison && node.TrackComparison != null && MetadataFieldRegistry.TryGet(node.TrackComparison.FieldKey, out field))
                return IsAdvancedField(field);

            if (node.NodeType == MkvMetadataRuleConditionNodeType.AlternativeAny && node.Alternative != null)
                return NodesUseAdvancedFields(node.Alternative.Any);

            return false;
        }

        /// <summary>
        /// Indica se una operazione usa campi o tag avanzati
        /// </summary>
        /// <param name="scope">Scope regola</param>
        /// <param name="operation">Operazione da verificare</param>
        /// <returns>Vero se usa advanced</returns>
        private static bool OperationUsesAdvancedFields(MkvMetadataTargetScope scope, MkvMetadataOperation operation)
        {
            MetadataFieldDefinition field;
            MetadataTagDefinition tag;
            if (operation == null)
                return false;

            if ((operation.Type == MkvMetadataOperationType.SetField || operation.Type == MkvMetadataOperationType.ClearField || operation.Type == MkvMetadataOperationType.SetExclusiveFlag) &&
                MetadataFieldRegistry.TryGet(operation.FieldKey, out field))
                return IsAdvancedField(field);

            if ((operation.Type == MkvMetadataOperationType.SetTagField || operation.Type == MkvMetadataOperationType.ClearTagField) &&
                MetadataTagRegistry.TryGet(operation.TagKey, out tag))
                return tag.Visibility == MetadataFieldVisibility.Advanced && IsTagCompatibleWithOperation(scope, operation.TagTarget, tag);

            return false;
        }

        /// <summary>
        /// Verifica se un campo è avanzato
        /// </summary>
        /// <param name="field">Campo da verificare</param>
        /// <returns>Vero se avanzato</returns>
        private static bool IsAdvancedField(MetadataFieldDefinition field)
        {
            return field.Visibility == MetadataFieldVisibility.Advanced || field.EditPolicy == MetadataFieldEditPolicy.Advanced || field.RiskLevel == MetadataFieldRiskLevel.Advanced;
        }

        /// <summary>
        /// Verifica compatibilità tag con target operazione
        /// </summary>
        /// <param name="scope">Scope regola</param>
        /// <param name="target">Target tag</param>
        /// <param name="tag">Tag da verificare</param>
        /// <returns>Vero se compatibile</returns>
        private static bool IsTagCompatibleWithOperation(MkvMetadataTargetScope scope, MkvMetadataTagTarget target, MetadataTagDefinition tag)
        {
            MkvMetadataTargetScope targetScope = GetTagTargetScope(scope, target);
            for (int i = 0; i < tag.TargetScopes.Count; i++)
            {
                if (tag.TargetScopes[i] == targetScope)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Restituisce label settore
        /// </summary>
        /// <param name="sector">Settore campo</param>
        /// <returns>Label settore</returns>
        private static string GetSectorLabel(MetadataFieldSector sector)
        {
            return AppText.T("web.metadata.fieldSector." + sector.ToString());
        }

        /// <summary>
        /// Confronta campi per ordinamento UI
        /// </summary>
        /// <param name="left">Campo sinistro</param>
        /// <param name="right">Campo destro</param>
        /// <returns>Risultato confronto</returns>
        private static int CompareFields(MetadataFieldDefinition left, MetadataFieldDefinition right)
        {
            int groupCompare = left.SortGroup.CompareTo(right.SortGroup);
            if (groupCompare != 0)
                return groupCompare;

            return string.Compare(left.Label, right.Label, StringComparison.CurrentCultureIgnoreCase);
        }

        #endregion
    }
}
