using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Execution;

namespace RemuxForge.Core.Splitting
{
    /// <summary>Produce il file di output di una clip copiata senza ricodifica (mkvmerge --split parts, l'audio che mkvmerge non taglia con il video a parte); il taglio al fotogramma lo fanno le strategie di MkvSplitExactCutterBase.</summary>
    public class MkvSplitExecutor
    {
        #region Costruttore

        /// <summary>Costruttore vuoto: il servizio è stateless.</summary>
        public MkvSplitExecutor()
        {
        }

        #endregion

        #region Pipeline fast (ffmpeg seek + stream copy)

        /// <summary>Fast path: mkvmerge --split parts per video e sottotitoli e, con l'audio da tagliare a parte (FLAC, Opus fuori dall'inizio del file), un taglio dell'audio riunito al video.</summary>
        /// <param name="seg">Segmento da elaborare.</param>
        /// <param name="inputFile">File MKV di input.</param>
        /// <param name="outputFile">Path del file di output.</param>
        /// <param name="tempDir">Directory temporanea per i file intermedi.</param>
        /// <param name="tracks">Tracce del file normalizzato, nel loro ordine: l'audio che mkvmerge non taglia insieme al video si taglia a parte (MkvSplitSeparateAudioBase).</param>
        /// <param name="subtitleIds">Tracce di sottotitoli che mkvmerge taglia insieme al video, vuota se non ce ne sono.</param>
        public void SplitFast(MkvSplitSegment seg, string inputFile, string outputFile, string tempDir, List<TrackInfo> tracks, List<int> subtitleIds)
        {
            string startTc;
            string endTc;
            List<string> muxArgs;
            double sizeMb;
            List<string> subtitleArgs = subtitleIds.Count > 0 ? new List<string> { "--subtitle-tracks", string.Join(",", subtitleIds) } : new List<string> { "--no-subtitles" };
            MkvSplitSeparateAudioBase separateAudio = MkvSplitSeparateAudioBase.For(seg, tracks);

            startTc = Utils.SecsToTs(seg.StartTs);
            endTc = Utils.SecsToTs(seg.EndTs);

            if (separateAudio != null)
            {
                string videoMkv = Path.Combine(tempDir, "video.mkv");
                string avFile = Path.Combine(tempDir, "av.mkv");
                List<string> videoArgs = new List<string>();
                List<int> keptAudioIds = tracks.FindAll(track => track.Type == "audio" && !separateAudio.Tracks.Contains(track)).ConvertAll(track => track.Id);
                int fromVideo = 0;
                int fromAudio = 0;

                // Video, sottotitoli e audio che resta con il video si splittano senza l'audio tagliato a parte, che poi si rimuxa
                videoArgs.Add("-o"); videoArgs.Add(videoMkv);
                if (keptAudioIds.Count == 0)
                    videoArgs.Add("--no-audio");
                else
                {
                    videoArgs.Add("--audio-tracks"); videoArgs.Add(string.Join(",", keptAudioIds));
                }
                videoArgs.AddRange(subtitleArgs);
                videoArgs.Add("--no-chapters");
                videoArgs.Add("--split"); videoArgs.Add("parts:" + startTc + "-" + endTc);
                videoArgs.Add(inputFile);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.mkvmergeVideoSplit", startTc, endTc));
                MkvSplitExternalTools.Instance.RunMkvmerge(videoArgs);

                separateAudio.Cut(inputFile, startTc, endTc, avFile);

                // Il video di una parte che inizia su un keyframe di GOP aperto parte dai pacchetti dei B iniziali e mostra il
                // keyframe dopo la loro durata (D25); l'audio tagliato a parte comincia dal keyframe ed è portato a 0: si sposta della
                // stessa durata, come fa mkvmerge quando taglia video e audio insieme
                MkvmergeCommand mux = new MkvmergeCommand(outputFile);
                int videoInput = mux.AddInput(videoMkv, "--no-chapters");
                int audioInput = seg.OpenGopHeadSeconds > 0
                    ? mux.AddInput(avFile, "--sync", "-1:" + Math.Round(seg.OpenGopHeadSeconds * 1000.0).ToString(CultureInfo.InvariantCulture))
                    : mux.AddInput(avFile);
                // Le tracce tornano nell'ordine del sorgente, che le clip accodate devono condividere
                foreach (TrackInfo track in tracks)
                {
                    if (separateAudio.Tracks.Contains(track))
                        mux.Place(track.Id, audioInput, fromAudio++);
                    else if (track.Type == "video" || track.Type == "audio" || (track.Type == "subtitles" && subtitleIds.Contains(track.Id)))
                        mux.Place(track.Id, videoInput, fromVideo++);
                }
                mux.AddTrackOrder();
                mux.AddGlobalOptions(MkvSplitExternalTools.ClipLacingOptions(tracks));
                muxArgs = mux.ToArguments();
                MkvSplitExternalTools.Instance.RunMkvmerge(muxArgs);
            }
            else
            {
                // Senza audio da tagliare a parte: mkvmerge --split parts per taglio frame-perfect (preserva VFR nativamente)
                MkvmergeCommand split = new MkvmergeCommand(outputFile);
                List<string> inputOptions = new List<string> { "--no-chapters" };
                inputOptions.AddRange(subtitleArgs);
                inputOptions.Add("--split"); inputOptions.Add("parts:" + startTc + "-" + endTc);
                split.AddInput(inputFile, inputOptions.ToArray());
                split.AddGlobalOptions(MkvSplitExternalTools.ClipLacingOptions(tracks));
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.mkvmergeSplit", startTc, endTc));
                MkvSplitExternalTools.Instance.RunMkvmerge(split.ToArguments());
            }

            sizeMb = new FileInfo(outputFile).Length / 1048576.0;
            ConsoleHelper.Write(LogSection.Split, LogLevel.Success, AppText.F("split.exec.ok", Path.GetFileName(outputFile), sizeMb.ToString("F1", CultureInfo.InvariantCulture)));
        }

        #endregion
    }
}
