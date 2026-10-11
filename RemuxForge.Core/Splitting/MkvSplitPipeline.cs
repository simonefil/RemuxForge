using RemuxForge.Core.Chapters;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Media;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Orchestrazione completa della modalità split
    /// </summary>
    public class MkvSplitPipeline
    {
        #region Variabili di classe

        /// <summary>
        /// Formati di immagine ammessi dall'encoder h263 (sub-QCIF, QCIF, CIF, 4CIF, 16CIF)
        /// </summary>
        private static readonly string[] s_h263StandardSizes = new string[] { "128x96", "176x144", "352x288", "704x576", "1408x1152" };

        #endregion

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
            files = ResolveInputFiles(options.Split.SourcePath, options.Recursive);
            if (files.Count == 0)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.T("split.noMkvFiles"));
                return 1;
            }
            // Un batch parte solo con lo stesso codec e lo stesso tipo di scansione in tutti i file
            if (files.Count > 1)
            {
                string mismatch = CheckBatchVideo(files, out _, out _);
                if (mismatch.Length > 0)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Error, mismatch);
                    return 1;
                }
            }

            for (int i = 0; i < files.Count; i++)
            {
                fileResult = this.ExecuteFileInternal(options, files[i]);
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
        /// Risolve i file da dividere: il file indicato, oppure i file supportati della cartella in ordine di percorso
        /// </summary>
        /// <param name="source">File o cartella sorgente</param>
        /// <param name="recursive">True per cercare anche nelle sottocartelle</param>
        /// <returns>Percorsi assoluti dei file</returns>
        public static List<string> ResolveInputFiles(string source, bool recursive)
        {
            List<string> files = new List<string>();
            SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            if (File.Exists(source))
            {
                files.Add(Path.GetFullPath(source));
            }
            else if (Directory.Exists(source))
            {
                foreach (string file in Directory.GetFiles(source, "*", searchOption))
                {
                    if (MediaContainerFormats.IsSupported(file))
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
        /// Verifica che i file di un batch abbiano lo stesso codec video e lo stesso tipo di scansione, progressivo o
        /// interlacciato (D14, P4). Legge i file originali con ffprobe e MediaInfo come il wizard per un file singolo, senza normalizzare
        /// </summary>
        /// <param name="files">File del batch</param>
        /// <param name="codecName">Codec del primo file, come lo riporta ffprobe</param>
        /// <param name="scanType">Scansione del primo file, vuota se non dichiarata</param>
        /// <returns>Errore con i file diversi dal primo, vuoto se il batch è omogeneo</returns>
        public static string CheckBatchVideo(List<string> files, out string codecName, out string scanType)
        {
            List<string> different = new List<string>();
            codecName = null;
            scanType = "";

            for (int i = 0; i < files.Count; i++)
            {
                string codec;
                try
                {
                    codec = MkvSplitExternalTools.Instance.GetVideoParams(files[i]).CodecName;
                }
                catch (InvalidOperationException)
                {
                    codec = null;
                }
                string scan = MkvSplitExternalTools.Instance.GetVideoScanType(files[i]);
                if (i == 0)
                {
                    codecName = codec;
                    scanType = scan;
                }
                else if (codec != codecName || MediaInfoService.IsInterlacedScanType(scan) != MediaInfoService.IsInterlacedScanType(scanType))
                {
                    different.Add(DescribeBatchVideo(files[i], codec, scan));
                }
            }

            return different.Count == 0 ? "" : AppText.F("split.batch.videoMismatch", DescribeBatchVideo(files[0], codecName, scanType), string.Join("; ", different));
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Nome del file con codec e tipo di scansione, per l'errore del batch non omogeneo
        /// </summary>
        /// <param name="file">File del batch</param>
        /// <param name="codec">Codec letto, null se illeggibile</param>
        /// <param name="scan">Scansione letta, vuota se non dichiarata</param>
        /// <returns>Descrizione localizzata</returns>
        private static string DescribeBatchVideo(string file, string codec, string scan)
        {
            return AppText.F("split.batch.videoFile", Path.GetFileName(file), codec != null ? MkvSplitCodecCapabilities.GetDisplayName(codec) : "?",
                MediaInfoService.IsInterlacedScanType(scan) ? AppText.F("split.batch.interlaced", scan) : AppText.T("split.batch.progressive"));
        }

        /// <summary>
        /// Esegue la pipeline split su un singolo file già risolto senza rieseguire setup tool: la regola diventa il documento
        /// e il piano eseguibile dell'interfaccia Web, eseguiti dallo stesso ExecutePlan
        /// </summary>
        private MkvSplitExecutionResult ExecuteFileInternal(Options options, string inputFile)
        {
            MkvSplitExecutionResult result = new MkvSplitExecutionResult();
            MkvSplitOptions splitOptions;
            MkvSplitDocumentService service = new MkvSplitDocumentService();

            try
            {
                splitOptions = options.Split.Clone();
                // Il manuale nasce nell'editor: da riga di comando non c'è un montaggio da eseguire
                if (splitOptions.Manual)
                {
                    result.ExitCode = 1;
                    result.ErrorMessage = AppText.T("split.plan.manualUndefined");
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.plan.invalid", result.ErrorMessage));
                    return result;
                }
                // Il lease tiene il temporaneo dall'analisi all'esecuzione del file: una sola normalizzazione, poi sparisce
                using (MkvSplitSourceMediaLease lease = MkvSplitSourceMedia.Instance.Acquire(inputFile))
                {
                    MkvSplitPlan legacy = service.PlanRule(splitOptions, MkvSplitSourceIdentity.FromFile(inputFile), null, Guid.NewGuid(), out MkvSplitDocument document);
                    MkvSplitPlanner planner = new MkvSplitPlanner();
                    if (!legacy.IsValid)
                    {
                        result.ExitCode = 1;
                        result.ErrorMessage = legacy.ErrorMessage;
                        ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.F("split.input", legacy.InputFile));
                        planner.PrintPlan(legacy);
                        ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.plan.invalid", legacy.ErrorMessage));
                        return result;
                    }
                    // Codec, GOP aperto, frame rate e tracce li giudica il piano eseguibile: ExecutePlan dice perché non parte. Il piano
                    // della regola si stampa dopo l'intestazione dell'input
                    MkvSplitExecutionPlan plan = service.BuildExecutionPlan(document, MkvSplitAnalysisCache.Instance.GetOrBuild(inputFile), splitOptions);
                    result.ExitCode = this.ExecutePlan(plan, splitOptions, null, () => planner.PrintPlan(legacy)).ExitCode;
                    if (!plan.IsValid)
                        result.ErrorMessage = DescribeErrors(plan);
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
        /// Esegue gli output di un piano eseguibile, per la CLI e per l'interfaccia Web, pubblicando solo file completi. Il
        /// Matroska normalizzato si prende una volta per il piano e si verifica contro l'analisi; flusso grezzo, mappe e
        /// sottotitoli da tagliare al pacchetto si preparano una volta sola. Un output che fallisce non ferma gli altri
        /// </summary>
        /// <param name="plan">Piano eseguibile</param>
        /// <param name="args">Opzioni di esecuzione</param>
        /// <param name="stopRequested">Controllo cooperativo tra gli stadi e prima della pubblicazione;
        /// non inoltra automaticamente la cancellazione ai processi in corso. L'interruzione dei processi
        /// è gestita dall'infrastruttura ProcessRunner, già collegata allo stop dal chiamante Web.</param>
        /// <param name="printPlan">Stampa del piano della regola subito dopo l'intestazione dell'input (CLI); null se non c'è</param>
        /// <returns>Esito per output, con exit code 1 se un output è fallito o il piano non è eseguibile</returns>
        public MkvSplitMontageExecutionResult ExecutePlan(MkvSplitExecutionPlan plan, MkvSplitOptions args, Func<bool> stopRequested = null, Action printPlan = null)
        {
            MkvSplitMontageExecutionResult result = new MkvSplitMontageExecutionResult();
            MkvSplitSourceIdentity source = plan.Document.Source;
            string outputDir = plan.Outputs.Count > 0 ? Path.GetDirectoryName(plan.Outputs[0].Projection.FullPath) : "";
            MkvSplitSourceMediaLease lease = null;
            string planFolder = null;
            string media = null;
            string raw = null;
            List<MkvSplitFrameInfo> map = null;
            int[] decodeMap = null;
            MkvSplitCodecCapability capability = null;
            bool packets = false;
            List<string> head = null;
            string subtitles = null;
            bool hasFlac = plan.Tracks.Exists(track => IsFlac(track));
            List<TrackInfo> sourceAudio = plan.Tracks.FindAll(track => track.Type == "audio");

            foreach (MkvSplitExecutionOutput output in plan.Outputs)
                result.Outputs.Add(new MkvSplitOutputExecutionResult { OutputId = output.OutputId });
            ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.F("split.input", source.FullPath));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.output", outputDir));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.Count("split.foundChapters", plan.Analysis.Chapters.Count));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.frameCount", plan.Analysis.SourcePts.Length));
            printPlan?.Invoke();
            if (!plan.IsValid)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.plan.invalid", DescribeErrors(plan)));
                result.ExitCode = 1;
                return result;
            }
            MkvSplitVideoParams video = plan.Analysis.VideoParams;
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.videoParams", video.CodecName, video.PixFmt, video.ColorSpace, video.ColorPrimaries, video.ColorTransfer, video.ColorRange));
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.frameRateMode", plan.Analysis.FrameRateMode));
            if (args.DryRun)
            {
                foreach (MkvSplitDiagnostic warning in plan.Projection.Diagnostics.FindAll(item => item.Severity == MkvSplitDiagnosticSeverity.Warning))
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, warning.Message);
                ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.T("split.dryRun"));
                return result;
            }
            // Gli output che non si rifanno non prendono il Matroska né preparano nulla
            List<MkvSplitExecutionOutput> running = plan.Outputs.FindAll(output => args.Force || !File.Exists(output.Projection.FullPath));
            try
            {
                if (running.Count > 0)
                {
                    if (stopRequested?.Invoke() == true) throw new OperationCanceledException();
                    // Il sorgente resta identità e nome; video, tracce e allegati si leggono dal Matroska normalizzato. Rifatto
                    // dopo l'analisi, deve avere gli stessi fotogrammi con gli stessi tempi: il piano li indirizza per indice
                    lease = MkvSplitSourceMedia.Instance.Acquire(source.FullPath);
                    media = lease.Path;
                    if (lease.IsTemporary && !ReferenceEquals(lease.VerifiedAnalysis, plan.Analysis))
                    {
                        int count = MkvSplitExternalTools.Instance.CountPackets(media);
                        double[] pts = MkvSplitExternalTools.Instance.ExtractSourcePts(media, count, out _);
                        if (count != plan.Analysis.PacketCount || !pts.AsSpan().SequenceEqual(plan.Analysis.SourcePts))
                            throw new InvalidOperationException(AppText.T("split.montage.sourceContentChanged"));
                        lease.VerifiedAnalysis = plan.Analysis;
                    }
                    planFolder = Path.Combine(AppSettingsService.Instance.GetTempFolder(), "split_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(planFolder);
                    List<MkvSplitExecutionOutput> exact = running.FindAll(output => output.Clips.Exists(clip => !clip.UsesFastPath));
                    if (exact.Count > 0)
                    {
                        if (!MkvSplitCodecCapabilities.IsExactCutAvailable(video.CodecName, plan.Analysis.ScanType, out string reason))
                            throw new InvalidOperationException(reason);
                        capability = MkvSplitCodecCapabilities.Find(video.CodecName);
                        // Il taglio a pacchetti legge il Matroska normalizzato: nessun flusso grezzo né mappa dei byte
                        packets = capability.Method == MkvSplitExactCutMethod.Packets;
                        ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.T(packets ? "split.packetPath" : "split.slowPath"));
                        if (!packets)
                        {
                            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.T("split.extractingRawVideo"));
                            raw = Path.Combine(planFolder, "source." + RawExtension(capability.Codec));
                            MkvSplitExternalTools.Instance.ExtractRawTrack(media, 0, raw);
                            map = MkvSplitExternalTools.Instance.GetFrameByteMap(raw);
                        }
                        decodeMap = BuildPresentationToDecodeMap(media, plan.Analysis.SourcePts.Length);
                        if ((map != null && map.Count != plan.Analysis.SourcePts.Length) || decodeMap == null)
                            throw new InvalidOperationException(AppText.T("split.montage.invalidTimeline"));
                        head = BuildHeadEncodeArgs(video, capability, args);
                        // Sottotitoli che il ritaglio non sa riscrivere: le clip ricodificate li tagliano al pacchetto da qui
                        List<int> fallback = exact.SelectMany(output => output.FallbackSubtitleTrackIds).Distinct().ToList();
                        if (fallback.Count > 0)
                        {
                            subtitles = Path.Combine(planFolder, "subtitles.mks");
                            MkvSplitExternalTools.Instance.RunMkvmerge(new string[] { "-o", subtitles, "--no-video", "--no-audio", "--no-chapters", "--no-attachments", "--no-global-tags",
                                "--subtitle-tracks", string.Join(",", plan.Tracks.FindAll(track => fallback.Contains(track.Id)).ConvertAll(track => track.Id)), media });
                        }
                    }
                }
                foreach (MkvSplitExecutionOutput output in plan.Outputs)
                {
                    MkvSplitOutputExecutionResult state = result.Outputs.Find(item => item.OutputId == output.OutputId);
                    string temporary = null;
                    try
                    {
                        if (stopRequested?.Invoke() == true) throw new OperationCanceledException();
                        ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.F("split.segment", plan.Outputs.IndexOf(output) + 1, output.Projection.FileName));
                        if (!source.Matches(MkvSplitSourceIdentity.FromFile(source.FullPath)))
                            throw new InvalidOperationException(AppText.T("split.montage.sourceChanged"));
                        string path = output.Projection.FullPath;
                        if (string.Equals(path, source.FullPath, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(AppText.F("split.montage.nameCollision", path));
                        if (!running.Contains(output))
                        {
                            ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, AppText.F("split.skipExists", output.Projection.FileName,
                                (new FileInfo(path).Length / 1048576.0).ToString("F1", CultureInfo.InvariantCulture)));
                            state.Status = MkvSplitOutputExecutionStatus.ExistsSkipped;
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        // Avvisi non bloccanti dell'output nel log (D25)
                        foreach (MkvSplitDiagnostic warning in plan.Projection.Diagnostics.FindAll(item => item.Severity == MkvSplitDiagnosticSeverity.Warning && item.OutputId == output.OutputId))
                            ConsoleHelper.Write(LogSection.Split, LogLevel.Notice, warning.Message);
                        // Clip e file completo nascono accanto alla destinazione: la pubblicazione è un rename sullo stesso volume
                        temporary = Path.Combine(Path.GetDirectoryName(path), ".remuxforge-split-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(temporary);
                        MkvSplitExecutor executor = new MkvSplitExecutor();
                        bool separateFlac = hasFlac && output.Clips.Count > 1;
                        List<string> clips = new List<string>();
                        string clipSubtitles = output.FallbackSubtitleTrackIds.Count > 0 ? subtitles : null;
                        // I pacchetti dei B iniziali che restano in testa a una clip (D25, §11 (q)) la allungano: il suo keyframe e
                        // tutto ciò che segue arrivano dopo la loro durata. I B iniziali che una clip perde alla fine la accorciano: il
                        // suo video finisce prima del taglio e la clip successiva parte prima della stessa durata
                        double[] heads = new double[output.Clips.Count];
                        double[] losts = new double[output.Clips.Count];
                        for (int index = 0; index < heads.Length; index++)
                        {
                            MkvSplitSegment segment = output.Clips[index].Segment;
                            heads[index] = output.Clips[index].OpenGopHeadPackets > 0
                                ? plan.Analysis.SourcePts[segment.StartFrame] - plan.Analysis.SourcePts[segment.StartFrame - output.Clips[index].OpenGopHeadPackets] : 0;
                            losts[index] = output.Clips[index].OpenGopLostFrames > 0
                                ? output.Projection.Clips[index].SourceEndSeconds - plan.Analysis.SourcePts[segment.StartFrame + segment.FrameCount - output.Clips[index].OpenGopLostFrames] : 0;
                        }
                        foreach (MkvSplitExecutionClip clip in output.Clips)
                        {
                            if (stopRequested?.Invoke() == true) throw new OperationCanceledException();
                            string work = Path.Combine(temporary, "clip-" + clips.Count);
                            Directory.CreateDirectory(work);
                            string clipPath = Path.Combine(work, "clip.mkv");
                            if (clip.UsesFastPath) executor.SplitFast(clip.Segment, media, clipPath, work, plan.Tracks, output.FallbackSubtitleTrackIds);
                            else if (packets) executor.SplitPackets(clip.Segment, media, plan.Analysis.SourcePts, plan.Analysis.KeyFlags, decodeMap, head, capability.Codec, clipPath, work, plan.Tracks, output.FallbackSubtitleTrackIds, clipSubtitles);
                            else executor.SplitSlow(clip.Segment, media, raw, map, plan.Analysis.SourcePts, decodeMap, head, capability.Codec, clipPath, work, plan.Tracks, output.FallbackSubtitleTrackIds, clipSubtitles);
                            // Il keyframe di GOP aperto toglie i B iniziali alla fine e li lascia come pacchetti in testa (D25): né più né meno
                            int clipPackets = clip.Segment.FrameCount + clip.OpenGopHeadPackets + clip.OpenGopDroppedPackets - clip.OpenGopLostFrames;
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
                                long shift = (long)Math.Round(MkvSplitExternalTools.Instance.ExtractSourcePts(clipPath, clipPackets, out _)[clip.OpenGopDroppedPackets] * 1000.0);
                                if (shift != 0)
                                {
                                    string shifted = Path.Combine(work, "clip-shifted.mkv");
                                    MkvSplitExternalTools.Instance.RunMkvmerge(new string[] { "-o", shifted, "--sync", "-1:" + (-shift).ToString(CultureInfo.InvariantCulture), clipPath });
                                    File.Move(shifted, clipPath, true);
                                }
                                if (clip.OpenGopDroppedPackets > 0 && MkvSplitExternalTools.Instance.CountPackets(clipPath) != clipPackets - clip.OpenGopDroppedPackets)
                                    throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
                            }
                            // Alla giunta l'audio della clip si ferma alla fine del suo video, così l'accodamento per file fa partire la clip
                            // successiva dopo l'ultimo fotogramma con lo scarto fra audio e video del sorgente: si tolgono i pacchetti che
                            // finiscono oltre, cioè i B iniziali persi su un keyframe di GOP aperto (D25) e la frazione di blocco che sfora il
                            // taglio. mkvmerge non taglia il FLAC: con il FLAC separato resta intero e lo ferma il concat
                            if (clips.Count < output.Clips.Count - 1)
                            {
                                MkvSplitExternalTools.Instance.ExtractSourcePts(clipPath, clipPackets - clip.OpenGopDroppedPackets, out double videoEnd);
                                List<TrackInfo> clipTracks = MkvSplitExternalTools.Instance.GetFileInfo(clipPath)?.Tracks;
                                List<TrackInfo> clipAudio = clipTracks?.FindAll(track => track.Type == "audio");
                                if (!double.IsFinite(videoEnd) || clipAudio == null || clipAudio.Count != sourceAudio.Count)
                                    throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
                                double cut = double.PositiveInfinity;
                                List<int> trimmedIds = new List<int>();
                                for (int index = 0; index < clipAudio.Count; index++)
                                {
                                    if (separateFlac && IsFlac(sourceAudio[index])) continue;
                                    trimmedIds.Add(clipAudio[index].Id);
                                    cut = Math.Min(cut, MkvSplitExternalTools.Instance.GetAudioOverrunStart(clipPath, clipAudio[index].Id, videoEnd));
                                }
                                if (double.IsFinite(cut))
                                {
                                    string audio = Path.Combine(work, "clip-audio.mka");
                                    string trimmed = Path.Combine(work, "clip-trimmed.mkv");
                                    MkvSplitExternalTools.Instance.RunMkvmerge(new string[] { "-o", audio, "--no-video", "--no-subtitles", "--no-chapters", "--no-attachments", "--no-global-tags",
                                        "--audio-tracks", string.Join(",", trimmedIds), "--split", "parts:00:00:00-" + MkvSplitSegmentService.SecsToTs(cut), clipPath });
                                    // Le tracce tagliate tornano al loro posto fra quelle della clip, video secondari e sottotitoli compresi:
                                    // l'unione e il concat del FLAC le leggono per posizione
                                    List<string> order = new List<string>();
                                    int trimmedIndex = 0;
                                    foreach (TrackInfo track in clipTracks)
                                        order.Add(trimmedIds.Contains(track.Id) ? "1:" + trimmedIndex++ : "0:" + track.Id);
                                    List<string> merge = new List<string> { "-o", trimmed };
                                    List<int> keptIds = clipAudio.FindAll(track => !trimmedIds.Contains(track.Id)).ConvertAll(track => track.Id);
                                    if (keptIds.Count == 0) merge.Add("--no-audio");
                                    else { merge.Add("--audio-tracks"); merge.Add(string.Join(",", keptIds)); }
                                    merge.AddRange(new string[] { clipPath, audio, "--track-order", string.Join(",", order) });
                                    MkvSplitExternalTools.Instance.RunMkvmerge(merge);
                                    File.Move(trimmed, clipPath, true);
                                }
                            }
                            clips.Add(clipPath);
                        }
                        if (stopRequested?.Invoke() == true) throw new OperationCanceledException();
                        string assembled = clips[0];
                        if (clips.Count > 1)
                        {
                            assembled = Path.Combine(temporary, "assembled.mkv");
                            // Accodamento per file: la clip successiva parte dopo il pacchetto più tardo della precedente, cioè dopo il suo
                            // ultimo fotogramma, perché l'audio si ferma prima. Per traccia l'audio della clip successiva seguirebbe la fine
                            // dell'audio della precedente e ne erediterebbe lo scarto rispetto al video
                            List<string> append = new List<string> { "-o", assembled, "--append-mode", "file" };
                            foreach (string clip in clips)
                            {
                                if (clip != clips[0]) append.Add("+");
                                append.Add("--no-chapters"); append.Add("--no-attachments");
                                if (separateFlac)
                                {
                                    MkvFileInfo clipInfo = MkvSplitExternalTools.Instance.GetFileInfo(clip);
                                    List<TrackInfo> clipAudio = clipInfo?.Tracks.FindAll(track => track.Type == "audio");
                                    if (clipAudio == null || clipAudio.Count != sourceAudio.Count)
                                        throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
                                    List<int> compatibleIds = new List<int>();
                                    for (int index = 0; index < sourceAudio.Count; index++)
                                        if (!IsFlac(sourceAudio[index])) compatibleIds.Add(clipAudio[index].Id);
                                    if (compatibleIds.Count == 0) append.Add("--no-audio");
                                    else { append.Add("--audio-tracks"); append.Add(string.Join(",", compatibleIds)); }
                                }
                                append.Add(clip);
                            }
                            MkvSplitExternalTools.Instance.RunMkvmerge(append);
                        }
                        string final = Path.Combine(temporary, "complete.mkv");
                        List<string> mux = new List<string> { "-o", final, "--no-chapters", "--no-attachments", "--no-global-tags" };
                        // I sottotitoli delle clip sono solo quelli tagliati al pacchetto
                        if (output.FallbackSubtitleTrackIds.Count == 0) mux.Add("--no-subtitles");
                        // Gli intermedi slow riordinano gli ID; ripristinare i flag e i nomi per posizione/type, non per ID sorgente.
                        // Le tracce video secondarie seguono la principale come nel sorgente
                        MkvFileInfo assembledInfo = MkvSplitExternalTools.Instance.GetFileInfo(assembled);
                        Dictionary<int, string> trackOrder = new Dictionary<int, string>();
                        foreach (string type in new string[] { "video", "audio", "subtitles" })
                        {
                            List<TrackInfo> original = plan.Tracks.FindAll(track => track.Type == type && !(separateFlac && IsFlac(track))
                                && (type != "subtitles" || output.FallbackSubtitleTrackIds.Contains(track.Id)));
                            List<TrackInfo> actual = assembledInfo.Tracks.FindAll(track => track.Type == type);
                            if (original.Count != actual.Count) throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
                            for (int index = 0; index < actual.Count; index++)
                            {
                                AddTrackOptions(mux, actual[index].Id, original[index]);
                                trackOrder.Add(original[index].Id, "0:" + actual[index].Id);
                            }
                        }
                        mux.Add(assembled);
                        // Riutilizzare gli allegati e i tag globali originali senza introdurre altre tracce.
                        mux.AddRange(new string[] { "--no-video", "--no-audio", "--no-chapters", "--no-track-tags" });
                        if (output.NativeSubtitleTrackIds.Count == 0) mux.Add("--no-subtitles");
                        else
                        {
                            mux.Add("--subtitle-tracks"); mux.Add(string.Join(",", output.NativeSubtitleTrackIds));
                            foreach (TrackInfo track in plan.Tracks.FindAll(track => output.NativeSubtitleTrackIds.Contains(track.Id)))
                            {
                                AddTrackOptions(mux, track.Id, track);
                                trackOrder.Add(track.Id, "1:" + track.Id);
                            }
                        }
                        mux.Add(media);
                        int inputIndex = 2;
                        if (separateFlac)
                        {
                            // Durate video esplicite: il concat audio segue le posizioni risultato, non i tag durata degli intermedi. Alla
                            // giunta il FLAC si ferma alla fine del video della clip, come le altre tracce audio
                            string manifest = Path.Combine(temporary, "flac.ffconcat");
                            using (StreamWriter writer = new StreamWriter(manifest, false, new System.Text.UTF8Encoding(false)))
                            {
                                writer.WriteLine("ffconcat version 1.0");
                                for (int index = 0; index < clips.Count; index++)
                                {
                                    string videoDuration = (output.Projection.Clips[index].DurationSeconds + heads[index] - losts[index]).ToString("R", CultureInfo.InvariantCulture);
                                    writer.WriteLine("file 'clip-" + index + "/clip.mkv'");
                                    writer.WriteLine("duration " + videoDuration);
                                    if (index < clips.Count - 1) writer.WriteLine("outpoint " + videoDuration);
                                }
                            }
                            for (int index = 0; index < sourceAudio.Count; index++)
                            {
                                TrackInfo audio = sourceAudio[index];
                                if (!IsFlac(audio)) continue;
                                if (stopRequested?.Invoke() == true) throw new OperationCanceledException();
                                string audioPath = Path.Combine(temporary, "flac-" + index + ".mka");
                                MkvSplitExternalTools.Instance.RunFfmpeg(new string[] { "-y", "-v", "error", "-f", "concat", "-safe", "0", "-i", manifest,
                                    "-map", "0:a:" + index, "-c:a", "copy", "-vn", "-sn", "-dn", audioPath });
                                MkvFileInfo audioInfo = MkvSplitExternalTools.Instance.GetFileInfo(audioPath);
                                if (audioInfo?.Tracks.Count != 1 || !IsFlac(audioInfo.Tracks[0]))
                                    throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
                                AddTrackOptions(mux, audioInfo.Tracks[0].Id, audio);
                                trackOrder.Add(audio.Id, inputIndex++ + ":" + audioInfo.Tracks[0].Id);
                                mux.Add("--no-chapters"); mux.Add("--no-attachments"); mux.Add("--no-global-tags"); mux.Add(audioPath);
                            }
                        }
                        // La proiezione mette ogni clip dove inizia il suo keyframe senza i B iniziali; il video copiato lo mostra dopo
                        // quelli rimasti in testa alla clip e alle precedenti (D25, §11 (q)) e prima di quelli che le precedenti hanno perso
                        // alla fine: sottotitoli e capitoli riscritti seguono il video, e alla giunta i sottotitoli della clip si fermano alla
                        // fine del suo video. Un capitolo in apertura di clip, entro l'arrotondamento al fotogramma, resta sul suo inizio, con i B iniziali; gli altri seguono il keyframe (§11 (r))
                        MkvSplitOutputProjection placed = new MkvSplitOutputProjection { OutputId = output.OutputId, DurationSeconds = output.Projection.DurationSeconds, FrameCount = output.Projection.FrameCount };
                        List<ChapterMark> placedChapters = ChapterTimelineService.Clone(output.Projection.Chapters);
                        double headsBefore = 0;
                        double lostsBefore = 0;
                        long clipNs = 0;
                        for (int index = 0; index < output.Projection.Clips.Count; index++)
                        {
                            MkvSplitClipProjection clip = output.Projection.Clips[index];
                            long nextNs = clipNs + (long)Math.Round(clip.SourceEndSeconds * 1000000000.0) - (long)Math.Round(clip.SourceStartSeconds * 1000000000.0);
                            placed.Clips.Add(new MkvSplitClipProjection
                            {
                                ClipId = clip.ClipId,
                                StartFrame = clip.StartFrame,
                                EndFrameExclusive = clip.EndFrameExclusive,
                                ResultStartFrame = clip.ResultStartFrame,
                                SourceStartSeconds = clip.SourceStartSeconds,
                                SourceEndSeconds = index < output.Projection.Clips.Count - 1 ? clip.SourceEndSeconds - losts[index] : clip.SourceEndSeconds,
                                ResultStartSeconds = clip.ResultStartSeconds + headsBefore + heads[index] - lostsBefore,
                                DurationSeconds = clip.DurationSeconds
                            });
                            // Stessi confini in nanosecondi del montaggio dei capitoli (ChapterTimelineService.Montage)
                            for (int position = 0; position < placedChapters.Count; position++)
                            {
                                long start = output.Projection.Chapters[position].StartNs;
                                if (start < clipNs || start >= nextNs)
                                    continue;
                                if (placedChapters[position].HasEnd)
                                    placedChapters[position].EndNs += (long)Math.Round((headsBefore + heads[index] - lostsBefore) * 1000000000.0);
                                placedChapters[position].StartNs += (long)Math.Round((start - clipNs <= ChapterTimelineService.FRAME_ROUNDING_NS ? headsBefore : headsBefore + heads[index]) * 1000000000.0 - lostsBefore * 1000000000.0);
                            }
                            headsBefore += heads[index];
                            lostsBefore += losts[index];
                            clipNs = nextNs;
                        }
                        foreach (MkvSplitSubtitleTrack subtitle in plan.Subtitles)
                        {
                            if (output.NativeSubtitleTrackIds.Contains(subtitle.Track.Id)) continue;
                            string subtitlePath = Path.Combine(temporary, "result-sub-" + subtitle.Track.Id + subtitle.Extension);
                            MkvSplitSubtitleService.WriteOutput(subtitle, placed, subtitlePath);
                            AddTrackOptions(mux, 0, subtitle.Track);
                            mux.Add(subtitlePath);
                            trackOrder.Add(subtitle.Track.Id, inputIndex++ + ":0");
                        }
                        // Sottotitoli nativi e riscritti arrivano da input diversi: l'ordine originale va imposto in modo esplicito
                        if (separateFlac || plan.Tracks.TrueForAll(track => trackOrder.ContainsKey(track.Id)))
                        {
                            mux.Add("--track-order");
                            mux.Add(string.Join(",", plan.Tracks.ConvertAll(track => trackOrder[track.Id])));
                        }
                        if (placedChapters.Count > 0)
                        {
                            string chapters = Path.Combine(temporary, "chapters.xml");
                            ChapterTimelineService.WriteXmlFile(placedChapters, chapters);
                            mux.Add("--chapters"); mux.Add(chapters);
                        }
                        if (stopRequested?.Invoke() == true) throw new OperationCanceledException();
                        MkvSplitExternalTools.Instance.RunMkvmerge(mux);
                        ValidateMontageVideo(final, output, plan.Analysis, heads, losts);
                        if (!source.Matches(MkvSplitSourceIdentity.FromFile(source.FullPath)))
                            throw new InvalidOperationException(AppText.T("split.montage.sourceChanged"));
                        if (stopRequested?.Invoke() == true) throw new OperationCanceledException();
                        File.Move(final, path, args.Force);
                        state.Status = MkvSplitOutputExecutionStatus.Done;
                    }
                    catch (Exception exception)
                    {
                        if (exception is OperationCanceledException || stopRequested?.Invoke() == true)
                            throw new OperationCanceledException();
                        state.Status = MkvSplitOutputExecutionStatus.Failed;
                        state.ErrorMessage = exception.Message;
                        result.ExitCode = 1;
                        ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.errorLine", exception.Message));
                    }
                    finally
                    {
                        if (temporary != null) try { Directory.Delete(temporary, true); } catch (IOException) { }
                    }
                }
            }
            catch (Exception exception)
            {
                bool cancelled = exception is OperationCanceledException || stopRequested?.Invoke() == true;
                // Senza Matroska o senza preparazione nessun output in attesa può partire: falliscono tutti con lo stesso motivo
                foreach (MkvSplitOutputExecutionResult pending in result.Outputs.FindAll(item => item.Status == MkvSplitOutputExecutionStatus.Pending))
                {
                    pending.Status = cancelled ? MkvSplitOutputExecutionStatus.Cancelled : MkvSplitOutputExecutionStatus.Failed;
                    pending.ErrorMessage = cancelled ? pending.ErrorMessage : exception.Message;
                }
                result.ExitCode = 1;
                if (!cancelled)
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.errorLine", exception.Message));
            }
            finally
            {
                if (planFolder != null) try { Directory.Delete(planFolder, true); } catch (IOException) { }
                lease?.Dispose();
            }
            // Riepilogo: gli output scritti e, a parte, quelli falliti
            List<string> failed = plan.Outputs.FindAll(output => result.Outputs.Find(item => item.OutputId == output.OutputId).Status == MkvSplitOutputExecutionStatus.Failed)
                .ConvertAll(output => output.Projection.FileName);
            ConsoleHelper.Write(LogSection.Split, failed.Count == 0 ? LogLevel.Success : LogLevel.Notice,
                AppText.Count("split.done", result.Outputs.Count(item => item.Status == MkvSplitOutputExecutionStatus.Done), outputDir));
            if (failed.Count > 0)
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.Count("split.failedOutputs", failed.Count, string.Join(", ", failed)));
            return result;
        }

        /// <summary>
        /// Errori del piano eseguibile per i messaggi che non mostrano la clip: il motivo senza il riferimento alla clip quando c'è
        /// </summary>
        /// <param name="plan">Piano eseguibile non valido</param>
        /// <returns>Errori separati da spazi</returns>
        private static string DescribeErrors(MkvSplitExecutionPlan plan)
        {
            return string.Join(" ", plan.Projection.Diagnostics.Where(item => item.Severity == MkvSplitDiagnosticSeverity.Error)
                .Select(item => string.IsNullOrEmpty(item.Reason) ? item.Message : item.Reason));
        }

        /// <summary>
        /// Aggiunge al comando mkvmerge lingua, nome e flag della traccia originale, per la traccia con l'ID indicato nel file che segue
        /// </summary>
        /// <param name="command">Argomenti mkvmerge</param>
        /// <param name="id">ID della traccia nel file che segue gli argomenti</param>
        /// <param name="track">Traccia del sorgente da cui copiare le proprietà</param>
        private static void AddTrackOptions(List<string> command, int id, TrackInfo track)
        {
            command.AddRange(new string[] { "--language", id + ":" + (string.IsNullOrEmpty(track.LanguageIetf) ? (string.IsNullOrEmpty(track.Language) ? "und" : track.Language) : track.LanguageIetf),
                "--track-name", id + ":" + (track.Name ?? ""), "--default-track-flag", id + ":" + (track.DefaultTrack ? "yes" : "no"),
                "--forced-display-flag", id + ":" + (track.ForcedTrack ? "yes" : "no") });
        }

        /// <summary>
        /// Indica se la traccia è audio FLAC, che il montaggio taglia a parte quando un output ha più clip
        /// </summary>
        /// <param name="track">Traccia da controllare</param>
        /// <returns>True per una traccia audio FLAC</returns>
        private static bool IsFlac(TrackInfo track)
        {
            return track.Type == "audio" && track.Codec.IndexOf("FLAC", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Verifica che il video dell'output abbia esattamente i fotogrammi e i tempi della proiezione. Su un keyframe di GOP
        /// aperto copiato (D25) una clip può portare in testa i pacchetti dei B iniziali, che spostano lei e le successive
        /// della loro durata, o averli tolti (§11 (q)); alla fine perde i propri e anticipa le successive: né più né meno
        /// </summary>
        /// <param name="path">Output completo da verificare</param>
        /// <param name="output">Output del piano eseguibile</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="heads">Durata in secondi dei pacchetti dei B iniziali in testa a ogni clip, 0 se assenti</param>
        /// <param name="losts">Durata in secondi dei B iniziali persi alla fine di ogni clip, 0 se assenti</param>
        private static void ValidateMontageVideo(string path, MkvSplitExecutionOutput output, MkvSplitAnalysis analysis, double[] heads, double[] losts)
        {
            int packets = MkvSplitExternalTools.Instance.CountPackets(path);
            double[] actual = MkvSplitExternalTools.Instance.ExtractSourcePts(path, packets, out double end);
            if (actual.Length != output.Projection.FrameCount + output.Clips.Sum(clip => clip.OpenGopHeadPackets - clip.OpenGopLostFrames) || !double.IsFinite(end)
                || Math.Abs(end - (output.Projection.DurationSeconds + heads.Sum() - losts.Sum())) > 0.001001)
                throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
            int index = 0;
            double shift = 0;
            for (int position = 0; position < output.Projection.Clips.Count; position++)
            {
                MkvSplitClipProjection clip = output.Projection.Clips[position];
                shift += heads[position];
                for (int frame = clip.StartFrame - output.Clips[position].OpenGopHeadPackets; frame < clip.EndFrameExclusive - output.Clips[position].OpenGopLostFrames; frame++)
                    if (Math.Abs(actual[index++] - (clip.ResultStartSeconds + shift + analysis.SourcePts[frame] - clip.SourceStartSeconds)) > 0.001001)
                        throw new InvalidOperationException(AppText.T("split.montage.outputMismatch"));
                shift -= losts[position];
            }
        }

        /// <summary>
        /// Costruisce gli argomenti dell'encoder per testa e coda ricodificate: qualità e velocità dalle opzioni
        /// del lavoro, oppure i default del codec; pix_fmt e colori dal sorgente
        /// </summary>
        /// <param name="p">Parametri video del sorgente</param>
        /// <param name="capability">Capacità del codec, con taglio al fotogramma</param>
        /// <param name="options">Opzioni Split del lavoro</param>
        /// <returns>Argomenti ffmpeg dell'encoder</returns>
        internal static List<string> BuildHeadEncodeArgs(MkvSplitVideoParams p, MkvSplitCodecCapability capability, MkvSplitOptions options)
        {
            string paramsFlag;
            string defaultPixFmt;
            string pixFmt;
            int quality = options.EncodeQuality ?? capability.QualityDefault;
            string speed = string.IsNullOrWhiteSpace(options.EncodeSpeed) ? capability.SpeedDefault : options.EncodeSpeed.Trim().ToLowerInvariant();
            List<string> args = new List<string>();
            List<string> encParams = new List<string>();

            if (capability.Method == MkvSplitExactCutMethod.Packets)
            {
                // L'encoder h263 accetta solo i formati standard: le altre risoluzioni sono H.263+ e si ricodificano con h263p
                args.Add("-c:v");
                args.Add(capability.Codec == MkvSplitCodec.H263 && Array.IndexOf(s_h263StandardSizes, p.Width + "x" + p.Height) < 0 ? "h263p" : capability.Encoder);
                if (capability.Codec == MkvSplitCodec.Av1 || capability.Codec == MkvSplitCodec.Vp9)
                {
                    args.Add("-crf"); args.Add(quality.ToString(CultureInfo.InvariantCulture));
                    // libvpx-vp9 è in modalità a qualità costante solo senza bitrate obiettivo
                    if (capability.Codec == MkvSplitCodec.Vp9) { args.Add("-b:v"); args.Add("0"); }
                    args.Add("-" + capability.SpeedName); args.Add(speed);
                    args.Add("-g"); args.Add("1");
                    args.Add("-pix_fmt"); args.Add(p.PixFmt != null ? p.PixFmt : "yuv420p");
                    // Colori del sorgente come per H.264, H.265 e MPEG-2: libsvtav1 li scrive tutti nel bitstream, libvpx-vp9 solo
                    // spazio colore e range, primarie e trasferimento restano al contenitore
                    if (p.ColorSpace != null) { args.Add("-colorspace"); args.Add(p.ColorSpace); }
                    if (p.ColorPrimaries != null) { args.Add("-color_primaries"); args.Add(p.ColorPrimaries); }
                    if (p.ColorTransfer != null) { args.Add("-color_trc"); args.Add(p.ColorTransfer); }
                    if (p.ColorRange != null) { args.Add("-color_range"); args.Add(p.ColorRange); }
                }
                else
                {
                    args.Add("-q:v"); args.Add(quality.ToString(CultureInfo.InvariantCulture));
                    // MPEG-4 ASP con B-frame: una testa tutta intra avrebbe un VOL low delay diverso da quello del corpo (§3.3 punto 3)
                    if (capability.Codec == MkvSplitCodec.Mpeg4) { args.Add("-bf"); args.Add("2"); }
                    else { args.Add("-g"); args.Add("1"); }
                    // Video for Windows: testa e coda con il fourcc del sorgente, o l'unione con il corpo non è compatibile
                    if (p.CodecTag != null) { args.Add("-vtag"); args.Add(p.CodecTag); }
                }

                return args;
            }

            if (capability.Codec == MkvSplitCodec.H264)
            {
                paramsFlag = "-x264-params";
                defaultPixFmt = "yuv420p";
            }
            else if (capability.Codec == MkvSplitCodec.Mpeg2)
            {
                paramsFlag = null;
                defaultPixFmt = "yuv420p";
            }
            else
            {
                paramsFlag = "-x265-params";
                defaultPixFmt = "yuv420p10le";
            }

            pixFmt = p.PixFmt != null ? p.PixFmt : defaultPixFmt;
            args.Add("-c:v"); args.Add(capability.Encoder);
            if (capability.Codec == MkvSplitCodec.Mpeg2)
            {
                // MPEG-2 non ha CRF: la qualità si esprime con il quantizzatore, e l'all-intra
                // si ottiene con i flag dell'encoder invece che con i parametri di x264/x265
                args.Add("-q:v"); args.Add(quality.ToString(CultureInfo.InvariantCulture));
                args.Add("-g"); args.Add("1");
                args.Add("-bf"); args.Add("0");
            }
            else
            {
                args.Add("-crf"); args.Add(quality.ToString(CultureInfo.InvariantCulture));
                args.Add("-preset"); args.Add(speed);
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
