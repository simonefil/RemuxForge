using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media;
using RemuxForge.Core.Media.Ffmpeg;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Sincronizzazione tramite confronto visivo frame-by-frame
    /// </summary>
    public class FrameSyncService : VideoSyncServiceBase
    {
        #region Costanti

        /// <summary>
        /// Numero massimo di cluster candidati da verificare nella ricerca iniziale
        /// </summary>
        private const int MAX_INITIAL_CANDIDATES = 12;

        /// <summary>
        /// Finestra minima clustering initial in millisecondi
        /// Su VFR/anime i cut equivalenti possono cadere su PTS distanti più di un frame medio
        /// </summary>
        private const int INITIAL_CLUSTER_TOLERANCE_MIN_MS = 150;

        /// <summary>
        /// Tolleranza minima per associare cut source/lang nella verifica initial
        /// </summary>
        private const int INITIAL_NEAREST_TOLERANCE_MIN_MS = 250;

        /// <summary>
        /// Tolleranza minima di consistenza dei cut verificati in initial
        /// </summary>
        private const int INITIAL_CONSISTENCY_TOLERANCE_MIN_MS = 120;

        /// <summary>
        /// Numero massimo di match fingerprint considerati per ogni cut sorgente
        /// </summary>
        private const int INITIAL_FINGERPRINT_TOP_MATCHES_PER_CUT = 3;

        /// <summary>
        /// Correlazione minima per trasformare un match fingerprint in candidato initial
        /// La verifica forte visuale resta obbligatoria prima dell'accettazione
        /// </summary>
        private const double INITIAL_FINGERPRINT_MIN_CORRELATION = 0.72;

        /// <summary>
        /// FPS usato solo dal fallback visuale esteso quando i tagli scena non bastano
        /// </summary>
        private const double VISUAL_SCAN_FPS = 4.0;

        /// <summary>
        /// Durata sorgente del fallback visuale esteso
        /// </summary>
        private const int VISUAL_SCAN_SOURCE_DURATION_SEC = 240;

        /// <summary>
        /// Durata lingua del fallback visuale esteso
        /// </summary>
        private const int VISUAL_SCAN_LANG_DURATION_SEC = 300;

        /// <summary>
        /// Passo offset del fallback visuale esteso
        /// </summary>
        private const int VISUAL_SCAN_OFFSET_STEP_MS = 250;

        /// <summary>
        /// Numero massimo di campioni informativi usati dal fallback visuale
        /// </summary>
        private const int VISUAL_SCAN_MAX_SAMPLES = 24;

        /// <summary>
        /// Score minimo del fallback visuale quando il margine è molto netto
        /// L'offset resta comunque soggetto alla verifica finale sui checkpoint
        /// </summary>
        private const double VISUAL_SCAN_PROMISING_MIN_SCORE = 0.30;

        /// <summary>
        /// Margine richiesto per accettare un fallback visuale a score basso come proposta iniziale
        /// </summary>
        private const double VISUAL_SCAN_PROMISING_MIN_MARGIN = 0.08;

        /// <summary>
        /// Numero di offset migliori da rivalutare con score completo dopo il ranking veloce descriptor-only
        /// </summary>
        private const int VISUAL_SCAN_FAST_TOP_CANDIDATES = 16;

        /// <summary>
        /// Numero massimo di campioni usati dalla ricerca locale visuale sui checkpoint
        /// </summary>
        private const int CHECKPOINT_LOCAL_MAX_SAMPLES = 12;

        /// <summary>
        /// Numero minimo di campioni per accettare una ricerca locale checkpoint
        /// </summary>
        private const int CHECKPOINT_LOCAL_MIN_SAMPLES = 5;

        /// <summary>
        /// Step grossolano ricerca locale checkpoint in millisecondi
        /// </summary>
        private const int CHECKPOINT_LOCAL_COARSE_STEP_MS = 200;

        /// <summary>
        /// Numero massimo processi ffmpeg video simultanei durante FrameSync
        /// ffmpeg usa già thread interni, quindi evitare oversubscription eccessiva
        /// </summary>
        private const int MAX_CONCURRENT_VIDEO_EXTRACTS = 4;

        /// <summary>
        /// Score minimo per preferire l'offset iniziale quando un checkpoint locale cade su frame simili ma driftati
        /// </summary>
        private const double CHECKPOINT_INITIAL_ANCHOR_MIN_SCORE = 0.70;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Riferimento alla configurazione FrameSync (binding diretto, modifiche immediate)
        /// </summary>
        private readonly FrameSyncConfig _fsConfig;

        /// <summary>
        /// Offset raffinato per ciascun punto di verifica
        /// </summary>
        private readonly int[] _offsets;

        /// <summary>
        /// SSIM per ciascun punto di verifica
        /// </summary>
        private readonly double[] _ssimValues;

        /// <summary>
        /// Validità per ciascun punto di verifica
        /// </summary>
        private readonly bool[] _pointValid;

        /// <summary>
        /// Risultati dettagliati per ciascun punto di verifica
        /// </summary>
        private readonly FrameSyncPointResult[] _pointResults;

        /// <summary>
        /// Tempo di esecuzione totale in ms
        /// </summary>
        private long _frameSyncTimeMs;

        /// <summary>
        /// Ultimo risultato dettagliato frame-sync
        /// </summary>
        private FrameSyncResult _lastResult;

        /// <summary>
        /// Ultimo risultato della ricerca iniziale
        /// </summary>
        private FrameSyncInitialResult _lastInitialResult;

        /// <summary>
        /// Timing diagnostici ultima sincronizzazione
        /// </summary>
        private FrameSyncTimingInfo _timing;

        /// <summary>
        /// Ultima geometria sorgente analizzata
        /// </summary>
        private FrameSyncGeometryInfo _lastSourceGeometry;

        /// <summary>
        /// Ultima geometria lingua analizzata
        /// </summary>
        private FrameSyncGeometryInfo _lastLanguageGeometry;

        /// <summary>
        /// Ultimo risultato fingerprint audio globale
        /// </summary>
        private AudioGlobalFingerprintResult _lastAudioGlobalResult;

        /// <summary>
        /// Cache segmenti estratti riusabili per profilo
        /// </summary>
        private readonly FrameSyncSegmentCache _segmentCache;

        /// <summary>
        /// Scoring candidati FrameSync
        /// </summary>
        private readonly FrameSyncCandidateScorer _candidateScorer;

        /// <summary>
        /// Builder risultato dettagliato FrameSync
        /// </summary>
        private readonly FrameSyncResultBuilder _resultBuilder;

        /// <summary>
        /// Resolver initial tramite audio globale
        /// </summary>
        private readonly FrameSyncAudioInitialResolver _audioInitialResolver;

        /// <summary>
        /// Builder candidati offset scene-cut
        /// </summary>
        private readonly FrameSyncOffsetCandidateBuilder _offsetCandidateBuilder;

        /// <summary>
        /// Matcher descriptor visual scan
        /// </summary>
        private readonly FrameSyncVisualScanMatcher _visualScanMatcher;

        /// <summary>
        /// Grouper checkpoint
        /// </summary>
        private readonly FrameSyncCheckpointGrouper _checkpointGrouper;

        /// <summary>
        /// Limiter processi ffmpeg video
        /// </summary>
        private readonly SemaphoreSlim _videoExtractSemaphore;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso eseguibile ffmpeg</param>
        public FrameSyncService(string ffmpegPath) : base(ffmpegPath, LogSection.FrameSync)
        {
            // Carica configurazione frame sync
            this._fsConfig = AppSettingsService.Instance.Settings.Advanced.FrameSync;

            this._offsets = new int[this._vsConfig.NumCheckPoints];
            this._ssimValues = new double[this._vsConfig.NumCheckPoints];
            this._pointValid = new bool[this._vsConfig.NumCheckPoints];
            this._pointResults = new FrameSyncPointResult[this._vsConfig.NumCheckPoints];
            this._frameSyncTimeMs = 0;
            this._lastResult = new FrameSyncResult();
            this._lastInitialResult = new FrameSyncInitialResult();
            this._timing = new FrameSyncTimingInfo();
            this._lastSourceGeometry = null;
            this._lastLanguageGeometry = null;
            this._lastAudioGlobalResult = new AudioGlobalFingerprintResult();
            this._segmentCache = new FrameSyncSegmentCache(this._vsConfig);
            this._candidateScorer = new FrameSyncCandidateScorer(this._vsConfig, this._fsConfig);
            this._resultBuilder = new FrameSyncResultBuilder();
            this._audioInitialResolver = new FrameSyncAudioInitialResolver(this._ffmpegPath, this._fsConfig, this._ffmpegConfig);
            this._offsetCandidateBuilder = new FrameSyncOffsetCandidateBuilder();
            this._visualScanMatcher = new FrameSyncVisualScanMatcher(this._vsConfig, this._candidateScorer, VISUAL_SCAN_MAX_SAMPLES, VISUAL_SCAN_FAST_TOP_CANDIDATES, VISUAL_SCAN_OFFSET_STEP_MS, CHECKPOINT_LOCAL_MAX_SAMPLES, CHECKPOINT_LOCAL_MIN_SAMPLES);
            this._checkpointGrouper = new FrameSyncCheckpointGrouper(this._vsConfig, this._fsConfig);
            this._videoExtractSemaphore = new SemaphoreSlim(MAX_CONCURRENT_VIDEO_EXTRACTS, MAX_CONCURRENT_VIDEO_EXTRACTS);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Trova offset sync tramite scene-cut voting poi verifica a 9 punti
        /// </summary>
        /// <param name="sourceFile">Percorso file video sorgente</param>
        /// <param name="languageFile">Percorso file video lingua</param>
        /// <returns>Offset in ms o int.MinValue se non trovato</returns>
        public int RefineOffset(string sourceFile, string languageFile)
        {
            int resultOffset = int.MinValue;
            Stopwatch stopwatch = new Stopwatch();
            Stopwatch phaseStopwatch = new Stopwatch();
            int durationMs;
            double fps;
            bool infoOk;
            int frameIntervalMs;
            int initialDelay = int.MinValue;
            List<int> validOffsets = new List<int>();
            int validCount;
            int bestGroupCount = 0;
            int bestGroupOffset = 0;
            double bestGroupScoreAverage;
            double bestGroupScoreMin;
            int initialFinalDeltaMs = 0;
            int groupingToleranceMs = 0;
            int anomalyCount;
            int initialCenteredOffset;
            int initialCenteredCount;
            double initialCenteredScoreAverage;
            double initialCenteredScoreMin;
            double langFps;
            double langTargetFps;
            double sourceTargetFps;
            double fpsRatio;
            string failureReason = "";
            bool originalSourceGeometryCropToFourThree = this._geometryCropSourceToFourThree;
            bool originalLanguageGeometryCropToFourThree = this._geometryCropLanguageToFourThree;
            VideoTimingResolver timingResolver = new VideoTimingResolver();
            VideoTimingInfo sourceTiming;
            VideoTimingInfo langTiming;
            stopwatch.Start();
            this._timing = new FrameSyncTimingInfo();
            this._lastAudioGlobalResult = new AudioGlobalFingerprintResult();
            this.ClearExtractSegmentCache();

            // Resetta risultati verifica
            for (int i = 0; i < this._vsConfig.NumCheckPoints; i++)
            {
                this._offsets[i] = int.MinValue;
                this._ssimValues[i] = 0.0;
                this._pointValid[i] = false;
                this._pointResults[i] = new FrameSyncPointResult();
                this._pointResults[i].CheckpointPercent = (i + 1) * 10;
            }

            // Ottiene informazioni video dal file sorgente
            phaseStopwatch.Restart();
            infoOk = this.GetVideoInfo(sourceFile, out durationMs, out fps);
            phaseStopwatch.Stop();
            this._timing.VideoInfoMs += phaseStopwatch.ElapsedMilliseconds;

            if (infoOk && durationMs >= this._fsConfig.MinDurationMs)
            {
                phaseStopwatch.Restart();
                this.PrepareGeometryDrivenCrop(sourceFile, languageFile);
                phaseStopwatch.Stop();
                this._timing.GeometryMs += phaseStopwatch.ElapsedMilliseconds;
                this._lastSourceGeometry = this._lastSourceGeometryInfo;
                this._lastLanguageGeometry = this._lastLanguageGeometryInfo;

                sourceTiming = timingResolver.Resolve(sourceFile, null);
                langTiming = timingResolver.Resolve(languageFile, null);
                if (sourceTiming.ObservedFps > 0.0 && (!sourceTiming.CanNormalizeToNominalFps || sourceTiming.IsVariableFrameRate))
                {
                    fps = sourceTiming.ObservedFps;
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  Source VFR/non affidabile: confronto su PTS reali, fps osservato diagnostico " + fps.ToString("F3", CultureInfo.InvariantCulture) + " (" + sourceTiming.Reason + ")");
                }
                else if (sourceTiming.NominalFps > 0.0 && sourceTiming.CanNormalizeToNominalFps)
                {
                    fps = sourceTiming.NominalFps;
                }

                // Calcola intervallo tra frame in ms per tolleranze
                frameIntervalMs = (int)Math.Round(1000.0 / fps);
                if (frameIntervalMs < 1)
                {
                    frameIntervalMs = 1;
                }

                // Rileva fps del file lingua per log informativo
                phaseStopwatch.Restart();
                if (this.GetVideoInfo(languageFile, out _, out langFps))
                {
                    if (langTiming != null && langTiming.ObservedFps > 0.0 && (!langTiming.CanNormalizeToNominalFps || langTiming.IsVariableFrameRate))
                    {
                        langFps = langTiming.ObservedFps;
                    }
                    else if (langTiming != null && langTiming.NominalFps > 0.0 && langTiming.CanNormalizeToNominalFps)
                    {
                        langFps = langTiming.NominalFps;
                    }
                    fpsRatio = langFps / fps;

                    // Log se fps lang differisce di più del 2% dal source
                    if (Math.Abs(fpsRatio - 1.0) > 0.02)
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  FPS osservati diversi (diagnostica): source=" + fps.ToString("F3", CultureInfo.InvariantCulture) + ", lang=" + langFps.ToString("F3", CultureInfo.InvariantCulture));
                    }
                }
                phaseStopwatch.Stop();
                this._timing.VideoInfoMs += phaseStopwatch.ElapsedMilliseconds;

                sourceTargetFps = 0.0;
                langTargetFps = 0.0;
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Campionamento FrameSync su timestamp PTS reali");

                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Durata: " + (durationMs / 1000) + "s, fps diagnostico=" + fps.ToString("F3", CultureInfo.InvariantCulture) + ", intervallo fallback=" + frameIntervalMs + "ms, core=" + Environment.ProcessorCount);

                // Fase 1: ricerca delay iniziale tramite scene-cut voting
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, "  Ricerca delay iniziale (2 min source, 3 min lang)...");
                ConsoleHelper.Progress(LogSection.FrameSync, 18, "FrameSync: initial");
                phaseStopwatch.Restart();
                initialDelay = this.FindInitialDelay(sourceFile, languageFile, fps, sourceTargetFps, langTargetFps);
                phaseStopwatch.Stop();
                this._timing.InitialSearchMs += phaseStopwatch.ElapsedMilliseconds;

                // Libera memoria frame FindInitialDelay prima della verifica a 9 punti
                GC.Collect();

                if (initialDelay != int.MinValue)
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Success, "  Delay iniziale: " + initialDelay + "ms");

                    // Fase 2: verifica a 9 punti distribuiti nel video
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, "  Verifica a 9 punti (4 thread)...");
                    ConsoleHelper.Progress(LogSection.FrameSync, 58, "FrameSync: checkpoint");
                    phaseStopwatch.Restart();
                    this.VerifyAtMultiplePoints(sourceFile, languageFile, durationMs, initialDelay, fps, sourceTargetFps, langTargetFps);
                    phaseStopwatch.Stop();
                    this._timing.CheckpointsMs += phaseStopwatch.ElapsedMilliseconds;

                    // Raccoglie offset validi
                    for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
                    {
                        if (this._pointValid[p])
                        {
                            validOffsets.Add(this._offsets[p]);
                        }
                    }

                    validCount = validOffsets.Count;

                    if (validCount > 0)
                    {
                        // Tolleranza raggruppamento: frameInterval * numero frame configurato
                        groupingToleranceMs = frameIntervalMs * this._fsConfig.GroupingToleranceFrames;

                        // Trova il gruppo di offset coerenti più grande
                        for (int i = 0; i < validOffsets.Count; i++)
                        {
                            int groupCount = 0;
                            int groupSum = 0;
                            for (int j = 0; j < validOffsets.Count; j++)
                            {
                                int diff = Math.Abs(validOffsets[i] - validOffsets[j]);

                                if (diff <= groupingToleranceMs)
                                {
                                    groupCount++;
                                    groupSum += validOffsets[j];
                                }
                            }

                            if (groupCount > bestGroupCount)
                            {
                                bestGroupCount = groupCount;
                                bestGroupOffset = groupSum / groupCount;
                            }
                        }
                    }

                    if (validCount >= this._fsConfig.MinValidPoints)
                    {
                        this._checkpointGrouper.ComputeGroupScore(bestGroupOffset, groupingToleranceMs, this._pointValid, this._offsets, this._pointResults, this._ssimValues, out bestGroupScoreAverage, out bestGroupScoreMin);

                        if (bestGroupCount >= this._fsConfig.MinValidPoints)
                        {
                            // Log punti anomali scartati
                            anomalyCount = validCount - bestGroupCount;
                            if (anomalyCount > 0)
                            {
                                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  " + anomalyCount + " punti anomali scartati, " + bestGroupCount + " punti coerenti");
                            }

                            initialFinalDeltaMs = Math.Abs(bestGroupOffset - initialDelay);
                            if (initialFinalDeltaMs > frameIntervalMs * this._fsConfig.InitialCheckpointDriftPenaltyFrames)
                            {
                                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  Drift initial/checkpoint: initial=" + initialDelay + "ms, cluster=" + bestGroupOffset + "ms, delta=" + initialFinalDeltaMs + "ms");
                            }

                            if (initialFinalDeltaMs > frameIntervalMs * this._fsConfig.InitialCheckpointDriftRejectFrames)
                            {
                                failureReason = "Drift initial/checkpoint eccessivo";
                                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Drift initial/checkpoint eccessivo: initial=" + initialDelay + "ms, cluster=" + bestGroupOffset + "ms, delta=" + initialFinalDeltaMs + "ms");
                            }
                            else if (bestGroupScoreAverage < this._fsConfig.FinalMinConfidence)
                            {
                                failureReason = "Confidence finale insufficiente";
                                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Confidence checkpoint insufficiente: avg=" + bestGroupScoreAverage.ToString("F3", CultureInfo.InvariantCulture) + ", min=" + bestGroupScoreMin.ToString("F3", CultureInfo.InvariantCulture) + ", richiesta=" + this._fsConfig.FinalMinConfidence.ToString("F3", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                resultOffset = bestGroupOffset;
                            }
                        }
                        else
                        {
                            if (this.TryBuildInitialCenteredCheckpointGroup(initialDelay, frameIntervalMs, out initialCenteredOffset, out initialCenteredCount, out initialCenteredScoreAverage, out initialCenteredScoreMin))
                            {
                                resultOffset = initialCenteredOffset;
                                initialFinalDeltaMs = Math.Abs(resultOffset - initialDelay);
                                anomalyCount = validCount - initialCenteredCount;

                                if (anomalyCount > 0)
                                {
                                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  " + anomalyCount + " punti fuori dalla finestra initial scartati, " + initialCenteredCount + " punti coerenti");
                                }
                                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  Checkpoint coerenti con initial: " + initialCenteredCount + "/" + validCount + ", offset mediano=" + initialCenteredOffset + "ms, avg=" + initialCenteredScoreAverage.ToString("F3", CultureInfo.InvariantCulture) + ", min=" + initialCenteredScoreMin.ToString("F3", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                // Offset non coerenti
                                failureReason = "Offset non coerenti";
                                StringBuilder detail = new StringBuilder();
                                for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
                                {
                                    if (this._pointValid[p])
                                    {
                                        if (detail.Length > 0) { detail.Append(", "); }
                                        detail.Append((p + 1) * 10 + "%=" + this._offsets[p] + "ms");
                                    }
                                }
                                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Offset non coerenti (" + bestGroupCount + "/" + validCount + " nel gruppo principale): " + detail);
                            }
                        }
                    }
                    else
                    {
                        failureReason = "Punti validi insufficienti";
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Solo " + validCount + "/" + this._vsConfig.NumCheckPoints + " punti validi (minimo richiesto: " + this._fsConfig.MinValidPoints + ")");
                    }
                }
                else
                {
                    if (this._lastInitialResult != null && !string.IsNullOrEmpty(this._lastInitialResult.FailureReason))
                    {
                        failureReason = this._lastInitialResult.FailureReason;
                    }
                    else
                    {
                        failureReason = "Delay iniziale non determinabile";
                    }
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Impossibile determinare delay iniziale");
                    ConsoleHelper.Progress(LogSection.FrameSync, 76, "FrameSync: non conclusivo");
                }
            }
            else
            {
                failureReason = "Info video non disponibili o durata troppo breve";
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Impossibile ottenere info video o durata troppo breve");
                ConsoleHelper.Progress(LogSection.FrameSync, 76, "FrameSync: non conclusivo");
            }

            stopwatch.Stop();
            this._frameSyncTimeMs = stopwatch.ElapsedMilliseconds;
            this._timing.TotalMs = this._frameSyncTimeMs;

            // Inverte segno: internamente usa langTime - sourceTime per i calcoli,
            // ma il delay da applicare è l'opposto (negativo se lang è in ritardo)
            if (resultOffset != int.MinValue)
            {
                resultOffset = -resultOffset;
            }

            this._lastResult = this.BuildLegacyResult(resultOffset, initialDelay, failureReason);
            this._lastResult?.InitialToFinalDeltaMs = initialFinalDeltaMs;
            this.ClearExtractSegmentCache();
            this._geometryCropSourceToFourThree = originalSourceGeometryCropToFourThree;
            this._geometryCropLanguageToFourThree = originalLanguageGeometryCropToFourThree;

            return resultOffset;
        }

        /// <summary>
        /// Riepilogo risultati per tutti i punti di verifica
        /// </summary>
        /// <returns>Stringa riepilogativa con offset e MSE per ogni punto</returns>
        public string GetDetailSummary()
        {
            StringBuilder sb = new StringBuilder();
            int validCount = 0;
            int failedCount = 0;
            int minOffset = 0;
            int maxOffset = 0;
            bool hasOffset = false;
            for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
            {
                if (this._pointValid[p])
                {
                    validCount++;
                    if (!hasOffset)
                    {
                        minOffset = this._offsets[p];
                        maxOffset = this._offsets[p];
                        hasOffset = true;
                    }
                    else
                    {
                        if (this._offsets[p] < minOffset) { minOffset = this._offsets[p]; }
                        if (this._offsets[p] > maxOffset) { maxOffset = this._offsets[p]; }
                    }
                }
                else
                {
                    failedCount++;
                }
            }

            sb.Append("checkpoint validi=");
            sb.Append(validCount);
            sb.Append('/');
            sb.Append(this._vsConfig.NumCheckPoints);
            if (hasOffset)
            {
                sb.Append(", offset range=");
                if (minOffset == maxOffset)
                {
                    sb.Append(minOffset).Append("ms");
                }
                else
                {
                    sb.Append(minOffset).Append("..").Append(maxOffset).Append("ms");
                }
            }
            if (failedCount > 0)
            {
                sb.Append(", fail=").Append(failedCount);
            }

            return sb.ToString();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Accetta checkpoint distribuiti se sono coerenti con l'initial anche quando non formano un cluster da singolo frame
        /// </summary>
        private bool TryBuildInitialCenteredCheckpointGroup(int initialDelay, double frameIntervalMs, out int medianOffset, out int coherentCount, out double averageScore, out double minScore)
        {
            bool result = false;
            int toleranceMs = (int)Math.Round(frameIntervalMs * this._fsConfig.InitialCheckpointDriftPenaltyFrames);
            List<int> offsets = new List<int>();
            double scoreSum = 0.0;
            medianOffset = 0;
            coherentCount = 0;
            averageScore = 0.0;
            minScore = 1.0;

            if (toleranceMs < 80)
            {
                toleranceMs = 80;
            }

            for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
            {
                if (this._pointValid[p] && Math.Abs(this._offsets[p] - initialDelay) <= toleranceMs)
                {
                    double score = this._pointResults[p] != null ? this._pointResults[p].BestScore : this._ssimValues[p];
                    if (score < 0.0) { score = -score; }
                    if (score > 1.0) { score = 1.0; }

                    offsets.Add(this._offsets[p]);
                    scoreSum += score;
                    if (score < minScore)
                    {
                        minScore = score;
                    }
                }
            }

            coherentCount = offsets.Count;
            if (coherentCount == 0)
            {
                minScore = 0.0;
                return result;
            }

            averageScore = scoreSum / coherentCount;
            if (coherentCount < this._fsConfig.MinValidPoints || averageScore < this._fsConfig.FinalMinConfidence)
            {
                return result;
            }

            offsets.Sort();
            if ((coherentCount & 1) == 1)
            {
                medianOffset = offsets[coherentCount / 2];
            }
            else
            {
                medianOffset = (int)Math.Round((offsets[(coherentCount / 2) - 1] + offsets[coherentCount / 2]) / 2.0);
            }

            result = true;

            return result;
        }

        /// <summary>
        /// Costruisce un segmento cache riusabile se l'estrazione iniziale è valida
        /// </summary>
        private FrameExtractProfile BuildFrameExtractProfile(string filePath, int startMs, double durationSec, double targetFps, bool geometryCropToFourThree, string manualCropPx)
        {
            FrameExtractProfile profile = new FrameExtractProfile();
            string normalizedManualCrop = Options.NormalizeAnalysisCropPx(manualCropPx);
            profile.FilePath = filePath;
            profile.StartMs = startMs;
            profile.DurationSec = durationSec;
            profile.TargetFps = targetFps;
            profile.GeometryCropToFourThree = this.UseGeometryCrop(geometryCropToFourThree, normalizedManualCrop);
            profile.ManualAnalysisCropPx = normalizedManualCrop;
            return profile;
        }

        /// <summary>
        /// Estrae un segmento usando un profilo esplicito
        /// </summary>
        private void ExtractSegment(FrameExtractProfile profile, out List<byte[]> frames, out double[] timestampsMs)
        {
            this.ExtractSegment(profile.FilePath, profile.StartMs, profile.DurationSec, profile.TargetFps, profile.GeometryCropToFourThree, profile.ManualAnalysisCropPx, out frames, out timestampsMs);
        }

        /// <summary>
        /// Estrae un segmento usando la cache generale della sincronizzazione corrente
        /// </summary>
        private void ExtractSegmentCached(FrameExtractProfile profile, out List<byte[]> frames, out double[] timestampsMs)
        {
            bool cacheHit;
            long elapsedMs;
            cacheHit = this._segmentCache.Extract(profile, this.ExtractSegment, this._videoExtractSemaphore, out frames, out timestampsMs, out elapsedMs);

            lock (this._timing)
            {
                this._timing.VideoExtractCalls++;
                if (cacheHit)
                {
                    this._timing.VideoExtractCacheHits++;
                }
                else
                {
                    this._timing.VideoExtractCacheMisses++;
                }
                this._timing.VideoExtractCachedMs += elapsedMs;
            }
        }

        /// <summary>
        /// Svuota la cache segmenti della sincronizzazione corrente
        /// </summary>
        private void ClearExtractSegmentCache()
        {
            this._segmentCache.Clear();
        }

        /// <summary>
        /// Calcola il range offset coperto dalla finestra estratta
        /// </summary>
        private void CalculateWindowOffsetRange(int sourceStartMs, double sourceDurationSec, double langDurationSec, out int minOffsetMs, out int maxOffsetMs)
        {
            minOffsetMs = -sourceStartMs - (int)Math.Ceiling(sourceDurationSec * 1000.0);
            maxOffsetMs = (int)Math.Ceiling(langDurationSec * 1000.0) - sourceStartMs;
        }

        /// <summary>
        /// Restituisce il massimo valore assoluto del range offset
        /// </summary>
        private int GetMaxAbsOffset(int minOffsetMs, int maxOffsetMs)
        {
            int absMin = Math.Abs(minOffsetMs);
            int absMax = Math.Abs(maxOffsetMs);
            return absMin > absMax ? absMin : absMax;
        }

        /// <summary>
        /// Ricerca delay iniziale tramite voting su coppie di tagli di scena
        /// </summary>
        /// <param name="sourceFile">Percorso file video sorgente</param>
        /// <param name="languageFile">Percorso file video lingua</param>
        /// <param name="fps">Frame rate del video sorgente</param>
        /// <param name="sourceTargetFps">FPS target per normalizzazione source (0 = fps nativo)</param>
        /// <param name="langTargetFps">FPS target per normalizzazione lang (0 = fps nativo)</param>
        /// <returns>Delay in ms o int.MinValue se non determinabile</returns>
        private int FindInitialDelay(string sourceFile, string languageFile, double fps, double sourceTargetFps, double langTargetFps)
        {
            int result = int.MinValue;
            List<byte[]> sourceFrames = null;
            List<byte[]> langFrames = null;
            double[] sourceTimestampsMs = null;
            double[] langTimestampsMs = null;
            string sourceFileCopy = sourceFile;
            string langFileCopy = languageFile;
            double frameIntervalMs = 1000.0 / fps;
            double clusterTolMs = frameIntervalMs;
            double nearestTolMs = 3.0 * frameIntervalMs;
            double consistencyTolMs = frameIntervalMs;
            List<int> sourceCutsRaw;
            List<int> langCutsRaw;
            double[][] sourceTemporalFingerprints = null;
            double[][] langTemporalFingerprints = null;
            List<int> validSourceCuts = new List<int>();
            List<int> validLangCuts = new List<int>();
            int srcCutCount;
            int lngCutCount;
            int candidateCount;
            int minInitialOffsetMs;
            int maxInitialOffsetMs;
            double[] candidates;
            int candidateIdx;
            List<FrameSyncCandidate> initialCandidates;
            List<double> verifiedDelays = new List<double>();
            double medianDelay;
            int consistentCount = 0;
            FrameSyncCandidate bestCandidate = null;
            FrameSyncCandidate secondCandidate;
            double candidateMargin;
            bool candidateAmbiguous = false;
            Stopwatch phaseStopwatch = new Stopwatch();

            this._lastInitialResult = new FrameSyncInitialResult();
            this.CalculateWindowOffsetRange(this._fsConfig.SourceStartSec * 1000, this._fsConfig.SourceDurationSec, this._fsConfig.LangDurationSec, out minInitialOffsetMs, out maxInitialOffsetMs);

            if (clusterTolMs < INITIAL_CLUSTER_TOLERANCE_MIN_MS)
            {
                clusterTolMs = INITIAL_CLUSTER_TOLERANCE_MIN_MS;
            }
            if (nearestTolMs < INITIAL_NEAREST_TOLERANCE_MIN_MS)
            {
                nearestTolMs = INITIAL_NEAREST_TOLERANCE_MIN_MS;
            }
            if (consistencyTolMs < INITIAL_CONSISTENCY_TOLERANCE_MIN_MS)
            {
                consistencyTolMs = INITIAL_CONSISTENCY_TOLERANCE_MIN_MS;
            }

            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Estrazione frame (source " + this._fsConfig.SourceDurationSec + "s, lang " + this._fsConfig.LangDurationSec + "s)...");
            ConsoleHelper.Progress(LogSection.FrameSync, 24, "FrameSync: estrazione");

            // Estrae segmenti in parallelo preservando i timestamp PTS originali
            double fpsCopy = sourceTargetFps;
            bool sourceGeometryCropCopy = this._geometryCropSourceToFourThree;
            bool languageGeometryCropCopy = this._geometryCropLanguageToFourThree;
            double langTargetFpsCopy = langTargetFps;
            FrameExtractProfile sourceProfile = this.BuildFrameExtractProfile(sourceFileCopy, this._fsConfig.SourceStartSec * 1000, this._fsConfig.SourceDurationSec, fpsCopy, sourceGeometryCropCopy, this._analysisCropSourcePx);
            FrameExtractProfile languageProfile = this.BuildFrameExtractProfile(langFileCopy, 0, this._fsConfig.LangDurationSec, langTargetFpsCopy, languageGeometryCropCopy, this._analysisCropLanguagePx);
            Thread sourceThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegmentCached(sourceProfile, out f, out t);
                sourceFrames = f;
                sourceTimestampsMs = t;
            });
            Thread langThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegmentCached(languageProfile, out f, out t);
                langFrames = f;
                langTimestampsMs = t;
            });
            phaseStopwatch.Restart();
            sourceThread.Start();
            langThread.Start();
            sourceThread.Join();
            langThread.Join();
            phaseStopwatch.Stop();
            this._timing.InitialExtractMs += phaseStopwatch.ElapsedMilliseconds;

            // Verifica frame sufficienti
            if (sourceFrames == null || sourceFrames.Count < this._vsConfig.CutSignatureLength)
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Frame sorgente insufficienti: " + (sourceFrames != null ? sourceFrames.Count : 0));
            }
            else if (langFrames == null || langFrames.Count < this._vsConfig.CutSignatureLength)
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Frame lingua insufficienti: " + (langFrames != null ? langFrames.Count : 0));
            }
            else
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Frame estratti: source=" + sourceFrames.Count + ", lang=" + langFrames.Count);
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Range offset initial da finestra: " + minInitialOffsetMs + "ms.." + maxInitialOffsetMs + "ms");

                // Rileva tagli di scena in entrambi i segmenti
                phaseStopwatch.Restart();
                sourceCutsRaw = this.DetectSceneCuts(sourceFrames);
                langCutsRaw = this.DetectSceneCuts(langFrames);

                // Filtra tagli con margine sufficiente per la firma
                for (int i = 0; i < sourceCutsRaw.Count; i++)
                {
                    if (sourceCutsRaw[i] >= this._vsConfig.CutHalfWindow && sourceCutsRaw[i] + this._vsConfig.CutHalfWindow <= sourceFrames.Count)
                    {
                        validSourceCuts.Add(sourceCutsRaw[i]);
                    }
                }
                for (int i = 0; i < langCutsRaw.Count; i++)
                {
                    if (langCutsRaw[i] >= this._vsConfig.CutHalfWindow && langCutsRaw[i] + this._vsConfig.CutHalfWindow <= langFrames.Count)
                    {
                        validLangCuts.Add(langCutsRaw[i]);
                    }
                }

                srcCutCount = validSourceCuts.Count;
                lngCutCount = validLangCuts.Count;

                if (srcCutCount < this._vsConfig.MinSceneCuts || lngCutCount < this._vsConfig.MinSceneCuts)
                {
                    List<int> relaxedSourceCutsRaw = this.DetectSceneCutsRelaxed(sourceFrames);
                    List<int> relaxedLangCutsRaw = this.DetectSceneCutsRelaxed(langFrames);
                    List<int> relaxedSourceCuts = new List<int>();
                    List<int> relaxedLangCuts = new List<int>();

                    for (int i = 0; i < relaxedSourceCutsRaw.Count; i++)
                    {
                        if (relaxedSourceCutsRaw[i] >= this._vsConfig.CutHalfWindow && relaxedSourceCutsRaw[i] + this._vsConfig.CutHalfWindow <= sourceFrames.Count)
                        {
                            relaxedSourceCuts.Add(relaxedSourceCutsRaw[i]);
                        }
                    }
                    for (int i = 0; i < relaxedLangCutsRaw.Count; i++)
                    {
                        if (relaxedLangCutsRaw[i] >= this._vsConfig.CutHalfWindow && relaxedLangCutsRaw[i] + this._vsConfig.CutHalfWindow <= langFrames.Count)
                        {
                            relaxedLangCuts.Add(relaxedLangCutsRaw[i]);
                        }
                    }

                    if (relaxedSourceCuts.Count >= this._vsConfig.MinSceneCuts && relaxedLangCuts.Count >= this._vsConfig.MinSceneCuts)
                    {
                        sourceCutsRaw = relaxedSourceCutsRaw;
                        langCutsRaw = relaxedLangCutsRaw;
                        validSourceCuts = relaxedSourceCuts;
                        validLangCuts = relaxedLangCuts;
                        srcCutCount = validSourceCuts.Count;
                        lngCutCount = validLangCuts.Count;
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  Scene-cut fallback permissivo attivo: source=" + srcCutCount + ", lang=" + lngCutCount);
                    }
                }

                phaseStopwatch.Stop();
                this._timing.InitialSceneCutMs += phaseStopwatch.ElapsedMilliseconds;
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Tagli rilevati: source=" + sourceCutsRaw.Count + " (" + srcCutCount + " utilizzabili), lang=" + langCutsRaw.Count + " (" + lngCutCount + " utilizzabili)");
                ConsoleHelper.Progress(LogSection.FrameSync, 34, "FrameSync: candidati");

                if (srcCutCount >= this._vsConfig.MinSceneCuts && lngCutCount >= this._vsConfig.MinSceneCuts)
                {
                    sourceTemporalFingerprints = new double[sourceFrames.Count][];
                    langTemporalFingerprints = new double[langFrames.Count][];

                    phaseStopwatch.Restart();
                    // Genera offset candidati da tutte le coppie (sourceCut, langCut)
                    candidateCount = srcCutCount * lngCutCount;
                    candidates = new double[candidateCount];
                    candidateIdx = 0;

                    for (int s = 0; s < srcCutCount; s++)
                    {
                        double srcMs = sourceTimestampsMs[validSourceCuts[s]];
                        for (int l = 0; l < lngCutCount; l++)
                        {
                            double lngMs = langTimestampsMs[validLangCuts[l]];
                            candidates[candidateIdx] = lngMs - srcMs;
                            candidateIdx++;
                        }
                    }

                    // Ordina e trova cluster candidati su finestra temporale robusta ai PTS VFR
                    Array.Sort(candidates);
                    initialCandidates = this._offsetCandidateBuilder.SelectInitialCandidates(candidates, candidateCount, clusterTolMs, MAX_INITIAL_CANDIDATES, this.GetMaxAbsOffset(minInitialOffsetMs, maxInitialOffsetMs));
                    this._lastInitialResult = new FrameSyncInitialResult();
                    phaseStopwatch.Stop();
                    this._timing.InitialVotingMs += phaseStopwatch.ElapsedMilliseconds;

                    if (initialCandidates.Count > 0)
                    {
                        for (int i = 0; i < initialCandidates.Count; i++)
                        {
                            this._lastInitialResult.Candidates.Add(initialCandidates[i]);
                        }

                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Voting: " + initialCandidates.Count + " cluster candidati su " + candidateCount + " coppie");

                        phaseStopwatch.Restart();
                        InitialCandidateVerifyResult[] verifyResults = new InitialCandidateVerifyResult[initialCandidates.Count];
                        Thread[] candidateThreads = new Thread[initialCandidates.Count];
                        for (int c = 0; c < initialCandidates.Count; c++)
                        {
                            int candidateIndex = c;
                            candidateThreads[c] = new Thread(() =>
                            {
                                InitialCandidateVerifyResult verifyResult = new InitialCandidateVerifyResult();
                                List<double> localDelays = new List<double>();
                                int localVerifiedCount;
                                double localScore;
                                double localBlurScore;
                                double localEdgeScore;
                                double localBlockScore;
                                double localMotionScore;
                                double localHashScore;
                                int localDescriptorVotes;
                                double localOffset = initialCandidates[candidateIndex].OffsetMs;

                                verifyResult.Delay = this.VerifyInitialCandidate(sourceFrames, langFrames, sourceTemporalFingerprints, langTemporalFingerprints, sourceTimestampsMs, langTimestampsMs, validSourceCuts, validLangCuts, localOffset, consistencyTolMs, nearestTolMs, localDelays, out localVerifiedCount, out localScore, out localBlurScore, out localEdgeScore, out localBlockScore, out localMotionScore, out localHashScore, out localDescriptorVotes);
                                verifyResult.VerifiedCount = localVerifiedCount;
                                verifyResult.Score = localScore;
                                verifyResult.BlurScore = localBlurScore;
                                verifyResult.EdgeScore = localEdgeScore;
                                verifyResult.BlockScore = localBlockScore;
                                verifyResult.MotionScore = localMotionScore;
                                verifyResult.HashScore = localHashScore;
                                verifyResult.DescriptorVotes = localDescriptorVotes;
                                verifyResult.Delays = localDelays;
                                verifyResults[candidateIndex] = verifyResult;
                            });
                            candidateThreads[c].Start();
                        }

                        for (int c = 0; c < candidateThreads.Length; c++)
                        {
                            candidateThreads[c].Join();
                        }
                        phaseStopwatch.Stop();
                        this._timing.InitialCandidateVerifyMs += phaseStopwatch.ElapsedMilliseconds;

                        for (int c = 0; c < initialCandidates.Count; c++)
                        {
                            InitialCandidateVerifyResult verifyResult = verifyResults[c];
                            if (verifyResult == null)
                            {
                                continue;
                            }

                            initialCandidates[c].MatchedCuts = verifyResult.VerifiedCount;
                            initialCandidates[c].TemporalScore = (srcCutCount > 0) ? verifyResult.VerifiedCount / (double)srcCutCount : 0.0;
                            initialCandidates[c].VisualScore = verifyResult.Score > 0.0 ? verifyResult.Score : 0.0;
                            initialCandidates[c].BlurScore = verifyResult.BlurScore;
                            initialCandidates[c].EdgeScore = verifyResult.EdgeScore;
                            initialCandidates[c].BlockScore = verifyResult.BlockScore;
                            initialCandidates[c].MotionScore = verifyResult.MotionScore;
                            initialCandidates[c].HashScore = verifyResult.HashScore;
                            initialCandidates[c].DescriptorVotes = verifyResult.DescriptorVotes;
                            initialCandidates[c].DescriptorAgreement = verifyResult.DescriptorVotes / 6.0;
                            initialCandidates[c].CombinedScore = this._candidateScorer.ComputeCandidateScore(initialCandidates[c].TemporalScore, verifyResult.Score, verifyResult.BlurScore, verifyResult.EdgeScore, verifyResult.BlockScore, verifyResult.MotionScore, verifyResult.HashScore);

                            if (verifyResult.Delay != int.MinValue)
                            {
                                initialCandidates[c].OffsetMs = verifyResult.Delay;
                                verifiedDelays.Clear();
                                for (int d = 0; d < verifyResult.Delays.Count; d++)
                                {
                                    verifiedDelays.Add(verifyResult.Delays[d]);
                                }
                            }
                        }

                        initialCandidates.Sort((a, b) =>
                        {
                            int cmp = b.CombinedScore.CompareTo(a.CombinedScore);
                            if (cmp != 0) { return cmp; }
                            return b.VoteCount.CompareTo(a.VoteCount);
                        });
                        this._lastInitialResult.Candidates.Clear();
                        for (int i = 0; i < initialCandidates.Count; i++)
                        {
                            this._lastInitialResult.Candidates.Add(initialCandidates[i]);
                        }

                        bestCandidate = this._candidateScorer.SelectBestCandidate(initialCandidates, clusterTolMs, this._fsConfig.InitialMinMatchedCuts, this._fsConfig.InitialMinScore, this._fsConfig.InitialMinMargin, out secondCandidate, out candidateMargin, out candidateAmbiguous);

                        if (bestCandidate != null)
                        {
                            bestCandidate.SecondBestScore = secondCandidate != null ? secondCandidate.CombinedScore : 0.0;
                            bestCandidate.Margin = candidateMargin;

                            if (!candidateAmbiguous)
                            {
                                result = bestCandidate.OffsetMs;
                                this._lastInitialResult.Success = true;
                                this._lastInitialResult.BestCandidate = bestCandidate;
                            }
                            else
                            {
                                this._lastInitialResult.Ambiguous = true;
                                this._lastInitialResult.FailureReason = "Candidati iniziali ambigui";
                                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Candidati iniziali ambigui: best=" + bestCandidate.OffsetMs + "ms score=" + bestCandidate.CombinedScore.ToString("F3", CultureInfo.InvariantCulture) + ", second=" + secondCandidate.OffsetMs + "ms score=" + secondCandidate.CombinedScore.ToString("F3", CultureInfo.InvariantCulture) + ", margin=" + candidateMargin.ToString("F3", CultureInfo.InvariantCulture));
                            }
                        }
                    }
                    else
                    {
                        this._lastInitialResult.FailureReason = "Nessun cluster candidato";
                    }

                    if (result != int.MinValue && bestCandidate != null)
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  Tagli iniziali verificati: " + bestCandidate.MatchedCuts + " (minimo: " + this._fsConfig.InitialMinMatchedCuts + ")");
                    }
                    else if (!candidateAmbiguous && bestCandidate != null && verifiedDelays.Count >= this._fsConfig.InitialMinMatchedCuts)
                    {
                        // Mediana degli offset verificati
                        verifiedDelays.Sort();
                        medianDelay = verifiedDelays[verifiedDelays.Count / 2];

                        // Verifica consistenza entro 1 frame dalla mediana
                        for (int i = 0; i < verifiedDelays.Count; i++)
                        {
                            if (Math.Abs(verifiedDelays[i] - medianDelay) <= consistencyTolMs)
                            {
                                consistentCount++;
                            }
                        }

                        if (consistentCount >= this._vsConfig.MinSceneCuts)
                        {
                            if (result == int.MinValue)
                            {
                                result = (int)Math.Round(medianDelay);
                            }
                            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Delay iniziale: " + result + "ms (mediana di " + verifiedDelays.Count + " tagli, " + consistentCount + " consistenti)");
                        }
                        else
                        {
                            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Delay non consistente: solo " + consistentCount + "/" + verifiedDelays.Count + " entro 1 frame dalla mediana");
                        }
                    }
                    else if (verifiedDelays.Count >= this._fsConfig.InitialMinMatchedCuts)
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Candidato iniziale scartato: tagli verificati=" + verifiedDelays.Count + " (minimo: " + this._fsConfig.InitialMinMatchedCuts + "), score o margine sotto soglia");
                        if (string.IsNullOrEmpty(this._lastInitialResult.FailureReason))
                        {
                            this._lastInitialResult.FailureReason = "Candidato iniziale sotto soglia";
                        }
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Tagli iniziali verificati insufficienti: " + verifiedDelays.Count + " (minimo: " + this._fsConfig.InitialMinMatchedCuts + ")");
                        if (string.IsNullOrEmpty(this._lastInitialResult.FailureReason))
                        {
                            this._lastInitialResult.FailureReason = "Nessun candidato iniziale verificato";
                        }
                    }
                }
                else
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Tagli di scena insufficienti: source=" + srcCutCount + ", lang=" + lngCutCount + " (minimo: " + this._vsConfig.MinSceneCuts + ")");
                    this._lastInitialResult.FailureReason = "Tagli di scena insufficienti";
                }

                if (result == int.MinValue)
                {
                    if (sourceTemporalFingerprints != null && langTemporalFingerprints != null && srcCutCount >= this._vsConfig.MinSceneCuts && lngCutCount >= this._vsConfig.MinSceneCuts)
                    {
                        phaseStopwatch.Restart();
                        result = this.FindInitialDelayByTemporalCutMatching(sourceFrames, langFrames, sourceTemporalFingerprints, langTemporalFingerprints, sourceTimestampsMs, langTimestampsMs, validSourceCuts, validLangCuts, clusterTolMs, consistencyTolMs, nearestTolMs, minInitialOffsetMs, maxInitialOffsetMs);
                        phaseStopwatch.Stop();
                        this._timing.InitialCandidateVerifyMs += phaseStopwatch.ElapsedMilliseconds;
                    }
                }

                if (result == int.MinValue && this._fsConfig.AudioGlobalEnabled)
                {
                    result = this.FindInitialDelayByAudioGlobal(sourceFile, languageFile, true, int.MinValue);
                }

                if (result == int.MinValue)
                {
                    phaseStopwatch.Restart();
                    result = this.FindInitialDelayByVisualScanFallback(sourceFile, languageFile, fps);
                    phaseStopwatch.Stop();
                    this._timing.InitialCandidateVerifyMs += phaseStopwatch.ElapsedMilliseconds;
                }

                if (result != int.MinValue && this.ShouldVerifyInitialWithAudioGlobal())
                {
                    if (!this.VerifyInitialDelayWithAudioGlobal(sourceFile, languageFile, result, frameIntervalMs))
                    {
                        result = int.MinValue;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Verifica un initial visuale debole contro l'audio globale
        /// </summary>
        private bool VerifyInitialDelayWithAudioGlobal(string sourceFile, string languageFile, int visualOffsetMs, double frameIntervalMs)
        {
            return this._audioInitialResolver.VerifyVisualInitial(sourceFile, languageFile, visualOffsetMs, frameIntervalMs, this._lastInitialResult, this._timing, out this._lastAudioGlobalResult);
        }

        /// <summary>
        /// Usa la fingerprint audio globale per proporre un delay iniziale quando il video non conclude
        /// Il risultato resta soggetto alla verifica checkpoint video
        /// </summary>
        private int FindInitialDelayByAudioGlobal(string sourceFile, string languageFile, bool applyCandidate, int visualOffsetMs)
        {
            return this._audioInitialResolver.FindInitialDelay(sourceFile, languageFile, applyCandidate, visualOffsetMs, this._lastInitialResult, this._timing, out this._lastAudioGlobalResult);
        }

        /// <summary>
        /// Decide se calcolare audio globale per validare un initial prodotto da fallback visuale debole
        /// </summary>
        private bool ShouldVerifyInitialWithAudioGlobal()
        {
            bool result = false;
            FrameSyncCandidate candidate;
            if (!this._fsConfig.AudioGlobalEnabled || this._lastInitialResult == null || this._lastInitialResult.BestCandidate == null)
            {
                return result;
            }

            candidate = this._lastInitialResult.BestCandidate;
            if (candidate.Source == FrameSyncCandidate.LOCAL_SEARCH && candidate.CombinedScore > 0.0 && candidate.CombinedScore < 0.45)
            {
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Genera candidati initial accoppiando ogni cut sorgente con i cut lingua più simili per fingerprint temporale
        /// Questo evita che il voting su timestamp grezzi domini quando VFR o cut non equivalenti spostano i cluster
        /// </summary>
        private int FindInitialDelayByTemporalCutMatching(List<byte[]> sourceFrames, List<byte[]> langFrames, double[][] sourceTemporalFingerprints, double[][] langTemporalFingerprints, double[] sourceTimestampsMs, double[] langTimestampsMs, List<int> validSourceCuts, List<int> validLangCuts, double clusterTolMs, double consistencyTolMs, double nearestTolMs, int minInitialOffsetMs, int maxInitialOffsetMs)
        {
            int result = int.MinValue;
            List<FrameSyncCandidate> candidates;
            InitialCandidateVerifyResult[] verifyResults;
            Thread[] candidateThreads;
            FrameSyncCandidate bestCandidate;
            FrameSyncCandidate secondCandidate;
            double candidateMargin;
            bool candidateAmbiguous;
            candidates = this.BuildTemporalCutMatchCandidates(sourceFrames, langFrames, sourceTemporalFingerprints, langTemporalFingerprints, sourceTimestampsMs, langTimestampsMs, validSourceCuts, validLangCuts, clusterTolMs, minInitialOffsetMs, maxInitialOffsetMs);

            if (candidates.Count == 0)
            {
                return result;
            }

            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Fingerprint cut matching: " + candidates.Count + " cluster candidati");

            verifyResults = new InitialCandidateVerifyResult[candidates.Count];
            candidateThreads = new Thread[candidates.Count];

            for (int c = 0; c < candidates.Count; c++)
            {
                int candidateIndex = c;
                candidateThreads[c] = new Thread(() =>
                {
                    InitialCandidateVerifyResult verifyResult = new InitialCandidateVerifyResult();
                    List<double> localDelays = new List<double>();
                    int localVerifiedCount;
                    double localScore;
                    double localBlurScore;
                    double localEdgeScore;
                    double localBlockScore;
                    double localMotionScore;
                    double localHashScore;
                    int localDescriptorVotes;
                    double localOffset = candidates[candidateIndex].OffsetMs;

                    verifyResult.Delay = this.VerifyInitialCandidate(sourceFrames, langFrames, sourceTemporalFingerprints, langTemporalFingerprints, sourceTimestampsMs, langTimestampsMs, validSourceCuts, validLangCuts, localOffset, consistencyTolMs, nearestTolMs, localDelays, out localVerifiedCount, out localScore, out localBlurScore, out localEdgeScore, out localBlockScore, out localMotionScore, out localHashScore, out localDescriptorVotes);
                    verifyResult.VerifiedCount = localVerifiedCount;
                    verifyResult.Score = localScore;
                    verifyResult.BlurScore = localBlurScore;
                    verifyResult.EdgeScore = localEdgeScore;
                    verifyResult.BlockScore = localBlockScore;
                    verifyResult.MotionScore = localMotionScore;
                    verifyResult.HashScore = localHashScore;
                    verifyResult.DescriptorVotes = localDescriptorVotes;
                    verifyResult.Delays = localDelays;
                    verifyResults[candidateIndex] = verifyResult;
                });
                candidateThreads[c].Start();
            }

            for (int c = 0; c < candidateThreads.Length; c++)
            {
                candidateThreads[c].Join();
            }

            for (int c = 0; c < candidates.Count; c++)
            {
                InitialCandidateVerifyResult verifyResult = verifyResults[c];
                if (verifyResult == null)
                {
                    continue;
                }

                candidates[c].MatchedCuts = verifyResult.VerifiedCount;
                candidates[c].TemporalScore = validSourceCuts.Count > 0 ? verifyResult.VerifiedCount / (double)validSourceCuts.Count : 0.0;
                candidates[c].VisualScore = verifyResult.Score > 0.0 ? verifyResult.Score : 0.0;
                candidates[c].BlurScore = verifyResult.BlurScore;
                candidates[c].EdgeScore = verifyResult.EdgeScore;
                candidates[c].BlockScore = verifyResult.BlockScore;
                candidates[c].MotionScore = verifyResult.MotionScore;
                candidates[c].HashScore = verifyResult.HashScore;
                candidates[c].DescriptorVotes = verifyResult.DescriptorVotes;
                candidates[c].DescriptorAgreement = verifyResult.DescriptorVotes / 6.0;
                candidates[c].CombinedScore = this._candidateScorer.ComputeCandidateScore(candidates[c].TemporalScore, verifyResult.Score, verifyResult.BlurScore, verifyResult.EdgeScore, verifyResult.BlockScore, verifyResult.MotionScore, verifyResult.HashScore);

                if (verifyResult.Delay != int.MinValue)
                {
                    candidates[c].OffsetMs = verifyResult.Delay;
                }
            }

            candidates.Sort((a, b) =>
            {
                int cmp = b.CombinedScore.CompareTo(a.CombinedScore);
                if (cmp != 0) { return cmp; }
                return b.VoteCount.CompareTo(a.VoteCount);
            });

            for (int i = 0; i < candidates.Count; i++)
            {
                this._lastInitialResult.Candidates.Add(candidates[i]);
            }

            bestCandidate = this._candidateScorer.SelectBestCandidate(candidates, clusterTolMs, this._fsConfig.InitialMinMatchedCuts, this._fsConfig.InitialMinScore, this._fsConfig.InitialMinMargin, out secondCandidate, out candidateMargin, out candidateAmbiguous);

            if (bestCandidate != null)
            {
                bestCandidate.SecondBestScore = secondCandidate != null ? secondCandidate.CombinedScore : 0.0;
                bestCandidate.Margin = candidateMargin;

                if (!candidateAmbiguous)
                {
                    result = bestCandidate.OffsetMs;
                    this._lastInitialResult.Success = true;
                    this._lastInitialResult.Ambiguous = false;
                    this._lastInitialResult.BestCandidate = bestCandidate;
                    this._lastInitialResult.FailureReason = "";
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Delay iniziale fingerprint: " + result + "ms (" + bestCandidate.MatchedCuts + " tagli, score=" + bestCandidate.CombinedScore.ToString("F3", CultureInfo.InvariantCulture) + ")");
                }
                else
                {
                    this._lastInitialResult.Ambiguous = true;
                    this._lastInitialResult.FailureReason = "Candidati fingerprint iniziali ambigui";
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Candidati fingerprint iniziali ambigui: best=" + bestCandidate.OffsetMs + "ms score=" + bestCandidate.CombinedScore.ToString("F3", CultureInfo.InvariantCulture) + ", second=" + secondCandidate.OffsetMs + "ms score=" + secondCandidate.CombinedScore.ToString("F3", CultureInfo.InvariantCulture));
                }
            }

            return result;
        }

        /// <summary>
        /// Costruisce cluster offset partendo dai migliori match fingerprint per ogni cut sorgente
        /// </summary>
        private List<FrameSyncCandidate> BuildTemporalCutMatchCandidates(List<byte[]> sourceFrames, List<byte[]> langFrames, double[][] sourceTemporalFingerprints, double[][] langTemporalFingerprints, double[] sourceTimestampsMs, double[] langTimestampsMs, List<int> validSourceCuts, List<int> validLangCuts, double clusterTolMs, int minInitialOffsetMs, int maxInitialOffsetMs)
        {
            int maxCandidateCount = validSourceCuts.Count * INITIAL_FINGERPRINT_TOP_MATCHES_PER_CUT;
            double[] offsets = new double[maxCandidateCount];
            int offsetCount = 0;
            double minCorrelation = this._vsConfig.FingerprintCorrelationThreshold - 0.08;
            List<FrameSyncCandidate> result = new List<FrameSyncCandidate>();

            if (minCorrelation < INITIAL_FINGERPRINT_MIN_CORRELATION)
            {
                minCorrelation = INITIAL_FINGERPRINT_MIN_CORRELATION;
            }

            for (int s = 0; s < validSourceCuts.Count; s++)
            {
                double[] srcFingerprint = this.GetTemporalFingerprintCached(sourceFrames, validSourceCuts[s], sourceTemporalFingerprints);
                double[] topScores = new double[INITIAL_FINGERPRINT_TOP_MATCHES_PER_CUT];
                double[] topOffsets = new double[INITIAL_FINGERPRINT_TOP_MATCHES_PER_CUT];

                if (srcFingerprint == null)
                {
                    continue;
                }

                for (int i = 0; i < topScores.Length; i++)
                {
                    topScores[i] = -1.0;
                }

                for (int l = 0; l < validLangCuts.Count; l++)
                {
                    double[] langFingerprint = this.GetTemporalFingerprintCached(langFrames, validLangCuts[l], langTemporalFingerprints);
                    if (langFingerprint == null)
                    {
                        continue;
                    }

                    double correlation = this.ComputeFingerprintCorrelation(srcFingerprint, langFingerprint);
                    if (correlation < minCorrelation)
                    {
                        continue;
                    }

                    double offsetMs = langTimestampsMs[validLangCuts[l]] - sourceTimestampsMs[validSourceCuts[s]];
                    if (offsetMs < minInitialOffsetMs || offsetMs > maxInitialOffsetMs)
                    {
                        continue;
                    }

                    for (int top = 0; top < topScores.Length; top++)
                    {
                        if (correlation > topScores[top])
                        {
                            for (int move = topScores.Length - 1; move > top; move--)
                            {
                                topScores[move] = topScores[move - 1];
                                topOffsets[move] = topOffsets[move - 1];
                            }

                            topScores[top] = correlation;
                            topOffsets[top] = offsetMs;
                            break;
                        }
                    }
                }

                for (int top = 0; top < topScores.Length; top++)
                {
                    if (topScores[top] >= minCorrelation && offsetCount < offsets.Length)
                    {
                        offsets[offsetCount] = topOffsets[top];
                        offsetCount++;
                    }
                }
            }

            if (offsetCount > 0)
            {
                Array.Sort(offsets, 0, offsetCount);
                result = this._offsetCandidateBuilder.SelectInitialCandidates(offsets, offsetCount, clusterTolMs, MAX_INITIAL_CANDIDATES, this.GetMaxAbsOffset(minInitialOffsetMs, maxInitialOffsetMs));
                for (int i = 0; i < result.Count; i++)
                {
                    result[i].Source = FrameSyncCandidate.TEMPORAL_FINGERPRINT;
                }
            }

            return result;
        }

        /// <summary>
        /// Verifica un candidato iniziale usando prima SSIM sui cut e poi fingerprint temporale
        /// </summary>
        /// <param name="sourceFrames">Frame sorgente</param>
        /// <param name="langFrames">Frame lingua</param>
        /// <param name="sourceTemporalFingerprints">Fingerprint temporali source</param>
        /// <param name="langTemporalFingerprints">Fingerprint temporali lingua</param>
        /// <param name="sourceTimestampsMs">Timestamp frame sorgente</param>
        /// <param name="langTimestampsMs">Timestamp frame lingua</param>
        /// <param name="validSourceCuts">Cut sorgente utilizzabili</param>
        /// <param name="validLangCuts">Cut lingua utilizzabili</param>
        /// <param name="candidateOffset">Offset candidato interno, langTime - sourceTime</param>
        /// <param name="frameIntervalMs">Intervallo frame in millisecondi</param>
        /// <param name="nearestTolMs">Tolleranza per cut vicino</param>
        /// <param name="verifiedDelays">Delay verificati prodotti dal candidato</param>
        /// <param name="verifiedCount">Numero cut verificati</param>
        /// <param name="bestScore">Miglior score visuale o correlazione negativa se fallback fingerprint</param>
        /// <param name="bestBlurScore">Miglior score blur/denoise</param>
        /// <param name="bestEdgeScore">Miglior score edge</param>
        /// <param name="bestBlockScore">Miglior score fingerprint blocchi</param>
        /// <param name="bestMotionScore">Miglior score movimento a blocchi</param>
        /// <param name="bestHashScore">Miglior score hash percettivo</param>
        /// <param name="bestDescriptorVotes">Numero descriptor concordanti del miglior match</param>
        /// <returns>Delay interno verificato, int.MinValue se non valido</returns>
        private int VerifyInitialCandidate(List<byte[]> sourceFrames, List<byte[]> langFrames, double[][] sourceTemporalFingerprints, double[][] langTemporalFingerprints, double[] sourceTimestampsMs, double[] langTimestampsMs, List<int> validSourceCuts, List<int> validLangCuts, double candidateOffset, double frameIntervalMs, double nearestTolMs, List<double> verifiedDelays, out int verifiedCount, out double bestScore, out double bestBlurScore, out double bestEdgeScore, out double bestBlockScore, out double bestMotionScore, out double bestHashScore, out int bestDescriptorVotes)
        {
            int result = int.MinValue;
            double srcCutMs;
            double expectedLangCutMs;
            int nearestLangCutIdx;
            double nearestDistMs;
            int sigStartSrc;
            int sigStartLng;
            double blurCorrelation = 0.0;
            double edgeCorrelation = 0.0;
            double blockCorrelation = 0.0;
            double motionCorrelation = 0.0;
            double hashSimilarity = 0.0;
            double visualScore;
            double localSsim;
            double localBlurCorrelation;
            double localEdgeCorrelation;
            double localBlockCorrelation;
            double localEdgeBlockCorrelation;
            double localMotionCorrelation;
            double localHashSimilarity;
            double localVisualScore;
            int descriptorVotes;
            int localDescriptorVotes;
            int bestLocalShift;
            double medianDelay;
            int consistentCount = 0;
            bestScore = 0.0;
            bestBlurScore = 0.0;
            bestEdgeScore = 0.0;
            bestBlockScore = 0.0;
            bestMotionScore = 0.0;
            bestHashScore = 0.0;
            bestDescriptorVotes = 0;
            verifiedDelays.Clear();

            // Primo passaggio: associa ogni cut source al cut language più vicino previsto
            // dall'offset candidato, poi verifica il contenuto visuale attorno al taglio.
            for (int s = 0; s < validSourceCuts.Count; s++)
            {
                srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                expectedLangCutMs = srcCutMs + candidateOffset;

                nearestLangCutIdx = this.FindNearestCut(langTimestampsMs, validLangCuts, expectedLangCutMs, out nearestDistMs);

                if (nearestLangCutIdx >= 0 && nearestDistMs <= nearestTolMs)
                {
                    sigStartSrc = validSourceCuts[s] - this._vsConfig.CutHalfWindow;
                    sigStartLng = validLangCuts[nearestLangCutIdx] - this._vsConfig.CutHalfWindow;

                    if (sigStartLng >= 0 && sigStartLng + this._vsConfig.CutSignatureLength <= langFrames.Count)
                    {
                        visualScore = 0.0;
                        descriptorVotes = 0;
                        bestLocalShift = 0;

                        // Compensa piccoli errori di quantizzazione/PTS provando il cut language
                        // con uno shift locale di pochi frame.
                        for (int shift = -2; shift <= 2; shift++)
                        {
                            int shiftedSigStartLng = sigStartLng + shift;
                            if (shiftedSigStartLng < 0 || shiftedSigStartLng + this._vsConfig.CutSignatureLength > langFrames.Count)
                            {
                                continue;
                            }

                            localSsim = this.ComputeSequenceSsim(sourceFrames, sigStartSrc, langFrames, shiftedSigStartLng, this._vsConfig.CutSignatureLength);
                            localBlurCorrelation = this.ComputeSequenceBlurredCorrelation(sourceFrames, sigStartSrc, langFrames, shiftedSigStartLng, this._vsConfig.CutSignatureLength);
                            localEdgeCorrelation = this.ComputeSequenceEdgeCorrelation(sourceFrames, sigStartSrc, langFrames, shiftedSigStartLng, this._vsConfig.CutSignatureLength);
                            localBlockCorrelation = this.ComputeSequenceBlockCorrelation(sourceFrames, sigStartSrc, langFrames, shiftedSigStartLng, this._vsConfig.CutSignatureLength);
                            localEdgeBlockCorrelation = this.ComputeSequenceEdgeBlockCorrelation(sourceFrames, sigStartSrc, langFrames, shiftedSigStartLng, this._vsConfig.CutSignatureLength);
                            localMotionCorrelation = this.ComputeSequenceBlockMotionCorrelation(sourceFrames, sigStartSrc, langFrames, shiftedSigStartLng, this._vsConfig.CutSignatureLength);
                            localHashSimilarity = this.ComputeSequenceHashSimilarity(sourceFrames, sigStartSrc, langFrames, shiftedSigStartLng, this._vsConfig.CutSignatureLength);
                            localBlockCorrelation = (localBlockCorrelation * 0.65) + (localEdgeBlockCorrelation * 0.35);
                            localVisualScore = this._candidateScorer.ComputeVisualCandidateScore(localSsim, localBlurCorrelation, localEdgeCorrelation, localBlockCorrelation, localMotionCorrelation, localHashSimilarity);
                            localDescriptorVotes = this._candidateScorer.CountDescriptorVotes(localSsim, localBlurCorrelation, localEdgeCorrelation, localBlockCorrelation, localMotionCorrelation, localHashSimilarity);

                            // Preferisce lo score visuale migliore; a score quasi pari sceglie il match
                            // con più descriptor concordanti, più robusto su frame statici o compressi.
                            if (localVisualScore > visualScore || (localDescriptorVotes > descriptorVotes && Math.Abs(localVisualScore - visualScore) < 0.02))
                            {
                                blurCorrelation = localBlurCorrelation;
                                edgeCorrelation = localEdgeCorrelation;
                                blockCorrelation = localBlockCorrelation;
                                motionCorrelation = localMotionCorrelation;
                                hashSimilarity = localHashSimilarity;
                                visualScore = localVisualScore;
                                descriptorVotes = localDescriptorVotes;
                                bestLocalShift = shift;
                            }
                        }

                        // Un cut entra nel set verificato solo se abbastanza descriptor concordano.
                        // Il delay salvato usa il timestamp reale del cut shiftato, non il candidato teorico.
                        if (descriptorVotes >= this._fsConfig.MinDescriptorVotes)
                        {
                            double actualLngMs = langTimestampsMs[validLangCuts[nearestLangCutIdx]];
                            if (bestLocalShift != 0)
                            {
                                int shiftedCutIndex = validLangCuts[nearestLangCutIdx] + bestLocalShift;
                                if (shiftedCutIndex >= 0 && shiftedCutIndex < langTimestampsMs.Length)
                                {
                                    actualLngMs = langTimestampsMs[shiftedCutIndex];
                                }
                            }
                            verifiedDelays.Add(actualLngMs - srcCutMs);
                            if (visualScore > bestScore)
                            {
                                bestScore = visualScore;
                                bestDescriptorVotes = descriptorVotes;
                            }
                            if (blurCorrelation > bestBlurScore)
                            {
                                bestBlurScore = blurCorrelation;
                            }
                            if (edgeCorrelation > bestEdgeScore)
                            {
                                bestEdgeScore = edgeCorrelation;
                            }
                            if (blockCorrelation > bestBlockScore)
                            {
                                bestBlockScore = blockCorrelation;
                            }
                            if (motionCorrelation > bestMotionScore)
                            {
                                bestMotionScore = motionCorrelation;
                            }
                            if (hashSimilarity > bestHashScore)
                            {
                                bestHashScore = hashSimilarity;
                            }
                        }
                    }
                }
            }

            verifiedCount = verifiedDelays.Count;

            if (verifiedCount < this._vsConfig.MinSceneCuts)
            {
                // Fallback: se i descriptor visuali non danno abbastanza cut validi,
                // usa fingerprint temporali meno selettivi ma ancora ancorati ai cut.
                verifiedDelays.Clear();
                bestScore = 0.0;
                bestBlurScore = 0.0;
                bestEdgeScore = 0.0;
                bestBlockScore = 0.0;
                bestMotionScore = 0.0;
                bestHashScore = 0.0;
                bestDescriptorVotes = 0;

                for (int s = 0; s < validSourceCuts.Count; s++)
                {
                    srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                    expectedLangCutMs = srcCutMs + candidateOffset;

                    nearestLangCutIdx = this.FindNearestCut(langTimestampsMs, validLangCuts, expectedLangCutMs, out nearestDistMs);

                    if (nearestLangCutIdx >= 0 && nearestDistMs <= nearestTolMs)
                    {
                        double[] srcFingerprint = this.GetTemporalFingerprintCached(sourceFrames, validSourceCuts[s], sourceTemporalFingerprints);
                        double[] lngFingerprint = this.GetTemporalFingerprintCached(langFrames, validLangCuts[nearestLangCutIdx], langTemporalFingerprints);

                        if (srcFingerprint != null && lngFingerprint != null)
                        {
                            double correlation = this.ComputeFingerprintCorrelation(srcFingerprint, lngFingerprint);

                            if (correlation >= this._vsConfig.FingerprintCorrelationThreshold)
                            {
                                double actualLngMs = langTimestampsMs[validLangCuts[nearestLangCutIdx]];
                                verifiedDelays.Add(actualLngMs - srcCutMs);
                                if (bestScore == 0.0 || correlation > -bestScore)
                                {
                                    bestScore = -correlation;
                                }
                            }
                        }
                    }
                }

                verifiedCount = verifiedDelays.Count;
            }

            if (verifiedDelays.Count >= this._vsConfig.MinSceneCuts)
            {
                // L'offset finale del candidato è la mediana dei delay verificati:
                // riduce l'impatto di un singolo cut sbagliato o di un PTS locale rumoroso.
                verifiedDelays.Sort();
                medianDelay = verifiedDelays[verifiedDelays.Count / 2];

                // La mediana viene accettata solo se abbastanza delay cadono entro un frame:
                // senza questa coerenza il candidato è temporalmente instabile.
                for (int i = 0; i < verifiedDelays.Count; i++)
                {
                    if (Math.Abs(verifiedDelays[i] - medianDelay) <= frameIntervalMs)
                    {
                        consistentCount++;
                    }
                }

                if (consistentCount >= this._vsConfig.MinSceneCuts)
                {
                    result = (int)Math.Round(medianDelay);
                }
                else
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "    Candidato non consistente: " + consistentCount + "/" + verifiedDelays.Count + " entro 1 frame");
                }
            }

            return result;
        }

        /// <summary>
        /// Fallback iniziale per sorgenti lente/scure dove i tagli scena sono pochi o non affidabili
        /// Estrae una finestra più lunga a basso FPS e cerca l'offset su campioni ad alto movimento
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="languageFile">File lingua</param>
        /// <param name="sourceFps">FPS sorgente usato per arrotondare al frame più vicino</param>
        /// <returns>Delay iniziale positivo, oppure int.MinValue</returns>
        private int FindInitialDelayByVisualScanFallback(string sourceFile, string languageFile, double sourceFps)
        {
            int result = int.MinValue;
            List<byte[]> sourceFrames;
            List<byte[]> langFrames;
            double[] sourceTimestampsMs;
            double[] langTimestampsMs;
            List<int> sampleIndices;
            VisualScanFrameDescriptor[] sourceDescriptors;
            VisualScanFrameDescriptor[] langDescriptors;
            VisualScanCandidate best = new VisualScanCandidate();
            VisualScanCandidate second = new VisualScanCandidate();
            List<VisualScanCandidate> fastCandidates = new List<VisualScanCandidate>();
            List<int> exactOffsets;
            VisualScanCandidate[] workerBestCandidates;
            VisualScanCandidate[] workerSecondCandidates;
            List<VisualScanCandidate>[] workerTopCandidates;
            int threadCount = Environment.ProcessorCount;
            Thread[] workers;
            int offsetCount;
            int minVisualOffsetMs;
            int maxVisualOffsetMs;
            int frameIntervalMs;
            int roundedOffset;
            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  Fallback visuale esteso: finestra " + VISUAL_SCAN_SOURCE_DURATION_SEC + "s/" + VISUAL_SCAN_LANG_DURATION_SEC + "s a " + VISUAL_SCAN_FPS.ToString("F1", CultureInfo.InvariantCulture) + "fps");
            this.CalculateWindowOffsetRange(this._fsConfig.SourceStartSec * 1000, VISUAL_SCAN_SOURCE_DURATION_SEC, VISUAL_SCAN_LANG_DURATION_SEC, out minVisualOffsetMs, out maxVisualOffsetMs);

            this.ExtractVisualScanSegments(sourceFile, languageFile, out sourceFrames, out sourceTimestampsMs, out langFrames, out langTimestampsMs);

            if (sourceFrames != null && langFrames != null && sourceFrames.Count > 2 && langFrames.Count > 2)
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Range offset fallback visuale da finestra: " + minVisualOffsetMs + "ms.." + maxVisualOffsetMs + "ms");
                sampleIndices = this._visualScanMatcher.SelectVisualScanSamples(sourceFrames, sourceTimestampsMs);

                if (sampleIndices.Count >= 8)
                {
                    sourceDescriptors = this._visualScanMatcher.BuildVisualScanDescriptors(sourceFrames);
                    langDescriptors = this._visualScanMatcher.BuildVisualScanDescriptors(langFrames);
                    offsetCount = ((maxVisualOffsetMs - minVisualOffsetMs) / VISUAL_SCAN_OFFSET_STEP_MS) + 1;
                    if (threadCount > offsetCount)
                    {
                        threadCount = offsetCount;
                    }
                    if (threadCount < 1)
                    {
                        threadCount = 1;
                    }

                    workers = new Thread[threadCount];
                    workerBestCandidates = new VisualScanCandidate[threadCount];
                    workerSecondCandidates = new VisualScanCandidate[threadCount];
                    workerTopCandidates = new List<VisualScanCandidate>[threadCount];
                    for (int w = 0; w < threadCount; w++)
                    {
                        int workerIndex = w;
                        workers[w] = new Thread(() =>
                        {
                            VisualScanCandidate localBest = new VisualScanCandidate();
                            VisualScanCandidate localSecond = new VisualScanCandidate();
                            List<VisualScanCandidate> localTop = new List<VisualScanCandidate>();

                            for (int offsetIndex = workerIndex; offsetIndex < offsetCount; offsetIndex += threadCount)
                            {
                                int offsetMs = minVisualOffsetMs + (offsetIndex * VISUAL_SCAN_OFFSET_STEP_MS);
                                VisualScanCandidate candidate = this._visualScanMatcher.ScoreVisualScanOffset(sourceDescriptors, langDescriptors, sourceTimestampsMs, langTimestampsMs, sampleIndices, offsetMs, VISUAL_SCAN_OFFSET_STEP_MS, false);
                                this._visualScanMatcher.TrackVisualScanCandidate(candidate, ref localBest, ref localSecond);
                                this._visualScanMatcher.AddTopVisualScanCandidate(localTop, candidate, VISUAL_SCAN_FAST_TOP_CANDIDATES);
                            }

                            workerBestCandidates[workerIndex] = localBest;
                            workerSecondCandidates[workerIndex] = localSecond;
                            workerTopCandidates[workerIndex] = localTop;
                        });
                        workers[w].Start();
                    }

                    for (int w = 0; w < threadCount; w++)
                    {
                        workers[w].Join();
                    }

                    for (int w = 0; w < threadCount; w++)
                    {
                        this._visualScanMatcher.TrackVisualScanCandidate(workerBestCandidates[w], ref best, ref second);
                        this._visualScanMatcher.TrackVisualScanCandidate(workerSecondCandidates[w], ref best, ref second);
                        for (int i = 0; i < workerTopCandidates[w].Count; i++)
                        {
                            this._visualScanMatcher.AddTopVisualScanCandidate(fastCandidates, workerTopCandidates[w][i], VISUAL_SCAN_FAST_TOP_CANDIDATES);
                        }
                    }

                    best = new VisualScanCandidate();
                    second = new VisualScanCandidate();
                    exactOffsets = this._visualScanMatcher.BuildExactVisualScanOffsets(fastCandidates, minVisualOffsetMs, maxVisualOffsetMs);
                    for (int i = 0; i < exactOffsets.Count; i++)
                    {
                        VisualScanCandidate exactCandidate = this._visualScanMatcher.ScoreVisualScanOffset(sourceDescriptors, langDescriptors, sourceTimestampsMs, langTimestampsMs, sampleIndices, exactOffsets[i], VISUAL_SCAN_OFFSET_STEP_MS, true);
                        this._visualScanMatcher.TrackVisualScanCandidate(exactCandidate, ref best, ref second);
                    }

                    frameIntervalMs = (int)Math.Round(1000.0 / sourceFps);
                    if (frameIntervalMs < 1)
                    {
                        frameIntervalMs = 1;
                    }

                    bool strongMatch = best.SampleCount >= 8 && best.Score >= 0.72 && best.Score - second.Score >= 0.035;
                    bool marginConfirmedMatch = best.SampleCount >= 16 &&
                        best.Score >= VISUAL_SCAN_PROMISING_MIN_SCORE &&
                        best.Score - second.Score >= VISUAL_SCAN_PROMISING_MIN_MARGIN;

                    if (strongMatch || marginConfirmedMatch)
                    {
                        roundedOffset = (int)Math.Round(best.OffsetMs / (double)frameIntervalMs) * frameIntervalMs;
                        result = roundedOffset;

                        FrameSyncCandidate candidate = new FrameSyncCandidate();
                        candidate.OffsetMs = result;
                        candidate.Source = FrameSyncCandidate.LOCAL_SEARCH;
                        candidate.VoteCount = best.SampleCount;
                        candidate.VisualScore = best.Score;
                        candidate.BlurScore = best.BlurScore;
                        candidate.EdgeScore = best.EdgeScore;
                        candidate.BlockScore = best.BlockScore;
                        candidate.MotionScore = best.MotionScore;
                        candidate.HashScore = best.HashScore;
                        candidate.DescriptorVotes = best.DescriptorVotes;
                        candidate.DescriptorAgreement = best.DescriptorVotes / 6.0;
                        candidate.CombinedScore = best.Score;
                        candidate.SecondBestScore = second.Score;
                        candidate.Margin = best.Score - second.Score;
                        candidate.MatchedCuts = best.SampleCount;

                        this._lastInitialResult.Candidates.Add(candidate);
                        this._lastInitialResult.BestCandidate = candidate;
                        this._lastInitialResult.Success = true;
                        this._lastInitialResult.Ambiguous = false;
                        this._lastInitialResult.FailureReason = "";

                        if (strongMatch)
                        {
                            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  Fallback visuale confermato: offset=" + result + "ms, score=" + best.Score.ToString("F3", CultureInfo.InvariantCulture) + ", second=" + second.Score.ToString("F3", CultureInfo.InvariantCulture) + ", campioni=" + best.SampleCount);
                        }
                        else
                        {
                            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  Fallback visuale confermato da margine: offset=" + result + "ms, score=" + best.Score.ToString("F3", CultureInfo.InvariantCulture) + ", second=" + second.Score.ToString("F3", CultureInfo.InvariantCulture) + ", voti=" + best.DescriptorVotes + ", campioni=" + best.SampleCount);
                        }
                    }
                    else if (best.SampleCount >= 8 && best.Score >= 0.30)
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Fallback visuale promettente ma non abbastanza netto: best=" + best.OffsetMs + "ms score=" + best.Score.ToString("F3", CultureInfo.InvariantCulture) + ", second=" + second.Score.ToString("F3", CultureInfo.InvariantCulture) + ", campioni=" + best.SampleCount);
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Fallback visuale non conclusivo: best=" + best.OffsetMs + "ms score=" + best.Score.ToString("F3", CultureInfo.InvariantCulture) + ", second=" + second.Score.ToString("F3", CultureInfo.InvariantCulture) + ", campioni=" + best.SampleCount);
                    }
                }
                else
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Fallback visuale non disponibile: campioni informativi insufficienti (" + sampleIndices.Count + ")");
                }
            }

            return result;
        }

        /// <summary>
        /// Estrae in parallelo i segmenti a basso FPS per il fallback visuale
        /// </summary>
        private void ExtractVisualScanSegments(string sourceFile, string languageFile, out List<byte[]> sourceFrames, out double[] sourceTimestampsMs, out List<byte[]> langFrames, out double[] langTimestampsMs)
        {
            List<byte[]> srcFrames = null;
            List<byte[]> lngFrames = null;
            double[] srcTs = null;
            double[] lngTs = null;
            Thread sourceThread;
            Thread langThread;
            FrameExtractProfile sourceProfile = this.BuildFrameExtractProfile(sourceFile, this._fsConfig.SourceStartSec * 1000, VISUAL_SCAN_SOURCE_DURATION_SEC, VISUAL_SCAN_FPS, this._geometryCropSourceToFourThree, this._analysisCropSourcePx);
            FrameExtractProfile languageProfile = this.BuildFrameExtractProfile(languageFile, 0, VISUAL_SCAN_LANG_DURATION_SEC, VISUAL_SCAN_FPS, this._geometryCropLanguageToFourThree, this._analysisCropLanguagePx);

            sourceThread = new Thread(() =>
            {
                this.ExtractSegmentCached(sourceProfile, out srcFrames, out srcTs);
            });
            langThread = new Thread(() =>
            {
                this.ExtractSegmentCached(languageProfile, out lngFrames, out lngTs);
            });

            sourceThread.Start();
            langThread.Start();
            sourceThread.Join();
            langThread.Join();

            sourceFrames = srcFrames;
            sourceTimestampsMs = srcTs;
            langFrames = lngFrames;
            langTimestampsMs = lngTs;
        }

        /// <summary>
        /// Restituisce il temporal fingerprint usando cache per evitare ricalcoli tra candidati
        /// </summary>
        private double[] GetTemporalFingerprintCached(List<byte[]> frames, int cutIndex, double[][] cache)
        {
            if (cutIndex < 0 || cutIndex >= cache.Length)
            {
                return null;
            }

            if (cache[cutIndex] == null)
            {
                cache[cutIndex] = this.ComputeTemporalFingerprint(frames, cutIndex);
            }

            return cache[cutIndex];
        }

        /// <summary>
        /// Verifica un checkpoint con ricerca locale visuale quando i cut non bastano
        /// </summary>
        private int VerifyAtPointByLocalVisualSearch(List<byte[]> sourceFrames, List<byte[]> langFrames, double[] sourceTimestampsMs, double[] langTimestampsMs, int baseOffset, double timestampMatchToleranceMs, int searchRadiusMs, FrameSyncPointResult pointResult, out double bestScore)
        {
            int result = int.MinValue;
            int radiusMs = searchRadiusMs;
            List<int> sampleIndices;
            VisualScanFrameDescriptor[] sourceDescriptors;
            VisualScanFrameDescriptor[] langDescriptors;
            VisualScanCandidate best = new VisualScanCandidate();
            VisualScanCandidate second = new VisualScanCandidate();
            VisualScanCandidate candidate;
            bestScore = 0.0;

            if (radiusMs < 2000)
            {
                radiusMs = 2000;
            }
            if (radiusMs > 4000)
            {
                radiusMs = 4000;
            }

            sampleIndices = this.SelectCheckpointLocalSearchSamples(sourceFrames, sourceTimestampsMs);
            if (sampleIndices.Count < CHECKPOINT_LOCAL_MIN_SAMPLES)
            {
                pointResult.RejectReason = "Campioni locali insufficienti";
                return result;
            }

            sourceDescriptors = this._visualScanMatcher.BuildVisualScanDescriptors(sourceFrames);
            langDescriptors = this._visualScanMatcher.BuildVisualScanDescriptors(langFrames);

            for (int offsetMs = baseOffset - radiusMs; offsetMs <= baseOffset + radiusMs; offsetMs += CHECKPOINT_LOCAL_COARSE_STEP_MS)
            {
                candidate = this._visualScanMatcher.ScoreVisualScanOffset(sourceDescriptors, langDescriptors, sourceTimestampsMs, langTimestampsMs, sampleIndices, offsetMs, timestampMatchToleranceMs);
                this._visualScanMatcher.TrackCheckpointLocalCandidate(candidate, ref best, ref second, CHECKPOINT_LOCAL_COARSE_STEP_MS);
            }

            if (best.SampleCount >= CHECKPOINT_LOCAL_MIN_SAMPLES)
            {
                int refineStart = best.OffsetMs - CHECKPOINT_LOCAL_COARSE_STEP_MS;
                int refineEnd = best.OffsetMs + CHECKPOINT_LOCAL_COARSE_STEP_MS;
                for (int offsetMs = refineStart; offsetMs <= refineEnd; offsetMs += 10)
                {
                    candidate = this._visualScanMatcher.ScoreVisualScanOffset(sourceDescriptors, langDescriptors, sourceTimestampsMs, langTimestampsMs, sampleIndices, offsetMs, timestampMatchToleranceMs);
                    this._visualScanMatcher.TrackCheckpointLocalCandidate(candidate, ref best, ref second, 40);
                }
            }

            if (best.SampleCount >= CHECKPOINT_LOCAL_MIN_SAMPLES &&
                best.Score >= this._fsConfig.CheckpointMinScore &&
                best.Score - second.Score >= this._fsConfig.CheckpointMinMargin &&
                best.DescriptorVotes >= this._fsConfig.MinDescriptorVotes)
            {
                result = best.OffsetMs;
                bestScore = best.Score;

                pointResult.Accepted = true;
                pointResult.RejectReason = "";
                pointResult.BestOffsetMs = -result;
                pointResult.BestScore = best.Score;
                pointResult.BlurScore = best.BlurScore;
                pointResult.SecondBestScore = second.Score;
                pointResult.Margin = best.Score - second.Score;
                pointResult.DescriptorVotes = best.DescriptorVotes;
                pointResult.DescriptorAgreement = best.DescriptorVotes / 6.0;
                pointResult.MotionScore = best.MotionScore;
                pointResult.MatchMethod = FrameSyncCandidate.LOCAL_SEARCH;
            }
            else
            {
                pointResult.BestScore = best.Score;
                pointResult.BlurScore = best.BlurScore;
                pointResult.SecondBestScore = second.Score;
                pointResult.Margin = best.Score - second.Score;
                pointResult.DescriptorVotes = best.DescriptorVotes;
                pointResult.DescriptorAgreement = best.DescriptorVotes / 6.0;
                pointResult.MotionScore = best.MotionScore;
                pointResult.RejectReason = "Local search non conclusivo";
            }

            return result;
        }

        /// <summary>
        /// Seleziona campioni informativi nel segmento checkpoint
        /// </summary>
        private List<int> SelectCheckpointLocalSearchSamples(List<byte[]> frames, double[] timestampsMs)
        {
            return this._visualScanMatcher.SelectCheckpointLocalSearchSamples(frames, timestampsMs);
        }

        /// <summary>
        /// Calcola varianza luma leggera per scartare frame neri/statici
        /// </summary>
        private double ComputeFrameVariance(byte[] frame)
        {
            return this._visualScanMatcher.ComputeFrameVariance(frame);
        }

        /// <summary>
        /// Trova il taglio lingua più vicino al timestamp atteso
        /// </summary>
        /// <param name="langTimestampsMs">Timestamp frame lingua</param>
        /// <param name="validLangCuts">Cut lingua utilizzabili</param>
        /// <param name="expectedLangCutMs">Timestamp atteso in millisecondi</param>
        /// <param name="nearestDistMs">Distanza del cut più vicino</param>
        /// <returns>Indice dentro validLangCuts, -1 se non disponibile</returns>
        private int FindNearestCut(double[] langTimestampsMs, List<int> validLangCuts, double expectedLangCutMs, out double nearestDistMs)
        {
            int result = -1;
            double distMs;
            nearestDistMs = double.MaxValue;

            for (int l = 0; l < validLangCuts.Count; l++)
            {
                distMs = Math.Abs(langTimestampsMs[validLangCuts[l]] - expectedLangCutMs);
                if (distMs < nearestDistMs)
                {
                    nearestDistMs = distMs;
                    result = l;
                }
            }

            return result;
        }

        /// <summary>
        /// Verifica offset a 9 punti distribuiti nel video in parallelo
        /// </summary>
        /// <param name="sourceFile">Percorso file video sorgente</param>
        /// <param name="languageFile">Percorso file video lingua</param>
        /// <param name="durationMs">Durata totale video in millisecondi</param>
        /// <param name="initialDelay">Delay iniziale stimato in ms</param>
        /// <param name="fps">Frame rate del video sorgente</param>
        /// <param name="sourceTargetFps">FPS target per normalizzazione source (0 = fps nativo)</param>
        /// <param name="langTargetFps">FPS target per normalizzazione lang (0 = fps nativo)</param>
        private void VerifyAtMultiplePoints(string sourceFile, string languageFile, int durationMs, int initialDelay, double fps, double sourceTargetFps, double langTargetFps)
        {
            int threadCount = 4;
            string srcFile = sourceFile;
            string lngFile = languageFile;
            int percentage;
            int retryCount = 0;
            double sourceFpsCopy = sourceTargetFps;
            double langFpsCopy = langTargetFps;
            Stopwatch phaseStopwatch = new Stopwatch();

            // Limita ai punti disponibili
            if (threadCount > this._vsConfig.NumCheckPoints)
            {
                threadCount = this._vsConfig.NumCheckPoints;
            }

            // Primo passaggio con finestra base
            Thread[] workers = new Thread[threadCount];
            phaseStopwatch.Start();

            for (int w = 0; w < threadCount; w++)
            {
                int workerIndex = w;

                workers[w] = new Thread(() =>
                {
                    for (int p = workerIndex; p < this._vsConfig.NumCheckPoints; p += threadCount)
                    {
                        int pct = (p + 1) * 10;
                        int pointMs = (int)((long)durationMs * pct / 100);

                        double ssim;
                        int offset = this.VerifyAtPoint(srcFile, lngFile, pointMs, initialDelay, fps, sourceFpsCopy, this._vsConfig.VerifySourceDurationSec, this._vsConfig.VerifyLangDurationSec, langFpsCopy, p, out ssim);

                        this._offsets[p] = offset;
                        this._ssimValues[p] = ssim;

                        if (offset != int.MinValue)
                        {
                            this._pointValid[p] = true;
                        }
                    }
                });

                workers[w].Start();
            }

            // Attende completamento primo passaggio
            for (int w = 0; w < threadCount; w++)
            {
                workers[w].Join();
            }
            phaseStopwatch.Stop();
            this._timing.CheckpointsBaseMs += phaseStopwatch.ElapsedMilliseconds;

            if (this._checkpointGrouper.CanSkipRetry(initialDelay, fps, this._pointValid, this._offsets, this._pointResults, this._ssimValues, out int baseValidCount, out int baseGroupCount, out double baseGroupScore) && this._vsConfig.NumCheckPoints - baseValidCount <= 1)
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Retry checkpoint saltato: base pass già conclusivo (" + baseGroupCount + "/" + baseValidCount + " coerenti, avg=" + baseGroupScore.ToString("F3", CultureInfo.InvariantCulture) + ")");
            }
            else
            {
                // Retry con finestra allargata per i punti falliti
                for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
                {
                    if (!this._pointValid[p])
                    {
                        retryCount++;
                    }
                }

                if (retryCount > 0)
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Retry " + retryCount + " punti con finestra allargata (" + this._vsConfig.VerifySourceRetrySec + "s/" + this._vsConfig.VerifyLangRetrySec + "s)...");

                    // Raccoglie indici dei punti falliti
                    List<int> failedPoints = new List<int>();
                    for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
                    {
                        if (!this._pointValid[p])
                        {
                            failedPoints.Add(p);
                        }
                    }

                    // Limita thread retry a 4 (ogni ffmpeg usa già auto-threading)
                    int retryThreadCount = 4;
                    if (retryThreadCount > failedPoints.Count)
                    {
                        retryThreadCount = failedPoints.Count;
                    }

                    Thread[] retryWorkers = new Thread[retryThreadCount];
                    List<int> failedPointsCopy = failedPoints;

                    phaseStopwatch.Restart();
                    for (int w = 0; w < retryThreadCount; w++)
                    {
                        int workerIndex = w;

                        retryWorkers[w] = new Thread(() =>
                        {
                            for (int f = workerIndex; f < failedPointsCopy.Count; f += retryThreadCount)
                            {
                                int pointIndex = failedPointsCopy[f];
                                int pct = (pointIndex + 1) * 10;
                                int pointMs = (int)((long)durationMs * pct / 100);

                                double ssim;
                                int offset = this.VerifyAtPoint(srcFile, lngFile, pointMs, initialDelay, fps, sourceFpsCopy, this._vsConfig.VerifySourceRetrySec, this._vsConfig.VerifyLangRetrySec, langFpsCopy, pointIndex, out ssim);

                                this._offsets[pointIndex] = offset;
                                this._ssimValues[pointIndex] = ssim;

                                if (offset != int.MinValue)
                                {
                                    this._pointValid[pointIndex] = true;
                                }
                            }
                        });

                        retryWorkers[w].Start();
                    }

                    // Attende completamento retry
                    for (int r = 0; r < retryThreadCount; r++)
                    {
                        retryWorkers[r].Join();
                    }
                    phaseStopwatch.Stop();
                    this._timing.CheckpointsRetryMs += phaseStopwatch.ElapsedMilliseconds;
                }
            }

            retryCount = 0;
            for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
            {
                if (!this._pointValid[p])
                {
                    retryCount++;
                }
            }

            if (retryCount > 0)
            {
                int adaptiveValidCount = 0;
                for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
                {
                    if (this._pointValid[p])
                    {
                        adaptiveValidCount++;
                    }
                }

                if (adaptiveValidCount >= this._fsConfig.MinValidPoints)
                {
                    retryCount = 0;
                }
            }

            if (retryCount > 0)
            {
                int adaptiveTargetValid = this._fsConfig.MinValidPoints;
                int adaptiveValidCount = 0;
                for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
                {
                    if (this._pointValid[p])
                    {
                        adaptiveValidCount++;
                    }
                }

                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Checkpoint adattivi di salvataggio: " + retryCount + " punti non informativi");
                phaseStopwatch.Restart();
                for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
                {
                    if (adaptiveValidCount >= adaptiveTargetValid)
                    {
                        break;
                    }

                    if (!this._pointValid[p])
                    {
                        double ssim;
                        int pct = (p + 1) * 10;
                        int pointMs = (int)((long)durationMs * pct / 100);
                        int offset = this.VerifyAdaptiveCheckpoint(srcFile, lngFile, durationMs, pointMs, initialDelay, fps, sourceFpsCopy, langFpsCopy, p, out ssim);

                        if (offset != int.MinValue)
                        {
                            this._offsets[p] = offset;
                            this._ssimValues[p] = ssim;
                            this._pointValid[p] = true;
                            adaptiveValidCount++;
                        }
                    }
                }
                phaseStopwatch.Stop();
                this._timing.CheckpointsRetryMs += phaseStopwatch.ElapsedMilliseconds;
            }

            // Log risultati
            for (int p = 0; p < this._vsConfig.NumCheckPoints; p++)
            {
                percentage = (p + 1) * 10;

                if (this._pointValid[p])
                {
                    string method = this._pointResults[p] != null && !string.IsNullOrEmpty(this._pointResults[p].MatchMethod) ? this._pointResults[p].MatchMethod : "match";
                    // Valore negativo = correlazione fingerprint, positivo = score visuale/combinato
                    if (this._ssimValues[p] < 0)
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  " + percentage + "%: OK offset=" + this._offsets[p] + "ms, corr=" + (-this._ssimValues[p]).ToString("F2", CultureInfo.InvariantCulture) + ", metodo=" + method);
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  " + percentage + "%: OK offset=" + this._offsets[p] + "ms, score=" + this._ssimValues[p].ToString("F3", CultureInfo.InvariantCulture) + ", metodo=" + method);
                    }
                }
                else
                {
                    string reason = this._pointResults[p] != null && !string.IsNullOrEmpty(this._pointResults[p].RejectReason) ? this._pointResults[p].RejectReason : "nessun match";
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  " + percentage + "%: FAIL " + reason);
                }
            }
        }

        /// <summary>
        /// Sostituisce un checkpoint fisso non informativo con punti vicini sulla timeline
        /// Accetta solo offset coerenti con l'initial; non usa la ricerca adattiva per correggere drift
        /// </summary>
        private int VerifyAdaptiveCheckpoint(string sourceFile, string languageFile, int durationMs, int basePointMs, int initialDelay, double fps, double sourceTargetFps, double langTargetFps, int checkpointIndex, out double bestSsim)
        {
            int result = int.MinValue;
            int[] shiftsMs = new int[] { -15000, 15000, -30000, 30000 };
            double frameIntervalMs = 1000.0 / fps;

            bestSsim = 0.0;

            if (frameIntervalMs < 1.0)
            {
                frameIntervalMs = 1.0;
            }

            for (int i = 0; i < shiftsMs.Length; i++)
            {
                int pointMs = basePointMs + shiftsMs[i];
                if (pointMs < 60000 || pointMs > durationMs - 60000)
                {
                    continue;
                }

                double ssim;
                int offset = this.VerifyAtPoint(sourceFile, languageFile, pointMs, initialDelay, fps, sourceTargetFps, this._vsConfig.VerifySourceDurationSec, this._vsConfig.VerifyLangDurationSec, langTargetFps, checkpointIndex, out ssim);

                if (offset != int.MinValue && this.IsCheckpointOffsetNearInitial(offset, initialDelay, frameIntervalMs))
                {
                    result = offset;
                    bestSsim = ssim;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Ottiene durata e frame rate del video tramite ffmpeg
        /// </summary>
        /// <param name="filePath">Percorso file video</param>
        /// <param name="durationMs">Durata in millisecondi (output)</param>
        /// <param name="fps">Frame rate rilevato (output)</param>
        /// <returns>True se le informazioni sono state ottenute</returns>
        private bool GetVideoInfo(string filePath, out int durationMs, out double fps)
        {
            FfmpegVideoInfoReader reader = new FfmpegVideoInfoReader(this._ffmpegPath, this._ffmpegConfig, LogSection.FrameSync);
            return reader.TryRead(filePath, out durationMs, out fps);
        }

        /// <summary>
        /// Verifica e raffina offset a un singolo punto temporale tramite cut-to-cut matching
        /// </summary>
        private int VerifyAtPoint(string sourceFile, string languageFile, int sourceTimestampMs, int baseOffset, double fps, double sourceTargetFps, int sourceDurationSec, int langDurationSec, double langTargetFps, int checkpointIndex, out double bestSsim)
        {
            bestSsim = 0.0;
            int resultOffset = int.MinValue;
            double frameIntervalMs = 1000.0 / fps;
            double nearestTolMs = 3.0 * frameIntervalMs;
            int halfSourceMs = (sourceDurationSec * 1000) / 2;
            int sourceStartMs = sourceTimestampMs - halfSourceMs;
            int langCenter = sourceTimestampMs + baseOffset;
            int halfLangMs = (langDurationSec * 1000) / 2;
            int langStartMs = langCenter - halfLangMs;
            List<byte[]> sourceFrames = null;
            List<byte[]> langFrames = null;
            double[] sourceTimestampsMs = null;
            double[] langTimestampsMs = null;
            double[][] sourceTemporalFingerprints;
            double[][] langTemporalFingerprints;
            string sourceFileCopy = sourceFile;
            string langFileCopy = languageFile;
            int sourceStartMsCopy;
            int langStartMsCopy;
            List<int> sourceCutsRaw;
            List<int> langCutsRaw;
            List<int> validSourceCuts = new List<int>();
            List<int> validLangCuts = new List<int>();
            int srcCutCount = 0;
            int lngCutCount = 0;
            List<double> cutDelays = new List<double>();
            List<FrameSyncCandidate> localCandidates;
            int searchRadiusMs = 0;
            int verifiedCount;
            int candidateOffset;
            double candidateScore;
            double candidateBlurScore;
            double candidateEdgeScore;
            double candidateBlockScore;
            double candidateMotionScore;
            double candidateHashScore;
            int candidateDescriptorVotes;
            FrameSyncCandidate bestCandidate;
            FrameSyncCandidate secondCandidate;
            double candidateMargin;
            bool candidateAmbiguous;
            double timestampMatchToleranceMs;
            FrameSyncPointResult pointResult = new FrameSyncPointResult();
            Stopwatch pointStopwatch = new Stopwatch();
            Stopwatch phaseStopwatch = new Stopwatch();

            pointStopwatch.Start();
            pointResult.CheckpointPercent = (checkpointIndex + 1) * 10;
            pointResult.ExpectedOffsetMs = -baseOffset;

            // Limita inizio segmenti a 0
            if (sourceStartMs < 0)
            {
                sourceStartMs = 0;
            }
            if (langStartMs < 0)
            {
                langStartMs = 0;
            }
            sourceStartMsCopy = sourceStartMs;
            langStartMsCopy = langStartMs;

            // Estrae i due segmenti in parallelo (fps forzato per garantire output CFR)
            double fpsCopy = sourceTargetFps;
            double langFpsCopy = langTargetFps;
            bool sourceGeometryCropCopy = this._geometryCropSourceToFourThree;
            bool languageGeometryCropCopy = this._geometryCropLanguageToFourThree;
            FrameExtractProfile sourceProfile = this.BuildFrameExtractProfile(sourceFileCopy, sourceStartMsCopy, sourceDurationSec, fpsCopy, sourceGeometryCropCopy, this._analysisCropSourcePx);
            FrameExtractProfile languageProfile = this.BuildFrameExtractProfile(langFileCopy, langStartMsCopy, langDurationSec, langFpsCopy, languageGeometryCropCopy, this._analysisCropLanguagePx);
            Thread sourceThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegmentCached(sourceProfile, out f, out t);
                sourceFrames = f;
                sourceTimestampsMs = t;
            });
            Thread langThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegmentCached(languageProfile, out f, out t);
                langFrames = f;
                langTimestampsMs = t;
            });
            phaseStopwatch.Start();
            sourceThread.Start();
            langThread.Start();
            sourceThread.Join();
            langThread.Join();
            phaseStopwatch.Stop();
            pointResult.ExtractMs = phaseStopwatch.ElapsedMilliseconds;

            // Verifica frame sufficienti
            if (sourceFrames != null && sourceFrames.Count >= this._vsConfig.CutSignatureLength && langFrames != null && langFrames.Count >= this._vsConfig.CutSignatureLength)
            {
                pointResult.SourceVariance = this.ComputeSegmentVariance(sourceFrames);
                pointResult.LanguageVariance = this.ComputeSegmentVariance(langFrames);
                pointResult.SourceBlackRatio = this.ComputeSegmentBlackRatio(sourceFrames);
                pointResult.LanguageBlackRatio = this.ComputeSegmentBlackRatio(langFrames);
                timestampMatchToleranceMs = this.ComputeTimestampMatchToleranceMs(sourceTimestampsMs, langTimestampsMs, frameIntervalMs);

                phaseStopwatch.Restart();
                this.TryAnchorCheckpointToInitial(sourceFrames, langFrames, sourceTimestampsMs, langTimestampsMs, baseOffset, timestampMatchToleranceMs, pointResult, out resultOffset, out bestSsim);
                phaseStopwatch.Stop();
                pointResult.CandidateMs += phaseStopwatch.ElapsedMilliseconds;

                // Rileva tagli in entrambi i segmenti
                if (resultOffset == int.MinValue)
                {
                    phaseStopwatch.Restart();
                    sourceCutsRaw = this.DetectSceneCuts(sourceFrames);
                    langCutsRaw = this.DetectSceneCuts(langFrames);

                    // Filtra tagli con margine sufficiente
                    for (int i = 0; i < sourceCutsRaw.Count; i++)
                    {
                        if (sourceCutsRaw[i] >= this._vsConfig.CutHalfWindow && sourceCutsRaw[i] + this._vsConfig.CutHalfWindow <= sourceFrames.Count)
                        {
                            validSourceCuts.Add(sourceCutsRaw[i]);
                        }
                    }
                    for (int i = 0; i < langCutsRaw.Count; i++)
                    {
                        if (langCutsRaw[i] >= this._vsConfig.CutHalfWindow && langCutsRaw[i] + this._vsConfig.CutHalfWindow <= langFrames.Count)
                        {
                            validLangCuts.Add(langCutsRaw[i]);
                        }
                    }

                    srcCutCount = validSourceCuts.Count;
                    lngCutCount = validLangCuts.Count;
                    phaseStopwatch.Stop();
                    pointResult.SceneCutMs = phaseStopwatch.ElapsedMilliseconds;
                }

                if (resultOffset == int.MinValue && srcCutCount > 0 && lngCutCount > 0)
                {
                    sourceTemporalFingerprints = new double[sourceFrames.Count][];
                    langTemporalFingerprints = new double[langFrames.Count][];

                    phaseStopwatch.Restart();
                    searchRadiusMs = ((langDurationSec - sourceDurationSec) * 1000) / 2;
                    if (searchRadiusMs < (int)Math.Round(nearestTolMs * 3.0))
                    {
                        searchRadiusMs = (int)Math.Round(nearestTolMs * 3.0);
                    }

                    localCandidates = this._offsetCandidateBuilder.BuildOffsetCandidates(sourceTimestampsMs, langTimestampsMs, validSourceCuts, validLangCuts, baseOffset - searchRadiusMs, baseOffset + searchRadiusMs, frameIntervalMs, MAX_INITIAL_CANDIDATES, this.GetMaxAbsOffset(baseOffset - searchRadiusMs, baseOffset + searchRadiusMs));

                    for (int c = 0; c < localCandidates.Count; c++)
                    {
                        candidateOffset = this.VerifyInitialCandidate(sourceFrames, langFrames, sourceTemporalFingerprints, langTemporalFingerprints, sourceTimestampsMs, langTimestampsMs, validSourceCuts, validLangCuts, localCandidates[c].OffsetMs, frameIntervalMs, nearestTolMs, cutDelays, out verifiedCount, out candidateScore, out candidateBlurScore, out candidateEdgeScore, out candidateBlockScore, out candidateMotionScore, out candidateHashScore, out candidateDescriptorVotes);
                        localCandidates[c].MatchedCuts = verifiedCount;
                        localCandidates[c].TemporalScore = (srcCutCount > 0) ? verifiedCount / (double)srcCutCount : 0.0;
                        localCandidates[c].VisualScore = candidateScore > 0.0 ? candidateScore : 0.0;
                        localCandidates[c].BlurScore = candidateBlurScore;
                        localCandidates[c].EdgeScore = candidateEdgeScore;
                        localCandidates[c].BlockScore = candidateBlockScore;
                        localCandidates[c].MotionScore = candidateMotionScore;
                        localCandidates[c].HashScore = candidateHashScore;
                        localCandidates[c].DescriptorVotes = candidateDescriptorVotes;
                        localCandidates[c].DescriptorAgreement = candidateDescriptorVotes / 6.0;
                        localCandidates[c].CombinedScore = this._candidateScorer.ComputeCandidateScore(localCandidates[c].TemporalScore, candidateScore, candidateBlurScore, candidateEdgeScore, candidateBlockScore, candidateMotionScore, candidateHashScore);

                        if (candidateOffset != int.MinValue)
                        {
                            localCandidates[c].OffsetMs = candidateOffset;
                        }
                    }

                    bestCandidate = this._candidateScorer.SelectBestCandidate(localCandidates, frameIntervalMs, this._vsConfig.MinSceneCuts, this._fsConfig.CheckpointMinScore, this._fsConfig.CheckpointMinMargin, out secondCandidate, out candidateMargin, out candidateAmbiguous);

                    if (bestCandidate != null)
                    {
                        bestCandidate.SecondBestScore = secondCandidate != null ? secondCandidate.CombinedScore : 0.0;
                        bestCandidate.Margin = candidateMargin;

                        pointResult.BestOffsetMs = -bestCandidate.OffsetMs;
                        pointResult.BestScore = bestCandidate.CombinedScore;
                        pointResult.BlurScore = bestCandidate.BlurScore;
                        pointResult.SecondBestScore = bestCandidate.SecondBestScore;
                        pointResult.Margin = bestCandidate.Margin;
                        pointResult.DescriptorVotes = bestCandidate.DescriptorVotes;
                        pointResult.DescriptorAgreement = bestCandidate.DescriptorAgreement;
                        pointResult.MotionScore = bestCandidate.MotionScore;
                        pointResult.MatchMethod = bestCandidate.VisualScore > 0.0 ? "VisualEdge" : FrameSyncCandidate.TEMPORAL_FINGERPRINT;

                        if (!candidateAmbiguous)
                        {
                            resultOffset = bestCandidate.OffsetMs;
                            bestSsim = bestCandidate.CombinedScore;
                            pointResult.Accepted = true;
                        }
                        else
                        {
                            pointResult.RejectReason = "Match ambiguo";
                        }
                    }
                    else
                    {
                        pointResult.RejectReason = "Nessun candidato verificato";
                    }
                    phaseStopwatch.Stop();
                    pointResult.CandidateMs += phaseStopwatch.ElapsedMilliseconds;
                }
                else if (resultOffset == int.MinValue)
                {
                    pointResult.RejectReason = "Tagli insufficienti";
                }

                if (resultOffset == int.MinValue)
                {
                    phaseStopwatch.Restart();
                    resultOffset = this.VerifyAtPointByLocalVisualSearch(sourceFrames, langFrames, sourceTimestampsMs, langTimestampsMs, baseOffset, timestampMatchToleranceMs, searchRadiusMs, pointResult, out bestSsim);
                    phaseStopwatch.Stop();
                    pointResult.CandidateMs += phaseStopwatch.ElapsedMilliseconds;
                }

                if (resultOffset == int.MinValue)
                {
                    phaseStopwatch.Restart();
                    this.TryAnchorCheckpointToInitial(sourceFrames, langFrames, sourceTimestampsMs, langTimestampsMs, baseOffset, timestampMatchToleranceMs, pointResult, out resultOffset, out bestSsim);
                    phaseStopwatch.Stop();
                    pointResult.CandidateMs += phaseStopwatch.ElapsedMilliseconds;
                }

                if (resultOffset == int.MinValue && this.IsStaticOrBlackSegment(pointResult))
                {
                    pointResult.RejectReason = "Segmento statico/nero";
                }

                if (resultOffset != int.MinValue && this.ShouldRecheckCheckpointAgainstInitial(resultOffset, baseOffset, frameIntervalMs))
                {
                    int driftedOffset = resultOffset;
                    if (!this.TryAnchorCheckpointToInitial(sourceFrames, langFrames, sourceTimestampsMs, langTimestampsMs, baseOffset, timestampMatchToleranceMs, pointResult, out resultOffset, out bestSsim))
                    {
                        this.MarkCheckpointDriftRejected(driftedOffset, baseOffset, frameIntervalMs, pointResult);
                        resultOffset = int.MinValue;
                        bestSsim = 0.0;
                    }
                }
            }
            else
            {
                pointResult.RejectReason = "Frame insufficienti";
            }

            if (checkpointIndex >= 0 && checkpointIndex < this._pointResults.Length)
            {
                pointStopwatch.Stop();
                pointResult.TimingMs = pointStopwatch.ElapsedMilliseconds;
                this._pointResults[checkpointIndex] = pointResult;
            }

            return resultOffset;
        }

        /// <summary>
        /// In FrameSync puro il delay deve restare coerente con l'initial
        /// </summary>
        private bool IsCheckpointOffsetNearInitial(int resultOffset, int baseOffset, double frameIntervalMs)
        {
            bool result;
            int driftMs = Math.Abs(resultOffset - baseOffset);
            int toleranceMs = (int)Math.Round(frameIntervalMs * this._fsConfig.InitialCheckpointDriftPenaltyFrames);

            if (toleranceMs < 1)
            {
                toleranceMs = 1;
            }

            if (driftMs > toleranceMs)
            {
                return false;
            }

            result = true;
            return result;
        }

        /// <summary>
        /// Decide quando un checkpoint locale va ricontrollato sull'offset iniziale
        /// </summary>
        private bool ShouldRecheckCheckpointAgainstInitial(int resultOffset, int baseOffset, double frameIntervalMs)
        {
            bool result = false;
            int driftMs = Math.Abs(resultOffset - baseOffset);
            int toleranceMs = (int)Math.Round(frameIntervalMs);

            if (toleranceMs < 80)
            {
                toleranceMs = 80;
            }

            if (driftMs > toleranceMs)
            {
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Marca un checkpoint come driftato rispetto all'offset iniziale
        /// </summary>
        private void MarkCheckpointDriftRejected(int resultOffset, int baseOffset, double frameIntervalMs, FrameSyncPointResult pointResult)
        {
            int driftMs = Math.Abs(resultOffset - baseOffset);
            int toleranceMs = (int)Math.Round(frameIntervalMs);

            if (toleranceMs < 80)
            {
                toleranceMs = 80;
            }

            pointResult.Accepted = false;
            pointResult.RejectReason = "Drift checkpoint/initial " + driftMs + "ms oltre " + toleranceMs + "ms";
        }

        /// <summary>
        /// Calcola una tolleranza PTS reale per associare frame VFR/CFR senza usare l'fps medio
        /// </summary>
        private double ComputeTimestampMatchToleranceMs(double[] sourceTimestampsMs, double[] langTimestampsMs, double fallbackFrameIntervalMs)
        {
            double result = fallbackFrameIntervalMs * 2.0;
            double sourceGap = this.ComputeMaxTimestampGapMs(sourceTimestampsMs);
            double langGap = this.ComputeMaxTimestampGapMs(langTimestampsMs);
            double maxGap = sourceGap > langGap ? sourceGap : langGap;

            if (maxGap > 0.0)
            {
                result = maxGap * 0.60;
            }
            if (result < 80.0)
            {
                result = 80.0;
            }
            if (result > 220.0)
            {
                result = 220.0;
            }

            return result;
        }

        /// <summary>
        /// Rileva il gap massimo tra PTS consecutivi nel segmento estratto
        /// </summary>
        private double ComputeMaxTimestampGapMs(double[] timestampsMs)
        {
            double result = 0.0;
            if (timestampsMs == null || timestampsMs.Length < 2)
            {
                return result;
            }

            for (int i = 1; i < timestampsMs.Length; i++)
            {
                double gap = timestampsMs[i] - timestampsMs[i - 1];
                if (gap > result)
                {
                    result = gap;
                }
            }

            return result;
        }

        /// <summary>
        /// Se il miglior match locale è driftato, rivaluta l'offset iniziale sugli stessi frame
        /// Evita falsi positivi su VFR anime con frame tenuti o ripetuti
        /// </summary>
        private bool TryAnchorCheckpointToInitial(List<byte[]> sourceFrames, List<byte[]> langFrames, double[] sourceTimestampsMs, double[] langTimestampsMs, int baseOffset, double timestampMatchToleranceMs, FrameSyncPointResult pointResult, out int resultOffset, out double bestScore)
        {
            bool result = false;
            List<int> sampleIndices;
            VisualScanFrameDescriptor[] sourceDescriptors;
            VisualScanFrameDescriptor[] langDescriptors;
            VisualScanCandidate anchorCandidate;
            double minScore = CHECKPOINT_INITIAL_ANCHOR_MIN_SCORE;

            resultOffset = int.MinValue;
            bestScore = 0.0;

            if (sourceFrames == null || langFrames == null || sourceTimestampsMs == null || langTimestampsMs == null)
            {
                return result;
            }

            sampleIndices = this.SelectCheckpointInitialAnchorSamples(sourceFrames);
            if (sampleIndices.Count < CHECKPOINT_LOCAL_MIN_SAMPLES)
            {
                return result;
            }

            sourceDescriptors = this._visualScanMatcher.BuildVisualScanDescriptors(sourceFrames);
            langDescriptors = this._visualScanMatcher.BuildVisualScanDescriptors(langFrames);
            anchorCandidate = this._visualScanMatcher.ScoreVisualScanOffset(sourceDescriptors, langDescriptors, sourceTimestampsMs, langTimestampsMs, sampleIndices, baseOffset, timestampMatchToleranceMs);

            if (anchorCandidate.Score >= this._fsConfig.CheckpointMinScore + 0.08)
            {
                minScore = this._fsConfig.CheckpointMinScore + 0.08;
            }

            if (anchorCandidate.SampleCount >= CHECKPOINT_LOCAL_MIN_SAMPLES &&
                anchorCandidate.Score >= minScore &&
                anchorCandidate.DescriptorVotes >= this._fsConfig.MinDescriptorVotes)
            {
                resultOffset = baseOffset;
                bestScore = anchorCandidate.Score;
                pointResult.Accepted = true;
                pointResult.RejectReason = "";
                pointResult.BestOffsetMs = -baseOffset;
                pointResult.BestScore = anchorCandidate.Score;
                pointResult.BlurScore = anchorCandidate.BlurScore;
                pointResult.SecondBestScore = pointResult.BestScore;
                pointResult.Margin = 0.0;
                pointResult.DescriptorVotes = anchorCandidate.DescriptorVotes;
                pointResult.DescriptorAgreement = anchorCandidate.DescriptorVotes / 6.0;
                pointResult.MotionScore = anchorCandidate.MotionScore;
                pointResult.MatchMethod = "InitialAnchor";
                result = true;
            }

            return result;
        }

        /// <summary>
        /// Campioni uniformi per confermare l'offset iniziale su tutta la finestra checkpoint
        /// Non usa i massimi locali: su anime e frame ripetuti quei punti favoriscono falsi offset vicini
        /// </summary>
        private List<int> SelectCheckpointInitialAnchorSamples(List<byte[]> frames)
        {
            List<int> result = new List<int>();
            int maxSamples = CHECKPOINT_LOCAL_MAX_SAMPLES;
            int step;
            if (frames == null || frames.Count < 3)
            {
                return result;
            }

            step = frames.Count / (maxSamples + 1);
            if (step < 1)
            {
                step = 1;
            }

            for (int i = step; i < frames.Count - 1 && result.Count < maxSamples; i += step)
            {
                result.Add(i);
            }

            return result;
        }

        /// <summary>
        /// Calcola varianza media su campioni del segmento
        /// </summary>
        private double ComputeSegmentVariance(List<byte[]> frames)
        {
            double total = 0.0;
            int count = 0;
            int step = frames.Count / 8;

            if (step < 1)
            {
                step = 1;
            }

            for (int i = 0; i < frames.Count; i += step)
            {
                total += this.ComputeFrameVariance(frames[i]);
                count++;
                if (count >= 8)
                {
                    break;
                }
            }

            return count > 0 ? total / count : 0.0;
        }

        /// <summary>
        /// Calcola quota media di pixel quasi neri su campioni del segmento
        /// </summary>
        private double ComputeSegmentBlackRatio(List<byte[]> frames)
        {
            double total = 0.0;
            int count = 0;
            int step = frames.Count / 8;

            if (step < 1)
            {
                step = 1;
            }

            for (int i = 0; i < frames.Count; i += step)
            {
                total += this.ComputeFrameBlackRatio(frames[i]);
                count++;
                if (count >= 8)
                {
                    break;
                }
            }

            return count > 0 ? total / count : 0.0;
        }

        /// <summary>
        /// Calcola quota pixel molto scuri in un frame
        /// </summary>
        private double ComputeFrameBlackRatio(byte[] frame)
        {
            int dark = 0;
            int count = 0;
            int step = 4;

            for (int i = 0; i < frame.Length; i += step)
            {
                if (frame[i] <= 16)
                {
                    dark++;
                }
                count++;
            }

            return count > 0 ? dark / (double)count : 0.0;
        }

        /// <summary>
        /// Indica se il checkpoint è troppo statico o nero per essere affidabile
        /// </summary>
        private bool IsStaticOrBlackSegment(FrameSyncPointResult pointResult)
        {
            bool sourceStatic = pointResult.SourceVariance <= this._fsConfig.StaticSegmentVarianceThreshold;
            bool langStatic = pointResult.LanguageVariance <= this._fsConfig.StaticSegmentVarianceThreshold;
            bool sourceBlack = pointResult.SourceBlackRatio >= this._fsConfig.BlackFrameRatioThreshold;
            bool langBlack = pointResult.LanguageBlackRatio >= this._fsConfig.BlackFrameRatioThreshold;

            return (sourceStatic && langStatic) || (sourceBlack && langBlack);
        }

        /// <summary>
        /// Costruisce il risultato dettagliato usando i dati prodotti dall'algoritmo legacy
        /// </summary>
        /// <param name="finalOffset">Offset finale da applicare alle tracce importate</param>
        /// <param name="initialDelay">Delay iniziale interno (langTime - sourceTime)</param>
        /// <param name="failureReason">Motivo fallimento, vuoto in caso di successo</param>
        /// <returns>Risultato dettagliato frame-sync</returns>
        private FrameSyncResult BuildLegacyResult(int finalOffset, int initialDelay, string failureReason)
        {
            return this._resultBuilder.Build(
                finalOffset,
                initialDelay,
                failureReason,
                this._lastSourceGeometry,
                this._lastLanguageGeometry,
                this._lastAudioGlobalResult,
                this._timing,
                this._lastInitialResult,
                this._pointResults,
                this._pointValid,
                this._offsets,
                this._ssimValues,
                this._vsConfig.NumCheckPoints,
                this._fsConfig.MinValidPoints,
                this._fsConfig.FinalMinConfidence);
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Tempo di esecuzione frame sync in ms
        /// </summary>
        public long FrameSyncTimeMs { get { return this._frameSyncTimeMs; } }

        /// <summary>
        /// Ultimo risultato dettagliato frame-sync
        /// </summary>
        public FrameSyncResult LastResult { get { return this._lastResult; } }

        #endregion

        #region Classi private

        /// <summary>
        /// Risultato temporaneo della verifica parallela di un candidato iniziale
        /// </summary>
        private class InitialCandidateVerifyResult
        {
            /// <summary>
            /// Delay candidato verificato
            /// </summary>
            public int Delay { get; set; }

            /// <summary>
            /// Numero checkpoint verificati
            /// </summary>
            public int VerifiedCount { get; set; }

            /// <summary>
            /// Score aggregato
            /// </summary>
            public double Score { get; set; }

            /// <summary>
            /// Score descriptor blur
            /// </summary>
            public double BlurScore { get; set; }

            /// <summary>
            /// Score descriptor edge
            /// </summary>
            public double EdgeScore { get; set; }

            /// <summary>
            /// Score blocchi luminanza
            /// </summary>
            public double BlockScore { get; set; }

            /// <summary>
            /// Score movimento
            /// </summary>
            public double MotionScore { get; set; }

            /// <summary>
            /// Score hash percettivi
            /// </summary>
            public double HashScore { get; set; }

            /// <summary>
            /// Voti descriptor concordi
            /// </summary>
            public int DescriptorVotes { get; set; }

            /// <summary>
            /// Delay locali verificati sui checkpoint
            /// </summary>
            public List<double> Delays { get; set; }
        }

        #endregion
    }
}
