using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Splitting
{
    /// <summary>
    /// Ricerca dei keyframe su una sequenza di fotogrammi, in avanti fino a un limite o all'indietro fino all'inizio
    /// </summary>
    public sealed class MkvSplitKeyframeIndex
    {
        #region Variabili di classe

        /// <summary>
        /// Fotogrammi con il flag keyframe
        /// </summary>
        private readonly List<MkvSplitFrameInfo> _frames;

        /// <summary>
        /// Posizione in _frames di ogni indice cercato, null se gli indici sono quelli di _frames
        /// </summary>
        private readonly int[] _order;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore sugli indici della sequenza stessa
        /// </summary>
        /// <param name="frames">Fotogrammi con il flag keyframe</param>
        public MkvSplitKeyframeIndex(List<MkvSplitFrameInfo> frames)
        {
            this._frames = frames;
            this._order = null;
        }

        /// <summary>
        /// Costruttore su indici tradotti: si cerca per presentazione in una mappa scritta in ordine di decodifica
        /// </summary>
        /// <param name="frames">Fotogrammi con il flag keyframe, nell'ordine di order</param>
        /// <param name="order">Posizione in frames di ogni indice cercato</param>
        public MkvSplitKeyframeIndex(List<MkvSplitFrameInfo> frames, int[] order)
        {
            this._frames = frames;
            this._order = order;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Numero di indici cercabili
        /// </summary>
        public int Count => this._order != null ? this._order.Length : this._frames.Count;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Primo keyframe da un indice in avanti, prima di un limite
        /// </summary>
        /// <param name="from">Primo indice controllato</param>
        /// <param name="limit">Limite esclusivo, ridotto a Count</param>
        /// <returns>Indice del keyframe, -1 se nessuno</returns>
        public int Next(int from, int limit)
        {
            int end = Math.Min(limit, this.Count);
            for (int i = from; i < end; i++)
            {
                if (this.IsKey(i))
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Primo keyframe da un indice all'indietro, fino all'inizio della sequenza
        /// </summary>
        /// <param name="from">Primo indice controllato; un valore negativo non trova nulla</param>
        /// <returns>Indice del keyframe, -1 se nessuno</returns>
        public int Previous(int from)
        {
            for (int i = from; i >= 0; i--)
            {
                if (this.IsKey(i))
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Indica se l'indice è un keyframe
        /// </summary>
        /// <param name="index">Indice cercato</param>
        /// <returns>True se il fotogramma dell'indice è un keyframe</returns>
        public bool IsKey(int index)
        {
            return this._frames[this._order != null ? this._order[index] : index].Key;
        }

        #endregion
    }
}
