using RemuxForge.Core.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Analysis.Deep.Features
{
    /// <summary>
    /// Backend CPU SIFT con descriptor persistenti e valutazione delle sole coppie richieste
    /// </summary>
    public sealed class OpenCvSiftBatchMatcher : FrameFeatureBatchMatcherBase
    {
        #region Variabili di classe

        private readonly FrameFeatureMatcherOptions _options;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore con opzioni SIFT condivise dai worker
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
        public override bool IsAvailable(out string rejectReason)
        {
            using (OpenCvSiftFeatureMatcher matcher = this.CreateMatcher())
                return matcher.IsAvailable(out rejectReason);
        }

        /// <summary>
        /// Costruisce la matrice SIFT completa in parallelo
        /// </summary>
        public override DeepSiftBatchMatchResult BuildMatrix(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors, int maxDegreeOfParallelism, CancellationToken cancellationToken, IProgress<DeepSiftBatchProgress> progress = null, IReadOnlyList<DeepSiftFramePair> plannedPairs = null)
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
            result.DeclaredSourceAnchorCount = sourceAnchors.Count;
            result.DeclaredLanguageAnchorCount = languageAnchors.Count;
            OpenCvSiftFeatureSet[] sourceFeatures = new OpenCvSiftFeatureSet[sourceAnchors.Count];
            OpenCvSiftFeatureSet[] languageFeatures = new OpenCvSiftFeatureSet[languageAnchors.Count];
            Stopwatch stopwatch = Stopwatch.StartNew();
            ConcurrentDictionary<int, byte> workerThreads = new ConcurrentDictionary<int, byte>();
            HashSet<long> plannedPairKeys = this.CreatePlannedPairKeys(plannedPairs, sourceAnchors.Count, languageAnchors.Count);

            try
            {
                this.ValidateAnchors(sourceAnchors);
                this.ValidateAnchors(languageAnchors);

                ParallelOptions options = new ParallelOptions();
                options.MaxDegreeOfParallelism = configuredWorkerCount;
                options.CancellationToken = cancellationToken;

                Parallel.For<OpenCvSiftFeatureMatcher>(0, sourceAnchors.Count, options, this.CreateMatcher, (index, _, matcher) =>
                {
                    workerThreads.TryAdd(Thread.CurrentThread.ManagedThreadId, 0);
                    DeepSiftVisualAnchor anchor = sourceAnchors[index];
                    sourceFeatures[index] = matcher.ExtractFeatures(anchor.Frame, anchor.Width, anchor.Height);
                    return matcher;
                }, matcher => matcher.Dispose());

                Parallel.For<OpenCvSiftFeatureMatcher>(0, languageAnchors.Count, options, this.CreateMatcher, (index, _, matcher) =>
                {
                    workerThreads.TryAdd(Thread.CurrentThread.ManagedThreadId, 0);
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
                bool preserveInputIndexes = plannedPairKeys != null;
                result.SourceAnchors = preserveInputIndexes ? new List<DeepSiftVisualAnchor>(sourceAnchors) : this.GetActiveAnchors(sourceAnchors, activeSourceIndexes);
                result.LanguageAnchors = preserveInputIndexes ? new List<DeepSiftVisualAnchor>(languageAnchors) : this.GetActiveAnchors(languageAnchors, activeLanguageIndexes);
                result.SourceAnchorCount = result.SourceAnchors.Count;
                result.LanguageAnchorCount = result.LanguageAnchors.Count;
                result.Matrix = new DeepSiftMatchMatrix(result.SourceAnchorCount, result.LanguageAnchorCount);
                const int TILE_ROW_COUNT = 4;
                int totalTiles = activeSourceIndexes.Count == 0 ? 0 : (activeSourceIndexes.Count + TILE_ROW_COUNT - 1) / TILE_ROW_COUNT;
                long totalCells = plannedPairKeys != null ? plannedPairKeys.Count : (long)activeSourceIndexes.Count * activeLanguageIndexes.Count;
                long processedCells = 0;
                long descriptorMatchingTicks = 0;
                long geometryTicks = 0;
                int completedTiles = 0;
                Parallel.ForEach<Tuple<int, int>, OpenCvSiftFeatureMatcher>(Partitioner.Create(0, activeSourceIndexes.Count, TILE_ROW_COUNT), options, this.CreateMatcher, (range, _, matcher) =>
                {
                    workerThreads.TryAdd(Thread.CurrentThread.ManagedThreadId, 0);
                    for (int sourceIndex = range.Item1; sourceIndex < range.Item2; sourceIndex++)
                    {
                        int originalSourceIndex = activeSourceIndexes[sourceIndex];
                        for (int languageIndex = 0; languageIndex < activeLanguageIndexes.Count; languageIndex++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int originalLanguageIndex = activeLanguageIndexes[languageIndex];
                            if (plannedPairKeys != null && !plannedPairKeys.Contains(this.GetPairKey(originalSourceIndex, originalLanguageIndex)))
                                continue;
                            FrameFeatureMatchResult match = matcher.Match(sourceFeatures[originalSourceIndex], languageFeatures[originalLanguageIndex]);
                            if (match != null)
                            {
                                Interlocked.Add(ref descriptorMatchingTicks, match.DescriptorMatchingTicks);
                                Interlocked.Add(ref geometryTicks, match.GeometryTicks);
                            }
                            int matrixSourceIndex = preserveInputIndexes ? originalSourceIndex : sourceIndex;
                            int matrixLanguageIndex = preserveInputIndexes ? originalLanguageIndex : languageIndex;
                            result.Matrix.Set(matrixSourceIndex, matrixLanguageIndex, this.CreateCell(match));
                            Interlocked.Increment(ref processedCells);
                        }
                    }

                    long currentProcessed = Interlocked.Read(ref processedCells);
                    int currentTiles = Interlocked.Increment(ref completedTiles);
                    progress?.Report(new DeepSiftBatchProgress { CompletedTiles = currentTiles, TotalTiles = totalTiles, ProcessedCells = currentProcessed, TotalCells = totalCells });
                    return matcher;
                }, matcher => matcher.Dispose());

                result.MatchingMs = stopwatch.ElapsedMilliseconds;
                result.DescriptorMatchingMs = (long)Math.Round(descriptorMatchingTicks * 1000.0 / Stopwatch.Frequency);
                result.GeometryMs = (long)Math.Round(geometryTicks * 1000.0 / Stopwatch.Frequency);
                this.UpdateMatrixCounters(result.Matrix, processedCells);
                this.PopulateAcceptedPairs(result);
                result.WorkerCount = workerThreads.Count;
                result.ProcessedCellCount = result.Matrix.ProcessedCellCount;
                result.AcceptedCellCount = result.Matrix.AcceptedCellCount;
                result.MatrixSizeBytes = result.Matrix.CompactSizeBytes;
                result.CompletedTileCount = completedTiles;
                result.PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64;
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.RejectReason = "Matching SIFT annullato";
                return result;
            }
            catch (Exception ex)
            {
                result.RejectReason = "Matching SIFT batch fallito: " + ex.Message;
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
        /// Rilascia il wrapper batch, che non possiede risorse native persistenti
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
        private OpenCvSiftFeatureMatcher CreateMatcher()
        {
            return new OpenCvSiftFeatureMatcher(this._options);
        }

        /// <summary>
        /// Traduce il risultato scalare nel contratto della matrice
        /// </summary>
        private DeepSiftMatchCell CreateCell(FrameFeatureMatchResult match)
        {
            DeepSiftMatchCell result = new DeepSiftMatchCell();
            result.State = match != null && match.Accepted ? DeepSiftMatchState.Accepted : DeepSiftMatchState.Rejected;
            result.Score = match != null ? match.Score : 0.0;
            result.InlierCount = match != null ? match.InlierCount : 0;
            result.InlierRatio = match != null ? match.InlierRatio : 0.0;
            result.SourceCoverage = match != null ? match.SourceCoverage : 0.0;
            result.LanguageCoverage = match != null ? match.LanguageCoverage : 0.0;
            result.MeanReprojectionError = match != null ? match.MeanReprojectionError : 0.0;
            return result;
        }

        /// <summary>
        /// Conta i frame che non hanno prodotto descriptor informativi
        /// </summary>
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

        private HashSet<long> CreatePlannedPairKeys(IReadOnlyList<DeepSiftFramePair> pairs, int sourceCount, int languageCount)
        {
            if (pairs == null)
                return null;
            HashSet<long> result = new HashSet<long>();
            for (int i = 0; i < pairs.Count; i++)
            {
                DeepSiftFramePair pair = pairs[i];
                if (pair.SourceAnchorIndex < 0 || pair.SourceAnchorIndex >= sourceCount || pair.LanguageAnchorIndex < 0 || pair.LanguageAnchorIndex >= languageCount)
                    throw new ArgumentOutOfRangeException(nameof(pairs));
                result.Add(this.GetPairKey(pair.SourceAnchorIndex, pair.LanguageAnchorIndex));
            }
            return result;
        }

        private long GetPairKey(int sourceIndex, int languageIndex)
        {
            return ((long)sourceIndex << 32) | (uint)languageIndex;
        }

        #endregion
    }
}
