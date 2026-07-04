using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Raffina le transizioni tra regioni DeepAnalysis e produce operazioni EditMap
    /// </summary>
    public class DeepTransitionRefiner
    {
        #region Delegati

        /// <summary>
        /// Calcola il raggio di ricerca locale tra due regioni offset
        /// </summary>
        /// <param name="current">Regione corrente</param>
        /// <param name="next">Regione successiva</param>
        /// <returns>Raggio ricerca in secondi</returns>
        public delegate double TransitionRadiusResolver(OffsetRegion current, OffsetRegion next);

        /// <summary>
        /// Cerca un crossover confrontando due offset candidati con metrica differenziale
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="langFile">File lingua</param>
        /// <param name="searchStartSrc">Inizio ricerca source</param>
        /// <param name="searchEndSrc">Fine ricerca source</param>
        /// <param name="oldOffsetSec">Offset precedente</param>
        /// <param name="newOffsetSec">Offset successivo</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="transition">Diagnostica transizione da popolare</param>
        /// <returns>Crossover source in secondi, oppure -1</returns>
        public delegate double DifferentialCrossoverScanner(string sourceFile, string langFile, double searchStartSrc, double searchEndSrc, double oldOffsetSec, double newOffsetSec, double inverseRatio, DeepAnalysisTransitionDiagnostic transition);

        /// <summary>
        /// Conferma linearmente un crossover approssimativo
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="langFile">File lingua</param>
        /// <param name="approximateSrc">Crossover approssimativo source</param>
        /// <param name="oldOffsetSec">Offset precedente</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <returns>Crossover confermato source</returns>
        public delegate double LinearCrossoverConfirmer(string sourceFile, string langFile, double approximateSrc, double oldOffsetSec, double inverseRatio);

        /// <summary>
        /// Verifica localmente una transizione candidata
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="langFile">File lingua</param>
        /// <param name="crossoverSrcSec">Crossover source</param>
        /// <param name="oldOffsetSec">Offset precedente</param>
        /// <param name="newOffsetSec">Offset successivo</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <returns>Diagnostica verifica locale</returns>
        public delegate DeepAnalysisLocalVerificationDiagnostic LocalTransitionVerifier(string sourceFile, string langFile, double crossoverSrcSec, double oldOffsetSec, double newOffsetSec, double inverseRatio);

        #endregion

        #region Variabili di classe

        private readonly TransitionRadiusResolver _radiusResolver;

        private readonly DifferentialCrossoverScanner _visualCrossoverScanner;

        private readonly DeepVisualFrameAnalyzer _visualFrameAnalyzer;

        private readonly LinearCrossoverConfirmer _linearCrossoverConfirmer;

        private readonly LocalTransitionVerifier _localTransitionVerifier;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="radiusResolver">Risolutore raggio transizione</param>
        /// <param name="visualCrossoverScanner">Scanner crossover visuale</param>
        /// <param name="visualFrameAnalyzer">Analyzer visuale frame-based</param>
        /// <param name="linearCrossoverConfirmer">Confermatore lineare</param>
        /// <param name="localTransitionVerifier">Verificatore locale</param>
        public DeepTransitionRefiner(TransitionRadiusResolver radiusResolver, DifferentialCrossoverScanner visualCrossoverScanner, DeepVisualFrameAnalyzer visualFrameAnalyzer, LinearCrossoverConfirmer linearCrossoverConfirmer, LocalTransitionVerifier localTransitionVerifier)
        {
            this._radiusResolver = radiusResolver;
            this._visualCrossoverScanner = visualCrossoverScanner;
            this._visualFrameAnalyzer = visualFrameAnalyzer;
            this._linearCrossoverConfirmer = linearCrossoverConfirmer;
            this._localTransitionVerifier = localTransitionVerifier;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Raffina i punti di transizione tramite scansione locale video
        /// </summary>
        public List<EditOperation> Refine(string sourceFile, string langFile, List<OffsetRegion> regions, double inverseRatio, DeepAnalysisPerformanceDiagnostic performanceDiagnostics, bool timelineMode, bool geometryCropSourceToFourThree, bool geometryCropLanguageToFourThree, out List<DeepAnalysisTransitionDiagnostic> transitions)
        {
            List<EditOperation> operations = new List<EditOperation>();
            transitions = new List<DeepAnalysisTransitionDiagnostic>();
            double oldOffsetSec;
            double newOffsetSec;
            double bestCrossover;
            double breakpointSrc;
            double searchStartSrc;
            double searchEndSrc;
            double searchRadiusSec;
            double validationStartSrc;
            int durationMs;
            int minOffsetChangeMs;
            int langTimestampMs;
            int sourceTimestampMs;
            string operationType;
            string refineMethod;
            double boundaryToleranceSec;
            double unsupportedGapStartSrc;
            double unsupportedGapEndSrc;
            bool strongDifferentialAccepted;
            int operationDurationMs;
            double effectiveOffsetSec = regions.Count > 0 ? regions[0].OffsetMs / 1000.0 : 0.0;
            DeepAnalysisTransitionDiagnostic transition;
            List<double> acceptedOffsetBeforeSec = new List<double>();
            List<DeepAnalysisTransitionDiagnostic> acceptedTransitions = new List<DeepAnalysisTransitionDiagnostic>();
            // Ogni coppia di regioni adiacenti può generare un cut o un insert silence
            for (int r = 0; r < regions.Count - 1; r++)
            {
                performanceDiagnostics.TransitionRefineCount++;
                oldOffsetSec = effectiveOffsetSec;
                newOffsetSec = regions[r + 1].OffsetMs / 1000.0;
                durationMs = (int)Math.Abs(Math.Round((newOffsetSec - oldOffsetSec) * 1000.0));
                transition = new DeepAnalysisTransitionDiagnostic();
                transition.Index = r + 1;
                transition.OldOffsetMs = (int)Math.Round(oldOffsetSec * 1000.0);
                transition.NewOffsetMs = regions[r + 1].OffsetMs;
                transition.DeltaMs = (int)Math.Round((newOffsetSec - oldOffsetSec) * 1000.0);
                transition.DurationMs = durationMs;
                transitions.Add(transition);

                minOffsetChangeMs = 100;

                // Delta molto piccoli sono rumore rispetto alla precisione effettiva della timeline
                if (durationMs < minOffsetChangeMs)
                {
                    transition.Status = "Skipped";
                    transition.RejectReason = "Delta sotto soglia timeline";
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Transizione " + (r + 1) + ": delta offset " + durationMs + "ms sotto soglia=" + minOffsetChangeMs + ", skip");
                    continue;
                }

                breakpointSrc = (regions[r].EndSrcSec + regions[r + 1].StartSrcSec) / 2.0;
                searchRadiusSec = this._radiusResolver(regions[r], regions[r + 1]);
                searchStartSrc = breakpointSrc - searchRadiusSec;
                searchEndSrc = breakpointSrc + searchRadiusSec;
                transition.BreakpointSrcSec = breakpointSrc;
                transition.SearchStartSrcSec = searchStartSrc;
                transition.SearchEndSrcSec = searchEndSrc;

                // La ricerca resta confinata alle due regioni coinvolte
                if (searchStartSrc < regions[r].StartSrcSec) { searchStartSrc = regions[r].StartSrcSec; }
                if (searchEndSrc > regions[r + 1].EndSrcSec) { searchEndSrc = regions[r + 1].EndSrcSec; }
                if (searchStartSrc < 0.0) { searchStartSrc = 0.0; }

                unsupportedGapStartSrc = regions[r].SupportEndSrcSec;
                unsupportedGapEndSrc = regions[r + 1].SupportStartSrcSec;
                if (timelineMode && unsupportedGapStartSrc > 0.0 && unsupportedGapEndSrc > unsupportedGapStartSrc && (unsupportedGapEndSrc - unsupportedGapStartSrc) > (searchEndSrc - searchStartSrc))
                {
                    searchStartSrc = newOffsetSec < oldOffsetSec ? Math.Max(regions[r].StartSrcSec, unsupportedGapStartSrc - Math.Max(10.0, (durationMs / 1000.0) + 2.0)) : Math.Max(regions[r].StartSrcSec, unsupportedGapEndSrc);

                    searchEndSrc = unsupportedGapEndSrc + 90.0;
                    if (searchEndSrc > regions[r + 1].EndSrcSec) { searchEndSrc = regions[r + 1].EndSrcSec; }
                }
                if (timelineMode && regions[r].SupportEndSrcSec > regions[r + 1].SupportStartSrcSec)
                {
                    // Anchor sovrapposti indicano una zona ambigua: il boundary reale può essere molto dopo il primo anchor del nuovo plateau
                    searchStartSrc = Math.Min(searchStartSrc, regions[r + 1].SupportStartSrcSec);
                    searchEndSrc = Math.Max(searchEndSrc, Math.Min(regions[r + 1].SupportEndSrcSec, regions[r + 1].SupportStartSrcSec + 180.0));
                    if (searchEndSrc > regions[r + 1].EndSrcSec) { searchEndSrc = regions[r + 1].EndSrcSec; }
                }
                if (timelineMode && regions[r + 1].MatchCount <= 1 && regions[r + 1].SupportEndSrcSec > searchEndSrc)
                {
                    searchEndSrc = regions[r + 1].SupportEndSrcSec;
                    if (searchEndSrc > regions[r + 1].EndSrcSec) { searchEndSrc = regions[r + 1].EndSrcSec; }
                }

                transition.SearchStartSrcSec = searchStartSrc;
                transition.SearchEndSrcSec = searchEndSrc;

                if (searchEndSrc <= searchStartSrc || (searchEndSrc - searchStartSrc) < 1.0)
                {
                    transition.Status = "Rejected";
                    transition.RejectReason = "Finestra refine invalida";
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  Transizione " + (r + 1) + ": finestra refine invalida attorno a src " + breakpointSrc.ToString("F1", CultureInfo.InvariantCulture) + "s, skip");
                    continue;
                }

                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Transizione " + (r + 1) + ": scansione densa in src " + searchStartSrc.ToString("F1", CultureInfo.InvariantCulture) + "-" + searchEndSrc.ToString("F1", CultureInfo.InvariantCulture) + "s, breakpoint " + breakpointSrc.ToString("F1", CultureInfo.InvariantCulture) + "s (offset " + ((int)(oldOffsetSec * 1000)) + " -> " + ((int)(newOffsetSec * 1000)) + "ms)");

                validationStartSrc = searchStartSrc;
                refineMethod = "";
                bestCrossover = -1.0;

                if (timelineMode &&
                    newOffsetSec > oldOffsetSec &&
                    durationMs > 1500 &&
                    regions[r + 1].MatchCount <= 1 &&
                    Math.Abs(regions[r].EndSrcSec - regions[r + 1].StartSrcSec) <= 0.001)
                {
                    // Le regioni di tail recovery possono arrivare già con un boundary frame-confirmed.
                    // In quel caso il primo match post-gap è troppo tardo per l'operazione INSERT.
                    bestCrossover = regions[r + 1].StartSrcSec;
                    refineMethod = "frame-boundary";
                }

                if (bestCrossover < 0.0)
                {
                    // Primo fallback visuale: confronto differenziale tra vecchio e nuovo offset
                    bestCrossover = this._visualCrossoverScanner(sourceFile, langFile, searchStartSrc, searchEndSrc, oldOffsetSec, newOffsetSec, inverseRatio, transition);
                    if (bestCrossover >= 0.0)
                    {
                        refineMethod = "visual-differential";
                        performanceDiagnostics.TransitionVisualRefineCount++;
                    }
                }

                if (bestCrossover < 0.0 && timelineMode)
                {
                    // In timeline video-only i frame ripetuti aiutano su anime e VFR con pose statiche
                    bestCrossover = this._visualFrameAnalyzer.RepeatedFrameCrossover(sourceFile, langFile, searchStartSrc, searchEndSrc, oldOffsetSec, newOffsetSec, inverseRatio, geometryCropSourceToFourThree, geometryCropLanguageToFourThree);
                    if (bestCrossover >= 0.0)
                    {
                        refineMethod = "repeated-frame";
                        performanceDiagnostics.TransitionVisualRefineCount++;
                    }
                }

                if (bestCrossover < 0.0)
                {
                    // Ultimo percorso: dip denso e conferma lineare sul tratto locale
                    bestCrossover = this._visualFrameAnalyzer.DenseScanCrossover(sourceFile, langFile, searchStartSrc, searchEndSrc, oldOffsetSec, inverseRatio, geometryCropSourceToFourThree, geometryCropLanguageToFourThree);
                    bestCrossover = this._linearCrossoverConfirmer(sourceFile, langFile, bestCrossover, oldOffsetSec, inverseRatio);
                    refineMethod = "dense-linear";
                    performanceDiagnostics.TransitionDenseLinearRefineCount++;
                }
                boundaryToleranceSec = timelineMode ? Math.Max(2.0, (durationMs / 1000.0) + 1.5) : 0.0;
                if (bestCrossover < validationStartSrc - boundaryToleranceSec || bestCrossover > searchEndSrc + boundaryToleranceSec)
                {
                    transition.Status = "Rejected";
                    transition.RejectReason = "Crossover fuori finestra";
                    transition.ValidationStartSrcSec = validationStartSrc;
                    transition.CrossoverSrcSec = bestCrossover;
                    transition.AudioCrossover = false;
                    transition.RefineMethod = refineMethod;
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  Transizione " + (r + 1) + ": crossover fuori finestra (" + bestCrossover.ToString("F2", CultureInfo.InvariantCulture) + "s fuori " + validationStartSrc.ToString("F1", CultureInfo.InvariantCulture) + "-" + searchEndSrc.ToString("F1", CultureInfo.InvariantCulture) + "s), skip");
                    continue;
                }

                sourceTimestampMs = (int)Math.Round(bestCrossover * 1000.0);
                langTimestampMs = (int)Math.Round((bestCrossover - oldOffsetSec) * 1000.0);
                if (Math.Abs(inverseRatio - 1.0) > 0.0001)
                {
                    langTimestampMs = (int)Math.Round(langTimestampMs * inverseRatio);
                }

                operationType = newOffsetSec > oldOffsetSec ? EditOperation.INSERT_SILENCE : EditOperation.CUT_SEGMENT;
                operationDurationMs = EditMapTimelineHelper.SourceDurationToLanguageDurationMs(durationMs, inverseRatio);

                // L'operazione viene aggiunta prima della verifica per mantenere diagnostica completa e poi rimossa se non valida
                EditOperation op = new EditOperation();
                op.Type = operationType;
                op.LangTimestampMs = langTimestampMs;
                op.DurationMs = operationDurationMs;
                op.SourceTimestampMs = sourceTimestampMs;
                op.VisualSourceTimestampMs = sourceTimestampMs;
                operations.Add(op);

                transition.Status = "Accepted";
                transition.ValidationStartSrcSec = validationStartSrc;
                transition.AudioCrossover = false;
                transition.RefineMethod = refineMethod;
                transition.CrossoverSrcSec = bestCrossover;
                transition.OperationType = operationType;
                transition.LangTimestampMs = langTimestampMs;
                transition.SourceTimestampMs = sourceTimestampMs;
                transition.DurationMs = durationMs;
                transition.LocalVerification = this._localTransitionVerifier(sourceFile, langFile, bestCrossover, oldOffsetSec, newOffsetSec, inverseRatio);
                strongDifferentialAccepted = false;
                for (int c = 0; c < transition.Candidates.Count; c++)
                {
                    if (Math.Abs(transition.Candidates[c].SourceSec - bestCrossover) <= 0.05 &&
                        (string.Equals(transition.Candidates[c].Decision, "accepted-strong-differential", StringComparison.Ordinal) ||
                        string.Equals(transition.Candidates[c].Decision, "accepted-timeline-cut-boundary", StringComparison.Ordinal)) &&
                        !transition.Candidates[c].AudioRejected)
                    {
                        strongDifferentialAccepted = true;
                        break;
                    }
                }

                if (transition.LocalVerification == null || !transition.LocalVerification.Verified)
                {
                    if (timelineMode)
                    {
                        if (strongDifferentialAccepted)
                        {
                            transition.Status = "AcceptedTentative";
                            transition.RejectReason = "Candidato differenziale forte, demandato alla verifica globale";
                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Transizione " + (r + 1) + ": candidato differenziale forte, operazione timeline mantenuta per verifica globale");
                        }
                        else if (transition.LocalVerification != null && transition.LocalVerification.CanDeferToGlobalVerification)
                        {
                            transition.Status = "AcceptedTentative";
                            transition.RejectReason = "Verifica locale non conclusiva, demandata alla verifica globale";
                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, "  Transizione " + (r + 1) + ": verifica locale non conclusiva, operazione timeline mantenuta per verifica globale");
                        }
                        else
                        {
                            transition.Status = "SkippedUnverified";
                            transition.RejectReason = "Verifica locale timeline-first fallita, operazione scartata";
                            transition.OperationType = "";
                            operations.RemoveAt(operations.Count - 1);
                            ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  Transizione " + (r + 1) + ": verifica locale fallita, operazione timeline scartata");
                            continue;
                        }
                    }
                    else
                    {
                        transition.Status = "Rejected";
                        transition.RejectReason = "Verifica locale transizione fallita";
                        transition.OperationType = "";
                        operations.RemoveAt(operations.Count - 1);
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  Transizione " + (r + 1) + ": verifica locale fallita, operazione scartata");
                        continue;
                    }
                }

                if (timelineMode && durationMs < 150)
                {
                    transition.Status = "SkippedUnverified";
                    transition.RejectReason = "Delta video-only sotto soglia di confidenza";
                    transition.OperationType = "";
                    operations.RemoveAt(operations.Count - 1);
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  Transizione " + (r + 1) + ": delta video-only " + durationMs + "ms sotto soglia di confidenza, operazione scartata");
                    continue;
                }

                if (timelineMode && operations.Count >= 2 && acceptedTransitions.Count > 0)
                {
                    EditOperation previousOp = operations[operations.Count - 2];
                    int previousDeltaMs = EditMapTimelineHelper.GetSourceOperationDeltaMs(previousOp, inverseRatio);
                    int currentDeltaMs = string.Equals(op.Type, EditOperation.INSERT_SILENCE, StringComparison.Ordinal) ? durationMs : -durationMs;
                    int residualDeltaMs = previousDeltaMs + currentDeltaMs;

                    if (Math.Sign(previousDeltaMs) != Math.Sign(currentDeltaMs) &&
                        Math.Abs(residualDeltaMs) < minOffsetChangeMs &&
                        Math.Abs(previousDeltaMs) <= 500 &&
                        Math.Abs(currentDeltaMs) <= 500)
                    {
                        DeepAnalysisTransitionDiagnostic previousTransition = acceptedTransitions[acceptedTransitions.Count - 1];
                        double offsetBeforePairSec = acceptedOffsetBeforeSec[acceptedOffsetBeforeSec.Count - 1];

                        operations.RemoveAt(operations.Count - 1);
                        operations.RemoveAt(operations.Count - 1);
                        acceptedTransitions.RemoveAt(acceptedTransitions.Count - 1);
                        acceptedOffsetBeforeSec.RemoveAt(acceptedOffsetBeforeSec.Count - 1);

                        previousTransition.Status = "SkippedCompensated";
                        previousTransition.OperationType = "";
                        previousTransition.RejectReason = "Coppia video-only piccola e compensata sotto soglia timeline";
                        transition.Status = "SkippedCompensated";
                        transition.OperationType = "";
                        transition.RejectReason = "Coppia video-only piccola e compensata sotto soglia timeline";
                        effectiveOffsetSec = offsetBeforePairSec;
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  Transizioni " + previousTransition.Index + "-" + (r + 1) + ": coppia video-only compensata (" + previousDeltaMs + "ms/" + currentDeltaMs + "ms), operazioni scartate");
                        continue;
                    }
                }

                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Transizione " + (r + 1) + ": " + operationType + " @ lang " + (langTimestampMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + "s, durata lang " + operationDurationMs + "ms/source " + durationMs + "ms (crossover src " + bestCrossover.ToString("F2", CultureInfo.InvariantCulture) + "s)");
                acceptedOffsetBeforeSec.Add(oldOffsetSec);
                acceptedTransitions.Add(transition);
                effectiveOffsetSec = newOffsetSec;
            }

            return operations;
        }

        #endregion

        #region Metodi privati

        #endregion
    }
}
