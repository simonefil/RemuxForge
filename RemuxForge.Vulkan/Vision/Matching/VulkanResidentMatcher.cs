using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Pipelines;
using RemuxForge.Vulkan.Runtime;
using RemuxForge.Vulkan.Scheduling;
using RemuxForge.Vulkan.Vision.Sift;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Vision.Matching
{
    /// <summary>
    /// Performs ratio testing, reciprocal matching, and compaction while keeping descriptors resident in device memory
    /// </summary>
    internal sealed unsafe class VulkanResidentMatcher
    {
        #region Instance fields

        /// <summary>
        /// Provides the device, pipeline, scheduler, and resource-pool services used to record matching work
        /// </summary>
        private readonly VulkanRuntimeContext _runtime;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a matcher backed by the specified Vulkan runtime
        /// </summary>
        /// <param name="runtime">Runtime services used to access the Vulkan device and shared resources</param>
        public VulkanResidentMatcher(VulkanRuntimeContext runtime)
        {
            this._runtime = runtime;
        }

        #endregion

        #region Public methods

        /// <summary>
        /// Records forward matching, reverse matching, ratio testing, reciprocal filtering, and compaction for a pair tile
        /// </summary>
        /// <param name="first">Feature collection used as the forward-match source</param>
        /// <param name="second">Feature collection used as the reverse-match source</param>
        /// <param name="pairs">Frame pairs from which the tile is selected</param>
        /// <param name="start">Index of the first pair included in the tile</param>
        /// <param name="count">Number of pairs included in the tile</param>
        /// <param name="options">Matching thresholds and runtime options passed to the shaders</param>
        /// <param name="diagnostics">Diagnostics updated with dispatch and GPU activity recorded by this operation</param>
        /// <param name="cancellationToken">Token used while waiting for the recorded submission to complete</param>
        /// <returns>A workspace that owns the GPU buffers needed by later matching stages; the caller must dispose it after the final consumer has finished</returns>
        public VulkanMatchWorkspace Match(VulkanSiftFeatureCollection first, VulkanSiftFeatureCollection second, IReadOnlyList<VulkanFramePair> pairs, int start, int count, VulkanSiftOptions options, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            if ((uint)count > this._runtime.Capabilities.MaximumComputeWorkGroupCountY)
                throw new VulkanResourceExhaustedException("The pair tile exceeds maxComputeWorkGroupCountY.");
            VulkanMatchWorkspace workspace = new VulkanMatchWorkspace(this._runtime, first, second, pairs, start, count);
            List<VulkanDescriptorSetLease> descriptorSets = new List<VulkanDescriptorSetLease>();
            try
            {
                // Build forward and reverse jobs only for pairs in the current tile
                VulkanComputePipeline jobPipeline = this._runtime.PipelineLibrary.Get("SiftBuildMatchJobs", 5, 12, diagnostics);
                string matchShader = this._runtime.Capabilities.IntegerDotProduct ? "SiftResidentMatchIntegerDot" : "SiftResidentMatch";
                VulkanComputePipeline matchPipeline = this._runtime.PipelineLibrary.Get(matchShader, 13, 16, diagnostics);
                VulkanDescriptorSetLease jobSet = this._runtime.PipelineLibrary.RentDescriptorSet(jobPipeline, new[] { first.Counts, second.Counts, workspace.Metadata, workspace.Jobs, workspace.Control });
                VulkanBuffer[] matchBuffers = new[] { first.Descriptors, second.Descriptors, first.Counts, second.Counts, workspace.Metadata, workspace.Nearest, workspace.Flags, workspace.Prefix, workspace.Candidates, workspace.ReciprocalMatches, workspace.Counts, workspace.Jobs, workspace.Control };
                VulkanDescriptorSetLease matchSet = this._runtime.PipelineLibrary.RentDescriptorSet(matchPipeline, matchBuffers);
                descriptorSets.Add(jobSet);
                descriptorSets.Add(matchSet);
                using (VulkanSubmission submission = this._runtime.Scheduler.Execute(commandBuffer =>
                {
                    JobPush jobPush = new JobPush { PairCount = (uint)count, ForwardJobCapacity = (uint)workspace.ForwardJobCapacity };
                    jobPush.Operation = 0;
                    this.Dispatch(commandBuffer, jobPipeline, jobSet, jobPush, (uint)count, 1, diagnostics);
                    this.Barrier(commandBuffer);
                    jobPush.Operation = 1;
                    this.Dispatch(commandBuffer, jobPipeline, jobSet, jobPush, (uint)count, 1, diagnostics);
                    this.Barrier(commandBuffer);
                    jobPush.Operation = 2;
                    this.Dispatch(commandBuffer, jobPipeline, jobSet, jobPush, 1, 1, diagnostics);
                    VkMemoryBarrier indirectBarrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.ShaderWrite, dstAccessMask = VkAccessFlags.IndirectCommandRead | VkAccessFlags.ShaderRead };
                    this._runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.DrawIndirect | VkPipelineStageFlags.ComputeShader, VkDependencyFlags.None, 1, &indirectBarrier, 0, null, 0, null);
                    MatchPush push = new MatchPush { PairCount = (uint)count, LoweRatio = options.LoweRatio, ForwardJobCapacity = (uint)workspace.ForwardJobCapacity };
                    push.Operation = 0;
                    this.Bind(commandBuffer, matchPipeline, matchSet, push);
                    this._runtime.DeviceApi.vkCmdDispatchIndirect(commandBuffer, workspace.Control.Buffer, 2UL * sizeof(uint));
                    diagnostics.DispatchCount++;
                    this.Barrier(commandBuffer);
                    push.Operation = 1;
                    this.Bind(commandBuffer, matchPipeline, matchSet, push);
                    this._runtime.DeviceApi.vkCmdDispatchIndirect(commandBuffer, workspace.Control.Buffer, 5UL * sizeof(uint));
                    diagnostics.DispatchCount++;
                    this.Barrier(commandBuffer);
                    // Compact matches that pass both the ratio test and reciprocal filtering
                    push.Operation = 2;
                    this.Dispatch(commandBuffer, matchPipeline, matchSet, push, DivideRoundUp((uint)Math.Max(1, workspace.MaximumFirstCapacity), 256), (uint)count, diagnostics);
                    this.Barrier(commandBuffer);
                    this.RecordScan(commandBuffer, workspace, descriptorSets, diagnostics);
                    push.Operation = 3;
                    this.Dispatch(commandBuffer, matchPipeline, matchSet, push, DivideRoundUp((uint)Math.Max(1, workspace.MaximumFirstCapacity), 256), (uint)count, diagnostics);
                    this.Barrier(commandBuffer);
                }, diagnostics, VulkanGpuPhase.Matching, cancellationToken))
                    submission.Wait(diagnostics, cancellationToken);
                return workspace;
            }
            catch
            {
                workspace.Dispose();
                throw;
            }
            finally
            {
                for (int i = descriptorSets.Count - 1; i >= 0; i--)
                    descriptorSets[i].Dispose();
            }
        }

        #endregion

        #region Private methods

        /// <summary>
        /// Records the hierarchical prefix scan used to compact reciprocal candidates
        /// </summary>
        /// <param name="commandBuffer">Command buffer that receives the scan dispatches and barriers</param>
        /// <param name="workspace">Workspace containing the flags, prefix output, and scan scratch buffers</param>
        /// <param name="leases">Descriptor-set leases that remain owned by the caller until submission cleanup</param>
        /// <param name="diagnostics">Diagnostics updated for every dispatch recorded by the scan</param>
        private void RecordScan(VkCommandBuffer commandBuffer, VulkanMatchWorkspace workspace, List<VulkanDescriptorSetLease> leases, VulkanVisionDiagnostics diagnostics)
        {
            VulkanComputePipeline pipeline = this._runtime.PipelineLibrary.Get("SiftPrefixScan", 3, 36, diagnostics);
            VulkanDescriptorSetLease primary = this._runtime.PipelineLibrary.RentDescriptorSet(pipeline, new[] { workspace.Flags, workspace.Prefix, workspace.ScanScratch });
            VulkanDescriptorSetLease scratch = this._runtime.PipelineLibrary.RentDescriptorSet(pipeline, new[] { workspace.ScanScratch, workspace.ScanScratch, workspace.ScanScratch });
            leases.Add(primary);
            leases.Add(scratch);
            List<ScanLevel> levels = new List<ScanLevel>();
            int scratchCursor = 0;
            int count = Math.Max(1, workspace.ReciprocalCapacity);
            int inputOffset = 0;
            int outputOffset = 0;
            VulkanDescriptorSetLease set = primary;
            while (true)
            {
                int blocks = (count + 255) / 256;
                int sumsOffset = scratchCursor;
                scratchCursor += blocks;
                PrefixPush push = new PrefixPush { InputOffset = (uint)inputOffset, OutputOffset = (uint)outputOffset, BlockOffset = (uint)sumsOffset, ElementCount = (uint)count };
                this.Dispatch(commandBuffer, pipeline, set, push, (uint)blocks, 1, diagnostics);
                this.Barrier(commandBuffer);
                levels.Add(new ScanLevel(set, outputOffset, count));
                if (blocks <= 1)
                    break;
                inputOffset = sumsOffset;
                outputOffset = scratchCursor;
                scratchCursor += blocks;
                count = blocks;
                set = scratch;
            }
            for (int i = levels.Count - 2; i >= 0; i--)
            {
                ScanLevel level = levels[i];
                ScanLevel parent = levels[i + 1];
                PrefixPush add = new PrefixPush { OutputOffset = (uint)level.OutputOffset, BlockOffset = (uint)parent.OutputOffset, ElementCount = (uint)level.ElementCount, Operation = 2 };
                this.Dispatch(commandBuffer, pipeline, level.Set, add, (uint)((level.ElementCount + 255) / 256), 1, diagnostics);
                this.Barrier(commandBuffer);
            }
        }

        /// <summary>
        /// Binds a pipeline and push constants, then records a compute dispatch
        /// </summary>
        /// <param name="commandBuffer">Command buffer that receives the binding and dispatch commands</param>
        /// <param name="pipeline">Compute pipeline to bind</param>
        /// <param name="set">Descriptor set lease containing resources for the dispatch</param>
        /// <param name="push">Push constants copied to the pipeline before dispatch</param>
        /// <param name="x">Dispatch size along the X dimension</param>
        /// <param name="y">Dispatch size along the Y dimension</param>
        /// <param name="diagnostics">Diagnostics whose dispatch counter is incremented</param>
        private void Dispatch<T>(VkCommandBuffer commandBuffer, VulkanComputePipeline pipeline, VulkanDescriptorSetLease set, T push, uint x, uint y, VulkanVisionDiagnostics diagnostics) where T : unmanaged
        {
            this.Bind(commandBuffer, pipeline, set, push);
            this._runtime.DeviceApi.vkCmdDispatch(commandBuffer, x, y, 1);
            diagnostics.DispatchCount++;
        }

        /// <summary>
        /// Binds a compute pipeline, descriptor set, and push constants to a command buffer
        /// </summary>
        /// <param name="commandBuffer">Command buffer that receives the binding commands</param>
        /// <param name="pipeline">Compute pipeline to bind</param>
        /// <param name="set">Descriptor set lease to bind at set index zero</param>
        /// <param name="push">Push constants copied to the pipeline</param>
        private void Bind<T>(VkCommandBuffer commandBuffer, VulkanComputePipeline pipeline, VulkanDescriptorSetLease set, T push) where T : unmanaged
        {
            this._runtime.DeviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, pipeline.Pipeline);
            VkDescriptorSet descriptorSet = set.DescriptorSet;
            this._runtime.DeviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, pipeline.PipelineLayout, 0, descriptorSet);
            this._runtime.DeviceApi.vkCmdPushConstants(commandBuffer, pipeline.PipelineLayout, VkShaderStageFlags.Compute, 0, (uint)sizeof(T), &push);
        }

        /// <summary>
        /// Records a compute-to-compute memory barrier for shader writes consumed by subsequent dispatches
        /// </summary>
        /// <param name="commandBuffer">Command buffer that receives the barrier</param>
        private void Barrier(VkCommandBuffer commandBuffer)
        {
            VkMemoryBarrier barrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.ShaderWrite, dstAccessMask = VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite };
            this._runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkDependencyFlags.None, 1, &barrier, 0, null, 0, null);
        }

        /// <summary>
        /// Divides a value by a positive divisor and rounds the result up
        /// </summary>
        /// <param name="value">Non-negative value to divide</param>
        /// <param name="divisor">Positive divisor used for the calculation</param>
        /// <returns>The smallest integer greater than or equal to <paramref name="value" /> divided by <paramref name="divisor" /></returns>
        private static uint DivideRoundUp(uint value, uint divisor)
        {
            return checked((value + divisor - 1) / divisor);
        }

        #endregion

        #region Nested types

        /// <summary>
        /// Describes one level of the hierarchical prefix scan
        /// </summary>
        private sealed class ScanLevel
        {
            /// <summary>
            /// Initializes a scan level descriptor
            /// </summary>
            /// <param name="set">Descriptor set used to record the level's dispatches</param>
            /// <param name="outputOffset">Element offset of this level's output in the prefix buffer</param>
            /// <param name="elementCount">Number of input elements represented by this level</param>
            public ScanLevel(VulkanDescriptorSetLease set, int outputOffset, int elementCount)
            {
                this.Set = set;
                this.OutputOffset = outputOffset;
                this.ElementCount = elementCount;
            }

            /// <summary>
            /// Descriptor set used for this scan level
            /// </summary>
            public VulkanDescriptorSetLease Set { get; }

            /// <summary>
            /// Element offset of this level's output in the prefix buffer
            /// </summary>
            public int OutputOffset { get; }

            /// <summary>
            /// Number of input elements represented by this level
            /// </summary>
            public int ElementCount { get; }
        }

        /// <summary>
        /// Carries per-dispatch parameters for the resident matching shader
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct MatchPush
        {
            /// <summary>
            /// Number of frame pairs processed by the dispatch
            /// </summary>
            public uint PairCount;

            /// <summary>
            /// Shader phase selected for the dispatch
            /// </summary>
            public uint Operation;

            /// <summary>
            /// Maximum accepted ratio between the nearest and second-nearest descriptor distances
            /// </summary>
            public float LoweRatio;

            /// <summary>
            /// Capacity reserved for forward matching jobs
            /// </summary>
            public uint ForwardJobCapacity;
        }

        /// <summary>
        /// Carries per-dispatch parameters for the match-job construction shader
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct JobPush
        {
            /// <summary>
            /// Number of frame pairs processed by the dispatch
            /// </summary>
            public uint PairCount;

            /// <summary>
            /// Capacity reserved for forward matching jobs
            /// </summary>
            public uint ForwardJobCapacity;

            /// <summary>
            /// Shader phase selected for the dispatch
            /// </summary>
            public uint Operation;
        }

        /// <summary>
        /// Carries per-dispatch parameters for the hierarchical prefix-scan shader
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PrefixPush
        {
            /// <summary>
            /// Element offset of the input flags or partial sums
            /// </summary>
            public uint InputOffset;

            /// <summary>
            /// Element offset of the output prefix values
            /// </summary>
            public uint OutputOffset;

            /// <summary>
            /// Element offset where block sums are written
            /// </summary>
            public uint BlockOffset;

            /// <summary>
            /// Number of elements processed by the dispatch
            /// </summary>
            public uint ElementCount;

            /// <summary>
            /// Shader phase selected for the dispatch
            /// </summary>
            public uint Operation;

            /// <summary>
            /// Reserved input frame stride, which is zero for the current scan layout
            /// </summary>
            public uint InputFrameStride;

            /// <summary>
            /// Reserved output frame stride, which is zero for the current scan layout
            /// </summary>
            public uint OutputFrameStride;

            /// <summary>
            /// Reserved block frame stride, which is zero for the current scan layout
            /// </summary>
            public uint BlockFrameStride;

            /// <summary>
            /// Reserved frame count, which is zero for the current scan layout
            /// </summary>
            public uint FrameCount;
        }

        #endregion
    }
}
