using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>
    /// Scanner file per ProcessingPipeline
    /// </summary>
    public class PipelineFileScanner
    {
        #region Delegati

        /// <summary>
        /// Scrive una riga di log pipeline
        /// </summary>
        /// <param name="section">Sezione log</param>
        /// <param name="level">Livello log</param>
        /// <param name="text">Testo log</param>
        public delegate void LogWriter(LogSection section, LogLevel level, string text);

        #endregion

        #region Variabili di classe

        private LogWriter _log;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="log">Callback log</param>
        public PipelineFileScanner(LogWriter log)
        {
            this._log = log;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scansiona i file sorgente e lingua secondo le opzioni
        /// </summary>
        /// <param name="options">Opzioni operative</param>
        /// <param name="needsMerge">True se il flusso richiede merge</param>
        /// <returns>Record trovati</returns>
        public List<FileProcessingRecord> Scan(Options options, bool needsMerge, CancellationToken cancellationToken = default)
        {
            List<FileProcessingRecord> records = new List<FileProcessingRecord>();
            Dictionary<string, List<string>> languageIndex = new Dictionary<string, List<string>>();
            Dictionary<string, List<string>> sourceIndex = new Dictionary<string, List<string>>();
            string extList = string.Join(", ", MediaContainerFormats.Extensions);
            cancellationToken.ThrowIfCancellationRequested();
            List<string> sourceFiles = this.FindVideoFiles(options.SourceFolder, options.Recursive, cancellationToken);
            bool singleFileMode = File.Exists(options.SourceFolder);
            // Percorso Lang vuoto: la pipeline lo riempie con la cartella Source e ogni file si abbina soltanto a sé stesso
            // Lang e Source si confrontano come cartelle sul disco, non come testo (D22)
            bool selfPairing = !singleFileMode && (string.IsNullOrEmpty(options.LanguageFolder) || FileHelper.IsSamePath(options.SourceFolder, options.LanguageFolder));

            this._log(LogSection.General, LogLevel.Success, AppText.Count("remux.pipeline.sourceFilesFound", sourceFiles.Count, extList));

            if (needsMerge)
            {
                if (singleFileMode)
                {
                    this._log(LogSection.General, LogLevel.Success, AppText.T("remux.pipeline.singleLangFile"));
                }
                else if (!selfPairing)
                {
                    this._log(LogSection.General, LogLevel.Info, AppText.T("remux.pipeline.indexingLangFolder"));
                    List<string> languageFiles = this.FindVideoFiles(options.LanguageFolder, options.Recursive, cancellationToken);

                    for (int i = 0; i < languageFiles.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string langFileName = Path.GetFileName(languageFiles[i]);
                        string langEpisodeId = this.GetEpisodeIdentifier(langFileName, options.MatchPattern);
                        if (!string.IsNullOrEmpty(langEpisodeId))
                        {
                            if (!languageIndex.ContainsKey(langEpisodeId))
                                languageIndex[langEpisodeId] = new List<string>();
                            languageIndex[langEpisodeId].Add(languageFiles[i]);
                        }
                    }

                    this._log(LogSection.General, LogLevel.Success, AppText.Count("remux.pipeline.langFilesIndexed", languageIndex.Count));
                }
            }

            // Lo stesso episodio in due container (ep01.mp4 e ep01.mkv) non ha un abbinamento univoco: il record si salta con i file in conflitto (D03)
            if (needsMerge && !singleFileMode && !selfPairing)
            {
                for (int i = 0; i < sourceFiles.Count; i++)
                {
                    string sourceEpisodeId = this.GetEpisodeIdentifier(Path.GetFileName(sourceFiles[i]), options.MatchPattern);
                    if (!string.IsNullOrEmpty(sourceEpisodeId))
                    {
                        if (!sourceIndex.ContainsKey(sourceEpisodeId))
                            sourceIndex[sourceEpisodeId] = new List<string>();
                        sourceIndex[sourceEpisodeId].Add(sourceFiles[i]);
                    }
                }
            }

            for (int i = 0; i < sourceFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string sourceFilePath = sourceFiles[i];
                string sourceFileName = Path.GetFileName(sourceFilePath);
                string episodeId = singleFileMode ? sourceFileName : this.GetEpisodeIdentifier(sourceFileName, options.MatchPattern);
                FileProcessingRecord record = new FileProcessingRecord();
                record.SourceFileName = sourceFileName;
                record.SourceFilePath = sourceFilePath;

                FileInfo sourceFileInfo = new FileInfo(sourceFilePath);
                record.SourceSize = sourceFileInfo.Length;

                if (needsMerge)
                {
                    if (selfPairing)
                    {
                        record.EpisodeId = !string.IsNullOrEmpty(episodeId) ? episodeId : sourceFileName;
                        record.LangFileName = sourceFileName;
                        record.LangFilePath = sourceFilePath;
                        record.LangSize = record.SourceSize;
                    }
                    else if (singleFileMode)
                    {
                        string languageFilePath = options.LanguageFolder;
                        record.EpisodeId = episodeId;
                        record.LangFileName = Path.GetFileName(languageFilePath);
                        record.LangFilePath = languageFilePath;

                        FileInfo langFileInfo = new FileInfo(languageFilePath);
                        record.LangSize = langFileInfo.Length;
                    }
                    else if (string.IsNullOrEmpty(episodeId))
                    {
                        record.SkipCode = FileProcessingRecord.SKIP_NO_EPISODE_ID;
                        record.Status = FileStatus.Skipped;
                        records.Add(record);
                        continue;
                    }
                    else if (sourceIndex[episodeId].Count > 1 || (languageIndex.ContainsKey(episodeId) && languageIndex[episodeId].Count > 1))
                    {
                        List<string> conflicting = new List<string>();
                        foreach (string file in sourceIndex[episodeId].Count > 1 ? sourceIndex[episodeId] : languageIndex[episodeId])
                            conflicting.Add(Path.GetFileName(file));
                        record.EpisodeId = episodeId;
                        record.SkipReason = AppText.F(sourceIndex[episodeId].Count > 1 ? "remux.pipeline.duplicateSourceEpisode" : "remux.pipeline.duplicateLangEpisode", string.Join(", ", conflicting));
                        record.Status = FileStatus.Skipped;
                        this._log(LogSection.General, LogLevel.Warning, record.SourceFileName + ": " + record.SkipReason);
                        records.Add(record);
                        continue;
                    }
                    else if (!languageIndex.ContainsKey(episodeId))
                    {
                        record.SkipCode = FileProcessingRecord.SKIP_NO_MATCH;
                        record.Status = FileStatus.Skipped;
                        records.Add(record);
                        continue;
                    }
                    else
                    {
                        record.EpisodeId = episodeId;
                        string languageFilePath = languageIndex[episodeId][0];
                        record.LangFileName = Path.GetFileName(languageFilePath);
                        record.LangFilePath = languageFilePath;

                        FileInfo langFileInfo = new FileInfo(languageFilePath);
                        record.LangSize = langFileInfo.Length;
                    }
                }
                else
                {
                    record.EpisodeId = !string.IsNullOrEmpty(episodeId) ? episodeId : sourceFileName;
                }

                record.Status = FileStatus.Pending;
                records.Add(record);
            }

            // Due sorgenti che producono lo stesso file di output, o un output che cade su un altro sorgente: come per D03
            // si saltano tutti i file in conflitto. L'output è noto solo con Sovrascrivi o con una destinazione
            if (options.Overwrite || !string.IsNullOrEmpty(options.DestinationFolder))
            {
                PipelineOutputManager outputManager = new PipelineOutputManager();
                Dictionary<string, FileProcessingRecord> sourceByIdentity = new Dictionary<string, FileProcessingRecord>(StringComparer.Ordinal);
                Dictionary<string, List<FileProcessingRecord>> recordsByOutput = new Dictionary<string, List<FileProcessingRecord>>(StringComparer.Ordinal);
                Dictionary<string, string> outputNames = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < records.Count; i++)
                {
                    sourceByIdentity[FileHelper.GetPathIdentity(records[i].SourceFilePath)] = records[i];
                }
                for (int i = 0; i < records.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (records[i].Status != FileStatus.Pending)
                    {
                        continue;
                    }

                    string outputPath = outputManager.ComputeFinalOutputPath(records[i].SourceFilePath, options);
                    string outputIdentity = FileHelper.GetPathIdentity(outputPath);
                    if (!recordsByOutput.ContainsKey(outputIdentity))
                    {
                        recordsByOutput[outputIdentity] = new List<FileProcessingRecord>();
                        outputNames[outputIdentity] = Path.GetFileName(outputPath);
                    }
                    recordsByOutput[outputIdentity].Add(records[i]);
                    if (sourceByIdentity.TryGetValue(outputIdentity, out FileProcessingRecord other) && !recordsByOutput[outputIdentity].Contains(other))
                    {
                        recordsByOutput[outputIdentity].Add(other);
                    }
                }

                foreach (KeyValuePair<string, List<FileProcessingRecord>> output in recordsByOutput)
                {
                    if (output.Value.Count < 2)
                    {
                        continue;
                    }

                    List<string> conflicting = new List<string>();
                    foreach (FileProcessingRecord conflict in output.Value)
                        conflicting.Add(conflict.SourceFileName);
                    foreach (FileProcessingRecord conflict in output.Value)
                    {
                        conflict.SkipReason = AppText.F("remux.pipeline.duplicateOutput", outputNames[output.Key], string.Join(", ", conflicting));
                        conflict.Status = FileStatus.Skipped;
                        this._log(LogSection.General, LogLevel.Warning, conflict.SourceFileName + ": " + conflict.SkipReason);
                    }
                }
            }

            return records;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Estrae l'identificatore episodio dal nome file usando la regex configurata
        /// </summary>
        /// <param name="fileName">Nome file sorgente</param>
        /// <param name="pattern">Pattern regex episodio</param>
        /// <returns>Identificatore episodio, oppure stringa vuota</returns>
        private string GetEpisodeIdentifier(string fileName, string pattern)
        {
            string result = "";
            Match match = Regex.Match(fileName, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                // Se la regex contiene gruppi, l'ID stabile è la concatenazione dei gruppi catturati
                if (match.Groups.Count > 1)
                {
                    StringBuilder sb = new StringBuilder();
                    for (int g = 1; g < match.Groups.Count; g++)
                    {
                        if (g > 1)
                        {
                            sb.Append("_");
                        }
                        sb.Append(match.Groups[g].Value);
                    }
                    result = sb.ToString();
                }
                else
                {
                    result = match.Value;
                }
            }

            return result;
        }

        /// <summary>
        /// Cerca i file video con le estensioni accettate (D02)
        /// </summary>
        /// <param name="folder">Cartella da scansionare, o file singolo</param>
        /// <param name="recursive">True per scansione ricorsiva</param>
        /// <returns>Lista file trovati</returns>
        private List<string> FindVideoFiles(string folder, bool recursive, CancellationToken cancellationToken)
        {
            List<string> files = new List<string>();

            if (File.Exists(folder))
            {
                if (MediaContainerFormats.IsSupported(folder))
                {
                    files.Add(folder);
                }

                return files;
            }

            // Il filtro per estensione si applica dopo: il pattern di GetFiles su Linux distingue le maiuscole
            string[] found = Directory.GetFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
            for (int i = 0; i < found.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (MediaContainerFormats.IsSupported(found[i]))
                {
                    files.Add(found[i]);
                }
            }

            return files;
        }

        #endregion
    }
}
