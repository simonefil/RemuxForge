using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// EditMap prodotta dal percorso SIFT globale e diagnostica dei boundary
    /// </summary>
    public class DeepSiftEditMapResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepSiftEditMapResult()
        {
            this.EditMap = new EditMap();
            this.Plateaus = new List<DeepSiftPlateauDiagnostic>();
            this.Boundaries = new List<DeepSiftBoundaryResult>();
            this.RejectReason = "";
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Mappa pronta per riallineare la timeline language al source
        /// </summary>
        public EditMap EditMap { get; set; }

        /// <summary>
        /// Plateau derivati direttamente dalle run MATCH del traceback
        /// </summary>
        public List<DeepSiftPlateauDiagnostic> Plateaus { get; set; }

        /// <summary>
        /// Diagnostica ordinata dei gap e dei boundary
        /// </summary>
        public List<DeepSiftBoundaryResult> Boundaries { get; set; }

        /// <summary>
        /// True soltanto quando tutti i cambi fascia sono stati localizzati
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Motivo del rifiuto fail-closed, vuoto quando la mappa è completa
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Tempo di estrazione dei frame nei corridoi locali
        /// </summary>
        public long LocalSourceExtractionMs { get; set; }

        /// <summary>
        /// Tempo di estrazione dei frame language nei corridoi locali
        /// </summary>
        public long LocalLanguageExtractionMs { get; set; }

        /// <summary>
        /// Tempo SIFT dei corridoi locali
        /// </summary>
        public long LocalMatchingMs { get; set; }

        /// <summary>
        /// Tempo complessivo per selezione black-run, disambiguazione e costruzione EditMap
        /// </summary>
        public long TotalElapsedMs { get; set; }

        /// <summary>
        /// Numero di gap temporali attraversati dal traceback
        /// </summary>
        public int GapCount { get; set; }

        /// <summary>
        /// Numero di gap convertiti in operazioni
        /// </summary>
        public int OperationCount { get; set; }

        #endregion
    }

    /// <summary>
    /// Boundary scelto per un singolo cambio di offset
    /// </summary>
    public class DeepSiftBoundaryResult
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepSiftBoundaryResult()
        {
            this.GapType = "";
            this.CommonTimeline = "";
            this.RejectReason = "";
        }

        /// <summary>
        /// Operazione prodotta, null quando il gap è replacement o non rappresentabile
        /// </summary>
        public EditOperation Operation { get; set; }

        /// <summary>
        /// Tipo di gap globale
        /// </summary>
        public string GapType { get; set; }

        /// <summary>
        /// Lato comune usato per il boundary
        /// </summary>
        public string CommonTimeline { get; set; }

        /// <summary>
        /// Numero di candidate nel corridoio comune
        /// </summary>
        public int CandidateCount { get; set; }

        /// <summary>
        /// Candidate univoche dopo pairing old/new
        /// </summary>
        public int PairedCandidateCount { get; set; }

        /// <summary>
        /// True quando il boundary è stato trovato al frame
        /// </summary>
        public bool FrameRefined { get; set; }

        /// <summary>
        /// True quando una black-run è stata usata come landmark di conferma
        /// </summary>
        public bool BlackRunPaired { get; set; }

        /// <summary>
        /// Match locali accettati prima del boundary
        /// </summary>
        public int AcceptedBeforeMatches { get; set; }

        /// <summary>
        /// Match locali accettati dopo il boundary
        /// </summary>
        public int AcceptedAfterMatches { get; set; }

        /// <summary>
        /// Limiti PTS del corridoio common-side
        /// </summary>
        public double CommonCorridorStartMs { get; set; }

        /// <summary>
        /// Fine del corridoio PTS common-side
        /// </summary>
        public double CommonCorridorEndMs { get; set; }

        /// <summary>
        /// Limiti PTS del corridoio extra-side
        /// </summary>
        public double ExtraCorridorStartMs { get; set; }

        /// <summary>
        /// Fine del corridoio PTS extra-side
        /// </summary>
        public double ExtraCorridorEndMs { get; set; }

        /// <summary>
        /// Offset dei plateau ai lati del boundary
        /// </summary>
        public double PreviousOffsetMs { get; set; }

        /// <summary>
        /// Offset del plateau successivo al boundary
        /// </summary>
        public double NextOffsetMs { get; set; }

        /// <summary>
        /// Ultimo frame old, primo frame new e boundary operativo common-side
        /// </summary>
        public double LastOldCommonFramePtsMs { get; set; }

        /// <summary>
        /// PTS del primo frame common-side appartenente al nuovo plateau
        /// </summary>
        public double FirstNewCommonFramePtsMs { get; set; }

        /// <summary>
        /// Boundary common-side scelto per l'operazione di montaggio
        /// </summary>
        public double SelectedCommonBoundaryMs { get; set; }

        /// <summary>
        /// Motivo del rifiuto del boundary
        /// </summary>
        public string RejectReason { get; set; }
    }

    /// <summary>
    /// Plateau temporale ottenuto da una singola run MATCH del traceback
    /// </summary>
    public class DeepSiftPlateauDiagnostic
    {
        /// <summary>
        /// Primo indice match
        /// </summary>
        public int FirstMatchIndex { get; set; }

        /// <summary>
        /// Ultimo indice match
        /// </summary>
        public int LastMatchIndex { get; set; }

        /// <summary>
        /// Offset mediano PTS-aware
        /// </summary>
        public double OffsetMs { get; set; }

        /// <summary>
        /// Dispersione assoluta mediana degli offset
        /// </summary>
        public double OffsetDispersionMs { get; set; }

        /// <summary>
        /// Primo PTS source
        /// </summary>
        public double SourceStartPtsMs { get; set; }

        /// <summary>
        /// Ultimo PTS source
        /// </summary>
        public double SourceEndPtsMs { get; set; }

        /// <summary>
        /// Numero di match SIFT
        /// </summary>
        public int MatchCount { get; set; }

        /// <summary>
        /// Tolleranza temporale derivata dai frame reali del plateau
        /// </summary>
        public double FrameToleranceMs { get; set; }

        /// <summary>
        /// Stato esplicito della verifica del plateau
        /// </summary>
        public bool Accepted { get; set; }
    }
}
