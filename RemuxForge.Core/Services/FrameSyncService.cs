using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemuxForge.Core
{
    /// <summary>
    /// Sincronizzazione tramite confronto visivo frame-by-frame
    /// </summary>
    public class FrameSyncService : VideoSyncServiceBase
    {
        #region Variabili di classe

        /// <summary>
        /// Durata minima video in ms per procedere
        /// </summary>
        private int _minDurationMs;

        /// <summary>
        /// Inizio estrazione sorgente per ricerca delay
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
        /// Minimo punti consistenti per confermare delay iniziale
        /// </summary>
        private int _initialMinValidPoints;

        /// <summary>
        /// Offset raffinato per ciascun punto di verifica
        /// </summary>
        private int[] _offsets;

        /// <summary>
        /// SSIM per ciascun punto di verifica
        /// </summary>
        private double[] _ssimValues;

        /// <summary>
        /// Validita' per ciascun punto di verifica
        /// </summary>
        private bool[] _pointValid;

        /// <summary>
        /// Tempo di esecuzione totale in ms
        /// </summary>
        private long _frameSyncTimeMs;

        /// <summary>
        /// Parsing durata dal log ffmpeg
        /// </summary>
        private static readonly Regex s_durationRegex = new Regex(@"Duration:\s*(\d{2}):(\d{2}):(\d{2})\.(\d{2})", RegexOptions.Compiled);

        /// <summary>
        /// Parsing frame rate dal log ffmpeg
        /// </summary>
        private static readonly Regex s_fpsRegex = new Regex(@"(\d+(?:\.\d+)?)\s*fps", RegexOptions.Compiled);

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso eseguibile ffmpeg</param>
        public FrameSyncService(string ffmpegPath) : base(ffmpegPath, LogSection.FrameSync)
        {
            // Carica configurazione frame sync
            FrameSyncConfig cfg = AppSettingsService.Instance.Settings.Advanced.FrameSync;
            this._minDurationMs = cfg.MinDurationMs;
            this._sourceStartSec = cfg.SourceStartSec;
            this._sourceDurationSec = cfg.SourceDurationSec;
            this._langDurationSec = cfg.LangDurationSec;
            this._initialMinValidPoints = cfg.MinValidPoints;

            this._offsets = new int[this._numCheckPoints];
            this._ssimValues = new double[this._numCheckPoints];
            this._pointValid = new bool[this._numCheckPoints];
            this._frameSyncTimeMs = 0;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Trova offset sync tramite scene-cut voting poi verifica a 9 punti
        /// </summary>
        /// <param name="sourceFile">Percorso file video sorgente</param>
        /// <param name="languageFile">Percorso file video lingua</param>
        /// <returns>Offset in ms o int.MinValue se non trovato</returns>
        public int RefineOffset(string sourceFile, string languageFile)
        {
            int resultOffset = int.MinValue;
            Stopwatch stopwatch = new Stopwatch();
            int durationMs = 0;
            double fps = 0.0;
            bool infoOk = false;
            int frameIntervalMs = 0;
            int initialDelay = int.MinValue;
            List<int> validOffsets = new List<int>();
            int validCount = 0;
            int bestGroupCount = 0;
            int bestGroupOffset = 0;
            int anomalyCount = 0;
            int langDurationMs = 0;
            double langFps = 0.0;
            double langTargetFps = 0.0;
            double fpsRatio = 0.0;

            stopwatch.Start();

            // Resetta risultati verifica
            for (int i = 0; i < this._numCheckPoints; i++)
            {
                this._offsets[i] = int.MinValue;
                this._ssimValues[i] = 0.0;
                this._pointValid[i] = false;
            }

            // Ottiene informazioni video dal file sorgente
            infoOk = this.GetVideoInfo(sourceFile, out durationMs, out fps);

            if (infoOk && durationMs >= this._minDurationMs)
            {
                // Calcola intervallo tra frame in ms
                frameIntervalMs = (int)Math.Round(1000.0 / fps);
                if (frameIntervalMs < 1)
                {
                    frameIntervalMs = 1;
                }

                // Rileva fps del file lingua per log informativo
                if (this.GetVideoInfo(languageFile, out langDurationMs, out langFps))
                {
                    fpsRatio = langFps / fps;

                    // Log se fps lang differisce di piu' del 2% dal source
                    if (Math.Abs(fpsRatio - 1.0) > 0.02)
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  FPS diversi: source=" + fps.ToString("F3", CultureInfo.InvariantCulture) + ", lang=" + langFps.ToString("F3", CultureInfo.InvariantCulture) + " - normalizzazione a " + fps.ToString("F3", CultureInfo.InvariantCulture) + "fps");
                    }
                }

                // Normalizza sempre entrambi i file al fps source per garantire output CFR
                // Senza normalizzazione, file VFR producono indici frame non mappabili a timestamp
                langTargetFps = fps;

                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Durata: " + (durationMs / 1000) + "s, FPS: " + fps.ToString("F3", CultureInfo.InvariantCulture) + ", intervallo frame: " + frameIntervalMs + "ms, core: " + Environment.ProcessorCount);

                // Fase 1: ricerca delay iniziale tramite scene-cut voting
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, "  Ricerca delay iniziale (2 min source, 3 min lang)...");
                initialDelay = this.FindInitialDelay(sourceFile, languageFile, fps, langTargetFps);

                // Libera memoria frame FindInitialDelay prima della verifica a 9 punti
                GC.Collect();

                if (initialDelay != int.MinValue)
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Success, "  Delay iniziale: " + initialDelay + "ms");

                    // Fase 2: verifica a 9 punti distribuiti nel video
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Phase, "  Verifica a 9 punti (4 thread)...");
                    this.VerifyAtMultiplePoints(sourceFile, languageFile, durationMs, initialDelay, fps, langTargetFps);

                    // Raccoglie offset validi
                    for (int p = 0; p < this._numCheckPoints; p++)
                    {
                        if (this._pointValid[p])
                        {
                            validOffsets.Add(this._offsets[p]);
                        }
                    }

                    validCount = validOffsets.Count;

                    if (validCount >= this._initialMinValidPoints)
                    {
                        // Trova il gruppo di offset coerenti piu' grande
                        for (int i = 0; i < validOffsets.Count; i++)
                        {
                            int groupCount = 0;
                            int groupSum = 0;

                            for (int j = 0; j < validOffsets.Count; j++)
                            {
                                int diff = Math.Abs(validOffsets[i] - validOffsets[j]);

                                if (diff <= frameIntervalMs)
                                {
                                    groupCount++;
                                    groupSum += validOffsets[j];
                                }
                            }

                            if (groupCount > bestGroupCount)
                            {
                                bestGroupCount = groupCount;
                                bestGroupOffset = groupSum / groupCount;
                            }
                        }

                        if (bestGroupCount >= this._initialMinValidPoints)
                        {
                            // Log punti anomali scartati
                            anomalyCount = validCount - bestGroupCount;
                            if (anomalyCount > 0)
                            {
                                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  " + anomalyCount + " punti anomali scartati, " + bestGroupCount + " punti coerenti");
                            }

                            resultOffset = bestGroupOffset;
                        }
                        else
                        {
                            // Offset non coerenti
                            StringBuilder detail = new StringBuilder();
                            for (int p = 0; p < this._numCheckPoints; p++)
                            {
                                if (this._pointValid[p])
                                {
                                    if (detail.Length > 0) { detail.Append(", "); }
                                    detail.Append((p + 1) * 10 + "%=" + this._offsets[p] + "ms");
                                }
                            }
                            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Offset non coerenti (" + bestGroupCount + "/" + validCount + " nel gruppo principale): " + detail.ToString());
                        }
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Solo " + validCount + "/" + this._numCheckPoints + " punti validi (minimo richiesto: " + this._initialMinValidPoints + ")");
                    }
                }
                else
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Impossibile determinare delay iniziale");
                }
            }
            else
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Impossibile ottenere info video o durata troppo breve");
            }

            stopwatch.Stop();
            this._frameSyncTimeMs = stopwatch.ElapsedMilliseconds;

            // Inverte segno: internamente usa langTime - sourceTime per i calcoli,
            // ma il delay da applicare e' l'opposto (negativo se lang e' in ritardo)
            if (resultOffset != int.MinValue)
            {
                resultOffset = -resultOffset;
            }

            return resultOffset;
        }

        /// <summary>
        /// Riepilogo risultati per tutti i punti di verifica
        /// </summary>
        /// <returns>Stringa riepilogativa con offset e MSE per ogni punto</returns>
        public string GetDetailSummary()
        {
            StringBuilder sb = new StringBuilder();
            int percentage = 0;

            for (int p = 0; p < this._numCheckPoints; p++)
            {
                if (sb.Length > 0) { sb.Append(", "); }
                percentage = (p + 1) * 10;

                if (this._pointValid[p])
                {
                    // Valore negativo = correlazione fingerprint, positivo = SSIM
                    if (this._ssimValues[p] < 0)
                    {
                        sb.Append(percentage + "%=" + this._offsets[p] + "ms(corr:" + (-this._ssimValues[p]).ToString("F2", CultureInfo.InvariantCulture) + ")");
                    }
                    else
                    {
                        sb.Append(percentage + "%=" + this._offsets[p] + "ms(SSIM:" + this._ssimValues[p].ToString("F3", CultureInfo.InvariantCulture) + ")");
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
        /// Ricerca delay iniziale tramite voting su coppie di tagli di scena
        /// </summary>
        /// <param name="sourceFile">Percorso file video sorgente</param>
        /// <param name="languageFile">Percorso file video lingua</param>
        /// <param name="fps">Frame rate del video sorgente</param>
        /// <param name="langTargetFps">FPS target per normalizzazione lang (0 = fps nativo)</param>
        /// <returns>Delay in ms o int.MinValue se non determinabile</returns>
        private int FindInitialDelay(string sourceFile, string languageFile, double fps, double langTargetFps)
        {
            int result = int.MinValue;
            List<byte[]> sourceFrames = null;
            List<byte[]> langFrames = null;
            double[] sourceTimestampsMs = null;
            double[] langTimestampsMs = null;
            string sourceFileCopy = sourceFile;
            string langFileCopy = languageFile;
            double frameIntervalMs = 1000.0 / fps;
            double nearestTolMs = 3.0 * frameIntervalMs;
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
            double winningOffset = 0.0;
            List<double> verifiedDelays = new List<double>();
            int verifiedCount = 0;
            double medianDelay = 0.0;
            int consistentCount = 0;
            double srcCutMs = 0.0;
            double expectedLangCutMs = 0.0;
            int nearestLangCutIdx = -1;
            double nearestDistMs = 0.0;
            double distMs = 0.0;
            int sigStartSrc = 0;
            int sigStartLng = 0;
            double ssim = 0.0;

            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Estrazione frame (source " + this._sourceDurationSec + "s, lang " + this._langDurationSec + "s)...");

            // Estrae segmenti in parallelo (fps forzato per garantire output CFR, passthrough se VFR)
            double fpsCopy = fps;
            bool cropSrcCopy = this._cropSourceTo43;
            bool cropLngCopy = this._cropLangTo43;
            double langTargetFpsCopy = langTargetFps;
            Thread sourceThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegment(sourceFileCopy, this._sourceStartSec * 1000, this._sourceDurationSec, fpsCopy, cropSrcCopy, out f, out t);
                sourceFrames = f;
                sourceTimestampsMs = t;
            });
            Thread langThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegment(langFileCopy, 0, this._langDurationSec, langTargetFpsCopy, cropLngCopy, out f, out t);
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
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Frame sorgente insufficienti: " + (sourceFrames != null ? sourceFrames.Count : 0));
            }
            else if (langFrames == null || langFrames.Count < this._cutSignatureLength)
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Frame lingua insufficienti: " + (langFrames != null ? langFrames.Count : 0));
            }
            else
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Frame estratti: source=" + sourceFrames.Count + ", lang=" + langFrames.Count);

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
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Tagli rilevati: source=" + sourceCutsRaw.Count + " (" + srcCutCount + " utilizzabili), lang=" + langCutsRaw.Count + " (" + lngCutCount + " utilizzabili)");

                if (srcCutCount >= this._minSceneCuts && lngCutCount >= this._minSceneCuts)
                {
                    // Genera offset candidati da tutte le coppie (sourceCut, langCut)
                    candidateCount = srcCutCount * lngCutCount;
                    candidates = new double[candidateCount];
                    candidateIdx = 0;

                    for (int s = 0; s < srcCutCount; s++)
                    {
                        double srcMs = sourceTimestampsMs[validSourceCuts[s]];
                        for (int l = 0; l < lngCutCount; l++)
                        {
                            double lngMs = langTimestampsMs[validLangCuts[l]];
                            candidates[candidateIdx] = lngMs - srcMs;
                            candidateIdx++;
                        }
                    }

                    // Ordina e trova cluster piu' denso (sliding window di 1 frame)
                    Array.Sort(candidates);
                    bestClusterCount = 0;
                    left = 0;

                    for (int r = 0; r < candidateCount; r++)
                    {
                        while (candidates[r] - candidates[left] > frameIntervalMs)
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

                    // Offset vincente = mediana del cluster
                    winningOffset = candidates[bestClusterStart + bestClusterCount / 2];
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Voting: " + bestClusterCount + " voti su " + candidateCount + " candidati, offset=" + ((int)Math.Round(winningOffset)) + "ms");

                    // Verifica MSE: per ogni taglio source trova il taglio lang atteso e confronta firma
                    for (int s = 0; s < srcCutCount; s++)
                    {
                        srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                        expectedLangCutMs = srcCutMs + winningOffset;

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
                                    double actualLngMs = langTimestampsMs[validLangCuts[nearestLangCutIdx]];
                                    verifiedDelays.Add(actualLngMs - srcCutMs);
                                }
                            }
                        }
                    }

                    verifiedCount = verifiedDelays.Count;
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Tagli verificati SSIM: " + verifiedCount + "/" + srcCutCount);

                    // Fallback: se SSIM insufficiente, ritenta con fingerprint temporale
                    if (verifiedCount < this._minSceneCuts)
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  SSIM insufficiente, fallback a fingerprint temporale...");
                        verifiedDelays.Clear();

                        for (int s = 0; s < srcCutCount; s++)
                        {
                            srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                            expectedLangCutMs = srcCutMs + winningOffset;

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
                                        verifiedDelays.Add(actualLngMs - srcCutMs);
                                    }
                                }
                            }
                        }

                        verifiedCount = verifiedDelays.Count;
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Tagli verificati fingerprint: " + verifiedCount + "/" + srcCutCount);
                    }

                    if (verifiedDelays.Count >= this._minSceneCuts)
                    {
                        // Mediana degli offset verificati
                        verifiedDelays.Sort();
                        medianDelay = verifiedDelays[verifiedDelays.Count / 2];

                        // Verifica consistenza entro 1 frame dalla mediana
                        for (int i = 0; i < verifiedDelays.Count; i++)
                        {
                            if (Math.Abs(verifiedDelays[i] - medianDelay) <= frameIntervalMs)
                            {
                                consistentCount++;
                            }
                        }

                        if (consistentCount >= this._minSceneCuts)
                        {
                            result = (int)Math.Round(medianDelay);
                            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Delay iniziale: " + result + "ms (mediana di " + verifiedDelays.Count + " tagli, " + consistentCount + " consistenti)");
                        }
                        else
                        {
                            ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Delay non consistente: solo " + consistentCount + "/" + verifiedDelays.Count + " entro 1 frame dalla mediana");
                        }
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Solo " + verifiedDelays.Count + " tagli verificati (minimo: " + this._minSceneCuts + ")");
                    }
                }
                else
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Tagli di scena insufficienti: source=" + srcCutCount + ", lang=" + lngCutCount + " (minimo: " + this._minSceneCuts + ")");
                }
            }

            return result;
        }

        /// <summary>
        /// Verifica offset a 9 punti distribuiti nel video in parallelo
        /// </summary>
        /// <param name="sourceFile">Percorso file video sorgente</param>
        /// <param name="languageFile">Percorso file video lingua</param>
        /// <param name="durationMs">Durata totale video in millisecondi</param>
        /// <param name="initialDelay">Delay iniziale stimato in ms</param>
        /// <param name="fps">Frame rate del video sorgente</param>
        /// <param name="langTargetFps">FPS target per normalizzazione lang (0 = fps nativo)</param>
        private void VerifyAtMultiplePoints(string sourceFile, string languageFile, int durationMs, int initialDelay, double fps, double langTargetFps)
        {
            int threadCount = 4;
            string srcFile = sourceFile;
            string lngFile = languageFile;
            int percentage = 0;
            int retryCount = 0;
            double langFpsCopy = langTargetFps;

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
                        int pointMs = (int)((long)durationMs * pct / 100);

                        double ssim = 0.0;
                        int offset = this.VerifyAtPoint(srcFile, lngFile, pointMs, initialDelay, fps, this._verifySourceDurationSec, this._verifyLangDurationSec, langFpsCopy, out ssim);

                        this._offsets[p] = offset;
                        this._ssimValues[p] = ssim;

                        if (offset != int.MinValue)
                        {
                            this._pointValid[p] = true;
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
                if (!this._pointValid[p])
                {
                    retryCount++;
                }
            }

            if (retryCount > 0 && retryCount < this._numCheckPoints)
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  Retry " + retryCount + " punti con finestra allargata (" + this._verifySourceRetrySec + "s/" + this._verifyLangRetrySec + "s)...");

                // Raccoglie indici dei punti falliti
                List<int> failedPoints = new List<int>();
                for (int p = 0; p < this._numCheckPoints; p++)
                {
                    if (!this._pointValid[p])
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
                            int pointMs = (int)((long)durationMs * pct / 100);

                            double ssim = 0.0;
                            int offset = this.VerifyAtPoint(srcFile, lngFile, pointMs, initialDelay, fps, this._verifySourceRetrySec, this._verifyLangRetrySec, langFpsCopy, out ssim);

                            this._offsets[pointIndex] = offset;
                            this._ssimValues[pointIndex] = ssim;

                            if (offset != int.MinValue)
                            {
                                this._pointValid[pointIndex] = true;
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

                if (this._pointValid[p])
                {
                    // Valore negativo = correlazione fingerprint, positivo = SSIM
                    if (this._ssimValues[p] < 0)
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  " + percentage + "%: offset=" + this._offsets[p] + "ms, corr=" + (-this._ssimValues[p]).ToString("F2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Debug, "  " + percentage + "%: offset=" + this._offsets[p] + "ms, SSIM=" + this._ssimValues[p].ToString("F3", CultureInfo.InvariantCulture));
                    }
                }
                else
                {
                    ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Notice, "  " + percentage + "%: nessun match");
                }
            }
        }

        /// <summary>
        /// Ottiene durata e frame rate del video tramite ffmpeg
        /// </summary>
        /// <param name="filePath">Percorso file video</param>
        /// <param name="durationMs">Durata in millisecondi (output)</param>
        /// <param name="fps">Frame rate rilevato (output)</param>
        /// <returns>True se le informazioni sono state ottenute</returns>
        private bool GetVideoInfo(string filePath, out int durationMs, out double fps)
        {
            durationMs = 0;
            fps = 25.0;
            bool success = false;
            Process process = null;
            string stdout = "";
            string stderr = "";
            string output = "";
            Match durationMatch = null;
            Match fpsMatch = null;
            int hours = 0;
            int minutes = 0;
            int seconds = 0;
            int centiseconds = 0;
            double parsedFps = 0.0;
            string args = "";

            try
            {
                // Esegue ffmpeg per leggere solo le info dell'header
                args = "-nostdin -hide_banner";
                if (this._useHwaccel)
                {
                    args = args + " -hwaccel auto";
                }
                args = args + " -i \"" + filePath + "\"";

                process = new Process();
                process.StartInfo.FileName = this._ffmpegPath;
                process.StartInfo.Arguments = args;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.Start();

                // Svuota stdout in thread separato
                // Catch silenzioso intenzionale: pipe puo' chiudersi se il processo termina
                Thread outThread = new Thread(() =>
                {
                    try { stdout = process.StandardOutput.ReadToEnd(); }
                    catch { }
                });
                outThread.Start();

                // Legge stderr su thread principale
                stderr = process.StandardError.ReadToEnd();
                outThread.Join();
                process.WaitForExit();

                // Combina output per parsing
                output = stdout + stderr;

                // Parsing durata
                durationMatch = s_durationRegex.Match(output);
                if (durationMatch.Success)
                {
                    hours = int.Parse(durationMatch.Groups[1].Value);
                    minutes = int.Parse(durationMatch.Groups[2].Value);
                    seconds = int.Parse(durationMatch.Groups[3].Value);
                    centiseconds = int.Parse(durationMatch.Groups[4].Value);
                    durationMs = (hours * 3600000) + (minutes * 60000) + (seconds * 1000) + (centiseconds * 10);
                    success = true;
                }

                // Parsing frame rate
                fpsMatch = s_fpsRegex.Match(output);
                if (fpsMatch.Success)
                {
                    parsedFps = double.Parse(fpsMatch.Groups[1].Value, CultureInfo.InvariantCulture);
                    if (parsedFps > 0.0)
                    {
                        fps = parsedFps;
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Errore GetVideoInfo: " + ex.Message);
            }
            finally
            {
                if (process != null) { process.Dispose(); process = null; }
            }

            return success;
        }

        /// <summary>
        /// Verifica e raffina offset a un singolo punto temporale tramite cut-to-cut matching
        /// </summary>
        private int VerifyAtPoint(string sourceFile, string languageFile, int sourceTimestampMs, int baseOffset, double fps, int sourceDurationSec, int langDurationSec, double langTargetFps, out double bestSsim)
        {
            bestSsim = 0.0;
            int resultOffset = int.MinValue;
            double frameIntervalMs = 1000.0 / fps;
            double nearestTolMs = 3.0 * frameIntervalMs;
            int halfSourceMs = (sourceDurationSec * 1000) / 2;
            int sourceStartMs = sourceTimestampMs - halfSourceMs;
            int langCenter = sourceTimestampMs + baseOffset;
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
            List<double> cutDelays = new List<double>();
            double medianDelay = 0.0;
            double srcCutMs = 0.0;
            double expectedLangCutMs = 0.0;
            int nearestLangCutIdx = -1;
            double nearestDistMs = 0.0;
            double distMs = 0.0;
            int sigStartSrc = 0;
            int sigStartLng = 0;
            double ssim = 0.0;

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

            // Estrae i due segmenti in parallelo (fps forzato per garantire output CFR)
            double fpsCopy = fps;
            double langFpsCopy = langTargetFps;
            bool cropSrcCopy = this._cropSourceTo43;
            bool cropLngCopy = this._cropLangTo43;
            Thread sourceThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegment(sourceFileCopy, sourceStartMsCopy, sourceDurationSec, fpsCopy, cropSrcCopy, out f, out t);
                sourceFrames = f;
                sourceTimestampsMs = t;
            });
            Thread langThread = new Thread(() =>
            {
                List<byte[]> f;
                double[] t;
                this.ExtractSegment(langFileCopy, langStartMsCopy, langDurationSec, langFpsCopy, cropLngCopy, out f, out t);
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

                    // Posizione attesa del taglio lang (basata su baseOffset)
                    expectedLangCutMs = srcCutMs + baseOffset;

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
                                // Offset preciso basato sulle posizioni effettive
                                double actualLngMs = langTimestampsMs[validLangCuts[nearestLangCutIdx]];
                                cutDelays.Add(actualLngMs - srcCutMs);

                                // Aggiorna SSIM migliore
                                if (ssim > bestSsim)
                                {
                                    bestSsim = ssim;
                                }
                            }
                        }
                    }
                }

                // Fallback: se SSIM insufficiente, ritenta con fingerprint temporale
                if (cutDelays.Count == 0 && srcCutCount > 0 && lngCutCount > 0)
                {
                    for (int s = 0; s < srcCutCount; s++)
                    {
                        srcCutMs = sourceTimestampsMs[validSourceCuts[s]];
                        expectedLangCutMs = srcCutMs + baseOffset;

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
                                    cutDelays.Add(actualLngMs - srcCutMs);

                                    // Salva correlazione migliore come valore negativo per distinguerla da SSIM
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
                if (cutDelays.Count > 0)
                {
                    cutDelays.Sort();
                    medianDelay = cutDelays[cutDelays.Count / 2];
                    resultOffset = (int)Math.Round(medianDelay);
                }
            }

            return resultOffset;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Offset raffinati per i punti di verifica
        /// </summary>
        public int[] Offsets { get { return this._offsets; } }

        /// <summary>
        /// Valori SSIM per i punti di verifica
        /// </summary>
        public double[] SsimValues { get { return this._ssimValues; } }

        /// <summary>
        /// Array di validita' per i punti di verifica
        /// </summary>
        public bool[] PointValid { get { return this._pointValid; } }

        /// <summary>
        /// Tempo di esecuzione frame sync in ms
        /// </summary>
        public long FrameSyncTimeMs { get { return this._frameSyncTimeMs; } }

        #endregion
    }
}
