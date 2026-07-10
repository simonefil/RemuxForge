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
            string containerTitle;

            PopulateCatalogFields(fileInfo.Fields, fileInfo.RawGeneral, MetadataFieldSector.Container, null);
            fileInfo.ContainerTitle = fileInfo.Fields.TryGetValue("container_title", out containerTitle) ? containerTitle : "";
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
            track.LanguageIetf = GetRaw(track.RawFields, "Language/String");
            track.Language = LanguageValidator.NormalizeToIso6392(GetRawFirst(track.RawFields, "Language/String3", "Language"));
            if (string.IsNullOrEmpty(track.Language))
                track.Language = LanguageValidator.NormalizeToIso6392(track.LanguageIetf);

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
            Dictionary<string, string> computedValues = BuildTrackComputedValues(track, "video");
            PopulateCatalogFields(track.Fields, track.RawFields, MetadataFieldSector.Video, computedValues);
        }

        /// <summary>
        /// Popola i campi audio normalizzati
        /// </summary>
        /// <param name="track">Traccia audio</param>
        private static void PopulateAudioFields(MkvMetadataTrackInfo track)
        {
            string channels = GetRawFirst(track.RawFields, "Channels", "Channel(s)");
            string samplingRate = GetRaw(track.RawFields, "SamplingRate");
            string compressionMode = GetRaw(track.RawFields, "Compression_Mode");
            Dictionary<string, string> computedValues = BuildTrackComputedValues(track, "audio");

            computedValues["audio_channels_label"] = AudioChannelHelper.FormatChannels(channels);
            computedValues["audio_sampling_rate_khz"] = MetadataValueNormalizer.FormatSamplingRateKhz(samplingRate);
            computedValues["audio_quality"] = CodecMapping.DetectAudioQuality(track.Format, compressionMode);
            PopulateCatalogFields(track.Fields, track.RawFields, MetadataFieldSector.Audio, computedValues);
        }

        /// <summary>
        /// Popola i campi sottotitoli normalizzati
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        private static void PopulateSubtitleFields(MkvMetadataTrackInfo track)
        {
            Dictionary<string, string> computedValues = BuildTrackComputedValues(track, "subtitle");
            PopulateCatalogFields(track.Fields, track.RawFields, MetadataFieldSector.Subtitle, computedValues);
        }

        /// <summary>
        /// Popola i campi definiti dal catalogo per un settore
        /// </summary>
        /// <param name="destination">Dizionario destinazione</param>
        /// <param name="raw">Campi raw MediaInfo</param>
        /// <param name="sector">Settore metadata da leggere</param>
        /// <param name="computedValues">Valori derivati dal reader</param>
        private static void PopulateCatalogFields(Dictionary<string, string> destination, Dictionary<string, string> raw, MetadataFieldSector sector, Dictionary<string, string> computedValues)
        {
            List<MetadataFieldDefinition> fields = MetadataFieldRegistry.GetAll();

            for (int i = 0; i < fields.Count; i++)
            {
                MetadataFieldDefinition field = fields[i];
                string value = "";
                if (field.Sector != sector)
                    continue;

                if (computedValues != null && computedValues.TryGetValue(field.Key, out value))
                {
                    destination[field.Key] = value != null ? value : "";
                    continue;
                }

                if (field.MediaInfoFieldNames == null || field.MediaInfoFieldNames.Count == 0)
                    continue;

                value = field.ValueType == MetadataFieldValueType.Boolean
                    ? GetRawBoolean(raw, field.MediaInfoFieldNames.ToArray())
                    : GetRawFirst(raw, field.MediaInfoFieldNames.ToArray());
                if (!string.IsNullOrEmpty(value))
                    destination[field.Key] = value;
            }
        }

        /// <summary>
        /// Costruisce i valori derivati comuni a tutte le tracce
        /// </summary>
        /// <param name="track">Traccia sorgente</param>
        /// <param name="prefix">Prefisso campo metadata</param>
        /// <returns>Valori derivati indicizzati per campo</returns>
        private static Dictionary<string, string> BuildTrackComputedValues(MkvMetadataTrackInfo track, string prefix)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result[prefix + "_title"] = track.Title;
            result[prefix + "_language"] = track.Language;
            result[prefix + "_language_ietf"] = track.LanguageIetf;
            result[prefix + "_type"] = track.TrackKind;
            result[prefix + "_stream_order"] = track.StreamOrder.ToString(CultureInfo.InvariantCulture);
            result[prefix + "_index"] = track.TypeIndex.ToString(CultureInfo.InvariantCulture);
            result[prefix + "_selector"] = track.TrackSelector;
            result[prefix + "_id"] = track.TrackId.ToString(CultureInfo.InvariantCulture);
            result[prefix + "_unique_id"] = track.TrackUniqueId;
            result[prefix + "_format"] = track.Format;
            result[prefix + "_codec_id"] = track.CodecId;
            result[prefix + "_stream_size"] = track.StreamSize.ToString(CultureInfo.InvariantCulture);
            return result;
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
