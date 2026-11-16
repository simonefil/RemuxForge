using RemuxForge.Core.Subtitles;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Sottotitolo SRT, ASS o SSA da tradurre. Tiene il testo decodificato del file e, per ogni battuta, l'intervallo del campo
    /// Text: la scrittura sostituisce solo i campi Text delle battute tradotte, quindi senza traduzioni il file esce identico
    /// byte per byte (BOM, terminatori, indici, righe non riconosciute)
    /// </summary>
    public class SubtitleTranslationDocument
    {
        #region Costanti

        /// <summary>Riga Dialogue che AssSubtitleDocument non sa leggere</summary>
        public const string REJECTION_ASS_INVALID_DIALOGUE = "ass-invalid-dialogue";

        /// <summary>Byte nulli senza BOM: UTF-16 senza BOM</summary>
        public const string REJECTION_UTF16_WITHOUT_BOM = "utf16-without-bom";

        /// <summary>UTF-8 non valido e nessuna codifica scelta</summary>
        public const string REJECTION_ENCODING_REQUIRED = "encoding-required";

        /// <summary>Codifica scelta che il runtime non conosce</summary>
        public const string REJECTION_ENCODING_UNKNOWN = "encoding-unknown";

        /// <summary>Il testo letto non torna ai byte del file</summary>
        public const string REJECTION_ROUNDTRIP_MISMATCH = "roundtrip-mismatch";

        /// <summary>Nessuna battuta riconosciuta</summary>
        public const string REJECTION_NO_CUES = "no-cues";

        /// <summary>Codifica predefinita, come in Aggiungi tracce</summary>
        public const string DEFAULT_ENCODING = "UTF-8";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Testo decodificato del file
        /// </summary>
        private readonly string _text;

        /// <summary>
        /// BOM da rimettere davanti in scrittura
        /// </summary>
        private readonly byte[] _preamble;

        /// <summary>
        /// Codifica della scrittura
        /// </summary>
        private readonly Encoding _outputEncoding;

        /// <summary>
        /// Battute nell'ordine del file, escluse comprese
        /// </summary>
        private readonly List<SubtitleCue> _cues;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="format">Formato del file</param>
        /// <param name="text">Testo decodificato</param>
        /// <param name="preamble">BOM da rimettere in scrittura</param>
        /// <param name="outputEncoding">Codifica della scrittura</param>
        /// <param name="cues">Battute nell'ordine del file</param>
        /// <param name="untimedBlockCount">Blocchi SRT senza riga dei tempi valida</param>
        /// <param name="rejectionReason">Motivo del rifiuto, vuoto per un file valido</param>
        private SubtitleTranslationDocument(SubtitleTranslationFormat format, string text, byte[] preamble, Encoding outputEncoding,
            List<SubtitleCue> cues, int untimedBlockCount, string rejectionReason)
        {
            this.Format = format;
            this._text = text;
            this._preamble = preamble;
            this._outputEncoding = outputEncoding;
            this._cues = cues;
            this.UntimedBlockCount = untimedBlockCount;
            this.RejectionReason = rejectionReason;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge un sottotitolo dai byte del file. Un file che non si può tradurre torna con RejectionReason e nessuna battuta
        /// </summary>
        /// <param name="bytes">Byte del file</param>
        /// <param name="format">Formato del file</param>
        /// <param name="encoding">Codifica scelta per un file senza BOM che non è UTF-8, vuota o DEFAULT_ENCODING altrimenti</param>
        /// <returns>Documento letto</returns>
        public static SubtitleTranslationDocument Read(byte[] bytes, SubtitleTranslationFormat format, string encoding)
        {
            byte[] data = bytes ?? new byte[0];
            int offset = 0;
            Encoding decoding;
            Encoding outputEncoding;
            List<SubtitleCue> empty = new List<SubtitleCue>();

            // BOM, poi UTF-8 senza BOM, poi la codifica scelta come in Aggiungi tracce
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            {
                decoding = new UTF8Encoding(false);
                outputEncoding = decoding;
                offset = 3;
            }
            else if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
            {
                decoding = new UnicodeEncoding(data[0] == 0xFE, false);
                outputEncoding = decoding;
                offset = 2;
            }
            else if (Array.IndexOf(data, (byte)0) >= 0)
            {
                return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, 0, REJECTION_UTF16_WITHOUT_BOM);
            }
            else
            {
                outputEncoding = new UTF8Encoding(false);
                decoding = outputEncoding;
                try
                {
                    new UTF8Encoding(false, true).GetString(data);
                }
                catch (DecoderFallbackException)
                {
                    if (string.IsNullOrEmpty(encoding) || string.Equals(encoding, DEFAULT_ENCODING, StringComparison.OrdinalIgnoreCase))
                        return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, 0, REJECTION_ENCODING_REQUIRED);

                    // Le codifiche di Aggiungi tracce che il runtime non ha di suo stanno nel provider delle code page
                    try
                    {
                        decoding = CodePagesEncodingProvider.Instance.GetEncoding(encoding) ?? Encoding.GetEncoding(encoding);
                    }
                    catch (ArgumentException)
                    {
                        return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, 0, REJECTION_ENCODING_UNKNOWN);
                    }
                }
            }

            // La ricodifica deve tornare ai byte letti: una decodifica con perdita rifiuta il file
            string text = decoding.GetString(data, offset, data.Length - offset);
            byte[] encoded = decoding.GetBytes(text);
            bool same = encoded.Length == data.Length - offset;
            for (int i = 0; same && i < encoded.Length; i++)
                same = encoded[i] == data[offset + i];
            if (!same)
                return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, 0, REJECTION_ROUNDTRIP_MISMATCH);

            byte[] preamble = new byte[offset];
            Array.Copy(data, preamble, offset);

            // Righe fisiche: CRLF, LF e CR isolato, anche misti; l'ultima senza terminatore se il file non finisce a capo
            List<SourceLine> lines = new List<SourceLine>();
            int lineStart = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i == text.Length)
                {
                    if (i > lineStart)
                        lines.Add(new SourceLine(lineStart, i - lineStart, ""));
                    break;
                }

                if (text[i] != '\r' && text[i] != '\n')
                    continue;

                string terminator = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? "\r\n" : text[i].ToString();
                lines.Add(new SourceLine(lineStart, i - lineStart, terminator));
                i += terminator.Length - 1;
                lineStart = i + 1;
            }

            List<RawCue> rawCues = new List<RawCue>();
            int untimed = 0;
            if (format == SubtitleTranslationFormat.Srt)
            {
                // Blocchi: righe non vuote consecutive; una riga di soli spazi conta come vuota
                int index = 0;
                while (index < lines.Count)
                {
                    if (string.IsNullOrWhiteSpace(text.Substring(lines[index].Start, lines[index].Length)))
                    {
                        index++;
                        continue;
                    }

                    int blockStart = index;
                    while (index < lines.Count && !string.IsNullOrWhiteSpace(text.Substring(lines[index].Start, lines[index].Length)))
                        index++;
                    int blockEnd = index - 1;

                    // Riga dei tempi: la prima con -->; prima di lei l'indice, che resta com'è
                    int timing = -1;
                    for (int l = blockStart; l <= blockEnd && timing < 0; l++)
                    {
                        if (text.IndexOf("-->", lines[l].Start, lines[l].Length, StringComparison.Ordinal) >= 0)
                            timing = l;
                    }

                    long startMs = 0;
                    long endMs = 0;
                    bool timed = false;
                    if (timing >= 0)
                    {
                        string timingLine = text.Substring(lines[timing].Start, lines[timing].Length);
                        int arrow = timingLine.IndexOf("-->", StringComparison.Ordinal);
                        string[] endTokens = timingLine.Substring(arrow + 3).Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        timed = endTokens.Length > 0
                            && SrtSubtitleTimelineRewriter.TryParseTimestamp(timingLine.Substring(0, arrow).Trim(), out startMs, true)
                            && SrtSubtitleTimelineRewriter.TryParseTimestamp(endTokens[0], out endMs, true);
                    }

                    if (!timed)
                    {
                        untimed++;
                        continue;
                    }

                    // Text: dalla riga dopo i tempi alla fine del contenuto dell'ultima riga del blocco, terminatori interni compresi
                    if (timing == blockEnd)
                    {
                        int after = lines[timing].Start + lines[timing].Length + lines[timing].Terminator.Length;
                        rawCues.Add(new RawCue(startMs, endMs, after, 0, "", null, SubtitleCue.EXCLUSION_EMPTY, lines[timing].Terminator));
                    }
                    else
                    {
                        int textStart = lines[timing + 1].Start;
                        rawCues.Add(new RawCue(startMs, endMs, textStart, lines[blockEnd].Start + lines[blockEnd].Length - textStart, "", null, "",
                            lines[timing].Terminator));
                    }
                }
            }
            else
            {
                List<AssSubtitleDialogue> dialogues;
                try
                {
                    dialogues = AssSubtitleDocument.Parse(text).ReadDialogues();
                }
                catch (InvalidDataException)
                {
                    return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, 0, REJECTION_ASS_INVALID_DIALOGUE);
                }

                // Format di [Events] in vigore su ogni riga, letto come ReadDialogues, per Style e Layer
                string[] formatByLine = new string[lines.Count];
                bool inEvents = false;
                string eventFormat = null;
                for (int l = 0; l < lines.Count; l++)
                {
                    string trimmed = text.Substring(lines[l].Start, lines[l].Length).Replace("\0", "").Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        inEvents = trimmed.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                        eventFormat = null;
                    }
                    if (inEvents && trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                        eventFormat = trimmed.Substring(7);
                    formatByLine[l] = eventFormat;
                }

                foreach (AssSubtitleDialogue dialogue in dialogues)
                {
                    // Text: dopo la virgola che chiude il penultimo campo, fino alla fine della riga fisica (spazi in coda compresi)
                    SourceLine line = lines[dialogue.LineIndex];
                    int position = line.Start;
                    int lineEnd = line.Start + line.Length;
                    while (position < lineEnd && (char.IsWhiteSpace(text[position]) || text[position] == '\0'))
                        position++;
                    position += "Dialogue:".Length;
                    for (int commas = 0; commas < dialogue.Fields.Length - 1; commas++)
                        position = text.IndexOf(',', position, lineEnd - position) + 1;

                    string lineFormat = formatByLine[dialogue.LineIndex];
                    int styleColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "Style");
                    int layerColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "Layer");
                    int? layer = null;
                    if (layerColumn >= 0 && AssSubtitleUtils.TryParseInt(dialogue.Fields[layerColumn], out int layerValue))
                        layer = layerValue;
                    string effect = dialogue.EffectColumn >= 0 ? dialogue.Fields[dialogue.EffectColumn].Trim() : "";
                    rawCues.Add(new RawCue(dialogue.StartMs, dialogue.EndMs, position, lineEnd - position,
                        styleColumn >= 0 ? dialogue.Fields[styleColumn].Trim() : "", layer, !string.IsNullOrEmpty(effect) ? SubtitleCue.EXCLUSION_EFFECT : "", ""));
                }
            }

            if (rawCues.Count == 0)
                return new SubtitleTranslationDocument(format, text, preamble, outputEncoding, empty, untimed, REJECTION_NO_CUES);

            // Pulizia, id progressivi sulle traducibili e duplicati su layer diversi: stessi tempi e testo pulito, layer diverso
            SubtitleCueCleaner cleaner = new SubtitleCueCleaner(format);
            List<SubtitleCue> cues = new List<SubtitleCue>();
            Dictionary<string, SubtitleCue> firstByContent = new Dictionary<string, SubtitleCue>(StringComparer.Ordinal);
            int nextId = 1;
            for (int i = 0; i < rawCues.Count; i++)
            {
                RawCue raw = rawCues[i];
                SubtitleCleanResult cleaned = !string.IsNullOrEmpty(raw.ExclusionReason)
                    ? new SubtitleCleanResult("", null, raw.ExclusionReason)
                    : cleaner.Clean(text.Substring(raw.TextStart, raw.TextLength), raw.FallbackLineBreak);
                int? id = null;
                int? duplicateOf = null;
                string key = raw.StartMs.ToString(CultureInfo.InvariantCulture) + "|" + raw.EndMs.ToString(CultureInfo.InvariantCulture) + "|" + cleaned.CleanText;
                if (string.IsNullOrEmpty(cleaned.ExclusionReason))
                {
                    if (raw.Layer.HasValue && firstByContent.TryGetValue(key, out SubtitleCue original) && original.Layer != raw.Layer)
                    {
                        duplicateOf = original.Id;
                    }
                    else
                    {
                        id = nextId++;
                    }
                }

                SubtitleCue cue = new SubtitleCue(id, i + 1, raw.StartMs, raw.EndMs, cleaned.CleanText, cleaned.Recipe, raw.Style, raw.Layer,
                    cleaned.ExclusionReason, duplicateOf, raw.TextStart, raw.TextLength);
                if (id.HasValue && raw.Layer.HasValue)
                    firstByContent.TryAdd(key, cue);
                cues.Add(cue);
            }

            return new SubtitleTranslationDocument(format, text, preamble, outputEncoding, cues, untimed, "");
        }

        /// <summary>
        /// Scrive il file sostituendo i soli campi Text delle battute tradotte e delle loro copie su altri layer
        /// </summary>
        /// <param name="translatedText">Testo pulito tradotto per id, già validato</param>
        /// <returns>Byte del file, con lo stesso BOM e la stessa codifica del sorgente (UTF-8 per una codifica scelta)</returns>
        public byte[] Write(IReadOnlyDictionary<int, string> translatedText)
        {
            SubtitleCueCleaner cleaner = new SubtitleCueCleaner(this.Format);
            StringBuilder content = new StringBuilder();
            int copied = 0;
            foreach (SubtitleCue cue in this._cues)
            {
                int? source = cue.Id ?? cue.DuplicateOf;
                if (!source.HasValue || !translatedText.TryGetValue(source.Value, out string translation))
                    continue;

                content.Append(this._text, copied, cue.TextStart - copied);
                content.Append(cleaner.Recompose(cue, translation));
                copied = cue.TextStart + cue.TextLength;
            }

            content.Append(this._text, copied, this._text.Length - copied);

            byte[] body = this._outputEncoding.GetBytes(content.ToString());
            byte[] result = new byte[this._preamble.Length + body.Length];
            Array.Copy(this._preamble, result, this._preamble.Length);
            Array.Copy(body, 0, result, this._preamble.Length, body.Length);
            return result;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Formato del file
        /// </summary>
        public SubtitleTranslationFormat Format { get; }

        /// <summary>
        /// Battute nell'ordine del file, escluse e copie comprese
        /// </summary>
        public IReadOnlyList<SubtitleCue> Cues
        {
            get { return this._cues; }
        }

        /// <summary>
        /// Blocchi SRT senza una riga dei tempi valida, lasciati come sono, per il rapporto di preparazione
        /// </summary>
        public int UntimedBlockCount { get; }

        /// <summary>
        /// Motivo del rifiuto del file, vuoto per un file valido
        /// </summary>
        public string RejectionReason { get; }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Riga fisica del testo decodificato, con lo stesso indice di AssSubtitleDocument
        /// </summary>
        /// <param name="Start">Inizio del contenuto</param>
        /// <param name="Length">Lunghezza del contenuto, terminatore escluso</param>
        /// <param name="Terminator">CRLF, LF, CR, o vuoto per l'ultima riga senza newline</param>
        private sealed record SourceLine(int Start, int Length, string Terminator);

        /// <summary>
        /// Battuta letta prima della pulizia
        /// </summary>
        /// <param name="StartMs">Inizio in millisecondi</param>
        /// <param name="EndMs">Fine in millisecondi</param>
        /// <param name="TextStart">Inizio del campo Text</param>
        /// <param name="TextLength">Lunghezza del campo Text</param>
        /// <param name="Style">Stile ASS/SSA</param>
        /// <param name="Layer">Layer ASS</param>
        /// <param name="ExclusionReason">Esclusione decisa dalla riga (Effect, battuta SRT senza testo)</param>
        /// <param name="FallbackLineBreak">Terminatore della riga dei tempi SRT</param>
        private sealed record RawCue(long StartMs, long EndMs, int TextStart, int TextLength, string Style, int? Layer, string ExclusionReason,
            string FallbackLineBreak);

        #endregion
    }
}
