using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Edit
{
    /// <summary>
    /// Calcola i dHash dei fotogrammi e misura quanto ogni offset di una griglia ne spiega
    /// </summary>
    internal abstract class HashBackendBase : IDisposable
    {
        #region Metodi pubblici

        /// <summary>
        /// Crea il backend richiesto senza fallback impliciti
        /// </summary>
        /// <param name="backend">Backend di visione configurato</param>
        /// <returns>Backend degli hash del tipo richiesto</returns>
        public static HashBackendBase Create(VisionBackendKind backend)
        {
            switch (backend)
            {
                case VisionBackendKind.Cpu:
                    return new CpuHashBackend();
                case VisionBackendKind.Vulkan:
                    return new VulkanHashBackend();
                default:
                    throw new InvalidOperationException(AppText.F("deep.temporal.hashBackend.unsupportedBackend", backend));
            }
        }

        /// <summary>
        /// Verifica che il backend sia utilizzabile nella sessione corrente
        /// </summary>
        /// <param name="rejectReason">Motivo della mancata disponibilità</param>
        /// <returns>True se il backend è disponibile, altrimenti false</returns>
        public abstract bool IsAvailable(out string rejectReason);

        /// <summary>
        /// Calcola i dHash di un blocco di quadrati di analisi contigui
        /// </summary>
        /// <param name="frames">Quadrati grigi di analisi, uno dopo l'altro</param>
        /// <param name="frameCount">Quadrati effettivamente contenuti nel blocco</param>
        /// <param name="hash0">Accumulatore dei dHash orizzontali</param>
        /// <param name="hash1">Accumulatore dei dHash verticali</param>
        public abstract void Hash(byte[] frames, int frameCount, List<ulong> hash0, List<ulong> hash1);

        /// <summary>
        /// Deriva tutti i segnali visuali di un blocco di quadrati di analisi contigui
        /// </summary>
        /// <param name="frames">Quadrati grigi di analisi, uno dopo l'altro</param>
        /// <param name="frameCount">Quadrati effettivamente contenuti nel blocco</param>
        /// <param name="hash0">Accumulatore dei dHash orizzontali</param>
        /// <param name="hash1">Accumulatore dei dHash verticali</param>
        /// <param name="lumaMean">Accumulatore delle luminanze medie</param>
        /// <param name="thumbStd">Accumulatore delle deviazioni standard delle miniature</param>
        /// <param name="thumbPixels">Accumulatore dei pixel delle miniature 12x12</param>
        public abstract void Analyze(byte[] frames, int frameCount, List<ulong> hash0, List<ulong> hash1, List<float> lumaMean, List<float> thumbStd, List<byte> thumbPixels);

        /// <summary>
        /// Lega il backend alla coppia su cui misurerà, una volta sola
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        public abstract void Attach(PairSignals pair);

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
        public abstract double[] Scan(int firstIndex, int stride, int indexCount, double firstOffsetMs, double stepMs, int candidateCount, int radius, int threshold);

        /// <summary>
        /// Rilascia le risorse possedute dal backend
        /// </summary>
        public abstract void Dispose();

        #endregion

        #region Proprietà

        /// <summary>
        /// Quadrati di analisi che conviene consegnare a <see cref="Hash"/> in un colpo solo
        /// </summary>
        public abstract int BatchFrames { get; }

        #endregion
    }
}
