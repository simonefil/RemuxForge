using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Media;
using RemuxForge.Core.Splitting.Execution;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

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
        /// <param name="cancellationToken">Interruzione: ferma il file in corso come lo Stop del Web e salta i successivi</param>
        /// <returns>Exit code 0/1</returns>
        public int Execute(Options options, CancellationToken cancellationToken = default)
        {
            int result = 0;
            List<string> files;
            MkvSplitExecutionResult fileResult;

            MkvSplitExternalTools.Instance.ResolveBinaries();
            files = MkvSplitOptions.ResolveInputFiles(options.Split.SourcePath, options.Recursive);
            if (files.Count == 0)
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.T("split.noMkvFiles"));
                return 1;
            }
            // Un batch parte solo con lo stesso codec e lo stesso tipo di scansione in tutti i file
            if (files.Count > 1)
            {
                string mismatch = MkvSplitVideoProfile.CheckBatch(files).Mismatch;
                if (mismatch.Length > 0)
                {
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Error, mismatch);
                    return 1;
                }
            }

            for (int i = 0; i < files.Count && !cancellationToken.IsCancellationRequested; i++)
            {
                fileResult = this.ExecuteFileInternal(options, files[i], cancellationToken);
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
        /// Esegue la pipeline split su un singolo file già risolto senza rieseguire setup tool: la regola diventa il documento
        /// e il piano eseguibile dell'interfaccia Web, eseguiti dallo stesso ExecutePlan
        /// </summary>
        /// <param name="options">Opzioni globali</param>
        /// <param name="inputFile">File da dividere</param>
        /// <param name="cancellationToken">Interruzione dell'esecuzione</param>
        private MkvSplitExecutionResult ExecuteFileInternal(Options options, string inputFile, CancellationToken cancellationToken)
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
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.plan.invalid", AppText.T("split.plan.manualUndefined")));
                    return result;
                }
                // Il lease tiene il temporaneo dall'analisi all'esecuzione del file: una sola normalizzazione, poi sparisce
                using (NormalizedMediaLease lease = NormalizedMediaStore.Instance.Acquire(inputFile, true))
                {
                    MkvSplitRulePlan rule = service.PlanRule(splitOptions, MkvSplitSourceIdentity.FromFile(inputFile), null, Guid.NewGuid());
                    MkvSplitPlan legacy = rule.Plan;
                    MkvSplitPlanner planner = new MkvSplitPlanner();
                    if (!legacy.IsValid)
                    {
                        result.ExitCode = 1;
                        ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.F("split.input", legacy.InputFile));
                        planner.PrintPlan(legacy);
                        ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.plan.invalid", legacy.ErrorMessage));
                        return result;
                    }
                    // Codec, GOP aperto, frame rate e tracce li giudica il piano eseguibile: ExecutePlan dice perché non parte. Il piano
                    // della regola si stampa dopo l'intestazione dell'input
                    MkvSplitExecutionPlan plan = service.BuildExecutionPlan(rule.Document, MkvSplitAnalysisCache.Instance.GetOrBuild(inputFile), splitOptions);
                    result.ExitCode = this.ExecutePlan(plan, splitOptions, cancellationToken, () => planner.PrintPlan(legacy)).ExitCode;
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = 1;
                // Un processo fermato dall'interruzione non è un errore da riportare
                if (!cancellationToken.IsCancellationRequested)
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
        /// <param name="cancellationToken">Annullamento controllato tra gli stadi e prima della pubblicazione; non arriva ai processi
        /// in corso, che interrompe l'infrastruttura ProcessRunner collegata allo stop dal chiamante Web</param>
        /// <param name="printPlan">Stampa del piano della regola subito dopo l'intestazione dell'input (CLI); null se non c'è</param>
        /// <returns>Esito per output, con exit code 1 se un output è fallito o il piano non è eseguibile</returns>
        public MkvSplitMontageExecutionResult ExecutePlan(MkvSplitExecutionPlan plan, MkvSplitOptions args, CancellationToken cancellationToken = default, Action printPlan = null)
        {
            MkvSplitMontageExecutionResult result = new MkvSplitMontageExecutionResult();
            MkvSplitSourceIdentity source = plan.Document.Source;
            string outputDir = plan.Outputs.Count > 0 ? Path.GetDirectoryName(plan.Outputs[0].Projection.FullPath) : "";
            bool hasFlac = plan.Tracks.Exists(MkvSplitExternalTools.IsFlacAudio);
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
            using (MkvSplitPlanMedia media = new MkvSplitPlanMedia())
            {
                try
                {
                    if (running.Count > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        media.Prepare(plan, args, running);
                    }
                    MkvSplitClipProducer producer = new MkvSplitClipProducer(plan, media);
                    foreach (MkvSplitExecutionOutput output in plan.Outputs)
                    {
                        MkvSplitOutputExecutionResult state = result.Outputs.Find(item => item.OutputId == output.OutputId);
                        string temporary = null;
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            ConsoleHelper.Write(LogSection.Split, LogLevel.Phase, AppText.F("split.segment", plan.Outputs.IndexOf(output) + 1, output.Projection.FileName));
                            if (!source.Matches(MkvSplitSourceIdentity.FromFile(source.FullPath)))
                                throw new InvalidOperationException(AppText.T("split.montage.sourceChanged"));
                            string path = output.Projection.FullPath;
                            if (FileHelper.IsSamePath(path, source.FullPath))
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
                            bool separateFlac = hasFlac && output.Clips.Count > 1;
                            List<string> clips = producer.Produce(output, temporary, new MkvSplitJunctionAudioTrim(sourceAudio, separateFlac), cancellationToken);
                            cancellationToken.ThrowIfCancellationRequested();
                            MkvSplitOutputAssembler assembler = new MkvSplitOutputAssembler(plan, output, media.MediaPath, temporary, separateFlac, sourceAudio);
                            MkvSplitOpenGopTimeline timeline = new MkvSplitOpenGopTimeline(output);
                            string final = assembler.Mux(assembler.Append(clips), timeline, cancellationToken);
                            // Pubblicazione: solo un file completo, con il video della proiezione e il sorgente invariato
                            timeline.Validate(final, plan.Analysis);
                            if (!source.Matches(MkvSplitSourceIdentity.FromFile(source.FullPath)))
                                throw new InvalidOperationException(AppText.T("split.montage.sourceChanged"));
                            cancellationToken.ThrowIfCancellationRequested();
                            File.Move(final, path, args.Force);
                            state.Status = MkvSplitOutputExecutionStatus.Done;
                        }
                        catch (Exception exception)
                        {
                            if (exception is OperationCanceledException || cancellationToken.IsCancellationRequested)
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
                    bool cancelled = exception is OperationCanceledException || cancellationToken.IsCancellationRequested;
                    result.ClosePending(cancelled, exception.Message);
                    if (!cancelled)
                        ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.errorLine", exception.Message));
                }
            }
            result.WriteSummary(plan, outputDir);
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

        #endregion
    }
}
