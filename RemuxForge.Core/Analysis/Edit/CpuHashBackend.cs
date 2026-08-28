using RemuxForge.Core.Analysis.Edit.Extraction;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Analysis.Edit
{
    /// <summary>
    /// dHash e scansione degli offset sul processore, sempre disponibili
    /// </summary>
    internal class CpuHashBackend : HashBackendBase
    {
        #region Costanti

        /// <summary>
        /// Numero di byte di un quadrato grigio di analisi
        /// </summary>
        private const int FRAME_BYTES = FrameSignals.SIDE * FrameSignals.SIDE;

        /// <summary>
        /// Lato del blocco che genera un pixel della miniatura
        /// </summary>
        private const int THUMB_BLOCK = FrameSignals.SIDE / FrameSignals.THUMB_SIDE;

        /// <summary>
        /// Numero di pixel della miniatura
        /// </summary>
        private const int THUMB_PIXELS = FrameSignals.THUMB_SIDE * FrameSignals.THUMB_SIDE;

        #endregion

        #region Variabili di classe

        /// <summary>
        /// Coppia su cui si misura
        /// </summary>
        private PairSignals _pair;

        /// <summary>
        /// Serializza i produttori che condividono questa istanza
        /// </summary>
        private readonly object _analysisLock = new object();

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Verifica che il backend sia utilizzabile nella sessione corrente
        /// </summary>
        /// <param name="rejectReason">Motivo della mancata disponibilità</param>
        /// <returns>Sempre true, il percorso sul processore non ha prerequisiti</returns>
        public override bool IsAvailable(out string rejectReason)
        {
            rejectReason = "";
            return true;
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
            for (int frame = 0; frame < frameCount; frame++)
            {
                int origin = frame * FRAME_BYTES;
                hash0.Add(ComputeHorizontalHash(frames, origin));
                hash1.Add(ComputeVerticalHash(frames, origin));
            }
        }

        /// <summary>
        /// Deriva dHash, luminanza e miniatura sul processore conservando l'ordine dei frame
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
                for (int frame = 0; frame < frameCount; frame++)
                {
                    int origin = frame * FRAME_BYTES;
                    hash0.Add(ComputeHorizontalHash(frames, origin));
                    hash1.Add(ComputeVerticalHash(frames, origin));
                    AppendMeasurements(frames, origin, lumaMean, thumbStd, thumbPixels);
                }
            }
        }

        /// <summary>
        /// Lega il backend alla coppia su cui misurerà
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        public override void Attach(PairSignals pair)
        {
            this._pair = pair;
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
            for (int candidate = 0; candidate < candidateCount; candidate++)
            {
                double offsetMs = firstOffsetMs + candidate * stepMs;
                int explained = 0;
                for (int slot = 0; slot < indexCount; slot++)
                {
                    if (HashOps.Distance(this._pair, firstIndex + slot * stride, offsetMs, radius) <= threshold)
                        explained++;
                }
                fractions[candidate] = (double)explained / indexCount;
            }
            return fractions;
        }

        /// <summary>
        /// Rilascia le risorse possedute dal backend
        /// </summary>
        public override void Dispose()
        {
            this._pair = null;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Calcola il dHash orizzontale confrontando colonne adiacenti di una griglia 8x9
        /// </summary>
        /// <param name="frames">Blocco che contiene il quadrato di analisi</param>
        /// <param name="origin">Primo byte del quadrato dentro il blocco</param>
        /// <returns>64 bit impacchettati con il primo confronto nel bit più significativo</returns>
        private static ulong ComputeHorizontalHash(byte[] frames, int origin)
        {
            // Le medie di 72 pixel interi sono somme esatte: confrontarle è confrontare gli interi
            int[] cells = new int[8 * 9];
            for (int row = 0; row < FrameSignals.SIDE; row++)
            {
                int cellRow = row / 9;
                int offset = origin + row * FrameSignals.SIDE;
                for (int column = 0; column < FrameSignals.SIDE; column++)
                    cells[cellRow * 9 + column / 8] += frames[offset + column];
            }

            ulong result = 0UL;
            int bit = 0;
            for (int cellRow = 0; cellRow < 8; cellRow++)
            {
                for (int cellColumn = 0; cellColumn < 8; cellColumn++)
                {
                    if (cells[cellRow * 9 + cellColumn + 1] > cells[cellRow * 9 + cellColumn])
                        result |= 1UL << (63 - bit);
                    bit++;
                }
            }

            return result;
        }

        /// <summary>
        /// Calcola il dHash verticale confrontando righe adiacenti di una griglia 9x8
        /// </summary>
        /// <param name="frames">Blocco che contiene il quadrato di analisi</param>
        /// <param name="origin">Primo byte del quadrato dentro il blocco</param>
        /// <returns>64 bit impacchettati con il primo confronto nel bit più significativo</returns>
        private static ulong ComputeVerticalHash(byte[] frames, int origin)
        {
            int[] cells = new int[9 * 8];
            for (int row = 0; row < FrameSignals.SIDE; row++)
            {
                int cellRow = row / 8;
                int offset = origin + row * FrameSignals.SIDE;
                for (int column = 0; column < FrameSignals.SIDE; column++)
                    cells[cellRow * 8 + column / 9] += frames[offset + column];
            }

            ulong result = 0UL;
            int bit = 0;
            for (int cellRow = 0; cellRow < 8; cellRow++)
            {
                for (int cellColumn = 0; cellColumn < 8; cellColumn++)
                {
                    if (cells[(cellRow + 1) * 8 + cellColumn] > cells[cellRow * 8 + cellColumn])
                        result |= 1UL << (63 - bit);
                    bit++;
                }
            }

            return result;
        }

        /// <summary>
        /// Calcola luminanza, miniatura 12x12 e deviazione standard di un quadrato
        /// </summary>
        /// <param name="frames">Blocco che contiene il quadrato di analisi</param>
        /// <param name="origin">Primo byte del quadrato dentro il blocco</param>
        /// <param name="lumaMean">Accumulatore delle luminanze medie</param>
        /// <param name="thumbStd">Accumulatore delle deviazioni standard</param>
        /// <param name="thumbPixels">Accumulatore delle miniature</param>
        private static void AppendMeasurements(byte[] frames, int origin, List<float> lumaMean, List<float> thumbStd, List<byte> thumbPixels)
        {
            int[] sums = new int[THUMB_PIXELS];
            long lumaSum = 0;
            for (int row = 0; row < FrameSignals.SIDE; row++)
            {
                int cellRow = row / THUMB_BLOCK;
                int offset = origin + row * FrameSignals.SIDE;
                for (int column = 0; column < FrameSignals.SIDE; column++)
                {
                    byte value = frames[offset + column];
                    lumaSum += value;
                    sums[cellRow * FrameSignals.THUMB_SIDE + column / THUMB_BLOCK] += value;
                }
            }

            const double CELL_PIXELS = THUMB_BLOCK * THUMB_BLOCK;
            double total = 0.0;
            double totalSquares = 0.0;
            for (int i = 0; i < THUMB_PIXELS; i++)
            {
                double value = sums[i] / CELL_PIXELS;
                total += value;
                totalSquares += value * value;
                thumbPixels.Add((byte)(sums[i] / (THUMB_BLOCK * THUMB_BLOCK)));
            }

            double mean = total / THUMB_PIXELS;
            lumaMean.Add((float)((double)lumaSum / FRAME_BYTES));
            thumbStd.Add((float)Math.Sqrt(Math.Max(totalSquares / THUMB_PIXELS - mean * mean, 0.0)));
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Sul processore un fotogramma alla volta costa quanto mille: non serve accumularli
        /// </summary>
        public override int BatchFrames
        {
            get { return 1; }
        }

        #endregion
    }
}
