using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Codecs;

namespace RemuxForge.Core.Splitting.Cutters
{
    /// <summary>
    /// Strategia del taglio al fotogramma di una clip: testa ricodificata fino al primo keyframe, corpo copiato dal sorgente, coda
    /// ricodificata quando gli ultimi fotogrammi referenziano un'ancora oltre il taglio, poi assemblaggio e mux con i tempi del
    /// sorgente. Le sottoclassi decidono come si prepara il sorgente, come si copia il corpo e come si accodano le parti. La crea il
    /// codec (MkvSplitExactCutCodecBase.CreateCutter), che conosce il proprio metodo di taglio
    /// </summary>
    public abstract class MkvSplitExactCutterBase
    {
        #region Costanti

        /// <summary>
        /// Finestra entro cui cercare il riordino dei B-frame oltre la fine del segmento (in frame)
        /// </summary>
        private const int REORDER_LOOKAHEAD = 64;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Codec del video tagliato
        /// </summary>
        private readonly MkvSplitExactCutCodecBase _codec;

        /// <summary>
        /// Argomenti ffmpeg della ricodifica di testa e coda
        /// </summary>
        private List<string> _headArgs;

        /// <summary>
        /// Tracce del file normalizzato, nel loro ordine
        /// </summary>
        private List<TrackInfo> _tracks;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore sul codec del video tagliato
        /// </summary>
        /// <param name="codec">Codec del video tagliato</param>
        protected MkvSplitExactCutterBase(MkvSplitExactCutCodecBase codec)
        {
            this._codec = codec;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// File MKV normalizzato da cui si leggono video, audio e tracce video secondarie
        /// </summary>
        protected string Media { get; private set; }

        /// <summary>
        /// Analisi del sorgente: PTS e keyframe in ordine di presentazione
        /// </summary>
        protected MkvSplitAnalysis Analysis { get; private set; }

        /// <summary>
        /// Posizione in ordine di decodifica di ogni fotogramma, indicizzata per presentazione
        /// </summary>
        protected int[] PresentationToDecode { get; private set; }

        /// <summary>
        /// Keyframe con cui la strategia apre la clip, cercati per presentazione
        /// </summary>
        protected abstract MkvSplitKeyframeIndex Keyframes { get; }

        /// <summary>
        /// Chiave del testo che annuncia il percorso di taglio
        /// </summary>
        protected abstract string PathTextKey { get; }

        /// <summary>
        /// Nome del file della testa ricodificata, nella cartella della clip
        /// </summary>
        protected abstract string HeadFileName { get; }

        /// <summary>
        /// Vero se dopo una testa ricodificata deve restare almeno un fotogramma da copiare. Per default no: la clip senza keyframe
        /// si ricodifica per intero
        /// </summary>
        protected virtual bool RequiresBodyAfterHead => false;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Fotogrammi da ricodificare in testa a una clip, cioè fino al primo keyframe dall'inizio in ordine di presentazione. Lo
        /// usano il piano, per il costo annunciato, e l'esecuzione, con lo stesso risultato
        /// </summary>
        /// <param name="keyframes">Keyframe cercati per presentazione</param>
        /// <param name="startFrame">Primo fotogramma della clip</param>
        /// <param name="frameCount">Fotogrammi della clip</param>
        /// <returns>Fotogrammi della testa, 0 se la clip inizia su un keyframe, -1 se la clip non si può aprire</returns>
        public abstract int HeadReencodeCount(MkvSplitKeyframeIndex keyframes, int startFrame, int frameCount);

        /// <summary>
        /// Prepara una volta per il piano ciò che serve a tagliare le sue clip: la sorgente della strategia, la corrispondenza fra
        /// presentazione e decodifica, verificata contro l'analisi, e gli argomenti della ricodifica
        /// </summary>
        /// <param name="media">File MKV normalizzato del sorgente</param>
        /// <param name="folder">Cartella di lavoro del piano</param>
        /// <param name="plan">Piano eseguibile, con analisi e tracce</param>
        /// <param name="options">Opzioni di esecuzione, con qualità e velocità della ricodifica</param>
        /// <exception cref="InvalidOperationException">Fotogrammi letti diversi da quelli dell'analisi</exception>
        public void Prepare(string media, string folder, MkvSplitExecutionPlan plan, MkvSplitOptions options)
        {
            int frameCount = plan.Analysis.SourcePts.Length;
            List<double> decodePts;
            double[] keys;
            int[] map;

            this.Media = media;
            this.Analysis = plan.Analysis;
            this._tracks = plan.Tracks;
            ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.T(this.PathTextKey));
            this.PrepareSource(folder);

            // Corrispondenza fra indice di presentazione e indice di decodifica, ordinando per PTS i pacchetti letti in ordine di
            // decodifica. Senza B-frame è l'identità
            decodePts = MkvSplitExternalTools.Instance.GetDecodeOrderPts(media);
            if (decodePts.Count != frameCount || !this.MatchesTimeline(frameCount))
                throw new InvalidOperationException(AppText.T("split.montage.invalidTimeline"));
            keys = decodePts.ToArray();
            map = new int[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                map[i] = i;
            }
            Array.Sort(keys, map);
            this.PresentationToDecode = map;

            this._headArgs = this._codec.BuildHeadEncodeArgs(plan.Analysis.VideoParams, options);
        }

        /// <summary>
        /// Produce il Matroska di una clip: testa ricodificata, corpo copiato, coda ricodificata, assemblaggio delle parti e mux con
        /// i tempi del sorgente, l'audio, le tracce video secondarie e i sottotitoli tagliati al pacchetto
        /// </summary>
        /// <param name="context">Clip da produrre</param>
        /// <exception cref="InvalidOperationException">Clip senza keyframe da cui aprirla, oppure pacchetti o fotogrammi diversi da quelli attesi</exception>
        public void Split(MkvSplitExactCutContext context)
        {
            MkvSplitSegment seg = context.Segment;
            int startFrame = seg.StartFrame;
            int endFrame = seg.StartFrame + seg.FrameCount - 1;
            int headCount = this.HeadReencodeCount(this.Keyframes, startFrame, seg.FrameCount);
            int bodyStart;
            int tailStart;
            string headFile;
            string tailFile;
            List<string> parts = new List<string>();

            // Il corpo copiato parte dal primo keyframe: le immagini leading di un GOP aperto stanno dopo di lui in decodifica ma
            // vanno mostrate prima, e cadono quindi nella testa ricodificata
            if (headCount < 0)
                throw new InvalidOperationException(AppText.F("split.exec.noKeyframeInEpisode", startFrame));
            bodyStart = startFrame + headCount;
            tailStart = bodyStart <= endFrame ? FindTailStart(this.PresentationToDecode, bodyStart, endFrame) : endFrame + 1;
            if (headCount > 0 && tailStart == bodyStart && this.RequiresBodyAfterHead)
                throw new InvalidOperationException(AppText.F("split.exec.noKeyframeInEpisode", startFrame));

            // Ricodifica della testa direttamente dal container originale: il decoder riceve extradata e reference corretti, a
            // differenza di un frammento isolato
            if (headCount > 0)
            {
                headFile = Path.Combine(context.TempDir, this.HeadFileName);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, this.DescribeHead(startFrame, bodyStart - 1));
                this.ReencodeFrameRange(startFrame, bodyStart - 1, headFile);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.headDone", headCount));
                parts.Add(headFile);
            }

            this.AddBody(parts, bodyStart, tailStart, headCount > 0, context.TempDir);

            // I B-frame finali che puntano a un'ancora esclusa non si copiano: il decoder li scarterebbe. Si ricodificano con gli
            // stessi argomenti della testa
            if (tailStart <= endFrame)
            {
                tailFile = Path.Combine(context.TempDir, "tail.bs");
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.tailReencode", tailStart, endFrame, endFrame - tailStart + 1));
                this.ReencodeFrameRange(tailStart, endFrame, tailFile);
                parts.Add(tailFile);
            }

            this.MuxSegment(context, this.Assemble(parts, context.TempDir));
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Prepara la sorgente del corpo copiato, dopo l'annuncio del percorso e prima della lettura dell'ordine di decodifica. Per
        /// default nessuna: il corpo si legge dal Matroska normalizzato
        /// </summary>
        /// <param name="folder">Cartella di lavoro del piano</param>
        protected virtual void PrepareSource(string folder)
        {
        }

        /// <summary>
        /// Verifica che la sorgente preparata abbia i fotogrammi dell'analisi. Per default non c'è nulla da verificare
        /// </summary>
        /// <param name="frameCount">Fotogrammi dell'analisi</param>
        /// <returns>True se la sorgente è coerente con l'analisi</returns>
        protected virtual bool MatchesTimeline(int frameCount)
        {
            return true;
        }

        /// <summary>
        /// Testo che annuncia la ricodifica della testa
        /// </summary>
        /// <param name="firstFrame">Primo fotogramma della testa</param>
        /// <param name="lastFrame">Ultimo fotogramma della testa</param>
        /// <returns>Testo localizzato</returns>
        protected abstract string DescribeHead(int firstFrame, int lastFrame);

        /// <summary>
        /// Copia il corpo della clip, dal primo keyframe all'inizio della coda in ordine di presentazione, e ne aggiunge i file alle
        /// parti da assemblare
        /// </summary>
        /// <param name="parts">Parti della clip in ordine, già con la testa se c'è</param>
        /// <param name="firstFrame">Primo fotogramma del corpo</param>
        /// <param name="tailStart">Primo fotogramma della coda, oppure la fine della clip più uno</param>
        /// <param name="afterHead">Vero se il corpo segue una testa ricodificata</param>
        /// <param name="tempDir">Cartella della clip</param>
        protected abstract void AddBody(List<string> parts, int firstFrame, int tailStart, bool afterHead, string tempDir);

        /// <summary>
        /// Accoda le parti della clip in un unico video, senza tempi definitivi
        /// </summary>
        /// <param name="parts">Parti della clip in ordine</param>
        /// <param name="tempDir">Cartella della clip</param>
        /// <returns>Video della clip, bitstream o Matroska</returns>
        protected abstract string Assemble(List<string> parts, string tempDir);

        #endregion

        #region Metodi privati

        /// <summary>
        /// Trova il primo fotogramma della coda da ricodificare. Con una piramide di B-frame gli ultimi fotogrammi del segmento
        /// referenziano un'ancora che sta oltre il taglio: copiarli darebbe un file con il numero giusto di packet ma non
        /// decodificabile in fondo. Sono riconoscibili perché in decodifica stanno dopo il primo fotogramma che il segmento non
        /// contiene
        /// </summary>
        /// <param name="presentationToDecode">Indice di decodifica di ogni frame, per presentazione</param>
        /// <param name="firstPresentation">Primo fotogramma copiabile</param>
        /// <param name="lastPresentation">Ultimo fotogramma del segmento</param>
        /// <returns>Primo fotogramma della coda, oppure lastPresentation + 1 se la coda non serve</returns>
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
        /// Ricodifica un range inclusivo di frame video dal container originale nel formato delle parti del codec e verifica che il
        /// risultato abbia esattamente un pacchetto per fotogramma richiesto
        /// </summary>
        /// <param name="startFrame">Primo frame da includere</param>
        /// <param name="endFrame">Ultimo frame da includere</param>
        /// <param name="outputFile">Parte ricodificata di output</param>
        /// <exception cref="InvalidOperationException">Il numero di pacchetti ricodificati non è quello dei fotogrammi richiesti</exception>
        private void ReencodeFrameRange(int startFrame, int endFrame, string outputFile)
        {
            List<string> ffArgs;
            int expected = endFrame - startFrame + 1;
            int actual;

            ffArgs = new List<string>();
            ffArgs.Add("-y"); ffArgs.Add("-hide_banner"); ffArgs.Add("-loglevel"); ffArgs.Add("warning");
            ffArgs.Add("-i"); ffArgs.Add(this.Media);
            ffArgs.Add("-map"); ffArgs.Add("0:v:0");
            ffArgs.Add("-vf"); ffArgs.Add("select=between(n\\," + startFrame.ToString(CultureInfo.InvariantCulture) + "\\," + endFrame.ToString(CultureInfo.InvariantCulture) + "),setpts=N/FRAME_RATE/TB");
            foreach (string a in this._headArgs) { ffArgs.Add(a); }
            ffArgs.Add("-an"); ffArgs.Add("-f"); ffArgs.Add(this._codec.RawMuxer); ffArgs.Add(outputFile);
            MkvSplitExternalTools.Instance.RunFfmpeg(ffArgs);

            actual = MkvSplitExternalTools.Instance.CountPackets(outputFile);
            if (actual != expected)
            {
                throw new InvalidOperationException(AppText.F("split.exec.headReencodeMismatch", actual, expected));
            }
        }

        /// <summary>
        /// Chiude la clip: applica al video i timecodes v2 del sorgente (VFR preservato), verifica il numero di fotogrammi e unisce
        /// audio, tracce video secondarie e sottotitoli tagliati al pacchetto, nell'ordine del sorgente
        /// </summary>
        /// <param name="context">Clip da produrre</param>
        /// <param name="video">Video della clip, bitstream o Matroska, senza tempi definitivi</param>
        private void MuxSegment(MkvSplitExactCutContext context, string video)
        {
            MkvSplitSegment seg = context.Segment;
            double[] sourcePts = this.Analysis.SourcePts;
            string tcFile;
            string videoMkv;
            int actual;
            string avFile;
            int avExit;
            bool hasAv;
            List<string> muxCmd;
            double sizeMb;
            StreamWriter sw;
            double first;
            double rel;
            List<TrackInfo> videos = this._tracks.FindAll(track => track.Type == "video");
            List<string> order = new List<string>();
            int secondary = 0;
            int audio = 0;
            int subtitle = 0;

            // File timecodes v2 rebasato a 0 dal primo frame del segmento, con la fine del segmento come riga in più: mkvmerge ne
            // ricava la durata dell'ultimo fotogramma, che altrimenti sarebbe la sua durata predefinita (su un VFR la distanza più
            // frequente, per esempio 50 ms invece di 33 ms con il pulldown 3:2)
            tcFile = Path.Combine(context.TempDir, "timecodes.txt");
            sw = null;
            try
            {
                sw = new StreamWriter(tcFile, false, new UTF8Encoding(false));
                sw.WriteLine("# timecode format v2");
                first = sourcePts[seg.StartFrame];
                for (int i = 0; i < seg.FrameCount; i++)
                {
                    rel = (sourcePts[seg.StartFrame + i] - first) * 1000.0;
                    sw.WriteLine(rel.ToString("0.000", CultureInfo.InvariantCulture));
                }
                rel = (seg.EndTs - first) * 1000.0;
                sw.WriteLine(rel.ToString("0.000", CultureInfo.InvariantCulture));
            }
            finally
            {
                if (sw != null) { sw.Dispose(); }
            }

            // Remux del video applicando i timecodes v2 (VFR preservato)
            videoMkv = Path.Combine(context.TempDir, "video.mkv");
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
            avFile = Path.Combine(context.TempDir, "av.mkv");
            bool expectsAv = videos.Count > 1 || this._tracks.Exists(track => track.Type == "audio");
            List<string> avArgs = new List<string>
            {
                "-y", "-hide_banner", "-loglevel", "warning",
                "-i", this.Media,
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
            muxCmd.Add("-o"); muxCmd.Add(context.OutputFile);
            muxCmd.Add("--no-chapters"); muxCmd.Add(videoMkv);
            if (hasAv)
            {
                muxCmd.Add("--no-chapters"); muxCmd.Add(avFile);
            }
            if (context.Subtitles != null)
            {
                // mkvmerge porta a zero il primo pacchetto di una parte senza video: un sottotitolo àncora esattamente all'inizio
                // del tratto (arrotondato per eccesso al millisecondo dei timestamp Matroska) tiene lo zero sul primo fotogramma
                // e poi si scarta. I pacchetti oltre la fine del tratto restano fuori come nel taglio con il video
                long anchorMs = (long)Math.Ceiling(seg.StartTs * 1000.0 - 1e-6);
                string anchor = Path.Combine(context.TempDir, "anchor.srt");
                string cutSubtitles = Path.Combine(context.TempDir, "subtitles.mks");
                File.WriteAllText(anchor, "1\n" + MkvSplitSegmentService.SecsToTs(anchorMs / 1000.0).Replace('.', ',') + " --> "
                    + MkvSplitSegmentService.SecsToTs((anchorMs + 1) / 1000.0).Replace('.', ',') + "\n.\n", new UTF8Encoding(false));
                MkvSplitExternalTools.Instance.RunMkvmerge(new string[] { "-o", cutSubtitles, anchor, "--no-chapters", context.Subtitles,
                    "--split", "parts:" + MkvSplitSegmentService.SecsToTs(anchorMs / 1000.0) + "-" + MkvSplitSegmentService.SecsToTs(seg.EndTs) });
                muxCmd.Add("--subtitle-tracks"); muxCmd.Add(string.Join(",", Enumerable.Range(1, context.SubtitleIds.Count)));
                muxCmd.Add("--no-chapters"); muxCmd.Add(cutSubtitles);
            }
            // Le tracce arrivano da tre file: si rimettono nell'ordine del sorgente, che le clip accodate devono condividere
            foreach (TrackInfo track in this._tracks)
            {
                if (track == videos[0])
                    order.Add("0:0");
                else if (track.Type == "video")
                    order.Add("1:" + secondary++);
                else if (track.Type == "audio")
                    order.Add("1:" + (videos.Count - 1 + audio++));
                else if (track.Type == "subtitles" && context.Subtitles != null && context.SubtitleIds.Contains(track.Id))
                    order.Add((hasAv ? "2:" : "1:") + (1 + subtitle++));
            }
            muxCmd.Add("--track-order"); muxCmd.Add(string.Join(",", order));
            MkvSplitExternalTools.Instance.RunMkvmerge(muxCmd);

            // Log finale con dimensione del file prodotto
            sizeMb = new FileInfo(context.OutputFile).Length / 1048576.0;
            ConsoleHelper.Write(LogSection.Split, LogLevel.Success, AppText.F("split.exec.ok", Path.GetFileName(context.OutputFile), sizeMb.ToString("F1", CultureInfo.InvariantCulture)));
        }

        #endregion
    }
}
