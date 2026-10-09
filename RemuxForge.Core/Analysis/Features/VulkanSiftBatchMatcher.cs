using RemuxForge.Core.Models;
using RemuxForge.Core.Localization;
using RemuxForge.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace RemuxForge.Core.Analysis.Features
{
    /// <summary>
    /// Backend batch SIFT con matching reciproco e RANSAC interamente eseguiti tramite Vulkan
    /// </summary>
    public sealed class VulkanSiftBatchMatcher : FrameFeatureBatchMatcherBase
    {
        #region Costanti

        /// <summary>
        /// Identificativo stabile del backend SIFT Vulkan
        /// </summary>
        public const string BACKEND_NAME = "vortice-vulkan-sift-ransac";

        /// <summary>
        /// Numero massimo di carichi Vulkan mantenuti contemporaneamente in volo
        /// </summary>
        private const int MAXIMUM_IN_FLIGHT_WORKLOADS = 3;

        /// <summary>
        /// Zero delega al runtime Vulkan il budget disponibile esposto dal driver
        /// </summary>
        private const ulong MAXIMUM_VRAM_BYTES = 0UL;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Opzioni SIFT condivise con gli altri backend di matching
        /// </summary>
        private readonly FrameFeatureMatcherOptions _options;

        /// <summary>
        /// Sincronizza il controllo di disponibilità e il rilascio delle risorse Vulkan
        /// </summary>
        private readonly object _availabilityLock;

        /// <summary>
        /// Serializza l'esecuzione e il rilascio della pipeline Vulkan
        /// </summary>
        private readonly object _executionLock;

        /// <summary>
        /// Contesto Vulkan persistente posseduto dal matcher
        /// </summary>
        private VulkanVisionContext _context;

        /// <summary>
        /// Pipeline SIFT Vulkan persistente posseduta dal matcher
        /// </summary>
        private VulkanSiftPipeline _pipeline;

        /// <summary>
        /// Motivo diagnostico dell'indisponibilità del backend
        /// </summary>
        private string _availabilityRejectReason;

        /// <summary>
        /// Indica che il controllo di disponibilità è già stato eseguito
        /// </summary>
        private bool _availabilityChecked;

        /// <summary>
        /// Indica che il matcher è stato disposto e non può più essere utilizzato
        /// </summary>
        private bool _disposed;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore con opzioni condivise dal percorso globale
        /// </summary>
        /// <param name="options">Opzioni SIFT condivise con il backend CPU</param>
        public VulkanSiftBatchMatcher(FrameFeatureMatcherOptions options = null)
        {
            this._options = options ?? new FrameFeatureMatcherOptions();
            this._availabilityLock = new object();
            this._executionLock = new object();
            this._availabilityRejectReason = "";
            this._options.Validate();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Verifica e inizializza una sola volta il contesto e la pipeline Vulkan persistenti
        /// </summary>
        /// <param name="rejectReason">Motivo della mancata disponibilità</param>
        /// <returns>True se il backend è disponibile</returns>
        public override bool IsAvailable(out string rejectReason)
        {
            lock (this._availabilityLock)
            {
                if (this._disposed)
                {
                    rejectReason = AppText.T("deep.temporal.matcher.vulkanDisposed");
                    return false;
                }

                if (!this._availabilityChecked)
                {
                    try
                    {
                        VulkanVisionOptions options = new VulkanVisionOptions();
                        options.MaximumInFlightWorkloads = MAXIMUM_IN_FLIGHT_WORKLOADS;
                        options.MaximumVramBytes = MAXIMUM_VRAM_BYTES;
                        this._context = new VulkanVisionContext(options);
                        this._pipeline = this._context.CreateSiftPipeline();
                    }
                    catch (Exception ex)
                    {
                        this._pipeline?.Dispose();
                        this._pipeline = null;
                        this._context?.Dispose();
                        this._context = null;
                        this._availabilityRejectReason = ex.Message;
                    }

                    this._availabilityChecked = true;
                }

                rejectReason = this._availabilityRejectReason;
                return this._pipeline != null;
            }
        }

        /// <summary>
        /// Costruisce la matrice dei match usando la pipeline Vulkan
        /// </summary>
        /// <param name="sourceAnchors">Ancore della timeline source</param>
        /// <param name="languageAnchors">Ancore della timeline language</param>
        /// <param name="maxDegreeOfParallelism">Parallelismo massimo dichiarato</param>
        /// <param name="cancellationToken">Token di cancellazione</param>
        /// <returns>Matrice dei match e diagnostica del batch</returns>
        public override DeepSiftBatchMatchResult BuildMatrix(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors, int maxDegreeOfParallelism, CancellationToken cancellationToken)
        {
            if (sourceAnchors == null)
                throw new ArgumentNullException(nameof(sourceAnchors));
            if (languageAnchors == null)
                throw new ArgumentNullException(nameof(languageAnchors));
            if (maxDegreeOfParallelism < 1)
                throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));

            DeepSiftBatchMatchResult result = this.CreateInitialResult();
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                this.ValidateAnchors(sourceAnchors);
                this.ValidateAnchors(languageAnchors);
                if (!this.IsAvailable(out string rejectReason))
                {
                    result.RejectReason = AppText.F("deep.temporal.matcher.vulkanUnavailable", rejectReason);
                    return result;
                }

                cancellationToken.ThrowIfCancellationRequested();
                VulkanSiftBatchResult batch;
                lock (this._executionLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    VulkanSiftBatchRequest request = this.CreateRequest(sourceAnchors, languageAnchors);
                    batch = this._pipeline.Execute(request, cancellationToken);
                }

                this.PopulateResult(result, batch, sourceAnchors, languageAnchors);
                result.MatchingMs = stopwatch.ElapsedMilliseconds;
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.RejectReason = AppText.T("deep.temporal.matcher.vulkanCancelled");
                return result;
            }
            catch (Exception ex)
            {
                result.RejectReason = AppText.F("deep.temporal.matcher.vulkanFailed", ex);
                return result;
            }
        }

        /// <summary>
        /// Rilascia pipeline e contesto dopo l'eventuale esecuzione attiva
        /// </summary>
        public override void Dispose()
        {
            lock (this._availabilityLock)
            {
                if (this._disposed)
                    return;
                this._disposed = true;
                lock (this._executionLock)
                {
                    this._pipeline?.Dispose();
                    this._pipeline = null;
                    this._context?.Dispose();
                    this._context = null;
                }
            }
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome backend stabile
        /// </summary>
        public override string BackendName { get { return BACKEND_NAME; } }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Inizializza il risultato con l'identificativo del backend
        /// </summary>
        /// <returns>Risultato iniziale del batch</returns>
        private DeepSiftBatchMatchResult CreateInitialResult()
        {
            DeepSiftBatchMatchResult result = new DeepSiftBatchMatchResult();
            result.BackendName = this.BackendName;
            return result;
        }

        /// <summary>
        /// Costruisce una richiesta Vulkan completa con tutte le coppie fra le due timeline
        /// </summary>
        /// <param name="sourceAnchors">Ancore source da caricare</param>
        /// <param name="languageAnchors">Ancore language da caricare</param>
        /// <returns>Richiesta batch completa</returns>
        private VulkanSiftBatchRequest CreateRequest(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors)
        {
            List<VulkanImageFrame> sourceFrames = this.CreateFrames(sourceAnchors);
            List<VulkanImageFrame> languageFrames = this.CreateFrames(languageAnchors);
            List<VulkanFramePair> pairs = new List<VulkanFramePair>(checked(sourceFrames.Count * languageFrames.Count));
            for (int sourceIndex = 0; sourceIndex < sourceFrames.Count; sourceIndex++)
            {
                for (int languageIndex = 0; languageIndex < languageFrames.Count; languageIndex++)
                {
                    VulkanFramePair pair = new VulkanFramePair();
                    pair.FirstFrameIndex = sourceIndex;
                    pair.SecondFrameIndex = languageIndex;
                    pairs.Add(pair);
                }
            }

            VulkanSiftOptions options = this.CreateVulkanOptions(sourceAnchors, languageAnchors);
            VulkanSiftBatchRequest request = new VulkanSiftBatchRequest(sourceFrames, languageFrames, pairs, options);
            request.OmitFeaturelessPairResults = true;

            return request;
        }

        /// <summary>
        /// Converte le ancore in frame grayscale pronti per la pipeline Vulkan
        /// </summary>
        /// <param name="anchors">Ancore da convertire</param>
        /// <returns>Frame Vulkan nello stesso ordine delle ancore</returns>
        private List<VulkanImageFrame> CreateFrames(IReadOnlyList<DeepSiftVisualAnchor> anchors)
        {
            List<VulkanImageFrame> result = new List<VulkanImageFrame>(anchors.Count);
            for (int i = 0; i < anchors.Count; i++)
                result.Add(new VulkanImageFrame(this.GetStableFrameIdentifier(anchors[i]), anchors[i].Frame, anchors[i].Width, anchors[i].Height, anchors[i].Width, VulkanPixelFormat.Gray8));
            return result;
        }

        /// <summary>
        /// Traduce le opzioni condivise nel formato richiesto dalla pipeline SIFT Vulkan
        /// </summary>
        /// <param name="sourceAnchors">Ancore source usate per il rapporto delle feature</param>
        /// <param name="languageAnchors">Ancore language usate per il rapporto delle feature</param>
        /// <returns>Opzioni SIFT configurate per la richiesta Vulkan</returns>
        private VulkanSiftOptions CreateVulkanOptions(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors)
        {
            VulkanSiftOptions result = new VulkanSiftOptions();
            result.OctaveLayers = this._options.OctaveLayers;
            result.ContrastThreshold = (float)this._options.ContrastThreshold;
            result.EdgeThreshold = (float)this._options.EdgeThreshold;
            result.Sigma = (float)this._options.Sigma;
            result.DoubleInput = this._options.DoubleInput;
            result.FeatureRatio = this.CalculateFeatureRatio(sourceAnchors, languageAnchors, result.DoubleInput);
            result.MaximumFeaturesPerFrame = this._options.MaxFeatures;
            result.LoweRatio = (float)this._options.LoweRatio;
            result.MinimumKeypointsPerFrame = this._options.MinKeypoints;
            result.MinimumReciprocalMatches = this._options.MinReciprocalMatches;
            result.MinimumInliers = this._options.MinInliers;
            result.MinimumInlierRatio = (float)this._options.MinInlierRatio;
            result.MinimumCoverage = (float)this._options.MinCoverage;
            result.MaximumMeanReprojectionError = (float)this._options.MaxMeanReprojectionError;
            result.MinimumHomographyAreaRatio = (float)this._options.MinHomographyAreaRatio;
            result.MaximumHomographyAreaRatio = (float)this._options.MaxHomographyAreaRatio;
            result.RansacReprojectionThreshold = (float)this._options.RansacReprojectionThreshold;
            return result;
        }

        /// <summary>
        /// Calcola il rapporto di scala necessario a rispettare il limite di feature per frame
        /// </summary>
        /// <param name="sourceAnchors">Ancore source da considerare</param>
        /// <param name="languageAnchors">Ancore language da considerare</param>
        /// <param name="doubleInput">Indica se la piramide usa l'ingresso raddoppiato</param>
        /// <returns>Rapporto di feature compreso fra il limite minimo e uno</returns>
        private float CalculateFeatureRatio(IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors, bool doubleInput)
        {
            long maximumOctaveArea = 1;
            for (int i = 0; i < sourceAnchors.Count; i++)
                maximumOctaveArea = Math.Max(maximumOctaveArea, this.CalculateOctaveArea(sourceAnchors[i].Width, sourceAnchors[i].Height, doubleInput));
            for (int i = 0; i < languageAnchors.Count; i++)
                maximumOctaveArea = Math.Max(maximumOctaveArea, this.CalculateOctaveArea(languageAnchors[i].Width, languageAnchors[i].Height, doubleInput));
            return Math.Min(1.0f, Math.Max(1.0f / maximumOctaveArea, (float)this._options.MaxFeatures / maximumOctaveArea));
        }

        /// <summary>
        /// Somma l'area dei livelli della piramide che rispettano la dimensione minima
        /// </summary>
        /// <param name="width">Larghezza del frame</param>
        /// <param name="height">Altezza del frame</param>
        /// <param name="doubleInput">Indica se raddoppiare le dimensioni iniziali</param>
        /// <returns>Area complessiva dei livelli utilizzabili</returns>
        private long CalculateOctaveArea(int width, int height, bool doubleInput)
        {
            long result = 0;
            int octaveWidth = doubleInput ? checked(width * 2) : width;
            int octaveHeight = doubleInput ? checked(height * 2) : height;
            while (octaveWidth >= 8 && octaveHeight >= 8)
            {
                result = checked(result + ((long)octaveWidth * octaveHeight));
                octaveWidth /= 2;
                octaveHeight /= 2;
            }
            return Math.Max(1, result);
        }

        /// <summary>
        /// Popola il risultato completo rimuovendo dalla matrice le ancore prive di feature
        /// </summary>
        /// <param name="result">Risultato da completare</param>
        /// <param name="batch">Risultato prodotto dalla pipeline Vulkan</param>
        /// <param name="sourceAnchors">Ancore source originali</param>
        /// <param name="languageAnchors">Ancore language originali</param>
        private void PopulateResult(DeepSiftBatchMatchResult result, VulkanSiftBatchResult batch, IReadOnlyList<DeepSiftVisualAnchor> sourceAnchors, IReadOnlyList<DeepSiftVisualAnchor> languageAnchors)
        {
            int[] sourceKeypointCounts = this.CopyFrameCounts(batch.FirstFrameKeypointCounts, sourceAnchors.Count);
            int[] languageKeypointCounts = this.CopyFrameCounts(batch.SecondFrameKeypointCounts, languageAnchors.Count);

            List<int> activeSourceIndexes = this.GetActiveIndexes(sourceKeypointCounts);
            List<int> activeLanguageIndexes = this.GetActiveIndexes(languageKeypointCounts);
            Dictionary<int, int> sourceMatrixIndexes = this.CreateMatrixIndexes(activeSourceIndexes);
            Dictionary<int, int> languageMatrixIndexes = this.CreateMatrixIndexes(activeLanguageIndexes);
            result.SourceFeaturelessAnchorCount = sourceAnchors.Count - activeSourceIndexes.Count;
            result.LanguageFeaturelessAnchorCount = languageAnchors.Count - activeLanguageIndexes.Count;
            result.SourceAnchors = this.GetActiveAnchors(sourceAnchors, activeSourceIndexes);
            result.LanguageAnchors = this.GetActiveAnchors(languageAnchors, activeLanguageIndexes);
            result.SourceAnchorCount = result.SourceAnchors.Count;
            result.LanguageAnchorCount = result.LanguageAnchors.Count;
            result.Matrix = new DeepSiftMatchMatrix(result.SourceAnchorCount, result.LanguageAnchorCount);

            for (int i = 0; i < batch.PairResults.Count; i++)
            {
                VulkanSiftPairResult pairResult = batch.PairResults[i];
                if (pairResult.Status != VulkanSiftPairStatus.Accepted)
                    this.AddRejectionCount(result, pairResult.RejectReason.ToString());
                if (!sourceMatrixIndexes.TryGetValue(pairResult.Pair.FirstFrameIndex, out int sourceIndex) || !languageMatrixIndexes.TryGetValue(pairResult.Pair.SecondFrameIndex, out int languageIndex))
                    continue;
                DeepSiftMatchCell cell = this.CreateCell(pairResult);
                result.Matrix.Set(sourceIndex, languageIndex, cell);
                if (cell.State == DeepSiftMatchState.Accepted)
                    this.AddAcceptedPair(result, sourceIndex, languageIndex, cell, this.CopyHomography(pairResult.Homography));
            }

            this.UpdateMatrixCounters(result.Matrix, batch.Diagnostics.ProcessedPairCount);
            result.ProcessedCellCount = result.Matrix.ProcessedCellCount;
            result.AcceptedCellCount = result.Matrix.AcceptedCellCount;
            result.UploadMs = this.ToMilliseconds(batch.Diagnostics.UploadTicks);
            result.FeatureExtractionMs = this.ToMilliseconds(batch.Diagnostics.DescriptorTicks);
            result.DescriptorMatchingMs = this.ToMilliseconds(batch.Diagnostics.MatchingTicks);
            result.GeometryMs = this.ToMilliseconds(batch.Diagnostics.RansacTicks);
            result.ReadbackMs = this.ToMilliseconds(batch.Diagnostics.ReadbackTicks);
        }

        /// <summary>
        /// Copia i conteggi dei keypoint dopo averne verificato la cardinalità attesa
        /// </summary>
        /// <param name="counts">Conteggi restituiti dalla pipeline Vulkan</param>
        /// <param name="expectedCount">Numero atteso di frame</param>
        /// <returns>Copia indicizzata dei conteggi</returns>
        private int[] CopyFrameCounts(IReadOnlyList<int> counts, int expectedCount)
        {
            if (counts == null || counts.Count != expectedCount)
                throw new InvalidOperationException(AppText.T("deep.temporal.matcher.inconsistentVulkanFrameCounts"));
            int[] result = new int[expectedCount];
            for (int i = 0; i < expectedCount; i++)
                result[i] = counts[i];
            return result;
        }

        /// <summary>
        /// Converte i tick del cronometro ad alta risoluzione in millisecondi arrotondati
        /// </summary>
        /// <param name="ticks">Durata espressa in tick</param>
        /// <returns>Durata in millisecondi</returns>
        private long ToMilliseconds(long ticks)
        {
            return (long)Math.Round(ticks * 1000.0 / Stopwatch.Frequency);
        }

        /// <summary>
        /// Traduce il risultato di una coppia Vulkan nella cella della matrice condivisa
        /// </summary>
        /// <param name="match">Risultato della coppia prodotto dalla pipeline</param>
        /// <returns>Cella di matching con stato e metriche geometriche</returns>
        private DeepSiftMatchCell CreateCell(VulkanSiftPairResult match)
        {
            DeepSiftMatchCell result = new DeepSiftMatchCell();
            result.State = match.Status == VulkanSiftPairStatus.Accepted ? DeepSiftMatchState.Accepted : DeepSiftMatchState.Rejected;
            result.Score = match.Score;
            return result;
        }

        /// <summary>
        /// Incrementa il contatore deterministico del motivo di rifiuto Vulkan
        /// </summary>
        /// <param name="result">Risultato batch da aggiornare</param>
        /// <param name="reason">Motivo esposto dalla pipeline Vulkan</param>
        private void AddRejectionCount(DeepSiftBatchMatchResult result, string reason)
        {
            string key = string.IsNullOrEmpty(reason) ? "Unknown" : reason;
            result.RejectionCounts.TryGetValue(key, out int count);
            result.RejectionCounts[key] = count + 1;
        }

        /// <summary>
        /// Converte l'omografia Vulkan in precisione doppia per il contratto condiviso
        /// </summary>
        /// <param name="homography">Omografia Vulkan row-major</param>
        /// <returns>Omografia convertita, oppure null</returns>
        private double[] CopyHomography(float[] homography)
        {
            if (homography == null)
                return null;
            double[] result = new double[homography.Length];
            for (int i = 0; i < homography.Length; i++)
                result[i] = homography[i];
            return result;
        }

        /// <summary>
        /// Seleziona gli indici delle ancore che superano la soglia minima di keypoint
        /// </summary>
        /// <param name="keypointCounts">Numero di keypoint per ancora</param>
        /// <returns>Indici delle ancore con feature sufficienti</returns>
        private List<int> GetActiveIndexes(int[] keypointCounts)
        {
            List<int> result = new List<int>();
            for (int i = 0; i < keypointCounts.Length; i++)
            {
                if (keypointCounts[i] >= this._options.MinKeypoints)
                    result.Add(i);
            }
            return result;
        }

        /// <summary>
        /// Crea la mappa dagli indici originali degli elementi attivi alle posizioni della matrice
        /// </summary>
        /// <param name="activeIndexes">Indici originali degli elementi attivi</param>
        /// <returns>Mappa da indice originale a indice della matrice</returns>
        private Dictionary<int, int> CreateMatrixIndexes(List<int> activeIndexes)
        {
            Dictionary<int, int> result = new Dictionary<int, int>(activeIndexes.Count);
            for (int i = 0; i < activeIndexes.Count; i++)
                result.Add(activeIndexes[i], i);
            return result;
        }

        #endregion
    }
}
