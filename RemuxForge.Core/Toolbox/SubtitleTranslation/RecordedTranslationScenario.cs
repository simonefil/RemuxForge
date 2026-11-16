using System.Collections.Generic;
using System.Text.Json.Nodes;

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
        WrongCues,

        /// <summary>Consegna valida che segnala un termine nuovo</summary>
        NewTerm
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
            this.Term = "";
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

        /// <summary>
        /// Termine nuovo segnalato con NewTerm, reso col prefisso sintetico
        /// </summary>
        public string Term { get; set; }

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
            this.Deliveries = new List<RecordedTranslationDelivery>();
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

        /// <summary>
        /// Consegne al posto di quella sintetica, per tipo di chiamata, file e numero di chiamata
        /// </summary>
        public List<RecordedTranslationDelivery> Deliveries { get; set; }

        #endregion
    }

    /// <summary>
    /// Consegna di una chiamata dello scenario
    /// </summary>
    public class RecordedTranslationDelivery
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public RecordedTranslationDelivery()
        {
            this.CallKind = TranslationCallKind.A1;
            this.FileId = "";
            this.Occurrence = 1;
            this.Arguments = null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Tipo di chiamata
        /// </summary>
        public TranslationCallKind CallKind { get; set; }

        /// <summary>
        /// File della chiamata, vuoto per tutti; TranslationSessionKey.NO_FILE per le chiamate su più file
        /// </summary>
        public string FileId { get; set; }

        /// <summary>
        /// Numero della chiamata di quel tipo su quel file, da 1; un giro di correzione è una chiamata nuova
        /// </summary>
        public int Occurrence { get; set; }

        /// <summary>
        /// Argomenti del tool, null per una risposta di solo testo
        /// </summary>
        public JsonObject Arguments { get; set; }

        #endregion
    }
}
