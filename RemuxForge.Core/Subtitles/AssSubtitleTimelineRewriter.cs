using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Riscrive sottotitoli ASS/SSA applicando cut e insert della timeline
    /// </summary>
    internal class AssSubtitleTimelineRewriter
    {
        #region Metodi pubblici

        /// <summary>
        /// Riscrive il contenuto ASS/SSA applicando le operazioni dell'edit map
        /// </summary>
        /// <param name="content">Contenuto ASS/SSA originale</param>
        /// <param name="editMap">Edit map da applicare</param>
        /// <returns>Contenuto ASS/SSA riscritto</returns>
        public string Rewrite(string content, EditMap editMap)
        {
            StringBuilder result = new StringBuilder();
            string normalized = content.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", "");
            string[] lines = normalized.Split('\n');
            string line;
            string trimmed;
            bool inEvents = false;
            int startIndex = 1;
            int endIndex = 2;
            int fieldCount = 0;

            // La sezione Events contiene formato campi e righe Dialogue da riscrivere
            for (int i = 0; i < lines.Length; i++)
            {
                line = lines[i];
                trimmed = line.Trim();

                // Fuori da [Events] il file viene preservato byte-to-line senza interpretare sezioni non sottotitolo
                if (string.Equals(trimmed, "[Events]", StringComparison.OrdinalIgnoreCase))
                {
                    inEvents = true;
                    result.Append(line).Append('\n');
                    continue;
                }

                if (inEvents && trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    inEvents = false;
                }

                // ASS permette ordine campi dinamico: Start/End vanno risolti dalla Format corrente
                if (inEvents && trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                {
                    this.ResolveFormat(trimmed.Substring(7), out startIndex, out endIndex, out fieldCount);
                    result.Append(line).Append('\n');
                    continue;
                }

                // Solo i Dialogue sono renderizzati e quindi vengono mappati sulla timeline editata
                if (inEvents && trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                {
                    this.AppendRewrittenDialogue(result, line, startIndex, endIndex, fieldCount, editMap);
                    continue;
                }

                result.Append(line).Append('\n');
            }

            return result.ToString();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Riscrive una riga Dialogue mantenendo invariati tutti i campi non temporali
        /// </summary>
        /// <param name="result">Buffer output</param>
        /// <param name="line">Riga Dialogue originale</param>
        /// <param name="startIndex">Indice campo Start</param>
        /// <param name="endIndex">Indice campo End</param>
        /// <param name="fieldCount">Numero campi del formato ASS</param>
        /// <param name="editMap">Edit map da applicare</param>
        private void AppendRewrittenDialogue(StringBuilder result, string line, int startIndex, int endIndex, int fieldCount, EditMap editMap)
        {
            string prefix = "Dialogue:";
            string body = line.Substring(prefix.Length);
            string[] fields = fieldCount > 0 ? body.Split(new char[] { ',' }, fieldCount) : body.Split(',');
            List<SubtitleCueInterval> intervals;
            long startMs;
            long endMs;
            if (fields.Length <= startIndex || fields.Length <= endIndex || !this.TryParseTimestamp(fields[startIndex].Trim(), out startMs) || !this.TryParseTimestamp(fields[endIndex].Trim(), out endMs))
            {
                result.Append(line).Append('\n');
                return;
            }

            intervals = SubtitleTimelineMapper.ApplyOperationsToCue(startMs, endMs, editMap);

            // Un Dialogue attraversato da un cut puo' generare piu' righe Dialogue
            for (int i = 0; i < intervals.Count; i++)
            {
                if (intervals[i].EndMs <= intervals[i].StartMs)
                {
                    continue;
                }

                fields[startIndex] = this.FormatTimestamp(intervals[i].StartMs);
                fields[endIndex] = this.FormatTimestamp(intervals[i].EndMs);
                result.Append(prefix).Append(string.Join(",", fields)).Append('\n');
            }
        }

        /// <summary>
        /// Converte un timestamp ASS/SSA in millisecondi
        /// </summary>
        /// <param name="value">Timestamp ASS/SSA</param>
        /// <param name="ms">Millisecondi risultanti</param>
        /// <returns>True se il timestamp è valido</returns>
        private bool TryParseTimestamp(string value, out long ms)
        {
            string[] parts = value.Split(new char[] { ':', '.' });
            int h;
            int m;
            int s;
            int centi;
            ms = 0;

            // Formato atteso: h:mm:ss.cc
            if (parts.Length < 4)
            {
                return false;
            }

            if (!int.TryParse(parts[0], out h) || !int.TryParse(parts[1], out m) || !int.TryParse(parts[2], out s) || !int.TryParse(parts[3], out centi))
            {
                return false;
            }

            ms = (((h * 60L) + m) * 60L + s) * 1000L + (centi * 10L);
            return true;
        }

        /// <summary>
        /// Formatta millisecondi nel formato timestamp ASS/SSA
        /// </summary>
        /// <param name="ms">Millisecondi da formattare</param>
        /// <returns>Timestamp ASS/SSA</returns>
        private string FormatTimestamp(long ms)
        {
            long h;
            long m;
            long s;
            long centi;
            
            // Clamp difensivo: gli eventi ASS non possono iniziare prima di zero
            if (ms < 0)
            {
                ms = 0;
            }

            h = ms / 3600000L;
            ms %= 3600000L;
            m = ms / 60000L;
            ms %= 60000L;
            s = ms / 1000L;
            centi = (ms % 1000L) / 10L;
            return h.ToString(CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) + ":" + s.ToString("00", CultureInfo.InvariantCulture) + "." + centi.ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Risolve gli indici dei campi Start/End dalla riga Format ASS
        /// </summary>
        /// <param name="format">Definizione campi dopo il prefisso Format</param>
        /// <param name="startIndex">Indice campo Start</param>
        /// <param name="endIndex">Indice campo End</param>
        /// <param name="fieldCount">Numero campi dichiarati</param>
        private void ResolveFormat(string format, out int startIndex, out int endIndex, out int fieldCount)
        {
            string[] fields = format.Split(',');
            startIndex = 1;
            endIndex = 2;
            fieldCount = fields.Length;

            // Default ASS classico: Layer, Start, End
            for (int i = 0; i < fields.Length; i++)
            {
                string name = fields[i].Trim();
                if (string.Equals(name, "Start", StringComparison.OrdinalIgnoreCase))
                {
                    startIndex = i;
                }
                else if (string.Equals(name, "End", StringComparison.OrdinalIgnoreCase))
                {
                    endIndex = i;
                }
            }
        }

        #endregion
    }
}
