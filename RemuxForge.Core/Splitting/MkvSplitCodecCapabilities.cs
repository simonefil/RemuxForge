using RemuxForge.Core.Localization;
using RemuxForge.Core.Splitting.Codecs;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Registro dei codec video dello Split, con le capacità di taglio di ognuno: autorità unica per wizard, planner, esecuzione e CLI
    /// </summary>
    /// <remarks>
    /// Un codec assente dalla tabella, o non rilevato, si taglia solo al keyframe; lo stesso vale per il video interlacciato.
    /// </remarks>
    public static class MkvSplitCodecCapabilities
    {
        #region Costanti

        /// <summary>
        /// Codec mkvmerge di MPEG-4 Part 2 (Xvid, DivX 4 e successivi)
        /// </summary>
        public const string MPEG4_PART2_CODEC = "MPEG-4p2";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Preset per nome di libx264 e libx265, dal più veloce al più lento
        /// </summary>
        private static readonly string[] s_x26xPresets = new string[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo" };

        /// <summary>
        /// Codec della tabella per nome ffprobe
        /// </summary>
        private static readonly MkvSplitVideoCodecBase[] s_codecs = new MkvSplitVideoCodecBase[]
        {
            new MkvSplitH264Codec { CodecName = "h264", DisplayName = "H.264", Encoder = "libx264",
                QualityName = "CRF", QualityDefault = 14, QualityMin = 0, QualityMax = 51, SpeedName = "preset", SpeedDefault = "medium", SpeedValues = s_x26xPresets },
            new MkvSplitHevcCodec { CodecName = "hevc", DisplayName = "H.265", Encoder = "libx265",
                QualityName = "CRF", QualityDefault = 14, QualityMin = 0, QualityMax = 51, SpeedName = "preset", SpeedDefault = "medium", SpeedValues = s_x26xPresets },
            new MkvSplitMpeg2Codec { CodecName = "mpeg2video", DisplayName = "MPEG-2", Encoder = "mpeg2video",
                QualityName = "q", QualityDefault = 2, QualityMin = 1, QualityMax = 31 },
            new MkvSplitMpeg4AspCodec { CodecName = "mpeg4", DisplayName = "Xvid/DivX (MPEG-4 ASP)", Encoder = "mpeg4",
                QualityName = "q", QualityDefault = 2, QualityMin = 1, QualityMax = 31 },
            new MkvSplitVfwCodec { CodecName = "msmpeg4v3", DisplayName = "DivX 3", Encoder = "msmpeg4",
                QualityName = "q", QualityDefault = 2, QualityMin = 1, QualityMax = 31 },
            new MkvSplitVfwCodec { CodecName = "msmpeg4v2", DisplayName = "MS-MPEG4v2", Encoder = "msmpeg4v2",
                QualityName = "q", QualityDefault = 2, QualityMin = 1, QualityMax = 31 },
            new MkvSplitH263Codec { CodecName = "h263", DisplayName = "H.263", Encoder = "h263",
                QualityName = "q", QualityDefault = 2, QualityMin = 1, QualityMax = 31 },
            new MkvSplitAv1Vp9Codec { CodecName = "av1", DisplayName = "AV1", Encoder = "libsvtav1",
                QualityName = "CRF", QualityDefault = 16, QualityMin = 0, QualityMax = 63, SpeedName = "preset", SpeedDefault = "6", SpeedMin = 0, SpeedMax = 13 },
            new MkvSplitAv1Vp9Codec { CodecName = "vp9", DisplayName = "VP9", Encoder = "libvpx-vp9", ConstantQualityArgs = new string[] { "-b:v", "0" },
                QualityName = "CRF", QualityDefault = 15, QualityMin = 0, QualityMax = 63, SpeedName = "cpu-used", SpeedDefault = "2", SpeedMin = 0, SpeedMax = 8 },
            new MkvSplitKeyframeOnlyCodec { CodecName = "vc1", DisplayName = "VC-1" }
        };

        /// <summary>
        /// Codec fuori tabella che la lettura dei keyframe tratta come quelli della tabella: per il taglio restano fuori lista (D15)
        /// </summary>
        private static readonly MkvSplitVideoCodecBase[] s_keyFlagsOnly = new MkvSplitVideoCodecBase[]
        {
            new MkvSplitVfwKeyframeOnlyCodec { CodecName = "msmpeg4v1", DisplayName = "msmpeg4v1" }
        };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Codec della tabella indicato
        /// </summary>
        /// <param name="codecName">Nome del codec come lo riporta ffprobe</param>
        /// <returns>Codec, null se il codec è assente dalla tabella o non rilevato</returns>
        public static MkvSplitVideoCodecBase Find(string codecName)
        {
            return Array.Find(s_codecs, item => string.Equals(item.CodecName, codecName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Codec della tabella che si taglia al fotogramma
        /// </summary>
        /// <param name="codecName">Nome del codec come lo riporta ffprobe</param>
        /// <returns>Codec con la ricodifica di testa e coda, null se il codec si taglia solo al keyframe, è assente dalla tabella o non è rilevato</returns>
        public static MkvSplitExactCutCodecBase FindExactCut(string codecName)
        {
            return Find(codecName) as MkvSplitExactCutCodecBase;
        }

        /// <summary>
        /// Codec che corregge la lettura dei keyframe: quello della tabella oppure uno fuori tabella con flag keyframe da verificare
        /// </summary>
        /// <param name="codecName">Nome del codec come lo riporta ffprobe</param>
        /// <returns>Codec, null se il codec non ha correzioni da fare</returns>
        public static MkvSplitVideoCodecBase FindForKeyFlags(string codecName)
        {
            return Find(codecName) ?? Array.Find(s_keyFlagsOnly, item => string.Equals(item.CodecName, codecName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Nome leggibile del codec, come nella tabella delle capacità; per un codec fuori tabella il nome ffprobe
        /// </summary>
        /// <param name="codecName">Nome del codec come lo riporta ffprobe</param>
        /// <returns>Nome da mostrare, vuoto se il codec non è rilevato</returns>
        public static string GetDisplayName(string codecName)
        {
            return Find(codecName)?.DisplayName ?? codecName ?? "";
        }

        /// <summary>
        /// Valida qualità e velocità della ricodifica contro i range dell'encoder del codec
        /// </summary>
        /// <param name="capability">Codec con taglio al fotogramma</param>
        /// <param name="quality">Qualità richiesta, null per il default</param>
        /// <param name="speed">Velocità richiesta, null o vuota per il default</param>
        /// <returns>Messaggi localizzati, vuoto se i valori sono ammessi</returns>
        public static List<string> ValidateEncodeSettings(MkvSplitExactCutCodecBase capability, int? quality, string speed)
        {
            List<string> errors = new List<string>();
            string value = (speed ?? "").Trim();

            if (quality.HasValue && (quality.Value < capability.QualityMin || quality.Value > capability.QualityMax))
                errors.Add(AppText.F("split.encode.qualityRange", quality.Value, capability.DisplayName, capability.QualityName, capability.QualityMin, capability.QualityMax));

            if (value.Length == 0)
                return errors;

            if (capability.SpeedName == null)
                errors.Add(AppText.F("split.encode.speedUnsupported", capability.DisplayName));
            else if (capability.SpeedValues != null && Array.IndexOf(capability.SpeedValues, value.ToLowerInvariant()) < 0)
                errors.Add(AppText.F("split.encode.speedInvalid", value, capability.DisplayName, capability.SpeedName, string.Join(", ", capability.SpeedValues)));
            else if (capability.SpeedValues == null
                && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < capability.SpeedMin || number > capability.SpeedMax))
                errors.Add(AppText.F("split.encode.speedInvalid", value, capability.DisplayName, capability.SpeedName, capability.SpeedMin + "–" + capability.SpeedMax));

            return errors;
        }

        #endregion
    }
}
