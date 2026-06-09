using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;

namespace RemuxForge.Core.Splitting
{
    /// <summary>Costruzione dei segmenti: parsing range CLI, grouping per pattern/capitoli, naming dei file, snap a keyframe.</summary>
    public class MkvSplitSegmentService
    {
        #region Costanti

        /// <summary>Caratteri non ammessi nei filename (sostituiti con underscore).</summary>
        private const string FORBIDDEN_FS_CHARS = "\\/:*?\"<>|";

        #endregion

        #region Costruttore

        /// <summary>Costruttore vuoto: il servizio è stateless.</summary>
        public MkvSplitSegmentService()
        {
        }

        #endregion

        #region Utility tempo (static, pubbliche perché usate anche da EpisodeSplitter)

        /// <summary>Converte secondi in stringa HH:MM:SS.mmm.</summary>
        /// <param name="s">Numero di secondi da formattare.</param>
        /// <returns>Stringa nel formato HH:MM:SS.mmm.</returns>
        public static string SecsToTs(double s)
        {
            int h;
            int m;
            double sec;

            h = (int)(s / 3600.0);
            m = (int)((s - h * 3600.0) / 60.0);
            sec = s - h * 3600.0 - m * 60.0;
            return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:00.000}", h, m, sec);
        }

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
            if (s.Length > 0 && (s[0] == 'f' || s[0] == 'F'))
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
            return BisectLeft(sourcePts, value);
        }

        /// <summary>Equivalente di Python bisect.bisect_left su array di double: ritorna il minimo indice i tale che arr[i] &gt;= value.</summary>
        /// <param name="arr">Array ordinato crescente.</param>
        /// <param name="value">Valore da inserire.</param>
        /// <returns>Minimo indice i tale che arr[i] &gt;= value.</returns>
        public static int BisectLeft(double[] arr, double value)
        {
            int lo;
            int hi;
            int mid;

            lo = 0;
            hi = arr.Length;
            while (lo < hi)
            {
                mid = lo + ((hi - lo) >> 1);
                if (arr[mid] < value) { lo = mid + 1; }
                else { hi = mid; }
            }
            return lo;
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

            // Enumerazione delle scorciatoie utilizzate
            shortcuts = new List<string>();
            if (!string.IsNullOrEmpty(args.SplitAt)) { shortcuts.Add("--split-at"); }
            if (!string.IsNullOrEmpty(args.TrimStart)) { shortcuts.Add("--trim-start"); }
            if (!string.IsNullOrEmpty(args.TrimEnd)) { shortcuts.Add("--trim-end"); }
            if (args.ChaptersEach) { shortcuts.Add("--chapters-each"); }

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

            // Trasformazione di --split-at in sequenza di range (0-T1,T1-T2,...,Tn-END)
            if (!string.IsNullOrEmpty(args.SplitAt))
            {
                tokens = new List<string>();
                foreach (string t in args.SplitAt.Split(','))
                {
                    string tt = t.Trim();
                    if (tt.Length > 0) { tokens.Add(tt); }
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
                if (r.Length == 0)
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
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.F("split.warnRangeStartClamped", i + 1, startF));
                    startF = 0;
                }

                // Clamping di end oltre EOF
                if (endF > totalFrames)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.F("split.warnRangeEndClamped", i + 1, endF, totalFrames));
                    endF = totalFrames;
                }

                // Start già oltre EOF: clampato all'ultimo frame
                if (startF >= totalFrames)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.F("split.warnRangeStartPastEof", i + 1));
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
            WarnIfOverlapping(result);
            return result;
        }

        /// <summary>Emette un warning se esistono range sovrapposti dopo sort per start.</summary>
        /// <param name="ranges">Lista di coppie (startFrame, endFrame).</param>
        private static void WarnIfOverlapping(List<(int, int)> ranges)
        {
            List<(int, int)> sorted;

            sorted = new List<(int, int)>(ranges);
            sorted.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (sorted[i + 1].Item1 < sorted[i].Item2)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.T("split.warnOverlappingRanges"));
                    return;
                }
            }
        }

        #endregion

        #region Costruzione segmenti

        /// <summary>Costruisce la lista dei segmenti in base alla modalità scelta e ritorna anche la MkvSplitMode effettiva.</summary>
        /// <param name="args">Argomenti CLI parsati.</param>
        /// <param name="chapters">Capitoli estratti dal sorgente.</param>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente.</param>
        /// <param name="duration">Durata totale del file in secondi.</param>
        /// <returns>Tupla con la lista dei segmenti e la MkvSplitMode effettiva.</returns>
        public (List<MkvSplitSegment> segments, MkvSplitMode mode) Build(MkvSplitOptions args, List<MkvSplitChapter> chapters, double[] sourcePts, double duration)
        {
            int totalFrames;
            int nCh;
            List<MkvSplitSegment> segments;
            double startTs;
            double endTs;
            int startF;
            int endF;
            MkvSplitSegment seg;
            List<(int startFrame, int endFrame)> frameRanges;
            MkvSplitMode mode;
            List<MkvSplitChapter> segChapters;
            int[] pattern;
            int sum;
            int chIdx;
            int numCh;
            List<MkvSplitChapter> epChs;
            int epNum;
            int startIdx;
            int endIdx;
            int frameCount;

            totalFrames = sourcePts.Length;

            // Modalità ChaptersEach: un segmento per capitolo
            if (args.ChaptersEach)
            {
                if (chapters == null || chapters.Count == 0)
                {
                    throw new ArgumentException(AppText.T("split.chaptersEachRequiresChapters"));
                }
                nCh = chapters.Count;
                segments = new List<MkvSplitSegment>(nCh);

                // Ogni capitolo diventa un segmento; l'ultimo chiude a duration
                for (int i = 0; i < nCh; i++)
                {
                    startTs = chapters[i].Timestamp;
                    endTs = (i + 1 < nCh) ? chapters[i + 1].Timestamp : duration;
                    startF = BisectLeft(sourcePts, startTs);
                    endF = (i + 1 < nCh) ? BisectLeft(sourcePts, endTs) : totalFrames;
                    seg = new MkvSplitSegment();
                    seg.Num = i + 1;
                    seg.StartTs = startTs;
                    seg.EndTs = endTs;
                    seg.StartFrame = startF;
                    seg.FrameCount = endF - startF;
                    seg.Chapters = new List<MkvSplitChapter>();
                    seg.Chapters.Add(chapters[i]);
                    segments.Add(seg);
                }
                return (segments, MkvSplitMode.ChaptersEach);
            }

            // Modalità Ranges: può diventare Trim se c'è un solo range
            if (!string.IsNullOrEmpty(args.Ranges))
            {
                frameRanges = this.ParseRanges(args.Ranges, sourcePts, duration);
                mode = (frameRanges.Count == 1) ? MkvSplitMode.Trim : MkvSplitMode.Ranges;
                segments = new List<MkvSplitSegment>(frameRanges.Count);

                // Ogni range diventa un segmento; i capitoli del range vengono inclusi
                for (int i = 0; i < frameRanges.Count; i++)
                {
                    startF = frameRanges[i].startFrame;
                    endF = frameRanges[i].endFrame;
                    startTs = sourcePts[startF];
                    endTs = (endF < totalFrames) ? sourcePts[endF] : duration;
                    segChapters = new List<MkvSplitChapter>();
                    foreach (MkvSplitChapter c in chapters)
                    {
                        if (c.Timestamp >= startTs && c.Timestamp < endTs) { segChapters.Add(c); }
                    }
                    seg = new MkvSplitSegment();
                    seg.Num = i + 1;
                    seg.StartTs = startTs;
                    seg.EndTs = endTs;
                    seg.StartFrame = startF;
                    seg.FrameCount = endF - startF;
                    seg.Chapters = segChapters;
                    segments.Add(seg);
                }
                return (segments, mode);
            }

            // Modalita Pattern: raggruppa i capitoli secondo --pattern
            nCh = chapters != null ? chapters.Count : 0;
            if (string.IsNullOrEmpty(args.Pattern))
            {
                PrintNoModeSelected(chapters, nCh);
                throw new ArgumentException(AppText.T("split.noModeSelectedShort"));
            }

            pattern = ParsePattern(args.Pattern);

            // Il pattern deve sommare al numero di capitoli del file
            sum = 0;
            for (int i = 0; i < pattern.Length; i++) { sum += pattern[i]; }
            if (sum != nCh)
            {
                throw new ArgumentException(AppText.F("split.patternSumMismatch", sum, nCh));
            }

            ConsoleHelper.Write(LogSection.Split, LogLevel.Info, AppText.F("split.patternSummary", string.Join(",", pattern), pattern.Length));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "");

            // Costruzione degli episodi secondo il pattern di capitoli
            segments = new List<MkvSplitSegment>(pattern.Length);
            chIdx = 0;
            for (int epI = 0; epI < pattern.Length; epI++)
            {
                numCh = pattern[epI];
                epChs = chapters.GetRange(chIdx, numCh);
                startTs = epChs[0].Timestamp;
                endTs = (chIdx + numCh < nCh) ? chapters[chIdx + numCh].Timestamp : duration;
                epNum = epI + 1;

                // Range frame reale del blocco di capitoli (O(log N) per ciascun episodio).
                startIdx = BisectLeft(sourcePts, startTs);
                endIdx = (chIdx + numCh >= nCh) ? sourcePts.Length : BisectLeft(sourcePts, endTs);
                frameCount = endIdx - startIdx;

                seg = new MkvSplitSegment();
                seg.Num = epI + 1;
                seg.Episode = epNum;
                seg.StartTs = startTs;
                seg.EndTs = endTs;
                seg.StartFrame = startIdx;
                seg.FrameCount = frameCount;
                seg.Chapters = epChs;
                segments.Add(seg);

                chIdx += numCh;
            }
            return (segments, MkvSplitMode.Pattern);
        }

        /// <summary>Stampa un errore esplicativo quando l'utente non ha scelto una modalità di split.</summary>
        /// <param name="chapters">Capitoli del sorgente per suggerire pattern validi.</param>
        /// <param name="nCh">Numero di capitoli del sorgente.</param>
        private static void PrintNoModeSelected(List<MkvSplitChapter> chapters, int nCh)
        {
            ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.T("split.noModeSelected"));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.fileHasChapters", nCh));
            for (int i = 0; i < nCh; i++)
            {
                string chName = chapters[i].Name ?? string.Empty;
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.chapterLine", i + 1, chapters[i].TsStr, chName));
            }
        }

        /// <summary>Parsa un pattern del tipo "5,5,5,6" in un array di interi.</summary>
        /// <param name="patternStr">Stringa con gli interi separati da virgola.</param>
        /// <returns>Array di interi parsati.</returns>
        private static int[] ParsePattern(string patternStr)
        {
            string[] parts;
            int[] p;

            parts = patternStr.Split(',');
            p = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                p[i] = int.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
            }
            return p;
        }

        #endregion

        #region Naming

        /// <summary>Applica i nomi ai segmenti usando --output-template oppure il default per la modalita.</summary>
        /// <param name="segments">Lista dei segmenti a cui assegnare il nome.</param>
        /// <param name="args">Argomenti CLI parsati.</param>
        /// <param name="mode">Modalità di split effettiva.</param>
        /// <param name="inputFile">File di input (serve per {source_name}).</param>
        public void ApplyNaming(List<MkvSplitSegment> segments, MkvSplitOptions args, MkvSplitMode mode, string inputFile)
        {
            string template;

            // Rendering del template custom o del default della modalita
            template = !string.IsNullOrEmpty(args.OutputTemplate) ? args.OutputTemplate : DefaultTemplate(mode);
            foreach (MkvSplitSegment s in segments)
            {
                s.File = SanitizeFilename(RenderTemplate(template, s, inputFile));
            }
        }

        /// <summary>Ritorna il template di default per la modalità.</summary>
        /// <param name="mode">Modalità di split.</param>
        /// <returns>Template di default per la modalità.</returns>
        private static string DefaultTemplate(MkvSplitMode mode)
        {
            switch (mode)
            {
                case MkvSplitMode.Trim: return "{source_name}_trimmed.mkv";
                case MkvSplitMode.ChaptersEach: return "{source_name}.ch{n:02d}.mkv";
                default: return "{source_name}.part{n:02d}.mkv";
            }
        }

        /// <summary>Renderizza un template Python-style con variabili {n}, {n+offset}, {source_name}, {start}, {end}, {chapter_name}.</summary>
        /// <param name="template">Template string da renderizzare.</param>
        /// <param name="seg">Segmento corrente per il rendering delle variabili.</param>
        /// <param name="inputFile">File di input (serve per {source_name}).</param>
        /// <returns>Stringa renderizzata.</returns>
        private static string RenderTemplate(string template, MkvSplitSegment seg, string inputFile)
        {
            string sourceName;
            string chapterName;
            StringBuilder sb;
            int close;
            string token;
            string name;
            string fmt;
            int colon;
            char c;
            int i;

            sourceName = Path.GetFileNameWithoutExtension(inputFile);
            chapterName = (seg.Chapters != null && seg.Chapters.Count > 0 && seg.Chapters[0].Name != null) ? seg.Chapters[0].Name : string.Empty;
            sb = new StringBuilder(template.Length + 32);
            i = 0;

            // Scansione sequenziale del template: ogni "{...}" è un placeholder
            while (i < template.Length)
            {
                c = template[i];
                if (c == '{')
                {
                    close = template.IndexOf('}', i + 1);
                    if (close < 0) { throw new FormatException("Unclosed '{' in template."); }
                    token = template.Substring(i + 1, close - i - 1);

                    // Separazione di nome e format spec sul ':' interno
                    colon = token.IndexOf(':');
                    if (colon < 0) { name = token; fmt = null; }
                    else { name = token.Substring(0, colon); fmt = token.Substring(colon + 1); }

                    // Sostituzione in base al nome della variabile
                    switch (name)
                    {
                        case "n": sb.Append(FormatInt(seg.Num, fmt)); break;
                        case "source_name": sb.Append(sourceName); break;
                        case "start": sb.Append(SecsToTs(seg.StartTs)); break;
                        case "end": sb.Append(SecsToTs(seg.EndTs)); break;
                        case "chapter_name": sb.Append(chapterName); break;
                        default:
                            if (TryRenderNumberExpression(name, seg.Num, fmt, out string renderedNumber))
                            {
                                sb.Append(renderedNumber);
                            }
                            else
                            {
                                throw new FormatException("Unknown template variable '{" + name + "}'.");
                            }
                            break;
                    }
                    i = close + 1;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            return sb.ToString();
        }

        /// <summary>Renderizza espressioni numeriche semplici basate su n, ad esempio n+213 o n-1.</summary>
        /// <param name="name">Nome token senza format spec.</param>
        /// <param name="segmentNumber">Numero segmento 1-based.</param>
        /// <param name="fmt">Format spec opzionale.</param>
        /// <param name="rendered">Risultato renderizzato.</param>
        /// <returns>True se il token e' una espressione numerica supportata.</returns>
        private static bool TryRenderNumberExpression(string name, int segmentNumber, string fmt, out string rendered)
        {
            int signIndex;
            int offset;
            string offsetText;

            rendered = "";
            if (name == null || name.Length < 3 || name[0] != 'n')
            {
                return false;
            }

            signIndex = 1;
            if (name[signIndex] != '+' && name[signIndex] != '-')
            {
                return false;
            }

            offsetText = name.Substring(signIndex + 1);
            if (offsetText.Length == 0 || !int.TryParse(offsetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset))
            {
                throw new FormatException("Invalid numeric offset in template variable '{" + name + "}'.");
            }

            if (name[signIndex] == '-')
            {
                offset = -offset;
            }

            rendered = FormatInt(segmentNumber + offset, fmt);
            return true;
        }

        /// <summary>Formatta un intero secondo uno spec Python-style; supporta solo "d" e "0Nd" (zero-pad a N cifre).</summary>
        /// <param name="value">Valore intero da formattare.</param>
        /// <param name="fmt">Format spec Python-style.</param>
        /// <returns>Stringa formattata dell'intero.</returns>
        private static string FormatInt(int value, string fmt)
        {
            string width;
            int w;

            if (string.IsNullOrEmpty(fmt))
            {
                return value.ToString(CultureInfo.InvariantCulture);
            }
            if (fmt.EndsWith("d", StringComparison.Ordinal))
            {
                width = fmt.Substring(0, fmt.Length - 1);
                if (width.StartsWith("0", StringComparison.Ordinal) && width.Length > 1)
                {
                    width = width.Substring(1);
                }
                int.TryParse(width, NumberStyles.Integer, CultureInfo.InvariantCulture, out w);
                return value.ToString("D" + w.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            }
            return value.ToString(fmt, CultureInfo.InvariantCulture);
        }

        /// <summary>Sostituisce i caratteri non ammessi nel filename con underscore.</summary>
        /// <param name="name">Nome proposto.</param>
        /// <returns>Nome con i caratteri proibiti sostituiti da underscore.</returns>
        private static string SanitizeFilename(string name)
        {
            StringBuilder sb;
            char c;

            sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                c = name[i];
                sb.Append(FORBIDDEN_FS_CHARS.IndexOf(c) >= 0 ? '_' : c);
            }
            return sb.ToString();
        }

        #endregion

        #region Snap a keyframe

        /// <summary>Sposta l'inizio di ogni segmento al keyframe scelto secondo la strategia; l'end frame resta invariato.</summary>
        /// <param name="segments">Lista dei segmenti da aggiornare in place.</param>
        /// <param name="frameMap">Mappa dei frame con flag keyframe.</param>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente.</param>
        /// <param name="mode">Strategia di snap scelta.</param>
        public void ApplySnap(List<MkvSplitSegment> segments, List<MkvSplitFrameInfo> frameMap, double[] sourcePts, MkvSplitSnapMode mode)
        {
            List<(int num, int oldS, int newS)> changed;
            int s;
            int? before;
            int? after;
            int? newS;
            int oldEnd;
            List<(int start, int end, int num)> boundaries;

            if (mode == MkvSplitSnapMode.Off) { return; }

            changed = new List<(int, int, int)>();

            // Per ogni segmento, se lo start non è già un keyframe, prova a spostarlo
            foreach (MkvSplitSegment seg in segments)
            {
                s = seg.StartFrame;
                if (frameMap[s].Key) { continue; }

                // Scelta del nuovo start in base alla strategia
                if (mode == MkvSplitSnapMode.Before)
                {
                    newS = s > 0 ? FindKeyframe(frameMap, s - 1, true) : null;
                }
                else if (mode == MkvSplitSnapMode.After)
                {
                    newS = s + 1 < frameMap.Count ? FindKeyframe(frameMap, s + 1, false) : null;
                }
                else
                {
                    // Nearest: il più vicino in distanza di frame
                    before = s > 0 ? FindKeyframe(frameMap, s - 1, true) : null;
                    after = s + 1 < frameMap.Count ? FindKeyframe(frameMap, s + 1, false) : null;
                    if (before == null) { newS = after; }
                    else if (after == null) { newS = before; }
                    else { newS = (s - before.Value) <= (after.Value - s) ? before : after; }
                }

                // Se non c'è nessun keyframe valido, lascio il segmento invariato
                if (newS == null || newS.Value == s) { continue; }

                // Se il nuovo start supererebbe la fine del segmento, skip
                oldEnd = s + seg.FrameCount;
                if (newS.Value >= oldEnd)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.F("split.warnSnapEatSegment", seg.Num));
                    continue;
                }

                // Aggiornamento del segmento mantenendo la stessa fine
                seg.FrameCount = oldEnd - newS.Value;
                seg.StartFrame = newS.Value;
                seg.StartTs = sourcePts[newS.Value];
                changed.Add((seg.Num, s, newS.Value));
            }

            // Log delle modifiche e warning in caso di overlap creati dallo snap
            if (changed.Count > 0)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Info, AppText.T("split.snapApplied"));
                foreach ((int num, int oldS, int newS) ch in changed)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.snapSegment", ch.num, ch.oldS, ch.newS, ch.newS - ch.oldS));
                }
                boundaries = new List<(int, int, int)>();
                foreach (MkvSplitSegment x in segments)
                {
                    boundaries.Add((x.StartFrame, x.StartFrame + x.FrameCount, x.Num));
                }
                boundaries.Sort((a, b) => a.start.CompareTo(b.start));
                for (int i = 0; i < boundaries.Count - 1; i++)
                {
                    if (boundaries[i + 1].start < boundaries[i].end)
                    {
                        ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.F("split.warnSnapOverlap", boundaries[i].num, boundaries[i + 1].num));
                        break;
                    }
                }
            }
        }

        /// <summary>Cerca il keyframe più vicino in una direzione (prima=true, dopo=false).</summary>
        /// <param name="frameMap">Mappa dei frame con flag keyframe.</param>
        /// <param name="start">Indice iniziale della ricerca.</param>
        /// <param name="goBefore">True per cercare a ritroso, false per cercare avanti.</param>
        /// <returns>Indice del keyframe trovato oppure null se nessuno.</returns>
        private static int? FindKeyframe(List<MkvSplitFrameInfo> frameMap, int start, bool goBefore)
        {
            if (goBefore)
            {
                for (int i = start; i >= 0; i--) { if (frameMap[i].Key) { return i; } }
            }
            else
            {
                for (int i = start; i < frameMap.Count; i++) { if (frameMap[i].Key) { return i; } }
            }
            return null;
        }

        #endregion
    }
}
