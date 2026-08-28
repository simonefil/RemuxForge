using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Runtime;
using System;
using System.Collections.Generic;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Vision.Sift
{
    /// <summary>
    /// Owns the Vulkan buffer views and temporary storage required to extract SIFT features for one frame
    /// </summary>
    internal sealed class VulkanSiftWorkspace : IDisposable
    {
        #region Class fields

        /// <summary>
        /// Tracks the buffer leases owned by this workspace in acquisition order
        /// </summary>
        private readonly List<VulkanBufferLease> _leases;

        /// <summary>
        /// Indicates whether the owned leases have already been released
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructors

        /// <summary>
        /// Creates an independently allocated workspace for one image frame
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool supplies the Vulkan buffers</param>
        /// <param name="frame">Image frame whose pixels are copied to the host-visible staging buffer</param>
        /// <param name="plan">SIFT plan that determines buffer capacities and offsets</param>
        public VulkanSiftWorkspace(VulkanRuntimeContext runtime, VulkanImageFrame frame, VulkanSiftPlan plan)
        {
            this.Frame = frame;
            this.Plan = plan;
            this.PackedWorkspace = null;
            this._leases = new List<VulkanBufferLease>();
            ulong inputBytes = checked((ulong)(frame.Stride * frame.Height));
            this.InputStaging = this.Rent(runtime, inputBytes, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            this.PackedInput = this.Rent(runtime, inputBytes, VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal);
            this.InputFloat = this.RentFloat(runtime, plan.InputFloatElements, VkBufferUsageFlags.StorageBuffer);
            this.TemporaryFloat = this.RentFloat(runtime, plan.TemporaryFloatElements, VkBufferUsageFlags.StorageBuffer);
            this.Gaussian = this.RentFloat(runtime, plan.GaussianFloatElements, VkBufferUsageFlags.StorageBuffer);
            this.Gradients = this.RentFloat(runtime, checked(plan.GaussianFloatElements * 2), VkBufferUsageFlags.StorageBuffer);
            this.Dog = this.RentFloat(runtime, plan.DogFloatElements, VkBufferUsageFlags.StorageBuffer);
            int flagElements = Math.Max(plan.CandidateCount, plan.OrientationCapacity);
            this.Flags = this.RentUInt(runtime, flagElements, VkBufferUsageFlags.StorageBuffer);
            this.Prefix = this.RentUInt(runtime, flagElements, VkBufferUsageFlags.StorageBuffer);
            this.ScanScratch = this.RentUInt(runtime, plan.ScanScratchElements, VkBufferUsageFlags.StorageBuffer);
            this.Candidates = this.Rent(runtime, checked((ulong)Math.Max(plan.CandidateCount, plan.OrientationCapacity) * 32UL), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal);
            this.Keypoints = this.Rent(runtime, checked((ulong)plan.FeatureCapacity * 32UL), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal);
            this.SortedKeypoints = this.Rent(runtime, checked((ulong)plan.FeatureCapacity * 32UL), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal);
            this.OrientedKeypoints = this.Rent(runtime, checked((ulong)plan.OrientationCapacity * 32UL), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal);
            this.Descriptors = this.RentUInt(runtime, checked(plan.OrientationCapacity * 32), VkBufferUsageFlags.StorageBuffer);
            this.Counters = this.RentUInt(runtime, checked(plan.Octaves.Count * 5 + 5), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferSrc);
            this.IndirectCommands = this.RentUInt(runtime, checked(plan.Octaves.Count * 6), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.IndirectBuffer);
            this.InputStaging.Write<byte>(frame.Pixels.Span);
        }

        /// <summary>
        /// Creates a workspace as a set of views into a packed SIFT workspace
        /// </summary>
        /// <param name="frame">Image frame represented by the selected packed slice</param>
        /// <param name="plan">SIFT plan that determines the selected slice sizes</param>
        /// <param name="workspace">Packed workspace that owns the underlying allocations</param>
        /// <param name="framePlan">Packed offsets for the selected frame</param>
        public VulkanSiftWorkspace(VulkanImageFrame frame, VulkanSiftPlan plan, VulkanSiftPackedWorkspace workspace, VulkanSiftPackedFramePlan framePlan)
        {
            this.Frame = frame ?? throw new ArgumentNullException(nameof(frame));
            this.Plan = plan ?? throw new ArgumentNullException(nameof(plan));
            ArgumentNullException.ThrowIfNull(workspace);
            ArgumentNullException.ThrowIfNull(framePlan);
            this._leases = new List<VulkanBufferLease>();
            this.PackedWorkspace = workspace;
            ulong inputBytes = checked((ulong)frame.Stride * (ulong)frame.Height);
            int flagElements = Math.Max(plan.CandidateCount, plan.OrientationCapacity);
            int candidateElements = Math.Max(plan.CandidateCount, plan.OrientationCapacity);
            this.InputStaging = workspace.InputStaging.CreateView(framePlan.InputByteOffset, inputBytes);
            this.PackedInput = workspace.PackedInput.CreateView(framePlan.InputByteOffset, inputBytes);
            this.InputFloat = workspace.InputFloat.CreateView(checked((ulong)framePlan.InputFloatOffset * sizeof(float)), checked((ulong)plan.InputFloatElements * sizeof(float)));
            this.TemporaryFloat = workspace.TemporaryFloat.CreateView(checked((ulong)framePlan.TemporaryFloatOffset * sizeof(float)), checked((ulong)plan.TemporaryFloatElements * sizeof(float)));
            this.Gaussian = workspace.Gaussian.CreateView(checked((ulong)framePlan.GaussianFloatOffset * sizeof(float)), checked((ulong)plan.GaussianFloatElements * sizeof(float)));
            this.Gradients = workspace.Gradients.CreateView(checked((ulong)framePlan.GradientFloatOffset * sizeof(float)), checked((ulong)plan.GaussianFloatElements * 2UL * sizeof(float)));
            this.Dog = workspace.Dog.CreateView(checked((ulong)framePlan.DogFloatOffset * sizeof(float)), checked((ulong)plan.DogFloatElements * sizeof(float)));
            this.Flags = workspace.Flags.CreateView(checked((ulong)framePlan.FlagOffset * sizeof(uint)), checked((ulong)flagElements * sizeof(uint)));
            this.Prefix = workspace.Prefix.CreateView(checked((ulong)framePlan.FlagOffset * sizeof(uint)), checked((ulong)flagElements * sizeof(uint)));
            this.ScanScratch = workspace.ScanScratch.CreateView(checked((ulong)framePlan.ScanScratchOffset * sizeof(uint)), checked((ulong)plan.ScanScratchElements * sizeof(uint)));
            this.Candidates = workspace.Candidates.CreateView(checked((ulong)framePlan.CandidateOffset * 32UL), checked((ulong)candidateElements * 32UL));
            this.Keypoints = workspace.Keypoints.CreateView(checked((ulong)framePlan.KeypointOffset * 32UL), checked((ulong)plan.FeatureCapacity * 32UL));
            this.SortedKeypoints = workspace.SortedKeypoints.CreateView(checked((ulong)framePlan.KeypointOffset * 32UL), checked((ulong)plan.FeatureCapacity * 32UL));
            this.OrientedKeypoints = workspace.OrientedKeypoints.CreateView(checked((ulong)framePlan.OrientedKeypointOffset * 32UL), checked((ulong)plan.OrientationCapacity * 32UL));
            this.Descriptors = workspace.Descriptors.CreateView(checked((ulong)framePlan.DescriptorOffset * sizeof(uint)), checked((ulong)plan.OrientationCapacity * 32UL * sizeof(uint)));
            this.Counters = workspace.Counters.CreateView(checked((ulong)framePlan.CounterOffset * sizeof(uint)), checked((ulong)(plan.Octaves.Count * 5 + 5) * sizeof(uint)));
            this.IndirectCommands = workspace.IndirectCommands.CreateView(checked((ulong)framePlan.IndirectCommandOffset * sizeof(uint)), checked((ulong)plan.Octaves.Count * 6UL * sizeof(uint)));
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Releases all resources owned by this workspace
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

        #region Private methods

        /// <summary>
        /// Rents a device-local buffer sized for the specified number of single-precision elements
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool supplies the buffer</param>
        /// <param name="elements">Number of single-precision elements required by the buffer</param>
        /// <param name="usage">Vulkan usage flags for the buffer</param>
        /// <returns>The rented buffer</returns>
        private VulkanBuffer RentFloat(VulkanRuntimeContext runtime, int elements, VkBufferUsageFlags usage)
        {
            return this.Rent(runtime, checked((ulong)elements * sizeof(float)), usage, VkMemoryPropertyFlags.DeviceLocal);
        }

        /// <summary>
        /// Rents a device-local buffer sized for the specified number of unsigned integer elements
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool supplies the buffer</param>
        /// <param name="elements">Number of unsigned integer elements required by the buffer</param>
        /// <param name="usage">Vulkan usage flags for the buffer</param>
        /// <returns>The rented buffer</returns>
        private VulkanBuffer RentUInt(VulkanRuntimeContext runtime, int elements, VkBufferUsageFlags usage)
        {
            return this.Rent(runtime, checked((ulong)elements * sizeof(uint)), usage, VkMemoryPropertyFlags.DeviceLocal);
        }

        /// <summary>
        /// Rents a Vulkan buffer and records its lease as owned by this workspace
        /// </summary>
        /// <param name="runtime">Runtime context whose resource pool supplies the buffer</param>
        /// <param name="size">Number of bytes required by the buffer</param>
        /// <param name="usage">Vulkan usage flags for the buffer</param>
        /// <param name="properties">Required Vulkan memory properties</param>
        /// <returns>The buffer associated with the recorded lease</returns>
        private VulkanBuffer Rent(VulkanRuntimeContext runtime, ulong size, VkBufferUsageFlags usage, VkMemoryPropertyFlags properties)
        {
            VulkanBufferLease lease = runtime.ResourcePool.Rent(size, usage, properties);
            this._leases.Add(lease);
            return lease.Buffer;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the image frame represented by this workspace
        /// </summary>
        public VulkanImageFrame Frame { get; }

        /// <summary>
        /// Gets the SIFT plan that defines this workspace's buffer capacities
        /// </summary>
        public VulkanSiftPlan Plan { get; }

        /// <summary>
        /// Gets the packed workspace that owns the underlying buffers, or null for an independently allocated workspace
        /// </summary>
        public VulkanSiftPackedWorkspace PackedWorkspace { get; }

        /// <summary>
        /// Gets the host-visible staging buffer containing the source frame pixels
        /// </summary>
        public VulkanBuffer InputStaging { get; }

        /// <summary>
        /// Gets the device-local buffer receiving the packed input representation
        /// </summary>
        public VulkanBuffer PackedInput { get; }

        /// <summary>
        /// Gets the storage buffer containing the floating-point input image
        /// </summary>
        public VulkanBuffer InputFloat { get; }

        /// <summary>
        /// Gets the storage buffer used for intermediate floating-point image data
        /// </summary>
        public VulkanBuffer TemporaryFloat { get; }

        /// <summary>
        /// Gets the storage buffer containing the Gaussian pyramid data
        /// </summary>
        public VulkanBuffer Gaussian { get; }

        /// <summary>
        /// Gets the storage buffer containing image gradients
        /// </summary>
        public VulkanBuffer Gradients { get; }

        /// <summary>
        /// Gets the storage buffer containing Difference-of-Gaussian data
        /// </summary>
        public VulkanBuffer Dog { get; }

        /// <summary>
        /// Gets the storage buffer containing candidate and orientation flags
        /// </summary>
        public VulkanBuffer Flags { get; }

        /// <summary>
        /// Gets the storage buffer containing prefix-scan results for the flags
        /// </summary>
        public VulkanBuffer Prefix { get; }

        /// <summary>
        /// Gets the storage buffer used as scratch space by the hierarchical prefix scan
        /// </summary>
        public VulkanBuffer ScanScratch { get; }

        /// <summary>
        /// Gets the storage buffer containing candidate records in the 32-byte shader layout
        /// </summary>
        public VulkanBuffer Candidates { get; }

        /// <summary>
        /// Gets the storage buffer containing detected keypoints in the 32-byte shader layout
        /// </summary>
        public VulkanBuffer Keypoints { get; }

        /// <summary>
        /// Gets the storage buffer containing keypoints after stable sorting
        /// </summary>
        public VulkanBuffer SortedKeypoints { get; }

        /// <summary>
        /// Gets the storage buffer containing keypoints after orientation assignment
        /// </summary>
        public VulkanBuffer OrientedKeypoints { get; }

        /// <summary>
        /// Gets the storage buffer containing 32 unsigned integer descriptor words per oriented keypoint
        /// </summary>
        public VulkanBuffer Descriptors { get; }

        /// <summary>
        /// Gets the storage buffer containing per-octave counters and transfer-source results
        /// </summary>
        public VulkanBuffer Counters { get; }

        /// <summary>
        /// Gets the storage buffer containing indirect dispatch commands for the SIFT stages
        /// </summary>
        public VulkanBuffer IndirectCommands { get; }

        #endregion
    }
}
