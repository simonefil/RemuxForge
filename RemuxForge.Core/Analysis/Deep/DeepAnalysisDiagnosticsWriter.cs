using RemuxForge.Core.Analysis.Diagnostics;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Models;
using System;
using System.Globalization;
using System.IO;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Scrive la diagnostica ufficiale DeepAnalysis in formato JSON
    /// </summary>
    public class DeepAnalysisDiagnosticsWriter : DiagnosticsWriterBase
    {
        #region Costanti

        /// <summary>
        /// Nome della cartella che contiene le diagnostiche DeepAnalysis
        /// </summary>
        private const string DIAGNOSTICS_FOLDER_NAME = "deepanalysis-diagnostics";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive il risultato raccolto per un episodio
        /// </summary>
        /// <param name="record">Record elaborazione</param>
        /// <param name="options">Opzioni operative usate per l'episodio</param>
        /// <returns>Percorso del JSON scritto, vuoto se non disponibile</returns>
        public string Write(FileProcessingRecord record, Options options)
        {
            if (record == null || record.DeepAnalysisResult == null)
                return "";

            AdvancedConfig advanced = AppSettingsService.Instance.Settings.Advanced;
            DeepAnalysisDiagnosticsPayload payload = new DeepAnalysisDiagnosticsPayload();
            payload.GeneratedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            payload.EpisodeId = record.EpisodeId;
            payload.SourceFileName = record.SourceFileName;
            payload.LanguageFileName = record.LangFileName;
            payload.SourceFilePath = record.SourceFilePath;
            payload.LanguageFilePath = record.LangFilePath;
            payload.Applied = record.DeepAnalysisApplied;
            payload.RequestedStretchFactor = options != null ? options.ManualStretchFactor : "";
            payload.RequestedSourceCropPx = options != null ? options.AnalysisCropSourcePx : "";
            payload.RequestedLanguageCropPx = options != null ? options.AnalysisCropLanguagePx : "";
            payload.DeepAnalysisConfig = advanced.DeepAnalysis;
            payload.VideoSyncConfig = advanced.VideoSync;
            payload.FfmpegConfig = advanced.Ffmpeg;
            payload.Result = record.DeepAnalysisResult;

            string result = this.BuildDiagnosticsBasePath(DIAGNOSTICS_FOLDER_NAME, record.EpisodeId) + ".json";
            this.WriteJson(result, payload);
            return result;
        }

        /// <summary>
        /// Elimina le diagnostiche Deep Analysis correnti e le directory del formato precedente
        /// </summary>
        public void Clear()
        {
            string configFolder = AppSettingsService.Instance.ConfigFolder;
            this.DeleteDirectoryIfExists(Path.Combine(configFolder, "deepanalysis-runs"));
            this.DeleteDirectoryIfExists(Path.Combine(configFolder, DIAGNOSTICS_FOLDER_NAME));
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Elimina una directory diagnostica esistente
        /// </summary>
        /// <param name="directoryPath">Directory esatta da eliminare</param>
        private void DeleteDirectoryIfExists(string directoryPath)
        {
            if (Directory.Exists(directoryPath))
                Directory.Delete(directoryPath, true);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Payload persistito per una singola esecuzione
        /// </summary>
        private class DeepAnalysisDiagnosticsPayload
        {
            /// <summary>
            /// Data e ora di generazione in formato ISO 8601
            /// </summary>
            public string GeneratedAt { get; set; }

            /// <summary>
            /// Identificatore dell'episodio
            /// </summary>
            public string EpisodeId { get; set; }

            /// <summary>
            /// Nome del file source
            /// </summary>
            public string SourceFileName { get; set; }

            /// <summary>
            /// Nome del file language
            /// </summary>
            public string LanguageFileName { get; set; }

            /// <summary>
            /// Percorso completo del file source
            /// </summary>
            public string SourceFilePath { get; set; }

            /// <summary>
            /// Percorso completo del file language
            /// </summary>
            public string LanguageFilePath { get; set; }

            /// <summary>
            /// True quando la mappa DeepAnalysis è stata applicata
            /// </summary>
            public bool Applied { get; set; }

            /// <summary>
            /// Fattore di stretch richiesto prima della normalizzazione
            /// </summary>
            public string RequestedStretchFactor { get; set; }

            /// <summary>
            /// Crop source richiesto dall'utente
            /// </summary>
            public string RequestedSourceCropPx { get; set; }

            /// <summary>
            /// Crop language richiesto dall'utente
            /// </summary>
            public string RequestedLanguageCropPx { get; set; }

            /// <summary>
            /// Configurazione Deep Analysis effettiva
            /// </summary>
            public DeepAnalysisConfig DeepAnalysisConfig { get; set; }

            /// <summary>
            /// Configurazione VideoSync condivisa effettiva
            /// </summary>
            public VideoSyncConfig VideoSyncConfig { get; set; }

            /// <summary>
            /// Configurazione FFmpeg effettiva
            /// </summary>
            public FfmpegConfig FfmpegConfig { get; set; }

            /// <summary>
            /// Risultato completo della pipeline DeepAnalysis
            /// </summary>
            public DeepAnalysisResult Result { get; set; }
        }

        #endregion
    }
}
