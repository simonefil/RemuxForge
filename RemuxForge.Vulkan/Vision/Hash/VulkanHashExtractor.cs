using RemuxForge.Vulkan.Memory;
using RemuxForge.Vulkan.Pipelines;
using RemuxForge.Vulkan.Runtime;
using RemuxForge.Vulkan.Scheduling;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Vulkan;

namespace RemuxForge.Vulkan.Vision.Hash
{
    /// <summary>
    /// Computes the perceptual hash of every analysis square on the device
    /// </summary>
    internal sealed unsafe class VulkanHashExtractor
    {
        #region Constants

        /// <summary>
        /// Frames hashed by a single submission, one workgroup each
        /// </summary>
        public const int FRAMES_PER_SUBMISSION = 12288;

        /// <summary>
        /// Side in pixels of the analysis square required by the extraction shader
        /// </summary>
        public const int FRAME_SIDE = 72;

        /// <summary>
        /// Size in bytes of one analysis square
        /// </summary>
        public const int FRAME_BYTES = FRAME_SIDE * FRAME_SIDE;

        /// <summary>
        /// Number of 32-bit output words produced for each frame
        /// </summary>
        private const int SIGNAL_WORDS = 42;

        /// <summary>
        /// Number of packed thumbnail bytes produced for each frame
        /// </summary>
        private const int THUMBNAIL_BYTES = 144;

        #endregion

        #region Class Fields

        /// <summary>
        /// Provides the device, pipeline, scheduler, and resource-pool services used to record extraction work
        /// </summary>
        private readonly VulkanRuntimeContext _runtime;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes an extractor backed by the specified Vulkan runtime
        /// </summary>
        /// <param name="runtime">Runtime services used to access the Vulkan device and shared resources</param>
        public VulkanHashExtractor(VulkanRuntimeContext runtime)
        {
            this._runtime = runtime;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Hashes a sequence of tightly packed grayscale analysis squares
        /// </summary>
        /// <param name="frames">Analysis squares, each one holding one byte per pixel in row order</param>
        /// <param name="frameCount">Number of analysis squares contained in the sequence</param>
        /// <param name="diagnostics">Diagnostics updated with dispatch, upload and readback activity</param>
        /// <param name="cancellationToken">Token used while waiting for the recorded submissions to complete</param>
        /// <returns>The signals of every analysis square, in the order they were provided</returns>
        public VulkanFrameSignalBatch Extract(ReadOnlySpan<byte> frames, int frameCount, VulkanVisionDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            VulkanFrameHash[] hashes = new VulkanFrameHash[frameCount];
            float[] lumaMeans = new float[frameCount];
            float[] thumbnailStandardDeviations = new float[frameCount];
            byte[] thumbnailPixels = new byte[checked(frameCount * THUMBNAIL_BYTES)];
            VulkanComputePipeline pipeline = this._runtime.PipelineLibrary.Get("HashExtract", 2, (uint)Marshal.SizeOf<ExtractPush>(), diagnostics);
            for (int start = 0; start < frameCount; start += FRAMES_PER_SUBMISSION)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(FRAMES_PER_SUBMISSION, frameCount - start);
                ulong frameBytes = checked((ulong)count * FRAME_BYTES);
                ulong signalBytes = checked((ulong)count * SIGNAL_WORDS * sizeof(uint));
                using (VulkanBufferLease stagingLease = this._runtime.ResourcePool.Rent(frameBytes, VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent))
                using (VulkanBufferLease frameLease = this._runtime.ResourcePool.Rent(frameBytes, VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.DeviceLocal))
                using (VulkanBufferLease signalLease = this._runtime.ResourcePool.Rent(signalBytes, VkBufferUsageFlags.StorageBuffer | VkBufferUsageFlags.TransferSrc, VkMemoryPropertyFlags.DeviceLocal))
                using (VulkanBufferLease readbackLease = this._runtime.ResourcePool.Rent(signalBytes, VkBufferUsageFlags.TransferDst, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent))
                {
                    long uploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    stagingLease.Buffer.Write<byte>(frames.Slice(start * FRAME_BYTES, count * FRAME_BYTES));
                    diagnostics.UploadTicks += System.Diagnostics.Stopwatch.GetTimestamp() - uploadStart;
                    diagnostics.UploadedBytes += frameBytes;
                    VulkanBuffer staging = stagingLease.Buffer;
                    VulkanBuffer deviceFrames = frameLease.Buffer;
                    VulkanBuffer deviceSignals = signalLease.Buffer;
                    VulkanBuffer readback = readbackLease.Buffer;
                    using (VulkanDescriptorSetLease descriptorSet = this._runtime.PipelineLibrary.RentDescriptorSet(pipeline, new[] { deviceFrames, deviceSignals }))
                    using (VulkanSubmission submission = this._runtime.Scheduler.Execute(commandBuffer =>
                    {
                        int uploadTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Upload);
                        VkBufferCopy upload = new VkBufferCopy { srcOffset = staging.BindingOffset, dstOffset = deviceFrames.BindingOffset, size = frameBytes };
                        this._runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, staging.Buffer, deviceFrames.Buffer, 1, &upload);
                        this._runtime.Scheduler.EndGpuPhase(commandBuffer, uploadTimestamp);
                        VkMemoryBarrier uploadBarrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.TransferWrite, dstAccessMask = VkAccessFlags.ShaderRead };
                        this._runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.ComputeShader, VkDependencyFlags.None, 1, &uploadBarrier, 0, null, 0, null);
                        int extractTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Descriptor);
                        ExtractPush push = new ExtractPush { FrameCount = (uint)count };
                        this._runtime.DeviceApi.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, pipeline.Pipeline);
                        VkDescriptorSet boundSet = descriptorSet.DescriptorSet;
                        this._runtime.DeviceApi.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, pipeline.PipelineLayout, 0, boundSet);
                        this._runtime.DeviceApi.vkCmdPushConstants(commandBuffer, pipeline.PipelineLayout, VkShaderStageFlags.Compute, 0, (uint)sizeof(ExtractPush), &push);
                        this._runtime.DeviceApi.vkCmdDispatch(commandBuffer, (uint)count, 1, 1);
                        diagnostics.DispatchCount++;
                        this._runtime.Scheduler.EndGpuPhase(commandBuffer, extractTimestamp);
                        VkMemoryBarrier readbackBarrier = new VkMemoryBarrier { srcAccessMask = VkAccessFlags.ShaderWrite, dstAccessMask = VkAccessFlags.TransferRead };
                        this._runtime.DeviceApi.vkCmdPipelineBarrier(commandBuffer, VkPipelineStageFlags.ComputeShader, VkPipelineStageFlags.Transfer, VkDependencyFlags.None, 1, &readbackBarrier, 0, null, 0, null);
                        int readbackTimestamp = this._runtime.Scheduler.BeginGpuPhase(commandBuffer, VulkanGpuPhase.Readback);
                        VkBufferCopy download = new VkBufferCopy { srcOffset = deviceSignals.BindingOffset, dstOffset = readback.BindingOffset, size = signalBytes };
                        this._runtime.DeviceApi.vkCmdCopyBuffer(commandBuffer, deviceSignals.Buffer, readback.Buffer, 1, &download);
                        this._runtime.Scheduler.EndGpuPhase(commandBuffer, readbackTimestamp);
                    }, diagnostics, VulkanGpuPhase.None, cancellationToken))
                        submission.Wait(diagnostics, cancellationToken);

                    long readStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    uint[] records = readback.Read<uint>(checked(count * SIGNAL_WORDS));
                    diagnostics.ReadbackTicks += System.Diagnostics.Stopwatch.GetTimestamp() - readStart;
                    diagnostics.ReadbackBytes += signalBytes;
                    for (int i = 0; i < count; i++)
                    {
                        int resultIndex = start + i;
                        int hashOffset = i * 4;
                        hashes[resultIndex] = new VulkanFrameHash(((ulong)records[hashOffset] << 32) | records[hashOffset + 1], ((ulong)records[hashOffset + 2] << 32) | records[hashOffset + 3]);
                        int measurementOffset = count * 4 + i * 2;
                        lumaMeans[resultIndex] = BitConverter.Int32BitsToSingle((int)records[measurementOffset]);
                        thumbnailStandardDeviations[resultIndex] = BitConverter.Int32BitsToSingle((int)records[measurementOffset + 1]);
                        int thumbnailOffset = count * 6 + i * 36;
                        int thumbnailResultOffset = resultIndex * THUMBNAIL_BYTES;
                        for (int word = 0; word < 36; word++)
                        {
                            uint packed = records[thumbnailOffset + word];
                            int pixelOffset = thumbnailResultOffset + word * 4;
                            thumbnailPixels[pixelOffset] = (byte)packed;
                            thumbnailPixels[pixelOffset + 1] = (byte)(packed >> 8);
                            thumbnailPixels[pixelOffset + 2] = (byte)(packed >> 16);
                            thumbnailPixels[pixelOffset + 3] = (byte)(packed >> 24);
                        }
                    }
                }
            }
            return new VulkanFrameSignalBatch(hashes, lumaMeans, thumbnailStandardDeviations, thumbnailPixels);
        }

        #endregion

        #region Nested Types

        /// <summary>
        /// Carries per-dispatch parameters for the hash extraction shader
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct ExtractPush
        {
            /// <summary>
            /// Number of analysis squares processed by the dispatch
            /// </summary>
            public uint FrameCount;
        }

        #endregion
    }
}
