using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Parte di un'unione: file intero letto con tracce, durata e capitoli
    /// </summary>
    public class MkvSplitJoinPart
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvSplitJoinPart()
        {
            this.Identity = new MkvSplitSourceIdentity();
            this.DurationNs = 0;
            this.Tracks = new List<TrackInfo>();
            this.Chapters = new List<ChapterMark>();
            this.ChapterWarnings = new List<string>();
            this.Error = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Identità del file al momento della lettura: l'esecuzione la ricontrolla
        /// </summary>
        public MkvSplitSourceIdentity Identity { get; set; }

        /// <summary>
        /// Percorso completo del file
        /// </summary>
        public string FilePath
        {
            get { return this.Identity.FullPath; }
        }

        /// <summary>
        /// Durata del contenitore in nanosecondi: è lo spostamento che mkvmerge applica alla parte successiva
        /// </summary>
        public long DurationNs { get; set; }

        /// <summary>
        /// Tracce del file
        /// </summary>
        public List<TrackInfo> Tracks { get; set; }

        /// <summary>
        /// Capitoli della parte, nel suo tempo
        /// </summary>
        public List<ChapterMark> Chapters { get; set; }

        /// <summary>
        /// Avvisi localizzati sui capitoli non riportati
        /// </summary>
        public List<string> ChapterWarnings { get; set; }

        /// <summary>
        /// Errore localizzato di lettura, vuoto se la parte è utilizzabile
        /// </summary>
        public string Error { get; set; }

        #endregion
    }

    /// <summary>
    /// Piano di un'unione: parti in ordine, capitoli risultanti e motivi di blocco
    /// </summary>
    public class MkvSplitJoinPlan
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvSplitJoinPlan()
        {
            this.Parts = new List<MkvSplitJoinPart>();
            this.Chapters = new List<ChapterMark>();
            this.Issues = new List<string>();
            this.TotalDurationNs = 0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Parti nell'ordine di unione
        /// </summary>
        public List<MkvSplitJoinPart> Parts { get; set; }

        /// <summary>
        /// Capitoli concatenati nel tempo dell'output
        /// </summary>
        public List<ChapterMark> Chapters { get; set; }

        /// <summary>
        /// Motivi localizzati che bloccano l'unione, uno per parte e traccia
        /// </summary>
        public List<string> Issues { get; set; }

        /// <summary>
        /// Durata totale prevista in nanosecondi
        /// </summary>
        public long TotalDurationNs { get; set; }

        /// <summary>
        /// Vero se l'unione si può eseguire
        /// </summary>
        public bool IsValid
        {
            get { return this.Issues.Count == 0 && this.Parts.Count >= 2; }
        }

        #endregion
    }
}
