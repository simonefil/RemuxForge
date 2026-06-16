using RemuxForge.Core.Models;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Contratto comune per rewriter canvas sottotitoli
    /// </summary>
    internal interface ISubtitleCanvasRewriter
    {
        /// <summary>
        /// Indica se il rewriter gestisce la traccia
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>True se il codec è supportato</returns>
        bool CanHandle(TrackInfo track);

        /// <summary>
        /// Restituisce l'estensione del file principale gestito dal rewriter
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>Estensione con punto</returns>
        string GetPrimaryExtension(TrackInfo track);

        /// <summary>
        /// Riscrive il file sottotitoli estratto
        /// </summary>
        /// <param name="context">Contesto riscrittura</param>
        /// <param name="track">Traccia sottotitoli</param>
        /// <param name="inputFile">File input principale</param>
        /// <param name="outputFile">File output principale</param>
        /// <param name="result">Risultato riscrittura</param>
        /// <returns>True se riscrittura riuscita</returns>
        bool Rewrite(SubtitleCanvasRewriteContext context, TrackInfo track, string inputFile, string outputFile, out SubtitleCanvasRewriteResult result);

        /// <summary>
        /// Valida il file output principale e gli eventuali sidecar
        /// </summary>
        /// <param name="context">Contesto riscrittura</param>
        /// <param name="outputFile">File output principale</param>
        /// <returns>True se output valido</returns>
        bool ValidateOutput(SubtitleCanvasRewriteContext context, string outputFile);
    }
}
