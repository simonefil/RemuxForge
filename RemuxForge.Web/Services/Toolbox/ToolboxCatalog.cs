using Microsoft.Extensions.DependencyInjection;
using RemuxForge.Core.Models;
using RemuxForge.Web.Components.Toolbox.TrackExtraction;
using RemuxForge.Web.Components.Toolbox.VideoToMkv;
using System;
using System.Collections.Generic;

namespace RemuxForge.Web.Services.Toolbox
{
    /// <summary>
    /// Elenco ordinato delle card della home e accesso ai loro orchestratori
    /// </summary>
    public sealed class ToolboxCatalog
    {
        #region Variabili di classe

        /// <summary>
        /// Provider da cui si risolvono gli orchestratori singleton
        /// </summary>
        private readonly IServiceProvider _services;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="services">Provider dei servizi applicativi</param>
        public ToolboxCatalog(IServiceProvider services)
        {
            this._services = services;
            this.Cards = new List<ToolboxCard>
            {
                new ToolboxCard(Options.MODE_REMUX, "web.toolbox.cards.remux.title", "web.toolbox.cards.remux.description", "movie", typeof(MergeOrchestrator), null, "web.progress.episode"),
                new ToolboxCard(Options.MODE_SPLIT, "web.toolbox.cards.split.title", "web.toolbox.cards.split.description", "content_cut", typeof(SplitOrchestrator), null, "web.progress.episode"),
                new ToolboxCard(Options.MODE_METADATA, "web.toolbox.cards.metadata.title", "web.toolbox.cards.metadata.description", "tune", typeof(MetadataOrchestrator), null, "web.progress.episode"),
                new ToolboxCard("video-to-mkv", "web.toolbox.cards.videoToMkv.title", "web.toolbox.cards.videoToMkv.description", "video_file", typeof(VideoToMkvOrchestrator), typeof(VideoToMkvPage), "web.progress.file"),
                new ToolboxCard("track-extraction", "web.toolbox.cards.trackExtraction.title", "web.toolbox.cards.trackExtraction.description", "call_split", typeof(TrackExtractionOrchestrator), typeof(TrackExtractionPage), "web.progress.file")
            };
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Card nell'ordine della home
        /// </summary>
        public IReadOnlyList<ToolboxCard> Cards { get; }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Cerca una card per identificativo
        /// </summary>
        /// <param name="id">Identificativo della card</param>
        /// <returns>Card trovata, null se l'identificativo è vuoto o ignoto</returns>
        public ToolboxCard Find(string id)
        {
            foreach (ToolboxCard card in this.Cards)
            {
                if (string.Equals(card.Id, id, StringComparison.Ordinal))
                    return card;
            }

            return null;
        }

        /// <summary>
        /// Restituisce l'orchestratore di una card
        /// </summary>
        /// <param name="card">Card richiesta</param>
        /// <returns>Orchestratore singleton della card</returns>
        public MediaOrchestratorBase GetOrchestrator(ToolboxCard card)
        {
            return (MediaOrchestratorBase)this._services.GetRequiredService(card.OrchestratorType);
        }

        /// <summary>
        /// Verifica se una card sta eseguendo un'operazione
        /// </summary>
        /// <returns>True se almeno un orchestratore è occupato</returns>
        public bool IsAnyBusy()
        {
            foreach (ToolboxCard card in this.Cards)
            {
                if (this.GetOrchestrator(card).IsBusy)
                    return true;
            }

            return false;
        }

        #endregion
    }
}
