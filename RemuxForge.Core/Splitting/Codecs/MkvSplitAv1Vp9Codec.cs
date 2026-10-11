using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// AV1 e VP9: parti in Matroska, ricodifica all-intra a qualità costante con CRF e velocità dell'encoder
    /// </summary>
    public sealed class MkvSplitAv1Vp9Codec : MkvSplitPacketCodecBase
    {
        #region Proprietà

        /// <summary>
        /// Argomenti che portano l'encoder a qualità costante oltre al CRF, vuoto se il CRF basta (libvpx-vp9 è a qualità costante
        /// solo senza bitrate obiettivo)
        /// </summary>
        public string[] ConstantQualityArgs { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Parti in Matroska
        /// </summary>
        public override string RawMuxer => "matroska";

        /// <summary>
        /// Nessun filtro: il corpo selezionato si accoda così com'è
        /// </summary>
        public override string BodyBitstreamFilters => "";

        /// <summary>
        /// Qualità come CRF
        /// </summary>
        protected override string QualityOption => "-crf";

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Qualità costante, velocità, un keyframe per fotogramma, pix_fmt e colori del sorgente come per H.264, H.265 e MPEG-2:
        /// libsvtav1 li scrive tutti nel bitstream, libvpx-vp9 solo spazio colore e range, primarie e trasferimento restano al contenitore
        /// </summary>
        /// <param name="args">Argomenti in costruzione</param>
        /// <param name="p">Parametri video del sorgente</param>
        /// <param name="speed">Velocità richiesta o predefinita</param>
        protected override void AddEncodeArgs(List<string> args, MkvSplitVideoParams p, string speed)
        {
            args.AddRange(this.ConstantQualityArgs);
            args.Add("-" + this.SpeedName); args.Add(speed);
            args.Add("-g"); args.Add("1");
            AddPictureArgs(args, p, "yuv420p", null);
        }

        #endregion
    }
}
