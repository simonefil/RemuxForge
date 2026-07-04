using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using RemuxForge.Core.Transcoding;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// Gestione percorsi output, merge e encoding finale della pipeline
    /// </summary>
    public class PipelineOutputManager
    {
        #region Metodi pubblici

        /// <summary>
        /// Prepara path temporaneo e finale per l'output
        /// </summary>
        /// <param name="sourceFilePath">Path file sorgente</param>
        /// <param name="options">Opzioni operative</param>
        /// <param name="tempOutput">Path temporaneo risultante</param>
        /// <param name="finalOutput">Path finale risultante</param>
        public void PrepareOutputPaths(string sourceFilePath, Options options, out string tempOutput, out string finalOutput)
        {
            string sourceDir;
            string sourceNameNoExt;
            string normalizedSource;
            string normalizedFolder;
            string relativePath;
            string destDir;
            if (options.Overwrite)
            {
                sourceDir = Path.GetDirectoryName(sourceFilePath);
                if (string.IsNullOrEmpty(sourceDir))
                {
                    sourceDir = Directory.GetCurrentDirectory();
                }
                sourceNameNoExt = Path.GetFileNameWithoutExtension(sourceFilePath);
                tempOutput = Path.Combine(sourceDir, sourceNameNoExt + "_TEMP.mkv");
                finalOutput = sourceFilePath;
            }
            else
            {
                normalizedSource = this.NormalizePath(sourceFilePath);
                normalizedFolder = this.NormalizePath(options.SourceFolder);
                relativePath = normalizedSource.Substring(normalizedFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                finalOutput = Path.Combine(options.DestinationFolder, relativePath);
                tempOutput = finalOutput;

                destDir = Path.GetDirectoryName(finalOutput);
                if (string.IsNullOrEmpty(destDir))
                {
                    destDir = Directory.GetCurrentDirectory();
                }
                if (!Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }
            }
        }

        /// <summary>
        /// Calcola il path finale di output
        /// </summary>
        /// <param name="sourceFilePath">Path file sorgente</param>
        /// <param name="options">Opzioni operative</param>
        /// <returns>Path finale</returns>
        public string ComputeFinalOutputPath(string sourceFilePath, Options options)
        {
            string finalOutput;
            string normalizedSource;
            string normalizedFolder;
            string relativePath;
            if (options.Overwrite)
            {
                finalOutput = sourceFilePath;
            }
            else
            {
                normalizedSource = this.NormalizePath(sourceFilePath);
                normalizedFolder = this.NormalizePath(options.SourceFolder);
                relativePath = normalizedSource.Substring(normalizedFolder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                finalOutput = Path.Combine(options.DestinationFolder, relativePath);
            }

            return finalOutput;
        }

        /// <summary>
        /// Esegue mkvmerge e aggiorna il record con esito e tempi
        /// </summary>
        /// <param name="record">Record in elaborazione</param>
        /// <param name="mergeArgs">Argomenti mkvmerge</param>
        /// <param name="tempOutput">Path temporaneo</param>
        /// <param name="finalOutput">Path finale</param>
        /// <param name="options">Opzioni operative</param>
        /// <param name="mkvService">Servizio mkvmerge</param>
        public void RunMergeAndRecord(FileProcessingRecord record, List<string> mergeArgs, string tempOutput, string finalOutput, Options options, MkvToolsService mkvService)
        {
            Stopwatch mergeStopwatch;
            string mergeOutput;
            int exitCode;
            FileInfo resultFileInfo;
            if (options.DryRun)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Phase, "  [DRY-RUN] " + mkvService.FormatMergeCommand(mergeArgs));
                record.Success = true;
                record.Status = FileStatus.Done;
            }
            else
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Info, "  Unione in corso...");
                ConsoleHelper.Progress(LogSection.Merge, 65, "Merge: mux");

                mergeStopwatch = new Stopwatch();
                mergeStopwatch.Start();

                exitCode = mkvService.ExecuteMerge(mergeArgs, out mergeOutput);

                mergeStopwatch.Stop();
                record.MergeTimeMs = mergeStopwatch.ElapsedMilliseconds;

                if (exitCode == 0 || exitCode == 1)
                {
                    ConsoleHelper.Write(LogSection.Merge, LogLevel.Success, "  Unione completata (" + record.MergeTimeMs + "ms)");
                    ConsoleHelper.Progress(LogSection.Merge, 100, "Merge: completato");

                    if (options.Overwrite)
                    {
                        File.Delete(finalOutput);
                        File.Move(tempOutput, finalOutput);
                        ConsoleHelper.Write(LogSection.Merge, LogLevel.Success, "  File originale sostituito");
                    }

                    if (File.Exists(finalOutput))
                    {
                        resultFileInfo = new FileInfo(finalOutput);
                        record.ResultSize = resultFileInfo.Length;
                    }

                    record.Success = true;
                    record.Status = FileStatus.Done;
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Merge, LogLevel.Error, "  mkvmerge fallito con codice " + exitCode);
                    if (!string.IsNullOrEmpty(mergeOutput))
                    {
                        ConsoleHelper.Write(LogSection.Merge, LogLevel.Error, "  Output: " + mergeOutput);
                    }

                    FileHelper.DeleteTempFile(tempOutput);

                    record.ErrorMessage = "Merge fallito: codice " + exitCode;
                    record.Status = FileStatus.Error;
                }
            }
        }

        /// <summary>
        /// Esegue encoding opzionale e aggiorna il record con esito e tempi
        /// </summary>
        /// <param name="record">Record in elaborazione</param>
        /// <param name="mergedFile">File merge da codificare</param>
        /// <param name="options">Opzioni operative</param>
        /// <param name="ffmpegPath">Percorso ffmpeg</param>
        /// <param name="fileUpdated">Callback aggiornamento record</param>
        public void RunEncodingAndRecord(FileProcessingRecord record, string mergedFile, Options options, string ffmpegPath, Action<FileProcessingRecord> fileUpdated)
        {
            EncodingProfile profile;
            VideoEncodingService encService;
            Stopwatch encStopwatch;
            FileInfo encodedInfo;
            bool encSuccess;
            profile = AppSettingsService.Instance.GetProfile(options.EncodingProfileName);
            if (profile == null)
            {
                ConsoleHelper.Write(LogSection.Encode, LogLevel.Warning, "  Profilo '" + options.EncodingProfileName + "' non trovato, encoding saltato");
                return;
            }

            record.Status = FileStatus.Encoding;
            record.EncodingProfileName = profile.Name;
            if (fileUpdated != null)
            {
                fileUpdated(record);
            }

            encService = new VideoEncodingService(ffmpegPath);
            record.EncodingCommand = encService.BuildCommandString(mergedFile, mergedFile, profile);

            ConsoleHelper.Write(LogSection.Encode, LogLevel.Info, "  Encoding con profilo '" + profile.Name + "' (" + profile.Codec + ")...");

            encStopwatch = new Stopwatch();
            encStopwatch.Start();

            encSuccess = encService.Encode(mergedFile, mergedFile, profile, line =>
            {
                ConsoleHelper.Write(LogSection.Encode, LogLevel.Debug, "  " + line);
            });

            encStopwatch.Stop();
            record.EncodingTimeMs = encStopwatch.ElapsedMilliseconds;

            if (encSuccess)
            {
                if (File.Exists(mergedFile))
                {
                    encodedInfo = new FileInfo(mergedFile);
                    record.EncodedSize = encodedInfo.Length;
                }

                ConsoleHelper.Write(LogSection.Encode, LogLevel.Success, "  Encoding completato (" + record.EncodingTimeMs + "ms)");
                record.Success = true;
                record.Status = FileStatus.Done;
            }
            else
            {
                ConsoleHelper.Write(LogSection.Encode, LogLevel.Error, "  Encoding fallito");
                record.ErrorMessage = "Encoding fallito con profilo " + profile.Name;
                record.Status = FileStatus.Error;
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Normalizza un percorso destinazione in formato assoluto senza separatori finali
        /// </summary>
        /// <param name="path">Percorso da normalizzare</param>
        /// <returns>Percorso normalizzato</returns>
        private string NormalizePath(string path)
        {
            string result = path;

            if (!string.IsNullOrEmpty(path))
            {
                // Il confronto destinazione/source usa path assoluti per evitare falsi negativi
                result = Path.GetFullPath(path);
                result = result.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }

            return result;
        }

        #endregion
    }
}
