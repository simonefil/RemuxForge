namespace RemuxForge.Core.Splitting.Codecs
{
    /// <summary>
    /// Parametro di velocità dell'encoder della ricodifica: nome dell'opzione ffmpeg, valore predefinito e valori ammessi
    /// </summary>
    public abstract class MkvSplitEncodeSpeedBase
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="name">Nome tecnico del parametro, senza il trattino dell'opzione ffmpeg (preset o cpu-used)</param>
        /// <param name="defaultValue">Velocità predefinita</param>
        protected MkvSplitEncodeSpeedBase(string name, string defaultValue)
        {
            this.Name = name;
            this.Default = defaultValue;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Nome tecnico del parametro (preset o cpu-used)
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Velocità predefinita
        /// </summary>
        public string Default { get; }

        /// <summary>
        /// Valori ammessi come si mostrano nell'errore di una velocità non valida
        /// </summary>
        public abstract string AllowedText { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Indica se la velocità richiesta è ammessa dall'encoder
        /// </summary>
        /// <param name="value">Velocità richiesta, già senza spazi ai lati e non vuota</param>
        /// <returns>True se il valore è ammesso</returns>
        public abstract bool Accepts(string value);

        #endregion
    }
}
