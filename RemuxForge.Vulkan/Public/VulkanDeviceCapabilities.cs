namespace RemuxForge.Vulkan
{
    /// <summary>
    /// Describes the identity, limits, optional features, and selected execution tier of a Vulkan physical device
    /// </summary>
    public sealed class VulkanDeviceCapabilities
    {
        /// <summary>
        /// Initializes a capability object with empty textual identity values
        /// </summary>
        internal VulkanDeviceCapabilities()
        {
            this.DeviceName = "";
        }

        /// <summary>Gets the zero-based index assigned by Vulkan physical-device enumeration</summary>
        public int EnumerationIndex { get; internal set; }
        /// <summary>Gets the device name reported by the driver, or the runtime fallback when Vulkan provides none</summary>
        public string DeviceName { get; internal set; }
        /// <summary>Gets the PCI vendor identifier reported by the physical device</summary>
        public uint VendorId { get; internal set; }
        /// <summary>Gets the PCI device identifier reported by the physical device</summary>
        public uint DeviceId { get; internal set; }
        /// <summary>Gets the numeric Vulkan value corresponding to <c>VkPhysicalDeviceType</c></summary>
        public uint DeviceType { get; internal set; }
        /// <summary>Gets the index of the compute queue family selected for the runtime</summary>
        public uint ComputeQueueFamilyIndex { get; internal set; }
        /// <summary>Gets the maximum byte range addressable by one storage buffer</summary>
        public ulong MaximumStorageBufferRange { get; internal set; }
        /// <summary>Gets the minimum byte alignment required for storage-buffer offsets</summary>
        public ulong MinimumStorageBufferOffsetAlignment { get; internal set; }
        /// <summary>Gets the maximum shared memory available to one compute workgroup, in bytes</summary>
        public uint MaximumComputeSharedMemorySize { get; internal set; }
        /// <summary>Gets the maximum number of compute workgroups dispatchable along the Y axis</summary>
        public uint MaximumComputeWorkGroupCountY { get; internal set; }
        /// <summary>Gets the native subgroup size in invocations</summary>
        public uint SubgroupSize { get; internal set; }
        /// <summary>Gets whether the compute stage supports the required subgroup ballot operations</summary>
        public bool SubgroupBallot { get; internal set; }
        /// <summary>Gets whether the required accelerated packed unsigned integer dot-product operations are supported</summary>
        public bool IntegerDotProduct { get; internal set; }
        /// <summary>Gets whether shaders may use 64-bit integer arithmetic</summary>
        public bool ShaderInt64 { get; internal set; }
        /// <summary>Gets whether a compatible subgroup cooperative matrix configuration is available</summary>
        public bool CooperativeMatrix { get; internal set; }
        /// <summary>Gets whether the device exposes the <c>VK_EXT_memory_budget</c> extension</summary>
        public bool MemoryBudget { get; internal set; }
        /// <summary>Gets whether the device exposes the <c>VK_KHR_portability_subset</c> extension</summary>
        public bool PortabilitySubset { get; internal set; }
        /// <summary>Gets whether timestamp queries are currently considered usable for compute work</summary>
        public bool TimestampQueries { get; internal set; }
        /// <summary>Gets the duration of one GPU timestamp tick, in nanoseconds</summary>
        public float TimestampPeriodNanoseconds { get; internal set; }
        /// <summary>Gets the capability tier assigned by the runtime to the physical device</summary>
        public VulkanCapabilityTier Tier { get; internal set; }
    }
}
