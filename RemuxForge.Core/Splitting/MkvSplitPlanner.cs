using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
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
            return this.BuildPlan(args, inputFile, phaseCallback, null);
        }

        /// <summary>
        /// Costruisce il piano di taglio per un singolo file, con i segmenti dell'editor al posto di quelli della configurazione
        /// </summary>
        /// <param name="args">Opzioni split già clonate per il file</param>
        /// <param name="inputFile">File sorgente</param>
        /// <param name="phaseCallback">Callback opzionale invocata a ogni fase dell'analisi</param>
        /// <param name="overrideSegments">Segmenti costruiti nell'editor, null quando comanda la configurazione</param>
        /// <returns>Piano del file, valido oppure con il motivo dell'invalidità</returns>
        public MkvSplitPlan BuildPlan(MkvSplitOptions args, string inputFile, Action<string> phaseCallback, List<MkvSplitOverrideSegment> overrideSegments)
        {
            MkvSplitPlan plan = new MkvSplitPlan();
            MkvSplitAnalysis analysis;
            MkvSplitSegmentService segmentService;
            (List<MkvSplitSegment> segments, MkvSplitMode mode) built;
            List<int> keyframeIndexes;

            plan.InputFile = Path.GetFullPath(inputFile);
            plan.Snap = args.Snap;
            plan.OutputDir = !string.IsNullOrEmpty(args.OutputDir) ? Path.GetFullPath(args.OutputDir) : Path.GetDirectoryName(plan.InputFile);

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

            // I keyframe servono all'editor per disegnare la corsia e per agganciarci i confini
            keyframeIndexes = new List<int>();
            for (int i = 0; i < analysis.KeyFlags.Count; i++)
            {
                if (analysis.KeyFlags[i].Key) { keyframeIndexes.Add(i); }
            }
            plan.KeyframeIndexes = keyframeIndexes.ToArray();

            segmentService = new MkvSplitSegmentService();
            if (overrideSegments != null)
            {
                // I confini li ha messi l'utente sulla timeline: lo snap non li tocca, e il costo
                // della ricodifica lo dichiara il piano
                plan.Segments = BuildOverrideSegments(overrideSegments, plan);
                plan.Mode = MkvSplitMode.Manual;
                plan.IsOverride = true;
            }
            else
            {
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
            }

            segmentService.ApplyNaming(plan.Segments, args, plan.Mode, plan.InputFile);
            plan.Warnings.AddRange(segmentService.Warnings);

            this.AnnotateCost(plan, analysis.KeyFlags);
            this.AnnotateCoverage(plan);
            this.AnnotateOutputs(plan, args.Force);

            plan.UsesFastPath = args.Snap != MkvSplitSnapMode.Off
                && plan.FrameRateMode != MkvSplitFrameRateMode.Unknown
                && EndsAreCuttable(plan, analysis.KeyFlags);
            if (plan.UsesFastPath)
            {
                plan.TotalReencodeFrames = 0;
            }

            // Il fast path e' indipendente dal codec perche' mkvmerge rimuxa qualunque cosa; lo slow
            // path lavora sul bitstream e ne conosce solo tre, quindi il piano lo dice adesso invece
            // di lasciare che l'eccezione arrivi a esecuzione gia' partita
            if (!plan.UsesFastPath
                && string.IsNullOrEmpty(plan.ErrorMessage)
                && !MkvSplitPipeline.TryParseCodec(plan.VideoParams.CodecName, out MkvSplitCodec _))
            {
                plan.ErrorMessage = AppText.F("split.unsupportedVideoCodec", plan.VideoParams.CodecName);
            }

            plan.IsValid = string.IsNullOrEmpty(plan.ErrorMessage) && plan.Segments.Count > 0;
            if (!plan.IsValid && string.IsNullOrEmpty(plan.ErrorMessage))
            {
                plan.ErrorMessage = AppText.T(plan.Mode == MkvSplitMode.Manual ? "split.plan.manualUndefined" : "split.plan.noSegments");
            }

            return plan;
        }

        /// <summary>
        /// Traduce i segmenti dell'editor in segmenti di piano, ricalcolando tempi e capitoli dai frame
        /// </summary>
        /// <param name="overrideSegments">Segmenti costruiti nell'editor</param>
        /// <param name="plan">Piano in costruzione, già completo di PTS, durata e capitoli</param>
        /// <returns>Segmenti ordinati e numerati, senza quelli esclusi</returns>
        private static List<MkvSplitSegment> BuildOverrideSegments(List<MkvSplitOverrideSegment> overrideSegments, MkvSplitPlan plan)
        {
            List<MkvSplitSegment> segments = new List<MkvSplitSegment>();
            List<MkvSplitOverrideSegment> ordered = new List<MkvSplitOverrideSegment>(overrideSegments);
            MkvSplitSegment seg;
            int startFrame;
            int frameCount;
            int num = 0;

            ordered.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
            foreach (MkvSplitOverrideSegment source in ordered)
            {
                if (source.Excluded) { continue; }

                startFrame = Math.Max(0, Math.Min(plan.FrameCount - 1, source.StartFrame));
                frameCount = Math.Max(1, Math.Min(plan.FrameCount - startFrame, source.FrameCount));
                num++;

                seg = new MkvSplitSegment();
                seg.Num = num;
                seg.Episode = num;
                seg.StartFrame = startFrame;
                seg.FrameCount = frameCount;
                seg.StartTs = plan.SourcePts[startFrame];

                // La fine è esclusiva: coincide con il PTS del frame successivo, o con la durata sull'ultimo
                seg.EndTs = (startFrame + frameCount < plan.FrameCount) ? plan.SourcePts[startFrame + frameCount] : plan.Duration;

                foreach (MkvSplitChapter chapter in plan.Chapters)
                {
                    if (chapter.Timestamp >= seg.StartTs && chapter.Timestamp < seg.EndTs) { seg.Chapters.Add(chapter); }
                }
                segments.Add(seg);
            }

            return segments;
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
                duration = seg.EndTs - seg.StartTs;
                min = (int)(duration / 60.0);
                secRem = duration - min * 60.0;
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.segmentLine", PadRight(seg.File, 40), MkvSplitSegmentService.SecsToTs(seg.StartTs), MkvSplitSegmentService.SecsToTs(seg.EndTs), min.ToString(System.Globalization.CultureInfo.InvariantCulture), secRem.ToString("00.00", System.Globalization.CultureInfo.InvariantCulture), seg.Chapters.Count, seg.FrameCount));
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
        /// Verifica che ogni segmento finisca su un keyframe o sulla fine del file. Il fast path taglia
        /// con mkvmerge, che può chiudere una parte solo su un keyframe: se la fine non ci cade mkvmerge
        /// allunga il segmento fino al successivo, e il file prodotto non sarebbe quello annunciato dal
        /// piano. In quel caso si passa dallo slow path, che la fine la rispetta al fotogramma.
        /// </summary>
        /// <param name="plan">Piano da controllare</param>
        /// <param name="keyFlags">Flag keyframe per fotogramma, in ordine di presentazione</param>
        /// <returns>True se il fast path può produrre esattamente i segmenti del piano</returns>
        private static bool EndsAreCuttable(MkvSplitPlan plan, List<MkvSplitFrameInfo> keyFlags)
        {
            int end;

            foreach (MkvSplitSegment seg in plan.Segments)
            {
                end = seg.StartFrame + seg.FrameCount;
                if (end >= keyFlags.Count)
                {
                    continue;
                }

                if (!keyFlags[end].Key)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Annota per ogni segmento se apre su keyframe e quanti frame andrebbero ricodificati
        /// </summary>
        /// <param name="plan">Piano da annotare</param>
        /// <param name="keyFlags">Flag keyframe per packet</param>
        private void AnnotateCost(MkvSplitPlan plan, List<MkvSplitFrameInfo> keyFlags)
        {
            int lookAhead;
            int keyAfter;

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

                // La testa va ricodificata fino al primo keyframe successivo, con la stessa finestra dell'esecutore
                keyAfter = -1;
                lookAhead = Math.Min(seg.StartFrame + MkvSplitExecutor.KEYFRAME_LOOKAHEAD, keyFlags.Count);
                for (int i = seg.StartFrame + 1; i < lookAhead; i++)
                {
                    if (keyFlags[i].Key) { keyAfter = i; break; }
                }

                if (keyAfter < 0 || keyAfter > seg.StartFrame + seg.FrameCount - 1)
                {
                    plan.ErrorMessage = AppText.F("split.exec.noKeyframeInEpisode", seg.StartFrame);
                    continue;
                }

                seg.ReencodeFrames = keyAfter - seg.StartFrame;
                plan.TotalReencodeFrames += seg.ReencodeFrames;
                plan.Warnings.Add(new MkvSplitWarning(MkvSplitWarningKind.Reencode, AppText.F("split.plan.reencodeSegment", seg.Num, seg.ReencodeFrames), seg.Num));
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
                if (string.Equals(outPath, plan.InputFile, StringComparison.OrdinalIgnoreCase))
                {
                    plan.ErrorMessage = AppText.F("split.segmentWouldOverwriteInput", seg.Num, seg.File);
                    continue;
                }

                if (seen.TryGetValue(outPath, out previousNum))
                {
                    plan.ErrorMessage = AppText.F("split.plan.nameCollision", previousNum, seg.Num, seg.File);
                    plan.Warnings.Add(new MkvSplitWarning(MkvSplitWarningKind.NameCollision, plan.ErrorMessage, seg.Num));
                    continue;
                }
                seen[outPath] = seg.Num;

                if (File.Exists(outPath))
                {
                    seg.OutputState = force ? MkvSplitOutputState.ExistsOverwrite : MkvSplitOutputState.ExistsSkip;
                    plan.Warnings.Add(new MkvSplitWarning(MkvSplitWarningKind.OutputExists, AppText.F(force ? "split.plan.outputOverwrite" : "split.plan.outputSkip", seg.File), seg.Num));
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
