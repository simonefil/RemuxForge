using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

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
        public List<FileProcessingRecord> Scan(Options options, bool needsMerge)
        {
            List<FileProcessingRecord> records = new List<FileProcessingRecord>();
            Dictionary<string, string> languageIndex = new Dictionary<string, string>();
            string extList = string.Join(", ", options.FileExtensions);
            List<string> sourceFiles = this.FindVideoFiles(options.SourceFolder, options.FileExtensions, options.Recursive);
            bool singleFileMode = File.Exists(options.SourceFolder);

            this._log(LogSection.General, LogLevel.Success, "Trovati " + sourceFiles.Count + " file sorgente (" + extList + ")");

            if (needsMerge)
            {
                if (singleFileMode)
                {
                    this._log(LogSection.General, LogLevel.Success, "File lingua singolo selezionato");
                }
                else
                {
                    this._log(LogSection.General, LogLevel.Info, "Indicizzazione cartella lingua...");
                    List<string> languageFiles = this.FindVideoFiles(options.LanguageFolder, options.FileExtensions, options.Recursive);

                    for (int i = 0; i < languageFiles.Count; i++)
                    {
                        string langFileName = Path.GetFileName(languageFiles[i]);
                        string langEpisodeId = this.GetEpisodeIdentifier(langFileName, options.MatchPattern);
                        if (!string.IsNullOrEmpty(langEpisodeId))
                        {
                            languageIndex[langEpisodeId] = languageFiles[i];
                        }
                    }

                    this._log(LogSection.General, LogLevel.Success, "Indicizzati " + languageIndex.Count + " file lingua");
                }
            }

            for (int i = 0; i < sourceFiles.Count; i++)
            {
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
                    if (singleFileMode)
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
                        record.SkipReason = "No episode ID";
                        record.Status = FileStatus.Skipped;
                        records.Add(record);
                        continue;
                    }
                    else if (!languageIndex.ContainsKey(episodeId))
                    {
                        record.SkipReason = "No match";
                        record.Status = FileStatus.Skipped;
                        records.Add(record);
                        continue;
                    }
                    else
                    {
                        record.EpisodeId = episodeId;
                        string languageFilePath = languageIndex[episodeId];
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
        /// Cerca i file video nella cartella usando le estensioni configurate
        /// </summary>
        /// <param name="folder">Cartella da scansionare</param>
        /// <param name="extensions">Estensioni video senza punto</param>
        /// <param name="recursive">True per scansione ricorsiva</param>
        /// <returns>Lista file trovati</returns>
        private List<string> FindVideoFiles(string folder, List<string> extensions, bool recursive)
        {
            List<string> files = new List<string>();

            if (File.Exists(folder))
            {
                string fileExtension = Path.GetExtension(folder).TrimStart('.');
                for (int e = 0; e < extensions.Count; e++)
                {
                    if (string.Equals(fileExtension, extensions[e].Trim().TrimStart('.'), System.StringComparison.OrdinalIgnoreCase))
                    {
                        files.Add(folder);
                        break;
                    }
                }

                return files;
            }

            SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            for (int e = 0; e < extensions.Count; e++)
            {
                // Mantiene il pattern locale estensione-per-estensione per rispettare l'ordine configurato
                string pattern = "*." + extensions[e];
                string[] found = Directory.GetFiles(folder, pattern, searchOption);
                for (int i = 0; i < found.Length; i++)
                {
                    files.Add(found[i]);
                }
            }

            return files;
        }

        #endregion
    }
}
