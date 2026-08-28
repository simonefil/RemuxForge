using System;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Memory
{
    /// <summary>
    /// Represents an allocated Vulkan buffer or a non-owning view into one
    /// </summary>
    internal sealed unsafe class VulkanBuffer : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Allocator that owns the backing memory block and receives the allocation slice when the owning instance is disposed
        /// </summary>
        private readonly VulkanMemoryAllocator _allocator;

        /// <summary>
        /// Memory block containing the buffer allocation and any persistent host mapping
        /// </summary>
        private readonly VulkanMemoryBlock _block;

        /// <summary>
        /// Vulkan device API used to destroy the owned buffer handle
        /// </summary>
        private readonly VkDeviceApi _deviceApi;

        /// <summary>
        /// Indicates whether this wrapper owns the Vulkan buffer handle and its allocation slice
        /// </summary>
        private readonly bool _ownsBuffer;

        /// <summary>
        /// Indicates whether this wrapper has released its resources
        /// </summary>
        private bool _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Initializes an owning wrapper for a Vulkan buffer bound to an allocator slice
        /// </summary>
        /// <param name="allocator">Allocator that owns the memory block and releases the slice on disposal</param>
        /// <param name="block">Memory block containing the bound buffer allocation</param>
        /// <param name="deviceApi">Vulkan device API associated with the buffer handle</param>
        /// <param name="buffer">Created Vulkan buffer handle bound to the allocation slice</param>
        /// <param name="offset">Absolute byte offset of the allocation slice within the memory block</param>
        /// <param name="size">Logical byte size of the Vulkan buffer</param>
        /// <param name="allocationSize">Byte size reserved for the buffer according to Vulkan memory requirements</param>
        /// <param name="properties">Memory properties selected for the allocation</param>
        internal VulkanBuffer(VulkanMemoryAllocator allocator, VulkanMemoryBlock block, VkDeviceApi deviceApi, VkBuffer buffer, ulong offset, ulong size, ulong allocationSize, VkMemoryPropertyFlags properties)
        {
            this._allocator = allocator;
            this._block = block;
            this._deviceApi = deviceApi;
            this.Buffer = buffer;
            this.Offset = offset;
            this.Size = size;
            this.AllocationSize = allocationSize;
            this.MemoryProperties = properties;
            this.BindingOffset = 0;
            this._ownsBuffer = true;
        }

        /// <summary>
        /// Initializes a non-owning view over a range of another buffer wrapper
        /// </summary>
        /// <param name="owner">Owning wrapper whose Vulkan handle and allocation are shared by the view</param>
        /// <param name="offset">Byte offset of the view relative to the owner's exposed range</param>
        /// <param name="size">Logical byte size of the view</param>
        private VulkanBuffer(VulkanBuffer owner, ulong offset, ulong size)
        {
            this._allocator = owner._allocator;
            this._block = owner._block;
            this._deviceApi = owner._deviceApi;
            this.Buffer = owner.Buffer;
            this.Offset = checked(owner.Offset + offset);
            this.Size = size;
            this.AllocationSize = 0;
            this.MemoryProperties = owner.MemoryProperties;
            this.BindingOffset = checked(owner.BindingOffset + offset);
            this._ownsBuffer = false;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Creates a non-owning view over a validated byte range of this buffer
        /// </summary>
        /// <param name="offset">Byte offset relative to this buffer's exposed range</param>
        /// <param name="size">Byte size of the view</param>
        /// <returns>A view that shares this buffer's Vulkan handle and allocation</returns>
        public VulkanBuffer CreateView(ulong offset, ulong size)
        {
            this.ThrowIfDisposed();
            if (offset > this.Size || size == 0 || size > this.Size - offset)
                throw new ArgumentOutOfRangeException(nameof(size));
            return new VulkanBuffer(this, offset, size);
        }

        /// <summary>
        /// Copies unmanaged values into the buffer's host-visible mapped range
        /// </summary>
        /// <param name="values">Values to copy into the buffer</param>
        /// <param name="destinationOffset">Byte offset relative to this buffer's exposed range</param>
        public void Write<T>(ReadOnlySpan<T> values, ulong destinationOffset = 0) where T : unmanaged
        {
            this.ThrowIfDisposed();
            ulong byteCount = checked((ulong)(values.Length * sizeof(T)));
            if (destinationOffset > this.Size || byteCount > this.Size - destinationOffset)
                throw new ArgumentOutOfRangeException(nameof(destinationOffset));
            if ((this.MemoryProperties & VkMemoryPropertyFlags.HostVisible) == 0)
                throw new InvalidOperationException("The buffer is not host-visible.");
            if (this._block.MappedPointer == IntPtr.Zero)
                throw new InvalidOperationException("The host-visible block does not provide persistent mapping.");
            void* destination = (byte*)this._block.MappedPointer + this.Offset + destinationOffset;
            fixed (T* source = values)
                System.Buffer.MemoryCopy(source, destination, byteCount, byteCount);
        }

        /// <summary>
        /// Reads unmanaged values from the buffer's host-visible mapped range
        /// </summary>
        /// <param name="count">Number of values to read</param>
        /// <param name="sourceOffset">Byte offset relative to this buffer's exposed range</param>
        /// <returns>A newly allocated array containing the requested values</returns>
        public T[] Read<T>(int count, ulong sourceOffset = 0) where T : unmanaged
        {
            this.ThrowIfDisposed();
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));
            ulong byteCount = checked((ulong)(count * sizeof(T)));
            if (sourceOffset > this.Size || byteCount > this.Size - sourceOffset)
                throw new ArgumentOutOfRangeException(nameof(sourceOffset));
            if ((this.MemoryProperties & VkMemoryPropertyFlags.HostVisible) == 0)
                throw new InvalidOperationException("The buffer is not host-visible.");
            T[] result = new T[count];
            if (count == 0)
                return result;
            if (this._block.MappedPointer == IntPtr.Zero)
                throw new InvalidOperationException("The host-visible block does not provide persistent mapping.");
            void* source = (byte*)this._block.MappedPointer + this.Offset + sourceOffset;
            fixed (T* destination = result)
                System.Buffer.MemoryCopy(source, destination, byteCount, byteCount);
            return result;
        }

        /// <summary>
        /// Releases resources owned by this wrapper
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;
            this._disposed = true;
            if (this._ownsBuffer && this.Buffer.IsNotNull)
                this._deviceApi.vkDestroyBuffer(this.Buffer);
            this.Buffer = VkBuffer.Null;
            if (this._ownsBuffer)
                this._allocator.Release(this._block, this.Offset, this.AllocationSize);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Rejects operations attempted after this wrapper has been disposed
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (this._disposed)
                throw new ObjectDisposedException(nameof(VulkanBuffer));
        }

        #endregion

        #region Properties

        /// <summary>
        /// Vulkan buffer handle used by commands and descriptor bindings
        /// </summary>
        public VkBuffer Buffer { get; private set; }

        /// <summary>
        /// Absolute byte offset of this range within the backing device memory block
        /// </summary>
        public ulong Offset { get; }

        /// <summary>
        /// Byte offset of this range within the Vulkan buffer for descriptor and command bindings
        /// </summary>
        public ulong BindingOffset { get; }

        /// <summary>
        /// Logical byte size of the range exposed by this wrapper
        /// </summary>
        public ulong Size { get; }

        /// <summary>
        /// Byte size of the allocator slice reserved for the owning buffer
        /// </summary>
        internal ulong AllocationSize { get; }

        /// <summary>
        /// Memory properties selected for the backing allocation
        /// </summary>
        public VkMemoryPropertyFlags MemoryProperties { get; }

        #endregion
    }
}
