using RemuxForge.Core.Infrastructure;
using System;
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

            string[] arguments = new string[] {
                "-nostdin", "-v", "error", "-i", filePath,
                "-map", "0:a:" + streamIndex.ToString(CultureInfo.InvariantCulture),
                "-ac", "1", "-ar", SAMPLE_RATE.ToString(CultureInfo.InvariantCulture),
                "-f", "f32le", "-" };
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
            if (sourceLanguages.Count == 0 || languageLanguages.Count == 0 || string.IsNullOrEmpty(sourceLanguages[0]))
                return false;

            for (int i = 0; i < languageLanguages.Count; i++)
            {
                if (!string.Equals(languageLanguages[i], sourceLanguages[0], StringComparison.OrdinalIgnoreCase))
                    continue;
                languageStream = i;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Genera in un solo decode le tile PNG della visualizzazione audio richiesta
        /// </summary>
        /// <param name="filePath">File multimediale</param>
        /// <param name="trackId">ID della traccia nel contenitore</param>
        /// <param name="durationMs">Durata da rappresentare in millisecondi</param>
        /// <param name="spectrogram">True per lo spettrogramma, false per la forma d'onda</param>
        /// <param name="highQuality">True per una risoluzione orizzontale e verticale maggiore</param>
        /// <param name="timeoutMs">Timeout del comando in millisecondi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Tile della visualizzazione e relativa scala temporale</returns>
        public AudioTimelineImage GenerateTimelineImageForTrackId(string filePath, int trackId, double durationMs, bool spectrogram, bool highQuality, int timeoutMs, CancellationToken cancellationToken)
        {
            const int tileWidth = 8192;
            int maximumTileCount = highQuality ? 24 : 16;
            int tileHeight = highQuality ? 128 : 96;
            string selector = trackId.ToString(CultureInfo.InvariantCulture);
            double safeDurationMs = Math.Max(1.0, durationMs);
            double millisecondsPerPixel = Math.Max(1.0, safeDurationMs / (tileWidth * maximumTileCount));
            double tileDurationMs = tileWidth * millisecondsPerPixel;
            int tileCount = Math.Max(1, Math.Min(maximumTileCount, (int)Math.Ceiling(safeDurationMs / tileDurationMs)));
            string representedSeconds = (tileCount * tileDurationMs / 1000.0).ToString("0.######", CultureInfo.InvariantCulture);
            string normalizedInput = "[0:" + selector + "]aformat=channel_layouts=mono,apad=whole_dur=" + representedSeconds + ",atrim=end=" + representedSeconds;
            string temporaryDirectory = Path.Combine(Path.GetTempPath(), "remuxforge-audio-timeline-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);

            try
            {
                List<string> outputs = new List<string>();
                List<string> filters = new List<string>();
                if (tileCount == 1)
                {
                    filters.Add(BuildTimelineImageFilter(normalizedInput, "[w0]", tileWidth, tileHeight, spectrogram));
                }
                else
                {
                    List<string> timestamps = new List<string>();
                    List<string> segmentLabels = new List<string>();
                    for (int i = 1; i < tileCount; i++)
                        timestamps.Add((i * tileDurationMs / 1000.0).ToString("0.######", CultureInfo.InvariantCulture));
                    for (int i = 0; i < tileCount; i++)
                        segmentLabels.Add("[a" + i.ToString(CultureInfo.InvariantCulture) + "]");
                    filters.Add(normalizedInput + ",asegment=timestamps=" + string.Join("|", timestamps) + string.Join("", segmentLabels));
                    for (int i = 0; i < tileCount; i++)
                        filters.Add(BuildTimelineImageFilter("[a" + i.ToString(CultureInfo.InvariantCulture) + "]", "[w" + i.ToString(CultureInfo.InvariantCulture) + "]", tileWidth, tileHeight, spectrogram));
                }

                List<string> arguments = new List<string> { "-nostdin", "-v", "error", "-i", filePath, "-filter_complex", string.Join(";", filters) };
                for (int i = 0; i < tileCount; i++)
                {
                    string outputPath = Path.Combine(temporaryDirectory, i.ToString("D2", CultureInfo.InvariantCulture) + ".png");
                    outputs.Add(outputPath);
                    arguments.Add("-map");
                    arguments.Add("[w" + i.ToString(CultureInfo.InvariantCulture) + "]");
                    arguments.Add("-frames:v");
                    arguments.Add("1");
                    arguments.Add(outputPath);
                }

                ProcessResult run = ProcessRunner.Run(this._ffmpegPath, arguments.ToArray(), timeoutMs, cancellationToken);
                if (run.ExitCode != 0)
                    throw new InvalidOperationException("Impossibile generare la visualizzazione audio di " + Path.GetFileName(filePath) + ": " + run.Stderr);

                List<byte[]> tiles = new List<byte[]>();
                for (int i = 0; i < outputs.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tiles.Add(File.ReadAllBytes(outputs[i]));
                }

                return new AudioTimelineImage(tileWidth, tileHeight, millisecondsPerPixel, tileDurationMs, this.ReadOriginMs(filePath, selector, timeoutMs), tiles);
            }
            finally
            {
                if (Directory.Exists(temporaryDirectory))
                    Directory.Delete(temporaryDirectory, true);
            }
        }

        /// <summary>
        /// Genera l'inviluppo min/max completo usato dal renderer vettoriale della waveform
        /// </summary>
        /// <param name="filePath">File multimediale</param>
        /// <param name="trackId">ID della traccia nel contenitore</param>
        /// <param name="durationMs">Durata da rappresentare in millisecondi</param>
        /// <param name="highQuality">True per un bucket ogni millisecondo</param>
        /// <param name="timeoutMs">Timeout del comando in millisecondi</param>
        /// <param name="cancellationToken">Token di annullamento</param>
        /// <returns>Inviluppo temporale quantizzato a 16 bit</returns>
        public AudioTimelineWaveform GenerateTimelineWaveformForTrackId(string filePath, int trackId, double durationMs, bool highQuality, int timeoutMs, CancellationToken cancellationToken)
        {
            const int maximumLowQualityPoints = 262144;
            const int maximumHighQualityPoints = 4194304;
            string selector = trackId.ToString(CultureInfo.InvariantCulture);
            double safeDurationMs = Math.Max(1.0, durationMs);
            int maximumPoints = highQuality ? maximumHighQualityPoints : maximumLowQualityPoints;
            double requestedStepMs = Math.Max(1.0, safeDurationMs / maximumPoints);
            int bucketSamples = Math.Max(1, (int)Math.Ceiling(requestedStepMs * SAMPLE_RATE / 1000.0));
            double stepMs = bucketSamples * 1000.0 / SAMPLE_RATE;
            List<short> minimum = new List<short>();
            List<short> maximum = new List<short>();
            byte[] pending = new byte[sizeof(float)];
            int pendingBytes = 0;
            int samplesInBucket = 0;
            float bucketMinimum = 0.0f;
            float bucketMaximum = 0.0f;
            short peak = 0;
            string representedSeconds = (safeDurationMs / 1000.0).ToString("0.######", CultureInfo.InvariantCulture);
            string[] arguments = new string[] {
                "-nostdin", "-v", "error", "-i", filePath,
                "-map", "0:" + selector,
                "-af", "aformat=channel_layouts=mono,aresample=" + SAMPLE_RATE.ToString(CultureInfo.InvariantCulture) + ",apad=whole_dur=" + representedSeconds + ",atrim=end=" + representedSeconds,
                "-f", "f32le", "-" };
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
            return new AudioTimelineWaveform(stepMs, this.ReadOriginMs(filePath, selector, timeoutMs), peak, minimum.ToArray(), maximum.ToArray());
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Costruisce il filtro della singola tile senza legende o cornici generate da FFmpeg
        /// </summary>
        private static string BuildTimelineImageFilter(string inputLabel, string outputLabel, int width, int height, bool spectrogram)
        {
            string size = width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
            if (spectrogram)
                return inputLabel + "showspectrumpic=s=" + size + ":legend=0:mode=combined:color=intensity:scale=log:fscale=lin:orientation=vertical" + outputLabel;
            return inputLabel + "showwavespic=s=" + size + ":split_channels=0:colors=white:filter=peak:draw=full,format=rgba,colorkey=0x000000:0.01:0.0" + outputLabel;
        }

        /// <summary>Aggiunge un bucket min/max quantizzato alla waveform</summary>
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
            ProcessResult run = ProcessRunner.Run(this._ffprobePath, new string[] {
                "-v", "error", "-select_streams", "a", "-show_entries", "stream_tags=language", "-of", "json", filePath }, timeoutMs);
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
            return this.ReadOriginMs(filePath, "a:" + streamIndex.ToString(CultureInfo.InvariantCulture), timeoutMs);
        }

        /// <summary>
        /// Legge l'origine tramite un selettore ffprobe già risolto
        /// </summary>
        private double ReadOriginMs(string filePath, string streamSelector, int timeoutMs)
        {
            // ffmpeg estrae il PCM dal campione zero e butta via l'origine: senza rimetterla
            // l'offset audio e quello video non stanno sulla stessa origine
            double result = 0.0;
            if (string.IsNullOrEmpty(this._ffprobePath))
                return result;

            ProcessResult run = ProcessRunner.Run(this._ffprobePath, new string[] {
                "-v", "error", "-select_streams", streamSelector,
                "-show_entries", "stream=start_time,initial_padding", "-of", "json", filePath }, timeoutMs);
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
                // initial_padding è ritardo di codec e va tolto. Si converte alla frequenza di
                // analisi, come nel prototipo: lo scarto rispetto alla frequenza nativa resta
                // molto sotto i 60 ms con cui l'audio giudica
                if (stream.TryGetProperty("initial_padding", out JsonElement padding) && padding.TryGetInt32(out int paddingSamples))
                    result -= paddingSamples * 1000.0 / SAMPLE_RATE;
            }
            catch (JsonException)
            {
            }

            return result;
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

        /// <summary>Larghezza di ogni tile</summary>
        public int TileWidth { get; private set; }

        /// <summary>Altezza di ogni tile</summary>
        public int TileHeight { get; private set; }

        /// <summary>Scala temporale orizzontale</summary>
        public double MillisecondsPerPixel { get; private set; }

        /// <summary>Durata rappresentata da ogni tile</summary>
        public double TileDurationMs { get; private set; }

        /// <summary>Origine della traccia nel contenitore</summary>
        public double OriginMs { get; private set; }

        /// <summary>Tile PNG ordinate temporalmente</summary>
        public List<byte[]> Tiles { get; private set; }
    }

    /// <summary>
    /// Inviluppo min/max completo per il rendering vettoriale della waveform
    /// </summary>
    public class AudioTimelineWaveform
    {
        /// <summary>Costruttore</summary>
        public AudioTimelineWaveform(double millisecondsPerPoint, double originMs, short peak, short[] minimum, short[] maximum)
        {
            this.MillisecondsPerPoint = millisecondsPerPoint;
            this.OriginMs = originMs;
            this.Peak = peak;
            this.Minimum = minimum;
            this.Maximum = maximum;
        }

        /// <summary>Passo temporale fra due bucket</summary>
        public double MillisecondsPerPoint { get; private set; }

        /// <summary>Origine della traccia nel contenitore</summary>
        public double OriginMs { get; private set; }

        /// <summary>Picco assoluto globale quantizzato</summary>
        public short Peak { get; private set; }

        /// <summary>Minimi dei bucket</summary>
        public short[] Minimum { get; private set; }

        /// <summary>Massimi dei bucket</summary>
        public short[] Maximum { get; private set; }
    }
}
