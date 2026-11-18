using RemuxForge.Core.Configuration;
using RemuxForge.Core.Toolbox.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Export dei sottotitoli tradotti: il documento del sorgente con i soli campi testo sostituiti, accanto al sorgente o nella
    /// cartella scelta, col nome del sorgente e la lingua d'arrivo ISO 639-2 al posto di quella del sorgente in coda. Il file
    /// scritto dall'ultimo export del job, se nessuno l'ha cambiato, si sovrascrive; un altro file col nome voluto non si tocca e
    /// il nuovo prende il suffisso prima della lingua
    /// </summary>
    public static class TranslationExporter
    {
        #region Metodi pubblici

        /// <summary>
        /// Nome dell'uscita: «nome.lingua.ext». Il codice in coda al nome del sorgente si toglie solo se è la lingua del sorgente,
        /// in qualunque forma: una parola del titolo che sembra un codice (Dr.No, Part.II) resta nel nome
        /// </summary>
        /// <param name="sourcePath">Sottotitolo sorgente</param>
        /// <param name="sourceLanguage">Lingua del sorgente</param>
        /// <param name="targetLanguage">Lingua d'arrivo</param>
        /// <returns>Nome del file tradotto</returns>
        public static string GetOutputName(string sourcePath, string sourceLanguage, string targetLanguage)
        {
            string stem = Path.GetFileNameWithoutExtension(sourcePath);
            string source = LanguageValidator.NormalizeToIso6392(sourceLanguage);
            int dot = stem.LastIndexOf('.');
            if (dot > 0 && source.Length > 0 && LanguageValidator.NormalizeToIso6392(stem.Substring(dot + 1)) == source)
                stem = stem.Substring(0, dot);

            return stem + "." + LanguageValidator.NormalizeToIso6392(targetLanguage) + Path.GetExtension(sourcePath);
        }

        /// <summary>
        /// Percorso voluto dell'uscita, prima delle collisioni
        /// </summary>
        /// <param name="sourcePath">Sottotitolo sorgente</param>
        /// <param name="sourceLanguage">Lingua del sorgente</param>
        /// <param name="targetLanguage">Lingua d'arrivo</param>
        /// <param name="outputFolder">Cartella scelta, null per l'uscita accanto al sorgente</param>
        /// <returns>Percorso voluto</returns>
        public static string GetOutputPath(string sourcePath, string sourceLanguage, string targetLanguage, string outputFolder)
        {
            string folder = string.IsNullOrEmpty(outputFolder) ? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) : outputFolder;
            return Path.Combine(folder, GetOutputName(sourcePath, sourceLanguage, targetLanguage));
        }

        /// <summary>
        /// Scrive un file tradotto: le battute senza testo tradotto escono col testo originale. Il file dell'ultimo export, ancora
        /// con il contenuto scritto allora, si sovrascrive; percorso e hash del file scritto restano nello stato del job, che il
        /// chiamante salva. Un errore lascia il nome libero e lo stato com'era
        /// </summary>
        /// <param name="file">File del job col documento del sorgente</param>
        /// <param name="desiredPath">Percorso voluto</param>
        /// <param name="allocator">Percorsi del lotto, sorgenti compresi</param>
        /// <param name="warnings">Riceve i backup del file sovrascritto che non si sono potuti cancellare</param>
        /// <returns>Percorso scritto</returns>
        public static string Export(TranslationRunFile file, string desiredPath, OutputPathAllocator allocator, List<string> warnings)
        {
            Dictionary<int, string> texts = new Dictionary<int, string>();
            foreach (TranslatedCueState translation in file.State.Translations)
            {
                if (translation.Text != null)
                    texts[translation.Id] = translation.Text;
            }

            // Il file dell'export precedente è ancora del job solo se il contenuto è quello scritto allora: modificato a mano non lo è più
            string previous = file.State.ExportedPath;
            string replaceable = previous.Length > 0 && File.Exists(previous) &&
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(previous))).ToLowerInvariant() == file.State.ExportedSha256 ? previous : null;
            byte[] content = file.Document.Write(texts);
            string path = allocator.Reserve(desiredPath, true, replaceable);
            try
            {
                using (StagedOutputFile staged = new StagedOutputFile(path))
                {
                    File.WriteAllBytes(staged.TempPath, content);
                    if (File.Exists(path))
                        warnings.AddRange(staged.CommitReplacing(path));
                    else
                        staged.Commit();
                }
            }
            catch
            {
                allocator.Release(path);
                throw;
            }

            file.State.ExportedPath = path;
            file.State.ExportedSha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            return path;
        }

        #endregion
    }
}
