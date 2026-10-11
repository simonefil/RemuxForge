using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System.IO;

namespace RemuxForge.Core.Splitting.Subtitles
{
    /// <summary>
    /// Clipper di un formato bitmap: gli eventi si ricompongono dai pacchetti del formato senza decodificare la grafica, e un pacchetto
    /// che non si sa ricomporre interrompe la lettura con l'errore del formato
    /// </summary>
    /// <typeparam name="TEvent">Evento del formato</typeparam>
    internal abstract class MkvSplitBitmapSubtitleClipperBase<TEvent> : MkvSplitSubtitleClipperBase<TEvent> where TEvent : MkvSplitSubtitleEvent
    {
        #region Proprietà

        /// <summary>
        /// Formato bitmap
        /// </summary>
        public override bool IsBitmap => true;

        /// <summary>
        /// Chiave del messaggio di errore della traccia, con l'id della traccia e il dettaglio
        /// </summary>
        protected abstract string TrackErrorKey { get; }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Interrompe la lettura con InvalidDataException se la condizione non è soddisfatta
        /// </summary>
        /// <param name="valid">Condizione richiesta</param>
        /// <param name="track">Traccia in lettura</param>
        /// <param name="detail">Dettaglio dell'errore</param>
        protected void Require(bool valid, TrackInfo track, string detail)
        {
            if (!valid)
                throw new InvalidDataException(AppText.F(this.TrackErrorKey, track.Id, detail));
        }

        #endregion
    }
}
