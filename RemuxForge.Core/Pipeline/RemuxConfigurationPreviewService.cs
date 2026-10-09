using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Core.Pipeline
{
    /// <summary>Scanner e inventario isolati per richiesta; nessuna pipeline attiva o impostazione globale viene mutata.</summary>
    public sealed class RemuxConfigurationPreviewService
    {
        private readonly string _mkvMergePath;
        public RemuxConfigurationPreviewService(string mkvMergePath) { this._mkvMergePath = mkvMergePath; }

        public Task<RemuxPreviewSnapshot> PreviewAsync(RemuxPreviewRequest request,
            IProgress<RemuxPreviewProgress> progress = null, CancellationToken cancellationToken = default)
        {
            RemuxPreviewRequest detached = request with { FileExtensions = request.FileExtensions.ToArray() };
            return Task.Run(() => this.Preview(detached, progress, cancellationToken), cancellationToken);
        }

        private RemuxPreviewSnapshot Preview(RemuxPreviewRequest request, IProgress<RemuxPreviewProgress> progress, CancellationToken cancellation)
        {
            List<RemuxPreviewError> errors = new List<RemuxPreviewError>();
            List<RemuxPreviewPair> pairs = new List<RemuxPreviewPair>();
            List<RemuxTrackGroup> groups = new List<RemuxTrackGroup>();
            Dictionary<string, MkvFileInfo> cache = new Dictionary<string, MkvFileInfo>(StringComparer.Ordinal);
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if ((!File.Exists(request.SourcePath) && !Directory.Exists(request.SourcePath)) ||
                    (!File.Exists(request.LangPath) && !Directory.Exists(request.LangPath)))
                    throw new ArgumentException(AppText.T("remuxConfiguration.inputPathsUnavailable"));
                if (File.Exists(request.SourcePath) != File.Exists(request.LangPath))
                    throw new ArgumentException(AppText.T("remuxConfiguration.inputPairType"));
                if (request.FileExtensions.Count == 0) throw new ArgumentException(AppText.T("validation.extensionRequired"));
                if (File.Exists(request.SourcePath) && new[] { request.SourcePath, request.LangPath }.Any(file =>
                    !request.FileExtensions.Any(extension => string.Equals(Path.GetExtension(file).TrimStart('.'), extension.Trim().TrimStart('.'), StringComparison.OrdinalIgnoreCase))))
                    throw new ArgumentException(AppText.T("remuxConfiguration.inputExtensionNotAllowed"));
                if (!File.Exists(request.SourcePath)) _ = new Regex(request.MatchPattern);
                Options scanOptions = new Options(false)
                {
                    SourceFolder = Path.GetFullPath(request.SourcePath), LanguageFolder = Path.GetFullPath(request.LangPath),
                    MatchPattern = request.MatchPattern, FileExtensions = request.FileExtensions.ToList(), Recursive = request.Recursive
                };
                PipelineFileScanner scanner = new PipelineFileScanner((section, level, text) => { });
                foreach (FileProcessingRecord record in scanner.Scan(scanOptions, true, cancellation))
                {
                    pairs.Add(new RemuxPreviewPair(string.IsNullOrEmpty(record.LangFilePath) ? record.SourceFilePath :
                        RemuxPairTrackSelection.CreatePairKey(record.SourceFilePath, record.LangFilePath),
                        record.SourceFilePath, record.LangFilePath, record.EpisodeId, record.SkipReason));
                }
                cancellation.ThrowIfCancellationRequested();
                string[] files = pairs.Where(pair => pair.IsMatched).SelectMany(pair => new[] { pair.SourceFilePath, pair.LangFilePath })
                    .Distinct(StringComparer.Ordinal).ToArray();
                MkvToolsService tools = new MkvToolsService(this._mkvMergePath);
                progress?.Report(new RemuxPreviewProgress(0, files.Length));
                for (int index = 0; index < files.Length; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string message = "";
                    MkvFileInfo info = tools.GetFileInfoIsolated(files[index], 30000, cancellation, text => message = text);
                    if (info == null) errors.Add(new RemuxPreviewError(files[index], string.IsNullOrEmpty(message) ? AppText.T("remuxConfiguration.tracksReadFailed") : message));
                    else cache.Add(files[index], info);
                    progress?.Report(new RemuxPreviewProgress(index + 1, files.Length));
                }
                cancellation.ThrowIfCancellationRequested();
                // Non pubblicare coperture parziali come inventario definitivo.
                if (errors.Count == 0) groups.AddRange(BuildGroups(pairs, cache));
                cancellation.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { errors.Add(new RemuxPreviewError("", ex.Message)); }
            return new RemuxPreviewSnapshot(pairs.AsReadOnly(), groups.AsReadOnly(), errors.AsReadOnly());
        }

        public static string CreateGroupKey(RemuxTrackSide side, TrackInfo track)
        {
            string type = (track.Type ?? "").Trim().ToUpperInvariant();
            string language = CanonicalLanguage(track.Language);
            string title = Regex.Replace((track.Name ?? "").Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ").ToUpperInvariant();
            string[] values = { side.ToString(), type, language.ToUpperInvariant(), (track.Codec ?? "").Trim().ToUpperInvariant(),
                type == "AUDIO" ? track.Channels.ToString(CultureInfo.InvariantCulture) : "",
                track.DefaultTrack ? "1" : "0", track.ForcedTrack ? "1" : "0", title };
            return string.Concat(values.Select(value => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value));
        }

        private static string CanonicalLanguage(string language)
        {
            string value = string.IsNullOrWhiteSpace(language) ? "und" : language.Trim();
            return LanguageValidator.TryNormalizeToIso6392(value, out string normalized) ? normalized : value;
        }

        private static IEnumerable<RemuxTrackGroup> BuildGroups(List<RemuxPreviewPair> pairs, Dictionary<string, MkvFileInfo> cache)
        {
            List<RemuxTrackMember> members = new List<RemuxTrackMember>();
            foreach (RemuxPreviewPair pair in pairs.Where(pair => pair.IsMatched))
            {
                foreach (RemuxTrackSide side in new[] { RemuxTrackSide.Source, RemuxTrackSide.Lang })
                {
                    string file = side == RemuxTrackSide.Source ? pair.SourceFilePath : pair.LangFilePath;
                    foreach (TrackInfo track in cache[file].Tracks.Where(track =>
                        string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase) || string.Equals(track.Type, "subtitles", StringComparison.OrdinalIgnoreCase)))
                        members.Add(new RemuxTrackMember(pair.PairKey, side, track));
                }
            }
            int count = pairs.Count(pair => pair.IsMatched);
            foreach (var group in members.GroupBy(member => CreateGroupKey(member.Side, member.Track), StringComparer.Ordinal))
            {
                RemuxTrackMember first = group.First();
                TrackInfo track = first.Track;
                yield return new RemuxTrackGroup(group.Key, first.Side, track.Type, CanonicalLanguage(track.Language), track.Codec,
                    string.Equals(track.Type, "audio", StringComparison.OrdinalIgnoreCase) ? track.Channels : 0,
                    track.DefaultTrack, track.ForcedTrack, track.Name, group.ToList().AsReadOnly(), count);
            }
        }

        /// <summary>Valida tutte le coppie e gli ID rispetto allo snapshot corrente, prima di applicare il lavoro.</summary>
        public static OptionsValidationResult ValidateSelection(RemuxPreviewSnapshot snapshot, RemuxTrackSelection selection)
        {
            OptionsValidationResult result = new OptionsValidationResult();
            if (snapshot == null || !snapshot.InventoryComplete || selection?.Pairs == null)
            {
                result.AddError(AppText.T("remuxConfiguration.selectionInventoryUnavailable"));
                return result;
            }
            string[] matched = snapshot.Pairs.Where(pair => pair.IsMatched).Select(pair => pair.PairKey).ToArray();
            if (selection.Pairs.Any(pair => pair == null || pair.SourceAudioIds == null || pair.SourceSubIds == null || pair.LangAudioIds == null || pair.LangSubIds == null))
            {
                result.AddError(AppText.T("remuxConfiguration.selectionIncomplete"));
                return result;
            }
            try
            {
                if (selection.Pairs.Count != matched.Length || selection.Pairs.Select(pair => pair.PairKey).Distinct().Count() != matched.Length ||
                    !new HashSet<string>(matched, StringComparer.Ordinal).SetEquals(selection.Pairs.Select(pair => pair.PairKey)))
                {
                    result.AddError(AppText.T("remuxConfiguration.selectionPairMismatch"));
                    return result;
                }
                foreach (RemuxPairTrackSelection pair in selection.Pairs)
                {
                    foreach (var part in new[] { (RemuxTrackSide.Source, "audio", pair.SourceAudioIds), (RemuxTrackSide.Source, "subtitles", pair.SourceSubIds),
                        (RemuxTrackSide.Lang, "audio", pair.LangAudioIds), (RemuxTrackSide.Lang, "subtitles", pair.LangSubIds) })
                    {
                        int[] available = snapshot.Groups.Where(group => group.Side == part.Item1 && string.Equals(group.Type, part.Item2, StringComparison.OrdinalIgnoreCase))
                            .SelectMany(group => group.Members).Where(member => member.PairKey == pair.PairKey).Select(member => member.Track.Id).ToArray();
                        if (!part.Item3.IsSubsetOf(available)) result.AddError(AppText.F("remuxConfiguration.selectionIdsMissingPair", pair.SourceFilePath));
                    }
                }
                Dictionary<string, RemuxPairTrackSelection> byPair = selection.Pairs.ToDictionary(pair => pair.PairKey, StringComparer.Ordinal);
                foreach (RemuxTrackGroup group in snapshot.Groups)
                {
                    int selectedMembers = 0;
                    foreach (RemuxTrackMember member in group.Members)
                    {
                        RemuxPairTrackSelection pair = byPair[member.PairKey];
                        bool audio = string.Equals(group.Type, "audio", StringComparison.OrdinalIgnoreCase);
                        HashSet<int> ids = group.Side == RemuxTrackSide.Source ? (audio ? pair.SourceAudioIds : pair.SourceSubIds) :
                            (audio ? pair.LangAudioIds : pair.LangSubIds);
                        if (ids.Contains(member.Track.Id)) selectedMembers++;
                    }
                    if (selectedMembers > 0 && selectedMembers != group.Members.Count)
                        result.AddError(AppText.T("remuxConfiguration.selectionGroupUniform"));
                }
                if (!selection.Pairs.Any(pair => pair.HasLangTracks)) result.AddError(AppText.T("remuxConfiguration.noProcessablePairs"));
            }
            catch (ArgumentException ex) { result.AddError(AppText.F("remuxConfiguration.selectionError", ex.Message)); }
            return result;
        }
    }
}
