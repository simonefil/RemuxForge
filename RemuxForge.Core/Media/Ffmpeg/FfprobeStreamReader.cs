using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;

namespace RemuxForge.Core.Media.Ffmpeg
{
    /// <summary>
    /// Legge con ffprobe gli stream di un file nella numerazione di ffmpeg (indice assoluto dello stream), la stessa che usa
    /// -map 0:&lt;indice&gt;: per i contenitori non Matroska è l'unica numerazione affidabile, perché gli ID di mkvmerge non
    /// corrispondono agli stream di ffmpeg
    /// </summary>
    public sealed class FfprobeStreamReader
    {
        #region Variabili di classe

        /// <summary>
        /// Percorso eseguibile ffprobe
        /// </summary>
        private readonly string _ffprobePath;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffprobePath">Percorso eseguibile ffprobe</param>
        public FfprobeStreamReader(string ffprobePath)
        {
            this._ffprobePath = ffprobePath;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Legge durata e stream di un file
        /// </summary>
        /// <param name="filePath">File da leggere</param>
        /// <returns>Durata e stream nell'ordine di ffmpeg</returns>
        /// <exception cref="InvalidOperationException">ffprobe non legge il file; il messaggio riporta il suo errore</exception>
        public FfprobeMediaInfo Read(string filePath)
        {
            FfprobeMediaInfo info = new FfprobeMediaInfo();
            ProcessResult run = ProcessRunner.RunIsolated(this._ffprobePath, new string[]
            {
                "-v", "error",
                "-show_entries", "format=duration:stream=index,codec_type,codec_name,profile:stream_tags=language,title:stream_disposition=default,forced",
                "-of", "json",
                filePath
            }, 0, CancellationToken.None);

            if (run.ExitCode != 0 || string.IsNullOrWhiteSpace(run.Stdout))
                throw new InvalidOperationException(AppText.F("toolbox.ffprobe.readFailed", run.Stderr.Trim().Length > 0 ? run.Stderr.Trim() : AppText.F("toolbox.ffprobe.exitCode", run.ExitCode)));

            using (JsonDocument document = JsonDocument.Parse(run.Stdout))
            {
                JsonElement root = document.RootElement;
                JsonElement format;
                JsonElement streams;
                double duration;

                if (root.TryGetProperty("format", out format) && format.TryGetProperty("duration", out JsonElement durationElement) &&
                    double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration))
                    info.DurationSeconds = duration;

                if (root.TryGetProperty("streams", out streams))
                {
                    foreach (JsonElement element in streams.EnumerateArray())
                    {
                        FfprobeStream stream = new FfprobeStream();
                        JsonElement tags;
                        JsonElement disposition;

                        stream.Index = element.TryGetProperty("index", out JsonElement index) ? index.GetInt32() : info.Streams.Count;
                        stream.CodecType = element.TryGetProperty("codec_type", out JsonElement codecType) ? codecType.GetString() ?? "" : "";
                        stream.CodecName = element.TryGetProperty("codec_name", out JsonElement codecName) ? codecName.GetString() ?? "" : "";
                        stream.Profile = element.TryGetProperty("profile", out JsonElement profile) ? profile.GetString() ?? "" : "";
                        if (element.TryGetProperty("tags", out tags))
                        {
                            stream.Language = tags.TryGetProperty("language", out JsonElement language) ? language.GetString() ?? "" : "";
                            stream.Title = tags.TryGetProperty("title", out JsonElement title) ? title.GetString() ?? "" : "";
                        }
                        if (element.TryGetProperty("disposition", out disposition))
                        {
                            stream.IsDefault = disposition.TryGetProperty("default", out JsonElement isDefault) && isDefault.GetInt32() == 1;
                            stream.IsForced = disposition.TryGetProperty("forced", out JsonElement isForced) && isForced.GetInt32() == 1;
                        }

                        info.Streams.Add(stream);
                    }
                }
            }

            return info;
        }

        #endregion
    }

    /// <summary>
    /// Durata e stream di un file letti da ffprobe
    /// </summary>
    public sealed class FfprobeMediaInfo
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public FfprobeMediaInfo()
        {
            this.Streams = new List<FfprobeStream>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Durata del file in secondi, 0 se ffprobe non la riporta
        /// </summary>
        public double DurationSeconds { get; set; }

        /// <summary>
        /// Stream nell'ordine e con l'indice di ffmpeg
        /// </summary>
        public List<FfprobeStream> Streams { get; }

        #endregion
    }

    /// <summary>
    /// Uno stream come lo descrive ffprobe
    /// </summary>
    public sealed class FfprobeStream
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public FfprobeStream()
        {
            this.CodecType = "";
            this.CodecName = "";
            this.Profile = "";
            this.Language = "";
            this.Title = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Indice assoluto dello stream, quello di -map 0:&lt;indice&gt;
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Tipo dello stream: video, audio, subtitle, data, attachment
        /// </summary>
        public string CodecType { get; set; }

        /// <summary>
        /// Nome breve del codec (aac, ac3, mov_text, hdmv_pgs_subtitle…)
        /// </summary>
        public string CodecName { get; set; }

        /// <summary>
        /// Profilo del codec, vuoto se ffprobe non lo riporta
        /// </summary>
        public string Profile { get; set; }

        /// <summary>
        /// Tag della lingua così come è scritto nel file, vuoto se manca
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// Titolo dello stream, vuoto se manca
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// True se lo stream è marcato come predefinito
        /// </summary>
        public bool IsDefault { get; set; }

        /// <summary>
        /// True se lo stream è marcato come forzato
        /// </summary>
        public bool IsForced { get; set; }

        #endregion
    }
}
