namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Livello di severità di un messaggio di log
    /// </summary>
    public enum LogLevel
    {
        /// <summary>
        /// Operazione fallita
        /// </summary>
        Error,

        /// <summary>
        /// Problema recuperabile
        /// </summary>
        Warning,

        /// <summary>
        /// Fallback automatico, anomalia soft
        /// </summary>
        Notice,

        /// <summary>
        /// Completamento con successo
        /// </summary>
        Success,

        /// <summary>
        /// Inizio fase o sottofase
        /// </summary>
        Phase,

        /// <summary>
        /// Header operazione principale
        /// </summary>
        Header,

        /// <summary>
        /// Informazione rilevante, stato
        /// </summary>
        Info,

        /// <summary>
        /// Dati neutri, righe tabella
        /// </summary>
        Text,

        /// <summary>
        /// Diagnostica tecnica dettagliata
        /// </summary>
        Debug
    }
}
