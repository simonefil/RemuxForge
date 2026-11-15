using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Esito della validazione di una consegna: codice, messaggio inglese per il modello e chiave del messaggio della UI con i
    /// suoi valori
    /// </summary>
    /// <param name="Id">Battuta, null per i problemi degli elementi scartati</param>
    /// <param name="Code">Codice dell'esito</param>
    /// <param name="Severity">Gravità</param>
    /// <param name="ModelMessage">Messaggio per il modello</param>
    /// <param name="UiKey">Chiave del messaggio della UI</param>
    /// <param name="Args">Valori dei segnaposto del messaggio della UI</param>
    public sealed record TranslationValidationIssue(int? Id, string Code, IssueSeverity Severity, string ModelMessage, string UiKey,
        IReadOnlyDictionary<string, string> Args)
    {
        /// <summary>
        /// Riga per il modello: «id CODICE: messaggio», senza id per i problemi degli elementi scartati
        /// </summary>
        /// <returns>Riga per il modello</returns>
        public string ToModelLine()
        {
            if (this.Id.HasValue)
                return this.Id.Value + " " + this.Code + ": " + this.ModelMessage;

            return this.Code + ": " + this.ModelMessage;
        }
    }
}
