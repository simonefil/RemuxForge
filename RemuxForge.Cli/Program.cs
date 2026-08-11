using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Metadata;
using RemuxForge.Core.Models;
using RemuxForge.Core.Pipeline;
using RemuxForge.Core.Splitting;
using System;
using System.Collections.Generic;

namespace RemuxForge.Cli
{
    /// <summary>
    /// Entry point applicativo della CLI RemuxForge
    /// </summary>
    internal class Program
    {
        #region Entry point

        /// <summary>
        /// Entry point
        /// </summary>
        /// <param name="args">Argomenti riga di comando</param>
        /// <returns>Codice uscita: 0 successo, 1 errore</returns>
        static int Main(string[] args)
        {
            bool done = false;
            int exitCode = 0;
            Options opts = null;
            OptionsValidationResult validation;
            ConsoleHelper.SetRuntimeMode(LogRuntimeMode.Cli);

            // Abilita log su file se variabile d'ambiente impostata
            string logFilePath = Environment.GetEnvironmentVariable("REMUXFORGE_LOG_FILE");
            if (!string.IsNullOrEmpty(logFilePath))
            {
                ConsoleHelper.EnableFileLog(logFilePath);
            }

            // Inizializza AppSettings e cartella .remux-forge prima di tutto
            _ = AppSettingsService.Instance.Initialize();
            AppText.Initialize(FindLanguageArgument(args), AppSettingsService.Instance.Settings.Ui.Language);
            ProcessingPipeline pipeline = null;
            List<FileProcessingRecord> records;
            ProcessingStats stats;
            // Nessun argomento: mostra help CLI
            if (args.Length == 0)
            {
                PrintHelp();
                done = true;
            }

            // Parsing argomenti
            if (!done)
            {
                opts = Options.Parse(args);
                if (!string.IsNullOrEmpty(opts.ErrorMessage))
                {
                    ConsoleHelper.Write(LogSection.Config, LogLevel.Error, AppText.F("cli.error", opts.ErrorMessage));
                    ConsoleHelper.Write(LogSection.Config, LogLevel.Debug, AppText.T("cli.helpHint"));
                    exitCode = 1;
                    done = true;
                }
            }

            // Help
            if (!done && opts.Help)
            {
                PrintHelp();
                done = true;
            }

            if (!done)
            {
                validation = OptionsValidator.Validate(opts, true, false);
                if (!validation.IsValid)
                {
                    for (int i = 0; i < validation.Errors.Count; i++)
                    {
                        ConsoleHelper.Write(LogSection.Config, LogLevel.Error, AppText.F("cli.error", validation.Errors[i]));
                    }
                    for (int i = 0; i < validation.Warnings.Count; i++)
                    {
                        ConsoleHelper.Write(LogSection.Config, LogLevel.Info, validation.Warnings[i]);
                    }

                    exitCode = 1;
                    done = true;
                }
            }

            // Inizializza pipeline
            if (!done && opts.Mode == Options.MODE_SPLIT)
            {
                try
                {
                    MkvSplitPipeline splitPipeline = new MkvSplitPipeline();
                    exitCode = splitPipeline.Execute(opts);
                }
                catch (Exception ex)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("cli.splitError", ex.Message));
                    exitCode = 1;
                }
                done = true;
            }

            if (!done && opts.Mode == Options.MODE_METADATA)
            {
                exitCode = ExecuteMetadata(opts);
                done = true;
            }

            // Inizializza pipeline
            if (!done)
            {
                pipeline = new ProcessingPipeline();

                // Collega log pipeline alla console
                pipeline.OnLogMessage += (section, level, text) =>
                {
                    // Componi testo con prefisso sezione se non General
                    string displayText = section != LogSection.General ? ConsoleHelper.FormatSectionPrefix(section) + text : text;
                    ConsoleColor color = ConsoleHelper.MapLevelToColor(level);
                    ConsoleColor original = Console.ForegroundColor;
                    Console.ForegroundColor = color;
                    Console.WriteLine(displayText);
                    Console.ForegroundColor = original;
                };

                if (!pipeline.Initialize(opts))
                {
                    exitCode = 1;
                    done = true;
                }
            }

            // Scan ed elaborazione
            if (!done)
            {
                // Banner
                ConsoleHelper.Write(LogSection.General, LogLevel.Phase, "\n========================================");
                ConsoleHelper.Write(LogSection.General, LogLevel.Phase, "  RemuxForge");
                ConsoleHelper.Write(LogSection.General, LogLevel.Phase, "========================================\n");

                // Configurazione
                PrintConfiguration(opts, pipeline.CodecPatterns);

                // Scan file
                records = pipeline.ScanFiles();

                // Analizza e processa ogni file
                for (int i = 0; i < records.Count; i++)
                {
                    if (records[i].Status == FileStatus.Pending)
                    {
                        pipeline.AnalyzeFile(records[i]);

                        if (records[i].Status == FileStatus.Analyzed)
                        {
                            pipeline.ProcessFile(records[i]);
                        }
                    }
                }

                // Report e riepilogo
                stats = ComputeStats(records);
                PrintDetailedReport(records, opts.DryRun);
                PrintSummary(stats);
            }

            return exitCode;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Legge --lang prima del parsing opzioni completo, così anche help ed errori iniziali usano la lingua richiesta
        /// </summary>
        /// <param name="args">Argomenti CLI</param>
        /// <returns>Lingua richiesta o vuoto</returns>
        private static string FindLanguageArgument(string[] args)
        {
            string result = "";

            if (args == null)
            {
                return result;
            }

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--lang", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    result = args[i + 1];
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Calcola statistiche di elaborazione dai record
        /// </summary>
        /// <param name="records">Lista record elaborazione</param>
        /// <returns>Statistiche calcolate</returns>
        private static ProcessingStats ComputeStats(List<FileProcessingRecord> records)
        {
            ProcessingStats stats = new ProcessingStats();

            for (int i = 0; i < records.Count; i++)
            {
                FileProcessingRecord r = records[i];

                // File elaborati con successo
                if (r.Success)
                {
                    stats.Processed++;
                }
                // File saltati per mancanza ID episodio
                else if (string.Equals(r.SkipReason, "No episode ID"))
                {
                    stats.Skipped++;
                }
                // File senza corrispondenza lingua
                else if (string.Equals(r.SkipReason, "No match"))
                {
                    stats.NoMatch++;
                }
                // File senza tracce corrispondenti
                else if (string.Equals(r.SkipReason, "No matching tracks"))
                {
                    stats.NoTracks++;
                }
                // File con sync fallito (frame-sync o speed correction)
                else if (r.ErrorMessage.IndexOf("sync", StringComparison.OrdinalIgnoreCase) >= 0 || r.ErrorMessage.IndexOf("Speed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    stats.SyncFailed++;
                }
                // Errori generici (merge fallito, encoding fallito, etc.)
                else if (r.Status == FileStatus.Error)
                {
                    stats.Errors++;
                }
            }

            return stats;
        }

        /// <summary>
        /// Esegue modalità metadata da CLI tramite preset JSON
        /// </summary>
        /// <param name="opts">Opzioni</param>
        /// <returns>Exit code</returns>
        private static int ExecuteMetadata(Options opts)
        {
            int exitCode = 0;
            string mediaInfoPath;
            string mkvMergePath;
            string mkvPropEditPath;
            string mkvExtractPath;

            try
            {
                MetadataPresetService presetService = new MetadataPresetService(AppSettingsService.Instance.ConfigFolder);
                MkvMetadataPreset preset = presetService.Load(opts.Metadata.PresetPath);
                MkvMetadataPresetValidationResult validation = MetadataPresetService.Validate(preset);
                if (!validation.IsValid)
                {
                    ConsoleHelper.Write(LogSection.Config, LogLevel.Error, validation.ErrorMessage);
                    return 1;
                }

                mediaInfoPath = AppSettingsService.Instance.Settings.Tools.MediaInfoPath;
                if (string.IsNullOrEmpty(mediaInfoPath))
                {
                    mediaInfoPath = "mediainfo";
                }

                MetadataMediaInfoReader reader = new MetadataMediaInfoReader(mediaInfoPath);
                MetadataFileScanner scanner = new MetadataFileScanner(reader);
                List<MkvMetadataRecord> records = scanner.Scan(opts.Metadata.SourcePath, opts.Metadata.Recursive);
                MetadataPipelineEvaluator evaluator = new MetadataPipelineEvaluator();

                mkvMergePath = AppSettingsService.Instance.Settings.Tools.MkvMergePath;
                mkvPropEditPath = AppSettingsService.Instance.Settings.Tools.MkvPropEditPath;
                mkvExtractPath = AppSettingsService.Instance.Settings.Tools.MkvExtractPath;
                MetadataExecutionService executor = new MetadataExecutionService(mkvMergePath, mkvPropEditPath, mkvExtractPath);

                for (int i = 0; i < records.Count; i++)
                {
                    executor.PopulateExistingTags(records[i]);
                    evaluator.AnalyzeRecord(records[i], preset, opts.Metadata.OutputPolicy);
                }

                ValidateMetadataOutputTargets(records, opts.Metadata);

                ConsoleHelper.Write(LogSection.General, LogLevel.Success, AppText.F("cli.metadata.scanned", records.Count));
                ConsoleHelper.Write(LogSection.General, LogLevel.Info, AppText.F("cli.metadata.presetLoaded", preset.Name));

                for (int i = 0; i < records.Count; i++)
                {
                    MkvMetadataExecutionResult result = executor.Execute(records[i], opts.Metadata);
                    if (result.ExitCode == 0)
                    {
                        ConsoleHelper.Write(LogSection.General, LogLevel.Success, AppText.F("cli.metadata.ok", records[i].InputFile));
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.General, LogLevel.Error, AppText.F("cli.metadata.recordError", result.ErrorMessage));
                        exitCode = 1;
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.General, LogLevel.Error, AppText.F("cli.metadata.error", ex.Message));
                exitCode = 1;
            }

            return exitCode;
        }

        /// <summary>
        /// Valida collisioni output metadata
        /// </summary>
        private static void ValidateMetadataOutputTargets(List<MkvMetadataRecord> records, MkvMetadataOptions options)
        {
            Dictionary<string, string> targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (options.OutputPolicy != MkvMetadataOutputPolicy.OutputPath)
            {
                return;
            }

            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].ExecutionMode == MkvMetadataExecutionMode.NoOp)
                {
                    continue;
                }

                string outputFile = MetadataExecutionService.BuildOutputFile(records[i], options);
                if (targets.ContainsKey(outputFile))
                {
                    throw new InvalidOperationException(AppText.F("metadata.error.outputCollision", outputFile));
                }
                if (System.IO.File.Exists(outputFile))
                {
                    throw new InvalidOperationException(AppText.F("metadata.error.outputExists", outputFile));
                }

                targets[outputFile] = records[i].InputFile;
            }
        }

        /// <summary>
        /// Stampa il testo di aiuto completo
        /// </summary>
        private static void PrintHelp()
        {
            Console.WriteLine(AppText.F("cli.help", Utils.GetVersion()));
        }

        /// <summary>
        /// Stampa il riepilogo configurazione corrente
        /// </summary>
        /// <param name="opts">Opzioni validate</param>
        /// <param name="codecPatterns">Pattern codec risolti o null</param>
        private static void PrintConfiguration(Options opts, string[] codecPatterns)
        {
            ConsoleHelper.Write(LogSection.Config, LogLevel.Info, AppText.T("cli.config.title"));
            ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.sourceFolder", opts.SourceFolder));
            if (string.Equals(opts.SourceFolder, opts.LanguageFolder, StringComparison.OrdinalIgnoreCase))
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Phase, AppText.T("cli.config.singleSource"));
            }
            else
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.languageFolder", opts.LanguageFolder));
            }
            ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.targetLanguage", string.Join(", ", opts.TargetLanguage)));
            ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.matchPattern", opts.MatchPattern));
            ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.fileExtensions", string.Join(", ", opts.FileExtensions)));
            if (opts.Overwrite)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.T("cli.config.outputOverwrite"));
            }
            else
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.T("cli.config.outputDestination"));
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.outputFolder", opts.DestinationFolder));
            }

            // Mostra configurazione sync
            ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.speedCorrection", opts.SpeedCorrectionMode));
            if (opts.SpeedCorrectionMode == Options.SPEED_CORRECTION_MANUAL && !string.IsNullOrEmpty(opts.ManualStretchFactor))
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.manualStretch", opts.ManualStretchFactor));
            }
            if (!string.IsNullOrEmpty(opts.AnalysisCropSourcePx) || !string.IsNullOrEmpty(opts.AnalysisCropLanguagePx))
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.analysisCrop", FormatAnalysisCrop(opts.AnalysisCropSourcePx), FormatAnalysisCrop(opts.AnalysisCropLanguagePx)));
            }
            if (opts.SubtitleCanvasRewrite)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.T("cli.config.subtitleCanvasRewrite"));
            }

            if (opts.DeepAnalysis)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Success, AppText.T("cli.config.deepActive"));
                if (opts.AudioDelay != 0 || opts.SubtitleDelay != 0)
                {
                    ConsoleHelper.Write(LogSection.Config, LogLevel.Notice, AppText.F("cli.config.manualOffsetDeep", Utils.FormatDelay(opts.AudioDelay), Utils.FormatDelay(opts.SubtitleDelay)));
                }
            }
            else if (opts.FrameSync)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Success, AppText.T("cli.config.frameSyncActive"));
                if (opts.AudioDelay != 0 || opts.SubtitleDelay != 0)
                {
                    ConsoleHelper.Write(LogSection.Config, LogLevel.Notice, AppText.F("cli.config.manualOffsetFrameSync", Utils.FormatDelay(opts.AudioDelay), Utils.FormatDelay(opts.SubtitleDelay)));
                }
            }
            else
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.audioDelay", Utils.FormatDelay(opts.AudioDelay)));
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.subtitleDelay", Utils.FormatDelay(opts.SubtitleDelay)));
            }
            if (opts.AudioSourceFillThresholdMs > 0)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.audioSourceFill", opts.AudioSourceFillThresholdMs, opts.AudioSourceFillLanguage, FormatAudioSourceFillModes(opts)));
            }
            if (!string.IsNullOrEmpty(opts.AudioFormat))
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.audioFormat", Utils.FormatAudioFormat(opts.AudioFormat), opts.AudioProcessingScope));
                if (opts.AudioDownsample24To16)
                {
                    ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.T("cli.config.audio24To16"));
                }
                if (opts.AudioPeakNormalize)
                {
                    ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.normalization", opts.AudioPeakTargetDb.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
            // Mostra flag filtro
            if (opts.SubOnly)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Phase, AppText.T("cli.config.subOnly"));
            }
            if (opts.AudioOnly)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Phase, AppText.T("cli.config.audioOnly"));
            }
            if (opts.AudioCodec.Count > 0 && codecPatterns != null)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Success, AppText.F("cli.config.selectedCodec", string.Join(", ", opts.AudioCodec), string.Join(", ", codecPatterns)));
            }

            // Mostra filtri tracce sorgente
            if (opts.KeepSourceAudioLangs.Count > 0)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.keepSourceAudio", string.Join(", ", opts.KeepSourceAudioLangs)));
            }
            if (opts.KeepSourceAudioCodec.Count > 0)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.keepSourceAudioCodec", string.Join(", ", opts.KeepSourceAudioCodec)));
            }
            if (opts.KeepSourceSubtitleLangs.Count > 0)
            {
                ConsoleHelper.Write(LogSection.Config, LogLevel.Text, AppText.F("cli.config.keepSourceSubs", string.Join(", ", opts.KeepSourceSubtitleLangs)));
            }

            Console.WriteLine();
        }

        /// <summary>
        /// Formatta le modalità audio source fill attive
        /// </summary>
        /// <param name="opts">Opzioni correnti</param>
        /// <returns>Lista modalità in formato leggibile</returns>
        private static string FormatAudioSourceFillModes(Options opts)
        {
            List<string> modes = new List<string>();
            if (opts.AudioSourceFillStart) { modes.Add(Options.AUDIO_SOURCE_FILL_START); }
            if (opts.AudioSourceFillEnd) { modes.Add(Options.AUDIO_SOURCE_FILL_END); }
            if (opts.AudioSourceFillInsertSilence) { modes.Add(Options.AUDIO_SOURCE_FILL_INSERT_SILENCE); }
            return string.Join(",", modes);
        }

        /// <summary>
        /// Formatta un crop di analisi opzionale
        /// </summary>
        /// <param name="cropPx">Crop nel formato L:R:T:B</param>
        /// <returns>Crop o off</returns>
        private static string FormatAnalysisCrop(string cropPx)
        {
            return !string.IsNullOrEmpty(cropPx) ? cropPx : AppText.T("cli.off");
        }

        /// <summary>
        /// Stampa il report dettagliato con tabelle
        /// </summary>
        /// <param name="records">Lista record elaborazione</param>
        /// <param name="isDryRun">Se in modalità dry run</param>
        private static void PrintDetailedReport(List<FileProcessingRecord> records, bool isDryRun)
        {
            // Filtra solo i record elaborati con successo (o dry run)
            List<FileProcessingRecord> validRecords = new List<FileProcessingRecord>();
            for (int i = 0; i < records.Count; i++)
            {
                if (records[i].Success || (isDryRun && !string.IsNullOrEmpty(records[i].LangFileName)))
                {
                    validRecords.Add(records[i]);
                }
            }

            if (validRecords.Count > 0)
            {
                ConsoleHelper.Write(LogSection.Report, LogLevel.Phase, "\n========================================");
                ConsoleHelper.Write(LogSection.Report, LogLevel.Phase, AppText.T("cli.report.detailedTitle"));
                ConsoleHelper.Write(LogSection.Report, LogLevel.Phase, "========================================\n");

                // Tabella 1: Source Files
                ConsoleHelper.Write(LogSection.Report, LogLevel.Info, AppText.T("cli.report.sourceFiles"));
                ConsoleHelper.Write(LogSection.Report, LogLevel.Text, "  " + Utils.PadRight(AppText.T("cli.report.episode"), 12) + Utils.PadRight(AppText.T("cli.report.audio"), 20) + Utils.PadRight(AppText.T("cli.report.subtitles"), 20) + Utils.PadRight(AppText.T("cli.report.size"), 12));
                ConsoleHelper.Write(LogSection.Report, LogLevel.Debug, "  " + new string('-', 64));

                for (int i = 0; i < validRecords.Count; i++)
                {
                    FileProcessingRecord r = validRecords[i];
                    string line = "  " + Utils.PadRight(r.EpisodeId, 12) + Utils.PadRight(Utils.FormatLangs(r.SourceAudioLangs), 20) + Utils.PadRight(Utils.FormatLangs(r.SourceSubLangs), 20) + Utils.PadRight(Utils.FormatSize(r.SourceSize), 12);
                    ConsoleHelper.Write(LogSection.Report, LogLevel.Text, line);
                }

                Console.WriteLine();

                // Tabella 2: Language Files
                ConsoleHelper.Write(LogSection.Report, LogLevel.Info, AppText.T("cli.report.languageFiles"));
                ConsoleHelper.Write(LogSection.Report, LogLevel.Text, "  " + Utils.PadRight(AppText.T("cli.report.episode"), 12) + Utils.PadRight(AppText.T("cli.report.audio"), 20) + Utils.PadRight(AppText.T("cli.report.subtitles"), 20) + Utils.PadRight(AppText.T("cli.report.size"), 12));
                ConsoleHelper.Write(LogSection.Report, LogLevel.Debug, "  " + new string('-', 64));

                for (int i = 0; i < validRecords.Count; i++)
                {
                    FileProcessingRecord r = validRecords[i];
                    string line = "  " + Utils.PadRight(r.EpisodeId, 12) + Utils.PadRight(Utils.FormatLangs(r.LangAudioLangs), 20) + Utils.PadRight(Utils.FormatLangs(r.LangSubLangs), 20) + Utils.PadRight(Utils.FormatSize(r.LangSize), 12);
                    ConsoleHelper.Write(LogSection.Report, LogLevel.Text, line);
                }

                Console.WriteLine();

                // Tabella 3: Result Files
                ConsoleHelper.Write(LogSection.Report, LogLevel.Info, AppText.T("cli.report.resultFiles"));
                ConsoleHelper.Write(LogSection.Report, LogLevel.Text, "  " + Utils.PadRight(AppText.T("cli.report.episode"), 12) + Utils.PadRight(AppText.T("cli.report.audio"), 15) + Utils.PadRight(AppText.T("cli.report.subtitles"), 15) + Utils.PadRight(AppText.T("cli.report.size"), 10) + Utils.PadRight(AppText.T("cli.report.delay"), 12) + Utils.PadRight(AppText.T("cli.report.frameSync"), 10) + Utils.PadRight(AppText.T("cli.report.frameSyncConfidence"), 8) + Utils.PadRight(AppText.T("cli.report.deep"), 10) + Utils.PadRight(AppText.T("cli.report.speed"), 10) + Utils.PadRight(AppText.T("cli.report.merge"), 10));
                ConsoleHelper.Write(LogSection.Report, LogLevel.Debug, "  " + new string('-', 112));

                for (int i = 0; i < validRecords.Count; i++)
                {
                    FileProcessingRecord r = validRecords[i];
                    string sizeStr = isDryRun ? AppText.T("cli.na") : Utils.FormatSize(r.ResultSize);
                    string delayStr = Utils.FormatDelay(r.AudioDelayApplied);
                    string frameSyncStr = r.FrameSyncTimeMs > 0 ? r.FrameSyncTimeMs + "ms" : "-";
                    string frameSyncConfidenceStr = r.FrameSyncResult != null ? r.FrameSyncResult.Confidence.ToString("P0", System.Globalization.CultureInfo.InvariantCulture) : "-";
                    string deepStr = r.DeepAnalysisApplied && r.DeepAnalysisMap != null ? AppText.F("cli.report.deepOps", r.DeepAnalysisMap.Operations.Count) : "-";
                    string speedStr = r.SpeedCorrectionTimeMs > 0 ? r.SpeedCorrectionTimeMs + "ms" : "-";
                    string mergeStr = r.MergeTimeMs > 0 ? r.MergeTimeMs + "ms" : (isDryRun ? AppText.T("cli.na") : "-");

                    string line = "  " + Utils.PadRight(r.EpisodeId, 12) + Utils.PadRight(Utils.FormatLangs(r.ResultAudioLangs), 15) + Utils.PadRight(Utils.FormatLangs(r.ResultSubLangs), 15) + Utils.PadRight(sizeStr, 10) + Utils.PadRight(delayStr, 12) + Utils.PadRight(frameSyncStr, 10) + Utils.PadRight(frameSyncConfidenceStr, 8) + Utils.PadRight(deepStr, 10) + Utils.PadRight(speedStr, 10) + Utils.PadRight(mergeStr, 10);
                    ConsoleHelper.Write(LogSection.Report, LogLevel.Text, line);
                }

                Console.WriteLine();
            }
        }

        /// <summary>
        /// Stampa il riepilogo elaborazione finale
        /// </summary>
        /// <param name="stats">Statistiche elaborazione</param>
        private static void PrintSummary(ProcessingStats stats)
        {
            ConsoleHelper.Write(LogSection.Report, LogLevel.Phase, "\n========================================");
            ConsoleHelper.Write(LogSection.Report, LogLevel.Phase, AppText.T("cli.report.summaryTitle"));
            ConsoleHelper.Write(LogSection.Report, LogLevel.Phase, "========================================");
            ConsoleHelper.Write(LogSection.Report, LogLevel.Success, AppText.F("cli.report.processed", stats.Processed));
            ConsoleHelper.Write(LogSection.Report, LogLevel.Info, AppText.F("cli.report.skipped", stats.Skipped));
            ConsoleHelper.Write(LogSection.Report, LogLevel.Info, AppText.F("cli.report.noMatch", stats.NoMatch));
            ConsoleHelper.Write(LogSection.Report, LogLevel.Info, AppText.F("cli.report.noTracks", stats.NoTracks));

            if (stats.SyncFailed > 0)
            {
                ConsoleHelper.Write(LogSection.Report, LogLevel.Info, AppText.F("cli.report.syncFailed", stats.SyncFailed));
            }

            ConsoleHelper.Write(LogSection.Report, stats.Errors > 0 ? LogLevel.Error : LogLevel.Success,
                AppText.F("cli.report.errors", stats.Errors));

            Console.WriteLine();
        }

        #endregion
    }
}
