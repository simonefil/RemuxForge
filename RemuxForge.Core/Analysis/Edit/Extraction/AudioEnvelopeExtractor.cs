using OpenCvSharp;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media.Ffmpeg;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Analysis.Edit.Extraction
{
    /// <summary>
    /// Inviluppo di energia di una traccia audio, campionato ogni 10 millisecondi
    /// </summary>
    internal class AudioEnvelope
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="decibel">Energia in dB per campione</param>
        /// <param name="originMs">Istante di partenza della traccia nel contenitore</param>
        public AudioEnvelope(float[] decibel, double originMs)
        {
            this.Decibel = decibel;
            this.OriginMs = originMs;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Energia in dB, un campione ogni <see cref="AudioEnvelopeExtractor.STEP_MS"/> millisecondi
        /// </summary>
        public float[] Decibel { get; private set; }

        /// <summary>
        /// Istante in cui la traccia parte nel contenitore, in millisecondi
        /// </summary>
        public double OriginMs { get; private set; }

        /// <summary>
        /// Numero di campioni dell'inviluppo
        /// </summary>
        public int Count
        {
            get { return this.Decibel.Length; }
        }

        #endregion
    }

    /// <summary>
    /// Estrae segnali di analisi e visualizzazioni complete da una traccia audio
    /// </summary>
    public class AudioEnvelopeExtractor
    {
        #region Costanti

        /// <summary>
        /// Frequenza di campionamento a cui viene riportata la traccia
        /// </summary>
        public const int SAMPLE_RATE = 16000;

        /// <summary>
        /// Campioni per finestra di energia: 10 millisecondi a 16 kHz
        /// </summary>
        public const int HOP = 160;

        /// <summary>
        /// Passo temporale dell'inviluppo in millisecondi
        /// </summary>
        public const double STEP_MS = HOP * 1000.0 / SAMPLE_RATE;

        /// <summary>
        /// Energia minima rappresentabile, sotto la quale il campione è silenzio digitale
        /// </summary>
        private const double ENERGY_FLOOR = 1e-7;

        /// <summary>
        /// Finestra minima della STFT dello spettrogramma, in campioni
        /// </summary>
        private const int SPECTROGRAM_MINIMUM_WINDOW = 256;

        /// <summary>
        /// Finestra massima della STFT dello spettrogramma, in campioni: tenuta corta perché la
        /// timeline chiede risoluzione temporale, non risoluzione in frequenza
        /// </summary>
        private const int SPECTROGRAM_MAXIMUM_WINDOW = 2048;

        /// <summary>
        /// Byte oltre i quali le visualizzazioni meno recenti vengono scartate
        /// </summary>
        private const long TIMELINE_CACHE_LIMIT_BYTES = 256L * 1024L * 1024L;

        #endregion

        #region Variabili di istanza

        /// <summary>
        /// Percorso dell'eseguibile ffmpeg
        /// </summary>
        private string _ffmpegPath;

        /// <summary>
        /// Percorso dell'eseguibile ffprobe
        /// </summary>
        private string _ffprobePath;

        /// <summary>
        /// Visualizzazioni già calcolate, indicizzate per file, traccia e qualità
        /// </summary>
        private readonly Dictionary<string, AudioTimelineCacheEntry> _timelines;

        /// <summary>
        /// Ordine di utilizzo delle visualizzazioni in cache, dalla più recente
        /// </summary>
        private readonly LinkedList<string> _timelineOrder;

        /// <summary>
        /// Calcoli in corso, per far attendere le richieste gemelle invece di ripetere l'estrazione
        /// </summary>
        private readonly Dictionary<string, Lazy<AudioTimelinePair>> _timelinesInFlight;

        /// <summary>
        /// Byte occupati dalle visualizzazioni in cache
        /// </summary>
        private long _timelineCacheBytes;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso di ffmpeg</param>
        /// <param name="ffprobePath">Percorso di ffprobe</param>
        public AudioEnvelopeExtractor(string ffmpegPath, string ffprobePath)
        {
            this._ffmpegPath = ffmpegPath ?? "";
            this._ffprobePath = ffprobePath ?? "";
            this._timelines = new Dictionary<string, AudioTimelineCacheEntry>(StringComparer.Ordinal);
            this._timelineOrder = new LinkedList<string>();
            this._timelinesInFlight = new Dictionary<string, Lazy<AudioTimelinePair>>(StringComparer.Ordinal);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Estrae l'inviluppo di energia di una traccia audio del file
        /// </summary>
        /// <param name="filePath">File multimediale</param>
        /// <param name="streamIndex">Indice della traccia audio nell'ordine del contenitore</param>
        /// <param name="timeoutMs">Timeout del comando in millisecondi</param>
        /// <returns>Inviluppo in dB con l'origine di contenitore già risolta</returns>
        internal AudioEnvelope Extract(string filePath, int streamIndex, int timeoutMs)
        {
            List<float> decibel = new List<float>();
            byte[] pending = new byte[HOP * sizeof(float)];
            int pendingBytes = 0;

            string[] arguments = FfmpegCommand.Decode(false)
                .Input(filePath)
                .AudioStream("a:" + streamIndex.ToString(CultureInfo.InvariantCulture))
                .MonoResampled(SAMPLE_RATE)
                .ToFloatPcm()
                .Build();
            ProcessBinaryResult run = ProcessRunner.RunBinaryStdout(this._ffmpegPath, arguments, (buffer, count) =>
            {
                int consumed = 0;
                while (consumed < count)
                {
                    int copied = Math.Min(pending.Length - pendingBytes, count - consumed);
                    Buffer.BlockCopy(buffer, consumed, pending, pendingBytes, copied);
                    pendingBytes += copied;
                    consumed += copied;
                    if (pendingBytes < pending.Length)
                        continue;
                    pendingBytes = 0;
                    double squares = 0.0;
                    for (int i = 0; i < HOP; i++)
                    {
                        double sample = BitConverter.ToSingle(pending, i * sizeof(float));
                        squares += sample * sample;
                    }
                    decibel.Add((float)(20.0 * Math.Log10(Math.Max(Math.Sqrt(squares / HOP), ENERGY_FLOOR))));
                }
            }, timeoutMs);

            if (run.ExitCode != 0)
                throw new InvalidOperationException("Nessun campione audio estratto da " + Path.GetFileName(filePath) + ": " + run.Stderr);

            return new AudioEnvelope(decibel.ToArray(), this.ReadOriginMs(filePath, streamIndex, timeoutMs));
        }


        /// <summary>
        /// Sceglie la traccia audio che le due copie condividono, quando esiste
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="languageFile">File della copia doppiata</param>
        /// <param name="timeoutMs">Timeout dei comandi in millisecondi</param>
        /// <param name="sourceStream">Indice della traccia sorgente</param>
        /// <param name="languageStream">Indice della traccia della copia doppiata</param>
        /// <returns>True quando le due copie dichiarano davvero la stessa lingua</returns>
        public bool ResolveSharedStreams(string sourceFile, string languageFile, int timeoutMs, out int sourceStream, out int languageStream)
        {
            // Il letto di musica ed effetti sopravvive al doppiaggio, ma con la traccia nella
            // stessa lingua l'inviluppo aggancia molto meglio
            sourceStream = 0;
            languageStream = 0;
            List<string> sourceLanguages = null;
            List<string> languageLanguages = null;
            Parallel.Invoke(
                () => sourceLanguages = this.ReadStreamLanguages(sourceFile, timeoutMs),
                () => languageLanguages = this.ReadStreamLanguages(languageFile, timeoutMs));
            if (sourceLanguages.Count == 0 || languageLanguages.Count == 0)
                return false;
            string sourceLanguage = LanguageValidator.NormalizeToIso6392(sourceLanguages[0]);
            if (string.IsNullOrEmpty(sourceLanguage) || sourceLanguage == "und" || sourceLanguage == "mul" || sourceLanguage == "zxx")
                return false;

            for (int i = 0; i < languageLanguages.Count; i++)
            {
                if (!string.Equals(LanguageValidator.NormalizeToIso6392(languageLanguages[i]), sourceLanguage, StringComparison.OrdinalIgnoreCase))
                    continue;
                languageStream = i;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Restituisce le visualizzazioni della traccia, calcolandole soltanto al primo accesso e
        /// facendo attendere le richieste gemelle invece di ripetere l'estrazione
        /// </summary>
        /// <param name="filePath">File multimediale</param>
        /// <param name="trackId">ID della traccia nel contenitore</param>
        /// <param name="durationMs">Durata da rappresentare in millisecondi</param>
        /// <param name="highQuality">True per una risoluzione orizzontale e verticale maggiore</param>
        /// <param name="timeoutMs">Timeout del comando in millisecondi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Coppia inviluppo/spettrogramma della traccia</returns>
        public AudioTimelinePair GetOrGenerateTimeline(string filePath, int trackId, double durationMs, bool highQuality, int timeoutMs, CancellationToken cancellationToken)
        {
            string key = BuildTimelineCacheKey(filePath, trackId, durationMs, highQuality);
            Lazy<AudioTimelinePair> pending;
            lock (this._timelines)
            {
                if (this._timelines.TryGetValue(key, out AudioTimelineCacheEntry cached))
                {
                    this._timelineOrder.Remove(cached.Node);
                    this._timelineOrder.AddFirst(cached.Node);
                    return cached.Timeline;
                }

                if (!this._timelinesInFlight.TryGetValue(key, out pending))
                {
                    pending = new Lazy<AudioTimelinePair>(() => this.GenerateTimelineForTrackId(filePath, trackId, durationMs, highQuality, timeoutMs, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication);
                    this._timelinesInFlight.Add(key, pending);
                }
            }

            try
            {
                AudioTimelinePair result = pending.Value;
                this.CacheTimeline(key, result);
                return result;
            }
            finally
            {
                lock (this._timelines)
                {
                    if (this._timelinesInFlight.TryGetValue(key, out Lazy<AudioTimelinePair> current) && ReferenceEquals(current, pending))
                        this._timelinesInFlight.Remove(key);
                }
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Genera con una sola decodifica alla frequenza nativa della traccia sia l'inviluppo della
        /// forma d'onda sia le tile dello spettrogramma, sulla stessa identica scala temporale
        /// </summary>
        /// <param name="filePath">File multimediale</param>
        /// <param name="trackId">ID della traccia nel contenitore</param>
        /// <param name="durationMs">Durata da rappresentare in millisecondi</param>
        /// <param name="highQuality">True per una risoluzione orizzontale e verticale maggiore</param>
        /// <param name="timeoutMs">Timeout del comando in millisecondi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Coppia inviluppo/spettrogramma della traccia</returns>
        private AudioTimelinePair GenerateTimelineForTrackId(string filePath, int trackId, double durationMs, bool highQuality, int timeoutMs, CancellationToken cancellationToken)
        {
            const int tileWidth = 8192;
            const int maximumLowQualityPoints = 262144;
            const int maximumHighQualityPoints = 4194304;
            string selector = trackId.ToString(CultureInfo.InvariantCulture);
            int sampleRate = this.ReadSampleRate(filePath, selector, timeoutMs);
            int maximumTileCount = highQuality ? 24 : 16;
            int tileHeight = highQuality ? 128 : 96;
            double safeDurationMs = Math.Max(1.0, durationMs);
            double millisecondsPerPixel = Math.Max(1.0, safeDurationMs / (tileWidth * maximumTileCount));
            double tileDurationMs = tileWidth * millisecondsPerPixel;
            int tileCount = Math.Max(1, Math.Min(maximumTileCount, (int)Math.Ceiling(safeDurationMs / tileDurationMs)));
            string representedSeconds = (tileCount * tileDurationMs / 1000.0).ToString("0.######", CultureInfo.InvariantCulture);

            // Il passo fra due colonne vale esattamente un pixel: la colonna n cade a n * millisecondsPerPixel
            // per costruzione, ed è questo che tiene lo spettrogramma allineato alla forma d'onda
            double samplesPerColumn = Math.Max(1.0, millisecondsPerPixel * sampleRate / 1000.0);
            int windowSamples = Math.Min(SPECTROGRAM_MAXIMUM_WINDOW, Math.Max(SPECTROGRAM_MINIMUM_WINDOW, NextPowerOfTwo((int)Math.Ceiling(samplesPerColumn))));
            SpectrogramAccumulator spectrogram = new SpectrogramAccumulator(tileCount, tileWidth, tileHeight, samplesPerColumn, windowSamples);

            int maximumPoints = highQuality ? maximumHighQualityPoints : maximumLowQualityPoints;
            double requestedStepMs = Math.Max(1.0, safeDurationMs / maximumPoints);
            int bucketSamples = Math.Max(1, (int)Math.Ceiling(requestedStepMs * sampleRate / 1000.0));
            double stepMs = bucketSamples * 1000.0 / sampleRate;
            List<short> minimum = new List<short>();
            List<short> maximum = new List<short>();
            byte[] pending = new byte[sizeof(float)];
            int pendingBytes = 0;
            int samplesInBucket = 0;
            float bucketMinimum = 0.0f;
            float bucketMaximum = 0.0f;
            short peak = 0;

            // Niente aresample: la traccia va letta alla sua frequenza, altrimenti lo spettrogramma
            // si ferma a meta' della Nyquist dichiarata dall'editor
            string[] arguments = FfmpegCommand.Decode(false)
                .Input(filePath)
                .AudioStream(selector)
                .AudioFilter("aformat=channel_layouts=mono,apad=whole_dur=" + representedSeconds + ",atrim=end=" + representedSeconds)
                .ToFloatPcm()
                .Build();

            try
            {
                ProcessBinaryResult run = ProcessRunner.RunBinaryStdout(this._ffmpegPath, arguments, (buffer, count) =>
                {
                    int consumed = 0;
                    while (consumed < count)
                    {
                        int copied = Math.Min(sizeof(float) - pendingBytes, count - consumed);
                        Buffer.BlockCopy(buffer, consumed, pending, pendingBytes, copied);
                        pendingBytes += copied;
                        consumed += copied;
                        if (pendingBytes < sizeof(float))
                            continue;
                        pendingBytes = 0;
                        float sample = BitConverter.ToSingle(pending, 0);
                        spectrogram.Push(sample);
                        if (samplesInBucket == 0)
                        {
                            bucketMinimum = sample;
                            bucketMaximum = sample;
                        }
                        else
                        {
                            bucketMinimum = Math.Min(bucketMinimum, sample);
                            bucketMaximum = Math.Max(bucketMaximum, sample);
                        }
                        samplesInBucket++;
                        if (samplesInBucket < bucketSamples)
                            continue;
                        AddWaveformBucket(minimum, maximum, bucketMinimum, bucketMaximum, ref peak);
                        samplesInBucket = 0;
                    }
                }, timeoutMs, cancellationToken);

                if (samplesInBucket > 0)
                    AddWaveformBucket(minimum, maximum, bucketMinimum, bucketMaximum, ref peak);
                if (run.ExitCode != 0)
                    throw new InvalidOperationException("Nessun campione audio estratto da " + Path.GetFileName(filePath) + ": " + run.Stderr);

                double originMs = this.ReadOriginMs(filePath, selector, sampleRate, timeoutMs);
                AudioTimelineWaveform waveform = new AudioTimelineWaveform(stepMs, originMs, peak, minimum.ToArray(), maximum.ToArray());
                AudioTimelineImage image = new AudioTimelineImage(tileWidth, tileHeight, millisecondsPerPixel, tileDurationMs, originMs, spectrogram.Complete(cancellationToken));
                return new AudioTimelinePair(waveform, image);
            }
            catch (Exception)
            {
                spectrogram.Abort();
                throw;
            }
        }

        /// <summary>
        /// Aggiunge un bucket min/max quantizzato alla waveform
        /// </summary>
        /// <param name="minimum">Minimi già accumulati, a cui viene aggiunto il bucket</param>
        /// <param name="maximum">Massimi già accumulati, a cui viene aggiunto il bucket</param>
        /// <param name="bucketMinimum">Campione più basso del bucket corrente</param>
        /// <param name="bucketMaximum">Campione più alto del bucket corrente</param>
        /// <param name="peak">Picco assoluto accumulato, alzato quando il bucket lo supera</param>
        private static void AddWaveformBucket(List<short> minimum, List<short> maximum, float bucketMinimum, float bucketMaximum, ref short peak)
        {
            short quantizedMinimum = (short)Math.Round(Math.Max(-1.0f, Math.Min(1.0f, bucketMinimum)) * short.MaxValue);
            short quantizedMaximum = (short)Math.Round(Math.Max(-1.0f, Math.Min(1.0f, bucketMaximum)) * short.MaxValue);
            minimum.Add(quantizedMinimum);
            maximum.Add(quantizedMaximum);
            int bucketPeak = Math.Max(Math.Abs((int)quantizedMinimum), Math.Abs((int)quantizedMaximum));
            if (bucketPeak > peak)
                peak = (short)bucketPeak;
        }

        /// <summary>
        /// Legge i tag di lingua delle tracce audio nell'ordine del contenitore
        /// </summary>
        /// <param name="filePath">File multimediale</param>
        /// <param name="timeoutMs">Timeout del comando in millisecondi</param>
        /// <returns>Tag di lingua, vuoti dove il contenitore non li dichiara</returns>
        private List<string> ReadStreamLanguages(string filePath, int timeoutMs)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(this._ffprobePath))
                return result;
            ProcessResult run = ProcessRunner.Run(this._ffprobePath, FfmpegCommand.Probe("a", "stream_tags=language", filePath), timeoutMs);
            if (run.ExitCode != 0)
                return result;

            try
            {
                using JsonDocument document = JsonDocument.Parse(run.Stdout);
                if (!document.RootElement.TryGetProperty("streams", out JsonElement streams))
                    return result;
                foreach (JsonElement stream in streams.EnumerateArray())
                {
                    string language = "";
                    if (stream.TryGetProperty("tags", out JsonElement tags) && tags.TryGetProperty("language", out JsonElement value))
                        language = value.GetString() ?? "";
                    result.Add(language);
                }
            }
            catch (JsonException)
            {
                result.Clear();
            }

            return result;
        }

        /// <summary>
        /// Legge l'istante in cui la traccia audio parte nel contenitore
        /// </summary>
        /// <param name="filePath">File multimediale</param>
        /// <param name="streamIndex">Indice della traccia audio</param>
        /// <param name="timeoutMs">Timeout del comando in millisecondi</param>
        /// <returns>Origine in millisecondi, al netto del ritardo di codec</returns>
        private double ReadOriginMs(string filePath, int streamIndex, int timeoutMs)
        {
            return this.ReadOriginMs(filePath, "a:" + streamIndex.ToString(CultureInfo.InvariantCulture), SAMPLE_RATE, timeoutMs);
        }

        /// <summary>
        /// Legge l'origine tramite un selettore ffprobe già risolto
        /// </summary>
        /// <param name="filePath">File multimediale</param>
        /// <param name="streamSelector">Selettore ffprobe della traccia</param>
        /// <param name="sampleRate">Frequenza a cui è stato estratto il PCM</param>
        /// <param name="timeoutMs">Timeout del comando in millisecondi</param>
        /// <returns>Origine in millisecondi, al netto del ritardo di codec</returns>
        private double ReadOriginMs(string filePath, string streamSelector, int sampleRate, int timeoutMs)
        {
            // ffmpeg estrae il PCM dal campione zero e butta via l'origine: senza rimetterla
            // l'offset audio e quello video non stanno sulla stessa origine
            double result = 0.0;
            if (string.IsNullOrEmpty(this._ffprobePath))
                return result;

            ProcessResult run = ProcessRunner.Run(this._ffprobePath, FfmpegCommand.Probe(streamSelector, "stream=start_time,initial_padding", filePath), timeoutMs);
            if (run.ExitCode != 0)
                return result;

            try
            {
                using JsonDocument document = JsonDocument.Parse(run.Stdout);
                if (!document.RootElement.TryGetProperty("streams", out JsonElement streams) || streams.GetArrayLength() == 0)
                    return result;
                JsonElement stream = streams[0];
                if (stream.TryGetProperty("start_time", out JsonElement startTime) && double.TryParse(startTime.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                    result = seconds * 1000.0;
                // initial_padding è ritardo di codec e va tolto, convertito alla stessa frequenza
                // con cui il chiamante ha estratto il PCM
                if (stream.TryGetProperty("initial_padding", out JsonElement padding) && padding.TryGetInt32(out int paddingSamples))
                    result -= paddingSamples * 1000.0 / sampleRate;
            }
            catch (JsonException)
            {
            }

            return result;
        }


        /// <summary>
        /// Costruisce la chiave di cache di una visualizzazione, includendo la versione del file
        /// </summary>
        /// <param name="filePath">File multimediale</param>
        /// <param name="trackId">ID della traccia nel contenitore</param>
        /// <param name="durationMs">Durata rappresentata in millisecondi</param>
        /// <param name="highQuality">True per la risoluzione maggiore</param>
        /// <returns>Chiave univoca della visualizzazione</returns>
        private static string BuildTimelineCacheKey(string filePath, int trackId, double durationMs, bool highQuality)
        {
            long length = 0L;
            long ticks = 0L;
            FileInfo file = new FileInfo(filePath);
            if (file.Exists)
            {
                length = file.Length;
                ticks = file.LastWriteTimeUtc.Ticks;
            }

            return filePath + "|" + length.ToString(CultureInfo.InvariantCulture) + "|" + ticks.ToString(CultureInfo.InvariantCulture)
                + "|" + trackId.ToString(CultureInfo.InvariantCulture) + "|" + durationMs.ToString("R", CultureInfo.InvariantCulture)
                + "|" + (highQuality ? "high" : "low");
        }

        /// <summary>
        /// Inserisce la visualizzazione in cache scartando le meno recenti oltre il budget
        /// </summary>
        /// <param name="key">Chiave della visualizzazione</param>
        /// <param name="timeline">Visualizzazione calcolata</param>
        private void CacheTimeline(string key, AudioTimelinePair timeline)
        {
            long bytes = timeline.Waveform.Minimum.LongLength * sizeof(short) * 2L;
            for (int index = 0; index < timeline.Image.Tiles.Count; index++)
                bytes += timeline.Image.Tiles[index].LongLength;

            lock (this._timelines)
            {
                if (this._timelines.ContainsKey(key))
                    return;
                LinkedListNode<string> node = this._timelineOrder.AddFirst(key);
                this._timelines.Add(key, new AudioTimelineCacheEntry(timeline, node, bytes));
                this._timelineCacheBytes += bytes;
                while (this._timelineCacheBytes > TIMELINE_CACHE_LIMIT_BYTES && this._timelineOrder.Count > 1)
                {
                    LinkedListNode<string> oldest = this._timelineOrder.Last;
                    this._timelineOrder.RemoveLast();
                    if (this._timelines.TryGetValue(oldest.Value, out AudioTimelineCacheEntry evicted))
                    {
                        this._timelineCacheBytes -= evicted.Bytes;
                        this._timelines.Remove(oldest.Value);
                    }
                }
            }
        }

        /// <summary>
        /// Legge la frequenza di campionamento nativa della traccia
        /// </summary>
        /// <param name="filePath">File multimediale</param>
        /// <param name="streamSelector">Selettore ffprobe della traccia</param>
        /// <param name="timeoutMs">Timeout del comando in millisecondi</param>
        /// <returns>Frequenza dichiarata dal contenitore, o quella di analisi se manca</returns>
        private int ReadSampleRate(string filePath, string streamSelector, int timeoutMs)
        {
            if (string.IsNullOrEmpty(this._ffprobePath))
                return SAMPLE_RATE;

            ProcessResult run = ProcessRunner.Run(this._ffprobePath, FfmpegCommand.Probe(streamSelector, "stream=sample_rate", filePath), timeoutMs);
            if (run.ExitCode != 0)
                return SAMPLE_RATE;

            try
            {
                using JsonDocument document = JsonDocument.Parse(run.Stdout);
                if (!document.RootElement.TryGetProperty("streams", out JsonElement streams) || streams.GetArrayLength() == 0)
                    return SAMPLE_RATE;
                if (streams[0].TryGetProperty("sample_rate", out JsonElement rate) && int.TryParse(rate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                    return parsed;
            }
            catch (JsonException)
            {
            }

            return SAMPLE_RATE;
        }

        /// <summary>
        /// Arrotonda per eccesso alla potenza di due
        /// </summary>
        /// <param name="value">Valore da arrotondare</param>
        /// <returns>La più piccola potenza di due maggiore o uguale al valore</returns>
        private static int NextPowerOfTwo(int value)
        {
            int result = 1;
            while (result < value)
                result <<= 1;
            return result;
        }

        #endregion
    }

    /// <summary>
    /// Costruisce le tile dello spettrogramma mentre i campioni arrivano da FFmpeg, calcolando le
    /// colonne su thread separati
    /// </summary>
    internal sealed class SpectrogramAccumulator
    {
        #region Costanti

        /// <summary>
        /// Livello in dB sotto il quale una banda dello spettrogramma è nera
        /// </summary>
        private const double DECIBEL_FLOOR = -90.0;

        #endregion

        #region Variabili di istanza

        private readonly int _tileWidth;
        private readonly int _tileHeight;
        private readonly double _samplesPerColumn;
        private readonly int _windowSamples;
        private readonly int _frameSpan;
        private readonly int _framesPerColumn;
        private readonly int _columnCount;
        private readonly int _binCount;
        private readonly float[] _window;
        private readonly float _windowNormalization;
        private readonly float[] _ring;
        private readonly byte[][] _tiles;
        private readonly BlockingCollection<KeyValuePair<int, float[]>> _columns;
        private readonly Task _renderer;
        private long _sampleCount;
        private int _nextColumn;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="tileCount">Numero di tile affiancate</param>
        /// <param name="tileWidth">Larghezza in pixel della singola tile</param>
        /// <param name="tileHeight">Altezza in pixel della singola tile</param>
        /// <param name="samplesPerColumn">Campioni fra due colonne, cioè la larghezza in campioni di un pixel</param>
        /// <param name="windowSamples">Campioni della finestra della trasformata</param>
        public SpectrogramAccumulator(int tileCount, int tileWidth, int tileHeight, double samplesPerColumn, int windowSamples)
        {
            this._tileWidth = tileWidth;
            this._tileHeight = tileHeight;
            this._samplesPerColumn = samplesPerColumn;
            this._windowSamples = windowSamples;
            // Quando il pixel è più largo della finestra la colonna copre più trasformate consecutive,
            // altrimenti resterebbero campioni mai guardati fra un pixel e il successivo
            this._framesPerColumn = Math.Max(1, (int)Math.Ceiling(samplesPerColumn / windowSamples));
            this._frameSpan = this._framesPerColumn * windowSamples;
            this._columnCount = tileCount * tileWidth;
            this._binCount = windowSamples / 2 + 1;
            this._ring = new float[this._frameSpan];
            this._tiles = new byte[tileCount][];
            for (int index = 0; index < tileCount; index++)
                this._tiles[index] = new byte[tileWidth * tileHeight];

            this._window = new float[windowSamples];
            double sum = 0.0;
            for (int index = 0; index < windowSamples; index++)
            {
                double value = 0.5 - 0.5 * Math.Cos(2.0 * Math.PI * index / windowSamples);
                this._window[index] = (float)value;
                sum += value;
            }

            this._windowNormalization = (float)(2.0 / Math.Max(1.0, sum));
            this._columns = new BlockingCollection<KeyValuePair<int, float[]>>(Environment.ProcessorCount * 8);
            this._renderer = Task.Run(() => this.RenderColumns());
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Accoda un campione decodificato, emettendo le colonne via via che sono complete
        /// </summary>
        /// <param name="sample">Campione mono a virgola mobile</param>
        public void Push(float sample)
        {
            this._ring[(int)(this._sampleCount % this._frameSpan)] = sample;
            this._sampleCount++;
            // Il passo resta frazionario: arrotondarlo a un numero intero di campioni farebbe scorrere
            // lo spettrogramma di un millesimo, cioè di oltre un secondo alla fine di un film
            while (this._nextColumn < this._columnCount && this._sampleCount >= this.ColumnStartSample(this._nextColumn) + this._frameSpan / 2)
            {
                this.EmitColumn(this._nextColumn);
                this._nextColumn++;
            }
        }

        /// <summary>
        /// Completa le colonne rimaste, attende i calcoli e codifica le tile
        /// </summary>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Tile PNG nell'ordine temporale</returns>
        public List<byte[]> Complete(CancellationToken cancellationToken)
        {
            // La coda di FFmpeg finisce prima dell'ultima finestra: il silenzio finale chiude le colonne
            while (this._nextColumn < this._columnCount)
                this.Push(0.0f);
            this._columns.CompleteAdding();
            this._renderer.GetAwaiter().GetResult();

            List<byte[]> result = new List<byte[]>();
            byte[][] encoded = new byte[this._tiles.Length][];
            Parallel.For(0, this._tiles.Length, new ParallelOptions { CancellationToken = cancellationToken }, index =>
            {
                using Mat intensity = new Mat(this._tileHeight, this._tileWidth, MatType.CV_8UC1);
                intensity.SetArray(this._tiles[index]);
                using Mat colored = new Mat();
                Cv2.ApplyColorMap(intensity, colored, ColormapTypes.Inferno);
                Cv2.ImEncode(".png", colored, out byte[] png);
                encoded[index] = png;
            });

            result.AddRange(encoded);
            this._columns.Dispose();
            return result;
        }

        /// <summary>
        /// Abbandona il calcolo quando l'estrazione fallisce
        /// </summary>
        public void Abort()
        {
            if (!this._columns.IsAddingCompleted)
                this._columns.CompleteAdding();
            try
            {
                this._renderer.GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }

            this._columns.Dispose();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Restituisce il campione su cui è centrata la colonna
        /// </summary>
        /// <param name="columnIndex">Indice della colonna sull'intera timeline</param>
        /// <returns>Indice del campione, arrotondato al più vicino</returns>
        private long ColumnStartSample(int columnIndex)
        {
            return (long)Math.Round(columnIndex * this._samplesPerColumn);
        }

        /// <summary>
        /// Copia dal buffer circolare i campioni della colonna e li affida ai thread di calcolo
        /// </summary>
        /// <param name="columnIndex">Indice della colonna sull'intera timeline</param>
        private void EmitColumn(int columnIndex)
        {
            float[] frame = ArrayPool<float>.Shared.Rent(this._frameSpan);
            // La finestra è centrata sull'istante della colonna: allinearla a sinistra anticiperebbe
            // ogni transiente di mezza finestra
            int offset = (int)((this.ColumnStartSample(columnIndex) + this._frameSpan / 2) % this._frameSpan);
            int head = this._frameSpan - offset;
            Array.Copy(this._ring, offset, frame, 0, head);
            if (offset > 0)
                Array.Copy(this._ring, 0, frame, head, offset);
            this._columns.Add(new KeyValuePair<int, float[]>(columnIndex, frame));
        }

        /// <summary>
        /// Consuma le colonne in parallelo trasformandole in pixel
        /// </summary>
        private void RenderColumns()
        {
            try
            {
                OrderablePartitioner<KeyValuePair<int, float[]>> partitioner = Partitioner.Create(this._columns.GetConsumingEnumerable(), EnumerablePartitionerOptions.NoBuffering);
                Parallel.ForEach(partitioner, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    () => new SpectrogramWorkspace(this._windowSamples, this._binCount),
                    (column, state, workspace) =>
                    {
                        this.RenderColumn(column.Key, column.Value, workspace);
                        ArrayPool<float>.Shared.Return(column.Value);
                        return workspace;
                    },
                    workspace => workspace.Dispose());
            }
            catch (Exception)
            {
                // Senza consumatore il produttore resterebbe fermo sulla coda piena: chiuderla lo fa fallire subito
                if (!this._columns.IsAddingCompleted)
                    this._columns.CompleteAdding();
                throw;
            }
        }

        /// <summary>
        /// Calcola lo spettro della colonna e ne scrive i pixel nella tile
        /// </summary>
        /// <param name="columnIndex">Indice della colonna sull'intera timeline</param>
        /// <param name="frame">Campioni che ricadono nel pixel</param>
        /// <param name="workspace">Buffer riutilizzati dal thread corrente</param>
        private void RenderColumn(int columnIndex, float[] frame, SpectrogramWorkspace workspace)
        {
            Array.Clear(workspace.Magnitude, 0, workspace.Magnitude.Length);
            for (int part = 0; part < this._framesPerColumn; part++)
            {
                int start = part * this._windowSamples;
                for (int index = 0; index < this._windowSamples; index++)
                    workspace.Windowed[index] = frame[start + index] * this._window[index];
                workspace.Input.SetArray(workspace.Windowed);
                Cv2.Dft(workspace.Input, workspace.Output, DftFlags.ComplexOutput);
                workspace.Output.GetArray(out Vec2f[] spectrum);
                for (int bin = 0; bin < this._binCount; bin++)
                {
                    float magnitude = (float)Math.Sqrt(spectrum[bin].Item0 * spectrum[bin].Item0 + spectrum[bin].Item1 * spectrum[bin].Item1);
                    if (magnitude > workspace.Magnitude[bin])
                        workspace.Magnitude[bin] = magnitude;
                }
            }

            byte[] tile = this._tiles[columnIndex / this._tileWidth];
            int x = columnIndex % this._tileWidth;
            // La riga zero è la Nyquist: lo spettro cresce verso l'alto come nell'immagine precedente
            for (int row = 0; row < this._tileHeight; row++)
            {
                int firstBin = (this._tileHeight - 1 - row) * (this._binCount - 1) / this._tileHeight;
                int lastBin = (this._tileHeight - row) * (this._binCount - 1) / this._tileHeight;
                float loudest = 0.0f;
                for (int bin = firstBin; bin <= Math.Max(firstBin, lastBin); bin++)
                {
                    if (workspace.Magnitude[bin] > loudest)
                        loudest = workspace.Magnitude[bin];
                }

                double decibel = 20.0 * Math.Log10(Math.Max(1e-9, loudest * this._windowNormalization));
                double level = (decibel - DECIBEL_FLOOR) / -DECIBEL_FLOOR;
                tile[row * this._tileWidth + x] = (byte)Math.Round(255.0 * Math.Max(0.0, Math.Min(1.0, level)));
            }
        }

        #endregion
    }

    /// <summary>
    /// Buffer di lavoro di un singolo thread di calcolo dello spettrogramma
    /// </summary>
    internal sealed class SpectrogramWorkspace : IDisposable
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="windowSamples">Campioni della finestra della trasformata</param>
        /// <param name="binCount">Numero di bande utili dello spettro</param>
        public SpectrogramWorkspace(int windowSamples, int binCount)
        {
            this.Windowed = new float[windowSamples];
            this.Magnitude = new float[binCount];
            this.Input = new Mat(1, windowSamples, MatType.CV_32FC1);
            this.Output = new Mat();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Campioni della finestra già moltiplicati per la finestra di Hann
        /// </summary>
        public float[] Windowed { get; private set; }

        /// <summary>
        /// Ampiezza massima per banda fra le trasformate della colonna
        /// </summary>
        public float[] Magnitude { get; private set; }

        /// <summary>
        /// Matrice di ingresso della trasformata
        /// </summary>
        public Mat Input { get; private set; }

        /// <summary>
        /// Matrice di uscita della trasformata
        /// </summary>
        public Mat Output { get; private set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Rilascia le matrici native
        /// </summary>
        public void Dispose()
        {
            this.Input.Dispose();
            this.Output.Dispose();
        }

        #endregion
    }

    /// <summary>
    /// Visualizzazione audio completa suddivisa in tile PNG ad alta risoluzione
    /// </summary>
    public class AudioTimelineImage
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        public AudioTimelineImage(int tileWidth, int tileHeight, double millisecondsPerPixel, double tileDurationMs, double originMs, List<byte[]> tiles)
        {
            this.TileWidth = tileWidth;
            this.TileHeight = tileHeight;
            this.MillisecondsPerPixel = millisecondsPerPixel;
            this.TileDurationMs = tileDurationMs;
            this.OriginMs = originMs;
            this.Tiles = tiles;
        }

        /// <summary>
        /// Larghezza di ogni tile
        /// </summary>
        public int TileWidth { get; private set; }

        /// <summary>
        /// Altezza di ogni tile
        /// </summary>
        public int TileHeight { get; private set; }

        /// <summary>
        /// Scala temporale orizzontale
        /// </summary>
        public double MillisecondsPerPixel { get; private set; }

        /// <summary>
        /// Durata rappresentata da ogni tile
        /// </summary>
        public double TileDurationMs { get; private set; }

        /// <summary>
        /// Origine della traccia nel contenitore
        /// </summary>
        public double OriginMs { get; private set; }

        /// <summary>
        /// Tile PNG ordinate temporalmente
        /// </summary>
        public List<byte[]> Tiles { get; private set; }
    }

    /// <summary>
    /// Inviluppo min/max completo per il rendering vettoriale della waveform
    /// </summary>
    public class AudioTimelineWaveform
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        public AudioTimelineWaveform(double millisecondsPerPoint, double originMs, short peak, short[] minimum, short[] maximum)
        {
            this.MillisecondsPerPoint = millisecondsPerPoint;
            this.OriginMs = originMs;
            this.Peak = peak;
            this.Minimum = minimum;
            this.Maximum = maximum;
        }

        /// <summary>
        /// Passo temporale fra due bucket
        /// </summary>
        public double MillisecondsPerPoint { get; private set; }

        /// <summary>
        /// Origine della traccia nel contenitore
        /// </summary>
        public double OriginMs { get; private set; }

        /// <summary>
        /// Picco assoluto globale quantizzato
        /// </summary>
        public short Peak { get; private set; }

        /// <summary>
        /// Minimi dei bucket
        /// </summary>
        public short[] Minimum { get; private set; }

        /// <summary>
        /// Massimi dei bucket
        /// </summary>
        public short[] Maximum { get; private set; }
    }

    /// <summary>
    /// Voce della cache delle visualizzazioni audio
    /// </summary>
    internal sealed class AudioTimelineCacheEntry
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="timeline">Visualizzazione calcolata</param>
        /// <param name="node">Nodo nell'ordine di utilizzo</param>
        /// <param name="bytes">Byte occupati</param>
        public AudioTimelineCacheEntry(AudioTimelinePair timeline, LinkedListNode<string> node, long bytes)
        {
            this.Timeline = timeline;
            this.Node = node;
            this.Bytes = bytes;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Visualizzazione calcolata
        /// </summary>
        public AudioTimelinePair Timeline { get; private set; }

        /// <summary>
        /// Nodo nell'ordine di utilizzo
        /// </summary>
        public LinkedListNode<string> Node { get; private set; }

        /// <summary>
        /// Byte occupati
        /// </summary>
        public long Bytes { get; private set; }

        #endregion
    }

    /// <summary>
    /// Inviluppo e spettrogramma della stessa traccia, prodotti dalla stessa decodifica
    /// </summary>
    public class AudioTimelinePair
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="waveform">Inviluppo min/max della traccia</param>
        /// <param name="image">Spettrogramma suddiviso in tile</param>
        public AudioTimelinePair(AudioTimelineWaveform waveform, AudioTimelineImage image)
        {
            this.Waveform = waveform;
            this.Image = image;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Inviluppo min/max della traccia
        /// </summary>
        public AudioTimelineWaveform Waveform { get; private set; }

        /// <summary>
        /// Spettrogramma suddiviso in tile
        /// </summary>
        public AudioTimelineImage Image { get; private set; }

        #endregion
    }
}
