using RemuxForge.Core.Analysis.Deep;
using RemuxForge.Core.Analysis.FrameSync;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// Scrittura diagnostiche opzionali della pipeline
    /// </summary>
    public class PipelineDiagnosticsWriter
    {
        #region Metodi pubblici

        /// <summary>
        /// Scrive la diagnostica FrameSync se abilitata
        /// </summary>
        /// <param name="record">Record elaborato</param>
        /// <param name="options">Opzioni operative</param>
        public void WriteFrameSyncIfEnabled(FileProcessingRecord record, Options options)
        {
            string diagnosticsPath;
            if (options == null || !options.FrameSyncDiagnostics || record == null || record.FrameSyncResult == null)
            {
                return;
            }

            try
            {
                FrameSyncDiagnosticsWriter writer = new FrameSyncDiagnosticsWriter();
                diagnosticsPath = writer.Write(record, options);
                if (!string.IsNullOrEmpty(diagnosticsPath))
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Diagnostica frame-sync: " + diagnosticsPath);
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Errore diagnostica frame-sync: " + ex.Message);
            }
        }

        /// <summary>
        /// Scrive la diagnostica DeepAnalysis se abilitata
        /// </summary>
        /// <param name="record">Record elaborato</param>
        /// <param name="options">Opzioni operative</param>
        public void WriteDeepAnalysisIfEnabled(FileProcessingRecord record, Options options)
        {
            string diagnosticsPath;
            if (options == null || record == null || !options.DeepAnalysisDiagnostics)
            {
                return;
            }

            try
            {
                DeepAnalysisDiagnosticsWriter writer = new DeepAnalysisDiagnosticsWriter();
                diagnosticsPath = writer.Write(record);
                if (!string.IsNullOrEmpty(diagnosticsPath))
                {
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Diagnostica deep-analysis: " + diagnosticsPath);
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  Errore diagnostica deep-analysis: " + ex.Message);
            }
        }

        #endregion
    }
}
