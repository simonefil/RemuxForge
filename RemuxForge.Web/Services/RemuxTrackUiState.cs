using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RemuxForge.Web.Services
{
    /// <summary>Presentazione del lavoro applicato: Source esclusi e copie Lang incluse, non gli ID del motore o i preset.</summary>
    public sealed class RemuxTrackUiState
    {
        public RemuxPreviewRequest Request { get; init; }
        public HashSet<string> ResultGroups { get; init; } = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> ExcludedGroups { get; init; } = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> KnownGroups { get; init; } = new HashSet<string>(StringComparer.Ordinal);

        public RemuxTrackUiState Clone() => new RemuxTrackUiState
        {
            Request = this.Request with { FileExtensions = this.Request.FileExtensions.ToArray() },
            ResultGroups = new HashSet<string>(this.ResultGroups, StringComparer.Ordinal),
            ExcludedGroups = new HashSet<string>(this.ExcludedGroups, StringComparer.Ordinal),
            KnownGroups = new HashSet<string>(this.KnownGroups, StringComparer.Ordinal)
        };

        public bool Matches(RemuxPreviewRequest request) => SameInputs(this.Request, request);

        public static bool SameInputs(RemuxPreviewRequest left, RemuxPreviewRequest right)
        {
            static string FullPath(string path)
            {
                if (string.IsNullOrWhiteSpace(path)) return "";
                try { return Path.GetFullPath(path.Trim().Trim('"')); }
                catch (ArgumentException) { return path; }
            }
            return left != null && right != null && FullPath(left.SourcePath) == FullPath(right.SourcePath) &&
                FullPath(left.LangPath) == FullPath(right.LangPath) && left.MatchPattern == right.MatchPattern &&
                left.Recursive == right.Recursive && left.FileExtensions.SequenceEqual(right.FileExtensions);
        }
    }
}
