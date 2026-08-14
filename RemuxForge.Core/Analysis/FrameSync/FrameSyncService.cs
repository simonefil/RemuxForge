using RemuxForge.Core.Analysis.Deep.Features;
using RemuxForge.Core.Analysis.Deep;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Media.Ffmpeg;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Determina un offset costante tramite SIFT, PTS reali e percorsi temporali monotoni
    /// </summary>
    public sealed class FrameSyncService : VideoSyncServiceBase
    {
        #region Costanti

        /// <summary>
        /// Passo temporale delle ancore NxM iniziali
        /// </summary>
        private const double INITIAL_SAMPLE_INTERVAL_SEC = 1.0;

        /// <summary>
        /// Passo temporale delle ancore locali dei checkpoint
        /// </summary>
        private const double CHECKPOINT_SAMPLE_INTERVAL_SEC = 0.5;

        /// <summary>
        /// Semilarghezza della banda di offset valutata nei checkpoint
        /// </summary>
        private const double CHECKPOINT_OFFSET_CORRIDOR_MS = 1000.0;

        /// <summary>
        /// Copertura temporale minima del percorso iniziale
        /// </summary>
        private const double INITIAL_MINIMUM_COVERAGE_MS = 20000.0;

        /// <summary>
        /// Copertura temporale minima del percorso locale di un checkpoint
        /// </summary>
        private const double CHECKPOINT_MINIMUM_COVERAGE_MS = 2000.0;

        /// <summary>
        /// Durata source della finestra full-rate usata per il refinement finale
        /// </summary>
        private const double PRECISION_SOURCE_DURATION_SEC = 4.0;

        /// <summary>
        /// Durata language della finestra full-rate usata per il refinement finale
        /// </summary>
        private const double PRECISION_LANGUAGE_DURATION_SEC = 6.0;

        /// <summary>
        /// Semilarghezza della banda full-rate intorno all'offset dei checkpoint
        /// </summary>
        private const double PRECISION_OFFSET_CORRIDOR_MS = 600.0;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Configurazione FrameSync applicata alla sessione
        /// </summary>
        private readonly FrameSyncConfig _frameSyncConfig;

        /// <summary>
        /// Resolver dei modi SIFT a offset costante
        /// </summary>
        private readonly FrameSyncSiftTemporalResolver _temporalResolver;

        /// <summary>
        /// Tempo dell'ultima esecuzione FrameSync
        /// </summary>
        private long _frameSyncTimeMs;

        /// <summary>
        /// Ultimo risultato diagnostico FrameSync
        /// </summary>
        private FrameSyncResult _lastResult;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza il servizio con il percorso FFmpeg risolto
        /// </summary>
        /// <param name="ffmpegPath">Percorso dell'eseguibile FFmpeg</param>
        public FrameSyncService(string ffmpegPath) : base(ffmpegPath, LogSection.FrameSync)
        {
            if (string.IsNullOrEmpty(ffmpegPath))
                throw new ArgumentException(AppText.T("analysis.sift.missingFfmpegPath"), nameof(ffmpegPath));
            this._frameSyncConfig = AppSettingsService.Instance.Settings.Advanced.FrameSync;
            this._temporalResolver = new FrameSyncSiftTemporalResolver();
            this._lastResult = new FrameSyncResult();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Determina e verifica un offset costante tramite ricerca SIFT iniziale e checkpoint locali
        /// </summary>
        /// <param name="sourceFile">Percorso del video sorgente</param>
        /// <param name="languageFile">Percorso del video lingua</param>
        /// <returns>Offset da applicare in millisecondi oppure <see cref="int.MinValue"/></returns>
        public int RefineOffset(string sourceFile, string languageFile)
        {
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            FrameSyncResult result = new FrameSyncResult();
            FrameSyncTimingInfo timing = result.Timing;
            bool originalSourceGeometryCrop = this._geometryCropSourceToFourThree;
            bool originalLanguageGeometryCrop = this._geometryCropLanguageToFourThree;
            int finalOffset = int.MinValue;

            try
            {
                if (!this.TryPrepare(sourceFile, languageFile, result, timing, out int durationMs))
                    return int.MinValue;

                AdvancedConfig advanced = AppSettingsService.Instance.Settings.Advanced;
                SiftBackendKind backend = advanced.GetSiftBackendKind();
                using (FrameFeatureBatchMatcherBase matcher = FrameFeatureBatchMatcherBase.Create(backend))
                {
                    if (!matcher.IsAvailable(out string rejectReason))
                    {
                        result.FailureReason = rejectReason;
                        return int.MinValue;
                    }

                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, AppText.T("framesync.sift.initialPhase"));
                    ConsoleHelper.Progress(LogSection.FrameSync, 18, AppText.T("framesync.sift.initialProgress"));
                    FrameSyncCandidate initial = this.ResolveInitial(sourceFile, languageFile, matcher, result, timing);
                    if (initial == null)
                        return int.MinValue;

                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Success, AppText.F("framesync.sift.initialOffset", Utils.FormatDelay(initial.OffsetMs), initial.StrongPairCount, initial.ProcessedPairCount));
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, AppText.F("framesync.sift.checkpointPhase", this._vsConfig.NumCheckPoints));
                    ConsoleHelper.Progress(LogSection.FrameSync, 58, AppText.T("framesync.sift.checkpointProgress"));
                    this.ResolveCheckpoints(sourceFile, languageFile, durationMs, initial, matcher, result, timing);
                    finalOffset = this.FinalizeOffset(sourceFile, languageFile, durationMs, initial, matcher, result, timing);
                }
                return finalOffset;
            }
            catch (Exception ex)
            {
                result.FailureReason = AppText.F("framesync.sift.failed", ex.Message);
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Error, result.FailureReason);
                return int.MinValue;
            }
            finally
            {
                totalStopwatch.Stop();
                timing.TotalMs = totalStopwatch.ElapsedMilliseconds;
                this._frameSyncTimeMs = timing.TotalMs;
                this._lastResult = result;
                this._geometryCropSourceToFourThree = originalSourceGeometryCrop;
                this._geometryCropLanguageToFourThree = originalLanguageGeometryCrop;
            }
        }

        /// <summary>
        /// Restituisce un riepilogo compatto dei checkpoint SIFT
        /// </summary>
        /// <returns>Riepilogo localizzato dell'ultima esecuzione</returns>
        public string GetDetailSummary()
        {
            int accepted = 0;
            for (int pointIndex = 0; pointIndex < this._lastResult.Points.Count; pointIndex++)
            {
                if (this._lastResult.Points[pointIndex].Accepted)
                    accepted++;
            }
            return AppText.F("framesync.sift.summary", accepted, this._lastResult.Points.Count, this._lastResult.Timing.InitialPairCount, this._lastResult.Timing.CheckpointPairCount, this._lastResult.Timing.PrecisionPairCount);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Legge la durata e prepara la geometria comune alle estrazioni SIFT
        /// </summary>
        private bool TryPrepare(string sourceFile, string languageFile, FrameSyncResult result, FrameSyncTimingInfo timing, out int durationMs)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            FfmpegVideoInfoReader reader = new FfmpegVideoInfoReader(this._ffmpegPath, this._ffmpegConfig, LogSection.FrameSync);
            bool infoAvailable = reader.TryRead(sourceFile, out durationMs, out _);
            stopwatch.Stop();
            timing.VideoInfoMs = stopwatch.ElapsedMilliseconds;
            if (!infoAvailable || durationMs < this._frameSyncConfig.MinDurationMs)
            {
                result.FailureReason = AppText.T("framesync.sift.videoInfoUnavailable");
                return false;
            }

            stopwatch.Restart();
            this.PrepareGeometryDrivenCrop(sourceFile, languageFile);
            stopwatch.Stop();
            timing.GeometryMs = stopwatch.ElapsedMilliseconds;
            result.SourceGeometry = this._lastSourceGeometryInfo;
            result.LanguageGeometry = this._lastLanguageGeometryInfo;
            return true;
        }

        /// <summary>
        /// Estrae le finestre iniziali e risolve il modo SIFT monotono dominante
        /// </summary>
        private FrameSyncCandidate ResolveInitial(string sourceFile, string languageFile, FrameFeatureBatchMatcherBase matcher, FrameSyncResult result, FrameSyncTimingInfo timing)
        {
            Stopwatch phaseStopwatch = Stopwatch.StartNew();
            int sourceStartMs = this._frameSyncConfig.SourceStartSec * 1000;
            this.ExtractSegmentAtInterval(sourceFile, sourceStartMs, this._frameSyncConfig.SourceDurationSec, INITIAL_SAMPLE_INTERVAL_SEC, this._geometryCropSourceToFourThree, this._analysisCropSourcePx, out List<byte[]> sourceFrames, out double[] sourcePtsMs);
            this.ExtractSegmentAtInterval(languageFile, 0, this._frameSyncConfig.LangDurationSec, INITIAL_SAMPLE_INTERVAL_SEC, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx, out List<byte[]> languageFrames, out double[] languagePtsMs);
            timing.InitialExtractMs = phaseStopwatch.ElapsedMilliseconds;

            List<DeepSiftVisualAnchor> sourceAnchors = this.BuildAnchors(sourceFrames, sourcePtsMs);
            List<DeepSiftVisualAnchor> languageAnchors = this.BuildAnchors(languageFrames, languagePtsMs);
            phaseStopwatch.Restart();
            DeepSiftBatchMatchResult batch = matcher.BuildMatrix(sourceAnchors, languageAnchors, ParallelismHelper.ResolveDefaultMaxDegree(), CancellationToken.None);
            timing.InitialMatchMs = phaseStopwatch.ElapsedMilliseconds;
            timing.InitialSearchMs = timing.InitialExtractMs + timing.InitialMatchMs;
            timing.InitialPairCount = batch != null ? batch.ProcessedCellCount : 0;
            if (batch == null || batch.Cancelled || !string.IsNullOrEmpty(batch.RejectReason))
            {
                result.Initial.FailureReason = batch != null ? batch.RejectReason : AppText.T("framesync.sift.initialUnavailable");
                result.FailureReason = result.Initial.FailureReason;
                return null;
            }

            List<FrameSyncCandidate> candidates = this._temporalResolver.Resolve(batch.AcceptedPairs, batch.BackendName, batch.ProcessedCellCount);
            result.Initial.Candidates.AddRange(candidates);
            if (candidates.Count == 0 || Math.Min(candidates[0].SourceCoverageMs, candidates[0].LanguageCoverageMs) < INITIAL_MINIMUM_COVERAGE_MS)
            {
                result.Initial.FailureReason = AppText.T("framesync.sift.initialUnsupported");
                result.FailureReason = result.Initial.FailureReason;
                return null;
            }
            if (!this._temporalResolver.IsBestCandidateUnique(candidates))
            {
                result.Initial.Ambiguous = true;
                result.Initial.FailureReason = AppText.T("framesync.sift.initialAmbiguous");
                result.FailureReason = result.Initial.FailureReason;
                result.Ambiguous = true;
                return null;
            }

            result.Initial.Success = true;
            result.Initial.BestCandidate = candidates[0];
            return candidates[0];
        }

        /// <summary>
        /// Verifica l'offset iniziale in checkpoint distribuiti usando sole coppie nella banda PTS locale
        /// </summary>
        private void ResolveCheckpoints(string sourceFile, string languageFile, int durationMs, FrameSyncCandidate initial, FrameFeatureBatchMatcherBase matcher, FrameSyncResult result, FrameSyncTimingInfo timing)
        {
            Stopwatch checkpointsStopwatch = Stopwatch.StartNew();
            for (int pointIndex = 0; pointIndex < this._vsConfig.NumCheckPoints; pointIndex++)
            {
                int percentage = (int)Math.Round((pointIndex + 1) * 100.0 / (this._vsConfig.NumCheckPoints + 1));
                int sourceCenterMs = (int)Math.Round(durationMs * percentage / 100.0);
                FrameSyncPointResult point = this.ResolveCheckpoint(sourceFile, languageFile, sourceCenterMs, percentage, initial.OffsetMs, matcher);
                result.Points.Add(point);
                timing.CheckpointExtractMs += point.ExtractMs;
                timing.CheckpointMatchMs += point.MatchMs;
                timing.CheckpointPairCount += point.ProcessedPairCount;
                ConsoleHelper.Write(LogSection.FrameSync, point.Accepted ? LogLevel.Debug : LogLevel.Notice, AppText.F("framesync.sift.checkpointResult", percentage, point.Accepted ? AppText.T("framesync.sift.accepted") : AppText.T("framesync.sift.rejected"), point.BestOffsetMs == int.MinValue ? "-" : Utils.FormatDelay(point.BestOffsetMs), point.StrongPairCount, point.ProcessedPairCount));
            }
            checkpointsStopwatch.Stop();
            timing.CheckpointsMs = checkpointsStopwatch.ElapsedMilliseconds;
        }

        /// <summary>
        /// Risolve un singolo checkpoint SIFT nel corridoio dell'offset atteso
        /// </summary>
        private FrameSyncPointResult ResolveCheckpoint(string sourceFile, string languageFile, int sourceCenterMs, int percentage, int expectedOffsetMs, FrameFeatureBatchMatcherBase matcher)
        {
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            Stopwatch phaseStopwatch = Stopwatch.StartNew();
            FrameSyncPointResult result = new FrameSyncPointResult();
            result.CheckpointPercent = percentage;
            result.ExpectedOffsetMs = expectedOffsetMs;
            result.Backend = matcher.BackendName;
            int sourceDurationMs = this._vsConfig.VerifySourceDurationSec * 1000;
            int languageDurationMs = this._vsConfig.VerifyLangDurationSec * 1000;
            int sourceStartMs = Math.Max(0, sourceCenterMs - (sourceDurationMs / 2));
            int expectedLanguageCenterMs = sourceCenterMs - expectedOffsetMs;
            int languageStartMs = Math.Max(0, expectedLanguageCenterMs - (languageDurationMs / 2));
            this.ExtractSegmentAtInterval(sourceFile, sourceStartMs, this._vsConfig.VerifySourceDurationSec, CHECKPOINT_SAMPLE_INTERVAL_SEC, this._geometryCropSourceToFourThree, this._analysisCropSourcePx, out List<byte[]> sourceFrames, out double[] sourcePtsMs);
            this.ExtractSegmentAtInterval(languageFile, languageStartMs, this._vsConfig.VerifyLangDurationSec, CHECKPOINT_SAMPLE_INTERVAL_SEC, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx, out List<byte[]> languageFrames, out double[] languagePtsMs);
            result.ExtractMs = phaseStopwatch.ElapsedMilliseconds;

            List<DeepSiftVisualAnchor> sourceAnchors = this.BuildAnchors(sourceFrames, sourcePtsMs);
            List<DeepSiftVisualAnchor> languageAnchors = this.BuildAnchors(languageFrames, languagePtsMs);
            List<DeepSiftFramePair> plannedPairs = this.BuildOffsetBandPairs(sourceAnchors, languageAnchors, expectedOffsetMs, CHECKPOINT_OFFSET_CORRIDOR_MS);
            if (plannedPairs.Count == 0)
            {
                result.RejectReason = AppText.T("framesync.sift.checkpointWithoutPairs");
                result.TimingMs = totalStopwatch.ElapsedMilliseconds;
                return result;
            }

            phaseStopwatch.Restart();
            DeepSiftBatchMatchResult batch = matcher.BuildMatrix(sourceAnchors, languageAnchors, ParallelismHelper.ResolveDefaultMaxDegree(), CancellationToken.None, null, plannedPairs);
            result.MatchMs = phaseStopwatch.ElapsedMilliseconds;
            result.ProcessedPairCount = batch != null ? batch.ProcessedCellCount : 0;
            if (batch == null || batch.Cancelled || !string.IsNullOrEmpty(batch.RejectReason))
            {
                result.RejectReason = batch != null ? batch.RejectReason : AppText.T("framesync.sift.checkpointUnavailable");
                result.TimingMs = totalStopwatch.ElapsedMilliseconds;
                return result;
            }

            List<FrameSyncCandidate> candidates = this._temporalResolver.Resolve(batch.AcceptedPairs, batch.BackendName, batch.ProcessedCellCount);
            if (candidates.Count == 0 || !this._temporalResolver.IsBestCandidateUnique(candidates))
            {
                result.RejectReason = AppText.T("framesync.sift.checkpointAmbiguous");
                result.TimingMs = totalStopwatch.ElapsedMilliseconds;
                return result;
            }

            FrameSyncCandidate best = candidates[0];
            result.BestOffsetMs = best.OffsetMs;
            result.BestScore = best.MeanScore;
            result.DispersionMs = best.DispersionMs;
            result.AcceptedPairCount = best.AcceptedPairCount;
            result.StrongPairCount = best.StrongPairCount;
            result.SourceCoverageMs = best.SourceCoverageMs;
            result.LanguageCoverageMs = best.LanguageCoverageMs;
            if (Math.Min(best.SourceCoverageMs, best.LanguageCoverageMs) < CHECKPOINT_MINIMUM_COVERAGE_MS)
                result.RejectReason = AppText.T("framesync.sift.checkpointUnsupported");
            else if (Math.Abs(best.OffsetMs - expectedOffsetMs) > CHECKPOINT_OFFSET_CORRIDOR_MS)
                result.RejectReason = AppText.T("framesync.sift.checkpointDrift");
            else
                result.Accepted = true;
            result.TimingMs = totalStopwatch.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// Conclude FrameSync richiedendo checkpoint sufficienti e coerenti con un solo offset costante
        /// </summary>
        private int FinalizeOffset(string sourceFile, string languageFile, int durationMs, FrameSyncCandidate initial, FrameFeatureBatchMatcherBase matcher, FrameSyncResult result, FrameSyncTimingInfo timing)
        {
            List<int> offsets = new List<int>();
            double scoreSum = 0.0;
            for (int pointIndex = 0; pointIndex < result.Points.Count; pointIndex++)
            {
                FrameSyncPointResult point = result.Points[pointIndex];
                if (!point.Accepted)
                    continue;
                offsets.Add(point.BestOffsetMs);
                scoreSum += point.BestScore;
            }
            if (offsets.Count < this._frameSyncConfig.MinValidPoints)
            {
                result.FailureReason = AppText.F("framesync.sift.insufficientCheckpoints", offsets.Count, this._frameSyncConfig.MinValidPoints);
                return int.MinValue;
            }

            offsets.Sort();
            int middle = offsets.Count / 2;
            int coarseOffset = offsets.Count % 2 == 0 ? (int)Math.Round((offsets[middle - 1] + offsets[middle]) * 0.5) : offsets[middle];
            for (int offsetIndex = 0; offsetIndex < offsets.Count; offsetIndex++)
            {
                if (Math.Abs(offsets[offsetIndex] - coarseOffset) > CHECKPOINT_OFFSET_CORRIDOR_MS)
                {
                    result.Ambiguous = true;
                    result.FailureReason = AppText.T("framesync.sift.inconsistentCheckpoints");
                    return int.MinValue;
                }
            }
            if (Math.Abs(coarseOffset - initial.OffsetMs) > CHECKPOINT_OFFSET_CORRIDOR_MS)
            {
                result.Ambiguous = true;
                result.FailureReason = AppText.T("framesync.sift.initialCheckpointDrift");
                return int.MinValue;
            }

            result.Confidence = scoreSum / offsets.Count;
            if (result.Confidence < this._frameSyncConfig.FinalMinConfidence)
            {
                result.FailureReason = AppText.F("framesync.sift.insufficientConfidence", result.Confidence.ToString("P0", CultureInfo.InvariantCulture), this._frameSyncConfig.FinalMinConfidence.ToString("P0", CultureInfo.InvariantCulture));
                return int.MinValue;
            }

            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, AppText.T("framesync.sift.precisionPhase"));
            ConsoleHelper.Progress(LogSection.FrameSync, 72, AppText.T("framesync.sift.precisionProgress"));
            FrameSyncCandidate precision = this.ResolvePrecisionOffset(sourceFile, languageFile, durationMs, coarseOffset, matcher, result, timing);
            if (precision == null)
                return int.MinValue;

            result.OffsetMs = precision.OffsetMs;
            result.InitialToFinalDeltaMs = Math.Abs(precision.OffsetMs - initial.OffsetMs);
            result.Confidence = ((scoreSum + precision.MeanScore) / (offsets.Count + 1));
            result.Success = result.Confidence >= this._frameSyncConfig.FinalMinConfidence;
            if (!result.Success)
            {
                result.FailureReason = AppText.F("framesync.sift.insufficientConfidence", result.Confidence.ToString("P0", CultureInfo.InvariantCulture), this._frameSyncConfig.FinalMinConfidence.ToString("P0", CultureInfo.InvariantCulture));
                return int.MinValue;
            }
            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Success, AppText.F("framesync.sift.precisionOffset", Utils.FormatDelay(precision.OffsetMs), result.PrecisionCheckpointPercent, precision.StrongPairCount, precision.ProcessedPairCount));
            return precision.OffsetMs;
        }

        /// <summary>
        /// Risolve l'offset al frame usando in ordine i checkpoint più informativi
        /// </summary>
        private FrameSyncCandidate ResolvePrecisionOffset(string sourceFile, string languageFile, int durationMs, int coarseOffsetMs, FrameFeatureBatchMatcherBase matcher, FrameSyncResult result, FrameSyncTimingInfo timing)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<FrameSyncPointResult> points = new List<FrameSyncPointResult>();
            for (int pointIndex = 0; pointIndex < result.Points.Count; pointIndex++)
            {
                if (result.Points[pointIndex].Accepted)
                    points.Add(result.Points[pointIndex]);
            }
            points.Sort(this.ComparePrecisionPoints);

            string lastRejectReason = AppText.T("framesync.sift.precisionUnavailable");
            for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                FrameSyncPointResult point = points[pointIndex];
                int sourceCenterMs = (int)Math.Round(durationMs * point.CheckpointPercent / 100.0);
                FrameSyncCandidate candidate = this.ResolvePrecisionCandidate(sourceFile, languageFile, sourceCenterMs, coarseOffsetMs, matcher, timing, out string rejectReason);
                if (candidate != null)
                {
                    result.PrecisionCandidate = candidate;
                    result.PrecisionCheckpointPercent = point.CheckpointPercent;
                    stopwatch.Stop();
                    timing.PrecisionRefinementMs = stopwatch.ElapsedMilliseconds;
                    return candidate;
                }

                lastRejectReason = rejectReason;
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, AppText.F("framesync.sift.precisionAttemptRejected", point.CheckpointPercent, rejectReason));
            }

            stopwatch.Stop();
            timing.PrecisionRefinementMs = stopwatch.ElapsedMilliseconds;
            result.FailureReason = AppText.F("framesync.sift.precisionFailed", lastRejectReason);
            return null;
        }

        /// <summary>
        /// Esegue un refinement full-rate nella banda del checkpoint selezionato
        /// </summary>
        private FrameSyncCandidate ResolvePrecisionCandidate(string sourceFile, string languageFile, int sourceCenterMs, int expectedOffsetMs, FrameFeatureBatchMatcherBase matcher, FrameSyncTimingInfo timing, out string rejectReason)
        {
            Stopwatch phaseStopwatch = Stopwatch.StartNew();
            int sourceStartMs = Math.Max(0, sourceCenterMs - (int)Math.Round(PRECISION_SOURCE_DURATION_SEC * 500.0));
            int expectedLanguageCenterMs = sourceCenterMs - expectedOffsetMs;
            int languageStartMs = Math.Max(0, expectedLanguageCenterMs - (int)Math.Round(PRECISION_LANGUAGE_DURATION_SEC * 500.0));
            this.ExtractSegment(sourceFile, sourceStartMs, PRECISION_SOURCE_DURATION_SEC, 0.0, this._geometryCropSourceToFourThree, this._analysisCropSourcePx, out List<byte[]> sourceFrames, out double[] sourcePtsMs);
            this.ExtractSegment(languageFile, languageStartMs, PRECISION_LANGUAGE_DURATION_SEC, 0.0, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx, out List<byte[]> languageFrames, out double[] languagePtsMs);
            timing.PrecisionExtractMs += phaseStopwatch.ElapsedMilliseconds;

            List<DeepSiftVisualAnchor> sourceAnchors = this.BuildAnchors(sourceFrames, sourcePtsMs);
            List<DeepSiftVisualAnchor> languageAnchors = this.BuildAnchors(languageFrames, languagePtsMs);
            List<DeepSiftFramePair> plannedPairs = this.BuildOffsetBandPairs(sourceAnchors, languageAnchors, expectedOffsetMs, PRECISION_OFFSET_CORRIDOR_MS);
            if (plannedPairs.Count == 0)
            {
                rejectReason = AppText.T("framesync.sift.checkpointWithoutPairs");
                return null;
            }

            phaseStopwatch.Restart();
            DeepSiftBatchMatchResult batch = matcher.BuildMatrix(sourceAnchors, languageAnchors, ParallelismHelper.ResolveDefaultMaxDegree(), CancellationToken.None, null, plannedPairs);
            timing.PrecisionMatchMs += phaseStopwatch.ElapsedMilliseconds;
            timing.PrecisionPairCount += batch != null ? batch.ProcessedCellCount : 0;
            if (batch == null || batch.Cancelled || !string.IsNullOrEmpty(batch.RejectReason))
            {
                rejectReason = batch != null && !string.IsNullOrEmpty(batch.RejectReason) ? batch.RejectReason : AppText.T("framesync.sift.checkpointUnavailable");
                return null;
            }

            List<FrameSyncCandidate> candidates = this._temporalResolver.Resolve(batch.AcceptedPairs, batch.BackendName, batch.ProcessedCellCount);
            if (candidates.Count == 0 || !this._temporalResolver.IsBestCandidateUnique(candidates))
            {
                rejectReason = AppText.T("framesync.sift.checkpointAmbiguous");
                return null;
            }

            FrameSyncCandidate best = candidates[0];
            if (Math.Min(best.SourceCoverageMs, best.LanguageCoverageMs) < CHECKPOINT_MINIMUM_COVERAGE_MS)
            {
                rejectReason = AppText.T("framesync.sift.checkpointUnsupported");
                return null;
            }
            if (Math.Abs(best.OffsetMs - expectedOffsetMs) > PRECISION_OFFSET_CORRIDOR_MS)
            {
                rejectReason = AppText.T("framesync.sift.checkpointDrift");
                return null;
            }

            rejectReason = "";
            return best;
        }

        /// <summary>
        /// Ordina i checkpoint per supporto, copertura, confidence e posizione
        /// </summary>
        private int ComparePrecisionPoints(FrameSyncPointResult left, FrameSyncPointResult right)
        {
            int comparison = right.StrongPairCount.CompareTo(left.StrongPairCount);
            if (comparison != 0)
                return comparison;
            comparison = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(Math.Min(right.SourceCoverageMs, right.LanguageCoverageMs)).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(Math.Min(left.SourceCoverageMs, left.LanguageCoverageMs)));
            if (comparison != 0)
                return comparison;
            comparison = DeepSiftTemporalMetricComparer.QuantizeMetric(right.BestScore).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMetric(left.BestScore));
            return comparison != 0 ? comparison : left.CheckpointPercent.CompareTo(right.CheckpointPercent);
        }

        /// <summary>
        /// Costruisce ancore SIFT conservando PTS e durata di campionamento reali
        /// </summary>
        private List<DeepSiftVisualAnchor> BuildAnchors(List<byte[]> frames, double[] timestampsMs)
        {
            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>();
            if (frames == null || timestampsMs == null)
                return result;
            int count = Math.Min(frames.Count, timestampsMs.Length);
            for (int frameIndex = 0; frameIndex < count; frameIndex++)
            {
                double durationMs = frameIndex + 1 < count ? timestampsMs[frameIndex + 1] - timestampsMs[frameIndex] : frameIndex > 0 ? timestampsMs[frameIndex] - timestampsMs[frameIndex - 1] : INITIAL_SAMPLE_INTERVAL_SEC * 1000.0;
                DeepSiftVisualAnchor anchor = new DeepSiftVisualAnchor();
                anchor.Index = frameIndex;
                anchor.FrameIndex = frameIndex;
                anchor.PtsMs = timestampsMs[frameIndex];
                anchor.DurationMs = durationMs > 0.0 ? durationMs : 1.0;
                anchor.FrameDurationMs = anchor.DurationMs;
                anchor.Frame = frames[frameIndex];
                anchor.Width = this._vsConfig.FrameWidth;
                anchor.Height = this._vsConfig.FrameHeight;
                result.Add(anchor);
            }
            return result;
        }

        /// <summary>
        /// Pianifica le sole coppie comprese nella banda dell'offset atteso
        /// </summary>
        private List<DeepSiftFramePair> BuildOffsetBandPairs(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors, double expectedOffsetMs, double corridorMs)
        {
            List<DeepSiftFramePair> result = new List<DeepSiftFramePair>();
            int languageStartIndex = 0;
            for (int sourceIndex = 0; sourceIndex < sourceAnchors.Count; sourceIndex++)
            {
                double minimumLanguagePtsMs = sourceAnchors[sourceIndex].PtsMs - expectedOffsetMs - corridorMs;
                double maximumLanguagePtsMs = sourceAnchors[sourceIndex].PtsMs - expectedOffsetMs + corridorMs;
                while (languageStartIndex < languageAnchors.Count && languageAnchors[languageStartIndex].PtsMs < minimumLanguagePtsMs)
                    languageStartIndex++;
                int languageIndex = languageStartIndex;
                while (languageIndex < languageAnchors.Count && languageAnchors[languageIndex].PtsMs <= maximumLanguagePtsMs)
                {
                    DeepSiftFramePair pair = new DeepSiftFramePair();
                    pair.SourceAnchorIndex = sourceIndex;
                    pair.LanguageAnchorIndex = languageIndex;
                    result.Add(pair);
                    languageIndex++;
                }
            }
            return result;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Tempo dell'ultima esecuzione FrameSync
        /// </summary>
        public long FrameSyncTimeMs { get { return this._frameSyncTimeMs; } }

        /// <summary>
        /// Ultimo risultato diagnostico FrameSync
        /// </summary>
        public FrameSyncResult LastResult { get { return this._lastResult; } }

        #endregion
    }
}
