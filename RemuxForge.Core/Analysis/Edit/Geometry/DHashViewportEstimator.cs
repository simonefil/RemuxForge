using RemuxForge.Core.Analysis.Edit.Extraction;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Analysis.Edit.Geometry
{
    /// <summary>
    /// Risultato della calibrazione del viewport usato esclusivamente dal descrittore dHash
    /// </summary>
    internal class DHashViewportEstimationResult
    {
        #region Costruttore

        /// <summary>
        /// Inizializza un risultato non conclusivo
        /// </summary>
        public DHashViewportEstimationResult()
        {
            this.SourceGeometry = new FrameGeometry();
            this.LanguageGeometry = new FrameGeometry();
            this.RejectReason = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True quando è stata scelta una coppia di viewport
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Geometria dHash della sorgente
        /// </summary>
        public FrameGeometry SourceGeometry { get; set; }

        /// <summary>
        /// Geometria dHash della copia doppiata
        /// </summary>
        public FrameGeometry LanguageGeometry { get; set; }

        /// <summary>
        /// Frazione dei campioni sorgente con distanza non superiore a venti
        /// </summary>
        public double MatchRate { get; set; }

        /// <summary>
        /// Distanza di Hamming mediana dei migliori accoppiamenti
        /// </summary>
        public double MedianDistance { get; set; }

        /// <summary>
        /// Motivo per cui non è stato possibile calibrare il viewport
        /// </summary>
        public string RejectReason { get; set; }

        #endregion
    }

    /// <summary>
    /// Calibra il viewport stabile del descrittore dHash senza alterare la geometria affine del canvas
    /// </summary>
    internal class DHashViewportEstimator
    {
        #region Costanti

        private const int CALIBRATION_SIDE = 256;
        private const int SOURCE_WINDOW_SECONDS = 30;
        private const int LANGUAGE_WINDOW_SECONDS = 150;
        private const int SAMPLE_FPS = 5;
        private const int MATCH_DISTANCE = 20;

        private static readonly double[] s_windowStarts = new double[] { 400.0, 1200.0 };
        private static readonly double[] s_zooms = new double[] { 1.0, 0.96, 0.92, 0.88, 0.84, 0.80 };
        private static readonly double[] s_shifts = new double[] { -0.03, 0.0, 0.03 };

        #endregion

        #region Variabili di istanza

        private readonly string _ffmpegPath;
        private readonly FfmpegConfig _ffmpegConfig;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza il calibratore con la configurazione di decodifica corrente
        /// </summary>
        public DHashViewportEstimator(string ffmpegPath, FfmpegConfig ffmpegConfig)
        {
            this._ffmpegPath = ffmpegPath ?? "";
            this._ffmpegConfig = ffmpegConfig ?? new FfmpegConfig();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Sceglie zoom e traslazione dHash sui migliori accoppiamenti di due finestre indipendenti
        /// </summary>
        public DHashViewportEstimationResult Estimate(string sourceFile, string languageFile, string sourceCropPx, string languageCropPx, int sourceDurationMs, int languageDurationMs, CancellationToken cancellationToken)
        {
            DHashViewportEstimationResult result = new DHashViewportEstimationResult();
            if (sourceDurationMs <= 0 || languageDurationMs <= 0)
            {
                result.RejectReason = "Durata video non disponibile per la calibrazione dHash";
                return result;
            }

            RawFrameWindow[] sourceWindows = new RawFrameWindow[s_windowStarts.Length];
            RawFrameWindow[] languageWindows = new RawFrameWindow[s_windowStarts.Length];
            ParallelOptions decodeOptions = new ParallelOptions();
            decodeOptions.CancellationToken = cancellationToken;
            decodeOptions.MaxDegreeOfParallelism = s_windowStarts.Length * 2;
            Parallel.For(0, s_windowStarts.Length * 2, decodeOptions, index =>
            {
                if (index < s_windowStarts.Length)
                {
                    double start = ResolveStart(s_windowStarts[index], SOURCE_WINDOW_SECONDS, sourceDurationMs);
                    sourceWindows[index] = this.DecodeWindow(sourceFile, sourceCropPx, start, SOURCE_WINDOW_SECONDS, cancellationToken);
                }
                else
                {
                    int windowIndex = index - s_windowStarts.Length;
                    double start = ResolveStart(Math.Max(0.0, s_windowStarts[windowIndex] - 60.0), LANGUAGE_WINDOW_SECONDS, languageDurationMs);
                    languageWindows[windowIndex] = this.DecodeWindow(languageFile, languageCropPx, start, LANGUAGE_WINDOW_SECONDS, cancellationToken);
                }
            });

            for (int i = 0; i < s_windowStarts.Length; i++)
            {
                if (sourceWindows[i].Count == 0 || languageWindows[i].Count == 0)
                {
                    result.RejectReason = "Campioni insufficienti per la calibrazione dHash";
                    return result;
                }
            }

            VariantSignals[][] sourceSignals = this.BuildSignals(sourceWindows, cancellationToken);
            VariantSignals[][] languageSignals = this.BuildSignals(languageWindows, cancellationToken);
            ViewportCandidate best = this.FindBestCandidate(sourceSignals, languageSignals, cancellationToken);
            if (best == null)
            {
                result.RejectReason = "Nessun viewport dHash misurabile";
                return result;
            }

            ViewportVariant sourceVariant = BuildVariants()[best.SourceVariantIndex];
            ViewportVariant languageVariant = BuildVariants()[best.LanguageVariantIndex];
            result.SourceGeometry.CropPx = Options.NormalizeAnalysisCropPx(sourceCropPx);
            result.SourceGeometry.UseCentralSquare = true;
            result.SourceGeometry.Zoom = sourceVariant.Zoom;
            result.SourceGeometry.VerticalShift = sourceVariant.Shift;
            result.LanguageGeometry.CropPx = Options.NormalizeAnalysisCropPx(languageCropPx);
            result.LanguageGeometry.UseCentralSquare = true;
            result.LanguageGeometry.Zoom = languageVariant.Zoom;
            result.LanguageGeometry.VerticalShift = languageVariant.Shift;
            result.MatchRate = best.MatchRate;
            result.MedianDistance = best.MedianDistance;
            result.Success = true;
            return result;
        }

        #endregion

        #region Metodi privati - Decodifica

        /// <summary>
        /// Decodifica una finestra a cinque fotogrammi al secondo nel quadrato di calibrazione
        /// </summary>
        private RawFrameWindow DecodeWindow(string filePath, string cropPx, double startSeconds, int durationSeconds, CancellationToken cancellationToken)
        {
            List<string> arguments = new List<string>();
            arguments.Add("-nostdin");
            arguments.Add("-v");
            arguments.Add("error");
            if (this._ffmpegConfig.HardwareAcceleration && FfmpegConfig.IsValidHardwareAccelerationMethod(this._ffmpegConfig.HardwareAccelerationMethod))
            {
                arguments.Add("-hwaccel");
                arguments.Add(this._ffmpegConfig.HardwareAccelerationMethod);
            }
            arguments.Add("-ss");
            arguments.Add(startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            arguments.Add("-i");
            arguments.Add(filePath);
            arguments.Add("-t");
            arguments.Add(durationSeconds.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-an");
            arguments.Add("-sn");
            arguments.Add("-dn");
            arguments.Add("-map");
            arguments.Add("0:v:0");
            arguments.Add("-vf");
            arguments.Add(BuildCalibrationFilter(cropPx));
            arguments.Add("-f");
            arguments.Add("rawvideo");
            arguments.Add("-");

            using (MemoryStream pixels = new MemoryStream())
            {
                ProcessBinaryResult run = ProcessRunner.RunBinaryStdout(this._ffmpegPath, arguments.ToArray(), (buffer, count) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pixels.Write(buffer, 0, count);
                }, this._ffmpegConfig.FrameExtractionTimeoutMs);
                cancellationToken.ThrowIfCancellationRequested();
                if (run.ExitCode != 0)
                    throw new InvalidOperationException("Decodifica di calibrazione dHash non riuscita per " + Path.GetFileName(filePath) + ": " + GetLastErrorLine(run.Stderr));

                byte[] data = pixels.ToArray();
                int frameBytes = CALIBRATION_SIDE * CALIBRATION_SIDE;
                int frameCount = data.Length / frameBytes;
                if (data.Length != frameCount * frameBytes)
                    Array.Resize(ref data, frameCount * frameBytes);
                return new RawFrameWindow(data, frameCount);
            }
        }

        /// <summary>
        /// Costruisce la normalizzazione di base comune a tutti i viewport candidati
        /// </summary>
        private static string BuildCalibrationFilter(string cropPx)
        {
            List<string> filters = new List<string>();
            if (Options.TryParseAnalysisCropPx(cropPx, out int left, out int right, out int top, out int bottom) && (left != 0 || right != 0 || top != 0 || bottom != 0))
            {
                filters.Add("crop=iw-" + left.ToString(CultureInfo.InvariantCulture) + "-" + right.ToString(CultureInfo.InvariantCulture) +
                    ":ih-" + top.ToString(CultureInfo.InvariantCulture) + "-" + bottom.ToString(CultureInfo.InvariantCulture) +
                    ":" + left.ToString(CultureInfo.InvariantCulture) + ":" + top.ToString(CultureInfo.InvariantCulture));
            }
            filters.Add("fps=" + SAMPLE_FPS.ToString(CultureInfo.InvariantCulture));
            filters.Add("scale=iw*sar:ih");
            filters.Add("crop=min(iw\\,ih):min(iw\\,ih):(iw-min(iw\\,ih))/2:(ih-min(iw\\,ih))/2");
            filters.Add("scale=" + CALIBRATION_SIDE.ToString(CultureInfo.InvariantCulture) + ":" + CALIBRATION_SIDE.ToString(CultureInfo.InvariantCulture) + ":flags=area");
            filters.Add("format=gray");
            return string.Join(",", filters);
        }

        #endregion

        #region Metodi privati - Descrittori

        /// <summary>
        /// Calcola tutti i descrittori candidati una sola volta per ciascun fotogramma
        /// </summary>
        private VariantSignals[][] BuildSignals(RawFrameWindow[] windows, CancellationToken cancellationToken)
        {
            ViewportVariant[] variants = BuildVariants();
            VariantSignals[][] result = new VariantSignals[windows.Length][];
            for (int windowIndex = 0; windowIndex < windows.Length; windowIndex++)
            {
                RawFrameWindow window = windows[windowIndex];
                VariantSignals[] windowSignals = new VariantSignals[variants.Length];
                for (int variantIndex = 0; variantIndex < variants.Length; variantIndex++)
                    windowSignals[variantIndex] = new VariantSignals(window.Count);

                ParallelOptions options = new ParallelOptions();
                options.CancellationToken = cancellationToken;
                options.MaxDegreeOfParallelism = ParallelismHelper.ResolveDefaultMaxDegree();
                Parallel.For(0, window.Count, options, frameIndex =>
                {
                    int[] integral = BuildIntegralImage(window.Pixels, frameIndex * CALIBRATION_SIDE * CALIBRATION_SIDE);
                    double[] resized = new double[FrameSignals.SIDE * FrameSignals.SIDE];
                    for (int variantIndex = 0; variantIndex < variants.Length; variantIndex++)
                    {
                        ResizeViewport(integral, variants[variantIndex], resized);
                        ComputeHashes(resized, out ulong hash0, out ulong hash1);
                        windowSignals[variantIndex].Hash0[frameIndex] = hash0;
                        windowSignals[variantIndex].Hash1[frameIndex] = hash1;
                    }
                });
                result[windowIndex] = windowSignals;
            }
            return result;
        }

        /// <summary>
        /// Costruisce l'immagine integrale del frame 256x256
        /// </summary>
        private static int[] BuildIntegralImage(byte[] pixels, int origin)
        {
            int stride = CALIBRATION_SIDE + 1;
            int[] integral = new int[stride * stride];
            for (int row = 0; row < CALIBRATION_SIDE; row++)
            {
                int rowSum = 0;
                int sourceOffset = origin + row * CALIBRATION_SIDE;
                int targetOffset = (row + 1) * stride;
                int previousOffset = row * stride;
                for (int column = 0; column < CALIBRATION_SIDE; column++)
                {
                    rowSum += pixels[sourceOffset + column];
                    integral[targetOffset + column + 1] = integral[previousOffset + column + 1] + rowSum;
                }
            }
            return integral;
        }

        /// <summary>
        /// Riduce il viewport a 72x72 con gli stessi intervalli area del prototipo
        /// </summary>
        private static void ResizeViewport(int[] integral, ViewportVariant variant, double[] resized)
        {
            int stride = CALIBRATION_SIDE + 1;
            int side = (int)Math.Round(CALIBRATION_SIDE * variant.Zoom);
            int left = (CALIBRATION_SIDE - side) / 2;
            int top = (int)Math.Round(left + variant.Shift * CALIBRATION_SIDE);
            top = Math.Max(0, Math.Min(CALIBRATION_SIDE - side, top));
            for (int row = 0; row < FrameSignals.SIDE; row++)
            {
                int y0 = top + row * side / FrameSignals.SIDE;
                int y1 = top + (row + 1) * side / FrameSignals.SIDE;
                for (int column = 0; column < FrameSignals.SIDE; column++)
                {
                    int x0 = left + column * side / FrameSignals.SIDE;
                    int x1 = left + (column + 1) * side / FrameSignals.SIDE;
                    int sum = integral[y1 * stride + x1] - integral[y0 * stride + x1] - integral[y1 * stride + x0] + integral[y0 * stride + x0];
                    resized[row * FrameSignals.SIDE + column] = sum / (double)((y1 - y0) * (x1 - x0));
                }
            }
        }

        /// <summary>
        /// Deriva i due dHash dalla matrice ridotta senza quantizzare le medie area
        /// </summary>
        private static void ComputeHashes(double[] frame, out ulong hash0, out ulong hash1)
        {
            double[] horizontalCells = new double[8 * 9];
            double[] verticalCells = new double[9 * 8];
            for (int row = 0; row < FrameSignals.SIDE; row++)
            {
                int offset = row * FrameSignals.SIDE;
                for (int column = 0; column < FrameSignals.SIDE; column++)
                {
                    double value = frame[offset + column];
                    horizontalCells[(row / 9) * 9 + column / 8] += value;
                    verticalCells[(row / 8) * 8 + column / 9] += value;
                }
            }

            hash0 = 0UL;
            hash1 = 0UL;
            int bit = 0;
            for (int row = 0; row < 8; row++)
            {
                for (int column = 0; column < 8; column++)
                {
                    if (horizontalCells[row * 9 + column + 1] > horizontalCells[row * 9 + column])
                        hash0 |= 1UL << (63 - bit);
                    if (verticalCells[(row + 1) * 8 + column] > verticalCells[row * 8 + column])
                        hash1 |= 1UL << (63 - bit);
                    bit++;
                }
            }
        }

        #endregion

        #region Metodi privati - Scelta

        /// <summary>
        /// Misura tutte le coppie di viewport e conserva la migliore secondo copertura e mediana
        /// </summary>
        private ViewportCandidate FindBestCandidate(VariantSignals[][] source, VariantSignals[][] language, CancellationToken cancellationToken)
        {
            int variantCount = BuildVariants().Length;
            ViewportCandidate[] candidates = new ViewportCandidate[variantCount * variantCount];
            ParallelOptions options = new ParallelOptions();
            options.CancellationToken = cancellationToken;
            options.MaxDegreeOfParallelism = ParallelismHelper.ResolveDefaultMaxDegree();
            Parallel.For(0, candidates.Length, options, candidateIndex =>
            {
                int sourceVariant = candidateIndex / variantCount;
                int languageVariant = candidateIndex % variantCount;
                double matchRateSum = 0.0;
                double medianDistanceSum = 0.0;
                for (int windowIndex = 0; windowIndex < source.Length; windowIndex++)
                {
                    VariantSignals sourceSignals = source[windowIndex][sourceVariant];
                    VariantSignals languageSignals = language[windowIndex][languageVariant];
                    List<int> distances = new List<int>(sourceSignals.Count);
                    for (int sourceIndex = 0; sourceIndex < sourceSignals.Count; sourceIndex++)
                    {
                        int bestDistance = int.MaxValue;
                        for (int languageIndex = 0; languageIndex < languageSignals.Count; languageIndex++)
                        {
                            int distance = BitOperations.PopCount(sourceSignals.Hash0[sourceIndex] ^ languageSignals.Hash0[languageIndex]) +
                                BitOperations.PopCount(sourceSignals.Hash1[sourceIndex] ^ languageSignals.Hash1[languageIndex]);
                            if (distance < bestDistance)
                                bestDistance = distance;
                        }
                        distances.Add(bestDistance);
                    }
                    distances.Sort();
                    int accepted = 0;
                    for (int i = 0; i < distances.Count; i++)
                    {
                        if (distances[i] <= MATCH_DISTANCE)
                            accepted++;
                    }
                    matchRateSum += distances.Count > 0 ? accepted / (double)distances.Count : 0.0;
                    medianDistanceSum += Median(distances);
                }
                candidates[candidateIndex] = new ViewportCandidate(sourceVariant, languageVariant,
                    matchRateSum / source.Length,
                    medianDistanceSum / source.Length);
            });

            ViewportVariant[] variants = BuildVariants();
            ViewportCandidate best = null;
            for (int i = 0; i < candidates.Length; i++)
            {
                if (best == null || IsBetter(candidates[i], best, variants))
                    best = candidates[i];
            }
            return best;
        }

        /// <summary>
        /// Applica l'ordinamento stabile usato dal contratto di calibrazione
        /// </summary>
        private static bool IsBetter(ViewportCandidate candidate, ViewportCandidate current, ViewportVariant[] variants)
        {
            int comparison = candidate.MatchRate.CompareTo(current.MatchRate);
            if (comparison != 0)
                return comparison > 0;
            comparison = current.MedianDistance.CompareTo(candidate.MedianDistance);
            if (comparison != 0)
                return comparison > 0;

            ViewportVariant candidateSource = variants[candidate.SourceVariantIndex];
            ViewportVariant currentSource = variants[current.SourceVariantIndex];
            comparison = candidateSource.Zoom.CompareTo(currentSource.Zoom);
            if (comparison != 0)
                return comparison > 0;
            comparison = candidateSource.Shift.CompareTo(currentSource.Shift);
            if (comparison != 0)
                return comparison > 0;
            ViewportVariant candidateLanguage = variants[candidate.LanguageVariantIndex];
            ViewportVariant currentLanguage = variants[current.LanguageVariantIndex];
            comparison = candidateLanguage.Zoom.CompareTo(currentLanguage.Zoom);
            if (comparison != 0)
                return comparison > 0;
            return candidateLanguage.Shift > currentLanguage.Shift;
        }

        /// <summary>
        /// Crea la griglia ordinata di zoom e traslazioni candidate
        /// </summary>
        private static ViewportVariant[] BuildVariants()
        {
            List<ViewportVariant> result = new List<ViewportVariant>();
            for (int zoomIndex = 0; zoomIndex < s_zooms.Length; zoomIndex++)
            {
                for (int shiftIndex = 0; shiftIndex < s_shifts.Length; shiftIndex++)
                    result.Add(new ViewportVariant(s_zooms[zoomIndex], s_shifts[shiftIndex]));
            }
            return result.ToArray();
        }

        /// <summary>
        /// Calcola la mediana di una lista già ordinata
        /// </summary>
        private static double Median(List<int> values)
        {
            if (values.Count == 0)
                return double.PositiveInfinity;
            int middle = values.Count / 2;
            if ((values.Count & 1) != 0)
                return values[middle];
            return (values[middle - 1] + values[middle]) / 2.0;
        }

        /// <summary>
        /// Mantiene la finestra dentro la durata disponibile senza cambiare i punti standard dei film lunghi
        /// </summary>
        private static double ResolveStart(double requestedSeconds, int windowSeconds, int durationMs)
        {
            double lastStart = Math.Max(0.0, durationMs / 1000.0 - windowSeconds);
            return Math.Min(requestedSeconds, lastStart);
        }

        /// <summary>
        /// Recupera l'ultima riga utile della diagnostica FFmpeg
        /// </summary>
        private static string GetLastErrorLine(string text)
        {
            string[] lines = (text ?? "").Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    return lines[i].Trim();
            }
            return "nessuna diagnostica";
        }

        #endregion

        #region Classi private

        private sealed class RawFrameWindow
        {
            public RawFrameWindow(byte[] pixels, int count)
            {
                this.Pixels = pixels;
                this.Count = count;
            }

            public byte[] Pixels { get; private set; }
            public int Count { get; private set; }
        }

        private sealed class VariantSignals
        {
            public VariantSignals(int count)
            {
                this.Hash0 = new ulong[count];
                this.Hash1 = new ulong[count];
            }

            public ulong[] Hash0 { get; private set; }
            public ulong[] Hash1 { get; private set; }
            public int Count { get { return this.Hash0.Length; } }
        }

        private sealed class ViewportVariant
        {
            public ViewportVariant(double zoom, double shift)
            {
                this.Zoom = zoom;
                this.Shift = shift;
            }

            public double Zoom { get; private set; }
            public double Shift { get; private set; }
        }

        private sealed class ViewportCandidate
        {
            public ViewportCandidate(int sourceVariantIndex, int languageVariantIndex, double matchRate, double medianDistance)
            {
                this.SourceVariantIndex = sourceVariantIndex;
                this.LanguageVariantIndex = languageVariantIndex;
                this.MatchRate = matchRate;
                this.MedianDistance = medianDistance;
            }

            public int SourceVariantIndex { get; private set; }
            public int LanguageVariantIndex { get; private set; }
            public double MatchRate { get; private set; }
            public double MedianDistance { get; private set; }
        }

        #endregion
    }
}
