using System;
using System.Collections.Generic;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Esito di apertura o applicazione dell'editor Split
    /// </summary>
    public enum SplitApplyStatus
    {
        /// <summary>
        /// Operazione riuscita
        /// </summary>
        Applied,

        /// <summary>
        /// Documento o richiesta non validi
        /// </summary>
        Invalid,

        /// <summary>
        /// Revisioni non più allineate o sessione inesistente
        /// </summary>
        Conflict,

        /// <summary>
        /// Orchestrator occupato
        /// </summary>
        Busy,

        /// <summary>
        /// Operazione annullata
        /// </summary>
        Cancelled
    }

    /// <summary>
    /// Copia indipendente per l'editor; le revisioni applicato/opzioni sono possedute dall'orchestrator.
    /// </summary>
    public class SplitEditorSnapshot
    {
        #region Proprietà

        /// <summary>
        /// Sessione dell'editor
        /// </summary>
        public Guid SessionId { get; set; }

        /// <summary>
        /// Revisione applicata attesa all'apertura
        /// </summary>
        public long ExpectedAppliedRevision { get; set; }

        /// <summary>
        /// Revisione delle opzioni attesa all'apertura
        /// </summary>
        public long ExpectedOptionsRevision { get; set; }

        /// <summary>
        /// Documento da modificare
        /// </summary>
        public MkvSplitDocument Document { get; set; }

        /// <summary>
        /// Analisi del sorgente
        /// </summary>
        public MkvSplitAnalysis Analysis { get; set; }

        /// <summary>
        /// Informazioni del file sorgente
        /// </summary>
        public MkvFileInfo SourceInfo { get; set; }

        /// <summary>
        /// Opzioni Split
        /// </summary>
        public MkvSplitOptions Options { get; set; }

        #endregion
    }

    /// <summary>
    /// Esito dell'apertura dell'editor
    /// </summary>
    public class SplitEditorOpenResult
    {
        #region Proprietà

        /// <summary>
        /// Copia per l'editor, valorizzata se l'apertura è riuscita
        /// </summary>
        public SplitEditorSnapshot Snapshot { get; set; }

        /// <summary>
        /// Diagnostiche dell'apertura
        /// </summary>
        public List<MkvSplitDiagnostic> Diagnostics { get; set; } = new List<MkvSplitDiagnostic>();

        #endregion
    }

    /// <summary>
    /// Richiesta di applicazione del documento modificato
    /// </summary>
    public class SplitApplyRequest
    {
        #region Proprietà

        /// <summary>
        /// Sessione dell'editor
        /// </summary>
        public Guid SessionId { get; set; }

        /// <summary>
        /// Revisione applicata attesa
        /// </summary>
        public long ExpectedAppliedRevision { get; set; }

        /// <summary>
        /// Revisione delle opzioni attesa
        /// </summary>
        public long ExpectedOptionsRevision { get; set; }

        /// <summary>
        /// Revisione del draft, non inferiore all'ultima ricevuta dalla sessione
        /// </summary>
        public long DraftRevision { get; set; }

        /// <summary>
        /// Documento da applicare
        /// </summary>
        public MkvSplitDocument Document { get; set; }

        #endregion
    }

    /// <summary>
    /// Esito dell'applicazione del documento
    /// </summary>
    public class SplitApplyResult
    {
        #region Proprietà

        /// <summary>
        /// Stato dell'applicazione
        /// </summary>
        public SplitApplyStatus Status { get; set; }

        /// <summary>
        /// Diagnostiche dell'applicazione
        /// </summary>
        public List<MkvSplitDiagnostic> Diagnostics { get; set; } = new List<MkvSplitDiagnostic>();

        /// <summary>
        /// Revisione applicata corrente
        /// </summary>
        public long AppliedRevision { get; set; }

        /// <summary>
        /// Revisione delle opzioni corrente
        /// </summary>
        public long OptionsRevision { get; set; }

        #endregion
    }

    /// <summary>
    /// Esito del calcolo del documento della regola per l'editor
    /// </summary>
    public class SplitRuleDocumentResult
    {
        #region Proprietà

        /// <summary>
        /// Documento della regola, valorizzato se il calcolo è riuscito
        /// </summary>
        public MkvSplitDocument Document { get; set; }

        /// <summary>
        /// Diagnostiche del calcolo
        /// </summary>
        public List<MkvSplitDiagnostic> Diagnostics { get; set; } = new List<MkvSplitDiagnostic>();

        #endregion
    }

    /// <summary>
    /// Esito di un'unione di parti
    /// </summary>
    public class SplitJoinResult
    {
        #region Proprietà

        /// <summary>
        /// Vero se il file unito è stato pubblicato
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Vero se l'unione è stata interrotta dall'utente
        /// </summary>
        public bool Stopped { get; set; }

        /// <summary>
        /// Percorso del file unito
        /// </summary>
        public string OutputPath { get; set; } = "";

        /// <summary>
        /// Messaggio localizzato di errore, vuoto in caso di successo
        /// </summary>
        public string Message { get; set; } = "";

        #endregion
    }
}
