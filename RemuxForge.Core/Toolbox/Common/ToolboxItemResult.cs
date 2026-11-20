using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using System;
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

        #region Metodi pubblici

        /// <summary>
        /// Segna l'elemento interrotto se lo stop è stato chiesto: uno strumento ucciso dallo stop ha un exit code qualsiasi, e lo
        /// stop si riconosce prima del codice
        /// </summary>
        /// <returns>True se l'elemento è interrotto</returns>
        public bool StopIfRequested()
        {
            if (!ProcessRunner.IsStopRequested())
                return false;

            this.Status = ToolboxItemStatus.Stopped;
            this.Message = AppText.T("toolbox.output.stopped");
            return true;
        }

        /// <summary>
        /// Segna l'elemento non riuscito per gli errori dello strumento: interi nel dettaglio, in riga senza percorsi completi
        /// </summary>
        /// <param name="errors">Errori dello strumento</param>
        /// <param name="shortenPaths">Sostituisce nel motivo in riga i percorsi completi con i nomi dei file</param>
        public void Fail(List<string> errors, Func<string, string> shortenPaths)
        {
            this.Status = ToolboxItemStatus.Failed;
            this.Details = string.Join(Environment.NewLine, errors);
            this.Message = shortenPaths(string.Join(" ", errors));
        }

        /// <summary>
        /// Segna l'elemento riuscito, con avvisi se lo strumento ne ha dati
        /// </summary>
        /// <param name="outputPath">File scritto</param>
        public void Complete(string outputPath)
        {
            this.Status = this.Warnings.Count > 0 ? ToolboxItemStatus.DoneWithWarnings : ToolboxItemStatus.Done;
            this.OutputPath = outputPath;
        }

        /// <summary>
        /// Aggiunge al motivo i temporanei rimasti sul disco, con il percorso; il dettaglio, quando c'è, prende il posto del motivo
        /// nel log e nel suggerimento della riga, e li riceve anche lui
        /// </summary>
        /// <param name="leftovers">Temporanei che non si sono cancellati</param>
        public void AddLeftovers(List<string> leftovers)
        {
            if (leftovers.Count == 0)
                return;

            this.Message = (this.Message + " " + string.Join(" ", leftovers)).Trim();
            if (this.Details.Length > 0)
                this.Details += Environment.NewLine + string.Join(Environment.NewLine, leftovers);
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
