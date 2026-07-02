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

                if (!includeAdvanced && field.EditPolicy == MetadataFieldEditPolicy.Advanced)
                    continue;

                if (IsScopeCompatible(field, scope))
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
            AddReadable(fields, "encoded_application", MetadataFieldSector.Container, MetadataFieldValueType.String);
            AddReadable(fields, "encoded_library", MetadataFieldSector.Container, MetadataFieldValueType.String);
            AddReadable(fields, "tagged_application", MetadataFieldSector.Container, MetadataFieldValueType.String);
            AddReadable(fields, "encoded_date", MetadataFieldSector.Container, MetadataFieldValueType.Date);
            AddReadable(fields, "tagged_date", MetadataFieldSector.Container, MetadataFieldValueType.Date);

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
            result.Sector = source.Sector;
            result.TargetScopes = new List<MkvMetadataTargetScope>(source.TargetScopes);
            result.ValueType = source.ValueType;
            result.Unit = source.Unit;
            result.IsReadable = source.IsReadable;
            result.IsEditable = source.IsEditable;
            result.IsClearable = source.IsClearable;
            result.RiskLevel = source.RiskLevel;
            result.EditPolicy = source.EditPolicy;
            result.MediaInfoFieldNames = new List<string>(source.MediaInfoFieldNames);
            result.MkvPropEditProperty = source.MkvPropEditProperty;
            result.MkvMergeArgument = source.MkvMergeArgument;
            result.AllowedValues = new List<string>(source.AllowedValues);
            result.RequiresRemux = source.RequiresRemux;

            return result;
        }

        /// <summary>
        /// Aggiunge i campi video al registro
        /// </summary>
        /// <param name="fields">Lista campi da popolare</param>
        private static void AddVideoFields(List<MetadataFieldDefinition> fields)
        {
            AddTrackEditable(fields, "video_title", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.String, "name", true, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_language", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.Language, "language", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_language_ietf", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.Language, "language-ietf", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_default", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.Boolean, "flag-default", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_forced", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.Boolean, "flag-forced", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_enabled", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.Boolean, "flag-enabled", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_original", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.Boolean, "flag-original", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "video_codec_name", MetadataFieldSector.Video, MkvMetadataTargetScope.Video, MetadataFieldValueType.String, "codec-name", true, MetadataFieldRiskLevel.Advanced);
            AddVideoEditable(fields, "video_alpha_mode", MetadataFieldValueType.Integer, "alpha-mode");
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
            AddVideoEditable(fields, "video_color_matrix", MetadataFieldValueType.Integer, "color-matrix-coefficients");
            AddVideoEditable(fields, "video_color_bits", MetadataFieldValueType.Integer, "color-bits-per-channel");
            AddVideoEditable(fields, "video_chroma_subsample_horizontal", MetadataFieldValueType.Integer, "chroma-subsample-horizontal");
            AddVideoEditable(fields, "video_chroma_subsample_vertical", MetadataFieldValueType.Integer, "chroma-subsample-vertical");
            AddVideoEditable(fields, "video_cb_subsample_horizontal", MetadataFieldValueType.Integer, "cb-subsample-horizontal");
            AddVideoEditable(fields, "video_cb_subsample_vertical", MetadataFieldValueType.Integer, "cb-subsample-vertical");
            AddVideoEditable(fields, "video_chroma_siting_horizontal", MetadataFieldValueType.Integer, "chroma-siting-horizontal");
            AddVideoEditable(fields, "video_chroma_siting_vertical", MetadataFieldValueType.Integer, "chroma-siting-vertical");
            AddVideoEditable(fields, "video_color_range", MetadataFieldValueType.Integer, "color-range");
            AddVideoEditable(fields, "video_transfer", MetadataFieldValueType.Integer, "color-transfer-characteristics");
            AddVideoEditable(fields, "video_primaries", MetadataFieldValueType.Integer, "color-primaries");
            AddVideoEditable(fields, "video_max_cll", MetadataFieldValueType.Integer, "max-content-light");
            AddVideoEditable(fields, "video_max_fall", MetadataFieldValueType.Integer, "max-frame-light");
            AddVideoEditable(fields, "video_chromaticity_red_x", MetadataFieldValueType.Decimal, "chromaticity-coordinates-red-x");
            AddVideoEditable(fields, "video_chromaticity_red_y", MetadataFieldValueType.Decimal, "chromaticity-coordinates-red-y");
            AddVideoEditable(fields, "video_chromaticity_green_x", MetadataFieldValueType.Decimal, "chromaticity-coordinates-green-x");
            AddVideoEditable(fields, "video_chromaticity_green_y", MetadataFieldValueType.Decimal, "chromaticity-coordinates-green-y");
            AddVideoEditable(fields, "video_chromaticity_blue_x", MetadataFieldValueType.Decimal, "chromaticity-coordinates-blue-x");
            AddVideoEditable(fields, "video_chromaticity_blue_y", MetadataFieldValueType.Decimal, "chromaticity-coordinates-blue-y");
            AddVideoEditable(fields, "video_white_coordinates_x", MetadataFieldValueType.Decimal, "white-coordinates-x");
            AddVideoEditable(fields, "video_white_coordinates_y", MetadataFieldValueType.Decimal, "white-coordinates-y");
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
            AddReadable(fields, "video_format_profile", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_format_level", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_chroma_subsampling", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_chroma_position", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_scan_type", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_scan_order", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_compression_mode", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_bits_per_pixel_frame", MetadataFieldSector.Video, MetadataFieldValueType.Decimal);
            AddReadable(fields, "video_color_primaries", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_transfer_characteristics", MetadataFieldSector.Video, MetadataFieldValueType.String);
            AddReadable(fields, "video_matrix_coefficients", MetadataFieldSector.Video, MetadataFieldValueType.String);
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
            AddTrackEditable(fields, "audio_language_ietf", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Language, "language-ietf", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_default", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-default", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_forced", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-forced", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_enabled", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-enabled", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_commentary", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-commentary", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_hearing_impaired", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-hearing-impaired", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_visual_impaired", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-visual-impaired", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_original", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.Boolean, "flag-original", false, MetadataFieldRiskLevel.Normal);
            AddTrackEditable(fields, "audio_codec_name", MetadataFieldSector.Audio, MkvMetadataTargetScope.Audio, MetadataFieldValueType.String, "codec-name", true, MetadataFieldRiskLevel.Advanced);
            AddAudioEditable(fields, "audio_sampling_frequency", MetadataFieldValueType.Decimal, "sampling-frequency");
            AddAudioEditable(fields, "audio_output_sampling_frequency", MetadataFieldValueType.Decimal, "output-sampling-frequency");
            AddAudioEditable(fields, "audio_channels", MetadataFieldValueType.Integer, "channels");
            AddAudioEditable(fields, "audio_bit_depth", MetadataFieldValueType.Integer, "bit-depth");
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
            AddReadable(fields, "audio_sampling_rate", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_sampling_rate_khz", MetadataFieldSector.Audio, MetadataFieldValueType.Decimal);
            AddReadable(fields, "audio_sampling_count", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_bitdepth", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_bitdepth_detected", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_bitdepth_stored", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_bitrate", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_bitrate_mode", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_compression_mode", MetadataFieldSector.Audio, MetadataFieldValueType.String);
            AddReadable(fields, "audio_duration", MetadataFieldSector.Audio, MetadataFieldValueType.Duration);
            AddReadable(fields, "audio_stream_size", MetadataFieldSector.Audio, MetadataFieldValueType.Bytes);
            AddReadable(fields, "audio_frame_count", MetadataFieldSector.Audio, MetadataFieldValueType.Integer);
            AddReadable(fields, "audio_format_profile", MetadataFieldSector.Audio, MetadataFieldValueType.String);
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
            AddTrackEditable(fields, "subtitle_language_ietf", MetadataFieldSector.Subtitle, MkvMetadataTargetScope.Subtitle, MetadataFieldValueType.Language, "language-ietf", false, MetadataFieldRiskLevel.Normal);
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
