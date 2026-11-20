namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Tipo di una battuta da tradurre, letto alla preparazione e congelato nel job
    /// </summary>
    public enum SubtitleCueKind
    {
        /// <summary>Battuta parlata</summary>
        Dialogue,

        /// <summary>Testo di una canzone: sigla, sigla finale, canzone di scena</summary>
        Song,

        /// <summary>Scritta sullo schermo: cartello, titolo, didascalia</summary>
        Sign
    }
}
