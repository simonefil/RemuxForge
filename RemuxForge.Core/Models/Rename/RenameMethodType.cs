namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Tipi metodo disponibili per Advanced Rename
    /// </summary>
    public enum RenameMethodType
    {
        /// <summary>Trova e sostituisce testo nel nome file</summary>
        Replace,

        /// <summary>Inserisce testo in una posizione specifica</summary>
        Add,

        /// <summary>Rimuove caratteri per posizione o pattern</summary>
        Remove,

        /// <summary>Cambia maiuscole/minuscole del nome file</summary>
        NewCase,

        /// <summary>Sostituisce il nome file con un pattern a tag</summary>
        NewName,

        /// <summary>Rimuove caratteri dai bordi del nome file</summary>
        Trim
    }
}
