using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Evidenza della compensazione audio fissa, distinta dalla mappa video
    /// </summary>
    public class DeepAudioOffsetDiagnostic
    {
        /// <summary>True quando finestre distribuite confermano una compensazione costante</summary>
        public bool Accepted { get; set; }
        /// <summary>Offset da applicare ai campioni language nel dominio nativo: negativo anticipa</summary>
        public int LanguageOffsetMs { get; set; }
        /// <summary>Dispersione delle misure affidabili nel dominio source</summary>
        public double SpreadMs { get; set; }
        /// <summary>Motivo dell'astensione, vuoto quando accettato</summary>
        public string Reason { get; set; } = "not_measured";
        /// <summary>Finestre misurate dopo aver congelato la mappa video</summary>
        public List<DeepAudioOffsetWindow> Windows { get; set; } = new List<DeepAudioOffsetWindow>();
    }

    /// <summary>
    /// Misura audio locale che non modifica le operazioni video
    /// </summary>
    public class DeepAudioOffsetWindow
    {
        /// <summary>Inizio della finestra nel dominio source</summary>
        public double SourceStartMs { get; set; }
        /// <summary>Residuo misurato: positivo quando l'audio language è in ritardo</summary>
        public double ResidualMs { get; set; }
        /// <summary>Correlazione normalizzata al massimo locale</summary>
        public double Correlation { get; set; }
        /// <summary>True quando dinamica, correlazione e unicità rendono utilizzabile la misura</summary>
        public bool Reliable { get; set; }
    }
}
