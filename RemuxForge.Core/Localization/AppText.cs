using RemuxForge.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace RemuxForge.Core.Localization
{
    /// <summary>
    /// Testi applicativi localizzati tramite una mappa JSON piatta per lingua
    /// </summary>
    public static class AppText
    {
        #region Costanti

        public const string LANG_EN = "en";

        public const string LANG_IT = "it";

        private const string LANG_ENV_VAR = "REMUXFORGE_LANG";

        #endregion

        #region Variabili statiche

        private static readonly object s_lock = new object();

        private static Dictionary<string, string> s_texts;

        private static string s_language = LANG_EN;

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Inizializza il catalogo testi rispettando precedenza CLI, env, config, default
        /// </summary>
        /// <param name="cliLanguage">Lingua richiesta dalla CLI, vuota se non specificata</param>
        /// <param name="configLanguage">Lingua salvata in configurazione</param>
        public static void Initialize(string cliLanguage, string configLanguage)
        {
            string language = NormalizeLanguage(cliLanguage);
            if (string.IsNullOrEmpty(language))
            {
                language = NormalizeLanguage(Environment.GetEnvironmentVariable(LANG_ENV_VAR));
            }
            if (string.IsNullOrEmpty(language))
            {
                language = NormalizeLanguage(configLanguage);
            }
            if (string.IsNullOrEmpty(language))
            {
                language = LANG_EN;
            }

            lock (s_lock)
            {
                s_language = language;
                s_texts = LoadResource(language);
            }
        }

        /// <summary>
        /// Restituisce un testo localizzato
        /// </summary>
        /// <param name="key">Chiave testo</param>
        /// <returns>Testo localizzato o chiave tra parentesi se mancante</returns>
        public static string T(string key)
        {
            EnsureInitialized();

            if (key == null)
            {
                return "";
            }

            if (s_texts.TryGetValue(key, out string text))
            {
                return text;
            }

            return "[" + key + "]";
        }

        /// <summary>
        /// Restituisce un testo localizzato formattato con placeholder posizionali
        /// </summary>
        /// <param name="key">Chiave testo</param>
        /// <param name="args">Valori placeholder</param>
        /// <returns>Testo localizzato formattato</returns>
        public static string F(string key, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, T(key), args);
        }

        /// <summary>
        /// Normalizza una lingua supportata
        /// </summary>
        /// <param name="value">Valore lingua</param>
        /// <returns>en, it o vuoto se non supportata</returns>
        public static string NormalizeLanguage(string value)
        {
            string result = "";
            string trimmed = value != null ? value.Trim().ToLowerInvariant() : "";

            if (trimmed == LANG_EN || trimmed == "eng" || trimmed.StartsWith("en-", StringComparison.Ordinal))
            {
                result = LANG_EN;
            }
            else if (trimmed == LANG_IT || trimmed == "ita" || trimmed.StartsWith("it-", StringComparison.Ordinal))
            {
                result = LANG_IT;
            }

            return result;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Lingua corrente del catalogo testi
        /// </summary>
        public static string Language { get { EnsureInitialized(); return s_language; } }

        #endregion

        #region Metodi privati

        private static void EnsureInitialized()
        {
            if (s_texts == null)
            {
                Initialize("", AppSettingsService.Instance.Settings.Ui.Language);
            }
        }

        private static Dictionary<string, string> LoadResource(string language)
        {
            Dictionary<string, string> result = new Dictionary<string, string>();
            Assembly assembly = typeof(AppText).Assembly;
            string resourceName = "RemuxForge.Core.Localization.Resources." + language + ".json";

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    return result;
                }

                using (StreamReader reader = new StreamReader(stream))
                {
                    string json = reader.ReadToEnd();
                    using (JsonDocument document = JsonDocument.Parse(json))
                    {
                        foreach (JsonProperty property in document.RootElement.EnumerateObject())
                        {
                            if (property.Value.ValueKind == JsonValueKind.String)
                            {
                                result[property.Name] = property.Value.GetString();
                            }
                            else if (property.Value.ValueKind == JsonValueKind.Array)
                            {
                                List<string> lines = new List<string>();
                                foreach (JsonElement item in property.Value.EnumerateArray())
                                {
                                    lines.Add(item.GetString() ?? "");
                                }
                                result[property.Name] = string.Join(Environment.NewLine, lines);
                            }
                        }
                    }
                }
            }

            return result;
        }

        #endregion
    }
}
