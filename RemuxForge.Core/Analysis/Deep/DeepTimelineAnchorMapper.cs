using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Costruisce una mappa timeline-first da anchor video distribuiti
    /// </summary>
    public class DeepTimelineAnchorMapper
    {
        #region Delegati

        /// <summary>
        /// Cerca un anchor visuale attorno a un centro source
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="langFile">File lingua</param>
        /// <param name="sourceCenterSec">Centro finestra source</param>
        /// <param name="searchRadiusMs">Raggio ricerca offset</param>
        /// <param name="searchStepMs">Passo ricerca offset</param>
        /// <param name="anchor">Anchor diagnostico prodotto</param>
        /// <returns>True se il probe produce un anchor valido o diagnosticabile</returns>
        public delegate bool VisualAnchorProbe(string sourceFile, string langFile, double sourceCenterSec, int searchRadiusMs, int searchStepMs, double inverseRatio, out DeepAnalysisTimelineAnchorDiagnostic anchor);

        #endregion

        #region Costanti

        private const double ANCHOR_WINDOW_SEC = 80.0;
        private const double VIDEO_ANCHOR_STEP_SEC = 30.0;
        private const int MIN_SEARCH_RADIUS_MS = 20000;
        private const int MAX_SEARCH_RADIUS_MS = 120000;
        private const int SEARCH_RADIUS_PADDING_MS = 10000;
        private const int VIDEO_SEARCH_STEP_MS = 50;
        private const int PLATEAU_TOLERANCE_MS = 100;
        private const int MIN_TIMELINE_TRANSITION_MS = 100;
        private const int ISOLATED_OUTLIER_MIN_DELTA_MS = 15000;
        private const int ISOLATED_OUTLIER_MAX_NEIGHBOR_DELTA_MS = 15000;
        private const double ISOLATED_OUTLIER_MAX_SCORE = 0.90;
        private const int MIN_ACCEPTED_ANCHORS = 5;
        private const int MIN_VIDEO_PLATEAU_ANCHORS = 2;
        private const double SHORT_VIDEO_PLATEAU_MAX_SEC = 120.0;
        private const int SHORT_VIDEO_PLATEAU_MAX_ANCHORS = 11;
        private const double DENSE_VIDEO_ANCHOR_STEP_SEC = 5.0;

        #endregion

        #region Variabili di classe

        private readonly string _mkvMergePath;
        private readonly VisualAnchorProbe _visualAnchorProbe;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="mkvMergePath">Percorso mkvmerge</param>
        /// <param name="visualAnchorProbe">Probe anchor visuale</param>
        public DeepTimelineAnchorMapper(string mkvMergePath, VisualAnchorProbe visualAnchorProbe)
        {
            this._mkvMergePath = mkvMergePath;
            this._visualAnchorProbe = visualAnchorProbe;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Prova a costruire una timeline a offset costanti usando anchor video
        /// </summary>
        public DeepTimelineMapResult Build(string sourceFile, string langFile, int sourceDurationMs, double inverseRatio)
        {
            DeepTimelineMapResult result = new DeepTimelineMapResult();
            MkvFileInfo sourceInfo;
            MkvFileInfo languageInfo;
            double sourceDurationSec = sourceDurationMs / 1000.0;
            double languageDurationSec;
            int searchRadiusMs;
            List<DeepAnalysisTimelineAnchorDiagnostic> acceptedAnchors;
            result.Diagnostic.Status = "Skipped";

            if (string.IsNullOrEmpty(this._mkvMergePath) || !File.Exists(this._mkvMergePath))
            {
                result.RejectReason = "mkvmerge non disponibile per timeline video";
                result.Diagnostic.RejectReason = result.RejectReason;
                return result;
            }

            sourceInfo = new MkvToolsService(this._mkvMergePath).GetFileInfo(sourceFile);
            languageInfo = new MkvToolsService(this._mkvMergePath).GetFileInfo(langFile);
            if (sourceInfo == null || languageInfo == null)
            {
                result.RejectReason = "metadata container non leggibili";
                result.Diagnostic.RejectReason = result.RejectReason;
                return result;
            }

            languageDurationSec = languageInfo.ContainerDurationNs > 0 ? languageInfo.ContainerDurationNs / 1000000000.0 : sourceDurationSec;
            searchRadiusMs = this.ResolveSearchRadiusMs(sourceDurationSec, languageDurationSec);
            if (sourceDurationSec < ANCHOR_WINDOW_SEC * 2.0 || languageDurationSec < ANCHOR_WINDOW_SEC * 2.0)
            {
                result.RejectReason = "durata insufficiente per timeline";
                result.Diagnostic.RejectReason = result.RejectReason;
                return result;
            }

            result.Diagnostic.Status = "Running";

            result.Diagnostic.AnchorMode = "video";
            result.Diagnostic.TrackLanguage = "video";
            if (!this.BuildVisualAnchors(sourceFile, langFile, sourceDurationSec, searchRadiusMs, inverseRatio, result.Diagnostic))
            {
                result.RejectReason = "anchor video insufficienti";
                result.Diagnostic.Status = "Rejected";
                result.Diagnostic.RejectReason = result.RejectReason;
                return result;
            }

            this.DensifyVisualTransitionAnchors(sourceFile, langFile, sourceDurationSec, searchRadiusMs, inverseRatio, result.Diagnostic);

            acceptedAnchors = this.GetAcceptedAnchors(result.Diagnostic.Anchors);
            result.Diagnostic.AcceptedAnchorCount = acceptedAnchors.Count;
            result.Diagnostic.AnchorCount = result.Diagnostic.Anchors.Count;
            result.Diagnostic.AverageAcceptedScore = this.ComputeAverageScore(acceptedAnchors);

            if (acceptedAnchors.Count < MIN_ACCEPTED_ANCHORS)
            {
                result.RejectReason = "anchor timeline insufficienti";
                result.Diagnostic.Status = "Rejected";
                result.Diagnostic.RejectReason = result.RejectReason;
                return result;
            }

            this.BuildPlateaus(acceptedAnchors, result.Diagnostic);
            result.Diagnostic.PlateauCount = result.Diagnostic.Plateaus.Count;
            result.Regions = this.BuildRegionsFromPlateaus(result.Diagnostic.Plateaus, sourceDurationSec);

            if (result.Regions.Count == 0)
            {
                result.RejectReason = "nessun plateau timeline valido";
                result.Diagnostic.Status = "Rejected";
                result.Diagnostic.RejectReason = result.RejectReason;
                return result;
            }

            result.Success = true;
            result.Diagnostic.Status = "Accepted";
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Calcola il raggio ricerca offset in base alla differenza durata
        /// </summary>
        /// <param name="sourceDurationSec">Durata source</param>
        /// <param name="languageDurationSec">Durata language</param>
        /// <returns>Raggio ricerca in millisecondi</returns>
        private int ResolveSearchRadiusMs(double sourceDurationSec, double languageDurationSec)
        {
            int result = MIN_SEARCH_RADIUS_MS;
            int durationDeltaMs = (int)Math.Round(Math.Abs(languageDurationSec - sourceDurationSec) * 1000.0);

            if (durationDeltaMs + SEARCH_RADIUS_PADDING_MS > result)
            {
                result = durationDeltaMs + SEARCH_RADIUS_PADDING_MS;
            }

            if (result > MAX_SEARCH_RADIUS_MS)
            {
                result = MAX_SEARCH_RADIUS_MS;
            }

            return result;
        }

        /// <summary>
        /// Costruisce anchor video distribuiti lungo il file
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="langFile">File lingua</param>
        /// <param name="sourceDurationSec">Durata source</param>
        /// <param name="searchRadiusMs">Raggio ricerca</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="diagnostic">Diagnostica timeline da aggiornare</param>
        /// <returns>True se la scansione visuale è stata avviata</returns>
        private bool BuildVisualAnchors(string sourceFile, string langFile, double sourceDurationSec, int searchRadiusMs, double inverseRatio, DeepAnalysisTimelineMapDiagnostic diagnostic)
        {
            bool result = false;
            double centerSec = ANCHOR_WINDOW_SEC / 2.0;
            double endCenterSec = sourceDurationSec - (ANCHOR_WINDOW_SEC / 2.0);
            List<double> centers = new List<double>();
            DeepAnalysisTimelineAnchorDiagnostic[] anchors;
            if (this._visualAnchorProbe == null)
            {
                return result;
            }

            while (centerSec <= endCenterSec)
            {
                // I centri visuali sono fitti per compensare ambiguità su anime/VFR
                centers.Add(centerSec);
                centerSec += VIDEO_ANCHOR_STEP_SEC;
            }

            anchors = this.BuildVisualAnchorsParallel(sourceFile, langFile, centers, searchRadiusMs, inverseRatio, "anchor video non conclusivo");
            diagnostic.Anchors.AddRange(anchors);

            result = true;
            return result;
        }

        /// <summary>
        /// Aggiunge anchor video densi tra due anchor accettati con offset diverso
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="langFile">File lingua</param>
        /// <param name="sourceDurationSec">Durata source</param>
        /// <param name="searchRadiusMs">Raggio ricerca</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="diagnostic">Diagnostica timeline da aggiornare</param>
        private void DensifyVisualTransitionAnchors(string sourceFile, string langFile, double sourceDurationSec, int searchRadiusMs, double inverseRatio, DeepAnalysisTimelineMapDiagnostic diagnostic)
        {
            List<DeepAnalysisTimelineAnchorDiagnostic> baseAccepted = this.GetAcceptedAnchors(diagnostic.Anchors);
            List<double> centers = new List<double>();
            DeepAnalysisTimelineAnchorDiagnostic[] denseAnchors;
            if (this._visualAnchorProbe == null || baseAccepted.Count < 2)
            {
                return;
            }

            for (int i = 0; i < baseAccepted.Count - 1; i++)
            {
                bool anchorNear;
                bool centerNear;
                // Densifichiamo solo dove c'è una vera transizione di offset da localizzare
                if (Math.Abs(baseAccepted[i + 1].OffsetMs - baseAccepted[i].OffsetMs) < MIN_TIMELINE_TRANSITION_MS)
                {
                    continue;
                }

                double centerSec = baseAccepted[i].SourceCenterSec + DENSE_VIDEO_ANCHOR_STEP_SEC;
                while (centerSec < baseAccepted[i + 1].SourceCenterSec)
                {
                    anchorNear = false;
                    for (int a = 0; a < diagnostic.Anchors.Count; a++)
                    {
                        if (Math.Abs(diagnostic.Anchors[a].SourceCenterSec - centerSec) < 0.5)
                        {
                            anchorNear = true;
                            break;
                        }
                    }

                    centerNear = false;
                    for (int c = 0; c < centers.Count; c++)
                    {
                        if (Math.Abs(centers[c] - centerSec) < 0.5)
                        {
                            centerNear = true;
                            break;
                        }
                    }

                    if (centerSec > ANCHOR_WINDOW_SEC / 2.0 && centerSec < sourceDurationSec - (ANCHOR_WINDOW_SEC / 2.0) && !anchorNear && !centerNear)
                    {
                        centers.Add(centerSec);
                    }

                    centerSec += DENSE_VIDEO_ANCHOR_STEP_SEC;
                }
            }

            if (centers.Count == 0)
            {
                return;
            }

            centers.Sort();
            denseAnchors = this.BuildVisualAnchorsParallel(sourceFile, langFile, centers, searchRadiusMs, inverseRatio, "anchor video dense non conclusivo");
            diagnostic.Anchors.AddRange(denseAnchors);
            diagnostic.Anchors.Sort((left, right) => left.SourceCenterSec.CompareTo(right.SourceCenterSec));
        }

        /// <summary>
        /// Esegue probe visuali in parallelo su una lista di centri
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="langFile">File lingua</param>
        /// <param name="centers">Centri source da analizzare</param>
        /// <param name="searchRadiusMs">Raggio ricerca</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="rejectReason">Motivo da assegnare agli anchor non conclusivi</param>
        /// <returns>Array anchor ordinato come i centri in input</returns>
        private DeepAnalysisTimelineAnchorDiagnostic[] BuildVisualAnchorsParallel(string sourceFile, string langFile, List<double> centers, int searchRadiusMs, double inverseRatio, string rejectReason)
        {
            DeepAnalysisTimelineAnchorDiagnostic[] result = new DeepAnalysisTimelineAnchorDiagnostic[centers.Count];
            ParallelOptions options = new ParallelOptions();
            options.MaxDegreeOfParallelism = ParallelismHelper.ResolveDefaultMaxDegree();

            Parallel.For(0, centers.Count, options, delegate (int i)
            {
                // Ogni slot dell'array è scritto da una sola iterazione, quindi non serve lock
                DeepAnalysisTimelineAnchorDiagnostic anchor;
                if (this._visualAnchorProbe(sourceFile, langFile, centers[i], searchRadiusMs, VIDEO_SEARCH_STEP_MS, inverseRatio, out anchor))
                {
                    result[i] = anchor;
                }
                else
                {
                    anchor = new DeepAnalysisTimelineAnchorDiagnostic();
                    anchor.SourceCenterSec = centers[i];
                    anchor.Accepted = false;
                    anchor.RejectReason = rejectReason;
                    result[i] = anchor;
                }
            });

            return result;
        }

        /// <summary>
        /// Filtra gli anchor accettati
        /// </summary>
        /// <param name="anchors">Anchor diagnostici</param>
        /// <returns>Solo anchor accettati</returns>
        private List<DeepAnalysisTimelineAnchorDiagnostic> GetAcceptedAnchors(List<DeepAnalysisTimelineAnchorDiagnostic> anchors)
        {
            List<DeepAnalysisTimelineAnchorDiagnostic> result = new List<DeepAnalysisTimelineAnchorDiagnostic>();

            for (int i = 0; i < anchors.Count; i++)
            {
                if (anchors[i].Accepted)
                {
                    result.Add(anchors[i]);
                }
            }

            return result;
        }

        /// <summary>
        /// Calcola lo score medio di una lista anchor
        /// </summary>
        /// <param name="anchors">Anchor accettati</param>
        /// <returns>Score medio</returns>
        private double ComputeAverageScore(List<DeepAnalysisTimelineAnchorDiagnostic> anchors)
        {
            double result = 0.0;
            if (anchors.Count == 0)
            {
                return result;
            }

            for (int i = 0; i < anchors.Count; i++)
            {
                result += anchors[i].Score;
            }

            return result / anchors.Count;
        }

        /// <summary>
        /// Raggruppa anchor consecutivi in plateau a offset stabile
        /// </summary>
        /// <param name="anchors">Anchor accettati ordinati</param>
        /// <param name="diagnostic">Diagnostica timeline da aggiornare</param>
        private void BuildPlateaus(List<DeepAnalysisTimelineAnchorDiagnostic> anchors, DeepAnalysisTimelineMapDiagnostic diagnostic)
        {
            List<DeepAnalysisTimelineAnchorDiagnostic> current = new List<DeepAnalysisTimelineAnchorDiagnostic>();
            int currentOffset = anchors[0].OffsetMs;

            for (int i = 0; i < anchors.Count; i++)
            {
                // Quando l'offset si allontana dalla mediana corrente, il plateau è chiuso
                if (current.Count > 0 && Math.Abs(anchors[i].OffsetMs - currentOffset) > PLATEAU_TOLERANCE_MS)
                {
                    this.AddPlateau(current, diagnostic);
                    current.Clear();
                }

                current.Add(anchors[i]);
                currentOffset = this.MedianOffset(current);
            }

            if (current.Count > 0)
            {
                this.AddPlateau(current, diagnostic);
            }

            this.MergeAdjacentPlateaus(diagnostic);
            this.RemoveIsolatedOutlierPlateaus(diagnostic);
            this.MergeAdjacentPlateaus(diagnostic);
        }

        /// <summary>
        /// Aggiunge un plateau se contiene abbastanza anchor per la modalità corrente
        /// </summary>
        /// <param name="anchors">Anchor del plateau</param>
        /// <param name="diagnostic">Diagnostica timeline da aggiornare</param>
        private void AddPlateau(List<DeepAnalysisTimelineAnchorDiagnostic> anchors, DeepAnalysisTimelineMapDiagnostic diagnostic)
        {
            if (anchors.Count < MIN_VIDEO_PLATEAU_ANCHORS)
            {
                return;
            }

            DeepAnalysisTimelinePlateauDiagnostic plateau = new DeepAnalysisTimelinePlateauDiagnostic();
            double halfStepSec = VIDEO_ANCHOR_STEP_SEC / 2.0;
            // I bordi plateau sono stimati a metà passo attorno al primo/ultimo anchor
            plateau.Index = diagnostic.Plateaus.Count;
            plateau.StartSrcSec = anchors[0].SourceCenterSec - halfStepSec;
            plateau.EndSrcSec = anchors[anchors.Count - 1].SourceCenterSec + halfStepSec;
            plateau.FirstAnchorSrcSec = anchors[0].SourceCenterSec;
            plateau.LastAnchorSrcSec = anchors[anchors.Count - 1].SourceCenterSec;
            if (plateau.StartSrcSec < 0.0) { plateau.StartSrcSec = 0.0; }
            plateau.OffsetMs = this.MedianOffset(anchors);
            plateau.AnchorCount = anchors.Count;
            plateau.AverageScore = this.ComputeAverageScore(anchors);
            diagnostic.Plateaus.Add(plateau);
        }

        /// <summary>
        /// Fonde plateau adiacenti con offset ancora equivalente
        /// </summary>
        /// <param name="diagnostic">Diagnostica contenente i plateau da fondere</param>
        private void MergeAdjacentPlateaus(DeepAnalysisTimelineMapDiagnostic diagnostic)
        {
            List<DeepAnalysisTimelinePlateauDiagnostic> merged = new List<DeepAnalysisTimelinePlateauDiagnostic>();

            if (diagnostic.Plateaus == null || diagnostic.Plateaus.Count == 0)
            {
                return;
            }

            for (int i = 0; i < diagnostic.Plateaus.Count; i++)
            {
                DeepAnalysisTimelinePlateauDiagnostic current = diagnostic.Plateaus[i];
                if (merged.Count > 0 && Math.Abs(merged[merged.Count - 1].OffsetMs - current.OffsetMs) < MIN_TIMELINE_TRANSITION_MS)
                {
                    // Media pesata per numero anchor: mantiene più influenza ai plateau più supportati
                    DeepAnalysisTimelinePlateauDiagnostic previous = merged[merged.Count - 1];
                    int totalAnchors = previous.AnchorCount + current.AnchorCount;
                    previous.EndSrcSec = current.EndSrcSec;
                    previous.LastAnchorSrcSec = current.LastAnchorSrcSec;
                    previous.OffsetMs = (int)Math.Round(((previous.OffsetMs * previous.AnchorCount) + (current.OffsetMs * current.AnchorCount)) / (double)totalAnchors);
                    previous.AverageScore = ((previous.AverageScore * previous.AnchorCount) + (current.AverageScore * current.AnchorCount)) / totalAnchors;
                    previous.AnchorCount = totalAnchors;
                }
                else
                {
                    current.Index = merged.Count;
                    merged.Add(current);
                }
            }

            diagnostic.Plateaus.Clear();
            diagnostic.Plateaus.AddRange(merged);
        }

        /// <summary>
        /// Rimuove plateau video brevi non supportati e spike timeline isolati
        /// </summary>
        /// <param name="diagnostic">Diagnostica contenente i plateau da filtrare</param>
        private void RemoveIsolatedOutlierPlateaus(DeepAnalysisTimelineMapDiagnostic diagnostic)
        {
            List<DeepAnalysisTimelinePlateauDiagnostic> filtered = new List<DeepAnalysisTimelinePlateauDiagnostic>();
            DeepAnalysisTimelinePlateauDiagnostic previous;
            DeepAnalysisTimelinePlateauDiagnostic current;
            DeepAnalysisTimelinePlateauDiagnostic next;
            int previousStepMs;
            int nextStepMs;
            bool isSupportedMonotonicPlateau;
            bool removeCurrent;

            if (diagnostic.Plateaus == null || diagnostic.Plateaus.Count < 3)
            {
                return;
            }

            for (int i = 0; i < diagnostic.Plateaus.Count; i++)
            {
                current = diagnostic.Plateaus[i];
                previousStepMs = 0;
                nextStepMs = 0;
                isSupportedMonotonicPlateau = false;
                removeCurrent = false;

                if (i > 0 && i < diagnostic.Plateaus.Count - 1)
                {
                    previous = diagnostic.Plateaus[i - 1];
                    next = diagnostic.Plateaus[i + 1];
                    previousStepMs = current.OffsetMs - previous.OffsetMs;
                    nextStepMs = next.OffsetMs - current.OffsetMs;
                    // Una progressione ben supportata nello stesso verso rappresenta edit cumulativi reali
                    isSupportedMonotonicPlateau = previousStepMs != 0 &&
                        Math.Sign(previousStepMs) == Math.Sign(nextStepMs) &&
                        current.AnchorCount > MIN_VIDEO_PLATEAU_ANCHORS &&
                        current.AverageScore >= ISOLATED_OUTLIER_MAX_SCORE;
                }

                if (i > 0 && i < diagnostic.Plateaus.Count - 1 &&
                    string.Equals(diagnostic.AnchorMode, "video", StringComparison.Ordinal) &&
                    current.EndSrcSec - current.StartSrcSec <= SHORT_VIDEO_PLATEAU_MAX_SEC &&
                    current.AnchorCount <= SHORT_VIDEO_PLATEAU_MAX_ANCHORS &&
                    !isSupportedMonotonicPlateau)
                {
                    removeCurrent = true;
                }
                else if (i > 0 && i < diagnostic.Plateaus.Count - 1 && current.AnchorCount <= 1 && current.AverageScore < ISOLATED_OUTLIER_MAX_SCORE)
                {
                    previous = diagnostic.Plateaus[i - 1];
                    next = diagnostic.Plateaus[i + 1];

                    if (Math.Abs(current.OffsetMs - previous.OffsetMs) >= ISOLATED_OUTLIER_MIN_DELTA_MS &&
                        Math.Abs(current.OffsetMs - next.OffsetMs) >= ISOLATED_OUTLIER_MIN_DELTA_MS &&
                        Math.Abs(previous.OffsetMs - next.OffsetMs) <= ISOLATED_OUTLIER_MAX_NEIGHBOR_DELTA_MS)
                    {
                        removeCurrent = true;
                    }
                }

                if (!removeCurrent)
                {
                    current.Index = filtered.Count;
                    filtered.Add(current);
                }
            }

            diagnostic.Plateaus.Clear();
            diagnostic.Plateaus.AddRange(filtered);
        }

        /// <summary>
        /// Calcola la mediana offset degli anchor
        /// </summary>
        /// <param name="anchors">Anchor da valutare</param>
        /// <returns>Offset mediano in millisecondi</returns>
        private int MedianOffset(List<DeepAnalysisTimelineAnchorDiagnostic> anchors)
        {
            List<int> values = new List<int>();

            for (int i = 0; i < anchors.Count; i++)
            {
                values.Add(anchors[i].OffsetMs);
            }

            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>
        /// Converte plateau timeline in regioni source continue
        /// </summary>
        /// <param name="plateaus">Plateau validi</param>
        /// <param name="sourceDurationSec">Durata source</param>
        /// <returns>Regioni offset continue</returns>
        private List<OffsetRegion> BuildRegionsFromPlateaus(List<DeepAnalysisTimelinePlateauDiagnostic> plateaus, double sourceDurationSec)
        {
            List<OffsetRegion> result = new List<OffsetRegion>();

            for (int i = 0; i < plateaus.Count; i++)
            {
                // Il confine tra due plateau è il punto medio tra fine plateau precedente e inizio successivo
                OffsetRegion region = new OffsetRegion();
                region.StartSrcSec = i == 0 ? 0.0 : this.ResolvePlateauBoundarySec(plateaus[i - 1], plateaus[i]);
                region.EndSrcSec = i == plateaus.Count - 1 ? sourceDurationSec : this.ResolvePlateauBoundarySec(plateaus[i], plateaus[i + 1]);
                region.SupportStartSrcSec = plateaus[i].StartSrcSec;
                region.SupportEndSrcSec = plateaus[i].EndSrcSec;
                region.FirstAnchorSrcSec = plateaus[i].FirstAnchorSrcSec;
                region.LastAnchorSrcSec = plateaus[i].LastAnchorSrcSec;
                region.OffsetMs = plateaus[i].OffsetMs;
                region.MatchCount = plateaus[i].AnchorCount;
                if (region.EndSrcSec > region.StartSrcSec)
                {
                    result.Add(region);
                }
            }

            return result;
        }

        /// <summary>
        /// Risolve il confine tra due plateau evitando di espandere il plateau sinistro dentro gap visuali ampi
        /// </summary>
        /// <param name="left">Plateau precedente</param>
        /// <param name="right">Plateau successivo</param>
        /// <returns>Timestamp source del confine in secondi</returns>
        private double ResolvePlateauBoundarySec(DeepAnalysisTimelinePlateauDiagnostic left, DeepAnalysisTimelinePlateauDiagnostic right)
        {
            double gapSec = right.StartSrcSec - left.EndSrcSec;
            if (gapSec > VIDEO_ANCHOR_STEP_SEC)
            {
                return right.StartSrcSec;
            }

            return (left.EndSrcSec + right.StartSrcSec) / 2.0;
        }

        #endregion

    }
}
