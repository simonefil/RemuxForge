using System;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// Velocità scelta per nome fra i preset dell'encoder, senza distinguere le maiuscole
    /// </summary>
    public sealed class MkvSplitPresetEncodeSpeed : MkvSplitEncodeSpeedBase
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="name">Nome tecnico del parametro</param>
        /// <param name="defaultValue">Preset predefinito</param>
        /// <param name="values">Preset ammessi, in minuscolo, dal più veloce al più lento</param>
        public MkvSplitPresetEncodeSpeed(string name, string defaultValue, string[] values) : base(name, defaultValue)
        {
            this.Values = values;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Preset ammessi, in minuscolo, dal più veloce al più lento
        /// </summary>
        public string[] Values { get; }

        /// <summary>
        /// Elenco dei preset
        /// </summary>
        public override string AllowedText { get { return string.Join(", ", this.Values); } }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Indica se il preset è fra quelli dell'encoder
        /// </summary>
        /// <param name="value">Preset richiesto</param>
        /// <returns>True se il preset, in minuscolo, è nell'elenco</returns>
        public override bool Accepts(string value)
        {
            return Array.IndexOf(this.Values, value.ToLowerInvariant()) >= 0;
        }

        #endregion
    }
}
