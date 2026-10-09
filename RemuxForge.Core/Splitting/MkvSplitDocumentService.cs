using RemuxForge.Core.Chapters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Autorità del documento, delle coordinate e del piano Split. Non modifica mai gli input.
    /// </summary>
    public partial class MkvSplitDocumentService
    {
        #region Costanti

        /// <summary>
        /// Tolleranza di risoluzione: ben sotto qualunque intervallo fra frame, ben sopra l'errore di arrotondamento dei double in secondi
        /// </summary>
        private const double RESOLVE_TOLERANCE_SECONDS = 1e-7;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Converte un piano legacy in documento: un output con una sola clip per ogni segmento
        /// </summary>
        /// <param name="plan">Piano legacy</param>
        /// <param name="source">Identità del sorgente</param>
        /// <returns>Documento equivalente</returns>
        public MkvSplitDocument FromLegacyPlan(MkvSplitPlan plan, MkvSplitSourceIdentity source)
        {
            MkvSplitDocument document = new MkvSplitDocument
            {
                Source = source.Clone(),
                OriginMode = plan.Mode
            };
            foreach (MkvSplitSegment segment in plan.Segments)
            {
                document.Outputs.Add(new MkvSplitOutput
                {
                    Clips = new List<MkvSplitClip>
                    {
                        new MkvSplitClip
                        {
                            StartFrame = segment.StartFrame,
                            EndFrameExclusive = checked(segment.StartFrame + segment.FrameCount)
                        }
                    }
                });
            }
            return document;
        }

        /// <summary>
        /// Crea un documento con un solo output che copre l'intero sorgente
        /// </summary>
        /// <param name="source">Identità del sorgente</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <returns>Documento con il sorgente intero</returns>
        public MkvSplitDocument CreateFullSource(MkvSplitSourceIdentity source, MkvSplitAnalysis analysis)
        {
            return new MkvSplitDocument
            {
                Source = source.Clone(),
                OriginMode = MkvSplitMode.Manual,
                Outputs = new List<MkvSplitOutput>
                {
                    new MkvSplitOutput
                    {
                        Clips = new List<MkvSplitClip>
                        {
                            new MkvSplitClip
                            {
                                StartFrame = 0,
                                EndFrameExclusive = analysis.SourcePts.Length
                            }
                        }
                    }
                }
            };
        }

        /// <summary>
        /// Crea una diagnostica di errore con messaggio localizzato split.montage.{code}
        /// </summary>
        /// <param name="code">Codice della diagnostica</param>
        /// <param name="outputId">Output coinvolto</param>
        /// <param name="clipId">Clip coinvolta</param>
        /// <param name="args">Argomenti del messaggio</param>
        /// <returns>Diagnostica di errore</returns>
        internal static MkvSplitDiagnostic Diagnostic(string code, Guid? outputId = null, Guid? clipId = null, params object[] args)
        {
            return new MkvSplitDiagnostic
            {
                Code = code,
                Message = AppText.F("split.montage." + code, args),
                Severity = MkvSplitDiagnosticSeverity.Error,
                OutputId = outputId,
                ClipId = clipId
            };
        }

        /// <summary>
        /// Validazione strutturale utilizzabile su un draft che contiene output vuoti.
        /// </summary>
        /// <param name="document">Documento da validare</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <returns>Diagnostiche di errore</returns>
        internal static List<MkvSplitDiagnostic> ValidateStructure(MkvSplitDocument document, MkvSplitAnalysis analysis)
        {
            List<MkvSplitDiagnostic> errors = new List<MkvSplitDiagnostic>();
            if (document == null || document.Source == null || string.IsNullOrEmpty(document.Source.FullPath)
                || document.Id == Guid.Empty || document.Outputs == null || !Enum.IsDefined(document.OriginMode))
            {
                errors.Add(Diagnostic("invalidDocument"));
                return errors;
            }
            if (analysis?.SourcePts == null || analysis.SourcePts.Length == 0 || !double.IsFinite(analysis.Duration)
                || analysis.Duration <= analysis.SourcePts[analysis.SourcePts.Length - 1])
            {
                errors.Add(Diagnostic("invalidTimeline"));
                return errors;
            }
            for (int index = 0; index < analysis.SourcePts.Length; index++)
            {
                if (!double.IsFinite(analysis.SourcePts[index]) || analysis.SourcePts[index] < 0
                    || (index > 0 && analysis.SourcePts[index] <= analysis.SourcePts[index - 1]))
                {
                    errors.Add(Diagnostic("invalidTimeline"));
                    return errors;
                }
            }
            HashSet<Guid> ids = new HashSet<Guid> { document.Id };
            foreach (MkvSplitOutput output in document.Outputs)
            {
                if (output == null || output.Id == Guid.Empty || !ids.Add(output.Id) || output.Clips == null || !Enum.IsDefined(output.NameMode))
                {
                    errors.Add(Diagnostic("invalidDocument", output?.Id));
                    continue;
                }
                long frameCount = 0;
                foreach (MkvSplitClip clip in output.Clips)
                {
                    if (clip == null || clip.Id == Guid.Empty || !ids.Add(clip.Id))
                        errors.Add(Diagnostic("invalidDocument", output.Id, clip?.Id));
                    else if (clip.StartFrame < 0 || clip.EndFrameExclusive <= clip.StartFrame || clip.EndFrameExclusive > analysis.SourcePts.Length)
                        errors.Add(Diagnostic("invalidRange", output.Id, clip.Id));
                    else
                        frameCount += clip.EndFrameExclusive - clip.StartFrame;
                }
                if (frameCount > int.MaxValue)
                    errors.Add(Diagnostic("invalidRange", output.Id));
            }
            return errors;
        }

        /// <summary>
        /// Proietta il documento sulla timeline: clip, durate, capitoli, nomi e percorsi degli output, copertura del sorgente
        /// </summary>
        /// <param name="document">Documento da proiettare</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="options">Opzioni Split</param>
        /// <returns>Proiezione con eventuali diagnostiche</returns>
        public MkvSplitTimelineProjection Project(MkvSplitDocument document, MkvSplitAnalysis analysis, MkvSplitOptions options)
        {
            MkvSplitTimelineProjection projection = new MkvSplitTimelineProjection();
            projection.Diagnostics.AddRange(ValidateStructure(document, analysis));
            if (!projection.IsValid)
                return projection;
            foreach (string error in MkvSplitSegmentService.ValidateTemplate(options.OutputTemplate))
            {
                projection.Diagnostics.Add(new MkvSplitDiagnostic
                {
                    Code = "invalidName",
                    Message = error,
                    Severity = MkvSplitDiagnosticSeverity.Error
                });
            }
            if (!projection.IsValid)
                return projection;
            List<(int start, int end)> coverage = new List<(int, int)>();
            HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string directory = Path.GetFullPath(string.IsNullOrEmpty(options.OutputDir) ? Path.GetDirectoryName(document.Source.FullPath) : options.OutputDir);
            if (document.Outputs.Count == 0)
                projection.Diagnostics.Add(Diagnostic("noOutputs"));
            foreach (MkvSplitOutput output in document.Outputs)
            {
                MkvSplitOutputProjection result = new MkvSplitOutputProjection { OutputId = output.Id };
                List<ChapterRange> chapterRanges = new List<ChapterRange>();
                projection.Outputs.Add(result);
                foreach (MkvSplitClip clip in output.Clips)
                {
                    double start = analysis.SourcePts[clip.StartFrame];
                    double end = BoundarySeconds(analysis, clip.EndFrameExclusive);
                    result.Clips.Add(new MkvSplitClipProjection
                    {
                        ClipId = clip.Id,
                        StartFrame = clip.StartFrame,
                        EndFrameExclusive = clip.EndFrameExclusive,
                        ResultStartFrame = result.FrameCount,
                        SourceStartSeconds = start,
                        SourceEndSeconds = end,
                        ResultStartSeconds = result.DurationSeconds,
                        DurationSeconds = end - start
                    });
                    chapterRanges.Add(new ChapterRange((long)Math.Round(start * 1000000000.0), (long)Math.Round(end * 1000000000.0)));
                    result.DurationSeconds += end - start;
                    result.FrameCount += clip.EndFrameExclusive - clip.StartFrame;
                    coverage.Add((clip.StartFrame, clip.EndFrameExclusive));
                }
                // Capitoli del risultato: quelli che iniziano in ogni clip, nella sua posizione, con la fine troncata alla clip
                result.Chapters = ChapterTimelineService.Montage(analysis.Chapters, chapterRanges);
                if (output.Clips.Count == 0)
                    projection.Diagnostics.Add(Diagnostic("emptyOutput", output.Id));
                try
                {
                    result.FileName = output.NameMode == MkvSplitNameMode.Custom ? output.CustomFileName :
                        MkvSplitSegmentService.RenderOutputName(options, document.OriginMode, document.Source.FullPath,
                            projection.Outputs.Count, result.Clips.FirstOrDefault()?.SourceStartSeconds ?? 0,
                            result.Clips.LastOrDefault()?.SourceEndSeconds ?? 0, result.DurationSeconds, result.Chapters);
                    if (!IsValidOutputFileName(result.FileName))
                        throw new ArgumentException();
                    result.FullPath = Path.GetFullPath(Path.Combine(directory, result.FileName.Replace('\\', '/')));
                    if (string.Equals(result.FullPath, document.Source.FullPath, StringComparison.OrdinalIgnoreCase)
                        || !paths.Add(result.FullPath))
                        projection.Diagnostics.Add(Diagnostic("nameCollision", output.Id, null, result.FileName));
                    result.OutputState = File.Exists(result.FullPath) ? (options.Force ? MkvSplitOutputState.ExistsOverwrite : MkvSplitOutputState.ExistsSkip) : MkvSplitOutputState.New;
                }
                catch (ArgumentException)
                {
                    projection.Diagnostics.Add(Diagnostic("invalidName", output.Id));
                }
                projection.DurationSeconds += result.DurationSeconds;
                projection.ResultFrameCount += result.FrameCount;
            }
            int coveredEnd = 0;
            foreach ((int start, int end) range in coverage.OrderBy(range => range.start))
            {
                projection.CoveredSourceFrames += Math.Max(0, range.end - Math.Max(coveredEnd, range.start));
                coveredEnd = Math.Max(coveredEnd, range.end);
            }
            return projection;
        }

        /// <summary>
        /// Crea il documento della regola: il sorgente intero in manuale, un output per segmento del piano in batch.
        /// Il documento conserva l'identità indicata, cosi' sostituisce il draft della stessa sessione.
        /// </summary>
        /// <param name="legacy">Piano della regola batch, null in manuale</param>
        /// <param name="source">Identità del sorgente</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="documentId">Identità del documento da sostituire</param>
        /// <returns>Documento della regola</returns>
        public MkvSplitDocument CreateRuleDocument(MkvSplitPlan legacy, MkvSplitSourceIdentity source, MkvSplitAnalysis analysis, Guid documentId)
        {
            MkvSplitDocument document = legacy == null ? this.CreateFullSource(source, analysis) : this.FromLegacyPlan(legacy, source);
            document.Id = documentId;
            return document;
        }

        /// <summary>
        /// Verifica un nome file output: percorso MKV relativo, senza risalite e senza caratteri vietati
        /// </summary>
        /// <param name="fileName">Nome file da verificare</param>
        /// <returns>True se il nome è utilizzabile</returns>
        public static bool IsValidOutputFileName(string fileName)
        {
            string normalized = (fileName ?? "").Replace('\\', '/');
            return !string.IsNullOrWhiteSpace(normalized) && !Path.IsPathRooted(normalized)
                && !normalized.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."
                    || part.Any(character => char.IsControl(character) || ":*?\"<>|".Contains(character)))
                && normalized.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Secondi sorgente di un confine: il PTS del frame, oppure la durata del sorgente per il confine finale
        /// </summary>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="frame">Confine in frame, fino al numero di frame incluso</param>
        /// <returns>Secondi sorgente del confine</returns>
        public static double BoundarySeconds(MkvSplitAnalysis analysis, int frame)
        {
            return frame == analysis.SourcePts.Length ? analysis.Duration : analysis.SourcePts[frame];
        }

        /// <summary>
        /// Risolve un tempo sorgente nel frame che lo contiene
        /// </summary>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="sourceSeconds">Tempo sorgente in secondi</param>
        /// <returns>Risoluzione, non valida se il tempo è fuori dal sorgente</returns>
        public MkvSplitFrameResolution ResolveSource(MkvSplitAnalysis analysis, double sourceSeconds)
        {
            if (analysis?.SourcePts == null || analysis.SourcePts.Length == 0 || !double.IsFinite(sourceSeconds)
                || sourceSeconds < analysis.SourcePts[0] || sourceSeconds > analysis.Duration)
                return new MkvSplitFrameResolution();
            int frame = ContainingFrame(analysis.SourcePts, sourceSeconds);
            return new MkvSplitFrameResolution
            {
                IsValid = true,
                SourceFrame = frame,
                SourceSeconds = analysis.SourcePts[frame]
            };
        }

        /// <summary>
        /// Risolve un tempo del risultato di un output nel frame sorgente della clip che lo contiene
        /// </summary>
        /// <param name="projection">Proiezione del documento</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="outputId">Output di riferimento</param>
        /// <param name="resultSeconds">Tempo nel risultato in secondi</param>
        /// <returns>Risoluzione, non valida se il tempo è fuori dall'output</returns>
        public MkvSplitFrameResolution ResolveResult(MkvSplitTimelineProjection projection, MkvSplitAnalysis analysis, Guid outputId, double resultSeconds)
        {
            MkvSplitOutputProjection output = projection.Outputs.Find(item => item.OutputId == outputId);
            if (output == null || output.Clips.Count == 0 || !double.IsFinite(resultSeconds)
                || resultSeconds < 0 || resultSeconds > output.DurationSeconds)
                return new MkvSplitFrameResolution();
            MkvSplitClipProjection clip = output.Clips.Find(item => resultSeconds < item.ResultEndSeconds) ?? output.Clips.Last();
            // L'andata e ritorno risultato/sorgente in double puo' cadere un ulp sotto il PTS del frame:
            // la tolleranza e il limite inferiore della clip evitano di risolvere il frame precedente
            double sourceSeconds = clip.SourceStartSeconds + resultSeconds - clip.ResultStartSeconds;
            int frame = Math.Max(clip.StartFrame, Math.Min(clip.EndFrameExclusive - 1, ContainingFrame(analysis.SourcePts, sourceSeconds + RESOLVE_TOLERANCE_SECONDS)));
            return ResolveProjectedClipFrame(analysis, clip, frame, false);
        }

        /// <summary>
        /// Risolve un frame risultato senza conversione FPS o ricerca temporale approssimata.
        /// Con boundary=true ammette count: ultimo frame incluso, confine esclusivo e tempi di fine.
        /// Le giunzioni interne appartengono alla clip successiva.
        /// </summary>
        /// <param name="projection">Proiezione del documento</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="outputId">Output di riferimento</param>
        /// <param name="resultFrame">Frame nel risultato</param>
        /// <param name="boundary">Vero per risolvere un confine invece di un frame incluso</param>
        /// <returns>Risoluzione, non valida se il frame è fuori dall'output</returns>
        public MkvSplitFrameResolution ResolveResultFrame(MkvSplitTimelineProjection projection, MkvSplitAnalysis analysis,
            Guid outputId, int resultFrame, bool boundary = false)
        {
            MkvSplitOutputProjection output = FindResolvableOutput(projection, analysis, outputId);
            if (output == null || resultFrame < 0 || resultFrame > output.FrameCount
                || (!boundary && resultFrame == output.FrameCount))
                return new MkvSplitFrameResolution();
            if (resultFrame == output.FrameCount)
                return ResolveProjectedClipFrame(analysis, output.Clips.Last(), output.Clips.Last().EndFrameExclusive, true);
            MkvSplitClipProjection clip = output.Clips.Find(item => resultFrame >= item.ResultStartFrame
                && resultFrame - item.ResultStartFrame < item.FrameCount);
            return clip == null ? new MkvSplitFrameResolution() : ResolveProjectedClipFrame(analysis, clip,
                clip.StartFrame + (resultFrame - clip.ResultStartFrame), boundary);
        }

        /// <summary>
        /// Risolve il frame/confine sorgente di una specifica occorrenza.
        /// Il confine finale resta associato a clipId, non alla clip successiva del risultato.
        /// </summary>
        /// <param name="projection">Proiezione del documento</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="outputId">Output di riferimento</param>
        /// <param name="clipId">Occorrenza di riferimento</param>
        /// <param name="sourceFrame">Frame sorgente</param>
        /// <param name="boundary">Vero per risolvere un confine invece di un frame incluso</param>
        /// <returns>Risoluzione, non valida se il frame è fuori dall'occorrenza</returns>
        public MkvSplitFrameResolution ResolveClipSourceFrame(MkvSplitTimelineProjection projection, MkvSplitAnalysis analysis,
            Guid outputId, Guid clipId, int sourceFrame, bool boundary = false)
        {
            MkvSplitOutputProjection output = FindResolvableOutput(projection, analysis, outputId);
            MkvSplitClipProjection clip = output?.Clips.Find(item => item.ClipId == clipId);
            return clip == null ? new MkvSplitFrameResolution() : ResolveProjectedClipFrame(analysis, clip, sourceFrame, boundary);
        }

        /// <summary>
        /// Come ResolveResult, ma alla fine esatta dell'output risolve il confine finale invece dell'ultimo frame
        /// </summary>
        /// <param name="projection">Proiezione del documento</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="outputId">Output di riferimento</param>
        /// <param name="resultSeconds">Tempo nel risultato in secondi</param>
        /// <returns>Risoluzione del confine</returns>
        public MkvSplitFrameResolution ResolveResultBoundary(MkvSplitTimelineProjection projection, MkvSplitAnalysis analysis, Guid outputId, double resultSeconds)
        {
            MkvSplitFrameResolution result = this.ResolveResult(projection, analysis, outputId, resultSeconds);
            MkvSplitOutputProjection output = projection.Outputs.Find(item => item.OutputId == outputId);
            if (result.IsValid && resultSeconds == output.DurationSeconds)
            {
                result.ResultFrame = output.FrameCount;
                result.ResultSeconds = output.DurationSeconds;
            }
            return result;
        }

        /// <summary>
        /// Costruisce il piano eseguibile: proiezione, percorso veloce o ricodifica per ogni clip, tracce e sottotitoli
        /// </summary>
        /// <param name="document">Documento da eseguire</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="options">Opzioni Split</param>
        /// <returns>Piano con eventuali diagnostiche</returns>
        public MkvSplitExecutionPlan BuildExecutionPlan(MkvSplitDocument document, MkvSplitAnalysis analysis, MkvSplitOptions options)
        {
            MkvSplitExecutionPlan plan = new MkvSplitExecutionPlan
            {
                Document = document?.Clone(),
                Analysis = analysis,
                Projection = this.Project(document, analysis, options)
            };
            if (!plan.IsValid)
                return plan;
            if (analysis.PacketCount != analysis.SourcePts.Length || analysis.KeyFlags.Count != analysis.SourcePts.Length)
                plan.Projection.Diagnostics.Add(Diagnostic("invalidTimeline"));
            foreach (MkvSplitOutputProjection output in plan.Projection.Outputs)
            {
                MkvSplitExecutionOutput execution = new MkvSplitExecutionOutput
                {
                    OutputId = output.OutputId,
                    Projection = output
                };
                plan.Outputs.Add(execution);
                foreach (MkvSplitClipProjection clip in output.Clips)
                {
                    bool startKey = clip.StartFrame < analysis.KeyFlags.Count && analysis.KeyFlags[clip.StartFrame].Key;
                    bool endKey = clip.EndFrameExclusive == analysis.SourcePts.Length
                        || (clip.EndFrameExclusive < analysis.KeyFlags.Count && analysis.KeyFlags[clip.EndFrameExclusive].Key);
                    // Un keyframe di GOP aperto si taglia dallo slow path, che ricodifica solo i fotogrammi che lo attraversano
                    bool fast = startKey && endKey && !analysis.KeyFlags[clip.StartFrame].OpenGop
                        && (clip.EndFrameExclusive == analysis.SourcePts.Length || !analysis.KeyFlags[clip.EndFrameExclusive].OpenGop);
                    int reencode = 0;
                    if (!fast && !MkvSplitPipeline.TryParseCodec(analysis.VideoParams?.CodecName ?? "", out _))
                        plan.Projection.Diagnostics.Add(Diagnostic("unsupportedVideo", output.OutputId, clip.ClipId, analysis.VideoParams?.CodecName));
                    if (!startKey)
                    {
                        int nextKey = -1;
                        for (int frame = clip.StartFrame + 1; frame < Math.Min(clip.EndFrameExclusive, Math.Min(analysis.KeyFlags.Count, clip.StartFrame + MkvSplitExecutor.KEYFRAME_LOOKAHEAD)); frame++)
                        {
                            if (analysis.KeyFlags[frame].Key)
                            {
                                nextKey = frame;
                                break;
                            }
                        }
                        if (nextKey < 0)
                            plan.Projection.Diagnostics.Add(Diagnostic("shortClip", output.OutputId, clip.ClipId));
                        else
                            reencode = nextKey - clip.StartFrame;
                    }
                    execution.Clips.Add(new MkvSplitExecutionClip
                    {
                        UsesFastPath = fast,
                        Segment = new MkvSplitSegment
                        {
                            StartFrame = clip.StartFrame,
                            FrameCount = clip.FrameCount,
                            StartTs = clip.SourceStartSeconds,
                            EndTs = clip.SourceEndSeconds,
                            StartsOnKeyframe = startKey,
                            ReencodeFrames = reencode
                        }
                    });
                }
            }
            // La disponibilità dei tool/tracce è una proprietà del piano eseguibile, non della proiezione del draft.
            try
            {
                MkvFileInfo info = MkvSplitExternalTools.Instance.GetFileInfo(document.Source.FullPath);
                if (info == null)
                    throw new InvalidOperationException(AppText.T("split.montage.trackInventory"));
                plan.Tracks = info.Tracks;
                if (info.Tracks.Count(item => item.Type == "video") != 1 || info.Tracks.First(item => item.Type == "video").Id != 0)
                    plan.Projection.Diagnostics.Add(Diagnostic("videoTrackLayout"));
                List<Guid> unchangedOutputs = new List<Guid>();
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
                        unchangedOutputs.Add(output.OutputId);
                }
                List<MkvSplitOutputProjection> cutOutputs = plan.Projection.Outputs.Where(output => !unchangedOutputs.Contains(output.OutputId)).ToList();
                foreach (TrackInfo track in info.Tracks.Where(item => item.Type == "subtitles"))
                {
                    if (!MkvSplitSubtitleService.IsSupported(track))
                    {
                        foreach (MkvSplitExecutionOutput output in plan.Outputs.Where(output => unchangedOutputs.Contains(output.OutputId)))
                            output.NativeSubtitleTrackIds.Add(track.Id);
                        foreach (MkvSplitOutputProjection output in cutOutputs)
                            plan.Projection.Diagnostics.Add(Diagnostic("unsupportedSubtitle", output.OutputId, null, track.Id, track.Codec));
                    }
                }
                if (plan.IsValid && info.Tracks.Any(item => item.Type == "subtitles" && MkvSplitSubtitleService.IsSupported(item)))
                {
                    string temporary = Path.Combine(Path.GetTempPath(), "remuxforge-split-sub-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(temporary);
                    try
                    {
                        foreach (TrackInfo track in info.Tracks.Where(item => item.Type == "subtitles" && MkvSplitSubtitleService.IsSupported(item)))
                        {
                            bool bitmap = track.Codec == "S_HDMV/PGS" || track.Codec == "S_VOBSUB"
                                || track.Codec.Equals("HDMV PGS", StringComparison.OrdinalIgnoreCase) || track.Codec.Equals("VobSub", StringComparison.OrdinalIgnoreCase);
                            if (bitmap)
                            {
                                foreach (MkvSplitExecutionOutput output in plan.Outputs.Where(output => unchangedOutputs.Contains(output.OutputId)))
                                    output.NativeSubtitleTrackIds.Add(track.Id);
                                if (cutOutputs.Count == 0)
                                    continue;
                            }
                            MkvSplitSubtitleTrack subtitle = MkvSplitSubtitleService.ReadSource(document.Source.FullPath, track, temporary, analysis.Duration);
                            plan.Subtitles.Add(subtitle);
                            // Una traccia sorgente senza eventi resta vuota in ogni taglio: si copia nativa, senza riscriverla
                            if (subtitle.Events.Count == 0)
                            {
                                foreach (MkvSplitExecutionOutput output in plan.Outputs.Where(output => !output.NativeSubtitleTrackIds.Contains(track.Id)))
                                    output.NativeSubtitleTrackIds.Add(track.Id);
                                continue;
                            }
                            bool fitsVideo = subtitle.Events.All(item => item.StartSeconds >= analysis.SourcePts[0] && item.EndSeconds <= analysis.Duration);
                            if (fitsVideo && !bitmap)
                            {
                                foreach (MkvSplitExecutionOutput output in plan.Outputs.Where(output => unchangedOutputs.Contains(output.OutputId)))
                                    output.NativeSubtitleTrackIds.Add(track.Id);
                            }
                            foreach (MkvSplitOutputProjection output in plan.Projection.Outputs.Where(output => !plan.Outputs.Find(item => item.OutputId == output.OutputId).NativeSubtitleTrackIds.Contains(track.Id)))
                            {
                                foreach (MkvSplitClipProjection clip in output.Clips)
                                {
                                    foreach (MkvSplitSubtitleEvent item in subtitle.Events.Where(item => item.HasTimedEffects))
                                    {
                                        if (item.StartSeconds < clip.SourceEndSeconds && item.EndSeconds > clip.SourceStartSeconds
                                            && (item.StartSeconds < clip.SourceStartSeconds || item.EndSeconds > clip.SourceEndSeconds))
                                            plan.Projection.Diagnostics.Add(Diagnostic("subtitleTimedBoundary", output.OutputId, clip.ClipId,
                                                track.Id, clip.SourceStartSeconds, clip.SourceEndSeconds));
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        Directory.Delete(temporary, true);
                    }
                }
            }
            catch (Exception exception)
            {
                plan.Projection.Diagnostics.Add(new MkvSplitDiagnostic
                {
                    Code = "trackInventory",
                    Message = exception.Message,
                    Severity = MkvSplitDiagnosticSeverity.Error
                });
            }
            return plan;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Ricerca binaria dell'ultimo frame con PTS minore o uguale al tempo indicato
        /// </summary>
        /// <param name="pts">PTS del sorgente ordinati crescente</param>
        /// <param name="seconds">Tempo in secondi</param>
        /// <returns>Indice del frame che contiene il tempo, almeno 0</returns>
        private static int ContainingFrame(double[] pts, double seconds)
        {
            int low = 0;
            int high = pts.Length;
            while (low < high)
            {
                int mid = low + ((high - low) >> 1);
                if (pts[mid] <= seconds)
                    low = mid + 1;
                else
                    high = mid;
            }
            return Math.Max(0, low - 1);
        }

        /// <summary>
        /// Una proiezione draft può avere altri output vuoti o problemi di naming: validare la geometria
        /// del solo output richiesto, senza trasformare la preview in una validazione di esportazione.
        /// </summary>
        /// <param name="projection">Proiezione del documento</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="outputId">Output richiesto</param>
        /// <returns>Output con geometria coerente, altrimenti null</returns>
        private static MkvSplitOutputProjection FindResolvableOutput(MkvSplitTimelineProjection projection, MkvSplitAnalysis analysis, Guid outputId)
        {
            if (projection?.Outputs == null || analysis?.SourcePts == null || analysis.SourcePts.Length == 0
                || !double.IsFinite(analysis.Duration) || analysis.Duration <= analysis.SourcePts.Last() || outputId == Guid.Empty)
                return null;
            MkvSplitOutputProjection output = projection.Outputs.Find(item => item != null && item.OutputId == outputId);
            if (output?.Clips == null || output.Clips.Count == 0)
                return null;
            long count = 0;
            double duration = 0;
            HashSet<Guid> ids = new HashSet<Guid>();
            foreach (MkvSplitClipProjection clip in output.Clips)
            {
                if (clip == null || clip.ClipId == Guid.Empty || !ids.Add(clip.ClipId) || clip.StartFrame < 0
                    || clip.EndFrameExclusive <= clip.StartFrame || clip.EndFrameExclusive > analysis.SourcePts.Length
                    || clip.ResultStartFrame != count || clip.ResultStartSeconds != duration)
                    return null;
                double start = analysis.SourcePts[clip.StartFrame];
                double end = BoundarySeconds(analysis, clip.EndFrameExclusive);
                if (!double.IsFinite(start) || start < 0 || !double.IsFinite(end) || end <= start
                    || clip.SourceStartSeconds != start || clip.SourceEndSeconds != end || clip.DurationSeconds != end - start)
                    return null;
                count += clip.FrameCount;
                duration += clip.DurationSeconds;
            }
            return count == output.FrameCount && count <= int.MaxValue && double.IsFinite(duration)
                && duration == output.DurationSeconds ? output : null;
        }

        /// <summary>
        /// Risolve un frame o confine sorgente interno a una clip proiettata nelle coordinate sorgente e risultato
        /// </summary>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="clip">Clip proiettata</param>
        /// <param name="sourceFrame">Frame sorgente</param>
        /// <param name="boundary">Vero per risolvere un confine invece di un frame incluso</param>
        /// <returns>Risoluzione, non valida se il frame è fuori dalla clip</returns>
        private static MkvSplitFrameResolution ResolveProjectedClipFrame(MkvSplitAnalysis analysis,
            MkvSplitClipProjection clip, int sourceFrame, bool boundary)
        {
            if (sourceFrame < clip.StartFrame || sourceFrame > clip.EndFrameExclusive
                || (!boundary && sourceFrame == clip.EndFrameExclusive))
                return new MkvSplitFrameResolution();
            bool end = sourceFrame == clip.EndFrameExclusive;
            int included = end ? sourceFrame - 1 : sourceFrame;
            double seconds = boundary ? BoundarySeconds(analysis, sourceFrame) : analysis.SourcePts[included];
            if (!double.IsFinite(seconds) || seconds < clip.SourceStartSeconds || seconds > clip.SourceEndSeconds)
                return new MkvSplitFrameResolution();
            return new MkvSplitFrameResolution
            {
                IsValid = true,
                ClipId = clip.ClipId,
                SourceFrame = included,
                ResultFrame = clip.ResultStartFrame + (sourceFrame - clip.StartFrame),
                SourceSeconds = seconds,
                ResultSeconds = end ? clip.ResultEndSeconds : clip.ResultStartSeconds + seconds - clip.SourceStartSeconds
            };
        }

        #endregion
    }
}
