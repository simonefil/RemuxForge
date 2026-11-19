using System;
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
        #region Costanti

        /// <summary>
        /// Prefisso della chiave del motivo breve di un rifiuto per l'utente, seguito dal codice dell'esito o da incomplete
        /// </summary>
        public const string REASON_KEY_PREFIX = "toolbox.subtitleTranslation.reason.";

        #endregion

        #region Metodi pubblici

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

        /// <summary>
        /// Codici degli esiti dalle righe per il modello, del validatore «id CODICE: messaggio» o «CODICE: messaggio» e del
        /// glossario «CODICE percorso: messaggio»: la prima parola in maiuscolo prima dei due punti
        /// </summary>
        /// <param name="lines">Righe di un rifiuto</param>
        /// <returns>Codici distinti nell'ordine</returns>
        public static List<string> ReadCodes(IEnumerable<string> lines)
        {
            List<string> codes = new List<string>();
            foreach (string line in lines)
            {
                int colon = line.IndexOf(':');
                string head = colon < 0 ? line : line.Substring(0, colon);
                string code = Array.Find(head.Split(' '), w => w.Length > 0 && char.IsAsciiLetterUpper(w[0]) && string.Equals(w, w.ToUpperInvariant(), StringComparison.Ordinal)) ?? head.Trim();
                if (!codes.Contains(code))
                    codes.Add(code);
            }

            return codes;
        }

        #endregion
    }
}
