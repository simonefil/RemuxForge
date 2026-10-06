using RemuxForge.Core.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RemuxForge.Web.Services
{
    /// <summary>Presentazione dei codec: equivalenze e match restano quelli del catalogo Core.</summary>
    public static class RemuxCodecCatalog
    {
        public sealed record CodecOption(string Code, string Label, string SearchText);
        private sealed record Entry(CodecOption Option, HashSet<string> Patterns);

        // Chiavi già accettate da Core, scelte esplicitamente anziché dal primo alias enumerato.
        private static readonly Dictionary<string, string> PreferredNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ac3", "AC-3" }, { "eac3", "E-AC-3" }, { "truehd", "TrueHD" },
            { "atmos", "Dolby Atmos" }, { "mlp", "MLP" }, { "dts", "DTS" },
            { "dts-hd", "DTS-HD" }, { "dts-hd ma", "DTS-HD Master Audio" },
            { "dts-hd hr", "DTS-HD High Resolution" }, { "dts-es", "DTS-ES" },
            { "dts:x", "DTS:X" }, { "flac", "FLAC" }, { "pcm", "PCM" },
            { "alac", "ALAC" }, { "aac", "AAC" }, { "mp3", "MP3" },
            { "mp2", "MP2" }, { "opus", "Opus" }, { "vorbis", "Vorbis" }
        };

        private static readonly Entry[] Entries = BuildEntries();
        public static IReadOnlyList<CodecOption> Options { get; } = Array.AsReadOnly(Entries.Select(e => e.Option).ToArray());

        private static Entry[] BuildEntries()
        {
            return CodecMapping.GetAllCodecNames().Split(',').Select(c => c.Trim())
                .GroupBy(c => string.Join("\n", CodecMapping.GetCodecPatterns(c)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .Select(p => p.ToUpperInvariant())), StringComparer.Ordinal)
                .Select(group =>
                {
                    string[] aliases = group.ToArray();
                    string code = PreferredNames.Keys.FirstOrDefault(c => aliases.Contains(c, StringComparer.OrdinalIgnoreCase))
                        ?? aliases.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).First().ToLowerInvariant();
                    string label = PreferredNames.TryGetValue(code, out string name) ? name : code;
                    return new Entry(new CodecOption(code, label, label + " " + string.Join(" ", aliases)),
                        new HashSet<string>(CodecMapping.GetCodecPatterns(code), StringComparer.OrdinalIgnoreCase));
                }).ToArray();
        }

        public static string Canonical(string value)
        {
            string[] patterns = CodecMapping.GetCodecPatterns(value ?? "");
            return patterns == null ? value : Entries.FirstOrDefault(e => e.Patterns.SetEquals(patterns))?.Option.Code ?? value;
        }

        public static List<string> Normalize(IEnumerable<string> values) => (values ?? Array.Empty<string>())
            .Select(Canonical).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public static bool MatchesSearch(CodecOption option, string query)
        {
            string text = query?.Trim() ?? "";
            // Un alias completo (anche fuzzy per Core) identifica un solo filtro, non un substring.
            string[] patterns = CodecMapping.GetCodecPatterns(text);
            if (patterns != null)
            {
                string code = Entries.FirstOrDefault(e => e.Patterns.SetEquals(patterns))?.Option.Code;
                return string.Equals(option.Code, code, StringComparison.OrdinalIgnoreCase);
            }
            return option.SearchText.Contains(text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
