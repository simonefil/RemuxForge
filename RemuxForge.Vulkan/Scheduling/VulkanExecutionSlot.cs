using System;
using System.Collections.Generic;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Scheduling
{
    /// <summary>
    /// Owns the Vulkan command resources reused by one scheduler submission
    /// </summary>
    internal sealed class VulkanExecutionSlot : IDisposable
    {
        #region Constants

        /// <summary>
        /// Number of timestamp queries reserved for this slot
        /// </summary>
        public const uint TIMESTAMP_QUERY_CAPACITY = 256;

        #endregion

        #region Class fields

        /// <summary>
        /// Vulkan device dispatch used to destroy the slot's owned pools
        /// </summary>
        private readonly VkDeviceApi _deviceApi;

        /// <summary>
        /// Tracks whether the slot's owned Vulkan resources have been released
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates an execution slot with the command and timestamp resources supplied by the scheduler
        /// </summary>
        /// <param name="deviceApi">Vulkan device dispatch associated with the supplied resources</param>
        /// <param name="commandPool">Command pool that owns the command buffer</param>
        /// <param name="commandBuffer">Command buffer recorded for submissions using this slot</param>
        /// <param name="timestampQueryPool">Timestamp query pool, or <c>VkQueryPool.Null</c> when timestamp queries are unavailable</param>
        public VulkanExecutionSlot(VkDeviceApi deviceApi, VkCommandPool commandPool, VkCommandBuffer commandBuffer, VkQueryPool timestampQueryPool)
        {
            this._deviceApi = deviceApi;
            this.CommandPool = commandPool;
            this.CommandBuffer = commandBuffer;
            this.TimestampQueryPool = timestampQueryPool;
            this.TimestampSpans = new List<VulkanGpuTimestampSpan>();
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Releases the Vulkan resources owned by this slot
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;
            this._disposed = true;
            if (this.TimestampQueryPool.IsNotNull)
                this._deviceApi.vkDestroyQueryPool(this.TimestampQueryPool);
            if (this.CommandPool.IsNotNull)
                this._deviceApi.vkDestroyCommandPool(this.CommandPool);
            this.CommandPool = VkCommandPool.Null;
            this.CommandBuffer = VkCommandBuffer.Null;
            this.TimestampQueryPool = VkQueryPool.Null;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Command pool that owns the reusable command buffer
        /// </summary>
        public VkCommandPool CommandPool { get; private set; }

        /// <summary>
        /// Command buffer recorded for the current submission
        /// </summary>
        public VkCommandBuffer CommandBuffer { get; private set; }

        /// <summary>
        /// Timeline semaphore value signaled when the current submission completes
        /// </summary>
        public ulong CompletionValue { get; set; }

        /// <summary>
        /// Timestamp query pool used by this slot, or a null handle when timestamp queries are unavailable
        /// </summary>
        public VkQueryPool TimestampQueryPool { get; private set; }

        /// <summary>
        /// Indicates whether timestamp results from the current submission still require resolution
        /// </summary>
        public bool TimestampPending { get; set; }

        /// <summary>
        /// Index of the next unused timestamp query for the current submission
        /// </summary>
        public uint NextTimestampQuery { get; set; }

        /// <summary>
        /// GPU phase associated with the submission-wide timestamp span
        /// </summary>
        public VulkanGpuPhase SubmissionPhase { get; set; }

        /// <summary>
        /// Timestamp spans recorded for the current submission
        /// </summary>
        public List<VulkanGpuTimestampSpan> TimestampSpans { get; }

        #endregion
    }
}
