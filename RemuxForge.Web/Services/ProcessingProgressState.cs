namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Stato avanzamento operativo esposto dalla WebUI
    /// </summary>
    public class ProcessingProgressState
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public ProcessingProgressState()
        {
            this.IsActive = false;
            this.Operation = "";
            this.CurrentEpisode = "";
            this.CurrentStatus = "";
            this.CurrentIndex = 0;
            this.Total = 0;
            this.Completed = 0;
            this.CurrentPercent = 0;
            this.GlobalPercent = 0;
            this.CurrentIndeterminate = false;
            this.GlobalIndeterminate = false;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Crea una copia isolata dello stato corrente
        /// </summary>
        /// <returns>Copia stato</returns>
        public ProcessingProgressState Clone()
        {
            ProcessingProgressState result = new ProcessingProgressState();
            result.IsActive = this.IsActive;
            result.Operation = this.Operation;
            result.CurrentEpisode = this.CurrentEpisode;
            result.CurrentStatus = this.CurrentStatus;
            result.CurrentIndex = this.CurrentIndex;
            result.Total = this.Total;
            result.Completed = this.Completed;
            result.CurrentPercent = this.CurrentPercent;
            result.GlobalPercent = this.GlobalPercent;
            result.CurrentIndeterminate = this.CurrentIndeterminate;
            result.GlobalIndeterminate = this.GlobalIndeterminate;
            return result;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// True se un'operazione è in corso
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// Nome operazione corrente
        /// </summary>
        public string Operation { get; set; }

        /// <summary>
        /// Episodio corrente
        /// </summary>
        public string CurrentEpisode { get; set; }

        /// <summary>
        /// Stato fase corrente
        /// </summary>
        public string CurrentStatus { get; set; }

        /// <summary>
        /// Indice episodio corrente, base 1
        /// </summary>
        public int CurrentIndex { get; set; }

        /// <summary>
        /// Numero totale episodi dell'operazione
        /// </summary>
        public int Total { get; set; }

        /// <summary>
        /// Episodi completati
        /// </summary>
        public int Completed { get; set; }

        /// <summary>
        /// Percentuale fase episodio corrente
        /// </summary>
        public int CurrentPercent { get; set; }

        /// <summary>
        /// Percentuale globale batch
        /// </summary>
        public int GlobalPercent { get; set; }

        /// <summary>
        /// True se la barra episodio è indeterminata
        /// </summary>
        public bool CurrentIndeterminate { get; set; }

        /// <summary>
        /// True se la barra globale è indeterminata
        /// </summary>
        public bool GlobalIndeterminate { get; set; }

        #endregion
    }
}
