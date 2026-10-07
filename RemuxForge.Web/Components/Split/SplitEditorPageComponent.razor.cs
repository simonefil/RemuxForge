using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting;
using RemuxForge.Web.Components.Shared;
using RemuxForge.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Radzen;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Web.Components.Split
{
    /// <summary>
    /// Editor di montaggio Split: output multipli composti da clip del sorgente, con anteprima e timeline
    /// </summary>
    public partial class SplitEditorPageComponent
    {
        #region Variabili di classe

        /// <summary>
        /// Servizio di proiezione, risoluzione ed esecuzione dei comandi sul documento
        /// </summary>
        private readonly MkvSplitDocumentService _service = new MkvSplitDocumentService();

        /// <summary>
        /// Cancellazione legata alla vita del componente
        /// </summary>
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

        /// <summary>
        /// Stato di vista per ogni output
        /// </summary>
        private readonly Dictionary<Guid, OutputView> _views = new Dictionary<Guid, OutputView>();

        /// <summary>
        /// Pila degli stati annullabili
        /// </summary>
        private readonly Stack<Checkpoint> _undo = new Stack<Checkpoint>();

        /// <summary>
        /// Pila degli stati ripristinabili
        /// </summary>
        private readonly Stack<Checkpoint> _redo = new Stack<Checkpoint>();

        /// <summary>
        /// Snapshot di apertura fornito dall'orchestratore
        /// </summary>
        private SplitEditorSnapshot _snapshot;

        /// <summary>
        /// Documento in modifica
        /// </summary>
        private MkvSplitDocument _draft;

        /// <summary>
        /// Proiezione del documento in modifica
        /// </summary>
        private MkvSplitTimelineProjection _projection;

        /// <summary>
        /// Output selezionato
        /// </summary>
        private Guid _outputId;

        /// <summary>
        /// Output di destinazione per copia, spostamento e unione
        /// </summary>
        private Guid _destinationId;

        /// <summary>
        /// Frame sorgente sotto il cursore
        /// </summary>
        private int _sourceFrame;

        /// <summary>
        /// Inizio della selezione sul sorgente
        /// </summary>
        private int _sourceIn;

        /// <summary>
        /// Fine esclusiva della selezione sul sorgente
        /// </summary>
        private int _sourceOut;

        /// <summary>
        /// Inizio della clip nei campi di trim
        /// </summary>
        private int _trimIn;

        /// <summary>
        /// Fine esclusiva della clip nei campi di trim
        /// </summary>
        private int _trimOut;

        /// <summary>
        /// Nome dell'output nel campo di rinomina
        /// </summary>
        private string _name = "";

        /// <summary>
        /// Diagnostica mostrata all'utente
        /// </summary>
        private string _error = "";

        /// <summary>
        /// Messaggio sovrapposto all'anteprima
        /// </summary>
        private string _previewMessage = "";

        /// <summary>
        /// Modalità di visualizzazione audio
        /// </summary>
        private string _audioMode = "waveform";

        /// <summary>
        /// Guadagno della forma d'onda
        /// </summary>
        private double _gain = 1;

        /// <summary>
        /// Traccia audio mostrata nella timeline
        /// </summary>
        private int? _trackId;

        /// <summary>
        /// True durante apertura o applicazione
        /// </summary>
        private bool _busy;

        /// <summary>
        /// True dopo chiusura o dispose
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// True se l'anteprima mostra il sorgente invece del risultato
        /// </summary>
        private bool _sourcePreview;

        /// <summary>
        /// Revisione del documento in modifica
        /// </summary>
        private long _revision;

        /// <summary>
        /// Sequenza delle richieste di anteprima
        /// </summary>
        private long _requestSequence;

        /// <summary>
        /// Token del gesto JS in corso
        /// </summary>
        private string _gestureToken;

        /// <summary>
        /// Ultima sequenza ricevuta per il gesto in corso
        /// </summary>
        private long _gestureSequence;

        /// <summary>
        /// Stato catturato all'inizio del gesto
        /// </summary>
        private Checkpoint _gestureCheckpoint;

        /// <summary>
        /// Diagnostica presente all'inizio del gesto
        /// </summary>
        private string _gestureError;

        /// <summary>
        /// Proiezione provvisoria durante il gesto
        /// </summary>
        private MkvSplitTimelineProjection _gestureProjection;

        /// <summary>
        /// True mentre è aperto un dialog di conferma
        /// </summary>
        private bool _confirming;

        /// <summary>
        /// True durante l'inizializzazione dell'interop JS
        /// </summary>
        private bool _initializingInterop;

        /// <summary>
        /// True se l'interop JS è fallito
        /// </summary>
        private bool _interopFailed;

        /// <summary>
        /// Completamento dell'inizializzazione interop in corso
        /// </summary>
        private TaskCompletionSource _interopFinished;

        /// <summary>
        /// Frame effettivamente mostrato dall'anteprima
        /// </summary>
        private MkvSplitFrameResolution _displayedFrame;

        /// <summary>
        /// Elemento radice dell'editor
        /// </summary>
        private ElementReference _root;

        /// <summary>
        /// Pannello di anteprima frame
        /// </summary>
        private FramePreviewPaneComponent _preview;

        /// <summary>
        /// Timeline del sorgente
        /// </summary>
        private MediaTimelinePanelComponent _sourceTimeline;

        /// <summary>
        /// Timeline del risultato
        /// </summary>
        private MediaTimelinePanelComponent _resultTimeline;

        /// <summary>
        /// Modulo JS della timeline
        /// </summary>
        private IJSObjectReference _module;

        /// <summary>
        /// Oggetto JS dell'anteprima frame
        /// </summary>
        private IJSObjectReference _framePreview;

        /// <summary>
        /// Oggetto JS che cattura la tastiera dell'editor
        /// </summary>
        private IJSObjectReference _keyboard;

        /// <summary>
        /// Riferimento .NET passato al JS
        /// </summary>
        private DotNetObjectReference<SplitEditorPageComponent> _reference;

        /// <summary>
        /// Vista vuota usata quando l'output non ha stato
        /// </summary>
        private OutputView _emptyView = new OutputView();

        #endregion

        #region Proprietà

        /// <summary>
        /// Indice del record Split nella griglia
        /// </summary>
        [Parameter]
        public int RecordIndex { get; set; }

        /// <summary>
        /// Record Split modificato
        /// </summary>
        [Parameter]
        public MkvSplitRecord Record { get; set; }

        /// <summary>
        /// Segmento su cui aprire l'editor
        /// </summary>
        [Parameter]
        public int InitialSegmentNum { get; set; }

        /// <summary>
        /// Callback di chiusura dell'editor
        /// </summary>
        [Parameter]
        public EventCallback OnClose { get; set; }

        /// <summary>
        /// Output selezionato nel documento
        /// </summary>
        private MkvSplitOutput Output
        {
            get { return this._draft?.Outputs.Find(o => o.Id == this._outputId); }
        }

        /// <summary>
        /// Proiezione dell'output selezionato, provvisoria durante un gesto
        /// </summary>
        private MkvSplitOutputProjection Result
        {
            get { return (this._gestureProjection ?? this._projection)?.Outputs.Find(o => o.OutputId == this._outputId); }
        }

        /// <summary>
        /// Stato di vista dell'output selezionato
        /// </summary>
        private OutputView View
        {
            get { return this._views.TryGetValue(this._outputId, out OutputView view) ? view : this._emptyView; }
        }

        /// <summary>
        /// Clip selezionata
        /// </summary>
        private MkvSplitClip Clip
        {
            get { return this.Output?.Clips.Find(c => c.Id == this.View.ClipId); }
        }

        /// <summary>
        /// Clip successiva a quella selezionata
        /// </summary>
        private MkvSplitClip NextClip
        {
            get { return this.Clip == null ? null : this.Output.Clips.Skip(this.Output.Clips.IndexOf(this.Clip) + 1).FirstOrDefault(); }
        }

        /// <summary>
        /// True se la clip selezionata e la successiva condividono il confine
        /// </summary>
        private bool Contiguous
        {
            get { return this.NextClip != null && this.Clip.EndFrameExclusive == this.NextClip.StartFrame; }
        }

        /// <summary>
        /// True se il documento differisce da quello aperto
        /// </summary>
        private bool Dirty
        {
            get { return this._draft != null && JsonSerializer.Serialize(this._draft) != JsonSerializer.Serialize(this._snapshot.Document); }
        }

        #endregion

        #region Metodi pubblici

        /// <summary>
        /// Seek dalla timeline JS
        /// </summary>
        /// <param name="side">Timeline di origine: source o result</param>
        /// <param name="ms">Posizione in millisecondi</param>
        [JSInvokable]
        public async Task OnMontageSeek(string side, double ms)
        {
            if (this._disposed || this._snapshot == null)
                return;
            this._sourcePreview = side == "source";
            if (this._sourcePreview)
                this._sourceFrame = this._service.ResolveSource(this._snapshot.Analysis, Math.Clamp(ms / 1000, this._snapshot.Analysis.SourcePts[0], this._snapshot.Analysis.Duration)).SourceFrame;
            else
                this.View.Seconds = Math.Clamp(ms / 1000, 0, this.Result?.DurationSeconds ?? 0);
            await this.RefreshAsync();
        }

        /// <summary>
        /// Gesto dalla timeline JS. Ogni movimento si valuta sul draft invariato. Solo la fine applica, una volta.
        /// </summary>
        /// <param name="token">Token del gesto</param>
        /// <param name="phase">Fase: begin, move, end o cancel</param>
        /// <param name="kind">Tipo: seek, trim o reorder</param>
        /// <param name="side">Timeline di origine</param>
        /// <param name="clipId">Clip coinvolta</param>
        /// <param name="start">True se il trim riguarda l'inizio della clip</param>
        /// <param name="ms">Posizione in millisecondi</param>
        /// <param name="index">Indice di inserimento per il riordino</param>
        /// <param name="revision">Revisione del documento vista dal JS</param>
        /// <param name="outputId">Output visto dal JS</param>
        /// <param name="sequence">Sequenza dell'evento nel gesto</param>
        [JSInvokable]
        public async Task OnMontageGesture(string token, string phase, string kind, string side, string clipId, bool start, double ms, int index, long revision, string outputId, long sequence)
        {
            if (this._disposed || this._busy || revision != this._revision || outputId != this._outputId.ToString())
                return;
            if (phase == "begin")
            {
                if (this._gestureToken != null)
                    return;
                this._gestureToken = token;
                this._gestureSequence = sequence;
                this._gestureCheckpoint = this.Capture();
                this._gestureError = this._error;
            }
            else if (this._gestureToken != token || sequence <= this._gestureSequence)
                return;
            this._gestureSequence = sequence;
            if (phase == "cancel")
            {
                this.RestoreGestureCursor();
                this.ClearGesture();
                await this.RefreshAsync();
                return;
            }
            if (kind == "seek")
            {
                this._sourcePreview = side == "source";
                if (this._sourcePreview)
                    this._sourceFrame = this._service.ResolveSource(this._snapshot.Analysis, Math.Clamp(ms / 1000, this._snapshot.Analysis.SourcePts[0], this._snapshot.Analysis.Duration)).SourceFrame;
                else
                    this.View.Seconds = Math.Clamp(ms / 1000, 0, this._projection.Outputs.Find(o => o.OutputId == this._outputId)?.DurationSeconds ?? 0);
                if (phase == "end")
                    this.ClearGesture();
                await this.RefreshAsync();
                return;
            }
            if (!Guid.TryParse(clipId, out Guid id))
                return;
            MkvSplitClip clip = this.Output?.Clips.Find(c => c.Id == id);
            if (clip == null)
                return;
            int boundary = ms >= this._snapshot.Analysis.Duration * 1000 ? this._snapshot.Analysis.SourcePts.Length : this._service.ResolveSource(this._snapshot.Analysis, Math.Clamp(ms / 1000, this._snapshot.Analysis.SourcePts[0], this._snapshot.Analysis.Duration)).SourceFrame;
            MkvSplitEditCommand command = kind == "reorder"
                ? new MkvSplitEditCommand { Kind = MkvSplitEditKind.ReorderClips, OutputId = this._outputId, ClipIds = new List<Guid> { id }, InsertIndex = index }
                : new MkvSplitEditCommand { Kind = MkvSplitEditKind.TrimClip, OutputId = this._outputId, ClipId = id, StartFrame = start ? boundary : clip.StartFrame, EndFrameExclusive = start ? clip.EndFrameExclusive : boundary };
            MkvSplitEditResult candidate = this._service.Execute(this._draft, command, this._snapshot.Analysis);
            this._error = string.Join(Environment.NewLine, candidate.Diagnostics.Select(d => d.Message));
            if (phase == "end")
            {
                Checkpoint checkpoint = this._gestureCheckpoint;
                this.ClearGesture();
                await this.ExecuteAsync(command, checkpoint);
                if (!candidate.Changed)
                    await this.RefreshAsync();
                if (kind == "trim" && !this._disposed)
                {
                    MkvSplitFrameResolution finalFrame = this._service.ResolveClipBoundary(this._draft, this._snapshot.Analysis, this._outputId, id, start);
                    this.SetBoundaryCursor(finalFrame);
                    await this.RefreshAsync(false);
                    await this.LoadPreviewAsync(finalFrame);
                }
                return;
            }
            this._gestureProjection = this._service.Project(candidate.Document, this._snapshot.Analysis, this._snapshot.Options);
            this._sourcePreview = false;
            MkvSplitFrameResolution frame = kind == "reorder"
                ? this._service.ResolveResult(this._gestureProjection, this._snapshot.Analysis, this._outputId, Math.Min(this.View.Seconds, this.Result?.DurationSeconds ?? 0))
                : this._service.ResolveClipBoundary(candidate.Document, this._snapshot.Analysis, this._outputId, id, start);
            if (kind == "trim")
                this.SetBoundaryCursor(frame);
            await this.RefreshAsync(false);
            await this.LoadPreviewAsync(frame);
        }

        /// <summary>
        /// Passo di un frame dai pulsanti o dalla tastiera
        /// </summary>
        /// <param name="side">Timeline richiesta</param>
        /// <param name="delta">Numero di frame, con segno</param>
        [JSInvokable]
        public async Task OnFrameStep(string side, int delta)
        {
            if (this._disposed || this._snapshot == null)
                return;
            if (this._sourcePreview)
                this._sourceFrame = Math.Clamp(this._sourceFrame + delta, 0, this._snapshot.Analysis.SourcePts.Length - 1);
            else if (this.Result?.FrameCount > 0)
            {
                MkvSplitTimelineProjection projection = this._gestureProjection ?? this._projection;
                MkvSplitFrameResolution currentFrame = this._service.ResolveResultBoundary(projection, this._snapshot.Analysis, this._outputId, this.View.Seconds);
                if (currentFrame.IsValid)
                {
                    int targetFrame = Math.Clamp(currentFrame.ResultFrame + delta, 0, this.Result.FrameCount - 1);
                    MkvSplitFrameResolution steppedFrame = this._service.ResolveResultFrame(projection, this._snapshot.Analysis, this._outputId, targetFrame);
                    if (steppedFrame.IsValid)
                        this.View.Seconds = steppedFrame.ResultSeconds;
                }
            }
            await this.RefreshAsync();
        }

        /// <summary>
        /// Tasto catturato dall'editor
        /// </summary>
        /// <param name="key">Tasto premuto</param>
        /// <param name="shift">True se Shift è premuto</param>
        /// <param name="editing">True se il focus è in un campo modificabile</param>
        [JSInvokable]
        public async Task OnEditorKey(string key, bool shift, bool editing)
        {
            if (this._disposed)
                return;
            if (key == "Escape")
                await this.CloseAsync();
            else if (!editing && key == "Delete" && this.Clip != null)
                await this.EditAsync(MkvSplitEditKind.RemoveClips);
            else if (this._snapshot != null && !editing && (key is "Home" or "End"))
                await this.OnMontageSeek(this._sourcePreview ? "source" : "result",
                    key == "Home" ? 0 : (this._sourcePreview ? this._snapshot.Analysis.Duration : this.Result?.DurationSeconds ?? 0) * 1000);
        }

        /// <summary>
        /// Rilascia sessione, timeline e oggetti JS
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            this._disposed = true;
            this._lifetime.Cancel();
            ++this._requestSequence;
            if (this._initializingInterop && this._interopFinished != null)
                await this._interopFinished.Task;
            if (this._snapshot != null)
                this.SplitOrchestrator.CloseEditor(this._snapshot.SessionId);
            // Il dispose dei figli puo' precedere il completamento di un AttachAsync in corso.
            // Il loro DisposeAsync e' idempotente: si ripete una volta a interop concluso.
            if (this._sourceTimeline != null)
                await this._sourceTimeline.DisposeAsync();
            if (this._resultTimeline != null)
                await this._resultTimeline.DisposeAsync();
            foreach (IJSObjectReference js in new IJSObjectReference[] { this._keyboard, this._framePreview })
            {
                if (js != null)
                {
                    try
                    {
                        await js.InvokeVoidAsync("dispose");
                        await js.DisposeAsync();
                    }
                    catch (JSException)
                    {
                    }
                }
            }
            if (this._module != null)
            {
                try
                {
                    await this._module.DisposeAsync();
                }
                catch (JSException)
                {
                }
            }
            this._reference?.Dispose();
            this._lifetime.Dispose();
        }

        #endregion

        #region Metodi protetti

        /// <summary>
        /// Apre la sessione di modifica
        /// </summary>
        protected override async Task OnInitializedAsync()
        {
            await this.OpenAsync();
        }

        /// <summary>
        /// Inizializza l'interop JS e collega anteprima e timeline quando sono disponibili
        /// </summary>
        /// <param name="firstRender">True al primo render</param>
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (this._disposed || this._initializingInterop || this._interopFailed)
                return;
            this._initializingInterop = true;
            this._interopFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                if (this._module == null)
                {
                    this._module = await this.JsRuntime.InvokeAsync<IJSObjectReference>("import", "./js/split-timeline.js");
                    if (this._disposed)
                        return;
                    this._reference = DotNetObjectReference.Create(this);
                    this._keyboard = await this._module.InvokeAsync<IJSObjectReference>("captureEditorKeyboard", this._root, this._reference);
                    if (this._disposed)
                        return;
                    await this._module.InvokeVoidAsync("focusEditor", this._root);
                }
                if (this._snapshot != null && this._preview != null && (this._framePreview == null || !this._sourceTimeline.IsAttached || !this._resultTimeline.IsAttached))
                {
                    this._framePreview ??= await this._module.InvokeAsync<IJSObjectReference>("createFramePreview", this._preview.Canvas);
                    if (this._disposed)
                        return;
                    await this._sourceTimeline.AttachAsync(this._module, this._reference, this.TimelineModel(true));
                    if (this._disposed)
                        return;
                    await this._resultTimeline.AttachAsync(this._module, this._reference, this.TimelineModel(false));
                    await this.RefreshAsync();
                }
            }
            catch (JSException)
            {
                this._interopFailed = true;
                this._previewMessage = T("previewError");
                if (!this._disposed)
                    this.StateHasChanged();
            }
            finally
            {
                this._initializingInterop = false;
                this._interopFinished.TrySetResult();
            }
        }

        #endregion

        #region Metodi privati

        /// <summary>
        /// Testo localizzato dell'editor di montaggio
        /// </summary>
        /// <param name="key">Chiave relativa</param>
        /// <returns>Testo localizzato</returns>
        private static string T(string key)
        {
            return AppText.T("web.splitMontage." + key);
        }

        /// <summary>
        /// Formatta secondi come timestamp
        /// </summary>
        /// <param name="seconds">Secondi</param>
        /// <returns>Timestamp formattato</returns>
        private static string Time(double seconds)
        {
            return MkvSplitSegmentService.SecsToTs(seconds);
        }

        /// <summary>
        /// Apre la sessione dall'orchestratore e prepara documento e proiezione
        /// </summary>
        private async Task OpenAsync()
        {
            if (this._busy || this._disposed)
                return;
            this._busy = true;
            this._error = "";
            try
            {
                SplitEditorOpenResult opened = await this.SplitOrchestrator.OpenEditorAsync(this.RecordIndex, this._lifetime.Token);
                if (this._disposed)
                {
                    if (opened.Snapshot != null)
                        this.SplitOrchestrator.CloseEditor(opened.Snapshot.SessionId);
                    return;
                }
                this._snapshot = opened.Snapshot;
                if (this._snapshot == null)
                {
                    this._error = string.Join(Environment.NewLine, opened.Diagnostics.Select(d => d.Message));
                    return;
                }
                this._draft = this._snapshot.Document.Clone();
                this._projection = this._service.Project(this._draft, this._snapshot.Analysis, this._snapshot.Options);
                this._sourceOut = this._snapshot.Analysis.SourcePts.Length;
                this._trackId = this._snapshot.SourceInfo.Tracks.FirstOrDefault(t => t.Type == "audio")?.Id;
                this.SelectOutput(this._draft.Outputs.ElementAtOrDefault(Math.Max(0, this.InitialSegmentNum - 1))?.Id ?? this._draft.Outputs.FirstOrDefault()?.Id ?? Guid.Empty);
            }
            finally
            {
                this._busy = false;
            }
        }

        /// <summary>
        /// Riprova l'anteprima: reinizializza l'interop se era fallito, altrimenti ricarica
        /// </summary>
        private async Task RetryPreviewAsync()
        {
            if (this._interopFailed)
            {
                this._interopFailed = false;
                this.StateHasChanged();
            }
            else
                await this.RefreshAsync();
        }

        /// <summary>
        /// Seleziona un output e riallinea clip, selezione, cursore e campi
        /// </summary>
        /// <param name="id">Output da selezionare</param>
        private void SelectOutput(Guid id)
        {
            this._outputId = id;
            if (!this._views.ContainsKey(id))
                this._views[id] = new OutputView();
            if (this.Output?.Clips.Any(c => c.Id == this.View.ClipId) != true)
                this.View.ClipId = this.Output?.Clips.FirstOrDefault()?.Id ?? Guid.Empty;
            this.View.Selected.RemoveWhere(id => this.Output?.Clips.Any(c => c.Id == id) != true);
            this.View.Seconds = Math.Clamp(this.View.Seconds, 0, this.Result?.DurationSeconds ?? 0);
            this._name = this.Result?.FileName ?? "";
            this._destinationId = this._draft.Outputs.FirstOrDefault(o => o.Id != id)?.Id ?? Guid.Empty;
            this.SetTrimFields();
        }

        /// <summary>
        /// Riporta nei campi di trim i confini della clip selezionata
        /// </summary>
        private void SetTrimFields()
        {
            this._trimIn = this.Clip?.StartFrame ?? 0;
            this._trimOut = this.Clip?.EndFrameExclusive ?? 0;
        }

        /// <summary>
        /// Seleziona un output annullando l'eventuale gesto in corso
        /// </summary>
        /// <param name="id">Output da selezionare</param>
        private async Task SelectOutputAsync(Guid id)
        {
            if (this._gestureToken != null)
            {
                this.RestoreGestureCursor();
                this.ClearGesture();
                await this._module.InvokeVoidAsync("cancelEditorGestures", this._root);
            }
            this.SelectOutput(id);
            this._sourcePreview = false;
            await this.RefreshAsync();
        }

        /// <summary>
        /// Seleziona una clip
        /// </summary>
        /// <param name="id">Clip da selezionare</param>
        private async Task SelectClipAsync(Guid id)
        {
            this.View.ClipId = id;
            this.SetTrimFields();
            await this.RefreshAsync();
        }

        /// <summary>
        /// Aggiunge o toglie una clip dalla selezione multipla
        /// </summary>
        /// <param name="id">Clip</param>
        /// <param name="args">Evento della checkbox</param>
        private void ToggleClipSelection(Guid id, ChangeEventArgs args)
        {
            if (args.Value is true)
                this.View.Selected.Add(id);
            else
                this.View.Selected.Remove(id);
        }

        /// <summary>
        /// Clip selezionate nell'ordine dell'output, oppure la sola clip corrente
        /// </summary>
        /// <returns>Identificativi delle clip</returns>
        private List<Guid> SelectedClipIds()
        {
            return this.View.Selected.Count > 0
                ? this.Output.Clips.Where(c => this.View.Selected.Contains(c.Id)).Select(c => c.Id).ToList()
                : this.Clip == null ? new List<Guid>() : new List<Guid> { this.Clip.Id };
        }

        /// <summary>
        /// Sceglie se l'anteprima mostra il sorgente o il risultato
        /// </summary>
        /// <param name="source">True per il sorgente</param>
        private async Task SetPreviewSideAsync(bool source)
        {
            this._sourcePreview = source;
            await this.RefreshAsync();
        }

        /// <summary>
        /// Segna inizio o fine della selezione sul sorgente al frame corrente
        /// </summary>
        /// <param name="start">True per l'inizio</param>
        private async Task MarkSourceAsync(bool start)
        {
            if (start)
                this._sourceIn = this._sourceFrame;
            else
                this._sourceOut = this._sourceFrame + 1;
            await this.RefreshAsync();
        }

        /// <summary>
        /// Segna inizio o fine della selezione sul risultato al frame corrente
        /// </summary>
        /// <param name="start">True per l'inizio</param>
        private async Task MarkResultAsync(bool start)
        {
            if (start)
                this.View.In = this.ResultFrame();
            else
                this.View.Out = this.ResultFrame();
            await this.RefreshAsync();
        }

        /// <summary>
        /// Aggiorna timeline e anteprima dopo un cambio di selezione nei campi
        /// </summary>
        private async Task SelectionChangedAsync()
        {
            await this.RefreshAsync();
        }

        /// <summary>
        /// Confine del risultato al cursore corrente
        /// </summary>
        /// <returns>Frame del risultato</returns>
        private int ResultFrame()
        {
            return this._service.ResolveResultBoundary(this._projection, this._snapshot.Analysis, this._outputId, this.View.Seconds).ResultFrame;
        }

        /// <summary>
        /// Rinomina l'output con il nome personalizzato
        /// </summary>
        private Task RenameAsync()
        {
            return this.EditAsync(MkvSplitEditKind.RenameOutput, nameMode: MkvSplitNameMode.Custom);
        }

        /// <summary>
        /// Applica i campi di trim alla clip
        /// </summary>
        private Task TrimAsync()
        {
            return this.EditAsync(MkvSplitEditKind.TrimClip);
        }

        /// <summary>
        /// Porta l'inizio della clip sul keyframe secondo lo snap configurato
        /// </summary>
        private async Task SnapStartAsync()
        {
            if (this.Clip == null || this._busy || this._gestureToken != null || this._snapshot.Options.Snap == MkvSplitSnapMode.Off)
                return;
            MkvSplitSegment segment = new MkvSplitSegment
            {
                Num = 1,
                StartFrame = this.Clip.StartFrame,
                FrameCount = this.Clip.EndFrameExclusive - this.Clip.StartFrame,
                StartTs = this._snapshot.Analysis.SourcePts[this.Clip.StartFrame],
                EndTs = MkvSplitDocumentService.BoundarySeconds(this._snapshot.Analysis, this.Clip.EndFrameExclusive)
            };
            MkvSplitSegmentService service = new MkvSplitSegmentService();
            service.ApplySnap(new List<MkvSplitSegment> { segment }, this._snapshot.Analysis.KeyFlags, this._snapshot.Analysis.SourcePts, this._snapshot.Options.Snap);
            if (service.Warnings.Count > 0)
            {
                this._error = string.Join(Environment.NewLine, service.Warnings.Select(w => w.Message));
                return;
            }
            await this.ExecuteAsync(new MkvSplitEditCommand
            {
                Kind = MkvSplitEditKind.TrimClip,
                OutputId = this._outputId,
                ClipId = this.Clip.Id,
                StartFrame = segment.StartFrame,
                EndFrameExclusive = segment.StartFrame + segment.FrameCount
            });
        }

        /// <summary>
        /// Costruisce ed esegue un comando dallo stato corrente dell'editor
        /// </summary>
        /// <param name="kind">Tipo di modifica</param>
        /// <param name="nameMode">Modalità del nome per la rinomina</param>
        private async Task EditAsync(MkvSplitEditKind kind, MkvSplitNameMode nameMode = MkvSplitNameMode.Automatic)
        {
            MkvSplitOutput destination = this._draft?.Outputs.Find(o => o.Id == this._destinationId);
            MkvSplitFrameResolution splitCursor = kind == MkvSplitEditKind.SplitClip
                ? this._service.ResolveResultBoundary(this._projection, this._snapshot.Analysis, this._outputId, this.View.Seconds)
                : null;
            await this.ExecuteAsync(new MkvSplitEditCommand
            {
                Kind = kind,
                OutputId = this._outputId,
                ClipId = kind == MkvSplitEditKind.SplitClip ? splitCursor.ClipId ?? Guid.Empty : this.Clip?.Id ?? Guid.Empty,
                NextClipId = this.NextClip?.Id ?? Guid.Empty,
                OutputIds = kind == MkvSplitEditKind.MergeOutputs ? new List<Guid> { this._outputId, this._destinationId } : new List<Guid> { this._outputId },
                ClipIds = this.SelectedClipIds(),
                DestinationOutputId = this._destinationId,
                InsertIndex = kind is MkvSplitEditKind.CreateEmptyOutput or MkvSplitEditKind.CreateOutputFromSource ? this._draft.Outputs.Count : destination?.Clips.Count ?? 0,
                StartFrame = kind == MkvSplitEditKind.TrimClip ? this._trimIn : kind == MkvSplitEditKind.MoveSharedBoundary ? this._trimOut : this._sourceIn,
                EndFrameExclusive = kind == MkvSplitEditKind.TrimClip ? this._trimOut : this._sourceOut,
                ResultFrame = kind == MkvSplitEditKind.RemoveResultRange ? this.View.In : splitCursor?.ResultFrame ?? this.ResultFrame(),
                ResultEndFrameExclusive = this.View.Out,
                NameMode = nameMode,
                CustomFileName = this._name
            });
        }

        /// <summary>
        /// Sposta l'output selezionato nella lista
        /// </summary>
        /// <param name="delta">Spostamento, con segno</param>
        private async Task ReorderOutputAsync(int delta)
        {
            if (this.Output == null)
                return;
            int index = this._draft.Outputs.IndexOf(this.Output);
            int target = index + delta;
            if (target < 0 || target >= this._draft.Outputs.Count)
                return;
            await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.ReorderOutputs, OutputIds = new List<Guid> { this._outputId }, InsertIndex = target });
        }

        /// <summary>
        /// Sposta le clip selezionate nell'output
        /// </summary>
        /// <param name="delta">Spostamento, con segno</param>
        private async Task ReorderClipAsync(int delta)
        {
            if (this.Clip == null)
                return;
            List<Guid> selected = this.SelectedClipIds();
            int index = this.Output.Clips.FindIndex(c => selected.Contains(c.Id));
            int target = index + delta;
            if (target < 0 || target > this.Output.Clips.Count - selected.Count)
                return;
            await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.ReorderClips, OutputId = this._outputId, ClipIds = selected, InsertIndex = target });
        }

        /// <summary>
        /// Esegue un comando sul documento e registra lo stato precedente per l'annullamento
        /// </summary>
        /// <param name="command">Comando da eseguire</param>
        /// <param name="checkpoint">Stato da registrare, quello corrente se null</param>
        private async Task ExecuteAsync(MkvSplitEditCommand command, Checkpoint checkpoint = null)
        {
            if (this._busy || this._disposed || this._draft == null || this._gestureToken != null)
                return;
            this._gestureProjection = null;
            MkvSplitEditResult result = this._service.Execute(this._draft, command, this._snapshot.Analysis);
            this._error = string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message));
            if (!result.Changed)
                return;
            this._undo.Push(checkpoint ?? this.Capture());
            this._redo.Clear();
            this._draft = result.Document;
            this._revision++;
            this._projection = this._service.Project(this._draft, this._snapshot.Analysis, this._snapshot.Options);
            this.SelectOutput(result.SelectedOutputId ?? (this._draft.Outputs.Any(o => o.Id == this._outputId) ? this._outputId : this._draft.Outputs.FirstOrDefault()?.Id ?? Guid.Empty));
            if (result.SelectedClipId.HasValue)
                this.View.ClipId = result.SelectedClipId.Value;
            this.SetTrimFields();
            await this.RefreshAsync();
        }

        /// <summary>
        /// Cattura lo stato corrente dell'editor
        /// </summary>
        /// <returns>Stato catturato</returns>
        private Checkpoint Capture()
        {
            return new Checkpoint(this._draft.Clone(), this._outputId, this._views.ToDictionary(p => p.Key, p => p.Value.Clone()), this._sourceFrame, this._sourceIn, this._sourceOut, this._sourcePreview);
        }

        /// <summary>
        /// Annulla o ripristina l'ultima modifica
        /// </summary>
        /// <param name="redo">True per ripristinare</param>
        private async Task HistoryAsync(bool redo)
        {
            Stack<Checkpoint> source = redo ? this._redo : this._undo;
            if (this._busy || source.Count == 0)
                return;
            if (this._gestureToken != null)
            {
                this.RestoreGestureCursor();
                this.ClearGesture();
                await this._module.InvokeVoidAsync("cancelEditorGestures", this._root);
            }
            this.ClearGesture();
            (redo ? this._undo : this._redo).Push(this.Capture());
            Checkpoint checkpoint = source.Pop();
            this._draft = checkpoint.Document;
            this._views.Clear();
            foreach (KeyValuePair<Guid, OutputView> item in checkpoint.Views)
                this._views[item.Key] = item.Value;
            this._sourceFrame = checkpoint.SourceFrame;
            this._sourceIn = checkpoint.SourceIn;
            this._sourceOut = checkpoint.SourceOut;
            this._sourcePreview = checkpoint.SourcePreview;
            this._projection = this._service.Project(this._draft, this._snapshot.Analysis, this._snapshot.Options);
            this._revision++;
            this.SelectOutput(checkpoint.OutputId);
            this._error = "";
            await this.RefreshAsync();
        }

        /// <summary>
        /// Porta il cursore del risultato sul confine di clip risolto
        /// </summary>
        /// <param name="frame">Confine risolto</param>
        private void SetBoundaryCursor(MkvSplitFrameResolution frame)
        {
            if (!frame.IsValid || !frame.ClipId.HasValue)
                return;
            MkvSplitFrameResolution cursor = this._service.ResolveClipSourceFrame(this._gestureProjection ?? this._projection, this._snapshot.Analysis,
                this._outputId, frame.ClipId.Value, frame.SourceFrame);
            if (cursor.IsValid)
                this.View.Seconds = cursor.ResultSeconds;
        }

        /// <summary>
        /// Ripristina cursore, diagnostica e vista catturati all'inizio del gesto
        /// </summary>
        private void RestoreGestureCursor()
        {
            if (this._gestureCheckpoint == null)
                return;
            this._sourceFrame = this._gestureCheckpoint.SourceFrame;
            this._error = this._gestureError;
            this._sourcePreview = this._gestureCheckpoint.SourcePreview;
            if (this._gestureCheckpoint.Views.TryGetValue(this._outputId, out OutputView view))
                this._views[this._outputId] = view.Clone();
        }

        /// <summary>
        /// Azzera lo stato del gesto in corso
        /// </summary>
        private void ClearGesture()
        {
            this._gestureToken = null;
            this._gestureCheckpoint = null;
            this._gestureProjection = null;
        }

        /// <summary>
        /// Modello passato alla timeline JS
        /// </summary>
        /// <param name="source">True per la timeline del sorgente</param>
        /// <returns>Modello serializzabile</returns>
        private object TimelineModel(bool source)
        {
            MkvSplitAnalysis analysis = this._snapshot.Analysis;
            return new
            {
                side = source ? "source" : "result",
                outputId = this._outputId,
                revision = this._revision,
                durationMs = (source ? analysis.Duration : this.Result?.DurationSeconds ?? 0) * 1000,
                sourceDurationMs = analysis.Duration * 1000,
                playheadMs = (source ? analysis.SourcePts[this._sourceFrame] : this.View.Seconds) * 1000,
                audioMode = this._audioMode,
                waveformGain = this._gain,
                precisionMode = true,
                nyquistHz = (this._snapshot.SourceInfo.Tracks.FirstOrDefault(t => t.Id == this._trackId)?.SamplingFrequency ?? 48000) / 2.0,
                selectionStartMs = source ? MkvSplitDocumentService.BoundarySeconds(analysis, Math.Clamp(this._sourceIn, 0, analysis.SourcePts.Length)) * 1000 : this.ResultBoundarySeconds(this.View.In) * 1000,
                selectionEndMs = source ? MkvSplitDocumentService.BoundarySeconds(analysis, Math.Clamp(this._sourceOut, 0, analysis.SourcePts.Length)) * 1000 : this.ResultBoundarySeconds(this.View.Out) * 1000,
                keyframes = source ? analysis.KeyFlags.Select((flag, index) => new { flag.Key, index }).Where(f => f.Key && f.index < analysis.SourcePts.Length).Select(f => analysis.SourcePts[f.index] * 1000).ToArray() : Array.Empty<double>(),
                audioUrl = this._trackId.HasValue ? $"/api/split-audio/{this.RecordIndex}/input?trackId={this._trackId}&durationMs={Math.Ceiling(analysis.Duration * 1000)}&mode={this._audioMode}&quality=high&sessionId={this._snapshot.SessionId}" : null,
                chapters = (source ? analysis.Chapters : this.Result?.Chapters ?? new List<ChapterMark>()).Select(c => new { timeMs = c.StartSeconds * 1000, name = c.Name }).ToArray(),
                clips = (this.Result?.Clips ?? new List<MkvSplitClipProjection>()).Select((c, i) => new { id = c.ClipId, startMs = c.ResultStartSeconds * 1000, endMs = c.ResultEndSeconds * 1000, sourceStartMs = c.SourceStartSeconds * 1000, sourceEndMs = c.SourceEndSeconds * 1000, selected = c.ClipId == this.View.ClipId, label = (i + 1).ToString() }).ToArray()
            };
        }

        /// <summary>
        /// Secondi del confine di risultato indicato
        /// </summary>
        /// <param name="frame">Confine nel risultato</param>
        /// <returns>Secondi del confine, 0 se non risolvibile</returns>
        private double ResultBoundarySeconds(int frame)
        {
            if (this.Result == null || this.Result.Clips.Count == 0)
                return 0;
            MkvSplitFrameResolution boundary = this._service.ResolveResultFrame(this._gestureProjection ?? this._projection, this._snapshot.Analysis,
                this._outputId, Math.Clamp(frame, 0, this.Result.FrameCount), boundary: true);
            return boundary.IsValid ? boundary.ResultSeconds : 0;
        }

        /// <summary>
        /// Corsie audio della timeline
        /// </summary>
        /// <param name="side">Timeline: source o result</param>
        /// <returns>Corsie audio</returns>
        private List<MediaTimelinePanelComponent.AudioLane> AudioLanes(string side)
        {
            return new List<MediaTimelinePanelComponent.AudioLane>
            {
                new MediaTimelinePanelComponent.AudioLane
                {
                    ElementId = "split-audio-" + side,
                    Label = T("audio"),
                    Tracks = this._snapshot.SourceInfo.Tracks.Where(t => t.Type == "audio").ToList(),
                    SelectedTrackId = this._trackId,
                    OnTrackChanged = EventCallback.Factory.Create<ChangeEventArgs>(this, async e =>
                    {
                        if (int.TryParse(e.Value?.ToString(), out int id))
                            this._trackId = id;
                        await this.RefreshAsync();
                    })
                }
            };
        }

        /// <summary>
        /// Cambia la modalità di visualizzazione audio
        /// </summary>
        /// <param name="mode">Modalità</param>
        private async Task AudioModeAsync(string mode)
        {
            this._audioMode = mode;
            await this.RefreshAsync();
        }

        /// <summary>
        /// Cambia il guadagno della forma d'onda
        /// </summary>
        /// <param name="value">Guadagno</param>
        private async Task GainAsync(double value)
        {
            this._gain = value;
            await this.RefreshAsync();
        }

        /// <summary>
        /// Frame da mostrare in anteprima per il lato corrente
        /// </summary>
        /// <returns>Frame risolto</returns>
        private MkvSplitFrameResolution PreviewResolution()
        {
            return this._sourcePreview
                ? this._service.ResolveSource(this._snapshot.Analysis, this._snapshot.Analysis.SourcePts[this._sourceFrame])
                : this._service.ResolveResult(this._projection, this._snapshot.Analysis, this._outputId, this.View.Seconds);
        }

        /// <summary>
        /// Riga informativa sotto l'anteprima
        /// </summary>
        /// <returns>Testo informativo</returns>
        private string PreviewMeta()
        {
            return this._snapshot == null
                ? ""
                : T("source") + " " + Time((this._displayedFrame ?? this.PreviewResolution()).SourceSeconds) + " · " + T("frame") + " " + (this._displayedFrame ?? this.PreviewResolution()).SourceFrame + " · " + T("result") + " " + Time(this.View.Seconds);
        }

        /// <summary>
        /// Ridisegna il componente, aggiorna le timeline e ricarica l'anteprima
        /// </summary>
        /// <param name="preview">True per ricaricare anche l'anteprima</param>
        private async Task RefreshAsync(bool preview = true)
        {
            if (this._disposed)
                return;
            this.StateHasChanged();
            if (this._module == null || this._framePreview == null)
                return;
            ++this._requestSequence;
            await this._framePreview.InvokeVoidAsync("cancel");
            if (this._disposed)
                return;
            await this._sourceTimeline.UpdateAsync(this.TimelineModel(true), false);
            if (this._disposed)
                return;
            await this._resultTimeline.UpdateAsync(this.TimelineModel(false), false);
            if (this._disposed)
                return;
            if (preview)
                await this.LoadPreviewAsync(this.PreviewResolution());
        }

        /// <summary>
        /// Carica un frame nell'anteprima scartando le risposte superate
        /// </summary>
        /// <param name="frame">Frame da caricare</param>
        private async Task LoadPreviewAsync(MkvSplitFrameResolution frame)
        {
            if (this._disposed || this._framePreview == null)
                return;
            long sequence = ++this._requestSequence;
            long revision = this._revision;
            Guid output = this._outputId;
            this._previewMessage = frame.IsValid ? T("loading") : this.UnresolvedPreviewMessage();
            this.StateHasChanged();
            try
            {
                bool committed = await this._framePreview.InvokeAsync<bool>("load", frame.IsValid ? $"/api/split-preview/{this.RecordIndex}/input/{frame.SourceFrame}?sessionId={this._snapshot.SessionId}&draftRevision={revision}&request={sequence}" : null);
                if (!this._disposed && sequence == this._requestSequence && revision == this._revision && output == this._outputId && committed)
                {
                    this._previewMessage = frame.IsValid ? "" : this.UnresolvedPreviewMessage();
                    this._displayedFrame = frame;
                }
            }
            catch (JSException)
            {
                if (!this._disposed && sequence == this._requestSequence && revision == this._revision)
                    this._previewMessage = T("previewError");
            }
            if (!this._disposed)
                this.StateHasChanged();
        }

        /// <summary>
        /// Messaggio per un frame non risolto: output senza clip e frame non risolvibile sono casi diversi per l'utente
        /// </summary>
        /// <returns>Messaggio localizzato</returns>
        private string UnresolvedPreviewMessage()
        {
            return !this._sourcePreview && (this.Result == null || this.Result.Clips.Count == 0) ? T("empty") : T("frameUnavailable");
        }

        /// <summary>
        /// Applica il documento al record tramite l'orchestratore
        /// </summary>
        private async Task ApplyAsync()
        {
            if (this._busy || this._snapshot == null || this._gestureToken != null)
                return;
            this._busy = true;
            try
            {
                SplitApplyResult result = await this.SplitOrchestrator.ApplyDraftAsync(new SplitApplyRequest
                {
                    SessionId = this._snapshot.SessionId,
                    ExpectedAppliedRevision = this._snapshot.ExpectedAppliedRevision,
                    ExpectedOptionsRevision = this._snapshot.ExpectedOptionsRevision,
                    DraftRevision = ++this._revision,
                    Document = this._draft.Clone()
                }, this._lifetime.Token);
                if (this._disposed)
                    return;
                if (result.Status == SplitApplyStatus.Applied)
                    await this.FinishAsync();
                else
                {
                    this._error = string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message));
                    // La revisione e' avanzata con la richiesta: le timeline JS devono riceverla o scarterebbero ogni gesto
                    await this.RefreshAsync();
                }
            }
            finally
            {
                this._busy = false;
            }
        }

        /// <summary>
        /// Dopo conferma riapplica la regola o ripristina il sorgente
        /// </summary>
        private async Task ResetAsync()
        {
            if (this._busy || this._confirming || this._gestureToken != null || this._snapshot == null)
                return;
            this._confirming = true;
            if (this._module != null)
                await this._module.InvokeVoidAsync("rememberEditorFocus", this._root);
            bool reset;
            try
            {
                reset = await this.DialogService.Confirm(T(this._snapshot.Document.OriginMode == MkvSplitMode.Manual ? "restoreConfirm" : "resetConfirm"), T("title"),
                    new ConfirmOptions { OkButtonText = T("resetAction"), CancelButtonText = T("continue"), CloseDialogOnOverlayClick = false }) == true;
            }
            finally
            {
                this._confirming = false;
            }
            if (!reset)
            {
                if (this._module != null)
                    await this._module.InvokeVoidAsync("restoreEditorFocus", this._root);
                return;
            }
            this._busy = true;
            try
            {
                SplitApplyResult result = await this.SplitOrchestrator.ReapplyRuleAsync(this._snapshot.SessionId, this._lifetime.Token);
                if (this._disposed)
                    return;
                if (result.Status == SplitApplyStatus.Applied)
                    await this.FinishAsync();
                else
                    this._error = string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message));
            }
            finally
            {
                this._busy = false;
            }
        }

        /// <summary>
        /// Chiude l'editor, chiedendo conferma se ci sono modifiche non applicate
        /// </summary>
        private async Task CloseAsync()
        {
            if (this._disposed || this._confirming)
                return;
            if (this._gestureToken != null)
            {
                this.RestoreGestureCursor();
                this.ClearGesture();
                if (this._module != null)
                    await this._module.InvokeVoidAsync("cancelEditorGestures", this._root);
                await this.RefreshAsync();
            }
            if (this._snapshot != null && this._busy)
                return;
            if (this.Dirty)
            {
                this._confirming = true;
                if (this._module != null)
                    await this._module.InvokeVoidAsync("rememberEditorFocus", this._root);
                bool discard;
                try
                {
                    discard = await this.DialogService.Confirm(T("discardConfirm"), T("title"),
                        new ConfirmOptions { OkButtonText = T("discard"), CancelButtonText = T("continue"), CloseDialogOnOverlayClick = false }) == true;
                }
                finally
                {
                    this._confirming = false;
                }
                if (!discard)
                {
                    if (this._module != null)
                        await this._module.InvokeVoidAsync("restoreEditorFocus", this._root);
                    return;
                }
            }
            await this.FinishAsync();
        }

        /// <summary>
        /// Chiude sessione e anteprima e notifica la chiusura
        /// </summary>
        private async Task FinishAsync()
        {
            if (this._disposed)
                return;
            this._disposed = true;
            this._lifetime.Cancel();
            ++this._requestSequence;
            if (this._framePreview != null)
            {
                try
                {
                    await this._framePreview.InvokeVoidAsync("cancel");
                }
                catch (JSException)
                {
                }
            }
            if (this._snapshot != null)
                this.SplitOrchestrator.CloseEditor(this._snapshot.SessionId);
            await this.OnClose.InvokeAsync();
        }

        #endregion

        #region Tipi annidati

        /// <summary>
        /// Stato di vista di un output: cursore, selezione sul risultato e clip selezionate
        /// </summary>
        private sealed class OutputView
        {
            #region Proprietà

            /// <summary>
            /// Cursore nel risultato, in secondi
            /// </summary>
            public double Seconds { get; set; }

            /// <summary>
            /// Inizio della selezione nel risultato
            /// </summary>
            public int In { get; set; }

            /// <summary>
            /// Fine esclusiva della selezione nel risultato
            /// </summary>
            public int Out { get; set; }

            /// <summary>
            /// Clip corrente
            /// </summary>
            public Guid ClipId { get; set; }

            /// <summary>
            /// Clip della selezione multipla
            /// </summary>
            public HashSet<Guid> Selected { get; set; } = new HashSet<Guid>();

            #endregion

            #region Metodi pubblici

            /// <summary>
            /// Copia indipendente della vista
            /// </summary>
            /// <returns>Vista copiata</returns>
            public OutputView Clone()
            {
                return new OutputView { Seconds = this.Seconds, In = this.In, Out = this.Out, ClipId = this.ClipId, Selected = new HashSet<Guid>(this.Selected) };
            }

            #endregion
        }

        /// <summary>
        /// Stato dell'editor registrato per annulla e ripristina
        /// </summary>
        /// <param name="Document">Documento</param>
        /// <param name="OutputId">Output selezionato</param>
        /// <param name="Views">Viste per output</param>
        /// <param name="SourceFrame">Frame sorgente sotto il cursore</param>
        /// <param name="SourceIn">Inizio della selezione sul sorgente</param>
        /// <param name="SourceOut">Fine esclusiva della selezione sul sorgente</param>
        /// <param name="SourcePreview">True se l'anteprima mostrava il sorgente</param>
        private sealed record Checkpoint(MkvSplitDocument Document, Guid OutputId, Dictionary<Guid, OutputView> Views, int SourceFrame, int SourceIn, int SourceOut, bool SourcePreview);

        #endregion
    }
}
