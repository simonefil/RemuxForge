using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Verifica globale DeepAnalysis su punti distribuiti della timeline source
    /// </summary>
    public class DeepGlobalVerifier
    {
        #region Variabili di classe

        private readonly DeepAnalysisConfig _deepAnalysisConfig;

        private readonly VideoSyncConfig _videoSyncConfig;

        private readonly DeepVisualFrameAnalyzer _visualFrameAnalyzer;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="deepAnalysisConfig">Configurazione DeepAnalysis</param>
        /// <param name="videoSyncConfig">Configurazione metrica video</param>
        /// <param name="visualFrameAnalyzer">Analyzer visuale frame-based</param>
        public DeepGlobalVerifier(DeepAnalysisConfig deepAnalysisConfig, VideoSyncConfig videoSyncConfig, DeepVisualFrameAnalyzer visualFrameAnalyzer)
        {
            this._deepAnalysisConfig = deepAnalysisConfig;
            this._videoSyncConfig = videoSyncConfig;
            this._visualFrameAnalyzer = visualFrameAnalyzer;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Verifica che l'EditMap risultante produca match visuali coerenti lungo tutto il file
        /// </summary>
        /// <param name="sourceFile">File source</param>
        /// <param name="langFile">File lang</param>
        /// <param name="regions">Regioni offset rilevate</param>
        /// <param name="operations">Operazioni EditMap espresse sulla timeline lang</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="initialDelayMs">Delay iniziale realmente applicato al mux</param>
        /// <param name="sourceDurationMs">Durata source in millisecondi</param>
        /// <param name="geometryCropSourceToFourThree">True se il source viene croppato per match visuale 4:3</param>
        /// <param name="geometryCropLanguageToFourThree">True se il lang viene croppato per match visuale 4:3</param>
        /// <param name="baselineMse">MSE medio verificato</param>
        /// <param name="verification">Diagnostica verifica globale</param>
        /// <returns>True se la mappa supera la verifica globale</returns>
        public bool Verify(string sourceFile, string langFile, List<OffsetRegion> regions, List<EditOperation> operations, double inverseRatio, int initialDelayMs, int sourceDurationMs, bool geometryCropSourceToFourThree, bool geometryCropLanguageToFourThree, out double baselineMse, out DeepAnalysisGlobalVerificationDiagnostic verification)
        {
            bool verified;
            int validPoints = 0;
            double totalMse = 0.0;
            int pointsChecked;
            double stepMs;
            double maxMse = 0.0;
            double dynamicThreshold;
            List<double> allMse = new List<double>();
            double[] pointMse;
            bool[] pointValid;
            ParallelOptions parallelOptions;
            List<OffsetRegion> verificationRegions;
            baselineMse = 0.0;
            verification = new DeepAnalysisGlobalVerificationDiagnostic();
            verificationRegions = this.BuildOperationalRegions(regions, operations, inverseRatio, initialDelayMs, sourceDurationMs);
            stepMs = sourceDurationMs / (double)(this._deepAnalysisConfig.GlobalVerifyPoints + 1);
            pointMse = new double[this._deepAnalysisConfig.GlobalVerifyPoints + 1];
            pointValid = new bool[this._deepAnalysisConfig.GlobalVerifyPoints + 1];
            parallelOptions = new ParallelOptions();
            parallelOptions.MaxDegreeOfParallelism = ParallelismHelper.ResolveDefaultMaxDegree();

            Parallel.For(1, this._deepAnalysisConfig.GlobalVerifyPoints + 1, parallelOptions, p =>
            {
                double mse;
                double srcPointMs = stepMs * p;

                if (this._visualFrameAnalyzer.TryComputeGlobalPointMse(sourceFile, langFile, verificationRegions, srcPointMs, inverseRatio, this._deepAnalysisConfig.CoarseFps, geometryCropSourceToFourThree, geometryCropLanguageToFourThree, out mse))
                {
                    pointMse[p] = mse;
                    pointValid[p] = true;
                }
            });

            for (int i = 1; i < pointValid.Length; i++)
            {
                if (pointValid[i])
                {
                    allMse.Add(pointMse[i]);
                    totalMse += pointMse[i];
                    if (pointMse[i] > maxMse) { maxMse = pointMse[i]; }
                }
            }

            pointsChecked = allMse.Count;
            if (pointsChecked > 0)
            {
                baselineMse = totalMse / pointsChecked;
            }

            dynamicThreshold = baselineMse * this._deepAnalysisConfig.VerifyMseMultiplier;
            if (dynamicThreshold < this._videoSyncConfig.MseThreshold)
            {
                dynamicThreshold = this._videoSyncConfig.MseThreshold;
            }

            for (int i = 0; i < allMse.Count; i++)
            {
                if (allMse[i] < dynamicThreshold)
                {
                    validPoints++;
                }
            }

            double ratio = (pointsChecked > 0) ? (double)validPoints / pointsChecked : 0.0;
            verified = ratio >= this._deepAnalysisConfig.GlobalVerifyMinRatio;
            verification.Verified = verified;
            verification.ValidPoints = validPoints;
            verification.PointsChecked = pointsChecked;
            verification.Ratio = ratio;
            verification.BaselineMse = baselineMse;
            verification.DynamicThreshold = dynamicThreshold;
            verification.MaxMse = maxMse;

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Verifica: " + validPoints + "/" + pointsChecked + " punti OK (MSE baseline=" + baselineMse.ToString("F1", CultureInfo.InvariantCulture) + ", soglia=" + dynamicThreshold.ToString("F1", CultureInfo.InvariantCulture) + ", max=" + maxMse.ToString("F1", CultureInfo.InvariantCulture) + ")");

            return verified;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Costruisce le regioni realmente applicate dall'EditMap finale
        /// </summary>
        /// <param name="regions">Regioni offset rilevate dalla timeline</param>
        /// <param name="operations">Operazioni EditMap espresse sulla timeline lang</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="initialDelayMs">Delay iniziale realmente applicato</param>
        /// <param name="sourceDurationMs">Durata source in millisecondi</param>
        /// <returns>Regioni offset simulate dopo l'applicazione dell'EditMap</returns>
        private List<OffsetRegion> BuildOperationalRegions(List<OffsetRegion> regions, List<EditOperation> operations, double inverseRatio, int initialDelayMs, int sourceDurationMs)
        {
            List<OffsetRegion> result = new List<OffsetRegion>();
            double currentOffsetMs = initialDelayMs;
            double currentStartSec = 0.0;
            double sourceDurationSec = sourceDurationMs / 1000.0;
            List<EditOperation> orderedOperations;

            if (operations == null || operations.Count == 0)
            {
                OffsetRegion constantRegion = new OffsetRegion();
                constantRegion.StartSrcSec = 0.0;
                constantRegion.EndSrcSec = sourceDurationSec;
                constantRegion.OffsetMs = currentOffsetMs;
                result.Add(constantRegion);
                return result;
            }

            orderedOperations = new List<EditOperation>(operations);
            orderedOperations.Sort((a, b) => this.ResolveVisualSourceTimestampMs(a).CompareTo(this.ResolveVisualSourceTimestampMs(b)));
            for (int i = 0; i < orderedOperations.Count; i++)
            {
                double operationSrcSec = this.ResolveVisualSourceTimestampMs(orderedOperations[i]) / 1000.0;
                if (operationSrcSec > currentStartSec)
                {
                    OffsetRegion region = new OffsetRegion();
                    region.StartSrcSec = currentStartSec;
                    region.EndSrcSec = operationSrcSec;
                    region.OffsetMs = currentOffsetMs;
                    result.Add(region);
                }

                currentOffsetMs += EditMapTimelineHelper.GetSourceOperationDeltaMs(orderedOperations[i], inverseRatio);

                currentStartSec = operationSrcSec;
            }

            if (sourceDurationSec > currentStartSec)
            {
                OffsetRegion lastRegion = new OffsetRegion();
                lastRegion.StartSrcSec = currentStartSec;
                lastRegion.EndSrcSec = sourceDurationSec;
                lastRegion.OffsetMs = currentOffsetMs;
                result.Add(lastRegion);
            }

            return result.Count > 0 ? result : regions;
        }

        /// <summary>
        /// Restituisce il boundary source/video di una operazione
        /// </summary>
        /// <param name="operation">Operazione editmap</param>
        /// <returns>Timestamp source video in millisecondi</returns>
        private int ResolveVisualSourceTimestampMs(EditOperation operation)
        {
            if (operation == null)
            {
                return 0;
            }

            if (operation.VisualSourceTimestampMs > 0)
            {
                return operation.VisualSourceTimestampMs;
            }

            return operation.SourceTimestampMs;
        }

        #endregion
    }
}
