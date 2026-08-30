using System;
using System.Collections.Generic;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Selezione multipla di righe con ancora, condivisa da tutte le griglie del workspace
    /// </summary>
    public class RowSelectionState
    {
        #region Variabili di classe

        /// <summary>
        /// Indici selezionati, sempre ordinati
        /// </summary>
        private readonly List<int> _indices;

        /// <summary>
        /// Ancora da cui parte la selezione a range
        /// </summary>
        private int _anchorIndex;

        #endregion

        #region Costruttore

        /// <summary>Costruttore</summary>
        public RowSelectionState()
        {
            this._indices = new List<int>();
            this._anchorIndex = -1;
        }

        #endregion

        #region Proprietà

        /// <summary>Indici selezionati</summary>
        public List<int> Indices
        {
            get { return this._indices; }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Applica una selezione con i modifier stile file explorer
        /// </summary>
        /// <param name="index">Indice della riga interessata</param>
        /// <param name="ctrl">True per selezione additiva o toggle</param>
        /// <param name="shift">True per selezione a range dall'ancora</param>
        /// <param name="count">Numero di righe della griglia</param>
        /// <param name="focusedIndex">Indice attualmente a fuoco, usato quando l'ancora non è valida</param>
        public void Apply(int index, bool ctrl, bool shift, int count, int focusedIndex)
        {
            if (index < 0 || index >= count)
                return;

            if (shift)
            {
                if (this._anchorIndex < 0 || this._anchorIndex >= count)
                    this._anchorIndex = focusedIndex >= 0 ? focusedIndex : index;

                if (!ctrl)
                    this._indices.Clear();

                this.AddRange(this._anchorIndex, index);
                return;
            }

            if (ctrl)
            {
                this.Toggle(index);
                return;
            }

            this._indices.Clear();
            this._indices.Add(index);
            this._anchorIndex = index;
        }

        /// <summary>
        /// Inverte la selezione di una riga e ne fa la nuova ancora
        /// </summary>
        /// <param name="index">Indice della riga</param>
        public void Toggle(int index)
        {
            if (this._indices.Contains(index))
            {
                this._indices.Remove(index);
            }
            else
            {
                this._indices.Add(index);
                this._indices.Sort();
            }

            this._anchorIndex = index;
        }

        /// <summary>
        /// Seleziona tutte le righe
        /// </summary>
        /// <param name="count">Numero di righe della griglia</param>
        public void SelectAll(int count)
        {
            this._indices.Clear();
            for (int i = 0; i < count; i++)
            {
                this._indices.Add(i);
            }
        }

        /// <summary>
        /// True se la riga è selezionata
        /// </summary>
        /// <param name="index">Indice della riga</param>
        /// <returns>True se selezionata</returns>
        public bool IsSelected(int index)
        {
            return this._indices.Contains(index);
        }

        /// <summary>
        /// Indici su cui applicare un'azione: la selezione, oppure la sola riga a fuoco
        /// </summary>
        /// <param name="count">Numero di righe della griglia</param>
        /// <param name="focusedIndex">Indice attualmente a fuoco</param>
        /// <returns>Indici validi in ordine crescente</returns>
        public List<int> GetActionIndices(int count, int focusedIndex)
        {
            List<int> result = new List<int>();

            for (int i = 0; i < this._indices.Count; i++)
            {
                if (this._indices[i] >= 0 && this._indices[i] < count && !result.Contains(this._indices[i]))
                {
                    result.Add(this._indices[i]);
                }
            }

            if (result.Count == 0 && focusedIndex >= 0 && focusedIndex < count)
            {
                result.Add(focusedIndex);
            }

            result.Sort();
            return result;
        }

        /// <summary>
        /// Rimuove gli indici non più validi dopo un refresh dei record
        /// </summary>
        /// <param name="count">Numero di righe della griglia</param>
        public void Normalize(int count)
        {
            for (int i = this._indices.Count - 1; i >= 0; i--)
            {
                if (this._indices[i] < 0 || this._indices[i] >= count)
                    this._indices.RemoveAt(i);
            }

            if (this._anchorIndex >= count)
                this._anchorIndex = count - 1;
        }

        /// <summary>
        /// Imposta l'ancora della selezione a range
        /// </summary>
        /// <param name="index">Indice dell'ancora</param>
        public void SetAnchor(int index)
        {
            this._anchorIndex = index;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Aggiunge alla selezione un intervallo inclusivo di righe
        /// </summary>
        /// <param name="startIndex">Primo indice</param>
        /// <param name="endIndex">Ultimo indice</param>
        private void AddRange(int startIndex, int endIndex)
        {
            int first = Math.Min(startIndex, endIndex);
            int last = Math.Max(startIndex, endIndex);

            for (int i = first; i <= last; i++)
            {
                if (!this._indices.Contains(i))
                    this._indices.Add(i);
            }

            this._indices.Sort();
        }

        #endregion
    }
}
