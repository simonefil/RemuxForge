using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Media.Ffmpeg;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;

namespace RemuxForge.Core.Media
{
    /// <summary>
    /// Classe base per servizi di sincronizzazione video tramite confronto frame
    /// </summary>
    public abstract class VideoSyncServiceBase
    {
        #region Variabili di classe

        /// <summary>
        /// Riferimento alla configurazione VideoSync (binding diretto, modifiche immediate)
        /// </summary>
        protected VideoSyncConfig _vsConfig;

        /// <summary>
        /// Riferimento alla configurazione Ffmpeg (binding diretto, modifiche immediate)
        /// </summary>
        protected FfmpegConfig _ffmpegConfig;

        /// <summary>
        /// Percorso eseguibile ffmpeg
        /// </summary>
        protected string _ffmpegPath;

        /// <summary>
        /// Sezione di log per messaggi
        /// </summary>
        private readonly LogSection _logSection;

        /// <summary>
        /// Croppa i frame del file sorgente a 4:3 centrato quando richiesto dall'autocrop geometry
        /// </summary>
        protected bool _geometryCropSourceToFourThree;

        /// <summary>
        /// Croppa i frame del file lingua a 4:3 centrato quando richiesto dall'autocrop geometry
        /// </summary>
        protected bool _geometryCropLanguageToFourThree;

        /// <summary>
        /// Crop manuale source per analisi visuale nel formato L:R:T:B
        /// </summary>
        protected string _analysisCropSourcePx;

        /// <summary>
        /// Crop manuale lingua per analisi visuale nel formato L:R:T:B
        /// </summary>
        protected string _analysisCropLanguagePx;

        /// <summary>
        /// Analyzer geometry video condiviso dal servizio
        /// </summary>
        private readonly VideoGeometryAnalyzer _geometryAnalyzer;

        /// <summary>
        /// Normalizzatore bordi neri condiviso dal servizio
        /// </summary>
        private readonly BlackBorderNormalizer _blackBorderNormalizer;

        /// <summary>
        /// Rilevatore tagli scena condiviso dal servizio
        /// </summary>
        private readonly SceneCutDetector _sceneCutDetector;

        /// <summary>
        /// Calcolatore metriche visuali condiviso dal servizio
        /// </summary>
        private readonly VisualMetricCalculator _visualMetricCalculator;

        /// <summary>
        /// Ultima geometria diagnostica sorgente preparata dal servizio
        /// </summary>
        protected FrameSyncGeometryInfo _lastSourceGeometryInfo;

        /// <summary>
        /// Ultima geometria diagnostica lingua preparata dal servizio
        /// </summary>
        protected FrameSyncGeometryInfo _lastLanguageGeometryInfo;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
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
        /// Configura il crop manuale source/lang usato solo per l'analisi visuale
        /// </summary>
        /// <param name="sourceCropPx">Crop source nel formato L:R:T:B</param>
        /// <param name="languageCropPx">Crop lingua nel formato L:R:T:B</param>
        public void SetAnalysisCrop(string sourceCropPx, string languageCropPx)
        {
            this._analysisCropSourcePx = Options.NormalizeAnalysisCropPx(sourceCropPx);
            this._analysisCropLanguagePx = Options.NormalizeAnalysisCropPx(languageCropPx);
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Estrae frame di un segmento video applicando un eventuale crop manuale di analisi
        /// </summary>
        /// <param name="filePath">Percorso file video</param>
        /// <param name="startMs">Inizio estrazione in millisecondi</param>
        /// <param name="durationSec">Durata estrazione in secondi</param>
        /// <param name="targetFps">FPS target per normalizzazione (0 = passthrough senza decimazione)</param>
        /// <param name="geometryCropToFourThree">Se true, croppa il frame a 4:3 centrato prima dello scale (rimuove pillarbox)</param>
        /// <param name="manualCropPx">Crop manuale L:R:T:B in pixel; se presente sostituisce il crop geometry 4:3</param>
        /// <param name="frames">Lista frame grayscale estratti (output)</param>
        /// <param name="timestampsMs">Array di timestamp assoluti in ms, uno per frame estratto (output)</param>
        protected void ExtractSegment(string filePath, int startMs, double durationSec, double targetFps, bool geometryCropToFourThree, string manualCropPx, out List<byte[]> frames, out double[] timestampsMs)
        {
            FrameExtractionService extractor = new FrameExtractionService(this._ffmpegPath, this._vsConfig, this._ffmpegConfig, this._logSection);
            string normalizedManualCrop = Options.NormalizeAnalysisCropPx(manualCropPx);
            bool effectiveGeometryCrop = this.UseGeometryCrop(geometryCropToFourThree, normalizedManualCrop);

            extractor.ExtractSegment(filePath, startMs, durationSec, targetFps, effectiveGeometryCrop, normalizedManualCrop, out frames, out timestampsMs);
            this.NormalizeBlackBorders(filePath, effectiveGeometryCrop, normalizedManualCrop, frames);
        }

        /// <summary>
        /// Estrae frame campionati nel tempo conservando i PTS originali selezionati
        /// </summary>
        protected void ExtractSegmentAtInterval(string filePath, int startMs, double durationSec, double sampleIntervalSec, bool geometryCropToFourThree, string manualCropPx, out List<byte[]> frames, out double[] timestampsMs)
        {
            FrameExtractionService extractor = new FrameExtractionService(this._ffmpegPath, this._vsConfig, this._ffmpegConfig, this._logSection);
            string normalizedManualCrop = Options.NormalizeAnalysisCropPx(manualCropPx);
            bool effectiveGeometryCrop = this.UseGeometryCrop(geometryCropToFourThree, normalizedManualCrop);
            extractor.ExtractSegmentAtInterval(filePath, startMs, durationSec, sampleIntervalSec, effectiveGeometryCrop, normalizedManualCrop, out frames, out timestampsMs);
            this.NormalizeBlackBorders(filePath, effectiveGeometryCrop, normalizedManualCrop, frames);
        }

        /// <summary>
        /// Analizza e memorizza in cache la geometria video rilevante per il frame matching
        /// </summary>
        /// <param name="filePath">Percorso file video</param>
        /// <returns>Profilo geometria, o null se non rilevabile</returns>
        protected VideoGeometryProfile AnalyzeVideoGeometry(string filePath)
        {
            return this._geometryAnalyzer.Analyze(filePath);
        }

        /// <summary>
        /// Logga il confronto geometrico tra sorgente e lingua
        /// </summary>
        /// <param name="sourceGeometry">Geometria sorgente</param>
        /// <param name="languageGeometry">Geometria lingua</param>
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

            ConsoleHelper.Write(this._logSection, LogLevel.Debug, "  Geometry source: " + sourceGeometry.ToShortString());
            ConsoleHelper.Write(this._logSection, LogLevel.Debug, "  Geometry lang:   " + languageGeometry.ToShortString());

            if (geometryMismatch)
            {
                ConsoleHelper.Write(this._logSection, LogLevel.Notice, "  Geometry mismatch: normalizzazione SAR/DAR e auto-crop attive");
            }
        }

        /// <summary>
        /// Analizza geometria source/lang e applica il crop 4:3 automatico quando il confronto è pillarbox vs 4:3 nativo
        /// </summary>
        /// <param name="sourceFile">File sorgente</param>
        /// <param name="languageFile">File lingua</param>
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
        /// Applica crop 4:3 automatico quando la geometria indica un confronto pillarbox 16:9 contro nativo 4:3
        /// </summary>
        /// <param name="sourceGeometry">Geometria sorgente</param>
        /// <param name="languageGeometry">Geometria lingua</param>
        /// <param name="sourceManualCropPx">Crop manuale source configurato</param>
        /// <param name="languageManualCropPx">Crop manuale lingua configurato</param>
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
                ConsoleHelper.Write(this._logSection, LogLevel.Notice, "  Geometry crop source 4:3 attivo: source wide/pillarbox vs lang 4:3");
            }

            if (languageWide && sourceFourThree && languageSquare && languageHasBorders && string.IsNullOrEmpty(languageManualCropPx))
            {
                this._geometryCropLanguageToFourThree = true;
                ConsoleHelper.Write(this._logSection, LogLevel.Notice, "  Geometry crop lang 4:3 attivo: lang wide/pillarbox vs source 4:3");
            }
        }

        /// <summary>
        /// Verifica se la geometria display è circa 4:3
        /// </summary>
        /// <param name="geometry">Profilo geometria</param>
        /// <returns>True se aspect circa 4:3</returns>
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
        /// Verifica se la geometria display è circa wide 16:9
        /// </summary>
        /// <param name="geometry">Profilo geometria</param>
        /// <returns>True se aspect circa 16:9</returns>
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
        /// <param name="geometry">Profilo geometria</param>
        /// <returns>True se SAR circa 1:1</returns>
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
        /// Crea DTO diagnostico dalla geometria interna
        /// </summary>
        /// <param name="geometry">Profilo geometria interno</param>
        /// <param name="geometryCropToFourThree">True se crop 4:3 attivato dalla geometria</param>
        /// <param name="manualCropPx">Crop manuale L:R:T:B in pixel</param>
        /// <returns>DTO diagnostico</returns>
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
        /// Rileva bordi neri stabili nel segmento e normalizza i frame usando la variante di crop applicata
        /// </summary>
        /// <param name="filePath">Percorso file, usato come chiave cache profilo</param>
        /// <param name="geometryCropToFourThree">True se i frame sono stati estratti con crop geometry 4:3</param>
        /// <param name="manualCropPx">Crop manuale L:R:T:B applicato ai frame</param>
        /// <param name="frames">Frame grayscale da normalizzare in-place</param>
        protected void NormalizeBlackBorders(string filePath, bool geometryCropToFourThree, string manualCropPx, List<byte[]> frames)
        {
            this._blackBorderNormalizer.Normalize(filePath, geometryCropToFourThree, manualCropPx, frames);
        }

        /// <summary>
        /// Restituisce true se il crop geometry può restare attivo con il crop manuale corrente
        /// </summary>
        /// <param name="geometryCropToFourThree">Flag crop geometry richiesto</param>
        /// <param name="manualCropPx">Crop manuale normalizzato o raw</param>
        /// <returns>True se usare il crop geometry</returns>
        protected bool UseGeometryCrop(bool geometryCropToFourThree, string manualCropPx)
        {
            return geometryCropToFourThree && string.IsNullOrEmpty(Options.NormalizeAnalysisCropPx(manualCropPx));
        }

        /// <summary>
        /// Logga il crop manuale di analisi quando configurato
        /// </summary>
        /// <param name="role">Ruolo del file</param>
        /// <param name="manualCropPx">Crop manuale normalizzato</param>
        private void LogManualAnalysisCrop(string role, string manualCropPx)
        {
            if (!string.IsNullOrEmpty(manualCropPx))
            {
                ConsoleHelper.Write(this._logSection, LogLevel.Notice, "  Analysis crop " + role + " attivo: " + manualCropPx + " px");
            }
        }

        /// <summary>
        /// Calcola SSIM medio di una sequenza di frame consecutivi (confronto cross-file)
        /// </summary>
        /// <param name="sourceFrames">Lista frame sorgente</param>
        /// <param name="sourceStartIdx">Indice iniziale nei frame sorgente</param>
        /// <param name="langFrames">Lista frame lingua</param>
        /// <param name="langStartIdx">Indice iniziale nei frame lingua</param>
        /// <param name="sequenceLength">Numero di frame nella sequenza</param>
        /// <returns>SSIM medio della sequenza o 0.0 se frame insufficienti</returns>
        protected double ComputeSequenceSsim(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceSsim(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Calcola correlazione blur media su una sequenza
        /// </summary>
        protected double ComputeSequenceBlurredCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceBlurredCorrelation(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Calcola correlazione edge media di una sequenza di frame consecutivi
        /// </summary>
        protected double ComputeSequenceEdgeCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceEdgeCorrelation(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Calcola correlazione media dei fingerprint a blocchi su una sequenza di frame
        /// </summary>
        protected double ComputeSequenceBlockCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceBlockCorrelation(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Calcola correlazione media edge-block su una sequenza
        /// </summary>
        protected double ComputeSequenceEdgeBlockCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceEdgeBlockCorrelation(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Calcola correlazione media block-motion su una sequenza
        /// </summary>
        protected double ComputeSequenceBlockMotionCorrelation(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceBlockMotionCorrelation(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Calcola similarità media aHash/dHash su una sequenza
        /// </summary>
        protected double ComputeSequenceHashSimilarity(List<byte[]> sourceFrames, int sourceStartIdx, List<byte[]> langFrames, int langStartIdx, int sequenceLength)
        {
            return this._visualMetricCalculator.ComputeSequenceHashSimilarity(sourceFrames, sourceStartIdx, langFrames, langStartIdx, sequenceLength);
        }

        /// <summary>
        /// Rileva tagli di scena tramite MSE tra frame consecutivi
        /// </summary>
        /// <param name="frames">Lista frame grayscale</param>
        /// <returns>Lista indici frame dove avviene il taglio</returns>
        protected List<int> DetectSceneCuts(List<byte[]> frames)
        {
            return this._sceneCutDetector.Detect(frames);
        }

        /// <summary>
        /// Rileva tagli di scena tramite MSE tra frame consecutivi con soglia più permissiva
        /// Usato come fallback quando un segmento scuro/grainy non produce cut con la soglia conservativa
        /// </summary>
        /// <param name="frames">Lista frame grayscale</param>
        /// <returns>Lista indici frame dove avviene il taglio</returns>
        protected List<int> DetectSceneCutsRelaxed(List<byte[]> frames)
        {
            return this._sceneCutDetector.DetectRelaxed(frames);
        }

        /// <summary>
        /// Calcola il fingerprint temporale di un taglio di scena usando luma, edge e block-motion
        /// </summary>
        protected double[] ComputeTemporalFingerprint(List<byte[]> frames, int cutIndex)
        {
            return this._visualMetricCalculator.ComputeTemporalFingerprint(frames, cutIndex);
        }

        /// <summary>
        /// Calcola la correlazione di Pearson tra due fingerprint temporali
        /// </summary>
        protected double ComputeFingerprintCorrelation(double[] fp1, double[] fp2)
        {
            return this._visualMetricCalculator.ComputeFingerprintCorrelation(fp1, fp2);
        }

        #endregion

    }
}
