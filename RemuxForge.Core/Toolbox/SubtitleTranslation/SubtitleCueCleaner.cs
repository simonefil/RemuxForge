using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Pulizia del campo Text di una battuta per il modello e ricomposizione nel formato d'origine. Il modello vede solo il testo
    /// leggibile, gli a capo come \n e i tratti formattati a metà frase come ⟦n testo⟧; tag di riga, effetti, entità e caratteri
    /// riservati restano nella ricetta. Ricomporre il testo pulito non tradotto restituisce il campo Text originale.
    /// I blocchi a metà frase sono di due specie. Un blocco su un confine di parola che cambia la formattazione, o che anima il
    /// testo seguente con \t, è semantico: diventa un segno e il modello lo mette attorno alle parole tradotte. Un blocco dentro
    /// una parola (sfumature e animazioni lettera per lettera), un karaoke o un tratto formattato di soli spazi sono posizionali:
    /// la battuta va al modello senza segni e l'harness ridistribuisce il testo tradotto sui pezzi del sorgente in proporzione ai
    /// caratteri, con tutti i blocchi originali al loro posto nella sequenza (durate del karaoke e loro somma comprese)
    /// </summary>
    public class SubtitleCueCleaner
    {
        #region Costanti

        /// <summary>
        /// Apertura del segno di un tratto formattato
        /// </summary>
        public const char MARK_OPEN = '⟦';

        /// <summary>
        /// Chiusura del segno di un tratto formattato
        /// </summary>
        public const char MARK_CLOSE = '⟧';

        /// <summary>
        /// Spazio unificatore, forma pulita di \h
        /// </summary>
        private const char HARD_SPACE = '\u00A0';

        /// <summary>
        /// Gettone ASS dello spazio unificatore
        /// </summary>
        private const string ASS_HARD_SPACE = "\\h";

        /// <summary>
        /// Gettone ASS dell'a capo forzato
        /// </summary>
        private const string ASS_HARD_BREAK = "\\N";

        /// <summary>
        /// Gettone ASS dell'a capo morbido
        /// </summary>
        private const string ASS_SOFT_BREAK = "\\n";

        /// <summary>
        /// Canale dello stile scelto con \rNome
        /// </summary>
        private const string ASS_STYLE_CHANNEL = "r";

        /// <summary>
        /// Tag ASS che anima il testo seguente fino a fine riga o a \r
        /// </summary>
        private const string ASS_TRANSFORM = "t";

        /// <summary>
        /// Prefisso del canale di stato di un \t: uno per tag, perché un'animazione non si annulla e non si sostituisce
        /// </summary>
        private const string TRANSFORM_CHANNEL = "t#";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Tag HTML che gli SRT interpretano, ancorato alla posizione di ricerca
        /// </summary>
        private static readonly Regex s_srtTag = new Regex(@"\G<(?<close>/?)(?<name>i|b|u|font)(?:\s[^>]*)?>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// Entità SRT riconosciute, ancorate alla posizione di ricerca
        /// </summary>
        private static readonly Regex s_srtEntity = new Regex(@"\G&(?:(?<named>amp|lt|gt|quot|apos|nbsp)|#(?<dec>[0-9]{1,7})|#[xX](?<hex>[0-9a-fA-F]{1,6}));", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Nomi dei tag ASS, i più lunghi prima perché un nome può cominciare con un altro (\bord e \b, \pos e \p)
        /// </summary>
        private static readonly string[] s_assTagNames = new string[]
        {
            "alpha", "iclip", "xbord", "ybord", "xshad", "yshad", "bord", "shad", "blur", "clip", "fade", "fscx", "fscy", "move",
            "pos", "org", "fad", "fax", "fay", "fsp", "frx", "fry", "frz", "pbo", "an", "fn", "fs", "fr", "fe", "be", "kf", "ko",
            "1c", "2c", "3c", "4c", "1a", "2a", "3a", "4a", "a", "b", "c", "i", "k", "K", "p", "q", "r", "s", "t", "u"
        };

        /// <summary>
        /// Tag ASS di stato in linea e canali che impostano: lo stato resta finché lo stesso canale non cambia, o \r lo azzera
        /// </summary>
        private static readonly Dictionary<string, string[]> s_assStateChannels = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "c", new string[] { "1c" } }, { "1c", new string[] { "1c" } }, { "2c", new string[] { "2c" } }, { "3c", new string[] { "3c" } },
            { "4c", new string[] { "4c" } }, { "alpha", new string[] { "1a", "2a", "3a", "4a" } }, { "1a", new string[] { "1a" } },
            { "2a", new string[] { "2a" } }, { "3a", new string[] { "3a" } }, { "4a", new string[] { "4a" } }, { "fn", new string[] { "fn" } },
            { "fs", new string[] { "fs" } }, { "fscx", new string[] { "fscx" } }, { "fscy", new string[] { "fscy" } }, { "fsp", new string[] { "fsp" } },
            { "frx", new string[] { "frx" } }, { "fry", new string[] { "fry" } }, { "frz", new string[] { "frz" } }, { "fr", new string[] { "frz" } },
            { "fax", new string[] { "fax" } }, { "fay", new string[] { "fay" } }, { "fe", new string[] { "fe" } },
            { "bord", new string[] { "xbord", "ybord" } }, { "xbord", new string[] { "xbord" } }, { "ybord", new string[] { "ybord" } },
            { "shad", new string[] { "xshad", "yshad" } }, { "xshad", new string[] { "xshad" } }, { "yshad", new string[] { "yshad" } },
            { "blur", new string[] { "blur" } }, { "be", new string[] { "be" } }
        };

        /// <summary>
        /// Tag ASS del karaoke
        /// </summary>
        private static readonly HashSet<string> s_assKaraokeTags = new HashSet<string>(StringComparer.Ordinal) { "k", "K", "kf", "ko" };

        /// <summary>
        /// Tag ASS animati: nel prefisso valgono per tutta la riga, a metà frase fanno una scritta a comparsa
        /// </summary>
        private static readonly HashSet<string> s_assTimedTags = new HashSet<string>(StringComparer.Ordinal) { "t", "move", "fad", "fade" };

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Formato delle battute
        /// </summary>
        private readonly SubtitleTranslationFormat _format;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="format">Formato delle battute</param>
        public SubtitleCueCleaner(SubtitleTranslationFormat format)
        {
            this._format = format;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Pulisce il campo Text di una battuta
        /// </summary>
        /// <param name="text">Campo Text originale; in SRT le righe con i loro terminatori</param>
        /// <param name="fallbackLineBreak">A capo SRT per la traduzione di una battuta su una riga: il terminatore della riga dei tempi</param>
        /// <param name="legacy">True nei progetti senza tabella degli stili, che restano come quando sono stati tradotti: karaoke,
        /// animazioni a metà frase, blocchi ASS a metà di un SRT e tratti di soli spazi escludono la riga, e i blocchi dentro le
        /// parole fanno segni</param>
        /// <returns>Testo pulito con la ricetta, oppure il motivo di esclusione</returns>
        public SubtitleCleanResult Clean(string text, string fallbackLineBreak, bool legacy)
        {
            string source = text ?? "";
            bool isSrt = this._format == SubtitleTranslationFormat.Srt;
            List<CleanItem> items = new List<CleanItem>();
            Dictionary<char, string> entities = new Dictionary<char, string>();
            HashSet<char> literalChars = new HashSet<char>();
            bool mixedEntities = false;
            int position = 0;

            // Gettoni del campo Text: tag, a capo e caratteri; in ASS un blocco senza \ è un commento
            while (position < source.Length)
            {
                char current = source[position];
                char next = position + 1 < source.Length ? source[position + 1] : '\0';
                if (isSrt)
                {
                    if (current == '\r' || current == '\n')
                    {
                        int length = current == '\r' && next == '\n' ? 2 : 1;
                        items.Add(new CleanItem(CleanItemKind.Break, source.Substring(position, length), '\n'));
                        position += length;
                        continue;
                    }

                    if (current == '<')
                    {
                        Match tag = s_srtTag.Match(source, position);
                        if (tag.Success)
                        {
                            CleanItem tagItem = new CleanItem(CleanItemKind.Tag, tag.Value, '\0');
                            tagItem.HtmlName = tag.Groups["name"].Value.ToLowerInvariant();
                            tagItem.HtmlClose = !string.IsNullOrEmpty(tag.Groups["close"].Value);
                            items.Add(tagItem);
                            position += tag.Length;
                            continue;
                        }
                    }

                    // Le graffe sono tag solo nella forma {\…}
                    if (current == '{' && next == '\\')
                    {
                        int close = source.IndexOf('}', position);
                        if (close > position)
                        {
                            CleanItem blockItem = new CleanItem(CleanItemKind.Tag, source.Substring(position, close - position + 1), '\0');
                            blockItem.IsSrtAssBlock = true;
                            items.Add(blockItem);
                            position = close + 1;
                            continue;
                        }
                    }

                    if (current == '&')
                    {
                        Match entity = s_srtEntity.Match(source, position);
                        int code = -1;
                        if (entity.Success)
                        {
                            if (entity.Groups["named"].Success)
                            {
                                switch (entity.Groups["named"].Value)
                                {
                                    case "amp": code = '&'; break;
                                    case "lt": code = '<'; break;
                                    case "gt": code = '>'; break;
                                    case "quot": code = '"'; break;
                                    case "apos": code = '\''; break;
                                    default: code = HARD_SPACE; break;
                                }
                            }
                            else if (entity.Groups["dec"].Success)
                            {
                                code = int.Parse(entity.Groups["dec"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                            }
                            else
                            {
                                code = int.Parse(entity.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            }
                        }

                        // Solo caratteri singoli del piano base: un'entità fuori da qui resta testo
                        if (code > 0 && code <= 0xFFFF && (code < 0xD800 || code > 0xDFFF) && code != '\t' && code != '\r' && code != '\n')
                        {
                            char decoded = (char)code;
                            if (entities.TryGetValue(decoded, out string known) && known != entity.Value)
                                mixedEntities = true;
                            entities[decoded] = entity.Value;
                            items.Add(new CleanItem(CleanItemKind.Char, entity.Value, decoded));
                            position += entity.Length;
                            continue;
                        }
                    }
                }
                else
                {
                    if (current == '{')
                    {
                        int close = source.IndexOf('}', position);
                        if (close < 0)
                            return new SubtitleCleanResult("", null, SubtitleCue.EXCLUSION_UNCLOSED_BRACE);

                        string raw = source.Substring(position, close - position + 1);
                        CleanItem blockItem = new CleanItem(CleanItemKind.Tag, raw, '\0');
                        blockItem.AssTags = new List<AssTag>();
                        int tagStart = raw.IndexOf('\\');

                        // Ogni tag va da una \ alla successiva; gli argomenti fra parentesi possono contenere altre \ (\t)
                        while (tagStart >= 0 && tagStart < raw.Length - 1)
                        {
                            int nameStart = tagStart + 1;
                            string name = "";
                            for (int n = 0; n < s_assTagNames.Length; n++)
                            {
                                if (string.CompareOrdinal(raw, nameStart, s_assTagNames[n], 0, s_assTagNames[n].Length) == 0)
                                {
                                    name = s_assTagNames[n];
                                    break;
                                }
                            }
                            if (string.IsNullOrEmpty(name))
                            {
                                int letters = nameStart;
                                while (letters < raw.Length - 1 && char.IsLetter(raw[letters]))
                                    letters++;
                                name = raw.Substring(nameStart, letters - nameStart);
                            }

                            int argStart = nameStart + name.Length;
                            int argEnd = argStart;
                            if (argEnd < raw.Length - 1 && raw[argEnd] == '(')
                            {
                                int depth = 0;
                                while (argEnd < raw.Length - 1)
                                {
                                    if (raw[argEnd] == '(')
                                    {
                                        depth++;
                                    }
                                    else if (raw[argEnd] == ')')
                                    {
                                        depth--;
                                    }
                                    argEnd++;
                                    if (depth == 0)
                                        break;
                                }
                            }
                            else
                            {
                                while (argEnd < raw.Length - 1 && raw[argEnd] != '\\')
                                    argEnd++;
                            }

                            blockItem.AssTags.Add(new AssTag(name, raw.Substring(argStart, argEnd - argStart), raw.Substring(tagStart, argEnd - tagStart)));
                            tagStart = raw.IndexOf('\\', argEnd);
                        }

                        items.Add(blockItem);
                        position = close + 1;
                        continue;
                    }

                    if (current == '\\' && (next == 'N' || next == 'n'))
                    {
                        items.Add(new CleanItem(CleanItemKind.Break, source.Substring(position, 2), '\n'));
                        position += 2;
                        continue;
                    }

                    if (current == '\\' && next == 'h')
                    {
                        items.Add(new CleanItem(CleanItemKind.Char, ASS_HARD_SPACE, HARD_SPACE));
                        position += 2;
                        continue;
                    }
                }

                // Carattere di testo; un tab diventa spazio, così un record non ha mai un tab dentro il testo
                items.Add(new CleanItem(CleanItemKind.Char, current.ToString(), current == '\t' ? ' ' : current));
                literalChars.Add(current);
                position++;
            }

            foreach (char entityChar in entities.Keys)
            {
                if (literalChars.Contains(entityChar))
                    mixedEntities = true;
            }

            // Primo e ultimo carattere visibile: prima e dopo stanno prefisso e suffisso
            int first = -1;
            int last = -1;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Kind == CleanItemKind.Char && !char.IsWhiteSpace(items[i].Clean))
                {
                    if (first < 0)
                        first = i;
                    last = i;
                }
            }

            // Esclusioni che dipendono dai tag
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].AssTags == null)
                    continue;
                foreach (AssTag tag in items[i].AssTags)
                {
                    if (tag.Name == "pbo" || (tag.Name == "p" && int.TryParse(tag.Arg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int drawingScale) && drawingScale > 0))
                        return new SubtitleCleanResult("", null, SubtitleCue.EXCLUSION_DRAWING);
                }
            }
            for (int i = 0; legacy && i < items.Count; i++)
            {
                if (items[i].AssTags == null)
                    continue;
                foreach (AssTag tag in items[i].AssTags)
                {
                    if (s_assKaraokeTags.Contains(tag.Name))
                        return new SubtitleCleanResult("", null, SubtitleCue.EXCLUSION_KARAOKE);
                }
            }
            if (first < 0)
                return new SubtitleCleanResult("", null, SubtitleCue.EXCLUSION_EMPTY);
            for (int i = first + 1; legacy && i < last; i++)
            {
                if (items[i].AssTags == null)
                    continue;
                foreach (AssTag tag in items[i].AssTags)
                {
                    if (s_assTimedTags.Contains(tag.Name))
                        return new SubtitleCleanResult("", null, SubtitleCue.EXCLUSION_TIMED_EFFECT);
                }
            }
            for (int i = first + 1; legacy && i < items.Count; i++)
            {
                if (items[i].IsSrtAssBlock)
                    return new SubtitleCleanResult("", null, SubtitleCue.EXCLUSION_SRT_INLINE_ASS_TAG);
            }
            if (mixedEntities)
                return new SubtitleCleanResult("", null, SubtitleCue.EXCLUSION_MIXED_ENTITIES);

            // Battuta posizionale: un blocco a metà frase col karaoke, dentro una parola o, in SRT, un blocco ASS
            bool segmented = false;
            for (int i = first + 1; !legacy && i < last; i++)
            {
                if (items[i].Kind != CleanItemKind.Tag)
                    continue;
                int before = i - 1;
                while (items[before].Kind == CleanItemKind.Tag)
                    before--;
                int after = i + 1;
                while (items[after].Kind == CleanItemKind.Tag)
                    after++;
                segmented |= items[i].IsSrtAssBlock || (items[i].AssTags != null && items[i].AssTags.Exists(tag => s_assKaraokeTags.Contains(tag.Name)))
                    || (items[before].Kind == CleanItemKind.Char && items[after].Kind == CleanItemKind.Char && char.IsLetterOrDigit(items[before].Clean)
                    && char.IsLetterOrDigit(items[after].Clean));
            }

            // A capo della traduzione: in SRT il primo terminatore interno, in ASS/SSA \N, o \n se la battuta usa solo quello
            string lineBreakToken = isSrt ? fallbackLineBreak ?? "\n" : ASS_HARD_BREAK;
            bool hardBreak = false;
            bool softBreak = false;
            foreach (CleanItem item in items)
            {
                if (item.Kind != CleanItemKind.Break)
                    continue;
                if (isSrt && !hardBreak && !softBreak)
                    lineBreakToken = item.Raw;
                hardBreak |= item.Raw == ASS_HARD_BREAK;
                softBreak |= item.Raw != ASS_HARD_BREAK;
            }
            if (!isSrt && softBreak && !hardBreak)
                lineBreakToken = ASS_SOFT_BREAK;

            // Stato della formattazione dopo ogni gettone. SRT: pila dei tag aperti. ASS: interruttori \i \b \u \s e canali di
            // stato (\c, \fs, \rNome…) che restano attraverso i blocchi; la chiave tiene solo i canali diversi dal valore attivo
            // al primo carattere visibile, che vale per tutta la riga
            string initialKey = isSrt ? "" : "0|";
            List<SrtEntry> stack = new List<SrtEntry>();
            int switches = 0;
            int[] switchOnBy = new int[] { -1, -1, -1, -1 };
            Dictionary<string, AssChannel> channels = new Dictionary<string, AssChannel>(StringComparer.Ordinal);
            Dictionary<string, AssChannel> baseChannels = null;
            int tagOrder = 0;
            for (int i = 0; i < items.Count; i++)
            {
                CleanItem item = items[i];
                if (item.HtmlName != null)
                {
                    if (!item.HtmlClose)
                    {
                        stack.Add(new SrtEntry(item.HtmlName, item.Raw, i));
                    }
                    else
                    {
                        int open = stack.FindLastIndex(entry => entry.Name == item.HtmlName);
                        if (open >= 0)
                            stack.RemoveRange(open, stack.Count - open);
                    }
                }
                else if (item.AssTags != null)
                {
                    foreach (AssTag tag in item.AssTags)
                    {
                        int bit = this.SwitchBit(tag.Name);
                        tagOrder++;
                        if (bit >= 0)
                        {
                            if (int.TryParse(tag.Arg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value != 0)
                            {
                                switches |= 1 << bit;
                                switchOnBy[bit] = i;
                            }
                            else
                            {
                                switches &= ~(1 << bit);
                            }
                        }
                        else if (tag.Name == ASS_STYLE_CHANNEL)
                        {
                            switches = 0;
                            channels.Clear();
                            if (!string.IsNullOrEmpty(tag.Arg.Trim()))
                                channels[ASS_STYLE_CHANNEL] = new AssChannel(tag, tagOrder);
                        }
                        else if (!legacy && tag.Name == ASS_TRANSFORM)
                        {
                            channels[TRANSFORM_CHANNEL + tagOrder.ToString(CultureInfo.InvariantCulture)] = new AssChannel(tag, tagOrder);
                        }
                        else if (s_assStateChannels.TryGetValue(tag.Name, out string[] tagChannels))
                        {
                            // Senza argomento il tag riporta il canale allo stile
                            foreach (string channel in tagChannels)
                            {
                                if (string.IsNullOrEmpty(tag.Arg.Trim()))
                                {
                                    channels.Remove(channel);
                                }
                                else
                                {
                                    channels[channel] = new AssChannel(tag, tagOrder);
                                }
                            }
                        }
                    }
                }
                if (i == first)
                    baseChannels = new Dictionary<string, AssChannel>(channels, StringComparer.Ordinal);

                item.Stack = new List<SrtEntry>(stack);
                item.Switches = switches;
                item.SwitchOnBy = (int[])switchOnBy.Clone();
                item.Channels = new Dictionary<string, AssChannel>(channels, StringComparer.Ordinal);
                if (isSrt)
                {
                    StringBuilder key = new StringBuilder();
                    foreach (SrtEntry entry in stack)
                        key.Append(entry.Raw).Append('\u0001');
                    item.StateKey = key.ToString();
                }
                else
                {
                    StringBuilder key = new StringBuilder(switches.ToString(CultureInfo.InvariantCulture)).Append('|');
                    SortedSet<string> names = new SortedSet<string>(channels.Keys, StringComparer.Ordinal);
                    names.UnionWith(baseChannels != null ? baseChannels.Keys : channels.Keys);
                    foreach (string name in names)
                    {
                        string value = channels.TryGetValue(name, out AssChannel current) ? current.Tag.Arg.Trim() : null;
                        string baseValue = value;
                        if (baseChannels != null)
                            baseValue = baseChannels.TryGetValue(name, out AssChannel initial) ? initial.Tag.Arg.Trim() : null;
                        if (value != baseValue)
                            key.Append(name).Append('=').Append(value).Append('\u0001');
                    }
                    item.StateKey = key.ToString();
                }
            }

            // Formattazione di base: quella comune a tutti i caratteri fra il primo e l'ultimo visibile; resta in prefisso e suffisso
            List<SrtEntry> baseStack = null;
            int baseSwitches = -1;
            for (int i = first; i <= last; i++)
            {
                if (items[i].Kind != CleanItemKind.Char)
                    continue;
                if (baseStack == null)
                {
                    baseStack = new List<SrtEntry>(items[i].Stack);
                    baseSwitches = items[i].Switches;
                    continue;
                }

                int common = 0;
                while (common < baseStack.Count && common < items[i].Stack.Count && baseStack[common].Raw == items[i].Stack[common].Raw)
                    common++;
                baseStack.RemoveRange(common, baseStack.Count - common);
                baseSwitches &= items[i].Switches;
            }
            string baseKey;
            if (isSrt)
            {
                StringBuilder key = new StringBuilder();
                foreach (SrtEntry entry in baseStack)
                    key.Append(entry.Raw).Append('\u0001');
                baseKey = key.ToString();
            }
            else
            {
                baseKey = baseSwitches.ToString(CultureInfo.InvariantCulture) + "|";
            }

            // A capo avvolti: chiusura subito prima e riapertura subito dopo che rimettono lo stesso stato (<i>…</i>⏎<i>…</i>)
            for (int i = first + 1; i < last; i++)
            {
                if (items[i].Kind != CleanItemKind.Break || items[i - 1].Kind != CleanItemKind.Tag || items[i + 1].Kind != CleanItemKind.Tag)
                    continue;

                int closeStart = i - 1;
                while (items[closeStart - 1].Kind == CleanItemKind.Tag)
                    closeStart--;
                int openEnd = i + 1;
                while (items[openEnd + 1].Kind == CleanItemKind.Tag)
                    openEnd++;
                if (items[closeStart].Wrapped || items[closeStart - 1].StateKey != items[openEnd].StateKey
                    || items[i - 1].StateKey == items[closeStart - 1].StateKey)
                    continue;

                items[i].WrapClose = this.ConcatRaw(items, closeStart, i - 1);
                items[i].WrapOpen = this.ConcatRaw(items, i + 1, openEnd);
                for (int w = closeStart; w <= openEnd; w++)
                {
                    if (w != i)
                        items[w].Wrapped = true;
                }
            }

            // Tratti: i pezzi fra i blocchi che cambiano lo stato; i blocchi che non lo cambiano restano staccati
            List<CleanSegment> segments = new List<CleanSegment>();
            List<string> boundaries = new List<string>();
            CleanSegment segment = new CleanSegment(items[first].StateKey);
            int index = first;
            while (index <= last)
            {
                if (items[index].Kind != CleanItemKind.Tag)
                {
                    segment.Items.Add(index);
                    index++;
                    continue;
                }

                int groupEnd = index;
                while (items[groupEnd + 1].Kind == CleanItemKind.Tag)
                    groupEnd++;
                if (!items[index].Wrapped)
                {
                    if (items[index - 1].StateKey == items[groupEnd].StateKey)
                    {
                        items[index].GroupEnd = groupEnd;
                        segment.Items.Add(index);
                    }
                    else
                    {
                        segments.Add(segment);
                        boundaries.Add(this.ConcatRaw(items, index, groupEnd));
                        segment = new CleanSegment(items[groupEnd].StateKey);
                    }
                }
                index = groupEnd + 1;
            }
            segments.Add(segment);

            foreach (CleanSegment piece in segments)
            {
                bool visible = false;
                foreach (int itemIndex in piece.Items)
                    visible |= items[itemIndex].Kind == CleanItemKind.Char && !char.IsWhiteSpace(items[itemIndex].Clean);
                if (piece.StateKey != baseKey && !visible && legacy)
                    return new SubtitleCleanResult("", null, SubtitleCue.EXCLUSION_WHITESPACE_SPAN);
                segmented |= piece.StateKey != baseKey && !visible;
            }

            // Battuta posizionale: prefisso fino al primo carattere visibile, suffisso dopo l'ultimo, e ogni blocco in mezzo
            // staccato alla sua posizione nel testo pulito, che non ha segni
            if (segmented)
            {
                StringBuilder plain = new StringBuilder();
                List<string> plainBreaks = new List<string>();
                List<int> plainHardSpaces = new List<int>();
                List<int> plainTabs = new List<int>();
                List<DetachedBlock> positional = new List<DetachedBlock>();
                for (int i = first; i <= last; i++)
                {
                    CleanItem item = items[i];
                    if (item.Kind == CleanItemKind.Tag)
                    {
                        positional.Add(new DetachedBlock(plain.Length, item.Raw));
                        continue;
                    }
                    if (item.Kind == CleanItemKind.Break)
                    {
                        plainBreaks.Add(item.Raw);
                        plain.Append('\n');
                        continue;
                    }
                    if (item.Raw == "\t")
                    {
                        plainTabs.Add(plain.Length);
                    }
                    else if (item.Raw == ASS_HARD_SPACE)
                    {
                        plainHardSpaces.Add(plain.Length);
                    }
                    plain.Append(item.Clean);
                }

                return new SubtitleCleanResult(plain.ToString(), new CueRecipe(this.ConcatRaw(items, 0, first - 1), this.ConcatRaw(items, last + 1, items.Count - 1), null, "",
                    new List<MarkRecipe>(), plainBreaks, lineBreakToken, plainHardSpaces, entities, positional, plainTabs, true), "");
            }

            // Prefisso, suffisso e aperture e chiusure dei segni
            string prefix = this.ConcatRaw(items, 0, first - 1);
            string suffix = this.ConcatRaw(items, last + 1, items.Count - 1);
            string prefixLinePart = "";
            LineWrap lineWrap = null;
            List<MarkRecipe> marks = new List<MarkRecipe>();
            List<(int Item, int Mark)> emission = new List<(int Item, int Mark)>();
            for (int s = 0; s < segments.Count; s++)
            {
                CleanSegment piece = segments[s];
                if (piece.StateKey == baseKey)
                {
                    foreach (int itemIndex in piece.Items)
                    {
                        emission.Add((itemIndex, 0));
                        if (lineWrap == null && baseKey != initialKey && items[itemIndex].WrapOpen != null)
                            lineWrap = new LineWrap(items[itemIndex].WrapOpen, items[itemIndex].WrapClose);
                    }
                    continue;
                }

                int number = marks.Count + 1;
                CleanItem firstChar = null;
                bool perLine = false;
                foreach (int itemIndex in piece.Items)
                {
                    if (firstChar == null && items[itemIndex].Kind == CleanItemKind.Char)
                        firstChar = items[itemIndex];
                    perLine |= items[itemIndex].WrapOpen != null;
                }
                List<SrtEntry> extras = firstChar.Stack.GetRange(baseStack.Count, firstChar.Stack.Count - baseStack.Count);
                int extraSwitches = firstChar.Switches & ~baseSwitches;
                StringBuilder fullOpen = new StringBuilder();
                StringBuilder fullClose = new StringBuilder();
                foreach (SrtEntry entry in extras)
                {
                    fullOpen.Append(entry.Raw);
                    fullClose.Insert(0, "</" + entry.Raw.Substring(1, entry.Name.Length) + ">");
                }

                // ASS: apertura dal testo base e chiusura verso il testo base del tratto. Gli interruttori in più si accendono e
                // si spengono; i canali diversi dal testo base riprendono il valore del tratto o del testo base, nell'ordine dei tag
                // del sorgente perché un tag composto (\alpha, \bord) non copra quelli scritti dopo, e un canale senza valore torna
                // allo stile col tag senza argomento del canale, scritto dopo i valori. Con uno stile \r diverso si riparte da \r e
                // si rimette tutto lo stato
                StringBuilder assOpen = new StringBuilder();
                StringBuilder assClose = new StringBuilder();
                if (!isSrt)
                {
                    Dictionary<string, AssChannel> markChannels = firstChar.Channels;
                    markChannels.TryGetValue(ASS_STYLE_CHANNEL, out AssChannel markStyle);
                    baseChannels.TryGetValue(ASS_STYLE_CHANNEL, out AssChannel baseStyle);
                    bool restyled = markStyle?.Tag.Arg.Trim() != baseStyle?.Tag.Arg.Trim();
                    SortedSet<string> names = new SortedSet<string>(markChannels.Keys, StringComparer.Ordinal);
                    names.UnionWith(baseChannels.Keys);
                    names.Remove(ASS_STYLE_CHANNEL);
                    HashSet<string> openNeeded = new HashSet<string>(StringComparer.Ordinal);
                    HashSet<string> closeNeeded = new HashSet<string>(StringComparer.Ordinal);
                    if (restyled)
                    {
                        assOpen.Append(markStyle != null ? markStyle.Tag.Raw : "\\r");
                        assClose.Append(baseStyle != null ? baseStyle.Tag.Raw : "\\r");
                        openNeeded.UnionWith(markChannels.Keys);
                        closeNeeded.UnionWith(baseChannels.Keys);
                    }
                    foreach (string name in names)
                    {
                        markChannels.TryGetValue(name, out AssChannel markValue);
                        baseChannels.TryGetValue(name, out AssChannel baseValue);
                        if (restyled || markValue?.Tag.Arg.Trim() == baseValue?.Tag.Arg.Trim())
                            continue;
                        openNeeded.Add(name);
                        closeNeeded.Add(name);
                    }

                    // Valori in ordine di tag; un tag composto riscritto obbliga a riscrivere anche i tag successivi sui suoi canali e
                    // ad azzerare in coda quelli che lo stato non ha
                    Dictionary<string, AssChannel>[] states = new Dictionary<string, AssChannel>[] { markChannels, baseChannels };
                    HashSet<string>[] neededSets = new HashSet<string>[] { openNeeded, closeNeeded };
                    StringBuilder[] targets = new StringBuilder[] { assOpen, assClose };
                    for (int side = 0; side < states.Length; side++)
                    {
                        Dictionary<string, AssChannel> state = states[side];
                        HashSet<string> needed = neededSets[side];
                        StringBuilder target = targets[side];
                        List<AssChannel> ordered = new List<AssChannel>();
                        foreach (KeyValuePair<string, AssChannel> entry in state)
                        {
                            if (entry.Key != ASS_STYLE_CHANNEL && !ordered.Exists(written => written.Order == entry.Value.Order))
                                ordered.Add(entry.Value);
                        }
                        ordered.Sort((a, b) => a.Order.CompareTo(b.Order));
                        foreach (AssChannel value in ordered)
                        {
                            List<string> covered = new List<string>();
                            foreach (KeyValuePair<string, AssChannel> entry in state)
                            {
                                if (entry.Key != ASS_STYLE_CHANNEL && entry.Value.Order == value.Order)
                                    covered.Add(entry.Key);
                            }
                            if (!covered.Exists(needed.Contains))
                                continue;
                            target.Append(value.Tag.Raw);
                            needed.UnionWith(value.Tag.Name == ASS_TRANSFORM ? covered : s_assStateChannels[value.Tag.Name]);
                        }

                        // Un'animazione non si annulla: nessun tag la riporta allo stile
                        foreach (string channel in new SortedSet<string>(needed, StringComparer.Ordinal))
                        {
                            if (!state.ContainsKey(channel) && !channel.StartsWith(TRANSFORM_CHANNEL, StringComparison.Ordinal))
                                target.Append('\\').Append(channel);
                        }
                    }

                    // Interruttori: dopo \r si riaccendono tutti quelli dello stato, altrimenti solo quelli in più
                    for (int bit = 0; bit < 4; bit++)
                    {
                        if ((extraSwitches & (1 << bit)) != 0 || (restyled && (firstChar.Switches & (1 << bit)) != 0))
                        {
                            AssTag switchOn = items[firstChar.SwitchOnBy[bit]].AssTags.FindLast(tag => this.SwitchBit(tag.Name) == bit);
                            assOpen.Append(switchOn.Raw);
                            if (!restyled)
                                assClose.Append('\\').Append(switchOn.Name).Append('0');
                        }
                        if (restyled && (baseSwitches & (1 << bit)) != 0)
                            assClose.Append(items[items[first].SwitchOnBy[bit]].AssTags.FindLast(tag => this.SwitchBit(tag.Name) == bit).Raw);
                    }
                }
                string fullAssOpen = assOpen.Length > 0 ? "{" + assOpen + "}" : "";
                string fullAssClose = assClose.Length > 0 ? "{" + assClose + "}" : "";

                string originalOpen;
                string open;
                if (s == 0)
                {
                    // Il segno 1 si apre nel prefisso, dal gettone che accende la prima formattazione in più
                    int territory = first;
                    foreach (SrtEntry entry in extras)
                        territory = Math.Min(territory, entry.PushIndex);
                    for (int bit = 0; bit < 4; bit++)
                    {
                        // Un interruttore acceso più volte di fila si apre dalla prima accensione
                        int switchStart = first;
                        while ((extraSwitches & (1 << bit)) != 0 && switchStart > 0 && (items[switchStart - 1].Switches & (1 << bit)) != 0)
                            switchStart--;
                        territory = Math.Min(territory, switchStart);
                    }

                    StringBuilder linePart = new StringBuilder();
                    StringBuilder inlinePart = new StringBuilder();
                    for (int t = territory; t < first; t++)
                    {
                        CleanItem item = items[t];
                        if (item.Kind != CleanItemKind.Tag)
                            continue;
                        if (item.AssTags == null)
                        {
                            if (item.HtmlName != null && !item.HtmlClose && extras.Exists(entry => entry.PushIndex == t))
                            {
                                inlinePart.Append(item.Raw);
                            }
                            else
                            {
                                linePart.Append(item.Raw);
                            }
                            continue;
                        }

                        StringBuilder lineTags = new StringBuilder();
                        StringBuilder inlineTags = new StringBuilder();
                        foreach (AssTag tag in item.AssTags)
                        {
                            int bit = this.SwitchBit(tag.Name);
                            if (bit >= 0 && (extraSwitches & (1 << bit)) != 0)
                            {
                                inlineTags.Append(tag.Raw);
                            }
                            else
                            {
                                lineTags.Append(tag.Raw);
                            }
                        }
                        if (item.AssTags.Count == 0)
                        {
                            linePart.Append(item.Raw);
                        }
                        else if (lineTags.Length > 0)
                        {
                            linePart.Append('{').Append(lineTags).Append('}');
                        }
                        if (inlineTags.Length > 0)
                            inlinePart.Append('{').Append(inlineTags).Append('}');
                    }

                    prefix = this.ConcatRaw(items, 0, territory - 1);
                    prefixLinePart = linePart.ToString();
                    originalOpen = this.ConcatRaw(items, territory, first - 1);
                    open = isSrt ? fullOpen.ToString() : inlinePart.ToString();
                }
                else
                {
                    // Dopo un altro tratto il blocco originale è il passaggio da quel tratto: dal testo base serve l'apertura intera
                    originalOpen = boundaries[s - 1];
                    if (isSrt)
                    {
                        open = fullOpen.ToString();
                    }
                    else
                    {
                        open = segments[s - 1].StateKey == baseKey ? originalOpen : fullAssOpen;
                    }
                }

                string originalClose;
                string close;
                if (s == segments.Count - 1)
                {
                    // L'ultimo segno si chiude nel suffisso, al gettone che riporta la formattazione di base
                    int closing = -1;
                    for (int u = last + 1; u < items.Count && closing < 0; u++)
                    {
                        if (items[u].Kind == CleanItemKind.Tag && items[u].StateKey == baseKey)
                            closing = u;
                    }

                    if (closing < 0)
                    {
                        originalClose = "";
                        close = isSrt ? fullClose.ToString() : fullAssClose;
                    }
                    else
                    {
                        StringBuilder closingTags = new StringBuilder();
                        for (int u = last + 1; u <= closing; u++)
                        {
                            if (items[u].Kind == CleanItemKind.Tag)
                                closingTags.Append(items[u].Raw);
                        }
                        originalClose = this.ConcatRaw(items, last + 1, closing);
                        close = isSrt ? fullClose.ToString() : closingTags.ToString();
                        suffix = this.ConcatRaw(items, closing + 1, items.Count - 1);
                    }
                }
                else if (segments[s + 1].StateKey == baseKey)
                {
                    originalClose = boundaries[s];
                    close = isSrt ? fullClose.ToString() : originalClose;
                }
                else
                {
                    originalClose = "";
                    close = isSrt ? fullClose.ToString() : fullAssClose;
                }

                // Gli spazi fra il tag e il testo del tratto stanno fuori dal segno
                int leading = 0;
                while (s > 0 && leading < piece.Items.Count && items[piece.Items[leading]].Kind == CleanItemKind.Char
                    && char.IsWhiteSpace(items[piece.Items[leading]].Clean))
                    leading++;
                int trailing = 0;
                while (s < segments.Count - 1 && trailing < piece.Items.Count - leading
                    && items[piece.Items[piece.Items.Count - 1 - trailing]].Kind == CleanItemKind.Char
                    && char.IsWhiteSpace(items[piece.Items[piece.Items.Count - 1 - trailing]].Clean))
                    trailing++;
                StringBuilder innerLeading = new StringBuilder();
                StringBuilder innerTrailing = new StringBuilder();
                for (int p = 0; p < piece.Items.Count; p++)
                {
                    if (p == leading)
                        emission.Add((-1, number));
                    if (p == piece.Items.Count - trailing)
                        emission.Add((-1, -number));
                    emission.Add((piece.Items[p], 0));
                    if (p < leading)
                    {
                        innerLeading.Append(items[piece.Items[p]].Clean);
                    }
                    else if (p >= piece.Items.Count - trailing)
                    {
                        innerTrailing.Append(items[piece.Items[p]].Clean);
                    }
                }
                if (trailing == 0)
                    emission.Add((-1, -number));

                marks.Add(new MarkRecipe(number, open, close, originalOpen, originalClose, perLine, innerLeading.ToString(), innerTrailing.ToString()));
            }

            // Testo pulito, a capo originali e posizioni di tab, \h e blocchi staccati
            StringBuilder clean = new StringBuilder();
            List<string> lineBreaks = new List<string>();
            List<int> hardSpaces = new List<int>();
            List<int> tabs = new List<int>();
            List<DetachedBlock> detached = new List<DetachedBlock>();
            foreach ((int Item, int Mark) step in emission)
            {
                if (step.Mark > 0)
                {
                    clean.Append(MARK_OPEN).Append(step.Mark.ToString(CultureInfo.InvariantCulture)).Append(' ');
                    continue;
                }
                if (step.Mark < 0)
                {
                    clean.Append(MARK_CLOSE);
                    continue;
                }

                CleanItem item = items[step.Item];
                if (item.Kind == CleanItemKind.Tag)
                {
                    detached.Add(new DetachedBlock(clean.Length, this.ConcatRaw(items, step.Item, item.GroupEnd)));
                }
                else if (item.Kind == CleanItemKind.Break)
                {
                    lineBreaks.Add((item.WrapClose ?? "") + item.Raw + (item.WrapOpen ?? ""));
                    clean.Append('\n');
                }
                else
                {
                    if (item.Raw == "\t")
                    {
                        tabs.Add(clean.Length);
                    }
                    else if (item.Raw == ASS_HARD_SPACE)
                    {
                        hardSpaces.Add(clean.Length);
                    }
                    clean.Append(item.Clean);
                }
            }

            return new SubtitleCleanResult(clean.ToString(), new CueRecipe(prefix, suffix, lineWrap, prefixLinePart, marks, lineBreaks,
                lineBreakToken, hardSpaces, entities, detached, tabs, false), "");
        }

        /// <summary>
        /// Ricompone il campo Text di una battuta da un testo pulito, tradotto o no. Il testo deve essere già normalizzato dal
        /// validatore; se coincide con il testo pulito del sorgente si rifà il campo Text originale
        /// </summary>
        /// <param name="cue">Battuta traducibile o copia, con la sua ricetta</param>
        /// <param name="text">Testo pulito con a capo \n e segni ⟦n testo⟧</param>
        /// <returns>Campo Text nel formato d'origine</returns>
        public string Recompose(SubtitleCue cue, string text)
        {
            CueRecipe recipe = cue.Recipe;
            string value = text ?? "";
            bool unchanged = value == cue.CleanText;

            // Battuta posizionale tradotta. Il sorgente è diviso dai suoi blocchi in pezzi; ogni confine sta alla stessa frazione dei
            // caratteri nel testo tradotto, arrotondata alla metà per eccesso. I caratteri sono grafemi e un a capo conta come uno.
            // Se il testo tradotto ha almeno tanti caratteri quanti pezzi, ogni pezzo ne riceve almeno uno: i confini si spostano in
            // avanti e poi indietro quanto basta. Altrimenti i pezzi in fondo restano vuoti, coi loro blocchi uno dopo l'altro. I
            // blocchi escono tutti, nell'ordine e coi valori originali: le durate del karaoke e la loro somma non cambiano
            if (recipe.Segmented && !unchanged)
            {
                int[] sourceStarts = StringInfo.ParseCombiningCharacters(cue.CleanText);
                List<string> elements = new List<string>();
                TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(value);
                while (enumerator.MoveNext())
                    elements.Add(enumerator.GetTextElement());

                // Confini distinti del sorgente in grafemi, e per ogni blocco il suo confine
                List<int> sourceCuts = new List<int>();
                int[] blockCut = new int[recipe.Detached.Count];
                for (int b = 0; b < recipe.Detached.Count; b++)
                {
                    int cut = 0;
                    while (cut < sourceStarts.Length && sourceStarts[cut] < recipe.Detached[b].CleanOffset)
                        cut++;
                    if (sourceCuts.Count == 0 || sourceCuts[sourceCuts.Count - 1] != cut)
                        sourceCuts.Add(cut);
                    blockCut[b] = sourceCuts.Count - 1;
                }

                int sourceLength = sourceStarts.Length;
                int targetLength = elements.Count;
                int[] targetCuts = new int[sourceCuts.Count];
                for (int k = 0; k < sourceCuts.Count; k++)
                    targetCuts[k] = (int)((2L * sourceCuts[k] * targetLength + sourceLength) / (2L * sourceLength));
                bool everyPiece = targetLength >= sourceCuts.Count + 1;
                for (int k = 0; k < targetCuts.Length; k++)
                    targetCuts[k] = Math.Min(Math.Max(targetCuts[k], (k > 0 ? targetCuts[k - 1] : 0) + (everyPiece ? 1 : 0)), targetLength);
                for (int k = targetCuts.Length - 1; k >= 0 && everyPiece; k--)
                    targetCuts[k] = Math.Min(targetCuts[k], (k < targetCuts.Length - 1 ? targetCuts[k + 1] : targetLength) - 1);

                StringBuilder distributed = new StringBuilder(recipe.Prefix);
                int nextBlock = 0;
                for (int e = 0; e <= elements.Count; e++)
                {
                    while (nextBlock < recipe.Detached.Count && targetCuts[blockCut[nextBlock]] <= e)
                        distributed.Append(recipe.Detached[nextBlock++].Raw);
                    if (e == elements.Count)
                        break;

                    string element = elements[e];
                    if (element == "\n" || element == "\r" || element == "\r\n")
                    {
                        distributed.Append(recipe.LineBreakToken);
                        continue;
                    }
                    foreach (char current in element)
                    {
                        if (current == HARD_SPACE && recipe.HardSpaceOffsets.Count > 0)
                        {
                            distributed.Append(ASS_HARD_SPACE);
                        }
                        else if (recipe.Entities.TryGetValue(current, out string entity))
                        {
                            distributed.Append(entity);
                        }
                        else
                        {
                            distributed.Append(current);
                        }
                    }
                }

                return distributed.Append(recipe.Suffix).ToString();
            }

            List<RecomposePiece> pieces = new List<RecomposePiece>();
            StringBuilder run = new StringBuilder();
            int runStart = 0;
            int position = 0;

            // Pezzi del testo: caratteri, aperture e chiusure dei segni, a capo
            while (position < value.Length)
            {
                char current = value[position];
                int digits = position + 1;
                while (current == MARK_OPEN && digits < value.Length && value[digits] >= '0' && value[digits] <= '9')
                    digits++;
                bool isOpen = current == MARK_OPEN && digits > position + 1 && digits < value.Length && value[digits] == ' ';
                bool isBreak = current == '\r' || current == '\n';
                if (!isOpen && !isBreak && current != MARK_CLOSE)
                {
                    if (run.Length == 0)
                        runStart = position;
                    run.Append(current);
                    position++;
                    continue;
                }

                if (run.Length > 0)
                {
                    pieces.Add(new RecomposePiece(RecomposePieceKind.Text, run.ToString(), runStart, 0));
                    run.Clear();
                }
                if (isOpen)
                {
                    int number = int.Parse(value.Substring(position + 1, digits - position - 1), NumberStyles.Integer, CultureInfo.InvariantCulture);
                    pieces.Add(new RecomposePiece(RecomposePieceKind.Open, "", position, number));
                    position = digits + 1;
                }
                else if (isBreak)
                {
                    pieces.Add(new RecomposePiece(RecomposePieceKind.Break, "", position, 0));
                    position += current == '\r' && position + 1 < value.Length && value[position + 1] == '\n' ? 2 : 1;
                }
                else
                {
                    pieces.Add(new RecomposePiece(RecomposePieceKind.Close, "", position, 0));
                    position++;
                }
            }
            if (run.Length > 0)
                pieces.Add(new RecomposePiece(RecomposePieceKind.Text, run.ToString(), runStart, 0));

            // Gli spazi subito prima di un'apertura o subito dopo una chiusura tornano dentro i tag del tratto
            int openNumber = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                if (pieces[i].Kind == RecomposePieceKind.Open)
                    openNumber = pieces[i].Number;
                if (pieces[i].Kind == RecomposePieceKind.Close)
                    pieces[i] = new RecomposePiece(RecomposePieceKind.Close, "", pieces[i].Offset, openNumber);
                if (pieces[i].Kind == RecomposePieceKind.Open && i > 0 && pieces[i - 1].Kind == RecomposePieceKind.Text
                    && pieces[i].Number <= recipe.Marks.Count)
                {
                    string leading = recipe.Marks[pieces[i].Number - 1].InnerLeading;
                    RecomposePiece before = pieces[i - 1];
                    if (!string.IsNullOrEmpty(leading) && before.Text.EndsWith(leading, StringComparison.Ordinal))
                    {
                        int keep = before.Text.Length - leading.Length;
                        pieces[i - 1] = pieces[i];
                        pieces[i] = new RecomposePiece(RecomposePieceKind.Text, leading, before.Offset + keep, 0);
                        if (keep > 0)
                        {
                            pieces.Insert(i - 1, new RecomposePiece(RecomposePieceKind.Text, before.Text.Substring(0, keep), before.Offset, 0));
                            i++;
                        }
                    }
                }
                if (pieces[i].Kind == RecomposePieceKind.Close && pieces[i].Number > 0 && pieces[i].Number <= recipe.Marks.Count
                    && i + 1 < pieces.Count && pieces[i + 1].Kind == RecomposePieceKind.Text)
                {
                    string trailing = recipe.Marks[pieces[i].Number - 1].InnerTrailing;
                    RecomposePiece after = pieces[i + 1];
                    if (!string.IsNullOrEmpty(trailing) && after.Text.StartsWith(trailing, StringComparison.Ordinal))
                    {
                        pieces[i + 1] = pieces[i];
                        pieces[i] = new RecomposePiece(RecomposePieceKind.Text, trailing, after.Offset, 0);
                        if (after.Text.Length > trailing.Length)
                            pieces.Insert(i + 2, new RecomposePiece(RecomposePieceKind.Text, after.Text.Substring(trailing.Length), after.Offset + trailing.Length, 0));
                        i++;
                    }
                }
            }

            // Ricomposizione: originali quando il testo non cambia, aperture, chiusure e a capo per il testo tradotto altrimenti
            StringBuilder output = new StringBuilder(recipe.Prefix);
            bool firstIsMarkOne = pieces.Count > 0 && pieces[0].Kind == RecomposePieceKind.Open && pieces[0].Number == 1;
            if (!unchanged && !firstIsMarkOne)
                output.Append(recipe.PrefixLinePart);
            MarkRecipe openMark = null;
            bool continuesMark = false;
            int breakIndex = 0;
            int detachedIndex = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                RecomposePiece piece = pieces[i];
                while (unchanged && detachedIndex < recipe.Detached.Count && recipe.Detached[detachedIndex].CleanOffset <= piece.Offset)
                    output.Append(recipe.Detached[detachedIndex++].Raw);

                if (piece.Kind == RecomposePieceKind.Text)
                {
                    for (int c = 0; c < piece.Text.Length; c++)
                    {
                        char current = piece.Text[c];
                        int offset = piece.Offset + c;
                        while (unchanged && detachedIndex < recipe.Detached.Count && recipe.Detached[detachedIndex].CleanOffset <= offset)
                            output.Append(recipe.Detached[detachedIndex++].Raw);
                        if (unchanged && recipe.TabOffsets.Contains(offset))
                        {
                            output.Append('\t');
                        }
                        else if (current == HARD_SPACE && recipe.HardSpaceOffsets.Count > 0 && (!unchanged || recipe.HardSpaceOffsets.Contains(offset)))
                        {
                            output.Append(ASS_HARD_SPACE);
                        }
                        else if (recipe.Entities.TryGetValue(current, out string entity))
                        {
                            output.Append(entity);
                        }
                        else
                        {
                            output.Append(current);
                        }
                    }
                }
                else if (piece.Kind == RecomposePieceKind.Open)
                {
                    openMark = recipe.Marks[piece.Number - 1];
                    output.Append(unchanged || continuesMark || (i == 0 && piece.Number == 1) ? openMark.OriginalOpen : openMark.Open);
                    continuesMark = false;
                }
                else if (piece.Kind == RecomposePieceKind.Close)
                {
                    MarkRecipe closed = recipe.Marks[piece.Number - 1];
                    RecomposePiece next = i + 1 < pieces.Count ? pieces[i + 1] : null;
                    bool nextIsFollowingMark = next != null && next.Kind == RecomposePieceKind.Open && next.Number == piece.Number + 1;
                    if (unchanged)
                    {
                        output.Append(closed.OriginalClose);
                    }
                    else if (this._format == SubtitleTranslationFormat.Srt && next != null && next.Kind == RecomposePieceKind.Open)
                    {
                        // Tratti adiacenti in SRT: i tag comuni non si chiudono e riaprono
                        List<string> closedOpens = new List<string>();
                        List<string> nextOpens = new List<string>();
                        for (Match tag = s_srtTag.Match(closed.Open); tag.Success; tag = s_srtTag.Match(closed.Open, tag.Index + tag.Length))
                            closedOpens.Add(tag.Value);
                        for (Match tag = s_srtTag.Match(recipe.Marks[next.Number - 1].Open); tag.Success; tag = s_srtTag.Match(recipe.Marks[next.Number - 1].Open, tag.Index + tag.Length))
                            nextOpens.Add(tag.Value);
                        int common = 0;
                        while (common < closedOpens.Count && common < nextOpens.Count && closedOpens[common] == nextOpens[common])
                            common++;
                        for (int c = closedOpens.Count - 1; c >= common; c--)
                            output.Append("</").Append(closedOpens[c].Substring(1, s_srtTag.Match(closedOpens[c]).Groups["name"].Length)).Append('>');
                        for (int o = common; o < nextOpens.Count; o++)
                            output.Append(nextOpens[o]);
                        openMark = recipe.Marks[next.Number - 1];
                        i++;
                        continue;
                    }
                    else if (string.IsNullOrEmpty(closed.OriginalClose) && nextIsFollowingMark)
                    {
                        // Tratti adiacenti anche nel sorgente: il segno seguente si apre col passaggio originale
                        continuesMark = true;
                    }
                    else if (next != null || !string.IsNullOrEmpty(closed.OriginalClose))
                    {
                        // Un tratto che nel sorgente arriva in fondo senza chiudersi si chiude solo se dopo c'è altro testo
                        output.Append(closed.Close);
                    }
                    openMark = null;
                }
                else if (unchanged)
                {
                    output.Append(recipe.LineBreaks[breakIndex++]);
                }
                else
                {
                    // Il tratto aperto si chiude prima dell'a capo e si riapre dopo, anche dentro le righe avvolte
                    bool wrapsMark = openMark != null && (openMark.PerLine || recipe.LineWrap != null);
                    if (wrapsMark)
                        output.Append(openMark.Close);
                    if (recipe.LineWrap != null)
                        output.Append(recipe.LineWrap.Close);
                    output.Append(recipe.LineBreakToken);
                    if (recipe.LineWrap != null)
                        output.Append(recipe.LineWrap.Open);
                    if (wrapsMark)
                        output.Append(openMark.Open);
                }
            }

            // Blocchi staccati: al loro posto se il testo non cambia, prima del suffisso nel testo tradotto
            while (detachedIndex < recipe.Detached.Count)
                output.Append(recipe.Detached[detachedIndex++].Raw);
            return output.Append(recipe.Suffix).ToString();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Concatena i gettoni originali di un intervallo
        /// </summary>
        /// <param name="items">Gettoni del campo Text</param>
        /// <param name="from">Primo gettone, compreso</param>
        /// <param name="to">Ultimo gettone, compreso; prima di from per un intervallo vuoto</param>
        /// <returns>Testo originale dell'intervallo</returns>
        private string ConcatRaw(List<CleanItem> items, int from, int to)
        {
            StringBuilder result = new StringBuilder();
            for (int i = from; i <= to; i++)
                result.Append(items[i].Raw);
            return result.ToString();
        }

        /// <summary>
        /// Interruttore ASS di un tag
        /// </summary>
        /// <param name="name">Nome del tag</param>
        /// <returns>Bit dell'interruttore (\i 0, \b 1, \u 2, \s 3), -1 per gli altri tag</returns>
        private int SwitchBit(string name)
        {
            switch (name)
            {
                case "i": return 0;
                case "b": return 1;
                case "u": return 2;
                case "s": return 3;
                default: return -1;
            }
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Tipo di gettone del campo Text
        /// </summary>
        private enum CleanItemKind
        {
            /// <summary>Carattere di testo</summary>
            Char,

            /// <summary>A capo</summary>
            Break,

            /// <summary>Tag o blocco</summary>
            Tag
        }

        /// <summary>
        /// Tipo di pezzo del testo da ricomporre
        /// </summary>
        private enum RecomposePieceKind
        {
            /// <summary>Caratteri</summary>
            Text,

            /// <summary>Apertura di un segno</summary>
            Open,

            /// <summary>Chiusura di un segno</summary>
            Close,

            /// <summary>A capo</summary>
            Break
        }

        /// <summary>
        /// Gettone del campo Text con lo stato della formattazione dopo di lui
        /// </summary>
        private sealed class CleanItem
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="kind">Tipo di gettone</param>
            /// <param name="raw">Testo originale</param>
            /// <param name="clean">Carattere nel testo pulito</param>
            public CleanItem(CleanItemKind kind, string raw, char clean)
            {
                this.Kind = kind;
                this.Raw = raw;
                this.Clean = clean;
            }

            /// <summary>Tipo di gettone</summary>
            public CleanItemKind Kind { get; }

            /// <summary>Testo originale</summary>
            public string Raw { get; }

            /// <summary>Carattere nel testo pulito</summary>
            public char Clean { get; }

            /// <summary>Nome del tag HTML SRT in minuscolo</summary>
            public string HtmlName { get; set; }

            /// <summary>Tag HTML di chiusura</summary>
            public bool HtmlClose { get; set; }

            /// <summary>Blocco {\…} in un SRT</summary>
            public bool IsSrtAssBlock { get; set; }

            /// <summary>Tag di un blocco ASS, vuoto per un commento</summary>
            public List<AssTag> AssTags { get; set; }

            /// <summary>Pila dei tag SRT aperti dopo il gettone</summary>
            public List<SrtEntry> Stack { get; set; }

            /// <summary>Interruttori ASS accesi dopo il gettone</summary>
            public int Switches { get; set; }

            /// <summary>Gettone che ha acceso per ultimo ciascun interruttore ASS</summary>
            public int[] SwitchOnBy { get; set; }

            /// <summary>Canali di stato ASS dopo il gettone</summary>
            public Dictionary<string, AssChannel> Channels { get; set; }

            /// <summary>Chiave confrontabile dello stato dopo il gettone</summary>
            public string StateKey { get; set; }

            /// <summary>Tag che fa parte dell'avvolgimento di un a capo</summary>
            public bool Wrapped { get; set; }

            /// <summary>Chiusura prima dell'a capo avvolto</summary>
            public string WrapClose { get; set; }

            /// <summary>Riapertura dopo l'a capo avvolto</summary>
            public string WrapOpen { get; set; }

            /// <summary>Ultimo gettone del gruppo di tag staccato che comincia qui</summary>
            public int GroupEnd { get; set; }
        }

        /// <summary>
        /// Pezzo del testo pulito con lo stesso stato della formattazione
        /// </summary>
        private sealed class CleanSegment
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="stateKey">Chiave dello stato</param>
            public CleanSegment(string stateKey)
            {
                this.StateKey = stateKey;
                this.Items = new List<int>();
            }

            /// <summary>Chiave dello stato</summary>
            public string StateKey { get; }

            /// <summary>Gettoni del pezzo: caratteri, a capo e primi gettoni dei gruppi staccati</summary>
            public List<int> Items { get; }
        }

        /// <summary>
        /// Tag di un blocco ASS
        /// </summary>
        /// <param name="Name">Nome senza \</param>
        /// <param name="Arg">Argomento, parentesi comprese</param>
        /// <param name="Raw">Tag originale con la \</param>
        private sealed record AssTag(string Name, string Arg, string Raw);

        /// <summary>
        /// Tag SRT aperto
        /// </summary>
        /// <param name="Name">Nome in minuscolo</param>
        /// <param name="Raw">Tag originale</param>
        /// <param name="PushIndex">Gettone che l'ha aperto</param>
        private sealed record SrtEntry(string Name, string Raw, int PushIndex);

        /// <summary>
        /// Valore di un canale di stato ASS
        /// </summary>
        /// <param name="Tag">Tag che l'ha impostato</param>
        /// <param name="Order">Posizione del tag fra tutti i tag della battuta</param>
        private sealed record AssChannel(AssTag Tag, int Order);

        /// <summary>
        /// Pezzo del testo da ricomporre
        /// </summary>
        /// <param name="Kind">Tipo di pezzo</param>
        /// <param name="Text">Caratteri, per un pezzo di testo</param>
        /// <param name="Offset">Posizione nel testo</param>
        /// <param name="Number">Numero del segno</param>
        private sealed record RecomposePiece(RecomposePieceKind Kind, string Text, int Offset, int Number);

        #endregion
    }

    /// <summary>
    /// Esito della pulizia di una battuta
    /// </summary>
    /// <param name="CleanText">Testo pulito, vuoto per una battuta esclusa</param>
    /// <param name="Recipe">Ricetta di ricomposizione, null per una battuta esclusa</param>
    /// <param name="ExclusionReason">Motivo per cui la battuta resta al codice, vuoto se si traduce</param>
    public sealed record SubtitleCleanResult(string CleanText, CueRecipe Recipe, string ExclusionReason);
}
