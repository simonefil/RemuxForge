namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// H.264: ricodifica con libx264, parameter set SPS/PPS e access unit delimiter in testa all'intestazione
    /// </summary>
    public sealed class MkvSplitH264Codec : MkvSplitAnnexBCodecBase
    {
        #region Variabili di classe

        /// <summary>
        /// Access unit delimiter H.264 (NAL type 9, primary_pic_type = I) in formato Annex B: due IDR consecutivi con lo stesso
        /// idr_pic_id verrebbero fusi dal parser di mkvmerge in un'unica access unit, perdendo un fotogramma
        /// </summary>
        private static readonly byte[] s_accessUnitDelimiter = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x09, 0x10 };

        /// <summary>
        /// NAL dei parameter set: SPS e PPS
        /// </summary>
        private static readonly int[] s_parameterSetTypes = new int[] { 7, 8 };

        #endregion

        #region Proprietà

        /// <summary>
        /// Elementary stream H.264
        /// </summary>
        public override string RawMuxer => "h264";

        /// <summary>
        /// Estensione dell'elementary stream H.264
        /// </summary>
        public override string RawExtension => "h264";

        /// <summary>
        /// Parametri di libx264
        /// </summary>
        protected override string ParamsOption => "-x264-params";

        /// <summary>
        /// 8 bit 4:2:0
        /// </summary>
        protected override string DefaultPixFmt => "yuv420p";

        /// <summary>
        /// SPS e PPS
        /// </summary>
        protected override int[] ParameterSetTypes => s_parameterSetTypes;

        /// <summary>
        /// Access unit delimiter
        /// </summary>
        protected override byte[] HeaderPrefix => s_accessUnitDelimiter;

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Tipo del NAL H.264: i 5 bit bassi dell'header
        /// </summary>
        /// <param name="header">Primo byte dell'header del NAL</param>
        /// <returns>Tipo del NAL</returns>
        protected override int NalType(byte header)
        {
            return header & 31;
        }

        /// <summary>
        /// NAL di slice, IDR compreso (tipi 1–5)
        /// </summary>
        /// <param name="nalType">Tipo del NAL</param>
        /// <returns>True per un NAL di fotogramma</returns>
        protected override bool IsPictureNal(int nalType)
        {
            return nalType >= 1 && nalType <= 5;
        }

        #endregion
    }
}
