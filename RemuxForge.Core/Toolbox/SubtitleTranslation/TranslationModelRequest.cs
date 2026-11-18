using RemuxForge.Core.Models;
using System;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Chiamata della traduzione inviata al provider del modello
    /// </summary>
    public class TranslationModelRequest
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public TranslationModelRequest()
        {
            this.Kind = TranslationCallKind.B;
            this.ProjectId = "";
            this.FileId = TranslationSessionKey.NO_FILE;
            this.Request = new AiResponseRequest();
            this.PrefixReused = false;
            this.AttemptFinished = null;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Tipo di chiamata
        /// </summary>
        public TranslationCallKind Kind { get; set; }

        /// <summary>
        /// Identificativo del progetto, parte della chiave di cache
        /// </summary>
        public string ProjectId { get; set; }

        /// <summary>
        /// Identificativo del file, TranslationSessionKey.NO_FILE per le chiamate su più file
        /// </summary>
        public string FileId { get; set; }

        /// <summary>
        /// Richiesta da mandare; session_id e prompt_cache_key li imposta il provider
        /// </summary>
        public AiResponseRequest Request { get; set; }

        /// <summary>
        /// True se il piano del runner prevede altre richieste della stessa sessione dopo questa, che rileggono il primo messaggio.
        /// Quante richieste avrà una sessione lo sa solo il runner, che pianifica blocchi e tratte; come si mette in cache la parte
        /// fissa lo sa solo il provider, che conosce il servizio. Il runner lo dice qui, il provider decide se e come prepararla
        /// </summary>
        public bool PrefixReused { get; set; }

        /// <summary>
        /// Avviso a ogni tentativo di trasporto arrivato al servizio, riuscito o fallito; null se non serve
        /// </summary>
        public Action<TranslationTransportAttempt> AttemptFinished { get; set; }

        #endregion
    }
}
