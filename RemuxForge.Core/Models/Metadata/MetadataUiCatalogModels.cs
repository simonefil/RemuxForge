using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Campo metadata pronto per dropdown e browser UI
    /// </summary>
    public class MetadataCatalogFieldItem
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataCatalogFieldItem()
        {
            this.Key = "";
            this.Label = "";
            this.Description = "";
            this.Token = "";
            this.Sector = MetadataFieldSector.File;
            this.TargetScopes = new List<MkvMetadataTargetScope>();
            this.ValueType = MetadataFieldValueType.String;
            this.InputKind = MetadataFieldInputKind.Text;
            this.Visibility = MetadataFieldVisibility.Primary;
            this.IsEditable = false;
            this.IsClearable = false;
            this.Unit = "";
            this.SortGroup = 0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Chiave campo
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// Label campo
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// Descrizione campo
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Token inseribile
        /// </summary>
        public string Token { get; set; }

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
        /// Tipo input
        /// </summary>
        public MetadataFieldInputKind InputKind { get; set; }

        /// <summary>
        /// Visibilità UI
        /// </summary>
        public MetadataFieldVisibility Visibility { get; set; }

        /// <summary>
        /// True se editabile
        /// </summary>
        public bool IsEditable { get; set; }

        /// <summary>
        /// True se cancellabile
        /// </summary>
        public bool IsClearable { get; set; }

        /// <summary>
        /// Unità del campo
        /// </summary>
        public string Unit { get; set; }

        /// <summary>
        /// Gruppo ordinamento
        /// </summary>
        public int SortGroup { get; set; }

        #endregion
    }

    /// <summary>
    /// Definizione tag Matroska gestito dalla UI
    /// </summary>
    /// <summary>
    /// Livelli di target Matroska per i tag
    /// </summary>
    public static class MetadataTagTargetLevels
    {
        #region Costanti

        /// <summary>Traccia o brano</summary>
        public const int TRACK = 30;

        /// <summary>Film o episodio, il livello che Matroska assume quando manca</summary>
        public const int EPISODE = 50;

        /// <summary>Stagione o volume</summary>
        public const int SEASON = 60;

        /// <summary>Collezione o serie</summary>
        public const int COLLECTION = 70;

        #endregion
    }

    /// <summary>
    /// Chiave con cui un tag di contenitore viene indicizzato quando non sta al livello 50
    /// </summary>
    public static class MetadataTagKey
    {
        #region Metodi pubblici

        /// <summary>
        /// Costruisce la chiave livello:NOME
        /// </summary>
        /// <param name="targetTypeValue">Livello di target</param>
        /// <param name="tagName">Nome del tag</param>
        /// <returns>Chiave indicizzata</returns>
        public static string Build(int targetTypeValue, string tagName)
        {
            return targetTypeValue.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + (tagName != null ? tagName.Trim().ToUpperInvariant() : "");
        }

        #endregion
    }

    public class MetadataTagDefinition
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataTagDefinition()
        {
            this.Key = "";
            this.Name = "";
            this.Label = "";
            this.Description = "";
            this.ValueType = MetadataFieldValueType.String;
            this.InputKind = MetadataFieldInputKind.Text;
            this.Visibility = MetadataFieldVisibility.Primary;
            this.TargetScopes = new List<MkvMetadataTargetScope>();
            this.IsEditable = true;
            this.IsClearable = true;
            this.AllowedValues = new List<MetadataInputOption>();
            this.HelpKey = "";
            this.SortGroup = 0;
            this.DefaultTargetTypeValue = MetadataTagTargetLevels.EPISODE;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Livello di target con cui il tag va scritto se non specificato altrimenti
        /// </summary>
        public int DefaultTargetTypeValue { get; set; }

        /// <summary>
        /// Chiave interna tag
        /// </summary>
        public string Key { get; set; }

        /// <summary>
        /// Nome tag Matroska
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Label UI
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// Descrizione tag
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Tipo valore
        /// </summary>
        public MetadataFieldValueType ValueType { get; set; }

        /// <summary>
        /// Tipo input
        /// </summary>
        public MetadataFieldInputKind InputKind { get; set; }

        /// <summary>
        /// Visibilità UI
        /// </summary>
        public MetadataFieldVisibility Visibility { get; set; }

        /// <summary>
        /// Scope target compatibili
        /// </summary>
        public List<MkvMetadataTargetScope> TargetScopes { get; set; }

        /// <summary>
        /// True se editabile
        /// </summary>
        public bool IsEditable { get; set; }

        /// <summary>
        /// True se cancellabile
        /// </summary>
        public bool IsClearable { get; set; }

        /// <summary>
        /// Valori consentiti
        /// </summary>
        public List<MetadataInputOption> AllowedValues { get; set; }

        /// <summary>
        /// Chiave help specifica
        /// </summary>
        public string HelpKey { get; set; }

        /// <summary>
        /// Gruppo ordinamento
        /// </summary>
        public int SortGroup { get; set; }

        #endregion
    }

    /// <summary>
    /// Funzione expression disponibile per UI e validazione
    /// </summary>
    public class MetadataCatalogFunctionItem
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataCatalogFunctionItem()
        {
            this.Name = "";
            this.Call = "";
            this.Description = "";
            this.ExampleInput = "";
            this.ExampleExpression = "";
            this.ExampleOutput = "";
            this.ExampleNotes = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome funzione
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Chiamata inseribile
        /// </summary>
        public string Call { get; set; }

        /// <summary>
        /// Descrizione funzione
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Input esempio
        /// </summary>
        public string ExampleInput { get; set; }

        /// <summary>
        /// Espressione esempio
        /// </summary>
        public string ExampleExpression { get; set; }

        /// <summary>
        /// Output esempio
        /// </summary>
        public string ExampleOutput { get; set; }

        /// <summary>
        /// Note esempio
        /// </summary>
        public string ExampleNotes { get; set; }

        #endregion
    }

    /// <summary>
    /// Operatore condizione pronto per dropdown UI
    /// </summary>
    public class MetadataConditionOperatorItem
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataConditionOperatorItem()
        {
            this.Label = "";
            this.RequiresValue = false;
            this.RequiresRange = false;
            this.RequiresList = false;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Operatore dominio
        /// </summary>
        public MkvMetadataConditionOperator Operator { get; set; }

        /// <summary>
        /// Label localizzata
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// Vero se richiede un valore singolo
        /// </summary>
        public bool RequiresValue { get; set; }

        /// <summary>
        /// Vero se richiede un intervallo
        /// </summary>
        public bool RequiresRange { get; set; }

        /// <summary>
        /// Vero se richiede una lista valori
        /// </summary>
        public bool RequiresList { get; set; }

        #endregion
    }

    /// <summary>
    /// Help contestuale metadata
    /// </summary>
    public class MetadataHelpInfo
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MetadataHelpInfo()
        {
            this.Title = "";
            this.Text = "";
            this.Example = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Titolo help
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Testo help
        /// </summary>
        public string Text { get; set; }

        /// <summary>
        /// Esempio opzionale
        /// </summary>
        public string Example { get; set; }

        #endregion
    }

}
