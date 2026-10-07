using RemuxForge.Core.Configuration;
using RemuxForge.Core.Models;
using System.Collections.Generic;

namespace RemuxForge.Web.Services
{
    /// <summary>Mapping esplicito della whitelist, mai serializzazione di Options nel preset.</summary>
    public static class RemuxPresetUiHelper
    {
        public static RemuxPreset Capture(Options o, RemuxMuxKind kind, string name = "")
        {
            return new RemuxPreset
            {
                Name = name, MuxKind = kind, FrameSync = o.FrameSync,
                Recursive = o.Recursive, MatchPattern = o.MatchPattern, FileExtensions = new List<string>(o.FileExtensions),
                TargetLanguage = new List<string>(o.TargetLanguage), AudioCodec = new List<string>(o.AudioCodec),
                KeepSourceAudioLangs = new List<string>(o.KeepSourceAudioLangs), KeepSourceAudioCodec = new List<string>(o.KeepSourceAudioCodec),
                KeepSourceSubtitleLangs = new List<string>(o.KeepSourceSubtitleLangs), SubOnly = o.SubOnly, AudioOnly = o.AudioOnly,
                SpeedCorrectionMode = o.SpeedCorrectionMode, ManualStretchFactor = o.ManualStretchFactor,
                AudioDelay = o.AudioDelay, SubtitleDelay = o.SubtitleDelay,
                AnalysisCropSourcePx = o.AnalysisCropSourcePx, AnalysisCropLanguagePx = o.AnalysisCropLanguagePx,
                SubtitleCanvasRewrite = o.SubtitleCanvasRewrite, CopyLangChapters = o.CopyLangChapters, AudioFormat = o.AudioFormat, AudioProcessingScope = o.AudioProcessingScope,
                AudioDownsample24To16 = o.AudioDownsample24To16, AudioPeakNormalize = o.AudioPeakNormalize, AudioPeakTargetDb = o.AudioPeakTargetDb,
                AudioFixedGain = o.AudioFixedGain, AudioFixedGainDb = o.AudioFixedGainDb,
                AudioSourceFillThresholdMs = o.AudioSourceFillThresholdMs, AudioSourceFillLanguage = o.AudioSourceFillLanguage,
                AudioSourceFillStart = o.AudioSourceFillStart, AudioSourceFillEnd = o.AudioSourceFillEnd,
                AudioSourceFillInsertSilence = o.AudioSourceFillInsertSilence, AudioSourceFillGainDb = o.AudioSourceFillGainDb,
                EncodingProfileName = o.EncodingProfileName, Overwrite = o.Overwrite
            };
        }

        public static Options Restore(RemuxPreset preset)
        {
            RemuxPreset p = preset.Clone();
            return new Options
            {
                Mode = Options.MODE_REMUX, FrameSync = p.FrameSync, DeepAnalysis = p.MuxKind == RemuxMuxKind.DeepAnalysis,
                Recursive = p.Recursive, MatchPattern = p.MatchPattern ?? "", FileExtensions = p.FileExtensions,
                TargetLanguage = p.TargetLanguage, AudioCodec = p.AudioCodec, KeepSourceAudioLangs = p.KeepSourceAudioLangs,
                KeepSourceAudioCodec = p.KeepSourceAudioCodec, KeepSourceSubtitleLangs = p.KeepSourceSubtitleLangs,
                SubOnly = p.SubOnly, AudioOnly = p.AudioOnly, SpeedCorrectionMode = p.SpeedCorrectionMode ?? "",
                ManualStretchFactor = p.ManualStretchFactor ?? "", AudioDelay = p.AudioDelay, SubtitleDelay = p.SubtitleDelay,
                AnalysisCropSourcePx = p.AnalysisCropSourcePx ?? "", AnalysisCropLanguagePx = p.AnalysisCropLanguagePx ?? "",
                SubtitleCanvasRewrite = p.SubtitleCanvasRewrite, CopyLangChapters = p.CopyLangChapters, AudioFormat = p.AudioFormat ?? "", AudioProcessingScope = p.AudioProcessingScope ?? "",
                AudioDownsample24To16 = p.AudioDownsample24To16, AudioPeakNormalize = p.AudioPeakNormalize, AudioPeakTargetDb = p.AudioPeakTargetDb,
                AudioFixedGain = p.AudioFixedGain, AudioFixedGainDb = p.AudioFixedGainDb,
                AudioSourceFillThresholdMs = p.AudioSourceFillThresholdMs, AudioSourceFillLanguage = p.AudioSourceFillLanguage ?? "",
                AudioSourceFillStart = p.AudioSourceFillStart, AudioSourceFillEnd = p.AudioSourceFillEnd,
                AudioSourceFillInsertSilence = p.AudioSourceFillInsertSilence, AudioSourceFillGainDb = p.AudioSourceFillGainDb,
                EncodingProfileName = p.EncodingProfileName ?? "", Overwrite = p.Overwrite
            };
        }

        public static Options CloneOptions(Options options, RemuxMuxKind kind)
        {
            Options copy = Restore(Capture(options, kind));
            copy.SourceFolder = options.SourceFolder;
            copy.LanguageFolder = options.LanguageFolder;
            copy.DestinationFolder = options.DestinationFolder;
            copy.MkvMergePath = options.MkvMergePath;
            copy.FrameSyncDiagnostics = options.FrameSyncDiagnostics;
            copy.DeepAnalysisDiagnostics = options.DeepAnalysisDiagnostics;
            copy.DryRun = options.DryRun;
            copy.ExplicitTrackSelection = options.ExplicitTrackSelection?.Clone();
            copy.SkipPairsWithoutSelectedLangTracks = options.SkipPairsWithoutSelectedLangTracks;
            return copy;
        }
    }
}
