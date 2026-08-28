
namespace RemuxForge.Core.Analysis.Edit
{
    /// <summary>
    /// Natura di un'operazione di montaggio rilevata fra le due copie
    /// </summary>
    internal enum EditOperationKind
    {
        /// <summary>
        /// La copia doppiata ha tolto materiale: l'offset sale
        /// </summary>
        CutSegment,

        /// <summary>
        /// La copia doppiata ha aggiunto materiale: l'offset scende
        /// </summary>
        InsertSilence
    }

    /// <summary>
    /// Chi ha deciso la posizione finale del confine
    /// </summary>
    internal enum BoundaryDecision
    {
        /// <summary>
        /// Il changepoint sui fotogrammi, senza correzioni successive
        /// </summary>
        ChangePoint,

        /// <summary>
        /// L'audio dentro la run scura, che ha impedito di arretrare oltre
        /// </summary>
        AudioInsideBlack,

        /// <summary>
        /// L'inizio della run di nero
        /// </summary>
        BlackRunStart,

        /// <summary>
        /// Lo stacco visivo netto dentro un intervallo equivalente
        /// </summary>
        SceneChange,

        /// <summary>
        /// L'ultimo fotogramma che solo l'offset di sinistra spiega
        /// </summary>
        ExclusiveFrame,

        /// <summary>
        /// L'estremo sinistro della finestra ambigua
        /// </summary>
        AmbiguousExtreme
    }

    /// <summary>
    /// Un'operazione candidata, dal profilo fino al giudizio finale
    /// </summary>
    internal class EditOperationCandidate
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public EditOperationCandidate()
        {
            this.Boundary = BoundaryDecision.ChangePoint;
            this.UncertaintyMs = 0.0;
            this.RejectReason = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Natura dell'operazione
        /// </summary>
        public EditOperationKind Kind { get; set; }

        /// <summary>
        /// Istante del confine nel dominio della sorgente
        /// </summary>
        public double TimestampMs { get; set; }

        /// <summary>
        /// Durata del materiale tolto o aggiunto
        /// </summary>
        public double DurationMs { get; set; }

        /// <summary>
        /// Offset del pianoro precedente il confine
        /// </summary>
        public double OffsetBeforeMs { get; set; }

        /// <summary>
        /// Offset del pianoro successivo al confine
        /// </summary>
        public double OffsetAfterMs { get; set; }

        /// <summary>
        /// Ultimo istante affidabile del pianoro precedente
        /// </summary>
        public double PlateauEndBeforeMs { get; set; }

        /// <summary>
        /// Primo istante affidabile del pianoro successivo
        /// </summary>
        public double PlateauStartAfterMs { get; set; }

        /// <summary>
        /// Larghezza della cima piatta con cui sono stati misurati i due offset
        /// </summary>
        public double UncertaintyMs { get; set; }

        /// <summary>
        /// Chi ha deciso la posizione finale del confine
        /// </summary>
        public BoundaryDecision Boundary { get; set; }

        /// <summary>
        /// Filtro che ha scartato l'operazione, vuoto quando è stata accettata
        /// </summary>
        public string RejectReason { get; set; }

        /// <summary>
        /// Istante in cui riparte il pianoro successivo: un INSERT buca la sorgente
        /// </summary>
        public double ResumeMs
        {
            get { return this.Kind == EditOperationKind.InsertSilence ? this.TimestampMs + this.DurationMs : this.TimestampMs; }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Copia il candidato conservandone tutti i campi
        /// </summary>
        /// <returns>Nuovo candidato identico</returns>
        public EditOperationCandidate Clone()
        {
            return new EditOperationCandidate
            {
                Kind = this.Kind,
                TimestampMs = this.TimestampMs,
                DurationMs = this.DurationMs,
                OffsetBeforeMs = this.OffsetBeforeMs,
                OffsetAfterMs = this.OffsetAfterMs,
                PlateauEndBeforeMs = this.PlateauEndBeforeMs,
                PlateauStartAfterMs = this.PlateauStartAfterMs,
                UncertaintyMs = this.UncertaintyMs,
                Boundary = this.Boundary,
                RejectReason = this.RejectReason
            };
        }

        #endregion
    }

    /// <summary>
    /// Esito del raffinamento di un confine sui fotogrammi a piena frequenza
    /// </summary>
    internal class ChangePointResult
    {
        #region Proprietà

        /// <summary>
        /// Primo fotogramma dopo l'ultimo comune: è il confine proposto
        /// </summary>
        public double NextAfterLastMs { get; set; }

        /// <summary>
        /// Primo fotogramma spiegato dall'offset di destra
        /// </summary>
        public double FirstCommonMs { get; set; }

        /// <summary>
        /// True quando il minimo è censurato dall'inizio della finestra
        /// </summary>
        public bool TouchesWindowStart { get; set; }

        /// <summary>
        /// True quando il minimo è censurato dalla fine della finestra
        /// </summary>
        public bool TouchesWindowEnd { get; set; }

        #endregion
    }
}
