using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace RemuxForge.Core
{
    /// <summary>
    /// Rilevamento e correzione differenze di velocita' tra sorgente e lingua
    /// </summary>
    public class SpeedCorrectionService : VideoSyncServiceBase
    {
        #region Variabili di classe

        /// <summary>
        /// Inizio estrazione sorgente in secondi
        /// </summary>
        private int _sourceStartSec;

        /// <summary>
        /// Durata segmento sorgente per ricerca delay
        /// </summary>
        private int _sourceDurationSec;

        /// <summary>
        /// Durata segmento lingua per ricerca delay
        /// </summary>
        private int _langDurationSec;

        /// <summary>
        /// Delay iniziale rilevato in ms
        /// </summary>
        private int _initialDelayMs;

        /// <summary>
        /// Rapporto stretch come stringa per mkvmerge
        /// </summary>
        private string _stretchFactor;

        /// <summary>
        /// Delay calcolato per mkvmerge --sync
        /// </summary>
        private int _syncDelayMs;

        /// <summary>
        /// FPS traccia video sorgente
        /// </summary>
        private double _sourceFps;

        /// <summary>
        /// FPS traccia video lingua
        /// </summary>
        private double _langFps;

        /// <summary>
        /// Rapporto inverso di stretch (1/stretchRatio)
        /// </summary>
        private double _inverseRatio;

        /// <summary>
        /// Tempo di esecuzione totale in ms
        /// </summary>
        private long _executionTimeMs;

        /// <summary>
        /// Offset di verifica per ciascun punto
        /// </summary>
        private int[] _verifyOffsets;

        /// <summary>
        /// SSIM di verifica per ciascun punto
        /// </summary>
        private double[] _verifySsimValues;

        /// <summary>
        /// Validita' di ciascun punto di verifica
        /// </summary>
        private bool[] _verifyPointValid;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public SpeedCorrectionService(string ffmpegPath) : base(ffmpegPath, LogSection.Speed)
        {
            // Carica configurazione speed correction
            SpeedCorrectionConfig cfg = AppSettingsService.Instance.Settings.Advanced.SpeedCorrection;
            this._sourceStartSec = cfg.SourceStartSec;
            this._sourceDurationSec = cfg.SourceDurationSec;
            this._langDurationSec = cfg.LangDurationSec;

            this._initialDelayMs = 0;
            this._stretchFactor = "";
            this._syncDelayMs = 0;
            this._sourceFps = 0.0;
            this._langFps = 0.0;
            this._inverseRatio = 0.0;
            this._executionTimeMs = 0;
            this._verifyOffsets = new int[this._numCheckPoints];
            this._verifySsimValues = new double[this._numCheckPoints];
            this._verifyPointValid = new bool[this._numCheckPoints];
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Rileva mismatch di velocita' tra sorgente e lingua via default_duration
        /// </summary>
        public static bool DetectSpeedMismatch(MkvFileInfo sourceInfo, MkvFileInfo langInfo, out double sourceFps, out double langFps)
        {
            bool detected = false;
            sourceFps = 0.0;
            langFps = 0.0;
            long sourceDefaultDuration = 0;
            long langDefaultDuration = 0;
            double speedRatio = 0.0;
            double ratioDiff = 0.0;
            double durationRatio = 0.0;
            double durationDiff = 0.0;
            SpeedCorrectionConfig cfg = AppSettingsService.Instance.Settings.Advanced.SpeedCorrection;
            double minSpeedRatioDiff = cfg.MinSpeedRatioDiff;
            double maxDurationDiffTelecine = cfg.MaxDurationDiffTelecine;

            // Cerca default_duration per tracce video
            sourceDefaultDuration = Utils.GetVideoDefaultDuration(sourceInfo.Tracks);
            langDefaultDuration = Utils.GetVideoDefaultDuration(langInfo.Tracks);

            // Confronta solo se entrambi hanno default_duration
            if (sourceDefaultDuration > 0 && langDefaultDuration > 0)
            {
                speedRatio = (double)sourceDefaultDuration / langDefaultDuration;
                ratioDiff = Math.Abs(speedRatio - 1.0);

                if (ratioDiff >= minSpeedRatioDiff)
                {
                    // Imposta fps solo se differenza significativa
                    sourceFps = 1000000000.0 / sourceDefaultDuration;
                    langFps = 1000000000.0 / langDefaultDuration;

                    // Verifica durata container per escludere soft telecine
                    // Se le durate sono quasi uguali nonostante FPS diversi, non e' un vero speed mismatch
                    if (sourceInfo.ContainerDurationNs > 0 && langInfo.ContainerDurationNs > 0)
                    {
                        durationRatio = (double)sourceInfo.ContainerDurationNs / langInfo.ContainerDurationNs;
                        durationDiff = Math.Abs(durationRatio - 1.0);

                        if (durationDiff < maxDurationDiffTelecine)
                        {
                            // Durate quasi identiche: probabile soft telecine o metadata errata
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  FPS diversi (" + sourceFps.ToString("F3", CultureInfo.InvariantCulture) + " vs " + langFps.ToString("F3", CultureInfo.InvariantCulture) + ") ma durata container identica (diff " + (durationDiff * 100).ToString("F2", CultureInfo.InvariantCulture) + "%) - probabile soft telecine, speed correction saltata");
                        }
                        else
                        {
                            // Durate effettivamente diverse: vero speed mismatch
                            detected = true;
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Speed mismatch rilevato: " + sourceFps.ToString("F3", CultureInfo.InvariantCulture) + "fps vs " + langFps.ToString("F3", CultureInfo.InvariantCulture) + "fps (durata diff " + (durationDiff * 100).ToString("F2", CultureInfo.InvariantCulture) + "%)");
                        }
                    }
                    else
                    {
                        // Durata container non disponibile, usa solo il confronto FPS
                        detected = true;
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Speed mismatch rilevato: " + sourceFps.ToString("F3", CultureInfo.InvariantCulture) + "fps vs " + langFps.ToString("F3", CultureInfo.InvariantCulture) + "fps");
                    }
                }
            }

            return detected;
        }

        /// <summary>
        /// Cerca delay iniziale e verifica correzione a 9 punti
        /// </summary>
        public bool FindDelayAndVerify(string sourceFile, string languageFile, long sourceDefaultDurationNs, long langDefaultDurationNs, int sourceDurationMs)
        {
            bool success = false;
            Stopwatch stopwatch = new Stopwatch();
            double stretchRatio = 0.0;
            int initialDelay = int.MinValue;
            bool verified = false;

            stopwatch.Start();

            // Resetta risultati verifica
            for (int i = 0; i < this._numCheckPoints; i++)
            {
                this._verifyOffsets[i] = int.MinValue;
                this._verifySsimValues[i] = 0.0;
                this._verifyPointValid[i] = false;
            }

            // Calcola rapporto stretch e fps da default_duration
            stretchRatio = (double)sourceDefaultDurationNs / langDefaultDurationNs;
            this._inverseRatio = 1.0 / stretchRatio;
            this._sourceFps = 1000000000.0 / sourceDefaultDurationNs;
            this._langFps = 1000000000.0 / langDefaultDurationNs;
            this._stretchFactor = sourceDefaultDurationNs.ToString() + "/" + langDefaultDurationNs.ToString();

            ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Rapporto stretch: " + stretchRatio.ToString("F6", CultureInfo.InvariantCulture) + " (" + this._sourceFps.ToString("F3", CultureInfo.InvariantCulture) + "fps -> " + this._langFps.ToString("F3", CultureInfo.InvariantCulture) + "fps)");

            // Cerca delay iniziale tramite scene-cut voting
            initialDelay = this.FindInitialDelay(sourceFile, languageFile);

            // Libera memoria frame FindInitialDelay prima della verifica a 9 punti
            GC.Collect();

            if (initialDelay != int.MinValue)
            {
                this._initialDelayMs = initialDelay;

                // Calcola delay per mkvmerge
                this._syncDelayMs = (int)Math.Round(-this._initialDelayMs * stretchRatio);

                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Delay iniziale: " + this._initialDelayMs + "ms, sync delay: " + this._syncDelayMs + "ms, stretch: " + this._stretchFactor);

                // Verifica correzione a 9 punti con scene-cut matching
                verified = this.VerifyCorrection(sourceFile, languageFile, sourceDurationMs, this._initialDelayMs);

                if (!verified)
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Verifica a 9 punti fallita");
                }
            }
            else
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Impossibile determinare delay iniziale");
            }

            stopwatch.Stop();
            this._executionTimeMs = stopwatch.ElapsedMilliseconds;

            if (initialDelay != int.MinValue && verified)
            {
                success = true;
            }

            return success;
        }

        /// <summary>
        /// Riepilogo risultati verifica per tutti i punti
        /// </summary>
        public string GetDetailSummary()
        {
            StringBuilder sb = new StringBuilder();
            int percentage = 0;

            for (int p = 0; p < this._numCheckPoints; p++)
            {
                if (sb.Length > 0) { sb.Append(", "); }
                percentage = (p + 1) * 10;

                if (this._verifyPointValid[p])
                {
                    // Valore negativo = correlazione fingerprint, positivo = SSIM
                    if (this._verifySsimValues[p] < 0)
                    {
                        sb.Append(percentage + "%=" + this._verifyOffsets[p] + "ms(corr:" + (-this._verifySsimValues[p]).ToString("F2", CultureInfo.InvariantCulture) + ")");
                    }
                    else
                    {
                        sb.Append(percentage + "%=" + this._verifyOffsets[p] + "ms(SSIM:" + this._verifySsimValues[p].ToString("F3", CultureInfo.InvariantCulture) + ")");
                    }
                }
                else
                {
                    sb.Append(percentage + "%=FAIL");
                }
            }

            return sb.ToString();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Ricerca delay iniziale tramite voting su tagli di scena
        /// </summary>
        private int FindInitialDelay(string sourceFile, string languageFile)
        {
            int result = int.MinValue;
            List<byte[]> sourceFrames = null;
            List<byte[]> langFrames = null;
            double[] sourceTimestampsMs = null;
            double[] langTimestampsMs = null;
            string sourceFileCopy = sourceFile;
            string langFileCopy = languageFile;
            double sourceFrameIntervalMs = 1000.0 / this._sourceFps;
            double nearestTolMs = 3.0 * sourceFrameIntervalMs;
            List<int> sourceCutsRaw = null;
            List<int> langCutsRaw = null;
            List<int> validSourceCuts = new List<int>();
            List<int> validLangCuts = new List<int>();
            int srcCutCount = 0;
            int lngCutCount = 0;
            int candidateCount = 0;
            double[] candidates = null;
            int candidateIdx = 0;
            int bestClusterStart = 0;
            int bestClusterCount = 0;
            int left = 0;
            int currentCount = 0;
            double winningDelay = 0.0;
            List<double> verifiedDelays = new List<double>();
            int verifiedCount = 0;
            double medianDelay = 0.0;
            int consistentCount = 0;
            double srcCutMs = 0.0;
            double driftComponent = 0.0;
            double expectedLangCutMs = 0.0;
            int nearestLangCutIdx = -1;
            double nearestDistMs = 0.0;
            double distMs = 0.0;
            int sigStartSrc = 0;
            int sigStartLng = 0;
            double ssim = 0.0;

            ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Estrazione frame per ricerca delay (source " + this._sourceDurationSec + "s, lang " + this._langDurationSec + "s)...");

            // Estrae segmenti in parallelo a fps nativo
            bool cropSrcCopy = this._cropSourceTo43;
            bool cropLngCopy = this._cropLangTo43;
            Thread sourceThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegment(sourceFileCopy, this._sourceStartSec * 1000, this._sourceDurationSec, 0, cropSrcCopy, out f, out t);
                sourceFrames = f;
                sourceTimestampsMs = t;
            });
            Thread langThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegment(langFileCopy, 0, this._langDurationSec, 0, cropLngCopy, out f, out t);
                langFrames = f;
                langTimestampsMs = t;
            });
            sourceThread.Start();
            langThread.Start();
            sourceThread.Join();
            langThread.Join();

            // Verifica frame sufficienti
            if (sourceFrames == null || sourceFrames.Count < this._cutSignatureLength)
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Frame sorgente insufficienti: " + (sourceFrames != null ? sourceFrames.Count : 0));
            }
            else if (langFrames == null || langFrames.Count < this._cutSignatureLength)
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Frame lingua insufficienti: " + (langFrames != null ? langFrames.Count : 0));
            }
            else
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Frame estratti: source=" + sourceFrames.Count + ", lang=" + langFrames.Count);

                // Rileva tagli di scena in entrambi i segmenti
                sourceCutsRaw = this.DetectSceneCuts(sourceFrames);
                langCutsRaw = this.DetectSceneCuts(langFrames);

                // Filtra tagli con margine sufficiente per la firma
                for (int i = 0; i < sourceCutsRaw.Count; i++)
                {
                    if (sourceCutsRaw[i] >= this._cutHalfWindow && sourceCutsRaw[i] + this._cutHalfWindow <= sourceFrames.Count)
                    {
                        validSourceCuts.Add(sourceCutsRaw[i]);
                    }
                }
                for (int i = 0; i < langCutsRaw.Count; i++)
                {
                    if (langCutsRaw[i] >= this._cutHalfWindow && langCutsRaw[i] + this._cutHalfWindow <= langFrames.Count)
                    {
                        validLangCuts.Add(langCutsRaw[i]);
                    }
                }

                srcCutCount = validSourceCuts.Count;
                lngCutCount = validLangCuts.Count;
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Tagli rilevati: source=" + sourceCutsRaw.Count + " (" + srcCutCount + " utilizzabili), lang=" + langCutsRaw.Count + " (" + lngCutCount + " utilizzabili)");

                if (srcCutCount >= this._minSceneCuts && lngCutCount >= this._minSceneCuts)
                {
                    // Genera langDelay candidati da tutte le coppie (sourceCut, langCut)
                    candidateCount = srcCutCount * lngCutCount;
                    candidates = new double[candidateCount];
                    candidateIdx = 0;

                    for (int s = 0; s < srcCutCount; s++)
                    {
                        srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                        driftComponent = srcCutMs * (this._inverseRatio - 1.0);

                        for (int l = 0; l < lngCutCount; l++)
                        {
                            double lngCutMs = langTimestampsMs[validLangCuts[l]];
                            // Rimuove componente drift per isolare il delay iniziale
                            candidates[candidateIdx] = (lngCutMs - srcCutMs) - driftComponent;
                            candidateIdx++;
                        }
                    }

                    // Ordina e trova cluster piu' denso (sliding window di 3 frame sorgente)
                    // La compensazione drift amplifica errori nel ratio, serve finestra piu' ampia
                    double votingWindow = sourceFrameIntervalMs * 3.0;
                    Array.Sort(candidates);
                    bestClusterCount = 0;
                    left = 0;

                    for (int r = 0; r < candidateCount; r++)
                    {
                        while (candidates[r] - candidates[left] > votingWindow)
                        {
                            left++;
                        }
                        currentCount = r - left + 1;
                        if (currentCount > bestClusterCount)
                        {
                            bestClusterCount = currentCount;
                            bestClusterStart = left;
                        }
                    }

                    // Delay vincente = mediana del cluster
                    winningDelay = candidates[bestClusterStart + bestClusterCount / 2];
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Voting: " + bestClusterCount + " voti su " + candidateCount + " candidati, delay=" + ((int)Math.Round(winningDelay)) + "ms");

                    // Verifica MSE: per ogni taglio source trova il taglio lang atteso e confronta firma
                    for (int s = 0; s < srcCutCount; s++)
                    {
                        srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                        driftComponent = srcCutMs * (this._inverseRatio - 1.0);

                        // Posizione attesa del taglio lang: srcCutMs * _inverseRatio + winningDelay
                        expectedLangCutMs = srcCutMs + winningDelay + driftComponent;

                        // Cerca il taglio lang piu' vicino alla posizione attesa (distanza in ms)
                        nearestLangCutIdx = -1;
                        nearestDistMs = double.MaxValue;
                        for (int l = 0; l < lngCutCount; l++)
                        {
                            distMs = Math.Abs(langTimestampsMs[validLangCuts[l]] - expectedLangCutMs);
                            if (distMs < nearestDistMs)
                            {
                                nearestDistMs = distMs;
                                nearestLangCutIdx = l;
                            }
                        }

                        // Verifica solo se taglio lang entro 3 frame dalla posizione attesa
                        if (nearestLangCutIdx >= 0 && nearestDistMs <= nearestTolMs)
                        {
                            sigStartSrc = validSourceCuts[s] - this._cutHalfWindow;
                            sigStartLng = validLangCuts[nearestLangCutIdx] - this._cutHalfWindow;

                            if (sigStartLng >= 0 && sigStartLng + this._cutSignatureLength <= langFrames.Count)
                            {
                                ssim = this.ComputeSequenceSsim(sourceFrames, sigStartSrc, langFrames, sigStartLng, this._cutSignatureLength);

                                if (ssim >= this._ssimThreshold && ssim <= this._ssimMaxThreshold)
                                {
                                    // Calcola langDelay verificato
                                    double actualLngMs = langTimestampsMs[validLangCuts[nearestLangCutIdx]];
                                    double actualDelay = (actualLngMs - srcCutMs) - driftComponent;
                                    verifiedDelays.Add(actualDelay);
                                }
                            }
                        }
                    }

                    verifiedCount = verifiedDelays.Count;
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Tagli verificati SSIM: " + verifiedCount + "/" + srcCutCount);

                    // Fallback: se SSIM insufficiente, ritenta con fingerprint temporale
                    // Il fingerprint confronta pattern di variazione inter-frame ed e' fps-indipendente
                    if (verifiedCount < this._minSceneCuts)
                    {
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  SSIM insufficiente, fallback a fingerprint temporale...");
                        verifiedDelays.Clear();

                        for (int s = 0; s < srcCutCount; s++)
                        {
                            srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                            driftComponent = srcCutMs * (this._inverseRatio - 1.0);
                            expectedLangCutMs = srcCutMs + winningDelay + driftComponent;

                            // Cerca il taglio lang piu' vicino alla posizione attesa (distanza in ms)
                            nearestLangCutIdx = -1;
                            nearestDistMs = double.MaxValue;
                            for (int l = 0; l < lngCutCount; l++)
                            {
                                distMs = Math.Abs(langTimestampsMs[validLangCuts[l]] - expectedLangCutMs);
                                if (distMs < nearestDistMs)
                                {
                                    nearestDistMs = distMs;
                                    nearestLangCutIdx = l;
                                }
                            }

                            // Verifica solo se taglio lang entro 3 frame dalla posizione attesa
                            if (nearestLangCutIdx >= 0 && nearestDistMs <= nearestTolMs)
                            {
                                double[] srcFingerprint = this.ComputeTemporalFingerprint(sourceFrames, validSourceCuts[s]);
                                double[] lngFingerprint = this.ComputeTemporalFingerprint(langFrames, validLangCuts[nearestLangCutIdx]);

                                if (srcFingerprint != null && lngFingerprint != null)
                                {
                                    double correlation = this.ComputeFingerprintCorrelation(srcFingerprint, lngFingerprint);

                                    if (correlation >= this._fingerprintCorrelationThreshold)
                                    {
                                        double actualLngMs = langTimestampsMs[validLangCuts[nearestLangCutIdx]];
                                        double actualDelay = (actualLngMs - srcCutMs) - driftComponent;
                                        verifiedDelays.Add(actualDelay);
                                    }
                                }
                            }
                        }

                        verifiedCount = verifiedDelays.Count;
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Tagli verificati fingerprint: " + verifiedCount + "/" + srcCutCount);
                    }

                    if (verifiedDelays.Count >= this._minSceneCuts)
                    {
                        // Mediana dei delay verificati
                        verifiedDelays.Sort();
                        medianDelay = verifiedDelays[verifiedDelays.Count / 2];

                        // Verifica consistenza entro 1 frame dalla mediana
                        for (int i = 0; i < verifiedDelays.Count; i++)
                        {
                            if (Math.Abs(verifiedDelays[i] - medianDelay) <= sourceFrameIntervalMs)
                            {
                                consistentCount++;
                            }
                        }

                        if (consistentCount >= this._minSceneCuts)
                        {
                            result = (int)Math.Round(medianDelay);
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Delay iniziale: " + result + "ms (mediana di " + verifiedDelays.Count + " tagli, " + consistentCount + " consistenti)");
                        }
                        else
                        {
                            ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Delay non consistente: solo " + consistentCount + "/" + verifiedDelays.Count + " entro 1 frame dalla mediana");
                        }
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Solo " + verifiedDelays.Count + " tagli verificati (minimo: " + this._minSceneCuts + ")");
                    }
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Tagli di scena insufficienti: source=" + srcCutCount + ", lang=" + lngCutCount + " (minimo: " + this._minSceneCuts + ")");
                }
            }

            return result;
        }

        /// <summary>
        /// Verifica correzione velocita' a 9 punti con scene-cut matching parallelo
        /// </summary>
        private bool VerifyCorrection(string sourceFile, string languageFile, int sourceDurationMs, int initialDelayMs)
        {
            bool success = false;
            int validCount = 0;
            double sourceFrameIntervalMs = 1000.0 / this._sourceFps;
            int threadCount = 4;
            string srcFile = sourceFile;
            string lngFile = languageFile;
            int percentage = 0;
            int expectedOffset = 0;
            int offsetError = 0;
            int retryCount = 0;

            // Limita ai punti disponibili
            if (threadCount > this._numCheckPoints)
            {
                threadCount = this._numCheckPoints;
            }

            // Primo passaggio con finestra base
            Thread[] workers = new Thread[threadCount];

            for (int w = 0; w < threadCount; w++)
            {
                int workerIndex = w;

                workers[w] = new Thread(() =>
                {
                    for (int p = workerIndex; p < this._numCheckPoints; p += threadCount)
                    {
                        int pct = (p + 1) * 10;
                        int sourceTimestampMs = (int)((long)sourceDurationMs * pct / 100);
                        int expOffset = initialDelayMs + (int)Math.Round(sourceTimestampMs * (this._inverseRatio - 1.0));

                        double ssim = 0.0;
                        int actualOffset = this.VerifyAtPoint(srcFile, lngFile, sourceTimestampMs, expOffset, this._verifySourceDurationSec, this._verifyLangDurationSec, out ssim);

                        this._verifyOffsets[p] = actualOffset;
                        this._verifySsimValues[p] = ssim;

                        if (actualOffset != int.MinValue)
                        {
                            int err = Math.Abs(actualOffset - expOffset);
                            if (err <= sourceFrameIntervalMs)
                            {
                                this._verifyPointValid[p] = true;
                            }
                        }
                    }
                });

                workers[w].Start();
            }

            // Attende completamento primo passaggio
            for (int w = 0; w < threadCount; w++)
            {
                workers[w].Join();
            }

            // Retry con finestra allargata per i punti falliti
            for (int p = 0; p < this._numCheckPoints; p++)
            {
                if (!this._verifyPointValid[p])
                {
                    retryCount++;
                }
            }

            if (retryCount > 0 && retryCount < this._numCheckPoints)
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Retry " + retryCount + " punti con finestra allargata (" + this._verifySourceRetrySec + "s/" + this._verifyLangRetrySec + "s)...");

                // Raccoglie indici dei punti falliti
                List<int> failedPoints = new List<int>();
                for (int p = 0; p < this._numCheckPoints; p++)
                {
                    if (!this._verifyPointValid[p])
                    {
                        failedPoints.Add(p);
                    }
                }

                // Limita thread retry a 4 (ogni ffmpeg usa gia' auto-threading)
                int retryThreadCount = 4;
                if (retryThreadCount > failedPoints.Count)
                {
                    retryThreadCount = failedPoints.Count;
                }

                Thread[] retryWorkers = new Thread[retryThreadCount];
                List<int> failedPointsCopy = failedPoints;

                for (int w = 0; w < retryThreadCount; w++)
                {
                    int workerIndex = w;

                    retryWorkers[w] = new Thread(() =>
                    {
                        for (int f = workerIndex; f < failedPointsCopy.Count; f += retryThreadCount)
                        {
                            int pointIndex = failedPointsCopy[f];
                            int pct = (pointIndex + 1) * 10;
                            int sourceTimestampMs = (int)((long)sourceDurationMs * pct / 100);
                            int expOffset = initialDelayMs + (int)Math.Round(sourceTimestampMs * (this._inverseRatio - 1.0));

                            double ssim = 0.0;
                            int actualOffset = this.VerifyAtPoint(srcFile, lngFile, sourceTimestampMs, expOffset, this._verifySourceRetrySec, this._verifyLangRetrySec, out ssim);

                            this._verifyOffsets[pointIndex] = actualOffset;
                            this._verifySsimValues[pointIndex] = ssim;

                            if (actualOffset != int.MinValue)
                            {
                                int err = Math.Abs(actualOffset - expOffset);
                                if (err <= sourceFrameIntervalMs)
                                {
                                    this._verifyPointValid[pointIndex] = true;
                                }
                            }
                        }
                    });

                    retryWorkers[w].Start();
                }

                // Attende completamento retry
                for (int r = 0; r < retryThreadCount; r++)
                {
                    retryWorkers[r].Join();
                }
            }

            // Log risultati
            for (int p = 0; p < this._numCheckPoints; p++)
            {
                percentage = (p + 1) * 10;
                expectedOffset = initialDelayMs + (int)Math.Round(((long)sourceDurationMs * percentage / 100) * (this._inverseRatio - 1.0));

                if (this._verifyPointValid[p])
                {
                    validCount++;
                    offsetError = Math.Abs(this._verifyOffsets[p] - expectedOffset);
                    // Valore negativo = correlazione fingerprint, positivo = SSIM
                    if (this._verifySsimValues[p] < 0)
                    {
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  " + percentage + "%: offset=" + this._verifyOffsets[p] + "ms (atteso=" + expectedOffset + "ms, err=" + offsetError + "ms, corr=" + (-this._verifySsimValues[p]).ToString("F2", CultureInfo.InvariantCulture) + ")");
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  " + percentage + "%: offset=" + this._verifyOffsets[p] + "ms (atteso=" + expectedOffset + "ms, err=" + offsetError + "ms, SSIM=" + this._verifySsimValues[p].ToString("F3", CultureInfo.InvariantCulture) + ")");
                    }
                }
                else if (this._verifyOffsets[p] != int.MinValue)
                {
                    offsetError = Math.Abs(this._verifyOffsets[p] - expectedOffset);
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  " + percentage + "%: offset=" + this._verifyOffsets[p] + "ms troppo diverso da atteso=" + expectedOffset + "ms (err=" + offsetError + "ms)");
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Speed, LogLevel.Notice, "  " + percentage + "%: nessun match");
                }
            }

            // Verifica numero minimo punti validi
            if (validCount >= this._minValidPoints)
            {
                success = true;
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Debug, "  Verifica superata: " + validCount + "/" + this._numCheckPoints + " punti validi");
            }
            else
            {
                ConsoleHelper.Write(LogSection.Speed, LogLevel.Warning, "  Solo " + validCount + "/" + this._numCheckPoints + " punti validi (minimo: " + this._minValidPoints + ")");
            }

            return success;
        }

        /// <summary>
        /// Verifica offset a un singolo punto temporale tramite cut-to-cut matching
        /// </summary>
        private int VerifyAtPoint(string sourceFile, string languageFile, int sourceTimestampMs, int expectedOffset, int sourceDurationSec, int langDurationSec, out double bestSsim)
        {
            bestSsim = 0.0;
            int resultOffset = int.MinValue;
            double sourceFrameIntervalMs = 1000.0 / this._sourceFps;
            double nearestTolMs = 3.0 * sourceFrameIntervalMs;
            int halfSourceMs = (sourceDurationSec * 1000) / 2;
            int sourceStartMs = sourceTimestampMs - halfSourceMs;
            int langCenter = sourceTimestampMs + expectedOffset;
            int halfLangMs = (langDurationSec * 1000) / 2;
            int langStartMs = langCenter - halfLangMs;
            List<byte[]> sourceFrames = null;
            List<byte[]> langFrames = null;
            double[] sourceTimestampsMs = null;
            double[] langTimestampsMs = null;
            string sourceFileCopy = sourceFile;
            string langFileCopy = languageFile;
            int sourceStartMsCopy = 0;
            int langStartMsCopy = 0;
            List<int> sourceCutsRaw = null;
            List<int> langCutsRaw = null;
            List<int> validSourceCuts = new List<int>();
            List<int> validLangCuts = new List<int>();
            int srcCutCount = 0;
            int lngCutCount = 0;
            List<double> cutOffsets = new List<double>();
            double medianOffset = 0.0;
            double srcCutMs = 0.0;
            double expectedLangCutMs = 0.0;
            int nearestLangCutIdx = -1;
            double nearestDistMs = 0.0;
            double distMs = 0.0;
            int sigStartSrc = 0;
            int sigStartLng = 0;
            double ssim = 0.0;
            double driftDelta = 0.0;

            // Limita inizio segmenti a 0
            if (sourceStartMs < 0)
            {
                sourceStartMs = 0;
            }
            if (langStartMs < 0)
            {
                langStartMs = 0;
            }
            sourceStartMsCopy = sourceStartMs;
            langStartMsCopy = langStartMs;

            // Estrae i due segmenti in parallelo a fps nativo
            bool cropSrcCopy = this._cropSourceTo43;
            bool cropLngCopy = this._cropLangTo43;
            Thread sourceThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegment(sourceFileCopy, sourceStartMsCopy, sourceDurationSec, 0, cropSrcCopy, out f, out t);
                sourceFrames = f;
                sourceTimestampsMs = t;
            });
            Thread langThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegment(langFileCopy, langStartMsCopy, langDurationSec, 0, cropLngCopy, out f, out t);
                langFrames = f;
                langTimestampsMs = t;
            });
            sourceThread.Start();
            langThread.Start();
            sourceThread.Join();
            langThread.Join();

            // Verifica frame sufficienti
            if (sourceFrames != null && sourceFrames.Count >= this._cutSignatureLength && langFrames != null && langFrames.Count >= this._cutSignatureLength)
            {
                // Rileva tagli in entrambi i segmenti
                sourceCutsRaw = this.DetectSceneCuts(sourceFrames);
                langCutsRaw = this.DetectSceneCuts(langFrames);

                // Filtra tagli con margine sufficiente
                for (int i = 0; i < sourceCutsRaw.Count; i++)
                {
                    if (sourceCutsRaw[i] >= this._cutHalfWindow && sourceCutsRaw[i] + this._cutHalfWindow <= sourceFrames.Count)
                    {
                        validSourceCuts.Add(sourceCutsRaw[i]);
                    }
                }
                for (int i = 0; i < langCutsRaw.Count; i++)
                {
                    if (langCutsRaw[i] >= this._cutHalfWindow && langCutsRaw[i] + this._cutHalfWindow <= langFrames.Count)
                    {
                        validLangCuts.Add(langCutsRaw[i]);
                    }
                }

                srcCutCount = validSourceCuts.Count;
                lngCutCount = validLangCuts.Count;

                // Per ogni taglio source, cerca il taglio lang atteso e verifica MSE
                for (int s = 0; s < srcCutCount; s++)
                {
                    // Posizione assoluta del taglio source (timestamp reale dal showinfo)
                    srcCutMs = sourceTimestampsMs[validSourceCuts[s]];

                    // Posizione attesa del taglio lang (basata su expectedOffset)
                    expectedLangCutMs = srcCutMs + expectedOffset;

                    // Cerca il taglio lang piu' vicino (distanza in ms)
                    nearestLangCutIdx = -1;
                    nearestDistMs = double.MaxValue;
                    for (int l = 0; l < lngCutCount; l++)
                    {
                        distMs = Math.Abs(langTimestampsMs[validLangCuts[l]] - expectedLangCutMs);
                        if (distMs < nearestDistMs)
                        {
                            nearestDistMs = distMs;
                            nearestLangCutIdx = l;
                        }
                    }

                    // Verifica solo se taglio lang entro 3 frame dalla posizione attesa
                    if (nearestLangCutIdx >= 0 && nearestDistMs <= nearestTolMs)
                    {
                        sigStartSrc = validSourceCuts[s] - this._cutHalfWindow;
                        sigStartLng = validLangCuts[nearestLangCutIdx] - this._cutHalfWindow;

                        if (sigStartLng >= 0 && sigStartLng + this._cutSignatureLength <= langFrames.Count)
                        {
                            ssim = this.ComputeSequenceSsim(sourceFrames, sigStartSrc, langFrames, sigStartLng, this._cutSignatureLength);

                            if (ssim >= this._ssimThreshold && ssim <= this._ssimMaxThreshold)
                            {
                                // Offset grezzo al punto del taglio
                                double actualLngMs = langTimestampsMs[validLangCuts[nearestLangCutIdx]];
                                double rawOffset = actualLngMs - srcCutMs;

                                // Normalizza rimuovendo il drift relativo al centro della finestra
                                // Cosi' tutti gli offset sono riferiti a sourceTimestampMs
                                driftDelta = (srcCutMs - sourceTimestampMs) * (this._inverseRatio - 1.0);
                                cutOffsets.Add(rawOffset - driftDelta);

                                // Aggiorna SSIM migliore
                                if (ssim > bestSsim)
                                {
                                    bestSsim = ssim;
                                }
                            }
                        }
                    }
                }

                // Fallback: se MSE pixel fallisce, ritenta con fingerprint temporale
                if (cutOffsets.Count == 0 && srcCutCount > 0 && lngCutCount > 0)
                {
                    for (int s = 0; s < srcCutCount; s++)
                    {
                        srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                        expectedLangCutMs = srcCutMs + expectedOffset;

                        nearestLangCutIdx = -1;
                        nearestDistMs = double.MaxValue;
                        for (int l = 0; l < lngCutCount; l++)
                        {
                            distMs = Math.Abs(langTimestampsMs[validLangCuts[l]] - expectedLangCutMs);
                            if (distMs < nearestDistMs)
                            {
                                nearestDistMs = distMs;
                                nearestLangCutIdx = l;
                            }
                        }

                        if (nearestLangCutIdx >= 0 && nearestDistMs <= nearestTolMs)
                        {
                            double[] srcFp = this.ComputeTemporalFingerprint(sourceFrames, validSourceCuts[s]);
                            double[] lngFp = this.ComputeTemporalFingerprint(langFrames, validLangCuts[nearestLangCutIdx]);

                            if (srcFp != null && lngFp != null)
                            {
                                double correlation = this.ComputeFingerprintCorrelation(srcFp, lngFp);

                                if (correlation >= this._fingerprintCorrelationThreshold)
                                {
                                    double actualLngMs = langTimestampsMs[validLangCuts[nearestLangCutIdx]];
                                    double rawOffset = actualLngMs - srcCutMs;

                                    driftDelta = (srcCutMs - sourceTimestampMs) * (this._inverseRatio - 1.0);
                                    cutOffsets.Add(rawOffset - driftDelta);

                                    // Salva correlazione come valore negativo per distinguerla da SSIM
                                    if (bestSsim == 0.0 || correlation > -bestSsim)
                                    {
                                        bestSsim = -correlation;
                                    }
                                }
                            }
                        }
                    }
                }

                // Calcola offset come mediana dei tagli matchati
                if (cutOffsets.Count > 0)
                {
                    cutOffsets.Sort();
                    medianOffset = cutOffsets[cutOffsets.Count / 2];
                    resultOffset = (int)Math.Round(medianOffset);
                }
            }

            return resultOffset;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Delay iniziale rilevato in ms
        /// </summary>
        public int InitialDelayMs { get { return this._initialDelayMs; } }

        /// <summary>
        /// Rapporto stretch per mkvmerge
        /// </summary>
        public string StretchFactor { get { return this._stretchFactor; } }

        /// <summary>
        /// Delay per mkvmerge --sync
        /// </summary>
        public int SyncDelayMs { get { return this._syncDelayMs; } }

        /// <summary>
        /// FPS file sorgente
        /// </summary>
        public double SourceFps { get { return this._sourceFps; } }

        /// <summary>
        /// FPS file lingua
        /// </summary>
        public double LangFps { get { return this._langFps; } }

        /// <summary>
        /// Tempo di esecuzione totale in ms
        /// </summary>
        public long ExecutionTimeMs { get { return this._executionTimeMs; } }

        /// <summary>
        /// Offset di verifica per ciascun punto
        /// </summary>
        public int[] VerifyOffsets { get { return this._verifyOffsets; } }

        /// <summary>
        /// Valori SSIM di verifica per ciascun punto
        /// </summary>
        public double[] VerifySsimValues { get { return this._verifySsimValues; } }

        /// <summary>
        /// Validita' di ciascun punto di verifica
        /// </summary>
        public bool[] VerifyPointValid { get { return this._verifyPointValid; } }

        #endregion
    }
}
