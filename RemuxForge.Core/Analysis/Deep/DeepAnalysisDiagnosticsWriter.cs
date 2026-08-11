using RemuxForge.Core.Analysis.Diagnostics;
using RemuxForge.Core.Models;
using System;
using System.Globalization;

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
        /// <returns>Percorso del JSON scritto, vuoto se non disponibile</returns>
        public string Write(FileProcessingRecord record)
        {
            if (record == null || record.DeepAnalysisResult == null)
                return "";

            DeepAnalysisDiagnosticsPayload payload = new DeepAnalysisDiagnosticsPayload();
            payload.GeneratedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            payload.EpisodeId = record.EpisodeId;
            payload.SourceFileName = record.SourceFileName;
            payload.LanguageFileName = record.LangFileName;
            payload.SourceFilePath = record.SourceFilePath;
            payload.LanguageFilePath = record.LangFilePath;
            payload.Applied = record.DeepAnalysisApplied;
            payload.Result = record.DeepAnalysisResult;

            string result = this.BuildDiagnosticsBasePath(DIAGNOSTICS_FOLDER_NAME, record.EpisodeId) + ".json";
            this.WriteJson(result, payload);
            return result;
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
            /// Risultato completo della pipeline DeepAnalysis
            /// </summary>
            public DeepAnalysisResult Result { get; set; }
        }

        #endregion
    }
}
