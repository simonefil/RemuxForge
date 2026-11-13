using System;

namespace RemuxForge.Web.Services.Toolbox
{
    /// <summary>
    /// Voce della home: testi, icona e orchestratore che esegue le operazioni della card
    /// </summary>
    public sealed class ToolboxCard
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="id">Identificativo stabile della card</param>
        /// <param name="titleKey">Chiave del titolo</param>
        /// <param name="descriptionKey">Chiave della descrizione</param>
        /// <param name="icon">Icona Material</param>
        /// <param name="orchestratorType">Tipo dell'orchestratore registrato nel DI</param>
        public ToolboxCard(string id, string titleKey, string descriptionKey, string icon, Type orchestratorType)
        {
            this.Id = id;
            this.TitleKey = titleKey;
            this.DescriptionKey = descriptionKey;
            this.Icon = icon;
            this.OrchestratorType = orchestratorType;
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Identificativo stabile, salvato come ultima card usata
        /// </summary>
        public string Id { get; }

        /// <summary>
        /// Chiave del titolo
        /// </summary>
        public string TitleKey { get; }

        /// <summary>
        /// Chiave della descrizione di una riga
        /// </summary>
        public string DescriptionKey { get; }

        /// <summary>
        /// Icona Material
        /// </summary>
        public string Icon { get; }

        /// <summary>
        /// Tipo dell'orchestratore, derivato da <see cref="MediaOrchestratorBase"/>
        /// </summary>
        public Type OrchestratorType { get; }

        #endregion
    }
}
