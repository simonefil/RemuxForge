using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// Codec video dello Split: nome, metodo del taglio al fotogramma e correzioni della lettura dei keyframe. Le istanze sono
    /// immutabili e le tiene il registro MkvSplitCodecCapabilities
    /// </summary>
    public abstract class MkvSplitVideoCodecBase
    {
        #region Proprietà

        /// <summary>
        /// Nome del codec come lo riporta ffprobe
        /// </summary>
        public string CodecName { get; init; }

        /// <summary>
        /// Nome mostrato all'utente
        /// </summary>
        public string DisplayName { get; init; }

        /// <summary>
        /// Metodo del taglio al fotogramma, None se il codec si taglia solo al keyframe
        /// </summary>
        public abstract MkvSplitExactCutMethod Method { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Corregge i flag keyframe letti da ffprobe, in ordine di decodifica, prima che si calcolino GOP aperti e B iniziali.
        /// Per default il flag del pacchetto è quello del fotogramma e non serve correggerlo
        /// </summary>
        /// <param name="inputFile">File MKV letto</param>
        /// <param name="frames">Pacchetti in ordine di decodifica</param>
        /// <param name="pts">PTS dei pacchetti in ordine di decodifica</param>
        public virtual void CorrectKeyFlags(string inputFile, List<MkvSplitFrameInfo> frames, List<double> pts)
        {
        }

        /// <summary>
        /// Marca i keyframe di GOP aperto i cui B iniziali si decodificano anche senza il GOP precedente. Per default nessuno:
        /// i B iniziali restano dipendenti dal GOP precedente
        /// </summary>
        /// <param name="inputFile">File MKV letto</param>
        /// <param name="frames">Fotogrammi in ordine di presentazione, con GOP aperti e B iniziali già rilevati</param>
        public virtual void MarkDecodableLeadingFrames(string inputFile, List<MkvSplitFrameInfo> frames)
        {
        }

        #endregion
    }
}
