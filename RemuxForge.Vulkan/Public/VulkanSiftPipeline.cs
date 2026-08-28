using RemuxForge.Vulkan.Runtime;
using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Vision.Geometry;
using RemuxForge.Vulkan.Vision.Matching;
using RemuxForge.Vulkan.Vision.Sift;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace RemuxForge.Vulkan
{
    /// <summary>
    /// Owns the persistent SIFT extraction, reciprocal matching, and RANSAC homography pipeline
    /// </summary>
    public sealed class VulkanSiftPipeline : IDisposable
    {
        #region Constants

        /// <summary>
        /// Upper bound on the number of frame pairs submitted to one matching and RANSAC tile
        /// </summary>
        private const int MAXIMUM_PAIRS_PER_SUBMISSION = 1024;

        #endregion

        #region Instance Fields

        /// <summary>
        /// Context that owns this pipeline and removes it after direct disposal
        /// </summary>
        private readonly VulkanVisionContext _context;

        /// <summary>
        /// Shared Vulkan runtime used by extraction, matching, allocation, and diagnostics
        /// </summary>
        private readonly VulkanRuntimeContext _runtime;

        /// <summary>
        /// Synchronizes pipeline lifecycle state and prepared-batch ownership
        /// </summary>
        private readonly object _lifecycleLock;

        /// <summary>
        /// Pipeline-owned SIFT extractor reused across executions
        /// </summary>
        private readonly VulkanSiftExtractor _extractor;

        /// <summary>
        /// Pipeline-owned matcher for feature collections that remain resident
        /// </summary>
        private readonly VulkanResidentMatcher _matcher;

        /// <summary>
        /// Pipeline-owned RANSAC homography evaluator
        /// </summary>
        private readonly VulkanHomographyRansac _ransac;

        /// <summary>
        /// Cancellation source signaled when the pipeline begins disposal
        /// </summary>
        private readonly CancellationTokenSource _disposeCancellation;

        /// <summary>
        /// Event signaled while no execution or prepared batch holds pipeline ownership
        /// </summary>
        private readonly ManualResetEventSlim _idle;

        /// <summary>
        /// Prepared batches whose resident feature collections are owned by this pipeline
        /// </summary>
        private readonly HashSet<VulkanSiftPreparedBatch> _preparedBatches;

        /// <summary>
        /// Number of active executions and resident prepared batches
        /// </summary>
        private int _activeExecutions;

        /// <summary>
        /// Indicates that disposal has started and new work must be rejected
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a pipeline bound to the specified persistent context and runtime
        /// </summary>
        /// <param name="context">Context that owns the pipeline lifecycle</param>
        /// <param name="runtime">Runtime shared by the context's pipelines</param>
        internal VulkanSiftPipeline(VulkanVisionContext context, VulkanRuntimeContext runtime)
        {
            this._context = context;
            this._runtime = runtime;
            this._lifecycleLock = new object();
            this._extractor = new VulkanSiftExtractor(runtime);
            this._matcher = new VulkanResidentMatcher(runtime);
            this._ransac = new VulkanHomographyRansac(runtime);
            this._disposeCancellation = new CancellationTokenSource();
            this._idle = new ManualResetEventSlim(true);
            this._preparedBatches = new HashSet<VulkanSiftPreparedBatch>();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Executes the complete batch, extracting features for the requested frames
        /// </summary>
        /// <param name="request">Frames, pairs, and algorithm options to process</param>
        /// <param name="cancellationToken">Token used to request cooperative cancellation</param>
        /// <returns>Pair results in request order and aggregated diagnostics</returns>
        public VulkanSiftBatchResult Execute(VulkanSiftBatchRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            this.ThrowIfDisposed();
            ValidateRequest(request);
            cancellationToken.ThrowIfCancellationRequested();
            VulkanSiftBatchResult result = new VulkanSiftBatchResult();
            result.Capabilities = this._runtime.Capabilities;
            result.Diagnostics = new VulkanVisionDiagnostics();
            result.Diagnostics.ProbeTicks = this._runtime.InitializationTicks;
            foreach (KeyValuePair<string, string> metadata in this._runtime.ShaderLoader.BuildMetadata)
                result.Diagnostics.Toolchain.Add(metadata.Key, metadata.Value);
            this.PopulateAlgorithmConfiguration(request.Options, result.Diagnostics);
            this._runtime.DrainValidationMessages(result.Diagnostics);
            if (request.Pairs.Count == 0)
                return result;
            this.EnterExecution();
            using (CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._disposeCancellation.Token))
            {
                cancellationToken = linkedCancellation.Token;
                Stopwatch endToEnd = Stopwatch.StartNew();
                List<FeatureChunk> firstChunks = null;
                List<FeatureChunk> secondChunks = null;
                try
                {
                    Stopwatch phase = Stopwatch.StartNew();
                    firstChunks = this.ExtractChunks(request.FirstFrames, request.Options, result.Diagnostics, cancellationToken);
                    secondChunks = this.ExtractChunks(request.SecondFrames, request.Options, result.Diagnostics, cancellationToken);
                    result.Diagnostics.DescriptorTicks += phase.ElapsedTicks;
                    result.Diagnostics.AlgorithmConfiguration["firstFeatureChunks"] = firstChunks.Count.ToString(CultureInfo.InvariantCulture);
                    result.Diagnostics.AlgorithmConfiguration["secondFeatureChunks"] = secondChunks.Count.ToString(CultureInfo.InvariantCulture);
                    int[] firstFrameCounts = BuildFrameCounts(firstChunks);
                    int[] secondFrameCounts = BuildFrameCounts(secondChunks);
                    result.FirstFrameKeypointCounts = firstFrameCounts;
                    result.SecondFrameKeypointCounts = secondFrameCounts;
                    result.Diagnostics.DeclaredFrameCount = request.FirstFrames.Count + request.SecondFrames.Count;
                    result.Diagnostics.DeclaredPairCount = request.Pairs.Count;
                    VulkanSiftPairResult[] orderedResults = request.OmitFeaturelessPairResults ? Array.Empty<VulkanSiftPairResult>() : new VulkanSiftPairResult[request.Pairs.Count];
                    List<PairGroup> groups = BuildPairGroups(firstChunks, secondChunks, request.Pairs, firstFrameCounts, secondFrameCounts, request.Options.MinimumKeypointsPerFrame, request.OmitFeaturelessPairResults, orderedResults, result.Diagnostics);
                    int activePairCount = 0;
                    for (int i = 0; i < groups.Count; i++)
                        activePairCount += groups[i].Pairs.Count;
                    int processedPairs = 0;
                    for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                    {
                        PairGroup group = groups[groupIndex];
                        int pairCursor = 0;
                        while (pairCursor < group.Pairs.Count)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int tileCount = this.ResolveTileCount(group.First.Features, group.Second.Features, group.Pairs, pairCursor, request.Options);
                            phase.Restart();
                            using (VulkanMatchWorkspace matchWorkspace = this._matcher.Match(group.First.Features, group.Second.Features, group.Pairs, pairCursor, tileCount, request.Options, result.Diagnostics, cancellationToken))
                            {
                                result.Diagnostics.MatchingTicks += phase.ElapsedTicks;
                                phase.Restart();
                                List<VulkanSiftPairResult> tileResults = this._ransac.Execute(group.First.Features, group.Second.Features, group.First.Frames, group.Second.Frames, matchWorkspace, request.Options, result.Diagnostics, cancellationToken);
                                result.Diagnostics.RansacTicks += phase.ElapsedTicks;
                                if (tileResults.Count != tileCount)
                                    throw new VulkanDeviceLostException("The Vulkan tile produced an incomplete result set.");
                                for (int i = 0; i < tileResults.Count; i++)
                                {
                                    int originalIndex = group.OriginalIndexes[pairCursor + i];
                                    tileResults[i].Pair = request.Pairs[originalIndex];
                                    if (request.OmitFeaturelessPairResults)
                                        result.PairResults.Add(tileResults[i]);
                                    else
                                        orderedResults[originalIndex] = tileResults[i];
                                    VulkanSiftRejectReason reason = tileResults[i].RejectReason;
                                    if (!result.Diagnostics.Rejections.TryAdd(reason, 1))
                                        result.Diagnostics.Rejections[reason]++;
                                }
                            }
                            pairCursor += tileCount;
                            processedPairs += tileCount;
                            result.Diagnostics.CompletedTileCount++;
                            request.Progress?.Report(new VulkanVisionProgress
                            {
                                UploadedFrames = request.FirstFrames.Count + request.SecondFrames.Count,
                                TotalFrames = request.FirstFrames.Count + request.SecondFrames.Count,
                                ExtractedFrames = request.FirstFrames.Count + request.SecondFrames.Count,
                                ProcessedPairs = processedPairs,
                                TotalPairs = activePairCount,
                                CompletedTiles = result.Diagnostics.CompletedTileCount,
                                ResidentBytes = this._runtime.Allocator.GetStatistics().UsedBytes
                            });
                        }
                    }
                    if (!request.OmitFeaturelessPairResults)
                        result.PairResults.AddRange(orderedResults);
                    int expectedPairCount = request.OmitFeaturelessPairResults ? activePairCount : request.Pairs.Count;
                    if (result.PairResults.Count != expectedPairCount)
                        throw new VulkanDeviceLostException("The Vulkan pipeline did not materialize every requested pair.");
                    result.Diagnostics.ProcessedPairCount = result.PairResults.Count;
                    for (int i = 0; i < result.PairResults.Count; i++)
                    {
                        if (result.PairResults[i].Status == VulkanSiftPairStatus.Accepted)
                            result.Diagnostics.AcceptedPairCount++;
                    }
                    VulkanMemoryStatistics statistics = this._runtime.Allocator.GetStatistics();
                    result.Diagnostics.CurrentVramBytes = statistics.UsedBytes;
                    result.Diagnostics.PeakVramBytes = Math.Max(result.Diagnostics.PeakVramBytes, statistics.AllocatedBytes);
                    result.Diagnostics.CachedVramBytes = statistics.CachedBytes;
                    result.Diagnostics.WastedVramBytes = statistics.AllocatedBytes - statistics.UsedBytes;
                    result.Diagnostics.AllocationCount = statistics.BlockCount;
                    result.Diagnostics.HostVisibleAllocatedBytes = statistics.HostAllocatedBytes;
                    result.Diagnostics.HostVisibleUsedBytes = statistics.HostUsedBytes;
                    return result;
                }
                catch (VulkanVisionException)
                {
                    throw;
                }
                catch (Exception ex) when (ex.Message.IndexOf("ErrorDeviceLost", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    throw new VulkanDeviceLostException("The Vulkan device was lost while executing the pipeline.", ex);
                }
                finally
                {
                    DisposeChunks(firstChunks);
                    DisposeChunks(secondChunks);
                    result.Diagnostics.EndToEndTicks = endToEnd.ElapsedTicks;
                    this._runtime.DrainValidationMessages(result.Diagnostics);
                    this.ExitExecution();
                }
            }
        }

        /// <summary>
        /// Extracts features once and creates a reusable resident batch owned by the pipeline
        /// </summary>
        /// <param name="firstFrames">First set of frames to keep resident</param>
        /// <param name="secondFrames">Second set of frames to keep resident</param>
        /// <param name="options">SIFT, matching, and RANSAC options, or null to use defaults</param>
        /// <param name="cancellationToken">Token used to request cooperative cancellation</param>
        /// <returns>A resident batch that must be disposed after its last execution</returns>
        public VulkanSiftPreparedBatch Prepare(IReadOnlyList<VulkanImageFrame> firstFrames, IReadOnlyList<VulkanImageFrame> secondFrames, VulkanSiftOptions options = null, CancellationToken cancellationToken = default)
        {
            options = options ?? new VulkanSiftOptions();
            VulkanSiftBatchRequest validationRequest = new VulkanSiftBatchRequest(firstFrames, secondFrames, Array.Empty<VulkanFramePair>(), options);
            this.ThrowIfDisposed();
            ValidateRequest(validationRequest);
            cancellationToken.ThrowIfCancellationRequested();
            this.EnterExecution();
            List<FeatureChunk> firstChunks = null;
            List<FeatureChunk> secondChunks = null;
            VulkanVisionDiagnostics diagnostics = new VulkanVisionDiagnostics();
            diagnostics.ProbeTicks = this._runtime.InitializationTicks;
            foreach (KeyValuePair<string, string> metadata in this._runtime.ShaderLoader.BuildMetadata)
                diagnostics.Toolchain.Add(metadata.Key, metadata.Value);
            this.PopulateAlgorithmConfiguration(options, diagnostics);
            this._runtime.DrainValidationMessages(diagnostics);
            Stopwatch endToEnd = Stopwatch.StartNew();
            try
            {
                using (CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._disposeCancellation.Token))
                {
                    Stopwatch phase = Stopwatch.StartNew();
                    firstChunks = this.ExtractChunks(firstFrames, options, diagnostics, linkedCancellation.Token);
                    secondChunks = this.ExtractChunks(secondFrames, options, diagnostics, linkedCancellation.Token);
                    diagnostics.DescriptorTicks += phase.ElapsedTicks;
                }
                diagnostics.AlgorithmConfiguration["firstFeatureChunks"] = firstChunks.Count.ToString(CultureInfo.InvariantCulture);
                diagnostics.AlgorithmConfiguration["secondFeatureChunks"] = secondChunks.Count.ToString(CultureInfo.InvariantCulture);
                diagnostics.DeclaredFrameCount = firstFrames.Count + secondFrames.Count;
                VulkanSiftPreparedBatch result = new VulkanSiftPreparedBatch(this, firstChunks, secondChunks, BuildFrameCounts(firstChunks), BuildFrameCounts(secondChunks), options, diagnostics, endToEnd);
                this.RegisterPrepared(result);
                return result;
            }
            catch
            {
                DisposeChunks(firstChunks);
                DisposeChunks(secondChunks);
                this.ExitExecution();
                throw;
            }
        }

        /// <summary>
        /// Releases the pipeline, cancels active work, and releases all owned prepared batches
        /// </summary>
        public void Dispose()
        {
            this.DisposeCore(true);
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// Executes matching and RANSAC using features that are already resident
        /// </summary>
        /// <param name="prepared">Prepared batch that owns the resident feature collections</param>
        /// <param name="pairs">Pairs indexed against the frames stored in <paramref name="prepared"/></param>
        /// <param name="progress">Optional receiver for aggregated execution progress</param>
        /// <param name="omitFeaturelessPairResults">Whether to omit materialized results for featureless pairs</param>
        /// <param name="cancellationToken">Token used to request cooperative cancellation</param>
        /// <returns>Pair results and diagnostics for this execution</returns>
        internal VulkanSiftBatchResult ExecutePrepared(VulkanSiftPreparedBatch prepared, IReadOnlyList<VulkanFramePair> pairs, IProgress<VulkanVisionProgress> progress, bool omitFeaturelessPairResults, CancellationToken cancellationToken)
        {
            Stopwatch executionStopwatch = Stopwatch.StartNew();
            if (prepared == null)
                throw new ArgumentNullException(nameof(prepared));
            if (pairs == null)
                throw new ArgumentNullException(nameof(pairs));
            this.ThrowIfDisposed();
            prepared.ThrowIfDisposed();
            for (int i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].FirstFrameIndex < 0 || pairs[i].FirstFrameIndex >= prepared.FirstFrameCounts.Length)
                    throw new ArgumentOutOfRangeException(nameof(pairs));
                if (pairs[i].SecondFrameIndex < 0 || pairs[i].SecondFrameIndex >= prepared.SecondFrameCounts.Length)
                    throw new ArgumentOutOfRangeException(nameof(pairs));
            }

            VulkanSiftBatchResult result = new VulkanSiftBatchResult();
            result.Capabilities = this._runtime.Capabilities;
            result.FirstFrameKeypointCounts = prepared.FirstFrameCounts;
            result.SecondFrameKeypointCounts = prepared.SecondFrameCounts;
            if (pairs.Count == 0)
            {
                result.Diagnostics = prepared.CreateExecutionDiagnostics();
                return result;
            }
            bool includesPreparation;
            VulkanVisionDiagnostics diagnostics = prepared.BeginExecutionDiagnostics(out includesPreparation);
            result.Diagnostics = diagnostics;

            using (CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._disposeCancellation.Token))
            {
                cancellationToken = linkedCancellation.Token;
                VulkanSiftPairResult[] orderedResults = omitFeaturelessPairResults ? Array.Empty<VulkanSiftPairResult>() : new VulkanSiftPairResult[pairs.Count];
                List<PairGroup> groups = BuildPairGroups(prepared.FirstChunks, prepared.SecondChunks, pairs, prepared.FirstFrameCounts, prepared.SecondFrameCounts, prepared.Options.MinimumKeypointsPerFrame, omitFeaturelessPairResults, orderedResults, diagnostics);
                int activePairCount = 0;
                for (int i = 0; i < groups.Count; i++)
                    activePairCount += groups[i].Pairs.Count;
                int processedPairs = 0;
                Stopwatch phase = Stopwatch.StartNew();
                for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                {
                    PairGroup group = groups[groupIndex];
                    int pairCursor = 0;
                    while (pairCursor < group.Pairs.Count)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int tileCount = this.ResolveTileCount(group.First.Features, group.Second.Features, group.Pairs, pairCursor, prepared.Options);
                        phase.Restart();
                        using (VulkanMatchWorkspace matchWorkspace = this._matcher.Match(group.First.Features, group.Second.Features, group.Pairs, pairCursor, tileCount, prepared.Options, diagnostics, cancellationToken))
                        {
                            diagnostics.MatchingTicks += phase.ElapsedTicks;
                            phase.Restart();
                            List<VulkanSiftPairResult> tileResults = this._ransac.Execute(group.First.Features, group.Second.Features, group.First.Frames, group.Second.Frames, matchWorkspace, prepared.Options, diagnostics, cancellationToken);
                            diagnostics.RansacTicks += phase.ElapsedTicks;
                            if (tileResults.Count != tileCount)
                                throw new VulkanDeviceLostException("The Vulkan tile produced an incomplete result set.");
                            for (int i = 0; i < tileResults.Count; i++)
                            {
                                int originalIndex = group.OriginalIndexes[pairCursor + i];
                                tileResults[i].Pair = pairs[originalIndex];
                                if (omitFeaturelessPairResults)
                                    result.PairResults.Add(tileResults[i]);
                                else
                                    orderedResults[originalIndex] = tileResults[i];
                                VulkanSiftRejectReason reason = tileResults[i].RejectReason;
                                if (!diagnostics.Rejections.TryAdd(reason, 1))
                                    diagnostics.Rejections[reason]++;
                            }
                        }
                        pairCursor += tileCount;
                        processedPairs += tileCount;
                        diagnostics.CompletedTileCount++;
                        progress?.Report(new VulkanVisionProgress
                        {
                            UploadedFrames = prepared.FirstFrameCounts.Length + prepared.SecondFrameCounts.Length,
                            TotalFrames = prepared.FirstFrameCounts.Length + prepared.SecondFrameCounts.Length,
                            ExtractedFrames = prepared.FirstFrameCounts.Length + prepared.SecondFrameCounts.Length,
                            ProcessedPairs = processedPairs,
                            TotalPairs = activePairCount,
                            CompletedTiles = diagnostics.CompletedTileCount,
                            ResidentBytes = this._runtime.Allocator.GetStatistics().UsedBytes
                        });
                    }
                }
                if (!omitFeaturelessPairResults)
                    result.PairResults.AddRange(orderedResults);
                int expectedPairCount = omitFeaturelessPairResults ? activePairCount : pairs.Count;
                if (result.PairResults.Count != expectedPairCount)
                    throw new VulkanDeviceLostException("The Vulkan pipeline did not materialize every requested pair.");
                diagnostics.DeclaredPairCount += pairs.Count;
                diagnostics.ProcessedPairCount += result.PairResults.Count;
                for (int i = 0; i < result.PairResults.Count; i++)
                {
                    if (result.PairResults[i].Status == VulkanSiftPairStatus.Accepted)
                        diagnostics.AcceptedPairCount++;
                }
                VulkanMemoryStatistics statistics = this._runtime.Allocator.GetStatistics();
                diagnostics.CurrentVramBytes = statistics.UsedBytes;
                diagnostics.PeakVramBytes = Math.Max(diagnostics.PeakVramBytes, statistics.AllocatedBytes);
                diagnostics.CachedVramBytes = statistics.CachedBytes;
                diagnostics.WastedVramBytes = statistics.AllocatedBytes - statistics.UsedBytes;
                diagnostics.AllocationCount = statistics.BlockCount;
                diagnostics.HostVisibleAllocatedBytes = statistics.HostAllocatedBytes;
                diagnostics.HostVisibleUsedBytes = statistics.HostUsedBytes;
                diagnostics.EndToEndTicks = includesPreparation ? prepared.EndToEnd.ElapsedTicks : executionStopwatch.ElapsedTicks;
                this._runtime.DrainValidationMessages(diagnostics);
                return result;
            }
        }

        /// <summary>
        /// Removes and releases a resident batch owned by the pipeline
        /// </summary>
        /// <param name="prepared">Batch to remove from ownership and release</param>
        internal void ReleasePrepared(VulkanSiftPreparedBatch prepared)
        {
            lock (this._lifecycleLock)
            {
                if (!this._preparedBatches.Remove(prepared))
                    return;
            }
            DisposeChunks(prepared.FirstChunks);
            DisposeChunks(prepared.SecondChunks);
            this._runtime.DrainValidationMessages(prepared.Diagnostics);
            this.ExitExecution();
        }

        /// <summary>
        /// Disposes the pipeline as part of its owning context's teardown
        /// </summary>
        internal void DisposeFromContext()
        {
            this.DisposeCore(false);
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Releases resources and ownership without duplicating the public and context disposal paths
        /// </summary>
        /// <param name="releaseFromContext">Whether direct disposal must remove this pipeline from its context</param>
        private void DisposeCore(bool releaseFromContext)
        {
            List<VulkanSiftPreparedBatch> preparedBatches = null;
            bool ownsDispose = false;
            lock (this._lifecycleLock)
            {
                if (!this._disposed)
                {
                    this._disposed = true;
                    this._disposeCancellation.Cancel();
                    preparedBatches = new List<VulkanSiftPreparedBatch>(this._preparedBatches);
                    ownsDispose = true;
                }
            }
            if (preparedBatches != null)
            {
                // The owner must also release batches that their consumers did not close.
                for (int preparedIndex = 0; preparedIndex < preparedBatches.Count; preparedIndex++)
                    preparedBatches[preparedIndex].DisposeFromOwner();
            }
            this._idle.Wait();
            if (ownsDispose && releaseFromContext)
                this._context.Release(this);
        }

        /// <summary>
        /// Registers a resident batch in the pipeline lifecycle
        /// </summary>
        /// <param name="prepared">Batch whose resident feature collections are now owned by the pipeline</param>
        private void RegisterPrepared(VulkanSiftPreparedBatch prepared)
        {
            lock (this._lifecycleLock)
            {
                this.ThrowIfDisposed();
                this._preparedBatches.Add(prepared);
            }
        }

        /// <summary>
        /// Validates the request, frame indexes, and algorithm options before the first submission
        /// </summary>
        /// <param name="request">Request whose collections, pair indexes, and options must be validated</param>
        private static void ValidateRequest(VulkanSiftBatchRequest request)
        {
            VulkanSiftOptions options = request.Options;
            if (options.OctaveLayers < 1)
                throw new ArgumentOutOfRangeException(nameof(request), options.OctaveLayers, "Octave layers must be greater than zero.");
            if (options.ContrastThreshold <= 0.0f || !float.IsFinite(options.ContrastThreshold))
                throw new ArgumentOutOfRangeException(nameof(request), options.ContrastThreshold, "Contrast threshold must be finite and greater than zero.");
            if (options.EdgeThreshold <= 0.0f || !float.IsFinite(options.EdgeThreshold))
                throw new ArgumentOutOfRangeException(nameof(request), options.EdgeThreshold, "Edge threshold must be finite and greater than zero.");
            if (options.Sigma <= 0.0f || !float.IsFinite(options.Sigma))
                throw new ArgumentOutOfRangeException(nameof(request), options.Sigma, "Sigma must be finite and greater than zero.");
            if (options.FeatureRatio <= 0.0f || options.FeatureRatio > 1.0f || !float.IsFinite(options.FeatureRatio))
                throw new ArgumentOutOfRangeException(nameof(request), options.FeatureRatio, "Feature ratio must be finite and in the range (0, 1].");
            if (options.MaximumFeaturesPerFrame < 1)
                throw new ArgumentOutOfRangeException(nameof(request), options.MaximumFeaturesPerFrame, "Maximum features per frame must be greater than zero.");
            if (options.IntensityScale <= 0.0f || !float.IsFinite(options.IntensityScale))
                throw new ArgumentOutOfRangeException(nameof(request), options.IntensityScale, "Intensity scale must be finite and greater than zero.");
            if (options.LoweRatio <= 0.0f || options.LoweRatio >= 1.0f || !float.IsFinite(options.LoweRatio))
                throw new ArgumentOutOfRangeException(nameof(request), options.LoweRatio, "Lowe ratio must be finite and in the range (0, 1).");
            if (options.RansacHypothesisCount < 1)
                throw new ArgumentOutOfRangeException(nameof(request), options.RansacHypothesisCount, "RANSAC hypothesis count must be greater than zero.");
            if (options.MinimumKeypointsPerFrame < 1)
                throw new ArgumentOutOfRangeException(nameof(request), options.MinimumKeypointsPerFrame, "Minimum keypoints per frame must be greater than zero.");
            if (options.MinimumReciprocalMatches < 1)
                throw new ArgumentOutOfRangeException(nameof(request), options.MinimumReciprocalMatches, "Minimum reciprocal matches must be greater than zero.");
            if (options.MinimumInliers < 1)
                throw new ArgumentOutOfRangeException(nameof(request), options.MinimumInliers, "Minimum inliers must be greater than zero.");
            if (options.MinimumInlierRatio < 0.0f || options.MinimumInlierRatio > 1.0f || !float.IsFinite(options.MinimumInlierRatio))
                throw new ArgumentOutOfRangeException(nameof(request), options.MinimumInlierRatio, "Minimum inlier ratio must be finite and in the range [0, 1].");
            if (options.MinimumCoverage < 0.0f || options.MinimumCoverage > 1.0f || !float.IsFinite(options.MinimumCoverage))
                throw new ArgumentOutOfRangeException(nameof(request), options.MinimumCoverage, "Minimum coverage must be finite and in the range [0, 1].");
            if (options.MaximumMeanReprojectionError <= 0.0f || !float.IsFinite(options.MaximumMeanReprojectionError))
                throw new ArgumentOutOfRangeException(nameof(request), options.MaximumMeanReprojectionError, "Maximum mean reprojection error must be finite and greater than zero.");
            if (options.RansacReprojectionThreshold <= 0.0f || !float.IsFinite(options.RansacReprojectionThreshold))
                throw new ArgumentOutOfRangeException(nameof(request), options.RansacReprojectionThreshold, "RANSAC reprojection threshold must be finite and greater than zero.");
            if (options.MinimumHomographyAreaRatio <= 0.0f || !float.IsFinite(options.MinimumHomographyAreaRatio))
                throw new ArgumentOutOfRangeException(nameof(request), options.MinimumHomographyAreaRatio, "Minimum homography area ratio must be finite and greater than zero.");
            if (options.MaximumHomographyAreaRatio < options.MinimumHomographyAreaRatio || !float.IsFinite(options.MaximumHomographyAreaRatio))
                throw new ArgumentOutOfRangeException(nameof(request), options.MaximumHomographyAreaRatio, "Maximum homography area ratio must be finite and no less than the minimum.");
            for (int i = 0; i < request.FirstFrames.Count; i++)
            {
                if (request.FirstFrames[i] == null)
                    throw new ArgumentException("The first collection contains a null frame.", nameof(request));
            }
            for (int i = 0; i < request.SecondFrames.Count; i++)
            {
                if (request.SecondFrames[i] == null)
                    throw new ArgumentException("The second collection contains a null frame.", nameof(request));
            }
            for (int i = 0; i < request.Pairs.Count; i++)
            {
                VulkanFramePair pair = request.Pairs[i];
                if (pair.FirstFrameIndex < 0 || pair.FirstFrameIndex >= request.FirstFrames.Count)
                    throw new ArgumentOutOfRangeException(nameof(request), "First frame index is outside the available frame range.");
                if (pair.SecondFrameIndex < 0 || pair.SecondFrameIndex >= request.SecondFrames.Count)
                    throw new ArgumentOutOfRangeException(nameof(request), "Second frame index is outside the available frame range.");
            }
        }

        /// <summary>
        /// Calculates a tile size that fits the device and available VRAM limits
        /// </summary>
        /// <param name="first">First resident feature collection</param>
        /// <param name="second">Second resident feature collection</param>
        /// <param name="pairs">Pair list from which the tile is selected</param>
        /// <param name="start">First pair position considered for the tile</param>
        /// <param name="options">Algorithm options that determine the RANSAC workspace size</param>
        /// <returns>Number of pairs that can be submitted in the next tile</returns>
        private int ResolveTileCount(VulkanSiftFeatureCollection first, VulkanSiftFeatureCollection second, IReadOnlyList<VulkanFramePair> pairs, int start, VulkanSiftOptions options)
        {
            VulkanMemoryStatistics statistics = this._runtime.Allocator.GetStatistics();
            ulong cachedDeviceBytes = this._runtime.ResourcePool.GetCachedDeviceBytes();
            ulong committedBytes = statistics.UsedBytes > cachedDeviceBytes ? statistics.UsedBytes - cachedDeviceBytes : 0UL;
            ulong available = statistics.PressureThreshold > committedBytes ? statistics.PressureThreshold - committedBytes : 0;
            if (available < 1024UL * 1024UL)
            {
                this._runtime.ResourcePool.Trim();
                statistics = this._runtime.Allocator.GetStatistics();
                available = statistics.PressureThreshold > statistics.AllocatedBytes ? statistics.PressureThreshold - statistics.AllocatedBytes : 0;
            }
            int maximumByDevice = checked((int)Math.Min((uint)MAXIMUM_PAIRS_PER_SUBMISSION, this._runtime.Capabilities.MaximumComputeWorkGroupCountY));
            int result = 0;
            ulong nearestElements = 0;
            ulong reciprocalElements = 0;
            ulong forwardJobs = 0;
            ulong reverseJobs = 0;
            ulong maximumBufferRange = this._runtime.Capabilities.MaximumStorageBufferRange == 0 ? ulong.MaxValue : this._runtime.Capabilities.MaximumStorageBufferRange;
            for (int i = start; i < pairs.Count && result < maximumByDevice; i++)
            {
                VulkanFramePair pair = pairs[i];
                ulong firstCapacity = checked((ulong)first.Frames[pair.FirstFrameIndex].Capacity);
                ulong secondCapacity = checked((ulong)second.Frames[pair.SecondFrameIndex].Capacity);
                ulong candidateNearest = checked(nearestElements + firstCapacity + secondCapacity);
                ulong candidateReciprocal = checked(reciprocalElements + firstCapacity);
                ulong candidateForwardJobs = checked(forwardJobs + RoundUp(firstCapacity, 16UL));
                ulong candidateReverseJobs = checked(reverseJobs + RoundUp(secondCapacity, 16UL));
                ulong candidateJobs = checked(candidateForwardJobs + candidateReverseJobs);
                ulong candidatePairCount = checked((ulong)result + 1UL);
                ulong scanScratchElements = ResolveScanScratchElements(candidateReciprocal);
                ulong matchBytes = checked(candidateNearest * 16UL);
                matchBytes = checked(matchBytes + candidateReciprocal * 40UL);
                matchBytes = checked(matchBytes + scanScratchElements * sizeof(uint));
                matchBytes = checked(matchBytes + candidateJobs * 8UL);
                matchBytes = checked(matchBytes + candidatePairCount * 56UL + 32UL);
                ulong ransacBytes = checked(candidatePairCount * (ulong)options.RansacHypothesisCount * 64UL);
                ransacBytes = checked(ransacBytes + candidatePairCount * 304UL + 7UL * sizeof(uint));
                ulong required = checked(matchBytes + ransacBytes);
                bool integerOverflow = candidateNearest > int.MaxValue || candidateReciprocal > int.MaxValue - 255UL || candidateForwardJobs > int.MaxValue || candidateReverseJobs > int.MaxValue || candidateJobs > int.MaxValue;
                bool storageOverflow = candidateNearest * 16UL > maximumBufferRange || candidateReciprocal * 16UL > maximumBufferRange || candidateJobs * 8UL > maximumBufferRange || candidatePairCount * (ulong)options.RansacHypothesisCount * 64UL > maximumBufferRange;
                if (integerOverflow || storageOverflow || required > available)
                    break;
                nearestElements = candidateNearest;
                reciprocalElements = candidateReciprocal;
                forwardJobs = candidateForwardJobs;
                reverseJobs = candidateReverseJobs;
                result++;
            }
            if (result == 0)
                throw new VulkanResourceExhaustedException("A single SIFT/RANSAC pair exceeds the available VRAM budget.");
            return result;
        }

        /// <summary>
        /// Calculates scratch elements required by the hierarchical prefix scan
        /// </summary>
        /// <param name="elementCount">Number of elements to scan</param>
        /// <returns>Total scratch elements, including the fixed base allocation</returns>
        private static ulong ResolveScanScratchElements(ulong elementCount)
        {
            ulong total = 512UL;
            ulong blocks = (elementCount + 255UL) / 256UL;
            while (blocks > 1UL)
            {
                total = checked(total + blocks * 2UL);
                blocks = (blocks + 255UL) / 256UL;
            }
            return total;
        }

        /// <summary>
        /// Rounds a value up to the next multiple of an alignment
        /// </summary>
        /// <param name="value">Value to round</param>
        /// <param name="alignment">Required alignment, which must be non-zero</param>
        /// <returns>The smallest aligned value that is greater than or equal to <paramref name="value"/></returns>
        private static ulong RoundUp(ulong value, ulong alignment)
        {
            return checked((value + alignment - 1UL) / alignment * alignment);
        }

        /// <summary>
        /// Extracts features in bounded windows while preserving frame order
        /// </summary>
        /// <param name="frames">Frames to process in source order</param>
        /// <param name="options">SIFT options used for extraction and feature limiting</param>
        /// <param name="diagnostics">Diagnostics object updated with extraction measurements</param>
        /// <param name="cancellationToken">Token used to request cooperative cancellation</param>
        /// <returns>Resident feature chunks covering the input frames in order</returns>
        private List<FeatureChunk> ExtractChunks(IReadOnlyList<VulkanImageFrame> frames, VulkanSiftOptions options, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            List<FeatureChunk> result = new List<FeatureChunk>();
            int start = 0;
            try
            {
                while (start < frames.Count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = this.ResolveExtractionChunkCount(frames, start, options);
                    List<VulkanImageFrame> chunkFrames = new List<VulkanImageFrame>(count);
                    for (int i = 0; i < count; i++)
                        chunkFrames.Add(frames[start + i]);
                    VulkanSiftFeatureCollection extracted = null;
                    VulkanSiftFeatureCollection limited = null;
                    try
                    {
                        extracted = this._extractor.ExtractPacked(chunkFrames, options, diagnostics, null, cancellationToken);
                        limited = extracted.Limit(this._runtime, options.MaximumFeaturesPerFrame, diagnostics, cancellationToken);
                        VulkanSiftFeatureCollection.FrameDiagnosticRecord[] records = limited.ReadDiagnostics(this._runtime, diagnostics, cancellationToken);
                        this.AccumulateFrameDiagnostics(records, options.MinimumKeypointsPerFrame, diagnostics);
                        VulkanSiftFeatureCollection compact = limited.Compact(this._runtime, records, diagnostics, cancellationToken);
                        result.Add(new FeatureChunk(result.Count, start, chunkFrames, compact));
                    }
                    finally
                    {
                        limited?.Dispose();
                        extracted?.Dispose();
                    }
                    start += count;
                }
                return result;
            }
            catch
            {
                DisposeChunks(result);
                throw;
            }
        }

        /// <summary>
        /// Calculates how many frames can coexist within the current extraction budget
        /// </summary>
        /// <param name="frames">Frames from which the next extraction window is selected</param>
        /// <param name="start">First frame index considered for the window</param>
        /// <param name="options">SIFT options used to estimate feature capacities</param>
        /// <returns>Number of frames that fit the current budget</returns>
        private int ResolveExtractionChunkCount(IReadOnlyList<VulkanImageFrame> frames, int start, VulkanSiftOptions options)
        {
            VulkanMemoryStatistics statistics = this._runtime.Allocator.GetStatistics();
            ulong cachedDeviceBytes = this._runtime.ResourcePool.GetCachedDeviceBytes();
            ulong committedBytes = statistics.UsedBytes > cachedDeviceBytes ? statistics.UsedBytes - cachedDeviceBytes : 0UL;
            ulong available = statistics.PressureThreshold > committedBytes ? statistics.PressureThreshold - committedBytes : 0UL;
            ulong budget = available * 3UL / 4UL;
            ulong maximumBufferBytes = this._runtime.Capabilities.MaximumStorageBufferRange == 0 ? ulong.MaxValue : this._runtime.Capabilities.MaximumStorageBufferRange;
            List<VulkanImageFrame> candidateFrames = new List<VulkanImageFrame>();
            List<VulkanSiftPlan> candidatePlans = new List<VulkanSiftPlan>();
            ulong totalCapacity = 0;
            ulong limitedCapacity = 0;
            int result = 0;
            while (start + result < frames.Count && result < 256)
            {
                VulkanImageFrame frame = frames[start + result];
                VulkanImageFrame referenceFrame = frames[start];
                bool compatibleLayout = frame.Width == referenceFrame.Width
                    && frame.Height == referenceFrame.Height
                    && frame.Stride == referenceFrame.Stride
                    && frame.PixelFormat == referenceFrame.PixelFormat
                    && frame.RgbToGrayMatrix == referenceFrame.RgbToGrayMatrix;
                if (result > 0 && !compatibleLayout)
                    break;
                VulkanSiftPlan plan = VulkanSiftPlan.Create(frame.Width, frame.Height, options);
                candidateFrames.Add(frame);
                candidatePlans.Add(plan);
                ulong candidateCapacity = checked(totalCapacity + (ulong)plan.OrientationCapacity);
                ulong candidateLimitedCapacity = checked(limitedCapacity + (ulong)Math.Min(plan.OrientationCapacity, options.MaximumFeaturesPerFrame));
                ulong extractedBytes = this.GetFeatureCollectionDeviceBytes(candidateCapacity, result + 1);
                ulong limitedBytes = this.GetFeatureCollectionDeviceBytes(candidateLimitedCapacity, result + 1);
                VulkanSiftPackedPlan packedPlan = new VulkanSiftPackedPlan(candidateFrames, candidatePlans, this._runtime.Capabilities.MinimumStorageBufferOffsetAlignment);
                ulong concurrentBytes = checked(extractedBytes + Math.Max(packedPlan.GetDeviceWorkspaceBytes(), checked(limitedBytes * 2UL)));
                bool bufferTooLarge = packedPlan.GetMaximumDeviceBufferBytes() > maximumBufferBytes || checked(candidateCapacity * 128UL) > maximumBufferBytes;
                if (bufferTooLarge || concurrentBytes > budget)
                    break;
                totalCapacity = candidateCapacity;
                limitedCapacity = candidateLimitedCapacity;
                result++;
            }
            if (result == 0)
                throw new VulkanResourceExhaustedException("A single SIFT frame exceeds the available VRAM budget.");
            return result;
        }

        /// <summary>
        /// Estimates device bytes occupied by a feature collection with the specified capacity
        /// </summary>
        /// <param name="capacity">Total feature capacity across the collection</param>
        /// <param name="frameCount">Number of frames represented by the collection</param>
        /// <returns>Size-class-rounded device allocation estimate</returns>
        private ulong GetFeatureCollectionDeviceBytes(ulong capacity, int frameCount)
        {
            ulong result = VulkanResourcePool.ResolveSizeClass(Math.Max(1UL, checked(capacity * 32UL)));
            result = checked(result + VulkanResourcePool.ResolveSizeClass(Math.Max(1UL, checked(capacity * 128UL))));
            result = checked(result + VulkanResourcePool.ResolveSizeClass(Math.Max(1UL, checked(capacity * sizeof(uint)))));
            result = checked(result + VulkanResourcePool.ResolveSizeClass(Math.Max(1UL, checked((ulong)frameCount * sizeof(uint)))));
            return checked(result + VulkanResourcePool.ResolveSizeClass(Math.Max(1UL, checked((ulong)frameCount * 16UL))));
        }

        /// <summary>
        /// Groups pairs by resident chunks and materializes featureless rejections
        /// </summary>
        /// <param name="firstChunks">Resident chunks for the first frame set</param>
        /// <param name="secondChunks">Resident chunks for the second frame set</param>
        /// <param name="pairs">Pairs to group using global frame indexes</param>
        /// <param name="firstFrameCounts">Per-frame feature capacities for the first frame set</param>
        /// <param name="secondFrameCounts">Per-frame feature capacities for the second frame set</param>
        /// <param name="minimumKeypoints">Minimum feature count required for matching</param>
        /// <param name="omitFeaturelessResults">Whether featureless pair results should be omitted</param>
        /// <param name="orderedResults">Output array used to preserve original pair order when results are retained</param>
        /// <param name="diagnostics">Diagnostics object updated with featureless rejection counts</param>
        /// <returns>Groups whose pairs share the same resident feature collections</returns>
        private static List<PairGroup> BuildPairGroups(List<FeatureChunk> firstChunks, List<FeatureChunk> secondChunks, IReadOnlyList<VulkanFramePair> pairs, int[] firstFrameCounts, int[] secondFrameCounts, int minimumKeypoints, bool omitFeaturelessResults, VulkanSiftPairResult[] orderedResults, VulkanVisionDiagnostics diagnostics)
        {
            FeatureChunk[] firstMap = BuildChunkMap(firstChunks);
            FeatureChunk[] secondMap = BuildChunkMap(secondChunks);
            Dictionary<long, PairGroup> map = new Dictionary<long, PairGroup>();
            List<PairGroup> result = new List<PairGroup>();
            int featurelessFirstCount = 0;
            int featurelessSecondCount = 0;
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                VulkanFramePair pair = pairs[pairIndex];
                VulkanSiftRejectReason rejectReason = VulkanSiftRejectReason.None;
                if (firstFrameCounts[pair.FirstFrameIndex] < minimumKeypoints)
                {
                    rejectReason = VulkanSiftRejectReason.FeaturelessFirstFrame;
                    featurelessFirstCount++;
                }
                else if (secondFrameCounts[pair.SecondFrameIndex] < minimumKeypoints)
                {
                    rejectReason = VulkanSiftRejectReason.FeaturelessSecondFrame;
                    featurelessSecondCount++;
                }
                if (rejectReason != VulkanSiftRejectReason.None)
                {
                    if (!omitFeaturelessResults)
                    {
                        orderedResults[pairIndex] = new VulkanSiftPairResult
                        {
                            Pair = pair,
                            Status = VulkanSiftPairStatus.Rejected,
                            RejectReason = rejectReason,
                            FirstKeypointCount = firstFrameCounts[pair.FirstFrameIndex],
                            SecondKeypointCount = secondFrameCounts[pair.SecondFrameIndex],
                            Homography = new float[9]
                        };
                    }
                    continue;
                }
                FeatureChunk first = firstMap[pair.FirstFrameIndex];
                FeatureChunk second = secondMap[pair.SecondFrameIndex];
                long key = ((long)first.Index << 32) | (uint)second.Index;
                if (!map.TryGetValue(key, out PairGroup group))
                {
                    group = new PairGroup(first, second);
                    map.Add(key, group);
                    result.Add(group);
                }
                group.Pairs.Add(new VulkanFramePair
                {
                    FirstFrameIndex = pair.FirstFrameIndex - first.Start,
                    SecondFrameIndex = pair.SecondFrameIndex - second.Start
                });
                group.OriginalIndexes.Add(pairIndex);
            }
            if (featurelessFirstCount > 0)
                diagnostics.Rejections[VulkanSiftRejectReason.FeaturelessFirstFrame] = featurelessFirstCount;
            if (featurelessSecondCount > 0)
                diagnostics.Rejections[VulkanSiftRejectReason.FeaturelessSecondFrame] = featurelessSecondCount;
            return result;
        }

        /// <summary>
        /// Reconstructs per-frame feature counts from extracted chunks
        /// </summary>
        /// <param name="chunks">Resident chunks covering the frame sequence</param>
        /// <returns>Feature capacity for each global frame index</returns>
        private static int[] BuildFrameCounts(List<FeatureChunk> chunks)
        {
            FeatureChunk[] map = BuildChunkMap(chunks);
            int[] result = new int[map.Length];
            for (int frameIndex = 0; frameIndex < map.Length; frameIndex++)
            {
                FeatureChunk chunk = map[frameIndex];
                result[frameIndex] = chunk.Features.Frames[frameIndex - chunk.Start].Capacity;
            }
            return result;
        }

        /// <summary>
        /// Builds a direct frame-to-chunk map
        /// </summary>
        /// <param name="chunks">Resident chunks with contiguous frame ranges</param>
        /// <returns>Chunk reference for each global frame index</returns>
        private static FeatureChunk[] BuildChunkMap(List<FeatureChunk> chunks)
        {
            int frameCount = chunks.Count == 0 ? 0 : chunks[chunks.Count - 1].Start + chunks[chunks.Count - 1].Frames.Count;
            FeatureChunk[] result = new FeatureChunk[frameCount];
            for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                FeatureChunk chunk = chunks[chunkIndex];
                for (int localIndex = 0; localIndex < chunk.Frames.Count; localIndex++)
                    result[chunk.Start + localIndex] = chunk;
            }
            return result;
        }

        /// <summary>
        /// Releases all resident feature collections in reverse chunk order
        /// </summary>
        /// <param name="chunks">Chunks whose feature collections must be disposed, or null</param>
        private static void DisposeChunks(List<FeatureChunk> chunks)
        {
            if (chunks == null)
                return;
            for (int i = chunks.Count - 1; i >= 0; i--)
                chunks[i].Features.Dispose();
        }

        /// <summary>
        /// Aggregates per-frame counters into batch diagnostics
        /// </summary>
        /// <param name="records">Per-frame diagnostic records read from the resident collection</param>
        /// <param name="minimumKeypoints">Minimum descriptor count that marks a frame as active</param>
        /// <param name="diagnostics">Diagnostics object to update</param>
        private void AccumulateFrameDiagnostics(VulkanSiftFeatureCollection.FrameDiagnosticRecord[] records, int minimumKeypoints, VulkanVisionDiagnostics diagnostics)
        {
            for (int i = 0; i < records.Length; i++)
            {
                diagnostics.CandidateKeypointCount += records[i].CandidateCount;
                diagnostics.RefinedKeypointCount += records[i].RefinedCount;
                diagnostics.TruncatedKeypointCount += records[i].TruncatedCount;
                diagnostics.DescriptorCount += records[i].DescriptorCount;
                if (records[i].DescriptorCount >= minimumKeypoints)
                    diagnostics.ActiveFrameCount++;
                else
                    diagnostics.FeaturelessFrameCount++;
            }
        }

        /// <summary>
        /// Records the effective algorithm parameters used by an execution
        /// </summary>
        /// <param name="options">Options whose values are copied to diagnostics</param>
        /// <param name="diagnostics">Diagnostics object to update</param>
        private void PopulateAlgorithmConfiguration(VulkanSiftOptions options, VulkanVisionDiagnostics diagnostics)
        {
            diagnostics.AlgorithmConfiguration["octaveLayers"] = options.OctaveLayers.ToString(CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["contrastThreshold"] = options.ContrastThreshold.ToString("R", CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["edgeThreshold"] = options.EdgeThreshold.ToString("R", CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["sigma"] = options.Sigma.ToString("R", CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["doubleInput"] = options.DoubleInput.ToString(CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["intensityScale"] = options.IntensityScale.ToString("R", CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["featureRatio"] = options.FeatureRatio.ToString("R", CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["maximumFeaturesPerFrame"] = options.MaximumFeaturesPerFrame.ToString(CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["loweRatio"] = options.LoweRatio.ToString("R", CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["minimumKeypointsPerFrame"] = options.MinimumKeypointsPerFrame.ToString(CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["minimumReciprocalMatches"] = options.MinimumReciprocalMatches.ToString(CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["minimumInliers"] = options.MinimumInliers.ToString(CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["minimumInlierRatio"] = options.MinimumInlierRatio.ToString("R", CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["minimumCoverage"] = options.MinimumCoverage.ToString("R", CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["maximumMeanReprojectionError"] = options.MaximumMeanReprojectionError.ToString("R", CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["minimumHomographyAreaRatio"] = options.MinimumHomographyAreaRatio.ToString("R", CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["maximumHomographyAreaRatio"] = options.MaximumHomographyAreaRatio.ToString("R", CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["ransacReprojectionThreshold"] = options.RansacReprojectionThreshold.ToString("R", CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["ransacHypothesisCount"] = options.RansacHypothesisCount.ToString(CultureInfo.InvariantCulture);
            diagnostics.AlgorithmConfiguration["randomSeed"] = options.RandomSeed.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Rejects use of the instance after disposal has started
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (this._disposed)
                throw new ObjectDisposedException(nameof(VulkanSiftPipeline));
        }

        /// <summary>
        /// Registers an active execution in the thread-safe lifecycle state
        /// </summary>
        private void EnterExecution()
        {
            lock (this._lifecycleLock)
            {
                this.ThrowIfDisposed();
                this._activeExecutions++;
                this._idle.Reset();
            }
        }

        /// <summary>
        /// Completes an active execution and signals the idle state when ownership reaches zero
        /// </summary>
        private void ExitExecution()
        {
            lock (this._lifecycleLock)
            {
                this._activeExecutions--;
                if (this._activeExecutions == 0)
                    this._idle.Set();
            }
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Associates a frame range with its resident features
        /// </summary>
        internal sealed class FeatureChunk
        {
            /// <summary>
            /// Creates a chunk descriptor for a contiguous frame range
            /// </summary>
            /// <param name="index">Stable chunk index used for grouping pairs</param>
            /// <param name="start">Global index of the first frame in the chunk</param>
            /// <param name="frames">Frames retained by the chunk for downstream RANSAC evaluation</param>
            /// <param name="features">Compact feature collection owned by the chunk</param>
            public FeatureChunk(int index, int start, IReadOnlyList<VulkanImageFrame> frames, VulkanSiftFeatureCollection features)
            {
                this.Index = index;
                this.Start = start;
                this.Frames = frames;
                this.Features = features;
            }

            /// <summary>
            /// Stable index of this chunk in the extraction result
            /// </summary>
            public int Index { get; }

            /// <summary>
            /// Global frame index at which this chunk starts
            /// </summary>
            public int Start { get; }

            /// <summary>
            /// Frames represented by the chunk and retained for homography evaluation
            /// </summary>
            public IReadOnlyList<VulkanImageFrame> Frames { get; }

            /// <summary>
            /// Compact resident features owned and disposed by the containing pipeline
            /// </summary>
            public VulkanSiftFeatureCollection Features { get; }
        }

        /// <summary>
        /// Groups pairs that share the same resident feature chunks
        /// </summary>
        private sealed class PairGroup
        {
            /// <summary>
            /// Creates an empty group for two resident feature chunks
            /// </summary>
            /// <param name="first">Resident chunk for the first frame set</param>
            /// <param name="second">Resident chunk for the second frame set</param>
            public PairGroup(FeatureChunk first, FeatureChunk second)
            {
                this.First = first;
                this.Second = second;
                this.Pairs = new List<VulkanFramePair>();
                this.OriginalIndexes = new List<int>();
            }

            /// <summary>
            /// Resident chunk for the first frame set
            /// </summary>
            public FeatureChunk First { get; }

            /// <summary>
            /// Resident chunk for the second frame set
            /// </summary>
            public FeatureChunk Second { get; }

            /// <summary>
            /// Pair indexes translated to the local coordinates of the two chunks
            /// </summary>
            public List<VulkanFramePair> Pairs { get; }

            /// <summary>
            /// Original indexes used to restore tile results to request order
            /// </summary>
            public List<int> OriginalIndexes { get; }
        }

        #endregion
    }

    /// <summary>
    /// Reusable resident SIFT features for multiple pair sets
    /// </summary>
    public sealed class VulkanSiftPreparedBatch : IDisposable
    {
        #region Instance Fields

        /// <summary>
        /// Pipeline that owns the resident feature collections and lifecycle registration
        /// </summary>
        private readonly VulkanSiftPipeline _owner;

        /// <summary>
        /// Serializes executions and disposal of this prepared batch
        /// </summary>
        private readonly object _executionLock;

        /// <summary>
        /// Indicates whether preparation costs have already been attributed to a non-empty execution
        /// </summary>
        private bool _preparationDiagnosticsClaimed;

        /// <summary>
        /// Indicates that this batch no longer owns usable resident resources
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a prepared batch from resident feature chunks and their execution metadata
        /// </summary>
        /// <param name="owner">Pipeline that owns and registered this batch</param>
        /// <param name="firstChunks">Resident chunks for the first frame set</param>
        /// <param name="secondChunks">Resident chunks for the second frame set</param>
        /// <param name="firstFrameCounts">Per-frame feature capacities for the first frame set</param>
        /// <param name="secondFrameCounts">Per-frame feature capacities for the second frame set</param>
        /// <param name="options">Options used to create the resident features</param>
        /// <param name="diagnostics">Diagnostics accumulated during preparation</param>
        /// <param name="endToEnd">Stopwatch started during preparation and used for the preparation-attributed execution timestamp</param>
        internal VulkanSiftPreparedBatch(VulkanSiftPipeline owner, List<VulkanSiftPipeline.FeatureChunk> firstChunks, List<VulkanSiftPipeline.FeatureChunk> secondChunks, int[] firstFrameCounts, int[] secondFrameCounts, VulkanSiftOptions options, VulkanVisionDiagnostics diagnostics, Stopwatch endToEnd)
        {
            this._owner = owner;
            this._executionLock = new object();
            this.FirstChunks = firstChunks;
            this.SecondChunks = secondChunks;
            this.FirstFrameCounts = firstFrameCounts;
            this.SecondFrameCounts = secondFrameCounts;
            this.Options = options;
            this.Diagnostics = diagnostics;
            this.EndToEnd = endToEnd;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Executes matching and RANSAC for the specified pairs without re-extracting features
        /// </summary>
        /// <param name="pairs">Pairs indexed against the prepared frames</param>
        /// <param name="progress">Optional receiver for aggregated execution progress</param>
        /// <param name="omitFeaturelessPairResults">Whether to omit materialized results for featureless pairs</param>
        /// <param name="cancellationToken">Token used to request cooperative cancellation</param>
        /// <returns>Pair results in request order and execution diagnostics</returns>
        public VulkanSiftBatchResult Execute(IReadOnlyList<VulkanFramePair> pairs, IProgress<VulkanVisionProgress> progress = null, bool omitFeaturelessPairResults = false, CancellationToken cancellationToken = default)
        {
            lock (this._executionLock)
            {
                this.ThrowIfDisposed();
                return this._owner.ExecutePrepared(this, pairs, progress, omitFeaturelessPairResults, cancellationToken);
            }
        }

        /// <summary>
        /// Releases the resident features and buffers owned by the batch
        /// </summary>
        public void Dispose()
        {
            this.DisposeCore();
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// Releases the batch from the owning pipeline without suppressing finalization
        /// </summary>
        internal void DisposeFromOwner()
        {
            this.DisposeCore();
        }

        /// <summary>
        /// Attributes preparation costs only to the first non-empty execution of the resident batch
        /// </summary>
        /// <param name="includesPreparation">Receives whether the returned diagnostics include preparation costs</param>
        /// <returns>Diagnostics instance to update for the current execution</returns>
        internal VulkanVisionDiagnostics BeginExecutionDiagnostics(out bool includesPreparation)
        {
            includesPreparation = !this._preparationDiagnosticsClaimed;
            if (includesPreparation)
            {
                this._preparationDiagnosticsClaimed = true;
                return this.Diagnostics;
            }

            return this.CreateExecutionDiagnostics();
        }

        /// <summary>
        /// Creates diagnostics for a subsequent execution of the prepared batch
        /// </summary>
        /// <returns>A new diagnostics instance containing stable preparation metadata</returns>
        internal VulkanVisionDiagnostics CreateExecutionDiagnostics()
        {
            VulkanVisionDiagnostics result = new VulkanVisionDiagnostics();
            result.DeclaredFrameCount = this.Diagnostics.DeclaredFrameCount;
            foreach (KeyValuePair<string, string> entry in this.Diagnostics.AlgorithmConfiguration)
                result.AlgorithmConfiguration[entry.Key] = entry.Value;
            foreach (KeyValuePair<string, string> entry in this.Diagnostics.ShaderHashes)
                result.ShaderHashes[entry.Key] = entry.Value;
            foreach (KeyValuePair<string, string> entry in this.Diagnostics.Toolchain)
                result.Toolchain[entry.Key] = entry.Value;
            return result;
        }

        /// <summary>
        /// Rejects use of the batch after disposal
        /// </summary>
        internal void ThrowIfDisposed()
        {
            if (this._disposed)
                throw new ObjectDisposedException(nameof(VulkanSiftPreparedBatch));
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Releases resources and ownership without duplicating public and owner disposal paths
        /// </summary>
        private void DisposeCore()
        {
            lock (this._executionLock)
            {
                if (this._disposed)
                    return;
                this._disposed = true;
                this.EndToEnd.Stop();
                this._owner.ReleasePrepared(this);
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Resident feature chunks for the first frame set
        /// </summary>
        internal List<VulkanSiftPipeline.FeatureChunk> FirstChunks { get; }

        /// <summary>
        /// Resident feature chunks for the second frame set
        /// </summary>
        internal List<VulkanSiftPipeline.FeatureChunk> SecondChunks { get; }

        /// <summary>
        /// Per-frame feature capacities for the first frame set
        /// </summary>
        internal int[] FirstFrameCounts { get; }

        /// <summary>
        /// Per-frame feature capacities for the second frame set
        /// </summary>
        internal int[] SecondFrameCounts { get; }

        /// <summary>
        /// Options used to extract and process the resident features
        /// </summary>
        internal VulkanSiftOptions Options { get; }

        /// <summary>
        /// Diagnostics accumulated during preparation and the first attributed non-empty execution
        /// </summary>
        internal VulkanVisionDiagnostics Diagnostics { get; }

        /// <summary>
        /// Stopwatch started during preparation and used for the preparation-attributed execution timestamp
        /// </summary>
        internal Stopwatch EndToEnd { get; }

        #endregion
    }
}
