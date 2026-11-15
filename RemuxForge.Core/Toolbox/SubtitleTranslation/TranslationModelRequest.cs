using RemuxForge.Core.Models;

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

        #endregion
    }
}
