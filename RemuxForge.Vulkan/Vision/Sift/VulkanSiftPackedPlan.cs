using RemuxForge.Vulkan.Memory;
using System;
using System.Collections.Generic;

namespace RemuxForge.Vulkan.Vision.Sift
{
    /// <summary>
    /// Calculates aligned offsets and buffer capacities for a packed SIFT batch
    /// </summary>
    internal sealed class VulkanSiftPackedPlan
    {
        #region Costruttore

        /// <summary>
        /// Builds the shared buffer layout for a packed batch of SIFT frames
        /// </summary>
        /// <param name="frames">Frames that will share the packed buffers</param>
        /// <param name="plans">Per-frame SIFT plans corresponding to <paramref name="frames"/> in the same order</param>
        /// <param name="storageBufferOffsetAlignment">Required alignment, in bytes, for storage-buffer offsets; zero is treated as one</param>
        public VulkanSiftPackedPlan(IReadOnlyList<VulkanImageFrame> frames, IReadOnlyList<VulkanSiftPlan> plans, ulong storageBufferOffsetAlignment)
        {
            ArgumentNullException.ThrowIfNull(frames);
            ArgumentNullException.ThrowIfNull(plans);
            if (frames.Count == 0)
                throw new ArgumentException("A packed batch must contain at least one frame.", nameof(frames));
            if (frames.Count != plans.Count)
                throw new ArgumentException("Frames and SIFT plans must have the same count.", nameof(plans));
            if (storageBufferOffsetAlignment == 0)
                storageBufferOffsetAlignment = 1;

            VulkanImageFrame referenceFrame = frames[0];
            VulkanSiftPlan referencePlan = plans[0] ?? throw new ArgumentException("A SIFT plan cannot be null.", nameof(plans));
            List<VulkanSiftPackedFramePlan> framePlans = new List<VulkanSiftPackedFramePlan>(frames.Count);
            ulong inputByteOffset = 0;
            uint inputFloatOffset = 0;
            uint temporaryFloatOffset = 0;
            uint gaussianFloatOffset = 0;
            uint gradientFloatOffset = 0;
            uint dogFloatOffset = 0;
            uint flagOffset = 0;
            uint scanScratchOffset = 0;
            uint candidateOffset = 0;
            uint keypointOffset = 0;
            uint orientedKeypointOffset = 0;
            uint descriptorOffset = 0;
            uint counterOffset = 0;
            uint indirectCommandOffset = 0;

            for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
            {
                VulkanImageFrame frame = frames[frameIndex] ?? throw new ArgumentException("A frame cannot be null.", nameof(frames));
                VulkanSiftPlan plan = plans[frameIndex] ?? throw new ArgumentException("A SIFT plan cannot be null.", nameof(plans));
                this.ValidateCompatibility(referenceFrame, referencePlan, frame, plan, frameIndex);
                int flagElements = Math.Max(plan.CandidateCount, plan.OrientationCapacity);
                int candidateElements = Math.Max(plan.CandidateCount, plan.OrientationCapacity);
                int counterElements = checked(plan.Octaves.Count * 5 + 5);
                int indirectCommandElements = checked(plan.Octaves.Count * 6);
                ulong inputBytes = checked((ulong)frame.Stride * (ulong)frame.Height);

                framePlans.Add(new VulkanSiftPackedFramePlan(
                    inputByteOffset,
                    inputFloatOffset,
                    temporaryFloatOffset,
                    gaussianFloatOffset,
                    gradientFloatOffset,
                    dogFloatOffset,
                    flagOffset,
                    scanScratchOffset,
                    candidateOffset,
                    keypointOffset,
                    orientedKeypointOffset,
                    descriptorOffset,
                    counterOffset,
                    indirectCommandOffset));

                inputByteOffset = AlignUp(checked(inputByteOffset + inputBytes), Math.Max((ulong)sizeof(uint), storageBufferOffsetAlignment));
                inputFloatOffset = AddElements(inputFloatOffset, plan.InputFloatElements, sizeof(float), storageBufferOffsetAlignment);
                temporaryFloatOffset = AddElements(temporaryFloatOffset, plan.TemporaryFloatElements, sizeof(float), storageBufferOffsetAlignment);
                gaussianFloatOffset = AddElements(gaussianFloatOffset, plan.GaussianFloatElements, sizeof(float), storageBufferOffsetAlignment);
                gradientFloatOffset = AddElements(gradientFloatOffset, checked(plan.GaussianFloatElements * 2), sizeof(float), storageBufferOffsetAlignment);
                dogFloatOffset = AddElements(dogFloatOffset, plan.DogFloatElements, sizeof(float), storageBufferOffsetAlignment);
                flagOffset = AddElements(flagOffset, flagElements, sizeof(uint), storageBufferOffsetAlignment);
                scanScratchOffset = AddElements(scanScratchOffset, plan.ScanScratchElements, sizeof(uint), storageBufferOffsetAlignment);
                candidateOffset = AddElements(candidateOffset, candidateElements, 32, storageBufferOffsetAlignment);
                keypointOffset = AddElements(keypointOffset, plan.FeatureCapacity, 32, storageBufferOffsetAlignment);
                orientedKeypointOffset = AddElements(orientedKeypointOffset, plan.OrientationCapacity, 32, storageBufferOffsetAlignment);
                descriptorOffset = AddElements(descriptorOffset, checked(plan.OrientationCapacity * 32), sizeof(uint), storageBufferOffsetAlignment);
                counterOffset = AddElements(counterOffset, counterElements, sizeof(uint), storageBufferOffsetAlignment);
                indirectCommandOffset = AddElements(indirectCommandOffset, indirectCommandElements, sizeof(uint), storageBufferOffsetAlignment);
            }

            this.Frames = framePlans;
            this.InputBytes = inputByteOffset;
            this.InputFloatElements = inputFloatOffset;
            this.TemporaryFloatElements = temporaryFloatOffset;
            this.GaussianFloatElements = gaussianFloatOffset;
            this.GradientFloatElements = gradientFloatOffset;
            this.DogFloatElements = dogFloatOffset;
            this.FlagElements = flagOffset;
            this.ScanScratchElements = scanScratchOffset;
            this.CandidateElements = candidateOffset;
            this.KeypointElements = keypointOffset;
            this.OrientedKeypointElements = orientedKeypointOffset;
            this.DescriptorElements = descriptorOffset;
            this.CounterElements = counterOffset;
            this.IndirectCommandElements = indirectCommandOffset;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Returns the total pooled workspace reservation required after resource-pool size-class rounding
        /// </summary>
        /// <returns>The sum of the pooled allocation sizes for all logical packed buffers</returns>
        public ulong GetDeviceWorkspaceBytes()
        {
            ulong result = this.GetPooledBytes(this.InputBytes);
            result = checked(result + this.GetPooledBytes((ulong)this.InputFloatElements * sizeof(float)));
            result = checked(result + this.GetPooledBytes((ulong)this.TemporaryFloatElements * sizeof(float)));
            result = checked(result + this.GetPooledBytes((ulong)this.GaussianFloatElements * sizeof(float)));
            result = checked(result + this.GetPooledBytes((ulong)this.GradientFloatElements * sizeof(float)));
            result = checked(result + this.GetPooledBytes((ulong)this.DogFloatElements * sizeof(float)));
            result = checked(result + this.GetPooledBytes((ulong)this.FlagElements * sizeof(uint)) * 2UL);
            result = checked(result + this.GetPooledBytes((ulong)this.ScanScratchElements * sizeof(uint)));
            result = checked(result + this.GetPooledBytes((ulong)this.CandidateElements * 32UL));
            result = checked(result + this.GetPooledBytes((ulong)this.KeypointElements * 32UL) * 2UL);
            result = checked(result + this.GetPooledBytes((ulong)this.OrientedKeypointElements * 32UL));
            result = checked(result + this.GetPooledBytes((ulong)this.DescriptorElements * sizeof(uint)));
            result = checked(result + this.GetPooledBytes((ulong)this.CounterElements * sizeof(uint)));
            result = checked(result + this.GetPooledBytes((ulong)this.IndirectCommandElements * sizeof(uint)));
            return result;
        }

        /// <summary>
        /// Returns the largest individual device buffer required by the packed layout
        /// </summary>
        /// <returns>The maximum unrounded byte size among the logical packed buffers</returns>
        public ulong GetMaximumDeviceBufferBytes()
        {
            ulong result = this.InputBytes;
            result = Math.Max(result, checked((ulong)this.InputFloatElements * sizeof(float)));
            result = Math.Max(result, checked((ulong)this.TemporaryFloatElements * sizeof(float)));
            result = Math.Max(result, checked((ulong)this.GaussianFloatElements * sizeof(float)));
            result = Math.Max(result, checked((ulong)this.GradientFloatElements * sizeof(float)));
            result = Math.Max(result, checked((ulong)this.DogFloatElements * sizeof(float)));
            result = Math.Max(result, checked((ulong)this.FlagElements * sizeof(uint)));
            result = Math.Max(result, checked((ulong)this.ScanScratchElements * sizeof(uint)));
            result = Math.Max(result, checked((ulong)this.CandidateElements * 32UL));
            result = Math.Max(result, checked((ulong)this.KeypointElements * 32UL));
            result = Math.Max(result, checked((ulong)this.OrientedKeypointElements * 32UL));
            result = Math.Max(result, checked((ulong)this.DescriptorElements * sizeof(uint)));
            result = Math.Max(result, checked((ulong)this.CounterElements * sizeof(uint)));
            return Math.Max(result, checked((ulong)this.IndirectCommandElements * sizeof(uint)));
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Resolves a logical allocation size to the resource pool's size class
        /// </summary>
        /// <param name="minimumBytes">Minimum number of bytes required by the logical buffer</param>
        /// <returns>The size class used by the resource pool</returns>
        private ulong GetPooledBytes(ulong minimumBytes)
        {
            return VulkanResourcePool.ResolveSizeClass(Math.Max(1UL, minimumBytes));
        }

        /// <summary>
        /// Advances an element offset by a count and rounds the resulting byte offset to both required boundaries
        /// </summary>
        /// <param name="current">Current offset expressed in elements</param>
        /// <param name="count">Number of elements to reserve</param>
        /// <param name="elementSize">Size of one element in bytes</param>
        /// <param name="alignment">Required byte alignment for the next segment</param>
        /// <returns>The aligned end offset expressed in elements</returns>
        private static uint AddElements(uint current, int count, int elementSize, ulong alignment)
        {
            ulong endBytes = checked((checked((ulong)current) + checked((uint)count)) * checked((uint)elementSize));
            ulong alignedBytes = AlignUp(endBytes, alignment);
            if (alignedBytes % checked((uint)elementSize) != 0)
                alignedBytes = AlignUp(alignedBytes, checked((uint)elementSize));
            return checked((uint)(alignedBytes / checked((uint)elementSize)));
        }

        /// <summary>
        /// Rounds a byte offset up to the next multiple of an alignment
        /// </summary>
        /// <param name="value">Byte offset to round</param>
        /// <param name="alignment">Positive alignment in bytes</param>
        /// <returns>The smallest aligned value that is greater than or equal to <paramref name="value"/></returns>
        private static ulong AlignUp(ulong value, ulong alignment)
        {
            return checked((value + alignment - 1UL) / alignment * alignment);
        }

        /// <summary>
        /// Verifies that a frame and its SIFT plan can use the layout established by the first batch item
        /// </summary>
        /// <param name="referenceFrame">First frame in the batch, used as the geometry and pixel-format reference</param>
        /// <param name="referencePlan">First SIFT plan in the batch, used as the shared-layout reference</param>
        /// <param name="frame">Frame currently being validated</param>
        /// <param name="plan">SIFT plan currently being validated</param>
        /// <param name="frameIndex">Zero-based index of the current frame in the batch</param>
        private void ValidateCompatibility(VulkanImageFrame referenceFrame, VulkanSiftPlan referencePlan, VulkanImageFrame frame, VulkanSiftPlan plan, int frameIndex)
        {
            bool compatible = frame.Width == referenceFrame.Width
                && frame.Height == referenceFrame.Height
                && frame.Stride == referenceFrame.Stride
                && frame.PixelFormat == referenceFrame.PixelFormat
                && frame.RgbToGrayMatrix == referenceFrame.RgbToGrayMatrix
                && plan.OctaveLayers == referencePlan.OctaveLayers
                && plan.InputFloatElements == referencePlan.InputFloatElements
                && plan.TemporaryFloatElements == referencePlan.TemporaryFloatElements
                && plan.GaussianFloatElements == referencePlan.GaussianFloatElements
                && plan.DogFloatElements == referencePlan.DogFloatElements
                && plan.CandidateCount == referencePlan.CandidateCount
                && plan.FeatureCapacity == referencePlan.FeatureCapacity
                && plan.OrientationCapacity == referencePlan.OrientationCapacity
                && plan.ScanScratchElements == referencePlan.ScanScratchElements
                && plan.Octaves.Count == referencePlan.Octaves.Count;
            if (!compatible)
                throw new ArgumentException($"Packed frame {frameIndex} does not have uniform SIFT geometry and layout.", nameof(plan));
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the per-frame offsets in source-list order
        /// </summary>
        public IReadOnlyList<VulkanSiftPackedFramePlan> Frames { get; }

        /// <summary>
        /// Gets the total byte span reserved for packed input frames, including alignment padding
        /// </summary>
        public ulong InputBytes { get; }

        /// <summary>
        /// Gets the total number of float elements reserved for packed normalized input
        /// </summary>
        public uint InputFloatElements { get; }

        /// <summary>
        /// Gets the total number of float elements reserved for temporary SIFT processing data
        /// </summary>
        public uint TemporaryFloatElements { get; }

        /// <summary>
        /// Gets the total number of float elements reserved for the Gaussian pyramid
        /// </summary>
        public uint GaussianFloatElements { get; }

        /// <summary>
        /// Gets the total number of float elements reserved for image gradients
        /// </summary>
        public uint GradientFloatElements { get; }

        /// <summary>
        /// Gets the total number of float elements reserved for difference-of-Gaussians data
        /// </summary>
        public uint DogFloatElements { get; }

        /// <summary>
        /// Gets the number of flag elements reserved for both the flag and prefix buffers
        /// </summary>
        public uint FlagElements { get; }

        /// <summary>
        /// Gets the total number of unsigned-integer elements reserved for prefix-scan scratch data
        /// </summary>
        public uint ScanScratchElements { get; }

        /// <summary>
        /// Gets the total number of 32-byte candidate records reserved for the packed batch
        /// </summary>
        public uint CandidateElements { get; }

        /// <summary>
        /// Gets the total number of 32-byte keypoint records reserved for the packed batch
        /// </summary>
        public uint KeypointElements { get; }

        /// <summary>
        /// Gets the total number of 32-byte oriented-keypoint records reserved for the packed batch
        /// </summary>
        public uint OrientedKeypointElements { get; }

        /// <summary>
        /// Gets the total number of unsigned-integer elements reserved for descriptors
        /// </summary>
        public uint DescriptorElements { get; }

        /// <summary>
        /// Gets the total number of unsigned-integer elements reserved for per-octave counters
        /// </summary>
        public uint CounterElements { get; }

        /// <summary>
        /// Gets the total number of unsigned-integer elements reserved for indirect dispatch commands
        /// </summary>
        public uint IndirectCommandElements { get; }

        #endregion
    }

    /// <summary>
    /// Stores the offsets of one frame within the shared packed buffers
    /// </summary>
    internal sealed class VulkanSiftPackedFramePlan
    {
        #region Costruttore

        /// <summary>
        /// Creates an offset plan for one frame in a packed SIFT batch
        /// </summary>
        /// <param name="inputByteOffset">Byte offset of the frame's input pixels</param>
        /// <param name="inputFloatOffset">Float-element offset of the frame's normalized input</param>
        /// <param name="temporaryFloatOffset">Float-element offset of the frame's temporary processing data</param>
        /// <param name="gaussianFloatOffset">Float-element offset of the frame's Gaussian pyramid</param>
        /// <param name="gradientFloatOffset">Float-element offset of the frame's gradient data</param>
        /// <param name="dogFloatOffset">Float-element offset of the frame's difference-of-Gaussians data</param>
        /// <param name="flagOffset">Unsigned-integer offset of the frame's flag and prefix data</param>
        /// <param name="scanScratchOffset">Unsigned-integer offset of the frame's prefix-scan scratch data</param>
        /// <param name="candidateOffset">32-byte-record offset of the frame's candidate data</param>
        /// <param name="keypointOffset">32-byte-record offset of the frame's keypoint data</param>
        /// <param name="orientedKeypointOffset">32-byte-record offset of the frame's oriented-keypoint data</param>
        /// <param name="descriptorOffset">Unsigned-integer offset of the frame's descriptor data</param>
        /// <param name="counterOffset">Unsigned-integer offset of the frame's per-octave counters</param>
        /// <param name="indirectCommandOffset">Unsigned-integer offset of the frame's indirect dispatch commands</param>
        public VulkanSiftPackedFramePlan(
            ulong inputByteOffset,
            uint inputFloatOffset,
            uint temporaryFloatOffset,
            uint gaussianFloatOffset,
            uint gradientFloatOffset,
            uint dogFloatOffset,
            uint flagOffset,
            uint scanScratchOffset,
            uint candidateOffset,
            uint keypointOffset,
            uint orientedKeypointOffset,
            uint descriptorOffset,
            uint counterOffset,
            uint indirectCommandOffset)
        {
            this.InputByteOffset = inputByteOffset;
            this.InputFloatOffset = inputFloatOffset;
            this.TemporaryFloatOffset = temporaryFloatOffset;
            this.GaussianFloatOffset = gaussianFloatOffset;
            this.GradientFloatOffset = gradientFloatOffset;
            this.DogFloatOffset = dogFloatOffset;
            this.FlagOffset = flagOffset;
            this.ScanScratchOffset = scanScratchOffset;
            this.CandidateOffset = candidateOffset;
            this.KeypointOffset = keypointOffset;
            this.OrientedKeypointOffset = orientedKeypointOffset;
            this.DescriptorOffset = descriptorOffset;
            this.CounterOffset = counterOffset;
            this.IndirectCommandOffset = indirectCommandOffset;
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets the byte offset of the frame's input pixels
        /// </summary>
        public ulong InputByteOffset { get; }

        /// <summary>
        /// Gets the float-element offset of the frame's normalized input
        /// </summary>
        public uint InputFloatOffset { get; }

        /// <summary>
        /// Gets the float-element offset of the frame's temporary processing data
        /// </summary>
        public uint TemporaryFloatOffset { get; }

        /// <summary>
        /// Gets the float-element offset of the frame's Gaussian pyramid
        /// </summary>
        public uint GaussianFloatOffset { get; }

        /// <summary>
        /// Gets the float-element offset of the frame's gradient data
        /// </summary>
        public uint GradientFloatOffset { get; }

        /// <summary>
        /// Gets the float-element offset of the frame's difference-of-Gaussians data
        /// </summary>
        public uint DogFloatOffset { get; }

        /// <summary>
        /// Gets the unsigned-integer offset of the frame's flag and prefix data
        /// </summary>
        public uint FlagOffset { get; }

        /// <summary>
        /// Gets the unsigned-integer offset of the frame's prefix-scan scratch data
        /// </summary>
        public uint ScanScratchOffset { get; }

        /// <summary>
        /// Gets the 32-byte-record offset of the frame's candidate data
        /// </summary>
        public uint CandidateOffset { get; }

        /// <summary>
        /// Gets the 32-byte-record offset of the frame's keypoint data
        /// </summary>
        public uint KeypointOffset { get; }

        /// <summary>
        /// Gets the 32-byte-record offset of the frame's oriented-keypoint data
        /// </summary>
        public uint OrientedKeypointOffset { get; }

        /// <summary>
        /// Gets the unsigned-integer offset of the frame's descriptor data
        /// </summary>
        public uint DescriptorOffset { get; }

        /// <summary>
        /// Gets the unsigned-integer offset of the frame's per-octave counters
        /// </summary>
        public uint CounterOffset { get; }

        /// <summary>
        /// Gets the unsigned-integer offset of the frame's indirect dispatch commands
        /// </summary>
        public uint IndirectCommandOffset { get; }

        #endregion
    }
}
