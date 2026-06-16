using System.Collections.Generic;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Risultato comune della riscrittura canvas sottotitoli
    /// </summary>
    internal class SubtitleCanvasRewriteResult
    {
        #region Variabili di classe

        /// <summary>
        /// Contatori numerici prodotti dal rewriter
        /// </summary>
        private readonly Dictionary<string, int> _counters;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public SubtitleCanvasRewriteResult()
        {
            this._counters = new Dictionary<string, int>();
            this.Format = "";
            this.ErrorMessage = "";
            this.Summary = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Incrementa un contatore
        /// </summary>
        /// <param name="name">Nome contatore</param>
        public void Increment(string name)
        {
            this.Add(name, 1);
        }

        /// <summary>
        /// Incrementa un contatore del valore indicato
        /// </summary>
        /// <param name="name">Nome contatore</param>
        /// <param name="value">Valore da aggiungere</param>
        public void Add(string name, int value)
        {
            if (!this._counters.ContainsKey(name))
            {
                this._counters[name] = 0;
            }

            this._counters[name] += value;
        }

        /// <summary>
        /// Imposta un contatore
        /// </summary>
        /// <param name="name">Nome contatore</param>
        /// <param name="value">Valore contatore</param>
        public void Set(string name, int value)
        {
            this._counters[name] = value;
        }

        /// <summary>
        /// Legge un contatore
        /// </summary>
        /// <param name="name">Nome contatore</param>
        /// <returns>Valore contatore, 0 se assente</returns>
        public int Get(string name)
        {
            int result;
            if (!this._counters.TryGetValue(name, out result))
            {
                result = 0;
            }

            return result;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Formato sottotitolo processato
        /// </summary>
        public string Format { get; set; }

        /// <summary>
        /// Messaggio errore per fallback
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Summary compatto per log
        /// </summary>
        public string Summary { get; set; }

        #endregion
    }
}
