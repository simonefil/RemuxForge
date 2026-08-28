using System;
using System.Collections.Generic;

namespace RemuxForge.Vulkan.Vision.Sift
{
    /// <summary>
    /// Stores the geometry, buffer capacities, offsets and Gaussian kernels required to execute SIFT for one frame
    /// </summary>
    internal sealed class VulkanSiftPlan
    {
        #region Costruttore

        /// <summary>
        /// Initializes an empty plan for population by <see cref="Create"/>
        /// </summary>
        private VulkanSiftPlan()
        {
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Creates the SIFT geometry, flattened buffer layout and Gaussian filter tables for a frame
        /// </summary>
        /// <param name="width">Width of the source frame in pixels</param>
        /// <param name="height">Height of the source frame in pixels</param>
        /// <param name="options">SIFT parameters that determine octave geometry and feature capacity</param>
        /// <returns>A completed plan whose offsets can be used by the SIFT extractor and packed workspace</returns>
        public static VulkanSiftPlan Create(int width, int height, VulkanSiftOptions options)
        {
            VulkanSiftPlan result = new VulkanSiftPlan();
            result.BaseWidth = options.DoubleInput ? checked(width * 2) : width;
            result.BaseHeight = options.DoubleInput ? checked(height * 2) : height;
            result.OctaveLayers = options.OctaveLayers;
            result.DoubleInput = options.DoubleInput;
            int minimumDimension = Math.Min(result.BaseWidth, result.BaseHeight);
            int octaveCount = Math.Max(1, (int)Math.Floor(Math.Log2(minimumDimension)) - 2);
            result.Octaves = new List<VulkanSiftOctavePlan>(octaveCount);
            int gaussianOffset = 0;
            int dogOffset = 0;
            int candidateOffset = 0;
            int featureOffset = 0;
            int orientationOffset = 0;
            int maximumArea = 0;
            int maximumScanElements = 0;
            int octaveWidth = result.BaseWidth;
            int octaveHeight = result.BaseHeight;
            for (int octave = 0; octave < octaveCount && Math.Min(octaveWidth, octaveHeight) >= 8; octave++)
            {
                int area = checked(octaveWidth * octaveHeight);
                int candidateCount = checked(area * options.OctaveLayers);
                int featureCapacity = Math.Max(1, checked((int)Math.Ceiling(area * options.FeatureRatio)));
                VulkanSiftOctavePlan octavePlan = new VulkanSiftOctavePlan
                {
                    Index = octave,
                    Width = octaveWidth,
                    Height = octaveHeight,
                    GaussianOffset = gaussianOffset,
                    DogOffset = dogOffset,
                    CandidateOffset = candidateOffset,
                    FeatureOffset = featureOffset,
                    OrientationOffset = orientationOffset,
                    CandidateCount = candidateCount,
                    FeatureCapacity = featureCapacity,
                    CounterOffset = octave * 5
                };
                result.Octaves.Add(octavePlan);
                gaussianOffset = checked(gaussianOffset + area * (options.OctaveLayers + 3));
                dogOffset = checked(dogOffset + area * (options.OctaveLayers + 2));
                candidateOffset = checked(candidateOffset + candidateCount);
                featureOffset = checked(featureOffset + featureCapacity);
                orientationOffset = checked(orientationOffset + featureCapacity * 3);
                maximumArea = Math.Max(maximumArea, area);
                maximumScanElements = Math.Max(maximumScanElements, Math.Max(candidateCount, featureCapacity * 3));
                octaveWidth /= 2;
                octaveHeight /= 2;
            }
            result.InputFloatElements = checked(width * height + result.BaseWidth * result.BaseHeight);
            result.TemporaryFloatElements = maximumArea;
            result.GaussianFloatElements = gaussianOffset;
            result.DogFloatElements = dogOffset;
            result.CandidateCount = candidateOffset;
            result.FeatureCapacity = featureOffset;
            result.OrientationCapacity = orientationOffset;
            result.ScanScratchElements = ResolveScanScratchElements(maximumScanElements);
            BuildFilters(result, options);
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Builds the filter descriptors and contiguous normalized Gaussian coefficient buffer for a plan
        /// </summary>
        /// <param name="plan">Plan that receives the filter descriptors and coefficient array</param>
        /// <param name="options">SIFT parameters that determine the base sigma and scale spacing</param>
        private static void BuildFilters(VulkanSiftPlan plan, VulkanSiftOptions options)
        {
            List<float> weights = new List<float>();
            plan.Filters = new List<VulkanGaussianFilterPlan>();
            double assumedInputSigma = 0.5;
            double compensation = options.DoubleInput ? 4.0 * assumedInputSigma * assumedInputSigma : assumedInputSigma * assumedInputSigma;
            double initialSigma = Math.Sqrt(Math.Max(0.01, options.Sigma * options.Sigma - compensation));
            AddFilter(plan.Filters, weights, initialSigma);
            double scale = Math.Pow(2.0, 1.0 / options.OctaveLayers);
            for (int layer = 1; layer < options.OctaveLayers + 3; layer++)
            {
                double previous = options.Sigma * Math.Pow(scale, layer - 1);
                double total = options.Sigma * Math.Pow(scale, layer);
                AddFilter(plan.Filters, weights, Math.Sqrt(total * total - previous * previous));
            }
            plan.GaussianWeights = weights.ToArray();
        }

        /// <summary>
        /// Appends one normalized one-dimensional Gaussian kernel and records its slice in the shared coefficient buffer
        /// </summary>
        /// <param name="filters">Filter descriptor list that receives the new kernel metadata</param>
        /// <param name="weights">Contiguous coefficient buffer that receives the kernel samples</param>
        /// <param name="sigma">Standard deviation used to generate the kernel</param>
        private static void AddFilter(List<VulkanGaussianFilterPlan> filters, List<float> weights, double sigma)
        {
            int length = Math.Min(31, ((int)Math.Round(sigma * 6.0 + 1.0)) | 1);
            length = Math.Max(3, length);
            int radius = length / 2;
            int offset = weights.Count;
            double sum = 0.0;
            for (int i = -radius; i <= radius; i++)
            {
                float value = (float)Math.Exp(-(i * i) / (2.0 * sigma * sigma));
                weights.Add(value);
                sum += value;
            }
            for (int i = 0; i < length; i++)
                weights[offset + i] = (float)(weights[offset + i] / sum);
            filters.Add(new VulkanGaussianFilterPlan { Offset = offset, Length = length });
        }

        /// <summary>
        /// Calculates the scratch capacity required by the hierarchical prefix scan
        /// </summary>
        /// <param name="elementCount">Number of elements in the largest scan</param>
        /// <returns>The number of scratch elements required, including the minimum reserve</returns>
        private static int ResolveScanScratchElements(int elementCount)
        {
            int total = 0;
            int blocks = DivideRoundUp(elementCount, 256);
            while (blocks > 1)
            {
                total = checked(total + blocks * 2);
                blocks = DivideRoundUp(blocks, 256);
            }
            return Math.Max(512, total + 512);
        }

        /// <summary>
        /// Divides two positive integers and rounds the quotient up to the next whole block
        /// </summary>
        /// <param name="value">Number of elements to cover</param>
        /// <param name="divisor">Number of elements covered by one block</param>
        /// <returns>The smallest integer number of blocks that covers <paramref name="value"/></returns>
        private static int DivideRoundUp(int value, int divisor)
        {
            return checked((value + divisor - 1) / divisor);
        }

        #endregion

        #region Properties

        /// <summary>
        /// Width of the first octave after applying <see cref="DoubleInput"/>
        /// </summary>
        public int BaseWidth { get; private set; }

        /// <summary>
        /// Height of the first octave after applying <see cref="DoubleInput"/>
        /// </summary>
        public int BaseHeight { get; private set; }

        /// <summary>
        /// Number of Gaussian and Difference of Gaussians layers configured for each octave
        /// </summary>
        public int OctaveLayers { get; private set; }

        /// <summary>
        /// Indicates whether the source image is doubled before the first octave
        /// </summary>
        public bool DoubleInput { get; private set; }

        /// <summary>
        /// Total number of float elements reserved for the source and base-resolution input images
        /// </summary>
        public int InputFloatElements { get; private set; }

        /// <summary>
        /// Maximum pixel area of any octave reserved for separable Gaussian-filter scratch data
        /// </summary>
        public int TemporaryFloatElements { get; private set; }

        /// <summary>
        /// Total number of float elements reserved for all Gaussian images across all octaves
        /// </summary>
        public int GaussianFloatElements { get; private set; }

        /// <summary>
        /// Total number of float elements reserved for all Difference of Gaussians images across all octaves
        /// </summary>
        public int DogFloatElements { get; private set; }

        /// <summary>
        /// Total number of candidate keypoint slots across all octaves
        /// </summary>
        public int CandidateCount { get; private set; }

        /// <summary>
        /// Total number of compacted keypoint slots reserved across all octaves
        /// </summary>
        public int FeatureCapacity { get; private set; }

        /// <summary>
        /// Total number of oriented-keypoint slots reserved across all octaves, with three slots per feature slot
        /// </summary>
        public int OrientationCapacity { get; private set; }

        /// <summary>
        /// Number of uint elements reserved for hierarchical prefix-scan scratch data
        /// </summary>
        public int ScanScratchElements { get; private set; }

        /// <summary>
        /// Ordered octave plans generated for this frame
        /// </summary>
        public List<VulkanSiftOctavePlan> Octaves { get; private set; }

        /// <summary>
        /// Contiguous normalized one-dimensional Gaussian coefficients shared by all filters in the plan
        /// </summary>
        public float[] GaussianWeights { get; private set; }

        /// <summary>
        /// Ordered Gaussian filter descriptors used to construct the pyramid
        /// </summary>
        public List<VulkanGaussianFilterPlan> Filters { get; private set; }

        #endregion
    }

    /// <summary>
    /// Describes the geometry and flattened buffer ranges of one SIFT octave
    /// </summary>
    internal sealed class VulkanSiftOctavePlan
    {
        /// <summary>
        /// Zero-based index of this octave in the parent plan
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Width of the images in this octave in pixels
        /// </summary>
        public int Width { get; set; }

        /// <summary>
        /// Height of the images in this octave in pixels
        /// </summary>
        public int Height { get; set; }

        /// <summary>
        /// Element offset of the first Gaussian image for this octave
        /// </summary>
        public int GaussianOffset { get; set; }

        /// <summary>
        /// Element offset of the first Difference of Gaussians image for this octave
        /// </summary>
        public int DogOffset { get; set; }

        /// <summary>
        /// Element offset of the first candidate keypoint slot for this octave
        /// </summary>
        public int CandidateOffset { get; set; }

        /// <summary>
        /// Element offset of the first compacted keypoint slot for this octave
        /// </summary>
        public int FeatureOffset { get; set; }

        /// <summary>
        /// Element offset of the first oriented-keypoint slot for this octave
        /// </summary>
        public int OrientationOffset { get; set; }

        /// <summary>
        /// Number of candidate slots generated for this octave across its pixels and scale layers
        /// </summary>
        public int CandidateCount { get; set; }

        /// <summary>
        /// Maximum number of compacted keypoints reserved for this octave
        /// </summary>
        public int FeatureCapacity { get; set; }

        /// <summary>
        /// Element offset of the first counter reserved for this octave in the counter buffer
        /// </summary>
        public int CounterOffset { get; set; }
    }

    /// <summary>
    /// Describes one Gaussian filter slice in the plan's shared coefficient buffer
    /// </summary>
    internal sealed class VulkanGaussianFilterPlan
    {
        /// <summary>
        /// Element offset of the first coefficient in the shared Gaussian-weight buffer
        /// </summary>
        public int Offset { get; set; }

        /// <summary>
        /// Number of coefficients in this filter kernel
        /// </summary>
        public int Length { get; set; }

    }
}
