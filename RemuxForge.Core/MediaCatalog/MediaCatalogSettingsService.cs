using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.IO;
using System.Text.Json;

namespace RemuxForge.Core.MediaCatalog
{
    /// <summary>
    /// Impostazioni dei cataloghi media: file privato nella cartella di configurazione, tenuto in memoria dopo la prima lettura
    /// </summary>
    public class MediaCatalogSettingsService
    {
        #region Costanti

        /// <summary>
        /// Nome del file delle impostazioni
        /// </summary>
        public const string FILE_NAME = "media-catalog-settings.json";

        /// <summary>
        /// Lingua usata quando né il file né la lingua dell'interfaccia ne danno una valida
        /// </summary>
        private const string FALLBACK_LANGUAGE = "eng";

        #endregion

        #region Variabili di classe

        /// <summary>
        /// File delle impostazioni
        /// </summary>
        private readonly PrivateJsonFile<MediaCatalogSettings> _file;

        /// <summary>
        /// Serializza lettura e scrittura
        /// </summary>
        private readonly object _fileLock;

        /// <summary>
        /// Impostazioni correnti, null finché non lette
        /// </summary>
        private MediaCatalogSettings _current;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="configFolder">Cartella configurazione RemuxForge</param>
        public MediaCatalogSettingsService(string configFolder)
        {
            this._file = new PrivateJsonFile<MediaCatalogSettings>(configFolder, FILE_NAME);
            this._fileLock = new object();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Restituisce una copia delle impostazioni correnti, leggendo il file al primo uso; un file illeggibile vale come assente
        /// </summary>
        /// <returns>Impostazioni</returns>
        public MediaCatalogSettings Load()
        {
            lock (this._fileLock)
            {
                if (this._current == null)
                {
                    MediaCatalogSettings read = null;
                    try
                    {
                        read = this._file.Read();
                    }
                    catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
                    {
                        ConsoleHelper.Write(LogSection.Config, LogLevel.Warning, AppText.F("mediaCatalog.settingsLoadError", FILE_NAME, ex.Message));
                    }

                    this._current = Normalize(read != null ? read : new MediaCatalogSettings());
                }

                return this._current.Clone();
            }
        }

        /// <summary>
        /// Salva le impostazioni con scrittura atomica e le rende correnti
        /// </summary>
        /// <param name="settings">Impostazioni da salvare</param>
        public void Save(MediaCatalogSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            MediaCatalogSettings normalized = Normalize(settings.Clone());

            lock (this._fileLock)
            {
                this._file.Write(normalized);
                this._current = normalized;
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Ripulisce le credenziali e completa la lingua di default
        /// </summary>
        /// <param name="settings">Impostazioni da normalizzare</param>
        /// <returns>Le stesse impostazioni</returns>
        private static MediaCatalogSettings Normalize(MediaCatalogSettings settings)
        {
            string language;

            settings.TvdbApiKey = settings.TvdbApiKey != null ? settings.TvdbApiKey.Trim() : "";
            settings.TmdbCredential = settings.TmdbCredential != null ? settings.TmdbCredential.Trim() : "";
            if (!Enum.IsDefined(settings.TvdbMode))
                settings.TvdbMode = TvdbAccessMode.Api;

            // Senza una scelta salvata si propone la lingua dell'interfaccia
            language = LanguageValidator.NormalizeToIso6392(settings.DefaultLanguage);
            if (language.Length == 0)
                language = LanguageValidator.NormalizeToIso6392(AppSettingsService.Instance.Settings.Ui.Language);

            settings.DefaultLanguage = language.Length > 0 ? language : FALLBACK_LANGUAGE;
            return settings;
        }

        #endregion
    }
}
