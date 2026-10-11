using System;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Il Matroska temporaneo della sessione dell'editor non c'è più, o non è mai stato creato per un sorgente non MKV: gli
    /// endpoint multimediali rispondono 409 con il messaggio localizzato e l'editor lo mostra una volta sola
    /// </summary>
    public class SessionMediaMissingException : InvalidOperationException
    {
        #region Costanti

        /// <summary>
        /// Prefisso con cui il client marca l'errore di una risposta 409, seguito dal messaggio del server
        /// </summary>
        public const string CLIENT_MARKER = "session-media-missing:";

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="message">Messaggio localizzato, con l'invito a chiudere e riaprire l'editor</param>
        public SessionMediaMissingException(string message) : base(message)
        {
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Riconosce nel testo di un errore JavaScript la risposta 409 di un endpoint multimediale
        /// </summary>
        /// <param name="text">Messaggio dell'errore JavaScript, eventualmente seguito dallo stack</param>
        /// <param name="message">Messaggio del server, vuoto se il testo non è di una risposta 409</param>
        /// <returns>True se il temporaneo della sessione manca</returns>
        public static bool TryReadClientError(string text, out string message)
        {
            int start = text == null ? -1 : text.IndexOf(CLIENT_MARKER, StringComparison.Ordinal);
            message = "";
            if (start < 0)
                return false;
            start += CLIENT_MARKER.Length;
            int end = text.IndexOf('\n', start);
            message = (end < 0 ? text.Substring(start) : text.Substring(start, end - start)).Trim();
            return true;
        }

        #endregion
    }
}
