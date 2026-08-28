using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Runtime;
using RemuxForge.Vulkan.Vision.Sift;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Vision.Matching
{
    /// <summary>
    /// Owns the temporary buffers and host-side metadata used by resident reciprocal matching for one pair tile
    /// </summary>
    internal sealed class VulkanMatchWorkspace : IDisposable
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
        /// Creates the metadata and temporary buffers required to match a selected range of frame pairs
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool owns the rented Vulkan buffers</param>
        /// <param name="first">Feature collection supplying descriptors and frame capacity metadata for the first side of each pair</param>
        /// <param name="second">Feature collection supplying descriptors and frame capacity metadata for the second side of each pair</param>
        /// <param name="pairs">Source frame-pair list; the selected range is copied into <see cref="Pairs"/></param>
        /// <param name="start">Zero-based index of the first pair to include</param>
        /// <param name="count">Number of consecutive pairs to include</param>
        public VulkanMatchWorkspace(VulkanRuntimeContext runtime, VulkanSiftFeatureCollection first, VulkanSiftFeatureCollection second, IReadOnlyList<VulkanFramePair> pairs, int start, int count)
        {
            this._leases = new List<VulkanBufferLease>();
            this.Pairs = new List<VulkanFramePair>(count);
            this.MetadataRecords = new PairMatchRecord[count];
            int nearestCount = 0;
            int reciprocalCapacity = 0;
            int forwardJobCapacity = 0;
            int reverseJobCapacity = 0;
            int maximumFirstCapacity = 0;
            for (int i = 0; i < count; i++)
            {
                VulkanFramePair pair = pairs[start + i];
                this.Pairs.Add(pair);
                VulkanSiftFrameFeatures firstFrame = first.Frames[pair.FirstFrameIndex];
                VulkanSiftFrameFeatures secondFrame = second.Frames[pair.SecondFrameIndex];
                PairMatchRecord record = new PairMatchRecord
                {
                    FirstFrame = (uint)pair.FirstFrameIndex,
                    SecondFrame = (uint)pair.SecondFrameIndex,
                    FirstOffset = (uint)firstFrame.CapacityOffset,
                    SecondOffset = (uint)secondFrame.CapacityOffset,
                    ForwardOffset = (uint)nearestCount,
                    ReverseOffset = checked((uint)(nearestCount + firstFrame.Capacity)),
                    FlagOffset = (uint)reciprocalCapacity,
                    CandidateOffset = (uint)reciprocalCapacity,
                    OutputOffset = (uint)reciprocalCapacity,
                    FirstCapacity = (uint)firstFrame.Capacity,
                    SecondCapacity = (uint)secondFrame.Capacity
                };
                this.MetadataRecords[i] = record;
                nearestCount = checked(nearestCount + firstFrame.Capacity + secondFrame.Capacity);
                reciprocalCapacity = checked(reciprocalCapacity + firstFrame.Capacity);
                forwardJobCapacity = checked(forwardJobCapacity + RoundUp(firstFrame.Capacity, 16));
                reverseJobCapacity = checked(reverseJobCapacity + RoundUp(secondFrame.Capacity, 16));
                maximumFirstCapacity = Math.Max(maximumFirstCapacity, firstFrame.Capacity);
            }
            this.MaximumFirstCapacity = maximumFirstCapacity;
            this.ReciprocalCapacity = reciprocalCapacity;
            this.ForwardJobCapacity = forwardJobCapacity;
            this.Metadata = this.Rent(runtime, checked((ulong)Math.Max(1, count) * (ulong)Marshal.SizeOf<PairMatchRecord>()), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            this.Nearest = this.Rent(runtime, checked((ulong)Math.Max(1, nearestCount) * 16UL), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal);
            this.Flags = this.RentUInt(runtime, reciprocalCapacity, VkBufferUsageFlags.StorageBuffer);
            this.Prefix = this.RentUInt(runtime, reciprocalCapacity, VkBufferUsageFlags.StorageBuffer);
            this.ScanScratch = this.RentUInt(runtime, ResolveScanScratchElements(reciprocalCapacity), VkBufferUsageFlags.StorageBuffer);
            this.Candidates = this.Rent(runtime, checked((ulong)Math.Max(1, reciprocalCapacity) * 16UL), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal);
            this.ReciprocalMatches = this.Rent(runtime, checked((ulong)Math.Max(1, reciprocalCapacity) * 16UL), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal);
            this.Counts = this.RentUInt(runtime, checked(count * 2), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferSrc);
            this.Jobs = this.Rent(runtime, checked((ulong)Math.Max(1, checked(forwardJobCapacity + reverseJobCapacity)) * 8UL), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal);
            this.Control = this.Rent(runtime, 8UL * sizeof(uint), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.IndirectBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            this.Control.Write<uint>(new uint[8]);
            this.Metadata.Write<PairMatchRecord>(this.MetadataRecords);
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
        /// Rents a device-local buffer sized for the requested number of unsigned integers
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool owns the buffer</param>
        /// <param name="count">Number of unsigned integer elements required</param>
        /// <param name="usage">Vulkan usage flags for the buffer</param>
        /// <returns>The rented buffer view</returns>
        private VulkanBuffer RentUInt(VulkanRuntimeContext runtime, int count, VkBufferUsageFlags usage)
        {
            return this.Rent(runtime, checked((ulong)Math.Max(1, count) * sizeof(uint)), usage, VkMemoryPropertyFlags.DeviceLocal);
        }

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

        /// <summary>
        /// Calculates the unsigned integer scratch capacity required by the hierarchical exclusive prefix scan
        /// </summary>
        /// <param name="elementCount">Number of input elements in the scan</param>
        /// <returns>Total scratch elements, including storage for every higher-level block sum</returns>
        private static int ResolveScanScratchElements(int elementCount)
        {
            int total = 512;
            int blocks = (elementCount + 255) / 256;
            while (blocks > 1)
            {
                total = checked(total + blocks * 2);
                blocks = (blocks + 255) / 256;
            }
            return total;
        }

        /// <summary>
        /// Rounds a non-negative element count up to the next multiple of the requested alignment
        /// </summary>
        /// <param name="value">Element count to round</param>
        /// <param name="alignment">Positive alignment in elements</param>
        /// <returns>The aligned element count</returns>
        private static int RoundUp(int value, int alignment)
        {
            return checked((value + alignment - 1) / alignment * alignment);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the selected frame pairs in their original order
        /// </summary>
        public List<VulkanFramePair> Pairs { get; }

        /// <summary>
        /// Gets the host-side records uploaded to <see cref="Metadata"/>, one record per selected pair
        /// </summary>
        public PairMatchRecord[] MetadataRecords { get; }

        /// <summary>
        /// Gets the largest first-frame feature capacity in the selected tile
        /// </summary>
        public int MaximumFirstCapacity { get; }

        /// <summary>
        /// Gets the total first-frame capacity reserved for reciprocal flags, prefix values and candidate records
        /// </summary>
        public int ReciprocalCapacity { get; }

        /// <summary>
        /// Gets the total forward query-job capacity after sixteen-query alignment
        /// </summary>
        public int ForwardJobCapacity { get; }

        /// <summary>
        /// Gets the host-visible and host-coherent metadata buffer consumed by the job and match pipelines
        /// </summary>
        public VulkanBuffer Metadata { get; }

        /// <summary>
        /// Gets the device-local nearest and second-nearest result records for forward and reverse queries
        /// </summary>
        public VulkanBuffer Nearest { get; }

        /// <summary>
        /// Gets the device-local reciprocal flag buffer indexed by first-frame descriptor capacity
        /// </summary>
        public VulkanBuffer Flags { get; }

        /// <summary>
        /// Gets the device-local exclusive-prefix buffer used to compact reciprocal candidates
        /// </summary>
        public VulkanBuffer Prefix { get; }

        /// <summary>
        /// Gets the device-local scratch buffer containing block sums for the hierarchical prefix scan
        /// </summary>
        public VulkanBuffer ScanScratch { get; }

        /// <summary>
        /// Gets the device-local candidate records produced before reciprocal-match compaction
        /// </summary>
        public VulkanBuffer Candidates { get; }

        /// <summary>
        /// Gets the device-local compacted reciprocal-match records consumed by geometry processing
        /// </summary>
        public VulkanBuffer ReciprocalMatches { get; }

        /// <summary>
        /// Gets the transfer-source buffer containing the two match counts produced for each pair
        /// </summary>
        public VulkanBuffer Counts { get; }

        /// <summary>
        /// Gets the device-local forward and reverse query-job records
        /// </summary>
        public VulkanBuffer Jobs { get; }

        /// <summary>
        /// Gets the host-visible indirect-dispatch control buffer initialized for the job-building pipeline
        /// </summary>
        public VulkanBuffer Control { get; }


        #endregion

        #region Nested Types

        /// <summary>
        /// Describes the descriptor ranges, work ranges and output ranges for one pair
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct PairMatchRecord
        {
            /// <summary>
            /// Index of the first frame in the first feature collection
            /// </summary>
            public uint FirstFrame;

            /// <summary>
            /// Index of the second frame in the second feature collection
            /// </summary>
            public uint SecondFrame;

            /// <summary>
            /// Base descriptor-record index for the first frame
            /// </summary>
            public uint FirstOffset;

            /// <summary>
            /// Base descriptor-record index for the second frame
            /// </summary>
            public uint SecondOffset;

            /// <summary>
            /// Base nearest-record index for forward queries
            /// </summary>
            public uint ForwardOffset;

            /// <summary>
            /// Base nearest-record index for reverse queries
            /// </summary>
            public uint ReverseOffset;

            /// <summary>
            /// Base unsigned-integer index for reciprocal flags and prefix values
            /// </summary>
            public uint FlagOffset;

            /// <summary>
            /// Base reciprocal-record index for un-compacted candidates
            /// </summary>
            public uint CandidateOffset;

            /// <summary>
            /// Base reciprocal-record index for compacted output
            /// </summary>
            public uint OutputOffset;

            /// <summary>
            /// Maximum descriptor capacity reserved for the first frame
            /// </summary>
            public uint FirstCapacity;

            /// <summary>
            /// Maximum descriptor capacity reserved for the second frame
            /// </summary>
            public uint SecondCapacity;
        }

        #endregion
    }
}
