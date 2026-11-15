using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Stato del glossario di un progetto
    /// </summary>
    public enum GlossaryStatus
    {
        /// <summary>Proposto dall'analisi o caricato, modificabile</summary>
        Draft,

        /// <summary>Approvato dall'utente</summary>
        Approved
    }

    /// <summary>
    /// Tipo di un termine del glossario
    /// </summary>
    public enum GlossaryTermKind
    {
        /// <summary>Nome proprio</summary>
        Name,

        /// <summary>Luogo</summary>
        Place,

        /// <summary>Organizzazione</summary>
        Organization,

        /// <summary>Titolo</summary>
        Title,

        /// <summary>Termine ricorrente</summary>
        Term,

        /// <summary>Tormentone o frase fatta</summary>
        Catchphrase
    }

    /// <summary>
    /// Genere di un personaggio
    /// </summary>
    public enum CharacterGender
    {
        /// <summary>Maschile</summary>
        Male,

        /// <summary>Femminile</summary>
        Female,

        /// <summary>Altro</summary>
        Other,

        /// <summary>Non noto</summary>
        Unknown
    }

    /// <summary>
    /// Provenienza di una voce del glossario
    /// </summary>
    public enum GlossaryOrigin
    {
        /// <summary>Proposta dall'analisi</summary>
        Analysis,

        /// <summary>Scritta o modificata dall'utente</summary>
        User,

        /// <summary>Caricata con Importa glossario</summary>
        Import,

        /// <summary>Termine nuovo approvato dopo la traduzione</summary>
        NewTerm
    }

    /// <summary>
    /// Glossario di un progetto: termini, personaggi, forme di cortesia, tono e regole di registro della serie
    /// </summary>
    public class Glossary
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public Glossary()
        {
            this.Revision = 0;
            this.Status = GlossaryStatus.Draft;
            this.ApprovedAt = null;
            this.SourceLanguage = "";
            this.TargetLanguage = "";
            this.Series = "";
            this.Tone = "";
            this.RegisterRules = "";
            this.Terms = new List<GlossaryTerm>();
            this.Characters = new List<GlossaryCharacter>();
            this.Addressing = new List<GlossaryAddressing>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Revisione, che sale a ogni approvazione
        /// </summary>
        public int Revision { get; set; }

        /// <summary>
        /// Bozza o approvato
        /// </summary>
        public GlossaryStatus Status { get; set; }

        /// <summary>
        /// Ultima approvazione, null per una bozza mai approvata
        /// </summary>
        public DateTimeOffset? ApprovedAt { get; set; }

        /// <summary>
        /// Lingua d'origine ISO 639-2 per cui vale
        /// </summary>
        public string SourceLanguage { get; set; }

        /// <summary>
        /// Lingua d'arrivo ISO 639-2 per cui vale
        /// </summary>
        public string TargetLanguage { get; set; }

        /// <summary>
        /// Serie identificata
        /// </summary>
        public string Series { get; set; }

        /// <summary>
        /// Tono dei dialoghi e come renderlo
        /// </summary>
        public string Tone { get; set; }

        /// <summary>
        /// Regole generali delle forme di cortesia e del registro
        /// </summary>
        public string RegisterRules { get; set; }

        /// <summary>
        /// Termini
        /// </summary>
        public List<GlossaryTerm> Terms { get; set; }

        /// <summary>
        /// Personaggi
        /// </summary>
        public List<GlossaryCharacter> Characters { get; set; }

        /// <summary>
        /// Forme di cortesia fra chi parla e a chi
        /// </summary>
        public List<GlossaryAddressing> Addressing { get; set; }

        #endregion
    }

    /// <summary>
    /// Termine del glossario
    /// </summary>
    public class GlossaryTerm
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public GlossaryTerm()
        {
            this.Source = "";
            this.Target = "";
            this.Kind = GlossaryTermKind.Term;
            this.Invariable = false;
            this.Note = "";
            this.Origin = GlossaryOrigin.User;
            this.Episodes = new List<string>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Termine nella lingua d'origine, senza articolo iniziale
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// Resa nella lingua d'arrivo; uguale a Source se resta invariato
        /// </summary>
        public string Target { get; set; }

        /// <summary>
        /// Tipo del termine
        /// </summary>
        public GlossaryTermKind Kind { get; set; }

        /// <summary>
        /// True per un nome proprio che non si flette
        /// </summary>
        public bool Invariable { get; set; }

        /// <summary>
        /// Nota per il traduttore, vuota se nessuna
        /// </summary>
        public string Note { get; set; }

        /// <summary>
        /// Provenienza della voce
        /// </summary>
        public GlossaryOrigin Origin { get; set; }

        /// <summary>
        /// Identificativi dei file in cui compare il termine
        /// </summary>
        public List<string> Episodes { get; set; }

        #endregion
    }

    /// <summary>
    /// Personaggio del glossario
    /// </summary>
    public class GlossaryCharacter
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public GlossaryCharacter()
        {
            this.Name = "";
            this.Gender = CharacterGender.Unknown;
            this.Role = "";
            this.Relationships = "";
            this.Origin = GlossaryOrigin.User;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Genere
        /// </summary>
        public CharacterGender Gender { get; set; }

        /// <summary>
        /// Ruolo nella serie
        /// </summary>
        public string Role { get; set; }

        /// <summary>
        /// Rapporti con gli altri personaggi
        /// </summary>
        public string Relationships { get; set; }

        /// <summary>
        /// Provenienza della voce
        /// </summary>
        public GlossaryOrigin Origin { get; set; }

        #endregion
    }

    /// <summary>
    /// Forma di cortesia fra chi parla e a chi
    /// </summary>
    public class GlossaryAddressing
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public GlossaryAddressing()
        {
            this.Speaker = "";
            this.Addressee = "";
            this.Form = "";
            this.Note = "";
            this.Origin = GlossaryOrigin.User;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Chi parla
        /// </summary>
        public string Speaker { get; set; }

        /// <summary>
        /// A chi parla
        /// </summary>
        public string Addressee { get; set; }

        /// <summary>
        /// Forma della lingua d'arrivo (tu, lei, voi per l'italiano)
        /// </summary>
        public string Form { get; set; }

        /// <summary>
        /// Nota, vuota se nessuna
        /// </summary>
        public string Note { get; set; }

        /// <summary>
        /// Provenienza della voce
        /// </summary>
        public GlossaryOrigin Origin { get; set; }

        #endregion
    }
}
