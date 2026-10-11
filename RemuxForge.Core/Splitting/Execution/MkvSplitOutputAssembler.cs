using RemuxForge.Core.Chapters;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace RemuxForge.Core.Splitting.Execution
{
    /// <summary>
    /// File completo di un output: accodamento delle clip per file e mux finale con i nomi e i flag delle tracce del sorgente, gli
    /// allegati e i tag globali del Matroska, il FLAC ricomposto a parte, i sottotitoli riscritti e i capitoli spostati con il video
    /// </summary>
    public sealed class MkvSplitOutputAssembler
    {
        #region Variabili di classe

        /// <summary>
        /// Piano eseguibile
        /// </summary>
        private readonly MkvSplitExecutionPlan _plan;

        /// <summary>
        /// Output del piano eseguibile
        /// </summary>
        private readonly MkvSplitExecutionOutput _output;

        /// <summary>
        /// Matroska normalizzato, per allegati, tag globali e sottotitoli nativi
        /// </summary>
        private readonly string _media;

        /// <summary>
        /// Cartella temporanea dell'output
        /// </summary>
        private readonly string _temporary;

        /// <summary>
        /// True se il FLAC dell'output si ricompone a parte con il concat
        /// </summary>
        private readonly bool _separateFlac;

        /// <summary>
        /// Tracce audio del sorgente
        /// </summary>
        private readonly List<TrackInfo> _sourceAudio;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="plan">Piano eseguibile</param>
        /// <param name="output">Output del piano eseguibile</param>
        /// <param name="media">Matroska normalizzato, per allegati, tag globali e sottotitoli nativi</param>
        /// <param name="temporary">Cartella temporanea dell'output, con le clip</param>
        /// <param name="separateFlac">True se il FLAC dell'output si ricompone a parte</param>
        /// <param name="sourceAudio">Tracce audio del sorgente</param>
        public MkvSplitOutputAssembler(MkvSplitExecutionPlan plan, MkvSplitExecutionOutput output, string media, string temporary, bool separateFlac, List<TrackInfo> sourceAudio)
        {
            this._plan = plan;
            this._output = output;
            this._media = media;
            this._temporary = temporary;
            this._separateFlac = separateFlac;
            this._sourceAudio = sourceAudio;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Accoda le clip per file. La clip successiva parte dopo il pacchetto più tardo della precedente, cioè dopo il suo ultimo
        /// fotogramma, perché l'audio si ferma prima. Per traccia l'audio della clip successiva seguirebbe la fine dell'audio della
        /// precedente e ne erediterebbe lo scarto rispetto al video. Con il FLAC separato le clip si accodano senza
        /// </summary>
        /// <param name="clips">Clip nell'ordine del montaggio</param>
        /// <returns>File accodato, la clip stessa se è una sola</returns>
        /// <exception cref="InvalidOperationException">Clip con tracce audio diverse dal sorgente</exception>
        public string Append(List<string> clips)
        {
            if (clips.Count == 1)
                return clips[0];
            string assembled = Path.Combine(this._temporary, "assembled.mkv");
            List<string> append = new List<string> { "-o", assembled, "--append-mode", "file" };
            foreach (string clip in clips)
            {
                if (clip != clips[0]) append.Add("+");
                append.Add("--no-chapters"); append.Add("--no-attachments");
                if (this._separateFlac)
                {
                    MkvFileInfo clipInfo = MkvSplitExternalTools.Instance.GetFileInfo(clip);
                    List<TrackInfo> clipAudio = clipInfo?.Tracks.FindAll(track => track.Type == "audio");
                    if (clipAudio == null || clipAudio.Count != this._sourceAudio.Count)
                        throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
                    List<int> compatibleIds = new List<int>();
                    for (int index = 0; index < this._sourceAudio.Count; index++)
                        if (!MkvSplitExternalTools.IsFlacAudio(this._sourceAudio[index])) compatibleIds.Add(clipAudio[index].Id);
                    if (compatibleIds.Count == 0) append.Add("--no-audio");
                    else { append.Add("--audio-tracks"); append.Add(string.Join(",", compatibleIds)); }
                }
                append.Add(clip);
            }
            MkvSplitExternalTools.Instance.RunMkvmerge(append);
            return assembled;
        }

        /// <summary>
        /// Scrive il file completo dell'output
        /// </summary>
        /// <param name="assembled">File accodato</param>
        /// <param name="timeline">Tempi dell'output come li mostra il video copiato</param>
        /// <param name="cancellationToken">Annullamento controllato prima di ogni FLAC e prima del mux</param>
        /// <returns>File completo, complete.mkv nella cartella temporanea</returns>
        /// <exception cref="InvalidOperationException">Tracce diverse da quelle attese</exception>
        public string Mux(string assembled, MkvSplitOpenGopTimeline timeline, CancellationToken cancellationToken)
        {
            string final = Path.Combine(this._temporary, "complete.mkv");
            MkvmergeCommand mux = new MkvmergeCommand(final);
            List<string> assembledOptions = new List<string> { "--no-chapters", "--no-attachments", "--no-global-tags" };
            // I sottotitoli delle clip sono solo quelli tagliati al pacchetto
            if (this._output.FallbackSubtitleTrackIds.Count == 0) assembledOptions.Add("--no-subtitles");
            // Gli intermedi slow riordinano gli ID; ripristinare i flag e i nomi per posizione/type, non per ID sorgente.
            // Le tracce video secondarie seguono la principale come nel sorgente
            MkvFileInfo assembledInfo = MkvSplitExternalTools.Instance.GetFileInfo(assembled);
            int assembledInput = mux.InputCount;
            foreach (string type in new string[] { "video", "audio", "subtitles" })
            {
                List<TrackInfo> original = this._plan.Tracks.FindAll(track => track.Type == type && !(this._separateFlac && MkvSplitExternalTools.IsFlacAudio(track))
                    && (type != "subtitles" || this._output.FallbackSubtitleTrackIds.Contains(track.Id)));
                List<TrackInfo> actual = assembledInfo.Tracks.FindAll(track => track.Type == type);
                if (original.Count != actual.Count) throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
                for (int index = 0; index < actual.Count; index++)
                {
                    assembledOptions.AddRange(MkvmergeCommand.TrackOptions(actual[index].Id, original[index]));
                    mux.Place(original[index].Id, assembledInput, actual[index].Id);
                }
            }
            mux.AddInput(assembled, assembledOptions.ToArray());
            // Riutilizzare gli allegati e i tag globali originali senza introdurre altre tracce.
            List<string> mediaOptions = new List<string> { "--no-video", "--no-audio", "--no-chapters", "--no-track-tags" };
            int mediaInput = mux.InputCount;
            if (this._output.NativeSubtitleTrackIds.Count == 0) mediaOptions.Add("--no-subtitles");
            else
            {
                mediaOptions.Add("--subtitle-tracks"); mediaOptions.Add(string.Join(",", this._output.NativeSubtitleTrackIds));
                foreach (TrackInfo track in this._plan.Tracks.FindAll(track => this._output.NativeSubtitleTrackIds.Contains(track.Id)))
                {
                    mediaOptions.AddRange(MkvmergeCommand.TrackOptions(track.Id, track));
                    mux.Place(track.Id, mediaInput, track.Id);
                }
            }
            mux.AddInput(this._media, mediaOptions.ToArray());
            if (this._separateFlac)
            {
                // Durate video esplicite: il concat audio segue le posizioni risultato, non i tag durata degli intermedi. Alla
                // giunta il FLAC si ferma alla fine del video della clip, come le altre tracce audio
                string manifest = Path.Combine(this._temporary, "flac.ffconcat");
                using (StreamWriter writer = new StreamWriter(manifest, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine("ffconcat version 1.0");
                    for (int index = 0; index < this._output.Clips.Count; index++)
                    {
                        string videoDuration = (this._output.Projection.Clips[index].DurationSeconds + this._output.Clips[index].OpenGop.HeadSeconds - this._output.Clips[index].OpenGop.LostSeconds).ToString("R", CultureInfo.InvariantCulture);
                        writer.WriteLine("file 'clip-" + index + "/clip.mkv'");
                        writer.WriteLine("duration " + videoDuration);
                        if (index < this._output.Clips.Count - 1) writer.WriteLine("outpoint " + videoDuration);
                    }
                }
                for (int index = 0; index < this._sourceAudio.Count; index++)
                {
                    TrackInfo audio = this._sourceAudio[index];
                    if (!MkvSplitExternalTools.IsFlacAudio(audio)) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    string audioPath = Path.Combine(this._temporary, "flac-" + index + ".mka");
                    MkvSplitExternalTools.Instance.RunFfmpeg(new string[] { "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", manifest,
                        "-map", "0:a:" + index, "-c:a", "copy", "-vn", "-sn", "-dn", audioPath });
                    MkvFileInfo audioInfo = MkvSplitExternalTools.Instance.GetFileInfo(audioPath);
                    if (audioInfo?.Tracks.Count != 1 || !MkvSplitExternalTools.IsFlacAudio(audioInfo.Tracks[0]))
                        throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
                    List<string> audioOptions = new List<string>(MkvmergeCommand.TrackOptions(audioInfo.Tracks[0].Id, audio)) { "--no-chapters", "--no-attachments", "--no-global-tags" };
                    mux.Place(audio.Id, mux.AddInput(audioPath, audioOptions.ToArray()), audioInfo.Tracks[0].Id);
                }
            }
            foreach (MkvSplitSubtitleTrack subtitle in this._plan.Subtitles)
            {
                if (this._output.NativeSubtitleTrackIds.Contains(subtitle.Track.Id)) continue;
                string subtitlePath = Path.Combine(this._temporary, "result-sub-" + subtitle.Track.Id + subtitle.Clipper.Extension(subtitle.Track));
                subtitle.Clipper.Write(subtitle, timeline.Placed, subtitlePath);
                mux.Place(subtitle.Track.Id, mux.AddInput(subtitlePath, MkvmergeCommand.TrackOptions(0, subtitle.Track)), 0);
            }
            // Sottotitoli nativi e riscritti arrivano da input diversi: l'ordine originale va imposto in modo esplicito
            if (this._separateFlac || this._plan.Tracks.TrueForAll(track => mux.IsPlaced(track.Id)))
                mux.AddTrackOrder(this._plan.Tracks.ConvertAll(track => track.Id));
            if (timeline.Chapters.Count > 0)
            {
                string chapters = Path.Combine(this._temporary, "chapters.xml");
                ChapterTimelineService.WriteXmlFile(timeline.Chapters, chapters);
                mux.AddGlobalOptions("--chapters", chapters);
            }
            cancellationToken.ThrowIfCancellationRequested();
            MkvSplitExternalTools.Instance.RunMkvmerge(mux.ToArguments());
            return final;
        }

        #endregion
    }
}
