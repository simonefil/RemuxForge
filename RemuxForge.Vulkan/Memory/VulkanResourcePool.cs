using System;
using System.Collections.Generic;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Memory
{
    /// <summary>
    /// Pools Vulkan buffers by size class, usage flags and memory properties
    /// </summary>
    internal sealed class VulkanResourcePool : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Allocator used to create buffers when the cache has no matching resource; ownership remains with the runtime
        /// </summary>
        private readonly VulkanMemoryAllocator _allocator;

        /// <summary>
        /// Lock protecting the cache and the pool lifecycle state
        /// </summary>
        private readonly object _sync;

        /// <summary>
        /// Cached buffers grouped by an immutable key; the pool owns every buffer stored in this collection
        /// </summary>
        private readonly Dictionary<BufferKey, Stack<VulkanBuffer>> _available;

        /// <summary>
        /// Indicates that the pool has released its cached buffers and no longer accepts returned resources
        /// </summary>
        private bool _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Creates an empty buffer pool backed by the specified allocator
        /// </summary>
        /// <param name="allocator">Allocator used for cache misses; this pool does not dispose it</param>
        public VulkanResourcePool(VulkanMemoryAllocator allocator)
        {
            this._allocator = allocator;
            this._sync = new object();
            this._available = new Dictionary<BufferKey, Stack<VulkanBuffer>>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Rents a reusable buffer with at least the requested capacity
        /// </summary>
        /// <param name="minimumSize">Minimum buffer capacity in bytes; zero is normalized to four bytes</param>
        /// <param name="usage">Vulkan usage flags required by the caller</param>
        /// <param name="properties">Memory properties required by the caller</param>
        /// <returns>A lease that owns the rented buffer until it is disposed</returns>
        public VulkanBufferLease Rent(ulong minimumSize, VkBufferUsageFlags usage, VkMemoryPropertyFlags properties)
        {
            if (minimumSize == 0)
                minimumSize = 4;
            ulong sizeClass = ResolveSizeClass(minimumSize);
            BufferKey key = new BufferKey(sizeClass, usage, properties);
            lock (this._sync)
            {
                this.ThrowIfDisposed();
                if (this._available.TryGetValue(key, out Stack<VulkanBuffer> buffers) && buffers.Count > 0)
                    return new VulkanBufferLease(this, key, buffers.Pop());
            }
            VulkanBuffer buffer;
            try
            {
                buffer = this._allocator.AllocateBuffer(sizeClass, usage, properties);
            }
            catch (VulkanResourceExhaustedException)
            {
                this.Trim();
                buffer = this._allocator.AllocateBuffer(sizeClass, usage, properties);
            }
            return new VulkanBufferLease(this, key, buffer);
        }

        /// <summary>
        /// Disposes every buffer currently retained in the cache
        /// </summary>
        public void Trim()
        {
            lock (this._sync)
            {
                foreach (Stack<VulkanBuffer> buffers in this._available.Values)
                {
                    while (buffers.Count > 0)
                        buffers.Pop().Dispose();
                }
                this._available.Clear();
            }
        }

        /// <summary>
        /// Gets the total device-local allocation size retained by the cache
        /// </summary>
        /// <returns>The sum of <c>AllocationSize</c> for cached device-local buffers</returns>
        public ulong GetCachedDeviceBytes()
        {
            lock (this._sync)
            {
                ulong result = 0;
                foreach (Stack<VulkanBuffer> buffers in this._available.Values)
                {
                    foreach (VulkanBuffer buffer in buffers)
                    {
                        if ((buffer.MemoryProperties & VkMemoryPropertyFlags.DeviceLocal) != 0)
                            result = checked(result + buffer.AllocationSize);
                    }
                }
                return result;
            }
        }

        /// <summary>
        /// Disposes the buffers retained by the pool and marks it as unusable
        /// </summary>
        public void Dispose()
        {
            lock (this._sync)
            {
                if (this._disposed)
                    return;
                this.Trim();
                this._disposed = true;
            }
        }

        #endregion

        #region Metodi internal

        /// <summary>
        /// Returns a rented buffer to the cache or disposes it when the pool is already disposed
        /// </summary>
        /// <param name="key">Cache key produced when the buffer was rented</param>
        /// <param name="buffer">Buffer whose ownership is transferred back to the pool</param>
        internal void Return(BufferKey key, VulkanBuffer buffer)
        {
            lock (this._sync)
            {
                if (this._disposed)
                {
                    buffer.Dispose();
                    return;
                }
                if (!this._available.TryGetValue(key, out Stack<VulkanBuffer> buffers))
                {
                    buffers = new Stack<VulkanBuffer>();
                    this._available.Add(key, buffers);
                }
                buffers.Push(buffer);
            }
        }

        /// <summary>
        /// Resolves a requested capacity to the pool's size class
        /// </summary>
        /// <param name="minimumSize">Minimum capacity in bytes</param>
        /// <returns>The smallest power-of-two size class that is at least 4096 bytes and at least <paramref name="minimumSize"/></returns>
        internal static ulong ResolveSizeClass(ulong minimumSize)
        {
            ulong result = 4096;
            while (result < minimumSize)
                result = checked(result * 2);
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Throws when the pool has already been disposed
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (this._disposed)
                throw new ObjectDisposedException(nameof(VulkanResourcePool));
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Identifies a homogeneous buffer class in the resource-pool cache
        /// </summary>
        internal readonly struct BufferKey : IEquatable<BufferKey>
        {
            #region Costruttore

            /// <summary>
            /// Creates an immutable cache key from the buffer requirements
            /// </summary>
            /// <param name="size">Resolved buffer size class in bytes</param>
            /// <param name="usage">Vulkan usage flags required by the buffer</param>
            /// <param name="properties">Memory properties required by the buffer</param>
            public BufferKey(ulong size, VkBufferUsageFlags usage, VkMemoryPropertyFlags properties)
            {
                this.Size = size;
                this.Usage = usage;
                this.Properties = properties;
            }

            #endregion

            #region Metodi pubblici

            /// <summary>
            /// Determines whether this key has the same buffer requirements as another key
            /// </summary>
            /// <param name="other">Key to compare with this key</param>
            /// <returns><c>true</c> when size, usage flags and memory properties are identical</returns>
            public bool Equals(BufferKey other)
            {
                return this.Size == other.Size && this.Usage == other.Usage && this.Properties == other.Properties;
            }

            /// <summary>
            /// Determines whether an object is an equivalent buffer cache key
            /// </summary>
            /// <param name="obj">Object to compare with this key</param>
            /// <returns><c>true</c> when <paramref name="obj"/> is a <see cref="BufferKey"/> with identical requirements</returns>
            public override bool Equals(object obj)
            {
                return obj is BufferKey other && this.Equals(other);
            }

            /// <summary>
            /// Gets a hash code derived from every cache-key component
            /// </summary>
            /// <returns>A hash code suitable for dictionary lookup</returns>
            public override int GetHashCode()
            {
                return HashCode.Combine(this.Size, this.Usage, this.Properties);
            }

            #endregion

            #region Properties

            /// <summary>
            /// Resolved buffer size class in bytes
            /// </summary>
            public ulong Size { get; }

            /// <summary>
            /// Vulkan usage flags that participate in cache identity
            /// </summary>
            public VkBufferUsageFlags Usage { get; }

            /// <summary>
            /// Memory properties that participate in cache identity
            /// </summary>
            public VkMemoryPropertyFlags Properties { get; }

            #endregion
        }

        #endregion
    }

    /// <summary>
    /// Owns one rented buffer and returns it to its resource pool when disposed
    /// </summary>
    internal sealed class VulkanBufferLease : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Owning pool used to return the buffer; cleared after disposal
        /// </summary>
        private VulkanResourcePool _pool;

        /// <summary>
        /// Immutable cache key used to return the buffer to its original size and requirement class
        /// </summary>
        private readonly VulkanResourcePool.BufferKey _key;

        /// <summary>
        /// Indicates whether this lease has already transferred or released its buffer
        /// </summary>
        private bool _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Creates a lease for a buffer rented from the specified pool
        /// </summary>
        /// <param name="pool">Pool that owns the cache and receives the buffer on disposal</param>
        /// <param name="key">Cache key associated with the rented buffer</param>
        /// <param name="buffer">Buffer transferred to this lease until disposal</param>
        internal VulkanBufferLease(VulkanResourcePool pool, VulkanResourcePool.BufferKey key, VulkanBuffer buffer)
        {
            this._pool = pool;
            this._key = key;
            this.Buffer = buffer;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Returns the buffer to its pool and releases this lease
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;
            this._disposed = true;
            VulkanBuffer buffer = this.Buffer;
            this.Buffer = null;
            this._pool.Return(this._key, buffer);
            this._pool = null;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Buffer currently owned by this lease
        /// </summary>
        public VulkanBuffer Buffer { get; private set; }

        #endregion
    }
}
