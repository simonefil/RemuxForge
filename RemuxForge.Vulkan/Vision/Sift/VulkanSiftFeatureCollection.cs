using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Pipelines;
using RemuxForge.Vulkan.Runtime;
using RemuxForge.Vulkan.Scheduling;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Vision.Sift
{
    /// <summary>
    /// Owns resident keypoints, descriptors, counters and diagnostics for a set of frames
    /// </summary>
    internal sealed unsafe class VulkanSiftFeatureCollection : IDisposable
    {
        #region Instance Fields

        /// <summary>
        /// Retains ownership of every buffer acquired by this collection
        /// </summary>
        private readonly List<VulkanBufferLease> _leases;

        /// <summary>
        /// Stores the metadata element offset for each frame in the host-visible metadata buffer
        /// </summary>
        private readonly int[] _metadataOffsets;

        /// <summary>
        /// Tracks whether the collection has already released its owned leases
        /// </summary>
        private bool _disposed;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a collection from the per-frame SIFT plans
        /// </summary>
        /// <param name="runtime">Runtime context used to acquire Vulkan buffers</param>
        /// <param name="plans">Plans that define frame capacities and octave merge metadata</param>
        private VulkanSiftFeatureCollection(VulkanRuntimeContext runtime, IReadOnlyList<VulkanSiftPlan> plans)
        {
            this._leases = new List<VulkanBufferLease>();
            this._metadataOffsets = new int[plans.Count];
            this.Frames = new List<VulkanSiftFrameFeatures>(plans.Count);
            int totalCapacity = 0;
            int metadataCount = 0;
            for (int i = 0; i < plans.Count; i++)
            {
                int capacity = plans[i].OrientationCapacity;
                this.Frames.Add(new VulkanSiftFrameFeatures(totalCapacity, capacity));
                totalCapacity = checked(totalCapacity + capacity);
                metadataCount = checked(metadataCount + plans[i].Octaves.Count);
            }
            this.TotalCapacity = totalCapacity;
            this.Keypoints = this.Rent(runtime, checked((ulong)Math.Max(1, totalCapacity) * 32UL), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.DeviceLocal);
            this.Descriptors = this.Rent(runtime, checked((ulong)Math.Max(1, totalCapacity) * 32UL * sizeof(uint)), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.DeviceLocal);
            this.Norms = this.Rent(runtime, checked((ulong)Math.Max(1, totalCapacity) * sizeof(uint)), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.DeviceLocal);
            this.Counts = this.Rent(runtime, checked((ulong)Math.Max(1, plans.Count) * sizeof(uint)), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.DeviceLocal);
            this.DiagnosticCounts = this.Rent(runtime, checked((ulong)Math.Max(1, plans.Count) * 16UL), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.DeviceLocal);
            this.Metadata = this.Rent(runtime, checked((ulong)Math.Max(1, metadataCount) * (ulong)Marshal.SizeOf<OctaveMergeRecord>()), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
            List<OctaveMergeRecord> metadata = new List<OctaveMergeRecord>();
            for (int frameIndex = 0; frameIndex < plans.Count; frameIndex++)
            {
                this._metadataOffsets[frameIndex] = metadata.Count;
                VulkanSiftPlan plan = plans[frameIndex];
                for (int octaveIndex = 0; octaveIndex < plan.Octaves.Count; octaveIndex++)
                {
                    VulkanSiftOctavePlan octave = plan.Octaves[octaveIndex];
                    metadata.Add(new OctaveMergeRecord
                    {
                        KeypointOffset = (uint)octave.OrientationOffset,
                        DescriptorOffset = checked((uint)((ulong)octave.OrientationOffset * 32UL)),
                        CounterOffset = checked((uint)octave.CounterOffset),
                        Capacity = checked((uint)(octave.FeatureCapacity * 3))
                    });
                }
            }
            this.Metadata.Write<OctaveMergeRecord>(CollectionsMarshal.AsSpan(metadata));
        }

        /// <summary>
        /// Initializes a collection with caller-provided per-frame capacities
        /// </summary>
        /// <param name="runtime">Runtime context used to acquire Vulkan buffers</param>
        /// <param name="capacities">Maximum feature count reserved for each frame</param>
        private VulkanSiftFeatureCollection(VulkanRuntimeContext runtime, IReadOnlyList<int> capacities)
        {
            this._leases = new List<VulkanBufferLease>();
            this._metadataOffsets = new int[capacities.Count];
            this.Frames = new List<VulkanSiftFrameFeatures>(capacities.Count);
            int totalCapacity = 0;
            for (int i = 0; i < capacities.Count; i++)
            {
                this.Frames.Add(new VulkanSiftFrameFeatures(totalCapacity, capacities[i]));
                totalCapacity = checked(totalCapacity + capacities[i]);
            }
            this.TotalCapacity = totalCapacity;
            this.Keypoints = this.Rent(runtime, checked((ulong)Math.Max(1, totalCapacity) * 32UL), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.DeviceLocal);
            this.Descriptors = this.Rent(runtime, checked((ulong)Math.Max(1, totalCapacity) * 32UL * sizeof(uint)), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.DeviceLocal);
            this.Norms = this.Rent(runtime, checked((ulong)Math.Max(1, totalCapacity) * sizeof(uint)), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.DeviceLocal);
            this.Counts = this.Rent(runtime, checked((ulong)Math.Max(1, capacities.Count) * sizeof(uint)), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.DeviceLocal);
            this.DiagnosticCounts = this.Rent(runtime, checked((ulong)Math.Max(1, capacities.Count) * 16UL), VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst | VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.DeviceLocal);
            this.Metadata = this.Rent(runtime, 16UL, VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Creates a feature collection whose capacities and merge metadata come from SIFT plans
        /// </summary>
        /// <param name="runtime">Runtime context used to acquire Vulkan buffers</param>
        /// <param name="plans">Plans that define the collection's frame layout</param>
        /// <returns>A feature collection owning buffers for all planned frames</returns>
        public static VulkanSiftFeatureCollection Create(VulkanRuntimeContext runtime, IReadOnlyList<VulkanSiftPlan> plans)
        {
            return new VulkanSiftFeatureCollection(runtime, plans);
        }

        /// <summary>
        /// Selects at most the requested number of highest-ranked features per frame on the GPU
        /// </summary>
        /// <param name="runtime">Runtime context used to submit the selection workload</param>
        /// <param name="maximumFeaturesPerFrame">Upper bound for the retained feature count of each frame</param>
        /// <param name="diagnostics">Diagnostic accumulator updated by the recorded dispatches</param>
        /// <param name="cancellationToken">Token used while waiting for GPU completion</param>
        /// <returns>A new collection containing the selected features and their diagnostics</returns>
        public VulkanSiftFeatureCollection Limit(VulkanRuntimeContext runtime, int maximumFeaturesPerFrame, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            if (maximumFeaturesPerFrame < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumFeaturesPerFrame));
            int[] capacities = new int[this.Frames.Count];
            SelectionFrameRecord[] metadata = new SelectionFrameRecord[this.Frames.Count];
            for (int i = 0; i < this.Frames.Count; i++)
                capacities[i] = Math.Min(this.Frames[i].Capacity, maximumFeaturesPerFrame);
            VulkanSiftFeatureCollection result = new VulkanSiftFeatureCollection(runtime, capacities);
            try
            {
                for (int i = 0; i < this.Frames.Count; i++)
                {
                    metadata[i] = new SelectionFrameRecord
                    {
                        SourceOffset = checked((uint)this.Frames[i].CapacityOffset),
                        SourceCapacity = checked((uint)this.Frames[i].Capacity),
                        TargetOffset = checked((uint)result.Frames[i].CapacityOffset),
                        TargetCapacity = checked((uint)result.Frames[i].Capacity)
                    };
                }
                ulong metadataBytes = checked((ulong)Math.Max(1, metadata.Length) * (ulong)Marshal.SizeOf<SelectionFrameRecord>());
                ulong stateBytes = checked((ulong)Math.Max(1, metadata.Length) * (ulong)Marshal.SizeOf<SelectionStateRecord>());
                int selectionElements = Math.Max(1, this.TotalCapacity);
                int scanScratchElements = ResolveSelectionScanScratchElements(selectionElements);
                int histogramElements = checked(Math.Max(1, this.Frames.Count) * 256);
                using (VulkanBufferLease metadataLease = runtime.ResourcePool.Rent(metadataBytes, VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent))
                using (VulkanBufferLease stateLease = runtime.ResourcePool.Rent(stateBytes, VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal))
                using (VulkanBufferLease flagsLease = runtime.ResourcePool.Rent(checked((ulong)selectionElements * sizeof(uint)), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal))
                using (VulkanBufferLease prefixLease = runtime.ResourcePool.Rent(checked((ulong)selectionElements * sizeof(uint)), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal))
                using (VulkanBufferLease scanScratchLease = runtime.ResourcePool.Rent(checked((ulong)scanScratchElements * sizeof(uint)), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal))
                using (VulkanBufferLease histogramLease = runtime.ResourcePool.Rent(checked((ulong)histogramElements * sizeof(uint)), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.DeviceLocal))
                {
                    metadataLease.Buffer.Write<SelectionFrameRecord>(metadata);
                    VulkanComputePipeline pipeline = runtime.PipelineLibrary.Get("SiftSelectTopFeatures", 15, 16, diagnostics);
                    VulkanComputePipeline scanPipeline = runtime.PipelineLibrary.Get("SiftPrefixScan", 3, 36, diagnostics);
                    VulkanBuffer[] buffers = new VulkanBuffer[]
                    {
                        this.Keypoints,
                        this.Descriptors,
                        this.Counts,
                        this.DiagnosticCounts,
                        result.Keypoints,
                        result.Descriptors,
                        result.Counts,
                        result.DiagnosticCounts,
                        metadataLease.Buffer,
                        stateLease.Buffer,
                        this.Norms,
                        result.Norms,
                        flagsLease.Buffer,
                        prefixLease.Buffer,
                        histogramLease.Buffer
                    };
                    using (VulkanDescriptorSetLease descriptorSet = runtime.PipelineLibrary.RentDescriptorSet(pipeline, buffers))
                    using (VulkanDescriptorSetLease scanPrimarySet = runtime.PipelineLibrary.RentDescriptorSet(scanPipeline, new VulkanBuffer[] { flagsLease.Buffer, prefixLease.Buffer, scanScratchLease.Buffer }))
                    using (VulkanDescriptorSetLease scanScratchSet = runtime.PipelineLibrary.RentDescriptorSet(scanPipeline, new VulkanBuffer[] { scanScratchLease.Buffer, scanScratchLease.Buffer, scanScratchLease.Buffer }))
                    using (VulkanSubmission submission = runtime.Scheduler.Execute(commandBuffer =>
                    {
                        VkMemoryBarrier barrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.ShaderWrite, dstAccessMask = VkAccessFlags.ShaderRead };
                        runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkDependencyFlags.None, 1, &barrier, 0, null, 0, null);
                        runtime.DeviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, pipeline.Pipeline);
                        VkDescriptorSet set = descriptorSet.DescriptorSet;
                        runtime.DeviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, pipeline.PipelineLayout, 0, set);
                        uint frameCount = checked((uint)this.Frames.Count);
                        if (frameCount > 0)
                        {
                            uint maximumCapacity = 1;
                            for (int i = 0; i < this.Frames.Count; i++)
                                maximumCapacity = Math.Max(maximumCapacity, checked((uint)this.Frames[i].Capacity));
                            uint workgroupsPerFrame = DivideRoundUp(maximumCapacity, 256);
                            uint frameWorkgroups = DivideRoundUp(frameCount, 256);
                            uint candidateWorkgroups = checked(frameCount * workgroupsPerFrame);
                            SelectionPush push = new SelectionPush { FrameCount = frameCount, WorkgroupsPerFrame = workgroupsPerFrame };
                            this.RecordSelectionDispatch(runtime, commandBuffer, pipeline, push, 0, frameWorkgroups, diagnostics);
                            for (int shift = 24; shift >= 0; shift -= 8)
                            {
                                push.Shift = checked((uint)shift);
                                this.RecordSelectionDispatch(runtime, commandBuffer, pipeline, push, 13, frameCount, diagnostics);
                                this.RecordSelectionDispatch(runtime, commandBuffer, pipeline, push, 14, candidateWorkgroups, diagnostics);
                                this.RecordSelectionDispatch(runtime, commandBuffer, pipeline, push, 15, frameWorkgroups, diagnostics);
                            }
                            this.RecordSelectionDispatch(runtime, commandBuffer, pipeline, push, 16, frameWorkgroups, diagnostics);
                            for (int shift = 24; shift >= 0; shift -= 8)
                            {
                                push.Shift = checked((uint)shift);
                                this.RecordSelectionDispatch(runtime, commandBuffer, pipeline, push, 13, frameCount, diagnostics);
                                this.RecordSelectionDispatch(runtime, commandBuffer, pipeline, push, 17, candidateWorkgroups, diagnostics);
                                this.RecordSelectionDispatch(runtime, commandBuffer, pipeline, push, 18, frameWorkgroups, diagnostics);
                            }
                            this.RecordSelectionDispatch(runtime, commandBuffer, pipeline, push, 7, candidateWorkgroups, diagnostics);
                            this.RecordSelectionScan(runtime, commandBuffer, scanPipeline, scanPrimarySet, scanScratchSet, selectionElements, diagnostics);
                            runtime.DeviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, pipeline.Pipeline);
                            runtime.DeviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, pipeline.PipelineLayout, 0, set);
                            this.RecordSelectionDispatch(runtime, commandBuffer, pipeline, push, 9, candidateWorkgroups, diagnostics);
                            this.RecordSelectionDispatch(runtime, commandBuffer, pipeline, push, 8, frameWorkgroups, diagnostics);
                        }
                    }, diagnostics, VulkanGpuPhase.Descriptor, cancellationToken))
                        submission.Wait(diagnostics, cancellationToken);
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Copies the diagnostically accepted feature ranges into a compact collection
        /// </summary>
        /// <param name="runtime">Runtime context used to submit the copy workload</param>
        /// <param name="records">Per-frame diagnostic records containing the accepted descriptor counts</param>
        /// <param name="diagnostics">Diagnostic accumulator updated by the submitted workload</param>
        /// <param name="cancellationToken">Token used while waiting for GPU completion</param>
        /// <returns>A new collection sized to the descriptor count of each input frame</returns>
        public VulkanSiftFeatureCollection Compact(VulkanRuntimeContext runtime, FrameDiagnosticRecord[] records, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            if (records == null)
                throw new ArgumentNullException(nameof(records));
            if (records.Length != this.Frames.Count)
                throw new ArgumentException("Diagnostic counts are incompatible with the SIFT collection.", nameof(records));
            int[] capacities = new int[records.Length];
            for (int i = 0; i < records.Length; i++)
                capacities[i] = checked((int)records[i].DescriptorCount);
            VulkanSiftFeatureCollection result = new VulkanSiftFeatureCollection(runtime, capacities);
            try
            {
                uint[] countValues = new uint[records.Length];
                for (int i = 0; i < records.Length; i++)
                    countValues[i] = records[i].DescriptorCount;
                ulong countBytes = checked((ulong)Math.Max(1, records.Length) * sizeof(uint));
                ulong diagnosticBytes = checked((ulong)Math.Max(1, records.Length) * 16UL);
                using (VulkanBufferLease countUpload = runtime.ResourcePool.Rent(countBytes, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent))
                using (VulkanBufferLease diagnosticUpload = runtime.ResourcePool.Rent(diagnosticBytes, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent))
                using (VulkanSubmission submission = runtime.Scheduler.Execute(commandBuffer =>
                {
                    countUpload.Buffer.Write<uint>(countValues);
                    diagnosticUpload.Buffer.Write<FrameDiagnosticRecord>(records);
                    VkMemoryBarrier barrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.ShaderWrite, dstAccessMask = VkAccessFlags.TransferRead };
                    runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.Transfer, VkDependencyFlags.None, 1, &barrier, 0, null, 0, null);
                    for (int i = 0; i < records.Length; i++)
                    {
                        ulong count = records[i].DescriptorCount;
                        if (count == 0)
                            continue;
                        VkBufferCopy keypointCopy = new VkBufferCopy
                        {
                            srcOffset = checked((ulong)this.Frames[i].CapacityOffset * 32UL),
                            dstOffset = checked((ulong)result.Frames[i].CapacityOffset * 32UL),
                            size = checked(count * 32UL)
                        };
                        runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, this.Keypoints.Buffer, result.Keypoints.Buffer, 1, &keypointCopy);
                        VkBufferCopy descriptorCopy = new VkBufferCopy
                        {
                            srcOffset = checked((ulong)this.Frames[i].CapacityOffset * 32UL * sizeof(uint)),
                            dstOffset = checked((ulong)result.Frames[i].CapacityOffset * 32UL * sizeof(uint)),
                            size = checked(count * 32UL * sizeof(uint))
                        };
                        runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, this.Descriptors.Buffer, result.Descriptors.Buffer, 1, &descriptorCopy);
                        VkBufferCopy normCopy = new VkBufferCopy
                        {
                            srcOffset = checked((ulong)this.Frames[i].CapacityOffset * sizeof(uint)),
                            dstOffset = checked((ulong)result.Frames[i].CapacityOffset * sizeof(uint)),
                            size = checked(count * sizeof(uint))
                        };
                        runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, this.Norms.Buffer, result.Norms.Buffer, 1, &normCopy);
                    }
                    if (records.Length > 0)
                    {
                        VkBufferCopy countCopy = new VkBufferCopy { size = checked((ulong)records.Length * sizeof(uint)) };
                        runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, countUpload.Buffer.Buffer, result.Counts.Buffer, 1, &countCopy);
                        VkBufferCopy diagnosticCopy = new VkBufferCopy { size = checked((ulong)records.Length * 16UL) };
                        runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, diagnosticUpload.Buffer.Buffer, result.DiagnosticCounts.Buffer, 1, &diagnosticCopy);
                    }
                    VkMemoryBarrier complete = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.TransferWrite, dstAccessMask = VkAccessFlags.ShaderRead };
                    runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, VkDependencyFlags.None, 1, &complete, 0, null, 0, null);
                }, diagnostics, VulkanGpuPhase.Upload, cancellationToken))
                    submission.Wait(diagnostics, cancellationToken);
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Records a merge dispatch for a frame into an existing command buffer
        /// </summary>
        /// <param name="runtime">Runtime context used to obtain the pipeline and record commands</param>
        /// <param name="commandBuffer">Command buffer receiving the merge commands</param>
        /// <param name="workspace">Workspace containing the oriented features to merge</param>
        /// <param name="frameIndex">Destination frame index in this collection</param>
        /// <param name="diagnostics">Diagnostic accumulator updated by the dispatch</param>
        /// <param name="leases">Descriptor-set leases retained until the surrounding submission completes</param>
        public void RecordMerge(VulkanRuntimeContext runtime, VkCommandBuffer commandBuffer, VulkanSiftWorkspace workspace, int frameIndex, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases)
        {
            VulkanComputePipeline pipeline = runtime.PipelineLibrary.Get("SiftMergeFeatures", 9, 60, diagnostics);
            VulkanSiftFrameFeatures frame = this.Frames[frameIndex];
            VulkanDescriptorSetLease descriptorSet = runtime.PipelineLibrary.RentDescriptorSet(pipeline, new VulkanBuffer[] { workspace.OrientedKeypoints, workspace.Descriptors, workspace.Counters, this.Metadata, this.Keypoints, this.Descriptors, this.Counts, this.DiagnosticCounts, this.Norms });
            leases.Add(descriptorSet);
            VkMemoryBarrier barrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.ShaderWrite, dstAccessMask = VkAccessFlags.ShaderRead };
            runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkDependencyFlags.None, 1, &barrier, 0, null, 0, null);
            runtime.DeviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, pipeline.Pipeline);
            VkDescriptorSet set = descriptorSet.DescriptorSet;
            runtime.DeviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, pipeline.PipelineLayout, 0, set);
            MergePush push = new MergePush
            {
                MetadataOffset = (uint)this._metadataOffsets[frameIndex],
                OctaveCount = (uint)workspace.Plan.Octaves.Count,
                SourceCapacity = (uint)workspace.Plan.OrientationCapacity,
                TargetKeypointOffset = (uint)frame.CapacityOffset,
                TargetDescriptorOffset = checked((uint)((ulong)frame.CapacityOffset * 32UL)),
                TargetCounterOffset = (uint)frameIndex,
                FrameIndex = (uint)frameIndex
            };
            runtime.DeviceApi.vkCmdPushConstants(commandBuffer, pipeline.PipelineLayout, VkShaderStageFlags.Compute, 0, 60, &push);
            runtime.DeviceApi.vkCmdDispatch(commandBuffer, DivideRoundUp((uint)Math.Max(1, workspace.Plan.OrientationCapacity), 256), 1, 1);
            diagnostics.DispatchCount++;
        }

        /// <summary>
        /// Records one packed merge dispatch for a contiguous range of frames
        /// </summary>
        /// <param name="runtime">Runtime context used to obtain the pipeline and record commands</param>
        /// <param name="commandBuffer">Command buffer receiving the merge commands</param>
        /// <param name="workspaces">Packed frame workspaces sharing one packed allocation</param>
        /// <param name="frameStart">First destination frame index in this collection</param>
        /// <param name="diagnostics">Diagnostic accumulator updated by the dispatch</param>
        /// <param name="leases">Descriptor-set leases retained until the surrounding submission completes</param>
        public void RecordPackedMerge(VulkanRuntimeContext runtime, VkCommandBuffer commandBuffer, IReadOnlyList<VulkanSiftWorkspace> workspaces, int frameStart, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases)
        {
            VulkanSiftWorkspace first = workspaces[0];
            VulkanSiftPackedWorkspace packed = first.PackedWorkspace;
            VulkanComputePipeline pipeline = runtime.PipelineLibrary.Get("SiftMergeFeatures", 9, 60, diagnostics);
            VulkanSiftFrameFeatures firstFrame = this.Frames[frameStart];
            VulkanDescriptorSetLease descriptorSet = runtime.PipelineLibrary.RentDescriptorSet(pipeline, new VulkanBuffer[] { packed.OrientedKeypoints, packed.Descriptors, packed.Counters, this.Metadata, this.Keypoints, this.Descriptors, this.Counts, this.DiagnosticCounts, this.Norms });
            leases.Add(descriptorSet);
            VkMemoryBarrier barrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.ShaderWrite, dstAccessMask = VkAccessFlags.ShaderRead };
            runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkDependencyFlags.None, 1, &barrier, 0, null, 0, null);
            runtime.DeviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, pipeline.Pipeline);
            VkDescriptorSet set = descriptorSet.DescriptorSet;
            runtime.DeviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, pipeline.PipelineLayout, 0, set);
            MergePush push = new MergePush
            {
                MetadataOffset = (uint)this._metadataOffsets[frameStart],
                OctaveCount = (uint)first.Plan.Octaves.Count,
                SourceCapacity = (uint)first.Plan.OrientationCapacity,
                TargetKeypointOffset = (uint)firstFrame.CapacityOffset,
                TargetDescriptorOffset = checked((uint)((ulong)firstFrame.CapacityOffset * 32UL)),
                TargetCounterOffset = (uint)frameStart,
                FrameIndex = (uint)frameStart,
                SourceKeypointFrameStride = this.GetFrameStride(workspaces, item => item.OrientedKeypoints, 32),
                SourceDescriptorFrameStride = this.GetFrameStride(workspaces, item => item.Descriptors, sizeof(uint)),
                SourceCounterFrameStride = this.GetFrameStride(workspaces, item => item.Counters, sizeof(uint)),
                MetadataFrameStride = (uint)first.Plan.Octaves.Count,
                TargetKeypointFrameStride = (uint)firstFrame.Capacity,
                TargetDescriptorFrameStride = checked((uint)firstFrame.Capacity * 32u),
                TargetCounterFrameStride = 1,
                FrameCount = checked((uint)workspaces.Count)
            };
            runtime.DeviceApi.vkCmdPushConstants(commandBuffer, pipeline.PipelineLayout, VkShaderStageFlags.Compute, 0, 60, &push);
            runtime.DeviceApi.vkCmdDispatch(commandBuffer, DivideRoundUp((uint)Math.Max(1, first.Plan.OrientationCapacity), 256), 1, checked((uint)workspaces.Count));
            diagnostics.DispatchCount++;
        }

        /// <summary>
        /// Reads one diagnostic record per frame after synchronizing the GPU copy
        /// </summary>
        /// <param name="runtime">Runtime context used to submit the readback</param>
        /// <param name="diagnostics">Diagnostic accumulator updated with readback timing and byte count</param>
        /// <param name="cancellationToken">Token used while waiting for GPU completion</param>
        /// <returns>The diagnostic record for each frame, in collection order</returns>
        public FrameDiagnosticRecord[] ReadDiagnostics(VulkanRuntimeContext runtime, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            long readbackStart = System.Diagnostics.Stopwatch.GetTimestamp();
            ulong byteCount = checked((ulong)this.Frames.Count * 16UL);
            if (byteCount == 0)
                return Array.Empty<FrameDiagnosticRecord>();
            using (VulkanBufferLease readback = runtime.ResourcePool.Rent(byteCount, VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent))
            using (VulkanSubmission submission = runtime.Scheduler.Execute(commandBuffer =>
            {
                VkMemoryBarrier barrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.ShaderWrite, dstAccessMask = VkAccessFlags.TransferRead };
                runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.Transfer, VkDependencyFlags.None, 1, &barrier, 0, null, 0, null);
                VkBufferCopy copy = new VkBufferCopy { size = byteCount };
                runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, this.DiagnosticCounts.Buffer, readback.Buffer.Buffer, 1, &copy);
            }, diagnostics, VulkanGpuPhase.Readback, cancellationToken))
            {
                submission.Wait(diagnostics, cancellationToken);
                diagnostics.ReadbackBytes += byteCount;
                FrameDiagnosticRecord[] result = readback.Buffer.Read<FrameDiagnosticRecord>(this.Frames.Count);
                diagnostics.ReadbackTicks += System.Diagnostics.Stopwatch.GetTimestamp() - readbackStart;
                return result;
            }
        }

        /// <summary>
        /// Releases all resources owned by this collection
        /// </summary>
        public void Dispose()
        {
            if (this._disposed)
                return;
            this._disposed = true;
            for (int i = this._leases.Count - 1; i >= 0; i--)
                this._leases[i].Dispose();
            this._leases.Clear();
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Acquires a reusable buffer and records its lease as collection-owned state
        /// </summary>
        /// <param name="runtime">Runtime context that owns the resource pool</param>
        /// <param name="size">Minimum number of bytes required by the buffer</param>
        /// <param name="usage">Vulkan usage flags required by the buffer consumers</param>
        /// <param name="properties">Memory properties required by the buffer consumers</param>
        /// <returns>The buffer associated with the retained lease</returns>
        private VulkanBuffer Rent(VulkanRuntimeContext runtime, ulong size, VkBufferUsageFlags usage, VkMemoryPropertyFlags properties)
        {
            VulkanBufferLease lease = runtime.ResourcePool.Rent(size, usage, properties);
            this._leases.Add(lease);
            return lease.Buffer;
        }

        /// <summary>
        /// Calculates the element stride between the first two packed workspace views
        /// </summary>
        /// <param name="workspaces">Packed workspaces whose selected buffers share one allocation</param>
        /// <param name="selector">Selects the buffer whose frame stride is required</param>
        /// <param name="elementSize">Size in bytes of one logical element in the selected buffer</param>
        /// <returns>The stride in logical elements, or zero when fewer than two workspaces exist</returns>
        private uint GetFrameStride(IReadOnlyList<VulkanSiftWorkspace> workspaces, Func<VulkanSiftWorkspace, VulkanBuffer> selector, int elementSize)
        {
            if (workspaces.Count < 2)
                return 0u;
            ulong first = selector(workspaces[0]).BindingOffset;
            ulong second = selector(workspaces[1]).BindingOffset;
            return checked((uint)((second - first) / (ulong)elementSize));
        }

        /// <summary>
        /// Records one selection phase, its dispatch and the barrier required by the next phase
        /// </summary>
        /// <param name="runtime">Runtime context used to record Vulkan commands</param>
        /// <param name="commandBuffer">Command buffer receiving the phase</param>
        /// <param name="pipeline">Selection pipeline bound by the phase</param>
        /// <param name="push">Push constants whose phase field is updated in place</param>
        /// <param name="phase">Shader phase identifier</param>
        /// <param name="workgroupCount">Number of workgroups to dispatch</param>
        /// <param name="diagnostics">Diagnostic accumulator updated for the dispatch</param>
        private void RecordSelectionDispatch(VulkanRuntimeContext runtime, VkCommandBuffer commandBuffer, VulkanComputePipeline pipeline, SelectionPush push, uint phase, uint workgroupCount, VulkanVisionDiagnostics diagnostics)
        {
            push.Phase = phase;
            runtime.DeviceApi.vkCmdPushConstants(commandBuffer, pipeline.PipelineLayout, VkShaderStageFlags.Compute, 0, 16, &push);
            runtime.DeviceApi.vkCmdDispatch(commandBuffer, workgroupCount, 1, 1);
            VkMemoryBarrier barrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.ShaderWrite, dstAccessMask = VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite };
            runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkDependencyFlags.None, 1, &barrier, 0, null, 0, null);
            diagnostics.DispatchCount++;
        }

        /// <summary>
        /// Records the hierarchical prefix scan used by feature selection
        /// </summary>
        /// <param name="runtime">Runtime context used to record Vulkan commands</param>
        /// <param name="commandBuffer">Command buffer receiving the scan</param>
        /// <param name="pipeline">Prefix-scan pipeline</param>
        /// <param name="primarySet">Descriptor set for the first scan level</param>
        /// <param name="scratchSet">Descriptor set reused by higher scan levels</param>
        /// <param name="elementCount">Number of selection flags to scan</param>
        /// <param name="diagnostics">Diagnostic accumulator updated for each scan dispatch</param>
        private void RecordSelectionScan(VulkanRuntimeContext runtime, VkCommandBuffer commandBuffer, VulkanComputePipeline pipeline, VulkanDescriptorSetLease primarySet, VulkanDescriptorSetLease scratchSet, int elementCount, VulkanVisionDiagnostics diagnostics)
        {
            List<SelectionScanLevel> levels = new List<SelectionScanLevel>();
            int scratchCursor = 0;
            int count = elementCount;
            int inputOffset = 0;
            int outputOffset = 0;
            VulkanDescriptorSetLease set = primarySet;
            while (true)
            {
                int blocks = (count + 255) / 256;
                int sumsOffset = scratchCursor;
                scratchCursor += blocks;
                PrefixPush push = new PrefixPush { InputOffset = (uint)inputOffset, OutputOffset = (uint)outputOffset, BlockOffset = (uint)sumsOffset, ElementCount = (uint)count };
                this.RecordSelectionScanDispatch(runtime, commandBuffer, pipeline, set, push, (uint)blocks, diagnostics);
                levels.Add(new SelectionScanLevel(set, outputOffset, count));
                if (blocks <= 1)
                    break;
                inputOffset = sumsOffset;
                outputOffset = scratchCursor;
                scratchCursor += blocks;
                count = blocks;
                set = scratchSet;
            }
            for (int levelIndex = levels.Count - 2; levelIndex >= 0; levelIndex--)
            {
                SelectionScanLevel level = levels[levelIndex];
                SelectionScanLevel parent = levels[levelIndex + 1];
                PrefixPush add = new PrefixPush { OutputOffset = (uint)level.OutputOffset, BlockOffset = (uint)parent.OutputOffset, ElementCount = (uint)level.ElementCount, Operation = 2 };
                this.RecordSelectionScanDispatch(runtime, commandBuffer, pipeline, level.Set, add, (uint)((level.ElementCount + 255) / 256), diagnostics);
            }
        }

        /// <summary>
        /// Records one prefix-scan dispatch and the barrier for the following scan phase
        /// </summary>
        /// <param name="runtime">Runtime context used to record Vulkan commands</param>
        /// <param name="commandBuffer">Command buffer receiving the dispatch</param>
        /// <param name="pipeline">Prefix-scan pipeline</param>
        /// <param name="set">Descriptor set for the current scan level</param>
        /// <param name="push">Push constants describing the current scan operation</param>
        /// <param name="workgroupCount">Number of workgroups to dispatch</param>
        /// <param name="diagnostics">Diagnostic accumulator updated for the dispatch</param>
        private void RecordSelectionScanDispatch(VulkanRuntimeContext runtime, VkCommandBuffer commandBuffer, VulkanComputePipeline pipeline, VulkanDescriptorSetLease set, PrefixPush push, uint workgroupCount, VulkanVisionDiagnostics diagnostics)
        {
            runtime.DeviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, pipeline.Pipeline);
            VkDescriptorSet descriptorSet = set.DescriptorSet;
            runtime.DeviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, pipeline.PipelineLayout, 0, descriptorSet);
            runtime.DeviceApi.vkCmdPushConstants(commandBuffer, pipeline.PipelineLayout, VkShaderStageFlags.Compute, 0, 36, &push);
            runtime.DeviceApi.vkCmdDispatch(commandBuffer, workgroupCount, 1, 1);
            VkMemoryBarrier barrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.ShaderWrite, dstAccessMask = VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite };
            runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkDependencyFlags.None, 1, &barrier, 0, null, 0, null);
            diagnostics.DispatchCount++;
        }

        /// <summary>
        /// Calculates the number of uint elements required by all prefix-scan levels
        /// </summary>
        /// <param name="elementCount">Number of input elements to scan</param>
        /// <returns>At least one scratch element, or the total storage required by the hierarchy</returns>
        private static int ResolveSelectionScanScratchElements(int elementCount)
        {
            int scratchCursor = 0;
            int count = elementCount;
            while (true)
            {
                int blocks = (count + 255) / 256;
                scratchCursor = checked(scratchCursor + blocks);
                if (blocks <= 1)
                    return Math.Max(1, scratchCursor);
                scratchCursor = checked(scratchCursor + blocks);
                count = blocks;
            }
        }

        /// <summary>
        /// Divides a value by a divisor and rounds the result upward
        /// </summary>
        /// <param name="value">Value to divide</param>
        /// <param name="divisor">Positive divisor</param>
        /// <returns>The smallest integer greater than or equal to <paramref name="value"/> divided by <paramref name="divisor"/></returns>
        private static uint DivideRoundUp(uint value, uint divisor)
        {
            return checked((value + divisor - 1) / divisor);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Describes the resident feature slice assigned to each frame
        /// </summary>
        public List<VulkanSiftFrameFeatures> Frames { get; }

        /// <summary>
        /// Gets the total number of feature records reserved across all frames
        /// </summary>
        public int TotalCapacity { get; }

        /// <summary>
        /// Gets the resident keypoint buffer shared by all frame slices
        /// </summary>
        public VulkanBuffer Keypoints { get; }

        /// <summary>
        /// Gets the resident descriptor buffer shared by all frame slices
        /// </summary>
        public VulkanBuffer Descriptors { get; }

        /// <summary>
        /// Gets the resident norm buffer shared by all frame slices
        /// </summary>
        public VulkanBuffer Norms { get; }

        /// <summary>
        /// Gets the resident descriptor-count buffer indexed by frame
        /// </summary>
        public VulkanBuffer Counts { get; }

        /// <summary>
        /// Gets the resident diagnostic-count buffer indexed by frame
        /// </summary>
        public VulkanBuffer DiagnosticCounts { get; }

        /// <summary>
        /// Gets the host-visible octave merge metadata buffer
        /// </summary>
        public VulkanBuffer Metadata { get; }

        #endregion

        #region Nested Types

        /// <summary>
        /// Describes the source ranges that one octave contributes to the merge shader
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct OctaveMergeRecord
        {
            /// <summary>
            /// Element offset of the octave's oriented keypoints
            /// </summary>
            public uint KeypointOffset;

            /// <summary>
            /// Element offset of the octave's descriptors in uint units
            /// </summary>
            public uint DescriptorOffset;

            /// <summary>
            /// Counter offset associated with the octave
            /// </summary>
            public uint CounterOffset;

            /// <summary>
            /// Maximum number of oriented keypoints contributed by the octave
            /// </summary>
            public uint Capacity;
        }

        /// <summary>
        /// Defines the push constants used by the feature merge shader
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct MergePush
        {
            /// <summary>
            /// Element offset of the first metadata record for the destination frame
            /// </summary>
            public uint MetadataOffset;

            /// <summary>
            /// Number of octave metadata records to process
            /// </summary>
            public uint OctaveCount;

            /// <summary>
            /// Maximum number of oriented source features in the workspace
            /// </summary>
            public uint SourceCapacity;

            /// <summary>
            /// Element offset of the destination keypoint range
            /// </summary>
            public uint TargetKeypointOffset;

            /// <summary>
            /// Element offset of the destination descriptor range in uint units
            /// </summary>
            public uint TargetDescriptorOffset;

            /// <summary>
            /// Frame index in the destination counter buffer
            /// </summary>
            public uint TargetCounterOffset;

            /// <summary>
            /// Logical source frame index used by the shader
            /// </summary>
            public uint FrameIndex;

            /// <summary>
            /// Source keypoint stride between packed frames
            /// </summary>
            public uint SourceKeypointFrameStride;

            /// <summary>
            /// Source descriptor stride between packed frames in uint units
            /// </summary>
            public uint SourceDescriptorFrameStride;

            /// <summary>
            /// Source counter stride between packed frames
            /// </summary>
            public uint SourceCounterFrameStride;

            /// <summary>
            /// Metadata stride between packed frames
            /// </summary>
            public uint MetadataFrameStride;

            /// <summary>
            /// Destination keypoint capacity stride between packed frames
            /// </summary>
            public uint TargetKeypointFrameStride;

            /// <summary>
            /// Destination descriptor capacity stride between packed frames in uint units
            /// </summary>
            public uint TargetDescriptorFrameStride;

            /// <summary>
            /// Destination counter stride between packed frames
            /// </summary>
            public uint TargetCounterFrameStride;

            /// <summary>
            /// Number of packed frames processed by the dispatch
            /// </summary>
            public uint FrameCount;
        }

        /// <summary>
        /// Describes source and destination ranges for one frame in Top-K selection
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SelectionFrameRecord
        {
            /// <summary>
            /// Source keypoint and descriptor element offset
            /// </summary>
            public uint SourceOffset;

            /// <summary>
            /// Number of source feature records available for the frame
            /// </summary>
            public uint SourceCapacity;

            /// <summary>
            /// Destination keypoint and descriptor element offset
            /// </summary>
            public uint TargetOffset;

            /// <summary>
            /// Number of feature records reserved in the destination frame slice
            /// </summary>
            public uint TargetCapacity;
        }

        /// <summary>
        /// Stores the deterministic state used by one radix-selection frame
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SelectionStateRecord
        {
            /// <summary>
            /// Lower bound of the current rank interval
            /// </summary>
            public uint Lower;

            /// <summary>
            /// Upper bound of the current rank interval
            /// </summary>
            public uint Upper;

            /// <summary>
            /// Number of candidates in the current interval
            /// </summary>
            public uint Count;

            /// <summary>
            /// Number of candidates greater than the current radix partition
            /// </summary>
            public uint Greater;

            /// <summary>
            /// Number of candidates equal to the current radix partition
            /// </summary>
            public uint Equal;
        }

        /// <summary>
        /// Defines the push constants shared by the Top-K selection phases
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct SelectionPush
        {
            /// <summary>
            /// Number of frames processed by the selection dispatch
            /// </summary>
            public uint FrameCount;

            /// <summary>
            /// Number of radix workgroups assigned to each frame
            /// </summary>
            public uint WorkgroupsPerFrame;

            /// <summary>
            /// Current selection phase identifier
            /// </summary>
            public uint Phase;

            /// <summary>
            /// Current radix shift in bits
            /// </summary>
            public uint Shift;
        }

        /// <summary>
        /// Defines the push constants for one hierarchical prefix-scan dispatch
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PrefixPush
        {
            /// <summary>
            /// Input flag or block-sum offset
            /// </summary>
            public uint InputOffset;

            /// <summary>
            /// Output prefix offset
            /// </summary>
            public uint OutputOffset;

            /// <summary>
            /// Scratch block-sum offset
            /// </summary>
            public uint BlockOffset;

            /// <summary>
            /// Number of elements processed by the dispatch
            /// </summary>
            public uint ElementCount;

            /// <summary>
            /// Prefix-scan operation identifier
            /// </summary>
            public uint Operation;

            /// <summary>
            /// Input frame stride reserved for packed scan variants
            /// </summary>
            public uint InputFrameStride;

            /// <summary>
            /// Output frame stride reserved for packed scan variants
            /// </summary>
            public uint OutputFrameStride;

            /// <summary>
            /// Block-sum frame stride reserved for packed scan variants
            /// </summary>
            public uint BlockFrameStride;

            /// <summary>
            /// Frame count reserved for packed scan variants
            /// </summary>
            public uint FrameCount;
        }

        /// <summary>
        /// Describes one level of the hierarchical scan used by Top-K selection
        /// </summary>
        private sealed class SelectionScanLevel
        {
            /// <summary>
            /// Initializes a scan level description
            /// </summary>
            /// <param name="set">Descriptor set used for this scan level</param>
            /// <param name="outputOffset">Output offset for the level</param>
            /// <param name="elementCount">Number of elements represented by the level</param>
            public SelectionScanLevel(VulkanDescriptorSetLease set, int outputOffset, int elementCount)
            {
                this.Set = set;
                this.OutputOffset = outputOffset;
                this.ElementCount = elementCount;
            }

            /// <summary>
            /// Gets the descriptor set used to dispatch this scan level
            /// </summary>
            public VulkanDescriptorSetLease Set { get; }

            /// <summary>
            /// Gets the output offset for this scan level
            /// </summary>
            public int OutputOffset { get; }

            /// <summary>
            /// Gets the number of elements represented by this scan level
            /// </summary>
            public int ElementCount { get; }
        }

        /// <summary>
        /// Stores the diagnostic counters produced for one frame
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct FrameDiagnosticRecord
        {
            /// <summary>
            /// Number of candidate extrema examined for the frame
            /// </summary>
            public uint CandidateCount;

            /// <summary>
            /// Number of descriptors emitted for the frame
            /// </summary>
            public uint DescriptorCount;

            /// <summary>
            /// Number of candidates removed by capacity truncation
            /// </summary>
            public uint TruncatedCount;

            /// <summary>
            /// Number of candidates that completed refinement
            /// </summary>
            public uint RefinedCount;
        }

        #endregion
    }

    /// <summary>
    /// Describes the resident feature range assigned to one frame
    /// </summary>
    internal sealed class VulkanSiftFrameFeatures
    {
        #region Constructors

        /// <summary>
        /// Initializes a frame feature range
        /// </summary>
        /// <param name="capacityOffset">Logical element offset of the frame's reserved slice</param>
        /// <param name="capacity">Number of feature records reserved for the frame</param>
        public VulkanSiftFrameFeatures(int capacityOffset, int capacity)
        {
            this.CapacityOffset = capacityOffset;
            this.Capacity = capacity;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the logical element offset of the frame's reserved feature slice
        /// </summary>
        public int CapacityOffset { get; }

        /// <summary>
        /// Gets the number of feature records reserved for the frame
        /// </summary>
        public int Capacity { get; }

        #endregion
    }
}
