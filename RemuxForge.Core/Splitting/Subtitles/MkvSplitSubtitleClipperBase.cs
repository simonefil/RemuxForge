using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Subtitles
{
    /// <summary>
    /// Clipper di un formato di sottotitoli con i suoi eventi tipizzati. Riconosce il codec dai formati dichiarati e interseca ogni
    /// evento con le clip dell'output; le sottoclassi leggono il file estratto e scrivono le occorrenze nel proprio formato
    /// </summary>
    /// <typeparam name="TEvent">Evento del formato</typeparam>
    internal abstract class MkvSplitSubtitleClipperBase<TEvent> : IMkvSplitSubtitleClipper where TEvent : MkvSplitSubtitleEvent
    {
        #region Proprietà

        /// <summary>
        /// Vero se il formato è bitmap
        /// </summary>
        public abstract bool IsBitmap { get; }

        /// <summary>
        /// Formati gestiti, con codec ed estensione del file estratto
        /// </summary>
        protected abstract MkvSplitSubtitleFormat[] Formats { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Indica se il clipper gestisce la traccia
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>True se il codec è di uno dei formati gestiti</returns>
        public bool CanHandle(TrackInfo track)
        {
            return this.Extension(track) != null;
        }

        /// <summary>
        /// Restituisce l'estensione del file estratto per il codec della traccia
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>Estensione con il punto, null se il codec non è di un formato gestito</returns>
        public string Extension(TrackInfo track)
        {
            return Array.Find(this.Formats, format => format.Matches(track))?.Extension;
        }

        /// <summary>
        /// Legge il file estratto e ne ricava gli eventi nel tempo del sorgente
        /// </summary>
        /// <param name="path">File estratto, con gli eventuali sidecar accanto</param>
        /// <param name="track">Traccia sorgente</param>
        /// <param name="videoEndSeconds">Fine del video, che chiude gli eventi senza fine nel formato</param>
        /// <returns>Traccia letta, legata a questo clipper</returns>
        public MkvSplitSubtitleTrack<TEvent> Read(string path, TrackInfo track, double videoEndSeconds)
        {
            MkvSplitSubtitleTrack<TEvent> result = new MkvSplitSubtitleTrack<TEvent>
            {
                Track = track,
                Clipper = this
            };
            this.ReadEvents(path, result, videoEndSeconds);
            return result;
        }

        /// <summary>
        /// Scrive la traccia ritagliata e riposizionata sulle clip di un output
        /// </summary>
        /// <param name="track">Traccia letta da questo clipper</param>
        /// <param name="output">Proiezione dell'output</param>
        /// <param name="path">File di destinazione</param>
        public abstract void Write(MkvSplitSubtitleTrack<TEvent> track, MkvSplitOutputProjection output, string path);

        /// <summary>
        /// Legge il file estratto come traccia generica
        /// </summary>
        /// <param name="path">File estratto</param>
        /// <param name="track">Traccia sorgente</param>
        /// <param name="videoEndSeconds">Fine del video</param>
        /// <returns>Traccia letta</returns>
        MkvSplitSubtitleTrack IMkvSplitSubtitleClipper.Read(string path, TrackInfo track, double videoEndSeconds)
        {
            return this.Read(path, track, videoEndSeconds);
        }

        /// <summary>
        /// Scrive una traccia generica: è sempre una traccia letta da questo clipper, con i suoi eventi
        /// </summary>
        /// <param name="track">Traccia letta da questo clipper</param>
        /// <param name="output">Proiezione dell'output</param>
        /// <param name="path">File di destinazione</param>
        void IMkvSplitSubtitleClipper.Write(MkvSplitSubtitleTrack track, MkvSplitOutputProjection output, string path)
        {
            this.Write((MkvSplitSubtitleTrack<TEvent>)track, output, path);
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Legge gli eventi e lo stato della traccia dal file estratto
        /// </summary>
        /// <param name="path">File estratto</param>
        /// <param name="result">Traccia da riempire, con la traccia sorgente già impostata</param>
        /// <param name="videoEndSeconds">Fine del video</param>
        protected abstract void ReadEvents(string path, MkvSplitSubtitleTrack<TEvent> result, double videoEndSeconds);

        /// <summary>
        /// Occorrenze degli eventi nelle clip dell'output, clip per clip nell'ordine dell'output e poi evento per evento: l'intersezione
        /// nel tempo del sorgente, non vuota, e lo spostamento che la porta nel tempo dell'output. Un evento con effetti temporizzati
        /// non si può tagliare a metà
        /// </summary>
        /// <param name="track">Traccia letta</param>
        /// <param name="output">Proiezione dell'output</param>
        /// <returns>Evento, inizio e fine dell'intersezione nel tempo del sorgente e spostamento verso il tempo dell'output</returns>
        protected static IEnumerable<(TEvent Event, double Start, double End, double Shift)> Clip(MkvSplitSubtitleTrack<TEvent> track, MkvSplitOutputProjection output)
        {
            foreach (MkvSplitClipProjection clip in output.Clips)
            {
                double shift = clip.ResultStartSeconds - clip.SourceStartSeconds;
                foreach (TEvent item in track.Events)
                {
                    double start = Math.Max(item.StartSeconds, clip.SourceStartSeconds);
                    double end = Math.Min(item.EndSeconds, clip.SourceEndSeconds);
                    if (end <= start)
                        continue;
                    if (item.HasTimedEffects && (start > item.StartSeconds || end < item.EndSeconds))
                        throw new InvalidOperationException(AppText.F("split.montage.subtitleTimedBoundary", track.Track.Id,
                            clip.SourceStartSeconds, clip.SourceEndSeconds));
                    yield return (item, start, end, shift);
                }
            }
        }

        #endregion
    }
}
