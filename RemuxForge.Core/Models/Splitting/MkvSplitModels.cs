using System.Collections.Generic;
using System;

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
            this.EncodeQuality = null;
            this.EncodeSpeed = null;
            this.Force = false;
            this.DryRun = false;
        }

        #endregion

        #region Proprietà

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
        /// Qualità della ricodifica (CRF o quantizzatore, secondo il codec), null per il default del codec
        /// </summary>
        public int? EncodeQuality { get; set; }

        /// <summary>
        /// Velocità della ricodifica (preset o cpu-used, secondo il codec), null per il default del codec
        /// </summary>
        public string EncodeSpeed { get; set; }

        /// <summary>
        /// Sovrascrive output esistenti
        /// </summary>
        public bool Force { get; set; }

        /// <summary>
        /// Stampa i segmenti senza eseguire lo split
        /// </summary>
        public bool DryRun { get; set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Copia indipendente delle opzioni, per un lavoro che non deve vedere modifiche successive
        /// </summary>
        /// <returns>Opzioni con gli stessi valori: sono tutti valori o stringhe, la copia superficiale basta</returns>
        public MkvSplitOptions Clone()
        {
            return (MkvSplitOptions)this.MemberwiseClone();
        }

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
    /// Codec video della tabella delle capacità di taglio
    /// </summary>
    public enum MkvSplitCodec
    {
        /// <summary>HEVC / H.265</summary>
        Hevc,

        /// <summary>AVC / H.264</summary>
        H264,

        /// <summary>MPEG-2 Video</summary>
        Mpeg2,

        /// <summary>MPEG-4 ASP (Xvid/DivX)</summary>
        Mpeg4,

        /// <summary>DivX 3 (MS-MPEG4v3)</summary>
        MsMpeg4v3,

        /// <summary>MS-MPEG4v2</summary>
        MsMpeg4v2,

        /// <summary>H.263</summary>
        H263,

        /// <summary>AV1</summary>
        Av1,

        /// <summary>VP9</summary>
        Vp9,

        /// <summary>VC-1</summary>
        Vc1
    }

    /// <summary>
    /// Metodo con cui un codec si taglia al fotogramma
    /// </summary>
    public enum MkvSplitExactCutMethod
    {
        /// <summary>Nessuno: solo taglio al keyframe</summary>
        None,

        /// <summary>Testa ricodificata e corpo copiato a livello di byte dal flusso grezzo</summary>
        Bytes,

        /// <summary>Testa e coda ricodificate e corpo copiato selezionando i pacchetti per PTS</summary>
        Packets
    }

    /// <summary>
    /// Capacità di taglio di un codec video e parametri della sua ricodifica
    /// </summary>
    public sealed class MkvSplitCodecCapability
    {
        #region Proprietà

        /// <summary>Nome del codec come lo riporta ffprobe</summary>
        public string CodecName { get; set; }

        /// <summary>Nome mostrato all'utente</summary>
        public string DisplayName { get; set; }

        /// <summary>Codec canonico</summary>
        public MkvSplitCodec Codec { get; set; }

        /// <summary>Metodo del taglio al fotogramma, None se il codec si taglia solo al keyframe</summary>
        public MkvSplitExactCutMethod Method { get; set; }

        /// <summary>Encoder ffmpeg della ricodifica, null senza taglio al fotogramma</summary>
        public string Encoder { get; set; }

        /// <summary>Nome tecnico del parametro di qualità (CRF o q)</summary>
        public string QualityName { get; set; }

        /// <summary>Qualità predefinita</summary>
        public int QualityDefault { get; set; }

        /// <summary>Qualità minima ammessa</summary>
        public int QualityMin { get; set; }

        /// <summary>Qualità massima ammessa</summary>
        public int QualityMax { get; set; }

        /// <summary>Nome tecnico del parametro di velocità (preset o cpu-used), null se l'encoder non lo prevede</summary>
        public string SpeedName { get; set; }

        /// <summary>Velocità predefinita, null se l'encoder non la prevede</summary>
        public string SpeedDefault { get; set; }

        /// <summary>Velocità ammesse per nome, null quando la velocità è un numero fra SpeedMin e SpeedMax</summary>
        public string[] SpeedValues { get; set; }

        /// <summary>Velocità numerica minima</summary>
        public int SpeedMin { get; set; }

        /// <summary>Velocità numerica massima</summary>
        public int SpeedMax { get; set; }

        #endregion
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
        public List<ChapterMark> Chapters { get; set; }

        /// <summary>Nome file output, relativo alla cartella di output</summary>
        public string File { get; set; }

        /// <summary>True se il primo frame del segmento è un keyframe</summary>
        public bool StartsOnKeyframe { get; set; }

        /// <summary>Frame che andranno ricodificati per aprire il segmento</summary>
        public int ReencodeFrames { get; set; }

        /// <summary>Inizio chiesto prima dello snap al keyframe, null se lo snap non l'ha spostato</summary>
        public int? RequestedStartFrame { get; set; }

        /// <summary>Fine chiesta prima dello snap al keyframe, null se lo snap non l'ha spostata</summary>
        public int? RequestedEndFrame { get; set; }

        /// <summary>Stato del file di output su disco</summary>
        public MkvSplitOutputState OutputState { get; set; }

        /// <summary>
        /// Durata in secondi dei pacchetti dei B iniziali in testa alla parte che inizia su un keyframe di GOP aperto (D25):
        /// la parte parte dal primo di loro e il keyframe arriva dopo, quindi l'audio FLAC tagliato a parte da ffmpeg si sposta
        /// della stessa durata. Zero negli altri casi
        /// </summary>
        public double OpenGopHeadSeconds { get; set; }

        /// <summary>Costruttore</summary>
        public MkvSplitSegment()
        {
            this.Chapters = new List<ChapterMark>();
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

        /// <summary>True se keyframe di GOP aperto: fotogrammi decodificati dopo si mostrano prima, quindi un taglio senza ricodifica li sposterebbe</summary>
        public bool OpenGop { get; set; }

        /// <summary>
        /// Keyframe di GOP aperto: numero dei fotogrammi che lo precedono subito in presentazione e si decodificano dopo di lui
        /// (B iniziali). Zero se il keyframe è chiuso o se il GOP è aperto in un altro modo. Un taglio senza ricodifica li
        /// toglie dalla parte che finisce sul keyframe e li lascia come pacchetti in testa a quella che ci inizia (D25)
        /// </summary>
        public int LeadingFrames { get; set; }

        /// <summary>
        /// Keyframe MPEG-2 di GOP aperto con i B iniziali e closed_gop=1 nell'header GOP: i B iniziali sono predetti solo
        /// all'indietro e si decodificano anche senza il GOP precedente. Il flag non dice se il GOP è aperto, lo dicono
        /// OpenGop e LeadingFrames: dice solo se i B iniziali già rilevati si possono mostrare (§11 (o))
        /// </summary>
        public bool LeadingDecodable { get; set; }
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

        /// <summary>Fourcc della traccia Video for Windows, null se la traccia è nativa</summary>
        public string CodecTag { get; set; }

        /// <summary>Larghezza in pixel</summary>
        public int Width { get; set; }

        /// <summary>Altezza in pixel</summary>
        public int Height { get; set; }
    }

    /// <summary>
    /// Record operativo batch/single split
    /// </summary>
    public class MkvSplitRecord
    {
        /// <summary>Identità stabile durante refresh del medesimo sorgente invariato.</summary>
        public Guid RecordId { get; set; } = Guid.NewGuid();

        /// <summary>Proiezione informativa del montaggio applicato; l'orchestrator ne possiede l'autorità.</summary>
        public MkvSplitTimelineProjection MontageProjection { get; set; }

        public List<MkvSplitOutputExecutionResult> OutputResults { get; set; } = new List<MkvSplitOutputExecutionResult>();
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
        /// <summary>Exit code della pipeline</summary>
        public int ExitCode { get; set; }

        /// <summary>Messaggio errore sintetico</summary>
        public string ErrorMessage { get; set; }

        /// <summary>Costruttore</summary>
        public MkvSplitExecutionResult()
        {
            this.ExitCode = 0;
            this.ErrorMessage = "";
        }
    }
}
