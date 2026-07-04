using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Reader MediaInfo JSON per modalità Metadata
    /// </summary>
    public class MetadataMediaInfoReader
    {
        #region Costanti

        /// <summary>
        /// Timeout lettura JSON MediaInfo
        /// </summary>
        private const int MEDIAINFO_JSON_TIMEOUT_MS = 30000;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso MediaInfo CLI
        /// </summary>
        private readonly string _mediaInfoPath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="mediaInfoPath">Percorso mediainfo</param>
        public MetadataMediaInfoReader(string mediaInfoPath)
        {
            this._mediaInfoPath = !string.IsNullOrEmpty(mediaInfoPath) ? mediaInfoPath : "mediainfo";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge metadata da file MKV
        /// </summary>
        /// <param name="filePath">File MKV</param>
        /// <returns>Metadata letti</returns>
        public MkvMetadataFileInfo ReadFile(string filePath)
        {
            ProcessResult processResult;
            JsonDocument document = null;

            if (!File.Exists(filePath))
                throw new FileNotFoundException(AppText.T("metadata.reader.fileNotFound"), filePath);

            processResult = ProcessRunner.Run(this._mediaInfoPath, new string[] { "--Output=JSON", filePath }, MEDIAINFO_JSON_TIMEOUT_MS);
            if (processResult.ExitCode != 0 || string.IsNullOrEmpty(processResult.Stdout.Trim()))
                throw new InvalidOperationException(AppText.F("metadata.reader.invalidJson", processResult.Stderr));

            try
            {
                document = JsonDocument.Parse(processResult.Stdout);
                return this.ParseDocument(filePath, document);
            }
            finally
            {
                if (document != null)
                    document.Dispose();
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Converte il documento JSON MediaInfo nel modello metadata
        /// </summary>
        /// <param name="filePath">Percorso file MKV</param>
        /// <param name="document">Documento JSON MediaInfo</param>
        /// <returns>Info metadata file</returns>
        private MkvMetadataFileInfo ParseDocument(string filePath, JsonDocument document)
        {
            MkvMetadataFileInfo result = CreateBaseFileInfo(filePath);
            JsonElement mediaElement;
            JsonElement tracksElement;
            int streamOrder = 0;
            int videoIndex = 0;
            int audioIndex = 0;
            int subtitleIndex = 0;

            if (!document.RootElement.TryGetProperty("media", out mediaElement))
                return result;

            if (!mediaElement.TryGetProperty("track", out tracksElement) || tracksElement.ValueKind != JsonValueKind.Array)
                return result;

            foreach (JsonElement trackElement in tracksElement.EnumerateArray())
            {
                string type = GetPropertyString(trackElement, "@type");
                Dictionary<string, string> raw = ReadRawFields(trackElement);
                if (string.Equals(type, "General", StringComparison.OrdinalIgnoreCase))
                {
                    result.RawGeneral = raw;
                    PopulateGeneralFields(result);
                }
                else
                {
                    streamOrder++;
                    MkvMetadataTrackInfo track = ParseTrack(type, raw, streamOrder, ref videoIndex, ref audioIndex, ref subtitleIndex);
                    if (!string.IsNullOrEmpty(track.TrackKind))
                        result.Tracks.Add(track);
                    else
                        result.OtherStreams.Add(ParseOtherStream(type, raw, streamOrder));
                }
            }

            return result;
        }

        /// <summary>
        /// Crea il modello file base con i campi derivati dal filesystem
        /// </summary>
        /// <param name="filePath">Percorso file MKV</param>
        /// <returns>Info metadata file iniziale</returns>
        private static MkvMetadataFileInfo CreateBaseFileInfo(string filePath)
        {
            FileInfo fileInfo = new FileInfo(filePath);
            MkvMetadataFileInfo result = new MkvMetadataFileInfo();
            result.FilePath = Path.GetFullPath(filePath);
            result.FileName = Path.GetFileName(filePath);
            result.FileStem = Path.GetFileNameWithoutExtension(filePath);
            result.FileExtension = Path.GetExtension(filePath);
            result.FileSize = fileInfo.Length;
            result.Fields["file_path"] = result.FilePath;
            result.Fields["file_name"] = result.FileName;
            result.Fields["file_stem"] = result.FileStem;
            result.Fields["file_extension"] = result.FileExtension;
            result.Fields["file_size"] = result.FileSize.ToString(CultureInfo.InvariantCulture);
            result.Fields["file_folder"] = Path.GetDirectoryName(result.FilePath) ?? "";
            result.Fields["file_relative_folder"] = "";
            return result;
        }

        /// <summary>
        /// Popola i campi container/general letti da MediaInfo
        /// </summary>
        /// <param name="fileInfo">Info file da popolare</param>
        private static void PopulateGeneralFields(MkvMetadataFileInfo fileInfo)
        {
            fileInfo.ContainerTitle = GetRaw(fileInfo.RawGeneral, "Title");
            fileInfo.Fields["container_title"] = fileInfo.ContainerTitle;
            fileInfo.Fields["container_movie"] = GetRaw(fileInfo.RawGeneral, "Movie");
            fileInfo.Fields["container_collection"] = GetRaw(fileInfo.RawGeneral, "Collection");
            fileInfo.Fields["container_season"] = GetRaw(fileInfo.RawGeneral, "Season");
            fileInfo.Fields["container_part"] = GetRaw(fileInfo.RawGeneral, "Part");
            fileInfo.Fields["container_date"] = GetRawFirst(fileInfo.RawGeneral, "Recorded_Date", "Encoded_Date");
            fileInfo.Fields["segment_filename"] = GetRaw(fileInfo.RawGeneral, "SegmentFilename");
            fileInfo.Fields["prev_filename"] = GetRaw(fileInfo.RawGeneral, "PreviousFilename");
            fileInfo.Fields["next_filename"] = GetRaw(fileInfo.RawGeneral, "NextFilename");
            fileInfo.Fields["muxing_application"] = GetRaw(fileInfo.RawGeneral, "Encoded_Application");
            fileInfo.Fields["writing_application"] = GetRaw(fileInfo.RawGeneral, "Encoded_Library");
            fileInfo.Fields["general_format"] = GetRaw(fileInfo.RawGeneral, "Format");
            fileInfo.Fields["general_format_version"] = GetRaw(fileInfo.RawGeneral, "Format_Version");
            fileInfo.Fields["general_duration"] = GetRaw(fileInfo.RawGeneral, "Duration");
            fileInfo.Fields["general_overall_bitrate"] = GetRaw(fileInfo.RawGeneral, "OverallBitRate");
            fileInfo.Fields["general_frame_rate"] = GetRaw(fileInfo.RawGeneral, "FrameRate");
            fileInfo.Fields["general_frame_count"] = GetRaw(fileInfo.RawGeneral, "FrameCount");
            fileInfo.Fields["encoded_application"] = GetRaw(fileInfo.RawGeneral, "Encoded_Application");
            fileInfo.Fields["encoded_library"] = GetRaw(fileInfo.RawGeneral, "Encoded_Library");
            fileInfo.Fields["tagged_application"] = GetRaw(fileInfo.RawGeneral, "Tagged_Application");
            fileInfo.Fields["encoded_date"] = GetRaw(fileInfo.RawGeneral, "Encoded_Date");
            fileInfo.Fields["tagged_date"] = GetRaw(fileInfo.RawGeneral, "Tagged_Date");
        }

        /// <summary>
        /// Converte uno stream MediaInfo in traccia metadata modificabile quando supportata
        /// </summary>
        /// <param name="mediaInfoType">Tipo stream MediaInfo</param>
        /// <param name="raw">Campi raw dello stream</param>
        /// <param name="streamOrder">Indice stream MediaInfo</param>
        /// <param name="videoIndex">Indice video progressivo</param>
        /// <param name="audioIndex">Indice audio progressivo</param>
        /// <param name="subtitleIndex">Indice sottotitoli progressivo</param>
        /// <returns>Traccia metadata</returns>
        private static MkvMetadataTrackInfo ParseTrack(string mediaInfoType, Dictionary<string, string> raw, int streamOrder, ref int videoIndex, ref int audioIndex, ref int subtitleIndex)
        {
            MkvMetadataTrackInfo track = new MkvMetadataTrackInfo();
            track.MediaInfoType = mediaInfoType;
            track.RawFields = raw;
            track.StreamOrder = streamOrder;

            if (string.Equals(mediaInfoType, "Video", StringComparison.OrdinalIgnoreCase))
            {
                videoIndex++;
                track.TrackKind = "video";
                track.TypeIndex = videoIndex;
                track.TrackSelector = "track:v" + videoIndex.ToString(CultureInfo.InvariantCulture);
            }
            else if (string.Equals(mediaInfoType, "Audio", StringComparison.OrdinalIgnoreCase))
            {
                audioIndex++;
                track.TrackKind = "audio";
                track.TypeIndex = audioIndex;
                track.TrackSelector = "track:a" + audioIndex.ToString(CultureInfo.InvariantCulture);
            }
            else if (string.Equals(mediaInfoType, "Text", StringComparison.OrdinalIgnoreCase))
            {
                subtitleIndex++;
                track.TrackKind = "subtitles";
                track.TypeIndex = subtitleIndex;
                track.TrackSelector = "track:s" + subtitleIndex.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                track.TrackKind = "";
                return track;
            }

            PopulateTrackFields(track);
            return track;
        }

        /// <summary>
        /// Converte uno stream MediaInfo non modificabile in info diagnostica
        /// </summary>
        /// <param name="mediaInfoType">Tipo stream MediaInfo</param>
        /// <param name="raw">Campi raw dello stream</param>
        /// <param name="streamOrder">Indice stream MediaInfo</param>
        /// <returns>Stream metadata non modificabile</returns>
        private static MkvMetadataTrackInfo ParseOtherStream(string mediaInfoType, Dictionary<string, string> raw, int streamOrder)
        {
            MkvMetadataTrackInfo track = new MkvMetadataTrackInfo();
            track.MediaInfoType = mediaInfoType;
            track.RawFields = raw;
            track.StreamOrder = streamOrder;
            track.TrackKind = mediaInfoType != null ? mediaInfoType.ToLowerInvariant() : "other";
            track.Format = GetRaw(raw, "Format");
            track.CodecId = GetRaw(raw, "CodecID");
            return track;
        }

        /// <summary>
        /// Popola i campi comuni della traccia metadata
        /// </summary>
        /// <param name="track">Traccia metadata</param>
        private static void PopulateTrackFields(MkvMetadataTrackInfo track)
        {
            track.TrackId = MetadataValueNormalizer.ParseInt(GetRaw(track.RawFields, "ID"));
            track.TrackUniqueId = GetRaw(track.RawFields, "UniqueID");
            track.Format = GetRaw(track.RawFields, "Format");
            track.CodecId = GetRaw(track.RawFields, "CodecID");
            track.Title = GetRawFirst(track.RawFields, "Title", "Name");
            track.Language = GetRawFirst(track.RawFields, "Language", "Language/String3");
            track.LanguageIetf = GetRaw(track.RawFields, "Language/String");
            track.StreamSize = MetadataValueNormalizer.ParseLong(GetRawFirst(track.RawFields, "StreamSize", "StreamSize/String"));

            if (track.TrackKind == "video")
                PopulateVideoFields(track);
            else if (track.TrackKind == "audio")
                PopulateAudioFields(track);
            else if (track.TrackKind == "subtitles")
                PopulateSubtitleFields(track);
        }

        /// <summary>
        /// Popola i campi video normalizzati
        /// </summary>
        /// <param name="track">Traccia video</param>
        private static void PopulateVideoFields(MkvMetadataTrackInfo track)
        {
            track.Fields["video_title"] = track.Title;
            track.Fields["video_language"] = track.Language;
            track.Fields["video_language_ietf"] = track.LanguageIetf;
            track.Fields["video_default"] = GetRawBoolean(track.RawFields, "Default", "Default/String");
            track.Fields["video_forced"] = GetRawBoolean(track.RawFields, "Forced", "Forced/String");
            track.Fields["video_enabled"] = GetRawBoolean(track.RawFields, "Enabled", "Enabled/String");
            track.Fields["video_original"] = GetRawBoolean(track.RawFields, "Original", "Original/String");
            track.Fields["video_codec_name"] = GetRawFirst(track.RawFields, "Codec", "Codec/String");
            track.Fields["video_type"] = track.TrackKind;
            track.Fields["video_stream_order"] = track.StreamOrder.ToString(CultureInfo.InvariantCulture);
            track.Fields["video_index"] = track.TypeIndex.ToString(CultureInfo.InvariantCulture);
            track.Fields["video_selector"] = track.TrackSelector;
            track.Fields["video_id"] = track.TrackId.ToString(CultureInfo.InvariantCulture);
            track.Fields["video_unique_id"] = track.TrackUniqueId;
            track.Fields["video_format"] = track.Format;
            track.Fields["video_codec_id"] = track.CodecId;
            track.Fields["video_profile"] = GetRaw(track.RawFields, "Format_Profile");
            track.Fields["video_level"] = GetRawFirst(track.RawFields, "Format_Level", "Format_Level/String");
            track.Fields["video_tier"] = GetRaw(track.RawFields, "Format_Tier");
            track.Fields["video_width"] = GetRaw(track.RawFields, "Width");
            track.Fields["video_height"] = GetRaw(track.RawFields, "Height");
            track.Fields["video_stored_width"] = GetRaw(track.RawFields, "Stored_Width");
            track.Fields["video_stored_height"] = GetRaw(track.RawFields, "Stored_Height");
            track.Fields["video_sampled_width"] = GetRaw(track.RawFields, "Sampled_Width");
            track.Fields["video_sampled_height"] = GetRaw(track.RawFields, "Sampled_Height");
            track.Fields["video_dar"] = GetRawFirst(track.RawFields, "DisplayAspectRatio", "DisplayAspectRatio/String");
            track.Fields["video_par"] = GetRawFirst(track.RawFields, "PixelAspectRatio", "PixelAspectRatio/String");
            track.Fields["video_active_width"] = GetRaw(track.RawFields, "Active_Width");
            track.Fields["video_active_height"] = GetRaw(track.RawFields, "Active_Height");
            track.Fields["video_active_dar"] = GetRaw(track.RawFields, "Active_DisplayAspectRatio");
            track.Fields["video_rotation"] = GetRaw(track.RawFields, "Rotation");
            track.Fields["video_pixel_width"] = track.Fields["video_width"];
            track.Fields["video_pixel_height"] = track.Fields["video_height"];
            track.Fields["video_display_width"] = GetRawFirst(track.RawFields, "DisplayWidth", "Display_Width");
            track.Fields["video_display_height"] = GetRawFirst(track.RawFields, "DisplayHeight", "Display_Height");
            track.Fields["video_display_unit"] = GetRaw(track.RawFields, "DisplayUnit");
            track.Fields["video_crop_left"] = GetRaw(track.RawFields, "PixelCropLeft");
            track.Fields["video_crop_top"] = GetRaw(track.RawFields, "PixelCropTop");
            track.Fields["video_crop_right"] = GetRaw(track.RawFields, "PixelCropRight");
            track.Fields["video_crop_bottom"] = GetRaw(track.RawFields, "PixelCropBottom");
            track.Fields["video_hdr_format"] = GetRaw(track.RawFields, "HDR_Format");
            track.Fields["video_hdr_profile"] = GetRaw(track.RawFields, "HDR_Format_Profile");
            track.Fields["video_hdr_level"] = GetRaw(track.RawFields, "HDR_Format_Level");
            track.Fields["video_hdr_settings"] = GetRaw(track.RawFields, "HDR_Format_Settings");
            track.Fields["video_hdr_compatibility"] = GetRaw(track.RawFields, "HDR_Format_Compatibility");
            track.Fields["video_frame_rate_mode"] = GetRaw(track.RawFields, "FrameRate_Mode");
            track.Fields["video_fps"] = GetRaw(track.RawFields, "FrameRate");
            track.Fields["video_fps_min"] = GetRaw(track.RawFields, "FrameRate_Minimum");
            track.Fields["video_fps_nominal"] = GetRaw(track.RawFields, "FrameRate_Nominal");
            track.Fields["video_fps_max"] = GetRaw(track.RawFields, "FrameRate_Maximum");
            track.Fields["video_fps_original"] = GetRaw(track.RawFields, "FrameRate_Original");
            track.Fields["video_color_space"] = GetRaw(track.RawFields, "ColorSpace");
            track.Fields["video_bitdepth"] = GetRaw(track.RawFields, "BitDepth");
            track.Fields["video_duration"] = GetRaw(track.RawFields, "Duration");
            track.Fields["video_bitrate"] = GetRaw(track.RawFields, "BitRate");
            track.Fields["video_bitrate_mode"] = GetRaw(track.RawFields, "BitRate_Mode");
            track.Fields["video_stream_size"] = track.StreamSize.ToString(CultureInfo.InvariantCulture);
            track.Fields["video_frame_count"] = GetRaw(track.RawFields, "FrameCount");
            track.Fields["video_format_profile"] = GetRaw(track.RawFields, "Format_Profile");
            track.Fields["video_format_level"] = GetRawFirst(track.RawFields, "Format_Level", "Format_Level/String");
            track.Fields["video_chroma_subsampling"] = GetRaw(track.RawFields, "ChromaSubsampling");
            track.Fields["video_color_bits"] = track.Fields["video_bitdepth"];
            track.Fields["video_interlaced"] = GetRawFirst(track.RawFields, "ScanType", "Interlaced");
            track.Fields["video_scan_type"] = track.Fields["video_interlaced"];
            track.Fields["video_field_order"] = GetRaw(track.RawFields, "ScanOrder");
            track.Fields["video_scan_order"] = track.Fields["video_field_order"];
            track.Fields["video_stereo_mode"] = GetRaw(track.RawFields, "MultiView_Layout");
            track.Fields["video_compression_mode"] = GetRaw(track.RawFields, "Compression_Mode");
            track.Fields["video_bits_per_pixel_frame"] = GetRaw(track.RawFields, "Bits-(Pixel*Frame)");
            track.Fields["video_chroma_position"] = GetRaw(track.RawFields, "ChromaSubsampling_Position");
            track.Fields["video_color_range"] = GetRaw(track.RawFields, "colour_range");
            track.Fields["video_color_matrix"] = GetRaw(track.RawFields, "matrix_coefficients");
            track.Fields["video_color_primaries"] = GetRaw(track.RawFields, "colour_primaries");
            track.Fields["video_primaries"] = track.Fields["video_color_primaries"];
            track.Fields["video_transfer_characteristics"] = GetRaw(track.RawFields, "transfer_characteristics");
            track.Fields["video_transfer"] = track.Fields["video_transfer_characteristics"];
            track.Fields["video_matrix_coefficients"] = GetRaw(track.RawFields, "matrix_coefficients");
            track.Fields["video_mastering_display_primaries"] = GetRaw(track.RawFields, "MasteringDisplay_ColorPrimaries");
            track.Fields["video_mastering_display_luminance"] = GetRaw(track.RawFields, "MasteringDisplay_Luminance");
            track.Fields["video_max_cll"] = GetRawFirst(track.RawFields, "MaxCLL", "Maximum_Content_Light_Level");
            track.Fields["video_max_fall"] = GetRawFirst(track.RawFields, "MaxFALL", "Maximum_FrameAverage_Light_Level");
            track.Fields["video_projection_type"] = GetRaw(track.RawFields, "ProjectionType");
        }

        /// <summary>
        /// Popola i campi audio normalizzati
        /// </summary>
        /// <param name="track">Traccia audio</param>
        private static void PopulateAudioFields(MkvMetadataTrackInfo track)
        {
            string channels = GetRawFirst(track.RawFields, "Channels", "Channel(s)");
            string samplingRate = GetRaw(track.RawFields, "SamplingRate");
            string bitDepth = GetRaw(track.RawFields, "BitDepth");
            string compressionMode = GetRaw(track.RawFields, "Compression_Mode");

            track.Fields["audio_format"] = track.Format;
            track.Fields["audio_format_commercial"] = GetRawFirst(track.RawFields, "Format_Commercial", "Format/String");
            track.Fields["audio_profile"] = GetRaw(track.RawFields, "Format_Profile");
            track.Fields["audio_codec_id"] = track.CodecId;
            track.Fields["audio_codec_description"] = GetRawFirst(track.RawFields, "CodecID_Description", "CodecID/Info");
            track.Fields["audio_format_settings"] = GetRaw(track.RawFields, "Format_Settings");
            track.Fields["audio_channels"] = channels;
            track.Fields["audio_channels_label"] = AudioChannelHelper.FormatChannels(channels);
            track.Fields["audio_channel_positions"] = GetRaw(track.RawFields, "ChannelPositions");
            track.Fields["audio_sampling_rate"] = samplingRate;
            track.Fields["audio_sampling_rate_khz"] = MetadataValueNormalizer.FormatSamplingRateKhz(samplingRate);
            track.Fields["audio_sampling_count"] = GetRaw(track.RawFields, "SamplesCount");
            track.Fields["audio_sampling_frequency"] = samplingRate;
            track.Fields["audio_output_sampling_frequency"] = GetRawFirst(track.RawFields, "OutputSamplingRate", "Output_SamplingRate");
            track.Fields["audio_bitdepth"] = bitDepth;
            track.Fields["audio_bitdepth_detected"] = GetRaw(track.RawFields, "BitDepth_Detected");
            track.Fields["audio_bitdepth_stored"] = GetRaw(track.RawFields, "BitDepth_Stored");
            track.Fields["audio_bit_depth"] = bitDepth;
            track.Fields["audio_emphasis"] = GetRaw(track.RawFields, "Emphasis");
            track.Fields["audio_bitrate"] = GetRaw(track.RawFields, "BitRate");
            track.Fields["audio_bitrate_mode"] = GetRaw(track.RawFields, "BitRate_Mode");
            track.Fields["audio_compression_mode"] = compressionMode;
            track.Fields["audio_duration"] = GetRaw(track.RawFields, "Duration");
            track.Fields["audio_stream_size"] = track.StreamSize.ToString(CultureInfo.InvariantCulture);
            track.Fields["audio_frame_count"] = GetRaw(track.RawFields, "FrameCount");
            track.Fields["audio_format_profile"] = GetRaw(track.RawFields, "Format_Profile");
            track.Fields["audio_channel_layout"] = GetRaw(track.RawFields, "ChannelLayout");
            track.Fields["audio_delay"] = GetRawFirst(track.RawFields, "Delay", "Delay/String");
            track.Fields["audio_video_delay"] = GetRawFirst(track.RawFields, "Video_Delay", "Video_Delay/String");
            track.Fields["audio_replaygain_gain"] = GetRaw(track.RawFields, "ReplayGain_Gain");
            track.Fields["audio_replaygain_peak"] = GetRaw(track.RawFields, "ReplayGain_Peak");
            track.Fields["audio_service_kind"] = GetRaw(track.RawFields, "ServiceKind");
            track.Fields["audio_quality"] = CodecMapping.DetectAudioQuality(track.Format, compressionMode);
            track.Fields["audio_title"] = track.Title;
            track.Fields["audio_language"] = track.Language;
            track.Fields["audio_language_ietf"] = track.LanguageIetf;
            track.Fields["audio_default"] = GetRawBoolean(track.RawFields, "Default", "Default/String");
            track.Fields["audio_forced"] = GetRawBoolean(track.RawFields, "Forced", "Forced/String");
            track.Fields["audio_enabled"] = GetRawBoolean(track.RawFields, "Enabled", "Enabled/String");
            track.Fields["audio_commentary"] = GetRawBoolean(track.RawFields, "Commentary", "Commentary/String");
            track.Fields["audio_hearing_impaired"] = GetRawBoolean(track.RawFields, "HearingImpaired", "HearingImpaired/String");
            track.Fields["audio_visual_impaired"] = GetRawBoolean(track.RawFields, "VisualImpaired", "VisualImpaired/String");
            track.Fields["audio_original"] = GetRawBoolean(track.RawFields, "Original", "Original/String");
            track.Fields["audio_codec_name"] = GetRawFirst(track.RawFields, "Codec", "Codec/String");
            track.Fields["audio_type"] = track.TrackKind;
            track.Fields["audio_stream_order"] = track.StreamOrder.ToString(CultureInfo.InvariantCulture);
            track.Fields["audio_index"] = track.TypeIndex.ToString(CultureInfo.InvariantCulture);
            track.Fields["audio_selector"] = track.TrackSelector;
            track.Fields["audio_id"] = track.TrackId.ToString(CultureInfo.InvariantCulture);
            track.Fields["audio_unique_id"] = track.TrackUniqueId;
        }

        /// <summary>
        /// Popola i campi sottotitoli normalizzati
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        private static void PopulateSubtitleFields(MkvMetadataTrackInfo track)
        {
            track.Fields["subtitle_format"] = track.Format;
            track.Fields["subtitle_format_commercial"] = GetRawFirst(track.RawFields, "Format_Commercial", "Format/String");
            track.Fields["subtitle_codec_id"] = track.CodecId;
            track.Fields["subtitle_codec_description"] = GetRawFirst(track.RawFields, "CodecID_Description", "CodecID/Info");
            track.Fields["subtitle_muxing_mode"] = GetRaw(track.RawFields, "MuxingMode");
            track.Fields["subtitle_stream_size"] = track.StreamSize.ToString(CultureInfo.InvariantCulture);
            track.Fields["subtitle_element_count"] = GetRaw(track.RawFields, "ElementCount");
            track.Fields["subtitle_duration"] = GetRaw(track.RawFields, "Duration");
            track.Fields["subtitle_duration_start"] = GetRaw(track.RawFields, "Duration_Start");
            track.Fields["subtitle_duration_end"] = GetRaw(track.RawFields, "Duration_End");
            track.Fields["subtitle_bitrate"] = GetRaw(track.RawFields, "BitRate");
            track.Fields["subtitle_width"] = GetRaw(track.RawFields, "Width");
            track.Fields["subtitle_height"] = GetRaw(track.RawFields, "Height");
            track.Fields["subtitle_dar"] = GetRawFirst(track.RawFields, "DisplayAspectRatio", "DisplayAspectRatio/String");
            track.Fields["subtitle_frame_rate_mode"] = GetRaw(track.RawFields, "FrameRate_Mode");
            track.Fields["subtitle_fps"] = GetRaw(track.RawFields, "FrameRate");
            track.Fields["subtitle_frame_count"] = GetRaw(track.RawFields, "FrameCount");
            track.Fields["subtitle_format_profile"] = GetRaw(track.RawFields, "Format_Profile");
            track.Fields["subtitle_compression_mode"] = GetRaw(track.RawFields, "Compression_Mode");
            track.Fields["subtitle_events_total"] = GetRaw(track.RawFields, "Events_Total");
            track.Fields["subtitle_events_min_duration"] = GetRaw(track.RawFields, "Events_MinDuration");
            track.Fields["subtitle_lines_count"] = GetRaw(track.RawFields, "Lines_Count");
            track.Fields["subtitle_title"] = track.Title;
            track.Fields["subtitle_language"] = track.Language;
            track.Fields["subtitle_language_ietf"] = track.LanguageIetf;
            track.Fields["subtitle_default"] = GetRawBoolean(track.RawFields, "Default", "Default/String");
            track.Fields["subtitle_forced"] = GetRawBoolean(track.RawFields, "Forced", "Forced/String");
            track.Fields["subtitle_enabled"] = GetRawBoolean(track.RawFields, "Enabled", "Enabled/String");
            track.Fields["subtitle_hearing_impaired"] = GetRawBoolean(track.RawFields, "HearingImpaired", "HearingImpaired/String");
            track.Fields["subtitle_text_descriptions"] = GetRawBoolean(track.RawFields, "TextDescriptions", "TextDescriptions/String");
            track.Fields["subtitle_original"] = GetRawBoolean(track.RawFields, "Original", "Original/String");
            track.Fields["subtitle_codec_name"] = GetRawFirst(track.RawFields, "Codec", "Codec/String");
            track.Fields["subtitle_type"] = track.TrackKind;
            track.Fields["subtitle_stream_order"] = track.StreamOrder.ToString(CultureInfo.InvariantCulture);
            track.Fields["subtitle_index"] = track.TypeIndex.ToString(CultureInfo.InvariantCulture);
            track.Fields["subtitle_selector"] = track.TrackSelector;
            track.Fields["subtitle_id"] = track.TrackId.ToString(CultureInfo.InvariantCulture);
            track.Fields["subtitle_unique_id"] = track.TrackUniqueId;
        }

        /// <summary>
        /// Legge tutti i campi semplici di uno stream MediaInfo
        /// </summary>
        /// <param name="element">Elemento JSON stream</param>
        /// <returns>Dizionario raw case-insensitive</returns>
        private static Dictionary<string, string> ReadRawFields(JsonElement element)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    result[property.Name] = property.Value.GetString();
                else if (property.Value.ValueKind == JsonValueKind.Number || property.Value.ValueKind == JsonValueKind.True || property.Value.ValueKind == JsonValueKind.False)
                    result[property.Name] = property.Value.ToString();
            }

            return result;
        }

        /// <summary>
        /// Legge una proprietà stringa da un elemento JSON
        /// </summary>
        /// <param name="element">Elemento JSON</param>
        /// <param name="propertyName">Nome proprietà</param>
        /// <returns>Valore stringa o stringa vuota</returns>
        private static string GetPropertyString(JsonElement element, string propertyName)
        {
            JsonElement property;
            if (element.TryGetProperty(propertyName, out property) && property.ValueKind == JsonValueKind.String)
                return property.GetString();

            return "";
        }

        /// <summary>
        /// Legge un campo raw MediaInfo
        /// </summary>
        /// <param name="raw">Dizionario raw</param>
        /// <param name="key">Chiave campo</param>
        /// <returns>Valore raw o stringa vuota</returns>
        private static string GetRaw(Dictionary<string, string> raw, string key)
        {
            string result;
            if (raw != null && raw.TryGetValue(key, out result) && result != null)
                return result;

            return "";
        }

        /// <summary>
        /// Legge il primo campo raw MediaInfo valorizzato tra più chiavi
        /// </summary>
        /// <param name="raw">Dizionario raw</param>
        /// <param name="keys">Chiavi candidate</param>
        /// <returns>Primo valore raw valorizzato, oppure stringa vuota</returns>
        private static string GetRawFirst(Dictionary<string, string> raw, params string[] keys)
        {
            string value;

            if (keys == null)
                return "";

            for (int i = 0; i < keys.Length; i++)
            {
                value = GetRaw(raw, keys[i]);
                if (!string.IsNullOrEmpty(value.Trim()))
                    return value;
            }

            return "";
        }

        /// <summary>
        /// Legge e normalizza un flag booleano MediaInfo da più chiavi possibili
        /// </summary>
        /// <param name="raw">Dizionario raw</param>
        /// <param name="keys">Chiavi candidate</param>
        /// <returns>Flag normalizzato 1/0 o stringa vuota</returns>
        private static string GetRawBoolean(Dictionary<string, string> raw, params string[] keys)
        {
            return MetadataValueNormalizer.NormalizeBoolean(GetRawFirst(raw, keys));
        }

        #endregion
    }
}
