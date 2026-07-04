using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using System.Collections.Generic;
using System.Globalization;

namespace RemuxForge.Core.Models
{
    /// <summary>
    /// Opzioni da riga di comando: parsing e validazione dei parametri CLI
    /// </summary>
    public class Options
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public Options()
        {
            this.Mode = "";
            this.Language = "";
            this.Help = false;
            this.SourceFolder = "";
            this.LanguageFolder = "";
            this.TargetLanguage = new List<string>();
            this.MatchPattern = @"S(\d+)E(\d+)";
            this.Overwrite = false;
            this.DestinationFolder = "";
            this.AudioDelay = 0;
            this.SubtitleDelay = 0;
            this.AudioSourceFillThresholdMs = 0;
            this.AudioSourceFillLanguage = "";
            this.AudioSourceFillStart = false;
            this.AudioSourceFillEnd = false;
            this.AudioSourceFillInsertSilence = false;
            this.SpeedCorrectionMode = SPEED_CORRECTION_OFF;
            this.ManualStretchFactor = "";
            this.FrameSync = false;
            this.FrameSyncDiagnostics = false;
            this.DeepAnalysis = false;
            this.DeepAnalysisDiagnostics = false;
            this.AnalysisCropSourcePx = "";
            this.AnalysisCropLanguagePx = "";
            this.SubtitleCanvasRewrite = false;
            this.AudioCodec = new List<string>();
            this.SubOnly = false;
            this.AudioOnly = false;
            this.KeepSourceAudioLangs = new List<string>();
            this.KeepSourceAudioCodec = new List<string>();
            this.KeepSourceSubtitleLangs = new List<string>();
            this.MkvMergePath = !string.IsNullOrEmpty(AppSettingsService.Instance.Settings.Tools.MkvMergePath) ? AppSettingsService.Instance.Settings.Tools.MkvMergePath : "mkvmerge";
            this.Recursive = true;
            this.DryRun = false;
            this.FileExtensions = new List<string> { "mkv" };
            this.AudioFormat = "";
            this.AudioProcessingScope = "disabled";
            this.AudioDownsample24To16 = false;
            this.AudioPeakNormalize = false;
            this.AudioPeakTargetDb = -1.0;
            this.ErrorMessage = "";
            this.EncodingProfileName = "";
            this.Split = new MkvSplitOptions();
            this.Metadata = new MkvMetadataOptions();
        }

        #endregion

        #region Costanti

        /// <summary>
        /// Modalità remux
        /// </summary>
        public const string MODE_REMUX = "remux";

        /// <summary>
        /// Modalità split
        /// </summary>
        public const string MODE_SPLIT = "split";

        /// <summary>
        /// Modalità metadata
        /// </summary>
        public const string MODE_METADATA = "metadata";

        /// <summary>
        /// Speed correction disabilitata
        /// </summary>
        public const string SPEED_CORRECTION_OFF = "off";

        /// <summary>
        /// Speed correction automatica conservativa
        /// </summary>
        public const string SPEED_CORRECTION_AUTO = "auto";

        /// <summary>
        /// Speed correction con stretch factor manuale verificato
        /// </summary>
        public const string SPEED_CORRECTION_MANUAL = "manual";

        /// <summary>
        /// Modalità audio source fill: inizio file
        /// </summary>
        public const string AUDIO_SOURCE_FILL_START = "start";

        /// <summary>
        /// Modalità audio source fill: fine file
        /// </summary>
        public const string AUDIO_SOURCE_FILL_END = "end";

        /// <summary>
        /// Modalità audio source fill: INSERT_SILENCE DeepAnalysis
        /// </summary>
        public const string AUDIO_SOURCE_FILL_INSERT_SILENCE = "insert-silence";

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Parsa gli argomenti da riga di comando in un'istanza
        /// </summary>
        /// <param name="args">Array di argomenti da riga di comando</param>
        /// <returns>Istanza Options popolata</returns>
        public static Options Parse(string[] args)
        {
            Options options = new Options();
            int i = 0;
            string key;
            bool handled;

            if (args == null)
            {
                options.ErrorMessage = AppText.T("options.invalidArgs");
                return options;
            }

            if (HasHelpArgument(args))
            {
                options.Help = true;
                return options;
            }

            DetectMode(args, options);

            while (i < args.Length && string.IsNullOrEmpty(options.ErrorMessage))
            {
                key = NormalizeKey(args[i]);
                handled = HandleCommonArgument(options, args, ref i, key);

                if (!handled)
                {
                    handled = HandleModeArgument(options, args, ref i, key);
                }

                if (!handled && string.IsNullOrEmpty(options.ErrorMessage))
                {
                    options.ErrorMessage = AppText.F("options.unknownParameter", key);
                }
            }

            return options;
        }

        /// <summary>
        /// Parsa un crop analisi nel formato L:R:T:B in pixel
        /// </summary>
        /// <param name="value">Valore da parsare</param>
        /// <param name="left">Pixel da tagliare a sinistra</param>
        /// <param name="right">Pixel da tagliare a destra</param>
        /// <param name="top">Pixel da tagliare in alto</param>
        /// <param name="bottom">Pixel da tagliare in basso</param>
        /// <returns>True se il valore è vuoto o valido</returns>
        public static bool TryParseAnalysisCropPx(string value, out int left, out int right, out int top, out int bottom)
        {
            bool result = true;
            string trimmed = value != null ? value.Trim() : "";
            string[] parts;

            left = 0;
            right = 0;
            top = 0;
            bottom = 0;

            if (string.IsNullOrEmpty(trimmed))
            {
                return result;
            }

            parts = trimmed.Split(':');
            if (parts.Length != 4 ||
                !int.TryParse(parts[0].Trim(), out left) ||
                !int.TryParse(parts[1].Trim(), out right) ||
                !int.TryParse(parts[2].Trim(), out top) ||
                !int.TryParse(parts[3].Trim(), out bottom) ||
                left < 0 || right < 0 || top < 0 || bottom < 0)
            {
                result = false;
            }

            return result;
        }

        /// <summary>
        /// Normalizza il crop analisi rimuovendo il valore no-op
        /// </summary>
        /// <param name="value">Valore crop L:R:T:B</param>
        /// <returns>Stringa normalizzata, vuota se nessun crop</returns>
        public static string NormalizeAnalysisCropPx(string value)
        {
            string result = value != null ? value.Trim() : "";
            int left;
            int right;
            int top;
            int bottom;

            if (TryParseAnalysisCropPx(result, out left, out right, out top, out bottom) &&
                left == 0 && right == 0 && top == 0 && bottom == 0)
            {
                result = "";
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Rileva --mode prima del parsing completo
        /// </summary>
        private static void DetectMode(string[] args, Options options)
        {
            string key;
            string mode = "";

            for (int i = 0; i < args.Length; i++)
            {
                key = NormalizeKey(args[i]);
                if (key == "mode")
                {
                    if (i + 1 >= args.Length)
                    {
                        options.ErrorMessage = AppText.T("options.missingModeValue");
                        return;
                    }

                    mode = args[i + 1].Trim().ToLowerInvariant();
                    break;
                }
            }

            if (string.IsNullOrEmpty(mode))
            {
                options.ErrorMessage = AppText.T("options.missingMode");
            }
            else if (mode != MODE_REMUX && mode != MODE_SPLIT && mode != MODE_METADATA)
            {
                options.ErrorMessage = AppText.F("options.invalidMode", mode);
            }
            else
            {
                options.Mode = mode;
            }
        }

        /// <summary>
        /// Smista argomenti specifici della modalità
        /// </summary>
        private static bool HandleModeArgument(Options options, string[] args, ref int i, string key)
        {
            if (options.Mode == MODE_SPLIT)
            {
                return HandleSplitArgument(options, args, ref i, key);
            }

            if (options.Mode == MODE_METADATA)
            {
                return HandleMetadataArgument(options, args, ref i, key);
            }

            return HandleRemuxArgument(options, args, ref i, key);
        }

        /// <summary>
        /// Gestisce argomenti comuni a remux e split
        /// </summary>
        private static bool HandleCommonArgument(Options options, string[] args, ref int i, string key)
        {
            bool handled = true;

            if (key == "mode")
            {
                if (RequireValue(args, i, options))
                {
                    i += 2;
                }
            }
            else if (key == "h" || key == "help" || key == "?")
            {
                options.Help = true;
                i++;
            }
            else if (key == "n" || key == "dry-run")
            {
                options.DryRun = true;
                options.Split.DryRun = true;
                options.Metadata.DryRun = true;
                i++;
            }
            else if (key == "r" || key == "recursive")
            {
                options.Recursive = true;
                options.Metadata.Recursive = true;
                i++;
            }
            else if (key == "nr" || key == "no-recursive")
            {
                options.Recursive = false;
                options.Metadata.Recursive = false;
                i++;
            }
            else if (key == "ext" || key == "extensions")
            {
                if (RequireValue(args, i, options))
                {
                    options.FileExtensions.Clear();
                    ParseExtensions(args[i + 1], options.FileExtensions);
                    i += 2;
                }
            }
            else if (key == "lang")
            {
                if (RequireValue(args, i, options))
                {
                    options.Language = AppText.NormalizeLanguage(args[i + 1]);
                    if (string.IsNullOrEmpty(options.Language))
                    {
                        options.ErrorMessage = AppText.F("options.invalidLanguage", args[i + 1]);
                    }
                    i += 2;
                }
            }
            else
            {
                handled = false;
            }

            return handled;
        }

        /// <summary>
        /// Gestisce argomenti metadata
        /// </summary>
        private static bool HandleMetadataArgument(Options options, string[] args, ref int i, string key)
        {
            bool handled = true;
            string value;

            if (key == "o" || key == "overwrite")
            {
                options.Overwrite = true;
                options.Metadata.OutputPolicy = MkvMetadataOutputPolicy.Overwrite;
                i++;
            }
            else if (key == "preserve-folder-structure")
            {
                options.Metadata.PreserveFolderStructure = true;
                i++;
            }
            else if (key == "no-preserve-folder-structure")
            {
                options.Metadata.PreserveFolderStructure = false;
                i++;
            }
            else if (IsMetadataValueArgument(key))
            {
                if (RequireValue(args, i, options))
                {
                    value = args[i + 1];
                    if (key == "s" || key == "source")
                    {
                        options.SourceFolder = value;
                        options.Metadata.SourcePath = value;
                    }
                    else if (key == "preset")
                    {
                        options.Metadata.PresetPath = value;
                    }
                    else if (key == "d" || key == "destination" || key == "output-dir")
                    {
                        options.DestinationFolder = value;
                        options.Metadata.OutputDir = value;
                        options.Metadata.OutputPolicy = MkvMetadataOutputPolicy.OutputPath;
                    }
                    i += 2;
                }
            }
            else
            {
                handled = false;
            }

            options.Metadata.Recursive = options.Recursive;
            options.Metadata.DryRun = options.DryRun;
            return handled;
        }

        /// <summary>
        /// Indica se un argomento metadata richiede un valore
        /// </summary>
        private static bool IsMetadataValueArgument(string key)
        {
            return key == "s" || key == "source" ||
                key == "preset" ||
                key == "d" || key == "destination" || key == "output-dir";
        }

        /// <summary>
        /// Gestisce argomenti remux
        /// </summary>
        private static bool HandleRemuxArgument(Options options, string[] args, ref int i, string key)
        {
            bool handled = true;
            string value;
            int delay;
            string audioFormat;
            double peakTargetDb;

            if (key == "fs" || key == "framesync")
            {
                options.FrameSync = true;
                i++;
            }
            else if (key == "framesync-diagnostics")
            {
                options.FrameSyncDiagnostics = true;
                i++;
            }
            else if (key == "deep-analysis-diagnostics")
            {
                options.DeepAnalysisDiagnostics = true;
                i++;
            }
            else if (key == "da" || key == "deep-analysis")
            {
                options.DeepAnalysis = true;
                i++;
            }
            else if (key == "so" || key == "sub-only")
            {
                options.SubOnly = true;
                i++;
            }
            else if (key == "ao" || key == "audio-only")
            {
                options.AudioOnly = true;
                i++;
            }
            else if (key == "audio-24-to-16")
            {
                options.AudioDownsample24To16 = true;
                i++;
            }
            else if (key == "audio-peak-normalize")
            {
                options.AudioPeakNormalize = true;
                i++;
            }
            else if (key == "o" || key == "overwrite")
            {
                options.Overwrite = true;
                i++;
            }
            else if (key == "no-speed-correction")
            {
                options.SpeedCorrectionMode = SPEED_CORRECTION_OFF;
                i++;
            }
            else if (key == "subtitle-canvas-rewrite" || key == "sub-canvas-rewrite")
            {
                options.SubtitleCanvasRewrite = true;
                i++;
            }
            else if (IsRemuxValueArgument(key))
            {
                if (RequireValue(args, i, options))
                {
                    value = args[i + 1];
                    if (key == "s" || key == "source")
                    {
                        options.SourceFolder = value;
                    }
                    else if (key == "l" || key == "language")
                    {
                        options.LanguageFolder = value;
                    }
                    else if (key == "t" || key == "target-language")
                    {
                        ParseCsvToList(value, options.TargetLanguage);
                    }
                    else if (key == "m" || key == "match-pattern")
                    {
                        options.MatchPattern = value;
                    }
                    else if (key == "d" || key == "destination")
                    {
                        options.DestinationFolder = value;
                    }
                    else if (key == "ad" || key == "audio-delay")
                    {
                        if (!int.TryParse(value, out delay))
                        {
                            options.ErrorMessage = AppText.F("options.invalidIntValue", "audio-delay", value);
                        }
                        options.AudioDelay = delay;
                    }
                    else if (key == "sd" || key == "subtitle-delay")
                    {
                        if (!int.TryParse(value, out delay))
                        {
                            options.ErrorMessage = AppText.F("options.invalidIntValue", "subtitle-delay", value);
                        }
                        options.SubtitleDelay = delay;
                    }
                    else if (key == "audio-source-fill-threshold-ms")
                    {
                        if (!int.TryParse(value, out delay))
                        {
                            options.ErrorMessage = AppText.F("options.invalidIntValue", "audio-source-fill-threshold-ms", value);
                        }
                        options.AudioSourceFillThresholdMs = delay;
                    }
                    else if (key == "audio-source-fill-language")
                    {
                        options.AudioSourceFillLanguage = value.Trim();
                    }
                    else if (key == "audio-source-fill-modes")
                    {
                        ParseAudioSourceFillModes(value, options);
                    }
                    else if (key == "speed-correction")
                    {
                        options.SpeedCorrectionMode = NormalizeSpeedCorrectionMode(value);
                        if (string.IsNullOrEmpty(options.SpeedCorrectionMode))
                        {
                            options.ErrorMessage = AppText.F("options.invalidSpeedCorrection", value);
                        }
                    }
                    else if (key == "stretch-factor")
                    {
                        options.ManualStretchFactor = value.Trim();
                        options.SpeedCorrectionMode = SPEED_CORRECTION_MANUAL;
                    }
                    else if (key == "analysis-crop-source-px" || key == "analysis-crop-source")
                    {
                        options.AnalysisCropSourcePx = NormalizeAnalysisCropPx(value);
                    }
                    else if (key == "analysis-crop-lang-px" || key == "analysis-crop-language-px" || key == "analysis-crop-lang" || key == "analysis-crop-language")
                    {
                        options.AnalysisCropLanguagePx = NormalizeAnalysisCropPx(value);
                    }
                    else if (key == "ac" || key == "audio-codec")
                    {
                        ParseCsvToList(value, options.AudioCodec);
                    }
                    else if (key == "ksa" || key == "keep-source-audio")
                    {
                        ParseCsvToList(value, options.KeepSourceAudioLangs);
                    }
                    else if (key == "ksac" || key == "keep-source-audio-codec")
                    {
                        ParseCsvToList(value, options.KeepSourceAudioCodec);
                    }
                    else if (key == "kss" || key == "keep-source-subs")
                    {
                        ParseCsvToList(value, options.KeepSourceSubtitleLangs);
                    }
                    else if (key == "audio-format")
                    {
                        audioFormat = value.Trim().ToLowerInvariant();
                        if (audioFormat == "flac" || audioFormat == "lpcm" || audioFormat == "aac" || audioFormat == "opus" || audioFormat == "ac3")
                        {
                            options.AudioFormat = audioFormat;
                            if (options.AudioProcessingScope == "disabled")
                            {
                                options.AudioProcessingScope = "all";
                            }
                        }
                        else
                        {
                            options.ErrorMessage = AppText.F("options.invalidAudioFormat", value);
                        }
                    }
                    else if (key == "audio-scope")
                    {
                        options.AudioProcessingScope = NormalizeScope(value);
                        if (string.IsNullOrEmpty(options.AudioProcessingScope))
                        {
                            options.ErrorMessage = AppText.F("options.invalidAudioScope", value);
                        }
                    }
                    else if (key == "audio-peak-target-db")
                    {
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out peakTargetDb))
                        {
                            options.ErrorMessage = AppText.F("options.invalidIntValue", "audio-peak-target-db", value);
                        }
                        options.AudioPeakTargetDb = peakTargetDb;
                    }
                    else if (key == "mkv" || key == "mkvmerge-path")
                    {
                        options.MkvMergePath = value;
                    }
                    else if (key == "ep" || key == "encoding-profile")
                    {
                        options.EncodingProfileName = value;
                    }
                    i += 2;
                }
            }
            else
            {
                handled = false;
            }

            return handled;
        }

        /// <summary>
        /// Indica se un argomento remux richiede un valore
        /// </summary>
        /// <param name="key">Chiave normalizzata</param>
        /// <returns>True se la chiave è una option remux con valore</returns>
        private static bool IsRemuxValueArgument(string key)
        {
            return key == "s" || key == "source" ||
                key == "l" || key == "language" ||
                key == "t" || key == "target-language" ||
                key == "m" || key == "match-pattern" ||
                key == "d" || key == "destination" ||
                key == "ad" || key == "audio-delay" ||
                key == "sd" || key == "subtitle-delay" ||
                key == "audio-source-fill-threshold-ms" ||
                key == "audio-source-fill-language" ||
                key == "audio-source-fill-modes" ||
                key == "speed-correction" ||
                key == "stretch-factor" ||
                key == "analysis-crop-source-px" || key == "analysis-crop-source" ||
                key == "analysis-crop-lang-px" || key == "analysis-crop-language-px" ||
                key == "analysis-crop-lang" || key == "analysis-crop-language" ||
                key == "ac" || key == "audio-codec" ||
                key == "ksa" || key == "keep-source-audio" ||
                key == "ksac" || key == "keep-source-audio-codec" ||
                key == "kss" || key == "keep-source-subs" ||
                key == "audio-format" ||
                key == "audio-scope" ||
                key == "audio-peak-target-db" ||
                key == "mkv" || key == "mkvmerge-path" ||
                key == "ep" || key == "encoding-profile";
        }

        /// <summary>
        /// Normalizza uno scope audio
        /// </summary>
        private static string NormalizeScope(string value)
        {
            string result = "";
            string trimmed = value != null ? value.Trim().ToLowerInvariant() : "";

            if (trimmed == "disabled" || trimmed == "off" || trimmed == "no")
            {
                result = "disabled";
            }
            else if (trimmed == "lang" || trimmed == "language")
            {
                result = "lang";
            }
            else if (trimmed == "all" || trimmed == "tutti")
            {
                result = "all";
            }

            return result;
        }

        /// <summary>
        /// Gestisce argomenti split
        /// </summary>
        private static bool HandleSplitArgument(Options options, string[] args, ref int i, string key)
        {
            bool handled = true;
            string value;

            if (key == "chapters-each")
            {
                options.Split.ChaptersEach = true;
                i++;
            }
            else if (key == "force")
            {
                options.Split.Force = true;
                i++;
            }
            else if (IsSplitValueArgument(key))
            {
                if (RequireValue(args, i, options))
                {
                    value = args[i + 1];
                    if (key == "s" || key == "source")
                    {
                        options.SourceFolder = value;
                        options.Split.SourcePath = value;
                    }
                    else if (key == "source-raw")
                    {
                        options.Split.SourceRaw = value;
                    }
                    else if (key == "d" || key == "destination" || key == "o" || key == "output-dir")
                    {
                        options.DestinationFolder = value;
                        options.Split.OutputDir = value;
                    }
                    else if (key == "pattern")
                    {
                        options.Split.Pattern = value;
                    }
                    else if (key == "ranges")
                    {
                        options.Split.Ranges = value;
                    }
                    else if (key == "split-at")
                    {
                        options.Split.SplitAt = value;
                    }
                    else if (key == "trim-start")
                    {
                        options.Split.TrimStart = value;
                    }
                    else if (key == "trim-end")
                    {
                        options.Split.TrimEnd = value;
                    }
                    else if (key == "output-template")
                    {
                        options.Split.OutputTemplate = value;
                    }
                    else if (key == "snap")
                    {
                        options.Split.Snap = ParseSnapMode(value, options);
                    }
                    else if (key == "log")
                    {
                        options.Split.Log = value;
                    }
                    i += 2;
                }
            }
            else
            {
                handled = false;
            }

            return handled;
        }

        /// <summary>
        /// Indica se un argomento split richiede un valore
        /// </summary>
        /// <param name="key">Chiave normalizzata</param>
        /// <returns>True se la chiave è una option split con valore</returns>
        private static bool IsSplitValueArgument(string key)
        {
            return key == "s" || key == "source" ||
                key == "source-raw" ||
                key == "d" || key == "destination" || key == "o" || key == "output-dir" ||
                key == "pattern" ||
                key == "ranges" ||
                key == "split-at" ||
                key == "trim-start" ||
                key == "trim-end" ||
                key == "output-template" ||
                key == "snap" ||
                key == "log";
        }

        /// <summary>
        /// Verifica se un argomento valore è presente
        /// </summary>
        private static bool RequireValue(string[] args, int i, Options options)
        {
            bool result = (i + 1 < args.Length) && (!args[i + 1].StartsWith("-") || (args[i + 1].Length > 1 && char.IsDigit(args[i + 1][1])));
            if (!result)
            {
                options.ErrorMessage = AppText.F("options.missingValue", NormalizeKey(args[i]));
            }

            return result;
        }

        /// <summary>
        /// Normalizza una chiave CLI
        /// </summary>
        private static string NormalizeKey(string value)
        {
            return value.ToLowerInvariant().TrimStart('-');
        }

        /// <summary>
        /// True se gli argomenti chiedono help
        /// </summary>
        private static bool HasHelpArgument(string[] args)
        {
            bool result = false;
            string key;
            for (int i = 0; i < args.Length; i++)
            {
                key = NormalizeKey(args[i]);
                if (key == "h" || key == "help" || key == "?")
                {
                    result = true;
                    break;
                }
            }

            return result;
        }

        /// <summary>
        /// Parsa una stringa CSV e aggiunge i valori trimmati non vuoti alla lista
        /// </summary>
        private static void ParseCsvToList(string value, List<string> target)
        {
            string[] parts = value.Split(',');
            string trimmed;

            for (int j = 0; j < parts.Length; j++)
            {
                trimmed = parts[j].Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    target.Add(trimmed);
                }
            }
        }

        /// <summary>
        /// Parsa modalità audio source fill
        /// </summary>
        /// <param name="value">Lista CSV modalità</param>
        /// <param name="options">Opzioni da aggiornare</param>
        private static void ParseAudioSourceFillModes(string value, Options options)
        {
            string[] parts = value.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string mode = parts[i].Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(mode))
                {
                    continue;
                }
                if (mode == AUDIO_SOURCE_FILL_START)
                {
                    options.AudioSourceFillStart = true;
                }
                else if (mode == AUDIO_SOURCE_FILL_END)
                {
                    options.AudioSourceFillEnd = true;
                }
                else if (mode == AUDIO_SOURCE_FILL_INSERT_SILENCE)
                {
                    options.AudioSourceFillInsertSilence = true;
                }
                else
                {
                    options.ErrorMessage = AppText.F("options.invalidAudioSourceFillMode", mode);
                }
            }
        }

        /// <summary>
        /// Parsa estensioni CSV rimuovendo il punto iniziale
        /// </summary>
        private static void ParseExtensions(string value, List<string> target)
        {
            string[] parts = value.Split(',');
            string trimmed;

            for (int j = 0; j < parts.Length; j++)
            {
                trimmed = parts[j].Trim().TrimStart('.');
                if (!string.IsNullOrEmpty(trimmed))
                {
                    target.Add(trimmed);
                }
            }
        }

        /// <summary>
        /// Normalizza la modalità speed correction
        /// </summary>
        private static string NormalizeSpeedCorrectionMode(string value)
        {
            string result = "";
            string trimmed = value != null ? value.Trim().ToLowerInvariant() : "";

            if (trimmed == SPEED_CORRECTION_OFF || trimmed == "none" || trimmed == "disabled")
            {
                result = SPEED_CORRECTION_OFF;
            }
            else if (trimmed == SPEED_CORRECTION_AUTO || trimmed == "autosafe")
            {
                result = SPEED_CORRECTION_AUTO;
            }
            else if (trimmed == SPEED_CORRECTION_MANUAL)
            {
                result = SPEED_CORRECTION_MANUAL;
            }

            return result;
        }

        /// <summary>
        /// Parsa modalità snap split
        /// </summary>
        private static MkvSplitSnapMode ParseSnapMode(string value, Options options)
        {
            MkvSplitSnapMode result = MkvSplitSnapMode.Off;
            string trimmed = value != null ? value.Trim().ToLowerInvariant() : "";

            if (trimmed == "off")
            {
                result = MkvSplitSnapMode.Off;
            }
            else if (trimmed == "before")
            {
                result = MkvSplitSnapMode.Before;
            }
            else if (trimmed == "after")
            {
                result = MkvSplitSnapMode.After;
            }
            else if (trimmed == "nearest")
            {
                result = MkvSplitSnapMode.Nearest;
            }
            else
            {
                options.ErrorMessage = AppText.T("options.invalidSnap");
            }

            return result;
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// Modalità operativa obbligatoria: remux, split o metadata
        /// </summary>
        public string Mode { get; set; }

        /// <summary>
        /// Lingua richiesta da CLI (--lang)
        /// </summary>
        public string Language { get; set; }

        /// <summary>
        /// Indica se è stato richiesto il messaggio di aiuto (-h, --help)
        /// </summary>
        public bool Help { get; set; }

        /// <summary>
        /// Percorso della cartella sorgente contenente i file MKV (-s, --source)
        /// </summary>
        public string SourceFolder { get; set; }

        /// <summary>
        /// Percorso della cartella lingua contenente i file MKV nella lingua alternativa (-l, --language)
        /// </summary>
        public string LanguageFolder { get; set; }

        /// <summary>
        /// Lista di codici lingua ISO 639-2 da estrarre (-t, --target-language). Supporta valori multipli separati da virgola
        /// </summary>
        public List<string> TargetLanguage { get; set; }

        /// <summary>
        /// Pattern regex per il matching degli episodi (-m, --match-pattern). Default: S(\d+)E(\d+)
        /// </summary>
        public string MatchPattern { get; set; }

        /// <summary>
        /// Modalità overwrite: sovrascrive i file sorgente (-o, --overwrite). Default: false
        /// </summary>
        public bool Overwrite { get; set; }

        /// <summary>
        /// Cartella di destinazione per l'output (-d, --destination)
        /// </summary>
        public string DestinationFolder { get; set; }

        /// <summary>
        /// Ritardo audio manuale in millisecondi (-ad, --audio-delay). Sommato all'offset frame-sync se abilitato
        /// </summary>
        public int AudioDelay { get; set; }

        /// <summary>
        /// Ritardo sottotitoli manuale in millisecondi (-sd, --subtitle-delay). Sommato all'offset frame-sync se abilitato
        /// </summary>
        public int SubtitleDelay { get; set; }

        /// <summary>
        /// Soglia oltre cui usare audio source per riempire inizio, fine o INSERT_SILENCE
        /// </summary>
        public int AudioSourceFillThresholdMs { get; set; }

        /// <summary>
        /// Lingua audio sorgente da usare per audio source fill
        /// </summary>
        public string AudioSourceFillLanguage { get; set; }

        /// <summary>
        /// Usa audio source all'inizio se il delay positivo supera la soglia
        /// </summary>
        public bool AudioSourceFillStart { get; set; }

        /// <summary>
        /// Usa audio source in coda se la traccia lang termina prima del source oltre soglia
        /// </summary>
        public bool AudioSourceFillEnd { get; set; }

        /// <summary>
        /// Usa audio source per INSERT_SILENCE DeepAnalysis oltre soglia
        /// </summary>
        public bool AudioSourceFillInsertSilence { get; set; }

        /// <summary>
        /// Modalità speed correction: off, auto, manual
        /// </summary>
        public string SpeedCorrectionMode { get; set; }

        /// <summary>
        /// Stretch factor manuale per mkvmerge --sync
        /// </summary>
        public string ManualStretchFactor { get; set; }

        /// <summary>
        /// Indica se il raffinamento frame sync è abilitato (-fs, --framesync)
        /// </summary>
        public bool FrameSync { get; set; }

        /// <summary>
        /// Scrive diagnostica JSON per il frame-sync (--framesync-diagnostics)
        /// </summary>
        public bool FrameSyncDiagnostics { get; set; }

        /// <summary>
        /// Indica se la deep analysis è abilitata (-da, --deep-analysis). Mutuamente esclusiva con FrameSync
        /// </summary>
        public bool DeepAnalysis { get; set; }

        /// <summary>
        /// Scrive diagnostica JSON per la deep analysis (--deep-analysis-diagnostics)
        /// </summary>
        public bool DeepAnalysisDiagnostics { get; set; }

        /// <summary>
        /// Crop manuale source per frame di analisi visuale, formato L:R:T:B in pixel
        /// </summary>
        public string AnalysisCropSourcePx { get; set; }

        /// <summary>
        /// Crop manuale lingua per frame di analisi visuale, formato L:R:T:B in pixel
        /// </summary>
        public string AnalysisCropLanguagePx { get; set; }

        /// <summary>
        /// Riscrive canvas e coordinate dei sottotitoli importati quando possibile
        /// </summary>
        public bool SubtitleCanvasRewrite { get; set; }

        /// <summary>
        /// Lista di codec audio da importare (-ac, --audio-codec). Solo le tracce con questi codec verranno importate
        /// </summary>
        public List<string> AudioCodec { get; set; }

        /// <summary>
        /// Importa solo sottotitoli, ignora tracce audio (-so, --sub-only)
        /// </summary>
        public bool SubOnly { get; set; }

        /// <summary>
        /// Importa solo tracce audio, ignora sottotitoli (-ao, --audio-only)
        /// </summary>
        public bool AudioOnly { get; set; }

        /// <summary>
        /// Lista di codici lingua da mantenere nelle tracce audio sorgente (-ksa, --keep-source-audio)
        /// </summary>
        public List<string> KeepSourceAudioLangs { get; set; }

        /// <summary>
        /// Lista di codec audio da mantenere nelle tracce sorgente (-ksac, --keep-source-audio-codec). Solo le tracce con questi codec verranno mantenute
        /// </summary>
        public List<string> KeepSourceAudioCodec { get; set; }

        /// <summary>
        /// Lista di codici lingua da mantenere nelle tracce sottotitoli sorgente (-kss, --keep-source-subs)
        /// </summary>
        public List<string> KeepSourceSubtitleLangs { get; set; }

        /// <summary>
        /// Percorso dell'eseguibile mkvmerge (-mkv, --mkvmerge-path). Default: cerca nel PATH
        /// </summary>
        public string MkvMergePath { get; set; }

        /// <summary>
        /// Formato audio finale per tracce processate. Valori: flac, lpcm, aac, opus, ac3 o vuoto
        /// </summary>
        public string AudioFormat { get; set; }

        /// <summary>
        /// Scope del processing audio: disabled, lang, all
        /// </summary>
        public string AudioProcessingScope { get; set; }

        /// <summary>
        /// Converte audio 24-bit in 16-bit con soxr e dither
        /// </summary>
        public bool AudioDownsample24To16 { get; set; }

        /// <summary>
        /// Applica peak normalization alle tracce processate
        /// </summary>
        public bool AudioPeakNormalize { get; set; }

        /// <summary>
        /// Target dB per peak normalization
        /// </summary>
        public double AudioPeakTargetDb { get; set; }

        /// <summary>
        /// Indica se cercare ricorsivamente nelle sottocartelle (-r, --recursive). Default: true
        /// </summary>
        public bool Recursive { get; set; }

        /// <summary>
        /// Modalità dry run: mostra cosa verrebbe fatto senza eseguire (-n, --dry-run)
        /// </summary>
        public bool DryRun { get; set; }

        /// <summary>
        /// Lista di estensioni file da cercare (-ext, --extensions). Default: mkv
        /// </summary>
        public List<string> FileExtensions { get; set; }

        /// <summary>
        /// Messaggio di errore dal parsing. Vuoto se nessun errore
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Nome del profilo di encoding video post-merge, vuoto = disabilitato
        /// </summary>
        public string EncodingProfileName { get; set; }

        /// <summary>
        /// Opzioni specifiche della modalità split
        /// </summary>
        public MkvSplitOptions Split { get; set; }

        /// <summary>
        /// Opzioni specifiche della modalità metadata
        /// </summary>
        public MkvMetadataOptions Metadata { get; set; }

        #endregion
    }
}
