using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Orchestrazione completa della modalità split
    /// </summary>
    public class MkvSplitPipeline
    {
        #region Metodi pubblici

        /// <summary>
        /// Esegue split single file o batch in base a --source
        /// </summary>
        /// <param name="options">Opzioni globali</param>
        /// <returns>Exit code 0/1</returns>
        public int Execute(Options options)
        {
            int result = 0;
            List<string> files;
            MkvSplitExecutionResult fileResult;

            MkvSplitExternalTools.Instance.ResolveBinaries();
            files = this.ResolveInputFiles(options);
            if (files.Count == 0)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.T("split.noMkvFiles"));
                return 1;
            }

            for (int i = 0; i < files.Count; i++)
            {
                fileResult = this.ExecuteFileInternal(options, files[i], files.Count > 1);
                if (fileResult.ExitCode != 0)
                {
                    result = 1;
                    if (files.Count <= 1)
                    {
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Esegue la pipeline split su un singolo file già risolto
        /// </summary>
        /// <param name="options">Opzioni globali</param>
        /// <param name="inputFile">File da elaborare</param>
        /// <param name="batch">True se parte di un batch</param>
        /// <returns>Risultato per-file</returns>
        public MkvSplitExecutionResult ExecuteFile(Options options, string inputFile, bool batch)
        {
            MkvSplitExternalTools.Instance.ResolveBinaries();
            return this.ExecuteFileInternal(options, inputFile, batch);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Esegue la pipeline split su un singolo file già risolto senza rieseguire setup tool
        /// </summary>
        private MkvSplitExecutionResult ExecuteFileInternal(Options options, string inputFile, bool batch)
        {
            MkvSplitExecutionResult result = new MkvSplitExecutionResult();
            MkvSplitOptions splitOptions;
            List<MkvSplitSegment> segments;

            result.InputFile = inputFile;
            try
            {
                splitOptions = CloneSplitOptions(options.Split);
                splitOptions.InputFile = inputFile;
                splitOptions.Batch = batch;
                result.ExitCode = this.ExecuteSingle(splitOptions, out segments);
                result.Segments = segments;
                if (result.ExitCode != 0 && string.IsNullOrEmpty(result.ErrorMessage))
                {
                    result.ErrorMessage = AppText.T("split.error.generic");
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = 1;
                result.ErrorMessage = ex.Message;
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("cli.splitError", ex.Message));
            }

            return result;
        }

        /// <summary>
        /// Risolve file input da source file/cartella
        /// </summary>
        private List<string> ResolveInputFiles(Options options)
        {
            List<string> files = new List<string>();
            string source = options.Split.SourcePath;
            SearchOption searchOption = options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            if (File.Exists(source))
            {
                files.Add(Path.GetFullPath(source));
            }
            else if (Directory.Exists(source))
            {
                for (int i = 0; i < options.FileExtensions.Count; i++)
                {
                    foreach (string file in Directory.GetFiles(source, "*." + options.FileExtensions[i].TrimStart('.'), searchOption))
                    {
                        files.Add(Path.GetFullPath(file));
                    }
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                throw new FileNotFoundException(AppText.F("validation.splitSourceNotFound", source), source);
            }

            return files;
        }

        /// <summary>
        /// Esegue la pipeline completa su un singolo file
        /// </summary>
        private int ExecuteSingle(MkvSplitOptions args, out List<MkvSplitSegment> resultSegments)
        {
            string inputFile;
            string sourceRaw;
            string outputDir;
            List<MkvSplitChapter> chapters;
            double[] sourcePts;
            int inputFrames;
            double duration;
            MkvSplitSegmentService segmentService;
            MkvSplitExecutor splitter;
            (List<MkvSplitSegment> segments, MkvSplitMode mode) built;
            List<MkvSplitSegment> segments;
            MkvSplitMode mode;
            MkvSplitVideoParams vp;
            MkvSplitCodec codec;
            List<string> headArgs;
            string absInput;
            bool canFastPath;
            bool sameSource;
            MkvSplitFrameRateMode frameRateMode = MkvSplitFrameRateMode.Unknown;

            resultSegments = new List<MkvSplitSegment>();
            inputFile = Path.GetFullPath(args.InputFile);
            if (!File.Exists(inputFile))
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.fileNotFound", inputFile));
                return 1;
            }

            sourceRaw = !string.IsNullOrEmpty(args.SourceRaw) ? Path.GetFullPath(args.SourceRaw) : inputFile;
            if (!File.Exists(sourceRaw))
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.fileNotFound", sourceRaw));
                return 1;
            }

            outputDir = !string.IsNullOrEmpty(args.OutputDir) ? args.OutputDir : Path.GetDirectoryName(inputFile);
            Directory.CreateDirectory(outputDir);

            ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.F("split.input", inputFile));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.sourceRaw", sourceRaw));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.output", outputDir));

            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.T("split.extractingChapters"));
            chapters = MkvSplitExternalTools.Instance.GetChapters(inputFile);
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.foundChapters", chapters.Count));

            sourcePts = MkvSplitExternalTools.Instance.ExtractSourcePts(sourceRaw);
            inputFrames = MkvSplitExternalTools.Instance.CountPackets(inputFile);
            if (inputFrames != sourcePts.Length)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.frameCountMismatch", sourcePts.Length, inputFrames));
                return 1;
            }

            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.frameCount", inputFrames));
            duration = MkvSplitExternalTools.Instance.GetDuration(inputFile);

            segmentService = new MkvSplitSegmentService();
            segmentService.NormalizeShortcuts(args, duration, sourcePts.Length);
            built = segmentService.Build(args, chapters, sourcePts, duration);
            segments = built.segments;
            mode = built.mode;
            segmentService.ApplyNaming(segments, args, mode, inputFile);
            resultSegments = segments;

            vp = MkvSplitExternalTools.Instance.GetVideoParams(inputFile);
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.videoParams", vp.CodecName, vp.PixFmt, vp.ColorSpace, vp.ColorPrimaries, vp.ColorTransfer, vp.ColorRange));

            absInput = Path.GetFullPath(inputFile);
            foreach (MkvSplitSegment seg in segments)
            {
                string outPath = Path.GetFullPath(Path.Combine(outputDir, seg.File));
                if (string.Equals(outPath, absInput, StringComparison.OrdinalIgnoreCase))
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.segmentWouldOverwriteInput", seg.Num, seg.File));
                    return 1;
                }
            }

            this.PrintSegments(segments);
            if (args.DryRun)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.T("split.dryRun"));
                return 0;
            }

            canFastPath = false;
            sameSource = string.Equals(sourceRaw, inputFile, StringComparison.OrdinalIgnoreCase);
            if (args.Snap != MkvSplitSnapMode.Off && sameSource)
            {
                frameRateMode = MkvSplitExternalTools.Instance.DetectFrameRateMode(inputFile);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.frameRateMode", frameRateMode));
                if (frameRateMode == MkvSplitFrameRateMode.Unknown)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.T("split.frameRateUnknown"));
                    return 1;
                }
                canFastPath = true;
            }
            else if (args.Snap != MkvSplitSnapMode.Off && !sameSource)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.T("split.fastPathDisabledSourceRaw"));
            }

            splitter = new MkvSplitExecutor();
            if (canFastPath)
            {
                return this.RunFastPath(args, segments, inputFile, sourcePts, outputDir, absInput, splitter, frameRateMode == MkvSplitFrameRateMode.Vfr);
            }

            codec = ParseCodec(vp.CodecName);
            headArgs = BuildHeadEncodeArgs(vp, codec);
            return this.RunSlowPath(args, segments, inputFile, sourcePts, outputDir, absInput, splitter, headArgs, codec);
        }

        /// <summary>
        /// Esegue fast path
        /// </summary>
        private int RunFastPath(MkvSplitOptions args, List<MkvSplitSegment> segments, string inputFile, double[] sourcePts, string outputDir, string absInput, MkvSplitExecutor splitter, bool isVfr)
        {
            List<MkvSplitFrameInfo> keyFlags;
            MkvSplitSegmentService segmentService;
            bool hasFlac;

            hasFlac = MkvSplitExternalTools.Instance.HasFlacAudio(inputFile);
            ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.F("split.fastPath", isVfr ? "VFR" : "CFR", hasFlac ? " + mkvmerge video / ffmpeg AV (FLAC)" : " + mkvmerge"));

            keyFlags = MkvSplitExternalTools.Instance.GetKeyFlags(inputFile);
            segmentService = new MkvSplitSegmentService();
            segmentService.ApplySnap(segments, keyFlags, sourcePts, args.Snap);

            foreach (MkvSplitSegment seg in segments)
            {
                if (!this.ProcessSegment(seg, outputDir, absInput, args.Force, tmp => splitter.SplitFast(seg, inputFile, Path.Combine(outputDir, seg.File), tmp, hasFlac)))
                {
                    return 1;
                }
            }

            ConsoleHelper.Write(LogSection.Split, LogLevel.Success, AppText.F("split.doneFastPath", segments.Count, outputDir));
            return 0;
        }

        /// <summary>
        /// Esegue slow path
        /// </summary>
        private int RunSlowPath(MkvSplitOptions args, List<MkvSplitSegment> segments, string inputFile, double[] sourcePts, string outputDir, string absInput, MkvSplitExecutor splitter, List<string> headArgs, MkvSplitCodec codec)
        {
            string rawExt = codec == MkvSplitCodec.Hevc ? "h265" : "h264";
            string rawFile = Path.Combine(outputDir, "_raw_temp." + rawExt);
            List<MkvSplitFrameInfo> frameMap;
            MkvSplitSegmentService segmentService;

            ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.T("split.slowPath"));
            try
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.T("split.extractingRawVideo"));
                MkvSplitExternalTools.Instance.ExtractRawTrack(inputFile, 0, rawFile);
                frameMap = MkvSplitExternalTools.Instance.GetFrameByteMap(rawFile);
                if (frameMap.Count != sourcePts.Length)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.frameMapMismatch", frameMap.Count, sourcePts.Length));
                    return 1;
                }

                segmentService = new MkvSplitSegmentService();
                segmentService.ApplySnap(segments, frameMap, sourcePts, args.Snap);

                foreach (MkvSplitSegment seg in segments)
                {
                    if (!this.ProcessSegment(seg, outputDir, absInput, args.Force, tmp => splitter.SplitSlow(seg, inputFile, rawFile, frameMap, sourcePts, headArgs, codec, Path.Combine(outputDir, seg.File), tmp)))
                    {
                        return 1;
                    }
                }
            }
            finally
            {
                if (File.Exists(rawFile))
                {
                    try { File.Delete(rawFile); } catch (IOException) { }
                }
            }

            ConsoleHelper.Write(LogSection.Split, LogLevel.Success, AppText.F("split.done", segments.Count, outputDir));
            return 0;
        }

        /// <summary>
        /// Processa un segmento con temp dedicata
        /// </summary>
        private bool ProcessSegment(MkvSplitSegment seg, string outputDir, string absInput, bool force, Action<string> splitAction)
        {
            string outPath;
            string tmp;
            double sizeMb;

            ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.F("split.segment", seg.Num, seg.File));
            outPath = Path.Combine(outputDir, seg.File);
            if (File.Exists(outPath) && !force)
            {
                sizeMb = new FileInfo(outPath).Length / 1048576.0;
                ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.F("split.skipExists", seg.File, sizeMb.ToString("F1", CultureInfo.InvariantCulture)));
                return true;
            }

            tmp = Path.Combine(outputDir, "_tmp_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmp);
            try
            {
                splitAction(tmp);
                return true;
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.errorLine", ex.Message));
                if (File.Exists(outPath) && !string.Equals(Path.GetFullPath(outPath), absInput, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(outPath); } catch (IOException) { }
                }
                return false;
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch (IOException) { }
            }
        }

        /// <summary>
        /// Stampa segmenti
        /// </summary>
        private void PrintSegments(List<MkvSplitSegment> segments)
        {
            ConsoleHelper.Write(LogSection.Split, LogLevel.Info, AppText.T("split.segments"));
            foreach (MkvSplitSegment seg in segments)
            {
                double d = seg.EndTs - seg.StartTs;
                int min = (int)(d / 60.0);
                double secRem = d - min * 60.0;
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.segmentLine", PadRight(seg.File, 40), MkvSplitSegmentService.SecsToTs(seg.StartTs), MkvSplitSegmentService.SecsToTs(seg.EndTs), min.ToString(CultureInfo.InvariantCulture), secRem.ToString("00.00", CultureInfo.InvariantCulture), seg.Chapters.Count, seg.FrameCount));
            }
        }

        /// <summary>
        /// Clona opzioni split per batch
        /// </summary>
        private static MkvSplitOptions CloneSplitOptions(MkvSplitOptions source)
        {
            MkvSplitOptions result = new MkvSplitOptions();
            result.SourcePath = source.SourcePath;
            result.SourceRaw = source.SourceRaw;
            result.OutputDir = source.OutputDir;
            result.Pattern = source.Pattern;
            result.Ranges = source.Ranges;
            result.SplitAt = source.SplitAt;
            result.TrimStart = source.TrimStart;
            result.TrimEnd = source.TrimEnd;
            result.ChaptersEach = source.ChaptersEach;
            result.OutputTemplate = source.OutputTemplate;
            result.Snap = source.Snap;
            result.Force = source.Force;
            result.Log = source.Log;
            result.DryRun = source.DryRun;
            return result;
        }

        /// <summary>
        /// Parsa codec video supportati
        /// </summary>
        private static MkvSplitCodec ParseCodec(string codecName)
        {
            string codec = codecName ?? "hevc";
            if (codec == "h264") { return MkvSplitCodec.H264; }
            if (codec == "hevc") { return MkvSplitCodec.Hevc; }
            throw new ArgumentException(AppText.F("split.unsupportedVideoCodec", codecName));
        }

        /// <summary>
        /// Costruisce argomenti encode head
        /// </summary>
        private static List<string> BuildHeadEncodeArgs(MkvSplitVideoParams p, MkvSplitCodec codec)
        {
            string encoder;
            string paramsFlag;
            string defaultPixFmt;
            string pixFmt;
            List<string> args = new List<string>();
            List<string> encParams = new List<string>();

            if (codec == MkvSplitCodec.H264)
            {
                encoder = "libx264";
                paramsFlag = "-x264-params";
                defaultPixFmt = "yuv420p";
            }
            else
            {
                encoder = "libx265";
                paramsFlag = "-x265-params";
                defaultPixFmt = "yuv420p10le";
            }

            pixFmt = p.PixFmt != null ? p.PixFmt : defaultPixFmt;
            args.Add("-c:v"); args.Add(encoder);
            args.Add("-crf"); args.Add("14");
            args.Add("-preset"); args.Add("medium");
            args.Add("-pix_fmt"); args.Add(pixFmt);
            encParams.Add("keyint=1");
            encParams.Add("bframes=0");

            if (p.ColorSpace != null)
            {
                args.Add("-colorspace"); args.Add(p.ColorSpace);
                encParams.Add("colormatrix=" + p.ColorSpace);
            }
            if (p.ColorPrimaries != null)
            {
                args.Add("-color_primaries"); args.Add(p.ColorPrimaries);
                encParams.Add("colorprim=" + p.ColorPrimaries);
            }
            if (p.ColorTransfer != null)
            {
                args.Add("-color_trc"); args.Add(p.ColorTransfer);
                encParams.Add("transfer=" + p.ColorTransfer);
            }
            if (p.ColorRange != null)
            {
                args.Add("-color_range"); args.Add(p.ColorRange);
                encParams.Add("range=" + (p.ColorRange == "pc" ? "full" : "limited"));
            }

            args.Add(paramsFlag);
            args.Add(string.Join(":", encParams));
            return args;
        }

        /// <summary>
        /// Pad destra
        /// </summary>
        private static string PadRight(string text, int width)
        {
            if (text == null) { return new string(' ', width); }
            if (text.Length >= width) { return text; }
            return text + new string(' ', width - text.Length);
        }

        #endregion
    }
}
