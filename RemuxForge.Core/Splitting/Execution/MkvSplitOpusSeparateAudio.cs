using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Execution
{
    /// <summary>
    /// Tracce Opus di una clip copiata che non parte dall'inizio del file: mkvmerge le taglia senza il video, quindi senza scartare
    /// il pre-roll, sullo stesso intervallo del video
    /// </summary>
    public sealed class MkvSplitOpusSeparateAudio : MkvSplitSeparateAudioBase
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="tracks">Tracce Opus, nell'ordine del sorgente</param>
        public MkvSplitOpusSeparateAudio(List<TrackInfo> tracks) : base(tracks)
        {
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Taglia con mkvmerge le sole tracce Opus dell'intervallo: senza il video la parte comincia dal pacchetto audio all'inizio
        /// dell'intervallo e finisce alla sua fine
        /// </summary>
        /// <param name="inputFile">File normalizzato</param>
        /// <param name="startTc">Inizio dell'intervallo</param>
        /// <param name="endTc">Fine dell'intervallo</param>
        /// <param name="audioFile">File scritto</param>
        public override void Cut(string inputFile, string startTc, string endTc, string audioFile)
        {
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.mkvmergeSplit", startTc, endTc));
            MkvSplitExternalTools.Instance.RunMkvmerge(new string[] { "-o", audioFile, "--no-video", "--no-subtitles", "--no-chapters", "--no-attachments", "--no-global-tags",
                "--audio-tracks", string.Join(",", this.Tracks.ConvertAll(track => track.Id)), "--split", "parts:" + startTc + "-" + endTc, inputFile });
        }

        #endregion
    }
}
