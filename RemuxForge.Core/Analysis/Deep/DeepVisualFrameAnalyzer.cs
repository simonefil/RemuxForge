using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Analisi visuale frame-based per DeepAnalysis
    /// </summary>
    public class DeepVisualFrameAnalyzer
    {
        #region Delegati

        /// <summary>
        /// Estrae frame grayscale e timestamp reali da un segmento video
        /// </summary>
        /// <param name="filePath">File video</param>
        /// <param name="startMs">Inizio segmento in millisecondi</param>
        /// <param name="durationSec">Durata segmento in secondi</param>
        /// <param name="targetFps">FPS target, oppure 0 per frame nativi</param>
        /// <param name="geometryCropToFourThree">Normalizzazione geometrica 4:3</param>
        /// <param name="frames">Frame estratti</param>
        /// <param name="timestampsMs">Timestamp reali dei frame estratti</param>
        public delegate void FrameExtractor(string filePath, int startMs, double durationSec, double targetFps, bool geometryCropToFourThree, out List<byte[]> frames, out double[] timestampsMs);

        /// <summary>
        /// Calcola una metrica tra due frame
        /// </summary>
        /// <param name="firstFrame">Primo frame</param>
        /// <param name="secondFrame">Secondo frame</param>
        /// <returns>Valore metrica</returns>
        public delegate double FrameMetric(byte[] firstFrame, byte[] secondFrame);

        #endregion

        #region Variabili di classe

        private readonly DeepAnalysisConfig _config;

        private readonly FrameExtractor _extractor;

        private readonly FrameMetric _ssimMetric;

        private readonly FrameMetric _mseMetric;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="config">Configurazione DeepAnalysis</param>
        /// <param name="extractor">Delegato estrazione frame</param>
        /// <param name="ssimMetric">Metrica SSIM</param>
        /// <param name="mseMetric">Metrica MSE</param>
        public DeepVisualFrameAnalyzer(DeepAnalysisConfig config, FrameExtractor extractor, FrameMetric ssimMetric, FrameMetric mseMetric)
        {
            this._config = config;
            this._extractor = extractor;
            this._ssimMetric = ssimMetric;
            this._mseMetric = mseMetric;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Trova cali SSIM in una regione usando match per timestamp, utile per individuare discontinuita'
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="langFile">File lingua</param>
        /// <param name="regionStart">Inizio regione source in secondi</param>
        /// <param name="regionEnd">Fine regione source in secondi</param>
        /// <param name="offsetSec">Offset corrente in secondi</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="geometryCropSourceToFourThree">Normalizzazione geometrica 4:3 source</param>
        /// <param name="geometryCropLanguageToFourThree">Normalizzazione geometrica 4:3 lingua</param>
        /// <returns>Lista timestamp source dove inizia un dip</returns>
        public List<double> FindDipsInRegion(string sourceFile, string langFile, double regionStart, double regionEnd, double offsetSec, double inverseRatio, bool geometryCropSourceToFourThree, bool geometryCropLanguageToFourThree)
        {
            List<double> dips = new List<double>();
            double duration = regionEnd - regionStart;
            double langStart;
            int maxIdx;
            int consecutiveLow = 0;
            int dipStartIdx = -1;
            List<byte[]> srcFrames;
            double[] sourceTimestampsMs;
            List<byte[]> langFrames;
            double[] langTimestampsMs;
            double frameIntervalMs = 1000.0 / this._config.DenseScanFps;
            double toleranceMs = frameIntervalMs * 2.0;
            double srcRelMs;
            double targetLangMs;
            int nearestIdx;
            double nearestDistMs;
            // Estrae la regione source e la regione lang attesa in base all'offset corrente
            this._extractor(sourceFile, (int)(regionStart * 1000), duration, this._config.DenseScanFps, geometryCropSourceToFourThree, out srcFrames, out sourceTimestampsMs);
            if (srcFrames.Count < 4)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    FindDips: estrazione source fallita (" + srcFrames.Count + " frame su " + duration.ToString("F0", CultureInfo.InvariantCulture) + "s attesi)");
                return dips;
            }

            langStart = regionStart - offsetSec;
            if (Math.Abs(inverseRatio - 1.0) > 0.0001) { langStart = langStart * inverseRatio; }
            if (langStart < 0.0) { langStart = 0.0; }

            this._extractor(langFile, (int)(langStart * 1000), duration, this._config.DenseScanFps, geometryCropLanguageToFourThree, out langFrames, out langTimestampsMs);

            if (langFrames.Count < 4 || langTimestampsMs.Length < 4)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    FindDips: estrazione lang fallita (" + langFrames.Count + " frame su " + duration.ToString("F0", CultureInfo.InvariantCulture) + "s attesi)");
                return dips;
            }

            maxIdx = srcFrames.Count;
            double[] ssimValues = new double[maxIdx];
            for (int i = 0; i < maxIdx; i++)
            {
                // Con VFR il frame equivalente non è lo stesso indice: si usa il timestamp più vicino
                srcRelMs = sourceTimestampsMs[i] - sourceTimestampsMs[0];
                targetLangMs = langTimestampsMs[0] + srcRelMs;
                nearestIdx = this.NearestTimestampIndex(langTimestampsMs, targetLangMs);
                if (nearestIdx < 0)
                {
                    ssimValues[i] = 0.0;
                }
                else
                {
                    nearestDistMs = Math.Abs(langTimestampsMs[nearestIdx] - targetLangMs);
                    if (nearestDistMs > toleranceMs)
                    {
                        ssimValues[i] = 0.0;
                    }
                    else
                    {
                        ssimValues[i] = this._ssimMetric(srcFrames[i], langFrames[nearestIdx]);
                    }
                }
            }

            for (int i = 0; i < maxIdx; i++)
            {
                // Il dip viene accettato solo dopo un numero minimo di frame consecutivi sotto soglia
                if (ssimValues[i] < this._config.VerifyDipSsimThreshold)
                {
                    if (consecutiveLow == 0) { dipStartIdx = i; }
                    consecutiveLow++;

                    if (consecutiveLow >= this._config.DenseScanMinDipFrames)
                    {
                        double dipSrc = sourceTimestampsMs[dipStartIdx] / 1000.0;
                        dips.Add(dipSrc);
                        while (i < maxIdx && ssimValues[i] < this._config.VerifyDipSsimThreshold) { i++; }
                        consecutiveLow = 0;
                        dipStartIdx = -1;
                    }
                }
                else
                {
                    consecutiveLow = 0;
                    dipStartIdx = -1;
                }
            }

            return dips;
        }

        /// <summary>
        /// Calcola SSIM medio tra due sequenze usando allineamento per timestamp
        /// </summary>
        /// <param name="srcFrames">Frame source</param>
        /// <param name="srcTimestampsMs">Timestamp source</param>
        /// <param name="langFrames">Frame lingua</param>
        /// <param name="langTimestampsMs">Timestamp lingua</param>
        /// <returns>SSIM medio sui pair validi</returns>
        public double ComputeTimestampMatchedSsim(List<byte[]> srcFrames, double[] srcTimestampsMs, List<byte[]> langFrames, double[] langTimestampsMs)
        {
            double result = 0.0;
            double totalSsim = 0.0;
            int validPairs = 0;
            double srcRelMs;
            double targetLangMs;
            int nearestIdx;
            double nearestDistMs;
            double toleranceMs = 500.0;

            if (srcFrames.Count == 0 || langFrames.Count == 0 || srcTimestampsMs.Length == 0 || langTimestampsMs.Length == 0)
            {
                return result;
            }

            for (int i = 0; i < srcFrames.Count && i < srcTimestampsMs.Length; i++)
            {
                // La tolleranza evita match arbitrari quando i timestamp VFR hanno buchi troppo ampi
                srcRelMs = srcTimestampsMs[i] - srcTimestampsMs[0];
                targetLangMs = langTimestampsMs[0] + srcRelMs;
                nearestIdx = this.NearestTimestampIndex(langTimestampsMs, targetLangMs);
                if (nearestIdx < 0 || nearestIdx >= langFrames.Count) { continue; }

                nearestDistMs = Math.Abs(langTimestampsMs[nearestIdx] - targetLangMs);
                if (nearestDistMs > toleranceMs) { continue; }

                totalSsim += this._ssimMetric(srcFrames[i], langFrames[nearestIdx]);
                validPairs++;
            }

            if (validPairs > 0)
            {
                result = totalSsim / validPairs;
            }

            return result;
        }

        /// <summary>
        /// Cerca il crossover visuale in una finestra tramite scansione densa SSIM
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="langFile">File lingua</param>
        /// <param name="searchStartSrc">Inizio finestra source</param>
        /// <param name="searchEndSrc">Fine finestra source</param>
        /// <param name="oldOffsetSec">Offset precedente</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="geometryCropSourceToFourThree">Normalizzazione geometrica 4:3 source</param>
        /// <param name="geometryCropLanguageToFourThree">Normalizzazione geometrica 4:3 lingua</param>
        /// <returns>Timestamp crossover source stimato</returns>
        public double DenseScanCrossover(string sourceFile, string langFile, double searchStartSrc, double searchEndSrc, double oldOffsetSec, double inverseRatio, bool geometryCropSourceToFourThree, bool geometryCropLanguageToFourThree)
        {
            double result = (searchStartSrc + searchEndSrc) / 2.0;
            double duration = searchEndSrc - searchStartSrc;
            double langStartOld;
            int maxIdx;
            int consecutiveLow = 0;
            int dipStartIdx = -1;
            double minSsim = 0.0;
            int minIdx = 0;
            List<byte[]> srcFrames;
            double[] sourceTimestampsMs;
            List<byte[]> langOldFrames;
            double[] langTimestampsMs;
            double frameIntervalMs = 1000.0 / this._config.DenseScanFps;
            double toleranceMs = frameIntervalMs * 2.0;
            double srcRelMs;
            double targetLangMs;
            int nearestIdx;
            double nearestDistMs;
            // La scansione densa confronta source con il vecchio offset per trovare dove la similarità crolla
            this._extractor(sourceFile, (int)(searchStartSrc * 1000), duration, this._config.DenseScanFps, geometryCropSourceToFourThree, out srcFrames, out sourceTimestampsMs);
            if (srcFrames.Count < 4)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    DenseScan: estrazione source fallita (" + srcFrames.Count + " frame su " + duration.ToString("F0", CultureInfo.InvariantCulture) + "s attesi)");
                return result;
            }

            langStartOld = searchStartSrc - oldOffsetSec;
            if (Math.Abs(inverseRatio - 1.0) > 0.0001) { langStartOld = langStartOld * inverseRatio; }
            if (langStartOld < 0.0) { langStartOld = 0.0; }

            this._extractor(langFile, (int)(langStartOld * 1000), duration, this._config.DenseScanFps, geometryCropLanguageToFourThree, out langOldFrames, out langTimestampsMs);

            if (langOldFrames.Count < 4 || langTimestampsMs.Length < 4)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    DenseScan: estrazione lang fallita (" + langOldFrames.Count + " frame su " + duration.ToString("F0", CultureInfo.InvariantCulture) + "s attesi)");
                return result;
            }

            maxIdx = srcFrames.Count;
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione densa: " + maxIdx + " frame a " + this._config.DenseScanFps.ToString("F0", CultureInfo.InvariantCulture) + "fps su " + duration.ToString("F0", CultureInfo.InvariantCulture) + "s");

            double[] ssimValues = new double[maxIdx];
            for (int i = 0; i < maxIdx; i++)
            {
                // Anche qui il confronto è timestamp-based per non confondere VFR con disallineamento
                srcRelMs = sourceTimestampsMs[i] - sourceTimestampsMs[0];
                targetLangMs = langTimestampsMs[0] + srcRelMs;
                nearestIdx = this.NearestTimestampIndex(langTimestampsMs, targetLangMs);
                if (nearestIdx < 0 || nearestIdx >= langOldFrames.Count)
                {
                    ssimValues[i] = 0.0;
                }
                else
                {
                    nearestDistMs = Math.Abs(langTimestampsMs[nearestIdx] - targetLangMs);
                    if (nearestDistMs > toleranceMs)
                    {
                        ssimValues[i] = 0.0;
                    }
                    else
                    {
                        ssimValues[i] = this._ssimMetric(srcFrames[i], langOldFrames[nearestIdx]);
                    }
                }
            }

            for (int i = 0; i < maxIdx; i++)
            {
                // Crossover primario: prima sequenza di frame sotto soglia abbastanza lunga
                if (ssimValues[i] < this._config.DenseScanSsimThreshold)
                {
                    if (consecutiveLow == 0) { dipStartIdx = i; }
                    consecutiveLow++;

                    if (consecutiveLow >= this._config.DenseScanMinDipFrames)
                    {
                        result = sourceTimestampsMs[dipStartIdx] / 1000.0;
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione densa: dip a src " + result.ToString("F1", CultureInfo.InvariantCulture) + "s (frame " + dipStartIdx + "/" + maxIdx + ", " + consecutiveLow + " frame SSIM<" + this._config.DenseScanSsimThreshold.ToString("F1", CultureInfo.InvariantCulture) + ")");
                        return result;
                    }
                }
                else
                {
                    consecutiveLow = 0;
                    dipStartIdx = -1;
                }
            }

            // Fallback diagnostico: se non c'è dip netto, usa il minimo SSIM ma lo segnala come warning
            for (int i = 0; i < maxIdx; i++)
            {
                if (ssimValues[i] < minSsim)
                {
                    minSsim = ssimValues[i];
                    minIdx = i;
                }
            }
            result = sourceTimestampsMs[minIdx] / 1000.0;
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    Scansione densa: nessun dip netto, uso minimo SSIM=" + minSsim.ToString("F4", CultureInfo.InvariantCulture) + " a src " + result.ToString("F1", CultureInfo.InvariantCulture) + "s");

            return result;
        }

        /// <summary>
        /// Cerca il crossover usando la durata dei run di frame ripetuti, utile soprattutto su anime/VFR
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="langFile">File lingua</param>
        /// <param name="searchStartSrc">Inizio finestra source</param>
        /// <param name="searchEndSrc">Fine finestra source</param>
        /// <param name="oldOffsetSec">Offset precedente</param>
        /// <param name="newOffsetSec">Offset successivo</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="geometryCropSourceToFourThree">Normalizzazione geometrica 4:3 source</param>
        /// <param name="geometryCropLanguageToFourThree">Normalizzazione geometrica 4:3 lingua</param>
        /// <returns>Timestamp crossover source, oppure -1</returns>
        public double RepeatedFrameCrossover(string sourceFile, string langFile, double searchStartSrc, double searchEndSrc, double oldOffsetSec, double newOffsetSec, double inverseRatio, bool geometryCropSourceToFourThree, bool geometryCropLanguageToFourThree)
        {
            double result = -1.0;
            double duration = searchEndSrc - searchStartSrc;
            double langStartOld = searchStartSrc - oldOffsetSec;
            double langStartNew = searchStartSrc - newOffsetSec;
            List<byte[]> srcFrames;
            double[] sourceTimestampsMs;
            List<byte[]> langOldFrames;
            double[] langOldTimestampsMs;
            List<byte[]> langNewFrames;
            double[] langNewTimestampsMs;
            double[] sourceRunDurations;
            double[] oldRunDurations;
            double[] newRunDurations;
            double srcRelMs;
            double oldTargetMs;
            double newTargetMs;
            int oldIdx;
            int newIdx;
            int consecutiveNewBetter = 0;
            int crossoverIdx = -1;
            double oldDiff;
            double newDiff;
            double minRunMs = 80.0;
            double minImprovementMs = 35.0;
            double toleranceMs = 100.0;

            // Su finestre troppo corte i run ripetuti non hanno abbastanza contesto
            if (duration < 2.0)
            {
                return result;
            }

            if (Math.Abs(inverseRatio - 1.0) > 0.0001)
            {
                langStartOld = langStartOld * inverseRatio;
                langStartNew = langStartNew * inverseRatio;
            }

            if (langStartOld < 0.0) { langStartOld = 0.0; }
            if (langStartNew < 0.0) { langStartNew = 0.0; }

            this._extractor(sourceFile, (int)(searchStartSrc * 1000), duration, 0.0, geometryCropSourceToFourThree, out srcFrames, out sourceTimestampsMs);
            this._extractor(langFile, (int)(langStartOld * 1000), duration, 0.0, geometryCropLanguageToFourThree, out langOldFrames, out langOldTimestampsMs);
            this._extractor(langFile, (int)(langStartNew * 1000), duration, 0.0, geometryCropLanguageToFourThree, out langNewFrames, out langNewTimestampsMs);

            if (srcFrames.Count < 4 || langOldFrames.Count < 4 || langNewFrames.Count < 4 || sourceTimestampsMs.Length < 4 || langOldTimestampsMs.Length < 4 || langNewTimestampsMs.Length < 4)
            {
                return result;
            }

            sourceRunDurations = this.BuildRepeatedFrameRunDurations(srcFrames, sourceTimestampsMs);
            oldRunDurations = this.BuildRepeatedFrameRunDurations(langOldFrames, langOldTimestampsMs);
            newRunDurations = this.BuildRepeatedFrameRunDurations(langNewFrames, langNewTimestampsMs);

            // Un crossover è accettato quando il nuovo offset spiega meglio più run consecutivi
            for (int i = 0; i < sourceTimestampsMs.Length; i++)
            {
                if (sourceRunDurations[i] < minRunMs)
                {
                    consecutiveNewBetter = 0;
                    crossoverIdx = -1;
                    continue;
                }

                srcRelMs = sourceTimestampsMs[i] - sourceTimestampsMs[0];
                oldTargetMs = langOldTimestampsMs[0] + srcRelMs;
                newTargetMs = langNewTimestampsMs[0] + srcRelMs;
                oldIdx = this.NearestTimestampIndex(langOldTimestampsMs, oldTargetMs);
                newIdx = this.NearestTimestampIndex(langNewTimestampsMs, newTargetMs);

                if (oldIdx < 0 || oldIdx >= oldRunDurations.Length || newIdx < 0 || newIdx >= newRunDurations.Length)
                {
                    consecutiveNewBetter = 0;
                    crossoverIdx = -1;
                    continue;
                }

                if (Math.Abs(langOldTimestampsMs[oldIdx] - oldTargetMs) > toleranceMs || Math.Abs(langNewTimestampsMs[newIdx] - newTargetMs) > toleranceMs)
                {
                    consecutiveNewBetter = 0;
                    crossoverIdx = -1;
                    continue;
                }

                oldDiff = Math.Abs(sourceRunDurations[i] - oldRunDurations[oldIdx]);
                newDiff = Math.Abs(sourceRunDurations[i] - newRunDurations[newIdx]);
                if (newDiff + minImprovementMs < oldDiff)
                {
                    if (consecutiveNewBetter == 0)
                    {
                        crossoverIdx = i;
                    }

                    consecutiveNewBetter++;
                    if (consecutiveNewBetter >= 3)
                    {
                        result = sourceTimestampsMs[crossoverIdx] / 1000.0;
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Frame ripetuti: crossover a src " + result.ToString("F2", CultureInfo.InvariantCulture) + "s (run source=" + sourceRunDurations[crossoverIdx].ToString("F0", CultureInfo.InvariantCulture) + "ms)");
                        return result;
                    }
                }
                else
                {
                    consecutiveNewBetter = 0;
                    crossoverIdx = -1;
                }
            }

            return result;
        }

        /// <summary>
        /// Calcola score visuali locali su un singolo punto source usando l'offset indicato
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="langFile">File lingua</param>
        /// <param name="srcSec">Timestamp source in secondi</param>
        /// <param name="offsetSec">Offset da applicare</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="coarseFps">FPS estrazione</param>
        /// <param name="geometryCropSourceToFourThree">Normalizzazione geometrica 4:3 source</param>
        /// <param name="geometryCropLanguageToFourThree">Normalizzazione geometrica 4:3 lingua</param>
        /// <param name="mse">MSE migliore trovato</param>
        /// <param name="ssim">SSIM migliore trovato</param>
        /// <returns>True se almeno un confronto valido è stato eseguito</returns>
        public bool TryComputeLocalVisualScoreAt(string sourceFile, string langFile, double srcSec, double offsetSec, double inverseRatio, double coarseFps, bool geometryCropSourceToFourThree, bool geometryCropLanguageToFourThree, out double mse, out double ssim, List<DeepAnalysisLocalVisualScoreSampleDiagnostic> samples = null, string pointName = "", string offsetName = "")
        {
            bool result = false;
            int srcMs = (int)Math.Round(srcSec * 1000.0);
            double langMs = (srcSec - offsetSec) * 1000.0;
            double frameIntervalMs = 1000.0 / Math.Max(coarseFps, 1.0);
            double toleranceMs = frameIntervalMs * 2.0;
            double languagePaddingMs = toleranceMs;
            double langExtractStartMs;
            double langExtractDurationSec;
            List<byte[]> srcFrames;
            double[] srcFramesTs;
            List<byte[]> langFrames;
            double[] langFramesTs;
            double targetLangMs;
            int nearestIdx;
            double nearestDistMs;
            double currentMse;
            double currentSsim;
            mse = double.MaxValue;
            ssim = 0.0;
            if (Math.Abs(inverseRatio - 1.0) > 0.0001)
            {
                langMs = langMs * inverseRatio;
            }

            if (langMs < 0.0)
            {
                return result;
            }

            langExtractStartMs = langMs - languagePaddingMs;
            if (langExtractStartMs < 0.0)
            {
                langExtractStartMs = 0.0;
            }

            langExtractDurationSec = 2.0 + ((langMs - langExtractStartMs) / 1000.0) + (languagePaddingMs / 1000.0);
            this._extractor(sourceFile, srcMs, 2.0, coarseFps, geometryCropSourceToFourThree, out srcFrames, out srcFramesTs);
            this._extractor(langFile, (int)Math.Round(langExtractStartMs), langExtractDurationSec, coarseFps, geometryCropLanguageToFourThree, out langFrames, out langFramesTs);

            if (srcFrames.Count == 0 || langFrames.Count == 0 || srcFramesTs.Length == 0 || langFramesTs.Length == 0)
            {
                return result;
            }

            for (int i = 0; i < srcFrames.Count && i < srcFramesTs.Length; i++)
            {
                targetLangMs = srcFramesTs[i] - (offsetSec * 1000.0);
                if (Math.Abs(inverseRatio - 1.0) > 0.0001)
                {
                    targetLangMs = targetLangMs * inverseRatio;
                }

                nearestIdx = this.NearestTimestampIndex(langFramesTs, targetLangMs);
                if (nearestIdx < 0 || nearestIdx >= langFrames.Count)
                {
                    continue;
                }

                nearestDistMs = Math.Abs(langFramesTs[nearestIdx] - targetLangMs);
                if (nearestDistMs > toleranceMs)
                {
                    continue;
                }

                currentMse = this._mseMetric(srcFrames[i], langFrames[nearestIdx]);
                if (currentMse < mse)
                {
                    mse = currentMse;
                }

                currentSsim = this._ssimMetric(srcFrames[i], langFrames[nearestIdx]);
                if (currentSsim > ssim)
                {
                    ssim = currentSsim;
                }

                if (samples != null)
                {
                    samples.Add(new DeepAnalysisLocalVisualScoreSampleDiagnostic
                    {
                        PointName = pointName,
                        OffsetName = offsetName,
                        RequestedSourceStartSec = srcMs / 1000.0,
                        RequestedLanguageStartSec = langExtractStartMs / 1000.0,
                        SourceTimestampSec = srcFramesTs[i] / 1000.0,
                        OffsetLanguageTimestampSec = targetLangMs / 1000.0,
                        TargetLanguageTimestampSec = targetLangMs / 1000.0,
                        MatchedLanguageTimestampSec = langFramesTs[nearestIdx] / 1000.0,
                        OffsetMatchDeltaMs = langFramesTs[nearestIdx] - targetLangMs,
                        MatchDistanceMs = nearestDistMs,
                        Mse = currentMse,
                        Ssim = currentSsim
                    });
                }

                result = true;
            }

            return result;
        }

        /// <summary>
        /// Calcola MSE su un punto globale scegliendo l'offset della regione che contiene il timestamp
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="langFile">File lingua</param>
        /// <param name="regions">Regioni offset</param>
        /// <param name="srcPointMs">Timestamp source in millisecondi</param>
        /// <param name="inverseRatio">Rapporto inverso speed correction</param>
        /// <param name="coarseFps">FPS estrazione</param>
        /// <param name="geometryCropSourceToFourThree">Normalizzazione geometrica 4:3 source</param>
        /// <param name="geometryCropLanguageToFourThree">Normalizzazione geometrica 4:3 lingua</param>
        /// <param name="mse">MSE migliore trovato</param>
        /// <returns>True se almeno un confronto valido è stato eseguito</returns>
        public bool TryComputeGlobalPointMse(string sourceFile, string langFile, List<OffsetRegion> regions, double srcPointMs, double inverseRatio, double coarseFps, bool geometryCropSourceToFourThree, bool geometryCropLanguageToFourThree, out double mse)
        {
            bool result = false;
            double offsetSec;
            double srcPointSec;
            double langPointMs;
            double frameIntervalMs = 1000.0 / Math.Max(coarseFps, 1.0);
            double toleranceMs = frameIntervalMs * 2.0;
            double languagePaddingMs = toleranceMs;
            double langExtractStartMs;
            double langExtractDurationSec;
            List<byte[]> srcFrames;
            double[] srcFramesTs;
            List<byte[]> langFrames;
            double[] langFramesTs;
            double targetLangMs;
            int nearestIdx;
            double nearestDistMs;

            mse = double.MaxValue;
            srcPointSec = srcPointMs / 1000.0;
            offsetSec = 0.0;
            for (int r = regions.Count - 1; r >= 0; r--)
            {
                if (regions[r].StartSrcSec <= srcPointSec)
                {
                    offsetSec = regions[r].OffsetMs / 1000.0;
                    break;
                }
            }

            langPointMs = srcPointMs - (offsetSec * 1000.0);
            if (Math.Abs(inverseRatio - 1.0) > 0.0001)
            {
                langPointMs = langPointMs * inverseRatio;
            }

            if (langPointMs < 0.0)
            {
                return result;
            }

            langExtractStartMs = langPointMs - languagePaddingMs;
            if (langExtractStartMs < 0.0)
            {
                langExtractStartMs = 0.0;
            }

            langExtractDurationSec = 2.0 + ((langPointMs - langExtractStartMs) / 1000.0) + (languagePaddingMs / 1000.0);
            this._extractor(sourceFile, (int)srcPointMs, 2, coarseFps, geometryCropSourceToFourThree, out srcFrames, out srcFramesTs);
            this._extractor(langFile, (int)langExtractStartMs, langExtractDurationSec, coarseFps, geometryCropLanguageToFourThree, out langFrames, out langFramesTs);

            if (srcFrames.Count == 0 || langFrames.Count == 0 || srcFramesTs.Length == 0 || langFramesTs.Length == 0)
            {
                return result;
            }

            for (int vf = 0; vf < srcFrames.Count && vf < srcFramesTs.Length; vf++)
            {
                // Usa il frame language più vicino al timestamp assoluto previsto dall'offset.
                targetLangMs = srcFramesTs[vf] - (offsetSec * 1000.0);
                if (Math.Abs(inverseRatio - 1.0) > 0.0001)
                {
                    targetLangMs = targetLangMs * inverseRatio;
                }

                nearestIdx = this.NearestTimestampIndex(langFramesTs, targetLangMs);
                if (nearestIdx < 0 || nearestIdx >= langFrames.Count) { continue; }
                nearestDistMs = Math.Abs(langFramesTs[nearestIdx] - targetLangMs);
                if (nearestDistMs > toleranceMs) { continue; }

                double vMse = this._mseMetric(srcFrames[vf], langFrames[nearestIdx]);
                if (vMse < mse) { mse = vMse; }
            }

            if (mse < double.MaxValue)
            {
                result = true;
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Calcola per ogni frame la durata del run di frame quasi identici a cui appartiene
        /// </summary>
        /// <param name="frames">Frame estratti</param>
        /// <param name="timestampsMs">Timestamp frame</param>
        /// <returns>Durata run associata a ogni indice</returns>
        private double[] BuildRepeatedFrameRunDurations(List<byte[]> frames, double[] timestampsMs)
        {
            double[] result = new double[timestampsMs.Length];
            int frameCount = Math.Min(frames.Count, timestampsMs.Length);
            int runStart = 0;
            double thresholdMse = 8.0;

            if (frameCount == 0)
            {
                return result;
            }

            for (int i = 1; i < frameCount; i++)
            {
                // Una differenza MSE sopra soglia chiude il run di frame ripetuti
                if (this._mseMetric(frames[i - 1], frames[i]) > thresholdMse)
                {
                    this.FillRunDuration(result, timestampsMs, runStart, i - 1);
                    runStart = i;
                }
            }

            this.FillRunDuration(result, timestampsMs, runStart, frameCount - 1);
            return result;
        }

        /// <summary>
        /// Assegna la stessa durata a tutti i frame appartenenti a un run ripetuto
        /// </summary>
        /// <param name="runDurations">Array destinazione</param>
        /// <param name="timestampsMs">Timestamp frame</param>
        /// <param name="startIndex">Indice iniziale run</param>
        /// <param name="endIndex">Indice finale run</param>
        private void FillRunDuration(double[] runDurations, double[] timestampsMs, int startIndex, int endIndex)
        {
            double durationMs = 0.0;
            if (startIndex < 0 || endIndex < startIndex || endIndex >= timestampsMs.Length)
            {
                return;
            }

            // Per un run di un solo frame la durata resta zero: non porta informazione utile
            if (endIndex > startIndex)
            {
                durationMs = timestampsMs[endIndex] - timestampsMs[startIndex];
            }

            for (int i = startIndex; i <= endIndex; i++)
            {
                runDurations[i] = durationMs;
            }
        }

        /// <summary>
        /// Trova con ricerca binaria l'indice del timestamp più vicino
        /// </summary>
        /// <param name="timestampsMs">Timestamp ordinati</param>
        /// <param name="targetMs">Timestamp target</param>
        /// <returns>Indice più vicino, oppure -1</returns>
        private int NearestTimestampIndex(double[] timestampsMs, double targetMs)
        {
            int result = -1;
            int low = 0;
            int high;
            int mid;
            int insertIndex;
            double previousDistance;
            double nextDistance;
            if (timestampsMs == null || timestampsMs.Length == 0)
            {
                return result;
            }

            high = timestampsMs.Length - 1;
            while (low <= high)
            {
                // Ricerca binaria standard: l'array timestamp è monotono per costruzione ffmpeg
                mid = low + ((high - low) / 2);
                if (timestampsMs[mid] < targetMs)
                {
                    low = mid + 1;
                }
                else if (timestampsMs[mid] > targetMs)
                {
                    high = mid - 1;
                }
                else
                {
                    return mid;
                }
            }

            insertIndex = low;
            if (insertIndex <= 0)
            {
                result = 0;
            }
            else if (insertIndex >= timestampsMs.Length)
            {
                result = timestampsMs.Length - 1;
            }
            else
            {
                previousDistance = Math.Abs(timestampsMs[insertIndex - 1] - targetMs);
                nextDistance = Math.Abs(timestampsMs[insertIndex] - targetMs);
                result = previousDistance <= nextDistance ? insertIndex - 1 : insertIndex;
            }

            return result;
        }

        #endregion
    }
}
