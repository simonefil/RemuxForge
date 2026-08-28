using RemuxForge.Vulkan.Runtime;
using RemuxForge.Vulkan.Vision.Hash;
using RemuxForge.Vulkan.Vision.Matching;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace RemuxForge.Vulkan
{
    /// <summary>
    /// Owns the persistent perceptual hash extraction and offset measurement pipeline
    /// </summary>
    public sealed class VulkanHashPipeline : IDisposable
    {
        #region Constants

        /// <summary>
        /// Side in pixels of the analysis square the extraction shader hashes
        /// </summary>
        public const int FrameSide = VulkanHashExtractor.FRAME_SIDE;

        /// <summary>
        /// Size in bytes of one analysis square
        /// </summary>
        public const int FrameBytes = VulkanHashExtractor.FRAME_BYTES;

        /// <summary>
        /// Number of analysis squares one extraction submission covers, and the batch size that keeps the device busy
        /// </summary>
        public const int FramesPerSubmission = VulkanHashExtractor.FRAMES_PER_SUBMISSION;

        #endregion

        #region Class Fields

        /// <summary>
        /// Context that owns this pipeline lifecycle
        /// </summary>
        private readonly VulkanVisionContext _context;

        /// <summary>
        /// Runtime shared by the context pipelines
        /// </summary>
        private readonly VulkanRuntimeContext _runtime;

        /// <summary>
        /// Synchronizes lifecycle transitions and access to the owned prepared batches
        /// </summary>
        private readonly object _lifecycleLock;

        /// <summary>
        /// Records the hash extraction dispatches
        /// </summary>
        private readonly VulkanHashExtractor _extractor;

        /// <summary>
        /// Records the offset measurement dispatches
        /// </summary>
        private readonly VulkanHashMatcher _matcher;

        /// <summary>
        /// Cancels work in flight when the pipeline is disposed
        /// </summary>
        private readonly CancellationTokenSource _disposeCancellation;

        /// <summary>
        /// Signals that no execution owns pipeline resources
        /// </summary>
        private readonly ManualResetEventSlim _idle;

        /// <summary>
        /// Prepared batches whose resident tracks are still owned by the pipeline
        /// </summary>
        private readonly HashSet<VulkanHashPreparedBatch> _preparedBatches;

        /// <summary>
        /// Number of executions currently owning pipeline resources
        /// </summary>
        private int _activeExecutions;

        /// <summary>
        /// Indicates whether this pipeline has completed disposal
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a pipeline bound to the specified persistent context and runtime
        /// </summary>
        /// <param name="context">Context that owns the pipeline lifecycle</param>
        /// <param name="runtime">Runtime shared by the context pipelines</param>
        internal VulkanHashPipeline(VulkanVisionContext context, VulkanRuntimeContext runtime)
        {
            this._context = context;
            this._runtime = runtime;
            this._lifecycleLock = new object();
            this._extractor = new VulkanHashExtractor(runtime);
            this._matcher = new VulkanHashMatcher(runtime);
            this._disposeCancellation = new CancellationTokenSource();
            this._idle = new ManualResetEventSlim(true);
            this._preparedBatches = new HashSet<VulkanHashPreparedBatch>();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Hashes a sequence of tightly packed grayscale analysis squares
        /// </summary>
        /// <param name="frames">Analysis squares, each one holding one byte per pixel in row order</param>
        /// <param name="cancellationToken">Token used to request cooperative cancellation</param>
        /// <returns>The hash of every analysis square, in the order they were provided</returns>
        public VulkanFrameHash[] Extract(ReadOnlySpan<byte> frames, CancellationToken cancellationToken = default)
        {
            return this.ExtractSignals(frames, cancellationToken).Hashes;
        }

        /// <summary>
        /// Extracts hashes, luminance and compact thumbnails from tightly packed grayscale analysis squares
        /// </summary>
        /// <param name="frames">Analysis squares, each one holding one byte per pixel in row order</param>
        /// <param name="cancellationToken">Token used to request cooperative cancellation</param>
        /// <returns>All ordered frame signals produced by the device</returns>
        public VulkanFrameSignalBatch ExtractSignals(ReadOnlySpan<byte> frames, CancellationToken cancellationToken = default)
        {
            this.ThrowIfDisposed();
            if (frames.Length % FrameBytes != 0)
                throw new ArgumentException("The frame sequence does not contain a whole number of analysis squares.", nameof(frames));
            cancellationToken.ThrowIfCancellationRequested();
            this.EnterExecution();
            try
            {
                using (CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._disposeCancellation.Token))
                {
                    VulkanVisionDiagnostics diagnostics = this.CreateDiagnostics();
                    VulkanFrameSignalBatch result = this._extractor.Extract(frames, frames.Length / FrameBytes, diagnostics, linkedCancellation.Token);
                    this._runtime.DrainValidationMessages(diagnostics);
                    return result;
                }
            }
            finally
            {
                this.ExitExecution();
            }
        }

        /// <summary>
        /// Keeps both tracks resident in device memory so that offsets can be measured against them
        /// </summary>
        /// <param name="source">Frame hashes and timestamps of the source track</param>
        /// <param name="language">Frame hashes and timestamps of the dubbed track</param>
        /// <param name="cancellationToken">Token used to request cooperative cancellation</param>
        /// <returns>A batch owning the resident tracks until it is disposed</returns>
        public VulkanHashPreparedBatch Prepare(VulkanHashTrack source, VulkanHashTrack language, CancellationToken cancellationToken = default)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (language == null)
                throw new ArgumentNullException(nameof(language));
            this.ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            this.EnterExecution();
            VulkanHashCollection residentSource = null;
            VulkanHashCollection residentLanguage = null;
            VulkanVisionDiagnostics diagnostics = this.CreateDiagnostics();
            Stopwatch endToEnd = Stopwatch.StartNew();
            try
            {
                using (CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._disposeCancellation.Token))
                {
                    residentSource = new VulkanHashCollection(this._runtime, source, diagnostics, linkedCancellation.Token);
                    residentLanguage = new VulkanHashCollection(this._runtime, language, diagnostics, linkedCancellation.Token);
                }
                diagnostics.DeclaredFrameCount = source.Count + language.Count;
                this._runtime.DrainValidationMessages(diagnostics);
                VulkanHashPreparedBatch result = new VulkanHashPreparedBatch(this, residentSource, residentLanguage, diagnostics, endToEnd);
                this.RegisterPrepared(result);
                return result;
            }
            catch
            {
                residentLanguage?.Dispose();
                residentSource?.Dispose();
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
        /// Measures a batch of scans against the tracks kept resident by a prepared batch
        /// </summary>
        /// <param name="prepared">Batch owning the resident tracks</param>
        /// <param name="scans">Scans forming the batch, in request order</param>
        /// <param name="cancellationToken">Token used to request cooperative cancellation</param>
        /// <returns>The outcome of every requested scan and the diagnostics of the execution</returns>
        internal VulkanHashBatchResult ExecutePrepared(VulkanHashPreparedBatch prepared, IReadOnlyList<VulkanHashScan> scans, CancellationToken cancellationToken)
        {
            if (scans == null)
                throw new ArgumentNullException(nameof(scans));
            this.ThrowIfDisposed();
            prepared.ThrowIfDisposed();
            ValidateScans(scans, prepared.Source.Count);
            cancellationToken.ThrowIfCancellationRequested();
            this.EnterExecution();
            try
            {
                VulkanVisionDiagnostics diagnostics = prepared.Diagnostics;
                using (CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, this._disposeCancellation.Token))
                using (VulkanHashWorkspace workspace = new VulkanHashWorkspace(this._runtime, scans))
                {
                    long matchStart = Stopwatch.GetTimestamp();
                    uint[] counts = this._matcher.Match(prepared.Source, prepared.Language, workspace, diagnostics, linkedCancellation.Token);
                    diagnostics.MatchingTicks += Stopwatch.GetTimestamp() - matchStart;
                    this._runtime.DrainValidationMessages(diagnostics);
                    diagnostics.EndToEndTicks = prepared.EndToEnd.ElapsedTicks;
                    return new VulkanHashBatchResult(BuildResults(scans, workspace.CandidateOffsets, counts), diagnostics);
                }
            }
            finally
            {
                this.ExitExecution();
            }
        }

        /// <summary>
        /// Releases the resident tracks owned by a prepared batch
        /// </summary>
        /// <param name="prepared">Batch whose resident tracks are no longer needed</param>
        internal void ReleasePrepared(VulkanHashPreparedBatch prepared)
        {
            lock (this._lifecycleLock)
            {
                if (!this._preparedBatches.Remove(prepared))
                    return;
            }
            prepared.Language.Dispose();
            prepared.Source.Dispose();
            this._runtime.DrainValidationMessages(prepared.Diagnostics);
            this.ExitExecution();
        }

        /// <summary>
        /// Disposes the pipeline as part of its owning context teardown
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
            List<VulkanHashPreparedBatch> preparedBatches = null;
            bool ownsDispose = false;
            lock (this._lifecycleLock)
            {
                if (!this._disposed)
                {
                    this._disposed = true;
                    this._disposeCancellation.Cancel();
                    preparedBatches = new List<VulkanHashPreparedBatch>(this._preparedBatches);
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
        /// Creates the diagnostics of one operation, already carrying the runtime and toolchain identity
        /// </summary>
        /// <returns>The diagnostics of the operation</returns>
        private VulkanVisionDiagnostics CreateDiagnostics()
        {
            VulkanVisionDiagnostics diagnostics = new VulkanVisionDiagnostics();
            diagnostics.ProbeTicks = this._runtime.InitializationTicks;
            foreach (KeyValuePair<string, string> metadata in this._runtime.ShaderLoader.BuildMetadata)
                diagnostics.Toolchain.Add(metadata.Key, metadata.Value);
            return diagnostics;
        }

        /// <summary>
        /// Registers a resident batch in the pipeline lifecycle
        /// </summary>
        /// <param name="prepared">Batch whose resident tracks are now owned by the pipeline</param>
        private void RegisterPrepared(VulkanHashPreparedBatch prepared)
        {
            lock (this._lifecycleLock)
            {
                this.ThrowIfDisposed();
                this._preparedBatches.Add(prepared);
            }
        }

        /// <summary>
        /// Validates the frame ranges, candidate grids and acceptance limits of a batch of scans
        /// </summary>
        /// <param name="scans">Scans forming the batch</param>
        /// <param name="sourceCount">Number of frames in the resident source track</param>
        private static void ValidateScans(IReadOnlyList<VulkanHashScan> scans, int sourceCount)
        {
            for (int i = 0; i < scans.Count; i++)
            {
                VulkanHashScan scan = scans[i];
                if (scan.IndexCount < 0 || scan.CandidateCount < 1)
                    throw new ArgumentOutOfRangeException(nameof(scans), "A scan must declare a non-negative frame count and at least one candidate offset.");
                if (!double.IsFinite(scan.FirstOffsetMs) || !double.IsFinite(scan.StepMs))
                    throw new ArgumentOutOfRangeException(nameof(scans), "A scan must declare a finite candidate grid.");
                if (scan.Stride < 1)
                    throw new ArgumentOutOfRangeException(nameof(scans), "A scan must declare a positive frame stride.");
                if (scan.ToleranceRadius < 0 || scan.Threshold < 0 || scan.Threshold > 128)
                    throw new ArgumentOutOfRangeException(nameof(scans), "A scan must declare a non-negative tolerance radius and a threshold between zero and one hundred twenty-eight.");
                if (scan.FirstIndex < 0 || (scan.IndexCount > 0 && (long)scan.FirstIndex + (long)(scan.IndexCount - 1) * scan.Stride >= sourceCount))
                    throw new ArgumentOutOfRangeException(nameof(scans), "A scan measures source frames outside the resident track.");
            }
        }

        /// <summary>
        /// Assembles the per-scan outcome from the flattened explained frame counts
        /// </summary>
        /// <param name="scans">Scans forming the batch, in request order</param>
        /// <param name="candidateOffsets">Base result index reserved for each scan</param>
        /// <param name="counts">Explained frame count of every candidate offset, in batch order</param>
        /// <returns>The outcome of every requested scan, in request order</returns>
        private static List<VulkanHashScanResult> BuildResults(IReadOnlyList<VulkanHashScan> scans, int[] candidateOffsets, uint[] counts)
        {
            List<VulkanHashScanResult> results = new List<VulkanHashScanResult>(scans.Count);
            for (int i = 0; i < scans.Count; i++)
            {
                VulkanHashScan scan = scans[i];
                int[] explained = new int[scan.CandidateCount];
                int best = 0;
                for (int candidate = 0; candidate < scan.CandidateCount; candidate++)
                {
                    explained[candidate] = (int)counts[candidateOffsets[i] + candidate];
                    if (explained[candidate] > explained[best])
                        best = candidate;
                }
                results.Add(new VulkanHashScanResult(explained, best, scan.FirstOffsetMs + best * scan.StepMs, scan.IndexCount));
            }
            return results;
        }

        /// <summary>
        /// Rejects use of the instance after disposal
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (this._disposed)
                throw new ObjectDisposedException(nameof(VulkanHashPipeline));
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
    }

    /// <summary>
    /// Owns the two tracks kept resident in device memory for repeated offset measurements
    /// </summary>
    public sealed class VulkanHashPreparedBatch : IDisposable
    {
        #region Class Fields

        /// <summary>
        /// Pipeline that owns the resident tracks of this batch
        /// </summary>
        private readonly VulkanHashPipeline _owner;

        /// <summary>
        /// Serializes the executions recorded against this batch
        /// </summary>
        private readonly object _executionLock;

        /// <summary>
        /// Indicates whether this batch has completed disposal
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a batch owning the two resident tracks
        /// </summary>
        /// <param name="owner">Pipeline that owns the resident tracks</param>
        /// <param name="source">Resident hashes and timestamps of the source track</param>
        /// <param name="language">Resident hashes and timestamps of the dubbed track</param>
        /// <param name="diagnostics">Diagnostics collected while preparing the batch</param>
        /// <param name="endToEnd">Stopwatch started when the preparation began</param>
        internal VulkanHashPreparedBatch(VulkanHashPipeline owner, VulkanHashCollection source, VulkanHashCollection language, VulkanVisionDiagnostics diagnostics, Stopwatch endToEnd)
        {
            this._owner = owner;
            this._executionLock = new object();
            this.Source = source;
            this.Language = language;
            this.Diagnostics = diagnostics;
            this.EndToEnd = endToEnd;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Measures a batch of scans against the resident tracks
        /// </summary>
        /// <param name="scans">Scans forming the batch, in request order</param>
        /// <param name="cancellationToken">Token used to request cooperative cancellation</param>
        /// <returns>The outcome of every requested scan and the diagnostics of the execution</returns>
        public VulkanHashBatchResult Execute(IReadOnlyList<VulkanHashScan> scans, CancellationToken cancellationToken = default)
        {
            lock (this._executionLock)
                return this._owner.ExecutePrepared(this, scans, cancellationToken);
        }

        /// <summary>
        /// Releases the resident tracks owned by this batch
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;
            this._disposed = true;
            this._owner.ReleasePrepared(this);
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// Releases the batch as part of its owning pipeline teardown
        /// </summary>
        internal void DisposeFromOwner()
        {
            this.Dispose();
        }

        /// <summary>
        /// Rejects use of the instance after disposal
        /// </summary>
        internal void ThrowIfDisposed()
        {
            if (this._disposed)
                throw new ObjectDisposedException(nameof(VulkanHashPreparedBatch));
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the resident source track
        /// </summary>
        internal VulkanHashCollection Source { get; }

        /// <summary>
        /// Gets the resident dubbed track
        /// </summary>
        internal VulkanHashCollection Language { get; }

        /// <summary>
        /// Gets the diagnostics shared by the preparation and the executions of this batch
        /// </summary>
        internal VulkanVisionDiagnostics Diagnostics { get; }

        /// <summary>
        /// Gets the stopwatch started when the preparation began
        /// </summary>
        internal Stopwatch EndToEnd { get; }

        #endregion
    }
}
