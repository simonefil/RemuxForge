using RemuxForge.Core.Chapters;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Cutters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Costruisce il piano di taglio di un file: è la sorgente unica usata da UI, CLI ed esecuzione
    /// </summary>
    public class MkvSplitPlanner
    {
        #region Metodi pubblici

        /// <summary>
        /// Costruisce il piano di taglio per un singolo file
        /// </summary>
        /// <param name="args">Opzioni split già clonate per il file</param>
        /// <param name="inputFile">File sorgente</param>
        /// <param name="phaseCallback">Callback opzionale invocata a ogni fase dell'analisi</param>
        /// <returns>Piano del file, valido oppure con il motivo dell'invalidità</returns>
        public MkvSplitPlan BuildPlan(MkvSplitOptions args, string inputFile, Action<string> phaseCallback)
        {
            MkvSplitPlan plan = new MkvSplitPlan();
            MkvSplitAnalysis analysis;
            MkvSplitSegmentService segmentService;
            (List<MkvSplitSegment> segments, MkvSplitMode mode) built;

            plan.InputFile = Path.GetFullPath(inputFile);
            plan.OutputDir = args.ResolveOutputDir(plan.InputFile);

            if (!File.Exists(plan.InputFile))
            {
                plan.ErrorMessage = AppText.F("split.fileNotFound", plan.InputFile);
                return plan;
            }

            try
            {
                if (phaseCallback != null) { phaseCallback(AppText.F("split.plan.analyzing", Path.GetFileName(plan.InputFile))); }
                analysis = MkvSplitAnalysisCache.Instance.GetOrBuild(plan.InputFile);
            }
            catch (Exception ex)
            {
                plan.ErrorMessage = ex.Message;
                return plan;
            }

            plan.Chapters = analysis.Chapters;
            foreach (string chapterWarning in analysis.ChapterWarnings)
                plan.Warnings.Add(new MkvSplitWarning(MkvSplitWarningKind.ChapterNotCarried, chapterWarning));
            plan.Duration = analysis.Duration;
            plan.SourcePts = analysis.SourcePts;
            plan.FrameCount = analysis.SourcePts.Length;
            plan.VideoParams = analysis.VideoParams;
            plan.FrameRateMode = analysis.FrameRateMode;

            // La coerenza fra PTS e packet è la precondizione di ogni mappatura frame -> byte
            if (analysis.PacketCount != analysis.SourcePts.Length)
            {
                plan.ErrorMessage = AppText.F("split.frameCountMismatch", analysis.SourcePts.Length, analysis.PacketCount);
                return plan;
            }

            // Un template non valido va detto prima di costruire i segmenti, non a metà rendering
            List<string> templateErrors = MkvSplitSegmentService.ValidateTemplate(args.OutputTemplate);
            if (templateErrors.Count > 0)
            {
                plan.ErrorMessage = string.Join(" ", templateErrors);
                return plan;
            }

            segmentService = new MkvSplitSegmentService();
            try
            {
                if (phaseCallback != null) { phaseCallback(AppText.F("split.plan.building", Path.GetFileName(plan.InputFile))); }
                segmentService.NormalizeShortcuts(args, plan.Duration, plan.FrameCount);
                built = segmentService.Build(args, plan.Chapters, plan.SourcePts, plan.Duration);
            }
            catch (Exception ex)
            {
                plan.Warnings.AddRange(segmentService.Warnings);
                plan.ErrorMessage = ex.Message;
                return plan;
            }

            plan.Segments = built.segments;
            plan.Mode = built.mode;

            // Lo snap decide i confini definitivi, quindi precede il naming: {start} deve essere l'inizio reale
            segmentService.ApplySnap(plan.Segments, analysis.KeyFlags, plan.SourcePts, args.Snap);

            // I capitoli seguono i confini effettivi: lo snap puo' averne spostati dentro o fuori. Quelli fra il confine richiesto
            // e quello effettivo di un qualsiasi segmento seguono il confine
            List<MkvSplitClip> clips = plan.Segments.ConvertAll(seg => seg.ToClip());
            for (int i = 0; i < plan.Segments.Count; i++)
                plan.Segments[i].Chapters = analysis.SelectChapters(clips[i], plan.Segments[i].StartTs, clips);

            segmentService.ApplyNaming(plan.Segments, args, plan.Mode, plan.InputFile);
            plan.Warnings.AddRange(segmentService.Warnings);

            // Percorso, GOP aperto, codec, frame rate e ricodifica li decide il piano eseguibile (MkvSplitDocumentService.BuildExecutionPlan):
            // qui restano i confini, i nomi e il costo annunciato
            MkvSplitVideoProfile profile = analysis.VideoProfile;
            this.AnnotateCost(plan, analysis.KeyFlags, profile.ExactCutCodec?.CreateCutter(), profile.IsExactCutAvailable);
            this.AnnotateCoverage(plan);
            this.AnnotateOutputs(plan, args.Force);

            plan.IsValid = string.IsNullOrEmpty(plan.ErrorMessage) && plan.Segments.Count > 0;
            if (!plan.IsValid && string.IsNullOrEmpty(plan.ErrorMessage))
            {
                plan.ErrorMessage = AppText.T(plan.Mode == MkvSplitMode.Manual ? "split.plan.manualUndefined" : "split.plan.noSegments");
            }

            return plan;
        }

        /// <summary>
        /// Scrive il piano nel log con lo stesso dettaglio che la UI mostra a video
        /// </summary>
        /// <param name="plan">Piano da stampare</param>
        public void PrintPlan(MkvSplitPlan plan)
        {
            double duration;
            int min;
            double secRem;

            ConsoleHelper.Write(LogSection.Split, LogLevel.Info, AppText.T("split.segments"));
            foreach (MkvSplitSegment seg in plan.Segments)
            {
                // Arrotonda al centesimo prima di separare i minuti: 119.996 diventa 2:00.00 e non 1:60.00
                duration = Math.Round(seg.EndTs - seg.StartTs, 2, MidpointRounding.AwayFromZero);
                min = (int)(duration / 60.0);
                secRem = Math.Round(duration - min * 60.0, 2, MidpointRounding.AwayFromZero);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.segmentLine", PadRight(seg.File, 40), Utils.SecsToTs(seg.StartTs), Utils.SecsToTs(seg.EndTs), min.ToString(CultureInfo.InvariantCulture), secRem.ToString("00.00", CultureInfo.InvariantCulture), seg.Chapters.Count, seg.FrameCount));
            }

            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.plan.coverage",
                AppText.T(plan.Coverage == MkvSplitCoverage.Partition ? "split.plan.coveragePartition" : "split.plan.coverageExtract"),
                plan.DiscardedFrames));
            if (plan.TotalReencodeFrames > 0)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.F("split.plan.reencodeTotal", plan.TotalReencodeFrames));
            }
            foreach (MkvSplitWarning warning in plan.Warnings)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, warning.Message);
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Annota per ogni segmento se apre su keyframe e quanti frame andrebbero ricodificati
        /// </summary>
        /// <param name="plan">Piano da annotare</param>
        /// <param name="keyFlags">Flag keyframe per packet</param>
        /// <param name="cutter">Strategia del taglio al fotogramma del codec, che dà la testa da ricodificare; null se il codec non ne ha,
        /// e il costo annunciato segue allora la regola del taglio a byte</param>
        /// <param name="exactCut">True se il video si taglia al fotogramma: altrimenti il motivo del «solo keyframe» lo dà il piano eseguibile e il keyframe mancante non conta</param>
        private void AnnotateCost(MkvSplitPlan plan, List<MkvSplitFrameInfo> keyFlags, MkvSplitExactCutterBase cutter, bool exactCut)
        {
            int headCount;
            MkvSplitKeyframeIndex keyframes = new MkvSplitKeyframeIndex(keyFlags);

            plan.TotalReencodeFrames = 0;
            foreach (MkvSplitSegment seg in plan.Segments)
            {
                if (seg.StartFrame < 0 || seg.StartFrame >= keyFlags.Count)
                {
                    continue;
                }

                seg.StartsOnKeyframe = keyFlags[seg.StartFrame].Key;
                if (seg.StartsOnKeyframe)
                {
                    seg.ReencodeFrames = 0;
                    continue;
                }

                // La testa va ricodificata fino al primo keyframe successivo, con la regola dell'esecuzione
                headCount = cutter != null ? cutter.HeadReencodeCount(keyframes, seg.StartFrame, seg.FrameCount)
                    : MkvSplitByteExactCutter.LookaheadHeadReencodeCount(keyframes, seg.StartFrame, seg.FrameCount);
                if (headCount < 0)
                {
                    if (exactCut)
                        plan.ErrorMessage = AppText.F("split.exec.noKeyframeInEpisode", seg.StartFrame);
                    continue;
                }

                seg.ReencodeFrames = headCount;
                plan.TotalReencodeFrames += seg.ReencodeFrames;
                plan.Warnings.Add(new MkvSplitWarning(MkvSplitWarningKind.Reencode, AppText.F("split.plan.reencodeSegment", seg.Num, seg.ReencodeFrames)));
            }
        }

        /// <summary>
        /// Determina se il piano ripartisce il sorgente o ne estrae solo alcuni tratti
        /// </summary>
        /// <param name="plan">Piano da annotare</param>
        private void AnnotateCoverage(MkvSplitPlan plan)
        {
            bool[] covered;
            int discarded = 0;
            int end;

            covered = new bool[plan.FrameCount];
            foreach (MkvSplitSegment seg in plan.Segments)
            {
                end = Math.Min(plan.FrameCount, seg.StartFrame + seg.FrameCount);
                for (int i = Math.Max(0, seg.StartFrame); i < end; i++)
                {
                    covered[i] = true;
                }
            }

            for (int i = 0; i < covered.Length; i++)
            {
                if (!covered[i]) { discarded++; }
            }

            plan.DiscardedFrames = discarded;
            plan.Coverage = discarded == 0 ? MkvSplitCoverage.Partition : MkvSplitCoverage.Extract;
        }

        /// <summary>
        /// Verifica collisioni fra i nomi generati e stato dei file di output su disco
        /// </summary>
        /// <param name="plan">Piano da annotare</param>
        /// <param name="force">True se gli output esistenti verranno sovrascritti</param>
        private void AnnotateOutputs(MkvSplitPlan plan, bool force)
        {
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string outPath;
            int previousNum;

            foreach (MkvSplitSegment seg in plan.Segments)
            {
                outPath = Path.GetFullPath(Path.Combine(plan.OutputDir, seg.File));

                // Un segmento che scrive sull'input distruggerebbe la sorgente a metà batch
                if (FileHelper.IsSamePath(outPath, plan.InputFile))
                {
                    plan.ErrorMessage = AppText.F("split.segmentWouldOverwriteInput", seg.Num, seg.File);
                    continue;
                }

                if (seen.TryGetValue(outPath, out previousNum))
                {
                    plan.ErrorMessage = AppText.F("split.plan.nameCollision", previousNum, seg.Num, seg.File);
                    plan.Warnings.Add(new MkvSplitWarning(MkvSplitWarningKind.NameCollision, plan.ErrorMessage));
                    continue;
                }
                seen[outPath] = seg.Num;

                if (File.Exists(outPath))
                {
                    seg.OutputState = force ? MkvSplitOutputState.ExistsOverwrite : MkvSplitOutputState.ExistsSkip;
                    plan.Warnings.Add(new MkvSplitWarning(MkvSplitWarningKind.OutputExists, AppText.F(force ? "split.plan.outputOverwrite" : "split.plan.outputSkip", seg.File)));
                }
                else
                {
                    seg.OutputState = MkvSplitOutputState.New;
                }
            }
        }

        /// <summary>
        /// Pad destra
        /// </summary>
        /// <param name="text">Testo da allineare</param>
        /// <param name="width">Larghezza minima</param>
        /// <returns>Testo allineato a sinistra</returns>
        private static string PadRight(string text, int width)
        {
            if (text == null) { return new string(' ', width); }
            if (text.Length >= width) { return text; }
            return text + new string(' ', width - text.Length);
        }

        #endregion
    }
}
