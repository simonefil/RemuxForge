using System;

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
            this.ApiVersion = "";
            this.DriverVersion = "";
        }

        /// <summary>Gets the zero-based index assigned by Vulkan physical-device enumeration</summary>
        public int EnumerationIndex { get; internal set; }
        /// <summary>Gets the device name reported by the driver, or the runtime fallback when Vulkan provides none</summary>
        public string DeviceName { get; internal set; }
        /// <summary>Gets the Vulkan API version exposed by the physical device in major.minor.patch format</summary>
        public string ApiVersion { get; internal set; }
        /// <summary>Gets the driver version rendered from Vulkan's numeric version value</summary>
        public string DriverVersion { get; internal set; }
        /// <summary>Gets the raw Vulkan driver version used when identifying compatible pipeline-cache data</summary>
        public uint DriverVersionRaw { get; internal set; }
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
        /// <summary>Gets the initial effective device-local memory-pressure threshold in bytes</summary>
        public ulong MemoryPressureThresholdBytes { get; internal set; }
        /// <summary>Gets the maximum number of invocations in one compute workgroup</summary>
        public uint MaximumComputeWorkGroupInvocations { get; internal set; }
        /// <summary>Gets the maximum shared memory available to one compute workgroup, in bytes</summary>
        public uint MaximumComputeSharedMemorySize { get; internal set; }
        /// <summary>Gets the maximum number of compute workgroups dispatchable along the X axis</summary>
        public uint MaximumComputeWorkGroupCountX { get; internal set; }
        /// <summary>Gets the maximum number of compute workgroups dispatchable along the Y axis</summary>
        public uint MaximumComputeWorkGroupCountY { get; internal set; }
        /// <summary>Gets the native subgroup size in invocations</summary>
        public uint SubgroupSize { get; internal set; }
        /// <summary>Gets whether the compute stage supports the required subgroup ballot operations</summary>
        public bool SubgroupBallot { get; internal set; }
        /// <summary>Gets whether the required accelerated packed unsigned integer dot-product operations are supported</summary>
        public bool IntegerDotProduct { get; internal set; }
        /// <summary>Gets whether a compatible subgroup cooperative matrix configuration is available</summary>
        public bool CooperativeMatrix { get; internal set; }
        /// <summary>Gets the M dimension of the selected cooperative matrix configuration</summary>
        public uint CooperativeMatrixMSize { get; internal set; }
        /// <summary>Gets the N dimension of the selected cooperative matrix configuration</summary>
        public uint CooperativeMatrixNSize { get; internal set; }
        /// <summary>Gets the K dimension of the selected cooperative matrix configuration</summary>
        public uint CooperativeMatrixKSize { get; internal set; }
        /// <summary>Gets whether timeline semaphore support was recorded for the selected device</summary>
        public bool TimelineSemaphore { get; internal set; }
        /// <summary>Gets whether the device exposes the <c>VK_EXT_memory_budget</c> extension</summary>
        public bool MemoryBudget { get; internal set; }
        /// <summary>Gets whether the device exposes the <c>VK_KHR_portability_subset</c> extension</summary>
        public bool PortabilitySubset { get; internal set; }
        /// <summary>Gets whether timestamp queries are currently considered usable for compute work</summary>
        public bool TimestampQueries { get; internal set; }
        /// <summary>Gets the duration of one GPU timestamp tick, in nanoseconds</summary>
        public float TimestampPeriodNanoseconds { get; internal set; }
        /// <summary>Gets the 16-byte Vulkan pipeline-cache UUID associated with the physical device</summary>
        public ReadOnlyMemory<byte> PipelineCacheUuid { get; internal set; }
        /// <summary>Gets the capability tier assigned by the runtime to the physical device</summary>
        public VulkanCapabilityTier Tier { get; internal set; }
    }
}
