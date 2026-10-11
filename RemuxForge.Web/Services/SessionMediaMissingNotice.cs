using Radzen;
using RemuxForge.Core.Localization;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Notifica del temporaneo della sessione mancante per un editor aperto: anteprime e immagini audio falliscono tutte con
    /// la stessa risposta 409, e l'utente la vede una sola volta, con l'invito a chiudere e riaprire l'editor
    /// </summary>
    public class SessionMediaMissingNotice
    {
        #region Variabili di classe

        /// <summary>
        /// True dopo la prima notifica della sessione dell'editor
        /// </summary>
        private bool _notified;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Notifica il messaggio del server, solo la prima volta
        /// </summary>
        /// <param name="notifications">Servizio delle notifiche</param>
        /// <param name="message">Messaggio localizzato della risposta 409</param>
        public void Notify(NotificationService notifications, string message)
        {
            if (this._notified)
                return;
            this._notified = true;
            notifications.Notify(NotificationSeverity.Error, AppText.T("web.editor.sessionMediaMissingTitle"), message, 8000);
        }

        /// <summary>
        /// Riconosce nell'errore JavaScript di un'anteprima la risposta 409 e la notifica, solo la prima volta
        /// </summary>
        /// <param name="notifications">Servizio delle notifiche</param>
        /// <param name="error">Messaggio dell'errore JavaScript</param>
        /// <returns>True se l'errore è il temporaneo mancante, che il chiamante non deve notificare di nuovo</returns>
        public bool HandleClientError(NotificationService notifications, string error)
        {
            if (!SessionMediaMissingException.TryReadClientError(error, out string message))
                return false;
            this.Notify(notifications, message);
            return true;
        }

        #endregion
    }
}
