using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Cutters;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Splitting.Planning
{
    /// <summary>
    /// Come si taglia una clip del piano eseguibile: confini copiati o ricodificati, B iniziali dei keyframe di GOP aperto ai due
    /// confini, fotogrammi della testa da ricodificare e diagnostiche della clip, nell'ordine in cui il piano le riporta
    /// </summary>
    public sealed class MkvSplitClipCutDecision
    {
        #region Variabili di classe

        /// <summary>
        /// Clip della proiezione
        /// </summary>
        private readonly MkvSplitClipProjection _clip;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore: decide il taglio della clip
        /// </summary>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="profile">Profilo del video del sorgente</param>
        /// <param name="cutter">Strategia del taglio al fotogramma del codec, null se il codec non ne ha: il costo della testa segue
        /// allora la regola del taglio a byte</param>
        /// <param name="projection">Proiezione del documento, con tutti gli output</param>
        /// <param name="output">Output della clip</param>
        /// <param name="clip">Clip da decidere</param>
        public MkvSplitClipCutDecision(MkvSplitAnalysis analysis, MkvSplitVideoProfile profile, MkvSplitExactCutterBase cutter, MkvSplitTimelineProjection projection,
            MkvSplitOutputProjection output, MkvSplitClipProjection clip)
        {
            this._clip = clip;
            this.Diagnostics = new List<MkvSplitDiagnostic>();
            this.StartsOnKeyframe = clip.StartFrame < analysis.KeyFlags.Count && analysis.KeyFlags[clip.StartFrame].Key;
            bool endKey = clip.EndFrameExclusive == analysis.SourcePts.Length
                || (clip.EndFrameExclusive < analysis.KeyFlags.Count && analysis.KeyFlags[clip.EndFrameExclusive].Key);
            // Un keyframe di GOP aperto si taglia dallo slow path, che ricodifica solo i fotogrammi che lo attraversano.
            // Sull'interlacciato si copia (D25). In apertura i B iniziali restano come pacchetti in testa; in una giunta
            // interna restano solo se si decodificano da soli (closed_gop=1), altrimenti seguirebbero la clip precedente
            // e si decodificherebbero con i suoi riferimenti, quindi si tolgono (§11 (q)). In chiusura la clip perde i
            // suoi B iniziali; se la segue un'altra clip, alla giunta il suo audio si ferma alla fine del video (ExecutePlan)
            bool startOpen = this.StartsOnKeyframe && analysis.KeyFlags[clip.StartFrame].OpenGop;
            bool endOpen = endKey && clip.EndFrameExclusive < analysis.SourcePts.Length && analysis.KeyFlags[clip.EndFrameExclusive].OpenGop;
            this.StartCopy = this.StartsOnKeyframe && (!startOpen || profile.IsCopyCutKeyframe(analysis.KeyFlags[clip.StartFrame]));
            this.EndCopy = endKey && (!endOpen || profile.IsCopyCutKeyframe(analysis.KeyFlags[clip.EndFrameExclusive]));
            bool keyframeOnlyVideo = false;
            // Una clip da ricodificare su un video che si taglia solo al keyframe, con il motivo: codec, interlacciato o,
            // con entrambi i confini su keyframe dell'interlacciato, il GOP aperto
            if (!this.UsesFastPath && !profile.IsExactCutAvailable)
            {
                keyframeOnlyVideo = true;
                if (this.StartsOnKeyframe && endKey && profile.ScanType.IsInterlaced)
                {
                    int frame = this.StartCopy ? clip.EndFrameExclusive : clip.StartFrame;
                    this.Diagnostics.Add(new MkvSplitDiagnostic
                    {
                        Code = "openGop",
                        Message = AppText.F("split.openGopRejected", profile.ScanType, frame, Utils.SecsToTs(analysis.SourcePts[frame])),
                        Severity = MkvSplitDiagnosticSeverity.Error,
                        OutputId = output.OutputId,
                        ClipId = clip.ClipId
                    });
                }
                else
                {
                    string keyframeOnly = profile.KeyframeOnlyReason;
                    MkvSplitDiagnostic unsupported = MkvSplitDocumentService.Diagnostic("unsupportedVideo", output.OutputId, clip.ClipId, keyframeOnly);
                    unsupported.Reason = keyframeOnly;
                    this.Diagnostics.Add(unsupported);
                }
            }
            int leading = this.UsesFastPath && startOpen ? analysis.KeyFlags[clip.StartFrame].LeadingFrames : 0;
            bool decodable = leading > 0 && analysis.KeyFlags[clip.StartFrame].LeadingDecodable;
            int headPackets = clip == output.Clips[0] || decodable ? leading : 0;
            int lostFrames = this.UsesFastPath && endOpen ? analysis.KeyFlags[clip.EndFrameExclusive].LeadingFrames : 0;
            this.OpenGop = new MkvSplitOpenGopJoin(analysis.SourcePts, clip.StartFrame, clip.EndFrameExclusive, clip.SourceEndSeconds, leading, headPackets, lostFrames);
            // Con closed_gop=1 i B iniziali che la clip perde si ritrovano in testa alla clip che inizia sul keyframe, in qualsiasi output
            bool shownElsewhere = lostFrames > 0 && analysis.KeyFlags[clip.EndFrameExclusive].LeadingDecodable
                && projection.Outputs.Exists(other => other.Clips.Exists(next => next.StartFrame == clip.EndFrameExclusive));
            if (leading > 0)
                this.Diagnostics.Add(new MkvSplitDiagnostic
                {
                    Code = decodable ? "openGopShift" : headPackets > 0 ? "openGopHead" : "openGopDropped",
                    Message = decodable ? AppText.F("split.openGopShift", output.FileName, leading, Math.Round(this.OpenGop.LeadingSeconds * 1000.0).ToString(CultureInfo.InvariantCulture))
                        : AppText.F(headPackets > 0 ? "split.openGopHead" : "split.openGopDropped", output.FileName, leading),
                    Severity = MkvSplitDiagnosticSeverity.Warning,
                    OutputId = output.OutputId,
                    ClipId = clip.ClipId
                });
            if (lostFrames > 0 && !shownElsewhere)
                this.Diagnostics.Add(new MkvSplitDiagnostic
                {
                    Code = "openGopCut",
                    Message = AppText.F("split.openGopCut", output.FileName, lostFrames,
                        Math.Round((analysis.SourcePts[clip.EndFrameExclusive] - analysis.SourcePts[clip.EndFrameExclusive - lostFrames]) * 1000.0).ToString(CultureInfo.InvariantCulture)),
                    Severity = MkvSplitDiagnosticSeverity.Warning,
                    OutputId = output.OutputId,
                    ClipId = clip.ClipId
                });
            if (!this.StartsOnKeyframe)
            {
                MkvSplitKeyframeIndex keyframes = new MkvSplitKeyframeIndex(analysis.KeyFlags);
                int headCount = cutter != null ? cutter.HeadReencodeCount(keyframes, clip.StartFrame, clip.FrameCount)
                    : MkvSplitByteExactCutter.LookaheadHeadReencodeCount(keyframes, clip.StartFrame, clip.FrameCount);
                // Sul video che si taglia solo al keyframe il motivo è già detto: la testa non si ricodifica comunque
                if (headCount < 0 && !keyframeOnlyVideo)
                    this.Diagnostics.Add(MkvSplitDocumentService.Diagnostic("shortClip", output.OutputId, clip.ClipId));
                else if (headCount >= 0)
                    this.ReencodeFrames = headCount;
            }
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True se la clip inizia su un keyframe
        /// </summary>
        public bool StartsOnKeyframe { get; }

        /// <summary>
        /// True se il confine d'inizio si taglia senza ricodifica
        /// </summary>
        public bool StartCopy { get; }

        /// <summary>
        /// True se il confine di fine si taglia senza ricodifica
        /// </summary>
        public bool EndCopy { get; }

        /// <summary>
        /// True se la clip si copia senza ricodifica: entrambi i confini si tagliano copiando
        /// </summary>
        public bool UsesFastPath => this.StartCopy && this.EndCopy;

        /// <summary>
        /// B iniziali dei keyframe di GOP aperto ai due confini della clip copiata: copiati in testa, rimasti, tolti e persi alla fine
        /// </summary>
        public MkvSplitOpenGopJoin OpenGop { get; }

        /// <summary>
        /// Fotogrammi della testa da ricodificare fino al primo keyframe, zero se la clip inizia su un keyframe o non si ricodifica
        /// </summary>
        public int ReencodeFrames { get; }

        /// <summary>
        /// Diagnostiche della clip, nell'ordine del piano: video solo al keyframe, B iniziali in testa, B iniziali persi, clip corta
        /// </summary>
        public List<MkvSplitDiagnostic> Diagnostics { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Clip del piano eseguibile con il segmento da estrarre dal sorgente
        /// </summary>
        /// <returns>Clip eseguibile</returns>
        public MkvSplitExecutionClip ToExecutionClip()
        {
            return new MkvSplitExecutionClip
            {
                UsesFastPath = this.UsesFastPath,
                OpenGop = this.OpenGop,
                Segment = new MkvSplitSegment
                {
                    StartFrame = this._clip.StartFrame,
                    FrameCount = this._clip.FrameCount,
                    StartTs = this._clip.SourceStartSeconds,
                    EndTs = this._clip.SourceEndSeconds,
                    StartsOnKeyframe = this.StartsOnKeyframe,
                    ReencodeFrames = this.ReencodeFrames,
                    // La clip copiata contiene i pacchetti di tutti i B iniziali finché ExecutePlan non toglie quelli da togliere
                    OpenGopHeadSeconds = this.OpenGop.LeadingSeconds
                }
            };
        }

        #endregion
    }
}
