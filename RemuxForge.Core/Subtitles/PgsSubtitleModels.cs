namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Bitmap PGS decodificata in indici palette
    /// </summary>
    /// <param name="Width">Larghezza bitmap</param>
    /// <param name="Height">Altezza bitmap</param>
    /// <param name="Pixels">Indici palette in ordine row-major</param>
    internal sealed record PgsSubtitleBitmap(int Width, int Height, byte[] Pixels);

    /// <summary>
    /// Definizione completa di un oggetto PGS ODS
    /// </summary>
    /// <param name="ObjectId">Identificativo oggetto PGS</param>
    /// <param name="Version">Versione oggetto PGS</param>
    /// <param name="Width">Larghezza bitmap oggetto</param>
    /// <param name="Height">Altezza bitmap oggetto</param>
    /// <param name="RleData">Payload RLE completo dell'oggetto</param>
    /// <param name="FirstPacketHeader">Header SUP del primo pacchetto ODS</param>
    internal sealed record PgsObjectDefinition(
        int ObjectId,
        int Version,
        int Width,
        int Height,
        byte[] RleData,
        byte[] FirstPacketHeader);

    /// <summary>
    /// Dimensione oggetto PGS nota in un epoch
    /// </summary>
    /// <param name="Width">Larghezza oggetto</param>
    /// <param name="Height">Altezza oggetto</param>
    internal readonly record struct PgsObjectSize(int Width, int Height);
}
