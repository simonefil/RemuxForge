using RemuxForge.Core.Models;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Helper dominio per compatibilità tra scope metadata, campi e tracce
    /// </summary>
    public static class MetadataScopeHelper
    {
        #region Metodi pubblici

        /// <summary>
        /// Indica se un campo leggibile è disponibile nello scope richiesto
        /// </summary>
        /// <param name="field">Definizione campo metadata</param>
        /// <param name="scope">Scope target</param>
        /// <returns>Vero se il campo può essere letto nello scope</returns>
        public static bool IsFieldReadableInScope(MetadataFieldDefinition field, MkvMetadataTargetScope scope)
        {
            if (field == null)
                return false;

            if (field.Sector == MetadataFieldSector.File || field.Sector == MetadataFieldSector.Container)
                return true;

            return IsTrackFieldInScope(field, scope);
        }

        /// <summary>
        /// Indica se un campo traccia appartiene allo scope richiesto
        /// </summary>
        /// <param name="field">Definizione campo metadata</param>
        /// <param name="scope">Scope target</param>
        /// <returns>Vero se il campo traccia appartiene allo scope</returns>
        public static bool IsTrackFieldInScope(MetadataFieldDefinition field, MkvMetadataTargetScope scope)
        {
            if (field == null || scope == MkvMetadataTargetScope.Container)
                return false;

            return ScopeFromFieldSector(field.Sector) == scope;
        }

        /// <summary>
        /// Converte tipo traccia runtime nello scope metadata corrispondente
        /// </summary>
        /// <param name="track">Traccia metadata</param>
        /// <returns>Scope metadata corrispondente</returns>
        public static MkvMetadataTargetScope ScopeFromTrack(MkvMetadataTrackInfo track)
        {
            if (track == null)
                return MkvMetadataTargetScope.Container;
            if (track.TrackKind == "video")
                return MkvMetadataTargetScope.Video;
            if (track.TrackKind == "audio")
                return MkvMetadataTargetScope.Audio;
            if (track.TrackKind == "subtitles")
                return MkvMetadataTargetScope.Subtitle;

            return MkvMetadataTargetScope.Container;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Converte settore campo nello scope traccia corrispondente
        /// </summary>
        /// <param name="sector">Settore metadata</param>
        /// <returns>Scope metadata corrispondente</returns>
        private static MkvMetadataTargetScope ScopeFromFieldSector(MetadataFieldSector sector)
        {
            if (sector == MetadataFieldSector.Video)
                return MkvMetadataTargetScope.Video;
            if (sector == MetadataFieldSector.Audio)
                return MkvMetadataTargetScope.Audio;
            if (sector == MetadataFieldSector.Subtitle)
                return MkvMetadataTargetScope.Subtitle;

            return MkvMetadataTargetScope.Container;
        }

        #endregion
    }
}
