using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Pipelines;
using RemuxForge.Vulkan.Runtime;
using RemuxForge.Vulkan.Scheduling;
using RemuxForge.Vulkan.Vision.Hash;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Vision.Matching
{
    /// <summary>
    /// Counts the source frames each candidate offset explains, keeping both tracks resident in device memory
    /// </summary>
    internal sealed unsafe class VulkanHashMatcher
    {
        #region Class Fields

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
        public VulkanHashMatcher(VulkanRuntimeContext runtime)
        {
            this._runtime = runtime;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Records the explained frame count of every candidate offset in the batch
        /// </summary>
        /// <param name="source">Resident hashes and timestamps of the source track</param>
        /// <param name="language">Resident hashes and timestamps of the dubbed track</param>
        /// <param name="workspace">Workspace holding the scan metadata, the candidate grid and the result buffers</param>
        /// <param name="diagnostics">Diagnostics updated with dispatch and readback activity</param>
        /// <param name="cancellationToken">Token used while waiting for the recorded submission to complete</param>
        /// <returns>The explained frame count of every candidate offset, in batch order</returns>
        public uint[] Match(VulkanHashCollection source, VulkanHashCollection language, VulkanHashWorkspace workspace, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            if (workspace.CandidateCount == 0)
                return Array.Empty<uint>();
            VulkanComputePipeline pipeline = this._runtime.PipelineLibrary.Get("HashMatch", 8, (uint)Marshal.SizeOf<MatchPush>(), diagnostics);
            // La finestra di lavori si taglia a multipli di trentadue perche' l'inizio della
            // vista cada su un offset che qualunque dispositivo accetta per uno storage buffer
            int jobsPerTile = (int)Math.Min((uint)workspace.CandidateCount, this._runtime.Capabilities.MaximumComputeWorkGroupCountY & ~31u);
            if (jobsPerTile < 1)
                throw new VulkanResourceExhaustedException("The device does not allow enough workgroup columns for one hash scan tile.");
            ulong jobStride = (ulong)Marshal.SizeOf<VulkanHashWorkspace.JobRecord>();
            List<VulkanDescriptorSetLease> descriptorSets = new List<VulkanDescriptorSetLease>();
            List<int> tileStarts = new List<int>();
            try
            {
                // One descriptor set per tile, each one seeing only the jobs it dispatches: the
                // job index rides on the workgroup column, which the device bounds
                for (int start = 0; start < workspace.CandidateCount; start += jobsPerTile)
                {
                    int count = Math.Min(jobsPerTile, workspace.CandidateCount - start);
                    VulkanBuffer jobs = workspace.Jobs.CreateView((ulong)start * jobStride, (ulong)count * jobStride);
                    descriptorSets.Add(this._runtime.PipelineLibrary.RentDescriptorSet(pipeline, new[] { source.Hashes, language.Hashes, source.Times, language.Times, workspace.Scans, jobs, workspace.Candidates, workspace.Results }));
                    tileStarts.Add(start);
                }

                uint accumulateGroups = DivideRoundUp((uint)Math.Max(1, workspace.MaximumIndexCount), 256);
                using (VulkanSubmission submission = this._runtime.Scheduler.Execute(commandBuffer =>
                {
                    int matchTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Matching);
                    MatchPush clear = new MatchPush { Operation = 0, LanguageCount = (uint)language.Count, ResultCount = (uint)workspace.CandidateCount };
                    this.Bind(commandBuffer, pipeline, descriptorSets[0], clear);
                    this._runtime.DeviceApi.vkCmdDispatch(commandBuffer, DivideRoundUp((uint)workspace.CandidateCount, 256), 1, 1);
                    diagnostics.DispatchCount++;
                    this.Barrier(commandBuffer);
                    for (int tile = 0; tile < descriptorSets.Count; tile++)
                    {
                        int count = Math.Min(jobsPerTile, workspace.CandidateCount - tileStarts[tile]);
                        MatchPush accumulate = new MatchPush { JobCount = (uint)count, Operation = 1, LanguageCount = (uint)language.Count, ResultCount = (uint)workspace.CandidateCount };
                        this.Bind(commandBuffer, pipeline, descriptorSets[tile], accumulate);
                        this._runtime.DeviceApi.vkCmdDispatch(commandBuffer, accumulateGroups, (uint)count, 1);
                        diagnostics.DispatchCount++;
                    }
                    this._runtime.Scheduler.EndGpuPhase(commandBuffer, matchTimestamp);
                    VkMemoryBarrier transferBarrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.ShaderWrite, dstAccessMask = VkAccessFlags.TransferRead };
                    this._runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.Transfer, VkDependencyFlags.None, 1, &transferBarrier, 0, null, 0, null);
                    int readbackTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Readback);
                    VkBufferCopy download = new VkBufferCopy { srcOffset = workspace.Results.BindingOffset, dstOffset = workspace.Readback.BindingOffset, size = (ulong)workspace.CandidateCount * sizeof(uint) };
                    this._runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, workspace.Results.Buffer, workspace.Readback.Buffer, 1, &download);
                    this._runtime.Scheduler.EndGpuPhase(commandBuffer, readbackTimestamp);
                }, diagnostics, VulkanGpuPhase.None, cancellationToken))
                    submission.Wait(diagnostics, cancellationToken);

                long readStart = System.Diagnostics.Stopwatch.GetTimestamp();
                uint[] counts = workspace.Readback.Read<uint>(workspace.CandidateCount);
                diagnostics.ReadbackTicks += System.Diagnostics.Stopwatch.GetTimestamp() - readStart;
                diagnostics.ReadbackBytes += (ulong)workspace.CandidateCount * sizeof(uint);
                return counts;
            }
            finally
            {
                for (int i = descriptorSets.Count - 1; i >= 0; i--)
                    descriptorSets[i].Dispose();
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Binds a compute pipeline, descriptor set, and push constants to a command buffer
        /// </summary>
        /// <param name="commandBuffer">Command buffer that receives the binding commands</param>
        /// <param name="pipeline">Compute pipeline to bind</param>
        /// <param name="set">Descriptor set lease to bind at set index zero</param>
        /// <param name="push">Push constants copied to the pipeline</param>
        private void Bind(VkCommandBuffer commandBuffer, VulkanComputePipeline pipeline, VulkanDescriptorSetLease set, MatchPush push)
        {
            this._runtime.DeviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, pipeline.Pipeline);
            VkDescriptorSet descriptorSet = set.DescriptorSet;
            this._runtime.DeviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, pipeline.PipelineLayout, 0, descriptorSet);
            this._runtime.DeviceApi.vkCmdPushConstants(commandBuffer, pipeline.PipelineLayout, VkShaderStageFlags.Compute, 0, (uint)sizeof(MatchPush), &push);
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
        /// <returns>The smallest integer greater than or equal to the quotient</returns>
        private static uint DivideRoundUp(uint value, uint divisor)
        {
            return checked((value + divisor - 1) / divisor);
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Carries per-dispatch parameters for the hash matching shader
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct MatchPush
        {
            /// <summary>
            /// Number of scan and candidate jobs processed by the dispatch
            /// </summary>
            public uint JobCount;

            /// <summary>
            /// Shader phase selected for the dispatch
            /// </summary>
            public uint Operation;

            /// <summary>
            /// Number of frames in the dubbed track
            /// </summary>
            public uint LanguageCount;

            /// <summary>
            /// Total number of candidate offsets in the batch
            /// </summary>
            public uint ResultCount;
        }

        #endregion
    }
}
