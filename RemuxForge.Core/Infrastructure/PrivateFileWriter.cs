using System;
using System.IO;
using System.Text;

namespace RemuxForge.Core.Infrastructure
{
    /// <summary>
    /// Scrittura atomica dei file di configurazione, leggibili solo dal proprietario
    /// </summary>
    public static class PrivateFileWriter
    {
        #region Metodi pubblici

        /// <summary>
        /// Scrive il testo in un file temporaneo leggibile solo dal proprietario e lo sposta sul file finale
        /// </summary>
        /// <param name="filePath">Percorso del file</param>
        /// <param name="content">Contenuto UTF-8 senza BOM</param>
        public static void WriteAtomic(string filePath, string content)
        {
            string tempFilePath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                string folder = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);

                FileStreamOptions streamOptions = new FileStreamOptions();
                streamOptions.Mode = FileMode.CreateNew;
                streamOptions.Access = FileAccess.Write;
                streamOptions.Share = FileShare.None;

                // Il file può contenere token e chiavi: su macOS e Linux nasce leggibile solo dal proprietario
                if (!OperatingSystem.IsWindows())
                    streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                using (FileStream stream = new FileStream(tempFilePath, streamOptions))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(content);
                    writer.Flush();
                    stream.Flush(true);
                }

                File.Move(tempFilePath, filePath, true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempFilePath))
                        File.Delete(tempFilePath);
                }
                catch (IOException)
                {
                    // La scrittura ha già propagato l'errore principale; la pulizia resta best-effort
                }
                catch (UnauthorizedAccessException)
                {
                    // La scrittura ha già propagato l'errore principale; la pulizia resta best-effort
                }
            }
        }

        #endregion
    }
}
