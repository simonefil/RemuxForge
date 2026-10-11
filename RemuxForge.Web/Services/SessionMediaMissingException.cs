using System;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Il Matroska temporaneo della sessione dell'editor non c'è più, o non è mai stato creato per un sorgente non MKV: gli
    /// endpoint multimediali rispondono 409 con il messaggio localizzato e l'editor lo mostra una volta sola
    /// </summary>
    public class SessionMediaMissingException : InvalidOperationException
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="message">Messaggio localizzato, con l'invito a chiudere e riaprire l'editor</param>
        public SessionMediaMissingException(string message) : base(message)
        {
        }

        #endregion
    }
}
