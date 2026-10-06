using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Riscrive sottotitoli SRT applicando cut e insert della timeline
    /// </summary>
    internal class SrtSubtitleTimelineRewriter
    {
        private static readonly Regex s_completeTimestamp = new Regex(@"\A[0-9]+:[0-9]{2}:[0-9]{2},[0-9]{3}\z", RegexOptions.Compiled);

        #region Metodi pubblici

        /// <summary>
        /// Riscrive il contenuto SRT applicando le operazioni dell'edit map
        /// </summary>
        /// <param name="content">Contenuto SRT originale</param>
        /// <param name="editMap">Edit map da applicare</param>
        /// <returns>Contenuto SRT riscritto</returns>
        public string Rewrite(string content, EditMap editMap)
        {
            StringBuilder result = new StringBuilder();
            List<SubtitleCueInterval> intervals;
            int index = 1;
            foreach (SrtSubtitleCue cue in ParseCues(content))
            {
                intervals = SubtitleTimelineMapper.ApplyOperationsToCue(cue.StartMs, cue.EndMs, editMap);

                // Un cue può diventare più cue se un cut attraversa l'intervallo originale
                for (int c = 0; c < intervals.Count; c++)
                {
                    if (intervals[c].EndMs <= intervals[c].StartMs)
                    {
                        continue;
                    }

                    result.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    result.Append(FormatTimestamp(intervals[c].StartMs)).Append(" --> ").Append(FormatTimestamp(intervals[c].EndMs)).Append('\n');
                    if (cue.HasTextLines) result.Append(cue.Text).Append('\n');
                    result.Append('\n');
                    index++;
                }
            }

            return result.ToString();
        }

        /// <summary>Parsing codec condiviso; strict permette ai chiamanti di rifiutare uno scarto implicito.</summary>
        public static List<SrtSubtitleCue> ParseCues(string content, bool strict = false, bool acceptDotTimestamp = false)
        {
            List<SrtSubtitleCue> result = new List<SrtSubtitleCue>();
            string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
            if (strict)
            {
                string[] normalizedLines = normalized.Split('\n');
                for (int index = 0; index < normalizedLines.Length; index++)
                    if (string.IsNullOrWhiteSpace(normalizedLines[index])) normalizedLines[index] = "";
                normalized = string.Join("\n", normalizedLines);
            }
            foreach (string raw in normalized.Split(new string[] { "\n\n" }, StringSplitOptions.None))
            {
                string block = raw.Trim('\n');
                if (string.IsNullOrWhiteSpace(block)) continue;
                string[] lines = block.Split('\n');
                int timing = FindTimingLine(lines);
                string[] parts = timing < 0 ? Array.Empty<string>() : lines[timing].Split(new string[] { "-->" }, StringSplitOptions.None);
                if (parts.Length != 2 || !TryParseTimestamp(parts[0].Trim(), out long start, acceptDotTimestamp, strict) || !TryParseTimestamp(parts[1].Trim(), out long end, acceptDotTimestamp, strict))
                {
                    if (strict) throw new InvalidDataException("Invalid SRT cue");
                    continue;
                }
                result.Add(new SrtSubtitleCue(start, end, string.Join("\n", lines, timing + 1, lines.Length - timing - 1), lines.Length > timing + 1));
            }
            return result;
        }

        /// <summary>Serializza cue già mappati, senza applicare una EditMap.</summary>
        public static string SerializeCues(IEnumerable<SrtSubtitleCue> cues)
        {
            StringBuilder result = new StringBuilder();
            int index = 0;
            foreach (SrtSubtitleCue cue in cues)
            {
                result.Append(++index).Append('\n');
                result.Append(FormatTimestamp(cue.StartMs)).Append(" --> ").Append(FormatTimestamp(cue.EndMs)).Append('\n');
                if (cue.HasTextLines) result.Append(cue.Text).Append('\n');
                result.Append('\n');
            }
            return result.ToString();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Cerca la riga timing di un blocco SRT
        /// </summary>
        /// <param name="lines">Righe del blocco</param>
        /// <returns>Indice riga timing, -1 se assente</returns>
        public static int FindTimingLine(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf("-->", StringComparison.Ordinal) >= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Converte un timestamp SRT in millisecondi
        /// </summary>
        /// <param name="value">Timestamp SRT</param>
        /// <param name="ms">Millisecondi risultanti</param>
        /// <param name="acceptDot">Accetta il punto come separatore dei millisecondi</param>
        /// <param name="strict">Valida la forma completa, senza componenti extra o token successivi</param>
        /// <returns>True se il timestamp è valido</returns>
        public static bool TryParseTimestamp(string value, out long ms, bool acceptDot = false, bool strict = false)
        {
            if (acceptDot) value = value.Replace('.', ',');
            ms = 0;
            if (strict && !s_completeTimestamp.IsMatch(value)) return false;
            string[] parts = value.Split(new char[] { ':', ',' });
            int h;
            int m;
            int s;
            int milli;
            ms = 0;

            // Formato atteso: hh:mm:ss,mmm
            if (parts.Length < 4)
            {
                return false;
            }

            if (!int.TryParse(parts[0], out h) || !int.TryParse(parts[1], out m) || !int.TryParse(parts[2], out s) || !int.TryParse(parts[3], out milli))
            {
                return false;
            }

            ms = (((h * 60L) + m) * 60L + s) * 1000L + milli;
            return true;
        }

        /// <summary>
        /// Formatta millisecondi nel formato timestamp SRT
        /// </summary>
        /// <param name="ms">Millisecondi da formattare</param>
        /// <returns>Timestamp SRT</returns>
        public static string FormatTimestamp(long ms)
        {
            long h;
            long m;
            long s;
            long milli;
            // I timestamp sottotitolo non possono diventare negativi dopo i cut
            if (ms < 0)
            {
                ms = 0;
            }

            h = ms / 3600000L;
            ms %= 3600000L;
            m = ms / 60000L;
            ms %= 60000L;
            s = ms / 1000L;
            milli = ms % 1000L;
            return h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) + ":" + s.ToString("00", CultureInfo.InvariantCulture) + "," + milli.ToString("000", CultureInfo.InvariantCulture);
        }

        #endregion
    }
    internal sealed record SrtSubtitleCue(long StartMs, long EndMs, string Text, bool HasTextLines = true);
}
