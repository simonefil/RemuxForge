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
    /// Text: la scrittura sostituisce solo i campi Text delle battute tradotte e le righe delle frasi animate la cui riga intera è
    /// tradotta, quindi senza traduzioni il file esce identico byte per byte (BOM, terminatori, indici, righe non riconosciute)
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
        /// Margine in millisecondi attorno ai tempi di una riga Comment entro cui le righe disegnate dalle sue lettere le si legano:
        /// la durata massima di un ingresso o di un'uscita animati fuori dai tempi della riga Comment
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
        /// Animazione o posizione in un blocco: \pos, \move, \org, \t, \fad, \fade, \clip, \iclip e i tempi del karaoke
        /// </summary>
        private static readonly Regex s_animation = new Regex(@"\{[^}]*\\(?:pos|move|org|t|fad|i?clip|k|K)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        /// Righe Comment delle frasi animate per posizione della battuta, con le righe disegnate che sostituiscono
        /// </summary>
        private readonly Dictionary<int, LetterEffect> _letterEffects;

        /// <summary>
        /// Righe che se ne vanno con la traduzione di una battuta, per posizione della battuta: i pezzi della frase animata di cui
        /// la battuta è la riga intera
        /// </summary>
        private readonly Dictionary<int, List<SourceLine>> _attachedLines;

        /// <summary>
        /// Posizioni delle righe intere lette con la frase già visibile
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
        /// <param name="letterEffects">Righe Comment delle frasi animate per posizione della battuta</param>
        /// <param name="attachedLines">Righe dei pezzi delle frasi animate per posizione della riga intera</param>
        /// <param name="shownLines">Posizioni delle righe intere lette con la frase visibile</param>
        /// <param name="rejectionReason">Motivo del rifiuto, vuoto per un file valido</param>
        private SubtitleTranslationDocument(SubtitleTranslationFormat format, string text, byte[] preamble, Encoding outputEncoding,
            List<SubtitleCue> cues, List<SubtitleStyleUsage> styles, Dictionary<int, LetterEffect> letterEffects,
            Dictionary<int, List<SourceLine>> attachedLines,
            HashSet<int> shownLines, string rejectionReason)
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
            this.RejectionReason = rejectionReason;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge un sottotitolo dai byte del file. Un file che non si può tradurre torna con RejectionReason e nessuna battuta.
        /// Una riga ASS/SSA prende il tipo riconosciuto della sua combinazione di stile, attore ed effetto
        /// e si esclude solo se è testo da non tradurre; una combinazione senza tipo vale come dialogo. Una battuta SRT prende il
        /// tipo della sua riga nella tratta riconosciuta di cui ripete almeno tre righe consecutive; fuori dalle righe da non
        /// tradurre, una battuta SRT con ♪ o ♫ è una canzone. Di una frase animata, i cui pezzi mostrano a schermo una riga intera
        /// della stessa combinazione (riga Comment o Dialogue), la riga intera è una battuta e i pezzi restano esclusi
        /// </summary>
        /// <param name="bytes">Byte del file</param>
        /// <param name="format">Formato del file</param>
        /// <param name="encoding">Codifica scelta per un file senza BOM che non è UTF-8, vuota o DEFAULT_ENCODING altrimenti</param>
        /// <param name="styles">Tabella degli stili del progetto</param>
        /// <param name="stretches">Tratte SRT riconosciute del progetto, null prima del riconoscimento</param>
        /// <returns>Documento letto</returns>
        public static SubtitleTranslationDocument Read(byte[] bytes, SubtitleTranslationFormat format, string encoding, IReadOnlyList<TranslationProjectStyle> styles,
            IReadOnlyList<TranslationProjectStretch> stretches)
        {
            byte[] data = bytes ?? new byte[0];
            TextSubtitleEncodingResult detected = TextSubtitleEncoding.Detect(data);
            int offset = detected.BomLength;
            Encoding decoding;
            Encoding outputEncoding;

            // BOM, poi UTF-8 senza BOM, poi la codifica scelta come in Aggiungi tracce
            if (detected.Kind == TextSubtitleEncodingKind.Utf16WithoutBom)
                return Rejected(format, REJECTION_UTF16_WITHOUT_BOM);

            outputEncoding = detected.Kind == TextSubtitleEncodingKind.NotUtf8 ? new UTF8Encoding(false) : detected.Encoding;
            decoding = outputEncoding;
            if (detected.Kind == TextSubtitleEncodingKind.NotUtf8)
            {
                if (string.IsNullOrEmpty(encoding) || string.Equals(encoding, DEFAULT_ENCODING, StringComparison.OrdinalIgnoreCase))
                    return Rejected(format, REJECTION_ENCODING_REQUIRED);

                // Le codifiche di Aggiungi tracce che il runtime non ha di suo stanno nel provider delle code page
                try
                {
                    decoding = CodePagesEncodingProvider.Instance.GetEncoding(encoding) ?? Encoding.GetEncoding(encoding);
                }
                catch (ArgumentException)
                {
                    return Rejected(format, REJECTION_ENCODING_UNKNOWN);
                }
            }

            // La ricodifica deve tornare ai byte letti: una decodifica con perdita rifiuta il file
            string text = decoding.GetString(data, offset, data.Length - offset);
            byte[] encoded = decoding.GetBytes(text);
            bool same = encoded.Length == data.Length - offset;
            for (int i = 0; same && i < encoded.Length; i++)
                same = encoded[i] == data[offset + i];
            if (!same)
                return Rejected(format, REJECTION_ROUNDTRIP_MISMATCH);

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

            Dictionary<string, TranslationStyleKind?> kinds = new Dictionary<string, TranslationStyleKind?>(StringComparer.Ordinal);
            foreach (TranslationProjectStyle style in styles)
                kinds[style.Name + "\u0001" + style.Actor + "\u0001" + style.Effect] = style.Kind;

            SubtitleCueCleaner cleaner = new SubtitleCueCleaner(format);
            List<RawCue> rawCues = new List<RawCue>();
            Dictionary<int, LetterEffect> letterEffects = new Dictionary<int, LetterEffect>();
            Dictionary<int, List<SourceLine>> attachedLines = new Dictionary<int, List<SourceLine>>();
            Dictionary<int, string> shownTexts = new Dictionary<int, string>();
            Dictionary<int, int> copyPositions = new Dictionary<int, int>();
            List<SubtitleStyleUsage> usages = new List<SubtitleStyleUsage>();
            List<List<ShownInterval>> intervals = new List<List<ShownInterval>>();
            Dictionary<string, List<ShownInterval>> shown = new Dictionary<string, List<ShownInterval>>(StringComparer.Ordinal);
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
                        continue;

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
                    dialogues = assDocument.ReadDialogues(true);
                }
                catch (InvalidDataException)
                {
                    return Rejected(format, REJECTION_ASS_INVALID_DIALOGUE);
                }

                // Le righe Comment valide possono dare la frase di righe animate. Degli stili ([V4+ Styles], o [V4 Styles] di un
                // SSA) servono allineamento e margini, per la riga a schermo delle righe e la posizione della riga che prende il posto
                // delle righe disegnate da un Comment, e lo stato per canale, per la visibilità e l'aspetto; l'allineamento SSA (1-3
                // in basso, +4 in alto, +8 al centro) passa alla numerazione ASS
                Dictionary<string, AssStyleLayout> styleLayouts = new Dictionary<string, AssStyleLayout>(StringComparer.Ordinal);
                Dictionary<string, Dictionary<string, string>> styleStates = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
                bool inStyles = false;
                bool ssaStyles = false;
                string styleFormat = null;
                for (int l = 0; l < lines.Count; l++)
                {
                    string trimmed = text.Substring(lines[l].Start, lines[l].Length).Replace("\0", "").Trim();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        ssaStyles = trimmed.Equals("[V4 Styles]", StringComparison.OrdinalIgnoreCase);
                        inStyles = ssaStyles || trimmed.Equals("[V4+ Styles]", StringComparison.OrdinalIgnoreCase);
                        styleFormat = null;
                    }
                    if (inStyles && trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
                        styleFormat = trimmed.Substring(7);
                    if (inStyles && trimmed.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
                    {
                        int styleCount = AssSubtitleUtils.CountFields(styleFormat);
                        string[] styleFields = AssSubtitleUtils.SplitFields(trimmed.Substring("Style:".Length).TrimStart(), styleCount);
                        int alignmentColumn = AssSubtitleUtils.ResolveFieldIndex(styleFormat, "Alignment");
                        int marginLColumn = AssSubtitleUtils.ResolveFieldIndex(styleFormat, "MarginL");
                        int marginRColumn = AssSubtitleUtils.ResolveFieldIndex(styleFormat, "MarginR");
                        int marginVColumn = AssSubtitleUtils.ResolveFieldIndex(styleFormat, "MarginV");
                        int nameColumn = AssSubtitleUtils.ResolveFieldIndex(styleFormat, "Name");
                        if (styleCount > 0 && styleFields.Length == styleCount && nameColumn >= 0)
                            styleStates[styleFields[nameColumn].Trim()] = cleaner.ReadStyleState(styleFormat, styleFields, ssaStyles);
                        if (styleCount > 0 && styleFields.Length == styleCount && nameColumn >= 0
                            && alignmentColumn >= 0 && AssSubtitleUtils.TryParseInt(styleFields[alignmentColumn], out int alignment)
                            && marginLColumn >= 0 && AssSubtitleUtils.TryParseInt(styleFields[marginLColumn], out int marginL)
                            && marginRColumn >= 0 && AssSubtitleUtils.TryParseInt(styleFields[marginRColumn], out int marginR)
                            && marginVColumn >= 0 && AssSubtitleUtils.TryParseInt(styleFields[marginVColumn], out int marginV))
                        {
                            if (ssaStyles)
                                alignment = SubtitleCueCleaner.ToAssAlignment(alignment);
                            styleLayouts[styleFields[nameColumn].Trim()] = new AssStyleLayout(alignment, marginL, marginR, marginV);
                        }
                    }
                }

                List<AssEvent> events = new List<AssEvent>();
                foreach (AssSubtitleDialogue dialogue in dialogues)
                {
                    // Text: dopo la virgola che chiude il penultimo campo, fino alla fine della riga fisica (spazi in coda compresi)
                    SourceLine line = lines[dialogue.LineIndex];
                    int position = line.Start;
                    int lineEnd = line.Start + line.Length;
                    while (position < lineEnd && (char.IsWhiteSpace(text[position]) || text[position] == '\0'))
                        position++;
                    position += dialogue.Comment ? "Comment:".Length : "Dialogue:".Length;
                    for (int commas = 0; commas < dialogue.Fields.Length - 1; commas++)
                        position = text.IndexOf(',', position, lineEnd - position) + 1;

                    string lineFormat = dialogue.Format;
                    int styleColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "Style");
                    int layerColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "Layer");
                    int actorColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "Name");
                    if (actorColumn < 0)
                        actorColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "Actor");
                    int? layer = null;
                    if (layerColumn >= 0 && AssSubtitleUtils.TryParseInt(dialogue.Fields[layerColumn], out int layerValue))
                        layer = layerValue;
                    string raw = text.Substring(position, lineEnd - position);
                    events.Add(new AssEvent(dialogue, dialogue.Comment, position, lineEnd - position, layerColumn, layer,
                        styleColumn >= 0 ? dialogue.Fields[styleColumn].Trim() : "", actorColumn >= 0 ? dialogue.Fields[actorColumn].Trim() : "",
                        dialogue.EffectColumn >= 0 ? dialogue.Fields[dialogue.EffectColumn].Trim() : "", raw,
                        s_assBlock.Replace(raw, "").Replace("\\N", " ").Replace("\\n", " ").Replace("\\h", " ").Trim(), s_drawing.IsMatch(raw)));
                }

                // Frasi animate, solo con la tabella degli stili. Una combinazione di stile, attore ed effetto alla volta; un pezzo è una
                // riga Dialogue animata o posizionata che mostra almeno un carattere, e la sua riga intera è un'altra riga della
                // combinazione, o una riga Comment dello stesso stile e attore che non sia un modello del templater, che si sovrappone
                // o si tocca nel tempo col pezzo, sta sulla stessa riga a schermo (per un Comment non si guarda) e ha un testo senza
                // spazi più lungo dei caratteri mostrati dal pezzo, che contiene i suoi tratti visibili nell'ordine. Il pezzo va con la
                // riga intera con più tempo in comune, poi con quella che comincia più vicino, poi con la prima nel file; a parità fra
                // testi diversi va con quella i cui pezzi già assegnati stanno sulla sua riga a schermo, e se non è una sola i gruppi
                // coinvolti non si formano. Un pezzo senza riga intera segue la scritta di cui è la continuazione (stesso testo
                // mostrato, comincia dove l'altra finisce), se no la riga Comment più vicina nel tempo entro la tolleranza delle
                // lettere; le copie di una riga intera (stesso testo, stessa riga a schermo, tempi che si toccano) entrano nel suo
                // gruppo. Il gruppo vale se ha una riga che contiene i tratti visibili di tutte, una riga che ne mostra solo una parte,
                // due righe con testi mostrati diversi a schermo insieme (una serie di righe una dopo l'altra
                // è una rivelazione, salvo i pezzi di parola che in sequenza compongono la riga che segue) e, nell'insieme delle righe,
                // tutti i caratteri della frase. La frase è la riga più lunga fra quelle che contengono tutti i tratti, una Dialogue prima
                // di un Comment; la riga principale è la Dialogue con la frase, la più lunga nel tempo e poi sul layer più basso, o il
                // Comment

                // PlayRes mancanti come in libass quando lo script non li dichiara
                int screenWidth = assDocument.TryGetScriptInfoInt("PlayResX", out int scriptWidth) ? scriptWidth : 0;
                int screenHeight = assDocument.TryGetScriptInfoInt("PlayResY", out int scriptHeight) ? scriptHeight : 0;
                if (screenWidth <= 0 && screenHeight <= 0)
                {
                    screenWidth = 384;
                    screenHeight = 288;
                }
                else if (screenHeight <= 0)
                {
                    screenHeight = screenWidth == 1280 ? 1024 : screenWidth * 3 / 4;
                }
                else if (screenWidth <= 0)
                {
                    screenWidth = screenHeight == 1024 ? 1280 : screenHeight * 4 / 3;
                }
                string[] spaceless = events.ConvertAll(assEvent => string.Concat(assEvent.Readable.Where(c => !char.IsWhiteSpace(c)))).ToArray();
                string[] shownOf = new string[events.Count];
                List<string>[] fragmentsOf = new List<string>[events.Count];
                List<AssScreenBox>[] boxesOf = new List<AssScreenBox>[events.Count];
                Dictionary<string, List<int>> linesByUnit = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                Dictionary<string, List<int>> commentsByStyle = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                for (int e = 0; e < events.Count; e++)
                {
                    AssEvent assEvent = events[e];
                    if (assEvent.Drawing || spaceless[e].Length == 0 || (assEvent.Comment && Array.Exists(new string[] { "template", "code", "mixin" },
                        model => assEvent.Effect.StartsWith(model, StringComparison.OrdinalIgnoreCase))))
                        continue;

                    // Tratti visibili: caratteri senza spazi mostrati in qualche istante, divisi dai caratteri nascosti
                    List<bool> visible = cleaner.ReadVisibleCharacters(assEvent.Raw, assEvent.Style, styleStates);
                    StringBuilder shownCharacters = new StringBuilder();
                    StringBuilder fragment = new StringBuilder();
                    fragmentsOf[e] = new List<string>();
                    for (int c = 0; c < spaceless[e].Length && c < visible.Count; c++)
                    {
                        if (visible[c])
                        {
                            shownCharacters.Append(spaceless[e][c]);
                            fragment.Append(spaceless[e][c]);
                        }
                        else if (fragment.Length > 0)
                        {
                            fragmentsOf[e].Add(fragment.ToString());
                            fragment.Clear();
                        }
                    }
                    if (fragment.Length > 0)
                        fragmentsOf[e].Add(fragment.ToString());
                    shownOf[e] = shownCharacters.ToString();

                    string group = assEvent.Style + "\u0001" + assEvent.Actor;
                    if (assEvent.Comment)
                    {
                        if (!commentsByStyle.ContainsKey(group))
                            commentsByStyle[group] = new List<int>();
                        commentsByStyle[group].Add(e);
                        continue;
                    }
                    if (!linesByUnit.ContainsKey(group + "\u0001" + assEvent.Effect))
                        linesByUnit[group + "\u0001" + assEvent.Effect] = new List<int>();
                    linesByUnit[group + "\u0001" + assEvent.Effect].Add(e);
                    string lineFormat = assEvent.Line.Format;
                    int lineMarginLColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "MarginL");
                    int lineMarginRColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "MarginR");
                    int lineMarginVColumn = AssSubtitleUtils.ResolveFieldIndex(lineFormat, "MarginV");
                    AssLineMargins margins = new AssLineMargins(
                        lineMarginLColumn >= 0 && AssSubtitleUtils.TryParseInt(assEvent.Line.Fields[lineMarginLColumn], out int lineMarginL) ? lineMarginL : 0,
                        lineMarginRColumn >= 0 && AssSubtitleUtils.TryParseInt(assEvent.Line.Fields[lineMarginRColumn], out int lineMarginR) ? lineMarginR : 0,
                        lineMarginVColumn >= 0 && AssSubtitleUtils.TryParseInt(assEvent.Line.Fields[lineMarginVColumn], out int lineMarginV) ? lineMarginV : 0);
                    if (styleLayouts.TryGetValue(assEvent.Style, out AssStyleLayout styleLayout))
                        boxesOf[e] = cleaner.ReadScreenBoxes(assEvent.Raw, styleStates.GetValueOrDefault(assEvent.Style), styleLayout, margins, screenWidth, screenHeight);
                }

                // Per ogni riga di un gruppo che se ne va, la riga principale; le righe disegnate di ogni Comment principale; i pezzi
                // tolti con la traduzione di ogni riga principale Dialogue; le righe intere lette con la frase visibile e le loro copie
                int[] phraseOf = new int[events.Count];
                Array.Fill(phraseOf, -1);
                Dictionary<int, List<int>> generatedBySource = new Dictionary<int, List<int>>();
                Dictionary<int, List<int>> piecesByPhrase = new Dictionary<int, List<int>>();
                Dictionary<int, string> wholeShown = new Dictionary<int, string>();
                Dictionary<int, int> copyOf = new Dictionary<int, int>();
                Dictionary<int, int> basePositions = new Dictionary<int, int>();
                foreach (KeyValuePair<string, List<int>> unit in linesByUnit)
                {
                    List<int> unitLines = unit.Value.OrderBy(l => events[l].Line.StartMs).ThenBy(l => l).ToList();
                    List<int> candidates = unitLines.Concat(commentsByStyle.GetValueOrDefault(unit.Key.Substring(0, unit.Key.LastIndexOf('\u0001')), new List<int>())).ToList();
                    List<int> pieces = unitLines.FindAll(l => s_animation.IsMatch(events[l].Raw) && fragmentsOf[l].Count > 0);
                    Dictionary<int, int> owner = new Dictionary<int, int>();
                    List<KeyValuePair<int, List<int>>> tied = new List<KeyValuePair<int, List<int>>>();
                    foreach (int piece in pieces)
                    {
                        List<int> wholes = candidates.FindAll(w => w != piece && Touch(events, piece, w) && spaceless[w].Length > shownOf[piece].Length
                            && ContainsInOrder(fragmentsOf[piece], spaceless[w]) && (events[w].Comment || SameRow(boxesOf, piece, w)))
                            .OrderByDescending(w => SharedTime(events, piece, w)).ThenBy(w => Math.Abs(events[w].Line.StartMs - events[piece].Line.StartMs)).ThenBy(w => w).ToList();
                        if (wholes.Count == 0)
                            continue;
                        List<int> best = wholes.FindAll(w => SharedTime(events, piece, w) == SharedTime(events, piece, wholes[0])
                            && Math.Abs(events[w].Line.StartMs - events[piece].Line.StartMs) == Math.Abs(events[wholes[0]].Line.StartMs - events[piece].Line.StartMs));
                        if (best.Select(w => spaceless[w]).Distinct(StringComparer.Ordinal).Count() > 1)
                        {
                            tied.Add(new KeyValuePair<int, List<int>>(piece, best));
                        }
                        else
                        {
                            owner[piece] = wholes[0];
                        }
                    }

                    // Pareggi fra righe intere di testo diverso: un candidato che è a sua volta un pezzo conta come la sua riga intera
                    Dictionary<int, int> firm = new Dictionary<int, int>(owner);
                    HashSet<int> ambiguous = new HashSet<int>();
                    foreach (KeyValuePair<int, List<int>> tie in tied)
                    {
                        List<int> roots = tie.Value.Select(w => RootOf(firm, w)).Distinct().ToList();
                        List<int> hits = roots.FindAll(root => pieces.Exists(other => firm.TryGetValue(other, out int whole) && RootOf(firm, whole) == root && SameRow(boxesOf, tie.Key, other)));
                        if (roots.Count == 1)
                        {
                            owner[tie.Key] = tie.Value[0];
                        }
                        else if (hits.Count == 1)
                        {
                            owner[tie.Key] = hits[0];
                        }
                        else
                        {
                            ambiguous.UnionWith(roots);
                        }
                    }

                    // Continuazioni: fotogrammi e fasi della stessa scritta, che comincia dove l'altra finisce
                    Dictionary<int, List<int>> continuations = pieces.ToDictionary(p => p, p => new List<int>());
                    for (int p = 0; p < pieces.Count; p++)
                    {
                        for (int q = p + 1; q < pieces.Count && events[pieces[q]].Line.StartMs <= events[pieces[p]].Line.EndMs; q++)
                        {
                            if (events[pieces[q]].Line.StartMs != events[pieces[p]].Line.EndMs || shownOf[pieces[q]] != shownOf[pieces[p]])
                                continue;
                            continuations[pieces[p]].Add(pieces[q]);
                            continuations[pieces[q]].Add(pieces[p]);
                        }
                    }
                    for (bool changed = true; changed;)
                    {
                        changed = false;
                        foreach (int piece in pieces.Where(p => !owner.ContainsKey(p)))
                        {
                            int followed = continuations[piece].FindIndex(next => owner.TryGetValue(next, out int whole) && ContainsInOrder(fragmentsOf[piece], spaceless[whole]));
                            if (followed < 0)
                                continue;
                            owner[piece] = owner[continuations[piece][followed]];
                            changed = true;
                        }
                    }

                    // Ingressi e uscite staccati dalla riga Comment, senza riga intera né continuazione: la riga Comment più vicina nel
                    // tempo entro la tolleranza delle lettere
                    foreach (int piece in pieces.Where(p => !owner.ContainsKey(p)))
                    {
                        int source = candidates.Where(w => events[w].Comment && Gap(events, w, piece) <= LETTER_EFFECT_TOLERANCE_MS && spaceless[w].Length > shownOf[piece].Length
                            && ContainsInOrder(fragmentsOf[piece], spaceless[w])).OrderBy(w => Gap(events, w, piece)).ThenBy(w => w).DefaultIfEmpty(-1).First();
                        if (source >= 0)
                            owner[piece] = source;
                    }

                    // Pezzi di parola in sequenza (fotogrammi di una parola che si compone): i pezzi posizionati senza un'altra riga
                    // intera che, uno dopo l'altro senza buchi nel tempo, arrivano all'inizio di una riga della combinazione e coi testi
                    // distinti nell'ordine, non tutti di un carattere, ne danno il testo senza spazi vanno con quella riga, anche se non
                    // sono mai a schermo insieme
                    HashSet<int> composed = new HashSet<int>();
                    foreach (int whole in unitLines.Where(w => !owner.ContainsKey(w) && spaceless[w].Length > 1))
                    {
                        List<int> chain = new List<int>();
                        long edge = events[whole].Line.StartMs;
                        List<int> reaching = pieces.FindAll(p => IsFreePiece(events, spaceless, owner, p, whole) && events[p].Line.EndMs >= edge);
                        while (reaching.Count > 0)
                        {
                            chain.AddRange(reaching);
                            edge = Math.Min(edge, reaching.Min(p => events[p].Line.StartMs));
                            reaching = pieces.FindAll(p => !chain.Contains(p) && IsFreePiece(events, spaceless, owner, p, whole) && events[p].Line.EndMs >= edge);
                        }
                        List<string> texts = chain.OrderBy(p => events[p].Line.StartMs).ThenBy(p => p).Select(p => spaceless[p]).Distinct(StringComparer.Ordinal).ToList();
                        if (chain.Count == 0 || string.Concat(texts) != spaceless[whole] || texts.TrueForAll(text => new StringInfo(text).LengthInTextElements == 1))
                            continue;
                        foreach (int p in chain)
                            owner[p] = whole;
                        composed.Add(whole);
                    }

                    // Gruppi: i pezzi con la loro riga intera, e le copie della riga intera
                    Dictionary<int, int> parent = new Dictionary<int, int>();
                    foreach (KeyValuePair<int, int> link in owner)
                        parent[FindGroup(parent, link.Key)] = FindGroup(parent, link.Value);
                    HashSet<int> used = new HashSet<int>(owner.Values);
                    foreach (int whole in candidates.Where(w => used.Contains(w) && !events[w].Comment))
                    {
                        foreach (int copy in unitLines.Where(c => c != whole && spaceless[c] == spaceless[whole] && Touch(events, c, whole) && SameRow(boxesOf, c, whole)))
                            parent[FindGroup(parent, copy)] = FindGroup(parent, whole);
                    }

                    foreach (List<int> members in parent.Keys.ToList().GroupBy(n => FindGroup(parent, n)).Select(g => g.OrderBy(m => events[m].Line.StartMs).ThenBy(m => m).ToList()).ToList())
                    {
                        List<int> phraseLines = members.FindAll(w => members.TrueForAll(m => ContainsInOrder(fragmentsOf[m], spaceless[w])));
                        List<int> drawn = members.FindAll(m => !events[m].Comment);
                        if (phraseLines.Count == 0 || !drawn.Exists(m => shownOf[m] != spaceless[phraseLines[0]])
                            || (!members.Exists(composed.Contains) && !drawn.Exists(a => drawn.Exists(b => SharedTime(events, a, b) > 0 && shownOf[a] != shownOf[b])))
                            || members.Exists(ambiguous.Contains))
                            continue;

                        // Tutti i caratteri della frase mostrati, contando una volta le righe con lo stesso testo mostrato nello stesso punto
                        Dictionary<char, int> missing = spaceless[phraseLines.OrderByDescending(w => spaceless[w].Length).First()].GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
                        foreach (int m in drawn.GroupBy(m => shownOf[m] + "\u0001" + (boxesOf[m] == null ? "" : Math.Round(boxesOf[m][boxesOf[m].Count - 1].X).ToString(CultureInfo.InvariantCulture)
                            + "," + Math.Round(boxesOf[m][boxesOf[m].Count - 1].Y).ToString(CultureInfo.InvariantCulture))).Select(g => g.First()))
                        {
                            foreach (char c in shownOf[m].Where(missing.ContainsKey))
                                missing[c]--;
                        }
                        if (missing.Values.Any(count => count > 0))
                            continue;

                        int phraseLine = phraseLines.OrderByDescending(w => spaceless[w].Length).ThenBy(w => events[w].Comment).ThenByDescending(w => events[w].Line.EndMs - events[w].Line.StartMs)
                            .ThenBy(w => events[w].Layer ?? 0).ThenBy(w => w).First();
                        int principal = phraseLines.Where(w => !events[w].Comment && spaceless[w] == spaceless[phraseLine]).OrderByDescending(w => events[w].Line.EndMs - events[w].Line.StartMs)
                            .ThenBy(w => events[w].Layer ?? 0).ThenBy(w => w).DefaultIfEmpty(phraseLine).First();
                        // Frase da una riga Comment: la riga tradotta prende il posto delle lettere col loro aspetto a riposo e dove stanno
                        // ferme, quindi il gruppo vale solo se in ogni layer le lettere hanno a riposo la stessa visibilità (alpha, bordo e
                        // ombra), un solo allineamento e una sola altezza ferma
                        if (events[principal].Comment)
                        {
                            if (!drawn.GroupBy(m => events[m].Layer).All(layer => cleaner.ReadLetterLook(layer.Select(m => events[m].Raw).ToList(), styleStates.GetValueOrDefault(events[principal].Style)) is SubtitleLetterLook look
                                && look.VisibilityShared && look.Alignment.HasValue && look.PositionY != null))
                                continue;
                            if (!generatedBySource.ContainsKey(principal))
                                generatedBySource[principal] = new List<int>();
                            generatedBySource[principal].AddRange(drawn);
                            generatedBySource[principal].Sort();
                            foreach (int line in drawn)
                                phraseOf[line] = principal;
                            continue;
                        }

                        // Frase da una riga Dialogue: restano le righe intere, cioè le righe col testo della frase che non ne mostrano meno
                        // di un'altra riga sovrapposta con lo stesso testo; gli altri pezzi se ne vanno con la traduzione. Se una riga intera
                        // nasconde una parte della frase, la frase è rivelata a pezzi: ogni riga intera si legge con la frase visibile e
                        // prende la traduzione della primaria, quella col testo pulito con più segni (la prima nel file a parità). Il
                        // gruppo non vale se una riga intera non si può mostrare per intero o ha un testo pulito diverso da quello della
                        // primaria, con o senza segni
                        List<int> wholeLines = drawn.FindAll(m => spaceless[m] == spaceless[principal] && !drawn.Exists(o => o != m && spaceless[o] == spaceless[m]
                            && events[o].Line.StartMs < events[m].Line.EndMs && events[o].Line.EndMs > events[m].Line.StartMs && shownOf[o].Length > shownOf[m].Length));
                        if (!wholeLines.Contains(principal))
                            continue;
                        if (wholeLines.Exists(w => shownOf[w].Length < spaceless[w].Length))
                        {
                            Dictionary<int, string> wholeTexts = wholeLines.ToDictionary(w => w, w => cleaner.ShowWholeLine(events[w].Raw, events[w].Style, styleStates));
                            if (wholeTexts.ContainsValue(null))
                                continue;
                            Dictionary<int, string> cleanTexts = wholeLines.ToDictionary(w => w, w => cleaner.Clean(wholeTexts[w], "").CleanText);
                            int primary = wholeLines.OrderByDescending(w => cleanTexts[w].Count(m => m == SubtitleCueCleaner.MARK_OPEN)).ThenBy(w => w).First();
                            string unmarked = SubtitleCueCleaner.RemoveMarks(cleanTexts[primary]);
                            if (!wholeLines.TrueForAll(w => cleanTexts[w] == cleanTexts[primary] || cleanTexts[w] == unmarked))
                                continue;
                            foreach (int w in wholeLines)
                            {
                                wholeShown[w] = wholeTexts[w];
                                if (w != primary)
                                    copyOf[w] = primary;
                            }
                        }
                        piecesByPhrase[principal] = drawn.Except(wholeLines).ToList();
                        foreach (int piece in piecesByPhrase[principal])
                            phraseOf[piece] = principal;
                    }
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

                    // Le righe Comment senza frase animata restano fuori; le altre righe di una frase animata restano fuori anche dagli
                    // indizi: le righe disegnate da un Comment lasciano il posto alla riga Comment tradotta, i pezzi di una frase da
                    // una riga Dialogue se ne vanno quando la riga principale è tradotta
                    if (assEvent.Comment && !generatedBySource.ContainsKey(e))
                        continue;
                    if (phraseOf[e] >= 0)
                    {
                        rawCues.Add(new RawCue(dialogue.StartMs, dialogue.EndMs, assEvent.TextStart, assEvent.TextLength, style, assEvent.Layer, SubtitleCueKind.Dialogue,
                            SubtitleCue.EXCLUSION_ANIMATED_PIECE, ""));
                        continue;
                    }
                    if (piecesByPhrase.TryGetValue(e, out List<int> attached) && attached.Count > 0)
                        attachedLines[rawCues.Count + 1] = attached.ConvertAll(a => lines[events[a].Line.LineIndex]);
                    if (wholeShown.TryGetValue(e, out string whole))
                    {
                        shownTexts[rawCues.Count + 1] = whole;
                        basePositions[e] = rawCues.Count + 1;
                        foreach (KeyValuePair<int, int> copy in copyOf.Where(c => basePositions.ContainsKey(c.Key) && basePositions.ContainsKey(c.Value)))
                            copyPositions[basePositions[copy.Key]] = basePositions[copy.Value];
                    }

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
                        int marginLColumn = AssSubtitleUtils.ResolveFieldIndex(dialogue.Format, "MarginL");
                        int marginRColumn = AssSubtitleUtils.ResolveFieldIndex(dialogue.Format, "MarginR");
                        List<string> prefixes = new List<string>();
                        foreach (int? letterLayer in layers)
                        {
                            SubtitleLetterLook look = cleaner.ReadLetterLook(generatedLines.FindAll(g => events[g].Layer == letterLayer).ConvertAll(g => events[g].Raw),
                                styleStates.GetValueOrDefault(style));
                            string tags = look.Tags;
                            if (look.Alignment.HasValue && look.PositionY != null && styleLayouts.TryGetValue(style, out AssStyleLayout layout) && layout.Alignment >= 1 && layout.Alignment <= 9
                                && assDocument.TryGetScriptInfoInt("PlayResX", out int playResX))
                            {
                                int marginL = marginLColumn >= 0 && AssSubtitleUtils.TryParseInt(dialogue.Fields[marginLColumn], out int lineMarginL) && lineMarginL != 0 ? lineMarginL : layout.MarginL;
                                int marginR = marginRColumn >= 0 && AssSubtitleUtils.TryParseInt(dialogue.Fields[marginRColumn], out int lineMarginR) && lineMarginR != 0 ? lineMarginR : layout.MarginR;
                                int column = (layout.Alignment - 1) % 3;
                                double x = SubtitleCueCleaner.AnchorX(column, marginL, marginR, playResX);
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
                        intervals.Add(new List<ShownInterval>());
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
                            shown[shownKey] = new List<ShownInterval>();
                        ShownInterval counted = shown[shownKey].Find(t => (t.StartMs == dialogue.StartMs && t.EndMs == dialogue.EndMs) || t.EndMs == dialogue.StartMs);
                        continued = counted != null;
                        if (continued)
                        {
                            counted.EndMs = Math.Max(counted.EndMs, dialogue.EndMs);
                        }
                        else
                        {
                            counted = new ShownInterval(dialogue.StartMs, dialogue.EndMs);
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

                    // Si esclude solo il testo da non tradurre: la combinazione da non tradurre e la riga di un solo carattere,
                    // qualunque sia la sua combinazione
                    TranslationStyleKind? styleKind;
                    kinds.TryGetValue(style + "\u0001" + actor + "\u0001" + effect, out styleKind);
                    string exclusion = styleKind == TranslationStyleKind.NotTranslatable || fragment ? SubtitleCue.EXCLUSION_NOT_TRANSLATABLE : "";
                    SubtitleCueKind kind = ToCueKind(styleKind);
                    rawCues.Add(new RawCue(dialogue.StartMs, dialogue.EndMs, assEvent.TextStart, assEvent.TextLength, style, assEvent.Layer, kind, exclusion, ""));
                }

                // Righe di ogni combinazione sovrapposte nel tempo: una riga si sovrappone se comincia prima che finisca una riga
                // cominciata prima, o se la seguente comincia prima che lei finisca; il massimo sullo schermo conta le fini prima
                // degli inizi allo stesso istante
                for (int u = 0; u < usages.Count; u++)
                {
                    List<ShownInterval> times = intervals[u];
                    times.Sort((a, b) => a.StartMs != b.StartMs ? a.StartMs.CompareTo(b.StartMs) : a.EndMs.CompareTo(b.EndMs));
                    long reach = long.MinValue;
                    for (int t = 0; t < times.Count; t++)
                    {
                        if (times[t].StartMs < reach || (t + 1 < times.Count && times[t + 1].StartMs < times[t].EndMs))
                            usages[u].OverlappingLines++;
                        reach = Math.Max(reach, times[t].EndMs);
                    }

                    List<KeyValuePair<long, int>> steps = new List<KeyValuePair<long, int>>();
                    foreach (ShownInterval time in times)
                    {
                        steps.Add(new KeyValuePair<long, int>(time.StartMs, 1));
                        steps.Add(new KeyValuePair<long, int>(time.EndMs, -1));
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
                return new SubtitleTranslationDocument(format, text, preamble, outputEncoding, new List<SubtitleCue>(), usages, letterEffects, attachedLines, new HashSet<int>(shownTexts.Keys), REJECTION_NO_CUES);

            // Pulizia
            List<SubtitleCleanResult> cleanedCues = new List<SubtitleCleanResult>();
            for (int i = 0; i < rawCues.Count; i++)
            {
                RawCue raw = rawCues[i];
                SubtitleCleanResult cleaned = !string.IsNullOrEmpty(raw.ExclusionReason)
                    ? new SubtitleCleanResult("", null, raw.ExclusionReason)
                    : cleaner.Clean(shownTexts.GetValueOrDefault(i + 1, text.Substring(raw.TextStart, raw.TextLength)), raw.FallbackLineBreak);

                // Riga SRT di un solo carattere: da non tradurre, come quella ASS
                if (string.IsNullOrEmpty(cleaned.ExclusionReason) && new StringInfo(cleaned.CleanText).LengthInTextElements == 1)
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
                                srtKinds[timed[r]] = ToCueKind(lineKind);
                                if (lineKind == TranslationStyleKind.NotTranslatable)
                                    cleanedCues[timed[r]] = new SubtitleCleanResult("", null, SubtitleCue.EXCLUSION_NOT_TRANSLATABLE);
                            }
                        }
                    }
                }
            }

            // Id progressivi sulle traducibili nell'ordine di lettura (inizio, a pari inizio ordine nel file) e duplicati: le righe su
            // layer diversi con gli stessi tempi e lo stesso testo pulito, e le righe base di una frase rivelata a pezzi, copie della
            // primaria anche quando questa viene dopo nel file
            int?[] ids = new int?[rawCues.Count];
            int?[] duplicates = new int?[rawCues.Count];
            Dictionary<string, int[]> firstByContent = new Dictionary<string, int[]>(StringComparer.Ordinal);
            List<int> reading = Enumerable.Range(0, rawCues.Count).ToList();
            reading.Sort((a, b) => rawCues[a].StartMs != rawCues[b].StartMs ? rawCues[a].StartMs.CompareTo(rawCues[b].StartMs) : a.CompareTo(b));
            int nextId = 1;
            foreach (int i in reading)
            {
                RawCue raw = rawCues[i];
                string key = raw.StartMs.ToString(CultureInfo.InvariantCulture) + "|" + raw.EndMs.ToString(CultureInfo.InvariantCulture) + "|" + cleanedCues[i].CleanText;
                if (!string.IsNullOrEmpty(cleanedCues[i].ExclusionReason) || copyPositions.ContainsKey(i + 1))
                    continue;
                if (raw.Layer.HasValue && firstByContent.TryGetValue(key, out int[] original) && original[1] != raw.Layer.Value)
                {
                    duplicates[i] = original[0];
                }
                else
                {
                    ids[i] = nextId++;
                    if (raw.Layer.HasValue)
                        firstByContent.TryAdd(key, new int[] { ids[i].Value, raw.Layer.Value });
                }
            }
            foreach (KeyValuePair<int, int> copy in copyPositions)
            {
                if (string.IsNullOrEmpty(cleanedCues[copy.Key - 1].ExclusionReason))
                    duplicates[copy.Key - 1] = ids[copy.Value - 1] ?? duplicates[copy.Value - 1];
            }

            List<SubtitleCue> cues = new List<SubtitleCue>();
            for (int i = 0; i < rawCues.Count; i++)
            {
                RawCue raw = rawCues[i];
                SubtitleCleanResult cleaned = cleanedCues[i];
                int? id = ids[i];
                int? duplicateOf = duplicates[i];

                SubtitleCueKind kind = format == SubtitleTranslationFormat.Srt ? srtKinds[i] : raw.Kind;
                if (format == SubtitleTranslationFormat.Srt && cleaned.CleanText.IndexOfAny(s_songMarks) >= 0)
                    kind = SubtitleCueKind.Song;
                SubtitleCue cue = new SubtitleCue(id, i + 1, raw.StartMs, raw.EndMs, cleaned.CleanText, cleaned.Recipe, raw.Style, kind, duplicateOf,
                    raw.TextStart, raw.TextLength);
                cues.Add(cue);
            }

            return new SubtitleTranslationDocument(format, text, preamble, outputEncoding, cues, usages, letterEffects, attachedLines, new HashSet<int>(shownTexts.Keys), "");
        }

        /// <summary>
        /// Scrive il file sostituendo i soli campi Text delle battute tradotte e delle loro copie (su altri layer, o altre righe di una
        /// frase rivelata a pezzi); di una frase animata con la riga Comment tradotta la riga Comment resta com'è e una riga Dialogue
        /// col testo tradotto per layer delle lettere prende il posto delle righe disegnate; di una frase animata con la riga Dialogue
        /// tradotta i pezzi se ne vanno e la riga è scritta con la frase intera visibile. Una traduzione uguale al testo pulito del
        /// sorgente, a meno degli spazi e degli a capo come nelle tratte ripetute, lascia i pezzi come sono
        /// </summary>
        /// <param name="translatedText">Testo pulito tradotto per id, già validato</param>
        /// <returns>Byte del file, con lo stesso BOM e la stessa codifica del sorgente (UTF-8 per una codifica scelta)</returns>
        public byte[] Write(IReadOnlyDictionary<int, string> translatedText)
        {
            SubtitleCueCleaner cleaner = new SubtitleCueCleaner(this.Format);
            List<TextEdit> edits = new List<TextEdit>();
            Dictionary<int, string> sourceTexts = this._cues.Where(c => c.Id.HasValue).ToDictionary(c => c.Id.Value, c => c.CleanText);
            foreach (SubtitleCue cue in this._cues)
            {
                int? source = cue.Id ?? cue.DuplicateOf;
                if (!source.HasValue || !translatedText.TryGetValue(source.Value, out string translation))
                    continue;

                // Un testo uguale al pulito del sorgente, a meno di spazi e a capo come la chiave delle tratte ripetute, lascia
                // i pezzi delle frasi animate come sono in ogni file della tratta; per una copia si decide sulla battuta
                // di cui è copia, così tutte le righe di una frase restano o cambiano insieme. Una copia senza segni di una battuta che
                // li ha ne prende la traduzione senza segni
                bool unchanged = SharedStretchDetector.Key(this.Format, translation ?? "") == SharedStretchDetector.Key(this.Format, sourceTexts[source.Value]);
                if (translation != null && cue.DuplicateOf.HasValue && cue.CleanText.IndexOf(SubtitleCueCleaner.MARK_OPEN) < 0)
                    translation = SubtitleCueCleaner.RemoveMarks(translation);
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

        #region Metodi privati

        /// <summary>
        /// Documento di un file rifiutato, senza testo né battute
        /// </summary>
        /// <param name="format">Formato del file</param>
        /// <param name="reason">Motivo del rifiuto</param>
        /// <returns>Documento rifiutato</returns>
        private static SubtitleTranslationDocument Rejected(SubtitleTranslationFormat format, string reason)
        {
            return new SubtitleTranslationDocument(format, "", new byte[0], null, new List<SubtitleCue>(), new List<SubtitleStyleUsage>(), new Dictionary<int, LetterEffect>(),
                new Dictionary<int, List<SourceLine>>(), new HashSet<int>(), reason);
        }

        /// <summary>
        /// Indica se due righe stanno sulla stessa riga di testo a schermo: riquadri sovrapposti in verticale
        /// </summary>
        /// <param name="boxesOf">Riquadri di ogni riga, null se lo stile manca</param>
        /// <param name="a">Prima riga</param>
        /// <param name="b">Seconda riga</param>
        /// <returns>True sulla stessa riga a schermo</returns>
        private static bool SameRow(List<AssScreenBox>[] boxesOf, int a, int b)
        {
            return boxesOf[a] != null && boxesOf[b] != null
                && boxesOf[a].Exists(boxA => boxesOf[b].Exists(boxB => Math.Min(boxA.Bottom, boxB.Bottom) - Math.Max(boxA.Top, boxB.Top) > 0));
        }

        /// <summary>
        /// Indica se i tempi di due righe si toccano o si sovrappongono
        /// </summary>
        /// <param name="events">Righe di [Events]</param>
        /// <param name="a">Prima riga</param>
        /// <param name="b">Seconda riga</param>
        /// <returns>True se i tempi si toccano</returns>
        private static bool Touch(List<AssEvent> events, int a, int b)
        {
            return events[a].Line.StartMs <= events[b].Line.EndMs && events[b].Line.StartMs <= events[a].Line.EndMs;
        }

        /// <summary>
        /// Tempo in comune di due righe
        /// </summary>
        /// <param name="events">Righe di [Events]</param>
        /// <param name="a">Prima riga</param>
        /// <param name="b">Seconda riga</param>
        /// <returns>Millisecondi in comune, 0 se non si sovrappongono</returns>
        private static long SharedTime(List<AssEvent> events, int a, int b)
        {
            return Math.Max(0, Math.Min(events[a].Line.EndMs, events[b].Line.EndMs) - Math.Max(events[a].Line.StartMs, events[b].Line.StartMs));
        }

        /// <summary>
        /// Indica se un testo contiene i tratti nell'ordine
        /// </summary>
        /// <param name="fragments">Tratti</param>
        /// <param name="whole">Testo</param>
        /// <returns>True se ogni tratto segue il precedente</returns>
        private static bool ContainsInOrder(List<string> fragments, string whole)
        {
            int at = 0;
            foreach (string fragment in fragments)
            {
                int found = whole.IndexOf(fragment, at, StringComparison.Ordinal);
                if (found < 0)
                    return false;
                at = found + fragment.Length;
            }
            return true;
        }

        /// <summary>
        /// Riga intera a cui arriva un candidato seguendo i pezzi già assegnati
        /// </summary>
        /// <param name="firm">Riga intera di ogni pezzo assegnato senza pareggio</param>
        /// <param name="w">Candidato</param>
        /// <returns>Riga intera in fondo alla catena</returns>
        private static int RootOf(Dictionary<int, int> firm, int w)
        {
            HashSet<int> seen = new HashSet<int>();
            while (firm.TryGetValue(w, out int next) && seen.Add(w))
                w = next;
            return w;
        }

        /// <summary>
        /// Distanza nel tempo fra una riga Comment e un pezzo
        /// </summary>
        /// <param name="events">Righe di [Events]</param>
        /// <param name="w">Riga Comment</param>
        /// <param name="piece">Pezzo</param>
        /// <returns>Millisecondi fra le due righe, 0 se si toccano</returns>
        private static long Gap(List<AssEvent> events, int w, int piece)
        {
            return Math.Max(0, Math.Max(events[w].Line.StartMs - events[piece].Line.EndMs, events[piece].Line.StartMs - events[w].Line.EndMs));
        }

        /// <summary>
        /// Indica se un pezzo posizionato può comporre una riga: senza un'altra riga intera, finito prima che la riga cominci e con
        /// un testo diverso
        /// </summary>
        /// <param name="events">Righe di [Events]</param>
        /// <param name="spaceless">Testo senza spazi di ogni riga</param>
        /// <param name="owner">Riga intera di ogni pezzo assegnato</param>
        /// <param name="p">Pezzo</param>
        /// <param name="whole">Riga da comporre</param>
        /// <returns>True se il pezzo è libero per la riga</returns>
        private static bool IsFreePiece(List<AssEvent> events, string[] spaceless, Dictionary<int, int> owner, int p, int whole)
        {
            return p != whole && (!owner.TryGetValue(p, out int current) || current == whole) && s_position.IsMatch(events[p].Raw)
                && events[p].Line.StartMs < events[whole].Line.StartMs && events[p].Line.EndMs <= events[whole].Line.StartMs && spaceless[p] != spaceless[whole];
        }

        /// <summary>
        /// Gruppo di una riga, con la compressione dei cammini
        /// </summary>
        /// <param name="parent">Riga di riferimento di ogni riga, aggiornata</param>
        /// <param name="n">Riga</param>
        /// <returns>Riga che rappresenta il gruppo</returns>
        private static int FindGroup(Dictionary<int, int> parent, int n)
        {
            parent.TryAdd(n, n);
            while (parent[n] != n)
                n = parent[n] = parent[parent[n]];
            return n;
        }

        /// <summary>
        /// Tipo della battuta dal tipo riconosciuto: canzone e scritta restano tali, il resto è dialogo
        /// </summary>
        /// <param name="kind">Tipo riconosciuto, null senza</param>
        /// <returns>Tipo della battuta</returns>
        private static SubtitleCueKind ToCueKind(TranslationStyleKind? kind)
        {
            return kind == TranslationStyleKind.Song ? SubtitleCueKind.Song : kind == TranslationStyleKind.Sign ? SubtitleCueKind.Sign : SubtitleCueKind.Dialogue;
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
        /// Tempi di una riga contata negli indizi di una combinazione, allungati dalle righe che la continuano
        /// </summary>
        private sealed class ShownInterval
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="startMs">Inizio in millisecondi</param>
            /// <param name="endMs">Fine in millisecondi</param>
            public ShownInterval(long startMs, long endMs)
            {
                this.StartMs = startMs;
                this.EndMs = endMs;
            }

            /// <summary>
            /// Inizio in millisecondi
            /// </summary>
            public long StartMs { get; }

            /// <summary>
            /// Fine in millisecondi, fino alla fine dell'ultima riga che la continua
            /// </summary>
            public long EndMs { get; set; }
        }

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
        /// Riga Comment di una frase animata
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
