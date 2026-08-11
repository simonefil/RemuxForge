using OpenCvSharp;
using OpenCvSharp.Features2D;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RemuxForge.Core.Analysis.Deep.Features
{
    /// <summary>
    /// Backend CPU SIFT basato su OpenCvSharp con ratio test, cross-check e RANSAC
    /// </summary>
    public sealed class OpenCvSiftFeatureMatcher : IDisposable
    {
        #region Costanti

        /// <summary>
        /// Identificatore stabile del backend CPU OpenCV
        /// </summary>
        public const string BACKEND_NAME = "opencv-sift-cpu";

        #endregion

        #region Variabili di classe

        private readonly FrameFeatureMatcherOptions _options;

        private readonly object _availabilityLock;

        private bool _availabilityChecked;

        private bool _available;

        private string _availabilityRejectReason;

        private SIFT _sift;

        private BFMatcher _matcher;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore con parametri predefiniti
        /// </summary>
        public OpenCvSiftFeatureMatcher()
            : this(new FrameFeatureMatcherOptions())
        {
        }

        /// <summary>
        /// Costruttore con parametri espliciti
        /// </summary>
        /// <param name="options">Parametri matcher</param>
        public OpenCvSiftFeatureMatcher(FrameFeatureMatcherOptions options)
        {
            this._options = options ?? throw new ArgumentNullException(nameof(options));
            this._availabilityLock = new object();
            this._availabilityRejectReason = "";
            this._options.Validate();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Verifica che OpenCvSharp e il runtime nativo siano caricabili
        /// </summary>
        public bool IsAvailable(out string rejectReason)
        {
            lock (this._availabilityLock)
            {
                if (!this._availabilityChecked)
                {
                    try
                    {
                        string version = Cv2.GetVersionString();
                        if (string.IsNullOrEmpty(version))
                        {
                            this._availabilityRejectReason = "OpenCV non restituisce una versione valida";
                            this._available = false;
                        }
                        else
                        {
                            using (SIFT.Create(1, this._options.OctaveLayers, this._options.ContrastThreshold, this._options.EdgeThreshold, this._options.Sigma))
                            {
                            }

                            this._available = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        this._availabilityRejectReason = "Backend OpenCV SIFT non disponibile: " + ex.Message;
                        this._available = false;
                    }

                    this._availabilityChecked = true;
                }

                rejectReason = this._availabilityRejectReason;
                return this._available;
            }
        }

        /// <summary>
        /// Estrae keypoint e descriptor SIFT da un frame grayscale
        /// </summary>
        public OpenCvSiftFeatureSet ExtractFeatures(byte[] grayscaleFrame, int width, int height)
        {
            Mat descriptors = null;
            KeyPoint[] keypoints;

            if (grayscaleFrame == null)
                throw new ArgumentNullException(nameof(grayscaleFrame));
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            if (grayscaleFrame.Length != width * height)
                throw new ArgumentException("La dimensione del buffer non coincide con larghezza e altezza", nameof(grayscaleFrame));
            if (!this.IsAvailable(out string rejectReason))
                throw new InvalidOperationException(rejectReason);

            try
            {
                descriptors = new Mat();
                this.EnsureWorkerResources();
                using (Mat image = Mat.FromPixelData(height, width, MatType.CV_8UC1, grayscaleFrame))
                {
                    this._sift.DetectAndCompute(image, null, out keypoints, descriptors, false);
                }

                return new OpenCvSiftFeatureSet(this.BackendName, width, height, keypoints, descriptors);
            }
            catch
            {
                if (descriptors != null)
                    descriptors.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Confronta descriptor SIFT e verifica la coerenza geometrica tramite omografia RANSAC
        /// </summary>
        public FrameFeatureMatchResult Match(OpenCvSiftFeatureSet sourceFeatures, OpenCvSiftFeatureSet languageFeatures)
        {
            List<DMatch> forwardRatioMatches;
            List<DMatch> reverseRatioMatches;

            if (!this.TryInitializeMatch(sourceFeatures, languageFeatures, out FrameFeatureMatchResult result))
                return result;

            this.EnsureWorkerResources();
            long phaseStart = Stopwatch.GetTimestamp();
            forwardRatioMatches = this.ApplyRatioTest(this._matcher.KnnMatch(sourceFeatures.Descriptors, languageFeatures.Descriptors, 2));
            reverseRatioMatches = this.ApplyRatioTest(this._matcher.KnnMatch(languageFeatures.Descriptors, sourceFeatures.Descriptors, 2));
            result.DescriptorMatchingTicks = Stopwatch.GetTimestamp() - phaseStart;
            return this.CompleteGeometricMatch(result, sourceFeatures, languageFeatures, forwardRatioMatches, reverseRatioMatches);
        }
        private FrameFeatureMatchResult CompleteGeometricMatch(FrameFeatureMatchResult result, OpenCvSiftFeatureSet source, OpenCvSiftFeatureSet language, List<DMatch> forwardRatioMatches, List<DMatch> reverseRatioMatches)
        {
            List<DMatch> reciprocalMatches = this.CrossCheck(forwardRatioMatches, reverseRatioMatches);
            return this.CompleteGeometricMatch(result, source, language, reciprocalMatches, forwardRatioMatches.Count);
        }

        private FrameFeatureMatchResult CompleteGeometricMatch(FrameFeatureMatchResult result, OpenCvSiftFeatureSet source, OpenCvSiftFeatureSet language, List<DMatch> reciprocalMatches, int forwardRatioMatchCount)
        {
            List<Point2d> sourcePoints;
            List<Point2d> languagePoints;
            List<Point2d> sourceInlierPoints = new List<Point2d>();
            List<Point2d> languageInlierPoints = new List<Point2d>();

            result.RatioMatchCount = forwardRatioMatchCount;
            result.ReciprocalMatchCount = reciprocalMatches.Count;
            if (reciprocalMatches.Count < this._options.MinReciprocalMatches)
            {
                result.RejectReason = "Match reciproci insufficienti";
                return result;
            }

            sourcePoints = new List<Point2d>(reciprocalMatches.Count);
            languagePoints = new List<Point2d>(reciprocalMatches.Count);
            for (int i = 0; i < reciprocalMatches.Count; i++)
            {
                Point2f sourcePoint = source.Keypoints[reciprocalMatches[i].QueryIdx].Pt;
                Point2f languagePoint = language.Keypoints[reciprocalMatches[i].TrainIdx].Pt;
                sourcePoints.Add(new Point2d(sourcePoint.X, sourcePoint.Y));
                languagePoints.Add(new Point2d(languagePoint.X, languagePoint.Y));
            }

            long phaseStart = Stopwatch.GetTimestamp();
            using (Mat inlierMask = new Mat())
            using (Mat homography = Cv2.FindHomography(sourcePoints, languagePoints, HomographyMethods.Ransac, this._options.RansacReprojectionThreshold, inlierMask))
            {
                if (homography.Empty() || inlierMask.Empty())
                {
                    result.RejectReason = "Trasformazione geometrica non stimabile";
                    return result;
                }

                for (int i = 0; i < reciprocalMatches.Count; i++)
                {
                    if (inlierMask.At<byte>(i, 0) != 0)
                    {
                        sourceInlierPoints.Add(sourcePoints[i]);
                        languageInlierPoints.Add(languagePoints[i]);
                    }
                }

                result.Homography = this.ReadHomography(homography);
                result.MeanReprojectionError = this.ComputeMeanReprojectionError(sourceInlierPoints, languageInlierPoints, homography);
            }
            result.GeometryTicks = Stopwatch.GetTimestamp() - phaseStart;

            result.InlierCount = sourceInlierPoints.Count;
            result.InlierRatio = reciprocalMatches.Count > 0 ? result.InlierCount / (double)reciprocalMatches.Count : 0.0;
            result.SourceCoverage = this.ComputeCoverage(sourceInlierPoints, source.Width, source.Height);
            result.LanguageCoverage = this.ComputeCoverage(languageInlierPoints, language.Width, language.Height);
            result.Score = this.ComputeScore(result);

            if (result.InlierCount < this._options.MinInliers)
            {
                result.RejectReason = "Inlier geometrici insufficienti";
                return result;
            }
            if (result.InlierRatio < this._options.MinInlierRatio)
            {
                result.RejectReason = "Rapporto inlier insufficiente";
                return result;
            }
            if (result.SourceCoverage < this._options.MinCoverage || result.LanguageCoverage < this._options.MinCoverage)
            {
                result.RejectReason = "Copertura spaziale insufficiente";
                return result;
            }
            if (result.MeanReprojectionError > this._options.MaxMeanReprojectionError || double.IsNaN(result.MeanReprojectionError) || double.IsInfinity(result.MeanReprojectionError))
            {
                result.RejectReason = "Errore di riproiezione eccessivo";
                return result;
            }
            if (!this.IsHomographyPlausible(result.Homography, source.Width, source.Height))
            {
                result.RejectReason = "Omografia geometricamente non valida";
                return result;
            }

            result.Accepted = true;
            return result;
        }

        /// <summary>
        /// Il backend corrente non mantiene risorse condivise tra chiamate
        /// </summary>
        public void Dispose()
        {
            this._matcher?.Dispose();
            this._matcher = null;
            this._sift?.Dispose();
            this._sift = null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Identificativo stabile del backend CPU
        /// </summary>
        public string BackendName { get { return BACKEND_NAME; } }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Valida i parametri indipendentemente dal runtime nativo
        /// </summary>
        /// <summary>
        /// Crea una sola istanza SIFT e BFMatcher per il worker corrente
        /// </summary>
        private void EnsureWorkerResources()
        {
            if (this._sift == null)
                this._sift = SIFT.Create(this._options.MaxFeatures, this._options.OctaveLayers, this._options.ContrastThreshold, this._options.EdgeThreshold, this._options.Sigma);
            if (this._matcher == null)
                this._matcher = new BFMatcher(NormTypes.L2, false);
        }

        /// <summary>
        /// Valida i feature set e inizializza la diagnostica comune CPU/Vulkan
        /// </summary>
        private bool TryInitializeMatch(OpenCvSiftFeatureSet sourceFeatures, OpenCvSiftFeatureSet languageFeatures, out FrameFeatureMatchResult result)
        {
            result = new FrameFeatureMatchResult();
            result.BackendName = this.BackendName;
            if (sourceFeatures == null || languageFeatures == null || !string.Equals(sourceFeatures.BackendName, this.BackendName, StringComparison.Ordinal) || !string.Equals(languageFeatures.BackendName, this.BackendName, StringComparison.Ordinal))
            {
                result.RejectReason = "Feature prodotte da un backend incompatibile";
                return false;
            }

            result.SourceKeypointCount = sourceFeatures.KeypointCount;
            result.LanguageKeypointCount = languageFeatures.KeypointCount;
            if (sourceFeatures.KeypointCount < this._options.MinKeypoints || languageFeatures.KeypointCount < this._options.MinKeypoints || sourceFeatures.Descriptors.Empty() || languageFeatures.Descriptors.Empty())
            {
                result.RejectReason = "Keypoint insufficienti";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Verifica finitezza, orientamento e scala dell'omografia stimata
        /// </summary>
        private bool IsHomographyPlausible(double[] homography, int width, int height)
        {
            if (homography == null || homography.Length != 9 || width <= 0 || height <= 0)
                return false;
            for (int i = 0; i < homography.Length; i++)
            {
                if (double.IsNaN(homography[i]) || double.IsInfinity(homography[i]))
                    return false;
            }

            Point2d[] corners = new Point2d[]
            {
                new Point2d(0.0, 0.0),
                new Point2d(width, 0.0),
                new Point2d(width, height),
                new Point2d(0.0, height)
            };
            Point2d[] projected = new Point2d[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                double denominator = (homography[6] * corners[i].X) + (homography[7] * corners[i].Y) + homography[8];
                if (Math.Abs(denominator) < 1.0e-12)
                    return false;
                projected[i] = new Point2d(
                    ((homography[0] * corners[i].X) + (homography[1] * corners[i].Y) + homography[2]) / denominator,
                    ((homography[3] * corners[i].X) + (homography[4] * corners[i].Y) + homography[5]) / denominator);
            }
            double signedArea = 0.0;
            for (int i = 0; i < projected.Length; i++)
            {
                Point2d current = projected[i];
                Point2d next = projected[(i + 1) % projected.Length];
                if (double.IsNaN(current.X) || double.IsInfinity(current.X) || double.IsNaN(current.Y) || double.IsInfinity(current.Y))
                    return false;
                signedArea += (current.X * next.Y) - (next.X * current.Y);
            }

            if (signedArea <= 0.0)
                return false;
            double areaRatio = (signedArea * 0.5) / (width * (double)height);
            return areaRatio >= this._options.MinHomographyAreaRatio && areaRatio <= this._options.MaxHomographyAreaRatio;
        }

        /// <summary>
        /// Applica il Lowe ratio test ai due nearest neighbour di ogni descriptor
        /// </summary>
        private List<DMatch> ApplyRatioTest(DMatch[][] matches)
        {
            List<DMatch> result = new List<DMatch>();
            if (matches == null)
                return result;

            for (int i = 0; i < matches.Length; i++)
            {
                if (matches[i] != null && matches[i].Length >= 2 && matches[i][0].Distance < matches[i][1].Distance * this._options.LoweRatio)
                {
                    result.Add(matches[i][0]);
                }
            }

            return result;
        }
       /// <summary>
        /// Conserva soltanto i match che concordano nelle due direzioni
        /// </summary>
        private List<DMatch> CrossCheck(List<DMatch> forwardMatches, List<DMatch> reverseMatches)
        {
            List<DMatch> result = new List<DMatch>();
            Dictionary<int, int> reverseMap = new Dictionary<int, int>();

            for (int i = 0; i < reverseMatches.Count; i++)
            {
                reverseMap[reverseMatches[i].QueryIdx] = reverseMatches[i].TrainIdx;
            }

            for (int i = 0; i < forwardMatches.Count; i++)
            {
                if (reverseMap.TryGetValue(forwardMatches[i].TrainIdx, out int reverseSourceIndex) && reverseSourceIndex == forwardMatches[i].QueryIdx)
                {
                    result.Add(forwardMatches[i]);
                }
            }

            return result;
        }

        /// <summary>
        /// Calcola la copertura del bounding box degli inlier rispetto al frame
        /// </summary>
        private double ComputeCoverage(List<Point2d> points, int width, int height)
        {
            if (points == null || points.Count < 2 || width <= 0 || height <= 0)
                return 0.0;

            double minX = points[0].X;
            double maxX = points[0].X;
            double minY = points[0].Y;
            double maxY = points[0].Y;
            for (int i = 1; i < points.Count; i++)
            {
                if (points[i].X < minX) { minX = points[i].X; }
                if (points[i].X > maxX) { maxX = points[i].X; }
                if (points[i].Y < minY) { minY = points[i].Y; }
                if (points[i].Y > maxY) { maxY = points[i].Y; }
            }

            double coverage = ((maxX - minX) * (maxY - minY)) / (width * (double)height);
            if (coverage < 0.0) { return 0.0; }
            if (coverage > 1.0) { return 1.0; }
            return coverage;
        }

        /// <summary>
        /// Calcola l'errore medio fra punti language e source riproiettati
        /// </summary>
        private double ComputeMeanReprojectionError(List<Point2d> sourcePoints, List<Point2d> languagePoints, Mat homography)
        {
            if (sourcePoints.Count == 0 || sourcePoints.Count != languagePoints.Count)
                return double.MaxValue;

            Point2d[] projected = Cv2.PerspectiveTransform(sourcePoints, homography);
            double total = 0.0;
            for (int i = 0; i < projected.Length; i++)
            {
                double dx = projected[i].X - languagePoints[i].X;
                double dy = projected[i].Y - languagePoints[i].Y;
                total += Math.Sqrt((dx * dx) + (dy * dy));
            }

            return total / projected.Length;
        }

        /// <summary>
        /// Copia l'omografia in un formato indipendente da OpenCV
        /// </summary>
        private double[] ReadHomography(Mat homography)
        {
            double[] result = new double[9];
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 3; column++)
                {
                    result[(row * 3) + column] = homography.At<double>(row, column);
                }
            }

            return result;
        }

        /// <summary>
        /// Costruisce una confidence diagnostica senza partecipare ancora alla pipeline decisionale
        /// </summary>
        private double ComputeScore(FrameFeatureMatchResult result)
        {
            double quantityScore = Math.Min(1.0, result.InlierCount / 30.0);
            double coverageScore = Math.Min(1.0, Math.Min(result.SourceCoverage, result.LanguageCoverage) / 0.25);
            double score = (result.InlierRatio * 0.55) + (quantityScore * 0.25) + (coverageScore * 0.20);
            if (score < 0.0) { return 0.0; }
            if (score > 1.0) { return 1.0; }
            return score;
        }

        #endregion
    }
}
