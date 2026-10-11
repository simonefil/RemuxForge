using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Subtitles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Splitting.Subtitles
{
    /// <summary>
    /// Clipping temporale dei sottotitoli testuali SRT e ASS/SSA necessario a D08a. Nessuna conversione di formato o scarto implicito
    /// </summary>
    internal sealed class MkvSplitTextSubtitleClipper : MkvSplitSubtitleClipperBase<MkvSplitTextSubtitleEvent>
    {
        #region Variabili di classe

        /// <summary>
        /// Tag ASS con effetti temporizzati (karaoke, \t, \move, \fad, \fade) che un taglio altererebbe
        /// </summary>
        private static readonly Regex s_timedAss = new Regex(@"\\(?:k[fo]?\d|K\d|t\s*\(|move\s*\(|fad\s*\(|fade\s*\()", RegexOptions.IgnoreCase);

        /// <summary>
        /// SubRip: tempi al millisecondo e cue con il solo testo
        /// </summary>
        private static readonly MkvSplitSubtitleFormat s_srt = new MkvSplitSubtitleFormat(".srt", new[] { "SubRip/SRT" }, new[] { "S_TEXT/UTF8", "S_TEXT/ASCII" });

        /// <summary>
        /// Formati gestiti: SubRip, poi ASS e SSA con tempi al centesimo e righe Dialogue riscritte nel documento sorgente
        /// </summary>
        private static readonly MkvSplitSubtitleFormat[] s_formats = new MkvSplitSubtitleFormat[]
        {
            s_srt,
            new MkvSplitSubtitleFormat(".ass", new[] { "SubStationAlpha", "ASS" }, new[] { "S_TEXT/ASS", "S_ASS" }),
            new MkvSplitSubtitleFormat(".ssa", new[] { "SSA" }, new[] { "S_TEXT/SSA", "S_SSA" })
        };

        #endregion

        #region Proprietà

        /// <summary>
        /// Formato testuale
        /// </summary>
        public override bool IsBitmap => false;

        /// <summary>
        /// SubRip, ASS e SSA
        /// </summary>
        protected override MkvSplitSubtitleFormat[] Formats => s_formats;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive il file SRT o ASS/SSA dell'output: ogni occorrenza diventa un cue, o una riga Dialogue al posto di quella sorgente
        /// </summary>
        /// <param name="track">Traccia letta dal sorgente</param>
        /// <param name="output">Proiezione dell'output</param>
        /// <param name="path">File di destinazione</param>
        public override void Write(MkvSplitSubtitleTrack<MkvSplitTextSubtitleEvent> track, MkvSplitOutputProjection output, string path)
        {
            bool ass = !s_srt.Matches(track.Track);
            List<SrtSubtitleCue> cues = new List<SrtSubtitleCue>();
            Dictionary<int, List<string>> dialogues = new Dictionary<int, List<string>>();
            if (ass)
            {
                foreach (MkvSplitTextSubtitleEvent item in track.Events)
                    dialogues[item.SourceLineIndex] = new List<string>();
            }
            foreach ((MkvSplitTextSubtitleEvent subtitle, double sourceStart, double sourceEnd, double shift) in Clip(track, output))
            {
                // L'intersezione nulla dopo quantizzazione non deve creare un evento vuoto.
                long startMs = (long)Math.Round((sourceStart + shift) * (ass ? 100 : 1000), MidpointRounding.AwayFromZero) * (ass ? 10 : 1);
                long endMs = (long)Math.Round((sourceEnd + shift) * (ass ? 100 : 1000), MidpointRounding.AwayFromZero) * (ass ? 10 : 1);
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

        #region Metodi protetti

        /// <summary>
        /// Legge cue SRT o righe Dialogue ASS/SSA; il contenuto resta nella traccia per riscriverlo
        /// </summary>
        /// <param name="path">File estratto</param>
        /// <param name="result">Traccia da riempire</param>
        /// <param name="videoEndSeconds">Non usata: ogni evento testuale ha la sua fine</param>
        protected override void ReadEvents(string path, MkvSplitSubtitleTrack<MkvSplitTextSubtitleEvent> result, double videoEndSeconds)
        {
            TrackInfo track = result.Track;
            string content = File.ReadAllText(path);
            result.SourceContent = content;
            if (s_srt.Matches(track))
            {
                foreach (SrtSubtitleCue cue in SrtSubtitleTimelineRewriter.ParseCues(content, true, true))
                {
                    result.Events.Add(new MkvSplitTextSubtitleEvent
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
                    result.Events.Add(new MkvSplitTextSubtitleEvent
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
                throw new InvalidOperationException(AppText.F("split.montage.subtitleContent", track.Id, track.Codec));
        }

        #endregion
    }
}
