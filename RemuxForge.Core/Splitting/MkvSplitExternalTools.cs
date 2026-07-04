using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
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
        /// Percorso mkvpropedit
        /// </summary>
        private string _mkvpropedit;

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
            this._mkvpropedit = "";
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
            this._mkvpropedit = resolver.ResolveMkvPropEditPath(this._mkvmerge, true);
            this._ffmpeg = resolver.ResolveFfmpegPath(true, true);
            this._ffprobe = resolver.ResolveFfprobePath(this._ffmpeg, true);
            this._mediainfo = resolver.ResolveMediaInfoPath(true);

            if (string.IsNullOrEmpty(this._mkvmerge))
                missing.Add("mkvmerge");

            if (string.IsNullOrEmpty(this._mkvextract))
                missing.Add("mkvextract");

            if (string.IsNullOrEmpty(this._mkvpropedit))
                missing.Add("mkvpropedit");

            if (string.IsNullOrEmpty(this._ffmpeg))
                missing.Add("ffmpeg");

            if (string.IsNullOrEmpty(this._ffprobe))
                missing.Add("ffprobe");

            if (string.IsNullOrEmpty(this._mediainfo))
                missing.Add("mediainfo");

            if (missing.Count > 0)
            {
                throw new InvalidOperationException("Tool mancanti: " + string.Join(", ", missing));
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
                "-show_entries", "stream=pix_fmt,color_space,color_primaries,color_transfer,color_range,codec_name",
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
            }

            return vp;
        }

        /// <summary>
        /// Legge durata container via ffprobe
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <returns>Durata container in secondi</returns>
        public double GetDuration(string inputFile)
        {
            ProcessResult r = this.Run(this._ffprobe, new string[] { "-v", "quiet", "-show_entries", "format=duration", "-of", "csv=p=0", inputFile });
            return double.Parse(r.Stdout.Trim(), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Estrae PTS video via mkvextract timestamps_v2
        /// </summary>
        /// <param name="sourceFile">File sorgente MKV</param>
        /// <returns>Array ordinato dei PTS video in secondi</returns>
        public double[] ExtractSourcePts(string sourceFile)
        {
            string tempFile;
            List<double> pts = new List<double>(1 << 17);
            StreamReader reader = null;
            string line;
            double value;

            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "Extracting source PTS...");
            tempFile = Path.Combine(Path.GetTempPath(), "mkv_pts_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            try
            {
                this.Run(this._mkvextract, new string[] { sourceFile, "timestamps_v2", "0:" + tempFile });
                reader = new StreamReader(tempFile);
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (string.IsNullOrEmpty(line) || line[0] == '#')
                        continue;

                    if (double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        pts.Add(value / 1000.0);
                    }
                }
            }
            finally
            {
                if (reader != null)
                    reader.Dispose();

                try
                {
                    File.Delete(tempFile);
                }
                catch (IOException)
                {
                }
            }

            if (pts.Count > 0)
                pts.RemoveAt(pts.Count - 1);

            double[] arr = pts.ToArray();
            Array.Sort(arr);
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  " + arr.Length + " PTS extracted");
            return arr;
        }

        /// <summary>
        /// Mappa posizione, dimensione e keyflag di ogni packet del raw stream
        /// </summary>
        /// <param name="rawFile">File raw da analizzare</param>
        /// <returns>Mappa dei frame con posizione, dimensione e keyflag</returns>
        public List<MkvSplitFrameInfo> GetFrameByteMap(string rawFile)
        {
            List<MkvSplitFrameInfo> frames = new List<MkvSplitFrameInfo>(1 << 17);
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "Mapping frame byte positions...");

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

            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  " + frames.Count + " frames mapped");
            return frames;
        }

        /// <summary>
        /// Legge solo keyflag video
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <returns>Lista dei packet con flag keyframe</returns>
        public List<MkvSplitFrameInfo> GetKeyFlags(string inputFile)
        {
            List<MkvSplitFrameInfo> frames = new List<MkvSplitFrameInfo>(1 << 17);
            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "Probing keyframe flags...");

            this.RunStreamed(this._ffprobe, new string[]
            {
                "-v", "quiet", "-select_streams", "v:0",
                "-show_entries", "packet=flags", "-of", "csv=p=0", inputFile
            }, delegate (string line)
            {
                MkvSplitFrameInfo f;
                line = line.Trim();
                if (string.IsNullOrEmpty(line))
                    return;

                f = new MkvSplitFrameInfo();
                f.Key = line.IndexOf('K') >= 0;
                frames.Add(f);
            });

            ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  " + frames.Count + " packets probed");
            return frames;
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
        /// Probe dimensione/posizione packet
        /// </summary>
        /// <param name="file">File video da analizzare</param>
        /// <returns>Lista dimensione/posizione dei packet</returns>
        public List<(int size, long pos)> ProbePacketsSizePos(string file)
        {
            List<(int size, long pos)> frames = new List<(int, long)>();
            this.RunStreamed(this._ffprobe, new string[]
            {
                "-v", "quiet", "-select_streams", "v:0",
                "-show_entries", "packet=pos,size", "-of", "csv=p=0", file
            }, delegate (string line)
            {
                string[] parts = line.Split(',');
                if (parts.Length < 2)
                    return;

                frames.Add((int.Parse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture), long.Parse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture)));
            });

            return frames;
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
        /// Estrae capitoli in formato simple
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <returns>Lista capitoli estratti</returns>
        public List<MkvSplitChapter> GetChapters(string inputFile)
        {
            ProcessResult r;
            List<MkvSplitChapter> chapters = new List<MkvSplitChapter>();
            int eq;
            string key;
            string val;
            bool hasName;
            string[] hms;
            int h;
            int m;
            double s;
            MkvSplitChapter chapter;

            r = this.Run(this._mkvextract, new string[] { inputFile, "chapters", "-s" });
            foreach (string rawLine in r.Stdout.Split('\n'))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line))
                    continue;

                eq = line.IndexOf('=');
                if (eq < 0)
                    continue;

                key = line.Substring(0, eq);
                val = line.Substring(eq + 1);
                hasName = key.IndexOf("NAME", StringComparison.Ordinal) >= 0;
                if (!hasName)
                {
                    hms = val.Split(':');
                    if (hms.Length == 3)
                    {
                        h = int.Parse(hms[0], CultureInfo.InvariantCulture);
                        m = int.Parse(hms[1], CultureInfo.InvariantCulture);
                        s = double.Parse(hms[2], CultureInfo.InvariantCulture);
                        chapter = new MkvSplitChapter();
                        chapter.Timestamp = h * 3600.0 + m * 60.0 + s;
                        chapter.TsStr = val;
                        chapters.Add(chapter);
                    }
                }
                else if (chapters.Count > 0)
                {
                    chapters[chapters.Count - 1].Name = val;
                }
            }

            return chapters;
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
            this.Run(this._mkvmerge, args);
        }

        /// <summary>
        /// Esegue mkvpropedit
        /// </summary>
        /// <param name="args">Argomenti mkvpropedit</param>
        public void RunMkvpropedit(IEnumerable<string> args)
        {
            this.Run(this._mkvpropedit, args);
        }

        /// <summary>
        /// True se il file contiene audio FLAC
        /// </summary>
        /// <param name="inputFile">File sorgente MKV</param>
        /// <returns>True se almeno una traccia audio è FLAC</returns>
        public bool HasFlacAudio(string inputFile)
        {
            MkvToolsService service = new MkvToolsService(this._mkvmerge);
            MkvFileInfo info = service.GetFileInfo(inputFile);
            if (info == null)
                return false;

            for (int i = 0; i < info.Tracks.Count; i++)
            {
                if (string.Equals(info.Tracks[i].Type, "audio", StringComparison.OrdinalIgnoreCase) &&
                    info.Tracks[i].Codec.IndexOf("FLAC", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Rileva CFR/VFR da MediaInfo CLI
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

        #endregion

        #region Metodi privati

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
            {
                ConsoleHelper.Write(LogSection.Split, LogLevel.Error, "  FAILED (exit " + result.ExitCode + ")");
                if (!string.IsNullOrEmpty(result.Stdout))
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  stdout: " + Truncate(result.Stdout, 2000));

                if (!string.IsNullOrEmpty(result.Stderr))
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  stderr: " + Truncate(result.Stderr, 2000));

                throw new InvalidOperationException("Command failed: " + Path.GetFileName(exe));
            }

            return result;
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
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Text, "  stderr: " + Truncate(result.Stderr, 2000));

                throw new InvalidOperationException("Command failed: " + Path.GetFileName(exe));
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
