using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemuxForge.Core.Toolbox.SubtitleTranslation
{
    /// <summary>
    /// Parti comuni delle richieste della traduzione: formato a record delle battute, regole per lingua d'arrivo, nomi inglesi
    /// delle lingue nei template e serializzazione JSON leggibile del testo mandato al modello
    /// </summary>
    public static class TranslationRequestBuilder
    {
        #region Costanti

        /// <summary>
        /// Blocco «Record format», identico in tutte le instructions che lo usano
        /// </summary>
        public const string RECORD_FORMAT = "Record format\n" +
            "- A source cue is a record. Its header is \"<id>\" or \"<id> [gN]\", then a tab and the first line of the cue. Each further line of the same cue starts with a tab.\n" +
            "- A translation record has the same header followed by \" =>\", then a tab and the translated text, with the same continuation lines. A header with nothing after \" =>\" means the cue has no accepted translation yet.\n" +
            "- A signal record (review only) has the header \"<id> !\", then a tab and one signal per line.\n" +
            "- In submit_translation, a line break inside a cue is \"\\n\" inside the JSON string.";

        /// <summary>
        /// Passo dell'analisi sulle forme di cortesia per le lingue senza forme grammaticali
        /// </summary>
        private const string GENERIC_ADDRESS_PROCEDURE = "Describe in register_rules how formal each recurring character is with the others.";

        #endregion

        #region Variabili statiche

        /// <summary>
        /// Forme di cortesia per lingua d'arrivo ISO 639-2; le lingue assenti non hanno forme e il glossario non ha addressing
        /// </summary>
        private static readonly Dictionary<string, string[]> s_addressForms = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            { "ita", new string[] { "tu", "lei", "voi" } },
            { "fra", new string[] { "tu", "vous" } },
            { "deu", new string[] { "du", "Sie", "ihr" } },
            { "spa", new string[] { "tú", "usted", "vosotros", "ustedes" } }
        };

        /// <summary>
        /// Descrizione dell'enum delle forme nello schema, dove la lingua la fissa
        /// </summary>
        private static readonly Dictionary<string, string> s_addressFormDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "ita", "tu: informal; lei: formal; voi: plural or archaic reverential." }
        };

        /// <summary>
        /// Opzioni del JSON mandato al modello: rientro di uno spazio e caratteri non ASCII leggibili
        /// </summary>
        private static readonly JsonSerializerOptions s_modelJson = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentSize = 1,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Scrive una battuta nel formato a record: testata con id e gruppo, tab, prima riga; le righe seguenti cominciano col tab
        /// </summary>
        /// <param name="cue">Battuta traducibile</param>
        /// <returns>Record della battuta</returns>
        public static string FormatRecord(SubtitleCue cue)
        {
            string header = cue.Id.Value.ToString(CultureInfo.InvariantCulture);
            if (cue.Group.HasValue)
                header += " [g" + cue.Group.Value.ToString(CultureInfo.InvariantCulture) + "]";

            return header + "\t" + cue.CleanText.Replace("\n", "\n\t");
        }

        /// <summary>
        /// Scrive il sorgente numerato: i record di tutte le battute traducibili, in ordine di id
        /// </summary>
        /// <param name="cues">Battute del file, escluse e copie comprese</param>
        /// <returns>Sorgente numerato, una riga o più per battuta</returns>
        public static string FormatSource(IReadOnlyList<SubtitleCue> cues)
        {
            StringBuilder source = new StringBuilder();
            foreach (SubtitleCue cue in cues)
            {
                if (!cue.Id.HasValue)
                    continue;

                if (source.Length > 0)
                    source.Append('\n');
                source.Append(FormatRecord(cue));
            }

            return source.ToString();
        }

        /// <summary>
        /// Nome inglese di una lingua per i template
        /// </summary>
        /// <param name="language">Codice ISO 639-2</param>
        /// <returns>Nome inglese, oppure il codice se il runtime non conosce la lingua</returns>
        public static string GetEnglishName(string language)
        {
            string code = LanguageValidator.NormalizeToIso6392(language);
            foreach (CultureInfo culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                if (string.Equals(culture.ThreeLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(culture.Name))
                    return culture.EnglishName;
            }

            return code;
        }

        /// <summary>
        /// Nome inglese della lingua dell'interfaccia, in cui il modello scrive i testi che legge l'utente
        /// </summary>
        /// <returns>Nome inglese della lingua dell'app</returns>
        public static string GetNotesLanguage()
        {
            return CultureInfo.GetCultureInfo(AppText.Language).EnglishName;
        }

        /// <summary>
        /// Forme di cortesia della lingua d'arrivo
        /// </summary>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        /// <returns>Forme, null per una lingua senza forme grammaticali</returns>
        public static string[] GetAddressForms(string targetLanguage)
        {
            string[] forms;
            return s_addressForms.TryGetValue(LanguageValidator.NormalizeToIso6392(targetLanguage), out forms) ? forms : null;
        }

        /// <summary>
        /// Descrizione delle forme di cortesia nello schema del glossario
        /// </summary>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        /// <returns>Descrizione, vuota dove la lingua non la fissa</returns>
        public static string GetAddressFormDescription(string targetLanguage)
        {
            string description;
            return s_addressFormDescriptions.TryGetValue(LanguageValidator.NormalizeToIso6392(targetLanguage), out description) ? description : "";
        }

        /// <summary>
        /// Passo dell'analisi sulle forme di cortesia della lingua d'arrivo
        /// </summary>
        /// <param name="targetLanguage">Lingua d'arrivo ISO 639-2</param>
        /// <returns>Testo del passo per {address_procedure}</returns>
        public static string GetAddressProcedure(string targetLanguage)
        {
            string[] forms = GetAddressForms(targetLanguage);
            if (forms == null)
                return GENERIC_ADDRESS_PROCEDURE;

            // Articolo inglese davanti al nome della lingua: an Italian, a German
            string name = GetEnglishName(targetLanguage);
            string article = "AEIOU".IndexOf(char.ToUpperInvariant(name[0])) >= 0 ? "an " : "a ";
            return "Decide the address form (" + string.Join(", ", forms) + ") between the recurring characters as " + article + name + " adaptation would, and fill addressing.";
        }

        /// <summary>
        /// Serializza un nodo JSON per il testo dei messaggi al modello
        /// </summary>
        /// <param name="node">Nodo da scrivere</param>
        /// <returns>JSON rientrato di uno spazio, con accenti e segni leggibili</returns>
        public static string ToModelJson(JsonNode node)
        {
            return node.ToJsonString(s_modelJson);
        }

        /// <summary>
        /// Crea un messaggio user della Responses API
        /// </summary>
        /// <param name="text">Testo del messaggio</param>
        /// <returns>Item del messaggio</returns>
        public static JsonObject CreateUserMessage(string text)
        {
            return new JsonObject { ["role"] = "user", ["content"] = text };
        }

        #endregion
    }
}
