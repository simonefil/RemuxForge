using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RemuxForge.Core.Splitting.Planning
{
    /// <summary>
    /// Strada dei sottotitoli di ogni output del piano eseguibile: copiati nativi dal Matroska, tagliati al pacchetto con le clip
    /// oppure riscritti dagli eventi letti nell'analisi. Un output che ripete il sorgente intero li copia nativi quando può
    /// </summary>
    public sealed class MkvSplitSubtitleRouter
    {
        #region Variabili di classe

        /// <summary>
        /// Piano eseguibile, con le tracce già assegnate
        /// </summary>
        private readonly MkvSplitExecutionPlan _plan;

        /// <summary>
        /// Analisi del sorgente, con inventario ed eventi dei sottotitoli
        /// </summary>
        private readonly MkvSplitAnalysis _analysis;

        /// <summary>
        /// Output che ripetono il sorgente intero, una clip dopo l'altra senza buchi
        /// </summary>
        private readonly List<Guid> _unchangedOutputs;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="plan">Piano eseguibile, con le tracce già assegnate</param>
        /// <param name="analysis">Analisi del sorgente</param>
        public MkvSplitSubtitleRouter(MkvSplitExecutionPlan plan, MkvSplitAnalysis analysis)
        {
            this._plan = plan;
            this._analysis = analysis;
            this._unchangedOutputs = new List<Guid>();
            foreach (MkvSplitOutputProjection output in plan.Projection.Outputs)
            {
                int next = 0;
                bool unchanged = true;
                foreach (MkvSplitClipProjection clip in output.Clips)
                {
                    if (clip.StartFrame != next)
                        unchanged = false;
                    next = clip.EndFrameExclusive;
                }
                if (unchanged && next == analysis.SourcePts.Length)
                    this._unchangedOutputs.Add(output.OutputId);
            }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Assegna le tracce di sottotitoli agli output. I codec che il ritaglio non sa riscrivere li taglia mkvmerge al pacchetto;
        /// gli altri, se il piano è valido, si riscrivono, con le diagnostiche degli effetti a tempo tagliati da un confine
        /// </summary>
        public void Route()
        {
            List<MkvSplitOutputProjection> cutOutputs = this._plan.Projection.Outputs.Where(output => !this._unchangedOutputs.Contains(output.OutputId)).ToList();
            // I codec che il ritaglio non sa riscrivere li taglia mkvmerge al pacchetto insieme alle clip
            foreach (TrackInfo track in this._analysis.Tracks.Where(item => item.Type == "subtitles" && !MkvSplitSubtitleClippers.IsSupported(item)))
            {
                foreach (MkvSplitExecutionOutput output in this._plan.Outputs)
                {
                    if (this._unchangedOutputs.Contains(output.OutputId))
                        output.NativeSubtitleTrackIds.Add(track.Id);
                    else
                        output.FallbackSubtitleTrackIds.Add(track.Id);
                }
            }
            if (!this._plan.IsValid)
                return;
            foreach (MkvSplitSubtitleTrack subtitle in this._analysis.Subtitles)
            {
                TrackInfo track = subtitle.Track;
                bool bitmap = subtitle.Clipper.IsBitmap;
                if (bitmap)
                {
                    foreach (MkvSplitExecutionOutput output in this._plan.Outputs.Where(output => this._unchangedOutputs.Contains(output.OutputId)))
                        output.NativeSubtitleTrackIds.Add(track.Id);
                    if (cutOutputs.Count == 0)
                        continue;
                }
                this._plan.Subtitles.Add(subtitle);
                // Una traccia sorgente senza eventi resta vuota in ogni taglio: si copia nativa, senza riscriverla
                if (subtitle.Events.Count == 0)
                {
                    foreach (MkvSplitExecutionOutput output in this._plan.Outputs.Where(output => !output.NativeSubtitleTrackIds.Contains(track.Id)))
                        output.NativeSubtitleTrackIds.Add(track.Id);
                    continue;
                }
                bool fitsVideo = subtitle.Events.All(item => item.StartSeconds >= this._analysis.SourcePts[0] && item.EndSeconds <= this._analysis.Duration);
                if (fitsVideo && !bitmap)
                {
                    foreach (MkvSplitExecutionOutput output in this._plan.Outputs.Where(output => this._unchangedOutputs.Contains(output.OutputId)))
                        output.NativeSubtitleTrackIds.Add(track.Id);
                }
                foreach (MkvSplitOutputProjection output in this._plan.Projection.Outputs.Where(output => !this._plan.Outputs.Find(item => item.OutputId == output.OutputId).NativeSubtitleTrackIds.Contains(track.Id)))
                {
                    foreach (MkvSplitClipProjection clip in output.Clips)
                    {
                        foreach (MkvSplitSubtitleEvent item in subtitle.Events.Where(item => item.HasTimedEffects))
                        {
                            if (item.StartSeconds < clip.SourceEndSeconds && item.EndSeconds > clip.SourceStartSeconds
                                && (item.StartSeconds < clip.SourceStartSeconds || item.EndSeconds > clip.SourceEndSeconds))
                                this._plan.Projection.Diagnostics.Add(MkvSplitDocumentService.Diagnostic("subtitleTimedBoundary", output.OutputId, clip.ClipId,
                                    track.Id, clip.SourceStartSeconds, clip.SourceEndSeconds));
                        }
                    }
                }
            }
        }

        #endregion
    }
}
