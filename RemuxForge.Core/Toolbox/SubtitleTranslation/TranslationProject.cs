using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Passo della card raggiunto da un progetto
    /// </summary>
    public enum TranslationProjectStep
    {
        /// <summary>File, lingue e contesto</summary>
        Project,

        /// <summary>Analisi e glossario</summary>
        Glossary,

        /// <summary>Traduzione</summary>
        Translation,

        /// <summary>Revisione</summary>
        Review,

        /// <summary>Esportazione</summary>
        Export
    }

    /// <summary>
    /// Modalità della revisione AI
    /// </summary>
    public enum TranslationReviewMode
    {
        /// <summary>Nessuna revisione</summary>
        None,

        /// <summary>Tutti i blocchi</summary>
        Full
    }

    /// <summary>
    /// Tipo riconosciuto di un'unità del progetto: una combinazione di stile, attore ed effetto delle righe ASS/SSA, o un tratto
    /// di una tratta SRT ripetuta fra i file. Il file tradotto è quello sorgente con tradotto solo il testo nella lingua d'origine
    /// </summary>
    public enum TranslationStyleKind
    {
        /// <summary>Battute parlate</summary>
        Dialogue,

        /// <summary>Testi di canzoni</summary>
        Song,

        /// <summary>Scritte sullo schermo</summary>
        Sign,

        /// <summary>Testo da non tradurre: un'altra lingua (romaji, kanji) o i frammenti di un effetto (lettere o sillabe animate una
        /// per riga); lasciato identico byte per byte</summary>
        NotTranslatable
    }

    /// <summary>
    /// Progetto di traduzione: file sorgente, lingue, contesto, modelli e glossario del suo unico giro
    /// </summary>
    public class TranslationProject
    {
        #region Costanti

        /// <summary>
        /// Versione del formato di project.json
        /// </summary>
        public const int SCHEMA_VERSION = 3;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationProject()
        {
            this.SchemaVersion = SCHEMA_VERSION;
            this.Id = "";
            this.Name = "";
            this.CreatedAt = DateTimeOffset.Now;
            this.UpdatedAt = this.CreatedAt;
            this.SourceLanguage = "";
            this.TargetLanguage = "";
            this.Context = "";
            this.Models = new TranslationPhaseModels();
            this.ReasoningEfforts = new TranslationPhaseModels();
            this.Options = new TranslationProjectOptions();
            this.Files = new List<TranslationProjectFile>();
            this.Styles = new List<TranslationProjectStyle>();
            this.Stretches = null;
            this.NextFileNumber = 1;
            this.Glossary = null;
            this.ImportedGlossary = null;
            this.Analysis = null;
            this.ReachedStep = TranslationProjectStep.Project;
            this.ActiveJobId = null;
            this.JobIds = new List<string>();
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
        /// Nome mostrato nell'elenco
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Creazione
        /// </summary>
        public DateTimeOffset CreatedAt { get; set; }

        /// <summary>
        /// Ultima modifica
        /// </summary>
        public DateTimeOffset UpdatedAt { get; set; }

        /// <summary>
        /// Lingua d'origine ISO 639-2, vuota finché non è scelta
        /// </summary>
        public string SourceLanguage { get; set; }

        /// <summary>
        /// Lingua d'arrivo ISO 639-2, vuota finché non è scelta
        /// </summary>
        public string TargetLanguage { get; set; }

        /// <summary>
        /// Campo serie/contesto, anche vuoto
        /// </summary>
        public string Context { get; set; }

        /// <summary>
        /// Modello di ogni fase
        /// </summary>
        public TranslationPhaseModels Models { get; set; }

        /// <summary>
        /// Livello di ragionamento di ogni fase, accanto al modello
        /// </summary>
        public TranslationPhaseModels ReasoningEfforts { get; set; }

        /// <summary>
        /// Opzioni del progetto
        /// </summary>
        public TranslationProjectOptions Options { get; set; }

        /// <summary>
        /// File sorgente nell'ordine di aggiunta
        /// </summary>
        public List<TranslationProjectFile> Files { get; set; }

        /// <summary>
        /// Combinazioni di stile, attore ed effetto delle righe ASS/SSA col tipo riconosciuto, senza tipo prima del riconoscimento
        /// </summary>
        public List<TranslationProjectStyle> Styles { get; set; }

        /// <summary>
        /// Tratte SRT ripetute fra i file col tipo riconosciuto; null finché i tipi del progetto non sono riconosciuti, vuota dopo
        /// il riconoscimento se non ce ne sono
        /// </summary>
        public List<TranslationProjectStretch> Stretches { get; set; }

        /// <summary>
        /// Prossimo numero di fileId, mai riusato
        /// </summary>
        public int NextFileNumber { get; set; }

        /// <summary>
        /// Glossario corrente, bozza o approvato; null prima dell'analisi e del caricamento
        /// </summary>
        public Glossary Glossary { get; set; }

        /// <summary>
        /// File caricato con Importa glossario, null senza caricamento
        /// </summary>
        public ImportedGlossaryInfo ImportedGlossary { get; set; }

        /// <summary>
        /// Stato dell'analisi, null prima del primo Analizza
        /// </summary>
        public TranslationAnalysis Analysis { get; set; }

        /// <summary>
        /// Passo raggiunto
        /// </summary>
        public TranslationProjectStep ReachedStep { get; set; }

        /// <summary>
        /// Job attivo del progetto, null prima dell'avvio del giro
        /// </summary>
        public string ActiveJobId { get; set; }

        /// <summary>
        /// Job del progetto, il più recente in fondo
        /// </summary>
        public List<string> JobIds { get; set; }

        #endregion
    }

    /// <summary>
    /// Sottotitolo sorgente del progetto
    /// </summary>
    public class TranslationProjectFile
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationProjectFile()
        {
            this.Id = "";
            this.Path = "";
            this.Format = SubtitleTranslationFormat.Srt;
            this.Encoding = SubtitleTranslationDocument.DEFAULT_ENCODING;
            this.SizeBytes = 0;
            this.SourceSha256 = "";
            this.Preparation = new TranslationFilePreparation();
            this.RejectedReason = "";
            this.AddedAt = DateTimeOffset.Now;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Identificativo f1, f2, … mai riusato
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Percorso assoluto del sorgente
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// Formato
        /// </summary>
        public SubtitleTranslationFormat Format { get; set; }

        /// <summary>
        /// Codifica scelta per un sorgente che non è UTF-8
        /// </summary>
        public string Encoding { get; set; }

        /// <summary>
        /// Dimensione alla preparazione
        /// </summary>
        public long SizeBytes { get; set; }

        /// <summary>
        /// SHA-256 esadecimale dei byte alla preparazione
        /// </summary>
        public string SourceSha256 { get; set; }

        /// <summary>
        /// Esito della preparazione
        /// </summary>
        public TranslationFilePreparation Preparation { get; set; }

        /// <summary>
        /// Codice del motivo del rifiuto, vuoto per un file valido
        /// </summary>
        public string RejectedReason { get; set; }

        /// <summary>
        /// Aggiunta al progetto
        /// </summary>
        public DateTimeOffset AddedAt { get; set; }

        #endregion
    }

    /// <summary>
    /// Combinazione di stile, attore ed effetto delle righe ASS/SSA del progetto col tipo riconosciuto
    /// </summary>
    public class TranslationProjectStyle
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationProjectStyle()
        {
            this.Name = "";
            this.Actor = "";
            this.Effect = "";
            this.Kind = null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome dello stile, come nel campo Style delle righe
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Campo Name (attore) delle righe, senza spazi ai bordi
        /// </summary>
        public string Actor { get; set; }

        /// <summary>
        /// Campo Effect delle righe, senza spazi ai bordi
        /// </summary>
        public string Effect { get; set; }

        /// <summary>
        /// Tipo riconosciuto, null prima del riconoscimento: le righe valgono come dialogo
        /// </summary>
        public TranslationStyleKind? Kind { get; set; }

        #endregion
    }

    /// <summary>
    /// Tratta SRT ripetuta fra i file del progetto col tipo riconosciuto di ogni riga: le battute di un file che ne ripetono
    /// almeno tre righe consecutive prendono il tipo della loro riga
    /// </summary>
    public class TranslationProjectStretch
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationProjectStretch()
        {
            this.Lines = new List<string>();
            this.Kinds = new List<TranslationStyleKind>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Testi puliti delle battute della tratta, nell'ordine di tempo
        /// </summary>
        public List<string> Lines { get; set; }

        /// <summary>
        /// Tipo riconosciuto di ogni riga, nell'ordine di Lines: un riassunto seguito dalla sigla ha due tratti di tipo diverso
        /// </summary>
        public List<TranslationStyleKind> Kinds { get; set; }

        #endregion
    }

    /// <summary>
    /// Esito della preparazione di un sorgente
    /// </summary>
    public class TranslationFilePreparation
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationFilePreparation()
        {
            this.CueCount = 0;
            this.ExcludedCount = 0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Battute traducibili
        /// </summary>
        public int CueCount { get; set; }

        /// <summary>
        /// Battute lasciate al codice
        /// </summary>
        public int ExcludedCount { get; set; }

        #endregion
    }

    /// <summary>
    /// Un valore per fase dell'harness: modelli o livelli di ragionamento
    /// </summary>
    public class TranslationPhaseModels
    {
        #region Costanti

        /// <summary>
        /// Modello di tutti i passi di un progetto nuovo, se l'accesso lo offre
        /// </summary>
        public const string DEFAULT_MODEL = "gpt-6.1-sol";

        /// <summary>
        /// Livello di ragionamento di tutti i passi di un progetto nuovo, se il modello lo accetta
        /// </summary>
        public const string DEFAULT_REASONING_EFFORT = "medium";

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationPhaseModels()
        {
            this.Analysis = "";
            this.Translation = "";
            this.Review = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Analisi (A0, A1, A2)
        /// </summary>
        public string Analysis { get; set; }

        /// <summary>
        /// Traduzione dei blocchi
        /// </summary>
        public string Translation { get; set; }

        /// <summary>
        /// Revisione AI
        /// </summary>
        public string Review { get; set; }

        #endregion
    }

    /// <summary>
    /// Opzioni del progetto
    /// </summary>
    public class TranslationProjectOptions
    {
        #region Costanti

        /// <summary>
        /// File tradotti in parallelo di un progetto nuovo
        /// </summary>
        public const int DEFAULT_PARALLEL_FILES = 2;

        /// <summary>
        /// Minimo dei file tradotti in parallelo
        /// </summary>
        public const int MIN_PARALLEL_FILES = 1;

        /// <summary>
        /// Massimo dei file tradotti in parallelo
        /// </summary>
        public const int MAX_PARALLEL_FILES = 10;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationProjectOptions()
        {
            this.ReviewMode = TranslationReviewMode.Full;
            this.MaxParallelFiles = DEFAULT_PARALLEL_FILES;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// File tradotti in parallelo, da MIN_PARALLEL_FILES a MAX_PARALLEL_FILES
        /// </summary>
        public int MaxParallelFiles { get; set; }

        /// <summary>
        /// Modalità della revisione AI, completa nei progetti nuovi; i progetti salvati tengono la loro
        /// </summary>
        public TranslationReviewMode ReviewMode { get; set; }

        #endregion
    }

    /// <summary>
    /// File caricato con Importa glossario
    /// </summary>
    public class ImportedGlossaryInfo
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public ImportedGlossaryInfo()
        {
            this.FileName = "";
            this.Sha256 = "";
            this.ImportedAt = DateTimeOffset.Now;
            this.TermCount = 0;
            this.CharacterCount = 0;
            this.AddressingCount = 0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome del file senza percorso
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// SHA-256 dei byte del file
        /// </summary>
        public string Sha256 { get; set; }

        /// <summary>
        /// Momento del caricamento
        /// </summary>
        public DateTimeOffset ImportedAt { get; set; }

        /// <summary>
        /// Termini caricati
        /// </summary>
        public int TermCount { get; set; }

        /// <summary>
        /// Personaggi caricati
        /// </summary>
        public int CharacterCount { get; set; }

        /// <summary>
        /// Forme di cortesia caricate
        /// </summary>
        public int AddressingCount { get; set; }

        #endregion
    }
}
