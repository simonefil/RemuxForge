using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Esito della validazione di una consegna
    /// </summary>
    /// <param name="Cues">Esito di ogni battuta richiesta, in ordine di id; gli errori di consegna stanno su ogni battuta</param>
    /// <param name="Issues">Problemi degli elementi scartati, senza id</param>
    public sealed record TranslationValidationResult(IReadOnlyList<CueValidationResult> Cues, IReadOnlyList<TranslationValidationIssue> Issues)
    {
        /// <summary>
        /// Righe degli errori per il ritentativo del modello: prima gli elementi scartati, poi le battute in ordine di id; nessuna
        /// riga se tutte le battute sono valide
        /// </summary>
        /// <returns>Righe per il modello</returns>
        public IReadOnlyList<string> ToModelLines()
        {
            List<string> lines = new List<string>();
            foreach (CueValidationResult cue in this.Cues)
            {
                foreach (TranslationValidationIssue issue in cue.Issues)
                {
                    if (issue.Severity == IssueSeverity.Error)
                        lines.Add(issue.ToModelLine());
                }
            }

            if (lines.Count == 0)
                return lines;

            List<string> entries = new List<string>();
            foreach (TranslationValidationIssue issue in this.Issues)
                entries.Add(issue.ToModelLine());
            entries.AddRange(lines);
            return entries;
        }
    }
}
