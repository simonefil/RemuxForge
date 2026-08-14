using RemuxForge.Core.Analysis.Deep.Features;
using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media.Ffmpeg;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Raffina i gap del percorso globale al frame e costruisce una EditMap completa
    /// </summary>
    public sealed class DeepSiftEditMapBuilder
    {
        #region Variabili di classe

        /// <summary>
        /// Risolve i percorsi e i regimi nelle regioni candidate
        /// </summary>
        private readonly DeepSiftLocalTopologyResolver _localTopologyResolver;

        /// <summary>
        /// Sezione usata per la diagnostica della fase EditMap
        /// </summary>
        private readonly LogSection _logSection;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore con backend batch SIFT e preprocess locale condiviso
        /// </summary>
        /// <param name="ffmpegPath">Percorso dell'eseguibile FFmpeg</param>
        /// <param name="ffmpegConfig">Configurazione di estrazione FFmpeg</param>
        /// <param name="videoSyncConfig">Configurazione della sincronizzazione video</param>
        /// <param name="logSection">Sezione usata per la diagnostica</param>
        /// <param name="batchMatcher">Backend batch SIFT</param>
        /// <param name="geometryCropResolver">Resolver crop geometrico per file</param>
        /// <param name="frameNormalizer">Normalizzatore bordi neri sui frame estratti</param>
        /// <param name="maximumParallelism">Parallelismo massimo consentito</param>
        public DeepSiftEditMapBuilder(string ffmpegPath, FfmpegConfig ffmpegConfig, VideoSyncConfig videoSyncConfig, LogSection logSection, FrameFeatureBatchMatcherBase batchMatcher, Func<string, bool> geometryCropResolver, Action<string, bool, string, List<byte[]>> frameNormalizer, int maximumParallelism)
        {
            if (string.IsNullOrEmpty(ffmpegPath))
                throw new ArgumentException(AppText.T("deep.temporal.argument.missingFfmpegPath"), nameof(ffmpegPath));
            if (ffmpegConfig == null)
                throw new ArgumentNullException(nameof(ffmpegConfig));
            if (maximumParallelism < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumParallelism));
            if (videoSyncConfig == null)
                throw new ArgumentNullException(nameof(videoSyncConfig));
            if (batchMatcher == null)
                throw new ArgumentNullException(nameof(batchMatcher));
            if (geometryCropResolver == null)
                throw new ArgumentNullException(nameof(geometryCropResolver));
            if (frameNormalizer == null)
                throw new ArgumentNullException(nameof(frameNormalizer));
            this._logSection = logSection;
            this._localTopologyResolver = new DeepSiftLocalTopologyResolver(ffmpegPath, ffmpegConfig, videoSyncConfig, logSection, batchMatcher, geometryCropResolver, frameNormalizer, maximumParallelism);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce la EditMap dal percorso canonico locale e rifinisce i boundary con le black run disponibili
        /// </summary>
        /// <param name="sourcePath">Percorso del file source</param>
        /// <param name="languagePath">Percorso del file language</param>
        /// <param name="sourceCropPx">Crop manuale applicato al file source</param>
        /// <param name="languageCropPx">Crop manuale applicato al file language</param>
        /// <param name="stretchFactor">Fattore di stretch serializzato</param>
        /// <param name="sourceToLanguageScale">Scala temporale dal source al language</param>
        /// <param name="sourceTimeline">Timeline delle ancore source</param>
        /// <param name="languageTimeline">Timeline delle ancore language</param>
        /// <param name="temporal">Evidenza temporale da convertire in EditMap</param>
        /// <param name="cancellationToken">Token per l'annullamento cooperativo dell'elaborazione</param>
        /// <returns>Risultato della costruzione e della validazione della EditMap</returns>
        public DeepSiftEditMapResult BuildTemporal(string sourcePath, string languagePath, string sourceCropPx, string languageCropPx, string stretchFactor, double sourceToLanguageScale, DeepSiftAnchorTimeline sourceTimeline, DeepSiftAnchorTimeline languageTimeline, DeepSiftTemporalEvidenceResult temporal, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sourcePath))
                throw new ArgumentException(AppText.T("deep.temporal.argument.missingSourcePath"), nameof(sourcePath));
            if (string.IsNullOrEmpty(languagePath))
                throw new ArgumentException(AppText.T("deep.temporal.argument.missingLanguagePath"), nameof(languagePath));
            if (sourceTimeline == null)
                throw new ArgumentNullException(nameof(sourceTimeline));
            if (languageTimeline == null)
                throw new ArgumentNullException(nameof(languageTimeline));
            if (temporal == null || !temporal.Accepted || temporal.SupportRuns.Count == 0)
                throw new ArgumentException(AppText.T("deep.temporal.argument.unacceptedTemporalResult"), nameof(temporal));
            if (sourceToLanguageScale <= 0.0 || double.IsNaN(sourceToLanguageScale) || double.IsInfinity(sourceToLanguageScale))
                throw new ArgumentOutOfRangeException(nameof(sourceToLanguageScale));
            if (string.IsNullOrEmpty(stretchFactor))
            {
                if (Math.Abs(sourceToLanguageScale - 1.0) > 0.000000001)
                    throw new ArgumentException(AppText.T("deep.temporal.argument.emptyStretchRequiresIdentity"), nameof(stretchFactor));
            }
            else
            {
                if (!SpeedCorrectionService.TryParseStretchFactor(stretchFactor, out double stretchRatio, out _) ||
                    Math.Abs((1.0 / stretchRatio) - sourceToLanguageScale) > 0.000000001)
                    throw new ArgumentException(AppText.T("deep.temporal.argument.inconsistentStretchScale"), nameof(stretchFactor));
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            DeepSiftEditMapResult result = new DeepSiftEditMapResult();
            result.EditMap.StretchFactor = stretchFactor ?? "";
            ConsoleHelper.Write(this._logSection, LogLevel.Phase, AppText.T("deep.temporal.log.phaseLocalRegions"));
            ConsoleHelper.Progress(this._logSection, 68, AppText.T("deep.temporal.progress.localRegions"));
            this._localTopologyResolver.Resolve(sourcePath, languagePath, sourceCropPx, languageCropPx, sourceToLanguageScale, this.GetTimelineEndMs(sourceTimeline), this.GetTimelineEndMs(languageTimeline), sourceTimeline.BlackRuns, languageTimeline.BlackRuns, temporal, cancellationToken);
            if (!temporal.Accepted)
            {
                result.RejectReason = temporal.RejectReason;
                result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
                return result;
            }
            ConsoleHelper.Write(this._logSection, LogLevel.Phase, AppText.T("deep.temporal.log.phaseFrameBoundaries"));
            ConsoleHelper.Progress(this._logSection, 82, AppText.T("deep.temporal.progress.frameBoundaries"));
            List<ResolvedTransition> transitions = this.CollectCanonicalTransitions(temporal, result);
            if (!string.IsNullOrEmpty(result.RejectReason))
            {
                result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            if (transitions.Count == 0)
            {
                double canonicalOffsetMs = temporal.SupportRuns.Count > 0 ? this.SelectCanonicalSupportOffset(temporal.SupportRuns[0]) : temporal.ResolvedRegimes[0].OffsetMs;
                this.AddLocalPlateau(result, temporal.ResolvedRegimes[0], canonicalOffsetMs);
                result.EditMap.InitialDelayMs = (int)Math.Round(canonicalOffsetMs);
            }
            else
            {
                this.AddLocalPlateau(result, transitions[0].BeforeRegime, transitions[0].PreviousOffsetMs);
                for (int transitionIndex = 0; transitionIndex < transitions.Count; transitionIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ResolvedTransition resolved = transitions[transitionIndex];
                    Stopwatch boundaryStopwatch = Stopwatch.StartNew();
                    DeepSiftBoundaryResult boundary = this.BuildBoundary(resolved, sourceToLanguageScale, resolved.Region.SourceBlackRuns, resolved.Region.LanguageBlackRuns);
                    if (resolved.Region != null)
                        resolved.Region.BoundaryRefinementMs += boundaryStopwatch.ElapsedMilliseconds;
                    result.Boundaries.Add(boundary);
                    this.AddLocalPlateau(result, resolved.AfterRegime, boundary.NextOffsetMs);
                    EditOperation operation = this.BuildOperation(boundary, sourceToLanguageScale);
                    if (operation.DurationMs <= 0)
                    {
                        result.RejectReason = AppText.T("deep.temporal.editMap.nonPositiveOperationDuration");
                        result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
                        return result;
                    }
                    boundary.Operation = operation;
                    result.EditMap.Operations.Add(operation);
                }
                result.EditMap.InitialDelayMs = (int)Math.Round(result.Plateaus[0].OffsetMs);
            }

            for (int regionIndex = 0; regionIndex < temporal.CandidateRegions.Count; regionIndex++)
            {
                DeepSiftTemporalCandidateRegion region = temporal.CandidateRegions[regionIndex];
                result.LocalSourceExtractionMs += region.SourceExtractionMs;
                result.LocalLanguageExtractionMs += region.LanguageExtractionMs;
                result.LocalMatchingMs += region.MatchingMs;
                result.GapCount += region.GapCount;
            }
            result.OperationCount = result.EditMap.Operations.Count;
            if (!this.ValidateEditMap(result, sourceToLanguageScale, sourceTimeline, languageTimeline, out string validationReason))
            {
                List<string> operationDiagnostics = new List<string>(result.EditMap.Operations.Count);
                for (int operationIndex = 0; operationIndex < result.EditMap.Operations.Count; operationIndex++)
                {
                    EditOperation operation = result.EditMap.Operations[operationIndex];
                    operationDiagnostics.Add(operation.Type + "@s" + operation.SourceTimestampMs.ToString(CultureInfo.InvariantCulture) + "/l" + operation.LangTimestampMs.ToString(CultureInfo.InvariantCulture) + "/d" + operation.DurationMs.ToString(CultureInfo.InvariantCulture));
                }
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, AppText.F("deep.temporal.log.editMapRejected", string.Join(", ", operationDiagnostics)));
                result.EditMap.Operations.Clear();
                result.OperationCount = 0;
                result.RejectReason = validationReason;
                result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
                return result;
            }
            if (!this.ReplayLocalPaths(temporal, result, sourceToLanguageScale, out string replayReason))
            {
                result.EditMap.Operations.Clear();
                result.OperationCount = 0;
                result.RejectReason = replayReason;
                result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            result.Success = true;
            result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// Associa le transizioni canoniche alle regioni locali che ne dimostrano i boundary
        /// </summary>
        /// <param name="temporal">Evidenza temporale con transizioni e regioni locali risolte</param>
        /// <param name="result">Risultato da aggiornare in caso di rifiuto del percorso</param>
        /// <returns>Transizioni canoniche associate ai rispettivi regimi locali</returns>
        private List<ResolvedTransition> CollectCanonicalTransitions(DeepSiftTemporalEvidenceResult temporal, DeepSiftEditMapResult result)
        {
            List<ResolvedTransition> transitions = new List<ResolvedTransition>();
            for (int transitionIndex = 0; transitionIndex < temporal.ResolvedTransitions.Count; transitionIndex++)
            {
                DeepSiftLocalTransition transition = temporal.ResolvedTransitions[transitionIndex];
                ResolvedTransition resolved = new ResolvedTransition();
                if (!this.TryFindCandidateTransition(temporal.CandidateRegions, transition, out DeepSiftTemporalCandidateRegion region, out DeepSiftLocalTransition localTransition))
                {
                    result.RejectReason = AppText.T("deep.temporal.editMap.transitionWithoutResolvedRegion");
                    return new List<ResolvedTransition>();
                }
                resolved.Region = region;
                resolved.Transition = localTransition;
                resolved.BeforeRegime = resolved.Region.Regimes[resolved.Transition.BeforeRegimeIndex];
                resolved.AfterRegime = resolved.Region.Regimes[resolved.Transition.AfterRegimeIndex];
                resolved.PreviousOffsetMs = temporal.ResolvedRegimes[transition.BeforeRegimeIndex].OffsetMs;
                resolved.NextOffsetMs = temporal.ResolvedRegimes[transition.AfterRegimeIndex].OffsetMs;
                transitions.Add(resolved);
            }
            if (temporal.ResolvedRegimes.Count == 0)
                result.RejectReason = AppText.T("deep.temporal.editMap.pathWithoutObservableRegimes");
            else if (transitions.Count + 1 != temporal.ResolvedRegimes.Count)
                result.RejectReason = AppText.T("deep.temporal.editMap.inconsistentRegimesAndTransitions");
            return transitions;
        }

        /// <summary>
        /// Ritrova nella regione proprietaria una transizione della topologia canonica
        /// </summary>
        /// <param name="regions">Regioni locali risolte</param>
        /// <param name="canonical">Transizione della topologia aggregata</param>
        /// <param name="resolvedRegion">Regione proprietaria della transizione</param>
        /// <param name="resolvedTransition">Transizione locale corrispondente</param>
        /// <returns>True quando tutti i boundary coincidono</returns>
        private bool TryFindCandidateTransition(IReadOnlyList<DeepSiftTemporalCandidateRegion> regions, DeepSiftLocalTransition canonical, out DeepSiftTemporalCandidateRegion resolvedRegion, out DeepSiftLocalTransition resolvedTransition)
        {
            resolvedRegion = null;
            resolvedTransition = null;
            for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
            {
                DeepSiftTemporalCandidateRegion region = regions[regionIndex];
                if (region.State != DeepSiftCandidateRegionState.ResolvedTransitions)
                    continue;
                for (int transitionIndex = 0; transitionIndex < region.Transitions.Count; transitionIndex++)
                {
                    DeepSiftLocalTransition candidate = region.Transitions[transitionIndex];
                    bool sameBoundary = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.LastBeforeSourcePtsMs) == DeepSiftTemporalMetricComparer.QuantizeMilliseconds(canonical.LastBeforeSourcePtsMs) &&
                                        DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.FirstAfterSourcePtsMs) == DeepSiftTemporalMetricComparer.QuantizeMilliseconds(canonical.FirstAfterSourcePtsMs) &&
                                        DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.LastBeforeLanguagePtsMs) == DeepSiftTemporalMetricComparer.QuantizeMilliseconds(canonical.LastBeforeLanguagePtsMs) &&
                                        DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.FirstAfterLanguagePtsMs) == DeepSiftTemporalMetricComparer.QuantizeMilliseconds(canonical.FirstAfterLanguagePtsMs);
                    if (!sameBoundary)
                        continue;
                    resolvedRegion = region;
                    resolvedTransition = candidate;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Converte un regime locale in un plateau diagnostico canonico
        /// </summary>
        /// <param name="result">Risultato EditMap da aggiornare</param>
        /// <param name="regime">Regime locale risolto</param>
        /// <param name="canonicalOffsetMs">Offset vincolato al supporto globale</param>
        private void AddLocalPlateau(DeepSiftEditMapResult result, DeepSiftLocalRegime regime, double canonicalOffsetMs)
        {
            DeepSiftPlateauDiagnostic plateau = new DeepSiftPlateauDiagnostic();
            plateau.FirstMatchIndex = regime.FirstPathIndex;
            plateau.LastMatchIndex = regime.LastPathIndex;
            plateau.OffsetMs = canonicalOffsetMs;
            plateau.OffsetDispersionMs = regime.UncertaintyMs;
            plateau.SourceStartPtsMs = regime.SourceStartPtsMs;
            plateau.SourceEndPtsMs = regime.SourceEndPtsMs;
            plateau.MatchCount = regime.MatchCount;
            plateau.FrameToleranceMs = Math.Max(1.0, regime.UncertaintyMs);
            result.Plateaus.Add(plateau);
        }

        /// <summary>
        /// Vincola l'offset mediano all'intersezione osservata del support run
        /// </summary>
        /// <param name="support">Support run globale</param>
        /// <returns>Offset canonico compreso nell'intervallo del supporto</returns>
        private double SelectCanonicalSupportOffset(DeepSiftTemporalSupportRun support)
        {
            return Math.Clamp(support.OffsetMs, support.MinimumOffsetMs, support.MaximumOffsetMs);
        }

        /// <summary>
        /// Localizza il boundary sul lato comune e usa le black run soltanto dopo che SIFT ha dimostrato entrambi i regimi
        /// </summary>
        /// <param name="resolved">Transizione locale associata ai regimi canonici</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="sourceBlackRuns">Black run rilevate nella timeline source</param>
        /// <param name="languageBlackRuns">Black run rilevate nella timeline language</param>
        /// <returns>Boundary con la posizione e il metodo di rifinitura selezionati</returns>
        private DeepSiftBoundaryResult BuildBoundary(ResolvedTransition resolved, double scale, IReadOnlyList<DeepBlackTimelineRun> sourceBlackRuns, IReadOnlyList<DeepBlackTimelineRun> languageBlackRuns)
        {
            DeepSiftLocalRegime before = resolved.BeforeRegime;
            DeepSiftLocalRegime after = resolved.AfterRegime;
            DeepSiftLocalTransition transition = resolved.Transition;
            double offsetDeltaMs = resolved.NextOffsetMs - resolved.PreviousOffsetMs;
            bool sourceGap = offsetDeltaMs > 0.0;
            double lastBeforeCommonMs = sourceGap ? transition.LastBeforeLanguagePtsMs : transition.LastBeforeSourcePtsMs;
            double firstAfterCommonMs = sourceGap ? transition.FirstAfterCandidateLanguagePtsMs : transition.FirstAfterCandidateSourcePtsMs;
            double corridorStartMs = Math.Min(lastBeforeCommonMs, firstAfterCommonMs);
            double corridorEndMs = Math.Max(lastBeforeCommonMs, firstAfterCommonMs);
            double uncertaintyMs = before.UncertaintyMs + after.UncertaintyMs;
            IReadOnlyList<DeepBlackTimelineRun> commonTimelineRuns = sourceGap ? languageBlackRuns : sourceBlackRuns;
            IReadOnlyList<DeepBlackTimelineRun> extraTimelineRuns = sourceGap ? sourceBlackRuns : languageBlackRuns;
            double extraStartMs = sourceGap ? (corridorStartMs / scale) + Math.Min(resolved.PreviousOffsetMs, resolved.NextOffsetMs) : (corridorStartMs - Math.Max(resolved.PreviousOffsetMs, resolved.NextOffsetMs)) * scale;
            double extraEndMs = sourceGap ? (corridorEndMs / scale) + Math.Max(resolved.PreviousOffsetMs, resolved.NextOffsetMs) : (corridorEndMs - Math.Min(resolved.PreviousOffsetMs, resolved.NextOffsetMs)) * scale;
            List<DeepBlackTimelineRun> commonRuns = this.SelectBlackRuns(commonTimelineRuns, corridorStartMs - uncertaintyMs, corridorEndMs + uncertaintyMs);
            List<DeepBlackTimelineRun> extraRuns = this.SelectBlackRuns(extraTimelineRuns, extraStartMs - uncertaintyMs, extraEndMs + uncertaintyMs);
            double predictedBoundaryMs = firstAfterCommonMs;
            double blackBoundaryToleranceMs = Math.Max(1.0, uncertaintyMs + Math.Max(before.UncertaintyMs, after.UncertaintyMs));

            DeepSiftBoundaryResult boundary = new DeepSiftBoundaryResult();
            boundary.PreviousOffsetMs = resolved.PreviousOffsetMs;
            boundary.NextOffsetMs = resolved.NextOffsetMs;
            boundary.LastPreviousCommonFramePtsMs = lastBeforeCommonMs;
            boundary.FirstNextCommonFramePtsMs = firstAfterCommonMs;
            boundary.SelectedCommonBoundaryMs = predictedBoundaryMs;
            boundary.CommonCorridorStartMs = corridorStartMs;
            boundary.CommonCorridorEndMs = corridorEndMs;
            boundary.ExtraCorridorStartMs = extraStartMs;
            boundary.ExtraCorridorEndMs = extraEndMs;
            boundary.AcceptedBeforeMatches = before.MatchCount;
            boundary.AcceptedAfterMatches = after.MatchCount;
            boundary.RefinementMethod = DeepSiftBoundaryRefinementMethod.SiftFrame;
            boundary.GapType = sourceGap ? DeepSiftGapType.Source : DeepSiftGapType.Language;
            boundary.CommonTimeline = sourceGap ? DeepSiftTimelineSide.Language : DeepSiftTimelineSide.Source;
            int pairCount = this.TryResolveBlackTransition(commonRuns, extraRuns, sourceGap, scale, resolved.PreviousOffsetMs, resolved.NextOffsetMs, corridorStartMs, corridorEndMs, predictedBoundaryMs, blackBoundaryToleranceMs, out double blackBoundaryMs);
            boundary.CandidateCount = commonRuns.Count + extraRuns.Count;
            boundary.PairedCandidateCount = pairCount;
            if (pairCount == 1)
            {
                boundary.SelectedCommonBoundaryMs = blackBoundaryMs;
                boundary.RefinementMethod = DeepSiftBoundaryRefinementMethod.PairedBlackRun;
            }
            else
            {
                int containedCount = this.TryResolveContainedBlackTransition(commonRuns, extraRuns, sourceGap, scale, resolved.PreviousOffsetMs, resolved.NextOffsetMs, corridorStartMs, corridorEndMs, blackBoundaryToleranceMs, out double containedBoundaryMs);
                if (containedCount == 1)
                {
                    boundary.SelectedCommonBoundaryMs = containedBoundaryMs;
                    boundary.RefinementMethod = DeepSiftBoundaryRefinementMethod.ContainedBlackRun;
                }
                else
                {
                    int projectedCount = this.TryResolveProjectedExtraBlackTransition(extraRuns, sourceGap, scale, resolved.PreviousOffsetMs, resolved.NextOffsetMs, corridorStartMs, corridorEndMs, blackBoundaryToleranceMs, out double projectedBoundaryMs);
                    if (projectedCount == 1)
                    {
                        boundary.SelectedCommonBoundaryMs = projectedBoundaryMs;
                        boundary.RefinementMethod = DeepSiftBoundaryRefinementMethod.ProjectedExtraBlackRun;
                    }
                }
            }
            return boundary;
        }

        /// <summary>
        /// Riapplica logicamente la EditMap a ogni punto forte del percorso canonico definitivo
        /// </summary>
        /// <param name="temporal">Evidenza temporale con il percorso canonico da verificare</param>
        /// <param name="result">Risultato contenente plateau e operazioni da riapplicare</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="rejectReason">Motivo del rifiuto quando il replay rileva una contraddizione</param>
        /// <returns>True quando tutti i punti del percorso sono compatibili con la EditMap</returns>
        private bool ReplayLocalPaths(DeepSiftTemporalEvidenceResult temporal, DeepSiftEditMapResult result, double scale, out string rejectReason)
        {
            rejectReason = "";
            double[] regimeReplayUncertaintyMs = this.BuildRegimeReplayUncertainties(temporal.ResolvedPath, result.Plateaus);
            for (int pathIndex = 0; pathIndex < temporal.ResolvedPath.Count; pathIndex++)
            {
                DeepSiftLocalPathPoint point = temporal.ResolvedPath[pathIndex];
                if (point.ModeIndex < 0)
                {
                    rejectReason = AppText.F("deep.temporal.editMap.strongPairWithoutRegime", point.SourcePtsMs.ToString("F3", CultureInfo.InvariantCulture));
                    return false;
                }
                double expectedOffsetMs = result.Plateaus[0].OffsetMs;
                int plateauIndex = 0;
                for (int operationIndex = 0; operationIndex < result.EditMap.Operations.Count; operationIndex++)
                {
                    EditOperation operation = result.EditMap.Operations[operationIndex];
                    if (operation.SourceTimestampMs > point.SourcePtsMs)
                        break;
                    expectedOffsetMs += EditMapTimelineHelper.GetSourceOperationDeltaMs(operation, scale);
                    plateauIndex++;
                }
                if (point.ModeIndex >= temporal.ResolvedRegimes.Count || plateauIndex != point.ModeIndex)
                {
                    rejectReason = AppText.F("deep.temporal.editMap.topologyReplayContradiction", point.SourcePtsMs.ToString("F3", CultureInfo.InvariantCulture));
                    return false;
                }
                double roundingUncertaintyMs = result.EditMap.Operations.Count + 1.0;
                if (Math.Abs(point.OffsetMs - expectedOffsetMs) <= regimeReplayUncertaintyMs[point.ModeIndex] + roundingUncertaintyMs)
                    continue;
                rejectReason = AppText.F("deep.temporal.editMap.offsetReplayContradiction", point.SourcePtsMs.ToString("F3", CultureInfo.InvariantCulture));
                return false;
            }
            return true;
        }

        /// <summary>
        /// Calcola per ogni regime l'inviluppo necessario al replay dei punti locali
        /// </summary>
        /// <param name="path">Percorso locale canonico</param>
        /// <param name="plateaus">Plateau della EditMap</param>
        /// <returns>Incertezza massima osservata per regime</returns>
        private double[] BuildRegimeReplayUncertainties(IReadOnlyList<DeepSiftLocalPathPoint> path, IReadOnlyList<DeepSiftPlateauDiagnostic> plateaus)
        {
            double[] result = new double[plateaus.Count];
            for (int pathIndex = 0; pathIndex < path.Count; pathIndex++)
            {
                DeepSiftLocalPathPoint point = path[pathIndex];
                if (point.ModeIndex < 0 || point.ModeIndex >= plateaus.Count)
                    continue;
                double observedEnvelopeMs = Math.Abs(point.OffsetMs - plateaus[point.ModeIndex].OffsetMs) + point.UncertaintyMs;
                result[point.ModeIndex] = Math.Max(result[point.ModeIndex], observedEnvelopeMs);
            }
            return result;
        }

        /// <summary>
        /// Converte il delta fra regimi in una singola operazione sulla timeline language
        /// </summary>
        /// <param name="boundary">Boundary rifinito tra i due regimi</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <returns>Operazione di inserimento o taglio corrispondente al delta fra regimi</returns>
        private EditOperation BuildOperation(DeepSiftBoundaryResult boundary, double scale)
        {
            double offsetDeltaMs = boundary.NextOffsetMs - boundary.PreviousOffsetMs;
            EditOperation operation = new EditOperation();
            if (offsetDeltaMs > 0.0)
            {
                double sourceBeforeMs = (boundary.SelectedCommonBoundaryMs / scale) + boundary.PreviousOffsetMs;
                int sourceDurationMs = (int)Math.Round(offsetDeltaMs);
                operation.Type = EditOperation.INSERT_SILENCE;
                operation.LangTimestampMs = Math.Max(0, (int)Math.Round(boundary.SelectedCommonBoundaryMs));
                operation.SourceTimestampMs = Math.Max(0, (int)Math.Round(sourceBeforeMs));
                operation.VisualSourceTimestampMs = operation.SourceTimestampMs;
                operation.DurationMs = EditMapTimelineHelper.SourceDurationToLanguageDurationMs(sourceDurationMs, scale);
            }
            else
            {
                double languageBeforeMs = (boundary.SelectedCommonBoundaryMs - boundary.PreviousOffsetMs) * scale;
                double languageAfterMs = (boundary.SelectedCommonBoundaryMs - boundary.NextOffsetMs) * scale;
                operation.Type = EditOperation.CUT_SEGMENT;
                operation.LangTimestampMs = Math.Max(0, (int)Math.Round(languageBeforeMs));
                operation.SourceTimestampMs = Math.Max(0, (int)Math.Round(boundary.SelectedCommonBoundaryMs));
                operation.VisualSourceTimestampMs = operation.SourceTimestampMs;
                operation.DurationMs = (int)Math.Round(languageAfterMs - languageBeforeMs);
            }
            return operation;
        }

        /// <summary>
        /// Cerca coppie di run nere i cui estremi rappresentano entrambi i regimi osservati
        /// </summary>
        /// <param name="commonRuns">Run nere sul lato comune</param>
        /// <param name="extraRuns">Run nere sul lato con il segmento extra</param>
        /// <param name="sourceGap">True quando il segmento extra appartiene al source</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="previousRegimeOffsetMs">Offset prima della transizione</param>
        /// <param name="nextRegimeOffsetMs">Offset dopo la transizione</param>
        /// <param name="minimumBoundaryMs">Limite inferiore del corridoio comune</param>
        /// <param name="maximumBoundaryMs">Limite superiore del corridoio comune</param>
        /// <param name="predictedCommonBoundaryMs">Boundary visuale previsto sul lato comune</param>
        /// <param name="toleranceMs">Tolleranza temporale degli estremi</param>
        /// <param name="boundaryMs">Boundary nero selezionato</param>
        /// <returns>Numero di coppie compatibili trovate</returns>
        private int TryResolveBlackTransition(List<DeepBlackTimelineRun> commonRuns, List<DeepBlackTimelineRun> extraRuns, bool sourceGap, double scale, double previousRegimeOffsetMs, double nextRegimeOffsetMs, double minimumBoundaryMs, double maximumBoundaryMs, double predictedCommonBoundaryMs, double toleranceMs, out double boundaryMs)
        {
            boundaryMs = 0.0;
            int pairCount = 0;
            double bestDistanceMs = double.PositiveInfinity;
            for (int commonIndex = 0; commonIndex < commonRuns.Count; commonIndex++)
            {
                DeepBlackTimelineRun commonRun = commonRuns[commonIndex];
                double expectedExtraStartMs = sourceGap ? (commonRun.StartPtsMs / scale) + previousRegimeOffsetMs : (commonRun.StartPtsMs - previousRegimeOffsetMs) * scale;
                double expectedExtraEndMs = sourceGap ? (commonRun.EndPtsMs / scale) + nextRegimeOffsetMs : (commonRun.EndPtsMs - nextRegimeOffsetMs) * scale;
                double extraToleranceMs = sourceGap ? toleranceMs : toleranceMs * scale;
                for (int extraIndex = 0; extraIndex < extraRuns.Count; extraIndex++)
                {
                    DeepBlackTimelineRun extraRun = extraRuns[extraIndex];
                    bool endpointPair = Math.Abs(extraRun.StartPtsMs - expectedExtraStartMs) <= extraToleranceMs && Math.Abs(extraRun.EndPtsMs - expectedExtraEndMs) <= extraToleranceMs;
                    if (!endpointPair)
                        continue;
                    double selectedBoundaryMs = commonRun.EndPtsMs;
                    if (selectedBoundaryMs < minimumBoundaryMs || selectedBoundaryMs > maximumBoundaryMs)
                        continue;
                    pairCount++;
                    double distanceMs = Math.Abs(selectedBoundaryMs - predictedCommonBoundaryMs);
                    if (distanceMs < bestDistanceMs)
                    {
                        boundaryMs = selectedBoundaryMs;
                        bestDistanceMs = distanceMs;
                    }
                }
            }
            return pairCount;
        }

        /// <summary>
        /// Proietta sul lato comune ciascuna run nera del solo lato extra usando i due regimi osservati
        /// </summary>
        /// <param name="extraRuns">Run nere disponibili soltanto sul lato con il segmento extra</param>
        /// <param name="sourceGap">True quando il segmento extra appartiene al source</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="previousRegimeOffsetMs">Offset prima della transizione</param>
        /// <param name="nextRegimeOffsetMs">Offset dopo la transizione</param>
        /// <param name="minimumBoundaryMs">Limite inferiore del corridoio comune</param>
        /// <param name="maximumBoundaryMs">Limite superiore del corridoio comune</param>
        /// <param name="toleranceMs">Tolleranza temporale tra le proiezioni</param>
        /// <param name="boundaryMs">Boundary proiettato selezionato</param>
        /// <returns>Numero di run extra proiettate in modo compatibile</returns>
        private int TryResolveProjectedExtraBlackTransition(IReadOnlyList<DeepBlackTimelineRun> extraRuns, bool sourceGap, double scale, double previousRegimeOffsetMs, double nextRegimeOffsetMs, double minimumBoundaryMs, double maximumBoundaryMs, double toleranceMs, out double boundaryMs)
        {
            boundaryMs = 0.0;
            int candidateCount = 0;
            for (int runIndex = 0; runIndex < extraRuns.Count; runIndex++)
            {
                DeepBlackTimelineRun run = extraRuns[runIndex];
                double previousProjectionMs = sourceGap ? (run.StartPtsMs - previousRegimeOffsetMs) * scale : (run.StartPtsMs / scale) + previousRegimeOffsetMs;
                double nextProjectionMs = sourceGap ? (run.EndPtsMs - nextRegimeOffsetMs) * scale : (run.EndPtsMs / scale) + nextRegimeOffsetMs;
                if (Math.Abs(previousProjectionMs - nextProjectionMs) > toleranceMs)
                    continue;
                if (previousProjectionMs < minimumBoundaryMs || previousProjectionMs > maximumBoundaryMs)
                    continue;
                boundaryMs = previousProjectionMs;
                candidateCount++;
            }
            return candidateCount;
        }

        /// <summary>
        /// Valuta le coppie di run quando l'intervallo proiettato del segmento extra interseca una run comune
        /// </summary>
        /// <param name="commonRuns">Run nere sul lato comune</param>
        /// <param name="extraRuns">Run nere sul lato con il segmento extra</param>
        /// <param name="sourceGap">True quando il segmento extra appartiene al source</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="previousRegimeOffsetMs">Offset prima della transizione</param>
        /// <param name="nextRegimeOffsetMs">Offset dopo la transizione</param>
        /// <param name="minimumBoundaryMs">Limite inferiore del corridoio comune</param>
        /// <param name="maximumBoundaryMs">Limite superiore del corridoio comune</param>
        /// <param name="toleranceMs">Tolleranza temporale dell'intersezione</param>
        /// <param name="boundaryMs">Boundary contenuto selezionato</param>
        /// <returns>Numero di coppie compatibili trovate</returns>
        private int TryResolveContainedBlackTransition(IReadOnlyList<DeepBlackTimelineRun> commonRuns, IReadOnlyList<DeepBlackTimelineRun> extraRuns, bool sourceGap, double scale, double previousRegimeOffsetMs, double nextRegimeOffsetMs, double minimumBoundaryMs, double maximumBoundaryMs, double toleranceMs, out double boundaryMs)
        {
            boundaryMs = 0.0;
            int candidateCount = 0;
            for (int commonIndex = 0; commonIndex < commonRuns.Count; commonIndex++)
            {
                DeepBlackTimelineRun commonRun = commonRuns[commonIndex];
                for (int extraIndex = 0; extraIndex < extraRuns.Count; extraIndex++)
                {
                    DeepBlackTimelineRun extraRun = extraRuns[extraIndex];
                    double previousProjectionMs = sourceGap ? (extraRun.StartPtsMs - previousRegimeOffsetMs) * scale : (extraRun.StartPtsMs / scale) + previousRegimeOffsetMs;
                    double nextProjectionMs = sourceGap ? (extraRun.EndPtsMs - nextRegimeOffsetMs) * scale : (extraRun.EndPtsMs / scale) + nextRegimeOffsetMs;
                    double projectionStartMs = Math.Min(previousProjectionMs, nextProjectionMs);
                    double projectionEndMs = Math.Max(previousProjectionMs, nextProjectionMs);
                    bool intersectsCommonRun = projectionStartMs <= commonRun.EndPtsMs + toleranceMs && projectionEndMs >= commonRun.StartPtsMs - toleranceMs;
                    if (!intersectsCommonRun)
                        continue;
                    double selectedBoundaryMs = Math.Clamp((previousProjectionMs + nextProjectionMs) * 0.5, commonRun.StartPtsMs, commonRun.EndPtsMs);
                    if (selectedBoundaryMs < minimumBoundaryMs || selectedBoundaryMs > maximumBoundaryMs)
                        continue;
                    boundaryMs = selectedBoundaryMs;
                    candidateCount++;
                }
            }
            return candidateCount;
        }

        /// <summary>
        /// Seleziona le run nere che intersecano il corridoio richiesto
        /// </summary>
        /// <param name="runs">Run nere della timeline</param>
        /// <param name="startMs">Inizio del corridoio</param>
        /// <param name="endMs">Fine del corridoio</param>
        /// <returns>Run nere che intersecano il corridoio</returns>
        private List<DeepBlackTimelineRun> SelectBlackRuns(IReadOnlyList<DeepBlackTimelineRun> runs, double startMs, double endMs)
        {
            List<DeepBlackTimelineRun> result = new List<DeepBlackTimelineRun>();
            if (runs == null)
                return result;
            for (int runIndex = 0; runIndex < runs.Count; runIndex++)
            {
                DeepBlackTimelineRun run = runs[runIndex];
                if (run.EndPtsMs < startMs || run.StartPtsMs > endMs)
                    continue;
                result.Add(run);
            }
            return result;
        }

        /// <summary>
        /// Verifica ordine, limiti e proiezione cumulativa dell'EditMap prima dell'accettazione
        /// </summary>
        /// <param name="result">Risultato contenente la EditMap e i plateau da verificare</param>
        /// <param name="sourceToLanguageScale">Scala temporale dal source al language</param>
        /// <param name="sourceTimeline">Timeline source usata per verificare i limiti</param>
        /// <param name="languageTimeline">Timeline language usata per verificare i limiti</param>
        /// <param name="rejectReason">Motivo del rifiuto quando la EditMap non è valida</param>
        /// <returns>True quando la EditMap rispetta ordine, limiti e proiezioni attese</returns>
        private bool ValidateEditMap(DeepSiftEditMapResult result, double sourceToLanguageScale, DeepSiftAnchorTimeline sourceTimeline, DeepSiftAnchorTimeline languageTimeline, out string rejectReason)
        {
            rejectReason = "";
            if (sourceToLanguageScale <= 0.0 || double.IsNaN(sourceToLanguageScale) || double.IsInfinity(sourceToLanguageScale))
            {
                rejectReason = AppText.T("deep.temporal.editMap.invalidScale");
                return false;
            }

            if (result.Plateaus.Count != result.EditMap.Operations.Count + 1)
            {
                rejectReason = AppText.T("deep.temporal.editMap.inconsistentPlateauOperationCount");
                return false;
            }

            int cumulativeDeltaMs = 0;
            int previousLanguageEndMs = -1;
            int previousSourceTimestampMs = -1;
            double sourceEndMs = this.GetTimelineEndMs(sourceTimeline);
            double languageEndMs = this.GetTimelineEndMs(languageTimeline);
            for (int operationIndex = 0; operationIndex < result.EditMap.Operations.Count; operationIndex++)
            {
                EditOperation operation = result.EditMap.Operations[operationIndex];
                if (operation == null || operation.DurationMs <= 0)
                {
                    rejectReason = AppText.T("deep.temporal.editMap.invalidOperation");
                    return false;
                }
                if (!string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal) && !string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                {
                    rejectReason = AppText.T("deep.temporal.editMap.unknownOperationType");
                    return false;
                }
                if (operation.LangTimestampMs < 0 || operation.SourceTimestampMs < 0 || operation.VisualSourceTimestampMs < 0)
                {
                    rejectReason = AppText.T("deep.temporal.editMap.negativeTimestamps");
                    return false;
                }
                if (operation.LangTimestampMs < previousLanguageEndMs || operation.SourceTimestampMs < previousSourceTimestampMs)
                {
                    rejectReason = AppText.T("deep.temporal.editMap.overlappingOperations");
                    return false;
                }
                if (operation.SourceTimestampMs > sourceEndMs || operation.LangTimestampMs > languageEndMs || (string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal) && operation.LangTimestampMs + operation.DurationMs > languageEndMs))
                {
                    rejectReason = AppText.T("deep.temporal.editMap.timestampsBeyondTimeline");
                    return false;
                }

                double expectedSourceBeforeMs = (operation.LangTimestampMs / sourceToLanguageScale) + result.Plateaus[0].OffsetMs + cumulativeDeltaMs;
                double coordinateToleranceMs = this.GetPlateauValidationTolerance(result.Plateaus[operationIndex], result.Plateaus[operationIndex + 1]);
                if (Math.Abs(operation.SourceTimestampMs - expectedSourceBeforeMs) > coordinateToleranceMs)
                {
                    rejectReason = AppText.T("deep.temporal.editMap.inconsistentBoundaryProjection");
                    return false;
                }

                int sourceDeltaMs = EditMapTimelineHelper.GetSourceOperationDeltaMs(operation, sourceToLanguageScale);
                cumulativeDeltaMs += sourceDeltaMs;
                double expectedDeltaMs = result.Plateaus[operationIndex + 1].OffsetMs - result.Plateaus[0].OffsetMs;
                double actualDeltaMs = cumulativeDeltaMs;
                double toleranceMs = this.GetPlateauValidationTolerance(result.Plateaus[operationIndex], result.Plateaus[operationIndex + 1]);
                if (Math.Abs(actualDeltaMs - expectedDeltaMs) > toleranceMs)
                {
                    rejectReason = AppText.T("deep.temporal.editMap.cumulativeOffsetMismatch");
                    return false;
                }

                previousLanguageEndMs = operation.LangTimestampMs;
                if (string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                    previousLanguageEndMs += operation.DurationMs;
                previousSourceTimestampMs = operation.SourceTimestampMs;
            }

            return true;
        }

        /// <summary>
        /// Calcola l'ultimo PTS rappresentato dalla timeline indicizzata
        /// </summary>
        /// <param name="timeline">Timeline di cui calcolare la fine temporale</param>
        /// <returns>Timestamp in millisecondi della fine della timeline</returns>
        private double GetTimelineEndMs(DeepSiftAnchorTimeline timeline)
        {
            if (timeline == null || timeline.Anchors == null || timeline.Anchors.Count == 0)
                return 0.0;
            DeepSiftVisualAnchor last = timeline.Anchors[timeline.Anchors.Count - 1];
            return last.PtsMs + Math.Max(last.DurationMs, last.FrameDurationMs);
        }

        /// <summary>
        /// Calcola una tolleranza proporzionale alla risoluzione temporale dei plateau
        /// </summary>
        /// <param name="previous">Plateau precedente al boundary</param>
        /// <param name="next">Plateau successivo al boundary</param>
        /// <returns>Tolleranza temporale da applicare alla validazione del boundary</returns>
        private double GetPlateauValidationTolerance(DeepSiftPlateauDiagnostic previous, DeepSiftPlateauDiagnostic next)
        {
            double observedDispersionMs = Math.Max(previous.OffsetDispersionMs, next.OffsetDispersionMs);
            return Math.Max(Math.Max(previous.FrameToleranceMs, next.FrameToleranceMs), observedDispersionMs * 2.0);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Aggrega la transizione locale e i regimi necessari a costruire un'operazione
        /// </summary>
        private sealed class ResolvedTransition
        {
            /// <summary>
            /// Regione candidata proprietaria
            /// </summary>
            public DeepSiftTemporalCandidateRegion Region { get; set; }

            /// <summary>
            /// Transizione locale risolta
            /// </summary>
            public DeepSiftLocalTransition Transition { get; set; }

            /// <summary>
            /// Regime locale precedente
            /// </summary>
            public DeepSiftLocalRegime BeforeRegime { get; set; }

            /// <summary>
            /// Regime locale successivo
            /// </summary>
            public DeepSiftLocalRegime AfterRegime { get; set; }

            /// <summary>
            /// Offset canonico precedente
            /// </summary>
            public double PreviousOffsetMs { get; set; }

            /// <summary>
            /// Offset canonico successivo
            /// </summary>
            public double NextOffsetMs { get; set; }
        }

        #endregion
    }
}
