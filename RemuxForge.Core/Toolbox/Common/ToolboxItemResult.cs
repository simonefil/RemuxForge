using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.Common
{
    /// <summary>
    /// Stato di un elemento nel lotto di una card della toolbox
    /// </summary>
    public enum ToolboxItemStatus
    {
        /// <summary>In attesa</summary>
        Pending,

        /// <summary>Elaborazione in corso</summary>
        Running,

        /// <summary>File scritto</summary>
        Done,

        /// <summary>File scritto, con avvisi dello strumento</summary>
        DoneWithWarnings,

        /// <summary>Elaborazione non riuscita</summary>
        Failed,

        /// <summary>Elaborazione interrotta dallo stop</summary>
        Stopped
    }

    /// <summary>
    /// Esito di un elemento nel lotto di una card della toolbox
    /// </summary>
    public class ToolboxItemResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore di un esito in attesa
        /// </summary>
        public ToolboxItemResult() : this(ToolboxItemStatus.Pending)
        {
        }

        /// <summary>
        /// Costruttore di un esito senza file, motivo né avvisi
        /// </summary>
        /// <param name="status">Stato dell'elemento</param>
        public ToolboxItemResult(ToolboxItemStatus status)
        {
            this.Status = status;
            this.OutputPath = "";
            this.Message = "";
            this.Details = "";
            this.Warnings = new List<string>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Stato dell'elemento
        /// </summary>
        public ToolboxItemStatus Status { get; set; }

        /// <summary>
        /// File scritto, vuoto se l'elaborazione non è riuscita
        /// </summary>
        public string OutputPath { get; set; }

        /// <summary>
        /// Motivo dell'errore o dell'interruzione, senza percorsi completi
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Errori dello strumento così come li ha scritti, vuoto se il motivo non viene dallo strumento
        /// </summary>
        public string Details { get; set; }

        /// <summary>
        /// Avvisi dello strumento, anche con elaborazione riuscita
        /// </summary>
        public List<string> Warnings { get; }

        /// <summary>
        /// True se il file è scritto, anche con avvisi, e l'elemento non va rielaborato
        /// </summary>
        public bool IsDone
        {
            get { return this.Status == ToolboxItemStatus.Done || this.Status == ToolboxItemStatus.DoneWithWarnings; }
        }

        #endregion
    }
}
