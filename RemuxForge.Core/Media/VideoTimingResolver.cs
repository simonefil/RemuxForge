using RemuxForge.Core.Configuration;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.IO;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Risolve timing video usando MediaInfo come fonte primaria e default_duration solo se coerente
    /// </summary>
    public class VideoTimingResolver
    {
        #region Costanti

        private const double DEFAULT_DURATION_FRAME_COUNT_TOLERANCE = 0.02;

        #endregion

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
            long defaultDurationNs = 0;
            if (videoTrack != null)
            {
                defaultDurationNs = videoTrack.DefaultDurationNs;
                result.FrameCount = videoTrack.VideoFrameCount;
                if (videoTrack.TrackDurationNs > 0)
                {
                    result.DurationMs = videoTrack.TrackDurationNs / 1000000.0;
                }
            }

            if (result.DurationMs <= 0.0 && fileInfo != null && fileInfo.ContainerDurationNs > 0)
            {
                result.DurationMs = fileInfo.ContainerDurationNs / 1000000.0;
            }

            this.ReadMediaInfo(filePath, mediaInfoPath, result);

            if (result.FrameCount > 0 && result.DurationMs > 0.0)
            {
                result.ObservedFps = result.FrameCount / (result.DurationMs / 1000.0);
            }

            result.IsDefaultDurationTrusted = this.IsDefaultDurationTrusted(defaultDurationNs, result.FrameCount, result.DurationMs);
            this.Classify(result);
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Integra le informazioni timing lette da MediaInfo nel risultato corrente
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
            if (service.TryGetVideoTiming(filePath, out string mode, out double frameRate, out double originalFrameRate, out long frameCount, out double durationMs, out double minFrameRate, out double maxFrameRate))
            {
                // MediaInfo è la fonte primaria per VFR/CFR: sovrascrive i dati mkvmerge quando disponibili
                result.IsMediaInfoAvailable = true;
                result.FrameRateMode = mode;
                if (frameRate > 0.0)
                {
                    result.NominalFps = frameRate;
                }
                else if (originalFrameRate > 0.0)
                {
                    result.NominalFps = originalFrameRate;
                }
                if (frameCount > 0)
                {
                    result.FrameCount = frameCount;
                }
                if (durationMs > 0.0)
                {
                    result.DurationMs = durationMs;
                }
                if (minFrameRate > 0.0 && maxFrameRate > 0.0 && Math.Abs(maxFrameRate - minFrameRate) > 0.001)
                {
                    result.IsVariableFrameRate = true;
                }
            }
        }

        /// <summary>
        /// Classifica il timing video per la normalizzazione di campionamento FrameSync
        /// </summary>
        /// <param name="timing">Timing video da classificare</param>
        private void Classify(VideoTimingInfo timing)
        {
            string mode = timing.FrameRateMode != null ? timing.FrameRateMode.Trim().ToLowerInvariant() : "";
            bool modeVariable = mode.Contains("variable") || mode == "vfr";
            bool modeConstant = mode.Contains("constant") || mode == "cfr";

            if (modeVariable)
            {
                timing.IsVariableFrameRate = true;
            }

            // Le motivazioni sono usate nei log/UI, quindi devono restare descrittive e non solo tecniche
            if (!timing.IsMediaInfoAvailable)
            {
                timing.Reason = "MediaInfo non disponibile";
            }
            else if (timing.IsVariableFrameRate)
            {
                timing.Reason = "MediaInfo segnala VFR";
            }
            else if (!timing.IsDefaultDurationTrusted)
            {
                timing.Reason = "default_duration non coerente con frame count/durata";
            }
            else
            {
                timing.Reason = "timing CFR coerente";
            }

            timing.CanNormalizeToNominalFps = timing.IsMediaInfoAvailable && modeConstant && !timing.IsVariableFrameRate && timing.NominalFps > 0.0;
        }

        /// <summary>
        /// Verifica se default_duration è coerente con durata e numero frame reali
        /// </summary>
        /// <param name="defaultDurationNs">Default duration Matroska in nanosecondi</param>
        /// <param name="frameCount">Numero frame video</param>
        /// <param name="durationMs">Durata video in millisecondi</param>
        /// <returns>True se default_duration è utilizzabile come sorgente timing CFR</returns>
        private bool IsDefaultDurationTrusted(long defaultDurationNs, long frameCount, double durationMs)
        {
            bool result = false;
            double expectedFrames;
            double ratioDiff;
            if (defaultDurationNs > 0 && frameCount > 0 && durationMs > 0.0)
            {
                // Il confronto usa il conteggio frame atteso invece del solo FPS dichiarato
                expectedFrames = (durationMs * 1000000.0) / defaultDurationNs;
                if (expectedFrames > 0.0)
                {
                    ratioDiff = Math.Abs(expectedFrames - frameCount) / Math.Max(expectedFrames, frameCount);
                    result = ratioDiff <= DEFAULT_DURATION_FRAME_COUNT_TOLERANCE;
                }
            }

            return result;
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
