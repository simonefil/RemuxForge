namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Stato di elaborazione di un file nel pipeline
    /// </summary>
    public enum FileStatus
    {
        /// <summary>File in attesa</summary>
        Pending,
        /// <summary>Analisi in corso</summary>
        Analyzing,
        /// <summary>Analisi completata</summary>
        Analyzed,
        /// <summary>Elaborazione in corso</summary>
        Processing,
        /// <summary>Codifica in corso</summary>
        Encoding,
        /// <summary>Elaborazione completata</summary>
        Done,
        /// <summary>Elaborazione fallita</summary>
        Error,
        /// <summary>File escluso dall'elaborazione</summary>
        Skipped
    }
}
