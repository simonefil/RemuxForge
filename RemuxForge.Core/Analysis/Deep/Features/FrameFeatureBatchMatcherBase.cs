using RemuxForge.Core.Models;
using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Core.Analysis.Deep.Features
{
    /// <summary>
    /// Definisce le operazioni comuni ai backend che estraggono feature SIFT
    /// e costruiscono matrici di corrispondenza tra ancore video
    /// </summary>
    public abstract class FrameFeatureBatchMatcherBase : IDisposable
    {
        #region Metodi pubblici

        /// <summary>
        /// Verifica che il backend sia utilizzabile nella sessione corrente
        /// e restituisce l'eventuale motivo di indisponibilità
        /// </summary>
        /// <param name="rejectReason">Motivo della mancata disponibilità</param>
        /// <returns>True se il backend è disponibile, altrimenti false</returns>
        public abstract bool IsAvailable(out string rejectReason);

        /// <summary>
        /// Estrae le feature e valuta le coppie richieste tra le ancore source e language
        /// costruendo la matrice dei risultati e la relativa diagnostica
        /// </summary>
        /// <param name="sourceAnchors">Ancore della timeline source ordinate per PTS</param>
        /// <param name="languageAnchors">Ancore della timeline language ordinate per PTS</param>
        /// <param name="maxDegreeOfParallelism">Numero massimo di worker utilizzabili dal backend</param>
        /// <param name="cancellationToken">Token per richiedere l'annullamento cooperativo</param>
        /// <param name="progress">Destinatario opzionale degli aggiornamenti di progresso</param>
        /// <param name="plannedPairs">Coppie sparse da valutare, oppure null per valutare tutte le combinazioni</param>
        /// <returns>Risultato del matching con matrice, contatori e diagnostica del batch</returns>
        public abstract DeepSiftBatchMatchResult BuildMatrix(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors, int maxDegreeOfParallelism, CancellationToken cancellationToken, IProgress<DeepSiftBatchProgress> progress = null, IReadOnlyList<DeepSiftFramePair> plannedPairs = null);

        /// <summary>
        /// Apre uno scope locale nel quale il backend può riutilizzare le feature
        /// dei frame già incontrati tra batch consecutivi
        /// </summary>
        public virtual void BeginFeatureReuseScope()
        {
        }

        /// <summary>
        /// Chiude lo scope locale e rilascia le feature persistenti
        /// possedute dal backend per il riuso tra batch
        /// </summary>
        public virtual void EndFeatureReuseScope()
        {
        }

        /// <summary>
        /// Rilascia le risorse possedute dal backend
        /// al termine del suo ciclo di vita
        /// </summary>
        public abstract void Dispose();

        #endregion

        #region Metodi protected

        /// <summary>
        /// Verifica che tutte le ancore contengano dimensioni valide
        /// e un buffer SIFT coerente con la relativa geometria
        /// </summary>
        /// <param name="anchors">Ancore da validare</param>
        protected void ValidateAnchors(IReadOnlyList<DeepSiftVisualAnchor> anchors)
        {
            for (int i = 0; i < anchors.Count; i++)
            {
                if (anchors[i] == null)
                    throw new ArgumentException(AppText.T("deep.temporal.matcher.nullAnchor"), nameof(anchors));
                if (anchors[i].Width <= 0 || anchors[i].Height <= 0)
                    throw new ArgumentException(AppText.T("deep.temporal.matcher.invalidAnchorDimensions"), nameof(anchors));
                if (anchors[i].Frame == null || anchors[i].Frame.Length != anchors[i].Width * anchors[i].Height)
                    throw new ArgumentException(AppText.T("deep.temporal.matcher.inconsistentAnchorBuffer"), nameof(anchors));
            }
        }

        /// <summary>
        /// Calcola dal PTS un identificatore deterministico indipendente
        /// da finestra, tile e ordine di dispatch
        /// </summary>
        /// <param name="anchor">Ancora dalla quale ricavare l'identificatore</param>
        /// <returns>Identificatore stabile del frame</returns>
        protected int GetStableFrameIdentifier(DeepSiftVisualAnchor anchor)
        {
            long ptsMicroseconds = checked((long)Math.Round(anchor.PtsMs * 1000.0));
            ulong value = unchecked((ulong)ptsMicroseconds);
            value ^= value >> 33;
            value *= 0xff51afd7ed558ccdUL;
            value ^= value >> 33;
            return (int)(value & 0x7fffffffUL);
        }

        /// <summary>
        /// Combina gli identificatori stabili dei due frame
        /// per inizializzare in modo riproducibile il RANSAC CPU
        /// </summary>
        /// <param name="source">Ancora del frame source</param>
        /// <param name="language">Ancora del frame language</param>
        /// <returns>Seed stabile per la coppia di frame</returns>
        protected int GetStablePairSeed(DeepSiftVisualAnchor source, DeepSiftVisualAnchor language)
        {
            return unchecked((this.GetStableFrameIdentifier(source) * 397) ^ this.GetStableFrameIdentifier(language));
        }

        /// <summary>
        /// Proietta gli indici attivi sulle ancore originali
        /// ricalcolando la durata dell'intervallo rappresentato da ciascuna ancora
        /// </summary>
        /// <param name="anchors">Sequenza originale delle ancore ordinate per PTS</param>
        /// <param name="indexes">Indici delle ancore da mantenere</param>
        /// <returns>Nuova sequenza contenente le sole ancore attive</returns>
        protected List<DeepSiftVisualAnchor> GetActiveAnchors(IReadOnlyList<DeepSiftVisualAnchor> anchors, List<int> indexes)
        {
            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>(indexes.Count);
            for (int i = 0; i < indexes.Count; i++)
            {
                DeepSiftVisualAnchor source = anchors[indexes[i]];
                double endPtsMs;
                if (i + 1 < indexes.Count)
                    endPtsMs = anchors[indexes[i + 1]].PtsMs;
                else
                {
                    DeepSiftVisualAnchor last = anchors[anchors.Count - 1];
                    endPtsMs = last.PtsMs + last.DurationMs;
                }

                DeepSiftVisualAnchor active = new DeepSiftVisualAnchor();
                active.Index = source.Index;
                active.FrameIndex = source.FrameIndex;
                active.PtsMs = source.PtsMs;
                active.DurationMs = endPtsMs > source.PtsMs ? endPtsMs - source.PtsMs : source.DurationMs;
                active.FrameDurationMs = source.FrameDurationMs;
                active.Frame = source.Frame;
                active.Width = source.Width;
                active.Height = source.Height;
                result.Add(active);
            }
            return result;
        }

        /// <summary>
        /// Completa i contatori della matrice dopo la scrittura delle celle
        /// usando il numero di celle elaborate fornito dal chiamante
        /// </summary>
        /// <param name="matrix">Matrice i cui stati devono essere conteggiati</param>
        /// <param name="processed">Numero di celle elaborate durante il batch</param>
        protected void UpdateMatrixCounters(DeepSiftMatchMatrix matrix, long processed)
        {
            int accepted = 0;
            for (int sourceIndex = 0; sourceIndex < matrix.SourceCount; sourceIndex++)
            {
                for (int languageIndex = 0; languageIndex < matrix.LanguageCount; languageIndex++)
                {
                    if (matrix.Get(sourceIndex, languageIndex).State == DeepSiftMatchState.Accepted)
                        accepted++;
                }
            }

            matrix.AcceptedCellCount = accepted;
            matrix.ProcessedCellCount = processed;
        }

        /// <summary>
        /// Materializza nel risultato le sole evidenze positive
        /// necessarie al replay temporale
        /// </summary>
        /// <param name="batch">Risultato del batch da arricchire con le coppie accettate</param>
        protected void PopulateAcceptedPairs(DeepSiftBatchMatchResult batch)
        {
            for (int sourceIndex = 0; sourceIndex < batch.Matrix.SourceCount; sourceIndex++)
            {
                for (int languageIndex = 0; languageIndex < batch.Matrix.LanguageCount; languageIndex++)
                {
                    DeepSiftMatchCell cell = batch.Matrix.Get(sourceIndex, languageIndex);
                    if (cell.State != DeepSiftMatchState.Accepted)
                        continue;

                    this.AddAcceptedPair(batch, sourceIndex, languageIndex, cell);
                }
            }
        }

        /// <summary>
        /// Converte una cella accettata in una diagnostica temporale
        /// e la aggiunge alle coppie accettate del batch
        /// </summary>
        /// <param name="batch">Risultato del batch che raccoglie la diagnostica</param>
        /// <param name="sourceIndex">Indice dell'ancora source nella matrice</param>
        /// <param name="languageIndex">Indice dell'ancora language nella matrice</param>
        /// <param name="cell">Cella accettata da convertire</param>
        protected void AddAcceptedPair(DeepSiftBatchMatchResult batch, int sourceIndex, int languageIndex, DeepSiftMatchCell cell)
        {
            DeepSiftAcceptedPairDiagnostic pair = new DeepSiftAcceptedPairDiagnostic();
            pair.SourceAnchorIndex = sourceIndex;
            pair.LanguageAnchorIndex = languageIndex;
            pair.SourcePtsMs = batch.SourceAnchors[sourceIndex].PtsMs;
            pair.LanguagePtsMs = batch.LanguageAnchors[languageIndex].PtsMs;
            pair.SourceFrameDurationMs = batch.SourceAnchors[sourceIndex].FrameDurationMs;
            pair.LanguageFrameDurationMs = batch.LanguageAnchors[languageIndex].FrameDurationMs;
            pair.SourceSamplingDurationMs = batch.SourceAnchors[sourceIndex].DurationMs;
            pair.LanguageSamplingDurationMs = batch.LanguageAnchors[languageIndex].DurationMs;
            pair.Score = cell.Score;
            pair.InlierCount = cell.InlierCount;
            pair.InlierRatio = cell.InlierRatio;
            pair.SourceCoverage = cell.SourceCoverage;
            pair.LanguageCoverage = cell.LanguageCoverage;
            pair.MeanReprojectionError = cell.MeanReprojectionError;
            batch.AcceptedPairs.Add(pair);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Restituisce l'identificativo stabile del backend
        /// usato nei risultati e nella diagnostica
        /// </summary>
        public abstract string BackendName { get; }

        /// <summary>
        /// Indica se il backend gestisce internamente una richiesta sparsa di grandi dimensioni
        /// senza tile aggiuntive create dall'host
        /// </summary>
        public virtual bool SupportsNativeSparseBatching { get { return false; } }

        #endregion
    }
}
