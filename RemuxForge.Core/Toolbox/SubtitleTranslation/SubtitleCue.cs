namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Battuta di un sottotitolo da tradurre: tempi, testo pulito per il modello, ricetta per ricomporre il campo Text e
    /// posizione del campo nel testo decodificato del file
    /// </summary>
    public class SubtitleCue
    {
        #region Costanti

        /// <summary>Battuta senza testo, o con soli tag</summary>
        public const string EXCLUSION_EMPTY = "empty";

        /// <summary>Campo Effect non vuoto, nei progetti senza tabella degli stili</summary>
        public const string EXCLUSION_EFFECT = "effect";

        /// <summary>Stile o tratta SRT riconosciuti come testo che non è nella lingua d'origine</summary>
        public const string EXCLUSION_NOT_SOURCE_LANGUAGE = "not-source-language";

        /// <summary>Drawing vettoriale</summary>
        public const string EXCLUSION_DRAWING = "drawing";

        /// <summary>Karaoke, nei progetti senza tabella degli stili</summary>
        public const string EXCLUSION_KARAOKE = "karaoke";

        /// <summary>Effetto a tempo a metà frase (scritta a comparsa), nei progetti senza tabella degli stili</summary>
        public const string EXCLUSION_TIMED_EFFECT = "timed-effect";

        /// <summary>Graffa aperta e mai chiusa</summary>
        public const string EXCLUSION_UNCLOSED_BRACE = "unclosed-brace";

        /// <summary>Tratto formattato vuoto o di soli spazi, nei progetti senza tabella degli stili</summary>
        public const string EXCLUSION_WHITESPACE_SPAN = "whitespace-span";

        /// <summary>Blocco ASS in un SRT dopo l'inizio del testo, nei progetti senza tabella degli stili</summary>
        public const string EXCLUSION_SRT_INLINE_ASS_TAG = "srt-inline-ass-tag";

        /// <summary>Stesso carattere scritto crudo e come entità, o con due entità diverse</summary>
        public const string EXCLUSION_MIXED_ENTITIES = "mixed-entities";

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="id">Id della battuta traducibile, null per esclusa o copia</param>
        /// <param name="position">Ordine nel file, da 1</param>
        /// <param name="startMs">Inizio in millisecondi</param>
        /// <param name="endMs">Fine in millisecondi</param>
        /// <param name="cleanText">Testo pulito, vuoto per una battuta esclusa</param>
        /// <param name="recipe">Ricetta di ricomposizione, null per una battuta esclusa</param>
        /// <param name="style">Stile ASS/SSA, vuoto in SRT</param>
        /// <param name="kind">Tipo della battuta</param>
        /// <param name="layer">Layer ASS, null in SRT e SSA</param>
        /// <param name="exclusionReason">Motivo per cui la battuta resta al codice, vuoto se si traduce</param>
        /// <param name="duplicateOf">Id della battuta di cui questa è la copia su un altro layer</param>
        /// <param name="textStart">Inizio del campo Text nel testo decodificato</param>
        /// <param name="textLength">Lunghezza del campo Text nel testo decodificato</param>
        public SubtitleCue(int? id, int position, long startMs, long endMs, string cleanText, CueRecipe recipe, string style, SubtitleCueKind kind,
            int? layer, string exclusionReason, int? duplicateOf, int textStart, int textLength)
        {
            this.Id = id;
            this.Position = position;
            this.StartMs = startMs;
            this.EndMs = endMs;
            this.CleanText = cleanText ?? "";
            this.Recipe = recipe;
            this.Style = style ?? "";
            this.Kind = kind;
            this.Layer = layer;
            this.ExclusionReason = exclusionReason ?? "";
            this.DuplicateOf = duplicateOf;
            this.TextStart = textStart;
            this.TextLength = textLength;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Confronto in ordine di tempo: inizio, poi ordine nel file a pari inizio
        /// </summary>
        /// <param name="a">Prima battuta</param>
        /// <param name="b">Seconda battuta</param>
        /// <returns>Negativo se a viene prima di b</returns>
        public static int CompareByTime(SubtitleCue a, SubtitleCue b)
        {
            return a.StartMs != b.StartMs ? a.StartMs.CompareTo(b.StartMs) : a.Position.CompareTo(b.Position);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Id progressivo da 1 sulle sole battute traducibili, null per le escluse e per le copie
        /// </summary>
        public int? Id { get; }

        /// <summary>
        /// Ordine nel file di tutte le battute, da 1
        /// </summary>
        public int Position { get; }

        /// <summary>
        /// Inizio in millisecondi
        /// </summary>
        public long StartMs { get; }

        /// <summary>
        /// Fine in millisecondi
        /// </summary>
        public long EndMs { get; }

        /// <summary>
        /// Testo che vede il modello: a capo come \n e tratti formattati a metà frase come ⟦n testo⟧
        /// </summary>
        public string CleanText { get; }

        /// <summary>
        /// Tutto ciò che il modello non vede, per rimettere il campo Text nel formato d'origine
        /// </summary>
        public CueRecipe Recipe { get; }

        /// <summary>
        /// Stile ASS/SSA, vuoto in SRT
        /// </summary>
        public string Style { get; }

        /// <summary>
        /// Tipo della battuta: in ASS/SSA quello riconosciuto per lo stile, in SRT quello della tratta riconosciuta o canzone se il
        /// testo ha ♪ o ♫
        /// </summary>
        public SubtitleCueKind Kind { get; }

        /// <summary>
        /// Layer ASS, null in SRT e SSA
        /// </summary>
        public int? Layer { get; }

        /// <summary>
        /// Gruppo di frase spezzata, null fuori dai gruppi
        /// </summary>
        public int? Group { get; internal set; }

        /// <summary>
        /// Motivo per cui la battuta resta al codice, vuoto se si traduce
        /// </summary>
        public string ExclusionReason { get; }

        /// <summary>
        /// Id della battuta di cui questa è la copia su un altro layer: ne prende la traduzione con la propria ricetta
        /// </summary>
        public int? DuplicateOf { get; }

        /// <summary>
        /// Inizio del campo Text nel testo decodificato
        /// </summary>
        public int TextStart { get; }

        /// <summary>
        /// Lunghezza del campo Text nel testo decodificato
        /// </summary>
        public int TextLength { get; }

        #endregion
    }
}
