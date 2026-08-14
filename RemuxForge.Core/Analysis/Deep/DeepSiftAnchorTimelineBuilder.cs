using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
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
    /// Costruisce la timeline globale SIFT con ancore uniformi, PTS e intervalli di nero
    /// </summary>
    public sealed class DeepSiftAnchorTimelineBuilder
    {

        #region Variabili statiche

        /// <summary>
        /// Individua PTS e durata nelle righe showinfo prodotte per le ancore
        /// </summary>
        private static readonly Regex s_ptsTimeRegex = new Regex(@"showinfo@anchor_index.*?pts_time:(\-?\d+(?:\.\d+)?).*?duration_time:(N/A|\-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso dell'eseguibile FFmpeg usato per estrarre i frame
        /// </summary>
        private readonly string _ffmpegPath;

        /// <summary>
        /// Percorso di mkvmerge usato per individuare il primo track video Matroska
        /// </summary>
        private readonly string _mkvMergePath;

        /// <summary>
        /// Percorso di mkvextract usato per leggere i timestamps_v2 del track video
        /// </summary>
        private readonly string _mkvExtractPath;

        /// <summary>
        /// Configurazione usata per la decodifica e l'estrazione tramite FFmpeg
        /// </summary>
        private readonly FfmpegConfig _ffmpegConfig;

        /// <summary>
        /// Larghezza in pixel dei frame SIFT normalizzati
        /// </summary>
        private readonly int _width;

        /// <summary>
        /// Altezza in pixel dei frame SIFT normalizzati
        /// </summary>
        private readonly int _height;

        /// <summary>
        /// Passo minimo del campionamento globale espresso in secondi
        /// </summary>
        private readonly double _sampleStepSec;

        /// <summary>
        /// Indica se applicare il crop geometrico 4:3 quando manca un crop manuale
        /// </summary>
        private readonly bool _geometryCropToFourThree;

        /// <summary>
        /// Normalizzatore dei bordi neri applicato ai frame estratti
        /// </summary>
        private readonly Action<List<byte[]>> _frameNormalizer;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza il builder con il preprocess SIFT deterministico
        /// </summary>
        /// <param name="ffmpegPath">Percorso FFmpeg</param>
        /// <param name="mkvMergePath">Percorso di mkvmerge per individuare il track video</param>
        /// <param name="mkvExtractPath">Percorso di mkvextract per leggere i timestamps_v2</param>
        /// <param name="ffmpegConfig">Configurazione della decodifica e del timeout FFmpeg</param>
        /// <param name="width">Larghezza in pixel dei frame SIFT</param>
        /// <param name="height">Altezza in pixel dei frame SIFT</param>
        /// <param name="sampleStepSec">Passo minimo del campionamento globale in secondi PTS</param>
        /// <param name="geometryCropToFourThree">Indica se applicare il crop geometrico 4:3 prima dello scale</param>
        /// <param name="frameNormalizer">Normalizzatore dei bordi neri applicato ai frame estratti</param>
        public DeepSiftAnchorTimelineBuilder(string ffmpegPath, string mkvMergePath, string mkvExtractPath, FfmpegConfig ffmpegConfig, int width, int height, double sampleStepSec, bool geometryCropToFourThree, Action<List<byte[]>> frameNormalizer)
        {
            if (string.IsNullOrEmpty(ffmpegPath))
                throw new ArgumentException(AppText.T("analysis.sift.missingFfmpegPath"), nameof(ffmpegPath));
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
        /// Costruisce la timeline uniforme completa senza ridurre nuovamente le ancore estratte
        /// </summary>
        /// <param name="filePath">Percorso del file video</param>
        /// <param name="manualCropPx">Crop manuale FFmpeg oppure stringa vuota per disabilitarlo</param>
        /// <returns>Timeline con le ancore estratte al passo uniforme configurato</returns>
        public DeepSiftAnchorTimeline BuildUniform(string filePath, string manualCropPx)
        {
            return this.BuildInternal(filePath, manualCropPx);
        }

        /// <summary>
        /// Estrae e normalizza i frame, quindi associa PTS, durate e intervalli neri alla timeline
        /// </summary>
        /// <param name="filePath">Percorso del file video</param>
        /// <param name="manualCropPx">Crop manuale FFmpeg oppure stringa vuota per disabilitarlo</param>
        /// <returns>Timeline uniforme con ancore indicizzate per PTS</returns>
        private DeepSiftAnchorTimeline BuildInternal(string filePath, string manualCropPx)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException(AppText.T("deep.temporal.argument.missingVideoPath"), nameof(filePath));

            Stopwatch phaseStopwatch = Stopwatch.StartNew();
            List<byte[]> frames;
            string stderr;
            this.ExtractCandidates(filePath, manualCropPx, 0.0, 0.0, out frames, out stderr);
            this._frameNormalizer(frames);

            long decodePreprocessMs = phaseStopwatch.ElapsedMilliseconds;
            phaseStopwatch.Restart();
            List<FrameTimestamp> timestamps = this.ParseTimestamps(stderr);
            if (timestamps.Count < frames.Count)
                throw new InvalidOperationException(AppText.T("deep.temporal.timeline.inconsistentPtsIndex"));
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
        /// <summary>
        /// Decodifica i frame candidati e conserva le diagnostiche showinfo e blackdetect, riprovando senza accelerazione hardware se necessario
        /// </summary>
        /// <param name="filePath">Percorso del file video</param>
        /// <param name="manualCropPx">Crop manuale FFmpeg oppure stringa vuota per disabilitarlo</param>
        /// <param name="startSec">Posizione iniziale della decodifica in secondi</param>
        /// <param name="maximumDurationSec">Durata massima da decodificare oppure zero</param>
        /// <param name="frames">Elenco dei frame raw estratti e ricomposti</param>
        /// <param name="stderr">Diagnostica FFmpeg associata all'estrazione</param>
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
                if (useHardwareAcceleration)
                    ConsoleHelper.Write(LogSection.Deep, LogLevel.Notice, AppText.F("deep.temporal.ffmpeg.hardwareAccelerationRetry", this.GetLastErrorLine(stderr)));
            }

            if (processResult == null || processResult.ExitCode != 0)
                throw new InvalidOperationException(AppText.T("deep.temporal.timeline.globalIndexingFailed"));
        }

        /// <summary>
        /// Prepara gli argomenti FFmpeg per la decodifica e il campionamento temporale globale
        /// </summary>
        /// <param name="filePath">Percorso del file video</param>
        /// <param name="manualCropPx">Crop manuale FFmpeg oppure stringa vuota per disabilitarlo</param>
        /// <param name="useHardwareAcceleration">Indica se richiedere il metodo di accelerazione hardware configurato</param>
        /// <param name="startSec">Posizione iniziale della decodifica in secondi</param>
        /// <param name="maximumDurationSec">Durata massima da decodificare oppure zero</param>
        /// <returns>Argomenti da passare a FFmpeg</returns>
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
        /// Costruisce il grafo FFmpeg per il campionamento delle ancore e l'analisi degli intervalli neri
        /// </summary>
        /// <param name="manualCropPx">Crop manuale FFmpeg oppure stringa vuota per disabilitarlo</param>
        /// <returns>Grafo di filtri con preprocess condiviso, ancore campionate e blackdetect</returns>
        private string BuildFilter(string manualCropPx)
        {
            string common = this.BuildCommonPreprocess(manualCropPx);
            string select = "select='isnan(prev_selected_t)+gte(t,prev_selected_t+" + this._sampleStepSec.ToString("F6", CultureInfo.InvariantCulture) + ")'";
            return "[0:v:0]split=2[anchor_input][black_input];" +
                "[anchor_input]" + select + "," + common + "scale=" + this._width.ToString(CultureInfo.InvariantCulture) + ":" + this._height.ToString(CultureInfo.InvariantCulture) + ":flags=bilinear+accurate_rnd+full_chroma_int+bitexact,format=gray,showinfo@anchor_index[anchor_output];" +
                "[black_input]" + common + FfmpegBlackRunScanner.ANALYSIS_FILTER + ",nullsink";
        }

        /// <summary>
        /// Costruisce il preprocess condiviso dai rami delle ancore e di blackdetect
        /// </summary>
        /// <param name="manualCropPx">Crop manuale FFmpeg oppure stringa vuota per disabilitarlo</param>
        /// <returns>Filtri di crop, normalizzazione SAR e ridimensionamento preliminare</returns>
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
        /// Determina se richiedere a FFmpeg il metodo di accelerazione hardware configurato
        /// </summary>
        /// <returns>true quando l'accelerazione è abilitata e il metodo è valorizzato</returns>
        private bool ShouldUseHardwareAcceleration()
        {
            return this._ffmpegConfig.HardwareAcceleration &&
                   !string.IsNullOrWhiteSpace(this._ffmpegConfig.HardwareAccelerationMethod);
        }

        /// <summary>
        /// Recupera l'ultima riga non vuota della diagnostica FFmpeg
        /// </summary>
        /// <param name="text">Testo diagnostico completo</param>
        /// <returns>Ultima riga non vuota oppure messaggio localizzato quando la diagnostica è vuota</returns>
        private string GetLastErrorLine(string text)
        {
            string result = AppText.T("deep.temporal.ffmpeg.noDetails");
            string[] lines = (text ?? "").Replace("\r", "").Split('\n');
            for (int lineIndex = lines.Length - 1; lineIndex >= 0; lineIndex--)
            {
                if (string.IsNullOrWhiteSpace(lines[lineIndex]))
                    continue;
                result = lines[lineIndex].Trim();
                break;
            }
            return result;
        }

        /// <summary>
        /// Legge PTS e durata dalle righe showinfo e conserva l'indice originale dei frame
        /// </summary>
        /// <param name="stderr">Diagnostica FFmpeg contenente showinfo</param>
        /// <returns>Metadati temporali nell'ordine prodotto da FFmpeg</returns>
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

        /// <summary>
        /// Riordina frame e metadati per PTS eliminando i duplicati temporali
        /// </summary>
        /// <param name="frames">Frame da riordinare</param>
        /// <param name="timestamps">Metadati temporali paralleli ai frame</param>
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
        /// Sostituisce PTS e durate showinfo con i valori Matroska quando la corrispondenza è affidabile
        /// </summary>
        /// <param name="filePath">Percorso del contenitore video</param>
        /// <param name="selectedTimestamps">Metadati temporali delle ancore selezionate</param>
        /// <returns>true quando tutti i metadati Matroska sono stati applicati</returns>
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
        /// Estrae la sequenza PTS del primo track video Matroska tramite timestamps_v2
        /// </summary>
        /// <param name="filePath">Percorso del contenitore Matroska</param>
        /// <returns>Sequenza dei timestamps_v2 validi espressi in millisecondi</returns>
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
        /// Converte le righe timestamps_v2 in valori numerici espressi in millisecondi
        /// </summary>
        /// <param name="lines">Righe prodotte da mkvextract, eventualmente contenenti commenti o colonne aggiuntive</param>
        /// <returns>Valori numerici validi in millisecondi</returns>
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
        /// Calcola la durata del frame Matroska dai PTS adiacenti
        /// </summary>
        /// <param name="timestamps">Timeline Matroska completa</param>
        /// <param name="index">Indice del frame richiesto</param>
        /// <returns>Durata dedotta dai PTS adiacenti oppure zero quando non è disponibile</returns>
        private double ResolveMkvFrameDuration(List<double> timestamps, int index)
        {
            if (index + 1 < timestamps.Count && timestamps[index + 1] > timestamps[index])
                return timestamps[index + 1] - timestamps[index];
            if (index > 0 && timestamps[index] > timestamps[index - 1])
                return timestamps[index] - timestamps[index - 1];
            return 0.0;
        }

        /// <summary>
        /// Risolve la durata del frame dai PTS adiacenti quando showinfo non la espone
        /// </summary>
        /// <param name="timestamps">Timestamp delle ancore selezionate</param>
        /// <param name="index">Indice dell'ancora richiesta</param>
        /// <returns>Durata dedotta dai PTS adiacenti oppure il fallback di 40 ms</returns>
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

        /// <summary>
        /// Raggruppa PTS, durata e indice originale di un frame estratto
        /// </summary>
        private class FrameTimestamp
        {
            /// <summary>
            /// PTS del frame espresso in millisecondi
            /// </summary>
            public double PtsMs { get; set; }

            /// <summary>
            /// Durata del frame espressa in millisecondi
            /// </summary>
            public double DurationMs { get; set; }

            /// <summary>
            /// Indice del frame nell'ordine originale di estrazione
            /// </summary>
            public int OriginalFrameIndex { get; set; }
        }

        /// <summary>
        /// Associa un buffer visivo ai relativi metadati temporali durante il riordino
        /// </summary>
        private class CandidateFrame
        {
            /// <summary>
            /// Buffer in scala di grigi del frame
            /// </summary>
            public byte[] Frame { get; set; }

            /// <summary>
            /// Metadati temporali associati al buffer
            /// </summary>
            public FrameTimestamp Timestamp { get; set; }
        }

        /// <summary>
        /// Ricompone frame raw completi dai blocchi letti dallo standard output di FFmpeg
        /// </summary>
        private class RawFrameCollector
        {
            /// <summary>
            /// Dimensione attesa in byte di un singolo frame raw
            /// </summary>
            private readonly int _frameSize;

            /// <summary>
            /// Elenco in cui accodare i frame completi
            /// </summary>
            private readonly List<byte[]> _frames;

            /// <summary>
            /// Buffer del frame attualmente in costruzione
            /// </summary>
            private byte[] _frame;

            /// <summary>
            /// Numero di byte già ricevuti nel frame corrente
            /// </summary>
            private int _written;

            /// <summary>
            /// Inizializza il ricompositore dei frame raw
            /// </summary>
            /// <param name="frameSize">Dimensione in byte di un frame completo</param>
            /// <param name="frames">Destinazione dei frame completi</param>
            public RawFrameCollector(int frameSize, List<byte[]> frames)
            {
                this._frameSize = frameSize;
                this._frames = frames;
                this._frame = new byte[frameSize];
            }

            /// <summary>
            /// Accoda un blocco di dati e trasferisce nell'elenco ogni frame completato
            /// </summary>
            /// <param name="buffer">Buffer ricevuto dal processo FFmpeg</param>
            /// <param name="bytesRead">Numero di byte validi presenti nel buffer</param>
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
