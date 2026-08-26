using RemuxForge.Core.Configuration;
using RemuxForge.Core.Media.Ffmpeg;
using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Stato condiviso dai servizi di sincronizzazione video
    /// </summary>
    public abstract class VideoSyncServiceBase
    {
        #region Variabili di classe

        /// <summary>
        /// Configurazione VideoSync condivisa dal servizio
        /// </summary>
        protected VideoSyncConfig _vsConfig;

        /// <summary>
        /// Configurazione FFmpeg condivisa dal servizio
        /// </summary>
        protected FfmpegConfig _ffmpegConfig;

        /// <summary>
        /// Percorso dell'eseguibile FFmpeg
        /// </summary>
        protected string _ffmpegPath;

        /// <summary>
        /// Crop manuale source richiesto dall'utente
        /// </summary>
        protected string _analysisCropSourcePx;

        /// <summary>
        /// Crop manuale language richiesto dall'utente
        /// </summary>
        protected string _analysisCropLanguagePx;

        /// <summary>
        /// Sezione di log del servizio
        /// </summary>
        private readonly LogSection _logSection;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza configurazione e percorsi condivisi
        /// </summary>
        /// <param name="ffmpegPath">Percorso eseguibile FFmpeg</param>
        /// <param name="logSection">Sezione di log</param>
        protected VideoSyncServiceBase(string ffmpegPath, LogSection logSection)
        {
            this._ffmpegPath = ffmpegPath;
            this._logSection = logSection;
            this._vsConfig = AppSettingsService.Instance.Settings.Advanced.VideoSync;
            this._ffmpegConfig = AppSettingsService.Instance.Settings.Advanced.Ffmpeg;
            this._analysisCropSourcePx = "";
            this._analysisCropLanguagePx = "";
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Configura i crop manuali usati come autorità dal bootstrap geometrico
        /// </summary>
        /// <param name="sourceCropPx">Crop source L:R:T:B</param>
        /// <param name="languageCropPx">Crop language L:R:T:B</param>
        public void SetAnalysisCrop(string sourceCropPx, string languageCropPx)
        {
            this._analysisCropSourcePx = Options.NormalizeAnalysisCropPx(sourceCropPx);
            this._analysisCropLanguagePx = Options.NormalizeAnalysisCropPx(languageCropPx);
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Estrae frame campionati applicando un crop fisso già risolto dal bootstrap
        /// </summary>
        /// <param name="filePath">Percorso del video</param>
        /// <param name="startMs">Inizio estrazione in millisecondi</param>
        /// <param name="durationSec">Durata estrazione in secondi</param>
        /// <param name="sampleIntervalSec">Intervallo fra campioni in secondi</param>
        /// <param name="cropPx">Crop fisso L:R:T:B</param>
        /// <param name="frames">Frame grayscale estratti</param>
        /// <param name="timestampsMs">PTS dei frame estratti</param>
        protected void ExtractSegmentAtInterval(string filePath, int startMs, double durationSec, double sampleIntervalSec, string cropPx, out List<byte[]> frames, out double[] timestampsMs)
        {
            FrameExtractionService extractor = new FrameExtractionService(this._ffmpegPath, this._vsConfig, this._ffmpegConfig, this._logSection);
            extractor.ExtractSegmentAtInterval(filePath, startMs, durationSec, sampleIntervalSec, false, Options.NormalizeAnalysisCropPx(cropPx), out frames, out timestampsMs);
        }

        #endregion
    }
}
