namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Sezione operativa di un messaggio di log
    /// </summary>
    public enum LogSection
    {
        /// <summary>
        /// Flusso principale, messaggi generici
        /// </summary>
        General,

        /// <summary>
        /// Configurazione e validazione parametri
        /// </summary>
        Config,

        /// <summary>
        /// Correzione velocità (SpeedCorrectionService)
        /// </summary>
        Speed,

        /// <summary>
        /// Deep analysis SIFT e applicazione degli edit di timeline
        /// </summary>
        Deep,

        /// <summary>
        /// Sincronizzazione frame (FrameSyncService)
        /// </summary>
        FrameSync,

        /// <summary>
        /// Processing audio (AudioProcessingService)
        /// </summary>
        Conv,

        /// <summary>
        /// Encoding video (VideoEncodingService)
        /// </summary>
        Encode,

        /// <summary>
        /// Merge tracce (MkvToolsService)
        /// </summary>
        Merge,

        /// <summary>
        /// Split MKV (MkvSplitService)
        /// </summary>
        Split,

        /// <summary>
        /// Setup e download ffmpeg (FfmpegProvider)
        /// </summary>
        Ffmpeg,

        /// <summary>
        /// Report finale
        /// </summary>
        Report
    }
}
