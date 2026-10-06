using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting;
using RemuxForge.Web.Components.Shared;
using RemuxForge.Web.Services;

namespace RemuxForge.Web.Components.Split;

public partial class SplitEditorPageComponent
{
    [Parameter] public int RecordIndex { get; set; }
    [Parameter] public MkvSplitRecord Record { get; set; }
    [Parameter] public int InitialSegmentNum { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }
    private readonly MkvSplitDocumentService _service = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<Guid, OutputView> _views = new();
    private readonly Stack<Checkpoint> _undo = new(), _redo = new();
    private SplitEditorSnapshot _snapshot;
    private MkvSplitDocument _draft;
    private MkvSplitTimelineProjection _projection;
    private Guid _outputId, _destinationId;
    private int _sourceFrame, _sourceIn, _sourceOut, _trimIn, _trimOut;
    private string _name = "", _error = "", _previewMessage = "", _audioMode = "waveform";
    private double _gain = 1;
    private int? _trackId;
    private bool _busy, _disposed, _sourcePreview;
    private long _revision, _requestSequence;
    private string _gestureToken;
    private long _gestureSequence;
    private Checkpoint _gestureCheckpoint;
    private string _gestureError;
    private MkvSplitTimelineProjection _gestureProjection;
    private bool _confirming;
    private bool _initializingInterop, _interopFailed;
    private TaskCompletionSource _interopFinished;
    private MkvSplitFrameResolution _displayedFrame;
    private ElementReference _root;
    private FramePreviewPaneComponent _preview;
    private MediaTimelinePanelComponent _sourceTimeline, _resultTimeline;
    private IJSObjectReference _module, _framePreview, _keyboard;
    private DotNetObjectReference<SplitEditorPageComponent> _reference;
    private OutputView _emptyView = new();
    private MkvSplitOutput Output => _draft?.Outputs.Find(o => o.Id == _outputId);
    private MkvSplitOutputProjection Result => (_gestureProjection ?? _projection)?.Outputs.Find(o => o.OutputId == _outputId);
    private OutputView View => _views.TryGetValue(_outputId, out var view) ? view : _emptyView;
    private MkvSplitClip Clip => Output?.Clips.Find(c => c.Id == View.ClipId);
    private MkvSplitClip NextClip => Clip == null ? null : Output.Clips.Skip(Output.Clips.IndexOf(Clip) + 1).FirstOrDefault();
    private bool Contiguous => NextClip != null && Clip.EndFrameExclusive == NextClip.StartFrame;
    private static string T(string key) => AppText.T("web.splitMontage." + key);
    private static string Time(double seconds) => MkvSplitSegmentService.SecsToTs(seconds);
    private bool Dirty => _draft != null && JsonSerializer.Serialize(_draft) != JsonSerializer.Serialize(_snapshot.Document);

    protected override async Task OnInitializedAsync() => await OpenAsync();

    private async Task OpenAsync()
    {
        if (_busy || _disposed) return;
        _busy = true;
        _error = "";
        try
        {
            var opened = await SplitOrchestrator.OpenEditorAsync(RecordIndex, _lifetime.Token);
            if (_disposed) { if (opened.Snapshot != null) SplitOrchestrator.CloseEditor(opened.Snapshot.SessionId); return; }
            _snapshot = opened.Snapshot;
            if (_snapshot == null) { _error = string.Join(Environment.NewLine, opened.Diagnostics.Select(d => d.Message)); return; }
            _draft = _snapshot.Document.Clone();
            _projection = _service.Project(_draft, _snapshot.Analysis, _snapshot.Options);
            _sourceOut = _snapshot.Analysis.SourcePts.Length;
            _trackId = _snapshot.SourceInfo.Tracks.FirstOrDefault(t => t.Type == "audio")?.Id;
            SelectOutput(_draft.Outputs.ElementAtOrDefault(Math.Max(0, InitialSegmentNum - 1))?.Id ?? _draft.Outputs.FirstOrDefault()?.Id ?? Guid.Empty);
        }
        finally { _busy = false; }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || _initializingInterop || _interopFailed) return;
        _initializingInterop = true;
        _interopFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (_module == null)
            {
                _module = await JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/split-timeline.js");
                if (_disposed) return;
                _reference = DotNetObjectReference.Create(this);
                _keyboard = await _module.InvokeAsync<IJSObjectReference>("captureEditorKeyboard", _root, _reference);
                if (_disposed) return;
                await _module.InvokeVoidAsync("focusEditor", _root);
            }
            if (_snapshot != null && _preview != null && (_framePreview == null || !_sourceTimeline.IsAttached || !_resultTimeline.IsAttached))
            {
                _framePreview ??= await _module.InvokeAsync<IJSObjectReference>("createFramePreview", _preview.Canvas);
                if (_disposed) return;
                await _sourceTimeline.AttachAsync(_module, _reference, TimelineModel(true));
                if (_disposed) return;
                await _resultTimeline.AttachAsync(_module, _reference, TimelineModel(false));
                await RefreshAsync();
            }
        }
        catch (JSException) { _interopFailed = true; _previewMessage = T("previewError"); if (!_disposed) StateHasChanged(); }
        finally { _initializingInterop = false; _interopFinished.TrySetResult(); }
    }
    private async Task RetryPreviewAsync() { if (_interopFailed) { _interopFailed = false; StateHasChanged(); } else await RefreshAsync(); }

    private void SelectOutput(Guid id)
    {
        _outputId = id;
        if (!_views.ContainsKey(id)) _views[id] = new OutputView();
        if (Output?.Clips.Any(c => c.Id == View.ClipId) != true) View.ClipId = Output?.Clips.FirstOrDefault()?.Id ?? Guid.Empty;
        View.Selected.RemoveWhere(id => Output?.Clips.Any(c => c.Id == id) != true);
        View.Seconds = Math.Clamp(View.Seconds, 0, Result?.DurationSeconds ?? 0);
        _name = Result?.FileName ?? "";
        _destinationId = _draft.Outputs.FirstOrDefault(o => o.Id != id)?.Id ?? Guid.Empty;
        SetTrimFields();
    }
    private void SetTrimFields() { _trimIn = Clip?.StartFrame ?? 0; _trimOut = Clip?.EndFrameExclusive ?? 0; }
    private async Task SelectOutputAsync(Guid id) { if (_gestureToken != null) { RestoreGestureCursor(); ClearGesture(); await _module.InvokeVoidAsync("cancelEditorGestures", _root); } SelectOutput(id); _sourcePreview = false; await RefreshAsync(); }
    private async Task SelectClipAsync(Guid id) { View.ClipId = id; SetTrimFields(); await RefreshAsync(); }
    private void ToggleClipSelection(Guid id, ChangeEventArgs args) { if (args.Value is true) View.Selected.Add(id); else View.Selected.Remove(id); }
    private List<Guid> SelectedClipIds() => View.Selected.Count > 0 ? Output.Clips.Where(c => View.Selected.Contains(c.Id)).Select(c => c.Id).ToList() : Clip == null ? new() : new() { Clip.Id };
    private async Task SetPreviewSideAsync(bool source) { _sourcePreview = source; await RefreshAsync(); }
    private async Task MarkSourceAsync(bool start) { if (start) _sourceIn = _sourceFrame; else _sourceOut = _sourceFrame + 1; await RefreshAsync(); }
    private async Task MarkResultAsync(bool start) { if (start) View.In = ResultFrame(); else View.Out = ResultFrame(); await RefreshAsync(); }
    private async Task SelectionChangedAsync() => await RefreshAsync();
    private int ResultFrame() => _service.ResolveResultBoundary(_projection, _snapshot.Analysis, _outputId, View.Seconds).ResultFrame;

    private Task RenameAsync() => EditAsync(MkvSplitEditKind.RenameOutput, nameMode: MkvSplitNameMode.Custom);
    private Task TrimAsync() => EditAsync(MkvSplitEditKind.TrimClip);
    private async Task SnapStartAsync()
    {
        if (Clip == null || _busy || _gestureToken != null || _snapshot.Options.Snap == MkvSplitSnapMode.Off) return;
        var segment = new MkvSplitSegment { Num = 1, StartFrame = Clip.StartFrame, FrameCount = Clip.EndFrameExclusive - Clip.StartFrame,
            StartTs = _snapshot.Analysis.SourcePts[Clip.StartFrame], EndTs = MkvSplitDocumentService.BoundarySeconds(_snapshot.Analysis, Clip.EndFrameExclusive) };
        var service = new MkvSplitSegmentService();
        service.ApplySnap(new() { segment }, _snapshot.Analysis.KeyFlags, _snapshot.Analysis.SourcePts, _snapshot.Options.Snap);
        if (service.Warnings.Count > 0) { _error = string.Join(Environment.NewLine, service.Warnings.Select(w => w.Message)); return; }
        await ExecuteAsync(new() { Kind = MkvSplitEditKind.TrimClip, OutputId = _outputId, ClipId = Clip.Id, StartFrame = segment.StartFrame, EndFrameExclusive = segment.StartFrame + segment.FrameCount });
    }
    private async Task EditAsync(MkvSplitEditKind kind, MkvSplitNameMode nameMode = MkvSplitNameMode.Automatic)
    {
        var destination = _draft?.Outputs.Find(o => o.Id == _destinationId);
        var splitCursor = kind == MkvSplitEditKind.SplitClip
            ? _service.ResolveResultBoundary(_projection, _snapshot.Analysis, _outputId, View.Seconds)
            : null;
        await ExecuteAsync(new MkvSplitEditCommand {
            Kind = kind, OutputId = _outputId, ClipId = kind == MkvSplitEditKind.SplitClip ? splitCursor.ClipId ?? Guid.Empty : Clip?.Id ?? Guid.Empty, NextClipId = NextClip?.Id ?? Guid.Empty,
            OutputIds = kind == MkvSplitEditKind.MergeOutputs ? new() { _outputId, _destinationId } : new() { _outputId },
            ClipIds = SelectedClipIds(), DestinationOutputId = _destinationId,
            InsertIndex = kind is MkvSplitEditKind.CreateEmptyOutput or MkvSplitEditKind.CreateOutputFromSource ? _draft.Outputs.Count : destination?.Clips.Count ?? 0,
            StartFrame = kind == MkvSplitEditKind.TrimClip ? _trimIn : kind == MkvSplitEditKind.MoveSharedBoundary ? _trimOut : _sourceIn,
            EndFrameExclusive = kind == MkvSplitEditKind.TrimClip ? _trimOut : _sourceOut,
            ResultFrame = kind == MkvSplitEditKind.RemoveResultRange ? View.In : splitCursor?.ResultFrame ?? ResultFrame(), ResultEndFrameExclusive = View.Out,
            NameMode = nameMode, CustomFileName = _name
        });
    }
    private async Task ReorderOutputAsync(int delta)
    {
        if (Output == null) return;
        int index = _draft.Outputs.IndexOf(Output), target = index + delta;
        if (target < 0 || target >= _draft.Outputs.Count) return;
        await ExecuteAsync(new() { Kind = MkvSplitEditKind.ReorderOutputs, OutputIds = new() { _outputId }, InsertIndex = target });
    }
    private async Task ReorderClipAsync(int delta)
    {
        if (Clip == null) return;
        var selected = SelectedClipIds();
        int index = Output.Clips.FindIndex(c => selected.Contains(c.Id)), target = index + delta;
        if (target < 0 || target > Output.Clips.Count - selected.Count) return;
        await ExecuteAsync(new() { Kind = MkvSplitEditKind.ReorderClips, OutputId = _outputId, ClipIds = selected, InsertIndex = target });
    }
    private async Task ExecuteAsync(MkvSplitEditCommand command, Checkpoint checkpoint = null)
    {
        if (_busy || _disposed || _draft == null || _gestureToken != null) return;
        _gestureProjection = null;
        var result = _service.Execute(_draft, command, _snapshot.Analysis);
        _error = string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message));
        if (!result.Changed) return;
        _undo.Push(checkpoint ?? Capture()); _redo.Clear();
        _draft = result.Document; _revision++;
        _projection = _service.Project(_draft, _snapshot.Analysis, _snapshot.Options);
        SelectOutput(result.SelectedOutputId ?? (_draft.Outputs.Any(o => o.Id == _outputId) ? _outputId : _draft.Outputs.FirstOrDefault()?.Id ?? Guid.Empty));
        if (result.SelectedClipId.HasValue) View.ClipId = result.SelectedClipId.Value;
        SetTrimFields();
        await RefreshAsync();
    }
    private Checkpoint Capture() => new(_draft.Clone(), _outputId, _views.ToDictionary(p => p.Key, p => p.Value.Clone()), _sourceFrame, _sourceIn, _sourceOut, _sourcePreview);
    private async Task HistoryAsync(bool redo)
    {
        var source = redo ? _redo : _undo;
        if (_busy || source.Count == 0) return;
        if (_gestureToken != null) { RestoreGestureCursor(); ClearGesture(); await _module.InvokeVoidAsync("cancelEditorGestures", _root); }
        ClearGesture();
        (redo ? _undo : _redo).Push(Capture());
        var checkpoint = source.Pop();
        _draft = checkpoint.Document; _views.Clear();
        foreach (var item in checkpoint.Views) _views[item.Key] = item.Value;
        _sourceFrame = checkpoint.SourceFrame; _sourceIn = checkpoint.SourceIn; _sourceOut = checkpoint.SourceOut;
        _sourcePreview = checkpoint.SourcePreview;
        _projection = _service.Project(_draft, _snapshot.Analysis, _snapshot.Options); _revision++;
        SelectOutput(checkpoint.OutputId); _error = ""; await RefreshAsync();
    }
    [JSInvokable] public async Task OnMontageSeek(string side, double ms)
    {
        if (_disposed || _snapshot == null) return;
        _sourcePreview = side == "source";
        if (_sourcePreview) _sourceFrame = _service.ResolveSource(_snapshot.Analysis, Math.Clamp(ms / 1000, _snapshot.Analysis.SourcePts[0], _snapshot.Analysis.Duration)).SourceFrame;
        else View.Seconds = Math.Clamp(ms / 1000, 0, Result?.DurationSeconds ?? 0);
        await RefreshAsync();
    }
    // Every move is evaluated against the unchanged draft. Only end commits once.
    [JSInvokable] public async Task OnMontageGesture(string token, string phase, string kind, string side, string clipId, bool start, double ms, int index, long revision, string outputId, long sequence)
    {
        if (_disposed || _busy || revision != _revision || outputId != _outputId.ToString()) return;
        if (phase == "begin")
        {
            if (_gestureToken != null) return;
            _gestureToken = token; _gestureSequence = sequence; _gestureCheckpoint = Capture();
            _gestureError = _error;
        }
        else if (_gestureToken != token || sequence <= _gestureSequence) return;
        _gestureSequence = sequence;
        if (phase == "cancel")
        {
            RestoreGestureCursor(); ClearGesture(); await RefreshAsync(); return;
        }
        if (kind == "seek")
        {
            _sourcePreview = side == "source";
            if (_sourcePreview) _sourceFrame = _service.ResolveSource(_snapshot.Analysis, Math.Clamp(ms / 1000, _snapshot.Analysis.SourcePts[0], _snapshot.Analysis.Duration)).SourceFrame;
            else View.Seconds = Math.Clamp(ms / 1000, 0, _projection.Outputs.Find(o => o.OutputId == _outputId)?.DurationSeconds ?? 0);
            if (phase == "end") ClearGesture();
            await RefreshAsync(); return;
        }
        if (!Guid.TryParse(clipId, out var id)) return;
        var clip = Output?.Clips.Find(c => c.Id == id);
        if (clip == null) return;
        int boundary = ms >= _snapshot.Analysis.Duration * 1000 ? _snapshot.Analysis.SourcePts.Length : _service.ResolveSource(_snapshot.Analysis, Math.Clamp(ms / 1000, _snapshot.Analysis.SourcePts[0], _snapshot.Analysis.Duration)).SourceFrame;
        var command = kind == "reorder"
            ? new MkvSplitEditCommand { Kind = MkvSplitEditKind.ReorderClips, OutputId = _outputId, ClipIds = new() { id }, InsertIndex = index }
            : new MkvSplitEditCommand { Kind = MkvSplitEditKind.TrimClip, OutputId = _outputId, ClipId = id, StartFrame = start ? boundary : clip.StartFrame, EndFrameExclusive = start ? clip.EndFrameExclusive : boundary };
        var candidate = _service.Execute(_draft, command, _snapshot.Analysis);
        _error = string.Join(Environment.NewLine, candidate.Diagnostics.Select(d => d.Message));
        if (phase == "end")
        {
            var checkpoint = _gestureCheckpoint;
            ClearGesture();
            await ExecuteAsync(command, checkpoint);
            if (!candidate.Changed) await RefreshAsync();
            if (kind == "trim" && !_disposed)
            {
                var finalFrame = _service.ResolveClipBoundary(_draft, _snapshot.Analysis, _outputId, id, start);
                SetBoundaryCursor(finalFrame); await RefreshAsync(false); await LoadPreviewAsync(finalFrame);
            }
            return;
        }
        _gestureProjection = _service.Project(candidate.Document, _snapshot.Analysis, _snapshot.Options);
        _sourcePreview = false;
        var frame = kind == "reorder"
            ? _service.ResolveResult(_gestureProjection, _snapshot.Analysis, _outputId, Math.Min(View.Seconds, Result?.DurationSeconds ?? 0))
            : _service.ResolveClipBoundary(candidate.Document, _snapshot.Analysis, _outputId, id, start);
        if (kind == "trim") SetBoundaryCursor(frame);
        await RefreshAsync(false);
        await LoadPreviewAsync(frame);
    }
    private void SetBoundaryCursor(MkvSplitFrameResolution frame)
    {
        if (!frame.IsValid || !frame.ClipId.HasValue) return;
        var cursor = _service.ResolveClipSourceFrame(_gestureProjection ?? _projection, _snapshot.Analysis,
            _outputId, frame.ClipId.Value, frame.SourceFrame);
        if (cursor.IsValid) View.Seconds = cursor.ResultSeconds;
    }
    private void RestoreGestureCursor()
    {
        if (_gestureCheckpoint == null) return;
        _sourceFrame = _gestureCheckpoint.SourceFrame;
        _error = _gestureError;
        _sourcePreview = _gestureCheckpoint.SourcePreview;
        if (_gestureCheckpoint.Views.TryGetValue(_outputId, out var view)) _views[_outputId] = view.Clone();
    }
    private void ClearGesture() { _gestureToken = null; _gestureCheckpoint = null; _gestureProjection = null; }
    [JSInvokable] public async Task OnFrameStep(string side, int delta)
    {
        if (_disposed || _snapshot == null) return;
        if (_sourcePreview) _sourceFrame = Math.Clamp(_sourceFrame + delta, 0, _snapshot.Analysis.SourcePts.Length - 1);
        else if (Result?.FrameCount > 0)
        {
            var projection = _gestureProjection ?? _projection;
            var currentFrame = _service.ResolveResultBoundary(projection, _snapshot.Analysis, _outputId, View.Seconds);
            if (currentFrame.IsValid)
            {
                int targetFrame = Math.Clamp(currentFrame.ResultFrame + delta, 0, Result.FrameCount - 1);
                var steppedFrame = _service.ResolveResultFrame(projection, _snapshot.Analysis, _outputId, targetFrame);
                if (steppedFrame.IsValid) View.Seconds = steppedFrame.ResultSeconds;
            }
        }
        await RefreshAsync();
    }
    [JSInvokable] public async Task OnEditorKey(string key, bool shift, bool editing)
    {
        if (_disposed) return;
        if (key == "Escape") await CloseAsync();
        else if (!editing && key == "Delete" && Clip != null) await EditAsync(MkvSplitEditKind.RemoveClips);
        else if (_snapshot != null && !editing && (key is "Home" or "End")) await OnMontageSeek(_sourcePreview ? "source" : "result", key == "Home" ? 0 : (_sourcePreview ? _snapshot.Analysis.Duration : Result?.DurationSeconds ?? 0) * 1000);
    }
    private object TimelineModel(bool source)
    {
        var analysis = _snapshot.Analysis;
        return new {
            side = source ? "source" : "result", outputId = _outputId, revision = _revision, durationMs = (source ? analysis.Duration : Result?.DurationSeconds ?? 0) * 1000,
            sourceDurationMs = analysis.Duration * 1000, playheadMs = (source ? analysis.SourcePts[_sourceFrame] : View.Seconds) * 1000,
            audioMode = _audioMode, waveformGain = _gain, precisionMode = true, nyquistHz = (_snapshot.SourceInfo.Tracks.FirstOrDefault(t => t.Id == _trackId)?.SamplingFrequency ?? 48000) / 2.0,
            selectionStartMs = source ? MkvSplitDocumentService.BoundarySeconds(analysis, Math.Clamp(_sourceIn, 0, analysis.SourcePts.Length)) * 1000 : ResultBoundarySeconds(View.In) * 1000,
            selectionEndMs = source ? MkvSplitDocumentService.BoundarySeconds(analysis, Math.Clamp(_sourceOut, 0, analysis.SourcePts.Length)) * 1000 : ResultBoundarySeconds(View.Out) * 1000,
            keyframes = source ? analysis.KeyFlags.Select((flag, index) => new { flag.Key, index }).Where(f => f.Key && f.index < analysis.SourcePts.Length).Select(f => analysis.SourcePts[f.index] * 1000).ToArray() : Array.Empty<double>(),
            audioUrl = _trackId.HasValue ? $"/api/split-audio/{RecordIndex}/input?trackId={_trackId}&durationMs={Math.Ceiling(analysis.Duration * 1000)}&mode={_audioMode}&quality=high&sessionId={_snapshot.SessionId}" : null,
            chapters = (source ? analysis.Chapters : Result?.Chapters ?? new()).Select(c => new { timeMs = c.Timestamp * 1000, name = c.Name }).ToArray(),
            clips = (Result?.Clips ?? new()).Select((c, i) => new { id = c.ClipId, startMs = c.ResultStartSeconds * 1000, endMs = c.ResultEndSeconds * 1000, sourceStartMs = c.SourceStartSeconds * 1000, sourceEndMs = c.SourceEndSeconds * 1000, selected = c.ClipId == View.ClipId, label = (i + 1).ToString() }).ToArray()
        };
    }
    private double ResultBoundarySeconds(int frame)
    {
        if (Result == null || Result.Clips.Count == 0) return 0;
        var boundary = _service.ResolveResultFrame(_gestureProjection ?? _projection, _snapshot.Analysis,
            _outputId, Math.Clamp(frame, 0, Result.FrameCount), boundary: true);
        return boundary.IsValid ? boundary.ResultSeconds : 0;
    }
    private List<MediaTimelinePanelComponent.AudioLane> AudioLanes(string side) => new() { new() { ElementId = "split-audio-" + side, Label = T("audio"), Tracks = _snapshot.SourceInfo.Tracks.Where(t => t.Type == "audio").ToList(), SelectedTrackId = _trackId, OnTrackChanged = EventCallback.Factory.Create<ChangeEventArgs>(this, async e => { if (int.TryParse(e.Value?.ToString(), out int id)) _trackId = id; await RefreshAsync(); }) } };
    private async Task AudioModeAsync(string mode) { _audioMode = mode; await RefreshAsync(); }
    private async Task GainAsync(double value) { _gain = value; await RefreshAsync(); }
    private MkvSplitFrameResolution PreviewResolution() => _sourcePreview ? _service.ResolveSource(_snapshot.Analysis, _snapshot.Analysis.SourcePts[_sourceFrame]) : _service.ResolveResult(_projection, _snapshot.Analysis, _outputId, View.Seconds);
    private string PreviewMeta() => _snapshot == null ? "" : T("source") + " " + Time((_displayedFrame ?? PreviewResolution()).SourceSeconds) + " · " + T("frame") + " " + (_displayedFrame ?? PreviewResolution()).SourceFrame + " · " + T("result") + " " + Time(View.Seconds);
    private async Task RefreshAsync(bool preview = true)
    {
        if (_disposed) return;
        StateHasChanged();
        if (_module == null || _framePreview == null) return;
        ++_requestSequence;
        await _framePreview.InvokeVoidAsync("cancel");
        if (_disposed) return;
        await _sourceTimeline.UpdateAsync(TimelineModel(true), false);
        if (_disposed) return;
        await _resultTimeline.UpdateAsync(TimelineModel(false), false);
        if (_disposed) return;
        if (preview) await LoadPreviewAsync(PreviewResolution());
    }
    private async Task LoadPreviewAsync(MkvSplitFrameResolution frame)
    {
        if (_disposed || _framePreview == null) return;
        long sequence = ++_requestSequence, revision = _revision;
        Guid output = _outputId;
        _previewMessage = frame.IsValid ? T("loading") : T("empty"); StateHasChanged();
        try
        {
            bool committed = await _framePreview.InvokeAsync<bool>("load", frame.IsValid ? $"/api/split-preview/{RecordIndex}/input/{frame.SourceFrame}?sessionId={_snapshot.SessionId}&draftRevision={revision}&request={sequence}" : null);
            if (!_disposed && sequence == _requestSequence && revision == _revision && output == _outputId && committed) { _previewMessage = frame.IsValid ? "" : T("empty"); _displayedFrame = frame; }
        }
        catch (JSException) { if (!_disposed && sequence == _requestSequence && revision == _revision) _previewMessage = T("previewError"); }
        if (!_disposed) StateHasChanged();
    }
    private async Task ApplyAsync()
    {
        if (_busy || _snapshot == null || _gestureToken != null) return;
        _busy = true;
        try
        {
            var result = await SplitOrchestrator.ApplyDraftAsync(new() { SessionId = _snapshot.SessionId, ExpectedAppliedRevision = _snapshot.ExpectedAppliedRevision, ExpectedOptionsRevision = _snapshot.ExpectedOptionsRevision, DraftRevision = ++_revision, Document = _draft.Clone() }, _lifetime.Token);
            if (_disposed) return;
            if (result.Status == SplitApplyStatus.Applied) await FinishAsync();
            else _error = string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message));
        }
        finally { _busy = false; }
    }
    private async Task ResetAsync()
    {
        if (_busy || _confirming || _gestureToken != null || _snapshot == null) return;
        _confirming = true;
        if (_module != null) await _module.InvokeVoidAsync("rememberEditorFocus", _root);
        bool reset;
        try { reset = await DialogService.Confirm(T(_snapshot.Document.OriginMode == MkvSplitMode.Manual ? "restoreConfirm" : "resetConfirm"), T("title"), new() { OkButtonText = T("resetAction"), CancelButtonText = T("continue"), CloseDialogOnOverlayClick = false }) == true; }
        finally { _confirming = false; }
        if (!reset) { if (_module != null) await _module.InvokeVoidAsync("restoreEditorFocus", _root); return; }
        _busy = true;
        try
        {
            var result = await SplitOrchestrator.ReapplyRuleAsync(_snapshot.SessionId, _lifetime.Token);
            if (_disposed) return;
            if (result.Status == SplitApplyStatus.Applied) await FinishAsync();
            else _error = string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message));
        }
        finally { _busy = false; }
    }
    private async Task CloseAsync()
    {
        if (_disposed || _confirming) return;
        if (_gestureToken != null) { RestoreGestureCursor(); ClearGesture(); if (_module != null) await _module.InvokeVoidAsync("cancelEditorGestures", _root); await RefreshAsync(); }
        if (_snapshot != null && _busy) return;
        if (Dirty)
        {
            _confirming = true;
            if (_module != null) await _module.InvokeVoidAsync("rememberEditorFocus", _root);
            bool discard;
            try { discard = await DialogService.Confirm(T("discardConfirm"), T("title"), new() { OkButtonText = T("discard"), CancelButtonText = T("continue"), CloseDialogOnOverlayClick = false }) == true; }
            finally { _confirming = false; }
            if (!discard) { if (_module != null) await _module.InvokeVoidAsync("restoreEditorFocus", _root); return; }
        }
        await FinishAsync();
    }
    private async Task FinishAsync() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); ++_requestSequence; if (_framePreview != null) { try { await _framePreview.InvokeVoidAsync("cancel"); } catch (JSException) { } } if (_snapshot != null) SplitOrchestrator.CloseEditor(_snapshot.SessionId); await OnClose.InvokeAsync(); }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; _lifetime.Cancel(); ++_requestSequence;
        if (_initializingInterop && _interopFinished != null) await _interopFinished.Task;
        if (_snapshot != null) SplitOrchestrator.CloseEditor(_snapshot.SessionId);
        // Child disposal can precede completion of an outstanding AttachAsync.
        // Their DisposeAsync is idempotent; sweep once after interop has settled.
        if (_sourceTimeline != null) await _sourceTimeline.DisposeAsync();
        if (_resultTimeline != null) await _resultTimeline.DisposeAsync();
        foreach (var js in new[] { _keyboard, _framePreview }) if (js != null) { try { await js.InvokeVoidAsync("dispose"); await js.DisposeAsync(); } catch (JSException) { } }
        if (_module != null) { try { await _module.DisposeAsync(); } catch (JSException) { } }
        _reference?.Dispose(); _lifetime.Dispose();
    }
    private sealed class OutputView { public double Seconds { get; set; } public int In { get; set; } public int Out { get; set; } public Guid ClipId { get; set; } public HashSet<Guid> Selected { get; set; } = new(); public OutputView Clone() => new() { Seconds = Seconds, In = In, Out = Out, ClipId = ClipId, Selected = new(Selected) }; }
    private sealed record Checkpoint(MkvSplitDocument Document, Guid OutputId, Dictionary<Guid, OutputView> Views, int SourceFrame, int SourceIn, int SourceOut, bool SourcePreview);
}
