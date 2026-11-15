using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Esito di una chiamata nel registro
    /// </summary>
    public enum TranslationCallOutcome
    {
        /// <summary>Consegna valida</summary>
        Accepted,

        /// <summary>Consegna con voci o battute respinte dalla validazione: tornano al modello con gli errori</summary>
        PartiallyRejected,

        /// <summary>Risposta troncata (incomplete)</summary>
        Reduced,

        /// <summary>Errore del trasporto o del servizio</summary>
        Error
    }

    /// <summary>
    /// Riga del registro delle chiamate (calls.jsonl): una per tentativo di trasporto arrivato al servizio
    /// </summary>
    public class TranslationCallRecord
    {
        #region Costanti

        /// <summary>
        /// Versione del formato della riga
        /// </summary>
        public const int SCHEMA_VERSION = 1;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationCallRecord()
        {
            this.SchemaVersion = SCHEMA_VERSION;
            this.CallSeq = 0;
            this.Kind = TranslationCallKind.A1;
            this.FileId = null;
            this.RequestedIds = null;
            this.Round = null;
            this.TransportAttempt = 1;
            this.Model = "";
            this.ReasoningEffort = null;
            this.SessionId = null;
            this.StartedAt = DateTimeOffset.Now;
            this.DurationMs = 0;
            this.HttpStatus = 0;
            this.ResponseStatus = TranslationCallResponseStatus.Completed;
            this.IncompleteReason = null;
            this.ErrorKind = null;
            this.ServiceCode = null;
            this.Outcome = TranslationCallOutcome.Accepted;
            this.Rejections = new List<TranslationCallRejection>();
            this.Usage = null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Versione del formato
        /// </summary>
        public int SchemaVersion { get; set; }

        /// <summary>
        /// Progressivo nel registro, da 1
        /// </summary>
        public int CallSeq { get; set; }

        /// <summary>
        /// Tipo di chiamata
        /// </summary>
        public TranslationCallKind Kind { get; set; }

        /// <summary>
        /// File, null per le chiamate su più file
        /// </summary>
        public string FileId { get; set; }

        /// <summary>
        /// Numero di id richiesti: le battute mandate al modello
        /// </summary>
        public int? RequestedIds { get; set; }

        /// <summary>
        /// Giro di correzione di A1 e A2, da 1
        /// </summary>
        public int? Round { get; set; }

        /// <summary>
        /// Tentativo del retry di trasporto, da 1
        /// </summary>
        public int TransportAttempt { get; set; }

        /// <summary>
        /// Modello
        /// </summary>
        public string Model { get; set; }

        /// <summary>
        /// Livello di ragionamento mandato, null se tolto dopo un rifiuto
        /// </summary>
        public string ReasoningEffort { get; set; }

        /// <summary>
        /// Valore di session_id mandato
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// Ora d'invio
        /// </summary>
        public DateTimeOffset StartedAt { get; set; }

        /// <summary>
        /// Durata fino all'evento finale o all'errore
        /// </summary>
        public long DurationMs { get; set; }

        /// <summary>
        /// Stato HTTP; 200 anche per gli errori nello stream, 0 se non noto
        /// </summary>
        public int HttpStatus { get; set; }

        /// <summary>
        /// Stato della risposta
        /// </summary>
        public TranslationCallResponseStatus ResponseStatus { get; set; }

        /// <summary>
        /// Motivo della risposta troncata
        /// </summary>
        public string IncompleteReason { get; set; }

        /// <summary>
        /// Tipo d'errore, null senza errore
        /// </summary>
        public AiErrorKind? ErrorKind { get; set; }

        /// <summary>
        /// Codice d'errore del servizio
        /// </summary>
        public string ServiceCode { get; set; }

        /// <summary>
        /// Esito della chiamata
        /// </summary>
        public TranslationCallOutcome Outcome { get; set; }

        /// <summary>
        /// Motivi dei rifiuti della validazione, la riga espandibile del pannello Chiamate
        /// </summary>
        public List<TranslationCallRejection> Rejections { get; set; }

        /// <summary>
        /// Consumo, null se la chiamata non ha avuto risposta
        /// </summary>
        public TranslationCallUsage Usage { get; set; }

        #endregion
    }

    /// <summary>
    /// Stato della risposta di una chiamata
    /// </summary>
    public enum TranslationCallResponseStatus
    {
        /// <summary>Risposta completa</summary>
        Completed,

        /// <summary>Risposta troncata</summary>
        Incomplete,

        /// <summary>Errore</summary>
        Error
    }

    /// <summary>
    /// Rifiuto della validazione: una battuta con i suoi motivi, oppure la consegna intera senza id
    /// </summary>
    public class TranslationCallRejection
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationCallRejection()
        {
            this.Id = null;
            this.Reasons = new List<string>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Battuta respinta, null per la consegna intera
        /// </summary>
        public int? Id { get; set; }

        /// <summary>
        /// Righe del validatore
        /// </summary>
        public List<string> Reasons { get; set; }

        #endregion
    }

    /// <summary>
    /// Consumo di una chiamata
    /// </summary>
    public class TranslationCallUsage
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationCallUsage()
        {
            this.InputTokens = null;
            this.CachedTokens = null;
            this.CacheWriteTokens = null;
            this.OutputTokens = null;
            this.ReasoningTokens = null;
            this.WebSearchRequests = null;
            this.WebSearchCalls = null;
        }

        /// <summary>
        /// Costruttore dal consumo letto nella risposta
        /// </summary>
        /// <param name="usage">Consumo della risposta</param>
        /// <param name="webSearchCalls">Item web_search_call ricevuti</param>
        public TranslationCallUsage(AiUsage usage, int webSearchCalls)
        {
            this.InputTokens = usage.InputTokens;
            this.CachedTokens = usage.CachedTokens;
            this.CacheWriteTokens = usage.CacheWriteTokens;
            this.OutputTokens = usage.OutputTokens;
            this.ReasoningTokens = usage.ReasoningTokens;
            this.WebSearchRequests = usage.WebSearchRequests;
            this.WebSearchCalls = webSearchCalls;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Token in ingresso, cache compresa
        /// </summary>
        public long? InputTokens { get; set; }

        /// <summary>
        /// Token in ingresso letti dalla cache
        /// </summary>
        public long? CachedTokens { get; set; }

        /// <summary>
        /// Token scritti in cache
        /// </summary>
        public long? CacheWriteTokens { get; set; }

        /// <summary>
        /// Token in uscita, ragionamento compreso
        /// </summary>
        public long? OutputTokens { get; set; }

        /// <summary>
        /// Token di ragionamento
        /// </summary>
        public long? ReasoningTokens { get; set; }

        /// <summary>
        /// Ricerche web contate dal servizio
        /// </summary>
        public long? WebSearchRequests { get; set; }

        /// <summary>
        /// Item web_search_call ricevuti
        /// </summary>
        public int? WebSearchCalls { get; set; }

        #endregion
    }
}
