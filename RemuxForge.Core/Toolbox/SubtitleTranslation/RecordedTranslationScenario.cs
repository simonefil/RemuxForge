using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Guasto simulato dal provider registrato
    /// </summary>
    public enum RecordedTranslationFaultKind
    {
        /// <summary>Risposta troncata (response.incomplete) senza item</summary>
        Incomplete,

        /// <summary>429 di frequenza al primo tentativo, poi la risposta</summary>
        RateLimit,

        /// <summary>Limite del piano o credito esaurito a ogni tentativo</summary>
        UsageLimit,

        /// <summary>Parametro facoltativo rifiutato finché la richiesta lo contiene</summary>
        UnsupportedCapability,

        /// <summary>Consegna con la prima battuta vuota e l'ultima mancante</summary>
        WrongCues
    }

    /// <summary>
    /// Guasto simulato su una chiamata precisa: l'n-esima chiamata di un tipo su un file
    /// </summary>
    public class RecordedTranslationFault
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public RecordedTranslationFault()
        {
            this.Kind = RecordedTranslationFaultKind.Incomplete;
            this.CallKind = null;
            this.FileId = "";
            this.Occurrence = 1;
            this.RetryAfterSeconds = 0;
            this.Parameter = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Guasto da simulare
        /// </summary>
        public RecordedTranslationFaultKind Kind { get; set; }

        /// <summary>
        /// Tipo di chiamata colpito, null per tutti
        /// </summary>
        public TranslationCallKind? CallKind { get; set; }

        /// <summary>
        /// File colpito, vuoto per tutti
        /// </summary>
        public string FileId { get; set; }

        /// <summary>
        /// Chiamata colpita fra quelle dello stesso tipo sullo stesso file, da 1
        /// </summary>
        public int Occurrence { get; set; }

        /// <summary>
        /// Retry-After del 429 simulato in secondi, zero per il backoff
        /// </summary>
        public int RetryAfterSeconds { get; set; }

        /// <summary>
        /// Parametro rifiutato (error.param), vuoto per lasciarlo dedurre al provider
        /// </summary>
        public string Parameter { get; set; }

        #endregion
    }

    /// <summary>
    /// Scenario del provider registrato: latenza e guasti simulati
    /// </summary>
    public class RecordedTranslationScenario
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public RecordedTranslationScenario()
        {
            this.LatencyMilliseconds = 0;
            this.Faults = new List<RecordedTranslationFault>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Attesa simulata di ogni risposta, in millisecondi
        /// </summary>
        public int LatencyMilliseconds { get; set; }

        /// <summary>
        /// Guasti simulati
        /// </summary>
        public List<RecordedTranslationFault> Faults { get; set; }

        #endregion
    }
}
