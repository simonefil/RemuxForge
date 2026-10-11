using System;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// H.265: ricodifica con libx265, parameter set VPS/SPS/PPS
    /// </summary>
    public sealed class MkvSplitHevcCodec : MkvSplitAnnexBCodecBase
    {
        #region Variabili di classe

        /// <summary>
        /// NAL dei parameter set: VPS, SPS e PPS
        /// </summary>
        private static readonly int[] s_parameterSetTypes = new int[] { 32, 33, 34 };

        #endregion

        #region Proprietà

        /// <summary>
        /// Elementary stream H.265
        /// </summary>
        public override string RawMuxer => "hevc";

        /// <summary>
        /// Estensione dell'elementary stream H.265
        /// </summary>
        public override string RawExtension => "h265";

        /// <summary>
        /// Parametri di libx265
        /// </summary>
        protected override string ParamsOption => "-x265-params";

        /// <summary>
        /// 10 bit 4:2:0
        /// </summary>
        protected override string DefaultPixFmt => "yuv420p10le";

        /// <summary>
        /// VPS, SPS e PPS
        /// </summary>
        protected override int[] ParameterSetTypes => s_parameterSetTypes;

        /// <summary>
        /// Nessun prefisso: i parameter set aprono l'intestazione
        /// </summary>
        protected override byte[] HeaderPrefix => Array.Empty<byte>();

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Tipo del NAL H.265: i 6 bit dopo il forbidden bit
        /// </summary>
        /// <param name="header">Primo byte dell'header del NAL</param>
        /// <returns>Tipo del NAL</returns>
        protected override int NalType(byte header)
        {
            return (header >> 1) & 63;
        }

        /// <summary>
        /// NAL VCL (tipi 0–31)
        /// </summary>
        /// <param name="nalType">Tipo del NAL</param>
        /// <returns>True per un NAL di fotogramma</returns>
        protected override bool IsPictureNal(int nalType)
        {
            return nalType < 32;
        }

        #endregion
    }
}
