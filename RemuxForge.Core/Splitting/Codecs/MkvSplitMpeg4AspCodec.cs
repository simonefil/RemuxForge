using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// MPEG-4 ASP (Xvid, DivX 4 e successivi): parti in Matroska, testa con B-frame e VOL reinserito nel primo keyframe del corpo
    /// </summary>
    public sealed class MkvSplitMpeg4AspCodec : MkvSplitPacketCodecBase
    {
        #region Proprietà

        /// <summary>
        /// Parti in Matroska
        /// </summary>
        public override string RawMuxer => "matroska";

        /// <summary>
        /// Il VOL dell'MPEG-4 ASP sta solo nei dati privati: va reinserito nel primo keyframe del corpo, dopo il VOL della testa
        /// </summary>
        public override string BodyBitstreamFilters => ",dump_extra=freq=k";

        /// <summary>
        /// Qualità come quantizzatore
        /// </summary>
        protected override string QualityOption => "-q:v";

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Testa con B-frame: una testa tutta intra avrebbe un VOL low delay diverso da quello del corpo (§3.3 punto 3); fourcc del
        /// sorgente, o l'unione con il corpo non è compatibile
        /// </summary>
        /// <param name="args">Argomenti in costruzione</param>
        /// <param name="p">Parametri video del sorgente</param>
        /// <param name="speed">Non usata: l'encoder non prevede una velocità</param>
        protected override void AddEncodeArgs(List<string> args, MkvSplitVideoParams p, string speed)
        {
            args.Add("-bf"); args.Add("2");
            if (p.CodecTag != null) { args.Add("-vtag"); args.Add(p.CodecTag); }
        }

        #endregion
    }
}
