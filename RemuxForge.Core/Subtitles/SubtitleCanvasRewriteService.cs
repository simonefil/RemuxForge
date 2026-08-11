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
    /// Applica riscritture canvas/coordinate ai sottotitoli importati
    /// </summary>
    public class SubtitleCanvasRewriteService
    {
        #region Variabili di classe

        /// <summary>
        /// Resolver centralizzato strumenti esterni
        /// </summary>
        private readonly ToolPathResolverService _toolPathResolver;

        /// <summary>
        /// Rewriter formato-specifici disponibili
        /// </summary>
        private readonly List<ISubtitleCanvasRewriter> _rewriters;

        #endregion

        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        /// <param name="toolPathResolver">Resolver tool esterni</param>
        public SubtitleCanvasRewriteService(ToolPathResolverService toolPathResolver)
        {
            this._toolPathResolver = toolPathResolver ?? new ToolPathResolverService(AppSettingsService.Instance.ConfigFolder);
            this._rewriters = new List<ISubtitleCanvasRewriter>
            {
                new PgsSubtitleCanvasRewriter(),
                new AssSubtitleCanvasRewriter(),
                new VobSubSubtitleCanvasRewriter()
            };
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Processa i sottotitoli importati che supportano rewrite canvas
        /// </summary>
        /// <param name="record">Record elaborazione corrente</param>
        /// <param name="subtitleTracks">Tracce sottotitoli importate dal file lingua</param>
        /// <param name="processedLangSubTracks">Mappa tracce sottotitolo già sostituite da file temporanei</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="ffmpegPath">Path ffmpeg</param>
        /// <param name="tempFolder">Cartella temporanea</param>
        public void ProcessImportedSubtitles(
            FileProcessingRecord record,
            List<TrackInfo> subtitleTracks,
            Dictionary<int, string> processedLangSubTracks,
            Options options,
            string ffmpegPath,
            string tempFolder)
        {
            SubtitleCanvasRewriteContext context;
            ISubtitleCanvasRewriter rewriter;

            if (record == null || options == null || !options.SubtitleCanvasRewrite || subtitleTracks == null || subtitleTracks.Count == 0 || processedLangSubTracks == null)
            {
                return;
            }

            if (options.DryRun)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Text, "  [DRY-RUN] Subtitle canvas rewrite attivo");
                return;
            }

            if (!this.TryBuildContext(record, options, ffmpegPath, tempFolder, out context))
            {
                return;
            }

            // Ogni traccia viene processata solo se un rewriter formato-specifico la supporta
            for (int i = 0; i < subtitleTracks.Count; i++)
            {
                rewriter = this.ResolveRewriter(subtitleTracks[i]);
                if (rewriter != null)
                {
                    this.TryProcessTrack(context, subtitleTracks[i], processedLangSubTracks, rewriter);
                }
            }

        }

        #endregion

        #region Metodi privati - Piano geometria

        /// <summary>
        /// Costruisce il contesto comune dal crop effettivamente applicato in analisi
        /// </summary>
        /// <param name="record">Record pipeline corrente</param>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="ffmpegPath">Path ffmpeg risolto</param>
        /// <param name="tempFolder">Cartella temporanea pipeline</param>
        /// <param name="context">Contesto canvas costruito</param>
        /// <returns>True se il contesto è valido e richiede rewrite</returns>
        private bool TryBuildContext(FileProcessingRecord record, Options options, string ffmpegPath, string tempFolder, out SubtitleCanvasRewriteContext context)
        {
            FrameSyncGeometryInfo sourceGeometry;
            FrameSyncGeometryInfo languageGeometry;
            SubtitleCanvasCrop sourceCrop;
            SubtitleCanvasCrop languageCrop;
            SubtitleCanvasTransform transform;

            context = null;

            // La geometria deve essere quella usata dall'analisi: source e lang possono avere storage/display/crop diversi
            sourceGeometry = this.ResolveAnalysisGeometryInfo(record, true);
            languageGeometry = this.ResolveAnalysisGeometryInfo(record, false);
            if (sourceGeometry == null || languageGeometry == null || sourceGeometry.Width <= 0 || sourceGeometry.Height <= 0 || languageGeometry.Width <= 0 || languageGeometry.Height <= 0)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning, "  Subtitle canvas rewrite ignorato: geometria analisi non disponibile");
                return false;
            }

            // I crop effettivi possono arrivare da opzioni manuali, crop verticale manuale o rilevamento pillarbox automatico
            sourceCrop = this.ResolveEffectiveCrop(sourceGeometry, options.AnalysisCropSourcePx);
            languageCrop = this.ResolveEffectiveCrop(languageGeometry, options.AnalysisCropLanguagePx);

            // La trasformazione base resta nello spazio video; i rewriter testuali possono crearne una nello spazio script
            transform = new SubtitleCanvasTransform();
            transform.InputCanvasWidth = languageGeometry.Width;
            transform.InputCanvasHeight = languageGeometry.Height;
            transform.OutputCanvasWidth = sourceGeometry.Width;
            transform.OutputCanvasHeight = sourceGeometry.Height;
            transform.InputDisplayWidth = languageGeometry.DisplayWidth > 0 ? languageGeometry.DisplayWidth : languageGeometry.Width;
            transform.InputDisplayHeight = languageGeometry.DisplayHeight > 0 ? languageGeometry.DisplayHeight : languageGeometry.Height;
            transform.OutputDisplayWidth = sourceGeometry.DisplayWidth > 0 ? sourceGeometry.DisplayWidth : sourceGeometry.Width;
            transform.OutputDisplayHeight = sourceGeometry.DisplayHeight > 0 ? sourceGeometry.DisplayHeight : sourceGeometry.Height;
            transform.InputCropLeft = languageCrop.Left;
            transform.InputCropRight = languageCrop.Right;
            transform.InputCropTop = languageCrop.Top;
            transform.InputCropBottom = languageCrop.Bottom;
            transform.OutputCropLeft = sourceCrop.Left;
            transform.OutputCropRight = sourceCrop.Right;
            transform.OutputCropTop = sourceCrop.Top;
            transform.OutputCropBottom = sourceCrop.Bottom;

            if (!this.ValidateTransform(transform))
            {
                return false;
            }

            // Se canvas, crop e active area coincidono, non c'è nulla da riscrivere per nessun formato sottotitolo
            if (transform.InputCanvasWidth == transform.OutputCanvasWidth &&
                transform.InputCanvasHeight == transform.OutputCanvasHeight &&
                transform.OffsetX == 0 &&
                transform.OffsetY == 0 &&
                !transform.RequiresScaling)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Text, "  Subtitle canvas rewrite ignorato: canvas già allineato");
                return false;
            }

            // Il context viene passato ai rewriter formato-specifici insieme ai path tool/temp risolti dalla pipeline
            context = new SubtitleCanvasRewriteContext();
            context.Record = record;
            context.Options = options;
            context.Transform = transform;
            context.SourceCropMode = sourceCrop.Mode;
            context.LanguageCropMode = languageCrop.Mode;
            context.FfmpegPath = ffmpegPath != null ? ffmpegPath : "";
            context.TempFolder = tempFolder != null ? tempFolder : "";

            ConsoleHelper.Write(LogSection.Merge, LogLevel.Notice,
                "  Subtitle canvas rewrite: lang " + transform.InputCanvasWidth + "x" + transform.InputCanvasHeight +
                " crop " + this.FormatCrop(transform.InputCropLeft, transform.InputCropRight, transform.InputCropTop, transform.InputCropBottom, context.LanguageCropMode) +
                " -> source " + transform.OutputCanvasWidth + "x" + transform.OutputCanvasHeight +
                " crop " + this.FormatCrop(transform.OutputCropLeft, transform.OutputCropRight, transform.OutputCropTop, transform.OutputCropBottom, context.SourceCropMode) +
                ", active " + transform.InputActiveWidth + "x" + transform.InputActiveHeight + " -> " + transform.OutputActiveWidth + "x" + transform.OutputActiveHeight +
                ", scale " + transform.ScaleX.ToString("0.######", CultureInfo.InvariantCulture) + ":" + transform.ScaleY.ToString("0.######", CultureInfo.InvariantCulture) +
                ", mode " + (transform.RequiresBitmapScaling ? "scale" : "offset-only") +
                ", offset " + transform.OffsetX.ToString(CultureInfo.InvariantCulture) + ":" + transform.OffsetY.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        /// <summary>
        /// Legge geometria già prodotta da DeepAnalysis o FrameSync
        /// </summary>
        /// <param name="record">Record pipeline corrente</param>
        /// <param name="source">True per geometria source, false per language</param>
        /// <returns>Geometria analisi disponibile, null se assente</returns>
        private FrameSyncGeometryInfo ResolveAnalysisGeometryInfo(FileProcessingRecord record, bool source)
        {
            FrameSyncGeometryInfo result = null;

            if (record.DeepAnalysisResult != null)
            {
                result = source ? record.DeepAnalysisResult.SourceGeometry : record.DeepAnalysisResult.LanguageGeometry;
            }

            if (result == null && record.FrameSyncResult != null)
            {
                result = source ? record.FrameSyncResult.SourceGeometry : record.FrameSyncResult.LanguageGeometry;
            }

            return result;
        }

        /// <summary>
        /// Determina il crop effettivo usabile per coordinate sottotitoli
        /// </summary>
        /// <param name="geometry">Geometria analisi</param>
        /// <param name="optionCropPx">Crop manuale globale da opzioni</param>
        /// <returns>Crop effettivo da usare nella trasformazione</returns>
        private SubtitleCanvasCrop ResolveEffectiveCrop(FrameSyncGeometryInfo geometry, string optionCropPx)
        {
            SubtitleCanvasCrop result = new SubtitleCanvasCrop();
            string manualCrop = Options.NormalizeAnalysisCropPx(geometry.ManualAnalysisCropPx);
            int left;
            int right;
            int top;
            int bottom;
            int activeWidth;

            // Priorità al crop salvato dalla geometria di analisi, poi al crop manuale globale
            if (string.IsNullOrEmpty(manualCrop))
            {
                manualCrop = Options.NormalizeAnalysisCropPx(optionCropPx);
            }

            // Il crop manuale esplicito è l'unico caso in cui top/bottom possono essere diversi da zero
            if (!string.IsNullOrEmpty(manualCrop) && Options.TryParseAnalysisCropPx(manualCrop, out left, out right, out top, out bottom))
            {
                result.Left = left;
                result.Right = right;
                result.Top = top;
                result.Bottom = bottom;
                result.Mode = "manual";
            }
            else if (geometry.GeometryCropToFourThree)
            {
                // Rilevamento pillarbox: calcola l'area 4:3 centrata usando l'altezza del video
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
        /// Valida trasformazione geometrica comune
        /// </summary>
        /// <param name="transform">Trasformazione da validare</param>
        /// <returns>True se active area input/output sono valide</returns>
        private bool ValidateTransform(SubtitleCanvasTransform transform)
        {
            if (transform.InputActiveWidth <= 0 || transform.InputActiveHeight <= 0 || transform.OutputActiveWidth <= 0 || transform.OutputActiveHeight <= 0)
            {
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning, "  Subtitle canvas rewrite ignorato: crop non valido");
                return false;
            }

            return true;
        }

        #endregion

        #region Metodi privati - Processing tracce

        /// <summary>
        /// Processa una traccia sottotitoli supportata
        /// </summary>
        /// <param name="context">Contesto canvas comune</param>
        /// <param name="track">Traccia sottotitoli da processare</param>
        /// <param name="processedLangSubTracks">Mappa tracce già estratte, tagliate o riscritte</param>
        /// <param name="rewriter">Rewriter formato-specifico</param>
        private void TryProcessTrack(SubtitleCanvasRewriteContext context, TrackInfo track, Dictionary<int, string> processedLangSubTracks, ISubtitleCanvasRewriter rewriter)
        {
            string inputFile;
            string outputFile;
            string outputExtension;
            string previousFile = "";
            bool extractedInput = false;
            SubtitleCanvasRewriteResult result;

            // Se un passo precedente ha prodotto un file temporaneo, il canvas rewrite deve partire da quello
            if (processedLangSubTracks.ContainsKey(track.Id))
            {
                inputFile = processedLangSubTracks[track.Id];
                previousFile = inputFile;
            }
            else
            {
                // Altrimenti estrae solo la traccia richiesta dal lang, lasciando invariato il container originale
                inputFile = Path.Combine(context.TempFolder, "subcanvas_t" + track.Id.ToString(CultureInfo.InvariantCulture) + "_src_" + Guid.NewGuid().ToString("N").Substring(0, 8) + rewriter.GetPrimaryExtension(track));
                extractedInput = true;
                if (!this.ExtractSubtitleTrack(context.Record.LangFilePath, track.Id, inputFile, context.Options))
                {
                    this.DeleteSubtitleFiles(inputFile);
                    ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning, "  Subtitle canvas rewrite t" + track.Id + " ignorato: estrazione fallita");
                    return;
                }
            }

            // Se il timeline edit ha già normalizzato il formato testuale, preserva l'estensione del file temporaneo
            outputExtension = !string.IsNullOrEmpty(previousFile) && !string.IsNullOrEmpty(Path.GetExtension(inputFile)) ? Path.GetExtension(inputFile) : rewriter.GetPrimaryExtension(track);
            outputFile = Path.Combine(context.TempFolder, "subcanvas_t" + track.Id.ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + outputExtension);

            // Il rewriter produce file standalone muxabili; il dizionario viene aggiornato solo dopo validazione
            if (rewriter.Rewrite(context, track, inputFile, outputFile, out result) &&
                File.Exists(outputFile) &&
                rewriter.ValidateOutput(context, outputFile))
            {
                processedLangSubTracks[track.Id] = outputFile;
                if (!string.IsNullOrEmpty(previousFile))
                {
                    this.DeleteSubtitleFiles(previousFile);
                }

                ConsoleHelper.Write(LogSection.Merge, LogLevel.Success,
                    "  Subtitle canvas rewrite " + result.Format + " t" + track.Id + ": " + result.Summary);
            }
            else
            {
                // Non-strict: un formato non riscrivibile non blocca il remux e non sostituisce la traccia originale
                this.DeleteSubtitleFiles(outputFile);
                ConsoleHelper.Write(LogSection.Merge, LogLevel.Warning,
                    "  Subtitle canvas rewrite t" + track.Id + " ignorato: " +
                    (result != null && !string.IsNullOrEmpty(result.ErrorMessage) ? result.ErrorMessage : "validazione fallita"));
            }

            if (extractedInput)
            {
                this.DeleteSubtitleFiles(inputFile);
            }
        }

        /// <summary>
        /// Risolve il rewriter compatibile con la traccia
        /// </summary>
        /// <param name="track">Traccia sottotitoli</param>
        /// <returns>Rewriter compatibile o null</returns>
        private ISubtitleCanvasRewriter ResolveRewriter(TrackInfo track)
        {
            for (int i = 0; i < this._rewriters.Count; i++)
            {
                if (this._rewriters[i].CanHandle(track))
                {
                    return this._rewriters[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Estrae una traccia sottotitoli con mkvextract
        /// </summary>
        /// <param name="langFile">File language da cui estrarre</param>
        /// <param name="trackId">Track id da estrarre</param>
        /// <param name="outputFile">File output estratto</param>
        /// <param name="options">Opzioni correnti</param>
        /// <returns>True se mkvextract ha prodotto il file</returns>
        private bool ExtractSubtitleTrack(string langFile, int trackId, string outputFile, Options options)
        {
            string mkvExtractPath = this.ResolveMkvExtractPath(options);
            ProcessResult result;

            if (string.IsNullOrEmpty(mkvExtractPath))
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
        /// Risolve mkvextract dalla configurazione corrente
        /// </summary>
        /// <param name="options">Opzioni correnti</param>
        /// <returns>Path mkvextract risolto</returns>
        private string ResolveMkvExtractPath(Options options)
        {
            string mkvMergePath = options.MkvMergePath;
            if (string.IsNullOrEmpty(mkvMergePath))
            {
                mkvMergePath = this._toolPathResolver.ResolveMkvMergePath(false);
            }

            return this._toolPathResolver.ResolveMkvExtractPath(mkvMergePath, false);
        }

        /// <summary>
        /// Cancella file sottotitolo principale e sidecar noti
        /// </summary>
        /// <param name="filePath">File sottotitolo principale</param>
        private void DeleteSubtitleFiles(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return;
            }

            FileHelper.DeleteTempFile(filePath);
            if (string.Equals(Path.GetExtension(filePath), ".idx", StringComparison.OrdinalIgnoreCase))
            {
                FileHelper.DeleteTempFile(Path.ChangeExtension(filePath, ".sub"));
            }
        }

        #endregion

        #region Metodi privati - Utility

        /// <summary>
        /// Formatta crop per log compatto
        /// </summary>
        /// <param name="left">Crop sinistro</param>
        /// <param name="right">Crop destro</param>
        /// <param name="top">Crop superiore</param>
        /// <param name="bottom">Crop inferiore</param>
        /// <param name="mode">Modalità origine crop</param>
        /// <returns>Crop formattato</returns>
        private string FormatCrop(int left, int right, int top, int bottom, string mode)
        {
            return left.ToString(CultureInfo.InvariantCulture) + ":" +
                right.ToString(CultureInfo.InvariantCulture) + ":" +
                top.ToString(CultureInfo.InvariantCulture) + ":" +
                bottom.ToString(CultureInfo.InvariantCulture) +
                " (" + mode + ")";
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
            /// Modalità crop
            /// </summary>
            public string Mode { get; set; }
        }

        #endregion
    }
}
