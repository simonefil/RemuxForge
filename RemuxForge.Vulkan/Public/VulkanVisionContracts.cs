using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Vulkan
{
    /// <summary>Minimum Vulkan capability tier available to the vision runtime</summary>
    public enum VulkanCapabilityTier
    {
        /// <summary>Device cannot execute the vision workload</summary>
        Unsupported = 0,
        /// <summary>Device provides the base compute capability</summary>
        Base = 1,
        /// <summary>Device supports the required subgroup operations</summary>
        Subgroup = 2
    }

    /// <summary>Functional outcome of processing a frame pair</summary>
    public enum VulkanSiftPairStatus : uint
    {
        /// <summary>Pair did not satisfy the acceptance criteria</summary>
        Rejected = 0,
        /// <summary>Pair satisfied the acceptance criteria</summary>
        Accepted = 1
    }

    /// <summary>Deterministic reason why a frame pair was rejected</summary>
    public enum VulkanSiftRejectReason : uint
    {
        /// <summary>No rejection occurred</summary>
        None = 0,
        /// <summary>First frame does not contain enough features</summary>
        FeaturelessFirstFrame = 1,
        /// <summary>Second frame does not contain enough features</summary>
        FeaturelessSecondFrame = 2,
        /// <summary>Too few reciprocal matches were found</summary>
        InsufficientReciprocalMatches = 4,
        /// <summary>No valid homography was found</summary>
        NoValidHomography = 6,
        /// <summary>Too few inliers supported the selected homography</summary>
        InsufficientInliers = 7,
        /// <summary>Inlier ratio was below the configured threshold</summary>
        InsufficientInlierRatio = 8,
        /// <summary>Geometric coverage was below the configured threshold</summary>
        InsufficientCoverage = 9,
        /// <summary>Mean reprojection error exceeded the configured threshold</summary>
        ExcessiveReprojectionError = 10,
        /// <summary>Homography was geometrically implausible</summary>
        ImplausibleHomography = 11
    }

    /// <summary>Configures device selection, memory, concurrency and runtime validation</summary>
    public sealed class VulkanVisionOptions
    {
        /// <summary>Initializes options with automatic device selection and Debug validation enabled</summary>
        public VulkanVisionOptions()
        {
            this.DeviceIndex = -1;
            this.MaximumInFlightWorkloads = 4;
#if DEBUG
            this.EnableValidation = true;
#else
            this.EnableValidation = false;
#endif
        }

        /// <summary>Device index, or <c>-1</c> to select a device automatically</summary>
        public int DeviceIndex { get; set; }
        /// <summary>Maximum VRAM budget in bytes; zero uses the budget resolved by the runtime</summary>
        public ulong MaximumVramBytes { get; set; }
        /// <summary>Maximum number of workloads registered concurrently; valid values are from 1 through 64</summary>
        public int MaximumInFlightWorkloads { get; set; }
        /// <summary>Enables the Vulkan validation layers when they are available</summary>
        public bool EnableValidation { get; set; }
        /// <summary>Optional pipeline-cache data produced by the same device and driver</summary>
        public byte[] InitialPipelineCache { get; set; }
    }

    /// <summary>Configures SIFT extraction, reciprocal matching and RANSAC</summary>
    public sealed class VulkanSiftOptions
    {
        /// <summary>Initializes the qualified default algorithm parameters</summary>
        public VulkanSiftOptions()
        {
            this.OctaveLayers = 3;
            this.ContrastThreshold = 0.04f;
            this.EdgeThreshold = 10.0f;
            this.Sigma = 1.6f;
            this.DoubleInput = true;
            this.IntensityScale = 1.0f / 256.0f;
            this.FeatureRatio = 0.05f;
            this.MaximumFeaturesPerFrame = 2000;
            this.LoweRatio = 0.75f;
            this.MinimumKeypointsPerFrame = 20;
            this.MinimumReciprocalMatches = 8;
            this.MinimumInliers = 6;
            this.MinimumInlierRatio = 0.2f;
            this.MinimumCoverage = 0.08f;
            this.MaximumMeanReprojectionError = 4.0f;
            this.MinimumHomographyAreaRatio = 0.20f;
            this.MaximumHomographyAreaRatio = 5.0f;
            this.RansacReprojectionThreshold = 3.0f;
            this.RansacHypothesisCount = 5760;
            this.RandomSeed = 0x6A09E667u;
        }

        /// <summary>Number of Gaussian and DoG levels per octave</summary>
        public int OctaveLayers { get; set; }
        /// <summary>SIFT contrast threshold</summary>
        public float ContrastThreshold { get; set; }
        /// <summary>Threshold used to remove responses associated with edges</summary>
        public float EdgeThreshold { get; set; }
        /// <summary>Initial sigma of the Gaussian pyramid</summary>
        public float Sigma { get; set; }
        /// <summary>Doubles the resolution before the first octave</summary>
        public bool DoubleInput { get; set; }
        /// <summary>Scale factor applied to normalized Gray8 pixels</summary>
        public float IntensityScale { get; set; }
        /// <summary>Maximum fraction of pixels promoted to feature candidates</summary>
        public float FeatureRatio { get; set; }
        /// <summary>Maximum number of features retained per frame</summary>
        public int MaximumFeaturesPerFrame { get; set; }
        /// <summary>Threshold used by Lowe's ratio test</summary>
        public float LoweRatio { get; set; }
        /// <summary>Minimum keypoints required for a frame to be informative</summary>
        public int MinimumKeypointsPerFrame { get; set; }
        /// <summary>Minimum reciprocal matches required before RANSAC</summary>
        public int MinimumReciprocalMatches { get; set; }
        /// <summary>Minimum inliers required to accept a homography</summary>
        public int MinimumInliers { get; set; }
        /// <summary>Minimum ratio of inliers to reciprocal matches</summary>
        public float MinimumInlierRatio { get; set; }
        /// <summary>Minimum geometric coverage required in both frames</summary>
        public float MinimumCoverage { get; set; }
        /// <summary>Maximum mean reprojection error in pixels</summary>
        public float MaximumMeanReprojectionError { get; set; }
        /// <summary>Minimum permitted homography area ratio</summary>
        public float MinimumHomographyAreaRatio { get; set; }
        /// <summary>Maximum permitted homography area ratio</summary>
        public float MaximumHomographyAreaRatio { get; set; }
        /// <summary>RANSAC reprojection threshold in pixels</summary>
        public float RansacReprojectionThreshold { get; set; }
        /// <summary>Number of deterministic RANSAC hypotheses evaluated per pair</summary>
        public int RansacHypothesisCount { get; set; }
        /// <summary>Deterministic seed used to generate hypotheses</summary>
        public uint RandomSeed { get; set; }
    }

    /// <summary>Packed pixel format accepted by the pipeline</summary>
    public enum VulkanPixelFormat : uint
    {
        /// <summary>One intensity byte per pixel</summary>
        Gray8 = 0,
        /// <summary>Three bytes per pixel in RGB order</summary>
        Rgb24 = 1,
        /// <summary>Three bytes per pixel in BGR order</summary>
        Bgr24 = 2,
        /// <summary>Four bytes per pixel in RGBA order</summary>
        Rgba32 = 3,
        /// <summary>Four bytes per pixel in BGRA order</summary>
        Bgra32 = 4
    }

    /// <summary>Explicit matrix applied to RGB samples to produce SIFT luma</summary>
    public enum VulkanRgbToGrayMatrix : uint
    {
        /// <summary>No conversion; valid only for Gray8 input</summary>
        None = 0,
        /// <summary>BT.601 luma coefficients</summary>
        Bt601 = 1,
        /// <summary>BT.709 luma coefficients</summary>
        Bt709 = 2,
        /// <summary>BT.2020 luma coefficients</summary>
        Bt2020 = 3
    }

    /// <summary>Packed frame with an explicit format and color conversion</summary>
    public sealed class VulkanImageFrame
    {
        /// <summary>Creates a frame after validating format, dimensions, stride and buffer capacity</summary>
        /// <param name="identifier">Opaque identifier carried into geometric processing and result metadata</param>
        /// <param name="pixels">Caller-owned packed pixel memory retained for the duration of execution</param>
        /// <param name="width">Frame width in pixels</param>
        /// <param name="height">Frame height in pixels</param>
        /// <param name="stride">Distance in bytes between consecutive rows, including any padding</param>
        /// <param name="pixelFormat">Packed layout of the samples</param>
        /// <param name="rgbToGrayMatrix">Explicit matrix for color input, or <see cref="VulkanRgbToGrayMatrix.None"/> for Gray8</param>
        public VulkanImageFrame(long identifier, ReadOnlyMemory<byte> pixels, int width, int height, int stride, VulkanPixelFormat pixelFormat = VulkanPixelFormat.Gray8, VulkanRgbToGrayMatrix rgbToGrayMatrix = VulkanRgbToGrayMatrix.None)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            int bytesPerPixel = GetBytesPerPixel(pixelFormat);
            if (pixelFormat == VulkanPixelFormat.Gray8 && rgbToGrayMatrix != VulkanRgbToGrayMatrix.None)
                throw new ArgumentException("Gray8 does not accept an RGB conversion matrix.", nameof(rgbToGrayMatrix));
            if (pixelFormat != VulkanPixelFormat.Gray8 && !IsSupportedRgbMatrix(rgbToGrayMatrix))
                throw new ArgumentException("An RGB format requires an explicit conversion matrix.", nameof(rgbToGrayMatrix));
            if (stride < checked(width * bytesPerPixel))
                throw new ArgumentOutOfRangeException(nameof(stride));
            if (pixels.Length < checked(stride * height))
                throw new ArgumentException("The frame buffer does not contain all declared rows.", nameof(pixels));
            this.Identifier = identifier;
            this.Pixels = pixels;
            this.Width = width;
            this.Height = height;
            this.Stride = stride;
            this.PixelFormat = pixelFormat;
            this.RgbToGrayMatrix = rgbToGrayMatrix;
        }

        /// <summary>Opaque identifier carried into the result metadata</summary>
        public long Identifier { get; }
        /// <summary>Packed pixel storage in row order</summary>
        public ReadOnlyMemory<byte> Pixels { get; }
        /// <summary>Frame width in pixels</summary>
        public int Width { get; }
        /// <summary>Frame height in pixels</summary>
        public int Height { get; }
        /// <summary>Distance in bytes between consecutive rows</summary>
        public int Stride { get; }
        /// <summary>Packed pixel layout</summary>
        public VulkanPixelFormat PixelFormat { get; }
        /// <summary>Explicit RGB-to-luma conversion matrix</summary>
        public VulkanRgbToGrayMatrix RgbToGrayMatrix { get; }

        /// <summary>Resolves the packed bytes-per-pixel value for a supported format</summary>
        /// <param name="pixelFormat">Pixel format to resolve</param>
        /// <returns>Number of bytes occupied by one packed pixel</returns>
        private static int GetBytesPerPixel(VulkanPixelFormat pixelFormat)
        {
            if (pixelFormat == VulkanPixelFormat.Gray8)
                return 1;
            if (pixelFormat == VulkanPixelFormat.Rgb24 || pixelFormat == VulkanPixelFormat.Bgr24)
                return 3;
            if (pixelFormat == VulkanPixelFormat.Rgba32 || pixelFormat == VulkanPixelFormat.Bgra32)
                return 4;
            throw new ArgumentOutOfRangeException(nameof(pixelFormat));
        }

        /// <summary>Determines whether a matrix is valid for an RGB input format</summary>
        /// <param name="matrix">Matrix value to validate</param>
        /// <returns><see langword="true"/> when the value identifies a supported RGB-to-luma matrix</returns>
        private static bool IsSupportedRgbMatrix(VulkanRgbToGrayMatrix matrix)
        {
            return matrix == VulkanRgbToGrayMatrix.Bt601
                || matrix == VulkanRgbToGrayMatrix.Bt709
                || matrix == VulkanRgbToGrayMatrix.Bt2020;
        }
    }

    /// <summary>Indices of a pair spanning the first and second frame collections</summary>
    public struct VulkanFramePair
    {
        /// <summary>Zero-based index in the first frame collection</summary>
        public int FirstFrameIndex { get; set; }
        /// <summary>Zero-based index in the second frame collection</summary>
        public int SecondFrameIndex { get; set; }
    }

    /// <summary>Complete request for one pipeline execution</summary>
    public sealed class VulkanSiftBatchRequest
    {
        /// <summary>Creates a request while retaining the caller's collections without copying them</summary>
        /// <param name="firstFrames">First frame collection indexed by the pairs</param>
        /// <param name="secondFrames">Second frame collection indexed by the pairs</param>
        /// <param name="pairs">Pairs to process in the requested order</param>
        /// <param name="options">SIFT, matching and RANSAC parameters, or <see langword="null"/> to use defaults</param>
        public VulkanSiftBatchRequest(IReadOnlyList<VulkanImageFrame> firstFrames, IReadOnlyList<VulkanImageFrame> secondFrames, IReadOnlyList<VulkanFramePair> pairs, VulkanSiftOptions options = null)
        {
            this.FirstFrames = firstFrames ?? throw new ArgumentNullException(nameof(firstFrames));
            this.SecondFrames = secondFrames ?? throw new ArgumentNullException(nameof(secondFrames));
            this.Pairs = pairs ?? throw new ArgumentNullException(nameof(pairs));
            this.Options = options ?? new VulkanSiftOptions();
        }

        /// <summary>First frame collection</summary>
        public IReadOnlyList<VulkanImageFrame> FirstFrames { get; }
        /// <summary>Second frame collection</summary>
        public IReadOnlyList<VulkanImageFrame> SecondFrames { get; }
        /// <summary>Pairs to evaluate in the requested order</summary>
        public IReadOnlyList<VulkanFramePair> Pairs { get; }
        /// <summary>Algorithm options retained by the request</summary>
        public VulkanSiftOptions Options { get; }
        /// <summary>Optional recipient of aggregate progress snapshots</summary>
        public IProgress<VulkanVisionProgress> Progress { get; set; }
        /// <summary>Omits results for pairs containing a frame without enough features</summary>
        public bool OmitFeaturelessPairResults { get; set; }
    }

    /// <summary>SIFT and RANSAC outcome for one frame pair</summary>
    public sealed class VulkanSiftPairResult
    {
        /// <summary>Original frame pair</summary>
        public VulkanFramePair Pair { get; set; }
        /// <summary>Whether the pair satisfied the acceptance criteria</summary>
        public VulkanSiftPairStatus Status { get; set; }
        /// <summary>Deterministic rejection reason, or <see cref="VulkanSiftRejectReason.None"/> for an accepted pair</summary>
        public VulkanSiftRejectReason RejectReason { get; set; }
        /// <summary>Normalized composite score produced by geometric validation</summary>
        public float Score { get; set; }
        /// <summary>Keypoint count in the first frame</summary>
        public int FirstKeypointCount { get; set; }
        /// <summary>Keypoint count in the second frame</summary>
        public int SecondKeypointCount { get; set; }
        /// <summary>Matches that survived the forward ratio test</summary>
        public int ForwardRatioMatchCount { get; set; }
        /// <summary>Reciprocal match count</summary>
        public int ReciprocalMatchCount { get; set; }
        /// <summary>Inlier count for the selected homography</summary>
        public int InlierCount { get; set; }
        /// <summary>Ratio of inliers to reciprocal matches</summary>
        public float InlierRatio { get; set; }
        /// <summary>Normalized geometric coverage of the first frame</summary>
        public float FirstCoverage { get; set; }
        /// <summary>Normalized geometric coverage of the second frame</summary>
        public float SecondCoverage { get; set; }
        /// <summary>Mean reprojection error in pixels</summary>
        public float MeanReprojectionError { get; set; }
        /// <summary>Nine-element 3x3 homography in row-major order</summary>
        public float[] Homography { get; set; }
    }

    /// <summary>Aggregated result of one Vulkan batch</summary>
    public sealed class VulkanSiftBatchResult
    {
        /// <summary>Initializes an empty result with an owned pair-result list</summary>
        public VulkanSiftBatchResult()
        {
            this.PairResults = new List<VulkanSiftPairResult>();
        }

        /// <summary>Results for materialized pairs</summary>
        public List<VulkanSiftPairResult> PairResults { get; }
        /// <summary>Keypoint counts for the first collection in frame order</summary>
        public IReadOnlyList<int> FirstFrameKeypointCounts { get; internal set; }
        /// <summary>Keypoint counts for the second collection in frame order</summary>
        public IReadOnlyList<int> SecondFrameKeypointCounts { get; internal set; }
        /// <summary>Capabilities of the device that executed the batch</summary>
        public VulkanDeviceCapabilities Capabilities { get; internal set; }
        /// <summary>Aggregated diagnostics for the execution</summary>
        public VulkanVisionDiagnostics Diagnostics { get; internal set; }
    }

    /// <summary>Aggregate progress for upload, extraction and matching</summary>
    public sealed class VulkanVisionProgress
    {
        /// <summary>Frames uploaded to the device</summary>
        public int UploadedFrames { get; set; }
        /// <summary>Total frames in both input collections</summary>
        public int TotalFrames { get; set; }
        /// <summary>Frames whose feature extraction has completed</summary>
        public int ExtractedFrames { get; set; }
        /// <summary>Pairs processed so far</summary>
        public int ProcessedPairs { get; set; }
        /// <summary>Total active pairs considered for processing</summary>
        public int TotalPairs { get; set; }
        /// <summary>Completed GPU tiles</summary>
        public int CompletedTiles { get; set; }
        /// <summary>Current resident device memory in bytes</summary>
        public ulong ResidentBytes { get; set; }
    }
}
