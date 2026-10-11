using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace RemuxForge.Core.Splitting.Execution
{
    /// <summary>
    /// Clip di un output, una cartella di lavoro per clip: copiate dal Matroska normalizzato oppure tagliate al fotogramma dalla
    /// strategia del codec, verificate nel numero di pacchetti, riportate a zero e, alla giunta, con l'audio fermo alla fine del video
    /// </summary>
    public sealed class MkvSplitClipProducer
    {
        #region Variabili di classe

        /// <summary>
        /// Piano eseguibile
        /// </summary>
        private readonly MkvSplitExecutionPlan _plan;

        /// <summary>
        /// Media preparati per il piano
        /// </summary>
        private readonly MkvSplitPlanMedia _media;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="plan">Piano eseguibile</param>
        /// <param name="media">Media preparati per il piano</param>
        public MkvSplitClipProducer(MkvSplitExecutionPlan plan, MkvSplitPlanMedia media)
        {
            this._plan = plan;
            this._media = media;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Produce le clip di un output nell'ordine del montaggio
        /// </summary>
        /// <param name="output">Output del piano eseguibile</param>
        /// <param name="temporary">Cartella temporanea dell'output</param>
        /// <param name="junctionTrim">Taglio dell'audio alla giunta dell'output</param>
        /// <param name="cancellationToken">Annullamento controllato prima di ogni clip</param>
        /// <returns>Percorsi delle clip, clip-N/clip.mkv nella cartella temporanea</returns>
        /// <exception cref="InvalidOperationException">Clip con un numero di pacchetti diverso da quello atteso</exception>
        public List<string> Produce(MkvSplitExecutionOutput output, string temporary, MkvSplitJunctionAudioTrim junctionTrim, CancellationToken cancellationToken)
        {
            MkvSplitExecutor executor = new MkvSplitExecutor();
            List<string> clips = new List<string>();
            string clipSubtitles = output.FallbackSubtitleTrackIds.Count > 0 ? this._media.FallbackSubtitles : null;
            foreach (MkvSplitExecutionClip clip in output.Clips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string work = Path.Combine(temporary, "clip-" + clips.Count);
                Directory.CreateDirectory(work);
                string clipPath = Path.Combine(work, "clip.mkv");
                if (clip.UsesFastPath) executor.SplitFast(clip.Segment, this._media.MediaPath, clipPath, work, this._plan.Tracks, output.FallbackSubtitleTrackIds);
                else this._media.Cutter.Split(new MkvSplitExactCutContext { Segment = clip.Segment, OutputFile = clipPath, TempDir = work, SubtitleIds = output.FallbackSubtitleTrackIds, Subtitles = clipSubtitles });
                // Il keyframe di GOP aperto toglie i B iniziali alla fine e li lascia come pacchetti in testa (D25): né più né meno
                int clipPackets = clip.Segment.FrameCount + clip.OpenGop.HeadPackets + clip.OpenGop.DroppedPackets - clip.OpenGop.LostFrames;
                if (MkvSplitExternalTools.Instance.CountPackets(clipPath) != clipPackets)
                    throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
                // mkvmerge riporta a 0 le parti che tagliano dentro il file, non quella che parte dal suo primo keyframe:
                // nel normalizzato di un TS o di un M2TS il video inizia qualche millisecondo dopo l'audio e la clip
                // resterebbe spostata rispetto alla proiezione. Lo spostamento si toglie a tutte le tracce, così
                // l'offset fra audio e video resta quello del sorgente. Con i B iniziali da togliere (§11 (q)) lo zero
                // va sul keyframe: i loro pacchetti finiscono a tempi negativi e mkvmerge li scarta, mentre l'audio,
                // che mkvmerge taglia dal keyframe, resta intero
                if (clip.UsesFastPath)
                {
                    long shift = (long)Math.Round(MkvSplitExternalTools.Instance.ExtractSourcePts(clipPath, clipPackets, out _)[clip.OpenGop.DroppedPackets] * 1000.0);
                    if (shift != 0)
                    {
                        string shifted = Path.Combine(work, "clip-shifted.mkv");
                        MkvSplitExternalTools.Instance.RunMkvmerge(new string[] { "-o", shifted, "--sync", "-1:" + (-shift).ToString(CultureInfo.InvariantCulture), clipPath });
                        File.Move(shifted, clipPath, true);
                    }
                    if (clip.OpenGop.DroppedPackets > 0 && MkvSplitExternalTools.Instance.CountPackets(clipPath) != clipPackets - clip.OpenGop.DroppedPackets)
                        throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
                }
                if (clips.Count < output.Clips.Count - 1)
                    junctionTrim.Apply(clipPath, work, clipPackets - clip.OpenGop.DroppedPackets);
                clips.Add(clipPath);
            }
            return clips;
        }

        #endregion
    }
}
