using System.Globalization;

namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// Velocità numerica intera in un intervallo
    /// </summary>
    public sealed class MkvSplitRangeEncodeSpeed : MkvSplitEncodeSpeedBase
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="name">Nome tecnico del parametro</param>
        /// <param name="defaultValue">Velocità predefinita</param>
        /// <param name="min">Velocità minima</param>
        /// <param name="max">Velocità massima</param>
        public MkvSplitRangeEncodeSpeed(string name, string defaultValue, int min, int max) : base(name, defaultValue)
        {
            this.Min = min;
            this.Max = max;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Velocità minima
        /// </summary>
        public int Min { get; }

        /// <summary>
        /// Velocità massima
        /// </summary>
        public int Max { get; }

        /// <summary>
        /// Intervallo ammesso
        /// </summary>
        public override string AllowedText { get { return this.Min + "–" + this.Max; } }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Indica se il valore è un intero senza segno nell'intervallo
        /// </summary>
        /// <param name="value">Velocità richiesta</param>
        /// <returns>True se il numero è fra minimo e massimo</returns>
        public override bool Accepts(string value)
        {
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number >= this.Min && number <= this.Max;
        }

        #endregion
    }
}
