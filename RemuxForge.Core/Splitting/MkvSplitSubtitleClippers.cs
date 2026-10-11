using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting.Subtitles;
using System;
using System.IO;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Registro dei clipper dei sottotitoli dello Split: unica sede del riconoscimento dei codec ritagliabili. Un codec che nessun
    /// clipper gestisce lo taglia mkvmerge al pacchetto insieme alle clip
    /// </summary>
    internal static class MkvSplitSubtitleClippers
    {
        #region Variabili di classe

        /// <summary>
        /// Clipper disponibili, senza stato
        /// </summary>
        private static readonly IMkvSplitSubtitleClipper[] s_clippers = new IMkvSplitSubtitleClipper[]
        {
            new MkvSplitTextSubtitleClipper(),
            new MkvSplitPgsSubtitleClipper(),
            new MkvSplitVobSubSubtitleClipper()
        };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Risolve il clipper compatibile con la traccia
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>Clipper compatibile o null</returns>
        public static IMkvSplitSubtitleClipper Resolve(TrackInfo track)
        {
            return Array.Find(s_clippers, clipper => clipper.CanHandle(track));
        }

        /// <summary>
        /// Vero se la traccia di sottotitoli si può ritagliare
        /// </summary>
        /// <param name="track">Traccia da verificare</param>
        /// <returns>Vero se un clipper gestisce il codec</returns>
        public static bool IsSupported(TrackInfo track)
        {
            return Resolve(track) != null;
        }

        /// <summary>
        /// Estrae la traccia dal sorgente e ne legge gli eventi nel tempo del sorgente
        /// </summary>
        /// <param name="source">File sorgente</param>
        /// <param name="track">Traccia da estrarre</param>
        /// <param name="directory">Cartella temporanea per l'estrazione</param>
        /// <param name="videoEndSeconds">Fine del video, usata per chiudere l'ultimo display-set PGS e l'unico SPU VobSub senza stop</param>
        /// <returns>Traccia letta, legata al suo clipper</returns>
        public static MkvSplitSubtitleTrack ReadSource(string source, TrackInfo track, string directory, double videoEndSeconds)
        {
            IMkvSplitSubtitleClipper clipper = Resolve(track);
            if (clipper == null)
                throw new InvalidOperationException(AppText.F("split.montage.unsupportedSubtitle", track.Id, track.Codec));
            string path = Path.Combine(directory, "source-sub-" + track.Id + clipper.Extension(track));
            MkvSplitExternalTools.Instance.ExtractRawTrack(source, track.Id, path);
            return clipper.Read(path, track, videoEndSeconds);
        }

        #endregion
    }
}
