using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Mappa completa delle operazioni di edit prodotta dalla deep analysis
    /// Descrive come riallineare le tracce lang al source
    /// </summary>
    public class EditMap
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public EditMap()
        {
            this.InitialDelayMs = 0;
            this.StretchFactor = "";
            this.Operations = new List<EditOperation>();
            this.AnalysisTimeMs = 0;
            this.BaselineMse = 0.0;
            this.Diagnostics = null;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Delay iniziale in ms da applicare al mux, separato dagli edit iniziali materializzati in Operations
        /// </summary>
        public int InitialDelayMs { get; set; }

        /// <summary>
        /// Stretch ratio come stringa per mkvmerge, vuoto se nessuno
        /// </summary>
        public string StretchFactor { get; set; }

        /// <summary>
        /// Lista ordinata per timestamp delle operazioni di edit
        /// </summary>
        public List<EditOperation> Operations { get; set; }

        /// <summary>
        /// Tempo di esecuzione analisi in ms
        /// </summary>
        public long AnalysisTimeMs { get; set; }

        /// <summary>
        /// MSE medio tra frame allineati (baseline qualità match)
        /// </summary>
        public double BaselineMse { get; set; }

        /// <summary>
        /// Diagnostica strutturata DeepAnalysis, se disponibile
        /// </summary>
        [JsonIgnore]
        public DeepAnalysisDiagnostics Diagnostics { get; set; }

        #endregion
    }
}
