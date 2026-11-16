using RemuxForge.Core.Configuration;
using RemuxForge.Core.Toolbox.Common;
using System.Collections.Generic;
using System.IO;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Export dei sottotitoli tradotti: il documento del sorgente con i soli campi testo sostituiti, accanto al sorgente o nella
    /// cartella scelta, col nome del sorgente e la lingua d'arrivo ISO 639-2 al posto di quella in coda; un nome occupato prende il suffisso, mai sovrascritto
    /// </summary>
    public static class TranslationExporter
    {
        #region Metodi pubblici

        /// <summary>
        /// Nome dell'uscita: «nome.lingua.ext», con la lingua d'arrivo al posto del codice di lingua in coda al nome del sorgente
        /// </summary>
        /// <param name="sourcePath">Sottotitolo sorgente</param>
        /// <param name="targetLanguage">Lingua d'arrivo</param>
        /// <returns>Nome del file tradotto</returns>
        public static string GetOutputName(string sourcePath, string targetLanguage)
        {
            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            int dot = stem.LastIndexOf('.');
            if (dot > 0 && LanguageValidator.IsValid(stem.Substring(dot + 1)))
                stem = stem.Substring(0, dot);

            return stem + "." + LanguageValidator.NormalizeToIso6392(targetLanguage) + Path.GetExtension(sourcePath);
        }

        /// <summary>
        /// Percorso voluto dell'uscita, prima delle collisioni
        /// </summary>
        /// <param name="sourcePath">Sottotitolo sorgente</param>
        /// <param name="targetLanguage">Lingua d'arrivo</param>
        /// <param name="outputFolder">Cartella scelta, null per l'uscita accanto al sorgente</param>
        /// <returns>Percorso voluto</returns>
        public static string GetOutputPath(string sourcePath, string targetLanguage, string outputFolder)
        {
            string folder = string.IsNullOrEmpty(outputFolder) ? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) : outputFolder;
            return Path.Combine(folder, GetOutputName(sourcePath, targetLanguage));
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
