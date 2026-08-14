using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Deep
{
    /// <summary>
    /// Mantiene le rappresentanti migliori delle famiglie temporali sui due assi PTS
    /// </summary>
    internal sealed class DeepSiftTemporalPairAccumulator
    {
        #region Variabili di classe

        /// <summary>
        /// Scala source-language usata per calcolare offset e incertezza temporale
        /// </summary>
        private readonly double _scale;

        /// <summary>
        /// Indica se le celle PTS ripetute tra batch devono essere ignorate
        /// </summary>
        private readonly bool _deduplicate;

        /// <summary>
        /// Chiavi delle celle PTS già accettate durante la deduplicazione
        /// </summary>
        private readonly HashSet<(long SourcePts, long LanguagePts)> _acceptedKeys;

        /// <summary>
        /// Assi delle famiglie temporali indicizzati per PTS source
        /// </summary>
        private readonly Dictionary<long, TemporalPairAxis> _sourceAxes;

        /// <summary>
        /// Assi delle famiglie temporali indicizzati per PTS language
        /// </summary>
        private readonly Dictionary<long, TemporalPairAxis> _languageAxes;

        /// <summary>
        /// Numero di coppie registrate prima della compattazione
        /// </summary>
        private int _acceptedPairCount;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza l'accumulatore con la deduplicazione attiva
        /// </summary>
        /// <param name="scale">Scala source-language usata per calcolare offset e incertezza temporale</param>
        public DeepSiftTemporalPairAccumulator(double scale)
            : this(scale, true)
        {
        }

        /// <summary>
        /// Inizializza l'accumulatore scegliendo se deduplicare le celle provenienti da batch sovrapposti
        /// </summary>
        /// <param name="scale">Scala source-language usata per calcolare offset e incertezza temporale</param>
        /// <param name="deduplicate">Indica se ignorare le celle PTS già registrate</param>
        public DeepSiftTemporalPairAccumulator(double scale, bool deduplicate)
        {
            if (!double.IsFinite(scale) || scale <= 0.0)
                throw new ArgumentOutOfRangeException(nameof(scale));
            this._scale = scale;
            this._deduplicate = deduplicate;
            this._acceptedKeys = new HashSet<(long SourcePts, long LanguagePts)>();
            this._sourceAxes = new Dictionary<long, TemporalPairAxis>();
            this._languageAxes = new Dictionary<long, TemporalPairAxis>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Registra una coppia e, se richiesto, ignora le celle PTS già registrate
        /// </summary>
        /// <param name="pair">Coppia accettata dal controllo geometrico</param>
        public void Add(DeepSiftAcceptedPairDiagnostic pair)
        {
            if (pair == null)
                return;
            (long SourcePts, long LanguagePts) key = GetPairKey(pair);
            if (this._deduplicate && !this._acceptedKeys.Add(key))
                return;
            this._acceptedPairCount++;

            this.GetAxis(this._sourceAxes, key.SourcePts).Add(pair, this._scale);
            this.GetAxis(this._languageAxes, key.LanguagePts).Add(pair, this._scale);
        }

        /// <summary>
        /// Registra tutte le coppie di una sequenza accettata dal controllo geometrico
        /// </summary>
        /// <param name="pairs">Sequenza di coppie da registrare</param>
        public void AddRange(IReadOnlyList<DeepSiftAcceptedPairDiagnostic> pairs)
        {
            if (pairs == null)
                return;
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
                this.Add(pairs[pairIndex]);
        }

        /// <summary>
        /// Restituisce l'unione deterministica delle rappresentanti mantenute sui due assi PTS
        /// </summary>
        /// <returns>Coppie rappresentative compatte ordinate per PTS</returns>
        public List<DeepSiftAcceptedPairDiagnostic> GetCandidates()
        {
            Dictionary<(long SourcePts, long LanguagePts), DeepSiftAcceptedPairDiagnostic> candidates = new Dictionary<(long SourcePts, long LanguagePts), DeepSiftAcceptedPairDiagnostic>();
            this.AppendAxisCandidates(candidates, this._sourceAxes);
            this.AppendAxisCandidates(candidates, this._languageAxes);
            List<DeepSiftAcceptedPairDiagnostic> result = new List<DeepSiftAcceptedPairDiagnostic>(candidates.Values);
            result.Sort(ComparePairs);
            return result;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Numero di coppie registrate prima della compattazione
        /// </summary>
        public int AcceptedPairCount { get { return this._acceptedPairCount; } }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Recupera o crea l'asse temporale associato a una chiave PTS
        /// </summary>
        /// <param name="axes">Indice degli assi temporali</param>
        /// <param name="key">Chiave PTS quantizzata</param>
        /// <returns>Asse associato alla chiave</returns>
        private TemporalPairAxis GetAxis(Dictionary<long, TemporalPairAxis> axes, long key)
        {
            if (!axes.TryGetValue(key, out TemporalPairAxis axis))
            {
                axis = new TemporalPairAxis();
                axes.Add(key, axis);
            }
            return axis;
        }

        /// <summary>
        /// Aggiunge le rappresentanti di ogni asse all'insieme compatto delle coppie candidate
        /// </summary>
        /// <param name="candidates">Candidate indicizzate per coppia PTS</param>
        /// <param name="axes">Assi temporali da materializzare</param>
        private void AppendAxisCandidates(Dictionary<(long SourcePts, long LanguagePts), DeepSiftAcceptedPairDiagnostic> candidates, Dictionary<long, TemporalPairAxis> axes)
        {
            foreach (TemporalPairAxis axis in axes.Values)
            {
                IReadOnlyList<DeepSiftAcceptedPairDiagnostic> representatives = axis.Representatives;
                for (int representativeIndex = 0; representativeIndex < representatives.Count; representativeIndex++)
                {
                    DeepSiftAcceptedPairDiagnostic pair = representatives[representativeIndex];
                    candidates[GetPairKey(pair)] = pair;
                }
            }
        }

        /// <summary>
        /// Calcola la chiave PTS deterministica di una coppia
        /// </summary>
        /// <param name="pair">Coppia da indicizzare</param>
        /// <returns>Valori PTS source e language quantizzati</returns>
        private static (long SourcePts, long LanguagePts) GetPairKey(DeepSiftAcceptedPairDiagnostic pair)
        {
            return (DeepSiftTemporalMetricComparer.QuantizeMilliseconds(pair.SourcePtsMs), DeepSiftTemporalMetricComparer.QuantizeMilliseconds(pair.LanguagePtsMs));
        }

        /// <summary>
        /// Confronta le coppie per PTS e usa il punteggio di confidenza come spareggio finale
        /// </summary>
        /// <param name="left">Prima coppia da confrontare</param>
        /// <param name="right">Seconda coppia da confrontare</param>
        /// <returns>Risultato del confronto deterministico</returns>
        private static int ComparePairs(DeepSiftAcceptedPairDiagnostic left, DeepSiftAcceptedPairDiagnostic right)
        {
            int comparison = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(left.SourcePtsMs).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(right.SourcePtsMs));
            if (comparison != 0)
                return comparison;
            comparison = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(left.LanguagePtsMs).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(right.LanguagePtsMs));
            if (comparison != 0)
                return comparison;
            return DeepSiftTemporalMetricComparer.QuantizeMetric(right.Score).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMetric(left.Score));
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Raggruppa le famiglie temporalmente equivalenti associate a un singolo PTS dell'asse
        /// </summary>
        private sealed class TemporalPairAxis
        {
            /// <summary>
            /// Famiglie temporalmente distinte mantenute per l'asse
            /// </summary>
            private readonly List<TemporalPairFamily> _families;

            /// <summary>
            /// Inizializza un asse senza famiglie temporali
            /// </summary>
            public TemporalPairAxis()
            {
                this._families = new List<TemporalPairFamily>();
            }

            /// <summary>
            /// Inserisce la coppia nella famiglia compatibile o crea una nuova famiglia
            /// </summary>
            /// <param name="pair">Coppia da valutare</param>
            /// <param name="scale">Scala source-language usata per calcolare offset e incertezza temporale</param>
            public void Add(DeepSiftAcceptedPairDiagnostic pair, double scale)
            {
                double offsetMs = pair.SourcePtsMs - (pair.LanguagePtsMs / scale);
                double uncertaintyMs = DeepSiftTemporalMetricComparer.GetPairUncertaintyMs(pair, scale);
                TemporalPairFamily selected = null;
                double selectedDistanceMs = double.PositiveInfinity;
                for (int familyIndex = 0; familyIndex < this._families.Count; familyIndex++)
                {
                    TemporalPairFamily family = this._families[familyIndex];
                    double distanceMs = Math.Abs(offsetMs - family.OffsetMs);
                    if (distanceMs > uncertaintyMs + family.UncertaintyMs || distanceMs >= selectedDistanceMs)
                        continue;
                    selected = family;
                    selectedDistanceMs = distanceMs;
                }
                if (selected == null)
                {
                    TemporalPairFamily candidate = new TemporalPairFamily(pair, offsetMs, uncertaintyMs);
                    if (this._families.Count < 2)
                        this._families.Add(candidate);
                    else
                    {
                        int worstIndex = this._families[0].IsBetterThan(this._families[1]) ? 1 : 0;
                        if (candidate.IsBetterThan(this._families[worstIndex]))
                            this._families[worstIndex] = candidate;
                    }
                }
                else
                    selected.Consider(pair, offsetMs, uncertaintyMs);
            }

            /// <summary>
            /// Migliore e seconda rappresentante delle famiglie mantenute
            /// </summary>
            public IReadOnlyList<DeepSiftAcceptedPairDiagnostic> Representatives
            {
                get
                {
                    List<DeepSiftAcceptedPairDiagnostic> result = new List<DeepSiftAcceptedPairDiagnostic>(2);
                    if (this._families.Count == 0)
                        return result;
                    int bestIndex = 0;
                    for (int familyIndex = 1; familyIndex < this._families.Count; familyIndex++)
                    {
                        if (this._families[familyIndex].IsBetterThan(this._families[bestIndex]))
                            bestIndex = familyIndex;
                    }
                    result.Add(this._families[bestIndex].Representative);
                    int runnerIndex = -1;
                    for (int familyIndex = 0; familyIndex < this._families.Count; familyIndex++)
                    {
                        if (familyIndex == bestIndex)
                            continue;
                        if (runnerIndex < 0 || this._families[familyIndex].IsBetterThan(this._families[runnerIndex]))
                            runnerIndex = familyIndex;
                    }
                    if (runnerIndex >= 0)
                        result.Add(this._families[runnerIndex].Representative);
                    return result;
                }
            }
        }

        /// <summary>
        /// Mantiene la rappresentante migliore di una singola famiglia temporale equivalente
        /// </summary>
        private sealed class TemporalPairFamily
        {
            /// <summary>
            /// Inizializza una famiglia dalla prima coppia osservata
            /// </summary>
            /// <param name="representative">Prima coppia assegnata alla famiglia</param>
            /// <param name="offsetMs">Offset temporale della rappresentante</param>
            /// <param name="uncertaintyMs">Incertezza temporale della rappresentante</param>
            public TemporalPairFamily(DeepSiftAcceptedPairDiagnostic representative, double offsetMs, double uncertaintyMs)
            {
                this.Representative = representative;
                this.OffsetMs = offsetMs;
                this.UncertaintyMs = uncertaintyMs;
            }

            /// <summary>
            /// Aggiorna la famiglia e sostituisce la rappresentante se la nuova coppia è preferibile
            /// </summary>
            /// <param name="pair">Nuova coppia da valutare</param>
            /// <param name="offsetMs">Offset della nuova coppia</param>
            /// <param name="uncertaintyMs">Incertezza della nuova coppia</param>
            public void Consider(DeepSiftAcceptedPairDiagnostic pair, double offsetMs, double uncertaintyMs)
            {
                this.UncertaintyMs = Math.Max(this.UncertaintyMs, uncertaintyMs);
                if (CompareConfidence(pair, this.Representative) <= 0)
                    return;
                this.Representative = pair;
                this.OffsetMs = offsetMs;
            }

            /// <summary>
            /// Indica se la rappresentante corrente è preferibile a quella di un'altra famiglia
            /// </summary>
            /// <param name="alternative">Famiglia alternativa</param>
            /// <returns>True quando la famiglia corrente è preferibile</returns>
            public bool IsBetterThan(TemporalPairFamily alternative)
            {
                return CompareConfidence(this.Representative, alternative.Representative) > 0;
            }

            /// <summary>
            /// Rappresentante corrente della famiglia
            /// </summary>
            public DeepSiftAcceptedPairDiagnostic Representative { get; private set; }

            /// <summary>
            /// Offset temporale associato alla rappresentante corrente
            /// </summary>
            public double OffsetMs { get; private set; }

            /// <summary>
            /// Massima incertezza temporale osservata nella famiglia
            /// </summary>
            public double UncertaintyMs { get; private set; }

            /// <summary>
            /// Confronta il punteggio di confidenza e i PTS di due rappresentanti
            /// </summary>
            /// <param name="candidate">Rappresentante candidata</param>
            /// <param name="alternative">Rappresentante corrente</param>
            /// <returns>Valore positivo quando la candidata è preferibile</returns>
            private static int CompareConfidence(DeepSiftAcceptedPairDiagnostic candidate, DeepSiftAcceptedPairDiagnostic alternative)
            {
                int comparison = DeepSiftTemporalMetricComparer.QuantizeMetric(candidate.Score).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMetric(alternative.Score));
                if (comparison != 0)
                    return comparison;
                comparison = DeepSiftTemporalMetricComparer.QuantizeMilliseconds(alternative.SourcePtsMs).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.SourcePtsMs));
                if (comparison != 0)
                    return comparison;
                return DeepSiftTemporalMetricComparer.QuantizeMilliseconds(alternative.LanguagePtsMs).CompareTo(DeepSiftTemporalMetricComparer.QuantizeMilliseconds(candidate.LanguagePtsMs));
            }
        }

        #endregion
    }
}
