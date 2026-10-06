using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;

namespace RemuxForge.Core.Models
{
    /// <summary>Input esplicito del lavoro; null significa regole legacy, non una selezione vuota.</summary>
    public sealed class RemuxTrackSelection
    {
        public List<RemuxPairTrackSelection> Pairs { get; set; } = new List<RemuxPairTrackSelection>();

        public RemuxTrackSelection Clone()
        {
            return new RemuxTrackSelection { Pairs = this.Pairs.Select(pair => pair.Clone()).ToList() };
        }
    }

    /// <summary>Quattro insiemi ID riferiti a una coppia concreta, indipendenti dai risultati.</summary>
    public sealed class RemuxPairTrackSelection
    {
        public string SourceFilePath { get; set; } = "";
        public string LangFilePath { get; set; } = "";
        public HashSet<int> SourceAudioIds { get; set; } = new HashSet<int>();
        public HashSet<int> SourceSubIds { get; set; } = new HashSet<int>();
        public HashSet<int> LangAudioIds { get; set; } = new HashSet<int>();
        public HashSet<int> LangSubIds { get; set; } = new HashSet<int>();
        public string PairKey => CreatePairKey(this.SourceFilePath, this.LangFilePath);
        public bool HasLangTracks => this.LangAudioIds.Count > 0 || this.LangSubIds.Count > 0;

        public static string CreatePairKey(string source, string lang)
        {
            string sourcePath = Path.GetFullPath(source);
            string langPath = Path.GetFullPath(lang);
            return sourcePath.Length.ToString(CultureInfo.InvariantCulture) + ":" + sourcePath +
                langPath.Length.ToString(CultureInfo.InvariantCulture) + ":" + langPath;
        }

        public RemuxPairTrackSelection Clone()
        {
            return new RemuxPairTrackSelection
            {
                SourceFilePath = this.SourceFilePath, LangFilePath = this.LangFilePath,
                SourceAudioIds = new HashSet<int>(this.SourceAudioIds), SourceSubIds = new HashSet<int>(this.SourceSubIds),
                LangAudioIds = new HashSet<int>(this.LangAudioIds), LangSubIds = new HashSet<int>(this.LangSubIds)
            };
        }
    }
}
