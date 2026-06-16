using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Documento IDX VobSub preservabile
    /// </summary>
    internal class VobSubIndexDocument
    {
        #region Variabili di classe

        /// <summary>
        /// Righe IDX originali/riscritte
        /// </summary>
        private readonly List<string> _lines;

        /// <summary>
        /// Righe entry rimosse dal documento
        /// </summary>
        private readonly HashSet<int> _removedLines;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="lines">Righe IDX originali</param>
        private VobSubIndexDocument(List<string> lines)
        {
            this._lines = lines;
            this._removedLines = new HashSet<int>();
            this.Entries = new List<VobSubIndexEntry>();
            this.Parse();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge un documento IDX da file
        /// </summary>
        /// <param name="filePath">Path IDX</param>
        /// <returns>Documento letto</returns>
        public static VobSubIndexDocument Load(string filePath)
        {
            return new VobSubIndexDocument(new List<string>(File.ReadAllLines(filePath, Encoding.Latin1)));
        }

        /// <summary>
        /// Salva il documento IDX
        /// </summary>
        /// <param name="filePath">Path output</param>
        public void Save(string filePath)
        {
            File.WriteAllText(filePath, this.Serialize(), Encoding.Latin1);
        }

        /// <summary>
        /// Serializza il documento IDX
        /// </summary>
        /// <returns>Contenuto IDX</returns>
        public string Serialize()
        {
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < this._lines.Count; i++)
            {
                if (this._removedLines.Contains(i))
                {
                    continue;
                }

                result.Append(this._lines[i]).Append('\n');
            }

            return result.ToString();
        }

        /// <summary>
        /// Aggiorna size canvas IDX
        /// </summary>
        /// <param name="width">Larghezza</param>
        /// <param name="height">Altezza</param>
        public void SetSize(int width, int height)
        {
            this.SetOrAddSetting("size", width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture));
            this.Width = width;
            this.Height = height;
        }

        /// <summary>
        /// Aggiorna org IDX
        /// </summary>
        /// <param name="x">Coordinata X</param>
        /// <param name="y">Coordinata Y</param>
        public void SetOrg(int x, int y)
        {
            this.SetOrAddSetting("org", x.ToString(CultureInfo.InvariantCulture) + ", " + y.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Aggiorna scale IDX
        /// </summary>
        /// <param name="xPercent">Scala X percentuale</param>
        /// <param name="yPercent">Scala Y percentuale</param>
        public void SetScale(int xPercent, int yPercent)
        {
            this.SetOrAddSetting("scale", xPercent.ToString(CultureInfo.InvariantCulture) + "%, " + yPercent.ToString(CultureInfo.InvariantCulture) + "%");
        }

        /// <summary>
        /// Aggiorna align IDX
        /// </summary>
        /// <param name="value">Valore align</param>
        public void SetAlign(string value)
        {
            this.SetOrAddSetting("align", value);
        }

        /// <summary>
        /// Riscrive una entry con nuovo timestamp e filepos
        /// </summary>
        /// <param name="entry">Entry da aggiornare</param>
        /// <param name="timestampMs">Timestamp</param>
        /// <param name="filePosition">Filepos</param>
        public void RewriteEntry(VobSubIndexEntry entry, long timestampMs, long filePosition)
        {
            this._lines[entry.LineIndex] = VobSubSubtitleUtils.RewriteEntryLine(this._lines[entry.LineIndex], timestampMs, filePosition);
            entry.TimestampMs = timestampMs;
            entry.FilePosition = filePosition;
        }

        /// <summary>
        /// Rimuove una entry timestamp/filepos dal documento
        /// </summary>
        /// <param name="entry">Entry da rimuovere</param>
        public void RemoveEntry(VobSubIndexEntry entry)
        {
            this._removedLines.Add(entry.LineIndex);
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Entry timestamp/filepos
        /// </summary>
        public List<VobSubIndexEntry> Entries { get; private set; }

        /// <summary>
        /// Larghezza canvas IDX
        /// </summary>
        public int Width { get; private set; }

        /// <summary>
        /// Altezza canvas IDX
        /// </summary>
        public int Height { get; private set; }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Parse impostazioni ed entry
        /// </summary>
        private void Parse()
        {
            long timestampMs;
            long filePosition;

            for (int i = 0; i < this._lines.Count; i++)
            {
                this.TryParseSize(this._lines[i]);
                if (VobSubSubtitleUtils.TryParseEntryLine(this._lines[i], out timestampMs, out filePosition))
                {
                    this.Entries.Add(new VobSubIndexEntry(i, timestampMs, filePosition));
                }
            }
        }

        /// <summary>
        /// Parse riga size
        /// </summary>
        /// <param name="line">Riga IDX da analizzare</param>
        private void TryParseSize(string line)
        {
            int colonIndex;
            string value;
            string[] parts;
            int width;
            int height;

            colonIndex = line.IndexOf(':');
            if (colonIndex < 0 || !line.Substring(0, colonIndex).Trim().Equals("size", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            value = line.Substring(colonIndex + 1).Trim();
            parts = value.Split(new char[] { 'x', 'X' });
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out width) && int.TryParse(parts[1].Trim(), out height))
            {
                this.Width = width;
                this.Height = height;
            }
        }

        /// <summary>
        /// Imposta o aggiunge una riga setting
        /// </summary>
        /// <param name="name">Nome setting IDX</param>
        /// <param name="value">Valore setting da scrivere</param>
        private void SetOrAddSetting(string name, string value)
        {
            int insertIndex = 0;
            int colonIndex;
            string currentName;

            // Aggiorna setting esistente; in alternativa prepara l'inserimento prima dei timestamp
            for (int i = 0; i < this._lines.Count; i++)
            {
                colonIndex = this._lines[i].IndexOf(':');
                if (colonIndex < 0)
                {
                    continue;
                }

                currentName = this._lines[i].Substring(0, colonIndex).Trim();
                if (currentName.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    this._lines[i] = name + ": " + value;
                    return;
                }

                if (!currentName.Equals("timestamp", StringComparison.OrdinalIgnoreCase))
                {
                    insertIndex = i + 1;
                }
            }

            // L'inserimento sposta gli indici delle entry gia' parseate e delle righe rimosse
            this._lines.Insert(insertIndex, name + ": " + value);
            this.ShiftLineState(insertIndex, 1);
        }

        /// <summary>
        /// Aggiorna gli indici interni dopo inserimenti nel documento
        /// </summary>
        /// <param name="startIndex">Indice riga da cui applicare lo shift</param>
        /// <param name="delta">Delta indice da applicare</param>
        private void ShiftLineState(int startIndex, int delta)
        {
            List<int> removedLines;

            // Le entry sono parseate prima dei rewrite metadata: se inseriamo una riga sopra i timestamp vanno riallineate
            for (int i = 0; i < this.Entries.Count; i++)
            {
                if (this.Entries[i].LineIndex >= startIndex)
                {
                    this.Entries[i].ShiftLineIndex(delta);
                }
            }

            if (this._removedLines.Count == 0)
            {
                return;
            }

            removedLines = new List<int>(this._removedLines);
            this._removedLines.Clear();
            for (int i = 0; i < removedLines.Count; i++)
            {
                this._removedLines.Add(removedLines[i] >= startIndex ? removedLines[i] + delta : removedLines[i]);
            }
        }

        #endregion
    }

    /// <summary>
    /// Entry timestamp/filepos letta dal file IDX VobSub
    /// </summary>
    internal sealed class VobSubIndexEntry
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="lineIndex">Indice riga IDX</param>
        /// <param name="timestampMs">Timestamp in millisecondi</param>
        /// <param name="filePosition">Filepos SUB</param>
        public VobSubIndexEntry(int lineIndex, long timestampMs, long filePosition)
        {
            this.LineIndex = lineIndex;
            this.TimestampMs = timestampMs;
            this.FilePosition = filePosition;
        }

        /// <summary>
        /// Indice riga IDX originale
        /// </summary>
        public int LineIndex { get; private set; }

        /// <summary>
        /// Sposta l'indice riga quando il documento inserisce metadata prima della entry
        /// </summary>
        /// <param name="delta">Delta indice da applicare</param>
        public void ShiftLineIndex(int delta)
        {
            this.LineIndex += delta;
        }

        /// <summary>
        /// Timestamp entry in millisecondi
        /// </summary>
        public long TimestampMs { get; set; }

        /// <summary>
        /// Filepos corrente nel SUB
        /// </summary>
        public long FilePosition { get; set; }
    }
}
