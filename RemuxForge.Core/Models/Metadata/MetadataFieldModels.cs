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
            this.Sector = MetadataFieldSector.File;
            this.TargetScopes = new List<MkvMetadataTargetScope>();
            this.ValueType = MetadataFieldValueType.String;
            this.Unit = "";
            this.IsReadable = true;
            this.IsEditable = false;
            this.IsClearable = false;
            this.RiskLevel = MetadataFieldRiskLevel.Normal;
            this.EditPolicy = MetadataFieldEditPolicy.ReadOnly;
            this.MediaInfoFieldNames = new List<string>();
            this.MkvPropEditProperty = "";
            this.MkvMergeArgument = "";
            this.AllowedValues = new List<string>();
            this.RequiresRemux = false;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Chiave interna campo
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// Etichetta UI
        /// </summary>
        public string Label { get; set; }

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
        public List<string> AllowedValues { get; set; }

        /// <summary>
        /// True se richiede remux
        /// </summary>
        public bool RequiresRemux { get; set; }

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

        #region Proprieta

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
