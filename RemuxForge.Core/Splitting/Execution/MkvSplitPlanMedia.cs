using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Cutters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RemuxForge.Core.Splitting.Execution
{
    /// <summary>
    /// Media preparati una volta per tutti gli output di un piano eseguibile: il Matroska normalizzato verificato contro l'analisi, la
    /// cartella del piano, la strategia del taglio al fotogramma con flusso grezzo, mappe e argomenti della testa, e i sottotitoli da
    /// tagliare al pacchetto. Il rilascio cancella la cartella del piano e restituisce il Matroska
    /// </summary>
    public sealed class MkvSplitPlanMedia : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Lease del Matroska normalizzato, null finché non preparato
        /// </summary>
        private NormalizedMediaLease _lease;

        /// <summary>
        /// Cartella temporanea del piano, null finché non preparata
        /// </summary>
        private string _planFolder;

        #endregion

        #region Proprietà

        /// <summary>
        /// Matroska normalizzato da cui si leggono video, tracce e allegati; null se nessun output si rifà
        /// </summary>
        public string MediaPath { get; private set; }

        /// <summary>
        /// Strategia del taglio al fotogramma preparata, null se nessuna clip da rifare si ricodifica
        /// </summary>
        public MkvSplitExactCutterBase Cutter { get; private set; }

        /// <summary>
        /// Sottotitoli che il ritaglio non sa riscrivere, da tagliare al pacchetto nelle clip ricodificate; null se non servono
        /// </summary>
        public string FallbackSubtitles { get; private set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Prende il Matroska e prepara ciò che serve agli output da rifare. Gli output che non si rifanno non prendono il
        /// Matroska né preparano nulla
        /// </summary>
        /// <param name="plan">Piano eseguibile</param>
        /// <param name="args">Opzioni di esecuzione</param>
        /// <param name="running">Output da rifare</param>
        /// <exception cref="InvalidOperationException">Matroska rifatto con altri fotogrammi o video che si taglia solo al keyframe</exception>
        public void Prepare(MkvSplitExecutionPlan plan, MkvSplitOptions args, List<MkvSplitExecutionOutput> running)
        {
            // Il sorgente resta identità e nome; video, tracce e allegati si leggono dal Matroska normalizzato. Rifatto
            // dopo l'analisi, deve avere gli stessi fotogrammi con gli stessi tempi: il piano li indirizza per indice
            this._lease = NormalizedMediaStore.Instance.Acquire(plan.Document.Source.FullPath, true);
            this.MediaPath = this._lease.Path;
            if (this._lease.IsTemporary && !ReferenceEquals(this._lease.VerifiedContent, plan.Analysis))
            {
                int count = MkvSplitExternalTools.Instance.CountPackets(this.MediaPath);
                double[] pts = MkvSplitExternalTools.Instance.ExtractSourcePts(this.MediaPath, count, out _);
                if (count != plan.Analysis.PacketCount || !pts.AsSpan().SequenceEqual(plan.Analysis.SourcePts))
                    throw new InvalidOperationException(AppText.T("split.montage.sourceContentChanged"));
                this._lease.VerifiedContent = plan.Analysis;
            }
            this._planFolder = Path.Combine(AppSettingsService.Instance.GetTempFolder(), "split_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(this._planFolder);
            List<MkvSplitExecutionOutput> exact = running.FindAll(output => output.Clips.Exists(clip => !clip.UsesFastPath));
            if (exact.Count > 0)
            {
                MkvSplitVideoProfile profile = plan.Analysis.VideoProfile;
                if (!profile.IsExactCutAvailable)
                    throw new InvalidOperationException(profile.KeyframeOnlyReason);
                // La strategia la sceglie il codec: il taglio a byte estrae qui il flusso grezzo, quello a pacchetti legge il
                // Matroska normalizzato
                this.Cutter = profile.ExactCutCodec.CreateCutter();
                this.Cutter.Prepare(this.MediaPath, this._planFolder, plan, args);
                // Sottotitoli che il ritaglio non sa riscrivere: le clip ricodificate li tagliano al pacchetto da qui
                List<int> fallback = exact.SelectMany(output => output.FallbackSubtitleTrackIds).Distinct().ToList();
                if (fallback.Count > 0)
                {
                    this.FallbackSubtitles = Path.Combine(this._planFolder, "subtitles.mks");
                    MkvSplitExternalTools.Instance.RunMkvmerge(new string[] { "-o", this.FallbackSubtitles, "--no-video", "--no-audio", "--no-chapters", "--no-attachments", "--no-global-tags",
                        "--subtitle-tracks", string.Join(",", plan.Tracks.FindAll(track => fallback.Contains(track.Id)).ConvertAll(track => track.Id)), this.MediaPath });
                }
            }
        }

        /// <summary>
        /// Cancella la cartella del piano e rilascia il Matroska normalizzato
        /// </summary>
        public void Dispose()
        {
            if (this._planFolder != null) try { Directory.Delete(this._planFolder, true); } catch (IOException) { }
            this._planFolder = null;
            this._lease?.Dispose();
            this._lease = null;
        }

        #endregion
    }
}
