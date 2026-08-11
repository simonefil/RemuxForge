using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace RemuxForge.Core.Analysis.Deep.Features
{
    /// <summary>
    /// Base backend-neutral per descriptor e matrici SIFT
    /// </summary>
    public abstract class FrameFeatureBatchMatcherBase : IDisposable
    {
        #region Metodi pubblici

        /// <summary>
        /// Verifica che il backend sia utilizzabile
        /// </summary>
        /// <param name="rejectReason">Motivo della mancata disponibilità</param>
        /// <returns>True se il backend è disponibile</returns>
        public abstract bool IsAvailable(out string rejectReason);

        /// <summary>
        /// Estrae descriptor e valuta le coppie source-language richieste
        /// </summary>
        /// <param name="sourceAnchors">Ancore source ordinate per PTS</param>
        /// <param name="languageAnchors">Ancore language ordinate per PTS</param>
        /// <param name="maxDegreeOfParallelism">Numero massimo di worker</param>
        /// <param name="cancellationToken">Token cooperativo</param>
        /// <param name="progress">Destinatario opzionale del progresso</param>
        /// <param name="plannedPairs">Coppie sparse opzionali</param>
        /// <returns>Matrice e diagnostica del batch</returns>
        public abstract DeepSiftBatchMatchResult BuildMatrix(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors, int maxDegreeOfParallelism, CancellationToken cancellationToken, IProgress<DeepSiftBatchProgress> progress = null, IReadOnlyList<DeepSiftFramePair> plannedPairs = null);

        /// <summary>
        /// Rilascia le risorse possedute dal backend
        /// </summary>
        public abstract void Dispose();

        #endregion

        #region Metodi protected

        /// <summary>
        /// Verifica che tutte le ancore contengano buffer SIFT coerenti
        /// </summary>
        protected void ValidateAnchors(IReadOnlyList<DeepSiftVisualAnchor> anchors)
        {
            for (int i = 0; i < anchors.Count; i++)
            {
                if (anchors[i] == null)
                    throw new ArgumentException("La sequenza contiene un'ancora nulla", nameof(anchors));
                if (anchors[i].Width <= 0 || anchors[i].Height <= 0)
                    throw new ArgumentException("Un'ancora ha dimensioni SIFT non valide", nameof(anchors));
                if (anchors[i].Frame == null || anchors[i].Frame.Length != anchors[i].Width * anchors[i].Height)
                    throw new ArgumentException("Un'ancora ha un buffer SIFT non coerente", nameof(anchors));
            }
        }

        /// <summary>
        /// Proietta gli indici attivi sulle ancore originali
        /// </summary>
        protected List<DeepSiftVisualAnchor> GetActiveAnchors(IReadOnlyList<DeepSiftVisualAnchor> anchors, List<int> indexes)
        {
            List<DeepSiftVisualAnchor> result = new List<DeepSiftVisualAnchor>(indexes.Count);
            for (int i = 0; i < indexes.Count; i++)
                result.Add(anchors[indexes[i]]);
            return result;
        }

        /// <summary>
        /// Completa i contatori dopo la scrittura della matrice
        /// </summary>
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
        /// Materializza le sole evidenze positive necessarie al replay temporale
        /// </summary>
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
        /// Converte una cella accettata in evidenza temporale
        /// </summary>
        protected void AddAcceptedPair(DeepSiftBatchMatchResult batch, int sourceIndex, int languageIndex, DeepSiftMatchCell cell)
        {
            DeepSiftAcceptedPairDiagnostic pair = new DeepSiftAcceptedPairDiagnostic();
            pair.SourceAnchorIndex = sourceIndex;
            pair.LanguageAnchorIndex = languageIndex;
            pair.SourcePtsMs = batch.SourceAnchors[sourceIndex].PtsMs;
            pair.LanguagePtsMs = batch.LanguageAnchors[languageIndex].PtsMs;
            pair.SourceFrameDurationMs = batch.SourceAnchors[sourceIndex].FrameDurationMs;
            pair.LanguageFrameDurationMs = batch.LanguageAnchors[languageIndex].FrameDurationMs;
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
        /// Identificativo stabile del backend
        /// </summary>
        public abstract string BackendName { get; }

        #endregion
    }
}
