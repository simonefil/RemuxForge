using RemuxForge.Core.Subtitles;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Sottotitolo SRT, ASS o SSA da tradurre. Tiene il testo decodificato del file e, per ogni battuta, l'intervallo del campo
    /// Text: la scrittura sostituisce solo i campi Text delle battute tradotte, e le righe disegnate da un effetto lettera per
    /// lettera la cui riga sorgente è tradotta, quindi senza traduzioni il file esce identico byte per byte (BOM, terminatori,
    /// indici, righe non riconosciute)
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

        /// <summary>
        /// Margine in millisecondi attorno ai tempi della riga sorgente entro cui stanno le righe di un effetto lettera per lettera:
        /// il massimo misurato sugli ingressi e le uscite animati delle release
        /// </summary>
        public const long LETTER_EFFECT_TOLERANCE_MS = 1210;

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
        /// Ritaglio di una riga: \clip o \iclip
        /// </summary>
        private static readonly Regex s_clip = new Regex(@"\{[^}]*\\i?clip\s*\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        /// Combinazioni di stile, attore ed effetto delle righe ASS/SSA nell'ordine della prima riga, vuoto in SRT
        /// </summary>
        private readonly List<SubtitleStyleUsage> _styles;

        /// <summary>
        /// Righe sorgente degli effetti lettera per lettera per posizione della battuta, con le righe disegnate che sostituiscono
        /// </summary>
        private readonly Dictionary<int, LetterEffect> _letterEffects;

        /// <summary>
        /// Righe che se ne vanno con la traduzione di una battuta, per posizione della battuta: i pezzi di parola animati prima
        /// della riga intera e le evidenziazioni sovrapposte alla riga sotto
        /// </summary>
        private readonly Dictionary<int, List<SourceLine>> _attachedLines;

        /// <summary>
        /// Posizioni delle righe sotto lette coi tratti coperti dalle evidenziazioni già visibili
        /// </summary>
        private readonly HashSet<int> _shownLines;

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
        /// <param name="letterEffects">Righe sorgente degli effetti lettera per lettera per posizione della battuta</param>
        /// <param name="attachedLines">Righe dei pezzi di parola e delle evidenziazioni sovrapposte per posizione della battuta</param>
        /// <param name="shownLines">Posizioni delle righe sotto lette coi tratti coperti dalle evidenziazioni visibili</param>
        /// <param name="untimedBlockCount">Blocchi SRT senza riga dei tempi valida</param>
        /// <param name="rejectionReason">Motivo del rifiuto, vuoto per un file valido</param>
        private SubtitleTranslationDocument(SubtitleTranslationFormat format, string text, byte[] preamble, Encoding outputEncoding,
            List<SubtitleCue> cues, List<SubtitleStyleUsage> styles, Dictionary<int, LetterEffect> letterEffects,
            Dictionary<int, List<SourceLine>> attachedLines,
            HashSet<int> shownLines, int untimedBlockCount, string rejectionReason)
        {
            this.Format = format;
            this._text = text;
            this._preamble = preamble;
            this._outputEncoding = outputEncoding;
            this._cues = cues;
            this._styles = styles;
            this._letterEffects = letterEffects;
            this._attachedLines = attachedLines;
            this._shownLines = shownLines;
            this.UntimedBlockCount = untimedBlockCount;
            this.RejectionReason = rejectionReason;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge un sottotitolo dai byte del file. Un file che non si può tradurre torna con RejectionReason e nessuna battuta.
        /// Con la tabella degli stili una riga ASS/SSA prende il tipo riconosciuto della sua combinazione di stile, attore ed effetto
        /// e si esclude solo se è testo da non tradurre; una combinazione senza tipo vale come dialogo. Una battuta SRT prende il
        /// tipo della sua riga nella tratta riconosciuta di cui ripete almeno tre righe consecutive; fuori dalle righe da non
        /// tradurre, una battuta SRT con ♪ o ♫ è una canzone. Con la tabella degli stili la riga Comment sorgente di un effetto
        /// lettera per lettera è una battuta della sua combinazione e le righe Dialogue disegnate dall'effetto restano escluse
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
                return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, new List<SubtitleStyleUsage>(), new Dictionary<int, LetterEffect>(), new Dictionary<int, List<SourceLine>>(),
                    new HashSet<int>(), 0, REJECTION_UTF16_WITHOUT_BOM);
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
                        return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, new List<SubtitleStyleUsage>(), new Dictionary<int, LetterEffect>(), new Dictionary<int, List<SourceLine>>(),
                            new HashSet<int>(), 0, REJECTION_ENCODING_REQUIRED);

                    // Le codifiche di Aggiungi tracce che il runtime non ha di suo stanno nel provider delle code page
                    try
                    {
                        decoding = CodePagesEncodingProvider.Instance.GetEncoding(encoding) ?? Encoding.GetEncoding(encoding);
                    }
                    catch (ArgumentException)
                    {
                        return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, new List<SubtitleStyleUsage>(), new Dictionary<int, LetterEffect>(), new Dictionary<int, List<SourceLine>>(),
                            new HashSet<int>(), 0, REJECTION_ENCODING_UNKNOWN);
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
                return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, new List<SubtitleStyleUsage>(), new Dictionary<int, LetterEffect>(), new Dictionary<int, List<SourceLine>>(),
                    new HashSet<int>(), 0, REJECTION_ROUNDTRIP_MISMATCH);

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
                kinds[style.Name + "\u0001" + style.Actor + "\u0001" + style.Effect] = style.Kind;

            SubtitleCueCleaner cleaner = new SubtitleCueCleaner(format);
            List<RawCue> rawCues = new List<RawCue>();
            Dictionary<int, LetterEffect> letterEffects = new Dictionary<int, LetterEffect>();
            Dictionary<int, List<SourceLine>> attachedLines = new Dictionary<int, List<SourceLine>>();
            Dictionary<int, string> shownTexts = new Dictionary<int, string>();
            List<SubtitleStyleUsage> usages = new List<SubtitleStyleUsage>();
            List<List<long[]>> intervals = new List<List<long[]>>();
            Dictionary<string, List<long[]>> shown = new Dictionary<string, List<long[]>>(StringComparer.Ordinal);
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
                AssSubtitleDocument assDocument;
                try
                {
                    assDocument = AssSubtitleDocument.Parse(text);
                    dialogues = assDocument.ReadDialogues();
                }
                catch (InvalidDataException)
                {
                    return new SubtitleTranslationDocument(format, "", new byte[0], null, empty, new List<SubtitleStyleUsage>(), new Dictionary<int, LetterEffect>(), new Dictionary<int, List<SourceLine>>(),
                        new HashSet<int>(), 0, REJECTION_ASS_INVALID_DIALOGUE);
                }

                // Format di [Events] in vigore su ogni riga, letto come ReadDialogues, per Style e Layer; le righe Comment valide con
                // lo stesso Format sono le sorgenti possibili di un effetto disegnato lettera per lettera. Degli stili ([V4+ Styles], o
                // [V4 Styles] di un SSA) servono allineamento e margini orizzontali, per la posizione della riga che prende il posto
                // delle lettere, e lo stato per canale, per l'aspetto; l'allineamento SSA (1-3 in basso, +4 in alto, +8 al centro)
                // passa alla numerazione ASS
                string[] formatByLine = new string[lines.Count];
                List<KeyValuePair<AssSubtitleDialogue, bool>> lineEvents = dialogues.ConvertAll(d => new KeyValuePair<AssSubtitleDialogue, bool>(d, false));
                Dictionary<string, int[]> styleLayouts = new Dictionary<string, int[]>(StringComparer.Ordinal);
                Dictionary<string, Dictionary<string, string>> styleStates = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                bool inEvents = false;
                bool inStyles = false;
                bool ssaStyles = false;
                string eventFormat = null;
                string styleFormat = null;
                for (int l = 0; l < lines.Count; l++)
                {
                    string trimmed = text.Substring(lines[l].Start, lines[l].Length).Replace("\0", "").Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        inEvents = trimmed.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                        ssaStyles = trimmed.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase);
                        inStyles = ssaStyles || trimmed.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase);
                        eventFormat = null;
                        styleFormat = null;
                    }
                    if (inEvents && trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                        eventFormat = trimmed.Substring(7);
                    if (inStyles && trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                        styleFormat = trimmed.Substring(7);
                    if (inStyles && trimmed.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
                    {
                        int styleCount = AssSubtitleUtils.CountFields(styleFormat);
                        string[] styleFields = AssSubtitleUtils.SplitFields(trimmed.Substring("Style:".Length).TrimStart(), styleCount);
                        int[] columns = new int[] { AssSubtitleUtils.ResolveFieldIndex(styleFormat, "Alignment"), AssSubtitleUtils.ResolveFieldIndex(styleFormat, "MarginL"),
                            AssSubtitleUtils.ResolveFieldIndex(styleFormat, "MarginR") };
                        int nameColumn = AssSubtitleUtils.ResolveFieldIndex(styleFormat, "Name");
                        int[] layout = new int[3];
                        if (styleCount > 0 && styleFields.Length == styleCount && nameColumn >= 0)
                            styleStates[styleFields[nameColumn].Trim()] = cleaner.ReadStyleState(styleFormat, styleFields, ssaStyles);
                        if (styleCount > 0 && styleFields.Length == styleCount && nameColumn >= 0
                            && Array.TrueForAll(new int[] { 0, 1, 2 }, k => columns[k] >= 0 && AssSubtitleUtils.TryParseInt(styleFields[columns[k]], out layout[k])))
                        {
                            if (ssaStyles)
                                layout[0] = layout[0] >= 1 && layout[0] <= 3 ? layout[0] : layout[0] >= 5 && layout[0] <= 7 ? layout[0] + 2
                                    : layout[0] >= 9 && layout[0] <= 11 ? layout[0] - 5 : 0;
                            styleLayouts[styleFields[nameColumn].Trim()] = layout;
                        }
                    }
                    formatByLine[l] = eventFormat;
                    if (!inEvents || !trimmed.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int count = AssSubtitleUtils.CountFields(eventFormat);
                    int startColumn = AssSubtitleUtils.ResolveFieldIndex(eventFormat, "Start");
                    int endColumn = AssSubtitleUtils.ResolveFieldIndex(eventFormat, "End");
                    string[] fields = AssSubtitleUtils.SplitFields(trimmed.Substring("Comment:".Length).TrimStart(), count);
                    if (count > 0 && startColumn >= 0 && endColumn >= 0 && AssSubtitleUtils.ResolveFieldIndex(eventFormat, "Text") == count - 1 && fields.Length == count
                        && AssSubtitleTimelineRewriter.TryParseTimestamp(fields[startColumn].Trim(), out long commentStart)
                        && AssSubtitleTimelineRewriter.TryParseTimestamp(fields[endColumn].Trim(), out long commentEnd))
                    {
                        lineEvents.Add(new KeyValuePair<AssSubtitleDialogue, bool>(new AssSubtitleDialogue(l, fields, startColumn, endColumn,
                            AssSubtitleUtils.ResolveFieldIndex(eventFormat, "Effect"), commentStart, commentEnd), true));
                    }
                }
                lineEvents.Sort((a, b) => a.Key.LineIndex.CompareTo(b.Key.LineIndex));

                List<AssEvent> events = new List<AssEvent>();
                foreach (KeyValuePair<AssSubtitleDialogue, bool> lineEvent in lineEvents)
                {
                    // Text: dopo la virgola che chiude il penultimo campo, fino alla fine della riga fisica (spazi in coda compresi)
                    AssSubtitleDialogue dialogue = lineEvent.Key;
                    SourceLine line = lines[dialogue.LineIndex];
                    int position = line.Start;
                    int lineEnd = line.Start + line.Length;
                    while (position < lineEnd && (char.IsWhiteSpace(text[position]) || text[position] == '\0'))
                        position++;
                    position += lineEvent.Value ? "Comment:".Length : "Dialogue:".Length;
                    for (int commas = 0; commas < dialogue.Fields.Length - 1; commas++)
                        position = text.IndexOf(',', position, lineEnd - position) + 1;

                    string lineFormat = formatByLine[dialogue.LineIndex];
                    int styleColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "Style");
                    int layerColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "Layer");
                    int actorColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "Name");
                    if (actorColumn < 0)
                        actorColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "Actor");
                    int? layer = null;
                    if (layerColumn >= 0 && AssSubtitleUtils.TryParseInt(dialogue.Fields[layerColumn], out int layerValue))
                        layer = layerValue;
                    string raw = text.Substring(position, lineEnd - position);
                    events.Add(new AssEvent(dialogue, lineEvent.Value, position, lineEnd - position, layerColumn, layer,
                        styleColumn >= 0 ? dialogue.Fields[styleColumn].Trim() : "", actorColumn >= 0 ? dialogue.Fields[actorColumn].Trim() : "",
                        dialogue.EffectColumn >= 0 ? dialogue.Fields[dialogue.EffectColumn].Trim() : "", raw,
                        s_assBlock.Replace(raw, "").Replace("\\N", " ").Replace("\\n", " ").Replace("\\h", " ").Trim(), s_drawing.IsMatch(raw)));
                }

                // Effetti lettera per lettera, solo con la tabella degli stili: una riga Comment è la sorgente delle righe Dialogue
                // della sua combinazione di stile e attore se su ogni layer con righe libere dentro i suoi tempi, allargati della
                // tolleranza, una corsa di righe consecutive dà le sue lettere senza spazi, ognuna ripetuta lo stesso numero di volte
                // (le fasi dell'effetto). Il legame vale solo se riesce su tutti quei layer
                int[] sourceOf = new int[events.Count];
                Array.Fill(sourceOf, -1);
                Dictionary<int, List<int>> generatedBySource = new Dictionary<int, List<int>>();
                Dictionary<string, List<int>> dialoguesByStyle = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                for (int d = 0; d < events.Count && !legacy; d++)
                {
                    if (events[d].Comment)
                        continue;
                    if (!dialoguesByStyle.ContainsKey(events[d].Style + "\u0001" + events[d].Actor))
                        dialoguesByStyle[events[d].Style + "\u0001" + events[d].Actor] = new List<int>();
                    dialoguesByStyle[events[d].Style + "\u0001" + events[d].Actor].Add(d);
                }
                for (int c = 0; c < events.Count && !legacy; c++)
                {
                    AssEvent source = events[c];
                    if (!source.Comment || source.Drawing || !dialoguesByStyle.TryGetValue(source.Style + "\u0001" + source.Actor, out List<int> candidates))
                        continue;

                    List<string> letters = new List<string>();
                    TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(source.Readable);
                    while (elements.MoveNext())
                    {
                        if (!string.IsNullOrWhiteSpace(elements.GetTextElement()))
                            letters.Add(elements.GetTextElement());
                    }
                    if (letters.Count < 2)
                        continue;

                    SortedDictionary<int, List<int>> byLayer = new SortedDictionary<int, List<int>>();
                    foreach (int d in candidates)
                    {
                        AssEvent candidate = events[d];
                        if (sourceOf[d] >= 0 || candidate.Line.StartMs < source.Line.StartMs - LETTER_EFFECT_TOLERANCE_MS || candidate.Line.EndMs > source.Line.EndMs + LETTER_EFFECT_TOLERANCE_MS)
                            continue;
                        if (!byLayer.ContainsKey(candidate.Layer ?? 0))
                            byLayer[candidate.Layer ?? 0] = new List<int>();
                        byLayer[candidate.Layer ?? 0].Add(d);
                    }

                    List<int> generated = new List<int>();
                    bool complete = byLayer.Count > 0;
                    foreach (List<int> layerLines in byLayer.Values)
                    {
                        int matched = 0;
                        for (int p = 0; p < layerLines.Count && matched == 0; p++)
                        {
                            for (int phases = 1; matched == 0 && p + phases * letters.Count <= layerLines.Count && events[layerLines[p + phases - 1]].Readable == letters[0]; phases++)
                            {
                                bool spelled = true;
                                for (int q = 0; q < phases * letters.Count && spelled; q++)
                                    spelled = !events[layerLines[p + q]].Drawing && events[layerLines[p + q]].Readable == letters[q / phases];
                                if (spelled)
                                {
                                    matched = phases * letters.Count;
                                    generated.AddRange(layerLines.GetRange(p, matched));
                                }
                            }
                        }
                        complete &= matched > 0;
                    }
                    if (!complete)
                        continue;

                    generated.Sort();
                    generatedBySource[c] = generated;
                    foreach (int g in generated)
                        sourceOf[g] = c;
                }

                // Pezzi di parola animati, solo con la tabella degli stili: righe posizionate (\pos, \move o \org, come i fotogrammi
                // di un'animazione) della stessa combinazione di stile, attore ed effetto di una riga intera, finite entro il suo
                // inizio, che risalgono senza buchi nel tempo dall'inizio della riga intera (fotogrammi e copie su altri layer). Le
                // battute di dialogo senza posizione non sono mai pezzi. I testi distinti, nell'ordine in cui compaiono, uniti senza
                // spazi danno la riga intera senza spazi, e almeno uno ha più di un carattere: fatti di soli caratteri singoli sono un
                // effetto lettera per lettera. Il legame vale solo se riesce per intero
                int[] wholeOf = new int[events.Count];
                Array.Fill(wholeOf, -1);
                Dictionary<int, List<int>> piecesByWhole = new Dictionary<int, List<int>>();
                string[] spaceless = events.ConvertAll(assEvent => string.Concat(assEvent.Readable.Where(c => !char.IsWhiteSpace(c)))).ToArray();
                Dictionary<string, List<int>> eventsByUnit = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                for (int u = 0; u < events.Count && !legacy; u++)
                {
                    string unit = events[u].Style + "\u0001" + events[u].Actor + "\u0001" + events[u].Effect;
                    if (!events[u].Comment && !events[u].Drawing && sourceOf[u] < 0)
                    {
                        if (!eventsByUnit.ContainsKey(unit))
                            eventsByUnit[unit] = new List<int>();
                        eventsByUnit[unit].Add(u);
                    }
                }
                for (int w = 0; w < events.Count && !legacy; w++)
                {
                    AssEvent whole = events[w];
                    if (whole.Comment || whole.Drawing || sourceOf[w] >= 0 || wholeOf[w] >= 0 || new StringInfo(spaceless[w]).LengthInTextElements < 2)
                        continue;

                    List<int> candidates = eventsByUnit[whole.Style + "\u0001" + whole.Actor + "\u0001" + whole.Effect].FindAll(p => p != w && wholeOf[p] < 0
                        && !piecesByWhole.ContainsKey(p) && s_position.IsMatch(events[p].Raw) && events[p].Line.StartMs < whole.Line.StartMs && events[p].Line.EndMs <= whole.Line.StartMs && spaceless[p].Length > 0 && spaceless[p] != spaceless[w]
                        && spaceless[w].Contains(spaceless[p], StringComparison.Ordinal));

                    // Il tratto coperto parte dall'inizio della riga intera e si allarga all'indietro con ogni riga che lo tocca
                    List<int> chain = new List<int>();
                    long edge = whole.Line.StartMs;
                    List<int> reaching = candidates.FindAll(p => events[p].Line.EndMs >= edge);
                    while (reaching.Count > 0)
                    {
                        chain.AddRange(reaching);
                        edge = Math.Min(edge, reaching.Min(p => events[p].Line.StartMs));
                        reaching = candidates.FindAll(p => !chain.Contains(p) && events[p].Line.EndMs >= edge);
                    }
                    chain.Sort((a, b) => events[a].Line.StartMs != events[b].Line.StartMs ? events[a].Line.StartMs.CompareTo(events[b].Line.StartMs) : a.CompareTo(b));
                    List<string> texts = new List<string>();
                    foreach (int p in chain)
                    {
                        if (!texts.Contains(spaceless[p]))
                            texts.Add(spaceless[p]);
                    }
                    if (chain.Count == 0 || string.Concat(texts) != spaceless[w] || texts.TrueForAll(pieceText => new StringInfo(pieceText).LengthInTextElements == 1))
                        continue;

                    chain.Sort();
                    piecesByWhole[w] = chain;
                    foreach (int p in chain)
                        wholeOf[p] = w;
                }

                // Evidenziazioni sovrapposte, solo con la tabella degli stili: righe ritagliate (\clip o \iclip) e posizionate della
                // stessa unità di una riga sotto, sovrapposte nel tempo, il cui testo è un tratto che la riga sotto nasconde e poi
                // rimostra com'era (un tratto che copre tutta la riga non conta): lo ridisegnano sopra di essa, in un altro colore.
                // Ognuna va con la prima riga sotto nell'ordine del file; ogni riga sotto, comprese le copie su altri layer, si legge
                // coi tratti che le evidenziazioni coprono già visibili: tradotta, si scrive così e le evidenziazioni se ne vanno
                int[] underOf = new int[events.Count];
                Array.Fill(underOf, -1);
                Dictionary<int, List<int>> overlaysByUnder = new Dictionary<int, List<int>>();
                Dictionary<int, List<SubtitleHiddenSpan>> coveredByUnder = new Dictionary<int, List<SubtitleHiddenSpan>>();
                for (int u = 0; u < events.Count && !legacy; u++)
                {
                    AssEvent under = events[u];
                    if (under.Comment || under.Drawing || sourceOf[u] >= 0 || wholeOf[u] >= 0 || underOf[u] >= 0)
                        continue;
                    List<SubtitleHiddenSpan> hidden = cleaner.ReadHiddenSpans(under.Raw, styleStates.GetValueOrDefault(under.Style)).FindAll(span => span.Text != under.Readable);
                    if (hidden.Count == 0)
                        continue;
                    List<int> overlays = eventsByUnit[under.Style + "\u0001" + under.Actor + "\u0001" + under.Effect].FindAll(o => o != u && wholeOf[o] < 0
                        && !overlaysByUnder.ContainsKey(o) && !coveredByUnder.ContainsKey(o) && !piecesByWhole.ContainsKey(o) && s_clip.IsMatch(events[o].Raw)
                        && s_position.IsMatch(events[o].Raw) && events[o].Line.StartMs < under.Line.EndMs && events[o].Line.EndMs > under.Line.StartMs
                        && hidden.Exists(span => span.Text == events[o].Readable));
                    if (overlays.Count == 0)
                        continue;
                    coveredByUnder[u] = hidden.FindAll(span => overlays.Exists(o => events[o].Readable == span.Text));
                    overlays.RemoveAll(o => underOf[o] >= 0);
                    if (overlays.Count == 0)
                        continue;
                    overlaysByUnder[u] = overlays;
                    foreach (int o in overlays)
                        underOf[o] = u;
                }

                for (int e = 0; e < events.Count; e++)
                {
                    AssEvent assEvent = events[e];
                    AssSubtitleDialogue dialogue = assEvent.Line;
                    string style = assEvent.Style;
                    string actor = assEvent.Actor;
                    string effect = assEvent.Effect;
                    string raw = assEvent.Raw;
                    string readable = assEvent.Readable;

                    // Le righe Comment senza effetto legato restano fuori; le righe disegnate dall'effetto restano fuori anche dagli
                    // indizi e lasciano il posto alla riga sorgente tradotta
                    if (assEvent.Comment && !generatedBySource.ContainsKey(e))
                        continue;
                    if (sourceOf[e] >= 0)
                    {
                        rawCues.Add(new RawCue(dialogue.StartMs, dialogue.EndMs, assEvent.TextStart, assEvent.TextLength, style, assEvent.Layer, SubtitleCueKind.Dialogue,
                            SubtitleCue.EXCLUSION_LETTER_EFFECT, ""));
                        continue;
                    }

                    // I pezzi di parola restano fuori anche dagli indizi e se ne vanno quando la riga intera è tradotta
                    if (wholeOf[e] >= 0)
                    {
                        rawCues.Add(new RawCue(dialogue.StartMs, dialogue.EndMs, assEvent.TextStart, assEvent.TextLength, style, assEvent.Layer, SubtitleCueKind.Dialogue,
                            SubtitleCue.EXCLUSION_WORD_PIECE, ""));
                        continue;
                    }
                    // Le evidenziazioni sovrapposte restano fuori anche dagli indizi e se ne vanno quando la riga sotto è tradotta
                    if (underOf[e] >= 0)
                    {
                        rawCues.Add(new RawCue(dialogue.StartMs, dialogue.EndMs, assEvent.TextStart, assEvent.TextLength, style, assEvent.Layer, SubtitleCueKind.Dialogue,
                            SubtitleCue.EXCLUSION_OVERLAY, ""));
                        continue;
                    }
                    List<int> attached = piecesByWhole.GetValueOrDefault(e, new List<int>()).Concat(overlaysByUnder.GetValueOrDefault(e, new List<int>())).ToList();
                    if (attached.Count > 0)
                        attachedLines[rawCues.Count + 1] = attached.ConvertAll(a => lines[events[a].Line.LineIndex]);
                    if (coveredByUnder.TryGetValue(e, out List<SubtitleHiddenSpan> covered))
                        shownTexts[rawCues.Count + 1] = cleaner.ShowHiddenSpans(raw, covered);

                    // Righe tradotte di una sorgente: una Dialogue per layer delle lettere, coi campi della riga Comment, il layer,
                    // l'Effect della prima lettera e l'aspetto fisso delle lettere di quel layer, e il testo senza i tempi del karaoke;
                    // prendono il posto della prima lettera. La posizione viene dalle lettere quando ne hanno una sola: la riga
                    // verticale del loro \an e la loro y, con la colonna dello stile e la x sul suo bordo (margine sinistro, destro o
                    // centro), perché le lettere stanno sul bordo dello stile e una traduzione di altra lunghezza deve partire da lì
                    if (generatedBySource.TryGetValue(e, out List<int> generatedLines))
                    {
                        List<int?> layers = new List<int?>();
                        foreach (int g in generatedLines)
                        {
                            if (!layers.Contains(events[g].Layer))
                                layers.Add(events[g].Layer);
                        }
                        int marginLColumn = AssSubtitleUtils.ResolveFieldIndex(formatByLine[dialogue.LineIndex], "MarginL");
                        int marginRColumn = AssSubtitleUtils.ResolveFieldIndex(formatByLine[dialogue.LineIndex], "MarginR");
                        List<string> prefixes = new List<string>();
                        foreach (int? letterLayer in layers)
                        {
                            SubtitleLetterLook look = cleaner.ReadLetterLook(generatedLines.FindAll(g => events[g].Layer == letterLayer).ConvertAll(g => events[g].Raw),
                                styleStates.GetValueOrDefault(style));
                            string tags = look.Tags;
                            if (look.Alignment.HasValue && look.PositionY != null && styleLayouts.TryGetValue(style, out int[] layout) && layout[0] >= 1 && layout[0] <= 9
                                && assDocument.TryGetScriptInfoInt("PlayResX", out int playResX))
                            {
                                int marginL = marginLColumn >= 0 && AssSubtitleUtils.TryParseInt(dialogue.Fields[marginLColumn], out int lineMarginL) && lineMarginL != 0 ? lineMarginL : layout[1];
                                int marginR = marginRColumn >= 0 && AssSubtitleUtils.TryParseInt(dialogue.Fields[marginRColumn], out int lineMarginR) && lineMarginR != 0 ? lineMarginR : layout[2];
                                int column = (layout[0] - 1) % 3;
                                double x = column == 0 ? marginL : column == 2 ? playResX - marginR : (marginL + playResX - marginR) / 2.0;
                                tags = "\\an" + ((look.Alignment.Value - 1) / 3 * 3 + column + 1).ToString(CultureInfo.InvariantCulture)
                                    + "\\pos(" + x.ToString(CultureInfo.InvariantCulture) + "," + look.PositionY + ")" + tags;
                            }
                            string[] fields = (string[])dialogue.Fields.Clone();
                            if (assEvent.LayerColumn >= 0 && letterLayer.HasValue)
                                fields[assEvent.LayerColumn] = letterLayer.Value.ToString(CultureInfo.InvariantCulture);
                            AssSubtitleDialogue letter = events[generatedLines.Find(g => events[g].Layer == letterLayer)].Line;
                            if (dialogue.EffectColumn >= 0 && letter.EffectColumn >= 0)
                                fields[dialogue.EffectColumn] = letter.Fields[letter.EffectColumn];
                            prefixes.Add("Dialogue: " + string.Join(",", fields, 0, fields.Length - 1) + "," + (tags.Length > 0 ? "{" + tags + "}" : ""));
                        }
                        letterEffects[rawCues.Count + 1] = new LetterEffect(prefixes, generatedLines.ConvertAll(g => lines[events[g].Line.LineIndex]));
                    }

                    // Indizi del tipo della combinazione sulle righe con testo leggibile, qualunque sia il tipo; i disegni restano fuori,
                    // e le righe di un solo carattere, frammenti di un effetto, restano fuori dagli indizi
                    SubtitleStyleUsage usage = usages.Find(u => u.Is(style, actor, effect));
                    if (usage == null)
                    {
                        usage = new SubtitleStyleUsage(style, actor, effect);
                        usages.Add(usage);
                        intervals.Add(new List<long[]>());
                    }
                    bool fragment = new StringInfo(readable).LengthInTextElements == 1;

                    // Una riga continua una riga già contata se ne ripete il testo leggibile negli stessi tempi (copie su altri layer,
                    // pezzi ritagliati) o dall'istante in cui finisce (fotogrammi di un'animazione, dissolvenze): la riga contata si
                    // allunga fino alla sua fine e gli indizi la contano una volta
                    bool continued = false;
                    if (!assEvent.Drawing && readable.Length > 0)
                    {
                        string shownKey = usages.IndexOf(usage).ToString(CultureInfo.InvariantCulture) + "\u0001" + readable;
                        if (!shown.ContainsKey(shownKey))
                            shown[shownKey] = new List<long[]>();
                        long[] counted = shown[shownKey].Find(t => (t[0] == dialogue.StartMs && t[1] == dialogue.EndMs) || t[1] == dialogue.StartMs);
                        continued = counted != null;
                        if (continued)
                        {
                            counted[1] = Math.Max(counted[1], dialogue.EndMs);
                        }
                        else
                        {
                            counted = new long[] { dialogue.StartMs, dialogue.EndMs };
                            shown[shownKey].Add(counted);
                            if (!fragment)
                                intervals[usages.IndexOf(usage)].Add(counted);
                        }
                        usage.Texts.Add(readable);
                        usage.Positions.Add(rawCues.Count + 1);
                        usage.Continued.Add(continued);
                    }
                    if (!assEvent.Drawing && readable.Length > 0 && !fragment && !continued)
                    {
                        if (s_karaoke.IsMatch(raw) || s_karaokeEffect.IsMatch(effect))
                            usage.KaraokeLines++;
                        if (s_position.IsMatch(raw))
                            usage.PositionLines++;
                    }

                    // Senza tabella l'Effect esclude e tutto è dialogo; con la tabella esclude solo il testo da non tradurre: la
                    // combinazione da non tradurre e la riga di un solo carattere, qualunque sia la sua combinazione
                    TranslationStyleKind? styleKind;
                    kinds.TryGetValue(style + "\u0001" + actor + "\u0001" + effect, out styleKind);
                    string exclusion = legacy && !string.IsNullOrEmpty(effect) ? SubtitleCue.EXCLUSION_EFFECT : "";
                    if (styleKind == TranslationStyleKind.NotTranslatable || (!legacy && fragment))
                        exclusion = SubtitleCue.EXCLUSION_NOT_TRANSLATABLE;
                    SubtitleCueKind kind = styleKind == TranslationStyleKind.Song ? SubtitleCueKind.Song : styleKind == TranslationStyleKind.Sign ? SubtitleCueKind.Sign : SubtitleCueKind.Dialogue;
                    rawCues.Add(new RawCue(dialogue.StartMs, dialogue.EndMs, assEvent.TextStart, assEvent.TextLength, style, assEvent.Layer, kind, exclusion, ""));
                }

                // Righe di ogni combinazione sovrapposte nel tempo: una riga si sovrappone se comincia prima che finisca una riga
                // cominciata prima, o se la seguente comincia prima che lei finisca; il massimo sullo schermo conta le fini prima
                // degli inizi allo stesso istante
                for (int u = 0; u < usages.Count; u++)
                {
                    List<long[]> times = intervals[u];
                    times.Sort((a, b) => a[0] != b[0] ? a[0].CompareTo(b[0]) : a[1].CompareTo(b[1]));
                    long reach = long.MinValue;
                    for (int t = 0; t < times.Count; t++)
                    {
                        if (times[t][0] < reach || (t + 1 < times.Count && times[t + 1][0] < times[t][1]))
                            usages[u].OverlappingLines++;
                        reach = Math.Max(reach, times[t][1]);
                    }

                    List<KeyValuePair<long, int>> steps = new List<KeyValuePair<long, int>>();
                    foreach (long[] time in times)
                    {
                        steps.Add(new KeyValuePair<long, int>(time[0], 1));
                        steps.Add(new KeyValuePair<long, int>(time[1], -1));
                    }
                    steps.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Value.CompareTo(b.Value));
                    int concurrent = 0;
                    foreach (KeyValuePair<long, int> step in steps)
                    {
                        concurrent += step.Value;
                        usages[u].MaxConcurrent = Math.Max(usages[u].MaxConcurrent, concurrent);
                    }
                    if (times.Count > 0)
                        usages[u].MaxConcurrent = Math.Max(usages[u].MaxConcurrent, 1);
                }
            }

            if (rawCues.Count == 0)
                return new SubtitleTranslationDocument(format, text, preamble, outputEncoding, empty, usages, letterEffects, attachedLines, new HashSet<int>(shownTexts.Keys), untimed, REJECTION_NO_CUES);

            // Pulizia
            List<SubtitleCleanResult> cleanedCues = new List<SubtitleCleanResult>();
            for (int i = 0; i < rawCues.Count; i++)
            {
                RawCue raw = rawCues[i];
                SubtitleCleanResult cleaned = !string.IsNullOrEmpty(raw.ExclusionReason)
                    ? new SubtitleCleanResult("", null, raw.ExclusionReason)
                    : cleaner.Clean(shownTexts.GetValueOrDefault(i + 1, text.Substring(raw.TextStart, raw.TextLength)), raw.FallbackLineBreak, legacy);

                // Riga SRT di un solo carattere: da non tradurre, come quella ASS
                if (!legacy && string.IsNullOrEmpty(cleaned.ExclusionReason) && new StringInfo(cleaned.CleanText).LengthInTextElements == 1)
                    cleaned = new SubtitleCleanResult("", null, SubtitleCue.EXCLUSION_NOT_TRANSLATABLE);
                cleanedCues.Add(cleaned);
            }

            // Tipi SRT dalle tratte riconosciute: ogni corsa di almeno tre battute consecutive in ordine di tempo uguali a righe
            // consecutive di una tratta prende il tipo di ogni sua riga, e una riga da non tradurre esclude la sua battuta
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
                                TranslationStyleKind lineKind = stretch.Kinds[q + r - p];
                                claimed[r] = true;
                                srtKinds[timed[r]] = lineKind == TranslationStyleKind.Song ? SubtitleCueKind.Song : lineKind == TranslationStyleKind.Sign ? SubtitleCueKind.Sign : SubtitleCueKind.Dialogue;
                                if (lineKind == TranslationStyleKind.NotTranslatable)
                                    cleanedCues[timed[r]] = new SubtitleCleanResult("", null, SubtitleCue.EXCLUSION_NOT_TRANSLATABLE);
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

            return new SubtitleTranslationDocument(format, text, preamble, outputEncoding, cues, usages, letterEffects, attachedLines, new HashSet<int>(shownTexts.Keys), untimed, "");
        }

        /// <summary>
        /// Scrive il file sostituendo i soli campi Text delle battute tradotte e delle loro copie su altri layer; la riga sorgente
        /// tradotta di un effetto lettera per lettera resta com'è e una riga Dialogue col testo tradotto per layer delle lettere
        /// prende il posto delle righe disegnate; i pezzi di parola animati prima di una riga intera tradotta se ne vanno, e così
        /// le evidenziazioni sovrapposte a una riga sotto tradotta, scritta coi tratti che coprivano visibili. Una traduzione uguale al
        /// testo pulito del sorgente, a meno degli spazi e degli a capo come nelle tratte ripetute, lascia lettere, pezzi ed
        /// evidenziazioni come sono
        /// </summary>
        /// <param name="translatedText">Testo pulito tradotto per id, già validato</param>
        /// <returns>Byte del file, con lo stesso BOM e la stessa codifica del sorgente (UTF-8 per una codifica scelta)</returns>
        public byte[] Write(IReadOnlyDictionary<int, string> translatedText)
        {
            SubtitleCueCleaner cleaner = new SubtitleCueCleaner(this.Format);
            List<TextEdit> edits = new List<TextEdit>();
            foreach (SubtitleCue cue in this._cues)
            {
                int? source = cue.Id ?? cue.DuplicateOf;
                if (!source.HasValue || !translatedText.TryGetValue(source.Value, out string translation))
                    continue;

                // Un testo uguale al pulito del sorgente, a meno di spazi e a capo come la chiave delle tratte ripetute, lascia
                // lettere, pezzi di parola ed evidenziazioni come sono in ogni file della tratta
                bool unchanged = SharedStretchDetector.Key(this.Format, translation ?? "") == SharedStretchDetector.Key(this.Format, cue.CleanText);
                if (!this._letterEffects.TryGetValue(cue.Position, out LetterEffect letterEffect))
                {
                    // Una riga sotto letta coi tratti già visibili resta com'è se invariata, e tradotta si scrive coi tratti visibili
                    if (unchanged && this._shownLines.Contains(cue.Position))
                        continue;
                    edits.Add(new TextEdit(cue.TextStart, cue.TextLength, cleaner.Recompose(cue, translation)));
                    if (!unchanged)
                        foreach (SourceLine attached in this._attachedLines.GetValueOrDefault(cue.Position, new List<SourceLine>()))
                            edits.Add(new TextEdit(attached.Start, attached.Length + attached.Terminator.Length, ""));
                    continue;
                }
                if (unchanged)
                    continue;

                string recomposed = cleaner.RemoveKaraokeTags(cleaner.Recompose(cue, translation));
                edits.Add(new TextEdit(letterEffect.Lines[0].Start, letterEffect.Lines[0].Length,
                    string.Join(letterEffect.Lines[0].Terminator, letterEffect.Prefixes.ConvertAll(prefix => prefix + recomposed))));
                for (int i = 1; i < letterEffect.Lines.Count; i++)
                    edits.Add(new TextEdit(letterEffect.Lines[i].Start, letterEffect.Lines[i].Length + letterEffect.Lines[i].Terminator.Length, ""));
            }
            edits.Sort((a, b) => a.Start.CompareTo(b.Start));

            StringBuilder content = new StringBuilder();
            int copied = 0;
            foreach (TextEdit edit in edits)
            {
                content.Append(this._text, copied, edit.Start - copied);
                content.Append(edit.Text);
                copied = edit.Start + edit.Length;
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
        /// Combinazioni di stile, attore ed effetto delle righe ASS/SSA nell'ordine della prima riga, con gli indizi del tipo; vuoto
        /// in SRT
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
        /// <param name="ExclusionReason">Esclusione decisa dalla riga (Effect, testo da non tradurre, battuta SRT senza testo)</param>
        /// <param name="FallbackLineBreak">Terminatore della riga dei tempi SRT</param>
        private sealed record RawCue(long StartMs, long EndMs, int TextStart, int TextLength, string Style, int? Layer, SubtitleCueKind Kind,
            string ExclusionReason, string FallbackLineBreak);

        /// <summary>
        /// Riga Dialogue o Comment di [Events] coi campi che servono alla lettura
        /// </summary>
        /// <param name="Line">Riga letta col suo Format</param>
        /// <param name="Comment">True per una riga Comment</param>
        /// <param name="TextStart">Inizio del campo Text</param>
        /// <param name="TextLength">Lunghezza del campo Text</param>
        /// <param name="LayerColumn">Colonna del Layer nel Format, -1 se manca</param>
        /// <param name="Layer">Layer, null se manca</param>
        /// <param name="Style">Stile</param>
        /// <param name="Actor">Attore</param>
        /// <param name="Effect">Effetto</param>
        /// <param name="Raw">Campo Text</param>
        /// <param name="Readable">Testo senza tag, con gli a capo come spazi</param>
        /// <param name="Drawing">True per un disegno</param>
        private sealed record AssEvent(AssSubtitleDialogue Line, bool Comment, int TextStart, int TextLength, int LayerColumn, int? Layer, string Style, string Actor,
            string Effect, string Raw, string Readable, bool Drawing);

        /// <summary>
        /// Riga sorgente di un effetto lettera per lettera
        /// </summary>
        /// <param name="Prefixes">Inizio delle righe Dialogue tradotte, una per layer, fino al testo tradotto</param>
        /// <param name="Lines">Righe fisiche disegnate dall'effetto nell'ordine del file; la prima lascia il posto alle righe tradotte</param>
        private sealed record LetterEffect(List<string> Prefixes, List<SourceLine> Lines);

        /// <summary>
        /// Sostituzione di un tratto del testo decodificato
        /// </summary>
        /// <param name="Start">Inizio del tratto</param>
        /// <param name="Length">Lunghezza del tratto</param>
        /// <param name="Text">Testo al suo posto</param>
        private sealed record TextEdit(int Start, int Length, string Text);

        #endregion
    }
}
