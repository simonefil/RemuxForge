using System.Runtime.InteropServices;

namespace RemuxForge.Vulkan
{
    /// <summary>
    /// Defines the ABI layout of one SIFT keypoint
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct KeypointRecord
    {
        /// <summary>
        /// Horizontal keypoint coordinate in the source image
        /// </summary>
        public float X;

        /// <summary>
        /// Vertical keypoint coordinate in the source image
        /// </summary>
        public float Y;

        /// <summary>
        /// Characteristic scale of the keypoint
        /// </summary>
        public float Scale;

        /// <summary>
        /// Detector response associated with the keypoint
        /// </summary>
        public float Response;

        /// <summary>
        /// Assigned keypoint orientation in degrees
        /// </summary>
        public float Orientation;

        /// <summary>
        /// Index of the frame containing the keypoint
        /// </summary>
        public uint FrameIndex;

        /// <summary>
        /// Octave in which the keypoint was detected
        /// </summary>
        public uint Octave;

        /// <summary>
        /// Scale-space layer in which the keypoint was detected
        /// </summary>
        public uint Layer;
    }

    /// <summary>
    /// Defines the ABI layout of one one-hundred-twenty-eight-bit perceptual frame hash
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct HashRecord
    {
        /// <summary>
        /// Most significant half of the horizontal gradient hash
        /// </summary>
        public uint Word0;

        /// <summary>
        /// Least significant half of the horizontal gradient hash
        /// </summary>
        public uint Word1;

        /// <summary>
        /// Most significant half of the vertical gradient hash
        /// </summary>
        public uint Word2;

        /// <summary>
        /// Least significant half of the vertical gradient hash
        /// </summary>
        public uint Word3;
    }

    /// <summary>
    /// Defines the ABI layout of one reciprocal descriptor match
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct ReciprocalMatchRecord
    {
        /// <summary>
        /// Index of the descriptor from the first feature set
        /// </summary>
        public uint FirstDescriptorIndex;

        /// <summary>
        /// Index of the descriptor from the second feature set
        /// </summary>
        public uint SecondDescriptorIndex;

        /// <summary>
        /// Descriptor distance reported for the match
        /// </summary>
        public float Distance;

        /// <summary>
        /// Index of the frame pair associated with the match
        /// </summary>
        public uint PairIndex;
    }

    /// <summary>
    /// Defines the ABI layout of one RANSAC homography hypothesis
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct RansacHypothesisRecord
    {
        /// <summary>
        /// First four-component vector of the candidate homography
        /// </summary>
        public Float4 Row0;

        /// <summary>
        /// Second four-component vector of the candidate homography
        /// </summary>
        public Float4 Row1;

        /// <summary>
        /// Third four-component vector of the candidate homography
        /// </summary>
        public Float4 Row2;

        /// <summary>
        /// Packed inlier count, hypothesis index, validity marker and error state
        /// </summary>
        public UInt4 State;
    }

    /// <summary>
    /// Defines the complete ABI layout of one geometric pair result
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PairResultRecord
    {
        /// <summary>
        /// Status, rejection reason and per-side keypoint counts
        /// </summary>
        public UInt4 Header0;

        /// <summary>
        /// Matching and inlier counts produced for the pair
        /// </summary>
        public UInt4 Header1;

        /// <summary>
        /// Inlier ratio, coverage values and mean reprojection error
        /// </summary>
        public Float4 Metrics0;

        /// <summary>
        /// Pair score and shader-reserved metric components
        /// </summary>
        public Float4 Metrics1;

        /// <summary>
        /// First four-component vector of the selected homography
        /// </summary>
        public Float4 Row0;

        /// <summary>
        /// Second four-component vector of the selected homography
        /// </summary>
        public Float4 Row1;

        /// <summary>
        /// Third four-component vector of the selected homography
        /// </summary>
        public Float4 Row2;
    }

    /// <summary>
    /// Represents four consecutive unsigned integers in the shader ABI
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct UInt4
    {
        /// <summary>
        /// First unsigned integer component
        /// </summary>
        public uint X;

        /// <summary>
        /// Second unsigned integer component
        /// </summary>
        public uint Y;

        /// <summary>
        /// Third unsigned integer component
        /// </summary>
        public uint Z;

        /// <summary>
        /// Fourth unsigned integer component
        /// </summary>
        public uint W;
    }

    /// <summary>
    /// Represents four consecutive floating-point values in the shader ABI
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct Float4
    {
        /// <summary>
        /// First floating-point component
        /// </summary>
        public float X;

        /// <summary>
        /// Second floating-point component
        /// </summary>
        public float Y;

        /// <summary>
        /// Third floating-point component
        /// </summary>
        public float Z;

        /// <summary>
        /// Fourth floating-point component
        /// </summary>
        public float W;
    }
}
