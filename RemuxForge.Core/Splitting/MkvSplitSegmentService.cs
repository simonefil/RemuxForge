using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RemuxForge.Core.Chapters;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Segments;

namespace RemuxForge.Core.Splitting
{
    /// <summary>Costruzione dei segmenti: parsing range CLI, grouping per pattern/capitoli, naming dei file, snap a keyframe.</summary>
    public class MkvSplitSegmentService
    {
        #region Costanti

        /// <summary>Caratteri non ammessi nei filename (sostituiti con underscore).</summary>
        private const string FORBIDDEN_FS_CHARS = ":*?\"<>|";

        #endregion

        #region Costruttore

        /// <summary>Costruttore: il servizio accumula gli avvisi delle chiamate successive in <see cref="Warnings"/>.</summary>
        public MkvSplitSegmentService()
        {
            this.Warnings = new List<MkvSplitWarning>();
        }

        #endregion

        #region Proprietà

        /// <summary>Avvisi raccolti dalle chiamate al servizio, letti dal pianificatore.</summary>
        public List<MkvSplitWarning> Warnings { get; private set; }

        #endregion

        #region Avvisi

        /// <summary>Registra un avviso nel piano e ne scrive la riga di dettaglio nel log; lo usano anche le strategie dei segmenti.</summary>
        /// <param name="kind">Categoria dell'avviso.</param>
        /// <param name="message">Testo localizzato.</param>
        internal void AddWarning(MkvSplitWarningKind kind, string message)
        {
            this.Warnings.Add(new MkvSplitWarning(kind, message));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, message);
        }

        #endregion

        #region Utility tempo (static, pubbliche perché usate anche da EpisodeSplitter)

        /// <summary>Parsa un token tempo accettando HH:MM:SS.mmm, MM:SS.mmm, SS.mmm, f&lt;int&gt; (frame index) oppure END.</summary>
        /// <param name="s">Stringa da parsare.</param>
        /// <param name="duration">Durata totale del file, usata per interpretare END.</param>
        /// <returns>Tupla (valore numerico, flag isFrame).</returns>
        public static (double value, bool isFrame) ParseTime(string s, double duration)
        {
            string[] parts;
            int f;
            int h;
            int m;
            double sec;

            s = s.Trim();

            // Parola riservata: fine file
            if (s == "END") { return (duration, false); }

            // Prefisso "f" seguito da un intero = indice di frame
            if (!string.IsNullOrEmpty(s) && (s[0] == 'f' || s[0] == 'F'))
            {
                f = int.Parse(s.Substring(1), CultureInfo.InvariantCulture);
                return (f, true);
            }

            // Parse basato sul numero di ":" presenti
            parts = s.Split(':');
            if (parts.Length == 3)
            {
                h = int.Parse(parts[0], CultureInfo.InvariantCulture);
                m = int.Parse(parts[1], CultureInfo.InvariantCulture);
                sec = double.Parse(parts[2], CultureInfo.InvariantCulture);
                return (h * 3600.0 + m * 60.0 + sec, false);
            }
            if (parts.Length == 2)
            {
                m = int.Parse(parts[0], CultureInfo.InvariantCulture);
                sec = double.Parse(parts[1], CultureInfo.InvariantCulture);
                return (m * 60.0 + sec, false);
            }

            // Nessun ":" = stringa in secondi decimali puri
            return (double.Parse(s, CultureInfo.InvariantCulture), false);
        }

        /// <summary>Converte (valore, isFrame) in indice di frame. Per i valori temporali usa bisect_left sui PTS.</summary>
        /// <param name="value">Valore numerico (secondi oppure indice frame).</param>
        /// <param name="isFrame">True se il valore è un indice di frame già risolto.</param>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente.</param>
        /// <returns>Indice di frame corrispondente.</returns>
        public static int TimeToFrame(double value, bool isFrame, double[] sourcePts)
        {
            if (isFrame) { return (int)value; }
            return MkvSplitAnalysis.BisectLeft(sourcePts, value);
        }

        /// <summary>
        /// Fotogramma marcato da un capitolo: il primo con PTS non inferiore al suo inizio, entro lo scarto di arrotondamento
        /// fra il tempo del capitolo e quello del fotogramma (ChapterTimelineService.FRAME_ROUNDING_NS)
        /// </summary>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente.</param>
        /// <param name="chapterSeconds">Inizio del capitolo in secondi.</param>
        /// <returns>Indice del fotogramma, oppure il numero di fotogrammi se il capitolo cade dopo l'ultimo.</returns>
        public static int ChapterFrame(double[] sourcePts, double chapterSeconds)
        {
            return MkvSplitAnalysis.BisectLeft(sourcePts, chapterSeconds - ChapterTimelineService.FRAME_ROUNDING_NS / 1000000000.0);
        }

        #endregion

        #region Normalizzazione scorciatoie

        /// <summary>Traduce --split-at/--trim-*/--chapters-each in args.Ranges dopo aver validato le mutue esclusioni.</summary>
        /// <param name="args">Argomenti CLI parsati.</param>
        /// <param name="duration">Durata del file in secondi.</param>
        /// <param name="totalFrames">Numero totale di frame del sorgente.</param>
        public void NormalizeShortcuts(MkvSplitOptions args, double duration, int totalFrames)
        {
            List<string> shortcuts;
            List<string> others;
            List<string> tokens;
            HashSet<string> seen;
            string key;
            string s;
            string e;
            List<string> parts;
            StringBuilder rsb;
            (double val, bool isFrame) parsed;

            // La modalità manuale non riceve parametri di taglio: i segmenti arrivano dall'editor
            if (args.Manual)
            {
                return;
            }

            // Enumerazione delle scorciatoie utilizzate
            shortcuts = new List<string>();
            if (!string.IsNullOrEmpty(args.SplitAt)) { shortcuts.Add("--split-at"); }
            if (!string.IsNullOrEmpty(args.TrimStart)) { shortcuts.Add("--trim-start"); }
            if (!string.IsNullOrEmpty(args.TrimEnd)) { shortcuts.Add("--trim-end"); }
            if (args.ChaptersEach) { shortcuts.Add("--chapters-each"); }
            if (args.ChaptersPerEpisode > 0) { shortcuts.Add("--chapters-per-episode"); }

            // Enumerazione delle opzioni full (ranges/pattern)
            others = new List<string>();
            if (!string.IsNullOrEmpty(args.Ranges)) { others.Add("--ranges"); }
            if (!string.IsNullOrEmpty(args.Pattern)) { others.Add("--pattern"); }

            // Validazione mutua esclusione fra pattern e ranges
            if (!string.IsNullOrEmpty(args.Pattern) && !string.IsNullOrEmpty(args.Ranges))
            {
                throw new ArgumentException(AppText.T("split.patternRangesExclusive"));
            }

            // Scorciatoie e opzioni full non si combinano
            if (shortcuts.Count > 0 && others.Count > 0)
            {
                throw new ArgumentException(AppText.F("split.optionsCannotCombine", string.Join(", ", shortcuts), string.Join(", ", others)));
            }

            // --split-at non si combina con --trim-*
            if (!string.IsNullOrEmpty(args.SplitAt) && (!string.IsNullOrEmpty(args.TrimStart) || !string.IsNullOrEmpty(args.TrimEnd)))
            {
                throw new ArgumentException(AppText.T("split.splitAtTrimExclusive"));
            }

            // --chapters-each non si combina con nessuna altra scorciatoia
            if (args.ChaptersEach && (!string.IsNullOrEmpty(args.SplitAt) || !string.IsNullOrEmpty(args.TrimStart) || !string.IsNullOrEmpty(args.TrimEnd)))
            {
                throw new ArgumentException(AppText.T("split.chaptersEachShortcutExclusive"));
            }

            // --chapters-per-episode è una modalità intera, non si combina con niente
            if (args.ChaptersPerEpisode > 0 && shortcuts.Count > 1)
            {
                throw new ArgumentException(AppText.T("split.chaptersPerEpisodeExclusive"));
            }

            // Trasformazione di --split-at in sequenza di range (0-T1,T1-T2,...,Tn-END)
            if (!string.IsNullOrEmpty(args.SplitAt))
            {
                tokens = new List<string>();
                foreach (string t in args.SplitAt.Split(','))
                {
                    string tt = t.Trim();
                    if (!string.IsNullOrEmpty(tt))
                        tokens.Add(tt);
                }
                if (tokens.Count == 0)
                {
                    throw new ArgumentException(AppText.T("split.splitAtEmpty"));
                }

                // Check di duplicati e range
                seen = new HashSet<string>();
                foreach (string tok in tokens)
                {
                    parsed = ParseTime(tok, duration);
                    if (parsed.isFrame) { key = "f|" + ((int)parsed.val).ToString(CultureInfo.InvariantCulture); }
                    else { key = "t|" + Math.Round(parsed.val, 6).ToString("F6", CultureInfo.InvariantCulture); }

                    if (!seen.Add(key))
                    {
                        throw new ArgumentException(AppText.F("split.splitAtDuplicate", tok));
                    }
                    if (parsed.isFrame)
                    {
                        if (parsed.val <= 0 || parsed.val >= totalFrames)
                        {
                            throw new ArgumentException(AppText.F("split.splitAtFrameOutOfRange", parsed.val, totalFrames - 1));
                        }
                    }
                    else
                    {
                        if (parsed.val <= 0 || parsed.val >= duration)
                        {
                            throw new ArgumentException(AppText.F("split.splitAtTimeOutOfRange", tok));
                        }
                    }
                }

                // Costruzione della stringa ranges equivalente: 0-T1,T1-T2,...,Tn-END
                parts = new List<string>();
                parts.Add("0");
                parts.AddRange(tokens);
                parts.Add("END");
                rsb = new StringBuilder();
                for (int i = 0; i < parts.Count - 1; i++)
                {
                    if (i > 0) { rsb.Append(','); }
                    rsb.Append(parts[i]);
                    rsb.Append('-');
                    rsb.Append(parts[i + 1]);
                }
                args.Ranges = rsb.ToString();
                return;
            }

            // Trasformazione di --trim-start/--trim-end in un singolo range
            if (!string.IsNullOrEmpty(args.TrimStart) || !string.IsNullOrEmpty(args.TrimEnd))
            {
                s = !string.IsNullOrEmpty(args.TrimStart) ? args.TrimStart.Trim() : "0";
                e = !string.IsNullOrEmpty(args.TrimEnd) ? args.TrimEnd.Trim() : "END";
                args.Ranges = s + "-" + e;
            }
        }

        #endregion

        #region Parsing ranges

        /// <summary>Parsa "T1-T2,T3-T4,..." in coppie (startFrame, endFrame) esclusive a destra, con clamping/warning sulle range fuori EOF.</summary>
        /// <param name="rangesStr">Stringa dei range separati da virgola.</param>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente.</param>
        /// <param name="duration">Durata totale del file.</param>
        /// <returns>Lista di coppie (startFrame, endFrame) esclusive a destra.</returns>
        public List<(int startFrame, int endFrame)> ParseRanges(string rangesStr, double[] sourcePts, double duration)
        {
            int totalFrames;
            List<(int, int)> result;
            string[] rangeTokens;
            string r;
            int dash;
            string t1Str;
            string t2Str;
            (double val, bool isFrame) t1;
            (double val, bool isFrame) t2;
            int startF;
            int endF;

            totalFrames = sourcePts.Length;
            result = new List<(int, int)>();
            rangeTokens = rangesStr.Split(',');

            // Parsing di ciascun range T1-T2
            for (int i = 0; i < rangeTokens.Length; i++)
            {
                r = rangeTokens[i].Trim();
                if (string.IsNullOrEmpty(r))
                {
                    throw new ArgumentException(AppText.F("split.rangeEmpty", i + 1));
                }
                dash = r.IndexOf('-');
                if (dash < 0)
                {
                    throw new ArgumentException(AppText.F("split.rangeExpected", i + 1, r));
                }
                t1Str = r.Substring(0, dash);
                t2Str = r.Substring(dash + 1);
                t1 = ParseTime(t1Str, duration);
                t2 = ParseTime(t2Str, duration);
                startF = TimeToFrame(t1.val, t1.isFrame, sourcePts);
                endF = TimeToFrame(t2.val, t2.isFrame, sourcePts);

                // Clamping di start negativi
                if (startF < 0)
                {
                    this.AddWarning(MkvSplitWarningKind.RangeClamped, AppText.F("split.warnRangeStartClamped", i + 1, startF));
                    startF = 0;
                }

                // Clamping di end oltre EOF
                if (endF > totalFrames)
                {
                    this.AddWarning(MkvSplitWarningKind.RangeClamped, AppText.F("split.warnRangeEndClamped", i + 1, endF, totalFrames));
                    endF = totalFrames;
                }

                // Start già oltre EOF: clampato all'ultimo frame
                if (startF >= totalFrames)
                {
                    this.AddWarning(MkvSplitWarningKind.RangeClamped, AppText.F("split.warnRangeStartPastEof", i + 1));
                    startF = totalFrames - 1;
                    endF = totalFrames;
                }

                // Range vuoti o invertiti sono errori hard
                if (startF >= endF)
                {
                    throw new ArgumentException(AppText.F("split.rangeEmptyOrInverted", i + 1, startF, endF));
                }
                result.Add((startF, endF));
            }

            // Warning (non fatale) su overlap di range ordinati
            this.WarnIfOverlapping(result);
            return result;
        }

        /// <summary>Emette un warning se esistono range sovrapposti dopo sort per start.</summary>
        /// <param name="ranges">Lista di coppie (startFrame, endFrame).</param>
        private void WarnIfOverlapping(List<(int, int)> ranges)
        {
            List<(int, int)> sorted;

            sorted = new List<(int, int)>(ranges);
            sorted.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (sorted[i + 1].Item1 < sorted[i].Item2)
                {
                    this.AddWarning(MkvSplitWarningKind.RangeOverlap, AppText.T("split.warnOverlappingRanges"));
                    return;
                }
            }
        }

        #endregion

        #region Costruzione segmenti

        /// <summary>Costruisce la lista dei segmenti in base alla modalità scelta e ritorna anche la MkvSplitMode effettiva.
        /// I capitoli dei segmenti li assegna il planner sui confini effettivi, dopo lo snap.</summary>
        /// <param name="args">Argomenti CLI parsati.</param>
        /// <param name="chapters">Capitoli estratti dal sorgente.</param>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente.</param>
        /// <param name="duration">Durata totale del file in secondi.</param>
        /// <returns>Tupla con la lista dei segmenti e la MkvSplitMode effettiva.</returns>
        public (List<MkvSplitSegment> segments, MkvSplitMode mode) Build(MkvSplitOptions args, List<ChapterMark> chapters, double[] sourcePts, double duration)
        {
            return MkvSplitSegmentStrategyBase.For(args).Build(this, chapters, sourcePts, duration);
        }

        #endregion

        #region Naming

        /// <summary>Variabili ammesse dal template dei nomi di output.</summary>
        private static readonly string[] TEMPLATE_VARIABLES = new string[] { "source", "n", "episode", "chapter", "start", "end", "duration" };

        /// <summary>Applica i nomi ai segmenti usando il template scelto o il default della modalità. L'estensione la mette il programma.</summary>
        /// <param name="segments">Lista dei segmenti a cui assegnare il nome.</param>
        /// <param name="args">Opzioni split del file.</param>
        /// <param name="mode">Modalità di split effettiva.</param>
        /// <param name="inputFile">File di input, da cui si ricava {source}.</param>
        public void ApplyNaming(List<MkvSplitSegment> segments, MkvSplitOptions args, MkvSplitMode mode, string inputFile)
        {
            string template;

            template = !string.IsNullOrEmpty(args.OutputTemplate) ? args.OutputTemplate : DefaultTemplate(mode);
            // Il nome del sorgente si risolve una volta per tutti i segmenti: la disambiguazione D21 legge la cartella
            string sourceName = ResolveSourceName(inputFile);
            foreach (MkvSplitSegment seg in segments)
            {
                seg.File = SanitizeRelativePath(RenderTemplate(template, seg, sourceName, args.StartNumber)) + ".mkv";
            }
        }

        /// <summary>
        /// Valore di {source}: il nome del file senza estensione, oppure con l'estensione d'origine quando nella stessa cartella
        /// c'è un altro container accettato con lo stesso nome base (D21). Il .mkv tiene il nome normale; le maiuscole non contano,
        /// su nessun file system
        /// </summary>
        /// <param name="inputFile">File sorgente</param>
        /// <returns>Nome da usare nel template</returns>
        public static string ResolveSourceName(string inputFile)
        {
            string sourceName = Path.GetFileNameWithoutExtension(inputFile);
            string directory = Path.GetDirectoryName(inputFile);
            if (!MediaContainerFormats.IsMatroska(inputFile) && !string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                foreach (string sibling in Directory.EnumerateFiles(directory, sourceName + ".*", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
                {
                    if (MediaContainerFormats.IsSupported(sibling)
                        && string.Equals(Path.GetFileNameWithoutExtension(sibling), sourceName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(Path.GetFileName(sibling), Path.GetFileName(inputFile), StringComparison.Ordinal))
                    {
                        return Path.GetFileName(inputFile);
                    }
                }
            }

            return sourceName;
        }

        /// <summary>Ritorna il template di default della modalità, senza estensione.</summary>
        /// <param name="mode">Modalità di split.</param>
        /// <returns>Template di default.</returns>
        public static string DefaultTemplate(MkvSplitMode mode)
        {
            switch (mode)
            {
                case MkvSplitMode.Trim: return "{source}_trimmed";
                case MkvSplitMode.ChaptersEach: return "{source}.ch{n:02}";
                case MkvSplitMode.Pattern: return "{source}.E{episode:02}";
                case MkvSplitMode.ChaptersPerEpisode: return "{source}.E{episode:02}";
                default: return "{source}.part{n:02}";
            }
        }

        /// <summary>Valida un template e restituisce gli errori localizzati; un template vuoto è valido e significa "usa il default della modalità".</summary>
        /// <param name="template">Template da validare.</param>
        /// <returns>Lista di errori, vuota se il template è valido.</returns>
        public static List<string> ValidateTemplate(string template)
        {
            List<string> errors = new List<string>();
            int close;
            string token;
            string name;
            string fmt;
            int colon;
            int i;

            if (string.IsNullOrEmpty(template))
            {
                return errors;
            }

            if (Path.IsPathRooted(template))
            {
                errors.Add(AppText.T("split.template.rooted"));
            }
            if (template.Contains(".."))
            {
                errors.Add(AppText.T("split.template.parentPath"));
            }

            i = 0;
            while (i < template.Length)
            {
                if (template[i] != '{')
                {
                    i++;
                    continue;
                }

                close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    errors.Add(AppText.T("split.template.unclosed"));
                    break;
                }

                token = template.Substring(i + 1, close - i - 1);
                colon = token.IndexOf(':');
                name = colon < 0 ? token : token.Substring(0, colon);
                fmt = colon < 0 ? null : token.Substring(colon + 1);

                if (Array.IndexOf(TEMPLATE_VARIABLES, name) < 0)
                {
                    errors.Add(AppText.F("split.template.unknownVariable", name, DescribeReplacement(name)));
                }
                else if (fmt != null && fmt != "02")
                {
                    errors.Add(AppText.F("split.template.unsupportedFormat", name, fmt));
                }

                i = close + 1;
            }

            return errors;
        }

        /// <summary>Nomina il sostituto di una variabile della vecchia sintassi, stringa vuota se il nome non è riconoscibile.</summary>
        /// <param name="name">Nome della variabile trovata nel template.</param>
        /// <returns>Testo che nomina il sostituto oppure la lista delle variabili ammesse.</returns>
        private static string DescribeReplacement(string name)
        {
            if (name == "source_name") { return AppText.F("split.template.useInstead", "{source}"); }
            if (name == "chapter_name") { return AppText.F("split.template.useInstead", "{chapter}"); }
            if (name != null && name.Length > 1 && name[0] == 'n' && (name[1] == '+' || name[1] == '-'))
            {
                return AppText.T("split.template.useStartNumber");
            }
            return AppText.F("split.template.allowed", "{" + string.Join("} {", TEMPLATE_VARIABLES) + "}");
        }

        /// <summary>Renderizza il template sostituendo le variabili del set chiuso.</summary>
        /// <param name="template">Template da renderizzare.</param>
        /// <param name="seg">Segmento corrente.</param>
        /// <param name="sourceName">Valore di {source}, da ResolveSourceName.</param>
        /// <param name="startNumber">Numero da cui parte la numerazione degli episodi.</param>
        /// <returns>Stringa renderizzata, ancora da sanificare.</returns>
        private static string RenderTemplate(string template, MkvSplitSegment seg, string sourceName, int startNumber, double? resultDuration = null)
        {
            string chapterName;
            StringBuilder sb;
            int close;
            string token;
            string name;
            string fmt;
            int colon;
            int episode;
            int i;

            chapterName = (seg.Chapters != null && seg.Chapters.Count > 0 && seg.Chapters[0].Name != null) ? seg.Chapters[0].Name : string.Empty;
            episode = startNumber + (seg.Episode > 0 ? seg.Episode : seg.Num) - 1;
            sb = new StringBuilder(template.Length + 32);
            i = 0;

            while (i < template.Length)
            {
                if (template[i] != '{')
                {
                    sb.Append(template[i]);
                    i++;
                    continue;
                }

                close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    throw new FormatException(AppText.T("split.template.unclosed"));
                }

                token = template.Substring(i + 1, close - i - 1);
                colon = token.IndexOf(':');
                name = colon < 0 ? token : token.Substring(0, colon);
                fmt = colon < 0 ? null : token.Substring(colon + 1);

                switch (name)
                {
                    case "source": sb.Append(sourceName); break;
                    case "n": sb.Append(FormatInt(seg.Num, fmt)); break;
                    case "episode": sb.Append(FormatInt(episode, fmt)); break;
                    case "chapter": sb.Append(chapterName); break;
                    case "start": sb.Append(SecsToFilenameTs(seg.StartTs)); break;
                    case "end": sb.Append(SecsToFilenameTs(seg.EndTs)); break;
                    case "duration": sb.Append(SecsToFilenameTs(resultDuration ?? (seg.EndTs - seg.StartTs))); break;
                    default: throw new FormatException(AppText.F("split.template.unknownVariable", name, DescribeReplacement(name)));
                }

                i = close + 1;
            }

            return sb.ToString();
        }

        /// <summary>Naming output multiclip: start/end seguono l'ordine risultato, duration conta tutte le occorrenze.</summary>
        /// <param name="sourceName">Valore di {source}, da ResolveSourceName: si risolve una volta per tutti gli output.</param>
        public static string RenderOutputName(MkvSplitOptions options, MkvSplitMode mode, string sourceName,
            int number, double start, double end, double duration, List<ChapterMark> chapters)
        {
            MkvSplitSegment segment = new MkvSplitSegment { Num = number, Episode = number,
                StartTs = start, EndTs = end, Chapters = chapters };
            string template = string.IsNullOrEmpty(options.OutputTemplate) ? DefaultTemplate(mode) : options.OutputTemplate;
            return SanitizeRelativePath(RenderTemplate(template, segment, sourceName, options.StartNumber, duration)) + ".mkv";
        }

        /// <summary>Formatta un intero: nessun padding, oppure "02" per lo zero-pad a due cifre.</summary>
        /// <param name="value">Valore da formattare.</param>
        /// <param name="fmt">Format spec, null oppure "02".</param>
        /// <returns>Stringa formattata.</returns>
        private static string FormatInt(int value, string fmt)
        {
            if (string.IsNullOrEmpty(fmt))
            {
                return value.ToString(CultureInfo.InvariantCulture);
            }
            if (fmt == "02")
            {
                return value.ToString("D2", CultureInfo.InvariantCulture);
            }
            throw new FormatException(AppText.F("split.template.unsupportedFormat", "", fmt));
        }

        /// <summary>Converte secondi in una forma leggibile e valida come nome di file, ad esempio 00h21m40s.</summary>
        /// <param name="s">Secondi da formattare.</param>
        /// <returns>Stringa nel formato HHhMMmSSs.</returns>
        public static string SecsToFilenameTs(double s)
        {
            int h;
            int m;
            int sec;

            if (s < 0.0) { s = 0.0; }
            h = (int)(s / 3600.0);
            m = (int)((s - h * 3600.0) / 60.0);
            sec = (int)(s - h * 3600.0 - m * 60.0);
            return string.Format(CultureInfo.InvariantCulture, "{0:D2}h{1:D2}m{2:D2}s", h, m, sec);
        }

        /// <summary>Sanifica il nome reso dal template conservando le barre come separatori di sottocartella.</summary>
        /// <param name="rendered">Nome reso dal template.</param>
        /// <returns>Percorso relativo alla cartella di output.</returns>
        private static string SanitizeRelativePath(string rendered)
        {
            string[] parts;
            List<string> clean = new List<string>();
            StringBuilder sb;
            string component;

            parts = rendered.Split('/', '\\');
            foreach (string part in parts)
            {
                sb = new StringBuilder(part.Length);
                for (int i = 0; i < part.Length; i++)
                {
                    sb.Append(FORBIDDEN_FS_CHARS.IndexOf(part[i]) >= 0 ? '_' : part[i]);
                }

                // Spazi e punti finali rendono il nome inutilizzabile su Windows e invisibile altrove
                component = sb.ToString().TrimEnd(' ', '.');
                if (!string.IsNullOrEmpty(component))
                {
                    clean.Add(component);
                }
            }

            return clean.Count > 0 ? string.Join(Path.DirectorySeparatorChar.ToString(), clean) : "segment";
        }

        #endregion

        #region Snap a keyframe

        /// <summary>Sposta i confini di taglio sul keyframe scelto. Un confine condiviso fra due segmenti adiacenti si muove una sola volta, aggiornando insieme la fine del precedente e l'inizio del successivo: il partizionamento resta senza perdite né duplicati.</summary>
        /// <param name="segments">Lista dei segmenti da aggiornare in place.</param>
        /// <param name="frameMap">Mappa dei frame con flag keyframe.</param>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente.</param>
        /// <param name="mode">Strategia di snap scelta.</param>
        public void ApplySnap(List<MkvSplitSegment> segments, List<MkvSplitFrameInfo> frameMap, double[] sourcePts, MkvSplitSnapMode mode)
        {
            List<int> boundaries;
            List<MkvSplitSegment> opening;
            List<MkvSplitSegment> closing;
            List<MkvSplitSegment> moved;
            int? newB;
            bool eatsSegment;

            moved = new List<MkvSplitSegment>();
            if (mode == MkvSplitSnapMode.Off) { return; }
            MkvSplitKeyframeIndex keyframes = new MkvSplitKeyframeIndex(frameMap);

            // I confini candidati sono gli inizi dei segmenti: la fine di un segmento non richiede
            // un keyframe per decodificare, e quando è condivisa coincide già con l'inizio del successivo.
            boundaries = new List<int>();
            foreach (MkvSplitSegment seg in segments)
            {
                if (!boundaries.Contains(seg.StartFrame)) { boundaries.Add(seg.StartFrame); }
            }
            boundaries.Sort();

            foreach (int boundary in boundaries)
            {
                if (boundary < 0 || boundary >= frameMap.Count) { continue; }
                if (frameMap[boundary].Key) { continue; }

                newB = FindSnapTarget(keyframes, boundary, mode);
                if (newB == null)
                {
                    this.AddWarning(MkvSplitWarningKind.SnapNoKeyframe, AppText.F("split.warnSnapNoKeyframe", boundary));
                    continue;
                }
                if (newB.Value == boundary) { continue; }

                // Il confine si sposta solo se nessuno dei segmenti che tocca verrebbe annullato
                opening = new List<MkvSplitSegment>();
                closing = new List<MkvSplitSegment>();
                eatsSegment = false;
                foreach (MkvSplitSegment seg in segments)
                {
                    if (seg.StartFrame == boundary)
                    {
                        opening.Add(seg);
                        if (newB.Value >= seg.StartFrame + seg.FrameCount) { eatsSegment = true; }
                    }
                    else if (seg.StartFrame + seg.FrameCount == boundary)
                    {
                        closing.Add(seg);
                        if (newB.Value <= seg.StartFrame) { eatsSegment = true; }
                    }
                }

                if (eatsSegment)
                {
                    this.AddWarning(MkvSplitWarningKind.SnapEatSegment, AppText.F("split.warnSnapEatSegment", opening.Count > 0 ? opening[0].Num : closing[0].Num));
                    continue;
                }

                // Il confine chiesto resta sul segmento: i capitoli fra il confine chiesto e quello effettivo lo seguono anche nel documento
                foreach (MkvSplitSegment seg in closing)
                {
                    seg.FrameCount = newB.Value - seg.StartFrame;
                    seg.EndTs = newB.Value < sourcePts.Length ? sourcePts[newB.Value] : seg.EndTs;
                    seg.RequestedEndFrame = boundary;
                }
                foreach (MkvSplitSegment seg in opening)
                {
                    seg.FrameCount = seg.StartFrame + seg.FrameCount - newB.Value;
                    seg.StartFrame = newB.Value;
                    seg.StartTs = sourcePts[newB.Value];
                    seg.RequestedStartFrame = boundary;
                }

                // Ogni confine spostato apre almeno un segmento: i confini candidati sono gli inizi
                moved.Add(opening[0]);
            }

            if (moved.Count > 0)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Info, AppText.T("split.snapApplied"));
                foreach (MkvSplitSegment seg in moved)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.snapBoundary", seg.RequestedStartFrame.Value, seg.StartFrame, seg.StartFrame - seg.RequestedStartFrame.Value));
                }
            }
        }

        /// <summary>Sceglie il keyframe verso cui spostare un confine secondo la strategia; a pari distanza vince quello prima.</summary>
        /// <param name="keyframes">Keyframe della mappa dei frame.</param>
        /// <param name="boundary">Frame del confine richiesto.</param>
        /// <param name="mode">Strategia di snap.</param>
        /// <returns>Frame del keyframe scelto, oppure null se non ne esiste uno nella direzione richiesta.</returns>
        private static int? FindSnapTarget(MkvSplitKeyframeIndex keyframes, int boundary, MkvSplitSnapMode mode)
        {
            int before = mode == MkvSplitSnapMode.After ? -1 : keyframes.Previous(boundary - 1);
            int after = mode == MkvSplitSnapMode.Before ? -1 : keyframes.Next(boundary + 1, keyframes.Count);

            if (before < 0 && after < 0) { return null; }
            if (before < 0) { return after; }
            if (after < 0) { return before; }
            return (boundary - before) <= (after - boundary) ? before : after;
        }

        #endregion
    }
}
