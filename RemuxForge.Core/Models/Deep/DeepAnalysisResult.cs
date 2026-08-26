using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Stato strutturato prodotto dalla pipeline Deep Analysis
    /// </summary>
    public enum DeepAnalysisStatus
    {
        /// <summary>
        /// Analisi non ancora avviata o priva di un esito
        /// </summary>
        NotStarted = 0,

        /// <summary>
        /// Analisi completata con un risultato accettato
        /// </summary>
        Accepted = 1,

        /// <summary>
        /// Analisi completata con un rifiuto fail-closed
        /// </summary>
        Rejected = 2,

        /// <summary>
        /// Analisi interrotta prima di produrre un esito accettato o rifiutato
        /// </summary>
        Cancelled = 3
    }

    /// <summary>
    /// Geometria di normalizzazione scelta per una delle due copie
    /// </summary>
    public class DeepAnalysisGeometry
    {
        #region Costruttore

        /// <summary>
        /// Inizializza la geometria neutra
        /// </summary>
        public DeepAnalysisGeometry()
        {
            this.CropPx = "";
            this.Zoom = 1.0;
            this.Mode = "independent_viewport";
            this.ViewportRight = 1.0;
            this.ViewportBottom = 1.0;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Crop manuale in pixel applicato prima della normalizzazione
        /// </summary>
        public string CropPx { get; set; }

        /// <summary>
        /// Frazione del quadrato centrale conservata dal viewport dHash
        /// </summary>
        public double Zoom { get; set; }

        /// <summary>
        /// Traslazione verticale del viewport dHash come frazione del lato
        /// </summary>
        public double VerticalShift { get; set; }

        /// <summary>
        /// Fotogrammi indicizzati con questa geometria
        /// </summary>
        public int FrameCount { get; set; }

        /// <summary>
        /// Contratto geometrico usato dall'indicizzazione dHash
        /// </summary>
        public string Mode { get; set; }

        /// <summary>
        /// Estremo sinistro del viewport nell'area attiva
        /// </summary>
        public double ViewportLeft { get; set; }

        /// <summary>
        /// Estremo superiore del viewport nell'area attiva
        /// </summary>
        public double ViewportTop { get; set; }

        /// <summary>
        /// Estremo destro del viewport nell'area attiva
        /// </summary>
        public double ViewportRight { get; set; }

        /// <summary>
        /// Estremo inferiore del viewport nell'area attiva
        /// </summary>
        public double ViewportBottom { get; set; }

        #endregion
    }

    /// <summary>
    /// Un tratto a offset costante fra due operazioni
    /// </summary>
    public class DeepAnalysisPlateau
    {
        #region Proprietà

        /// <summary>
        /// Inizio del tratto nella timeline source
        /// </summary>
        public double StartMs { get; set; }

        /// <summary>
        /// Fine del tratto nella timeline source
        /// </summary>
        public double EndMs { get; set; }

        /// <summary>
        /// Offset costante del tratto
        /// </summary>
        public double OffsetMs { get; set; }

        #endregion
    }

    /// <summary>
    /// Una singola operazione con la provenienza del suo confine e l'esito dei filtri
    /// </summary>
    public class DeepAnalysisOperationDiagnostic
    {
        #region Costruttore

        /// <summary>
        /// Inizializza la diagnostica con testi vuoti
        /// </summary>
        public DeepAnalysisOperationDiagnostic()
        {
            this.Type = "";
            this.BoundaryDecidedBy = "";
            this.RejectReason = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Tipo di operazione nella nomenclatura della EditMap
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// Confine nella timeline source
        /// </summary>
        public double SourceTimestampMs { get; set; }

        /// <summary>
        /// Durata del materiale tolto o aggiunto, nella timeline source
        /// </summary>
        public double DurationMs { get; set; }

        /// <summary>
        /// Offset del pianoro precedente
        /// </summary>
        public double OffsetBeforeMs { get; set; }

        /// <summary>
        /// Offset del pianoro successivo
        /// </summary>
        public double OffsetAfterMs { get; set; }

        /// <summary>
        /// Larghezza della cima piatta con cui sono stati misurati i due offset
        /// </summary>
        public double UncertaintyMs { get; set; }

        /// <summary>
        /// Chi ha deciso la posizione finale del confine
        /// </summary>
        public string BoundaryDecidedBy { get; set; }

        /// <summary>
        /// Filtro che ha scartato l'operazione, vuoto quando è stata accettata
        /// </summary>
        public string RejectReason { get; set; }

        #endregion
    }

    /// <summary>
    /// Risultato diagnostico completo prodotto dalla pipeline DeepAnalysis
    /// </summary>
    public class DeepAnalysisResult
    {
        #region Costruttore

        /// <summary>
        /// Inizializza un risultato con stato non avviato e valori testuali vuoti
        /// </summary>
        public DeepAnalysisResult()
        {
            this.BackendName = "";
            this.Status = DeepAnalysisStatus.NotStarted;
            this.RejectReason = "";
            this.StretchFactor = "";
            this.SourceToLanguageScale = 1.0;
            this.Source = new DeepAnalysisGeometry();
            this.Language = new DeepAnalysisGeometry();
            this.GeometryAlignment = new VisualGeometryAlignment();
            this.Plateaus = new List<DeepAnalysisPlateau>();
            this.Operations = new List<DeepAnalysisOperationDiagnostic>();
            this.RejectedOperations = new List<DeepAnalysisOperationDiagnostic>();
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome del backend visuale usato per le misure di hash
        /// </summary>
        public string BackendName { get; set; }

        /// <summary>
        /// Stato strutturato corrente della pipeline di analisi
        /// </summary>
        [JsonIgnore]
        public DeepAnalysisStatus Status { get; set; }

        /// <summary>
        /// Rappresentazione testuale stabile dello stato esposta nel contratto diagnostico JSON
        /// </summary>
        [JsonPropertyName("Status")]
        public string SerializedStatus { get { return this.Status.ToString(); } }

        /// <summary>
        /// Motivo del rifiuto fail-closed, quando presente
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Fattore di stretch logico normalizzato, vuoto quando non serve alcuna deformazione temporale
        /// </summary>
        public string StretchFactor { get; set; }

        /// <summary>
        /// Fattore di scala usato per proiettare i tempi dalla timeline source alla timeline language
        /// </summary>
        public double SourceToLanguageScale { get; set; }

        /// <summary>
        /// Geometria di normalizzazione della sorgente
        /// </summary>
        public DeepAnalysisGeometry Source { get; set; }

        /// <summary>
        /// Geometria di normalizzazione della copia doppiata
        /// </summary>
        public DeepAnalysisGeometry Language { get; set; }

        /// <summary>
        /// Frazione di fotogrammi che si corrispondono con la geometria scelta
        /// </summary>
        public double GeometryMatchRate { get; set; }

        /// <summary>
        /// Distanza di Hamming mediana fra le due copie alla geometria scelta
        /// </summary>
        public double GeometryMedianDistance { get; set; }

        /// <summary>
        /// Geometria video source usata per determinare il crop e riscrivere il canvas dei sottotitoli
        /// </summary>
        public FrameSyncGeometryInfo SourceGeometry { get; set; }

        /// <summary>
        /// Geometria video language usata per determinare il crop e riscrivere il canvas dei sottotitoli
        /// </summary>
        public FrameSyncGeometryInfo LanguageGeometry { get; set; }

        /// <summary>
        /// Trasformazione globale dall'area attiva language all'area attiva source
        /// </summary>
        public VisualGeometryAlignment GeometryAlignment { get; set; }

        /// <summary>
        /// Offset del primo tratto, ancorato sulla copertura complessiva
        /// </summary>
        public double InitialOffsetMs { get; set; }

        /// <summary>
        /// Frazione del film che resta agganciata applicando la EditMap prodotta
        /// </summary>
        public double Coverage { get; set; }

        /// <summary>
        /// Tratti a offset costante fra un'operazione e la successiva
        /// </summary>
        public List<DeepAnalysisPlateau> Plateaus { get; set; }

        /// <summary>
        /// Operazioni accettate, con la provenienza del confine
        /// </summary>
        public List<DeepAnalysisOperationDiagnostic> Operations { get; set; }

        /// <summary>
        /// Operazioni scartate, con il filtro che le ha respinte
        /// </summary>
        public List<DeepAnalysisOperationDiagnostic> RejectedOperations { get; set; }

        /// <summary>
        /// Tempo totale di esecuzione della pipeline in millisecondi
        /// </summary>
        public long TotalElapsedMs { get; set; }

        /// <summary>
        /// Picco del working set del processo osservato durante la run
        /// </summary>
        public long PeakWorkingSetBytes { get; set; }

        /// <summary>
        /// Directory persistente creata prima dell'avvio della run
        /// </summary>
        public string RunDirectory { get; set; }

        #endregion
    }
}
