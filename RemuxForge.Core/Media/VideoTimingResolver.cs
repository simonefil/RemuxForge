using RemuxForge.Core.Configuration;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System.IO;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Risolve la durata video usando MediaInfo come fonte primaria e i metadati mkvmerge come fallback
    /// </summary>
    public class VideoTimingResolver
    {
        #region Variabili di classe

        /// <summary>
        /// Resolver centralizzato per il path di MediaInfo
        /// </summary>
        private readonly ToolPathResolverService _toolPathResolver;

        #endregion

        #region Costruttori

        /// <summary>
        /// Costruttore esplicito
        /// </summary>
        /// <param name="toolPathResolver">Resolver strumenti esterni</param>
        public VideoTimingResolver(ToolPathResolverService toolPathResolver)
        {
            this._toolPathResolver = toolPathResolver ?? new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Risolve timing video per un file
        /// </summary>
        public VideoTimingInfo Resolve(string filePath, MkvFileInfo fileInfo)
        {
            VideoTimingInfo result = new VideoTimingInfo();
            TrackInfo videoTrack = this.FindVideoTrack(fileInfo);
            string mediaInfoPath = this.ResolveMediaInfoPath();
            if (videoTrack != null && videoTrack.TrackDurationNs > 0)
            {
                result.DurationMs = videoTrack.TrackDurationNs / 1000000.0;
            }

            if (result.DurationMs <= 0.0 && fileInfo != null && fileInfo.ContainerDurationNs > 0)
            {
                result.DurationMs = fileInfo.ContainerDurationNs / 1000000.0;
            }

            this.ReadMediaInfo(filePath, mediaInfoPath, result);
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Integra la durata letta da MediaInfo nel risultato corrente
        /// </summary>
        /// <param name="filePath">File da analizzare</param>
        /// <param name="mediaInfoPath">Percorso MediaInfo CLI</param>
        /// <param name="result">Oggetto timing da aggiornare</param>
        private void ReadMediaInfo(string filePath, string mediaInfoPath, VideoTimingInfo result)
        {
            if (string.IsNullOrEmpty(mediaInfoPath) || !File.Exists(mediaInfoPath))
            {
                return;
            }

            MediaInfoService service = new MediaInfoService(mediaInfoPath);
            // MediaInfo è la fonte primaria: sovrascrive la durata mkvmerge quando disponibile
            if (service.TryGetVideoDuration(filePath, out double durationMs) && durationMs > 0.0)
            {
                result.DurationMs = durationMs;
            }
        }

        /// <summary>
        /// Cerca la prima traccia video nei metadati mkvmerge
        /// </summary>
        /// <param name="fileInfo">Informazioni contenitore</param>
        /// <returns>Traccia video trovata, oppure null</returns>
        private TrackInfo FindVideoTrack(MkvFileInfo fileInfo)
        {
            TrackInfo result = null;
            if (fileInfo == null || fileInfo.Tracks == null)
            {
                return result;
            }

            for (int i = 0; i < fileInfo.Tracks.Count; i++)
            {
                if (fileInfo.Tracks[i].Type == "video")
                {
                    result = fileInfo.Tracks[i];
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Risolve MediaInfo CLI usando prima la configurazione e poi il provider locale
        /// </summary>
        /// <returns>Percorso MediaInfo CLI, oppure stringa vuota</returns>
        private string ResolveMediaInfoPath()
        {
            string result = this._toolPathResolver.ResolveMediaInfoPath(false);
            return result;
        }

        #endregion
    }
}
