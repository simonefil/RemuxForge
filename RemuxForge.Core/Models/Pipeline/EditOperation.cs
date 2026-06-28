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

        #endregion

        #region Proprieta

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
        /// Espressa sempre nella timeline originale del lang, prima di eventuale stretch mkvmerge
        /// </summary>
        public int DurationMs { get; set; }

        /// <summary>
        /// Timestamp corrispondente nel source/finale in millisecondi, per log, debug e source-fill
        /// </summary>
        public int SourceTimestampMs { get; set; }

        #endregion
    }
}
