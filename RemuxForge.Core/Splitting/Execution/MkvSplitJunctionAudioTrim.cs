using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Splitting.Execution
{
    /// <summary>
    /// Taglio dell'audio di una clip seguita da un'altra. Alla giunta l'audio della clip si ferma alla fine del suo video, così
    /// l'accodamento per file fa partire la clip successiva dopo l'ultimo fotogramma con lo scarto fra audio e video del sorgente: si
    /// tolgono i pacchetti che finiscono oltre, cioè i B iniziali persi su un keyframe di GOP aperto (D25) e la frazione di blocco
    /// che sfora il taglio. mkvmerge non taglia il FLAC: con il FLAC separato resta intero e lo ferma il concat
    /// </summary>
    public sealed class MkvSplitJunctionAudioTrim
    {
        #region Variabili di classe

        /// <summary>
        /// Tracce audio del sorgente, nell'ordine in cui le porta la clip
        /// </summary>
        private readonly List<TrackInfo> _sourceAudio;

        /// <summary>
        /// True se il FLAC dell'output si ricompone a parte e quindi non si taglia qui
        /// </summary>
        private readonly bool _separateFlac;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="sourceAudio">Tracce audio del sorgente, nell'ordine in cui le porta la clip</param>
        /// <param name="separateFlac">True se il FLAC dell'output si ricompone a parte</param>
        public MkvSplitJunctionAudioTrim(List<TrackInfo> sourceAudio, bool separateFlac)
        {
            this._sourceAudio = sourceAudio;
            this._separateFlac = separateFlac;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Ferma l'audio della clip alla fine del suo video, sostituendo la clip
        /// </summary>
        /// <param name="clipPath">Clip da tagliare</param>
        /// <param name="work">Cartella di lavoro della clip</param>
        /// <param name="videoPackets">Pacchetti video della clip</param>
        /// <exception cref="InvalidOperationException">Clip senza fine del video o con tracce audio diverse dal sorgente</exception>
        public void Apply(string clipPath, string work, int videoPackets)
        {
            MkvSplitExternalTools.Instance.ExtractSourcePts(clipPath, videoPackets, out double videoEnd);
            List<TrackInfo> clipTracks = MkvSplitExternalTools.Instance.GetFileInfo(clipPath)?.Tracks;
            List<TrackInfo> clipAudio = clipTracks?.FindAll(track => track.Type == "audio");
            if (!double.IsFinite(videoEnd) || clipAudio == null || clipAudio.Count != this._sourceAudio.Count)
                throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
            double cut = double.PositiveInfinity;
            List<int> trimmedIds = new List<int>();
            for (int index = 0; index < clipAudio.Count; index++)
            {
                if (this._separateFlac && MkvSplitExternalTools.IsFlacAudio(this._sourceAudio[index])) continue;
                trimmedIds.Add(clipAudio[index].Id);
                cut = Math.Min(cut, MkvSplitExternalTools.Instance.GetAudioOverrunStart(clipPath, clipAudio[index].Id, videoEnd));
            }
            if (!double.IsFinite(cut))
                return;
            string audio = Path.Combine(work, "clip-audio.mka");
            string trimmed = Path.Combine(work, "clip-trimmed.mkv");
            MkvSplitExternalTools.Instance.RunMkvmerge(new string[] { "-o", audio, "--no-video", "--no-subtitles", "--no-chapters", "--no-attachments", "--no-global-tags",
                "--audio-tracks", string.Join(",", trimmedIds), "--split", "parts:00:00:00-" + Utils.SecsToTs(cut), clipPath });
            // Le tracce tagliate tornano al loro posto fra quelle della clip, video secondari e sottotitoli compresi:
            // l'unione e il concat del FLAC le leggono per posizione
            MkvmergeCommand merge = new MkvmergeCommand(trimmed);
            List<int> keptIds = clipAudio.FindAll(track => !trimmedIds.Contains(track.Id)).ConvertAll(track => track.Id);
            int clipInput = keptIds.Count == 0 ? merge.AddInput(clipPath, "--no-audio") : merge.AddInput(clipPath, "--audio-tracks", string.Join(",", keptIds));
            int audioInput = merge.AddInput(audio);
            int trimmedIndex = 0;
            foreach (TrackInfo track in clipTracks)
            {
                if (trimmedIds.Contains(track.Id)) merge.Place(track.Id, audioInput, trimmedIndex++);
                else merge.Place(track.Id, clipInput, track.Id);
            }
            merge.AddTrackOrder();
            MkvSplitExternalTools.Instance.RunMkvmerge(merge.ToArguments());
            File.Move(trimmed, clipPath, true);
        }

        #endregion
    }
}
