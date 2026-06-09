using RemuxForge.Core.Analysis.Diagnostics;
using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Models;
using System;
using System.Globalization;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Scrive diagnostica DeepAnalysis in formato JSON per tuning e regressioni
    /// </summary>
    public class DeepAnalysisDiagnosticsWriter : DiagnosticsWriterBase
    {
        #region Costanti

        /// <summary>
        /// Nome cartella diagnostica
        /// </summary>
        private const string DIAGNOSTICS_FOLDER_NAME = "deepanalysis-diagnostics";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive un file JSON diagnostico per un episodio elaborato con DeepAnalysis
        /// </summary>
        /// <param name="record">Record elaborazione</param>
        /// <param name="options">Opzioni operative</param>
        /// <returns>Percorso file scritto, vuoto se non scritto</returns>
        public string Write(FileProcessingRecord record, Options options)
        {
            string result = "";
            string baseName;
            DeepAnalysisDiagnosticsPayload payload;
            string sourceFrameRateMode;
            string languageFrameRateMode;
            string frameRateModeReason;
            string autoSpeedPolicy;
            if (record == null)
            {
                return result;
            }

            baseName = this.BuildDiagnosticsBasePath(DIAGNOSTICS_FOLDER_NAME, record.EpisodeId);
            result = baseName + ".json";

            payload = new DeepAnalysisDiagnosticsPayload();
            payload.GeneratedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
            payload.EpisodeId = record.EpisodeId;
            payload.SourceFileName = record.SourceFileName;
            payload.LanguageFileName = record.LangFileName;
            payload.SourceFilePath = record.SourceFilePath;
            payload.LanguageFilePath = record.LangFilePath;
            payload.Status = this.GetDiagnosticStatus(record);
            payload.ErrorMessage = record.ErrorMessage;
            if (string.Equals(payload.Status, "DeepAnalysisFailed", StringComparison.Ordinal) && string.IsNullOrEmpty(payload.ErrorMessage))
            {
                payload.ErrorMessage = "Deep analysis fallita";
            }
            payload.DeepAnalysisApplied = record.DeepAnalysisApplied;
            payload.DeepAnalysisTimeMs = record.DeepAnalysisTimeMs;
            payload.SpeedCorrectionMode = options != null ? options.SpeedCorrectionMode : "";
            payload.ManualStretchFactor = options != null ? options.ManualStretchFactor : "";
            if (SpeedCorrectionService.TryGetFrameRateModes(record.SourceFilePath, record.LangFilePath, out sourceFrameRateMode, out languageFrameRateMode, out frameRateModeReason))
            {
                payload.SourceFrameRateMode = sourceFrameRateMode;
                payload.LanguageFrameRateMode = languageFrameRateMode;
                if (SpeedCorrectionService.ShouldBlockAutoForVfr(record.SourceFilePath, record.LangFilePath, out autoSpeedPolicy))
                {
                    payload.AutoSpeedPolicy = "blocked: " + autoSpeedPolicy;
                }
                else
                {
                    payload.AutoSpeedPolicy = "allowed: " + autoSpeedPolicy;
                }
            }
            else
            {
                payload.FrameRateModeReason = frameRateModeReason;
                payload.AutoSpeedPolicy = "blocked: " + frameRateModeReason;
            }
            payload.AudioOnly = options != null && options.AudioOnly;
            payload.SubOnly = options != null && options.SubOnly;
            payload.EditMap = record.DeepAnalysisMap;

            if (record.DeepAnalysisMap != null)
            {
                payload.OperationCount = record.DeepAnalysisMap.Operations != null ? record.DeepAnalysisMap.Operations.Count : 0;
                payload.InitialDelayMs = record.DeepAnalysisMap.InitialDelayMs;
                payload.StretchFactor = record.DeepAnalysisMap.StretchFactor;
                payload.BaselineMse = record.DeepAnalysisMap.BaselineMse;
                payload.Diagnostics = record.DeepAnalysisMap.Diagnostics;
            }

            this.WriteJson(result, payload);

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Restituisce uno stato diagnostico stabile, indipendente dallo stato temporaneo della pipeline
        /// </summary>
        /// <param name="record">Record elaborazione</param>
        /// <returns>Stato diagnostico</returns>
        private string GetDiagnosticStatus(FileProcessingRecord record)
        {
            if (record.DeepAnalysisApplied && record.DeepAnalysisMap != null)
            {
                return "DeepAnalysisOk";
            }

            if (!record.DeepAnalysisApplied && record.DeepAnalysisMap != null)
            {
                return "DeepAnalysisFailed";
            }

            if (!string.IsNullOrEmpty(record.ErrorMessage))
            {
                return "DeepAnalysisFailed";
            }

            return record.Status.ToString();
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Payload JSON diagnostico DeepAnalysis
        /// </summary>
        private class DeepAnalysisDiagnosticsPayload
        {
            /// <summary>
            /// Timestamp generazione diagnostica
            /// </summary>
            public string GeneratedAt { get; set; }
            /// <summary>
            /// Identificatore episodio
            /// </summary>
            public string EpisodeId { get; set; }
            /// <summary>
            /// Nome file source
            /// </summary>
            public string SourceFileName { get; set; }
            /// <summary>
            /// Nome file language
            /// </summary>
            public string LanguageFileName { get; set; }
            /// <summary>
            /// Path completo source
            /// </summary>
            public string SourceFilePath { get; set; }
            /// <summary>
            /// Path completo language
            /// </summary>
            public string LanguageFilePath { get; set; }
            /// <summary>
            /// Stato record
            /// </summary>
            public string Status { get; set; }
            /// <summary>
            /// Messaggio errore record
            /// </summary>
            public string ErrorMessage { get; set; }
            /// <summary>
            /// True se DeepAnalysis e' stata applicata
            /// </summary>
            public bool DeepAnalysisApplied { get; set; }
            /// <summary>
            /// Durata DeepAnalysis
            /// </summary>
            public long DeepAnalysisTimeMs { get; set; }
            /// <summary>
            /// Modalita' speed correction
            /// </summary>
            public string SpeedCorrectionMode { get; set; }
            /// <summary>
            /// Stretch factor manuale
            /// </summary>
            public string ManualStretchFactor { get; set; }
            /// <summary>
            /// Modalita' frame rate source
            /// </summary>
            public string SourceFrameRateMode { get; set; }
            /// <summary>
            /// Modalita' frame rate language
            /// </summary>
            public string LanguageFrameRateMode { get; set; }
            /// <summary>
            /// Motivazione policy frame rate
            /// </summary>
            public string FrameRateModeReason { get; set; }
            /// <summary>
            /// Policy auto speed applicata
            /// </summary>
            public string AutoSpeedPolicy { get; set; }
            /// <summary>
            /// True se run solo audio
            /// </summary>
            public bool AudioOnly { get; set; }
            /// <summary>
            /// True se run solo sottotitoli
            /// </summary>
            public bool SubOnly { get; set; }
            /// <summary>
            /// Numero operazioni prodotte
            /// </summary>
            public int OperationCount { get; set; }
            /// <summary>
            /// Delay iniziale
            /// </summary>
            public int InitialDelayMs { get; set; }
            /// <summary>
            /// Stretch factor finale
            /// </summary>
            public string StretchFactor { get; set; }
            /// <summary>
            /// Baseline MSE
            /// </summary>
            public double BaselineMse { get; set; }
            /// <summary>
            /// Edit map prodotta
            /// </summary>
            public EditMap EditMap { get; set; }
            /// <summary>
            /// Diagnostica DeepAnalysis completa
            /// </summary>
            public DeepAnalysisDiagnostics Diagnostics { get; set; }
        }

        #endregion
    }
}
