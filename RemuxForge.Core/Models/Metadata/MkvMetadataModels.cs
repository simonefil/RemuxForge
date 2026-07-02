using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Politica output runtime della modalita' Metadata
    /// </summary>
    public enum MkvMetadataOutputPolicy
    {
        /// <summary>Sovrascrive il file sorgente</summary>
        Overwrite,

        /// <summary>Scrive su percorso output</summary>
        OutputPath
    }

    /// <summary>
    /// Stato analisi metadata
    /// </summary>
    public enum MkvMetadataAnalysisStatus
    {
        /// <summary>Non analizzato</summary>
        NotAnalyzed,

        /// <summary>Analizzato</summary>
        Analyzed,

        /// <summary>Analisi obsoleta</summary>
        Stale,

        /// <summary>Errore analisi</summary>
        Error
    }

    /// <summary>
    /// Modalita' tecnica di esecuzione metadata
    /// </summary>
    public enum MkvMetadataExecutionMode
    {
        /// <summary>Nessuna modifica</summary>
        NoOp,

        /// <summary>Modifica in place con mkvpropedit</summary>
        PropEdit,

        /// <summary>Copia e modifica con mkvpropedit</summary>
        CopyPropEdit,

        /// <summary>Remux con mkvmerge</summary>
        MkvMerge
    }

    /// <summary>
    /// Opzioni runtime della modalita' Metadata
    /// </summary>
    public class MkvMetadataOptions
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataOptions()
        {
            this.SourcePath = "";
            this.PresetPath = "";
            this.OutputPolicy = MkvMetadataOutputPolicy.Overwrite;
            this.OutputDir = "";
            this.Recursive = true;
            this.PreserveFolderStructure = true;
            this.DryRun = false;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// File o cartella input
        /// </summary>
        public string SourcePath { get; set; }

        /// <summary>
        /// Percorso preset JSON pipeline-only
        /// </summary>
        public string PresetPath { get; set; }

        /// <summary>
        /// Politica output runtime
        /// </summary>
        public MkvMetadataOutputPolicy OutputPolicy { get; set; }

        /// <summary>
        /// Cartella output quando OutputPolicy e' OutputPath
        /// </summary>
        public string OutputDir { get; set; }

        /// <summary>
        /// True per scansione ricorsiva
        /// </summary>
        public bool Recursive { get; set; }

        /// <summary>
        /// Mantiene struttura relativa con output path ricorsivo
        /// </summary>
        public bool PreserveFolderStructure { get; set; }

        /// <summary>
        /// Analizza senza eseguire scritture
        /// </summary>
        public bool DryRun { get; set; }

        #endregion
    }

    /// <summary>
    /// Preset regole Metadata
    /// </summary>
    public class MkvMetadataPreset
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataPreset()
        {
            this.SchemaVersion = 3;
            this.Name = "";
            this.Description = "";
            this.Rules = new List<MkvMetadataRule>();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Versione schema preset
        /// </summary>
        public int SchemaVersion { get; set; }

        /// <summary>
        /// Nome preset
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Descrizione preset
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Regole ordinate
        /// </summary>
        public List<MkvMetadataRule> Rules { get; set; }

        #endregion
    }

    /// <summary>
    /// Regola Metadata seriale
    /// </summary>
    public class MkvMetadataRule
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataRule()
        {
            this.Description = "";
            this.Enabled = true;
            this.TargetScope = MkvMetadataTargetScope.Audio;
            this.When = new MkvMetadataRuleWhen();
            this.Operations = new List<MkvMetadataOperation>();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Descrizione obbligatoria
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Regola abilitata
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Ambito target
        /// </summary>
        public MkvMetadataTargetScope TargetScope { get; set; }

        /// <summary>
        /// Condizioni QUANDO
        /// </summary>
        public MkvMetadataRuleWhen When { get; set; }

        /// <summary>
        /// Operazioni ALLORA
        /// </summary>
        public List<MkvMetadataOperation> Operations { get; set; }

        #endregion
    }

    /// <summary>
    /// Ambito target regola
    /// </summary>
    public enum MkvMetadataTargetScope
    {
        /// <summary>File/container</summary>
        Container,

        /// <summary>Tracce video</summary>
        Video,

        /// <summary>Tracce audio</summary>
        Audio,

        /// <summary>Tracce sottotitoli</summary>
        Subtitle
    }

    /// <summary>
    /// Blocco QUANDO root: tutte le condizioni sono in AND
    /// </summary>
    public class MkvMetadataRuleWhen
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataRuleWhen()
        {
            this.All = new List<MkvMetadataRuleConditionNode>();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Condizioni root in AND
        /// </summary>
        public List<MkvMetadataRuleConditionNode> All { get; set; }

        #endregion
    }

    /// <summary>
    /// Tipo nodo condizione dominio
    /// </summary>
    public enum MkvMetadataRuleConditionNodeType
    {
        Field,
        TrackComparison,
        TrackGroupCount,
        AlternativeAny
    }

    /// <summary>
    /// Nodo condizione dominio
    /// </summary>
    public class MkvMetadataRuleConditionNode
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataRuleConditionNode()
        {
            this.NodeType = MkvMetadataRuleConditionNodeType.Field;
            this.Field = new MkvMetadataFieldCondition();
            this.TrackComparison = null;
            this.TrackGroupCount = null;
            this.Alternative = null;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Tipo nodo
        /// </summary>
        public MkvMetadataRuleConditionNodeType NodeType { get; set; }

        /// <summary>
        /// Condizione su campo
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MkvMetadataFieldCondition Field { get; set; }

        /// <summary>
        /// Condizione confronto con altre tracce
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MkvMetadataTrackComparisonCondition TrackComparison { get; set; }

        /// <summary>
        /// Condizione conteggio gruppo tracce
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MkvMetadataTrackGroupCountCondition TrackGroupCount { get; set; }

        /// <summary>
        /// Blocco alternativo OR
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MkvMetadataAlternativeAnyBlock Alternative { get; set; }

        #endregion
    }

    /// <summary>
    /// Blocco alternativo: almeno una condizione deve essere vera
    /// </summary>
    public class MkvMetadataAlternativeAnyBlock
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataAlternativeAnyBlock()
        {
            this.Any = new List<MkvMetadataRuleConditionNode>();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Condizioni alternative in OR
        /// </summary>
        public List<MkvMetadataRuleConditionNode> Any { get; set; }

        #endregion
    }

    /// <summary>
    /// Condizione su campo MediaInfo/metadata
    /// </summary>
    public class MkvMetadataFieldCondition
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataFieldCondition()
        {
            this.FieldKey = "";
            this.Operator = MkvMetadataConditionOperator.Equals;
            this.Value = "";
            this.Values = new List<string>();
            this.FromValue = "";
            this.ToValue = "";
            this.Unit = "";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Chiave campo
        /// </summary>
        public string FieldKey { get; set; }

        /// <summary>
        /// Operatore condizione
        /// </summary>
        public MkvMetadataConditionOperator Operator { get; set; }

        /// <summary>
        /// Valore singolo
        /// </summary>
        public string Value { get; set; }

        /// <summary>
        /// Valori lista per InList/NotInList
        /// </summary>
        public List<string> Values { get; set; }

        /// <summary>
        /// Valore minimo per range
        /// </summary>
        public string FromValue { get; set; }

        /// <summary>
        /// Valore massimo per range
        /// </summary>
        public string ToValue { get; set; }

        /// <summary>
        /// Unita' valore
        /// </summary>
        public string Unit { get; set; }

        #endregion
    }

    /// <summary>
    /// Condizione confronto con altre tracce
    /// </summary>
    public class MkvMetadataTrackComparisonCondition
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataTrackComparisonCondition()
        {
            this.FieldKey = "";
            this.Relation = MkvMetadataTrackComparisonRelation.Largest;
            this.Group = MkvMetadataTrackGroup.AllInScope;
            this.Rank = 1;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Campo confrontato
        /// </summary>
        public string FieldKey { get; set; }

        /// <summary>
        /// Relazione confronto
        /// </summary>
        public MkvMetadataTrackComparisonRelation Relation { get; set; }

        /// <summary>
        /// Gruppo tracce
        /// </summary>
        public MkvMetadataTrackGroup Group { get; set; }

        /// <summary>
        /// Rank richiesto
        /// </summary>
        public int Rank { get; set; }

        #endregion
    }

    /// <summary>
    /// Condizione conteggio gruppo tracce
    /// </summary>
    public class MkvMetadataTrackGroupCountCondition
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataTrackGroupCountCondition()
        {
            this.Group = MkvMetadataTrackGroup.AllInScope;
            this.Operator = MkvMetadataConditionOperator.GreaterThan;
            this.Value = 1;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Gruppo tracce
        /// </summary>
        public MkvMetadataTrackGroup Group { get; set; }

        /// <summary>
        /// Operatore numerico
        /// </summary>
        public MkvMetadataConditionOperator Operator { get; set; }

        /// <summary>
        /// Conteggio confrontato
        /// </summary>
        public int Value { get; set; }

        #endregion
    }

    /// <summary>
    /// Operatore condizione metadata
    /// </summary>
    public enum MkvMetadataConditionOperator
    {
        Equals,
        NotEquals,
        Contains,
        NotContains,
        StartsWith,
        EndsWith,
        Regex,
        NotRegex,
        IsEmpty,
        IsNotEmpty,
        InList,
        NotInList,
        GreaterThan,
        GreaterOrEqual,
        LessThan,
        LessOrEqual,
        Between,
        NotBetween,
        IsTrue,
        IsFalse
    }

    /// <summary>
    /// Relazione confronto con altre tracce
    /// </summary>
    public enum MkvMetadataTrackComparisonRelation
    {
        EqualsAny,
        NotEqualsAll,
        GreaterThanAll,
        GreaterOrEqualAll,
        LessThanAll,
        LessOrEqualAll,
        Largest,
        Smallest,
        Rank
    }

    /// <summary>
    /// Gruppo tracce dominio
    /// </summary>
    public enum MkvMetadataTrackGroup
    {
        SameLanguage,
        SameFormat,
        SameLanguageAndFormat,
        AllInScope
    }

    /// <summary>
    /// Operazione THEN metadata
    /// </summary>
    public class MkvMetadataOperation
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataOperation()
        {
            this.Type = MkvMetadataOperationType.SetField;
            this.FieldKey = "";
            this.Value = "";
            this.TagKey = "";
            this.TagTarget = MkvMetadataTagTarget.Current;
            this.ClearTagsConfirmed = false;
            this.ExclusiveGroup = MkvMetadataExclusiveGroup.AllInScope;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Tipo operazione
        /// </summary>
        public MkvMetadataOperationType Type { get; set; }

        /// <summary>
        /// Chiave campo target
        /// </summary>
        public string FieldKey { get; set; }

        /// <summary>
        /// Valore o template
        /// </summary>
        public string Value { get; set; }

        /// <summary>
        /// Chiave tag gestita da UI
        /// </summary>
        public string TagKey { get; set; }

        /// <summary>
        /// Target tag gestito da UI
        /// </summary>
        public MkvMetadataTagTarget TagTarget { get; set; }

        /// <summary>
        /// Conferma esplicita per ClearTags
        /// </summary>
        public bool ClearTagsConfirmed { get; set; }

        /// <summary>
        /// Gruppo esclusivita' per SetExclusiveFlag
        /// </summary>
        public MkvMetadataExclusiveGroup ExclusiveGroup { get; set; }

        #endregion
    }

    /// <summary>
    /// Tipo operazione metadata
    /// </summary>
    public enum MkvMetadataOperationType
    {
        SetField,
        ClearField,
        SetExclusiveFlag,
        RemoveTrack,
        AddOrUpdateTrackStatisticsTags,
        DeleteTrackStatisticsTags,
        SetTagField,
        ClearTagField,
        ClearTags
    }

    /// <summary>
    /// Target operazioni tag gestite da UI
    /// </summary>
    public enum MkvMetadataTagTarget
    {
        Current,
        File,
        CurrentTrack,
        AllTracks
    }

    /// <summary>
    /// Gruppo per flag esclusivi
    /// </summary>
    public enum MkvMetadataExclusiveGroup
    {
        SameLanguage,
        SameFormat,
        SameLanguageAndFormat,
        AllInScope
    }

    /// <summary>
    /// Record operativo Metadata
    /// </summary>
    public class MkvMetadataRecord
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataRecord()
        {
            this.InputFile = "";
            this.RelativeFolder = "";
            this.Status = "Pending";
            this.ErrorMessage = "";
            this.AnalysisStatus = MkvMetadataAnalysisStatus.NotAnalyzed;
            this.ExecutionMode = MkvMetadataExecutionMode.NoOp;
            this.FileInfo = new MkvMetadataFileInfo();
            this.OriginalFileInfo = new MkvMetadataFileInfo();
            this.Changes = new List<MkvMetadataChange>();
            this.CommandPreview = "";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// File input
        /// </summary>
        public string InputFile { get; set; }

        /// <summary>
        /// Cartella relativa rispetto alla sorgente
        /// </summary>
        public string RelativeFolder { get; set; }

        /// <summary>
        /// Dimensione file
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// Stato testuale
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Messaggio errore
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Stato analisi
        /// </summary>
        public MkvMetadataAnalysisStatus AnalysisStatus { get; set; }

        /// <summary>
        /// Modalita' esecuzione prevista
        /// </summary>
        public MkvMetadataExecutionMode ExecutionMode { get; set; }

        /// <summary>
        /// Numero regole matchate
        /// </summary>
        public int MatchCount { get; set; }

        /// <summary>
        /// Numero modifiche previste
        /// </summary>
        public int ChangeCount { get; set; }

        /// <summary>
        /// Informazioni metadata lette
        /// </summary>
        public MkvMetadataFileInfo FileInfo { get; set; }

        /// <summary>
        /// Snapshot originale letto da MediaInfo
        /// </summary>
        public MkvMetadataFileInfo OriginalFileInfo { get; set; }

        /// <summary>
        /// Modifiche previste dall'analisi
        /// </summary>
        public List<MkvMetadataChange> Changes { get; set; }

        /// <summary>
        /// Anteprima comando/piano esecuzione
        /// </summary>
        public string CommandPreview { get; set; }

        #endregion
    }

    /// <summary>
    /// Modifica metadata prevista
    /// </summary>
    public class MkvMetadataChange
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataChange()
        {
            this.RuleDescription = "";
            this.Scope = MkvMetadataTargetScope.Container;
            this.TrackSelector = "";
            this.TrackKind = "";
            this.TrackUniqueId = "";
            this.FieldKey = "";
            this.MkvPropEditProperty = "";
            this.BeforeValue = "";
            this.AfterValue = "";
            this.OperationType = MkvMetadataOperationType.SetField;
            this.RequiresRemux = false;
            this.Message = "";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Descrizione regola che ha prodotto la modifica
        /// </summary>
        public string RuleDescription { get; set; }

        /// <summary>
        /// Scope modifica
        /// </summary>
        public MkvMetadataTargetScope Scope { get; set; }

        /// <summary>
        /// Selector traccia
        /// </summary>
        public string TrackSelector { get; set; }

        /// <summary>
        /// Tipo traccia
        /// </summary>
        public string TrackKind { get; set; }

        /// <summary>
        /// UID traccia se disponibile
        /// </summary>
        public string TrackUniqueId { get; set; }

        /// <summary>
        /// Chiave campo
        /// </summary>
        public string FieldKey { get; set; }

        /// <summary>
        /// Proprieta' mkvpropedit
        /// </summary>
        public string MkvPropEditProperty { get; set; }

        /// <summary>
        /// Valore precedente
        /// </summary>
        public string BeforeValue { get; set; }

        /// <summary>
        /// Valore nuovo
        /// </summary>
        public string AfterValue { get; set; }

        /// <summary>
        /// Tipo operazione
        /// </summary>
        public MkvMetadataOperationType OperationType { get; set; }

        /// <summary>
        /// True se richiede remux
        /// </summary>
        public bool RequiresRemux { get; set; }

        /// <summary>
        /// Messaggio descrittivo
        /// </summary>
        public string Message { get; set; }

        #endregion
    }

    /// <summary>
    /// Risultato esecuzione metadata su un file
    /// </summary>
    public class MkvMetadataExecutionResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataExecutionResult()
        {
            this.InputFile = "";
            this.OutputFile = "";
            this.ExitCode = 0;
            this.ErrorMessage = "";
            this.CommandText = "";
            this.DryRun = false;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// File input
        /// </summary>
        public string InputFile { get; set; }

        /// <summary>
        /// File output effettivo
        /// </summary>
        public string OutputFile { get; set; }

        /// <summary>
        /// Codice uscita
        /// </summary>
        public int ExitCode { get; set; }

        /// <summary>
        /// Messaggio errore
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Comando o riepilogo comandi
        /// </summary>
        public string CommandText { get; set; }

        /// <summary>
        /// True se non sono state eseguite scritture
        /// </summary>
        public bool DryRun { get; set; }

        #endregion
    }

    /// <summary>
    /// Informazioni metadata lette da MediaInfo
    /// </summary>
    public class MkvMetadataFileInfo
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataFileInfo()
        {
            this.FilePath = "";
            this.FileName = "";
            this.FileStem = "";
            this.FileExtension = "";
            this.ContainerTitle = "";
            this.RawGeneral = new Dictionary<string, string>();
            this.Fields = new Dictionary<string, string>();
            this.Tags = new Dictionary<string, string>();
            this.Tracks = new List<MkvMetadataTrackInfo>();
            this.OtherStreams = new List<MkvMetadataTrackInfo>();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Percorso file
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Nome file
        /// </summary>
        public string FileName { get; set; }

        /// <summary>
        /// Nome file senza estensione
        /// </summary>
        public string FileStem { get; set; }

        /// <summary>
        /// Estensione file
        /// </summary>
        public string FileExtension { get; set; }

        /// <summary>
        /// Dimensione file
        /// </summary>
        public long FileSize { get; set; }

        /// <summary>
        /// Titolo container
        /// </summary>
        public string ContainerTitle { get; set; }

        /// <summary>
        /// Campi raw General MediaInfo
        /// </summary>
        public Dictionary<string, string> RawGeneral { get; set; }

        /// <summary>
        /// Alias normalizzati file/container
        /// </summary>
        public Dictionary<string, string> Fields { get; set; }

        /// <summary>
        /// Tag globali MKV gestiti da UI
        /// </summary>
        public Dictionary<string, string> Tags { get; set; }

        /// <summary>
        /// Tracce
        /// </summary>
        public List<MkvMetadataTrackInfo> Tracks { get; set; }

        /// <summary>
        /// Stream MediaInfo non gestiti come tracce modificabili
        /// </summary>
        public List<MkvMetadataTrackInfo> OtherStreams { get; set; }

        #endregion
    }

    /// <summary>
    /// Informazioni metadata di una traccia
    /// </summary>
    public class MkvMetadataTrackInfo
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataTrackInfo()
        {
            this.MediaInfoType = "";
            this.TrackKind = "";
            this.TrackSelector = "";
            this.Format = "";
            this.CodecId = "";
            this.Title = "";
            this.Language = "";
            this.LanguageIetf = "";
            this.RawFields = new Dictionary<string, string>();
            this.Fields = new Dictionary<string, string>();
            this.Tags = new Dictionary<string, string>();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Tipo MediaInfo originale
        /// </summary>
        public string MediaInfoType { get; set; }

        /// <summary>
        /// Tipo normalizzato: video/audio/subtitles
        /// </summary>
        public string TrackKind { get; set; }

        /// <summary>
        /// Indice traccia nello stream MediaInfo
        /// </summary>
        public int StreamOrder { get; set; }

        /// <summary>
        /// Indice 1-based nel tipo traccia
        /// </summary>
        public int TypeIndex { get; set; }

        /// <summary>
        /// Selector mkvpropedit, ad esempio track:a1
        /// </summary>
        public string TrackSelector { get; set; }

        /// <summary>
        /// ID traccia MKV se disponibile
        /// </summary>
        public int TrackId { get; set; }

        /// <summary>
        /// UID traccia se disponibile
        /// </summary>
        public string TrackUniqueId { get; set; }

        /// <summary>
        /// Formato traccia
        /// </summary>
        public string Format { get; set; }

        /// <summary>
        /// Codec ID
        /// </summary>
        public string CodecId { get; set; }

        /// <summary>
        /// Titolo/nome traccia
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Lingua normalizzata
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// Lingua IETF/BCP 47
        /// </summary>
        public string LanguageIetf { get; set; }

        /// <summary>
        /// Dimensione stream
        /// </summary>
        public long StreamSize { get; set; }

        /// <summary>
        /// Campi raw MediaInfo
        /// </summary>
        public Dictionary<string, string> RawFields { get; set; }

        /// <summary>
        /// Alias normalizzati
        /// </summary>
        public Dictionary<string, string> Fields { get; set; }

        /// <summary>
        /// Tag traccia MKV gestiti da UI
        /// </summary>
        public Dictionary<string, string> Tags { get; set; }

        #endregion
    }
}
