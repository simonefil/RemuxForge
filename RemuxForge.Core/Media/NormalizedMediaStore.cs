using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Matroska letti al posto dei file scelti dall'utente: un temporaneo per file e modalità, vivo finché qualcuno ne tiene un lease
    /// </summary>
    /// <remarks>
    /// I file originali restano l'identità di documenti, nomi e verifiche di modifica; le letture del video passano da qui (D13).
    /// Chi lavora sullo stesso file nello stesso momento (analisi, editor, esecuzione, unione) condivide il temporaneo: l'ultimo lease
    /// rilasciato lo cancella. La modalità nativa di MPEG-4 Part 2 fa parte della voce: Split e Unisci la chiedono, Remux no.
    /// </remarks>
    public sealed class NormalizedMediaStore
    {
        #region Variabili di classe

        /// <summary>
        /// Istanza condivisa
        /// </summary>
        private static readonly NormalizedMediaStore s_instance = new NormalizedMediaStore();

        /// <summary>
        /// Voce corrente per modalità e percorso completo del file
        /// </summary>
        private readonly Dictionary<string, Entry> _media = new Dictionary<string, Entry>(StringComparer.Ordinal);

        #endregion

        #region Proprietà

        /// <summary>
        /// Istanza condivisa
        /// </summary>
        public static NormalizedMediaStore Instance { get { return s_instance; } }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Prende un lease sul Matroska da leggere per il file. La normalizzazione avviene al primo accesso al percorso,
        /// fuori dal lock globale: file diversi si normalizzano in parallelo, lo stesso file una volta sola
        /// </summary>
        /// <param name="filePath">File scelto dall'utente</param>
        /// <param name="requireNativeMpeg4">True per rimuxare anche gli MKV con MPEG-4 Part 2 in modalità Video for Windows</param>
        /// <param name="mkvMergePath">mkvmerge della normalizzazione, null per quello risolto dalla configurazione; vale quello di chi crea la voce</param>
        /// <returns>Lease da rilasciare con Dispose</returns>
        public NormalizedMediaLease Acquire(string filePath, bool requireNativeMpeg4, string mkvMergePath = null)
        {
            string fullPath = Path.GetFullPath(filePath);
            string key = (requireNativeMpeg4 ? "native|" : "vfw|") + fullPath;
            // Un file sparito non ha identità da leggere: la sua voce vale finché il file non ricompare
            MkvSplitSourceIdentity identity = File.Exists(fullPath) ? MkvSplitSourceIdentity.FromFile(fullPath) : new MkvSplitSourceIdentity { FullPath = fullPath, Length = -1 };
            Entry entry;

            lock (this._media)
            {
                // Un file cambiato apre una voce nuova: la vecchia resta ai lease che la tengono e sparisce con l'ultimo
                if (!this._media.TryGetValue(key, out entry) || !entry.Identity.Matches(identity))
                {
                    entry = new Entry(this, key, filePath, identity, requireNativeMpeg4, mkvMergePath);
                    this._media[key] = entry;
                }
                entry.References++;
            }

            return new NormalizedMediaLease(entry, filePath);
        }

        /// <summary>
        /// Prende i lease di Source e Lang di una coppia
        /// </summary>
        /// <param name="sourcePath">File Source</param>
        /// <param name="langPath">File Lang, vuoto per non prenderlo</param>
        /// <param name="requireNativeMpeg4">True per rimuxare anche gli MKV con MPEG-4 Part 2 in modalità Video for Windows</param>
        /// <param name="mkvMergePath">mkvmerge della normalizzazione, null per quello risolto dalla configurazione</param>
        /// <returns>Coppia da rilasciare con Dispose</returns>
        public NormalizedMediaPair AcquirePair(string sourcePath, string langPath, bool requireNativeMpeg4, string mkvMergePath = null)
        {
            return new NormalizedMediaPair(this.Acquire(sourcePath, requireNativeMpeg4, mkvMergePath), string.IsNullOrEmpty(langPath) ? null : this.Acquire(langPath, requireNativeMpeg4, mkvMergePath));
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
                if (this._media.TryGetValue(entry.Key, out Entry current) && ReferenceEquals(current, entry))
                    this._media.Remove(entry.Key);
            }

            entry.Discard();
        }

        #endregion

        #region Tipi annidati

        /// <summary>
        /// Temporaneo di un file con i riferimenti che lo tengono vivo
        /// </summary>
        internal sealed class Entry
        {
            #region Variabili di classe

            /// <summary>
            /// Serializza la normalizzazione: chi arriva durante quella in corso aspetta il suo esito
            /// </summary>
            private readonly object _normalization = new object();

            /// <summary>
            /// Protegge Matroska, scarto e contenuto verificato
            /// </summary>
            private readonly object _state = new object();

            /// <summary>
            /// File passato alla normalizzazione, come l'ha indicato chi ha creato la voce
            /// </summary>
            private readonly string _filePath;

            /// <summary>
            /// True per rimuxare anche gli MKV con MPEG-4 Part 2 in modalità Video for Windows
            /// </summary>
            private readonly bool _requireNativeMpeg4;

            /// <summary>
            /// mkvmerge della normalizzazione, null per quello risolto dalla configurazione
            /// </summary>
            private readonly string _mkvMergePath;

            /// <summary>
            /// Matroska normalizzato, null finché nessuno l'ha chiesto
            /// </summary>
            private NormalizedMediaFile _media;

            /// <summary>
            /// True dopo il rilascio dell'ultimo lease
            /// </summary>
            private bool _discarded;

            /// <summary>
            /// Contenuto letto da questo temporaneo, o già verificato contro di esso
            /// </summary>
            private object _verifiedContent;

            #endregion

            #region Costruttore

            /// <summary>
            /// Costruttore
            /// </summary>
            /// <param name="owner">Registro proprietario</param>
            /// <param name="key">Chiave nel registro</param>
            /// <param name="filePath">File da normalizzare</param>
            /// <param name="identity">Identità del file al momento della richiesta</param>
            /// <param name="requireNativeMpeg4">True per rimuxare anche gli MKV con MPEG-4 Part 2 in modalità Video for Windows</param>
            /// <param name="mkvMergePath">mkvmerge della normalizzazione, null per quello risolto dalla configurazione</param>
            public Entry(NormalizedMediaStore owner, string key, string filePath, MkvSplitSourceIdentity identity, bool requireNativeMpeg4, string mkvMergePath)
            {
                this.Owner = owner;
                this.Key = key;
                this.Identity = identity;
                this._filePath = filePath;
                this._requireNativeMpeg4 = requireNativeMpeg4;
                this._mkvMergePath = mkvMergePath;
            }

            #endregion

            #region Proprietà

            /// <summary>
            /// Registro proprietario
            /// </summary>
            public NormalizedMediaStore Owner { get; }

            /// <summary>
            /// Chiave nel registro
            /// </summary>
            public string Key { get; }

            /// <summary>
            /// Identità del file al momento della richiesta
            /// </summary>
            public MkvSplitSourceIdentity Identity { get; }

            /// <summary>
            /// Lease aperti sulla voce, protetti dal lock del registro
            /// </summary>
            public int References { get; set; }

            /// <summary>
            /// Contenuto letto da questo temporaneo, o già verificato contro di esso
            /// </summary>
            public object VerifiedContent
            {
                get { lock (this._state) { return this._verifiedContent; } }
                set { lock (this._state) { this._verifiedContent = value; } }
            }

            #endregion

            #region Metodi pubblici

            /// <summary>
            /// Matroska della voce, normalizzato alla prima richiesta. Un fallimento dovuto all'annullamento non resta nella voce:
            /// chi chiede dopo riprova
            /// </summary>
            /// <param name="cancellationToken">Annullamento della normalizzazione</param>
            /// <returns>File normalizzato, con l'errore come dato</returns>
            public NormalizedMediaFile Open(CancellationToken cancellationToken)
            {
                lock (this._normalization)
                {
                    if (this._media != null)
                        return this._media;
                    NormalizedMediaFile media = (this._mkvMergePath == null ? MediaContainerNormalizer.Create() : MediaContainerNormalizer.Create(this._mkvMergePath))
                        .Normalize(this._filePath, this._requireNativeMpeg4, cancellationToken);
                    if (!media.Success && cancellationToken.IsCancellationRequested)
                        return media;
                    bool discarded;
                    lock (this._state)
                    {
                        this._media = media;
                        discarded = this._discarded;
                    }
                    // L'ultimo lease è stato rilasciato durante la normalizzazione: il temporaneo appena scritto non deve restare
                    if (discarded)
                        media.Dispose();
                    return media;
                }
            }

            /// <summary>
            /// Cancella il temporaneo, subito se esiste o alla fine della normalizzazione in corso
            /// </summary>
            public void Discard()
            {
                NormalizedMediaFile media;
                lock (this._state)
                {
                    this._discarded = true;
                    media = this._media;
                }
                media?.Dispose();
            }

            #endregion
        }

        #endregion
    }

    /// <summary>
    /// Riferimento al Matroska normalizzato di un file: finché è aperto il temporaneo non viene cancellato
    /// </summary>
    public sealed class NormalizedMediaLease : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Voce condivisa
        /// </summary>
        private readonly NormalizedMediaStore.Entry _entry;

        /// <summary>
        /// File come l'ha indicato chi ha preso il lease: è il Matroska da leggere quando non serve un temporaneo
        /// </summary>
        private readonly string _filePath;

        /// <summary>
        /// Vero dopo il rilascio
        /// </summary>
        private int _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="entry">Voce condivisa</param>
        /// <param name="filePath">File come l'ha indicato chi ha preso il lease</param>
        internal NormalizedMediaLease(NormalizedMediaStore.Entry entry, string filePath)
        {
            this._entry = entry;
            this._filePath = filePath;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// File scelto dall'utente, come l'ha indicato chi ha preso il lease
        /// </summary>
        public string OriginalPath { get { return this._filePath; } }

        /// <summary>
        /// Matroska da leggere, normalizzato al primo accesso; un fallimento della normalizzazione diventa InvalidOperationException
        /// </summary>
        public string Path
        {
            get
            {
                NormalizedMediaFile media = this._entry.Open(CancellationToken.None);
                if (!media.Success)
                    throw new InvalidOperationException(media.Error);
                return media.IsTemporary ? media.Path : this._filePath;
            }
        }

        /// <summary>
        /// True se il Matroska è un temporaneo e non il file stesso
        /// </summary>
        public bool IsTemporary { get { return this._entry.Open(CancellationToken.None).IsTemporary; } }

        /// <summary>
        /// Contenuto letto da questo temporaneo, o già verificato contro di esso (per lo Split l'analisi); null finché nessuno l'ha fatto.
        /// Si confronta per riferimento
        /// </summary>
        public object VerifiedContent
        {
            get { return this._entry.VerifiedContent; }
            set { this._entry.VerifiedContent = value; }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Normalizza il file, se nessuno l'ha già fatto
        /// </summary>
        /// <param name="cancellationToken">Annullamento della normalizzazione</param>
        /// <returns>Motivo del fallimento, vuoto se il Matroska è disponibile</returns>
        public string Open(CancellationToken cancellationToken)
        {
            return this._entry.Open(cancellationToken).Error;
        }

        /// <summary>
        /// Rilascia il riferimento; l'ultimo cancella il temporaneo
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref this._disposed, 1) == 0)
                this._entry.Owner.Release(this._entry);
        }

        #endregion
    }

    /// <summary>
    /// Lease di Source e Lang presi insieme; il Lang si normalizza solo se il Source è riuscito
    /// </summary>
    public sealed class NormalizedMediaPair : IDisposable
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="source">Lease del Source, null se non richiesto</param>
        /// <param name="lang">Lease del Lang, null se non richiesto</param>
        public NormalizedMediaPair(NormalizedMediaLease source, NormalizedMediaLease lang)
        {
            this.Source = source;
            this.Lang = lang;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Lease del Source, null se non richiesto
        /// </summary>
        public NormalizedMediaLease Source { get; }

        /// <summary>
        /// Lease del Lang, null se non richiesto
        /// </summary>
        public NormalizedMediaLease Lang { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Normalizza il Source e poi, solo se è riuscito, il Lang
        /// </summary>
        /// <param name="cancellationToken">Annullamento della normalizzazione</param>
        /// <returns>Motivo del primo fallimento, prima del Source e poi del Lang; vuoto se tutto è riuscito</returns>
        public string Open(CancellationToken cancellationToken)
        {
            string error = this.Source != null ? this.Source.Open(cancellationToken) : "";
            return error.Length == 0 && this.Lang != null ? this.Lang.Open(cancellationToken) : error;
        }

        /// <summary>
        /// Rilascia i lease di Source e Lang
        /// </summary>
        public void Dispose()
        {
            this.Source?.Dispose();
            this.Lang?.Dispose();
        }

        #endregion
    }
}
