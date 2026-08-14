using RemuxForge.Core.Analysis.Deep.Features;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Raccoglie evidenze SIFT locali per ricostruire l'allineamento temporale lungo l'intera timeline
    /// </summary>
    internal sealed partial class DeepSiftTemporalAligner
    {
        #region Costanti

        /// <summary>
        /// Distanza temporale fra due dispatch globali consecutivi
        /// </summary>
        private const double DISPATCH_PERIOD_MS = 25000.0;

        /// <summary>
        /// Semilarghezza della finestra source di ogni dispatch
        /// </summary>
        private const double SOURCE_WINDOW_HALF_WIDTH_MS = 15000.0;

        /// <summary>
        /// Raggio di ricerca dell'offset attorno alla previsione corrente
        /// </summary>
        private const double OFFSET_SEARCH_RADIUS_MS = 15000.0;

        /// <summary>
        /// Durata di una slice dell'evidenza globale
        /// </summary>
        private const double GLOBAL_SLICE_MS = 10000.0;

        /// <summary>
        /// Passo fra slice globali sovrapposte
        /// </summary>
        private const double GLOBAL_SLICE_STEP_MS = 5000.0;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Risolve l'offset iniziale dalla prima slice cronologica con un solo modo univoco
        /// </summary>
        /// <param name="pairs">Coppie accettate durante il bootstrap</param>
        /// <param name="sourceToLanguageScale">Scala temporale source-language</param>
        /// <param name="offsetMs">Offset iniziale risolto in millisecondi</param>
        /// <returns>True quando esiste una slice iniziale temporalmente univoca</returns>
        public bool TryResolveInitialOffset(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, double sourceToLanguageScale, out double offsetMs)
        {
            if (pairs == null)
                throw new ArgumentNullException(nameof(pairs));
            if (!double.IsFinite(sourceToLanguageScale) || sourceToLanguageScale <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(sourceToLanguageScale));

            double firstSourcePtsMs = double.PositiveInfinity;
            double lastSourcePtsMs = double.NegativeInfinity;
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                if (pair == null || !double.IsFinite(pair.SourcePtsMs))
                    continue;
                firstSourcePtsMs = Math.Min(firstSourcePtsMs, pair.SourcePtsMs);
                lastSourcePtsMs = Math.Max(lastSourcePtsMs, pair.SourcePtsMs);
            }
            DeepSiftTemporalEvidenceOptions options = new DeepSiftTemporalEvidenceOptions();
            this.BuildPairIndexes(pairs, out Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> bySource, out Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> byLanguage);
            int sliceIndex = 0;
            for (double sliceStartMs = firstSourcePtsMs; sliceStartMs <= lastSourcePtsMs; sliceStartMs += GLOBAL_SLICE_STEP_MS)
            {
                List<DeepSiftAcceptedPairDiagnostic> slicePairs = this.SelectWindowPairs(pairs, sliceStartMs, sliceStartMs + GLOBAL_SLICE_MS);
                List<DeepSiftTemporalMode> modes = this.BuildTemporalModes(slicePairs, sliceIndex, sourceToLanguageScale, options, bySource, byLanguage, false);
                if (this.TrySelectUniqueMode(modes, out DeepSiftTemporalMode selected))
                {
                    offsetMs = selected.OffsetMs;
                    return true;
                }
                sliceIndex++;
            }
            offsetMs = 0.0;
            return false;
        }

        /// <summary>
        /// Raccoglie evidenza temporale globale tramite dispatch locali senza ricorrere a timestamp annotati
        /// </summary>
        /// <param name="sourceAnchors">Timeline visuale source</param>
        /// <param name="languageAnchors">Timeline visuale language</param>
        /// <param name="sourceBlackRuns">Intervalli neri source</param>
        /// <param name="languageBlackRuns">Intervalli neri language</param>
        /// <param name="bootstrapPairs">Evidenze accettate durante il bootstrap</param>
        /// <param name="matcher">Backend di matching SIFT</param>
        /// <param name="initialOffsetMs">Offset iniziale del corridoio di ricerca</param>
        /// <param name="sourceToLanguageScale">Scala temporale source-language</param>
        /// <param name="maximumParallelism">Parallelismo massimo del matcher</param>
        /// <param name="cancellationToken">Token di annullamento cooperativo</param>
        /// <returns>Evidenza temporale globale con supporti e regioni candidate</returns>
        public DeepSiftTemporalEvidenceResult Track(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors, IReadOnlyList<DeepBlackTimelineRun> sourceBlackRuns, IReadOnlyList<DeepBlackTimelineRun> languageBlackRuns, IReadOnlyList<DeepSiftAcceptedPairDiagnostic> bootstrapPairs, FrameFeatureBatchMatcherBase matcher, double initialOffsetMs, double sourceToLanguageScale, int maximumParallelism, CancellationToken cancellationToken)
        {
            if (sourceAnchors == null)
                throw new ArgumentNullException(nameof(sourceAnchors));
            if (languageAnchors == null)
                throw new ArgumentNullException(nameof(languageAnchors));
            if (bootstrapPairs == null)
                throw new ArgumentNullException(nameof(bootstrapPairs));
            if (matcher == null)
                throw new ArgumentNullException(nameof(matcher));
            if (sourceAnchors.Count == 0 || languageAnchors.Count == 0)
                throw new ArgumentException(AppText.T("deep.temporal.argument.emptySiftTimeline"));

            DeepSiftTemporalPairAccumulator evidenceAccumulator = new DeepSiftTemporalPairAccumulator(sourceToLanguageScale);
            evidenceAccumulator.AddRange(bootstrapPairs);
            long processedPairCount = 0;
            double currentOffsetMs = initialOffsetMs;
            double sourceDurationMs = this.GetTimelineEndMs(sourceAnchors);
            double languageDurationMs = this.GetTimelineEndMs(languageAnchors);
            double bootstrapEndMs = this.GetEvidenceEndMs(bootstrapPairs);
            List<double> checkpoints = this.BuildDispatchCheckpoints(sourceDurationMs);
            for (int checkpointIndex = 0; checkpointIndex < checkpoints.Count; checkpointIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double checkpointMs = checkpoints[checkpointIndex];
                double sourceStartMs = Math.Max(0.0, checkpointMs - SOURCE_WINDOW_HALF_WIDTH_MS);
                double sourceEndMs = Math.Min(sourceDurationMs, checkpointMs + SOURCE_WINDOW_HALF_WIDTH_MS);
                if (sourceEndMs <= bootstrapEndMs)
                    continue;

                double languageCenterMs = (checkpointMs - currentOffsetMs) * sourceToLanguageScale;
                double languageHalfWidthMs = (SOURCE_WINDOW_HALF_WIDTH_MS + OFFSET_SEARCH_RADIUS_MS) * sourceToLanguageScale;
                double languageStartMs = Math.Max(0.0, languageCenterMs - languageHalfWidthMs);
                double languageEndMs = Math.Min(languageDurationMs, languageCenterMs + languageHalfWidthMs);
                List<DeepSiftVisualAnchor> sourceWindow = this.SelectAllWindowAnchors(sourceAnchors, sourceStartMs, sourceEndMs);
                List<DeepSiftVisualAnchor> languageWindow = this.SelectAllWindowAnchors(languageAnchors, languageStartMs, languageEndMs);
                if (sourceWindow.Count == 0 || languageWindow.Count == 0)
                    continue;

                List<DeepSiftFramePair> plannedPairs = this.BuildOffsetBandPairs(sourceWindow, languageWindow, currentOffsetMs, sourceToLanguageScale, OFFSET_SEARCH_RADIUS_MS);
                if (plannedPairs.Count == 0)
                    continue;
                DeepSiftBatchMatchResult batch = matcher.BuildMatrix(sourceWindow, languageWindow, maximumParallelism, cancellationToken, null, plannedPairs);
                processedPairCount += batch.ProcessedCellCount;
                if (batch.Cancelled)
                    throw new OperationCanceledException(cancellationToken);
                if (!string.IsNullOrEmpty(batch.RejectReason))
                {
                    DeepSiftTemporalEvidenceResult rejected = new DeepSiftTemporalEvidenceResult();
                    rejected.RejectReason = batch.RejectReason;
                    rejected.InputEvidenceCount = evidenceAccumulator.AcceptedPairCount;
                    return rejected;
                }

                List<DeepSiftAcceptedPairDiagnostic> usablePairs = this.SelectNonBlackPairs(batch.AcceptedPairs, sourceBlackRuns, languageBlackRuns);
                evidenceAccumulator.AddRange(usablePairs);
                if (this.TryResolveObservation(usablePairs, sourceToLanguageScale, currentOffsetMs, OFFSET_SEARCH_RADIUS_MS, 0, out DeepSiftAcceptedPairDiagnostic schedulingObservation))
                    currentOffsetMs = schedulingObservation.SourcePtsMs - (schedulingObservation.LanguagePtsMs / sourceToLanguageScale);
            }

            List<DeepSiftAcceptedPairDiagnostic> evidence = evidenceAccumulator.GetCandidates();
            DeepSiftTemporalEvidenceOptions options = new DeepSiftTemporalEvidenceOptions();
            List<DeepSiftTemporalSliceEvidence> slices = this.BuildGlobalEvidenceMap(evidence, sourceDurationMs, sourceToLanguageScale, options);
            DeepSiftTemporalEvidenceResult temporal = new DeepSiftTemporalEvidenceSolver(options).Solve(slices, sourceBlackRuns, languageBlackRuns, sourceToLanguageScale);
            temporal.InputEvidenceCount = evidenceAccumulator.AcceptedPairCount;
            temporal.GlobalPairEvidence.AddRange(evidence);
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, AppText.F("deep.temporal.log.multimodalTracking", processedPairCount, evidenceAccumulator.AcceptedPairCount, slices.Count, temporal.CandidateRegions.Count, temporal.SupportRuns.Count, this.SummarizeOffsets(temporal)));
            return temporal;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Seleziona tutte le ancore comprese nella finestra PTS senza un secondo thinning
        /// </summary>
        /// <param name="anchors">Timeline completa ordinata per PTS</param>
        /// <param name="startMs">Inizio incluso della finestra</param>
        /// <param name="endMs">Fine esclusa della finestra</param>
        /// <returns>Ancore comprese nella finestra</returns>
        private List<DeepSiftVisualAnchor> SelectAllWindowAnchors(IReadOnlyList<DeepSiftVisualAnchor> anchors, double startMs, double endMs)
        {
            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>();
            for (int anchorIndex = 0; anchorIndex < anchors.Count; anchorIndex++)
            {
                if (anchors[anchorIndex].PtsMs < startMs)
                    continue;
                if (anchors[anchorIndex].PtsMs >= endMs)
                    break;
                result.Add(anchors[anchorIndex]);
            }
            return result;
        }

        /// <summary>
        /// Esclude dalle evidenze temporali le coppie sostenute direttamente da almeno un frame nero
        /// </summary>
        /// <param name="pairs">Coppie geometricamente accettate</param>
        /// <param name="sourceBlackRuns">Intervalli neri source</param>
        /// <param name="languageBlackRuns">Intervalli neri language</param>
        /// <returns>Coppie sostenute da frame informativi su entrambi gli assi</returns>
        private List<DeepSiftAcceptedPairDiagnostic> SelectNonBlackPairs(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, IReadOnlyList<DeepBlackTimelineRun> sourceBlackRuns, IReadOnlyList<DeepBlackTimelineRun> languageBlackRuns)
        {
            List<DeepSiftAcceptedPairDiagnostic> result = new List<DeepSiftAcceptedPairDiagnostic>();
            if (pairs == null)
                return result;
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                if (pair == null || this.IsInsideBlackRun(pair.SourcePtsMs, sourceBlackRuns) || this.IsInsideBlackRun(pair.LanguagePtsMs, languageBlackRuns))
                    continue;
                result.Add(pair);
            }
            return result;
        }

        /// <summary>
        /// Verifica se un PTS appartiene a un intervallo nero
        /// </summary>
        /// <param name="ptsMs">PTS da verificare</param>
        /// <param name="runs">Intervalli neri della timeline</param>
        /// <returns>True quando il PTS è compreso in almeno un intervallo</returns>
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
        /// Pianifica soltanto le celle comprese nel corridoio temporale dell'offset atteso
        /// </summary>
        /// <param name="sourceAnchors">Ancore source della finestra</param>
        /// <param name="languageAnchors">Ancore language della finestra</param>
        /// <param name="expectedOffsetMs">Offset centrale del corridoio</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="radiusMs">Semilarghezza del corridoio in millisecondi source</param>
        /// <returns>Coppie di indici da inviare al matcher</returns>
        private List<DeepSiftFramePair> BuildOffsetBandPairs(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors, double expectedOffsetMs, double scale, double radiusMs)
        {
            List<DeepSiftFramePair> result = new List<DeepSiftFramePair>();
            for (int sourceIndex = 0; sourceIndex < sourceAnchors.Count; sourceIndex++)
            {
                double minimumLanguagePtsMs = (sourceAnchors[sourceIndex].PtsMs - expectedOffsetMs - radiusMs) * scale;
                double maximumLanguagePtsMs = (sourceAnchors[sourceIndex].PtsMs - expectedOffsetMs + radiusMs) * scale;
                int languageIndex = this.FindFirstAnchorAtOrAfter(languageAnchors, minimumLanguagePtsMs);
                while (languageIndex < languageAnchors.Count && languageAnchors[languageIndex].PtsMs <= maximumLanguagePtsMs)
                {
                    result.Add(new DeepSiftFramePair { SourceAnchorIndex = sourceIndex, LanguageAnchorIndex = languageIndex });
                    languageIndex++;
                }
            }
            return result;
        }

        /// <summary>
        /// Trova tramite ricerca binaria la prima ancora non precedente al PTS richiesto
        /// </summary>
        /// <param name="anchors">Ancore ordinate per PTS</param>
        /// <param name="ptsMs">PTS minimo richiesto</param>
        /// <returns>Indice della prima ancora compatibile oppure il numero di ancore</returns>
        private int FindFirstAnchorAtOrAfter(IReadOnlyList<DeepSiftVisualAnchor> anchors, double ptsMs)
        {
            int low = 0;
            int high = anchors.Count;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (anchors[middle].PtsMs < ptsMs)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        /// <summary>
        /// Calcola la fine effettiva della timeline includendo la durata dell'ultima ancora
        /// </summary>
        /// <param name="anchors">Timeline non vuota ordinata per PTS</param>
        /// <returns>PTS finale della timeline in millisecondi</returns>
        private double GetTimelineEndMs(IReadOnlyList<DeepSiftVisualAnchor> anchors)
        {
            DeepSiftVisualAnchor last = anchors[anchors.Count - 1];
            return last.PtsMs + Math.Max(last.DurationMs, last.FrameDurationMs);
        }

        /// <summary>
        /// Determina il massimo PTS source presente nelle evidenze
        /// </summary>
        /// <param name="pairs">Coppie temporali accettate</param>
        /// <returns>Massimo PTS source osservato</returns>
        private double GetEvidenceEndMs(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs)
        {
            double endMs = 0.0;
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
                endMs = Math.Max(endMs, pairs[pairIndex].SourcePtsMs);
            return endMs;
        }

        /// <summary>
        /// Calcola i centri dei dispatch sovrapposti ancorando l'ultima finestra alla fine reale della timeline
        /// </summary>
        /// <param name="sourceDurationMs">Durata della timeline source</param>
        /// <returns>Centri temporali ordinati delle finestre da elaborare</returns>
        private List<double> BuildDispatchCheckpoints(double sourceDurationMs)
        {
            List<double> result = new List<double>();
            double firstCheckpointMs = Math.Min(SOURCE_WINDOW_HALF_WIDTH_MS, sourceDurationMs * 0.5);
            for (double checkpointMs = firstCheckpointMs; checkpointMs < sourceDurationMs; checkpointMs += DISPATCH_PERIOD_MS)
                result.Add(checkpointMs);
            double terminalCheckpointMs = sourceDurationMs <= SOURCE_WINDOW_HALF_WIDTH_MS * 2.0 ? sourceDurationMs * 0.5 : sourceDurationMs - SOURCE_WINDOW_HALF_WIDTH_MS;
            if (result.Count == 0 || Math.Abs(result[result.Count - 1] - terminalCheckpointMs) > 0.001)
                result.Add(terminalCheckpointMs);
            result.Sort();
            return result;
        }

        /// <summary>
        /// Seleziona le coppie comprese nell'intervallo source richiesto
        /// </summary>
        /// <param name="pairs">Coppie temporali complessive</param>
        /// <param name="startMs">Inizio incluso dell'intervallo source</param>
        /// <param name="endMs">Fine esclusa dell'intervallo source</param>
        /// <returns>Coppie comprese nell'intervallo</returns>
        private List<DeepSiftAcceptedPairDiagnostic> SelectWindowPairs(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, double startMs, double endMs)
        {
            List<DeepSiftAcceptedPairDiagnostic> result = new List<DeepSiftAcceptedPairDiagnostic>();
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                if (pair.SourcePtsMs >= startMs && pair.SourcePtsMs < endMs)
                    result.Add(pair);
            }
            return result;
        }

        /// <summary>
        /// Risolve un'osservazione sintetica soltanto quando il corridoio contiene un modo univoco
        /// </summary>
        /// <param name="pairs">Coppie osservate nel dispatch</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="expectedOffsetMs">Offset previsto al centro del corridoio</param>
        /// <param name="searchRadiusMs">Raggio massimo rispetto all'offset previsto</param>
        /// <param name="observationIndex">Indice diagnostico dell'osservazione</param>
        /// <param name="observation">Osservazione rappresentativa risolta</param>
        /// <returns>True quando il modo temporale è univoco</returns>
        private bool TryResolveObservation(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, double scale, double expectedOffsetMs, double searchRadiusMs, int observationIndex, out DeepSiftAcceptedPairDiagnostic observation)
        {
            List<DeepSiftAcceptedPairDiagnostic> candidates = new List<DeepSiftAcceptedPairDiagnostic>();
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                double offsetMs = pair.SourcePtsMs - (pair.LanguagePtsMs / scale);
                if (Math.Abs(offsetMs - expectedOffsetMs) > searchRadiusMs)
                    continue;
                candidates.Add(pair);
            }
            DeepSiftTemporalEvidenceOptions options = new DeepSiftTemporalEvidenceOptions();
            List<DeepSiftTemporalMode> modes = this.BuildTemporalModes(candidates, observationIndex, scale, options, false);
            if (!this.TrySelectUniqueMode(modes, out DeepSiftTemporalMode selected))
            {
                observation = null;
                return false;
            }

            DeepSiftAcceptedPairDiagnostic representative = selected.Representative;
            observation = new DeepSiftAcceptedPairDiagnostic();
            observation.SourceAnchorIndex = observationIndex;
            observation.LanguageAnchorIndex = observationIndex;
            observation.SourcePtsMs = representative.SourcePtsMs;
            observation.LanguagePtsMs = (representative.SourcePtsMs - selected.OffsetMs) * scale;
            observation.SourceFrameDurationMs = representative.SourceFrameDurationMs;
            observation.LanguageFrameDurationMs = representative.LanguageFrameDurationMs;
            observation.SourceSamplingDurationMs = representative.SourceSamplingDurationMs;
            observation.LanguageSamplingDurationMs = representative.LanguageSamplingDurationMs;
            observation.Score = representative.Score;
            observation.InlierCount = representative.InlierCount;
            observation.InlierRatio = representative.InlierRatio;
            observation.SourceCoverage = representative.SourceCoverage;
            observation.LanguageCoverage = representative.LanguageCoverage;
            observation.MeanReprojectionError = representative.MeanReprojectionError;
            return true;
        }

        /// <summary>
        /// Seleziona un modo soltanto quando è l'unico non ambiguo con rappresentante
        /// </summary>
        /// <param name="modes">Modi temporali della slice</param>
        /// <param name="selected">Modo univoco selezionato</param>
        /// <returns>True quando esiste esattamente un modo utilizzabile</returns>
        private bool TrySelectUniqueMode(IReadOnlyList<DeepSiftTemporalMode> modes, out DeepSiftTemporalMode selected)
        {
            selected = null;
            for (int modeIndex = 0; modeIndex < modes.Count; modeIndex++)
            {
                DeepSiftTemporalMode mode = modes[modeIndex];
                if (mode.TemporallyAmbiguous || mode.Representative == null)
                    continue;
                if (selected != null)
                {
                    selected = null;
                    return false;
                }
                selected = mode;
            }
            return selected != null;
        }

        /// <summary>
        /// Costruisce slice globali multimodali e conserva esplicitamente gli intervalli senza evidenza
        /// </summary>
        /// <param name="pairs">Evidenze temporali globali</param>
        /// <param name="sourceDurationMs">Durata della timeline source</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="options">Criteri di supporto temporale</param>
        /// <returns>Slice sovrapposte con tutti i modi osservabili</returns>
        private List<DeepSiftTemporalSliceEvidence> BuildGlobalEvidenceMap(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, double sourceDurationMs, double scale, DeepSiftTemporalEvidenceOptions options)
        {
            List<DeepSiftTemporalSliceEvidence> result = new List<DeepSiftTemporalSliceEvidence>();
            this.BuildPairIndexes(pairs, out Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> bySource, out Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> byLanguage);
            int sliceIndex = 0;
            for (double sliceStartMs = 0.0; sliceStartMs < sourceDurationMs; sliceStartMs += GLOBAL_SLICE_STEP_MS)
            {
                double sliceEndMs = Math.Min(sourceDurationMs, sliceStartMs + GLOBAL_SLICE_MS);
                DeepSiftTemporalSliceEvidence slice = new DeepSiftTemporalSliceEvidence();
                slice.Index = sliceIndex;
                slice.SourceStartPtsMs = sliceStartMs;
                slice.SourceEndPtsMs = sliceEndMs;
                List<DeepSiftAcceptedPairDiagnostic> slicePairs = this.SelectWindowPairs(pairs, sliceStartMs, sliceEndMs);
                slice.Modes = this.BuildTemporalModes(slicePairs, sliceIndex, scale, options, bySource, byLanguage, true);
                slice.Kind = slice.Modes.Count == 0 ? DeepSiftTemporalSliceKind.Gap : DeepSiftTemporalSliceKind.Modes;
                result.Add(slice);
                sliceIndex++;
            }
            return result;
        }

        /// <summary>
        /// Costruisce tutti i modi osservabili della slice conservando le alternative temporali
        /// </summary>
        /// <param name="pairs">Coppie temporali osservate nella slice</param>
        /// <param name="sliceIndex">Indice diagnostico della slice</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="options">Criteri di supporto e ambiguità temporale</param>
        /// <param name="classifyManyToMany">Indica se classificare le relazioni molte-a-molte</param>
        /// <returns>Modi temporali ordinati per supporto e offset</returns>
        private List<DeepSiftTemporalMode> BuildTemporalModes(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, int sliceIndex, double scale, DeepSiftTemporalEvidenceOptions options, bool classifyManyToMany)
        {
            this.BuildPairIndexes(pairs, out Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> bySource, out Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> byLanguage);
            return this.BuildTemporalModes(pairs, sliceIndex, scale, options, bySource, byLanguage, classifyManyToMany);
        }

        /// <summary>
        /// Costruisce i modi usando indici reciproci condivisi fra slice sovrapposte
        /// </summary>
        /// <param name="pairs">Coppie temporali osservate nella slice</param>
        /// <param name="sliceIndex">Indice diagnostico della slice</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="options">Criteri di supporto e ambiguità temporale</param>
        /// <param name="bySource">Coppie indicizzate per PTS source</param>
        /// <param name="byLanguage">Coppie indicizzate per PTS language</param>
        /// <param name="classifyManyToMany">Indica se classificare le relazioni molte-a-molte</param>
        /// <returns>Modi temporali ordinati per supporto e offset</returns>
        private List<DeepSiftTemporalMode> BuildTemporalModes(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, int sliceIndex, double scale, DeepSiftTemporalEvidenceOptions options, Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> bySource, Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> byLanguage, bool classifyManyToMany)
        {
            List<OffsetCandidate> candidates = new List<OffsetCandidate>();
            HashSet<(long SourcePts, long LanguagePts)> manyToManyPairs = classifyManyToMany ? DeepSiftTemporalAmbiguityDetector.FindManyToManyPairs(pairs, options.MinimumScoreMargin) : new HashSet<(long SourcePts, long LanguagePts)>();
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                if (pair == null || pair.Score <= 0.0 || !double.IsFinite(pair.SourcePtsMs) || !double.IsFinite(pair.LanguagePtsMs))
                    continue;
                OffsetCandidate candidate = new OffsetCandidate(pair, pair.SourcePtsMs - (pair.LanguagePtsMs / scale));
                double uncertaintyMs = DeepSiftTemporalMetricComparer.GetFinitePairUncertaintyMs(pair, scale);
                bool sourceBest = this.IsUniqueTemporalBest(pair, bySource[this.GetPtsKey(pair.SourcePtsMs)], scale, uncertaintyMs, options.MinimumScoreMargin);
                bool languageBest = this.IsUniqueTemporalBest(pair, byLanguage[this.GetPtsKey(pair.LanguagePtsMs)], scale, uncertaintyMs, options.MinimumScoreMargin);
                bool manyToMany = manyToManyPairs.Contains((this.GetPtsKey(pair.SourcePtsMs), this.GetPtsKey(pair.LanguagePtsMs)));
                candidate.Classification = sourceBest && languageBest && !manyToMany ? DeepSiftTemporalPairClassification.Strong : DeepSiftTemporalPairClassification.Ambiguous;
                candidates.Add(candidate);
            }
            candidates.Sort((left, right) => left.OffsetMs != right.OffsetMs ? left.OffsetMs.CompareTo(right.OffsetMs) : left.Pair.SourcePtsMs.CompareTo(right.Pair.SourcePtsMs));

            List<OffsetCluster> clusters = new List<OffsetCluster>();
            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                OffsetCandidate candidate = candidates[candidateIndex];
                OffsetCluster selected = null;
                double selectedDistanceMs = double.PositiveInfinity;
                for (int clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
                {
                    double toleranceMs = DeepSiftTemporalMetricComparer.GetFinitePairUncertaintyMs(candidate.Pair, scale) + clusters[clusterIndex].MaximumUncertaintyMs;
                    double distanceMs = Math.Abs(candidate.OffsetMs - clusters[clusterIndex].OffsetMs);
                    if (distanceMs <= toleranceMs && distanceMs < selectedDistanceMs)
                    {
                        selected = clusters[clusterIndex];
                        selectedDistanceMs = distanceMs;
                    }
                }
                if (selected == null)
                {
                    selected = new OffsetCluster();
                    clusters.Add(selected);
                }
                selected.Add(candidate, DeepSiftTemporalMetricComparer.GetFinitePairUncertaintyMs(candidate.Pair, scale));
            }

            for (int clusterIndex = clusters.Count - 1; clusterIndex >= 0; clusterIndex--)
            {
                clusters[clusterIndex].ResolveStrongPath();
                if (clusters[clusterIndex].SourceIndexes.Count < options.MinimumDistinctSupport || clusters[clusterIndex].LanguageIndexes.Count < options.MinimumDistinctSupport)
                    clusters.RemoveAt(clusterIndex);
            }
            clusters.Sort((left, right) =>
            {
                int comparison = right.StrongSourceIndexes.Count.CompareTo(left.StrongSourceIndexes.Count);
                if (comparison != 0)
                    return comparison;
                comparison = right.StrongLanguageIndexes.Count.CompareTo(left.StrongLanguageIndexes.Count);
                if (comparison != 0)
                    return comparison;
                comparison = DeepSiftTemporalMetricComparer.QuantizeMetric(right.StrongScore).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMetric(left.StrongScore));
                return comparison != 0 ? comparison : left.OffsetMs.CompareTo(right.OffsetMs);
            });

            List<DeepSiftTemporalMode> result = new List<DeepSiftTemporalMode>();
            for (int clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
            {
                OffsetCluster cluster = clusters[clusterIndex];
                DeepSiftTemporalMode mode = new DeepSiftTemporalMode();
                mode.SliceIndex = sliceIndex;
                mode.ModeIndex = clusterIndex;
                mode.OffsetMs = cluster.OffsetMs;
                mode.UncertaintyMs = cluster.MaximumUncertaintyMs + cluster.DispersionMs;
                mode.DispersionMs = cluster.DispersionMs;
                mode.SourceStartPtsMs = cluster.SourceStartPtsMs;
                mode.SourceEndPtsMs = cluster.SourceEndPtsMs;
                mode.LanguageStartPtsMs = cluster.LanguageStartPtsMs;
                mode.LanguageEndPtsMs = cluster.LanguageEndPtsMs;
                mode.DistinctSourceCount = cluster.SourceIndexes.Count;
                mode.DistinctLanguageCount = cluster.LanguageIndexes.Count;
                mode.StrongDistinctSourceCount = cluster.StrongSourceIndexes.Count;
                mode.StrongDistinctLanguageCount = cluster.StrongLanguageIndexes.Count;
                mode.AcceptedPairCount = cluster.Candidates.Count;
                for (int candidateIndex = 0; candidateIndex < cluster.Candidates.Count; candidateIndex++)
                {
                    if (cluster.Candidates[candidateIndex].Classification == DeepSiftTemporalPairClassification.Strong)
                        mode.StrongPairCount++;
                    else
                        mode.AmbiguousPairCount++;
                }
                mode.Score = cluster.StrongScore;
                mode.SourceCoverageMs = cluster.StrongSourceCoverageMs;
                mode.LanguageCoverageMs = cluster.StrongLanguageCoverageMs;
                mode.BestToSecondScoreRatio = this.GetBestToSecondScoreRatio(cluster, clusters);
                mode.TemporallyAmbiguous = cluster.StrongSourceIndexes.Count < options.MinimumDistinctSupport || cluster.StrongLanguageIndexes.Count < options.MinimumDistinctSupport || this.HasCompetingTemporalMode(cluster, clusters, options.MinimumDistinctSupport, options.MinimumScoreMargin, scale);
                mode.Representative = this.SelectModeRepresentative(cluster);
                result.Add(mode);
            }
            return result;
        }

        /// <summary>
        /// Indicizza le coppie valide su entrambi gli assi PTS
        /// </summary>
        /// <param name="pairs">Coppie da indicizzare</param>
        /// <param name="bySource">Coppie raggruppate per PTS source</param>
        /// <param name="byLanguage">Coppie raggruppate per PTS language</param>
        private void BuildPairIndexes(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs, out Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> bySource, out Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> byLanguage)
        {
            bySource = new Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>>();
            byLanguage = new Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>>();
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                DeepSiftAcceptedPairDiagnostic pair = pairs[pairIndex];
                if (pair == null || pair.Score <= 0.0 || !double.IsFinite(pair.SourcePtsMs) || !double.IsFinite(pair.LanguagePtsMs))
                    continue;
                this.AddPair(bySource, this.GetPtsKey(pair.SourcePtsMs), pair);
                this.AddPair(byLanguage, this.GetPtsKey(pair.LanguagePtsMs), pair);
            }
        }

        /// <summary>
        /// Aggiunge una coppia alla famiglia associata alla chiave PTS
        /// </summary>
        /// <param name="index">Indice temporale da aggiornare</param>
        /// <param name="key">PTS quantizzato</param>
        /// <param name="pair">Coppia da aggiungere</param>
        private void AddPair(Dictionary<long, List<DeepSiftAcceptedPairDiagnostic>> index, long key, DeepSiftAcceptedPairDiagnostic pair)
        {
            if (!index.TryGetValue(key, out List<DeepSiftAcceptedPairDiagnostic> values))
            {
                values = new List<DeepSiftAcceptedPairDiagnostic>();
                index.Add(key, values);
            }
            values.Add(pair);
        }

        /// <summary>
        /// Quantizza un PTS alla precisione del microsecondo usata dagli indici locali
        /// </summary>
        /// <param name="ptsMs">PTS in millisecondi</param>
        /// <returns>Chiave temporale intera</returns>
        private long GetPtsKey(double ptsMs)
        {
            return (long)Math.Round(ptsMs * 1000.0);
        }

        /// <summary>
        /// Verifica che nessuna alternativa temporalmente distinta abbia confidence equivalente
        /// </summary>
        /// <param name="candidate">Coppia candidata</param>
        /// <param name="alternatives">Coppie sullo stesso asse PTS</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <param name="candidateUncertaintyMs">Incertezza della candidata</param>
        /// <param name="minimumScoreMargin">Margine minimo di confidence</param>
        /// <returns>True quando la candidata domina tutte le alternative incompatibili</returns>
        private bool IsUniqueTemporalBest(DeepSiftAcceptedPairDiagnostic candidate, IReadOnlyList<DeepSiftAcceptedPairDiagnostic> alternatives, double scale, double candidateUncertaintyMs, double minimumScoreMargin)
        {
            double candidateOffsetMs = candidate.SourcePtsMs - (candidate.LanguagePtsMs / scale);
            for (int index = 0; index < alternatives.Count; index++)
            {
                DeepSiftAcceptedPairDiagnostic alternative = alternatives[index];
                if (ReferenceEquals(candidate, alternative))
                    continue;
                double alternativeOffsetMs = alternative.SourcePtsMs - (alternative.LanguagePtsMs / scale);
                double uncertaintyMs = DeepSiftTemporalMetricComparer.GetFinitePairUncertaintyMs(alternative, scale);
                if (Math.Abs(candidateOffsetMs - alternativeOffsetMs) <= candidateUncertaintyMs + uncertaintyMs)
                    continue;
                if (!DeepSiftTemporalMetricComparer.HasHigherConfidence(candidate, alternative, minimumScoreMargin))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Calcola il rapporto fra il supporto del modo e il miglior concorrente sovrapposto
        /// </summary>
        /// <param name="cluster">Modo corrente</param>
        /// <param name="clusters">Tutti i modi della slice</param>
        /// <returns>Rapporto fra score oppure null in assenza di concorrenti</returns>
        private double? GetBestToSecondScoreRatio(OffsetCluster cluster, IReadOnlyList<OffsetCluster> clusters)
        {
            double runnerUpScore = 0.0;
            for (int clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
            {
                OffsetCluster candidate = clusters[clusterIndex];
                if (ReferenceEquals(cluster, candidate) || candidate.StrongPath.Count == 0)
                    continue;
                bool sourceOverlap = cluster.StrongSourceStartPtsMs <= candidate.StrongSourceEndPtsMs && candidate.StrongSourceStartPtsMs <= cluster.StrongSourceEndPtsMs;
                if (sourceOverlap)
                    runnerUpScore = Math.Max(runnerUpScore, candidate.StrongScore);
            }
            return runnerUpScore > 0.0 ? cluster.StrongScore / runnerUpScore : null;
        }

        /// <summary>
        /// Verifica se esiste un modo sovrapposto che il cluster corrente non domina
        /// </summary>
        /// <param name="cluster">Modo corrente</param>
        /// <param name="clusters">Tutti i modi della slice</param>
        /// <param name="minimumSupport">Supporto distinto minimo per asse</param>
        /// <param name="minimumScoreMargin">Margine minimo di confidence</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <returns>True quando permane un'alternativa temporale equivalente</returns>
        private bool HasCompetingTemporalMode(OffsetCluster cluster, IReadOnlyList<OffsetCluster> clusters, int minimumSupport, double minimumScoreMargin, double scale)
        {
            if (cluster.StrongSourceIndexes.Count < minimumSupport || cluster.StrongLanguageIndexes.Count < minimumSupport)
                return false;
            for (int clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
            {
                OffsetCluster other = clusters[clusterIndex];
                if (ReferenceEquals(cluster, other) || other.StrongSourceIndexes.Count < minimumSupport || other.StrongLanguageIndexes.Count < minimumSupport)
                    continue;
                bool sourceOverlap = cluster.StrongSourceStartPtsMs <= other.StrongSourceEndPtsMs && other.StrongSourceStartPtsMs <= cluster.StrongSourceEndPtsMs;
                if (sourceOverlap && !this.StrictlyDominates(cluster, other, minimumScoreMargin, scale))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Verifica la dominanza lessicografica senza comprimere modi incompatibili equivalenti
        /// </summary>
        /// <param name="candidate">Cluster candidato dominante</param>
        /// <param name="alternative">Cluster concorrente</param>
        /// <param name="minimumScoreMargin">Margine minimo di confidence</param>
        /// <param name="scale">Scala temporale source-language</param>
        /// <returns>True quando il candidato non è peggiore in alcuna metrica ed è migliore in almeno una</returns>
        private bool StrictlyDominates(OffsetCluster candidate, OffsetCluster alternative, double minimumScoreMargin, double scale)
        {
            double candidateCoverageMs = Math.Min(candidate.StrongSourceCoverageMs, candidate.StrongLanguageCoverageMs / scale);
            double alternativeCoverageMs = Math.Min(alternative.StrongSourceCoverageMs, alternative.StrongLanguageCoverageMs / scale);
            long candidateCoverage = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidateCoverageMs);
            long alternativeCoverage = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(alternativeCoverageMs);
            long candidateScore = DeepSiftTemporalMetricComparer.QuantizeMetric(candidate.AverageStrongScore);
            long alternativeScore = DeepSiftTemporalMetricComparer.QuantizeMetric(alternative.AverageStrongScore);
            long minimumScoreDifference = DeepSiftTemporalMetricComparer.QuantizeMetric(minimumScoreMargin);
            long candidateDispersion = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.DispersionMs);
            long alternativeDispersion = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(alternative.DispersionMs);
            bool noWorse = candidate.StrongSourceIndexes.Count >= alternative.StrongSourceIndexes.Count &&
                           candidate.StrongLanguageIndexes.Count >= alternative.StrongLanguageIndexes.Count &&
                           candidateCoverage >= alternativeCoverage &&
                           candidateScore >= alternativeScore &&
                           candidateDispersion <= alternativeDispersion;
            bool strictlyBetter = candidate.StrongSourceIndexes.Count > alternative.StrongSourceIndexes.Count ||
                                  candidate.StrongLanguageIndexes.Count > alternative.StrongLanguageIndexes.Count ||
                                  candidateCoverage > alternativeCoverage ||
                                  candidateScore - alternativeScore >= minimumScoreDifference ||
                                  candidateDispersion < alternativeDispersion;
            return noWorse && strictlyBetter;
        }

        /// <summary>
        /// Seleziona la coppia più vicina all'offset mediano usando la confidence come spareggio
        /// </summary>
        /// <param name="cluster">Cluster da rappresentare</param>
        /// <returns>Coppia rappresentativa del modo</returns>
        private DeepSiftAcceptedPairDiagnostic SelectModeRepresentative(OffsetCluster cluster)
        {
            OffsetCandidate selected = cluster.StrongPath.Count > 0 ? cluster.StrongPath[0] : cluster.Candidates[0];
            double selectedDistanceMs = Math.Abs(selected.OffsetMs - cluster.OffsetMs);
            IReadOnlyList<OffsetCandidate> representatives = cluster.StrongPath.Count > 0 ? cluster.StrongPath : cluster.Candidates;
            for (int candidateIndex = 1; candidateIndex < representatives.Count; candidateIndex++)
            {
                OffsetCandidate candidate = representatives[candidateIndex];
                double distanceMs = Math.Abs(candidate.OffsetMs - cluster.OffsetMs);
                long distance = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(distanceMs);
                long selectedDistance = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(selectedDistanceMs);
                long score = DeepSiftTemporalMetricComparer.QuantizeMetric(candidate.Pair.Score);
                long selectedScore = DeepSiftTemporalMetricComparer.QuantizeMetric(selected.Pair.Score);
                if (distance < selectedDistance || (distance == selectedDistance && (score > selectedScore || (score == selectedScore && candidate.Pair.SourcePtsMs < selected.Pair.SourcePtsMs))))
                {
                    selected = candidate;
                    selectedDistanceMs = distanceMs;
                }
            }
            return selected.Pair;
        }

        /// <summary>
        /// Serializza gli offset dei support run per la diagnostica compatta
        /// </summary>
        /// <param name="result">Evidenza temporale risolta</param>
        /// <returns>Sequenza offset-supporto oppure un trattino</returns>
        private string SummarizeOffsets(DeepSiftTemporalEvidenceResult result)
        {
            if (result == null || result.SupportRuns.Count == 0)
                return "-";
            List<string> values = new List<string>(result.SupportRuns.Count);
            for (int runIndex = 0; runIndex < result.SupportRuns.Count; runIndex++)
                values.Add(result.SupportRuns[runIndex].OffsetMs.ToString("F1", CultureInfo.InvariantCulture) + "(" + result.SupportRuns[runIndex].MatchCount.ToString(CultureInfo.InvariantCulture) + ")");
            return string.Join("/", values);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Associa una coppia accettata al relativo offset e alla classificazione temporale
        /// </summary>
        private sealed class OffsetCandidate
        {
            /// <summary>
            /// Costruisce una candidata temporale
            /// </summary>
            /// <param name="pair">Coppia SIFT accettata</param>
            /// <param name="offsetMs">Offset source-language in millisecondi</param>
            public OffsetCandidate(DeepSiftAcceptedPairDiagnostic pair, double offsetMs)
            {
                this.Pair = pair;
                this.OffsetMs = offsetMs;
            }

            /// <summary>
            /// Coppia SIFT accettata
            /// </summary>
            public DeepSiftAcceptedPairDiagnostic Pair { get; }

            /// <summary>
            /// Offset source-language in millisecondi
            /// </summary>
            public double OffsetMs { get; }

            /// <summary>
            /// Classificazione temporale della coppia
            /// </summary>
            public DeepSiftTemporalPairClassification Classification { get; set; }
        }

        /// <summary>
        /// Raggruppa candidate con offset compatibili entro le rispettive incertezze
        /// </summary>
        private sealed class OffsetCluster
        {
            /// <summary>
            /// Inizializza le collezioni di supporto del cluster
            /// </summary>
            public OffsetCluster()
            {
                this.Candidates = new List<OffsetCandidate>();
                this.SourceIndexes = new HashSet<long>();
                this.LanguageIndexes = new HashSet<long>();
                this.StrongSourceIndexes = new HashSet<long>();
                this.StrongLanguageIndexes = new HashSet<long>();
                this.StrongPath = new List<OffsetCandidate>();
            }

            /// <summary>
            /// Aggiunge una candidata e aggiorna mediana, dispersione e copertura
            /// </summary>
            /// <param name="candidate">Candidata da aggiungere</param>
            /// <param name="uncertaintyMs">Incertezza temporale della candidata</param>
            public void Add(OffsetCandidate candidate, double uncertaintyMs)
            {
                this.Candidates.Add(candidate);
                this.SourceIndexes.Add((long)Math.Round(candidate.Pair.SourcePtsMs * 1000.0));
                this.LanguageIndexes.Add((long)Math.Round(candidate.Pair.LanguagePtsMs * 1000.0));
                this.MaximumUncertaintyMs = Math.Max(this.MaximumUncertaintyMs, uncertaintyMs);
                int middle = this.Candidates.Count / 2;
                this.OffsetMs = this.Candidates.Count % 2 == 0 ? (this.Candidates[middle - 1].OffsetMs + this.Candidates[middle].OffsetMs) * 0.5 : this.Candidates[middle].OffsetMs;
                List<double> deviations = new List<double>(this.Candidates.Count);
                for (int candidateIndex = 0; candidateIndex < this.Candidates.Count; candidateIndex++)
                    deviations.Add(Math.Abs(this.Candidates[candidateIndex].OffsetMs - this.OffsetMs));
                deviations.Sort();
                this.DispersionMs = deviations.Count % 2 == 0 ? (deviations[middle - 1] + deviations[middle]) * 0.5 : deviations[middle];
                this.SourceStartPtsMs = this.Candidates.Count == 1 ? candidate.Pair.SourcePtsMs : Math.Min(this.SourceStartPtsMs, candidate.Pair.SourcePtsMs);
                this.SourceEndPtsMs = this.Candidates.Count == 1 ? candidate.Pair.SourcePtsMs : Math.Max(this.SourceEndPtsMs, candidate.Pair.SourcePtsMs);
                this.LanguageStartPtsMs = this.Candidates.Count == 1 ? candidate.Pair.LanguagePtsMs : Math.Min(this.LanguageStartPtsMs, candidate.Pair.LanguagePtsMs);
                this.LanguageEndPtsMs = this.Candidates.Count == 1 ? candidate.Pair.LanguagePtsMs : Math.Max(this.LanguageEndPtsMs, candidate.Pair.LanguagePtsMs);
            }

            /// <summary>
            /// Seleziona il percorso crescente delle coppie forti e ne aggiorna il supporto temporale
            /// </summary>
            public void ResolveStrongPath()
            {
                List<OffsetCandidate> candidates = new List<OffsetCandidate>();
                for (int candidateIndex = 0; candidateIndex < this.Candidates.Count; candidateIndex++)
                {
                    if (this.Candidates[candidateIndex].Classification == DeepSiftTemporalPairClassification.Strong)
                        candidates.Add(this.Candidates[candidateIndex]);
                }
                candidates.Sort((left, right) => left.Pair.SourcePtsMs != right.Pair.SourcePtsMs ? left.Pair.SourcePtsMs.CompareTo(right.Pair.SourcePtsMs) : left.Pair.LanguagePtsMs.CompareTo(right.Pair.LanguagePtsMs));
                if (candidates.Count == 0)
                    return;

                int[] lengths = new int[candidates.Count];
                double[] scores = new double[candidates.Count];
                int[] previous = new int[candidates.Count];
                int bestIndex = 0;
                for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                {
                    lengths[candidateIndex] = 1;
                    scores[candidateIndex] = candidates[candidateIndex].Pair.Score;
                    previous[candidateIndex] = -1;
                    for (int precedingIndex = 0; precedingIndex < candidateIndex; precedingIndex++)
                    {
                        if (candidates[precedingIndex].Pair.SourcePtsMs >= candidates[candidateIndex].Pair.SourcePtsMs || candidates[precedingIndex].Pair.LanguagePtsMs >= candidates[candidateIndex].Pair.LanguagePtsMs)
                            continue;
                        int length = lengths[precedingIndex] + 1;
                        double score = scores[precedingIndex] + candidates[candidateIndex].Pair.Score;
                        if (length > lengths[candidateIndex] || (length == lengths[candidateIndex] && DeepSiftTemporalMetricComparer.QuantizeMetric(score) > DeepSiftTemporalMetricComparer.QuantizeMetric(scores[candidateIndex])))
                        {
                            lengths[candidateIndex] = length;
                            scores[candidateIndex] = score;
                            previous[candidateIndex] = precedingIndex;
                        }
                    }
                    if (lengths[candidateIndex] > lengths[bestIndex] || (lengths[candidateIndex] == lengths[bestIndex] && DeepSiftTemporalMetricComparer.QuantizeMetric(scores[candidateIndex]) > DeepSiftTemporalMetricComparer.QuantizeMetric(scores[bestIndex])))
                        bestIndex = candidateIndex;
                }
                while (bestIndex >= 0)
                {
                    this.StrongPath.Add(candidates[bestIndex]);
                    bestIndex = previous[bestIndex];
                }
                this.StrongPath.Reverse();
                this.StrongSourceIndexes.Clear();
                this.StrongLanguageIndexes.Clear();
                for (int pathIndex = 0; pathIndex < this.StrongPath.Count; pathIndex++)
                {
                    OffsetCandidate candidate = this.StrongPath[pathIndex];
                    this.StrongSourceIndexes.Add((long)Math.Round(candidate.Pair.SourcePtsMs * 1000.0));
                    this.StrongLanguageIndexes.Add((long)Math.Round(candidate.Pair.LanguagePtsMs * 1000.0));
                    this.StrongScore += candidate.Pair.Score;
                    this.StrongSourceStartPtsMs = pathIndex == 0 ? candidate.Pair.SourcePtsMs : Math.Min(this.StrongSourceStartPtsMs, candidate.Pair.SourcePtsMs);
                    this.StrongSourceEndPtsMs = pathIndex == 0 ? candidate.Pair.SourcePtsMs : Math.Max(this.StrongSourceEndPtsMs, candidate.Pair.SourcePtsMs);
                    this.StrongLanguageStartPtsMs = pathIndex == 0 ? candidate.Pair.LanguagePtsMs : Math.Min(this.StrongLanguageStartPtsMs, candidate.Pair.LanguagePtsMs);
                    this.StrongLanguageEndPtsMs = pathIndex == 0 ? candidate.Pair.LanguagePtsMs : Math.Max(this.StrongLanguageEndPtsMs, candidate.Pair.LanguagePtsMs);
                }
            }

            /// <summary>
            /// Candidate assegnate al cluster
            /// </summary>
            public List<OffsetCandidate> Candidates { get; }

            /// <summary>
            /// PTS source distinti osservati
            /// </summary>
            public HashSet<long> SourceIndexes { get; }

            /// <summary>
            /// PTS language distinti osservati
            /// </summary>
            public HashSet<long> LanguageIndexes { get; }

            /// <summary>
            /// PTS source distinti nel percorso forte
            /// </summary>
            public HashSet<long> StrongSourceIndexes { get; }

            /// <summary>
            /// PTS language distinti nel percorso forte
            /// </summary>
            public HashSet<long> StrongLanguageIndexes { get; }

            /// <summary>
            /// Sottosequenza crescente delle candidate forti
            /// </summary>
            public List<OffsetCandidate> StrongPath { get; }

            /// <summary>
            /// Somma delle confidence nel percorso forte
            /// </summary>
            public double StrongScore { get; private set; }

            /// <summary>
            /// Confidence media del percorso forte
            /// </summary>
            public double AverageStrongScore { get { return this.StrongPath.Count > 0 ? this.StrongScore / this.StrongPath.Count : 0.0; } }

            /// <summary>
            /// Primo PTS source del percorso forte
            /// </summary>
            public double StrongSourceStartPtsMs { get; private set; }

            /// <summary>
            /// Ultimo PTS source del percorso forte
            /// </summary>
            public double StrongSourceEndPtsMs { get; private set; }

            /// <summary>
            /// Primo PTS language del percorso forte
            /// </summary>
            public double StrongLanguageStartPtsMs { get; private set; }

            /// <summary>
            /// Ultimo PTS language del percorso forte
            /// </summary>
            public double StrongLanguageEndPtsMs { get; private set; }

            /// <summary>
            /// Copertura source del percorso forte
            /// </summary>
            public double StrongSourceCoverageMs { get { return Math.Max(0.0, this.StrongSourceEndPtsMs - this.StrongSourceStartPtsMs); } }

            /// <summary>
            /// Copertura language del percorso forte
            /// </summary>
            public double StrongLanguageCoverageMs { get { return Math.Max(0.0, this.StrongLanguageEndPtsMs - this.StrongLanguageStartPtsMs); } }

            /// <summary>
            /// Mediana degli offset nel cluster
            /// </summary>
            public double OffsetMs { get; private set; }

            /// <summary>
            /// Massima incertezza temporale osservata
            /// </summary>
            public double MaximumUncertaintyMs { get; private set; }

            /// <summary>
            /// Deviazione assoluta mediana degli offset
            /// </summary>
            public double DispersionMs { get; private set; }

            /// <summary>
            /// Primo PTS source del cluster
            /// </summary>
            public double SourceStartPtsMs { get; private set; }

            /// <summary>
            /// Ultimo PTS source del cluster
            /// </summary>
            public double SourceEndPtsMs { get; private set; }

            /// <summary>
            /// Primo PTS language del cluster
            /// </summary>
            public double LanguageStartPtsMs { get; private set; }

            /// <summary>
            /// Ultimo PTS language del cluster
            /// </summary>
            public double LanguageEndPtsMs { get; private set; }
        }

        #endregion
    }

}
