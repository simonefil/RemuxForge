using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// Codec Video for Windows (MS-MPEG4v2, DivX 3, H.263): parti in AVI con il fourcc del sorgente e flag keyframe dell'indice AVI
    /// da verificare sul fotogramma
    /// </summary>
    public class MkvSplitVfwCodec : MkvSplitPacketCodecBase
    {
        #region Proprietà

        /// <summary>
        /// Parti in AVI
        /// </summary>
        public override string RawMuxer => "avi";

        /// <summary>
        /// In AVI il corpo riparte da 0: il muxer conta come fotogrammi saltati i tick prima del primo pacchetto e oltre 60000 rifiuta
        /// il file, cioè ogni corpo che inizia dopo un minuto
        /// </summary>
        public override string BodyBitstreamFilters => ",setts=ts=TS-STARTPTS";

        /// <summary>
        /// Qualità come quantizzatore
        /// </summary>
        protected override string QualityOption => "-q:v";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// In Video for Windows il flag è dell'indice AVI, non del fotogramma: si tengono come keyframe solo gli I reali
        /// </summary>
        /// <param name="inputFile">File MKV letto</param>
        /// <param name="frames">Pacchetti in ordine di decodifica</param>
        /// <param name="pts">PTS dei pacchetti in ordine di decodifica</param>
        public override void CorrectKeyFlags(string inputFile, List<MkvSplitFrameInfo> frames, List<double> pts)
        {
            MkvSplitExternalTools.Instance.CorrectAviIndexKeyFlags(inputFile, frames, pts);
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Testa e coda all-intra con il fourcc del sorgente, o l'unione con il corpo non è compatibile
        /// </summary>
        /// <param name="args">Argomenti in costruzione</param>
        /// <param name="p">Parametri video del sorgente</param>
        /// <param name="speed">Non usata: l'encoder non prevede una velocità</param>
        protected override void AddEncodeArgs(List<string> args, MkvSplitVideoParams p, string speed)
        {
            args.Add("-g"); args.Add("1");
            if (p.CodecTag != null) { args.Add("-vtag"); args.Add(p.CodecTag); }
        }

        #endregion
    }
}
