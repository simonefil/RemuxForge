using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Esito della validazione di una battuta richiesta
    /// </summary>
    /// <param name="Id">Battuta</param>
    /// <param name="Text">Testo consegnato dopo la normalizzazione, null se la battuta non ha un testo da giudicare</param>
    /// <param name="Issues">Errori e avvisi in ordine di controllo</param>
    public sealed record CueValidationResult(int Id, string Text, IReadOnlyList<TranslationValidationIssue> Issues)
    {
        /// <summary>
        /// Avvisi del glossario dell'esito, per lo stato della battuta
        /// </summary>
        /// <returns>Termini non rispettati</returns>
        public List<GlossaryWarning> GetGlossaryWarnings()
        {
            List<GlossaryWarning> warnings = new List<GlossaryWarning>();
            foreach (TranslationValidationIssue issue in this.Issues)
            {
                if (issue.Code == TranslationValidator.GLOSSARY_MISSING)
                    warnings.Add(new GlossaryWarning { Source = issue.Args["source"], Target = issue.Args["target"] });
            }

            return warnings;
        }

        /// <summary>
        /// La battuta ha almeno un errore e torna al modello
        /// </summary>
        public bool HasErrors
        {
            get
            {
                foreach (TranslationValidationIssue issue in this.Issues)
                {
                    if (issue.Severity == IssueSeverity.Error)
                        return true;
                }

                return false;
            }
        }
    }
}
