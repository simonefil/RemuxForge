using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Runtime;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Vision.Hash
{
    /// <summary>
    /// Owns the temporary buffers and host-side metadata describing one batch of hash scans
    /// </summary>
    internal sealed class VulkanHashWorkspace : IDisposable
    {
        #region Class Fields

        /// <summary>
        /// Owns every buffer lease acquired by this workspace
        /// </summary>
        private readonly List<VulkanBufferLease> _leases;

        /// <summary>
        /// Tracks whether the owned leases have already been returned to the resource pool
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Builds the scan metadata, the candidate grid and the buffers holding the batch results
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool owns the rented Vulkan buffers</param>
        /// <param name="scans">Scans forming the batch, in request order</param>
        public VulkanHashWorkspace(VulkanRuntimeContext runtime, IReadOnlyList<VulkanHashScan> scans)
        {
            this._leases = new List<VulkanBufferLease>();
            ScanRecord[] scanRecords = new ScanRecord[scans.Count];
            this.CandidateOffsets = new int[scans.Count];
            int resultCount = 0;
            int maximumIndexCount = 0;
            for (int i = 0; i < scans.Count; i++)
            {
                VulkanHashScan scan = scans[i];
                this.CandidateOffsets[i] = resultCount;
                scanRecords[i] = new ScanRecord
                {
                    FirstIndex = (uint)scan.FirstIndex,
                    Stride = (uint)scan.Stride,
                    IndexCount = (uint)scan.IndexCount,
                    CandidateOffset = (uint)resultCount,
                    Radius = (uint)scan.ToleranceRadius,
                    Threshold = (uint)scan.Threshold
                };
                resultCount = checked(resultCount + scan.CandidateCount);
                maximumIndexCount = Math.Max(maximumIndexCount, scan.IndexCount);
            }

            JobRecord[] jobRecords = new JobRecord[resultCount];
            double[] candidates = new double[Math.Max(1, resultCount)];
            int job = 0;
            for (int i = 0; i < scans.Count; i++)
            {
                VulkanHashScan scan = scans[i];
                for (int candidate = 0; candidate < scan.CandidateCount; candidate++)
                {
                    jobRecords[job] = new JobRecord { ScanIndex = (uint)i, CandidateIndex = (uint)candidate };
                    candidates[job] = scan.FirstOffsetMs + candidate * scan.StepMs;
                    job++;
                }
            }

            this.CandidateCount = resultCount;
            this.MaximumIndexCount = maximumIndexCount;
            this.Scans = this.Rent(runtime, checked((ulong)Math.Max(1, scans.Count) * (ulong)Marshal.SizeOf<ScanRecord>()), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            this.Jobs = this.Rent(runtime, checked((ulong)Math.Max(1, resultCount) * (ulong)Marshal.SizeOf<JobRecord>()), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            this.Candidates = this.Rent(runtime, checked((ulong)candidates.Length * sizeof(double)), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            this.Results = this.Rent(runtime, checked((ulong)Math.Max(1, resultCount) * sizeof(uint)), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.DeviceLocal);
            this.Readback = this.Rent(runtime, checked((ulong)Math.Max(1, resultCount) * sizeof(uint)), VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            this.Scans.Write<ScanRecord>(scanRecords);
            if (resultCount > 0)
                this.Jobs.Write<JobRecord>(jobRecords);
            this.Candidates.Write<double>(candidates);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Releases all Vulkan buffer leases owned by this workspace
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;
            this._disposed = true;
            for (int i = this._leases.Count - 1; i >= 0; i--)
                this._leases[i].Dispose();
            this._leases.Clear();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Rents a buffer and records its lease as workspace-owned state
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool supplies the buffer</param>
        /// <param name="size">Minimum buffer size in bytes</param>
        /// <param name="usage">Vulkan usage flags for the buffer</param>
        /// <param name="properties">Required Vulkan memory properties</param>
        /// <returns>The rented buffer</returns>
        private VulkanBuffer Rent(VulkanRuntimeContext runtime, ulong size, VkBufferUsageFlags usage, VkMemoryPropertyFlags properties)
        {
            VulkanBufferLease lease = runtime.ResourcePool.Rent(size, usage, properties);
            this._leases.Add(lease);
            return lease.Buffer;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the base result index reserved for each scan, in request order
        /// </summary>
        public int[] CandidateOffsets { get; }

        /// <summary>
        /// Gets the total number of candidate offsets across the batch, one dispatched job and one result each
        /// </summary>
        public int CandidateCount { get; }

        /// <summary>
        /// Gets the largest measured source frame count in the batch
        /// </summary>
        public int MaximumIndexCount { get; }

        /// <summary>
        /// Gets the host-visible scan metadata consumed by the matching shader
        /// </summary>
        public VulkanBuffer Scans { get; }

        /// <summary>
        /// Gets the host-visible scan and candidate job records
        /// </summary>
        public VulkanBuffer Jobs { get; }

        /// <summary>
        /// Gets the host-visible candidate offsets of every scan
        /// </summary>
        public VulkanBuffer Candidates { get; }

        /// <summary>
        /// Gets the device-local explained frame counts, one per candidate offset
        /// </summary>
        public VulkanBuffer Results { get; }

        /// <summary>
        /// Gets the host-visible buffer receiving the explained frame counts
        /// </summary>
        public VulkanBuffer Readback { get; }

        #endregion

        #region Nested Types

        /// <summary>
        /// Describes the measured source frames, the candidate range and the acceptance limits of one scan
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct ScanRecord
        {
            /// <summary>
            /// Index of the first measured source frame
            /// </summary>
            public uint FirstIndex;

            /// <summary>
            /// Distance in frames between two consecutive measured source frames
            /// </summary>
            public uint Stride;

            /// <summary>
            /// Number of measured source frames
            /// </summary>
            public uint IndexCount;

            /// <summary>
            /// Base candidate and result index reserved for the scan
            /// </summary>
            public uint CandidateOffset;

            /// <summary>
            /// Language frames explored on each side of the located timestamp
            /// </summary>
            public uint Radius;

            /// <summary>
            /// Highest Hamming distance that still counts as an explained frame
            /// </summary>
            public uint Threshold;
        }

        /// <summary>
        /// Associates one workgroup column with the scan and the candidate offset it measures
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct JobRecord
        {
            /// <summary>
            /// Index of the scan in the batch
            /// </summary>
            public uint ScanIndex;

            /// <summary>
            /// Index of the candidate offset inside the scan
            /// </summary>
            public uint CandidateIndex;
        }

        #endregion
    }
}
