using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;

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
            this.Chapters = new List<MkvSplitChapter>();
            this.SourcePts = new double[0];
            this.KeyFlags = new List<MkvSplitFrameInfo>();
            this.FrameRateMode = MkvSplitFrameRateMode.Unknown;
        }

        #endregion

        #region Proprietà

        /// <summary>Capitoli del sorgente</summary>
        public List<MkvSplitChapter> Chapters { get; set; }

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
        private const int FRAME_INFO_SIZE_BYTES = 16;

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
            analysis.Chapters = MkvSplitExternalTools.Instance.GetChapters(inputFile);
            analysis.Duration = MkvSplitExternalTools.Instance.GetDuration(inputFile);
            analysis.SourcePts = MkvSplitExternalTools.Instance.ExtractSourcePts(inputFile);
            analysis.PacketCount = MkvSplitExternalTools.Instance.CountPackets(inputFile);
            analysis.KeyFlags = MkvSplitExternalTools.Instance.GetKeyFlags(inputFile);
            analysis.VideoParams = MkvSplitExternalTools.Instance.GetVideoParams(inputFile);
            analysis.FrameRateMode = MkvSplitExternalTools.Instance.DetectFrameRateMode(inputFile);

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
        /// Stima la memoria occupata dalle strutture proporzionali al numero di frame
        /// </summary>
        /// <param name="analysis">Analisi da misurare</param>
        /// <returns>Dimensione stimata in byte</returns>
        private static long EstimateSizeBytes(MkvSplitAnalysis analysis)
        {
            long sourcePtsBytes = analysis.SourcePts != null ? analysis.SourcePts.LongLength * sizeof(double) : 0;
            long keyFlagsBytes = analysis.KeyFlags != null ? (long)analysis.KeyFlags.Count * FRAME_INFO_SIZE_BYTES : 0;
            return sourcePtsBytes + keyFlagsBytes;
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
