using RemuxForge.Core.Analysis.Deep.Features;
using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Infrastructure;
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
        #region Costanti

        private const double LOCAL_BLACK_SCAN_EXPANSION_MS = 90000.0;
        private const double LOCAL_COARSE_HALF_WIDTH_MS = 90000.0;
        private const double LOCAL_MARGIN_FRAME_MULTIPLIER = 3.0;

        #endregion

        #region Variabili di classe

        private readonly FrameExtractionService _commonFrameExtractor;
        private readonly FrameExtractionService _extraFrameExtractor;
        private readonly FrameFeatureBatchMatcherBase _batchMatcher;
        private readonly VideoSyncConfig _videoSyncConfig;
        private readonly int _maxDegreeOfParallelism;
        private readonly Func<string, bool> _geometryCropResolver;
        private readonly Action<string, bool, string, List<byte[]>> _frameNormalizer;

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
                throw new ArgumentException("Percorso FFmpeg mancante", nameof(ffmpegPath));
            if (ffmpegConfig == null)
                throw new ArgumentNullException(nameof(ffmpegConfig));
            if (maximumParallelism < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumParallelism));
            this._videoSyncConfig = videoSyncConfig ?? throw new ArgumentNullException(nameof(videoSyncConfig));
            this._batchMatcher = batchMatcher ?? throw new ArgumentNullException(nameof(batchMatcher));
            this._geometryCropResolver = geometryCropResolver ?? throw new ArgumentNullException(nameof(geometryCropResolver));
            this._frameNormalizer = frameNormalizer ?? throw new ArgumentNullException(nameof(frameNormalizer));
            this._commonFrameExtractor = new FrameExtractionService(ffmpegPath, videoSyncConfig, ffmpegConfig, logSection);
            this._extraFrameExtractor = new FrameExtractionService(ffmpegPath, videoSyncConfig, ffmpegConfig, logSection);
            this._maxDegreeOfParallelism = maximumParallelism;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce la EditMap direttamente dalle transizioni temporali sparse
        /// </summary>
        public DeepSiftEditMapResult BuildTemporal(string sourcePath, string languagePath, string sourceCropPx, string languageCropPx, string stretchFactor, double sourceToLanguageScale, DeepSiftAnchorTimeline sourceTimeline, DeepSiftAnchorTimeline languageTimeline, DeepSiftTemporalEvidenceResult temporal, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sourcePath))
                throw new ArgumentException("Percorso source mancante", nameof(sourcePath));
            if (string.IsNullOrEmpty(languagePath))
                throw new ArgumentException("Percorso language mancante", nameof(languagePath));
            if (sourceTimeline == null)
                throw new ArgumentNullException(nameof(sourceTimeline));
            if (languageTimeline == null)
                throw new ArgumentNullException(nameof(languageTimeline));
            if (temporal == null || !temporal.Accepted || temporal.Plateaus.Count == 0)
                throw new ArgumentException("Risultato temporale non accettato", nameof(temporal));
            if (sourceToLanguageScale <= 0.0 || double.IsNaN(sourceToLanguageScale) || double.IsInfinity(sourceToLanguageScale))
                throw new ArgumentOutOfRangeException(nameof(sourceToLanguageScale));
            if (string.IsNullOrEmpty(stretchFactor))
            {
                if (Math.Abs(sourceToLanguageScale - 1.0) > 0.000000001)
                    throw new ArgumentException("Lo stretch vuoto richiede scala temporale identità", nameof(stretchFactor));
            }
            else
            {
                if (!SpeedCorrectionService.TryParseStretchFactor(stretchFactor, out double stretchRatio, out _) ||
                    Math.Abs((1.0 / stretchRatio) - sourceToLanguageScale) > 0.000000001)
                    throw new ArgumentException("Stretch e scala temporale non sono coerenti", nameof(stretchFactor));
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            DeepSiftEditMapResult result = new DeepSiftEditMapResult();
            result.EditMap.StretchFactor = stretchFactor ?? "";
            result.EditMap.InitialDelayMs = (int)Math.Round(temporal.Plateaus[0].OffsetMs);
            for (int plateauIndex = 0; plateauIndex < temporal.Plateaus.Count; plateauIndex++)
            {
                DeepSiftTemporalPlateau temporalPlateau = temporal.Plateaus[plateauIndex];
                DeepSiftPlateauDiagnostic plateau = new DeepSiftPlateauDiagnostic();
                plateau.FirstMatchIndex = temporalPlateau.FirstChainIndex;
                plateau.LastMatchIndex = temporalPlateau.LastChainIndex;
                plateau.OffsetMs = temporalPlateau.OffsetMs;
                plateau.OffsetDispersionMs = temporalPlateau.UncertaintyMs;
                plateau.SourceStartPtsMs = temporalPlateau.SourceStartPtsMs;
                plateau.SourceEndPtsMs = temporalPlateau.SourceEndPtsMs;
                plateau.MatchCount = temporalPlateau.MatchCount;
                plateau.FrameToleranceMs = Math.Max(1.0, temporalPlateau.UncertaintyMs);
                plateau.Accepted = true;
                result.Plateaus.Add(plateau);
            }

            if (temporal.Transitions.Count != temporal.Plateaus.Count - 1)
            {
                result.RejectReason = "Numero transizioni temporali e plateau non coerente";
                result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            List<BoundaryScan> acceptedScans = new List<BoundaryScan>(temporal.Transitions.Count);
            for (int transitionIndex = 0; transitionIndex < temporal.Transitions.Count; transitionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeepSiftTemporalTransition transition = temporal.Transitions[transitionIndex];
                DeepSiftTemporalPlateau previousPlateau = temporal.Plateaus[transition.BeforePlateauIndex];
                DeepSiftTemporalPlateau nextPlateau = temporal.Plateaus[transition.AfterPlateauIndex];
                double offsetDeltaMs = nextPlateau.OffsetMs - previousPlateau.OffsetMs;
                if (Math.Abs(offsetDeltaMs) < 1.0)
                {
                    result.RejectReason = "Transizione temporale senza variazione di offset";
                    result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
                    return result;
                }

                DeepSiftBoundaryResult boundary = new DeepSiftBoundaryResult();
                boundary.PreviousOffsetMs = previousPlateau.OffsetMs;
                boundary.NextOffsetMs = nextPlateau.OffsetMs;
                boundary.LastOldCommonFramePtsMs = offsetDeltaMs > 0.0 ? transition.LastOldLanguagePtsMs : transition.LastOldSourcePtsMs;
                boundary.FirstNewCommonFramePtsMs = offsetDeltaMs > 0.0 ? transition.FirstNewLanguagePtsMs : transition.FirstNewSourcePtsMs;
                boundary.SelectedCommonBoundaryMs = boundary.FirstNewCommonFramePtsMs;
                boundary.CommonCorridorStartMs = Math.Min(boundary.LastOldCommonFramePtsMs, boundary.FirstNewCommonFramePtsMs);
                boundary.CommonCorridorEndMs = Math.Max(boundary.LastOldCommonFramePtsMs, boundary.FirstNewCommonFramePtsMs);
                boundary.AcceptedBeforeMatches = previousPlateau.MatchCount;
                boundary.AcceptedAfterMatches = nextPlateau.MatchCount;
                result.Boundaries.Add(boundary);
                double commonEvidenceSeparationMs = offsetDeltaMs > 0.0
                    ? Math.Abs(transition.FirstNewLanguagePtsMs - transition.LastOldLanguagePtsMs)
                    : Math.Abs(transition.FirstNewSourcePtsMs - transition.LastOldSourcePtsMs);
                double refinementRadiusMs = Math.Max(2000.0, commonEvidenceSeparationMs);
                double sourceTimelineDurationMs = this.GetTimelineEndMs(sourceTimeline);
                double sourceCenterMs = (transition.LastOldSourcePtsMs + transition.FirstNewSourcePtsMs) * 0.5;
                double sourceSearchStartMs = 0.0;
                if (transitionIndex > 0)
                {
                    DeepSiftTemporalTransition previousTransition = temporal.Transitions[transitionIndex - 1];
                    double previousSourceCenterMs = (previousTransition.LastOldSourcePtsMs + previousTransition.FirstNewSourcePtsMs) * 0.5;
                    sourceSearchStartMs = (previousSourceCenterMs + sourceCenterMs) * 0.5;
                }
                double sourceSearchEndMs = sourceTimelineDurationMs;
                if (transitionIndex + 1 < temporal.Transitions.Count)
                {
                    DeepSiftTemporalTransition followingTransition = temporal.Transitions[transitionIndex + 1];
                    double followingSourceCenterMs = (followingTransition.LastOldSourcePtsMs + followingTransition.FirstNewSourcePtsMs) * 0.5;
                    sourceSearchEndMs = (sourceCenterMs + followingSourceCenterMs) * 0.5;
                }
                sourceSearchStartMs = Math.Max(sourceSearchStartMs, sourceCenterMs - LOCAL_COARSE_HALF_WIDTH_MS);
                sourceSearchEndMs = Math.Min(sourceSearchEndMs, sourceCenterMs + LOCAL_COARSE_HALF_WIDTH_MS);
                bool sourceGap = offsetDeltaMs > 0.0;
                double commonSearchStartMs = sourceGap ? (sourceSearchStartMs - previousPlateau.OffsetMs) * sourceToLanguageScale : sourceSearchStartMs;
                double commonSearchEndMs = sourceGap ? (sourceSearchEndMs - nextPlateau.OffsetMs) * sourceToLanguageScale : sourceSearchEndMs;
                BoundaryScan scan = this.RefineTemporalBoundary(sourcePath, languagePath, sourceCropPx, languageCropPx, sourceToLanguageScale, transition, previousPlateau, nextPlateau, boundary, refinementRadiusMs, Math.Max(0.0, commonSearchStartMs), commonSearchEndMs, false, false, sourceTimeline.BlackRuns, languageTimeline.BlackRuns, cancellationToken);
                double expandedRadiusMs = Math.Min(15000.0, sourceTimelineDurationMs / Math.Max(1.0, Math.Sqrt(sourceTimeline.Anchors.Count)));
                if (!scan.Accepted && expandedRadiusMs > refinementRadiusMs)
                {
                    BoundaryScan expandedScan = this.RefineTemporalBoundary(sourcePath, languagePath, sourceCropPx, languageCropPx, sourceToLanguageScale, transition, previousPlateau, nextPlateau, boundary, expandedRadiusMs, double.NaN, double.NaN, false, false, sourceTimeline.BlackRuns, languageTimeline.BlackRuns, cancellationToken);
                    expandedScan.SourceExtractionMs += scan.SourceExtractionMs;
                    expandedScan.LanguageExtractionMs += scan.LanguageExtractionMs;
                    expandedScan.MatchingMs += scan.MatchingMs;
                    scan = expandedScan;
                }
                result.LocalSourceExtractionMs += scan.SourceExtractionMs;
                result.LocalLanguageExtractionMs += scan.LanguageExtractionMs;
                result.LocalMatchingMs += scan.MatchingMs;
                if (!scan.Accepted)
                {
                    result.RejectReason = boundary.RejectReason;
                    result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
                    return result;
                }
                boundary.SelectedCommonBoundaryMs = scan.CommonBoundaryMs;
                boundary.FrameRefined = true;
                acceptedScans.Add(scan);
            }

            for (int plateauIndex = 0; plateauIndex < result.Plateaus.Count; plateauIndex++)
                result.Plateaus[plateauIndex].OffsetMs = temporal.Plateaus[plateauIndex].OffsetMs;
            List<double>[] blackCorroboratedOffsets = new List<double>[result.Plateaus.Count];
            for (int plateauIndex = 0; plateauIndex < blackCorroboratedOffsets.Length; plateauIndex++)
                blackCorroboratedOffsets[plateauIndex] = new List<double>();
            for (int transitionIndex = 0; transitionIndex < temporal.Transitions.Count; transitionIndex++)
            {
                if (!result.Boundaries[transitionIndex].BlackRunPaired)
                    continue;
                DeepSiftTemporalTransition transition = temporal.Transitions[transitionIndex];
                double previousToleranceMs = Math.Max(100.0, temporal.Plateaus[transition.BeforePlateauIndex].UncertaintyMs * 1.5);
                double nextToleranceMs = Math.Max(100.0, temporal.Plateaus[transition.AfterPlateauIndex].UncertaintyMs * 1.5);
                if (Math.Abs(acceptedScans[transitionIndex].PreviousOffsetMs - temporal.Plateaus[transition.BeforePlateauIndex].OffsetMs) > 0.5 && Math.Abs(acceptedScans[transitionIndex].PreviousOffsetMs - temporal.Plateaus[transition.BeforePlateauIndex].OffsetMs) <= previousToleranceMs)
                    blackCorroboratedOffsets[transition.BeforePlateauIndex].Add(acceptedScans[transitionIndex].PreviousOffsetMs);
                if (Math.Abs(acceptedScans[transitionIndex].NextOffsetMs - temporal.Plateaus[transition.AfterPlateauIndex].OffsetMs) > 0.5 && Math.Abs(acceptedScans[transitionIndex].NextOffsetMs - temporal.Plateaus[transition.AfterPlateauIndex].OffsetMs) <= nextToleranceMs)
                    blackCorroboratedOffsets[transition.AfterPlateauIndex].Add(acceptedScans[transitionIndex].NextOffsetMs);
            }
            for (int plateauIndex = 0; plateauIndex < blackCorroboratedOffsets.Length; plateauIndex++)
            {
                List<double> candidates = blackCorroboratedOffsets[plateauIndex];
                if (candidates.Count == 0)
                    continue;
                candidates.Sort();
                int middle = candidates.Count / 2;
                result.Plateaus[plateauIndex].OffsetMs = candidates.Count % 2 == 0 ? (candidates[middle - 1] + candidates[middle]) * 0.5 : candidates[middle];
            }
            result.EditMap.InitialDelayMs = (int)Math.Round(result.Plateaus[0].OffsetMs);

            for (int transitionIndex = 0; transitionIndex < temporal.Transitions.Count; transitionIndex++)
            {
                DeepSiftTemporalTransition transition = temporal.Transitions[transitionIndex];
                DeepSiftBoundaryResult boundary = result.Boundaries[transitionIndex];
                BoundaryScan scan = acceptedScans[transitionIndex];
                double previousOffsetMs = result.Plateaus[transition.BeforePlateauIndex].OffsetMs;
                double nextOffsetMs = result.Plateaus[transition.AfterPlateauIndex].OffsetMs;
                boundary.PreviousOffsetMs = previousOffsetMs;
                boundary.NextOffsetMs = nextOffsetMs;
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  boundary " + transitionIndex.ToString(CultureInfo.InvariantCulture) + ": old=" + scan.PreviousOffsetMs.ToString("F1", CultureInfo.InvariantCulture) + "ms, risolto=" + previousOffsetMs.ToString("F1", CultureInfo.InvariantCulture) + "ms, new=" + scan.NextOffsetMs.ToString("F1", CultureInfo.InvariantCulture) + "ms, successivo=" + nextOffsetMs.ToString("F1", CultureInfo.InvariantCulture) + "ms");
                double offsetDeltaMs = nextOffsetMs - previousOffsetMs;
                if (Math.Abs(offsetDeltaMs) < 1.0)
                {
                    result.RejectReason = "Transizione temporale raffinata senza variazione di offset";
                    result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
                    return result;
                }

                EditOperation operation = new EditOperation();
                if (offsetDeltaMs > 0.0)
                {
                    boundary.GapType = "SOURCE_GAP";
                    boundary.CommonTimeline = "language";
                    double sourceBeforeMs = (boundary.SelectedCommonBoundaryMs / sourceToLanguageScale) + boundary.PreviousOffsetMs;
                    int sourceDurationMs = (int)Math.Round(offsetDeltaMs);
                    operation.Type = EditOperation.INSERT_SILENCE;
                    operation.LangTimestampMs = Math.Max(0, (int)Math.Round(boundary.SelectedCommonBoundaryMs));
                    operation.SourceTimestampMs = Math.Max(0, (int)Math.Round(sourceBeforeMs));
                    operation.VisualSourceTimestampMs = operation.SourceTimestampMs;
                    operation.DurationMs = EditMapTimelineHelper.SourceDurationToLanguageDurationMs(sourceDurationMs, sourceToLanguageScale);
                }
                else
                {
                    boundary.GapType = "LANGUAGE_GAP";
                    boundary.CommonTimeline = "source";
                    double languageBeforeMs = (boundary.SelectedCommonBoundaryMs - boundary.PreviousOffsetMs) * sourceToLanguageScale;
                    double languageAfterMs = (boundary.SelectedCommonBoundaryMs - boundary.NextOffsetMs) * sourceToLanguageScale;
                    operation.Type = EditOperation.CUT_SEGMENT;
                    operation.LangTimestampMs = Math.Max(0, (int)Math.Round(languageBeforeMs));
                    operation.SourceTimestampMs = Math.Max(0, (int)Math.Round(boundary.SelectedCommonBoundaryMs));
                    operation.VisualSourceTimestampMs = operation.SourceTimestampMs;
                    operation.DurationMs = (int)Math.Round(languageAfterMs - languageBeforeMs);
                }

                if (operation.DurationMs <= 0)
                {
                    result.RejectReason = "Durata operazione temporale non positiva";
                    result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
                    return result;
                }

                boundary.Operation = operation;
                result.EditMap.Operations.Add(operation);
            }

            result.GapCount = temporal.Transitions.Count;
            result.OperationCount = result.EditMap.Operations.Count;
            if (!this.ValidateEditMap(result, sourceToLanguageScale, sourceTimeline, languageTimeline, out string validationReason))
            {
                result.EditMap.Operations.Clear();
                result.OperationCount = 0;
                result.RejectReason = validationReason;
                result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            result.Success = true;
            result.TotalElapsedMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        private BoundaryScan RefineTemporalBoundary(string sourcePath, string languagePath, string sourceCropPx, string languageCropPx, double scale, DeepSiftTemporalTransition transition, DeepSiftTemporalPlateau previousPlateau, DeepSiftTemporalPlateau nextPlateau, DeepSiftBoundaryResult diagnostic, double searchRadiusMs, double commonStartOverrideMs, double commonEndOverrideMs, bool forceFullRate, bool skipBlackResolution, IReadOnlyList<DeepBlackTimelineRun> sourceBlackRuns, IReadOnlyList<DeepBlackTimelineRun> languageBlackRuns, CancellationToken cancellationToken)
        {
            BoundaryScan result = new BoundaryScan();
            bool sourceGap = nextPlateau.OffsetMs > previousPlateau.OffsetMs;
            double commonRadiusMs = sourceGap ? searchRadiusMs * scale : searchRadiusMs;
            double previousCommonMs = double.IsNaN(commonStartOverrideMs) ? (sourceGap ? transition.LastOldLanguagePtsMs : transition.LastOldSourcePtsMs) - commonRadiusMs : commonStartOverrideMs;
            double nextCommonMs = double.IsNaN(commonEndOverrideMs) ? (sourceGap ? transition.FirstNewLanguagePtsMs : transition.FirstNewSourcePtsMs) + commonRadiusMs : commonEndOverrideMs;
            previousCommonMs = Math.Max(0.0, previousCommonMs);
            if (nextCommonMs <= previousCommonMs)
            {
                diagnostic.RejectReason = "Corridoio temporale non monotono";
                return result;
            }

            double frameMarginMs = Math.Max(250.0, Math.Max(previousPlateau.UncertaintyMs, nextPlateau.UncertaintyMs) * LOCAL_MARGIN_FRAME_MULTIPLIER);
            int commonStartMs = Math.Max(0, (int)Math.Floor(previousCommonMs - frameMarginMs));
            double commonEndMs = nextCommonMs + frameMarginMs;
            string commonPath = sourceGap ? languagePath : sourcePath;
            string extraPath = sourceGap ? sourcePath : languagePath;
            string commonCropPx = sourceGap ? languageCropPx : sourceCropPx;
            string extraCropPx = sourceGap ? sourceCropPx : languageCropPx;
            double oldExtraStartMs = sourceGap ? (commonStartMs / scale) + previousPlateau.OffsetMs : (commonStartMs - previousPlateau.OffsetMs) * scale;
            double oldExtraEndMs = sourceGap ? (commonEndMs / scale) + previousPlateau.OffsetMs : (commonEndMs - previousPlateau.OffsetMs) * scale;
            double newExtraStartMs = sourceGap ? (commonStartMs / scale) + nextPlateau.OffsetMs : (commonStartMs - nextPlateau.OffsetMs) * scale;
            double newExtraEndMs = sourceGap ? (commonEndMs / scale) + nextPlateau.OffsetMs : (commonEndMs - nextPlateau.OffsetMs) * scale;
            int extraStartMs = Math.Max(0, (int)Math.Floor(Math.Min(oldExtraStartMs, newExtraStartMs) - frameMarginMs));
            double extraEndMs = Math.Max(oldExtraEndMs, newExtraEndMs) + frameMarginMs;
            diagnostic.CommonCorridorStartMs = commonStartMs;
            diagnostic.CommonCorridorEndMs = commonEndMs;
            diagnostic.ExtraCorridorStartMs = extraStartMs;
            diagnostic.ExtraCorridorEndMs = extraEndMs;
            double targetFps = forceFullRate ? 0.0 : nextCommonMs - previousCommonMs > 15000.0 ? 2.0 : 0.0;

            if (!skipBlackResolution)
            {
                double blackExpansionMs = forceFullRate ? 0.0 : LOCAL_BLACK_SCAN_EXPANSION_MS;
                double blackCommonStartMs = Math.Max(0.0, commonStartMs - blackExpansionMs);
                double blackCommonEndMs = commonEndMs + blackExpansionMs;
                double blackOldExtraStartMs = sourceGap ? (blackCommonStartMs / scale) + previousPlateau.OffsetMs : (blackCommonStartMs - previousPlateau.OffsetMs) * scale;
                double blackOldExtraEndMs = sourceGap ? (blackCommonEndMs / scale) + previousPlateau.OffsetMs : (blackCommonEndMs - previousPlateau.OffsetMs) * scale;
                double blackNewExtraStartMs = sourceGap ? (blackCommonStartMs / scale) + nextPlateau.OffsetMs : (blackCommonStartMs - nextPlateau.OffsetMs) * scale;
                double blackNewExtraEndMs = sourceGap ? (blackCommonEndMs / scale) + nextPlateau.OffsetMs : (blackCommonEndMs - nextPlateau.OffsetMs) * scale;
                double blackExtraStartMs = Math.Max(0.0, Math.Min(blackOldExtraStartMs, blackNewExtraStartMs) - frameMarginMs);
                double blackExtraEndMs = Math.Max(blackOldExtraEndMs, blackNewExtraEndMs) + frameMarginMs;
                IReadOnlyList<DeepBlackTimelineRun> commonTimelineRuns = sourceGap ? languageBlackRuns : sourceBlackRuns;
                IReadOnlyList<DeepBlackTimelineRun> extraTimelineRuns = sourceGap ? sourceBlackRuns : languageBlackRuns;
                List<DeepBlackTimelineRun> commonBlackRuns = this.SelectBlackRuns(commonTimelineRuns, blackCommonStartMs, blackCommonEndMs);
                List<DeepBlackTimelineRun> extraBlackRuns = this.SelectBlackRuns(extraTimelineRuns, blackExtraStartMs, blackExtraEndMs);
                int blackPairCount = this.TryResolveBlackTransition(commonBlackRuns, extraBlackRuns, sourceGap, scale, previousPlateau.OffsetMs, nextPlateau.OffsetMs, Math.Max(600.0, Math.Max(previousPlateau.UncertaintyMs, nextPlateau.UncertaintyMs) * 2.0), out double blackBoundaryMs, out double blackStartMs);
                if (blackPairCount == 1)
                {
                    double siftStartMs = Math.Max(0.0, blackStartMs - 5000.0);
                    double siftEndMs = blackBoundaryMs + 5000.0;
                    BoundaryScan blackGate = this.RefineTemporalBoundary(sourcePath, languagePath, sourceCropPx, languageCropPx, scale, transition, previousPlateau, nextPlateau, diagnostic, 0.0, siftStartMs, siftEndMs, true, true, sourceBlackRuns, languageBlackRuns, cancellationToken);
                    if (blackGate.Accepted && Math.Abs(blackGate.CommonBoundaryMs - blackBoundaryMs) <= 500.0)
                    {
                        diagnostic.CandidateCount = 1;
                        diagnostic.PairedCandidateCount = 1;
                        diagnostic.LastOldCommonFramePtsMs = blackStartMs;
                        diagnostic.FirstNewCommonFramePtsMs = blackBoundaryMs;
                        diagnostic.SelectedCommonBoundaryMs = blackBoundaryMs;
                        diagnostic.BlackRunPaired = true;
                        diagnostic.RejectReason = "";
                        blackGate.CommonBoundaryMs = blackBoundaryMs;
                        blackGate.PreviousOffsetMs = previousPlateau.OffsetMs;
                        blackGate.NextOffsetMs = nextPlateau.OffsetMs;
                        return blackGate;
                    }
                }
            }

            List<byte[]> commonFrames = null;
            List<byte[]> extraFrames = null;
            double[] commonTimestampsMs = null;
            double[] extraTimestampsMs = null;
            long commonExtractionMs = 0;
            long extraExtractionMs = 0;
            System.Threading.Tasks.ParallelOptions extractionOptions = new System.Threading.Tasks.ParallelOptions();
            extractionOptions.MaxDegreeOfParallelism = Math.Min(2, this._maxDegreeOfParallelism);
            extractionOptions.CancellationToken = cancellationToken;
            System.Threading.Tasks.Parallel.Invoke(extractionOptions,
                () =>
                {
                    Stopwatch extractionStopwatch = Stopwatch.StartNew();
                    bool geometryCrop = this._geometryCropResolver(commonPath);
                    this._commonFrameExtractor.ExtractSegment(commonPath, commonStartMs, Math.Max(0.1, (commonEndMs - commonStartMs) / 1000.0), targetFps, geometryCrop, commonCropPx, out commonFrames, out commonTimestampsMs);
                    this._frameNormalizer(commonPath, geometryCrop, commonCropPx, commonFrames);
                    commonExtractionMs = extractionStopwatch.ElapsedMilliseconds;
                },
                () =>
                {
                    Stopwatch extractionStopwatch = Stopwatch.StartNew();
                    bool geometryCrop = this._geometryCropResolver(extraPath);
                    this._extraFrameExtractor.ExtractSegment(extraPath, extraStartMs, Math.Max(0.1, (extraEndMs - extraStartMs) / 1000.0), targetFps, geometryCrop, extraCropPx, out extraFrames, out extraTimestampsMs);
                    this._frameNormalizer(extraPath, geometryCrop, extraCropPx, extraFrames);
                    extraExtractionMs = extractionStopwatch.ElapsedMilliseconds;
                });
            if (sourceGap)
            {
                result.LanguageExtractionMs = commonExtractionMs;
                result.SourceExtractionMs = extraExtractionMs;
            }
            else
            {
                result.SourceExtractionMs = commonExtractionMs;
                result.LanguageExtractionMs = extraExtractionMs;
            }
            if (commonFrames.Count < 4 || extraFrames.Count < 4)
            {
                diagnostic.RejectReason = "Frame insufficienti nel corridoio temporale";
                return result;
            }

            List<DeepSiftVisualAnchor> commonAnchors = this.BuildLocalAnchors(commonFrames, commonTimestampsMs);
            List<DeepSiftVisualAnchor> extraAnchors = this.BuildLocalAnchors(extraFrames, extraTimestampsMs);
            IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors = sourceGap ? extraAnchors : commonAnchors;
            IReadOnlyList<DeepSiftVisualAnchor> languageAnchors = sourceGap ? commonAnchors : extraAnchors;
            double pairToleranceMs = Math.Max(600.0, Math.Max(previousPlateau.UncertaintyMs, nextPlateau.UncertaintyMs) * 2.0);
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<DeepSiftFramePair> pairs = this.BuildDensePairs(sourceAnchors.Count, languageAnchors.Count);
            DeepSiftBatchMatchResult batch = this._batchMatcher.BuildMatrix(sourceAnchors, languageAnchors, this._maxDegreeOfParallelism, cancellationToken, null, pairs);
            if (batch.Cancelled)
                throw new OperationCanceledException(cancellationToken);
            result.MatchingMs = stopwatch.ElapsedMilliseconds;
            LocalTransitionCandidate selected = null;
            string selectionRejectReason;
            diagnostic.CandidateCount = pairs.Count;
            if (!batch.Cancelled && batch.Matrix != null && string.IsNullOrEmpty(batch.RejectReason))
            {
                List<LocalPairPathPoint> points = this.BuildLocalPairPath(batch.AcceptedPairs, sourceGap, scale, previousPlateau.OffsetMs, nextPlateau.OffsetMs, pairToleranceMs);
                points = this.KeepLongestMonotonePairPath(points);
                diagnostic.PairedCandidateCount = points.Count;
                selected = this.ResolveLocalPairTransition(points, previousPlateau, nextPlateau, !forceFullRate, out selectionRejectReason);
            }
            else
            {
                selectionRejectReason = string.IsNullOrEmpty(batch.RejectReason) ? "matching temporale locale non disponibile" : batch.RejectReason;
            }
            if (selected == null)
            {
                if (targetFps > 0.0 && !forceFullRate)
                {
                    double predictedBoundaryMs = sourceGap ? transition.FirstNewLanguagePtsMs : transition.FirstNewSourcePtsMs;
                    double fineRadiusMs = Math.Min(7500.0, Math.Max(1500.0, (nextCommonMs - previousCommonMs) * 0.5));
                    double fineStartMs = Math.Max(0.0, predictedBoundaryMs - fineRadiusMs);
                    double fineEndMs = predictedBoundaryMs + fineRadiusMs;
                    BoundaryScan fine = this.RefineTemporalBoundary(sourcePath, languagePath, sourceCropPx, languageCropPx, scale, transition, previousPlateau, nextPlateau, diagnostic, 0.0, fineStartMs, fineEndMs, true, false, sourceBlackRuns, languageBlackRuns, cancellationToken);
                    fine.SourceExtractionMs += result.SourceExtractionMs;
                    fine.LanguageExtractionMs += result.LanguageExtractionMs;
                    fine.MatchingMs += result.MatchingMs;
                    return fine;
                }
                diagnostic.RejectReason = "Crossover SIFT OLD/NEW locale non univoco: " + selectionRejectReason;
                return result;
            }

            long sceneMatchingMs = 0;
            if (forceFullRate && !skipBlackResolution && this.TryResolveCanonicalSceneBoundary(commonAnchors, selected.CommonBoundaryMs, cancellationToken, out double sceneBoundaryMs, out double scenePreviousFrameMs, out sceneMatchingMs))
            {
                selected.CommonBoundaryMs = sceneBoundaryMs;
                selected.LastOldCommonFramePtsMs = scenePreviousFrameMs;
                selected.FirstNewCommonFramePtsMs = sceneBoundaryMs;
            }
            result.MatchingMs += sceneMatchingMs;

            if (targetFps > 0.0 && !forceFullRate)
            {
                double fineStartMs = Math.Max(0.0, selected.LastOldCommonFramePtsMs - 2000.0);
                double fineEndMs = selected.FirstNewCommonFramePtsMs + 2000.0;
                BoundaryScan fine = this.RefineTemporalBoundary(sourcePath, languagePath, sourceCropPx, languageCropPx, scale, transition, previousPlateau, nextPlateau, diagnostic, 0.0, fineStartMs, fineEndMs, true, false, sourceBlackRuns, languageBlackRuns, cancellationToken);
                fine.SourceExtractionMs += result.SourceExtractionMs;
                fine.LanguageExtractionMs += result.LanguageExtractionMs;
                fine.MatchingMs += result.MatchingMs;
                return fine;
            }

            diagnostic.AcceptedBeforeMatches = selected.BeforeMatchCount;
            diagnostic.AcceptedAfterMatches = selected.AfterMatchCount;
            diagnostic.LastOldCommonFramePtsMs = selected.LastOldCommonFramePtsMs;
            diagnostic.FirstNewCommonFramePtsMs = selected.FirstNewCommonFramePtsMs;
            diagnostic.SelectedCommonBoundaryMs = selected.CommonBoundaryMs;
            diagnostic.RejectReason = "";
            result.Accepted = true;
            result.CommonBoundaryMs = selected.CommonBoundaryMs;
            result.PreviousOffsetMs = selected.PreviousOffsetMs;
            result.NextOffsetMs = selected.NextOffsetMs;
            return result;
        }

        /// <summary>
        /// Risolve un hard cut canonico usando match SIFT tra frame adiacenti già estratti
        /// </summary>
        /// <param name="commonAnchors">Frame full-rate della timeline comune</param>
        /// <param name="crossoverMs">Crossover OLD/NEW risolto dalla matrice locale</param>
        /// <param name="cancellationToken">Token cooperativo</param>
        /// <param name="boundaryMs">Timestamp del primo frame dopo il cambio scena</param>
        /// <param name="previousFrameMs">Timestamp dell'ultimo frame prima del cambio scena</param>
        /// <param name="matchingMs">Tempo impiegato dal batch SIFT adiacente</param>
        /// <returns>True quando esiste un hard cut stabile vicino al crossover</returns>
        private bool TryResolveCanonicalSceneBoundary(List<DeepSiftVisualAnchor> commonAnchors, double crossoverMs, CancellationToken cancellationToken, out double boundaryMs, out double previousFrameMs, out long matchingMs)
        {
            const double maximumDistanceMs = 2000.0;
            boundaryMs = 0.0;
            previousFrameMs = 0.0;
            matchingMs = 0;
            if (commonAnchors.Count < 4)
                return false;

            List<DeepSiftFramePair> adjacentPairs = new List<DeepSiftFramePair>(commonAnchors.Count - 1);
            for (int frameIndex = 1; frameIndex < commonAnchors.Count; frameIndex++)
                adjacentPairs.Add(new DeepSiftFramePair { SourceAnchorIndex = frameIndex - 1, LanguageAnchorIndex = frameIndex });

            Stopwatch stopwatch = Stopwatch.StartNew();
            DeepSiftBatchMatchResult batch = this._batchMatcher.BuildMatrix(commonAnchors, commonAnchors, this._maxDegreeOfParallelism, cancellationToken, null, adjacentPairs);
            matchingMs = stopwatch.ElapsedMilliseconds;
            if (batch.Cancelled)
                throw new OperationCanceledException(cancellationToken);
            if (batch.Matrix == null || !string.IsNullOrEmpty(batch.RejectReason))
                return false;

            for (int frameIndex = 2; frameIndex + 1 < commonAnchors.Count; frameIndex++)
            {
                DeepSiftMatchCell candidate = batch.Matrix.Get(frameIndex - 1, frameIndex);
                if (Math.Abs(commonAnchors[frameIndex].PtsMs - crossoverMs) > maximumDistanceMs || candidate.State != DeepSiftMatchState.Rejected || candidate.InlierCount != 0)
                    continue;
                DeepSiftMatchCell before = batch.Matrix.Get(frameIndex - 2, frameIndex - 1);
                DeepSiftMatchCell after = batch.Matrix.Get(frameIndex, frameIndex + 1);
                if (before.State != DeepSiftMatchState.Accepted || after.State != DeepSiftMatchState.Accepted)
                    continue;
                boundaryMs = commonAnchors[frameIndex].PtsMs;
                previousFrameMs = commonAnchors[frameIndex - 1].PtsMs;
                return true;
            }
            return false;
        }

        private int TryResolveBlackTransition(List<DeepBlackTimelineRun> commonRuns, List<DeepBlackTimelineRun> extraRuns, bool sourceGap, double scale, double oldOffsetMs, double newOffsetMs, double toleranceMs, out double boundaryMs, out double startMs)
        {
            boundaryMs = 0.0;
            startMs = 0.0;
            int pairCount = 0;
            for (int commonIndex = 0; commonIndex < commonRuns.Count; commonIndex++)
            {
                DeepBlackTimelineRun commonRun = commonRuns[commonIndex];
                double expectedExtraStartMs = sourceGap ? (commonRun.StartPtsMs / scale) + oldOffsetMs : (commonRun.StartPtsMs - oldOffsetMs) * scale;
                double expectedExtraEndMs = sourceGap ? (commonRun.EndPtsMs / scale) + newOffsetMs : (commonRun.EndPtsMs - newOffsetMs) * scale;
                double extraToleranceMs = sourceGap ? toleranceMs : toleranceMs * scale;
                for (int extraIndex = 0; extraIndex < extraRuns.Count; extraIndex++)
                {
                    DeepBlackTimelineRun extraRun = extraRuns[extraIndex];
                    if (Math.Abs(extraRun.StartPtsMs - expectedExtraStartMs) > extraToleranceMs || Math.Abs(extraRun.EndPtsMs - expectedExtraEndMs) > extraToleranceMs)
                        continue;
                    pairCount++;
                    boundaryMs = commonRun.EndPtsMs;
                    startMs = commonRun.StartPtsMs;
                }
            }
            return pairCount;
        }

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

        private List<DeepSiftFramePair> BuildDensePairs(int sourceCount, int languageCount)
        {
            List<DeepSiftFramePair> result = new List<DeepSiftFramePair>(checked(sourceCount * languageCount));
            for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
            {
                for (int languageIndex = 0; languageIndex < languageCount; languageIndex++)
                    result.Add(new DeepSiftFramePair { SourceAnchorIndex = sourceIndex, LanguageAnchorIndex = languageIndex });
            }
            return result;
        }

        private List<LocalPairPathPoint> BuildLocalPairPath(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> acceptedPairs, bool sourceGap, double scale, double oldOffsetMs, double newOffsetMs, double toleranceMs)
        {
            List<LocalPairPathPoint> result = new List<LocalPairPathPoint>();
            for (int pairIndex = 0; pairIndex < acceptedPairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = acceptedPairs[pairIndex];
                double offsetMs = pair.SourcePtsMs - (pair.LanguagePtsMs / scale);
                double oldDistanceMs = Math.Abs(offsetMs - oldOffsetMs);
                double newDistanceMs = Math.Abs(offsetMs - newOffsetMs);
                if (Math.Min(oldDistanceMs, newDistanceMs) > toleranceMs || Math.Abs(oldDistanceMs - newDistanceMs) < 1.0)
                    continue;
                LocalPairPathPoint point = new LocalPairPathPoint();
                point.CommonPtsMs = sourceGap ? pair.LanguagePtsMs : pair.SourcePtsMs;
                point.ExtraPtsMs = sourceGap ? pair.SourcePtsMs : pair.LanguagePtsMs;
                point.OffsetMs = offsetMs;
                point.IsNew = newDistanceMs < oldDistanceMs;
                point.Score = pair.Score;
                result.Add(point);
            }
            result.Sort((left, right) =>
            {
                int commonComparison = left.CommonPtsMs.CompareTo(right.CommonPtsMs);
                if (commonComparison != 0)
                    return commonComparison;
                int extraComparison = left.ExtraPtsMs.CompareTo(right.ExtraPtsMs);
                if (extraComparison != 0)
                    return extraComparison;
                return right.Score.CompareTo(left.Score);
            });
            return result;
        }

        private List<LocalPairPathPoint> KeepLongestMonotonePairPath(List<LocalPairPathPoint> points)
        {
            if (points.Count < 2)
                return points;
            int[] lengths = new int[points.Count];
            double[] scores = new double[points.Count];
            int[] previous = new int[points.Count];
            int bestIndex = 0;
            for (int index = 0; index < points.Count; index++)
            {
                lengths[index] = 1;
                scores[index] = points[index].Score;
                previous[index] = -1;
                for (int candidateIndex = 0; candidateIndex < index; candidateIndex++)
                {
                    if (points[candidateIndex].CommonPtsMs >= points[index].CommonPtsMs || points[candidateIndex].ExtraPtsMs >= points[index].ExtraPtsMs)
                        continue;
                    int candidateLength = lengths[candidateIndex] + 1;
                    double candidateScore = scores[candidateIndex] + points[index].Score;
                    if (candidateLength > lengths[index] || (candidateLength == lengths[index] && candidateScore > scores[index]))
                    {
                        lengths[index] = candidateLength;
                        scores[index] = candidateScore;
                        previous[index] = candidateIndex;
                    }
                }
                if (lengths[index] > lengths[bestIndex] || (lengths[index] == lengths[bestIndex] && scores[index] > scores[bestIndex]))
                    bestIndex = index;
            }
            List<LocalPairPathPoint> result = new List<LocalPairPathPoint>(lengths[bestIndex]);
            while (bestIndex >= 0)
            {
                result.Add(points[bestIndex]);
                bestIndex = previous[bestIndex];
            }
            result.Reverse();
            return result;
        }

        private LocalTransitionCandidate ResolveLocalPairTransition(List<LocalPairPathPoint> points, DeepSiftTemporalPlateau previousPlateau, DeepSiftTemporalPlateau nextPlateau, bool coarse, out string rejectReason)
        {
            rejectReason = "";
            if (points.Count < 4)
            {
                rejectReason = "supporto=" + points.Count.ToString(CultureInfo.InvariantCulture);
                return null;
            }
            int totalOld = 0;
            for (int i = 0; i < points.Count; i++)
                totalOld += points[i].IsNew ? 0 : 1;
            int oldBefore = 0;
            int newBefore = 0;
            int bestError = int.MaxValue;
            int bestIndex = -1;
            for (int splitIndex = 1; splitIndex < points.Count; splitIndex++)
            {
                if (points[splitIndex - 1].IsNew)
                    newBefore++;
                else
                    oldBefore++;
                int oldAfter = totalOld - oldBefore;
                int newAfter = points.Count - splitIndex - oldAfter;
                int error = newBefore + oldAfter;
                if (oldBefore < 2 || newAfter < 2)
                    continue;
                if (error < bestError)
                {
                    bestError = error;
                    bestIndex = splitIndex;
                }
            }
            int maximumError = Math.Max(1, coarse ? points.Count / 4 : (int)Math.Floor(points.Count * 0.15));
            if (bestIndex < 0 || bestError > maximumError)
            {
                rejectReason = bestIndex < 0 ? "supporto OLD/NEW insufficiente" : "errori=" + bestError.ToString(CultureInfo.InvariantCulture) + "/" + points.Count.ToString(CultureInfo.InvariantCulture);
                return null;
            }
            int lastOldIndex = bestIndex - 1;
            while (lastOldIndex >= 0 && points[lastOldIndex].IsNew)
                lastOldIndex--;
            int firstNewIndex = bestIndex;
            while (firstNewIndex < points.Count && !points[firstNewIndex].IsNew)
                firstNewIndex++;
            if (lastOldIndex < 0 || firstNewIndex >= points.Count)
            {
                rejectReason = "coppia OLD/NEW assente attorno allo split";
                return null;
            }
            LocalTransitionCandidate result = new LocalTransitionCandidate();
            result.CommonBoundaryMs = points[firstNewIndex].CommonPtsMs;
            result.BeforeMatchCount = bestIndex;
            result.AfterMatchCount = points.Count - bestIndex;
            result.LastOldCommonFramePtsMs = points[lastOldIndex].CommonPtsMs;
            result.FirstNewCommonFramePtsMs = points[firstNewIndex].CommonPtsMs;
            result.PreviousOffsetMs = this.ResolveDominantLocalOffset(points, 0, bestIndex - 1, previousPlateau.OffsetMs, previousPlateau.UncertaintyMs);
            result.NextOffsetMs = this.ResolveDominantLocalOffset(points, bestIndex, points.Count - 1, nextPlateau.OffsetMs, nextPlateau.UncertaintyMs);
            return result;
        }

        private double ResolveDominantLocalOffset(List<LocalPairPathPoint> points, int startIndex, int endIndex, double hypothesisMs, double uncertaintyMs)
        {
            const double CLUSTER_TOLERANCE_MS = 50.0;
            int bestCount = 0;
            double bestScore = double.NegativeInfinity;
            double bestCenterMs = hypothesisMs;
            for (int centerIndex = startIndex; centerIndex <= endIndex; centerIndex++)
            {
                int count = 0;
                double score = 0.0;
                for (int index = startIndex; index <= endIndex; index++)
                {
                    if (Math.Abs(points[index].OffsetMs - points[centerIndex].OffsetMs) > CLUSTER_TOLERANCE_MS)
                        continue;
                    count++;
                    score += points[index].Score;
                }
                if (count > bestCount || (count == bestCount && (score > bestScore || (Math.Abs(score - bestScore) < 0.000001 && Math.Abs(points[centerIndex].OffsetMs - hypothesisMs) < Math.Abs(bestCenterMs - hypothesisMs)))))
                {
                    bestCount = count;
                    bestScore = score;
                    bestCenterMs = points[centerIndex].OffsetMs;
                }
            }
            if (bestCount < 2 || Math.Abs(bestCenterMs - hypothesisMs) > Math.Max(100.0, uncertaintyMs * 1.5))
                return hypothesisMs;
            List<double> offsets = new List<double>();
            for (int index = startIndex; index <= endIndex; index++)
            {
                if (Math.Abs(points[index].OffsetMs - bestCenterMs) <= CLUSTER_TOLERANCE_MS)
                    offsets.Add(points[index].OffsetMs);
            }
            offsets.Sort();
            int middle = offsets.Count / 2;
            return offsets.Count % 2 == 0 ? (offsets[middle - 1] + offsets[middle]) * 0.5 : offsets[middle];
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Costruisce ancore locali con durate PTS reali
        /// </summary>
        private List<DeepSiftVisualAnchor> BuildLocalAnchors(List<byte[]> frames, double[] timestampsMs)
        {
            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                double durationMs = i + 1 < timestampsMs.Length ? timestampsMs[i + 1] - timestampsMs[i] : i > 0 ? timestampsMs[i] - timestampsMs[i - 1] : 40.0;
                DeepSiftVisualAnchor anchor = new DeepSiftVisualAnchor();
                anchor.Index = i;
                anchor.FrameIndex = i;
                anchor.PtsMs = timestampsMs[i];
                anchor.DurationMs = durationMs > 0.0 ? durationMs : 40.0;
                anchor.FrameDurationMs = anchor.DurationMs;
                anchor.Frame = frames[i];
                anchor.Width = this._videoSyncConfig.FrameWidth;
                anchor.Height = this._videoSyncConfig.FrameHeight;
                result.Add(anchor);
            }
            return result;
        }

        /// <summary>
        /// Simula le operazioni e verifica che ricostruiscano i salti di offset dei plateau globali
        /// </summary>
        private bool ValidateEditMap(DeepSiftEditMapResult result, double sourceToLanguageScale, DeepSiftAnchorTimeline sourceTimeline, DeepSiftAnchorTimeline languageTimeline, out string rejectReason)
        {
            rejectReason = "";
            if (sourceToLanguageScale <= 0.0 || double.IsNaN(sourceToLanguageScale) || double.IsInfinity(sourceToLanguageScale))
            {
                rejectReason = "Scala source-language non valida nella validazione EditMap";
                return false;
            }

            if (result.Plateaus.Count != result.EditMap.Operations.Count + 1)
            {
                rejectReason = "Numero plateau e operazioni non coerente";
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
                    rejectReason = "EditMap contiene un'operazione non valida";
                    return false;
                }
                if (!string.Equals(operation.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal) && !string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal))
                {
                    rejectReason = "EditMap contiene un tipo di operazione sconosciuto";
                    return false;
                }
                if (operation.LangTimestampMs < 0 || operation.SourceTimestampMs < 0 || operation.VisualSourceTimestampMs < 0)
                {
                    rejectReason = "EditMap contiene timestamp negativi";
                    return false;
                }
                if (operation.LangTimestampMs < previousLanguageEndMs || operation.SourceTimestampMs < previousSourceTimestampMs)
                {
                    rejectReason = "EditMap contiene operazioni sovrapposte o non ordinate";
                    return false;
                }
                if (operation.SourceTimestampMs > sourceEndMs || operation.LangTimestampMs > languageEndMs || (string.Equals(operation.Type, EditOperation.CUT_SEGMENT, StringComparison.Ordinal) && operation.LangTimestampMs + operation.DurationMs > languageEndMs))
                {
                    rejectReason = "EditMap contiene timestamp oltre la durata della timeline";
                    return false;
                }

                double expectedSourceBeforeMs = (operation.LangTimestampMs / sourceToLanguageScale) + result.Plateaus[0].OffsetMs + cumulativeDeltaMs;
                double coordinateToleranceMs = this.GetPlateauValidationTolerance(result.Plateaus[operationIndex], result.Plateaus[operationIndex + 1]);
                if (Math.Abs(operation.SourceTimestampMs - expectedSourceBeforeMs) > coordinateToleranceMs)
                {
                    rejectReason = "EditMap non proietta coerentemente il boundary language sulla timeline source";
                    return false;
                }

                int sourceDeltaMs = EditMapTimelineHelper.GetSourceOperationDeltaMs(operation, sourceToLanguageScale);
                cumulativeDeltaMs += sourceDeltaMs;
                double expectedDeltaMs = result.Plateaus[operationIndex + 1].OffsetMs - result.Plateaus[0].OffsetMs;
                double actualDeltaMs = cumulativeDeltaMs;
                double toleranceMs = this.GetPlateauValidationTolerance(result.Plateaus[operationIndex], result.Plateaus[operationIndex + 1]);
                if (Math.Abs(actualDeltaMs - expectedDeltaMs) > toleranceMs)
                {
                    rejectReason = "EditMap cumulativa non ricostruisce il salto di offset globale";
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
        private double GetPlateauValidationTolerance(DeepSiftPlateauDiagnostic previous, DeepSiftPlateauDiagnostic next)
        {
            double observedDispersionMs = Math.Max(previous.OffsetDispersionMs, next.OffsetDispersionMs);
            return Math.Max(Math.Max(previous.FrameToleranceMs, next.FrameToleranceMs), observedDispersionMs * 2.0);
        }

        #endregion

        #region Classi annidate

        private class LocalTransitionCandidate
        {
            public double CommonBoundaryMs { get; set; }
            public int BeforeMatchCount { get; set; }
            public int AfterMatchCount { get; set; }
            public double LastOldCommonFramePtsMs { get; set; }
            public double FirstNewCommonFramePtsMs { get; set; }
            public double PreviousOffsetMs { get; set; }
            public double NextOffsetMs { get; set; }
        }

        private class LocalPairPathPoint
        {
            public double CommonPtsMs { get; set; }
            public double ExtraPtsMs { get; set; }
            public double OffsetMs { get; set; }
            public bool IsNew { get; set; }
            public double Score { get; set; }
        }

        private class BoundaryScan
        {
            public bool Accepted { get; set; }
            public double CommonBoundaryMs { get; set; }
            public double PreviousOffsetMs { get; set; }
            public double NextOffsetMs { get; set; }
            public long SourceExtractionMs { get; set; }
            public long LanguageExtractionMs { get; set; }
            public long MatchingMs { get; set; }
        }

        #endregion
    }
}
