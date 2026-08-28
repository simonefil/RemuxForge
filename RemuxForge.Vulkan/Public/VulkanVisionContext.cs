using RemuxForge.Vulkan.Runtime;
using RemuxForge.Vulkan.Vision;
using System;
using System.Collections.Generic;

namespace RemuxForge.Vulkan
{
    /// <summary>
    /// Persistent Vulkan context shared by vision pipelines
    /// </summary>
    public sealed class VulkanVisionContext : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Vulkan runtime owned by this context and shared by its pipelines
        /// </summary>
        private readonly VulkanRuntimeContext _runtime;

        /// <summary>
        /// Synchronizes lifecycle transitions and access to the owned pipeline collection
        /// </summary>
        private readonly object _lifecycleLock;

        /// <summary>
        /// SIFT pipelines created by this context that have not yet been released
        /// </summary>
        private readonly List<VulkanSiftPipeline> _pipelines;

        /// <summary>
        /// Hash pipelines created by this context that have not yet been released
        /// </summary>
        private readonly List<VulkanHashPipeline> _hashPipelines;

        /// <summary>
        /// Indicates whether this context has completed disposal
        /// </summary>
        private bool _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Creates the persistent runtime on the selected device
        /// </summary>
        /// <param name="options">Device selection, memory budget, concurrency, validation, and initial pipeline cache; null uses the default options</param>
        public VulkanVisionContext(VulkanVisionOptions options = null)
        {
            VulkanVisionAbi.Validate();
            this.Options = options ?? new VulkanVisionOptions();
            ValidateOptions(this.Options);
            this._lifecycleLock = new object();
            this._pipelines = new List<VulkanSiftPipeline>();
            this._hashPipelines = new List<VulkanHashPipeline>();
            this._runtime = new VulkanRuntimeContext(this.Options);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Creates and registers a SIFT pipeline owned by this context
        /// </summary>
        /// <returns>A persistent SIFT pipeline owned by this context</returns>
        public VulkanSiftPipeline CreateSiftPipeline()
        {
            lock (this._lifecycleLock)
            {
                this.ThrowIfDisposed();
                VulkanSiftPipeline pipeline = new VulkanSiftPipeline(this, this._runtime);
                this._pipelines.Add(pipeline);
                return pipeline;
            }
        }

        /// <summary>
        /// Creates and registers a perceptual hash pipeline owned by this context
        /// </summary>
        /// <returns>A persistent hash pipeline owned by this context</returns>
        public VulkanHashPipeline CreateHashPipeline()
        {
            lock (this._lifecycleLock)
            {
                this.ThrowIfDisposed();
                VulkanHashPipeline pipeline = new VulkanHashPipeline(this, this._runtime);
                this._hashPipelines.Add(pipeline);
                return pipeline;
            }
        }

        /// <summary>
        /// Exports the pipeline cache compatible with the current device
        /// </summary>
        /// <returns>The identifying header and opaque pipeline-cache payload</returns>
        public byte[] GetPipelineCacheData()
        {
            lock (this._lifecycleLock)
            {
                this.ThrowIfDisposed();
                return this._runtime.GetPipelineCacheData();
            }
        }

        /// <summary>
        /// Disposes all created pipelines and the Vulkan runtime
        /// </summary>
        public void Dispose()
        {
            lock (this._lifecycleLock)
            {
                if (this._disposed)
                    return;
                this._disposed = true;
                for (int i = this._pipelines.Count - 1; i >= 0; i--)
                    this._pipelines[i].DisposeFromContext();
                this._pipelines.Clear();
                for (int i = this._hashPipelines.Count - 1; i >= 0; i--)
                    this._hashPipelines[i].DisposeFromContext();
                this._hashPipelines.Clear();
                this._runtime.Dispose();
            }
        }

        #endregion

        #region Metodi internal

        /// <summary>
        /// Removes a pipeline from this context's ownership tracking
        /// </summary>
        /// <param name="pipeline">Pipeline that has completed its own disposal</param>
        internal void Release(VulkanSiftPipeline pipeline)
        {
            lock (this._lifecycleLock)
                this._pipelines.Remove(pipeline);
        }

        /// <summary>
        /// Removes a hash pipeline from this context ownership tracking
        /// </summary>
        /// <param name="pipeline">Pipeline that has completed its own disposal</param>
        internal void Release(VulkanHashPipeline pipeline)
        {
            lock (this._lifecycleLock)
                this._hashPipelines.Remove(pipeline);
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Validates options that control device selection and workload concurrency
        /// </summary>
        /// <param name="options">Options to validate</param>
        private static void ValidateOptions(VulkanVisionOptions options)
        {
            if (options.DeviceIndex < -1)
                throw new ArgumentOutOfRangeException(nameof(options), options.DeviceIndex, "Device index must be -1 or greater.");
            if (options.MaximumInFlightWorkloads < 1 || options.MaximumInFlightWorkloads > 64)
                throw new ArgumentOutOfRangeException(nameof(options), options.MaximumInFlightWorkloads, "Maximum in-flight workloads must be between 1 and 64.");
        }

        /// <summary>
        /// Rejects use of the instance after disposal
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (this._disposed)
                throw new ObjectDisposedException(nameof(VulkanVisionContext));
        }

        #endregion

        #region Properties

        /// <summary>
        /// Options instance used to initialize this context
        /// </summary>
        public VulkanVisionOptions Options { get; }

        /// <summary>
        /// Capabilities reported by the selected device
        /// </summary>
        public VulkanDeviceCapabilities Capabilities { get { return this._runtime.Capabilities; } }

        #endregion
    }
}
