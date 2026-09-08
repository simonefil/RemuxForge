using RemuxForge.Core.Analysis.Deep;
using RemuxForge.Core.Analysis.FrameSync;
using RemuxForge.Core.Configuration;
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
            if (record == null)
            {
                return;
            }
            record.DeepAnalysisDiagnosticsPath = "";
            if (!IsDeepAnalysisDiagnosticsEnabled(options))
                return;

            try
            {
                DeepAnalysisDiagnosticsWriter writer = new DeepAnalysisDiagnosticsWriter();
                diagnosticsPath = writer.Write(record, options);
                if (!string.IsNullOrEmpty(diagnosticsPath))
                {
                    record.DeepAnalysisDiagnosticsPath = diagnosticsPath;
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Diagnostica deep-analysis: " + diagnosticsPath);
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  Errore diagnostica deep-analysis: " + ex.Message);
            }
        }

        /// <summary>
        /// Elimina tutte le diagnostiche Deep Analysis prima di un nuovo scan
        /// </summary>
        public void ClearDeepAnalysisDiagnostics()
        {
            DeepAnalysisDiagnosticsWriter writer = new DeepAnalysisDiagnosticsWriter();
            writer.Clear();
        }

        /// <summary>
        /// Indica se la diagnostica Deep Analysis è abilitata dalle opzioni operative o dalle impostazioni persistite
        /// </summary>
        /// <param name="options">Opzioni operative correnti</param>
        /// <returns>True quando la diagnostica deve essere prodotta ed esposta</returns>
        public static bool IsDeepAnalysisDiagnosticsEnabled(Options options)
        {
            return (options != null && options.DeepAnalysisDiagnostics) || AppSettingsService.Instance.Settings.Advanced.DeepAnalysis.DiagnosticsEnabled;
        }

        #endregion
    }
}
