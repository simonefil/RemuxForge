using RemuxForge.Core.Chapters;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RemuxForge.Core.Splitting.Execution
{
    /// <summary>
    /// Tempi di un output come li mostra il video copiato. I pacchetti dei B iniziali che restano in testa a una clip (D25, §11 (q))
    /// la allungano: il suo keyframe e tutto ciò che segue arrivano dopo la loro durata. I B iniziali che una clip perde alla fine la
    /// accorciano: il suo video finisce prima del taglio e la clip successiva parte prima della stessa durata.
    /// La proiezione mette ogni clip dove inizia il suo keyframe senza i B iniziali; il video copiato lo mostra dopo quelli rimasti in
    /// testa alla clip e alle precedenti e prima di quelli che le precedenti hanno perso alla fine: sottotitoli e capitoli riscritti
    /// seguono il video, e alla giunta i sottotitoli della clip si fermano alla fine del suo video. Un capitolo in apertura di clip,
    /// entro l'arrotondamento al fotogramma, resta sul suo inizio, con i B iniziali; gli altri seguono il keyframe (§11 (r))
    /// </summary>
    public sealed class MkvSplitOpenGopTimeline
    {
        #region Variabili di classe

        /// <summary>
        /// Output del piano eseguibile
        /// </summary>
        private readonly MkvSplitExecutionOutput _output;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore: posiziona clip e capitoli dell'output
        /// </summary>
        /// <param name="output">Output del piano eseguibile</param>
        public MkvSplitOpenGopTimeline(MkvSplitExecutionOutput output)
        {
            this._output = output;
            this.Placed = new MkvSplitOutputProjection { OutputId = output.OutputId, DurationSeconds = output.Projection.DurationSeconds, FrameCount = output.Projection.FrameCount };
            this.Chapters = ChapterTimelineService.Clone(output.Projection.Chapters);
            double headsBefore = 0;
            double lostsBefore = 0;
            long clipNs = 0;
            for (int index = 0; index < output.Projection.Clips.Count; index++)
            {
                MkvSplitClipProjection clip = output.Projection.Clips[index];
                MkvSplitOpenGopJoin openGop = output.Clips[index].OpenGop;
                long nextNs = clipNs + (long)Math.Round(clip.SourceEndSeconds * 1000000000.0) - (long)Math.Round(clip.SourceStartSeconds * 1000000000.0);
                this.Placed.Clips.Add(new MkvSplitClipProjection
                {
                    ClipId = clip.ClipId,
                    StartFrame = clip.StartFrame,
                    EndFrameExclusive = clip.EndFrameExclusive,
                    ResultStartFrame = clip.ResultStartFrame,
                    SourceStartSeconds = clip.SourceStartSeconds,
                    SourceEndSeconds = index < output.Projection.Clips.Count - 1 ? clip.SourceEndSeconds - openGop.LostSeconds : clip.SourceEndSeconds,
                    ResultStartSeconds = clip.ResultStartSeconds + headsBefore + openGop.HeadSeconds - lostsBefore,
                    DurationSeconds = clip.DurationSeconds
                });
                // Stessi confini in nanosecondi del montaggio dei capitoli (ChapterTimelineService.Montage)
                for (int position = 0; position < this.Chapters.Count; position++)
                {
                    long start = output.Projection.Chapters[position].StartNs;
                    if (start < clipNs || start >= nextNs)
                        continue;
                    if (this.Chapters[position].HasEnd)
                        this.Chapters[position].EndNs += (long)Math.Round((headsBefore + openGop.HeadSeconds - lostsBefore) * 1000000000.0);
                    this.Chapters[position].StartNs += (long)Math.Round((start - clipNs <= ChapterTimelineService.FRAME_ROUNDING_NS ? headsBefore : headsBefore + openGop.HeadSeconds) * 1000000000.0 - lostsBefore * 1000000000.0);
                }
                headsBefore += openGop.HeadSeconds;
                lostsBefore += openGop.LostSeconds;
                clipNs = nextNs;
            }
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Proiezione dell'output con le clip dove le mostra il video copiato: la riscrittura dei sottotitoli la segue
        /// </summary>
        public MkvSplitOutputProjection Placed { get; }

        /// <summary>
        /// Capitoli dell'output spostati come il video copiato
        /// </summary>
        public List<ChapterMark> Chapters { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Verifica che il video dell'output abbia esattamente i fotogrammi e i tempi della proiezione. Su un keyframe di GOP
        /// aperto copiato (D25) una clip può portare in testa i pacchetti dei B iniziali, che spostano lei e le successive
        /// della loro durata, o averli tolti (§11 (q)); alla fine perde i propri e anticipa le successive: né più né meno
        /// </summary>
        /// <param name="path">Output completo da verificare</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <exception cref="InvalidOperationException">Fotogrammi o tempi diversi dalla proiezione</exception>
        public void Validate(string path, MkvSplitAnalysis analysis)
        {
            int packets = MkvSplitExternalTools.Instance.CountPackets(path);
            double[] actual = MkvSplitExternalTools.Instance.ExtractSourcePts(path, packets, out double end);
            if (actual.Length != this._output.Projection.FrameCount + this._output.Clips.Sum(clip => clip.OpenGop.HeadPackets - clip.OpenGop.LostFrames) || !double.IsFinite(end)
                || Math.Abs(end - (this._output.Projection.DurationSeconds + this._output.Clips.Sum(clip => clip.OpenGop.HeadSeconds) - this._output.Clips.Sum(clip => clip.OpenGop.LostSeconds))) > 0.001001)
                throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
            int index = 0;
            double shift = 0;
            for (int position = 0; position < this._output.Projection.Clips.Count; position++)
            {
                MkvSplitClipProjection clip = this._output.Projection.Clips[position];
                MkvSplitOpenGopJoin openGop = this._output.Clips[position].OpenGop;
                shift += openGop.HeadSeconds;
                for (int frame = clip.StartFrame - openGop.HeadPackets; frame < clip.EndFrameExclusive - openGop.LostFrames; frame++)
                    if (Math.Abs(actual[index++] - (clip.ResultStartSeconds + shift + analysis.SourcePts[frame] - clip.SourceStartSeconds)) > 0.001001)
                        throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
                shift -= openGop.LostSeconds;
            }
        }

        #endregion
    }
}
