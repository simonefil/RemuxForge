using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Copertura del sorgente prodotta dalla modalità di taglio
    /// </summary>
    public enum MkvSplitCoverage
    {
        /// <summary>Il sorgente viene ripartito senza perdite</summary>
        Partition,

        /// <summary>Vengono estratti solo alcuni intervalli, il resto viene scartato</summary>
        Extract
    }

    /// <summary>
    /// Categoria di un avviso del piano
    /// </summary>
    public enum MkvSplitWarningKind
    {
        /// <summary>Un intervallo è stato riportato dentro i limiti del file</summary>
        RangeClamped,

        /// <summary>Due o più intervalli si sovrappongono</summary>
        RangeOverlap,

        /// <summary>Due segmenti generano lo stesso nome di output</summary>
        NameCollision,

        /// <summary>Il file di output esiste già</summary>
        OutputExists,

        /// <summary>Il segmento richiede la ricodifica di alcuni frame</summary>
        Reencode,

        /// <summary>Lo snap annullerebbe il segmento e non è stato applicato</summary>
        SnapEatSegment,

        /// <summary>Il confine richiesto non ha keyframe nella direzione scelta</summary>
        SnapNoKeyframe,

        /// <summary>Avviso sulla divisione dei capitoli in blocchi</summary>
        ChapterGrouping
    }

    /// <summary>
    /// Avviso raccolto durante la costruzione del piano
    /// </summary>
    public class MkvSplitWarning
    {
        /// <summary>Categoria dell'avviso</summary>
        public MkvSplitWarningKind Kind { get; set; }

        /// <summary>Testo localizzato dell'avviso</summary>
        public string Message { get; set; }

        /// <summary>Numero del segmento coinvolto, 0 se l'avviso riguarda il file</summary>
        public int SegmentNum { get; set; }

        /// <summary>Costruttore</summary>
        public MkvSplitWarning()
        {
            this.Message = "";
        }

        /// <summary>
        /// Costruttore con valori
        /// </summary>
        /// <param name="kind">Categoria dell'avviso</param>
        /// <param name="message">Testo localizzato</param>
        /// <param name="segmentNum">Numero del segmento coinvolto, 0 per avvisi di file</param>
        public MkvSplitWarning(MkvSplitWarningKind kind, string message, int segmentNum)
        {
            this.Kind = kind;
            this.Message = message;
            this.SegmentNum = segmentNum;
        }
    }

    /// <summary>
    /// Stato su disco del file di output di un segmento
    /// </summary>
    public enum MkvSplitOutputState
    {
        /// <summary>Il file non esiste ancora</summary>
        New,

        /// <summary>Il file esiste e verrà saltato</summary>
        ExistsSkip,

        /// <summary>Il file esiste e verrà sovrascritto</summary>
        ExistsOverwrite
    }

    /// <summary>
    /// Segmento costruito a mano nell'editor: sopravvive alla riesecuzione dell'analisi
    /// e sostituisce quelli che la configurazione globale produrrebbe
    /// </summary>
    public class MkvSplitOverrideSegment
    {
        /// <summary>Primo frame del segmento</summary>
        public int StartFrame { get; set; }

        /// <summary>Numero di frame del segmento</summary>
        public int FrameCount { get; set; }

        /// <summary>True quando il segmento resta sulla timeline ma non viene prodotto</summary>
        public bool Excluded { get; set; }
    }

    /// <summary>
    /// Piano di taglio calcolato per un singolo file
    /// </summary>
    public class MkvSplitPlan
    {
        #region Costruttore

        /// <summary>Costruttore</summary>
        public MkvSplitPlan()
        {
            this.InputFile = "";
            this.OutputDir = "";
            this.ErrorMessage = "";
            this.Chapters = new List<MkvSplitChapter>();
            this.Segments = new List<MkvSplitSegment>();
            this.Warnings = new List<MkvSplitWarning>();
            this.SourcePts = new double[0];
            this.KeyframeIndexes = new int[0];
            this.Mode = MkvSplitMode.Ranges;
            this.Coverage = MkvSplitCoverage.Extract;
            this.Snap = MkvSplitSnapMode.Off;
            this.FrameRateMode = MkvSplitFrameRateMode.Unknown;
            this.IsValid = false;
        }

        #endregion

        #region Proprietà

        /// <summary>File sorgente del piano</summary>
        public string InputFile { get; set; }

        /// <summary>Cartella di output risolta</summary>
        public string OutputDir { get; set; }

        /// <summary>Capitoli del sorgente</summary>
        public List<MkvSplitChapter> Chapters { get; set; }

        /// <summary>Durata del sorgente in secondi</summary>
        public double Duration { get; set; }

        /// <summary>Numero di frame del sorgente</summary>
        public int FrameCount { get; set; }

        /// <summary>PTS del sorgente, condivisi con l'esecuzione e con l'editor</summary>
        public double[] SourcePts { get; set; }

        /// <summary>Modalità di taglio effettivamente applicata</summary>
        public MkvSplitMode Mode { get; set; }

        /// <summary>Copertura del sorgente</summary>
        public MkvSplitCoverage Coverage { get; set; }

        /// <summary>Strategia di snap richiesta</summary>
        public MkvSplitSnapMode Snap { get; set; }

        /// <summary>Modalità frame rate rilevata</summary>
        public MkvSplitFrameRateMode FrameRateMode { get; set; }

        /// <summary>Parametri video del sorgente</summary>
        public MkvSplitVideoParams VideoParams { get; set; }

        /// <summary>Segmenti previsti</summary>
        public List<MkvSplitSegment> Segments { get; set; }

        /// <summary>Avvisi raccolti durante la costruzione</summary>
        public List<MkvSplitWarning> Warnings { get; set; }

        /// <summary>True se il piano è eseguibile</summary>
        public bool IsValid { get; set; }

        /// <summary>Motivo per cui il piano non è eseguibile</summary>
        public string ErrorMessage { get; set; }

        /// <summary>Frame del sorgente che nessun segmento include</summary>
        public int DiscardedFrames { get; set; }

        /// <summary>Frame totali che verranno ricodificati</summary>
        public int TotalReencodeFrames { get; set; }

        /// <summary>True se lo split userà il percorso veloce senza ricodifica</summary>
        public bool UsesFastPath { get; set; }

        /// <summary>Indici dei frame che sono keyframe, in ordine di presentazione</summary>
        public int[] KeyframeIndexes { get; set; }

        /// <summary>True quando i segmenti arrivano dall'editor invece che dalla configurazione globale</summary>
        public bool IsOverride { get; set; }

        #endregion
    }
}
