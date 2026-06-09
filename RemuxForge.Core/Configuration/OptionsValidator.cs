using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// <param name="requireSourceFolder">True se la sorgente e' obbligatoria</param>
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

            if (options.Mode != Options.MODE_REMUX && options.Mode != Options.MODE_SPLIT)
            {
                result.AddError(AppText.T("validation.missingInvalidMode"));
                return result;
            }

            if (options.Mode == Options.MODE_SPLIT)
            {
                ValidateSplitOptions(options, requireSourceFolder, validateFolderExists, result);
                return result;
            }

            needsMerge = options.TargetLanguage.Count > 0;
            needsFilter = options.KeepSourceAudioLangs.Count > 0 || options.KeepSourceAudioCodec.Count > 0 || options.KeepSourceSubtitleLangs.Count > 0;
            needsRemux = needsMerge || needsFilter || options.AudioFormat.Length > 0 || options.AudioRenameScope != "disabled";
            needsEncode = options.EncodingProfileName.Length > 0;

            if (options.FrameSync && options.DeepAnalysis)
            {
                result.AddError(AppText.T("validation.frameSyncDeepExclusive"));
            }

            if (options.SubOnly && options.AudioOnly)
            {
                result.AddError(AppText.T("validation.subOnlyAudioOnlyExclusive"));
            }

            if (options.Overwrite && options.DestinationFolder.Length > 0)
            {
                result.AddError(AppText.T("validation.overwriteDestinationExclusive"));
            }

            ValidateSpeedCorrection(options, result);
            ValidateAudioProcessing(options, result);
            ValidateAudioSourceFill(options, needsMerge, result);
            ValidateAnalysisCrop(options, result);
            ValidateRegex(options.MatchPattern, result);
            ValidateExtensions(options, result);
            ValidateLanguages(options, needsMerge, result);
            ValidateCodecs(options, result);
            ValidateFolders(options, requireSourceFolder, validateFolderExists, needsMerge, result);

            if (requireSourceFolder && !needsRemux && !needsEncode)
            {
                result.AddError(AppText.T("validation.noOperation"));
            }

            if (requireSourceFolder && !options.Overwrite && options.DestinationFolder.Length == 0 && !(needsEncode && !needsRemux))
            {
                result.AddError(AppText.T("validation.destinationOrOverwrite"));
            }

            return result;
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Valida modalita' e parametro manuale della speed correction
        /// </summary>
        /// <param name="options">Opzioni da validare</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateSpeedCorrection(Options options, OptionsValidationResult result)
        {
            if (options.SpeedCorrectionMode != Options.SPEED_CORRECTION_OFF &&
                options.SpeedCorrectionMode != Options.SPEED_CORRECTION_AUTO &&
                options.SpeedCorrectionMode != Options.SPEED_CORRECTION_MANUAL)
            {
                result.AddError(AppText.F("options.invalidSpeedCorrection", options.SpeedCorrectionMode));
                return;
            }

            if (options.SpeedCorrectionMode == Options.SPEED_CORRECTION_MANUAL)
            {
                // In manuale lo stretch deve essere esplicito: non si tenta inferenza automatica su VFR
                if (options.ManualStretchFactor.Trim().Length == 0)
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
        private static void ValidateAudioSourceFill(Options options, bool needsMerge, OptionsValidationResult result)
        {
            bool anyMode = options.AudioSourceFillStart || options.AudioSourceFillEnd || options.AudioSourceFillInsertSilence;
            bool active = anyMode || options.AudioSourceFillThresholdMs > 0 || options.AudioSourceFillLanguage.Length > 0;

            if (options.AudioSourceFillThresholdMs < 0)
            {
                result.AddError(AppText.T("validation.sourceFillThresholdNegative"));
            }

            if (active && !needsMerge)
            {
                result.AddError(AppText.T("validation.sourceFillNeedsTargetLanguage"));
            }

            if (active && (options.AudioFormat.Length == 0 || options.AudioProcessingScope == "disabled"))
            {
                result.AddError(AppText.T("validation.sourceFillNeedsAudio"));
            }

            if (active && options.AudioSourceFillThresholdMs <= 0)
            {
                result.AddError(AppText.T("validation.sourceFillThresholdPositive"));
            }

            if (active && options.AudioSourceFillLanguage.Length == 0)
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

            if (options.AudioSourceFillLanguage.Length > 0)
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

            if (!IsValidScope(options.AudioRenameScope))
            {
                result.AddError(AppText.F("options.invalidAudioRenameScope", options.AudioRenameScope));
            }

            if (options.AudioFormat.Length == 0 && options.AudioProcessingScope != "disabled")
            {
                result.AddError(AppText.T("validation.audioScopeRequiresFormat"));
            }

            if (options.AudioProcessingScope != "disabled" && options.AudioFormat.Length == 0)
            {
                result.AddError(AppText.T("validation.audioFormatRequiredWithScope"));
            }

            if ((options.AudioPeakNormalize || options.AudioDownsample24To16) && (options.AudioFormat.Length == 0 || options.AudioProcessingScope == "disabled"))
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
        /// Verifica se un formato audio e' valido
        /// </summary>
        private static bool IsValidAudioFormat(string value)
        {
            return value == "" || value == "flac" || value == "lpcm" || value == "aac" || value == "opus";
        }

        /// <summary>
        /// Verifica se uno scope audio e' valido
        /// </summary>
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
        /// <param name="needsMerge">True se e' richiesto merge da language</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateLanguages(Options options, bool needsMerge, OptionsValidationResult result)
        {
            if (needsMerge)
            {
                for (int i = 0; i < options.TargetLanguage.Count; i++)
                {
                    // Le lingue target sono obbligatorie solo quando il merge e' effettivamente richiesto
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
        /// <param name="requireSourceFolder">True se source e' obbligatoria</param>
        /// <param name="validateFolderExists">True se controllare esistenza su disco</param>
        /// <param name="needsMerge">True se la cartella language serve al merge</param>
        /// <param name="result">Risultato validazione da aggiornare</param>
        private static void ValidateFolders(Options options, bool requireSourceFolder, bool validateFolderExists, bool needsMerge, OptionsValidationResult result)
        {
            if (requireSourceFolder && options.SourceFolder.Length == 0)
            {
                result.AddError(AppText.T("validation.sourceRequired"));
            }

            if (validateFolderExists && options.SourceFolder.Length > 0 && !Directory.Exists(options.SourceFolder))
            {
                result.AddError(AppText.F("validation.sourceFolderNotFound", options.SourceFolder));
            }

            if (validateFolderExists && needsMerge && options.LanguageFolder.Length > 0 && !Directory.Exists(options.LanguageFolder))
            {
                result.AddError(AppText.F("validation.languageFolderNotFound", options.LanguageFolder));
            }
        }

        /// <summary>
        /// Valida opzioni della modalita' split
        /// </summary>
        /// <param name="options">Opzioni da validare</param>
        /// <param name="requireSourceFolder">True se source e' obbligatorio</param>
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

            if (requireSourceFolder && options.Split.SourcePath.Length == 0)
            {
                result.AddError(AppText.T("validation.sourceRequired"));
            }

            if (options.Split.SourcePath.Length > 0)
            {
                sourceIsFile = File.Exists(options.Split.SourcePath);
                sourceIsFolder = Directory.Exists(options.Split.SourcePath);

                if (validateFolderExists && !sourceIsFile && !sourceIsFolder)
                {
                    result.AddError(AppText.F("validation.splitSourceNotFound", options.Split.SourcePath));
                }
            }

            if (options.Split.Pattern.Length > 0) { modes++; }
            if (options.Split.Ranges.Length > 0) { modes++; }
            if (options.Split.SplitAt.Length > 0) { modes++; }
            if (options.Split.TrimStart.Length > 0 || options.Split.TrimEnd.Length > 0) { modes++; }
            if (options.Split.ChaptersEach) { modes++; }

            if (modes == 0)
            {
                result.AddError(AppText.T("validation.splitModeRequired"));
            }
            else if (modes > 1)
            {
                result.AddError(AppText.T("validation.splitModesExclusive"));
            }

            if (sourceIsFolder && options.Split.SourceRaw.Length > 0)
            {
                result.AddError(AppText.T("validation.sourceRawSingleFileOnly"));
            }
        }

        /// <summary>
        /// Valida uno stretch factor manuale in forma decimale o frazione
        /// </summary>
        /// <param name="value">Valore testuale</param>
        /// <returns>True se il valore e' positivo e parsabile</returns>
        private static bool IsValidStretchFactor(string value)
        {
            bool result = false;
            string trimmed = value != null ? value.Trim() : "";
            string[] parts;
            double numerator;
            double denominator;
            double parsed;
            if (trimmed.Contains("/"))
            {
                // Supporta forma frazionaria tipo 24000/25025 per casi PAL/NTSC precisi
                parts = trimmed.Split('/');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out numerator) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator) &&
                    numerator > 0.0 &&
                    denominator > 0.0)
                {
                    result = true;
                }
            }
            else if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && parsed > 0.0)
            {
                result = true;
            }

            return result;
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
        public void AddError(string text)
        {
            this.Errors.Add(text);
        }

        /// <summary>
        /// Aggiunge un warning
        /// </summary>
        public void AddWarning(string text)
        {
            this.Warnings.Add(text);
        }

        #endregion

        #region Proprieta

        /// <summary>
        /// True se non ci sono errori
        /// </summary>
        public bool IsValid
        {
            get { return this.Errors.Count == 0; }
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
            get { return string.Join("\n", this.Errors); }
        }

        #endregion
    }
}
