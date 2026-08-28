using RemuxForge.Vulkan.Runtime;
using System;
using System.Collections.Generic;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Memory
{
    /// <summary>
    /// Allocates and recycles Vulkan memory blocks while respecting the configured budget
    /// </summary>
    internal sealed unsafe class VulkanMemoryAllocator : IDisposable
    {
        #region Constants

        /// <summary>
        /// Maximum default target size for a device-local memory block
        /// </summary>
        private const ulong DEVICE_BLOCK_SIZE = 256UL * 1024UL * 1024UL;

        /// <summary>
        /// Default allocation size for a host-visible memory block
        /// </summary>
        private const ulong HOST_BLOCK_SIZE = 64UL * 1024UL * 1024UL;

        /// <summary>
        /// Capacity boundary at which the pressure threshold changes from a ratio to a reserve
        /// </summary>
        private const ulong FOUR_GIB = 4UL * 1024UL * 1024UL * 1024UL;

        /// <summary>
        /// Reserve subtracted from large device-local capacities
        /// </summary>
        private const ulong ONE_GIB = 1024UL * 1024UL * 1024UL;

        #endregion

        #region Instance fields

        /// <summary>
        /// Runtime context borrowed by the allocator for Vulkan queries and device operations
        /// </summary>
        private readonly VulkanRuntimeContext _runtime;

        /// <summary>
        /// Lock protecting the block list and all allocation state transitions
        /// </summary>
        private readonly object _sync;

        /// <summary>
        /// Vulkan memory blocks owned by this allocator
        /// </summary>
        private readonly List<VulkanMemoryBlock> _blocks;

        /// <summary>
        /// Optional caller-provided upper bound for device-local allocations
        /// </summary>
        private readonly ulong _callerCap;

        /// <summary>
        /// Pressure threshold used when memory-budget reporting is unavailable or yields no capacity
        /// </summary>
        private readonly ulong _fallbackPressureThreshold;

        /// <summary>
        /// Indicates whether the allocator has released its owned memory blocks
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the allocator and derives its initial device-local pressure threshold
        /// </summary>
        /// <param name="runtime">Runtime context that owns the Vulkan device used by the allocator</param>
        /// <param name="callerCap">Optional maximum number of device-local bytes that this allocator may reserve, or zero for no explicit cap</param>
        public VulkanMemoryAllocator(VulkanRuntimeContext runtime, ulong callerCap)
        {
            this._runtime = runtime;
            this._sync = new object();
            this._blocks = new List<VulkanMemoryBlock>();
            ulong available = this.GetDeviceLocalAvailableBytes();
            if (available == 0)
                available = ONE_GIB;
            ulong threshold = available < FOUR_GIB ? available * 3 / 4 : available - ONE_GIB;
            this._callerCap = callerCap;
            this._fallbackPressureThreshold = callerCap > 0 ? Math.Min(threshold, callerCap) : threshold;
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Creates a Vulkan buffer and assigns it a compatible memory range
        /// </summary>
        /// <param name="size">Logical size of the buffer in bytes; zero is normalized to the minimum Vulkan buffer size used by this allocator</param>
        /// <param name="usage">Usage flags that are passed to Vulkan when creating the buffer</param>
        /// <param name="properties">Memory property flags required for the allocation</param>
        /// <returns>A disposable buffer that owns the Vulkan buffer handle and its allocated range</returns>
        public VulkanBuffer AllocateBuffer(ulong size, VkBufferUsageFlags usage, VkMemoryPropertyFlags properties)
        {
            if (size == 0)
                size = 4;
            lock (this._sync)
            {
                this.ThrowIfDisposed();
                VkBufferCreateInfo createInfo = new VkBufferCreateInfo { size = size, usage = usage, sharingMode = VkSharingMode.Exclusive };
                this._runtime.DeviceApi.vkCreateBuffer(&createInfo, null, out VkBuffer buffer).CheckResult();
                try
                {
                    this._runtime.DeviceApi.vkGetBufferMemoryRequirements(buffer, out VkMemoryRequirements requirements);
                    uint memoryTypeIndex = this.FindMemoryType(requirements.memoryTypeBits, properties);
                    VulkanMemoryBlock block = this.FindBlock(memoryTypeIndex, requirements.size, requirements.alignment);
                    if (block == null)
                    {
                        this.TrimEmptyBlocks();
                        block = this.FindBlock(memoryTypeIndex, requirements.size, requirements.alignment) ?? this.CreateBlock(memoryTypeIndex, properties, requirements.size);
                    }
                    if (!block.TryAllocate(requirements.size, requirements.alignment, out ulong offset))
                        throw new VulkanResourceExhaustedException("The selected Vulkan block does not contain a valid slice.");
                    try
                    {
                        this._runtime.DeviceApi.vkBindBufferMemory(buffer, block.Memory, offset).CheckResult();
                        return new VulkanBuffer(this, block, this._runtime.DeviceApi, buffer, offset, size, requirements.size, properties);
                    }
                    catch
                    {
                        block.Release(offset, requirements.size);
                        throw;
                    }
                }
                catch
                {
                    this._runtime.DeviceApi.vkDestroyBuffer(buffer);
                    throw;
                }
            }
        }

        /// <summary>
        /// Captures the allocator's current usage, cache and pressure metrics
        /// </summary>
        /// <returns>An immutable snapshot of device-local and host-visible allocation metrics</returns>
        public VulkanMemoryStatistics GetStatistics()
        {
            lock (this._sync)
            {
                ulong allocated = 0;
                ulong used = 0;
                ulong hostAllocated = 0;
                ulong hostUsed = 0;
                for (int i = 0; i < this._blocks.Count; i++)
                {
                    if ((this._blocks[i].Properties & VkMemoryPropertyFlags.DeviceLocal) != 0)
                    {
                        allocated += this._blocks[i].Size;
                        used += this._blocks[i].UsedBytes;
                    }
                    else
                    {
                        hostAllocated += this._blocks[i].Size;
                        hostUsed += this._blocks[i].UsedBytes;
                    }
                }
                return new VulkanMemoryStatistics(allocated, used, allocated - used, this._blocks.Count, this.GetPressureThreshold(), hostAllocated, hostUsed);
            }
        }

        #endregion

        #region Internal methods

        /// <summary>
        /// Returns a released buffer range to its owning block
        /// </summary>
        /// <param name="block">Block that contains the released range</param>
        /// <param name="offset">Starting byte offset of the range within the block</param>
        /// <param name="size">Size of the range in bytes</param>
        internal void Release(VulkanMemoryBlock block, ulong offset, ulong size)
        {
            lock (this._sync)
            {
                if (!this._disposed)
                    block.Release(offset, size);
            }
        }

        /// <summary>
        /// Releases all Vulkan memory blocks owned by this allocator
        /// </summary>
        public void Dispose()
        {
            lock (this._sync)
            {
                if (this._disposed)
                    return;
                this._disposed = true;
                for (int i = this._blocks.Count - 1; i >= 0; i--)
                {
                    if (this._blocks[i].MappedPointer != IntPtr.Zero)
                        this._runtime.DeviceApi.vkUnmapMemory(this._blocks[i].Memory);
                    this._runtime.DeviceApi.vkFreeMemory(this._blocks[i].Memory);
                }
                this._blocks.Clear();
            }
        }

        #endregion

        #region Private methods

        /// <summary>
        /// Finds an existing block that can conservatively satisfy an allocation request
        /// </summary>
        /// <param name="memoryTypeIndex">Memory type index required by the allocation</param>
        /// <param name="size">Requested slice size in bytes</param>
        /// <param name="alignment">Required slice alignment in bytes</param>
        /// <returns>The first compatible block whose largest free range can contain the request, or <see langword="null"/> when no block qualifies</returns>
        private VulkanMemoryBlock FindBlock(uint memoryTypeIndex, ulong size, ulong alignment)
        {
            for (int i = 0; i < this._blocks.Count; i++)
            {
                VulkanMemoryBlock block = this._blocks[i];
                if (block.MemoryTypeIndex == memoryTypeIndex && block.LargestFreeRange >= size + Math.Max(0UL, alignment - 1))
                    return block;
            }
            return null;
        }

        /// <summary>
        /// Creates and maps a new memory block while respecting the pressure threshold
        /// </summary>
        /// <param name="memoryTypeIndex">Memory type index selected for the block</param>
        /// <param name="properties">Memory properties required by buffers using the block</param>
        /// <param name="minimumSize">Minimum block size required by the current allocation</param>
        /// <returns>A newly allocated block, optionally persistently mapped for host-visible memory</returns>
        private VulkanMemoryBlock CreateBlock(uint memoryTypeIndex, VkMemoryPropertyFlags properties, ulong minimumSize)
        {
            bool deviceLocal = (properties & VkMemoryPropertyFlags.DeviceLocal) != 0;
            ulong pressureThreshold = this.GetPressureThreshold();
            ulong defaultSize = deviceLocal ? Math.Min(DEVICE_BLOCK_SIZE, Math.Max(16UL * 1024UL * 1024UL, pressureThreshold / 4)) : HOST_BLOCK_SIZE;
            ulong blockSize = Math.Max(defaultSize, AlignUp(minimumSize, 4UL * 1024UL * 1024UL));
            ulong allocated = 0;
            for (int i = 0; i < this._blocks.Count; i++)
            {
                if ((this._blocks[i].Properties & VkMemoryPropertyFlags.DeviceLocal) != 0)
                    allocated += this._blocks[i].Size;
            }
            if (deviceLocal && (blockSize > pressureThreshold || allocated > pressureThreshold - blockSize))
                throw new VulkanResourceExhaustedException("The Vulkan VRAM budget remains exhausted after cache cleanup.");
            VkMemoryAllocateInfo allocateInfo = new VkMemoryAllocateInfo { allocationSize = blockSize, memoryTypeIndex = memoryTypeIndex };
            VkResult result = this._runtime.DeviceApi.vkAllocateMemory(&allocateInfo, null, out VkDeviceMemory memory);
            if (result != VkResult.Success)
            {
                this.TrimEmptyBlocks();
                result = this._runtime.DeviceApi.vkAllocateMemory(&allocateInfo, null, out memory);
            }
            if (result != VkResult.Success)
                throw new VulkanResourceExhaustedException("Vulkan memory allocation failed after cache cleanup: " + result);
            IntPtr mappedPointer = IntPtr.Zero;
            if ((properties & VkMemoryPropertyFlags.HostVisible) != 0)
            {
                void* mapped;
                VkResult mapResult = this._runtime.DeviceApi.vkMapMemory(memory, 0, blockSize, 0, &mapped);
                if (mapResult != VkResult.Success)
                {
                    this._runtime.DeviceApi.vkFreeMemory(memory);
                    throw new VulkanResourceExhaustedException("Persistent Vulkan memory mapping failed: " + mapResult);
                }
                mappedPointer = (IntPtr)mapped;
            }
            VulkanMemoryBlock block = new VulkanMemoryBlock(memory, memoryTypeIndex, properties, blockSize, mappedPointer);
            this._blocks.Add(block);
            return block;
        }

        /// <summary>
        /// Releases blocks that contain no active slices
        /// </summary>
        private void TrimEmptyBlocks()
        {
            for (int i = this._blocks.Count - 1; i >= 0; i--)
            {
                if (!this._blocks[i].IsEmpty)
                    continue;
                if (this._blocks[i].MappedPointer != IntPtr.Zero)
                    this._runtime.DeviceApi.vkUnmapMemory(this._blocks[i].Memory);
                this._runtime.DeviceApi.vkFreeMemory(this._blocks[i].Memory);
                this._blocks.RemoveAt(i);
            }
        }

        /// <summary>
        /// Selects a memory type that satisfies all requested properties
        /// </summary>
        /// <param name="typeBits">Bit mask identifying memory types permitted by Vulkan requirements</param>
        /// <param name="required">Memory property flags that the selected type must contain</param>
        /// <returns>The index of the first permitted memory type containing every required property</returns>
        private uint FindMemoryType(uint typeBits, VkMemoryPropertyFlags required)
        {
            this._runtime.InstanceApi.vkGetPhysicalDeviceMemoryProperties(this._runtime.PhysicalDevice, out VkPhysicalDeviceMemoryProperties properties);
            for (uint i = 0; i < properties.memoryTypeCount; i++)
            {
                if ((typeBits & (1u << (int)i)) != 0 && (properties.memoryTypes[(int)i].propertyFlags & required) == required)
                    return i;
            }
            throw new VulkanCapabilityUnsupportedException("The requested Vulkan memory type is unavailable: " + required);
        }

        /// <summary>
        /// Calculates the currently available device-local memory
        /// </summary>
        /// <returns>The sum of available bytes in device-local heaps</returns>
        private ulong GetDeviceLocalAvailableBytes()
        {
            if (this._runtime.Capabilities.MemoryBudget)
            {
                VkPhysicalDeviceMemoryBudgetPropertiesEXT budget = new VkPhysicalDeviceMemoryBudgetPropertiesEXT();
                VkPhysicalDeviceMemoryProperties2 properties2 = new VkPhysicalDeviceMemoryProperties2 { pNext = &budget };
                this._runtime.InstanceApi.vkGetPhysicalDeviceMemoryProperties2(this._runtime.PhysicalDevice, &properties2);
                ulong budgetAvailable = 0;
                for (int i = 0; i < properties2.memoryProperties.memoryHeapCount; i++)
                {
                    if ((properties2.memoryProperties.memoryHeaps[i].flags & VkMemoryHeapFlags.DeviceLocal) == 0)
                        continue;
                    ulong heapBudget = budget.heapBudget[i];
                    ulong heapUsage = budget.heapUsage[i];
                    budgetAvailable += heapBudget > heapUsage ? heapBudget - heapUsage : 0;
                }
                if (budgetAvailable > 0)
                    return budgetAvailable;
            }
            this._runtime.InstanceApi.vkGetPhysicalDeviceMemoryProperties(this._runtime.PhysicalDevice, out VkPhysicalDeviceMemoryProperties properties);
            ulong result = 0;
            for (int i = 0; i < properties.memoryHeapCount; i++)
            {
                if ((properties.memoryHeaps[i].flags & VkMemoryHeapFlags.DeviceLocal) != 0)
                    result += properties.memoryHeaps[i].size;
            }
            return result;
        }

        /// <summary>
        /// Calculates the effective pressure threshold from the current budget and external usage
        /// </summary>
        /// <returns>The maximum device-local capacity this allocator should target</returns>
        private ulong GetPressureThreshold()
        {
            if (!this._runtime.Capabilities.MemoryBudget)
                return this._fallbackPressureThreshold;
            ulong ownedDeviceBytes = 0;
            for (int i = 0; i < this._blocks.Count; i++)
            {
                if ((this._blocks[i].Properties & VkMemoryPropertyFlags.DeviceLocal) != 0)
                    ownedDeviceBytes += this._blocks[i].Size;
            }
            VkPhysicalDeviceMemoryBudgetPropertiesEXT budget = new VkPhysicalDeviceMemoryBudgetPropertiesEXT();
            VkPhysicalDeviceMemoryProperties2 properties = new VkPhysicalDeviceMemoryProperties2 { pNext = &budget };
            this._runtime.InstanceApi.vkGetPhysicalDeviceMemoryProperties2(this._runtime.PhysicalDevice, &properties);
            ulong effectiveCapacity = 0;
            for (int i = 0; i < properties.memoryProperties.memoryHeapCount; i++)
            {
                if ((properties.memoryProperties.memoryHeaps[i].flags & VkMemoryHeapFlags.DeviceLocal) == 0)
                    continue;
                ulong externalUsage = budget.heapUsage[i] > ownedDeviceBytes ? budget.heapUsage[i] - ownedDeviceBytes : 0;
                effectiveCapacity += budget.heapBudget[i] > externalUsage ? budget.heapBudget[i] - externalUsage : 0;
            }
            if (effectiveCapacity == 0)
                return this._fallbackPressureThreshold;
            ulong threshold = effectiveCapacity < FOUR_GIB ? effectiveCapacity * 3 / 4 : effectiveCapacity - ONE_GIB;
            return this._callerCap > 0 ? Math.Min(threshold, this._callerCap) : threshold;
        }

        /// <summary>
        /// Rounds a value up to the next multiple of an alignment
        /// </summary>
        /// <param name="value">Value to round</param>
        /// <param name="alignment">Non-zero alignment multiple</param>
        /// <returns><paramref name="value"/> when it is already aligned, otherwise the next aligned value</returns>
        private static ulong AlignUp(ulong value, ulong alignment)
        {
            ulong remainder = value % alignment;
            return remainder == 0 ? value : checked(value + alignment - remainder);
        }

        /// <summary>
        /// Rejects use of the allocator after disposal
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (this._disposed)
                throw new ObjectDisposedException(nameof(VulkanMemoryAllocator));
        }

        #endregion
    }

    /// <summary>
    /// Immutable snapshot of the Vulkan memory allocator's current metrics
    /// </summary>
    internal readonly struct VulkanMemoryStatistics
    {
        /// <summary>
        /// Initializes a metrics snapshot
        /// </summary>
        /// <param name="allocatedBytes">Total bytes reserved in device-local blocks</param>
        /// <param name="usedBytes">Bytes currently occupied by active device-local slices</param>
        /// <param name="cachedBytes">Unoccupied bytes retained in device-local blocks</param>
        /// <param name="blockCount">Number of device-local and host-visible blocks owned by the allocator</param>
        /// <param name="pressureThreshold">Current device-local pressure threshold</param>
        /// <param name="hostAllocatedBytes">Total bytes reserved in host-visible blocks</param>
        /// <param name="hostUsedBytes">Bytes currently occupied by active host-visible slices</param>
        public VulkanMemoryStatistics(ulong allocatedBytes, ulong usedBytes, ulong cachedBytes, int blockCount, ulong pressureThreshold, ulong hostAllocatedBytes, ulong hostUsedBytes)
        {
            this.AllocatedBytes = allocatedBytes;
            this.UsedBytes = usedBytes;
            this.CachedBytes = cachedBytes;
            this.BlockCount = blockCount;
            this.PressureThreshold = pressureThreshold;
            this.HostAllocatedBytes = hostAllocatedBytes;
            this.HostUsedBytes = hostUsedBytes;
        }

        /// <summary>
        /// Total bytes reserved in device-local blocks
        /// </summary>
        public ulong AllocatedBytes { get; }

        /// <summary>
        /// Bytes currently occupied by active device-local slices
        /// </summary>
        public ulong UsedBytes { get; }

        /// <summary>
        /// Unoccupied bytes retained in device-local blocks
        /// </summary>
        public ulong CachedBytes { get; }

        /// <summary>
        /// Number of device-local and host-visible blocks owned by the allocator
        /// </summary>
        public int BlockCount { get; }

        /// <summary>
        /// Current device-local pressure threshold
        /// </summary>
        public ulong PressureThreshold { get; }

        /// <summary>
        /// Total bytes reserved in host-visible blocks
        /// </summary>
        public ulong HostAllocatedBytes { get; }

        /// <summary>
        /// Bytes currently occupied by active host-visible slices
        /// </summary>
        public ulong HostUsedBytes { get; }
    }
}
