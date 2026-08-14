namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Risultato della fingerprint audio globale usata per stimare e validare l'offset temporale
    /// </summary>
    public class AudioGlobalFingerprintResult
    {
        #region Costruttore

        /// <summary>
        /// Inizializza il risultato della fingerprint audio con valori che indicano l'assenza di un candidato valido
        /// </summary>
        public AudioGlobalFingerprintResult()
        {
            this.Success = false;
            this.OffsetMs = int.MinValue;
            this.Score = 0.0;
            this.Margin = 0.0;
            this.Coverage = 0.0;
            this.EnvelopeScore = 0.0;
            this.SilenceScore = 0.0;
            this.OnsetScore = 0.0;
            this.DerivativeScore = 0.0;
            this.SilenceRunScore = 0.0;
            this.ChunkScore = 0.0;
            this.VideoOffsetMs = int.MinValue;
            this.AudioVideoDeltaMs = int.MinValue;
            this.ConfirmedVideoInitial = false;
            this.RejectedVideoInitial = false;
            this.CandidateCount = 0;
            this.WindowMs = 0;
            this.TimingMs = 0;
            this.ExtractionMs = 0;
            this.CorrelationMs = 0;
            this.SourceCacheHit = false;
            this.LanguageCacheHit = false;
            this.FailureReason = "";
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// Indica se la fingerprint audio ha prodotto un candidato sufficientemente netto
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Offset interno in millisecondi calcolato come langTime - sourceTime
        /// </summary>
        public int OffsetMs { get; set; }

        /// <summary>
        /// Score globale normalizzato
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// Margine tra il primo candidato e il secondo candidato distinto
        /// </summary>
        public double Margin { get; set; }

        /// <summary>
        /// Copertura temporale media delle finestre usate nel confronto
        /// </summary>
        public double Coverage { get; set; }

        /// <summary>
        /// Punteggio di correlazione dell'envelope RMS/energia
        /// </summary>
        public double EnvelopeScore { get; set; }

        /// <summary>
        /// Punteggio di concordanza della maschera dei silenzi
        /// </summary>
        public double SilenceScore { get; set; }

        /// <summary>
        /// Punteggio di correlazione degli onset e delle variazioni di energia
        /// </summary>
        public double OnsetScore { get; set; }

        /// <summary>
        /// Punteggio di correlazione della derivata con segno dell'envelope
        /// </summary>
        public double DerivativeScore { get; set; }

        /// <summary>
        /// Punteggio di concordanza della forma run-length dei silenzi
        /// </summary>
        public double SilenceRunScore { get; set; }

        /// <summary>
        /// Punteggio medio distribuito sui chunk temporali
        /// </summary>
        public double ChunkScore { get; set; }

        /// <summary>
        /// Offset video iniziale confrontato con quello audio quando disponibile
        /// </summary>
        public int VideoOffsetMs { get; set; }

        /// <summary>
        /// Delta assoluto in millisecondi tra offset audio e offset video iniziale
        /// </summary>
        public int AudioVideoDeltaMs { get; set; }

        /// <summary>
        /// Indica se la fingerprint audio conferma l'offset video iniziale
        /// </summary>
        public bool ConfirmedVideoInitial { get; set; }

        /// <summary>
        /// Indica se la fingerprint audio rifiuta un offset video iniziale debole
        /// </summary>
        public bool RejectedVideoInitial { get; set; }

        /// <summary>
        /// Numero di offset candidati valutati
        /// </summary>
        public int CandidateCount { get; set; }

        /// <summary>
        /// Dimensione della finestra della fingerprint in millisecondi
        /// </summary>
        public int WindowMs { get; set; }

        /// <summary>
        /// Tempo complessivo di elaborazione in millisecondi
        /// </summary>
        public long TimingMs { get; set; }

        /// <summary>
        /// Tempo di estrazione e costruzione della fingerprint in millisecondi
        /// </summary>
        public long ExtractionMs { get; set; }

        /// <summary>
        /// Tempo di correlazione e ricerca dell'offset in millisecondi
        /// </summary>
        public long CorrelationMs { get; set; }

        /// <summary>
        /// Indica se la fingerprint source è stata recuperata dalla cache
        /// </summary>
        public bool SourceCacheHit { get; set; }

        /// <summary>
        /// Indica se la fingerprint language è stata recuperata dalla cache
        /// </summary>
        public bool LanguageCacheHit { get; set; }

        /// <summary>
        /// Motivo del fallimento, vuoto quando l'analisi ha esito positivo
        /// </summary>
        public string FailureReason { get; set; }

        #endregion
    }
}
