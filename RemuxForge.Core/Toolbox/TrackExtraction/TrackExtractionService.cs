using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Media.Ffmpeg;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using RemuxForge.Core.Toolbox.Common;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemuxForge.Core.Toolbox.TrackExtraction
{
    /// <summary>
    /// Legge le tracce di un file ed estrae quelle scelte tali e quali: dai Matroska con mkvextract, dagli altri contenitori con
    /// ffmpeg. Ogni traccia si scrive in un temporaneo accanto al nome finale e prende quel nome solo se ha prodotto dati, così
    /// uno stop o un errore non lasciano file parziali o vuoti
    /// </summary>
    public sealed class TrackExtractionService
    {
        #region Costanti

        /// <summary>
        /// Exit code di mkvextract per estrazione riuscita con avvisi
        /// </summary>
        private const int MKVEXTRACT_WARNING_EXIT_CODE = 1;

        /// <summary>
        /// Prefisso delle righe di avanzamento di mkvextract --gui-mode
        /// </summary>
        private const string GUI_PROGRESS_PREFIX = "#GUI#progress ";

        /// <summary>
        /// Prefisso degli avvisi di mkvextract --gui-mode
        /// </summary>
        private const string GUI_WARNING_PREFIX = "#GUI#warning ";

        /// <summary>
        /// Prefisso degli errori di mkvextract --gui-mode
        /// </summary>
        private const string GUI_ERROR_PREFIX = "#GUI#error ";

        /// <summary>
        /// Prefisso della posizione corrente in microsecondi nelle righe di -progress di ffmpeg
        /// </summary>
        private const string FFMPEG_PROGRESS_PREFIX = "out_time_us=";

        /// <summary>
        /// Lingua del nome del file quando la traccia non ne indica una
        /// </summary>
        private const string UNDEFINED_LANGUAGE = "und";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Contesto con cui ffmpeg apre le righe di log, ad esempio [out#0/ac3 @ 0x7f8a…]
        /// </summary>
        private static readonly Regex s_ffmpegContext = new Regex(@"\[[^\]]* @ 0x[0-9a-fA-F]+\]\s*", RegexOptions.Compiled);

        /// <summary>
        /// BOM UTF-8 con cui mkvextract apre i sottotitoli di testo
        /// </summary>
        private static readonly byte[] s_utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso eseguibile mkvextract
        /// </summary>
        private readonly string _mkvExtractPath;

        /// <summary>
        /// Percorso eseguibile ffmpeg
        /// </summary>
        private readonly string _ffmpegPath;

        /// <summary>
        /// Lettore delle tracce dei Matroska
        /// </summary>
        private readonly MkvToolsService _mkvTools;

        /// <summary>
        /// Lettore degli stream degli altri contenitori
        /// </summary>
        private readonly FfprobeStreamReader _ffprobe;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="mkvMergePath">Percorso eseguibile mkvmerge, che legge le tracce dei Matroska</param>
        /// <param name="mkvExtractPath">Percorso eseguibile mkvextract</param>
        /// <param name="ffprobePath">Percorso eseguibile ffprobe, che legge gli stream degli altri contenitori</param>
        /// <param name="ffmpegPath">Percorso eseguibile ffmpeg</param>
        public TrackExtractionService(string mkvMergePath, string mkvExtractPath, string ffprobePath, string ffmpegPath)
        {
            this._mkvExtractPath = mkvExtractPath;
            this._ffmpegPath = ffmpegPath;
            this._mkvTools = new MkvToolsService(mkvMergePath);
            this._ffprobe = new FfprobeStreamReader(ffprobePath);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge le tracce di un file: per un Matroska con mkvmerge e la sua numerazione, per gli altri contenitori con ffprobe e
        /// la numerazione degli stream di ffmpeg. Capitoli, allegati e stream di dati non sono tracce e non compaiono
        /// </summary>
        /// <param name="filePath">File da leggere</param>
        /// <returns>File con le sue tracce</returns>
        /// <exception cref="InvalidOperationException">Il file non esiste o lo strumento non lo legge; il messaggio dice perché</exception>
        public TrackExtractionSource ReadTracks(string filePath)
        {
            List<ExtractableTrack> tracks = new List<ExtractableTrack>();
            string fullPath = Path.GetFullPath(filePath);

            if (!File.Exists(fullPath))
                throw new InvalidOperationException(AppText.F("toolbox.trackExtraction.inputMissing", Path.GetFileName(fullPath)));

            if (MediaContainerFormats.IsMatroska(fullPath))
            {
                string error = "";
                MkvFileInfo info = this._mkvTools.GetFileInfoIsolated(fullPath, 0, CancellationToken.None, message => error = message);

                // mkvmerge termina senza errori anche su un file che non riconosce: nessuna traccia vuol dire file illeggibile
                if (info == null || info.Tracks.Count == 0)
                    throw new InvalidOperationException(error.Length > 0 ? error.Replace(fullPath, Path.GetFileName(fullPath)) : AppText.F("toolbox.trackExtraction.unreadable", Path.GetFileName(fullPath)));

                foreach (TrackInfo track in info.Tracks)
                {
                    TrackExtractionKind kind = track.Type == "audio" ? TrackExtractionKind.Audio : track.Type == "subtitles" ? TrackExtractionKind.Subtitles : TrackExtractionKind.Video;
                    TrackExtractionFormat format = kind == TrackExtractionKind.Video ? null : TrackExtractionFormat.FromMatroskaCodecId(track.CodecId);
                    string language = LanguageValidator.NormalizeToIso6392(track.Language);

                    tracks.Add(new ExtractableTrack(track.Id, kind, track.Codec ?? "", format, language.Length > 0 ? language : UNDEFINED_LANGUAGE, track.Name ?? "",
                        track.DefaultTrack, track.ForcedTrack,
                        kind == TrackExtractionKind.Video ? AppText.T("toolbox.trackExtraction.videoNotExtracted") :
                        format == null ? AppText.F("toolbox.trackExtraction.formatNotExtracted", track.CodecId.Length > 0 ? track.CodecId : track.Codec) : ""));
                }

                return new TrackExtractionSource(fullPath, true, info.ContainerDurationNs / 1e9, tracks);
            }

            FfprobeMediaInfo media;
            try
            {
                media = this._ffprobe.Read(fullPath);
            }
            catch (InvalidOperationException ex)
            {
                // Come per gli errori dell'estrazione: senza il contesto di ffmpeg e con il nome del file al posto del percorso
                throw new InvalidOperationException(s_ffmpegContext.Replace(ex.Message, "").Replace(fullPath, Path.GetFileName(fullPath)));
            }

            foreach (FfprobeStream stream in media.Streams)
            {
                TrackExtractionKind kind;
                TrackExtractionFormat format;
                string language;
                string codec;

                // Dati, capitoli e allegati non sono tracce
                if (stream.CodecType == "audio")
                    kind = TrackExtractionKind.Audio;
                else if (stream.CodecType == "subtitle")
                    kind = TrackExtractionKind.Subtitles;
                else if (stream.CodecType == "video")
                    kind = TrackExtractionKind.Video;
                else
                    continue;

                format = kind == TrackExtractionKind.Video ? null : TrackExtractionFormat.FromFfmpegCodec(stream.CodecName);
                language = LanguageValidator.NormalizeToIso6392(stream.Language);
                codec = stream.Profile.Length > 0 && stream.Profile != "unknown" ? stream.CodecName + " (" + stream.Profile + ")" : stream.CodecName;
                tracks.Add(new ExtractableTrack(stream.Index, kind, codec, format, language.Length > 0 ? language : UNDEFINED_LANGUAGE, stream.Title,
                    stream.IsDefault, stream.IsForced,
                    kind == TrackExtractionKind.Video ? AppText.T("toolbox.trackExtraction.videoNotExtracted") :
                    format == null ? AppText.F("toolbox.trackExtraction.formatNotExtracted", stream.CodecName) : ""));
            }

            return new TrackExtractionSource(fullPath, false, media.DurationSeconds, tracks);
        }

        /// <summary>
        /// Percorso voluto per una traccia estratta: nome del video, lingua ed estensione del formato, accanto al video o nella
        /// cartella indicata. Il suffisso numerico di un nome occupato lo aggiunge l'allocatore del lotto
        /// </summary>
        /// <param name="inputPath">Video di partenza</param>
        /// <param name="outputFolder">Cartella di uscita, vuota per scrivere accanto al video</param>
        /// <param name="track">Traccia estraibile</param>
        /// <returns>Percorso voluto</returns>
        public static string GetDesiredPath(string inputPath, string outputFolder, ExtractableTrack track)
        {
            string folder = string.IsNullOrEmpty(outputFolder) ? Path.GetDirectoryName(Path.GetFullPath(inputPath)) : outputFolder;
            return Path.Combine(folder, Path.GetFileNameWithoutExtension(inputPath) + "." + track.Language + track.Format.Extension);
        }

        /// <summary>
        /// Estrae le tracce scelte di un file. Tutte le tracce passano in una sola chiamata allo strumento; se la chiamata
        /// fallisce, ogni traccia si riprova da sola, così una traccia che lo strumento non scrive non ferma le altre. Una traccia
        /// che non produce dati non si scrive. Lo stop interrompe lo strumento senza lasciare file, e un nome non usato torna al lotto
        /// </summary>
        /// <param name="source">File letto con ReadTracks</param>
        /// <param name="tracks">Tracce estraibili da estrarre, del file indicato</param>
        /// <param name="outputFolder">Cartella di uscita, vuota per scrivere accanto al video</param>
        /// <param name="allocator">Allocatore dei percorsi del lotto</param>
        /// <param name="onProgress">Avanzamento del file in percentuale</param>
        /// <returns>Esito di ogni traccia, nello stesso ordine</returns>
        public List<TrackExtractionResult> Extract(TrackExtractionSource source, IReadOnlyList<ExtractableTrack> tracks, string outputFolder, OutputPathAllocator allocator, Action<int> onProgress)
        {
            List<TrackExtractionResult> results = new List<TrackExtractionResult>();
            List<StagedOutputFile> staged = new List<StagedOutputFile>();
            string failure = string.IsNullOrEmpty(outputFolder) ? "" : OutputPathAllocator.ValidateFolder(outputFolder);
            ExtractionRun all;

            foreach (ExtractableTrack track in tracks)
                results.Add(new TrackExtractionResult(track));

            if (failure.Length == 0 && !File.Exists(source.Path))
                failure = AppText.F("toolbox.trackExtraction.inputMissing", Path.GetFileName(source.Path));

            try
            {
                // Una cartella non scrivibile si scopre qui, alla creazione del primo temporaneo, prima di lanciare lo strumento
                if (failure.Length == 0)
                {
                    try
                    {
                        foreach (TrackExtractionResult result in results)
                        {
                            result.ReservedPath = allocator.Reserve(GetDesiredPath(source.Path, outputFolder, result.Track));
                            staged.Add(new StagedOutputFile(result.ReservedPath));
                        }
                    }
                    catch (IOException ex)
                    {
                        failure = ex.Message;
                    }
                }

                if (failure.Length > 0)
                {
                    foreach (TrackExtractionResult result in results)
                    {
                        result.Status = TrackExtractionStatus.Failed;
                        result.Message = failure;
                    }
                    return results;
                }

                all = this.Run(source, staged, results, 0, results.Count, percent => onProgress?.Invoke(percent));
                for (int i = 0; i < results.Count; i++)
                {
                    TrackExtractionResult result = results[i];
                    ExtractionRun run = all;
                    long size;
                    string companion;

                    // Lo stop si riconosce prima dell'esito: uno strumento ucciso non va riprovato traccia per traccia
                    if (!ProcessRunner.IsStopRequested() && !all.Succeeded && results.Count > 1)
                    {
                        int index = i;
                        run = this.Run(source, staged, results, i, 1, percent => onProgress?.Invoke((index * 100 + percent) / results.Count));
                    }

                    if (ProcessRunner.IsStopRequested())
                    {
                        result.Status = TrackExtractionStatus.Stopped;
                        result.Message = AppText.T("toolbox.trackExtraction.stopped");
                        continue;
                    }

                    // Vuoto anche un file con il solo BOM UTF-8: è ciò che mkvextract scrive per un SRT senza eventi
                    companion = FileHelper.GetCompanionPath(staged[i].TempPath);
                    size = File.Exists(staged[i].TempPath) ? new FileInfo(staged[i].TempPath).Length : 0;
                    if (size == s_utf8Bom.Length && File.ReadAllBytes(staged[i].TempPath).AsSpan().SequenceEqual(s_utf8Bom))
                        size = 0;
                    if (!run.Succeeded)
                    {
                        result.Status = TrackExtractionStatus.Failed;
                        result.Details = string.Join(Environment.NewLine, run.Errors);
                        result.Message = string.Join(" ", run.Errors);
                    }
                    else if (size == 0 || (companion.Length > 0 && (!File.Exists(companion) || new FileInfo(companion).Length == 0)))
                    {
                        result.Status = TrackExtractionStatus.Failed;
                        result.Message = AppText.T("toolbox.trackExtraction.emptyTrack");
                    }
                    else
                    {
                        // Un nome finale che non si riesce a dare ferma questa traccia soltanto
                        try
                        {
                            staged[i].Commit();
                            result.Warnings.AddRange(run.Warnings);
                            result.Status = run.Warnings.Count > 0 ? TrackExtractionStatus.ExtractedWithWarnings : TrackExtractionStatus.Extracted;
                            result.OutputPath = result.ReservedPath;
                        }
                        catch (IOException ex)
                        {
                            result.Status = TrackExtractionStatus.Failed;
                            result.Message = ex.Message;
                        }
                    }
                }
            }
            finally
            {
                foreach (StagedOutputFile output in staged)
                    output.Dispose();
                foreach (TrackExtractionResult result in results)
                {
                    if (result.ReservedPath.Length > 0 && result.OutputPath.Length == 0)
                        allocator.Release(result.ReservedPath);
                }
            }

            return results;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Lancia mkvextract o ffmpeg su un gruppo consecutivo di tracce, ognuna verso il suo temporaneo, e ne raccoglie avanzamento,
        /// avvisi ed errori. Nei messaggi i percorsi completi del video e dei temporanei diventano i nomi dei file
        /// </summary>
        /// <param name="source">File di partenza</param>
        /// <param name="staged">Temporanei di tutte le tracce, nell'ordine dei risultati</param>
        /// <param name="results">Esiti di tutte le tracce, con la traccia e il nome prenotato</param>
        /// <param name="first">Prima traccia del gruppo</param>
        /// <param name="count">Tracce del gruppo</param>
        /// <param name="onProgress">Avanzamento del gruppo in percentuale</param>
        /// <returns>Esito della chiamata</returns>
        private ExtractionRun Run(TrackExtractionSource source, List<StagedOutputFile> staged, List<TrackExtractionResult> results, int first, int count, Action<int> onProgress)
        {
            ExtractionRun run = new ExtractionRun();
            List<string> arguments = new List<string>();
            ProcessResult process;

            if (source.IsMatroska)
            {
                arguments.Add("--gui-mode");
                arguments.Add(source.Path);
                arguments.Add("tracks");
                for (int i = first; i < first + count; i++)
                    arguments.Add(results[i].Track.Number.ToString(CultureInfo.InvariantCulture) + ":" + staged[i].TempPath);

                process = ProcessRunner.RunWithStdoutLines(this._mkvExtractPath, arguments, line =>
                {
                    int percent;
                    if (line.StartsWith(GUI_PROGRESS_PREFIX, StringComparison.Ordinal) &&
                        int.TryParse(line.Substring(GUI_PROGRESS_PREFIX.Length).TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out percent))
                        onProgress(percent);
                    else if (line.StartsWith(GUI_WARNING_PREFIX, StringComparison.Ordinal))
                        run.Warnings.Add(line.Substring(GUI_WARNING_PREFIX.Length).Trim());
                    else if (line.StartsWith(GUI_ERROR_PREFIX, StringComparison.Ordinal))
                        run.Errors.Add(line.Substring(GUI_ERROR_PREFIX.Length).Trim());
                });
                run.Succeeded = process.ExitCode == 0 || process.ExitCode == MKVEXTRACT_WARNING_EXIT_CODE;
            }
            else
            {
                // Le uscite sono temporanei vuoti già creati: -y li riempie; il muxer è esplicito perché l'estensione del temporaneo non basta a tutti
                arguments.AddRange(new string[] { "-nostdin", "-hide_banner", "-v", "error", "-nostats", "-progress", "pipe:1", "-y", "-i", source.Path });
                for (int i = first; i < first + count; i++)
                {
                    TrackExtractionFormat format = results[i].Track.Format;
                    arguments.AddRange(new string[] { "-map", "0:" + results[i].Track.Number.ToString(CultureInfo.InvariantCulture), "-map_metadata", "-1", "-map_chapters", "-1" });
                    arguments.AddRange(format.ConvertsToSrt ? new string[] { "-c:s", "srt" } : new string[] { "-c", "copy" });
                    arguments.AddRange(new string[] { "-f", format.Muxer });
                    arguments.AddRange(format.MuxerOptions);
                    arguments.Add(staged[i].TempPath);
                }

                process = ProcessRunner.RunWithStdoutLines(this._ffmpegPath, arguments, line =>
                {
                    long microseconds;
                    if (source.DurationSeconds > 0 && line.StartsWith(FFMPEG_PROGRESS_PREFIX, StringComparison.Ordinal) &&
                        long.TryParse(line.Substring(FFMPEG_PROGRESS_PREFIX.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out microseconds))
                        onProgress((int)Math.Clamp(microseconds / (source.DurationSeconds * 10000.0), 0, 100));
                });
                run.Succeeded = process.ExitCode == 0;
            }

            if (!run.Succeeded && run.Errors.Count == 0)
            {
                foreach (string line in process.Stderr.Split('\n'))
                {
                    if (line.Trim().Length > 0)
                        run.Errors.Add(line.Trim());
                }
                if (run.Errors.Count == 0)
                    run.Errors.Add(AppText.F("toolbox.trackExtraction.exitCode", Path.GetFileName(source.IsMatroska ? this._mkvExtractPath : this._ffmpegPath), process.ExitCode));
            }

            // ffmpeg apre ogni riga con il componente e il suo indirizzo in memoria ([ac3 @ 0x…]), che non dicono nulla a chi legge
            for (int e = 0; e < run.Errors.Count; e++)
            {
                string message = s_ffmpegContext.Replace(run.Errors[e], "").Replace(source.Path, Path.GetFileName(source.Path));
                for (int i = 0; i < staged.Count; i++)
                    message = message.Replace(staged[i].TempPath, Path.GetFileName(results[i].ReservedPath));
                run.Errors[e] = message;
            }

            return run;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Esito di una chiamata a mkvextract o ffmpeg
        /// </summary>
        private sealed class ExtractionRun
        {
            /// <summary>
            /// True se lo strumento è terminato senza errori
            /// </summary>
            public bool Succeeded;

            /// <summary>
            /// Avvisi dello strumento
            /// </summary>
            public readonly List<string> Warnings = new List<string>();

            /// <summary>
            /// Errori dello strumento, senza percorsi completi
            /// </summary>
            public readonly List<string> Errors = new List<string>();
        }

        #endregion
    }

    /// <summary>
    /// Stato di una traccia nell'estrazione
    /// </summary>
    public enum TrackExtractionStatus
    {
        /// <summary>Da estrarre</summary>
        Pending,

        /// <summary>Estrazione in corso</summary>
        Running,

        /// <summary>File scritto</summary>
        Extracted,

        /// <summary>File scritto, con avvisi dello strumento</summary>
        ExtractedWithWarnings,

        /// <summary>Estrazione non riuscita</summary>
        Failed,

        /// <summary>Estrazione interrotta dallo stop</summary>
        Stopped
    }

    /// <summary>
    /// Esito dell'estrazione di una traccia
    /// </summary>
    public sealed class TrackExtractionResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="track">Traccia estratta</param>
        public TrackExtractionResult(ExtractableTrack track)
        {
            this.Track = track;
            this.Status = TrackExtractionStatus.Pending;
            this.ReservedPath = "";
            this.OutputPath = "";
            this.Message = "";
            this.Details = "";
            this.Warnings = new List<string>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Traccia estratta
        /// </summary>
        public ExtractableTrack Track { get; }

        /// <summary>
        /// Stato finale della traccia
        /// </summary>
        public TrackExtractionStatus Status { get; set; }

        /// <summary>
        /// Nome prenotato per la traccia, vuoto se la prenotazione non è avvenuta
        /// </summary>
        public string ReservedPath { get; set; }

        /// <summary>
        /// File scritto, vuoto se l'estrazione non è riuscita
        /// </summary>
        public string OutputPath { get; set; }

        /// <summary>
        /// Motivo dell'errore o dell'interruzione, senza percorsi completi
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Errori dello strumento riga per riga, vuoto se il motivo non viene dallo strumento
        /// </summary>
        public string Details { get; set; }

        /// <summary>
        /// Avvisi dello strumento, anche con estrazione riuscita
        /// </summary>
        public List<string> Warnings { get; }

        #endregion
    }
}
