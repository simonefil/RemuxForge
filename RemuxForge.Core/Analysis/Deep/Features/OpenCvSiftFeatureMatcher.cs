using OpenCvSharp;
using OpenCvSharp.Features2D;
using RemuxForge.Core.Models;
using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace RemuxForge.Core.Analysis.Deep.Features
{
    /// <summary>
    /// Matcher SIFT CPU basato su OpenCvSharp con ratio test, cross-check e verifica geometrica RANSAC
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

        /// <summary>
        /// Parametri di estrazione, ratio test e verifica geometrica
        /// </summary>
        private readonly FrameFeatureMatcherOptions _options;

        /// <summary>
        /// Sincronizza il probe del runtime OpenCV
        /// </summary>
        private readonly object _availabilityLock;

        /// <summary>
        /// Indica che la disponibilità del runtime è già stata verificata
        /// </summary>
        private bool _availabilityChecked;

        /// <summary>
        /// Esito memorizzato del probe OpenCV
        /// </summary>
        private bool _available;

        /// <summary>
        /// Motivo diagnostico dell'indisponibilità del runtime
        /// </summary>
        private string _availabilityRejectReason;

        /// <summary>
        /// Estrattore SIFT nativo posseduto dal matcher
        /// </summary>
        private SIFT _sift;

        /// <summary>
        /// Matcher brute-force nativo posseduto dall'istanza
        /// </summary>
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
        /// Verifica che OpenCvSharp e il runtime nativo siano caricabili e memorizza l'esito del probe
        /// </summary>
        /// <param name="rejectReason">Motivo della mancata disponibilità del runtime</param>
        /// <returns>True se OpenCvSharp e il runtime nativo sono disponibili</returns>
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
                            this._availabilityRejectReason = AppText.T("deep.temporal.matcher.invalidOpenCvVersion");
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
                        this._availabilityRejectReason = AppText.F("deep.temporal.matcher.openCvUnavailable", ex.Message);
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
        /// <param name="grayscaleFrame">Buffer del frame in scala di grigi, con un byte per pixel</param>
        /// <param name="width">Larghezza del frame in pixel</param>
        /// <param name="height">Altezza del frame in pixel</param>
        /// <returns>Feature SIFT estratte dal frame</returns>
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
                throw new ArgumentException(AppText.T("deep.temporal.matcher.invalidFrameBufferSize"), nameof(grayscaleFrame));
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
        /// <param name="sourceFeatures">Feature estratte dal frame source</param>
        /// <param name="languageFeatures">Feature estratte dal frame language</param>
        /// <returns>Risultato del confronto dei descriptor e della verifica geometrica</returns>
        public FrameFeatureMatchResult Match(OpenCvSiftFeatureSet sourceFeatures, OpenCvSiftFeatureSet languageFeatures)
        {
            return this.Match(sourceFeatures, languageFeatures, 0);
        }

        /// <summary>
        /// Confronta descriptor e completa la verifica geometrica usando il seed RANSAC fornito
        /// </summary>
        /// <param name="sourceFeatures">Feature estratte dal frame source</param>
        /// <param name="languageFeatures">Feature estratte dal frame language</param>
        /// <param name="randomSeed">Seed usato per rendere deterministico RANSAC</param>
        /// <returns>Risultato del confronto dei descriptor e della verifica geometrica</returns>
        public FrameFeatureMatchResult Match(OpenCvSiftFeatureSet sourceFeatures, OpenCvSiftFeatureSet languageFeatures, int randomSeed)
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
            return this.CompleteGeometricMatch(result, sourceFeatures, languageFeatures, forwardRatioMatches, reverseRatioMatches, randomSeed);
        }
        /// <summary>
        /// Interseca i ratio match nei due versi prima della verifica geometrica
        /// </summary>
        /// <param name="result">Risultato da completare</param>
        /// <param name="source">Feature estratte dal frame source</param>
        /// <param name="language">Feature estratte dal frame language</param>
        /// <param name="forwardRatioMatches">Match source-language dopo il ratio test</param>
        /// <param name="reverseRatioMatches">Match language-source dopo il ratio test</param>
        /// <param name="randomSeed">Seed deterministico di RANSAC</param>
        /// <returns>Risultato geometrico completo</returns>
        private FrameFeatureMatchResult CompleteGeometricMatch(FrameFeatureMatchResult result, OpenCvSiftFeatureSet source, OpenCvSiftFeatureSet language, List<DMatch> forwardRatioMatches, List<DMatch> reverseRatioMatches, int randomSeed)
        {
            List<DMatch> reciprocalMatches = this.CrossCheck(forwardRatioMatches, reverseRatioMatches);
            return this.CompleteGeometricMatch(result, source, language, reciprocalMatches, forwardRatioMatches.Count, randomSeed);
        }

        /// <summary>
        /// Stima l'omografia e valida copertura, inlier ed errore di riproiezione
        /// </summary>
        /// <param name="result">Risultato da completare</param>
        /// <param name="source">Feature estratte dal frame source</param>
        /// <param name="language">Feature estratte dal frame language</param>
        /// <param name="reciprocalMatches">Match reciproci da verificare</param>
        /// <param name="forwardRatioMatchCount">Numero di match forward prima della reciprocità</param>
        /// <param name="randomSeed">Seed deterministico di RANSAC</param>
        /// <returns>Risultato geometrico completo</returns>
        private FrameFeatureMatchResult CompleteGeometricMatch(FrameFeatureMatchResult result, OpenCvSiftFeatureSet source, OpenCvSiftFeatureSet language, List<DMatch> reciprocalMatches, int forwardRatioMatchCount, int randomSeed)
        {
            List<Point2d> sourcePoints;
            List<Point2d> languagePoints;
            List<Point2d> sourceInlierPoints = new List<Point2d>();
            List<Point2d> languageInlierPoints = new List<Point2d>();

            result.RatioMatchCount = forwardRatioMatchCount;
            result.ReciprocalMatchCount = reciprocalMatches.Count;
            if (reciprocalMatches.Count < this._options.MinReciprocalMatches)
            {
                result.RejectReason = AppText.T("deep.temporal.matcher.insufficientReciprocalMatches");
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
            Cv2.SetTheRNG(unchecked((uint)randomSeed));
            using (Mat inlierMask = new Mat())
            using (Mat homography = Cv2.FindHomography(sourcePoints, languagePoints, HomographyMethods.Ransac, this._options.RansacReprojectionThreshold, inlierMask))
            {
                if (homography.Empty() || inlierMask.Empty())
                {
                    result.RejectReason = AppText.T("deep.temporal.matcher.unestimableGeometry");
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
                result.RejectReason = AppText.T("deep.temporal.matcher.insufficientGeometricInliers");
                return result;
            }
            if (result.InlierRatio < this._options.MinInlierRatio)
            {
                result.RejectReason = AppText.T("deep.temporal.matcher.insufficientInlierRatio");
                return result;
            }
            if (result.SourceCoverage < this._options.MinCoverage || result.LanguageCoverage < this._options.MinCoverage)
            {
                result.RejectReason = AppText.T("deep.temporal.matcher.insufficientSpatialCoverage");
                return result;
            }
            if (result.MeanReprojectionError > this._options.MaxMeanReprojectionError || double.IsNaN(result.MeanReprojectionError) || double.IsInfinity(result.MeanReprojectionError))
            {
                result.RejectReason = AppText.T("deep.temporal.matcher.excessiveReprojectionError");
                return result;
            }
            if (!this.IsHomographyPlausible(result.Homography, source.Width, source.Height))
            {
                result.RejectReason = AppText.T("deep.temporal.matcher.invalidHomography");
                return result;
            }

            result.Accepted = true;
            return result;
        }

        /// <summary>
        /// Rilascia le risorse native possedute dal matcher
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
        /// Valida i feature set e prepara la diagnostica comune dei matcher
        /// </summary>
        /// <param name="sourceFeatures">Feature estratte dal frame source</param>
        /// <param name="languageFeatures">Feature estratte dal frame language</param>
        /// <param name="result">Risultato diagnostico inizializzato dal metodo</param>
        /// <returns>True se i feature set sono compatibili e sufficienti per il confronto</returns>
        private bool TryInitializeMatch(OpenCvSiftFeatureSet sourceFeatures, OpenCvSiftFeatureSet languageFeatures, out FrameFeatureMatchResult result)
        {
            result = new FrameFeatureMatchResult();
            result.BackendName = this.BackendName;
            if (sourceFeatures == null || languageFeatures == null || !string.Equals(sourceFeatures.BackendName, this.BackendName, StringComparison.Ordinal) || !string.Equals(languageFeatures.BackendName, this.BackendName, StringComparison.Ordinal))
            {
                result.RejectReason = AppText.T("deep.temporal.matcher.incompatibleFeatureBackend");
                return false;
            }

            result.SourceKeypointCount = sourceFeatures.KeypointCount;
            result.LanguageKeypointCount = languageFeatures.KeypointCount;
            if (sourceFeatures.KeypointCount < this._options.MinKeypoints || languageFeatures.KeypointCount < this._options.MinKeypoints || sourceFeatures.Descriptors.Empty() || languageFeatures.Descriptors.Empty())
            {
                result.RejectReason = AppText.T("deep.temporal.matcher.insufficientKeypoints");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Verifica finitezza, orientamento e scala dell'omografia stimata
        /// </summary>
        /// <param name="homography">Valori dell'omografia disposti per righe</param>
        /// <param name="width">Larghezza del frame di riferimento in pixel</param>
        /// <param name="height">Altezza del frame di riferimento in pixel</param>
        /// <returns>True se l'omografia proietta il frame in modo plausibile</returns>
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
        /// <param name="matches">Match dei due nearest neighbour per ogni descriptor</param>
        /// <returns>Match che superano il ratio test</returns>
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
        /// <param name="forwardMatches">Match dal frame source al frame language</param>
        /// <param name="reverseMatches">Match dal frame language al frame source</param>
        /// <returns>Match presenti in entrambe le direzioni</returns>
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
        /// <param name="points">Punti degli inlier nel sistema di coordinate del frame</param>
        /// <param name="width">Larghezza del frame in pixel</param>
        /// <param name="height">Altezza del frame in pixel</param>
        /// <returns>Rapporto fra l'area del bounding box e l'area del frame, limitato all'intervallo 0-1</returns>
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
        /// Calcola l'errore medio fra i punti language e i punti source riproiettati nel sistema language
        /// </summary>
        /// <param name="sourcePoints">Punti source da riproiettare</param>
        /// <param name="languagePoints">Punti language di riferimento</param>
        /// <param name="homography">Omografia usata per la riproiezione</param>
        /// <returns>Errore medio di riproiezione oppure il valore massimo se gli input non sono coerenti</returns>
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
        /// <param name="homography">Matrice OpenCV dell'omografia</param>
        /// <returns>Valori dell'omografia disposti per righe</returns>
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
        /// Costruisce la confidence condivisa usata per ordinare alternative temporali già accettate
        /// </summary>
        /// <param name="result">Risultato con le metriche del confronto</param>
        /// <returns>Punteggio normalizzato nell'intervallo 0-1</returns>
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
