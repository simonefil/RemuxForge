using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
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
        public const int KEYFRAME_LOOKAHEAD = 500;

        /// <summary>Finestra entro cui cercare il riordino dei B-frame oltre la fine del segmento (in frame).</summary>
        private const int REORDER_LOOKAHEAD = 64;

        #endregion

        #region Variabili di classe

        /// <summary>Access unit delimiter H.264 (NAL type 9, primary_pic_type = I) in formato Annex B.</summary>
        private static readonly byte[] s_h264AccessUnitDelimiter = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x09, 0x10 };

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
        /// <param name="inputFile">File MKV normalizzato, da cui si leggono audio e tracce video secondarie.</param>
        /// <param name="rawFile">Bitstream video raw già estratto.</param>
        /// <param name="frameMap">Mappa dei packet del raw (posizione + keyflag), in ordine di decodifica.</param>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente, cioè in ordine di presentazione.</param>
        /// <param name="presentationToDecode">Indice nel raw di ogni frame, indicizzato per presentazione.</param>
        /// <param name="headArgs">Argomenti ffmpeg per la ri-codifica del GOP iniziale.</param>
        /// <param name="codec">MkvSplitCodec video canonico.</param>
        /// <param name="outputFile">File MKV di output.</param>
        /// <param name="tempDir">Directory temporanea dedicata al segmento.</param>
        /// <param name="tracks">Tracce del file normalizzato, nel loro ordine.</param>
        /// <param name="subtitleIds">Tracce di sottotitoli da tagliare al pacchetto, vuota se non ce ne sono.</param>
        /// <param name="subtitles">Matroska con le sole tracce di subtitleIds, nell'ordine del sorgente; null se non ce ne sono.</param>
        public void SplitSlow(MkvSplitSegment seg, string inputFile, string rawFile, List<MkvSplitFrameInfo> frameMap, double[] sourcePts, int[] presentationToDecode, List<string> headArgs, MkvSplitCodec codec, string outputFile, string tempDir, List<TrackInfo> tracks, List<int> subtitleIds, string subtitles)
        {
            int epStartFrame;
            int epFrameCount;
            int epEndFrame;
            bool startIsKey;
            string videoBs;
            long totalBytes;
            int kfAfter;
            int lookAhead;
            int headCount;
            int restStart;
            int restCount;
            int kfBefore;
            int tailStart;
            string tailFile;
            int headEndFrame;
            int localSkip;
            List<(int size, long pos)> reencFrames;
            string headFile;
            string parameterSetsFile;
            long restBytes;
            string restFile;

            // Estrazione dei dati principali dal segmento: gli indici del segmento sono di presentazione,
            // quelli del raw di decodifica, e la mappa traduce dagli uni agli altri
            epStartFrame = seg.StartFrame;
            epFrameCount = seg.FrameCount;
            epEndFrame = epStartFrame + epFrameCount - 1;
            startIsKey = frameMap[presentationToDecode[epStartFrame]].Key;

            // Path del bitstream video del segmento ricostruito (keyframe copy o head re-encoded + rest copy)
            videoBs = Path.Combine(tempDir, "video.bs");

            if (startIsKey)
            {
                // Caso semplice: lo start coincide già con un keyframe quindi posso fare byte copy diretta.
                // I parameter set vanno comunque anteposti: il range parte dal keyframe, non dall'inizio
                // del raw, e senza header di sequenza mkvmerge non riconosce nemmeno il tipo di file.
                tailStart = FindTailStart(presentationToDecode, epStartFrame, epEndFrame);
                restFile = Path.Combine(tempDir, "rest.bs");
                totalBytes = ExtractPresentationRange(rawFile, frameMap, presentationToDecode, epStartFrame, tailStart - 1, restFile, tempDir);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.copyFrames", tailStart - epStartFrame, (totalBytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture)));
                parameterSetsFile = Path.Combine(tempDir, "parameter_sets.bs");
                WriteRestHeader(rawFile, codec, parameterSetsFile);
                tailFile = BuildTail(inputFile, tailStart, epEndFrame, headArgs, codec, tempDir);
                if (tailFile != null)
                {
                    ConcatFiles(videoBs, parameterSetsFile, restFile, tailFile);
                }
                else
                {
                    ConcatFiles(videoBs, parameterSetsFile, restFile);
                }
            }
            else
            {
                // Caso complesso: head da ricostruire via re-encode all-intra per arrivare a un keyframe valido

                // Ricerca del primo keyframe dopo lo start (entro una finestra ragionevole). La ricerca
                // scorre l'ordine di presentazione: le immagini leading di un GOP aperto stanno nel raw
                // dopo il keyframe ma vanno mostrate prima, e cadono quindi nella testa ricodificata.
                kfAfter = -1;
                lookAhead = Math.Min(epStartFrame + KEYFRAME_LOOKAHEAD, presentationToDecode.Length);
                for (int i = epStartFrame + 1; i < lookAhead; i++)
                {
                    if (frameMap[presentationToDecode[i]].Key) { kfAfter = i; break; }
                }
                if (kfAfter < 0 || kfAfter > epEndFrame)
                {
                    throw new InvalidOperationException(AppText.F("split.exec.noKeyframeInEpisode", epStartFrame));
                }

                // Suddivisione del segmento: testa ricodificata, corpo copiato byte-exact dal keyframe e
                // coda ricodificata quando gli ultimi fotogrammi referenziano un'ancora oltre il taglio
                headCount = kfAfter - epStartFrame;
                restStart = kfAfter;
                tailStart = FindTailStart(presentationToDecode, restStart, epEndFrame);
                restCount = tailStart - restStart;
                if (restCount < 1)
                {
                    throw new InvalidOperationException(AppText.F("split.exec.noKeyframeInEpisode", epStartFrame));
                }

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
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.headReencode", epStartFrame, headEndFrame, headCount, localSkip));

                ReencodeFrameRange(inputFile, epStartFrame, headEndFrame, headArgs, codec, headFile);

                // Probe del numero di frame ri-codificati: deve combaciare esattamente con la testa richiesta.
                reencFrames = MkvSplitExternalTools.Instance.ProbePacketsSizePos(headFile);
                if (reencFrames.Count != headCount)
                {
                    throw new InvalidOperationException(AppText.F("split.exec.headReencodeMismatch", reencFrames.Count, headCount));
                }
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.headDone", headCount));

                // Copia byte-exact del rest, dal keyframe fino all'inizio della coda in ordine di presentazione
                restFile = Path.Combine(tempDir, "rest.bs");
                restBytes = ExtractPresentationRange(rawFile, frameMap, presentationToDecode, restStart, tailStart - 1, restFile, tempDir);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.restCopy", restCount, (restBytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture)));

                // Il rest originale richiede i parameter set originali; dopo la HEAD ricodificata
                // il decoder ha in memoria quelli del nuovo encoder.
                parameterSetsFile = Path.Combine(tempDir, "parameter_sets.bs");
                WriteRestHeader(rawFile, codec, parameterSetsFile);

                // Concatenazione binaria di head + parameter set originali + rest + eventuale coda ricodificata
                tailFile = BuildTail(inputFile, tailStart, epEndFrame, headArgs, codec, tempDir);
                if (tailFile != null)
                {
                    ConcatFiles(videoBs, headFile, parameterSetsFile, restFile, tailFile);
                }
                else
                {
                    ConcatFiles(videoBs, headFile, parameterSetsFile, restFile);
                }
            }

            MuxSegment(seg, inputFile, sourcePts, videoBs, outputFile, tempDir, tracks, subtitleIds, subtitles);
        }

        #endregion

        #region Pipeline a pacchetti (corpo copiato per PTS)

        /// <summary>
        /// Taglio esatto a pacchetti: testa e coda ricodificate con lo stesso codec, corpo copiato selezionando i pacchetti del
        /// sorgente per PTS e parti accodate con mkvmerge. Serve ai codec il cui flusso non si copia a byte da un elementary stream:
        /// MPEG-4 ASP, AV1 e VP9 con parti in Matroska, MS-MPEG4 e H.263 con parti in AVI e il fourcc del sorgente.
        /// </summary>
        /// <param name="seg">Segmento da produrre.</param>
        /// <param name="inputFile">File MKV normalizzato, da cui si leggono video, audio e tracce video secondarie.</param>
        /// <param name="sourcePts">PTS del sorgente ordinati crescente, cioè in ordine di presentazione.</param>
        /// <param name="keyFlags">Keyframe reali del sorgente, in ordine di presentazione.</param>
        /// <param name="presentationToDecode">Posizione in ordine di decodifica di ogni frame, indicizzata per presentazione.</param>
        /// <param name="headArgs">Argomenti ffmpeg della ricodifica di testa e coda.</param>
        /// <param name="codec">Codec video canonico.</param>
        /// <param name="outputFile">File MKV di output.</param>
        /// <param name="tempDir">Directory temporanea dedicata al segmento.</param>
        /// <param name="tracks">Tracce del file normalizzato, nel loro ordine.</param>
        /// <param name="subtitleIds">Tracce di sottotitoli da tagliare al pacchetto, vuota se non ce ne sono.</param>
        /// <param name="subtitles">Matroska con le sole tracce di subtitleIds, nell'ordine del sorgente; null se non ce ne sono.</param>
        public void SplitPackets(MkvSplitSegment seg, string inputFile, double[] sourcePts, List<MkvSplitFrameInfo> keyFlags, int[] presentationToDecode, List<string> headArgs, MkvSplitCodec codec, string outputFile, string tempDir, List<TrackInfo> tracks, List<int> subtitleIds, string subtitles)
        {
            int startFrame = seg.StartFrame;
            int endFrame = seg.StartFrame + seg.FrameCount - 1;
            int kfAfter = endFrame + 1;
            int tailStart;
            int bodyCount;
            string headFile;
            string bodyFile;
            string tailFile;
            string drop;
            string videoFile;
            List<string> parts = new List<string>();
            List<string> append;

            // Primo keyframe reale dall'inizio del taglio in ordine di presentazione: le immagini leading di un GOP aperto
            // lo precedono e cadono nella testa. Senza keyframe prima della fine la testa è l'intera clip
            for (int i = startFrame; i <= endFrame; i++)
            {
                if (keyFlags[i].Key) { kfAfter = i; break; }
            }
            tailStart = kfAfter <= endFrame ? FindTailStart(presentationToDecode, kfAfter, endFrame) : endFrame + 1;
            bodyCount = tailStart - kfAfter;

            if (kfAfter > startFrame)
            {
                headFile = Path.Combine(tempDir, "head.part");
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.packetHead", startFrame, kfAfter - 1, kfAfter - startFrame));
                ReencodeFrameRange(inputFile, startFrame, kfAfter - 1, headArgs, codec, headFile);
                if (MkvSplitExternalTools.Instance.CountPackets(headFile) != kfAfter - startFrame)
                {
                    throw new InvalidOperationException(AppText.F("split.exec.headReencodeMismatch", MkvSplitExternalTools.Instance.CountPackets(headFile), kfAfter - startFrame));
                }
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.headDone", kfAfter - startFrame));
                parts.Add(headFile);
            }

            if (bodyCount > 0)
            {
                // Selezione per PTS con soglie a metà fra fotogrammi contigui, immuni dall'arrotondamento dei timestamp. Il VOL
                // dell'MPEG-4 ASP sta solo nei dati privati: va reinserito nel primo keyframe del corpo, dopo il VOL della testa.
                // In AVI il corpo riparte da 0: il muxer conta come fotogrammi saltati i tick prima del primo pacchetto e oltre
                // 60000 rifiuta il file, cioè ogni corpo che inizia dopo un minuto
                drop = "lt(pts*tb\\," + Threshold(sourcePts, kfAfter) + ")";
                if (tailStart < sourcePts.Length)
                {
                    drop += "+gte(pts*tb\\," + Threshold(sourcePts, tailStart) + ")";
                }
                bodyFile = Path.Combine(tempDir, "body.part");
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.packetBody", bodyCount));
                MkvSplitExternalTools.Instance.RunFfmpeg(new string[]
                {
                    "-y", "-hide_banner", "-loglevel", "warning", "-i", inputFile, "-map", "0:v:0", "-c:v", "copy",
                    "-bsf:v", "noise=drop='" + drop + "'" + (codec == MkvSplitCodec.Mpeg4 ? ",dump_extra=freq=k" : "") + (RawFormat(codec) == "avi" ? ",setts=ts=TS-STARTPTS" : ""),
                    "-an", "-sn", "-dn", "-f", RawFormat(codec), bodyFile
                });
                if (MkvSplitExternalTools.Instance.CountPackets(bodyFile) != bodyCount)
                {
                    throw new InvalidOperationException(AppText.F("split.exec.packetBodyMismatch", MkvSplitExternalTools.Instance.CountPackets(bodyFile), bodyCount));
                }
                parts.Add(bodyFile);
            }

            // I B-frame finali che puntano a un'ancora esclusa non si copiano: il decoder li scarterebbe
            tailFile = BuildTail(inputFile, tailStart, endFrame, headArgs, codec, tempDir);
            if (tailFile != null)
            {
                parts.Add(tailFile);
            }

            // Accodamento delle parti: i tempi definitivi li applica MuxSegment dai PTS del sorgente
            videoFile = Path.Combine(tempDir, "video.parts.mkv");
            append = new List<string> { "-o", videoFile };
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) { append.Add("+"); }
                append.Add(parts[i]);
            }
            MkvSplitExternalTools.Instance.RunMkvmerge(append);

            MuxSegment(seg, inputFile, sourcePts, videoFile, outputFile, tempDir, tracks, subtitleIds, subtitles);
        }

        /// <summary>
        /// Soglia in secondi fra un fotogramma e il precedente, a metà fra i due PTS
        /// </summary>
        /// <param name="sourcePts">PTS del sorgente in ordine di presentazione.</param>
        /// <param name="frame">Primo fotogramma oltre la soglia.</param>
        /// <returns>Soglia formattata per un'espressione ffmpeg.</returns>
        private static string Threshold(double[] sourcePts, int frame)
        {
            double value = frame > 0 ? (sourcePts[frame - 1] + sourcePts[frame]) / 2.0 : sourcePts[frame] - 1.0;
            return value.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        #endregion

        #region Mux del segmento

        /// <summary>
        /// Chiude il segmento: applica al video i timecodes v2 del sorgente (VFR preservato), verifica il numero di fotogrammi
        /// e unisce audio, tracce video secondarie e sottotitoli tagliati al pacchetto, nell'ordine del sorgente.
        /// </summary>
        /// <param name="seg">Segmento da produrre.</param>
        /// <param name="inputFile">File MKV da cui estrarre audio e tracce video secondarie.</param>
        /// <param name="sourcePts">PTS del sorgente in ordine di presentazione.</param>
        /// <param name="video">Video del segmento, bitstream o Matroska, senza tempi definitivi.</param>
        /// <param name="outputFile">File MKV di output.</param>
        /// <param name="tempDir">Directory temporanea dedicata al segmento.</param>
        /// <param name="tracks">Tracce del file normalizzato, nel loro ordine.</param>
        /// <param name="subtitleIds">Tracce di sottotitoli da tagliare al pacchetto, vuota se non ce ne sono.</param>
        /// <param name="subtitles">Matroska con le sole tracce di subtitleIds, nell'ordine del sorgente; null se non ce ne sono.</param>
        private static void MuxSegment(MkvSplitSegment seg, string inputFile, double[] sourcePts, string video, string outputFile, string tempDir, List<TrackInfo> tracks, List<int> subtitleIds, string subtitles)
        {
            string tcFile;
            string videoMkv;
            int actual;
            string avFile;
            int avExit;
            bool hasAv;
            List<string> muxCmd;
            double sizeMb;
            List<TrackInfo> videos = tracks.FindAll(track => track.Type == "video");
            List<string> order = new List<string>();
            int secondary = 0;
            int audio = 0;
            int subtitle = 0;

            // Generazione del file timecodes v2 rebasato a 0 dal primo frame del segmento
            tcFile = Path.Combine(tempDir, "timecodes.txt");
            WriteTimecodesFile(tcFile, sourcePts, seg.StartFrame, seg.FrameCount, seg.EndTs);

            // Remux del video applicando i timecodes v2 (VFR preservato)
            videoMkv = Path.Combine(tempDir, "video.mkv");
            MkvSplitExternalTools.Instance.RunMkvmerge(new string[] { "-o", videoMkv, "--timestamps", "0:" + tcFile, video });

            // Sanity check sul numero di frame risultanti
            actual = MkvSplitExternalTools.Instance.CountPackets(videoMkv);
            if (actual != seg.FrameCount)
            {
                throw new InvalidOperationException(AppText.F("split.exec.remuxFrameMismatch", actual, seg.FrameCount));
            }
            else
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.videoFrames", actual));
            }

            // Audio e tracce video secondarie in un unico container con stream copy: prima le video, poi l'audio. Una traccia
            // secondaria senza pacchetti nel tratto (la copertina MJPEG all'inizio del file) resta come traccia vuota
            avFile = Path.Combine(tempDir, "av.mkv");
            bool expectsAv = videos.Count > 1 || tracks.Exists(track => track.Type == "audio");
            List<string> avArgs = new List<string>
            {
                "-y", "-hide_banner", "-loglevel", "warning",
                "-i", inputFile,
                "-ss", seg.StartTs.ToString("G", CultureInfo.InvariantCulture),
                "-to", seg.EndTs.ToString("G", CultureInfo.InvariantCulture),
                "-map", "0:v?", "-map", "-0:v:0", "-map", "0:a?",
                "-c:v", "copy", "-c:a", "copy",
                avFile
            };
            avExit = expectsAv ? MkvSplitExternalTools.Instance.RunFfmpegNoThrow(avArgs) : 0;
            hasAv = expectsAv && avExit == 0 && File.Exists(avFile) && new FileInfo(avFile).Length > 0;
            if (expectsAv && !hasAv) throw new InvalidOperationException(AppText.F("split.montage.avFailed", avExit));

            // Mux finale: video con timecodes, audio e video secondari, sottotitoli tagliati al pacchetto
            muxCmd = new List<string>();
            muxCmd.Add("-o"); muxCmd.Add(outputFile);
            muxCmd.Add("--no-chapters"); muxCmd.Add(videoMkv);
            if (hasAv)
            {
                muxCmd.Add("--no-chapters"); muxCmd.Add(avFile);
            }
            if (subtitles != null)
            {
                // mkvmerge porta a zero il primo pacchetto di una parte senza video: un sottotitolo àncora esattamente all'inizio
                // del tratto (arrotondato per eccesso al millisecondo dei timestamp Matroska) tiene lo zero sul primo fotogramma
                // e poi si scarta. I pacchetti oltre la fine del tratto restano fuori come nel taglio con il video
                long anchorMs = (long)Math.Ceiling(seg.StartTs * 1000.0 - 1e-6);
                string anchor = Path.Combine(tempDir, "anchor.srt");
                string cutSubtitles = Path.Combine(tempDir, "subtitles.mks");
                File.WriteAllText(anchor, "1\n" + MkvSplitSegmentService.SecsToTs(anchorMs / 1000.0).Replace('.', ',') + " --> "
                    + MkvSplitSegmentService.SecsToTs((anchorMs + 1) / 1000.0).Replace('.', ',') + "\n.\n", new UTF8Encoding(false));
                MkvSplitExternalTools.Instance.RunMkvmerge(new string[] { "-o", cutSubtitles, anchor, "--no-chapters", subtitles,
                    "--split", "parts:" + MkvSplitSegmentService.SecsToTs(anchorMs / 1000.0) + "-" + MkvSplitSegmentService.SecsToTs(seg.EndTs) });
                muxCmd.Add("--subtitle-tracks"); muxCmd.Add(string.Join(",", Enumerable.Range(1, subtitleIds.Count)));
                muxCmd.Add("--no-chapters"); muxCmd.Add(cutSubtitles);
            }
            // Le tracce arrivano da tre file: si rimettono nell'ordine del sorgente, che le clip accodate devono condividere
            foreach (TrackInfo track in tracks)
            {
                if (track == videos[0])
                    order.Add("0:0");
                else if (track.Type == "video")
                    order.Add("1:" + secondary++);
                else if (track.Type == "audio")
                    order.Add("1:" + (videos.Count - 1 + audio++));
                else if (track.Type == "subtitles" && subtitles != null && subtitleIds.Contains(track.Id))
                    order.Add((hasAv ? "2:" : "1:") + (1 + subtitle++));
            }
            muxCmd.Add("--track-order"); muxCmd.Add(string.Join(",", order));
            MkvSplitExternalTools.Instance.RunMkvmerge(muxCmd);

            // Log finale con dimensione del file prodotto
            sizeMb = new FileInfo(outputFile).Length / 1048576.0;
            ConsoleHelper.Write(LogSection.Split, LogLevel.Success, AppText.F("split.exec.ok", Path.GetFileName(outputFile), sizeMb.ToString("F1", CultureInfo.InvariantCulture)));
        }

        #endregion

        #region Pipeline fast (ffmpeg seek + stream copy)

        /// <summary>Fast path: mkvmerge --split parts per video e sottotitoli e, quando presente FLAC, ffmpeg stream copy per l'audio.</summary>
        /// <param name="seg">Segmento da elaborare.</param>
        /// <param name="inputFile">File MKV di input.</param>
        /// <param name="outputFile">Path del file di output.</param>
        /// <param name="tempDir">Directory temporanea per i file intermedi.</param>
        /// <param name="tracks">Tracce del file normalizzato, nel loro ordine: con il FLAC si separa il video con mkvmerge e l'audio con ffmpeg, perché mkvmerge non splitta FLAC insieme al video.</param>
        /// <param name="subtitleIds">Tracce di sottotitoli che mkvmerge taglia insieme al video, vuota se non ce ne sono.</param>
        public void SplitFast(MkvSplitSegment seg, string inputFile, string outputFile, string tempDir, List<TrackInfo> tracks, List<int> subtitleIds)
        {
            string startTc;
            string endTc;
            List<string> muxArgs;
            double sizeMb;
            List<string> subtitleArgs = subtitleIds.Count > 0 ? new List<string> { "--subtitle-tracks", string.Join(",", subtitleIds) } : new List<string> { "--no-subtitles" };

            startTc = MkvSplitSegmentService.SecsToTs(seg.StartTs);
            endTc = MkvSplitSegmentService.SecsToTs(seg.EndTs);

            if (tracks.Exists(track => track.Type == "audio" && track.Codec.IndexOf("FLAC", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                string videoMkv = Path.Combine(tempDir, "video.mkv");
                string avFile = Path.Combine(tempDir, "av.mkv");
                List<string> videoArgs = new List<string>();
                List<string> avArgs = new List<string>();
                List<string> order = new List<string>();
                int fromVideo = 0;
                int fromAudio = 0;
                int avExit;
                bool hasAv;

                // mkvmerge non splitta FLAC insieme al video; si splittano video e sottotitoli separatamente e si rimuxa l'audio estratto da FFmpeg
                videoArgs.Add("-o"); videoArgs.Add(videoMkv);
                videoArgs.Add("--no-audio");
                videoArgs.AddRange(subtitleArgs);
                videoArgs.Add("--no-chapters");
                videoArgs.Add("--split"); videoArgs.Add("parts:" + startTc + "-" + endTc);
                videoArgs.Add(inputFile);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.mkvmergeVideoSplit", startTc, endTc));
                MkvSplitExternalTools.Instance.RunMkvmerge(videoArgs);

                avArgs.Add("-y"); avArgs.Add("-hide_banner"); avArgs.Add("-loglevel"); avArgs.Add("warning");
                avArgs.Add("-i"); avArgs.Add(inputFile);
                avArgs.Add("-ss"); avArgs.Add(startTc);
                avArgs.Add("-to"); avArgs.Add(endTc);
                avArgs.Add("-map"); avArgs.Add("0:a?");
                avArgs.Add("-c:a"); avArgs.Add("copy");
                avArgs.Add("-vn");
                avArgs.Add("-avoid_negative_ts"); avArgs.Add("make_zero");
                avArgs.Add("-map_chapters"); avArgs.Add("-1");
                avArgs.Add(avFile);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.ffmpegAvCopy", startTc, endTc));
                avExit = MkvSplitExternalTools.Instance.RunFfmpegNoThrow(avArgs);
                hasAv = avExit == 0 && File.Exists(avFile) && new FileInfo(avFile).Length > 0;
                if (!hasAv) throw new InvalidOperationException(AppText.F("split.montage.avFailed", avExit));

                // Le tracce tornano nell'ordine del sorgente, che le clip accodate devono condividere
                foreach (TrackInfo track in tracks)
                {
                    if (track.Type == "audio")
                        order.Add("1:" + fromAudio++);
                    else if (track.Type == "video" || (track.Type == "subtitles" && subtitleIds.Contains(track.Id)))
                        order.Add("0:" + fromVideo++);
                }
                muxArgs = new List<string>();
                muxArgs.Add("-o"); muxArgs.Add(outputFile);
                muxArgs.Add("--no-chapters"); muxArgs.Add(videoMkv);
                // Il video di una parte che inizia su un keyframe di GOP aperto parte dai pacchetti dei B iniziali e mostra il
                // keyframe dopo la loro durata (D25); ffmpeg taglia l'audio dal keyframe e lo porta a 0: si sposta della stessa
                // durata, come fa mkvmerge quando taglia video e audio insieme
                if (seg.OpenGopHeadSeconds > 0)
                {
                    muxArgs.Add("--sync"); muxArgs.Add("-1:" + Math.Round(seg.OpenGopHeadSeconds * 1000.0).ToString(CultureInfo.InvariantCulture));
                }
                muxArgs.Add(avFile);
                muxArgs.Add("--track-order"); muxArgs.Add(string.Join(",", order));
                MkvSplitExternalTools.Instance.RunMkvmerge(muxArgs);
            }
            else
            {
                // Non-FLAC: mkvmerge --split parts per taglio frame-perfect (preserva VFR nativamente)
                muxArgs = new List<string>();
                muxArgs.Add("-o"); muxArgs.Add(outputFile);
                muxArgs.Add("--no-chapters");
                muxArgs.AddRange(subtitleArgs);
                muxArgs.Add("--split"); muxArgs.Add("parts:" + startTc + "-" + endTc);
                muxArgs.Add(inputFile);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.mkvmergeSplit", startTc, endTc));
                MkvSplitExternalTools.Instance.RunMkvmerge(muxArgs);
            }

            sizeMb = new FileInfo(outputFile).Length / 1048576.0;
            ConsoleHelper.Write(LogSection.Split, LogLevel.Success, AppText.F("split.exec.ok", Path.GetFileName(outputFile), sizeMb.ToString("F1", CultureInfo.InvariantCulture)));
        }

        /// <summary>
        /// Trova il primo fotogramma della coda da ricodificare. Con una piramide di B-frame gli ultimi
        /// fotogrammi del segmento referenziano un'ancora che sta oltre il taglio: copiarli darebbe un
        /// file con il numero giusto di packet ma non decodificabile in fondo. Sono riconoscibili perché
        /// nel raw stanno dopo il primo fotogramma che il segmento non contiene.
        /// </summary>
        /// <param name="presentationToDecode">Indice nel raw di ogni frame, per presentazione.</param>
        /// <param name="firstPresentation">Primo fotogramma copiabile.</param>
        /// <param name="lastPresentation">Ultimo fotogramma del segmento.</param>
        /// <returns>Primo fotogramma della coda, oppure lastPresentation + 1 se la coda non serve.</returns>
        private static int FindTailStart(int[] presentationToDecode, int firstPresentation, int lastPresentation)
        {
            int firstOutsideDecode;
            int limit;

            if (lastPresentation + 1 >= presentationToDecode.Length)
            {
                return lastPresentation + 1;
            }

            firstOutsideDecode = int.MaxValue;
            limit = Math.Min(presentationToDecode.Length, lastPresentation + 1 + REORDER_LOOKAHEAD);
            for (int i = lastPresentation + 1; i < limit; i++)
            {
                if (presentationToDecode[i] < firstOutsideDecode)
                {
                    firstOutsideDecode = presentationToDecode[i];
                }
            }

            for (int i = firstPresentation; i <= lastPresentation; i++)
            {
                if (presentationToDecode[i] > firstOutsideDecode)
                {
                    return i;
                }
            }

            return lastPresentation + 1;
        }

        /// <summary>
        /// Ricodifica la coda del segmento quando serve, con gli stessi argomenti della testa.
        /// </summary>
        /// <param name="inputFile">File MKV originale.</param>
        /// <param name="tailStart">Primo fotogramma della coda.</param>
        /// <param name="lastPresentation">Ultimo fotogramma del segmento.</param>
        /// <param name="headArgs">Argomenti encoder.</param>
        /// <param name="codec">Codec video canonico.</param>
        /// <param name="tempDir">Directory temporanea del segmento.</param>
        /// <returns>Path del bitstream della coda, oppure null se la coda non serve.</returns>
        private static string BuildTail(string inputFile, int tailStart, int lastPresentation, List<string> headArgs, MkvSplitCodec codec, string tempDir)
        {
            string tailFile;
            int tailCount;
            List<(int size, long pos)> tailFrames;

            tailCount = lastPresentation - tailStart + 1;
            if (tailCount < 1)
            {
                return null;
            }

            tailFile = Path.Combine(tempDir, "tail.bs");
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.tailReencode", tailStart, lastPresentation, tailCount));
            ReencodeFrameRange(inputFile, tailStart, lastPresentation, headArgs, codec, tailFile);
            tailFrames = MkvSplitExternalTools.Instance.ProbePacketsSizePos(tailFile);
            if (tailFrames.Count != tailCount)
            {
                throw new InvalidOperationException(AppText.F("split.exec.headReencodeMismatch", tailFrames.Count, tailCount));
            }

            return tailFile;
        }

        #endregion

        #region Helper parameter sets

        /// <summary>
        /// Scrive l'intestazione che precede la parte copiata byte-exact: i parameter set originali
        /// (H.264 SPS/PPS, HEVC VPS/SPS/PPS, MPEG-2 sequence header) e, per il solo H.264, un access
        /// unit delimiter. Serve perché il rest riparte da un keyframe interno al raw: senza parameter
        /// set il decoder non ha extradata, e in H.264 due IDR consecutivi con lo stesso idr_pic_id
        /// vengono fusi dal parser di mkvmerge in un'unica access unit, perdendo un fotogramma.
        /// </summary>
        /// <param name="rawFile">Elementary stream raw originale.</param>
        /// <param name="codec">MkvSplitCodec del raw.</param>
        /// <param name="outputFile">File di output contenente l'intestazione.</param>
        private static void WriteRestHeader(string rawFile, MkvSplitCodec codec, string outputFile)
        {
            byte[] data;
            byte[] buf;
            int read;
            bool ready;
            int sequence;
            List<(int start, int end, int type)> nals;
            bool hasVps;
            bool hasSps;
            bool hasPps;
            FileStream inF;
            FileStream outF;

            // I parameter set precedono il primo fotogramma del raw: se ne legge a blocchi solo il prefisso, fino al primo
            // fotogramma (che chiude l'ultimo parameter set) o alla fine del file. Il raw di un episodio intero può superare
            // i 2 GB che un array in memoria ammette
            data = new byte[0];
            buf = new byte[BUF_SIZE];
            ready = false;
            inF = null;
            try
            {
                inF = new FileStream(rawFile, FileMode.Open, FileAccess.Read, FileShare.Read, BUF_SIZE, FileOptions.SequentialScan);
                while (!ready && (read = inF.Read(buf, 0, buf.Length)) > 0)
                {
                    Array.Resize(ref data, data.Length + read);
                    Buffer.BlockCopy(buf, 0, data, data.Length - read, read);
                    if (codec == MkvSplitCodec.Mpeg2)
                    {
                        sequence = FindStartCode(data, 0, 0xB3);
                        ready = sequence >= 0 && FindStartCode(data, sequence + 4, 0x00) >= 0;
                    }
                    else
                    {
                        ready = FindAnnexBNals(data, codec).Exists(nal => codec == MkvSplitCodec.H264 ? nal.type >= 1 && nal.type <= 5 : nal.type < 32);
                    }
                }
            }
            finally
            {
                if (inF != null) { inF.Dispose(); }
            }

            if (codec == MkvSplitCodec.Mpeg2)
            {
                WriteMpeg2SequenceHeader(data, outputFile);
                return;
            }

            nals = FindAnnexBNals(data, codec);
            hasVps = codec == MkvSplitCodec.H264;
            hasSps = false;
            hasPps = false;

            outF = null;
            try
            {
                outF = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None);
                if (codec == MkvSplitCodec.H264)
                {
                    outF.Write(s_h264AccessUnitDelimiter, 0, s_h264AccessUnitDelimiter.Length);
                }

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
                throw new InvalidOperationException(AppText.T("split.exec.noParameterSets"));
            }
        }

        /// <summary>
        /// Scrive l'header di sequenza MPEG-2: dal primo sequence header start code (0x000001B3)
        /// fino al primo picture start code (0x00000100), così da includere sequence extension,
        /// eventuale GOP header e user data senza portarsi dietro alcun fotogramma.
        /// </summary>
        /// <param name="data">Elementary stream raw completo.</param>
        /// <param name="outputFile">File di output contenente l'header.</param>
        private static void WriteMpeg2SequenceHeader(byte[] data, string outputFile)
        {
            int start;
            int cursor;
            int next;
            int end;

            start = FindStartCode(data, 0, 0xB3);
            if (start < 0)
            {
                throw new InvalidOperationException(AppText.T("split.exec.noParameterSets"));
            }

            end = -1;
            cursor = start + 4;
            while (true)
            {
                next = FindStartCode(data, cursor, -1);
                if (next < 0)
                {
                    break;
                }

                if (data[next + 3] == 0x00)
                {
                    end = next;
                    break;
                }

                cursor = next + 4;
            }

            if (end < 0)
            {
                throw new InvalidOperationException(AppText.T("split.exec.noParameterSets"));
            }

            File.WriteAllBytes(outputFile, new ArraySegment<byte>(data, start, end - start).ToArray());
        }

        /// <summary>
        /// Cerca il prossimo start code MPEG-2 (0x000001) a partire da un offset, opzionalmente
        /// filtrando sul byte che lo segue.
        /// </summary>
        /// <param name="data">Buffer da scandire.</param>
        /// <param name="from">Offset iniziale.</param>
        /// <param name="wanted">Byte atteso dopo lo start code, oppure -1 per qualunque.</param>
        /// <returns>Offset dello start code, oppure -1.</returns>
        private static int FindStartCode(byte[] data, int from, int wanted)
        {
            for (int i = from; i + 3 < data.Length; i++)
            {
                if (data[i] == 0x00 && data[i + 1] == 0x00 && data[i + 2] == 0x01 && (wanted < 0 || data[i + 3] == wanted))
                {
                    return i;
                }
            }

            return -1;
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

        /// <summary>Ricodifica un range inclusivo di frame video dal container originale in un elementary stream raw, o nel contenitore delle parti del taglio a pacchetti.</summary>
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
            ffArgs.Add("-an"); ffArgs.Add("-f"); ffArgs.Add(RawFormat(codec)); ffArgs.Add(outputFile);
            MkvSplitExternalTools.Instance.RunFfmpeg(ffArgs);
        }

        /// <summary>
        /// Nome del muxer ffmpeg delle parti ricodificate o copiate: elementary stream raw per il taglio a byte, contenitore per
        /// quello a pacchetti, AVI per i codec Video for Windows e Matroska per gli altri.
        /// </summary>
        /// <param name="codec">Codec video canonico.</param>
        /// <returns>Nome del formato da passare a -f.</returns>
        private static string RawFormat(MkvSplitCodec codec)
        {
            if (codec == MkvSplitCodec.Hevc) { return "hevc"; }
            if (codec == MkvSplitCodec.H264) { return "h264"; }
            if (codec == MkvSplitCodec.Mpeg2) { return "mpeg2video"; }
            if (codec == MkvSplitCodec.MsMpeg4v3 || codec == MkvSplitCodec.MsMpeg4v2 || codec == MkvSplitCodec.H263) { return "avi"; }
            return "matroska";
        }

        #endregion

        #region Helper I/O (privati, riusati dentro SplitSlow)

        /// <summary>
        /// Copia dal raw tutti i frame il cui indice di presentazione cade nell'intervallo indicato.
        /// In ordine di decodifica quei frame formano quasi sempre un unico blocco contiguo, ma non
        /// sempre: con un GOP aperto le immagini leading stanno in mezzo e vanno saltate, e con i
        /// B-frame la coda del segmento è riordinata. I blocchi contigui vengono quindi uniti e
        /// concatenati nell'ordine in cui stanno nel file, che è quello che il decoder si aspetta.
        /// </summary>
        /// <param name="rawFile">Elementary stream raw.</param>
        /// <param name="frameMap">Mappa dei packet del raw, in ordine di decodifica.</param>
        /// <param name="presentationToDecode">Indice nel raw di ogni frame, per presentazione.</param>
        /// <param name="firstPresentation">Primo frame da copiare, in ordine di presentazione.</param>
        /// <param name="lastPresentation">Ultimo frame da copiare, in ordine di presentazione.</param>
        /// <param name="outputFile">File di output.</param>
        /// <param name="tempDir">Directory temporanea per i blocchi intermedi.</param>
        /// <returns>Byte copiati in totale.</returns>
        private static long ExtractPresentationRange(string rawFile, List<MkvSplitFrameInfo> frameMap, int[] presentationToDecode, int firstPresentation, int lastPresentation, string outputFile, string tempDir)
        {
            List<int> decodeIndexes = new List<int>(lastPresentation - firstPresentation + 1);
            List<string> blocks = new List<string>();
            long total = 0;
            int runStart;
            int runEnd;
            long runBytes;
            string blockFile;

            for (int i = firstPresentation; i <= lastPresentation; i++)
            {
                decodeIndexes.Add(presentationToDecode[i]);
            }

            decodeIndexes.Sort();

            runStart = 0;
            while (runStart < decodeIndexes.Count)
            {
                runEnd = runStart;
                while (runEnd + 1 < decodeIndexes.Count && decodeIndexes[runEnd + 1] == decodeIndexes[runEnd] + 1)
                {
                    runEnd++;
                }

                runBytes = frameMap[decodeIndexes[runEnd]].Pos + frameMap[decodeIndexes[runEnd]].Size - frameMap[decodeIndexes[runStart]].Pos;
                blockFile = Path.Combine(tempDir, "block_" + blocks.Count.ToString(CultureInfo.InvariantCulture) + ".bs");
                ExtractByteRange(rawFile, blockFile, frameMap[decodeIndexes[runStart]].Pos, runBytes);
                blocks.Add(blockFile);
                total += runBytes;
                runStart = runEnd + 1;
            }

            if (blocks.Count == 1)
            {
                File.Copy(blocks[0], outputFile, true);
                File.Delete(blocks[0]);
            }
            else
            {
                ConcatFiles(outputFile, blocks.ToArray());
                foreach (string block in blocks)
                {
                    File.Delete(block);
                }
            }

            return total;
        }

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

        /// <summary>
        /// Scrive il file timecodes v2 rebasato a 0 dal primo frame del segmento, con la fine del segmento come riga in più:
        /// mkvmerge ne ricava la durata dell'ultimo fotogramma, che altrimenti sarebbe la sua durata predefinita (su un VFR la
        /// distanza più frequente, per esempio 50 ms invece di 33 ms con il pulldown 3:2)
        /// </summary>
        /// <param name="path">File di output.</param>
        /// <param name="sourcePts">PTS del sorgente.</param>
        /// <param name="epStartFrame">Indice del primo frame del segmento.</param>
        /// <param name="epFrameCount">Numero di frame del segmento.</param>
        /// <param name="epEndTs">Fine esclusiva del segmento in secondi: PTS del fotogramma successivo o fine del video.</param>
        private static void WriteTimecodesFile(string path, double[] sourcePts, int epStartFrame, int epFrameCount, double epEndTs)
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
                rel = (epEndTs - first) * 1000.0;
                sw.WriteLine(rel.ToString("0.000", CultureInfo.InvariantCulture));
            }
            finally
            {
                if (sw != null) { sw.Dispose(); }
            }
        }

        #endregion
    }
}
