using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Subtitles;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Clipping temporale necessario a D08a. Nessuna conversione di formato o scarto implicito.
    /// </summary>
    public static class MkvSplitSubtitleService
    {
        #region Variabili statiche

        /// <summary>
        /// Tag ASS con effetti temporizzati (karaoke, \t, \move, \fad, \fade) che un taglio altererebbe
        /// </summary>
        private static readonly Regex s_timedAss = new Regex(@"\\(?:k[fo]?\d|K\d|t\s*\(|move\s*\(|fad\s*\(|fade\s*\()", RegexOptions.IgnoreCase);

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Vero se la traccia di sottotitoli si può ritagliare
        /// </summary>
        /// <param name="track">Traccia da verificare</param>
        /// <returns>Vero se il codec è supportato</returns>
        public static bool IsSupported(TrackInfo track)
        {
            return Extension(track) != null;
        }

        /// <summary>
        /// Estrae la traccia dal sorgente e ne legge gli eventi nel tempo del sorgente
        /// </summary>
        /// <param name="source">File sorgente</param>
        /// <param name="track">Traccia da estrarre</param>
        /// <param name="directory">Cartella temporanea per l'estrazione</param>
        /// <param name="videoEndSeconds">Fine del video, usata per chiudere l'ultimo display-set PGS</param>
        /// <returns>Traccia letta</returns>
        public static MkvSplitSubtitleTrack ReadSource(string source, TrackInfo track, string directory, double videoEndSeconds = double.NaN)
        {
            string extension = Extension(track);
            if (extension == null)
                throw new InvalidOperationException(AppText.F("split.montage.unsupportedSubtitle", track.Id, track.Codec));
            string path = Path.Combine(directory, "source-sub-" + track.Id + extension);
            MkvSplitExternalTools.Instance.ExtractRawTrack(source, track.Id, path);
            if (extension == ".sup")
                return MkvSplitPgsSubtitleService.Read(path, track, videoEndSeconds);
            if (extension == ".idx")
                return MkvSplitVobSubSubtitleService.Read(path, track);
            string content = File.ReadAllText(path);
            MkvSplitSubtitleTrack result = new MkvSplitSubtitleTrack
            {
                Track = track,
                Extension = extension,
                SourceContent = content
            };
            if (extension == ".srt")
            {
                foreach (SrtSubtitleCue cue in SrtSubtitleTimelineRewriter.ParseCues(content, true, true))
                {
                    result.Events.Add(new MkvSplitSubtitleEvent
                    {
                        StartSeconds = cue.StartMs / 1000.0,
                        EndSeconds = cue.EndMs / 1000.0,
                        Text = cue.Text
                    });
                }
            }
            else
            {
                foreach (AssSubtitleDialogue dialogue in AssSubtitleDocument.Parse(content).ReadDialogues())
                {
                    result.Events.Add(new MkvSplitSubtitleEvent
                    {
                        SourceLineIndex = dialogue.LineIndex,
                        StartSeconds = dialogue.StartMs / 1000.0,
                        EndSeconds = dialogue.EndMs / 1000.0,
                        Fields = dialogue.Fields,
                        StartColumn = dialogue.StartColumn,
                        EndColumn = dialogue.EndColumn,
                        HasTimedEffects = s_timedAss.IsMatch(dialogue.Fields.Last())
                            || (dialogue.EffectColumn >= 0 && !string.IsNullOrWhiteSpace(dialogue.Fields[dialogue.EffectColumn]))
                    });
                }
            }
            if (result.Events.Any(item => !double.IsFinite(item.StartSeconds) || !double.IsFinite(item.EndSeconds)
                || item.StartSeconds < 0 || item.EndSeconds <= item.StartSeconds))
                throw Invalid(track);
            return result;
        }

        /// <summary>
        /// Scrive la traccia ritagliata e riposizionata sulle clip di un output
        /// </summary>
        /// <param name="track">Traccia letta dal sorgente</param>
        /// <param name="output">Proiezione dell'output</param>
        /// <param name="path">File di destinazione</param>
        public static void WriteOutput(MkvSplitSubtitleTrack track, MkvSplitOutputProjection output, string path)
        {
            if (track.Extension == ".sup")
            {
                MkvSplitPgsSubtitleService.Write(track, output, path);
                return;
            }
            if (track.Extension == ".idx")
            {
                MkvSplitVobSubSubtitleService.Write(track, output, path);
                return;
            }
            bool ass = track.Extension != ".srt";
            List<SrtSubtitleCue> cues = new List<SrtSubtitleCue>();
            Dictionary<int, List<string>> dialogues = new Dictionary<int, List<string>>();
            if (ass)
            {
                foreach (MkvSplitSubtitleEvent item in track.Events)
                    dialogues[item.SourceLineIndex] = new List<string>();
            }
            foreach (MkvSplitClipProjection clip in output.Clips)
            {
                foreach (MkvSplitSubtitleEvent subtitle in track.Events)
                {
                    double start = Math.Max(subtitle.StartSeconds, clip.SourceStartSeconds);
                    double end = Math.Min(subtitle.EndSeconds, clip.SourceEndSeconds);
                    if (end <= start)
                        continue;
                    if (subtitle.HasTimedEffects && (start > subtitle.StartSeconds || end < subtitle.EndSeconds))
                        throw new InvalidOperationException(AppText.F("split.montage.subtitleTimedBoundary", track.Track.Id,
                            clip.SourceStartSeconds, clip.SourceEndSeconds));
                    start += clip.ResultStartSeconds - clip.SourceStartSeconds;
                    end += clip.ResultStartSeconds - clip.SourceStartSeconds;
                    // L'intersezione nulla dopo quantizzazione non deve creare un evento vuoto.
                    long startMs = (long)Math.Round(start * (ass ? 100 : 1000), MidpointRounding.AwayFromZero) * (ass ? 10 : 1);
                    long endMs = (long)Math.Round(end * (ass ? 100 : 1000), MidpointRounding.AwayFromZero) * (ass ? 10 : 1);
                    if (endMs <= startMs)
                        continue;
                    if (ass)
                    {
                        string[] fields = (string[])subtitle.Fields.Clone();
                        fields[subtitle.StartColumn] = AssSubtitleTimelineRewriter.FormatTimestamp(startMs);
                        fields[subtitle.EndColumn] = AssSubtitleTimelineRewriter.FormatTimestamp(endMs);
                        dialogues[subtitle.SourceLineIndex].Add("Dialogue: " + string.Join(",", fields));
                    }
                    else
                    {
                        cues.Add(new SrtSubtitleCue(startMs, endMs, subtitle.Text));
                    }
                }
            }
            string content;
            if (ass)
            {
                AssSubtitleDocument document = AssSubtitleDocument.Parse(track.SourceContent);
                document.ReplaceDialogueLines(dialogues);
                content = document.Serialize();
            }
            else
            {
                content = SrtSubtitleTimelineRewriter.SerializeCues(cues);
            }
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Estensione del file estratto per il codec della traccia
        /// </summary>
        /// <param name="track">Traccia di sottotitoli</param>
        /// <returns>Estensione con il punto, null se il codec non è supportato</returns>
        private static string Extension(TrackInfo track)
        {
            string codec = track.Codec ?? "";
            if (codec.Equals("SubRip/SRT", StringComparison.OrdinalIgnoreCase) || codec == "S_TEXT/UTF8" || codec == "S_TEXT/ASCII")
                return ".srt";
            if (codec.Equals("SubStationAlpha", StringComparison.OrdinalIgnoreCase) || codec.Equals("ASS", StringComparison.OrdinalIgnoreCase)
                || codec == "S_TEXT/ASS" || codec == "S_ASS")
                return ".ass";
            if (codec.Equals("SSA", StringComparison.OrdinalIgnoreCase) || codec == "S_TEXT/SSA" || codec == "S_SSA")
                return ".ssa";
            if (codec.Equals("HDMV PGS", StringComparison.OrdinalIgnoreCase) || codec == "S_HDMV/PGS")
                return ".sup";
            if (codec.Equals("VobSub", StringComparison.OrdinalIgnoreCase) || codec == "S_VOBSUB")
                return ".idx";
            return null;
        }

        /// <summary>
        /// Eccezione per contenuto della traccia non valido
        /// </summary>
        /// <param name="track">Traccia di sottotitoli</param>
        /// <returns>Eccezione con messaggio localizzato</returns>
        private static InvalidOperationException Invalid(TrackInfo track)
        {
            return new InvalidOperationException(AppText.F("split.montage.subtitleContent", track.Id, track.Codec));
        }

        #endregion
    }
}
