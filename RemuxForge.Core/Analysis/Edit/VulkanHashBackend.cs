using RemuxForge.Core.Analysis.Edit.Extraction;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Vulkan;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Edit
{
    /// <summary>
    /// dHash e scansione degli offset sulla GPU, con le due tracce residenti sul dispositivo
    /// </summary>
    internal class VulkanHashBackend : HashBackendBase
    {
        #region Costanti

        /// <summary>
        /// Carichi che il dispositivo può avere in volo contemporaneamente
        /// </summary>
        private const int MAXIMUM_IN_FLIGHT_WORKLOADS = 3;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Contesto Vulkan persistente
        /// </summary>
        private VulkanVisionContext _context;

        /// <summary>
        /// Pipeline degli hash creata dal contesto
        /// </summary>
        private VulkanHashPipeline _pipeline;

        /// <summary>
        /// Lotto che tiene residenti le due tracce
        /// </summary>
        private VulkanHashPreparedBatch _batch;

        /// <summary>
        /// Indica se la disponibilità è già stata verificata
        /// </summary>
        private bool _availabilityChecked;

        /// <summary>
        /// Motivo per cui il backend non è disponibile
        /// </summary>
        private string _availabilityRejectReason;

        /// <summary>
        /// Serializza i produttori che condividono la pipeline
        /// </summary>
        private readonly object _analysisLock = new object();

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Verifica che il backend sia utilizzabile nella sessione corrente
        /// </summary>
        /// <param name="rejectReason">Motivo della mancata disponibilità</param>
        /// <returns>True se il dispositivo e la pipeline si aprono</returns>
        public override bool IsAvailable(out string rejectReason)
        {
            if (!this._availabilityChecked)
            {
                try
                {
                    VulkanVisionOptions options = new VulkanVisionOptions();
                    options.MaximumInFlightWorkloads = MAXIMUM_IN_FLIGHT_WORKLOADS;
                    this._context = new VulkanVisionContext(options);
                    this._pipeline = this._context.CreateHashPipeline();
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

            rejectReason = this._availabilityRejectReason ?? "";
            return this._pipeline != null;
        }

        /// <summary>
        /// Calcola i dHash di un blocco di quadrati di analisi contigui
        /// </summary>
        /// <param name="frames">Quadrati grigi di analisi, uno dopo l'altro</param>
        /// <param name="frameCount">Quadrati effettivamente contenuti nel blocco</param>
        /// <param name="hash0">Accumulatore dei dHash orizzontali</param>
        /// <param name="hash1">Accumulatore dei dHash verticali</param>
        public override void Hash(byte[] frames, int frameCount, List<ulong> hash0, List<ulong> hash1)
        {
            if (!this.IsAvailable(out string rejectReason))
                throw new InvalidOperationException(AppText.F("deep.temporal.hashBackend.unavailable", VisionBackendKind.Vulkan, rejectReason));
            VulkanFrameHash[] hashes = this._pipeline.Extract(new ReadOnlySpan<byte>(frames, 0, frameCount * VulkanHashPipeline.FrameBytes));
            for (int i = 0; i < hashes.Length; i++)
            {
                hash0.Add(hashes[i].Horizontal);
                hash1.Add(hashes[i].Vertical);
            }
        }

        /// <summary>
        /// Deriva dHash, luminanza e miniature nella stessa dispatch Vulkan
        /// </summary>
        /// <param name="frames">Quadrati grigi di analisi, uno dopo l'altro</param>
        /// <param name="frameCount">Quadrati effettivamente contenuti nel blocco</param>
        /// <param name="hash0">Accumulatore dei dHash orizzontali</param>
        /// <param name="hash1">Accumulatore dei dHash verticali</param>
        /// <param name="lumaMean">Accumulatore delle luminanze medie</param>
        /// <param name="thumbStd">Accumulatore delle deviazioni standard delle miniature</param>
        /// <param name="thumbPixels">Accumulatore dei pixel delle miniature</param>
        public override void Analyze(byte[] frames, int frameCount, List<ulong> hash0, List<ulong> hash1, List<float> lumaMean, List<float> thumbStd, List<byte> thumbPixels)
        {
            lock (this._analysisLock)
            {
                if (!this.IsAvailable(out string rejectReason))
                    throw new InvalidOperationException(AppText.F("deep.temporal.hashBackend.unavailable", VisionBackendKind.Vulkan, rejectReason));
                VulkanFrameSignalBatch signals = this._pipeline.ExtractSignals(new ReadOnlySpan<byte>(frames, 0, frameCount * VulkanHashPipeline.FrameBytes));
                for (int i = 0; i < signals.Count; i++)
                {
                    hash0.Add(signals.Hashes[i].Horizontal);
                    hash1.Add(signals.Hashes[i].Vertical);
                }
                lumaMean.AddRange(signals.LumaMeans);
                thumbStd.AddRange(signals.ThumbnailStandardDeviations);
                thumbPixels.AddRange(signals.ThumbnailPixels);
            }
        }

        /// <summary>
        /// Carica le due tracce sul dispositivo, una volta sola per l'intera analisi
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        public override void Attach(PairSignals pair)
        {
            if (!this.IsAvailable(out string rejectReason))
                throw new InvalidOperationException(AppText.F("deep.temporal.hashBackend.unavailable", VisionBackendKind.Vulkan, rejectReason));
            this._batch = this._pipeline.Prepare(BuildTrack(pair.Source, pair.Source.PtsMs), BuildTrack(pair.Language, pair.LanguagePtsMs));
        }

        /// <summary>
        /// Frazione di fotogrammi spiegata da ogni offset della griglia
        /// </summary>
        /// <param name="firstIndex">Primo indice sorgente misurato</param>
        /// <param name="stride">Passo fra due indici sorgente consecutivi</param>
        /// <param name="indexCount">Quanti indici sorgente si misurano</param>
        /// <param name="firstOffsetMs">Primo offset candidato</param>
        /// <param name="stepMs">Passo fra due offset candidati</param>
        /// <param name="candidateCount">Quanti offset candidati</param>
        /// <param name="radius">Fotogrammi lang di tolleranza</param>
        /// <param name="threshold">Soglia di Hamming</param>
        /// <returns>Frazione spiegata di ogni candidato, nell'ordine della griglia</returns>
        public override double[] Scan(int firstIndex, int stride, int indexCount, double firstOffsetMs, double stepMs, int candidateCount, int radius, int threshold)
        {
            double[] fractions = new double[candidateCount];
            if (indexCount == 0)
                return fractions;

            VulkanHashScan scan = new VulkanHashScan(firstIndex, stride, indexCount, firstOffsetMs, stepMs, candidateCount, radius, threshold);
            VulkanHashScanResult result = this._batch.Execute(new[] { scan }).Scans[0];
            for (int candidate = 0; candidate < candidateCount; candidate++)
                fractions[candidate] = (double)result.ExplainedCounts[candidate] / indexCount;
            return fractions;
        }

        /// <summary>
        /// Rilascia il lotto residente, la pipeline e il contesto
        /// </summary>
        public override void Dispose()
        {
            this._batch?.Dispose();
            this._batch = null;
            this._pipeline?.Dispose();
            this._pipeline = null;
            this._context?.Dispose();
            this._context = null;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Traduce i segnali di una traccia nella forma che la pipeline tiene residente
        /// </summary>
        /// <param name="signals">Segnali della traccia</param>
        /// <param name="ptsMs">PTS della traccia nel dominio della sorgente</param>
        /// <returns>Traccia pronta per il dispositivo</returns>
        private static VulkanHashTrack BuildTrack(FrameSignals signals, double[] ptsMs)
        {
            VulkanFrameHash[] hashes = new VulkanFrameHash[signals.Count];
            for (int i = 0; i < signals.Count; i++)
                hashes[i] = new VulkanFrameHash(signals.Hash0[i], signals.Hash1[i]);
            return new VulkanHashTrack(hashes, ptsMs);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Un intero invio di estrazione: il dispositivo lavora un fotogramma per gruppo, e vuole gruppi
        /// </summary>
        public override int BatchFrames
        {
            get { return VulkanHashPipeline.FramesPerSubmission; }
        }

        #endregion
    }
}
