using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Risultato del probe dei metodi hardware esposti da ffmpeg
    /// </summary>
    public class FfmpegHardwareAccelerationProbeResult
    {
        /// <summary>
        /// Costruttore
        /// </summary>
        public FfmpegHardwareAccelerationProbeResult()
        {
            this.Methods = new List<string>();
            this.ErrorMessage = "";
        }

        /// <summary>
        /// Metodi che hanno creato correttamente un device nella sessione corrente
        /// </summary>
        public List<string> Methods { get; set; }

        /// <summary>
        /// Errore complessivo del probe, vuoto se almeno un metodo è disponibile
        /// </summary>
        public string ErrorMessage { get; set; }
    }
}
