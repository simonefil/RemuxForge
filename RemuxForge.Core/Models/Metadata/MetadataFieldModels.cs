using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Settore campo metadata
    /// </summary>
    public enum MetadataFieldSector
    {
        File,
        Container,
        Video,
        Audio,
        Subtitle,
        Tag
    }

    /// <summary>
    /// Tipo valore campo metadata
    /// </summary>
    public enum MetadataFieldValueType
    {
        String,
        Integer,
        Decimal,
        Boolean,
        Duration,
        Bytes,
        Language,
        LanguageIetf,
        Date
    }

    /// <summary>
    /// Livello rischio campo metadata
    /// </summary>
    public enum MetadataFieldRiskLevel
    {
        Normal,
        Advanced,
        Dangerous
    }

    /// <summary>
    /// Policy editabilità campo metadata
    /// </summary>
    public enum MetadataFieldEditPolicy
    {
        ReadOnly,
        Editable,
        Advanced,
        Blocked
    }

    /// <summary>
    /// Visibilità UI campo metadata
    /// </summary>
    public enum MetadataFieldVisibility
    {
        Primary,
        Advanced,
        Technical,
        Hidden
    }

    /// <summary>
    /// Tipo input UI suggerito dal backend
    /// </summary>
    public enum MetadataFieldInputKind
    {
        Text,
        Number,
        Decimal,
        Boolean,
        Select,
        LanguageSelect,
        LanguageIetf,
        SizeInput,
        DurationInput,
        DateInput
    }

    /// <summary>
    /// Contesto d'uso di uno schema input metadata
    /// </summary>
    public enum MetadataCatalogInputUsage
    {
        ConditionValue,
        OperationValue,
        ManualEdit,
        Sheet
    }

    /// <summary>
    /// Definizione campo metadata leggibile/editabile
    /// </summary>
    public class MetadataFieldDefinition
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataFieldDefinition()
        {
            this.Key = "";
            this.Label = "";
            this.Description = "";
            this.Sector = MetadataFieldSector.File;
            this.TargetScopes = new List<MkvMetadataTargetScope>();
            this.ValueType = MetadataFieldValueType.String;
            this.InputKind = MetadataFieldInputKind.Text;
            this.Unit = "";
            this.IsReadable = true;
            this.IsEditable = false;
            this.IsClearable = false;
            this.RiskLevel = MetadataFieldRiskLevel.Normal;
            this.EditPolicy = MetadataFieldEditPolicy.ReadOnly;
            this.Visibility = MetadataFieldVisibility.Primary;
            this.MediaInfoFieldNames = new List<string>();
            this.MkvPropEditProperty = "";
            this.MkvMergeArgument = "";
            this.AllowedValues = new List<MetadataInputOption>();
            this.HelpKey = "";
            this.SortGroup = 0;
            this.RequiresRemux = false;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Chiave interna campo
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// Etichetta UI
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// Descrizione breve per help e browser token
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Settore campo
        /// </summary>
        public MetadataFieldSector Sector { get; set; }

        /// <summary>
        /// Scope target compatibili
        /// </summary>
        public List<MkvMetadataTargetScope> TargetScopes { get; set; }

        /// <summary>
        /// Tipo valore
        /// </summary>
        public MetadataFieldValueType ValueType { get; set; }

        /// <summary>
        /// Tipo input UI suggerito
        /// </summary>
        public MetadataFieldInputKind InputKind { get; set; }

        /// <summary>
        /// Unità di misura
        /// </summary>
        public string Unit { get; set; }

        /// <summary>
        /// Campo leggibile
        /// </summary>
        public bool IsReadable { get; set; }

        /// <summary>
        /// Campo editabile
        /// </summary>
        public bool IsEditable { get; set; }

        /// <summary>
        /// Campo cancellabile
        /// </summary>
        public bool IsClearable { get; set; }

        /// <summary>
        /// Livello rischio
        /// </summary>
        public MetadataFieldRiskLevel RiskLevel { get; set; }

        /// <summary>
        /// Policy editabilità
        /// </summary>
        public MetadataFieldEditPolicy EditPolicy { get; set; }

        /// <summary>
        /// Visibilità UI
        /// </summary>
        public MetadataFieldVisibility Visibility { get; set; }

        /// <summary>
        /// Nomi campi MediaInfo sorgente
        /// </summary>
        public List<string> MediaInfoFieldNames { get; set; }

        /// <summary>
        /// Proprietà mkvpropedit
        /// </summary>
        public string MkvPropEditProperty { get; set; }

        /// <summary>
        /// Argomento mkvmerge se serve remux
        /// </summary>
        public string MkvMergeArgument { get; set; }

        /// <summary>
        /// Valori consentiti
        /// </summary>
        public List<MetadataInputOption> AllowedValues { get; set; }

        /// <summary>
        /// Chiave help specifica
        /// </summary>
        public string HelpKey { get; set; }

        /// <summary>
        /// Gruppo ordinamento UI
        /// </summary>
        public int SortGroup { get; set; }

        /// <summary>
        /// True se richiede remux
        /// </summary>
        public bool RequiresRemux { get; set; }

        #endregion
    }

    /// <summary>
    /// Opzione selezionabile per input metadata
    /// </summary>
    public class MetadataInputOption
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataInputOption()
        {
            this.Value = "";
            this.Label = "";
            this.Description = "";
        }

        /// <summary>
        /// Costruttore con valore e label
        /// </summary>
        /// <param name="value">Valore serializzato</param>
        /// <param name="label">Label visualizzata</param>
        public MetadataInputOption(string value, string label)
        {
            this.Value = value != null ? value : "";
            this.Label = label != null ? label : "";
            this.Description = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Valore serializzato
        /// </summary>
        public string Value { get; set; }

        /// <summary>
        /// Label visualizzata
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// Descrizione breve
        /// </summary>
        public string Description { get; set; }

        #endregion
    }

    /// <summary>
    /// Schema input metadata consumabile dalla UI
    /// </summary>
    public class MetadataInputSchema
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataInputSchema()
        {
            this.InputKind = MetadataFieldInputKind.Text;
            this.ValueType = MetadataFieldValueType.String;
            this.HtmlInputType = "text";
            this.Step = "";
            this.Placeholder = "";
            this.Unit = "";
            this.Options = new List<MetadataInputOption>();
            this.UnitOptions = new List<MetadataInputOption>();
            this.SupportsExpression = false;
            this.AllowsEmpty = true;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Tipo input da renderizzare
        /// </summary>
        public MetadataFieldInputKind InputKind { get; set; }

        /// <summary>
        /// Tipo logico del valore
        /// </summary>
        public MetadataFieldValueType ValueType { get; set; }

        /// <summary>
        /// Tipo input HTML base
        /// </summary>
        public string HtmlInputType { get; set; }

        /// <summary>
        /// Step input numerico
        /// </summary>
        public string Step { get; set; }

        /// <summary>
        /// Placeholder visualizzato
        /// </summary>
        public string Placeholder { get; set; }

        /// <summary>
        /// Unità base del valore
        /// </summary>
        public string Unit { get; set; }

        /// <summary>
        /// Opzioni valore
        /// </summary>
        public List<MetadataInputOption> Options { get; set; }

        /// <summary>
        /// Opzioni unità
        /// </summary>
        public List<MetadataInputOption> UnitOptions { get; set; }

        /// <summary>
        /// True se il campo accetta token/funzioni
        /// </summary>
        public bool SupportsExpression { get; set; }

        /// <summary>
        /// True se il valore vuoto è consentito
        /// </summary>
        public bool AllowsEmpty { get; set; }

        #endregion
    }

    /// <summary>
    /// Risultato validazione preset metadata
    /// </summary>
    public class MkvMetadataPresetValidationResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvMetadataPresetValidationResult()
        {
            this.Errors = new List<string>();
            this.Warnings = new List<string>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Aggiunge errore
        /// </summary>
        public void AddError(string text)
        {
            this.Errors.Add(text);
        }

        /// <summary>
        /// Aggiunge warning
        /// </summary>
        public void AddWarning(string text)
        {
            this.Warnings.Add(text);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Errori
        /// </summary>
        public List<string> Errors { get; private set; }

        /// <summary>
        /// Warning
        /// </summary>
        public List<string> Warnings { get; private set; }

        /// <summary>
        /// True se valido
        /// </summary>
        public bool IsValid
        {
            get { return this.Errors.Count == 0; }
        }

        /// <summary>
        /// Messaggio errori aggregato
        /// </summary>
        public string ErrorMessage
        {
            get { return string.Join("\n", this.Errors); }
        }

        #endregion
    }
}
