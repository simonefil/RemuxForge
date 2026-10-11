using RemuxForge.Core.Chapters;
using RemuxForge.Core.Infrastructure;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Cutters;
using RemuxForge.Core.Splitting.Planning;

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
        /// Converte un piano legacy in documento: un output con una sola clip per ogni segmento, con i confini chiesti prima dello snap
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
                    Clips = new List<MkvSplitClip> { segment.ToClip() }
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
            string directory = options.ResolveOutputDir(document.Source.FullPath);
            // {source} si risolve una volta: la disambiguazione D21 legge la cartella del sorgente
            string sourceName = MkvSplitSegmentService.ResolveSourceName(document.Source.FullPath);
            if (document.Outputs.Count == 0)
                projection.Diagnostics.Add(Diagnostic("noOutputs"));
            foreach (MkvSplitOutput output in document.Outputs)
            {
                MkvSplitOutputProjection result = new MkvSplitOutputProjection { OutputId = output.Id };
                List<ChapterRange> chapterRanges = new List<ChapterRange>();
                List<List<ChapterMark>> chapterSets = new List<List<ChapterMark>>();
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
                    // Confini della clip che lo snap della regola ha spostato: i capitoli fra il confine chiesto e quello effettivo lo seguono,
                    // come nel piano della regola. Un confine che l'utente ha poi spostato non è più della regola
                    chapterSets.Add(analysis.SelectChapters(clip, start, new[] { clip }));
                    result.DurationSeconds += end - start;
                    result.FrameCount += clip.EndFrameExclusive - clip.StartFrame;
                    coverage.Add((clip.StartFrame, clip.EndFrameExclusive));
                }
                // Capitoli del risultato: quelli che marcano un fotogramma di ogni clip, nella sua posizione, con la fine troncata alla clip
                result.Chapters = ChapterTimelineService.Montage(chapterSets, chapterRanges);
                if (output.Clips.Count == 0)
                    projection.Diagnostics.Add(Diagnostic("emptyOutput", output.Id));
                try
                {
                    result.FileName = output.NameMode == MkvSplitNameMode.Custom ? output.CustomFileName :
                        MkvSplitSegmentService.RenderOutputName(options, document.OriginMode, sourceName,
                            projection.Outputs.Count, result.Clips.FirstOrDefault()?.SourceStartSeconds ?? 0,
                            result.Clips.LastOrDefault()?.SourceEndSeconds ?? 0, result.DurationSeconds, result.Chapters);
                    if (!IsValidOutputFileName(result.FileName))
                        throw new ArgumentException();
                    result.FullPath = Path.GetFullPath(Path.Combine(directory, result.FileName.Replace('\\', '/')));
                    if (FileHelper.IsSamePath(result.FullPath, document.Source.FullPath)
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
        /// Piano della regola corrente e documento che ne deriva: lo stesso per CLI, analisi Web ed editor. In manuale non c'è
        /// piano e il documento è il sorgente intero; con un piano non valido il documento è null e la validità la giudica il chiamante
        /// </summary>
        /// <param name="options">Opzioni Split già clonate per il file</param>
        /// <param name="source">Identità del sorgente</param>
        /// <param name="analysis">Analisi del sorgente</param>
        /// <param name="documentId">Identità del documento</param>
        /// <returns>Piano della regola, null in manuale, e documento, null se il piano non è valido</returns>
        public MkvSplitRulePlan PlanRule(MkvSplitOptions options, MkvSplitSourceIdentity source, MkvSplitAnalysis analysis, Guid documentId)
        {
            MkvSplitPlan legacy = options.Manual ? null : new MkvSplitPlanner().BuildPlan(options, source.FullPath, null);
            return new MkvSplitRulePlan(legacy, legacy == null || legacy.IsValid ? this.CreateRuleDocument(legacy, source, analysis, documentId) : null);
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
            int frame = analysis.ContainingFrame(sourceSeconds);
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
            int frame = Math.Max(clip.StartFrame, Math.Min(clip.EndFrameExclusive - 1, analysis.ContainingFrame(sourceSeconds + RESOLVE_TOLERANCE_SECONDS)));
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
            // Senza modalità del frame rate lo snap non è sicuro, con o senza ricodifica: quello della regola e quello che l'editor
            // applica ai confini di un documento manuale (decisione 5 di §13)
            if (options.Snap != MkvSplitSnapMode.Off && analysis.FrameRateMode == MkvSplitFrameRateMode.Unknown)
                plan.Projection.Diagnostics.Add(new MkvSplitDiagnostic
                {
                    Code = "frameRateUnknown",
                    Message = AppText.T("split.plan.frameRateUnknown"),
                    Severity = MkvSplitDiagnosticSeverity.Error
                });
            // La testa da ricodificare la dà la strategia del codec; senza strategia il costo segue la regola del taglio a byte
            MkvSplitVideoProfile profile = analysis.VideoProfile;
            MkvSplitExactCutterBase cutter = profile.ExactCutCodec?.CreateCutter();
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
                    MkvSplitClipCutDecision decision = new MkvSplitClipCutDecision(analysis, profile, cutter, plan.Projection, output, clip);
                    plan.Projection.Diagnostics.AddRange(decision.Diagnostics);
                    execution.Clips.Add(decision.ToExecutionClip());
                }
            }
            // Qualità e velocità valgono per tutte le clip ricodificate: stessi range del planner CLI
            if (plan.Outputs.Any(output => output.Clips.Any(clip => !clip.UsesFastPath))
                && profile.IsExactCutAvailable)
            {
                foreach (string error in MkvSplitCodecCapabilities.ValidateEncodeSettings(profile.ExactCutCodec, options.EncodeQuality, options.EncodeSpeed))
                    plan.Projection.Diagnostics.Add(new MkvSplitDiagnostic { Code = "encodeSettings", Message = error, Severity = MkvSplitDiagnosticSeverity.Error });
            }
            // La disponibilità delle tracce è una proprietà del piano eseguibile, non della proiezione del draft: inventario ed
            // eventi dei sottotitoli li ha letti l'analisi, il piano non rilegge il Matroska
            if (analysis.TrackInventoryError.Length > 0)
            {
                plan.Projection.Diagnostics.Add(new MkvSplitDiagnostic
                {
                    Code = "trackInventory",
                    Message = analysis.TrackInventoryError,
                    Severity = MkvSplitDiagnosticSeverity.Error
                });
                return plan;
            }
            plan.Tracks = analysis.Tracks;
            // Il video principale è la traccia 0: conteggio dei pacchetti e timestamps_v2 la leggono per ID. Le altre tracce video
            // (copertine MJPEG e simili) seguono le clip come l'audio
            TrackInfo mainVideo = analysis.Tracks.FirstOrDefault(item => item.Type == "video");
            if (mainVideo == null || mainVideo.Id != 0)
                plan.Projection.Diagnostics.Add(Diagnostic("videoTrackLayout"));
            new MkvSplitSubtitleRouter(plan, analysis).Route();
            return plan;
        }

        #endregion

        #region Metodi privati

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
