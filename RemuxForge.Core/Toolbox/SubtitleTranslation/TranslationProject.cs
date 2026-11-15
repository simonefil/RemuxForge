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

        /// <summary>Termini nuovi</summary>
        NewTerms,

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
        /// <summary>Solo i blocchi con avvisi</summary>
        Targeted,

        /// <summary>Tutti i blocchi</summary>
        Full
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
        public const int SCHEMA_VERSION = 1;

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
            this.NextFileNumber = 1;
            this.Glossary = null;
            this.ImportedGlossary = null;
            this.Analysis = null;
            this.ReachedStep = TranslationProjectStep.Project;
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
            this.GroupCount = 0;
            this.NumberedSourceSha256 = "";
            this.PreparerVersion = 0;
            this.PreparedAt = DateTimeOffset.Now;
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

        /// <summary>
        /// Gruppi di frase spezzata
        /// </summary>
        public int GroupCount { get; set; }

        /// <summary>
        /// SHA-256 del sorgente numerato in UTF-8
        /// </summary>
        public string NumberedSourceSha256 { get; set; }

        /// <summary>
        /// Versione della preparazione
        /// </summary>
        public int PreparerVersion { get; set; }

        /// <summary>
        /// Momento della preparazione
        /// </summary>
        public DateTimeOffset PreparedAt { get; set; }

        #endregion
    }

    /// <summary>
    /// Un valore per fase dell'harness: modelli o livelli di ragionamento
    /// </summary>
    public class TranslationPhaseModels
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationPhaseModels()
        {
            this.Analysis = "";
            this.Translation = "";
            this.NewTerms = "";
            this.Review = null;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Valore della revisione: il suo, oppure quello della traduzione finché non si cambia
        /// </summary>
        /// <returns>Valore della revisione</returns>
        public string ResolveReview()
        {
            return this.Review ?? this.Translation;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Analisi (A1, A2)
        /// </summary>
        public string Analysis { get; set; }

        /// <summary>
        /// Traduzione dei blocchi
        /// </summary>
        public string Translation { get; set; }

        /// <summary>
        /// Applicazione dei termini nuovi
        /// </summary>
        public string NewTerms { get; set; }

        /// <summary>
        /// Revisione AI; null segue Translation
        /// </summary>
        public string Review { get; set; }

        #endregion
    }

    /// <summary>
    /// Opzioni del progetto
    /// </summary>
    public class TranslationProjectOptions
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationProjectOptions()
        {
            this.ReviewMode = TranslationReviewMode.Targeted;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Modalità della revisione AI
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
