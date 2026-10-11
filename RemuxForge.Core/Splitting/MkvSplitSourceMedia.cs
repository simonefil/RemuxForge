using RemuxForge.Core.Configuration;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Matroska letti dallo Split al posto dei sorgenti: un temporaneo per sorgente, vivo finché qualcuno ne tiene un lease
    /// </summary>
    /// <remarks>
    /// I sorgenti restano l'identità del documento, dei nomi e delle verifiche di modifica; ogni lettura del video passa da qui (D13).
    /// Anche gli MKV con MPEG-4 Part 2 in modalità Video for Windows vengono rimuxati: il taglio esatto lo vuole nativo.
    /// Analisi, editor ed esecuzione dello stesso sorgente condividono il temporaneo: l'ultimo lease rilasciato lo cancella.
    /// </remarks>
    public sealed class MkvSplitSourceMedia
    {
        #region Variabili di classe

        /// <summary>
        /// Istanza condivisa
        /// </summary>
        private static readonly MkvSplitSourceMedia s_instance = new MkvSplitSourceMedia();

        /// <summary>
        /// Voce corrente per percorso completo del sorgente
        /// </summary>
        private readonly Dictionary<string, Entry> _media = new Dictionary<string, Entry>(StringComparer.Ordinal);

        #endregion

        #region Proprietà

        /// <summary>
        /// Istanza condivisa
        /// </summary>
        public static MkvSplitSourceMedia Instance { get { return s_instance; } }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Prende un lease sul Matroska da leggere per il sorgente. La normalizzazione avviene al primo accesso al percorso,
        /// fuori dal lock globale: sorgenti diversi si normalizzano in parallelo, lo stesso sorgente una volta sola
        /// </summary>
        /// <param name="sourcePath">Sorgente scelto dall'utente</param>
        /// <returns>Lease da rilasciare con Dispose</returns>
        public MkvSplitSourceMediaLease Acquire(string sourcePath)
        {
            string fullPath = Path.GetFullPath(sourcePath);
            MkvSplitSourceIdentity identity = MkvSplitSourceIdentity.FromFile(fullPath);
            Entry entry;

            lock (this._media)
            {
                // Un sorgente cambiato apre una voce nuova: la vecchia resta ai lease che la tengono e sparisce con l'ultimo
                if (!this._media.TryGetValue(fullPath, out entry) || !entry.Identity.Matches(identity))
                {
                    entry = new Entry(fullPath, identity);
                    this._media[fullPath] = entry;
                }
                entry.References++;
            }

            return new MkvSplitSourceMediaLease(this, entry);
        }

        #endregion

        #region Metodi interni

        /// <summary>
        /// Rilascia un riferimento alla voce e la cancella con l'ultimo
        /// </summary>
        /// <param name="entry">Voce del lease</param>
        internal void Release(Entry entry)
        {
            lock (this._media)
            {
                entry.References--;
                if (entry.References > 0)
                    return;
                if (this._media.TryGetValue(entry.FullPath, out Entry current) && ReferenceEquals(current, entry))
                    this._media.Remove(entry.FullPath);
            }

            if (entry.Media.IsValueCreated)
                entry.Media.Value.Dispose();
        }

        #endregion

        #region Tipi annidati

        /// <summary>
        /// Temporaneo di un sorgente con i riferimenti che lo tengono vivo
        /// </summary>
        internal sealed class Entry
        {
            #region Costruttore

            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="fullPath">Percorso completo del sorgente</param>
            /// <param name="identity">Identità del sorgente al momento della richiesta</param>
            public Entry(string fullPath, MkvSplitSourceIdentity identity)
            {
                this.FullPath = fullPath;
                this.Identity = identity;
                this.Media = new Lazy<NormalizedMediaFile>(() =>
                {
                    ToolPathResolverService resolver = new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder);
                    NormalizedMediaFile media = new MediaContainerNormalizer(resolver.ResolveMkvMergePath(false), AppSettingsService.Instance.GetTempFolder()).Normalize(fullPath, true, CancellationToken.None);
                    if (!media.Success)
                        throw new InvalidOperationException(media.Error);
                    return media;
                }, LazyThreadSafetyMode.ExecutionAndPublication);
            }

            #endregion

            #region Proprietà

            /// <summary>
            /// Percorso completo del sorgente
            /// </summary>
            public string FullPath { get; }

            /// <summary>
            /// Identità del sorgente al momento della richiesta
            /// </summary>
            public MkvSplitSourceIdentity Identity { get; }

            /// <summary>
            /// Matroska normalizzato, creato al primo accesso
            /// </summary>
            public Lazy<NormalizedMediaFile> Media { get; }

            /// <summary>
            /// Lease aperti sulla voce, protetti dal lock del dizionario
            /// </summary>
            public int References { get; set; }

            /// <summary>
            /// Analisi letta da questo temporaneo, o già verificata contro di esso
            /// </summary>
            public MkvSplitAnalysis VerifiedAnalysis { get; set; }

            #endregion
        }

        #endregion
    }

    /// <summary>
    /// Riferimento al Matroska normalizzato di un sorgente: finché è aperto il temporaneo non viene cancellato
    /// </summary>
    public sealed class MkvSplitSourceMediaLease : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Registro proprietario
        /// </summary>
        private readonly MkvSplitSourceMedia _owner;

        /// <summary>
        /// Voce condivisa
        /// </summary>
        private readonly MkvSplitSourceMedia.Entry _entry;

        /// <summary>
        /// Vero dopo il rilascio
        /// </summary>
        private int _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="owner">Registro proprietario</param>
        /// <param name="entry">Voce condivisa</param>
        internal MkvSplitSourceMediaLease(MkvSplitSourceMedia owner, MkvSplitSourceMedia.Entry entry)
        {
            this._owner = owner;
            this._entry = entry;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Matroska da leggere, normalizzato al primo accesso
        /// </summary>
        public string Path { get { return this._entry.Media.Value.Path; } }

        /// <summary>
        /// True se il Matroska è un temporaneo e non il sorgente stesso
        /// </summary>
        public bool IsTemporary { get { return this._entry.Media.Value.IsTemporary; } }

        /// <summary>
        /// Analisi letta da questo temporaneo, o già verificata contro di esso; null finché nessuno l'ha fatto
        /// </summary>
        public MkvSplitAnalysis VerifiedAnalysis
        {
            get { lock (this._entry) { return this._entry.VerifiedAnalysis; } }
            set { lock (this._entry) { this._entry.VerifiedAnalysis = value; } }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Rilascia il riferimento; l'ultimo cancella il temporaneo
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this._disposed, 1) == 0)
                this._owner.Release(this._entry);
        }

        #endregion
    }
}
