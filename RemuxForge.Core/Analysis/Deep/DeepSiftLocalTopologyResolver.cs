using RemuxForge.Core.Analysis.Deep.Features;
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
    /// Risolve ogni regione candidata con una sola estrazione full-rate e un solo percorso multi-regime
    /// </summary>
    internal sealed class DeepSiftLocalTopologyResolver
    {
        #region Costanti

        /// <summary>
        /// Numero massimo di coppie per cui è sostenibile una matrice densa
        /// </summary>
        private const long MAXIMUM_DENSE_PAIR_COUNT = 120000;

        /// <summary>
        /// Numero minimo di supporti distinti per considerare osservabile un regime
        /// </summary>
        private const int MINIMUM_DISTINCT_REGIME_SUPPORT = 3;

        /// <summary>
        /// Numero massimo di frame source mantenuti in un tile
        /// </summary>
        private const int SOURCE_TILE_SIZE = 64;

        /// <summary>
        /// Numero di tile source compresi in una stripe di estrazione streaming
        /// </summary>
        private const int STREAMING_SOURCE_TILE_COUNT = 16;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Estrattore dei frame della timeline source
        /// </summary>
        private readonly FrameExtractionService _sourceFrameExtractor;

        /// <summary>
        /// Estrattore dei frame della timeline language
        /// </summary>
        private readonly FrameExtractionService _languageFrameExtractor;

        /// <summary>
        /// Lettore delle informazioni video necessarie a scegliere la strategia di estrazione
        /// </summary>
        private readonly FfmpegVideoInfoReader _videoInfoReader;

        /// <summary>
        /// Matcher batch usato per confrontare le feature visuali
        /// </summary>
        private readonly FrameFeatureBatchMatcherBase _batchMatcher;

        /// <summary>
        /// Configurazione temporale e geometrica condivisa dal resolver
        /// </summary>
        private readonly VideoSyncConfig _videoSyncConfig;

        /// <summary>
        /// Numero massimo di elaborazioni parallele consentite al matcher
        /// </summary>
        private readonly int _maximumParallelism;

        /// <summary>
        /// Risolve se una timeline richiede il ritaglio geometrico
        /// </summary>
        private readonly Func<string, bool> _geometryCropResolver;

        /// <summary>
        /// Normalizza i frame estratti prima della costruzione delle feature
        /// </summary>
        private readonly Action<string, bool, string, List<byte[]>> _frameNormalizer;

        /// <summary>
        /// Sezione di log usata per gli esiti della risoluzione locale
        /// </summary>
        private readonly LogSection _logSection;

        /// <summary>
        /// Soglie condivise per la valutazione dell'evidenza temporale
        /// </summary>
        private readonly DeepSiftTemporalEvidenceOptions _temporalEvidenceOptions;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruisce il resolver indipendente dal backend di matching
        /// </summary>
        /// <param name="ffmpegPath">Percorso dell'eseguibile FFmpeg</param>
        /// <param name="ffmpegConfig">Configurazione del processo FFmpeg</param>
        /// <param name="videoSyncConfig">Configurazione della sincronizzazione video</param>
        /// <param name="logSection">Sezione di log da usare per le operazioni locali</param>
        /// <param name="batchMatcher">Matcher batch delle feature visuali</param>
        /// <param name="geometryCropResolver">Funzione che determina se applicare il ritaglio geometrico</param>
        /// <param name="frameNormalizer">Azione che normalizza i frame estratti</param>
        /// <param name="maximumParallelism">Numero massimo di elaborazioni parallele</param>
        public DeepSiftLocalTopologyResolver(string ffmpegPath, FfmpegConfig ffmpegConfig, VideoSyncConfig videoSyncConfig, LogSection logSection, FrameFeatureBatchMatcherBase batchMatcher, Func<string, bool> geometryCropResolver, Action<string, bool, string, List<byte[]>> frameNormalizer, int maximumParallelism)
        {
            this._videoSyncConfig = videoSyncConfig ?? throw new ArgumentNullException(nameof(videoSyncConfig));
            this._batchMatcher = batchMatcher ?? throw new ArgumentNullException(nameof(batchMatcher));
            this._geometryCropResolver = geometryCropResolver ?? throw new ArgumentNullException(nameof(geometryCropResolver));
            this._frameNormalizer = frameNormalizer ?? throw new ArgumentNullException(nameof(frameNormalizer));
            this._sourceFrameExtractor = new FrameExtractionService(ffmpegPath, videoSyncConfig, ffmpegConfig, logSection);
            this._languageFrameExtractor = new FrameExtractionService(ffmpegPath, videoSyncConfig, ffmpegConfig, logSection);
            this._videoInfoReader = new FfmpegVideoInfoReader(ffmpegPath, ffmpegConfig, logSection);
            this._logSection = logSection;
            this._maximumParallelism = maximumParallelism;
            this._temporalEvidenceOptions = new DeepSiftTemporalEvidenceOptions();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Risolve tutte le regioni senza ricevere una coppia di offset preventiva
        /// </summary>
        /// <param name="sourcePath">Percorso del video source</param>
        /// <param name="languagePath">Percorso del video language</param>
        /// <param name="sourceCropPx">Ritaglio da applicare al video source</param>
        /// <param name="languageCropPx">Ritaglio da applicare al video language</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="sourceDurationMs">Durata della timeline source in millisecondi</param>
        /// <param name="languageDurationMs">Durata della timeline language in millisecondi</param>
        /// <param name="sourceBlackRuns">Intervalli neri rilevati nella timeline source</param>
        /// <param name="languageBlackRuns">Intervalli neri rilevati nella timeline language</param>
        /// <param name="temporal">Evidenza temporale da completare con la topologia locale</param>
        /// <param name="cancellationToken">Token per interrompere l'elaborazione</param>
        public void Resolve(string sourcePath, string languagePath, string sourceCropPx, string languageCropPx, double scale, double sourceDurationMs, double languageDurationMs, IReadOnlyList<DeepBlackTimelineRun> sourceBlackRuns, IReadOnlyList<DeepBlackTimelineRun> languageBlackRuns, DeepSiftTemporalEvidenceResult temporal, CancellationToken cancellationToken)
        {
            this.MergeAdjacentSliceRegions(temporal);
            for (int regionIndex = 0; regionIndex < temporal.CandidateRegions.Count; regionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeepSiftTemporalCandidateRegion region = temporal.CandidateRegions[regionIndex];
                ConsoleHelper.Write(this._logSection, LogLevel.Notice, AppText.F("deep.temporal.log.regionPlan", regionIndex + 1, temporal.CandidateRegions.Count, (int)region.State, region.FirstSliceIndex, region.LastSliceIndex, region.BeforeSupportRunIndex, region.AfterSupportRunIndex, region.GlobalModes.Count, (int)region.OpenReasonFlags));
                if (region.State == DeepSiftCandidateRegionState.GlobalDropout)
                {
                    region.RejectReason = AppText.T("deep.temporal.local.globalDropout");
                    ConsoleHelper.Write(this._logSection, LogLevel.Notice, AppText.F("deep.temporal.log.globalDropout", regionIndex + 1, temporal.CandidateRegions.Count));
                    continue;
                }
                this.ResolveRegion(sourcePath, languagePath, sourceCropPx, languageCropPx, scale, sourceDurationMs, languageDurationMs, sourceBlackRuns, languageBlackRuns, temporal, region, cancellationToken);
                ConsoleHelper.Write(this._logSection, LogLevel.Notice, AppText.F("deep.temporal.log.localRegionResult", regionIndex + 1, temporal.CandidateRegions.Count, region.StrongPairCount, region.AmbiguousPairCount, region.ResolvedRegimeCount, region.ProducedTransitionCount));
                if (!temporal.Accepted)
                    return;
                if (region.State == DeepSiftCandidateRegionState.PendingLocalResolution && this.RequiresDistinctRegimes(temporal, region) && region.Transitions.Count == 0)
                {
                    region.State = DeepSiftCandidateRegionState.Rejected;
                    temporal.Accepted = false;
                    temporal.RejectReason = AppText.T("deep.temporal.local.incompatibleSupportsWithoutRegimes");
                    return;
                }
                if (region.State == DeepSiftCandidateRegionState.PendingLocalResolution && region.Transitions.Count == 0)
                {
                    region.State = DeepSiftCandidateRegionState.ResolvedDropout;
                    region.RejectReason = AppText.T("deep.temporal.local.exhaustedWithoutTransition");
                }
            }
            this.ResolveCanonicalTopology(temporal);
        }

        #endregion

        #region Risoluzione delle regioni

        /// <summary>
        /// Espande e risolve una regione riutilizzando feature, bande e coppie già elaborate
        /// </summary>
        /// <param name="sourcePath">Percorso del video source</param>
        /// <param name="languagePath">Percorso del video language</param>
        /// <param name="sourceCropPx">Ritaglio del video source</param>
        /// <param name="languageCropPx">Ritaglio del video language</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="sourceDurationMs">Durata della timeline source in millisecondi</param>
        /// <param name="languageDurationMs">Durata della timeline language in millisecondi</param>
        /// <param name="sourceBlackRuns">Intervalli neri della timeline source</param>
        /// <param name="languageBlackRuns">Intervalli neri della timeline language</param>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="region">Regione locale da risolvere</param>
        /// <param name="cancellationToken">Token per interrompere la risoluzione</param>
        private void ResolveRegion(string sourcePath, string languagePath, string sourceCropPx, string languageCropPx, double scale, double sourceDurationMs, double languageDurationMs, IReadOnlyList<DeepBlackTimelineRun> sourceBlackRuns, IReadOnlyList<DeepBlackTimelineRun> languageBlackRuns, DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalCandidateRegion region, CancellationToken cancellationToken)
        {
            RegionWorkspace workspace = new RegionWorkspace(scale);
            this._batchMatcher.BeginFeatureReuseScope();
            try
            {
                int expansionDepth = 1;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RegionPlan plan = this.BuildRegionPlanAndMergeOverlaps(temporal, region, scale, expansionDepth);
                    this.StabilizeSearchBands(workspace, plan, scale);
                    this.RecordLocalBlackRuns(region.SourceBlackRuns, sourceBlackRuns, plan.SourceStartMs, plan.SourceEndMs);
                    this.RecordLocalBlackRuns(region.LanguageBlackRuns, languageBlackRuns, plan.LanguageStartMs, plan.LanguageEndMs);
                    if (plan.SourceEndMs <= plan.SourceStartMs || plan.LanguageEndMs <= plan.LanguageStartMs || plan.Bands.Count == 0)
                    {
                        region.State = DeepSiftCandidateRegionState.Rejected;
                        region.RejectReason = AppText.T("deep.temporal.local.invalidPtsCorridor");
                        temporal.Accepted = false;
                        temporal.RejectReason = region.RejectReason;
                        return;
                    }
                    bool workspaceReady = this.ExpandWorkspace(sourcePath, languagePath, sourceCropPx, languageCropPx, scale, plan, region, workspace, cancellationToken);
                    if (!workspaceReady && region.WorkspaceFailure != DeepSiftLocalWorkspaceFailure.InsufficientFrames && region.WorkspaceFailure != DeepSiftLocalWorkspaceFailure.EmptyPairMatrix)
                    {
                        region.State = DeepSiftCandidateRegionState.Rejected;
                        temporal.Accepted = false;
                        temporal.RejectReason = string.IsNullOrEmpty(region.RejectReason) ? AppText.T("deep.temporal.local.workspaceFailed") : region.RejectReason;
                        return;
                    }
                    if (workspaceReady && workspace.AcceptedPairCount > 0)
                    {
                        this.ResolveWorkspacePath(workspace, temporal, region, scale);
                        this.TrimUnresolvedTerminalTail(temporal, region, sourceDurationMs, languageDurationMs);
                        this.AppendDenseResolvedSearchBands(workspace, region);
                        bool hasSufficientBoundarySupport = region.Transitions.Count > 0 && this.HasSufficientBoundarySupport(temporal, region, sourceDurationMs, languageDurationMs);
                        if (hasSufficientBoundarySupport)
                        {
                            region.State = DeepSiftCandidateRegionState.ResolvedTransitions;
                            return;
                        }
                        ConsoleHelper.Write(this._logSection, LogLevel.Notice, AppText.F("deep.temporal.log.unresolvedLocalRegion", region.Path.Count, region.SourceCoverageMs.ToString("F1"), this.SummarizePathOffsets(region.Path), region.Regimes.Count, region.Transitions.Count, false));
                        if (this.ResolvesAsSingleRegimeAcrossBoundary(temporal, region))
                        {
                            region.State = DeepSiftCandidateRegionState.ResolvedDropout;
                            region.RejectReason = AppText.T("deep.temporal.local.sameRegimeDropout");
                            return;
                        }
                    }
                    else if (workspaceReady)
                        region.RejectReason = AppText.T("deep.temporal.local.noAcceptedPairs");
                    int maximumExpansionDepth = this.GetMaximumExpansionDepth(temporal, region);
                    if (expansionDepth >= maximumExpansionDepth)
                    {
                        if (region.Transitions.Count > 0)
                        {
                            region.Transitions.Clear();
                            region.ProducedTransitionCount = 0;
                            region.RejectReason = AppText.T("deep.temporal.local.regimesNotConnectedToBorders");
                        }
                        return;
                    }
                    expansionDepth = Math.Min(maximumExpansionDepth, Math.Max(expansionDepth + 1, expansionDepth * 2));
                    RegionPlan expanded = this.BuildRegionPlan(temporal, region, scale, expansionDepth);
                    while (expansionDepth < maximumExpansionDepth &&
                           expanded.SourceStartMs >= plan.SourceStartMs && expanded.SourceEndMs <= plan.SourceEndMs &&
                           expanded.LanguageStartMs >= plan.LanguageStartMs && expanded.LanguageEndMs <= plan.LanguageEndMs)
                    {
                        expansionDepth = Math.Min(maximumExpansionDepth, Math.Max(expansionDepth + 1, expansionDepth * 2));
                        expanded = this.BuildRegionPlan(temporal, region, scale, expansionDepth);
                    }
                    if (expanded.SourceStartMs >= plan.SourceStartMs && expanded.SourceEndMs <= plan.SourceEndMs &&
                        expanded.LanguageStartMs >= plan.LanguageStartMs && expanded.LanguageEndMs <= plan.LanguageEndMs)
                    {
                        if (region.Transitions.Count > 0)
                        {
                            region.Transitions.Clear();
                            region.ProducedTransitionCount = 0;
                            region.RejectReason = AppText.T("deep.temporal.local.regimesNotConnectedToBorders");
                        }
                        return;
                    }
                }
            }
            finally
            {
                this._batchMatcher.EndFeatureReuseScope();
                DeepSiftVisualAnchorBufferHelper.ReleaseFrames(workspace.SourceAnchors);
                DeepSiftVisualAnchorBufferHelper.ReleaseFrames(workspace.LanguageAnchors);
            }
        }

        /// <summary>
        /// Copia nella regione gli intervalli neri che intersecano l'intervallo temporale richiesto
        /// </summary>
        /// <param name="destination">Lista locale da aggiornare</param>
        /// <param name="source">Intervalli neri globali disponibili</param>
        /// <param name="startMs">Inizio dell'intervallo in millisecondi</param>
        /// <param name="endMs">Fine dell'intervallo in millisecondi</param>
        private void RecordLocalBlackRuns(List<DeepBlackTimelineRun> destination, IReadOnlyList<DeepBlackTimelineRun> source, double startMs, double endMs)
        {
            destination.Clear();
            if (source == null)
                return;
            for (int runIndex = 0; runIndex < source.Count; runIndex++)
            {
                DeepBlackTimelineRun run = source[runIndex];
                if (run.EndPtsMs < startMs || run.StartPtsMs > endMs)
                    continue;
                destination.Add(run);
            }
        }

        /// <summary>
        /// Verifica se un PTS appartiene a uno degli intervalli neri forniti
        /// </summary>
        /// <param name="ptsMs">PTS da verificare in millisecondi</param>
        /// <param name="runs">Intervalli neri da esaminare</param>
        /// <returns>True quando il PTS ricade in un intervallo nero</returns>
        private bool IsInsideBlackRun(double ptsMs, IReadOnlyList<DeepBlackTimelineRun> runs)
        {
            if (runs == null)
                return false;
            for (int runIndex = 0; runIndex < runs.Count; runIndex++)
            {
                DeepBlackTimelineRun run = runs[runIndex];
                if (ptsMs >= run.StartPtsMs && ptsMs <= run.EndPtsMs)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Verifica che i regimi locali siano collegati ai supporti globali sui loro confini
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="region">Regione locale da verificare</param>
        /// <param name="sourceDurationMs">Durata della timeline source in millisecondi</param>
        /// <param name="languageDurationMs">Durata della timeline language in millisecondi</param>
        /// <returns>True quando entrambi i confini hanno supporto sufficiente</returns>
        private bool HasSufficientBoundarySupport(DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalCandidateRegion region, double sourceDurationMs, double languageDurationMs)
        {
            if (region.Regimes.Count == 0)
                return false;
            if (region.BeforeSupportRunIndex >= 0 && !this.RegimeSupportsGlobalRun(region.Regimes[0], temporal.SupportRuns[region.BeforeSupportRunIndex]))
                return false;
            if (region.AfterSupportRunIndex >= 0)
                return this.RegimeSupportsGlobalRun(region.Regimes[region.Regimes.Count - 1], temporal.SupportRuns[region.AfterSupportRunIndex]);
            return this.HasObservableTerminalRejoin(region, sourceDurationMs, languageDurationMs);
        }

        /// <summary>
        /// Conserva i regimi già collegati a supporti globali quando la parte successiva diverge senza rejoin
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="region">Regione locale da ridurre</param>
        /// <param name="sourceDurationMs">Durata della timeline source in millisecondi</param>
        /// <param name="languageDurationMs">Durata della timeline language in millisecondi</param>
        private void TrimUnresolvedTerminalTail(DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalCandidateRegion region, double sourceDurationMs, double languageDurationMs)
        {
            if (region.AfterSupportRunIndex >= 0 || region.Regimes.Count < 2)
                return;
            int lastAnchoredRegimeIndex = -1;
            int lastAnchoredSupportIndex = -1;
            bool observableTerminalRejoin = this.HasObservableTerminalRejoin(region, sourceDurationMs, languageDurationMs);
            for (int regimeIndex = region.Regimes.Count - 1; regimeIndex > 0 && lastAnchoredRegimeIndex < 0; regimeIndex--)
            {
                DeepSiftLocalRegime regime = region.Regimes[regimeIndex];
                for (int supportIndex = region.BeforeSupportRunIndex + 1; supportIndex < temporal.SupportRuns.Count; supportIndex++)
                {
                    DeepSiftTemporalSupportRun support = temporal.SupportRuns[supportIndex];
                    bool overlapsSource = regime.SourceStartPtsMs <= support.SourceEndPtsMs + regime.UncertaintyMs &&
                                          support.SourceStartPtsMs <= regime.SourceEndPtsMs + regime.UncertaintyMs;
                    bool persistentGlobalSupport = support.FirstChainIndex != support.LastChainIndex;
                    if (overlapsSource &&
                        this.RegimeSupportsGlobalRun(regime, support) &&
                        (persistentGlobalSupport || observableTerminalRejoin))
                    {
                        lastAnchoredRegimeIndex = regimeIndex;
                        lastAnchoredSupportIndex = supportIndex;
                        break;
                    }
                }
            }
            if (lastAnchoredRegimeIndex < 0)
                return;
            region.AfterSupportRunIndex = lastAnchoredSupportIndex;
            if (lastAnchoredRegimeIndex == region.Regimes.Count - 1)
                return;
            int lastPathIndex = region.Regimes[lastAnchoredRegimeIndex].LastPathIndex;
            for (int pathIndex = lastPathIndex + 1; pathIndex < region.Path.Count; pathIndex++)
                region.Path[pathIndex].ModeIndex = -1;
            region.Regimes.RemoveRange(lastAnchoredRegimeIndex + 1, region.Regimes.Count - lastAnchoredRegimeIndex - 1);
            region.Transitions.RemoveAll(transition => transition.AfterRegimeIndex > lastAnchoredRegimeIndex);
            region.ResolvedRegimeCount = region.Regimes.Count;
            region.ProducedTransitionCount = region.Transitions.Count;
        }

        /// <summary>
        /// Accetta una transizione terminale soltanto quando l'ultimo regime comune raggiunge entrambe le timeline
        /// </summary>
        /// <param name="region">Regione locale da verificare</param>
        /// <param name="sourceDurationMs">Durata della timeline source in millisecondi</param>
        /// <param name="languageDurationMs">Durata della timeline language in millisecondi</param>
        /// <returns>True quando l'ultimo punto osservabile raggiunge entrambe le durate</returns>
        private bool HasObservableTerminalRejoin(DeepSiftTemporalCandidateRegion region, double sourceDurationMs, double languageDurationMs)
        {
            if (sourceDurationMs <= 0 || languageDurationMs <= 0 || region.Path.Count == 0)
                return false;
            DeepSiftLocalRegime terminal = region.Regimes[region.Regimes.Count - 1];
            DeepSiftLocalPathPoint last = region.Path[terminal.LastPathIndex];
            double uncertaintyMs = Math.Max(terminal.UncertaintyMs, last.UncertaintyMs);
            return last.SourcePtsMs + uncertaintyMs >= sourceDurationMs &&
                   last.LanguagePtsMs + uncertaintyMs >= languageDurationMs;
        }

        /// <summary>
        /// Verifica se l'intervallo di offset di un regime interseca un supporto globale
        /// </summary>
        /// <param name="regime">Regime locale da confrontare</param>
        /// <param name="support">Supporto globale da confrontare</param>
        /// <returns>True quando i due intervalli di offset si intersecano</returns>
        private bool RegimeSupportsGlobalRun(DeepSiftLocalRegime regime, DeepSiftTemporalSupportRun support)
        {
            return this.RegimeOffsetIntersectsSupport(regime, support);
        }

        /// <summary>
        /// Confronta gli intervalli di offset di un regime e di un supporto tenendo conto dell'incertezza
        /// </summary>
        /// <param name="regime">Regime locale da confrontare</param>
        /// <param name="support">Supporto globale da confrontare</param>
        /// <returns>True quando gli intervalli di offset si intersecano</returns>
        private bool RegimeOffsetIntersectsSupport(DeepSiftLocalRegime regime, DeepSiftTemporalSupportRun support)
        {
            return regime.OffsetMs - regime.UncertaintyMs <= support.OffsetMs + support.UncertaintyMs &&
                   support.OffsetMs - support.UncertaintyMs <= regime.OffsetMs + regime.UncertaintyMs;
        }

        /// <summary>
        /// Calcola quante posizioni della catena globale possono essere usate per espandere la regione
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="region">Regione locale da espandere</param>
        /// <returns>Profondità massima di espansione</returns>
        private int GetMaximumExpansionDepth(DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalCandidateRegion region)
        {
            int result = 1;
            if (region.BeforeSupportRunIndex >= 0)
            {
                DeepSiftTemporalSupportRun before = temporal.SupportRuns[region.BeforeSupportRunIndex];
                result = Math.Max(result, before.LastChainIndex - before.FirstChainIndex + 1);
            }
            if (region.AfterSupportRunIndex >= 0)
            {
                DeepSiftTemporalSupportRun after = temporal.SupportRuns[region.AfterSupportRunIndex];
                result = Math.Max(result, after.LastChainIndex - after.FirstChainIndex + 1);
            }
            return result;
        }

        /// <summary>
        /// Fonde soltanto anomalie contigue nella mappa globale, prima di attribuire semantica agli envelope
        /// </summary>
        /// <param name="temporal">Evidenza temporale con le regioni candidate</param>
        private void MergeAdjacentSliceRegions(DeepSiftTemporalEvidenceResult temporal)
        {
            List<DeepSiftTemporalCandidateRegion> merged = new List<DeepSiftTemporalCandidateRegion>();
            for (int regionIndex = 0; regionIndex < temporal.CandidateRegions.Count; regionIndex++)
            {
                DeepSiftTemporalCandidateRegion region = temporal.CandidateRegions[regionIndex];
                if (merged.Count == 0)
                {
                    merged.Add(region);
                    continue;
                }
                int sameUnresolvedStartIndex = this.FindSameUnresolvedStartRegion(merged, region);
                if (sameUnresolvedStartIndex >= 0)
                {
                    DeepSiftTemporalCandidateRegion target = merged[sameUnresolvedStartIndex];
                    for (int mergeIndex = sameUnresolvedStartIndex + 1; mergeIndex < merged.Count; mergeIndex++)
                        this.MergeRegion(target, merged[mergeIndex]);
                    this.MergeRegion(target, region);
                    merged.RemoveRange(sameUnresolvedStartIndex + 1, merged.Count - sameUnresolvedStartIndex - 1);
                    continue;
                }
                DeepSiftTemporalCandidateRegion previous = merged[merged.Count - 1];
                bool adjacent = region.FirstSliceIndex <= previous.LastSliceIndex + 1;
                bool blackLinked = this.HasBlackEvidence(previous, region) &&
                                   previous.AfterSupportRunIndex >= 0 &&
                                   previous.AfterSupportRunIndex == region.BeforeSupportRunIndex;
                if (!adjacent && (!blackLinked || this.HasSeparatingSupportRun(temporal, previous, region)))
                {
                    merged.Add(region);
                    continue;
                }
                this.MergeRegion(previous, region);
            }
            temporal.CandidateRegions = merged;
            for (int regionIndex = 0; regionIndex < temporal.CandidateRegions.Count; regionIndex++)
                temporal.CandidateRegions[regionIndex].Index = regionIndex;
        }

        /// <summary>
        /// Cerca una regione pendente con lo stesso supporto globale iniziale
        /// </summary>
        /// <param name="regions">Regioni già aggregate</param>
        /// <param name="candidate">Regione candidata da confrontare</param>
        /// <returns>Indice della regione compatibile oppure -1</returns>
        private int FindSameUnresolvedStartRegion(IReadOnlyList<DeepSiftTemporalCandidateRegion> regions, DeepSiftTemporalCandidateRegion candidate)
        {
            if (candidate.State != DeepSiftCandidateRegionState.PendingLocalResolution || candidate.BeforeSupportRunIndex < 0)
                return -1;
            for (int regionIndex = regions.Count - 1; regionIndex >= 0; regionIndex--)
            {
                DeepSiftTemporalCandidateRegion region = regions[regionIndex];
                if (region.State == DeepSiftCandidateRegionState.PendingLocalResolution)
                    return region.BeforeSupportRunIndex == candidate.BeforeSupportRunIndex ? regionIndex : -1;
            }
            return -1;
        }

        /// <summary>
        /// Verifica se almeno una delle due regioni è stata aperta da un'anomalia nera
        /// </summary>
        /// <param name="first">Prima regione da confrontare</param>
        /// <param name="second">Seconda regione da confrontare</param>
        /// <returns>True quando una regione contiene evidenza di durata nera differente</returns>
        private bool HasBlackEvidence(DeepSiftTemporalCandidateRegion first, DeepSiftTemporalCandidateRegion second)
        {
            DeepSiftCandidateRegionReason blackReason = DeepSiftCandidateRegionReason.BlackRunDurationDifference;
            return (first.OpenReasonFlags & blackReason) != 0 || (second.OpenReasonFlags & blackReason) != 0;
        }

        /// <summary>
        /// Incorpora una regione nella destinazione aggiornandone envelope, motivi ed evidenze
        /// </summary>
        /// <param name="target">Regione aggregata da aggiornare</param>
        /// <param name="source">Regione da incorporare</param>
        private void MergeRegion(DeepSiftTemporalCandidateRegion target, DeepSiftTemporalCandidateRegion source)
        {
            int targetFirstSliceIndex = target.FirstSliceIndex;
            int targetLastSliceIndex = target.LastSliceIndex;
            if (source.FirstSliceIndex < targetFirstSliceIndex)
                target.BeforeSupportRunIndex = source.BeforeSupportRunIndex;
            else if (source.FirstSliceIndex == targetFirstSliceIndex)
                target.BeforeSupportRunIndex = target.BeforeSupportRunIndex < 0 || source.BeforeSupportRunIndex < 0 ? -1 : Math.Min(target.BeforeSupportRunIndex, source.BeforeSupportRunIndex);
            if (source.LastSliceIndex > targetLastSliceIndex)
                target.AfterSupportRunIndex = source.AfterSupportRunIndex;
            else if (source.LastSliceIndex == targetLastSliceIndex)
                target.AfterSupportRunIndex = target.AfterSupportRunIndex < 0 || source.AfterSupportRunIndex < 0 ? -1 : Math.Max(target.AfterSupportRunIndex, source.AfterSupportRunIndex);
            if (source.State == DeepSiftCandidateRegionState.PendingLocalResolution)
                target.State = DeepSiftCandidateRegionState.PendingLocalResolution;
            target.FirstSliceIndex = Math.Min(target.FirstSliceIndex, source.FirstSliceIndex);
            target.LastSliceIndex = Math.Max(target.LastSliceIndex, source.LastSliceIndex);
            target.SourceStartPtsMs = Math.Min(target.SourceStartPtsMs, source.SourceStartPtsMs);
            target.SourceEndPtsMs = Math.Max(target.SourceEndPtsMs, source.SourceEndPtsMs);
            target.LanguageStartPtsMs = Math.Min(target.LanguageStartPtsMs, source.LanguageStartPtsMs);
            target.LanguageEndPtsMs = Math.Max(target.LanguageEndPtsMs, source.LanguageEndPtsMs);
            target.OpenReasonFlags |= source.OpenReasonFlags;
            for (int modeIndex = 0; modeIndex < source.GlobalModes.Count; modeIndex++)
                target.GlobalModes.Add(source.GlobalModes[modeIndex]);
            this.AppendDistinctOffsets(target.BlackDerivedSearchOffsetsMs, source.BlackDerivedSearchOffsetsMs);
        }

        /// <summary>
        /// Fonde una regione non ancora elaborata appena l'espansione corrente ne raggiunge l'envelope,
        /// mantenendo lo stesso workspace e senza produrre percorsi locali sovrapposti
        /// </summary>
        /// <param name="temporal">Evidenza temporale con le regioni candidate</param>
        /// <param name="region">Regione locale in fase di espansione</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="expansionDepth">Profondità corrente di espansione</param>
        /// <returns>Piano locale aggiornato senza sovrapposizioni con regioni successive</returns>
        private RegionPlan BuildRegionPlanAndMergeOverlaps(DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalCandidateRegion region, double scale, int expansionDepth)
        {
            while (true)
            {
                RegionPlan plan = this.BuildRegionPlan(temporal, region, scale, expansionDepth);
                int regionIndex = temporal.CandidateRegions.IndexOf(region);
                bool merged = false;
                for (int candidateIndex = regionIndex + 1; candidateIndex < temporal.CandidateRegions.Count; candidateIndex++)
                {
                    DeepSiftTemporalCandidateRegion candidate = temporal.CandidateRegions[candidateIndex];
                    DeepSiftTemporalSliceEvidence candidateFirstSlice = this.FindSlice(temporal.Slices, candidate.FirstSliceIndex);
                    DeepSiftTemporalSliceEvidence candidateLastSlice = this.FindSlice(temporal.Slices, candidate.LastSliceIndex);
                    double candidateStartMs = candidateFirstSlice != null ? candidateFirstSlice.SourceStartPtsMs : candidate.SourceStartPtsMs;
                    double candidateEndMs = candidateLastSlice != null ? candidateLastSlice.SourceEndPtsMs : candidate.SourceEndPtsMs;
                    if (candidateStartMs > plan.SourceEndMs || candidateEndMs < plan.SourceStartMs)
                        continue;
                    if (this.HasSeparatingSupportRun(temporal, region, candidate))
                        continue;
                    this.MergeRegion(region, candidate);
                    temporal.CandidateRegions.RemoveAt(candidateIndex);
                    for (int index = regionIndex; index < temporal.CandidateRegions.Count; index++)
                        temporal.CandidateRegions[index].Index = index;
                    merged = true;
                    break;
                }
                if (!merged)
                    return plan;
            }
        }

        /// <summary>
        /// Conserva separate due anomalie quando fra loro esiste già un regime globale osservabile
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="first">Prima regione da confrontare</param>
        /// <param name="second">Seconda regione da confrontare</param>
        /// <returns>True quando un supporto globale separa le due anomalie</returns>
        private bool HasSeparatingSupportRun(DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalCandidateRegion first, DeepSiftTemporalCandidateRegion second)
        {
            int sharedRunIndex = first.AfterSupportRunIndex;
            return first.BeforeSupportRunIndex >= 0 &&
                   sharedRunIndex >= 0 &&
                   sharedRunIndex < temporal.SupportRuns.Count &&
                   first.BeforeSupportRunIndex != sharedRunIndex &&
                   second.BeforeSupportRunIndex == sharedRunIndex &&
                   temporal.SupportRuns[sharedRunIndex].MatchCount > 0;
        }

        #endregion

        #region Topologia canonica

        /// <summary>
        /// Costruisce l'unico percorso definitivo sostituendo la dorsale globale nelle regioni risolte localmente
        /// </summary>
        /// <param name="temporal">Evidenza temporale da ricomporre</param>
        private void ResolveCanonicalTopology(DeepSiftTemporalEvidenceResult temporal)
        {
            Dictionary<(long SourcePts, long LanguagePts), DeepSiftLocalPathPoint> points = new Dictionary<(long SourcePts, long LanguagePts), DeepSiftLocalPathPoint>();
            for (int chainIndex = 0; chainIndex < temporal.Chain.Count; chainIndex++)
            {
                DeepSiftTemporalChainMatch match = temporal.Chain[chainIndex];
                if (this.IsInsideResolvedCandidateRegion(temporal, match.SourcePtsMs, match.LanguagePtsMs))
                    continue;
                DeepSiftLocalPathPoint point = this.CreateGlobalPathPoint(match);
                points[this.GetPairPtsKey(point.SourcePtsMs, point.LanguagePtsMs)] = point;
            }
            for (int regionIndex = 0; regionIndex < temporal.CandidateRegions.Count; regionIndex++)
            {
                DeepSiftTemporalCandidateRegion region = temporal.CandidateRegions[regionIndex];
                if (region.State != DeepSiftCandidateRegionState.ResolvedTransitions)
                    continue;
                for (int pointIndex = 0; pointIndex < region.Path.Count; pointIndex++)
                {
                    DeepSiftLocalPathPoint point = region.Path[pointIndex];
                    if (point.ModeIndex < 0)
                        continue;
                    (long SourcePts, long LanguagePts) key = this.GetPairPtsKey(point.SourcePtsMs, point.LanguagePtsMs);
                    if (!points.TryGetValue(key, out DeepSiftLocalPathPoint existing) || DeepSiftTemporalMetricComparer.QuantizeMetric(point.Score) > DeepSiftTemporalMetricComparer.QuantizeMetric(existing.Score))
                        points[key] = point;
                }
            }
            List<DeepSiftLocalPathPoint> resolvedPath = new List<DeepSiftLocalPathPoint>(points.Values);
            resolvedPath.Sort(this.ComparePoints);
            for (int pointIndex = 1; pointIndex < resolvedPath.Count; pointIndex++)
            {
                DeepSiftLocalPathPoint previous = resolvedPath[pointIndex - 1];
                DeepSiftLocalPathPoint current = resolvedPath[pointIndex];
                if (previous.SourcePtsMs < current.SourcePtsMs && previous.LanguagePtsMs < current.LanguagePtsMs)
                    continue;
                temporal.Accepted = false;
                temporal.RejectReason = AppText.F("deep.temporal.local.canonicalPathNotMonotonic", current.SourcePtsMs.ToString("F3", CultureInfo.InvariantCulture));
                temporal.ResolvedPath.Clear();
                temporal.ResolvedRegimes.Clear();
                temporal.ResolvedTransitions.Clear();
                return;
            }
            temporal.ResolvedPath = resolvedPath;
            if (!this.ComposeCanonicalRegimes(temporal))
            {
                temporal.Accepted = false;
                temporal.RejectReason = AppText.T("deep.temporal.local.canonicalTransitionsNotMonotonic");
                temporal.ResolvedRegimes.Clear();
                temporal.ResolvedTransitions.Clear();
            }
        }

        /// <summary>
        /// Compone i regimi definitivi mantenendo le transizioni già dimostrate dai resolver locali
        /// </summary>
        /// <param name="temporal">Evidenza temporale con il percorso canonico</param>
        /// <returns>True quando regimi e transizioni canonici sono coerenti</returns>
        private bool ComposeCanonicalRegimes(DeepSiftTemporalEvidenceResult temporal)
        {
            List<DeepSiftLocalTransition> localTransitions = new List<DeepSiftLocalTransition>();
            for (int regionIndex = 0; regionIndex < temporal.CandidateRegions.Count; regionIndex++)
            {
                DeepSiftTemporalCandidateRegion region = temporal.CandidateRegions[regionIndex];
                if (region.State != DeepSiftCandidateRegionState.ResolvedTransitions)
                    continue;
                localTransitions.AddRange(region.Transitions);
            }
            localTransitions.Sort((left, right) =>
            {
                int comparison = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(left.FirstAfterSourcePtsMs).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(right.FirstAfterSourcePtsMs));
                return comparison != 0 ? comparison : DeepSiftTemporalMetricComparer.QuantizeMilliseconds(left.FirstAfterLanguagePtsMs).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(right.FirstAfterLanguagePtsMs));
            });
            localTransitions = this.MergeDuplicateTransitionCorridors(localTransitions);

            temporal.ResolvedRegimes.Clear();
            temporal.ResolvedTransitions.Clear();
            int segmentStartIndex = 0;
            for (int transitionIndex = 0; transitionIndex < localTransitions.Count; transitionIndex++)
            {
                DeepSiftLocalTransition local = localTransitions[transitionIndex];
                int segmentEndIndex = this.FindCanonicalTransitionSplit(temporal.ResolvedPath, segmentStartIndex, local) - 1;
                if (segmentEndIndex < segmentStartIndex)
                    return false;
                temporal.ResolvedRegimes.Add(this.BuildRegime(temporal.ResolvedPath, segmentStartIndex, segmentEndIndex));
                this.AssignCanonicalMode(temporal.ResolvedPath, segmentStartIndex, segmentEndIndex, transitionIndex);

                DeepSiftLocalTransition transition = new DeepSiftLocalTransition();
                transition.BeforeRegimeIndex = transitionIndex;
                transition.AfterRegimeIndex = transitionIndex + 1;
                transition.LastBeforeSourcePtsMs = local.LastBeforeSourcePtsMs;
                transition.FirstAfterSourcePtsMs = local.FirstAfterSourcePtsMs;
                transition.LastBeforeLanguagePtsMs = local.LastBeforeLanguagePtsMs;
                transition.FirstAfterLanguagePtsMs = local.FirstAfterLanguagePtsMs;
                transition.FirstAfterCandidateSourcePtsMs = local.FirstAfterCandidateSourcePtsMs;
                transition.FirstAfterCandidateLanguagePtsMs = local.FirstAfterCandidateLanguagePtsMs;
                temporal.ResolvedTransitions.Add(transition);
                segmentStartIndex = segmentEndIndex + 1;
            }
            if (segmentStartIndex >= temporal.ResolvedPath.Count)
                return false;
            temporal.ResolvedRegimes.Add(this.BuildRegime(temporal.ResolvedPath, segmentStartIndex, temporal.ResolvedPath.Count - 1));
            this.AssignCanonicalMode(temporal.ResolvedPath, segmentStartIndex, temporal.ResolvedPath.Count - 1, localTransitions.Count);
            if (temporal.ResolvedRegimes.Count != temporal.ResolvedTransitions.Count + 1)
                return false;
            this.AnchorCanonicalRegimesToGlobalSupport(temporal);
            return true;
        }

        /// <summary>
        /// Attribuisce ogni support-run al solo regime locale più vicino prima di usarne l'offset robusto
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        private void AnchorCanonicalRegimesToGlobalSupport(DeepSiftTemporalEvidenceResult temporal)
        {
            List<List<DeepSiftTemporalSupportRun>> assignments = new List<List<DeepSiftTemporalSupportRun>>(temporal.ResolvedRegimes.Count);
            for (int regimeIndex = 0; regimeIndex < temporal.ResolvedRegimes.Count; regimeIndex++)
                assignments.Add(new List<DeepSiftTemporalSupportRun>());

            for (int supportIndex = 0; supportIndex < temporal.SupportRuns.Count; supportIndex++)
            {
                DeepSiftTemporalSupportRun support = temporal.SupportRuns[supportIndex];
                int selectedRegimeIndex = -1;
                double selectedDistanceMs = double.PositiveInfinity;
                double selectedOverlapMs = double.NegativeInfinity;
                for (int regimeIndex = 0; regimeIndex < temporal.ResolvedRegimes.Count; regimeIndex++)
                {
                    DeepSiftLocalRegime regime = temporal.ResolvedRegimes[regimeIndex];
                    if (!this.RegimeOffsetIntersectsSupport(regime, support))
                        continue;
                    double overlapMs = Math.Min(regime.SourceEndPtsMs, support.SourceEndPtsMs) - Math.Max(regime.SourceStartPtsMs, support.SourceStartPtsMs);
                    if (overlapMs < 0.0)
                        continue;
                    double distanceMs = Math.Abs(regime.OffsetMs - support.OffsetMs);
                    long distance = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(distanceMs);
                    long selectedDistance = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(selectedDistanceMs);
                    long overlap = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(overlapMs);
                    long selectedOverlap = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(selectedOverlapMs);
                    if (selectedRegimeIndex >= 0 && (distance > selectedDistance || distance == selectedDistance && overlap <= selectedOverlap))
                        continue;
                    selectedRegimeIndex = regimeIndex;
                    selectedDistanceMs = distanceMs;
                    selectedOverlapMs = overlapMs;
                }
                if (selectedRegimeIndex >= 0)
                    assignments[selectedRegimeIndex].Add(support);
            }

            for (int regimeIndex = 0; regimeIndex < temporal.ResolvedRegimes.Count; regimeIndex++)
            {
                DeepSiftLocalRegime regime = temporal.ResolvedRegimes[regimeIndex];
                DeepSiftTemporalSupportRun selected = null;
                double selectedDistanceMs = double.PositiveInfinity;
                double selectedOverlapMs = double.NegativeInfinity;
                for (int assignmentIndex = 0; assignmentIndex < assignments[regimeIndex].Count; assignmentIndex++)
                {
                    DeepSiftTemporalSupportRun support = assignments[regimeIndex][assignmentIndex];
                    double distanceMs = Math.Abs(regime.OffsetMs - support.OffsetMs);
                    double overlapMs = Math.Min(regime.SourceEndPtsMs, support.SourceEndPtsMs) - Math.Max(regime.SourceStartPtsMs, support.SourceStartPtsMs);
                    long distance = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(distanceMs);
                    long selectedDistance = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(selectedDistanceMs);
                    long overlap = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(overlapMs);
                    long selectedOverlap = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(selectedOverlapMs);
                    if (selected != null && (distance > selectedDistance || distance == selectedDistance && overlap <= selectedOverlap))
                        continue;
                    selected = support;
                    selectedDistanceMs = distanceMs;
                    selectedOverlapMs = overlapMs;
                }
                if (selected != null)
                    regime.OffsetMs = selected.OffsetMs;
                else
                {
                    DeepSiftTemporalMode globalMode = this.SelectCanonicalGlobalMode(temporal.Slices, regime);
                    if (globalMode != null)
                        regime.OffsetMs = globalMode.OffsetMs;
                }
            }
        }

        /// <summary>
        /// Seleziona un modo globale affidabile soltanto per rendere canonico l'offset di una topologia già risolta localmente
        /// </summary>
        /// <param name="slices">Slice temporali globali da esaminare</param>
        /// <param name="regime">Regime locale da ancorare</param>
        /// <returns>Modo globale più adatto oppure null</returns>
        private DeepSiftTemporalMode SelectCanonicalGlobalMode(IReadOnlyList<DeepSiftTemporalSliceEvidence> slices, DeepSiftLocalRegime regime)
        {
            DeepSiftTemporalMode result = null;
            double resultOverlapMs = double.NegativeInfinity;
            int resultDistinctSupport = -1;
            for (int sliceIndex = 0; sliceIndex < slices.Count; sliceIndex++)
            {
                DeepSiftTemporalSliceEvidence slice = slices[sliceIndex];
                for (int modeIndex = 0; modeIndex < slice.Modes.Count; modeIndex++)
                {
                    DeepSiftTemporalMode mode = slice.Modes[modeIndex];
                    int distinctSupport = Math.Min(mode.StrongDistinctSourceCount, mode.StrongDistinctLanguageCount);
                    if (mode.TemporallyAmbiguous || mode.Representative == null || distinctSupport < MINIMUM_DISTINCT_REGIME_SUPPORT)
                        continue;
                    double overlapMs = Math.Min(regime.SourceEndPtsMs, mode.SourceEndPtsMs) - Math.Max(regime.SourceStartPtsMs, mode.SourceStartPtsMs);
                    if (overlapMs < 0.0 || !this.IntervalsOverlap(regime.OffsetMs, regime.UncertaintyMs, mode.OffsetMs, mode.UncertaintyMs))
                        continue;
                    if (result != null && !this.IsBetterCanonicalGlobalMode(mode, overlapMs, distinctSupport, result, resultOverlapMs, resultDistinctSupport))
                        continue;
                    result = mode;
                    resultOverlapMs = overlapMs;
                    resultDistinctSupport = distinctSupport;
                }
            }
            return result;
        }

        /// <summary>
        /// Confronta due modi globali secondo i criteri deterministici di canonicalizzazione
        /// </summary>
        /// <param name="candidate">Modo candidato</param>
        /// <param name="candidateOverlapMs">Sovrapposizione del candidato in millisecondi</param>
        /// <param name="candidateDistinctSupport">Supporto distinto del candidato</param>
        /// <param name="current">Modo attualmente selezionato</param>
        /// <param name="currentOverlapMs">Sovrapposizione del modo corrente in millisecondi</param>
        /// <param name="currentDistinctSupport">Supporto distinto del modo corrente</param>
        /// <returns>True quando il candidato è preferibile al modo corrente</returns>
        private bool IsBetterCanonicalGlobalMode(DeepSiftTemporalMode candidate, double candidateOverlapMs, int candidateDistinctSupport, DeepSiftTemporalMode current, double currentOverlapMs, int currentDistinctSupport)
        {
            long candidateOverlap = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidateOverlapMs);
            long currentOverlap = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(currentOverlapMs);
            if (candidateOverlap != currentOverlap)
                return candidateOverlap > currentOverlap;
            if (candidateDistinctSupport != currentDistinctSupport)
                return candidateDistinctSupport > currentDistinctSupport;
            if (candidate.StrongPairCount != current.StrongPairCount)
                return candidate.StrongPairCount > current.StrongPairCount;
            long candidateUncertainty = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.UncertaintyMs);
            long currentUncertainty = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(current.UncertaintyMs);
            if (candidateUncertainty != currentUncertainty)
                return candidateUncertainty < currentUncertainty;
            long candidateStart = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.SourceStartPtsMs);
            long currentStart = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(current.SourceStartPtsMs);
            if (candidateStart != currentStart)
                return candidateStart < currentStart;
            if (candidate.SliceIndex != current.SliceIndex)
                return candidate.SliceIndex < current.SliceIndex;
            return candidate.ModeIndex < current.ModeIndex;
        }

        /// <summary>
        /// Riduce a una sola transizione le prove locali i cui corridoi PTS descrivono lo stesso confine
        /// </summary>
        /// <param name="transitions">Transizioni locali ordinate per posizione</param>
        /// <returns>Transizioni con i corridoi duplicati compattati</returns>
        private List<DeepSiftLocalTransition> MergeDuplicateTransitionCorridors(IReadOnlyList<DeepSiftLocalTransition> transitions)
        {
            List<DeepSiftLocalTransition> result = new List<DeepSiftLocalTransition>();
            for (int transitionIndex = 0; transitionIndex < transitions.Count; transitionIndex++)
            {
                DeepSiftLocalTransition candidate = transitions[transitionIndex];
                if (result.Count == 0 || !this.TransitionCorridorsOverlap(result[result.Count - 1], candidate))
                {
                    result.Add(candidate);
                    continue;
                }
                if (this.CompareTransitionCorridors(candidate, result[result.Count - 1]) < 0)
                    result[result.Count - 1] = candidate;
            }
            return result;
        }

        /// <summary>
        /// Verifica se due corridoi di transizione si sovrappongono su entrambi gli assi temporali
        /// </summary>
        /// <param name="first">Primo corridoio da confrontare</param>
        /// <param name="second">Secondo corridoio da confrontare</param>
        /// <returns>True quando i corridoi si sovrappongono</returns>
        private bool TransitionCorridorsOverlap(DeepSiftLocalTransition first, DeepSiftLocalTransition second)
        {
            bool sourceOverlaps = first.LastBeforeSourcePtsMs <= second.FirstAfterSourcePtsMs &&
                                  second.LastBeforeSourcePtsMs <= first.FirstAfterSourcePtsMs;
            bool languageOverlaps = first.LastBeforeLanguagePtsMs <= second.FirstAfterLanguagePtsMs &&
                                    second.LastBeforeLanguagePtsMs <= first.FirstAfterLanguagePtsMs;
            return sourceOverlaps && languageOverlaps;
        }

        /// <summary>
        /// Confronta due corridoi privilegiando ampiezza e posizione deterministiche
        /// </summary>
        /// <param name="candidate">Primo corridoio da confrontare</param>
        /// <param name="alternative">Corridoio alternativo</param>
        /// <returns>Risultato del confronto ordinato</returns>
        private int CompareTransitionCorridors(DeepSiftLocalTransition candidate, DeepSiftLocalTransition alternative)
        {
            double candidateWidthMs = candidate.FirstAfterSourcePtsMs - candidate.LastBeforeSourcePtsMs +
                                      candidate.FirstAfterLanguagePtsMs - candidate.LastBeforeLanguagePtsMs;
            double alternativeWidthMs = alternative.FirstAfterSourcePtsMs - alternative.LastBeforeSourcePtsMs +
                                        alternative.FirstAfterLanguagePtsMs - alternative.LastBeforeLanguagePtsMs;
            int comparison = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidateWidthMs).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(alternativeWidthMs));
            if (comparison != 0)
                return comparison;
            comparison = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.FirstAfterSourcePtsMs).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(alternative.FirstAfterSourcePtsMs));
            return comparison != 0 ? comparison : DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.FirstAfterLanguagePtsMs).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(alternative.FirstAfterLanguagePtsMs));
        }

        /// <summary>
        /// Trova il primo punto canonico successivo al corridoio locale della transizione
        /// </summary>
        /// <param name="path">Percorso canonico da esaminare</param>
        /// <param name="startIndex">Indice da cui iniziare la ricerca</param>
        /// <param name="transition">Transizione locale di riferimento</param>
        /// <returns>Indice del primo punto successivo oppure la lunghezza del percorso</returns>
        private int FindCanonicalTransitionSplit(IReadOnlyList<DeepSiftLocalPathPoint> path, int startIndex, DeepSiftLocalTransition transition)
        {
            for (int pathIndex = startIndex; pathIndex < path.Count; pathIndex++)
            {
                if (path[pathIndex].SourcePtsMs >= transition.FirstAfterSourcePtsMs && path[pathIndex].LanguagePtsMs >= transition.FirstAfterLanguagePtsMs)
                    return pathIndex;
            }
            return path.Count;
        }

        /// <summary>
        /// Assegna un tratto contiguo del percorso al regime canonico indicato
        /// </summary>
        /// <param name="path">Percorso da aggiornare</param>
        /// <param name="startIndex">Indice iniziale del tratto</param>
        /// <param name="endIndex">Indice finale del tratto</param>
        /// <param name="modeIndex">Indice del regime da assegnare</param>
        private void AssignCanonicalMode(IReadOnlyList<DeepSiftLocalPathPoint> path, int startIndex, int endIndex, int modeIndex)
        {
            for (int pathIndex = startIndex; pathIndex <= endIndex; pathIndex++)
                path[pathIndex].ModeIndex = modeIndex;
        }

        /// <summary>
        /// Verifica se una coppia PTS ricade nella proiezione di una regione già risolta
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="sourcePtsMs">PTS source da verificare</param>
        /// <param name="languagePtsMs">PTS language da verificare</param>
        /// <returns>True quando almeno una proiezione della regione contiene la coppia</returns>
        private bool IsInsideResolvedCandidateRegion(DeepSiftTemporalEvidenceResult temporal, double sourcePtsMs, double languagePtsMs)
        {
            for (int regionIndex = 0; regionIndex < temporal.CandidateRegions.Count; regionIndex++)
            {
                DeepSiftTemporalCandidateRegion region = temporal.CandidateRegions[regionIndex];
                if (region.State != DeepSiftCandidateRegionState.ResolvedTransitions || region.Path.Count == 0)
                    continue;
                int firstResolvedIndex = 0;
                while (firstResolvedIndex < region.Path.Count && region.Path[firstResolvedIndex].ModeIndex < 0)
                    firstResolvedIndex++;
                int lastResolvedIndex = region.Path.Count - 1;
                while (lastResolvedIndex >= firstResolvedIndex && region.Path[lastResolvedIndex].ModeIndex < 0)
                    lastResolvedIndex--;
                if (lastResolvedIndex < firstResolvedIndex)
                    continue;
                DeepSiftLocalPathPoint firstResolved = region.Path[firstResolvedIndex];
                DeepSiftLocalPathPoint lastResolved = region.Path[lastResolvedIndex];
                bool insideSourceProjection = sourcePtsMs >= firstResolved.SourcePtsMs && sourcePtsMs <= lastResolved.SourcePtsMs;
                bool insideLanguageProjection = languagePtsMs >= firstResolved.LanguagePtsMs && languagePtsMs <= lastResolved.LanguagePtsMs;
                if (insideSourceProjection || insideLanguageProjection)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Converte un match globale nel punto usato dal percorso locale canonico
        /// </summary>
        /// <param name="match">Match globale da convertire</param>
        /// <returns>Punto del percorso con l'evidenza del match</returns>
        private DeepSiftLocalPathPoint CreateGlobalPathPoint(DeepSiftTemporalChainMatch match)
        {
            DeepSiftLocalPathPoint result = new DeepSiftLocalPathPoint();
            result.SourceAnchorIndex = match.SourceAnchorIndex;
            result.LanguageAnchorIndex = match.LanguageAnchorIndex;
            result.ModeIndex = match.ModeIndex;
            result.SourcePtsMs = match.SourcePtsMs;
            result.LanguagePtsMs = match.LanguagePtsMs;
            result.OffsetMs = match.OffsetMs;
            result.UncertaintyMs = match.UncertaintyMs;
            result.Score = match.Score;
            result.DistinctSupportCount = match.SupportCount;
            result.Classification = DeepSiftTemporalPairClassification.Strong;
            return result;
        }

        #endregion

        #region Workspace, estrazione e matching

        /// <summary>
        /// Esegue la matrice pianificata a tile mantenendo identiche coppie e PTS
        /// </summary>
        /// <param name="source">Ancore della timeline source</param>
        /// <param name="language">Ancore della timeline language</param>
        /// <param name="pairs">Coppie di ancore da confrontare</param>
        /// <param name="sourceBlackRuns">Intervalli neri della timeline source</param>
        /// <param name="languageBlackRuns">Intervalli neri della timeline language</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="cancellationToken">Token per interrompere il matching</param>
        /// <returns>Esito del matching locale e delle coppie accettate</returns>
        private LocalMatchResult MatchTiles(IReadOnlyList<DeepSiftVisualAnchor> source, IReadOnlyList<DeepSiftVisualAnchor> language, IReadOnlyList<DeepSiftFramePair> pairs, IReadOnlyList<DeepBlackTimelineRun> sourceBlackRuns, IReadOnlyList<DeepBlackTimelineRun> languageBlackRuns, double scale, CancellationToken cancellationToken)
        {
            LocalMatchResult result = new LocalMatchResult(scale);
            Stopwatch stopwatch = Stopwatch.StartNew();
            int sourceTileSize = this._batchMatcher.SupportsNativeSparseBatching ? Math.Max(1, source.Count) : SOURCE_TILE_SIZE;
            for (int sourceStartIndex = 0; sourceStartIndex < source.Count; sourceStartIndex += sourceTileSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int sourceEndIndex = Math.Min(source.Count, sourceStartIndex + sourceTileSize);
                List<DeepSiftFramePair> sourcePairs = new List<DeepSiftFramePair>();
                SortedSet<int> languageIndexes = new SortedSet<int>();
                for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
                {
                    DeepSiftFramePair pair = pairs[pairIndex];
                    if (pair.SourceAnchorIndex < sourceStartIndex || pair.SourceAnchorIndex >= sourceEndIndex)
                        continue;
                    sourcePairs.Add(pair);
                    languageIndexes.Add(pair.LanguageAnchorIndex);
                }
                if (languageIndexes.Count == 0)
                    continue;

                List<DeepSiftVisualAnchor> sourceTile = new List<DeepSiftVisualAnchor>(sourceEndIndex - sourceStartIndex);
                for (int sourceIndex = sourceStartIndex; sourceIndex < sourceEndIndex; sourceIndex++)
                    sourceTile.Add(source[sourceIndex]);
                List<int> languageGlobalIndexes = new List<int>(languageIndexes);
                Dictionary<int, int> languageLocalIndexes = new Dictionary<int, int>(languageGlobalIndexes.Count);
                List<DeepSiftVisualAnchor> languageTile = new List<DeepSiftVisualAnchor>(languageGlobalIndexes.Count);
                for (int languageIndex = 0; languageIndex < languageGlobalIndexes.Count; languageIndex++)
                {
                    int globalIndex = languageGlobalIndexes[languageIndex];
                    languageLocalIndexes.Add(globalIndex, languageIndex);
                    languageTile.Add(language[globalIndex]);
                }
                List<DeepSiftFramePair> tilePairs = new List<DeepSiftFramePair>(sourcePairs.Count);
                for (int pairIndex = 0; pairIndex < sourcePairs.Count; pairIndex++)
                {
                    DeepSiftFramePair pair = sourcePairs[pairIndex];
                    tilePairs.Add(new DeepSiftFramePair { SourceAnchorIndex = pair.SourceAnchorIndex - sourceStartIndex, LanguageAnchorIndex = languageLocalIndexes[pair.LanguageAnchorIndex] });
                }

                DeepSiftBatchMatchResult batch = this._batchMatcher.BuildMatrix(sourceTile, languageTile, this._maximumParallelism, cancellationToken, null, tilePairs);
                if (batch.Cancelled)
                    throw new OperationCanceledException(cancellationToken);
                if (batch.Matrix == null || !string.IsNullOrEmpty(batch.RejectReason))
                {
                    result.RejectReason = string.IsNullOrEmpty(batch.RejectReason) ? AppText.T("deep.temporal.local.tileMatchingUnavailable") : batch.RejectReason;
                    result.MatchingMs = stopwatch.ElapsedMilliseconds;
                    return result;
                }
                for (int acceptedIndex = 0; acceptedIndex < batch.AcceptedPairs.Count; acceptedIndex++)
                {
                    DeepSiftAcceptedPairDiagnostic accepted = batch.AcceptedPairs[acceptedIndex];
                    accepted.SourceAnchorIndex += sourceStartIndex;
                    accepted.LanguageAnchorIndex = languageGlobalIndexes[accepted.LanguageAnchorIndex];
                    if (this.IsInsideBlackRun(accepted.SourcePtsMs, sourceBlackRuns) || this.IsInsideBlackRun(accepted.LanguagePtsMs, languageBlackRuns))
                        continue;
                    result.Add(accepted);
                }
            }
            result.Complete();
            result.MatchingMs = stopwatch.ElapsedMilliseconds;
            return result;
        }

        /// <summary>
        /// Costruisce envelope, bande di offset e intervallo language per l'espansione corrente
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="region">Regione locale da pianificare</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="expansionDepth">Profondità corrente di espansione</param>
        /// <returns>Piano di estrazione e matching della regione</returns>
        private RegionPlan BuildRegionPlan(DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalCandidateRegion region, double scale, int expansionDepth)
        {
            RegionPlan result = new RegionPlan();
            DeepSiftTemporalSliceEvidence firstSlice = this.FindSlice(temporal.Slices, region.FirstSliceIndex);
            DeepSiftTemporalSliceEvidence lastSlice = this.FindSlice(temporal.Slices, region.LastSliceIndex);
            result.SourceStartMs = firstSlice != null ? firstSlice.SourceStartPtsMs : region.SourceStartPtsMs;
            result.SourceEndMs = lastSlice != null ? lastSlice.SourceEndPtsMs : region.SourceEndPtsMs;
            if (region.BeforeSupportRunIndex >= 0)
            {
                DeepSiftTemporalSupportRun before = temporal.SupportRuns[region.BeforeSupportRunIndex];
                int adjacentChainIndex = this.FindBeforeSupportChainIndex(temporal, before, region.FirstSliceIndex);
                int supportChainIndex = Math.Max(before.FirstChainIndex, adjacentChainIndex - expansionDepth);
                DeepSiftTemporalSliceEvidence supportSlice = this.FindSlice(temporal.Slices, temporal.Chain[supportChainIndex].SliceIndex);
                if (supportSlice != null)
                    result.SourceStartMs = Math.Min(result.SourceStartMs, supportSlice.SourceStartPtsMs);
            }
            if (region.AfterSupportRunIndex >= 0)
            {
                DeepSiftTemporalSupportRun after = temporal.SupportRuns[region.AfterSupportRunIndex];
                int adjacentChainIndex = this.FindAfterSupportChainIndex(temporal, after, region.LastSliceIndex);
                int supportChainIndex = Math.Min(after.LastChainIndex, adjacentChainIndex + expansionDepth);
                DeepSiftTemporalSliceEvidence supportSlice = this.FindSlice(temporal.Slices, temporal.Chain[supportChainIndex].SliceIndex);
                if (supportSlice != null)
                    result.SourceEndMs = Math.Max(result.SourceEndMs, supportSlice.SourceEndPtsMs);
            }
            if (region.BeforeSupportRunIndex >= 0)
                this.AddSupportBand(result.Bands, temporal.SupportRuns[region.BeforeSupportRunIndex], result.SourceStartMs, result.SourceEndMs);
            if (region.AfterSupportRunIndex >= 0)
                this.AddSupportBand(result.Bands, temporal.SupportRuns[region.AfterSupportRunIndex], result.SourceStartMs, result.SourceEndMs);
            for (int sliceIndex = 0; sliceIndex < temporal.Slices.Count; sliceIndex++)
            {
                DeepSiftTemporalSliceEvidence slice = temporal.Slices[sliceIndex];
                if (slice.SourceEndPtsMs < result.SourceStartMs || slice.SourceStartPtsMs > result.SourceEndMs)
                    continue;
                for (int modeIndex = 0; modeIndex < slice.Modes.Count; modeIndex++)
                {
                    DeepSiftTemporalMode mode = slice.Modes[modeIndex];
                    if (!this.IsReliableGlobalMode(mode))
                        continue;
                    double searchUncertaintyMs = Math.Max(1.0, mode.UncertaintyMs - mode.DispersionMs);
                    this.AddBand(result.Bands, mode.OffsetMs, searchUncertaintyMs, slice.SourceStartPtsMs, slice.SourceEndPtsMs);
                }
            }
            double blackSearchUncertaintyMs = this.GetRegionSearchUncertainty(temporal, region);
            for (int offsetIndex = 0; offsetIndex < region.BlackDerivedSearchOffsetsMs.Count; offsetIndex++)
                this.AddBand(result.Bands, region.BlackDerivedSearchOffsetsMs[offsetIndex], blackSearchUncertaintyMs, result.SourceStartMs, result.SourceEndMs);
            result.SourceStartMs = Math.Max(0.0, result.SourceStartMs);
            result.SourceEndMs = Math.Max(result.SourceStartMs, result.SourceEndMs);
            double minimumLanguageMs = double.PositiveInfinity;
            double maximumLanguageMs = double.NegativeInfinity;
            for (int bandIndex = 0; bandIndex < result.Bands.Count; bandIndex++)
            {
                OffsetBand band = result.Bands[bandIndex];
                minimumLanguageMs = Math.Min(minimumLanguageMs, (band.SourceStartMs - band.OffsetMs - band.UncertaintyMs) * scale);
                maximumLanguageMs = Math.Max(maximumLanguageMs, (band.SourceEndMs - band.OffsetMs + band.UncertaintyMs) * scale);
            }
            result.LanguageStartMs = Math.Max(0.0, minimumLanguageMs);
            result.LanguageEndMs = Math.Max(result.LanguageStartMs, maximumLanguageMs);
            region.ExpansionCount = expansionDepth;
            return result;
        }

        /// <summary>
        /// Verifica se un modo globale possiede rappresentante e supporto sufficienti per guidare la ricerca
        /// </summary>
        /// <param name="mode">Modo globale da valutare</param>
        /// <returns>True quando il modo è affidabile per la pianificazione</returns>
        private bool IsReliableGlobalMode(DeepSiftTemporalMode mode)
        {
            return mode.Representative != null &&
                   !mode.TemporallyAmbiguous &&
                   mode.StrongDistinctSourceCount >= this._temporalEvidenceOptions.MinimumDistinctSupport &&
                   mode.StrongDistinctLanguageCount >= this._temporalEvidenceOptions.MinimumDistinctSupport;
        }

        /// <summary>
        /// Conserva bande e intervalli PTS già elaborati e aggiunge soltanto le estensioni incontrate
        /// </summary>
        /// <param name="workspace">Workspace persistente della regione</param>
        /// <param name="plan">Piano corrente da stabilizzare</param>
        /// <param name="scale">Scala temporale source-language</param>
        private void StabilizeSearchBands(RegionWorkspace workspace, RegionPlan plan, double scale)
        {
            for (int planBandIndex = 0; planBandIndex < plan.Bands.Count; planBandIndex++)
            {
                OffsetBand planBand = plan.Bands[planBandIndex];
                this.AddBand(workspace.SearchBands, planBand.OffsetMs, planBand.UncertaintyMs, planBand.SourceStartMs, planBand.SourceEndMs);
            }

            plan.Bands.Clear();
            for (int bandIndex = 0; bandIndex < workspace.SearchBands.Count; bandIndex++)
            {
                OffsetBand band = workspace.SearchBands[bandIndex];
                double sourceStartMs = Math.Max(plan.SourceStartMs, band.SourceStartMs);
                double sourceEndMs = Math.Min(plan.SourceEndMs, band.SourceEndMs);
                if (sourceEndMs < sourceStartMs)
                    continue;
                plan.Bands.Add(new OffsetBand
                {
                    OffsetMs = band.OffsetMs,
                    UncertaintyMs = band.UncertaintyMs,
                    SourceStartMs = sourceStartMs,
                    SourceEndMs = sourceEndMs
                });
            }

            double minimumLanguageMs = double.PositiveInfinity;
            double maximumLanguageMs = double.NegativeInfinity;
            for (int bandIndex = 0; bandIndex < plan.Bands.Count; bandIndex++)
            {
                OffsetBand band = plan.Bands[bandIndex];
                minimumLanguageMs = Math.Min(minimumLanguageMs, (band.SourceStartMs - band.OffsetMs - band.UncertaintyMs) * scale);
                maximumLanguageMs = Math.Max(maximumLanguageMs, (band.SourceEndMs - band.OffsetMs + band.UncertaintyMs) * scale);
            }
            plan.LanguageStartMs = Math.Max(0.0, minimumLanguageMs);
            plan.LanguageEndMs = Math.Max(plan.LanguageStartMs, maximumLanguageMs);
        }

        /// <summary>
        /// Propaga alle sole espansioni future i regimi scoperti da una matrice completa,
        /// il cui prefisso ha già valutato ogni associazione temporale possibile
        /// </summary>
        /// <param name="workspace">Workspace con le bande già esplorate</param>
        /// <param name="region">Regione con i regimi risolti</param>
        private void AppendDenseResolvedSearchBands(RegionWorkspace workspace, DeepSiftTemporalCandidateRegion region)
        {
            if (!workspace.HasDenseSearchCoverage)
                return;
            for (int regimeIndex = 0; regimeIndex < region.Regimes.Count; regimeIndex++)
            {
                DeepSiftLocalRegime regime = region.Regimes[regimeIndex];
                bool represented = false;
                for (int bandIndex = 0; bandIndex < workspace.SearchBands.Count; bandIndex++)
                {
                    OffsetBand band = workspace.SearchBands[bandIndex];
                    if (this.IntervalsOverlap(regime.OffsetMs, regime.UncertaintyMs, band.OffsetMs, band.UncertaintyMs))
                    {
                        represented = true;
                        break;
                    }
                }
                if (represented)
                    continue;
                workspace.SearchBands.Add(new OffsetBand
                {
                    OffsetMs = regime.OffsetMs,
                    UncertaintyMs = regime.UncertaintyMs,
                    SourceStartMs = regime.SourceStartPtsMs,
                    SourceEndMs = regime.SourceEndPtsMs
                });
            }
            workspace.SearchBands.Sort((left, right) => left.OffsetMs.CompareTo(right.OffsetMs));
        }

        /// <summary>
        /// Calcola l'incertezza minima delle bande derivate dai supporti della regione
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="region">Regione locale da pianificare</param>
        /// <returns>Incertezza di ricerca in millisecondi</returns>
        private double GetRegionSearchUncertainty(DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalCandidateRegion region)
        {
            double result = 1.0;
            if (region.BeforeSupportRunIndex >= 0)
                result = Math.Max(result, this.GetSupportSearchUncertainty(temporal.SupportRuns[region.BeforeSupportRunIndex]));
            if (region.AfterSupportRunIndex >= 0)
                result = Math.Max(result, this.GetSupportSearchUncertainty(temporal.SupportRuns[region.AfterSupportRunIndex]));
            return result;
        }

        /// <summary>
        /// Aggiunge una banda derivata da un supporto globale
        /// </summary>
        /// <param name="bands">Bande di destinazione</param>
        /// <param name="support">Supporto globale da trasformare</param>
        /// <param name="sourceStartMs">Inizio della copertura source</param>
        /// <param name="sourceEndMs">Fine della copertura source</param>
        private void AddSupportBand(List<OffsetBand> bands, DeepSiftTemporalSupportRun support, double sourceStartMs, double sourceEndMs)
        {
            this.AddBand(bands, support.OffsetMs, this.GetSupportSearchUncertainty(support), sourceStartMs, sourceEndMs);
        }

        /// <summary>
        /// Restituisce l'incertezza minima di ricerca associata a un supporto
        /// </summary>
        /// <param name="support">Supporto globale da valutare</param>
        /// <returns>Incertezza di ricerca in millisecondi</returns>
        private double GetSupportSearchUncertainty(DeepSiftTemporalSupportRun support)
        {
            return Math.Max(1.0, support.UncertaintyMs);
        }

        /// <summary>
        /// Accoda gli offset non ancora presenti entro la precisione temporale locale
        /// </summary>
        /// <param name="target">Lista di destinazione</param>
        /// <param name="source">Offset da aggiungere</param>
        private void AppendDistinctOffsets(List<double> target, IReadOnlyList<double> source)
        {
            for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
            {
                bool exists = false;
                for (int targetIndex = 0; targetIndex < target.Count; targetIndex++)
                    exists |= Math.Abs(target[targetIndex] - source[sourceIndex]) <= 0.001;
                if (!exists)
                    target.Add(source[sourceIndex]);
            }
        }

        /// <summary>
        /// Trova l'ultimo match della catena del supporto non successivo alla slice indicata
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="run">Supporto globale da attraversare</param>
        /// <param name="sliceIndex">Indice della slice di riferimento</param>
        /// <returns>Indice del match di catena selezionato</returns>
        private int FindBeforeSupportChainIndex(DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalSupportRun run, int sliceIndex)
        {
            int result = run.FirstChainIndex;
            for (int chainIndex = run.FirstChainIndex; chainIndex <= run.LastChainIndex; chainIndex++)
            {
                if (temporal.Chain[chainIndex].SliceIndex > sliceIndex)
                    break;
                result = chainIndex;
            }
            return result;
        }

        /// <summary>
        /// Trova il primo match della catena del supporto non precedente alla slice indicata
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="run">Supporto globale da attraversare</param>
        /// <param name="sliceIndex">Indice della slice di riferimento</param>
        /// <returns>Indice del match di catena selezionato</returns>
        private int FindAfterSupportChainIndex(DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalSupportRun run, int sliceIndex)
        {
            for (int chainIndex = run.FirstChainIndex; chainIndex <= run.LastChainIndex; chainIndex++)
            {
                if (temporal.Chain[chainIndex].SliceIndex >= sliceIndex)
                    return chainIndex;
            }
            return run.LastChainIndex;
        }

        /// <summary>
        /// Aggiunge o fonde una banda di offset mantenendo l'ordine temporale
        /// </summary>
        /// <param name="bands">Bande da aggiornare</param>
        /// <param name="offsetMs">Offset centrale della banda</param>
        /// <param name="uncertaintyMs">Incertezza dell'offset in millisecondi</param>
        /// <param name="sourceStartMs">Inizio della copertura source</param>
        /// <param name="sourceEndMs">Fine della copertura source</param>
        private void AddBand(List<OffsetBand> bands, double offsetMs, double uncertaintyMs, double sourceStartMs, double sourceEndMs)
        {
            uncertaintyMs = Math.Max(1.0, uncertaintyMs);
            for (int bandIndex = 0; bandIndex < bands.Count; bandIndex++)
            {
                OffsetBand band = bands[bandIndex];
                bool temporallyAdjacent = sourceStartMs <= band.SourceEndMs && band.SourceStartMs <= sourceEndMs;
                if (temporallyAdjacent && Math.Abs(offsetMs - band.OffsetMs) <= uncertaintyMs + band.UncertaintyMs)
                {
                    double minimumMs = Math.Min(band.OffsetMs - band.UncertaintyMs, offsetMs - uncertaintyMs);
                    double maximumMs = Math.Max(band.OffsetMs + band.UncertaintyMs, offsetMs + uncertaintyMs);
                    band.OffsetMs = (minimumMs + maximumMs) * 0.5;
                    band.UncertaintyMs = (maximumMs - minimumMs) * 0.5;
                    band.SourceStartMs = Math.Min(band.SourceStartMs, sourceStartMs);
                    band.SourceEndMs = Math.Max(band.SourceEndMs, sourceEndMs);
                    return;
                }
            }
            bands.Add(new OffsetBand { OffsetMs = offsetMs, UncertaintyMs = uncertaintyMs, SourceStartMs = sourceStartMs, SourceEndMs = sourceEndMs });
            bands.Sort((left, right) => left.SourceStartMs != right.SourceStartMs ? left.SourceStartMs.CompareTo(right.SourceStartMs) : left.OffsetMs.CompareTo(right.OffsetMs));
        }

        /// <summary>
        /// Recupera una slice dal suo indice logico
        /// </summary>
        /// <param name="slices">Slice temporali ordinate</param>
        /// <param name="sliceIndex">Indice logico richiesto</param>
        /// <returns>Slice corrispondente oppure null</returns>
        private DeepSiftTemporalSliceEvidence FindSlice(IReadOnlyList<DeepSiftTemporalSliceEvidence> slices, int sliceIndex)
        {
            for (int index = 0; index < slices.Count; index++)
            {
                if (slices[index].Index == sliceIndex)
                    return slices[index];
            }
            return null;
        }

        /// <summary>
        /// Verifica se i supporti ai confini richiedono regimi locali distinti
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="region">Regione locale da valutare</param>
        /// <returns>True quando gli intervalli di offset dei supporti sono incompatibili</returns>
        private bool RequiresDistinctRegimes(DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalCandidateRegion region)
        {
            if (region.BeforeSupportRunIndex < 0 || region.AfterSupportRunIndex < 0 || region.BeforeSupportRunIndex == region.AfterSupportRunIndex)
                return false;
            DeepSiftTemporalSupportRun before = temporal.SupportRuns[region.BeforeSupportRunIndex];
            DeepSiftTemporalSupportRun after = temporal.SupportRuns[region.AfterSupportRunIndex];
            return before.MinimumOffsetMs > after.MaximumOffsetMs || after.MinimumOffsetMs > before.MaximumOffsetMs;
        }

        /// <summary>
        /// Riconosce un dropout quando un solo regime locale attraversa la regione ed è compatibile con tutti i supporti disponibili
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="region">Regione locale da verificare</param>
        /// <returns>True quando un solo regime spiega l'intera regione</returns>
        private bool ResolvesAsSingleRegimeAcrossBoundary(DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalCandidateRegion region)
        {
            if (region.Regimes.Count != 1 ||
                (region.BeforeSupportRunIndex < 0 && region.AfterSupportRunIndex < 0))
                return false;
            DeepSiftLocalRegime local = region.Regimes[0];
            DeepSiftTemporalSupportRun before = region.BeforeSupportRunIndex >= 0 ? temporal.SupportRuns[region.BeforeSupportRunIndex] : null;
            DeepSiftTemporalSupportRun after = region.AfterSupportRunIndex >= 0 ? temporal.SupportRuns[region.AfterSupportRunIndex] : null;
            if (before != null && !this.RegimeOffsetIntersectsSupport(local, before))
                return false;
            if (after != null && !this.RegimeOffsetIntersectsSupport(local, after))
                return false;
            if (after == null)
                return true;
            DeepSiftTemporalSliceEvidence firstSlice = this.FindSlice(temporal.Slices, region.FirstSliceIndex);
            DeepSiftTemporalSliceEvidence lastSlice = this.FindSlice(temporal.Slices, region.LastSliceIndex);
            double requiredStartMs = firstSlice != null ? firstSlice.SourceStartPtsMs : region.SourceStartPtsMs;
            double requiredEndMs = lastSlice != null ? lastSlice.SourceEndPtsMs : region.SourceEndPtsMs;
            double uncertaintyMs = local.UncertaintyMs;
            if (before != null)
                uncertaintyMs += before.UncertaintyMs;
            uncertaintyMs += after.UncertaintyMs;
            return local.SourceStartPtsMs <= requiredStartMs + uncertaintyMs &&
                   local.SourceEndPtsMs >= requiredEndMs - uncertaintyMs;
        }

        /// <summary>
        /// Riassume gli offset iniziale e finale di un percorso locale
        /// </summary>
        /// <param name="path">Percorso da riassumere</param>
        /// <returns>Intervallo testuale degli offset oppure il valore localizzato per il percorso vuoto</returns>
        private string SummarizePathOffsets(IReadOnlyList<DeepSiftLocalPathPoint> path)
        {
            if (path.Count == 0)
                return AppText.T("deep.temporal.value.empty");
            return path[0].OffsetMs.ToString("F1", CultureInfo.InvariantCulture) + "→" + path[path.Count - 1].OffsetMs.ToString("F1", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Seleziona le coppie PTS non ancora elaborate nel workspace
        /// </summary>
        /// <param name="workspace">Workspace che conserva le chiavi elaborate</param>
        /// <param name="source">Ancore source</param>
        /// <param name="language">Ancore language</param>
        /// <param name="pairs">Coppie candidate</param>
        /// <returns>Coppie con chiave PTS nuova</returns>
        private List<DeepSiftFramePair> SelectPendingPairs(RegionWorkspace workspace, IReadOnlyList<DeepSiftVisualAnchor> source, IReadOnlyList<DeepSiftVisualAnchor> language, IReadOnlyList<DeepSiftFramePair> pairs)
        {
            List<DeepSiftFramePair> result = new List<DeepSiftFramePair>();
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftFramePair pair = pairs[pairIndex];
                (long SourcePts, long LanguagePts) key = this.GetPairPtsKey(source[pair.SourceAnchorIndex].PtsMs, language[pair.LanguageAnchorIndex].PtsMs);
                if (workspace.ProcessedPairKeys.Add(key))
                    result.Add(pair);
            }
            return result;
        }

        /// <summary>
        /// Accoda nel workspace le coppie accettate da un batch di matching
        /// </summary>
        /// <param name="workspace">Workspace della regione</param>
        /// <param name="match">Risultato del matching da incorporare</param>
        private void AppendAcceptedPairs(RegionWorkspace workspace, LocalMatchResult match)
        {
            workspace.AcceptedPairCount += match.AcceptedPairCount;
            for (int pairIndex = 0; pairIndex < match.AcceptedPairs.Count; pairIndex++)
                workspace.AcceptedPairs.Add(match.AcceptedPairs[pairIndex]);
        }

        /// <summary>
        /// Riassegna indici compatti alle coppie accettate in base ai loro PTS
        /// </summary>
        /// <param name="pairs">Coppie accettate da ricalibrare</param>
        private void RemapAcceptedPairIndexes(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs)
        {
            SortedSet<long> sourcePts = new SortedSet<long>();
            SortedSet<long> languagePts = new SortedSet<long>();
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                sourcePts.Add(this.GetPtsKey(pairs[pairIndex].SourcePtsMs));
                languagePts.Add(this.GetPtsKey(pairs[pairIndex].LanguagePtsMs));
            }
            Dictionary<long, int> sourceIndexes = this.BuildPtsIndexes(sourcePts);
            Dictionary<long, int> languageIndexes = this.BuildPtsIndexes(languagePts);
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                pair.SourceAnchorIndex = sourceIndexes[this.GetPtsKey(pair.SourcePtsMs)];
                pair.LanguageAnchorIndex = languageIndexes[this.GetPtsKey(pair.LanguagePtsMs)];
            }
        }

        /// <summary>
        /// Costruisce un indice progressivo per i valori PTS già quantizzati
        /// </summary>
        /// <param name="ptsValues">Valori PTS da indicizzare</param>
        /// <returns>Mappa dal PTS all'indice compatto</returns>
        private Dictionary<long, int> BuildPtsIndexes(IEnumerable<long> ptsValues)
        {
            Dictionary<long, int> result = new Dictionary<long, int>();
            foreach (long pts in ptsValues)
                result.Add(pts, result.Count);
            return result;
        }

        /// <summary>
        /// Costruisce la chiave PTS di una coppia source-language
        /// </summary>
        /// <param name="sourcePtsMs">PTS source in millisecondi</param>
        /// <param name="languagePtsMs">PTS language in millisecondi</param>
        /// <returns>Coppia di PTS quantizzati</returns>
        private (long SourcePts, long LanguagePts) GetPairPtsKey(double sourcePtsMs, double languagePtsMs)
        {
            return (this.GetPtsKey(sourcePtsMs), this.GetPtsKey(languagePtsMs));
        }

        /// <summary>
        /// Quantizza un PTS in una chiave intera stabile
        /// </summary>
        /// <param name="ptsMs">PTS in millisecondi</param>
        /// <returns>Chiave PTS quantizzata</returns>
        private long GetPtsKey(double ptsMs)
        {
            return (long)Math.Round(ptsMs * 1000.0);
        }

        /// <summary>
        /// Trasforma le coppie accettate del workspace in percorso, regimi, transizioni e lacune locali
        /// </summary>
        /// <param name="workspace">Workspace con le coppie accettate</param>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="region">Regione locale da aggiornare</param>
        /// <param name="scale">Scala temporale source-language</param>
        private void ResolveWorkspacePath(RegionWorkspace workspace, DeepSiftTemporalEvidenceResult temporal, DeepSiftTemporalCandidateRegion region, double scale)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<DeepSiftAcceptedPairDiagnostic> acceptedPairs = workspace.AcceptedPairs.GetCandidates();
            this.RemapAcceptedPairIndexes(acceptedPairs);
            region.AcceptedPairCount = workspace.AcceptedPairCount;
            region.LocalPairEvidence = acceptedPairs;
            List<DeepSiftLocalPathPoint> candidates = this.ClassifyPairs(acceptedPairs, scale, out int strongCount);
            region.StrongPairCount = strongCount;
            region.AmbiguousPairCount = Math.Max(0, region.AcceptedPairCount - strongCount);
            region.Path = this.BuildCanonicalPath(candidates, scale);
            this.CalculatePathCoverage(region.Path, out double sourceCoverageMs, out double languageCoverageMs);
            region.SourceCoverageMs = sourceCoverageMs;
            region.LanguageCoverageMs = languageCoverageMs;
            region.Regimes = this.BuildRegimes(region.Path);
            this.MergeRegimesExplainedBySingleGlobalSupport(temporal, region.Path, region.Regimes);
            this.RemoveUnobservableIntermediateRegimes(temporal, region.Path, region.Regimes, scale);
            this.AssignResolvedModes(region.Path, region.Regimes);
            region.Transitions = this.BuildTransitions(region.Path, region.Regimes);
            this.RefineTransitionCorridors(candidates, region.Regimes, region.Transitions);
            region.Gaps = this.BuildGaps(region.Path, scale);
            region.ResolvedRegimeCount = region.Regimes.Count;
            region.ProducedTransitionCount = region.Transitions.Count;
            region.GapCount = region.Gaps.Count;
            region.PathSolvingMs += stopwatch.ElapsedMilliseconds;
            region.RejectReason = region.Transitions.Count == 0 ? AppText.T("deep.temporal.local.dropoutOrAmbiguousTail") : "";
        }

        /// <summary>
        /// Verifica se il centro dell'offset di un regime ricade nell'intervallo di un supporto
        /// </summary>
        /// <param name="regime">Regime locale da verificare</param>
        /// <param name="support">Supporto globale di riferimento</param>
        /// <returns>True quando il centro del regime è contenuto nel supporto</returns>
        private bool IsRegimeCenterInsideSupport(DeepSiftLocalRegime regime, DeepSiftTemporalSupportRun support)
        {
            return regime.OffsetMs >= support.MinimumOffsetMs && regime.OffsetMs <= support.MaximumOffsetMs;
        }

        /// <summary>
        /// Riunisce tratti locali che restano interamente spiegati dalla stessa support-run globale
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="path">Percorso locale da cui ricostruire i regimi</param>
        /// <param name="regimes">Regimi locali da fondere in place</param>
        private void MergeRegimesExplainedBySingleGlobalSupport(DeepSiftTemporalEvidenceResult temporal, IReadOnlyList<DeepSiftLocalPathPoint> path, List<DeepSiftLocalRegime> regimes)
        {
            for (int regimeIndex = 0; regimeIndex + 1 < regimes.Count;)
            {
                DeepSiftLocalRegime first = regimes[regimeIndex];
                DeepSiftLocalRegime second = regimes[regimeIndex + 1];
                DeepSiftTemporalSupportRun explainingSupport = null;
                for (int supportIndex = 0; supportIndex < temporal.SupportRuns.Count; supportIndex++)
                {
                    DeepSiftTemporalSupportRun support = temporal.SupportRuns[supportIndex];
                    bool overlapsSource = first.SourceStartPtsMs <= support.SourceEndPtsMs + first.UncertaintyMs &&
                                          support.SourceStartPtsMs <= second.SourceEndPtsMs + second.UncertaintyMs;
                    if (overlapsSource && this.IsRegimeCenterInsideSupport(first, support) && this.IsRegimeCenterInsideSupport(second, support))
                    {
                        explainingSupport = support;
                        break;
                    }
                }
                if (explainingSupport == null)
                {
                    regimeIndex++;
                    continue;
                }
                List<int> indexes = new List<int>(first.PathIndexes);
                indexes.AddRange(second.PathIndexes);
                DeepSiftLocalRegime merged = this.BuildRegime(path, indexes);
                regimes[regimeIndex] = merged;
                regimes.RemoveAt(regimeIndex + 1);
            }
        }

        /// <summary>
        /// Elimina isole intermedie che non hanno né continuità full-rate né persistenza globale distinta
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="path">Percorso locale da esaminare</param>
        /// <param name="regimes">Regimi locali da filtrare in place</param>
        /// <param name="scale">Scala temporale source-language</param>
        private void RemoveUnobservableIntermediateRegimes(DeepSiftTemporalEvidenceResult temporal, IReadOnlyList<DeepSiftLocalPathPoint> path, List<DeepSiftLocalRegime> regimes, double scale)
        {
            for (int regimeIndex = regimes.Count - 2; regimeIndex > 0; regimeIndex--)
            {
                DeepSiftLocalRegime regime = regimes[regimeIndex];
                if (this.HasContinuousLocalSupport(path, regime, scale) || this.HasPersistentGlobalSupport(temporal, regime))
                    continue;
                regimes.RemoveAt(regimeIndex);
            }
        }

        /// <summary>
        /// Verifica se un regime contiene una sequenza locale sufficientemente continua
        /// </summary>
        /// <param name="path">Percorso locale da esaminare</param>
        /// <param name="regime">Regime da valutare</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <returns>True quando il regime ha supporto locale continuo sufficiente</returns>
        private bool HasContinuousLocalSupport(IReadOnlyList<DeepSiftLocalPathPoint> path, DeepSiftLocalRegime regime, double scale)
        {
            int continuousCount = 1;
            int maximumContinuousCount = 1;
            for (int indexIndex = 1; indexIndex < regime.PathIndexes.Count; indexIndex++)
            {
                DeepSiftLocalPathPoint previous = path[regime.PathIndexes[indexIndex - 1]];
                DeepSiftLocalPathPoint current = path[regime.PathIndexes[indexIndex]];
                double uncertaintyMs = previous.UncertaintyMs + current.UncertaintyMs;
                bool continuous = current.SourcePtsMs - previous.SourcePtsMs <= uncertaintyMs &&
                                  (current.LanguagePtsMs - previous.LanguagePtsMs) / scale <= uncertaintyMs;
                continuousCount = continuous ? continuousCount + 1 : 1;
                maximumContinuousCount = Math.Max(maximumContinuousCount, continuousCount);
            }
            return maximumContinuousCount >= MINIMUM_DISTINCT_REGIME_SUPPORT;
        }

        /// <summary>
        /// Verifica se un regime è confermato da un supporto globale persistente
        /// </summary>
        /// <param name="temporal">Evidenza temporale globale</param>
        /// <param name="regime">Regime locale da verificare</param>
        /// <returns>True quando esiste un supporto globale distinto e sovrapposto</returns>
        private bool HasPersistentGlobalSupport(DeepSiftTemporalEvidenceResult temporal, DeepSiftLocalRegime regime)
        {
            for (int supportIndex = 0; supportIndex < temporal.SupportRuns.Count; supportIndex++)
            {
                DeepSiftTemporalSupportRun support = temporal.SupportRuns[supportIndex];
                if (support.FirstChainIndex == support.LastChainIndex || !this.RegimeOffsetIntersectsSupport(regime, support))
                    continue;
                bool overlapsSource = regime.SourceStartPtsMs <= support.SourceEndPtsMs + regime.UncertaintyMs &&
                                      support.SourceStartPtsMs <= regime.SourceEndPtsMs + regime.UncertaintyMs;
                if (overlapsSource)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Espande il workspace scegliendo tra matrice densa e matching streaming
        /// </summary>
        /// <param name="sourcePath">Percorso del video source</param>
        /// <param name="languagePath">Percorso del video language</param>
        /// <param name="sourceCropPx">Ritaglio del video source</param>
        /// <param name="languageCropPx">Ritaglio del video language</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="plan">Piano di estrazione corrente</param>
        /// <param name="region">Regione locale da aggiornare</param>
        /// <param name="workspace">Workspace persistente della regione</param>
        /// <param name="cancellationToken">Token per interrompere l'elaborazione</param>
        /// <returns>True quando il workspace contiene frame sufficienti per proseguire</returns>
        private bool ExpandWorkspace(string sourcePath, string languagePath, string sourceCropPx, string languageCropPx, double scale, RegionPlan plan, DeepSiftTemporalCandidateRegion region, RegionWorkspace workspace, CancellationToken cancellationToken)
        {
            region.WorkspaceFailure = DeepSiftLocalWorkspaceFailure.None;
            if (!this.ShouldUseDenseMatrix(sourcePath, languagePath, plan))
                return this.ExpandWorkspaceStreaming(sourcePath, languagePath, sourceCropPx, languageCropPx, scale, plan, region, workspace, cancellationToken);
            workspace.HasDenseSearchCoverage = true;

            if (!this.ExpandTimeline(sourcePath, sourceCropPx, plan.SourceStartMs, plan.SourceEndMs, this._sourceFrameExtractor, workspace.SourceAnchors, workspace.SourceRanges, region, true, cancellationToken) ||
                !this.ExpandTimeline(languagePath, languageCropPx, plan.LanguageStartMs, plan.LanguageEndMs, this._languageFrameExtractor, workspace.LanguageAnchors, workspace.LanguageRanges, region, false, cancellationToken))
            {
                region.WorkspaceFailure = DeepSiftLocalWorkspaceFailure.ExtractionFailed;
                region.RejectReason = AppText.T("deep.temporal.local.frameExtractionFailed");
                return false;
            }
            this.RecordAnchorKeys(workspace.SourceFrameKeys, workspace.SourceAnchors);
            this.RecordAnchorKeys(workspace.LanguageFrameKeys, workspace.LanguageAnchors);
            if (workspace.SourceAnchors.Count < 4 || workspace.LanguageAnchors.Count < 4)
            {
                region.WorkspaceFailure = DeepSiftLocalWorkspaceFailure.InsufficientFrames;
                region.RejectReason = AppText.T("deep.temporal.local.insufficientAdaptiveFrames");
                return false;
            }

            List<DeepSiftFramePair> pairs = this.BuildPairs(workspace.SourceAnchors, workspace.LanguageAnchors, scale, plan.Bands, true);
            List<DeepSiftFramePair> pendingPairs = this.SelectPendingPairs(workspace, workspace.SourceAnchors, workspace.LanguageAnchors, pairs);
            if (pairs.Count == 0)
            {
                region.WorkspaceFailure = DeepSiftLocalWorkspaceFailure.EmptyPairMatrix;
                region.RejectReason = AppText.T("deep.temporal.local.emptyPairMatrix");
                return false;
            }
            if (pendingPairs.Count > 0)
            {
                LocalMatchResult match = this.MatchTiles(workspace.SourceAnchors, workspace.LanguageAnchors, pendingPairs, region.SourceBlackRuns, region.LanguageBlackRuns, scale, cancellationToken);
                region.MatchingMs += match.MatchingMs;
                if (!string.IsNullOrEmpty(match.RejectReason))
                {
                    region.WorkspaceFailure = DeepSiftLocalWorkspaceFailure.MatchingFailed;
                    region.RejectReason = match.RejectReason;
                    return false;
                }
                this.AppendAcceptedPairs(workspace, match);
            }

            region.SourceFrameCount = workspace.SourceFrameKeys.Count;
            region.LanguageFrameCount = workspace.LanguageFrameKeys.Count;
            region.ExtractionMs = region.SourceExtractionMs + region.LanguageExtractionMs;
            return true;
        }

        /// <summary>
        /// Processa una regione grande a stripe senza conservare i frame full-rate nel workspace
        /// </summary>
        /// <param name="sourcePath">Percorso del video source</param>
        /// <param name="languagePath">Percorso del video language</param>
        /// <param name="sourceCropPx">Ritaglio del video source</param>
        /// <param name="languageCropPx">Ritaglio del video language</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="plan">Piano di estrazione corrente</param>
        /// <param name="region">Regione locale da aggiornare</param>
        /// <param name="workspace">Workspace persistente della regione</param>
        /// <param name="cancellationToken">Token per interrompere l'elaborazione</param>
        /// <returns>True quando sono stati raccolti frame sufficienti su entrambe le timeline</returns>
        private bool ExpandWorkspaceStreaming(string sourcePath, string languagePath, string sourceCropPx, string languageCropPx, double scale, RegionPlan plan, DeepSiftTemporalCandidateRegion region, RegionWorkspace workspace, CancellationToken cancellationToken)
        {
            this._videoInfoReader.TryRead(sourcePath, out _, out double sourceFps);
            double stripeDurationMs = SOURCE_TILE_SIZE * STREAMING_SOURCE_TILE_COUNT * 1000.0 / Math.Max(1.0, sourceFps);
            List<ExtractedRange> missingRanges = this.FindMissingRanges(workspace.SourceRanges, plan.SourceStartMs, plan.SourceEndMs);
            for (int rangeIndex = 0; rangeIndex < missingRanges.Count; rangeIndex++)
            {
                ExtractedRange range = missingRanges[rangeIndex];
                for (double stripeStartMs = range.StartMs; stripeStartMs < range.EndMs; stripeStartMs += stripeDurationMs)
                {
                    List<byte[]> sourceFrames = null;
                    List<DeepSiftVisualAnchor> sourceAnchors = null;
                    List<DeepSiftVisualAnchor> languageAnchors = new List<DeepSiftVisualAnchor>();
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        double stripeEndMs = Math.Min(range.EndMs, stripeStartMs + stripeDurationMs);
                        int sourceExtractionStartMs = (int)Math.Floor(stripeStartMs);
                        double sourceDurationSec = Math.Max(0.1, (stripeEndMs - sourceExtractionStartMs) / 1000.0);
                        bool sourceCrop = this._geometryCropResolver(sourcePath);
                        Stopwatch sourceStopwatch = Stopwatch.StartNew();
                        if (!this._sourceFrameExtractor.TryExtractSegment(sourcePath, sourceExtractionStartMs, sourceDurationSec, 0.0, sourceCrop, sourceCropPx, out sourceFrames, out double[] sourceTimestampsMs))
                        {
                            region.WorkspaceFailure = DeepSiftLocalWorkspaceFailure.ExtractionFailed;
                            region.RejectReason = AppText.T("deep.temporal.local.frameExtractionFailed");
                            return false;
                        }
                        this._frameNormalizer(sourcePath, sourceCrop, sourceCropPx, sourceFrames);
                        region.SourceExtractionMs += sourceStopwatch.ElapsedMilliseconds;
                        if (sourceFrames.Count == 0)
                            continue;
                        workspace.SourceRanges.Add(new ExtractedRange { StartMs = stripeStartMs, EndMs = stripeEndMs });
                        sourceAnchors = this.SelectNewSourceAnchors(this.BuildAnchors(sourceFrames, sourceTimestampsMs), workspace.SourceFrameKeys);
                        if (sourceAnchors.Count == 0)
                            continue;

                        List<ExtractedRange> languageRanges = this.BuildLanguageExtractionRanges(sourceAnchors, plan, scale);
                        for (int languageRangeIndex = 0; languageRangeIndex < languageRanges.Count; languageRangeIndex++)
                        {
                            ExtractedRange languageRange = languageRanges[languageRangeIndex];
                            List<byte[]> languageFrames = null;
                            List<DeepSiftVisualAnchor> extractedLanguageAnchors = null;
                            try
                            {
                                int languageExtractionStartMs = (int)Math.Floor(languageRange.StartMs);
                                double languageDurationSec = Math.Max(0.1, (languageRange.EndMs - languageExtractionStartMs) / 1000.0);
                                bool languageCrop = this._geometryCropResolver(languagePath);
                                Stopwatch languageStopwatch = Stopwatch.StartNew();
                                if (!this._languageFrameExtractor.TryExtractSegment(languagePath, languageExtractionStartMs, languageDurationSec, 0.0, languageCrop, languageCropPx, out languageFrames, out double[] languageTimestampsMs))
                                {
                                    region.WorkspaceFailure = DeepSiftLocalWorkspaceFailure.ExtractionFailed;
                                    region.RejectReason = AppText.T("deep.temporal.local.frameExtractionFailed");
                                    return false;
                                }
                                this._frameNormalizer(languagePath, languageCrop, languageCropPx, languageFrames);
                                region.LanguageExtractionMs += languageStopwatch.ElapsedMilliseconds;
                                extractedLanguageAnchors = this.BuildAnchors(languageFrames, languageTimestampsMs);
                                this.MergeAnchors(languageAnchors, extractedLanguageAnchors);
                            }
                            finally
                            {
                                if (extractedLanguageAnchors != null)
                                {
                                    for (int anchorIndex = 0; anchorIndex < extractedLanguageAnchors.Count; anchorIndex++)
                                    {
                                        if (!languageAnchors.Contains(extractedLanguageAnchors[anchorIndex]))
                                            extractedLanguageAnchors[anchorIndex].Frame = Array.Empty<byte>();
                                    }
                                }
                                languageFrames?.Clear();
                            }
                        }
                        if (languageAnchors.Count == 0)
                            continue;
                        this.RecordAnchorKeys(workspace.LanguageFrameKeys, languageAnchors);

                        List<DeepSiftFramePair> pairs = this.BuildPairs(sourceAnchors, languageAnchors, scale, plan.Bands, false);
                        if (pairs.Count == 0)
                            continue;
                        // L'ambito resta aperto per l'intera regione: i backend che possiedono una cache
                        // riusano le feature PTS già incontrate durante espansioni e stripe adiacenti
                        LocalMatchResult match = this.MatchTiles(sourceAnchors, languageAnchors, pairs, region.SourceBlackRuns, region.LanguageBlackRuns, scale, cancellationToken);
                        region.MatchingMs += match.MatchingMs;
                        if (!string.IsNullOrEmpty(match.RejectReason))
                        {
                            region.WorkspaceFailure = DeepSiftLocalWorkspaceFailure.MatchingFailed;
                            region.RejectReason = match.RejectReason;
                            return false;
                        }
                        this.AppendAcceptedPairs(workspace, match);
                    }
                    finally
                    {
                        DeepSiftVisualAnchorBufferHelper.ReleaseFrames(sourceAnchors);
                        DeepSiftVisualAnchorBufferHelper.ReleaseFrames(languageAnchors);
                        sourceFrames?.Clear();
                    }
                }
            }
            this.MergeRanges(workspace.SourceRanges);
            region.SourceFrameCount = workspace.SourceFrameKeys.Count;
            region.LanguageFrameCount = workspace.LanguageFrameKeys.Count;
            region.ExtractionMs = region.SourceExtractionMs + region.LanguageExtractionMs;
            if (workspace.SourceFrameKeys.Count >= 4 && workspace.LanguageFrameKeys.Count >= 4)
                return true;
            region.WorkspaceFailure = DeepSiftLocalWorkspaceFailure.InsufficientFrames;
            region.RejectReason = AppText.T("deep.temporal.local.insufficientStreamingFrames");
            return false;
        }

        /// <summary>
        /// Costruisce gli intervalli language necessari a coprire le ancore source e le bande attive
        /// </summary>
        /// <param name="sourceAnchors">Ancore source della stripe corrente</param>
        /// <param name="plan">Piano con bande e limiti language</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <returns>Intervalli language da estrarre senza sovrapposizioni</returns>
        private List<ExtractedRange> BuildLanguageExtractionRanges(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, RegionPlan plan, double scale)
        {
            List<ExtractedRange> result = new List<ExtractedRange>();
            if (sourceAnchors.Count == 0)
                return result;
            double maximumFrameDurationMs = 1.0;
            for (int sourceIndex = 0; sourceIndex < sourceAnchors.Count; sourceIndex++)
                maximumFrameDurationMs = Math.Max(maximumFrameDurationMs, sourceAnchors[sourceIndex].FrameDurationMs);
            double sourceStartMs = sourceAnchors[0].PtsMs;
            double sourceEndMs = sourceAnchors[sourceAnchors.Count - 1].PtsMs;
            for (int bandIndex = 0; bandIndex < plan.Bands.Count; bandIndex++)
            {
                OffsetBand band = plan.Bands[bandIndex];
                double activeSourceStartMs = Math.Max(sourceStartMs, band.SourceStartMs);
                double activeSourceEndMs = Math.Min(sourceEndMs, band.SourceEndMs);
                if (activeSourceEndMs < activeSourceStartMs)
                    continue;
                double languageStartMs = (activeSourceStartMs - band.OffsetMs - band.UncertaintyMs - maximumFrameDurationMs) * scale;
                double languageEndMs = (activeSourceEndMs - band.OffsetMs + band.UncertaintyMs + maximumFrameDurationMs) * scale;
                languageStartMs = Math.Max(plan.LanguageStartMs, languageStartMs);
                languageEndMs = Math.Min(plan.LanguageEndMs, languageEndMs);
                if (languageEndMs > languageStartMs)
                    result.Add(new ExtractedRange { StartMs = languageStartMs, EndMs = languageEndMs });
            }
            this.MergeRanges(result);
            return result;
        }

        /// <summary>
        /// Determina se la regione può essere elaborata mantenendo una matrice densa in memoria
        /// </summary>
        /// <param name="sourcePath">Percorso del video source</param>
        /// <param name="languagePath">Percorso del video language</param>
        /// <param name="plan">Piano con gli intervalli da elaborare</param>
        /// <returns>True quando conteggi e prodotto delle coppie rientrano nei limiti densi</returns>
        private bool ShouldUseDenseMatrix(string sourcePath, string languagePath, RegionPlan plan)
        {
            this._videoInfoReader.TryRead(sourcePath, out _, out double sourceFps);
            this._videoInfoReader.TryRead(languagePath, out _, out double languageFps);
            double sourceFrameCount = Math.Ceiling(Math.Max(0.0, plan.SourceEndMs - plan.SourceStartMs) * Math.Max(1.0, sourceFps) / 1000.0);
            double languageFrameCount = Math.Ceiling(Math.Max(0.0, plan.LanguageEndMs - plan.LanguageStartMs) * Math.Max(1.0, languageFps) / 1000.0);
            double maximumResidentFrameCount = SOURCE_TILE_SIZE * STREAMING_SOURCE_TILE_COUNT;
            return sourceFrameCount <= maximumResidentFrameCount && languageFrameCount <= maximumResidentFrameCount && sourceFrameCount * languageFrameCount <= MAXIMUM_DENSE_PAIR_COUNT;
        }

        /// <summary>
        /// Registra le chiavi PTS delle ancore già presenti nel workspace
        /// </summary>
        /// <param name="keys">Insieme di chiavi da aggiornare</param>
        /// <param name="anchors">Ancore da indicizzare</param>
        private void RecordAnchorKeys(HashSet<long> keys, IReadOnlyList<DeepSiftVisualAnchor> anchors)
        {
            for (int anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
                keys.Add(this.GetPtsKey(anchors[anchorIndex].PtsMs));
        }

        /// <summary>
        /// Seleziona una sola volta ogni PTS source, rendendo disgiunte le stripe e le loro coppie
        /// </summary>
        /// <param name="anchors">Ancore estratte nella stripe corrente</param>
        /// <param name="knownKeys">PTS source già elaborati nelle stripe precedenti</param>
        /// <returns>Ancore con PTS nuovi nell'ordine originale</returns>
        private List<DeepSiftVisualAnchor> SelectNewSourceAnchors(IReadOnlyList<DeepSiftVisualAnchor> anchors, HashSet<long> knownKeys)
        {
            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>();
            for (int anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
            {
                DeepSiftVisualAnchor anchor = anchors[anchorIndex];
                if (!knownKeys.Add(this.GetPtsKey(anchor.PtsMs)))
                    continue;
                anchor.Index = result.Count;
                anchor.FrameIndex = result.Count;
                result.Add(anchor);
            }
            return result;
        }

        /// <summary>
        /// Estrae e aggiunge le porzioni mancanti di una timeline nel workspace
        /// </summary>
        /// <param name="path">Percorso della timeline da estrarre</param>
        /// <param name="cropPx">Ritaglio della timeline</param>
        /// <param name="startMs">Inizio dell'intervallo richiesto</param>
        /// <param name="endMs">Fine dell'intervallo richiesto</param>
        /// <param name="extractor">Estrattore associato alla timeline</param>
        /// <param name="anchors">Ancore di destinazione</param>
        /// <param name="ranges">Intervalli già estratti da aggiornare</param>
        /// <param name="region">Regione locale da aggiornare con i tempi di estrazione</param>
        /// <param name="source">True quando la timeline è source</param>
        /// <param name="cancellationToken">Token per interrompere l'estrazione</param>
        /// <returns>True quando tutte le porzioni richieste sono state estratte</returns>
        private bool ExpandTimeline(string path, string cropPx, double startMs, double endMs, FrameExtractionService extractor, List<DeepSiftVisualAnchor> anchors, List<ExtractedRange> ranges, DeepSiftTemporalCandidateRegion region, bool source, CancellationToken cancellationToken)
        {
            List<ExtractedRange> missing = this.FindMissingRanges(ranges, startMs, endMs);
            for (int rangeIndex = 0; rangeIndex < missing.Count; rangeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExtractedRange range = missing[rangeIndex];
                Stopwatch stopwatch = Stopwatch.StartNew();
                bool crop = this._geometryCropResolver(path);
                int extractionStartMs = (int)Math.Floor(range.StartMs);
                double durationSec = Math.Max(0.1, (range.EndMs - extractionStartMs) / 1000.0);
                if (!extractor.TryExtractSegment(path, extractionStartMs, durationSec, 0.0, crop, cropPx, out List<byte[]> frames, out double[] timestampsMs))
                    return false;
                this._frameNormalizer(path, crop, cropPx, frames);
                this.MergeAnchors(anchors, this.BuildAnchors(frames, timestampsMs));
                ranges.Add(range);
                if (source)
                    region.SourceExtractionMs += stopwatch.ElapsedMilliseconds;
                else
                    region.LanguageExtractionMs += stopwatch.ElapsedMilliseconds;
            }
            this.MergeRanges(ranges);
            return true;
        }

        /// <summary>
        /// Calcola le porzioni dell'intervallo richiesto non ancora presenti negli intervalli estratti
        /// </summary>
        /// <param name="ranges">Intervalli già estratti e ordinati logicamente</param>
        /// <param name="startMs">Inizio dell'intervallo richiesto</param>
        /// <param name="endMs">Fine dell'intervallo richiesto</param>
        /// <returns>Intervalli mancanti non sovrapposti</returns>
        private List<ExtractedRange> FindMissingRanges(IReadOnlyList<ExtractedRange> ranges, double startMs, double endMs)
        {
            List<ExtractedRange> result = new List<ExtractedRange>();
            double cursorMs = startMs;
            for (int rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
            {
                ExtractedRange range = ranges[rangeIndex];
                if (range.EndMs <= cursorMs || range.StartMs >= endMs)
                    continue;
                if (range.StartMs > cursorMs)
                    result.Add(new ExtractedRange { StartMs = cursorMs, EndMs = Math.Min(endMs, range.StartMs) });
                cursorMs = Math.Max(cursorMs, range.EndMs);
                if (cursorMs >= endMs)
                    break;
            }
            if (cursorMs < endMs)
                result.Add(new ExtractedRange { StartMs = cursorMs, EndMs = endMs });
            return result;
        }

        /// <summary>
        /// Fonde gli intervalli estratti sovrapposti o contigui
        /// </summary>
        /// <param name="ranges">Intervalli da ordinare e fondere</param>
        private void MergeRanges(List<ExtractedRange> ranges)
        {
            ranges.Sort((left, right) => left.StartMs.CompareTo(right.StartMs));
            for (int rangeIndex = ranges.Count - 2; rangeIndex >= 0; rangeIndex--)
            {
                if (ranges[rangeIndex + 1].StartMs > ranges[rangeIndex].EndMs)
                    continue;
                ranges[rangeIndex].EndMs = Math.Max(ranges[rangeIndex].EndMs, ranges[rangeIndex + 1].EndMs);
                ranges.RemoveAt(rangeIndex + 1);
            }
        }

        /// <summary>
        /// Fonde ancore con PTS distinti e riassegna gli indici ordinati
        /// </summary>
        /// <param name="destination">Ancore persistenti da aggiornare</param>
        /// <param name="anchors">Ancore estratte da incorporare</param>
        private void MergeAnchors(List<DeepSiftVisualAnchor> destination, IReadOnlyList<DeepSiftVisualAnchor> anchors)
        {
            HashSet<long> keys = new HashSet<long>();
            for (int anchorIndex = 0; anchorIndex < destination.Count; anchorIndex++)
                keys.Add(this.GetPtsKey(destination[anchorIndex].PtsMs));
            for (int anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
            {
                if (keys.Add(this.GetPtsKey(anchors[anchorIndex].PtsMs)))
                    destination.Add(anchors[anchorIndex]);
            }
            destination.Sort((left, right) => left.PtsMs.CompareTo(right.PtsMs));
            for (int anchorIndex = 0; anchorIndex < destination.Count; anchorIndex++)
            {
                destination[anchorIndex].Index = anchorIndex;
                destination[anchorIndex].FrameIndex = anchorIndex;
            }
        }

        /// <summary>
        /// Costruisce le ancore visuali associando frame, timestamp e dimensioni video
        /// </summary>
        /// <param name="frames">Frame estratti dalla timeline</param>
        /// <param name="timestampsMs">Timestamp dei frame in millisecondi</param>
        /// <returns>Ancore visuali nell'ordine dei timestamp</returns>
        private List<DeepSiftVisualAnchor> BuildAnchors(List<byte[]> frames, double[] timestampsMs)
        {
            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>(frames.Count);
            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                double durationMs = frameIndex + 1 < timestampsMs.Length ? timestampsMs[frameIndex + 1] - timestampsMs[frameIndex] : frameIndex > 0 ? timestampsMs[frameIndex] - timestampsMs[frameIndex - 1] : 40.0;
                DeepSiftVisualAnchor anchor = new DeepSiftVisualAnchor();
                anchor.Index = frameIndex;
                anchor.FrameIndex = frameIndex;
                anchor.PtsMs = timestampsMs[frameIndex];
                anchor.DurationMs = durationMs > 0.0 ? durationMs : 40.0;
                anchor.FrameDurationMs = anchor.DurationMs;
                anchor.Frame = frames[frameIndex];
                anchor.Width = this._videoSyncConfig.FrameWidth;
                anchor.Height = this._videoSyncConfig.FrameHeight;
                result.Add(anchor);
            }
            return result;
        }

        /// <summary>
        /// Costruisce la matrice densa o sparsa delle coppie ammesse dalle bande di offset
        /// </summary>
        /// <param name="source">Ancore source</param>
        /// <param name="language">Ancore language</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="bands">Bande di offset ammesse</param>
        /// <param name="useDenseMatrix">True per considerare tutte le coppie</param>
        /// <returns>Coppie di ancore da sottoporre al matcher</returns>
        private List<DeepSiftFramePair> BuildPairs(IReadOnlyList<DeepSiftVisualAnchor> source, IReadOnlyList<DeepSiftVisualAnchor> language, double scale, IReadOnlyList<OffsetBand> bands, bool useDenseMatrix)
        {
            long denseCount = (long)source.Count * language.Count;
            if (useDenseMatrix && denseCount <= MAXIMUM_DENSE_PAIR_COUNT)
            {
                List<DeepSiftFramePair> dense = new List<DeepSiftFramePair>((int)denseCount);
                for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
                {
                    for (int languageIndex = 0; languageIndex < language.Count; languageIndex++)
                        dense.Add(new DeepSiftFramePair { SourceAnchorIndex = sourceIndex, LanguageAnchorIndex = languageIndex });
                }
                return dense;
            }

            List<DeepSiftFramePair> result = new List<DeepSiftFramePair>();
            HashSet<long> keys = new HashSet<long>();
            for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
            {
                for (int bandIndex = 0; bandIndex < bands.Count; bandIndex++)
                {
                    OffsetBand band = bands[bandIndex];
                    if (source[sourceIndex].PtsMs < band.SourceStartMs || source[sourceIndex].PtsMs > band.SourceEndMs)
                        continue;
                    double frameUncertaintyMs = source[sourceIndex].FrameDurationMs;
                    double minimumPtsMs = (source[sourceIndex].PtsMs - band.OffsetMs - band.UncertaintyMs - frameUncertaintyMs) * scale;
                    double maximumPtsMs = (source[sourceIndex].PtsMs - band.OffsetMs + band.UncertaintyMs + frameUncertaintyMs) * scale;
                    int languageIndex = this.FindFirstAtOrAfter(language, minimumPtsMs);
                    while (languageIndex < language.Count && language[languageIndex].PtsMs <= maximumPtsMs)
                    {
                        long key = ((long)sourceIndex << 32) | (uint)languageIndex;
                        if (keys.Add(key))
                            result.Add(new DeepSiftFramePair { SourceAnchorIndex = sourceIndex, LanguageAnchorIndex = languageIndex });
                        languageIndex++;
                    }
                }
            }
            result.Sort((left, right) => left.SourceAnchorIndex != right.SourceAnchorIndex ? left.SourceAnchorIndex.CompareTo(right.SourceAnchorIndex) : left.LanguageAnchorIndex.CompareTo(right.LanguageAnchorIndex));
            return result;
        }

        /// <summary>
        /// Cerca con ricerca binaria la prima ancora non precedente al PTS richiesto
        /// </summary>
        /// <param name="anchors">Ancore ordinate per PTS</param>
        /// <param name="ptsMs">PTS limite in millisecondi</param>
        /// <returns>Indice della prima ancora compatibile</returns>
        private int FindFirstAtOrAfter(IReadOnlyList<DeepSiftVisualAnchor> anchors, double ptsMs)
        {
            int minimum = 0;
            int maximum = anchors.Count;
            while (minimum < maximum)
            {
                int middle = minimum + ((maximum - minimum) / 2);
                if (anchors[middle].PtsMs < ptsMs)
                    minimum = middle + 1;
                else
                    maximum = middle;
            }
            return minimum;
        }

        #endregion

        #region Percorso locale

        /// <summary>
        /// Classifica ogni coppia rispetto alle alternative reciproche sugli assi source e language
        /// </summary>
        /// <param name="pairs">Coppie accettate dal matcher</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="strongCount">Numero di coppie classificate come forti</param>
        /// <returns>Punti locali classificati e ordinati</returns>
        private List<DeepSiftLocalPathPoint> ClassifyPairs(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, double scale, out int strongCount)
        {
            Dictionary<int, List<DeepSiftAcceptedPairDiagnostic>> bySource = new Dictionary<int, List<DeepSiftAcceptedPairDiagnostic>>();
            Dictionary<int, List<DeepSiftAcceptedPairDiagnostic>> byLanguage = new Dictionary<int, List<DeepSiftAcceptedPairDiagnostic>>();
            HashSet<(long SourcePts, long LanguagePts)> manyToManyPairs = DeepSiftTemporalAmbiguityDetector.FindManyToManyPairs(pairs, this._temporalEvidenceOptions.MinimumScoreMargin);
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                this.AddPair(bySource, pairs[pairIndex].SourceAnchorIndex, pairs[pairIndex]);
                this.AddPair(byLanguage, pairs[pairIndex].LanguageAnchorIndex, pairs[pairIndex]);
            }
            List<DeepSiftLocalPathPoint> result = new List<DeepSiftLocalPathPoint>();
            strongCount = 0;
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                double offsetMs = pair.SourcePtsMs - (pair.LanguagePtsMs / scale);
                double uncertaintyMs = DeepSiftTemporalMetricComparer.GetPairUncertaintyMs(pair, scale);
                bool sourceBest = this.IsUniqueTemporalBest(pair, bySource[pair.SourceAnchorIndex], scale, uncertaintyMs);
                bool languageBest = this.IsUniqueTemporalBest(pair, byLanguage[pair.LanguageAnchorIndex], scale, uncertaintyMs);
                bool manyToMany = manyToManyPairs.Contains(this.GetPairPtsKey(pair.SourcePtsMs, pair.LanguagePtsMs));
                DeepSiftLocalPathPoint point = new DeepSiftLocalPathPoint();
                point.SourceAnchorIndex = pair.SourceAnchorIndex;
                point.LanguageAnchorIndex = pair.LanguageAnchorIndex;
                point.ModeIndex = -1;
                point.SourcePtsMs = pair.SourcePtsMs;
                point.LanguagePtsMs = pair.LanguagePtsMs;
                point.OffsetMs = offsetMs;
                point.UncertaintyMs = uncertaintyMs;
                point.Score = pair.Score;
                point.InlierCount = pair.InlierCount;
                point.InlierRatio = pair.InlierRatio;
                point.SourceCoverage = pair.SourceCoverage;
                point.LanguageCoverage = pair.LanguageCoverage;
                point.MeanReprojectionError = pair.MeanReprojectionError;
                point.DistinctSupportCount = 1;
                point.Classification = sourceBest && languageBest && !manyToMany ? DeepSiftTemporalPairClassification.Strong : DeepSiftTemporalPairClassification.Ambiguous;
                if (point.Classification == DeepSiftTemporalPairClassification.Strong)
                    strongCount++;
                result.Add(point);
            }
            result.Sort(this.ComparePoints);
            return result;
        }

        /// <summary>
        /// Inserisce una coppia nell'indice delle alternative per ancora
        /// </summary>
        /// <param name="index">Indice da aggiornare</param>
        /// <param name="key">Indice dell'ancora da usare come chiave</param>
        /// <param name="pair">Coppia da aggiungere</param>
        private void AddPair(Dictionary<int, List<DeepSiftAcceptedPairDiagnostic>> index, int key, DeepSiftAcceptedPairDiagnostic pair)
        {
            if (!index.TryGetValue(key, out List<DeepSiftAcceptedPairDiagnostic> values))
            {
                values = new List<DeepSiftAcceptedPairDiagnostic>();
                index.Add(key, values);
            }
            values.Add(pair);
        }

        /// <summary>
        /// Verifica se una coppia è la migliore alternativa temporale per un asse
        /// </summary>
        /// <param name="candidate">Coppia candidata</param>
        /// <param name="alternatives">Alternative sulla stessa ancora</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="candidateUncertaintyMs">Incertezza della coppia candidata</param>
        /// <returns>True quando nessuna alternativa distinta ha confidenza superiore</returns>
        private bool IsUniqueTemporalBest(DeepSiftAcceptedPairDiagnostic candidate, IReadOnlyList<DeepSiftAcceptedPairDiagnostic> alternatives, double scale, double candidateUncertaintyMs)
        {
            double candidateOffsetMs = candidate.SourcePtsMs - (candidate.LanguagePtsMs / scale);
            for (int index = 0; index < alternatives.Count; index++)
            {
                DeepSiftAcceptedPairDiagnostic alternative = alternatives[index];
                if (ReferenceEquals(candidate, alternative))
                    continue;
                double alternativeOffsetMs = alternative.SourcePtsMs - (alternative.LanguagePtsMs / scale);
                double uncertaintyMs = DeepSiftTemporalMetricComparer.GetPairUncertaintyMs(alternative, scale);
                if (Math.Abs(candidateOffsetMs - alternativeOffsetMs) <= candidateUncertaintyMs + uncertaintyMs)
                    continue;
                if (!DeepSiftTemporalMetricComparer.HasHigherConfidence(candidate, alternative, this._temporalEvidenceOptions.MinimumScoreMargin))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Ordina i punti locali per PTS e applica spareggi deterministici sulla qualità
        /// </summary>
        /// <param name="left">Primo punto da confrontare</param>
        /// <param name="right">Secondo punto da confrontare</param>
        /// <returns>Risultato del confronto ordinato</returns>
        private int ComparePoints(DeepSiftLocalPathPoint left, DeepSiftLocalPathPoint right)
        {
            int comparison = left.SourcePtsMs.CompareTo(right.SourcePtsMs);
            if (comparison != 0)
                return comparison;
            comparison = left.LanguagePtsMs.CompareTo(right.LanguagePtsMs);
            if (comparison != 0)
                return comparison;
            comparison = left.SourceAnchorIndex.CompareTo(right.SourceAnchorIndex);
            if (comparison != 0)
                return comparison;
            comparison = left.LanguageAnchorIndex.CompareTo(right.LanguageAnchorIndex);
            return comparison != 0 ? comparison : DeepSiftTemporalMetricComparer.QuantizeMetric(right.Score).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMetric(left.Score));
        }

        /// <summary>
        /// Costruisce il percorso monotono forte con spareggi deterministici
        /// </summary>
        /// <param name="candidates">Punti candidati classificati</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <returns>Percorso monotono composto dai punti forti</returns>
        private List<DeepSiftLocalPathPoint> BuildCanonicalPath(IReadOnlyList<DeepSiftLocalPathPoint> candidates, double scale)
        {
            List<DeepSiftLocalPathPoint> points = new List<DeepSiftLocalPathPoint>();
            for (int index = 0; index < candidates.Count; index++)
            {
                if (candidates[index].Classification == DeepSiftTemporalPairClassification.Strong)
                    points.Add(candidates[index]);
            }
            if (points.Count < 2)
                return points;

            PathState[] states = new PathState[points.Count];
            int bestIndex = 0;
            for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                states[pointIndex] = new PathState(points[pointIndex]);
                for (int previousIndex = 0; previousIndex < pointIndex; previousIndex++)
                {
                    if (points[previousIndex].SourcePtsMs >= points[pointIndex].SourcePtsMs || points[previousIndex].LanguagePtsMs >= points[pointIndex].LanguagePtsMs)
                        continue;
                    PathState candidate = states[previousIndex].Append(points[previousIndex], points[pointIndex], previousIndex, scale);
                    if (candidate.IsBetterThan(states[pointIndex]))
                        states[pointIndex] = candidate;
                }
                if (states[pointIndex].IsBetterThan(states[bestIndex]))
                    bestIndex = pointIndex;
            }
            List<DeepSiftLocalPathPoint> result = new List<DeepSiftLocalPathPoint>(states[bestIndex].MatchCount);
            while (bestIndex >= 0)
            {
                result.Add(points[bestIndex]);
                bestIndex = states[bestIndex].PreviousIndex;
            }
            result.Reverse();
            return result;
        }

        /// <summary>
        /// Segmenta il percorso forte in regimi con intervalli di offset incompatibili
        /// </summary>
        /// <param name="path">Percorso forte da segmentare</param>
        /// <returns>Regimi locali osservabili e non sovrapposti</returns>
        private List<DeepSiftLocalRegime> BuildRegimes(List<DeepSiftLocalPathPoint> path)
        {
            List<DeepSiftLocalRegime> raw = new List<DeepSiftLocalRegime>();
            if (path.Count == 0)
                return raw;
            int startIndex = 0;
            for (int pathIndex = 1; pathIndex <= path.Count; pathIndex++)
            {
                if (pathIndex < path.Count && this.IsCompatible(path, startIndex, pathIndex))
                    continue;
                raw.Add(this.BuildRegime(path, startIndex, pathIndex - 1));
                startIndex = pathIndex;
            }
            List<DeepSiftLocalRegime> result = new List<DeepSiftLocalRegime>();
            for (int rawIndex = 0; rawIndex < raw.Count; rawIndex++)
            {
                DeepSiftLocalRegime regime = raw[rawIndex];
                if (regime.MatchCount < MINIMUM_DISTINCT_REGIME_SUPPORT)
                    continue;
                if (result.Count > 0 && this.IntervalsOverlap(result[result.Count - 1], regime))
                {
                    List<int> mergedIndexes = new List<int>(result[result.Count - 1].PathIndexes);
                    mergedIndexes.AddRange(regime.PathIndexes);
                    result[result.Count - 1] = this.BuildRegime(path, mergedIndexes);
                    continue;
                }
                result.Add(regime);
            }
            return result;
        }

        /// <summary>
        /// Verifica se gli offset di un tratto condividono un intervallo compatibile
        /// </summary>
        /// <param name="path">Percorso da esaminare</param>
        /// <param name="startIndex">Indice iniziale del tratto</param>
        /// <param name="endIndex">Indice finale del tratto</param>
        /// <returns>True quando esiste un intervallo di offset comune</returns>
        private bool IsCompatible(IReadOnlyList<DeepSiftLocalPathPoint> path, int startIndex, int endIndex)
        {
            double minimumMs = double.NegativeInfinity;
            double maximumMs = double.PositiveInfinity;
            for (int index = startIndex; index <= endIndex; index++)
            {
                minimumMs = Math.Max(minimumMs, path[index].OffsetMs - path[index].UncertaintyMs);
                maximumMs = Math.Min(maximumMs, path[index].OffsetMs + path[index].UncertaintyMs);
            }
            return minimumMs <= maximumMs;
        }

        /// <summary>
        /// Costruisce un regime da un intervallo contiguo del percorso
        /// </summary>
        /// <param name="path">Percorso da cui leggere i punti</param>
        /// <param name="startIndex">Indice iniziale del regime</param>
        /// <param name="endIndex">Indice finale del regime</param>
        /// <returns>Regime locale costruito</returns>
        private DeepSiftLocalRegime BuildRegime(IReadOnlyList<DeepSiftLocalPathPoint> path, int startIndex, int endIndex)
        {
            List<int> indexes = new List<int>(endIndex - startIndex + 1);
            for (int index = startIndex; index <= endIndex; index++)
                indexes.Add(index);
            return this.BuildRegime(path, indexes);
        }

        /// <summary>
        /// Costruisce un regime dai punti indicizzati e ne calcola offset e incertezza robusti
        /// </summary>
        /// <param name="path">Percorso da cui leggere i punti</param>
        /// <param name="indexes">Indici dei punti appartenenti al regime</param>
        /// <returns>Regime locale costruito</returns>
        private DeepSiftLocalRegime BuildRegime(IReadOnlyList<DeepSiftLocalPathPoint> path, IReadOnlyList<int> indexes)
        {
            List<double> offsets = new List<double>();
            double uncertaintyMs = 1.0;
            int distinctSupportCount = 0;
            for (int indexIndex = 0; indexIndex < indexes.Count; indexIndex++)
            {
                int index = indexes[indexIndex];
                offsets.Add(path[index].OffsetMs);
                uncertaintyMs = Math.Max(uncertaintyMs, path[index].UncertaintyMs);
                distinctSupportCount += Math.Max(1, path[index].DistinctSupportCount);
            }
            offsets.Sort();
            int middle = offsets.Count / 2;
            double offsetMs = offsets.Count % 2 == 0 ? (offsets[middle - 1] + offsets[middle]) * 0.5 : offsets[middle];
            List<double> deviations = new List<double>(offsets.Count);
            for (int offsetIndex = 0; offsetIndex < offsets.Count; offsetIndex++)
                deviations.Add(Math.Abs(offsets[offsetIndex] - offsetMs));
            deviations.Sort();
            double dispersionMs = deviations.Count % 2 == 0 ? (deviations[middle - 1] + deviations[middle]) * 0.5 : deviations[middle];
            DeepSiftLocalRegime result = new DeepSiftLocalRegime();
            result.FirstPathIndex = indexes[0];
            result.LastPathIndex = indexes[indexes.Count - 1];
            result.PathIndexes.AddRange(indexes);
            result.MatchCount = distinctSupportCount;
            result.OffsetMs = offsetMs;
            result.UncertaintyMs = uncertaintyMs + dispersionMs;
            result.SourceStartPtsMs = path[result.FirstPathIndex].SourcePtsMs;
            result.SourceEndPtsMs = path[result.LastPathIndex].SourcePtsMs;
            result.LanguageStartPtsMs = path[result.FirstPathIndex].LanguagePtsMs;
            result.LanguageEndPtsMs = path[result.LastPathIndex].LanguagePtsMs;
            return result;
        }

        /// <summary>
        /// Verifica se gli intervalli di offset di due regimi si sovrappongono
        /// </summary>
        /// <param name="first">Primo regime da confrontare</param>
        /// <param name="second">Secondo regime da confrontare</param>
        /// <returns>True quando gli intervalli si intersecano</returns>
        private bool IntervalsOverlap(DeepSiftLocalRegime first, DeepSiftLocalRegime second)
        {
            return this.IntervalsOverlap(first.OffsetMs, first.UncertaintyMs, second.OffsetMs, second.UncertaintyMs);
        }

        /// <summary>
        /// Verifica la sovrapposizione tra due intervalli di offset e le rispettive incertezze
        /// </summary>
        /// <param name="firstOffsetMs">Offset centrale del primo intervallo</param>
        /// <param name="firstUncertaintyMs">Incertezza del primo intervallo</param>
        /// <param name="secondOffsetMs">Offset centrale del secondo intervallo</param>
        /// <param name="secondUncertaintyMs">Incertezza del secondo intervallo</param>
        /// <returns>True quando gli intervalli si intersecano</returns>
        private bool IntervalsOverlap(double firstOffsetMs, double firstUncertaintyMs, double secondOffsetMs, double secondUncertaintyMs)
        {
            return firstOffsetMs - firstUncertaintyMs <= secondOffsetMs + secondUncertaintyMs && secondOffsetMs - secondUncertaintyMs <= firstOffsetMs + firstUncertaintyMs;
        }

        /// <summary>
        /// Converte coppie di regimi osservabili in corridoi di transizione
        /// </summary>
        /// <param name="path">Percorso da cui leggere i confini dei regimi</param>
        /// <param name="regimes">Regimi locali ordinati</param>
        /// <returns>Transizioni tra regimi con offset incompatibili</returns>
        private List<DeepSiftLocalTransition> BuildTransitions(IReadOnlyList<DeepSiftLocalPathPoint> path, IReadOnlyList<DeepSiftLocalRegime> regimes)
        {
            List<DeepSiftLocalTransition> result = new List<DeepSiftLocalTransition>();
            for (int regimeIndex = 0; regimeIndex + 1 < regimes.Count; regimeIndex++)
            {
                DeepSiftLocalRegime before = regimes[regimeIndex];
                DeepSiftLocalRegime after = regimes[regimeIndex + 1];
                if (this.IntervalsOverlap(before, after))
                    continue;
                DeepSiftLocalTransition transition = new DeepSiftLocalTransition();
                transition.BeforeRegimeIndex = regimeIndex;
                transition.AfterRegimeIndex = regimeIndex + 1;
                transition.LastBeforeSourcePtsMs = path[before.LastPathIndex].SourcePtsMs;
                transition.FirstAfterSourcePtsMs = path[after.FirstPathIndex].SourcePtsMs;
                transition.LastBeforeLanguagePtsMs = path[before.LastPathIndex].LanguagePtsMs;
                transition.FirstAfterLanguagePtsMs = path[after.FirstPathIndex].LanguagePtsMs;
                transition.FirstAfterCandidateSourcePtsMs = transition.FirstAfterSourcePtsMs;
                transition.FirstAfterCandidateLanguagePtsMs = transition.FirstAfterLanguagePtsMs;
                result.Add(transition);
            }
            return result;
        }

        /// <summary>
        /// Estende all'indietro il primo frame forte del nuovo regime soltanto lungo un ponte SIFT monotono e continuo
        /// </summary>
        /// <param name="candidates">Punti candidati da usare per il raffinamento</param>
        /// <param name="regimes">Regimi locali che delimitano le transizioni</param>
        /// <param name="transitions">Corridoi di transizione da aggiornare</param>
        private void RefineTransitionCorridors(IReadOnlyList<DeepSiftLocalPathPoint> candidates, IReadOnlyList<DeepSiftLocalRegime> regimes, IReadOnlyList<DeepSiftLocalTransition> transitions)
        {
            for (int transitionIndex = 0; transitionIndex < transitions.Count; transitionIndex++)
            {
                DeepSiftLocalTransition transition = transitions[transitionIndex];
                DeepSiftLocalRegime after = regimes[transition.AfterRegimeIndex];
                double currentSourcePtsMs = transition.FirstAfterSourcePtsMs;
                double currentLanguagePtsMs = transition.FirstAfterLanguagePtsMs;
                double currentUncertaintyMs = after.UncertaintyMs;
                while (true)
                {
                    DeepSiftLocalPathPoint predecessor = null;
                    for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                    {
                        DeepSiftLocalPathPoint candidate = candidates[candidateIndex];
                        if (!this.IntervalsOverlap(candidate.OffsetMs, candidate.UncertaintyMs, after.OffsetMs, after.UncertaintyMs))
                            continue;
                        if (candidate.SourcePtsMs <= transition.LastBeforeSourcePtsMs || candidate.LanguagePtsMs <= transition.LastBeforeLanguagePtsMs)
                            continue;
                        double sourceStepMs = currentSourcePtsMs - candidate.SourcePtsMs;
                        double languageStepMs = currentLanguagePtsMs - candidate.LanguagePtsMs;
                        double continuityMs = Math.Max(1.0, currentUncertaintyMs + candidate.UncertaintyMs);
                        if (sourceStepMs <= 0.0 || languageStepMs <= 0.0 || sourceStepMs > continuityMs || languageStepMs > continuityMs)
                            continue;
                        if (predecessor != null && this.CompareBoundaryPredecessors(candidate, predecessor) <= 0)
                            continue;
                        predecessor = candidate;
                    }
                    if (predecessor == null)
                        break;
                    transition.FirstAfterCandidateSourcePtsMs = predecessor.SourcePtsMs;
                    transition.FirstAfterCandidateLanguagePtsMs = predecessor.LanguagePtsMs;
                    currentSourcePtsMs = predecessor.SourcePtsMs;
                    currentLanguagePtsMs = predecessor.LanguagePtsMs;
                    currentUncertaintyMs = predecessor.UncertaintyMs;
                }
            }
        }

        /// <summary>
        /// Ordina i candidati predecessori di un confine secondo PTS e qualità
        /// </summary>
        /// <param name="candidate">Primo candidato da confrontare</param>
        /// <param name="alternative">Candidato alternativo</param>
        /// <returns>Risultato del confronto ordinato</returns>
        private int CompareBoundaryPredecessors(DeepSiftLocalPathPoint candidate, DeepSiftLocalPathPoint alternative)
        {
            int sourceComparison = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.SourcePtsMs).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(alternative.SourcePtsMs));
            if (sourceComparison != 0)
                return sourceComparison;
            int languageComparison = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.LanguagePtsMs).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(alternative.LanguagePtsMs));
            if (languageComparison != 0)
                return languageComparison;
            return DeepSiftTemporalMetricComparer.QuantizeMetric(candidate.Score).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMetric(alternative.Score));
        }

        /// <summary>
        /// Assegna a ogni punto del percorso l'indice del regime risolto che lo contiene
        /// </summary>
        /// <param name="path">Percorso da aggiornare</param>
        /// <param name="regimes">Regimi risolti con i relativi indici di percorso</param>
        private void AssignResolvedModes(IReadOnlyList<DeepSiftLocalPathPoint> path, IReadOnlyList<DeepSiftLocalRegime> regimes)
        {
            for (int pathIndex = 0; pathIndex < path.Count; pathIndex++)
                path[pathIndex].ModeIndex = -1;
            for (int regimeIndex = 0; regimeIndex < regimes.Count; regimeIndex++)
            {
                DeepSiftLocalRegime regime = regimes[regimeIndex];
                for (int indexIndex = 0; indexIndex < regime.PathIndexes.Count; indexIndex++)
                    path[regime.PathIndexes[indexIndex]].ModeIndex = regimeIndex;
            }
        }

        /// <summary>
        /// Registra i salti del percorso senza attribuire loro semantica di montaggio
        /// </summary>
        /// <param name="path">Percorso da analizzare</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <returns>Lacune temporali rilevate tra punti consecutivi</returns>
        private List<DeepSiftLocalGap> BuildGaps(IReadOnlyList<DeepSiftLocalPathPoint> path, double scale)
        {
            List<DeepSiftLocalGap> result = new List<DeepSiftLocalGap>();
            for (int index = 1; index < path.Count; index++)
            {
                double sourceGapMs = path[index].SourcePtsMs - path[index - 1].SourcePtsMs;
                double languageGapMs = (path[index].LanguagePtsMs - path[index - 1].LanguagePtsMs) / scale;
                if (sourceGapMs <= path[index - 1].UncertaintyMs + path[index].UncertaintyMs && languageGapMs <= path[index - 1].UncertaintyMs + path[index].UncertaintyMs)
                    continue;
                DeepSiftLocalGap gap = new DeepSiftLocalGap();
                gap.BeforePathIndex = index - 1;
                gap.AfterPathIndex = index;
                gap.SourceStartPtsMs = path[index - 1].SourcePtsMs;
                gap.SourceEndPtsMs = path[index].SourcePtsMs;
                gap.LanguageStartPtsMs = path[index - 1].LanguagePtsMs;
                gap.LanguageEndPtsMs = path[index].LanguagePtsMs;
                result.Add(gap);
            }
            return result;
        }

        /// <summary>
        /// Calcola la copertura temporale del percorso sui due assi
        /// </summary>
        /// <param name="path">Percorso da misurare</param>
        /// <param name="sourceCoverageMs">Copertura source in millisecondi</param>
        /// <param name="languageCoverageMs">Copertura language in millisecondi</param>
        private void CalculatePathCoverage(IReadOnlyList<DeepSiftLocalPathPoint> path, out double sourceCoverageMs, out double languageCoverageMs)
        {
            sourceCoverageMs = 0.0;
            languageCoverageMs = 0.0;
            if (path.Count < 2)
                return;
            sourceCoverageMs = Math.Max(0.0, path[path.Count - 1].SourcePtsMs - path[0].SourcePtsMs);
            languageCoverageMs = Math.Max(0.0, path[path.Count - 1].LanguagePtsMs - path[0].LanguagePtsMs);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Conserva frame, bande e risultati riutilizzabili durante l'espansione di una regione
        /// </summary>
        private sealed class RegionWorkspace
        {
            /// <summary>
            /// Inizializza le raccolte del workspace per la scala temporale indicata
            /// </summary>
            /// <param name="scale">Scala temporale source-language</param>
            public RegionWorkspace(double scale)
            {
                this.SourceAnchors = new List<DeepSiftVisualAnchor>();
                this.LanguageAnchors = new List<DeepSiftVisualAnchor>();
                this.SourceRanges = new List<ExtractedRange>();
                this.LanguageRanges = new List<ExtractedRange>();
                this.ProcessedPairKeys = new HashSet<(long SourcePts, long LanguagePts)>();
                this.AcceptedPairs = new DeepSiftTemporalPairAccumulator(scale);
                this.SourceFrameKeys = new HashSet<long>();
                this.LanguageFrameKeys = new HashSet<long>();
                this.SearchBands = new List<OffsetBand>();
            }

            /// <summary>
            /// Ancore source conservate nel workspace
            /// </summary>
            public List<DeepSiftVisualAnchor> SourceAnchors { get; }

            /// <summary>
            /// Ancore language conservate nel workspace
            /// </summary>
            public List<DeepSiftVisualAnchor> LanguageAnchors { get; }

            /// <summary>
            /// Intervalli source già estratti
            /// </summary>
            public List<ExtractedRange> SourceRanges { get; }

            /// <summary>
            /// Intervalli language già estratti
            /// </summary>
            public List<ExtractedRange> LanguageRanges { get; }

            /// <summary>
            /// Chiavi PTS delle coppie già sottoposte al matching
            /// </summary>
            public HashSet<(long SourcePts, long LanguagePts)> ProcessedPairKeys { get; }

            /// <summary>
            /// Accumulatore delle coppie accettate nella regione
            /// </summary>
            public DeepSiftTemporalPairAccumulator AcceptedPairs { get; }

            /// <summary>
            /// Numero complessivo di coppie accettate dai batch
            /// </summary>
            public int AcceptedPairCount { get; set; }

            /// <summary>
            /// Chiavi PTS dei frame source estratti
            /// </summary>
            public HashSet<long> SourceFrameKeys { get; }

            /// <summary>
            /// Chiavi PTS dei frame language estratti
            /// </summary>
            public HashSet<long> LanguageFrameKeys { get; }

            /// <summary>
            /// Bande di ricerca già esplorate o stabilizzate
            /// </summary>
            public List<OffsetBand> SearchBands { get; }

            /// <summary>
            /// Indica se il workspace ha copertura da matrice densa
            /// </summary>
            public bool HasDenseSearchCoverage { get; set; }
        }

        /// <summary>
        /// Rappresenta un intervallo temporale estratto da una timeline
        /// </summary>
        private sealed class ExtractedRange
        {
            /// <summary>
            /// Inizio dell'intervallo in millisecondi
            /// </summary>
            public double StartMs { get; set; }

            /// <summary>
            /// Fine dell'intervallo in millisecondi
            /// </summary>
            public double EndMs { get; set; }
        }

        /// <summary>
        /// Descrive gli intervalli e le bande da elaborare per una regione
        /// </summary>
        private sealed class RegionPlan
        {
            /// <summary>
            /// Inizializza il piano con una raccolta vuota di bande
            /// </summary>
            public RegionPlan()
            {
                this.Bands = new List<OffsetBand>();
            }

            /// <summary>
            /// Inizio dell'intervallo source in millisecondi
            /// </summary>
            public double SourceStartMs { get; set; }

            /// <summary>
            /// Fine dell'intervallo source in millisecondi
            /// </summary>
            public double SourceEndMs { get; set; }

            /// <summary>
            /// Inizio dell'intervallo language in millisecondi
            /// </summary>
            public double LanguageStartMs { get; set; }

            /// <summary>
            /// Fine dell'intervallo language in millisecondi
            /// </summary>
            public double LanguageEndMs { get; set; }

            /// <summary>
            /// Bande di offset attive nel piano
            /// </summary>
            public List<OffsetBand> Bands { get; }
        }

        /// <summary>
        /// Contiene l'esito di un batch di matching locale
        /// </summary>
        private sealed class LocalMatchResult
        {
            /// <summary>
            /// Accumulatore interno usato prima della materializzazione delle coppie
            /// </summary>
            private DeepSiftTemporalPairAccumulator _acceptedPairs;

            /// <summary>
            /// Inizializza il risultato per la scala temporale indicata
            /// </summary>
            /// <param name="scale">Scala temporale source-language</param>
            public LocalMatchResult(double scale)
            {
                this._acceptedPairs = new DeepSiftTemporalPairAccumulator(scale, false);
                this.AcceptedPairs = new List<DeepSiftAcceptedPairDiagnostic>();
                this.RejectReason = "";
            }

            /// <summary>
            /// Aggiunge una coppia accettata all'accumulatore del batch
            /// </summary>
            /// <param name="pair">Coppia accettata dal matcher</param>
            public void Add(DeepSiftAcceptedPairDiagnostic pair)
            {
                this.AcceptedPairCount++;
                this._acceptedPairs.Add(pair);
            }

            /// <summary>
            /// Materializza le coppie aggregate e libera l'accumulatore temporaneo
            /// </summary>
            public void Complete()
            {
                this.AcceptedPairs = this._acceptedPairs.GetCandidates();
                this._acceptedPairs = null;
            }

            /// <summary>
            /// Coppie accettate materializzate al completamento del batch
            /// </summary>
            public List<DeepSiftAcceptedPairDiagnostic> AcceptedPairs { get; private set; }

            /// <summary>
            /// Numero di coppie aggiunte al batch
            /// </summary>
            public int AcceptedPairCount { get; private set; }

            /// <summary>
            /// Motivo del rifiuto del batch oppure stringa vuota
            /// </summary>
            public string RejectReason { get; set; }

            /// <summary>
            /// Durata del matching in millisecondi
            /// </summary>
            public long MatchingMs { get; set; }
        }

        /// <summary>
        /// Rappresenta una banda di offset applicabile a un intervallo source
        /// </summary>
        private sealed class OffsetBand
        {
            /// <summary>
            /// Offset centrale della banda in millisecondi
            /// </summary>
            public double OffsetMs { get; set; }

            /// <summary>
            /// Incertezza dell'offset in millisecondi
            /// </summary>
            public double UncertaintyMs { get; set; }

            /// <summary>
            /// Inizio della copertura source in millisecondi
            /// </summary>
            public double SourceStartMs { get; set; }

            /// <summary>
            /// Fine della copertura source in millisecondi
            /// </summary>
            public double SourceEndMs { get; set; }
        }

        /// <summary>
        /// Stato candidato usato per costruire il miglior percorso monotono
        /// </summary>
        private sealed class PathState
        {
            /// <summary>
            /// Inizializza lo stato con il primo punto del percorso
            /// </summary>
            /// <param name="point">Punto iniziale del percorso</param>
            public PathState(DeepSiftLocalPathPoint point)
            {
                this.MatchCount = 1;
                this.SourceCount = 1;
                this.LanguageCount = 1;
                this.Score = point.Score;
                this.FirstSourcePtsMs = point.SourcePtsMs;
                this.FirstLanguagePtsMs = point.LanguagePtsMs;
                this.FirstSourceIndex = point.SourceAnchorIndex;
                this.FirstLanguageIndex = point.LanguageAnchorIndex;
                this.LastSourcePtsMs = point.SourcePtsMs;
                this.LastLanguagePtsMs = point.LanguagePtsMs;
                this.LastSourceIndex = point.SourceAnchorIndex;
                this.LastLanguageIndex = point.LanguageAnchorIndex;
                this.PreviousIndex = -1;
            }

            /// <summary>
            /// Costruisce uno stato vuoto destinato all'estensione interna
            /// </summary>
            private PathState()
            {
            }

            /// <summary>
            /// Numero di punti nel percorso candidato
            /// </summary>
            public int MatchCount { get; private set; }

            /// <summary>
            /// Numero di ancore source coperte dal percorso
            /// </summary>
            public int SourceCount { get; private set; }

            /// <summary>
            /// Numero di ancore language coperte dal percorso
            /// </summary>
            public int LanguageCount { get; private set; }

            /// <summary>
            /// Copertura temporale comune del percorso in millisecondi
            /// </summary>
            public double CoverageMs { get; private set; }

            /// <summary>
            /// Somma dei punteggi dei punti del percorso
            /// </summary>
            public double Score { get; private set; }

            /// <summary>
            /// Numero di lacune aperte nel percorso
            /// </summary>
            public int GapCount { get; private set; }

            /// <summary>
            /// Lunghezza complessiva delle lacune in millisecondi
            /// </summary>
            public double GapLengthMs { get; private set; }

            /// <summary>
            /// PTS source iniziale del percorso
            /// </summary>
            public double FirstSourcePtsMs { get; private set; }

            /// <summary>
            /// PTS language iniziale del percorso
            /// </summary>
            public double FirstLanguagePtsMs { get; private set; }

            /// <summary>
            /// Indice source iniziale del percorso
            /// </summary>
            public int FirstSourceIndex { get; private set; }

            /// <summary>
            /// Indice language iniziale del percorso
            /// </summary>
            public int FirstLanguageIndex { get; private set; }

            /// <summary>
            /// PTS source finale del percorso
            /// </summary>
            public double LastSourcePtsMs { get; private set; }

            /// <summary>
            /// PTS language finale del percorso
            /// </summary>
            public double LastLanguagePtsMs { get; private set; }

            /// <summary>
            /// Indice source finale del percorso
            /// </summary>
            public int LastSourceIndex { get; private set; }

            /// <summary>
            /// Indice language finale del percorso
            /// </summary>
            public int LastLanguageIndex { get; private set; }

            /// <summary>
            /// Indice dello stato predecessore oppure -1
            /// </summary>
            public int PreviousIndex { get; private set; }

            /// <summary>
            /// Estende lo stato con un punto successivo compatibile
            /// </summary>
            /// <param name="previous">Punto precedente del percorso</param>
            /// <param name="current">Punto da aggiungere</param>
            /// <param name="previousIndex">Indice dello stato predecessore</param>
            /// <param name="scale">Scala temporale source-language</param>
            /// <returns>Nuovo stato esteso</returns>
            public PathState Append(DeepSiftLocalPathPoint previous, DeepSiftLocalPathPoint current, int previousIndex, double scale)
            {
                PathState result = new PathState();
                double sourceGapMs = current.SourcePtsMs - previous.SourcePtsMs;
                double languageGapMs = (current.LanguagePtsMs - previous.LanguagePtsMs) / scale;
                double uncertaintyMs = previous.UncertaintyMs + current.UncertaintyMs;
                bool opensGap = sourceGapMs > uncertaintyMs || languageGapMs > uncertaintyMs;
                result.MatchCount = this.MatchCount + 1;
                result.SourceCount = this.SourceCount + 1;
                result.LanguageCount = this.LanguageCount + 1;
                result.FirstSourcePtsMs = this.FirstSourcePtsMs;
                result.FirstLanguagePtsMs = this.FirstLanguagePtsMs;
                result.FirstSourceIndex = this.FirstSourceIndex;
                result.FirstLanguageIndex = this.FirstLanguageIndex;
                result.LastSourcePtsMs = current.SourcePtsMs;
                result.LastLanguagePtsMs = current.LanguagePtsMs;
                result.LastSourceIndex = current.SourceAnchorIndex;
                result.LastLanguageIndex = current.LanguageAnchorIndex;
                result.CoverageMs = Math.Min(result.LastSourcePtsMs - result.FirstSourcePtsMs, (result.LastLanguagePtsMs - result.FirstLanguagePtsMs) / scale);
                result.Score = this.Score + current.Score;
                result.GapCount = this.GapCount + (opensGap ? 1 : 0);
                result.GapLengthMs = this.GapLengthMs + (opensGap ? Math.Max(sourceGapMs - uncertaintyMs, languageGapMs - uncertaintyMs) : 0.0);
                result.PreviousIndex = previousIndex;
                return result;
            }

            /// <summary>
            /// Confronta lo stato corrente con un'alternativa secondo i criteri del percorso
            /// </summary>
            /// <param name="other">Stato alternativo da confrontare</param>
            /// <returns>True quando lo stato corrente è preferibile</returns>
            public bool IsBetterThan(PathState other)
            {
                if (this.MatchCount != other.MatchCount)
                    return this.MatchCount > other.MatchCount;
                if (this.SourceCount != other.SourceCount)
                    return this.SourceCount > other.SourceCount;
                if (this.LanguageCount != other.LanguageCount)
                    return this.LanguageCount > other.LanguageCount;
                long coverage = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(this.CoverageMs);
                long otherCoverage = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(other.CoverageMs);
                if (coverage != otherCoverage)
                    return coverage > otherCoverage;
                long score = DeepSiftTemporalMetricComparer.QuantizeMetric(this.Score);
                long otherScore = DeepSiftTemporalMetricComparer.QuantizeMetric(other.Score);
                if (score != otherScore)
                    return score > otherScore;
                if (this.GapCount != other.GapCount)
                    return this.GapCount < other.GapCount;
                long gapLength = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(this.GapLengthMs);
                long otherGapLength = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(other.GapLengthMs);
                if (gapLength != otherGapLength)
                    return gapLength < otherGapLength;
                long firstSourcePts = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(this.FirstSourcePtsMs);
                long otherFirstSourcePts = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(other.FirstSourcePtsMs);
                if (firstSourcePts != otherFirstSourcePts)
                    return firstSourcePts < otherFirstSourcePts;
                long firstLanguagePts = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(this.FirstLanguagePtsMs);
                long otherFirstLanguagePts = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(other.FirstLanguagePtsMs);
                if (firstLanguagePts != otherFirstLanguagePts)
                    return firstLanguagePts < otherFirstLanguagePts;
                long lastSourcePts = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(this.LastSourcePtsMs);
                long otherLastSourcePts = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(other.LastSourcePtsMs);
                if (lastSourcePts != otherLastSourcePts)
                    return lastSourcePts < otherLastSourcePts;
                long lastLanguagePts = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(this.LastLanguagePtsMs);
                long otherLastLanguagePts = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(other.LastLanguagePtsMs);
                if (lastLanguagePts != otherLastLanguagePts)
                    return lastLanguagePts < otherLastLanguagePts;
                if (this.FirstSourceIndex != other.FirstSourceIndex)
                    return this.FirstSourceIndex < other.FirstSourceIndex;
                if (this.FirstLanguageIndex != other.FirstLanguageIndex)
                    return this.FirstLanguageIndex < other.FirstLanguageIndex;
                if (this.LastSourceIndex != other.LastSourceIndex)
                    return this.LastSourceIndex < other.LastSourceIndex;
                if (this.LastLanguageIndex != other.LastLanguageIndex)
                    return this.LastLanguageIndex < other.LastLanguageIndex;
                return this.PreviousIndex < other.PreviousIndex;
            }
        }

        #endregion
    }
}
