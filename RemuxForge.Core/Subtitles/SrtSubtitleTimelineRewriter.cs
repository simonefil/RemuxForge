using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Riscrive sottotitoli SRT applicando cut e insert della timeline
    /// </summary>
    internal class SrtSubtitleTimelineRewriter
    {
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
            string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
            string[] blocks = normalized.Split(new string[] { "\n\n" }, StringSplitOptions.None);
            string block;
            string[] lines;
            string[] timingParts;
            List<SubtitleCueInterval> intervals;
            int index = 1;
            int timingLine;
            long startMs;
            long endMs;
            // Ogni blocco SRT contiene indice, riga timing e testo; i blocchi non parsabili vengono scartati
            for (int i = 0; i < blocks.Length; i++)
            {
                block = blocks[i].Trim('\n');
                if (block.Trim().Length == 0)
                {
                    continue;
                }

                lines = block.Split('\n');
                timingLine = this.FindTimingLine(lines);
                if (timingLine < 0)
                {
                    continue;
                }

                timingParts = lines[timingLine].Split(new string[] { "-->" }, StringSplitOptions.None);
                if (timingParts.Length != 2)
                {
                    continue;
                }

                if (!this.TryParseTimestamp(timingParts[0].Trim(), out startMs) || !this.TryParseTimestamp(timingParts[1].Trim(), out endMs))
                {
                    continue;
                }

                intervals = SubtitleTimelineMapper.ApplyOperationsToCue(startMs, endMs, editMap);

                // Un cue puo' diventare piu' cue se un cut attraversa l'intervallo originale
                for (int c = 0; c < intervals.Count; c++)
                {
                    if (intervals[c].EndMs <= intervals[c].StartMs)
                    {
                        continue;
                    }

                    result.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    result.Append(this.FormatTimestamp(intervals[c].StartMs)).Append(" --> ").Append(this.FormatTimestamp(intervals[c].EndMs)).Append('\n');
                    for (int l = timingLine + 1; l < lines.Length; l++)
                    {
                        result.Append(lines[l]).Append('\n');
                    }
                    result.Append('\n');
                    index++;
                }
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
        private int FindTimingLine(string[] lines)
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
        /// <returns>True se il timestamp è valido</returns>
        private bool TryParseTimestamp(string value, out long ms)
        {
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
        private string FormatTimestamp(long ms)
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
}
