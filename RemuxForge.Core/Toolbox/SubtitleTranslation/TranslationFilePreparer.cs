using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Preparazione di un file del progetto: lettura con la codifica scelta, battute traducibili, gruppi delle frasi spezzate e
    /// impronte del sorgente; senza chiamate al modello
    /// </summary>
    public static class TranslationFilePreparer
    {
        #region Costanti

        /// <summary>
        /// Versione della preparazione, da aumentare quando cambiano id, gruppi, tipi o testo pulito
        /// </summary>
        public const int PREPARER_VERSION = 15;

        /// <summary>
        /// Rifiuto di un file che non c'è più
        /// </summary>
        public const string REJECTION_FILE_MISSING = "file-missing";

        /// <summary>
        /// Rifiuto di un file che non si legge
        /// </summary>
        public const string REJECTION_READ_FAILED = "read-failed";

        /// <summary>
        /// Rifiuto di un file con un'estensione che non è .srt, .ass o .ssa
        /// </summary>
        public const string REJECTION_FORMAT_UNKNOWN = "format-unknown";

        /// <summary>
        /// Rifiuto di un file bloccato dall'analisi i cui byte non sono più quelli salvati nel progetto
        /// </summary>
        public const string REJECTION_SOURCE_CHANGED = "source-changed";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Formato di un sottotitolo dall'estensione
        /// </summary>
        /// <param name="path">Percorso del file</param>
        /// <param name="format">Formato riconosciuto</param>
        /// <returns>True per .srt, .ass e .ssa; false per le altre estensioni, che il file rifiuta</returns>
        public static bool TryGetFormat(string path, out SubtitleTranslationFormat format)
        {
            string extension = Path.GetExtension(path ?? "").ToLowerInvariant();
            format = extension == ".ass" ? SubtitleTranslationFormat.Ass : extension == ".ssa" ? SubtitleTranslationFormat.Ssa : SubtitleTranslationFormat.Srt;
            return extension == ".srt" || extension == ".ass" || extension == ".ssa";
        }

        /// <summary>
        /// Prepara un file del progetto e ne aggiorna dimensione, impronte, preparazione e rifiuto. Un file bloccato dall'analisi
        /// tiene le impronte salvate: se i byte sono cambiati si rifiuta, altrimenti si rilegge senza toccarle
        /// </summary>
        /// <param name="file">File del progetto</param>
        /// <param name="sourceLanguage">Lingua d'origine, che decide i gruppi; vuota se non ancora scelta</param>
        /// <param name="styles">Tabella degli stili del progetto, null nei progetti salvati prima</param>
        /// <param name="stretches">Tratte SRT riconosciute del progetto, null prima del riconoscimento</param>
        /// <param name="locked">True con l'analisi partita</param>
        /// <returns>Documento letto, null se il file è rifiutato</returns>
        public static SubtitleTranslationDocument Prepare(TranslationProjectFile file, string sourceLanguage, IReadOnlyList<TranslationProjectStyle> styles,
            IReadOnlyList<TranslationProjectStretch> stretches, bool locked)
        {
            byte[] bytes;
            file.RejectedReason = "";
            if (!locked)
                file.Preparation = new TranslationFilePreparation();

            if (!TryGetFormat(file.Path, out _))
            {
                file.RejectedReason = REJECTION_FORMAT_UNKNOWN;
                return null;
            }

            if (!File.Exists(file.Path))
            {
                file.RejectedReason = REJECTION_FILE_MISSING;
                return null;
            }

            try
            {
                bytes = File.ReadAllBytes(file.Path);
            }
            catch (IOException)
            {
                file.RejectedReason = REJECTION_READ_FAILED;
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                file.RejectedReason = REJECTION_READ_FAILED;
                return null;
            }

            string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (locked && sha256 != file.SourceSha256)
            {
                file.RejectedReason = REJECTION_SOURCE_CHANGED;
                return null;
            }

            SubtitleTranslationDocument document = SubtitleTranslationDocument.Read(bytes, file.Format, file.Encoding, styles, stretches);
            if (!string.IsNullOrEmpty(document.RejectionReason))
            {
                file.RejectedReason = document.RejectionReason;
                return null;
            }

            // I gruppi stanno nelle battute del documento: si rifanno anche sul file bloccato, che tiene la preparazione salvata
            int groups = new SplitSentenceGrouper().Group(document.Cues, sourceLanguage ?? "");
            if (locked)
                return document;

            file.SizeBytes = bytes.Length;
            file.SourceSha256 = sha256;

            TranslationFilePreparation preparation = new TranslationFilePreparation();
            preparation.GroupCount = groups;
            foreach (SubtitleCue cue in document.Cues)
            {
                if (cue.Id.HasValue)
                {
                    preparation.CueCount++;
                }
                else
                {
                    preparation.ExcludedCount++;
                }
            }

            preparation.NumberedSourceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(TranslationRequestBuilder.FormatSource(document.Cues)))).ToLowerInvariant();
            preparation.PreparerVersion = PREPARER_VERSION;
            preparation.PreparedAt = DateTimeOffset.Now;
            file.Preparation = preparation;
            return document;
        }

        #endregion
    }
}
