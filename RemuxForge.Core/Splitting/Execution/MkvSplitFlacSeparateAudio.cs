using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Splitting.Execution
{
    /// <summary>
    /// Audio di una clip copiata con il FLAC, che mkvmerge non taglia insieme al video: tutte le tracce audio, copiate da FFmpeg
    /// </summary>
    public sealed class MkvSplitFlacSeparateAudio : MkvSplitSeparateAudioBase
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="tracks">Tutte le tracce audio, nell'ordine del sorgente</param>
        public MkvSplitFlacSeparateAudio(List<TrackInfo> tracks) : base(tracks)
        {
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Copia con FFmpeg tutte le tracce audio dell'intervallo, riportate a zero
        /// </summary>
        /// <param name="inputFile">File normalizzato</param>
        /// <param name="startTc">Inizio dell'intervallo</param>
        /// <param name="endTc">Fine dell'intervallo</param>
        /// <param name="audioFile">File scritto</param>
        public override void Cut(string inputFile, string startTc, string endTc, string audioFile)
        {
            List<string> avArgs = new List<string>();
            int avExit;

            avArgs.Add("-y"); avArgs.Add("-hide_banner"); avArgs.Add("-loglevel"); avArgs.Add("warning");
            avArgs.Add("-i"); avArgs.Add(inputFile);
            avArgs.Add("-ss"); avArgs.Add(startTc);
            avArgs.Add("-to"); avArgs.Add(endTc);
            avArgs.Add("-map"); avArgs.Add("0:a?");
            avArgs.Add("-c:a"); avArgs.Add("copy");
            avArgs.Add("-vn");
            avArgs.Add("-avoid_negative_ts"); avArgs.Add("make_zero");
            avArgs.Add("-map_chapters"); avArgs.Add("-1");
            avArgs.Add(audioFile);
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.ffmpegAvCopy", startTc, endTc));
            avExit = MkvSplitExternalTools.Instance.RunFfmpegNoThrow(avArgs);
            if (avExit != 0 || !File.Exists(audioFile) || new FileInfo(audioFile).Length == 0)
                throw new InvalidOperationException(AppText.F("split.montage.avFailed", avExit));
        }

        #endregion
    }
}
