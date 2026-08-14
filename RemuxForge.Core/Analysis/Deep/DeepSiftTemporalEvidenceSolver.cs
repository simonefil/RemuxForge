using RemuxForge.Core.Models;
using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Seleziona il percorso temporale canonico dalle evidenze multimodali globali
    /// </summary>
    public sealed class DeepSiftTemporalEvidenceSolver
    {
        #region Variabili di classe

        /// <summary>
        /// Opzioni di supporto e selezione applicate dal solver
        /// </summary>
        private readonly DeepSiftTemporalEvidenceOptions _options;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruisce il solver con opzioni esplicite oppure predefinite
        /// </summary>
        /// <param name="options">Opzioni di supporto e selezione; se null vengono usate le impostazioni predefinite</param>
        public DeepSiftTemporalEvidenceSolver(DeepSiftTemporalEvidenceOptions options = null)
        {
            this._options = options ?? new DeepSiftTemporalEvidenceOptions();
            if (this._options.MinimumDistinctSupport < 1)
                throw new ArgumentOutOfRangeException(nameof(options));
            if (!double.IsFinite(this._options.MinimumScoreMargin) || this._options.MinimumScoreMargin < 0.0 || this._options.MinimumScoreMargin > 1.0)
                throw new ArgumentOutOfRangeException(nameof(options));
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Risolve il percorso crescente, i support run affidabili e le regioni candidate con la scala predefinita
        /// </summary>
        /// <param name="slices">Evidenze temporali ordinate per slice</param>
        /// <returns>Risultato della risoluzione temporale globale</returns>
        public DeepSiftTemporalEvidenceResult Solve(IReadOnlyList<DeepSiftTemporalSliceEvidence> slices)
        {
            return this.Solve(slices, null, null, 1.0);
        }

        /// <summary>
        /// Risolve la mappa globale usando i black run corrispondenti soltanto come segnali aggiuntivi di apertura
        /// </summary>
        /// <param name="slices">Evidenze temporali ordinate per slice</param>
        /// <param name="sourceBlackRuns">Black run rilevati nella timeline source, se disponibili</param>
        /// <param name="languageBlackRuns">Black run rilevati nella timeline language, se disponibili</param>
        /// <param name="scale">Scala di conversione dai PTS source ai PTS language</param>
        /// <returns>Risultato della risoluzione temporale globale</returns>
        public DeepSiftTemporalEvidenceResult Solve(IReadOnlyList<DeepSiftTemporalSliceEvidence> slices, IReadOnlyList<DeepBlackTimelineRun> sourceBlackRuns, IReadOnlyList<DeepBlackTimelineRun> languageBlackRuns, double scale)
        {
            if (slices == null)
                throw new ArgumentNullException(nameof(slices));
            if (!double.IsFinite(scale) || scale <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(scale));

            DeepSiftTemporalEvidenceResult result = new DeepSiftTemporalEvidenceResult();
            for (int sliceIndex = 0; sliceIndex < slices.Count; sliceIndex++)
            {
                DeepSiftTemporalSliceEvidence slice = slices[sliceIndex];
                if (slice == null)
                    continue;
                result.Slices.Add(slice);
                for (int modeIndex = 0; modeIndex < slice.Modes.Count; modeIndex++)
                    result.InputEvidenceCount += slice.Modes[modeIndex].DistinctSourceCount;
            }

            result.Chain = this.BuildCanonicalPath(result.Slices, scale, out double chainScore);
            result.ChainScore = chainScore;
            if (result.Chain.Count == 0)
            {
                result.RejectReason = AppText.T("deep.temporal.solver.noUniquePath");
                return result;
            }

            this.BuildSupportRuns(result);
            if (result.SupportRuns.Count == 0)
            {
                result.RejectReason = AppText.T("deep.temporal.solver.noReliableSupportRuns");
                return result;
            }
            this.BuildCandidateRegions(result, sourceBlackRuns, languageBlackRuns, scale);
            result.Accepted = true;
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Costruisce il percorso crescente migliore usando soltanto modi univoci e affidabili
        /// </summary>
        /// <param name="slices">Evidenze temporali da cui estrarre i candidati</param>
        /// <param name="scale">Scala di conversione dai PTS source ai PTS language</param>
        /// <param name="chainScore">Score complessivo del percorso selezionato</param>
        /// <returns>Match ordinati che compongono il percorso canonico</returns>
        private List<DeepSiftTemporalChainMatch> BuildCanonicalPath(IReadOnlyList<DeepSiftTemporalSliceEvidence> slices, double scale, out double chainScore)
        {
            List<DeepSiftTemporalChainMatch> candidates = new List<DeepSiftTemporalChainMatch>();
            for (int sliceIndex = 0; sliceIndex < slices.Count; sliceIndex++)
            {
                DeepSiftTemporalSliceEvidence slice = slices[sliceIndex];
                if (slice.Kind != DeepSiftTemporalSliceKind.Modes)
                    continue;
                for (int modeIndex = 0; modeIndex < slice.Modes.Count; modeIndex++)
                {
                    DeepSiftTemporalMode mode = slice.Modes[modeIndex];
                    if (mode.Representative == null || mode.TemporallyAmbiguous || mode.StrongDistinctSourceCount < this._options.MinimumDistinctSupport || mode.StrongDistinctLanguageCount < this._options.MinimumDistinctSupport)
                        continue;
                    DeepSiftAcceptedPairDiagnostic pair = mode.Representative;
                    DeepSiftTemporalChainMatch match = new DeepSiftTemporalChainMatch();
                    match.SourceAnchorIndex = pair.SourceAnchorIndex;
                    match.LanguageAnchorIndex = pair.LanguageAnchorIndex;
                    match.SliceIndex = mode.SliceIndex;
                    match.ModeIndex = mode.ModeIndex;
                    match.SupportCount = Math.Min(mode.StrongDistinctSourceCount, mode.StrongDistinctLanguageCount);
                    match.SourcePtsMs = pair.SourcePtsMs;
                    match.LanguagePtsMs = pair.LanguagePtsMs;
                    match.OffsetMs = mode.OffsetMs;
                    match.UncertaintyMs = mode.UncertaintyMs;
                    match.Score = mode.Score;
                    candidates.Add(match);
                }
            }

            candidates.Sort(this.CompareChainMatches);
            if (candidates.Count == 0)
            {
                chainScore = 0.0;
                return candidates;
            }

            GlobalPathState[] states = new GlobalPathState[candidates.Count];
            int bestIndex = 0;
            for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                states[candidateIndex] = new GlobalPathState(candidates[candidateIndex]);
                for (int previousIndex = 0; previousIndex < candidateIndex; previousIndex++)
                {
                    DeepSiftTemporalChainMatch previous = candidates[previousIndex];
                    DeepSiftTemporalChainMatch current = candidates[candidateIndex];
                    if (previous.SliceIndex >= current.SliceIndex || previous.SourcePtsMs >= current.SourcePtsMs || previous.LanguagePtsMs >= current.LanguagePtsMs)
                        continue;
                    GlobalPathState extended = states[previousIndex].Append(current, previousIndex, scale);
                    if (extended.IsBetterThan(states[candidateIndex]))
                        states[candidateIndex] = extended;
                }
                if (states[candidateIndex].IsBetterThan(states[bestIndex]))
                    bestIndex = candidateIndex;
            }

            chainScore = states[bestIndex].Score;
            List<DeepSiftTemporalChainMatch> result = new List<DeepSiftTemporalChainMatch>(states[bestIndex].MatchCount);
            while (bestIndex >= 0)
            {
                result.Add(candidates[bestIndex]);
                bestIndex = states[bestIndex].PreviousIndex;
            }
            result.Reverse();
            return result;
        }

        /// <summary>
        /// Confronta i match canonici per posizione temporale e identità del modo
        /// </summary>
        /// <param name="left">Primo match</param>
        /// <param name="right">Secondo match</param>
        /// <returns>Risultato del confronto deterministico</returns>
        private int CompareChainMatches(DeepSiftTemporalChainMatch left, DeepSiftTemporalChainMatch right)
        {
            int comparison = left.SourcePtsMs.CompareTo(right.SourcePtsMs);
            if (comparison != 0)
                return comparison;
            comparison = left.LanguagePtsMs.CompareTo(right.LanguagePtsMs);
            if (comparison != 0)
                return comparison;
            comparison = left.SliceIndex.CompareTo(right.SliceIndex);
            return comparison != 0 ? comparison : left.ModeIndex.CompareTo(right.ModeIndex);
        }

        /// <summary>
        /// Segmenta il percorso soltanto quando gli intervalli di offset PTS non sono compatibili
        /// </summary>
        /// <param name="result">Risultato temporale da aggiornare con i support run</param>
        private void BuildSupportRuns(DeepSiftTemporalEvidenceResult result)
        {
            int startIndex = 0;
            for (int chainIndex = 1; chainIndex <= result.Chain.Count; chainIndex++)
            {
                bool endsRegime = chainIndex == result.Chain.Count || !this.IsCompatibleWithRun(result.Chain, startIndex, chainIndex);
                if (!endsRegime)
                    continue;
                this.AddSupportRun(result, startIndex, chainIndex - 1);
                startIndex = chainIndex;
            }

        }

        /// <summary>
        /// Verifica che gli intervalli di offset del tratto mantengano un'intersezione comune
        /// </summary>
        /// <param name="chain">Percorso canonico ordinato</param>
        /// <param name="startIndex">Primo match del support run</param>
        /// <param name="nextIndex">Nuovo match da includere</param>
        /// <returns>True quando tutti gli intervalli restano compatibili</returns>
        private bool IsCompatibleWithRun(List<DeepSiftTemporalChainMatch> chain, int startIndex, int nextIndex)
        {
            double minimumOffsetMs = double.NegativeInfinity;
            double maximumOffsetMs = double.PositiveInfinity;
            for (int chainIndex = startIndex; chainIndex <= nextIndex; chainIndex++)
            {
                minimumOffsetMs = Math.Max(minimumOffsetMs, chain[chainIndex].OffsetMs - chain[chainIndex].UncertaintyMs);
                maximumOffsetMs = Math.Min(maximumOffsetMs, chain[chainIndex].OffsetMs + chain[chainIndex].UncertaintyMs);
            }
            return minimumOffsetMs <= maximumOffsetMs;
        }

        /// <summary>
        /// Materializza un support run da un tratto compatibile del percorso canonico
        /// </summary>
        /// <param name="result">Risultato temporale da aggiornare</param>
        /// <param name="startIndex">Primo indice incluso del percorso</param>
        /// <param name="endIndex">Ultimo indice incluso del percorso</param>
        private void AddSupportRun(DeepSiftTemporalEvidenceResult result, int startIndex, int endIndex)
        {
            List<double> offsets = new List<double>();
            double minimumOffsetMs = double.NegativeInfinity;
            double maximumOffsetMs = double.PositiveInfinity;
            for (int chainIndex = startIndex; chainIndex <= endIndex; chainIndex++)
            {
                offsets.Add(result.Chain[chainIndex].OffsetMs);
                minimumOffsetMs = Math.Max(minimumOffsetMs, result.Chain[chainIndex].OffsetMs - result.Chain[chainIndex].UncertaintyMs);
                maximumOffsetMs = Math.Min(maximumOffsetMs, result.Chain[chainIndex].OffsetMs + result.Chain[chainIndex].UncertaintyMs);
            }
            offsets.Sort();
            int middle = offsets.Count / 2;
            DeepSiftTemporalSupportRun run = new DeepSiftTemporalSupportRun();
            run.FirstChainIndex = startIndex;
            run.LastChainIndex = endIndex;
            run.MatchCount = 0;
            for (int chainIndex = startIndex; chainIndex <= endIndex; chainIndex++)
                run.MatchCount += result.Chain[chainIndex].SupportCount;
            run.OffsetMs = offsets.Count % 2 == 0 ? (offsets[middle - 1] + offsets[middle]) * 0.5 : offsets[middle];
            List<double> centeredUncertainties = new List<double>();
            for (int chainIndex = startIndex; chainIndex <= endIndex; chainIndex++)
                centeredUncertainties.Add(Math.Abs(result.Chain[chainIndex].OffsetMs - run.OffsetMs) + result.Chain[chainIndex].UncertaintyMs);
            centeredUncertainties.Sort();
            int uncertaintyMiddle = centeredUncertainties.Count / 2;
            run.UncertaintyMs = Math.Max(1.0, centeredUncertainties.Count % 2 == 0
                ? (centeredUncertainties[uncertaintyMiddle - 1] + centeredUncertainties[uncertaintyMiddle]) * 0.5
                : centeredUncertainties[uncertaintyMiddle]);
            run.MinimumOffsetMs = minimumOffsetMs;
            run.MaximumOffsetMs = maximumOffsetMs;
            run.SourceStartPtsMs = result.Chain[startIndex].SourcePtsMs;
            run.SourceEndPtsMs = result.Chain[endIndex].SourcePtsMs;
            run.LanguageStartPtsMs = result.Chain[startIndex].LanguagePtsMs;
            run.LanguageEndPtsMs = result.Chain[endIndex].LanguagePtsMs;
            result.SupportRuns.Add(run);
        }

        /// <summary>
        /// Apre e fonde le regioni candidate senza attribuire loro una topologia locale
        /// </summary>
        /// <param name="result">Risultato temporale da aggiornare</param>
        /// <param name="sourceBlackRuns">Black run della timeline source, se disponibili</param>
        /// <param name="languageBlackRuns">Black run della timeline language, se disponibili</param>
        /// <param name="scale">Scala di conversione dai PTS source ai PTS language</param>
        private void BuildCandidateRegions(DeepSiftTemporalEvidenceResult result, IReadOnlyList<DeepBlackTimelineRun> sourceBlackRuns, IReadOnlyList<DeepBlackTimelineRun> languageBlackRuns, double scale)
        {
            List<DeepSiftTemporalCandidateRegion> rawRegions = new List<DeepSiftTemporalCandidateRegion>();
            for (int runIndex = 0; runIndex + 1 < result.SupportRuns.Count; runIndex++)
            {
                DeepSiftTemporalSupportRun before = result.SupportRuns[runIndex];
                DeepSiftTemporalSupportRun after = result.SupportRuns[runIndex + 1];
                if (before.MatchCount < this._options.MinimumDistinctSupport || after.MatchCount < this._options.MinimumDistinctSupport)
                    continue;
                DeepSiftTemporalCandidateRegion region = new DeepSiftTemporalCandidateRegion();
                region.BeforeSupportRunIndex = runIndex;
                region.AfterSupportRunIndex = runIndex + 1;
                region.FirstSliceIndex = result.Chain[before.LastChainIndex].SliceIndex;
                region.LastSliceIndex = result.Chain[after.FirstChainIndex].SliceIndex;
                DeepSiftTemporalSliceEvidence firstSlice = this.FindSlice(result.Slices, region.FirstSliceIndex);
                DeepSiftTemporalSliceEvidence lastSlice = this.FindSlice(result.Slices, region.LastSliceIndex);
                region.SourceStartPtsMs = firstSlice != null ? firstSlice.SourceStartPtsMs : result.Chain[before.LastChainIndex].SourcePtsMs;
                region.SourceEndPtsMs = lastSlice != null ? lastSlice.SourceEndPtsMs : result.Chain[after.FirstChainIndex].SourcePtsMs;
                region.LanguageStartPtsMs = (region.SourceStartPtsMs - before.OffsetMs) * scale;
                region.LanguageEndPtsMs = (region.SourceEndPtsMs - after.OffsetMs) * scale;
                region.OpenReasonFlags |= DeepSiftCandidateRegionReason.OffsetOutsideCurrentRegime;
                rawRegions.Add(region);
            }

            for (int sliceIndex = 0; sliceIndex < result.Slices.Count; sliceIndex++)
            {
                DeepSiftTemporalSliceEvidence slice = result.Slices[sliceIndex];
                int reliableModeCount = this.CountReliableModes(slice);
                bool ambiguous = slice.Modes.Count > 0 && reliableModeCount == 0;
                bool multimodal = reliableModeCount > 1;
                bool covered = this.IsCoveredBySupportRun(result, slice.Index);
                if (slice.Kind != DeepSiftTemporalSliceKind.Gap && !multimodal && !ambiguous && covered)
                    continue;

                DeepSiftTemporalCandidateRegion existing = this.FindRegionForSlice(rawRegions, slice.Index);
                if (existing == null)
                {
                    existing = new DeepSiftTemporalCandidateRegion();
                    existing.FirstSliceIndex = slice.Index;
                    existing.LastSliceIndex = slice.Index;
                    existing.SourceStartPtsMs = slice.SourceStartPtsMs;
                    existing.SourceEndPtsMs = slice.SourceEndPtsMs;
                    this.ResolveSurroundingSupportRuns(result, slice.Index, out int beforeRunIndex, out int afterRunIndex);
                    existing.BeforeSupportRunIndex = beforeRunIndex;
                    existing.AfterSupportRunIndex = afterRunIndex;
                    rawRegions.Add(existing);
                }
                if (slice.Kind == DeepSiftTemporalSliceKind.Gap)
                    existing.OpenReasonFlags |= DeepSiftCandidateRegionReason.ExplicitGap;
                if (multimodal)
                    existing.OpenReasonFlags |= DeepSiftCandidateRegionReason.MultimodalSlice;
                if (ambiguous)
                    existing.OpenReasonFlags |= DeepSiftCandidateRegionReason.TemporallyAmbiguousSupport;
                if (!covered)
                    existing.OpenReasonFlags |= DeepSiftCandidateRegionReason.InterruptedMonotoneCoverage;
            }

            this.AddBlackRunRegions(result, rawRegions, sourceBlackRuns, languageBlackRuns, scale);

            rawRegions.Sort((left, right) => left.SourceStartPtsMs != right.SourceStartPtsMs ? left.SourceStartPtsMs.CompareTo(right.SourceStartPtsMs) : left.SourceEndPtsMs.CompareTo(right.SourceEndPtsMs));
            for (int rawIndex = 0; rawIndex < rawRegions.Count; rawIndex++)
            {
                DeepSiftTemporalCandidateRegion raw = rawRegions[rawIndex];
                if (result.CandidateRegions.Count > 0 &&
                    raw.SourceStartPtsMs <= result.CandidateRegions[result.CandidateRegions.Count - 1].SourceEndPtsMs)
                {
                    this.MergeCandidateRegion(result.CandidateRegions[result.CandidateRegions.Count - 1], raw);
                    continue;
                }
                result.CandidateRegions.Add(raw);
            }

            for (int regionIndex = 0; regionIndex < result.CandidateRegions.Count; regionIndex++)
            {
                DeepSiftTemporalCandidateRegion region = result.CandidateRegions[regionIndex];
                region.Index = regionIndex;
                this.AddSliceEvidence(result.Slices, region);
                this.ResolveRegionLanguageBounds(result, region, scale);
                region.State = this.NeedsLocalResolution(result, region)
                    ? DeepSiftCandidateRegionState.PendingLocalResolution
                    : DeepSiftCandidateRegionState.GlobalDropout;
            }
        }

        /// <summary>
        /// Deriva i limiti temporali language dai modi globali o dai supporti adiacenti
        /// </summary>
        /// <param name="result">Risultato temporale con i support run disponibili</param>
        /// <param name="region">Regione candidata da aggiornare</param>
        /// <param name="scale">Scala di conversione dai PTS source ai PTS language</param>
        private void ResolveRegionLanguageBounds(DeepSiftTemporalEvidenceResult result, DeepSiftTemporalCandidateRegion region, double scale)
        {
            double languageStartMs = double.PositiveInfinity;
            double languageEndMs = double.NegativeInfinity;
            for (int modeIndex = 0; modeIndex < region.GlobalModes.Count; modeIndex++)
            {
                DeepSiftTemporalMode mode = region.GlobalModes[modeIndex];
                languageStartMs = Math.Min(languageStartMs, mode.LanguageStartPtsMs);
                languageEndMs = Math.Max(languageEndMs, mode.LanguageEndPtsMs);
            }
            if (!double.IsFinite(languageStartMs) || !double.IsFinite(languageEndMs))
            {
                double beforeOffsetMs = region.BeforeSupportRunIndex >= 0 ? result.SupportRuns[region.BeforeSupportRunIndex].OffsetMs : region.AfterSupportRunIndex >= 0 ? result.SupportRuns[region.AfterSupportRunIndex].OffsetMs : 0.0;
                double afterOffsetMs = region.AfterSupportRunIndex >= 0 ? result.SupportRuns[region.AfterSupportRunIndex].OffsetMs : beforeOffsetMs;
                languageStartMs = (region.SourceStartPtsMs - beforeOffsetMs) * scale;
                languageEndMs = (region.SourceEndPtsMs - afterOffsetMs) * scale;
            }
            region.LanguageStartPtsMs = Math.Max(0.0, Math.Min(languageStartMs, languageEndMs));
            region.LanguageEndPtsMs = Math.Max(region.LanguageStartPtsMs, Math.Max(languageStartMs, languageEndMs));
        }

        /// <summary>
        /// Richiede il full-rate soltanto quando l'anomalia può contenere una transizione osservabile
        /// </summary>
        /// <param name="result">Risultato temporale con i support run disponibili</param>
        /// <param name="region">Regione candidata da valutare</param>
        /// <returns>True quando la regione richiede una risoluzione locale full-rate</returns>
        private bool NeedsLocalResolution(DeepSiftTemporalEvidenceResult result, DeepSiftTemporalCandidateRegion region)
        {
            if (region.BeforeSupportRunIndex < 0 && region.AfterSupportRunIndex < 0)
                return false;
            if (region.BeforeSupportRunIndex < 0)
                return false;
            DeepSiftTemporalSupportRun before = result.SupportRuns[region.BeforeSupportRunIndex];
            bool hasBlackEvidence = (region.OpenReasonFlags & DeepSiftCandidateRegionReason.BlackRunDurationDifference) != 0;
            bool hasMultimodalEvidence = (region.OpenReasonFlags & DeepSiftCandidateRegionReason.MultimodalSlice) != 0;
            bool hasCompetingSupportedModes = this.HasCompetingSupportedModes(region);
            if (region.AfterSupportRunIndex < 0)
                return hasCompetingSupportedModes || this.HasReliableModeOutsideSupports(region, before, null);
            if (region.BeforeSupportRunIndex >= 0 && region.BeforeSupportRunIndex == region.AfterSupportRunIndex)
                return hasBlackEvidence ||
                       hasCompetingSupportedModes ||
                       (hasMultimodalEvidence && this.HasReliableModeOutsideSupports(region, before, null));
            DeepSiftTemporalSupportRun after = result.SupportRuns[region.AfterSupportRunIndex];
            return !this.SupportIntervalsOverlap(before, after) ||
                   hasBlackEvidence ||
                   hasCompetingSupportedModes ||
                   (hasMultimodalEvidence && this.HasReliableModeOutsideSupports(region, before, after));
        }

        /// <summary>
        /// Richiede il resolver locale quando l'ambiguità globale conserva almeno due ipotesi sostenute e incompatibili
        /// </summary>
        /// <param name="region">Regione candidata da esaminare</param>
        /// <returns>True quando la regione contiene modi sostenuti con intervalli di offset incompatibili</returns>
        private bool HasCompetingSupportedModes(DeepSiftTemporalCandidateRegion region)
        {
            for (int firstIndex = 0; firstIndex < region.GlobalModes.Count; firstIndex++)
            {
                DeepSiftTemporalMode first = region.GlobalModes[firstIndex];
                if (!this.HasDistinctSupport(first))
                    continue;
                for (int secondIndex = firstIndex + 1; secondIndex < region.GlobalModes.Count; secondIndex++)
                {
                    DeepSiftTemporalMode second = region.GlobalModes[secondIndex];
                    if (!this.HasDistinctSupport(second))
                        continue;
                    bool intervalsOverlap = first.OffsetMs - first.UncertaintyMs <= second.OffsetMs + second.UncertaintyMs &&
                                            second.OffsetMs - second.UncertaintyMs <= first.OffsetMs + first.UncertaintyMs;
                    if (!intervalsOverlap)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Verifica che un modo abbia supporto distinto sufficiente su entrambi gli assi temporali
        /// </summary>
        /// <param name="mode">Modo temporale da verificare</param>
        /// <returns>True quando il modo supera la soglia di supporto</returns>
        private bool HasDistinctSupport(DeepSiftTemporalMode mode)
        {
            return mode.Representative != null &&
                   mode.StrongDistinctSourceCount >= this._options.MinimumDistinctSupport &&
                   mode.StrongDistinctLanguageCount >= this._options.MinimumDistinctSupport;
        }

        /// <summary>
        /// Cerca nella regione un modo affidabile incompatibile con i supporti adiacenti
        /// </summary>
        /// <param name="region">Regione candidata</param>
        /// <param name="first">Support run precedente</param>
        /// <param name="second">Support run successivo, se presente</param>
        /// <returns>True quando la regione contiene un offset affidabile distinto</returns>
        private bool HasReliableModeOutsideSupports(DeepSiftTemporalCandidateRegion region, DeepSiftTemporalSupportRun first, DeepSiftTemporalSupportRun second)
        {
            for (int modeIndex = 0; modeIndex < region.GlobalModes.Count; modeIndex++)
            {
                DeepSiftTemporalMode mode = region.GlobalModes[modeIndex];
                if (!this.IsReliableMode(mode))
                    continue;
                if (this.OffsetIntersectsSupport(mode.OffsetMs, mode.UncertaintyMs, first))
                    continue;
                if (second != null && this.OffsetIntersectsSupport(mode.OffsetMs, mode.UncertaintyMs, second))
                    continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Verifica il supporto distinto e l'assenza di ambiguità temporale di un modo
        /// </summary>
        /// <param name="mode">Modo temporale da verificare</param>
        /// <returns>True quando il modo è utilizzabile come evidenza affidabile</returns>
        private bool IsReliableMode(DeepSiftTemporalMode mode)
        {
            return this.HasDistinctSupport(mode) &&
                   !mode.TemporallyAmbiguous;
        }

        /// <summary>
        /// Apre regioni quando run nere corrispondenti implicano una variazione oltre l'incertezza PTS
        /// </summary>
        /// <param name="result">Risultato temporale con i support run disponibili</param>
        /// <param name="regions">Regioni candidate da aggiornare</param>
        /// <param name="sourceRuns">Black run della timeline source, se disponibili</param>
        /// <param name="languageRuns">Black run della timeline language, se disponibili</param>
        /// <param name="scale">Scala di conversione dai PTS source ai PTS language</param>
        private void AddBlackRunRegions(DeepSiftTemporalEvidenceResult result, List<DeepSiftTemporalCandidateRegion> regions, IReadOnlyList<DeepBlackTimelineRun> sourceRuns, IReadOnlyList<DeepBlackTimelineRun> languageRuns, double scale)
        {
            if (sourceRuns == null || languageRuns == null)
                return;
            for (int sourceIndex = 0; sourceIndex < sourceRuns.Count; sourceIndex++)
            {
                DeepBlackTimelineRun sourceRun = sourceRuns[sourceIndex];
                int beforeRunIndex = this.FindSupportRunBefore(result.SupportRuns, sourceRun.StartPtsMs);
                if (beforeRunIndex < 0)
                    continue;
                DeepSiftTemporalSupportRun before = result.SupportRuns[beforeRunIndex];
                int afterRunIndex = this.FindSupportRunAfter(result.SupportRuns, sourceRun.EndPtsMs);
                DeepSiftTemporalSupportRun after = afterRunIndex >= 0 ? result.SupportRuns[afterRunIndex] : null;
                double uncertaintyMs = before.UncertaintyMs + (after != null ? after.UncertaintyMs : before.UncertaintyMs);
                double impliedAfterUncertaintyMs = after != null ? after.UncertaintyMs : before.UncertaintyMs;
                double expectedLanguageStartMs = (sourceRun.StartPtsMs - before.OffsetMs) * scale;
                for (int languageIndex = 0; languageIndex < languageRuns.Count; languageIndex++)
                {
                    DeepBlackTimelineRun languageRun = languageRuns[languageIndex];
                    if (Math.Abs(languageRun.StartPtsMs - expectedLanguageStartMs) > uncertaintyMs * scale)
                        continue;
                    double impliedAfterOffsetMs = sourceRun.EndPtsMs - (languageRun.EndPtsMs / scale);
                    if (this.OffsetIntersectsSupport(impliedAfterOffsetMs, impliedAfterUncertaintyMs, before))
                        continue;
                    int candidateAfterRunIndex = afterRunIndex;
                    if (after != null && Math.Abs(languageRun.EndPtsMs - ((sourceRun.EndPtsMs - after.OffsetMs) * scale)) > uncertaintyMs * scale)
                        candidateAfterRunIndex = -1;

                    DeepSiftTemporalCandidateRegion region = new DeepSiftTemporalCandidateRegion();
                    region.BeforeSupportRunIndex = beforeRunIndex;
                    region.AfterSupportRunIndex = candidateAfterRunIndex;
                    region.SourceStartPtsMs = sourceRun.StartPtsMs;
                    region.SourceEndPtsMs = sourceRun.EndPtsMs;
                    region.LanguageStartPtsMs = languageRun.StartPtsMs;
                    region.LanguageEndPtsMs = languageRun.EndPtsMs;
                    region.FirstSliceIndex = this.FindSliceIndex(result.Slices, sourceRun.StartPtsMs);
                    region.LastSliceIndex = this.FindSliceIndex(result.Slices, sourceRun.EndPtsMs);
                    region.OpenReasonFlags |= DeepSiftCandidateRegionReason.BlackRunDurationDifference;
                    region.SourceBlackRuns.Add(sourceRun);
                    region.LanguageBlackRuns.Add(languageRun);
                    region.BlackDerivedSearchOffsetsMs.Add(before.OffsetMs);
                    region.BlackDerivedSearchOffsetsMs.Add(impliedAfterOffsetMs);
                    regions.Add(region);
                }
            }
        }

        /// <summary>
        /// Trova l'ultimo support run terminato non oltre il PTS source indicato
        /// </summary>
        /// <param name="runs">Support run ordinati</param>
        /// <param name="sourcePtsMs">PTS source di riferimento</param>
        /// <returns>Indice del supporto precedente oppure -1</returns>
        private int FindSupportRunBefore(IReadOnlyList<DeepSiftTemporalSupportRun> runs, double sourcePtsMs)
        {
            int result = -1;
            for (int runIndex = 0; runIndex < runs.Count; runIndex++)
            {
                if (runs[runIndex].SourceEndPtsMs > sourcePtsMs)
                    break;
                result = runIndex;
            }
            return result;
        }

        /// <summary>
        /// Verifica l'intersezione fra gli intervalli di offset di due support run
        /// </summary>
        /// <param name="first">Primo support run</param>
        /// <param name="second">Secondo support run</param>
        /// <returns>True quando gli intervalli si sovrappongono</returns>
        private bool SupportIntervalsOverlap(DeepSiftTemporalSupportRun first, DeepSiftTemporalSupportRun second)
        {
            return first.MinimumOffsetMs <= second.MaximumOffsetMs && second.MinimumOffsetMs <= first.MaximumOffsetMs;
        }

        /// <summary>
        /// Verifica se un intervallo di offset interseca l'intervallo di un support run
        /// </summary>
        /// <param name="offsetMs">Centro dell'offset</param>
        /// <param name="uncertaintyMs">Semilarghezza dell'intervallo</param>
        /// <param name="support">Support run da confrontare</param>
        /// <returns>True quando gli intervalli si sovrappongono</returns>
        private bool OffsetIntersectsSupport(double offsetMs, double uncertaintyMs, DeepSiftTemporalSupportRun support)
        {
            return offsetMs - uncertaintyMs <= support.MaximumOffsetMs && support.MinimumOffsetMs <= offsetMs + uncertaintyMs;
        }

        /// <summary>
        /// Trova il primo support run iniziato non prima del PTS source indicato
        /// </summary>
        /// <param name="runs">Support run ordinati</param>
        /// <param name="sourcePtsMs">PTS source di riferimento</param>
        /// <returns>Indice del supporto successivo oppure -1</returns>
        private int FindSupportRunAfter(IReadOnlyList<DeepSiftTemporalSupportRun> runs, double sourcePtsMs)
        {
            for (int runIndex = 0; runIndex < runs.Count; runIndex++)
            {
                if (runs[runIndex].SourceStartPtsMs >= sourcePtsMs)
                    return runIndex;
            }
            return -1;
        }

        /// <summary>
        /// Determina l'indice logico della slice che contiene il PTS source richiesto
        /// </summary>
        /// <param name="slices">Slice temporali ordinate</param>
        /// <param name="sourcePtsMs">PTS source di riferimento</param>
        /// <returns>Indice logico della slice contenente il PTS, dell'ultima slice disponibile oppure 0 se la lista è vuota</returns>
        private int FindSliceIndex(IReadOnlyList<DeepSiftTemporalSliceEvidence> slices, double sourcePtsMs)
        {
            for (int sliceIndex = 0; sliceIndex < slices.Count; sliceIndex++)
            {
                if (sourcePtsMs >= slices[sliceIndex].SourceStartPtsMs && sourcePtsMs <= slices[sliceIndex].SourceEndPtsMs)
                    return slices[sliceIndex].Index;
            }
            return slices.Count > 0 ? slices[slices.Count - 1].Index : 0;
        }

        /// <summary>
        /// Recupera una slice tramite il suo indice logico
        /// </summary>
        /// <param name="slices">Slice temporali disponibili</param>
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
        /// Trova la regione candidata che contiene l'indice di una slice
        /// </summary>
        /// <param name="regions">Regioni candidate ordinate</param>
        /// <param name="sliceIndex">Indice logico della slice</param>
        /// <returns>Regione contenente la slice oppure null</returns>
        private DeepSiftTemporalCandidateRegion FindRegionForSlice(IReadOnlyList<DeepSiftTemporalCandidateRegion> regions, int sliceIndex)
        {
            for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
            {
                if (sliceIndex >= regions[regionIndex].FirstSliceIndex && sliceIndex <= regions[regionIndex].LastSliceIndex)
                    return regions[regionIndex];
            }
            return null;
        }

        /// <summary>
        /// Verifica se una slice appartiene alla copertura di almeno un support run
        /// </summary>
        /// <param name="result">Risultato temporale corrente</param>
        /// <param name="sliceIndex">Indice logico della slice</param>
        /// <returns>True quando almeno un support run copre la slice</returns>
        private bool IsCoveredBySupportRun(DeepSiftTemporalEvidenceResult result, int sliceIndex)
        {
            for (int runIndex = 0; runIndex < result.SupportRuns.Count; runIndex++)
            {
                DeepSiftTemporalSupportRun run = result.SupportRuns[runIndex];
                int firstSliceIndex = result.Chain[run.FirstChainIndex].SliceIndex;
                int lastSliceIndex = result.Chain[run.LastChainIndex].SliceIndex;
                if (sliceIndex >= firstSliceIndex && sliceIndex <= lastSliceIndex)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Risolve i support run immediatamente precedente e successivo rispetto a una slice
        /// </summary>
        /// <param name="result">Risultato temporale corrente</param>
        /// <param name="sliceIndex">Indice logico della slice</param>
        /// <param name="beforeRunIndex">Indice di output del support run precedente oppure -1</param>
        /// <param name="afterRunIndex">Indice di output del support run successivo oppure -1</param>
        private void ResolveSurroundingSupportRuns(DeepSiftTemporalEvidenceResult result, int sliceIndex, out int beforeRunIndex, out int afterRunIndex)
        {
            beforeRunIndex = -1;
            afterRunIndex = -1;
            for (int runIndex = 0; runIndex < result.SupportRuns.Count; runIndex++)
            {
                DeepSiftTemporalSupportRun run = result.SupportRuns[runIndex];
                int firstSliceIndex = result.Chain[run.FirstChainIndex].SliceIndex;
                int lastSliceIndex = result.Chain[run.LastChainIndex].SliceIndex;
                if (firstSliceIndex < sliceIndex)
                    beforeRunIndex = runIndex;
                if (lastSliceIndex > sliceIndex && afterRunIndex < 0)
                    afterRunIndex = runIndex;
            }
        }

        /// <summary>
        /// Unisce limiti, motivi ed evidenze di due regioni candidate sovrapposte
        /// </summary>
        /// <param name="target">Regione aggregata da aggiornare</param>
        /// <param name="source">Regione da incorporare</param>
        private void MergeCandidateRegion(DeepSiftTemporalCandidateRegion target, DeepSiftTemporalCandidateRegion source)
        {
            double targetStartPtsMs = target.SourceStartPtsMs;
            double targetEndPtsMs = target.SourceEndPtsMs;
            if (source.SourceStartPtsMs < targetStartPtsMs)
                target.BeforeSupportRunIndex = source.BeforeSupportRunIndex;
            else if (Math.Abs(source.SourceStartPtsMs - targetStartPtsMs) <= 0.001)
                target.BeforeSupportRunIndex = target.BeforeSupportRunIndex < 0 || source.BeforeSupportRunIndex < 0 ? -1 : Math.Min(target.BeforeSupportRunIndex, source.BeforeSupportRunIndex);
            if (source.SourceEndPtsMs > targetEndPtsMs)
                target.AfterSupportRunIndex = source.AfterSupportRunIndex;
            else if (Math.Abs(source.SourceEndPtsMs - targetEndPtsMs) <= 0.001)
                target.AfterSupportRunIndex = target.AfterSupportRunIndex < 0 || source.AfterSupportRunIndex < 0 ? -1 : Math.Max(target.AfterSupportRunIndex, source.AfterSupportRunIndex);
            target.FirstSliceIndex = Math.Min(target.FirstSliceIndex, source.FirstSliceIndex);
            target.LastSliceIndex = Math.Max(target.LastSliceIndex, source.LastSliceIndex);
            target.SourceStartPtsMs = Math.Min(target.SourceStartPtsMs, source.SourceStartPtsMs);
            target.SourceEndPtsMs = Math.Max(target.SourceEndPtsMs, source.SourceEndPtsMs);
            target.LanguageStartPtsMs = Math.Min(target.LanguageStartPtsMs, source.LanguageStartPtsMs);
            target.LanguageEndPtsMs = Math.Max(target.LanguageEndPtsMs, source.LanguageEndPtsMs);
            target.OpenReasonFlags |= source.OpenReasonFlags;
            this.AppendDistinctOffsets(target.BlackDerivedSearchOffsetsMs, source.BlackDerivedSearchOffsetsMs);
            this.AppendDistinctBlackRuns(target.SourceBlackRuns, source.SourceBlackRuns);
            this.AppendDistinctBlackRuns(target.LanguageBlackRuns, source.LanguageBlackRuns);
        }

        /// <summary>
        /// Accoda gli offset non ancora presenti entro la precisione temporale prevista
        /// </summary>
        /// <param name="target">Lista di destinazione</param>
        /// <param name="source">Offset da incorporare</param>
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
        /// Accoda i black run con estremi temporali distinti
        /// </summary>
        /// <param name="target">Lista di destinazione</param>
        /// <param name="source">Black run da incorporare</param>
        private void AppendDistinctBlackRuns(List<DeepBlackTimelineRun> target, IReadOnlyList<DeepBlackTimelineRun> source)
        {
            for (int sourceIndex = 0; sourceIndex < source.Count; sourceIndex++)
            {
                DeepBlackTimelineRun candidate = source[sourceIndex];
                bool exists = false;
                for (int targetIndex = 0; targetIndex < target.Count; targetIndex++)
                    exists |= Math.Abs(target[targetIndex].StartPtsMs - candidate.StartPtsMs) <= 0.001 && Math.Abs(target[targetIndex].EndPtsMs - candidate.EndPtsMs) <= 0.001;
                if (!exists)
                    target.Add(candidate);
            }
        }

        /// <summary>
        /// Accumula nella regione i modi, i conteggi e i motivi osservati nelle slice coperte
        /// </summary>
        /// <param name="slices">Slice temporali complessive</param>
        /// <param name="region">Regione candidata da aggiornare</param>
        private void AddSliceEvidence(IReadOnlyList<DeepSiftTemporalSliceEvidence> slices, DeepSiftTemporalCandidateRegion region)
        {
            for (int sliceIndex = 0; sliceIndex < slices.Count; sliceIndex++)
            {
                DeepSiftTemporalSliceEvidence slice = slices[sliceIndex];
                if (slice.Index < region.FirstSliceIndex || slice.Index > region.LastSliceIndex)
                    continue;
                if (slice.Kind == DeepSiftTemporalSliceKind.Gap)
                    region.OpenReasonFlags |= DeepSiftCandidateRegionReason.ExplicitGap;
                int reliableModeCount = this.CountReliableModes(slice);
                if (reliableModeCount > 1)
                    region.OpenReasonFlags |= DeepSiftCandidateRegionReason.MultimodalSlice;
                if (slice.Modes.Count > 0 && reliableModeCount == 0)
                    region.OpenReasonFlags |= DeepSiftCandidateRegionReason.TemporallyAmbiguousSupport;
                for (int modeIndex = 0; modeIndex < slice.Modes.Count; modeIndex++)
                {
                    DeepSiftTemporalMode mode = slice.Modes[modeIndex];
                    region.GlobalModes.Add(mode);
                    region.AcceptedPairCount += mode.AcceptedPairCount;
                    region.StrongPairCount += mode.StrongPairCount;
                    region.AmbiguousPairCount += mode.AmbiguousPairCount;
                }
            }
        }

        /// <summary>
        /// Conta i modi affidabili presenti nella slice
        /// </summary>
        /// <param name="slice">Slice da esaminare</param>
        /// <returns>Numero di modi affidabili</returns>
        private int CountReliableModes(DeepSiftTemporalSliceEvidence slice)
        {
            int result = 0;
            for (int modeIndex = 0; modeIndex < slice.Modes.Count; modeIndex++)
            {
                DeepSiftTemporalMode mode = slice.Modes[modeIndex];
                if (this.IsReliableMode(mode))
                    result++;
            }
            return result;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Stato dinamico di un percorso globale terminante sul match corrente
        /// </summary>
        private sealed class GlobalPathState
        {
            /// <summary>
            /// Inizializza lo stato del percorso con il primo match
            /// </summary>
            /// <param name="match">Primo match del percorso</param>
            public GlobalPathState(DeepSiftTemporalChainMatch match)
            {
                this.MatchCount = 1;
                this.SupportCount = match.SupportCount;
                this.Score = match.Score;
                this.FirstSourcePtsMs = match.SourcePtsMs;
                this.FirstLanguagePtsMs = match.LanguagePtsMs;
                this.PreviousIndex = -1;
            }

            /// <summary>
            /// Costruisce uno stato vuoto destinato all'estensione di un percorso
            /// </summary>
            private GlobalPathState()
            {
            }

            /// <summary>
            /// Numero di match nel percorso
            /// </summary>
            public int MatchCount { get; private set; }

            /// <summary>
            /// Supporto distinto complessivo accumulato nel percorso
            /// </summary>
            public int SupportCount { get; private set; }

            /// <summary>
            /// Copertura temporale comune del percorso in millisecondi source
            /// </summary>
            public double CoverageMs { get; private set; }

            /// <summary>
            /// Somma degli score dei match del percorso
            /// </summary>
            public double Score { get; private set; }

            /// <summary>
            /// PTS source iniziale del percorso
            /// </summary>
            public double FirstSourcePtsMs { get; private set; }

            /// <summary>
            /// PTS language iniziale del percorso
            /// </summary>
            public double FirstLanguagePtsMs { get; private set; }

            /// <summary>
            /// Indice dello stato predecessore oppure -1
            /// </summary>
            public int PreviousIndex { get; private set; }

            /// <summary>
            /// Estende lo stato del percorso con un match compatibile
            /// </summary>
            /// <param name="current">Nuovo match</param>
            /// <param name="previousIndex">Indice dello stato predecessore</param>
            /// <param name="scale">Scala temporale source-language</param>
            /// <returns>Nuovo stato esteso</returns>
            public GlobalPathState Append(DeepSiftTemporalChainMatch current, int previousIndex, double scale)
            {
                GlobalPathState result = new GlobalPathState();
                result.MatchCount = this.MatchCount + 1;
                result.SupportCount = this.SupportCount + current.SupportCount;
                result.CoverageMs = Math.Min(current.SourcePtsMs - this.FirstSourcePtsMs, (current.LanguagePtsMs - this.FirstLanguagePtsMs) / scale);
                result.Score = this.Score + current.Score;
                result.FirstSourcePtsMs = this.FirstSourcePtsMs;
                result.FirstLanguagePtsMs = this.FirstLanguagePtsMs;
                result.PreviousIndex = previousIndex;
                return result;
            }

            /// <summary>
            /// Confronta due stati di percorso secondo l'ordinamento canonico del solver
            /// </summary>
            /// <param name="other">Stato alternativo</param>
            /// <returns>True quando lo stato corrente è preferibile</returns>
            public bool IsBetterThan(GlobalPathState other)
            {
                if (this.MatchCount != other.MatchCount)
                    return this.MatchCount > other.MatchCount;
                if (this.SupportCount != other.SupportCount)
                    return this.SupportCount > other.SupportCount;
                long coverage = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(this.CoverageMs);
                long otherCoverage = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(other.CoverageMs);
                if (coverage != otherCoverage)
                    return coverage > otherCoverage;
                long score = DeepSiftTemporalMetricComparer.QuantizeMetric(this.Score);
                long otherScore = DeepSiftTemporalMetricComparer.QuantizeMetric(other.Score);
                if (score != otherScore)
                    return score > otherScore;
                long firstSourcePts = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(this.FirstSourcePtsMs);
                long otherFirstSourcePts = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(other.FirstSourcePtsMs);
                if (firstSourcePts != otherFirstSourcePts)
                    return firstSourcePts < otherFirstSourcePts;
                return DeepSiftTemporalMetricComparer.QuantizeMilliseconds(this.FirstLanguagePtsMs) < DeepSiftTemporalMetricComparer.QuantizeMilliseconds(other.FirstLanguagePtsMs);
            }
        }

        #endregion

    }
}
