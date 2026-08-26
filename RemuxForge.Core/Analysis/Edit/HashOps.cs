using RemuxForge.Core.Analysis.Edit.Extraction;
using System;
using System.Numerics;

namespace RemuxForge.Core.Analysis.Edit
{
    /// <summary>
    /// Le due tracce portate nello stesso dominio temporale, con sopra le misure di distanza
    /// </summary>
    internal class PairSignals
    {
        #region Costruttore

        /// <summary>
        /// Costruttore che porta il lang nel dominio temporale del source
        /// </summary>
        /// <param name="source">Segnali della sorgente</param>
        /// <param name="language">Segnali della copia doppiata</param>
        /// <param name="stretch">Fattore di stretch da applicare ai PTS della copia doppiata</param>
        public PairSignals(FrameSignals source, FrameSignals language, double stretch)
        {
            this.Source = source;
            this.Language = language;
            this.Stretch = stretch;
            this.LanguagePtsMs = new double[language.Count];
            for (int i = 0; i < language.Count; i++)
                this.LanguagePtsMs[i] = language.PtsMs[i] * stretch;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Segnali della sorgente
        /// </summary>
        public FrameSignals Source { get; private set; }

        /// <summary>
        /// Segnali della copia doppiata
        /// </summary>
        public FrameSignals Language { get; private set; }

        /// <summary>
        /// Fattore di stretch applicato alla copia doppiata
        /// </summary>
        public double Stretch { get; private set; }

        /// <summary>
        /// PTS della copia doppiata già riportati nel dominio della sorgente
        /// </summary>
        public double[] LanguagePtsMs { get; private set; }

        #endregion
    }

    /// <summary>
    /// Distanze di Hamming fra i dHash delle due tracce, unica primitiva di misura visiva
    /// </summary>
    internal static class HashOps
    {
        #region Metodi pubblici

        /// <summary>
        /// Indice del primo elemento non minore del valore cercato
        /// </summary>
        /// <param name="values">Sequenza crescente</param>
        /// <param name="target">Valore cercato</param>
        /// <returns>Indice di inserimento a sinistra</returns>
        public static int LowerBound(double[] values, double target)
        {
            int low = 0;
            int high = values.Length;
            while (low < high)
            {
                int middle = (low + high) / 2;
                if (values[middle] < target)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        /// <summary>
        /// Distanza di hash al fotogramma lang più vicino, entro il raggio richiesto
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="sourceIndex">Indice del fotogramma sorgente</param>
        /// <param name="offsetMs">Offset da verificare</param>
        /// <param name="radius">Fotogrammi lang di tolleranza</param>
        /// <returns>Distanza di Hamming minima nell'intorno</returns>
        public static int Distance(PairSignals pair, int sourceIndex, double offsetMs, int radius)
        {
            // Il raggio serve perché le due copie non condividono la griglia dei tempi: con
            // framerate diversi il corrispondente giusto non è quello che cade sotto il timestamp
            double[] languagePts = pair.LanguagePtsMs;
            int center = LowerBound(languagePts, pair.Source.PtsMs[sourceIndex] + offsetMs);
            ulong sourceHash0 = pair.Source.Hash0[sourceIndex];
            ulong sourceHash1 = pair.Source.Hash1[sourceIndex];
            int best = 128;
            for (int shift = -radius; shift <= radius; shift++)
            {
                int index = center + shift;
                if (index < 0)
                    index = 0;
                if (index > languagePts.Length - 1)
                    index = languagePts.Length - 1;
                int distance = BitOperations.PopCount(sourceHash0 ^ pair.Language.Hash0[index]) +
                               BitOperations.PopCount(sourceHash1 ^ pair.Language.Hash1[index]);
                if (distance < best)
                    best = distance;
            }
            return best;
        }

        /// <summary>
        /// Distanza di Hamming fra i dHash di due fotogrammi qualsiasi
        /// </summary>
        /// <param name="left">Segnali della prima traccia</param>
        /// <param name="leftIndex">Indice del fotogramma nella prima traccia</param>
        /// <param name="right">Segnali della seconda traccia</param>
        /// <param name="rightIndex">Indice del fotogramma nella seconda traccia</param>
        /// <returns>Distanza di Hamming fra i due fotogrammi</returns>
        public static int Distance(FrameSignals left, int leftIndex, FrameSignals right, int rightIndex)
        {
            return BitOperations.PopCount(left.Hash0[leftIndex] ^ right.Hash0[rightIndex]) +
                   BitOperations.PopCount(left.Hash1[leftIndex] ^ right.Hash1[rightIndex]);
        }

        /// <summary>
        /// Frazione dei fotogrammi indicati che l'offset spiega entro soglia
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="indices">Indici sorgente da verificare</param>
        /// <param name="offsetMs">Offset da verificare</param>
        /// <param name="radius">Fotogrammi lang di tolleranza</param>
        /// <param name="threshold">Soglia di Hamming</param>
        /// <returns>Frazione di fotogrammi spiegati</returns>
        public static double ExplainedFraction(PairSignals pair, int[] indices, double offsetMs, int radius, int threshold)
        {
            if (indices.Length == 0)
                return 0.0;
            int explained = 0;
            for (int i = 0; i < indices.Length; i++)
            {
                if (Distance(pair, indices[i], offsetMs, radius) <= threshold)
                    explained++;
            }
            return (double)explained / indices.Length;
        }

        /// <summary>
        /// Distanza mediana dei fotogrammi indicati rispetto a un offset
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="indices">Indici sorgente da verificare</param>
        /// <param name="offsetMs">Offset da verificare</param>
        /// <param name="radius">Fotogrammi lang di tolleranza</param>
        /// <returns>Mediana delle distanze di Hamming</returns>
        public static double MedianDistance(PairSignals pair, int[] indices, double offsetMs, int radius)
        {
            if (indices.Length == 0)
                return 128.0;
            int[] distances = new int[indices.Length];
            for (int i = 0; i < indices.Length; i++)
                distances[i] = Distance(pair, indices[i], offsetMs, radius);
            Array.Sort(distances);
            int middle = distances.Length / 2;
            return distances.Length % 2 == 1 ? distances[middle] : (distances[middle - 1] + distances[middle]) / 2.0;
        }

        /// <summary>
        /// Progressione di indici sorgente compresa in un intervallo temporale
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="startMs">Inizio dell'intervallo</param>
        /// <param name="endMs">Fine dell'intervallo</param>
        /// <param name="stride">Passo di campionamento</param>
        /// <param name="firstIndex">Primo indice sorgente della progressione</param>
        /// <param name="indexCount">Quanti indici contiene la progressione</param>
        public static void Range(PairSignals pair, double startMs, double endMs, int stride, out int firstIndex, out int indexCount)
        {
            firstIndex = LowerBound(pair.Source.PtsMs, startMs);
            int last = LowerBound(pair.Source.PtsMs, endMs);
            indexCount = last <= firstIndex ? 0 : (last - firstIndex + stride - 1) / stride;
        }

        /// <summary>
        /// Indici sorgente compresi in un intervallo temporale, campionati a passo costante
        /// </summary>
        /// <param name="pair">Coppia di tracce</param>
        /// <param name="startMs">Inizio dell'intervallo</param>
        /// <param name="endMs">Fine dell'intervallo</param>
        /// <param name="stride">Passo di campionamento</param>
        /// <returns>Indici sorgente selezionati</returns>
        public static int[] RangeIndices(PairSignals pair, double startMs, double endMs, int stride)
        {
            Range(pair, startMs, endMs, stride, out int first, out int count);
            if (count == 0)
                return Array.Empty<int>();
            int[] result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = first + i * stride;
            return result;
        }

        #endregion
    }
}
