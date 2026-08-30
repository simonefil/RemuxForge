using RemuxForge.Core.Analysis.Edit.Extraction;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Media;
using RemuxForge.Core.Metadata;
using RemuxForge.Core.Models;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// File e tracce audio di un lato di un record, come li vedono gli endpoint multimediali
    /// </summary>
    public class MediaSource
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="filePath">Percorso del file da cui leggere fotogrammi e audio</param>
        /// <param name="audioTracks">Tracce audio selezionabili, null se non pertinenti</param>
        public MediaSource(string filePath, List<TrackInfo> audioTracks)
        {
            this.FilePath = filePath;
            this.AudioTracks = audioTracks;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Percorso del file da cui leggere fotogrammi e audio
        /// </summary>
        public string FilePath { get; }

        /// <summary>
        /// Tracce audio selezionabili sul file
        /// </summary>
        public List<TrackInfo> AudioTracks { get; }

        #endregion
    }

    /// <summary>
    /// Traduce indice di record e lato nel file da cui servire anteprime e immagini audio
    /// </summary>
    public interface IMediaSourceResolver
    {
        /// <summary>
        /// Indica se lo scope espone il lato richiesto
        /// </summary>
        /// <param name="side">Nome del lato, ad esempio source o language</param>
        /// <returns>True se il lato esiste in questo scope</returns>
        bool SupportsSide(string side);

        /// <summary>
        /// Risolve il file di un lato di un record
        /// </summary>
        /// <param name="recordIndex">Indice del record nella lista dello scope</param>
        /// <param name="side">Nome del lato</param>
        /// <returns>Sorgente multimediale, null se il record non esiste</returns>
        MediaSource ResolveMediaSource(int recordIndex, string side);
    }

    /// <summary>
    /// Endpoint condivisi dagli editor visuali: anteprima di un fotogramma e immagine di una traccia audio
    /// </summary>
    public static class MediaEndpoints
    {
        #region Costanti

        private const long AUDIO_RESPONSE_LIMIT_BYTES = 64L * 1024L * 1024L;

        #endregion

        #region Variabili statiche

        private static readonly SemaphoreSlim s_previewRequests = new SemaphoreSlim(4, 4);
        private static readonly SemaphoreSlim s_audioRequests = new SemaphoreSlim(2, 2);

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Serve il contenuto di un allegato di un record metadata, per mostrarne l'anteprima
        /// </summary>
        public static async Task<IResult> ServeMetadataAttachment(MetadataOrchestrator orchestrator, int recordIndex, int attachmentId, HttpContext context)
        {
            List<MkvMetadataRecord> records = orchestrator.GetRecords();
            if (recordIndex < 0 || recordIndex >= records.Count)
                return Results.NotFound();

            MkvMetadataRecord record = records[recordIndex];
            if (string.IsNullOrEmpty(record.InputFile) || !File.Exists(record.InputFile))
                return Results.NotFound();

            MkvMetadataAttachmentInfo attachment = null;
            List<MkvMetadataAttachmentInfo> attachments = record.FileInfo != null ? record.FileInfo.Attachments : null;
            for (int i = 0; attachments != null && i < attachments.Count; i++)
            {
                if (attachments[i].Id == attachmentId)
                    attachment = attachments[i];
            }

            if (attachment == null)
                return Results.NotFound();

            MetadataContainerReader reader = new MetadataContainerReader(AppSettingsService.Instance.Settings.Tools.MkvMergePath, AppSettingsService.Instance.Settings.Tools.MkvExtractPath);
            CancellationToken cancellationToken = context.RequestAborted;
            byte[] payload = await Task.Run(() => reader.ExtractAttachment(record.InputFile, attachmentId), cancellationToken);
            if (payload == null)
                return Results.NotFound();

            // L'allegato cambia solo quando cambia il file, e il file lo riscrive
            // l'applicazione: la cache privata basta e risparmia una mkvextract per render
            context.Response.Headers.CacheControl = "private, max-age=60";
            return Results.Bytes(payload, !string.IsNullOrEmpty(attachment.MimeType) ? attachment.MimeType : "application/octet-stream");
        }

        /// <summary>
        /// Serve una finestra di fotogrammi grezzi a partire dal file risolto dallo scope
        /// </summary>
        public static async Task<IResult> ServePreview(IMediaSourceResolver resolver, int recordIndex, string side, int frameIndex, int width, int height, int? count, HttpContext context, VideoFrameAccessService frameAccess)
        {
            if (width < 2 || height < 2 || width > 4096 || height > 4096)
                return Results.BadRequest("Invalid preview dimensions");
            int frameCount = count ?? 1;
            if (frameCount < 1 || frameCount > 60)
                return Results.BadRequest("Invalid preview frame count");

            if (!resolver.SupportsSide(side))
                return Results.BadRequest("Invalid preview side");

            MediaSource source = resolver.ResolveMediaSource(recordIndex, side);
            if (source == null)
                return Results.NotFound();

            string filePath = source.FilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return Results.NotFound();

            bool previewBudgetAcquired = false;
            try
            {
                CancellationToken cancellationToken = context.RequestAborted;
                await s_previewRequests.WaitAsync(cancellationToken);
                previewBudgetAcquired = true;
                List<VideoRawFrame> frames = await Task.Run(() => frameAccess.ExtractFrameRange(filePath, frameIndex, frameCount, width, height, cancellationToken), cancellationToken);
                VideoRawFrame frame = frames[0];
                string etag = frame.ETag.Substring(0, frame.ETag.Length - 1) + "-" + frames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\"";
                if (string.Equals(context.Request.Headers.IfNoneMatch.ToString(), etag, StringComparison.Ordinal))
                    return Results.StatusCode(StatusCodes.Status304NotModified);

                context.Response.Headers.ETag = etag;
                context.Response.Headers.CacheControl = "private, max-age=3600";
                context.Response.Headers["X-Frame-Index"] = frame.PresentationIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-Frame-Pts-Ms"] = frame.PtsMs.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-Frame-Width"] = frame.Width.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-Frame-Height"] = frame.Height.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-Frame-Count"] = frames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-Frame-Bytes"] = frame.Data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-Pixel-Format"] = frame.PixelFormat;
                context.Response.Headers["X-Color-Space"] = frame.ColorSpace ?? "";
                context.Response.Headers["X-Color-Range"] = frame.ColorRange ?? "";
                context.Response.Headers["X-Color-Primaries"] = frame.ColorPrimaries ?? "";
                context.Response.Headers["X-Color-Transfer"] = frame.ColorTransfer ?? "";
                if (frames.Count == 1)
                    return Results.Bytes(frame.Data, "application/octet-stream");
                byte[] payload = new byte[frame.Data.Length * frames.Count];
                for (int i = 0; i < frames.Count; i++)
                    Buffer.BlockCopy(frames[i].Data, 0, payload, i * frame.Data.Length, frame.Data.Length);
                return Results.Bytes(payload, "application/octet-stream");
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Results.BadRequest(exception.Message);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            finally
            {
                if (previewBudgetAcquired)
                    s_previewRequests.Release();
            }
        }

        /// <summary>
        /// Serve l'immagine della traccia audio (forma d'onda o spettrogramma) del file risolto dallo scope
        /// </summary>
        public static async Task<IResult> ServeAudioTimeline(IMediaSourceResolver resolver, int recordIndex, string side, int trackId, double durationMs, string mode, string quality, HttpContext context, AudioEnvelopeExtractor audioExtractor, VideoFrameAccessService frameAccess)
        {
            if (!double.IsFinite(durationMs) || durationMs <= 0.0)
                return Results.BadRequest("Invalid audio timeline duration");
            bool spectrogram;
            if (string.Equals(mode, "waveform", StringComparison.OrdinalIgnoreCase))
                spectrogram = false;
            else if (string.Equals(mode, "spectrogram", StringComparison.OrdinalIgnoreCase))
                spectrogram = true;
            else
                return Results.BadRequest("Invalid audio timeline mode");
            bool highQuality;
            if (string.Equals(quality, "low", StringComparison.OrdinalIgnoreCase))
                highQuality = false;
            else if (string.Equals(quality, "high", StringComparison.OrdinalIgnoreCase))
                highQuality = true;
            else
                return Results.BadRequest("Invalid audio timeline quality");

            if (!resolver.SupportsSide(side))
                return Results.BadRequest("Invalid audio timeline side");

            MediaSource source = resolver.ResolveMediaSource(recordIndex, side);
            if (source == null)
                return Results.NotFound();

            string filePath = source.FilePath;
            List<TrackInfo> tracks = source.AudioTracks;
            if (tracks == null || tracks.Find(track => track.Id == trackId) == null)
                return Results.BadRequest("Invalid audio track");
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return Results.NotFound();

            bool audioBudgetAcquired = false;
            try
            {
                int timeoutMs = AppSettingsService.Instance.Settings.Advanced.Ffmpeg.FrameExtractionTimeoutMs;
                CancellationToken cancellationToken = context.RequestAborted;
                await s_audioRequests.WaitAsync(cancellationToken);
                audioBudgetAcquired = true;
                VideoFrameIndex videoIndex = await Task.Run(() => frameAccess.GetOrBuildIndex(filePath, timeoutMs, cancellationToken), cancellationToken);
                double authoritativeDurationMs = videoIndex.EndPtsMs;
                if (!double.IsFinite(authoritativeDurationMs) || authoritativeDurationMs <= 0.0)
                    return Results.Problem("Video timeline unavailable", statusCode: StatusCodes.Status422UnprocessableEntity);
                if (!spectrogram)
                {
                    AudioTimelineWaveform waveform = await Task.Run(() => audioExtractor.GenerateTimelineWaveformForTrackId(filePath, trackId, authoritativeDurationMs, highQuality, timeoutMs, cancellationToken), cancellationToken);
                    long waveformBytes = 26L + waveform.Minimum.LongLength * sizeof(short) * 2L;
                    if (waveformBytes > AUDIO_RESPONSE_LIMIT_BYTES)
                        return Results.Problem("Audio timeline exceeds the response budget", statusCode: StatusCodes.Status413PayloadTooLarge);
                    using MemoryStream waveformPayload = new MemoryStream();
                    using (BinaryWriter writer = new BinaryWriter(waveformPayload, System.Text.Encoding.UTF8, true))
                    {
                        writer.Write(new byte[] { (byte)'R', (byte)'F', (byte)'W', (byte)'1' });
                        writer.Write(waveform.MillisecondsPerPoint);
                        writer.Write(waveform.OriginMs);
                        writer.Write(waveform.Peak);
                        writer.Write(waveform.Minimum.Length);
                        for (int i = 0; i < waveform.Minimum.Length; i++)
                        {
                            writer.Write(waveform.Minimum[i]);
                            writer.Write(waveform.Maximum[i]);
                        }
                    }
                    context.Response.Headers.CacheControl = "no-store";
                    return Results.Bytes(waveformPayload.ToArray(), "application/vnd.remuxforge.audio-timeline");
                }

                AudioTimelineImage image = await Task.Run(() => audioExtractor.GenerateTimelineImageForTrackId(filePath, trackId, authoritativeDurationMs, true, highQuality, timeoutMs, cancellationToken), cancellationToken);
                long imageBytes = 40L;
                for (int i = 0; i < image.Tiles.Count; i++)
                    imageBytes += sizeof(int) + image.Tiles[i].LongLength;
                if (imageBytes > AUDIO_RESPONSE_LIMIT_BYTES)
                    return Results.Problem("Audio timeline exceeds the response budget", statusCode: StatusCodes.Status413PayloadTooLarge);
                using MemoryStream payload = new MemoryStream();
                using (BinaryWriter writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(new byte[] { (byte)'R', (byte)'F', (byte)'A', (byte)'1' });
                    writer.Write(image.TileWidth);
                    writer.Write(image.TileHeight);
                    writer.Write(image.MillisecondsPerPixel);
                    writer.Write(image.TileDurationMs);
                    writer.Write(image.OriginMs);
                    writer.Write(image.Tiles.Count);
                    for (int i = 0; i < image.Tiles.Count; i++)
                    {
                        writer.Write(image.Tiles[i].Length);
                        writer.Write(image.Tiles[i]);
                    }
                }
                context.Response.Headers.CacheControl = "no-store";
                return Results.Bytes(payload.ToArray(), "application/vnd.remuxforge.audio-timeline");
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
            }
            finally
            {
                if (audioBudgetAcquired)
                    s_audioRequests.Release();
            }
        }

        #endregion
    }
}
