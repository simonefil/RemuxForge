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
    /// Records and executes all GPU stages required for SIFT extraction
    /// </summary>
    internal sealed unsafe class VulkanSiftExtractor
    {
        #region Variabili di classe

        /// <summary>
        /// Provides access to the Vulkan device, resource pool, pipeline library and scheduler used by the extractor
        /// </summary>
        private readonly VulkanRuntimeContext _runtime;

        #endregion

        #region Costruttore

        /// <summary>
        /// Creates an extractor bound to an existing Vulkan runtime
        /// </summary>
        /// <param name="runtime">Runtime context whose resources and command scheduler are borrowed by this extractor</param>
        public VulkanSiftExtractor(VulkanRuntimeContext runtime)
        {
            this._runtime = runtime;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Extracts SIFT features with independent in-flight workspaces
        /// </summary>
        /// <param name="frames">Input image frames to process in their original order</param>
        /// <param name="options">SIFT thresholds, scale-space settings and intensity conversion options</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated by CPU and GPU stages</param>
        /// <param name="progress">Optional progress sink that receives upload and completion counters</param>
        /// <param name="cancellationToken">Token checked while submissions are scheduled and completed</param>
        /// <returns>A feature collection containing the merged result for every input frame</returns>
        public VulkanSiftFeatureCollection Extract(IReadOnlyList<VulkanImageFrame> frames, VulkanSiftOptions options, VulkanVisionDiagnostics diagnostics, IProgress<VulkanVisionProgress> progress, CancellationToken cancellationToken)
        {
            // Build every plan before allocating the resident result collection
            List<VulkanSiftPlan> plans = new List<VulkanSiftPlan>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
                plans.Add(VulkanSiftPlan.Create(frames[i].Width, frames[i].Height, options));
            VulkanSiftFeatureCollection features = VulkanSiftFeatureCollection.Create(this._runtime, plans);
            if (frames.Count == 0)
                return features;
            // Keep multiple extractions in flight without exceeding scheduler capacity
            Queue<PendingExtraction> pending = new Queue<PendingExtraction>();
            VulkanSiftPlan filterPlan = plans[0];
            using (VulkanBufferLease weightsLease = this._runtime.ResourcePool.Rent(checked((ulong)filterPlan.GaussianWeights.Length * sizeof(float)), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent))
            {
                weightsLease.Buffer.Write<float>(filterPlan.GaussianWeights);
                try
                {
                    for (int i = 0; i < frames.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (pending.Count >= this._runtime.Scheduler.Capacity)
                            this.CompleteAndMerge(pending.Dequeue(), diagnostics, cancellationToken);
                        long uploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        VulkanSiftWorkspace workspace = new VulkanSiftWorkspace(this._runtime, frames[i], plans[i]);
                        diagnostics.UploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - uploadStart;
                        diagnostics.UploadedBytes += checked((ulong)(frames[i].Stride * frames[i].Height));
                        VulkanMemoryStatistics memory = this._runtime.Allocator.GetStatistics();
                        diagnostics.PeakVramBytes = Math.Max(diagnostics.PeakVramBytes, memory.AllocatedBytes);
                        PendingExtraction extraction = this.Submit(workspace, features, i, weightsLease.Buffer, options, diagnostics, cancellationToken);
                        pending.Enqueue(extraction);
                        progress?.Report(new VulkanVisionProgress { UploadedFrames = i + 1, TotalFrames = frames.Count, ExtractedFrames = i + 1 - pending.Count, ResidentBytes = this._runtime.Allocator.GetStatistics().UsedBytes });
                    }
                    // Complete all remaining submissions in submission order
                    while (pending.Count > 0)
                        this.CompleteAndMerge(pending.Dequeue(), diagnostics, cancellationToken);
                    return features;
                }
                catch
                {
                    while (pending.Count > 0)
                    {
                        PendingExtraction extraction = pending.Dequeue();
                        this.Complete(extraction, diagnostics, CancellationToken.None);
                        extraction.Workspace.Dispose();
                    }
                    features.Dispose();
                    throw;
                }
            }
        }

        /// <summary>
        /// Extracts homogeneous batches with shared buffers and three-dimensional dispatches
        /// </summary>
        /// <param name="frames">Input image frames to process in their original order</param>
        /// <param name="options">SIFT thresholds, scale-space settings and intensity conversion options</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated by CPU and GPU stages</param>
        /// <param name="progress">Optional progress sink that receives batch completion counters</param>
        /// <param name="cancellationToken">Token checked before each packed batch is built and executed</param>
        /// <returns>A feature collection containing the merged result for every input frame</returns>
        public VulkanSiftFeatureCollection ExtractPacked(IReadOnlyList<VulkanImageFrame> frames, VulkanSiftOptions options, VulkanVisionDiagnostics diagnostics, IProgress<VulkanVisionProgress> progress, CancellationToken cancellationToken)
        {
            if (frames.Count < 2 || !IsHomogeneous(frames))
                return this.Extract(frames, options, diagnostics, progress, cancellationToken);

            List<VulkanSiftPlan> plans = new List<VulkanSiftPlan>(frames.Count);
            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                plans.Add(VulkanSiftPlan.Create(frames[frameIndex].Width, frames[frameIndex].Height, options));
            VulkanSiftFeatureCollection features = VulkanSiftFeatureCollection.Create(this._runtime, plans);
            using (VulkanBufferLease weightsLease = this._runtime.ResourcePool.Rent(checked((ulong)plans[0].GaussianWeights.Length * sizeof(float)), VkBufferUsageFlags.StorageBuffer, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent))
            {
                weightsLease.Buffer.Write<float>(plans[0].GaussianWeights);
                try
                {
                    // Partition the workload into batches bounded by the effective memory budget
                    int batchStart = 0;
                    while (batchStart < frames.Count)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int batchCount = this.ResolvePackedBatchCount(frames, plans, batchStart);
                        List<VulkanImageFrame> batchFrames = new List<VulkanImageFrame>(batchCount);
                        List<VulkanSiftPlan> batchPlans = new List<VulkanSiftPlan>(batchCount);
                        for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                        {
                            batchFrames.Add(frames[batchStart + batchIndex]);
                            batchPlans.Add(plans[batchStart + batchIndex]);
                        }
                        VulkanSiftPackedPlan packedPlan = new VulkanSiftPackedPlan(batchFrames, batchPlans, this._runtime.Capabilities.MinimumStorageBufferOffsetAlignment);
                        using (VulkanSiftPackedWorkspace packedWorkspace = new VulkanSiftPackedWorkspace(this._runtime, batchFrames, packedPlan))
                        {
                            List<VulkanSiftWorkspace> workspaces = new List<VulkanSiftWorkspace>(batchCount);
                            for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
                            {
                                workspaces.Add(new VulkanSiftWorkspace(batchFrames[batchIndex], batchPlans[batchIndex], packedWorkspace, packedPlan.Frames[batchIndex]));
                                diagnostics.UploadedBytes += checked((ulong)batchFrames[batchIndex].Stride * (ulong)batchFrames[batchIndex].Height);
                            }
                            this.ExecutePackedBatch(workspaces, features, batchStart, weightsLease.Buffer, options, diagnostics, cancellationToken);
                            VulkanMemoryStatistics memory = this._runtime.Allocator.GetStatistics();
                            diagnostics.PeakVramBytes = Math.Max(diagnostics.PeakVramBytes, memory.AllocatedBytes);
                            progress?.Report(new VulkanVisionProgress { UploadedFrames = batchStart + batchCount, TotalFrames = frames.Count, ExtractedFrames = batchStart + batchCount, ResidentBytes = memory.UsedBytes });
                        }
                        batchStart += batchCount;
                    }
                    return features;
                }
                catch
                {
                    features.Dispose();
                    throw;
                }
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Calculates the largest packed batch compatible with current memory and buffer limits
        /// </summary>
        /// <param name="frames">All input frames available from the specified start index</param>
        /// <param name="plans">SIFT plans corresponding one-to-one with <paramref name="frames"/></param>
        /// <param name="start">Index of the first frame that may be included in the batch</param>
        /// <returns>The number of consecutive frames that fit in the effective packed workspace budget</returns>
        private int ResolvePackedBatchCount(IReadOnlyList<VulkanImageFrame> frames, IReadOnlyList<VulkanSiftPlan> plans, int start)
        {
            VulkanMemoryStatistics statistics = this._runtime.Allocator.GetStatistics();
            ulong cachedDeviceBytes = this._runtime.ResourcePool.GetCachedDeviceBytes();
            ulong committedBytes = statistics.UsedBytes > cachedDeviceBytes ? statistics.UsedBytes - cachedDeviceBytes : 0UL;
            ulong availableBytes = statistics.PressureThreshold > committedBytes ? statistics.PressureThreshold - committedBytes : 0UL;
            ulong workspaceBudgetBytes = availableBytes * 3UL / 4UL;
            ulong maximumBufferBytes = this._runtime.Capabilities.MaximumStorageBufferRange == 0 ? ulong.MaxValue : this._runtime.Capabilities.MaximumStorageBufferRange;
            List<VulkanImageFrame> batchFrames = new List<VulkanImageFrame>();
            List<VulkanSiftPlan> batchPlans = new List<VulkanSiftPlan>();
            int maximumCount = Math.Min(256, frames.Count - start);
            int result = 0;
            for (int index = 0; index < maximumCount; index++)
            {
                batchFrames.Add(frames[start + index]);
                batchPlans.Add(plans[start + index]);
                VulkanSiftPackedPlan candidate = new VulkanSiftPackedPlan(batchFrames, batchPlans, this._runtime.Capabilities.MinimumStorageBufferOffsetAlignment);
                if (!this.FitsPackedWorkspace(candidate, workspaceBudgetBytes, maximumBufferBytes))
                {
                    batchFrames.RemoveAt(batchFrames.Count - 1);
                    batchPlans.RemoveAt(batchPlans.Count - 1);
                    break;
                }
                result++;
            }
            if (result == 0)
                throw new VulkanResourceExhaustedException("A single packed SIFT workspace exceeds the available VRAM budget.");
            return result;
        }

        /// <summary>
        /// Determines whether a packed plan fits the supplied device limits
        /// </summary>
        /// <param name="plan">Packed plan whose workspace and largest buffer requirements are evaluated</param>
        /// <param name="workspaceBudgetBytes">Maximum device workspace budget available to the candidate</param>
        /// <param name="maximumBufferBytes">Maximum size allowed for an individual storage buffer</param>
        /// <returns><see langword="true"/> when both the aggregate workspace and largest buffer are within their limits</returns>
        private bool FitsPackedWorkspace(VulkanSiftPackedPlan plan, ulong workspaceBudgetBytes, ulong maximumBufferBytes)
        {
            return plan.GetMaximumDeviceBufferBytes() <= maximumBufferBytes && plan.GetDeviceWorkspaceBytes() <= workspaceBudgetBytes;
        }

        /// <summary>
        /// Executes and completes a packed batch, then releases its descriptors and workspaces
        /// </summary>
        /// <param name="workspaces">Per-frame workspace views into the shared packed allocation</param>
        /// <param name="features">Resident feature collection receiving the merged batch output</param>
        /// <param name="frameStart">Global frame index assigned to the first workspace in the batch</param>
        /// <param name="weights">Host-visible Gaussian weights buffer shared by the dense stages</param>
        /// <param name="options">SIFT thresholds and conversion settings used by the command graph</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated during recording and submission</param>
        /// <param name="cancellationToken">Token used while waiting for the submitted batch</param>
        private void ExecutePackedBatch(IReadOnlyList<VulkanSiftWorkspace> workspaces, VulkanSiftFeatureCollection features, int frameStart, VulkanBuffer weights, VulkanSiftOptions options, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            List<VulkanDescriptorSetLease> descriptorSets = new List<VulkanDescriptorSetLease>();
            try
            {
                using (VulkanSubmission submission = this._runtime.Scheduler.Execute(commandBuffer =>
                {
                    this.RecordPacked(commandBuffer, workspaces, features, frameStart, weights, options, diagnostics, descriptorSets);
                }, diagnostics, VulkanGpuPhase.None, cancellationToken))
                    submission.Wait(diagnostics, cancellationToken);
            }
            finally
            {
                for (int descriptorIndex = descriptorSets.Count - 1; descriptorIndex >= 0; descriptorIndex--)
                    descriptorSets[descriptorIndex].Dispose();
                for (int workspaceIndex = workspaces.Count - 1; workspaceIndex >= 0; workspaceIndex--)
                    workspaces[workspaceIndex].Dispose();
            }
        }

        /// <summary>
        /// Records the complete command graph for a packed SIFT batch
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the transfer and compute commands</param>
        /// <param name="workspaces">Per-frame views into the shared packed workspace</param>
        /// <param name="features">Resident feature collection receiving the merged output</param>
        /// <param name="frameStart">Global index of the first frame represented by the batch</param>
        /// <param name="weights">Gaussian filter weights bound to the dense stages</param>
        /// <param name="options">SIFT thresholds and conversion settings used by the command graph</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated by pipeline lookup and dispatch recording</param>
        /// <param name="leases">Collection that owns descriptor leases until the enclosing submission completes</param>
        private void RecordPacked(VkCommandBuffer commandBuffer, IReadOnlyList<VulkanSiftWorkspace> workspaces, VulkanSiftFeatureCollection features, int frameStart, VulkanBuffer weights, VulkanSiftOptions options, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases)
        {
            PackedDenseBindings bindings = this.CreatePackedDenseBindings(workspaces, weights, diagnostics, leases);
            VulkanSiftWorkspace firstWorkspace = workspaces[0];
            uint frameCount = checked((uint)workspaces.Count);

            // Transfer all packed frames into the shared device-local buffer
            int uploadTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Upload);
            for (int workspaceIndex = 0; workspaceIndex < workspaces.Count; workspaceIndex++)
            {
                VulkanSiftWorkspace workspace = workspaces[workspaceIndex];
                VkBufferCopy inputCopy = new VkBufferCopy
                {
                    srcOffset = workspace.InputStaging.BindingOffset,
                    dstOffset = workspace.PackedInput.BindingOffset,
                    size = checked((ulong)workspace.Frame.Stride * (ulong)workspace.Frame.Height)
                };
                this._runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, workspace.InputStaging.Buffer, workspace.PackedInput.Buffer, 1, &inputCopy);
            }
            this.Barrier(commandBuffer, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead);
            this._runtime.Scheduler.EndGpuPhase(commandBuffer, uploadTimestamp);

            // Convert input layout and color matrix into the float intensity buffer used by SIFT
            int normalizeTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Normalize);
            VulkanComputePipeline normalize = this.GetPipeline("SiftNormalizeInput", 2, 44, diagnostics);
            NormalizePush normalizePush = new NormalizePush
            {
                InputByteOffset = this.ByteOffset(firstWorkspace.PackedInput),
                InputStride = (uint)firstWorkspace.Frame.Stride,
                OutputOffset = this.ElementOffset(firstWorkspace.InputFloat, sizeof(float)),
                Width = (uint)firstWorkspace.Frame.Width,
                Height = (uint)firstWorkspace.Frame.Height,
                IntensityScale = options.IntensityScale,
                InputFrameStride = this.FrameStride(workspaces, workspace => workspace.PackedInput, 1),
                OutputFrameStride = this.FrameStride(workspaces, workspace => workspace.InputFloat, sizeof(float)),
                FrameCount = frameCount,
                PixelFormat = (uint)firstWorkspace.Frame.PixelFormat,
                RgbToGrayMatrix = (uint)firstWorkspace.Frame.RgbToGrayMatrix
            };
            this.Dispatch(commandBuffer, normalize, bindings.Normalize, normalizePush, DivideRoundUp((uint)(firstWorkspace.Frame.Width * firstWorkspace.Frame.Height), 256), 1, frameCount, diagnostics);
            this.ComputeBarrier(commandBuffer);
            if (workspaces[0].Plan.DoubleInput)
            {
                VulkanComputePipeline resize = this.GetPipeline("SiftResizeBilinear", 2, 36, diagnostics);
                ResizePush resizePush = new ResizePush
                {
                    InputOffset = this.ElementOffset(firstWorkspace.InputFloat, sizeof(float)),
                    OutputOffset = checked(this.ElementOffset(firstWorkspace.InputFloat, sizeof(float)) + (uint)(firstWorkspace.Frame.Width * firstWorkspace.Frame.Height)),
                    InputWidth = (uint)firstWorkspace.Frame.Width,
                    InputHeight = (uint)firstWorkspace.Frame.Height,
                    OutputWidth = (uint)firstWorkspace.Plan.BaseWidth,
                    OutputHeight = (uint)firstWorkspace.Plan.BaseHeight,
                    InputFrameStride = this.FrameStride(workspaces, workspace => workspace.InputFloat, sizeof(float)),
                    OutputFrameStride = this.FrameStride(workspaces, workspace => workspace.InputFloat, sizeof(float)),
                    FrameCount = frameCount
                };
                this.Dispatch(commandBuffer, resize, bindings.Resize, resizePush, DivideRoundUp((uint)firstWorkspace.Plan.BaseWidth, 16), DivideRoundUp((uint)firstWorkspace.Plan.BaseHeight, 16), frameCount, diagnostics);
                this.ComputeBarrier(commandBuffer);
            }
            this._runtime.Scheduler.EndGpuPhase(commandBuffer, normalizeTimestamp);

            VulkanComputePipeline gaussian = this.GetPipeline("SiftGaussian", 3, 40, diagnostics);
            VulkanComputePipeline resizeGaussian = this.GetPipeline("SiftResizeBilinear", 2, 36, diagnostics);
            VulkanComputePipeline gradients = this.GetPipeline("SiftBuildGradients", 2, 32, diagnostics);
            VulkanComputePipeline dog = this.GetPipeline("SiftDifferenceOfGaussians", 2, 28, diagnostics);
            // Build scale space, gradients and Difference of Gaussians for every frame
            int gaussianTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.GaussianPyramid);
            this.RecordPackedGaussianPair(commandBuffer, workspaces, bindings, gaussian, true, 0, diagnostics);
            for (int octaveIndex = 0; octaveIndex < workspaces[0].Plan.Octaves.Count; octaveIndex++)
            {
                if (octaveIndex > 0)
                {
                    VulkanSiftOctavePlan previous = firstWorkspace.Plan.Octaves[octaveIndex - 1];
                    VulkanSiftOctavePlan octave = firstWorkspace.Plan.Octaves[octaveIndex];
                    ResizePush resizePush = new ResizePush
                    {
                        InputOffset = checked(this.ElementOffset(firstWorkspace.Gaussian, sizeof(float)) + (uint)(previous.GaussianOffset + firstWorkspace.Plan.OctaveLayers * previous.Width * previous.Height)),
                        OutputOffset = checked(this.ElementOffset(firstWorkspace.Gaussian, sizeof(float)) + (uint)octave.GaussianOffset),
                        InputWidth = (uint)previous.Width,
                        InputHeight = (uint)previous.Height,
                        OutputWidth = (uint)octave.Width,
                        OutputHeight = (uint)octave.Height,
                        InputFrameStride = this.FrameStride(workspaces, workspace => workspace.Gaussian, sizeof(float)),
                        OutputFrameStride = this.FrameStride(workspaces, workspace => workspace.Gaussian, sizeof(float)),
                        FrameCount = frameCount
                    };
                    this.Dispatch(commandBuffer, resizeGaussian, bindings.GaussianResize, resizePush, DivideRoundUp((uint)octave.Width, 16), DivideRoundUp((uint)octave.Height, 16), frameCount, diagnostics);
                    this.ComputeBarrier(commandBuffer);
                }
                for (int layer = 1; layer < workspaces[0].Plan.OctaveLayers + 3; layer++)
                    this.RecordPackedGaussianPair(commandBuffer, workspaces, bindings, gaussian, false, octaveIndex, layer, diagnostics);

                VulkanSiftOctavePlan gradientOctave = firstWorkspace.Plan.Octaves[octaveIndex];
                GradientPush gradientPush = new GradientPush
                {
                    PixelOffset = checked(this.ElementOffset(firstWorkspace.Gaussian, sizeof(float)) + (uint)gradientOctave.GaussianOffset),
                    Width = (uint)gradientOctave.Width,
                    Height = (uint)gradientOctave.Height,
                    LayerCount = checked((uint)(firstWorkspace.Plan.OctaveLayers + 3)),
                    InputFrameStride = this.FrameStride(workspaces, workspace => workspace.Gaussian, sizeof(float)),
                    OutputFrameStride = this.FrameStride(workspaces, workspace => workspace.Gradients, sizeof(float) * 2),
                    FrameCount = frameCount,
                    OutputOffset = checked(this.ElementOffset(firstWorkspace.Gradients, sizeof(float) * 2) + (uint)gradientOctave.GaussianOffset)
                };
                this.Dispatch(commandBuffer, gradients, bindings.Gradients, gradientPush, DivideRoundUp((uint)gradientOctave.Width, 16), DivideRoundUp((uint)gradientOctave.Height, 16), checked(gradientPush.LayerCount * frameCount), diagnostics);
                this.ComputeBarrier(commandBuffer);

                for (int layer = 0; layer < workspaces[0].Plan.OctaveLayers + 2; layer++)
                {
                    VulkanSiftOctavePlan dogOctave = firstWorkspace.Plan.Octaves[octaveIndex];
                    int area = checked(dogOctave.Width * dogOctave.Height);
                    DogPush dogPush = new DogPush
                    {
                        FirstOffset = checked(this.ElementOffset(firstWorkspace.Gaussian, sizeof(float)) + (uint)(dogOctave.GaussianOffset + layer * area)),
                        SecondOffset = checked(this.ElementOffset(firstWorkspace.Gaussian, sizeof(float)) + (uint)(dogOctave.GaussianOffset + (layer + 1) * area)),
                        OutputOffset = checked(this.ElementOffset(firstWorkspace.Dog, sizeof(float)) + (uint)(dogOctave.DogOffset + layer * area)),
                        ElementCount = (uint)area,
                        InputFrameStride = this.FrameStride(workspaces, workspace => workspace.Gaussian, sizeof(float)),
                        OutputFrameStride = this.FrameStride(workspaces, workspace => workspace.Dog, sizeof(float)),
                        FrameCount = frameCount
                    };
                    this.Dispatch(commandBuffer, dog, bindings.Dog, dogPush, DivideRoundUp((uint)area, 256), 1, frameCount, diagnostics);
                }
                this.ComputeBarrier(commandBuffer);
            }
            this._runtime.Scheduler.EndGpuPhase(commandBuffer, gaussianTimestamp);

            // Transform extrema into sorted, oriented and described features
            for (int octaveIndex = 0; octaveIndex < firstWorkspace.Plan.Octaves.Count; octaveIndex++)
                this.RecordPackedKeypoints(commandBuffer, workspaces, firstWorkspace.Plan.Octaves[octaveIndex], frameStart, options, diagnostics, leases);
            features.RecordPackedMerge(this._runtime, commandBuffer, workspaces, frameStart, diagnostics, leases);
        }

        /// <summary>
        /// Creates descriptor sets shared by the packed dense stages
        /// </summary>
        /// <param name="workspaces">Packed per-frame views used to locate the shared buffers</param>
        /// <param name="weights">Gaussian filter weights bound to every Gaussian descriptor set</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator used for pipeline lookup</param>
        /// <param name="leases">Collection that owns the created descriptor leases</param>
        /// <returns>A descriptor bundle whose views match the packed shader bindings</returns>
        private PackedDenseBindings CreatePackedDenseBindings(IReadOnlyList<VulkanSiftWorkspace> workspaces, VulkanBuffer weights, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases)
        {
            VulkanSiftPackedWorkspace workspace = workspaces[0].PackedWorkspace;
            VulkanComputePipeline normalize = this.GetPipeline("SiftNormalizeInput", 2, 44, diagnostics);
            VulkanComputePipeline resize = this.GetPipeline("SiftResizeBilinear", 2, 36, diagnostics);
            VulkanComputePipeline gaussian = this.GetPipeline("SiftGaussian", 3, 40, diagnostics);
            VulkanComputePipeline dog = this.GetPipeline("SiftDifferenceOfGaussians", 2, 28, diagnostics);
            VulkanComputePipeline gradients = this.GetPipeline("SiftBuildGradients", 2, 32, diagnostics);
            return new PackedDenseBindings(
                this.RentBindings(normalize, leases, workspace.PackedInput, workspace.InputFloat),
                this.RentBindings(resize, leases, workspace.InputFloat, workspace.InputFloat),
                this.RentBindings(gaussian, leases, workspace.InputFloat, workspace.TemporaryFloat, weights),
                this.RentBindings(gaussian, leases, workspace.TemporaryFloat, workspace.Gaussian, weights),
                this.RentBindings(gaussian, leases, workspace.Gaussian, workspace.TemporaryFloat, weights),
                this.RentBindings(resize, leases, workspace.Gaussian, workspace.Gaussian),
                this.RentBindings(dog, leases, workspace.Gaussian, workspace.Dog),
                this.RentBindings(gradients, leases, workspace.Gaussian, workspace.Gradients));
        }

        /// <summary>
        /// Records the separable horizontal and vertical passes of an initial packed Gaussian filter
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the filter dispatches</param>
        /// <param name="workspaces">Packed workspaces whose common frame count and strides are used</param>
        /// <param name="bindings">Descriptor bindings for the packed dense stages</param>
        /// <param name="pipeline">Gaussian compute pipeline to dispatch</param>
        /// <param name="initial">Whether the input comes from the normalized image rather than a prior Gaussian layer</param>
        /// <param name="octaveIndex">Index of the octave being filtered</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated for each dispatch</param>
        private void RecordPackedGaussianPair(VkCommandBuffer commandBuffer, IReadOnlyList<VulkanSiftWorkspace> workspaces, PackedDenseBindings bindings, VulkanComputePipeline pipeline, bool initial, int octaveIndex, VulkanVisionDiagnostics diagnostics)
        {
            this.RecordPackedGaussianPair(commandBuffer, workspaces, bindings, pipeline, initial, octaveIndex, 0, diagnostics);
        }

        /// <summary>
        /// Records the separable horizontal and vertical passes of a packed Gaussian filter layer
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the filter dispatches</param>
        /// <param name="workspaces">Packed workspaces whose common frame count and strides are used</param>
        /// <param name="bindings">Descriptor bindings for the packed dense stages</param>
        /// <param name="pipeline">Gaussian compute pipeline to dispatch</param>
        /// <param name="initial">Whether the input comes from the normalized image rather than a prior Gaussian layer</param>
        /// <param name="octaveIndex">Index of the octave being filtered</param>
        /// <param name="layer">Gaussian layer to produce when <paramref name="initial"/> is <see langword="false"/></param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated for each dispatch</param>
        private void RecordPackedGaussianPair(VkCommandBuffer commandBuffer, IReadOnlyList<VulkanSiftWorkspace> workspaces, PackedDenseBindings bindings, VulkanComputePipeline pipeline, bool initial, int octaveIndex, int layer, VulkanVisionDiagnostics diagnostics)
        {
            VulkanSiftWorkspace workspace = workspaces[0];
            VulkanSiftOctavePlan octave = workspace.Plan.Octaves[octaveIndex];
            int area = checked(octave.Width * octave.Height);
            uint inputBase = initial ? this.ElementOffset(workspace.InputFloat, sizeof(float)) : this.ElementOffset(workspace.Gaussian, sizeof(float));
            uint inputOffset = initial ? checked(inputBase + (workspace.Plan.DoubleInput ? (uint)(workspace.Frame.Width * workspace.Frame.Height) : 0u)) : checked(inputBase + (uint)(octave.GaussianOffset + (layer - 1) * area));
            VulkanGaussianFilterPlan filter = initial ? workspace.Plan.Filters[0] : workspace.Plan.Filters[layer];
            GaussianPush horizontalPush = new GaussianPush
            {
                InputOffset = inputOffset,
                OutputOffset = this.ElementOffset(workspace.TemporaryFloat, sizeof(float)),
                Width = (uint)octave.Width,
                Height = (uint)octave.Height,
                FilterOffset = (uint)filter.Offset,
                FilterLength = (uint)filter.Length,
                InputFrameStride = this.FrameStride(workspaces, item => initial ? item.InputFloat : item.Gaussian, sizeof(float)),
                OutputFrameStride = this.FrameStride(workspaces, item => item.TemporaryFloat, sizeof(float)),
                FrameCount = checked((uint)workspaces.Count)
            };
            VulkanDescriptorSetLease set = initial ? bindings.InputToTemporary : bindings.GaussianToTemporary;
            this.Dispatch(commandBuffer, pipeline, set, horizontalPush, DivideRoundUp((uint)octave.Width, 256), (uint)octave.Height, checked((uint)workspaces.Count), diagnostics);
            this.ComputeBarrier(commandBuffer);
            GaussianPush verticalPush = new GaussianPush
            {
                InputOffset = this.ElementOffset(workspace.TemporaryFloat, sizeof(float)),
                OutputOffset = checked(this.ElementOffset(workspace.Gaussian, sizeof(float)) + (initial ? (uint)octave.GaussianOffset : (uint)(octave.GaussianOffset + layer * area))),
                Width = (uint)octave.Width,
                Height = (uint)octave.Height,
                FilterOffset = (uint)filter.Offset,
                FilterLength = (uint)filter.Length,
                Vertical = 1,
                InputFrameStride = this.FrameStride(workspaces, item => item.TemporaryFloat, sizeof(float)),
                OutputFrameStride = this.FrameStride(workspaces, item => item.Gaussian, sizeof(float)),
                FrameCount = checked((uint)workspaces.Count)
            };
            this.Dispatch(commandBuffer, pipeline, bindings.TemporaryToGaussian, verticalPush, DivideRoundUp((uint)octave.Height, 256), (uint)octave.Width, checked((uint)workspaces.Count), diagnostics);
            this.ComputeBarrier(commandBuffer);
        }

        /// <summary>
        /// Records packed detection, scan, compaction, sorting, orientation and descriptor stages
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the keypoint-processing dispatches</param>
        /// <param name="workspaces">Packed workspaces whose frame views are processed together</param>
        /// <param name="octave">Octave layout and capacities used for all offsets in this stage</param>
        /// <param name="frameStart">Global index of the first frame represented by the packed batch</param>
        /// <param name="options">SIFT thresholds used by extrema detection and refinement</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated for each pipeline and dispatch</param>
        /// <param name="leases">Collection that owns descriptor leases until the enclosing submission completes</param>
        private void RecordPackedKeypoints(VkCommandBuffer commandBuffer, IReadOnlyList<VulkanSiftWorkspace> workspaces, VulkanSiftOctavePlan octave, int frameStart, VulkanSiftOptions options, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases)
        {
            VulkanSiftWorkspace first = workspaces[0];
            VulkanSiftPackedWorkspace packed = first.PackedWorkspace;
            uint frameCount = checked((uint)workspaces.Count);
            int extremaTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Extrema);
            VulkanComputePipeline detect = this.GetPipeline("SiftDetectRefine", 3, 72, diagnostics);
            VulkanDescriptorSetLease detectSet = this.RentBindings(detect, leases, packed.Dog, packed.Flags, packed.Candidates);
            DetectPush detectPush = new DetectPush
            {
                DogOffset = checked(this.ElementOffset(first.Dog, sizeof(float)) + (uint)octave.DogOffset),
                FlagOffset = checked(this.ElementOffset(first.Flags, sizeof(uint)) + (uint)octave.CandidateOffset),
                CandidateOffset = checked(this.ElementOffset(first.Candidates, 32) + (uint)octave.CandidateOffset),
                Width = (uint)octave.Width,
                Height = (uint)octave.Height,
                DogLayerCount = (uint)(first.Plan.OctaveLayers + 2),
                Octave = (uint)octave.Index,
                FrameIndex = checked((uint)frameStart),
                OctaveLayers = (uint)first.Plan.OctaveLayers,
                DoubleInput = first.Plan.DoubleInput ? 1u : 0u,
                ExtremaThreshold = 0.5f * options.ContrastThreshold / options.OctaveLayers,
                ContrastThreshold = options.ContrastThreshold,
                EdgeThreshold = options.EdgeThreshold,
                Sigma = options.Sigma,
                DogFrameStride = this.FrameStride(workspaces, item => item.Dog, sizeof(float)),
                FlagFrameStride = this.FrameStride(workspaces, item => item.Flags, sizeof(uint)),
                CandidateFrameStride = this.FrameStride(workspaces, item => item.Candidates, 32),
                FrameCount = frameCount
            };
            this.Dispatch(commandBuffer, detect, detectSet, detectPush, DivideRoundUp((uint)octave.Width, 16), DivideRoundUp((uint)octave.Height, 8), checked((uint)first.Plan.OctaveLayers * frameCount), diagnostics);
            this.ComputeBarrier(commandBuffer);
            this.RecordPackedScan(commandBuffer, workspaces, octave.CandidateOffset, octave.CandidateCount, diagnostics, leases);

            VulkanComputePipeline compact = this.GetPipeline("SiftCompactKeypoints", 5, 52, diagnostics);
            VulkanDescriptorSetLease compactSet = this.RentBindings(compact, leases, packed.Flags, packed.Prefix, packed.Candidates, packed.Keypoints, packed.Counters);
            CompactPush compactPush = new CompactPush
            {
                FlagOffset = checked(this.ElementOffset(first.Flags, sizeof(uint)) + (uint)octave.CandidateOffset),
                PrefixOffset = checked(this.ElementOffset(first.Prefix, sizeof(uint)) + (uint)octave.CandidateOffset),
                CandidateOffset = checked(this.ElementOffset(first.Candidates, 32) + (uint)octave.CandidateOffset),
                OutputOffset = checked(this.ElementOffset(first.Keypoints, 32) + (uint)octave.FeatureOffset),
                ElementCount = (uint)octave.CandidateCount,
                Capacity = (uint)octave.FeatureCapacity,
                CounterOffset = checked(this.ElementOffset(first.Counters, sizeof(uint)) + (uint)octave.CounterOffset),
                FlagFrameStride = this.FrameStride(workspaces, item => item.Flags, sizeof(uint)),
                PrefixFrameStride = this.FrameStride(workspaces, item => item.Prefix, sizeof(uint)),
                CandidateFrameStride = this.FrameStride(workspaces, item => item.Candidates, 32),
                OutputFrameStride = this.FrameStride(workspaces, item => item.Keypoints, 32),
                CounterFrameStride = this.FrameStride(workspaces, item => item.Counters, sizeof(uint)),
                FrameCount = frameCount
            };
            this.Dispatch(commandBuffer, compact, compactSet, compactPush, DivideRoundUp((uint)octave.CandidateCount, 256), 1, frameCount, diagnostics);
            this.ComputeBarrier(commandBuffer);

            bool sortedInKeypoints = this.RecordPackedStableSort(commandBuffer, workspaces, octave, diagnostics, leases);
            bool deduplicatedInKeypoints = !sortedInKeypoints;
            VulkanBuffer packedSorted = sortedInKeypoints ? packed.Keypoints : packed.SortedKeypoints;
            VulkanBuffer packedDeduplicated = deduplicatedInKeypoints ? packed.Keypoints : packed.SortedKeypoints;
            VulkanBuffer firstSorted = sortedInKeypoints ? first.Keypoints : first.SortedKeypoints;
            VulkanBuffer firstDeduplicatedOutput = deduplicatedInKeypoints ? first.Keypoints : first.SortedKeypoints;
            VulkanComputePipeline deduplicate = this.GetPipeline("SiftDeduplicateKeypoints", 5, 52, diagnostics);
            VulkanDescriptorSetLease deduplicateSet = this.RentBindings(deduplicate, leases, packedSorted, packed.Flags, packed.Prefix, packedDeduplicated, packed.Counters);
            DeduplicatePush mark = new DeduplicatePush
            {
                InputOffset = checked(this.ElementOffset(firstSorted, 32) + (uint)octave.FeatureOffset),
                OutputOffset = checked(this.ElementOffset(firstDeduplicatedOutput, 32) + (uint)octave.FeatureOffset),
                FlagOffset = checked(this.ElementOffset(first.Flags, sizeof(uint)) + (uint)octave.FeatureOffset),
                PrefixOffset = checked(this.ElementOffset(first.Prefix, sizeof(uint)) + (uint)octave.FeatureOffset),
                Capacity = (uint)octave.FeatureCapacity,
                CounterOffset = checked(this.ElementOffset(first.Counters, sizeof(uint)) + (uint)octave.CounterOffset),
                Operation = 0,
                InputFrameStride = this.FrameStride(workspaces, item => sortedInKeypoints ? item.Keypoints : item.SortedKeypoints, 32),
                OutputFrameStride = this.FrameStride(workspaces, item => deduplicatedInKeypoints ? item.Keypoints : item.SortedKeypoints, 32),
                FlagFrameStride = this.FrameStride(workspaces, item => item.Flags, sizeof(uint)),
                PrefixFrameStride = this.FrameStride(workspaces, item => item.Prefix, sizeof(uint)),
                CounterFrameStride = this.FrameStride(workspaces, item => item.Counters, sizeof(uint)),
                FrameCount = frameCount
            };
            this.Dispatch(commandBuffer, deduplicate, deduplicateSet, mark, DivideRoundUp((uint)octave.FeatureCapacity, 256), 1, frameCount, diagnostics);
            this.ComputeBarrier(commandBuffer);
            this.RecordPackedScan(commandBuffer, workspaces, octave.FeatureOffset, octave.FeatureCapacity, diagnostics, leases);
            DeduplicatePush scatter = mark;
            scatter.Operation = 1;
            this.Dispatch(commandBuffer, deduplicate, deduplicateSet, scatter, DivideRoundUp((uint)octave.FeatureCapacity, 256), 1, frameCount, diagnostics);
            this.ComputeBarrier(commandBuffer);
            this._runtime.Scheduler.EndGpuPhase(commandBuffer, extremaTimestamp);

            int orientationTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Orientation);
            VulkanComputePipeline orientation = this.GetPipeline("SiftAssignOrientations", 5, 64, diagnostics);
            VulkanDescriptorSetLease orientationSet = this.RentBindings(orientation, leases, packed.Gradients, packedDeduplicated, packed.Candidates, packed.Flags, packed.Counters);
            VulkanBuffer firstDeduplicated = deduplicatedInKeypoints ? first.Keypoints : first.SortedKeypoints;
            OrientationPush orientationPush = new OrientationPush
            {
                GaussianOffset = checked(this.ElementOffset(first.Gradients, sizeof(float) * 2) + (uint)octave.GaussianOffset),
                KeypointOffset = checked(this.ElementOffset(firstDeduplicated, 32) + (uint)octave.FeatureOffset),
                CandidateOffset = checked(this.ElementOffset(first.Candidates, 32) + (uint)octave.OrientationOffset),
                FlagOffset = checked(this.ElementOffset(first.Flags, sizeof(uint)) + (uint)octave.OrientationOffset),
                KeypointCapacity = (uint)octave.FeatureCapacity,
                Width = (uint)octave.Width,
                Height = (uint)octave.Height,
                LayerStride = checked((uint)(octave.Width * octave.Height)),
                DoubleInput = first.Plan.DoubleInput ? 1u : 0u,
                CounterOffset = checked(this.ElementOffset(first.Counters, sizeof(uint)) + (uint)octave.CounterOffset),
                GradientFrameStride = this.FrameStride(workspaces, item => item.Gradients, sizeof(float) * 2),
                KeypointFrameStride = this.FrameStride(workspaces, item => deduplicatedInKeypoints ? item.Keypoints : item.SortedKeypoints, 32),
                CandidateFrameStride = this.FrameStride(workspaces, item => item.Candidates, 32),
                FlagFrameStride = this.FrameStride(workspaces, item => item.Flags, sizeof(uint)),
                CounterFrameStride = this.FrameStride(workspaces, item => item.Counters, sizeof(uint)),
                FrameCount = frameCount
            };
            uint orientationX = Math.Min((uint)octave.FeatureCapacity, 65535u);
            this.Dispatch(commandBuffer, orientation, orientationSet, orientationPush, orientationX, DivideRoundUp((uint)octave.FeatureCapacity, orientationX), frameCount, diagnostics);
            this.ComputeBarrier(commandBuffer);
            this.RecordPackedScan(commandBuffer, workspaces, octave.OrientationOffset, octave.FeatureCapacity * 3, diagnostics, leases);

            VulkanDescriptorSetLease orientedCompactSet = this.RentBindings(compact, leases, packed.Flags, packed.Prefix, packed.Candidates, packed.OrientedKeypoints, packed.Counters);
            CompactPush orientedCompact = new CompactPush
            {
                FlagOffset = checked(this.ElementOffset(first.Flags, sizeof(uint)) + (uint)octave.OrientationOffset),
                PrefixOffset = checked(this.ElementOffset(first.Prefix, sizeof(uint)) + (uint)octave.OrientationOffset),
                CandidateOffset = checked(this.ElementOffset(first.Candidates, 32) + (uint)octave.OrientationOffset),
                OutputOffset = checked(this.ElementOffset(first.OrientedKeypoints, 32) + (uint)octave.OrientationOffset),
                ElementCount = checked((uint)(octave.FeatureCapacity * 3)),
                Capacity = checked((uint)(octave.FeatureCapacity * 3)),
                CounterOffset = checked(this.ElementOffset(first.Counters, sizeof(uint)) + (uint)(octave.CounterOffset + 2)),
                FlagFrameStride = this.FrameStride(workspaces, item => item.Flags, sizeof(uint)),
                PrefixFrameStride = this.FrameStride(workspaces, item => item.Prefix, sizeof(uint)),
                CandidateFrameStride = this.FrameStride(workspaces, item => item.Candidates, 32),
                OutputFrameStride = this.FrameStride(workspaces, item => item.OrientedKeypoints, 32),
                CounterFrameStride = this.FrameStride(workspaces, item => item.Counters, sizeof(uint)),
                FrameCount = frameCount
            };
            this.Dispatch(commandBuffer, compact, orientedCompactSet, orientedCompact, DivideRoundUp(orientedCompact.ElementCount, 256), 1, frameCount, diagnostics);
            this.ComputeBarrier(commandBuffer);
            this._runtime.Scheduler.EndGpuPhase(commandBuffer, orientationTimestamp);

            int descriptorTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Descriptor);
            VulkanComputePipeline descriptor = this.GetPipeline("SiftBuildDescriptors", 4, 56, diagnostics);
            VulkanDescriptorSetLease descriptorSet = this.RentBindings(descriptor, leases, packed.Gradients, packed.OrientedKeypoints, packed.Descriptors, packed.Counters);
            DescriptorPush descriptorPush = new DescriptorPush
            {
                GaussianOffset = checked(this.ElementOffset(first.Gradients, sizeof(float) * 2) + (uint)octave.GaussianOffset),
                KeypointOffset = checked(this.ElementOffset(first.OrientedKeypoints, 32) + (uint)octave.OrientationOffset),
                DescriptorOffset = checked(this.ElementOffset(first.Descriptors, sizeof(uint)) + (uint)(octave.OrientationOffset * 32)),
                KeypointCapacity = checked((uint)(octave.FeatureCapacity * 3)),
                Width = (uint)octave.Width,
                Height = (uint)octave.Height,
                LayerStride = checked((uint)(octave.Width * octave.Height)),
                DoubleInput = first.Plan.DoubleInput ? 1u : 0u,
                CounterOffset = checked(this.ElementOffset(first.Counters, sizeof(uint)) + (uint)(octave.CounterOffset + 2)),
                GradientFrameStride = this.FrameStride(workspaces, item => item.Gradients, sizeof(float) * 2),
                KeypointFrameStride = this.FrameStride(workspaces, item => item.OrientedKeypoints, 32),
                DescriptorFrameStride = this.FrameStride(workspaces, item => item.Descriptors, sizeof(uint)),
                CounterFrameStride = this.FrameStride(workspaces, item => item.Counters, sizeof(uint)),
                FrameCount = frameCount
            };
            this.Dispatch(commandBuffer, descriptor, descriptorSet, descriptorPush, descriptorPush.KeypointCapacity, 1, frameCount, diagnostics);
            this.ComputeBarrier(commandBuffer);
            this._runtime.Scheduler.EndGpuPhase(commandBuffer, descriptorTimestamp);
        }

        /// <summary>
        /// Records a hierarchical prefix scan across all frames in a packed batch
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the scan dispatches</param>
        /// <param name="workspaces">Packed workspaces whose frame strides address each scan input and output</param>
        /// <param name="elementOffset">Element offset of the scan range within each packed buffer</param>
        /// <param name="elementCount">Number of elements in the scan range for each frame</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated for each dispatch</param>
        /// <param name="leases">Collection that owns the scan descriptor leases until submission completion</param>
        private void RecordPackedScan(VkCommandBuffer commandBuffer, IReadOnlyList<VulkanSiftWorkspace> workspaces, int elementOffset, int elementCount, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases)
        {
            VulkanSiftWorkspace first = workspaces[0];
            VulkanSiftPackedWorkspace packed = first.PackedWorkspace;
            VulkanComputePipeline pipeline = this.GetPipeline("SiftPrefixScan", 3, 36, diagnostics);
            VulkanDescriptorSetLease primary = this.RentBindings(pipeline, leases, packed.Flags, packed.Prefix, packed.ScanScratch);
            VulkanDescriptorSetLease scratch = this.RentBindings(pipeline, leases, packed.ScanScratch, packed.ScanScratch, packed.ScanScratch);
            uint flagBase = this.ElementOffset(first.Flags, sizeof(uint));
            uint prefixBase = this.ElementOffset(first.Prefix, sizeof(uint));
            uint scratchBase = this.ElementOffset(first.ScanScratch, sizeof(uint));
            uint flagStride = this.FrameStride(workspaces, item => item.Flags, sizeof(uint));
            uint prefixStride = this.FrameStride(workspaces, item => item.Prefix, sizeof(uint));
            uint scratchStride = this.FrameStride(workspaces, item => item.ScanScratch, sizeof(uint));
            uint frameCount = checked((uint)workspaces.Count);
            List<ScanLevel> levels = new List<ScanLevel>();
            List<bool> primaryLevels = new List<bool>();
            int scratchCursor = 0;
            int count = elementCount;
            uint inputOffset = checked(flagBase + (uint)elementOffset);
            uint outputOffset = checked(prefixBase + (uint)elementOffset);
            VulkanDescriptorSetLease descriptorSet = primary;
            bool isPrimary = true;
            while (true)
            {
                int blocks = checked((int)DivideRoundUp((uint)count, 256));
                uint sumsOffset = checked(scratchBase + (uint)scratchCursor);
                scratchCursor = checked(scratchCursor + blocks);
                PrefixPush push = new PrefixPush
                {
                    InputOffset = inputOffset,
                    OutputOffset = outputOffset,
                    BlockOffset = sumsOffset,
                    ElementCount = (uint)count,
                    Operation = 0,
                    InputFrameStride = isPrimary ? flagStride : scratchStride,
                    OutputFrameStride = isPrimary ? prefixStride : scratchStride,
                    BlockFrameStride = scratchStride,
                    FrameCount = frameCount
                };
                this.Dispatch(commandBuffer, pipeline, descriptorSet, push, (uint)blocks, 1, frameCount, diagnostics);
                this.ComputeBarrier(commandBuffer);
                levels.Add(new ScanLevel(descriptorSet, checked((int)outputOffset), count));
                primaryLevels.Add(isPrimary);
                if (blocks <= 1)
                    break;
                inputOffset = sumsOffset;
                outputOffset = checked(scratchBase + (uint)scratchCursor);
                scratchCursor = checked(scratchCursor + blocks);
                count = blocks;
                descriptorSet = scratch;
                isPrimary = false;
            }
            for (int index = levels.Count - 2; index >= 0; index--)
            {
                ScanLevel level = levels[index];
                ScanLevel parent = levels[index + 1];
                PrefixPush add = new PrefixPush
                {
                    OutputOffset = checked((uint)level.OutputOffset),
                    BlockOffset = checked((uint)parent.OutputOffset),
                    ElementCount = (uint)level.ElementCount,
                    Operation = 2,
                    OutputFrameStride = primaryLevels[index] ? prefixStride : scratchStride,
                    BlockFrameStride = scratchStride,
                    FrameCount = frameCount
                };
                this.Dispatch(commandBuffer, pipeline, level.DescriptorSet, add, DivideRoundUp((uint)level.ElementCount, 256), 1, frameCount, diagnostics);
                this.ComputeBarrier(commandBuffer);
            }
        }

        /// <summary>
        /// Records deterministic stable sorting for packed keypoints
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the sorting dispatches</param>
        /// <param name="workspaces">Packed workspaces containing the keypoint and scratch buffers</param>
        /// <param name="octave">Octave layout whose feature range is sorted</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated for each dispatch</param>
        /// <param name="leases">Collection that owns the sorting descriptor leases until submission completion</param>
        /// <returns><see langword="true"/> when the sorted keypoints remain in the keypoint buffer; otherwise <see langword="false"/></returns>
        private bool RecordPackedStableSort(VkCommandBuffer commandBuffer, IReadOnlyList<VulkanSiftWorkspace> workspaces, VulkanSiftOctavePlan octave, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases)
        {
            VulkanSiftWorkspace first = workspaces[0];
            VulkanSiftPackedWorkspace packed = first.PackedWorkspace;
            bool useBitonicSort = octave.FeatureCapacity <= 2048 && this._runtime.Capabilities.MaximumComputeSharedMemorySize >= 8192;
            VulkanComputePipeline smallSort = this.GetPipeline("SiftRadixSortSmall", 3, 52, diagnostics);
            VulkanComputePipeline subgroupSort = this._runtime.Capabilities.SubgroupBallot && this._runtime.Capabilities.SubgroupSize >= 8 ? this.GetPipeline("SiftRadixSortSubgroup", 3, 52, diagnostics) : null;
            VulkanComputePipeline multiSort = this.GetPipeline("SiftRadixSort", 4, 52, diagnostics);
            VulkanDescriptorSetLease forwardSmallSort = this.RentBindings(smallSort, leases, packed.Keypoints, packed.SortedKeypoints, packed.Counters);
            VulkanDescriptorSetLease reverseSmallSort = this.RentBindings(smallSort, leases, packed.SortedKeypoints, packed.Keypoints, packed.Counters);
            VulkanDescriptorSetLease forwardSubgroupSort = subgroupSort == null ? null : this.RentBindings(subgroupSort, leases, packed.Keypoints, packed.SortedKeypoints, packed.Counters);
            VulkanDescriptorSetLease forwardMultiSort = this.RentBindings(multiSort, leases, packed.Keypoints, packed.SortedKeypoints, packed.Counters, packed.ScanScratch);
            VulkanDescriptorSetLease reverseMultiSort = this.RentBindings(multiSort, leases, packed.SortedKeypoints, packed.Keypoints, packed.Counters, packed.ScanScratch);
            uint keypointBase = this.ElementOffset(first.Keypoints, 32);
            uint sortedBase = this.ElementOffset(first.SortedKeypoints, 32);
            uint counterBase = this.ElementOffset(first.Counters, sizeof(uint));
            uint scratchBase = this.ElementOffset(first.ScanScratch, sizeof(uint));
            uint keypointStride = this.FrameStride(workspaces, item => item.Keypoints, 32);
            uint sortedStride = this.FrameStride(workspaces, item => item.SortedKeypoints, 32);
            uint counterStride = this.FrameStride(workspaces, item => item.Counters, sizeof(uint));
            uint scratchStride = this.FrameStride(workspaces, item => item.ScanScratch, sizeof(uint));
            uint frameCount = checked((uint)workspaces.Count);
            uint blockCount = DivideRoundUp((uint)octave.FeatureCapacity, 256);
            RadixSortPush push = new RadixSortPush
            {
                InputOffset = checked(keypointBase + (uint)octave.FeatureOffset),
                OutputOffset = checked(sortedBase + (uint)octave.FeatureOffset),
                Capacity = (uint)octave.FeatureCapacity,
                CounterOffset = checked(counterBase + (uint)octave.CounterOffset),
                ScratchOffset = scratchBase,
                InputFrameStride = keypointStride,
                OutputFrameStride = sortedStride,
                CounterFrameStride = counterStride,
                ScratchFrameStride = scratchStride,
                FrameCount = frameCount
            };
            if (useBitonicSort)
            {
                VulkanComputePipeline bitonicSort = this.GetPipeline("SiftBitonicSortSmall", 3, 52, diagnostics);
                VulkanDescriptorSetLease bitonicSet = this.RentBindings(bitonicSort, leases, packed.Keypoints, packed.SortedKeypoints, packed.Counters);
                this.Dispatch(commandBuffer, bitonicSort, bitonicSet, push, 1, 1, frameCount, diagnostics);
                this.ComputeBarrier(commandBuffer);
                return false;
            }
            if (subgroupSort != null && blockCount < 32)
            {
                push.Operation = 4;
                this.Dispatch(commandBuffer, subgroupSort, forwardSubgroupSort, push, 1, 1, frameCount, diagnostics);
                this.ComputeBarrier(commandBuffer);
                return true;
            }
            for (uint keySelector = 0; keySelector < 5; keySelector++)
            {
                for (uint byteIndex = 0; byteIndex < 8; byteIndex++)
                {
                    bool forward = ((keySelector * 8 + byteIndex) & 1u) == 0u;
                    push.KeySelector = keySelector;
                    push.ByteIndex = byteIndex;
                    push.InputOffset = checked((forward ? keypointBase : sortedBase) + (uint)octave.FeatureOffset);
                    push.OutputOffset = checked((forward ? sortedBase : keypointBase) + (uint)octave.FeatureOffset);
                    push.InputFrameStride = forward ? keypointStride : sortedStride;
                    push.OutputFrameStride = forward ? sortedStride : keypointStride;
                    if (blockCount < 32)
                    {
                        push.Operation = 3;
                        this.Dispatch(commandBuffer, smallSort, forward ? forwardSmallSort : reverseSmallSort, push, 1, 1, frameCount, diagnostics);
                    }
                    else
                    {
                        push.Operation = 0;
                        this.Dispatch(commandBuffer, multiSort, forward ? forwardMultiSort : reverseMultiSort, push, blockCount, 1, frameCount, diagnostics);
                        this.ComputeBarrier(commandBuffer);
                        push.Operation = 1;
                        push.InputOffset = scratchBase;
                        push.OutputOffset = scratchBase;
                        this.Dispatch(commandBuffer, multiSort, forward ? forwardMultiSort : reverseMultiSort, push, 1, 1, frameCount, diagnostics);
                        this.ComputeBarrier(commandBuffer);
                        push.Operation = 2;
                        push.InputOffset = checked((forward ? keypointBase : sortedBase) + (uint)octave.FeatureOffset);
                        push.OutputOffset = checked((forward ? sortedBase : keypointBase) + (uint)octave.FeatureOffset);
                        this.Dispatch(commandBuffer, multiSort, forward ? forwardMultiSort : reverseMultiSort, push, blockCount, 1, frameCount, diagnostics);
                    }
                    this.ComputeBarrier(commandBuffer);
                }
            }
            return true;
        }

        /// <summary>
        /// Determines whether all frames share the metadata required by the packed path
        /// </summary>
        /// <param name="frames">Frames to compare; the first frame is used as the reference</param>
        /// <returns><see langword="true"/> when every frame has matching dimensions, stride and color-conversion metadata</returns>
        private static bool IsHomogeneous(IReadOnlyList<VulkanImageFrame> frames)
        {
            VulkanImageFrame reference = frames[0];
            for (int frameIndex = 1; frameIndex < frames.Count; frameIndex++)
            {
                VulkanImageFrame frame = frames[frameIndex];
                if (frame.Width != reference.Width
                    || frame.Height != reference.Height
                    || frame.Stride != reference.Stride
                    || frame.PixelFormat != reference.PixelFormat
                    || frame.RgbToGrayMatrix != reference.RgbToGrayMatrix)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Records and submits one extraction while retaining its resources until completion
        /// </summary>
        /// <param name="workspace">Workspace containing the frame resources and output views</param>
        /// <param name="features">Resident feature collection receiving this frame's merged output</param>
        /// <param name="frameIndex">Index assigned to the frame in the resident collection</param>
        /// <param name="weights">Host-visible Gaussian weights buffer shared by the dense stages</param>
        /// <param name="options">SIFT thresholds and conversion settings used by the command graph</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated during recording and submission</param>
        /// <param name="cancellationToken">Token used while scheduling the submission</param>
        /// <returns>A pending extraction that owns the submission, descriptor leases and workspace until completion</returns>
        private PendingExtraction Submit(VulkanSiftWorkspace workspace, VulkanSiftFeatureCollection features, int frameIndex, VulkanBuffer weights, VulkanSiftOptions options, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            List<VulkanDescriptorSetLease> descriptorSets = new List<VulkanDescriptorSetLease>();
            VulkanSubmission submission;
            try
            {
                submission = this._runtime.Scheduler.Execute(commandBuffer =>
                {
                    this.Record(commandBuffer, workspace, weights, options, diagnostics, descriptorSets);
                    features.RecordMerge(this._runtime, commandBuffer, workspace, frameIndex, diagnostics, descriptorSets);
                }, diagnostics, VulkanGpuPhase.None, cancellationToken);
                return new PendingExtraction(submission, descriptorSets, workspace);
            }
            catch
            {
                for (int i = descriptorSets.Count - 1; i >= 0; i--)
                    descriptorSets[i].Dispose();
                workspace.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Waits for an extraction and merges its features into the resident result
        /// </summary>
        /// <param name="extraction">Pending extraction whose submission and workspace are still owned by the caller</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated while waiting and merging</param>
        /// <param name="cancellationToken">Token used while waiting for GPU completion</param>
        private void CompleteAndMerge(PendingExtraction extraction, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            try
            {
                this.Complete(extraction, diagnostics, cancellationToken);
            }
            finally
            {
                extraction.Workspace.Dispose();
            }
        }

        /// <summary>
        /// Completes a submission and releases its associated descriptors
        /// </summary>
        /// <param name="extraction">Pending extraction whose submission and descriptor leases are completed</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated while waiting</param>
        /// <param name="cancellationToken">Token used while waiting for GPU completion</param>
        private void Complete(PendingExtraction extraction, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            try
            {
                extraction.Submission.Wait(diagnostics, cancellationToken);
            }
            finally
            {
                for (int i = extraction.DescriptorSets.Count - 1; i >= 0; i--)
                    extraction.DescriptorSets[i].Dispose();
                extraction.Submission.Dispose();
            }
        }

        /// <summary>
        /// Records the SIFT command graph for one frame
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the transfer and compute commands</param>
        /// <param name="workspace">Workspace containing the frame resources and output views</param>
        /// <param name="weights">Gaussian filter weights bound to the dense stages</param>
        /// <param name="options">SIFT thresholds and conversion settings used by the command graph</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated by pipeline lookup and dispatch recording</param>
        /// <param name="leases">Collection that owns descriptor leases until the enclosing submission completes</param>
        private void Record(VkCommandBuffer commandBuffer, VulkanSiftWorkspace workspace, VulkanBuffer weights, VulkanSiftOptions options, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases)
        {
            VulkanSiftPlan plan = workspace.Plan;
            int uploadTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Upload);
            VkBufferCopy inputCopy = new VkBufferCopy
            {
                srcOffset = workspace.InputStaging.BindingOffset,
                dstOffset = workspace.PackedInput.BindingOffset,
                size = checked((ulong)(workspace.Frame.Stride * workspace.Frame.Height))
            };
            this._runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, workspace.InputStaging.Buffer, workspace.PackedInput.Buffer, 1, &inputCopy);
            this.Barrier(commandBuffer, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, VkAccessFlags.TransferWrite, VkAccessFlags.ShaderRead);
            this._runtime.Scheduler.EndGpuPhase(commandBuffer, uploadTimestamp);
            int normalizeTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Normalize);
            VulkanComputePipeline normalize = this.GetPipeline("SiftNormalizeInput", 2, 44, diagnostics);
            VulkanDescriptorSetLease normalizeSet = this.Rent(normalize, leases, workspace.PackedInput, workspace.InputFloat);
            NormalizePush normalizePush = new NormalizePush
            {
                InputByteOffset = 0,
                InputStride = (uint)workspace.Frame.Stride,
                OutputOffset = 0,
                Width = (uint)workspace.Frame.Width,
                Height = (uint)workspace.Frame.Height,
                IntensityScale = options.IntensityScale,
                PixelFormat = (uint)workspace.Frame.PixelFormat,
                RgbToGrayMatrix = (uint)workspace.Frame.RgbToGrayMatrix
            };
            this.Dispatch(commandBuffer, normalize, normalizeSet, normalizePush, DivideRoundUp((uint)(workspace.Frame.Width * workspace.Frame.Height), 256), 1, 1, diagnostics);
            this.ComputeBarrier(commandBuffer);
            uint baseInputOffset = 0;
            if (plan.DoubleInput)
            {
                VulkanComputePipeline resize = this.GetPipeline("SiftResizeBilinear", 2, 36, diagnostics);
                VulkanDescriptorSetLease resizeSet = this.Rent(resize, leases, workspace.InputFloat, workspace.InputFloat);
                baseInputOffset = checked((uint)(workspace.Frame.Width * workspace.Frame.Height));
                ResizePush resizePush = new ResizePush
                {
                    InputOffset = 0,
                    OutputOffset = baseInputOffset,
                    InputWidth = (uint)workspace.Frame.Width,
                    InputHeight = (uint)workspace.Frame.Height,
                    OutputWidth = (uint)plan.BaseWidth,
                    OutputHeight = (uint)plan.BaseHeight
                };
                this.Dispatch(commandBuffer, resize, resizeSet, resizePush, DivideRoundUp((uint)plan.BaseWidth, 16), DivideRoundUp((uint)plan.BaseHeight, 16), 1, diagnostics);
                this.ComputeBarrier(commandBuffer);
            }
            this._runtime.Scheduler.EndGpuPhase(commandBuffer, normalizeTimestamp);

            VulkanComputePipeline gaussian = this.GetPipeline("SiftGaussian", 3, 40, diagnostics);
            VulkanDescriptorSetLease inputToTemporary = this.Rent(gaussian, leases, workspace.InputFloat, workspace.TemporaryFloat, weights);
            VulkanDescriptorSetLease temporaryToGaussian = this.Rent(gaussian, leases, workspace.TemporaryFloat, workspace.Gaussian, weights);
            VulkanDescriptorSetLease gaussianToTemporary = this.Rent(gaussian, leases, workspace.Gaussian, workspace.TemporaryFloat, weights);
            VulkanDescriptorSetLease gaussianToGaussian = this.Rent(this.GetPipeline("SiftResizeBilinear", 2, 36, diagnostics), leases, workspace.Gaussian, workspace.Gaussian);
            VulkanSiftOctavePlan firstOctave = plan.Octaves[0];
            int initialGaussianTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.GaussianPyramid);
            this.RecordGaussianPair(commandBuffer, gaussian, inputToTemporary, temporaryToGaussian, baseInputOffset, (uint)firstOctave.GaussianOffset, firstOctave.Width, firstOctave.Height, plan.Filters[0], diagnostics);
            this.ComputeBarrier(commandBuffer);
            this._runtime.Scheduler.EndGpuPhase(commandBuffer, initialGaussianTimestamp);
            for (int octaveIndex = 0; octaveIndex < plan.Octaves.Count; octaveIndex++)
            {
                VulkanSiftOctavePlan octave = plan.Octaves[octaveIndex];
                int area = checked(octave.Width * octave.Height);
                int gaussianTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.GaussianPyramid);
                if (octaveIndex > 0)
                {
                    VulkanSiftOctavePlan previous = plan.Octaves[octaveIndex - 1];
                    ResizePush downsample = new ResizePush
                    {
                        InputOffset = checked((uint)(previous.GaussianOffset + plan.OctaveLayers * previous.Width * previous.Height)),
                        OutputOffset = (uint)octave.GaussianOffset,
                        InputWidth = (uint)previous.Width,
                        InputHeight = (uint)previous.Height,
                        OutputWidth = (uint)octave.Width,
                        OutputHeight = (uint)octave.Height
                    };
                    this.Dispatch(commandBuffer, this.GetPipeline("SiftResizeBilinear", 2, 36, diagnostics), gaussianToGaussian, downsample, DivideRoundUp((uint)octave.Width, 16), DivideRoundUp((uint)octave.Height, 16), 1, diagnostics);
                    this.ComputeBarrier(commandBuffer);
                }
                for (int layer = 1; layer < plan.OctaveLayers + 3; layer++)
                {
                    uint inputOffset = checked((uint)(octave.GaussianOffset + (layer - 1) * area));
                    uint outputOffset = checked((uint)(octave.GaussianOffset + layer * area));
                    this.RecordGaussianPair(commandBuffer, gaussian, gaussianToTemporary, temporaryToGaussian, inputOffset, outputOffset, octave.Width, octave.Height, plan.Filters[layer], diagnostics);
                    this.ComputeBarrier(commandBuffer);
                }
                this.RecordGradients(commandBuffer, workspace, octave, diagnostics, leases);
                this._runtime.Scheduler.EndGpuPhase(commandBuffer, gaussianTimestamp);
                int dogTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Extrema);
                this.RecordDog(commandBuffer, workspace, octave, diagnostics, leases);
                this._runtime.Scheduler.EndGpuPhase(commandBuffer, dogTimestamp);
                this.RecordKeypoints(commandBuffer, workspace, octave, options, diagnostics, leases);
            }
        }

        /// <summary>
        /// Records the horizontal and vertical passes of a Gaussian filter
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the filter dispatches</param>
        /// <param name="pipeline">Gaussian compute pipeline used for both passes</param>
        /// <param name="horizontalSet">Descriptor set for the horizontal pass</param>
        /// <param name="verticalSet">Descriptor set for the vertical pass</param>
        /// <param name="inputOffset">Input image or Gaussian-layer offset in elements</param>
        /// <param name="outputOffset">Output Gaussian-layer offset in elements</param>
        /// <param name="width">Width of the filtered image or layer</param>
        /// <param name="height">Height of the filtered image or layer</param>
        /// <param name="filter">Gaussian kernel metadata used by the shader</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated for each dispatch</param>
        private void RecordGaussianPair(VkCommandBuffer commandBuffer, VulkanComputePipeline pipeline, VulkanDescriptorSetLease horizontalSet, VulkanDescriptorSetLease verticalSet, uint inputOffset, uint outputOffset, int width, int height, VulkanGaussianFilterPlan filter, VulkanVisionDiagnostics diagnostics)
        {
            GaussianPush horizontal = new GaussianPush
            {
                InputOffset = inputOffset,
                OutputOffset = 0,
                Width = (uint)width,
                Height = (uint)height,
                FilterOffset = (uint)filter.Offset,
                FilterLength = (uint)filter.Length,
                Vertical = 0
            };
            this.Dispatch(commandBuffer, pipeline, horizontalSet, horizontal, DivideRoundUp((uint)width, 256), (uint)height, 1, diagnostics);
            this.ComputeBarrier(commandBuffer);
            GaussianPush vertical = horizontal;
            vertical.InputOffset = 0;
            vertical.OutputOffset = outputOffset;
            vertical.Vertical = 1;
            this.Dispatch(commandBuffer, pipeline, verticalSet, vertical, DivideRoundUp((uint)height, 256), (uint)width, 1, diagnostics);
        }

        /// <summary>
        /// Records the Difference of Gaussians stage for one octave
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the DoG dispatches</param>
        /// <param name="workspace">Workspace containing Gaussian and DoG buffers</param>
        /// <param name="octave">Octave dimensions and offsets used for each layer pair</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated for each dispatch</param>
        /// <param name="leases">Collection that owns the descriptor lease until submission completion</param>
        private void RecordDog(VkCommandBuffer commandBuffer, VulkanSiftWorkspace workspace, VulkanSiftOctavePlan octave, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases)
        {
            VulkanComputePipeline pipeline = this.GetPipeline("SiftDifferenceOfGaussians", 2, 28, diagnostics);
            VulkanDescriptorSetLease descriptorSet = this.Rent(pipeline, leases, workspace.Gaussian, workspace.Dog);
            int area = checked(octave.Width * octave.Height);
            for (int layer = 0; layer < workspace.Plan.OctaveLayers + 2; layer++)
            {
                DogPush push = new DogPush
                {
                    FirstOffset = checked((uint)(octave.GaussianOffset + layer * area)),
                    SecondOffset = checked((uint)(octave.GaussianOffset + (layer + 1) * area)),
                    OutputOffset = checked((uint)(octave.DogOffset + layer * area)),
                    ElementCount = (uint)area
                };
                this.Dispatch(commandBuffer, pipeline, descriptorSet, push, DivideRoundUp((uint)area, 256), 1, 1, diagnostics);
            }
            this.ComputeBarrier(commandBuffer);
        }

        /// <summary>
        /// Records gradient magnitude and orientation for one octave
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the gradient dispatch</param>
        /// <param name="workspace">Workspace containing the Gaussian and gradient buffers</param>
        /// <param name="octave">Octave dimensions and Gaussian offset</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated for the dispatch</param>
        /// <param name="leases">Collection that owns the descriptor lease until submission completion</param>
        private void RecordGradients(VkCommandBuffer commandBuffer, VulkanSiftWorkspace workspace, VulkanSiftOctavePlan octave, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases)
        {
            VulkanComputePipeline pipeline = this.GetPipeline("SiftBuildGradients", 2, 32, diagnostics);
            VulkanDescriptorSetLease descriptorSet = this.Rent(pipeline, leases, workspace.Gaussian, workspace.Gradients);
            GradientPush push = new GradientPush
            {
                PixelOffset = (uint)octave.GaussianOffset,
                OutputOffset = (uint)octave.GaussianOffset,
                Width = (uint)octave.Width,
                Height = (uint)octave.Height,
                LayerCount = checked((uint)(workspace.Plan.OctaveLayers + 3))
            };
            this.Dispatch(commandBuffer, pipeline, descriptorSet, push, DivideRoundUp((uint)octave.Width, 16), DivideRoundUp((uint)octave.Height, 16), push.LayerCount, diagnostics);
            this.ComputeBarrier(commandBuffer);
        }

        /// <summary>
        /// Records sparse keypoint detection and descriptor generation for one octave
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the keypoint-processing dispatches</param>
        /// <param name="workspace">Workspace containing the current frame's intermediate and output buffers</param>
        /// <param name="octave">Octave dimensions, offsets and feature capacities</param>
        /// <param name="options">SIFT thresholds used by extrema detection and refinement</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated for each pipeline and dispatch</param>
        /// <param name="leases">Collection that owns descriptor leases until the enclosing submission completes</param>
        private void RecordKeypoints(VkCommandBuffer commandBuffer, VulkanSiftWorkspace workspace, VulkanSiftOctavePlan octave, VulkanSiftOptions options, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases)
        {
            int extremaTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Extrema);
            VulkanComputePipeline detect = this.GetPipeline("SiftDetectRefine", 3, 72, diagnostics);
            VulkanDescriptorSetLease detectSet = this.Rent(detect, leases, workspace.Dog, workspace.Flags, workspace.Candidates);
            DetectPush detectPush = new DetectPush
            {
                DogOffset = (uint)octave.DogOffset,
                FlagOffset = (uint)octave.CandidateOffset,
                CandidateOffset = (uint)octave.CandidateOffset,
                Width = (uint)octave.Width,
                Height = (uint)octave.Height,
                DogLayerCount = (uint)(workspace.Plan.OctaveLayers + 2),
                Octave = (uint)octave.Index,
                FrameIndex = 0,
                OctaveLayers = (uint)workspace.Plan.OctaveLayers,
                DoubleInput = workspace.Plan.DoubleInput ? 1u : 0u,
                ExtremaThreshold = 0.5f * options.ContrastThreshold / options.OctaveLayers,
                ContrastThreshold = options.ContrastThreshold,
                EdgeThreshold = options.EdgeThreshold,
                Sigma = options.Sigma
            };
            this.Dispatch(commandBuffer, detect, detectSet, detectPush, DivideRoundUp((uint)octave.Width, 16), DivideRoundUp((uint)octave.Height, 8), (uint)workspace.Plan.OctaveLayers, diagnostics);
            this.ComputeBarrier(commandBuffer);
            this.RecordScan(commandBuffer, workspace, octave.CandidateOffset, octave.CandidateCount, diagnostics, leases);
            VulkanComputePipeline compact = this.GetPipeline("SiftCompactKeypoints", 5, 52, diagnostics);
            VulkanDescriptorSetLease compactSet = this.Rent(compact, leases, workspace.Flags, workspace.Prefix, workspace.Candidates, workspace.Keypoints, workspace.Counters);
            CompactPush compactPush = new CompactPush
            {
                FlagOffset = (uint)octave.CandidateOffset,
                PrefixOffset = (uint)octave.CandidateOffset,
                CandidateOffset = (uint)octave.CandidateOffset,
                OutputOffset = (uint)octave.FeatureOffset,
                ElementCount = (uint)octave.CandidateCount,
                Capacity = (uint)octave.FeatureCapacity,
                CounterOffset = (uint)octave.CounterOffset
            };
            this.Dispatch(commandBuffer, compact, compactSet, compactPush, DivideRoundUp((uint)octave.CandidateCount, 256), 1, 1, diagnostics);
            this.ComputeBarrier(commandBuffer);
            VulkanBuffer deduplicatedKeypoints = this.RecordStableSortAndDeduplicate(commandBuffer, workspace, octave, diagnostics, leases);
            this._runtime.Scheduler.EndGpuPhase(commandBuffer, extremaTimestamp);
            int orientationTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Orientation);
            VulkanComputePipeline orientation = this.GetPipeline("SiftAssignOrientations", 5, 64, diagnostics);
            VulkanDescriptorSetLease orientationSet = this.Rent(orientation, leases, workspace.Gradients, deduplicatedKeypoints, workspace.Candidates, workspace.Flags, workspace.Counters);
            OrientationPush orientationPush = new OrientationPush
            {
                GaussianOffset = (uint)octave.GaussianOffset,
                KeypointOffset = (uint)octave.FeatureOffset,
                CandidateOffset = (uint)octave.OrientationOffset,
                FlagOffset = (uint)octave.OrientationOffset,
                KeypointCapacity = (uint)octave.FeatureCapacity,
                Width = (uint)octave.Width,
                Height = (uint)octave.Height,
                LayerStride = checked((uint)(octave.Width * octave.Height)),
                DoubleInput = workspace.Plan.DoubleInput ? 1u : 0u,
                CounterOffset = (uint)octave.CounterOffset
            };
            this.Bind(commandBuffer, orientation, orientationSet, orientationPush);
            uint orientationWorkgroupsX = Math.Min((uint)octave.FeatureCapacity, 65535u);
            uint orientationWorkgroupsY = DivideRoundUp((uint)octave.FeatureCapacity, orientationWorkgroupsX);
            this._runtime.DeviceApi.vkCmdDispatch(commandBuffer, orientationWorkgroupsX, orientationWorkgroupsY, 1);
            diagnostics.DispatchCount++;
            this.ComputeBarrier(commandBuffer);
            this.RecordScan(commandBuffer, workspace, octave.OrientationOffset, octave.FeatureCapacity * 3, diagnostics, leases);
            VulkanDescriptorSetLease orientedCompactSet = this.Rent(compact, leases, workspace.Flags, workspace.Prefix, workspace.Candidates, workspace.OrientedKeypoints, workspace.Counters);
            CompactPush orientedCompact = new CompactPush
            {
                FlagOffset = (uint)octave.OrientationOffset,
                PrefixOffset = (uint)octave.OrientationOffset,
                CandidateOffset = (uint)octave.OrientationOffset,
                OutputOffset = (uint)octave.OrientationOffset,
                ElementCount = checked((uint)(octave.FeatureCapacity * 3)),
                Capacity = checked((uint)(octave.FeatureCapacity * 3)),
                CounterOffset = checked((uint)(octave.CounterOffset + 2))
            };
            this.Dispatch(commandBuffer, compact, orientedCompactSet, orientedCompact, DivideRoundUp(orientedCompact.ElementCount, 256), 1, 1, diagnostics);
            this.ComputeBarrier(commandBuffer);
            this._runtime.Scheduler.EndGpuPhase(commandBuffer, orientationTimestamp);
            int descriptorTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Descriptor);
            VulkanComputePipeline indirect = this.GetPipeline("SiftBuildIndirect", 2, 20, diagnostics);
            VulkanDescriptorSetLease indirectSet = this.Rent(indirect, leases, workspace.Counters, workspace.IndirectCommands);
            IndirectPush descriptorCommand = new IndirectPush
            {
                CounterOffset = checked((uint)(octave.CounterOffset + 2)),
                CommandOffset = checked((uint)(octave.Index * 6 + 3)),
                LocalSize = 1,
                Multiplier = 1,
                Capacity = checked((uint)(octave.FeatureCapacity * 3))
            };
            this.Dispatch(commandBuffer, indirect, indirectSet, descriptorCommand, 1, 1, 1, diagnostics);
            this.IndirectBarrier(commandBuffer);
            VulkanComputePipeline descriptor = this.GetPipeline("SiftBuildDescriptors", 4, 56, diagnostics);
            VulkanDescriptorSetLease descriptorSet = this.Rent(descriptor, leases, workspace.Gradients, workspace.OrientedKeypoints, workspace.Descriptors, workspace.Counters);
            DescriptorPush descriptorPush = new DescriptorPush
            {
                GaussianOffset = (uint)octave.GaussianOffset,
                KeypointOffset = (uint)octave.OrientationOffset,
                DescriptorOffset = checked((uint)(octave.OrientationOffset * 32)),
                KeypointCapacity = checked((uint)(octave.FeatureCapacity * 3)),
                Width = (uint)octave.Width,
                Height = (uint)octave.Height,
                LayerStride = checked((uint)(octave.Width * octave.Height)),
                DoubleInput = workspace.Plan.DoubleInput ? 1u : 0u,
                CounterOffset = checked((uint)(octave.CounterOffset + 2))
            };
            this.Bind(commandBuffer, descriptor, descriptorSet, descriptorPush);
            this._runtime.DeviceApi.vkCmdDispatchIndirect(commandBuffer, workspace.IndirectCommands.Buffer, checked(workspace.IndirectCommands.BindingOffset + (ulong)(octave.Index * 6 + 3) * sizeof(uint)));
            diagnostics.DispatchCount++;
            this.ComputeBarrier(commandBuffer);
            this._runtime.Scheduler.EndGpuPhase(commandBuffer, descriptorTimestamp);
        }

        /// <summary>
        /// Records a hierarchical prefix scan for one frame
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the scan dispatches</param>
        /// <param name="workspace">Workspace containing the flag, prefix and scratch buffers</param>
        /// <param name="elementOffset">Element offset of the scan range in the workspace buffers</param>
        /// <param name="elementCount">Number of elements in the scan range</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated for each dispatch</param>
        /// <param name="leases">Collection that owns the scan descriptor leases until submission completion</param>
        private void RecordScan(VkCommandBuffer commandBuffer, VulkanSiftWorkspace workspace, int elementOffset, int elementCount, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases)
        {
            VulkanComputePipeline pipeline = this.GetPipeline("SiftPrefixScan", 3, 36, diagnostics);
            VulkanDescriptorSetLease primary = this.Rent(pipeline, leases, workspace.Flags, workspace.Prefix, workspace.ScanScratch);
            VulkanDescriptorSetLease scratch = this.Rent(pipeline, leases, workspace.ScanScratch, workspace.ScanScratch, workspace.ScanScratch);
            List<ScanLevel> levels = new List<ScanLevel>();
            int scratchCursor = 0;
            int count = elementCount;
            int inputOffset = elementOffset;
            int outputOffset = elementOffset;
            VulkanDescriptorSetLease descriptorSet = primary;
            while (true)
            {
                int blocks = checked((int)DivideRoundUp((uint)count, 256));
                int sumsOffset = scratchCursor;
                scratchCursor = checked(scratchCursor + blocks);
                PrefixPush push = new PrefixPush
                {
                    InputOffset = (uint)inputOffset,
                    OutputOffset = (uint)outputOffset,
                    BlockOffset = (uint)sumsOffset,
                    ElementCount = (uint)count,
                    Operation = 0
                };
                this.Dispatch(commandBuffer, pipeline, descriptorSet, push, (uint)blocks, 1, 1, diagnostics);
                this.ComputeBarrier(commandBuffer);
                levels.Add(new ScanLevel(descriptorSet, outputOffset, count));
                if (blocks <= 1)
                    break;
                inputOffset = sumsOffset;
                outputOffset = scratchCursor;
                scratchCursor = checked(scratchCursor + blocks);
                count = blocks;
                descriptorSet = scratch;
            }
            for (int i = levels.Count - 2; i >= 0; i--)
            {
                ScanLevel level = levels[i];
                ScanLevel parent = levels[i + 1];
                PrefixPush add = new PrefixPush
                {
                    OutputOffset = (uint)level.OutputOffset,
                    BlockOffset = (uint)parent.OutputOffset,
                    ElementCount = (uint)level.ElementCount,
                    Operation = 2
                };
                this.Dispatch(commandBuffer, pipeline, level.DescriptorSet, add, DivideRoundUp((uint)level.ElementCount, 256), 1, 1, diagnostics);
                this.ComputeBarrier(commandBuffer);
            }
        }

        /// <summary>
        /// Sorts refined keypoints deterministically and optionally deduplicates them
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the sorting and deduplication dispatches</param>
        /// <param name="workspace">Workspace containing keypoint, flag, prefix and scratch buffers</param>
        /// <param name="octave">Octave layout whose feature range is processed</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated for each dispatch</param>
        /// <param name="leases">Collection that owns all descriptor leases created by this method</param>
        /// <param name="deduplicate">Whether to run the mark, scan and scatter deduplication passes after sorting</param>
        /// <returns>The buffer containing the sorted keypoints, or the deduplicated output buffer when deduplication is enabled</returns>
        private VulkanBuffer RecordStableSortAndDeduplicate(VkCommandBuffer commandBuffer, VulkanSiftWorkspace workspace, VulkanSiftOctavePlan octave, VulkanVisionDiagnostics diagnostics, List<VulkanDescriptorSetLease> leases, bool deduplicate = true)
        {
            bool useBitonicSort = octave.FeatureCapacity <= 2048 && this._runtime.Capabilities.MaximumComputeSharedMemorySize >= 8192;
            VulkanComputePipeline smallSort = this.GetPipeline("SiftRadixSortSmall", 3, 52, diagnostics);
            VulkanComputePipeline subgroupSort = this._runtime.Capabilities.SubgroupBallot && this._runtime.Capabilities.SubgroupSize >= 8 ? this.GetPipeline("SiftRadixSortSubgroup", 3, 52, diagnostics) : null;
            VulkanComputePipeline multiSort = this.GetPipeline("SiftRadixSort", 4, 52, diagnostics);
            VulkanDescriptorSetLease forwardSmallSort = this.Rent(smallSort, leases, workspace.Keypoints, workspace.SortedKeypoints, workspace.Counters);
            VulkanDescriptorSetLease reverseSmallSort = this.Rent(smallSort, leases, workspace.SortedKeypoints, workspace.Keypoints, workspace.Counters);
            VulkanDescriptorSetLease forwardSubgroupSort = subgroupSort == null ? null : this.Rent(subgroupSort, leases, workspace.Keypoints, workspace.SortedKeypoints, workspace.Counters);
            VulkanDescriptorSetLease reverseSubgroupSort = subgroupSort == null ? null : this.Rent(subgroupSort, leases, workspace.SortedKeypoints, workspace.Keypoints, workspace.Counters);
            VulkanDescriptorSetLease forwardMultiSort = this.Rent(multiSort, leases, workspace.Keypoints, workspace.SortedKeypoints, workspace.Counters, workspace.ScanScratch);
            VulkanDescriptorSetLease reverseMultiSort = this.Rent(multiSort, leases, workspace.SortedKeypoints, workspace.Keypoints, workspace.Counters, workspace.ScanScratch);
            uint blockCount = DivideRoundUp((uint)octave.FeatureCapacity, 256);
            if (useBitonicSort)
            {
                VulkanComputePipeline bitonicSort = this.GetPipeline("SiftBitonicSortSmall", 3, 52, diagnostics);
                VulkanDescriptorSetLease bitonicSet = this.Rent(bitonicSort, leases, workspace.Keypoints, workspace.SortedKeypoints, workspace.Counters);
                RadixSortPush bitonicPush = new RadixSortPush
                {
                    InputOffset = (uint)octave.FeatureOffset,
                    OutputOffset = (uint)octave.FeatureOffset,
                    Capacity = (uint)octave.FeatureCapacity,
                    CounterOffset = (uint)octave.CounterOffset
                };
                this.Dispatch(commandBuffer, bitonicSort, bitonicSet, bitonicPush, 1, 1, 1, diagnostics);
                this.ComputeBarrier(commandBuffer);
            }
            else if (subgroupSort != null && blockCount < 32)
            {
                RadixSortPush fusedPush = new RadixSortPush
                {
                    InputOffset = (uint)octave.FeatureOffset,
                    OutputOffset = (uint)octave.FeatureOffset,
                    Capacity = (uint)octave.FeatureCapacity,
                    CounterOffset = (uint)octave.CounterOffset,
                    Operation = 4
                };
                this.Dispatch(commandBuffer, subgroupSort, forwardSubgroupSort, fusedPush, 1, 1, 1, diagnostics);
                this.ComputeBarrier(commandBuffer);
            }
            else
            {
                for (uint keySelector = 0; keySelector < 5; keySelector++)
                {
                    for (uint byteIndex = 0; byteIndex < 8; byteIndex++)
                    {
                        bool forward = ((keySelector * 8 + byteIndex) & 1u) == 0u;
                        RadixSortPush push = new RadixSortPush
                        {
                            InputOffset = (uint)octave.FeatureOffset,
                            OutputOffset = (uint)octave.FeatureOffset,
                            Capacity = (uint)octave.FeatureCapacity,
                            CounterOffset = (uint)octave.CounterOffset,
                            KeySelector = keySelector,
                            ByteIndex = byteIndex
                        };
                        if (blockCount < 32)
                        {
                            push.Operation = 3;
                            VulkanComputePipeline selectedSort = subgroupSort ?? smallSort;
                            VulkanDescriptorSetLease selectedSet = subgroupSort == null ? (forward ? forwardSmallSort : reverseSmallSort) : (forward ? forwardSubgroupSort : reverseSubgroupSort);
                            this.Dispatch(commandBuffer, selectedSort, selectedSet, push, 1, 1, 1, diagnostics);
                        }
                        else
                        {
                            push.Operation = 0;
                            this.Dispatch(commandBuffer, multiSort, forward ? forwardMultiSort : reverseMultiSort, push, blockCount, 1, 1, diagnostics);
                            this.ComputeBarrier(commandBuffer);
                            push.Operation = 1;
                            this.Dispatch(commandBuffer, multiSort, forward ? forwardMultiSort : reverseMultiSort, push, 1, 1, 1, diagnostics);
                            this.ComputeBarrier(commandBuffer);
                            push.Operation = 2;
                            this.Dispatch(commandBuffer, multiSort, forward ? forwardMultiSort : reverseMultiSort, push, blockCount, 1, 1, diagnostics);
                        }
                        this.ComputeBarrier(commandBuffer);
                    }
                }
            }

            VulkanBuffer sortedInput = useBitonicSort ? workspace.SortedKeypoints : workspace.Keypoints;
            VulkanBuffer deduplicatedOutput = useBitonicSort ? workspace.Keypoints : workspace.SortedKeypoints;
            if (!deduplicate)
                return sortedInput;
            VulkanComputePipeline deduplicatePipeline = this.GetPipeline("SiftDeduplicateKeypoints", 5, 52, diagnostics);
            VulkanDescriptorSetLease deduplicateSet = this.Rent(deduplicatePipeline, leases, sortedInput, workspace.Flags, workspace.Prefix, deduplicatedOutput, workspace.Counters);
            DeduplicatePush mark = new DeduplicatePush
            {
                InputOffset = (uint)octave.FeatureOffset,
                OutputOffset = (uint)octave.FeatureOffset,
                FlagOffset = (uint)octave.FeatureOffset,
                PrefixOffset = (uint)octave.FeatureOffset,
                Capacity = (uint)octave.FeatureCapacity,
                CounterOffset = (uint)octave.CounterOffset,
                Operation = 0
            };
            this.Dispatch(commandBuffer, deduplicatePipeline, deduplicateSet, mark, DivideRoundUp((uint)octave.FeatureCapacity, 256), 1, 1, diagnostics);
            this.ComputeBarrier(commandBuffer);
            this.RecordScan(commandBuffer, workspace, octave.FeatureOffset, octave.FeatureCapacity, diagnostics, leases);
            DeduplicatePush scatter = mark;
            scatter.Operation = 1;
            this.Dispatch(commandBuffer, deduplicatePipeline, deduplicateSet, scatter, DivideRoundUp((uint)octave.FeatureCapacity, 256), 1, 1, diagnostics);
            this.ComputeBarrier(commandBuffer);
            return deduplicatedOutput;
        }

        /// <summary>
        /// Retrieves a compute pipeline while validating its shader contract
        /// </summary>
        /// <param name="name">Pipeline name registered in the runtime library</param>
        /// <param name="bindings">Expected descriptor binding count</param>
        /// <param name="pushSize">Expected push-constant size in bytes</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator updated by pipeline lookup</param>
        /// <returns>The validated compute pipeline</returns>
        private VulkanComputePipeline GetPipeline(string name, uint bindings, uint pushSize, VulkanVisionDiagnostics diagnostics)
        {
            return this._runtime.PipelineLibrary.Get(name, bindings, pushSize, diagnostics);
        }

        /// <summary>
        /// Acquires a reusable descriptor set and records its ownership
        /// </summary>
        /// <param name="pipeline">Pipeline whose descriptor layout is used</param>
        /// <param name="leases">Collection that owns the returned descriptor lease</param>
        /// <param name="buffers">Buffers bound in the pipeline's declared binding order</param>
        /// <returns>The acquired descriptor lease</returns>
        private VulkanDescriptorSetLease Rent(VulkanComputePipeline pipeline, List<VulkanDescriptorSetLease> leases, params VulkanBuffer[] buffers)
        {
            VulkanDescriptorSetLease result = this._runtime.PipelineLibrary.RentDescriptorSet(pipeline, buffers);
            leases.Add(result);
            return result;
        }

        /// <summary>
        /// Creates descriptor bindings that preserve the supplied buffer views
        /// </summary>
        /// <param name="pipeline">Pipeline whose descriptor layout is used</param>
        /// <param name="leases">Collection that owns the returned descriptor lease</param>
        /// <param name="buffers">Buffers converted to whole-buffer bindings in declaration order</param>
        /// <returns>The acquired descriptor lease with explicit whole-buffer views</returns>
        private VulkanDescriptorSetLease RentBindings(VulkanComputePipeline pipeline, List<VulkanDescriptorSetLease> leases, params VulkanBuffer[] buffers)
        {
            List<VulkanBufferBinding> bindings = new List<VulkanBufferBinding>(buffers.Length);
            for (int index = 0; index < buffers.Length; index++)
                bindings.Add(VulkanBufferBinding.Whole(buffers[index]));
            VulkanDescriptorSetLease result = this._runtime.PipelineLibrary.RentDescriptorSet(pipeline, bindings);
            leases.Add(result);
            return result;
        }

        /// <summary>
        /// Converts a binding offset to bytes for push constants
        /// </summary>
        /// <param name="buffer">Buffer whose binding offset is converted</param>
        /// <returns>The binding offset represented as a 32-bit byte offset</returns>
        private uint ByteOffset(VulkanBuffer buffer)
        {
            return checked((uint)buffer.BindingOffset);
        }

        /// <summary>
        /// Converts a binding offset to typed elements
        /// </summary>
        /// <param name="buffer">Buffer whose binding offset is converted</param>
        /// <param name="elementSize">Size in bytes of one element</param>
        /// <returns>The binding offset divided by <paramref name="elementSize"/></returns>
        private uint ElementOffset(VulkanBuffer buffer, int elementSize)
        {
            return checked((uint)(buffer.BindingOffset / (ulong)elementSize));
        }

        /// <summary>
        /// Calculates the uniform distance between consecutive packed frames
        /// </summary>
        /// <param name="workspaces">Packed workspaces whose first two views establish the stride</param>
        /// <param name="selector">Selects the buffer whose frame stride is required</param>
        /// <param name="elementSize">Size in bytes of one element in the selected buffer</param>
        /// <returns>The frame stride in typed elements, or zero when fewer than two workspaces are supplied</returns>
        private uint FrameStride(IReadOnlyList<VulkanSiftWorkspace> workspaces, Func<VulkanSiftWorkspace, VulkanBuffer> selector, int elementSize)
        {
            if (workspaces.Count < 2)
                return 0u;
            ulong first = selector(workspaces[0]).BindingOffset;
            ulong second = selector(workspaces[1]).BindingOffset;
            return checked((uint)((second - first) / (ulong)elementSize));
        }

        /// <summary>
        /// Binds a pipeline and push-constant payload, then records a compute dispatch
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the binding and dispatch commands</param>
        /// <param name="pipeline">Compute pipeline to bind</param>
        /// <param name="descriptorSet">Descriptor set matching the pipeline layout</param>
        /// <param name="parameters">Push-constant payload copied into the command buffer</param>
        /// <param name="x">Number of workgroups dispatched along the X axis</param>
        /// <param name="y">Number of workgroups dispatched along the Y axis</param>
        /// <param name="z">Number of workgroups dispatched along the Z axis</param>
        /// <param name="diagnostics">Mutable diagnostic accumulator whose dispatch count is incremented</param>
        private void Dispatch<T>(VkCommandBuffer commandBuffer, VulkanComputePipeline pipeline, VulkanDescriptorSetLease descriptorSet, T parameters, uint x, uint y, uint z, VulkanVisionDiagnostics diagnostics) where T : unmanaged
        {
            this.Bind(commandBuffer, pipeline, descriptorSet, parameters);
            this._runtime.DeviceApi.vkCmdDispatch(commandBuffer, x, y, z);
            diagnostics.DispatchCount++;
        }

        /// <summary>
        /// Binds a pipeline, descriptor set and push-constant payload to a command buffer
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the binding commands</param>
        /// <param name="pipeline">Compute pipeline whose layout receives the bindings and push constants</param>
        /// <param name="descriptorSet">Descriptor set matching the pipeline layout</param>
        /// <param name="parameters">Push-constant payload copied into the command buffer</param>
        private void Bind<T>(VkCommandBuffer commandBuffer, VulkanComputePipeline pipeline, VulkanDescriptorSetLease descriptorSet, T parameters) where T : unmanaged
        {
            this._runtime.DeviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, pipeline.Pipeline);
            VkDescriptorSet set = descriptorSet.DescriptorSet;
            this._runtime.DeviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, pipeline.PipelineLayout, 0, set);
            this._runtime.DeviceApi.vkCmdPushConstants(commandBuffer, pipeline.PipelineLayout, VkShaderStageFlags.Compute, 0, (uint)sizeof(T), &parameters);
        }

        /// <summary>
        /// Makes compute writes visible to subsequent compute dispatches
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the memory barrier</param>
        private void ComputeBarrier(VkCommandBuffer commandBuffer)
        {
            this.Barrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.ComputeShader, VkAccessFlags.ShaderWrite, VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite);
        }

        /// <summary>
        /// Makes shader-produced indirect commands visible to indirect dispatch
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the memory barrier</param>
        private void IndirectBarrier(VkCommandBuffer commandBuffer)
        {
            this.Barrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.DrawIndirect, VkAccessFlags.ShaderWrite, VkAccessFlags.IndirectCommandRead);
        }

        /// <summary>
        /// Records a memory barrier with explicit pipeline stages and access masks
        /// </summary>
        /// <param name="commandBuffer">Command buffer receiving the barrier</param>
        /// <param name="sourceStage">Pipeline stage that produces the writes</param>
        /// <param name="destinationStage">Pipeline stage that consumes the writes</param>
        /// <param name="sourceAccess">Access types that must be made visible</param>
        /// <param name="destinationAccess">Access types that must observe the writes</param>
        private void Barrier(VkCommandBuffer commandBuffer, VkPipelineStageFlags sourceStage, VkPipelineStageFlags destinationStage, VkAccessFlags sourceAccess, VkAccessFlags destinationAccess)
        {
            VkMemoryBarrier barrier = new VkMemoryBarrier { srcAccessMask = sourceAccess, dstAccessMask = destinationAccess };
            this._runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, sourceStage, destinationStage, VkDependencyFlags.None, 1, &barrier, 0, null, 0, null);
        }

        /// <summary>
        /// Divides a value by a positive divisor and rounds the result up
        /// </summary>
        /// <param name="value">Value to divide</param>
        /// <param name="divisor">Positive divisor representing the workgroup or block width</param>
        /// <returns>The smallest integer that is greater than or equal to <paramref name="value"/> divided by <paramref name="divisor"/></returns>
        private static uint DivideRoundUp(uint value, uint divisor)
        {
            return checked((value + divisor - 1) / divisor);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Owns the resources and submission for an extraction that has not completed
        /// </summary>
        private sealed class PendingExtraction
        {
            /// <summary>
            /// Creates a pending extraction with all resources required until GPU completion
            /// </summary>
            /// <param name="submission">GPU submission that must complete before its resources can be released</param>
            /// <param name="descriptorSets">Descriptor leases retained by the submission</param>
            /// <param name="workspace">Workspace retained by the submission and merge operation</param>
            public PendingExtraction(VulkanSubmission submission, List<VulkanDescriptorSetLease> descriptorSets, VulkanSiftWorkspace workspace)
            {
                this.Submission = submission;
                this.DescriptorSets = descriptorSets;
                this.Workspace = workspace;
            }

            /// <summary>
            /// Gets the GPU submission retained until completion
            /// </summary>
            public VulkanSubmission Submission { get; }

            /// <summary>
            /// Gets the descriptor leases retained by the submission
            /// </summary>
            public List<VulkanDescriptorSetLease> DescriptorSets { get; }

            /// <summary>
            /// Gets the workspace retained until the submission has been merged
            /// </summary>
            public VulkanSiftWorkspace Workspace { get; }
        }

        /// <summary>
        /// Describes one level of a hierarchical prefix scan
        /// </summary>
        private sealed class ScanLevel
        {
            /// <summary>
            /// Creates a scan-level descriptor and records its output range
            /// </summary>
            /// <param name="descriptorSet">Descriptor set used to produce or update this level</param>
            /// <param name="outputOffset">Output offset in the descriptor's element units</param>
            /// <param name="elementCount">Number of elements represented by the level</param>
            public ScanLevel(VulkanDescriptorSetLease descriptorSet, int outputOffset, int elementCount)
            {
                this.DescriptorSet = descriptorSet;
                this.OutputOffset = outputOffset;
                this.ElementCount = elementCount;
            }

            /// <summary>
            /// Gets the descriptor set used to update this scan level
            /// </summary>
            public VulkanDescriptorSetLease DescriptorSet { get; }

            /// <summary>
            /// Gets the output offset of this scan level
            /// </summary>
            public int OutputOffset { get; }

            /// <summary>
            /// Gets the number of elements represented by this scan level
            /// </summary>
            public int ElementCount { get; }
        }

        /// <summary>
        /// Groups descriptor sets shared by packed dense stages
        /// </summary>
        private sealed class PackedDenseBindings
        {
            /// <summary>
            /// Creates the descriptor bundle used by all packed dense stages
            /// </summary>
            /// <param name="normalize">Descriptor set for input normalization</param>
            /// <param name="resize">Descriptor set for image resizing</param>
            /// <param name="inputToTemporary">Descriptor set for the initial horizontal Gaussian pass</param>
            /// <param name="temporaryToGaussian">Descriptor set for the vertical Gaussian pass into the Gaussian buffer</param>
            /// <param name="gaussianToTemporary">Descriptor set for subsequent horizontal Gaussian passes</param>
            /// <param name="gaussianResize">Descriptor set for octave downsampling</param>
            /// <param name="dog">Descriptor set for Difference of Gaussians</param>
            /// <param name="gradients">Descriptor set for gradient generation</param>
            public PackedDenseBindings(VulkanDescriptorSetLease normalize, VulkanDescriptorSetLease resize, VulkanDescriptorSetLease inputToTemporary, VulkanDescriptorSetLease temporaryToGaussian, VulkanDescriptorSetLease gaussianToTemporary, VulkanDescriptorSetLease gaussianResize, VulkanDescriptorSetLease dog, VulkanDescriptorSetLease gradients)
            {
                this.Normalize = normalize;
                this.Resize = resize;
                this.InputToTemporary = inputToTemporary;
                this.TemporaryToGaussian = temporaryToGaussian;
                this.GaussianToTemporary = gaussianToTemporary;
                this.GaussianResize = gaussianResize;
                this.Dog = dog;
                this.Gradients = gradients;
            }

            /// <summary>
            /// Gets the normalization descriptor set
            /// </summary>
            public VulkanDescriptorSetLease Normalize { get; }

            /// <summary>
            /// Gets the resize descriptor set
            /// </summary>
            public VulkanDescriptorSetLease Resize { get; }

            /// <summary>
            /// Gets the initial input-to-temporary Gaussian descriptor set
            /// </summary>
            public VulkanDescriptorSetLease InputToTemporary { get; }

            /// <summary>
            /// Gets the temporary-to-Gaussian descriptor set
            /// </summary>
            public VulkanDescriptorSetLease TemporaryToGaussian { get; }

            /// <summary>
            /// Gets the Gaussian-to-temporary descriptor set
            /// </summary>
            public VulkanDescriptorSetLease GaussianToTemporary { get; }

            /// <summary>
            /// Gets the Gaussian resize descriptor set
            /// </summary>
            public VulkanDescriptorSetLease GaussianResize { get; }

            /// <summary>
            /// Gets the Difference of Gaussians descriptor set
            /// </summary>
            public VulkanDescriptorSetLease Dog { get; }

            /// <summary>
            /// Gets the gradient-generation descriptor set
            /// </summary>
            public VulkanDescriptorSetLease Gradients { get; }
        }

        /// <summary>
        /// Defines the push-constant layout for extrema detection and refinement
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct DetectPush
        {
            /// <summary>
            /// Offset of the Difference of Gaussians range in float elements
            /// </summary>
            public uint DogOffset;

            /// <summary>
            /// Offset of candidate flags in uint elements
            /// </summary>
            public uint FlagOffset;

            /// <summary>
            /// Offset of candidate records in 32-byte elements
            /// </summary>
            public uint CandidateOffset;

            /// <summary>
            /// Octave width in pixels
            /// </summary>
            public uint Width;

            /// <summary>
            /// Octave height in pixels
            /// </summary>
            public uint Height;

            /// <summary>
            /// Number of DoG layers visible to the detector
            /// </summary>
            public uint DogLayerCount;

            /// <summary>
            /// Index of the current octave
            /// </summary>
            public uint Octave;

            /// <summary>
            /// Index of the current frame in the packed collection
            /// </summary>
            public uint FrameIndex;

            /// <summary>
            /// Number of usable layers in the octave
            /// </summary>
            public uint OctaveLayers;

            /// <summary>
            /// Indicates whether the input was resized into a second image plane
            /// </summary>
            public uint DoubleInput;

            /// <summary>
            /// Minimum contrast response used to reject weak extrema
            /// </summary>
            public float ExtremaThreshold;

            /// <summary>
            /// Contrast threshold used during keypoint refinement
            /// </summary>
            public float ContrastThreshold;

            /// <summary>
            /// Edge response threshold used to reject unstable extrema
            /// </summary>
            public float EdgeThreshold;

            /// <summary>
            /// Base Gaussian sigma used by the refinement stage
            /// </summary>
            public float Sigma;

            /// <summary>
            /// Stride between packed DoG frames in float elements
            /// </summary>
            public uint DogFrameStride;

            /// <summary>
            /// Stride between packed flag frames in uint elements
            /// </summary>
            public uint FlagFrameStride;

            /// <summary>
            /// Stride between packed candidate frames in 32-byte elements
            /// </summary>
            public uint CandidateFrameStride;

            /// <summary>
            /// Number of frames processed by the packed dispatch
            /// </summary>
            public uint FrameCount;
        }

        /// <summary>
        /// Defines the push-constant layout for input normalization
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct NormalizePush
        {
            /// <summary>
            /// Byte offset of the input frame within the source buffer
            /// </summary>
            public uint InputByteOffset;

            /// <summary>
            /// Source row stride in bytes
            /// </summary>
            public uint InputStride;

            /// <summary>
            /// Output offset in float elements
            /// </summary>
            public uint OutputOffset;

            /// <summary>
            /// Input frame width in pixels
            /// </summary>
            public uint Width;

            /// <summary>
            /// Input frame height in pixels
            /// </summary>
            public uint Height;

            /// <summary>
            /// Scale applied while converting source samples to intensity
            /// </summary>
            public float IntensityScale;

            /// <summary>
            /// Stride between packed input frames in source bytes
            /// </summary>
            public uint InputFrameStride;

            /// <summary>
            /// Stride between packed normalized frames in float elements
            /// </summary>
            public uint OutputFrameStride;

            /// <summary>
            /// Number of frames processed by the packed dispatch
            /// </summary>
            public uint FrameCount;

            /// <summary>
            /// Encoded source pixel format
            /// </summary>
            public uint PixelFormat;

            /// <summary>
            /// Encoded matrix used to convert RGB values to grayscale
            /// </summary>
            public uint RgbToGrayMatrix;
        }

        /// <summary>
        /// Defines the push-constant layout for bilinear resizing
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct ResizePush
        {
            /// <summary>
            /// Input image offset in float elements
            /// </summary>
            public uint InputOffset;

            /// <summary>
            /// Output image offset in float elements
            /// </summary>
            public uint OutputOffset;

            /// <summary>
            /// Input image width in pixels
            /// </summary>
            public uint InputWidth;

            /// <summary>
            /// Input image height in pixels
            /// </summary>
            public uint InputHeight;

            /// <summary>
            /// Output image width in pixels
            /// </summary>
            public uint OutputWidth;

            /// <summary>
            /// Output image height in pixels
            /// </summary>
            public uint OutputHeight;

            /// <summary>
            /// Stride between packed input frames in float elements
            /// </summary>
            public uint InputFrameStride;

            /// <summary>
            /// Stride between packed output frames in float elements
            /// </summary>
            public uint OutputFrameStride;

            /// <summary>
            /// Number of frames processed by the packed dispatch
            /// </summary>
            public uint FrameCount;
        }

        /// <summary>
        /// Defines the push-constant layout for one Gaussian filter pass
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct GaussianPush
        {
            /// <summary>
            /// Input image or layer offset in float elements
            /// </summary>
            public uint InputOffset;

            /// <summary>
            /// Output image or layer offset in float elements
            /// </summary>
            public uint OutputOffset;

            /// <summary>
            /// Filtered image width in pixels
            /// </summary>
            public uint Width;

            /// <summary>
            /// Filtered image height in pixels
            /// </summary>
            public uint Height;

            /// <summary>
            /// Offset of the Gaussian weights in the weights buffer
            /// </summary>
            public uint FilterOffset;

            /// <summary>
            /// Number of weights in the selected Gaussian kernel
            /// </summary>
            public uint FilterLength;

            /// <summary>
            /// Selects horizontal or vertical filtering
            /// </summary>
            public uint Vertical;

            /// <summary>
            /// Stride between packed input frames in float elements
            /// </summary>
            public uint InputFrameStride;

            /// <summary>
            /// Stride between packed output frames in float elements
            /// </summary>
            public uint OutputFrameStride;

            /// <summary>
            /// Number of frames processed by the packed dispatch
            /// </summary>
            public uint FrameCount;
        }

        /// <summary>
        /// Defines the push-constant layout for Difference of Gaussians
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct DogPush
        {
            /// <summary>
            /// Offset of the first Gaussian layer in float elements
            /// </summary>
            public uint FirstOffset;

            /// <summary>
            /// Offset of the second Gaussian layer in float elements
            /// </summary>
            public uint SecondOffset;

            /// <summary>
            /// Offset of the output DoG layer in float elements
            /// </summary>
            public uint OutputOffset;

            /// <summary>
            /// Number of pixels processed by the dispatch
            /// </summary>
            public uint ElementCount;

            /// <summary>
            /// Stride between packed input frames in float elements
            /// </summary>
            public uint InputFrameStride;

            /// <summary>
            /// Stride between packed output frames in float elements
            /// </summary>
            public uint OutputFrameStride;

            /// <summary>
            /// Number of frames processed by the packed dispatch
            /// </summary>
            public uint FrameCount;
        }

        /// <summary>
        /// Defines the push-constant layout for gradient generation
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct GradientPush
        {
            /// <summary>
            /// Offset of the first Gaussian layer in float elements
            /// </summary>
            public uint PixelOffset;

            /// <summary>
            /// Octave width in pixels
            /// </summary>
            public uint Width;

            /// <summary>
            /// Octave height in pixels
            /// </summary>
            public uint Height;

            /// <summary>
            /// Number of Gaussian layers processed by the dispatch
            /// </summary>
            public uint LayerCount;

            /// <summary>
            /// Stride between packed Gaussian frames in float elements
            /// </summary>
            public uint InputFrameStride;

            /// <summary>
            /// Stride between packed gradient frames in pairs of floats
            /// </summary>
            public uint OutputFrameStride;

            /// <summary>
            /// Number of frames processed by the packed dispatch
            /// </summary>
            public uint FrameCount;

            /// <summary>
            /// Offset of the first gradient layer in pairs of floats
            /// </summary>
            public uint OutputOffset;
        }

        /// <summary>
        /// Defines the push-constant layout for hierarchical prefix scans
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PrefixPush
        {
            /// <summary>
            /// Input range offset in uint elements
            /// </summary>
            public uint InputOffset;

            /// <summary>
            /// Output range offset in uint elements
            /// </summary>
            public uint OutputOffset;

            /// <summary>
            /// Block-sum output offset in uint elements
            /// </summary>
            public uint BlockOffset;

            /// <summary>
            /// Number of elements processed by the operation
            /// </summary>
            public uint ElementCount;

            /// <summary>
            /// Prefix-scan operation selected by the shader
            /// </summary>
            public uint Operation;

            /// <summary>
            /// Stride between packed input frames in uint elements
            /// </summary>
            public uint InputFrameStride;

            /// <summary>
            /// Stride between packed output frames in uint elements
            /// </summary>
            public uint OutputFrameStride;

            /// <summary>
            /// Stride between packed block-sum frames in uint elements
            /// </summary>
            public uint BlockFrameStride;

            /// <summary>
            /// Number of frames processed by the packed dispatch
            /// </summary>
            public uint FrameCount;
        }

        /// <summary>
        /// Defines the push-constant layout for candidate compaction
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct CompactPush
        {
            /// <summary>
            /// Candidate-flag offset in uint elements
            /// </summary>
            public uint FlagOffset;

            /// <summary>
            /// Prefix-value offset in uint elements
            /// </summary>
            public uint PrefixOffset;

            /// <summary>
            /// Candidate-record offset in 32-byte elements
            /// </summary>
            public uint CandidateOffset;

            /// <summary>
            /// Output keypoint offset in 32-byte elements
            /// </summary>
            public uint OutputOffset;

            /// <summary>
            /// Number of candidates processed by the operation
            /// </summary>
            public uint ElementCount;

            /// <summary>
            /// Maximum number of output records available
            /// </summary>
            public uint Capacity;

            /// <summary>
            /// Counter offset in uint elements
            /// </summary>
            public uint CounterOffset;

            /// <summary>
            /// Stride between packed flag frames in uint elements
            /// </summary>
            public uint FlagFrameStride;

            /// <summary>
            /// Stride between packed prefix frames in uint elements
            /// </summary>
            public uint PrefixFrameStride;

            /// <summary>
            /// Stride between packed candidate frames in 32-byte elements
            /// </summary>
            public uint CandidateFrameStride;

            /// <summary>
            /// Stride between packed output frames in 32-byte elements
            /// </summary>
            public uint OutputFrameStride;

            /// <summary>
            /// Stride between packed counter frames in uint elements
            /// </summary>
            public uint CounterFrameStride;

            /// <summary>
            /// Number of frames processed by the packed dispatch
            /// </summary>
            public uint FrameCount;
        }

        /// <summary>
        /// Defines the push-constant layout for indirect dispatch generation
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct IndirectPush
        {
            /// <summary>
            /// Counter offset in uint elements
            /// </summary>
            public uint CounterOffset;

            /// <summary>
            /// Indirect-command offset in uint elements
            /// </summary>
            public uint CommandOffset;

            /// <summary>
            /// Local workgroup size encoded in the generated command
            /// </summary>
            public uint LocalSize;

            /// <summary>
            /// Multiplier applied to the counter value
            /// </summary>
            public uint Multiplier;

            /// <summary>
            /// Maximum feature capacity used to clamp the generated command
            /// </summary>
            public uint Capacity;
        }

        /// <summary>
        /// Defines the push-constant layout for orientation assignment
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct OrientationPush
        {
            /// <summary>
            /// Gaussian layer offset in float pairs
            /// </summary>
            public uint GaussianOffset;

            /// <summary>
            /// Keypoint offset in 32-byte elements
            /// </summary>
            public uint KeypointOffset;

            /// <summary>
            /// Oriented-candidate offset in 32-byte elements
            /// </summary>
            public uint CandidateOffset;

            /// <summary>
            /// Orientation flag offset in uint elements
            /// </summary>
            public uint FlagOffset;

            /// <summary>
            /// Maximum number of keypoints processed by the shader
            /// </summary>
            public uint KeypointCapacity;

            /// <summary>
            /// Octave width in pixels
            /// </summary>
            public uint Width;

            /// <summary>
            /// Octave height in pixels
            /// </summary>
            public uint Height;

            /// <summary>
            /// Number of pixels in one Gaussian layer
            /// </summary>
            public uint LayerStride;

            /// <summary>
            /// Indicates whether the input was resized into a second image plane
            /// </summary>
            public uint DoubleInput;

            /// <summary>
            /// Feature counter offset in uint elements
            /// </summary>
            public uint CounterOffset;

            /// <summary>
            /// Stride between packed gradient frames in float pairs
            /// </summary>
            public uint GradientFrameStride;

            /// <summary>
            /// Stride between packed keypoint frames in 32-byte elements
            /// </summary>
            public uint KeypointFrameStride;

            /// <summary>
            /// Stride between packed candidate frames in 32-byte elements
            /// </summary>
            public uint CandidateFrameStride;

            /// <summary>
            /// Stride between packed flag frames in uint elements
            /// </summary>
            public uint FlagFrameStride;

            /// <summary>
            /// Stride between packed counter frames in uint elements
            /// </summary>
            public uint CounterFrameStride;

            /// <summary>
            /// Number of frames processed by the packed dispatch
            /// </summary>
            public uint FrameCount;
        }

        /// <summary>
        /// Defines the push-constant layout for descriptor generation
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct DescriptorPush
        {
            /// <summary>
            /// Gaussian layer offset in float pairs
            /// </summary>
            public uint GaussianOffset;

            /// <summary>
            /// Oriented keypoint offset in 32-byte elements
            /// </summary>
            public uint KeypointOffset;

            /// <summary>
            /// Descriptor output offset in uint elements
            /// </summary>
            public uint DescriptorOffset;

            /// <summary>
            /// Maximum number of oriented keypoints processed by the shader
            /// </summary>
            public uint KeypointCapacity;

            /// <summary>
            /// Octave width in pixels
            /// </summary>
            public uint Width;

            /// <summary>
            /// Octave height in pixels
            /// </summary>
            public uint Height;

            /// <summary>
            /// Number of pixels in one Gaussian layer
            /// </summary>
            public uint LayerStride;

            /// <summary>
            /// Indicates whether the input was resized into a second image plane
            /// </summary>
            public uint DoubleInput;

            /// <summary>
            /// Feature counter offset in uint elements
            /// </summary>
            public uint CounterOffset;

            /// <summary>
            /// Stride between packed gradient frames in float pairs
            /// </summary>
            public uint GradientFrameStride;

            /// <summary>
            /// Stride between packed keypoint frames in 32-byte elements
            /// </summary>
            public uint KeypointFrameStride;

            /// <summary>
            /// Stride between packed descriptor frames in uint elements
            /// </summary>
            public uint DescriptorFrameStride;

            /// <summary>
            /// Stride between packed counter frames in uint elements
            /// </summary>
            public uint CounterFrameStride;

            /// <summary>
            /// Number of frames processed by the packed dispatch
            /// </summary>
            public uint FrameCount;
        }

        /// <summary>
        /// Defines the push-constant layout for radix sorting
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct RadixSortPush
        {
            /// <summary>
            /// Input keypoint offset in 32-byte elements
            /// </summary>
            public uint InputOffset;

            /// <summary>
            /// Output keypoint offset in 32-byte elements
            /// </summary>
            public uint OutputOffset;

            /// <summary>
            /// Number of keypoint records available to sort
            /// </summary>
            public uint Capacity;

            /// <summary>
            /// Feature counter offset in uint elements
            /// </summary>
            public uint CounterOffset;

            /// <summary>
            /// Keypoint field selected by the radix pass
            /// </summary>
            public uint KeySelector;

            /// <summary>
            /// Byte selected within the current keypoint field
            /// </summary>
            public uint ByteIndex;

            /// <summary>
            /// Radix operation selected by the shader
            /// </summary>
            public uint Operation;

            /// <summary>
            /// Scratch histogram or prefix offset in uint elements
            /// </summary>
            public uint ScratchOffset;

            /// <summary>
            /// Stride between packed input frames in 32-byte elements
            /// </summary>
            public uint InputFrameStride;

            /// <summary>
            /// Stride between packed output frames in 32-byte elements
            /// </summary>
            public uint OutputFrameStride;

            /// <summary>
            /// Stride between packed counter frames in uint elements
            /// </summary>
            public uint CounterFrameStride;

            /// <summary>
            /// Stride between packed scratch frames in uint elements
            /// </summary>
            public uint ScratchFrameStride;

            /// <summary>
            /// Number of frames processed by the packed dispatch
            /// </summary>
            public uint FrameCount;
        }

        /// <summary>
        /// Defines the push-constant layout for keypoint deduplication
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct DeduplicatePush
        {
            /// <summary>
            /// Sorted input keypoint offset in 32-byte elements
            /// </summary>
            public uint InputOffset;

            /// <summary>
            /// Deduplicated output keypoint offset in 32-byte elements
            /// </summary>
            public uint OutputOffset;

            /// <summary>
            /// Duplicate-mark flag offset in uint elements
            /// </summary>
            public uint FlagOffset;

            /// <summary>
            /// Prefix offset in uint elements
            /// </summary>
            public uint PrefixOffset;

            /// <summary>
            /// Maximum number of keypoint records processed
            /// </summary>
            public uint Capacity;

            /// <summary>
            /// Feature counter offset in uint elements
            /// </summary>
            public uint CounterOffset;

            /// <summary>
            /// Deduplication operation selected by the shader
            /// </summary>
            public uint Operation;

            /// <summary>
            /// Stride between packed input frames in 32-byte elements
            /// </summary>
            public uint InputFrameStride;

            /// <summary>
            /// Stride between packed output frames in 32-byte elements
            /// </summary>
            public uint OutputFrameStride;

            /// <summary>
            /// Stride between packed flag frames in uint elements
            /// </summary>
            public uint FlagFrameStride;

            /// <summary>
            /// Stride between packed prefix frames in uint elements
            /// </summary>
            public uint PrefixFrameStride;

            /// <summary>
            /// Stride between packed counter frames in uint elements
            /// </summary>
            public uint CounterFrameStride;

            /// <summary>
            /// Number of frames processed by the packed dispatch
            /// </summary>
            public uint FrameCount;
        }

        #endregion
    }
}
