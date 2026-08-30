using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Opzioni specifiche della modalità split
    /// </summary>
    public class MkvSplitOptions
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MkvSplitOptions()
        {
            this.InputFile = "";
            this.InputFolder = "";
            this.SourcePath = "";
            this.OutputDir = "";
            this.Pattern = "";
            this.Ranges = "";
            this.SplitAt = "";
            this.TrimStart = "";
            this.TrimEnd = "";
            this.ChaptersEach = false;
            this.ChaptersPerEpisode = 0;
            this.Manual = false;
            this.OutputTemplate = "";
            this.StartNumber = 1;
            this.Snap = MkvSplitSnapMode.Nearest;
            this.Force = false;
            this.Batch = false;
            this.DryRun = false;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// File MKV di input in modalità singolo file
        /// </summary>
        public string InputFile { get; set; }

        /// <summary>
        /// Cartella input in modalità batch
        /// </summary>
        public string InputFolder { get; set; }

        /// <summary>
        /// Path sorgente ricevuto da CLI/UI, file o cartella
        /// </summary>
        public string SourcePath { get; set; }

        /// <summary>
        /// Directory output
        /// </summary>
        public string OutputDir { get; set; }

        /// <summary>
        /// Pattern capitoli per episodio
        /// </summary>
        public string Pattern { get; set; }

        /// <summary>
        /// Range espliciti
        /// </summary>
        public string Ranges { get; set; }

        /// <summary>
        /// Punti di split
        /// </summary>
        public string SplitAt { get; set; }

        /// <summary>
        /// Trim iniziale
        /// </summary>
        public string TrimStart { get; set; }

        /// <summary>
        /// Trim finale
        /// </summary>
        public string TrimEnd { get; set; }

        /// <summary>
        /// Un segmento per capitolo
        /// </summary>
        public bool ChaptersEach { get; set; }

        /// <summary>
        /// Numero di capitoli per episodio, 0 quando la modalità non è in uso
        /// </summary>
        public int ChaptersPerEpisode { get; set; }

        /// <summary>
        /// True quando i segmenti si costruiscono nell'editor invece che dalla configurazione
        /// </summary>
        public bool Manual { get; set; }

        /// <summary>
        /// Template custom per nomi output
        /// </summary>
        public string OutputTemplate { get; set; }

        /// <summary>
        /// Numero da cui parte la numerazione degli episodi
        /// </summary>
        public int StartNumber { get; set; }

        /// <summary>
        /// Strategia snap su keyframe
        /// </summary>
        public MkvSplitSnapMode Snap { get; set; }

        /// <summary>
        /// Sovrascrive output esistenti
        /// </summary>
        public bool Force { get; set; }

        /// <summary>
        /// True quando l'input è una cartella batch
        /// </summary>
        public bool Batch { get; set; }

        /// <summary>
        /// Stampa i segmenti senza eseguire lo split
        /// </summary>
        public bool DryRun { get; set; }

        #endregion
    }

    /// <summary>
    /// Strategia di snap dello start segmento su keyframe
    /// </summary>
    public enum MkvSplitSnapMode
    {
        /// <summary>Nessuno snap</summary>
        Off,

        /// <summary>Keyframe precedente</summary>
        Before,

        /// <summary>Keyframe successivo</summary>
        After,

        /// <summary>Keyframe più vicino</summary>
        Nearest
    }

    /// <summary>
    /// Modalità di costruzione segmenti
    /// </summary>
    public enum MkvSplitMode
    {
        /// <summary>Pattern capitoli</summary>
        Pattern,

        /// <summary>Range espliciti</summary>
        Ranges,

        /// <summary>Trim singolo</summary>
        Trim,

        /// <summary>Taglio in uno o più punti: i segmenti coprono tutto il file</summary>
        SplitAt,

        /// <summary>Un segmento per capitolo</summary>
        ChaptersEach,

        /// <summary>Blocchi di k capitoli per episodio</summary>
        ChaptersPerEpisode,

        /// <summary>Segmenti costruiti a mano nell'editor</summary>
        Manual
    }

    /// <summary>
    /// Codec video supportato dalla pipeline slow
    /// </summary>
    public enum MkvSplitCodec
    {
        /// <summary>HEVC / H.265</summary>
        Hevc,

        /// <summary>AVC / H.264</summary>
        H264,

        /// <summary>MPEG-2 Video</summary>
        Mpeg2
    }

    /// <summary>
    /// Modalità frame rate rilevata
    /// </summary>
    public enum MkvSplitFrameRateMode
    {
        /// <summary>Non determinabile</summary>
        Unknown,

        /// <summary>Constant Frame Rate</summary>
        Cfr,

        /// <summary>Variable Frame Rate</summary>
        Vfr
    }

    /// <summary>
    /// Stato operativo di un record split
    /// </summary>
    public enum MkvSplitStatus
    {
        /// <summary>Scansionato, non ancora analizzato</summary>
        Pending,

        /// <summary>Analisi del piano in corso</summary>
        Analyzing,

        /// <summary>Piano costruito e valido</summary>
        Planned,

        /// <summary>Piano costruito ma non eseguibile</summary>
        PlanInvalid,

        /// <summary>Segmenti ancora da definire nell'editor</summary>
        Undefined,

        /// <summary>Split in corso</summary>
        Running,

        /// <summary>Split completato</summary>
        Done,

        /// <summary>Split fallito</summary>
        Error,

        /// <summary>Interrotto su richiesta</summary>
        Stopped,

        /// <summary>Escluso dall'elaborazione</summary>
        Skipped
    }

    /// <summary>
    /// Capitolo estratto dal sorgente
    /// </summary>
    public class MkvSplitChapter
    {
        /// <summary>Timestamp in secondi</summary>
        public double Timestamp { get; set; }

        /// <summary>Timestamp originale</summary>
        public string TsStr { get; set; }

        /// <summary>Nome capitolo</summary>
        public string Name { get; set; }
    }

    /// <summary>
    /// Segmento output
    /// </summary>
    public class MkvSplitSegment
    {
        /// <summary>Numero progressivo 1-based</summary>
        public int Num { get; set; }

        /// <summary>Numero episodio in pattern mode</summary>
        public int Episode { get; set; }

        /// <summary>Timestamp inizio in secondi</summary>
        public double StartTs { get; set; }

        /// <summary>Timestamp fine esclusivo in secondi</summary>
        public double EndTs { get; set; }

        /// <summary>Primo frame</summary>
        public int StartFrame { get; set; }

        /// <summary>Numero frame</summary>
        public int FrameCount { get; set; }

        /// <summary>Capitoli contenuti</summary>
        public List<MkvSplitChapter> Chapters { get; set; }

        /// <summary>Nome file output, relativo alla cartella di output</summary>
        public string File { get; set; }

        /// <summary>True se il primo frame del segmento è un keyframe</summary>
        public bool StartsOnKeyframe { get; set; }

        /// <summary>Frame che andranno ricodificati per aprire il segmento</summary>
        public int ReencodeFrames { get; set; }

        /// <summary>Stato del file di output su disco</summary>
        public MkvSplitOutputState OutputState { get; set; }

        /// <summary>Costruttore</summary>
        public MkvSplitSegment()
        {
            this.Chapters = new List<MkvSplitChapter>();
            this.File = "";
        }
    }

    /// <summary>
    /// Info packet video
    /// </summary>
    public struct MkvSplitFrameInfo
    {
        /// <summary>Offset byte</summary>
        public long Pos { get; set; }

        /// <summary>Dimensione byte</summary>
        public int Size { get; set; }

        /// <summary>True se keyframe</summary>
        public bool Key { get; set; }
    }

    /// <summary>
    /// Parametri video letti via ffprobe
    /// </summary>
    public class MkvSplitVideoParams
    {
        /// <summary>Codec canonico</summary>
        public string CodecName { get; set; }

        /// <summary>Pixel format</summary>
        public string PixFmt { get; set; }

        /// <summary>Color space</summary>
        public string ColorSpace { get; set; }

        /// <summary>Color primaries</summary>
        public string ColorPrimaries { get; set; }

        /// <summary>Color transfer</summary>
        public string ColorTransfer { get; set; }

        /// <summary>Color range</summary>
        public string ColorRange { get; set; }
    }

    /// <summary>
    /// Record operativo batch/single split
    /// </summary>
    public class MkvSplitRecord
    {
        /// <summary>File input</summary>
        public string InputFile { get; set; }

        /// <summary>Stato corrente del record</summary>
        public MkvSplitStatus Status { get; set; }

        /// <summary>Messaggio errore</summary>
        public string ErrorMessage { get; set; }

        /// <summary>Segmenti previsti o prodotti</summary>
        public List<MkvSplitSegment> Segments { get; set; }

        /// <summary>True se completato con successo</summary>
        public bool Success { get; set; }

        /// <summary>Piano di taglio calcolato, null finché il file non è stato analizzato</summary>
        public MkvSplitPlan Plan { get; set; }

        /// <summary>Dimensione del sorgente in byte</summary>
        public long SourceSize { get; set; }

        /// <summary>Info di contenitore e tracce lette allo scan, null se la lettura è fallita</summary>
        public MkvFileInfo SourceInfo { get; set; }

        /// <summary>True se il record è escluso dalle operazioni</summary>
        public bool Skipped { get; set; }

        /// <summary>True quando i segmenti del file arrivano dall'editor invece che dalla configurazione globale</summary>
        public bool IsOverride { get; set; }

        /// <summary>Costruttore</summary>
        public MkvSplitRecord()
        {
            this.InputFile = "";
            this.Status = MkvSplitStatus.Pending;
            this.ErrorMessage = "";
            this.Segments = new List<MkvSplitSegment>();
            this.Success = false;
        }
    }

    /// <summary>
    /// Risultato di esecuzione split su un singolo file
    /// </summary>
    public class MkvSplitExecutionResult
    {
        /// <summary>File input elaborato</summary>
        public string InputFile { get; set; }

        /// <summary>Exit code della pipeline</summary>
        public int ExitCode { get; set; }

        /// <summary>Messaggio errore sintetico</summary>
        public string ErrorMessage { get; set; }

        /// <summary>Segmenti previsti o prodotti</summary>
        public List<MkvSplitSegment> Segments { get; set; }

        /// <summary>Costruttore</summary>
        public MkvSplitExecutionResult()
        {
            this.InputFile = "";
            this.ExitCode = 0;
            this.ErrorMessage = "";
            this.Segments = new List<MkvSplitSegment>();
        }
    }
}
