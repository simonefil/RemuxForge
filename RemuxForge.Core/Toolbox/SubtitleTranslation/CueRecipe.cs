using System.Collections.Generic;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Ricetta di ricomposizione di una battuta: tutto ciò che il modello non vede. Le stringhe «Original» rifanno il campo Text
    /// esatto quando il testo non cambia, le altre servono al testo tradotto
    /// </summary>
    /// <param name="Prefix">Gettoni e spazi prima del primo carattere di testo che non aprono il segno 1</param>
    /// <param name="Suffix">Gettoni e spazi dopo l'ultimo carattere di testo che non chiudono l'ultimo segno</param>
    /// <param name="LineWrap">Coppia che avvolge ogni riga, null se le righe non sono avvolte una per una</param>
    /// <param name="PrefixLinePart">Tag di riga del blocco che apre il segno 1, scritti in testa quando il segno 1 non resta all'inizio</param>
    /// <param name="Marks">Segni dei tratti formattati, indice 0 = segno 1</param>
    /// <param name="LineBreaks">A capo originali in ordine, con i tag che li avvolgono</param>
    /// <param name="LineBreakToken">A capo per il testo tradotto</param>
    /// <param name="HardSpaceOffsets">Posizioni nel testo pulito degli spazi unificatori che erano \h</param>
    /// <param name="Entities">Carattere e forma di entità del sorgente (solo SRT)</param>
    /// <param name="Detached">Blocchi a metà frase che non cambiano la formattazione (commenti, tag di riga); in una battuta posizionale
    /// tutti i blocchi a metà frase</param>
    /// <param name="TabOffsets">Posizioni nel testo pulito dei tab del sorgente, resi come spazi</param>
    /// <param name="Segmented">Battuta posizionale: niente segni, e i blocchi staccati si ridistribuiscono sul testo tradotto</param>
    public sealed record CueRecipe(string Prefix, string Suffix, LineWrap LineWrap, string PrefixLinePart, IReadOnlyList<MarkRecipe> Marks,
        IReadOnlyList<string> LineBreaks, string LineBreakToken, IReadOnlyList<int> HardSpaceOffsets, IReadOnlyDictionary<char, string> Entities,
        IReadOnlyList<DetachedBlock> Detached, IReadOnlyList<int> TabOffsets, bool Segmented);

    /// <summary>
    /// Coppia di tag che avvolge ogni riga della battuta: Close e Open si scrivono attorno a ogni a capo del testo tradotto
    /// </summary>
    /// <param name="Open">Apertura scritta dopo ogni a capo</param>
    /// <param name="Close">Chiusura scritta prima di ogni a capo</param>
    public sealed record LineWrap(string Open, string Close);

    /// <summary>
    /// Segno di un tratto formattato a metà frase
    /// </summary>
    /// <param name="Number">Numero del segno, da 1</param>
    /// <param name="Open">Apertura dal testo base per il testo tradotto: in SRT tutti i tag che valgono sul tratto</param>
    /// <param name="Close">Chiusura verso il testo base per il testo tradotto, ricavata dall'apertura quando il sorgente non la ha</param>
    /// <param name="OriginalOpen">Gettoni originali al posto di «⟦n »; dopo un tratto adiacente, il passaggio da quel tratto</param>
    /// <param name="OriginalClose">Gettoni originali al posto di «⟧», vuoti se il tratto arriva in fondo o al tratto seguente</param>
    /// <param name="PerLine">Il tratto si chiude a fine riga e si riapre a ogni riga</param>
    /// <param name="InnerLeading">Spazi fra l'apertura e il testo, che nel testo pulito stanno prima del segno</param>
    /// <param name="InnerTrailing">Spazi fra il testo e la chiusura, che nel testo pulito stanno dopo il segno</param>
    public sealed record MarkRecipe(int Number, string Open, string Close, string OriginalOpen, string OriginalClose, bool PerLine,
        string InnerLeading, string InnerTrailing);

    /// <summary>
    /// Blocco a metà frase che non cambia la formattazione: resta al suo posto se il testo non cambia, va prima del suffisso
    /// nel testo tradotto perché non si vede. In una battuta posizionale il blocco va alla stessa frazione del testo tradotto
    /// </summary>
    /// <param name="CleanOffset">Posizione nel testo pulito</param>
    /// <param name="Raw">Blocco originale</param>
    public sealed record DetachedBlock(int CleanOffset, string Raw);
}
