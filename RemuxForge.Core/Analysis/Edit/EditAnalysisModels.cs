
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
        /// La fine della run di nero, perché l'operazione non ci stava dentro
        /// </summary>
        BlackRunEnd,

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
    /// Un punto del profilo offset(t)
    /// </summary>
    internal class OffsetProfilePoint
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="timeMs">Istante di misura</param>
        /// <param name="offsetMs">Offset misurato</param>
        /// <param name="explained">Frazione di fotogrammi spiegati</param>
        public OffsetProfilePoint(double timeMs, double offsetMs, double explained)
        {
            this.TimeMs = timeMs;
            this.OffsetMs = offsetMs;
            this.Explained = explained;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Istante di misura in millisecondi
        /// </summary>
        public double TimeMs { get; private set; }

        /// <summary>
        /// Offset misurato in millisecondi
        /// </summary>
        public double OffsetMs { get; private set; }

        /// <summary>
        /// Frazione di fotogrammi della finestra spiegati dall'offset
        /// </summary>
        public double Explained { get; private set; }

        #endregion
    }

    /// <summary>
    /// Un tratto rettilineo del profilo, con la sua incertezza
    /// </summary>
    internal class OffsetSegment
    {
        #region Proprietà

        /// <summary>
        /// Indice del primo punto di profilo incluso
        /// </summary>
        public int FirstIndex { get; set; }

        /// <summary>
        /// Indice del primo punto di profilo escluso
        /// </summary>
        public int EndIndex { get; set; }

        /// <summary>
        /// Intercetta della retta
        /// </summary>
        public double Intercept { get; set; }

        /// <summary>
        /// Pendenza della retta, in millisecondi di offset per millisecondo di tempo
        /// </summary>
        public double Slope { get; set; }

        /// <summary>
        /// Varianza residua della regressione
        /// </summary>
        public double ResidualVariance { get; set; }

        /// <summary>
        /// Punti di profilo del tratto
        /// </summary>
        public int PointCount { get; set; }

        /// <summary>
        /// Media dei tempi del tratto
        /// </summary>
        public double TimeMean { get; set; }

        /// <summary>
        /// Devianza dei tempi del tratto
        /// </summary>
        public double TimeVariation { get; set; }

        /// <summary>
        /// Istante del primo punto del tratto
        /// </summary>
        public double StartMs { get; set; }

        /// <summary>
        /// Istante dell'ultimo punto del tratto
        /// </summary>
        public double EndMs { get; set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Valuta la retta del tratto in un istante
        /// </summary>
        /// <param name="timeMs">Istante di valutazione</param>
        /// <returns>Offset previsto dalla retta</returns>
        public double ValueAt(double timeMs)
        {
            return this.Intercept + this.Slope * timeMs;
        }

        #endregion
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
        /// Ultimo fotogramma spiegato dall'offset di sinistra
        /// </summary>
        public double LastCommonMs { get; set; }

        /// <summary>
        /// Primo fotogramma dopo l'ultimo comune: è il confine proposto
        /// </summary>
        public double NextAfterLastMs { get; set; }

        /// <summary>
        /// Primo fotogramma spiegato dall'offset di destra
        /// </summary>
        public double FirstCommonMs { get; set; }

        /// <summary>
        /// Fotogrammi che nessuno dei due offset spiega
        /// </summary>
        public int UnexplainedFrames { get; set; }

        /// <summary>
        /// True quando l'intervallo ambiguo è tutto nero
        /// </summary>
        public bool IsBlack { get; set; }

        #endregion
    }
}
