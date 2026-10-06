using System.Collections.Generic;

namespace RemuxForge.Core.Models
{
    public enum RemuxMuxKind { Simple, DelayCorrection, DeepAnalysis }

    /// <summary>Whitelist riutilizzabile. Non contiene Options, percorsi, strumenti, record o ID traccia.</summary>
    public sealed class RemuxPreset
    {
        public string Name { get; set; } = "";
        public RemuxMuxKind MuxKind { get; set; }
        public bool FrameSync { get; set; }
        public bool Recursive { get; set; } = true;
        public string MatchPattern { get; set; } = @"S(\d+)E(\d+)";
        public List<string> FileExtensions { get; set; } = new List<string> { "mkv" };
        public List<string> TargetLanguage { get; set; } = new List<string>();
        public List<string> AudioCodec { get; set; } = new List<string>();
        public List<string> KeepSourceAudioLangs { get; set; } = new List<string>();
        public List<string> KeepSourceAudioCodec { get; set; } = new List<string>();
        public List<string> KeepSourceSubtitleLangs { get; set; } = new List<string>();
        public bool SubOnly { get; set; }
        public bool AudioOnly { get; set; }
        public string SpeedCorrectionMode { get; set; } = Options.SPEED_CORRECTION_OFF;
        public string ManualStretchFactor { get; set; } = "";
        public int AudioDelay { get; set; }
        public int SubtitleDelay { get; set; }
        public string AnalysisCropSourcePx { get; set; } = "";
        public string AnalysisCropLanguagePx { get; set; } = "";
        public bool SubtitleCanvasRewrite { get; set; }
        public string AudioFormat { get; set; } = "";
        public string AudioProcessingScope { get; set; } = "disabled";
        public bool AudioDownsample24To16 { get; set; }
        public bool AudioPeakNormalize { get; set; }
        public double AudioPeakTargetDb { get; set; } = -1;
        public bool AudioFixedGain { get; set; }
        public double AudioFixedGainDb { get; set; }
        public int AudioSourceFillThresholdMs { get; set; }
        public string AudioSourceFillLanguage { get; set; } = "";
        public bool AudioSourceFillStart { get; set; }
        public bool AudioSourceFillEnd { get; set; }
        public bool AudioSourceFillInsertSilence { get; set; }
        public double AudioSourceFillGainDb { get; set; }
        public string EncodingProfileName { get; set; } = "";
        public bool Overwrite { get; set; }

        public RemuxPreset Clone()
        {
            RemuxPreset copy = (RemuxPreset)this.MemberwiseClone();
            copy.FileExtensions = new List<string>(this.FileExtensions ?? new List<string>());
            copy.TargetLanguage = new List<string>(this.TargetLanguage ?? new List<string>());
            copy.AudioCodec = new List<string>(this.AudioCodec ?? new List<string>());
            copy.KeepSourceAudioLangs = new List<string>(this.KeepSourceAudioLangs ?? new List<string>());
            copy.KeepSourceAudioCodec = new List<string>(this.KeepSourceAudioCodec ?? new List<string>());
            copy.KeepSourceSubtitleLangs = new List<string>(this.KeepSourceSubtitleLangs ?? new List<string>());
            return copy;
        }
    }
}
