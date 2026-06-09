using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Riscrive sottotitoli VobSub IDX/SUB applicando cut e insert ai timestamp e ai filepos
    /// </summary>
    internal class VobSubSubtitleTimelineRewriter
    {
        #region Metodi pubblici

        /// <summary>
        /// Riscrive una coppia IDX/SUB applicando le operazioni dell'edit map
        /// </summary>
        /// <param name="inputIdx">File IDX originale</param>
        /// <param name="inputSub">File SUB originale</param>
        /// <param name="outputIdx">File IDX riscritto</param>
        /// <param name="outputSub">File SUB riscritto</param>
        /// <param name="editMap">Edit map da applicare</param>
        /// <returns>True se la coppia riscritta contiene entry valide</returns>
        public bool Rewrite(string inputIdx, string inputSub, string outputIdx, string outputSub, EditMap editMap)
        {
            string[] lines = File.ReadAllLines(inputIdx, Encoding.Latin1);
            byte[] subData = File.ReadAllBytes(inputSub);
            List<VobSubEntry> entries = this.ParseEntries(lines);
            List<VobSubEntry> keptEntries = new List<VobSubEntry>();
            HashSet<int> entryLines = new HashSet<int>();
            StringBuilder result = new StringBuilder();
            MemoryStream subOutput = new MemoryStream();
            VobSubEntry entry;
            VobSubEntry kept;
            string line;
            long mappedMs;
            long nextFilePosition;
            int keptIndex = 0;
            // IDX contiene i timestamp e i filepos, SUB contiene i packet bitmap collegati
            if (entries.Count == 0)
            {
                return false;
            }

            // Prima fase: mappa timestamp e copia solo i blocchi SUB sopravvissuti ai cut
            for (int i = 0; i < entries.Count; i++)
            {
                entry = entries[i];
                entryLines.Add(entry.LineIndex);
                mappedMs = SubtitleTimelineMapper.MapPacketTimestamp(entry.TimestampMs, editMap);
                if (mappedMs < 0)
                {
                    continue;
                }

                nextFilePosition = i + 1 < entries.Count ? entries[i + 1].FilePosition : subData.Length;
                if (entry.FilePosition < 0 || nextFilePosition < entry.FilePosition || nextFilePosition > subData.Length)
                {
                    return false;
                }

                kept = new VobSubEntry();
                kept.LineIndex = entry.LineIndex;
                kept.TimestampMs = mappedMs;
                kept.FilePosition = subOutput.Position;
                kept.OriginalFilePosition = entry.FilePosition;
                kept.OriginalLength = nextFilePosition - entry.FilePosition;
                keptEntries.Add(kept);

                subOutput.Write(subData, (int)entry.FilePosition, (int)kept.OriginalLength);
            }

            // Nessuna entry valida significa sottotitolo completamente tagliato
            if (keptEntries.Count == 0)
            {
                return false;
            }

            // Seconda fase: riscrive le righe IDX aggiornando timestamp e filepos compattati
            for (int i = 0; i < lines.Length; i++)
            {
                line = lines[i];
                if (keptIndex >= keptEntries.Count || keptEntries[keptIndex].LineIndex != i)
                {
                    if (!entryLines.Contains(i))
                    {
                        result.Append(line).Append('\n');
                    }
                    continue;
                }

                result.Append(this.RewriteEntryLine(line, keptEntries[keptIndex].TimestampMs, keptEntries[keptIndex].FilePosition)).Append('\n');
                keptIndex++;
            }

            File.WriteAllText(outputIdx, result.ToString(), Encoding.Latin1);
            File.WriteAllBytes(outputSub, subOutput.ToArray());
            return true;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Estrae le entry timestamp/filepos dalle righe IDX
        /// </summary>
        /// <param name="lines">Righe IDX</param>
        /// <returns>Entry VobSub ordinate come nel file</returns>
        private List<VobSubEntry> ParseEntries(string[] lines)
        {
            List<VobSubEntry> result = new List<VobSubEntry>();
            VobSubEntry entry;
            long timestampMs;
            long filePosition;
            for (int i = 0; i < lines.Length; i++)
            {
                if (this.TryParseEntryLine(lines[i], out timestampMs, out filePosition))
                {
                    entry = new VobSubEntry();
                    entry.LineIndex = i;
                    entry.TimestampMs = timestampMs;
                    entry.FilePosition = filePosition;
                    result.Add(entry);
                }
            }

            return result;
        }

        /// <summary>
        /// Prova a leggere timestamp e filepos da una riga IDX
        /// </summary>
        /// <param name="line">Riga IDX</param>
        /// <param name="timestampMs">Timestamp in millisecondi</param>
        /// <param name="filePosition">Posizione nel file SUB</param>
        /// <returns>True se la riga contiene una entry valida</returns>
        private bool TryParseEntryLine(string line, out long timestampMs, out long filePosition)
        {
            int timestampIndex;
            int valueStart;
            int commaIndex;
            string timestamp;
            int filePosIndex;
            int filePosStart;
            int filePosEnd;
            string filePosValue;
            timestampMs = 0;
            filePosition = 0;

            // Riga attesa: timestamp: hh:mm:ss:mmm, filepos: xxxxxxxxx
            timestampIndex = line.IndexOf("timestamp:", StringComparison.OrdinalIgnoreCase);
            if (timestampIndex < 0)
            {
                return false;
            }

            valueStart = timestampIndex + "timestamp:".Length;
            commaIndex = line.IndexOf(",", valueStart, StringComparison.Ordinal);
            if (commaIndex < 0)
            {
                return false;
            }

            timestamp = line.Substring(valueStart, commaIndex - valueStart).Trim();
            if (!this.TryParseTimestamp(timestamp, out timestampMs))
            {
                return false;
            }

            filePosIndex = line.IndexOf("filepos:", commaIndex, StringComparison.OrdinalIgnoreCase);
            if (filePosIndex < 0)
            {
                return false;
            }

            filePosStart = filePosIndex + "filepos:".Length;
            while (filePosStart < line.Length && line[filePosStart] == ' ')
            {
                filePosStart++;
            }

            filePosEnd = filePosStart;
            while (filePosEnd < line.Length && this.IsHexChar(line[filePosEnd]))
            {
                filePosEnd++;
            }

            if (filePosEnd <= filePosStart)
            {
                return false;
            }

            filePosValue = line.Substring(filePosStart, filePosEnd - filePosStart);
            return long.TryParse(filePosValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out filePosition);
        }

        /// <summary>
        /// Riscrive una riga IDX con timestamp e filepos aggiornati
        /// </summary>
        /// <param name="line">Riga originale</param>
        /// <param name="timestampMs">Nuovo timestamp</param>
        /// <param name="filePosition">Nuovo filepos</param>
        /// <returns>Riga aggiornata</returns>
        private string RewriteEntryLine(string line, long timestampMs, long filePosition)
        {
            int timestampIndex = line.IndexOf("timestamp:", StringComparison.OrdinalIgnoreCase);
            int valueStart = timestampIndex + "timestamp:".Length;
            int commaIndex = line.IndexOf(",", valueStart, StringComparison.Ordinal);
            int filePosIndex = line.IndexOf("filepos:", commaIndex, StringComparison.OrdinalIgnoreCase);
            int filePosStart = filePosIndex + "filepos:".Length;
            int filePosEnd;
            StringBuilder result;
            while (filePosStart < line.Length && line[filePosStart] == ' ')
            {
                filePosStart++;
            }

            filePosEnd = filePosStart;
            while (filePosEnd < line.Length && this.IsHexChar(line[filePosEnd]))
            {
                filePosEnd++;
            }

            result = new StringBuilder();
            result.Append(line.Substring(0, valueStart));
            result.Append(" ");
            result.Append(this.FormatTimestamp(timestampMs));
            result.Append(line.Substring(commaIndex, filePosStart - commaIndex));
            result.Append(filePosition.ToString("x9", CultureInfo.InvariantCulture));
            result.Append(line.Substring(filePosEnd));
            return result.ToString();
        }

        /// <summary>
        /// Converte un timestamp IDX in millisecondi
        /// </summary>
        /// <param name="value">Timestamp IDX</param>
        /// <param name="ms">Millisecondi risultanti</param>
        /// <returns>True se il timestamp è valido</returns>
        private bool TryParseTimestamp(string value, out long ms)
        {
            string[] parts = value.Split(new char[] { ':', '.' });
            int h;
            int m;
            int s;
            int milli;
            ms = 0;

            // Formato atteso: hh:mm:ss:mmm
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
        /// Formatta millisecondi nel formato timestamp IDX
        /// </summary>
        /// <param name="ms">Millisecondi da formattare</param>
        /// <returns>Timestamp IDX</returns>
        private string FormatTimestamp(long ms)
        {
            long h;
            long m;
            long s;
            long milli;
            // Clamp difensivo per entry spostate prima dell'origine
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
            return h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) + ":" + s.ToString("00", CultureInfo.InvariantCulture) + ":" + milli.ToString("000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Indica se il carattere appartiene a un valore esadecimale filepos
        /// </summary>
        /// <param name="value">Carattere da verificare</param>
        /// <returns>True se è esadecimale</returns>
        private bool IsHexChar(char value)
        {
            return (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F');
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Entry timestamp/filepos letta dal file IDX VobSub
        /// </summary>
        private class VobSubEntry
        {
            /// <summary>
            /// Indice riga IDX originale
            /// </summary>
            public int LineIndex { get; set; }

            /// <summary>
            /// Timestamp entry in millisecondi
            /// </summary>
            public long TimestampMs { get; set; }

            /// <summary>
            /// Filepos corrente nel SUB riscritto
            /// </summary>
            public long FilePosition { get; set; }

            /// <summary>
            /// Filepos originale nel SUB sorgente
            /// </summary>
            public long OriginalFilePosition { get; set; }

            /// <summary>
            /// Lunghezza originale del blocco SUB
            /// </summary>
            public long OriginalLength { get; set; }
        }

        #endregion
    }
}
