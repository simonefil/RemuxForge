using System;
using System.Collections.Generic;

namespace RemuxForge.Vulkan
{
    /// <summary>Perceptual hash of one frame, as the horizontal and vertical gradient words</summary>
    public readonly struct VulkanFrameHash
    {
        /// <summary>Creates a frame hash from its two gradient words</summary>
        /// <param name="horizontal">Word comparing horizontally adjacent cells</param>
        /// <param name="vertical">Word comparing vertically adjacent cells</param>
        public VulkanFrameHash(ulong horizontal, ulong vertical)
        {
            this.Horizontal = horizontal;
            this.Vertical = vertical;
        }

        /// <summary>Word comparing horizontally adjacent cells, first comparison in the most significant bit</summary>
        public ulong Horizontal { get; }

        /// <summary>Word comparing vertically adjacent cells, first comparison in the most significant bit</summary>
        public ulong Vertical { get; }

    }

    /// <summary>Frame hashes, luminance and compact thumbnails produced by one device extraction</summary>
    public sealed class VulkanFrameSignalBatch
    {
        /// <summary>Creates a complete ordered signal batch</summary>
        /// <param name="hashes">Per-frame horizontal and vertical hashes</param>
        /// <param name="lumaMeans">Per-frame mean luminance</param>
        /// <param name="thumbnailStandardDeviations">Per-frame standard deviation of the 12x12 thumbnail</param>
        /// <param name="thumbnailPixels">Packed 12x12 thumbnails in frame order</param>
        internal VulkanFrameSignalBatch(VulkanFrameHash[] hashes, float[] lumaMeans, float[] thumbnailStandardDeviations, byte[] thumbnailPixels)
        {
            this.Hashes = hashes;
            this.LumaMeans = lumaMeans;
            this.ThumbnailStandardDeviations = thumbnailStandardDeviations;
            this.ThumbnailPixels = thumbnailPixels;
        }

        /// <summary>Number of frames represented by the batch</summary>
        public int Count { get { return this.Hashes.Length; } }

        /// <summary>Per-frame horizontal and vertical hashes</summary>
        public VulkanFrameHash[] Hashes { get; }

        /// <summary>Per-frame mean luminance values</summary>
        public float[] LumaMeans { get; }

        /// <summary>Per-frame standard deviations of the 12x12 thumbnails</summary>
        public float[] ThumbnailStandardDeviations { get; }

        /// <summary>Tightly packed 12x12 thumbnails in frame order</summary>
        public byte[] ThumbnailPixels { get; }
    }

    /// <summary>One decoded track: the frame hashes and the timestamps they were taken at</summary>
    public sealed class VulkanHashTrack
    {
        /// <summary>Creates a track from its frame hashes and their presentation timestamps</summary>
        /// <param name="hashes">Frame hashes in decoding order</param>
        /// <param name="timestampsMs">Presentation timestamps in milliseconds, in non-decreasing order</param>
        public VulkanHashTrack(IReadOnlyList<VulkanFrameHash> hashes, IReadOnlyList<double> timestampsMs)
        {
            if (hashes == null)
                throw new ArgumentNullException(nameof(hashes));
            if (timestampsMs == null)
                throw new ArgumentNullException(nameof(timestampsMs));
            if (hashes.Count == 0)
                throw new ArgumentException("The track does not contain any frame.", nameof(hashes));
            if (hashes.Count != timestampsMs.Count)
                throw new ArgumentException("The track declares a different number of hashes and timestamps.", nameof(timestampsMs));
            // La ricerca binaria del percorso GPU e' la stessa del percorso CPU: pretende una
            // sequenza non decrescente, e senza questo controllo il difetto emergerebbe come
            // un fotogramma appaiato male invece che come un errore
            if (!double.IsFinite(timestampsMs[0]))
                throw new ArgumentException("The track declares a timestamp that is not a finite value.", nameof(timestampsMs));
            for (int i = 1; i < timestampsMs.Count; i++)
            {
                if (!double.IsFinite(timestampsMs[i]))
                    throw new ArgumentException("The track declares a timestamp that is not a finite value.", nameof(timestampsMs));
                if (timestampsMs[i] < timestampsMs[i - 1])
                    throw new ArgumentException("The track timestamps are not in non-decreasing order.", nameof(timestampsMs));
            }
            this.Hashes = hashes;
            this.TimestampsMs = timestampsMs;
        }

        /// <summary>Frame hashes in decoding order</summary>
        public IReadOnlyList<VulkanFrameHash> Hashes { get; }

        /// <summary>Presentation timestamps in milliseconds, in non-decreasing order</summary>
        public IReadOnlyList<double> TimestampsMs { get; }

        /// <summary>Number of frames in the track</summary>
        public int Count { get { return this.Hashes.Count; } }
    }

    /// <summary>One grid of candidate offsets measured against one range of source frames</summary>
    public readonly struct VulkanHashScan
    {
        /// <summary>Creates a scan over a source frame range and a candidate offset grid</summary>
        /// <param name="firstIndex">Index of the first source frame taken into the measurement</param>
        /// <param name="stride">Distance in frames between two consecutive measured source frames</param>
        /// <param name="indexCount">Number of measured source frames</param>
        /// <param name="firstOffsetMs">First candidate offset in milliseconds</param>
        /// <param name="stepMs">Distance in milliseconds between two consecutive candidate offsets</param>
        /// <param name="candidateCount">Number of candidate offsets</param>
        /// <param name="toleranceRadius">Language frames explored on each side of the located timestamp</param>
        /// <param name="threshold">Highest Hamming distance that still counts as an explained frame</param>
        public VulkanHashScan(int firstIndex, int stride, int indexCount, double firstOffsetMs, double stepMs, int candidateCount, int toleranceRadius, int threshold)
        {
            this.FirstIndex = firstIndex;
            this.Stride = stride;
            this.IndexCount = indexCount;
            this.FirstOffsetMs = firstOffsetMs;
            this.StepMs = stepMs;
            this.CandidateCount = candidateCount;
            this.ToleranceRadius = toleranceRadius;
            this.Threshold = threshold;
        }

        /// <summary>Index of the first source frame taken into the measurement</summary>
        public int FirstIndex { get; }

        /// <summary>Distance in frames between two consecutive measured source frames</summary>
        public int Stride { get; }

        /// <summary>Number of measured source frames</summary>
        public int IndexCount { get; }

        /// <summary>First candidate offset in milliseconds</summary>
        public double FirstOffsetMs { get; }

        /// <summary>Distance in milliseconds between two consecutive candidate offsets</summary>
        public double StepMs { get; }

        /// <summary>Number of candidate offsets</summary>
        public int CandidateCount { get; }

        /// <summary>Language frames explored on each side of the located timestamp</summary>
        public int ToleranceRadius { get; }

        /// <summary>Highest Hamming distance that still counts as an explained frame</summary>
        public int Threshold { get; }
    }

    /// <summary>Outcome of one scan: how many source frames each candidate offset explains</summary>
    public sealed class VulkanHashScanResult
    {
        /// <summary>Creates the outcome of one scan</summary>
        /// <param name="explainedCounts">Explained frame count for each candidate offset, in grid order</param>
        /// <param name="bestCandidate">Index of the candidate offset explaining the most frames</param>
        /// <param name="bestOffsetMs">Offset in milliseconds of the best candidate</param>
        /// <param name="indexCount">Number of measured source frames</param>
        internal VulkanHashScanResult(IReadOnlyList<int> explainedCounts, int bestCandidate, double bestOffsetMs, int indexCount)
        {
            this.ExplainedCounts = explainedCounts;
            this.BestCandidate = bestCandidate;
            this.BestOffsetMs = bestOffsetMs;
            this.BestExplainedFraction = indexCount == 0 ? 0.0 : (double)explainedCounts[bestCandidate] / indexCount;
        }

        /// <summary>Explained frame count for each candidate offset, in grid order</summary>
        public IReadOnlyList<int> ExplainedCounts { get; }

        /// <summary>Index of the first candidate offset explaining the most frames</summary>
        public int BestCandidate { get; }

        /// <summary>Offset in milliseconds of the best candidate</summary>
        public double BestOffsetMs { get; }

        /// <summary>Fraction of measured source frames explained by the best candidate</summary>
        public double BestExplainedFraction { get; }
    }

    /// <summary>Results of one batch of scans and the diagnostics collected while running it</summary>
    public sealed class VulkanHashBatchResult
    {
        /// <summary>Creates the result of one batch of scans</summary>
        /// <param name="scans">Outcome of each requested scan, in request order</param>
        /// <param name="diagnostics">Diagnostics collected while running the batch</param>
        internal VulkanHashBatchResult(IReadOnlyList<VulkanHashScanResult> scans, VulkanVisionDiagnostics diagnostics)
        {
            this.Scans = scans;
            this.Diagnostics = diagnostics;
        }

        /// <summary>Outcome of each requested scan, in request order</summary>
        public IReadOnlyList<VulkanHashScanResult> Scans { get; }

        /// <summary>Diagnostics collected while running the batch</summary>
        public VulkanVisionDiagnostics Diagnostics { get; }
    }
}
