using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Opzioni del solver temporale basato sulle sole evidenze visuali positive
    /// </summary>
    public class DeepSiftTemporalEvidenceOptions
    {
        /// <summary>
        /// Costruisce le opzioni con i valori predefiniti
        /// </summary>
        public DeepSiftTemporalEvidenceOptions()
        {
            this.SupportWindowMatchCount = 3;
            this.FrameUncertaintyMultiplier = 2.0;
        }

        /// <summary>
        /// Numero dispari di match usati per il supporto locale
        /// </summary>
        public int SupportWindowMatchCount { get; set; }

        /// <summary>
        /// Moltiplicatore applicato all'incertezza temporale del frame
        /// </summary>
        public double FrameUncertaintyMultiplier { get; set; }
    }

    /// <summary>
    /// Evidenza visuale scelta dalla catena monotona globale
    /// </summary>
    public class DeepSiftTemporalChainMatch
    {
        /// <summary>
        /// Indice dell'ancora source
        /// </summary>
        public int SourceAnchorIndex { get; set; }

        /// <summary>
        /// Indice dell'ancora language
        /// </summary>
        public int LanguageAnchorIndex { get; set; }

        /// <summary>
        /// PTS source in millisecondi
        /// </summary>
        public double SourcePtsMs { get; set; }

        /// <summary>
        /// PTS language in millisecondi
        /// </summary>
        public double LanguagePtsMs { get; set; }

        /// <summary>
        /// Offset language-source normalizzato per la scala
        /// </summary>
        public double OffsetMs { get; set; }

        /// <summary>
        /// Incertezza temporale del match in millisecondi
        /// </summary>
        public double UncertaintyMs { get; set; }

        /// <summary>
        /// Punteggio visuale del match
        /// </summary>
        public double Score { get; set; }
    }

    /// <summary>
    /// Segmento temporale sostenuto da un offset costante
    /// </summary>
    public class DeepSiftTemporalPlateau
    {
        /// <summary>
        /// Primo indice incluso nella catena monotona
        /// </summary>
        public int FirstChainIndex { get; set; }

        /// <summary>
        /// Ultimo indice incluso nella catena monotona
        /// </summary>
        public int LastChainIndex { get; set; }

        /// <summary>
        /// Numero di match che sostengono il plateau
        /// </summary>
        public int MatchCount { get; set; }

        /// <summary>
        /// Offset rappresentativo del plateau
        /// </summary>
        public double OffsetMs { get; set; }

        /// <summary>
        /// Incertezza temporale rappresentativa del plateau
        /// </summary>
        public double UncertaintyMs { get; set; }

        /// <summary>
        /// PTS iniziale source del plateau
        /// </summary>
        public double SourceStartPtsMs { get; set; }

        /// <summary>
        /// PTS finale source del plateau
        /// </summary>
        public double SourceEndPtsMs { get; set; }

        /// <summary>
        /// PTS iniziale language del plateau
        /// </summary>
        public double LanguageStartPtsMs { get; set; }

        /// <summary>
        /// PTS finale language del plateau
        /// </summary>
        public double LanguageEndPtsMs { get; set; }
    }

    /// <summary>
    /// Cambio di offset delimitato dalle ultime prove OLD e prime prove NEW
    /// </summary>
    public class DeepSiftTemporalTransition
    {
        /// <summary>
        /// Indice del plateau precedente
        /// </summary>
        public int BeforePlateauIndex { get; set; }

        /// <summary>
        /// Indice del plateau successivo
        /// </summary>
        public int AfterPlateauIndex { get; set; }

        /// <summary>
        /// Variazione di offset tra i plateau
        /// </summary>
        public double OffsetDeltaMs { get; set; }

        /// <summary>
        /// Separazione temporale tra le evidenze ai lati della transizione
        /// </summary>
        public double SeparationMs { get; set; }

        /// <summary>
        /// Ultimo PTS source appartenente al plateau precedente
        /// </summary>
        public double LastOldSourcePtsMs { get; set; }

        /// <summary>
        /// Primo PTS source appartenente al plateau successivo
        /// </summary>
        public double FirstNewSourcePtsMs { get; set; }

        /// <summary>
        /// Ultimo PTS language appartenente al plateau precedente
        /// </summary>
        public double LastOldLanguagePtsMs { get; set; }

        /// <summary>
        /// Primo PTS language appartenente al plateau successivo
        /// </summary>
        public double FirstNewLanguagePtsMs { get; set; }
    }

    /// <summary>
    /// Risultato replayabile della segmentazione temporale
    /// </summary>
    public class DeepSiftTemporalEvidenceResult
    {
        /// <summary>
        /// Costruisce un risultato vuoto con collezioni inizializzate
        /// </summary>
        public DeepSiftTemporalEvidenceResult()
        {
            this.RejectReason = "";
            this.Chain = new List<DeepSiftTemporalChainMatch>();
            this.Plateaus = new List<DeepSiftTemporalPlateau>();
            this.Transitions = new List<DeepSiftTemporalTransition>();
        }

        /// <summary>
        /// True quando la catena e i plateau superano i criteri di accettazione
        /// </summary>
        public bool Accepted { get; set; }

        /// <summary>
        /// Motivo del rifiuto, vuoto per un risultato accettato
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Numero di evidenze ricevute dal solver
        /// </summary>
        public int InputEvidenceCount { get; set; }

        /// <summary>
        /// Punteggio aggregato della catena monotona
        /// </summary>
        public double ChainScore { get; set; }

        /// <summary>
        /// Match selezionati nella catena monotona
        /// </summary>
        public List<DeepSiftTemporalChainMatch> Chain { get; set; }

        /// <summary>
        /// Plateau temporali rilevati
        /// </summary>
        public List<DeepSiftTemporalPlateau> Plateaus { get; set; }

        /// <summary>
        /// Transizioni tra plateau consecutivi
        /// </summary>
        public List<DeepSiftTemporalTransition> Transitions { get; set; }

    }
}
