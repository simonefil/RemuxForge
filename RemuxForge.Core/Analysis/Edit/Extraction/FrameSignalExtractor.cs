using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace RemuxForge.Core.Analysis.Edit.Extraction
{
    /// <summary>
    /// Deriva tutti i segnali per fotogramma da una sola decodifica lineare del file
    /// </summary>
    internal class FrameSignalExtractor
    {
        #region Costanti

        /// <summary>
        /// Numero di byte di un fotogramma grigio di analisi
        /// </summary>
        private const int FRAME_BYTES = FrameSignals.SIDE * FrameSignals.SIDE;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Espressione che isola i pts_time prodotti da showinfo
        /// </summary>
        private static readonly Regex s_showInfoPtsRegex = new Regex(@"pts_time:(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

        #endregion

        #region Variabili di istanza

        /// <summary>
        /// Percorso dell'eseguibile ffmpeg
        /// </summary>
        private string _ffmpegPath;

        /// <summary>
        /// Configurazione ffmpeg con accelerazione hardware e timeout
        /// </summary>
        private FfmpegConfig _ffmpegConfig;

        /// <summary>
        /// Backend che calcola i dHash dei fotogrammi decodificati
        /// </summary>
        private HashBackendBase _hashBackend;

        /// <summary>
        /// Servizio condiviso per PTS e origine temporale dei video
        /// </summary>
        private VideoFrameAccessService _frameAccessService;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="ffmpegPath">Percorso di ffmpeg</param>
        /// <param name="ffprobePath">Percorso di ffprobe</param>
        /// <param name="mkvMergePath">Percorso di mkvmerge</param>
        /// <param name="mkvExtractPath">Percorso di mkvextract</param>
        /// <param name="ffmpegConfig">Configurazione ffmpeg corrente</param>
        /// <param name="hashBackend">Backend che calcola i dHash dei fotogrammi</param>
        public FrameSignalExtractor(string ffmpegPath, string ffprobePath, string mkvMergePath, string mkvExtractPath, FfmpegConfig ffmpegConfig, HashBackendBase hashBackend)
        {
            this._hashBackend = hashBackend;
            this._ffmpegPath = ffmpegPath ?? "";
            this._ffmpegConfig = ffmpegConfig ?? new FfmpegConfig();
            this._frameAccessService = new VideoFrameAccessService(ffprobePath, mkvMergePath, mkvExtractPath);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Decodifica il file una volta sola e ne deriva dHash, luminanza e miniature
        /// </summary>
        /// <param name="filePath">File multimediale da indicizzare</param>
        /// <param name="geometry">Geometria di normalizzazione condivisa dalla coppia</param>
        /// <param name="timeoutMs">Timeout complessivo della decodifica in millisecondi</param>
        /// <param name="cancellation">Token di annullamento</param>
        /// <returns>Segnali per fotogramma allineati ai PTS di contenitore</returns>
        public FrameSignals Extract(string filePath, FrameGeometry geometry, int timeoutMs, CancellationToken cancellation)
        {
            return this.Extract(filePath, geometry, 0.0, 0.0, 0, timeoutMs, cancellation);
        }

        /// <summary>
        /// Indicizza una sola finestra temporale, conservando i PTS assoluti del contenitore
        /// </summary>
        /// <param name="filePath">File multimediale da indicizzare</param>
        /// <param name="geometry">Geometria di normalizzazione condivisa dalla coppia</param>
        /// <param name="startMs">Inizio della finestra sull'orologio del contenitore, zero per l'intero file</param>
        /// <param name="durationMs">Durata della finestra, zero per l'intero file</param>
        /// <param name="frameBudget">Fotogrammi da chiedere alla decodifica, con margine sul framerate atteso</param>
        /// <param name="timeoutMs">Timeout complessivo della decodifica in millisecondi</param>
        /// <param name="cancellation">Token di annullamento</param>
        /// <returns>Segnali per fotogramma della sola finestra richiesta</returns>
        public FrameSignals Extract(string filePath, FrameGeometry geometry, double startMs, double durationMs, int frameBudget, int timeoutMs, CancellationToken cancellation)
        {
            // Su una finestra i PTS di contenitore non sono indicizzabili: li dà showinfo,
            // reso assoluto da -copyts
            bool windowed = durationMs > 0.0;
            // -ss conta dall'inizio del file, non dallo zero dell'orologio: su un contenitore
            // che parte dopo lo zero la finestra chiesta e quella decodificata non coincidono
            double seekMs = windowed ? Math.Max(0.0, startMs - this._frameAccessService.ReadStartTimeMs(filePath, timeoutMs, cancellation)) : 0.0;
            List<double> containerTimestamps = windowed ? new List<double>() : this._frameAccessService.ReadPresentationTimestamps(filePath, timeoutMs, cancellation);
            bool needsShowInfo = containerTimestamps.Count == 0;

            List<ulong> hash0 = new List<ulong>();
            List<ulong> hash1 = new List<ulong>();
            List<float> lumaMean = new List<float>();
            List<float> thumbStd = new List<float>();
            List<byte> thumbPixels = new List<byte>();
            // I fotogrammi si montano dentro il blocco e ci restano: il backend li prende tutti
            // insieme, e chi lavora a gruppi non paga un viaggio per fotogramma
            int batchFrames = Math.Max(1, this._hashBackend.BatchFrames);
            byte[] batch = new byte[batchFrames * FRAME_BYTES];
            int batchCount = 0;
            int pending = 0;

            string[] arguments = this.BuildArguments(filePath, geometry, needsShowInfo, seekMs, durationMs, frameBudget);
            ProcessBinaryResult run = ProcessRunner.RunBinaryStdout(this._ffmpegPath, arguments, (buffer, count) =>
            {
                cancellation.ThrowIfCancellationRequested();
                int consumed = 0;
                while (consumed < count)
                {
                    int origin = batchCount * FRAME_BYTES;
                    int copied = Math.Min(FRAME_BYTES - pending, count - consumed);
                    Buffer.BlockCopy(buffer, consumed, batch, origin + pending, copied);
                    pending += copied;
                    consumed += copied;
                    if (pending < FRAME_BYTES)
                        continue;
                    pending = 0;
                    batchCount++;
                    if (batchCount < batchFrames)
                        continue;
                    this._hashBackend.Analyze(batch, batchCount, hash0, hash1, lumaMean, thumbStd, thumbPixels);
                    batchCount = 0;
                }
            }, timeoutMs);

            if (batchCount > 0)
                this._hashBackend.Analyze(batch, batchCount, hash0, hash1, lumaMean, thumbStd, thumbPixels);

            cancellation.ThrowIfCancellationRequested();
            // Una decodifica interrotta consegna comunque i fotogrammi letti fino a lì: senza
            // questo controllo un film troncato a metà passa per un segnale completo, e le
            // operazioni che stanno oltre il troncamento spariscono senza un errore
            if (run.ExitCode != 0)
                throw new InvalidOperationException("Decodifica non riuscita di " + Path.GetFileName(filePath) + " (uscita " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + "): " + GetLastErrorLine(run.Stderr));
            if (hash0.Count == 0)
                throw new InvalidOperationException("Nessun fotogramma decodificato da " + Path.GetFileName(filePath) + ": " + GetLastErrorLine(run.Stderr));
            // Alcuni flussi Matroska dichiarano un ultimo frame che FFmpeg non emette pur
            // terminando correttamente. Si accetta solo quel singolo frame terminale; uno
            // scarto maggiore resta una decodifica realmente incompleta.
            if (!windowed && hash0.Count + 1 < containerTimestamps.Count)
                throw new InvalidOperationException("Decodifica troncata di " + Path.GetFileName(filePath) + ": " + hash0.Count.ToString(CultureInfo.InvariantCulture) + " fotogrammi su " + containerTimestamps.Count.ToString(CultureInfo.InvariantCulture) + ": " + GetLastErrorLine(run.Stderr));

            double[] ptsMs = needsShowInfo
                ? ParseShowInfoTimestamps(run.Stderr, hash0.Count)
                : TakeContainerTimestamps(containerTimestamps, hash0.Count);
            if (ptsMs.Length < hash0.Count)
                throw new InvalidOperationException("Timestamp insufficienti per " + Path.GetFileName(filePath) + ": " + ptsMs.Length.ToString(CultureInfo.InvariantCulture) + " per " + hash0.Count.ToString(CultureInfo.InvariantCulture) + " fotogrammi");

            int count = hash0.Count;
            if (windowed)
            {
                // Il budget di fotogrammi è generoso per non restare corti sul framerate vero:
                // quello che sborda dalla finestra si taglia qui, sui timestamp
                while (count > 0 && ptsMs[count - 1] > startMs + durationMs)
                    count--;
                if (count == 0)
                    throw new InvalidOperationException("Nessun fotogramma nella finestra richiesta di " + Path.GetFileName(filePath));
                hash0.RemoveRange(count, hash0.Count - count);
                hash1.RemoveRange(count, hash1.Count - count);
                lumaMean.RemoveRange(count, lumaMean.Count - count);
                thumbStd.RemoveRange(count, thumbStd.Count - count);
                thumbPixels.RemoveRange(count * FrameSignals.THUMB_SIDE * FrameSignals.THUMB_SIDE, thumbPixels.Count - count * FrameSignals.THUMB_SIDE * FrameSignals.THUMB_SIDE);
                Array.Resize(ref ptsMs, count);
            }

            return new FrameSignals(ptsMs, hash0.ToArray(), hash1.ToArray(), lumaMean.ToArray(), thumbStd.ToArray(), thumbPixels.ToArray());
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Compone la riga di comando della passata unica di decodifica
        /// </summary>
        /// <param name="filePath">File da decodificare</param>
        /// <param name="geometry">Geometria di normalizzazione</param>
        /// <param name="withShowInfo">True per chiedere i PTS a showinfo come riserva</param>
        /// <param name="startMs">Inizio della finestra in millisecondi</param>
        /// <param name="durationMs">Durata della finestra in millisecondi</param>
        /// <param name="frameBudget">Numero massimo di fotogrammi da decodificare</param>
        /// <returns>Argomenti nell'ordine atteso da ffmpeg</returns>
        private string[] BuildArguments(string filePath, FrameGeometry geometry, bool withShowInfo, double startMs, double durationMs, int frameBudget)
        {
            List<string> result = new List<string>();
            result.Add("-nostdin");
            result.Add("-v");
            result.Add(withShowInfo ? "info" : "error");
            if (this._ffmpegConfig.HardwareAcceleration && FfmpegConfig.IsValidHardwareAccelerationMethod(this._ffmpegConfig.HardwareAccelerationMethod))
            {
                result.Add("-hwaccel");
                result.Add(this._ffmpegConfig.HardwareAccelerationMethod);
            }
            if (durationMs > 0.0)
            {
                result.Add("-copyts");
                result.Add("-ss");
                result.Add((Math.Max(0.0, startMs) / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
            }
            result.Add("-i");
            result.Add(filePath);
            if (durationMs > 0.0)
            {
                // Con -copyts i limiti di durata non si applicano più in modo prevedibile, e su
                // un file con origine diversa da zero non esce un fotogramma: si conta e basta
                result.Add("-frames:v");
                result.Add(frameBudget.ToString(CultureInfo.InvariantCulture));
            }
            result.Add("-an");
            result.Add("-sn");
            result.Add("-dn");
            result.Add("-map");
            result.Add("0:v:0");
            result.Add("-fps_mode");
            result.Add("passthrough");
            result.Add("-vf");
            result.Add(this.BuildFilter(geometry, withShowInfo));
            result.Add("-f");
            result.Add("rawvideo");
            result.Add("-");
            return result.ToArray();
        }

        /// <summary>
        /// Compone la catena di filtri che porta il fotogramma al quadrato grigio di analisi
        /// </summary>
        /// <param name="geometry">Geometria di normalizzazione</param>
        /// <param name="withShowInfo">True per accodare showinfo</param>
        /// <returns>Catena di filtri separata da virgole</returns>
        private string BuildFilter(FrameGeometry geometry, bool withShowInfo)
        {
            List<string> filters = new List<string>();
            if (Options.TryParseAnalysisCropPx(geometry.CropPx, out int left, out int right, out int top, out int bottom) && (left != 0 || right != 0 || top != 0 || bottom != 0))
            {
                filters.Add("crop=iw-" + left.ToString(CultureInfo.InvariantCulture) + "-" + right.ToString(CultureInfo.InvariantCulture) +
                    ":ih-" + top.ToString(CultureInfo.InvariantCulture) + "-" + bottom.ToString(CultureInfo.InvariantCulture) +
                    ":" + left.ToString(CultureInfo.InvariantCulture) + ":" + top.ToString(CultureInfo.InvariantCulture));
            }

            if (geometry.UseNormalizedActiveViewport)
            {
                string leftFraction = geometry.ViewportLeft.ToString("0.########", CultureInfo.InvariantCulture);
                string topFraction = geometry.ViewportTop.ToString("0.########", CultureInfo.InvariantCulture);
                string widthFraction = (geometry.ViewportRight - geometry.ViewportLeft).ToString("0.########", CultureInfo.InvariantCulture);
                string heightFraction = (geometry.ViewportBottom - geometry.ViewportTop).ToString("0.########", CultureInfo.InvariantCulture);
                filters.Add("crop=iw*" + widthFraction + ":ih*" + heightFraction + ":iw*" + leftFraction + ":ih*" + topFraction);
            }
            else
            {
                filters.Add("scale=iw*sar:ih");
                if (geometry.UseCentralSquare)
                {
                    filters.Add("crop=min(iw\\,ih):min(iw\\,ih):(iw-min(iw\\,ih))/2:(ih-min(iw\\,ih))/2");
                    if (geometry.Zoom < 0.999999 || Math.Abs(geometry.VerticalShift) > 0.000001)
                    {
                        string zoom = geometry.Zoom.ToString("0.######", CultureInfo.InvariantCulture);
                        string shift = geometry.VerticalShift.ToString("0.######", CultureInfo.InvariantCulture);
                        filters.Add("crop=iw*" + zoom + ":ih*" + zoom + ":" +
                            "(iw-iw*" + zoom + ")/2:" +
                            "max(0\\,min(ih-ih*" + zoom + "\\,(ih-ih*" + zoom + ")/2+" + shift + "*ih))");
                    }
                }
            }
            filters.Add("scale=" + FrameSignals.SIDE.ToString(CultureInfo.InvariantCulture) + ":" + FrameSignals.SIDE.ToString(CultureInfo.InvariantCulture) + ":flags=area");
            filters.Add("format=gray");
            if (withShowInfo)
                filters.Add("showinfo");

            return string.Join(",", filters);
        }

        /// <summary>
        /// Riduce la timeline di contenitore al numero di fotogrammi effettivamente decodificati
        /// </summary>
        /// <param name="timestamps">Timeline completa</param>
        /// <param name="frameCount">Fotogrammi decodificati</param>
        /// <returns>Prefisso della timeline lungo quanto i fotogrammi</returns>
        private static double[] TakeContainerTimestamps(List<double> timestamps, int frameCount)
        {
            if (timestamps.Count < frameCount)
                return timestamps.ToArray();
            double[] result = new double[frameCount];
            timestamps.CopyTo(0, result, 0, frameCount);
            return result;
        }

        /// <summary>
        /// Ricava i PTS dalle righe showinfo della stessa passata di decodifica
        /// </summary>
        /// <param name="stderr">Diagnostica ffmpeg</param>
        /// <param name="frameCount">Fotogrammi decodificati</param>
        /// <returns>PTS in millisecondi nell'ordine di emissione</returns>
        private static double[] ParseShowInfoTimestamps(string stderr, int frameCount)
        {
            MatchCollection matches = s_showInfoPtsRegex.Matches(stderr ?? "");
            List<double> result = new List<double>();
            for (int i = 0; i < matches.Count && result.Count < frameCount; i++)
            {
                if (double.TryParse(matches[i].Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                    result.Add(seconds * 1000.0);
            }
            return result.ToArray();
        }



        /// <summary>
        /// Recupera l'ultima riga non vuota della diagnostica di un processo
        /// </summary>
        /// <param name="text">Diagnostica completa</param>
        /// <returns>Ultima riga non vuota oppure stringa descrittiva</returns>
        private static string GetLastErrorLine(string text)
        {
            string[] lines = (text ?? "").Replace("\r", "").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    return lines[i].Trim();
            }
            return "nessuna diagnostica";
        }

        #endregion
    }
}
