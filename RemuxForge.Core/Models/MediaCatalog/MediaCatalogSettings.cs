namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Modalità di accesso a TheTVDB
    /// </summary>
    public enum TvdbAccessMode
    {
        /// <summary>API ufficiale v4 con la chiave dell'utente</summary>
        Api,

        /// <summary>Lettura delle pagine del sito</summary>
        Site
    }

    /// <summary>
    /// Contenuto del file privato delle impostazioni dei cataloghi media
    /// </summary>
    public class MediaCatalogSettings
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public MediaCatalogSettings()
        {
            this.TvdbMode = TvdbAccessMode.Api;
            this.TvdbApiKey = "";
            this.TmdbCredential = "";
            this.DefaultLanguage = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Modalità di accesso a TheTVDB
        /// </summary>
        public TvdbAccessMode TvdbMode { get; set; }

        /// <summary>
        /// Chiave API TheTVDB dell'utente
        /// </summary>
        public string TvdbApiKey { get; set; }

        /// <summary>
        /// Read Access Token o API key TMDB dell'utente
        /// </summary>
        public string TmdbCredential { get; set; }

        /// <summary>
        /// Lingua ISO 639-2/T proposta nella ricerca
        /// </summary>
        public string DefaultLanguage { get; set; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Crea una copia
        /// </summary>
        /// <returns>Copia delle impostazioni</returns>
        public MediaCatalogSettings Clone()
        {
            MediaCatalogSettings copy = new MediaCatalogSettings();
            copy.TvdbMode = this.TvdbMode;
            copy.TvdbApiKey = this.TvdbApiKey;
            copy.TmdbCredential = this.TmdbCredential;
            copy.DefaultLanguage = this.DefaultLanguage;
            return copy;
        }

        #endregion
    }
}
