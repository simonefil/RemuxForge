using RemuxForge.Core.Subtitles;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

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

        #region Variabili statiche

        /// <summary>
        /// Blocco di tag ASS
        /// </summary>
        private static readonly Regex s_assBlock = new Regex(@"\{[^}]*\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Tag karaoke in un blocco: \k, \K, \kf, \ko con la durata
        /// </summary>
        private static readonly Regex s_karaoke = new Regex(@"\{[^}]*\\(?:k[fo]?|K)\s*\d", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Effect del karaoke e del templater di Aegisub: kara, karaoke, fx, template, code
        /// </summary>
        private static readonly Regex s_karaokeEffect = new Regex(@"^(?:kara|fx|template|code)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Posizione in un blocco: \pos, \move, \org
        /// </summary>
        private static readonly Regex s_position = new Regex(@"\{[^}]*\\(?:pos|move|org)\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Disegno in un blocco: \p con scala positiva
        /// </summary>
        private static readonly Regex s_drawing = new Regex(@"\{[^}]*\\p\s*0*[1-9]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Segni di canzone in un SRT
        /// </summary>
        private static readonly char[] s_songMarks = new char[] { '♪', '♫' };

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

        /// <summary>
        /// Stili delle righe ASS/SSA nell'ordine della prima riga, vuoto in SRT
        /// </summary>
        private readonly List<SubtitleStyleUsage> _styles;

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
        /// <param name="styles">Stili delle righe ASS/SSA</param>
        /// <param name="untimedBlockCount">Blocchi SRT senza riga dei tempi valida</param>
        /// <param name="rejectionReason">Motivo del rifiuto, vuoto per un file valido</param>
        private SubtitleTranslationDocument(SubtitleTranslationFormat format, string text, byte[] preamble, Encoding outputEncoding,
            List<SubtitleCue> cues, List<SubtitleStyleUsage> styles, int untimedBlockCount, string rejectionReason)
        {
            this.Format = format;
            this._text = text;
            this._preamble = preamble;
            this._outputEncoding = outputEncoding;
            this._cues = cues;
            this._styles = styles;
            this.UntimedBlockCount = untimedBlockCount;
            this.RejectionReason = rejectionReason;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge un sottotitolo dai byte del file. Un file che non si può tradurre torna con RejectionReason e nessuna battuta.
        /// Con la tabella degli stili una riga ASS/SSA prende il tipo riconosciuto del suo stile e si esclude solo se non è nella
        /// lingua d'origine; uno stile senza tipo vale come dialogo. Una battuta SRT prende il tipo della tratta riconosciuta di cui
        /// ripete almeno tre righe consecutive; fuori da una tratta che non è nella lingua d'origine, una battuta SRT con ♪ o ♫ è
        /// una canzone
        /// </summary>
        /// <param name="bytes">Byte del file</param>
        /// <param name="format">Formato del file</param>
        /// <param name="encoding">Codifica scelta per un file senza BOM che non è UTF-8, vuota o DEFAULT_ENCODING altrimenti</param>
        /// <param name="styles">Tabella degli stili del progetto; null nei progetti salvati prima, dove le righe con Effect o
        /// karaoke restano escluse, tutte le battute sono dialogo e la pulizia resta quella di allora</param>
        /// <param name="stretches">Tratte SRT riconosciute del progetto, null prima del riconoscimento</param>
        /// <returns>Documento letto</returns>
        public static SubtitleTranslationDocument Read(byte[] bytes, SubtitleTranslationFormat format, string encoding, IReadOnlyList<TranslationProjectStyle> styles,
            IReadOnlyList<TranslationProjectStretch> stretches)
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
                return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, new List<SubtitleStyleUsage>(), 0, REJECTION_UTF16_WITHOUT_BOM);
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
                        return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, new List<SubtitleStyleUsage>(), 0, REJECTION_ENCODING_REQUIRED);

                    // Le codifiche di Aggiungi tracce che il runtime non ha di suo stanno nel provider delle code page
                    try
                    {
                        decoding = CodePagesEncodingProvider.Instance.GetEncoding(encoding) ?? Encoding.GetEncoding(encoding);
                    }
                    catch (ArgumentException)
                    {
                        return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, new List<SubtitleStyleUsage>(), 0, REJECTION_ENCODING_UNKNOWN);
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
                return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, new List<SubtitleStyleUsage>(), 0, REJECTION_ROUNDTRIP_MISMATCH);

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

            bool legacy = styles == null;
            Dictionary<string, TranslationStyleKind?> kinds = new Dictionary<string, TranslationStyleKind?>(StringComparer.Ordinal);
            foreach (TranslationProjectStyle style in styles ?? new List<TranslationProjectStyle>())
                kinds[style.Name] = style.Kind;

            List<RawCue> rawCues = new List<RawCue>();
            List<SubtitleStyleUsage> usages = new List<SubtitleStyleUsage>();
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
                        rawCues.Add(new RawCue(startMs, endMs, after, 0, "", null, SubtitleCueKind.Dialogue, SubtitleCue.EXCLUSION_EMPTY, lines[timing].Terminator));
                    }
                    else
                    {
                        int textStart = lines[timing + 1].Start;
                        rawCues.Add(new RawCue(startMs, endMs, textStart, lines[blockEnd].Start + lines[blockEnd].Length - textStart, "", null,
                            SubtitleCueKind.Dialogue, "", lines[timing].Terminator));
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
                    return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, new List<SubtitleStyleUsage>(), 0, REJECTION_ASS_INVALID_DIALOGUE);
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
                    string style = styleColumn >= 0 ? dialogue.Fields[styleColumn].Trim() : "";
                    string raw = text.Substring(position, lineEnd - position);

                    // Indizi del tipo dello stile sulle righe con testo leggibile, qualunque sia il tipo; i disegni restano fuori
                    SubtitleStyleUsage usage = usages.Find(u => u.Name == style);
                    if (usage == null)
                    {
                        usage = new SubtitleStyleUsage(style);
                        usages.Add(usage);
                    }
                    string readable = s_assBlock.Replace(raw, "").Replace("\\N", " ").Replace("\\n", " ").Replace("\\h", " ").Trim();
                    if (!s_drawing.IsMatch(raw) && readable.Length > 0)
                    {
                        usage.Texts.Add(readable);
                        if (s_karaoke.IsMatch(raw) || s_karaokeEffect.IsMatch(effect))
                            usage.KaraokeLines++;
                        if (s_position.IsMatch(raw))
                            usage.PositionLines++;
                    }

                    // Senza tabella l'Effect esclude e tutto è dialogo; con la tabella esclude solo il testo che non è nella lingua d'origine
                    TranslationStyleKind? styleKind;
                    kinds.TryGetValue(style, out styleKind);
                    string exclusion = legacy && !string.IsNullOrEmpty(effect) ? SubtitleCue.EXCLUSION_EFFECT : "";
                    if (styleKind == TranslationStyleKind.NotSourceLanguage)
                        exclusion = SubtitleCue.EXCLUSION_NOT_SOURCE_LANGUAGE;
                    SubtitleCueKind kind = styleKind == TranslationStyleKind.Song ? SubtitleCueKind.Song : styleKind == TranslationStyleKind.Sign ? SubtitleCueKind.Sign : SubtitleCueKind.Dialogue;
                    rawCues.Add(new RawCue(dialogue.StartMs, dialogue.EndMs, position, lineEnd - position, style, layer, kind, exclusion, ""));
                }
            }

            if (rawCues.Count == 0)
                return new SubtitleTranslationDocument(format, text, preamble, outputEncoding, empty, usages, untimed, REJECTION_NO_CUES);

            // Pulizia
            SubtitleCueCleaner cleaner = new SubtitleCueCleaner(format);
            List<SubtitleCleanResult> cleanedCues = new List<SubtitleCleanResult>();
            foreach (RawCue raw in rawCues)
            {
                cleanedCues.Add(!string.IsNullOrEmpty(raw.ExclusionReason)
                    ? new SubtitleCleanResult("", null, raw.ExclusionReason)
                    : cleaner.Clean(text.Substring(raw.TextStart, raw.TextLength), raw.FallbackLineBreak, legacy));
            }

            // Tipi SRT dalle tratte riconosciute: ogni corsa di almeno tre battute consecutive in ordine di tempo uguali a righe
            // consecutive di una tratta prende il tipo della tratta, e una tratta che non è nella lingua d'origine le esclude
            SubtitleCueKind[] srtKinds = new SubtitleCueKind[rawCues.Count];
            if (format == SubtitleTranslationFormat.Srt && stretches != null)
            {
                List<int> timed = new List<int>();
                for (int i = 0; i < rawCues.Count; i++)
                {
                    if (string.IsNullOrEmpty(cleanedCues[i].ExclusionReason))
                        timed.Add(i);
                }
                timed.Sort((a, b) => rawCues[a].StartMs != rawCues[b].StartMs ? rawCues[a].StartMs.CompareTo(rawCues[b].StartMs) : a.CompareTo(b));
                bool[] claimed = new bool[timed.Count];
                List<string> timedKeys = timed.ConvertAll(i => SharedStretchDetector.Key(format, cleanedCues[i].CleanText));
                foreach (TranslationProjectStretch stretch in stretches)
                {
                    List<string> keys = stretch.Lines.ConvertAll(line => SharedStretchDetector.Key(format, line));
                    for (int p = 0; p < timed.Count; p++)
                    {
                        for (int q = 0; q < keys.Count && !claimed[p]; q++)
                        {
                            int run = 0;
                            while (p + run < timed.Count && q + run < keys.Count && !claimed[p + run]
                                && timedKeys[p + run] == keys[q + run])
                                run++;
                            if (run < SharedStretchDetector.MIN_CUES)
                                continue;
                            for (int r = p; r < p + run; r++)
                            {
                                claimed[r] = true;
                                srtKinds[timed[r]] = stretch.Kind == TranslationStyleKind.Song ? SubtitleCueKind.Song : stretch.Kind == TranslationStyleKind.Sign ? SubtitleCueKind.Sign : SubtitleCueKind.Dialogue;
                                if (stretch.Kind == TranslationStyleKind.NotSourceLanguage)
                                    cleanedCues[timed[r]] = new SubtitleCleanResult("", null, SubtitleCue.EXCLUSION_NOT_SOURCE_LANGUAGE);
                            }
                        }
                    }
                }
            }

            // Id progressivi sulle traducibili e duplicati su layer diversi: stessi tempi e testo pulito, layer diverso
            List<SubtitleCue> cues = new List<SubtitleCue>();
            Dictionary<string, SubtitleCue> firstByContent = new Dictionary<string, SubtitleCue>(StringComparer.Ordinal);
            int nextId = 1;
            for (int i = 0; i < rawCues.Count; i++)
            {
                RawCue raw = rawCues[i];
                SubtitleCleanResult cleaned = cleanedCues[i];
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

                SubtitleCueKind kind = format == SubtitleTranslationFormat.Srt ? srtKinds[i] : raw.Kind;
                if (!legacy && format == SubtitleTranslationFormat.Srt && cleaned.CleanText.IndexOfAny(s_songMarks) >= 0)
                    kind = SubtitleCueKind.Song;
                SubtitleCue cue = new SubtitleCue(id, i + 1, raw.StartMs, raw.EndMs, cleaned.CleanText, cleaned.Recipe, raw.Style, kind, raw.Layer,
                    cleaned.ExclusionReason, duplicateOf, raw.TextStart, raw.TextLength);
                if (id.HasValue && raw.Layer.HasValue)
                    firstByContent.TryAdd(key, cue);
                cues.Add(cue);
            }

            return new SubtitleTranslationDocument(format, text, preamble, outputEncoding, cues, usages, untimed, "");
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
        /// Stili delle righe ASS/SSA nell'ordine della prima riga, con gli indizi del tipo; vuoto in SRT
        /// </summary>
        public IReadOnlyList<SubtitleStyleUsage> Styles
        {
            get { return this._styles; }
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
        /// <param name="Kind">Tipo dello stile ASS/SSA, dialogo in SRT</param>
        /// <param name="ExclusionReason">Esclusione decisa dalla riga (Effect, stile non nella lingua d'origine, battuta SRT senza testo)</param>
        /// <param name="FallbackLineBreak">Terminatore della riga dei tempi SRT</param>
        private sealed record RawCue(long StartMs, long EndMs, int TextStart, int TextLength, string Style, int? Layer, SubtitleCueKind Kind,
            string ExclusionReason, string FallbackLineBreak);

        #endregion
    }
}
