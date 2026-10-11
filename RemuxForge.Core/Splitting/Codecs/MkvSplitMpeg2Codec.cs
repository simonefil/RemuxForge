using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// MPEG-2 Video: ricodifica con il quantizzatore, intestazione di sequenza come parameter set e header GOP che dice se i B iniziali
    /// di un GOP aperto si decodificano da soli
    /// </summary>
    public sealed class MkvSplitMpeg2Codec : MkvSplitByteCodecBase
    {
        #region Costanti

        /// <summary>
        /// Byte letti dall'inizio del pacchetto di un keyframe MPEG-2 per trovarne l'header GOP: lo precedono solo il blocco
        /// Matroska, il sequence header con le matrici, la sua estensione e gli eventuali user data
        /// </summary>
        private const int GOP_HEADER_WINDOW = 4096;

        #endregion

        #region Proprietà

        /// <summary>
        /// Elementary stream MPEG-2
        /// </summary>
        public override string RawMuxer => "mpeg2video";

        /// <summary>
        /// Estensione dell'elementary stream MPEG-2
        /// </summary>
        public override string RawExtension => "m2v";

        /// <summary>
        /// MPEG-2 non ha CRF: la qualità si esprime con il quantizzatore
        /// </summary>
        protected override string QualityOption => "-q:v";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Keyframe di GOP aperto con i B iniziali: closed_gop=1 nell'header GOP dice che i B iniziali sono predetti solo all'indietro e
        /// si decodificano da soli. Si legge solo l'header del keyframe, senza decodificare; senza header GOP leggibile i B iniziali
        /// restano dipendenti dal GOP precedente, come con closed_gop=0 (§11 (o))
        /// </summary>
        /// <param name="inputFile">File MKV letto</param>
        /// <param name="frames">Fotogrammi in ordine di presentazione, con l'offset del pacchetto nel Matroska</param>
        public override void MarkDecodableLeadingFrames(string inputFile, List<MkvSplitFrameInfo> frames)
        {
            byte[] window;
            FileStream inF = null;

            if (!frames.Exists(f => f.LeadingFrames > 0))
                return;

            window = new byte[GOP_HEADER_WINDOW];
            try
            {
                inF = new FileStream(inputFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                for (int i = 0; i < frames.Count; i++)
                {
                    MkvSplitFrameInfo f = frames[i];
                    if (f.LeadingFrames == 0 || f.Pos < 0)
                        continue;

                    inF.Seek(f.Pos, SeekOrigin.Begin);
                    int read = inF.ReadAtLeast(window, window.Length, false);
                    // Header GOP (00 00 01 B8): 25 bit di time_code, poi closed_gop; vale solo se precede il picture header (00 00 01 00).
                    // Il limite tiene dentro la lettura anche il byte di closed_gop, quattro dopo lo start code
                    int header = MpegStartCode.Find(window, 0, read - 4, code => code == 0xB8 || code == 0x00);
                    if (header >= 0 && window[header + 3] == 0xB8)
                        f.LeadingDecodable = (window[header + 7] & 0x40) != 0;
                    frames[i] = f;
                }
            }
            finally
            {
                if (inF != null) { inF.Dispose(); }
            }
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Ricodifica all-intra con i flag dell'encoder invece che con i parametri di x264/x265: un keyframe per fotogramma, senza B
        /// </summary>
        /// <param name="args">Argomenti in costruzione</param>
        /// <param name="p">Parametri video del sorgente</param>
        /// <param name="speed">Non usata: l'encoder non prevede una velocità</param>
        protected override void AddEncodeArgs(List<string> args, MkvSplitVideoParams p, string speed)
        {
            args.Add("-g"); args.Add("1");
            args.Add("-bf"); args.Add("0");
            AddPictureArgs(args, p, "yuv420p", null);
        }

        /// <summary>
        /// Indica se il prefisso contiene l'intestazione di sequenza seguita dal primo picture header
        /// </summary>
        /// <param name="data">Prefisso del flusso grezzo</param>
        /// <returns>True se il prefisso arriva al primo fotogramma</returns>
        protected override bool ReachesFirstPicture(byte[] data)
        {
            int sequence = MpegStartCode.Find(data, 0, 0xB3);
            return sequence >= 0 && MpegStartCode.Find(data, sequence + 4, 0x00) >= 0;
        }

        /// <summary>
        /// Scrive l'header di sequenza MPEG-2: dal primo sequence header start code (0x000001B3) fino al primo picture start code
        /// (0x00000100), così da includere sequence extension, eventuale GOP header e user data senza portarsi dietro alcun fotogramma
        /// </summary>
        /// <param name="data">Prefisso del flusso grezzo</param>
        /// <param name="outputFile">File di output con l'header</param>
        /// <exception cref="InvalidOperationException">Header di sequenza o picture header assente</exception>
        protected override void WriteParameterSets(byte[] data, string outputFile)
        {
            int start;
            int cursor;
            int next;
            int end;

            start = MpegStartCode.Find(data, 0, 0xB3);
            if (start < 0)
            {
                throw new InvalidOperationException(AppText.T("split.exec.noParameterSets"));
            }

            end = -1;
            cursor = start + 4;
            while (true)
            {
                next = MpegStartCode.Find(data, cursor, data.Length, code => true);
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

        #endregion
    }
}
