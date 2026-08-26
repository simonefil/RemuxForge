using RemuxForge.Core.Localization;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Parametri di un singolo metodo Advanced Rename
    /// </summary>
    public class RenameMethod
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public RenameMethod()
        {
            this.MethodType = RenameMethodType.Replace;
            this.SearchText = "";
            this.ReplaceText = "";
            this.CaseSensitive = false;
            this.UseRegex = false;
            this.InsertText = "";
            this.InsertPosition = 0;
            this.FromEnd = false;
            this.RemoveByPattern = false;
            this.RemoveStartIndex = 0;
            this.RemoveCount = 0;
            this.RemoveFromEnd = false;
            this.RemovePattern = "";
            this.RemovePatternCaseSensitive = false;
            this.RemovePatternUseRegex = false;
            this.CaseMode = 0;
            this.CaseScope = 0;
            this.NamePattern = "<Name>.<Ext>";
            this.TrimCharacters = " ";
            this.TrimLocation = 2;
            this.TrimScope = 0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Tipo metodo
        /// </summary>
        public RenameMethodType MethodType { get; set; }

        /// <summary>
        /// Testo da cercare
        /// </summary>
        public string SearchText { get; set; }

        /// <summary>
        /// Testo sostitutivo
        /// </summary>
        public string ReplaceText { get; set; }

        /// <summary>
        /// True se la ricerca distingue maiuscole/minuscole
        /// </summary>
        public bool CaseSensitive { get; set; }

        /// <summary>
        /// True se SearchText è una regex
        /// </summary>
        public bool UseRegex { get; set; }

        /// <summary>
        /// Testo da inserire
        /// </summary>
        public string InsertText { get; set; }

        /// <summary>
        /// Posizione inserimento
        /// </summary>
        public int InsertPosition { get; set; }

        /// <summary>
        /// True per contare la posizione dalla fine
        /// </summary>
        public bool FromEnd { get; set; }

        /// <summary>
        /// True per rimuovere tramite pattern
        /// </summary>
        public bool RemoveByPattern { get; set; }

        /// <summary>
        /// Indice iniziale rimozione
        /// </summary>
        public int RemoveStartIndex { get; set; }

        /// <summary>
        /// Numero caratteri da rimuovere
        /// </summary>
        public int RemoveCount { get; set; }

        /// <summary>
        /// True per contare rimozione dalla fine
        /// </summary>
        public bool RemoveFromEnd { get; set; }

        /// <summary>
        /// Pattern da rimuovere
        /// </summary>
        public string RemovePattern { get; set; }

        /// <summary>
        /// True se il pattern rimozione distingue maiuscole/minuscole
        /// </summary>
        public bool RemovePatternCaseSensitive { get; set; }

        /// <summary>
        /// True se RemovePattern è una regex
        /// </summary>
        public bool RemovePatternUseRegex { get; set; }

        /// <summary>
        /// Modalità case: 0=lowercase, 1=UPPERCASE, 2=Title Case
        /// </summary>
        public int CaseMode { get; set; }

        /// <summary>
        /// Scope case: 0=nome, 1=estensione, 2=nome completo
        /// </summary>
        public int CaseScope { get; set; }

        /// <summary>
        /// Pattern nuovo nome con tag
        /// </summary>
        public string NamePattern { get; set; }

        /// <summary>
        /// Caratteri da trim
        /// </summary>
        public string TrimCharacters { get; set; }

        /// <summary>
        /// Posizione trim: 0=start, 1=end, 2=entrambi
        /// </summary>
        public int TrimLocation { get; set; }

        /// <summary>
        /// Scope trim: 0=nome, 1=estensione, 2=nome completo
        /// </summary>
        public int TrimScope { get; set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Crea una copia profonda del metodo
        /// </summary>
        /// <returns>Copia del metodo</returns>
        public RenameMethod Clone()
        {
            RenameMethod copy = new RenameMethod();
            copy.MethodType = this.MethodType;
            copy.SearchText = this.SearchText;
            copy.ReplaceText = this.ReplaceText;
            copy.CaseSensitive = this.CaseSensitive;
            copy.UseRegex = this.UseRegex;
            copy.InsertText = this.InsertText;
            copy.InsertPosition = this.InsertPosition;
            copy.FromEnd = this.FromEnd;
            copy.RemoveByPattern = this.RemoveByPattern;
            copy.RemoveStartIndex = this.RemoveStartIndex;
            copy.RemoveCount = this.RemoveCount;
            copy.RemoveFromEnd = this.RemoveFromEnd;
            copy.RemovePattern = this.RemovePattern;
            copy.RemovePatternCaseSensitive = this.RemovePatternCaseSensitive;
            copy.RemovePatternUseRegex = this.RemovePatternUseRegex;
            copy.CaseMode = this.CaseMode;
            copy.CaseScope = this.CaseScope;
            copy.NamePattern = this.NamePattern;
            copy.TrimCharacters = this.TrimCharacters;
            copy.TrimLocation = this.TrimLocation;
            copy.TrimScope = this.TrimScope;
            return copy;
        }

        /// <summary>
        /// Restituisce una descrizione sintetica del metodo
        /// </summary>
        /// <returns>Descrizione leggibile</returns>
        public string GetDisplayName()
        {
            string result = "";

            if (this.MethodType == RenameMethodType.Replace)
            {
                result = AppText.F("rename.method.replaceDisplay", this.SearchText, this.ReplaceText);
            }
            else if (this.MethodType == RenameMethodType.Add)
            {
                result = AppText.F("rename.method.addDisplay", this.InsertText, this.InsertPosition);
            }
            else if (this.MethodType == RenameMethodType.Remove)
            {
                if (this.RemoveByPattern)
                {
                    result = AppText.F("rename.method.removePatternDisplay", this.RemovePattern);
                }
                else
                {
                    result = AppText.F("rename.method.removeCharsDisplay", this.RemoveCount, this.RemoveStartIndex);
                }
            }
            else if (this.MethodType == RenameMethodType.NewCase)
            {
                string[] modes = { AppText.T("web.rename.case.lowercase"), AppText.T("web.rename.case.uppercase"), AppText.T("web.rename.case.title") };
                int modeIndex = this.CaseMode >= 0 && this.CaseMode < modes.Length ? this.CaseMode : 0;
                result = AppText.F("rename.method.caseDisplay", modes[modeIndex]);
            }
            else if (this.MethodType == RenameMethodType.NewName)
            {
                result = AppText.F("rename.method.nameDisplay", this.NamePattern);
            }
            else if (this.MethodType == RenameMethodType.Trim)
            {
                string[] locations = { AppText.T("web.rename.location.start"), AppText.T("web.rename.location.end"), AppText.T("web.rename.location.both") };
                int locIndex = this.TrimLocation >= 0 && this.TrimLocation < locations.Length ? this.TrimLocation : 2;
                result = AppText.F("rename.method.trimDisplay", locations[locIndex]);
            }

            return result;
        }

        #endregion
    }
}
