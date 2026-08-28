using RemuxForge.Core.Analysis.Edit;
using RemuxForge.Core.Analysis.Edit.Extraction;
using RemuxForge.Core.Analysis.Edit.Geometry;
using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Media.Ffmpeg;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Determina un offset costante fra due copie e lo verifica in checkpoint distribuiti
    /// </summary>
    public sealed class FrameSyncService : VideoSyncServiceBase
    {
        #region Costanti

        /// <summary>
        /// Posizioni relative delle finestre dense di ricerca visiva
        /// </summary>
        private static readonly double[] SEARCH_POSITIONS = new double[] { 0.2, 0.5, 0.8 };

        /// <summary>
        /// Durata della finestra sorgente di ricerca, in millisecondi
        /// </summary>
        private const double SEARCH_WINDOW_MS = 20000.0;

        /// <summary>
        /// Semiampiezza del corridoio esplorato dalla ricerca visiva, in millisecondi
        /// </summary>
        private const double SEARCH_CORRIDOR_MS = 60000.0;

        /// <summary>
        /// Distanza entro cui due finestre di ricerca raccontano lo stesso offset
        /// </summary>
        private const double SEARCH_AGREEMENT_MS = 200.0;

        /// <summary>
        /// Larghezza del raggruppamento dei voti di offset, in millisecondi
        /// </summary>
        private const double VOTE_CLUSTER_MS = 100.0;

        /// <summary>
        /// Voti minimi perché il raggruppamento dominante sia credibile
        /// </summary>
        private const int VOTE_MIN_SUPPORT = 20;

        /// <summary>
        /// Durata della finestra sorgente di un checkpoint, in millisecondi
        /// </summary>
        private const double CHECKPOINT_WINDOW_MS = 6000.0;

        /// <summary>
        /// Semiampiezza della banda di offset verificata in un checkpoint
        /// </summary>
        private const double CHECKPOINT_CORRIDOR_MS = 1000.0;

        /// <summary>
        /// Scarto oltre il quale un checkpoint misurato racconta un altro offset
        /// </summary>
        private const double CHECKPOINT_TOLERANCE_MS = 100.0;

        /// <summary>
        /// Passo della scansione grossolana degli offset, in millisecondi
        /// </summary>
        private const double COARSE_STEP_MS = 20.0;

        /// <summary>
        /// Semiampiezza della scansione fine attorno all'offset grossolano
        /// </summary>
        private const double FINE_RADIUS_MS = 250.0;

        /// <summary>
        /// Passo della scansione fine degli offset, in millisecondi
        /// </summary>
        private const double FINE_STEP_MS = 5.0;

        /// <summary>
        /// Frazione minima di fotogrammi spiegati perché un checkpoint regga
        /// </summary>
        private const double MIN_EXPLAINED = 0.55;

        /// <summary>
        /// Fotogrammi minimi in una finestra perché la misura sia significativa
        /// </summary>
        private const int MIN_WINDOW_FRAMES = 50;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Configurazione FrameSync applicata alla sessione
        /// </summary>
        private readonly FrameSyncConfig _frameSyncConfig;

        /// <summary>
        /// Risolutore dei percorsi degli strumenti esterni
        /// </summary>
        private readonly ToolPathResolverService _toolPathResolver;

        /// <summary>
        /// Percorso di ffprobe usato dagli estrattori concorrenti
        /// </summary>
        private string _ffprobePath;

        /// <summary>
        /// Percorso di mkvmerge usato dagli estrattori concorrenti
        /// </summary>
        private string _mkvMergePath;

        /// <summary>
        /// Percorso di mkvextract usato dagli estrattori concorrenti
        /// </summary>
        private string _mkvExtractPath;

        /// <summary>
        /// Fattore che riporta i tempi della copia doppiata nel dominio della sorgente
        /// </summary>
        private double _stretch;

        /// <summary>
        /// Fotogrammi al secondo dichiarati dalla sorgente
        /// </summary>
        private double _sourceFps;

        /// <summary>
        /// Fotogrammi al secondo dichiarati dalla copia doppiata
        /// </summary>
        private double _languageFps;

        /// <summary>
        /// Geometria di normalizzazione della sorgente
        /// </summary>
        private FrameGeometry _sourceFrameGeometry;

        /// <summary>
        /// Geometria di normalizzazione della copia doppiata
        /// </summary>
        private FrameGeometry _languageFrameGeometry;

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
        /// <param name="toolPathResolver">Risolutore dei percorsi degli strumenti esterni</param>
        public FrameSyncService(string ffmpegPath, ToolPathResolverService toolPathResolver) : base(ffmpegPath, LogSection.FrameSync)
        {
            if (string.IsNullOrEmpty(ffmpegPath))
                throw new ArgumentException(AppText.T("analysis.sift.missingFfmpegPath"), nameof(ffmpegPath));
            this._frameSyncConfig = AppSettingsService.Instance.Settings.Advanced.FrameSync;
            this._toolPathResolver = toolPathResolver;
            this._stretch = 1.0;
            this._lastResult = new FrameSyncResult();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Determina e verifica un offset costante fra le due copie
        /// </summary>
        /// <param name="sourceFile">Percorso del video sorgente</param>
        /// <param name="languageFile">Percorso del video lingua</param>
        /// <param name="manualStretchFactor">Fattore di stretch manuale, vuoto quando le due copie corrono uguali</param>
        /// <returns>Offset da applicare in millisecondi oppure <see cref="int.MinValue"/></returns>
        public int RefineOffset(string sourceFile, string languageFile, string manualStretchFactor)
        {
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            FrameSyncResult result = new FrameSyncResult();
            FrameSyncTimingInfo timing = result.Timing;

            try
            {
                // Con lo stretch l'offset è costante solo nel dominio della sorgente: senza
                // applicarlo i checkpoint vedrebbero una deriva e rinuncerebbero sempre
                if (!this.TryResolveStretch(manualStretchFactor, result))
                    return int.MinValue;
                if (!this.TryPrepare(sourceFile, languageFile, result, timing, out int durationMs))
                    return int.MinValue;

                FrameSyncCandidate initial = this.ResolveInitial(sourceFile, languageFile, durationMs, result, timing, out bool initialFromAudio);
                if (initial == null)
                    return int.MinValue;

                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Success, AppText.F("framesync.match.initialOffset", Utils.FormatDelay(initial.OffsetMs), initial.StrongPairCount, initial.ProcessedPairCount));
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, AppText.F("framesync.match.checkpointPhase", this._vsConfig.NumCheckPoints));
                ConsoleHelper.Progress(LogSection.FrameSync, 58, AppText.T("framesync.match.checkpointProgress"));
                this.ResolveCheckpoints(sourceFile, languageFile, durationMs, initial, result, timing);
                int finalOffset = this.FinalizeOffset(initial, result);
                if (finalOffset != int.MinValue || !initialFromAudio)
                    return finalOffset;

                // L'audio serve solo ad accelerare la ricerca: un'origine di traccia diversa
                // dal video non deve impedire al percorso visivo autorevole di risolvere l'offset
                Stopwatch visualFallbackStopwatch = Stopwatch.StartNew();
                FrameSyncCandidate visualInitial = this.ResolveVisualOffset(sourceFile, languageFile, durationMs, timing);
                visualFallbackStopwatch.Stop();
                timing.InitialSearchMs += visualFallbackStopwatch.ElapsedMilliseconds;
                if (visualInitial == null || Math.Abs(visualInitial.OffsetMs - initial.OffsetMs) <= CHECKPOINT_TOLERANCE_MS)
                    return finalOffset;

                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, AppText.F("framesync.match.audioFallbackVisual", Utils.FormatDelay(initial.OffsetMs), Utils.FormatDelay(visualInitial.OffsetMs)));
                result.Initial.Candidates.Add(visualInitial);
                result.Initial.BestCandidate = visualInitial;
                this.ResetCheckpointResult(result);
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, AppText.F("framesync.match.checkpointPhase", this._vsConfig.NumCheckPoints));
                ConsoleHelper.Progress(LogSection.FrameSync, 58, AppText.T("framesync.match.checkpointProgress"));
                this.ResolveCheckpoints(sourceFile, languageFile, durationMs, visualInitial, result, timing);
                return this.FinalizeOffset(visualInitial, result);
            }
            catch (Exception ex)
            {
                result.FailureReason = AppText.F("framesync.match.failed", ex.Message);
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Error, result.FailureReason);
                return int.MinValue;
            }
            finally
            {
                totalStopwatch.Stop();
                timing.TotalMs = totalStopwatch.ElapsedMilliseconds;
                this._frameSyncTimeMs = timing.TotalMs;
                this._lastResult = result;
            }
        }

        /// <summary>
        /// Restituisce un riepilogo compatto dei checkpoint
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
            return AppText.F("framesync.match.summary", accepted, this._lastResult.Points.Count, this._lastResult.Timing.InitialPairCount, this._lastResult.Timing.CheckpointPairCount);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Porta il fattore manuale nel rapporto con cui si stirano i tempi della copia doppiata
        /// </summary>
        /// <param name="manualStretchFactor">Fattore di stretch manuale</param>
        /// <param name="result">Risultato diagnostico in costruzione</param>
        /// <returns>True quando il fattore è assente oppure valido</returns>
        private bool TryResolveStretch(string manualStretchFactor, FrameSyncResult result)
        {
            this._stretch = 1.0;
            if (string.IsNullOrEmpty(manualStretchFactor != null ? manualStretchFactor.Trim() : null))
                return true;

            if (!SpeedCorrectionService.TryParseStretchFactor(manualStretchFactor, out double stretchRatio, out _) || !double.IsFinite(stretchRatio) || stretchRatio <= 0.0)
            {
                result.FailureReason = AppText.F("framesync.match.invalidStretch", manualStretchFactor);
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Error, result.FailureReason);
                return false;
            }

            this._stretch = stretchRatio;
            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, AppText.F("framesync.match.stretch", manualStretchFactor));
            return true;
        }

        /// <summary>
        /// Legge la durata e stima la geometria comune alle due copie
        /// </summary>
        /// <param name="sourceFile">Percorso del video sorgente</param>
        /// <param name="languageFile">Percorso del video lingua</param>
        /// <param name="result">Risultato diagnostico in costruzione</param>
        /// <param name="timing">Tempi della sessione</param>
        /// <param name="durationMs">Durata della sorgente in millisecondi</param>
        /// <returns>True quando la misura può procedere</returns>
        private bool TryPrepare(string sourceFile, string languageFile, FrameSyncResult result, FrameSyncTimingInfo timing, out int durationMs)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            FfmpegVideoInfoReader reader = new FfmpegVideoInfoReader(this._ffmpegPath, this._ffmpegConfig, LogSection.FrameSync);
            bool infoAvailable = reader.TryRead(sourceFile, out durationMs, out this._sourceFps);
            reader.TryRead(languageFile, out _, out this._languageFps);
            stopwatch.Stop();
            timing.VideoInfoMs = stopwatch.ElapsedMilliseconds;
            if (!infoAvailable || durationMs < this._frameSyncConfig.MinDurationMs)
            {
                result.FailureReason = AppText.T("framesync.match.videoInfoUnavailable");
                return false;
            }

            stopwatch.Restart();
            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, AppText.T("framesync.match.geometryPhase"));
            ConsoleHelper.Progress(LogSection.FrameSync, 10, AppText.T("framesync.match.geometryProgress"));
            VisionBackendKind backend = AppSettingsService.Instance.Settings.Advanced.GetVisionBackendKind();
            FrameGeometryEstimator estimator = new FrameGeometryEstimator(this._ffmpegPath, this._ffmpegConfig, backend, LogSection.FrameSync);
            FrameGeometryEstimationResult geometry = estimator.Estimate(sourceFile, languageFile, this._analysisCropSourcePx, this._analysisCropLanguagePx, durationMs, CancellationToken.None);
            result.SourceGeometry = geometry.SourceGeometryInfo;
            result.LanguageGeometry = geometry.LanguageGeometryInfo;
            result.GeometryAlignment = geometry.Alignment;
            if (!geometry.Alignment.Success)
            {
                result.FailureReason = geometry.Alignment.RejectReason;
                return false;
            }
            this._sourceFrameGeometry = geometry.SourceCommonGeometry;
            this._languageFrameGeometry = geometry.LanguageCommonGeometry;
            stopwatch.Stop();
            timing.GeometryMs = stopwatch.ElapsedMilliseconds;
            ConsoleHelper.Progress(LogSection.FrameSync, 22, AppText.T("framesync.match.geometryProgress"));

            // FrameSync guarda finestre da sei secondi: aprire il dispositivo costa più di quanto
            // farebbe risparmiare, e gli hash restano sul processore
            this._ffprobePath = this._toolPathResolver.ResolveFfprobePath(this._ffmpegPath, false);
            this._mkvMergePath = this._toolPathResolver.ResolveMkvMergePath(false);
            this._mkvExtractPath = this._toolPathResolver.ResolveMkvExtractPath(this._mkvMergePath, false);
            return true;
        }

        /// <summary>
        /// Trova l'offset di partenza, dall'audio quando le due copie condividono una traccia
        /// </summary>
        /// <param name="sourceFile">Percorso del video sorgente</param>
        /// <param name="languageFile">Percorso del video lingua</param>
        /// <param name="durationMs">Durata della sorgente in millisecondi</param>
        /// <param name="result">Risultato diagnostico in costruzione</param>
        /// <param name="timing">Tempi della sessione</param>
        /// <param name="fromAudio">True quando il candidato arriva dalla traccia audio condivisa</param>
        /// <returns>Candidato iniziale oppure null</returns>
        private FrameSyncCandidate ResolveInitial(string sourceFile, string languageFile, int durationMs, FrameSyncResult result, FrameSyncTimingInfo timing, out bool fromAudio)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            FrameSyncCandidate candidate = this.ResolveAudioOffset(sourceFile, languageFile, timing);
            fromAudio = candidate != null;
            if (candidate == null)
                candidate = this.ResolveVisualOffset(sourceFile, languageFile, durationMs, timing);
            timing.InitialSearchMs = stopwatch.ElapsedMilliseconds;

            if (candidate == null)
            {
                result.Initial.FailureReason = AppText.T("framesync.match.initialUnsupported");
                result.FailureReason = result.Initial.FailureReason;
                return null;
            }

            result.Initial.Candidates.Add(candidate);
            result.Initial.BestCandidate = candidate;
            result.Initial.Success = true;
            return candidate;
        }

        /// <summary>
        /// Azzera soltanto l'esito dei checkpoint prima del retry con candidato visivo
        /// </summary>
        /// <param name="result">Risultato FrameSync da riutilizzare</param>
        private void ResetCheckpointResult(FrameSyncResult result)
        {
            result.Success = false;
            result.OffsetMs = int.MinValue;
            result.Confidence = 0.0;
            result.InitialToFinalDeltaMs = int.MinValue;
            result.FailureReason = "";
            result.Points.Clear();
            result.PrecisionCandidate = null;
            result.PrecisionCheckpointPercent = -1;
        }

        /// <summary>
        /// Cerca l'offset nella correlazione degli inviluppi della traccia condivisa
        /// </summary>
        /// <param name="sourceFile">Percorso del video sorgente</param>
        /// <param name="languageFile">Percorso del video lingua</param>
        /// <param name="timing">Tempi della sessione</param>
        /// <returns>Candidato audio oppure null quando la traccia non è condivisa</returns>
        private FrameSyncCandidate ResolveAudioOffset(string sourceFile, string languageFile, FrameSyncTimingInfo timing)
        {
            // Con una traccia nella stessa lingua l'offset costa due decodifiche audio, contro
            // le centinaia di secondi di video che servirebbero a cercarlo guardando
            string ffprobePath = this._toolPathResolver.ResolveFfprobePath(this._ffmpegPath, false);
            if (string.IsNullOrEmpty(ffprobePath))
                return null;

            AudioEnvelopeExtractor extractor = new AudioEnvelopeExtractor(this._ffmpegPath, ffprobePath);
            if (!extractor.ResolveSharedStreams(sourceFile, languageFile, this._ffmpegConfig.FrameExtractionTimeoutMs, out int sourceStream, out int languageStream))
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, AppText.T("framesync.match.audioUnavailable"));
                return null;
            }

            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, AppText.T("framesync.match.audioPhase"));
            ConsoleHelper.Progress(LogSection.FrameSync, 24, AppText.T("framesync.match.audioProgress"));
            Stopwatch stopwatch = Stopwatch.StartNew();
            AudioEnvelope source = null;
            AudioEnvelope language = null;
            Parallel.Invoke(
                () => source = extractor.Extract(sourceFile, sourceStream, this._ffmpegConfig.FrameExtractionTimeoutMs),
                () => language = extractor.Extract(languageFile, languageStream, this._ffmpegConfig.FrameExtractionTimeoutMs));
            timing.InitialExtractMs += stopwatch.ElapsedMilliseconds;
            if (!new FrameSyncAudioOffsetResolver().TryResolve(new AudioEnvelopePair(source, language, this._stretch), out double offsetMs, out double correlation))
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, AppText.T("framesync.match.audioInconsistent"));
                return null;
            }

            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, AppText.F("framesync.match.audioOffset", Utils.FormatDelay((int)Math.Round(offsetMs)), correlation.ToString("F2", CultureInfo.InvariantCulture)));
            FrameSyncCandidate result = new FrameSyncCandidate();
            result.Backend = AppText.T("framesync.match.audioBackend");
            result.OffsetMs = (int)Math.Round(offsetMs);
            result.MeanScore = correlation;
            result.SourceCoverageMs = source.Count * AudioEnvelopeExtractor.STEP_MS;
            result.LanguageCoverageMs = language.Count * AudioEnvelopeExtractor.STEP_MS;
            return result;
        }

        /// <summary>
        /// Cerca l'offset in finestre dense lontane dalla testa, dove le due copie si somigliano
        /// </summary>
        /// <param name="sourceFile">Percorso del video sorgente</param>
        /// <param name="languageFile">Percorso del video lingua</param>
        /// <param name="durationMs">Durata della sorgente in millisecondi</param>
        /// <param name="timing">Tempi della sessione</param>
        /// <returns>Candidato visivo oppure null</returns>
        private FrameSyncCandidate ResolveVisualOffset(string sourceFile, string languageFile, int durationMs, FrameSyncTimingInfo timing)
        {
            // Mai in testa: loghi, cartelli e sigle sono il punto in cui due edizioni
            // differiscono di più, ed è esattamente dove la vecchia ricerca guardava
            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, AppText.T("framesync.match.searchPhase"));
            ConsoleHelper.Progress(LogSection.FrameSync, 30, AppText.T("framesync.match.searchProgress"));
            List<FrameSyncCandidate> candidates = new List<FrameSyncCandidate>();
            FrameSyncCandidate[] measuredCandidates = new FrameSyncCandidate[SEARCH_POSITIONS.Length];
            long[] extractTimes = new long[SEARCH_POSITIONS.Length];
            long[] matchTimes = new long[SEARCH_POSITIONS.Length];
            ParallelOptions options = new ParallelOptions();
            options.MaxDegreeOfParallelism = Math.Max(1, Math.Min(SEARCH_POSITIONS.Length, Math.Max(1, ParallelismHelper.ResolveDefaultMaxDegree() / 4)));
            Parallel.For(0, SEARCH_POSITIONS.Length, options, positionIndex =>
            {
                double sourceStartMs = Math.Max(0.0, durationMs * SEARCH_POSITIONS[positionIndex] - SEARCH_WINDOW_MS / 2.0);
                Stopwatch stopwatch = Stopwatch.StartNew();
                PairSignals pair = this.ExtractPair(sourceFile, languageFile, sourceStartMs, SEARCH_WINDOW_MS, 0.0, SEARCH_CORRIDOR_MS);
                extractTimes[positionIndex] = stopwatch.ElapsedMilliseconds;
                if (pair == null)
                    return;

                stopwatch.Restart();
                measuredCandidates[positionIndex] = this.VotePair(pair);
                matchTimes[positionIndex] = stopwatch.ElapsedMilliseconds;
            });
            for (int positionIndex = 0; positionIndex < SEARCH_POSITIONS.Length; positionIndex++)
            {
                timing.InitialExtractMs += extractTimes[positionIndex];
                timing.InitialMatchMs += matchTimes[positionIndex];
                FrameSyncCandidate candidate = measuredCandidates[positionIndex];
                if (candidate == null)
                    continue;
                timing.InitialPairCount += candidate.ProcessedPairCount;
                candidates.Add(candidate);
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, AppText.F("framesync.match.searchWindow", (int)Math.Round(SEARCH_POSITIONS[positionIndex] * 100.0), Utils.FormatDelay(candidate.OffsetMs), candidate.StrongPairCount));
            }

            if (candidates.Count < 2)
                return null;

            // Finestre lontane che raccontano lo stesso offset sono la prova che è costante, ma
            // devono dirlo tutte: una che si sfila è un taglio, non un voto di minoranza
            FrameSyncCandidate best = candidates[0];
            for (int index = 1; index < candidates.Count; index++)
            {
                if (Math.Abs(candidates[index].OffsetMs - candidates[0].OffsetMs) > SEARCH_AGREEMENT_MS)
                    return null;
                if (candidates[index].StrongPairCount > best.StrongPairCount)
                    best = candidates[index];
            }

            return best;
        }

        /// <summary>
        /// Verifica l'offset iniziale in checkpoint distribuiti su tutta la durata
        /// </summary>
        /// <param name="sourceFile">Percorso del video sorgente</param>
        /// <param name="languageFile">Percorso del video lingua</param>
        /// <param name="durationMs">Durata della sorgente in millisecondi</param>
        /// <param name="initial">Candidato iniziale</param>
        /// <param name="result">Risultato diagnostico in costruzione</param>
        /// <param name="timing">Tempi della sessione</param>
        private void ResolveCheckpoints(string sourceFile, string languageFile, int durationMs, FrameSyncCandidate initial, FrameSyncResult result, FrameSyncTimingInfo timing)
        {
            Stopwatch checkpointsStopwatch = Stopwatch.StartNew();
            FrameSyncPointResult[] points = new FrameSyncPointResult[this._vsConfig.NumCheckPoints];
            ParallelOptions options = new ParallelOptions();
            options.MaxDegreeOfParallelism = Math.Max(1, Math.Min(this._vsConfig.NumCheckPoints, Math.Max(1, ParallelismHelper.ResolveDefaultMaxDegree() / 4)));
            Parallel.For(0, this._vsConfig.NumCheckPoints, options, pointIndex =>
            {
                int percentage = (int)Math.Round((pointIndex + 1) * 100.0 / (this._vsConfig.NumCheckPoints + 1));
                points[pointIndex] = this.ResolveCheckpoint(sourceFile, languageFile, durationMs * percentage / 100.0, percentage, initial.OffsetMs);
            });

            for (int pointIndex = 0; pointIndex < this._vsConfig.NumCheckPoints; pointIndex++)
            {
                FrameSyncPointResult point = points[pointIndex];
                result.Points.Add(point);
                timing.CheckpointExtractMs += point.ExtractMs;
                timing.CheckpointMatchMs += point.MatchMs;
                timing.CheckpointPairCount += point.ProcessedPairCount;
                ConsoleHelper.Write(LogSection.FrameSync, point.Accepted ? LogLevel.Debug : LogLevel.Notice, AppText.F("framesync.match.checkpointResult", point.CheckpointPercent, point.Accepted ? AppText.T("framesync.match.accepted") : point.RejectReason, point.BestOffsetMs == int.MinValue ? "-" : Utils.FormatDelay(point.BestOffsetMs), point.StrongPairCount, point.ProcessedPairCount));
                ConsoleHelper.Progress(LogSection.FrameSync, 58 + (pointIndex + 1) * 27 / this._vsConfig.NumCheckPoints, AppText.T("framesync.match.checkpointProgress"));
            }
            checkpointsStopwatch.Stop();
            timing.CheckpointsMs += checkpointsStopwatch.ElapsedMilliseconds;
        }

        /// <summary>
        /// Misura l'offset in un checkpoint e ne verifica la tenuta
        /// </summary>
        /// <param name="sourceFile">Percorso del video sorgente</param>
        /// <param name="languageFile">Percorso del video lingua</param>
        /// <param name="sourceCenterMs">Centro della finestra sorgente</param>
        /// <param name="percentage">Posizione percentuale del checkpoint</param>
        /// <param name="expectedOffsetMs">Offset atteso dal candidato iniziale</param>
        /// <returns>Esito del checkpoint</returns>
        private FrameSyncPointResult ResolveCheckpoint(string sourceFile, string languageFile, double sourceCenterMs, int percentage, int expectedOffsetMs)
        {
            // Verificare costa un confronto per fotogramma, cercare costa una griglia: qui si
            // verifica soltanto, e un checkpoint che cade dice che l'offset non è costante
            Stopwatch totalStopwatch = Stopwatch.StartNew();
            Stopwatch phaseStopwatch = Stopwatch.StartNew();
            FrameSyncPointResult result = new FrameSyncPointResult();
            result.CheckpointPercent = percentage;
            result.ExpectedOffsetMs = expectedOffsetMs;
            result.Backend = AppText.T("framesync.match.hashBackend");
            double sourceStartMs = Math.Max(0.0, sourceCenterMs - CHECKPOINT_WINDOW_MS / 2.0);
            PairSignals pair = this.ExtractPair(sourceFile, languageFile, sourceStartMs, CHECKPOINT_WINDOW_MS, expectedOffsetMs, CHECKPOINT_CORRIDOR_MS);
            result.ExtractMs = phaseStopwatch.ElapsedMilliseconds;
            if (pair == null)
            {
                result.RejectReason = AppText.T("framesync.match.checkpointWithoutFrames");
                result.TimingMs = totalStopwatch.ElapsedMilliseconds;
                return result;
            }

            phaseStopwatch.Restart();
            int[] indices = HashOps.RangeIndices(pair, sourceStartMs, sourceStartMs + CHECKPOINT_WINDOW_MS, 1);
            double coarseMs = this.Measure(pair, indices, expectedOffsetMs, CHECKPOINT_CORRIDOR_MS, COARSE_STEP_MS);
            double offsetMs = this.Measure(pair, indices, coarseMs, FINE_RADIUS_MS, FINE_STEP_MS);
            double explained = HashOps.ExplainedFraction(pair, indices, -offsetMs, EditAnalysisProfile.VERIFICATION_RADIUS, EditAnalysisProfile.VERIFICATION_THRESHOLD);
            result.MatchMs = phaseStopwatch.ElapsedMilliseconds;
            result.ProcessedPairCount = indices.Length * (long)Math.Ceiling((2.0 * CHECKPOINT_CORRIDOR_MS + COARSE_STEP_MS) / COARSE_STEP_MS);
            result.AcceptedPairCount = indices.Length;
            result.StrongPairCount = (int)Math.Round(explained * indices.Length);
            result.BestOffsetMs = (int)Math.Round(offsetMs);
            result.BestScore = explained;
            result.SourceCoverageMs = CHECKPOINT_WINDOW_MS;
            result.LanguageCoverageMs = CHECKPOINT_WINDOW_MS + 2.0 * CHECKPOINT_CORRIDOR_MS;

            if (explained < MIN_EXPLAINED)
                result.RejectReason = AppText.T("framesync.match.checkpointUnsupported");
            else if (Math.Abs(offsetMs - expectedOffsetMs) > CHECKPOINT_CORRIDOR_MS)
                result.RejectReason = AppText.T("framesync.match.checkpointDrift");
            else
                result.Accepted = true;
            result.TimingMs = totalStopwatch.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// Conclude richiedendo checkpoint sufficienti e coerenti con un solo offset costante
        /// </summary>
        /// <param name="initial">Candidato iniziale</param>
        /// <param name="result">Risultato diagnostico in costruzione</param>
        /// <returns>Offset finale oppure <see cref="int.MinValue"/></returns>
        private int FinalizeOffset(FrameSyncCandidate initial, FrameSyncResult result)
        {
            List<int> offsets = new List<int>();
            double scoreSum = 0.0;
            FrameSyncPointResult strongest = null;
            for (int pointIndex = 0; pointIndex < result.Points.Count; pointIndex++)
            {
                FrameSyncPointResult point = result.Points[pointIndex];

                // Un checkpoint che non si misura è solo un checkpoint in meno; uno che si
                // misura bene e dà un altro offset dice che lì dentro c'è un taglio
                if (!point.Accepted && point.BestScore >= MIN_EXPLAINED)
                {
                    result.FailureReason = AppText.T("framesync.match.inconsistentCheckpoints");
                    return int.MinValue;
                }
                if (!point.Accepted)
                    continue;
                offsets.Add(point.BestOffsetMs);
                scoreSum += point.BestScore;
                if (strongest == null || point.BestScore > strongest.BestScore)
                    strongest = point;
            }
            if (offsets.Count < this._frameSyncConfig.MinValidPoints)
            {
                result.FailureReason = AppText.F("framesync.match.insufficientCheckpoints", offsets.Count, this._frameSyncConfig.MinValidPoints);
                return int.MinValue;
            }

            // I checkpoint misurano, la mediana giudica: l'ancora iniziale ha un errore suo e
            // non è il metro con cui si decide se l'offset è costante
            offsets.Sort();
            int middle = offsets.Count / 2;
            int finalOffset = offsets.Count % 2 == 0 ? (int)Math.Round((offsets[middle - 1] + offsets[middle]) * 0.5) : offsets[middle];
            for (int offsetIndex = 0; offsetIndex < offsets.Count; offsetIndex++)
            {
                if (Math.Abs(offsets[offsetIndex] - finalOffset) > CHECKPOINT_TOLERANCE_MS)
                {
                    result.FailureReason = AppText.T("framesync.match.inconsistentCheckpoints");
                    return int.MinValue;
                }
            }

            result.Confidence = scoreSum / offsets.Count;
            if (result.Confidence < this._frameSyncConfig.FinalMinConfidence)
            {
                result.FailureReason = AppText.F("framesync.match.insufficientConfidence", result.Confidence.ToString("P0", CultureInfo.InvariantCulture), this._frameSyncConfig.FinalMinConfidence.ToString("P0", CultureInfo.InvariantCulture));
                return int.MinValue;
            }

            result.OffsetMs = finalOffset;
            result.InitialToFinalDeltaMs = Math.Abs(finalOffset - initial.OffsetMs);
            result.PrecisionCheckpointPercent = strongest.CheckpointPercent;
            result.PrecisionCandidate = new FrameSyncCandidate();
            result.PrecisionCandidate.Backend = strongest.Backend;
            result.PrecisionCandidate.OffsetMs = strongest.BestOffsetMs;
            result.PrecisionCandidate.MeanScore = strongest.BestScore;
            result.PrecisionCandidate.StrongPairCount = strongest.StrongPairCount;
            result.PrecisionCandidate.AcceptedPairCount = strongest.AcceptedPairCount;
            result.PrecisionCandidate.ProcessedPairCount = strongest.ProcessedPairCount;
            result.PrecisionCandidate.SourceCoverageMs = strongest.SourceCoverageMs;
            result.PrecisionCandidate.LanguageCoverageMs = strongest.LanguageCoverageMs;
            result.Success = true;
            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Success, AppText.F("framesync.match.finalOffset", Utils.FormatDelay(finalOffset), offsets.Count, result.Points.Count));
            return finalOffset;
        }

        /// <summary>
        /// Offset più votato dai fotogrammi che si somigliano nelle due finestre
        /// </summary>
        /// <param name="pair">Finestre delle due copie</param>
        /// <returns>Candidato del raggruppamento dominante oppure null</returns>
        private FrameSyncCandidate VotePair(PairSignals pair)
        {
            // Ogni coppia di fotogrammi simili vota la propria differenza di PTS: il
            // raggruppamento più affollato è l'offset, senza griglia da spazzare
            List<double> votes = new List<double>();
            for (int sourceIndex = 0; sourceIndex < pair.Source.Count; sourceIndex++)
            {
                for (int languageIndex = 0; languageIndex < pair.Language.Count; languageIndex++)
                {
                    if (HashOps.Distance(pair.Source, sourceIndex, pair.Language, languageIndex) > EditAnalysisProfile.DETECTION_THRESHOLD)
                        continue;
                    votes.Add(pair.Source.PtsMs[sourceIndex] - pair.LanguagePtsMs[languageIndex]);
                }
            }
            if (votes.Count < VOTE_MIN_SUPPORT)
                return null;

            votes.Sort();
            int bestStart = 0;
            int bestCount = 0;
            int start = 0;
            for (int end = 0; end < votes.Count; end++)
            {
                while (votes[end] - votes[start] > VOTE_CLUSTER_MS)
                    start++;
                if (end - start + 1 <= bestCount)
                    continue;
                bestCount = end - start + 1;
                bestStart = start;
            }
            if (bestCount < VOTE_MIN_SUPPORT)
                return null;

            FrameSyncCandidate result = new FrameSyncCandidate();
            result.Backend = AppText.T("framesync.match.hashBackend");
            result.OffsetMs = (int)Math.Round(votes[bestStart + bestCount / 2]);
            result.StrongPairCount = bestCount;
            result.AcceptedPairCount = votes.Count;
            result.ProcessedPairCount = (long)pair.Source.Count * pair.Language.Count;
            result.MeanScore = (double)bestCount / votes.Count;
            result.DispersionMs = votes[bestStart + bestCount - 1] - votes[bestStart];
            result.SourceCoverageMs = pair.Source.PtsMs[pair.Source.Count - 1] - pair.Source.PtsMs[0];
            result.LanguageCoverageMs = pair.LanguagePtsMs[pair.Language.Count - 1] - pair.LanguagePtsMs[0];
            return result;
        }

        /// <summary>
        /// Offset che spiega più fotogrammi, preso al centro della cima piatta
        /// </summary>
        /// <param name="pair">Finestre delle due copie</param>
        /// <param name="indices">Fotogrammi sorgente su cui si misura</param>
        /// <param name="centerMs">Centro della scansione</param>
        /// <param name="radiusMs">Semiampiezza della scansione</param>
        /// <param name="stepMs">Passo della scansione</param>
        /// <returns>Offset in millisecondi, nella convenzione lang = source - offset</returns>
        private double Measure(PairSignals pair, int[] indices, double centerMs, double radiusMs, double stepMs)
        {
            // Sotto la durata di un fotogramma la frazione spiegata non cambia più: il centro
            // della cima piatta è la stima, l'estremo sinistro sarebbe sbilanciato
            int count = (int)Math.Ceiling((2.0 * radiusMs + stepMs) / stepMs);
            double[] fractions = new double[count];
            double explained = -1.0;
            int best = 0;
            for (int i = 0; i < count; i++)
            {
                double candidateMs = centerMs - radiusMs + i * stepMs;
                fractions[i] = HashOps.ExplainedFraction(pair, indices, -candidateMs, EditAnalysisProfile.DETECTION_RADIUS, EditAnalysisProfile.DETECTION_THRESHOLD);
                if (fractions[i] <= explained)
                    continue;
                explained = fractions[i];
                best = i;
            }

            int low = best;
            while (low > 0 && fractions[low - 1] >= explained)
                low--;
            int high = best;
            while (high < count - 1 && fractions[high + 1] >= explained)
                high++;

            // L'intorno di tolleranza si conta dal primo fotogramma non precedente, e questo
            // arrotondamento per eccesso sposta la cima piatta di mezzo fotogramma in avanti
            double frameMs = (pair.LanguagePtsMs[pair.Language.Count - 1] - pair.LanguagePtsMs[0]) / Math.Max(1, pair.Language.Count - 1);
            return centerMs - radiusMs + (low + high) * 0.5 * stepMs - frameMs / 2.0;
        }

        /// <summary>
        /// Indicizza la finestra sorgente e la corrispondente finestra allargata della copia
        /// </summary>
        /// <param name="sourceFile">Percorso del video sorgente</param>
        /// <param name="languageFile">Percorso del video lingua</param>
        /// <param name="sourceStartMs">Inizio della finestra sorgente</param>
        /// <param name="windowMs">Durata della finestra sorgente</param>
        /// <param name="expectedOffsetMs">Offset atteso, nella convenzione lang = source - offset</param>
        /// <param name="corridorMs">Semiampiezza del corridoio di ricerca</param>
        /// <returns>Coppia di finestre oppure null quando i fotogrammi non bastano</returns>
        private PairSignals ExtractPair(string sourceFile, string languageFile, double sourceStartMs, double windowMs, double expectedOffsetMs, double corridorMs)
        {
            // La finestra della copia si cerca sul suo orologio, non su quello della sorgente
            double languageStartMs = Math.Max(0.0, (sourceStartMs - expectedOffsetMs - corridorMs) / this._stretch);
            double languageWindowMs = (windowMs + 2.0 * corridorMs) / this._stretch;
            FrameSignalExtractor sourceExtractor = new FrameSignalExtractor(this._ffmpegPath, this._ffprobePath, this._mkvMergePath, this._mkvExtractPath, this._ffmpegConfig, new CpuHashBackend());
            FrameSignalExtractor languageExtractor = new FrameSignalExtractor(this._ffmpegPath, this._ffprobePath, this._mkvMergePath, this._mkvExtractPath, this._ffmpegConfig, new CpuHashBackend());
            FrameSignals source = null;
            FrameSignals language = null;
            Parallel.Invoke(
                () => source = sourceExtractor.Extract(sourceFile, this._sourceFrameGeometry, sourceStartMs, windowMs, FrameBudget(windowMs, this._sourceFps), this._ffmpegConfig.FrameExtractionTimeoutMs, CancellationToken.None),
                () => language = languageExtractor.Extract(languageFile, this._languageFrameGeometry, languageStartMs, languageWindowMs, FrameBudget(languageWindowMs, this._languageFps), this._ffmpegConfig.FrameExtractionTimeoutMs, CancellationToken.None));
            if (source.Count < MIN_WINDOW_FRAMES || language.Count < MIN_WINDOW_FRAMES)
                return null;
            return new PairSignals(source, language, this._stretch);
        }

        /// <summary>
        /// Fotogrammi da chiedere alla decodifica per coprire una finestra
        /// </summary>
        /// <param name="windowMs">Durata della finestra in millisecondi</param>
        /// <param name="fps">Fotogrammi al secondo dichiarati dal file</param>
        /// <returns>Budget di fotogrammi con margine sul framerate variabile</returns>
        private static int FrameBudget(double windowMs, double fps)
        {
            return (int)Math.Ceiling(windowMs / 1000.0 * Math.Max(1.0, fps) * 1.1) + 8;
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
