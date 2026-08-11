using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media.Ffmpeg;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Decodifica ancore uniformi PTS-aware e landmark black-run per il nuovo nucleo SIFT
    /// </summary>
    public sealed class DeepSiftAnchorTimelineBuilder
    {

        #region Variabili statiche

        private static readonly Regex s_ptsTimeRegex = new Regex(@"showinfo@anchor_index.*?pts_time:(\-?\d+(?:\.\d+)?).*?duration_time:(N/A|\-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

        #endregion

        #region Variabili di classe

        private readonly string _ffmpegPath;
        private readonly string _mkvMergePath;
        private readonly string _mkvExtractPath;
        private readonly FfmpegConfig _ffmpegConfig;
        private readonly int _width;
        private readonly int _height;
        private readonly double _sampleStepSec;
        private readonly bool _geometryCropToFourThree;
        private readonly Action<List<byte[]>> _frameNormalizer;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore con preprocess SIFT deterministico
        /// </summary>
        /// <param name="ffmpegPath">Percorso FFmpeg</param>
        /// <param name="mkvMergePath">Percorso mkvmerge usato per il track id video</param>
        /// <param name="mkvExtractPath">Percorso mkvextract per timestamps_v2</param>
        /// <param name="ffmpegConfig">Configurazione FFmpeg</param>
        /// <param name="width">Larghezza SIFT</param>
        /// <param name="height">Altezza SIFT</param>
        /// <param name="sampleStepSec">Passo uniforme globale in secondi PTS</param>
        /// <param name="geometryCropToFourThree">True per applicare il crop geometrico 4:3 prima dello scale</param>
        /// <param name="frameNormalizer">Normalizzatore bordi neri sui frame estratti</param>
        public DeepSiftAnchorTimelineBuilder(string ffmpegPath, string mkvMergePath, string mkvExtractPath, FfmpegConfig ffmpegConfig, int width, int height, double sampleStepSec, bool geometryCropToFourThree, Action<List<byte[]>> frameNormalizer)
        {
            if (string.IsNullOrEmpty(ffmpegPath))
                throw new ArgumentException("Percorso FFmpeg mancante", nameof(ffmpegPath));
            if (ffmpegConfig == null)
                throw new ArgumentNullException(nameof(ffmpegConfig));
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));
            if (sampleStepSec <= 0.0 || double.IsNaN(sampleStepSec) || double.IsInfinity(sampleStepSec))
                throw new ArgumentOutOfRangeException(nameof(sampleStepSec));
            if (frameNormalizer == null)
                throw new ArgumentNullException(nameof(frameNormalizer));

            this._ffmpegPath = ffmpegPath;
            this._mkvMergePath = mkvMergePath ?? "";
            this._mkvExtractPath = mkvExtractPath ?? "";
            this._ffmpegConfig = ffmpegConfig;
            this._width = width;
            this._height = height;
            this._sampleStepSec = sampleStepSec;
            this._geometryCropToFourThree = geometryCropToFourThree;
            this._frameNormalizer = frameNormalizer;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Costruisce una timeline uniforme completa senza un secondo thinning delle ancore estratte
        /// </summary>
        /// <param name="filePath">Percorso del file video</param>
        /// <param name="manualCropPx">Crop manuale FFmpeg oppure stringa vuota</param>
        /// <returns>Timeline con tutte le ancore estratte al passo uniforme configurato</returns>
        public DeepSiftAnchorTimeline BuildUniform(string filePath, string manualCropPx)
        {
            return this.BuildInternal(filePath, manualCropPx);
        }

        private DeepSiftAnchorTimeline BuildInternal(string filePath, string manualCropPx)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("Percorso video mancante", nameof(filePath));

            Stopwatch phaseStopwatch = Stopwatch.StartNew();
            List<byte[]> frames;
            string stderr;
            this.ExtractCandidates(filePath, manualCropPx, 0.0, 0.0, out frames, out stderr);
            this._frameNormalizer(frames);

            long decodePreprocessMs = phaseStopwatch.ElapsedMilliseconds;
            phaseStopwatch.Restart();
            List<FrameTimestamp> timestamps = this.ParseTimestamps(stderr);
            if (timestamps.Count < frames.Count)
                throw new InvalidOperationException("Indice PTS SIFT non coerente con i frame selezionati");
            if (timestamps.Count > frames.Count)
                timestamps.RemoveRange(frames.Count, timestamps.Count - frames.Count);
            this.SortCandidatesByPts(frames, timestamps);
            List<DeepBlackTimelineRun> blackRuns = FfmpegBlackRunScanner.ParseDiagnostics(stderr);
            bool usedMkvTimestamps = this.TryApplyMkvTimestamps(filePath, timestamps);

            DeepSiftAnchorTimeline result = new DeepSiftAnchorTimeline();
            result.TimestampBackend = usedMkvTimestamps ? "mkvextract-timestamps_v2+ffmpeg-showinfo-global-sift" : "ffmpeg-showinfo-global-sift";
            result.DecodePreprocessMs = decodePreprocessMs;
            result.TimestampIndexMs = phaseStopwatch.ElapsedMilliseconds;
            result.DecodedFrameCount = frames.Count;
            result.BlackRuns = blackRuns;
            for (int i = 0; i < frames.Count; i++)
            {
                DeepSiftVisualAnchor anchor = new DeepSiftVisualAnchor();
                anchor.Index = i;
                anchor.FrameIndex = timestamps[i].OriginalFrameIndex;
                anchor.PtsMs = timestamps[i].PtsMs;
                anchor.FrameDurationMs = timestamps[i].DurationMs > 0.0 ? timestamps[i].DurationMs : this.ResolveFrameDuration(timestamps, i);
                anchor.DurationMs = i + 1 < timestamps.Count ? timestamps[i + 1].PtsMs - anchor.PtsMs : anchor.FrameDurationMs;
                if (anchor.DurationMs <= 0.0)
                    anchor.DurationMs = anchor.FrameDurationMs;
                anchor.Frame = frames[i];
                anchor.Width = this._width;
                anchor.Height = this._height;
                result.Anchors.Add(anchor);
            }

            result.SelectedFrameCount = result.Anchors.Count;
            return result;
        }

        #endregion

        #region Metodi privati
        private void ExtractCandidates(string filePath, string manualCropPx, double startSec, double maximumDurationSec, out List<byte[]> frames, out string stderr)
        {
            ProcessBinaryResult processResult = null;
            frames = new List<byte[]>();
            stderr = "";
            bool allowHardwareAcceleration = this.ShouldUseHardwareAcceleration();
            int attempts = allowHardwareAcceleration ? 2 : 1;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                bool useHardwareAcceleration = allowHardwareAcceleration && attempt == 0;
                List<string> arguments = this.BuildArguments(filePath, manualCropPx, useHardwareAcceleration, startSec, maximumDurationSec);
                frames.Clear();
                RawFrameCollector collector = new RawFrameCollector(this._width * this._height, frames);
                processResult = ProcessRunner.RunBinaryStdout(this._ffmpegPath, arguments.ToArray(), collector.Append, this._ffmpegConfig.FrameExtractionTimeoutMs);
                stderr = processResult != null ? processResult.Stderr : "";
                if (processResult != null && processResult.ExitCode == 0)
                    break;
            }

            if (processResult == null || processResult.ExitCode != 0)
                throw new InvalidOperationException("Indicizzazione globale SIFT FFmpeg fallita");
        }

        /// <summary>
        /// Costruisce la decodifica che unisce scene-cut e campionamento temporale
        /// </summary>
        private List<string> BuildArguments(string filePath, string manualCropPx, bool useHardwareAcceleration, double startSec, double maximumDurationSec)
        {
            List<string> result = new List<string>();
            result.Add("-nostdin");
            result.Add("-hide_banner");
            if (startSec > 0.0)
            {
                result.Add("-ss");
                result.Add(startSec.ToString("F6", CultureInfo.InvariantCulture));
            }
            if (maximumDurationSec > 0.0)
            {
                result.Add("-t");
                result.Add(maximumDurationSec.ToString("F6", CultureInfo.InvariantCulture));
            }
            if (useHardwareAcceleration)
            {
                result.Add("-hwaccel");
                result.Add(this._ffmpegConfig.HardwareAccelerationMethod);
            }
            result.Add("-i");
            result.Add(filePath);
            result.Add("-copyts");
            result.Add("-an");
            result.Add("-sn");
            result.Add("-dn");
            result.Add("-filter_complex");
            result.Add(this.BuildFilter(manualCropPx));
            result.Add("-map");
            result.Add("[anchor_output]");
            result.Add("-fps_mode");
            result.Add("passthrough");
            result.Add("-f");
            result.Add("rawvideo");
            result.Add("-");
            return result;
        }

        /// <summary>
        /// Applica crop, normalizzazione SAR e campionamento PTS-aware
        /// </summary>
        private string BuildFilter(string manualCropPx)
        {
            string common = this.BuildCommonPreprocess(manualCropPx);
            double denseStepSec = Math.Min(this._sampleStepSec, 0.25);
            string select = "select='isnan(prev_selected_t)+gte(t,prev_selected_t+" + denseStepSec.ToString("F6", CultureInfo.InvariantCulture) + ")'";
            return "[0:v:0]split=2[anchor_input][black_input];" +
                "[anchor_input]" + select + "," + common + "scale=" + this._width.ToString(CultureInfo.InvariantCulture) + ":" + this._height.ToString(CultureInfo.InvariantCulture) + ":flags=bilinear+accurate_rnd+full_chroma_int+bitexact,format=gray,showinfo@anchor_index[anchor_output];" +
                "[black_input]" + common + FfmpegBlackRunScanner.ANALYSIS_FILTER + ",nullsink";
        }

        private string BuildCommonPreprocess(string manualCropPx)
        {
            string crop = "";
            if (Options.TryParseAnalysisCropPx(manualCropPx, out int left, out int right, out int top, out int bottom) && (left != 0 || right != 0 || top != 0 || bottom != 0))
            {
                crop = "crop=iw-" + left.ToString(CultureInfo.InvariantCulture) + "-" + right.ToString(CultureInfo.InvariantCulture) + ":ih-" + top.ToString(CultureInfo.InvariantCulture) + "-" + bottom.ToString(CultureInfo.InvariantCulture) + ":" + left.ToString(CultureInfo.InvariantCulture) + ":" + top.ToString(CultureInfo.InvariantCulture) + ",";
            }
            else if (this._geometryCropToFourThree)
            {
                crop = "crop=ih*4/3:ih,";
            }
            return crop + "scale=w='trunc(iw*sar/2)*2':h=ih:flags=bilinear+accurate_rnd+full_chroma_int+bitexact,setsar=1,";
        }

        /// <summary>
        /// Usa il decoder hardware solo quando non impone un download VideoToolbox nella catena CPU
        /// </summary>
        private bool ShouldUseHardwareAcceleration()
        {
            return this._ffmpegConfig.HardwareAcceleration &&
                   !string.IsNullOrWhiteSpace(this._ffmpegConfig.HardwareAccelerationMethod);
        }

        /// <summary>
        /// Legge PTS e durata dalle righe showinfo
        /// </summary>
        private List<FrameTimestamp> ParseTimestamps(string stderr)
        {
            List<FrameTimestamp> result = new List<FrameTimestamp>();
            MatchCollection matches = s_ptsTimeRegex.Matches(stderr ?? "");
            for (int i = 0; i < matches.Count; i++)
            {
                if (!double.TryParse(matches[i].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ptsSec))
                    continue;
                double durationMs = 0.0;
                if (double.TryParse(matches[i].Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double durationSec))
                    durationMs = durationSec * 1000.0;
                result.Add(new FrameTimestamp { PtsMs = ptsSec * 1000.0, DurationMs = durationMs, OriginalFrameIndex = result.Count });
            }

            return result;
        }

        private void SortCandidatesByPts(List<byte[]> frames, List<FrameTimestamp> timestamps)
        {
            List<CandidateFrame> candidates = new List<CandidateFrame>(frames.Count);
            for (int i = 0; i < frames.Count; i++)
                candidates.Add(new CandidateFrame { Frame = frames[i], Timestamp = timestamps[i] });
            candidates.Sort((left, right) => left.Timestamp.PtsMs.CompareTo(right.Timestamp.PtsMs));
            frames.Clear();
            timestamps.Clear();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (timestamps.Count > 0 && Math.Abs(timestamps[timestamps.Count - 1].PtsMs - candidates[i].Timestamp.PtsMs) < 0.001)
                    continue;
                frames.Add(candidates[i].Frame);
                timestamps.Add(candidates[i].Timestamp);
            }
        }

        /// <summary>
        /// Sostituisce i PTS showinfo con quelli Matroska quando timestamps_v2 è disponibile
        /// </summary>
        private bool TryApplyMkvTimestamps(string filePath, List<FrameTimestamp> selectedTimestamps)
        {
            if (selectedTimestamps.Count == 0 || string.IsNullOrEmpty(this._mkvMergePath) || string.IsNullOrEmpty(this._mkvExtractPath))
                return false;
            string extension = Path.GetExtension(filePath);
            if (!string.Equals(extension, ".mkv", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".webm", StringComparison.OrdinalIgnoreCase))
                return false;

            List<double> allTimestamps = this.ReadMkvTimestamps(filePath);
            if (allTimestamps.Count == 0)
                return false;

            double[] resolvedPtsMs = new double[selectedTimestamps.Count];
            double[] resolvedDurationsMs = new double[selectedTimestamps.Count];
            int searchStartIndex = 0;
            for (int selectedIndex = 0; selectedIndex < selectedTimestamps.Count; selectedIndex++)
            {
                int nearestIndex = -1;
                double nearestDistanceMs = double.MaxValue;
                for (int fullIndex = searchStartIndex; fullIndex < allTimestamps.Count; fullIndex++)
                {
                    double distanceMs = Math.Abs(allTimestamps[fullIndex] - selectedTimestamps[selectedIndex].PtsMs);
                    if (distanceMs < nearestDistanceMs)
                    {
                        nearestIndex = fullIndex;
                        nearestDistanceMs = distanceMs;
                    }
                    if (allTimestamps[fullIndex] > selectedTimestamps[selectedIndex].PtsMs && distanceMs > nearestDistanceMs)
                        break;
                }

                if (nearestIndex < 0 || nearestDistanceMs > 2.0)
                    return false;

                resolvedPtsMs[selectedIndex] = allTimestamps[nearestIndex];
                resolvedDurationsMs[selectedIndex] = this.ResolveMkvFrameDuration(allTimestamps, nearestIndex);
                searchStartIndex = nearestIndex + 1;
            }

            for (int selectedIndex = 0; selectedIndex < selectedTimestamps.Count; selectedIndex++)
            {
                selectedTimestamps[selectedIndex].PtsMs = resolvedPtsMs[selectedIndex];
                selectedTimestamps[selectedIndex].DurationMs = resolvedDurationsMs[selectedIndex];
            }

            return true;
        }

        /// <summary>
        /// Estrae la sequenza PTS del primo video track Matroska
        /// </summary>
        private List<double> ReadMkvTimestamps(string filePath)
        {
            List<double> result = new List<double>();
            string temporaryPath = Path.Combine(Path.GetTempPath(), "remuxforge-sift-" + Guid.NewGuid().ToString("N") + ".timestamps");
            try
            {
                MkvFileInfo fileInfo = new MkvToolsService(this._mkvMergePath).GetFileInfo(filePath);
                TrackInfo videoTrack = null;
                if (fileInfo != null && fileInfo.Tracks != null)
                {
                    for (int i = 0; i < fileInfo.Tracks.Count; i++)
                    {
                        if (string.Equals(fileInfo.Tracks[i].Type, "video", StringComparison.OrdinalIgnoreCase))
                        {
                            videoTrack = fileInfo.Tracks[i];
                            break;
                        }
                    }
                }
                if (videoTrack == null)
                    return result;

                ProcessResult processResult = ProcessRunner.Run(this._mkvExtractPath, new string[] { filePath, "timestamps_v2", videoTrack.Id.ToString(CultureInfo.InvariantCulture) + ":" + temporaryPath }, this._ffmpegConfig.FrameExtractionTimeoutMs);
                if (processResult.ExitCode != 0 || !File.Exists(temporaryPath))
                    return result;

                result.AddRange(this.ParseMkvTimestampLines(File.ReadAllLines(temporaryPath)));
            }
            catch
            {
                result.Clear();
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }

            return result;
        }

        /// <summary>
        /// Converte le righe timestamps_v2, espresse in millisecondi decimali
        /// </summary>
        private List<double> ParseMkvTimestampLines(IEnumerable<string> lines)
        {
            List<double> result = new List<double>();
            if (lines == null)
                return result;
            foreach (string line in lines)
            {
                string value = line != null ? line.Trim().TrimStart('\uFEFF') : "";
                if (string.IsNullOrEmpty(value) || value[0] == '#')
                    continue;
                int separatorIndex = value.IndexOfAny(new char[] { ' ', '\t', ',', ';' });
                if (separatorIndex >= 0)
                    value = value.Substring(0, separatorIndex);
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double timestampMs))
                    result.Add(timestampMs);
            }

            return result;
        }

        /// <summary>
        /// Calcola la durata PTS del frame Matroska indicizzato
        /// </summary>
        private double ResolveMkvFrameDuration(List<double> timestamps, int index)
        {
            if (index + 1 < timestamps.Count && timestamps[index + 1] > timestamps[index])
                return timestamps[index + 1] - timestamps[index];
            if (index > 0 && timestamps[index] > timestamps[index - 1])
                return timestamps[index] - timestamps[index - 1];
            return 0.0;
        }

        /// <summary>
        /// Risolve una durata dal PTS successivo quando showinfo non la espone
        /// </summary>
        private double ResolveFrameDuration(List<FrameTimestamp> timestamps, int index)
        {
            if (index + 1 < timestamps.Count)
            {
                double delta = timestamps[index + 1].PtsMs - timestamps[index].PtsMs;
                if (delta > 0.0)
                    return delta;
            }
            if (index > 0)
            {
                double previous = timestamps[index].PtsMs - timestamps[index - 1].PtsMs;
                if (previous > 0.0)
                    return previous;
            }
            return 40.0;
        }

        #endregion

        #region Classi annidate

        private class FrameTimestamp
        {
            public double PtsMs { get; set; }
            public double DurationMs { get; set; }
            public int OriginalFrameIndex { get; set; }
        }

        private class CandidateFrame
        {
            public byte[] Frame { get; set; }
            public FrameTimestamp Timestamp { get; set; }
        }

        private class RawFrameCollector
        {
            private readonly int _frameSize;
            private readonly List<byte[]> _frames;
            private byte[] _frame;
            private int _written;

            public RawFrameCollector(int frameSize, List<byte[]> frames)
            {
                this._frameSize = frameSize;
                this._frames = frames;
                this._frame = new byte[frameSize];
            }

            public void Append(byte[] buffer, int bytesRead)
            {
                int offset = 0;
                while (offset < bytesRead)
                {
                    int copyLength = Math.Min(this._frameSize - this._written, bytesRead - offset);
                    Buffer.BlockCopy(buffer, offset, this._frame, this._written, copyLength);
                    this._written += copyLength;
                    offset += copyLength;
                    if (this._written == this._frameSize)
                    {
                        this._frames.Add(this._frame);
                        this._frame = new byte[this._frameSize];
                        this._written = 0;
                    }
                }
            }
        }

        #endregion
    }
}
