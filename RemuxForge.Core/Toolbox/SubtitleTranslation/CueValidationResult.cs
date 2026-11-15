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
