using System;
using System.IO;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Container accettati in ingresso da Remux, Split e Unisci parti
    /// </summary>
    public static class MediaContainerFormats
    {
        #region Costanti

        /// <summary>
        /// Estensioni accettate, con il punto e in minuscolo
        /// </summary>
        public static readonly string[] Extensions = new string[] { ".mkv", ".mp4", ".m4v", ".avi", ".m2ts", ".mts", ".ts" };

        /// <summary>
        /// Estensione dell'output, unico container prodotto
        /// </summary>
        public const string OUTPUT_EXTENSION = ".mkv";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Indica se il file ha un'estensione accettata in ingresso
        /// </summary>
        /// <param name="filePath">Percorso o nome del file</param>
        /// <returns>True se l'estensione è nella lista</returns>
        public static bool IsSupported(string filePath)
        {
            string extension = Path.GetExtension(filePath ?? "");
            return Array.FindIndex(Extensions, e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase)) >= 0;
        }

        /// <summary>
        /// Indica se il file è già un Matroska
        /// </summary>
        /// <param name="filePath">Percorso o nome del file</param>
        /// <returns>True per l'estensione .mkv</returns>
        public static bool IsMatroska(string filePath)
        {
            return string.Equals(Path.GetExtension(filePath ?? ""), OUTPUT_EXTENSION, StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}
