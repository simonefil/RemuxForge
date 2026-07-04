using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace RemuxForge.Core.Metadata
{
    /// <summary>
    /// Esegue modifiche metadata reali tramite mkvpropedit, mkvmerge e mkvextract
    /// </summary>
    public class MetadataExecutionService
    {
        #region Variabili di classe

        /// <summary>
        /// Percorso eseguibile mkvmerge
        /// </summary>
        private string _mkvMergePath;

        /// <summary>
        /// Percorso eseguibile mkvpropedit
        /// </summary>
        private string _mkvPropEditPath;

        /// <summary>
        /// Percorso eseguibile mkvextract
        /// </summary>
        private string _mkvExtractPath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="mkvMergePath">Percorso mkvmerge</param>
        /// <param name="mkvPropEditPath">Percorso mkvpropedit</param>
        /// <param name="mkvExtractPath">Percorso mkvextract</param>
        public MetadataExecutionService(string mkvMergePath, string mkvPropEditPath, string mkvExtractPath = "")
        {
            this._mkvMergePath = !string.IsNullOrEmpty(mkvMergePath) ? mkvMergePath : "mkvmerge";
            this._mkvPropEditPath = !string.IsNullOrEmpty(mkvPropEditPath) ? mkvPropEditPath : "mkvpropedit";
            this._mkvExtractPath = !string.IsNullOrEmpty(mkvExtractPath) ? mkvExtractPath : "mkvextract";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Esegue le modifiche metadata calcolate in analisi
        /// </summary>
        /// <param name="record">Record metadata analizzato</param>
        /// <param name="options">Opzioni runtime metadata</param>
        /// <returns>Risultato esecuzione</returns>
        public MkvMetadataExecutionResult Execute(MkvMetadataRecord record, MkvMetadataOptions options)
        {
            MkvMetadataExecutionResult result = new MkvMetadataExecutionResult();
            string targetFile;

            result.InputFile = record != null ? record.InputFile : "";
            if (record == null)
            {
                result.ExitCode = 1;
                result.ErrorMessage = AppText.T("metadata.execution.nullRecord");
                return result;
            }

            if (record.AnalysisStatus != MkvMetadataAnalysisStatus.Analyzed)
            {
                result.ExitCode = 1;
                result.ErrorMessage = AppText.T("metadata.execution.notAnalyzedRecord");
                return result;
            }

            if (record.Changes == null || record.Changes.Count == 0 || record.ExecutionMode == MkvMetadataExecutionMode.NoOp)
            {
                result.OutputFile = record.InputFile;
                result.CommandText = "NoOp";
                return result;
            }

            if (options != null && options.DryRun)
            {
                result.DryRun = true;
                result.OutputFile = BuildOutputFile(record, options);
                result.CommandText = BuildCommandPreview(record, options);
                return result;
            }

            targetFile = BuildOutputFile(record, options);
            result.OutputFile = targetFile;

            try
            {
                if (record.ExecutionMode == MkvMetadataExecutionMode.PropEdit)
                {
                    result.CommandText = this.ApplyPropEdit(record.InputFile, record.Changes, null, null, null);
                }
                else if (record.ExecutionMode == MkvMetadataExecutionMode.CopyPropEdit)
                {
                    EnsureParentFolder(targetFile);
                    File.Copy(record.InputFile, targetFile, true);
                    result.CommandText = this.ApplyPropEdit(targetFile, record.Changes, null, null, null);
                }
                else if (record.ExecutionMode == MkvMetadataExecutionMode.MkvMerge)
                {
                    result.CommandText = this.ExecuteRemux(record, options, targetFile);
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = 1;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Costruisce anteprima comando per il pannello di analisi
        /// </summary>
        /// <param name="record">Record metadata</param>
        /// <param name="options">Opzioni runtime metadata</param>
        /// <returns>Anteprima testuale comando</returns>
        public static string BuildCommandPreview(MkvMetadataRecord record, MkvMetadataOptions options)
        {
            StringBuilder sb = new StringBuilder();
            string outputFile = BuildOutputFile(record, options);
            bool hasPropEditOperations = false;

            if (record.ExecutionMode == MkvMetadataExecutionMode.NoOp)
                return "NoOp";

            if (record.ExecutionMode == MkvMetadataExecutionMode.CopyPropEdit)
            {
                sb.AppendLine("copy \"" + record.InputFile + "\" \"" + outputFile + "\"");
            }
            else if (record.ExecutionMode == MkvMetadataExecutionMode.MkvMerge)
            {
                sb.AppendLine("mkvmerge -o \"" + outputFile + "\" ... \"" + record.InputFile + "\"");
            }

            for (int i = 0; record.Changes != null && i < record.Changes.Count; i++)
            {
                MkvMetadataOperationType type = record.Changes[i].OperationType;

                switch (type)
                {
                    case MkvMetadataOperationType.SetField:
                    case MkvMetadataOperationType.ClearField:
                    case MkvMetadataOperationType.SetExclusiveFlag:
                    case MkvMetadataOperationType.AddOrUpdateTrackStatisticsTags:
                    case MkvMetadataOperationType.DeleteTrackStatisticsTags:
                    case MkvMetadataOperationType.SetTagField:
                    case MkvMetadataOperationType.ClearTagField:
                    case MkvMetadataOperationType.ClearTags:
                        hasPropEditOperations = true;
                        break;
                }

                if (hasPropEditOperations)
                    break;
            }

            if (hasPropEditOperations)
                sb.Append("mkvpropedit \"" + outputFile + "\" ...");

            return sb.ToString();
        }

        /// <summary>
        /// Costruisce il percorso output effettivo per un record metadata
        /// </summary>
        /// <param name="record">Record metadata</param>
        /// <param name="options">Opzioni runtime metadata</param>
        /// <returns>File output</returns>
        public static string BuildOutputFile(MkvMetadataRecord record, MkvMetadataOptions options)
        {
            if (options == null || options.OutputPolicy == MkvMetadataOutputPolicy.Overwrite)
                return record.InputFile;

            string relativeFolder = options.PreserveFolderStructure ? record.RelativeFolder : "";
            string folder = !string.IsNullOrEmpty(relativeFolder) ? Path.Combine(options.OutputDir, relativeFolder) : options.OutputDir;
            string fileName = Path.GetFileName(record.InputFile);
            if (!fileName.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
                fileName += ".mkv";

            return Path.Combine(folder, fileName);
        }

        /// <summary>
        /// Popola i tag MKV esistenti nel record per il confronto prima/dopo
        /// </summary>
        /// <param name="record">Record metadata</param>
        public void PopulateExistingTags(MkvMetadataRecord record)
        {
            XDocument document;

            if (record == null || record.FileInfo == null || string.IsNullOrEmpty(record.InputFile))
                return;

            document = this.LoadExistingTags(record.InputFile);
            PopulateExistingTagsOnFileInfo(record.FileInfo, document);
            if (record.OriginalFileInfo != null)
                PopulateExistingTagsOnFileInfo(record.OriginalFileInfo, document);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Esegue remux mkvmerge per rimozioni tracce e applica poi mkvpropedit sul risultato
        /// </summary>
        private string ExecuteRemux(MkvMetadataRecord record, MkvMetadataOptions options, string targetFile)
        {
            string remuxOutput = targetFile;
            string tempFile = "";
            string commandText;
            List<string> args;
            ProcessResult processResult;
            List<string> removedSelectors = new List<string>();
            Dictionary<string, string> selectorMap;
            Dictionary<string, string> trackUidMap;

            // Usa un file temporaneo per overwrite, così l'originale viene sostituito solo a operazione completata
            if (options == null || options.OutputPolicy == MkvMetadataOutputPolicy.Overwrite)
            {
                string inputFolder = Path.GetDirectoryName(record.InputFile);
                tempFile = Path.Combine(!string.IsNullOrEmpty(inputFolder) ? inputFolder : ".", Path.GetFileNameWithoutExtension(record.InputFile) + ".remuxforge." + Guid.NewGuid().ToString("N") + ".tmp.mkv");
                remuxOutput = tempFile;
            }
            else
            {
                EnsureParentFolder(remuxOutput);
            }

            // Traduce le modifiche RemoveTrack in selector logici da escludere nel remux
            for (int i = 0; i < record.Changes.Count; i++)
            {
                MkvMetadataChange change = record.Changes[i];

                if (change.OperationType == MkvMetadataOperationType.RemoveTrack && !string.IsNullOrEmpty(change.TrackSelector) && !removedSelectors.Contains(change.TrackSelector))
                    removedSelectors.Add(change.TrackSelector);
            }

            // Esegue prima il remux, poi applica eventuali modifiche metadata rimaste sul file prodotto
            selectorMap = BuildTrackSelectorMap(record.FileInfo.Tracks, removedSelectors);
            args = this.BuildRemuxArguments(record.InputFile, remuxOutput, removedSelectors);
            commandText = FormatCommand(this._mkvMergePath, args);
            processResult = ProcessRunner.Run(this._mkvMergePath, args.ToArray());
            if (processResult.ExitCode != 0)
            {
                CleanupTemp(tempFile);
                throw new InvalidOperationException(AppText.F("metadata.execution.mkvmergeFailed", LastErrorLine(!string.IsNullOrEmpty(processResult.Stderr) ? processResult.Stderr : processResult.Stdout)));
            }

            trackUidMap = this.BuildTrackUidMap(remuxOutput, record.FileInfo.Tracks, selectorMap);
            string propEditCommand = this.ApplyPropEdit(remuxOutput, record.Changes, removedSelectors, selectorMap, trackUidMap);
            if (propEditCommand != "mkvpropedit: NoOp")
                commandText += Environment.NewLine + propEditCommand;

            if (!string.IsNullOrEmpty(tempFile))
                ReplaceOriginal(tempFile, record.InputFile);

            return commandText;
        }

        /// <summary>
        /// Applica proprietà, tag e statistiche traccia tramite mkvpropedit
        /// </summary>
        private string ApplyPropEdit(string filePath, List<MkvMetadataChange> changes, List<string> removedSelectors, Dictionary<string, string> selectorMap, Dictionary<string, string> trackUidMap)
        {
            List<string> args = new List<string>();
            List<string> tempFiles = new List<string>();
            List<MkvMetadataChange> tagChanges = new List<MkvMetadataChange>();
            bool addStatisticsTags = false;
            bool deleteStatisticsTags = false;
            string editSelector;
            args.Add(filePath);

            try
            {
                // Separa le modifiche propedit dirette da tag XML e flag statistiche
                for (int i = 0; i < changes.Count; i++)
                {
                    MkvMetadataChange change = changes[i];
                    bool isTagOperation;
                    bool isDirectPropEditOperation;

                    if (change.OperationType == MkvMetadataOperationType.AddOrUpdateTrackStatisticsTags)
                    {
                        addStatisticsTags = true;
                        continue;
                    }

                    if (change.OperationType == MkvMetadataOperationType.DeleteTrackStatisticsTags)
                    {
                        deleteStatisticsTags = true;
                        continue;
                    }

                    isTagOperation = change.OperationType == MkvMetadataOperationType.SetTagField || change.OperationType == MkvMetadataOperationType.ClearTagField || change.OperationType == MkvMetadataOperationType.ClearTags;
                    if (isTagOperation)
                    {
                        if (removedSelectors == null || string.IsNullOrEmpty(change.TrackSelector) || !removedSelectors.Contains(change.TrackSelector))
                            tagChanges.Add(change);

                        continue;
                    }

                    isDirectPropEditOperation = change.OperationType == MkvMetadataOperationType.SetField || change.OperationType == MkvMetadataOperationType.ClearField || change.OperationType == MkvMetadataOperationType.SetExclusiveFlag;
                    if (!isDirectPropEditOperation)
                        continue;

                    if (removedSelectors != null && !string.IsNullOrEmpty(change.TrackSelector) && removedSelectors.Contains(change.TrackSelector))
                        continue;

                    if (string.IsNullOrEmpty(change.MkvPropEditProperty))
                        continue;

                    editSelector = ResolveRemuxedSelector(change.TrackSelector, selectorMap);
                    if (!string.IsNullOrEmpty(change.TrackSelector) && string.IsNullOrEmpty(editSelector))
                        continue;

                    args.Add("--edit");
                    args.Add(!string.IsNullOrEmpty(editSelector) ? editSelector : "info");

                    if (change.OperationType == MkvMetadataOperationType.ClearField)
                    {
                        args.Add("--delete");
                        args.Add(change.MkvPropEditProperty);
                    }
                    else
                    {
                        MetadataFieldDefinition field;
                        string propEditValue;
                        string errorMessage;

                        if (!MetadataFieldRegistry.TryGet(change.FieldKey, out field))
                            throw new InvalidOperationException(AppText.F("metadata.validation.unknownField", change.FieldKey));

                        if (!MetadataFieldRegistry.ValidateWritableValue(change.FieldKey, change.AfterValue, field.IsClearable, out propEditValue, out errorMessage))
                            throw new InvalidOperationException(errorMessage);

                        args.Add("--set");
                        args.Add(change.MkvPropEditProperty + "=" + propEditValue);
                    }
                }

                // I tag MKV vanno riscritti tramite un singolo file XML temporaneo
                if (tagChanges.Count > 0)
                {
                    string tagFile = this.BuildTagsXmlFile(filePath, tagChanges, trackUidMap);
                    tempFiles.Add(tagFile);
                    args.Add("--tags");
                    args.Add("all:" + tagFile);
                }

                if (addStatisticsTags)
                    args.Add("--add-track-statistics-tags");

                if (deleteStatisticsTags)
                    args.Add("--delete-track-statistics-tags");

                // Nessun argomento oltre al file: non lanciare mkvpropedit
                if (args.Count == 1)
                    return "mkvpropedit: NoOp";

                ProcessResult processResult = ProcessRunner.Run(this._mkvPropEditPath, args.ToArray());
                if (processResult.ExitCode != 0)
                    throw new InvalidOperationException(AppText.F("metadata.execution.mkvpropeditFailed", LastErrorLine(!string.IsNullOrEmpty(processResult.Stderr) ? processResult.Stderr : processResult.Stdout)));

                return FormatCommand(this._mkvPropEditPath, args);
            }
            finally
            {
                for (int i = 0; i < tempFiles.Count; i++)
                {
                    CleanupTemp(tempFiles[i]);
                }
            }
        }

        /// <summary>
        /// Costruisce gli argomenti mkvmerge conservando solo le tracce non rimosse
        /// </summary>
        private List<string> BuildRemuxArguments(string inputFile, string outputFile, List<string> removedSelectors)
        {
            List<string> args = new List<string>();
            MkvToolsService mkvTools = new MkvToolsService(this._mkvMergePath);
            MkvFileInfo info = mkvTools.GetFileInfo(inputFile);
            List<int> videoKeep = new List<int>();
            List<int> audioKeep = new List<int>();
            List<int> subtitleKeep = new List<int>();
            bool removeVideo = HasRemovedKind(removedSelectors, "track:v");
            bool removeAudio = HasRemovedKind(removedSelectors, "track:a");
            bool removeSubtitle = HasRemovedKind(removedSelectors, "track:s");
            int videoIndex = 0;
            int audioIndex = 0;
            int subtitleIndex = 0;

            // mkvmerge usa id interni, mentre la pipeline usa selector logici track:v1/a1/s1
            if (info == null)
                throw new InvalidOperationException(AppText.T("metadata.execution.cannotReadMkvmergeJson"));

            args.Add("-o");
            args.Add(outputFile);

            // Costruisce le liste di id da conservare per ogni tipo di traccia
            for (int i = 0; i < info.Tracks.Count; i++)
            {
                TrackInfo track = info.Tracks[i];
                if (track.Type == "video")
                {
                    videoIndex++;
                    if (!removedSelectors.Contains("track:v" + videoIndex.ToString(CultureInfo.InvariantCulture)))
                        videoKeep.Add(track.Id);
                }
                else if (track.Type == "audio")
                {
                    audioIndex++;
                    if (!removedSelectors.Contains("track:a" + audioIndex.ToString(CultureInfo.InvariantCulture)))
                        audioKeep.Add(track.Id);
                }
                else if (track.Type == "subtitles")
                {
                    subtitleIndex++;
                    if (!removedSelectors.Contains("track:s" + subtitleIndex.ToString(CultureInfo.InvariantCulture)))
                        subtitleKeep.Add(track.Id);
                }
            }

            // Aggiunge filtri mkvmerge solo per i tipi di traccia coinvolti da rimozioni
            AddTrackSelection(args, removeVideo, videoKeep, "--video-tracks", "-D");
            AddTrackSelection(args, removeAudio, audioKeep, "--audio-tracks", "-A");
            AddTrackSelection(args, removeSubtitle, subtitleKeep, "--subtitle-tracks", "-S");
            args.Add(inputFile);

            return args;
        }

        /// <summary>
        /// Aggiunge selezione tracce mkvmerge per un tipo di traccia
        /// </summary>
        private static void AddTrackSelection(List<string> args, bool hasRemoval, List<int> keepIds, string optionName, string noneOption)
        {
            if (!hasRemoval)
                return;

            if (keepIds.Count == 0)
            {
                args.Add(noneOption);
                return;
            }

            StringBuilder ids = new StringBuilder();
            for (int i = 0; i < keepIds.Count; i++)
            {
                if (i > 0)
                    ids.Append(",");

                ids.Append(keepIds[i].ToString(CultureInfo.InvariantCulture));
            }

            args.Add(optionName);
            args.Add(ids.ToString());
        }

        /// <summary>
        /// Costruisce la mappa tra selector originali e selector del file dopo remux
        /// </summary>
        /// <param name="tracks">Tracce del modello analizzato</param>
        /// <param name="removedSelectors">Selector rimossi dal remux</param>
        /// <returns>Mappa selector originale -> selector dopo remux</returns>
        private static Dictionary<string, string> BuildTrackSelectorMap(List<MkvMetadataTrackInfo> tracks, List<string> removedSelectors)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int videoIndex = 0;
            int audioIndex = 0;
            int subtitleIndex = 0;

            if (tracks == null)
                return result;

            for (int i = 0; i < tracks.Count; i++)
            {
                MkvMetadataTrackInfo track = tracks[i];
                string sourceSelector = track.TrackSelector != null ? track.TrackSelector : "";
                if (string.IsNullOrEmpty(sourceSelector))
                    continue;

                if (removedSelectors != null && removedSelectors.Contains(sourceSelector))
                    continue;

                if (track.TrackKind == "video")
                {
                    videoIndex++;
                    result[sourceSelector] = "track:v" + videoIndex.ToString(CultureInfo.InvariantCulture);
                }
                else if (track.TrackKind == "audio")
                {
                    audioIndex++;
                    result[sourceSelector] = "track:a" + audioIndex.ToString(CultureInfo.InvariantCulture);
                }
                else if (track.TrackKind == "subtitles")
                {
                    subtitleIndex++;
                    result[sourceSelector] = "track:s" + subtitleIndex.ToString(CultureInfo.InvariantCulture);
                }
            }

            return result;
        }

        /// <summary>
        /// Risolve il selector valido per il file remuxato
        /// </summary>
        /// <param name="selector">Selector originale della modifica</param>
        /// <param name="selectorMap">Mappa selector post-remux o null</param>
        /// <returns>Selector da usare con mkvpropedit o stringa vuota se la traccia non esiste più</returns>
        private static string ResolveRemuxedSelector(string selector, Dictionary<string, string> selectorMap)
        {
            string text = selector != null ? selector : "";
            string mapped;

            if (string.IsNullOrEmpty(text))
                return "";

            if (selectorMap == null)
                return text;

            if (selectorMap.TryGetValue(text, out mapped))
                return mapped;

            return "";
        }

        /// <summary>
        /// Risolve il TrackUID valido per il file remuxato
        /// </summary>
        /// <param name="trackUid">TrackUID originale della modifica</param>
        /// <param name="trackUidMap">Mappa TrackUID post-remux o null</param>
        /// <returns>TrackUID da usare nel file XML tags</returns>
        private static string ResolveRemuxedTrackUid(string trackUid, Dictionary<string, string> trackUidMap)
        {
            string text = trackUid != null ? trackUid.Trim() : "";
            string mapped;

            if (string.IsNullOrEmpty(text))
                return "";

            if (trackUidMap == null)
                return text;

            if (trackUidMap.TryGetValue(text, out mapped))
                return mapped;

            return text;
        }

        /// <summary>
        /// Costruisce la mappa tra TrackUID originali e TrackUID prodotti dal remux
        /// </summary>
        /// <param name="remuxedFile">File remuxato da leggere con mkvmerge</param>
        /// <param name="tracks">Tracce del modello analizzato</param>
        /// <param name="selectorMap">Mappa selector originale -> selector dopo remux</param>
        /// <returns>Mappa TrackUID originale -> TrackUID dopo remux</returns>
        private Dictionary<string, string> BuildTrackUidMap(string remuxedFile, List<MkvMetadataTrackInfo> tracks, Dictionary<string, string> selectorMap)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> selectorToUid = this.ReadTrackUidsBySelector(remuxedFile);

            if (tracks == null)
                return result;

            for (int i = 0; i < tracks.Count; i++)
            {
                MkvMetadataTrackInfo track = tracks[i];
                string oldUid = track.TrackUniqueId != null ? track.TrackUniqueId.Trim() : "";
                string newSelector = ResolveRemuxedSelector(track.TrackSelector, selectorMap);
                string newUid;

                if (string.IsNullOrEmpty(oldUid) || string.IsNullOrEmpty(newSelector))
                    continue;

                if (selectorToUid.TryGetValue(newSelector, out newUid) && !string.IsNullOrEmpty(newUid))
                    result[oldUid] = newUid;
            }

            return result;
        }

        /// <summary>
        /// Legge i TrackUID del file corrente indicizzati per selector logico
        /// </summary>
        /// <param name="filePath">File MKV da leggere</param>
        /// <returns>Mappa selector -> TrackUID</returns>
        private Dictionary<string, string> ReadTrackUidsBySelector(string filePath)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ProcessResult processResult;
            JsonDocument document = null;
            JsonElement tracksElement;
            int videoIndex = 0;
            int audioIndex = 0;
            int subtitleIndex = 0;

            processResult = ProcessRunner.Run(this._mkvMergePath, new string[] { "-J", filePath });
            if (processResult.ExitCode != 0 || string.IsNullOrEmpty(processResult.Stdout.Trim()))
                return result;

            try
            {
                document = JsonDocument.Parse(processResult.Stdout);
                if (!document.RootElement.TryGetProperty("tracks", out tracksElement) || tracksElement.ValueKind != JsonValueKind.Array)
                    return result;

                foreach (JsonElement trackElement in tracksElement.EnumerateArray())
                {
                    string type = GetJsonString(trackElement, "type");
                    string selector = "";

                    if (type == "video")
                    {
                        videoIndex++;
                        selector = "track:v" + videoIndex.ToString(CultureInfo.InvariantCulture);
                    }
                    else if (type == "audio")
                    {
                        audioIndex++;
                        selector = "track:a" + audioIndex.ToString(CultureInfo.InvariantCulture);
                    }
                    else if (type == "subtitles")
                    {
                        subtitleIndex++;
                        selector = "track:s" + subtitleIndex.ToString(CultureInfo.InvariantCulture);
                    }

                    if (!string.IsNullOrEmpty(selector))
                        result[selector] = GetJsonPropertyString(trackElement, "properties", "uid");
                }
            }
            finally
            {
                if (document != null)
                    document.Dispose();
            }

            return result;
        }

        /// <summary>
        /// Legge una proprietà stringa da un elemento JSON
        /// </summary>
        /// <param name="element">Elemento JSON</param>
        /// <param name="propertyName">Nome proprietà</param>
        /// <returns>Valore proprietà o stringa vuota</returns>
        private static string GetJsonString(JsonElement element, string propertyName)
        {
            JsonElement valueElement;
            if (element.TryGetProperty(propertyName, out valueElement))
                return valueElement.ToString();

            return "";
        }

        /// <summary>
        /// Legge una proprietà stringa da un oggetto JSON annidato
        /// </summary>
        /// <param name="element">Elemento JSON</param>
        /// <param name="objectName">Nome oggetto annidato</param>
        /// <param name="propertyName">Nome proprietà</param>
        /// <returns>Valore proprietà o stringa vuota</returns>
        private static string GetJsonPropertyString(JsonElement element, string objectName, string propertyName)
        {
            JsonElement objectElement;
            JsonElement valueElement;

            if (!element.TryGetProperty(objectName, out objectElement))
                return "";

            if (objectElement.ValueKind != JsonValueKind.Object)
                return "";

            if (objectElement.TryGetProperty(propertyName, out valueElement))
                return valueElement.ToString();

            return "";
        }

        /// <summary>
        /// Crea la cartella parent del file quando necessaria
        /// </summary>
        private static void EnsureParentFolder(string filePath)
        {
            string folder = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
        }

        /// <summary>
        /// Sostituisce il file originale mantenendo un backup temporaneo per rollback
        /// </summary>
        private static void ReplaceOriginal(string tempFile, string inputFile)
        {
            string backupFile = inputFile + ".remuxforge.bak";
            if (File.Exists(backupFile))
                File.Delete(backupFile);

            File.Move(inputFile, backupFile);
            try
            {
                File.Move(tempFile, inputFile);
                File.Delete(backupFile);
            }
            catch
            {
                if (File.Exists(inputFile))
                    File.Delete(inputFile);

                File.Move(backupFile, inputFile);
                throw;
            }
        }

        /// <summary>
        /// Elimina un file temporaneo ignorando errori di I/O
        /// </summary>
        private static void CleanupTemp(string tempFile)
        {
            if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch (IOException)
                {
                }
            }
        }

        /// <summary>
        /// Verifica se tra i selector rimossi esiste una traccia del tipo indicato
        /// </summary>
        private static bool HasRemovedKind(List<string> selectors, string prefix)
        {
            for (int i = 0; i < selectors.Count; i++)
            {
                if (selectors[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Scrive un file XML tags temporaneo con le modifiche richieste
        /// </summary>
        private string BuildTagsXmlFile(string filePath, List<MkvMetadataChange> tagChanges, Dictionary<string, string> trackUidMap)
        {
            XDocument document = this.LoadExistingTags(filePath);
            string tempFile = Path.Combine(Path.GetTempPath(), "remuxforge-tags-" + Guid.NewGuid().ToString("N") + ".xml");

            if (document.Root == null || document.Root.Name.LocalName != "Tags")
                document = new XDocument(new XElement("Tags"));

            for (int i = 0; i < tagChanges.Count; i++)
            {
                this.ApplyTagChange(document, tagChanges[i], trackUidMap);
            }

            document.Save(tempFile);
            return tempFile;
        }

        /// <summary>
        /// Estrae i tag MKV esistenti con mkvextract
        /// </summary>
        private XDocument LoadExistingTags(string filePath)
        {
            string tempFile = Path.Combine(Path.GetTempPath(), "remuxforge-existing-tags-" + Guid.NewGuid().ToString("N") + ".xml");
            ProcessResult result;
            try
            {
                result = ProcessRunner.Run(this._mkvExtractPath, new string[] { filePath, "tags", tempFile });
                if (result.ExitCode != 0)
                    throw new InvalidOperationException(AppText.F("metadata.execution.mkvextractTagsFailed", LastErrorLine(!string.IsNullOrEmpty(result.Stderr) ? result.Stderr : result.Stdout)));

                if (File.Exists(tempFile) && new FileInfo(tempFile).Length > 0)
                    return XDocument.Load(tempFile);
            }
            finally
            {
                CleanupTemp(tempFile);
            }

            return new XDocument(new XElement("Tags"));
        }

        /// <summary>
        /// Copia nel modello i tag gestiti presenti nell'XML mkvextract
        /// </summary>
        private static void PopulateExistingTagsOnFileInfo(MkvMetadataFileInfo fileInfo, XDocument document)
        {
            if (fileInfo == null)
                return;

            // Riparte da uno snapshot pulito per evitare tag residui di scansioni precedenti
            fileInfo.Tags.Clear();
            for (int i = 0; i < fileInfo.Tracks.Count; i++)
            {
                fileInfo.Tracks[i].Tags.Clear();
            }

            if (document == null || document.Root == null)
                return;

            foreach (XElement tag in document.Root.Elements("Tag"))
            {
                // I tag senza TrackUID sono considerati tag container
                if (IsGlobalTag(tag))
                {
                    ReadManagedSimpleTags(tag, fileInfo.Tags);
                    continue;
                }

                string trackUid = GetTrackUid(tag);
                MkvMetadataTrackInfo track = FindTrackByUid(fileInfo, trackUid);
                if (track != null)
                    ReadManagedSimpleTags(tag, track.Tags);
            }
        }

        /// <summary>
        /// Trova la traccia del modello tramite TrackUID MKV
        /// </summary>
        /// <param name="fileInfo">File metadata da interrogare</param>
        /// <param name="trackUid">TrackUID MKV cercato</param>
        /// <returns>Traccia corrispondente o null</returns>
        private static MkvMetadataTrackInfo FindTrackByUid(MkvMetadataFileInfo fileInfo, string trackUid)
        {
            string normalizedTrackUid = trackUid != null ? trackUid.Trim() : "";
            if (string.IsNullOrEmpty(normalizedTrackUid))
                return null;

            for (int i = 0; i < fileInfo.Tracks.Count; i++)
            {
                if (string.Equals(fileInfo.Tracks[i].TrackUniqueId, normalizedTrackUid, StringComparison.OrdinalIgnoreCase))
                    return fileInfo.Tracks[i];
            }

            return null;
        }

        /// <summary>
        /// Legge i SimpleTag gestiti da un nodo Tag
        /// </summary>
        private static void ReadManagedSimpleTags(XElement tag, Dictionary<string, string> target)
        {
            foreach (XElement simple in tag.Elements("Simple"))
            {
                XElement nameElement = simple.Element("Name");
                XElement valueElement = simple.Element("String");
                if (nameElement == null || valueElement == null)
                    continue;

                string name = NormalizeTagName(nameElement.Value);
                if (MetadataTagRegistry.IsAllowed(name))
                    target[name] = valueElement.Value != null ? valueElement.Value : "";
            }
        }

        /// <summary>
        /// Applica una modifica tag all'XML MKV
        /// </summary>
        private void ApplyTagChange(XDocument document, MkvMetadataChange change, Dictionary<string, string> trackUidMap)
        {
            XElement tag = this.FindOrCreateTargetTag(document, change, trackUidMap);

            if (change.OperationType == MkvMetadataOperationType.SetTagField)
            {
                MetadataTagDefinition tagDefinition;
                string tagValue;
                string errorMessage;

                if (!MetadataTagRegistry.TryGet(change.FieldKey, out tagDefinition))
                    throw new InvalidOperationException(AppText.F("metadata.validation.tagNotWritable", change.FieldKey));

                if (!MetadataTagRegistry.ValidateWritableValue(change.FieldKey, change.Scope, change.AfterValue, tagDefinition.IsClearable, out tagValue, out errorMessage))
                    throw new InvalidOperationException(errorMessage);

                RemoveSimpleTag(tag, change.FieldKey);
                tag.Add(new XElement("Simple",
                    new XElement("Name", NormalizeTagName(change.FieldKey)),
                    new XElement("String", tagValue)));
            }
            else if (change.OperationType == MkvMetadataOperationType.ClearTagField)
            {
                RemoveSimpleTag(tag, change.FieldKey);
            }
            else if (change.OperationType == MkvMetadataOperationType.ClearTags)
            {
                RemoveManagedSimpleTags(tag);
            }
        }

        /// <summary>
        /// Trova o crea il nodo Tag container/traccia destinatario della modifica
        /// </summary>
        private XElement FindOrCreateTargetTag(XDocument document, MkvMetadataChange change, Dictionary<string, string> trackUidMap)
        {
            XElement root = document.Root;
            string trackUid = ResolveRemuxedTrackUid(change.TrackUniqueId, trackUidMap);

            if (root == null)
            {
                document.Add(new XElement("Tags"));
                root = document.Root;
            }

            // Riusa un nodo Tag esistente quando punta allo stesso target
            foreach (XElement tag in root.Elements("Tag"))
            {
                if (change.Scope == MkvMetadataTargetScope.Container && IsGlobalTag(tag))
                    return tag;

                if (change.Scope != MkvMetadataTargetScope.Container && !string.IsNullOrEmpty(trackUid) && string.Equals(GetTrackUid(tag), trackUid, StringComparison.OrdinalIgnoreCase))
                    return tag;
            }

            // Crea un nuovo target globale o associato alla TrackUID della traccia
            XElement targets = new XElement("Targets");
            XElement result = new XElement("Tag", targets);
            if (change.Scope != MkvMetadataTargetScope.Container)
            {
                if (string.IsNullOrEmpty(trackUid))
                    throw new InvalidOperationException(AppText.F("metadata.execution.missingTrackUidForTag", change.TrackSelector));

                targets.Add(new XElement("TrackUID", trackUid));
            }

            root.Add(result);
            return result;
        }

        /// <summary>
        /// Determina se un nodo Tag è globale/container
        /// </summary>
        private static bool IsGlobalTag(XElement tag)
        {
            XElement targets = tag.Element("Targets");
            if (targets == null)
                return true;

            if (targets.Element("TrackUID") != null)
                return false;

            if (targets.Element("EditionUID") != null)
                return false;

            if (targets.Element("ChapterUID") != null)
                return false;

            if (targets.Element("AttachmentUID") != null)
                return false;

            return true;
        }

        /// <summary>
        /// Legge TrackUID da un nodo Tag
        /// </summary>
        private static string GetTrackUid(XElement tag)
        {
            XElement targets = tag.Element("Targets");
            XElement uid = targets != null ? targets.Element("TrackUID") : null;
            return uid != null ? uid.Value.Trim() : "";
        }

        /// <summary>
        /// Rimuove un SimpleTag con il nome indicato
        /// </summary>
        private static void RemoveSimpleTag(XElement tag, string tagName)
        {
            string normalized = NormalizeTagName(tagName);
            List<XElement> remove = new List<XElement>();

            foreach (XElement simple in tag.Elements("Simple"))
            {
                XElement name = simple.Element("Name");
                if (name != null && string.Equals(name.Value.Trim(), normalized, StringComparison.OrdinalIgnoreCase))
                    remove.Add(simple);
            }

            for (int i = 0; i < remove.Count; i++)
            {
                remove[i].Remove();
            }
        }

        /// <summary>
        /// Rimuove tutti i SimpleTag gestiti da RemuxForge
        /// </summary>
        private static void RemoveManagedSimpleTags(XElement tag)
        {
            List<string> managed = MetadataTagRegistry.GetEditableTagNames();
            List<XElement> remove = new List<XElement>();

            // Non rimuove tag liberi: cancella solo tag presenti nella registry editabile
            foreach (XElement simple in tag.Elements("Simple"))
            {
                XElement name = simple.Element("Name");
                if (name == null)
                    continue;

                for (int i = 0; i < managed.Count; i++)
                {
                    if (string.Equals(name.Value.Trim(), managed[i], StringComparison.OrdinalIgnoreCase))
                    {
                        remove.Add(simple);
                        break;
                    }
                }
            }

            for (int i = 0; i < remove.Count; i++)
            {
                remove[i].Remove();
            }
        }

        /// <summary>
        /// Normalizza il nome tag al formato MKV usato internamente
        /// </summary>
        private static string NormalizeTagName(string tagName)
        {
            return tagName != null ? tagName.Trim().ToUpperInvariant() : "";
        }

        /// <summary>
        /// Formatta comando e argomenti per anteprima/debug
        /// </summary>
        private static string FormatCommand(string executable, List<string> args)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(executable);
            for (int i = 0; i < args.Count; i++)
            {
                sb.Append(" ");
                if (args[i].IndexOf(' ') >= 0)
                {
                    sb.Append("\"");
                    sb.Append(args[i]);
                    sb.Append("\"");
                }
                else
                {
                    sb.Append(args[i]);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Estrae l'ultima riga significativa da stdout/stderr
        /// </summary>
        private static string LastErrorLine(string text)
        {
            string result = text != null ? text.Trim() : "";
            if (string.IsNullOrEmpty(result))
                return "";

            string[] lines = result.Replace("\r", "").Split('\n');
            return lines.Length > 0 ? lines[lines.Length - 1].Trim() : result;
        }

        #endregion
    }
}
