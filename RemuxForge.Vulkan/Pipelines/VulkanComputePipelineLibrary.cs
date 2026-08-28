using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Runtime;
using System;
using System.Collections.Generic;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Pipelines
{
    /// <summary>
    /// Creates and retains compute pipelines and descriptor sets compatible with the shader manifest
    /// </summary>
    internal sealed unsafe class VulkanComputePipelineLibrary : IDisposable
    {
        #region Instance Fields

        /// <summary>
        /// Runtime context that supplies the Vulkan device, pipeline cache, and shader loader
        /// </summary>
        private readonly VulkanRuntimeContext _runtime;

        /// <summary>
        /// Lock that serializes access to the cache, descriptor pool, and disposal state
        /// </summary>
        private readonly object _sync;

        /// <summary>
        /// Pipelines cached by shader name and the descriptor and push-constant layout values
        /// </summary>
        private readonly Dictionary<PipelineKey, VulkanComputePipeline> _pipelines;

        /// <summary>
        /// Descriptor pool owned by this library; destroying it also releases any sets still allocated from it
        /// </summary>
        private VkDescriptorPool _descriptorPool;

        /// <summary>
        /// Indicates that the library no longer accepts allocations or owns usable Vulkan resources
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates the descriptor pool and initializes the compute-pipeline cache
        /// </summary>
        /// <param name="runtime">Runtime context whose device, pipeline cache, and shader loader are used by the library</param>
        public VulkanComputePipelineLibrary(VulkanRuntimeContext runtime)
        {
            this._runtime = runtime;
            this._sync = new object();
            this._pipelines = new Dictionary<PipelineKey, VulkanComputePipeline>();
            VkDescriptorPoolSize poolSize = new VkDescriptorPoolSize(VkDescriptorType.StorageBuffer, 524288);
            VkDescriptorPoolCreateInfo poolInfo = new VkDescriptorPoolCreateInfo
            {
                flags = VkDescriptorPoolCreateFlags.FreeDescriptorSet,
                maxSets = 65536,
                poolSizeCount = 1,
                pPoolSizes = &poolSize
            };
            runtime.DeviceApi.vkCreateDescriptorPool(&poolInfo, null, out this._descriptorPool).CheckResult();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Gets a cached compute pipeline or creates the pipeline for a new shader layout
        /// </summary>
        /// <param name="shaderName">Shader resource name used to load the compute shader</param>
        /// <param name="bindingCount">Number of storage-buffer bindings in the shader descriptor set</param>
        /// <param name="pushConstantSize">Push-constant range size in bytes, or zero when no range is required</param>
        /// <param name="diagnostics">Diagnostics object updated with the shader hash and cache result</param>
        /// <returns>The pipeline associated with the requested shader and layout ABI</returns>
        public VulkanComputePipeline Get(string shaderName, uint bindingCount, uint pushConstantSize, VulkanVisionDiagnostics diagnostics)
        {
            PipelineKey key = new PipelineKey(shaderName, bindingCount, pushConstantSize);
            lock (this._sync)
            {
                this.ThrowIfDisposed();
                diagnostics.ShaderHashes[shaderName] = this._runtime.ShaderLoader.GetSha256(shaderName, bindingCount, pushConstantSize);
                if (this._pipelines.TryGetValue(key, out VulkanComputePipeline existing))
                {
                    diagnostics.PipelineCacheHitCount++;
                    return existing;
                }
                VulkanComputePipeline created = this.Create(key, diagnostics);
                this._pipelines.Add(key, created);
                diagnostics.PipelineCacheMissCount++;
                return created;
            }
        }

        /// <summary>
        /// Allocates and binds a descriptor set using the whole range of each supplied buffer
        /// </summary>
        /// <param name="pipeline">Pipeline whose descriptor-set layout defines the required binding count</param>
        /// <param name="buffers">Buffers supplied in shader binding order</param>
        /// <returns>A lease that owns the allocated descriptor set until it is disposed</returns>
        public VulkanDescriptorSetLease RentDescriptorSet(VulkanComputePipeline pipeline, IReadOnlyList<VulkanBuffer> buffers)
        {
            List<VulkanBufferBinding> bindings = new List<VulkanBufferBinding>(buffers.Count);
            for (int bufferIndex = 0; bufferIndex < buffers.Count; bufferIndex++)
                bindings.Add(VulkanBufferBinding.Whole(buffers[bufferIndex]));
            return this.RentDescriptorSet(pipeline, bindings);
        }

        /// <summary>
        /// Allocates and binds a descriptor set using explicit buffer ranges
        /// </summary>
        /// <param name="pipeline">Pipeline whose descriptor-set layout defines the required binding count</param>
        /// <param name="bindings">Buffer bindings supplied in shader binding order</param>
        /// <returns>A lease that owns the allocated descriptor set until it is disposed</returns>
        public VulkanDescriptorSetLease RentDescriptorSet(VulkanComputePipeline pipeline, IReadOnlyList<VulkanBufferBinding> bindings)
        {
            if (bindings.Count != pipeline.BindingCount)
                throw new ArgumentException("The buffer count does not match the shader layout.", nameof(bindings));
            lock (this._sync)
            {
                this.ThrowIfDisposed();
                VkDescriptorSetLayout layout = pipeline.DescriptorSetLayout;
                VkDescriptorSetAllocateInfo allocateInfo = new VkDescriptorSetAllocateInfo
                {
                    descriptorPool = this._descriptorPool,
                    descriptorSetCount = 1,
                    pSetLayouts = &layout
                };
                VkDescriptorSet descriptorSet;
                this._runtime.DeviceApi.vkAllocateDescriptorSets(&allocateInfo, &descriptorSet).CheckResult();
                VkDescriptorBufferInfo* bufferInfos = stackalloc VkDescriptorBufferInfo[bindings.Count];
                VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[bindings.Count];
                for (uint i = 0; i < bindings.Count; i++)
                {
                    VulkanBufferBinding binding = bindings[(int)i];
                    bufferInfos[i] = new VkDescriptorBufferInfo { buffer = binding.Buffer.Buffer, offset = binding.Offset, range = binding.Range };
                    writes[i] = new VkWriteDescriptorSet
                    {
                        dstSet = descriptorSet,
                        dstBinding = i,
                        descriptorCount = 1,
                        descriptorType = VkDescriptorType.StorageBuffer,
                        pBufferInfo = &bufferInfos[i]
                    };
                }
                this._runtime.DeviceApi.vkUpdateDescriptorSets((uint)bindings.Count, writes, 0, null);
                return new VulkanDescriptorSetLease(this, descriptorSet);
            }
        }

        /// <summary>
        /// Releases all resources owned by the library
        /// </summary>
        public void Dispose()
        {
            lock (this._sync)
            {
                if (this._disposed)
                    return;
                this._disposed = true;
                foreach (VulkanComputePipeline pipeline in this._pipelines.Values)
                    pipeline.Dispose();
                this._pipelines.Clear();
                if (this._descriptorPool.IsNotNull)
                    this._runtime.DeviceApi.vkDestroyDescriptorPool(this._descriptorPool);
                this._descriptorPool = VkDescriptorPool.Null;
            }
        }

        #endregion

        #region Internal Methods

        /// <summary>
        /// Returns a descriptor set to the pool that allocated it
        /// </summary>
        /// <param name="descriptorSet">Descriptor set to return, or a null handle</param>
        internal void Return(VkDescriptorSet descriptorSet)
        {
            lock (this._sync)
            {
                if (!this._disposed && descriptorSet.IsNotNull)
                    this._runtime.DeviceApi.vkFreeDescriptorSets(this._descriptorPool, descriptorSet);
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Creates a compute pipeline and its descriptor and pipeline layouts for a cache key
        /// </summary>
        /// <param name="key">Shader and layout values that define the pipeline ABI</param>
        /// <param name="diagnostics">Diagnostics object updated with the loaded shader hash</param>
        /// <returns>A pipeline that owns the created Vulkan pipeline and layout handles</returns>
        private VulkanComputePipeline Create(PipelineKey key, VulkanVisionDiagnostics diagnostics)
        {
            VkDescriptorSetLayoutBinding* bindings = stackalloc VkDescriptorSetLayoutBinding[(int)key.BindingCount];
            for (uint i = 0; i < key.BindingCount; i++)
            {
                bindings[i] = new VkDescriptorSetLayoutBinding
                {
                    binding = i,
                    descriptorType = VkDescriptorType.StorageBuffer,
                    descriptorCount = 1,
                    stageFlags = VkShaderStageFlags.Compute
                };
            }
            VkDescriptorSetLayoutCreateInfo descriptorInfo = new VkDescriptorSetLayoutCreateInfo { bindingCount = key.BindingCount, pBindings = bindings };
            this._runtime.DeviceApi.vkCreateDescriptorSetLayout(&descriptorInfo, null, out VkDescriptorSetLayout descriptorSetLayout).CheckResult();
            try
            {
                VkPushConstantRange pushRange = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Compute, offset = 0, size = key.PushConstantSize };
                VkPipelineLayoutCreateInfo layoutInfo = new VkPipelineLayoutCreateInfo
                {
                    setLayoutCount = 1,
                    pSetLayouts = &descriptorSetLayout,
                    pushConstantRangeCount = key.PushConstantSize > 0 ? 1u : 0u,
                    pPushConstantRanges = key.PushConstantSize > 0 ? &pushRange : null
                };
                this._runtime.DeviceApi.vkCreatePipelineLayout(&layoutInfo, null, out VkPipelineLayout pipelineLayout).CheckResult();
                try
                {
                    byte[] code = this._runtime.ShaderLoader.Load(key.ShaderName, key.BindingCount, key.PushConstantSize, out string hash);
                    diagnostics.ShaderHashes[key.ShaderName] = hash;
                    this._runtime.DeviceApi.vkCreateShaderModule(code, null, out VkShaderModule module).CheckResult();
                    try
                    {
                        VkUtf8ReadOnlyString entryPoint = "main"u8;
                        VkPipelineShaderStageCreateInfo stage = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Compute, module = module, pName = entryPoint };
                        VkComputePipelineCreateInfo pipelineInfo = new VkComputePipelineCreateInfo { stage = stage, layout = pipelineLayout };
                        VkPipeline pipeline;
                        this._runtime.DeviceApi.vkCreateComputePipelines(this._runtime.PipelineCache, 1, &pipelineInfo, null, &pipeline).CheckResult();
                        return new VulkanComputePipeline(this._runtime.DeviceApi, key.BindingCount, descriptorSetLayout, pipelineLayout, pipeline);
                    }
                    finally
                    {
                        this._runtime.DeviceApi.vkDestroyShaderModule(module);
                    }
                }
                catch
                {
                    this._runtime.DeviceApi.vkDestroyPipelineLayout(pipelineLayout);
                    throw;
                }
            }
            catch
            {
                this._runtime.DeviceApi.vkDestroyDescriptorSetLayout(descriptorSetLayout);
                throw;
            }
        }

        /// <summary>
        /// Rejects use of the library after disposal
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (this._disposed)
                throw new ObjectDisposedException(nameof(VulkanComputePipelineLibrary));
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Identifies a compute pipeline by shader name and layout ABI values
        /// </summary>
        private readonly struct PipelineKey : IEquatable<PipelineKey>
        {
            #region Constructor

            /// <summary>
            /// Creates a pipeline-cache key from the shader and layout values
            /// </summary>
            /// <param name="shaderName">Shader resource name</param>
            /// <param name="bindingCount">Number of storage-buffer bindings</param>
            /// <param name="pushConstantSize">Push-constant range size in bytes</param>
            public PipelineKey(string shaderName, uint bindingCount, uint pushConstantSize)
            {
                this.ShaderName = shaderName;
                this.BindingCount = bindingCount;
                this.PushConstantSize = pushConstantSize;
            }

            #endregion

            #region Public Methods

            /// <summary>
            /// Determines whether another key has the same shader and layout values
            /// </summary>
            /// <param name="other">Key to compare with this key</param>
            /// <returns><c>true</c> when all key values are equal; otherwise, <c>false</c></returns>
            public bool Equals(PipelineKey other)
            {
                return this.ShaderName == other.ShaderName && this.BindingCount == other.BindingCount && this.PushConstantSize == other.PushConstantSize;
            }

            /// <summary>
            /// Determines whether another object represents the same pipeline-cache key
            /// </summary>
            /// <param name="obj">Object to compare with this key</param>
            /// <returns><c>true</c> when <paramref name="obj"/> is an equal pipeline-cache key; otherwise, <c>false</c></returns>
            public override bool Equals(object obj)
            {
                return obj is PipelineKey other && this.Equals(other);
            }

            /// <summary>
            /// Computes a hash code consistent with all values used by key equality
            /// </summary>
            /// <returns>A hash code for this pipeline-cache key</returns>
            public override int GetHashCode()
            {
                return HashCode.Combine(this.ShaderName, this.BindingCount, this.PushConstantSize);
            }

            #endregion

            #region Properties

            /// <summary>
            /// Shader resource name that identifies the compute program
            /// </summary>
            public string ShaderName { get; }

            /// <summary>
            /// Number of storage-buffer bindings in the descriptor-set layout
            /// </summary>
            public uint BindingCount { get; }

            /// <summary>
            /// Push-constant range size in bytes, with zero meaning that no range is present
            /// </summary>
            public uint PushConstantSize { get; }

            #endregion
        }

        #endregion
    }

    /// <summary>
    /// Owns a Vulkan compute pipeline, pipeline layout, and descriptor-set layout
    /// </summary>
    internal sealed class VulkanComputePipeline : IDisposable
    {
        #region Instance Fields

        /// <summary>
        /// Device API used to destroy the owned Vulkan handles
        /// </summary>
        private readonly VkDeviceApi _deviceApi;

        /// <summary>
        /// Indicates that the owned Vulkan handles have already been released
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a pipeline wrapper and takes ownership of its Vulkan handles
        /// </summary>
        /// <param name="deviceApi">Device API associated with the supplied handles</param>
        /// <param name="bindingCount">Number of storage-buffer bindings in the descriptor-set layout</param>
        /// <param name="descriptorSetLayout">Descriptor-set layout owned by the new instance</param>
        /// <param name="pipelineLayout">Pipeline layout owned by the new instance</param>
        /// <param name="pipeline">Compute pipeline owned by the new instance</param>
        public VulkanComputePipeline(VkDeviceApi deviceApi, uint bindingCount, VkDescriptorSetLayout descriptorSetLayout, VkPipelineLayout pipelineLayout, VkPipeline pipeline)
        {
            this._deviceApi = deviceApi;
            this.BindingCount = bindingCount;
            this.DescriptorSetLayout = descriptorSetLayout;
            this.PipelineLayout = pipelineLayout;
            this.Pipeline = pipeline;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Releases the pipeline and layout handles owned by this instance
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;
            this._disposed = true;
            this._deviceApi.vkDestroyPipeline(this.Pipeline);
            this._deviceApi.vkDestroyPipelineLayout(this.PipelineLayout);
            this._deviceApi.vkDestroyDescriptorSetLayout(this.DescriptorSetLayout);
            this.Pipeline = VkPipeline.Null;
            this.PipelineLayout = VkPipelineLayout.Null;
            this.DescriptorSetLayout = VkDescriptorSetLayout.Null;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Number of storage-buffer bindings in the descriptor-set layout
        /// </summary>
        public uint BindingCount { get; }

        /// <summary>
        /// Descriptor-set layout used by the pipeline, or a null handle after disposal
        /// </summary>
        public VkDescriptorSetLayout DescriptorSetLayout { get; private set; }

        /// <summary>
        /// Pipeline layout used by the compute pipeline, or a null handle after disposal
        /// </summary>
        public VkPipelineLayout PipelineLayout { get; private set; }

        /// <summary>
        /// Compute pipeline handle, or a null handle after disposal
        /// </summary>
        public VkPipeline Pipeline { get; private set; }

        #endregion
    }

    /// <summary>
    /// Returns a descriptor set to the pool that created it
    /// </summary>
    internal sealed class VulkanDescriptorSetLease : IDisposable
    {
        #region Instance Fields

        /// <summary>
        /// Library that owns the descriptor pool, cleared after this lease is disposed
        /// </summary>
        private VulkanComputePipelineLibrary _owner;

        /// <summary>
        /// Indicates that the descriptor set has already been returned or invalidated
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a lease for an allocated descriptor set
        /// </summary>
        /// <param name="owner">Library that owns the descriptor pool</param>
        /// <param name="descriptorSet">Descriptor set allocated from the owner's pool</param>
        public VulkanDescriptorSetLease(VulkanComputePipelineLibrary owner, VkDescriptorSet descriptorSet)
        {
            this._owner = owner;
            this.DescriptorSet = descriptorSet;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Returns the descriptor set to its owning pool
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;
            this._disposed = true;
            this._owner.Return(this.DescriptorSet);
            this.DescriptorSet = VkDescriptorSet.Null;
            this._owner = null;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Descriptor-set handle leased from the owning library, or a null handle after disposal
        /// </summary>
        public VkDescriptorSet DescriptorSet { get; private set; }

        #endregion
    }
}
