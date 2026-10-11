using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;

namespace RemuxForge.Core.Splitting
{
    /// <summary>Produce il file di output di una clip copiata senza ricodifica (mkvmerge --split parts, FFmpeg per il FLAC); il taglio al fotogramma lo fanno le strategie di MkvSplitExactCutterBase.</summary>
    public class MkvSplitExecutor
    {
        #region Costruttore

        /// <summary>Costruttore vuoto: il servizio è stateless.</summary>
        public MkvSplitExecutor()
        {
        }

        #endregion

        #region Pipeline fast (ffmpeg seek + stream copy)

        /// <summary>Fast path: mkvmerge --split parts per video e sottotitoli e, quando presente FLAC, ffmpeg stream copy per l'audio.</summary>
        /// <param name="seg">Segmento da elaborare.</param>
        /// <param name="inputFile">File MKV di input.</param>
        /// <param name="outputFile">Path del file di output.</param>
        /// <param name="tempDir">Directory temporanea per i file intermedi.</param>
        /// <param name="tracks">Tracce del file normalizzato, nel loro ordine: con il FLAC si separa il video con mkvmerge e l'audio con ffmpeg, perché mkvmerge non splitta FLAC insieme al video.</param>
        /// <param name="subtitleIds">Tracce di sottotitoli che mkvmerge taglia insieme al video, vuota se non ce ne sono.</param>
        public void SplitFast(MkvSplitSegment seg, string inputFile, string outputFile, string tempDir, List<TrackInfo> tracks, List<int> subtitleIds)
        {
            string startTc;
            string endTc;
            List<string> muxArgs;
            double sizeMb;
            List<string> subtitleArgs = subtitleIds.Count > 0 ? new List<string> { "--subtitle-tracks", string.Join(",", subtitleIds) } : new List<string> { "--no-subtitles" };

            startTc = Utils.SecsToTs(seg.StartTs);
            endTc = Utils.SecsToTs(seg.EndTs);

            if (tracks.Exists(MkvSplitExternalTools.IsFlacAudio))
            {
                string videoMkv = Path.Combine(tempDir, "video.mkv");
                string avFile = Path.Combine(tempDir, "av.mkv");
                List<string> videoArgs = new List<string>();
                List<string> avArgs = new List<string>();
                int fromVideo = 0;
                int fromAudio = 0;
                int avExit;
                bool hasAv;

                // mkvmerge non splitta FLAC insieme al video; si splittano video e sottotitoli separatamente e si rimuxa l'audio estratto da FFmpeg
                videoArgs.Add("-o"); videoArgs.Add(videoMkv);
                videoArgs.Add("--no-audio");
                videoArgs.AddRange(subtitleArgs);
                videoArgs.Add("--no-chapters");
                videoArgs.Add("--split"); videoArgs.Add("parts:" + startTc + "-" + endTc);
                videoArgs.Add(inputFile);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.mkvmergeVideoSplit", startTc, endTc));
                MkvSplitExternalTools.Instance.RunMkvmerge(videoArgs);

                avArgs.Add("-y"); avArgs.Add("-hide_banner"); avArgs.Add("-loglevel"); avArgs.Add("warning");
                avArgs.Add("-i"); avArgs.Add(inputFile);
                avArgs.Add("-ss"); avArgs.Add(startTc);
                avArgs.Add("-to"); avArgs.Add(endTc);
                avArgs.Add("-map"); avArgs.Add("0:a?");
                avArgs.Add("-c:a"); avArgs.Add("copy");
                avArgs.Add("-vn");
                avArgs.Add("-avoid_negative_ts"); avArgs.Add("make_zero");
                avArgs.Add("-map_chapters"); avArgs.Add("-1");
                avArgs.Add(avFile);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.ffmpegAvCopy", startTc, endTc));
                avExit = MkvSplitExternalTools.Instance.RunFfmpegNoThrow(avArgs);
                hasAv = avExit == 0 && File.Exists(avFile) && new FileInfo(avFile).Length > 0;
                if (!hasAv) throw new InvalidOperationException(AppText.F("split.montage.avFailed", avExit));

                // Il video di una parte che inizia su un keyframe di GOP aperto parte dai pacchetti dei B iniziali e mostra il
                // keyframe dopo la loro durata (D25); ffmpeg taglia l'audio dal keyframe e lo porta a 0: si sposta della stessa
                // durata, come fa mkvmerge quando taglia video e audio insieme
                MkvmergeCommand mux = new MkvmergeCommand(outputFile);
                int videoInput = mux.AddInput(videoMkv, "--no-chapters");
                int audioInput = seg.OpenGopHeadSeconds > 0
                    ? mux.AddInput(avFile, "--sync", "-1:" + Math.Round(seg.OpenGopHeadSeconds * 1000.0).ToString(CultureInfo.InvariantCulture))
                    : mux.AddInput(avFile);
                // Le tracce tornano nell'ordine del sorgente, che le clip accodate devono condividere
                foreach (TrackInfo track in tracks)
                {
                    if (track.Type == "audio")
                        mux.Place(track.Id, audioInput, fromAudio++);
                    else if (track.Type == "video" || (track.Type == "subtitles" && subtitleIds.Contains(track.Id)))
                        mux.Place(track.Id, videoInput, fromVideo++);
                }
                mux.AddTrackOrder();
                muxArgs = mux.ToArguments();
                MkvSplitExternalTools.Instance.RunMkvmerge(muxArgs);
            }
            else
            {
                // Non-FLAC: mkvmerge --split parts per taglio frame-perfect (preserva VFR nativamente)
                muxArgs = new List<string>();
                muxArgs.Add("-o"); muxArgs.Add(outputFile);
                muxArgs.Add("--no-chapters");
                muxArgs.AddRange(subtitleArgs);
                muxArgs.Add("--split"); muxArgs.Add("parts:" + startTc + "-" + endTc);
                muxArgs.Add(inputFile);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.exec.mkvmergeSplit", startTc, endTc));
                MkvSplitExternalTools.Instance.RunMkvmerge(muxArgs);
            }

            sizeMb = new FileInfo(outputFile).Length / 1048576.0;
            ConsoleHelper.Write(LogSection.Split, LogLevel.Success, AppText.F("split.exec.ok", Path.GetFileName(outputFile), sizeMb.ToString("F1", CultureInfo.InvariantCulture)));
        }

        #endregion
    }
}
