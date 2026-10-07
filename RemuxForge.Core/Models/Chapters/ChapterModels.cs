using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Capitolo dell'edizione piatta: inizio, fine facoltativa e un titolo
    /// </summary>
    public class ChapterMark
    {
        #region Costanti

        /// <summary>
        /// Valore di EndNs per un capitolo senza fine dichiarata
        /// </summary>
        public const long NO_END = -1;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public ChapterMark()
        {
            this.StartNs = 0;
            this.EndNs = NO_END;
            this.Name = "";
            this.Language = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Inizio in nanosecondi
        /// </summary>
        public long StartNs { get; set; }

        /// <summary>
        /// Fine in nanosecondi, NO_END se non dichiarata
        /// </summary>
        public long EndNs { get; set; }

        /// <summary>
        /// Titolo
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Lingua del titolo (ISO 639-2), vuota se non dichiarata
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// Inizio in secondi
        /// </summary>
        public double StartSeconds
        {
            get { return this.StartNs / 1000000000.0; }
        }

        /// <summary>
        /// Vero se la fine e' dichiarata
        /// </summary>
        public bool HasEnd
        {
            get { return this.EndNs != NO_END; }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Copia indipendente del capitolo
        /// </summary>
        /// <returns>Copia</returns>
        public ChapterMark Clone()
        {
            return new ChapterMark { StartNs = this.StartNs, EndNs = this.EndNs, Name = this.Name, Language = this.Language };
        }

        #endregion
    }

    /// <summary>
    /// Esito della lettura dei capitoli di un file
    /// </summary>
    public class ChapterReadResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public ChapterReadResult()
        {
            this.Chapters = new List<ChapterMark>();
            this.Warnings = new List<string>();
            this.Error = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Capitoli dell'edizione scelta, ordinati per inizio
        /// </summary>
        public List<ChapterMark> Chapters { get; set; }

        /// <summary>
        /// Avvisi localizzati su quanto non riportato (edizioni ulteriori, capitoli annidati)
        /// </summary>
        public List<string> Warnings { get; set; }

        /// <summary>
        /// Errore localizzato che blocca l'uso dei capitoli, vuoto se la lettura e' utilizzabile
        /// </summary>
        public string Error { get; set; }

        #endregion
    }

    /// <summary>
    /// Destino di un istante trasformato da una mappa temporale
    /// </summary>
    public enum ChapterMapKind
    {
        /// <summary>L'istante esiste nel risultato</summary>
        Mapped,

        /// <summary>L'istante cade in un tratto rimosso: va al punto di giunzione</summary>
        Junction,

        /// <summary>L'istante non esiste nel risultato</summary>
        Drop
    }

    /// <summary>
    /// Istante trasformato da una mappa temporale
    /// </summary>
    public struct ChapterMapResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="kind">Destino</param>
        /// <param name="timeNs">Istante nel risultato in nanosecondi, ignorato per Drop</param>
        public ChapterMapResult(ChapterMapKind kind, long timeNs)
        {
            this.Kind = kind;
            this.TimeNs = timeNs;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Destino
        /// </summary>
        public ChapterMapKind Kind { get; }

        /// <summary>
        /// Istante nel risultato in nanosecondi
        /// </summary>
        public long TimeNs { get; }

        #endregion
    }

    /// <summary>
    /// Tratto di sorgente usato da un montaggio, in nanosecondi semiaperti [StartNs, EndNs)
    /// </summary>
    public struct ChapterRange
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="startNs">Inizio incluso</param>
        /// <param name="endNs">Fine esclusa</param>
        public ChapterRange(long startNs, long endNs)
        {
            this.StartNs = startNs;
            this.EndNs = endNs;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Inizio incluso
        /// </summary>
        public long StartNs { get; }

        /// <summary>
        /// Fine esclusa
        /// </summary>
        public long EndNs { get; }

        #endregion
    }

    /// <summary>
    /// Parte di un'unione: i suoi capitoli e la durata del contenitore
    /// </summary>
    public class ChapterPart
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public ChapterPart()
        {
            this.Chapters = new List<ChapterMark>();
            this.DurationNs = 0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Capitoli della parte, nel suo tempo
        /// </summary>
        public List<ChapterMark> Chapters { get; set; }

        /// <summary>
        /// Durata del contenitore della parte: e' lo spostamento che mkvmerge applica alla parte successiva
        /// </summary>
        public long DurationNs { get; set; }

        #endregion
    }
}
