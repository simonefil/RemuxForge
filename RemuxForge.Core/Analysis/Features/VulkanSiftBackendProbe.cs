using RemuxForge.Core.Models;
using RemuxForge.Vulkan;
using System;

namespace RemuxForge.Core.Analysis.Features
{
    /// <summary>
    /// Verifica che il backend SIFT Vulkan sia inizializzabile nella sessione corrente
    /// </summary>
    public class VulkanSiftBackendProbe
    {
        #region Metodi pubblici

        /// <summary>
        /// Crea contesto e pipeline usando lo stesso percorso del backend produttivo
        /// </summary>
        /// <returns>Disponibilità, device selezionato ed eventuale errore</returns>
        public VulkanSiftBackendProbeResult Probe()
        {
            VulkanSiftBackendProbeResult result = new VulkanSiftBackendProbeResult();
            VulkanVisionContext context = null;
            VulkanSiftPipeline pipeline = null;

            try
            {
                VulkanVisionOptions options = new VulkanVisionOptions();
                context = new VulkanVisionContext(options);
                pipeline = context.CreateSiftPipeline();
                result.Available = true;
                result.DeviceName = context.Capabilities.DeviceName;
            }
            catch (Exception ex)
            {
                result.ErrorMessage = ex.Message;
            }
            finally
            {
                pipeline?.Dispose();
                context?.Dispose();
            }

            return result;
        }

        #endregion
    }
}
