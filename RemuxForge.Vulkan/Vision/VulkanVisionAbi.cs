using System;
using System.Runtime.InteropServices;

namespace RemuxForge.Vulkan.Vision
{
    /// <summary>
    /// Validates the host-side layouts shared with the Vulkan SPIR-V shaders
    /// </summary>
    internal static class VulkanVisionAbi
    {
        /// <summary>
        /// Size in bytes of a keypoint record required by the shader ABI
        /// </summary>
        public const int KEYPOINT_RECORD_SIZE = 32;

        /// <summary>
        /// Size in bytes of a perceptual frame hash record required by the shader ABI
        /// </summary>
        public const int HASH_RECORD_SIZE = 16;

        /// <summary>
        /// Size in bytes of a reciprocal match record required by the shader ABI
        /// </summary>
        public const int RECIPROCAL_MATCH_RECORD_SIZE = 16;

        /// <summary>
        /// Size in bytes of a geometry pair record required by the shader ABI
        /// </summary>
        public const int GEOMETRY_PAIR_RECORD_SIZE = 56;

        /// <summary>
        /// Size in bytes of a RANSAC hypothesis record required by the shader ABI
        /// </summary>
        public const int RANSAC_HYPOTHESIS_RECORD_SIZE = 64;

        /// <summary>
        /// Size in bytes of a pair result record required by the shader ABI
        /// </summary>
        public const int PAIR_RESULT_RECORD_SIZE = 112;

        /// <summary>
        /// Verifies all host record sizes and selected field offsets required by the shader ABI
        /// </summary>
        public static void Validate()
        {
            EnsureSize<KeypointRecord>(KEYPOINT_RECORD_SIZE);
            EnsureOffset<KeypointRecord>(nameof(KeypointRecord.X), 0);
            EnsureOffset<KeypointRecord>(nameof(KeypointRecord.Orientation), 16);
            EnsureOffset<KeypointRecord>(nameof(KeypointRecord.Layer), 28);
            EnsureSize<HashRecord>(HASH_RECORD_SIZE);
            EnsureOffset<HashRecord>(nameof(HashRecord.Word1), 4);
            EnsureOffset<HashRecord>(nameof(HashRecord.Word3), 12);
            EnsureSize<ReciprocalMatchRecord>(RECIPROCAL_MATCH_RECORD_SIZE);
            EnsureOffset<ReciprocalMatchRecord>(nameof(ReciprocalMatchRecord.Distance), 8);
            EnsureSize<GeometryPairAbiRecord>(GEOMETRY_PAIR_RECORD_SIZE);
            EnsureOffset<GeometryPairAbiRecord>(nameof(GeometryPairAbiRecord.HypothesisOffset), 24);
            EnsureOffset<GeometryPairAbiRecord>(nameof(GeometryPairAbiRecord.SecondHeight), 44);
            EnsureOffset<GeometryPairAbiRecord>(nameof(GeometryPairAbiRecord.StableFirstFrame), 48);
            EnsureOffset<GeometryPairAbiRecord>(nameof(GeometryPairAbiRecord.StableSecondFrame), 52);
            EnsureSize<RansacHypothesisRecord>(RANSAC_HYPOTHESIS_RECORD_SIZE);
            EnsureOffset<RansacHypothesisRecord>(nameof(RansacHypothesisRecord.State), 48);
            EnsureSize<PairResultRecord>(PAIR_RESULT_RECORD_SIZE);
            EnsureOffset<PairResultRecord>(nameof(PairResultRecord.Metrics0), 32);
            EnsureOffset<PairResultRecord>(nameof(PairResultRecord.Row2), 96);
        }

        /// <summary>
        /// Verifies the marshaled size of a record against its shader ABI contract
        /// </summary>
        /// <param name="expected">Expected record size in bytes</param>
        private static void EnsureSize<T>(int expected) where T : struct
        {
            int actual = Marshal.SizeOf<T>();
            if (actual != expected)
                throw new VulkanShaderIncompatibleException("Invalid ABI size for " + typeof(T).Name + ": expected=" + expected + ", actual=" + actual);
        }

        /// <summary>
        /// Verifies a field offset against its shader ABI contract
        /// </summary>
        /// <param name="field">Managed field name whose marshaled offset is validated</param>
        /// <param name="expected">Expected field offset in bytes</param>
        private static void EnsureOffset<T>(string field, int expected) where T : struct
        {
            int actual = checked((int)Marshal.OffsetOf<T>(field));
            if (actual != expected)
                throw new VulkanShaderIncompatibleException("Invalid ABI offset for " + typeof(T).Name + "." + field + ": expected=" + expected + ", actual=" + actual);
        }
    }

    /// <summary>
    /// Defines the host-side layout of metadata for one frame pair shared with the RANSAC and pair-compaction shaders
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct GeometryPairAbiRecord
    {
        /// <summary>
        /// Index of the first frame in the first frame collection
        /// </summary>
        public uint FirstFrame;

        /// <summary>
        /// Index of the second frame in the second frame collection
        /// </summary>
        public uint SecondFrame;

        /// <summary>
        /// Base element offset of the first frame keypoints in packed keypoint storage
        /// </summary>
        public uint FirstKeypointOffset;

        /// <summary>
        /// Base element offset of the second frame keypoints in packed keypoint storage
        /// </summary>
        public uint SecondKeypointOffset;

        /// <summary>
        /// Base element offset of the reciprocal matches belonging to the pair
        /// </summary>
        public uint ReciprocalOffset;

        /// <summary>
        /// Base element offset of the pair count values, with reciprocal count at slot zero and forward count at slot one
        /// </summary>
        public uint MatchCountOffset;

        /// <summary>
        /// Base element offset of the RANSAC hypotheses reserved for the pair
        /// </summary>
        public uint HypothesisOffset;

        /// <summary>
        /// Output record index where the pair result is written
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
        /// Stable identifier used to seed deterministic RANSAC sampling for the first frame
        /// </summary>
        public uint StableFirstFrame;

        /// <summary>
        /// Stable identifier used to seed deterministic RANSAC sampling for the second frame
        /// </summary>
        public uint StableSecondFrame;
    }
}
