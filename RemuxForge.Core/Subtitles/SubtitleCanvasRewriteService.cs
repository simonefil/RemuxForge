using RemuxForge.Core.Configuration;
using RemuxForge.Core.Infrastructure;
using RemuxForge.Core.Models;
using RemuxForge.Core.Tools;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RemuxForge.Core.Subtitles
{
    /// <summary>
    /// Applica riscritture canvas/coordinate ai sottotitoli bitmap importati
    /// </summary>
    public class SubtitleCanvasRewriteService
    {
        #region Variabili di classe

        /// <summary>
        /// Resolver centralizzato strumenti esterni
        /// </summary>
        private readonly ToolPathResolverService _toolPathResolver;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="toolPathResolver">Resolver tool esterni</param>
        public SubtitleCanvasRewriteService(ToolPathResolverService toolPathResolver)
        {
            this._toolPathResolver = toolPathResolver ?? new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder);
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Processa i sottotitoli importati che supportano rewrite canvas
        /// </summary>
        /// <param name="record">Record elaborazione corrente</param>
        /// <param name="subtitleTracks">Tracce sottotitoli importate dal file lingua</param>
        /// <param name="processedLangSubTracks">Mappa tracce sottotitolo gia' sostituite da file temporanei</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="ffmpegPath">Path ffmpeg</param>
        /// <param name="tempFolder">Cartella temporanea</param>
        /// <returns>True: il processing e' completato o non applicabile</returns>
        public bool ProcessImportedSubtitles(
            FileProcessingRecord record,
            List<TrackInfo> subtitleTracks,
            Dictionary<int, string> processedLangSubTracks,
            Options options,
            string ffmpegPath,
            string tempFolder)
        {
            PgsCanvasRewritePlan plan;

            if (record == null || options == null || !options.SubtitleCanvasRewrite || subtitleTracks == null || subtitleTracks.Count == 0)
            {
                return true;
            }

            if (options.DryRun)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Text, "  [DRY-RUN] Subtitle canvas rewrite attivo");
                return true;
            }

            if (!this.TryBuildPgsPlan(record, options, out plan))
            {
                return true;
            }

            for (int i = 0; i < subtitleTracks.Count; i++)
            {
                if (this.IsPgsCodec(subtitleTracks[i].Codec))
                {
                    this.TryProcessPgsTrack(record, subtitleTracks[i], processedLangSubTracks, options, ffmpegPath, tempFolder, plan);
                }
            }

            return true;
        }

        #endregion

        #region Metodi privati - Piano geometria

        /// <summary>
        /// Costruisce il piano PGS dal crop effettivamente applicato in analisi
        /// </summary>
        private bool TryBuildPgsPlan(FileProcessingRecord record, Options options, out PgsCanvasRewritePlan plan)
        {
            FrameSyncGeometryInfo sourceGeometry;
            FrameSyncGeometryInfo languageGeometry;
            SubtitleCanvasCrop sourceCrop;
            SubtitleCanvasCrop languageCrop;
            plan = null;

            sourceGeometry = this.ResolveAnalysisGeometryInfo(record, true);
            languageGeometry = this.ResolveAnalysisGeometryInfo(record, false);
            if (sourceGeometry == null || languageGeometry == null || sourceGeometry.Width <= 0 || sourceGeometry.Height <= 0 || languageGeometry.Width <= 0 || languageGeometry.Height <= 0)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning, "  Subtitle canvas rewrite ignorato: geometria analisi non disponibile");
                return false;
            }

            sourceCrop = this.ResolveEffectiveCrop(sourceGeometry, options.AnalysisCropSourcePx);
            languageCrop = this.ResolveEffectiveCrop(languageGeometry, options.AnalysisCropLanguagePx);
            plan = new PgsCanvasRewritePlan();
            plan.InputCanvasWidth = languageGeometry.Width;
            plan.InputCanvasHeight = languageGeometry.Height;
            plan.OutputCanvasWidth = sourceGeometry.Width;
            plan.OutputCanvasHeight = sourceGeometry.Height;
            plan.InputCropLeft = languageCrop.Left;
            plan.InputCropRight = languageCrop.Right;
            plan.InputCropTop = languageCrop.Top;
            plan.InputCropBottom = languageCrop.Bottom;
            plan.OutputCropLeft = sourceCrop.Left;
            plan.OutputCropRight = sourceCrop.Right;
            plan.OutputCropTop = sourceCrop.Top;
            plan.OutputCropBottom = sourceCrop.Bottom;
            plan.SourceCropMode = sourceCrop.Mode;
            plan.LanguageCropMode = languageCrop.Mode;

            if (!this.ValidatePlan(plan))
            {
                plan = null;
                return false;
            }

            if (plan.InputCanvasWidth == plan.OutputCanvasWidth &&
                plan.InputCanvasHeight == plan.OutputCanvasHeight &&
                plan.OffsetX == 0 &&
                plan.OffsetY == 0)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Text, "  Subtitle canvas rewrite ignorato: canvas gia' allineato");
                plan = null;
                return false;
            }

            ConsoleHelper.Write(LogSection.Merge, LogLevel.Notice,
                "  Subtitle canvas rewrite: lang " + plan.InputCanvasWidth + "x" + plan.InputCanvasHeight +
                " crop " + this.FormatCrop(plan.InputCropLeft, plan.InputCropRight, plan.InputCropTop, plan.InputCropBottom, plan.LanguageCropMode) +
                " -> source " + plan.OutputCanvasWidth + "x" + plan.OutputCanvasHeight +
                " crop " + this.FormatCrop(plan.OutputCropLeft, plan.OutputCropRight, plan.OutputCropTop, plan.OutputCropBottom, plan.SourceCropMode) +
                ", offset " + plan.OffsetX.ToString(CultureInfo.InvariantCulture) + ":" + plan.OffsetY.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>
        /// Legge geometria gia' prodotta da DeepAnalysis o FrameSync
        /// </summary>
        private FrameSyncGeometryInfo ResolveAnalysisGeometryInfo(FileProcessingRecord record, bool source)
        {
            FrameSyncGeometryInfo result = null;

            if (record.DeepAnalysisMap != null && record.DeepAnalysisMap.Diagnostics != null)
            {
                result = source ? record.DeepAnalysisMap.Diagnostics.SourceGeometry : record.DeepAnalysisMap.Diagnostics.LanguageGeometry;
            }

            if (result == null && record.FrameSyncResult != null)
            {
                result = source ? record.FrameSyncResult.SourceGeometry : record.FrameSyncResult.LanguageGeometry;
            }

            return result;
        }

        /// <summary>
        /// Determina il crop effettivo usabile per coordinate PGS
        /// </summary>
        private SubtitleCanvasCrop ResolveEffectiveCrop(FrameSyncGeometryInfo geometry, string optionCropPx)
        {
            SubtitleCanvasCrop result = new SubtitleCanvasCrop();
            string manualCrop = Options.NormalizeAnalysisCropPx(geometry.ManualAnalysisCropPx);
            int left;
            int right;
            int top;
            int bottom;
            int activeWidth;

            if (manualCrop.Length == 0)
            {
                manualCrop = Options.NormalizeAnalysisCropPx(optionCropPx);
            }

            if (manualCrop.Length > 0 && Options.TryParseAnalysisCropPx(manualCrop, out left, out right, out top, out bottom))
            {
                result.Left = left;
                result.Right = right;
                result.Top = top;
                result.Bottom = bottom;
                result.Mode = "manual";
            }
            else if (geometry.GeometryCropToFourThree)
            {
                activeWidth = (int)Math.Round(geometry.Height * 4.0 / 3.0);
                result.Left = Math.Max(0, (geometry.Width - activeWidth) / 2);
                result.Right = Math.Max(0, geometry.Width - activeWidth - result.Left);
                result.Top = 0;
                result.Bottom = 0;
                result.Mode = "geometry_4_3";
            }
            else
            {
                result.Mode = "none";
            }

            return result;
        }

        /// <summary>
        /// Valida che il piano non richieda scaling bitmap
        /// </summary>
        private bool ValidatePlan(PgsCanvasRewritePlan plan)
        {
            if (plan.InputActiveWidth <= 0 || plan.InputActiveHeight <= 0 || plan.OutputActiveWidth <= 0 || plan.OutputActiveHeight <= 0)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning, "  Subtitle canvas rewrite ignorato: crop non valido");
                return false;
            }

            if (plan.InputActiveWidth != plan.OutputActiveWidth || plan.InputActiveHeight != plan.OutputActiveHeight)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning,
                    "  Subtitle canvas rewrite ignorato: aree attive diverse (" +
                    plan.InputActiveWidth + "x" + plan.InputActiveHeight + " -> " +
                    plan.OutputActiveWidth + "x" + plan.OutputActiveHeight + "), servirebbe scaling bitmap");
                return false;
            }

            return true;
        }

        #endregion

        #region Metodi privati - PGS

        /// <summary>
        /// Processa una traccia PGS importata
        /// </summary>
        private void TryProcessPgsTrack(FileProcessingRecord record, TrackInfo track, Dictionary<int, string> processedLangSubTracks, Options options, string ffmpegPath, string tempFolder, PgsCanvasRewritePlan plan)
        {
            string inputFile;
            string outputFile;
            string previousFile = "";
            bool extractedInput = false;
            PgsSubtitleCanvasRewriter rewriter;
            PgsCanvasRewriteReport report;

            if (processedLangSubTracks.ContainsKey(track.Id))
            {
                inputFile = processedLangSubTracks[track.Id];
                previousFile = inputFile;
            }
            else
            {
                inputFile = Path.Combine(tempFolder, "subcanvas_t" + track.Id.ToString(CultureInfo.InvariantCulture) + "_src_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sup");
                extractedInput = true;
                if (!this.ExtractPgsTrack(record.LangFilePath, track.Id, inputFile, options))
                {
                    FileHelper.DeleteTempFile(inputFile);
                    ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning, "  Subtitle canvas rewrite PGS t" + track.Id + " ignorato: estrazione fallita");
                    return;
                }
            }

            outputFile = Path.Combine(tempFolder, "subcanvas_t" + track.Id.ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sup");
            rewriter = new PgsSubtitleCanvasRewriter();
            if (rewriter.Rewrite(inputFile, outputFile, plan, out report) &&
                File.Exists(outputFile) &&
                this.ValidateSubtitleFile(outputFile, ffmpegPath))
            {
                processedLangSubTracks[track.Id] = outputFile;
                if (previousFile.Length > 0)
                {
                    FileHelper.DeleteTempFile(previousFile);
                }

                ConsoleHelper.Write(LogSection.Merge, LogLevel.Success,
                    "  Subtitle canvas rewrite PGS t" + track.Id + ": PCS=" + report.PcsSegments +
                    ", WDS=" + report.WdsSegments +
                    ", oggetti=" + report.ObjectCoordinatesRewritten +
                    this.FormatClampReport(report));
            }
            else
            {
                FileHelper.DeleteTempFile(outputFile);
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning,
                    "  Subtitle canvas rewrite PGS t" + track.Id + " ignorato: " +
                    (report != null && report.ErrorMessage.Length > 0 ? report.ErrorMessage : "validazione fallita"));
            }

            if (extractedInput)
            {
                FileHelper.DeleteTempFile(inputFile);
            }
        }

        /// <summary>
        /// Estrae una traccia PGS con mkvextract
        /// </summary>
        private bool ExtractPgsTrack(string langFile, int trackId, string outputFile, Options options)
        {
            string mkvExtractPath = this.ResolveMkvExtractPath(options);
            ProcessResult result;

            if (mkvExtractPath.Length == 0)
            {
                return false;
            }

            result = ProcessRunner.Run(mkvExtractPath, new string[]
            {
                "tracks",
                langFile,
                trackId.ToString(CultureInfo.InvariantCulture) + ":" + outputFile
            }, AppSettingsService.Instance.Settings.Advanced.SubtitleEdit.FfmpegTimeoutMs);

            return result != null && result.ExitCode == 0 && File.Exists(outputFile);
        }

        /// <summary>
        /// Valida che il SUP prodotto sia leggibile
        /// </summary>
        private bool ValidateSubtitleFile(string filePath, string ffmpegPath)
        {
            ProcessResult result = ProcessRunner.Run(ffmpegPath, new string[]
            {
                "-nostdin",
                "-v", "error",
                "-i", filePath,
                "-map", "0:0",
                "-c", "copy",
                "-f", "null",
                "-"
            }, AppSettingsService.Instance.Settings.Advanced.SubtitleEdit.FfmpegTimeoutMs);

            return result != null && result.ExitCode == 0;
        }

        /// <summary>
        /// Risolve mkvextract dalla configurazione corrente
        /// </summary>
        private string ResolveMkvExtractPath(Options options)
        {
            string mkvMergePath = options.MkvMergePath;
            if (mkvMergePath.Length == 0)
            {
                mkvMergePath = this._toolPathResolver.ResolveMkvMergePath(false);
            }

            return this._toolPathResolver.ResolveMkvExtractPath(mkvMergePath, false);
        }

        /// <summary>
        /// Determina se il codec rappresenta una traccia PGS
        /// </summary>
        private bool IsPgsCodec(string codec)
        {
            string c = codec != null ? codec.ToLowerInvariant() : "";
            return c.Contains("pgs") || c.Contains("s_hdmv/pgs");
        }

        #endregion

        #region Metodi privati - Utility

        /// <summary>
        /// Formatta crop per log compatto
        /// </summary>
        private string FormatCrop(int left, int right, int top, int bottom, string mode)
        {
            return left.ToString(CultureInfo.InvariantCulture) + ":" +
                right.ToString(CultureInfo.InvariantCulture) + ":" +
                top.ToString(CultureInfo.InvariantCulture) + ":" +
                bottom.ToString(CultureInfo.InvariantCulture) +
                " (" + mode + ")";
        }

        /// <summary>
        /// Formatta il report clamp per log compatto
        /// </summary>
        private string FormatClampReport(PgsCanvasRewriteReport report)
        {
            string result = "";
            if (report != null && report.DisplaySetsClamped > 0)
            {
                result = ", clamp=" + report.DisplaySetsClamped +
                    " (L" + report.MaxClampLeftPx.ToString(CultureInfo.InvariantCulture) +
                    " R" + report.MaxClampRightPx.ToString(CultureInfo.InvariantCulture) +
                    " U" + report.MaxClampUpPx.ToString(CultureInfo.InvariantCulture) +
                    " D" + report.MaxClampDownPx.ToString(CultureInfo.InvariantCulture) + ")";
            }

            return result;
        }

        #endregion

        #region Classi annidate

        /// <summary>
        /// Crop canvas espresso in coordinate video originali
        /// </summary>
        private class SubtitleCanvasCrop
        {
            /// <summary>
            /// Costruttore
            /// </summary>
            public SubtitleCanvasCrop()
            {
                this.Mode = "";
            }

            /// <summary>
            /// Crop sinistro
            /// </summary>
            public int Left { get; set; }

            /// <summary>
            /// Crop destro
            /// </summary>
            public int Right { get; set; }

            /// <summary>
            /// Crop superiore
            /// </summary>
            public int Top { get; set; }

            /// <summary>
            /// Crop inferiore
            /// </summary>
            public int Bottom { get; set; }

            /// <summary>
            /// Modalita' crop
            /// </summary>
            public string Mode { get; set; }
        }

        #endregion
    }
}
