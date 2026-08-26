using RemuxForge.Core.Configuration;
using RemuxForge.Core.Models;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Mantiene log append-only e risultato conclusivo per ogni run Deep Analysis
    /// </summary>
    internal sealed class DeepAnalysisRunDiagnosticsSession
    {
        private readonly string _logPath;
        private readonly string _resultPath;
        private readonly ConfigurationPayload _configuration;

        public DeepAnalysisRunDiagnosticsSession(string sourcePath, string languagePath, string sourceCropPx, string languageCropPx, string requestedStretchFactor, string backend)
        {
            string root = Path.Combine(AppSettingsService.Instance.ConfigFolder, "deepanalysis-runs");
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            string name = this.Sanitize(Path.GetFileNameWithoutExtension(sourcePath));
            string runId = Guid.NewGuid().ToString("N").Substring(0, 8);
            this.DirectoryPath = Path.Combine(root, (string.IsNullOrEmpty(name) ? "analysis" : name) + "-" + timestamp + "-" + runId);
            Directory.CreateDirectory(this.DirectoryPath);
            this._logPath = Path.Combine(this.DirectoryPath, "run.log");
            this._resultPath = Path.Combine(this.DirectoryPath, "result.json");
            this._configuration = new ConfigurationPayload
            {
                SourcePath = sourcePath,
                LanguagePath = languagePath,
                RequestedSourceCropPx = sourceCropPx ?? "",
                RequestedLanguageCropPx = languageCropPx ?? "",
                RequestedStretchFactor = requestedStretchFactor ?? "",
                Backend = backend ?? ""
            };
            this.WriteJson(Path.Combine(this.DirectoryPath, "configuration.json"), this._configuration);
            this.Append("run-started");
        }

        public string DirectoryPath { get; private set; }

        public void Append(string message)
        {
            File.AppendAllText(this._logPath, DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + " " + (message ?? "") + Environment.NewLine);
        }

        public void UpdateConfiguration(string sourceCropPx, string languageCropPx, string stretchFactor, double sourceToLanguageScale, FrameSyncGeometryInfo sourceGeometry, FrameSyncGeometryInfo languageGeometry, VisualGeometryAlignment geometryAlignment, FfmpegConfig ffmpegConfig, VideoSyncConfig videoSyncConfig, DeepAnalysisGeometry sourceFrameGeometry, DeepAnalysisGeometry languageFrameGeometry, double geometryMatchRate, double geometryMedianDistance, string backend)
        {
            this._configuration.EffectiveSourceCropPx = sourceCropPx ?? "";
            this._configuration.EffectiveLanguageCropPx = languageCropPx ?? "";
            this._configuration.StretchFactor = stretchFactor ?? "";
            this._configuration.SourceToLanguageScale = sourceToLanguageScale;
            this._configuration.SourceGeometry = sourceGeometry;
            this._configuration.LanguageGeometry = languageGeometry;
            this._configuration.GeometryAlignment = geometryAlignment;
            this._configuration.FfmpegConfig = ffmpegConfig;
            this._configuration.VideoSyncConfig = videoSyncConfig;
            this._configuration.SourceFrameGeometry = sourceFrameGeometry;
            this._configuration.LanguageFrameGeometry = languageFrameGeometry;
            this._configuration.GeometryMatchRate = geometryMatchRate;
            this._configuration.GeometryMedianDistance = geometryMedianDistance;
            this._configuration.Backend = backend ?? "";
            this.WriteJson(Path.Combine(this.DirectoryPath, "configuration.json"), this._configuration);
            this.Append("configuration-resolved");
        }

        public void Complete(DeepAnalysisResult result)
        {
            this.WriteJson(this._resultPath, result);
            this.Append("run-completed status=" + (result != null ? result.Status.ToString() : "Unknown"));
        }

        private void WriteJson<T>(string path, T value)
        {
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
                WriteIndented = true
            };
            File.WriteAllText(path, JsonSerializer.Serialize(value, options));
        }

        private string Sanitize(string value)
        {
            string result = value ?? "";
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int index = 0; index < invalid.Length; index++)
                result = result.Replace(invalid[index], '_');
            return result;
        }

        private class ConfigurationPayload
        {
            public string SourcePath { get; set; }
            public string LanguagePath { get; set; }
            public string RequestedSourceCropPx { get; set; }
            public string RequestedLanguageCropPx { get; set; }
            public string RequestedStretchFactor { get; set; }
            public string EffectiveSourceCropPx { get; set; }
            public string EffectiveLanguageCropPx { get; set; }
            public string StretchFactor { get; set; }
            public double SourceToLanguageScale { get; set; }
            public FrameSyncGeometryInfo SourceGeometry { get; set; }
            public FrameSyncGeometryInfo LanguageGeometry { get; set; }
            public VisualGeometryAlignment GeometryAlignment { get; set; }
            public FfmpegConfig FfmpegConfig { get; set; }
            public VideoSyncConfig VideoSyncConfig { get; set; }
            public DeepAnalysisGeometry SourceFrameGeometry { get; set; }
            public DeepAnalysisGeometry LanguageFrameGeometry { get; set; }
            public double GeometryMatchRate { get; set; }

            public double GeometryMedianDistance { get; set; }
            public string Backend { get; set; }
        }
    }
}
