using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Registro tag Matroska gestiti dalla UI Metadata
    /// </summary>
    public static class MetadataTagRegistry
    {
        #region Variabili statiche

        /// <summary>
        /// Tag registrati
        /// </summary>
        private static readonly List<MetadataTagDefinition> s_tags;

        /// <summary>
        /// Indice tag per nome
        /// </summary>
        private static readonly Dictionary<string, MetadataTagDefinition> s_byName;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore statico
        /// </summary>
        static MetadataTagRegistry()
        {
            s_tags = BuildTags();
            s_byName = new Dictionary<string, MetadataTagDefinition>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < s_tags.Count; i++)
            {
                s_byName[s_tags[i].Name] = s_tags[i];
            }

            ValidateCatalog();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce tag editabili per scope
        /// </summary>
        /// <param name="scope">Scope target</param>
        /// <param name="includeAdvanced">Vero per includere tag avanzati</param>
        /// <returns>Lista tag editabili</returns>
        public static List<MetadataTagDefinition> GetEditable(MkvMetadataTargetScope scope, bool includeAdvanced)
        {
            List<MetadataTagDefinition> result = new List<MetadataTagDefinition>();
            for (int i = 0; i < s_tags.Count; i++)
            {
                MetadataTagDefinition tag = s_tags[i];
                if (!tag.IsEditable || tag.Visibility == MetadataFieldVisibility.Hidden || tag.Visibility == MetadataFieldVisibility.Technical)
                    continue;

                if (!includeAdvanced && tag.Visibility == MetadataFieldVisibility.Advanced)
                    continue;

                if (IsScopeCompatible(tag, scope))
                    result.Add(CloneTag(tag));
            }

            result.Sort(CompareTags);
            return result;
        }

        /// <summary>
        /// Restituisce i nomi tag editabili gestiti dalla UI
        /// </summary>
        /// <returns>Nomi tag ordinati</returns>
        public static List<string> GetEditableTagNames()
        {
            List<string> result = new List<string>();
            for (int i = 0; i < s_tags.Count; i++)
            {
                if (s_tags[i].IsEditable && s_tags[i].Visibility != MetadataFieldVisibility.Hidden && s_tags[i].Visibility != MetadataFieldVisibility.Technical)
                    result.Add(s_tags[i].Name);
            }

            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>
        /// Cerca un tag per nome
        /// </summary>
        /// <param name="tagName">Nome tag</param>
        /// <param name="tag">Tag trovato</param>
        /// <returns>Vero se trovato</returns>
        public static bool TryGet(string tagName, out MetadataTagDefinition tag)
        {
            string name = tagName != null ? tagName.Trim() : "";
            MetadataTagDefinition found;

            if (s_byName.TryGetValue(name, out found))
            {
                tag = CloneTag(found);
                return true;
            }

            tag = null;
            return false;
        }

        /// <summary>
        /// Verifica se un tag è gestito dalla UI
        /// </summary>
        /// <param name="tagName">Nome tag</param>
        /// <returns>True se consentito</returns>
        public static bool IsAllowed(string tagName)
        {
            MetadataTagDefinition tag;
            return TryGet(tagName, out tag) && tag.IsEditable;
        }

        /// <summary>
        /// Valida che un tag sia scrivibile nello scope indicato
        /// </summary>
        /// <param name="tagName">Nome tag</param>
        /// <param name="scope">Scope target</param>
        /// <param name="errorMessage">Errore validazione</param>
        /// <returns>Vero se scrivibile</returns>
        public static bool ValidateWritable(string tagName, MkvMetadataTargetScope scope, out string errorMessage)
        {
            MetadataTagDefinition tag;

            errorMessage = "";
            if (!TryGet(tagName, out tag))
            {
                errorMessage = AppText.F("metadata.validation.tagNotWritable", tagName);
                return false;
            }

            if (!tag.IsEditable || !IsScopeCompatible(tag, scope))
            {
                errorMessage = AppText.F("metadata.validation.tagNotWritable", tagName);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Valida e normalizza un valore scrivibile per un tag
        /// </summary>
        /// <param name="tagName">Nome tag</param>
        /// <param name="scope">Scope target</param>
        /// <param name="value">Valore sorgente</param>
        /// <param name="allowEmpty">Vero se il valore vuoto è consentito</param>
        /// <param name="normalizedValue">Valore normalizzato</param>
        /// <param name="errorMessage">Errore validazione</param>
        /// <returns>Vero se il valore è valido</returns>
        public static bool ValidateWritableValue(string tagName, MkvMetadataTargetScope scope, string value, bool allowEmpty, out string normalizedValue, out string errorMessage)
        {
            MetadataTagDefinition tag;

            normalizedValue = value != null ? value.Trim() : "";
            if (!ValidateWritable(tagName, scope, out errorMessage))
                return false;

            if (!TryGet(tagName, out tag))
            {
                errorMessage = AppText.F("metadata.validation.tagNotWritable", tagName);
                return false;
            }

            return MetadataCatalogValueValidator.Validate(tag.ValueType, tag.InputKind, tag.AllowedValues, tag.Label, value, allowEmpty, out normalizedValue, out errorMessage);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Costruisce il catalogo tag gestito dalla UI
        /// </summary>
        /// <returns>Lista tag</returns>
        private static List<MetadataTagDefinition> BuildTags()
        {
            List<MetadataTagDefinition> result = new List<MetadataTagDefinition>();
            AddTag(result, "TITLE", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Primary, true, MkvMetadataTargetScope.Container, MkvMetadataTargetScope.Video, MkvMetadataTargetScope.Audio, MkvMetadataTargetScope.Subtitle);
            AddTag(result, "SUBTITLE", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Primary, true, MkvMetadataTargetScope.Container, MkvMetadataTargetScope.Video, MkvMetadataTargetScope.Audio, MkvMetadataTargetScope.Subtitle);
            AddTag(result, "DESCRIPTION", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Primary, true, MkvMetadataTargetScope.Container, MkvMetadataTargetScope.Video, MkvMetadataTargetScope.Audio, MkvMetadataTargetScope.Subtitle);
            AddTag(result, "COMMENT", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Primary, true, MkvMetadataTargetScope.Container, MkvMetadataTargetScope.Video, MkvMetadataTargetScope.Audio, MkvMetadataTargetScope.Subtitle);
            AddTag(result, "SUMMARY", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Primary, true, MkvMetadataTargetScope.Container);
            AddTag(result, "SYNOPSIS", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Primary, true, MkvMetadataTargetScope.Container);
            AddTag(result, "DATE_RELEASED", MetadataFieldValueType.Date, MetadataFieldInputKind.DateInput, MetadataFieldVisibility.Primary, true, MkvMetadataTargetScope.Container);
            AddTag(result, "GENRE", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Primary, true, MkvMetadataTargetScope.Container);
            AddTag(result, "PART_NUMBER", MetadataFieldValueType.Integer, MetadataFieldInputKind.Number, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container);
            AddTag(result, "PART_TOTAL", MetadataFieldValueType.Integer, MetadataFieldInputKind.Number, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container);
            AddTag(result, "DIRECTOR", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container);
            AddTag(result, "PRODUCER", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container);
            AddTag(result, "WRITTEN_BY", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container);
            AddTag(result, "COMPOSER", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container);
            AddTag(result, "ENCODER", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container);
            AddTag(result, "SOURCE", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container);
            AddTag(result, "LANGUAGE", MetadataFieldValueType.Language, MetadataFieldInputKind.LanguageSelect, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container, MkvMetadataTargetScope.Video, MkvMetadataTargetScope.Audio, MkvMetadataTargetScope.Subtitle);
            AddTag(result, "ORIGINAL_MEDIA_TYPE", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container);
            AddTag(result, "ORIGINAL_TITLE", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Primary, true, MkvMetadataTargetScope.Container);
            AddTag(result, "ACTOR", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Primary, true, MkvMetadataTargetScope.Container);
            AddTag(result, "PERFORMER", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container);
            AddTag(result, "ARTIST", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container, MkvMetadataTargetScope.Audio);
            AddTag(result, "ALBUM", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Advanced, true, MkvMetadataTargetScope.Container);
            AddTag(result, "TVDB", MetadataFieldValueType.String, MetadataFieldInputKind.Text, MetadataFieldVisibility.Primary, true, MkvMetadataTargetScope.Container);

            // Il titolo della serie e la stagione non sono dell'episodio: senza il livello
            // giusto finirebbero a 50 e ogni episodio direbbe di chiamarsi come la serie
            SetDefaultTargetLevel(result, "TVDB", MetadataTagTargetLevels.COLLECTION);
            SetDefaultTargetLevel(result, "ALBUM", MetadataTagTargetLevels.SEASON);
            return result;
        }

        /// <summary>
        /// Aggiunge un tag al catalogo
        /// </summary>
        /// <param name="tags">Lista destinazione</param>
        /// <param name="name">Nome tag Matroska</param>
        /// <param name="valueType">Tipo valore</param>
        /// <param name="inputKind">Tipo input</param>
        /// <param name="visibility">Visibilità UI</param>
        /// <param name="clearable">Vero se cancellabile</param>
        /// <param name="scopes">Scope compatibili</param>
        private static void AddTag(List<MetadataTagDefinition> tags, string name, MetadataFieldValueType valueType, MetadataFieldInputKind inputKind, MetadataFieldVisibility visibility, bool clearable, params MkvMetadataTargetScope[] scopes)
        {
            MetadataTagDefinition tag = new MetadataTagDefinition();
            tag.Key = name;
            tag.Name = name;
            tag.Label = name;
            tag.Description = name;
            tag.ValueType = valueType;
            tag.InputKind = inputKind;
            tag.Visibility = visibility;
            tag.IsEditable = true;
            tag.IsClearable = clearable;
            tag.SortGroup = visibility == MetadataFieldVisibility.Primary ? 0 : 100;
            if (scopes != null)
            {
                for (int i = 0; i < scopes.Length; i++)
                    tag.TargetScopes.Add(scopes[i]);
            }

            tags.Add(tag);
        }

        /// <summary>
        /// Imposta il livello di target di default di un tag
        /// </summary>
        /// <param name="tags">Lista tag in costruzione</param>
        /// <param name="name">Nome del tag</param>
        /// <param name="targetTypeValue">Livello di target</param>
        private static void SetDefaultTargetLevel(List<MetadataTagDefinition> tags, string name, int targetTypeValue)
        {
            for (int i = 0; i < tags.Count; i++)
            {
                if (string.Equals(tags[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    tags[i].DefaultTargetTypeValue = targetTypeValue;
            }
        }

        /// <summary>
        /// Clona una definizione tag
        /// </summary>
        /// <param name="source">Tag sorgente</param>
        /// <returns>Tag clonato</returns>
        private static MetadataTagDefinition CloneTag(MetadataTagDefinition source)
        {
            MetadataTagDefinition result = new MetadataTagDefinition();
            result.Key = source.Key;
            result.Name = source.Name;
            result.Label = source.Label;
            result.Description = source.Description;
            result.ValueType = source.ValueType;
            result.InputKind = source.InputKind;
            result.Visibility = source.Visibility;
            result.TargetScopes = new List<MkvMetadataTargetScope>(source.TargetScopes);
            result.IsEditable = source.IsEditable;
            result.IsClearable = source.IsClearable;
            result.AllowedValues = MetadataInputOptionCloner.CloneList(source.AllowedValues);
            result.HelpKey = source.HelpKey;
            result.SortGroup = source.SortGroup;
            result.DefaultTargetTypeValue = source.DefaultTargetTypeValue;
            return result;
        }

        /// <summary>
        /// Verifica compatibilità tag/scope
        /// </summary>
        /// <param name="tag">Definizione tag</param>
        /// <param name="scope">Scope target</param>
        /// <returns>Vero se compatibile</returns>
        private static bool IsScopeCompatible(MetadataTagDefinition tag, MkvMetadataTargetScope scope)
        {
            for (int i = 0; i < tag.TargetScopes.Count; i++)
            {
                if (tag.TargetScopes[i] == scope)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Confronta due tag per ordinamento UI
        /// </summary>
        /// <param name="left">Tag sinistro</param>
        /// <param name="right">Tag destro</param>
        /// <returns>Risultato confronto</returns>
        private static int CompareTags(MetadataTagDefinition left, MetadataTagDefinition right)
        {
            int groupCompare = left.SortGroup.CompareTo(right.SortGroup);
            if (groupCompare != 0)
                return groupCompare;

            return string.Compare(left.Label, right.Label, StringComparison.CurrentCultureIgnoreCase);
        }

        /// <summary>
        /// Valida coerenza strutturale catalogo tag
        /// </summary>
        private static void ValidateCatalog()
        {
            Dictionary<string, bool> names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < s_tags.Count; i++)
            {
                MetadataTagDefinition tag = s_tags[i];
                if (tag == null || string.IsNullOrEmpty(tag.Name))
                    throw new InvalidOperationException("Metadata tag catalog contains an empty name");

                if (names.ContainsKey(tag.Name))
                    throw new InvalidOperationException("Duplicate metadata tag name: " + tag.Name);

                if (tag.InputKind == MetadataFieldInputKind.Select && (tag.AllowedValues == null || tag.AllowedValues.Count == 0))
                    throw new InvalidOperationException("Metadata select tag without options: " + tag.Name);

                if (tag.TargetScopes == null || tag.TargetScopes.Count == 0)
                    throw new InvalidOperationException("Metadata tag without target scopes: " + tag.Name);

                names[tag.Name] = true;
            }
        }

        #endregion
    }
}
