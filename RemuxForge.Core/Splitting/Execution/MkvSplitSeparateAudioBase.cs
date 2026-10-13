using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Execution
{
    /// <summary>
    /// Tracce audio della clip copiata che mkvmerge non taglia insieme al video: si tagliano a parte sullo stesso intervallo e si
    /// riuniscono al video, tagliato da mkvmerge senza di loro
    /// </summary>
    public abstract class MkvSplitSeparateAudioBase
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="tracks">Tracce audio tagliate a parte, nell'ordine del sorgente</param>
        protected MkvSplitSeparateAudioBase(List<TrackInfo> tracks)
        {
            this.Tracks = tracks;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Tracce audio tagliate a parte, nell'ordine del sorgente
        /// </summary>
        public List<TrackInfo> Tracks { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Audio da tagliare a parte nella clip copiata di un segmento. Con il FLAC tutto l'audio, perché mkvmerge non taglia il FLAC
        /// insieme al video. Altrimenti le tracce Opus di una clip che non parte dall'inizio del file: mkvmerge apre la parte al keyframe
        /// e scarta i primi 80 ms di Opus (il pre-roll, fisso, non quello del file), spostando di altrettanto inizio e fine dell'audio
        /// rispetto al video; dall'inizio del file non scarta nulla
        /// </summary>
        /// <param name="seg">Segmento della clip</param>
        /// <param name="tracks">Tracce del file normalizzato</param>
        /// <returns>Audio da tagliare a parte, null se tutte le tracce si tagliano con il video</returns>
        public static MkvSplitSeparateAudioBase For(MkvSplitSegment seg, List<TrackInfo> tracks)
        {
            if (tracks.Exists(MkvSplitExternalTools.IsFlacAudio))
                return new MkvSplitFlacSeparateAudio(tracks.FindAll(track => track.Type == "audio"));
            if (seg.StartFrame > 0 && tracks.Exists(MkvSplitExternalTools.IsOpusAudio))
                return new MkvSplitOpusSeparateAudio(tracks.FindAll(MkvSplitExternalTools.IsOpusAudio));
            return null;
        }

        /// <summary>
        /// Taglia le tracce sull'intervallo della clip, con il primo campione all'inizio dell'intervallo
        /// </summary>
        /// <param name="inputFile">File normalizzato</param>
        /// <param name="startTc">Inizio dell'intervallo</param>
        /// <param name="endTc">Fine dell'intervallo</param>
        /// <param name="audioFile">File scritto, con le sole tracce tagliate nell'ordine del sorgente</param>
        /// <exception cref="System.InvalidOperationException">Taglio non riuscito</exception>
        public abstract void Cut(string inputFile, string startTc, string endTc, string audioFile);

        #endregion
    }
}
