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

        #endregion

        #region Metodi privati

        /// <summary>
        /// Esegue la pipeline split su un singolo file già risolto senza rieseguire setup tool
        /// </summary>
        private MkvSplitExecutionResult ExecuteFileInternal(Options options, string inputFile, bool batch)
        {
            MkvSplitExecutionResult result = new MkvSplitExecutionResult();
            MkvSplitOptions splitOptions;
            MkvSplitPlan plan;

            result.InputFile = inputFile;
            try
            {
                splitOptions = CloneSplitOptions(options.Split);
                splitOptions.InputFile = inputFile;
                splitOptions.Batch = batch;
                plan = new MkvSplitPlanner().BuildPlan(splitOptions, inputFile, null);
                result.Segments = plan.Segments;
                result.ExitCode = this.ExecutePlan(plan, splitOptions);
                if (result.ExitCode != 0 && !plan.IsValid)
                {
                    result.ErrorMessage = plan.ErrorMessage;
                }
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
        /// Esegue un piano già costruito, senza ricalcolare segmenti, snap o nomi
        /// </summary>
        /// <param name="plan">Piano da eseguire</param>
        /// <param name="args">Opzioni split del file</param>
        /// <returns>Exit code 0/1</returns>
        public int ExecutePlan(MkvSplitPlan plan, MkvSplitOptions args)
        {
            MkvSplitExecutor splitter;
            MkvSplitCodec codec;
            List<string> headArgs;
            MkvSplitPlanner planner = new MkvSplitPlanner();

            ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.F("split.input", plan.InputFile));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.output", plan.OutputDir));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.foundChapters", plan.Chapters.Count));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.frameCount", plan.FrameCount));

            planner.PrintPlan(plan);

            if (!plan.IsValid)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.plan.invalid", plan.ErrorMessage));
                return 1;
            }

            if (args.DryRun)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.T("split.dryRun"));
                return 0;
            }

            Directory.CreateDirectory(plan.OutputDir);
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.videoParams", plan.VideoParams.CodecName, plan.VideoParams.PixFmt, plan.VideoParams.ColorSpace, plan.VideoParams.ColorPrimaries, plan.VideoParams.ColorTransfer, plan.VideoParams.ColorRange));

            splitter = new MkvSplitExecutor();
            if (plan.UsesFastPath)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.frameRateMode", plan.FrameRateMode));
                return this.RunFastPath(args, plan, splitter);
            }

            if (args.Snap != MkvSplitSnapMode.Off && plan.FrameRateMode == MkvSplitFrameRateMode.Unknown)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.T("split.frameRateUnknown"));
                return 1;
            }

            if (!TryParseCodec(plan.VideoParams.CodecName, out codec))
            {
                throw new ArgumentException(AppText.F("split.unsupportedVideoCodec", plan.VideoParams.CodecName));
            }

            headArgs = BuildHeadEncodeArgs(plan.VideoParams, codec);
            return this.RunSlowPath(args, plan, splitter, headArgs, codec);
        }

        /// <summary>
        /// Esegue fast path
        /// </summary>
        private int RunFastPath(MkvSplitOptions args, MkvSplitPlan plan, MkvSplitExecutor splitter)
        {
            bool hasFlac;

            hasFlac = MkvSplitExternalTools.Instance.HasFlacAudio(plan.InputFile);
            ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.F("split.fastPath", plan.FrameRateMode == MkvSplitFrameRateMode.Vfr ? "VFR" : "CFR", hasFlac ? " + mkvmerge video / ffmpeg AV (FLAC)" : " + mkvmerge"));

            foreach (MkvSplitSegment seg in plan.Segments)
            {
                if (!this.ProcessSegment(seg, plan.OutputDir, plan.InputFile, args.Force, tmp => splitter.SplitFast(seg, plan.InputFile, Path.Combine(plan.OutputDir, seg.File), tmp, hasFlac)))
                {
                    return 1;
                }
            }

            ConsoleHelper.Write(LogSection.Split, LogLevel.Success, AppText.F("split.doneFastPath", plan.Segments.Count, plan.OutputDir));
            return 0;
        }

        /// <summary>
        /// Esegue slow path
        /// </summary>
        private int RunSlowPath(MkvSplitOptions args, MkvSplitPlan plan, MkvSplitExecutor splitter, List<string> headArgs, MkvSplitCodec codec)
        {
            string rawExt = RawExtension(codec);
            string rawFile = Path.Combine(plan.OutputDir, "_raw_temp." + rawExt);
            List<MkvSplitFrameInfo> frameMap;
            int[] presentationToDecode;

            ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.T("split.slowPath"));
            try
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.T("split.extractingRawVideo"));
                MkvSplitExternalTools.Instance.ExtractRawTrack(plan.InputFile, 0, rawFile);
                frameMap = MkvSplitExternalTools.Instance.GetFrameByteMap(rawFile);
                if (frameMap.Count != plan.SourcePts.Length)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.frameMapMismatch", frameMap.Count, plan.SourcePts.Length));
                    return 1;
                }

                presentationToDecode = BuildPresentationToDecodeMap(plan.InputFile, frameMap.Count);
                if (presentationToDecode == null)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.frameMapMismatch", frameMap.Count, plan.SourcePts.Length));
                    return 1;
                }

                foreach (MkvSplitSegment seg in plan.Segments)
                {
                    if (!this.ProcessSegment(seg, plan.OutputDir, plan.InputFile, args.Force, tmp => splitter.SplitSlow(seg, plan.InputFile, rawFile, frameMap, plan.SourcePts, presentationToDecode, headArgs, codec, Path.Combine(plan.OutputDir, seg.File), tmp)))
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

            ConsoleHelper.Write(LogSection.Split, LogLevel.Success, AppText.F("split.done", plan.Segments.Count, plan.OutputDir));
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
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
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
        /// Clona opzioni split per batch
        /// </summary>
        private static MkvSplitOptions CloneSplitOptions(MkvSplitOptions source)
        {
            MkvSplitOptions result = new MkvSplitOptions();
            result.SourcePath = source.SourcePath;
            result.OutputDir = source.OutputDir;
            result.Pattern = source.Pattern;
            result.Ranges = source.Ranges;
            result.SplitAt = source.SplitAt;
            result.TrimStart = source.TrimStart;
            result.TrimEnd = source.TrimEnd;
            result.ChaptersEach = source.ChaptersEach;
            result.ChaptersPerEpisode = source.ChaptersPerEpisode;
            result.Manual = source.Manual;
            result.OutputTemplate = source.OutputTemplate;
            result.StartNumber = source.StartNumber;
            result.Snap = source.Snap;
            result.Force = source.Force;
            result.DryRun = source.DryRun;
            return result;
        }

        /// <summary>
        /// Parsa i codec video che il taglio esatto sa manipolare a livello di bitstream
        /// </summary>
        /// <param name="codecName">Nome del codec come lo riporta ffprobe</param>
        /// <param name="codec">Codec canonico, valorizzato solo in caso di successo</param>
        /// <returns>true se il codec è supportato dallo slow path</returns>
        public static bool TryParseCodec(string codecName, out MkvSplitCodec codec)
        {
            string name = codecName ?? "hevc";
            codec = MkvSplitCodec.Hevc;
            if (name == "h264") { codec = MkvSplitCodec.H264; return true; }
            if (name == "hevc") { return true; }
            if (name == "mpeg2video") { codec = MkvSplitCodec.Mpeg2; return true; }
            return false;
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
            else if (codec == MkvSplitCodec.Mpeg2)
            {
                encoder = "mpeg2video";
                paramsFlag = null;
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
            if (codec == MkvSplitCodec.Mpeg2)
            {
                // MPEG-2 non ha CRF: la qualità si esprime con il quantizzatore, e l'all-intra
                // si ottiene con i flag dell'encoder invece che con i parametri di x264/x265
                args.Add("-q:v"); args.Add("2");
                args.Add("-g"); args.Add("1");
                args.Add("-bf"); args.Add("0");
            }
            else
            {
                args.Add("-crf"); args.Add("14");
                args.Add("-preset"); args.Add("medium");
                encParams.Add("keyint=1");
                encParams.Add("bframes=0");
            }
            args.Add("-pix_fmt"); args.Add(pixFmt);

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

            if (paramsFlag != null)
            {
                args.Add(paramsFlag);
                args.Add(string.Join(":", encParams));
            }

            return args;
        }

        /// <summary>
        /// Costruisce la corrispondenza fra indice di presentazione e indice nel raw bitstream,
        /// ordinando per PTS i packet letti in ordine di decodifica. Senza B-frame è l'identità.
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <param name="frameCount">Numero di frame atteso, per verificare la coerenza</param>
        /// <returns>Mappa indicizzata per presentazione, oppure null se i conteggi non tornano</returns>
        private static int[] BuildPresentationToDecodeMap(string inputFile, int frameCount)
        {
            List<double> decodePts = MkvSplitExternalTools.Instance.GetDecodeOrderPts(inputFile);
            double[] keys;
            int[] map;

            if (decodePts.Count != frameCount)
            {
                return null;
            }

            keys = decodePts.ToArray();
            map = new int[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                map[i] = i;
            }

            Array.Sort(keys, map);
            return map;
        }

        /// <summary>
        /// Estensione dell'elementary stream raw per il codec indicato
        /// </summary>
        /// <param name="codec">Codec video canonico</param>
        /// <returns>Estensione senza punto</returns>
        private static string RawExtension(MkvSplitCodec codec)
        {
            if (codec == MkvSplitCodec.Hevc) { return "h265"; }
            if (codec == MkvSplitCodec.H264) { return "h264"; }
            return "m2v";
        }

        #endregion
    }
}
