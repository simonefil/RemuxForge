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
        /// Lingua dei messaggi di mkvextract: fissa, perché gli avvisi si attribuiscono alla traccia che nominano
        /// </summary>
        private const string MKVEXTRACT_UI_LANGUAGE = "en_US";

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

        /// <summary>
        /// Traccia nominata da un messaggio di mkvextract in inglese: Track 2, track ID 2, track number 2
        /// </summary>
        private static readonly Regex s_mkvExtractTrack = new Regex(@"\btrack (?:ID |number )?(\d+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
        /// <param name="cancellationToken">Annullamento che interrompe lo strumento</param>
        /// <returns>File con le sue tracce</returns>
        /// <exception cref="InvalidOperationException">Il file non esiste o lo strumento non lo legge; il messaggio dice perché</exception>
        /// <exception cref="OperationCanceledException">La lettura è stata annullata</exception>
        public TrackExtractionSource ReadTracks(string filePath, CancellationToken cancellationToken)
        {
            List<ExtractableTrack> tracks = new List<ExtractableTrack>();
            string fullPath = Path.GetFullPath(filePath);

            if (!File.Exists(fullPath))
                throw new InvalidOperationException(AppText.F("toolbox.trackExtraction.inputMissing", Path.GetFileName(fullPath)));

            if (MediaContainerFormats.IsMatroska(fullPath))
            {
                string error = "";
                MkvFileInfo info = this._mkvTools.GetFileInfoIsolated(fullPath, 0, cancellationToken, message => error = message);

                // mkvmerge termina senza errori anche su un file che non riconosce: nessuna traccia vuol dire file illeggibile
                if (info == null || info.Tracks.Count == 0)
                    throw new InvalidOperationException(error.Length > 0 ? error.Replace(fullPath, Path.GetFileName(fullPath)) : AppText.F("toolbox.trackExtraction.unreadable", Path.GetFileName(fullPath)));

                foreach (TrackInfo track in info.Tracks)
                {
                    TrackExtractionKind kind = track.Type switch
                    {
                        "video" => TrackExtractionKind.Video,
                        "audio" => TrackExtractionKind.Audio,
                        "subtitles" => TrackExtractionKind.Subtitles,
                        _ => TrackExtractionKind.Other
                    };
                    string language = LanguageValidator.NormalizeToIso6392(track.Language);

                    tracks.Add(new ExtractableTrack(track.Id, kind, track.Type ?? "", track.Codec ?? "", track.CodecId ?? "", TrackExtractionFormat.FromMatroskaCodecId(track.CodecId),
                        language.Length > 0 ? language : UNDEFINED_LANGUAGE, track.Name ?? "", track.DefaultTrack, track.ForcedTrack));
                }

                return new TrackExtractionSource(fullPath, true, info.ContainerDurationNs / 1e9, tracks);
            }

            FfprobeMediaInfo media;
            try
            {
                media = this._ffprobe.Read(fullPath, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                // Come per gli errori dell'estrazione: senza il contesto di ffmpeg e con il nome del file al posto del percorso
                throw new InvalidOperationException(s_ffmpegContext.Replace(ex.Message, "").Replace(fullPath, Path.GetFileName(fullPath)));
            }

            foreach (FfprobeStream stream in media.Streams)
            {
                TrackExtractionKind kind;
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

                language = LanguageValidator.NormalizeToIso6392(stream.Language);
                codec = stream.Profile.Length > 0 && stream.Profile != "unknown" ? stream.CodecName + " (" + stream.Profile + ")" : stream.CodecName;
                tracks.Add(new ExtractableTrack(stream.Index, kind, stream.CodecType, codec, stream.CodecName, TrackExtractionFormat.FromFfmpegCodec(stream.CodecName),
                    language.Length > 0 ? language : UNDEFINED_LANGUAGE, stream.Title, stream.IsDefault, stream.IsForced));
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
        /// che non produce dati non si scrive. Lo stop interrompe lo strumento senza lasciare file, e un nome non usato torna al lotto.
        /// Un avviso va alla traccia che nomina; quello che non ne nomina una riguarda il file e non cambia l'esito delle tracce
        /// </summary>
        /// <param name="source">File letto con ReadTracks</param>
        /// <param name="tracks">Tracce estraibili da estrarre, del file indicato</param>
        /// <param name="outputFolder">Cartella di uscita, vuota per scrivere accanto al video</param>
        /// <param name="allocator">Allocatore dei percorsi del lotto</param>
        /// <param name="onProgress">Avanzamento del file in percentuale, che non torna indietro quando le tracce si riprovano</param>
        /// <param name="fileWarnings">Riceve gli avvisi dello strumento che non nominano una traccia</param>
        /// <returns>Esito di ogni traccia, nello stesso ordine</returns>
        public List<TrackExtractionResult> Extract(TrackExtractionSource source, IReadOnlyList<ExtractableTrack> tracks, string outputFolder, OutputPathAllocator allocator, Action<int> onProgress, List<string> fileWarnings)
        {
            List<TrackExtractionResult> results = new List<TrackExtractionResult>();
            List<StagedOutputFile> staged = new List<StagedOutputFile>();
            string failure = string.IsNullOrEmpty(outputFolder) ? "" : OutputPathAllocator.ValidateFolder(outputFolder);
            int reported = 0;
            Action<int> report = percent =>
            {
                if (percent > reported)
                {
                    reported = percent;
                    onProgress?.Invoke(percent);
                }
            };
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
                            result.ReservedPath = allocator.Reserve(GetDesiredPath(source.Path, outputFolder, result.Track), true);
                            staged.Add(new StagedOutputFile(result.ReservedPath, false));
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
                        result.Status = ToolboxItemStatus.Failed;
                        result.Message = failure;
                    }
                    return results;
                }

                all = this.Run(source, staged, results, 0, results.Count, report);
                if (all.Succeeded)
                    fileWarnings.AddRange(all.Warnings);
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
                        result.Warnings.Clear();
                        run = this.Run(source, staged, results, i, 1, percent => report((index * 100 + percent) / results.Count));
                    }

                    if (ProcessRunner.IsStopRequested())
                    {
                        result.Status = ToolboxItemStatus.Stopped;
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
                        result.Status = ToolboxItemStatus.Failed;
                        result.Details = string.Join(Environment.NewLine, run.Errors);
                        result.Message = string.Join(" ", run.Errors);
                    }
                    else if (size == 0 || (companion.Length > 0 && (!File.Exists(companion) || new FileInfo(companion).Length == 0)))
                    {
                        result.Status = ToolboxItemStatus.Failed;
                        result.Message = AppText.T("toolbox.trackExtraction.emptyTrack");
                    }
                    else
                    {
                        // Un nome finale che non si riesce a dare ferma questa traccia soltanto
                        try
                        {
                            staged[i].Commit();
                            result.Status = result.Warnings.Count > 0 ? ToolboxItemStatus.DoneWithWarnings : ToolboxItemStatus.Done;
                            result.OutputPath = result.ReservedPath;
                        }
                        catch (IOException ex)
                        {
                            result.Status = ToolboxItemStatus.Failed;
                            result.Message = ex.Message;
                        }
                    }
                }
            }
            finally
            {
                // Un temporaneo che non si cancella resta sul disco: la riga della sua traccia lo dice, con il percorso
                for (int i = 0; i < staged.Count; i++)
                {
                    List<string> leftovers = staged[i].Discard();
                    if (leftovers.Count > 0)
                    {
                        results[i].Message = (results[i].Message + " " + string.Join(" ", leftovers)).Trim();
                        // Il dettaglio, quando c'è, prende il posto del messaggio nel log e nel suggerimento della riga
                        if (results[i].Details.Length > 0)
                            results[i].Details += Environment.NewLine + string.Join(Environment.NewLine, leftovers);
                    }
                }
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
        /// avvisi ed errori. Un avviso va alla traccia del gruppo che nomina, o all'unica traccia del gruppo; gli altri restano del
        /// file. Nei messaggi i percorsi completi del video e dei temporanei diventano i nomi dei file
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
                arguments.Add("--ui-language");
                arguments.Add(MKVEXTRACT_UI_LANGUAGE);
                arguments.Add(source.Path);
                arguments.Add("tracks");
                for (int i = first; i < first + count; i++)
                    arguments.Add(results[i].Track.Number.ToString(CultureInfo.InvariantCulture) + ":" + staged[i].TempPath);

                process = MkvToolNixGuiMode.Run(this._mkvExtractPath, arguments, onProgress, warning =>
                {
                    int owner = count == 1 ? first : -1;

                    // Con più tracce, un avviso è di una traccia solo se nomina lei e nessun'altra; altrimenti resta del file
                    if (count > 1)
                    {
                        foreach (Match match in s_mkvExtractTrack.Matches(warning))
                        {
                            int named = results.FindIndex(first, count, r => r.Track.Number.ToString(CultureInfo.InvariantCulture) == match.Groups[1].Value);
                            owner = named >= 0 && (owner == -1 || owner == named) ? named : -2;
                            if (owner == -2)
                                break;
                        }
                    }
                    if (owner >= 0)
                        results[owner].Warnings.Add(warning);
                    else
                        run.Warnings.Add(warning);
                }, run.Errors);
                run.Succeeded = process.ExitCode == 0 || process.ExitCode == MkvToolNixGuiMode.WARNING_EXIT_CODE;
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
            /// Avvisi dello strumento che non nominano una traccia del gruppo
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
    /// Esito dell'estrazione di una traccia
    /// </summary>
    public sealed class TrackExtractionResult : ToolboxItemResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="track">Traccia estratta</param>
        public TrackExtractionResult(ExtractableTrack track)
        {
            this.Track = track;
            this.ReservedPath = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Traccia estratta
        /// </summary>
        public ExtractableTrack Track { get; }

        /// <summary>
        /// Nome prenotato per la traccia, vuoto se la prenotazione non è avvenuta
        /// </summary>
        public string ReservedPath { get; set; }

        #endregion
    }
}
