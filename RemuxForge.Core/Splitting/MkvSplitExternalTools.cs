using RemuxForge.Core.Chapters;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Codecs;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Wrapper tool esterni per la pipeline split, integrato con resolver e ProcessRunner di RemuxForge
    /// </summary>
    public class MkvSplitExternalTools
    {
        #region Singleton

        /// <summary>
        /// Istanza singleton del wrapper
        /// </summary>
        private static readonly MkvSplitExternalTools s_instance = new MkvSplitExternalTools();

        /// <summary>
        /// Istanza singleton
        /// </summary>
        public static MkvSplitExternalTools Instance { get { return s_instance; } }

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Percorso mkvextract
        /// </summary>
        private string _mkvextract;

        /// <summary>
        /// Percorso mkvmerge
        /// </summary>
        private string _mkvmerge;

        /// <summary>
        /// Percorso ffmpeg
        /// </summary>
        private string _ffmpeg;

        /// <summary>
        /// Percorso ffprobe
        /// </summary>
        private string _ffprobe;

        /// <summary>
        /// Percorso MediaInfo
        /// </summary>
        private string _mediainfo;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore privato
        /// </summary>
        private MkvSplitExternalTools()
        {
            this._mkvextract = "";
            this._mkvmerge = "";
            this._ffmpeg = "";
            this._ffprobe = "";
            this._mediainfo = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Risolve e valida i binari necessari alla modalità split
        /// </summary>
        public void ResolveBinaries()
        {
            ToolPathResolverService resolver = new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder);
            List<string> missing = new List<string>();

            this._mkvmerge = resolver.ResolveMkvMergePath(true);
            this._mkvextract = resolver.ResolveMkvExtractPath(this._mkvmerge, true);
            this._ffmpeg = resolver.ResolveFfmpegPath(true, true);
            this._ffprobe = resolver.ResolveFfprobePath(this._ffmpeg, true);
            this._mediainfo = resolver.ResolveMediaInfoPath(true);

            if (string.IsNullOrEmpty(this._mkvmerge))
                missing.Add("mkvmerge");

            if (string.IsNullOrEmpty(this._mkvextract))
                missing.Add("mkvextract");

            if (string.IsNullOrEmpty(this._ffmpeg))
                missing.Add("ffmpeg");

            if (string.IsNullOrEmpty(this._ffprobe))
                missing.Add("ffprobe");

            if (string.IsNullOrEmpty(this._mediainfo))
                missing.Add("mediainfo");

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(AppText.F("split.tools.missing", string.Join(", ", missing)));
            }
        }

        /// <summary>
        /// Legge parametri del primo stream video
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <returns>Parametri video letti da ffprobe</returns>
        public MkvSplitVideoParams GetVideoParams(string inputFile)
        {
            ProcessResult r;
            MkvSplitVideoParams vp;
            int eq;
            string key;
            string val;
            string line;

            r = this.Run(this._ffprobe, new string[]
            {
                "-v", "quiet", "-select_streams", "v:0",
                "-show_entries", "stream=pix_fmt,color_space,color_primaries,color_transfer,color_range,codec_name,codec_tag_string,width,height",
                "-of", "default=noprint_wrappers=1:nokey=0", inputFile
            });

            vp = new MkvSplitVideoParams();
            foreach (string rawLine in r.Stdout.Split('\n'))
            {
                line = rawLine.Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                key = line.Substring(0, eq).Trim();
                val = line.Substring(eq + 1).Trim();
                if (string.IsNullOrEmpty(val) || val.Equals("unknown", StringComparison.OrdinalIgnoreCase) || val == "N/A")
                    continue;

                if (key == "codec_name")
                {
                    vp.CodecName = val;
                }
                else if (key == "pix_fmt")
                {
                    vp.PixFmt = val;
                }
                else if (key == "color_space")
                {
                    vp.ColorSpace = val;
                }
                else if (key == "color_primaries")
                {
                    vp.ColorPrimaries = val;
                }
                else if (key == "color_transfer")
                {
                    vp.ColorTransfer = val;
                }
                else if (key == "color_range")
                {
                    vp.ColorRange = val;
                }
                // Le tracce native non hanno fourcc: ffprobe scrive i byte nulli come [0][0][0][0]
                else if (key == "codec_tag_string" && val.IndexOf('[') < 0)
                {
                    vp.CodecTag = val;
                }
                else if (key == "width")
                {
                    vp.Width = int.Parse(val, NumberStyles.Integer, CultureInfo.InvariantCulture);
                }
                else if (key == "height")
                {
                    vp.Height = int.Parse(val, NumberStyles.Integer, CultureInfo.InvariantCulture);
                }
            }

            return vp;
        }

        /// <summary>Separa i PTS dei frame dal confine finale aggiuntivo di timestamps_v2.</summary>
        public double[] ExtractSourcePts(string sourceFile, int packetCount, out double endTimestamp)
        {
            double value;

            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.T("split.tools.extractingPts"));
            List<double> pts = this.ReadTimestamps(sourceFile, 0, "mkv_pts_");

            endTimestamp = double.NaN;
            if (pts.Count == packetCount + 1)
            {
                endTimestamp = pts[pts.Count - 1];
                pts.RemoveAt(pts.Count - 1);
            }

            if (!double.IsFinite(endTimestamp))
            {
                value = double.NaN;
                double lastPts = double.NegativeInfinity;
                this.RunStreamed(this._ffprobe, new string[]
                {
                    "-v", "error", "-select_streams", "v:0", "-show_entries",
                    "packet=pts_time,duration_time", "-of", "csv=p=0", sourceFile
                }, line =>
                {
                    string[] fields = line.Split(',');
                    if (fields.Length >= 2
                        && double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double timestamp)
                        && timestamp >= lastPts)
                    {
                        lastPts = timestamp;
                        value = double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
                            && duration > 0 ? timestamp + duration : double.NaN;
                    }
                });
                endTimestamp = value;
            }

            double[] arr = pts.ToArray();
            Array.Sort(arr);
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.tools.ptsExtracted", arr.Length));
            return arr;
        }

        /// <summary>Legge l'inventario tracce usando lo stesso resolver della pipeline.</summary>
        public MkvFileInfo GetFileInfo(string inputFile)
        {
            return new MkvToolsService(this._mkvmerge).GetFileInfo(inputFile);
        }

        /// <summary>
        /// Legge i PTS video nell'ordine in cui i packet stanno nel file, cioè in ordine di decodifica.
        /// Serve a passare dagli indici di presentazione, con cui il piano descrive i segmenti, agli
        /// indici del raw bitstream, che è scritto in ordine di decodifica: con i B-frame i due ordini
        /// non coincidono.
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <returns>PTS in secondi, in ordine di decodifica</returns>
        public List<double> GetDecodeOrderPts(string inputFile)
        {
            List<double> pts = new List<double>(1 << 17);
            double value;

            this.RunStreamed(this._ffprobe, new string[]
            {
                "-v", "quiet", "-select_streams", "v:0",
                "-show_entries", "packet=pts_time", "-of", "csv=p=0", inputFile
            }, delegate (string line)
            {
                if (double.TryParse(line.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    pts.Add(value);
            });

            return pts;
        }

        /// <summary>
        /// Mappa posizione, dimensione e keyflag di ogni packet del raw stream
        /// </summary>
        /// <param name="rawFile">File raw da analizzare</param>
        /// <returns>Mappa dei frame con posizione, dimensione e keyflag</returns>
        public List<MkvSplitFrameInfo> GetFrameByteMap(string rawFile)
        {
            List<MkvSplitFrameInfo> frames = new List<MkvSplitFrameInfo>(1 << 17);
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.T("split.tools.mappingBytes"));

            this.RunStreamed(this._ffprobe, new string[]
            {
                "-v", "quiet", "-select_streams", "v:0",
                "-show_entries", "packet=pos,size,flags", "-of", "csv=p=0", rawFile
            }, delegate (string line)
            {
                string[] parts = line.Split(',');
                MkvSplitFrameInfo f;
                if (parts.Length < 2)
                    return;

                f = new MkvSplitFrameInfo();
                f.Size = int.Parse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture);
                f.Pos = long.Parse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture);
                f.Key = parts.Length > 2 && parts[2].IndexOf('K') >= 0;
                frames.Add(f);
            });

            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.tools.framesMapped", frames.Count));
            return frames;
        }

        /// <summary>
        /// Legge i keyflag video, marcando i keyframe di GOP aperto; il codec corregge i flag e marca i B iniziali decodificabili
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <param name="codecName">Codec video come lo riporta ffprobe</param>
        /// <returns>Fotogrammi in ordine di presentazione con flag keyframe</returns>
        public List<MkvSplitFrameInfo> GetKeyFlags(string inputFile, string codecName)
        {
            List<MkvSplitFrameInfo> frames = new List<MkvSplitFrameInfo>(1 << 17);
            List<double> pts = new List<double>(1 << 17);
            List<MkvSplitFrameInfo> byPresentation;
            double value;
            MkvSplitVideoCodecBase codec = MkvSplitCodecCapabilities.FindForKeyFlags(codecName);

            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.T("split.tools.probingKeyflags"));

            this.RunStreamed(this._ffprobe, new string[]
            {
                "-v", "quiet", "-select_streams", "v:0",
                "-show_entries", "packet=pts_time,pos,flags", "-of", "csv=p=0", inputFile
            }, delegate (string line)
            {
                MkvSplitFrameInfo f;
                string[] parts = line.Split(',');
                if (parts.Length < 3)
                    return;

                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return;

                f = new MkvSplitFrameInfo();
                // Offset del pacchetto nel Matroska: serve solo a leggere l'header GOP dei keyframe MPEG-2
                f.Pos = long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long pos) ? pos : -1;
                f.Key = parts[2].IndexOf('K') >= 0;
                frames.Add(f);
                pts.Add(value);
            });

            codec?.CorrectKeyFlags(inputFile, frames, pts);
            byPresentation = ToPresentationOrder(frames, pts.ToArray());
            codec?.MarkDecodableLeadingFrames(inputFile, byPresentation);

            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.tools.packetsProbed", byPresentation.Count));
            return byPresentation;
        }

        /// <summary>
        /// Tiene come keyframe solo i fotogrammi intra reali. In Video for Windows il flag è dell'indice AVI, non del fotogramma: un
        /// H.263 può avere ogni packet marcato keyframe. Il decoder con skip_frame nokey decodifica solo gli I reali, leggendo l'header
        /// di ogni packet
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <param name="frames">Pacchetti in ordine di decodifica</param>
        /// <param name="pts">PTS dei pacchetti in ordine di decodifica</param>
        public void CorrectAviIndexKeyFlags(string inputFile, List<MkvSplitFrameInfo> frames, List<double> pts)
        {
            HashSet<long> intra = new HashSet<long>();

            this.RunStreamed(this._ffprobe, new string[]
            {
                "-v", "quiet", "-select_streams", "v:0", "-skip_frame", "nokey",
                "-show_entries", "frame=pts_time", "-of", "csv=p=0", inputFile
            }, delegate (string line)
            {
                if (double.TryParse(line.Trim().TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out double framePts))
                    intra.Add((long)Math.Round(framePts * 1000.0));
            });
            for (int i = 0; i < frames.Count; i++)
            {
                MkvSplitFrameInfo f = frames[i];
                f.Key = intra.Contains((long)Math.Round(pts[i] * 1000.0));
                frames[i] = f;
            }
        }

        /// <summary>
        /// Riordina i packet per presentazione e marca i keyframe di GOP aperto
        /// </summary>
        /// <param name="decodeOrder">Packet in ordine di decodifica</param>
        /// <param name="pts">PTS dei packet in ordine di decodifica</param>
        /// <returns>Fotogrammi in ordine di presentazione</returns>
        public static List<MkvSplitFrameInfo> ToPresentationOrder(List<MkvSplitFrameInfo> decodeOrder, double[] pts)
        {
            int[] order = new int[pts.Length];
            int[] presentationOf = new int[pts.Length];
            MkvSplitFrameInfo[] frames = decodeOrder.ToArray();
            int maxBefore = -1;

            for (int index = 0; index < order.Length; index++)
                order[index] = index;
            Array.Sort((double[])pts.Clone(), order);
            for (int index = 0; index < order.Length; index++)
                presentationOf[order[index]] = index;

            // Con GOP aperti un fotogramma decodificato dopo il keyframe si mostra prima: mkvmerge taglia in ordine
            // di decodifica e lo sposterebbe nell'output sbagliato. Il keyframe è chiuso solo se i fotogrammi
            // decodificati prima sono esattamente quelli mostrati prima.
            for (int decode = 0; decode < frames.Length; decode++)
            {
                if (frames[decode].Key && (maxBefore != decode - 1 || presentationOf[decode] != decode))
                    frames[decode].OpenGop = true;
                // I fotogrammi decodificati prima sono tutti e soli i primi in presentazione: quelli che mancano fino al
                // keyframe sono i B iniziali, decodificati dopo e mostrati subito prima (D25)
                if (frames[decode].Key && maxBefore == decode - 1 && presentationOf[decode] > decode)
                    frames[decode].LeadingFrames = presentationOf[decode] - decode;
                maxBefore = Math.Max(maxBefore, presentationOf[decode]);
            }

            // ffprobe elenca i packet in ordine di decodifica, ma il piano indicizza i fotogrammi per
            // presentazione: con i B-frame i due ordini differiscono e senza riordino lo snap finirebbe
            // su un fotogramma che keyframe non è.
            Array.Sort((double[])pts.Clone(), frames);
            return new List<MkvSplitFrameInfo>(frames);
        }

        /// <summary>
        /// Conta packet video
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <returns>Numero di packet video</returns>
        public int CountPackets(string inputFile)
        {
            int count = 0;
            this.RunStreamed(this._ffprobe, new string[]
            {
                "-v", "quiet", "-select_streams", "v:0",
                "-show_entries", "packet=pos", "-of", "csv=p=0", inputFile
            }, delegate (string line)
            {
                if (!string.IsNullOrEmpty(line.Trim()))
                    count++;
            });

            return count;
        }

        /// <summary>
        /// Conta i fotogrammi video che il decoder restituisce decodificando l'intero file: un flusso che da un punto in poi non si
        /// decodifica ne restituisce meno dei pacchetti
        /// </summary>
        /// <param name="inputFile">File MKV</param>
        /// <returns>Fotogrammi decodificati della prima traccia video, 0 se ffprobe non li riporta</returns>
        public int CountDecodedFrames(string inputFile)
        {
            ProcessResult r = this.Run(this._ffprobe, new string[]
            {
                "-v", "quiet", "-select_streams", "v:0", "-count_frames",
                "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", inputFile
            });

            // Alcuni flussi (MPEG-2) aggiungono alla riga i campi vuoti dei dati laterali: il conteggio è il primo campo
            return int.TryParse(r.Stdout.Trim().Split(',')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? count : 0;
        }

        /// <summary>
        /// Inizio del primo pacchetto di una traccia audio che finisce oltre il tempo indicato. I tempi sono quelli dei blocchi
        /// Matroska, come li confronta mkvmerge, senza il ritardo del codec che ffprobe sottrae; ogni pacchetto finisce dove inizia
        /// il successivo e l'ultimo dove mkvextract chiude la traccia
        /// </summary>
        /// <param name="inputFile">File MKV</param>
        /// <param name="trackId">ID mkvmerge della traccia audio</param>
        /// <param name="endTimestamp">Tempo in secondi oltre cui l'audio non deve arrivare</param>
        /// <returns>Inizio in secondi del pacchetto, infinito se nessun pacchetto supera il tempo</returns>
        public double GetAudioOverrunStart(string inputFile, int trackId, double endTimestamp)
        {
            List<double> timestamps = this.ReadTimestamps(inputFile, trackId, "mkv_audio_ts_");

            // Tempi al millesimo: un pacchetto che finisce sull'ultimo fotogramma entro mezzo millisecondo non lo supera
            for (int index = 0; index + 1 < timestamps.Count; index++)
            {
                if (timestamps[index + 1] > endTimestamp + 0.0005)
                    return timestamps[index];
            }

            return double.PositiveInfinity;
        }

        /// <summary>
        /// Passa la prima traccia video di un Matroska per un filtro di bitstream di ffmpeg, che lavora sulla sola traccia, e la
        /// ricompone con mkvmerge insieme alle altre tracce del file, altre tracce video comprese, nello stesso ordine, con gli
        /// stessi ID e le stesse proprietà
        /// </summary>
        /// <param name="inputFile">Matroska da ricomporre</param>
        /// <param name="tracks">Tracce del Matroska, nel loro ordine</param>
        /// <param name="bitstreamFilter">Filtro di bitstream ffmpeg della traccia video</param>
        /// <param name="videoFile">Matroska intermedio con la sola traccia video filtrata</param>
        /// <param name="outputFile">Matroska ricomposto</param>
        /// <param name="globalOptions">Opzioni globali di mkvmerge per il Matroska ricomposto, nessuna se vuote</param>
        public void RecomposeFilteredVideo(string inputFile, List<TrackInfo> tracks, string bitstreamFilter, string videoFile, string outputFile, params string[] globalOptions)
        {
            TrackInfo video = tracks.Find(track => track.Type == "video");
            this.RunFfmpeg(new string[] { "-y", "-v", "error", "-i", inputFile, "-map", "0:v:0", "-c", "copy", "-bsf:v", bitstreamFilter, videoFile });
            MkvmergeCommand recompose = new MkvmergeCommand(outputFile);
            List<string> videoOptions = new List<string>(MkvmergeCommand.TrackOptions(0, video)) { "--no-chapters", "--no-attachments", "--no-global-tags", "--no-track-tags" };
            int videoInput = recompose.AddInput(videoFile, videoOptions.ToArray());
            int fileInput = recompose.AddInput(inputFile, "--video-tracks", "!" + video.Id);
            foreach (TrackInfo track in tracks)
                recompose.Place(track.Id, track.Id == video.Id ? videoInput : fileInput, track.Id == video.Id ? 0 : track.Id);
            recompose.AddTrackOrder();
            recompose.AddGlobalOptions(globalOptions);
            this.RunMkvmerge(recompose.ToArguments());
        }

        /// <summary>
        /// Esegue ffmpeg con throw su errore
        /// </summary>
        /// <param name="args">Argomenti ffmpeg</param>
        public void RunFfmpeg(IEnumerable<string> args)
        {
            this.Run(this._ffmpeg, args);
        }

        /// <summary>
        /// Esegue ffmpeg senza throw
        /// </summary>
        /// <param name="args">Argomenti ffmpeg</param>
        /// <returns>Exit code del processo</returns>
        public int RunFfmpegNoThrow(IEnumerable<string> args)
        {
            return this.RunNoThrow(this._ffmpeg, args);
        }

        /// <summary>
        /// Legge i capitoli dell'edizione predefinita del sorgente
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <returns>Capitoli, avvisi ed eventuale errore</returns>
        public ChapterReadResult ReadChapters(string inputFile)
        {
            return ChapterTimelineService.Read(this._mkvextract, inputFile);
        }

        /// <summary>
        /// Estrae traccia raw
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <param name="trackId">ID traccia mkvmerge</param>
        /// <param name="outputFile">File raw destinazione</param>
        public void ExtractRawTrack(string inputFile, int trackId, string outputFile)
        {
            this.Run(this._mkvextract, new string[] { inputFile, "tracks", trackId + ":" + outputFile });
        }

        /// <summary>
        /// Esegue mkvmerge
        /// </summary>
        /// <param name="args">Argomenti mkvmerge</param>
        public void RunMkvmerge(IEnumerable<string> args)
        {
            ProcessResult result = this.RunMkvmergeResult(args);
            // L'uscita 1 sono avvisi con il file scritto per intero: lo verificano i controlli su fotogrammi, tempi e tracce
            if (result.ExitCode == 1)
                LogMkvmergeWarnings(result);
            else if (result.ExitCode != 0)
                this.Fail(this._mkvmerge, result);
        }

        /// <summary>
        /// Riporta nel log di Split gli avvisi di mkvmerge
        /// </summary>
        /// <param name="result">Risultato del processo</param>
        public static void LogMkvmergeWarnings(ProcessResult result)
        {
            foreach (string line in MkvmergeLines(result))
                if (line.StartsWith("Warning:", StringComparison.Ordinal))
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Warning, AppText.F("split.tools.mkvmergeWarning", line.Substring("Warning:".Length).Trim()));
        }

        /// <summary>
        /// Opzioni globali di mkvmerge per il file di una clip dello Split: con una traccia audio Opus il lacing si disattiva. mkvmerge
        /// mette più frame Opus in un blocco e mkvextract dà a tutti il tempo del blocco, quindi alla giunta il taglio dell'audio non
        /// troverebbe il frame che finisce oltre il video e la clip successiva partirebbe dopo l'audio della precedente
        /// </summary>
        /// <param name="tracks">Tracce del file della clip</param>
        /// <returns>--disable-lacing con una traccia Opus, altrimenti nessuna opzione</returns>
        public static string[] ClipLacingOptions(List<TrackInfo> tracks)
        {
            return tracks.Exists(IsOpusAudio)
                ? new string[] { "--disable-lacing" }
                : Array.Empty<string>();
        }

        /// <summary>
        /// Indica se la traccia è audio Opus, di cui mkvmerge scarta il pre-roll quando taglia una parte insieme al video
        /// </summary>
        /// <param name="track">Traccia da controllare</param>
        /// <returns>True per una traccia audio Opus</returns>
        public static bool IsOpusAudio(TrackInfo track)
        {
            return track.Type == "audio" && track.Codec.IndexOf("Opus", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Indica se la traccia è audio FLAC, che mkvmerge non taglia insieme al video: lo Split lo taglia a parte
        /// </summary>
        /// <param name="track">Traccia da controllare</param>
        /// <returns>True per una traccia audio FLAC</returns>
        public static bool IsFlacAudio(TrackInfo track)
        {
            return track.Type == "audio" && track.Codec.IndexOf("FLAC", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Righe di errore e avviso di mkvmerge, per un messaggio che dica quale parte e traccia
        /// </summary>
        /// <param name="result">Risultato del processo</param>
        /// <returns>Messaggi concatenati, oppure l'intero output se mkvmerge non ne ha scritti</returns>
        public static string MkvmergeMessages(ProcessResult result)
        {
            List<string> lines = MkvmergeLines(result);
            return lines.Count > 0 ? string.Join(" ", lines) : ((result.Stdout ?? "") + "\n" + (result.Stderr ?? "")).Trim();
        }

        /// <summary>
        /// Esegue mkvmerge senza eccezioni sull'exit code, restituendo l'output per diagnostiche precise
        /// </summary>
        /// <param name="args">Argomenti mkvmerge</param>
        /// <returns>Risultato del processo</returns>
        public ProcessResult RunMkvmergeResult(IEnumerable<string> args)
        {
            List<string> list = new List<string>(args);
            this.PrintShortCmd(this._mkvmerge, list);
            return ProcessRunner.Run(this._mkvmerge, list.ToArray());
        }

        /// <summary>
        /// Rileva CFR/VFR della traccia video principale da MediaInfo CLI
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <returns>Modalità framerate rilevata</returns>
        public MkvSplitFrameRateMode DetectFrameRateMode(string inputFile)
        {
            MediaInfoService service = new MediaInfoService(this._mediainfo);
            string mode = service.GetVideoFrameRateMode(inputFile);
            if (mode.IndexOf("Constant", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(mode, "CFR", StringComparison.OrdinalIgnoreCase))
                return MkvSplitFrameRateMode.Cfr;

            if (mode.IndexOf("Variable", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(mode, "VFR", StringComparison.OrdinalIgnoreCase))
                return MkvSplitFrameRateMode.Vfr;

            return MkvSplitFrameRateMode.Unknown;
        }

        /// <summary>
        /// Legge la scansione del video da MediaInfo CLI
        /// </summary>
        /// <param name="inputFile">File sorgente</param>
        /// <returns>Scansione MediaInfo, vuota se non dichiarata</returns>
        /// <exception cref="InvalidOperationException">MediaInfo non risponde: il file non si tratta come progressivo</exception>
        public VideoScanType GetVideoScanType(string inputFile)
        {
            if (!new MediaInfoService(this._mediainfo).TryGetVideoScanType(inputFile, out VideoScanType scanType))
                throw new InvalidOperationException(AppText.F("split.tools.scanTypeReadFailed", Path.GetFileName(inputFile)));
            return scanType;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Legge con mkvextract i timestamps v2 di una traccia, nell'ordine del file, passando da un temporaneo cancellato subito
        /// </summary>
        /// <param name="inputFile">File MKV</param>
        /// <param name="trackId">ID mkvmerge della traccia</param>
        /// <param name="tempPrefix">Prefisso del temporaneo nella cartella temp</param>
        /// <returns>Timestamp in secondi; righe vuote, commenti e valori non numerici sono saltati</returns>
        private List<double> ReadTimestamps(string inputFile, int trackId, string tempPrefix)
        {
            string tempFile = Path.Combine(AppSettingsService.Instance.GetTempFolder(), tempPrefix + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            List<double> timestamps = new List<double>(1 << 17);

            try
            {
                this.Run(this._mkvextract, new string[] { inputFile, "timestamps_v2", trackId.ToString(CultureInfo.InvariantCulture) + ":" + tempFile });
                foreach (string line in File.ReadLines(tempFile))
                {
                    string text = line.Trim();
                    if (text.Length > 0 && text[0] != '#' && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                        timestamps.Add(value / 1000.0);
                }
            }
            finally
            {
                try
                {
                    File.Delete(tempFile);
                }
                catch (IOException)
                {
                }
            }

            return timestamps;
        }

        /// <summary>
        /// Esegue processo con throw su exit code diverso da zero
        /// </summary>
        /// <param name="exe">Eseguibile da invocare</param>
        /// <param name="args">Argomenti processo</param>
        /// <returns>Risultato processo</returns>
        private ProcessResult Run(string exe, IEnumerable<string> args)
        {
            List<string> list = new List<string>(args);
            this.PrintShortCmd(exe, list);
            ProcessResult result = ProcessRunner.Run(exe, list.ToArray());
            if (result.ExitCode != 0)
                this.Fail(exe, result);

            return result;
        }

        /// <summary>
        /// Riporta nel log l'uscita di un processo fallito e interrompe
        /// </summary>
        /// <param name="exe">Eseguibile invocato</param>
        /// <param name="result">Risultato del processo</param>
        private void Fail(string exe, ProcessResult result)
        {
            ConsoleHelper.Write(LogSection.Split, LogLevel.Error, AppText.F("split.tools.commandFailedExit", result.ExitCode));
            if (!string.IsNullOrEmpty(result.Stdout))
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.tools.stdout", Truncate(result.Stdout, 2000)));

            if (!string.IsNullOrEmpty(result.Stderr))
                ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.tools.stderr", Truncate(result.Stderr, 2000)));

            throw new InvalidOperationException(AppText.F("split.tools.commandFailed", Path.GetFileName(exe)));
        }

        /// <summary>
        /// Righe di mkvmerge che iniziano con Error: o Warning:
        /// </summary>
        /// <param name="result">Risultato del processo</param>
        /// <returns>Righe trovate, nell'ordine dell'output</returns>
        private static List<string> MkvmergeLines(ProcessResult result)
        {
            List<string> lines = new List<string>();
            foreach (string raw in ((result.Stdout ?? "") + "\n" + (result.Stderr ?? "")).Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Error:", StringComparison.Ordinal) || line.StartsWith("Warning:", StringComparison.Ordinal))
                    lines.Add(line);
            }
            return lines;
        }

        /// <summary>
        /// Esegue processo senza throw
        /// </summary>
        /// <param name="exe">Eseguibile da invocare</param>
        /// <param name="args">Argomenti processo</param>
        /// <returns>Exit code processo</returns>
        private int RunNoThrow(string exe, IEnumerable<string> args)
        {
            List<string> list = new List<string>(args);
            this.PrintShortCmd(exe, list);
            ProcessResult result = ProcessRunner.Run(exe, list.ToArray());
            return result.ExitCode;
        }

        /// <summary>
        /// Esegue processo leggendo stdout riga per riga
        /// </summary>
        /// <param name="exe">Eseguibile da invocare</param>
        /// <param name="args">Argomenti processo</param>
        /// <param name="onLine">Callback chiamata per ogni riga stdout</param>
        private void RunStreamed(string exe, IEnumerable<string> args, Action<string> onLine)
        {
            List<string> list = new List<string>(args);
            this.PrintShortCmd(exe, list);
            ProcessResult result = ProcessRunner.RunWithStdoutLines(exe, list, onLine);
            if (result.ExitCode != 0)
            {
                if (!string.IsNullOrEmpty(result.Stderr))
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Text, AppText.F("split.tools.stderr", Truncate(result.Stderr, 2000)));

                throw new InvalidOperationException(AppText.F("split.tools.commandFailed", Path.GetFileName(exe)));
            }
        }

        /// <summary>
        /// Log sintetico command line
        /// </summary>
        /// <param name="exe">Eseguibile invocato</param>
        /// <param name="args">Argomenti processo</param>
        private void PrintShortCmd(string exe, List<string> args)
        {
            StringBuilder sb = new StringBuilder("  $ ");
            string text;
            sb.Append(Path.GetFileName(exe));
            for (int i = 0; i < args.Count; i++)
            {
                sb.Append(' ');
                sb.Append(args[i]);
            }
            text = sb.ToString();
            if (text.Length > 120)
                text = text.Substring(0, 117) + "...";

            ConsoleHelper.Write(LogSection.Split, LogLevel.Debug, text);
        }

        /// <summary>
        /// Tronca testo
        /// </summary>
        /// <param name="text">Testo di input</param>
        /// <param name="max">Numero massimo caratteri</param>
        /// <returns>Testo troncato</returns>
        private static string Truncate(string text, int max)
        {
            if (text == null)
                return "";

            if (text.Length > max)
                return text.Substring(0, max);

            return text;
        }

        #endregion
    }
}
