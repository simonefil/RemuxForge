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

        /// <summary>Registra un avviso nel piano e ne scrive la riga di dettaglio nel log.</summary>
        /// <param name="kind">Categoria dell'avviso.</param>
        /// <param name="message">Testo localizzato.</param>
        /// <param name="segmentNum">Numero del segmento coinvolto, 0 per avvisi di file.</param>
        private void AddWarning(MkvSplitWarningKind kind, string message, int segmentNum)
        {
            this.Warnings.Add(new MkvSplitWarning(kind, message, segmentNum));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, message);
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
                    this.AddWarning(MkvSplitWarningKind.RangeClamped, AppText.F("split.warnRangeStartClamped", i + 1, startF), i + 1);
                    startF = 0;
                }

                // Clamping di end oltre EOF
                if (endF > totalFrames)
                {
                    this.AddWarning(MkvSplitWarningKind.RangeClamped, AppText.F("split.warnRangeEndClamped", i + 1, endF, totalFrames), i + 1);
                    endF = totalFrames;
                }

                // Start già oltre EOF: clampato all'ultimo frame
                if (startF >= totalFrames)
                {
                    this.AddWarning(MkvSplitWarningKind.RangeClamped, AppText.F("split.warnRangeStartPastEof", i + 1), i + 1);
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
                    this.AddWarning(MkvSplitWarningKind.RangeOverlap, AppText.T("split.warnOverlappingRanges"), 0);
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

            // Modalità Manuale: nessun segmento finché non li costruisce l'editor
            if (args.Manual)
            {
                return (new List<MkvSplitSegment>(), MkvSplitMode.Manual);
            }

            // Modalità ChaptersPerEpisode: blocchi di k capitoli, adattandosi a qualunque conteggio
            if (args.ChaptersPerEpisode > 0)
            {
                nCh = chapters != null ? chapters.Count : 0;
                if (nCh == 0)
                {
                    throw new ArgumentException(AppText.T("split.chaptersEachRequiresChapters"));
                }

                segments = new List<MkvSplitSegment>();
                epNum = 0;
                for (chIdx = 0; chIdx < nCh; chIdx += args.ChaptersPerEpisode)
                {
                    numCh = Math.Min(args.ChaptersPerEpisode, nCh - chIdx);
                    epChs = chapters.GetRange(chIdx, numCh);
                    epNum++;
                    startTs = epChs[0].Timestamp;
                    endTs = (chIdx + numCh < nCh) ? chapters[chIdx + numCh].Timestamp : duration;
                    startIdx = BisectLeft(sourcePts, startTs);
                    endIdx = (chIdx + numCh >= nCh) ? totalFrames : BisectLeft(sourcePts, endTs);

                    seg = new MkvSplitSegment();
                    seg.Num = epNum;
                    seg.Episode = epNum;
                    seg.StartTs = startTs;
                    seg.EndTs = endTs;
                    seg.StartFrame = startIdx;
                    seg.FrameCount = endIdx - startIdx;
                    seg.Chapters = epChs;
                    segments.Add(seg);
                }

                // L'ultimo blocco più corto è legittimo ma va dichiarato: cambia la durata dell'ultimo episodio
                if (nCh % args.ChaptersPerEpisode != 0)
                {
                    this.AddWarning(MkvSplitWarningKind.ChapterGrouping, AppText.F("split.warnChaptersPerEpisodeRemainder", args.ChaptersPerEpisode, nCh, nCh % args.ChaptersPerEpisode), epNum);
                }

                return (segments, MkvSplitMode.ChaptersPerEpisode);
            }

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
                    seg.Episode = i + 1;
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
                // --split-at è già stato riscritto in range, ma resta leggibile nelle opzioni: senza
                // questo la griglia direbbe "estrai intervalli" a chi ha chiesto di tagliare a un punto
                if (!string.IsNullOrEmpty(args.SplitAt)) { mode = MkvSplitMode.SplitAt; }
                else { mode = (frameRanges.Count == 1) ? MkvSplitMode.Trim : MkvSplitMode.Ranges; }
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
                    seg.Episode = i + 1;
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
            foreach (MkvSplitSegment seg in segments)
            {
                seg.File = SanitizeRelativePath(RenderTemplate(template, seg, inputFile, args.StartNumber)) + ".mkv";
            }
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
        /// <param name="inputFile">File di input, da cui si ricava {source}.</param>
        /// <param name="startNumber">Numero da cui parte la numerazione degli episodi.</param>
        /// <returns>Stringa renderizzata, ancora da sanificare.</returns>
        private static string RenderTemplate(string template, MkvSplitSegment seg, string inputFile, int startNumber)
        {
            string sourceName;
            string chapterName;
            StringBuilder sb;
            int close;
            string token;
            string name;
            string fmt;
            int colon;
            int episode;
            int i;

            sourceName = Path.GetFileNameWithoutExtension(inputFile);
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
                    case "duration": sb.Append(SecsToFilenameTs(seg.EndTs - seg.StartTs)); break;
                    default: throw new FormatException(AppText.F("split.template.unknownVariable", name, DescribeReplacement(name)));
                }

                i = close + 1;
            }

            return sb.ToString();
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
            List<(int oldB, int newB)> changed;
            int? newB;
            bool eatsSegment;

            if (mode == MkvSplitSnapMode.Off) { return; }

            changed = new List<(int, int)>();

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

                newB = FindSnapTarget(frameMap, boundary, mode);
                if (newB == null)
                {
                    this.AddWarning(MkvSplitWarningKind.SnapNoKeyframe, AppText.F("split.warnSnapNoKeyframe", boundary), 0);
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
                    this.AddWarning(MkvSplitWarningKind.SnapEatSegment, AppText.F("split.warnSnapEatSegment", opening.Count > 0 ? opening[0].Num : closing[0].Num), opening.Count > 0 ? opening[0].Num : closing[0].Num);
                    continue;
                }

                foreach (MkvSplitSegment seg in closing)
                {
                    seg.FrameCount = newB.Value - seg.StartFrame;
                    seg.EndTs = newB.Value < sourcePts.Length ? sourcePts[newB.Value] : seg.EndTs;
                }
                foreach (MkvSplitSegment seg in opening)
                {
                    seg.FrameCount = seg.StartFrame + seg.FrameCount - newB.Value;
                    seg.StartFrame = newB.Value;
                    seg.StartTs = sourcePts[newB.Value];
                }

                changed.Add((boundary, newB.Value));
            }

            if (changed.Count > 0)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Info, AppText.T("split.snapApplied"));
                foreach ((int oldB, int newB) ch in changed)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.snapBoundary", ch.oldB, ch.newB, ch.newB - ch.oldB));
                }
            }
        }

        /// <summary>Sceglie il keyframe verso cui spostare un confine secondo la strategia.</summary>
        /// <param name="frameMap">Mappa dei frame con flag keyframe.</param>
        /// <param name="boundary">Frame del confine richiesto.</param>
        /// <param name="mode">Strategia di snap.</param>
        /// <returns>Frame del keyframe scelto, oppure null se non ne esiste uno nella direzione richiesta.</returns>
        private static int? FindSnapTarget(List<MkvSplitFrameInfo> frameMap, int boundary, MkvSplitSnapMode mode)
        {
            int? before;
            int? after;

            if (mode == MkvSplitSnapMode.Before)
            {
                return boundary > 0 ? FindKeyframe(frameMap, boundary - 1, true) : null;
            }
            if (mode == MkvSplitSnapMode.After)
            {
                return boundary + 1 < frameMap.Count ? FindKeyframe(frameMap, boundary + 1, false) : null;
            }

            before = boundary > 0 ? FindKeyframe(frameMap, boundary - 1, true) : null;
            after = boundary + 1 < frameMap.Count ? FindKeyframe(frameMap, boundary + 1, false) : null;
            if (before == null) { return after; }
            if (after == null) { return before; }
            return (boundary - before.Value) <= (after.Value - boundary) ? before : after;
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
