using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Web.Components.Remux
{
    /// <summary>
    /// Wizard che ospita i campi RemuxFieldComponent: aggiorna l'help e fornisce gli errori del campo
    /// </summary>
    public interface IWizardFieldHost
    {
        /// <summary>
        /// Mostra nel pannello help la voce indicata
        /// </summary>
        /// <param name="key">Chiave dell'help</param>
        void SetHelp(string key);

        /// <summary>
        /// Errori da mostrare sotto il campo
        /// </summary>
        /// <param name="field">Campo validato</param>
        /// <returns>Errori del campo nella sezione aperta</returns>
        IEnumerable<PipelineInitializationIssue> FieldErrors(string field);
    }
}
