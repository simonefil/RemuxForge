namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Tipo di chiamata al modello della traduzione
    /// </summary>
    public enum TranslationCallKind
    {
        /// <summary>Riconoscimento del tipo degli stili e delle tratte SRT</summary>
        A0,

        /// <summary>Analisi dei file e proposta del glossario</summary>
        A1,

        /// <summary>Fusione dei glossari parziali in conflitto</summary>
        A2,

        /// <summary>Traduzione di un blocco</summary>
        B,

        /// <summary>Ritentativo delle battute respinte di un blocco</summary>
        BRetry,

        /// <summary>Blocco ridotto dopo una risposta troncata</summary>
        BReduced,

        /// <summary>Diagnosi della revisione</summary>
        D1
    }

    /// <summary>
    /// Regole dei tipi di chiamata
    /// </summary>
    public static class TranslationCallKindExtensions
    {
        #region Metodi pubblici

        /// <summary>
        /// Restituisce il tipo della chiave di cache: ritentativo e blocco ridotto condividono la chiave del blocco
        /// </summary>
        /// <param name="kind">Tipo di chiamata</param>
        /// <returns>A0, A1, A2, B o D1</returns>
        public static string GetSessionKind(this TranslationCallKind kind)
        {
            if (kind == TranslationCallKind.BRetry || kind == TranslationCallKind.BReduced)
                return TranslationCallKind.B.ToString();

            return kind.ToString();
        }

        #endregion
    }
}
