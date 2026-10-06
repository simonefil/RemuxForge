using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using RemuxForge.Core.Configuration;
using RemuxForge.Core.Analysis.Speed;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Media;
using RemuxForge.Core.Models;
using RemuxForge.Core.Pipeline;
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
    public partial class RemuxConfigWizardComponent
    {
        [Parameter] public Options Options { get; set; }
        [Parameter] public RemuxMuxKind MuxKind { get; set; }
        [Parameter] public string PresetName { get; set; } = "";
        [Parameter] public IJSObjectReference JsModule { get; set; }
        [Parameter] public Func<RemuxConfigurationDraft, Task<RemuxApplyResult>> ApplyConfiguration { get; set; }
        [Parameter] public EventCallback<RemuxConfigurationDraft> OnApplied { get; set; }
        [Parameter] public EventCallback OnClose { get; set; }
        [Parameter] public RemuxTrackUiState TrackUiState { get; set; }
        [Parameter] public DialogOptions HostOptions { get; set; }
        [Inject] private DialogService DialogService { get; set; }

        public RemuxConfigurationDraft Draft { get; private set; }
        private Options O => this.Draft.Options;
        private bool VisualAnalysis => this.O.FrameSync || this.O.DeepAnalysis;
        private bool InventoryReady => this.Draft.HasCurrentSnapshot && this.Draft.Snapshot?.InventoryComplete == true;
        private bool _confirmingPreset;
        public bool EditingLocked => this._applying || this._applied || this._confirmingPreset;
        private bool CannotEdit => this.EditingLocked || this._disposed;
        private bool AsyncResponseBlocked => this._applying || this._applied || this._disposed;
        private void Edit(Action change) { if (!this.CannotEdit) change(); }
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
        private static readonly string[] AudioFormats = { "", "flac", "lpcm", "aac", "opus", "ac3" };
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
        private string _mediaTitle = "", _mediaReport = "", _mediaError = "";
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
            this.Draft = new RemuxConfigurationDraft(this.Options, this.MuxKind, this.PresetName, this.TrackUiState);
            if (this.HostOptions != null)
            {
                this.HostOptions.CanClose = () => Task.FromResult(!this._applying);
                this.HostOptions.AutoFocusFirstElement = this.JsModule == null;
            }
            this._presets = AppSettingsService.Instance.GetRemuxPresets();
            this._presetToLoad = this.Draft.PresetName;
            this.SyncSourceFill();
            this.SyncSpeedFactor();
            this.SetHelp("mode");
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && this.JsModule != null)
            {
                this._focusSession = this.JsModule.InvokeAsync<IJSObjectReference>("focusRemuxDialog", this._content).AsTask();
                await this._focusSession;
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

        public IEnumerable<PipelineInitializationIssue> FieldErrors(string field) => this._errors.Where(e => e.Field == field && !string.IsNullOrEmpty(field));
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
        }
        private bool CanNavigate(int index) => index <= 1 || (!this._reading && this.Draft.HasMatching &&
            (!this.Draft.Advanced || this.InventoryReady));
        private void Navigate(int index)
        {
            if (index < 0 || index >= Sections.Length || !this.CanNavigate(index) || this.CannotEdit) return;
            if (index == 5 && this._section < 5)
            {
                this._errors = this.ValidateWizard().Where(e => IssueSection(e) == 4).ToList();
                if (this._errors.Count > 0)
                {
                    this._section = 4;
                    this.SetHelp("audioFormat");
                    return;
                }
            }
            this._section = index;
            this.SetHelp(index switch { 0 => "mode", 1 => "source", 2 => "tracks", 3 => "speed", 4 => "audioFormat", _ => "destination" });
        }

        public static string MuxLabel(RemuxMuxKind kind) => AppText.T("web.remux.mux." + kind);

        private static string SpeedModeLabel(string mode) => mode switch
        {
            Options.SPEED_CORRECTION_MANUAL => AppText.T("web.remux.manual"),
            Options.SPEED_CORRECTION_OFF => AppText.T("web.config.option.off"),
            _ => mode
        };
        private static string SpeedFactorLabel(string factor) => SpeedPresets.Any(p => p.Factor == factor)
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
        private static string FieldLabel(string key) => AppText.T(key).Replace("*", "");
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
            "disabled" => AppText.T("web.config.option.disabled"),
            "all" => AppText.T("web.config.option.all"),
            "lang" => "Lang",
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
            string wizardKey = "web.remux.help." + key;
            string title = AppText.T(wizardKey + ".title");
            if (title != "[" + wizardKey + ".title]")
            {
                this._helpTitle = title;
                this._helpText = AppText.T(wizardKey + ".text");
            }
            else
            {
                this._helpTitle = AppText.T("web.config.help.remux." + key + ".title");
                this._helpText = AppText.T("web.config.help.remux." + key + ".text");
            }
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

        private void SetMuxKind(RemuxMuxKind kind) => this.Edit(() => { this.Draft.SetMuxKind(kind); this._errors.Clear(); this.SetMuxHelp(kind); });
        private void SetAdvanced(bool advanced) => this.Edit(() => { this.Draft.SetAdvanced(advanced); this._errors.Clear(); });
        private void TracksChanged() => this.Edit(() => this._errors.RemoveAll(e => e.Section == "Tracks"));
        private static List<string> Csv(string value, bool extensions = false) => (value ?? "").Split(',').Select(v => extensions ? v.Trim().TrimStart('.') : v.Trim()).Where(v => v.Length > 0).Distinct().ToList();

        private void ChangeInput(int field, string path)
        {
            if (this.CannotEdit) return;
            if (field == 0) this.O.SourceFolder = path;
            else if (field == 1) this.O.LanguageFolder = path;
            else this.O.DestinationFolder = path;
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
            object result = await this.DialogService.OpenAsync<RemuxExistingDialogComponent>(AppText.T(field == 2 ? "web.folder.selectFolderTitle" : "web.folder.selectFileOrFolderTitle"),
                new Dictionary<string, object>
                {
                    { "Kind", "picker" }, { "InitialPath", field == 0 ? this.O.SourceFolder : field == 1 ? this.O.LanguageFolder : this.O.DestinationFolder },
                    { "ShowFiles", field != 2 }, { "AllowedExtensions", new List<string>(this.O.FileExtensions) }, { "JsModule", this.JsModule }
                }, ChildDialogOptions(picker: true));
            if (result is string path && !this._disposed) this.ChangeInput(field, path);
        }

        private DialogOptions ChildDialogOptions(bool picker = false) => new DialogOptions
        {
            Width = "min(70rem, 94vw)", Height = "min(48rem, 88vh)", CloseDialogOnOverlayClick = false,
            ContentCssClass = picker ? "rf-remux-picker-dialog-content" : null,
            CloseDialogOnEsc = true, AutoFocusFirstElement = this.JsModule == null, CloseAriaLabel = AppText.T("web.common.cancel")
        };

        private async Task OpenProfilesAsync()
        {
            if (this.CannotEdit) return;
            object result = await this.DialogService.OpenAsync<RemuxExistingDialogComponent>(AppText.T("web.encodingProfiles.title"),
                new Dictionary<string, object> { { "Kind", "encoding" }, { "JsModule", this.JsModule } }, ChildDialogOptions());
            if (result is string name && !this._disposed) this.ProfileSaved(name);
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
                    this._errors.Add(new PipelineInitializationIssue("preview", ex.Message, "SourceFolder", "Files", true));
            }
            finally
            {
                if (!this.AsyncResponseBlocked && revision == this.Draft.Revision) this._reading = false;
            }
        }

        private string SampleName(bool lang)
        {
            RemuxPreviewPair pair = this.Draft.HasMatching ? this.Draft.Snapshot.Pairs.First(p => p.IsMatched) : null;
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
            RemuxPreviewPair pair = this.Draft.Snapshot.Pairs.First(p => p.IsMatched);
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
                    List<string> details = new List<string> { Path.GetFileName(file) + " — " + result.ErrorMessage };
                    if (!string.IsNullOrEmpty(result.Stderr) && result.Stderr != result.ErrorMessage) details.Add(result.Stderr);
                    if (!string.IsNullOrEmpty(result.ExceptionDetails)) details.Add(result.ExceptionDetails);
                    details.Add("ErrorCode: " + result.ErrorCode + (result.ExitCode.HasValue ? " · ExitCode: " + result.ExitCode.Value : ""));
                    this._mediaError = string.Join(Environment.NewLine, details);
                    return;
                }
                this._mediaReport = result.Report;
                this._mediaTitle = (lang ? "Lang · " : "Source · ") + Path.GetFileName(file);
                await this.DialogService.OpenAsync<RemuxExistingDialogComponent>(this._mediaTitle,
                    new Dictionary<string, object> { { "Kind", "media" }, { "Title", this._mediaTitle }, { "Report", this._mediaReport }, { "JsModule", this.JsModule } }, ChildDialogOptions());
                childClosed = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!this.CannotEdit && mediaRevision == this._mediaRevision && revision == this.Draft.Revision)
                    this._mediaError = ex.Message;
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
            this._reading = false;
            this.SyncSourceFill();
            this._section = 0;
            this.SyncSpeedFactor();
            this.SetHelp("preset");
            this._errors = this.Draft.Validate(true).Where(e => e.Code == "profileMissing" || e.Code == "mode").ToList();
        }

        private void UpdatePreset() => this.SavePreset(true, this.Draft.PresetName);

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
            return "";
        }

        private void ProfileSaved(string name)
        {
            if (this.CannotEdit) return;
            this.O.EncodingProfileName = name;
            this._errors.RemoveAll(e => e.Code == "profileMissing");
        }
        private void ChangeAudioFormat(object value)
        {
            if (this.CannotEdit) return;
            this.O.AudioFormat = value as string ?? "";
            if (this.O.AudioFormat != "flac" && this.O.AudioFormat != "lpcm") this.O.AudioDownsample24To16 = false;
            if (string.IsNullOrEmpty(this.O.AudioFormat))
            {
                this.ChangeAudioScope("disabled");
                this.ChangeSourceFill(false);
            }
        }
        private void ChangeAudioScope(object value)
        {
            if (this.CannotEdit) return;
            this.O.AudioProcessingScope = value as string ?? "disabled";
            if (this.O.AudioProcessingScope == "disabled")
            {
                this.O.AudioDownsample24To16 = false;
                this.ChangeGainMode("none");
            }
        }
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
                if (this._section >= 2)
                {
                    this._errors = this.ValidateWizard().Where(issue => IssueSection(issue) == this._section).ToList();
                    if (this._errors.Count > 0) return;
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
            catch (Exception ex) { this._errors.Add(new PipelineInitializationIssue("apply", ex.Message, "", "Configuration", true)); }
            finally { this._applying = false; }
        }

        private IEnumerable<(int Section, string Text)> SummaryRows()
        {
            yield return (0, MuxLabel(this.Draft.MuxKind));
            yield return (1, "Source: " + this.O.SourceFolder + " · Lang: " + this.Draft.PreviewRequest().LangPath + " · " + (this.Draft.HasCurrentSnapshot ? this.Draft.Snapshot.MatchedPairs : 0) + " " + AppText.T("web.remux.matched"));
            yield return (2, AppText.T(this.Draft.Advanced ? "web.remux.advanced" : "web.remux.rules") + (this.Draft.Advanced ? "" : " · " + string.Join(", ", this.O.TargetLanguage)));
            yield return (3, (this.O.DeepAnalysis ? "Deep Analysis" : this.O.FrameSync ? "FrameSync" : AppText.T("web.remux.manual")) + " · Audio " + this.O.AudioDelay + " ms · Sub " + this.O.SubtitleDelay + " ms · " + SpeedModeLabel(this.O.SpeedCorrectionMode) + " " + this.O.ManualStretchFactor);
            if (this.VisualAnalysis) yield return (3, AppText.T("web.config.label.cropSource") + " " + this.O.AnalysisCropSourcePx + " · Lang " + this.O.AnalysisCropLanguagePx);
            yield return (4, "Audio: " + (string.IsNullOrEmpty(this.O.AudioFormat) ? AppText.T("web.remux.copyAudio") : this.O.AudioFormat + " / " + AudioScopeLabel(this.O.AudioProcessingScope)) + " · Video: " + (string.IsNullOrEmpty(this.O.EncodingProfileName) ? AppText.T("web.remux.originalVideo") : this.O.EncodingProfileName) + " · " + AppText.T("web.config.toggle.subtitleCanvasRewrite") + ": " + AppText.T(this.O.SubtitleCanvasRewrite ? "web.common.yes" : "web.common.no"));
            if (this.O.AudioDownsample24To16) yield return (4, AppText.T("web.config.toggle.audio24"));
            if (this.O.AudioPeakNormalize) yield return (4, AppText.T("web.config.toggle.normalization") + " " + this.O.AudioPeakTargetDb + " dB");
            if (this.O.AudioFixedGain) yield return (4, AppText.T("web.config.option.fixedGain") + " " + this.O.AudioFixedGainDb + " dB");
            if (this._sourceFill) yield return (4, AppText.T("web.config.toggle.audioSourceFill") + " · " + this.O.AudioSourceFillLanguage + " · " + this.O.AudioSourceFillThresholdMs + " ms · " + this.O.AudioSourceFillGainDb + " dB · " +
                (this.O.AudioSourceFillStart ? AppText.T("web.config.toggle.start") + " " : "") + (this.O.AudioSourceFillEnd ? AppText.T("web.config.toggle.end") + " " : "") +
                (this.O.AudioSourceFillInsertSilence ? AppText.T("web.config.toggle.insertSilence") : ""));
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
