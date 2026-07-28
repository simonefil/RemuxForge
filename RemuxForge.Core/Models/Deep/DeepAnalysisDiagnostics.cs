using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Diagnostica strutturata prodotta dalla DeepAnalysis
    /// </summary>
    public class DeepAnalysisDiagnostics
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepAnalysisDiagnostics()
        {
            this.StretchRatio = 1.0;
            this.InverseRatio = 1.0;
            this.StretchFactor = "";
            this.ManualStretchRequested = "";
            this.InitialAlignment = new DeepAnalysisInitialAlignmentDiagnostic();
            this.TimelineMap = new DeepAnalysisTimelineMapDiagnostic();
            this.Regions = new List<DeepAnalysisRegionDiagnostic>();
            this.Transitions = new List<DeepAnalysisTransitionDiagnostic>();
            this.GlobalVerification = new DeepAnalysisGlobalVerificationDiagnostic();
            this.Timing = new DeepAnalysisTimingDiagnostic();
            this.Performance = new DeepAnalysisPerformanceDiagnostic();
        }

        /// <summary>
        /// Rapporto stretch applicato
        /// </summary>
        public double StretchRatio { get; set; }
        /// <summary>
        /// Rapporto inverso usato per mappare source su language
        /// </summary>
        public double InverseRatio { get; set; }
        /// <summary>
        /// Stretch factor finale in formato testuale
        /// </summary>
        public string StretchFactor { get; set; }
        /// <summary>
        /// Stretch manuale richiesto dall'utente
        /// </summary>
        public string ManualStretchRequested { get; set; }
        /// <summary>
        /// True se l'auto stretch era consentito
        /// </summary>
        public bool AllowAutoStretch { get; set; }
        /// <summary>
        /// True se il candidato audio iniziale era disponibile
        /// </summary>
        public bool AudioInitialAvailable { get; set; }
        /// <summary>
        /// Offset iniziale audio in millisecondi
        /// </summary>
        public int AudioInitialOffsetMs { get; set; }
        /// <summary>
        /// Geometria video source
        /// </summary>
        public FrameSyncGeometryInfo SourceGeometry { get; set; }
        /// <summary>
        /// Geometria video language
        /// </summary>
        public FrameSyncGeometryInfo LanguageGeometry { get; set; }
        /// <summary>
        /// Diagnostica scelta offset iniziale
        /// </summary>
        public DeepAnalysisInitialAlignmentDiagnostic InitialAlignment { get; set; }
        /// <summary>
        /// Diagnostica timeline-first
        /// </summary>
        public DeepAnalysisTimelineMapDiagnostic TimelineMap { get; set; }
        /// <summary>
        /// Regioni offset diagnostiche
        /// </summary>
        public List<DeepAnalysisRegionDiagnostic> Regions { get; set; }
        /// <summary>
        /// Transizioni diagnostiche
        /// </summary>
        public List<DeepAnalysisTransitionDiagnostic> Transitions { get; set; }
        /// <summary>
        /// Verifica globale finale
        /// </summary>
        public DeepAnalysisGlobalVerificationDiagnostic GlobalVerification { get; set; }
        /// <summary>
        /// Timing delle fasi principali
        /// </summary>
        public DeepAnalysisTimingDiagnostic Timing { get; set; }
        /// <summary>
        /// Contatori prestazionali
        /// </summary>
        public DeepAnalysisPerformanceDiagnostic Performance { get; set; }
    }

    /// <summary>
    /// Diagnostica scelta offset iniziale DeepAnalysis
    /// </summary>
    public class DeepAnalysisInitialAlignmentDiagnostic
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepAnalysisInitialAlignmentDiagnostic()
        {
            this.SelectedSource = "";
            this.DecisionReason = "";
        }

        /// <summary>
        /// True se è disponibile un candidato da scene cut
        /// </summary>
        public bool SceneCandidateAvailable { get; set; }
        /// <summary>
        /// Offset candidato da scene cut
        /// </summary>
        public int SceneOffsetMs { get; set; }
        /// <summary>
        /// Voti del candidato scene cut
        /// </summary>
        public int SceneVotes { get; set; }
        /// <summary>
        /// True se è disponibile un candidato audio
        /// </summary>
        public bool AudioCandidateAvailable { get; set; }
        /// <summary>
        /// Offset candidato audio
        /// </summary>
        public int AudioOffsetMs { get; set; }
        /// <summary>
        /// Voti del candidato audio
        /// </summary>
        public int AudioVotes { get; set; }
        /// <summary>
        /// Numero cut testati
        /// </summary>
        public int CutsTested { get; set; }
        /// <summary>
        /// Voti minimi richiesti
        /// </summary>
        public int MinVotesRequired { get; set; }
        /// <summary>
        /// Offset iniziale selezionato
        /// </summary>
        public int SelectedOffsetMs { get; set; }
        /// <summary>
        /// Sorgente del candidato selezionato
        /// </summary>
        public string SelectedSource { get; set; }
        /// <summary>
        /// Motivazione decisione
        /// </summary>
        public string DecisionReason { get; set; }
    }

    /// <summary>
    /// Diagnostica timeline-first DeepAnalysis
    /// </summary>
    public class DeepAnalysisTimelineMapDiagnostic
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepAnalysisTimelineMapDiagnostic()
        {
            this.Status = "";
            this.RejectReason = "";
            this.AnchorMode = "";
            this.TrackLanguage = "";
            this.SourceTrackName = "";
            this.LanguageTrackName = "";
            this.Anchors = new List<DeepAnalysisTimelineAnchorDiagnostic>();
            this.Plateaus = new List<DeepAnalysisTimelinePlateauDiagnostic>();
        }

        /// <summary>
        /// Stato costruzione timeline
        /// </summary>
        public string Status { get; set; }
        /// <summary>
        /// Motivo rifiuto timeline
        /// </summary>
        public string RejectReason { get; set; }
        /// <summary>
        /// Modalità anchor usata
        /// </summary>
        public string AnchorMode { get; set; }
        /// <summary>
        /// Lingua traccia audio comune
        /// </summary>
        public string TrackLanguage { get; set; }
        /// <summary>
        /// Indice stream audio source
        /// </summary>
        public int SourceAudioStreamIndex { get; set; }
        /// <summary>
        /// Indice stream audio language
        /// </summary>
        public int LanguageAudioStreamIndex { get; set; }
        /// <summary>
        /// Nome traccia audio source
        /// </summary>
        public string SourceTrackName { get; set; }
        /// <summary>
        /// Nome traccia audio language
        /// </summary>
        public string LanguageTrackName { get; set; }
        /// <summary>
        /// Numero anchor generati
        /// </summary>
        public int AnchorCount { get; set; }
        /// <summary>
        /// Numero anchor accettati
        /// </summary>
        public int AcceptedAnchorCount { get; set; }
        /// <summary>
        /// Numero plateau prodotti
        /// </summary>
        public int PlateauCount { get; set; }
        /// <summary>
        /// Score medio anchor accettati
        /// </summary>
        public double AverageAcceptedScore { get; set; }
        /// <summary>
        /// Lista anchor diagnostici
        /// </summary>
        public List<DeepAnalysisTimelineAnchorDiagnostic> Anchors { get; set; }
        /// <summary>
        /// Lista plateau diagnostici
        /// </summary>
        public List<DeepAnalysisTimelinePlateauDiagnostic> Plateaus { get; set; }
    }

    /// <summary>
    /// Anchor temporale timeline-first
    /// </summary>
    public class DeepAnalysisTimelineAnchorDiagnostic
    {
        /// <summary>
        /// Centro source dell'anchor
        /// </summary>
        public double SourceCenterSec { get; set; }
        /// <summary>
        /// Offset stimato in millisecondi
        /// </summary>
        public int OffsetMs { get; set; }
        /// <summary>
        /// Score anchor
        /// </summary>
        public double Score { get; set; }
        /// <summary>
        /// Margine tra migliore e secondo candidato
        /// </summary>
        public double Margin { get; set; }
        /// <summary>
        /// True se l'anchor è stato accettato
        /// </summary>
        public bool Accepted { get; set; }
        /// <summary>
        /// Motivo rifiuto anchor
        /// </summary>
        public string RejectReason { get; set; }
    }

    /// <summary>
    /// Plateau di offset costante timeline-first
    /// </summary>
    public class DeepAnalysisTimelinePlateauDiagnostic
    {
        /// <summary>
        /// Indice plateau
        /// </summary>
        public int Index { get; set; }
        /// <summary>
        /// Inizio source plateau
        /// </summary>
        public double StartSrcSec { get; set; }
        /// <summary>
        /// Fine source plateau
        /// </summary>
        public double EndSrcSec { get; set; }
        /// <summary>
        /// Centro del primo anchor affidabile del plateau
        /// </summary>
        public double FirstAnchorSrcSec { get; set; }
        /// <summary>
        /// Centro dell'ultimo anchor affidabile del plateau
        /// </summary>
        public double LastAnchorSrcSec { get; set; }
        /// <summary>
        /// Offset plateau in millisecondi
        /// </summary>
        public int OffsetMs { get; set; }
        /// <summary>
        /// Numero anchor nel plateau
        /// </summary>
        public int AnchorCount { get; set; }
        /// <summary>
        /// Score medio plateau
        /// </summary>
        public double AverageScore { get; set; }
    }

    /// <summary>
    /// Regione DeepAnalysis con offset costante
    /// </summary>
    public class DeepAnalysisRegionDiagnostic
    {
        /// <summary>
        /// Indice regione
        /// </summary>
        public int Index { get; set; }
        /// <summary>
        /// Inizio source regione
        /// </summary>
        public double StartSrcSec { get; set; }
        /// <summary>
        /// Fine source regione
        /// </summary>
        public double EndSrcSec { get; set; }
        /// <summary>
        /// Centro del primo anchor affidabile della regione
        /// </summary>
        public double FirstAnchorSrcSec { get; set; }
        /// <summary>
        /// Centro dell'ultimo anchor affidabile della regione
        /// </summary>
        public double LastAnchorSrcSec { get; set; }
        /// <summary>
        /// Offset regione in millisecondi
        /// </summary>
        public double OffsetMs { get; set; }
        /// <summary>
        /// Numero match/anchor che supportano la regione
        /// </summary>
        public int MatchCount { get; set; }
    }

    /// <summary>
    /// Transizione candidate/refined tra due regioni DeepAnalysis
    /// </summary>
    public class DeepAnalysisTransitionDiagnostic
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepAnalysisTransitionDiagnostic()
        {
            this.Status = "";
            this.RejectReason = "";
            this.OperationType = "";
            this.RefineMethod = "";
            this.AudioFineTuneStatus = "NotRun";
            this.AudioFineTuneRejectReason = "";
            this.AudioFineTuneBoundaryKind = "";
            this.AudioFineTuneReferenceKind = "";
            this.AudioFineTuneReferenceTrackName = "";
            this.Candidates = new List<DeepAnalysisTransitionCandidateDiagnostic>();
        }

        /// <summary>
        /// Indice transizione
        /// </summary>
        public int Index { get; set; }
        /// <summary>
        /// Stato transizione
        /// </summary>
        public string Status { get; set; }
        /// <summary>
        /// Motivo rifiuto o nota diagnostica
        /// </summary>
        public string RejectReason { get; set; }
        /// <summary>
        /// Offset precedente in millisecondi
        /// </summary>
        public double OldOffsetMs { get; set; }
        /// <summary>
        /// Offset successivo in millisecondi
        /// </summary>
        public double NewOffsetMs { get; set; }
        /// <summary>
        /// Delta offset in millisecondi
        /// </summary>
        public int DeltaMs { get; set; }
        /// <summary>
        /// Breakpoint source stimato prima del refine
        /// </summary>
        public double BreakpointSrcSec { get; set; }
        /// <summary>
        /// Inizio finestra ricerca source
        /// </summary>
        public double SearchStartSrcSec { get; set; }
        /// <summary>
        /// Fine finestra ricerca source
        /// </summary>
        public double SearchEndSrcSec { get; set; }
        /// <summary>
        /// Inizio finestra validazione
        /// </summary>
        public double ValidationStartSrcSec { get; set; }
        /// <summary>
        /// True se il crossover arriva da audio locale
        /// </summary>
        public bool AudioCrossover { get; set; }
        /// <summary>
        /// Metodo refine usato
        /// </summary>
        public string RefineMethod { get; set; }
        /// <summary>
        /// Crossover finale in timeline source
        /// </summary>
        public double CrossoverSrcSec { get; set; }
        /// <summary>
        /// Tipo operazione prodotta
        /// </summary>
        public string OperationType { get; set; }
        /// <summary>
        /// Timestamp language operazione
        /// </summary>
        public int LangTimestampMs { get; set; }
        /// <summary>
        /// Timestamp source operazione
        /// </summary>
        public int SourceTimestampMs { get; set; }
        /// <summary>
        /// Durata operazione in millisecondi
        /// </summary>
        public int DurationMs { get; set; }
        /// <summary>
        /// Stato del fine tuning audio operativo
        /// </summary>
        public string AudioFineTuneStatus { get; set; }
        /// <summary>
        /// Motivo per cui il fine tuning audio non è stato applicato
        /// </summary>
        public string AudioFineTuneRejectReason { get; set; }
        /// <summary>
        /// Tipo di boundary audio selezionato
        /// </summary>
        public string AudioFineTuneBoundaryKind { get; set; }
        /// <summary>
        /// Reference audio usata per il fine tuning
        /// </summary>
        public string AudioFineTuneReferenceKind { get; set; }
        /// <summary>
        /// Nome della traccia audio usata come reference
        /// </summary>
        public string AudioFineTuneReferenceTrackName { get; set; }
        /// <summary>
        /// Indice ffmpeg della traccia audio usata come reference
        /// </summary>
        public int AudioFineTuneReferenceStreamIndex { get; set; }
        /// <summary>
        /// Timestamp reference prima del fine tuning
        /// </summary>
        public int AudioFineTuneReferenceOriginalTimestampMs { get; set; }
        /// <summary>
        /// Timestamp reference dopo il fine tuning
        /// </summary>
        public int AudioFineTuneReferenceSnappedTimestampMs { get; set; }
        /// <summary>
        /// Confidenza del boundary audio selezionato
        /// </summary>
        public double AudioFineTuneConfidence { get; set; }
        /// <summary>
        /// Timestamp language originale prima del fine tuning
        /// </summary>
        public int AudioFineTuneOriginalLangTimestampMs { get; set; }
        /// <summary>
        /// Timestamp language dopo il fine tuning
        /// </summary>
        public int AudioFineTuneSnappedLangTimestampMs { get; set; }
        /// <summary>
        /// Timestamp source operativo prima del fine tuning
        /// </summary>
        public int AudioFineTuneOriginalSourceTimestampMs { get; set; }
        /// <summary>
        /// Timestamp source operativo dopo il fine tuning
        /// </summary>
        public int AudioFineTuneSnappedSourceTimestampMs { get; set; }
        /// <summary>
        /// Boundary source/video mantenuto per diagnostica e verifica visuale
        /// </summary>
        public int AudioFineTuneVisualSourceTimestampMs { get; set; }
        /// <summary>
        /// Timestamp renderizzato prima del fine tuning
        /// </summary>
        public int AudioFineTuneRenderedBeforeMs { get; set; }
        /// <summary>
        /// Timestamp renderizzato dopo il fine tuning
        /// </summary>
        public int AudioFineTuneRenderedAfterMs { get; set; }
        /// <summary>
        /// Shift applicato dal fine tuning audio in millisecondi renderizzati
        /// </summary>
        public int AudioFineTuneShiftMs { get; set; }
        /// <summary>
        /// Inizio finestra fine tuning audio in millisecondi renderizzati
        /// </summary>
        public int AudioFineTuneWindowStartMs { get; set; }
        /// <summary>
        /// Fine finestra fine tuning audio in millisecondi renderizzati
        /// </summary>
        public int AudioFineTuneWindowEndMs { get; set; }
        /// <summary>
        /// Delta cumulativo delle operazioni precedenti in millisecondi renderizzati
        /// </summary>
        public int AudioFineTuneCumulativeDeltaBeforeMs { get; set; }
        /// <summary>
        /// Verifica locale associata alla transizione
        /// </summary>
        public DeepAnalysisLocalVerificationDiagnostic LocalVerification { get; set; }
        /// <summary>
        /// Candidati valutati durante il refine locale
        /// </summary>
        public List<DeepAnalysisTransitionCandidateDiagnostic> Candidates { get; set; }
    }

    /// <summary>
    /// Candidato locale valutato durante il refine di una transizione
    /// </summary>
    public class DeepAnalysisTransitionCandidateDiagnostic
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepAnalysisTransitionCandidateDiagnostic()
        {
            this.Decision = "";
            this.DarkBoundaryDecision = "";
        }

        /// <summary>
        /// Timestamp source del candidato
        /// </summary>
        public double SourceSec { get; set; }
        /// <summary>
        /// Timestamp source originale prima di eventuali correzioni dark boundary
        /// </summary>
        public double OriginalSourceSec { get; set; }
        /// <summary>
        /// Score differenziale usato per ordinare o filtrare il candidato
        /// </summary>
        public double Score { get; set; }
        /// <summary>
        /// Motion MSE source sul frame candidato
        /// </summary>
        public double MotionMse { get; set; }
        /// <summary>
        /// MSE vecchio offset sul frame candidato
        /// </summary>
        public double OldMse { get; set; }
        /// <summary>
        /// MSE nuovo offset sul frame candidato
        /// </summary>
        public double NewMse { get; set; }
        /// <summary>
        /// True se la verifica locale ha accettato direttamente il candidato
        /// </summary>
        public bool Verified { get; set; }
        /// <summary>
        /// True se il candidato può essere demandato alla verifica globale
        /// </summary>
        public bool CanDeferToGlobalVerification { get; set; }
        /// <summary>
        /// True se l'audio ammesso ha negato il candidato
        /// </summary>
        public bool AudioRejected { get; set; }
        /// <summary>
        /// Motivo sintetico del risultato candidato
        /// </summary>
        public string Decision { get; set; }
        /// <summary>
        /// True se il boundary è stato riportato all'inizio di una run dark comune
        /// </summary>
        public bool DarkBoundaryRewritten { get; set; }
        /// <summary>
        /// Shift applicato al boundary in millisecondi
        /// </summary>
        public double DarkBoundaryShiftMs { get; set; }
        /// <summary>
        /// Decisione diagnostica della correzione dark boundary
        /// </summary>
        public string DarkBoundaryDecision { get; set; }
        /// <summary>
        /// Numero frame validi nella run dark comune
        /// </summary>
        public int DarkBoundaryRunFrames { get; set; }
        /// <summary>
        /// Ratio dark dell'intervallo source/lang validato
        /// </summary>
        public double DarkBoundaryIntervalDarkRatio { get; set; }
    }

    /// <summary>
    /// Verifica locale di una transizione DeepAnalysis
    /// </summary>
    public class DeepAnalysisLocalVerificationDiagnostic
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepAnalysisLocalVerificationDiagnostic()
        {
            this.VisualSamples = new List<DeepAnalysisLocalVisualScoreSampleDiagnostic>();
        }

        /// <summary>
        /// True se la verifica locale è passata
        /// </summary>
        public bool Verified { get; set; }
        /// <summary>
        /// Timestamp source del punto prima della transizione
        /// </summary>
        public double BeforeSrcSec { get; set; }
        /// <summary>
        /// Timestamp source del punto dopo la transizione
        /// </summary>
        public double AfterSrcSec { get; set; }
        /// <summary>
        /// Timestamp source del punto forward della transizione
        /// </summary>
        public double ForwardSrcSec { get; set; }
        /// <summary>
        /// MSE prima della transizione con vecchio offset
        /// </summary>
        public double BeforeOldMse { get; set; }
        /// <summary>
        /// MSE prima della transizione con nuovo offset
        /// </summary>
        public double BeforeNewMse { get; set; }
        /// <summary>
        /// MSE dopo la transizione con vecchio offset
        /// </summary>
        public double AfterOldMse { get; set; }
        /// <summary>
        /// MSE dopo la transizione con nuovo offset
        /// </summary>
        public double AfterNewMse { get; set; }
        /// <summary>
        /// MSE forward con vecchio offset
        /// </summary>
        public double ForwardOldMse { get; set; }
        /// <summary>
        /// MSE forward con nuovo offset
        /// </summary>
        public double ForwardNewMse { get; set; }
        /// <summary>
        /// SSIM prima della transizione con vecchio offset
        /// </summary>
        public double BeforeOldSsim { get; set; }
        /// <summary>
        /// SSIM prima della transizione con nuovo offset
        /// </summary>
        public double BeforeNewSsim { get; set; }
        /// <summary>
        /// SSIM dopo la transizione con vecchio offset
        /// </summary>
        public double AfterOldSsim { get; set; }
        /// <summary>
        /// SSIM dopo la transizione con nuovo offset
        /// </summary>
        public double AfterNewSsim { get; set; }
        /// <summary>
        /// SSIM forward con vecchio offset
        /// </summary>
        public double ForwardOldSsim { get; set; }
        /// <summary>
        /// SSIM forward con nuovo offset
        /// </summary>
        public double ForwardNewSsim { get; set; }
        /// <summary>
        /// Rapporto miglioramento sul punto forward
        /// </summary>
        public double ForwardImprovementRatio { get; set; }
        /// <summary>
        /// Rapporto miglioramento locale
        /// </summary>
        public double ImprovementRatio { get; set; }
        /// <summary>
        /// True se la conferma audio locale ha validato la transizione
        /// </summary>
        public bool AudioVerified { get; set; }
        /// <summary>
        /// True se l'audio finale ammesso nega la transizione video con margine forte
        /// </summary>
        public bool AudioRejected { get; set; }
        /// <summary>
        /// Score audio prima della transizione con vecchio offset
        /// </summary>
        public double AudioBeforeOldScore { get; set; }
        /// <summary>
        /// Score audio prima della transizione con nuovo offset
        /// </summary>
        public double AudioBeforeNewScore { get; set; }
        /// <summary>
        /// Score audio dopo la transizione con vecchio offset
        /// </summary>
        public double AudioAfterOldScore { get; set; }
        /// <summary>
        /// Score audio dopo la transizione con nuovo offset
        /// </summary>
        public double AudioAfterNewScore { get; set; }
        /// <summary>
        /// Score audio forward con vecchio offset
        /// </summary>
        public double AudioForwardOldScore { get; set; }
        /// <summary>
        /// Score audio forward con nuovo offset
        /// </summary>
        public double AudioForwardNewScore { get; set; }
        /// <summary>
        /// True se la transizione può essere demandata alla verifica globale
        /// </summary>
        public bool CanDeferToGlobalVerification { get; set; }
        /// <summary>
        /// Coppie frame effettivamente confrontate dalla verifica visuale locale
        /// </summary>
        public List<DeepAnalysisLocalVisualScoreSampleDiagnostic> VisualSamples { get; set; }
    }

    /// <summary>
    /// Coppia frame usata per uno score visuale locale
    /// </summary>
    public class DeepAnalysisLocalVisualScoreSampleDiagnostic
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        public DeepAnalysisLocalVisualScoreSampleDiagnostic()
        {
            this.PointName = "";
            this.OffsetName = "";
        }

        /// <summary>
        /// Nome del punto locale verificato
        /// </summary>
        public string PointName { get; set; }
        /// <summary>
        /// Nome offset verificato
        /// </summary>
        public string OffsetName { get; set; }
        /// <summary>
        /// Inizio finestra source richiesta
        /// </summary>
        public double RequestedSourceStartSec { get; set; }
        /// <summary>
        /// Inizio finestra language richiesta
        /// </summary>
        public double RequestedLanguageStartSec { get; set; }
        /// <summary>
        /// Timestamp reale frame source
        /// </summary>
        public double SourceTimestampSec { get; set; }
        /// <summary>
        /// Timestamp language atteso dall'offset assoluto
        /// </summary>
        public double OffsetLanguageTimestampSec { get; set; }
        /// <summary>
        /// Timestamp language target usato dal matching locale
        /// </summary>
        public double TargetLanguageTimestampSec { get; set; }
        /// <summary>
        /// Timestamp reale frame language matchato
        /// </summary>
        public double MatchedLanguageTimestampSec { get; set; }
        /// <summary>
        /// Differenza tra frame language matchato e timestamp atteso dall'offset
        /// </summary>
        public double OffsetMatchDeltaMs { get; set; }
        /// <summary>
        /// Distanza tra frame language matchato e target locale
        /// </summary>
        public double MatchDistanceMs { get; set; }
        /// <summary>
        /// MSE calcolato sulla coppia
        /// </summary>
        public double Mse { get; set; }
        /// <summary>
        /// SSIM calcolato sulla coppia
        /// </summary>
        public double Ssim { get; set; }
    }

    /// <summary>
    /// Risultato verifica globale DeepAnalysis
    /// </summary>
    public class DeepAnalysisGlobalVerificationDiagnostic
    {
        /// <summary>
        /// True se la verifica globale è passata
        /// </summary>
        public bool Verified { get; set; }
        /// <summary>
        /// Punti validi
        /// </summary>
        public int ValidPoints { get; set; }
        /// <summary>
        /// Punti controllati
        /// </summary>
        public int PointsChecked { get; set; }
        /// <summary>
        /// Rapporto punti validi
        /// </summary>
        public double Ratio { get; set; }
        /// <summary>
        /// MSE baseline
        /// </summary>
        public double BaselineMse { get; set; }
        /// <summary>
        /// Soglia dinamica usata
        /// </summary>
        public double DynamicThreshold { get; set; }
        /// <summary>
        /// MSE massimo accettato
        /// </summary>
        public double MaxMse { get; set; }
    }

    /// <summary>
    /// Timing fasi DeepAnalysis in millisecondi
    /// </summary>
    public class DeepAnalysisTimingDiagnostic
    {
        /// <summary>
        /// Tempo totale
        /// </summary>
        public long TotalMs { get; set; }
        /// <summary>
        /// Tempo speed/stretch
        /// </summary>
        public long StretchMs { get; set; }
        /// <summary>
        /// Tempo rilevamento audio iniziale
        /// </summary>
        public long AudioInitialMs { get; set; }
        /// <summary>
        /// Tempo costruzione timeline
        /// </summary>
        public long TimelineMapMs { get; set; }
        /// <summary>
        /// Tempo refine transizioni
        /// </summary>
        public long TransitionRefineMs { get; set; }
        /// <summary>
        /// Tempo verifica globale
        /// </summary>
        public long GlobalVerifyMs { get; set; }
        /// <summary>
        /// Tempo fine tuning audio operativo
        /// </summary>
        public long AudioFineTuneMs { get; set; }
    }

    /// <summary>
    /// Contatori prestazionali interni DeepAnalysis
    /// </summary>
    public class DeepAnalysisPerformanceDiagnostic
    {
        /// <summary>
        /// Numero estrazioni segmento video
        /// </summary>
        public int SegmentExtractCalls { get; set; }
        /// <summary>
        /// Millisecondi totali estrazione segmenti
        /// </summary>
        public long SegmentExtractMs { get; set; }
        /// <summary>
        /// Numero estrazioni envelope audio
        /// </summary>
        public int AudioEnvelopeExtractCalls { get; set; }
        /// <summary>
        /// Millisecondi totali envelope audio
        /// </summary>
        public long AudioEnvelopeExtractMs { get; set; }
        /// <summary>
        /// Numero transizioni raffinate
        /// </summary>
        public int TransitionRefineCount { get; set; }
        /// <summary>
        /// Numero refine transizioni visuali
        /// </summary>
        public int TransitionVisualRefineCount { get; set; }
        /// <summary>
        /// Numero refine dense-linear
        /// </summary>
        public int TransitionDenseLinearRefineCount { get; set; }
    }
}
