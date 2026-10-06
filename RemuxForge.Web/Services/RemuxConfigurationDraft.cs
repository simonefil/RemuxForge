using RemuxForge.Core.Configuration;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media.Mkv;
using RemuxForge.Core.Models;
using RemuxForge.Core.Pipeline;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace RemuxForge.Web.Services
{
    /// <summary>Stato di editing privato al wizard; gli insiemi gruppo sono presentazione, non filtri del motore.</summary>
    public sealed class RemuxConfigurationDraft
    {
        public Options Options { get; private set; }
        public RemuxMuxKind MuxKind { get; private set; }
        public string PresetName { get; private set; } = "";
        private RemuxPreset _savedPreset;
        public bool Advanced { get; private set; }
        public RemuxPreviewSnapshot Snapshot { get; private set; }
        public RemuxPreviewRequest SnapshotRequest { get; private set; }
        public long Revision { get; private set; }
        public HashSet<string> ResultGroups { get; } = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> ExcludedGroups { get; } = new HashSet<string>(StringComparer.Ordinal);
        private RemuxPreviewRequest _choicesRequest;
        private RemuxPreviewRequest _inputsRequest;
        private HashSet<string> _knownGroups = new HashSet<string>(StringComparer.Ordinal);

        public RemuxConfigurationDraft(Options options, RemuxMuxKind kind, string presetName, RemuxTrackUiState trackUiState = null)
        {
            this.MuxKind = kind;
            this.Options = RemuxPresetUiHelper.CloneOptions(options ?? new Options(), kind);
            this.Options.Mode = Core.Models.Options.MODE_REMUX;
            this.Advanced = this.Options.ExplicitTrackSelection != null;
            this.PresetName = presetName ?? "";
            this._savedPreset = AppSettingsService.Instance.GetRemuxPresets().FirstOrDefault(p => p.Name == this.PresetName);
            this.NormalizeLanguages();
            this.ResetIncompatibleSynchronization();
            this._inputsRequest = this.PreviewRequest();
            if (this.Advanced && trackUiState != null && trackUiState.Matches(this.PreviewRequest()))
            {
                RemuxTrackUiState copy = trackUiState.Clone();
                this._choicesRequest = copy.Request;
                this.ResultGroups.UnionWith(copy.ResultGroups);
                this.ExcludedGroups.UnionWith(copy.ExcludedGroups);
                this._knownGroups = copy.KnownGroups;
            }
        }

        public bool Modified => this.Advanced || (this._savedPreset != null &&
            JsonSerializer.Serialize(this._savedPreset) != JsonSerializer.Serialize(this.CapturePreset(this.PresetName)));

        public RemuxPreset CapturePreset(string name) => RemuxPresetUiHelper.Capture(this.Options, this.MuxKind, name);

        public void MarkPresetSaved(RemuxPreset preset)
        {
            this.PresetName = preset.Name;
            this._savedPreset = preset.Clone();
        }

        public void LoadPreset(RemuxPreset preset)
        {
            string source = this.Options.SourceFolder, lang = this.Options.LanguageFolder, destination = this.Options.DestinationFolder;
            this.Options = RemuxPresetUiHelper.Restore(preset);
            this.Options.SourceFolder = source;
            this.Options.LanguageFolder = lang;
            this.Options.DestinationFolder = destination;
            this.SetMuxKind(preset.MuxKind); // Azzera i campi incompatibili dopo la conferma del caricamento.
            this.NormalizeLanguages();
            this.Advanced = false;
            this.InvalidatePreview();
            this.MarkPresetSaved(preset);
        }

        private void NormalizeLanguages()
        {
            static string Normalize(string value) => LanguageValidator.TryNormalizeToIso6392(value, out string code) ? code : value;
            this.Options.TargetLanguage = this.Options.TargetLanguage.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            this.Options.KeepSourceAudioLangs = this.Options.KeepSourceAudioLangs.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            this.Options.KeepSourceSubtitleLangs = this.Options.KeepSourceSubtitleLangs.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!string.IsNullOrEmpty(this.Options.AudioSourceFillLanguage)) this.Options.AudioSourceFillLanguage = Normalize(this.Options.AudioSourceFillLanguage);
        }

        public void SetMuxKind(RemuxMuxKind kind)
        {
            this.MuxKind = kind;
            this.Options.DeepAnalysis = kind == RemuxMuxKind.DeepAnalysis;
            if (kind != RemuxMuxKind.DelayCorrection) this.Options.FrameSync = false;
            this.ResetIncompatibleSynchronization();
        }

        public void SetFrameSync(bool enabled)
        {
            this.Options.FrameSync = this.MuxKind == RemuxMuxKind.DelayCorrection && enabled;
            this.ResetIncompatibleSynchronization();
        }

        private void ResetIncompatibleSynchronization()
        {
            if (this.Options.FrameSync || this.Options.DeepAnalysis)
            {
                this.Options.AudioDelay = 0;
                this.Options.SubtitleDelay = 0;
            }
            else
            {
                this.Options.AnalysisCropSourcePx = "";
                this.Options.AnalysisCropLanguagePx = "";
                this.Options.SubtitleCanvasRewrite = false;
            }
        }

        public void SetAdvanced(bool value)
        {
            if (this.Advanced == value) return;
            this.Advanced = value;
            this.Options.TargetLanguage.Clear();
            this.Options.AudioCodec.Clear();
            this.Options.KeepSourceAudioLangs.Clear();
            this.Options.KeepSourceAudioCodec.Clear();
            this.Options.KeepSourceSubtitleLangs.Clear();
            this.Options.SubOnly = false;
            this.Options.AudioOnly = false;
            this.Options.ExplicitTrackSelection = null;
            this.ResetGroupChoices();
            this._choicesRequest = this.HasCurrentSnapshot ? this.SnapshotRequest with { FileExtensions = this.SnapshotRequest.FileExtensions.ToArray() } : null;
            this._knownGroups = this.HasCurrentSnapshot ? new HashSet<string>(this.Snapshot.Groups.Select(g => g.Key), StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        }

        public RemuxPreviewRequest PreviewRequest() => new RemuxPreviewRequest(this.Options.SourceFolder,
            string.IsNullOrWhiteSpace(this.Options.LanguageFolder) ? this.Options.SourceFolder : this.Options.LanguageFolder,
            this.Options.MatchPattern, this.Options.FileExtensions.ToArray(), this.Options.Recursive);

        public bool HasCurrentSnapshot => this.Snapshot != null && this.SnapshotRequest != null &&
            SameRequest(this.SnapshotRequest, this.PreviewRequest());
        public bool HasMatching => this.HasCurrentSnapshot && this.Snapshot.MatchedPairs > 0;

        public static bool SameRequest(RemuxPreviewRequest left, RemuxPreviewRequest right) =>
            left.SourcePath == right.SourcePath && left.LangPath == right.LangPath && left.MatchPattern == right.MatchPattern &&
            left.Recursive == right.Recursive && left.FileExtensions.SequenceEqual(right.FileExtensions);

        public void InvalidatePreview()
        {
            this.Revision++;
            this.Snapshot = null;
            this.SnapshotRequest = null;
            this.Options.ExplicitTrackSelection = null;
            this.ResultGroups.Clear();
            this.ExcludedGroups.Clear();
            this._choicesRequest = null;
            this._knownGroups.Clear();
            this._inputsRequest = this.PreviewRequest();
        }

        public long BeginPreview()
        {
            // Conserva le scelte correnti, non il vecchio contratto motore. Cambi input le invalidano.
            if (this._inputsRequest != null && !RemuxTrackUiState.SameInputs(this._inputsRequest, this.PreviewRequest())) this.InvalidatePreview();
            this._inputsRequest = this.PreviewRequest();
            this.Revision++;
            this.Snapshot = null;
            this.SnapshotRequest = null;
            return this.Revision;
        }

        public bool AcceptPreview(long revision, RemuxPreviewRequest request, RemuxPreviewSnapshot snapshot)
        {
            if (revision != this.Revision || !SameRequest(request, this.PreviewRequest())) return false;
            this.Snapshot = snapshot;
            this.SnapshotRequest = request;
            if (!snapshot.InventoryComplete) return true; // Gli errori non cancellano le scelte prima di un Riprova.
            HashSet<string> available = new HashSet<string>(snapshot.Groups.Select(group => group.Key), StringComparer.Ordinal);
            if (this.Advanced && this._choicesRequest != null && RemuxTrackUiState.SameInputs(this._choicesRequest, request))
            {
                this.ResultGroups.IntersectWith(available);
                this.ExcludedGroups.IntersectWith(this.ResultGroups);
                foreach (RemuxTrackGroup group in snapshot.Groups.Where(group => group.Side == RemuxTrackSide.Source && !this._knownGroups.Contains(group.Key)))
                    this.ResultGroups.Add(group.Key);
            }
            else
            {
                this.ResetGroupChoices();
                // Solo la prima apertura senza presentazione Web recupera gli ID inclusi dal contratto runtime.
                if (this.Advanced && this.Options.ExplicitTrackSelection != null)
                {
                    foreach (RemuxTrackGroup group in snapshot.Groups)
                    {
                        bool selected = group.Members.All(member =>
                        {
                            RemuxPairTrackSelection pair = this.Options.ExplicitTrackSelection.Pairs.FirstOrDefault(p => p.PairKey == member.PairKey);
                            return pair != null && Ids(pair, group).Contains(member.Track.Id);
                        });
                        if (selected) this.ResultGroups.Add(group.Key);
                        else if (group.Side == RemuxTrackSide.Source) this.ExcludedGroups.Add(group.Key);
                    }
                }
            }
            this._choicesRequest = request with { FileExtensions = request.FileExtensions.ToArray() };
            this._knownGroups = available;
            // Old Web state could contain struck-out Lang copies. They now mean deletion.
            foreach (RemuxTrackGroup group in snapshot.Groups.Where(g => g.Side == RemuxTrackSide.Lang && this.ExcludedGroups.Contains(g.Key)))
            {
                this.ResultGroups.Remove(group.Key);
                this.ExcludedGroups.Remove(group.Key);
            }
            return true;
        }

        public RemuxTrackUiState CaptureTrackUiState() => !this.Advanced || !this.HasCurrentSnapshot || !this.Snapshot.InventoryComplete ? null :
            new RemuxTrackUiState
            {
                Request = this.SnapshotRequest with { FileExtensions = this.SnapshotRequest.FileExtensions.ToArray() },
                ResultGroups = new HashSet<string>(this.ResultGroups, StringComparer.Ordinal),
                ExcludedGroups = new HashSet<string>(this.ExcludedGroups, StringComparer.Ordinal),
                KnownGroups = new HashSet<string>(this._knownGroups, StringComparer.Ordinal)
            };

        private void ResetGroupChoices()
        {
            this.ResultGroups.Clear();
            this.ExcludedGroups.Clear();
            if (this.Snapshot != null)
                foreach (RemuxTrackGroup group in this.Snapshot.Groups.Where(g => g.Side == RemuxTrackSide.Source)) this.ResultGroups.Add(group.Key);
        }

        public void CopyGroup(RemuxTrackGroup group)
        {
            if (!this.CanChangeGroup(group) || group.Side != RemuxTrackSide.Lang) return;
            this.ResultGroups.Add(group.Key);
            this.ExcludedGroups.Remove(group.Key);
        }

        public void ToggleGroup(RemuxTrackGroup group)
        {
            if (!this.CanChangeGroup(group) || !this.ResultGroups.Contains(group.Key)) return;
            if (group.Side == RemuxTrackSide.Lang)
            {
                this.ResultGroups.Remove(group.Key);
                this.ExcludedGroups.Remove(group.Key);
                return;
            }
            if (!this.ExcludedGroups.Add(group.Key)) this.ExcludedGroups.Remove(group.Key);
        }

        private bool CanChangeGroup(RemuxTrackGroup group) => group != null && this.Advanced && this.HasCurrentSnapshot &&
            this.Snapshot.InventoryComplete && this.Snapshot.Groups.Any(g => ReferenceEquals(g, group));

        public bool HasSelectedLangAudio()
        {
            if (this.Advanced) return this.BuildSelection().Pairs.Any(p => p.LangAudioIds.Count > 0);
            if (!this.HasCurrentSnapshot || !this.Snapshot.InventoryComplete) return false;
            MkvToolsService tools = new MkvToolsService(this.Options.MkvMergePath);
            string[] codecs = this.Options.AudioCodec.SelectMany(c => CodecMapping.GetCodecPatterns(c) ?? Array.Empty<string>()).ToArray();
            foreach (RemuxPreviewPair pair in this.Snapshot.Pairs.Where(p => p.IsMatched))
            {
                List<TrackInfo> tracks = this.Snapshot.Groups.Where(g => g.Side == RemuxTrackSide.Lang).SelectMany(g => g.Members)
                    .Where(m => m.PairKey == pair.PairKey).Select(m => m.Track).ToList();
                PipelineTrackSelectionResolver.ResolveLanguage(new FileProcessingRecord(), this.Options, tracks, tools,
                    codecs.Length == 0 ? null : codecs, out List<TrackInfo> audio, out _);
                if (audio.Count > 0) return true;
            }
            return false;
        }

        // Wizard policy: Deep always requires rendering, even before inventory or with subtitle-only selection.
        public bool RequiresTimelineAudioProcessing => this.Options.DeepAnalysis ||
            (this.Options.SpeedCorrectionMode != Core.Models.Options.SPEED_CORRECTION_OFF && this.HasSelectedLangAudio());

        public bool RequiresAudioProcessing(bool sourceFillEnabled = false) => sourceFillEnabled || this.RequiresTimelineAudioProcessing;

        private static HashSet<int> Ids(RemuxPairTrackSelection pair, RemuxTrackGroup group)
        {
            bool audio = string.Equals(group.Type, "audio", StringComparison.OrdinalIgnoreCase);
            return group.Side == RemuxTrackSide.Source ? (audio ? pair.SourceAudioIds : pair.SourceSubIds) : (audio ? pair.LangAudioIds : pair.LangSubIds);
        }

        public RemuxTrackSelection BuildSelection()
        {
            RemuxTrackSelection result = new RemuxTrackSelection();
            if (!this.HasCurrentSnapshot) return result;
            Dictionary<string, RemuxPairTrackSelection> pairs = this.Snapshot.Pairs.Where(p => p.IsMatched).ToDictionary(p => p.PairKey,
                p => new RemuxPairTrackSelection { SourceFilePath = p.SourceFilePath, LangFilePath = p.LangFilePath });
            foreach (RemuxTrackGroup group in this.Snapshot.Groups.Where(g => this.ResultGroups.Contains(g.Key) && !this.ExcludedGroups.Contains(g.Key)))
                foreach (RemuxTrackMember member in group.Members) Ids(pairs[member.PairKey], group).Add(member.Track.Id);
            result.Pairs.AddRange(pairs.Values);
            return result;
        }

        /// <summary>Risoluzione legacy delegata al Core, non duplicata nel Web.</summary>
        public int ProcessablePairs()
        {
            if (!this.HasCurrentSnapshot || !this.Snapshot.InventoryComplete) return 0;
            if (this.Advanced) return this.BuildSelection().Pairs.Count(p => p.HasLangTracks);
            MkvToolsService tools = new MkvToolsService(this.Options.MkvMergePath);
            string[] codecs = this.Options.AudioCodec.SelectMany(c => CodecMapping.GetCodecPatterns(c) ?? Array.Empty<string>()).ToArray();
            int count = 0;
            foreach (RemuxPreviewPair pair in this.Snapshot.Pairs.Where(p => p.IsMatched))
            {
                List<TrackInfo> tracks = this.Snapshot.Groups.Where(g => g.Side == RemuxTrackSide.Lang).SelectMany(g => g.Members)
                    .Where(m => m.PairKey == pair.PairKey).Select(m => m.Track).ToList();
                PipelineTrackSelectionResolver.ResolveLanguage(new FileProcessingRecord(), this.Options, tracks, tools,
                    codecs.Length == 0 ? null : codecs, out List<TrackInfo> audio, out List<TrackInfo> subs);
                if (audio.Count + subs.Count > 0) count++;
            }
            return count;
        }

        public List<PipelineInitializationIssue> Validate(bool reusable = false, bool sourceFillEnabled = false)
        {
            Options candidate = RemuxPresetUiHelper.CloneOptions(this.Options, this.MuxKind);
            candidate.ExplicitTrackSelection = this.Advanced ? this.BuildSelection() : null;
            if (reusable) { candidate.SourceFolder = ""; candidate.LanguageFolder = ""; candidate.DestinationFolder = ""; }
            candidate.SkipPairsWithoutSelectedLangTracks = !reusable;
            bool? hasSelectedLangAudio = !reusable && this.HasCurrentSnapshot && this.Snapshot.InventoryComplete
                ? this.HasSelectedLangAudio() : null;
            List<PipelineInitializationIssue> errors = OptionsValidator.Validate(candidate, !reusable, !reusable, hasSelectedLangAudio).ErrorDetails.ToList();
            if (errors.Count == 0 && this.Advanced && this.HasCurrentSnapshot && this.Snapshot.InventoryComplete)
            {
                OptionsValidationResult fillValidation = new OptionsValidationResult();
                OptionsValidator.ValidateExplicitSourceFillSelection(candidate, fillValidation, pair => this.Snapshot.Groups
                    .Where(group => group.Side == RemuxTrackSide.Source).SelectMany(group => group.Members)
                    .Where(member => member.PairKey == pair.PairKey).Select(member => member.Track).ToList());
                errors.AddRange(fillValidation.ErrorDetails);
            }
            // The same predicate drives Required in the wizard. Keep all Core errors and supplement missing field errors.
            if (this.RequiresAudioProcessing(sourceFillEnabled))
            {
                if (string.IsNullOrWhiteSpace(candidate.AudioFormat) && !errors.Any(e => e.Field == nameof(Options.AudioFormat)))
                    errors.Add(new PipelineInitializationIssue("audioFormatRequired", AppText.T("web.remux.validation.audioFormatRequired"), nameof(Options.AudioFormat), "Processing"));
                if (candidate.AudioProcessingScope != "lang" && candidate.AudioProcessingScope != "all" &&
                    !errors.Any(e => e.Field == nameof(Options.AudioProcessingScope)))
                    errors.Add(new PipelineInitializationIssue("audioScopeRequired", AppText.T("web.remux.validation.audioScopeRequired"), nameof(Options.AudioProcessingScope), "Processing"));
            }
            if (!Enum.IsDefined(this.MuxKind) || (this.MuxKind != RemuxMuxKind.DelayCorrection && candidate.FrameSync))
                errors.Add(new PipelineInitializationIssue("mode", AppText.T("web.remux.invalidMode"), "Mode", "Mode"));
            if (((candidate.FrameSync || candidate.DeepAnalysis) && (candidate.AudioDelay != 0 || candidate.SubtitleDelay != 0)) ||
                (!(candidate.FrameSync || candidate.DeepAnalysis) && (!string.IsNullOrEmpty(candidate.AnalysisCropSourcePx) ||
                    !string.IsNullOrEmpty(candidate.AnalysisCropLanguagePx) || candidate.SubtitleCanvasRewrite)))
                errors.Add(new PipelineInitializationIssue("mode", AppText.T("web.remux.invalidMode"), "Mode", "Mode"));
            if (!string.IsNullOrEmpty(candidate.EncodingProfileName) && AppSettingsService.Instance.GetProfile(candidate.EncodingProfileName) == null)
                errors.Add(new PipelineInitializationIssue("profileMissing", AppText.F("web.remux.profileMissing", candidate.EncodingProfileName), "EncodingProfileName", "Processing"));
            if (reusable)
            {
                if (this.Advanced) errors.Add(new PipelineInitializationIssue("advancedPreset", AppText.T("web.remux.advancedPresetDisabled"), "ExplicitTrackSelection", "Tracks"));
                if (candidate.TargetLanguage.Count == 0) errors.Add(new PipelineInitializationIssue("targetRequired", AppText.T("validation.targetLanguageRequired"), "TargetLanguage", "Tracks"));
            }
            else
            {
                if (!this.HasMatching) errors.Add(new PipelineInitializationIssue("matching", AppText.T("web.remux.refreshRequired"), "SourceFolder", "Files"));
                if (this.HasCurrentSnapshot)
                    errors.AddRange(this.Snapshot.Errors.Select(error => new PipelineInitializationIssue("inventory", error.FilePath + " — " + error.Message, "SourceFolder", "Files", true)));
                if (this.HasMatching && this.Advanced)
                    errors.AddRange(RemuxConfigurationPreviewService.ValidateSelection(this.Snapshot, candidate.ExplicitTrackSelection).Errors
                        .Select(message => new PipelineInitializationIssue("selection", message, "ExplicitTrackSelection", "Tracks")));
                else if (this.HasMatching && this.Snapshot.InventoryComplete && this.ProcessablePairs() == 0)
                    errors.Add(new PipelineInitializationIssue("noTracks", AppText.T("web.remux.noProcessablePairs"), "TargetLanguage", "Tracks"));
            }
            return errors;
        }

        public Options BuildOptions()
        {
            Options result = RemuxPresetUiHelper.CloneOptions(this.Options, this.MuxKind);
            result.ExplicitTrackSelection = this.Advanced ? this.BuildSelection() : null;
            result.SkipPairsWithoutSelectedLangTracks = true;
            return result;
        }
    }
}
