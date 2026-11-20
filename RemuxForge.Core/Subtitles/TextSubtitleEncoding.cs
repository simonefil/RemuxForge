using System;
using System.Text;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Codifica di un sottotitolo di testo riconoscibile dai suoi byte
    /// </summary>
    public enum TextSubtitleEncodingKind
    {
        /// <summary>
        /// BOM UTF-8 o UTF-16: la codifica la dichiara il file
        /// </summary>
        Bom,

        /// <summary>
        /// Senza BOM e UTF-8 valido
        /// </summary>
        Utf8,

        /// <summary>
        /// Senza BOM con byte nulli: UTF-16 che nessuna codifica a un byte legge
        /// </summary>
        Utf16WithoutBom,

        /// <summary>
        /// Senza BOM e non UTF-8: la codifica va scelta
        /// </summary>
        NotUtf8
    }

    /// <summary>
    /// Riconosce la codifica di un sottotitolo di testo: BOM, poi UTF-16 senza BOM, poi UTF-8 rigoroso
    /// </summary>
    public static class TextSubtitleEncoding
    {
        #region Metodi pubblici

        /// <summary>
        /// Riconosce la codifica dei byte di un sottotitolo di testo
        /// </summary>
        /// <param name="data">Contenuto del file</param>
        /// <returns>Codifica riconosciuta</returns>
        public static TextSubtitleEncodingResult Detect(byte[] data)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return new TextSubtitleEncodingResult(TextSubtitleEncodingKind.Bom, new UTF8Encoding(false), 3);

            if (data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)))
                return new TextSubtitleEncodingResult(TextSubtitleEncodingKind.Bom, new UnicodeEncoding(data[0] == 0xFE, false), 2);

            if (Array.IndexOf(data, (byte)0) >= 0)
                return new TextSubtitleEncodingResult(TextSubtitleEncodingKind.Utf16WithoutBom, null, 0);

            try
            {
                new UTF8Encoding(false, true).GetString(data);
            }
            catch (DecoderFallbackException)
            {
                return new TextSubtitleEncodingResult(TextSubtitleEncodingKind.NotUtf8, null, 0);
            }

            return new TextSubtitleEncodingResult(TextSubtitleEncodingKind.Utf8, new UTF8Encoding(false), 0);
        }

        #endregion
    }

    /// <summary>
    /// Codifica riconosciuta di un sottotitolo di testo
    /// </summary>
    /// <param name="Kind">Esito del riconoscimento</param>
    /// <param name="Encoding">Codifica del BOM o UTF-8 senza BOM, null se il file non la determina</param>
    /// <param name="BomLength">Byte del BOM in testa al file, zero senza BOM</param>
    public sealed record TextSubtitleEncodingResult(TextSubtitleEncodingKind Kind, Encoding Encoding, int BomLength);
}
