using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Codecs;

namespace RemuxForge.Core.Splitting.Cutters
{
    /// <summary>
    /// Taglio esatto a byte: il corpo si copia a livello di byte dall'elementary stream grezzo, estratto una volta per il piano, ed è
    /// preceduto dai parameter set originali; le parti si concatenano in un unico bitstream
    /// </summary>
    public sealed class MkvSplitByteExactCutter : MkvSplitExactCutterBase
    {
        #region Costanti

        /// <summary>
        /// Buffer usato per le copie streaming (1 MB)
        /// </summary>
        private const int BUF_SIZE = 1 << 20;

        /// <summary>
        /// Profondità massima della ricerca del keyframe successivo allo start (in frame)
        /// </summary>
        private const int KEYFRAME_LOOKAHEAD = 500;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Codec tagliato a byte
        /// </summary>
        private readonly MkvSplitByteCodecBase _codec;

        /// <summary>
        /// Elementary stream grezzo del video
        /// </summary>
        private string _rawFile;

        /// <summary>
        /// Mappa dei pacchetti del grezzo (posizione, dimensione e keyframe), in ordine di decodifica
        /// </summary>
        private List<MkvSplitFrameInfo> _frameMap;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore sul codec tagliato a byte
        /// </summary>
        /// <param name="codec">Codec tagliato a byte</param>
        public MkvSplitByteExactCutter(MkvSplitByteCodecBase codec) : base(codec)
        {
            this._codec = codec;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Keyframe della mappa del grezzo, cercati per presentazione
        /// </summary>
        protected override MkvSplitKeyframeIndex Keyframes => new MkvSplitKeyframeIndex(this._frameMap, this.PresentationToDecode);

        /// <summary>
        /// Taglio esatto dal bitstream grezzo
        /// </summary>
        protected override string PathTextKey => "split.slowPath";

        /// <summary>
        /// Testa in un elementary stream
        /// </summary>
        protected override string HeadFileName => "head.bs";

        /// <summary>
        /// Dopo la testa ricodificata il corpo copiato porta i parameter set originali: senza un keyframe da copiare la clip non si apre
        /// </summary>
        protected override bool RequiresBodyAfterHead => true;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Testa fino al primo keyframe entro KEYFRAME_LOOKAHEAD fotogrammi e dentro la clip
        /// </summary>
        /// <param name="keyframes">Keyframe cercati per presentazione</param>
        /// <param name="startFrame">Primo fotogramma della clip</param>
        /// <param name="frameCount">Fotogrammi della clip</param>
        /// <returns>Fotogrammi della testa, 0 se la clip inizia su un keyframe, -1 se nessun keyframe la apre</returns>
        public override int HeadReencodeCount(MkvSplitKeyframeIndex keyframes, int startFrame, int frameCount)
        {
            return LookaheadHeadReencodeCount(keyframes, startFrame, frameCount);
        }

        /// <summary>
        /// Regola del taglio a byte per la testa da ricodificare: il primo keyframe entro KEYFRAME_LOOKAHEAD fotogrammi e dentro la
        /// clip. È anche la regola con cui il piano annuncia il costo della testa di un video senza taglio esatto, che poi non esegue
        /// </summary>
        /// <param name="keyframes">Keyframe cercati per presentazione</param>
        /// <param name="startFrame">Primo fotogramma della clip</param>
        /// <param name="frameCount">Fotogrammi della clip</param>
        /// <returns>Fotogrammi della testa, 0 se la clip inizia su un keyframe, -1 se nessun keyframe la apre</returns>
        public static int LookaheadHeadReencodeCount(MkvSplitKeyframeIndex keyframes, int startFrame, int frameCount)
        {
            int key = keyframes.Next(startFrame, startFrame + Math.Min(frameCount, KEYFRAME_LOOKAHEAD));

            return key >= 0 ? key - startFrame : -1;
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Estrae l'elementary stream grezzo del video e ne legge la mappa dei pacchetti
        /// </summary>
        /// <param name="folder">Cartella di lavoro del piano</param>
        protected override void PrepareSource(string folder)
        {
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.T("split.extractingRawVideo"));
            this._rawFile = Path.Combine(folder, "source." + this._codec.RawExtension);
            MkvSplitExternalTools.Instance.ExtractRawTrack(this.Media, 0, this._rawFile);
            this._frameMap = MkvSplitExternalTools.Instance.GetFrameByteMap(this._rawFile);
        }

        /// <summary>
        /// La mappa del grezzo deve avere un pacchetto per fotogramma dell'analisi
        /// </summary>
        /// <param name="frameCount">Fotogrammi dell'analisi</param>
        /// <returns>True se i conteggi coincidono</returns>
        protected override bool MatchesTimeline(int frameCount)
        {
            return this._frameMap.Count == frameCount;
        }

        /// <summary>
        /// Annuncio della testa con la distanza dal keyframe precedente, cercato sugli indici della mappa (ordine di decodifica);
        /// senza keyframe precedente la distanza è zero
        /// </summary>
        /// <param name="firstFrame">Primo fotogramma della testa</param>
        /// <param name="lastFrame">Ultimo fotogramma della testa</param>
        /// <returns>Testo localizzato</returns>
        protected override string DescribeHead(int firstFrame, int lastFrame)
        {
            int keyBefore = new MkvSplitKeyframeIndex(this._frameMap).Previous(firstFrame - 1);

            return AppText.F("split.exec.headReencode", firstFrame, lastFrame, lastFrame - firstFrame + 1, firstFrame - (keyBefore < 0 ? firstFrame : keyBefore));
        }

        /// <summary>
        /// Copia a byte dal grezzo i fotogrammi del corpo e vi antepone i parameter set originali: il corpo riparte da un keyframe
        /// interno al grezzo e, dopo una testa ricodificata, il decoder ha in memoria quelli del nuovo encoder
        /// </summary>
        /// <param name="parts">Parti della clip in ordine, già con la testa se c'è</param>
        /// <param name="firstFrame">Primo fotogramma del corpo</param>
        /// <param name="tailStart">Primo fotogramma della coda, oppure la fine della clip più uno</param>
        /// <param name="afterHead">Vero se il corpo segue una testa ricodificata</param>
        /// <param name="tempDir">Cartella della clip</param>
        protected override void AddBody(List<string> parts, int firstFrame, int tailStart, bool afterHead, string tempDir)
        {
            string restFile = Path.Combine(tempDir, "rest.bs");
            string parameterSetsFile = Path.Combine(tempDir, "parameter_sets.bs");
            long restBytes = this.ExtractPresentationRange(firstFrame, tailStart - 1, restFile, tempDir);

            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F(afterHead ? "split.exec.restCopy" : "split.exec.copyFrames", tailStart - firstFrame,
                (restBytes / 1048576.0).ToString("F1", CultureInfo.InvariantCulture)));
            this._codec.WriteParameterSets(this._rawFile, parameterSetsFile);
            parts.Add(parameterSetsFile);
            parts.Add(restFile);
        }

        /// <summary>
        /// Concatena le parti in un unico bitstream
        /// </summary>
        /// <param name="parts">Parti della clip in ordine</param>
        /// <param name="tempDir">Cartella della clip</param>
        /// <returns>Bitstream della clip</returns>
        protected override string Assemble(List<string> parts, string tempDir)
        {
            string videoBs = Path.Combine(tempDir, "video.bs");

            ConcatFiles(videoBs, parts.ToArray());
            return videoBs;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Copia dal grezzo tutti i frame il cui indice di presentazione cade nell'intervallo indicato. In ordine di decodifica quei
        /// frame formano quasi sempre un unico blocco contiguo, ma non sempre: con un GOP aperto le immagini leading stanno in mezzo e
        /// vanno saltate, e con i B-frame la coda del segmento è riordinata. I blocchi contigui vengono quindi uniti e concatenati
        /// nell'ordine in cui stanno nel file, che è quello che il decoder si aspetta
        /// </summary>
        /// <param name="firstPresentation">Primo frame da copiare, in ordine di presentazione</param>
        /// <param name="lastPresentation">Ultimo frame da copiare, in ordine di presentazione</param>
        /// <param name="outputFile">File di output</param>
        /// <param name="tempDir">Directory temporanea per i blocchi intermedi</param>
        /// <returns>Byte copiati in totale</returns>
        private long ExtractPresentationRange(int firstPresentation, int lastPresentation, string outputFile, string tempDir)
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
                decodeIndexes.Add(this.PresentationToDecode[i]);
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

                runBytes = this._frameMap[decodeIndexes[runEnd]].Pos + this._frameMap[decodeIndexes[runEnd]].Size - this._frameMap[decodeIndexes[runStart]].Pos;
                blockFile = Path.Combine(tempDir, "block_" + blocks.Count.ToString(CultureInfo.InvariantCulture) + ".bs");
                ExtractByteRange(this._rawFile, blockFile, this._frameMap[decodeIndexes[runStart]].Pos, runBytes);
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

        /// <summary>
        /// Copia un range di byte [startByte, startByte+length) da src a dst con buffer di 1 MB
        /// </summary>
        /// <param name="src">File sorgente</param>
        /// <param name="dst">File destinazione (sovrascritto)</param>
        /// <param name="startByte">Offset iniziale nel sorgente</param>
        /// <param name="length">Numero di byte da copiare</param>
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

        /// <summary>
        /// Concatena più file binari nell'ordine sul file di destinazione
        /// </summary>
        /// <param name="dst">File destinazione (sovrascritto)</param>
        /// <param name="sources">File sorgente concatenati nell'ordine</param>
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

        #endregion
    }
}
