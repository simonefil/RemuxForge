using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
