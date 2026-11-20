using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Web.Components.Shared
{
    /// <summary>
    /// Regole comuni della scelta di modello e livello di ragionamento fra i modelli dell'accesso
    /// </summary>
    public static class AiModelChoice
    {
        #region Metodi pubblici

        /// <summary>
        /// Livello da tenere col modello scelto: quello dato se il modello lo supporta, altrimenti il predefinito del modello
        /// </summary>
        /// <param name="model">Modello scelto, null se l'elenco non lo conosce</param>
        /// <param name="effort">Livello dato</param>
        /// <returns>Livello da tenere; con un modello sconosciuto quello dato</returns>
        public static string EffortFor(AiModelInfo model, string effort)
        {
            return model == null || model.SupportedReasoningLevels.Contains(effort ?? "") ? effort : model.DefaultReasoningLevel;
        }

        /// <summary>
        /// True se il modello salvato manca dall'elenco letto: si segnala e si svuota. Con l'elenco vuoto resta com'è
        /// </summary>
        /// <param name="models">Modelli dell'accesso</param>
        /// <param name="modelId">Modello salvato</param>
        /// <returns>True se il modello va svuotato</returns>
        public static bool IsUnknown(List<AiModelInfo> models, string modelId)
        {
            return models.Count > 0 && !string.IsNullOrEmpty(modelId) && models.Find(m => m.Id == modelId) == null;
        }

        #endregion
    }
}
