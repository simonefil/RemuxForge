using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Pipelines;
using RemuxForge.Vulkan.Runtime;
using RemuxForge.Vulkan.Scheduling;
using RemuxForge.Vulkan.Vision.Matching;
using RemuxForge.Vulkan.Vision.Sift;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Vision.Geometry
{
    /// <summary>
    /// Executes deterministic RANSAC and classifies frame pairs by geometric consistency
    /// </summary>
    internal sealed unsafe class VulkanHomographyRansac
    {
        #region Fields

        /// <summary>
        /// Provides pipeline, memory, device and scheduler services used by the GPU workload
        /// </summary>
        private readonly VulkanRuntimeContext _runtime;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a homography RANSAC executor bound to a Vulkan runtime
        /// </summary>
        /// <param name="runtime">Runtime that owns the device, pipeline library, resource pool and scheduler</param>
        public VulkanHomographyRansac(VulkanRuntimeContext runtime)
        {
            this._runtime = runtime;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Executes GPU compaction, homography RANSAC and result readback for the supplied frame pairs
        /// </summary>
        /// <param name="firstFeatures">Resident SIFT features belonging to the first frame sequence</param>
        /// <param name="secondFeatures">Resident SIFT features belonging to the second frame sequence</param>
        /// <param name="firstFrames">First frame sequence used for dimensions and stable identifiers</param>
        /// <param name="secondFrames">Second frame sequence used for dimensions and stable identifiers</param>
        /// <param name="matches">Resident reciprocal matches and pair metadata consumed by RANSAC</param>
        /// <param name="options">RANSAC thresholds, deterministic seed and workload limits</param>
        /// <param name="diagnostics">Diagnostic counters updated while recording, submitting and reading back the workload</param>
        /// <param name="cancellationToken">Token used to cancel command submission or completion</param>
        /// <returns>Results in the same order as <paramref name="matches"/>.Pairs</returns>
        public List<VulkanSiftPairResult> Execute(
            VulkanSiftFeatureCollection firstFeatures,
            VulkanSiftFeatureCollection secondFeatures,
            IReadOnlyList<VulkanImageFrame> firstFrames,
            IReadOnlyList<VulkanImageFrame> secondFrames,
            VulkanMatchWorkspace matches,
            VulkanSiftOptions options,
            VulkanVisionDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            int pairCount = matches.Pairs.Count;
            string stage = "metadata";
            try
            {
                GeometryPairRecord[] metadata = new GeometryPairRecord[pairCount];
                for (int i = 0; i < pairCount; i++)
                {
                    VulkanFramePair pair = matches.Pairs[i];
                    VulkanMatchWorkspace.PairMatchRecord matchRecord = matches.MetadataRecords[i];
                    metadata[i] = new GeometryPairRecord
                    {
                        FirstFrame = (uint)pair.FirstFrameIndex,
                        SecondFrame = (uint)pair.SecondFrameIndex,
                        FirstKeypointOffset = (uint)firstFeatures.Frames[pair.FirstFrameIndex].CapacityOffset,
                        SecondKeypointOffset = (uint)secondFeatures.Frames[pair.SecondFrameIndex].CapacityOffset,
                        ReciprocalOffset = matchRecord.OutputOffset,
                        MatchCountOffset = checked((uint)(i * 2)),
                        HypothesisOffset = checked((uint)(i * options.RansacHypothesisCount)),
                        ResultIndex = (uint)i,
                        FirstWidth = (uint)firstFrames[pair.FirstFrameIndex].Width,
                        FirstHeight = (uint)firstFrames[pair.FirstFrameIndex].Height,
                        SecondWidth = (uint)secondFrames[pair.SecondFrameIndex].Width,
                        SecondHeight = (uint)secondFrames[pair.SecondFrameIndex].Height,
                        StableFirstFrame = checked((uint)firstFrames[pair.FirstFrameIndex].Identifier),
                        StableSecondFrame = checked((uint)secondFrames[pair.SecondFrameIndex].Identifier)
                    };
                }
                stage = "allocation";
                using (VulkanBufferLease metadataLease = this._runtime.ResourcePool.Rent(
                    checked((ulong)Math.Max(1, pairCount) * (ulong)Marshal.SizeOf<GeometryPairRecord>()),
                    VkBufferUsageFlags.StorageBuffer,
                    VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent))
                using (VulkanBufferLease compactMetadataLease = this._runtime.ResourcePool.Rent(
                    checked((ulong)Math.Max(1, pairCount) * (ulong)Marshal.SizeOf<GeometryPairRecord>()),
                    VkBufferUsageFlags.StorageBuffer,
                    VkMemoryPropertyFlags.DeviceLocal))
                using (VulkanBufferLease hypothesisLease = this._runtime.ResourcePool.Rent(
                    checked((ulong)Math.Max(1, pairCount) * (ulong)options.RansacHypothesisCount * 64UL),
                    VkBufferUsageFlags.StorageBuffer,
                    VkMemoryPropertyFlags.DeviceLocal))
                using (VulkanBufferLease resultLease = this._runtime.ResourcePool.Rent(
                    checked((ulong)Math.Max(1, pairCount) * 112UL),
                    VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferSrc,
                    VkMemoryPropertyFlags.DeviceLocal))
                using (VulkanBufferLease readbackLease = this._runtime.ResourcePool.Rent(
                    checked((ulong)Math.Max(1, pairCount) * 112UL),
                    VkBufferUsageFlags.TransferDst,
                    VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent))
                using (VulkanBufferLease controlLease = this._runtime.ResourcePool.Rent(
                    7UL * sizeof(uint),
                    VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.IndirectBuffer,
                    VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent))
                {
                    stage = "upload";
                    metadataLease.Buffer.Write<GeometryPairRecord>(metadata);
                    controlLease.Buffer.Write<uint>(new uint[7]);
                    VulkanComputePipeline compactPipeline = this._runtime.PipelineLibrary.Get("RansacCompactPairs", 7, 20, diagnostics);
                    VulkanComputePipeline pipeline = this._runtime.PipelineLibrary.Get("HomographyRansac", 9, 52, diagnostics);
                    stage = "submission";
                    using (VulkanDescriptorSetLease compactSet = this._runtime.PipelineLibrary.RentDescriptorSet(
                        compactPipeline,
                        new[]
                        {
                            firstFeatures.Counts,
                            secondFeatures.Counts,
                            matches.Counts,
                            metadataLease.Buffer,
                            compactMetadataLease.Buffer,
                            resultLease.Buffer,
                            controlLease.Buffer
                        }))
                    using (VulkanDescriptorSetLease descriptorSet = this._runtime.PipelineLibrary.RentDescriptorSet(
                        pipeline,
                        new[]
                        {
                            firstFeatures.Keypoints,
                            secondFeatures.Keypoints,
                            firstFeatures.Counts,
                            secondFeatures.Counts,
                            matches.ReciprocalMatches,
                            matches.Counts,
                            compactMetadataLease.Buffer,
                            hypothesisLease.Buffer,
                            resultLease.Buffer
                        }))
                    using (VulkanSubmission submission = this._runtime.Scheduler.Execute(commandBuffer =>
                    {
                        int ransacTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Ransac);
                        CompactPush compactPush = new CompactPush
                        {
                            PairCount = (uint)pairCount,
                            Operation = 0,
                            HypothesisCount = (uint)options.RansacHypothesisCount,
                            MinimumKeypointsPerFrame = (uint)options.MinimumKeypointsPerFrame,
                            MinimumReciprocalMatches = (uint)options.MinimumReciprocalMatches
                        };
                        this.BindCompact(commandBuffer, compactPipeline, compactSet, compactPush);
                        this._runtime.DeviceApi.vkCmdDispatch(commandBuffer, DivideRoundUp((uint)pairCount, 256), 1, 1);
                        diagnostics.DispatchCount++;
                        this.ComputeBarrier(commandBuffer);
                        compactPush.Operation = 1;
                        this.BindCompact(commandBuffer, compactPipeline, compactSet, compactPush);
                        this._runtime.DeviceApi.vkCmdDispatch(commandBuffer, 1, 1, 1);
                        diagnostics.DispatchCount++;
                        VkMemoryBarrier indirectBarrier = new VkMemoryBarrier
                        {
                            srcAccessMask = VkAccessFlags.ShaderWrite,
                            dstAccessMask = VkAccessFlags.IndirectCommandRead | VkAccessFlags.ShaderRead
                        };
                        this._runtime.DeviceApi.vkCmdPipelineBarrier(
                            commandBuffer,
                            VkPipelineStageFlags.ComputeShader,
                            VkPipelineStageFlags.DrawIndirect | VkPipelineStageFlags.ComputeShader,
                            VkDependencyFlags.None,
                            1,
                            &indirectBarrier,
                            0,
                            null,
                            0,
                            null);
                        RansacPush push = new RansacPush
                        {
                            PairCount = (uint)pairCount,
                            Operation = 0,
                            HypothesisCount = (uint)options.RansacHypothesisCount,
                            RandomSeed = options.RandomSeed,
                            ReprojectionThreshold = options.RansacReprojectionThreshold,
                            MinimumReciprocalMatches = (uint)options.MinimumReciprocalMatches,
                            MinimumInliers = (uint)options.MinimumInliers,
                            MinimumInlierRatio = options.MinimumInlierRatio,
                            MinimumCoverage = options.MinimumCoverage,
                            MaximumMeanError = options.MaximumMeanReprojectionError,
                            MinimumAreaRatio = options.MinimumHomographyAreaRatio,
                            MaximumAreaRatio = options.MaximumHomographyAreaRatio,
                            MinimumKeypointsPerFrame = (uint)options.MinimumKeypointsPerFrame
                        };
                        this.Bind(commandBuffer, pipeline, descriptorSet, push);
                        this._runtime.DeviceApi.vkCmdDispatchIndirect(commandBuffer, controlLease.Buffer.Buffer, sizeof(uint));
                        diagnostics.DispatchCount++;
                        this.ComputeBarrier(commandBuffer);
                        push.Operation = 1;
                        this.Bind(commandBuffer, pipeline, descriptorSet, push);
                        this._runtime.DeviceApi.vkCmdDispatchIndirect(commandBuffer, controlLease.Buffer.Buffer, 4UL * sizeof(uint));
                        diagnostics.DispatchCount++;
                        this._runtime.Scheduler.EndGpuPhase(commandBuffer, ransacTimestamp);
                        int readbackTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Readback);
                        VkMemoryBarrier transferBarrier = new VkMemoryBarrier
                        {
                            srcAccessMask = VkAccessFlags.ShaderWrite,
                            dstAccessMask = VkAccessFlags.TransferRead
                        };
                        this._runtime.DeviceApi.vkCmdPipelineBarrier(
                            commandBuffer,
                            VkPipelineStageFlags.ComputeShader,
                            VkPipelineStageFlags.Transfer,
                            VkDependencyFlags.None,
                            1,
                            &transferBarrier,
                            0,
                            null,
                            0,
                            null);
                        VkBufferCopy copy = new VkBufferCopy { size = checked((ulong)pairCount * 112UL) };
                        this._runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, resultLease.Buffer.Buffer, readbackLease.Buffer.Buffer, 1, &copy);
                        this._runtime.Scheduler.EndGpuPhase(commandBuffer, readbackTimestamp);
                    }, diagnostics, VulkanGpuPhase.None, cancellationToken))
                        submission.Wait(diagnostics, cancellationToken);
                    stage = "readback";
                    diagnostics.ReadbackBytes += checked((ulong)pairCount * 112UL);
                    long readbackStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    GpuPairResult[] gpuResults = readbackLease.Buffer.Read<GpuPairResult>(pairCount);
                    diagnostics.ReadbackTicks += System.Diagnostics.Stopwatch.GetTimestamp() - readbackStart;
                    List<VulkanSiftPairResult> results = new List<VulkanSiftPairResult>(pairCount);
                    for (int i = 0; i < pairCount; i++)
                    {
                        VulkanFramePair pair = matches.Pairs[i];
                        results.Add(Convert(
                            pair,
                            gpuResults[i],
                            firstFeatures.Frames[pair.FirstFrameIndex].Capacity,
                            secondFeatures.Frames[pair.SecondFrameIndex].Capacity));
                    }
                    return results;
                }
            }
            catch (OverflowException ex)
            {
                throw new VulkanDeviceLostException(
                    "Overflow aritmetico RANSAC nello stadio " + stage + ", coppie=" + pairCount + ", ipotesi=" + options.RansacHypothesisCount,
                    ex);
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// Binds the RANSAC pipeline, descriptor set and push constants to a command buffer
        /// </summary>
        /// <param name="commandBuffer">Command buffer that receives the binding commands</param>
        /// <param name="pipeline">RANSAC compute pipeline to bind</param>
        /// <param name="set">Descriptor set containing the resident feature, match and output buffers</param>
        /// <param name="push">RANSAC parameters copied into the shader push-constant range</param>
        private void Bind(VkCommandBuffer commandBuffer, VulkanComputePipeline pipeline, VulkanDescriptorSetLease set, RansacPush push)
        {
            this._runtime.DeviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, pipeline.Pipeline);
            VkDescriptorSet descriptorSet = set.DescriptorSet;
            this._runtime.DeviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, pipeline.PipelineLayout, 0, descriptorSet);
            this._runtime.DeviceApi.vkCmdPushConstants(commandBuffer, pipeline.PipelineLayout, VkShaderStageFlags.Compute, 0, 52, &push);
        }

        /// <summary>
        /// Binds the pair-compaction pipeline, descriptor set and push constants to a command buffer
        /// </summary>
        /// <param name="commandBuffer">Command buffer that receives the binding commands</param>
        /// <param name="pipeline">Pair-compaction compute pipeline to bind</param>
        /// <param name="set">Descriptor set containing metadata, counters and indirect-control buffers</param>
        /// <param name="push">Compaction parameters copied into the shader push-constant range</param>
        private void BindCompact(VkCommandBuffer commandBuffer, VulkanComputePipeline pipeline, VulkanDescriptorSetLease set, CompactPush push)
        {
            this._runtime.DeviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, pipeline.Pipeline);
            VkDescriptorSet descriptorSet = set.DescriptorSet;
            this._runtime.DeviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, pipeline.PipelineLayout, 0, descriptorSet);
            this._runtime.DeviceApi.vkCmdPushConstants(commandBuffer, pipeline.PipelineLayout, VkShaderStageFlags.Compute, 0, 20, &push);
        }

        /// <summary>
        /// Makes compute writes visible to subsequent compute dispatches
        /// </summary>
        /// <param name="commandBuffer">Command buffer that receives the barrier</param>
        private void ComputeBarrier(VkCommandBuffer commandBuffer)
        {
            VkMemoryBarrier barrier = new VkMemoryBarrier
            {
                srcAccessMask = VkAccessFlags.ShaderWrite,
                dstAccessMask = VkAccessFlags.ShaderRead | VkAccessFlags.ShaderWrite
            };
            this._runtime.DeviceApi.vkCmdPipelineBarrier(
                commandBuffer,
                VkPipelineStageFlags.ComputeShader,
                VkPipelineStageFlags.ComputeShader,
                VkDependencyFlags.None,
                1,
                &barrier,
                0,
                null,
                0,
                null);
        }

        /// <summary>
        /// Converts one GPU result record into the managed pair-result contract
        /// </summary>
        /// <param name="pair">Frame pair associated with the GPU record</param>
        /// <param name="value">Result record read from the shader output buffer</param>
        /// <param name="firstCapacity">Maximum valid keypoint count for the first frame</param>
        /// <param name="secondCapacity">Maximum valid keypoint count for the second frame</param>
        /// <returns>Managed status, counters, metrics and homography for the frame pair</returns>
        private static VulkanSiftPairResult Convert(VulkanFramePair pair, GpuPairResult value, int firstCapacity, int secondCapacity)
        {
            return new VulkanSiftPairResult
            {
                Pair = pair,
                Status = (VulkanSiftPairStatus)value.Header0.X,
                RejectReason = (VulkanSiftRejectReason)value.Header0.Y,
                FirstKeypointCount = ValidateCount(value.Header0.Z, firstCapacity, "firstKeypointCount", pair),
                SecondKeypointCount = ValidateCount(value.Header0.W, secondCapacity, "secondKeypointCount", pair),
                ForwardRatioMatchCount = ValidateCount(value.Header1.X, firstCapacity, "forwardRatioMatchCount", pair),
                ReciprocalMatchCount = ValidateCount(value.Header1.Y, firstCapacity, "reciprocalMatchCount", pair),
                InlierCount = ValidateCount(value.Header1.Z, firstCapacity, "inlierCount", pair),
                InlierRatio = value.Metrics0.X,
                FirstCoverage = value.Metrics0.Y,
                SecondCoverage = value.Metrics0.Z,
                MeanReprojectionError = value.Metrics0.W,
                Score = value.Metrics1.X,
                Homography = new[]
                {
                    value.Row0.X,
                    value.Row0.Y,
                    value.Row0.Z,
                    value.Row1.X,
                    value.Row1.Y,
                    value.Row1.Z,
                    value.Row2.X,
                    value.Row2.Y,
                    value.Row2.Z
                }
            };
        }

        /// <summary>
        /// Validates and narrows a GPU-produced count to the managed result range
        /// </summary>
        /// <param name="value">Unsigned count read from the GPU result record</param>
        /// <param name="maximum">Maximum count allowed by the resident buffer or pair contract</param>
        /// <param name="field">Managed field name used in the diagnostic exception</param>
        /// <param name="pair">Frame pair associated with the count</param>
        /// <returns>The validated count represented as an <see cref="int"/></returns>
        private static int ValidateCount(uint value, int maximum, string field, VulkanFramePair pair)
        {
            if (value > (uint)maximum)
                throw new VulkanDeviceLostException(
                    "Invalid RANSAC readback: " + field + "=" + value
                    + ", massimo=" + maximum + ", coppia=" + pair.FirstFrameIndex + "/" + pair.SecondFrameIndex);
            return (int)value;
        }

        /// <summary>
        /// Computes a ceiling division for an unsigned dispatch extent
        /// </summary>
        /// <param name="value">Number of elements that must be processed</param>
        /// <param name="divisor">Number of elements handled by one dispatch group</param>
        /// <returns>The smallest whole number of groups that covers <paramref name="value"/></returns>
        private static uint DivideRoundUp(uint value, uint divisor)
        {
            return checked((value + divisor - 1) / divisor);
        }

        #endregion

        #region Nested Structs

        /// <summary>
        /// Describes the offsets and frame geometry sent to the RANSAC shader
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct GeometryPairRecord
        {
            /// <summary>
            /// Index of the first frame in the first feature collection
            /// </summary>
            public uint FirstFrame;

            /// <summary>
            /// Index of the second frame in the second feature collection
            /// </summary>
            public uint SecondFrame;

            /// <summary>
            /// Element offset of the first frame keypoints in the resident keypoint buffer
            /// </summary>
            public uint FirstKeypointOffset;

            /// <summary>
            /// Element offset of the second frame keypoints in the resident keypoint buffer
            /// </summary>
            public uint SecondKeypointOffset;

            /// <summary>
            /// Element offset of the pair's reciprocal matches
            /// </summary>
            public uint ReciprocalOffset;

            /// <summary>
            /// Element offset of the pair's match counters
            /// </summary>
            public uint MatchCountOffset;

            /// <summary>
            /// Element offset of the pair's RANSAC hypotheses
            /// </summary>
            public uint HypothesisOffset;

            /// <summary>
            /// Index of the pair's result record in the output buffer
            /// </summary>
            public uint ResultIndex;

            /// <summary>
            /// Width of the first frame in pixels
            /// </summary>
            public uint FirstWidth;

            /// <summary>
            /// Height of the first frame in pixels
            /// </summary>
            public uint FirstHeight;

            /// <summary>
            /// Width of the second frame in pixels
            /// </summary>
            public uint SecondWidth;

            /// <summary>
            /// Height of the second frame in pixels
            /// </summary>
            public uint SecondHeight;

            /// <summary>
            /// Stable identifier of the first frame used by deterministic sampling
            /// </summary>
            public uint StableFirstFrame;

            /// <summary>
            /// Stable identifier of the second frame used by deterministic sampling
            /// </summary>
            public uint StableSecondFrame;
        }

        /// <summary>
        /// Defines the push constants consumed by the homography RANSAC shader
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct RansacPush
        {
            /// <summary>
            /// Number of frame pairs in the dispatch
            /// </summary>
            public uint PairCount;

            /// <summary>
            /// Shader operation selected for hypothesis generation or result evaluation
            /// </summary>
            public uint Operation;

            /// <summary>
            /// Number of RANSAC hypotheses allocated per pair
            /// </summary>
            public uint HypothesisCount;

            /// <summary>
            /// Seed used by deterministic per-pair hypothesis sampling
            /// </summary>
            public uint RandomSeed;

            /// <summary>
            /// Reprojection distance threshold used to classify inliers
            /// </summary>
            public float ReprojectionThreshold;

            /// <summary>
            /// Minimum reciprocal match count required before RANSAC acceptance
            /// </summary>
            public uint MinimumReciprocalMatches;

            /// <summary>
            /// Minimum inlier count required for an accepted homography
            /// </summary>
            public uint MinimumInliers;

            /// <summary>
            /// Minimum ratio of inliers to reciprocal matches
            /// </summary>
            public float MinimumInlierRatio;

            /// <summary>
            /// Minimum geometric coverage required on both frames
            /// </summary>
            public float MinimumCoverage;

            /// <summary>
            /// Maximum mean reprojection error allowed for an accepted homography
            /// </summary>
            public float MaximumMeanError;

            /// <summary>
            /// Minimum projected-area ratio allowed for an accepted homography
            /// </summary>
            public float MinimumAreaRatio;

            /// <summary>
            /// Maximum projected-area ratio allowed for an accepted homography
            /// </summary>
            public float MaximumAreaRatio;

            /// <summary>
            /// Minimum keypoint count required in each frame
            /// </summary>
            public uint MinimumKeypointsPerFrame;
        }

        /// <summary>
        /// Defines the push constants consumed by the pair-compaction shader
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct CompactPush
        {
            /// <summary>
            /// Number of frame pairs in the dispatch
            /// </summary>
            public uint PairCount;

            /// <summary>
            /// Shader operation selected for pair filtering or indirect-count generation
            /// </summary>
            public uint Operation;

            /// <summary>
            /// Number of RANSAC hypotheses allocated per pair
            /// </summary>
            public uint HypothesisCount;

            /// <summary>
            /// Minimum keypoint count required in each frame
            /// </summary>
            public uint MinimumKeypointsPerFrame;

            /// <summary>
            /// Minimum reciprocal match count required for a pair to enter RANSAC
            /// </summary>
            public uint MinimumReciprocalMatches;
        }

        /// <summary>
        /// Represents four unsigned 32-bit lanes in a GPU result record
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct UInt4
        {
            /// <summary>
            /// First unsigned lane in the shader vector
            /// </summary>
            public uint X;

            /// <summary>
            /// Second unsigned lane in the shader vector
            /// </summary>
            public uint Y;

            /// <summary>
            /// Third unsigned lane in the shader vector
            /// </summary>
            public uint Z;

            /// <summary>
            /// Fourth unsigned lane in the shader vector
            /// </summary>
            public uint W;
        }

        /// <summary>
        /// Represents four single-precision lanes in a GPU result record
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct Float4
        {
            /// <summary>
            /// First floating-point lane in the shader vector
            /// </summary>
            public float X;

            /// <summary>
            /// Second floating-point lane in the shader vector
            /// </summary>
            public float Y;

            /// <summary>
            /// Third floating-point lane in the shader vector
            /// </summary>
            public float Z;

            /// <summary>
            /// Fourth floating-point lane in the shader vector
            /// </summary>
            public float W;
        }

        /// <summary>
        /// Represents the complete 112-byte result record produced for one frame pair
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct GpuPairResult
        {
            /// <summary>
            /// Status, rejection reason and primary keypoint counts
            /// </summary>
            public UInt4 Header0;

            /// <summary>
            /// Match and inlier counts associated with the pair
            /// </summary>
            public UInt4 Header1;

            /// <summary>
            /// Inlier ratio, frame coverage and mean reprojection error
            /// </summary>
            public Float4 Metrics0;

            /// <summary>
            /// Pair score and reserved metric lanes
            /// </summary>
            public Float4 Metrics1;

            /// <summary>
            /// First row of the returned homography matrix
            /// </summary>
            public Float4 Row0;

            /// <summary>
            /// Second row of the returned homography matrix
            /// </summary>
            public Float4 Row1;

            /// <summary>
            /// Third row of the returned homography matrix
            /// </summary>
            public Float4 Row2;
        }

        #endregion
    }
}
