namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Risultato del probe del backend SIFT Vulkan
    /// </summary>
    public class VulkanSiftBackendProbeResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public VulkanSiftBackendProbeResult()
        {
            this.DeviceName = "";
            this.ErrorMessage = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True se contesto e pipeline Vulkan sono inizializzabili
        /// </summary>
        public bool Available { get; set; }

        /// <summary>
        /// Nome del device Vulkan selezionato
        /// </summary>
        public string DeviceName { get; set; }

        /// <summary>
        /// Errore del probe, vuoto quando il backend è disponibile
        /// </summary>
        public string ErrorMessage { get; set; }

        #endregion
    }
}
