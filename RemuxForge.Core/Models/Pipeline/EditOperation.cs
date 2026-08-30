namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Singola operazione di edit da applicare a una traccia lang per riallinearla al source
    /// </summary>
    public class EditOperation
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public EditOperation()
        {
            this.Type = "";
            this.Scope = SCOPE_BODY;
            this.GainDb = 0.0;
        }

        #endregion

        #region Costanti

        /// <summary>
        /// Tipo operazione: il source ha contenuto extra, inserire silenzio nel lang
        /// </summary>
        public const string INSERT_SILENCE = "INSERT_SILENCE";

        /// <summary>
        /// Tipo operazione: il lang ha contenuto extra, tagliare segmento dal lang
        /// </summary>
        public const string CUT_SEGMENT = "CUT_SEGMENT";

        /// <summary>
        /// Provenienza: l'offset con cui le due copie partono disallineate
        /// </summary>
        public const string SCOPE_HEAD = "HEAD";

        /// <summary>
        /// Provenienza: una differenza di montaggio rilevata dall'analisi
        /// </summary>
        public const string SCOPE_BODY = "BODY";

        /// <summary>
        /// Provenienza: il residuo di lunghezza fra le due copie, non un esito dell'analisi
        /// </summary>
        public const string SCOPE_TAIL = "TAIL";

        #endregion

        #region Proprietà

        /// <summary>
        /// Tipo operazione: INSERT_SILENCE o CUT_SEGMENT
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// Timestamp nel riferimento del lang in millisecondi
        /// Per INSERT_SILENCE: punto nel lang dove inserire il silenzio
        /// Per CUT_SEGMENT: inizio del segmento da tagliare nel lang
        /// </summary>
        public int LangTimestampMs { get; set; }

        /// <summary>
        /// Durata dell'operazione in millisecondi
        /// Espressa sempre nella timeline originale del lang, prima del render dello stretch
        /// </summary>
        public int DurationMs { get; set; }

        /// <summary>
        /// Gain in decibel applicato all'audio Source che riempie questa operazione Insert
        /// </summary>
        public double GainDb { get; set; }

        /// <summary>
        /// Timestamp corrispondente nel source/finale in millisecondi, per log, debug e source-fill
        /// </summary>
        public int SourceTimestampMs { get; set; }

        /// <summary>
        /// Boundary source/video verificato dalla DeepAnalysis prima del fine tuning audio
        /// </summary>
        public int VisualSourceTimestampMs { get; set; }

        /// <summary>
        /// Provenienza dell'operazione: SCOPE_HEAD, SCOPE_BODY o SCOPE_TAIL
        /// Testa e coda esistono perché le due copie non partono e non finiscono insieme,
        /// il corpo perché il montaggio è diverso: chi consuma la mappa non deve dedurlo
        /// </summary>
        public string Scope { get; set; }

        #endregion
    }
}
