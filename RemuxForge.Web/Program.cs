using RemuxForge.Core.Analysis.Edit.Extraction;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Radzen;
using RemuxForge.Web.Components;
using RemuxForge.Web.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Web
{
    /// <summary>
    /// Entry point della WebUI RemuxForge
    /// </summary>
    public class Program
    {
        /// <summary>
        /// Avvia l'applicazione WebUI
        /// </summary>
        /// <param name="args">Argomenti riga di comando</param>
        public static void Main(string[] args)
        {
            int port = 5000;
            string envPort = Environment.GetEnvironmentVariable("REMUXFORGE_PORT");
            bool desktopMode = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--desktop", StringComparison.Ordinal))
                {
                    desktopMode = true;
                    break;
                }
            }

            ConsoleHelper.SetRuntimeMode(LogRuntimeMode.WebUi);

            if (envPort != null)
            {
                int parsedPort;
                if (int.TryParse(envPort, out parsedPort) && parsedPort >= (desktopMode ? 0 : 1) && parsedPort <= 65535)
                {
                    port = parsedPort;
                }
            }
            else
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "--port" && i + 1 < args.Length)
                    {
                        int parsedPort;
                        if (int.TryParse(args[i + 1], out parsedPort) && parsedPort >= (desktopMode ? 0 : 1) && parsedPort <= 65535)
                        {
                            port = parsedPort;
                        }
                    }
                }
            }

            List<string> hostArgs = new List<string>();
            string desktopWebRoot = Environment.GetEnvironmentVariable("REMUXFORGE_WEB_ROOT");
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--desktop", StringComparison.Ordinal))
                    continue;

                if (string.Equals(args[i], "--port", StringComparison.Ordinal) && i + 1 < args.Length)
                {
                    i++;
                    continue;
                }

                hostArgs.Add(args[i]);
            }

            WebApplicationOptions webApplicationOptions = new WebApplicationOptions
            {
                Args = hostArgs.ToArray(),
                ContentRootPath = desktopMode ? AppContext.BaseDirectory : null,
                WebRootPath = !string.IsNullOrEmpty(desktopWebRoot) ? desktopWebRoot : null
            };
            WebApplicationBuilder builder = WebApplication.CreateBuilder(webApplicationOptions);
            builder.WebHost.UseUrls((desktopMode ? "http://127.0.0.1:" : "http://0.0.0.0:") + port);

            // Inizializza impostazioni applicazione
            AppSettingsService.Instance.Initialize();
            AppText.Initialize("", AppSettingsService.Instance.Settings.Ui.Language);
            ToolPathResolverService toolPathResolver = new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder);
            string mkvMergePath;
            string mkvExtractPath;
            string mkvPropEditPath;
            string ffmpegPath;
            string ffprobePath;
            string mediaInfoPath;

            // Auto-find tool (mkvmerge, ffmpeg, mediainfo)
            bool toolsChanged = false;
            mkvMergePath = toolPathResolver.ResolveMkvMergePath(false);
            if (!string.IsNullOrEmpty(mkvMergePath))
            {
                if (!string.Equals(AppSettingsService.Instance.Settings.Tools.MkvMergePath, mkvMergePath, System.StringComparison.Ordinal))
                {
                    AppSettingsService.Instance.Settings.Tools.MkvMergePath = mkvMergePath;
                    toolsChanged = true;
                }
            }

            mkvExtractPath = toolPathResolver.ResolveMkvExtractPath(mkvMergePath, false);
            if (!string.IsNullOrEmpty(mkvExtractPath) && !string.Equals(AppSettingsService.Instance.Settings.Tools.MkvExtractPath, mkvExtractPath, System.StringComparison.Ordinal))
            {
                AppSettingsService.Instance.Settings.Tools.MkvExtractPath = mkvExtractPath;
                toolsChanged = true;
            }

            mkvPropEditPath = toolPathResolver.ResolveMkvPropEditPath(mkvMergePath, false);
            if (!string.IsNullOrEmpty(mkvPropEditPath) && !string.Equals(AppSettingsService.Instance.Settings.Tools.MkvPropEditPath, mkvPropEditPath, System.StringComparison.Ordinal))
            {
                AppSettingsService.Instance.Settings.Tools.MkvPropEditPath = mkvPropEditPath;
                toolsChanged = true;
            }

            ffmpegPath = toolPathResolver.ResolveFfmpegPath(false, false);
            if (!string.IsNullOrEmpty(ffmpegPath) && !string.Equals(AppSettingsService.Instance.Settings.Tools.FfmpegPath, ffmpegPath, System.StringComparison.Ordinal))
            {
                AppSettingsService.Instance.Settings.Tools.FfmpegPath = ffmpegPath;
                toolsChanged = true;
            }

            ffprobePath = toolPathResolver.ResolveFfprobePath(ffmpegPath, false);
            if (!string.IsNullOrEmpty(ffprobePath) && !string.Equals(AppSettingsService.Instance.Settings.Tools.FfprobePath, ffprobePath, System.StringComparison.Ordinal))
            {
                AppSettingsService.Instance.Settings.Tools.FfprobePath = ffprobePath;
                toolsChanged = true;
            }

            mediaInfoPath = toolPathResolver.ResolveMediaInfoPath(false);
            if (!string.IsNullOrEmpty(mediaInfoPath) && !string.Equals(AppSettingsService.Instance.Settings.Tools.MediaInfoPath, mediaInfoPath, System.StringComparison.Ordinal))
            {
                AppSettingsService.Instance.Settings.Tools.MediaInfoPath = mediaInfoPath;
                toolsChanged = true;
            }

            if (toolsChanged)
            {
                AppSettingsService.Instance.Save();
            }

            // Registra servizi
            builder.Services.AddSingleton(new VideoFrameAccessService(ffprobePath, mkvMergePath, mkvExtractPath, ffmpegPath, AppSettingsService.Instance.Settings.Advanced.Ffmpeg));
            builder.Services.AddSingleton(new AudioEnvelopeExtractor(ffmpegPath, ffprobePath));
            builder.Services.AddSingleton<MergeOrchestrator>();
            builder.Services.AddSingleton<SplitOrchestrator>();
            builder.Services.AddSingleton<MetadataOrchestrator>();
            builder.Services.AddRadzenComponents();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();

            WebApplication app = builder.Build();

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error", createScopeForErrors: true);
            }

            app.UseAntiforgery();
            app.UseStaticFiles();
            app.MapGet("/api/edit-map-preview/{recordIndex:int}/{side}/{frameIndex:int}", async (int recordIndex, string side, int frameIndex, int width, int height, int? count, HttpContext context, MergeOrchestrator orchestrator, VideoFrameAccessService frameAccess) =>
            {
                if (width < 2 || height < 2 || width > 4096 || height > 4096)
                    return Results.BadRequest("Invalid preview dimensions");
                int frameCount = count ?? 1;
                if (frameCount < 1 || frameCount > 60)
                    return Results.BadRequest("Invalid preview frame count");

                FileProcessingRecord record = orchestrator.GetRecord(recordIndex);
                if (record == null)
                    return Results.NotFound();

                string filePath;
                if (string.Equals(side, "source", StringComparison.OrdinalIgnoreCase))
                    filePath = record.SourceFilePath;
                else if (string.Equals(side, "language", StringComparison.OrdinalIgnoreCase))
                    filePath = record.LangFilePath;
                else
                    return Results.BadRequest("Invalid preview side");

                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return Results.NotFound();

                try
                {
                    CancellationToken cancellationToken = context.RequestAborted;
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
            });
            app.MapGet("/api/edit-map-waveform/{recordIndex:int}/{side}", async (int recordIndex, string side, double durationMs, HttpContext context, MergeOrchestrator orchestrator, AudioEnvelopeExtractor audioExtractor) =>
            {
                if (!double.IsFinite(durationMs) || durationMs <= 0.0 || durationMs > TimeSpan.FromDays(1).TotalMilliseconds)
                    return Results.BadRequest("Invalid waveform duration");

                FileProcessingRecord record = orchestrator.GetRecord(recordIndex);
                if (record == null)
                    return Results.NotFound();

                string filePath;
                int trackId;
                if (string.Equals(side, "source", StringComparison.OrdinalIgnoreCase))
                {
                    filePath = record.SourceFilePath;
                    if (record.SourceAudioTracks == null || record.SourceAudioTracks.Count == 0)
                        return Results.NoContent();
                    trackId = record.SourceAudioTracks[0].Id;
                }
                else if (string.Equals(side, "language", StringComparison.OrdinalIgnoreCase))
                {
                    filePath = record.LangFilePath;
                    if (record.ImportedAudioTracks == null || record.ImportedAudioTracks.Count == 0)
                        return Results.NoContent();
                    trackId = record.ImportedAudioTracks[0].Id;
                }
                else
                {
                    return Results.BadRequest("Invalid waveform side");
                }

                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return Results.NotFound();

                try
                {
                    int timeoutMs = AppSettingsService.Instance.Settings.Advanced.Ffmpeg.FrameExtractionTimeoutMs;
                    AudioWaveform waveform = await Task.Run(() => audioExtractor.GenerateWaveformForTrackId(filePath, trackId, durationMs, timeoutMs, context.RequestAborted), context.RequestAborted);
                    using MemoryStream payload = new MemoryStream();
                    using (BinaryWriter writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, true))
                    {
                        writer.Write(new byte[] { (byte)'R', (byte)'F', (byte)'W', (byte)'1' });
                        writer.Write(waveform.TileWidth);
                        writer.Write(waveform.TileHeight);
                        writer.Write(waveform.MillisecondsPerPixel);
                        writer.Write(waveform.TileDurationMs);
                        writer.Write(waveform.OriginMs);
                        writer.Write(waveform.Tiles.Count);
                        for (int i = 0; i < waveform.Tiles.Count; i++)
                        {
                            writer.Write(waveform.Tiles[i].Length);
                            writer.Write(waveform.Tiles[i]);
                        }
                    }
                    context.Response.Headers.CacheControl = "no-store";
                    return Results.Bytes(payload.ToArray(), "application/vnd.remuxforge.waveform");
                }
                catch (OperationCanceledException)
                {
                    return Results.StatusCode(499);
                }
                catch (InvalidOperationException exception)
                {
                    return Results.Problem(exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
                }
            });
            app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

            if (!desktopMode)
            {
                app.Run();
                return;
            }

            MergeOrchestrator mergeOrchestrator = app.Services.GetRequiredService<MergeOrchestrator>();
            SplitOrchestrator splitOrchestrator = app.Services.GetRequiredService<SplitOrchestrator>();
            MetadataOrchestrator metadataOrchestrator = app.Services.GetRequiredService<MetadataOrchestrator>();
            app.Lifetime.ApplicationStopping.Register(() =>
            {
                mergeOrchestrator.RequestStop();
                splitOrchestrator.Stop();
                metadataOrchestrator.Stop();
            });

            app.Start();

            IServer server = app.Services.GetRequiredService<IServer>();
            IServerAddressesFeature addressesFeature = server.Features.Get<IServerAddressesFeature>();
            string readyUrl = "";
            if (addressesFeature != null)
            {
                foreach (string address in addressesFeature.Addresses)
                {
                    readyUrl = address;
                    break;
                }
            }

            if (string.IsNullOrEmpty(readyUrl))
                throw new InvalidOperationException("Il server desktop non ha pubblicato un indirizzo locale");

            Console.Out.WriteLine("REMUXFORGE_READY " + JsonSerializer.Serialize(new { url = readyUrl }));
            Console.Out.Flush();

            _ = Task.Run(() =>
            {
                string command = Console.In.ReadLine();
                if (command == null || string.Equals(command, "SHUTDOWN", StringComparison.Ordinal))
                    app.Lifetime.StopApplication();
            });

            app.WaitForShutdown();
        }
    }
}
