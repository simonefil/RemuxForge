using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Runtime;
using RemuxForge.Vulkan.Scheduling;
using System;
using System.Collections.Generic;
using System.Threading;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Vision.Hash
{
    /// <summary>
    /// Keeps the frame hashes and timestamps of one track resident in device memory
    /// </summary>
    internal sealed unsafe class VulkanHashCollection : IDisposable
    {
        #region Class Fields

        /// <summary>
        /// Owns every buffer lease acquired by this collection
        /// </summary>
        private readonly List<VulkanBufferLease> _leases;

        /// <summary>
        /// Tracks whether the owned leases have already been returned to the resource pool
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Uploads one track to device-local storage
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool owns the rented Vulkan buffers</param>
        /// <param name="track">Frame hashes and timestamps to keep resident</param>
        /// <param name="diagnostics">Diagnostics updated with the upload cost</param>
        /// <param name="cancellationToken">Token used while waiting for the upload to complete</param>
        public VulkanHashCollection(VulkanRuntimeContext runtime, VulkanHashTrack track, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            this._leases = new List<VulkanBufferLease>();
            this.Count = track.Count;
            try
            {
                HashRecord[] records = new HashRecord[track.Count];
                double[] times = new double[track.Count];
                for (int i = 0; i < track.Count; i++)
                {
                    VulkanFrameHash hash = track.Hashes[i];
                    records[i] = new HashRecord
                    {
                        Word0 = (uint)(hash.Horizontal >> 32),
                        Word1 = (uint)hash.Horizontal,
                        Word2 = (uint)(hash.Vertical >> 32),
                        Word3 = (uint)hash.Vertical
                    };
                    times[i] = track.TimestampsMs[i];
                }
                ulong hashBytes = checked((ulong)track.Count * (ulong)VulkanVisionAbi.HASH_RECORD_SIZE);
                ulong timeBytes = checked((ulong)track.Count * sizeof(double));
                this.Hashes = this.Rent(runtime, hashBytes, VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst);
                this.Times = this.Rent(runtime, timeBytes, VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst);
                using (VulkanBufferLease staging = runtime.ResourcePool.Rent(checked(hashBytes + timeBytes), VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent))
                {
                    long uploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    staging.Buffer.Write<HashRecord>(records);
                    staging.Buffer.Write<double>(times, hashBytes);
                    VulkanBuffer source = staging.Buffer;
                    using (VulkanSubmission submission = runtime.Scheduler.Execute(commandBuffer =>
                    {
                        int uploadTimestamp = runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Upload);
                        VkBufferCopy hashCopy = new VkBufferCopy { srcOffset = source.BindingOffset, dstOffset = this.Hashes.BindingOffset, size = hashBytes };
                        runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, source.Buffer, this.Hashes.Buffer, 1, &hashCopy);
                        VkBufferCopy timeCopy = new VkBufferCopy { srcOffset = source.BindingOffset + hashBytes, dstOffset = this.Times.BindingOffset, size = timeBytes };
                        runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, source.Buffer, this.Times.Buffer, 1, &timeCopy);
                        runtime.Scheduler.EndGpuPhase(commandBuffer, uploadTimestamp);
                    }, diagnostics, VulkanGpuPhase.None, cancellationToken))
                        submission.Wait(diagnostics, cancellationToken);
                    diagnostics.UploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - uploadStart;
                    diagnostics.UploadedBytes += hashBytes + timeBytes;
                }
            }
            catch
            {
                this.Dispose();
                throw;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Releases all Vulkan buffer leases owned by this collection
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
        /// Rents a device-local buffer and records its lease as collection-owned state
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool supplies the buffer</param>
        /// <param name="size">Minimum buffer size in bytes</param>
        /// <param name="usage">Vulkan usage flags for the buffer</param>
        /// <returns>The rented buffer</returns>
        private VulkanBuffer Rent(VulkanRuntimeContext runtime, ulong size, VkBufferUsageFlags usage)
        {
            VulkanBufferLease lease = runtime.ResourcePool.Rent(Math.Max(1UL, size), usage, VkMemoryPropertyFlags.DeviceLocal);
            this._leases.Add(lease);
            return lease.Buffer;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the number of frames in the track
        /// </summary>
        public int Count { get; }

        /// <summary>
        /// Gets the device-local hash records of the track
        /// </summary>
        public VulkanBuffer Hashes { get; }

        /// <summary>
        /// Gets the device-local timestamps of the track
        /// </summary>
        public VulkanBuffer Times { get; }

        #endregion
    }
}
