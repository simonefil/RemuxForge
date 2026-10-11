using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// Codec Video for Windows fuori dalla tabella del taglio (MS-MPEG4v1, D15): si taglia solo al keyframe, ma il flag keyframe
    /// dell'indice AVI va verificato sul fotogramma come per gli altri codec Video for Windows
    /// </summary>
    public sealed class MkvSplitVfwKeyframeOnlyCodec : MkvSplitKeyframeOnlyCodec
    {
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
    }
}
