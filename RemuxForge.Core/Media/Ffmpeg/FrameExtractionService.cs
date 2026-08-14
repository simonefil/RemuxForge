using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Media.Ffmpeg
{
    /// <summary>
    /// Estrae frame grayscale e timestamp reali tramite ffmpeg
    /// </summary>
    public class FrameExtractionService
    {
        #region Variabili statiche

        /// <summary>
        /// Regex per parsing pts_time dalle righe showinfo nello stderr ffmpeg
        /// </summary>
        private static readonly Regex s_ptsTimeRegex = new Regex(@"pts_time:(\-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso dell'eseguibile ffmpeg
        /// </summary>
        private string _ffmpegPath;

        /// <summary>
        /// Configurazione della normalizzazione dei frame VideoSync
        /// </summary>
        private VideoSyncConfig _videoSyncConfig;

        /// <summary>
        /// Configurazione di esecuzione di ffmpeg
        /// </summary>
        private FfmpegConfig _ffmpegConfig;

        /// <summary>
        /// Sezione del log in cui riportare gli errori di estrazione
        /// </summary>
        private LogSection _logSection;

        /// <summary>
        /// Indica se è già stato registrato il primo fallback all'accelerazione software
        /// </summary>
        private static bool s_reportedHwAccelFallback;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza il servizio di estrazione dei frame
        /// </summary>
        /// <param name="ffmpegPath">Percorso dell'eseguibile ffmpeg</param>
        /// <param name="videoSyncConfig">Configurazione della normalizzazione dei frame</param>
        /// <param name="ffmpegConfig">Configurazione di esecuzione di ffmpeg</param>
        /// <param name="logSection">Sezione del log per gli errori di estrazione</param>
        public FrameExtractionService(string ffmpegPath, VideoSyncConfig videoSyncConfig, FfmpegConfig ffmpegConfig, LogSection logSection)
        {
            this._ffmpegPath = ffmpegPath;
            this._videoSyncConfig = videoSyncConfig;
            this._ffmpegConfig = ffmpegConfig;
            this._logSection = logSection;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Estrae frame di un segmento video applicando un eventuale crop manuale prima dello scale
        /// </summary>
        /// <param name="filePath">Percorso del file video da elaborare</param>
        /// <param name="startMs">Punto di inizio del segmento in millisecondi</param>
        /// <param name="durationSec">Durata del segmento in secondi</param>
        /// <param name="targetFps">Frequenza di campionamento dei frame, oppure zero per mantenere quella sorgente</param>
        /// <param name="geometryCropToFourThree">Indica se applicare il crop geometrico in rapporto quattro a tre</param>
        /// <param name="manualCropPx">Crop manuale nel formato sinistra:destra:alto:basso, oppure valore non configurato</param>
        /// <param name="frames">Frame grayscale estratti</param>
        /// <param name="timestampsMs">Timestamp PTS dei frame estratti in millisecondi</param>
        public void ExtractSegment(string filePath, int startMs, double durationSec, double targetFps, bool geometryCropToFourThree, string manualCropPx, out List<byte[]> frames, out double[] timestampsMs)
        {
            this.ExtractSegmentCore(filePath, startMs, durationSec, targetFps, 0.0, geometryCropToFourThree, manualCropPx, out frames, out timestampsMs);
        }

        /// <summary>
        /// Estrae un segmento e distingue un fallimento FFmpeg da un risultato valido
        /// </summary>
        /// <param name="filePath">Percorso del file video da elaborare</param>
        /// <param name="startMs">Punto di inizio del segmento in millisecondi</param>
        /// <param name="durationSec">Durata del segmento in secondi</param>
        /// <param name="targetFps">Frequenza di campionamento dei frame, oppure zero per mantenere quella sorgente</param>
        /// <param name="geometryCropToFourThree">Indica se applicare il crop geometrico in rapporto quattro a tre</param>
        /// <param name="manualCropPx">Crop manuale nel formato sinistra:destra:alto:basso, oppure valore non configurato</param>
        /// <param name="frames">Frame grayscale estratti</param>
        /// <param name="timestampsMs">Timestamp PTS dei frame estratti in millisecondi</param>
        /// <returns>true se l'estrazione è riuscita e frame e timestamp sono coerenti</returns>
        public bool TryExtractSegment(string filePath, int startMs, double durationSec, double targetFps, bool geometryCropToFourThree, string manualCropPx, out List<byte[]> frames, out double[] timestampsMs)
        {
            return this.ExtractSegmentCore(filePath, startMs, durationSec, targetFps, 0.0, geometryCropToFourThree, manualCropPx, out frames, out timestampsMs);
        }

        /// <summary>
        /// Estrae frame a intervalli temporali regolari conservando i PTS dei frame selezionati
        /// </summary>
        /// <param name="filePath">Percorso del file video da elaborare</param>
        /// <param name="startMs">Punto di inizio del segmento in millisecondi</param>
        /// <param name="durationSec">Durata del segmento in secondi</param>
        /// <param name="sampleIntervalSec">Intervallo tra i frame selezionati in secondi</param>
        /// <param name="geometryCropToFourThree">Indica se applicare il crop geometrico in rapporto quattro a tre</param>
        /// <param name="manualCropPx">Crop manuale nel formato sinistra:destra:alto:basso, oppure valore non configurato</param>
        /// <param name="frames">Frame grayscale estratti</param>
        /// <param name="timestampsMs">Timestamp PTS dei frame estratti in millisecondi</param>
        public void ExtractSegmentAtInterval(string filePath, int startMs, double durationSec, double sampleIntervalSec, bool geometryCropToFourThree, string manualCropPx, out List<byte[]> frames, out double[] timestampsMs)
        {
            if (sampleIntervalSec <= 0.0 || double.IsNaN(sampleIntervalSec) || double.IsInfinity(sampleIntervalSec))
                throw new ArgumentOutOfRangeException(nameof(sampleIntervalSec));
            this.ExtractSegmentCore(filePath, startMs, durationSec, 0.0, sampleIntervalSec, geometryCropToFourThree, manualCropPx, out frames, out timestampsMs);
        }

        /// <summary>
        /// Esegue l'estrazione del segmento con il filtro richiesto e il fallback software
        /// </summary>
        /// <param name="filePath">Percorso del file video da elaborare</param>
        /// <param name="startMs">Punto di inizio del segmento in millisecondi</param>
        /// <param name="durationSec">Durata del segmento in secondi</param>
        /// <param name="targetFps">Frequenza di campionamento dei frame, oppure zero per usare l'intervallo</param>
        /// <param name="sampleIntervalSec">Intervallo tra i frame selezionati in secondi, oppure zero per usare il frame rate</param>
        /// <param name="geometryCropToFourThree">Indica se applicare il crop geometrico in rapporto quattro a tre</param>
        /// <param name="manualCropPx">Crop manuale nel formato sinistra:destra:alto:basso, oppure valore non configurato</param>
        /// <param name="frames">Frame grayscale estratti</param>
        /// <param name="timestampsMs">Timestamp PTS dei frame estratti in millisecondi</param>
        /// <returns>true se l'estrazione è riuscita e frame e timestamp sono coerenti</returns>
        private bool ExtractSegmentCore(string filePath, int startMs, double durationSec, double targetFps, double sampleIntervalSec, bool geometryCropToFourThree, string manualCropPx, out List<byte[]> frames, out double[] timestampsMs)
        {
            frames = new List<byte[]>();
            timestampsMs = new double[0];
            ProcessBinaryResult processResult;
            double startSec;
            double endSec;
            string startFormatted;
            string endFormatted;
            string resolution;
            string filterChain;
            int frameSize = this._videoSyncConfig.FrameWidth * this._videoSyncConfig.FrameHeight;
            List<byte[]> extractedFrames = frames;
            List<string> args = new List<string>();
            string stderrText;
            MatchCollection ptsMatches;
            List<double> tsList = new List<double>();
            double ptsSec;
            int minCount;
            bool useFpsFilter;
            int maxAttempts;
            int timeoutMs;
            bool succeeded = false;
            try
            {
                startSec = startMs / 1000.0;
                endSec = startSec + durationSec;
                startFormatted = startSec.ToString("F3", CultureInfo.InvariantCulture);
                endFormatted = endSec.ToString("F3", CultureInfo.InvariantCulture);
                resolution = this._videoSyncConfig.FrameWidth + ":" + this._videoSyncConfig.FrameHeight;
                useFpsFilter = targetFps > 0.0 || sampleIntervalSec > 0.0;
                maxAttempts = this._ffmpegConfig.HardwareAcceleration ? 2 : 1;
                timeoutMs = this._ffmpegConfig.FrameExtractionTimeoutMs;

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    bool useHardwareAcceleration = this._ffmpegConfig.HardwareAcceleration && attempt == 0;
                    args.Clear();
                    extractedFrames.Clear();
                    tsList.Clear();

                    args.Add("-nostdin");
                    args.Add("-hide_banner");
                    if (useHardwareAcceleration)
                    {
                        args.Add("-hwaccel");
                        args.Add(this._ffmpegConfig.HardwareAccelerationMethod);
                    }
                    args.Add("-ss");
                    args.Add(startFormatted);
                    args.Add("-i");
                    args.Add(filePath);
                    args.Add("-copyts");
                    args.Add("-to");
                    args.Add(endFormatted);
                    args.Add("-fps_mode");
                    args.Add(useFpsFilter ? "vfr" : "passthrough");

                    filterChain = this.BuildFilterChain(targetFps, sampleIntervalSec, geometryCropToFourThree, manualCropPx, useFpsFilter, resolution);
                    args.Add("-vf");
                    args.Add(filterChain);
                    args.Add("-f");
                    args.Add("rawvideo");
                    args.Add("-");

                    RawFrameStdoutState stdoutState = new RawFrameStdoutState(frameSize, extractedFrames);
                    processResult = ProcessRunner.RunBinaryStdout(this._ffmpegPath, args.ToArray(), stdoutState.Append, timeoutMs);

                    stderrText = processResult.Stderr;
                    ptsMatches = s_ptsTimeRegex.Matches(stderrText);
                    for (int i = 0; i < ptsMatches.Count; i++)
                    {
                        if (double.TryParse(ptsMatches[i].Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out ptsSec))
                        {
                            tsList.Add(ptsSec * 1000.0);
                        }
                    }

                    minCount = Math.Min(extractedFrames.Count, tsList.Count);
                    if (minCount < extractedFrames.Count)
                    {
                        extractedFrames.RemoveRange(minCount, extractedFrames.Count - minCount);
                    }
                    if (minCount < tsList.Count)
                    {
                        tsList.RemoveRange(minCount, tsList.Count - minCount);
                    }

                    if (processResult.ExitCode == 0 && extractedFrames.Count > 0 && extractedFrames.Count == tsList.Count)
                    {
                        succeeded = true;
                        break;
                    }

                    extractedFrames.Clear();
                    tsList.Clear();
                    if (!useHardwareAcceleration)
                    {
                        ConsoleHelper.Write(this._logSection, LogLevel.Warning, AppText.F("deep.temporal.ffmpeg.frameExtractionFailed", this.GetLastErrorLine(processResult.Stderr)));
                        break;
                    }

                    if (System.Threading.Interlocked.Exchange(ref s_reportedHwAccelFallback, true) == false)
                    {
                        ConsoleHelper.Write(this._logSection, LogLevel.Notice, AppText.F("deep.temporal.ffmpeg.hardwareAccelerationRetry", this.GetLastErrorLine(processResult.Stderr)));
                    }
                }

                timestampsMs = tsList.ToArray();
                return succeeded;
            }
            catch (Exception ex)
            {
                ConsoleHelper.Write(this._logSection, LogLevel.Warning, AppText.F("deep.temporal.ffmpeg.segmentExtractionError", ex.Message));
                return false;
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Estrae l'ultima riga utile da un messaggio stderr
        /// </summary>
        /// <param name="text">Testo stderr</param>
        /// <returns>Ultima riga utile, o messaggio generico</returns>
        private string GetLastErrorLine(string text)
        {
            string result = AppText.T("deep.temporal.ffmpeg.noDetails");
            string[] lines;

            if (!string.IsNullOrEmpty(text))
            {
                lines = text.Replace("\r", "").Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    if (!string.IsNullOrEmpty(lines[i].Trim()))
                    {
                        result = lines[i].Trim();
                        break;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Costruisce la catena di filtri ffmpeg per normalizzare i frame
        /// </summary>
        /// <param name="targetFps">Frequenza di campionamento dei frame, oppure zero per non applicare il filtro fps</param>
        /// <param name="sampleIntervalSec">Intervallo tra i frame selezionati in secondi, oppure zero per non applicare il filtro select</param>
        /// <param name="geometryCropToFourThree">Indica se applicare il crop geometrico in rapporto quattro a tre</param>
        /// <param name="manualCropPx">Crop manuale nel formato sinistra:destra:alto:basso, oppure valore non configurato</param>
        /// <param name="useFpsFilter">Indica se la catena deve selezionare i frame secondo una frequenza o un intervallo</param>
        /// <param name="resolution">Risoluzione finale nel formato larghezza:altezza</param>
        /// <returns>Catena di filtri ffmpeg completa</returns>
        private string BuildFilterChain(double targetFps, double sampleIntervalSec, bool geometryCropToFourThree, string manualCropPx, bool useFpsFilter, string resolution)
        {
            string filterChain = "";
            string manualCropFilter;
            bool hasManualCrop;

            if (useFpsFilter)
            {
                if (sampleIntervalSec > 0.0)
                {
                    filterChain = "select='isnan(prev_selected_t)+gte(t-prev_selected_t\\," + sampleIntervalSec.ToString("R", CultureInfo.InvariantCulture) + ")'";
                }
                else
                {
                    filterChain = "fps=fps=" + targetFps.ToString("R", CultureInfo.InvariantCulture) + ":round=near";
                }
            }

            hasManualCrop = this.TryBuildManualCropFilter(manualCropPx, out manualCropFilter);
            if (hasManualCrop)
            {
                if (!string.IsNullOrEmpty(filterChain))
                {
                    filterChain = filterChain + "," + manualCropFilter;
                }
                else
                {
                    filterChain = manualCropFilter;
                }
            }
            else if (geometryCropToFourThree)
            {
                if (!string.IsNullOrEmpty(filterChain))
                {
                    filterChain = filterChain + ",crop=ih*4/3:ih";
                }
                else
                {
                    filterChain = "crop=ih*4/3:ih";
                }
            }
            if (!string.IsNullOrEmpty(filterChain))
            {
                filterChain = filterChain + ",scale=w='trunc(iw*sar/2)*2':h=ih:flags=fast_bilinear,setsar=1,scale=" + resolution + ":flags=fast_bilinear,format=gray";
            }
            else
            {
                filterChain = "scale=w='trunc(iw*sar/2)*2':h=ih:flags=fast_bilinear,setsar=1,scale=" + resolution + ":flags=fast_bilinear,format=gray";
            }

            filterChain = filterChain + ",showinfo";
            return filterChain;
        }

        /// <summary>
        /// Costruisce il filtro crop manuale L:R:T:B se configurato
        /// </summary>
        /// <param name="manualCropPx">Crop manuale in pixel</param>
        /// <param name="filter">Filtro ffmpeg risultante</param>
        /// <returns>True se esiste un crop manuale non nullo</returns>
        private bool TryBuildManualCropFilter(string manualCropPx, out string filter)
        {
            int left;
            int right;
            int top;
            int bottom;

            filter = "";
            if (!Options.TryParseAnalysisCropPx(manualCropPx, out left, out right, out top, out bottom))
            {
                return false;
            }

            if (left == 0 && right == 0 && top == 0 && bottom == 0)
            {
                return false;
            }

            filter = "crop=iw-" + left.ToString(CultureInfo.InvariantCulture) + "-" + right.ToString(CultureInfo.InvariantCulture) +
                ":ih-" + top.ToString(CultureInfo.InvariantCulture) + "-" + bottom.ToString(CultureInfo.InvariantCulture) +
                ":" + left.ToString(CultureInfo.InvariantCulture) +
                ":" + top.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        #endregion

        #region Classi private

        /// <summary>
        /// Stato mutabile usato dalla callback stdout per ricostruire frame raw completi
        /// </summary>
        private class RawFrameStdoutState
        {
            /// <summary>
            /// Dimensione in byte di ogni frame raw completo
            /// </summary>
            private readonly int _frameSize;

            /// <summary>
            /// Collezione in cui accodare i frame ricostruiti
            /// </summary>
            private readonly List<byte[]> _frames;

            /// <summary>
            /// Buffer del frame attualmente in costruzione
            /// </summary>
            private byte[] _frameData;

            /// <summary>
            /// Numero di byte già presenti nel buffer corrente
            /// </summary>
            private int _totalRead;

            /// <summary>
            /// Inizializza lo stato per frame raw di dimensione fissa
            /// </summary>
            /// <param name="frameSize">Dimensione in byte di un frame completo</param>
            /// <param name="frames">Collezione in cui accodare i frame ricostruiti</param>
            public RawFrameStdoutState(int frameSize, List<byte[]> frames)
            {
                this._frameSize = frameSize;
                this._frames = frames;
                this._frameData = new byte[frameSize];
                this._totalRead = 0;
            }

            /// <summary>
            /// Accoda un chunk stdout e produce frame completi quando il buffer è pieno
            /// </summary>
            /// <param name="buffer">Dati ricevuti dalla stdout di ffmpeg</param>
            /// <param name="bytesRead">Numero di byte validi presenti nel buffer</param>
            public void Append(byte[] buffer, int bytesRead)
            {
                int offset = 0;
                while (offset < bytesRead)
                {
                    int copyCount = Math.Min(this._frameSize - this._totalRead, bytesRead - offset);
                    Array.Copy(buffer, offset, this._frameData, this._totalRead, copyCount);
                    this._totalRead += copyCount;
                    offset += copyCount;

                    if (this._totalRead == this._frameSize)
                    {
                        this._frames.Add(this._frameData);
                        this._frameData = new byte[this._frameSize];
                        this._totalRead = 0;
                    }
                }
            }
        }

        #endregion
    }
}
