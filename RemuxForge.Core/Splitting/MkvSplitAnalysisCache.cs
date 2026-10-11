using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Analisi di un sorgente riusata dal piano, dall'esecuzione e dall'editor
    /// </summary>
    public class MkvSplitAnalysis
    {
        #region Costruttore

        /// <summary>Costruttore</summary>
        public MkvSplitAnalysis()
        {
            this.Chapters = new List<ChapterMark>();
            this.ChapterWarnings = new List<string>();
            this.SourcePts = new double[0];
            this.KeyFlags = new List<MkvSplitFrameInfo>();
            this.FrameRateMode = MkvSplitFrameRateMode.Unknown;
            this.ScanType = new VideoScanType("");
            this.Tracks = new List<TrackInfo>();
            this.Subtitles = new List<MkvSplitSubtitleTrack>();
            this.TrackInventoryError = "";
        }

        #endregion

        #region Proprietà

        /// <summary>Capitoli del sorgente</summary>
        public List<ChapterMark> Chapters { get; set; }

        /// <summary>Avvisi localizzati sui capitoli non riportati dall'edizione piatta</summary>
        public List<string> ChapterWarnings { get; set; }

        /// <summary>Durata in secondi</summary>
        public double Duration { get; set; }

        /// <summary>PTS ordinati crescente</summary>
        public double[] SourcePts { get; set; }

        /// <summary>Numero di packet video contati da ffprobe</summary>
        public int PacketCount { get; set; }

        /// <summary>Flag keyframe per packet</summary>
        public List<MkvSplitFrameInfo> KeyFlags { get; set; }

        /// <summary>Parametri video</summary>
        public MkvSplitVideoParams VideoParams { get; set; }

        /// <summary>Modalità frame rate rilevata</summary>
        public MkvSplitFrameRateMode FrameRateMode { get; set; }

        /// <summary>Scansione MediaInfo del video, vuota se non dichiarata: un video interlacciato si taglia solo al keyframe (D07)</summary>
        public VideoScanType ScanType { get; set; }

        /// <summary>Profilo del video per il taglio, dal codec dei parametri video e dalla scansione; fuori dallo snapshot dell'editor, che conserva i due campi</summary>
        [JsonIgnore]
        public MkvSplitVideoProfile VideoProfile => new MkvSplitVideoProfile(this.VideoParams?.CodecName, this.ScanType);

        /// <summary>Tracce del Matroska letto, con gli ID del sorgente; fuori dallo snapshot dell'editor</summary>
        [JsonIgnore]
        public List<TrackInfo> Tracks { get; set; }

        /// <summary>Tracce di sottotitoli ritagliabili lette dal Matroska, con gli eventi nel tempo del sorgente; fuori dallo snapshot dell'editor</summary>
        [JsonIgnore]
        public List<MkvSplitSubtitleTrack> Subtitles { get; set; }

        /// <summary>Errore localizzato di lettura delle tracce o dei sottotitoli, vuoto se la lettura è riuscita</summary>
        [JsonIgnore]
        public string TrackInventoryError { get; set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Equivalente di Python bisect.bisect_left su PTS ordinati: il minimo indice i tale che pts[i] &gt;= value
        /// </summary>
        /// <param name="pts">PTS ordinati crescente</param>
        /// <param name="value">Tempo in secondi</param>
        /// <returns>Minimo indice i tale che pts[i] &gt;= value, la lunghezza se non c'è</returns>
        public static int BisectLeft(double[] pts, double value)
        {
            int lo = 0;
            int hi = pts.Length;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (pts[mid] < value) { lo = mid + 1; }
                else { hi = mid; }
            }
            return lo;
        }

        /// <summary>
        /// Ricerca binaria dell'ultimo frame con PTS minore o uguale al tempo indicato
        /// </summary>
        /// <param name="seconds">Tempo sorgente in secondi</param>
        /// <returns>Indice del frame che contiene il tempo, almeno 0</returns>
        public int ContainingFrame(double seconds)
        {
            int low = 0;
            int high = this.SourcePts.Length;
            while (low < high)
            {
                int mid = low + ((high - low) >> 1);
                if (this.SourcePts[mid] <= seconds)
                    low = mid + 1;
                else
                    high = mid;
            }
            return Math.Max(0, low - 1);
        }

        /// <summary>
        /// Capitoli del sorgente che marcano un fotogramma della clip, nel tempo del sorgente. Il fotogramma marcato è quello
        /// di MkvSplitSegmentService.ChapterFrame, entro lo scarto di arrotondamento (ChapterTimelineService.FRAME_ROUNDING_NS),
        /// come nella costruzione dei segmenti. Un capitolo che cade fra il confine richiesto e quello effettivo, cioè dentro la
        /// distanza dello snap, segue il confine: passa all'inizio della parte nuova invece di restare in coda alla precedente;
        /// il capitolo sul confine richiesto lo segue anche quando lo snap va indietro. Un capitolo sul primo fotogramma della
        /// clip cade sul suo inizio, anche se il suo tempo lo precede (primo PTS del file dopo lo zero)
        /// </summary>
        /// <param name="clip">Clip con i confini effettivi</param>
        /// <param name="startSeconds">Tempo sorgente del primo fotogramma</param>
        /// <param name="snappedBy">Clip i cui confini chiesti prima dello snap spostano i capitoli: la clip stessa nel documento,
        /// tutti i segmenti nel piano della regola</param>
        /// <returns>Capitoli della clip</returns>
        public List<ChapterMark> SelectChapters(MkvSplitClip clip, double startSeconds, IEnumerable<MkvSplitClip> snappedBy)
        {
            List<ChapterMark> result = new List<ChapterMark>();

            foreach (ChapterMark chapter in this.Chapters)
            {
                int frame = MkvSplitSegmentService.ChapterFrame(this.SourcePts, chapter.StartSeconds);
                foreach (MkvSplitClip snapped in snappedBy)
                {
                    int? boundary = snapped.SnappedBoundary(frame);
                    if (boundary.HasValue)
                    {
                        frame = boundary.Value;
                        break;
                    }
                }
                if (frame < clip.StartFrame || frame >= clip.EndFrameExclusive)
                    continue;
                if (frame == clip.StartFrame)
                {
                    ChapterMark placed = chapter.Clone();
                    placed.StartNs = (long)Math.Round(startSeconds * 1000000000.0);
                    result.Add(placed);
                }
                else
                    result.Add(chapter);
            }

            return result;
        }

        #endregion
    }

    /// <summary>
    /// Cache delle analisi dei sorgenti, invalidata da data e dimensione del file
    /// </summary>
    public class MkvSplitAnalysisCache
    {
        #region Costanti

        /// <summary>Memoria massima occupata dalle analisi conservate</summary>
        private const long CACHE_LIMIT_BYTES = 256L * 1024L * 1024L;

        /// <summary>Dimensione CLR corrente di un elemento della mappa packet</summary>
        private const int FRAME_INFO_SIZE_BYTES = 24;

        #endregion

        #region Variabili di classe

        /// <summary>Istanza condivisa</summary>
        private static readonly MkvSplitAnalysisCache _instance = new MkvSplitAnalysisCache();

        /// <summary>Analisi memorizzate per chiave di file</summary>
        private readonly Dictionary<string, CacheEntry> _entries;

        /// <summary>Chiavi ordinate dall'analisi usata più di recente a quella meno recente</summary>
        private readonly LinkedList<string> _order;

        /// <summary>Chiave corrente associata a ogni percorso completo</summary>
        private readonly Dictionary<string, string> _pathKeys;

        /// <summary>Memoria stimata occupata dalle analisi correnti</summary>
        private long _cacheBytes;

        /// <summary>Lock di accesso</summary>
        private readonly object _lock;

        #endregion

        #region Costruttore

        /// <summary>Costruttore privato</summary>
        private MkvSplitAnalysisCache()
        {
            this._entries = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
            this._order = new LinkedList<string>();
            this._pathKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            this._lock = new object();
            this._cacheBytes = 0;
        }

        #endregion

        #region Proprietà

        /// <summary>Istanza condivisa della cache</summary>
        public static MkvSplitAnalysisCache Instance
        {
            get { return _instance; }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce l'analisi del file, calcolandola solo se non è già in cache
        /// </summary>
        /// <param name="inputFile">File da analizzare</param>
        /// <returns>Analisi del sorgente</returns>
        public MkvSplitAnalysis GetOrBuild(string inputFile)
        {
            string key;
            FileInfo info;

            info = new FileInfo(inputFile);
            key = info.FullName + "|" + info.LastWriteTimeUtc.Ticks.ToString() + "|" + info.Length.ToString();

            lock (this._lock)
            {
                if (this._entries.TryGetValue(key, out CacheEntry cached))
                {
                    this._order.Remove(cached.Node);
                    this._order.AddFirst(cached.Node);
                    return cached.Analysis;
                }
            }

            MkvSplitAnalysis analysis = new MkvSplitAnalysis();
            // La chiave resta il sorgente: la normalizzazione è deterministica, l'analisi vale anche per il temporaneo successivo.
            // Il lease è condiviso con l'editor o l'esecuzione che lo tengono già; altrimenti il temporaneo sparisce a fine analisi
            using (NormalizedMediaLease lease = NormalizedMediaStore.Instance.Acquire(inputFile, true))
            {
                string media = lease.Path;
                ChapterReadResult chapters = MkvSplitExternalTools.Instance.ReadChapters(media);

                // Capitoli che non si possono spostare nel tempo (ordered) bloccano il sorgente
                if (chapters.Error.Length > 0)
                    throw new InvalidOperationException(chapters.Error);

                analysis.Chapters = chapters.Chapters;
                analysis.ChapterWarnings = chapters.Warnings;
                analysis.PacketCount = MkvSplitExternalTools.Instance.CountPackets(media);
                analysis.SourcePts = MkvSplitExternalTools.Instance.ExtractSourcePts(media, analysis.PacketCount, out double videoEnd);
                // Senza la fine esatta del video si ripiega sulla durata del container letta da mkvmerge
                MkvFileInfo containerInfo = MkvSplitExternalTools.Instance.GetFileInfo(media);
                if (!double.IsFinite(videoEnd))
                {
                    videoEnd = containerInfo != null && containerInfo.ContainerDurationNs > 0 ? containerInfo.ContainerDurationNs / 1000000000.0 : double.NaN;
                    ConsoleHelper.Write(LogSection.Split, LogLevel.Warning, AppText.F("split.tools.videoEndFromContainer", Path.GetFileName(inputFile)));
                }
                analysis.Duration = videoEnd;
                analysis.VideoParams = MkvSplitExternalTools.Instance.GetVideoParams(media);
                analysis.KeyFlags = MkvSplitExternalTools.Instance.GetKeyFlags(media, analysis.VideoParams.CodecName);
                analysis.FrameRateMode = MkvSplitExternalTools.Instance.DetectFrameRateMode(media);
                analysis.ScanType = MkvSplitExternalTools.Instance.GetVideoScanType(inputFile);
                // Inventario delle tracce ed eventi dei sottotitoli: il piano eseguibile non rilegge il Matroska. Un errore qui
                // blocca solo l'esecuzione, come diagnostica del piano, non l'analisi
                string temporary = Path.Combine(AppSettingsService.Instance.GetTempFolder(), "remuxforge-split-sub-" + Guid.NewGuid().ToString("N"));
                try
                {
                    if (containerInfo == null)
                        throw new InvalidOperationException(AppText.T("split.montage.trackInventory"));
                    analysis.Tracks = containerInfo.Tracks;
                    if (analysis.Tracks.Any(item => item.Type == "subtitles" && MkvSplitSubtitleClippers.IsSupported(item)))
                    {
                        Directory.CreateDirectory(temporary);
                        foreach (TrackInfo track in analysis.Tracks.Where(item => item.Type == "subtitles" && MkvSplitSubtitleClippers.IsSupported(item)))
                            analysis.Subtitles.Add(MkvSplitSubtitleClippers.ReadSource(media, track, temporary, analysis.Duration));
                    }
                }
                catch (Exception exception)
                {
                    analysis.Subtitles.Clear();
                    analysis.TrackInventoryError = exception.Message;
                }
                finally
                {
                    if (Directory.Exists(temporary))
                        Directory.Delete(temporary, true);
                }
                lease.VerifiedContent = analysis;
            }

            lock (this._lock)
            {
                if (this._entries.TryGetValue(key, out CacheEntry cached))
                {
                    this._order.Remove(cached.Node);
                    this._order.AddFirst(cached.Node);
                    return cached.Analysis;
                }

                if (this._pathKeys.TryGetValue(info.FullName, out string previousKey))
                    this.RemoveEntry(previousKey);

                LinkedListNode<string> node = this._order.AddFirst(key);
                long sizeBytes = EstimateSizeBytes(analysis);
                this._entries[key] = new CacheEntry(info.FullName, analysis, node, sizeBytes);
                this._pathKeys[info.FullName] = key;
                this._cacheBytes += sizeBytes;

                while (this._cacheBytes > CACHE_LIMIT_BYTES && this._order.Last != null)
                    this.RemoveEntry(this._order.Last.Value);
            }

            return analysis;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Stima la memoria occupata dalle strutture proporzionali al numero di frame e dai sottotitoli letti
        /// </summary>
        /// <param name="analysis">Analisi da misurare</param>
        /// <returns>Dimensione stimata in byte</returns>
        private static long EstimateSizeBytes(MkvSplitAnalysis analysis)
        {
            long sourcePtsBytes = analysis.SourcePts != null ? analysis.SourcePts.LongLength * sizeof(double) : 0;
            long keyFlagsBytes = analysis.KeyFlags != null ? (long)analysis.KeyFlags.Count * FRAME_INFO_SIZE_BYTES : 0;
            long subtitleBytes = 0;
            // I sottotitoli bitmap tengono in memoria i display-set: contano come i dati per fotogramma
            foreach (MkvSplitSubtitleTrack subtitle in analysis.Subtitles)
            {
                subtitleBytes += (subtitle.SourceContent?.Length ?? 0) * sizeof(char) + (subtitle.BitmapPresentationHeader?.LongLength ?? 0);
                foreach (MkvSplitSubtitleEvent item in subtitle.Events)
                    subtitleBytes += item.SizeBytes;
            }
            return sourcePtsBytes + keyFlagsBytes + subtitleBytes;
        }

        /// <summary>
        /// Rimuove una voce e aggiorna indice per percorso, ordine e memoria
        /// </summary>
        /// <param name="key">Chiave completa della voce</param>
        private void RemoveEntry(string key)
        {
            if (!this._entries.TryGetValue(key, out CacheEntry entry))
                return;

            this._entries.Remove(key);
            this._order.Remove(entry.Node);
            this._cacheBytes -= entry.SizeBytes;
            if (this._pathKeys.TryGetValue(entry.FilePath, out string currentKey) && string.Equals(currentKey, key, StringComparison.Ordinal))
                this._pathKeys.Remove(entry.FilePath);
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Voce della cache con metadati necessari allo sfratto LRU
        /// </summary>
        private class CacheEntry
        {
            /// <summary>Costruttore</summary>
            public CacheEntry(string filePath, MkvSplitAnalysis analysis, LinkedListNode<string> node, long sizeBytes)
            {
                this.FilePath = filePath;
                this.Analysis = analysis;
                this.Node = node;
                this.SizeBytes = sizeBytes;
            }

            /// <summary>Percorso completo senza versione</summary>
            public string FilePath { get; private set; }

            /// <summary>Analisi memorizzata</summary>
            public MkvSplitAnalysis Analysis { get; private set; }

            /// <summary>Nodo nell'ordine LRU</summary>
            public LinkedListNode<string> Node { get; private set; }

            /// <summary>Memoria stimata della voce</summary>
            public long SizeBytes { get; private set; }
        }

        #endregion
    }
}
