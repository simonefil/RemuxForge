using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media.Ffmpeg;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Classe base per i servizi di sincronizzazione video basati sul confronto tra frame
    /// </summary>
    public abstract class VideoSyncServiceBase
    {
        #region Variabili di classe

        /// <summary>
        /// Configurazione VideoSync condivisa dal servizio, con modifiche applicate direttamente
        /// </summary>
        protected VideoSyncConfig _vsConfig;

        /// <summary>
        /// Configurazione Ffmpeg condivisa dal servizio, con modifiche applicate direttamente
        /// </summary>
        protected FfmpegConfig _ffmpegConfig;

        /// <summary>
        /// Percorso dell'eseguibile ffmpeg
        /// </summary>
        protected string _ffmpegPath;

        /// <summary>
        /// Sezione di log usata dal servizio
        /// </summary>
        private readonly LogSection _logSection;

        /// <summary>
        /// Indica se i frame del file sorgente devono essere ritagliati a 4:3 centrato quando richiesto dalla geometria
        /// </summary>
        protected bool _geometryCropSourceToFourThree;

        /// <summary>
        /// Indica se i frame del file lingua devono essere ritagliati a 4:3 centrato quando richiesto dalla geometria
        /// </summary>
        protected bool _geometryCropLanguageToFourThree;

        /// <summary>
        /// Crop manuale del file sorgente per l'analisi visuale nel formato L:R:T:B
        /// </summary>
        protected string _analysisCropSourcePx;

        /// <summary>
        /// Crop manuale del file lingua per l'analisi visuale nel formato L:R:T:B
        /// </summary>
        protected string _analysisCropLanguagePx;

        /// <summary>
        /// Analizzatore della geometria video condiviso dal servizio
        /// </summary>
        private readonly VideoGeometryAnalyzer _geometryAnalyzer;

        /// <summary>
        /// Normalizzatore dei bordi neri condiviso dal servizio
        /// </summary>
        private readonly BlackBorderNormalizer _blackBorderNormalizer;

        /// <summary>
        /// Rilevatore dei tagli di scena condiviso dal servizio
        /// </summary>
        private readonly SceneCutDetector _sceneCutDetector;

        /// <summary>
        /// Calcolatore delle metriche visuali condiviso dal servizio
        /// </summary>
        private readonly VisualMetricCalculator _visualMetricCalculator;

        /// <summary>
        /// Ultima geometria diagnostica del file sorgente preparata dal servizio
        /// </summary>
        protected FrameSyncGeometryInfo _lastSourceGeometryInfo;

        /// <summary>
        /// Ultima geometria diagnostica del file lingua preparata dal servizio
        /// </summary>
        protected FrameSyncGeometryInfo _lastLanguageGeometryInfo;

        #endregion

        #region Costruttore

        /// <summary>
        /// Inizializza il servizio con l'eseguibile ffmpeg e la sezione di log da utilizzare
        /// </summary>
        /// <param name="ffmpegPath">Percorso eseguibile ffmpeg</param>
        /// <param name="logSection">Sezione di log per messaggi</param>
        protected VideoSyncServiceBase(string ffmpegPath, LogSection logSection)
        {
            this._ffmpegPath = ffmpegPath;
            this._logSection = logSection;
            this._vsConfig = AppSettingsService.Instance.Settings.Advanced.VideoSync;
            this._ffmpegConfig = AppSettingsService.Instance.Settings.Advanced.Ffmpeg;
            this._geometryCropSourceToFourThree = false;
            this._geometryCropLanguageToFourThree = false;
            this._analysisCropSourcePx = "";
            this._analysisCropLanguagePx = "";
            this._geometryAnalyzer = new VideoGeometryAnalyzer(this._ffmpegPath, this._ffmpegConfig, this._logSection);
            this._blackBorderNormalizer = new BlackBorderNormalizer(this._ffmpegPath, this._vsConfig, this._ffmpegConfig, this._logSection, this._geometryAnalyzer);
            this._sceneCutDetector = new SceneCutDetector(this._vsConfig);
            this._visualMetricCalculator = new VisualMetricCalculator(this._vsConfig);
            this._lastSourceGeometryInfo = null;
            this._lastLanguageGeometryInfo = null;
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Configura il crop manuale dei file sorgente e lingua usato solo per l'analisi visuale
        /// </summary>
        /// <param name="sourceCropPx">Crop manuale del file sorgente nel formato L:R:T:B</param>
        /// <param name="languageCropPx">Crop manuale del file lingua nel formato L:R:T:B</param>
        public void SetAnalysisCrop(string sourceCropPx, string languageCropPx)
        {
            this._analysisCropSourcePx = Options.NormalizeAnalysisCropPx(sourceCropPx);
            this._analysisCropLanguagePx = Options.NormalizeAnalysisCropPx(languageCropPx);
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Estrae i frame di un segmento video applicando l'eventuale crop manuale di analisi
        /// </summary>
        /// <param name="filePath">Percorso del file video</param>
        /// <param name="startMs">Inizio dell'estrazione in millisecondi</param>
        /// <param name="durationSec">Durata dell'estrazione in secondi</param>
        /// <param name="targetFps">Frequenza dei frame target per la normalizzazione, con 0 per non decimare i frame</param>
        /// <param name="geometryCropToFourThree">Indica se ritagliare il frame a 4:3 centrato prima del ridimensionamento per rimuovere il pillarbox</param>
        /// <param name="manualCropPx">Crop manuale L:R:T:B in pixel, che sostituisce il crop geometrico 4:3 quando configurato</param>
        /// <param name="frames">Lista dei frame in scala di grigi estratti</param>
        /// <param name="timestampsMs">Array dei timestamp assoluti in millisecondi, uno per ogni frame estratto</param>
        protected void ExtractSegment(string filePath, int startMs, double durationSec, double targetFps, bool geometryCropToFourThree, string manualCropPx, out List<byte[]> frames, out double[] timestampsMs)
        {
            FrameExtractionService extractor = new FrameExtractionService(this._ffmpegPath, this._vsConfig, this._ffmpegConfig, this._logSection);
            string normalizedManualCrop = Options.NormalizeAnalysisCropPx(manualCropPx);
            bool effectiveGeometryCrop = this.UseGeometryCrop(geometryCropToFourThree, normalizedManualCrop);

            extractor.ExtractSegment(filePath, startMs, durationSec, targetFps, effectiveGeometryCrop, normalizedManualCrop, out frames, out timestampsMs);
            this.NormalizeBlackBorders(filePath, effectiveGeometryCrop, normalizedManualCrop, frames);
        }

        /// <summary>
        /// Estrae frame campionati a intervalli regolari conservando i PTS originali selezionati
        /// </summary>
        /// <param name="filePath">Percorso del file video</param>
        /// <param name="startMs">Inizio dell'estrazione in millisecondi</param>
        /// <param name="durationSec">Durata dell'estrazione in secondi</param>
        /// <param name="sampleIntervalSec">Intervallo di campionamento in secondi</param>
        /// <param name="geometryCropToFourThree">Indica se applicare il crop geometrico 4:3 centrato</param>
        /// <param name="manualCropPx">Crop manuale L:R:T:B in pixel, che sostituisce il crop geometrico 4:3 quando configurato</param>
        /// <param name="frames">Lista dei frame in scala di grigi estratti</param>
        /// <param name="timestampsMs">Array dei timestamp assoluti in millisecondi, uno per ogni frame estratto</param>
        protected void ExtractSegmentAtInterval(string filePath, int startMs, double durationSec, double sampleIntervalSec, bool geometryCropToFourThree, string manualCropPx, out List<byte[]> frames, out double[] timestampsMs)
        {
            FrameExtractionService extractor = new FrameExtractionService(this._ffmpegPath, this._vsConfig, this._ffmpegConfig, this._logSection);
            string normalizedManualCrop = Options.NormalizeAnalysisCropPx(manualCropPx);
            bool effectiveGeometryCrop = this.UseGeometryCrop(geometryCropToFourThree, normalizedManualCrop);
            extractor.ExtractSegmentAtInterval(filePath, startMs, durationSec, sampleIntervalSec, effectiveGeometryCrop, normalizedManualCrop, out frames, out timestampsMs);
            this.NormalizeBlackBorders(filePath, effectiveGeometryCrop, normalizedManualCrop, frames);
        }

        /// <summary>
        /// Analizza e memorizza in cache la geometria video rilevante per il confronto tra frame
        /// </summary>
        /// <param name="filePath">Percorso file video</param>
        /// <returns>Profilo geometria, o null se non rilevabile</returns>
        protected VideoGeometryProfile AnalyzeVideoGeometry(string filePath)
        {
            return this._geometryAnalyzer.Analyze(filePath);
        }

        /// <summary>
        /// Registra il confronto geometrico tra i file sorgente e lingua
        /// </summary>
        /// <param name="sourceGeometry">Geometria del file sorgente</param>
        /// <param name="languageGeometry">Geometria del file lingua</param>
        protected void LogVideoGeometryComparison(VideoGeometryProfile sourceGeometry, VideoGeometryProfile languageGeometry)
        {
            double aspectDiff;
            bool geometryMismatch;
            if (sourceGeometry == null || languageGeometry == null)
            {
                return;
            }

            aspectDiff = Math.Abs(sourceGeometry.DisplayAspect - languageGeometry.DisplayAspect);
            geometryMismatch = sourceGeometry.Width != languageGeometry.Width || sourceGeometry.Height != languageGeometry.Height || sourceGeometry.SarNum != languageGeometry.SarNum || sourceGeometry.SarDen != languageGeometry.SarDen || aspectDiff > 0.01;

            ConsoleHelper.Write(this._logSection, LogLevel.Debug, AppText.F("deep.temporal.geometry.source", sourceGeometry.ToShortString()));
            ConsoleHelper.Write(this._logSection, LogLevel.Debug, AppText.F("deep.temporal.geometry.language", languageGeometry.ToShortString()));

            if (geometryMismatch)
            {
                ConsoleHelper.Write(this._logSection, LogLevel.Notice, AppText.T("deep.temporal.geometry.mismatch"));
            }
        }

        /// <summary>
        /// Analizza la geometria dei file sorgente e lingua e prepara il crop 4:3 automatico quando il confronto è tra pillarbox e 4:3 nativo
        /// </summary>
        /// <param name="sourceFile">Percorso del file sorgente</param>
        /// <param name="languageFile">Percorso del file lingua</param>
        protected void PrepareGeometryDrivenCrop(string sourceFile, string languageFile)
        {
            VideoGeometryProfile sourceGeometry;
            VideoGeometryProfile languageGeometry;
            string sourceManualCrop;
            string languageManualCrop;
            bool sourceGeometryCrop;
            bool languageGeometryCrop;
            this._lastSourceGeometryInfo = null;
            this._lastLanguageGeometryInfo = null;
            this._geometryCropSourceToFourThree = false;
            this._geometryCropLanguageToFourThree = false;
            this._blackBorderNormalizer.Reset();

            sourceManualCrop = this._analysisCropSourcePx;
            languageManualCrop = this._analysisCropLanguagePx;
            this.LogManualAnalysisCrop("source", sourceManualCrop);
            this.LogManualAnalysisCrop("lang", languageManualCrop);

            sourceGeometry = this.AnalyzeVideoGeometry(sourceFile);
            languageGeometry = this.AnalyzeVideoGeometry(languageFile);

            this._blackBorderNormalizer.PrepareFile(sourceFile, 0, false, sourceManualCrop);
            this._blackBorderNormalizer.PrepareFile(languageFile, 0, false, languageManualCrop);

            this.LogVideoGeometryComparison(sourceGeometry, languageGeometry);
            this.ApplyGeometryDrivenCrop(sourceGeometry, languageGeometry, sourceManualCrop, languageManualCrop);

            sourceGeometryCrop = this.UseGeometryCrop(this._geometryCropSourceToFourThree, sourceManualCrop);
            languageGeometryCrop = this.UseGeometryCrop(this._geometryCropLanguageToFourThree, languageManualCrop);
            if (sourceGeometryCrop || languageGeometryCrop)
            {
                this._blackBorderNormalizer.Reset();
                this._blackBorderNormalizer.PrepareFile(sourceFile, 0, sourceGeometryCrop, sourceManualCrop);
                this._blackBorderNormalizer.PrepareFile(languageFile, 0, languageGeometryCrop, languageManualCrop);
            }

            this._lastSourceGeometryInfo = this.BuildGeometryInfo(sourceGeometry, sourceGeometryCrop, sourceManualCrop);
            this._lastLanguageGeometryInfo = this.BuildGeometryInfo(languageGeometry, languageGeometryCrop, languageManualCrop);
        }

        /// <summary>
        /// Attiva il crop 4:3 automatico quando la geometria indica un confronto tra pillarbox 16:9 e 4:3 nativo
        /// </summary>
        /// <param name="sourceGeometry">Geometria del file sorgente</param>
        /// <param name="languageGeometry">Geometria del file lingua</param>
        /// <param name="sourceManualCropPx">Crop manuale configurato per il file sorgente</param>
        /// <param name="languageManualCropPx">Crop manuale configurato per il file lingua</param>
        protected void ApplyGeometryDrivenCrop(VideoGeometryProfile sourceGeometry, VideoGeometryProfile languageGeometry, string sourceManualCropPx, string languageManualCropPx)
        {
            bool sourceFourThree = this.IsDisplayAspectFourThree(sourceGeometry);
            bool languageFourThree = this.IsDisplayAspectFourThree(languageGeometry);
            bool sourceWide = this.IsDisplayAspectWide(sourceGeometry);
            bool languageWide = this.IsDisplayAspectWide(languageGeometry);
            bool sourceSquare = this.IsSquarePixelGeometry(sourceGeometry);
            bool languageSquare = this.IsSquarePixelGeometry(languageGeometry);
            bool sourceHasBorders = sourceGeometry != null && sourceGeometry.HasBlackBorderCrop;
            bool languageHasBorders = languageGeometry != null && languageGeometry.HasBlackBorderCrop;

            if (sourceGeometry == null || languageGeometry == null)
            {
                return;
            }

            if (sourceWide && languageFourThree && sourceSquare && sourceHasBorders && string.IsNullOrEmpty(sourceManualCropPx))
            {
                this._geometryCropSourceToFourThree = true;
                ConsoleHelper.Write(this._logSection, LogLevel.Notice, AppText.T("deep.temporal.geometry.sourceCropFourThree"));
            }

            if (languageWide && sourceFourThree && languageSquare && languageHasBorders && string.IsNullOrEmpty(languageManualCropPx))
            {
                this._geometryCropLanguageToFourThree = true;
                ConsoleHelper.Write(this._logSection, LogLevel.Notice, AppText.T("deep.temporal.geometry.languageCropFourThree"));
            }
        }

        /// <summary>
        /// Verifica se il rapporto di visualizzazione della geometria è circa 4:3
        /// </summary>
        /// <param name="geometry">Profilo della geometria video</param>
        /// <returns>True se il rapporto di visualizzazione è circa 4:3</returns>
        protected bool IsDisplayAspectFourThree(VideoGeometryProfile geometry)
        {
            bool result = false;
            if (geometry != null)
            {
                result = geometry.DisplayAspect >= 1.28 && geometry.DisplayAspect <= 1.39;
            }

            return result;
        }

        /// <summary>
        /// Verifica se il rapporto di visualizzazione della geometria è circa 16:9
        /// </summary>
        /// <param name="geometry">Profilo della geometria video</param>
        /// <returns>True se il rapporto di visualizzazione è circa 16:9</returns>
        protected bool IsDisplayAspectWide(VideoGeometryProfile geometry)
        {
            bool result = false;
            if (geometry != null)
            {
                result = geometry.DisplayAspect >= 1.70 && geometry.DisplayAspect <= 1.86;
            }

            return result;
        }

        /// <summary>
        /// Verifica se la geometria usa pixel quasi quadrati
        /// </summary>
        /// <param name="geometry">Profilo della geometria video</param>
        /// <returns>True se il rapporto SAR è circa 1:1</returns>
        protected bool IsSquarePixelGeometry(VideoGeometryProfile geometry)
        {
            bool result = false;
            double sar;

            if (geometry != null && geometry.SarDen > 0)
            {
                sar = geometry.SarNum / (double)geometry.SarDen;
                result = Math.Abs(sar - 1.0) <= 0.02;
            }

            return result;
        }

        /// <summary>
        /// Crea il DTO diagnostico a partire dalla geometria interna
        /// </summary>
        /// <param name="geometry">Profilo della geometria interna</param>
        /// <param name="geometryCropToFourThree">Indica se il crop 4:3 è stato attivato dalla geometria</param>
        /// <param name="manualCropPx">Crop manuale L:R:T:B in pixel</param>
        /// <returns>DTO diagnostico oppure null se la geometria non è disponibile</returns>
        protected FrameSyncGeometryInfo BuildGeometryInfo(VideoGeometryProfile geometry, bool geometryCropToFourThree, string manualCropPx)
        {
            FrameSyncGeometryInfo result = null;
            string normalizedManualCrop = Options.NormalizeAnalysisCropPx(manualCropPx);
            if (geometry != null)
            {
                result = new FrameSyncGeometryInfo();
                result.FilePath = geometry.FilePath;
                result.Width = geometry.Width;
                result.Height = geometry.Height;
                result.SarNum = geometry.SarNum;
                result.SarDen = geometry.SarDen;
                result.DarNum = geometry.DarNum;
                result.DarDen = geometry.DarDen;
                result.DisplayWidth = geometry.DisplayWidth;
                result.DisplayHeight = geometry.DisplayHeight;
                result.DisplayAspect = geometry.DisplayAspect;
                result.HasBlackBorderCrop = geometry.HasBlackBorderCrop;
                result.CropLeft = geometry.CropLeft;
                result.CropRight = geometry.CropRight;
                result.CropTop = geometry.CropTop;
                result.CropBottom = geometry.CropBottom;
                result.ManualAnalysisCropPx = normalizedManualCrop;
                result.GeometryCropToFourThree = this.UseGeometryCrop(geometryCropToFourThree, normalizedManualCrop);
                if (!string.IsNullOrEmpty(result.ManualAnalysisCropPx))
                {
                    result.CropMode = "manual_analysis_crop";
                }
                else if (result.GeometryCropToFourThree)
                {
                    result.CropMode = "geometry_four_three";
                }
                else if (geometry.HasBlackBorderCrop)
                {
                    result.CropMode = "black_border_autocrop";
                }
                else
                {
                    result.CropMode = "none";
                }
            }

            return result;
        }

        /// <summary>
        /// Applica ai frame il profilo dei bordi neri già preparato per la variante di crop richiesta
        /// </summary>
        /// <param name="filePath">Percorso del file, usato come chiave per il profilo in cache</param>
        /// <param name="geometryCropToFourThree">Indica se i frame sono stati estratti con crop geometrico 4:3</param>
        /// <param name="manualCropPx">Crop manuale L:R:T:B applicato ai frame</param>
        /// <param name="frames">Frame in scala di grigi da normalizzare direttamente</param>
        protected void NormalizeBlackBorders(string filePath, bool geometryCropToFourThree, string manualCropPx, List<byte[]> frames)
        {
            this._blackBorderNormalizer.Normalize(filePath, geometryCropToFourThree, manualCropPx, frames);
        }

        /// <summary>
        /// Verifica se il crop geometrico può restare attivo con il crop manuale corrente
        /// </summary>
        /// <param name="geometryCropToFourThree">Indica se è richiesto il crop geometrico 4:3</param>
        /// <param name="manualCropPx">Crop manuale normalizzato o non normalizzato</param>
        /// <returns>True se il crop geometrico deve essere applicato</returns>
        protected bool UseGeometryCrop(bool geometryCropToFourThree, string manualCropPx)
        {
            return geometryCropToFourThree && string.IsNullOrEmpty(Options.NormalizeAnalysisCropPx(manualCropPx));
        }

        /// <summary>
        /// Registra il crop manuale di analisi quando è configurato
        /// </summary>
        /// <param name="role">Ruolo del file nel confronto</param>
        /// <param name="manualCropPx">Crop manuale normalizzato</param>
        private void LogManualAnalysisCrop(string role, string manualCropPx)
        {
            if (!string.IsNullOrEmpty(manualCropPx))
            {
                ConsoleHelper.Write(this._logSection, LogLevel.Notice, AppText.F("deep.temporal.geometry.manualCrop", role, manualCropPx));
            }
        }

        /// <summary>
        /// Calcola lo SSIM medio di una sequenza di frame consecutivi tra due file
        /// </summary>
        /// <param name="sourceFrames">Lista dei frame sorgente</param>
        /// <param name="sourceStartIdx">Indice iniziale nella lista dei frame sorgente</param>
        /// <param name="langFrames">Lista dei frame lingua</param>
        /// <param name="langStartIdx">Indice iniziale nella lista dei frame lingua</param>
        /// <param name="sequenceLength">Numero di frame nella sequenza</param>
        /// <returns>SSIM medio della sequenza oppure 0,0 se i frame sono insufficienti</returns>
        protected double ComputeSequenceSsim(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceSsim(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Calcola la correlazione media su una sequenza usando luma sfocata
        /// </summary>
        /// <param name="sourceFrames">Lista dei frame sorgente</param>
        /// <param name="sourceStartIdx">Indice iniziale nella lista dei frame sorgente</param>
        /// <param name="langFrames">Lista dei frame lingua</param>
        /// <param name="langStartIdx">Indice iniziale nella lista dei frame lingua</param>
        /// <param name="sequenceLength">Numero di frame nella sequenza</param>
        /// <returns>Correlazione media normalizzata tra 0 e 1 oppure 0,0 se i frame sono insufficienti</returns>
        protected double ComputeSequenceBlurredCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceBlurredCorrelation(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Calcola la correlazione edge media di una sequenza di frame consecutivi
        /// </summary>
        /// <param name="sourceFrames">Lista dei frame sorgente</param>
        /// <param name="sourceStartIdx">Indice iniziale nella lista dei frame sorgente</param>
        /// <param name="langFrames">Lista dei frame lingua</param>
        /// <param name="langStartIdx">Indice iniziale nella lista dei frame lingua</param>
        /// <param name="sequenceLength">Numero di frame nella sequenza</param>
        /// <returns>Correlazione media normalizzata tra 0 e 1 oppure 0,0 se i frame sono insufficienti</returns>
        protected double ComputeSequenceEdgeCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceEdgeCorrelation(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Calcola la correlazione media dei fingerprint a blocchi su una sequenza di frame
        /// </summary>
        /// <param name="sourceFrames">Lista dei frame sorgente</param>
        /// <param name="sourceStartIdx">Indice iniziale nella lista dei frame sorgente</param>
        /// <param name="langFrames">Lista dei frame lingua</param>
        /// <param name="langStartIdx">Indice iniziale nella lista dei frame lingua</param>
        /// <param name="sequenceLength">Numero di frame nella sequenza</param>
        /// <returns>Correlazione media normalizzata tra 0 e 1 oppure 0,0 se i frame sono insufficienti</returns>
        protected double ComputeSequenceBlockCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceBlockCorrelation(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Calcola la correlazione media edge-block su una sequenza
        /// </summary>
        /// <param name="sourceFrames">Lista dei frame sorgente</param>
        /// <param name="sourceStartIdx">Indice iniziale nella lista dei frame sorgente</param>
        /// <param name="langFrames">Lista dei frame lingua</param>
        /// <param name="langStartIdx">Indice iniziale nella lista dei frame lingua</param>
        /// <param name="sequenceLength">Numero di frame nella sequenza</param>
        /// <returns>Correlazione media normalizzata tra 0 e 1 oppure 0,0 se i frame sono insufficienti</returns>
        protected double ComputeSequenceEdgeBlockCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceEdgeBlockCorrelation(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Calcola la correlazione media block-motion su una sequenza
        /// </summary>
        /// <param name="sourceFrames">Lista dei frame sorgente</param>
        /// <param name="sourceStartIdx">Indice iniziale nella lista dei frame sorgente</param>
        /// <param name="langFrames">Lista dei frame lingua</param>
        /// <param name="langStartIdx">Indice iniziale nella lista dei frame lingua</param>
        /// <param name="sequenceLength">Numero di frame nella sequenza</param>
        /// <returns>Correlazione media normalizzata tra 0 e 1 oppure 0,0 se i frame sono insufficienti</returns>
        protected double ComputeSequenceBlockMotionCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceBlockMotionCorrelation(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Calcola la similarità media aHash/dHash su una sequenza
        /// </summary>
        /// <param name="sourceFrames">Lista dei frame sorgente</param>
        /// <param name="sourceStartIdx">Indice iniziale nella lista dei frame sorgente</param>
        /// <param name="langFrames">Lista dei frame lingua</param>
        /// <param name="langStartIdx">Indice iniziale nella lista dei frame lingua</param>
        /// <param name="sequenceLength">Numero di frame nella sequenza</param>
        /// <returns>Similarità media normalizzata tra 0 e 1 oppure 0,0 se i frame sono insufficienti</returns>
        protected double ComputeSequenceHashSimilarity(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceHashSimilarity(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Rileva i tagli di scena tramite MSE tra frame consecutivi
        /// </summary>
        /// <param name="frames">Lista dei frame in scala di grigi</param>
        /// <returns>Lista indici frame dove avviene il taglio</returns>
        protected List<int> DetectSceneCuts(List<byte[]> frames)
        {
            return this._sceneCutDetector.Detect(frames);
        }

        /// <summary>
        /// Rileva i tagli di scena tramite MSE tra frame consecutivi con una soglia più permissiva
        /// Usato come fallback quando un segmento scuro o granuloso non produce tagli con la soglia conservativa
        /// </summary>
        /// <param name="frames">Lista dei frame in scala di grigi</param>
        /// <returns>Lista indici frame dove avviene il taglio</returns>
        protected List<int> DetectSceneCutsRelaxed(List<byte[]> frames)
        {
            return this._sceneCutDetector.DetectRelaxed(frames);
        }

        /// <summary>
        /// Calcola il fingerprint temporale di un taglio di scena usando luma, edge e block-motion
        /// </summary>
        /// <param name="frames">Lista dei frame in scala di grigi</param>
        /// <param name="cutIndex">Indice del frame in cui avviene il taglio</param>
        /// <returns>Array dei valori inter-frame oppure null se gli indici non sono validi</returns>
        protected double[] ComputeTemporalFingerprint(List<byte[]> frames, int cutIndex)
        {
            return this._visualMetricCalculator.ComputeTemporalFingerprint(frames, cutIndex);
        }

        /// <summary>
        /// Calcola la correlazione di Pearson tra due fingerprint temporali
        /// </summary>
        /// <param name="fp1">Primo fingerprint temporale</param>
        /// <param name="fp2">Secondo fingerprint temporale</param>
        /// <returns>Coefficiente di correlazione tra -1 e 1 oppure 0,0 se non è calcolabile</returns>
        protected double ComputeFingerprintCorrelation(double[] fp1, double[] fp2)
        {
            return this._visualMetricCalculator.ComputeFingerprintCorrelation(fp1, fp2);
        }

        #endregion

    }
}
