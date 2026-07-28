using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;

namespace RemuxForge.Core.Splitting
{
    /// <summary>Produce i file di output per ogni singolo segmento; espone sia la pipeline slow (byte-perfect via raw) sia quella fast (ffmpeg seek + stream copy).</summary>
    public class MkvSplitExecutor
    {
        #region Costanti

        /// <summary>Buffer usato per le copie streaming (1 MB).</summary>
        private const int BUF_SIZE = 1 << 20;

        /// <summary>Profondità massima della ricerca del keyframe successivo allo start (in frame).</summary>
        private const int KEYFRAME_LOOKAHEAD = 500;

        #endregion

        #region Variabili di classe

        /// <summary>Regex che riconosce i nomi di capitolo generici "MkvSplitChapter NN" (rinumerati in output).</summary>
        private static readonly Regex s_genericChapterNameRe = new Regex(@"^\s*chapter\s*\d+\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        #endregion

        #region Costruttore

        /// <summary>Costruttore vuoto: il servizio è stateless.</summary>
        public MkvSplitExecutor()
        {
        }

        #endregion

        #region Pipeline slow (byte-perfect via raw)

        /// <summary>Slow path: usa il raw bitstream per tagliare byte-perfect, ri-codifica il GOP iniziale se lo start non è un keyframe.</summary>
        /// <param name="seg">Segmento da produrre.</param>
        /// <param name="inputFile">File MKV originale (da cui estrarre audio/sub).</param>
        /// <param name="rawFile">Bitstream video raw già estratto.</param>
        /// <param name="frameMap">Mappa dei packet del raw (posizione + keyflag).</param>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente.</param>
        /// <param name="headArgs">Argomenti ffmpeg per la ri-codifica del GOP iniziale.</param>
        /// <param name="codec">MkvSplitCodec video canonico.</param>
        /// <param name="outputFile">File MKV di output.</param>
        /// <param name="tempDir">Directory temporanea dedicata al segmento.</param>
        public void SplitSlow(MkvSplitSegment seg, string inputFile, string rawFile, List<MkvSplitFrameInfo> frameMap, double[] sourcePts, List<string> headArgs, MkvSplitCodec codec, string outputFile, string tempDir)
        {
            int epStartFrame;
            int epFrameCount;
            int epEndFrame;
            bool startIsKey;
            string tcFile;
            string videoBs;
            long startByte;
            long endByte;
            long totalBytes;
            int kfAfter;
            int lookAhead;
            int headCount;
            int restStart;
            int restCount;
            int kfBefore;
            int headEndFrame;
            int localSkip;
            List<(int size, long pos)> reencFrames;
            string headFile;
            string parameterSetsFile;
            long restStartByte;
            long restEndByte;
            long restBytes;
            string restFile;
            string videoMkv;
            int actual;
            string avFile;
            int avExit;
            bool hasAv;
            bool hasChapters;
            string chFile;
            List<string> muxCmd;
            double sizeMb;

            // Estrazione dei dati principali dal segmento
            epStartFrame = seg.StartFrame;
            epFrameCount = seg.FrameCount;
            epEndFrame = epStartFrame + epFrameCount - 1;
            startIsKey = frameMap[epStartFrame].Key;

            // Generazione del file timecodes v2 rebasato a 0 dal primo frame del segmento
            tcFile = Path.Combine(tempDir, "timecodes.txt");
            WriteTimecodesFile(tcFile, sourcePts, epStartFrame, epFrameCount);

            // Path del bitstream video del segmento ricostruito (keyframe copy o head re-encoded + rest copy)
            videoBs = Path.Combine(tempDir, "video.bs");

            if (startIsKey)
            {
                // Caso semplice: lo start coincide già con un keyframe quindi posso fare byte copy diretta
                startByte = frameMap[epStartFrame].Pos;
                endByte = frameMap[epEndFrame].Pos + frameMap[epEndFrame].Size;
                totalBytes = endByte - startByte;
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  Copy " + epFrameCount + " frames (" + (totalBytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB) from keyframe");
                ExtractByteRange(rawFile, videoBs, startByte, totalBytes);
            }
            else
            {
                // Caso complesso: head da ricostruire via re-encode all-intra per arrivare a un keyframe valido

                // Ricerca del primo keyframe dopo lo start (entro una finestra ragionevole)
                kfAfter = -1;
                lookAhead = Math.Min(epStartFrame + KEYFRAME_LOOKAHEAD, frameMap.Count);
                for (int i = epStartFrame + 1; i < lookAhead; i++)
                {
                    if (frameMap[i].Key) { kfAfter = i; break; }
                }
                if (kfAfter < 0 || kfAfter > epEndFrame)
                {
                    throw new InvalidOperationException("No keyframe found within episode after frame " + epStartFrame);
                }

                // Suddivisione del segmento: parte head ri-codificata, parte rest copiata byte-exact dal keyframe
                headCount = kfAfter - epStartFrame;
                restStart = kfAfter;
                restCount = epFrameCount - headCount;

                // Ricerca del keyframe precedente: serve come punto di decode valido per la ri-codifica
                kfBefore = epStartFrame;
                for (int i = epStartFrame - 1; i >= 0; i--)
                {
                    if (frameMap[i].Key) { kfBefore = i; break; }
                }

                // Ricodifica della testa direttamente dal container originale: il decoder riceve extradata
                // (SPS/PPS/VPS) e reference corretti, a differenza di un frammento raw isolato.
                headEndFrame = epStartFrame + headCount - 1;
                localSkip = epStartFrame - kfBefore;
                headFile = Path.Combine(tempDir, "head.bs");
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  HEAD: re-encode frames " + epStartFrame + "-" + headEndFrame + " (" + headCount + " frames, skip " + localSkip + " from previous keyframe)");

                ReencodeFrameRange(inputFile, epStartFrame, headEndFrame, headArgs, codec, headFile);

                // Probe del numero di frame ri-codificati: deve combaciare esattamente con la testa richiesta.
                reencFrames = MkvSplitExternalTools.Instance.ProbePacketsSizePos(headFile);
                if (reencFrames.Count != headCount)
                {
                    throw new InvalidOperationException("Re-encoded " + reencFrames.Count + " head frames, expected " + headCount + ".");
                }
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  HEAD: " + headCount + " frames re-encoded");

                // Copia byte-exact del rest dal keyframe kfAfter fino alla fine del segmento
                restStartByte = frameMap[restStart].Pos;
                restEndByte = frameMap[epEndFrame].Pos + frameMap[epEndFrame].Size;
                restBytes = restEndByte - restStartByte;
                restFile = Path.Combine(tempDir, "rest.bs");
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  REST: copy " + restCount + " frames (" + (restBytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB) from keyframe");
                ExtractByteRange(rawFile, restFile, restStartByte, restBytes);

                // Il rest originale richiede i parameter set originali; dopo la HEAD ricodificata
                // il decoder ha in memoria quelli del nuovo encoder.
                parameterSetsFile = Path.Combine(tempDir, "parameter_sets.bs");
                WriteParameterSets(rawFile, codec, parameterSetsFile);

                // Concatenazione binaria di head + parameter sets originali + rest nel bitstream finale del segmento
                ConcatFiles(videoBs, headFile, parameterSetsFile, restFile);
            }

            // Remux del bitstream applicando i timecodes v2 (VFR preservato)
            videoMkv = Path.Combine(tempDir, "video.mkv");
            MkvSplitExternalTools.Instance.RunMkvmerge(new string[] { "-o", videoMkv, "--timestamps", "0:" + tcFile, videoBs });

            // Sanity check sul numero di frame risultanti
            actual = MkvSplitExternalTools.Instance.CountPackets(videoMkv);
            if (actual != epFrameCount)
            {
                throw new InvalidOperationException("Remuxed video has " + actual + " frames, expected " + epFrameCount + ".");
            }
            else
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  Video: " + actual + " frames");
            }

            // Estrazione audio + sottotitoli in un unico container con stream copy (tutte le tracce via -map 0:a? + 0:s?)
            avFile = Path.Combine(tempDir, "av.mkv");
            avExit = MkvSplitExternalTools.Instance.RunFfmpegNoThrow(new string[]
            {
                "-y", "-hide_banner", "-loglevel", "warning",
                "-i", inputFile,
                "-ss", seg.StartTs.ToString("G", CultureInfo.InvariantCulture),
                "-to", seg.EndTs.ToString("G", CultureInfo.InvariantCulture),
                "-map", "0:a?", "-map", "0:s?",
                "-c:a", "copy", "-c:s", "copy", "-vn",
                avFile
            });
            hasAv = avExit == 0 && File.Exists(avFile) && new FileInfo(avFile).Length > 0;
            if (!hasAv)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, "  WARN: audio/subs extraction failed (exit " + avExit + "); muxing video-only.");
            }

            // Generazione del file capitoli rebasato e con nomi generici rinumerati, solo quando presenti
            hasChapters = seg.Chapters != null && seg.Chapters.Count > 0;
            chFile = null;
            if (hasChapters)
            {
                chFile = Path.Combine(tempDir, "chapters.txt");
                WriteChaptersFile(seg.Chapters, seg.StartTs, chFile);
            }

            // Mux finale: video con timecodes + av e capitoli custom quando presenti
            muxCmd = new List<string>();
            muxCmd.Add("-o"); muxCmd.Add(outputFile);
            muxCmd.Add("--no-chapters"); muxCmd.Add(videoMkv);
            if (hasAv)
            {
                muxCmd.Add("--no-video"); muxCmd.Add("--no-chapters"); muxCmd.Add(avFile);
            }
            if (hasChapters)
            {
                muxCmd.Add("--chapters"); muxCmd.Add(chFile);
            }
            MkvSplitExternalTools.Instance.RunMkvmerge(muxCmd);

            // Log finale con dimensione del file prodotto
            sizeMb = new FileInfo(outputFile).Length / 1048576.0;
            ConsoleHelper.Write(LogSection.Split, LogLevel.Success, "  OK " + Path.GetFileName(outputFile) + " (" + sizeMb.ToString("F1", CultureInfo.InvariantCulture) + " MB)");
        }

        #endregion

        #region Pipeline fast (ffmpeg seek + stream copy)

        /// <summary>Fast path: mkvmerge --split parts per default, ffmpeg stream copy solo se il file ha audio FLAC. Se VFR applica timecodes v2.</summary>
        /// <param name="seg">Segmento da elaborare.</param>
        /// <param name="inputFile">File MKV di input.</param>
        /// <param name="outputFile">Path del file di output.</param>
        /// <param name="tempDir">Directory temporanea per i file intermedi.</param>
        /// <param name="isVfr">Se true genera e applica timecodes v2.</param>
        /// <param name="sourcePts">PTS del sorgente (usato solo se isVfr).</param>
        /// <param name="hasFlac">Se true usa ffmpeg per il taglio (mkvmerge non gestisce split di FLAC).</param>
        public void SplitFast(MkvSplitSegment seg, string inputFile, string outputFile, string tempDir, bool isVfr, double[] sourcePts, bool hasFlac)
        {
            string startTc;
            string endTc;
            bool hasChapters;
            string chFile;
            string tcFile;
            List<string> muxArgs;
            double sizeMb;

            startTc = MkvSplitSegmentService.SecsToTs(seg.StartTs);
            endTc = MkvSplitSegmentService.SecsToTs(seg.EndTs);

            // Generazione del file capitoli rebasato, solo quando presenti
            hasChapters = seg.Chapters != null && seg.Chapters.Count > 0;
            chFile = null;
            if (hasChapters)
            {
                chFile = Path.Combine(tempDir, "chapters.txt");
                WriteChaptersFile(seg.Chapters, seg.StartTs, chFile);
            }

            if (hasFlac)
            {
                // FLAC: mkvmerge non supporta split, uso ffmpeg stream copy + remux
                string tempSeg = Path.Combine(tempDir, "seg.mkv");
                List<string> ffArgs = new List<string>();
                ffArgs.Add("-y"); ffArgs.Add("-hide_banner"); ffArgs.Add("-loglevel"); ffArgs.Add("warning");
                ffArgs.Add("-ss"); ffArgs.Add(startTc);
                ffArgs.Add("-to"); ffArgs.Add(endTc);
                ffArgs.Add("-i"); ffArgs.Add(inputFile);
                ffArgs.Add("-map"); ffArgs.Add("0:v?");
                ffArgs.Add("-map"); ffArgs.Add("0:a?");
                ffArgs.Add("-map"); ffArgs.Add("0:s?");
                ffArgs.Add("-c"); ffArgs.Add("copy");
                ffArgs.Add("-avoid_negative_ts"); ffArgs.Add("make_zero");
                ffArgs.Add("-map_chapters"); ffArgs.Add("-1");
                ffArgs.Add(tempSeg);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  ffmpeg fast-seek copy " + startTc + " -> " + endTc);
                MkvSplitExternalTools.Instance.RunFfmpeg(ffArgs);

                muxArgs = new List<string>();
                muxArgs.Add("-o"); muxArgs.Add(outputFile);
                if (isVfr)
                {
                    double[] segPts = MkvSplitExternalTools.Instance.ExtractSourcePts(tempSeg);
                    int tcCount = Math.Min(segPts.Length, sourcePts.Length - seg.StartFrame);
                    tcFile = Path.Combine(tempDir, "timecodes.txt");
                    WriteTimecodesFile(tcFile, sourcePts, seg.StartFrame, tcCount);
                    muxArgs.Add("--timestamps"); muxArgs.Add("0:" + tcFile);
                }
                muxArgs.Add("--no-chapters"); muxArgs.Add(tempSeg);
                if (hasChapters)
                {
                    muxArgs.Add("--chapters"); muxArgs.Add(chFile);
                }
                MkvSplitExternalTools.Instance.RunMkvmerge(muxArgs);
            }
            else
            {
                // Non-FLAC: mkvmerge --split parts per taglio frame-perfect (preserva VFR nativamente)
                muxArgs = new List<string>();
                muxArgs.Add("-o"); muxArgs.Add(outputFile);
                muxArgs.Add("--no-chapters");
                muxArgs.Add("--split"); muxArgs.Add("parts:" + startTc + "-" + endTc);
                muxArgs.Add(inputFile);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  mkvmerge split " + startTc + " -> " + endTc);
                MkvSplitExternalTools.Instance.RunMkvmerge(muxArgs);

                // Capitoli aggiunti post-split con mkvpropedit (modifica header in-place)
                if (hasChapters)
                {
                    MkvSplitExternalTools.Instance.RunMkvpropedit(new string[] { outputFile, "--chapters", chFile });
                }
            }

            sizeMb = new FileInfo(outputFile).Length / 1048576.0;
            ConsoleHelper.Write(LogSection.Split, LogLevel.Success, "  OK " + Path.GetFileName(outputFile) + " (" + sizeMb.ToString("F1", CultureInfo.InvariantCulture) + " MB)");
        }

        #endregion

        #region Helper parameter sets

        /// <summary>Scrive i parameter set originali (H.264 SPS/PPS, HEVC VPS/SPS/PPS) in formato Annex B.</summary>
        /// <param name="rawFile">Elementary stream raw originale.</param>
        /// <param name="codec">MkvSplitCodec del raw.</param>
        /// <param name="outputFile">File di output contenente solo i parameter set.</param>
        private static void WriteParameterSets(string rawFile, MkvSplitCodec codec, string outputFile)
        {
            byte[] data;
            List<(int start, int end, int type)> nals;
            bool hasVps;
            bool hasSps;
            bool hasPps;
            FileStream outF;

            data = File.ReadAllBytes(rawFile);
            nals = FindAnnexBNals(data, codec);
            hasVps = codec == MkvSplitCodec.H264;
            hasSps = false;
            hasPps = false;

            outF = null;
            try
            {
                outF = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None);
                foreach ((int start, int end, int type) nal in nals)
                {
                    if (IsParameterSet(codec, nal.type))
                    {
                        outF.Write(data, nal.start, nal.end - nal.start);
                        if (codec == MkvSplitCodec.H264)
                        {
                            if (nal.type == 7) { hasSps = true; }
                            if (nal.type == 8) { hasPps = true; }
                        }
                        else
                        {
                            if (nal.type == 32) { hasVps = true; }
                            if (nal.type == 33) { hasSps = true; }
                            if (nal.type == 34) { hasPps = true; }
                        }
                    }

                    if (hasVps && hasSps && hasPps) { break; }
                }
            }
            finally
            {
                if (outF != null) { outF.Dispose(); }
            }

            if (!hasVps || !hasSps || !hasPps)
            {
                throw new InvalidOperationException("Could not find complete parameter sets in raw bitstream.");
            }
        }

        /// <summary>Ritorna true se il NAL type è un parameter set per il codec indicato.</summary>
        private static bool IsParameterSet(MkvSplitCodec codec, int nalType)
        {
            if (codec == MkvSplitCodec.H264) { return nalType == 7 || nalType == 8; }
            return nalType == 32 || nalType == 33 || nalType == 34;
        }

        /// <summary>Trova i NAL Annex B nel buffer, includendo lo start code nel range restituito.</summary>
        private static List<(int start, int end, int type)> FindAnnexBNals(byte[] data, MkvSplitCodec codec)
        {
            List<(int start, int end, int type)> nals;
            List<int> starts;
            int pos;
            int sc;
            int type;
            int nalHeader;

            nals = new List<(int, int, int)>();
            starts = new List<int>();
            pos = 0;
            while (pos < data.Length - 3)
            {
                sc = StartCodeLength(data, pos);
                if (sc > 0)
                {
                    starts.Add(pos);
                    pos += sc;
                }
                else
                {
                    pos++;
                }
            }

            for (int i = 0; i < starts.Count; i++)
            {
                int start = starts[i];
                int end = (i + 1 < starts.Count) ? starts[i + 1] : data.Length;
                int payload = start + StartCodeLength(data, start);
                if (payload >= end) { continue; }

                nalHeader = data[payload];
                type = codec == MkvSplitCodec.H264 ? (nalHeader & 31) : ((nalHeader >> 1) & 63);
                nals.Add((start, end, type));
            }
            return nals;
        }

        /// <summary>Ritorna 3 o 4 se in offset c'è uno start code Annex B, altrimenti 0.</summary>
        private static int StartCodeLength(byte[] data, int offset)
        {
            if (offset + 3 <= data.Length && data[offset] == 0 && data[offset + 1] == 0 && data[offset + 2] == 1)
            {
                return 3;
            }
            if (offset + 4 <= data.Length && data[offset] == 0 && data[offset + 1] == 0 && data[offset + 2] == 0 && data[offset + 3] == 1)
            {
                return 4;
            }
            return 0;
        }

        #endregion

        #region Helper re-encode

        /// <summary>Ricodifica un range inclusivo di frame video dal container originale in un elementary stream raw.</summary>
        /// <param name="inputFile">File MKV originale.</param>
        /// <param name="startFrame">Primo frame da includere.</param>
        /// <param name="endFrame">Ultimo frame da includere.</param>
        /// <param name="headArgs">Argomenti encoder già costruiti.</param>
        /// <param name="codec">MkvSplitCodec raw di output.</param>
        /// <param name="outputFile">Elementary stream raw di output.</param>
        private static void ReencodeFrameRange(string inputFile, int startFrame, int endFrame, List<string> headArgs, MkvSplitCodec codec, string outputFile)
        {
            List<string> ffArgs;

            ffArgs = new List<string>();
            ffArgs.Add("-y"); ffArgs.Add("-hide_banner"); ffArgs.Add("-loglevel"); ffArgs.Add("warning");
            ffArgs.Add("-i"); ffArgs.Add(inputFile);
            ffArgs.Add("-map"); ffArgs.Add("0:v:0");
            ffArgs.Add("-vf"); ffArgs.Add("select=between(n\\," + startFrame.ToString(CultureInfo.InvariantCulture) + "\\," + endFrame.ToString(CultureInfo.InvariantCulture) + "),setpts=N/FRAME_RATE/TB");
            foreach (string a in headArgs) { ffArgs.Add(a); }
            ffArgs.Add("-an"); ffArgs.Add("-f"); ffArgs.Add(codec == MkvSplitCodec.Hevc ? "hevc" : "h264"); ffArgs.Add(outputFile);
            MkvSplitExternalTools.Instance.RunFfmpeg(ffArgs);
        }

        #endregion

        #region Helper I/O (privati, riusati dentro SplitSlow)

        /// <summary>Copia un range di byte [startByte, startByte+length) da src a dst con buffer di 1 MB.</summary>
        /// <param name="src">File sorgente.</param>
        /// <param name="dst">File destinazione (sovrascritto).</param>
        /// <param name="startByte">Offset iniziale nel sorgente.</param>
        /// <param name="length">Numero di byte da copiare.</param>
        private static void ExtractByteRange(string src, string dst, long startByte, long length)
        {
            FileStream inF;
            FileStream outF;
            byte[] buf;
            long remaining;
            int toRead;
            int read;

            inF = null;
            outF = null;
            buf = new byte[BUF_SIZE];

            try
            {
                // Apertura input in sola lettura, con hint di accesso sequenziale per il read-ahead del kernel
                inF = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BUF_SIZE, FileOptions.SequentialScan);
                inF.Seek(startByte, SeekOrigin.Begin);

                // Apertura output in sovrascrittura, stessi hint
                outF = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, BUF_SIZE, FileOptions.SequentialScan);

                // Copia a blocchi finché non ho scritto tutti i "length" byte (o EOF)
                remaining = length;
                while (remaining > 0)
                {
                    toRead = (int)Math.Min(remaining, buf.Length);
                    read = inF.Read(buf, 0, toRead);
                    if (read <= 0) { break; }
                    outF.Write(buf, 0, read);
                    remaining -= read;
                }
            }
            finally
            {
                // Dispose esplicito garantito anche in caso di eccezione, senza using nidificati
                if (outF != null) { outF.Dispose(); }
                if (inF != null) { inF.Dispose(); }
            }
        }

        /// <summary>Concatena più file binari nell'ordine sul file di destinazione.</summary>
        /// <param name="dst">File destinazione (sovrascritto).</param>
        /// <param name="sources">File sorgente concatenati nell'ordine.</param>
        private static void ConcatFiles(string dst, params string[] sources)
        {
            FileStream outF;
            FileStream inF;
            byte[] buf;
            int read;

            outF = null;
            inF = null;
            buf = new byte[BUF_SIZE];

            try
            {
                // Apertura output in sovrascrittura
                outF = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, BUF_SIZE, FileOptions.SequentialScan);

                // Iterazione sui file sorgente con append sequenziale su outF
                foreach (string src in sources)
                {
                    inF = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BUF_SIZE, FileOptions.SequentialScan);

                    // Copia blocco per blocco fino a EOF del sorgente corrente
                    while ((read = inF.Read(buf, 0, buf.Length)) > 0)
                    {
                        outF.Write(buf, 0, read);
                    }

                    // Rilascio immediato del file sorgente prima di passare al successivo
                    inF.Dispose();
                    inF = null;
                }
            }
            finally
            {
                // Dispose esplicito garantito in caso di eccezione
                if (inF != null) { inF.Dispose(); }
                if (outF != null) { outF.Dispose(); }
            }
        }

        /// <summary>Scrive il file timecodes v2 rebasato a 0 dal primo frame del segmento.</summary>
        /// <param name="path">File di output.</param>
        /// <param name="sourcePts">PTS del sorgente.</param>
        /// <param name="epStartFrame">Indice del primo frame del segmento.</param>
        /// <param name="epFrameCount">Numero di frame del segmento.</param>
        private static void WriteTimecodesFile(string path, double[] sourcePts, int epStartFrame, int epFrameCount)
        {
            StreamWriter sw;
            double first;
            double rel;

            sw = null;
            try
            {
                // Apertura in sovrascrittura, UTF-8 senza BOM
                sw = new StreamWriter(path, false, new UTF8Encoding(false));
                sw.WriteLine("# timecode format v2");

                // Rebase: il primo frame del segmento diventa il tempo 0
                first = sourcePts[epStartFrame];
                for (int i = 0; i < epFrameCount; i++)
                {
                    rel = (sourcePts[epStartFrame + i] - first) * 1000.0;
                    sw.WriteLine(rel.ToString("0.000", CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                if (sw != null) { sw.Dispose(); }
            }
        }

        #endregion

        #region Helper chapter (privati, riusati da Slow e Fast)

        /// <summary>Scrive il file capitoli in formato "Simple" di mkvmerge, rinumerando i nomi generici "MkvSplitChapter N".</summary>
        /// <param name="chapters">Capitoli del segmento.</param>
        /// <param name="startTs">Timestamp di inizio segmento (per il delta).</param>
        /// <param name="filepath">File di output.</param>
        private static void WriteChaptersFile(List<MkvSplitChapter> chapters, double startTs, string filepath)
        {
            StreamWriter sw;
            MkvSplitChapter ch;
            double rel;
            string name;
            string num;

            sw = null;
            try
            {
                // Apertura in sovrascrittura, UTF-8 senza BOM
                sw = new StreamWriter(filepath, false, new UTF8Encoding(false));

                // Per ogni capitolo: rebase del timestamp + rinumerazione nome se generico
                for (int i = 0; i < chapters.Count; i++)
                {
                    ch = chapters[i];
                    rel = Math.Max(0.0, ch.Timestamp - startTs);

                    // Nome: se vuoto o match "MkvSplitChapter NN" lo rinumero da 1; altrimenti conservo l'originale
                    if (!string.IsNullOrEmpty(ch.Name) && !s_genericChapterNameRe.IsMatch(ch.Name))
                    {
                        name = ch.Name;
                    }
                    else
                    {
                        name = string.Format(CultureInfo.InvariantCulture, "MkvSplitChapter {0:D2}", i + 1);
                    }

                    // Riga CHAPTERxx= e CHAPTERxxNAME= in formato Simple
                    num = (i + 1).ToString("D2", CultureInfo.InvariantCulture);
                    sw.Write("CHAPTER"); sw.Write(num); sw.Write('='); sw.WriteLine(MkvSplitSegmentService.SecsToTs(rel));
                    sw.Write("CHAPTER"); sw.Write(num); sw.Write("NAME="); sw.WriteLine(name);
                }
            }
            finally
            {
                if (sw != null) { sw.Dispose(); }
            }
        }

        #endregion
    }
}
