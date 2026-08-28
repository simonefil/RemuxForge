using System;
using System.Collections.Generic;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Memory
{
    /// <summary>
    /// Provides suballocation bookkeeping for a single Vulkan device-memory allocation
    /// </summary>
    internal sealed class VulkanMemoryBlock
    {
        #region Variabili di classe

        /// <summary>
        /// Sorted, non-overlapping free ranges that partition the currently available portion of the allocation
        /// </summary>
        private readonly List<FreeRange> _freeRanges;

        #endregion

        #region Costruttore

        /// <summary>
        /// Creates bookkeeping for an existing Vulkan device-memory allocation
        /// </summary>
        /// <param name="memory">Native device-memory allocation tracked by this block</param>
        /// <param name="memoryTypeIndex">Vulkan memory-type index used for the allocation</param>
        /// <param name="properties">Memory properties selected for the allocation</param>
        /// <param name="size">Total size of the native allocation in bytes</param>
        /// <param name="mappedPointer">Base address of the persistent host mapping, or <see cref="IntPtr.Zero"/> when the allocation is not mapped</param>
        public VulkanMemoryBlock(VkDeviceMemory memory, uint memoryTypeIndex, VkMemoryPropertyFlags properties, ulong size, IntPtr mappedPointer)
        {
            this.Memory = memory;
            this.MemoryTypeIndex = memoryTypeIndex;
            this.Properties = properties;
            this.Size = size;
            this.MappedPointer = mappedPointer;
            this._freeRanges = new List<FreeRange> { new FreeRange(0, size) };
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Attempts to reserve an aligned range from the block
        /// </summary>
        /// <param name="size">Number of bytes to reserve</param>
        /// <param name="alignment">Required byte alignment for the returned offset; values of zero and one require no adjustment</param>
        /// <param name="offset">Receives the aligned offset of the reserved range when the operation succeeds, or zero when it fails</param>
        /// <returns><c>true</c> when a suitable range was reserved; otherwise, <c>false</c></returns>
        public bool TryAllocate(ulong size, ulong alignment, out ulong offset)
        {
            for (int i = 0; i < this._freeRanges.Count; i++)
            {
                FreeRange range = this._freeRanges[i];
                ulong alignedOffset = AlignUp(range.Offset, alignment);
                ulong padding = alignedOffset - range.Offset;
                if (padding > range.Size || size > range.Size - padding)
                    continue;
                ulong suffixOffset = alignedOffset + size;
                ulong suffixSize = (range.Offset + range.Size) - suffixOffset;
                this._freeRanges.RemoveAt(i);
                if (suffixSize > 0)
                    this._freeRanges.Insert(i, new FreeRange(suffixOffset, suffixSize));
                if (padding > 0)
                    this._freeRanges.Insert(i, new FreeRange(range.Offset, padding));
                this.UsedBytes += size;
                offset = alignedOffset;
                return true;
            }
            offset = 0;
            return false;
        }

        /// <summary>
        /// Returns a previously reserved range to the free-range partition
        /// </summary>
        /// <param name="offset">Offset of the range being released</param>
        /// <param name="size">Size in bytes of the range being released</param>
        public void Release(ulong offset, ulong size)
        {
            int insertionIndex = 0;
            while (insertionIndex < this._freeRanges.Count && this._freeRanges[insertionIndex].Offset < offset)
                insertionIndex++;
            this._freeRanges.Insert(insertionIndex, new FreeRange(offset, size));
            this.UsedBytes -= size;
            for (int i = Math.Max(0, insertionIndex - 1); i + 1 < this._freeRanges.Count;)
            {
                FreeRange current = this._freeRanges[i];
                FreeRange next = this._freeRanges[i + 1];
                if (current.Offset + current.Size != next.Offset)
                {
                    i++;
                    continue;
                }
                this._freeRanges[i] = new FreeRange(current.Offset, current.Size + next.Size);
                this._freeRanges.RemoveAt(i + 1);
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Rounds a byte offset up to the requested alignment
        /// </summary>
        /// <param name="value">Offset to align</param>
        /// <param name="alignment">Alignment in bytes; values of zero and one leave the offset unchanged</param>
        /// <returns>The smallest aligned offset that is greater than or equal to <paramref name="value"/></returns>
        private static ulong AlignUp(ulong value, ulong alignment)
        {
            if (alignment <= 1)
                return value;
            ulong remainder = value % alignment;
            return remainder == 0 ? value : checked(value + alignment - remainder);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Native device-memory allocation represented by this block
        /// </summary>
        public VkDeviceMemory Memory { get; }

        /// <summary>
        /// Vulkan memory-type index used to create <see cref="Memory"/>
        /// </summary>
        public uint MemoryTypeIndex { get; }

        /// <summary>
        /// Memory properties associated with <see cref="Memory"/>
        /// </summary>
        public VkMemoryPropertyFlags Properties { get; }

        /// <summary>
        /// Total size of <see cref="Memory"/> in bytes
        /// </summary>
        public ulong Size { get; }

        /// <summary>
        /// Base address of the persistent host mapping, or <see cref="IntPtr.Zero"/> when no mapping exists
        /// </summary>
        public IntPtr MappedPointer { get; }

        /// <summary>
        /// Number of bytes currently reserved by live suballocations
        /// </summary>
        public ulong UsedBytes { get; private set; }

        /// <summary>
        /// Indicates whether the block has no currently reserved bytes
        /// </summary>
        public bool IsEmpty { get { return this.UsedBytes == 0; } }

        /// <summary>
        /// Size of the largest currently available free range in bytes
        /// </summary>
        public ulong LargestFreeRange
        {
            get
            {
                ulong result = 0;
                for (int i = 0; i < this._freeRanges.Count; i++)
                    result = Math.Max(result, this._freeRanges[i].Size);
                return result;
            }
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Represents an immutable free interval within the block
        /// </summary>
        private readonly struct FreeRange
        {
            /// <summary>
            /// Creates a free interval with the specified offset and size
            /// </summary>
            /// <param name="offset">Starting offset of the interval</param>
            /// <param name="size">Length of the interval in bytes</param>
            public FreeRange(ulong offset, ulong size)
            {
                this.Offset = offset;
                this.Size = size;
            }

            /// <summary>
            /// Starting offset of the free interval
            /// </summary>
            public ulong Offset { get; }

            /// <summary>
            /// Length of the free interval in bytes
            /// </summary>
            public ulong Size { get; }
        }

        #endregion
    }
}
