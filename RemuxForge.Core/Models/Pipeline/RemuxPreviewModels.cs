using System;
using System.Collections.Generic;
using System.Linq;

namespace RemuxForge.Core.Models
{
    public enum RemuxTrackSide { Source, Lang }

    /// <summary>Richiesta detached: non richiede output, target o impostazioni audio.</summary>
    public sealed record RemuxPreviewRequest(string SourcePath, string LangPath, string MatchPattern,
        IReadOnlyList<string> FileExtensions, bool Recursive);

    public sealed record RemuxPreviewPair(string PairKey, string SourceFilePath, string LangFilePath,
        string EpisodeId, string SkipReason)
    {
        public bool IsMatched => !string.IsNullOrEmpty(this.LangFilePath);
    }

    public sealed record RemuxTrackMember(string PairKey, RemuxTrackSide Side, TrackInfo Track);

    public sealed record RemuxTrackGroup(string Key, RemuxTrackSide Side, string Type, string Language,
        string Codec, int Channels, bool DefaultTrack, bool ForcedTrack, string Title,
        IReadOnlyList<RemuxTrackMember> Members, int TotalPairs)
    {
        public int CoveredPairs => this.Members.Select(member => member.PairKey).Distinct(StringComparer.Ordinal).Count();
        public bool IsPartial => this.CoveredPairs != this.TotalPairs;
    }

    public sealed record RemuxPreviewProgress(int CompletedFiles, int TotalFiles);
    public sealed record RemuxPreviewError(string FilePath, string Message);

    /// <summary>Inventario privato della richiesta; nessun record della pipeline attiva viene esposto.</summary>
    public sealed record RemuxPreviewSnapshot(IReadOnlyList<RemuxPreviewPair> Pairs,
        IReadOnlyList<RemuxTrackGroup> Groups, IReadOnlyList<RemuxPreviewError> Errors)
    {
        public int MatchedPairs => this.Pairs.Count(pair => pair.IsMatched);
        public bool InventoryComplete => this.Errors.Count == 0 && this.MatchedPairs > 0;
    }
}
