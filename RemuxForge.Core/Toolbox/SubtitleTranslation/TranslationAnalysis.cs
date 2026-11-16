using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Stato dell'analisi del progetto
    /// </summary>
    public enum TranslationAnalysisStatus
    {
        /// <summary>Chiamate in corso</summary>
        Running,

        /// <summary>Fermata da Stop, limite d'uso, errore o chiusura dell'app: si riprende</summary>
        Stopped,

        /// <summary>Glossario proposto</summary>
        Completed
    }

    /// <summary>
    /// Motivo dell'ultima fermata di un lavoro con chiamate AI
    /// </summary>
    public enum TranslationStopReason
    {
        /// <summary>Stop dell'utente</summary>
        Stop,

        /// <summary>Limite d'uso del piano o credito esaurito</summary>
        UsageLimit,

        /// <summary>Accesso non configurato, scaduto o rifiutato</summary>
        AccessError,

        /// <summary>Errore del servizio o consegna non valida dopo i giri di correzione</summary>
        ServiceError,

        /// <summary>App chiusa con il lavoro in corso</summary>
        Crash
    }

    /// <summary>
    /// Parte del glossario a cui si riferisce una voce scartata o in conflitto
    /// </summary>
    public enum GlossaryEntryKind
    {
        /// <summary>Termine</summary>
        Term,

        /// <summary>Personaggio</summary>
        Character,

        /// <summary>Forma di cortesia</summary>
        Addressing,

        /// <summary>Testo della serie</summary>
        Series,

        /// <summary>Testo del tono</summary>
        Tone,

        /// <summary>Testo delle regole di registro</summary>
        RegisterRules
    }

    /// <summary>
    /// Esito dell'analisi: stato, fermata, chiamate fatte, glossari parziali già consegnati e proposte scartate
    /// </summary>
    public class TranslationAnalysis
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationAnalysis()
        {
            this.Status = TranslationAnalysisStatus.Running;
            this.Stop = null;
            this.WebSearchUsed = false;
            this.WebSearchRejected = false;
            this.CallSeqs = new List<int>();
            this.CompletedAt = null;
            this.DiscardedForImport = new List<GlossaryDiscard>();
            this.Partials = new List<GlossaryPartial>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Stato dell'analisi
        /// </summary>
        public TranslationAnalysisStatus Status { get; set; }

        /// <summary>
        /// Ultima fermata, null se l'analisi non si è fermata
        /// </summary>
        public TranslationStop Stop { get; set; }

        /// <summary>
        /// True se almeno una chiamata ha avuto la ricerca web
        /// </summary>
        public bool WebSearchUsed { get; set; }

        /// <summary>
        /// True se l'accesso ha rifiutato la ricerca web
        /// </summary>
        public bool WebSearchRejected { get; set; }

        /// <summary>
        /// Chiamate dell'analisi nel registro del progetto
        /// </summary>
        public List<int> CallSeqs { get; set; }

        /// <summary>
        /// Fine dell'analisi, null finché non è conclusa
        /// </summary>
        public DateTimeOffset? CompletedAt { get; set; }

        /// <summary>
        /// Proposte del modello scartate perché la voce era già nel glossario caricato
        /// </summary>
        public List<GlossaryDiscard> DiscardedForImport { get; set; }

        /// <summary>
        /// Glossari consegnati dalle chiamate di analisi già concluse, uno per file o uno solo per tutti i file: la ripresa rifà
        /// solo le chiamate mancanti
        /// </summary>
        public List<GlossaryPartial> Partials { get; set; }

        #endregion
    }

    /// <summary>
    /// Fermata di un lavoro con chiamate AI
    /// </summary>
    public class TranslationStop
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationStop()
        {
            this.Reason = TranslationStopReason.Stop;
            this.Message = "";
            this.At = DateTimeOffset.Now;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Fermata per l'eccezione che ha interrotto un lavoro: Stop se l'ha chiesto l'utente, limite d'uso o credito, errore
        /// dell'accesso, altrimenti errore del servizio. Il timeout del client non è uno Stop: lo è solo l'annullamento chiesto
        /// </summary>
        /// <param name="error">Eccezione del lavoro</param>
        /// <param name="stopped">True se l'utente ha chiesto lo Stop</param>
        /// <returns>Fermata col messaggio da mostrare</returns>
        public static TranslationStop Create(Exception error, bool stopped)
        {
            AiServiceException service = error as AiServiceException;
            TranslationStop stop = new TranslationStop();
            if (stopped)
            {
                stop.Reason = TranslationStopReason.Stop;
            }
            else if (service != null && service.Kind == AiErrorKind.UsageLimit)
            {
                stop.Reason = TranslationStopReason.UsageLimit;
                stop.Message = service.Message;
            }
            else if (service != null && (service.Kind == AiErrorKind.NotConfigured || service.Kind == AiErrorKind.SignInRequired || service.Kind == AiErrorKind.InvalidApiKey ||
                service.Kind == AiErrorKind.InvalidSignIn || service.Kind == AiErrorKind.NotEligible))
            {
                stop.Reason = TranslationStopReason.AccessError;
                stop.Message = service.Message;
            }
            else
            {
                stop.Reason = TranslationStopReason.ServiceError;
                stop.Message = error.Message;
            }

            return stop;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Motivo
        /// </summary>
        public TranslationStopReason Reason { get; set; }

        /// <summary>
        /// Messaggio dell'accesso o dell'errore, vuoto per Stop
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Momento della fermata
        /// </summary>
        public DateTimeOffset At { get; set; }

        #endregion
    }

    /// <summary>
    /// Proposta del modello scartata perché la voce o il testo erano già nel glossario caricato; la proposta intera torna nel
    /// glossario quando il caricato si toglie
    /// </summary>
    public class GlossaryDiscard
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public GlossaryDiscard()
        {
            this.Kind = GlossaryEntryKind.Term;
            this.Key = "";
            this.Delivered = "";
            this.Term = null;
            this.Character = null;
            this.Addressing = null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Parte del glossario
        /// </summary>
        public GlossaryEntryKind Kind { get; set; }

        /// <summary>
        /// Chiave della voce: originale, nome o chi parla → a chi
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// Valore proposto: resa, genere, forma o testo
        /// </summary>
        public string Delivered { get; set; }

        /// <summary>
        /// Termine proposto, per Kind Term
        /// </summary>
        public GlossaryTerm Term { get; set; }

        /// <summary>
        /// Personaggio proposto, per Kind Character
        /// </summary>
        public GlossaryCharacter Character { get; set; }

        /// <summary>
        /// Forma di cortesia proposta, per Kind Addressing
        /// </summary>
        public GlossaryAddressing Addressing { get; set; }

        #endregion
    }

    /// <summary>
    /// Glossario consegnato da una chiamata di analisi
    /// </summary>
    public class GlossaryPartial
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public GlossaryPartial()
        {
            this.FileId = TranslationSessionKey.NO_FILE;
            this.Glossary = new Glossary();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// File analizzato, TranslationSessionKey.NO_FILE per la chiamata unica su tutti i file
        /// </summary>
        public string FileId { get; set; }

        /// <summary>
        /// Glossario consegnato, nella forma del progetto
        /// </summary>
        public Glossary Glossary { get; set; }

        #endregion
    }
}
