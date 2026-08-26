using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace RemuxForge.Core.Configuration
{
    /// <summary>
    /// Valida le regole funzionali condivise delle opzioni CLI/WebUI
    /// </summary>
    public static class OptionsValidator
    {
        #region Metodi pubblici

        /// <summary>
        /// Valida le opzioni
        /// </summary>
        /// <param name="options">Opzioni da validare</param>
        /// <param name="requireSourceFolder">True se la sorgente è obbligatoria</param>
        /// <param name="validateFolderExists">True se validare l'esistenza delle cartelle</param>
        /// <returns>Risultato validazione</returns>
        public static OptionsValidationResult Validate(Options options, bool requireSourceFolder, bool validateFolderExists)
        {
            OptionsValidationResult result = new OptionsValidationResult();
            bool needsMerge;
            bool needsFilter;
            bool needsRemux;
            bool needsEncode;
            if (options == null)
            {
                result.AddError(AppText.T("validation.invalidConfig"));
                return result;
            }

            if (options.Mode != Options.MODE_REMUX && options.Mode != Options.MODE_SPLIT && options.Mode != Options.MODE_METADATA)
            {
                result.AddError(AppText.T("validation.missingInvalidMode"));
                return result;
            }

            if (options.TargetLanguage == null || options.AudioCodec == null || options.KeepSourceAudioLangs == null || options.KeepSourceAudioCodec == null || options.KeepSourceSubtitleLangs == null || options.FileExtensions == null)
            {
                result.AddError(AppText.T("validation.invalidConfig"));
                return result;
            }

            if (options.Mode == Options.MODE_SPLIT)
            {
                ValidateSplitOptions(options, requireSourceFolder, validateFolderExists, result);
                return result;
            }

            if (options.Mode == Options.MODE_METADATA)
            {
                ValidateMetadataOptions(options, requireSourceFolder, validateFolderExists, result);
                return result;
            }

            needsMerge = options.TargetLanguage.Count > 0;
            needsFilter = options.KeepSourceAudioLangs.Count > 0 || options.KeepSourceAudioCodec.Count > 0 || options.KeepSourceSubtitleLangs.Count > 0;
            needsRemux = needsMerge || needsFilter || !string.IsNullOrEmpty(options.AudioFormat);
            needsEncode = !string.IsNullOrEmpty(options.EncodingProfileName);

            if (options.FrameSync && options.DeepAnalysis)
            {
                result.AddError(AppText.T("validation.frameSyncDeepExclusive"));
            }

            if (options.SubOnly && options.AudioOnly)
            {
                result.AddError(AppText.T("validation.subOnlyAudioOnlyExclusive"));
            }

            if (options.Overwrite && !string.IsNullOrEmpty(options.DestinationFolder))
            {
                result.AddError(AppText.T("validation.overwriteDestinationExclusive"));
            }

            ValidateSpeedCorrection(options, result);
            ValidateAudioProcessing(options, result);
            ValidateTimelineAudioProcessing(options, needsMerge, result);
            ValidateAudioSourceFill(options, needsMerge, result);
            ValidateAnalysisCrop(options, result);
            if (!File.Exists(options.SourceFolder))
                ValidateRegex(options.MatchPattern, result);
            ValidateExtensions(options, result);
            ValidateLanguages(options, needsMerge, result);
            ValidateCodecs(options, result);
            ValidateFolders(options, requireSourceFolder, validateFolderExists, needsMerge, result);

            if (requireSourceFolder && !needsRemux && !needsEncode)
            {
                result.AddError(AppText.T("validation.noOperation"));
            }

            if (requireSourceFolder && !options.Overwrite && string.IsNullOrEmpty(options.DestinationFolder) && !(needsEncode && !needsRemux))
            {
                result.AddError(AppText.T("validation.destinationOrOverwrite"));
            }

            return result;
        }

        /// <summary>
        /// Valida opzioni della modalità metadata
        /// </summary>
        /// <param name="options">Opzioni da validare</param>
        /// <param name="requireSourceFolder">True se source è obbligatorio</param>
        /// <param name="validateFolderExists">True se controllare esistenza su disco</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateMetadataOptions(Options options, bool requireSourceFolder, bool validateFolderExists, OptionsValidationResult result)
        {
            bool sourceIsFile;
            bool sourceIsFolder;

            if (options.Metadata == null)
            {
                result.AddError(AppText.T("metadata.validation.invalidConfig"));
                return;
            }

            options.Metadata.SourcePath = !string.IsNullOrEmpty(options.Metadata.SourcePath) ? options.Metadata.SourcePath : options.SourceFolder;
            options.Metadata.OutputDir = !string.IsNullOrEmpty(options.Metadata.OutputDir) ? options.Metadata.OutputDir : options.DestinationFolder;
            options.Metadata.Recursive = options.Recursive;
            options.Metadata.DryRun = options.DryRun;

            if (requireSourceFolder && string.IsNullOrEmpty(options.Metadata.SourcePath))
            {
                result.AddError(AppText.T("validation.sourceRequired"));
            }

            if (requireSourceFolder && string.IsNullOrEmpty(options.Metadata.PresetPath))
            {
                result.AddError(AppText.T("metadata.validation.presetRequired"));
            }

            if (validateFolderExists && !string.IsNullOrEmpty(options.Metadata.SourcePath))
            {
                sourceIsFile = File.Exists(options.Metadata.SourcePath);
                sourceIsFolder = Directory.Exists(options.Metadata.SourcePath);
                if (!sourceIsFile && !sourceIsFolder)
                {
                    result.AddError(AppText.F("metadata.validation.inputNotFound", options.Metadata.SourcePath));
                }
                else if (sourceIsFile && !string.Equals(Path.GetExtension(options.Metadata.SourcePath), ".mkv", StringComparison.OrdinalIgnoreCase))
                {
                    result.AddError(AppText.T("metadata.validation.onlyMkv"));
                }
            }

            if (validateFolderExists && !string.IsNullOrEmpty(options.Metadata.PresetPath) && !File.Exists(options.Metadata.PresetPath))
            {
                result.AddError(AppText.F("metadata.validation.presetNotFound", options.Metadata.PresetPath));
            }

            if (options.Metadata.OutputPolicy == MkvMetadataOutputPolicy.OutputPath && string.IsNullOrEmpty(options.Metadata.OutputDir))
            {
                result.AddError(AppText.T("metadata.validation.outputPathRequired"));
            }

            if (validateFolderExists && options.Metadata.OutputPolicy == MkvMetadataOutputPolicy.OutputPath && !string.IsNullOrEmpty(options.Metadata.OutputDir) && !Directory.Exists(options.Metadata.OutputDir))
            {
                result.AddError(AppText.F("metadata.validation.outputFolderNotFound", options.Metadata.OutputDir));
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Valida modalità e parametro manuale della speed correction
        /// </summary>
        /// <param name="options">Opzioni da validare</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateSpeedCorrection(Options options, OptionsValidationResult result)
        {
            if (options.SpeedCorrectionMode != Options.SPEED_CORRECTION_OFF &&
                options.SpeedCorrectionMode != Options.SPEED_CORRECTION_MANUAL)
            {
                result.AddError(AppText.F("options.invalidSpeedCorrection", options.SpeedCorrectionMode));
                return;
            }

            if (options.SpeedCorrectionMode == Options.SPEED_CORRECTION_MANUAL)
            {
                // In manuale lo stretch deve essere esplicito: non si tenta inferenza automatica su VFR
                if (string.IsNullOrWhiteSpace(options.ManualStretchFactor))
                {
                    result.AddError(AppText.T("validation.speedManualNeedsStretch"));
                }
                else if (!IsValidStretchFactor(options.ManualStretchFactor))
                {
                    result.AddError(AppText.F("validation.invalidManualStretch", options.ManualStretchFactor));
                }
            }
        }

        /// <summary>
        /// Valida opzioni audio source fill
        /// </summary>
        /// <param name="options">Opzioni da validare</param>
        /// <param name="needsMerge">True se è richiesto merge da language</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateAudioSourceFill(Options options, bool needsMerge, OptionsValidationResult result)
        {
            bool anyMode = options.AudioSourceFillStart || options.AudioSourceFillEnd || options.AudioSourceFillInsertSilence;
            bool active = anyMode || options.AudioSourceFillThresholdMs > 0 || !string.IsNullOrEmpty(options.AudioSourceFillLanguage);

            if (options.AudioSourceFillThresholdMs < 0)
            {
                result.AddError(AppText.T("validation.sourceFillThresholdNegative"));
            }

            if (active && !needsMerge)
            {
                result.AddError(AppText.T("validation.sourceFillNeedsTargetLanguage"));
            }

            if (active && (string.IsNullOrEmpty(options.AudioFormat) || options.AudioProcessingScope == "disabled"))
            {
                result.AddError(AppText.T("validation.sourceFillNeedsAudio"));
            }

            if (active && options.AudioSourceFillThresholdMs <= 0)
            {
                result.AddError(AppText.T("validation.sourceFillThresholdPositive"));
            }

            if (active && string.IsNullOrEmpty(options.AudioSourceFillLanguage))
            {
                result.AddError(AppText.T("validation.sourceFillLanguageRequired"));
            }

            if (active && !anyMode)
            {
                result.AddError(AppText.T("validation.sourceFillModeRequired"));
            }

            if (options.AudioSourceFillInsertSilence && !options.DeepAnalysis)
            {
                result.AddError(AppText.T("validation.sourceFillInsertSilenceNeedsDeep"));
            }

            if (!string.IsNullOrEmpty(options.AudioSourceFillLanguage))
            {
                ValidateLanguage("audio-source-fill-language", options.AudioSourceFillLanguage, result);
            }
        }

        /// <summary>
        /// Valida opzioni del processing audio
        /// </summary>
        private static void ValidateAudioProcessing(Options options, OptionsValidationResult result)
        {
            if (!IsValidAudioFormat(options.AudioFormat))
            {
                result.AddError(AppText.F("options.invalidAudioFormat", options.AudioFormat));
            }

            if (!IsValidScope(options.AudioProcessingScope))
            {
                result.AddError(AppText.F("options.invalidAudioScope", options.AudioProcessingScope));
            }

            if (options.AudioProcessingScope != "disabled" && string.IsNullOrEmpty(options.AudioFormat))
            {
                result.AddError(AppText.T("validation.audioFormatRequiredWithScope"));
            }

            if ((options.AudioPeakNormalize || options.AudioDownsample24To16) && (string.IsNullOrEmpty(options.AudioFormat) || options.AudioProcessingScope == "disabled"))
            {
                result.AddError(AppText.T("validation.audioNormalizeNeedsFormat"));
            }

            if (options.AudioDownsample24To16 && options.AudioFormat != "flac" && options.AudioFormat != "lpcm")
            {
                result.AddError(AppText.T("validation.audio24To16OnlyFlacLpcm"));
            }

            if (options.AudioPeakTargetDb > 0.0)
            {
                result.AddError(AppText.T("validation.audioPeakTargetMaxZero"));
            }

            if (options.AudioPeakTargetDb < -60.0)
            {
                result.AddError(AppText.T("validation.audioPeakTargetMin"));
            }
        }

        /// <summary>
        /// Verifica se Speed Correction o DeepAnalysis richiedono il render audio Language
        /// </summary>
        /// <param name="options">Opzioni correnti</param>
        /// <param name="needsMerge">True se è configurato un merge Language</param>
        /// <returns>True se tutte le tracce audio Language devono essere processate</returns>
        public static bool RequiresTimelineAudioProcessing(Options options, bool needsMerge)
        {
            return options != null &&
                needsMerge &&
                !options.SubOnly &&
                (options.SpeedCorrectionMode != Options.SPEED_CORRECTION_OFF || options.DeepAnalysis);
        }

        /// <summary>
        /// Valida la configurazione audio obbligatoria per Speed Correction e DeepAnalysis
        /// </summary>
        private static void ValidateTimelineAudioProcessing(Options options, bool needsMerge, OptionsValidationResult result)
        {
            if (!RequiresTimelineAudioProcessing(options, needsMerge))
            {
                return;
            }

            if (string.IsNullOrEmpty(options.AudioFormat))
            {
                if (options.SpeedCorrectionMode != Options.SPEED_CORRECTION_OFF)
                {
                    result.AddError(AppText.T("validation.speedNeedsAudioFormat"));
                }
                if (options.DeepAnalysis)
                {
                    result.AddError(AppText.T("validation.deepNeedsAudioFormat"));
                }
            }

            if (options.AudioProcessingScope == "disabled")
            {
                result.AddError(AppText.T("validation.timelineAudioNeedsLangScope"));
            }
        }

        /// <summary>
        /// Verifica se un formato audio è valido
        /// </summary>
        /// <param name="value">Formato audio</param>
        /// <returns>True se valido</returns>
        private static bool IsValidAudioFormat(string value)
        {
            return string.IsNullOrEmpty(value) || value == "flac" || value == "lpcm" || value == "aac" || value == "opus" || value == "ac3";
        }

        /// <summary>
        /// Verifica se uno scope audio è valido
        /// </summary>
        /// <param name="value">Scope audio</param>
        /// <returns>True se valido</returns>
        private static bool IsValidScope(string value)
        {
            return value == "disabled" || value == "lang" || value == "all";
        }

        /// <summary>
        /// Valida i crop manuali usati solo dal matching visuale
        /// </summary>
        /// <param name="options">Opzioni da validare</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateAnalysisCrop(Options options, OptionsValidationResult result)
        {
            if (!Options.TryParseAnalysisCropPx(options.AnalysisCropSourcePx, out _, out _, out _, out _))
            {
                result.AddError(AppText.T("validation.invalidAnalysisCropSource"));
            }

            if (!Options.TryParseAnalysisCropPx(options.AnalysisCropLanguagePx, out _, out _, out _, out _))
            {
                result.AddError(AppText.T("validation.invalidAnalysisCropLang"));
            }
        }

        /// <summary>
        /// Valida la regex di matching episodio
        /// </summary>
        /// <param name="pattern">Pattern regex</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateRegex(string pattern, OptionsValidationResult result)
        {
            try
            {
                _ = new Regex(pattern);
            }
            catch (Exception ex)
            {
                result.AddError(AppText.F("validation.invalidMatchPattern", ex.Message));
            }
        }

        /// <summary>
        /// Valida che sia configurata almeno una estensione video
        /// </summary>
        /// <param name="options">Opzioni da validare</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateExtensions(Options options, OptionsValidationResult result)
        {
            if (options.FileExtensions.Count == 0)
            {
                result.AddError(AppText.T("validation.extensionRequired"));
            }
        }

        /// <summary>
        /// Valida lingue target e filtri lingua
        /// </summary>
        /// <param name="options">Opzioni da validare</param>
        /// <param name="needsMerge">True se è richiesto merge da language</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateLanguages(Options options, bool needsMerge, OptionsValidationResult result)
        {
            if (needsMerge)
            {
                for (int i = 0; i < options.TargetLanguage.Count; i++)
                {
                    // Le lingue target sono obbligatorie solo quando il merge è effettivamente richiesto
                    ValidateLanguage(AppText.T("validation.labelTargetLanguage"), options.TargetLanguage[i], result);
                }
            }

            for (int i = 0; i < options.KeepSourceAudioLangs.Count; i++)
            {
                ValidateLanguage("keep-source-audio", options.KeepSourceAudioLangs[i], result);
            }

            for (int i = 0; i < options.KeepSourceSubtitleLangs.Count; i++)
            {
                ValidateLanguage("keep-source-subs", options.KeepSourceSubtitleLangs[i], result);
            }
        }

        /// <summary>
        /// Valida una singola lingua ISO 639
        /// </summary>
        /// <param name="label">Etichetta da usare negli errori</param>
        /// <param name="language">Codice lingua</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateLanguage(string label, string language, OptionsValidationResult result)
        {
            List<string> suggestions;
            if (language == null || !Regex.IsMatch(language.ToLowerInvariant(), @"^[a-z]{2,3}$"))
            {
                result.AddError(AppText.F("validation.languageInvalid", label, language));
                return;
            }

            if (!LanguageValidator.IsValid(language))
            {
                // Le suggestion restano warning per non nascondere l'errore principale
                result.AddError(AppText.F("validation.languageUnknown", label, language));
                suggestions = LanguageValidator.GetSimilar(language, 3);
                if (suggestions.Count > 0)
                {
                    result.AddWarning(AppText.F("validation.languageSuggestion", string.Join(", ", suggestions)));
                }
            }
        }

        /// <summary>
        /// Valida codec audio richiesti dall'utente
        /// </summary>
        /// <param name="options">Opzioni da validare</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateCodecs(Options options, OptionsValidationResult result)
        {
            for (int i = 0; i < options.AudioCodec.Count; i++)
            {
                if (CodecMapping.GetCodecPatterns(options.AudioCodec[i]) == null)
                {
                    result.AddError(AppText.F("validation.audioCodecUnknown", options.AudioCodec[i], CodecMapping.GetAllCodecNames()));
                }
            }

            for (int i = 0; i < options.KeepSourceAudioCodec.Count; i++)
            {
                if (CodecMapping.GetCodecPatterns(options.KeepSourceAudioCodec[i]) == null)
                {
                    result.AddError(AppText.F("validation.keepSourceAudioCodecUnknown", options.KeepSourceAudioCodec[i]));
                }
            }
        }

        /// <summary>
        /// Valida presenza ed esistenza delle cartelle operative
        /// </summary>
        /// <param name="options">Opzioni da validare</param>
        /// <param name="requireSourceFolder">True se source è obbligatoria</param>
        /// <param name="validateFolderExists">True se controllare esistenza su disco</param>
        /// <param name="needsMerge">True se la cartella language serve al merge</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateFolders(Options options, bool requireSourceFolder, bool validateFolderExists, bool needsMerge, OptionsValidationResult result)
        {
            bool sourceIsFile = !string.IsNullOrEmpty(options.SourceFolder) && File.Exists(options.SourceFolder);
            bool sourceIsFolder = !string.IsNullOrEmpty(options.SourceFolder) && Directory.Exists(options.SourceFolder);
            bool languageIsFile = !string.IsNullOrEmpty(options.LanguageFolder) && File.Exists(options.LanguageFolder);
            bool languageIsFolder = !string.IsNullOrEmpty(options.LanguageFolder) && Directory.Exists(options.LanguageFolder);

            if (requireSourceFolder && string.IsNullOrEmpty(options.SourceFolder))
            {
                result.AddError(AppText.T("validation.sourceRequired"));
            }

            if (sourceIsFile)
            {
                if ((needsMerge && !languageIsFile) || (!needsMerge && !string.IsNullOrEmpty(options.LanguageFolder)))
                {
                    result.AddError(AppText.T("validation.singleFilePairRequired"));
                }

                if (!IsAllowedFileExtension(options.SourceFolder, options.FileExtensions))
                {
                    result.AddError(AppText.F("validation.fileExtensionNotAllowed", options.SourceFolder));
                }

                if (languageIsFile && !IsAllowedFileExtension(options.LanguageFolder, options.FileExtensions))
                {
                    result.AddError(AppText.F("validation.fileExtensionNotAllowed", options.LanguageFolder));
                }
            }
            else
            {
                if (languageIsFile)
                {
                    result.AddError(AppText.T("validation.singleFilePairRequired"));
                }

                if (validateFolderExists && !string.IsNullOrEmpty(options.SourceFolder) && !sourceIsFolder)
                {
                    result.AddError(AppText.F("validation.sourceFolderNotFound", options.SourceFolder));
                }

                if (validateFolderExists && needsMerge && !string.IsNullOrEmpty(options.LanguageFolder) && !languageIsFolder && !languageIsFile)
                {
                    result.AddError(AppText.F("validation.languageFolderNotFound", options.LanguageFolder));
                }
            }
        }

        /// <summary>
        /// Verifica se il file usa una delle estensioni configurate
        /// </summary>
        /// <param name="filePath">Percorso file</param>
        /// <param name="extensions">Estensioni ammesse</param>
        /// <returns>True se l'estensione è ammessa</returns>
        private static bool IsAllowedFileExtension(string filePath, List<string> extensions)
        {
            string fileExtension = Path.GetExtension(filePath).TrimStart('.');
            for (int i = 0; i < extensions.Count; i++)
            {
                if (string.Equals(fileExtension, extensions[i].Trim().TrimStart('.'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Valida opzioni della modalità split
        /// </summary>
        /// <param name="options">Opzioni da validare</param>
        /// <param name="requireSourceFolder">True se source è obbligatorio</param>
        /// <param name="validateFolderExists">True se controllare esistenza su disco</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateSplitOptions(Options options, bool requireSourceFolder, bool validateFolderExists, OptionsValidationResult result)
        {
            int modes = 0;
            bool sourceIsFolder = false;
            bool sourceIsFile;

            ValidateExtensions(options, result);

            if (options.Split == null)
            {
                result.AddError(AppText.T("validation.invalidSplitConfig"));
                return;
            }

            if (requireSourceFolder && string.IsNullOrEmpty(options.Split.SourcePath))
            {
                result.AddError(AppText.T("validation.sourceRequired"));
            }

            if (!string.IsNullOrEmpty(options.Split.SourcePath))
            {
                sourceIsFile = File.Exists(options.Split.SourcePath);
                sourceIsFolder = Directory.Exists(options.Split.SourcePath);

                if (validateFolderExists && !sourceIsFile && !sourceIsFolder)
                {
                    result.AddError(AppText.F("validation.splitSourceNotFound", options.Split.SourcePath));
                }
            }

            if (!string.IsNullOrEmpty(options.Split.Pattern))
                modes++;
            if (!string.IsNullOrEmpty(options.Split.Ranges))
                modes++;
            if (!string.IsNullOrEmpty(options.Split.SplitAt))
                modes++;
            if (!string.IsNullOrEmpty(options.Split.TrimStart) || !string.IsNullOrEmpty(options.Split.TrimEnd))
                modes++;
            if (options.Split.ChaptersEach)
                modes++;

            if (modes == 0)
            {
                result.AddError(AppText.T("validation.splitModeRequired"));
            }
            else if (modes > 1)
            {
                result.AddError(AppText.T("validation.splitModesExclusive"));
            }

            if (sourceIsFolder && !string.IsNullOrEmpty(options.Split.SourceRaw))
            {
                result.AddError(AppText.T("validation.sourceRawSingleFileOnly"));
            }
        }

        /// <summary>
        /// Valida uno stretch factor manuale in forma decimale o frazione
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <returns>True se il valore è positivo e parsabile</returns>
        private static bool IsValidStretchFactor(string value)
        {
            return SpeedCorrectionService.TryParseStretchFactor(value, out _, out _);
        }

        #endregion
    }

    /// <summary>
    /// Risultato della validazione opzioni
    /// </summary>
    public class OptionsValidationResult
    {
        #region Costruttore

        /// <summary>
        /// Costruttore
        /// </summary>
        public OptionsValidationResult()
        {
            this.Errors = new List<string>();
            this.Warnings = new List<string>();
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Aggiunge un errore
        /// </summary>
        /// <param name="text">Testo errore</param>
        public void AddError(string text)
        {
            this.Errors.Add(text);
        }

        /// <summary>
        /// Aggiunge un warning
        /// </summary>
        /// <param name="text">Testo warning</param>
        public void AddWarning(string text)
        {
            this.Warnings.Add(text);
        }

        #endregion

        #region Proprietà

        /// <summary>
        /// True se non ci sono errori
        /// </summary>
        public bool IsValid
        {
            get
            {
                return this.Errors.Count == 0;
            }
        }

        /// <summary>
        /// Errori di validazione
        /// </summary>
        public List<string> Errors { get; private set; }

        /// <summary>
        /// Warning di validazione
        /// </summary>
        public List<string> Warnings { get; private set; }

        /// <summary>
        /// Messaggio errori aggregato
        /// </summary>
        public string ErrorMessage
        {
            get
            {
                return string.Join("\n", this.Errors);
            }
        }

        #endregion
    }
}
