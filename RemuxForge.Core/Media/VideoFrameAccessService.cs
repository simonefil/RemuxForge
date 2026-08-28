using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Singolo frame nella timeline di presentazione indicizzata
    /// </summary>
    public class VideoFrameIndexEntry
    {
        /// <summary>Indice zero-based nell'ordine di presentazione</summary>
        public int PresentationIndex { get; set; }

        /// <summary>PTS originale del contenitore in millisecondi</summary>
        public double PtsMs { get; set; }

        /// <summary>Durata stimata dell'intervallo di presentazione</summary>
        public double DurationMs { get; set; }

        /// <summary>True quando il packet corrispondente è un keyframe</summary>
        public bool IsKeyFrame { get; set; }
    }

    /// <summary>
    /// Indice completo dei frame di presentazione di un video
    /// </summary>
    public class VideoFrameIndex
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public VideoFrameIndex()
        {
            this.Frames = new List<VideoFrameIndexEntry>();
            this.PixelFormat = "";
            this.SampleAspectRatio = "1:1";
            this.DisplayAspectRatio = "";
            this.ColorSpace = "";
            this.ColorRange = "";
            this.ColorPrimaries = "";
            this.ColorTransfer = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce il frame il cui intervallo di presentazione contiene il timestamp
        /// </summary>
        /// <param name="timestampMs">PTS originale in millisecondi</param>
        /// <returns>Frame oppure null quando il timestamp è fuori timeline</returns>
        public VideoFrameIndexEntry FindContainingFrame(double timestampMs)
        {
            if (this.Frames.Count == 0 || !double.IsFinite(timestampMs))
                return null;
            if (timestampMs < this.FirstPtsMs)
                return this.FirstPtsMs - timestampMs <= this.Frames[0].DurationMs ? this.Frames[0] : null;

            int low = 0;
            int high = this.Frames.Count - 1;
            int candidate = -1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                if (this.Frames[middle].PtsMs <= timestampMs)
                {
                    candidate = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (candidate < 0)
                return null;
            VideoFrameIndexEntry result = this.Frames[candidate];
            return timestampMs < result.PtsMs + result.DurationMs ? result : null;
        }

        /// <summary>
        /// Restituisce il frame con PTS più vicino al timestamp richiesto
        /// </summary>
        /// <param name="timestampMs">PTS originale in millisecondi</param>
        /// <returns>Frame più vicino oppure null per indice vuoto</returns>
        public VideoFrameIndexEntry FindNearestFrame(double timestampMs)
        {
            if (this.Frames.Count == 0 || !double.IsFinite(timestampMs))
                return null;
            if (timestampMs <= this.Frames[0].PtsMs)
                return this.Frames[0];
            if (timestampMs >= this.Frames[this.Frames.Count - 1].PtsMs)
                return this.Frames[this.Frames.Count - 1];

            int low = 0;
            int high = this.Frames.Count - 1;
            while (low + 1 < high)
            {
                int middle = low + (high - low) / 2;
                if (this.Frames[middle].PtsMs <= timestampMs)
                    low = middle;
                else
                    high = middle;
            }

            return timestampMs - this.Frames[low].PtsMs <= this.Frames[high].PtsMs - timestampMs ? this.Frames[low] : this.Frames[high];
        }

        /// <summary>
        /// Restituisce un frame tramite indice di presentazione
        /// </summary>
        /// <param name="presentationIndex">Indice zero-based</param>
        /// <returns>Frame oppure null per indice non valido</returns>
        public VideoFrameIndexEntry GetFrame(int presentationIndex)
        {
            return presentationIndex >= 0 && presentationIndex < this.Frames.Count ? this.Frames[presentationIndex] : null;
        }

        #endregion

        #region Proprietà

        /// <summary>Frame ordinati per PTS e ordine di presentazione</summary>
        public List<VideoFrameIndexEntry> Frames { get; set; }

        /// <summary>Primo PTS originale</summary>
        public double FirstPtsMs { get; set; }

        /// <summary>Ultimo PTS originale</summary>
        public double LastPtsMs { get; set; }

        /// <summary>Fine stimata dell'ultimo intervallo di presentazione</summary>
        public double EndPtsMs { get; set; }

        /// <summary>Durata della timeline relativa al primo PTS</summary>
        public double DurationMs { get; set; }

        /// <summary>Passo mediano fra frame in millisecondi</summary>
        public double MedianFrameDurationMs { get; set; }

        /// <summary>Larghezza coded</summary>
        public int CodedWidth { get; set; }

        /// <summary>Altezza coded</summary>
        public int CodedHeight { get; set; }

        /// <summary>Sample aspect ratio dichiarato</summary>
        public string SampleAspectRatio { get; set; }

        /// <summary>Display aspect ratio dichiarato</summary>
        public string DisplayAspectRatio { get; set; }

        /// <summary>Pixel format FFmpeg</summary>
        public string PixelFormat { get; set; }

        /// <summary>Profondità nominale per componente</summary>
        public int BitDepth { get; set; }

        /// <summary>Matrice colore FFmpeg</summary>
        public string ColorSpace { get; set; }

        /// <summary>Range colore FFmpeg</summary>
        public string ColorRange { get; set; }

        /// <summary>Primarie colore FFmpeg</summary>
        public string ColorPrimaries { get; set; }

        /// <summary>Transfer function FFmpeg</summary>
        public string ColorTransfer { get; set; }

        /// <summary>True per video HDR o con profondità superiore a 8 bit</summary>
        public bool RequiresP010 { get; set; }

        /// <summary>Numero totale di frame</summary>
        public int FrameCount { get { return this.Frames.Count; } }

        /// <summary>Path interno usato soltanto dal servizio server-side</summary>
        internal string FilePath { get; set; }

        /// <summary>Dimensione file usata per invalidare la cache</summary>
        internal long FileLength { get; set; }

        /// <summary>Ultima modifica file usata per invalidare la cache</summary>
        internal long FileLastWriteTicks { get; set; }

        #endregion
    }

    /// <summary>
    /// Frame video raw pronto per il rendering WebGL2
    /// </summary>
    public class VideoRawFrame
    {
        /// <summary>Payload NV12 o P010 little-endian</summary>
        public byte[] Data { get; set; }

        /// <summary>Indice di presentazione realmente estratto</summary>
        public int PresentationIndex { get; set; }

        /// <summary>PTS realmente estratto in millisecondi</summary>
        public double PtsMs { get; set; }

        /// <summary>Larghezza del frame raw</summary>
        public int Width { get; set; }

        /// <summary>Altezza del frame raw</summary>
        public int Height { get; set; }

        /// <summary>Pixel format del payload: nv12 o p010le</summary>
        public string PixelFormat { get; set; }

        /// <summary>Matrice colore dichiarata dal video</summary>
        public string ColorSpace { get; set; }

        /// <summary>Range colore dichiarato dal video</summary>
        public string ColorRange { get; set; }

        /// <summary>Primarie colore dichiarate dal video</summary>
        public string ColorPrimaries { get; set; }

        /// <summary>Transfer function dichiarata dal video</summary>
        public string ColorTransfer { get; set; }

        /// <summary>Identificatore stabile per cache HTTP</summary>
        public string ETag { get; set; }
    }

    /// <summary>
    /// Indicizza PTS e proprietà video riusando i tool configurati dall'applicazione
    /// </summary>
    public class VideoFrameAccessService
    {
        #region Variabili di classe

        private const long RAW_CACHE_LIMIT_BYTES = 256L * 1024L * 1024L;
        private static readonly Regex s_ptsTimeRegex = new Regex(@"pts_time:([-+0-9.eE]+)", RegexOptions.Compiled);

        private readonly string _ffmpegPath;
        private readonly string _ffprobePath;
        private readonly string _mkvMergePath;
        private readonly string _mkvExtractPath;
        private readonly FfmpegConfig _ffmpegConfig;
        private readonly Dictionary<string, double> _startTimes;
        private readonly Dictionary<string, VideoFrameIndex> _indices;
        private readonly Dictionary<string, RawCacheEntry> _rawFrames;
        private readonly LinkedList<string> _rawFrameOrder;
        private long _rawCacheBytes;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public VideoFrameAccessService(string ffprobePath, string mkvMergePath, string mkvExtractPath, string ffmpegPath = "", FfmpegConfig ffmpegConfig = null)
        {
            this._ffmpegPath = ffmpegPath ?? "";
            this._ffprobePath = ffprobePath ?? "";
            this._mkvMergePath = mkvMergePath ?? "";
            this._mkvExtractPath = mkvExtractPath ?? "";
            this._ffmpegConfig = ffmpegConfig ?? new FfmpegConfig();
            this._startTimes = new Dictionary<string, double>(StringComparer.Ordinal);
            this._indices = new Dictionary<string, VideoFrameIndex>(StringComparer.Ordinal);
            this._rawFrames = new Dictionary<string, RawCacheEntry>(StringComparer.Ordinal);
            this._rawFrameOrder = new LinkedList<string>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce l'indice completo dei frame senza decodificare il video
        /// </summary>
        public VideoFrameIndex BuildIndex(string filePath, int timeoutMs, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("File video non disponibile", filePath);

            List<double> timestamps = this.ReadPresentationTimestamps(filePath, timeoutMs, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (timestamps.Count == 0)
                throw new InvalidOperationException("Nessun PTS video disponibile per " + Path.GetFileName(filePath));

            VideoFrameIndex result = new VideoFrameIndex();
            result.FilePath = filePath;
            FileInfo sourceFile = new FileInfo(filePath);
            result.FileLength = sourceFile.Length;
            result.FileLastWriteTicks = sourceFile.LastWriteTimeUtc.Ticks;
            this.ReadVideoProperties(filePath, result, timeoutMs, cancellationToken);
            List<bool> keyframes = this.ReadKeyframeFlags(filePath, timeoutMs, cancellationToken);
            double medianDurationMs = ComputeMedianFrameDuration(timestamps);
            result.MedianFrameDurationMs = medianDurationMs;
            for (int i = 0; i < timestamps.Count; i++)
            {
                double durationMs = i + 1 < timestamps.Count ? timestamps[i + 1] - timestamps[i] : medianDurationMs;
                if (!double.IsFinite(durationMs) || durationMs <= 0.0)
                    durationMs = medianDurationMs;
                result.Frames.Add(new VideoFrameIndexEntry
                {
                    PresentationIndex = i,
                    PtsMs = timestamps[i],
                    DurationMs = durationMs,
                    IsKeyFrame = i < keyframes.Count && keyframes[i]
                });
            }

            result.FirstPtsMs = timestamps[0];
            result.LastPtsMs = timestamps[timestamps.Count - 1];
            result.EndPtsMs = result.LastPtsMs + medianDurationMs;
            result.DurationMs = Math.Max(0.0, result.EndPtsMs - result.FirstPtsMs);
            lock (this._indices)
            {
                this._indices[filePath] = result;
            }
            return result;
        }

        /// <summary>
        /// Restituisce l'indice PTS condiviso costruendolo soltanto al primo accesso
        /// </summary>
        public VideoFrameIndex GetOrBuildIndex(string filePath, int timeoutMs, CancellationToken cancellationToken)
        {
            lock (this._indices)
            {
                if (this._indices.TryGetValue(filePath, out VideoFrameIndex cached) && IsCurrentFile(cached, filePath))
                    return cached;
            }

            return this.BuildIndex(filePath, timeoutMs, cancellationToken);
        }

        /// <summary>
        /// Estrae un frame esatto alla risoluzione utile della preview rispettando la configurazione hardware utente
        /// </summary>
        public VideoRawFrame ExtractFrame(string filePath, int presentationIndex, int maximumWidth, int maximumHeight, CancellationToken cancellationToken)
        {
            return this.ExtractFrameRange(filePath, presentationIndex, 1, maximumWidth, maximumHeight, cancellationToken)[0];
        }

        /// <summary>
        /// Estrae frame consecutivi con una sola decodifica per lo stepping rapido
        /// </summary>
        public List<VideoRawFrame> ExtractFrameRange(string filePath, int presentationIndex, int count, int maximumWidth, int maximumHeight, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(this._ffmpegPath) || !File.Exists(this._ffmpegPath))
                throw new InvalidOperationException("FFmpeg non disponibile per la preview EditMap");
            if (maximumWidth < 2 || maximumHeight < 2)
                throw new ArgumentOutOfRangeException(nameof(maximumWidth), "Le dimensioni preview devono essere positive");
            if (count < 1 || count > 60)
                throw new ArgumentOutOfRangeException(nameof(count), "Il numero di frame deve essere compreso fra 1 e 60");

            int timeoutMs = Math.Max(1000, this._ffmpegConfig.FrameExtractionTimeoutMs);
            VideoFrameIndex index = this.GetOrBuildIndex(filePath, timeoutMs, cancellationToken);
            count = Math.Min(count, Math.Max(0, index.FrameCount - presentationIndex));
            VideoFrameIndexEntry requested = index.GetFrame(presentationIndex);
            if (requested == null || count <= 0)
                throw new ArgumentOutOfRangeException(nameof(presentationIndex), "Frame fuori indice");

            ResolveOutputDimensions(index, maximumWidth, maximumHeight, out int width, out int height);
            string pixelFormat = index.RequiresP010 ? "p010le" : "nv12";
            List<VideoRawFrame> result = new List<VideoRawFrame>();
            bool completelyCached = true;
            for (int i = 0; i < count; i++)
            {
                VideoRawFrame cached = this.ReadCachedFrame(BuildCacheKey(filePath, presentationIndex + i, width, height, pixelFormat));
                if (cached == null)
                {
                    completelyCached = false;
                    break;
                }
                result.Add(cached);
            }
            if (completelyCached)
                return result;

            bool hardware = this._ffmpegConfig.HardwareAcceleration && FfmpegConfig.IsValidHardwareAccelerationMethod(this._ffmpegConfig.HardwareAccelerationMethod);
            result = this.DecodeExactFrames(filePath, index, requested, count, width, height, pixelFormat, hardware, timeoutMs, cancellationToken);
            if (result == null && hardware)
                result = this.DecodeExactFrames(filePath, index, requested, count, width, height, pixelFormat, false, timeoutMs, cancellationToken);
            if (result == null || result.Count != count)
                throw new InvalidOperationException("FFmpeg non ha restituito i frame richiesti");

            for (int i = 0; i < result.Count; i++)
            {
                result[i].ColorSpace = index.ColorSpace;
                result[i].ColorRange = index.ColorRange;
                result[i].ColorPrimaries = index.ColorPrimaries;
                result[i].ColorTransfer = index.ColorTransfer;
                result[i].ETag = BuildETag(filePath, result[i].PresentationIndex, width, height, pixelFormat);
                this.CacheFrame(BuildCacheKey(filePath, result[i].PresentationIndex, width, height, pixelFormat), result[i]);
            }
            return result;
        }

        /// <summary>
        /// Legge i PTS in ordine di presentazione usando timestamps_v2 per Matroska e ffprobe negli altri casi
        /// </summary>
        public List<double> ReadPresentationTimestamps(string filePath, int timeoutMs, CancellationToken cancellationToken)
        {
            List<double> result = new List<double>();
            if (string.Equals(Path.GetExtension(filePath), ".mkv", StringComparison.OrdinalIgnoreCase))
                result = this.ReadMatroskaTimestamps(filePath, timeoutMs, cancellationToken);
            if (result.Count == 0)
                result = this.ReadPacketTimeline(filePath, timeoutMs, cancellationToken, false).Timestamps;
            return result;
        }

        /// <summary>
        /// Legge l'origine temporale dichiarata dal primo stream video
        /// </summary>
        public double ReadStartTimeMs(string filePath, int timeoutMs, CancellationToken cancellationToken)
        {
            lock (this._startTimes)
            {
                if (this._startTimes.TryGetValue(filePath, out double cached))
                    return cached;
            }

            double result = 0.0;
            if (!string.IsNullOrEmpty(this._ffprobePath))
            {
                ProcessResult run = ProcessRunner.Run(this._ffprobePath, new string[] {
                    "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=start_time", "-of", "csv=p=0", filePath }, timeoutMs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (run.ExitCode == 0 && double.TryParse(run.Stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                    result = seconds * 1000.0;
            }

            lock (this._startTimes)
            {
                this._startTimes[filePath] = result;
            }
            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Estrae la timeline timestamps_v2 della vera traccia video Matroska
        /// </summary>
        private List<double> ReadMatroskaTimestamps(string filePath, int timeoutMs, CancellationToken cancellationToken)
        {
            List<double> result = new List<double>();
            string temporaryPath = Path.Combine(Path.GetTempPath(), "remuxforge-video-index-" + Guid.NewGuid().ToString("N") + ".timestamps");
            try
            {
                MkvFileInfo fileInfo = new MkvToolsService(this._mkvMergePath).GetFileInfo(filePath, timeoutMs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                TrackInfo videoTrack = FindVideoTrack(fileInfo);
                if (videoTrack == null || string.IsNullOrEmpty(this._mkvExtractPath))
                    return result;

                ProcessResult run = ProcessRunner.Run(this._mkvExtractPath, new string[] { filePath, "timestamps_v2", videoTrack.Id.ToString(CultureInfo.InvariantCulture) + ":" + temporaryPath }, timeoutMs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (run.ExitCode != 0 || !File.Exists(temporaryPath))
                    return result;

                foreach (string line in File.ReadLines(temporaryPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string value = line.Trim().TrimStart('﻿');
                    if (string.IsNullOrEmpty(value) || value[0] == '#')
                        continue;
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double timestampMs))
                        result.Add(timestampMs);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                result.Clear();
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
            }

            return result;
        }

        /// <summary>
        /// Legge PTS packet e, quando richiesto, i flag keyframe
        /// </summary>
        private PacketTimeline ReadPacketTimeline(string filePath, int timeoutMs, CancellationToken cancellationToken, bool includeFlags)
        {
            PacketTimeline result = new PacketTimeline();
            if (string.IsNullOrEmpty(this._ffprobePath))
                return result;

            string entries = includeFlags ? "packet=pts_time,flags" : "packet=pts_time";
            ProcessResult run = ProcessRunner.Run(this._ffprobePath, new string[] {
                "-v", "error", "-select_streams", "v:0", "-show_entries", entries, "-of", "csv=p=0", filePath }, timeoutMs, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (run.ExitCode != 0)
                return result;

            List<PacketEntry> packets = new List<PacketEntry>();
            string[] lines = run.Stdout.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string value = lines[i].Trim();
                if (string.IsNullOrEmpty(value))
                    continue;
                string[] fields = value.Split(',');
                if (!double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                    continue;
                packets.Add(new PacketEntry
                {
                    PtsMs = seconds * 1000.0,
                    KeyFrame = includeFlags && fields.Length > 1 && fields[1].IndexOf('K') >= 0,
                    OriginalIndex = i
                });
            }

            packets.Sort(ComparePackets);
            for (int i = 0; i < packets.Count; i++)
            {
                result.Timestamps.Add(packets[i].PtsMs);
                result.Keyframes.Add(packets[i].KeyFrame);
            }
            return result;
        }

        /// <summary>
        /// Legge i flag keyframe nello stesso ordine di presentazione dei PTS
        /// </summary>
        private List<bool> ReadKeyframeFlags(string filePath, int timeoutMs, CancellationToken cancellationToken)
        {
            return this.ReadPacketTimeline(filePath, timeoutMs, cancellationToken, true).Keyframes;
        }

        /// <summary>
        /// Legge dimensioni, aspect ratio e metadata colore del primo stream video
        /// </summary>
        private void ReadVideoProperties(string filePath, VideoFrameIndex index, int timeoutMs, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(this._ffprobePath))
                return;
            ProcessResult run = ProcessRunner.Run(this._ffprobePath, new string[] {
                "-v", "error", "-select_streams", "v:0",
                "-show_entries", "stream=width,height,sample_aspect_ratio,display_aspect_ratio,pix_fmt,bits_per_raw_sample,color_space,color_range,color_primaries,color_transfer",
                "-of", "json", filePath }, timeoutMs, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (run.ExitCode != 0 || string.IsNullOrWhiteSpace(run.Stdout))
                return;

            try
            {
                using JsonDocument document = JsonDocument.Parse(run.Stdout);
                if (!document.RootElement.TryGetProperty("streams", out JsonElement streams) || streams.GetArrayLength() == 0)
                    return;
                JsonElement stream = streams[0];
                index.CodedWidth = ReadInt(stream, "width");
                index.CodedHeight = ReadInt(stream, "height");
                index.SampleAspectRatio = ReadString(stream, "sample_aspect_ratio", "1:1");
                index.DisplayAspectRatio = ReadString(stream, "display_aspect_ratio", "");
                index.PixelFormat = ReadString(stream, "pix_fmt", "");
                index.BitDepth = ReadInt(stream, "bits_per_raw_sample");
                if (index.BitDepth <= 0)
                    index.BitDepth = InferBitDepth(index.PixelFormat);
                index.ColorSpace = ReadString(stream, "color_space", "");
                index.ColorRange = ReadString(stream, "color_range", "");
                index.ColorPrimaries = ReadString(stream, "color_primaries", "");
                index.ColorTransfer = ReadString(stream, "color_transfer", "");
                index.RequiresP010 = index.BitDepth > 8 || string.Equals(index.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase) || string.Equals(index.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
            }
        }

        /// <summary>
        /// Decodifica dal keyframe precedente e verifica il PTS ottenuto; in caso di seek impreciso riparte dall'inizio
        /// </summary>
        private List<VideoRawFrame> DecodeExactFrames(string filePath, VideoFrameIndex index, VideoFrameIndexEntry requested, int count, int width, int height, string pixelFormat, bool hardware, int timeoutMs, CancellationToken cancellationToken)
        {
            int keyFrameIndex = FindPreviousKeyFrame(index, requested.PresentationIndex);
            List<VideoRawFrame> result = this.RunFrameDecode(filePath, index, requested, count, keyFrameIndex, width, height, pixelFormat, hardware, timeoutMs, cancellationToken);
            if (FramesMatchIndex(result, index, requested.PresentationIndex))
                return result;

            result = this.RunFrameDecode(filePath, index, requested, count, 0, width, height, pixelFormat, hardware, timeoutMs, cancellationToken);
            return FramesMatchIndex(result, index, requested.PresentationIndex) ? result : null;
        }

        /// <summary>
        /// Esegue una singola decodifica raw a partire da un frame noto
        /// </summary>
        private List<VideoRawFrame> RunFrameDecode(string filePath, VideoFrameIndex index, VideoFrameIndexEntry requested, int count, int decodeStartIndex, int width, int height, string pixelFormat, bool hardware, int timeoutMs, CancellationToken cancellationToken)
        {
            List<string> arguments = new List<string>();
            arguments.Add("-nostdin");
            arguments.Add("-hide_banner");
            arguments.Add("-loglevel");
            arguments.Add("info");
            if (hardware)
            {
                arguments.Add("-hwaccel");
                arguments.Add(this._ffmpegConfig.HardwareAccelerationMethod);
            }
            if (decodeStartIndex > 0)
            {
                arguments.Add("-seek_timestamp");
                arguments.Add("1");
                arguments.Add("-ss");
                arguments.Add((index.Frames[decodeStartIndex].PtsMs / 1000.0).ToString("0.######", CultureInfo.InvariantCulture));
            }
            arguments.Add("-i");
            arguments.Add(filePath);
            arguments.Add("-copyts");
            arguments.Add("-map");
            arguments.Add("0:v:0");
            arguments.Add("-an");
            arguments.Add("-sn");
            arguments.Add("-dn");
            arguments.Add("-vf");
            string targetSeconds = (requested.PtsMs / 1000.0).ToString("0.######", CultureInfo.InvariantCulture);
            arguments.Add("select='gte(pts*TB," + targetSeconds + ")',scale=" + width.ToString(CultureInfo.InvariantCulture) + ":" + height.ToString(CultureInfo.InvariantCulture) + ":flags=lanczos,setsar=1,format=" + pixelFormat + ",showinfo");
            arguments.Add("-frames:v");
            arguments.Add(count.ToString(CultureInfo.InvariantCulture));
            arguments.Add("-fps_mode");
            arguments.Add("passthrough");
            arguments.Add("-f");
            arguments.Add("rawvideo");
            arguments.Add("-");

            using (MemoryStream output = new MemoryStream())
            {
                ProcessBinaryResult run = ProcessRunner.RunBinaryStdout(this._ffmpegPath, arguments.ToArray(), (buffer, count) => output.Write(buffer, 0, count), timeoutMs, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                int frameBytes = pixelFormat == "p010le" ? width * height * 3 : width * height * 3 / 2;
                if (run.ExitCode != 0 || output.Length != (long)frameBytes * count)
                    return null;

                MatchCollection matches = s_ptsTimeRegex.Matches(run.Stderr ?? "");
                if (matches.Count < count)
                    return null;
                byte[] raw = output.ToArray();
                List<VideoRawFrame> result = new List<VideoRawFrame>();
                for (int i = 0; i < count; i++)
                {
                    if (!double.TryParse(matches[i].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ptsSeconds))
                        return null;
                    VideoFrameIndexEntry actual = index.FindNearestFrame(ptsSeconds * 1000.0);
                    if (actual == null)
                        return null;
                    byte[] frameData = new byte[frameBytes];
                    Buffer.BlockCopy(raw, i * frameBytes, frameData, 0, frameBytes);
                    result.Add(new VideoRawFrame
                    {
                        Data = frameData,
                        PresentationIndex = actual.PresentationIndex,
                        PtsMs = ptsSeconds * 1000.0,
                        Width = width,
                        Height = height,
                        PixelFormat = pixelFormat
                    });
                }
                return result;
            }
        }

        /// <summary>
        /// Verifica che una sequenza decodificata corrisponda agli indici PTS richiesti
        /// </summary>
        private static bool FramesMatchIndex(List<VideoRawFrame> frames, VideoFrameIndex index, int startIndex)
        {
            if (frames == null || startIndex < 0 || startIndex + frames.Count > index.FrameCount)
                return false;
            for (int i = 0; i < frames.Count; i++)
            {
                VideoFrameIndexEntry expected = index.Frames[startIndex + i];
                if (frames[i].PresentationIndex != expected.PresentationIndex || Math.Abs(frames[i].PtsMs - expected.PtsMs) > Math.Max(0.5, expected.DurationMs * 0.1))
                    return false;
            }
            return frames.Count > 0;
        }

        /// <summary>
        /// Calcola dimensioni pari contenute nel box preservando SAR e aspect ratio
        /// </summary>
        private static void ResolveOutputDimensions(VideoFrameIndex index, int maximumWidth, int maximumHeight, out int width, out int height)
        {
            double sampleAspectRatio = ParseRatio(index.SampleAspectRatio, 1.0);
            double displayWidth = Math.Max(2.0, index.CodedWidth * sampleAspectRatio);
            double displayHeight = Math.Max(2.0, index.CodedHeight);
            double scale = Math.Min(maximumWidth / displayWidth, maximumHeight / displayHeight);
            scale = Math.Min(1.0, scale);
            width = Math.Max(2, (int)Math.Floor(displayWidth * scale / 2.0) * 2);
            height = Math.Max(2, (int)Math.Floor(displayHeight * scale / 2.0) * 2);
        }

        /// <summary>
        /// Converte un rapporto FFmpeg num:den in valore decimale
        /// </summary>
        private static double ParseRatio(string value, double fallback)
        {
            if (string.IsNullOrEmpty(value))
                return fallback;
            string[] parts = value.Split(':');
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator) || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator) || denominator == 0.0)
                return fallback;
            return numerator / denominator;
        }

        /// <summary>
        /// Trova il keyframe precedente più vicino
        /// </summary>
        private static int FindPreviousKeyFrame(VideoFrameIndex index, int presentationIndex)
        {
            for (int i = presentationIndex; i > 0; i--)
            {
                if (index.Frames[i].IsKeyFrame)
                    return i;
            }
            return 0;
        }

        /// <summary>
        /// Verifica che l'indice appartenga ancora alla stessa versione del file
        /// </summary>
        private static bool IsCurrentFile(VideoFrameIndex index, string filePath)
        {
            if (index == null || !string.Equals(index.FilePath, filePath, StringComparison.Ordinal) || !File.Exists(filePath))
                return false;
            FileInfo file = new FileInfo(filePath);
            return index.FileLength == file.Length && index.FileLastWriteTicks == file.LastWriteTimeUtc.Ticks;
        }

        /// <summary>
        /// Compone la chiave della cache raw
        /// </summary>
        private static string BuildCacheKey(string filePath, int frameIndex, int width, int height, string pixelFormat)
        {
            FileInfo file = new FileInfo(filePath);
            return filePath + "|" + file.Length.ToString(CultureInfo.InvariantCulture) + "|" + file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + frameIndex.ToString(CultureInfo.InvariantCulture) + "|" + width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture) + "|" + pixelFormat;
        }

        /// <summary>
        /// Compone un ETag senza esporre il path nel protocollo
        /// </summary>
        private static string BuildETag(string filePath, int frameIndex, int width, int height, string pixelFormat)
        {
            FileInfo file = new FileInfo(filePath);
            string value = file.Length.ToString("x", CultureInfo.InvariantCulture) + "-" + file.LastWriteTimeUtc.Ticks.ToString("x", CultureInfo.InvariantCulture) + "-" + frameIndex.ToString("x", CultureInfo.InvariantCulture) + "-" + width.ToString("x", CultureInfo.InvariantCulture) + "-" + height.ToString("x", CultureInfo.InvariantCulture) + "-" + pixelFormat;
            return "\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=') + "\"";
        }

        /// <summary>
        /// Legge e promuove un frame nella cache LRU
        /// </summary>
        private VideoRawFrame ReadCachedFrame(string key)
        {
            lock (this._rawFrames)
            {
                if (!this._rawFrames.TryGetValue(key, out RawCacheEntry entry))
                    return null;
                this._rawFrameOrder.Remove(entry.Node);
                this._rawFrameOrder.AddFirst(entry.Node);
                return entry.Frame;
            }
        }

        /// <summary>
        /// Inserisce un frame nella cache LRU entro il limite di memoria
        /// </summary>
        private void CacheFrame(string key, VideoRawFrame frame)
        {
            lock (this._rawFrames)
            {
                if (this._rawFrames.ContainsKey(key))
                    return;
                LinkedListNode<string> node = this._rawFrameOrder.AddFirst(key);
                this._rawFrames[key] = new RawCacheEntry { Frame = frame, Node = node };
                this._rawCacheBytes += frame.Data.Length;
                while (this._rawCacheBytes > RAW_CACHE_LIMIT_BYTES && this._rawFrameOrder.Last != null)
                {
                    string expiredKey = this._rawFrameOrder.Last.Value;
                    RawCacheEntry expired = this._rawFrames[expiredKey];
                    this._rawCacheBytes -= expired.Frame.Data.Length;
                    this._rawFrames.Remove(expiredKey);
                    this._rawFrameOrder.RemoveLast();
                }
            }
        }

        /// <summary>
        /// Calcola il passo mediano sui soli intervalli positivi
        /// </summary>
        private static double ComputeMedianFrameDuration(List<double> timestamps)
        {
            List<double> intervals = new List<double>();
            for (int i = 1; i < timestamps.Count; i++)
            {
                double interval = timestamps[i] - timestamps[i - 1];
                if (double.IsFinite(interval) && interval > 0.0)
                    intervals.Add(interval);
            }
            if (intervals.Count == 0)
                return 40.0;
            intervals.Sort();
            int middle = intervals.Count / 2;
            return intervals.Count % 2 == 1 ? intervals[middle] : (intervals[middle - 1] + intervals[middle]) / 2.0;
        }

        /// <summary>
        /// Trova la prima traccia video Matroska
        /// </summary>
        private static TrackInfo FindVideoTrack(MkvFileInfo fileInfo)
        {
            if (fileInfo == null || fileInfo.Tracks == null)
                return null;
            for (int i = 0; i < fileInfo.Tracks.Count; i++)
            {
                if (string.Equals(fileInfo.Tracks[i].Type, "video", StringComparison.OrdinalIgnoreCase))
                    return fileInfo.Tracks[i];
            }
            return null;
        }

        /// <summary>
        /// Confronta packet per PTS preservando l'ordine originale nei duplicati
        /// </summary>
        private static int ComparePackets(PacketEntry left, PacketEntry right)
        {
            int result = left.PtsMs.CompareTo(right.PtsMs);
            return result != 0 ? result : left.OriginalIndex.CompareTo(right.OriginalIndex);
        }

        /// <summary>
        /// Legge una proprietà JSON intera anche quando serializzata come stringa
        /// </summary>
        private static int ReadInt(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
                return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return number;
            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
        }

        /// <summary>
        /// Legge una proprietà JSON testuale con fallback
        /// </summary>
        private static string ReadString(JsonElement element, string propertyName, string fallback)
        {
            if (!element.TryGetProperty(propertyName, out JsonElement value))
                return fallback;
            string result = value.ToString();
            return string.IsNullOrEmpty(result) || string.Equals(result, "N/A", StringComparison.OrdinalIgnoreCase) ? fallback : result;
        }

        /// <summary>
        /// Deduce la profondità nominale dal nome del pixel format FFmpeg
        /// </summary>
        private static int InferBitDepth(string pixelFormat)
        {
            if (string.IsNullOrEmpty(pixelFormat))
                return 8;
            for (int bitDepth = 16; bitDepth >= 9; bitDepth--)
            {
                if (pixelFormat.IndexOf(bitDepth.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) >= 0)
                    return bitDepth;
            }
            return 8;
        }

        #endregion

        #region Classi annidate

        private class PacketTimeline
        {
            public PacketTimeline()
            {
                this.Timestamps = new List<double>();
                this.Keyframes = new List<bool>();
            }

            public List<double> Timestamps { get; set; }
            public List<bool> Keyframes { get; set; }
        }

        private class PacketEntry
        {
            public double PtsMs { get; set; }
            public bool KeyFrame { get; set; }
            public int OriginalIndex { get; set; }
        }

        private class RawCacheEntry
        {
            public VideoRawFrame Frame { get; set; }
            public LinkedListNode<string> Node { get; set; }
        }

        #endregion
    }
}
