using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MergeLanguageTracks
{
    /// <summary>
    /// Sincronizzazione tramite confronto visivo frame-by-frame
    /// </summary>
    public class FrameSyncService : VideoSyncServiceBase
    {
        #region Costanti

        /// <summary>
        /// Durata minima video in ms per procedere
        /// </summary>
        private const int MIN_DURATION_MS = 10000;

        /// <summary>
        /// Inizio estrazione sorgente per ricerca delay
        /// </summary>
        private const int INITIAL_SOURCE_START_SEC = 1;

        /// <summary>
        /// Durata segmento sorgente per ricerca delay
        /// </summary>
        private const int INITIAL_SOURCE_DURATION_SEC = 120;

        /// <summary>
        /// Durata segmento lingua per ricerca delay
        /// </summary>
        private const int INITIAL_LANG_DURATION_SEC = 180;

        /// <summary>
        /// Minimo punti consistenti per confermare delay iniziale
        /// </summary>
        private const int INITIAL_MIN_VALID_POINTS = 10;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Offset raffinato per ciascun punto di verifica
        /// </summary>
        private int[] _offsets;

        /// <summary>
        /// MSE per ciascun punto di verifica
        /// </summary>
        private double[] _mseValues;

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
        public FrameSyncService(string ffmpegPath) : base(ffmpegPath, "FRAME-SYNC")
        {
            this._offsets = new int[NUM_CHECK_POINTS];
            this._mseValues = new double[NUM_CHECK_POINTS];
            this._pointValid = new bool[NUM_CHECK_POINTS];
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

            stopwatch.Start();

            // Resetta risultati verifica
            for (int i = 0; i < NUM_CHECK_POINTS; i++)
            {
                this._offsets[i] = int.MinValue;
                this._mseValues[i] = double.MaxValue;
                this._pointValid[i] = false;
            }

            // Ottiene informazioni video dal file sorgente
            infoOk = this.GetVideoInfo(sourceFile, out durationMs, out fps);

            if (infoOk && durationMs >= MIN_DURATION_MS)
            {
                // Calcola intervallo tra frame in ms
                frameIntervalMs = (int)Math.Round(1000.0 / fps);
                if (frameIntervalMs < 1)
                {
                    frameIntervalMs = 1;
                }

                ConsoleHelper.WriteDarkGray("  [FRAME-SYNC] Durata: " + (durationMs / 1000) + "s, FPS: " + fps.ToString("F3", CultureInfo.InvariantCulture) + ", intervallo frame: " + frameIntervalMs + "ms, core: " + Environment.ProcessorCount);

                // Fase 1: ricerca delay iniziale tramite scene-cut voting
                ConsoleHelper.WriteCyan("  [FRAME-SYNC] Ricerca delay iniziale (2 min source, 3 min lang, " + Environment.ProcessorCount + " thread)...");
                initialDelay = this.FindInitialDelay(sourceFile, languageFile, fps);

                if (initialDelay != int.MinValue)
                {
                    ConsoleHelper.WriteGreen("  [FRAME-SYNC] Delay iniziale: " + initialDelay + "ms");

                    // Fase 2: verifica a 9 punti distribuiti nel video
                    ConsoleHelper.WriteCyan("  [FRAME-SYNC] Verifica a 9 punti (" + Environment.ProcessorCount + " thread)...");
                    this.VerifyAtMultiplePoints(sourceFile, languageFile, durationMs, initialDelay, fps);

                    // Raccoglie offset validi
                    for (int p = 0; p < NUM_CHECK_POINTS; p++)
                    {
                        if (this._pointValid[p])
                        {
                            validOffsets.Add(this._offsets[p]);
                        }
                    }

                    validCount = validOffsets.Count;

                    if (validCount >= MIN_VALID_POINTS)
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

                        if (bestGroupCount >= MIN_VALID_POINTS)
                        {
                            // Log punti anomali scartati
                            anomalyCount = validCount - bestGroupCount;
                            if (anomalyCount > 0)
                            {
                                ConsoleHelper.WriteDarkYellow("  [FRAME-SYNC] " + anomalyCount + " punti anomali scartati, " + bestGroupCount + " punti coerenti");
                            }

                            resultOffset = bestGroupOffset;
                        }
                        else
                        {
                            // Offset non coerenti
                            StringBuilder detail = new StringBuilder();
                            for (int p = 0; p < NUM_CHECK_POINTS; p++)
                            {
                                if (this._pointValid[p])
                                {
                                    if (detail.Length > 0) { detail.Append(", "); }
                                    detail.Append((p + 1) * 10 + "%=" + this._offsets[p] + "ms");
                                }
                            }
                            ConsoleHelper.WriteWarning("  [FRAME-SYNC] Offset non coerenti (" + bestGroupCount + "/" + validCount + " nel gruppo principale): " + detail.ToString());
                        }
                    }
                    else
                    {
                        ConsoleHelper.WriteWarning("  [FRAME-SYNC] Solo " + validCount + "/" + NUM_CHECK_POINTS + " punti validi (minimo richiesto: " + MIN_VALID_POINTS + ")");
                    }
                }
                else
                {
                    ConsoleHelper.WriteWarning("  [FRAME-SYNC] Impossibile determinare delay iniziale");
                }
            }
            else
            {
                ConsoleHelper.WriteWarning("  [FRAME-SYNC] Impossibile ottenere info video o durata troppo breve");
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

            for (int p = 0; p < NUM_CHECK_POINTS; p++)
            {
                if (sb.Length > 0) { sb.Append(", "); }
                percentage = (p + 1) * 10;

                if (this._pointValid[p])
                {
                    sb.Append(percentage + "%=" + this._offsets[p] + "ms(MSE:" + this._mseValues[p].ToString("F1", CultureInfo.InvariantCulture) + ")");
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
        /// <param name="fps">Frame rate del video</param>
        /// <returns>Delay in ms o int.MinValue se non determinabile</returns>
        private int FindInitialDelay(string sourceFile, string languageFile, double fps)
        {
            int result = int.MinValue;
            List<byte[]> sourceFrames = null;
            List<byte[]> langFrames = null;
            string sourceFileCopy = sourceFile;
            string langFileCopy = languageFile;
            double frameIntervalMs = 1000.0 / fps;
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
            int expectedLangFrame = 0;
            int nearestLangCutIdx = -1;
            int nearestDist = 0;
            int dist = 0;
            int sigStartSrc = 0;
            int sigStartLng = 0;
            double mse = 0.0;

            ConsoleHelper.WriteDarkGray("  [FRAME-SYNC] Estrazione frame (source " + INITIAL_SOURCE_DURATION_SEC + "s, lang " + INITIAL_LANG_DURATION_SEC + "s)...");

            // Estrae segmenti in parallelo
            Thread sourceThread = new Thread(() => { sourceFrames = this.ExtractSegment(sourceFileCopy, INITIAL_SOURCE_START_SEC * 1000, INITIAL_SOURCE_DURATION_SEC); });
            Thread langThread = new Thread(() => { langFrames = this.ExtractSegment(langFileCopy, 0, INITIAL_LANG_DURATION_SEC); });
            sourceThread.Start();
            langThread.Start();
            sourceThread.Join();
            langThread.Join();

            // Verifica frame sufficienti
            if (sourceFrames == null || sourceFrames.Count < CUT_SIGNATURE_LENGTH)
            {
                ConsoleHelper.WriteWarning("  [FRAME-SYNC] Frame sorgente insufficienti: " + (sourceFrames != null ? sourceFrames.Count : 0));
            }
            else if (langFrames == null || langFrames.Count < CUT_SIGNATURE_LENGTH)
            {
                ConsoleHelper.WriteWarning("  [FRAME-SYNC] Frame lingua insufficienti: " + (langFrames != null ? langFrames.Count : 0));
            }
            else
            {
                ConsoleHelper.WriteDarkGray("  [FRAME-SYNC] Frame estratti: source=" + sourceFrames.Count + ", lang=" + langFrames.Count);

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
                ConsoleHelper.WriteDarkGray("  [FRAME-SYNC] Tagli rilevati: source=" + sourceCutsRaw.Count + " (" + srcCutCount + " utilizzabili), lang=" + langCutsRaw.Count + " (" + lngCutCount + " utilizzabili)");

                if (srcCutCount >= MIN_SCENE_CUTS && lngCutCount >= MIN_SCENE_CUTS)
                {
                    // Genera offset candidati da tutte le coppie (sourceCut, langCut)
                    candidateCount = srcCutCount * lngCutCount;
                    candidates = new double[candidateCount];
                    candidateIdx = 0;

                    for (int s = 0; s < srcCutCount; s++)
                    {
                        double srcMs = (INITIAL_SOURCE_START_SEC * 1000.0) + (validSourceCuts[s] * frameIntervalMs);
                        for (int l = 0; l < lngCutCount; l++)
                        {
                            double lngMs = validLangCuts[l] * frameIntervalMs;
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
                    ConsoleHelper.WriteDarkGray("  [FRAME-SYNC] Voting: " + bestClusterCount + " voti su " + candidateCount + " candidati, offset=" + ((int)Math.Round(winningOffset)) + "ms");

                    // Verifica MSE: per ogni taglio source trova il taglio lang atteso e confronta firma
                    for (int s = 0; s < srcCutCount; s++)
                    {
                        srcCutMs = (INITIAL_SOURCE_START_SEC * 1000.0) + (validSourceCuts[s] * frameIntervalMs);
                        expectedLangCutMs = srcCutMs + winningOffset;
                        expectedLangFrame = (int)Math.Round(expectedLangCutMs / frameIntervalMs);

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
                                    double actualLngMs = validLangCuts[nearestLangCutIdx] * frameIntervalMs;
                                    verifiedDelays.Add(actualLngMs - srcCutMs);
                                }
                            }
                        }
                    }

                    verifiedCount = verifiedDelays.Count;
                    ConsoleHelper.WriteDarkGray("  [FRAME-SYNC] Tagli verificati MSE: " + verifiedCount + "/" + srcCutCount);

                    if (verifiedDelays.Count >= MIN_SCENE_CUTS)
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

                        if (consistentCount >= MIN_SCENE_CUTS)
                        {
                            result = (int)Math.Round(medianDelay);
                            ConsoleHelper.WriteDarkGray("  [FRAME-SYNC] Delay iniziale: " + result + "ms (mediana di " + verifiedDelays.Count + " tagli, " + consistentCount + " consistenti)");
                        }
                        else
                        {
                            ConsoleHelper.WriteWarning("  [FRAME-SYNC] Delay non consistente: solo " + consistentCount + "/" + verifiedDelays.Count + " entro 1 frame dalla mediana");
                        }
                    }
                    else
                    {
                        ConsoleHelper.WriteWarning("  [FRAME-SYNC] Solo " + verifiedDelays.Count + " tagli verificati (minimo: " + MIN_SCENE_CUTS + ")");
                    }
                }
                else
                {
                    ConsoleHelper.WriteWarning("  [FRAME-SYNC] Tagli di scena insufficienti: source=" + srcCutCount + ", lang=" + lngCutCount + " (minimo: " + MIN_SCENE_CUTS + ")");
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
        /// <param name="fps">Frame rate del video</param>
        private void VerifyAtMultiplePoints(string sourceFile, string languageFile, int durationMs, int initialDelay, double fps)
        {
            int threadCount = Environment.ProcessorCount;
            string srcFile = sourceFile;
            string lngFile = languageFile;
            int percentage = 0;
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
                        int pointMs = (int)((long)durationMs * pct / 100);

                        double mse = 0.0;
                        int offset = this.VerifyAtPoint(srcFile, lngFile, pointMs, initialDelay, fps, VERIFY_SOURCE_DURATION_SEC, VERIFY_LANG_DURATION_SEC, out mse);

                        this._offsets[p] = offset;
                        this._mseValues[p] = mse;

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
            for (int p = 0; p < NUM_CHECK_POINTS; p++)
            {
                if (!this._pointValid[p])
                {
                    retryCount++;
                }
            }

            if (retryCount > 0 && retryCount < NUM_CHECK_POINTS)
            {
                ConsoleHelper.WriteDarkGray("  [FRAME-SYNC] Retry " + retryCount + " punti con finestra allargata (" + VERIFY_SOURCE_RETRY_SEC + "s/" + VERIFY_LANG_RETRY_SEC + "s)...");

                Thread[] retryWorkers = new Thread[retryCount];
                int retryIdx = 0;

                for (int p = 0; p < NUM_CHECK_POINTS; p++)
                {
                    if (!this._pointValid[p])
                    {
                        int pointIndex = p;
                        int pct = (p + 1) * 10;
                        int pointMs = (int)((long)durationMs * pct / 100);

                        retryWorkers[retryIdx] = new Thread(() =>
                        {
                            double mse = 0.0;
                            int offset = this.VerifyAtPoint(srcFile, lngFile, pointMs, initialDelay, fps, VERIFY_SOURCE_RETRY_SEC, VERIFY_LANG_RETRY_SEC, out mse);

                            this._offsets[pointIndex] = offset;
                            this._mseValues[pointIndex] = mse;

                            if (offset != int.MinValue)
                            {
                                this._pointValid[pointIndex] = true;
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

                if (this._pointValid[p])
                {
                    ConsoleHelper.WriteDarkGray("  [FRAME-SYNC] " + percentage + "%: offset=" + this._offsets[p] + "ms, MSE=" + this._mseValues[p].ToString("F1", CultureInfo.InvariantCulture));
                }
                else
                {
                    ConsoleHelper.WriteDarkYellow("  [FRAME-SYNC] " + percentage + "%: nessun match");
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
                args = "-nostdin -hide_banner -i \"" + filePath + "\"";

                process = new Process();
                process.StartInfo.FileName = this._ffmpegPath;
                process.StartInfo.Arguments = args;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.Start();

                // Svuota stdout in thread separato
                Thread outThread = new Thread(() =>
                {
                    // Evita deadlock pipe stdout
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
                ConsoleHelper.WriteWarning("  [FRAME-SYNC] Errore GetVideoInfo: " + ex.Message);
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
        private int VerifyAtPoint(string sourceFile, string languageFile, int sourceTimestampMs, int baseOffset, double fps, int sourceDurationSec, int langDurationSec, out double bestMse)
        {
            bestMse = double.MaxValue;
            int resultOffset = int.MinValue;
            double frameIntervalMs = 1000.0 / fps;
            int halfSourceMs = (sourceDurationSec * 1000) / 2;
            int sourceStartMs = sourceTimestampMs - halfSourceMs;
            int langCenter = sourceTimestampMs + baseOffset;
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
            List<double> cutDelays = new List<double>();
            double medianDelay = 0.0;
            double srcCutMs = 0.0;
            double expectedLangCutMs = 0.0;
            int expectedLangFrame = 0;
            int nearestLangCutIdx = -1;
            int nearestDist = 0;
            int dist = 0;
            int sigStartSrc = 0;
            int sigStartLng = 0;
            double mse = 0.0;

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
                    srcCutMs = sourceStartMs + (validSourceCuts[s] * frameIntervalMs);

                    // Posizione attesa del taglio lang (basata su baseOffset)
                    expectedLangCutMs = srcCutMs + baseOffset;
                    expectedLangFrame = (int)Math.Round((expectedLangCutMs - langStartMs) / frameIntervalMs);

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
                                // Offset preciso basato sulle posizioni effettive
                                double actualLngMs = langStartMs + (validLangCuts[nearestLangCutIdx] * frameIntervalMs);
                                cutDelays.Add(actualLngMs - srcCutMs);

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
        /// Valori MSE per i punti di verifica
        /// </summary>
        public double[] MseValues { get { return this._mseValues; } }

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
