using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Fase del giro di un job
    /// </summary>
    public enum TranslationJobPhase
    {
        /// <summary>Traduzione a blocchi</summary>
        Translation,

        /// <summary>Revisione AI</summary>
        Review,

        /// <summary>Revisione dell'utente</summary>
        UserReview
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
        Paused
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

        /// <summary>Testo cambiato dalla revisione AI</summary>
        CorrectedByAi,

        /// <summary>Testo scritto dall'utente</summary>
        EditedByUser
    }

    /// <summary>
    /// Revisione AI di un blocco
    /// </summary>
    public enum TranslationReviewBlockStatus
    {
        /// <summary>Diagnosi da fare</summary>
        Pending,

        /// <summary>Diagnosi salvata, correzione da fare</summary>
        Diagnosed,

        /// <summary>Blocco rivisto; le battute di una parte senza diagnosi valida hanno il motivo nel loro esito</summary>
        Done
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
        public const int SCHEMA_VERSION = 3;

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
            this.ReviewMode = TranslationReviewMode.Full;
            this.SharedStretches = new List<SharedStretch>();
            this.Exported = false;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Riporta il job letto da disco, dopo una chiusura brusca, in uno stato da cui il giro prosegue: nella revisione
        /// dell'utente il giro aspetta l'utente e non ha lavoro in corso, quindi torna fermo; nelle altre fasi un giro rimasto in
        /// corso diventa fermato da una chiusura dell'app
        /// </summary>
        /// <returns>True se lo stato è cambiato e il job va salvato</returns>
        public bool RecoverAfterCrash()
        {
            if (this.Phase == TranslationJobPhase.UserReview)
            {
                if (this.Status == TranslationJobStatus.Idle)
                    return false;

                this.Status = TranslationJobStatus.Idle;
                this.Pause = null;
                return true;
            }

            if (this.Status != TranslationJobStatus.Running)
                return false;

            this.Status = TranslationJobStatus.Paused;
            this.Pause = new TranslationStop { Reason = TranslationStopReason.Crash, At = DateTimeOffset.Now };
            return true;
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

        /// <summary>
        /// Modalità della revisione AI del progetto all'avvio, completa se il file non la porta
        /// </summary>
        public TranslationReviewMode ReviewMode { get; set; }

        /// <summary>
        /// Tratte ripetute fra i file, tradotte una volta sola; vuoto nei job creati prima delle tratte
        /// </summary>
        public List<SharedStretch> SharedStretches { get; set; }

        /// <summary>
        /// True dopo un export che ha scritto tutti i file, finché una battuta non cambia; falso se il file non lo porta
        /// </summary>
        public bool Exported { get; set; }

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
        public const int SCHEMA_VERSION = 3;

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
            this.ReviewBlocks = null;
            this.ExportedPath = "";
            this.ExportedSha256 = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Battute congelate da una a un'altra nell'ordine di Cues, estremi compresi: l'ordine di tempo nei job nuovi, quello di id
        /// nei job salvati prima, dove il tratto coincide con l'intervallo di id
        /// </summary>
        /// <param name="firstId">Prima battuta</param>
        /// <param name="lastId">Ultima battuta</param>
        /// <returns>Battute del tratto</returns>
        public List<FrozenCue> GetSpan(int firstId, int lastId)
        {
            int first = this.Cues.FindIndex(c => c.Id == firstId);
            return this.Cues.GetRange(first, this.Cues.FindIndex(c => c.Id == lastId) - first + 1);
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
        /// Sorgente numerato congelato: la parte del file nel prefisso non cambia per tutta la vita del job, mentre contesto della
        /// serie e glossario possono cambiare e aprire un prefisso nuovo. In ordine di tempo, che dà l'ordine di blocchi, contesto e
        /// revisione; i job salvati prima lo hanno in ordine di id
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
        /// Blocchi scelti per la revisione AI, null prima della revisione
        /// </summary>
        public List<TranslationReviewBlock> ReviewBlocks { get; set; }

        /// <summary>
        /// Percorso dell'ultimo export del file, vuoto prima del primo
        /// </summary>
        public string ExportedPath { get; set; }

        /// <summary>
        /// SHA-256 del contenuto scritto dall'ultimo export: un file con un altro contenuto è stato cambiato fuori dal job e non si sovrascrive
        /// </summary>
        public string ExportedSha256 { get; set; }

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
        /// Testo pulito coi segni ⟦⟧
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Dialogo, canzone o cartello; dialogo nei job salvati prima dei tipi
        /// </summary>
        public SubtitleCueKind Kind { get; set; }

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
        /// Prima battuta nell'ordine delle battute congelate
        /// </summary>
        public int FirstId { get; set; }

        /// <summary>
        /// Ultima battuta nell'ordine delle battute congelate
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
            this.Repairs = new List<string>();
            this.Signals = new List<TranslationCueSignal>();
            this.AcceptedCallSeq = null;
            this.AiCorrection = null;
            this.NotReviewedReason = null;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Mette un testo nuovo validato: avvisi del glossario dal validatore, segnale Glossario messo o tolto; una battuta da
        /// rivedere diventa accettata. Un testo cambiato toglie le riparazioni del codice, che riguardavano il testo di prima
        /// </summary>
        /// <param name="text">Testo validato</param>
        /// <param name="warnings">Avvisi del glossario del validatore sul testo</param>
        public void SetText(string text, List<GlossaryWarning> warnings)
        {
            if (!string.Equals(text, this.Text, StringComparison.Ordinal))
                this.Repairs = new List<string>();
            this.Text = text;
            this.Status = TranslatedCueStatus.Accepted;
            this.ToReviewReason = null;
            this.GlossaryWarnings = warnings;
            this.Signals.Remove(TranslationCueSignal.Glossary);
            if (this.GlossaryWarnings.Count > 0)
                this.Signals.Add(TranslationCueSignal.Glossary);
        }

        /// <summary>
        /// Codici del motivo più recente dello stato da rivedere, per la griglia e per la diagnosi: quello del servizio, una parola
        /// sola, o quelli dell'ultimo rifiuto del validatore, di cui il motivo è la copia
        /// </summary>
        /// <returns>Codici distinti nell'ordine, vuoti senza motivo</returns>
        public List<string> GetReasonCodes()
        {
            if (this.ToReviewReason != null && this.ToReviewReason.IndexOf(' ') < 0)
                return new List<string> { this.ToReviewReason };

            return TranslationValidationIssue.ReadCodes(this.Rejections.Count > 0 ? this.Rejections[this.Rejections.Count - 1].Reasons : new List<string>());
        }

        /// <summary>
        /// Copia indipendente dell'esito con un altro id, per le copie di una tratta condivisa
        /// </summary>
        /// <param name="id">Id della copia</param>
        /// <returns>Esito copiato</returns>
        public TranslatedCueState Clone(int id)
        {
            TranslatedCueState clone = new TranslatedCueState();
            clone.Id = id;
            clone.Status = this.Status;
            clone.Text = this.Text;
            clone.Attempts = this.Attempts;
            foreach (CueRejection rejection in this.Rejections)
                clone.Rejections.Add(new CueRejection { CallSeq = rejection.CallSeq, Attempt = rejection.Attempt, Reasons = new List<string>(rejection.Reasons) });
            clone.ToReviewReason = this.ToReviewReason;
            foreach (GlossaryWarning warning in this.GlossaryWarnings)
                clone.GlossaryWarnings.Add(new GlossaryWarning { Source = warning.Source, Target = warning.Target });
            clone.Repairs.AddRange(this.Repairs);
            clone.Signals.AddRange(this.Signals);
            clone.AcceptedCallSeq = this.AcceptedCallSeq;
            clone.AiCorrection = this.AiCorrection != null ? new CueAiCorrection { Before = this.AiCorrection.Before, Explanation = this.AiCorrection.Explanation } : null;
            clone.NotReviewedReason = this.NotReviewedReason;
            return clone;
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
        /// Riparazioni del codice sul testo accettato, nelle righe del validatore: formattazione tolta, caratteri riservati e
        /// invisibili tolti
        /// </summary>
        public List<string> Repairs { get; set; }

        /// <summary>
        /// Avvisi cumulativi
        /// </summary>
        public List<TranslationCueSignal> Signals { get; set; }

        /// <summary>
        /// Chiamata che ha dato il testo accettato
        /// </summary>
        public int? AcceptedCallSeq { get; set; }

        /// <summary>
        /// Testo di prima e motivo della correzione della revisione AI, null se la revisione non l'ha cambiata
        /// </summary>
        public CueAiCorrection AiCorrection { get; set; }

        /// <summary>
        /// Motivo per cui la revisione AI non ha diagnosticato la battuta, nel codice dei motivi delle battute da rivedere: una
        /// parte del suo blocco senza diagnosi valida anche dopo i tentativi e i dimezzamenti. Null per una battuta diagnosticata o
        /// fuori dalla revisione; la modifica a mano lo toglie
        /// </summary>
        public string NotReviewedReason { get; set; }

        #endregion
    }

    /// <summary>
    /// Correzione della revisione AI di una battuta, mostrata al passaggio del mouse sull'avviso
    /// </summary>
    public class CueAiCorrection
    {
        #region Proprietà

        /// <summary>
        /// Testo prima della correzione, null per una battuta senza testo accettato
        /// </summary>
        public string Before { get; set; }

        /// <summary>
        /// Spiegazione della diagnosi, null per una battuta senza un problema proprio
        /// </summary>
        public string Explanation { get; set; }

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
    /// Blocco del piano scelto per la revisione AI
    /// </summary>
    public class TranslationReviewBlock
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationReviewBlock()
        {
            this.BlockIndex = 0;
            this.SharedStretch = null;
            this.FirstId = 0;
            this.LastId = 0;
            this.Status = TranslationReviewBlockStatus.Pending;
            this.Parts = null;
            this.Issues = new List<TranslationReviewIssue>();
            this.CallSeqs = new List<int>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Blocco del piano
        /// </summary>
        public int BlockIndex { get; set; }

        /// <summary>
        /// Tratta condivisa rivista dal blocco, sulle sole battute della sua prima occorrenza; null per un blocco del piano, che
        /// salta le battute delle tratte
        /// </summary>
        public int? SharedStretch { get; set; }

        /// <summary>
        /// Prima battuta del blocco nell'ordine delle battute congelate
        /// </summary>
        public int FirstId { get; set; }

        /// <summary>
        /// Ultima battuta del blocco nell'ordine delle battute congelate
        /// </summary>
        public int LastId { get; set; }

        /// <summary>
        /// Stato
        /// </summary>
        [JsonConverter(typeof(TranslationReviewBlockStatusJsonConverter))]
        public TranslationReviewBlockStatus Status { get; set; }

        /// <summary>
        /// Parti della diagnosi ancora da fare dopo un dimezzamento, in ordine di tempo; null finché la diagnosi non si è divisa.
        /// Le parti già diagnosticate hanno i loro problemi in Issues e la ripresa non le rifà
        /// </summary>
        public List<List<int>> Parts { get; set; }

        /// <summary>
        /// Problemi della diagnosi tenuti, per la correzione anche dopo una ripresa
        /// </summary>
        public List<TranslationReviewIssue> Issues { get; set; }

        /// <summary>
        /// Chiamate salvate del blocco: la diagnosi, poi le correzioni
        /// </summary>
        public List<int> CallSeqs { get; set; }

        #endregion
    }

    /// <summary>
    /// Problema di una battuta trovato dalla diagnosi
    /// </summary>
    public class TranslationReviewIssue
    {
        #region Proprietà

        /// <summary>
        /// Id della battuta
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Categoria
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Gravità
        /// </summary>
        public string Severity { get; set; }

        /// <summary>
        /// Spiegazione per l'utente
        /// </summary>
        public string Explanation { get; set; }

        /// <summary>
        /// Testo corretto suggerito, vuoto se la diagnosi non lo dà
        /// </summary>
        public string Suggestion { get; set; }

        #endregion
    }

    /// <summary>
    /// Stato della revisione AI di un blocco in JSON col nome del valore, come gli altri enum dei file della traduzione. In
    /// lettura accetta anche DiagnosisFailed e Failed dei job e degli archivi salvati prima, i blocchi non rivisti di allora, che
    /// diventano blocchi conclusi: il motivo stava nel blocco e non arriva alle battute
    /// </summary>
    public sealed class TranslationReviewBlockStatusJsonConverter : JsonConverter<TranslationReviewBlockStatus>
    {
        #region Costanti

        /// <summary>
        /// Valore tolto: diagnosi non valida salvata, con la correzione delle sole battute da rivedere ancora da fare
        /// </summary>
        private const string LEGACY_DIAGNOSIS_FAILED = "DiagnosisFailed";

        /// <summary>
        /// Valore tolto: blocco non rivisto, col motivo nel blocco
        /// </summary>
        private const string LEGACY_FAILED = "Failed";

        #endregion

        #region Metodi pubblici

        /// <inheritdoc />
        public override TranslationReviewBlockStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            string name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (string.Equals(name, LEGACY_DIAGNOSIS_FAILED, StringComparison.OrdinalIgnoreCase) || string.Equals(name, LEGACY_FAILED, StringComparison.OrdinalIgnoreCase))
                return TranslationReviewBlockStatus.Done;

            TranslationReviewBlockStatus status;
            if (name == null || !Array.Exists(Enum.GetNames<TranslationReviewBlockStatus>(), n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                || !Enum.TryParse(name, true, out status))
                throw new JsonException("Stato del blocco di revisione non valido: " + (name ?? reader.TokenType.ToString()));

            return status;
        }

        /// <inheritdoc />
        public override void Write(Utf8JsonWriter writer, TranslationReviewBlockStatus value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }

        #endregion
    }
}
