using System;

namespace RemuxForge.Vulkan.Memory
{
    /// <summary>
    /// Describes the buffer, byte offset and byte range written to a Vulkan descriptor
    /// </summary>
    internal readonly struct VulkanBufferBinding
    {
        /// <summary>
        /// Creates a non-owning binding for a range of a Vulkan buffer
        /// </summary>
        /// <param name="buffer">Buffer whose Vulkan handle is written to the descriptor</param>
        /// <param name="offset">Byte offset written to the descriptor</param>
        /// <param name="range">Number of bytes written to the descriptor; must be non-zero and no greater than the buffer size</param>
        public VulkanBufferBinding(VulkanBuffer buffer, ulong offset, ulong range)
        {
            this.Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            if (range == 0 || range > buffer.Size)
                throw new ArgumentOutOfRangeException(nameof(range));
            this.Offset = offset;
            this.Range = range;
        }

        /// <summary>
        /// Gets the buffer referenced by this binding
        /// </summary>
        public VulkanBuffer Buffer { get; }

        /// <summary>
        /// Gets the byte offset written to the Vulkan descriptor
        /// </summary>
        public ulong Offset { get; }

        /// <summary>
        /// Gets the number of bytes written to the Vulkan descriptor
        /// </summary>
        public ulong Range { get; }

        /// <summary>
        /// Creates a binding that covers the complete visible range of a buffer
        /// </summary>
        /// <param name="buffer">Buffer to bind</param>
        /// <returns>A binding using the buffer's binding offset and full visible size</returns>
        public static VulkanBufferBinding Whole(VulkanBuffer buffer)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return new VulkanBufferBinding(buffer, buffer.BindingOffset, buffer.Size);
        }
    }
}
