using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemuxForge.Core.Ai
{
    /// <summary>
    /// Lettura tollerante dei JSON dei servizi AI: proprietà stringa e oggetti da testo
    /// </summary>
    internal static class AiJson
    {
        #region Metodi pubblici

        /// <summary>
        /// Legge una proprietà stringa da un oggetto JSON
        /// </summary>
        /// <param name="source">Oggetto JSON</param>
        /// <param name="name">Nome proprietà</param>
        /// <returns>Valore o stringa vuota</returns>
        public static string GetString(JsonObject source, string name)
        {
            JsonValue value = source != null ? source[name] as JsonValue : null;
            string result;
            if (value != null && value.TryGetValue<string>(out result))
                return result != null ? result : "";

            return "";
        }

        /// <summary>
        /// Interpreta un testo come oggetto JSON
        /// </summary>
        /// <param name="text">Testo</param>
        /// <returns>Oggetto JSON o null</returns>
        public static JsonObject TryParseObject(string text)
        {
            try
            {
                return JsonNode.Parse(text != null ? text : "") as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        #endregion
    }
}
