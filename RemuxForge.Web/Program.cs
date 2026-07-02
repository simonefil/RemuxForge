using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemuxForge.Web.Components;
using RemuxForge.Web.Services;
using System;

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

            ConsoleHelper.SetRuntimeMode(LogRuntimeMode.WebUi);

            if (envPort != null)
            {
                int parsedPort;
                if (int.TryParse(envPort, out parsedPort) && parsedPort >= 1 && parsedPort <= 65535)
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
                        if (int.TryParse(args[i + 1], out parsedPort) && parsedPort >= 1 && parsedPort <= 65535)
                        {
                            port = parsedPort;
                        }
                    }
                }
            }

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
            if (mkvMergePath.Length > 0)
            {
                if (!string.Equals(AppSettingsService.Instance.Settings.Tools.MkvMergePath, mkvMergePath, System.StringComparison.Ordinal))
                {
                    AppSettingsService.Instance.Settings.Tools.MkvMergePath = mkvMergePath;
                    toolsChanged = true;
                }
            }

            mkvExtractPath = toolPathResolver.ResolveMkvExtractPath(mkvMergePath, false);
            if (mkvExtractPath.Length > 0 && !string.Equals(AppSettingsService.Instance.Settings.Tools.MkvExtractPath, mkvExtractPath, System.StringComparison.Ordinal))
            {
                AppSettingsService.Instance.Settings.Tools.MkvExtractPath = mkvExtractPath;
                toolsChanged = true;
            }

            mkvPropEditPath = toolPathResolver.ResolveMkvPropEditPath(mkvMergePath, false);
            if (mkvPropEditPath.Length > 0 && !string.Equals(AppSettingsService.Instance.Settings.Tools.MkvPropEditPath, mkvPropEditPath, System.StringComparison.Ordinal))
            {
                AppSettingsService.Instance.Settings.Tools.MkvPropEditPath = mkvPropEditPath;
                toolsChanged = true;
            }

            ffmpegPath = toolPathResolver.ResolveFfmpegPath(false, false);
            if (ffmpegPath.Length > 0 && !string.Equals(AppSettingsService.Instance.Settings.Tools.FfmpegPath, ffmpegPath, System.StringComparison.Ordinal))
            {
                AppSettingsService.Instance.Settings.Tools.FfmpegPath = ffmpegPath;
                toolsChanged = true;
            }

            ffprobePath = toolPathResolver.ResolveFfprobePath(ffmpegPath, false);
            if (ffprobePath.Length > 0 && !string.Equals(AppSettingsService.Instance.Settings.Tools.FfprobePath, ffprobePath, System.StringComparison.Ordinal))
            {
                AppSettingsService.Instance.Settings.Tools.FfprobePath = ffprobePath;
                toolsChanged = true;
            }

            mediaInfoPath = toolPathResolver.ResolveMediaInfoPath(false);
            if (mediaInfoPath.Length > 0 && !string.Equals(AppSettingsService.Instance.Settings.Tools.MediaInfoPath, mediaInfoPath, System.StringComparison.Ordinal))
            {
                AppSettingsService.Instance.Settings.Tools.MediaInfoPath = mediaInfoPath;
                toolsChanged = true;
            }

            if (toolsChanged)
            {
                AppSettingsService.Instance.Save();
            }

            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseUrls("http://0.0.0.0:" + port);

            // Registra servizi
            builder.Services.AddSingleton<MergeOrchestrator>();
            builder.Services.AddSingleton<SplitOrchestrator>();
            builder.Services.AddSingleton<MetadataOrchestrator>();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();

            WebApplication app = builder.Build();

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error", createScopeForErrors: true);
            }

            app.UseAntiforgery();
            app.UseStaticFiles();
            app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
