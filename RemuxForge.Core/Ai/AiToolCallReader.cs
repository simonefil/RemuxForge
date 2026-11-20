using RemuxForge.Core.Models;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace RemuxForge.Core.Ai
{
    /// <summary>
    /// Lettura delle chiamate di funzione di una risposta Responses senza stato lato server: item rimandati al giro successivo,
    /// argomenti ed esiti function_call_output
    /// </summary>
    public static class AiToolCallReader
    {
        #region Costanti

        /// <summary>
        /// Tipo degli item di chiamata di funzione
        /// </summary>
        private const string FUNCTION_CALL = "function_call";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Accoda all'ingresso ogni item della risposta, invariato: senza stato lato server è l'unica memoria del modello
        /// </summary>
        /// <param name="response">Risposta del servizio</param>
        /// <param name="input">Ingresso della sequenza</param>
        /// <returns>Chiamate di funzione della risposta, nell'ordine</returns>
        public static List<JsonObject> Echo(AiResponseResult response, JsonArray input)
        {
            List<JsonObject> calls = new List<JsonObject>();
            foreach (JsonObject item in response.Items)
            {
                input.Add(item.DeepClone());
                if (AiJson.GetString(item, "type") == FUNCTION_CALL)
                    calls.Add(item);
            }

            return calls;
        }

        /// <summary>
        /// Interpreta gli argomenti di una chiamata
        /// </summary>
        /// <param name="call">Item della chiamata</param>
        /// <returns>Argomenti, null se non sono un oggetto JSON</returns>
        public static JsonObject ParseArguments(JsonObject call)
        {
            return AiJson.TryParseObject(AiJson.GetString(call, "arguments"));
        }

        /// <summary>
        /// Crea l'esito di una chiamata da accodare all'ingresso del giro successivo
        /// </summary>
        /// <param name="call">Item della chiamata</param>
        /// <param name="output">Esito per il modello</param>
        /// <returns>Item function_call_output</returns>
        public static JsonObject CreateOutput(JsonObject call, string output)
        {
            return new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = AiJson.GetString(call, "call_id"),
                ["output"] = output
            };
        }

        #endregion
    }
}
