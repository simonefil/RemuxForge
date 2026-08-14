using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Tipo di timeline che contiene il segmento extra osservato
    /// </summary>
    public enum DeepSiftGapType
    {
        /// <summary>
        /// Nessuna timeline contiene un segmento extra
        /// </summary>
        None = 0,

        /// <summary>
        /// Il segmento extra appartiene alla timeline source rispetto al confronto
        /// </summary>
        Source = 1,

        /// <summary>
        /// Il segmento extra appartiene alla timeline language rispetto al confronto
        /// </summary>
        Language = 2
    }

    /// <summary>
    /// Lato della timeline comune usato per localizzare il boundary
    /// </summary>
    public enum DeepSiftTimelineSide
    {
        /// <summary>
        /// Nessun lato comune risolto
        /// </summary>
        None = 0,

        /// <summary>
        /// Il boundary è espresso sulla timeline source
        /// </summary>
        Source = 1,

        /// <summary>
        /// Il boundary è espresso sulla timeline language
        /// </summary>
        Language = 2
    }

    /// <summary>
    /// Metodo esclusivo usato per determinare il boundary operativo
    /// </summary>
    public enum DeepSiftBoundaryRefinementMethod
    {
        /// <summary>
        /// Boundary conservato sul primo frame SIFT del regime successivo
        /// </summary>
        SiftFrame = 0,

        /// <summary>
        /// Boundary derivato da una coppia coerente di black run
        /// </summary>
        PairedBlackRun = 1,

        /// <summary>
        /// Boundary derivato proiettando la black run del lato extra
        /// </summary>
        ProjectedExtraBlackRun = 2,

        /// <summary>
        /// Boundary derivato dall'intersezione con una black run del lato comune
        /// </summary>
        ContainedBlackRun = 3
    }

    /// <summary>
    /// Risultato della localizzazione dei boundary tramite il percorso SIFT globale
    /// </summary>
    /// <remarks>
    /// Contiene la mappa di editing, le diagnosi dei plateau e dei boundary e le metriche del percorso
    /// </remarks>
    public class DeepSiftEditMapResult
    {
        #region Costruttore

        /// <summary>
        /// Inizializza il risultato con la mappa e le raccolte diagnostiche vuote
        /// </summary>
        public DeepSiftEditMapResult()
        {
            this.EditMap = new EditMap();
            this.Plateaus = new List<DeepSiftPlateauDiagnostic>();
            this.Boundaries = new List<DeepSiftBoundaryResult>();
            this.RejectReason = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Mappa pronta per riallineare la timeline language alla timeline source
        /// </summary>
        public EditMap EditMap { get; set; }

        /// <summary>
        /// Diagnostica dei plateau derivati dai regimi del percorso temporale canonico
        /// </summary>
        public List<DeepSiftPlateauDiagnostic> Plateaus { get; set; }

        /// <summary>
        /// Diagnostica ordinata dei gap e dei boundary localizzati
        /// </summary>
        public List<DeepSiftBoundaryResult> Boundaries { get; set; }

        /// <summary>
        /// Indica se tutti i cambi di fascia sono stati localizzati
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Motivo del rifiuto fail-closed, vuoto quando la mappa è completa
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Tempo impiegato per estrarre i frame source nei corridoi locali, in millisecondi
        /// </summary>
        public long LocalSourceExtractionMs { get; set; }

        /// <summary>
        /// Tempo impiegato per estrarre i frame language nei corridoi locali, in millisecondi
        /// </summary>
        public long LocalLanguageExtractionMs { get; set; }

        /// <summary>
        /// Tempo impiegato dal matching SIFT nei corridoi locali, in millisecondi
        /// </summary>
        public long LocalMatchingMs { get; set; }

        /// <summary>
        /// Tempo complessivo per la selezione delle black run, la disambiguazione e la costruzione dell'EditMap, in millisecondi
        /// </summary>
        public long TotalElapsedMs { get; set; }

        /// <summary>
        /// Numero di gap registrati nei percorsi locali
        /// </summary>
        public int GapCount { get; set; }

        /// <summary>
        /// Numero di transizioni convertite in operazioni di editing
        /// </summary>
        public int OperationCount { get; set; }

        #endregion
    }

    /// <summary>
    /// Risultato della localizzazione del boundary per un singolo cambio di offset
    /// </summary>
    public class DeepSiftBoundaryResult
    {
        /// <summary>
        /// Inizializza il risultato senza gap e senza timeline comune selezionata
        /// </summary>
        public DeepSiftBoundaryResult()
        {
            this.GapType = DeepSiftGapType.None;
            this.CommonTimeline = DeepSiftTimelineSide.None;
        }

        /// <summary>
        /// Operazione di editing prodotta dalla transizione risolta
        /// </summary>
        public EditOperation Operation { get; set; }

        /// <summary>
        /// Tipo di gap rilevato nel percorso globale
        /// </summary>
        public DeepSiftGapType GapType { get; set; }

        /// <summary>
        /// Lato della timeline comune usato per determinare il boundary
        /// </summary>
        public DeepSiftTimelineSide CommonTimeline { get; set; }

        /// <summary>
        /// Numero di candidati rilevati nel corridoio comune
        /// </summary>
        public int CandidateCount { get; set; }

        /// <summary>
        /// Numero di coppie di black run coerenti con i due regimi osservati
        /// </summary>
        public int PairedCandidateCount { get; set; }

        /// <summary>
        /// Metodo usato per localizzare e raffinare il boundary
        /// </summary>
        public DeepSiftBoundaryRefinementMethod RefinementMethod { get; set; }

        /// <summary>
        /// Numero di match locali accettati prima del boundary
        /// </summary>
        public int AcceptedBeforeMatches { get; set; }

        /// <summary>
        /// Numero di match locali accettati dopo il boundary
        /// </summary>
        public int AcceptedAfterMatches { get; set; }

        /// <summary>
        /// PTS di inizio del corridoio sul lato comune, in millisecondi
        /// </summary>
        public double CommonCorridorStartMs { get; set; }

        /// <summary>
        /// PTS di fine del corridoio sul lato comune, in millisecondi
        /// </summary>
        public double CommonCorridorEndMs { get; set; }

        /// <summary>
        /// PTS di inizio del corridoio sul lato extra, in millisecondi
        /// </summary>
        public double ExtraCorridorStartMs { get; set; }

        /// <summary>
        /// PTS di fine del corridoio sul lato extra, in millisecondi
        /// </summary>
        public double ExtraCorridorEndMs { get; set; }

        /// <summary>
        /// Offset del plateau precedente al boundary, in millisecondi
        /// </summary>
        public double PreviousOffsetMs { get; set; }

        /// <summary>
        /// Offset del plateau successivo al boundary, in millisecondi
        /// </summary>
        public double NextOffsetMs { get; set; }

        /// <summary>
        /// PTS dell'ultimo frame del regime precedente sul lato comune, in millisecondi
        /// </summary>
        public double LastPreviousCommonFramePtsMs { get; set; }

        /// <summary>
        /// PTS del primo frame appartenente al regime successivo sul lato comune, in millisecondi
        /// </summary>
        public double FirstNextCommonFramePtsMs { get; set; }

        /// <summary>
        /// Boundary scelto sul lato comune per l'operazione di montaggio, in millisecondi
        /// </summary>
        public double SelectedCommonBoundaryMs { get; set; }
    }

    /// <summary>
    /// Diagnostica di un plateau temporale ottenuto da un regime del percorso canonico
    /// </summary>
    public class DeepSiftPlateauDiagnostic
    {
        /// <summary>
        /// Primo indice del percorso appartenente al regime del plateau
        /// </summary>
        public int FirstMatchIndex { get; set; }

        /// <summary>
        /// Ultimo indice del percorso appartenente al regime del plateau
        /// </summary>
        public int LastMatchIndex { get; set; }

        /// <summary>
        /// Offset mediano normalizzato sulla timeline source, calcolato sui PTS
        /// </summary>
        public double OffsetMs { get; set; }

        /// <summary>
        /// Dispersione assoluta mediana degli offset, in millisecondi
        /// </summary>
        public double OffsetDispersionMs { get; set; }

        /// <summary>
        /// PTS source del primo frame sostenuto dal plateau, in millisecondi
        /// </summary>
        public double SourceStartPtsMs { get; set; }

        /// <summary>
        /// PTS source dell'ultimo frame sostenuto dal plateau, in millisecondi
        /// </summary>
        public double SourceEndPtsMs { get; set; }

        /// <summary>
        /// Numero di match SIFT sostenuti dal plateau
        /// </summary>
        public int MatchCount { get; set; }

        /// <summary>
        /// Tolleranza temporale derivata dai frame effettivi del plateau, in millisecondi
        /// </summary>
        public double FrameToleranceMs { get; set; }
    }
}
