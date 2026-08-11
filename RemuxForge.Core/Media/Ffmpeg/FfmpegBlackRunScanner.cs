using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Media.Ffmpeg
{
    /// <summary>
    /// Rileva run nere PTS tramite un singolo processo FFmpeg senza estrarre rawvideo
    /// </summary>
    public static class FfmpegBlackRunScanner
    {
        #region Costanti

        /// <summary>
        /// Catena condivisa per rilevare le black-run a bassa risoluzione
        /// </summary>
        public const string ANALYSIS_FILTER = "scale=64:48:flags=fast_bilinear,format=gray,blackdetect=d=0.080:pix_th=0.062745:pic_th=0.920";

        #endregion

        #region Variabili statiche

        private static readonly Regex s_blackRunRegex = new Regex(@"black_start:(-?\d+(?:\.\d+)?)\s+black_end:(-?\d+(?:\.\d+)?)\s+black_duration:(\d+(?:\.\d+)?)", RegexOptions.Compiled);

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Converte le righe diagnostiche blackdetect in intervalli PTS
        /// </summary>
        public static List<DeepBlackTimelineRun> ParseDiagnostics(string diagnostics)
        {
            List<DeepBlackTimelineRun> result = new List<DeepBlackTimelineRun>();
            if (string.IsNullOrEmpty(diagnostics))
                return result;

            MatchCollection matches = s_blackRunRegex.Matches(diagnostics);
            for (int i = 0; i < matches.Count; i++)
            {
                double startPtsMs = double.Parse(matches[i].Groups[1].Value, CultureInfo.InvariantCulture) * 1000.0;
                double endPtsMs = double.Parse(matches[i].Groups[2].Value, CultureInfo.InvariantCulture) * 1000.0;
                double durationMs = double.Parse(matches[i].Groups[3].Value, CultureInfo.InvariantCulture) * 1000.0;
                if (endPtsMs > startPtsMs && durationMs >= 80.0)
                {
                    DeepBlackTimelineRun run = new DeepBlackTimelineRun();
                    run.StartPtsMs = startPtsMs;
                    run.EndPtsMs = endPtsMs;
                    run.DurationMs = endPtsMs - startPtsMs;
                    result.Add(run);
                }
            }
            return result;
        }

        #endregion
    }
}
