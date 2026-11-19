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

        /// <summary>Riga ASS/SSA o tratto di tratta SRT riconosciuti come testo da non tradurre</summary>
        public const string EXCLUSION_NOT_TRANSLATABLE = "not-translatable";

        /// <summary>Drawing vettoriale</summary>
        public const string EXCLUSION_DRAWING = "drawing";

        /// <summary>Graffa aperta e mai chiusa</summary>
        public const string EXCLUSION_UNCLOSED_BRACE = "unclosed-brace";

        /// <summary>Stesso carattere scritto crudo e come entità, o con due entità diverse</summary>
        public const string EXCLUSION_MIXED_ENTITIES = "mixed-entities";

        /// <summary>Riga di una frase animata (lettere, pezzi, fotogrammi, evidenziazioni) che mostra solo una parte della sua riga intera:
        /// se ne va quando la frase è tradotta, e al posto delle righe disegnate da una riga Comment va la riga Comment tradotta</summary>
        public const string EXCLUSION_ANIMATED_PIECE = "animated-piece";

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
        /// <param name="duplicateOf">Id della battuta di cui questa è la copia: su un altro layer, o un'altra riga intera della stessa frase rivelata a pezzi</param>
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
        /// Motivo per cui la battuta resta al codice, vuoto se si traduce
        /// </summary>
        public string ExclusionReason { get; }

        /// <summary>
        /// Id della battuta di cui questa è la copia, su un altro layer o come altra riga della stessa frase rivelata a pezzi: ne
        /// prende la traduzione con la propria ricetta
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
