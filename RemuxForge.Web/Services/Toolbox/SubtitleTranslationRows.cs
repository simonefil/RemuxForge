using RemuxForge.Core.Toolbox.SubtitleTranslation;
using System.Collections.Generic;

namespace RemuxForge.Web.Services.Toolbox
{
    /// <summary>
    /// Riga della griglia del passo Traduzione: un file del job
    /// </summary>
    public sealed class TranslationJobFileRow
    {
        #region Proprietà

        /// <summary>
        /// File del job
        /// </summary>
        public string FileId { get; set; }

        /// <summary>
        /// Nome del file sorgente
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// Stato della traduzione del file
        /// </summary>
        public TranslationFileStatus Status { get; set; }

        /// <summary>
        /// Codice del motivo del blocco
        /// </summary>
        public string BlockedReason { get; set; }

        /// <summary>
        /// Blocchi salvati
        /// </summary>
        public int BlocksDone { get; set; }

        /// <summary>
        /// Blocchi del piano, dimezzati esclusi
        /// </summary>
        public int BlocksTotal { get; set; }

        /// <summary>
        /// Battute accettate
        /// </summary>
        public int CuesDone { get; set; }

        /// <summary>
        /// Battute da tradurre
        /// </summary>
        public int CuesTotal { get; set; }

        /// <summary>
        /// Battute respinte e poi accettate o da rivedere
        /// </summary>
        public int Retried { get; set; }

        /// <summary>
        /// Battute da rivedere
        /// </summary>
        public int ToReview { get; set; }

        /// <summary>
        /// Token in ingresso reali, null se il registro non li ha
        /// </summary>
        public long? InputTokens { get; set; }

        /// <summary>
        /// Token in cache reali, null se il registro non li ha
        /// </summary>
        public long? CachedTokens { get; set; }

        /// <summary>
        /// Token in uscita reali, null se il registro non li ha
        /// </summary>
        public long? OutputTokens { get; set; }

        #endregion
    }

    /// <summary>
    /// Riga della griglia della revisione: una battuta da tradurre con lo stato della traduzione
    /// </summary>
    public sealed class TranslationReviewRow
    {
        #region Proprietà

        /// <summary>
        /// Id della battuta
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Inizio in millisecondi
        /// </summary>
        public long StartMs { get; set; }

        /// <summary>
        /// Fine in millisecondi
        /// </summary>
        public long EndMs { get; set; }

        /// <summary>
        /// Testo pulito del sorgente
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// Traduzione, null senza testo tradotto
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// True per una battuta da rivedere, senza traduzione o non rivista dalla revisione AI
        /// </summary>
        public bool ToReview { get; set; }

        /// <summary>
        /// Motivo dello stato da rivedere per l'utente, revisione AI non riuscita compresa; null fuori da quello stato o senza
        /// esito della traduzione
        /// </summary>
        public string ToReviewReason { get; set; }

        /// <summary>
        /// Segnali della battuta
        /// </summary>
        public List<TranslationCueSignal> Signals { get; set; }

        /// <summary>
        /// Testo prima della correzione della revisione AI, null se nessuno
        /// </summary>
        public string CorrectionBefore { get; set; }

        /// <summary>
        /// Motivo della correzione della revisione AI, null se nessuno
        /// </summary>
        public string CorrectionReason { get; set; }

        /// <summary>
        /// Numero della tratta ripetuta fra i file a cui la battuta appartiene, null fuori dalle tratte: la traduzione è la stessa in
        /// tutte le occorrenze e una modifica le cambia tutte
        /// </summary>
        public int? SharedStretch { get; set; }

        #endregion
    }

    /// <summary>
    /// Riga della griglia del passo Esporta: un file del job con l'uscita
    /// </summary>
    public sealed class TranslationExportRow
    {
        #region Proprietà

        /// <summary>
        /// File del job
        /// </summary>
        public string FileId { get; set; }

        /// <summary>
        /// Nome del file sorgente
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// Percorso voluto prima dell'export, percorso scritto dopo
        /// </summary>
        public string OutputPath { get; set; }

        /// <summary>
        /// True se il file è scritto
        /// </summary>
        public bool Written { get; set; }

        /// <summary>
        /// Battute col testo tradotto
        /// </summary>
        public int Translated { get; set; }

        /// <summary>
        /// Battute senza testo tradotto, che escono col testo originale
        /// </summary>
        public int Untranslated { get; set; }

        /// <summary>
        /// Motivo per cui il file non si scrive, vuoto se nessuno
        /// </summary>
        public string Error { get; set; }

        #endregion
    }
}
