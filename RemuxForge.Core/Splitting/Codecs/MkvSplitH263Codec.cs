using RemuxForge.Core.Models;
using System;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// H.263 in Video for Windows: l'encoder h263 accetta solo i formati standard, le altre risoluzioni sono H.263+ e si ricodificano
    /// con h263p
    /// </summary>
    public sealed class MkvSplitH263Codec : MkvSplitVfwCodec
    {
        #region Costanti

        /// <summary>
        /// Encoder H.263+ per le risoluzioni fuori dai formati standard
        /// </summary>
        private const string H263_PLUS_ENCODER = "h263p";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Formati di immagine ammessi dall'encoder h263 (sub-QCIF, QCIF, CIF, 4CIF, 16CIF)
        /// </summary>
        private static readonly string[] s_h263StandardSizes = new string[] { "128x96", "176x144", "352x288", "704x576", "1408x1152" };

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Encoder della tabella sui formati standard, h263p sugli altri
        /// </summary>
        /// <param name="p">Parametri video del sorgente</param>
        /// <returns>Nome dell'encoder ffmpeg</returns>
        protected override string SelectEncoder(MkvSplitVideoParams p)
        {
            return Array.IndexOf(s_h263StandardSizes, p.Width + "x" + p.Height) < 0 ? H263_PLUS_ENCODER : this.Encoder;
        }

        #endregion
    }
}
