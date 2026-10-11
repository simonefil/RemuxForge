using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Tabella delle capacità di taglio per codec video: autorità unica per wizard, planner, esecuzione e CLI
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
        /// Capacità per nome ffprobe del codec
        /// </summary>
        private static readonly MkvSplitCodecCapability[] s_capabilities = new MkvSplitCodecCapability[]
        {
            new MkvSplitCodecCapability { CodecName = "h264", DisplayName = "H.264", Codec = MkvSplitCodec.H264, Method = MkvSplitExactCutMethod.Bytes, Encoder = "libx264",
                QualityName = "CRF", QualityDefault = 14, QualityMin = 0, QualityMax = 51, SpeedName = "preset", SpeedDefault = "medium", SpeedValues = s_x26xPresets },
            new MkvSplitCodecCapability { CodecName = "hevc", DisplayName = "H.265", Codec = MkvSplitCodec.Hevc, Method = MkvSplitExactCutMethod.Bytes, Encoder = "libx265",
                QualityName = "CRF", QualityDefault = 14, QualityMin = 0, QualityMax = 51, SpeedName = "preset", SpeedDefault = "medium", SpeedValues = s_x26xPresets },
            new MkvSplitCodecCapability { CodecName = "mpeg2video", DisplayName = "MPEG-2", Codec = MkvSplitCodec.Mpeg2, Method = MkvSplitExactCutMethod.Bytes, Encoder = "mpeg2video",
                QualityName = "q", QualityDefault = 2, QualityMin = 1, QualityMax = 31 },
            new MkvSplitCodecCapability { CodecName = "mpeg4", DisplayName = "Xvid/DivX (MPEG-4 ASP)", Codec = MkvSplitCodec.Mpeg4, Method = MkvSplitExactCutMethod.Packets, Encoder = "mpeg4",
                QualityName = "q", QualityDefault = 2, QualityMin = 1, QualityMax = 31 },
            new MkvSplitCodecCapability { CodecName = "msmpeg4v3", DisplayName = "DivX 3", Codec = MkvSplitCodec.MsMpeg4v3, Method = MkvSplitExactCutMethod.Packets, Encoder = "msmpeg4",
                QualityName = "q", QualityDefault = 2, QualityMin = 1, QualityMax = 31 },
            new MkvSplitCodecCapability { CodecName = "msmpeg4v2", DisplayName = "MS-MPEG4v2", Codec = MkvSplitCodec.MsMpeg4v2, Method = MkvSplitExactCutMethod.Packets, Encoder = "msmpeg4v2",
                QualityName = "q", QualityDefault = 2, QualityMin = 1, QualityMax = 31 },
            new MkvSplitCodecCapability { CodecName = "h263", DisplayName = "H.263", Codec = MkvSplitCodec.H263, Method = MkvSplitExactCutMethod.Packets, Encoder = "h263",
                QualityName = "q", QualityDefault = 2, QualityMin = 1, QualityMax = 31 },
            new MkvSplitCodecCapability { CodecName = "av1", DisplayName = "AV1", Codec = MkvSplitCodec.Av1, Method = MkvSplitExactCutMethod.Packets, Encoder = "libsvtav1",
                QualityName = "CRF", QualityDefault = 16, QualityMin = 0, QualityMax = 63, SpeedName = "preset", SpeedDefault = "6", SpeedMin = 0, SpeedMax = 13 },
            new MkvSplitCodecCapability { CodecName = "vp9", DisplayName = "VP9", Codec = MkvSplitCodec.Vp9, Method = MkvSplitExactCutMethod.Packets, Encoder = "libvpx-vp9",
                QualityName = "CRF", QualityDefault = 15, QualityMin = 0, QualityMax = 63, SpeedName = "cpu-used", SpeedDefault = "2", SpeedMin = 0, SpeedMax = 8 },
            new MkvSplitCodecCapability { CodecName = "vc1", DisplayName = "VC-1", Codec = MkvSplitCodec.Vc1, Method = MkvSplitExactCutMethod.None }
        };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Capacità del codec indicato
        /// </summary>
        /// <param name="codecName">Nome del codec come lo riporta ffprobe</param>
        /// <returns>Capacità del codec, null se il codec è assente dalla tabella o non rilevato</returns>
        public static MkvSplitCodecCapability Find(string codecName)
        {
            return Array.Find(s_capabilities, item => string.Equals(item.CodecName, codecName, StringComparison.OrdinalIgnoreCase));
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
        /// Indica se il video si può tagliare al fotogramma, cioè con la ricodifica della testa
        /// </summary>
        /// <param name="codecName">Nome del codec come lo riporta ffprobe</param>
        /// <param name="scanType">Scansione letta da MediaInfo, vuota se non dichiarata</param>
        /// <param name="reason">Motivo localizzato del solo taglio al keyframe, vuoto se il taglio al fotogramma è disponibile</param>
        /// <returns>True se il taglio al fotogramma è disponibile</returns>
        public static bool IsExactCutAvailable(string codecName, VideoScanType scanType, out string reason)
        {
            MkvSplitCodecCapability capability = Find(codecName);

            reason = "";
            if (capability == null || capability.Method == MkvSplitExactCutMethod.None)
                reason = AppText.F("split.unsupportedVideoCodec", GetDisplayName(codecName));
            // Un video interlacciato non ha un fotogramma da ricodificare al giunto (D07)
            else if (scanType.IsInterlaced)
                reason = AppText.F("split.interlacedKeyframeOnly", scanType);

            return reason.Length == 0;
        }

        /// <summary>
        /// Indica se un fotogramma è un punto di taglio senza ricodifica: keyframe chiuso oppure, su video interlacciato
        /// che non si può ricodificare (D07), keyframe di GOP aperto con i soli B iniziali, che il taglio perde (D25)
        /// </summary>
        /// <param name="frame">Fotogramma del confine, in ordine di presentazione</param>
        /// <param name="scanType">Scansione letta da MediaInfo, vuota se non dichiarata (P4: progressivo)</param>
        /// <returns>True se mkvmerge può tagliare sul fotogramma</returns>
        public static bool IsCopyCutKeyframe(MkvSplitFrameInfo frame, VideoScanType scanType)
        {
            return frame.Key && (!frame.OpenGop || (frame.LeadingFrames > 0 && scanType.IsInterlaced));
        }

        /// <summary>
        /// Valida qualità e velocità della ricodifica contro i range dell'encoder del codec
        /// </summary>
        /// <param name="capability">Capacità del codec</param>
        /// <param name="quality">Qualità richiesta, null per il default</param>
        /// <param name="speed">Velocità richiesta, null o vuota per il default</param>
        /// <returns>Messaggi localizzati, vuoto se i valori sono ammessi</returns>
        public static List<string> ValidateEncodeSettings(MkvSplitCodecCapability capability, int? quality, string speed)
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
