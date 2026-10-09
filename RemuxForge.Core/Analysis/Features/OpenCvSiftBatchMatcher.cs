using RemuxForge.Core.Models;
using RemuxForge.Core.Localization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Analysis.Features
{
    /// <summary>
    /// Backend CPU SIFT con valutazione delle coppie complete o pianificate
    /// </summary>
    public sealed class OpenCvSiftBatchMatcher : FrameFeatureBatchMatcherBase
    {
        #region Variabili di classe

        /// <summary>
        /// Parametri condivisi di estrazione e matching SIFT
        /// </summary>
        private readonly FrameFeatureMatcherOptions _options;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza il backend con le opzioni SIFT condivise dai worker
        /// </summary>
        /// <param name="options">Opzioni del matcher</param>
        public OpenCvSiftBatchMatcher(FrameFeatureMatcherOptions options = null)
        {
            this._options = options ?? new FrameFeatureMatcherOptions();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Verifica la disponibilità del backend CPU
        /// </summary>
        /// <param name="rejectReason">Motivo per cui il backend non è disponibile</param>
        /// <returns>True se il backend CPU è disponibile</returns>
        public override bool IsAvailable(out string rejectReason)
        {
            using (OpenCvSiftFeatureMatcher matcher = this.CreateMatcher())
                return matcher.IsAvailable(out rejectReason);
        }

        /// <summary>
        /// Costruisce in parallelo la matrice SIFT completa
        /// </summary>
        /// <param name="sourceAnchors">Ancore visuali della timeline source</param>
        /// <param name="languageAnchors">Ancore visuali della timeline language</param>
        /// <param name="maxDegreeOfParallelism">Numero massimo di worker eseguibili in parallelo</param>
        /// <param name="cancellationToken">Token per annullare l'elaborazione</param>
        /// <returns>Risultato del matching batch con matrice e diagnostica dell'elaborazione</returns>
        public override DeepSiftBatchMatchResult BuildMatrix(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors, int maxDegreeOfParallelism, CancellationToken cancellationToken)
        {
            if (sourceAnchors == null)
                throw new ArgumentNullException(nameof(sourceAnchors));
            if (languageAnchors == null)
                throw new ArgumentNullException(nameof(languageAnchors));
            if (maxDegreeOfParallelism < 1)
                throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));

            DeepSiftBatchMatchResult result = new DeepSiftBatchMatchResult();
            result.BackendName = this.BackendName;
            int configuredWorkerCount = Math.Min(maxDegreeOfParallelism, Math.Max(1, Environment.ProcessorCount));
            OpenCvSiftFeatureSet[] sourceFeatures = new OpenCvSiftFeatureSet[sourceAnchors.Count];
            OpenCvSiftFeatureSet[] languageFeatures = new OpenCvSiftFeatureSet[languageAnchors.Count];
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                this.ValidateAnchors(sourceAnchors);
                this.ValidateAnchors(languageAnchors);

                ParallelOptions options = new ParallelOptions();
                options.MaxDegreeOfParallelism = configuredWorkerCount;
                options.CancellationToken = cancellationToken;

                Parallel.For<OpenCvSiftFeatureMatcher>(0, sourceAnchors.Count, options, this.CreateMatcher, (index, _, matcher) =>
                {
                    DeepSiftVisualAnchor anchor = sourceAnchors[index];
                    sourceFeatures[index] = matcher.ExtractFeatures(anchor.Frame, anchor.Width, anchor.Height);
                    return matcher;
                }, matcher => matcher.Dispose());

                Parallel.For<OpenCvSiftFeatureMatcher>(0, languageAnchors.Count, options, this.CreateMatcher, (index, _, matcher) =>
                {
                    DeepSiftVisualAnchor anchor = languageAnchors[index];
                    languageFeatures[index] = matcher.ExtractFeatures(anchor.Frame, anchor.Width, anchor.Height);
                    return matcher;
                }, matcher => matcher.Dispose());

                result.FeatureExtractionMs = stopwatch.ElapsedMilliseconds;
                stopwatch.Restart();
                result.SourceFeaturelessAnchorCount = this.CountFeatureless(sourceFeatures);
                result.LanguageFeaturelessAnchorCount = this.CountFeatureless(languageFeatures);
                List<int> activeSourceIndexes = this.GetActiveIndexes(sourceFeatures);
                List<int> activeLanguageIndexes = this.GetActiveIndexes(languageFeatures);
                result.SourceAnchors = this.GetActiveAnchors(sourceAnchors, activeSourceIndexes);
                result.LanguageAnchors = this.GetActiveAnchors(languageAnchors, activeLanguageIndexes);
                result.SourceAnchorCount = result.SourceAnchors.Count;
                result.LanguageAnchorCount = result.LanguageAnchors.Count;
                result.Matrix = new DeepSiftMatchMatrix(result.SourceAnchorCount, result.LanguageAnchorCount);
                const int TILE_ROW_COUNT = 4;
                long processedCells = 0;
                long descriptorMatchingTicks = 0;
                long geometryTicks = 0;
                ConcurrentDictionary<string, int> rejectionCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
                if (activeSourceIndexes.Count > 0 && activeLanguageIndexes.Count > 0)
                {
                    Parallel.ForEach<Tuple<int, int>, OpenCvSiftFeatureMatcher>(Partitioner.Create(0, activeSourceIndexes.Count, TILE_ROW_COUNT), options, this.CreateMatcher, (range, _, matcher) =>
                    {
                        for (int sourceIndex = range.Item1; sourceIndex < range.Item2; sourceIndex++)
                        {
                            int originalSourceIndex = activeSourceIndexes[sourceIndex];
                            for (int languageIndex = 0; languageIndex < activeLanguageIndexes.Count; languageIndex++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                int originalLanguageIndex = activeLanguageIndexes[languageIndex];
                                int randomSeed = this.GetStablePairSeed(sourceAnchors[originalSourceIndex], languageAnchors[originalLanguageIndex]);
                                FrameFeatureMatchResult match = matcher.Match(sourceFeatures[originalSourceIndex], languageFeatures[originalLanguageIndex], randomSeed);
                                if (match == null || !match.Accepted)
                                {
                                    string reason = match != null && !string.IsNullOrEmpty(match.RejectReason) ? match.RejectReason : "NoResult";
                                    rejectionCounts.AddOrUpdate(reason, 1, (_, count) => count + 1);
                                }
                                if (match != null)
                                {
                                    Interlocked.Add(ref descriptorMatchingTicks, match.DescriptorMatchingTicks);
                                    Interlocked.Add(ref geometryTicks, match.GeometryTicks);
                                }
                                DeepSiftMatchCell cell = this.CreateCell(match);
                                result.Matrix.Set(sourceIndex, languageIndex, cell);
                                if (cell.State == DeepSiftMatchState.Accepted)
                                {
                                    lock (result.AcceptedPairs)
                                    {
                                        this.AddAcceptedPair(result, sourceIndex, languageIndex, cell, match.Homography);
                                    }
                                }
                                Interlocked.Increment(ref processedCells);
                            }
                        }

                        return matcher;
                    }, matcher => matcher.Dispose());
                }

                // Le celle si accodano in ordine di completamento dei thread: senza un ordine
                // canonico il consenso a valle sceglie un cluster diverso a ogni esecuzione
                result.AcceptedPairs.Sort((left, right) => left.SourceAnchorIndex != right.SourceAnchorIndex ?
                    left.SourceAnchorIndex.CompareTo(right.SourceAnchorIndex) :
                    left.LanguageAnchorIndex.CompareTo(right.LanguageAnchorIndex));

                result.MatchingMs = stopwatch.ElapsedMilliseconds;
                result.DescriptorMatchingMs = (long)Math.Round(descriptorMatchingTicks * 1000.0 / Stopwatch.Frequency);
                result.GeometryMs = (long)Math.Round(geometryTicks * 1000.0 / Stopwatch.Frequency);
                this.UpdateMatrixCounters(result.Matrix, processedCells);
                result.ProcessedCellCount = result.Matrix.ProcessedCellCount;
                result.AcceptedCellCount = result.Matrix.AcceptedCellCount;
                foreach (KeyValuePair<string, int> entry in rejectionCounts)
                    result.RejectionCounts[entry.Key] = entry.Value;
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.RejectReason = AppText.T("deep.temporal.matcher.cpuCancelled");
                return result;
            }
            catch (Exception ex)
            {
                result.RejectReason = AppText.F("deep.temporal.matcher.cpuBatchFailed", ex.Message);
                return result;
            }
            finally
            {
                for (int i = 0; i < sourceFeatures.Length; i++)
                    sourceFeatures[i]?.Dispose();
                for (int i = 0; i < languageFeatures.Length; i++)
                    languageFeatures[i]?.Dispose();
            }
        }

        /// <summary>
        /// Il backend non possiede risorse oltre la durata di un batch
        /// </summary>
        public override void Dispose()
        {
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome backend stabile
        /// </summary>
        public override string BackendName { get { return OpenCvSiftFeatureMatcher.BACKEND_NAME; } }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Crea un matcher isolato per il worker corrente
        /// </summary>
        /// <returns>Matcher SIFT configurato con le opzioni condivise</returns>
        private OpenCvSiftFeatureMatcher CreateMatcher()
        {
            return new OpenCvSiftFeatureMatcher(this._options);
        }

        /// <summary>
        /// Traduce il risultato scalare nel contratto della matrice
        /// </summary>
        /// <param name="match">Risultato scalare del matching, oppure null se il confronto non ha prodotto un risultato</param>
        /// <returns>Cella della matrice corrispondente al risultato del matching</returns>
        private DeepSiftMatchCell CreateCell(FrameFeatureMatchResult match)
        {
            DeepSiftMatchCell result = new DeepSiftMatchCell();
            result.State = match != null && match.Accepted ? DeepSiftMatchState.Accepted : DeepSiftMatchState.Rejected;
            result.Score = match != null ? match.Score : 0.0;
            return result;
        }

        /// <summary>
        /// Conta le ancore che non hanno prodotto un numero sufficiente di keypoint SIFT
        /// </summary>
        /// <param name="features">Feature estratte per ogni ancora</param>
        /// <returns>Numero di ancore con un numero insufficiente di keypoint SIFT</returns>
        private int CountFeatureless(OpenCvSiftFeatureSet[] features)
        {
            int result = 0;
            for (int i = 0; i < features.Length; i++)
            {
                if (features[i] == null || features[i].KeypointCount < this._options.MinKeypoints)
                    result++;
            }

            return result;
        }

        /// <summary>
        /// Seleziona gli indici delle ancore che contengono un numero sufficiente di keypoint SIFT
        /// </summary>
        /// <param name="features">Feature estratte per ogni ancora</param>
        /// <returns>Indici delle ancore informative</returns>
        private List<int> GetActiveIndexes(OpenCvSiftFeatureSet[] features)
        {
            List<int> result = new List<int>();
            for (int i = 0; i < features.Length; i++)
            {
                if (features[i] != null && features[i].KeypointCount >= this._options.MinKeypoints)
                    result.Add(i);
            }
            return result;
        }

        #endregion
    }
}
