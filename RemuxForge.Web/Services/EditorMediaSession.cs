using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Media di una sessione editor: per ogni lato il lease del Matroska da leggere al posto del file scelto dall'utente (D18).
    /// Il lease non normalizza da sé: lo fa Open all'apertura dell'editor, o la prima lettura con EnsureAvailable. Dispose
    /// rilascia tutti i lati, e l'ultimo lease di un file cancella il suo temporaneo
    /// </summary>
    public sealed class EditorMediaSession : IDisposable
    {
        #region Variabili di classe

        /// <summary>
        /// Lease dei Matroska della sessione
        /// </summary>
        private readonly NormalizedMediaPair _media;

        /// <summary>
        /// Lato servito dal lease Source
        /// </summary>
        private readonly string _sourceSide;

        /// <summary>
        /// Lato servito dal lease Lang, null senza Lang
        /// </summary>
        private readonly string _langSide;

        /// <summary>
        /// Lock del rilascio
        /// </summary>
        private readonly object _lock = new object();

        /// <summary>
        /// True dopo il rilascio
        /// </summary>
        private bool _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="media">Lease dei Matroska della sessione</param>
        /// <param name="sourceSide">Lato servito dal lease Source</param>
        /// <param name="langSide">Lato servito dal lease Lang, null senza Lang</param>
        public EditorMediaSession(NormalizedMediaPair media, string sourceSide, string langSide)
        {
            this._media = media;
            this._sourceSide = sourceSide;
            this._langSide = langSide;
        }

        #endregion

        #region Metodi pubblici statici

        /// <summary>
        /// File da leggere senza sessione: senza sessione non si normalizza, quindi un file non MKV non si legge e l'editor
        /// riporta l'errore invece di aprire l'originale (D18)
        /// </summary>
        /// <param name="filePath">File scelto dall'utente</param>
        /// <returns>Il file stesso, se è un Matroska</returns>
        public static string RequireMatroska(string filePath)
        {
            if (!MediaContainerFormats.IsMatroska(filePath))
                throw new SessionMediaMissingException(AppText.F("web.editMap.sessionMediaMissing", Path.GetFileName(filePath)));
            return filePath;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Indica se la sessione espone il lato
        /// </summary>
        /// <param name="side">Nome del lato</param>
        /// <returns>True se il lato ha un lease</returns>
        public bool HasSide(string side)
        {
            return this.FindLease(side) != null;
        }

        /// <summary>
        /// File scelto dall'utente per il lato
        /// </summary>
        /// <param name="side">Nome del lato</param>
        /// <returns>Percorso originale, null se il lato non esiste</returns>
        public string GetOriginalPath(string side)
        {
            return this.FindLease(side)?.OriginalPath;
        }

        /// <summary>
        /// Normalizza il Source e poi, solo se è riuscito, il Lang
        /// </summary>
        /// <param name="cancellationToken">Annullamento dell'apertura</param>
        /// <returns>Motivo del fallimento, vuoto se tutti i Matroska sono disponibili</returns>
        public string Open(CancellationToken cancellationToken)
        {
            return this._media.Open(cancellationToken);
        }

        /// <summary>
        /// Matroska da leggere per il lato, normalizzato alla prima richiesta. Unico controllo del temporaneo della sessione:
        /// sparito dalla cartella temp a sessione aperta, sessione chiusa o normalizzazione fallita danno SessionMediaMissingException
        /// </summary>
        /// <param name="side">Nome del lato</param>
        /// <returns>Percorso del Matroska</returns>
        public string EnsureAvailable(string side)
        {
            NormalizedMediaLease lease = this.FindLease(side);
            bool disposed;
            lock (this._lock)
            {
                disposed = this._disposed;
            }
            string missing = AppText.F("web.editMap.sessionMediaMissing", Path.GetFileName(lease.OriginalPath));
            if (disposed)
                throw new SessionMediaMissingException(missing);
            string error = lease.Open(CancellationToken.None);
            if (error.Length > 0)
                throw new SessionMediaMissingException(error);
            // Il temporaneo sparito dalla cartella temp a sessione aperta non si rifà qui: l'editor va chiuso e riaperto
            if (lease.IsTemporary && !File.Exists(lease.Path))
                throw new SessionMediaMissingException(missing);
            return lease.Path;
        }

        /// <summary>
        /// Rilascia i lease di tutti i lati
        /// </summary>
        public void Dispose()
        {
            lock (this._lock)
            {
                if (this._disposed)
                    return;
                this._disposed = true;
            }
            this._media.Dispose();
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Lease del lato
        /// </summary>
        /// <param name="side">Nome del lato</param>
        /// <returns>Lease, null se il lato non esiste</returns>
        private NormalizedMediaLease FindLease(string side)
        {
            if (string.Equals(side, this._sourceSide, StringComparison.OrdinalIgnoreCase))
                return this._media.Source;
            return this._langSide != null && string.Equals(side, this._langSide, StringComparison.OrdinalIgnoreCase) ? this._media.Lang : null;
        }

        #endregion
    }

    /// <summary>
    /// Risolutore degli endpoint multimediali vincolato a una sessione editor: il file viene dalla sessione, le tracce audio
    /// dal chiamante
    /// </summary>
    public sealed class EditorMediaSessionResolver : IMediaSourceResolver
    {
        #region Variabili di classe

        /// <summary>
        /// Sessione dell'editor
        /// </summary>
        private readonly EditorMediaSession _session;

        /// <summary>
        /// Tracce audio selezionabili per indice di record e lato
        /// </summary>
        private readonly Func<int, string, List<TrackInfo>> _audioTracks;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="session">Sessione dell'editor</param>
        /// <param name="audioTracks">Tracce audio selezionabili per indice di record e lato</param>
        public EditorMediaSessionResolver(EditorMediaSession session, Func<int, string, List<TrackInfo>> audioTracks)
        {
            this._session = session;
            this._audioTracks = audioTracks;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Indica se la sessione espone il lato
        /// </summary>
        /// <param name="side">Nome del lato</param>
        /// <returns>True se il lato appartiene alla sessione</returns>
        public bool SupportsSide(string side)
        {
            return this._session.HasSide(side);
        }

        /// <summary>
        /// Risolve il Matroska della sessione per il lato
        /// </summary>
        /// <param name="recordIndex">Indice del record, passato alle tracce audio</param>
        /// <param name="side">Nome del lato</param>
        /// <returns>Sorgente della sessione, null se il lato non è esposto</returns>
        public MediaSource ResolveMediaSource(int recordIndex, string side)
        {
            return this.SupportsSide(side) ? new MediaSource(this._session.EnsureAvailable(side), this._audioTracks(recordIndex, side)) : null;
        }

        #endregion
    }
}
