using System;
using System.Collections.Generic;

namespace RemuxForge.Vulkan
{
    /// <summary>
    /// Collects host timing, GPU, memory, pipeline and quality measurements for one vision batch
    /// </summary>
    public sealed class VulkanVisionDiagnostics
    {
        /// <summary>Initializes empty counters and owned diagnostic collections</summary>
        public VulkanVisionDiagnostics()
        {
            this.ShaderHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            this.Toolchain = new Dictionary<string, string>(StringComparer.Ordinal);
            this.AlgorithmConfiguration = new Dictionary<string, string>(StringComparer.Ordinal);
            this.Rejections = new Dictionary<VulkanSiftRejectReason, int>();
            this.ValidationMessages = new List<string>();
        }

        /// <summary>Host ticks spent by the device probe and runtime initialization</summary>
        public long ProbeTicks { get; internal set; }
        /// <summary>Host ticks spent preparing and uploading buffers</summary>
        public long UploadTicks { get; internal set; }
        /// <summary>Host ticks spent in input normalization</summary>
        public long NormalizeTicks { get; internal set; }
        /// <summary>Host ticks spent building the Gaussian and difference-of-Gaussians pyramid</summary>
        public long GaussianPyramidTicks { get; internal set; }
        /// <summary>Host ticks spent detecting and refining extrema</summary>
        public long ExtremaTicks { get; internal set; }
        /// <summary>Host ticks spent assigning orientations and building descriptors</summary>
        public long DescriptorTicks { get; internal set; }
        /// <summary>Host ticks spent performing reciprocal matching</summary>
        public long MatchingTicks { get; internal set; }
        /// <summary>Host ticks spent in geometric RANSAC validation</summary>
        public long RansacTicks { get; internal set; }
        /// <summary>Host ticks spent reading results back from the GPU</summary>
        public long ReadbackTicks { get; internal set; }
        /// <summary>Host ticks spent waiting for fences</summary>
        public long HostWaitTicks { get; internal set; }
        /// <summary>Total host ticks elapsed for the batch</summary>
        public long EndToEndTicks { get; internal set; }
        /// <summary>Number of submissions sent to the compute queue</summary>
        public int SubmitCount { get; internal set; }
        /// <summary>Number of compute dispatches recorded</summary>
        public int DispatchCount { get; internal set; }
        /// <summary>Number of explicit host waits</summary>
        public int WaitCount { get; internal set; }
        /// <summary>Number of matching tiles completed</summary>
        public int CompletedTileCount { get; internal set; }
        /// <summary>Number of bytes transferred from the caller to the device</summary>
        public ulong UploadedBytes { get; internal set; }
        /// <summary>Number of bytes read back from the device</summary>
        public ulong ReadbackBytes { get; internal set; }
        /// <summary>Device-local memory reported as in use when the measurement was taken, in bytes</summary>
        public ulong CurrentVramBytes { get; internal set; }
        /// <summary>Highest device-local memory allocation observed, in bytes</summary>
        public ulong PeakVramBytes { get; internal set; }
        /// <summary>Device-local memory retained by allocator caches, in bytes</summary>
        public ulong CachedVramBytes { get; internal set; }
        /// <summary>Difference between allocated and used device-local memory, in bytes</summary>
        public ulong WastedVramBytes { get; internal set; }
        /// <summary>Host-visible memory allocated by the allocator, in bytes</summary>
        public ulong HostVisibleAllocatedBytes { get; internal set; }
        /// <summary>Host-visible memory currently used by the allocator, in bytes</summary>
        public ulong HostVisibleUsedBytes { get; internal set; }
        /// <summary>Number of memory blocks owned by the allocator</summary>
        public int AllocationCount { get; internal set; }
        /// <summary>Number of frames declared by the request</summary>
        public int DeclaredFrameCount { get; internal set; }
        /// <summary>Number of frames containing enough features for processing</summary>
        public int ActiveFrameCount { get; internal set; }
        /// <summary>Number of frames excluded because they lacked enough features</summary>
        public int FeaturelessFrameCount { get; internal set; }
        /// <summary>Number of candidate keypoints before refinement</summary>
        public long CandidateKeypointCount { get; internal set; }
        /// <summary>Number of keypoints that survived refinement</summary>
        public long RefinedKeypointCount { get; internal set; }
        /// <summary>Number of keypoints removed by the per-frame maximum</summary>
        public long TruncatedKeypointCount { get; internal set; }
        /// <summary>Number of materialized SIFT descriptors</summary>
        public long DescriptorCount { get; internal set; }
        /// <summary>Number of pairs declared by the request</summary>
        public int DeclaredPairCount { get; internal set; }
        /// <summary>Number of pairs for which a result was materialized</summary>
        public int ProcessedPairCount { get; internal set; }
        /// <summary>Number of pairs accepted by matching and RANSAC</summary>
        public int AcceptedPairCount { get; internal set; }
        /// <summary>Number of warning messages produced by the validation layers</summary>
        public int ValidationWarningCount { get; internal set; }
        /// <summary>Number of error messages produced by the validation layers</summary>
        public int ValidationErrorCount { get; internal set; }
        /// <summary>Aggregated GPU execution time, in nanoseconds</summary>
        public ulong GpuExecutionNanoseconds { get; internal set; }
        /// <summary>GPU time spent uploading data, in nanoseconds</summary>
        public ulong GpuUploadNanoseconds { get; internal set; }
        /// <summary>GPU time spent normalizing input, in nanoseconds</summary>
        public ulong GpuNormalizeNanoseconds { get; internal set; }
        /// <summary>GPU time spent building the Gaussian and difference-of-Gaussians pyramid, in nanoseconds</summary>
        public ulong GpuGaussianPyramidNanoseconds { get; internal set; }
        /// <summary>GPU time spent detecting extrema, in nanoseconds</summary>
        public ulong GpuExtremaNanoseconds { get; internal set; }
        /// <summary>GPU time spent assigning orientations, in nanoseconds</summary>
        public ulong GpuOrientationNanoseconds { get; internal set; }
        /// <summary>GPU time spent building descriptors, in nanoseconds</summary>
        public ulong GpuDescriptorNanoseconds { get; internal set; }
        /// <summary>GPU time spent performing reciprocal matching, in nanoseconds</summary>
        public ulong GpuMatchingNanoseconds { get; internal set; }
        /// <summary>GPU time spent in RANSAC, in nanoseconds</summary>
        public ulong GpuRansacNanoseconds { get; internal set; }
        /// <summary>GPU time spent reading results back, in nanoseconds</summary>
        public ulong GpuReadbackNanoseconds { get; internal set; }
        /// <summary>Number of timestamp queries consumed for timing</summary>
        public int TimestampQueryCount { get; internal set; }
        /// <summary>Number of timeline semaphore signals emitted</summary>
        public int TimelineSignalCount { get; internal set; }
        /// <summary>Number of pipelines retrieved from the runtime cache</summary>
        public int PipelineCacheHitCount { get; internal set; }
        /// <summary>Number of pipelines created because they were absent from the runtime cache</summary>
        public int PipelineCacheMissCount { get; internal set; }
        /// <summary>Maps each loaded shader name to its SHA-256 hash</summary>
        public Dictionary<string, string> ShaderHashes { get; }
        /// <summary>Maps embedded shader toolchain metadata names to their values</summary>
        public Dictionary<string, string> Toolchain { get; }
        /// <summary>Maps effective algorithm parameter names to culture-invariant serialized values</summary>
        public Dictionary<string, string> AlgorithmConfiguration { get; }
        /// <summary>Maps each pair outcome reason to the number of results carrying it</summary>
        public Dictionary<VulkanSiftRejectReason, int> Rejections { get; }
        /// <summary>Stores validation-layer messages drained during the batch</summary>
        public List<string> ValidationMessages { get; }
    }
}
