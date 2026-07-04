using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Risultato esecuzione Advanced Rename
    /// </summary>
    public class RenameExecutionResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public RenameExecutionResult()
        {
            this.Success = true;
            this.SuccessCount = 0;
            this.FailCount = 0;
            this.ErrorMessage = "";
            this.Errors = new List<string>();
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// True se l'esecuzione è riuscita
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// File rinominati con successo
        /// </summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// File non rinominati
        /// </summary>
        public int FailCount { get; set; }

        /// <summary>
        /// Primo errore o errore aggregato
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Lista errori
        /// </summary>
        public List<string> Errors { get; set; }

        #endregion
    }
}
