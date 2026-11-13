using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.IO;

namespace RemuxForge.Core.Infrastructure
{
    /// <summary>
    /// Metodi utility per operazioni su file
    /// </summary>
    public static class FileHelper
    {
        #region Metodi pubblici

        /// <summary>
        /// Elimina un file temporaneo in modo sicuro, loggando eventuali errori
        /// </summary>
        /// <param name="filePath">Percorso del file da eliminare</param>
        public static void DeleteTempFile(string filePath)
        {
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                try
                {
                    File.Delete(filePath);
                }
                catch (Exception ex)
                {
                    ConsoleHelper.Write(LogSection.General, LogLevel.Warning, AppText.F("remux.process.tempFileDeleteFailed", filePath, ex.Message));
                }
            }
        }

        /// <summary>
        /// Elimina un file sottotitolo temporaneo e gli eventuali sidecar muxabili (.sub di un .idx)
        /// </summary>
        /// <param name="filePath">Percorso del file sottotitolo principale</param>
        public static void DeleteTempSubtitleFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            DeleteTempFile(filePath);
            DeleteTempFile(GetCompanionPath(filePath));
        }

        /// <summary>
        /// File che accompagna sempre quello indicato con lo stesso nome (il .sub di un .idx VobSub)
        /// </summary>
        /// <param name="filePath">Percorso del file principale</param>
        /// <returns>Percorso del file accoppiato, vuoto se il formato non ne ha</returns>
        public static string GetCompanionPath(string filePath)
        {
            return string.Equals(Path.GetExtension(filePath), ".idx", StringComparison.OrdinalIgnoreCase) ? Path.ChangeExtension(filePath, ".sub") : "";
        }

        /// <summary>
        /// Elimina una directory temporanea in modo sicuro, loggando eventuali errori
        /// </summary>
        /// <param name="directoryPath">Percorso della directory da eliminare</param>
        public static void DeleteTempDirectory(string directoryPath)
        {
            if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath))
            {
                try
                {
                    Directory.Delete(directoryPath, true);
                }
                catch (Exception ex)
                {
                    ConsoleHelper.Write(LogSection.General, LogLevel.Warning, AppText.F("remux.process.tempDirDeleteFailed", directoryPath, ex.Message));
                }
            }
        }

        #endregion
    }
}
