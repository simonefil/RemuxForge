using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Documento ASS/SSA preservabile per rewrite canvas
    /// </summary>
    internal class AssSubtitleDocument
    {
        #region Variabili di classe

        /// <summary>
        /// Righe normalizzate senza terminatore
        /// </summary>
        private readonly List<string> _lines;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="lines">Righe normalizzate senza terminatore</param>
        /// <param name="newLine">Newline dominante da preservare</param>
        private AssSubtitleDocument(List<string> lines, string newLine)
        {
            this._lines = lines;
            this.NewLine = newLine;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Parse preservando righe e newline dominante
        /// </summary>
        /// <param name="content">Contenuto ASS/SSA</param>
        /// <returns>Documento parsato</returns>
        public static AssSubtitleDocument Parse(string content)
        {
            string newLine;
            string normalized;
            string[] parts;
            List<string> lines;

            newLine = content != null && content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            normalized = (content ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", "");
            parts = normalized.Split('\n');
            lines = new List<string>(parts);
            if (lines.Count > 0 && string.IsNullOrEmpty(lines[lines.Count - 1]))
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return new AssSubtitleDocument(lines, newLine);
        }

        /// <summary>
        /// Serializza il documento con newline preservato
        /// </summary>
        /// <returns>Contenuto ASS/SSA</returns>
        public string Serialize()
        {
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < this._lines.Count; i++)
            {
                if (i > 0)
                {
                    result.Append(this.NewLine);
                }

                result.Append(this._lines[i]);
            }

            result.Append(this.NewLine);
            return result.ToString();
        }

        /// <summary>
        /// Prova a leggere un intero dalla sezione Script Info
        /// </summary>
        /// <param name="name">Nome campo</param>
        /// <param name="value">Valore letto</param>
        /// <returns>True se il campo è presente e valido</returns>
        public bool TryGetScriptInfoInt(string name, out int value)
        {
            int index;
            string line;
            string rawValue;

            value = 0;
            index = this.FindScriptInfoLine(name);
            if (index < 0)
            {
                return false;
            }

            line = this._lines[index];
            rawValue = line.Substring(line.IndexOf(':') + 1).Trim();
            return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Prova a leggere un booleano dalla sezione Script Info
        /// </summary>
        /// <param name="name">Nome campo</param>
        /// <param name="value">Valore letto</param>
        /// <returns>True se il campo è presente e valido</returns>
        public bool TryGetScriptInfoBool(string name, out bool value)
        {
            int index;
            string line;
            string rawValue;

            value = false;
            index = this.FindScriptInfoLine(name);
            if (index < 0)
            {
                return false;
            }

            line = this._lines[index];
            rawValue = line.Substring(line.IndexOf(':') + 1).Trim();

            // ASS accetta più forme booleane nei metadata: normalizza solo quelle esplicite
            if (rawValue.Equals("yes", StringComparison.OrdinalIgnoreCase) || rawValue.Equals("true", StringComparison.OrdinalIgnoreCase) || rawValue == "1")
            {
                value = true;
                return true;
            }

            if (rawValue.Equals("no", StringComparison.OrdinalIgnoreCase) || rawValue.Equals("false", StringComparison.OrdinalIgnoreCase) || rawValue == "0")
            {
                value = false;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Imposta o aggiunge un intero nella sezione Script Info
        /// </summary>
        /// <param name="name">Nome campo</param>
        /// <param name="value">Valore da scrivere</param>
        public void SetOrAddScriptInfoInt(string name, int value)
        {
            int index;
            int insertIndex;
            string line = name + ": " + value.ToString(CultureInfo.InvariantCulture);

            index = this.FindScriptInfoLine(name);
            if (index >= 0)
            {
                this._lines[index] = line;
                return;
            }

            insertIndex = this.FindScriptInfoInsertIndex();
            if (insertIndex >= 0)
            {
                this._lines.Insert(insertIndex, line);
            }
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Righe del documento
        /// </summary>
        public List<string> Lines
        {
            get { return this._lines; }
        }

        /// <summary>
        /// Newline dominante del file originale
        /// </summary>
        public string NewLine { get; private set; }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Cerca una riga della sezione Script Info
        /// </summary>
        /// <param name="name">Nome campo da cercare</param>
        /// <returns>Indice della riga trovata, -1 se assente</returns>
        private int FindScriptInfoLine(string name)
        {
            bool inScriptInfo = false;
            string trimmed;
            int colonIndex;
            string currentName;

            // Entra solo nella sezione Script Info: campi omonimi in altre sezioni non vanno toccati
            for (int i = 0; i < this._lines.Count; i++)
            {
                trimmed = this._lines[i].Trim();
                if (string.Equals(trimmed, "[Script Info]", StringComparison.OrdinalIgnoreCase))
                {
                    inScriptInfo = true;
                    continue;
                }

                if (inScriptInfo && trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    return -1;
                }

                if (!inScriptInfo)
                {
                    continue;
                }

                // Le righe Script Info sono coppie nome: valore; commenti e righe libere vengono ignorati
                colonIndex = this._lines[i].IndexOf(':');
                if (colonIndex < 0)
                {
                    continue;
                }

                currentName = this._lines[i].Substring(0, colonIndex).Trim();
                if (string.Equals(currentName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Trova il punto di inserimento nella sezione Script Info
        /// </summary>
        private int FindScriptInfoInsertIndex()
        {
            bool inScriptInfo = false;
            string trimmed;

            for (int i = 0; i < this._lines.Count; i++)
            {
                trimmed = this._lines[i].Trim();
                if (string.Equals(trimmed, "[Script Info]", StringComparison.OrdinalIgnoreCase))
                {
                    inScriptInfo = true;
                    continue;
                }

                if (inScriptInfo && trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return inScriptInfo ? this._lines.Count : -1;
        }

        #endregion
    }
}
