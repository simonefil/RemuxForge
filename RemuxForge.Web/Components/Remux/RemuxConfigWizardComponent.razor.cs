using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Pipeline;
using RemuxForge.Web.Components.Shared;
using RemuxForge.Web.Services;
using Radzen;
using Radzen.Blazor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Web.Components.Remux
{
    public partial class RemuxConfigWizardComponent : IWizardFieldHost
    {
        [Parameter] public Options Options { get; set; }
        [Parameter] public RemuxMuxKind MuxKind { get; set; }
        [Parameter] public string PresetName { get; set; } = "";
        [Parameter] public IJSObjectReference JsModule { get; set; }
        [Parameter] public Func<RemuxConfigurationDraft, Task<RemuxApplyResult>> ApplyConfiguration { get; set; }
        [Parameter] public EventCallback<RemuxConfigurationDraft> OnApplied { get; set; }
        [Parameter] public RemuxTrackUiState TrackUiState { get; set; }
        [Parameter] public RemuxPreviewRequest PreviewRequest { get; set; }
        [Parameter] public RemuxPreviewSnapshot PreviewSnapshot { get; set; }
        [Parameter] public DialogOptions HostOptions { get; set; }
        [Inject] private DialogService DialogService { get; set; }
        [Inject] private NotificationService NotificationService { get; set; }

        public RemuxConfigurationDraft Draft { get; private set; }
        private Options O => this.Draft.Options;
        private bool VisualAnalysis => this.O.FrameSync || this.O.DeepAnalysis;
        private bool InventoryReady => this.Draft.HasCurrentSnapshot && this.Draft.Snapshot?.InventoryComplete == true;
        private bool _confirmingPreset;
        public bool EditingLocked => this._applying || this._applied || this._confirmingPreset;
        private bool CannotEdit => this.EditingLocked || this._disposed;
        private bool AsyncResponseBlocked => this._applying || this._applied || this._disposed;
        private void Edit(Action change)
        {
            if (this.CannotEdit) return;
            change();
            // Gli errori della sezione aperta seguono le modifiche, senza aspettare il prossimo Avanti
            if (this.SectionHasErrors(this._section)) this.ReplaceSectionErrors(this._section, this.ValidateWizard());
        }
        private static readonly string[] Sections = { "web.remux.section.preset", "web.remux.section.files", "web.remux.section.tracks", "web.config.section.sync", "web.remux.section.processing", "web.remux.section.output" };
        private static readonly (string Factor, string Key)[] SpeedPresets = {
            ("", "off"), ("1001/1000", "speed23976To24"), ("1000/1001", "speed24To23976"),
            ("1001/960", "speed23976To25"), ("960/1001", "speed25To23976"), ("5/4", "speed23976To2997"),
            ("4/5", "speed2997To23976"), ("25/24", "speed24To25"), ("24/25", "speed25To24"),
            ("1250/1001", "speed24To2997"), ("1001/1250", "speed2997To24"),
            ("1200/1001", "speed25To2997"), ("1001/1200", "speed2997To25") };
        private string _customSpeedFactor = "";
        private string SpeedValue => this.O.SpeedCorrectionMode == Options.SPEED_CORRECTION_MANUAL ? this.O.ManualStretchFactor : "";
        private IEnumerable<string> SpeedFactors => (string.IsNullOrEmpty(this._customSpeedFactor) ? Array.Empty<string>() : new[] { this._customSpeedFactor })
            .Concat(SpeedPresets.Select(p => p.Factor));
        private bool AudioRequired => this.Draft.RequiresAudioProcessing(this._sourceFill);
        private static readonly (RemuxMuxKind Kind, string Icon)[] MuxCards = {
            (RemuxMuxKind.Simple, "call_merge"),
            (RemuxMuxKind.DelayCorrection, "more_time"),
            (RemuxMuxKind.DeepAnalysis, "query_stats")
        };
        private static readonly string[] AudioFormats = { "flac", "lpcm", "aac", "opus", "ac3" };
        private static readonly string[] AudioScopes = { "disabled", "lang", "all" };
        private static readonly string[] GainModes = { "none", "peak", "fixed" };
        private IEnumerable<string> ProfileNames => AppSettingsService.Instance.Settings.EncodingProfiles.Select(p => p.Name);
        private string GainMode => this.O.AudioPeakNormalize ? "peak" : this.O.AudioFixedGain ? "fixed" : "none";
        private List<RemuxPreset> _presets;
        private List<PipelineInitializationIssue> _errors = new List<PipelineInitializationIssue>();
        private string _presetToLoad = "", _presetError = "";
        private int _section;
        private string _helpTitle = "", _helpText = "";
        private bool _reading, _applying, _sourceFill, _disposed;
        private bool _mediaLoading, _applied;
        private List<PipelineInitializationIssue> _warnings = new List<PipelineInitializationIssue>();
        private string _mediaTitle = "", _mediaReport = "", _mediaError = "", _mediaErrorDetails = "";
        private readonly ElementReference[] _muxCardRefs = new ElementReference[MuxCards.Length];
        private int? _focusMuxCard;
        private string _focusField;
        private bool _sourceIsFile;
        private CancellationTokenSource _previewCancellation;
        private CancellationTokenSource _mediaCancellation;
        private long _mediaRevision;
        private RemuxPreviewProgress _progress = new RemuxPreviewProgress(0, 0);
        private ElementReference _content;
        private Task<IJSObjectReference> _focusSession;
        private RadzenButton _mediaSourceButton, _mediaLangButton;
        private (RadzenButton Opener, long MediaRevision, long Revision)? _mediaFocusToRestore;

        protected override void OnInitialized()
        {
            this.Draft = new RemuxConfigurationDraft(this.Options, this.MuxKind, this.PresetName, this.TrackUiState, this.PreviewRequest, this.PreviewSnapshot);
            if (this.HostOptions != null)
            {
                this.HostOptions.CanClose = () => Task.FromResult(!this._applying);
                this.HostOptions.AutoFocusFirstElement = this.JsModule == null;
            }
            this._presets = AppSettingsService.Instance.GetRemuxPresets();
            this._presetToLoad = this.Draft.PresetName;
            this.SyncSourceFill();
            this.SyncSpeedFactor();
            this.SyncSourceIsFile();
            this.SetHelp("mode");
        }

        private void SyncSourceIsFile() => this._sourceIsFile = !string.IsNullOrWhiteSpace(this.O.SourceFolder) && File.Exists(this.O.SourceFolder);

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && this.JsModule != null)
            {
                this._focusSession = this.JsModule.InvokeAsync<IJSObjectReference>("focusRemuxDialog", this._content).AsTask();
                await this._focusSession;
            }
            if (this._focusMuxCard is int card)
            {
                this._focusMuxCard = null;
                await this._muxCardRefs[card].FocusAsync();
            }
            if (this._focusField is string field && this.JsModule != null)
            {
                this._focusField = null;
                await this.JsModule.InvokeAsync<bool>("focusRemuxField", this._content, field);
            }
            if (this._mediaFocusToRestore is { } focus)
            {
                this._mediaFocusToRestore = null;
                if (!this.CannotEdit && !this._mediaLoading && this._section == 1 && this.Draft.HasMatching &&
                    focus.MediaRevision == this._mediaRevision && focus.Revision == this.Draft.Revision &&
                    (ReferenceEquals(focus.Opener, this._mediaSourceButton) || ReferenceEquals(focus.Opener, this._mediaLangButton)))
                    await focus.Opener.Element.FocusAsync(preventScroll: true);
            }
        }

        private void SyncSourceFill() => this._sourceFill = this.O.AudioSourceFillThresholdMs > 0 ||
            !string.IsNullOrEmpty(this.O.AudioSourceFillLanguage) || this.O.AudioSourceFillStart || this.O.AudioSourceFillEnd ||
            this.O.AudioSourceFillInsertSilence || this.O.AudioSourceFillGainDb != 0;

        // Gli errori di inventario hanno già un riquadro per file nella sezione File
        public IEnumerable<PipelineInitializationIssue> FieldErrors(string field) => this._errors.Where(e => e.Field == field && !string.IsNullOrEmpty(field) &&
            e.Code != "inventory" && IssueSection(e) == this._section);
        private static readonly HashSet<string> InlineFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "Mode", "SourceFolder", "LanguageFolder", "FileExtensions", "MatchPattern", "ExplicitTrackSelection", "TargetLanguage", "AudioCodec",
            "KeepSourceAudioLangs", "KeepSourceAudioCodec", "KeepSourceSubtitleLangs", "SubOnly", "AudioOnly", "AudioDelay", "SubtitleDelay",
            "AnalysisCropSourcePx", "AnalysisCropLanguagePx", "SpeedCorrectionMode", "ManualStretchFactor", "AudioFormat", "AudioProcessingScope",
            "AudioDownsample24To16", "AudioPeakNormalize", "AudioPeakTargetDb", "AudioFixedGainDb", "AudioSourceFillThresholdMs", "AudioSourceFillLanguage",
            "AudioSourceFillGainDb", "AudioSourceFillStart", "EncodingProfileName", "SubtitleCanvasRewrite", "CopyLangChapters", "DestinationFolder", "Overwrite"
        };
        /// <summary>Il riquadro in alto elenca gli errori delle altre sezioni e quelli senza un campo visibile nella sezione corrente.</summary>
        private IEnumerable<PipelineInitializationIssue> AlertErrors => this._errors.Where(e => IssueSection(e) != this._section ||
            (e.Code == "inventory" ? this._section != 1 : !InlineFields.Contains(e.Field ?? "") ||
                (e.Field == "TargetLanguage" && this.Draft.Advanced)));
        private void ReplaceSectionErrors(int section, IEnumerable<PipelineInitializationIssue> errors)
        {
            this._errors.RemoveAll(e => IssueSection(e) == section);
            this._errors.AddRange(errors.Where(e => IssueSection(e) == section));
        }
        private static int IssueSection(PipelineInitializationIssue issue) => issue.Section switch
        {
            "Mode" => 0, "Files" => 1, "Tracks" => 2, "Synchronization" => 3, "Processing" => 4, "Output" => 5, _ => 0
        };
        private bool SectionHasErrors(int index) => this._errors.Any(e => IssueSection(e) == index);
        private void GoToError(PipelineInitializationIssue issue)
        {
            if (this.CannotEdit) return;
            int section = IssueSection(issue);
            this._section = this.CanNavigate(section) ? section : 1;
            this.SetHelp(issue.Section == "Tools" ? "tools" : "errors");
            if (!string.IsNullOrEmpty(issue.Field)) this._focusField = issue.Field;
        }
        private bool CanNavigate(int index) => index <= 1 || (!this._reading && this.Draft.HasMatching &&
            (!this.Draft.Advanced || this.InventoryReady));
        private void Navigate(int index)
        {
            if (index < 0 || index >= Sections.Length || !this.CanNavigate(index) || this.CannotEdit) return;
            if (index == 5 && this._section < 5)
            {
                this.ReplaceSectionErrors(4, this.ValidateWizard());
                if (this.SectionHasErrors(4))
                {
                    this._section = 4;
                    this.SetHelp("audioFormat");
                    return;
                }
            }
            this._section = index;
            this.SetHelp(index switch { 0 => "mode", 1 => "source", 2 => "tracks", 3 => "mode", 4 => "audioScope", _ => "destination" });
        }

        public static string MuxLabel(RemuxMuxKind kind) => AppText.T("web.remux.mux." + kind);

        private static string SpeedFactorLabel(string factor) => string.IsNullOrEmpty(factor) ? AppText.T("web.remux.speedNone") : SpeedPresets.Any(p => p.Factor == factor)
            ? AppText.T("web.config.option." + SpeedPresets.First(p => p.Factor == factor).Key)
            : AppText.F("web.config.option.speedCustom", factor);
        private void SyncSpeedFactor()
        {
            this._customSpeedFactor = "";
            if (this.O.SpeedCorrectionMode != Options.SPEED_CORRECTION_MANUAL) return;
            string factor = this.O.ManualStretchFactor?.Trim() ?? "";
            if (SpeedPresets.Any(p => p.Factor == factor)) this.O.ManualStretchFactor = factor;
            else if (SpeedCorrectionService.TryParseStretchFactor(factor, out _, out string normalized))
                this.O.ManualStretchFactor = this._customSpeedFactor = normalized;
        }
        private string[] CropParts(bool lang)
        {
            string crop = lang ? this.O.AnalysisCropLanguagePx : this.O.AnalysisCropSourcePx;
            return string.IsNullOrWhiteSpace(crop) ? new[] { "0", "0", "0", "0" } : crop.Split(':');
        }
        private string CropValue(bool lang, int index) => this.CropParts(lang).ElementAtOrDefault(index) ?? "0";
        private void ChangeCrop(bool lang, int index, string value)
        {
            if (this.CannotEdit) return;
            string[] parts = Enumerable.Range(0, 4).Select(i => this.CropValue(lang, i)).ToArray();
            parts[index] = value?.Trim() ?? "";
            string crop = string.Join(":", parts);
            bool valid = Options.TryParseAnalysisCropPx(crop, out int left, out int right, out int top, out int bottom);
            if (valid) crop = Options.NormalizeAnalysisCropPx(string.Join(":", left, right, top, bottom));
            if (lang) this.O.AnalysisCropLanguagePx = crop; else this.O.AnalysisCropSourcePx = crop;
            string field = lang ? "AnalysisCropLanguagePx" : "AnalysisCropSourcePx";
            this._errors.RemoveAll(e => e.Field == field);
            if (!valid) this._errors.Add(new PipelineInitializationIssue("crop", AppText.T(lang ? "validation.invalidAnalysisCropLang" : "validation.invalidAnalysisCropSource"), field, "Synchronization"));
        }
        private static string AudioScopeLabel(string scope) => scope switch
        {
            "disabled" => AppText.T("web.remux.scopeNone"),
            "all" => AppText.T("web.config.option.all"),
            "lang" => AppText.T("web.remux.scopeLang"),
            _ => scope
        };
        private static string GainModeLabel(string mode) => AppText.T(mode switch
        {
            "peak" => "web.config.option.peakNormalization",
            "fixed" => "web.config.option.fixedGain",
            _ => "web.common.none"
        });

        public void SetHelp(string key)
        {
            if (key == "mode")
            {
                this.SetMuxHelp(this.Draft.MuxKind);
                return;
            }
            this._helpTitle = AppText.T("web.remux.help." + key + ".title");
            this._helpText = AppText.T("web.remux.help." + key + ".text");
            if (!this._disposed) _ = this.InvokeAsync(this.StateHasChanged);
        }

        private void SetMuxHelp(RemuxMuxKind kind)
        {
            this._helpTitle = MuxLabel(kind);
            this._helpText = AppText.T(kind switch
            {
                RemuxMuxKind.Simple => "web.remuxWizard.help.mode.Simple.text",
                RemuxMuxKind.DelayCorrection => "web.remuxWizard.help.mode.DelayCorrection.text",
                _ => "web.remuxWizard.help.mode.DeepAnalysis.text"
            });
            if (!this._disposed) _ = this.InvokeAsync(this.StateHasChanged);
        }

        private void SetMuxKind(RemuxMuxKind kind) => this.Edit(() =>
        {
            this.Draft.SetMuxKind(kind);
            this.SyncSpeedFactor();
            this._errors.RemoveAll(e => IssueSection(e) == 0 || IssueSection(e) == 3);
            this.SetMuxHelp(kind);
        });

        /// <summary>Frecce sulle card del tipo di mux: si comportano come un gruppo di radio button</summary>
        private void MuxCardKey(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e, int index)
        {
            int step = e.Key switch { "ArrowRight" or "ArrowDown" => 1, "ArrowLeft" or "ArrowUp" => -1, _ => 0 };
            if (step == 0 || this.CannotEdit) return;
            int next = (index + step + MuxCards.Length) % MuxCards.Length;
            this.SetMuxKind(MuxCards[next].Kind);
            this._focusMuxCard = next;
        }
        private void SetAdvanced(bool advanced) => this.Edit(() => { this.Draft.SetAdvanced(advanced); this._errors.Clear(); });
        private void TracksChanged() => this.Edit(() => this._errors.RemoveAll(e => e.Section == "Tracks"));
        private static List<string> Csv(string value, bool extensions = false) => (value ?? "").Split(',').Select(v => extensions ? v.Trim().TrimStart('.') : v.Trim()).Where(v => v.Length > 0).Distinct().ToList();

        private void ChangeInput(int field, string path)
        {
            if (this.CannotEdit) return;
            if (field == 0) this.O.SourceFolder = path;
            else if (field == 1) this.O.LanguageFolder = path;
            else this.O.DestinationFolder = path;
            if (field == 0) this.SyncSourceIsFile();
            if (field != 2) this.InvalidatePreview();
        }

        private void InvalidatePreview()
        {
            this._previewCancellation?.Cancel();
            this.CancelMediaRead();
            this.Draft.InvalidatePreview();
            this._reading = false;
            this._mediaError = "";
            if (this._section > 1) this._section = 1;
        }

        private async Task Browse(int field)
        {
            if (this.CannotEdit) return;
            string path = await AppDialogs.BrowseAsync(this.DialogService, this.JsModule, field == 0 ? this.O.SourceFolder : field == 1 ? this.O.LanguageFolder : this.O.DestinationFolder,
                field != 2, true, new List<string>(this.O.FileExtensions));
            if (path != null && !this._disposed) this.ChangeInput(field, path);
        }

        private async Task OpenProfilesAsync()
        {
            if (this.CannotEdit) return;
            string name = await AppDialogs.OpenEncodingProfilesAsync(this.DialogService, this.JsModule);
            if (name != null && !this._disposed) this.ProfileSaved(name);
        }

        private async Task OpenSavePresetAsync()
        {
            if (this.Draft.Advanced || this.CannotEdit) return;
            await this.DialogService.OpenAsync<RemuxSavePresetDialogComponent>(AppText.T("web.remux.saveAs"),
                new Dictionary<string, object> { { "SavePreset", new Func<string, string>(name => this.SavePreset(false, name)) }, { "JsModule", this.JsModule } },
                new DialogOptions { Width = "min(34rem, 94vw)", CloseDialogOnOverlayClick = false, CloseDialogOnEsc = true, AutoFocusFirstElement = this.JsModule == null });
        }

        private async Task RefreshAsync()
        {
            if (this.CannotEdit) return;
            this.CancelMediaRead();
            this._previewCancellation?.Cancel();
            this._previewCancellation?.Dispose();
            CancellationTokenSource cancellation = new CancellationTokenSource();
            this._previewCancellation = cancellation;
            long revision = this.Draft.BeginPreview();
            RemuxPreviewRequest request = this.Draft.PreviewRequest();
            this._reading = true;
            this._errors.RemoveAll(e => e.Section == "Files" || e.Section == "Tracks");
            this._progress = new RemuxPreviewProgress(0, 0);
            IProgress<RemuxPreviewProgress> progress = new Progress<RemuxPreviewProgress>(value =>
                _ = this.InvokeAsync(() =>
                {
                    if (this.AsyncResponseBlocked || cancellation.IsCancellationRequested || revision != this.Draft.Revision) return;
                    this._progress = value;
                    this.StateHasChanged();
                }));
            try
            {
                RemuxConfigurationPreviewService preview = new RemuxConfigurationPreviewService(this.O.MkvMergePath);
                RemuxPreviewSnapshot snapshot = await preview.PreviewAsync(request, progress, cancellation.Token);
                if (!this.AsyncResponseBlocked && !cancellation.IsCancellationRequested && this.Draft.AcceptPreview(revision, request, snapshot))
                {
                    if (snapshot.MatchedPairs == 0) this._errors.Add(new PipelineInitializationIssue("matching", AppText.T("web.remux.noMatches"), "MatchPattern", "Files"));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!this.AsyncResponseBlocked && !cancellation.IsCancellationRequested && revision == this.Draft.Revision)
                    this._errors.Add(new PipelineInitializationIssue("preview", ex.Message, "SourceFolder", "Files"));
            }
            finally
            {
                if (!this.AsyncResponseBlocked && revision == this.Draft.Revision) this._reading = false;
            }
        }

        private string SampleName(bool lang)
        {
            RemuxPreviewPair pair = this.Draft.HasMatching ? this.SortedPairs.First() : null;
            return pair == null ? "—" : Path.GetFileName(lang ? pair.LangFilePath : pair.SourceFilePath);
        }
        private async Task OpenMediaInfoAsync(bool lang)
        {
            if (this.CannotEdit || !this.Draft.HasMatching) return;
            // Il pulsante perde il focus quando viene disabilitato durante la lettura.
            // Conserva il vero opener prima dell'await, non nel host del report già letto.
            RadzenButton opener = lang ? this._mediaLangButton : this._mediaSourceButton;
            bool childClosed = false;
            this.CancelMediaRead();
            CancellationTokenSource cancellation = new CancellationTokenSource();
            this._mediaCancellation = cancellation;
            long mediaRevision = this._mediaRevision;
            long revision = this.Draft.Revision;
            RemuxPreviewPair pair = this.SortedPairs.First();
            string file = lang ? pair.LangFilePath : pair.SourceFilePath;
            this._mediaLoading = true;
            this._mediaError = "";
            try
            {
                MediaInfoReportResult result = await new MediaInfoService(AppSettingsService.Instance.Settings.Tools.MediaInfoPath)
                    .GetReportDetailedAsync(file, cancellation.Token);
                if (this.CannotEdit || cancellation.IsCancellationRequested || mediaRevision != this._mediaRevision || revision != this.Draft.Revision) return;
                if (!result.Success)
                {
                    List<string> details = new List<string> { result.ErrorMessage };
                    if (!string.IsNullOrEmpty(result.Stderr) && result.Stderr != result.ErrorMessage) details.Add(result.Stderr);
                    if (!string.IsNullOrEmpty(result.ExceptionDetails)) details.Add(result.ExceptionDetails);
                    details.Add("ErrorCode: " + result.ErrorCode + (result.ExitCode.HasValue ? " · ExitCode: " + result.ExitCode.Value : ""));
                    this._mediaError = AppText.F("web.remux.mediaFailed", Path.GetFileName(file));
                    this._mediaErrorDetails = string.Join(Environment.NewLine, details);
                    return;
                }
                this._mediaReport = result.Report;
                this._mediaTitle = AppText.T(lang ? "web.remux.mediaInfoLang" : "web.remux.mediaInfoSource") + " · " + Path.GetFileName(file);
                await AppDialogs.OpenMediaInfoAsync(this.DialogService, this.JsModule, this._mediaTitle, this._mediaReport);
                childClosed = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!this.CannotEdit && mediaRevision == this._mediaRevision && revision == this.Draft.Revision)
                {
                    this._mediaError = AppText.F("web.remux.mediaFailed", Path.GetFileName(file));
                    this._mediaErrorDetails = ex.Message;
                }
            }
            finally
            {
                if (!this.AsyncResponseBlocked && mediaRevision == this._mediaRevision) this._mediaLoading = false;
                // OnAfterRender ripristina il focus soltanto dopo che il pulsante è
                // nuovamente abilitato nel DOM e il child è stato chiuso.
                if (childClosed && opener != null && !this.CannotEdit && !cancellation.IsCancellationRequested &&
                    mediaRevision == this._mediaRevision && revision == this.Draft.Revision && this._section == 1)
                    this._mediaFocusToRestore = (opener, mediaRevision, revision);
                if (ReferenceEquals(this._mediaCancellation, cancellation)) this._mediaCancellation = null;
                cancellation.Dispose();
            }
        }

        private void CancelMediaRead()
        {
            this._mediaFocusToRestore = null;
            this._mediaRevision++;
            this._mediaCancellation?.Cancel();
            this._mediaLoading = false;
            this._mediaError = "";
            this._mediaErrorDetails = "";
        }

        private async Task LoadPresetAsync()
        {
            if (this.CannotEdit) return;
            RemuxPreset preset = this._presets.FirstOrDefault(item => item.Name == this._presetToLoad)?.Clone();
            if (preset == null) return;
            object result;
            this._confirmingPreset = true;
            try
            {
                result = await this.DialogService.OpenAsync<RemuxLoadPresetDialogComponent>(AppText.T("web.remux.loadConfirmTitle"),
                    new Dictionary<string, object> { { "PresetName", preset.Name }, { "JsModule", this.JsModule } },
                    new DialogOptions
                    {
                        Width = "min(34rem, 94vw)", CloseDialogOnOverlayClick = false, CloseDialogOnEsc = true,
                        AutoFocusFirstElement = this.JsModule == null, CloseAriaLabel = AppText.T("web.common.cancel")
                    });
            }
            finally { this._confirmingPreset = false; }
            // Annulla, X ed Escape non modificano né validano la configurazione corrente.
            if (result is bool confirmed && confirmed && !this.CannotEdit) this.ConfirmLoadPreset(preset);
        }
        private void ConfirmLoadPreset(RemuxPreset preset)
        {
            if (this.CannotEdit) return;
            this._previewCancellation?.Cancel();
            this.CancelMediaRead();
            this.Draft.LoadPreset(preset);
            if (!this.Draft.HasCurrentSnapshot) this._reading = false;
            this.SyncSourceFill();
            this._section = 0;
            this.SyncSpeedFactor();
            this.SetHelp("preset");
            this._errors = this.Draft.Validate(true).Where(e => e.Code == "profileMissing" || e.Code == "mode").ToList();
        }

        private async Task DeletePresetAsync()
        {
            if (this.CannotEdit || string.IsNullOrEmpty(this._presetToLoad)) return;
            string name = this._presetToLoad;
            object result;
            this._confirmingPreset = true;
            try
            {
                result = await this.DialogService.OpenAsync<RemuxLoadPresetDialogComponent>(AppText.T("web.remux.deleteConfirmTitle"),
                    new Dictionary<string, object>
                    {
                        { "PresetName", name }, { "Message", AppText.T("web.remux.deleteConfirm") },
                        { "ConfirmText", AppText.T("web.remux.delete") }, { "Danger", true }, { "JsModule", this.JsModule }
                    },
                    new DialogOptions
                    {
                        Width = "min(34rem, 94vw)", CloseDialogOnOverlayClick = false, CloseDialogOnEsc = true,
                        AutoFocusFirstElement = this.JsModule == null, CloseAriaLabel = AppText.T("web.common.cancel")
                    });
            }
            finally { this._confirmingPreset = false; }
            if (result is not bool confirmed || !confirmed || this.CannotEdit) return;
            if (!AppSettingsService.Instance.DeleteRemuxPreset(name, out string error))
            {
                this.NotificationService.Notify(NotificationSeverity.Error, AppText.T("web.remux.preset"), error, 8000);
                return;
            }
            this._presets = AppSettingsService.Instance.GetRemuxPresets();
            this._presetToLoad = "";
            // Il lavoro resta configurato come prima; perde soltanto il legame con il preset eliminato
            if (string.Equals(this.Draft.PresetName, name, StringComparison.OrdinalIgnoreCase)) this.Draft.DetachPreset();
            this.NotificationService.Notify(NotificationSeverity.Success, AppText.T("web.remux.preset"), AppText.F("web.remux.presetDeleted", name), 4000);
        }

        private void UpdatePreset()
        {
            if (string.IsNullOrEmpty(this.SavePreset(true, this.Draft.PresetName)))
                this.NotificationService.Notify(NotificationSeverity.Success, AppText.T("web.remux.preset"), AppText.F("web.remux.presetSaved", this.Draft.PresetName), 4000);
        }

        private string SavePreset(bool update, string name)
        {
            // Guard anche handler: una UI disabilitata non è il contratto di autorizzazione al salvataggio.
            if (this.Draft.Advanced || this.CannotEdit) return AppText.T("web.remux.advancedPresetDisabled");
            this._errors = this.ValidateWizard(true);
            if (this._errors.Count > 0) return string.Join(Environment.NewLine, this._errors.Select(e => e.Message));
            RemuxPreset preset = this.Draft.CapturePreset(update ? this.Draft.PresetName : name.Trim());
            if (!AppSettingsService.Instance.SaveRemuxPreset(preset, update, out this._presetError))
            {
                if (update) this._errors.Add(new PipelineInitializationIssue("preset", this._presetError, "", "Configuration"));
                return this._presetError;
            }
            this.Draft.MarkPresetSaved(preset);
            this._presets = AppSettingsService.Instance.GetRemuxPresets();
            this._presetToLoad = preset.Name;
            if (!update) this.NotificationService.Notify(NotificationSeverity.Success, AppText.T("web.remux.preset"), AppText.F("web.remux.presetSaved", preset.Name), 4000);
            return "";
        }

        private void ProfileSaved(string name)
        {
            if (this.CannotEdit) return;
            this.O.EncodingProfileName = name;
            this._errors.RemoveAll(e => e.Code == "profileMissing");
        }
        private void ChangeAudioFormat(object value) => this.Edit(() =>
        {
            this.O.AudioFormat = value as string ?? "";
            if (this.O.AudioFormat != "flac" && this.O.AudioFormat != "lpcm") this.O.AudioDownsample24To16 = false;
        });

        /// <summary>Prima si sceglie cosa elaborare; senza elaborazione l'audio resta com'è e i campi di conversione si azzerano</summary>
        private void ChangeAudioScope(object value) => this.Edit(() =>
        {
            this.O.AudioProcessingScope = value as string ?? "disabled";
            if (this.O.AudioProcessingScope != "disabled") return;
            this.O.AudioFormat = "";
            this.O.AudioDownsample24To16 = false;
            this.ChangeGainMode("none");
            this.ChangeSourceFill(false);
        });

        /// <summary>Sovrascrivere il sorgente e scrivere in una cartella si escludono: attivare la sovrascrittura svuota la destinazione</summary>
        private void ChangeOverwrite(bool value) => this.Edit(() =>
        {
            this.O.Overwrite = value;
            if (value) this.O.DestinationFolder = "";
        });

        private static string PairStatus(RemuxPreviewPair pair) => pair.IsMatched ? AppText.T("web.remux.matched") : pair.SkipReason;
        private void ChangeGainMode(object value)
        {
            if (this.CannotEdit) return;
            this.O.AudioPeakNormalize = (string)value == "peak";
            this.O.AudioFixedGain = (string)value == "fixed";
        }
        private void ChangeSpeedFactor(object value)
        {
            if (this.CannotEdit) return;
            string factor = value as string ?? "";
            if (factor != this._customSpeedFactor) this._customSpeedFactor = "";
            this.O.ManualStretchFactor = factor;
            this.O.SpeedCorrectionMode = string.IsNullOrEmpty(factor) ? Options.SPEED_CORRECTION_OFF : Options.SPEED_CORRECTION_MANUAL;
        }
        private void ChangeSourceFill(bool enabled)
        {
            if (this.CannotEdit) return;
            this._sourceFill = enabled;
            if (enabled) return;
            this.O.AudioSourceFillThresholdMs = 0;
            this.O.AudioSourceFillLanguage = "";
            this.O.AudioSourceFillGainDb = 0;
            this.O.AudioSourceFillStart = this.O.AudioSourceFillEnd = this.O.AudioSourceFillInsertSilence = false;
        }

        private List<PipelineInitializationIssue> ValidateWizard(bool reusable = false)
        {
            List<PipelineInitializationIssue> errors = this.Draft.Validate(reusable, this._sourceFill);
            if (this._sourceFill)
            {
                void Require(bool invalid, string field, string key)
                {
                    if (invalid && !errors.Any(e => e.Field == field && e.Message == AppText.T(key)))
                        errors.Add(new PipelineInitializationIssue("sourceFill", AppText.T(key), field, "Processing"));
                }
                Require(this.O.AudioSourceFillThresholdMs <= 0, "AudioSourceFillThresholdMs", "validation.sourceFillThresholdPositive");
                Require(string.IsNullOrWhiteSpace(this.O.AudioSourceFillLanguage), "AudioSourceFillLanguage", "validation.sourceFillLanguageRequired");
                Require(!this.O.AudioSourceFillStart && !this.O.AudioSourceFillEnd && !this.O.AudioSourceFillInsertSilence, "AudioSourceFillStart", "validation.sourceFillModeRequired");
            }
            return errors;
        }

        private async Task NextAsync()
        {
            if (this.CannotEdit) return;
            if (this._section != 5)
            {
                // Dal passo File, Avanti legge i file quando l'anteprima corrente manca
                if (this._section == 1 && !this.Draft.HasMatching)
                {
                    await this.RefreshAsync();
                    if (!this.Draft.HasMatching || this.CannotEdit) return;
                }
                if (this._section >= 2)
                {
                    this.ReplaceSectionErrors(this._section, this.ValidateWizard());
                    if (this.SectionHasErrors(this._section)) return;
                }
                this.Navigate(this._section + 1);
                return;
            }
            this._errors = this.ValidateWizard();
            if (this._errors.Count > 0) return;
            // Ferma le richieste UI prima del clone preparato dall'orchestratore, senza invalidare lo snapshot valido.
            this._previewCancellation?.Cancel();
            this.CancelMediaRead();
            this._reading = false;
            this._applying = true;
            try
            {
                RemuxApplyResult result = await this.ApplyConfiguration(this.Draft);
                if (!result.Success) { this._errors = result.Errors.ToList(); return; }
                this._applied = true;
                this._warnings = result.Warnings.ToList();
                await this.OnApplied.InvokeAsync(this.Draft);
                if (this._warnings.Count == 0) this.DialogService.Close(true);
            }
            catch (Exception ex) { this._errors.Add(new PipelineInitializationIssue("apply", ex.Message, "", "Configuration")); }
            finally { this._applying = false; }
        }

        private IEnumerable<RemuxPreviewPair> SortedPairs => this.Draft.Snapshot.Pairs
            .OrderBy(p => p.IsMatched ? 0 : 1).ThenBy(p => p.EpisodeId ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => Path.GetFileName(p.SourceFilePath), StringComparer.OrdinalIgnoreCase);

        private string ProcessCountsText
        {
            get
            {
                int matched = this.Draft.Snapshot.MatchedPairs, processable = this.Draft.ProcessablePairs();
                string text = AppText.F("web.remux.processCounts", matched, processable);
                return matched > processable ? text + " · " + AppText.F("web.remux.processSkipped", matched - processable) : text;
            }
        }

        private static string YesNo(bool value) => AppText.T(value ? "web.common.yes" : "web.common.no");
        private static string Join(IEnumerable<string> parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
        private static string ListOrAll(List<string> values) => values.Count == 0 ? AppText.T("web.remux.summary.any") : string.Join(", ", values);

        /// <summary>Riepilogo per sezione: etichetta e valore leggibile, con i dettagli solo quando sono impostati</summary>
        private IEnumerable<(int Section, string Label, string Value)> SummaryRows()
        {
            yield return (0, AppText.T("web.remux.muxKind"), MuxLabel(this.Draft.MuxKind));
            string files = AppText.F("web.remux.summary.pairs", this.Draft.HasCurrentSnapshot ? this.Draft.Snapshot.MatchedPairs : 0) + Environment.NewLine +
                "Source: " + this.O.SourceFolder + Environment.NewLine + "Lang: " + this.Draft.PreviewRequest().LangPath;
            yield return (1, AppText.T("web.remux.summary.files"), files);
            string tracks = this.Draft.Advanced
                ? AppText.T("web.remux.advanced") + (this.InventoryReady ? Environment.NewLine + this.ProcessCountsText : "")
                : Join(new[]
                {
                    AppText.T("web.remux.summary.import") + " " + ListOrAll(this.O.TargetLanguage),
                    this.O.AudioCodec.Count > 0 ? AppText.T("web.remux.label.audioCodec") + " " + string.Join(", ", this.O.AudioCodec) : "",
                    this.O.SubOnly ? AppText.T("web.config.toggle.subOnly") : "", this.O.AudioOnly ? AppText.T("web.config.toggle.audioOnly") : ""
                }) + Environment.NewLine + Join(new[]
                {
                    AppText.T("web.remux.summary.keepAudio") + " " + ListOrAll(this.O.KeepSourceAudioLangs),
                    AppText.T("web.remux.summary.keepSub") + " " + ListOrAll(this.O.KeepSourceSubtitleLangs)
                });
            yield return (2, AppText.T("web.remux.section.tracks"), tracks);
            string sync = this.VisualAnalysis
                ? Join(new[]
                {
                    this.O.DeepAnalysis ? "Deep Analysis" : "FrameSync",
                    this.O.SpeedCorrectionMode == Options.SPEED_CORRECTION_MANUAL ? AppText.T("web.remux.label.speed") + " " + SpeedFactorLabel(this.O.ManualStretchFactor) : "",
                    string.IsNullOrEmpty(this.O.AnalysisCropSourcePx) ? "" : AppText.T("web.remux.label.cropSource") + " " + this.O.AnalysisCropSourcePx,
                    string.IsNullOrEmpty(this.O.AnalysisCropLanguagePx) ? "" : AppText.T("web.remux.label.cropLang") + " " + this.O.AnalysisCropLanguagePx
                })
                : AppText.F("web.remux.summary.delays", this.O.AudioDelay, this.O.SubtitleDelay);
            yield return (3, AppText.T("web.config.section.sync"), sync);
            string audio = this.O.AudioProcessingScope == "disabled" ? AppText.T("web.remux.copyAudio") : Join(new[]
            {
                AudioScopeLabel(this.O.AudioProcessingScope) + " · " + (string.IsNullOrEmpty(this.O.AudioFormat) ? "—" : this.O.AudioFormat.ToUpperInvariant()),
                this.O.AudioDownsample24To16 ? AppText.T("web.config.toggle.audio24") : "",
                this.O.AudioPeakNormalize ? AppText.T("web.config.option.peakNormalization") + " " + this.O.AudioPeakTargetDb + " dB" : "",
                this.O.AudioFixedGain ? AppText.T("web.config.option.fixedGain") + " " + this.O.AudioFixedGainDb + " dB" : ""
            });
            if (this._sourceFill)
                audio += Environment.NewLine + AppText.T("web.config.toggle.audioSourceFill") + ": " + Join(new[]
                {
                    this.O.AudioSourceFillLanguage, this.O.AudioSourceFillThresholdMs + " ms", this.O.AudioSourceFillGainDb + " dB",
                    this.O.AudioSourceFillStart ? AppText.T("web.config.toggle.start") : "", this.O.AudioSourceFillEnd ? AppText.T("web.config.toggle.end") : "",
                    this.O.AudioSourceFillInsertSilence ? AppText.T("web.config.toggle.insertSilence") : ""
                });
            yield return (4, "Audio", audio);
            yield return (4, "Video", string.IsNullOrEmpty(this.O.EncodingProfileName) ? AppText.T("web.remux.originalVideo") : this.O.EncodingProfileName);
            yield return (4, AppText.T("web.detail.subtitlesLabel"), AppText.T("web.config.toggle.subtitleCanvasRewrite") + ": " + YesNo(this.O.SubtitleCanvasRewrite) +
                " · " + AppText.T("web.config.toggle.copyLangChapters") + ": " + YesNo(this.O.CopyLangChapters));
            yield return (5, AppText.T("web.remux.summary.output"), this.O.Overwrite ? AppText.T("web.config.toggle.overwrite") :
                string.IsNullOrEmpty(this.O.DestinationFolder) ? "—" : this.O.DestinationFolder);
        }

        private async Task CancelAsync()
        {
            if (this._disposed || this._applying) return;
            if (!this._applied) this.InvalidatePreview();
            this.DialogService.Close(this._applied);
            await Task.CompletedTask;
        }
        public void Dispose()
        {
            if (this._disposed) return;
            this._disposed = true;
            this._previewCancellation?.Cancel();
            this._previewCancellation?.Dispose();
            this.CancelMediaRead();
            if (!this.EditingLocked) this.Draft?.InvalidatePreview();
        }
        public async ValueTask DisposeAsync()
        {
            this.Dispose();
            if (this._focusSession == null) return;
            try
            {
                IJSObjectReference session = await this._focusSession;
                await session.InvokeVoidAsync("dispose");
                await session.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
            catch (ObjectDisposedException) { }
        }
    }
}
