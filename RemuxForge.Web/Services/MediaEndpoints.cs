using RemuxForge.Core.Analysis.Edit.Extraction;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Media;
using RemuxForge.Core.Metadata;
using RemuxForge.Core.Models;
using RemuxForge.Core.Pipeline;
using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

        /// <summary>Prepara in RAM entrambe le visualizzazioni con la stessa cache e la stessa chiave degli endpoint</summary>
        /// <param name="filePath">Media originale</param>
        /// <param name="trackId">Traccia audio</param>
        /// <param name="highQuality">Qualità dell'editor di precisione</param>
        /// <param name="audioExtractor">Cache condivisa delle visualizzazioni</param>
        /// <param name="frameAccess">Cache condivisa degli indici video</param>
        /// <param name="cancellation">Annullamento dell'analisi</param>
        /// <returns>Completamento della preparazione</returns>
        public static async Task WarmAudioTimelineAsync(string filePath, int trackId, bool highQuality, AudioEnvelopeExtractor audioExtractor, VideoFrameAccessService frameAccess, CancellationToken cancellation)
        {
            await s_audioRequests.WaitAsync(cancellation);
            try
            {
                int timeoutMs = AppSettingsService.Instance.Settings.Advanced.Ffmpeg.FrameExtractionTimeoutMs;
                VideoFrameIndex index = await Task.Run(() => frameAccess.GetOrBuildIndex(filePath, timeoutMs, cancellation), cancellation);
                if (double.IsFinite(index.EndPtsMs) && index.EndPtsMs > 0.0)
                    await Task.Run(() => audioExtractor.PrepareTimeline(filePath, trackId, index.EndPtsMs, highQuality, timeoutMs, cancellation), cancellation);
            }
            finally
            {
                s_audioRequests.Release();
            }
        }

        /// <summary>
        /// Serve il contenuto di un allegato di un record metadata, per mostrarne l'anteprima
        /// </summary>
        /// <param name="orchestrator">Orchestratore che possiede i record metadata</param>
        /// <param name="recordIndex">Indice del record</param>
        /// <param name="attachmentId">ID dell'allegato nel record</param>
        /// <param name="context">Contesto della richiesta HTTP</param>
        /// <returns>Contenuto dell'allegato, o l'esito negativo quando non esiste</returns>
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
        /// Crea e scarica lo ZIP della diagnostica Deep Analysis di un episodio
        /// </summary>
        /// <param name="orchestrator">Orchestratore che possiede i record remux</param>
        /// <param name="recordIndex">Indice del record corrente</param>
        /// <param name="context">Contesto della richiesta HTTP</param>
        /// <returns>Archivio ZIP oppure l'esito negativo quando la diagnostica non è disponibile</returns>
        public static async Task<IResult> ServeDeepAnalysisDiagnostics(MergeOrchestrator orchestrator, int recordIndex, HttpContext context)
        {
            if (!PipelineDiagnosticsWriter.IsDeepAnalysisDiagnosticsEnabled(orchestrator.CurrentOptions))
                return Results.NotFound();

            FileProcessingRecord record = orchestrator.GetRecord(recordIndex);
            if (record == null || string.IsNullOrEmpty(record.DeepAnalysisDiagnosticsPath))
                return Results.NotFound();

            string diagnosticsRoot = Path.GetFullPath(Path.Combine(AppSettingsService.Instance.ConfigFolder, "deepanalysis-diagnostics"));
            string diagnosticPath = Path.GetFullPath(record.DeepAnalysisDiagnosticsPath);
            string relativePath = Path.GetRelativePath(diagnosticsRoot, diagnosticPath);
            if (relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relativePath) || !File.Exists(diagnosticPath))
                return Results.NotFound();

            try
            {
                using MemoryStream archiveStream = new MemoryStream();
                using (ZipArchive archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, true))
                {
                    ZipArchiveEntry entry = archive.CreateEntry(Path.GetFileName(diagnosticPath), CompressionLevel.Optimal);
                    await using FileStream diagnosticStream = File.OpenRead(diagnosticPath);
                    await using Stream entryStream = entry.Open();
                    await diagnosticStream.CopyToAsync(entryStream, context.RequestAborted);
                }

                string episodeName = SanitizeDownloadFileName(record.EpisodeId);
                string downloadName = (string.IsNullOrEmpty(episodeName) ? "episodio" : episodeName) + "-diagnostica.zip";
                context.Response.Headers.CacheControl = "no-store";
                return Results.File(archiveStream.ToArray(), "application/zip", downloadName);
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499);
            }
            catch (IOException)
            {
                return Results.NotFound();
            }
        }

        /// <summary>
        /// Serve una finestra di fotogrammi grezzi a partire dal file risolto dallo scope
        /// </summary>
        /// <param name="resolver">Risolutore del file secondo lo scope della richiesta</param>
        /// <param name="recordIndex">Indice del record</param>
        /// <param name="side">Lato richiesto</param>
        /// <param name="frameIndex">Primo fotogramma della finestra</param>
        /// <param name="width">Larghezza massima in pixel</param>
        /// <param name="height">Altezza massima in pixel</param>
        /// <param name="count">Numero di fotogrammi richiesti, uno quando manca</param>
        /// <param name="context">Contesto della richiesta HTTP</param>
        /// <param name="frameAccess">Servizio di indicizzazione e decodifica dei fotogrammi</param>
        /// <returns>Finestra di fotogrammi grezzi, o l'esito negativo quando non è servibile</returns>
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
        /// <param name="resolver">Risolutore del file secondo lo scope della richiesta</param>
        /// <param name="recordIndex">Indice del record</param>
        /// <param name="side">Lato richiesto</param>
        /// <param name="trackId">ID della traccia audio nel contenitore</param>
        /// <param name="durationMs">Durata da rappresentare in millisecondi</param>
        /// <param name="mode">Modalità richiesta, waveform o spectrogram</param>
        /// <param name="quality">Qualità richiesta, low o high</param>
        /// <param name="context">Contesto della richiesta HTTP</param>
        /// <param name="audioExtractor">Estrattore delle visualizzazioni audio</param>
        /// <param name="frameAccess">Servizio di indicizzazione dei fotogrammi, che detta la durata autorevole</param>
        /// <returns>Visualizzazione audio, o l'esito negativo quando non è servibile</returns>
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
                // Una sola decodifica alla frequenza nativa produce entrambe le visualizzazioni: la
                // richiesta gemella dell'altra modalità la ritrova in cache
                AudioTimelinePair timeline = await Task.Run(() => audioExtractor.GetOrGenerateTimeline(filePath, trackId, authoritativeDurationMs, highQuality, timeoutMs, cancellationToken), cancellationToken);
                if (!spectrogram)
                {
                    AudioTimelineWaveform waveform = timeline.Waveform;
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

                AudioTimelineImage image = timeline.Image;
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

        #region Metodi privati

        /// <summary>
        /// Sanitizza il nome episodio usato nell'allegato HTTP
        /// </summary>
        /// <param name="value">Nome originale</param>
        /// <returns>Nome compatibile con il filesystem e privo di caratteri di controllo</returns>
        private static string SanitizeDownloadFileName(string value)
        {
            string result = value ?? "";
            char[] invalidChars = Path.GetInvalidFileNameChars();
            for (int index = 0; index < invalidChars.Length; index++)
                result = result.Replace(invalidChars[index], '_');
            result = result.Replace('\r', '_').Replace('\n', '_');
            return result;
        }

        #endregion
    }
}
