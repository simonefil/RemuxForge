using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Registro campi metadata disponibili per condizioni e operazioni
    /// </summary>
    public static class MetadataFieldRegistry
    {
        #region Variabili statiche

        /// <summary>
        /// Campi metadata registrati
        /// </summary>
        private static readonly List<MetadataFieldDefinition> s_fields;

        /// <summary>
        /// Indice campi per chiave
        /// </summary>
        private static readonly Dictionary<string, MetadataFieldDefinition> s_byKey;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore statico
        /// </summary>
        static MetadataFieldRegistry()
        {
            s_fields = BuildFields();
            s_byKey = new Dictionary<string, MetadataFieldDefinition>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < s_fields.Count; i++)
            {
                s_byKey[s_fields[i].Key] = s_fields[i];
            }

            ValidateCatalog();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce tutti i campi noti
        /// </summary>
        /// <returns>Lista campi</returns>
        public static List<MetadataFieldDefinition> GetAll()
        {
            List<MetadataFieldDefinition> result = new List<MetadataFieldDefinition>();
            for (int i = 0; i < s_fields.Count; i++)
            {
                result.Add(LocalizeField(s_fields[i]));
            }

            return result;
        }

        /// <summary>
        /// Restituisce campi editabili per scope
        /// </summary>
        /// <param name="scope">Scope target</param>
        /// <param name="includeAdvanced">Vero per includere campi avanzati</param>
        /// <returns>Lista campi editabili</returns>
        public static List<MetadataFieldDefinition> GetEditable(MkvMetadataTargetScope scope, bool includeAdvanced)
        {
            List<MetadataFieldDefinition> result = new List<MetadataFieldDefinition>();

            for (int i = 0; i < s_fields.Count; i++)
            {
                MetadataFieldDefinition field = s_fields[i];
                if (!field.IsEditable || field.EditPolicy == MetadataFieldEditPolicy.Blocked || field.RiskLevel == MetadataFieldRiskLevel.Dangerous)
                    continue;

                if (field.Visibility == MetadataFieldVisibility.Hidden || field.Visibility == MetadataFieldVisibility.Technical)
                    continue;

                if (!includeAdvanced && (field.EditPolicy == MetadataFieldEditPolicy.Advanced || field.Visibility == MetadataFieldVisibility.Advanced))
                    continue;

                if (IsScopeCompatible(field, scope))
                    result.Add(LocalizeField(field));
            }

            return result;
        }

        /// <summary>
        /// Restituisce campi leggibili filtrati per scope e visibilità
        /// </summary>
        /// <param name="scope">Scope target</param>
        /// <param name="includeAdvanced">Vero per includere campi avanzati</param>
        /// <param name="includeTechnical">Vero per includere campi tecnici</param>
        /// <returns>Lista campi leggibili</returns>
        public static List<MetadataFieldDefinition> GetReadable(MkvMetadataTargetScope scope, bool includeAdvanced, bool includeTechnical)
        {
            List<MetadataFieldDefinition> result = new List<MetadataFieldDefinition>();

            for (int i = 0; i < s_fields.Count; i++)
            {
                MetadataFieldDefinition field = s_fields[i];
                if (!field.IsReadable || field.Visibility == MetadataFieldVisibility.Hidden)
                    continue;

                if (!includeAdvanced && field.Visibility == MetadataFieldVisibility.Advanced)
                    continue;

                if (!includeTechnical && field.Visibility == MetadataFieldVisibility.Technical)
                    continue;

                if (MetadataScopeHelper.IsFieldReadableInScope(field, scope))
                    result.Add(LocalizeField(field));
            }

            return result;
        }

        /// <summary>
        /// Cerca un campo per chiave
        /// </summary>
        /// <param name="key">Chiave campo</param>
        /// <param name="field">Campo trovato</param>
        /// <returns>Vero se trovato</returns>
        public static bool TryGet(string key, out MetadataFieldDefinition field)
        {
            if (key == null)
            {
                field = null;
                return false;
            }

            if (s_byKey.TryGetValue(key.Trim(), out field))
            {
                field = LocalizeField(field);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Indica se il campo è bloccato per scrittura
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <returns>Vero se il campo è pericoloso o bloccato</returns>
        public static bool IsBlockedForWrite(string fieldKey)
        {
            MetadataFieldDefinition field;

            if (!TryGet(fieldKey, out field))
                return false;

            return field.RiskLevel == MetadataFieldRiskLevel.Dangerous || field.EditPolicy == MetadataFieldEditPolicy.Blocked || !field.IsEditable;
        }

        /// <summary>
        /// Valida che un campo sia scrivibile
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <param name="errorMessage">Errore</param>
        /// <returns>Vero se scrivibile</returns>
        public static bool ValidateWritable(string fieldKey, out string errorMessage)
        {
            MetadataFieldDefinition field;

            errorMessage = "";
            if (!TryGet(fieldKey, out field))
            {
                errorMessage = AppText.F("metadata.validation.unknownField", fieldKey);
                return false;
            }

            if (field.RiskLevel == MetadataFieldRiskLevel.Dangerous || field.EditPolicy == MetadataFieldEditPolicy.Blocked)
            {
                errorMessage = AppText.F("metadata.validation.dangerousFieldNotEditable", fieldKey);
                return false;
            }

            if (!field.IsEditable)
            {
                errorMessage = AppText.F("metadata.validation.fieldNotEditable", fieldKey);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Valida e normalizza un valore scrivibile per un campo
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <param name="value">Valore sorgente</param>
        /// <param name="allowEmpty">Vero se il valore vuoto è consentito</param>
        /// <param name="normalizedValue">Valore normalizzato</param>
        /// <param name="errorMessage">Errore validazione</param>
        /// <returns>Vero se il valore è valido</returns>
        public static bool ValidateWritableValue(string fieldKey, string value, bool allowEmpty, out string normalizedValue, out string errorMessage)
        {
            MetadataFieldDefinition field;

            normalizedValue = value != null ? value.Trim() : "";
            if (!ValidateWritable(fieldKey, out errorMessage))
                return false;

            if (!TryGet(fieldKey, out field))
            {
                errorMessage = AppText.F("metadata.validation.unknownField", fieldKey);
                return false;
            }

            return MetadataCatalogValueValidator.Validate(field.ValueType, field.InputKind, field.AllowedValues, field.Label, value, allowEmpty, out normalizedValue, out errorMessage);
        }

        /// <summary>
        /// Verifica compatibilità campo/scope
        /// </summary>
        /// <param name="fieldKey">Chiave campo</param>
        /// <param name="scope">Scope target</param>
        /// <returns>Vero se compatibile</returns>
        public static bool IsScopeCompatible(string fieldKey, MkvMetadataTargetScope scope)
        {
            MetadataFieldDefinition field;

            if (!TryGet(fieldKey, out field))
                return false;

            return IsScopeCompatible(field, scope);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Costruisce la lista completa dei campi metadata noti
        /// </summary>
        /// <returns>Lista campi metadata</returns>
        private static List<MetadataFieldDefinition> BuildFields()
        {
            List<MetadataFieldDefinition> fields = new List<MetadataFieldDefinition>();

            AddReadable(fields, "file_name", MetadataFieldSector.File, MetadataFieldValueType.String);
            AddReadable(fields, "file_stem", MetadataFieldSector.File, MetadataFieldValueType.String);
            AddReadable(fields, "file_extension", MetadataFieldSector.File, MetadataFieldValueType.String);
            AddReadable(fields, "file_path", MetadataFieldSector.File, MetadataFieldValueType.String);
            AddReadable(fields, "file_folder", MetadataFieldSector.File, MetadataFieldValueType.String);
            AddReadable(fields, "file_relative_folder", MetadataFieldSector.File, MetadataFieldValueType.String);
            AddReadable(fields, "file_size", MetadataFieldSector.File, MetadataFieldValueType.Bytes);
            AddReadable(fields, "general_format", MetadataFieldSector.Container, MetadataFieldValueType.String);
            AddReadable(fields, "general_format_version", MetadataFieldSector.Container, MetadataFieldValueType.String);
            AddReadable(fields, "general_duration", MetadataFieldSector.Container, MetadataFieldValueType.Duration);
            AddReadable(fields, "general_overall_bitrate", MetadataFieldSector.Container, MetadataFieldValueType.Integer);
            AddReadable(fields, "general_frame_rate", MetadataFieldSector.Container, MetadataFieldValueType.Decimal);
            AddReadable(fields, "general_frame_count", MetadataFieldSector.Container, MetadataFieldValueType.Integer);
            AddReadable(fields, "container_movie", MetadataFieldSector.Container, MetadataFieldValueType.String);
            AddReadable(fields, "container_collection", MetadataFieldSector.Container, MetadataFieldValueType.String);
            AddReadable(fields, "container_season", MetadataFieldSector.Container, MetadataFieldValueType.String);
            AddReadable(fields, "container_part", MetadataFieldSector.Container, MetadataFieldValueType.String);
            AddReadable(fields, "tagged_application", MetadataFieldSector.Container, MetadataFieldValueType.String);
            AddReadable(fields, "encoded_date", MetadataFieldSector.Container, MetadataFieldValueType.Date);
            AddReadable(fields, "tagged_date", MetadataFieldSector.Container, MetadataFieldValueType.Date);
            AddReadable(fields, "attachment_count", MetadataFieldSector.Container, MetadataFieldValueType.Integer);
            AddReadable(fields, "attachment_names", MetadataFieldSector.Container, MetadataFieldValueType.String);
            AddReadable(fields, "chapter_count", MetadataFieldSector.Container, MetadataFieldValueType.Integer);
            AddReadable(fields, "chapter_first_name", MetadataFieldSector.Container, MetadataFieldValueType.String);

            AddEditable(fields, "container_title", MetadataFieldSector.Container, MkvMetadataTargetScope.Container, MetadataFieldValueType.String, "title", true, MetadataFieldRiskLevel.Normal);
            AddEditable(fields, "container_date", MetadataFieldSector.Container, MkvMetadataTargetScope.Container, MetadataFieldValueType.Date, "date", true, MetadataFieldRiskLevel.Normal);
            AddEditable(fields, "segment_filename", MetadataFieldSector.Container, MkvMetadataTargetScope.Container, MetadataFieldValueType.String, "segment-filename", true, MetadataFieldRiskLevel.Advanced);
            AddEditable(fields, "prev_filename", MetadataFieldSector.Container, MkvMetadataTargetScope.Container, MetadataFieldValueType.String, "prev-filename", true, MetadataFieldRiskLevel.Advanced);
            AddEditable(fields, "next_filename", MetadataFieldSector.Container, MkvMetadataTargetScope.Container, MetadataFieldValueType.String, "next-filename", true, MetadataFieldRiskLevel.Advanced);
            AddEditable(fields, "muxing_application", MetadataFieldSector.Container, MkvMetadataTargetScope.Container, MetadataFieldValueType.String, "muxing-application", true, MetadataFieldRiskLevel.Advanced);
            AddEditable(fields, "writing_application", MetadataFieldSector.Container, MkvMetadataTargetScope.Container, MetadataFieldValueType.String, "writing-application", true, MetadataFieldRiskLevel.Advanced);
            AddBlocked(fields, "segment_uid", MetadataFieldSector.Container, MkvMetadataTargetScope.Container, MetadataFieldValueType.String, "segment-uid");
            AddBlocked(fields, "prev_uid", MetadataFieldSector.Container, MkvMetadataTargetScope.Container, MetadataFieldValueType.String, "prev-uid");
            AddBlocked(fields, "next_uid", MetadataFieldSector.Container, MkvMetadataTargetScope.Container, MetadataFieldValueType.String, "next-uid");

            AddVideoFields(fields);
            AddAudioFields(fields);
            AddSubtitleFields(fields);
            ApplyCatalogMetadata(fields);

            return fields;
        }

        /// <summary>
        /// Crea una copia localizzata della definizione campo
        /// </summary>
        /// <param name="source">Definizione campo sorgente</param>
        /// <returns>Definizione campo localizzata</returns>
        private static MetadataFieldDefinition LocalizeField(MetadataFieldDefinition source)
        {
            MetadataFieldDefinition result = new MetadataFieldDefinition();
            string labelKey = "metadata.field." + source.Key;
            string localizedLabel = AppText.T(labelKey);

            result.Key = source.Key;
            result.Label = localizedLabel == "[" + labelKey + "]" ? source.Label : localizedLabel;
            result.Description = source.Description;
            result.Sector = source.Sector;
            result.TargetScopes = new List<MkvMetadataTargetScope>(source.TargetScopes);
            result.ValueType = source.ValueType;
            result.InputKind = source.InputKind;
            result.Unit = source.Unit;
            result.IsReadable = source.IsReadable;
            result.IsEditable = source.IsEditable;
            result.IsClearable = source.IsClearable;
            result.RiskLevel = source.RiskLevel;
            result.EditPolicy = source.EditPolicy;
            result.Visibility = source.Visibility;
            result.MediaInfoFieldNames = new List<string>(source.MediaInfoFieldNames);
            result.MkvPropEditProperty = source.MkvPropEditProperty;
            result.MkvMergeArgument = source.MkvMergeArgument;
            result.AllowedValues = MetadataInputOptionCloner.CloneList(source.AllowedValues);
            result.HelpKey = source.HelpKey;
            result.SortGroup = source.SortGroup;
            result.RequiresRemux = source.RequiresRemux;

            return result;
        }

        /// <summary>
        /// Applica mappature MediaInfo, visibilità e input kind al catalogo
        /// </summary>
        /// <param name="fields">Campi da configurare</param>
        private static void ApplyCatalogMetadata(List<MetadataFieldDefinition> fields)
        {
            Configure(fields, "file_name", MetadataFieldVisibility.Primary);
            Configure(fields, "file_stem", MetadataFieldVisibility.Primary);
            Configure(fields, "file_extension", MetadataFieldVisibility.Technical);
            Configure(fields, "file_path", MetadataFieldVisibility.Technical);
            Configure(fields, "file_folder", MetadataFieldVisibility.Technical);
            Configure(fields, "file_relative_folder", MetadataFieldVisibility.Technical);
            Configure(fields, "file_size", MetadataFieldVisibility.Primary, MetadataFieldInputKind.SizeInput, "");

            Configure(fields, "general_format", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Format");
            Configure(fields, "general_format_version", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Format_Version");
            Configure(fields, "general_duration", MetadataFieldVisibility.Primary, MetadataFieldInputKind.DurationInput, "", "Duration");
            Configure(fields, "general_overall_bitrate", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "bps", "OverallBitRate");
            Configure(fields, "general_frame_rate", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "fps", "FrameRate");
            Configure(fields, "general_frame_count", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "", "FrameCount");
            Configure(fields, "container_movie", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Movie");
            Configure(fields, "container_collection", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Collection");
            Configure(fields, "container_season", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Season");
            Configure(fields, "container_part", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Part");
            Configure(fields, "tagged_application", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Text, "", "Tagged_Application");
            Configure(fields, "encoded_date", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.DateInput, "", "Encoded_Date");
            Configure(fields, "tagged_date", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.DateInput, "", "Tagged_Date");
            Configure(fields, "attachment_count", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Number, "");
            Configure(fields, "attachment_names", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Text, "");
            Configure(fields, "chapter_count", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Number, "");
            Configure(fields, "chapter_first_name", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Text, "");
            Configure(fields, "container_title", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Text, "", "Title");
            Configure(fields, "container_date", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.DateInput, "", "Recorded_Date", "Encoded_Date");
            Configure(fields, "segment_filename", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "SegmentFilename");
            Configure(fields, "prev_filename", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Text, "", "PreviousFilename");
            Configure(fields, "next_filename", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Text, "", "NextFilename");
            Configure(fields, "muxing_application", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Encoded_Application");
            Configure(fields, "writing_application", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Encoded_Library");
            Configure(fields, "segment_uid", MetadataFieldVisibility.Technical);
            Configure(fields, "prev_uid", MetadataFieldVisibility.Technical);
            Configure(fields, "next_uid", MetadataFieldVisibility.Technical);

            ApplyVideoCatalogMetadata(fields);
            ApplyAudioCatalogMetadata(fields);
            ApplySubtitleCatalogMetadata(fields);
            ApplyAllowedValues(fields);
        }

        /// <summary>
        /// Applica opzioni enum statiche ai campi che usano select
        /// </summary>
        /// <param name="fields">Campi da configurare</param>
        private static void ApplyAllowedValues(List<MetadataFieldDefinition> fields)
        {
            SetOptions(fields, "video_display_unit",
                Option("0", "Pixels"),
                Option("1", "Centimeters"),
                Option("2", "Inches"),
                Option("3", "Display aspect ratio"),
                Option("4", "Unknown"));

            SetOptions(fields, "video_aspect_ratio_type",
                Option("0", "Free resizing"),
                Option("1", "Keep aspect ratio"),
                Option("2", "Fixed"));

            SetOptions(fields, "video_interlaced",
                Option("0", "Undetermined"),
                Option("1", "Interlaced"),
                Option("2", "Progressive"));

            SetOptions(fields, "video_field_order",
                Option("0", "Progressive"),
                Option("1", "Top field first"),
                Option("2", "Undetermined"),
                Option("6", "Bottom field first"),
                Option("9", "Bottom field first, swapped"),
                Option("14", "Top field first, swapped"));

            SetOptions(fields, "video_stereo_mode",
                Option("0", "Mono"),
                Option("1", "Side by side, left eye first"),
                Option("2", "Top-bottom, right eye first"),
                Option("3", "Top-bottom, left eye first"),
                Option("4", "Checkerboard, right eye first"),
                Option("5", "Checkerboard, left eye first"),
                Option("6", "Row interleaved, right eye first"),
                Option("7", "Row interleaved, left eye first"),
                Option("8", "Column interleaved, right eye first"),
                Option("9", "Column interleaved, left eye first"),
                Option("10", "Anaglyph cyan/red"),
                Option("11", "Side by side, right eye first"),
                Option("12", "Anaglyph green/magenta"),
                Option("13", "Both eyes laced in one block, left eye first"),
                Option("14", "Both eyes laced in one block, right eye first"));

            SetOptions(fields, "video_alpha_mode",
                Option("0", "None"),
                Option("1", "Present"));

            SetOptions(fields, "video_matrix_coefficients",
                Option("0", "Identity"),
                Option("1", "ITU-R BT.709"),
                Option("2", "Unspecified"),
                Option("3", "Reserved"),
                Option("4", "US FCC 73.682"),
                Option("5", "ITU-R BT.470BG"),
                Option("6", "SMPTE 170M"),
                Option("7", "SMPTE 240M"),
                Option("8", "YCoCg"),
                Option("9", "BT.2020 non-constant luminance"),
                Option("10", "BT.2020 constant luminance"),
                Option("11", "SMPTE ST 2085"),
                Option("12", "Chroma-derived non-constant luminance"),
                Option("13", "Chroma-derived constant luminance"),
                Option("14", "ITU-R BT.2100-0"));

            SetOptions(fields, "video_chroma_siting_horizontal",
                Option("0", "Unspecified"),
                Option("1", "Left collocated"),
                Option("2", "Half"));

            SetOptions(fields, "video_chroma_siting_vertical",
                Option("0", "Unspecified"),
                Option("1", "Top collocated"),
                Option("2", "Half"));

            SetOptions(fields, "video_color_range",
                Option("0", "Unspecified"),
                Option("1", "Broadcast range"),
                Option("2", "Full range"),
                Option("3", "Defined by matrix/transfer"));

            SetOptions(fields, "video_transfer_characteristics",
                Option("0", "Reserved"),
                Option("1", "ITU-R BT.709"),
                Option("2", "Unspecified"),
                Option("3", "Reserved 2"),
                Option("4", "Gamma 2.2 curve, BT.470M"),
                Option("5", "Gamma 2.8 curve, BT.470BG"),
                Option("6", "SMPTE 170M"),
                Option("7", "SMPTE 240M"),
                Option("8", "Linear"),
                Option("9", "Log"),
                Option("10", "Log sqrt"),
                Option("11", "IEC 61966-2-4"),
                Option("12", "ITU-R BT.1361 extended colour gamut"),
                Option("13", "IEC 61966-2-1"),
                Option("14", "ITU-R BT.2020 10 bit"),
                Option("15", "ITU-R BT.2020 12 bit"),
                Option("16", "ITU-R BT.2100 perceptual quantization"),
                Option("17", "SMPTE ST 428-1"),
                Option("18", "ARIB STD-B67 HLG"));

            SetOptions(fields, "video_color_primaries",
                Option("0", "Reserved"),
                Option("1", "ITU-R BT.709"),
                Option("2", "Unspecified"),
                Option("3", "Reserved 2"),
                Option("4", "ITU-R BT.470M"),
                Option("5", "ITU-R BT.470BG / BT.601 625"),
                Option("6", "ITU-R BT.601 525 / SMPTE 170M"),
                Option("7", "SMPTE 240M"),
                Option("8", "Film"),
                Option("9", "ITU-R BT.2020"),
                Option("10", "SMPTE ST 428-1"),
                Option("11", "SMPTE RP 432-2"),
                Option("12", "SMPTE EG 432-2"),
                Option("22", "EBU Tech. 3213-E / JEDEC P22 phosphors"));

            SetOptions(fields, "video_projection_type",
                Option("0", "Rectangular"),
                Option("1", "Equirectangular"),
                Option("2", "Cubemap"),
                Option("3", "Mesh"));
        }

        /// <summary>
        /// Applica mappature e visibilità ai campi video
        /// </summary>
        /// <param name="fields">Campi da configurare</param>
        private static void ApplyVideoCatalogMetadata(List<MetadataFieldDefinition> fields)
        {
            Configure(fields, "video_title", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Text, "", "Title", "Name");
            Configure(fields, "video_language", MetadataFieldVisibility.Primary, MetadataFieldInputKind.LanguageSelect, "", "Language", "Language/String3");
            Configure(fields, "video_language_ietf", MetadataFieldVisibility.Hidden, MetadataFieldInputKind.LanguageIetf, "", "Language/String");
            BlockFieldWrite(fields, "video_language_ietf");
            Configure(fields, "video_default", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Boolean, "", "Default", "Default/String");
            Configure(fields, "video_forced", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Boolean, "", "Forced", "Forced/String");
            Configure(fields, "video_enabled", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Boolean, "", "Enabled", "Enabled/String");
            Configure(fields, "video_original", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Boolean, "", "Original", "Original/String");
            Configure(fields, "video_codec_name", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Codec", "Codec/String");
            Configure(fields, "video_alpha_mode", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "AlphaMode");
            Configure(fields, "video_pixel_width", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "Width");
            Configure(fields, "video_pixel_height", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "Height");
            Configure(fields, "video_display_width", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "DisplayWidth", "Display_Width");
            Configure(fields, "video_display_height", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "DisplayHeight", "Display_Height");
            Configure(fields, "video_display_unit", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "DisplayUnit");
            Configure(fields, "video_crop_left", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "PixelCropLeft");
            Configure(fields, "video_crop_top", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "PixelCropTop");
            Configure(fields, "video_crop_right", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "PixelCropRight");
            Configure(fields, "video_crop_bottom", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "PixelCropBottom");
            Configure(fields, "video_interlaced", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "Interlaced");
            Configure(fields, "video_aspect_ratio_type", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "AspectRatioType");
            Configure(fields, "video_field_order", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "FieldOrder");
            Configure(fields, "video_stereo_mode", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "StereoMode");
            Configure(fields, "video_matrix_coefficients", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "matrix_coefficients");
            Configure(fields, "video_color_bits_per_channel", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "bit", "BitDepth");
            Configure(fields, "video_chroma_subsample_horizontal", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "");
            Configure(fields, "video_chroma_subsample_vertical", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "");
            Configure(fields, "video_cb_subsample_horizontal", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "");
            Configure(fields, "video_cb_subsample_vertical", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "");
            Configure(fields, "video_chroma_siting_horizontal", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "ChromaSitingHorz");
            Configure(fields, "video_chroma_siting_vertical", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "ChromaSitingVert");
            Configure(fields, "video_color_range", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "colour_range");
            Configure(fields, "video_transfer_characteristics", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "transfer_characteristics");
            Configure(fields, "video_color_primaries", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "colour_primaries");
            Configure(fields, "video_max_content_light", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "cd/m²", "MaxCLL", "Maximum_Content_Light_Level");
            Configure(fields, "video_max_frame_light", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "cd/m²", "MaxFALL", "Maximum_FrameAverage_Light_Level");
            Configure(fields, "video_chromaticity_red_x", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "");
            Configure(fields, "video_chromaticity_red_y", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "");
            Configure(fields, "video_chromaticity_green_x", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "");
            Configure(fields, "video_chromaticity_green_y", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "");
            Configure(fields, "video_chromaticity_blue_x", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "");
            Configure(fields, "video_chromaticity_blue_y", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "");
            Configure(fields, "video_white_point_x", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "", "MasteringDisplay_WhitePointChromaticityX");
            Configure(fields, "video_white_point_y", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "", "MasteringDisplay_WhitePointChromaticityY");
            Configure(fields, "video_max_luminance", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "cd/m²", "MasteringDisplay_LuminanceMax", "MasteringDisplay_Luminance_Max");
            Configure(fields, "video_min_luminance", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "cd/m²", "MasteringDisplay_LuminanceMin", "MasteringDisplay_Luminance_Min");
            Configure(fields, "video_projection_type", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Select, "", "ProjectionType");
            Configure(fields, "video_projection_pose_yaw", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "deg", "ProjectionPoseYaw");
            Configure(fields, "video_projection_pose_pitch", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "deg", "ProjectionPosePitch");
            Configure(fields, "video_projection_pose_roll", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "deg", "ProjectionPoseRoll");
            Configure(fields, "video_projection_private", MetadataFieldVisibility.Technical);
            Configure(fields, "video_number", MetadataFieldVisibility.Technical);
            Configure(fields, "video_uid", MetadataFieldVisibility.Technical);
            Configure(fields, "video_codec_id", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Text, "", "CodecID");
            Configure(fields, "video_type", MetadataFieldVisibility.Technical);
            Configure(fields, "video_stream_order", MetadataFieldVisibility.Technical);
            Configure(fields, "video_index", MetadataFieldVisibility.Technical);
            Configure(fields, "video_selector", MetadataFieldVisibility.Technical);
            Configure(fields, "video_id", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Number, "", "ID");
            Configure(fields, "video_unique_id", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Text, "", "UniqueID");
            Configure(fields, "video_format", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Text, "", "Format");
            Configure(fields, "video_profile", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Format_Profile");
            Configure(fields, "video_level", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Format_Level", "Format_Level/String");
            Configure(fields, "video_tier", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Format_Tier");
            Configure(fields, "video_hdr_format", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "HDR_Format");
            Configure(fields, "video_hdr_profile", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "HDR_Format_Profile");
            Configure(fields, "video_hdr_level", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "HDR_Format_Level");
            Configure(fields, "video_hdr_settings", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "HDR_Format_Settings");
            Configure(fields, "video_hdr_compatibility", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "HDR_Format_Compatibility");
            Configure(fields, "video_width", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Number, "px", "Width");
            Configure(fields, "video_height", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Number, "px", "Height");
            Configure(fields, "video_stored_width", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "Stored_Width");
            Configure(fields, "video_stored_height", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "Stored_Height");
            Configure(fields, "video_sampled_width", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "Sampled_Width");
            Configure(fields, "video_sampled_height", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "Sampled_Height");
            Configure(fields, "video_dar", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Text, "", "DisplayAspectRatio", "DisplayAspectRatio/String");
            Configure(fields, "video_par", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "PixelAspectRatio", "PixelAspectRatio/String");
            Configure(fields, "video_active_width", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "Active_Width");
            Configure(fields, "video_active_height", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "Active_Height");
            Configure(fields, "video_active_dar", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Active_DisplayAspectRatio");
            Configure(fields, "video_rotation", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "", "Rotation");
            Configure(fields, "video_frame_rate_mode", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "FrameRate_Mode");
            Configure(fields, "video_fps", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Decimal, "fps", "FrameRate");
            Configure(fields, "video_fps_min", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "fps", "FrameRate_Minimum");
            Configure(fields, "video_fps_nominal", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "fps", "FrameRate_Nominal");
            Configure(fields, "video_fps_max", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "fps", "FrameRate_Maximum");
            Configure(fields, "video_fps_original", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "fps", "FrameRate_Original");
            Configure(fields, "video_color_space", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "ColorSpace");
            Configure(fields, "video_bitdepth", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Number, "bit", "BitDepth");
            Configure(fields, "video_duration", MetadataFieldVisibility.Primary, MetadataFieldInputKind.DurationInput, "", "Duration");
            Configure(fields, "video_bitrate", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "bps", "BitRate");
            Configure(fields, "video_bitrate_mode", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "BitRate_Mode");
            Configure(fields, "video_stream_size", MetadataFieldVisibility.Primary, MetadataFieldInputKind.SizeInput, "", "StreamSize", "StreamSize/String");
            Configure(fields, "video_frame_count", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "", "FrameCount");
            Configure(fields, "video_chroma_subsampling", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "ChromaSubsampling");
            Configure(fields, "video_chroma_position", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "ChromaSubsampling_Position");
            Configure(fields, "video_scan_type", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "ScanType", "Interlaced");
            Configure(fields, "video_scan_order", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "ScanOrder");
            Configure(fields, "video_compression_mode", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Compression_Mode");
            Configure(fields, "video_bits_per_pixel_frame", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "", "Bits-(Pixel*Frame)");
            Configure(fields, "video_mastering_display_primaries", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "MasteringDisplay_ColorPrimaries");
            Configure(fields, "video_mastering_display_luminance", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "MasteringDisplay_Luminance");
        }

        /// <summary>
        /// Applica mappature e visibilità ai campi audio
        /// </summary>
        /// <param name="fields">Campi da configurare</param>
        private static void ApplyAudioCatalogMetadata(List<MetadataFieldDefinition> fields)
        {
            Configure(fields, "audio_title", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Text, "", "Title", "Name");
            Configure(fields, "audio_language", MetadataFieldVisibility.Primary, MetadataFieldInputKind.LanguageSelect, "", "Language", "Language/String3");
            Configure(fields, "audio_language_ietf", MetadataFieldVisibility.Hidden, MetadataFieldInputKind.LanguageIetf, "", "Language/String");
            BlockFieldWrite(fields, "audio_language_ietf");
            Configure(fields, "audio_default", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Boolean, "", "Default", "Default/String");
            Configure(fields, "audio_forced", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Boolean, "", "Forced", "Forced/String");
            Configure(fields, "audio_enabled", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Boolean, "", "Enabled", "Enabled/String");
            Configure(fields, "audio_commentary", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Boolean, "", "Commentary", "Commentary/String");
            Configure(fields, "audio_hearing_impaired", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Boolean, "", "HearingImpaired", "HearingImpaired/String");
            Configure(fields, "audio_visual_impaired", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Boolean, "", "VisualImpaired", "VisualImpaired/String");
            Configure(fields, "audio_original", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Boolean, "", "Original", "Original/String");
            Configure(fields, "audio_codec_name", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Codec", "Codec/String");
            Configure(fields, "audio_sampling_rate", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Decimal, "Hz", "SamplingRate");
            Configure(fields, "audio_output_sampling_rate", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "Hz", "OutputSamplingRate", "Output_SamplingRate");
            Configure(fields, "audio_channels", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Number, "", "Channels", "Channel(s)");
            Configure(fields, "audio_bitdepth", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Number, "bit", "BitDepth");
            Configure(fields, "audio_emphasis", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "", "Emphasis");
            Configure(fields, "audio_number", MetadataFieldVisibility.Technical);
            Configure(fields, "audio_uid", MetadataFieldVisibility.Technical);
            Configure(fields, "audio_codec_id", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Text, "", "CodecID");
            Configure(fields, "audio_type", MetadataFieldVisibility.Technical);
            Configure(fields, "audio_stream_order", MetadataFieldVisibility.Technical);
            Configure(fields, "audio_index", MetadataFieldVisibility.Technical);
            Configure(fields, "audio_selector", MetadataFieldVisibility.Technical);
            Configure(fields, "audio_id", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Number, "", "ID");
            Configure(fields, "audio_unique_id", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Text, "", "UniqueID");
            Configure(fields, "audio_format", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Text, "", "Format");
            Configure(fields, "audio_format_commercial", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Format_Commercial", "Format_Commercial_IfAny", "Format/String");
            Configure(fields, "audio_profile", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Format_Profile");
            Configure(fields, "audio_codec_description", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "CodecID_Description", "CodecID/Info");
            Configure(fields, "audio_format_settings", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Format_Settings");
            Configure(fields, "audio_channels_label", MetadataFieldVisibility.Primary);
            Configure(fields, "audio_channel_positions", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "ChannelPositions");
            Configure(fields, "audio_sampling_rate_khz", MetadataFieldVisibility.Primary);
            Configure(fields, "audio_sampling_count", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "", "SamplesCount", "SamplingCount");
            Configure(fields, "audio_bitdepth_detected", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Number, "bit", "BitDepth_Detected");
            Configure(fields, "audio_bitdepth_stored", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Number, "bit", "BitDepth_Stored");
            Configure(fields, "audio_bitrate", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "bps", "BitRate");
            Configure(fields, "audio_bitrate_mode", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "BitRate_Mode");
            Configure(fields, "audio_compression_mode", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Compression_Mode");
            Configure(fields, "audio_duration", MetadataFieldVisibility.Primary, MetadataFieldInputKind.DurationInput, "", "Duration");
            Configure(fields, "audio_stream_size", MetadataFieldVisibility.Primary, MetadataFieldInputKind.SizeInput, "", "StreamSize", "StreamSize/String");
            Configure(fields, "audio_frame_count", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "", "FrameCount");
            Configure(fields, "audio_channel_layout", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "ChannelLayout");
            Configure(fields, "audio_delay", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.DurationInput, "", "Delay", "Delay/String");
            Configure(fields, "audio_video_delay", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.DurationInput, "", "Video_Delay", "Video_Delay/String");
            Configure(fields, "audio_replaygain_gain", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "", "ReplayGain_Gain");
            Configure(fields, "audio_replaygain_peak", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "", "ReplayGain_Peak");
            Configure(fields, "audio_service_kind", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "ServiceKind");
            Configure(fields, "audio_quality", MetadataFieldVisibility.Primary);
        }

        /// <summary>
        /// Applica mappature e visibilità ai campi sottotitoli
        /// </summary>
        /// <param name="fields">Campi da configurare</param>
        private static void ApplySubtitleCatalogMetadata(List<MetadataFieldDefinition> fields)
        {
            Configure(fields, "subtitle_title", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Text, "", "Title", "Name");
            Configure(fields, "subtitle_language", MetadataFieldVisibility.Primary, MetadataFieldInputKind.LanguageSelect, "", "Language", "Language/String3");
            Configure(fields, "subtitle_language_ietf", MetadataFieldVisibility.Hidden, MetadataFieldInputKind.LanguageIetf, "", "Language/String");
            BlockFieldWrite(fields, "subtitle_language_ietf");
            Configure(fields, "subtitle_default", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Boolean, "", "Default", "Default/String");
            Configure(fields, "subtitle_forced", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Boolean, "", "Forced", "Forced/String");
            Configure(fields, "subtitle_enabled", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Boolean, "", "Enabled", "Enabled/String");
            Configure(fields, "subtitle_hearing_impaired", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Boolean, "", "HearingImpaired", "HearingImpaired/String");
            Configure(fields, "subtitle_text_descriptions", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Boolean, "", "TextDescriptions", "TextDescriptions/String");
            Configure(fields, "subtitle_original", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Boolean, "", "Original", "Original/String");
            Configure(fields, "subtitle_codec_name", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Codec", "Codec/String");
            Configure(fields, "subtitle_number", MetadataFieldVisibility.Technical);
            Configure(fields, "subtitle_uid", MetadataFieldVisibility.Technical);
            Configure(fields, "subtitle_codec_id", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Text, "", "CodecID");
            Configure(fields, "subtitle_type", MetadataFieldVisibility.Technical);
            Configure(fields, "subtitle_stream_order", MetadataFieldVisibility.Technical);
            Configure(fields, "subtitle_index", MetadataFieldVisibility.Technical);
            Configure(fields, "subtitle_selector", MetadataFieldVisibility.Technical);
            Configure(fields, "subtitle_id", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Number, "", "ID");
            Configure(fields, "subtitle_unique_id", MetadataFieldVisibility.Technical, MetadataFieldInputKind.Text, "", "UniqueID");
            Configure(fields, "subtitle_format", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Text, "", "Format");
            Configure(fields, "subtitle_format_commercial", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Format_Commercial", "Format_Commercial_IfAny", "Format/String");
            Configure(fields, "subtitle_codec_description", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "CodecID_Description", "CodecID/Info");
            Configure(fields, "subtitle_muxing_mode", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "MuxingMode");
            Configure(fields, "subtitle_stream_size", MetadataFieldVisibility.Primary, MetadataFieldInputKind.SizeInput, "", "StreamSize", "StreamSize/String");
            Configure(fields, "subtitle_element_count", MetadataFieldVisibility.Primary, MetadataFieldInputKind.Number, "", "ElementCount");
            Configure(fields, "subtitle_duration", MetadataFieldVisibility.Primary, MetadataFieldInputKind.DurationInput, "", "Duration");
            Configure(fields, "subtitle_duration_start", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.DurationInput, "", "Duration_Start");
            Configure(fields, "subtitle_duration_end", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.DurationInput, "", "Duration_End");
            Configure(fields, "subtitle_bitrate", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "bps", "BitRate");
            Configure(fields, "subtitle_width", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "Width");
            Configure(fields, "subtitle_height", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "px", "Height");
            Configure(fields, "subtitle_dar", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "DisplayAspectRatio", "DisplayAspectRatio/String");
            Configure(fields, "subtitle_frame_rate_mode", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "FrameRate_Mode");
            Configure(fields, "subtitle_fps", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Decimal, "fps", "FrameRate");
            Configure(fields, "subtitle_frame_count", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "", "FrameCount");
            Configure(fields, "subtitle_format_profile", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Format_Profile");
            Configure(fields, "subtitle_compression_mode", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Text, "", "Compression_Mode");
            Configure(fields, "subtitle_events_total", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "", "Events_Total");
            Configure(fields, "subtitle_events_min_duration", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.DurationInput, "", "Events_MinDuration");
            Configure(fields, "subtitle_lines_count", MetadataFieldVisibility.Advanced, MetadataFieldInputKind.Number, "", "Lines_Count");
        }

        /// <summary>
        /// Aggiunge i campi video al registro
        /// </summary>
        /// <param name="fields">Lista campi da popolare</param>
        private static void AddVideoFields(List<MetadataFieldDefinition> fields)
        {
            AddTrackEditable(fields, "video_title", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.String, "name", true, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_language", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.Language, "language", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_language_ietf", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.LanguageIetf, "language-ietf", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_default", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.Boolean, "flag-default", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_forced", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.Boolean, "flag-forced", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_enabled", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.Boolean, "flag-enabled", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_original", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.Boolean, "flag-original", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_codec_name", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.String, "codec-name", true, MetadataFieldRiskLevel.Advanced);
            AddVideoEditable(fields, "video_alpha_mode", MetadataFieldValueType.Boolean, "alpha-mode");
            AddVideoEditable(fields, "video_pixel_width", MetadataFieldValueType.Integer, "pixel-width");
            AddVideoEditable(fields, "video_pixel_height", MetadataFieldValueType.Integer, "pixel-height");
            AddVideoEditable(fields, "video_display_width", MetadataFieldValueType.Integer, "display-width");
            AddVideoEditable(fields, "video_display_height", MetadataFieldValueType.Integer, "display-height");
            AddVideoEditable(fields, "video_display_unit", MetadataFieldValueType.Integer, "display-unit");
            AddVideoEditable(fields, "video_crop_left", MetadataFieldValueType.Integer, "pixel-crop-left");
            AddVideoEditable(fields, "video_crop_top", MetadataFieldValueType.Integer, "pixel-crop-top");
            AddVideoEditable(fields, "video_crop_right", MetadataFieldValueType.Integer, "pixel-crop-right");
            AddVideoEditable(fields, "video_crop_bottom", MetadataFieldValueType.Integer, "pixel-crop-bottom");
            AddVideoEditable(fields, "video_interlaced", MetadataFieldValueType.Integer, "interlaced");
            AddVideoEditable(fields, "video_aspect_ratio_type", MetadataFieldValueType.Integer, "aspect-ratio-type");
            AddVideoEditable(fields, "video_field_order", MetadataFieldValueType.Integer, "field-order");
            AddVideoEditable(fields, "video_stereo_mode", MetadataFieldValueType.Integer, "stereo-mode");
            AddVideoEditable(fields, "video_matrix_coefficients", MetadataFieldValueType.Integer, "color-matrix-coefficients");
            AddVideoEditable(fields, "video_color_bits_per_channel", MetadataFieldValueType.Integer, "color-bits-per-channel");
            AddVideoEditable(fields, "video_chroma_subsample_horizontal", MetadataFieldValueType.Integer, "chroma-subsample-horizontal");
            AddVideoEditable(fields, "video_chroma_subsample_vertical", MetadataFieldValueType.Integer, "chroma-subsample-vertical");
            AddVideoEditable(fields, "video_cb_subsample_horizontal", MetadataFieldValueType.Integer, "cb-subsample-horizontal");
            AddVideoEditable(fields, "video_cb_subsample_vertical", MetadataFieldValueType.Integer, "cb-subsample-vertical");
            AddVideoEditable(fields, "video_chroma_siting_horizontal", MetadataFieldValueType.Integer, "chroma-siting-horizontal");
            AddVideoEditable(fields, "video_chroma_siting_vertical", MetadataFieldValueType.Integer, "chroma-siting-vertical");
            AddVideoEditable(fields, "video_color_range", MetadataFieldValueType.Integer, "color-range");
            AddVideoEditable(fields, "video_transfer_characteristics", MetadataFieldValueType.Integer, "color-transfer-characteristics");
            AddVideoEditable(fields, "video_color_primaries", MetadataFieldValueType.Integer, "color-primaries");
            AddVideoEditable(fields, "video_max_content_light", MetadataFieldValueType.Integer, "max-content-light");
            AddVideoEditable(fields, "video_max_frame_light", MetadataFieldValueType.Integer, "max-frame-light");
            AddVideoEditable(fields, "video_chromaticity_red_x", MetadataFieldValueType.Decimal, "chromaticity-coordinates-red-x");
            AddVideoEditable(fields, "video_chromaticity_red_y", MetadataFieldValueType.Decimal, "chromaticity-coordinates-red-y");
            AddVideoEditable(fields, "video_chromaticity_green_x", MetadataFieldValueType.Decimal, "chromaticity-coordinates-green-x");
            AddVideoEditable(fields, "video_chromaticity_green_y", MetadataFieldValueType.Decimal, "chromaticity-coordinates-green-y");
            AddVideoEditable(fields, "video_chromaticity_blue_x", MetadataFieldValueType.Decimal, "chromaticity-coordinates-blue-x");
            AddVideoEditable(fields, "video_chromaticity_blue_y", MetadataFieldValueType.Decimal, "chromaticity-coordinates-blue-y");
            AddVideoEditable(fields, "video_white_point_x", MetadataFieldValueType.Decimal, "white-coordinates-x");
            AddVideoEditable(fields, "video_white_point_y", MetadataFieldValueType.Decimal, "white-coordinates-y");
            AddVideoEditable(fields, "video_max_luminance", MetadataFieldValueType.Decimal, "max-luminance");
            AddVideoEditable(fields, "video_min_luminance", MetadataFieldValueType.Decimal, "min-luminance");
            AddVideoEditable(fields, "video_projection_type", MetadataFieldValueType.Integer, "projection-type");
            AddVideoEditable(fields, "video_projection_pose_yaw", MetadataFieldValueType.Decimal, "projection-pose-yaw");
            AddVideoEditable(fields, "video_projection_pose_pitch", MetadataFieldValueType.Decimal, "projection-pose-pitch");
            AddVideoEditable(fields, "video_projection_pose_roll", MetadataFieldValueType.Decimal, "projection-pose-roll");
            AddBlocked(fields, "video_projection_private", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.String, "projection-private");
            AddBlockedTrack(fields, "video_number", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.Integer, "track-number");
            AddBlockedTrack(fields, "video_uid", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.String, "track-uid");
            AddBlockedTrack(fields, "video_codec_id", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.String, "codec-id");

            AddReadable(fields, "video_type", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_stream_order", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_index", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_selector", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_id", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_unique_id", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_format", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_profile", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_level", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_tier", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_hdr_format", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_hdr_profile", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_hdr_level", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_hdr_settings", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_hdr_compatibility", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_width", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_height", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_stored_width", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_stored_height", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_sampled_width", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_sampled_height", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_dar", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_par", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_active_width", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_active_height", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_active_dar", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_rotation", MetadataFieldSector.Video, MetadataFieldValueType.Decimal);
            AddReadable(fields, "video_frame_rate_mode", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_fps", MetadataFieldSector.Video, MetadataFieldValueType.Decimal);
            AddReadable(fields, "video_fps_min", MetadataFieldSector.Video, MetadataFieldValueType.Decimal);
            AddReadable(fields, "video_fps_nominal", MetadataFieldSector.Video, MetadataFieldValueType.Decimal);
            AddReadable(fields, "video_fps_max", MetadataFieldSector.Video, MetadataFieldValueType.Decimal);
            AddReadable(fields, "video_fps_original", MetadataFieldSector.Video, MetadataFieldValueType.Decimal);
            AddReadable(fields, "video_color_space", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_bitdepth", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_duration", MetadataFieldSector.Video, MetadataFieldValueType.Duration);
            AddReadable(fields, "video_bitrate", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_bitrate_mode", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_stream_size", MetadataFieldSector.Video, MetadataFieldValueType.Bytes);
            AddReadable(fields, "video_frame_count", MetadataFieldSector.Video, MetadataFieldValueType.Integer);
            AddReadable(fields, "video_chroma_subsampling", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_chroma_position", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_scan_type", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_scan_order", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_compression_mode", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_bits_per_pixel_frame", MetadataFieldSector.Video, MetadataFieldValueType.Decimal);
            AddReadable(fields, "video_mastering_display_primaries", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_mastering_display_luminance", MetadataFieldSector.Video, MetadataFieldValueType.String);
        }

        /// <summary>
        /// Aggiunge i campi audio al registro
        /// </summary>
        /// <param name="fields">Lista campi da popolare</param>
        private static void AddAudioFields(List<MetadataFieldDefinition> fields)
        {
            AddTrackEditable(fields, "audio_title", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.String, "name", true, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_language", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Language, "language", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_language_ietf", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.LanguageIetf, "language-ietf", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_default", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-default", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_forced", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-forced", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_enabled", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-enabled", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_commentary", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-commentary", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_hearing_impaired", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-hearing-impaired", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_visual_impaired", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-visual-impaired", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_original", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-original", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_codec_name", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.String, "codec-name", true, MetadataFieldRiskLevel.Advanced);
            AddAudioEditable(fields, "audio_sampling_rate", MetadataFieldValueType.Decimal, "sampling-frequency");
            AddAudioEditable(fields, "audio_output_sampling_rate", MetadataFieldValueType.Decimal, "output-sampling-frequency");
            AddAudioEditable(fields, "audio_channels", MetadataFieldValueType.Integer, "channels");
            AddAudioEditable(fields, "audio_bitdepth", MetadataFieldValueType.Integer, "bit-depth");
            AddAudioEditable(fields, "audio_emphasis", MetadataFieldValueType.Integer, "emphasis");
            AddBlockedTrack(fields, "audio_number", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Integer, "track-number");
            AddBlockedTrack(fields, "audio_uid", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.String, "track-uid");
            AddBlockedTrack(fields, "audio_codec_id", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.String, "codec-id");

            AddReadable(fields, "audio_type", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_stream_order", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_index", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_selector", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_id", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_unique_id", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_format", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_format_commercial", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_profile", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_codec_description", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_format_settings", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_channels_label", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_channel_positions", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_sampling_rate_khz", MetadataFieldSector.Audio, MetadataFieldValueType.Decimal);
            AddReadable(fields, "audio_sampling_count", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_bitdepth_detected", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_bitdepth_stored", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_bitrate", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_bitrate_mode", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_compression_mode", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_duration", MetadataFieldSector.Audio, MetadataFieldValueType.Duration);
            AddReadable(fields, "audio_stream_size", MetadataFieldSector.Audio, MetadataFieldValueType.Bytes);
            AddReadable(fields, "audio_frame_count", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_channel_layout", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_delay", MetadataFieldSector.Audio, MetadataFieldValueType.Duration);
            AddReadable(fields, "audio_video_delay", MetadataFieldSector.Audio, MetadataFieldValueType.Duration);
            AddReadable(fields, "audio_replaygain_gain", MetadataFieldSector.Audio, MetadataFieldValueType.Decimal);
            AddReadable(fields, "audio_replaygain_peak", MetadataFieldSector.Audio, MetadataFieldValueType.Decimal);
            AddReadable(fields, "audio_service_kind", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_quality", MetadataFieldSector.Audio, MetadataFieldValueType.String);
        }

        /// <summary>
        /// Aggiunge i campi sottotitoli al registro
        /// </summary>
        /// <param name="fields">Lista campi da popolare</param>
        private static void AddSubtitleFields(List<MetadataFieldDefinition> fields)
        {
            AddTrackEditable(fields, "subtitle_title", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.String, "name", true, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "subtitle_language", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.Language, "language", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "subtitle_language_ietf", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.LanguageIetf, "language-ietf", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "subtitle_default", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.Boolean, "flag-default", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "subtitle_forced", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.Boolean, "flag-forced", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "subtitle_enabled", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.Boolean, "flag-enabled", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "subtitle_hearing_impaired", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.Boolean, "flag-hearing-impaired", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "subtitle_text_descriptions", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.Boolean, "flag-text-descriptions", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "subtitle_original", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.Boolean, "flag-original", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "subtitle_codec_name", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.String, "codec-name", true, MetadataFieldRiskLevel.Advanced);
            AddBlockedTrack(fields, "subtitle_number", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.Integer, "track-number");
            AddBlockedTrack(fields, "subtitle_uid", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.String, "track-uid");
            AddBlockedTrack(fields, "subtitle_codec_id", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.String, "codec-id");

            AddReadable(fields, "subtitle_type", MetadataFieldSector.Subtitle, MetadataFieldValueType.String);
            AddReadable(fields, "subtitle_stream_order", MetadataFieldSector.Subtitle, MetadataFieldValueType.Integer);
            AddReadable(fields, "subtitle_index", MetadataFieldSector.Subtitle, MetadataFieldValueType.Integer);
            AddReadable(fields, "subtitle_selector", MetadataFieldSector.Subtitle, MetadataFieldValueType.String);
            AddReadable(fields, "subtitle_id", MetadataFieldSector.Subtitle, MetadataFieldValueType.Integer);
            AddReadable(fields, "subtitle_unique_id", MetadataFieldSector.Subtitle, MetadataFieldValueType.String);
            AddReadable(fields, "subtitle_format", MetadataFieldSector.Subtitle, MetadataFieldValueType.String);
            AddReadable(fields, "subtitle_format_commercial", MetadataFieldSector.Subtitle, MetadataFieldValueType.String);
            AddReadable(fields, "subtitle_codec_description", MetadataFieldSector.Subtitle, MetadataFieldValueType.String);
            AddReadable(fields, "subtitle_muxing_mode", MetadataFieldSector.Subtitle, MetadataFieldValueType.String);
            AddReadable(fields, "subtitle_stream_size", MetadataFieldSector.Subtitle, MetadataFieldValueType.Bytes);
            AddReadable(fields, "subtitle_element_count", MetadataFieldSector.Subtitle, MetadataFieldValueType.Integer);
            AddReadable(fields, "subtitle_duration", MetadataFieldSector.Subtitle, MetadataFieldValueType.Duration);
            AddReadable(fields, "subtitle_duration_start", MetadataFieldSector.Subtitle, MetadataFieldValueType.Duration);
            AddReadable(fields, "subtitle_duration_end", MetadataFieldSector.Subtitle, MetadataFieldValueType.Duration);
            AddReadable(fields, "subtitle_bitrate", MetadataFieldSector.Subtitle, MetadataFieldValueType.Integer);
            AddReadable(fields, "subtitle_width", MetadataFieldSector.Subtitle, MetadataFieldValueType.Integer);
            AddReadable(fields, "subtitle_height", MetadataFieldSector.Subtitle, MetadataFieldValueType.Integer);
            AddReadable(fields, "subtitle_dar", MetadataFieldSector.Subtitle, MetadataFieldValueType.String);
            AddReadable(fields, "subtitle_frame_rate_mode", MetadataFieldSector.Subtitle, MetadataFieldValueType.String);
            AddReadable(fields, "subtitle_fps", MetadataFieldSector.Subtitle, MetadataFieldValueType.Decimal);
            AddReadable(fields, "subtitle_frame_count", MetadataFieldSector.Subtitle, MetadataFieldValueType.Integer);
            AddReadable(fields, "subtitle_format_profile", MetadataFieldSector.Subtitle, MetadataFieldValueType.String);
            AddReadable(fields, "subtitle_compression_mode", MetadataFieldSector.Subtitle, MetadataFieldValueType.String);
            AddReadable(fields, "subtitle_events_total", MetadataFieldSector.Subtitle, MetadataFieldValueType.Integer);
            AddReadable(fields, "subtitle_events_min_duration", MetadataFieldSector.Subtitle, MetadataFieldValueType.Duration);
            AddReadable(fields, "subtitle_lines_count", MetadataFieldSector.Subtitle, MetadataFieldValueType.Integer);
        }

        /// <summary>
        /// Configura visibilità e input kind usando il tipo valore del campo
        /// </summary>
        /// <param name="fields">Lista campi</param>
        /// <param name="key">Chiave campo</param>
        /// <param name="visibility">Visibilità UI</param>
        private static void Configure(List<MetadataFieldDefinition> fields, string key, MetadataFieldVisibility visibility)
        {
            MetadataFieldDefinition field = FindField(fields, key);
            if (field == null)
                return;

            field.Visibility = visibility;
            field.InputKind = ResolveInputKind(field.ValueType);
            field.Unit = "";
            field.MediaInfoFieldNames.Clear();
        }

        /// <summary>
        /// Configura visibilità, input kind, unità e sorgenti MediaInfo
        /// </summary>
        /// <param name="fields">Lista campi</param>
        /// <param name="key">Chiave campo</param>
        /// <param name="visibility">Visibilità UI</param>
        /// <param name="inputKind">Tipo input suggerito</param>
        /// <param name="unit">Unità di misura</param>
        /// <param name="mediaInfoFieldNames">Nomi campo MediaInfo candidati</param>
        private static void Configure(List<MetadataFieldDefinition> fields, string key, MetadataFieldVisibility visibility, MetadataFieldInputKind inputKind, string unit, params string[] mediaInfoFieldNames)
        {
            MetadataFieldDefinition field = FindField(fields, key);
            if (field == null)
                return;

            field.Visibility = visibility;
            field.InputKind = inputKind;
            field.Unit = unit != null ? unit : "";
            field.MediaInfoFieldNames.Clear();
            if (mediaInfoFieldNames == null)
                return;

            for (int i = 0; i < mediaInfoFieldNames.Length; i++)
            {
                if (!string.IsNullOrEmpty(mediaInfoFieldNames[i]))
                    field.MediaInfoFieldNames.Add(mediaInfoFieldNames[i]);
            }
        }

        /// <summary>
        /// Blocca un campo mantenendolo disponibile solo come dato interno letto da MediaInfo
        /// </summary>
        /// <param name="fields">Lista campi</param>
        /// <param name="key">Chiave campo</param>
        private static void BlockFieldWrite(List<MetadataFieldDefinition> fields, string key)
        {
            MetadataFieldDefinition field = FindField(fields, key);
            if (field == null)
                return;

            field.IsEditable = false;
            field.IsClearable = false;
            field.EditPolicy = MetadataFieldEditPolicy.Blocked;
        }

        /// <summary>
        /// Cerca un campo nella lista in costruzione
        /// </summary>
        /// <param name="fields">Lista campi</param>
        /// <param name="key">Chiave campo</param>
        /// <returns>Campo trovato o null</returns>
        private static MetadataFieldDefinition FindField(List<MetadataFieldDefinition> fields, string key)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                if (string.Equals(fields[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return fields[i];
            }

            return null;
        }

        /// <summary>
        /// Crea una opzione input
        /// </summary>
        /// <param name="value">Valore serializzato</param>
        /// <param name="label">Label visualizzata</param>
        /// <returns>Opzione input</returns>
        private static MetadataInputOption Option(string value, string label)
        {
            return new MetadataInputOption(value, label);
        }

        /// <summary>
        /// Assegna opzioni enum statiche a un campo
        /// </summary>
        /// <param name="fields">Lista campi</param>
        /// <param name="key">Chiave campo</param>
        /// <param name="options">Opzioni consentite</param>
        private static void SetOptions(List<MetadataFieldDefinition> fields, string key, params MetadataInputOption[] options)
        {
            MetadataFieldDefinition field = FindField(fields, key);
            if (field == null)
                return;

            field.AllowedValues.Clear();
            if (options == null)
                return;

            for (int i = 0; i < options.Length; i++)
            {
                if (options[i] != null)
                    field.AllowedValues.Add(options[i]);
            }
        }

        /// <summary>
        /// Valida coerenza strutturale del catalogo campi
        /// </summary>
        private static void ValidateCatalog()
        {
            Dictionary<string, bool> keys = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < s_fields.Count; i++)
            {
                MetadataFieldDefinition field = s_fields[i];
                if (field == null || string.IsNullOrEmpty(field.Key))
                    throw new InvalidOperationException("Metadata field catalog contains an empty key");

                if (keys.ContainsKey(field.Key))
                    throw new InvalidOperationException("Duplicate metadata field key: " + field.Key);

                keys[field.Key] = true;

                if (field.InputKind == MetadataFieldInputKind.Select && (field.AllowedValues == null || field.AllowedValues.Count == 0))
                    throw new InvalidOperationException("Metadata select field without options: " + field.Key);

                if (field.IsEditable && field.RiskLevel == MetadataFieldRiskLevel.Dangerous)
                    throw new InvalidOperationException("Dangerous metadata field cannot be editable: " + field.Key);

                if (field.IsEditable && string.IsNullOrEmpty(field.MkvPropEditProperty) && string.IsNullOrEmpty(field.MkvMergeArgument))
                    throw new InvalidOperationException("Editable metadata field without writer binding: " + field.Key);
            }
        }

        /// <summary>
        /// Ricava il tipo input UI dal tipo valore metadata
        /// </summary>
        /// <param name="valueType">Tipo valore metadata</param>
        /// <returns>Tipo input UI</returns>
        private static MetadataFieldInputKind ResolveInputKind(MetadataFieldValueType valueType)
        {
            switch (valueType)
            {
                case MetadataFieldValueType.Integer:
                    return MetadataFieldInputKind.Number;

                case MetadataFieldValueType.Decimal:
                    return MetadataFieldInputKind.Decimal;

                case MetadataFieldValueType.Boolean:
                    return MetadataFieldInputKind.Boolean;

                case MetadataFieldValueType.Duration:
                    return MetadataFieldInputKind.DurationInput;

                case MetadataFieldValueType.Bytes:
                    return MetadataFieldInputKind.SizeInput;

                case MetadataFieldValueType.Language:
                    return MetadataFieldInputKind.LanguageSelect;

                case MetadataFieldValueType.LanguageIetf:
                    return MetadataFieldInputKind.LanguageIetf;

                case MetadataFieldValueType.Date:
                    return MetadataFieldInputKind.DateInput;

                default:
                    return MetadataFieldInputKind.Text;
            }
        }

        /// <summary>
        /// Aggiunge un campo in sola lettura
        /// </summary>
        /// <param name="fields">Lista campi da popolare</param>
        /// <param name="key">Chiave campo</param>
        /// <param name="sector">Settore metadata</param>
        /// <param name="valueType">Tipo valore</param>
        private static void AddReadable(List<MetadataFieldDefinition> fields, string key, MetadataFieldSector sector, MetadataFieldValueType valueType)
        {
            MetadataFieldDefinition field = new MetadataFieldDefinition();
            field.Key = key;
            field.Label = key;
            field.Sector = sector;
            field.ValueType = valueType;
            field.InputKind = ResolveInputKind(valueType);
            field.IsReadable = true;
            field.IsEditable = false;
            field.IsClearable = false;
            field.EditPolicy = MetadataFieldEditPolicy.ReadOnly;
            field.RiskLevel = MetadataFieldRiskLevel.Normal;
            fields.Add(field);
        }

        /// <summary>
        /// Aggiunge un campo editabile container o traccia
        /// </summary>
        /// <param name="fields">Lista campi da popolare</param>
        /// <param name="key">Chiave campo</param>
        /// <param name="sector">Settore metadata</param>
        /// <param name="scope">Scope target</param>
        /// <param name="valueType">Tipo valore</param>
        /// <param name="property">Proprietà mkvpropedit</param>
        /// <param name="clearable">Vero se il campo può essere svuotato</param>
        /// <param name="risk">Livello di rischio</param>
        private static void AddEditable(List<MetadataFieldDefinition> fields, string key, MetadataFieldSector sector, MkvMetadataTargetScope scope, MetadataFieldValueType valueType, string property, bool clearable, MetadataFieldRiskLevel risk)
        {
            MetadataFieldDefinition field = new MetadataFieldDefinition();
            field.Key = key;
            field.Label = key;
            field.Sector = sector;
            field.ValueType = valueType;
            field.InputKind = ResolveInputKind(valueType);
            field.IsReadable = true;
            field.IsEditable = true;
            field.IsClearable = clearable;
            field.RiskLevel = risk;
            field.EditPolicy = risk == MetadataFieldRiskLevel.Advanced ? MetadataFieldEditPolicy.Advanced : MetadataFieldEditPolicy.Editable;
            field.MkvPropEditProperty = property;
            field.TargetScopes.Add(scope);

            fields.Add(field);
        }

        /// <summary>
        /// Aggiunge un campo video avanzato editabile
        /// </summary>
        /// <param name="fields">Lista campi da popolare</param>
        /// <param name="key">Chiave campo</param>
        /// <param name="valueType">Tipo valore</param>
        /// <param name="property">Proprietà mkvpropedit</param>
        private static void AddVideoEditable(List<MetadataFieldDefinition> fields, string key, MetadataFieldValueType valueType, string property)
        {
            AddEditable(fields, key, MetadataFieldSector.Video, MkvMetadataTargetScope.Video, valueType, property, false, MetadataFieldRiskLevel.Advanced);
        }

        /// <summary>
        /// Aggiunge un campo audio avanzato editabile
        /// </summary>
        /// <param name="fields">Lista campi da popolare</param>
        /// <param name="key">Chiave campo</param>
        /// <param name="valueType">Tipo valore</param>
        /// <param name="property">Proprietà mkvpropedit</param>
        private static void AddAudioEditable(List<MetadataFieldDefinition> fields, string key, MetadataFieldValueType valueType, string property)
        {
            AddEditable(fields, key, MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, valueType, property, false, MetadataFieldRiskLevel.Advanced);
        }

        /// <summary>
        /// Aggiunge un campo editabile di traccia
        /// </summary>
        /// <param name="fields">Lista campi da popolare</param>
        /// <param name="key">Chiave campo</param>
        /// <param name="sector">Settore metadata</param>
        /// <param name="scope">Scope target</param>
        /// <param name="valueType">Tipo valore</param>
        /// <param name="property">Proprietà mkvpropedit</param>
        /// <param name="clearable">Vero se il campo può essere svuotato</param>
        /// <param name="risk">Livello di rischio</param>
        private static void AddTrackEditable(List<MetadataFieldDefinition> fields, string key, MetadataFieldSector sector, MkvMetadataTargetScope scope, MetadataFieldValueType valueType, string property, bool clearable, MetadataFieldRiskLevel risk)
        {
            MetadataFieldDefinition field = CreateTrackField(key, sector, valueType, property, clearable, risk);
            field.TargetScopes.Add(scope);
            fields.Add(field);
        }

        /// <summary>
        /// Aggiunge un campo traccia bloccato
        /// </summary>
        /// <param name="fields">Lista campi da popolare</param>
        /// <param name="key">Chiave campo</param>
        /// <param name="sector">Settore metadata</param>
        /// <param name="scope">Scope target</param>
        /// <param name="valueType">Tipo valore</param>
        /// <param name="property">Proprietà mkvpropedit</param>
        private static void AddBlockedTrack(List<MetadataFieldDefinition> fields, string key, MetadataFieldSector sector, MkvMetadataTargetScope scope, MetadataFieldValueType valueType, string property)
        {
            MetadataFieldDefinition field = CreateTrackField(key, sector, valueType, property, false, MetadataFieldRiskLevel.Dangerous);
            field.TargetScopes.Add(scope);
            fields.Add(field);
        }

        /// <summary>
        /// Aggiunge un campo container o traccia bloccato
        /// </summary>
        /// <param name="fields">Lista campi da popolare</param>
        /// <param name="key">Chiave campo</param>
        /// <param name="sector">Settore metadata</param>
        /// <param name="scope">Scope target</param>
        /// <param name="valueType">Tipo valore</param>
        /// <param name="property">Proprietà mkvpropedit</param>
        private static void AddBlocked(List<MetadataFieldDefinition> fields, string key, MetadataFieldSector sector, MkvMetadataTargetScope scope, MetadataFieldValueType valueType, string property)
        {
            MetadataFieldDefinition field = new MetadataFieldDefinition();
            field.Key = key;
            field.Label = key;
            field.Sector = sector;
            field.ValueType = valueType;
            field.InputKind = ResolveInputKind(valueType);
            field.IsReadable = true;
            field.IsEditable = false;
            field.IsClearable = false;
            field.RiskLevel = MetadataFieldRiskLevel.Dangerous;
            field.EditPolicy = MetadataFieldEditPolicy.Blocked;
            field.MkvPropEditProperty = property;
            field.TargetScopes.Add(scope);
            fields.Add(field);
        }

        /// <summary>
        /// Crea una definizione campo di traccia
        /// </summary>
        /// <param name="key">Chiave campo</param>
        /// <param name="sector">Settore metadata</param>
        /// <param name="valueType">Tipo valore</param>
        /// <param name="property">Proprietà mkvpropedit</param>
        /// <param name="clearable">Vero se il campo può essere svuotato</param>
        /// <param name="risk">Livello di rischio</param>
        /// <returns>Definizione campo traccia</returns>
        private static MetadataFieldDefinition CreateTrackField(string key, MetadataFieldSector sector, MetadataFieldValueType valueType, string property, bool clearable, MetadataFieldRiskLevel risk)
        {
            MetadataFieldDefinition field = new MetadataFieldDefinition();
            field.Key = key;
            field.Label = key;
            field.Sector = sector;
            field.ValueType = valueType;
            field.InputKind = ResolveInputKind(valueType);
            field.IsReadable = true;
            field.IsEditable = risk != MetadataFieldRiskLevel.Dangerous;
            field.IsClearable = clearable && risk != MetadataFieldRiskLevel.Dangerous;
            field.RiskLevel = risk;
            field.EditPolicy = risk == MetadataFieldRiskLevel.Advanced ? MetadataFieldEditPolicy.Advanced : MetadataFieldEditPolicy.Editable;
            field.MkvPropEditProperty = property;
            if (risk == MetadataFieldRiskLevel.Dangerous)
                field.EditPolicy = MetadataFieldEditPolicy.Blocked;

            return field;
        }

        /// <summary>
        /// Verifica compatibilità tra definizione campo e scope target
        /// </summary>
        /// <param name="field">Definizione campo</param>
        /// <param name="scope">Scope target</param>
        /// <returns>Vero se compatibile</returns>
        private static bool IsScopeCompatible(MetadataFieldDefinition field, MkvMetadataTargetScope scope)
        {
            if (field.TargetScopes.Count == 0)
                return false;

            return field.TargetScopes.Contains(scope);
        }

        #endregion
    }
}
