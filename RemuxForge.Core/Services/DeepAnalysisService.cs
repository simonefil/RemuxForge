using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace RemuxForge.Core
{
    /// <summary>
    /// Servizio di analisi avanzata per file con edit diversi tra source e lang.
    /// Rileva offset multi-segmento e produce una EditMap per il riallineamento
    /// </summary>
    public class DeepAnalysisService : VideoSyncServiceBase
    {
        #region Variabili di classe

        /// <summary>
        /// FPS per estrazione fase grossa (2 frame/sec)
        /// </summary>
        private double _coarseFps;

        /// <summary>
        /// FPS per scansione densa transizioni (1 frame/sec)
        /// </summary>
        private double _denseScanFps;

        /// <summary>
        /// Soglia SSIM sotto cui si considera dip nella scansione densa
        /// </summary>
        private double _denseScanSsimThreshold;

        /// <summary>
        /// Minimo frame consecutivi sotto soglia per confermare dip
        /// </summary>
        private int _denseScanMinDipFrames;

        /// <summary>
        /// Finestra scansione lineare di conferma attorno al punto binaria (secondi, per lato)
        /// </summary>
        private double _linearScanWindowSec;

        /// <summary>
        /// Frame consecutivi per conferma crossover nella scansione lineare
        /// </summary>
        private int _linearScanConfirmFrames;

        /// <summary>
        /// Soglia SSIM per dip in verifica regioni (molto piu' restrittiva di DENSE_SCAN)
        /// Deve essere bassa per catturare solo transizioni reali (frame neri, scene diverse)
        /// e ignorare rumore da scene cuts e compressione
        /// </summary>
        private double _verifyDipSsimThreshold;

        /// <summary>
        /// Punti di probing multi-point dopo il dip (secondi dopo il dip).
        /// Tre punti distanziati per eliminare falsi positivi da scene cuts
        /// </summary>
        private double[] _probeMultiMarginsSec;

        /// <summary>
        /// Minimo punti di probing consistenti per confermare cambio offset
        /// </summary>
        private int _probeMinConsistentPoints;

        /// <summary>
        /// Durata segmento per probing offset in secondi
        /// </summary>
        private double _offsetProbeDurationSec;

        /// <summary>
        /// Offset candidati da testare nel probing (ms, aggiunti all'offset corrente)
        /// </summary>
        private int[] _offsetProbeDeltas;

        /// <summary>
        /// Soglia SSIM minima per accettare un offset candidato nel probing
        /// </summary>
        private double _offsetProbeMinSsim;

        /// <summary>
        /// Differenza minima offset per considerare un cambio reale (ms)
        /// </summary>
        private int _minOffsetChangeMs;

        /// <summary>
        /// Minimo match consecutivi con stesso offset per conferma cambio
        /// </summary>
        private int _minConsecutiveStable;

        /// <summary>
        /// Soglia ffmpeg scene detection
        /// </summary>
        private double _sceneThreshold;

        /// <summary>
        /// Tolleranza match scene cuts (ms)
        /// </summary>
        private int _matchToleranceMs;

        /// <summary>
        /// Tolleranza ampia per ricerca probe cambio offset (secondi)
        /// </summary>
        private double _wideProbeToleranceSec;

        /// <summary>
        /// Timeout estrazione scene cuts per singolo file (ms)
        /// </summary>
        private int _sceneExtractTimeoutMs;

        /// <summary>
        /// Numero di punti di verifica globale
        /// </summary>
        private int _globalVerifyPoints;

        /// <summary>
        /// Percentuale minima di punti che devono verificare per validazione
        /// </summary>
        private double _globalVerifyMinRatio;

        /// <summary>
        /// Moltiplicatore MSE baseline per soglia di verifica
        /// </summary>
        private double _verifyMseMultiplier;

        /// <summary>
        /// Range ricerca offset iniziale in secondi
        /// </summary>
        private int _initialOffsetRangeSec;

        /// <summary>
        /// Step ricerca offset iniziale in secondi
        /// </summary>
        private double _initialOffsetStepSec;

        /// <summary>
        /// Numero di source cuts iniziali per voting offset
        /// </summary>
        private int _initialVotingCuts;

        /// <summary>
        /// Tempo di esecuzione analisi in ms
        /// </summary>
        private long _analysisTimeMs;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso eseguibile ffmpeg</param>
        public DeepAnalysisService(string ffmpegPath) : base(ffmpegPath, LogSection.Deep)
        {
            DeepAnalysisConfig cfg = AppSettingsService.Instance.Settings.Advanced.DeepAnalysis;
            this._coarseFps = cfg.CoarseFps;
            this._denseScanFps = cfg.DenseScanFps;
            this._denseScanSsimThreshold = cfg.DenseScanSsimThreshold;
            this._denseScanMinDipFrames = cfg.DenseScanMinDipFrames;
            this._linearScanWindowSec = cfg.LinearScanWindowSec;
            this._linearScanConfirmFrames = cfg.LinearScanConfirmFrames;
            this._verifyDipSsimThreshold = cfg.VerifyDipSsimThreshold;
            this._probeMultiMarginsSec = cfg.ProbeMultiMarginsSec.ToArray();
            this._probeMinConsistentPoints = cfg.ProbeMinConsistentPoints;
            this._offsetProbeDurationSec = cfg.OffsetProbeDurationSec;
            this._offsetProbeDeltas = cfg.OffsetProbeDeltas.ToArray();
            this._offsetProbeMinSsim = cfg.OffsetProbeMinSsim;
            this._minOffsetChangeMs = cfg.MinOffsetChangeMs;
            this._minConsecutiveStable = cfg.MinConsecutiveStable;
            this._sceneThreshold = cfg.SceneThreshold;
            this._matchToleranceMs = cfg.MatchToleranceMs;
            this._wideProbeToleranceSec = cfg.WideProbeToleranceSec;
            this._sceneExtractTimeoutMs = cfg.SceneExtractTimeoutMs;
            this._globalVerifyPoints = cfg.GlobalVerifyPoints;
            this._globalVerifyMinRatio = cfg.GlobalVerifyMinRatio;
            this._verifyMseMultiplier = cfg.VerifyMseMultiplier;
            this._initialOffsetRangeSec = cfg.InitialOffsetRangeSec;
            this._initialOffsetStepSec = cfg.InitialOffsetStepSec;
            this._initialVotingCuts = cfg.InitialVotingCuts;
            this._analysisTimeMs = 0;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Analizza source e lang per produrre una EditMap con le operazioni di riallineamento
        /// </summary>
        /// <param name="sourceFile">Percorso file source</param>
        /// <param name="langFile">Percorso file lang</param>
        /// <param name="sourceDefaultDurationNs">Default duration traccia video source in nanosecondi</param>
        /// <param name="langDefaultDurationNs">Default duration traccia video lang in nanosecondi</param>
        /// <param name="sourceDurationMs">Durata container source in millisecondi</param>
        /// <returns>EditMap con operazioni di edit, null se analisi fallita</returns>
        public EditMap Analyze(string sourceFile, string langFile, long sourceDefaultDurationNs, long langDefaultDurationNs, int sourceDurationMs)
        {
            EditMap result = null;
            Stopwatch stopwatch = new Stopwatch();
            double stretchRatio = 0.0;
            double inverseRatio = 1.0;
            string stretchFactor = "";
            List<double> sourceCuts = null;
            List<double> langCuts = null;
            List<OffsetRegion> regions = null;
            List<EditOperation> operations = null;
            bool verified = false;
            double baselineMse = 0.0;
            int matchedCuts = 0;

            stopwatch.Start();

            // Fase 1: Rilevamento stretch globale
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Fase 1: Rilevamento stretch...");
            this.DetectStretch(sourceDefaultDurationNs, langDefaultDurationNs, out stretchRatio, out inverseRatio, out stretchFactor);

            // Fase 2: Estrazione scene cuts completa
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Fase 2: Estrazione scene cuts...");
            this.ExtractAllSceneCuts(sourceFile, langFile, out sourceCuts, out langCuts);

            if (sourceCuts == null || langCuts == null || sourceCuts.Count < this._minSceneCuts || langCuts.Count < this._minSceneCuts)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  Scene cuts insufficienti: source=" + (sourceCuts != null ? sourceCuts.Count : 0) + ", lang=" + (langCuts != null ? langCuts.Count : 0));
                stopwatch.Stop();
                this._analysisTimeMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Source: " + sourceCuts.Count + " scene cuts in " + (sourceDurationMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + "s");
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Lang: " + langCuts.Count + " scene cuts");

            // Fase 3: Matching scene cuts e rilevamento regioni
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Fase 3: Matching scene cuts...");
            regions = this.MatchSceneCuts(sourceCuts, langCuts, stretchRatio, inverseRatio, out matchedCuts);

            if (regions == null || regions.Count == 0)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  Matching fallito, nessuna regione trovata");
                stopwatch.Stop();
                this._analysisTimeMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Totale: " + matchedCuts + "/" + sourceCuts.Count + " match (" + (matchedCuts * 100 / sourceCuts.Count) + "%)");

            // Fase 3.5: Verifica regioni tramite scansione densa SSIM
            // Cerca transizioni nascoste che Fase 3 non ha rilevato via scene cuts
            regions = this.VerifyRegions(sourceFile, langFile, regions, inverseRatio);

            // Fase 4: Raffinamento transizioni
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Fase 4: Raffinamento transizioni...");
            operations = this.RefineTransitions(sourceFile, langFile, regions, inverseRatio);

            // Fase 5: Verifica globale
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Phase, "  Fase 5: Verifica globale...");
            verified = this.VerifyGlobal(sourceFile, langFile, regions, operations, inverseRatio, sourceDurationMs, out baselineMse);

            if (!verified)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  Verifica globale fallita");
                stopwatch.Stop();
                this._analysisTimeMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            // Costruisci EditMap
            stopwatch.Stop();
            this._analysisTimeMs = stopwatch.ElapsedMilliseconds;

            result = new EditMap();
            result.InitialDelayMs = (int)Math.Round(regions[0].OffsetMs);
            result.StretchFactor = stretchFactor;
            result.Operations = operations;
            result.AnalysisTimeMs = this._analysisTimeMs;
            result.SourceCutsAnalyzed = sourceCuts.Count;
            result.LangCutsAnalyzed = langCuts.Count;
            result.MatchedCuts = matchedCuts;
            result.BaselineMse = baselineMse;

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Success, "  EditMap: delay=" + result.InitialDelayMs + "ms, " + operations.Count + " operazioni, analisi " + this._analysisTimeMs + "ms");

            return result;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Tempo di esecuzione analisi in ms
        /// </summary>
        public long AnalysisTimeMs { get { return this._analysisTimeMs; } }

        #endregion

        #region Metodi privati — Fase 1: Stretch

        /// <summary>
        /// Rileva stretch globale dai default_duration delle tracce video
        /// </summary>
        /// <param name="sourceDefaultDurationNs">Default duration source in ns</param>
        /// <param name="langDefaultDurationNs">Default duration lang in ns</param>
        /// <param name="stretchRatio">Rapporto stretch (output)</param>
        /// <param name="inverseRatio">Rapporto inverso per compensazione drift (output)</param>
        /// <param name="stretchFactor">Stringa stretch per mkvmerge (output)</param>
        private void DetectStretch(long sourceDefaultDurationNs, long langDefaultDurationNs, out double stretchRatio, out double inverseRatio, out string stretchFactor)
        {
            double sourceFps = 0.0;
            double langFps = 0.0;
            double ratioDiff = 0.0;

            stretchRatio = 1.0;
            inverseRatio = 1.0;
            stretchFactor = "";

            if (sourceDefaultDurationNs > 0 && langDefaultDurationNs > 0)
            {
                stretchRatio = (double)sourceDefaultDurationNs / langDefaultDurationNs;
                ratioDiff = Math.Abs(stretchRatio - 1.0);

                if (ratioDiff >= 0.001)
                {
                    // Stretch significativo
                    inverseRatio = 1.0 / stretchRatio;
                    stretchFactor = sourceDefaultDurationNs.ToString() + "/" + langDefaultDurationNs.ToString();
                    sourceFps = 1000000000.0 / sourceDefaultDurationNs;
                    langFps = 1000000000.0 / langDefaultDurationNs;

                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Stretch: " + stretchRatio.ToString("F6", CultureInfo.InvariantCulture) + " (" + sourceFps.ToString("F3", CultureInfo.InvariantCulture) + "fps -> " + langFps.ToString("F3", CultureInfo.InvariantCulture) + "fps)");
                }
                else
                {
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Stretch: nessuno (stesso fps)");
                }
            }
        }

        #endregion

        #region Metodi privati — Fase 2: Estrazione scene cuts

        /// <summary>
        /// Estrae tutti i tagli di scena da entrambi i file in parallelo
        /// </summary>
        /// <param name="sourceFile">Percorso file source</param>
        /// <param name="langFile">Percorso file lang</param>
        /// <param name="sourceCuts">Scene cuts source in secondi (output)</param>
        /// <param name="langCuts">Scene cuts lang in secondi (output)</param>
        private void ExtractAllSceneCuts(string sourceFile, string langFile, out List<double> sourceCuts, out List<double> langCuts)
        {
            List<double> srcResult = null;
            List<double> lngResult = null;

            // Estrazione parallela su due thread
            Thread sourceThread = new Thread(() =>
            {
                srcResult = this.ExtractSceneCutsFromFile(sourceFile);
            });
            Thread langThread = new Thread(() =>
            {
                lngResult = this.ExtractSceneCutsFromFile(langFile);
            });

            sourceThread.Start();
            langThread.Start();
            sourceThread.Join();
            langThread.Join();

            sourceCuts = srcResult;
            langCuts = lngResult;
        }

        /// <summary>
        /// Estrae scene cuts da un singolo file tramite ffmpeg
        /// </summary>
        /// <param name="filePath">Percorso file video</param>
        /// <returns>Lista timestamp in secondi, null se errore</returns>
        private List<double> ExtractSceneCutsFromFile(string filePath)
        {
            List<double> cuts = new List<double>();
            Process process = null;
            string line = "";
            int ptsIdx = 0;
            int endIdx = 0;
            string ptsStr = "";
            double ptsTime = 0.0;
            string thresholdStr = this._sceneThreshold.ToString("F2", CultureInfo.InvariantCulture);

            try
            {
                process = new Process();
                process.StartInfo.FileName = this._ffmpegPath;
                process.StartInfo.ArgumentList.Add("-nostdin");
                process.StartInfo.ArgumentList.Add("-hide_banner");
                if (this._useHwaccel)
                {
                    process.StartInfo.ArgumentList.Add("-hwaccel");
                    process.StartInfo.ArgumentList.Add("auto");
                }
                process.StartInfo.ArgumentList.Add("-i");
                process.StartInfo.ArgumentList.Add(filePath);
                process.StartInfo.ArgumentList.Add("-vf");
                process.StartInfo.ArgumentList.Add("select='gt(scene," + thresholdStr + ")',showinfo");
                process.StartInfo.ArgumentList.Add("-vsync");
                process.StartInfo.ArgumentList.Add("vfr");
                process.StartInfo.ArgumentList.Add("-f");
                process.StartInfo.ArgumentList.Add("null");
                process.StartInfo.ArgumentList.Add("-");
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.Start();

                // Svuota stdout in thread separato
                // Catch silenzioso intenzionale: pipe puo' chiudersi se il processo termina
                Thread stdoutThread = new Thread(() =>
                {
                    try { process.StandardOutput.ReadToEnd(); }
                    catch { }
                });
                stdoutThread.Start();

                // Parsa stderr per showinfo: cerca pts_time
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    ptsIdx = line.IndexOf("pts_time:", StringComparison.Ordinal);
                    if (ptsIdx >= 0)
                    {
                        // Estrai valore dopo "pts_time:"
                        ptsIdx += 9;
                        endIdx = line.IndexOf(' ', ptsIdx);
                        if (endIdx < 0) { endIdx = line.Length; }
                        ptsStr = line.Substring(ptsIdx, endIdx - ptsIdx).Trim();

                        if (double.TryParse(ptsStr, NumberStyles.Float, CultureInfo.InvariantCulture, out ptsTime))
                        {
                            cuts.Add(ptsTime);
                        }
                    }
                }

                stdoutThread.Join();

                // Attendi termine con timeout
                if (!process.WaitForExit(this._sceneExtractTimeoutMs))
                {
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  Timeout estrazione scene cuts: " + Path.GetFileName(filePath));
                    // Kill best-effort: il processo potrebbe essere gia' terminato
                    try { process.Kill(); } catch { }
                    cuts = null;
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "  Errore estrazione scene cuts: " + ex.Message);
                cuts = null;
            }
            finally
            {
                if (process != null) { process.Dispose(); process = null; }
            }

            return cuts;
        }

        #endregion

        #region Metodi privati — Fase 3: Matching scene cuts

        /// <summary>
        /// Abbina scene cuts source e lang per rilevare regioni con offset diversi
        /// </summary>
        /// <param name="sourceCuts">Scene cuts source in secondi</param>
        /// <param name="langCuts">Scene cuts lang in secondi</param>
        /// <param name="stretchRatio">Rapporto stretch</param>
        /// <param name="inverseRatio">Rapporto inverso per compensazione</param>
        /// <param name="matchedCuts">Numero di match totali (output)</param>
        /// <returns>Lista regioni con offset, null se fallito</returns>
        private List<OffsetRegion> MatchSceneCuts(List<double> sourceCuts, List<double> langCuts, double stretchRatio, double inverseRatio, out int matchedCuts)
        {
            List<OffsetRegion> regions = new List<OffsetRegion>();
            double initialOffset = 0.0;
            double currentOffset = 0.0;
            double expectedLangTime = 0.0;
            double closestLangTime = 0.0;
            double actualOffset = 0.0;
            double offsetDiff = 0.0;
            int consecutiveNew = 0;
            double candidateOffset = 0.0;
            int regionMatchCount = 0;
            OffsetRegion currentRegion = null;
            double toleranceSec = this._matchToleranceMs / 1000.0;
            List<double> regionOffsets = new List<double>();

            matchedCuts = 0;

            // Determina offset iniziale tramite voting
            initialOffset = this.FindInitialOffset(sourceCuts, langCuts, inverseRatio);

            if (initialOffset == double.MinValue)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Error, "  Impossibile determinare offset iniziale");
                return null;
            }

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Offset iniziale: " + ((int)(initialOffset * 1000)).ToString() + "ms");

            currentOffset = initialOffset;
            consecutiveNew = 0;
            candidateOffset = 0.0;
            regionMatchCount = 0;

            // Apri prima regione
            currentRegion = new OffsetRegion();
            currentRegion.StartSrcSec = 0.0;
            currentRegion.OffsetMs = initialOffset * 1000.0;

            // Scansione di tutti i source cuts
            for (int i = 0; i < sourceCuts.Count; i++)
            {
                // Calcola posizione attesa nel lang
                expectedLangTime = sourceCuts[i] - currentOffset;
                if (Math.Abs(inverseRatio - 1.0) > 0.0001)
                {
                    // Compensazione drift per stretch
                    expectedLangTime = expectedLangTime * inverseRatio;
                }

                // Cerca match piu' vicino in lang
                closestLangTime = this.FindClosestCut(langCuts, expectedLangTime, toleranceSec);

                if (closestLangTime >= 0.0)
                {
                    // Match trovato
                    actualOffset = sourceCuts[i] - closestLangTime;
                    offsetDiff = Math.Abs(actualOffset - currentOffset) * 1000.0;

                    if (offsetDiff > this._minOffsetChangeMs)
                    {
                        // Potenziale cambio offset
                        if (consecutiveNew == 0 || Math.Abs(actualOffset - candidateOffset) * 1000.0 < this._matchToleranceMs)
                        {
                            // Stesso candidato
                            if (consecutiveNew == 0) { candidateOffset = actualOffset; }
                            consecutiveNew++;

                            if (consecutiveNew >= this._minConsecutiveStable)
                            {
                                // Cambio confermato: chiudi regione con mediana offset
                                currentRegion.EndSrcSec = sourceCuts[i - this._minConsecutiveStable + 1];
                                currentRegion.MatchCount = regionMatchCount;
                                if (regionOffsets.Count > 0)
                                {
                                    currentRegion.OffsetMs = this.ComputeMedian(regionOffsets);
                                }
                                regions.Add(currentRegion);

                                double oldOffset = currentOffset;
                                currentOffset = candidateOffset;

                                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Cambio offset a src " + sourceCuts[i].ToString("F2", CultureInfo.InvariantCulture) + "s: " + ((int)(currentRegion.OffsetMs)).ToString() + " -> " + ((int)(currentOffset * 1000)).ToString() + "ms");

                                // Nuova regione
                                currentRegion = new OffsetRegion();
                                currentRegion.StartSrcSec = sourceCuts[i - this._minConsecutiveStable + 1];
                                currentRegion.OffsetMs = currentOffset * 1000.0;
                                regionMatchCount = consecutiveNew;
                                matchedCuts += consecutiveNew;
                                regionOffsets.Clear();
                                consecutiveNew = 0;
                            }
                        }
                        else
                        {
                            // Candidato diverso, reset
                            consecutiveNew = 1;
                            candidateOffset = actualOffset;
                        }
                    }
                    else
                    {
                        // Match con offset corrente - raccogli offset reale per mediana
                        matchedCuts++;
                        regionMatchCount++;
                        regionOffsets.Add(actualOffset * 1000.0);
                        consecutiveNew = 0;
                    }
                }
                else
                {
                    // Nessun match con offset corrente
                    if (consecutiveNew > 0)
                    {
                        // Candidato attivo: verifica se matcha con il candidato
                        double candidateExpected = sourceCuts[i] - candidateOffset;
                        if (Math.Abs(inverseRatio - 1.0) > 0.0001)
                        {
                            candidateExpected = candidateExpected * inverseRatio;
                        }
                        double candidateMatch = this.FindClosestCut(langCuts, candidateExpected, toleranceSec);
                        if (candidateMatch >= 0.0)
                        {
                            consecutiveNew++;
                            if (consecutiveNew >= this._minConsecutiveStable)
                            {
                                // Cambio confermato: chiudi regione con mediana offset
                                currentRegion.EndSrcSec = sourceCuts[i - this._minConsecutiveStable + 1];
                                currentRegion.MatchCount = regionMatchCount;
                                if (regionOffsets.Count > 0)
                                {
                                    currentRegion.OffsetMs = this.ComputeMedian(regionOffsets);
                                }
                                regions.Add(currentRegion);

                                double oldOffset = currentOffset;
                                currentOffset = candidateOffset;

                                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Cambio offset a src " + sourceCuts[i].ToString("F2", CultureInfo.InvariantCulture) + "s: " + ((int)(currentRegion.OffsetMs)).ToString() + " -> " + ((int)(currentOffset * 1000)).ToString() + "ms");

                                // Nuova regione
                                currentRegion = new OffsetRegion();
                                currentRegion.StartSrcSec = sourceCuts[i - this._minConsecutiveStable + 1];
                                currentRegion.OffsetMs = currentOffset * 1000.0;
                                regionMatchCount = consecutiveNew;
                                matchedCuts += consecutiveNew;
                                regionOffsets.Clear();
                                consecutiveNew = 0;
                            }
                        }
                        else
                        {
                            consecutiveNew = 0;
                        }
                    }
                    else
                    {
                        // Nessun candidato: probe con tolleranza ampia per scoprire nuovo offset
                        double wideMatch = this.FindClosestCut(langCuts, expectedLangTime, this._wideProbeToleranceSec);
                        if (wideMatch >= 0.0)
                        {
                            double probeOffset = sourceCuts[i] - wideMatch;
                            double probeDiff = Math.Abs(probeOffset - currentOffset) * 1000.0;
                            if (probeDiff > this._minOffsetChangeMs)
                            {
                                candidateOffset = probeOffset;
                                consecutiveNew = 1;
                            }
                        }
                    }
                }
            }

            // Chiudi ultima regione con mediana offset
            currentRegion.EndSrcSec = sourceCuts[sourceCuts.Count - 1];
            currentRegion.MatchCount = regionMatchCount;
            if (regionOffsets.Count > 0)
            {
                currentRegion.OffsetMs = this.ComputeMedian(regionOffsets);
            }
            regions.Add(currentRegion);

            // Log regioni
            for (int r = 0; r < regions.Count; r++)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Regione " + (r + 1) + ": src " + regions[r].StartSrcSec.ToString("F0", CultureInfo.InvariantCulture) + "-" + regions[r].EndSrcSec.ToString("F0", CultureInfo.InvariantCulture) + "s offset " + ((int)regions[r].OffsetMs).ToString() + "ms (" + regions[r].MatchCount + " match)");
            }

            return regions;
        }

        /// <summary>
        /// Determina l'offset iniziale tramite voting sui primi N source cuts
        /// </summary>
        /// <param name="sourceCuts">Scene cuts source in secondi</param>
        /// <param name="langCuts">Scene cuts lang in secondi</param>
        /// <param name="inverseRatio">Rapporto inverso per compensazione stretch</param>
        /// <returns>Offset iniziale in secondi, double.MinValue se fallito</returns>
        private double FindInitialOffset(List<double> sourceCuts, List<double> langCuts, double inverseRatio)
        {
            double bestOffset = double.MinValue;
            int bestVotes = 0;
            double testOffset = 0.0;
            int votes = 0;
            double expectedLang = 0.0;
            double closestLang = 0.0;
            double toleranceSec = this._matchToleranceMs / 1000.0;
            int cutsToTest = Math.Min(this._initialVotingCuts, sourceCuts.Count);
            int steps = (int)(this._initialOffsetRangeSec * 2 / this._initialOffsetStepSec);

            // Testa range di offset da -30s a +30s con step 0.5s
            for (int s = 0; s <= steps; s++)
            {
                testOffset = -this._initialOffsetRangeSec + s * this._initialOffsetStepSec;
                votes = 0;

                for (int i = 0; i < cutsToTest; i++)
                {
                    expectedLang = sourceCuts[i] - testOffset;
                    if (Math.Abs(inverseRatio - 1.0) > 0.0001)
                    {
                        expectedLang = expectedLang * inverseRatio;
                    }

                    closestLang = this.FindClosestCut(langCuts, expectedLang, toleranceSec);
                    if (closestLang >= 0.0)
                    {
                        votes++;
                    }
                }

                if (votes > bestVotes)
                {
                    bestVotes = votes;
                    bestOffset = testOffset;
                }
            }

            // Richiedi almeno 30% di match per considerare valido
            if (bestVotes < cutsToTest * 0.3)
            {
                bestOffset = double.MinValue;
            }
            else
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Offset iniziale voting: " + ((int)(bestOffset * 1000)).ToString() + "ms (" + bestVotes + "/" + cutsToTest + " match)");
            }

            return bestOffset;
        }

        /// <summary>
        /// Trova il cut piu' vicino in una lista ordinata entro una tolleranza
        /// </summary>
        /// <param name="cuts">Lista ordinata di timestamp in secondi</param>
        /// <param name="target">Timestamp target</param>
        /// <param name="toleranceSec">Tolleranza in secondi</param>
        /// <returns>Timestamp del cut trovato, -1.0 se non trovato</returns>
        private double FindClosestCut(List<double> cuts, double target, double toleranceSec)
        {
            double result = -1.0;
            double bestDist = double.MaxValue;
            double dist = 0.0;
            int lo = 0;
            int hi = cuts.Count - 1;
            int mid = 0;

            // Ricerca binaria per trovare la posizione approssimata
            while (lo <= hi)
            {
                mid = (lo + hi) / 2;
                if (cuts[mid] < target) { lo = mid + 1; }
                else { hi = mid - 1; }
            }

            // Controlla i candidati attorno alla posizione trovata
            for (int j = Math.Max(0, lo - 2); j <= Math.Min(cuts.Count - 1, lo + 2); j++)
            {
                dist = Math.Abs(cuts[j] - target);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    if (dist <= toleranceSec)
                    {
                        result = cuts[j];
                    }
                }
            }

            return result;
        }

        #endregion

        #region Metodi privati — Fase 3.5: Verifica regioni (scansione densa)

        /// <summary>
        /// Verifica ogni regione tramite scansione densa SSIM per individuare
        /// transizioni che Fase 3 non ha rilevato via scene cut matching.
        /// Se trova dip SSIM in una regione, la splitta e determina il nuovo offset
        /// </summary>
        /// <param name="sourceFile">Percorso file source</param>
        /// <param name="langFile">Percorso file lang</param>
        /// <param name="regions">Regioni da Fase 3</param>
        /// <param name="inverseRatio">Rapporto inverso stretch</param>
        /// <returns>Lista regioni aggiornata (uguale se nessun dip trovato)</returns>
        private List<OffsetRegion> VerifyRegions(string sourceFile, string langFile, List<OffsetRegion> regions, double inverseRatio)
        {
            List<OffsetRegion> verified = new List<OffsetRegion>();
            bool foundNew = false;

            for (int r = 0; r < regions.Count; r++)
            {
                double regionStart = regions[r].StartSrcSec;
                double regionEnd = regions[r].EndSrcSec;
                double regionDuration = regionEnd - regionStart;
                double currentOffsetMs = regions[r].OffsetMs;
                double currentOffsetSec = currentOffsetMs / 1000.0;

                // Regioni corte (< 20s) non vale la pena verificare
                if (regionDuration < 20.0)
                {
                    verified.Add(regions[r]);
                    continue;
                }

                // Scansione densa della regione
                List<double> dipPositions = this.FindDipsInRegion(sourceFile, langFile, regionStart, regionEnd, currentOffsetSec, inverseRatio);

                if (dipPositions.Count == 0)
                {
                    // Nessun dip, regione confermata
                    verified.Add(regions[r]);
                    continue;
                }

                // Trovati dip: splitta la regione
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Regione " + (r + 1) + ": trovati " + dipPositions.Count + " dip nascosti");

                double splitStart = regionStart;
                double splitOffsetMs = currentOffsetMs;

                for (int d = 0; d < dipPositions.Count; d++)
                {
                    double dipSrc = dipPositions[d];

                    // Determina offset dopo il dip tramite probing multi-punto
                    double newOffsetMs = this.ProbeOffsetAfterDip(sourceFile, langFile, dipSrc, splitOffsetMs, inverseRatio);

                    if (newOffsetMs == splitOffsetMs)
                    {
                        // Probing fallito: offset invariato, falso positivo, non splittare
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    Dip a src " + dipSrc.ToString("F1", CultureInfo.InvariantCulture) + "s: probing offset fallito, ignoro");
                        continue;
                    }

                    // Probing confermato: crea sotto-regione prima del dip col vecchio offset
                    OffsetRegion beforeDip = new OffsetRegion();
                    beforeDip.StartSrcSec = splitStart;
                    beforeDip.EndSrcSec = dipSrc;
                    beforeDip.OffsetMs = splitOffsetMs;
                    beforeDip.MatchCount = regions[r].MatchCount / (dipPositions.Count + 1);
                    verified.Add(beforeDip);

                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Dip a src " + dipSrc.ToString("F1", CultureInfo.InvariantCulture) + "s: offset " + ((int)splitOffsetMs).ToString() + " -> " + ((int)newOffsetMs).ToString() + "ms");
                    splitStart = dipSrc;
                    splitOffsetMs = newOffsetMs;
                    foundNew = true;
                }

                // Sotto-regione finale (dopo ultimo dip fino a fine regione)
                OffsetRegion afterLastDip = new OffsetRegion();
                afterLastDip.StartSrcSec = splitStart;
                afterLastDip.EndSrcSec = regionEnd;
                afterLastDip.OffsetMs = splitOffsetMs;
                afterLastDip.MatchCount = regions[r].MatchCount / (dipPositions.Count + 1);
                verified.Add(afterLastDip);
            }

            // Merge regioni adiacenti con stesso offset (artefatti da dip di Fase 3 e VerifyRegions)
            if (foundNew)
            {
                List<OffsetRegion> merged = new List<OffsetRegion>();
                merged.Add(verified[0]);

                for (int r = 1; r < verified.Count; r++)
                {
                    OffsetRegion last = merged[merged.Count - 1];

                    if (Math.Abs(verified[r].OffsetMs - last.OffsetMs) < 1.0)
                    {
                        // Stesso offset: estendi la regione precedente
                        last.EndSrcSec = verified[r].EndSrcSec;
                        last.MatchCount = last.MatchCount + verified[r].MatchCount;
                    }
                    else
                    {
                        merged.Add(verified[r]);
                    }
                }

                verified = merged;

                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Regioni dopo verifica: " + verified.Count);
                for (int r = 0; r < verified.Count; r++)
                {
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Regione " + (r + 1) + ": src " + verified[r].StartSrcSec.ToString("F0", CultureInfo.InvariantCulture) + "-" + verified[r].EndSrcSec.ToString("F0", CultureInfo.InvariantCulture) + "s offset " + ((int)verified[r].OffsetMs).ToString() + "ms");
                }
            }

            return verified;
        }

        /// <summary>
        /// Cerca tutti i dip SSIM in una regione tramite scansione densa.
        /// Un dip indica una transizione non rilevata da Fase 3
        /// </summary>
        /// <param name="sourceFile">Percorso file source</param>
        /// <param name="langFile">Percorso file lang</param>
        /// <param name="regionStart">Inizio regione source in secondi</param>
        /// <param name="regionEnd">Fine regione source in secondi</param>
        /// <param name="offsetSec">Offset della regione in secondi</param>
        /// <param name="inverseRatio">Rapporto inverso stretch</param>
        /// <returns>Lista di posizioni source (secondi) dove inizia un dip</returns>
        private List<double> FindDipsInRegion(string sourceFile, string langFile, double regionStart, double regionEnd, double offsetSec, double inverseRatio)
        {
            List<double> dips = new List<double>();
            double duration = regionEnd - regionStart;
            double langStart = 0.0;
            int maxIdx = 0;
            int consecutiveLow = 0;
            int dipStartIdx = -1;
            List<byte[]> srcFrames = null;
            double[] sourceTimestampsMs = null;
            List<byte[]> langFrames = null;
            double[] langTimestampsMs = null;
            double frameIntervalMs = 1000.0 / this._denseScanFps;
            double toleranceMs = frameIntervalMs * 2.0;
            double srcRelMs = 0.0;
            double targetLangMs = 0.0;
            int nearestIdx = 0;
            double nearestDistMs = 0.0;

            // Estrai frame source con timestamps reali
            this.ExtractSegment(sourceFile, (int)(regionStart * 1000), duration, this._denseScanFps, this._cropSourceTo43, out srcFrames, out sourceTimestampsMs);
            if (srcFrames.Count < 4)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    FindDips: estrazione source fallita (" + srcFrames.Count + " frame su " + duration.ToString("F0", CultureInfo.InvariantCulture) + "s attesi)");
                return dips;
            }

            // Posizione lang con offset della regione
            langStart = regionStart - offsetSec;
            if (Math.Abs(inverseRatio - 1.0) > 0.0001) { langStart = langStart * inverseRatio; }
            if (langStart < 0.0) { langStart = 0.0; }

            // Estrai frame lang con timestamps reali
            this.ExtractSegment(langFile, (int)(langStart * 1000), duration, this._denseScanFps, this._cropLangTo43, out langFrames, out langTimestampsMs);

            if (langFrames.Count < 4 || langTimestampsMs.Length < 4)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    FindDips: estrazione lang fallita (" + langFrames.Count + " frame su " + duration.ToString("F0", CultureInfo.InvariantCulture) + "s attesi)");
                return dips;
            }

            maxIdx = srcFrames.Count;

            // Calcola SSIM abbinando ogni frame source al frame lang piu' vicino per tempo relativo.
            // Robusto a VFR: usa i timestamps reali da showinfo invece di assumere indici allineati.
            // Dove il lang non copre il tempo target (fuori range o troppo distante) marca come low SSIM.
            double[] ssimValues = new double[maxIdx];
            for (int i = 0; i < maxIdx; i++)
            {
                srcRelMs = sourceTimestampsMs[i] - sourceTimestampsMs[0];
                targetLangMs = langTimestampsMs[0] + srcRelMs;
                nearestIdx = NearestTimestampIndex(langTimestampsMs, targetLangMs);
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
                        ssimValues[i] = this.ComputeSsim(srcFrames[i], langFrames[nearestIdx]);
                    }
                }
            }

            // Cerca cluster di frame con SSIM sotto soglia restrittiva
            // (usa this._verifyDipSsimThreshold, molto piu' bassa di this._denseScanSsimThreshold,
            // per evitare falsi positivi da scene cuts e compressione)
            for (int i = 0; i < maxIdx; i++)
            {
                if (ssimValues[i] < this._verifyDipSsimThreshold)
                {
                    if (consecutiveLow == 0) { dipStartIdx = i; }
                    consecutiveLow++;

                    if (consecutiveLow >= this._denseScanMinDipFrames)
                    {
                        // Posizione reale del dip dal pts del frame sorgente (non da fps assunto)
                        double dipSrc = sourceTimestampsMs[dipStartIdx] / 1000.0;
                        dips.Add(dipSrc);

                        // Salta avanti per non trovare lo stesso dip piu' volte
                        while (i < maxIdx && ssimValues[i] < this._verifyDipSsimThreshold) { i++; }
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
        /// Determina il nuovo offset dopo un dip tramite probing multi-punto.
        /// Testa 3 punti distanziati (5s, 15s, 25s dopo il dip) per eliminare
        /// falsi positivi da scene cuts: un cambio reale produce lo stesso delta
        /// vincente in tutti i punti, un falso positivo no
        /// </summary>
        /// <param name="sourceFile">Percorso file source</param>
        /// <param name="langFile">Percorso file lang</param>
        /// <param name="dipSrcSec">Posizione source del dip in secondi</param>
        /// <param name="currentOffsetMs">Offset corrente in millisecondi</param>
        /// <param name="inverseRatio">Rapporto inverso stretch</param>
        /// <returns>Nuovo offset in ms, oppure currentOffsetMs se probing fallito</returns>
        private double ProbeOffsetAfterDip(string sourceFile, string langFile, double dipSrcSec, double currentOffsetMs, double inverseRatio)
        {
            double result = currentOffsetMs;
            int numPoints = this._probeMultiMarginsSec.Length;
            int[] winningDeltas = new int[numPoints];
            double[] winningSsims = new double[numPoints];
            int validPoints = 0;
            double probePointSrc = 0.0;
            double bestSsim = 0.0;
            int bestDelta = 0;
            double candidateOffsetMs = 0.0;
            double candidateOffsetSec = 0.0;
            double langPos = 0.0;
            double avgSsim = 0.0;
            List<byte[]> preFilterSrc = null;
            double[] preFilterSrcTs = null;
            List<byte[]> preFilterLang = null;
            double[] preFilterLangTs = null;
            List<byte[]> srcFrames = null;
            double[] srcFramesTs = null;
            List<byte[]> langFrames = null;
            double[] langFramesTs = null;

            // Pre-filtro veloce: testa l'offset corrente al primo punto di probe
            // Se SSIM >= soglia, l'offset corrente funziona ancora => falso positivo
            probePointSrc = dipSrcSec + this._probeMultiMarginsSec[0];
            this.ExtractSegment(sourceFile, (int)(probePointSrc * 1000), this._offsetProbeDurationSec, 0.0, this._cropSourceTo43, out preFilterSrc, out preFilterSrcTs);
            if (preFilterSrc.Count >= 2)
            {
                double currentOffsetSec = currentOffsetMs / 1000.0;
                langPos = probePointSrc - currentOffsetSec;
                if (Math.Abs(inverseRatio - 1.0) > 0.0001) { langPos = langPos * inverseRatio; }

                if (langPos >= 0.0)
                {
                    this.ExtractSegment(langFile, (int)(langPos * 1000), this._offsetProbeDurationSec, 0.0, this._cropLangTo43, out preFilterLang, out preFilterLangTs);
                    if (preFilterLang.Count >= 2)
                    {
                        avgSsim = this.ComputeTimestampMatchedSsim(preFilterSrc, preFilterSrcTs, preFilterLang, preFilterLangTs);

                        // Offset corrente funziona ancora: falso positivo, esci subito
                        if (avgSsim >= this._offsetProbeMinSsim) { return result; }
                    }
                }
            }

            // Offset corrente non funziona piu': cerca il nuovo con multi-point
            for (int p = 0; p < numPoints; p++)
            {
                probePointSrc = dipSrcSec + this._probeMultiMarginsSec[p];
                bestSsim = 0.0;
                bestDelta = 0;

                // Estrai frame source al punto di probe
                this.ExtractSegment(sourceFile, (int)(probePointSrc * 1000), this._offsetProbeDurationSec, 0.0, this._cropSourceTo43, out srcFrames, out srcFramesTs);
                if (srcFrames.Count < 2) { continue; }

                // Testa ogni offset candidato
                for (int d = 0; d < this._offsetProbeDeltas.Length; d++)
                {
                    candidateOffsetMs = currentOffsetMs + this._offsetProbeDeltas[d];
                    candidateOffsetSec = candidateOffsetMs / 1000.0;

                    // Posizione lang col candidato
                    langPos = probePointSrc - candidateOffsetSec;
                    if (Math.Abs(inverseRatio - 1.0) > 0.0001) { langPos = langPos * inverseRatio; }
                    if (langPos < 0.0) { continue; }

                    // Estrai frame lang
                    this.ExtractSegment(langFile, (int)(langPos * 1000), this._offsetProbeDurationSec, 0.0, this._cropLangTo43, out langFrames, out langFramesTs);
                    if (langFrames.Count < 2) { continue; }

                    // SSIM medio con matching per tempo relativo (robusto a VFR)
                    avgSsim = this.ComputeTimestampMatchedSsim(srcFrames, srcFramesTs, langFrames, langFramesTs);

                    if (avgSsim > bestSsim)
                    {
                        bestSsim = avgSsim;
                        bestDelta = this._offsetProbeDeltas[d];
                    }
                }

                // Registra risultato per questo punto
                winningDeltas[validPoints] = bestDelta;
                winningSsims[validPoints] = bestSsim;
                validPoints++;
            }

            // Richiede almeno 2 punti validi
            if (validPoints < this._probeMinConsistentPoints) { return result; }

            // Verifica consenso: stesso delta vincente in tutti i punti validi
            // e SSIM >= soglia in tutti
            bool consistent = true;
            int consensusDelta = winningDeltas[0];

            for (int p = 0; p < validPoints; p++)
            {
                if (winningDeltas[p] != consensusDelta) { consistent = false; break; }
                if (winningSsims[p] < this._offsetProbeMinSsim) { consistent = false; break; }
            }

            // Accetta solo se tutti i punti concordano sullo stesso delta
            if (consistent)
            {
                result = currentOffsetMs + consensusDelta;
            }

            return result;
        }

        /// <summary>
        /// Calcola SSIM medio tra due set di frame abbinando per tempo relativo (primo frame src vs primo frame lang).
        /// Per ogni frame source, trova il frame lang il cui tempo relativo differisce di meno rispetto al target.
        /// Robusto a file VFR dove gli indici non sono allineati
        /// </summary>
        /// <param name="srcFrames">Lista frame source</param>
        /// <param name="srcTimestampsMs">Timestamps source in ms</param>
        /// <param name="langFrames">Lista frame lang</param>
        /// <param name="langTimestampsMs">Timestamps lang in ms</param>
        /// <returns>SSIM medio, 0.0 se nessun match valido</returns>
        private double ComputeTimestampMatchedSsim(List<byte[]> srcFrames, double[] srcTimestampsMs, List<byte[]> langFrames, double[] langTimestampsMs)
        {
            double result = 0.0;
            double totalSsim = 0.0;
            int validPairs = 0;
            double srcRelMs = 0.0;
            double targetLangMs = 0.0;
            int nearestIdx = 0;
            double nearestDistMs = 0.0;
            double toleranceMs = 0.0;

            // Tolleranza generosa: mezzo secondo (tipicamente > 10 frame a 23.976fps)
            toleranceMs = 500.0;

            if (srcFrames.Count == 0 || langFrames.Count == 0 || srcTimestampsMs.Length == 0 || langTimestampsMs.Length == 0)
            {
                return result;
            }

            for (int i = 0; i < srcFrames.Count && i < srcTimestampsMs.Length; i++)
            {
                srcRelMs = srcTimestampsMs[i] - srcTimestampsMs[0];
                targetLangMs = langTimestampsMs[0] + srcRelMs;
                nearestIdx = NearestTimestampIndex(langTimestampsMs, targetLangMs);
                if (nearestIdx < 0 || nearestIdx >= langFrames.Count) { continue; }

                nearestDistMs = Math.Abs(langTimestampsMs[nearestIdx] - targetLangMs);
                if (nearestDistMs > toleranceMs) { continue; }

                totalSsim += this.ComputeSsim(srcFrames[i], langFrames[nearestIdx]);
                validPairs++;
            }

            if (validPairs > 0)
            {
                result = totalSsim / validPairs;
            }

            return result;
        }

        #endregion

        #region Metodi privati — Fase 4: Raffinamento transizioni

        /// <summary>
        /// Raffina i punti di transizione tramite scansione densa SSIM.
        /// Per ogni transizione tra regione R e R+1, cerca il punto esatto dove
        /// il contenuto source passa dal matchare col vecchio offset al nuovo
        /// </summary>
        /// <param name="sourceFile">Percorso file source</param>
        /// <param name="langFile">Percorso file lang</param>
        /// <param name="regions">Regioni con offset grossolani</param>
        /// <param name="inverseRatio">Rapporto inverso stretch</param>
        /// <returns>Lista di EditOperation ordinate per timestamp</returns>
        private List<EditOperation> RefineTransitions(string sourceFile, string langFile, List<OffsetRegion> regions, double inverseRatio)
        {
            List<EditOperation> operations = new List<EditOperation>();
            double oldOffsetSec = 0.0;
            double newOffsetSec = 0.0;
            double bestCrossover = 0.0;
            int durationMs = 0;
            int langTimestampMs = 0;
            int sourceTimestampMs = 0;
            string operationType = "";

            for (int r = 0; r < regions.Count - 1; r++)
            {
                oldOffsetSec = regions[r].OffsetMs / 1000.0;
                newOffsetSec = regions[r + 1].OffsetMs / 1000.0;

                // Finestra di ricerca: regione corrente + margine nella regione successiva
                // Il margine alla fine serve perche' il crossover puo' essere al bordo esatto
                double searchStartSrc = regions[r].StartSrcSec;
                double searchEndSrc = Math.Min(regions[r].EndSrcSec + 10.0, regions[r + 1].EndSrcSec);

                // Per regioni dopo la prima, salta i primi 10s per evitare la zona
                // di transizione precedente (spillover SSIM basso)
                if (r > 0) { searchStartSrc = searchStartSrc + 10.0; }

                // Non partire dal secondo 0 per la prima regione
                if (searchStartSrc < 5.0) { searchStartSrc = 5.0; }

                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Transizione " + (r + 1) + ": scansione densa in src " + searchStartSrc.ToString("F1", CultureInfo.InvariantCulture) + "-" + searchEndSrc.ToString("F1", CultureInfo.InvariantCulture) + "s (offset " + ((int)(oldOffsetSec * 1000)).ToString() + " -> " + ((int)(newOffsetSec * 1000)).ToString() + "ms)");

                // Scansione densa: cerca primo dip SSIM nell'intera regione
                bestCrossover = this.DenseScanCrossover(sourceFile, langFile, searchStartSrc, searchEndSrc, oldOffsetSec, inverseRatio);

                // Scansione lineare di conferma attorno al punto trovato
                bestCrossover = this.LinearScanConfirm(sourceFile, langFile, bestCrossover, oldOffsetSec, newOffsetSec, inverseRatio);

                // Classifica operazione
                durationMs = (int)Math.Abs(Math.Round((newOffsetSec - oldOffsetSec) * 1000.0));
                sourceTimestampMs = (int)Math.Round(bestCrossover * 1000.0);

                // Calcola timestamp lang
                langTimestampMs = (int)Math.Round((bestCrossover - oldOffsetSec) * 1000.0);
                if (Math.Abs(inverseRatio - 1.0) > 0.0001)
                {
                    langTimestampMs = (int)Math.Round(langTimestampMs * inverseRatio);
                }

                if (newOffsetSec > oldOffsetSec)
                {
                    // Source ha contenuto extra
                    operationType = EditOperation.INSERT_SILENCE;
                }
                else
                {
                    // Lang ha contenuto extra
                    operationType = EditOperation.CUT_SEGMENT;
                }

                EditOperation op = new EditOperation();
                op.Type = operationType;
                op.LangTimestampMs = langTimestampMs;
                op.DurationMs = durationMs;
                op.SourceTimestampMs = sourceTimestampMs;
                operations.Add(op);

                ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Transizione " + (r + 1) + ": " + operationType + " @ lang " + (langTimestampMs / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + "s, durata " + durationMs + "ms (crossover src " + bestCrossover.ToString("F2", CultureInfo.InvariantCulture) + "s)");
            }

            return operations;
        }

        /// <summary>
        /// Scansione densa del punto di transizione.
        /// Estrae frame a fps fisso per l'intera regione e cerca il primo cluster
        /// di frame dove SSIM con old offset scende sotto soglia.
        /// Robusto: non assume monotonicita' SSIM, trova dip anche in scene lente
        /// </summary>
        /// <param name="sourceFile">Percorso file source</param>
        /// <param name="langFile">Percorso file lang</param>
        /// <param name="searchStartSrc">Inizio finestra source in secondi</param>
        /// <param name="searchEndSrc">Fine finestra source in secondi</param>
        /// <param name="oldOffsetSec">Offset vecchio in secondi</param>
        /// <param name="inverseRatio">Rapporto inverso stretch</param>
        /// <returns>Timestamp source approssimato della transizione</returns>
        private double DenseScanCrossover(string sourceFile, string langFile, double searchStartSrc, double searchEndSrc, double oldOffsetSec, double inverseRatio)
        {
            double result = (searchStartSrc + searchEndSrc) / 2.0;
            double duration = searchEndSrc - searchStartSrc;
            double langStartOld = 0.0;
            int maxIdx = 0;
            int consecutiveLow = 0;
            int dipStartIdx = -1;
            double minSsim = double.MaxValue;
            int minIdx = 0;
            List<byte[]> srcFrames = null;
            double[] sourceTimestampsMs = null;
            List<byte[]> langOldFrames = null;
            double[] langTimestampsMs = null;
            double frameIntervalMs = 1000.0 / this._denseScanFps;
            double toleranceMs = frameIntervalMs * 2.0;
            double srcRelMs = 0.0;
            double targetLangMs = 0.0;
            int nearestIdx = 0;
            double nearestDistMs = 0.0;

            // Estrai frame source a fps fisso per l'intera regione
            this.ExtractSegment(sourceFile, (int)(searchStartSrc * 1000), duration, this._denseScanFps, this._cropSourceTo43, out srcFrames, out sourceTimestampsMs);
            if (srcFrames.Count < 4)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    DenseScan: estrazione source fallita (" + srcFrames.Count + " frame su " + duration.ToString("F0", CultureInfo.InvariantCulture) + "s attesi)");
                return result;
            }

            // Posizione lang col vecchio offset
            langStartOld = searchStartSrc - oldOffsetSec;
            if (Math.Abs(inverseRatio - 1.0) > 0.0001) { langStartOld = langStartOld * inverseRatio; }
            if (langStartOld < 0.0) { langStartOld = 0.0; }

            // Estrai frame lang col vecchio offset alla stessa fps
            this.ExtractSegment(langFile, (int)(langStartOld * 1000), duration, this._denseScanFps, this._cropLangTo43, out langOldFrames, out langTimestampsMs);

            if (langOldFrames.Count < 4 || langTimestampsMs.Length < 4)
            {
                ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    DenseScan: estrazione lang fallita (" + langOldFrames.Count + " frame su " + duration.ToString("F0", CultureInfo.InvariantCulture) + "s attesi)");
                return result;
            }

            maxIdx = srcFrames.Count;
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione densa: " + maxIdx + " frame a " + this._denseScanFps.ToString("F0", CultureInfo.InvariantCulture) + "fps su " + duration.ToString("F0", CultureInfo.InvariantCulture) + "s");

            // Calcola SSIM abbinando ogni frame source al frame lang piu' vicino per tempo relativo
            // (robusto a VFR: usa timestamps reali da showinfo invece di assumere indici allineati)
            double[] ssimValues = new double[maxIdx];
            for (int i = 0; i < maxIdx; i++)
            {
                srcRelMs = sourceTimestampsMs[i] - sourceTimestampsMs[0];
                targetLangMs = langTimestampsMs[0] + srcRelMs;
                nearestIdx = NearestTimestampIndex(langTimestampsMs, targetLangMs);
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
                        ssimValues[i] = this.ComputeSsim(srcFrames[i], langOldFrames[nearestIdx]);
                    }
                }
            }

            // Cerca primo cluster di frame con SSIM sotto soglia
            for (int i = 0; i < maxIdx; i++)
            {
                if (ssimValues[i] < this._denseScanSsimThreshold)
                {
                    if (consecutiveLow == 0) { dipStartIdx = i; }
                    consecutiveLow++;

                    if (consecutiveLow >= this._denseScanMinDipFrames)
                    {
                        // Posizione reale dal pts del frame sorgente (non da fps assunto)
                        result = sourceTimestampsMs[dipStartIdx] / 1000.0;
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione densa: dip a src " + result.ToString("F1", CultureInfo.InvariantCulture) + "s (frame " + dipStartIdx + "/" + maxIdx + ", " + consecutiveLow + " frame SSIM<" + this._denseScanSsimThreshold.ToString("F1", CultureInfo.InvariantCulture) + ")");
                        return result;
                    }
                }
                else
                {
                    consecutiveLow = 0;
                    dipStartIdx = -1;
                }
            }

            // Fallback: nessun dip netto, usa punto con SSIM minimo
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
        /// Scansione lineare differenziale di conferma attorno al punto trovato dalla binaria.
        /// Cerca il primo frame dove SSIM col nuovo offset supera SSIM col vecchio offset
        /// per almeno this._linearScanConfirmFrames consecutivi
        /// </summary>
        /// <param name="sourceFile">Percorso file source</param>
        /// <param name="langFile">Percorso file lang</param>
        /// <param name="approximateSrc">Punto approssimato dalla binaria in secondi source</param>
        /// <param name="oldOffsetSec">Offset vecchio in secondi</param>
        /// <param name="newOffsetSec">Offset nuovo in secondi</param>
        /// <param name="inverseRatio">Rapporto inverso stretch</param>
        /// <returns>Timestamp source raffinato del crossover in secondi</returns>
        private double LinearScanConfirm(string sourceFile, string langFile, double approximateSrc, double oldOffsetSec, double newOffsetSec, double inverseRatio)
        {
            double result = approximateSrc;
            double scanStart = approximateSrc - this._linearScanWindowSec;
            if (scanStart < 0.0) { scanStart = 0.0; }
            double scanDuration = this._linearScanWindowSec * 2.0;
            List<byte[]> srcFrames = null;
            double[] sourceTimestampsMs = null;
            List<byte[]> langOldFrames = null;
            double[] langOldTimestampsMs = null;
            List<byte[]> langNewFrames = null;
            double[] langNewTimestampsMs = null;
            double toleranceMs = 0.0;
            double srcRelMs = 0.0;
            double targetLangOldMs = 0.0;
            int nearestOldIdx = 0;
            double nearestOldDistMs = 0.0;
            double ssimOld = 0.0;

            // Estrai frame source a fps nativo nella finestra (passthrough)
            this.ExtractSegment(sourceFile, (int)(scanStart * 1000), scanDuration, 0.0, this._cropSourceTo43, out srcFrames, out sourceTimestampsMs);
            if (srcFrames.Count < 4 || sourceTimestampsMs.Length < 4) { return result; }

            // Posizione lang col vecchio offset
            double langStartOld = scanStart - oldOffsetSec;
            if (Math.Abs(inverseRatio - 1.0) > 0.0001) { langStartOld = langStartOld * inverseRatio; }
            if (langStartOld < 0.0) { langStartOld = 0.0; }

            // Posizione lang col nuovo offset
            double langStartNew = scanStart - newOffsetSec;
            if (Math.Abs(inverseRatio - 1.0) > 0.0001) { langStartNew = langStartNew * inverseRatio; }
            if (langStartNew < 0.0) { langStartNew = 0.0; }

            // Estrai frame lang con entrambi gli offset (passthrough)
            this.ExtractSegment(langFile, (int)(langStartOld * 1000), scanDuration, 0.0, this._cropLangTo43, out langOldFrames, out langOldTimestampsMs);
            this.ExtractSegment(langFile, (int)(langStartNew * 1000), scanDuration, 0.0, this._cropLangTo43, out langNewFrames, out langNewTimestampsMs);

            if (langOldFrames.Count < 4 || langOldTimestampsMs.Length < 4) { return result; }

            int maxIdx = srcFrames.Count;

            // Tolleranza ampia: scanDuration lo fps nativo puo' variare; uso 100ms
            toleranceMs = 100.0;

            // Scorri e trova il primo frame dove old offset smette di funzionare
            // (SSIM_old < 0.5) per almeno this._linearScanConfirmFrames consecutivi.
            // Matching per tempo relativo: robusto a VFR
            int consecutiveBad = 0;
            int crossoverIdx = -1;

            for (int i = 0; i < maxIdx; i++)
            {
                srcRelMs = sourceTimestampsMs[i] - sourceTimestampsMs[0];
                targetLangOldMs = langOldTimestampsMs[0] + srcRelMs;
                nearestOldIdx = NearestTimestampIndex(langOldTimestampsMs, targetLangOldMs);
                if (nearestOldIdx < 0 || nearestOldIdx >= langOldFrames.Count) { continue; }
                nearestOldDistMs = Math.Abs(langOldTimestampsMs[nearestOldIdx] - targetLangOldMs);
                if (nearestOldDistMs > toleranceMs) { continue; }

                ssimOld = this.ComputeSsim(srcFrames[i], langOldFrames[nearestOldIdx]);

                if (ssimOld < 0.5)
                {
                    if (consecutiveBad == 0)
                    {
                        crossoverIdx = i;
                    }
                    consecutiveBad++;

                    if (consecutiveBad >= this._linearScanConfirmFrames)
                    {
                        // Posizione reale dal pts del frame sorgente
                        result = sourceTimestampsMs[crossoverIdx] / 1000.0;
                        ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "    Scansione lineare: crossover confermato a src " + result.ToString("F2", CultureInfo.InvariantCulture) + "s (" + consecutiveBad + " frame old<0.5)");
                        return result;
                    }
                }
                else
                {
                    consecutiveBad = 0;
                    crossoverIdx = -1;
                }
            }

            // Se non confermato, usa il punto della binaria
            ConsoleHelper.Write(LogSection.Deep, LogLevel.Warning, "    Scansione lineare: conferma non raggiunta, uso punto binaria");
            return result;
        }

        #endregion

        #region Metodi privati — Fase 5: Verifica globale

        /// <summary>
        /// Verifica l'allineamento globale dopo aver applicato le regioni e le operazioni
        /// </summary>
        /// <param name="sourceFile">Percorso file source</param>
        /// <param name="langFile">Percorso file lang</param>
        /// <param name="regions">Regioni con offset</param>
        /// <param name="operations">Operazioni di edit</param>
        /// <param name="inverseRatio">Rapporto inverso stretch</param>
        /// <param name="sourceDurationMs">Durata source in ms</param>
        /// <param name="baselineMse">MSE baseline calcolato (output)</param>
        /// <returns>True se la verifica ha successo</returns>
        private bool VerifyGlobal(string sourceFile, string langFile, List<OffsetRegion> regions, List<EditOperation> operations, double inverseRatio, int sourceDurationMs, out double baselineMse)
        {
            bool verified = false;
            int validPoints = 0;
            double totalMse = 0.0;
            int pointsChecked = 0;
            double stepMs = 0.0;
            double srcPointMs = 0.0;
            double offsetSec = 0.0;
            double langPointMs = 0.0;
            List<byte[]> srcFrames = null;
            double[] srcFramesTs = null;
            List<byte[]> langFrames = null;
            double[] langFramesTs = null;
            double mse = 0.0;
            double maxMse = 0.0;
            double dynamicThreshold = 0.0;
            List<double> allMse = new List<double>();
            double srcRelMs = 0.0;
            double targetLangMs = 0.0;
            int nearestIdx = 0;
            double nearestDistMs = 0.0;
            double toleranceMs = 0.0;

            baselineMse = 0.0;

            // Calcola step tra punti di verifica
            stepMs = sourceDurationMs / (double)(this._globalVerifyPoints + 1);

            // Tolleranza matching: 2 intervalli di frame a coarse fps
            toleranceMs = (1000.0 / this._coarseFps) * 2.0;

            // Prima passata: raccolta MSE di tutti i punti
            for (int p = 1; p <= this._globalVerifyPoints; p++)
            {
                srcPointMs = stepMs * p;

                // Trova offset per questa posizione
                offsetSec = this.GetOffsetForPosition(regions, srcPointMs / 1000.0);

                // Calcola posizione lang corrispondente
                langPointMs = srcPointMs - (offsetSec * 1000.0);
                if (Math.Abs(inverseRatio - 1.0) > 0.0001)
                {
                    langPointMs = langPointMs * inverseRatio;
                }

                if (langPointMs < 0.0) { continue; }

                // Estrai pochi frame per confronto rapido con timestamps reali
                this.ExtractSegment(sourceFile, (int)srcPointMs, 2, this._coarseFps, this._cropSourceTo43, out srcFrames, out srcFramesTs);
                this.ExtractSegment(langFile, (int)langPointMs, 2, this._coarseFps, this._cropLangTo43, out langFrames, out langFramesTs);

                if (srcFrames.Count > 0 && langFrames.Count > 0 && srcFramesTs.Length > 0 && langFramesTs.Length > 0)
                {
                    // Minimo MSE tra coppie di frame abbinate per tempo relativo.
                    // Robusto a VFR: evita il match indice-based che decimerebbe arbitrariamente i frame
                    mse = double.MaxValue;
                    for (int vf = 0; vf < srcFrames.Count && vf < srcFramesTs.Length; vf++)
                    {
                        srcRelMs = srcFramesTs[vf] - srcFramesTs[0];
                        targetLangMs = langFramesTs[0] + srcRelMs;
                        nearestIdx = NearestTimestampIndex(langFramesTs, targetLangMs);
                        if (nearestIdx < 0 || nearestIdx >= langFrames.Count) { continue; }
                        nearestDistMs = Math.Abs(langFramesTs[nearestIdx] - targetLangMs);
                        if (nearestDistMs > toleranceMs) { continue; }

                        double vMse = this.ComputeMse(srcFrames[vf], langFrames[nearestIdx]);
                        if (vMse < mse) { mse = vMse; }
                    }

                    if (mse < double.MaxValue)
                    {
                        allMse.Add(mse);
                        totalMse += mse;

                        if (mse > maxMse) { maxMse = mse; }
                    }
                }
            }

            pointsChecked = allMse.Count;

            if (pointsChecked > 0)
            {
                baselineMse = totalMse / pointsChecked;
            }

            // Soglia dinamica: baseline * moltiplicatore, con floor a this._mseThreshold
            dynamicThreshold = baselineMse * this._verifyMseMultiplier;
            if (dynamicThreshold < this._mseThreshold)
            {
                dynamicThreshold = this._mseThreshold;
            }

            // Seconda passata: conta punti validi con soglia dinamica
            for (int i = 0; i < allMse.Count; i++)
            {
                if (allMse[i] < dynamicThreshold)
                {
                    validPoints++;
                }
            }

            // Verifica: almeno 80% dei punti devono essere sotto soglia dinamica
            double ratio = (pointsChecked > 0) ? (double)validPoints / pointsChecked : 0.0;
            verified = (ratio >= this._globalVerifyMinRatio);

            ConsoleHelper.Write(LogSection.Deep, LogLevel.Debug, "  Verifica: " + validPoints + "/" + pointsChecked + " punti OK (MSE baseline=" + baselineMse.ToString("F1", CultureInfo.InvariantCulture) + ", soglia=" + dynamicThreshold.ToString("F1", CultureInfo.InvariantCulture) + ", max=" + maxMse.ToString("F1", CultureInfo.InvariantCulture) + ")");

            return verified;
        }

        /// <summary>
        /// Determina l'offset corretto per una posizione source data
        /// </summary>
        /// <param name="regions">Lista regioni ordinate</param>
        /// <param name="srcSec">Posizione source in secondi</param>
        /// <returns>Offset in secondi per questa posizione</returns>
        private double GetOffsetForPosition(List<OffsetRegion> regions, double srcSec)
        {
            double offsetSec = 0.0;

            // Trova regione corrispondente (l'ultima con StartSrcSec <= srcSec)
            for (int r = regions.Count - 1; r >= 0; r--)
            {
                if (regions[r].StartSrcSec <= srcSec)
                {
                    offsetSec = regions[r].OffsetMs / 1000.0;
                    break;
                }
            }

            return offsetSec;
        }

        /// <summary>
        /// Calcola la mediana di una lista di valori double
        /// </summary>
        /// <param name="values">Lista valori</param>
        /// <returns>Mediana</returns>
        private double ComputeMedian(List<double> values)
        {
            double result = 0.0;
            List<double> sorted = new List<double>(values);
            int count = sorted.Count;

            sorted.Sort();

            if (count % 2 == 1)
            {
                result = sorted[count / 2];
            }
            else
            {
                result = (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;
            }

            return result;
        }

        #endregion

        #region Classi interne

        /// <summary>
        /// Regione con offset costante tra source e lang
        /// </summary>
        private class OffsetRegion
        {
            /// <summary>
            /// Inizio regione nel source in secondi
            /// </summary>
            public double StartSrcSec { get; set; }

            /// <summary>
            /// Fine regione nel source in secondi
            /// </summary>
            public double EndSrcSec { get; set; }

            /// <summary>
            /// Offset in millisecondi (source - lang)
            /// </summary>
            public double OffsetMs { get; set; }

            /// <summary>
            /// Numero di scene cuts matchati in questa regione
            /// </summary>
            public int MatchCount { get; set; }
        }

        #endregion
    }
}
