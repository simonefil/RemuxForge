using RemuxForge.Core.Models;

namespace RemuxForge.Core.Splitting.Subtitles
{
    /// <summary>
    /// Contratto comune per il ritaglio temporale di un formato di sottotitoli sulle clip di un output dello Split
    /// </summary>
    internal interface IMkvSplitSubtitleClipper
    {
        /// <summary>
        /// Vero se il formato è bitmap: la traccia non si copia nativa quando gli eventi stanno dentro il video
        /// </summary>
        bool IsBitmap { get; }

        /// <summary>
        /// Indica se il clipper gestisce la traccia
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>True se il codec è supportato</returns>
        bool CanHandle(TrackInfo track);

        /// <summary>
        /// Restituisce l'estensione del file estratto per il codec della traccia
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>Estensione con il punto, null se il codec non è supportato</returns>
        string Extension(TrackInfo track);

        /// <summary>
        /// Legge il file estratto e ne ricava gli eventi nel tempo del sorgente
        /// </summary>
        /// <param name="path">File estratto, con gli eventuali sidecar accanto</param>
        /// <param name="track">Traccia sorgente</param>
        /// <param name="videoEndSeconds">Fine del video, che chiude gli eventi senza fine nel formato</param>
        /// <returns>Traccia letta</returns>
        MkvSplitSubtitleTrack Read(string path, TrackInfo track, double videoEndSeconds);

        /// <summary>
        /// Scrive la traccia ritagliata e riposizionata sulle clip di un output
        /// </summary>
        /// <param name="track">Traccia letta da questo clipper</param>
        /// <param name="output">Proiezione dell'output</param>
        /// <param name="path">File di destinazione</param>
        void Write(MkvSplitSubtitleTrack track, MkvSplitOutputProjection output, string path);
    }
}
