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
            // Gli indici per episodio servono solo per abbinare due cartelle diverse
            Dictionary<string, List<string>> languageIndex = null;
            Dictionary<string, List<string>> sourceIndex = null;
            string extList = string.Join(", ", MediaContainerFormats.Extensions);
            cancellationToken.ThrowIfCancellationRequested();
            List<string> sourceFiles = this.FindVideoFiles(options.SourceFolder, options.Recursive, cancellationToken);
            bool singleFileMode = File.Exists(options.SourceFolder);
            // Percorso Lang vuoto: la pipeline lo riempie con la cartella Source e ogni file si abbina soltanto a sé stesso
            // Lang e Source si confrontano come cartelle sul disco, non come testo (D22)
            bool selfPairing = !singleFileMode && (string.IsNullOrEmpty(options.LanguageFolder) || PathIdentity.IsSame(options.SourceFolder, options.LanguageFolder));

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
                    languageIndex = this.BuildEpisodeIndex(this.FindVideoFiles(options.LanguageFolder, options.Recursive, cancellationToken), options.MatchPattern, cancellationToken);
                    this._log(LogSection.General, LogLevel.Success, AppText.Count("remux.pipeline.langFilesIndexed", languageIndex.Count));

                    // Lo stesso episodio in due container (ep01.mp4 e ep01.mkv) non ha un abbinamento univoco: il record si salta con i file in conflitto (D03)
                    sourceIndex = this.BuildEpisodeIndex(sourceFiles, options.MatchPattern, cancellationToken);
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

            // L'output è noto solo con Sovrascrivi o con una destinazione
            if (options.Overwrite || !string.IsNullOrEmpty(options.DestinationFolder))
            {
                this.SkipOutputCollisions(records, options, cancellationToken);
            }

            return records;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Due sorgenti che producono lo stesso file di output, o un output che cade su un altro sorgente: come per D03
        /// si saltano tutti i file in conflitto
        /// </summary>
        /// <param name="records">Record trovati, i pendenti in conflitto passano a saltati</param>
        /// <param name="options">Opzioni operative con la destinazione</param>
        /// <param name="cancellationToken">Token di annullamento cooperativo</param>
        private void SkipOutputCollisions(List<FileProcessingRecord> records, Options options, CancellationToken cancellationToken)
        {
            PipelineOutputManager outputManager = new PipelineOutputManager();
            Dictionary<PathIdentity, FileProcessingRecord> sourceByIdentity = new Dictionary<PathIdentity, FileProcessingRecord>();
            Dictionary<PathIdentity, List<FileProcessingRecord>> recordsByOutput = new Dictionary<PathIdentity, List<FileProcessingRecord>>();
            Dictionary<PathIdentity, string> outputNames = new Dictionary<PathIdentity, string>();
            for (int i = 0; i < records.Count; i++)
            {
                sourceByIdentity[new PathIdentity(records[i].SourceFilePath)] = records[i];
            }
            for (int i = 0; i < records.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (records[i].Status != FileStatus.Pending)
                {
                    continue;
                }

                string outputPath = outputManager.ComputeFinalOutputPath(records[i].SourceFilePath, options);
                PathIdentity outputIdentity = new PathIdentity(outputPath);
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

            foreach (KeyValuePair<PathIdentity, List<FileProcessingRecord>> output in recordsByOutput)
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

        /// <summary>
        /// Indicizza i file per identificatore di episodio; i file senza identificatore restano fuori
        /// </summary>
        /// <param name="files">File da indicizzare</param>
        /// <param name="pattern">Pattern regex episodio</param>
        /// <param name="cancellationToken">Token di annullamento cooperativo</param>
        /// <returns>File per identificatore, nell'ordine dato</returns>
        private Dictionary<string, List<string>> BuildEpisodeIndex(List<string> files, string pattern, CancellationToken cancellationToken)
        {
            Dictionary<string, List<string>> index = new Dictionary<string, List<string>>();
            for (int i = 0; i < files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string episodeId = this.GetEpisodeIdentifier(Path.GetFileName(files[i]), pattern);
                if (!string.IsNullOrEmpty(episodeId))
                {
                    if (!index.ContainsKey(episodeId))
                        index[episodeId] = new List<string>();
                    index[episodeId].Add(files[i]);
                }
            }

            return index;
        }

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

            files.AddRange(MediaContainerFormats.EnumerateSupported(folder, recursive, cancellationToken));
            return files;
        }

        #endregion
    }
}
