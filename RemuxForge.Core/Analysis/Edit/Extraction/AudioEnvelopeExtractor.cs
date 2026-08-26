using RemuxForge.Core.Infrastructure;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

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
    /// Estrae l'inviluppo di energia di una traccia audio con un solo comando ffmpeg
    /// </summary>
    internal class AudioEnvelopeExtractor
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
        public AudioEnvelope Extract(string filePath, int streamIndex, int timeoutMs)
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

            if (decibel.Count == 0 && run.ExitCode != 0)
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
            List<string> sourceLanguages = this.ReadStreamLanguages(sourceFile, timeoutMs);
            List<string> languageLanguages = this.ReadStreamLanguages(languageFile, timeoutMs);
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

        #endregion

        #region Metodi privati

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
            // ffmpeg estrae il PCM dal campione zero e butta via l'origine: senza rimetterla
            // l'offset audio e quello video non stanno sulla stessa origine
            double result = 0.0;
            if (string.IsNullOrEmpty(this._ffprobePath))
                return result;

            ProcessResult run = ProcessRunner.Run(this._ffprobePath, new string[] {
                "-v", "error", "-select_streams", "a:" + streamIndex.ToString(CultureInfo.InvariantCulture),
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
}
