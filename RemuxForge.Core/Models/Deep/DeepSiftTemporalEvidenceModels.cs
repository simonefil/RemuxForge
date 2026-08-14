using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    #region Configurazione e stati

    /// <summary>
    /// Opzioni usate dal solver temporale consapevole dei PTS
    /// </summary>
    public class DeepSiftTemporalEvidenceOptions
    {
        /// <summary>
        /// Inizializza le opzioni con i valori predefiniti del solver temporale
        /// </summary>
        public DeepSiftTemporalEvidenceOptions()
        {
            this.MinimumDistinctSupport = 3;
            this.MinimumScoreMargin = 0.04;
        }

        /// <summary>
        /// Numero minimo di frame distinti richiesto per considerare valido un modo globale
        /// </summary>
        public int MinimumDistinctSupport { get; set; }

        /// <summary>
        /// Margine minimo fra le confidence normalizzate di timestamp incompatibili
        /// </summary>
        public double MinimumScoreMargin { get; set; }
    }

    /// <summary>
    /// Classifica lo stato di una slice nella mappa globale delle evidenze
    /// </summary>
    public enum DeepSiftTemporalSliceKind
    {
        /// <summary>
        /// Slice con almeno un modo temporale osservabile
        /// </summary>
        Modes = 0,

        /// <summary>
        /// Slice priva di evidenze temporali utilizzabili
        /// </summary>
        Gap = 1
    }

    /// <summary>
    /// Cause combinabili che richiedono l'apertura di una regione temporale candidata
    /// </summary>
    [Flags]
    public enum DeepSiftCandidateRegionReason
    {
        /// <summary>
        /// Nessuna causa registrata
        /// </summary>
        None = 0,
        /// <summary>
        /// Modo osservato fuori dall'intervallo del regime corrente
        /// </summary>
        OffsetOutsideCurrentRegime = 1,
        /// <summary>
        /// Slice priva di evidenze temporali
        /// </summary>
        ExplicitGap = 2,
        /// <summary>
        /// Slice sostenuta da più modi temporali
        /// </summary>
        MultimodalSlice = 4,
        /// <summary>
        /// Supporto many-to-many non risolvibile globalmente
        /// </summary>
        TemporallyAmbiguousSupport = 8,
        /// <summary>
        /// Interruzione della copertura monotona
        /// </summary>
        InterruptedMonotoneCoverage = 16,
        /// <summary>
        /// Black run corrispondenti con differenza di durata osservabile
        /// </summary>
        BlackRunDurationDifference = 32
    }

    /// <summary>
    /// Esito strutturato del workspace di matching locale
    /// </summary>
    public enum DeepSiftLocalWorkspaceFailure
    {
        /// <summary>
        /// Nessun errore tecnico
        /// </summary>
        None = 0,
        /// <summary>
        /// Frame informativi insufficienti
        /// </summary>
        InsufficientFrames = 1,
        /// <summary>
        /// Nessuna coppia ricade nelle bande pianificate
        /// </summary>
        EmptyPairMatrix = 2,
        /// <summary>
        /// Backend di matching non disponibile o fallito
        /// </summary>
        MatchingFailed = 3,
        /// <summary>
        /// Estrazione FFmpeg fallita
        /// </summary>
        ExtractionFailed = 4
    }

    /// <summary>
    /// Stati esclusivi del ciclo di vita di una regione temporale candidata
    /// </summary>
    public enum DeepSiftCandidateRegionState
    {
        /// <summary>
        /// L'anomalia globale non richiede una risoluzione locale ed è conservata come dropout
        /// </summary>
        GlobalDropout = 0,

        /// <summary>
        /// La regione deve ancora essere risolta dal percorso locale
        /// </summary>
        PendingLocalResolution = 1,

        /// <summary>
        /// Il percorso locale ha dimostrato un solo regime e quindi un dropout
        /// </summary>
        ResolvedDropout = 2,

        /// <summary>
        /// Il percorso locale ha prodotto transizioni connesse ai supporti osservabili
        /// </summary>
        ResolvedTransitions = 3,

        /// <summary>
        /// La regione ha prodotto una contraddizione e ha causato il rifiuto fail-closed
        /// </summary>
        Rejected = 4
    }

    /// <summary>
    /// Classificazioni temporali esclusive di una coppia SIFT accettata geometricamente
    /// </summary>
    public enum DeepSiftTemporalPairClassification
    {
        /// <summary>
        /// Coppia con alternative temporali equivalenti
        /// </summary>
        Ambiguous = 0,
        /// <summary>
        /// Coppia reciprocamente univoca dopo la quantizzazione delle metriche
        /// </summary>
        Strong = 1
    }

    #endregion

    #region Evidenza globale

    /// <summary>
    /// Modo temporale conservato insieme alle alternative della relativa slice
    /// </summary>
    public class DeepSiftTemporalMode
    {
        /// <summary>
        /// Indice stabile della slice di appartenenza
        /// </summary>
        public int SliceIndex { get; set; }

        /// <summary>
        /// Indice stabile del modo all'interno della slice
        /// </summary>
        public int ModeIndex { get; set; }
        /// <summary>
        /// Offset mediano normalizzato rispetto alla timeline source
        /// </summary>
        public double OffsetMs { get; set; }

        /// <summary>
        /// Semilarghezza dell'intervallo di incertezza sui PTS
        /// </summary>
        public double UncertaintyMs { get; set; }

        /// <summary>
        /// Dispersione assoluta mediana degli offset osservati
        /// </summary>
        public double DispersionMs { get; set; }

        /// <summary>
        /// Primo PTS source sostenuto dal modo temporale
        /// </summary>
        public double SourceStartPtsMs { get; set; }

        /// <summary>
        /// Ultimo PTS source sostenuto dal modo temporale
        /// </summary>
        public double SourceEndPtsMs { get; set; }

        /// <summary>
        /// Primo PTS language sostenuto dal modo temporale
        /// </summary>
        public double LanguageStartPtsMs { get; set; }

        /// <summary>
        /// Ultimo PTS language sostenuto dal modo temporale
        /// </summary>
        public double LanguageEndPtsMs { get; set; }

        /// <summary>
        /// Numero di ancore source distinte che sostengono il modo
        /// </summary>
        public int DistinctSourceCount { get; set; }

        /// <summary>
        /// Numero di ancore language distinte che sostengono il modo
        /// </summary>
        public int DistinctLanguageCount { get; set; }

        /// <summary>
        /// Numero di ancore source distinte appartenenti al percorso temporalmente forte del modo
        /// </summary>
        public int StrongDistinctSourceCount { get; set; }

        /// <summary>
        /// Numero di ancore language distinte appartenenti al percorso temporalmente forte del modo
        /// </summary>
        public int StrongDistinctLanguageCount { get; set; }

        /// <summary>
        /// Numero di coppie accettate geometricamente nel cluster
        /// </summary>
        public int AcceptedPairCount { get; set; }

        /// <summary>
        /// Numero di coppie accettate e temporalmente univoche nel cluster
        /// </summary>
        public int StrongPairCount { get; set; }

        /// <summary>
        /// Numero di coppie geometriche non utilizzabili come supporto temporale
        /// </summary>
        public int AmbiguousPairCount { get; set; }

        /// <summary>
        /// Score SIFT aggregato del percorso temporalmente forte del modo
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// Copertura temporale source rappresentata dal modo
        /// </summary>
        public double SourceCoverageMs { get; set; }

        /// <summary>
        /// Copertura temporale language rappresentata dal modo
        /// </summary>
        public double LanguageCoverageMs { get; set; }

        /// <summary>
        /// Rapporto fra lo score del modo e quello del runner-up
        /// </summary>
        public double? BestToSecondScoreRatio { get; set; }

        /// <summary>
        /// Indica se la slice contiene associazioni temporali incompatibili
        /// </summary>
        public bool TemporallyAmbiguous { get; set; }

        /// <summary>
        /// Coppia rappresentativa scelta in modo deterministico per il modo
        /// </summary>
        public DeepSiftAcceptedPairDiagnostic Representative { get; set; }
    }

    /// <summary>
    /// Evidenza multimodale o GAP relativa a una slice del dispatch temporale
    /// </summary>
    public class DeepSiftTemporalSliceEvidence
    {
        /// <summary>
        /// Inizializza una slice con le collezioni già predisposte
        /// </summary>
        public DeepSiftTemporalSliceEvidence()
        {
            this.Modes = new List<DeepSiftTemporalMode>();
        }

        /// <summary>
        /// Indice ordinato della slice nella timeline source
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// PTS source iniziale della slice in millisecondi
        /// </summary>
        public double SourceStartPtsMs { get; set; }

        /// <summary>
        /// PTS source finale della slice in millisecondi
        /// </summary>
        public double SourceEndPtsMs { get; set; }

        /// <summary>
        /// Stato esplicito della slice
        /// </summary>
        public DeepSiftTemporalSliceKind Kind { get; set; }

        /// <summary>
        /// Modi temporali validi conservati nella slice
        /// </summary>
        public List<DeepSiftTemporalMode> Modes { get; set; }
    }

    /// <summary>
    /// Evidenza visuale scelta dal percorso monotono globale canonico
    /// </summary>
    public class DeepSiftTemporalChainMatch
    {
        /// <summary>
        /// Indice dell'ancora source rappresentativa
        /// </summary>
        public int SourceAnchorIndex { get; set; }
        /// <summary>
        /// Indice dell'ancora language rappresentativa
        /// </summary>
        public int LanguageAnchorIndex { get; set; }
        /// <summary>
        /// Indice della slice di provenienza
        /// </summary>
        public int SliceIndex { get; set; }
        /// <summary>
        /// Indice del modo nella slice di provenienza
        /// </summary>
        public int ModeIndex { get; set; }
        /// <summary>
        /// Numero di evidenze distinte del modo
        /// </summary>
        public int SupportCount { get; set; }
        /// <summary>
        /// PTS source rappresentativo in millisecondi
        /// </summary>
        public double SourcePtsMs { get; set; }
        /// <summary>
        /// PTS language rappresentativo in millisecondi
        /// </summary>
        public double LanguagePtsMs { get; set; }
        /// <summary>
        /// Offset normalizzato rispetto alla timeline source
        /// </summary>
        public double OffsetMs { get; set; }
        /// <summary>
        /// Semilarghezza dell'intervallo di incertezza sui PTS
        /// </summary>
        public double UncertaintyMs { get; set; }
        /// <summary>
        /// Score complessivo del modo rappresentato
        /// </summary>
        public double Score { get; set; }
    }

    /// <summary>
    /// Tratto globale affidabile usato per delimitare il lavoro locale
    /// </summary>
    public class DeepSiftTemporalSupportRun
    {
        /// <summary>
        /// Primo indice incluso del percorso globale
        /// </summary>
        public int FirstChainIndex { get; set; }
        /// <summary>
        /// Ultimo indice incluso del percorso globale
        /// </summary>
        public int LastChainIndex { get; set; }
        /// <summary>
        /// Numero di match osservabili nella run
        /// </summary>
        public int MatchCount { get; set; }
        /// <summary>
        /// Offset robusto della run
        /// </summary>
        public double OffsetMs { get; set; }
        /// <summary>
        /// Incertezza robusta della run
        /// </summary>
        public double UncertaintyMs { get; set; }
        /// <summary>
        /// Limite inferiore dell'intervallo di offset
        /// </summary>
        public double MinimumOffsetMs { get; set; }
        /// <summary>
        /// Limite superiore dell'intervallo di offset
        /// </summary>
        public double MaximumOffsetMs { get; set; }
        /// <summary>
        /// PTS source iniziale della run in millisecondi
        /// </summary>
        public double SourceStartPtsMs { get; set; }
        /// <summary>
        /// PTS source finale della run in millisecondi
        /// </summary>
        public double SourceEndPtsMs { get; set; }
        /// <summary>
        /// PTS language iniziale della run in millisecondi
        /// </summary>
        public double LanguageStartPtsMs { get; set; }
        /// <summary>
        /// PTS language finale della run in millisecondi
        /// </summary>
        public double LanguageEndPtsMs { get; set; }
    }

    #endregion

    #region Topologia locale

    /// <summary>
    /// Coppia SIFT locale classificata rispetto alle alternative temporali disponibili
    /// </summary>
    public class DeepSiftLocalPathPoint
    {
        /// <summary>
        /// Indice locale dell'ancora source
        /// </summary>
        public int SourceAnchorIndex { get; set; }
        /// <summary>
        /// Indice locale dell'ancora language
        /// </summary>
        public int LanguageAnchorIndex { get; set; }
        /// <summary>
        /// Indice del regime assegnato oppure -1 quando il punto non è assegnato
        /// </summary>
        public int ModeIndex { get; set; }
        /// <summary>
        /// PTS source in millisecondi
        /// </summary>
        public double SourcePtsMs { get; set; }
        /// <summary>
        /// PTS language in millisecondi
        /// </summary>
        public double LanguagePtsMs { get; set; }
        /// <summary>
        /// Offset normalizzato rispetto alla timeline source
        /// </summary>
        public double OffsetMs { get; set; }
        /// <summary>
        /// Semilarghezza dell'intervallo di incertezza sui PTS della coppia
        /// </summary>
        public double UncertaintyMs { get; set; }
        /// <summary>
        /// Score geometrico sottoposto a quantizzazione durante gli spareggi
        /// </summary>
        public double Score { get; set; }
        /// <summary>
        /// Numero di inlier geometrici
        /// </summary>
        public int InlierCount { get; set; }
        /// <summary>
        /// Rapporto degli inlier geometrici
        /// </summary>
        public double InlierRatio { get; set; }
        /// <summary>
        /// Copertura spaziale source
        /// </summary>
        public double SourceCoverage { get; set; }
        /// <summary>
        /// Copertura spaziale language
        /// </summary>
        public double LanguageCoverage { get; set; }
        /// <summary>
        /// Errore medio di riproiezione
        /// </summary>
        public double MeanReprojectionError { get; set; }
        /// <summary>
        /// Numero di supporti temporali distinti
        /// </summary>
        public int DistinctSupportCount { get; set; }
        /// <summary>
        /// Classificazione temporale esclusiva della coppia
        /// </summary>
        public DeepSiftTemporalPairClassification Classification { get; set; }
    }

    /// <summary>
    /// Regime temporale risolto dal percorso SIFT locale
    /// </summary>
    public class DeepSiftLocalRegime
    {
        /// <summary>
        /// Inizializza un regime senza punti assegnati
        /// </summary>
        public DeepSiftLocalRegime()
        {
            this.PathIndexes = new List<int>();
        }

        /// <summary>
        /// Primo indice del percorso appartenente al regime
        /// </summary>
        public int FirstPathIndex { get; set; }
        /// <summary>
        /// Ultimo indice del percorso appartenente al regime
        /// </summary>
        public int LastPathIndex { get; set; }
        /// <summary>
        /// Indici osservabili del percorso assegnati al regime
        /// </summary>
        public List<int> PathIndexes { get; set; }
        /// <summary>
        /// Numero di match osservabili del regime
        /// </summary>
        public int MatchCount { get; set; }
        /// <summary>
        /// Offset robusto del regime
        /// </summary>
        public double OffsetMs { get; set; }
        /// <summary>
        /// Incertezza robusta del regime
        /// </summary>
        public double UncertaintyMs { get; set; }
        /// <summary>
        /// PTS source iniziale del regime in millisecondi
        /// </summary>
        public double SourceStartPtsMs { get; set; }
        /// <summary>
        /// PTS source finale del regime in millisecondi
        /// </summary>
        public double SourceEndPtsMs { get; set; }
        /// <summary>
        /// PTS language iniziale del regime in millisecondi
        /// </summary>
        public double LanguageStartPtsMs { get; set; }
        /// <summary>
        /// PTS language finale del regime in millisecondi
        /// </summary>
        public double LanguageEndPtsMs { get; set; }
    }

    /// <summary>
    /// Cambio di offset prodotto da due regimi locali osservabili consecutivi
    /// </summary>
    public class DeepSiftLocalTransition
    {
        /// <summary>
        /// Indice del regime precedente
        /// </summary>
        public int BeforeRegimeIndex { get; set; }
        /// <summary>
        /// Indice del regime successivo
        /// </summary>
        public int AfterRegimeIndex { get; set; }
        /// <summary>
        /// Ultimo PTS source osservato prima del cambio
        /// </summary>
        public double LastBeforeSourcePtsMs { get; set; }
        /// <summary>
        /// Primo PTS source osservato dopo il cambio
        /// </summary>
        public double FirstAfterSourcePtsMs { get; set; }
        /// <summary>
        /// Ultimo PTS language osservato prima del cambio
        /// </summary>
        public double LastBeforeLanguagePtsMs { get; set; }
        /// <summary>
        /// Primo PTS language osservato dopo il cambio
        /// </summary>
        public double FirstAfterLanguagePtsMs { get; set; }
        /// <summary>
        /// Primo PTS source geometricamente accettato nel nuovo regime all'interno del corridoio
        /// </summary>
        public double FirstAfterCandidateSourcePtsMs { get; set; }
        /// <summary>
        /// Primo PTS language geometricamente accettato nel nuovo regime all'interno del corridoio
        /// </summary>
        public double FirstAfterCandidateLanguagePtsMs { get; set; }
    }

    /// <summary>
    /// Salto osservato fra due coppie consecutive del percorso locale, senza semantica di operazione
    /// </summary>
    public class DeepSiftLocalGap
    {
        /// <summary>
        /// Indice osservabile precedente al gap
        /// </summary>
        public int BeforePathIndex { get; set; }
        /// <summary>
        /// Indice osservabile successivo al gap
        /// </summary>
        public int AfterPathIndex { get; set; }
        /// <summary>
        /// Inizio source del gap in millisecondi
        /// </summary>
        public double SourceStartPtsMs { get; set; }
        /// <summary>
        /// Fine source del gap in millisecondi
        /// </summary>
        public double SourceEndPtsMs { get; set; }
        /// <summary>
        /// Inizio language del gap in millisecondi
        /// </summary>
        public double LanguageStartPtsMs { get; set; }
        /// <summary>
        /// Fine language del gap in millisecondi
        /// </summary>
        public double LanguageEndPtsMs { get; set; }
    }

    #endregion

    #region Regione candidata

    /// <summary>
    /// Regione globale da risolvere localmente senza imporre una topologia binaria
    /// </summary>
    public class DeepSiftTemporalCandidateRegion
    {
        /// <summary>
        /// Inizializza una regione diagnostica vuota con le collezioni predisposte
        /// </summary>
        public DeepSiftTemporalCandidateRegion()
        {
            this.BeforeSupportRunIndex = -1;
            this.AfterSupportRunIndex = -1;
            this.GlobalModes = new List<DeepSiftTemporalMode>();
            this.LocalPairEvidence = new List<DeepSiftAcceptedPairDiagnostic>();
            this.Path = new List<DeepSiftLocalPathPoint>();
            this.Regimes = new List<DeepSiftLocalRegime>();
            this.Transitions = new List<DeepSiftLocalTransition>();
            this.Gaps = new List<DeepSiftLocalGap>();
            this.SourceBlackRuns = new List<DeepBlackTimelineRun>();
            this.LanguageBlackRuns = new List<DeepBlackTimelineRun>();
            this.BlackDerivedSearchOffsetsMs = new List<double>();
            this.RejectReason = "";
        }

        /// <summary>
        /// Indice stabile della regione nell'ordine source
        /// </summary>
        public int Index { get; set; }

        /// <summary>
        /// Prima slice globale inclusa nell'anomalia
        /// </summary>
        public int FirstSliceIndex { get; set; }

        /// <summary>
        /// Ultima slice globale inclusa nell'anomalia
        /// </summary>
        public int LastSliceIndex { get; set; }

        /// <summary>
        /// Indice del supporto globale osservabile precedente oppure -1 quando assente
        /// </summary>
        public int BeforeSupportRunIndex { get; set; }

        /// <summary>
        /// Indice del supporto globale osservabile successivo oppure -1 quando assente
        /// </summary>
        public int AfterSupportRunIndex { get; set; }
        /// <summary>
        /// Stato esclusivo della regione durante la risoluzione, il replay e la costruzione della EditMap
        /// </summary>
        public DeepSiftCandidateRegionState State { get; set; }
        /// <summary>
        /// Inizio dell'envelope source in millisecondi
        /// </summary>
        public double SourceStartPtsMs { get; set; }

        /// <summary>
        /// Fine dell'envelope source in millisecondi
        /// </summary>
        public double SourceEndPtsMs { get; set; }

        /// <summary>
        /// Inizio dell'envelope language in millisecondi
        /// </summary>
        public double LanguageStartPtsMs { get; set; }

        /// <summary>
        /// Fine dell'envelope language in millisecondi
        /// </summary>
        public double LanguageEndPtsMs { get; set; }

        /// <summary>
        /// Cause strutturate che hanno aperto la regione
        /// </summary>
        public DeepSiftCandidateRegionReason OpenReasonFlags { get; set; }

        /// <summary>
        /// Esito tecnico esclusivo dell'ultimo workspace locale
        /// </summary>
        public DeepSiftLocalWorkspaceFailure WorkspaceFailure { get; set; }

        /// <summary>
        /// Restituisce le descrizioni localizzate delle cause di apertura
        /// </summary>
        public List<string> OpenReasons
        {
            get
            {
                List<string> result = new List<string>();
                if ((this.OpenReasonFlags & DeepSiftCandidateRegionReason.OffsetOutsideCurrentRegime) != 0)
                    result.Add(AppText.T("deep.temporal.regionReason.offsetOutsideCurrentRegime"));
                if ((this.OpenReasonFlags & DeepSiftCandidateRegionReason.ExplicitGap) != 0)
                    result.Add(AppText.T("deep.temporal.regionReason.explicitGap"));
                if ((this.OpenReasonFlags & DeepSiftCandidateRegionReason.MultimodalSlice) != 0)
                    result.Add(AppText.T("deep.temporal.regionReason.multimodalSlice"));
                if ((this.OpenReasonFlags & DeepSiftCandidateRegionReason.TemporallyAmbiguousSupport) != 0)
                    result.Add(AppText.T("deep.temporal.regionReason.temporallyAmbiguousSupport"));
                if ((this.OpenReasonFlags & DeepSiftCandidateRegionReason.InterruptedMonotoneCoverage) != 0)
                    result.Add(AppText.T("deep.temporal.regionReason.interruptedMonotoneCoverage"));
                if ((this.OpenReasonFlags & DeepSiftCandidateRegionReason.BlackRunDurationDifference) != 0)
                    result.Add(AppText.T("deep.temporal.regionReason.blackRunDurationDifference"));
                return result;
            }
        }
        /// <summary>
        /// Modi globali osservati all'interno dell'envelope
        /// </summary>
        public List<DeepSiftTemporalMode> GlobalModes { get; set; }

        /// <summary>
        /// Migliori coppie locali compatte per famiglia temporale, sufficienti al replay senza FFmpeg
        /// </summary>
        public List<DeepSiftAcceptedPairDiagnostic> LocalPairEvidence { get; set; }

        /// <summary>
        /// Percorso monotono locale scelto
        /// </summary>
        public List<DeepSiftLocalPathPoint> Path { get; set; }

        /// <summary>
        /// Regimi osservabili derivati dal percorso locale
        /// </summary>
        public List<DeepSiftLocalRegime> Regimes { get; set; }

        /// <summary>
        /// Transizioni osservabili fra i regimi locali
        /// </summary>
        public List<DeepSiftLocalTransition> Transitions { get; set; }

        /// <summary>
        /// Gap del percorso locale che separano supporti osservabili
        /// </summary>
        public List<DeepSiftLocalGap> Gaps { get; set; }

        /// <summary>
        /// Black run source comprese nell'envelope finale
        /// </summary>
        public List<DeepBlackTimelineRun> SourceBlackRuns { get; set; }

        /// <summary>
        /// Black run language comprese nell'envelope finale
        /// </summary>
        public List<DeepBlackTimelineRun> LanguageBlackRuns { get; set; }

        /// <summary>
        /// Offset di ricerca suggeriti dalle sole differenze fra black run
        /// </summary>
        public List<double> BlackDerivedSearchOffsetsMs { get; set; }

        /// <summary>
        /// Numero di coppie locali accettate geometricamente e non nere
        /// </summary>
        public int AcceptedPairCount { get; set; }

        /// <summary>
        /// Numero di coppie temporalmente forti
        /// </summary>
        public int StrongPairCount { get; set; }

        /// <summary>
        /// Numero di coppie temporalmente ambigue
        /// </summary>
        public int AmbiguousPairCount { get; set; }

        /// <summary>
        /// Numero di regimi locali risolti
        /// </summary>
        public int ResolvedRegimeCount { get; set; }

        /// <summary>
        /// Numero di transizioni locali prodotte
        /// </summary>
        public int ProducedTransitionCount { get; set; }

        /// <summary>
        /// Numero di frame source distinti elaborati
        /// </summary>
        public int SourceFrameCount { get; set; }

        /// <summary>
        /// Numero di frame language distinti elaborati
        /// </summary>
        public int LanguageFrameCount { get; set; }

        /// <summary>
        /// Copertura source del percorso locale in millisecondi
        /// </summary>
        public double SourceCoverageMs { get; set; }

        /// <summary>
        /// Copertura language del percorso locale in millisecondi
        /// </summary>
        public double LanguageCoverageMs { get; set; }

        /// <summary>
        /// Numero di gap individuati nel percorso locale
        /// </summary>
        public int GapCount { get; set; }

        /// <summary>
        /// Profondità massima di espansione raggiunta
        /// </summary>
        public int ExpansionCount { get; set; }

        /// <summary>
        /// Tempo di estrazione source in millisecondi
        /// </summary>
        public long SourceExtractionMs { get; set; }

        /// <summary>
        /// Tempo di estrazione language in millisecondi
        /// </summary>
        public long LanguageExtractionMs { get; set; }

        /// <summary>
        /// Tempo complessivo di estrazione in millisecondi
        /// </summary>
        public long ExtractionMs { get; set; }

        /// <summary>
        /// Tempo complessivo di matching in millisecondi
        /// </summary>
        public long MatchingMs { get; set; }

        /// <summary>
        /// Tempo di risoluzione del percorso in millisecondi
        /// </summary>
        public long PathSolvingMs { get; set; }

        /// <summary>
        /// Tempo di raffinamento dei boundary in millisecondi
        /// </summary>
        public long BoundaryRefinementMs { get; set; }

        /// <summary>
        /// Motivo localizzato del dropout o del rifiuto
        /// </summary>
        public string RejectReason { get; set; }
    }

    #endregion

    #region Risultato

    /// <summary>
    /// Risultato replayabile della mappa delle evidenze e del percorso temporale
    /// </summary>
    public class DeepSiftTemporalEvidenceResult
    {
        /// <summary>
        /// Inizializza un risultato vuoto con le collezioni già predisposte
        /// </summary>
        public DeepSiftTemporalEvidenceResult()
        {
            this.RejectReason = "";
            this.GlobalPairEvidence = new List<DeepSiftAcceptedPairDiagnostic>();
            this.Slices = new List<DeepSiftTemporalSliceEvidence>();
            this.Chain = new List<DeepSiftTemporalChainMatch>();
            this.SupportRuns = new List<DeepSiftTemporalSupportRun>();
            this.CandidateRegions = new List<DeepSiftTemporalCandidateRegion>();
            this.ResolvedPath = new List<DeepSiftLocalPathPoint>();
            this.ResolvedRegimes = new List<DeepSiftLocalRegime>();
            this.ResolvedTransitions = new List<DeepSiftLocalTransition>();
        }

        /// <summary>
        /// Indica se esiste un percorso globale univoco e coerente
        /// </summary>
        public bool Accepted { get; set; }

        /// <summary>
        /// Motivo localizzato del rifiuto fail-closed
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Numero di coppie globali filtrate ricevute dal solver
        /// </summary>
        public int InputEvidenceCount { get; set; }

        /// <summary>
        /// Score complessivo del percorso globale scelto
        /// </summary>
        public double ChainScore { get; set; }

        /// <summary>
        /// Coppie SIFT globali accettate e filtrate da cui ricostruire slice, modi e percorso senza rieseguire FFmpeg
        /// </summary>
        public List<DeepSiftAcceptedPairDiagnostic> GlobalPairEvidence { get; set; }

        /// <summary>
        /// Slice globali multimodali e GAP espliciti
        /// </summary>
        public List<DeepSiftTemporalSliceEvidence> Slices { get; set; }

        /// <summary>
        /// Percorso monotono globale scelto
        /// </summary>
        public List<DeepSiftTemporalChainMatch> Chain { get; set; }

        /// <summary>
        /// Run globali affidabili derivate dal percorso
        /// </summary>
        public List<DeepSiftTemporalSupportRun> SupportRuns { get; set; }

        /// <summary>
        /// Regioni candidate ordinate e non sovrapposte
        /// </summary>
        public List<DeepSiftTemporalCandidateRegion> CandidateRegions { get; set; }

        /// <summary>
        /// Unico percorso canonico dopo la sostituzione delle regioni risolte
        /// </summary>
        public List<DeepSiftLocalPathPoint> ResolvedPath { get; set; }

        /// <summary>
        /// Regimi osservabili dell'unico percorso canonico
        /// </summary>
        public List<DeepSiftLocalRegime> ResolvedRegimes { get; set; }

        /// <summary>
        /// Transizioni osservabili dell'unico percorso canonico
        /// </summary>
        public List<DeepSiftLocalTransition> ResolvedTransitions { get; set; }
    }

    #endregion
}
