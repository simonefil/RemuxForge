using System;
using System.Threading.Tasks;

namespace RemuxForge.Web.Services
{
    /// <summary>
    /// Destinazioni nelle quali renderizzare un comando UI
    /// </summary>
    [Flags]
    public enum UiCommandPlacement
    {
        None = 0,
        Menu = 1,
        Toolbar = 2,
        Status = 4,
        ContextMenu = 8
    }

    /// <summary>
    /// Sezione del menu applicativo
    /// </summary>
    public enum UiCommandMenuSection
    {
        None,
        File,
        Actions,
        Settings,
        Help
    }

    /// <summary>
    /// Definizione condivisa di un comando UI e della relativa esecuzione
    /// </summary>
    public class UiCommandDefinition
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public UiCommandDefinition()
        {
            this.Label = "";
            this.ToolbarLabel = "";
            this.StatusLabel = "";
            this.Shortcut = "";
            this.Icon = "";
            this.Callback = null;
            this.AsyncCallback = null;
        }

        /// <summary>
        /// Costruttore comando sincrono
        /// </summary>
        public UiCommandDefinition(
            string label,
            string shortcut,
            string icon,
            UiCommandPlacement placement,
            UiCommandMenuSection menuSection,
            bool disabled,
            Action callback) : this()
        {
            this.Label = label;
            this.Shortcut = shortcut;
            this.Icon = icon;
            this.Placement = placement;
            this.MenuSection = menuSection;
            this.Disabled = disabled;
            this.Callback = callback;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Invoca il comando se attivo
        /// </summary>
        public async Task ExecuteAsync()
        {
            if (this.Disabled)
            {
                return;
            }

            if (this.AsyncCallback != null)
            {
                await this.AsyncCallback.Invoke();
            }
            else if (this.Callback != null)
            {
                this.Callback.Invoke();
            }
        }

        /// <summary>
        /// Restituisce l'etichetta per la toolbar
        /// </summary>
        /// <returns>Etichetta specifica o principale</returns>
        public string GetToolbarLabel()
        {
            return string.IsNullOrEmpty(this.ToolbarLabel) ? this.Label : this.ToolbarLabel;
        }

        /// <summary>
        /// Restituisce l'etichetta per la status bar
        /// </summary>
        /// <returns>Etichetta specifica o principale</returns>
        public string GetStatusLabel()
        {
            return string.IsNullOrEmpty(this.StatusLabel) ? this.Label : this.StatusLabel;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Etichetta principale
        /// </summary>
        public string Label { get; set; }

        /// <summary>
        /// Etichetta alternativa per toolbar
        /// </summary>
        public string ToolbarLabel { get; set; }

        /// <summary>
        /// Etichetta alternativa per status bar
        /// </summary>
        public string StatusLabel { get; set; }

        /// <summary>
        /// Scorciatoia visualizzata
        /// </summary>
        public string Shortcut { get; set; }

        /// <summary>
        /// Icona Radzen
        /// </summary>
        public string Icon { get; set; }

        /// <summary>
        /// Ordine nella toolbar
        /// </summary>
        public int ToolbarOrder { get; set; }

        /// <summary>
        /// Ordine nella status bar
        /// </summary>
        public int StatusOrder { get; set; }

        /// <summary>
        /// Destinazioni di rendering
        /// </summary>
        public UiCommandPlacement Placement { get; set; }

        /// <summary>
        /// Sezione menu proprietaria
        /// </summary>
        public UiCommandMenuSection MenuSection { get; set; }

        /// <summary>
        /// True per inserire un separatore prima della voce menu
        /// </summary>
        public bool SeparatorBefore { get; set; }

        /// <summary>
        /// True per renderizzare nella zona secondaria della toolbar
        /// </summary>
        public bool SecondaryToolbar { get; set; }

        /// <summary>
        /// True per lo stile di pericolo della toolbar
        /// </summary>
        public bool DangerToolbar { get; set; }

        /// <summary>
        /// True se il comando non è invocabile
        /// </summary>
        public bool Disabled { get; set; }

        /// <summary>
        /// Callback sincrona
        /// </summary>
        public Action Callback { get; set; }

        /// <summary>
        /// Callback asincrona
        /// </summary>
        public Func<Task> AsyncCallback { get; set; }

        #endregion
    }
}
