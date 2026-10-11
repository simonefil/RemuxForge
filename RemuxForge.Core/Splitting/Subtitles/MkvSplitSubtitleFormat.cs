using RemuxForge.Core.Models;
using System;

namespace RemuxForge.Core.Splitting.Subtitles
{
    /// <summary>
    /// Formato di sottotitoli riconosciuto dallo Split: i nomi mkvmerge si confrontano senza maiuscole, i CodecID Matroska in modo esatto
    /// </summary>
    internal sealed class MkvSplitSubtitleFormat
    {
        #region Variabili di classe

        /// <summary>
        /// Nomi del codec come li riporta mkvmerge
        /// </summary>
        private readonly string[] _names;

        /// <summary>
        /// CodecID Matroska
        /// </summary>
        private readonly string[] _codecIds;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="extension">Estensione del file estratto, con il punto</param>
        /// <param name="names">Nomi del codec come li riporta mkvmerge</param>
        /// <param name="codecIds">CodecID Matroska</param>
        public MkvSplitSubtitleFormat(string extension, string[] names, string[] codecIds)
        {
            this.Extension = extension;
            this._names = names;
            this._codecIds = codecIds;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Estensione del file estratto, con il punto
        /// </summary>
        public string Extension { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Vero se il codec della traccia è di questo formato
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>True se nome o CodecID corrispondono</returns>
        public bool Matches(TrackInfo track)
        {
            string codec = track.Codec ?? "";
            return Array.Exists(this._names, name => codec.Equals(name, StringComparison.OrdinalIgnoreCase)) || Array.IndexOf(this._codecIds, codec) >= 0;
        }

        #endregion
    }
}
