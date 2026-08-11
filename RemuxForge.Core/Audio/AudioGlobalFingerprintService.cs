using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;

namespace RemuxForge.Core.Audio
{
    /// <summary>
    /// Fingerprint audio globale basata su envelope/silenzi/onset
    /// Produce candidati da verificare poi con il video
    /// </summary>
    public class AudioGlobalFingerprintService
    {
        #region Costanti

        /// <summary>
        /// Numero massimo fingerprint audio mantenuti in cache per processo
        /// </summary>
        private const int MAX_FINGERPRINT_CACHE = 16;

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Lock cache fingerprint audio
        /// </summary>
        private static readonly object s_fingerprintCacheLock = new object();

        /// <summary>
        /// Cache fingerprint audio bounded
        /// </summary>
        private static readonly List<CachedAudioFingerprint> s_fingerprintCache = new List<CachedAudioFingerprint>();

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso ffmpeg
        /// </summary>
        private readonly string _ffmpegPath;

        /// <summary>
        /// Configurazione FrameSync
        /// </summary>
        private readonly FrameSyncConfig _config;

        /// <summary>
        /// Configurazione ffmpeg
        /// </summary>
        private readonly FfmpegConfig _ffmpegConfig;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso ffmpeg</param>
        /// <param name="config">Configurazione FrameSync</param>
        /// <param name="ffmpegConfig">Configurazione ffmpeg</param>
        public AudioGlobalFingerprintService(string ffmpegPath, FrameSyncConfig config, FfmpegConfig ffmpegConfig)
        {
            this._ffmpegPath = ffmpegPath;
            this._config = config;
            this._ffmpegConfig = ffmpegConfig;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Trova offset audio globale tra source e language
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="languageFile">File lingua</param>
        /// <returns>Risultato audio globale</returns>
        public AudioGlobalFingerprintResult FindOffset(string sourceFile, string languageFile)
        {
            AudioGlobalFingerprintResult result = new AudioGlobalFingerprintResult();
            Stopwatch stopwatch = new Stopwatch();
            Stopwatch extractionStopwatch = new Stopwatch();
            Stopwatch correlationStopwatch = new Stopwatch();
            AudioFingerprint source;
            AudioFingerprint language;
            AudioOffsetScore best = new AudioOffsetScore();
            AudioOffsetScore second = new AudioOffsetScore();
            AudioFingerprint[] sourceResult = new AudioFingerprint[1];
            AudioFingerprint[] languageResult = new AudioFingerprint[1];
            bool[] sourceCacheHit = new bool[1];
            bool[] languageCacheHit = new bool[1];
            AudioOffsetScore[] workerBestScores;
            AudioOffsetScore[] workerSecondScores;
            int rangeMs = this._config.AudioGlobalSearchRangeMs;
            int stepMs = this._config.AudioGlobalCoarseStepMs;
            int offsetCount;
            int threadCount = Environment.ProcessorCount;
            Thread[] workers;
            stopwatch.Start();
            result.WindowMs = this._config.AudioGlobalWindowMs;

            extractionStopwatch.Start();
            Thread sourceThread = new Thread(() =>
            {
                bool cacheHit;
                sourceResult[0] = this.GetFingerprintCached(sourceFile, out cacheHit);
                sourceCacheHit[0] = cacheHit;
            });
            Thread languageThread = new Thread(() =>
            {
                bool cacheHit;
                languageResult[0] = this.GetFingerprintCached(languageFile, out cacheHit);
                languageCacheHit[0] = cacheHit;
            });
            sourceThread.Start();
            languageThread.Start();
            sourceThread.Join();
            languageThread.Join();
            extractionStopwatch.Stop();
            result.ExtractionMs = extractionStopwatch.ElapsedMilliseconds;
            source = sourceResult[0];
            language = languageResult[0];
            result.SourceCacheHit = sourceCacheHit[0];
            result.LanguageCacheHit = languageCacheHit[0];

            if (source == null || language == null || source.Envelope.Length < 20 || language.Envelope.Length < 20)
            {
                result.FailureReason = "Fingerprint audio insufficiente";
                stopwatch.Stop();
                result.TimingMs = stopwatch.ElapsedMilliseconds;
                return result;
            }

            correlationStopwatch.Start();

            if (stepMs < result.WindowMs)
            {
                stepMs = result.WindowMs;
            }

            offsetCount = ((rangeMs * 2) / stepMs) + 1;
            result.CandidateCount = offsetCount;

            if (threadCount > offsetCount)
            {
                threadCount = offsetCount;
            }
            if (threadCount < 1)
            {
                threadCount = 1;
            }

            workers = new Thread[threadCount];
            workerBestScores = new AudioOffsetScore[threadCount];
            workerSecondScores = new AudioOffsetScore[threadCount];
            for (int w = 0; w < threadCount; w++)
            {
                int workerIndex = w;
                workers[w] = new Thread(() =>
                {
                    AudioOffsetScore localBest = new AudioOffsetScore();
                    AudioOffsetScore localSecond = new AudioOffsetScore();

                    for (int offsetIndex = workerIndex; offsetIndex < offsetCount; offsetIndex += threadCount)
                    {
                        int offsetMs = -rangeMs + (offsetIndex * stepMs);
                        AudioOffsetScore score = this.ScoreOffset(source, language, offsetMs);
                        this.TrackCandidate(score, ref localBest, ref localSecond, stepMs * 2);
                    }

                    workerBestScores[workerIndex] = localBest;
                    workerSecondScores[workerIndex] = localSecond;
                });
                workers[w].Start();
            }

            for (int w = 0; w < threadCount; w++)
            {
                workers[w].Join();
            }

            for (int w = 0; w < threadCount; w++)
            {
                this.TrackCandidate(workerBestScores[w], ref best, ref second, stepMs * 2);
                this.TrackCandidate(workerSecondScores[w], ref best, ref second, stepMs * 2);
            }

            this.RefineBest(source, language, ref best, ref second);
            correlationStopwatch.Stop();

            result.OffsetMs = best.OffsetMs;
            result.Score = best.Score;
            result.Margin = best.Score - second.Score;
            result.Coverage = best.Coverage;
            result.EnvelopeScore = best.EnvelopeScore;
            result.SilenceScore = best.SilenceScore;
            result.OnsetScore = best.OnsetScore;
            result.DerivativeScore = best.DerivativeScore;
            result.SilenceRunScore = best.SilenceRunScore;
            result.ChunkScore = best.ChunkScore;

            if (best.Score >= this._config.AudioGlobalMinScore &&
                result.Margin >= this._config.AudioGlobalMinMargin &&
                best.Coverage >= this._config.AudioGlobalMinCoverage)
            {
                result.Success = true;
            }
            else
            {
                result.FailureReason = "Audio globale non conclusivo";
            }

            stopwatch.Stop();
            result.TimingMs = stopwatch.ElapsedMilliseconds;
            result.CorrelationMs = correlationStopwatch.ElapsedMilliseconds;

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Recupera fingerprint audio da cache o la estrae
        /// </summary>
        private AudioFingerprint GetFingerprintCached(string filePath, out bool cacheHit)
        {
            AudioFingerprint result;
            AudioFingerprintCacheKey key = this.BuildCacheKey(filePath);

            cacheHit = false;

            lock (s_fingerprintCacheLock)
            {
                for (int i = 0; i < s_fingerprintCache.Count; i++)
                {
                    CachedAudioFingerprint cached = s_fingerprintCache[i];
                    if (cached != null && cached.Key != null && cached.Key.EqualsKey(key))
                    {
                        result = cached.Fingerprint;
                        cacheHit = true;
                        return result;
                    }
                }
            }

            result = this.ExtractFingerprint(filePath);
            if (result != null)
            {
                this.StoreFingerprint(key, result);
            }

            return result;
        }

        /// <summary>
        /// Costruisce chiave cache fingerprint
        /// </summary>
        private AudioFingerprintCacheKey BuildCacheKey(string filePath)
        {
            AudioFingerprintCacheKey key = new AudioFingerprintCacheKey();
            FileInfo fileInfo;
            key.FilePath = filePath;
            key.SampleRate = this._config.AudioGlobalSampleRate;
            key.WindowMs = this._config.AudioGlobalWindowMs;

            try
            {
                fileInfo = new FileInfo(filePath);
                if (fileInfo.Exists)
                {
                    key.Length = fileInfo.Length;
                    key.LastWriteUtcTicks = fileInfo.LastWriteTimeUtc.Ticks;
                }
            }
            catch
            {
                key.Length = 0;
                key.LastWriteUtcTicks = 0;
            }

            return key;
        }

        /// <summary>
        /// Salva fingerprint audio in cache bounded
        /// </summary>
        private void StoreFingerprint(AudioFingerprintCacheKey key, AudioFingerprint fingerprint)
        {
            if (key == null || fingerprint == null)
            {
                return;
            }

            lock (s_fingerprintCacheLock)
            {
                for (int i = 0; i < s_fingerprintCache.Count; i++)
                {
                    if (s_fingerprintCache[i].Key.EqualsKey(key))
                    {
                        return;
                    }
                }

                while (s_fingerprintCache.Count >= MAX_FINGERPRINT_CACHE)
                {
                    s_fingerprintCache.RemoveAt(0);
                }

                CachedAudioFingerprint cached = new CachedAudioFingerprint();
                cached.Key = key;
                cached.Fingerprint = fingerprint;
                s_fingerprintCache.Add(cached);
            }
        }

        /// <summary>
        /// Raffina il miglior offset attorno al coarse winner
        /// </summary>
        private void RefineBest(AudioFingerprint source, AudioFingerprint language, ref AudioOffsetScore best, ref AudioOffsetScore second)
        {
            int refineStepMs = this._config.AudioGlobalWindowMs;
            int refineRadiusMs = this._config.AudioGlobalCoarseStepMs;

            if (refineStepMs < 10)
            {
                refineStepMs = 10;
            }

            for (int offsetMs = best.OffsetMs - refineRadiusMs; offsetMs <= best.OffsetMs + refineRadiusMs; offsetMs += refineStepMs)
            {
                AudioOffsetScore score = this.ScoreOffset(source, language, offsetMs);
                this.TrackCandidate(score, ref best, ref second, refineStepMs * 2);
            }
        }

        /// <summary>
        /// Estrae fingerprint audio tramite ffmpeg PCM mono
        /// </summary>
        private AudioFingerprint ExtractFingerprint(string filePath)
        {
            AudioFingerprint result = null;
            ProcessBinaryResult processResult;
            List<double> envelope = new List<double>();
            int sampleRate = this._config.AudioGlobalSampleRate;
            int windowSamples;
            int sample;
            int samplesInWindow = 0;
            double sumAbs = 0.0;
            List<string> args = new List<string>();

            if (sampleRate < 1)
            {
                sampleRate = 8000;
            }

            windowSamples = (sampleRate * this._config.AudioGlobalWindowMs) / 1000;
            if (windowSamples < 1)
            {
                windowSamples = 1;
            }

            try
            {
                args.Add("-nostdin");
                args.Add("-hide_banner");
                args.Add("-v");
                args.Add("error");
                if (this._ffmpegConfig != null && this._ffmpegConfig.HardwareAcceleration)
                {
                    args.Add("-hwaccel");
                    args.Add(this._ffmpegConfig.HardwareAccelerationMethod);
                }
                args.Add("-i");
                args.Add(filePath);
                args.Add("-map");
                args.Add("0:a:0");
                args.Add("-vn");
                args.Add("-sn");
                args.Add("-dn");
                args.Add("-ac");
                args.Add("1");
                args.Add("-ar");
                args.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
                args.Add("-f");
                args.Add("s16le");
                args.Add("-");

                processResult = ProcessRunner.RunBinaryStdout(this._ffmpegPath, args.ToArray(), (buffer, bytesRead) =>
                {
                    int usableBytes = bytesRead - (bytesRead % 2);
                    for (int i = 0; i < usableBytes; i += 2)
                    {
                        sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                        sumAbs += Math.Abs(sample) / 32768.0;
                        samplesInWindow++;

                        if (samplesInWindow >= windowSamples)
                        {
                            envelope.Add(sumAbs / samplesInWindow);
                            sumAbs = 0.0;
                            samplesInWindow = 0;
                        }
                    }
                });

                if (samplesInWindow > 0)
                {
                    envelope.Add(sumAbs / samplesInWindow);
                }

                if (processResult.ExitCode == 0 && envelope.Count > 0)
                {
                    result = this.BuildFingerprint(envelope);
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(LogSection.FrameSync, LogLevel.Warning, "  Errore fingerprint audio: " + ex.Message);
            }

            return result;
        }

        /// <summary>
        /// Costruisce feature normalizzate da envelope
        /// </summary>
        private AudioFingerprint BuildFingerprint(List<double> envelope)
        {
            AudioFingerprint result = new AudioFingerprint();
            double[] sorted = envelope.ToArray();
            double silenceThreshold;
            Array.Sort(sorted);
            silenceThreshold = sorted[(int)Math.Round((sorted.Length - 1) * 0.20)];
            silenceThreshold *= 1.35;

            result.Envelope = envelope.ToArray();
            result.Onset = new double[result.Envelope.Length];
            result.Derivative = new double[result.Envelope.Length];
            result.Silence = new byte[result.Envelope.Length];
            result.SilenceRun = new double[result.Envelope.Length];

            this.NormalizeInPlace(result.Envelope);

            for (int i = 0; i < envelope.Count; i++)
            {
                if (envelope[i] <= silenceThreshold)
                {
                    result.Silence[i] = 1;
                }

                if (i > 0)
                {
                    result.Derivative[i] = result.Envelope[i] - result.Envelope[i - 1];
                    result.Onset[i] = Math.Abs(result.Derivative[i]);
                }
            }

            this.NormalizeInPlace(result.Onset);
            this.NormalizeInPlace(result.Derivative);
            this.BuildSilenceRunFeature(result.Silence, result.SilenceRun);
            this.NormalizeInPlace(result.SilenceRun);

            return result;
        }

        /// <summary>
        /// Costruisce una feature run-length dei silenzi: valori alti al centro di run silenziose lunghe
        /// </summary>
        private void BuildSilenceRunFeature(byte[] silence, double[] runFeature)
        {
            int i = 0;
            while (i < silence.Length)
            {
                if (silence[i] == 0)
                {
                    runFeature[i] = 0.0;
                    i++;
                    continue;
                }

                int start = i;
                while (i < silence.Length && silence[i] == 1)
                {
                    i++;
                }

                int end = i - 1;
                int length = end - start + 1;
                for (int p = start; p <= end; p++)
                {
                    int left = p - start + 1;
                    int right = end - p + 1;
                    int distance = left < right ? left : right;
                    runFeature[p] = length + (distance * 0.25);
                }
            }
        }

        /// <summary>
        /// Normalizza un array con z-score e clipping
        /// </summary>
        private void NormalizeInPlace(double[] values)
        {
            double sum = 0.0;
            double variance = 0.0;
            double mean;
            double std;

            if (values == null || values.Length == 0)
            {
                return;
            }

            for (int i = 0; i < values.Length; i++)
            {
                sum += values[i];
            }
            mean = sum / values.Length;

            for (int i = 0; i < values.Length; i++)
            {
                double diff = values[i] - mean;
                variance += diff * diff;
            }
            std = Math.Sqrt(variance / values.Length);
            if (std < 0.000001)
            {
                std = 1.0;
            }

            for (int i = 0; i < values.Length; i++)
            {
                double value = (values[i] - mean) / std;
                if (value > 3.0) { value = 3.0; }
                if (value < -3.0) { value = -3.0; }
                values[i] = value;
            }
        }

        /// <summary>
        /// Calcola score per offset interno langTime-sourceTime
        /// </summary>
        private AudioOffsetScore ScoreOffset(AudioFingerprint source, AudioFingerprint language, int offsetMs)
        {
            AudioOffsetScore result = new AudioOffsetScore();
            int shift = (int)Math.Round(offsetMs / (double)this._config.AudioGlobalWindowMs);
            int sourceStart = 0;
            int langStart = shift;
            int count;
            double envelopeScore;
            double onsetScore;
            double silenceScore;
            double derivativeScore;
            double silenceRunScore;
            double chunkScore = 0.0;
            double legacyScore;
            double enhancedScore;
            double preliminaryScore;
            result.OffsetMs = offsetMs;

            if (langStart < 0)
            {
                sourceStart = -langStart;
                langStart = 0;
            }

            count = Math.Min(source.Envelope.Length - sourceStart, language.Envelope.Length - langStart);
            if (count <= 10)
            {
                return result;
            }

            envelopeScore = this.ComputeCorrelation(source.Envelope, sourceStart, language.Envelope, langStart, count);
            onsetScore = this.ComputeCorrelation(source.Onset, sourceStart, language.Onset, langStart, count);
            derivativeScore = this.ComputeCorrelation(source.Derivative, sourceStart, language.Derivative, langStart, count);
            silenceScore = this.ComputeSilenceScore(source.Silence, sourceStart, language.Silence, langStart, count);
            silenceRunScore = this.ComputeCorrelation(source.SilenceRun, sourceStart, language.SilenceRun, langStart, count);
            if (envelopeScore < 0.0) { envelopeScore = 0.0; }
            if (onsetScore < 0.0) { onsetScore = 0.0; }
            if (derivativeScore < 0.0) { derivativeScore = 0.0; }
            if (silenceRunScore < 0.0) { silenceRunScore = 0.0; }

            legacyScore = (envelopeScore * 0.40) + (onsetScore * 0.25) + (silenceScore * 0.35);
            enhancedScore = (envelopeScore * 0.32) + (onsetScore * 0.20) + (silenceScore * 0.28) + (derivativeScore * 0.08) + (silenceRunScore * 0.06);
            preliminaryScore = (legacyScore * 0.80) + (enhancedScore * 0.20);
            if (preliminaryScore >= 0.45)
            {
                chunkScore = this.ComputeChunkScore(source, sourceStart, language, langStart, count);
                if (chunkScore < 0.0) { chunkScore = 0.0; }
            }

            result.EnvelopeScore = envelopeScore;
            result.OnsetScore = onsetScore;
            result.SilenceScore = silenceScore;
            result.DerivativeScore = derivativeScore;
            result.SilenceRunScore = silenceRunScore;
            result.ChunkScore = chunkScore;
            result.Coverage = count / (double)Math.Min(source.Envelope.Length, language.Envelope.Length);
            enhancedScore = (envelopeScore * 0.32) + (onsetScore * 0.20) + (silenceScore * 0.28) + (derivativeScore * 0.08) + (silenceRunScore * 0.06) + (chunkScore * 0.06);
            result.Score = (legacyScore * 0.80) + (enhancedScore * 0.20);

            return result;
        }

        /// <summary>
        /// Calcola score distribuito su chunk temporali per evitare che un solo blocco domini il risultato
        /// </summary>
        private double ComputeChunkScore(AudioFingerprint source, int sourceStart, AudioFingerprint language, int langStart, int count)
        {
            int chunkCount = 8;
            int minChunkSize = 20;
            int usefulChunks = 0;
            int weakChunks = 0;
            double total = 0.0;
            double[] scores = new double[chunkCount];

            if (count < chunkCount * minChunkSize)
            {
                return 0.0;
            }

            for (int c = 0; c < chunkCount; c++)
            {
                int start = (count * c) / chunkCount;
                int end = (count * (c + 1)) / chunkCount;
                int length = end - start;
                if (length < minChunkSize)
                {
                    continue;
                }

                double envelope = this.ComputeCorrelation(source.Envelope, sourceStart + start, language.Envelope, langStart + start, length);
                double onset = this.ComputeCorrelation(source.Onset, sourceStart + start, language.Onset, langStart + start, length);
                double derivative = this.ComputeCorrelation(source.Derivative, sourceStart + start, language.Derivative, langStart + start, length);
                double silence = this.ComputeSilenceScore(source.Silence, sourceStart + start, language.Silence, langStart + start, length);
                double silenceRun = this.ComputeCorrelation(source.SilenceRun, sourceStart + start, language.SilenceRun, langStart + start, length);
                double chunk;
                if (envelope < 0.0) { envelope = 0.0; }
                if (onset < 0.0) { onset = 0.0; }
                if (derivative < 0.0) { derivative = 0.0; }
                if (silenceRun < 0.0) { silenceRun = 0.0; }

                chunk = (envelope * 0.34) + (onset * 0.18) + (silence * 0.28) + (derivative * 0.12) + (silenceRun * 0.08);
                scores[usefulChunks] = chunk;
                total += chunk;
                if (chunk < 0.45)
                {
                    weakChunks++;
                }
                usefulChunks++;
            }

            if (usefulChunks == 0)
            {
                return 0.0;
            }

            Array.Sort(scores, 0, usefulChunks);

            double average = total / usefulChunks;
            double lowerQuartile = scores[usefulChunks / 4];
            double penalty = 1.0 - (weakChunks / (double)usefulChunks * 0.25);
            if (penalty < 0.70)
            {
                penalty = 0.70;
            }

            return ((average * 0.70) + (lowerQuartile * 0.30)) * penalty;
        }

        /// <summary>
        /// Correlazione Pearson su segmenti
        /// </summary>
        private double ComputeCorrelation(double[] a, int aStart, double[] b, int bStart, int count)
        {
            double sumA = 0.0;
            double sumB = 0.0;
            double sumAA = 0.0;
            double sumBB = 0.0;
            double sumAB = 0.0;
            double denom;
            for (int i = 0; i < count; i++)
            {
                double av = a[aStart + i];
                double bv = b[bStart + i];
                sumA += av;
                sumB += bv;
                sumAA += av * av;
                sumBB += bv * bv;
                sumAB += av * bv;
            }

            denom = Math.Sqrt(((count * sumAA) - (sumA * sumA)) * ((count * sumBB) - (sumB * sumB)));
            if (denom <= 0.000001)
            {
                return 0.0;
            }

            return ((count * sumAB) - (sumA * sumB)) / denom;
        }

        /// <summary>
        /// Concordanza tra maschere di silenzio, pesata sulla presenza di almeno un silenzio
        /// </summary>
        private double ComputeSilenceScore(byte[] a, int aStart, byte[] b, int bStart, int count)
        {
            int match = 0;
            int useful = 0;
            for (int i = 0; i < count; i++)
            {
                byte av = a[aStart + i];
                byte bv = b[bStart + i];

                if (av == 1 || bv == 1)
                {
                    useful++;
                    if (av == bv)
                    {
                        match++;
                    }
                }
            }

            if (useful == 0)
            {
                return 0.0;
            }

            return match / (double)useful;
        }

        /// <summary>
        /// Aggiorna best e second tenendo separati offset troppo vicini
        /// </summary>
        private void TrackCandidate(AudioOffsetScore candidate, ref AudioOffsetScore best, ref AudioOffsetScore second, int minDistanceMs)
        {
            if (candidate == null || candidate.Coverage <= 0.0)
            {
                return;
            }

            if (candidate.Score > best.Score)
            {
                if (Math.Abs(candidate.OffsetMs - best.OffsetMs) > minDistanceMs)
                {
                    second = best;
                }
                best = candidate;
            }
            else if (Math.Abs(candidate.OffsetMs - best.OffsetMs) > minDistanceMs && candidate.Score > second.Score)
            {
                second = candidate;
            }
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Chiave cache fingerprint audio
        /// </summary>
        private class AudioFingerprintCacheKey
        {
            public string FilePath;
            public long Length;
            public long LastWriteUtcTicks;
            public int SampleRate;
            public int WindowMs;

            /// <summary>
            /// Confronta la chiave cache con un'altra chiave
            /// </summary>
            /// <param name="other">Chiave da confrontare</param>
            /// <returns>True se i campi identificano lo stesso fingerprint</returns>
            public bool EqualsKey(AudioFingerprintCacheKey other)
            {
                bool result = false;
                if (other != null &&
                    string.Equals(this.FilePath, other.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    this.Length == other.Length &&
                    this.LastWriteUtcTicks == other.LastWriteUtcTicks &&
                    this.SampleRate == other.SampleRate &&
                    this.WindowMs == other.WindowMs)
                {
                    result = true;
                }

                return result;
            }
        }

        /// <summary>
        /// Fingerprint audio cached
        /// </summary>
        private class CachedAudioFingerprint
        {
            public AudioFingerprintCacheKey Key;
            public AudioFingerprint Fingerprint;
        }

        /// <summary>
        /// Feature audio normalizzate
        /// </summary>
        private class AudioFingerprint
        {
            public double[] Envelope;
            public double[] Onset;
            public double[] Derivative;
            public byte[] Silence;
            public double[] SilenceRun;
        }

        /// <summary>
        /// Score di un offset audio
        /// </summary>
        private class AudioOffsetScore
        {
            public int OffsetMs;
            public double Score;
            public double Coverage;
            public double EnvelopeScore;
            public double SilenceScore;
            public double OnsetScore;
            public double DerivativeScore;
            public double SilenceRunScore;
            public double ChunkScore;
        }

        #endregion
    }
}
