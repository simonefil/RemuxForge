using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Helper copia profonda modelli metadata
    /// </summary>
    public static class MetadataModelCloner
    {
        #region Metodi pubblici

        /// <summary>
        /// Clona info file metadata
        /// </summary>
        /// <param name="source">Sorgente</param>
        /// <returns>Copia profonda</returns>
        public static MkvMetadataFileInfo CloneFileInfo(MkvMetadataFileInfo source)
        {
            MkvMetadataFileInfo clone = new MkvMetadataFileInfo();
            if (source == null)
            {
                return clone;
            }

            clone.FilePath = source.FilePath;
            clone.FileName = source.FileName;
            clone.FileStem = source.FileStem;
            clone.FileExtension = source.FileExtension;
            clone.FileSize = source.FileSize;
            clone.ContainerTitle = source.ContainerTitle;
            clone.RawGeneral = CloneDictionary(source.RawGeneral);
            clone.Fields = CloneDictionary(source.Fields);
            clone.Tags = CloneDictionary(source.Tags);
            clone.LeveledTags = CloneDictionary(source.LeveledTags);
            clone.Tracks = new List<MkvMetadataTrackInfo>();
            for (int i = 0; i < source.Tracks.Count; i++)
            {
                clone.Tracks.Add(CloneTrackInfo(source.Tracks[i]));
            }

            clone.OtherStreams = new List<MkvMetadataTrackInfo>();
            for (int i = 0; i < source.OtherStreams.Count; i++)
            {
                clone.OtherStreams.Add(CloneTrackInfo(source.OtherStreams[i]));
            }

            clone.Attachments = MetadataContainerReader.CloneAttachments(source.Attachments);
            clone.Chapters = MetadataContainerReader.CloneChapters(source.Chapters);
            return clone;
        }

        /// <summary>
        /// Clona info traccia metadata
        /// </summary>
        /// <param name="source">Sorgente</param>
        /// <returns>Copia profonda</returns>
        public static MkvMetadataTrackInfo CloneTrackInfo(MkvMetadataTrackInfo source)
        {
            MkvMetadataTrackInfo clone = new MkvMetadataTrackInfo();
            if (source == null)
            {
                return clone;
            }

            clone.MediaInfoType = source.MediaInfoType;
            clone.TrackKind = source.TrackKind;
            clone.StreamOrder = source.StreamOrder;
            clone.TypeIndex = source.TypeIndex;
            clone.TrackSelector = source.TrackSelector;
            clone.TrackId = source.TrackId;
            clone.TrackUniqueId = source.TrackUniqueId;
            clone.Format = source.Format;
            clone.CodecId = source.CodecId;
            clone.Title = source.Title;
            clone.Language = source.Language;
            clone.LanguageIetf = source.LanguageIetf;
            clone.StreamSize = source.StreamSize;
            clone.RawFields = CloneDictionary(source.RawFields);
            clone.Fields = CloneDictionary(source.Fields);
            clone.Tags = CloneDictionary(source.Tags);
            return clone;
        }

        /// <summary>
        /// Clona dizionario stringa
        /// </summary>
        /// <param name="source">Sorgente</param>
        /// <returns>Copia</returns>
        public static Dictionary<string, string> CloneDictionary(Dictionary<string, string> source)
        {
            Dictionary<string, string> clone = new Dictionary<string, string>();
            if (source == null)
            {
                return clone;
            }

            foreach (KeyValuePair<string, string> pair in source)
            {
                clone[pair.Key] = pair.Value;
            }

            return clone;
        }

        #endregion
    }
}
