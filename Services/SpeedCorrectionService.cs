using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace MergeLanguageTracks
{
    /// <summary>
    /// Rilevamento e correzione differenze di velocita' tra sorgente e lingua
    /// </summary>
    public class SpeedCorrectionService : VideoSyncServiceBase
    {
        #region Costanti

        /// <summary>
        /// Inizio estrazione sorgente in secondi
        /// </summary>
        private const int SOURCE_START_SEC = 1;

        /// <summary>
        /// Durata segmento sorgente per ricerca delay
        /// </summary>
        private const int SOURCE_DURATION_SEC = 16;

        /// <summary>
        /// Durata segmento lingua per ricerca delay
        /// </summary>
        private const int LANG_DURATION_SEC = 75;

        /// <summary>
        /// Soglia minima differenza rapporto fps
        /// </summary>
        private const double MIN_SPEED_RATIO_DIFF = 0.001;

        #endregion

        #region Variabili di classe

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
        /// MSE di verifica per ciascun punto
        /// </summary>
        private double[] _verifyMseValues;

        /// <summary>
        /// Validita' di ciascun punto di verifica
        /// </summary>
        private bool[] _verifyPointValid;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public SpeedCorrectionService(string ffmpegPath) : base(ffmpegPath, "SPEED")
        {
            this._initialDelayMs = 0;
            this._stretchFactor = "";
            this._syncDelayMs = 0;
            this._sourceFps = 0.0;
            this._langFps = 0.0;
            this._inverseRatio = 0.0;
            this._executionTimeMs = 0;
            this._verifyOffsets = new int[NUM_CHECK_POINTS];
            this._verifyMseValues = new double[NUM_CHECK_POINTS];
            this._verifyPointValid = new bool[NUM_CHECK_POINTS];
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

            // Cerca traccia video nel file sorgente
            for (int i = 0; i < sourceInfo.Tracks.Count; i++)
            {
                if (string.Equals(sourceInfo.Tracks[i].Type, "video", StringComparison.OrdinalIgnoreCase) && sourceInfo.Tracks[i].DefaultDurationNs > 0)
                {
                    sourceDefaultDuration = sourceInfo.Tracks[i].DefaultDurationNs;
                    break;
                }
            }

            // Cerca traccia video nel file lingua
            for (int i = 0; i < langInfo.Tracks.Count; i++)
            {
                if (string.Equals(langInfo.Tracks[i].Type, "video", StringComparison.OrdinalIgnoreCase) && langInfo.Tracks[i].DefaultDurationNs > 0)
                {
                    langDefaultDuration = langInfo.Tracks[i].DefaultDurationNs;
                    break;
                }
            }

            // Confronta solo se entrambi hanno default_duration
            if (sourceDefaultDuration > 0 && langDefaultDuration > 0)
            {
                sourceFps = 1000000000.0 / sourceDefaultDuration;
                langFps = 1000000000.0 / langDefaultDuration;
                speedRatio = (double)sourceDefaultDuration / langDefaultDuration;
                ratioDiff = Math.Abs(speedRatio - 1.0);

                if (ratioDiff >= MIN_SPEED_RATIO_DIFF)
                {
                    detected = true;
                    ConsoleHelper.WriteDarkGray("  [SPEED] Speed mismatch rilevato: " + sourceFps.ToString("F3", CultureInfo.InvariantCulture) + "fps vs " + langFps.ToString("F3", CultureInfo.InvariantCulture) + "fps");
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
            for (int i = 0; i < NUM_CHECK_POINTS; i++)
            {
                this._verifyOffsets[i] = int.MinValue;
                this._verifyMseValues[i] = double.MaxValue;
                this._verifyPointValid[i] = false;
            }

            // Calcola rapporto stretch e fps da default_duration
            stretchRatio = (double)sourceDefaultDurationNs / langDefaultDurationNs;
            this._inverseRatio = 1.0 / stretchRatio;
            this._sourceFps = 1000000000.0 / sourceDefaultDurationNs;
            this._langFps = 1000000000.0 / langDefaultDurationNs;
            this._stretchFactor = sourceDefaultDurationNs.ToString() + "/" + langDefaultDurationNs.ToString();

            ConsoleHelper.WriteDarkGray("  [SPEED] Rapporto stretch: " + stretchRatio.ToString("F6", CultureInfo.InvariantCulture) + " (" + this._sourceFps.ToString("F3", CultureInfo.InvariantCulture) + "fps -> " + this._langFps.ToString("F3", CultureInfo.InvariantCulture) + "fps)");

            // Cerca delay iniziale tramite scene-cut voting
            initialDelay = this.FindInitialDelay(sourceFile, languageFile);

            if (initialDelay != int.MinValue)
            {
                this._initialDelayMs = initialDelay;

                // Calcola delay per mkvmerge
                this._syncDelayMs = (int)Math.Round(-this._initialDelayMs * stretchRatio);

                ConsoleHelper.WriteDarkGray("  [SPEED] Delay iniziale: " + this._initialDelayMs + "ms, sync delay: " + this._syncDelayMs + "ms, stretch: " + this._stretchFactor);

                // Verifica correzione a 9 punti con scene-cut matching
                verified = this.VerifyCorrection(sourceFile, languageFile, sourceDurationMs, this._initialDelayMs);

                if (!verified)
                {
                    ConsoleHelper.WriteWarning("  [SPEED] Verifica a 9 punti fallita");
                }
            }
            else
            {
                ConsoleHelper.WriteWarning("  [SPEED] Impossibile determinare delay iniziale");
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

            for (int p = 0; p < NUM_CHECK_POINTS; p++)
            {
                if (sb.Length > 0) { sb.Append(", "); }
                percentage = (p + 1) * 10;

                if (this._verifyPointValid[p])
                {
                    sb.Append(percentage + "%=" + this._verifyOffsets[p] + "ms(MSE:" + this._verifyMseValues[p].ToString("F1", CultureInfo.InvariantCulture) + ")");
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
            string sourceFileCopy = sourceFile;
            string langFileCopy = languageFile;
            double sourceFrameIntervalMs = 1000.0 / this._sourceFps;
            double langFrameIntervalMs = 1000.0 / this._langFps;
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
            int expectedLangFrame = 0;
            int nearestLangCutIdx = -1;
            int nearestDist = 0;
            int dist = 0;
            int sigStartSrc = 0;
            int sigStartLng = 0;
            double mse = 0.0;

            ConsoleHelper.WriteDarkGray("  [SPEED] Estrazione frame per ricerca delay (source " + SOURCE_DURATION_SEC + "s, lang " + LANG_DURATION_SEC + "s)...");

            // Estrae segmenti in parallelo
            Thread sourceThread = new Thread(() => { sourceFrames = this.ExtractSegment(sourceFileCopy, SOURCE_START_SEC * 1000, SOURCE_DURATION_SEC); });
            Thread langThread = new Thread(() => { langFrames = this.ExtractSegment(langFileCopy, 0, LANG_DURATION_SEC); });
            sourceThread.Start();
            langThread.Start();
            sourceThread.Join();
            langThread.Join();

            // Verifica frame sufficienti
            if (sourceFrames == null || sourceFrames.Count < CUT_SIGNATURE_LENGTH)
            {
                ConsoleHelper.WriteWarning("  [SPEED] Frame sorgente insufficienti: " + (sourceFrames != null ? sourceFrames.Count : 0));
            }
            else if (langFrames == null || langFrames.Count < CUT_SIGNATURE_LENGTH)
            {
                ConsoleHelper.WriteWarning("  [SPEED] Frame lingua insufficienti: " + (langFrames != null ? langFrames.Count : 0));
            }
            else
            {
                ConsoleHelper.WriteDarkGray("  [SPEED] Frame estratti: source=" + sourceFrames.Count + ", lang=" + langFrames.Count);

                // Rileva tagli di scena in entrambi i segmenti
                sourceCutsRaw = this.DetectSceneCuts(sourceFrames);
                langCutsRaw = this.DetectSceneCuts(langFrames);

                // Filtra tagli con margine sufficiente per la firma
                for (int i = 0; i < sourceCutsRaw.Count; i++)
                {
                    if (sourceCutsRaw[i] >= CUT_HALF_WINDOW && sourceCutsRaw[i] + CUT_HALF_WINDOW <= sourceFrames.Count)
                    {
                        validSourceCuts.Add(sourceCutsRaw[i]);
                    }
                }
                for (int i = 0; i < langCutsRaw.Count; i++)
                {
                    if (langCutsRaw[i] >= CUT_HALF_WINDOW && langCutsRaw[i] + CUT_HALF_WINDOW <= langFrames.Count)
                    {
                        validLangCuts.Add(langCutsRaw[i]);
                    }
                }

                srcCutCount = validSourceCuts.Count;
                lngCutCount = validLangCuts.Count;
                ConsoleHelper.WriteDarkGray("  [SPEED] Tagli rilevati: source=" + sourceCutsRaw.Count + " (" + srcCutCount + " utilizzabili), lang=" + langCutsRaw.Count + " (" + lngCutCount + " utilizzabili)");

                if (srcCutCount >= MIN_SCENE_CUTS && lngCutCount >= MIN_SCENE_CUTS)
                {
                    // Genera langDelay candidati da tutte le coppie (sourceCut, langCut)
                    candidateCount = srcCutCount * lngCutCount;
                    candidates = new double[candidateCount];
                    candidateIdx = 0;

                    for (int s = 0; s < srcCutCount; s++)
                    {
                        srcCutMs = (SOURCE_START_SEC * 1000.0) + (validSourceCuts[s] * sourceFrameIntervalMs);
                        driftComponent = srcCutMs * (this._inverseRatio - 1.0);

                        for (int l = 0; l < lngCutCount; l++)
                        {
                            double lngCutMs = validLangCuts[l] * langFrameIntervalMs;
                            // Rimuove componente drift per isolare il delay iniziale
                            candidates[candidateIdx] = (lngCutMs - srcCutMs) - driftComponent;
                            candidateIdx++;
                        }
                    }

                    // Ordina e trova cluster piu' denso (sliding window di 1 frame sorgente)
                    Array.Sort(candidates);
                    bestClusterCount = 0;
                    left = 0;

                    for (int r = 0; r < candidateCount; r++)
                    {
                        while (candidates[r] - candidates[left] > sourceFrameIntervalMs)
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
                    ConsoleHelper.WriteDarkGray("  [SPEED] Voting: " + bestClusterCount + " voti su " + candidateCount + " candidati, delay=" + ((int)Math.Round(winningDelay)) + "ms");

                    // Verifica MSE: per ogni taglio source trova il taglio lang atteso e confronta firma
                    for (int s = 0; s < srcCutCount; s++)
                    {
                        srcCutMs = (SOURCE_START_SEC * 1000.0) + (validSourceCuts[s] * sourceFrameIntervalMs);
                        driftComponent = srcCutMs * (this._inverseRatio - 1.0);

                        // Posizione attesa del taglio lang: srcCutMs * _inverseRatio + winningDelay
                        expectedLangCutMs = srcCutMs + winningDelay + driftComponent;
                        expectedLangFrame = (int)Math.Round(expectedLangCutMs / langFrameIntervalMs);

                        // Cerca il taglio lang piu' vicino alla posizione attesa
                        nearestLangCutIdx = -1;
                        nearestDist = int.MaxValue;
                        for (int l = 0; l < lngCutCount; l++)
                        {
                            dist = Math.Abs(validLangCuts[l] - expectedLangFrame);
                            if (dist < nearestDist)
                            {
                                nearestDist = dist;
                                nearestLangCutIdx = l;
                            }
                        }

                        // Verifica solo se taglio lang entro 3 frame dalla posizione attesa
                        if (nearestLangCutIdx >= 0 && nearestDist <= 3)
                        {
                            sigStartSrc = validSourceCuts[s] - CUT_HALF_WINDOW;
                            sigStartLng = validLangCuts[nearestLangCutIdx] - CUT_HALF_WINDOW;

                            if (sigStartLng >= 0 && sigStartLng + CUT_SIGNATURE_LENGTH <= langFrames.Count)
                            {
                                mse = this.ComputeSequenceMse(sourceFrames, sigStartSrc, langFrames, sigStartLng, CUT_SIGNATURE_LENGTH);

                                if (mse <= MSE_THRESHOLD && mse >= MSE_MIN_THRESHOLD)
                                {
                                    // Calcola langDelay verificato
                                    double actualLngMs = validLangCuts[nearestLangCutIdx] * langFrameIntervalMs;
                                    double actualDelay = (actualLngMs - srcCutMs) - driftComponent;
                                    verifiedDelays.Add(actualDelay);
                                }
                            }
                        }
                    }

                    verifiedCount = verifiedDelays.Count;
                    ConsoleHelper.WriteDarkGray("  [SPEED] Tagli verificati MSE: " + verifiedCount + "/" + srcCutCount);

                    if (verifiedDelays.Count >= MIN_SCENE_CUTS)
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

                        if (consistentCount >= MIN_SCENE_CUTS)
                        {
                            result = (int)Math.Round(medianDelay);
                            ConsoleHelper.WriteDarkGray("  [SPEED] Delay iniziale: " + result + "ms (mediana di " + verifiedDelays.Count + " tagli, " + consistentCount + " consistenti)");
                        }
                        else
                        {
                            ConsoleHelper.WriteWarning("  [SPEED] Delay non consistente: solo " + consistentCount + "/" + verifiedDelays.Count + " entro 1 frame dalla mediana");
                        }
                    }
                    else
                    {
                        ConsoleHelper.WriteWarning("  [SPEED] Solo " + verifiedDelays.Count + " tagli verificati (minimo: " + MIN_SCENE_CUTS + ")");
                    }
                }
                else
                {
                    ConsoleHelper.WriteWarning("  [SPEED] Tagli di scena insufficienti: source=" + srcCutCount + ", lang=" + lngCutCount + " (minimo: " + MIN_SCENE_CUTS + ")");
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
            int threadCount = Environment.ProcessorCount;
            string srcFile = sourceFile;
            string lngFile = languageFile;
            int percentage = 0;
            int expectedOffset = 0;
            int offsetError = 0;
            int retryCount = 0;

            // Limita a 9 thread massimo
            if (threadCount > NUM_CHECK_POINTS)
            {
                threadCount = NUM_CHECK_POINTS;
            }

            // Primo passaggio con finestra base
            Thread[] workers = new Thread[threadCount];

            for (int w = 0; w < threadCount; w++)
            {
                int workerIndex = w;

                workers[w] = new Thread(() =>
                {
                    for (int p = workerIndex; p < NUM_CHECK_POINTS; p += threadCount)
                    {
                        int pct = (p + 1) * 10;
                        int sourceTimestampMs = (int)((long)sourceDurationMs * pct / 100);
                        int expOffset = initialDelayMs + (int)Math.Round(sourceTimestampMs * (this._inverseRatio - 1.0));

                        double mse = 0.0;
                        int actualOffset = this.VerifyAtPoint(srcFile, lngFile, sourceTimestampMs, expOffset, VERIFY_SOURCE_DURATION_SEC, VERIFY_LANG_DURATION_SEC, out mse);

                        this._verifyOffsets[p] = actualOffset;
                        this._verifyMseValues[p] = mse;

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
            for (int p = 0; p < NUM_CHECK_POINTS; p++)
            {
                if (!this._verifyPointValid[p])
                {
                    retryCount++;
                }
            }

            if (retryCount > 0 && retryCount < NUM_CHECK_POINTS)
            {
                ConsoleHelper.WriteDarkGray("  [SPEED] Retry " + retryCount + " punti con finestra allargata (" + VERIFY_SOURCE_RETRY_SEC + "s/" + VERIFY_LANG_RETRY_SEC + "s)...");

                Thread[] retryWorkers = new Thread[retryCount];
                int retryIdx = 0;

                for (int p = 0; p < NUM_CHECK_POINTS; p++)
                {
                    if (!this._verifyPointValid[p])
                    {
                        int pointIndex = p;
                        int pct = (p + 1) * 10;
                        int sourceTimestampMs = (int)((long)sourceDurationMs * pct / 100);
                        int expOffset = initialDelayMs + (int)Math.Round(sourceTimestampMs * (this._inverseRatio - 1.0));

                        retryWorkers[retryIdx] = new Thread(() =>
                        {
                            double mse = 0.0;
                            int actualOffset = this.VerifyAtPoint(srcFile, lngFile, sourceTimestampMs, expOffset, VERIFY_SOURCE_RETRY_SEC, VERIFY_LANG_RETRY_SEC, out mse);

                            this._verifyOffsets[pointIndex] = actualOffset;
                            this._verifyMseValues[pointIndex] = mse;

                            if (actualOffset != int.MinValue)
                            {
                                int err = Math.Abs(actualOffset - expOffset);
                                if (err <= sourceFrameIntervalMs)
                                {
                                    this._verifyPointValid[pointIndex] = true;
                                }
                            }
                        });

                        retryWorkers[retryIdx].Start();
                        retryIdx++;
                    }
                }

                // Attende completamento retry
                for (int r = 0; r < retryCount; r++)
                {
                    retryWorkers[r].Join();
                }
            }

            // Log risultati
            for (int p = 0; p < NUM_CHECK_POINTS; p++)
            {
                percentage = (p + 1) * 10;
                expectedOffset = initialDelayMs + (int)Math.Round(((long)sourceDurationMs * percentage / 100) * (this._inverseRatio - 1.0));

                if (this._verifyPointValid[p])
                {
                    validCount++;
                    offsetError = Math.Abs(this._verifyOffsets[p] - expectedOffset);
                    ConsoleHelper.WriteDarkGray("  [SPEED] " + percentage + "%: offset=" + this._verifyOffsets[p] + "ms (atteso=" + expectedOffset + "ms, err=" + offsetError + "ms, MSE=" + this._verifyMseValues[p].ToString("F1", CultureInfo.InvariantCulture) + ")");
                }
                else if (this._verifyOffsets[p] != int.MinValue)
                {
                    offsetError = Math.Abs(this._verifyOffsets[p] - expectedOffset);
                    ConsoleHelper.WriteDarkYellow("  [SPEED] " + percentage + "%: offset=" + this._verifyOffsets[p] + "ms troppo diverso da atteso=" + expectedOffset + "ms (err=" + offsetError + "ms)");
                }
                else
                {
                    ConsoleHelper.WriteDarkYellow("  [SPEED] " + percentage + "%: nessun match");
                }
            }

            // Verifica numero minimo punti validi
            if (validCount >= MIN_VALID_POINTS)
            {
                success = true;
                ConsoleHelper.WriteDarkGray("  [SPEED] Verifica superata: " + validCount + "/" + NUM_CHECK_POINTS + " punti validi");
            }
            else
            {
                ConsoleHelper.WriteWarning("  [SPEED] Solo " + validCount + "/" + NUM_CHECK_POINTS + " punti validi (minimo: " + MIN_VALID_POINTS + ")");
            }

            return success;
        }

        /// <summary>
        /// Verifica offset a un singolo punto temporale tramite cut-to-cut matching
        /// </summary>
        private int VerifyAtPoint(string sourceFile, string languageFile, int sourceTimestampMs, int expectedOffset, int sourceDurationSec, int langDurationSec, out double bestMse)
        {
            bestMse = double.MaxValue;
            int resultOffset = int.MinValue;
            double sourceFrameIntervalMs = 1000.0 / this._sourceFps;
            double langFrameIntervalMs = 1000.0 / this._langFps;
            int halfSourceMs = (sourceDurationSec * 1000) / 2;
            int sourceStartMs = sourceTimestampMs - halfSourceMs;
            int langCenter = sourceTimestampMs + expectedOffset;
            int halfLangMs = (langDurationSec * 1000) / 2;
            int langStartMs = langCenter - halfLangMs;
            List<byte[]> sourceFrames = null;
            List<byte[]> langFrames = null;
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
            int expectedLangFrame = 0;
            int nearestLangCutIdx = -1;
            int nearestDist = 0;
            int dist = 0;
            int sigStartSrc = 0;
            int sigStartLng = 0;
            double mse = 0.0;
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

            // Estrae i due segmenti in parallelo
            Thread sourceThread = new Thread(() => { sourceFrames = this.ExtractSegment(sourceFileCopy, sourceStartMsCopy, sourceDurationSec); });
            Thread langThread = new Thread(() => { langFrames = this.ExtractSegment(langFileCopy, langStartMsCopy, langDurationSec); });
            sourceThread.Start();
            langThread.Start();
            sourceThread.Join();
            langThread.Join();

            // Verifica frame sufficienti
            if (sourceFrames != null && sourceFrames.Count >= CUT_SIGNATURE_LENGTH && langFrames != null && langFrames.Count >= CUT_SIGNATURE_LENGTH)
            {
                // Rileva tagli in entrambi i segmenti
                sourceCutsRaw = this.DetectSceneCuts(sourceFrames);
                langCutsRaw = this.DetectSceneCuts(langFrames);

                // Filtra tagli con margine sufficiente
                for (int i = 0; i < sourceCutsRaw.Count; i++)
                {
                    if (sourceCutsRaw[i] >= CUT_HALF_WINDOW && sourceCutsRaw[i] + CUT_HALF_WINDOW <= sourceFrames.Count)
                    {
                        validSourceCuts.Add(sourceCutsRaw[i]);
                    }
                }
                for (int i = 0; i < langCutsRaw.Count; i++)
                {
                    if (langCutsRaw[i] >= CUT_HALF_WINDOW && langCutsRaw[i] + CUT_HALF_WINDOW <= langFrames.Count)
                    {
                        validLangCuts.Add(langCutsRaw[i]);
                    }
                }

                srcCutCount = validSourceCuts.Count;
                lngCutCount = validLangCuts.Count;

                // Per ogni taglio source, cerca il taglio lang atteso e verifica MSE
                for (int s = 0; s < srcCutCount; s++)
                {
                    // Posizione assoluta del taglio source
                    srcCutMs = sourceStartMs + (validSourceCuts[s] * sourceFrameIntervalMs);

                    // Posizione attesa del taglio lang (basata su expectedOffset)
                    expectedLangCutMs = srcCutMs + expectedOffset;
                    expectedLangFrame = (int)Math.Round((expectedLangCutMs - langStartMs) / langFrameIntervalMs);

                    // Cerca il taglio lang piu' vicino
                    nearestLangCutIdx = -1;
                    nearestDist = int.MaxValue;
                    for (int l = 0; l < lngCutCount; l++)
                    {
                        dist = Math.Abs(validLangCuts[l] - expectedLangFrame);
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearestLangCutIdx = l;
                        }
                    }

                    // Verifica solo se taglio lang entro 3 frame dalla posizione attesa
                    if (nearestLangCutIdx >= 0 && nearestDist <= 3)
                    {
                        sigStartSrc = validSourceCuts[s] - CUT_HALF_WINDOW;
                        sigStartLng = validLangCuts[nearestLangCutIdx] - CUT_HALF_WINDOW;

                        if (sigStartLng >= 0 && sigStartLng + CUT_SIGNATURE_LENGTH <= langFrames.Count)
                        {
                            mse = this.ComputeSequenceMse(sourceFrames, sigStartSrc, langFrames, sigStartLng, CUT_SIGNATURE_LENGTH);

                            if (mse <= MSE_THRESHOLD && mse >= MSE_MIN_THRESHOLD)
                            {
                                // Offset grezzo al punto del taglio
                                double actualLngMs = langStartMs + (validLangCuts[nearestLangCutIdx] * langFrameIntervalMs);
                                double rawOffset = actualLngMs - srcCutMs;

                                // Normalizza rimuovendo il drift relativo al centro della finestra
                                // Cosi' tutti gli offset sono riferiti a sourceTimestampMs
                                driftDelta = (srcCutMs - sourceTimestampMs) * (this._inverseRatio - 1.0);
                                cutOffsets.Add(rawOffset - driftDelta);

                                // Aggiorna MSE migliore
                                if (mse < bestMse)
                                {
                                    bestMse = mse;
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
        /// Valori MSE di verifica per ciascun punto
        /// </summary>
        public double[] VerifyMseValues { get { return this._verifyMseValues; } }

        /// <summary>
        /// Validita' di ciascun punto di verifica
        /// </summary>
        public bool[] VerifyPointValid { get { return this._verifyPointValid; } }

        #endregion
    }
}
