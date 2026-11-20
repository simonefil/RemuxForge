using RemuxForge.Core.Models;
using System;
using System.Diagnostics;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Tentativo di trasporto di una chiamata arrivato al servizio: la riga del registro delle chiamate
    /// </summary>
    public class TranslationTransportAttempt
    {
        #region Variabili di classe

        /// <summary>
        /// Misura della durata
        /// </summary>
        private readonly Stopwatch _watch;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore, all'invio del tentativo
        /// </summary>
        /// <param name="number">Tentativo del retry di trasporto, da 1</param>
        /// <param name="prepared">Richiesta mandata</param>
        public TranslationTransportAttempt(int number, AiResponseRequest prepared)
        {
            this.Number = number;
            this.Model = prepared.Model;
            this.ReasoningEffort = string.IsNullOrEmpty(prepared.ReasoningEffort) ? null : prepared.ReasoningEffort;
            this.SessionId = prepared.SessionId;
            this.StartedAt = DateTimeOffset.Now;
            this.DurationMs = 0;
            this.Result = null;
            this.Error = null;
            this.Stopped = false;
            this._watch = Stopwatch.StartNew();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Chiude la misura del tentativo
        /// </summary>
        public void Finish()
        {
            this._watch.Stop();
            this.DurationMs = this._watch.ElapsedMilliseconds;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Tentativo del retry di trasporto, da 1
        /// </summary>
        public int Number { get; }

        /// <summary>
        /// Modello mandato
        /// </summary>
        public string Model { get; }

        /// <summary>
        /// Livello di ragionamento mandato, null se tolto
        /// </summary>
        public string ReasoningEffort { get; }

        /// <summary>
        /// Valore di session_id mandato
        /// </summary>
        public string SessionId { get; }

        /// <summary>
        /// Ora d'invio
        /// </summary>
        public DateTimeOffset StartedAt { get; }

        /// <summary>
        /// Durata fino alla risposta o all'errore
        /// </summary>
        public long DurationMs { get; private set; }

        /// <summary>
        /// Risposta, null se il tentativo è fallito
        /// </summary>
        public AiResponseResult Result { get; set; }

        /// <summary>
        /// Errore del tentativo, null se è riuscito o è stato interrotto da Stop
        /// </summary>
        public AiServiceException Error { get; set; }

        /// <summary>
        /// True se Stop ha interrotto il tentativo: il consumo è reale e la riga del registro resta
        /// </summary>
        public bool Stopped { get; set; }

        #endregion
    }
}
