using RemuxForge.Core.Configuration;
using RemuxForge.Core.Toolbox.Common;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Export dei sottotitoli tradotti: il documento del sorgente con i soli campi testo sostituiti, accanto al sorgente o nella
    /// cartella scelta, col nome del sorgente e la lingua d'arrivo ISO 639-2; un nome occupato prende il suffisso, mai sovrascritto
    /// </summary>
    public static class TranslationExporter
    {
        #region Metodi pubblici

        /// <summary>
        /// Percorso voluto dell'uscita, prima delle collisioni: «nome.lingua.ext»
        /// </summary>
        /// <param name="sourcePath">Sottotitolo sorgente</param>
        /// <param name="targetLanguage">Lingua d'arrivo</param>
        /// <param name="outputFolder">Cartella scelta, null per l'uscita accanto al sorgente</param>
        /// <returns>Percorso voluto</returns>
        public static string GetOutputPath(string sourcePath, string targetLanguage, string outputFolder)
        {
            string folder = string.IsNullOrEmpty(outputFolder) ? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) : outputFolder;
            return Path.Combine(folder, Path.GetFileNameWithoutExtension(sourcePath) + "." + LanguageValidator.NormalizeToIso6392(targetLanguage) + Path.GetExtension(sourcePath));
        }

        /// <summary>
        /// Scrive un file tradotto: le battute senza testo tradotto escono col testo originale. Un errore lascia il nome libero
        /// </summary>
        /// <param name="file">File del job col documento del sorgente</param>
        /// <param name="desiredPath">Percorso voluto</param>
        /// <param name="allocator">Percorsi del lotto, sorgenti compresi</param>
        /// <returns>Percorso scritto</returns>
        public static string Export(TranslationRunFile file, string desiredPath, OutputPathAllocator allocator)
        {
            Dictionary<int, string> texts = new Dictionary<int, string>();
            foreach (TranslatedCueState translation in file.State.Translations)
            {
                if (translation.Text != null)
                    texts[translation.Id] = translation.Text;
            }

            string path = allocator.Reserve(desiredPath);
            try
            {
                using (StagedOutputFile staged = new StagedOutputFile(path))
                {
                    File.WriteAllBytes(staged.TempPath, file.Document.Write(texts));
                    staged.Commit();
                }
            }
            catch
            {
                allocator.Release(path);
                throw;
            }

            return path;
        }

        #endregion
    }
}
