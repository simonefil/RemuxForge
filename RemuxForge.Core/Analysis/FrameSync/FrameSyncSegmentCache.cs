using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace RemuxForge.Core.Analysis.FrameSync
{
    /// <summary>
    /// Cache dei segmenti frame estratti durante una singola esecuzione FrameSync
    /// </summary>
    public class FrameSyncSegmentCache
    {
        #region Costanti

        /// <summary>
        /// Numero massimo segmenti frame ampi mantenuti nella cache
        /// </summary>
        private const int MAX_EXTRACT_SEGMENT_CACHE = 6;

        /// <summary>
        /// Durata minima per mantenere un segmento nella cache generale
        /// </summary>
        private const double MIN_CACHED_EXTRACT_DURATION_SEC = 30.0;

        #endregion

        #region Delegati

        /// <summary>
        /// Handler di estrazione segmento
        /// </summary>
        public delegate void ExtractSegmentHandler(FrameExtractProfile profile, out List<byte[]> frames, out double[] timestampsMs);

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Configurazione VideoSync
        /// </summary>
        private readonly VideoSyncConfig _videoSyncConfig;

        /// <summary>
        /// Lock cache segmenti estratti
        /// </summary>
        private readonly object _cacheLock;

        /// <summary>
        /// Cache segmenti estratti riusabili per profilo
        /// </summary>
        private readonly List<CachedExtractSegment> _segments;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public FrameSyncSegmentCache(VideoSyncConfig videoSyncConfig)
        {
            this._videoSyncConfig = videoSyncConfig;
            this._cacheLock = new object();
            this._segments = new List<CachedExtractSegment>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Estrae un segmento usando la cache generale della sincronizzazione corrente
        /// </summary>
        public bool Extract(FrameExtractProfile profile, ExtractSegmentHandler extractor, SemaphoreSlim semaphore, out List<byte[]> frames, out double[] timestampsMs, out long elapsedMs)
        {
            Stopwatch stopwatch = new Stopwatch();
            bool cacheHit;
            stopwatch.Start();
            cacheHit = this.TryExtractFromCachedSegment(profile, out frames, out timestampsMs);
            if (!cacheHit)
            {
                semaphore.Wait();
                try
                {
                    extractor(profile, out frames, out timestampsMs);
                    this.StoreExtractSegment(profile, frames, timestampsMs);
                }
                finally
                {
                    semaphore.Release();
                }
            }
            stopwatch.Stop();

            elapsedMs = stopwatch.ElapsedMilliseconds;
            return cacheHit;
        }

        /// <summary>
        /// Svuota la cache segmenti della sincronizzazione corrente
        /// </summary>
        public void Clear()
        {
            lock (this._cacheLock)
            {
                this._segments.Clear();
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Costruisce un segmento cache riusabile se l'estrazione è valida
        /// </summary>
        private CachedExtractSegment BuildCachedExtractSegment(FrameExtractProfile profile, List<byte[]> frames, double[] timestampsMs)
        {
            CachedExtractSegment result = null;
            if (profile != null &&
                profile.DurationSec >= MIN_CACHED_EXTRACT_DURATION_SEC &&
                frames != null &&
                timestampsMs != null &&
                frames.Count == timestampsMs.Length &&
                frames.Count > 0)
            {
                result = new CachedExtractSegment();
                result.Profile = profile;
                result.Frames = frames;
                result.TimestampsMs = timestampsMs;
            }

            return result;
        }

        /// <summary>
        /// Salva un segmento nella cache generale se abbastanza ampio da poter essere riusato
        /// </summary>
        private void StoreExtractSegment(FrameExtractProfile profile, List<byte[]> frames, double[] timestampsMs)
        {
            CachedExtractSegment segment = this.BuildCachedExtractSegment(profile, frames, timestampsMs);

            if (segment == null)
            {
                return;
            }

            lock (this._cacheLock)
            {
                for (int i = 0; i < this._segments.Count; i++)
                {
                    if (this._segments[i].Profile.SameExtraction(profile))
                    {
                        return;
                    }
                }

                while (this._segments.Count >= MAX_EXTRACT_SEGMENT_CACHE)
                {
                    this._segments.RemoveAt(0);
                }

                this._segments.Add(segment);
            }
        }

        /// <summary>
        /// Prova a soddisfare un'estrazione usando un segmento già in memoria
        /// </summary>
        private bool TryExtractFromCachedSegment(FrameExtractProfile profile, out List<byte[]> frames, out double[] timestampsMs)
        {
            bool result = false;
            CachedExtractSegment bestSegment = null;
            frames = null;
            timestampsMs = null;

            if (profile == null)
            {
                return result;
            }

            lock (this._cacheLock)
            {
                for (int i = 0; i < this._segments.Count; i++)
                {
                    CachedExtractSegment segment = this._segments[i];
                    if (segment != null && segment.Profile != null && profile.IsContainedIn(segment.Profile))
                    {
                        bestSegment = segment;
                        break;
                    }
                }
            }

            if (bestSegment != null)
            {
                result = this.SliceCachedSegment(bestSegment, profile, out frames, out timestampsMs);
            }

            return result;
        }

        /// <summary>
        /// Estrae la porzione richiesta da un segmento cached
        /// </summary>
        private bool SliceCachedSegment(CachedExtractSegment segment, FrameExtractProfile profile, out List<byte[]> frames, out double[] timestampsMs)
        {
            bool result = false;
            List<byte[]> frameList = new List<byte[]>();
            List<double> timestampList = new List<double>();

            frames = null;
            timestampsMs = null;

            if (segment == null || segment.Frames == null || segment.TimestampsMs == null)
            {
                return result;
            }

            for (int i = 0; i < segment.TimestampsMs.Length; i++)
            {
                if (segment.TimestampsMs[i] >= profile.StartMs && segment.TimestampsMs[i] < profile.EndMs)
                {
                    frameList.Add(segment.Frames[i]);
                    timestampList.Add(segment.TimestampsMs[i]);
                }
            }

            if (frameList.Count >= this._videoSyncConfig.CutSignatureLength)
            {
                frames = frameList;
                timestampsMs = timestampList.ToArray();
                result = true;
            }

            return result;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Segmento estratto riusabile durante una singola esecuzione FrameSync
        /// </summary>
        private class CachedExtractSegment
        {
            /// <summary>
            /// Profilo estrazione associato al segmento
            /// </summary>
            public FrameExtractProfile Profile { get; set; }

            /// <summary>
            /// Frame estratti
            /// </summary>
            public List<byte[]> Frames { get; set; }

            /// <summary>
            /// Timestamp reali dei frame estratti
            /// </summary>
            public double[] TimestampsMs { get; set; }
        }

        #endregion
    }
}
