using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Runtime;
using System;
using System.Collections.Generic;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Vision.Sift
{
    /// <summary>
    /// Owns the shared buffers used by a packed SIFT batch
    /// </summary>
    internal sealed class VulkanSiftPackedWorkspace : IDisposable
    {
        #region Instance fields

        /// <summary>
        /// Tracks the leases acquired for every buffer owned by the workspace
        /// </summary>
        private readonly List<VulkanBufferLease> _leases;

        /// <summary>
        /// Indicates whether the workspace has released its owned leases
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Allocates and initializes the shared buffers for a packed SIFT batch
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool supplies the buffers</param>
        /// <param name="frames">Input frames whose pixel data is copied into the staging buffer</param>
        /// <param name="plan">Packed layout that defines frame offsets and aggregate buffer capacities</param>
        public VulkanSiftPackedWorkspace(VulkanRuntimeContext runtime, IReadOnlyList<VulkanImageFrame> frames, VulkanSiftPackedPlan plan)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            ArgumentNullException.ThrowIfNull(frames);
            ArgumentNullException.ThrowIfNull(plan);
            if (frames.Count != plan.Frames.Count)
                throw new ArgumentException("Packed frames and plans must have the same count.", nameof(frames));

            this._leases = new List<VulkanBufferLease>();
            this.InputStaging = this.Rent(runtime, plan.InputBytes, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            this.PackedInput = this.Rent(runtime, plan.InputBytes, VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal);
            this.InputFloat = this.RentFloat(runtime, plan.InputFloatElements, VkBufferUsageFlags.StorageBuffer);
            this.TemporaryFloat = this.RentFloat(runtime, plan.TemporaryFloatElements, VkBufferUsageFlags.StorageBuffer);
            this.Gaussian = this.RentFloat(runtime, plan.GaussianFloatElements, VkBufferUsageFlags.StorageBuffer);
            this.Gradients = this.RentFloat(runtime, plan.GradientFloatElements, VkBufferUsageFlags.StorageBuffer);
            this.Dog = this.RentFloat(runtime, plan.DogFloatElements, VkBufferUsageFlags.StorageBuffer);
            this.Flags = this.RentUInt(runtime, plan.FlagElements, VkBufferUsageFlags.StorageBuffer);
            this.Prefix = this.RentUInt(runtime, plan.FlagElements, VkBufferUsageFlags.StorageBuffer);
            this.ScanScratch = this.RentUInt(runtime, plan.ScanScratchElements, VkBufferUsageFlags.StorageBuffer);
            this.Candidates = this.RentRecords(runtime, plan.CandidateElements, VkBufferUsageFlags.StorageBuffer);
            this.Keypoints = this.RentRecords(runtime, plan.KeypointElements, VkBufferUsageFlags.StorageBuffer);
            this.SortedKeypoints = this.RentRecords(runtime, plan.KeypointElements, VkBufferUsageFlags.StorageBuffer);
            this.OrientedKeypoints = this.RentRecords(runtime, plan.OrientedKeypointElements, VkBufferUsageFlags.StorageBuffer);
            this.Descriptors = this.RentUInt(runtime, plan.DescriptorElements, VkBufferUsageFlags.StorageBuffer);
            this.Counters = this.RentUInt(runtime, plan.CounterElements, VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferSrc);
            this.IndirectCommands = this.RentUInt(runtime, plan.IndirectCommandElements, VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.IndirectBuffer);

            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                VulkanImageFrame frame = frames[frameIndex] ?? throw new ArgumentException("A frame cannot be null.", nameof(frames));
                VulkanSiftPackedFramePlan framePlan = plan.Frames[frameIndex];
                int inputLength = checked(frame.Stride * frame.Height);
                this.InputStaging.Write<byte>(frame.Pixels.Span.Slice(0, inputLength), framePlan.InputByteOffset);
            }
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Releases every buffer lease owned by the workspace
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;
            this._disposed = true;
            for (int leaseIndex = this._leases.Count - 1; leaseIndex >= 0; leaseIndex--)
                this._leases[leaseIndex].Dispose();
            this._leases.Clear();
        }

        #endregion

        #region Private methods

        /// <summary>
        /// Rents a device-local buffer sized for the requested number of floating-point elements
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool supplies the buffer</param>
        /// <param name="elements">Number of <see cref="System.Single"/> elements required</param>
        /// <param name="usage">Vulkan usage flags required by the buffer consumers</param>
        /// <returns>The pooled buffer reserved for the requested elements</returns>
        private VulkanBuffer RentFloat(VulkanRuntimeContext runtime, uint elements, VkBufferUsageFlags usage)
        {
            return this.Rent(runtime, checked((ulong)elements * sizeof(float)), usage, VkMemoryPropertyFlags.DeviceLocal);
        }

        /// <summary>
        /// Rents a device-local buffer sized for the requested number of unsigned integer elements
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool supplies the buffer</param>
        /// <param name="elements">Number of <see cref="System.UInt32"/> elements required</param>
        /// <param name="usage">Vulkan usage flags required by the buffer consumers</param>
        /// <returns>The pooled buffer reserved for the requested elements</returns>
        private VulkanBuffer RentUInt(VulkanRuntimeContext runtime, uint elements, VkBufferUsageFlags usage)
        {
            return this.Rent(runtime, checked((ulong)elements * sizeof(uint)), usage, VkMemoryPropertyFlags.DeviceLocal);
        }

        /// <summary>
        /// Rents storage for fixed-size packed records
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool supplies the buffer</param>
        /// <param name="elements">Number of records required</param>
        /// <param name="usage">Vulkan usage flags required by the buffer consumers</param>
        /// <returns>The pooled buffer reserved for the requested records</returns>
        private VulkanBuffer RentRecords(VulkanRuntimeContext runtime, uint elements, VkBufferUsageFlags usage)
        {
            return this.Rent(runtime, checked((ulong)elements * 32UL), usage, VkMemoryPropertyFlags.DeviceLocal);
        }

        /// <summary>
        /// Rents a buffer from the resource pool and records its lease for workspace disposal
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool supplies the buffer</param>
        /// <param name="size">Minimum number of bytes required by the buffer</param>
        /// <param name="usage">Vulkan usage flags required by the buffer consumers</param>
        /// <param name="properties">Vulkan memory properties required by the buffer consumers</param>
        /// <returns>The buffer owned through the recorded lease</returns>
        private VulkanBuffer Rent(VulkanRuntimeContext runtime, ulong size, VkBufferUsageFlags usage, VkMemoryPropertyFlags properties)
        {
            VulkanBufferLease lease = runtime.ResourcePool.Rent(Math.Max(1UL, size), usage, properties);
            this._leases.Add(lease);
            return lease.Buffer;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the host-visible staging buffer containing frame pixels at their planned byte offsets
        /// </summary>
        public VulkanBuffer InputStaging { get; }

        /// <summary>
        /// Gets the device-local buffer containing the packed input images after transfer
        /// </summary>
        public VulkanBuffer PackedInput { get; }

        /// <summary>
        /// Gets the device-local buffer containing normalized input intensities
        /// </summary>
        public VulkanBuffer InputFloat { get; }

        /// <summary>
        /// Gets the device-local temporary floating-point workspace
        /// </summary>
        public VulkanBuffer TemporaryFloat { get; }

        /// <summary>
        /// Gets the device-local Gaussian-pyramid storage
        /// </summary>
        public VulkanBuffer Gaussian { get; }

        /// <summary>
        /// Gets the device-local gradient storage
        /// </summary>
        public VulkanBuffer Gradients { get; }

        /// <summary>
        /// Gets the device-local Difference-of-Gaussians storage
        /// </summary>
        public VulkanBuffer Dog { get; }

        /// <summary>
        /// Gets the device-local candidate flag storage
        /// </summary>
        public VulkanBuffer Flags { get; }

        /// <summary>
        /// Gets the device-local prefix-scan output storage
        /// </summary>
        public VulkanBuffer Prefix { get; }

        /// <summary>
        /// Gets the device-local scratch storage for prefix scans
        /// </summary>
        public VulkanBuffer ScanScratch { get; }

        /// <summary>
        /// Gets the device-local storage for packed candidate records
        /// </summary>
        public VulkanBuffer Candidates { get; }

        /// <summary>
        /// Gets the device-local storage for extracted keypoint records
        /// </summary>
        public VulkanBuffer Keypoints { get; }

        /// <summary>
        /// Gets the device-local storage for keypoint records after sorting
        /// </summary>
        public VulkanBuffer SortedKeypoints { get; }

        /// <summary>
        /// Gets the device-local storage for keypoint records after orientation assignment
        /// </summary>
        public VulkanBuffer OrientedKeypoints { get; }

        /// <summary>
        /// Gets the device-local storage for packed descriptor values
        /// </summary>
        public VulkanBuffer Descriptors { get; }

        /// <summary>
        /// Gets the device-local counter storage, also usable as a transfer source
        /// </summary>
        public VulkanBuffer Counters { get; }

        /// <summary>
        /// Gets the device-local storage for indirect dispatch commands
        /// </summary>
        public VulkanBuffer IndirectCommands { get; }

        #endregion
    }
}
