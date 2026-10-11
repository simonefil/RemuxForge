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
    /// Taglio esatto a pacchetti: testa e coda ricodificate con lo stesso codec, corpo copiato selezionando i pacchetti del Matroska
    /// normalizzato per PTS e parti accodate con mkvmerge. Serve ai codec il cui flusso non si copia a byte da un elementary stream:
    /// MPEG-4 ASP, AV1 e VP9 con parti in Matroska, MS-MPEG4 e H.263 con parti in AVI e il fourcc del sorgente
    /// </summary>
    public sealed class MkvSplitPacketCutter : MkvSplitExactCutterBase
    {
        #region Variabili di classe

        /// <summary>
        /// Codec tagliato a pacchetti
        /// </summary>
        private readonly MkvSplitPacketCodecBase _codec;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore sul codec tagliato a pacchetti
        /// </summary>
        /// <param name="codec">Codec tagliato a pacchetti</param>
        public MkvSplitPacketCutter(MkvSplitPacketCodecBase codec) : base(codec)
        {
            this._codec = codec;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Keyframe reali del sorgente, quelli dell'analisi
        /// </summary>
        protected override MkvSplitKeyframeIndex Keyframes => new MkvSplitKeyframeIndex(this.Analysis.KeyFlags);

        /// <summary>
        /// Taglio esatto a pacchetti
        /// </summary>
        protected override string PathTextKey => "split.packetPath";

        /// <summary>
        /// Testa nel contenitore delle parti del codec
        /// </summary>
        protected override string HeadFileName => "head.part";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Testa fino al primo keyframe dentro la clip; senza keyframe la clip si ricodifica per intero
        /// </summary>
        /// <param name="keyframes">Keyframe cercati per presentazione</param>
        /// <param name="startFrame">Primo fotogramma della clip</param>
        /// <param name="frameCount">Fotogrammi della clip</param>
        /// <returns>Fotogrammi della testa, 0 se la clip inizia su un keyframe</returns>
        public override int HeadReencodeCount(MkvSplitKeyframeIndex keyframes, int startFrame, int frameCount)
        {
            int key = keyframes.Next(startFrame, startFrame + frameCount);

            return key >= 0 ? key - startFrame : frameCount;
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Annuncio della testa ricodificata a pacchetti
        /// </summary>
        /// <param name="firstFrame">Primo fotogramma della testa</param>
        /// <param name="lastFrame">Ultimo fotogramma della testa</param>
        /// <returns>Testo localizzato</returns>
        protected override string DescribeHead(int firstFrame, int lastFrame)
        {
            return AppText.F("split.exec.packetHead", firstFrame, lastFrame, lastFrame - firstFrame + 1);
        }

        /// <summary>
        /// Copia i pacchetti del corpo selezionandoli per PTS, con soglie a metà fra fotogrammi contigui immuni dall'arrotondamento
        /// dei timestamp, seguita dai filtri del codec; un corpo vuoto non produce parti
        /// </summary>
        /// <param name="parts">Parti della clip in ordine, già con la testa se c'è</param>
        /// <param name="firstFrame">Primo fotogramma del corpo</param>
        /// <param name="tailStart">Primo fotogramma della coda, oppure la fine della clip più uno</param>
        /// <param name="afterHead">Vero se il corpo segue una testa ricodificata</param>
        /// <param name="tempDir">Cartella della clip</param>
        /// <exception cref="InvalidOperationException">Pacchetti copiati diversi dai fotogrammi del corpo</exception>
        protected override void AddBody(List<string> parts, int firstFrame, int tailStart, bool afterHead, string tempDir)
        {
            double[] sourcePts = this.Analysis.SourcePts;
            int bodyCount = tailStart - firstFrame;
            string drop;
            string bodyFile;

            if (bodyCount <= 0)
            {
                return;
            }

            drop = "lt(pts*tb\\," + Threshold(sourcePts, firstFrame) + ")";
            if (tailStart < sourcePts.Length)
            {
                drop += "+gte(pts*tb\\," + Threshold(sourcePts, tailStart) + ")";
            }
            bodyFile = Path.Combine(tempDir, "body.part");
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.packetBody", bodyCount));
            MkvSplitExternalTools.Instance.RunFfmpeg(new string[]
            {
                "-y", "-hide_banner", "-loglevel", "warning", "-i", this.Media, "-map", "0:v:0", "-c:v", "copy",
                "-bsf:v", "noise=drop='" + drop + "'" + this._codec.BodyBitstreamFilters,
                "-an", "-sn", "-dn", "-f", this._codec.RawMuxer, bodyFile
            });
            if (MkvSplitExternalTools.Instance.CountPackets(bodyFile) != bodyCount)
            {
                throw new InvalidOperationException(AppText.F("split.exec.packetBodyMismatch", MkvSplitExternalTools.Instance.CountPackets(bodyFile), bodyCount));
            }
            parts.Add(bodyFile);
        }

        /// <summary>
        /// Accoda le parti con mkvmerge: i tempi definitivi li applica il mux della clip dai PTS del sorgente
        /// </summary>
        /// <param name="parts">Parti della clip in ordine</param>
        /// <param name="tempDir">Cartella della clip</param>
        /// <returns>Matroska con il video della clip</returns>
        protected override string Assemble(List<string> parts, string tempDir)
        {
            string videoFile = Path.Combine(tempDir, "video.parts.mkv");
            List<string> append = new List<string> { "-o", videoFile };

            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) { append.Add("+"); }
                append.Add(parts[i]);
            }
            MkvSplitExternalTools.Instance.RunMkvmerge(append);
            return videoFile;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Soglia in secondi fra un fotogramma e il precedente, a metà fra i due PTS
        /// </summary>
        /// <param name="sourcePts">PTS del sorgente in ordine di presentazione</param>
        /// <param name="frame">Primo fotogramma oltre la soglia</param>
        /// <returns>Soglia formattata per un'espressione ffmpeg</returns>
        private static string Threshold(double[] sourcePts, int frame)
        {
            double value = frame > 0 ? (sourcePts[frame - 1] + sourcePts[frame]) / 2.0 : sourcePts[frame] - 1.0;
            return value.ToString("0.000000", CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
