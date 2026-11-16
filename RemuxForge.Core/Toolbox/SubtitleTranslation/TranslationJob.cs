using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Fase del giro di un job
    /// </summary>
    public enum TranslationJobPhase
    {
        /// <summary>Traduzione a blocchi</summary>
        Translation,

        /// <summary>Termini nuovi da decidere</summary>
        NewTerms,

        /// <summary>Applicazione dei termini nuovi</summary>
        Applying,

        /// <summary>Revisione AI</summary>
        Review,

        /// <summary>Revisione dell'utente</summary>
        UserReview,

        /// <summary>Pronto per l'export</summary>
        Export,

        /// <summary>Giro chiuso dall'export</summary>
        Done
    }

    /// <summary>
    /// Stato del job
    /// </summary>
    public enum TranslationJobStatus
    {
        /// <summary>Il processo lavora</summary>
        Running,

        /// <summary>La fase aspetta un comando dell'utente</summary>
        Idle,

        /// <summary>Lavoro interrotto, col motivo nella pausa</summary>
        Paused,

        /// <summary>Giro chiuso dall'export</summary>
        Completed
    }

    /// <summary>
    /// Stato della traduzione di un file del job
    /// </summary>
    public enum TranslationFileStatus
    {
        /// <summary>Nessun blocco ancora salvato</summary>
        Pending,

        /// <summary>Almeno un blocco salvato, altri da fare</summary>
        InProgress,

        /// <summary>Ultimo blocco salvato</summary>
        Completed,

        /// <summary>Sorgente mancante o cambiato</summary>
        Blocked
    }

    /// <summary>
    /// Stato di un blocco del piano
    /// </summary>
    public enum TranslationBlockStatus
    {
        /// <summary>Da tradurre</summary>
        Pending,

        /// <summary>Tradotto e salvato</summary>
        Done,

        /// <summary>Sostituito dalle sue metà dopo una risposta troncata</summary>
        Split
    }

    /// <summary>
    /// Stato di una battuta con almeno un esito
    /// </summary>
    public enum TranslatedCueStatus
    {
        /// <summary>Traduzione accettata</summary>
        Accepted,

        /// <summary>Nessuna traduzione accettata dopo i ritentativi</summary>
        ToReview
    }

    /// <summary>
    /// Avviso cumulativo di una battuta
    /// </summary>
    public enum TranslationCueSignal
    {
        /// <summary>Respinta almeno una volta</summary>
        Retried,

        /// <summary>Resa del glossario assente dalla traduzione</summary>
        Glossary,

        /// <summary>Il sorgente contiene un termine nuovo segnalato dal modello</summary>
        NewTerm
    }

    /// <summary>
    /// Giro dalla traduzione all'export su tutti i file di un progetto: testata salvata in translation-jobs/&lt;id&gt;/job.json
    /// </summary>
    public class TranslationJob
    {
        #region Costanti

        /// <summary>
        /// Versione del formato di job.json
        /// </summary>
        public const int SCHEMA_VERSION = 1;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationJob()
        {
            this.SchemaVersion = SCHEMA_VERSION;
            this.Id = "";
            this.ProjectId = "";
            this.CreatedAt = DateTimeOffset.Now;
            this.UpdatedAt = this.CreatedAt;
            this.AccessMode = AiConnectionMode.ChatGptAccount;
            this.Models = new TranslationPhaseModels();
            this.ReasoningEfforts = new TranslationPhaseModels();
            this.FileIds = new List<string>();
            this.Glossary = new Glossary();
            this.GlossaryRevisionAtStart = 0;
            this.Phase = TranslationJobPhase.Translation;
            this.Status = TranslationJobStatus.Running;
            this.Pause = null;
            this.DisabledCapabilities = new List<string>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Versione del formato
        /// </summary>
        public int SchemaVersion { get; set; }

        /// <summary>
        /// Identificativo, uguale al nome della cartella
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Progetto di origine
        /// </summary>
        public string ProjectId { get; set; }

        /// <summary>
        /// Creazione
        /// </summary>
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>
        /// Ultimo salvataggio
        /// </summary>
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>
        /// Accesso dell'ultimo avvio o ripresa
        /// </summary>
        public AiConnectionMode AccessMode { get; set; }

        /// <summary>
        /// Modelli del progetto all'avvio, con la revisione già risolta
        /// </summary>
        public TranslationPhaseModels Models { get; set; }

        /// <summary>
        /// Livelli di ragionamento del progetto all'avvio, con la revisione già risolta
        /// </summary>
        public TranslationPhaseModels ReasoningEfforts { get; set; }

        /// <summary>
        /// File del job nell'ordine del progetto
        /// </summary>
        public List<string> FileIds { get; set; }

        /// <summary>
        /// Copia congelata del glossario approvato
        /// </summary>
        public Glossary Glossary { get; set; }

        /// <summary>
        /// Revisione del glossario all'avvio
        /// </summary>
        public int GlossaryRevisionAtStart { get; set; }

        /// <summary>
        /// Fase del giro
        /// </summary>
        public TranslationJobPhase Phase { get; set; }

        /// <summary>
        /// Stato
        /// </summary>
        public TranslationJobStatus Status { get; set; }

        /// <summary>
        /// Ultima pausa; null mentre il job lavora o aspetta l'utente
        /// </summary>
        public TranslationStop Pause { get; set; }

        /// <summary>
        /// Capacità spente nel job dopo un rifiuto dell'accesso
        /// </summary>
        public List<string> DisabledCapabilities { get; set; }

        #endregion
    }

    /// <summary>
    /// Stato di un file del job: translation-jobs/&lt;id&gt;/files/&lt;fileId&gt;.json
    /// </summary>
    public class TranslationJobFileState
    {
        #region Costanti

        /// <summary>
        /// Versione del formato del file di stato
        /// </summary>
        public const int SCHEMA_VERSION = 1;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationJobFileState()
        {
            this.SchemaVersion = SCHEMA_VERSION;
            this.FileId = "";
            this.SourceFileName = "";
            this.SourceSha256 = "";
            this.NumberedSourceSha256 = "";
            this.PreparerVersion = 0;
            this.Encoding = SubtitleTranslationDocument.DEFAULT_ENCODING;
            this.Cues = new List<FrozenCue>();
            this.Blocks = new List<TranslationBlock>();
            this.TargetBlockSize = SubtitleBlockPlanner.DEFAULT_TARGET;
            this.BlockCap = SubtitleBlockPlanner.DEFAULT_CAP;
            this.TranslationStatus = TranslationFileStatus.Pending;
            this.BlockedReason = "";
            this.Translations = new List<TranslatedCueState>();
            this.CollectedNewTerms = new List<CollectedNewTerm>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Versione del formato
        /// </summary>
        public int SchemaVersion { get; set; }

        /// <summary>
        /// File del progetto
        /// </summary>
        public string FileId { get; set; }

        /// <summary>
        /// Nome del file sorgente congelato col sorgente numerato: il prefisso non cambia se il file prende un altro nome
        /// </summary>
        public string SourceFileName { get; set; }

        /// <summary>
        /// SHA-256 del sorgente su cui il job lavora
        /// </summary>
        public string SourceSha256 { get; set; }

        /// <summary>
        /// SHA-256 del sorgente numerato
        /// </summary>
        public string NumberedSourceSha256 { get; set; }

        /// <summary>
        /// Versione della preparazione
        /// </summary>
        public int PreparerVersion { get; set; }

        /// <summary>
        /// Codifica usata in lettura
        /// </summary>
        public string Encoding { get; set; }

        /// <summary>
        /// Sorgente numerato congelato: il prefisso resta uguale byte per byte in tutta la vita del job
        /// </summary>
        public List<FrozenCue> Cues { get; set; }

        /// <summary>
        /// Piano dei blocchi nell'ordine di traduzione, metà dei blocchi dimezzati comprese
        /// </summary>
        public List<TranslationBlock> Blocks { get; set; }

        /// <summary>
        /// Obiettivo di dimensione del blocco in vigore
        /// </summary>
        public int TargetBlockSize { get; set; }

        /// <summary>
        /// Tetto del blocco in vigore
        /// </summary>
        public int BlockCap { get; set; }

        /// <summary>
        /// Stato della traduzione del file
        /// </summary>
        public TranslationFileStatus TranslationStatus { get; set; }

        /// <summary>
        /// Codice del motivo del blocco, vuoto se il file non è bloccato
        /// </summary>
        public string BlockedReason { get; set; }

        /// <summary>
        /// Battute con almeno un esito
        /// </summary>
        public List<TranslatedCueState> Translations { get; set; }

        /// <summary>
        /// Termini nuovi segnalati dal modello
        /// </summary>
        public List<CollectedNewTerm> CollectedNewTerms { get; set; }

        #endregion
    }

    /// <summary>
    /// Battuta del sorgente numerato congelata nel job
    /// </summary>
    public class FrozenCue
    {
        #region Proprietà

        /// <summary>
        /// Id della battuta
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Gruppo di frase spezzata, null fuori dai gruppi
        /// </summary>
        public int? Group { get; set; }

        /// <summary>
        /// Testo pulito coi segni ⟦⟧
        /// </summary>
        public string Text { get; set; }

        #endregion
    }

    /// <summary>
    /// Blocco del piano: intervallo di id consecutivi tradotto in una chiamata B
    /// </summary>
    public class TranslationBlock
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationBlock()
        {
            this.Index = 0;
            this.FirstId = 0;
            this.LastId = 0;
            this.CueCount = 0;
            this.Status = TranslationBlockStatus.Pending;
            this.SplitFrom = null;
            this.CallSeqs = new List<int>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Identificativo del blocco nel file, da 1, mai riusato
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Primo id
        /// </summary>
        public int FirstId { get; set; }

        /// <summary>
        /// Ultimo id
        /// </summary>
        public int LastId { get; set; }

        /// <summary>
        /// Battute del blocco
        /// </summary>
        public int CueCount { get; set; }

        /// <summary>
        /// Stato
        /// </summary>
        public TranslationBlockStatus Status { get; set; }

        /// <summary>
        /// Blocco dimezzato da cui viene, null per un blocco del piano
        /// </summary>
        public int? SplitFrom { get; set; }

        /// <summary>
        /// Chiamate B, B′ e B″ che l'hanno tradotto
        /// </summary>
        public List<int> CallSeqs { get; set; }

        #endregion
    }

    /// <summary>
    /// Esito di una battuta tradotta o da rivedere
    /// </summary>
    public class TranslatedCueState
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslatedCueState()
        {
            this.Id = 0;
            this.Status = TranslatedCueStatus.Accepted;
            this.Text = null;
            this.Attempts = 0;
            this.Rejections = new List<CueRejection>();
            this.ToReviewReason = null;
            this.GlossaryWarnings = new List<GlossaryWarning>();
            this.Signals = new List<TranslationCueSignal>();
            this.AcceptedCallSeq = null;
            this.Version = 0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Id della battuta
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Stato
        /// </summary>
        public TranslatedCueStatus Status { get; set; }

        /// <summary>
        /// Traduzione coi segni ⟦⟧, null per una battuta da rivedere senza testo
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Consegne provate
        /// </summary>
        public int Attempts { get; set; }

        /// <summary>
        /// Consegne respinte coi motivi del validatore
        /// </summary>
        public List<CueRejection> Rejections { get; set; }

        /// <summary>
        /// Motivo dello stato da rivedere: gli ultimi errori o incomplete
        /// </summary>
        public string ToReviewReason { get; set; }

        /// <summary>
        /// Termini del glossario non rispettati
        /// </summary>
        public List<GlossaryWarning> GlossaryWarnings { get; set; }

        /// <summary>
        /// Avvisi cumulativi
        /// </summary>
        public List<TranslationCueSignal> Signals { get; set; }

        /// <summary>
        /// Chiamata che ha dato il testo accettato
        /// </summary>
        public int? AcceptedCallSeq { get; set; }

        /// <summary>
        /// Sale di uno a ogni testo accettato
        /// </summary>
        public int Version { get; set; }

        #endregion
    }

    /// <summary>
    /// Consegna respinta di una battuta
    /// </summary>
    public class CueRejection
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public CueRejection()
        {
            this.CallSeq = 0;
            this.Attempt = 0;
            this.Reasons = new List<string>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Chiamata della consegna
        /// </summary>
        public int CallSeq { get; set; }

        /// <summary>
        /// Consegna del blocco, da 1
        /// </summary>
        public int Attempt { get; set; }

        /// <summary>
        /// Righe del validatore
        /// </summary>
        public List<string> Reasons { get; set; }

        #endregion
    }

    /// <summary>
    /// Termine del glossario non rispettato da una traduzione
    /// </summary>
    public class GlossaryWarning
    {
        #region Proprietà

        /// <summary>
        /// Termine del sorgente
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// Resa del glossario
        /// </summary>
        public string Target { get; set; }

        #endregion
    }

    /// <summary>
    /// Termine nuovo segnalato dal modello con la resa usata
    /// </summary>
    public class CollectedNewTerm
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public CollectedNewTerm()
        {
            this.Source = "";
            this.Target = "";
            this.Note = "";
            this.CallSeq = 0;
            this.BlockIndex = 0;
            this.CueIds = new List<int>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Termine segnalato
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// Resa usata
        /// </summary>
        public string Target { get; set; }

        /// <summary>
        /// Nota del modello
        /// </summary>
        public string Note { get; set; }

        /// <summary>
        /// Chiamata che l'ha segnalato
        /// </summary>
        public int CallSeq { get; set; }

        /// <summary>
        /// Blocco della chiamata
        /// </summary>
        public int BlockIndex { get; set; }

        /// <summary>
        /// Battute dell'intervallo il cui sorgente contiene il termine
        /// </summary>
        public List<int> CueIds { get; set; }

        #endregion
    }
}
