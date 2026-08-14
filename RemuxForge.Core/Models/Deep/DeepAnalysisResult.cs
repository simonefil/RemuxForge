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
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome del backend visuale usato per l'estrazione dei descriptor e il matching
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
        /// Timeline globale source con le ancore usate per l'allineamento
        /// </summary>
        public DeepSiftAnchorTimeline SourceTimeline { get; set; }

        /// <summary>
        /// Timeline globale language con le ancore usate per l'allineamento
        /// </summary>
        public DeepSiftAnchorTimeline LanguageTimeline { get; set; }

        /// <summary>
        /// Geometria video source usata per determinare il crop e riscrivere il canvas dei sottotitoli
        /// </summary>
        public FrameSyncGeometryInfo SourceGeometry { get; set; }

        /// <summary>
        /// Geometria video language usata per determinare il crop e riscrivere il canvas dei sottotitoli
        /// </summary>
        public FrameSyncGeometryInfo LanguageGeometry { get; set; }

        /// <summary>
        /// Risultati dei confronti SIFT prodotti esclusivamente per le coppie pianificate dalla pipeline temporale
        /// </summary>
        public DeepSiftBatchMatchResult BatchMatching { get; set; }

        /// <summary>
        /// Evidenza temporale globale con percorso monotono e margine rispetto alla seconda alternativa
        /// </summary>
        public DeepSiftTemporalEvidenceResult Alignment { get; set; }

        /// <summary>
        /// Mappa di modifica con esito fail-closed della localizzazione dei boundary
        /// </summary>
        public DeepSiftEditMapResult EditMapResult { get; set; }

        /// <summary>
        /// Tempo totale di esecuzione della pipeline in millisecondi
        /// </summary>
        public long TotalElapsedMs { get; set; }

        #endregion
    }
}
