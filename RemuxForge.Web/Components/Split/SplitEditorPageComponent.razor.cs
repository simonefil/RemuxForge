using RemuxForge.Core.Localization;
using RemuxForge.Core.Models;
using RemuxForge.Core.Splitting;
using RemuxForge.Web.Components.Shared;
using RemuxForge.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Radzen;
using Radzen.Blazor;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RemuxForge.Web.Components.Split
{
    /// <summary>
    /// Editor di taglio Split: una timeline del sorgente con i segmenti di tutti i file di uscita,
    /// anteprima del sorgente o di un file già montato ed elenco dei file con i loro segmenti
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
        /// Output attivo: quello dei segmenti selezionati e dell'anteprima montata
        /// </summary>
        private Guid _outputId;

        /// <summary>
        /// Frame sorgente sotto il cursore
        /// </summary>
        private int _sourceFrame;

        /// <summary>
        /// Inizio del tratto segnato con I, null se non segnato
        /// </summary>
        private int? _markIn;

        /// <summary>
        /// Fine esclusiva del tratto segnato con O, null se non segnata
        /// </summary>
        private int? _markOut;

        /// <summary>
        /// Output in rinomina, null se nessuno
        /// </summary>
        private Guid? _renamingId;

        /// <summary>
        /// Nome digitato nel campo di rinomina
        /// </summary>
        private string _name = "";

        /// <summary>
        /// Elemento trascinato nel pannello dei file
        /// </summary>
        private DragPayload _drag;

        /// <summary>
        /// Diagnostiche dell'ultimo comando, mostrate insieme a quelle della proiezione
        /// </summary>
        private List<MkvSplitDiagnostic> _feedback = new List<MkvSplitDiagnostic>();

        /// <summary>
        /// Errore di apertura dell'editor
        /// </summary>
        private string _openError = "";

        /// <summary>
        /// Testo di stato durante un'operazione asincrona
        /// </summary>
        private string _busyText = "";

        /// <summary>
        /// Selettore dell'elemento da mettere a fuoco dopo il prossimo render
        /// </summary>
        private string _pendingFocus;

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
        /// True se l'anteprima mostra il sorgente, false se mostra l'output attivo già montato
        /// </summary>
        private bool _sourcePreview = true;

        /// <summary>
        /// True mentre un menu contestuale dell'editor è aperto
        /// </summary>
        private bool _menuOpen;

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
        /// Diagnostiche presenti all'inizio del gesto
        /// </summary>
        private List<MkvSplitDiagnostic> _gestureFeedback;

        /// <summary>
        /// Documento provvisorio durante il gesto
        /// </summary>
        private MkvSplitDocument _gestureDocument;

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
        private MediaTimelinePanelComponent _timeline;

        /// <summary>
        /// Modulo JS della timeline
        /// </summary>
        private IJSObjectReference _module;

        /// <summary>
        /// Oggetto JS dell'anteprima frame
        /// </summary>
        private IJSObjectReference _framePreview;

        /// <summary>
        /// Oggetto JS che cattura tastiera e trascinamento dell'editor
        /// </summary>
        private IJSObjectReference _input;

        /// <summary>
        /// Riferimento .NET passato al JS
        /// </summary>
        private DotNetObjectReference<SplitEditorPageComponent> _reference;

        /// <summary>
        /// Vista vuota usata quando l'output non ha stato
        /// </summary>
        private OutputView _emptyView = new OutputView();

        /// <summary>
        /// Frame sorgente dei keyframe, in ordine crescente
        /// </summary>
        private int[] _keyframes = Array.Empty<int>();

        /// <summary>
        /// Versione dei campi Inizio e Fine: cambia per riportarli al valore del segmento dopo un tempo non applicato
        /// </summary>
        private int _timeFieldsVersion;

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
        /// Callback di chiusura dell'editor
        /// </summary>
        [Parameter]
        public EventCallback OnClose { get; set; }

        /// <summary>
        /// Output attivo nel documento
        /// </summary>
        private MkvSplitOutput Output
        {
            get { return this._draft?.Outputs.Find(o => o.Id == this._outputId); }
        }

        /// <summary>
        /// Documento mostrato: quello provvisorio durante un gesto, altrimenti il draft
        /// </summary>
        private MkvSplitDocument Document
        {
            get { return this._gestureDocument ?? this._draft; }
        }

        /// <summary>
        /// Proiezione mostrata: quella provvisoria durante un gesto, altrimenti quella del draft
        /// </summary>
        private MkvSplitTimelineProjection Projection
        {
            get { return this._gestureProjection ?? this._projection; }
        }

        /// <summary>
        /// Proiezione dell'output attivo
        /// </summary>
        private MkvSplitOutputProjection Result
        {
            get { return this.Projection?.Outputs.Find(o => o.OutputId == this._outputId); }
        }

        /// <summary>
        /// Stato di vista dell'output attivo
        /// </summary>
        private OutputView View
        {
            get { return this._views.TryGetValue(this._outputId, out OutputView view) ? view : this._emptyView; }
        }

        /// <summary>
        /// Segmento selezionato dell'output attivo, null se nessuno
        /// </summary>
        private MkvSplitClip Clip
        {
            get { return this.Output?.Clips.Find(c => c.Id == this.View.ClipId); }
        }

        /// <summary>
        /// Numero di frame del sorgente
        /// </summary>
        private int SourceFrameCount
        {
            get { return this._snapshot?.Analysis.SourcePts.Length ?? 0; }
        }

        /// <summary>
        /// True se è segnato almeno un estremo del tratto
        /// </summary>
        private bool HasMark
        {
            get { return this._markIn.HasValue || this._markOut.HasValue; }
        }

        /// <summary>
        /// Inizio del tratto segnato: l'inizio del sorgente se è segnata solo la fine
        /// </summary>
        private int MarkStart
        {
            get { return this._markIn ?? 0; }
        }

        /// <summary>
        /// Fine esclusiva del tratto segnato: la fine del sorgente se è segnato solo l'inizio
        /// </summary>
        private int MarkEnd
        {
            get { return this._markOut ?? this.SourceFrameCount; }
        }

        /// <summary>
        /// True se il campo di rinomina contiene un nome file non utilizzabile
        /// </summary>
        private bool NameInvalid
        {
            get { return this._renamingId.HasValue && !MkvSplitDocumentService.IsValidOutputFileName(this._name); }
        }

        /// <summary>
        /// Motivo per cui Applica non è disponibile, vuoto se è disponibile o se un'operazione è in corso
        /// </summary>
        private string ApplyNote
        {
            get { return this._projection != null && !this._busy && !this._projection.IsValid ? T("applyBlocked") : ""; }
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
        /// Gesto dalla timeline JS. Ogni movimento si valuta sul draft invariato. Solo la fine applica, una volta.
        /// </summary>
        /// <param name="token">Token del gesto</param>
        /// <param name="phase">Fase: begin, move, end o cancel</param>
        /// <param name="kind">Tipo: seek, select, trim o range</param>
        /// <param name="clipId">Segmento coinvolto, vuoto per seek e range</param>
        /// <param name="outputId">Output del segmento, vuoto per seek e range</param>
        /// <param name="start">True se il trim riguarda l'inizio del segmento</param>
        /// <param name="ms">Posizione sorgente in millisecondi</param>
        /// <param name="anchorMs">Punto di partenza del tratto in millisecondi, usato da range</param>
        /// <param name="revision">Revisione del documento vista dal JS</param>
        /// <param name="sequence">Sequenza dell'evento nel gesto</param>
        [JSInvokable]
        public async Task OnMontageGesture(string token, string phase, string kind, string clipId, string outputId, bool start, double ms, double anchorMs, long revision, long sequence)
        {
            if (this._disposed || this._busy || this._snapshot == null || revision != this._revision)
                return;
            if (phase == "begin")
            {
                if (this._gestureToken != null)
                    return;
                this._gestureToken = token;
                this._gestureSequence = sequence;
                this._gestureCheckpoint = this.Capture();
                this._gestureFeedback = this._feedback;
            }
            else if (this._gestureToken != token || sequence <= this._gestureSequence)
                return;
            this._gestureSequence = sequence;
            if (phase == "cancel")
            {
                this.RestoreGestureCursor();
                this.ClearGesture();
                await this.RefreshAsync(true, false);
                return;
            }
            if (kind == "range")
            {
                this.RangeGesture(anchorMs, ms);
                if (phase == "end")
                    this.ClearGesture();
                await this.RefreshAsync(true, false);
                return;
            }
            Guid.TryParse(outputId, out Guid ownerId);
            Guid.TryParse(clipId, out Guid id);
            MkvSplitOutput owner = this._draft.Outputs.Find(o => o.Id == ownerId);
            MkvSplitClip clip = owner?.Clips.Find(c => c.Id == id);
            if (kind is "seek" or "select" || clip == null)
            {
                this.SeekGesture(kind == "select" ? clip : null, ownerId, ms);
                if (phase == "end")
                {
                    if (kind == "select" && clip != null)
                    {
                        if (ownerId != this._outputId)
                            this.SelectOutput(ownerId);
                        this.View.ClipId = id;
                    }
                    this.ClearGesture();
                }
                await this.RefreshAsync(true, false);
                return;
            }
            int boundary = this.BoundaryAt(ms);
            List<MkvSplitEditCommand> commands = new List<MkvSplitEditCommand> { new MkvSplitEditCommand { Kind = MkvSplitEditKind.TrimClip, OutputId = ownerId, ClipId = id, StartFrame = start ? boundary : clip.StartFrame, EndFrameExclusive = start ? clip.EndFrameExclusive : boundary } };
            (MkvSplitDocument candidate, List<MkvSplitDiagnostic> diagnostics) = this.Simulate(commands);
            this._feedback = diagnostics;
            // L'anteprima mostra il sorgente sul frame del confine trascinato: il primo dentro il segmento
            this._sourcePreview = true;
            this._sourceFrame = Math.Clamp(start ? boundary : boundary - 1, 0, this.SourceFrameCount - 1);
            if (phase == "end")
            {
                Checkpoint checkpoint = this._gestureCheckpoint;
                this.ClearGesture();
                if (await this.ExecuteStepsAsync(commands.Select(c => (Func<MkvSplitEditResult, MkvSplitEditCommand>)(_ => c)).ToList(), checkpoint) && !this._disposed)
                {
                    this.SelectOutput(ownerId);
                    this.View.ClipId = id;
                }
                await this.RefreshAsync(true, false);
                return;
            }
            this._gestureDocument = candidate;
            this._gestureProjection = candidate != null ? this._service.Project(candidate, this._snapshot.Analysis, this._snapshot.Options) : null;
            await this.RefreshAsync(true, false);
        }

        /// <summary>
        /// Menu contestuale chiesto dalla timeline JS: su una barra quello del segmento, sul tratto segnato quello del tratto
        /// </summary>
        /// <param name="kind">Elemento sotto il puntatore: clip o range</param>
        /// <param name="clipId">Segmento sotto il puntatore, vuoto per range</param>
        /// <param name="outputId">Output del segmento, vuoto per range</param>
        /// <param name="clientX">Ascissa del puntatore nella finestra</param>
        /// <param name="clientY">Ordinata del puntatore nella finestra</param>
        [JSInvokable]
        public async Task OnTimelineContextMenu(string kind, string clipId, string outputId, double clientX, double clientY)
        {
            if (this._disposed || this._busy || this._snapshot == null || this._gestureToken != null)
                return;
            MouseEventArgs args = new MouseEventArgs { ClientX = clientX, ClientY = clientY };
            if (kind == "clip" && Guid.TryParse(outputId, out Guid ownerId) && Guid.TryParse(clipId, out Guid id))
                await this.OpenSegmentMenuAsync(ownerId, id, args);
            else if (kind == "range" && this.HasMark)
                this.OpenRangeMenu(args);
        }

        /// <summary>
        /// Passo di un frame dai pulsanti o dalla tastiera
        /// </summary>
        /// <param name="side">Timeline richiesta, non usata: il passo segue l'anteprima</param>
        /// <param name="delta">Numero di frame, con segno</param>
        [JSInvokable]
        public async Task OnFrameStep(string side, int delta)
        {
            if (this._disposed || this._snapshot == null)
                return;
            this.StepFrames(delta);
            await this.RefreshAsync();
        }

        /// <summary>
        /// Tasto catturato dall'editor
        /// </summary>
        /// <param name="key">Tasto premuto, oppure Undo e Redo per le scorciatoie di annullamento</param>
        /// <param name="shift">True se Shift è premuto</param>
        /// <param name="editing">True se il focus è in un campo modificabile</param>
        [JSInvokable]
        public async Task OnEditorKey(string key, bool shift, bool editing)
        {
            if (this._disposed)
                return;
            if (key == "Escape" && editing)
            {
                // Esc in un campo annulla la digitazione: il JS riporta il testo al modello, qui si chiude la rinomina
                this._renamingId = null;
                this.StateHasChanged();
                return;
            }
            if (key == "Escape")
            {
                if (this._menuOpen)
                {
                    this.CloseMenu();
                    return;
                }
                await this.CloseAsync();
                return;
            }
            if (editing || this._snapshot == null)
                return;
            if (key is "i" or "o")
                await this.MarkAsync(key == "i");
            else if (key == "s")
                await this.SplitAsync();
            else if (key is "Undo" or "Redo")
                await this.HistoryAsync(key == "Redo");
            else if (key == "Delete" && this.Clip != null)
                await this.RemoveSelectedClipAsync();
            else if (key is "Home" or "End")
                await this.GoToVideoEdgeAsync(key == "Home");
        }

        /// <summary>
        /// Rilascia sessione, timeline e oggetti JS
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            this._disposed = true;
            this._lifetime.Cancel();
            ++this._requestSequence;
            this.ContextMenuService.OnClose -= this.HandleMenuClosed;
            if (this._initializingInterop && this._interopFinished != null)
                await this._interopFinished.Task;
            if (this._snapshot != null)
                this.SplitOrchestrator.CloseEditor(this._snapshot.SessionId);
            // Il dispose dei figli puo' precedere il completamento di un AttachAsync in corso.
            // Il loro DisposeAsync e' idempotente: si ripete una volta a interop concluso.
            if (this._timeline != null)
                await this._timeline.DisposeAsync();
            foreach (IJSObjectReference js in new IJSObjectReference[] { this._input, this._framePreview })
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
            this.ContextMenuService.OnClose += this.HandleMenuClosed;
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
            if (this._pendingFocus != null && this._module != null)
            {
                string selector = this._pendingFocus;
                this._pendingFocus = null;
                try
                {
                    await this._module.InvokeVoidAsync("focusEditorTarget", this._root, selector);
                }
                catch (JSException)
                {
                }
            }
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
                    this._input = await this._module.InvokeAsync<IJSObjectReference>("captureEditorInput", this._root, this._reference);
                    if (this._disposed)
                        return;
                    await this._module.InvokeVoidAsync("focusEditor", this._root);
                }
                if (this._snapshot != null && this._preview != null && this._timeline != null && (this._framePreview == null || !this._timeline.IsAttached))
                {
                    this._framePreview ??= await this._module.InvokeAsync<IJSObjectReference>("createFramePreview", this._preview.Canvas);
                    if (this._disposed)
                        return;
                    await this._timeline.AttachAsync(this._module, this._reference, this.TimelineModel());
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
        /// Testo localizzato dell'editor di taglio
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
        /// Formatta una durata in forma compatta: minuti, secondi e decimi, con le ore solo se servono
        /// </summary>
        /// <param name="seconds">Durata in secondi</param>
        /// <returns>Durata formattata</returns>
        private static string Duration(double seconds)
        {
            TimeSpan span = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds, 1)));
            return span.TotalHours >= 1
                ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + span.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture)
                : span.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Tempo sorgente di un confine in frame, limitato al sorgente
        /// </summary>
        /// <param name="frame">Confine in frame</param>
        /// <returns>Timestamp formattato</returns>
        private string SourceTime(int frame)
        {
            return Time(this.BoundarySeconds(frame));
        }

        /// <summary>
        /// Secondi sorgente di un confine in frame, limitato al sorgente
        /// </summary>
        /// <param name="frame">Confine in frame</param>
        /// <returns>Secondi sorgente</returns>
        private double BoundarySeconds(int frame)
        {
            return MkvSplitDocumentService.BoundarySeconds(this._snapshot.Analysis, Math.Clamp(frame, 0, this.SourceFrameCount));
        }

        /// <summary>
        /// Durata in secondi di un segmento
        /// </summary>
        /// <param name="clip">Segmento</param>
        /// <returns>Secondi del segmento</returns>
        private double ClipSeconds(MkvSplitClip clip)
        {
            return this.BoundarySeconds(clip.EndFrameExclusive) - this.BoundarySeconds(clip.StartFrame);
        }

        /// <summary>
        /// Durata in secondi di un output
        /// </summary>
        /// <param name="output">Output</param>
        /// <returns>Somma delle durate dei segmenti</returns>
        private double OutputSeconds(MkvSplitOutput output)
        {
            return output.Clips.Sum(this.ClipSeconds);
        }

        /// <summary>
        /// Etichetta di un segmento: inizio e fine nel sorgente
        /// </summary>
        /// <param name="clip">Segmento</param>
        /// <returns>Etichetta</returns>
        private string ClipLabel(MkvSplitClip clip)
        {
            return this.SourceTime(clip.StartFrame) + " → " + this.SourceTime(clip.EndFrameExclusive);
        }

        /// <summary>
        /// Tooltip di un segmento: durata e frame del sorgente
        /// </summary>
        /// <param name="clip">Segmento</param>
        /// <returns>Testo del tooltip</returns>
        private string ClipTitle(MkvSplitClip clip)
        {
            return AppText.F("web.splitMontage.segmentTitle", Duration(this.ClipSeconds(clip)), clip.StartFrame, clip.EndFrameExclusive - 1);
        }

        /// <summary>
        /// Nome file di un output come lo calcola la proiezione
        /// </summary>
        /// <param name="id">Output</param>
        /// <returns>Nome file, quello personalizzato se la proiezione non lo contiene</returns>
        private string FileName(Guid id)
        {
            MkvSplitOutputProjection projected = this.Projection?.Outputs.Find(o => o.OutputId == id);
            return projected != null && !string.IsNullOrEmpty(projected.FileName) ? projected.FileName : this._draft?.Outputs.Find(o => o.Id == id)?.CustomFileName ?? "";
        }

        /// <summary>
        /// Nome breve di un output, usato sulle barre della timeline
        /// </summary>
        /// <param name="id">Output</param>
        /// <returns>Nome breve</returns>
        private string ShortName(Guid id)
        {
            return SplitEditorLayout.ShortName(this.FileName(id), this._snapshot.Document.Source.FullPath);
        }

        /// <summary>
        /// Apre la sessione dall'orchestratore e prepara documento e proiezione
        /// </summary>
        private async Task OpenAsync()
        {
            if (this._busy || this._disposed)
                return;
            this._busy = true;
            this._busyText = T("preparing");
            this._openError = "";
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
                    this._openError = string.Join(Environment.NewLine, opened.Diagnostics.Select(d => d.Message));
                    return;
                }
                this._draft = this._snapshot.Document.Clone();
                MkvSplitAnalysis analysis = this._snapshot.Analysis;
                this._keyframes = Enumerable.Range(0, Math.Min(analysis.KeyFlags.Count, analysis.SourcePts.Length)).Where(i => analysis.KeyFlags[i].Key).ToArray();
                this._projection = this._service.Project(this._draft, this._snapshot.Analysis, this._snapshot.Options);
                this._trackId = this._snapshot.SourceInfo.Tracks.FirstOrDefault(t => t.Type == "audio")?.Id;
                this.SelectOutput(this._draft.Outputs.FirstOrDefault()?.Id ?? Guid.Empty);
                this._sourceFrame = this.Clip?.StartFrame ?? 0;
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
        /// Rende attivo un output e riallinea selezione e cursore del risultato
        /// </summary>
        /// <param name="id">Output da rendere attivo</param>
        private void SelectOutput(Guid id)
        {
            this._outputId = id;
            if (!this._views.ContainsKey(id))
                this._views[id] = new OutputView();
            // Quando il segmento selezionato sparisce la selezione passa al primo segmento dell'output
            if (this.Output?.Clips.Any(c => c.Id == this.View.ClipId) != true)
                this.View.ClipId = this.Output?.Clips.FirstOrDefault()?.Id ?? Guid.Empty;
            this.View.Seconds = Math.Clamp(this.View.Seconds, 0, this.Result?.DurationSeconds ?? 0);
            if (this.Output == null || this.Output.Clips.Count == 0)
                this._sourcePreview = true;
        }

        /// <summary>
        /// Rende attivo un output dal suo riquadro, annullando l'eventuale gesto in corso
        /// </summary>
        /// <param name="id">Output da rendere attivo</param>
        private async Task SelectOutputAsync(Guid id)
        {
            if (id == this._outputId)
                return;
            await this.CancelGestureAsync();
            int frame = this.DisplayedSourceFrame();
            this.SelectOutput(id);
            // Il cambio di output non sposta l'anteprima: il sorgente resta sul frame mostrato
            this._sourcePreview = true;
            this._sourceFrame = frame;
            await this.RefreshAsync();
        }

        /// <summary>
        /// Annulla il gesto JS in corso riportando cursore e diagnostica allo stato iniziale
        /// </summary>
        private async Task CancelGestureAsync()
        {
            if (this._gestureToken == null)
                return;
            this.RestoreGestureCursor();
            this.ClearGesture();
            if (this._module != null)
                await this._module.InvokeVoidAsync("cancelEditorGestures", this._root);
        }

        /// <summary>
        /// Clic su un segmento dell'elenco: lo seleziona e porta l'anteprima al suo inizio
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="id">Segmento cliccato</param>
        private async Task ClickSegmentAsync(Guid outputId, Guid id)
        {
            await this.CancelGestureAsync();
            if (outputId != this._outputId)
            {
                this.SelectOutput(outputId);
                this._sourcePreview = true;
            }
            MkvSplitClip clip = this.Output?.Clips.Find(c => c.Id == id);
            if (clip != null)
            {
                this.View.ClipId = id;
                MkvSplitClipProjection projected = this.Result?.Clips.Find(c => c.ClipId == id);
                if (!this._sourcePreview && projected != null)
                    this.View.Seconds = projected.ResultStartSeconds;
                else
                    this._sourceFrame = clip.StartFrame;
            }
            await this.RefreshAsync();
        }

        /// <summary>
        /// Segmenti di tutti i file nell'ordine dell'elenco: file nell'ordine, segmenti nell'ordine del file
        /// </summary>
        /// <returns>Output e segmento di ogni voce</returns>
        private List<(Guid OutputId, Guid ClipId)> SegmentOrder()
        {
            return this._draft?.Outputs.SelectMany(o => o.Clips.Select(c => (o.Id, c.Id))).ToList() ?? new List<(Guid OutputId, Guid ClipId)>();
        }

        /// <summary>
        /// Posizione del segmento selezionato nell'ordine dell'elenco
        /// </summary>
        /// <returns>Posizione, -1 senza segmento selezionato</returns>
        private int SelectedSegmentIndex()
        {
            MkvSplitClip clip = this.Clip;
            return clip == null ? -1 : this.SegmentOrder().IndexOf((this._outputId, clip.Id));
        }

        /// <summary>
        /// Seleziona il segmento precedente o successivo nell'ordine dell'elenco, come un clic sulla sua riga
        /// </summary>
        /// <param name="delta">-1 per il precedente, 1 per il successivo</param>
        private async Task StepSegmentAsync(int delta)
        {
            List<(Guid OutputId, Guid ClipId)> order = this.SegmentOrder();
            int index = this.SelectedSegmentIndex();
            if (index < 0 || index + delta < 0 || index + delta >= order.Count)
                return;
            (Guid outputId, Guid clipId) = order[index + delta];
            await this.ClickSegmentAsync(outputId, clipId);
        }

        /// <summary>
        /// Porta il cursore sul sorgente al primo o all'ultimo frame di un segmento
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="clipId">Segmento</param>
        /// <param name="start">True per il primo frame, false per l'ultimo</param>
        private async Task GoToClipEdgeAsync(Guid outputId, Guid clipId, bool start)
        {
            MkvSplitClip clip = this._draft?.Outputs.Find(o => o.Id == outputId)?.Clips.Find(c => c.Id == clipId);
            if (clip == null)
                return;
            await this.CancelGestureAsync();
            this._sourcePreview = true;
            this._sourceFrame = Math.Clamp(start ? clip.StartFrame : clip.EndFrameExclusive - 1, 0, this.SourceFrameCount - 1);
            await this.RefreshAsync();
        }

        /// <summary>
        /// Porta il cursore al primo o all'ultimo frame del sorgente o dell'output montato mostrato
        /// </summary>
        /// <param name="start">True per l'inizio, false per la fine</param>
        private async Task GoToVideoEdgeAsync(bool start)
        {
            if (this._snapshot == null)
                return;
            if (this._sourcePreview)
                this._sourceFrame = start ? 0 : this.SourceFrameCount - 1;
            else
                this.View.Seconds = start ? 0 : this.LastResultSeconds();
            await this.RefreshAsync();
        }

        /// <summary>
        /// Keyframe del sorgente strettamente prima o dopo il frame mostrato
        /// </summary>
        /// <param name="direction">-1 per quello precedente, 1 per quello successivo</param>
        /// <returns>Frame del keyframe, null se in quella direzione non ce ne sono</returns>
        private int? AdjacentKeyframe(int direction)
        {
            if (this._snapshot == null || this._keyframes.Length == 0)
                return null;
            int frame = this.DisplayedSourceFrame();
            int found = Array.BinarySearch(this._keyframes, frame);
            // Senza corrispondenza il complemento indica il primo keyframe dopo il frame
            int after = found >= 0 ? found + 1 : ~found;
            int before = found >= 0 ? found - 1 : ~found - 1;
            int index = direction < 0 ? before : after;
            return index >= 0 && index < this._keyframes.Length ? this._keyframes[index] : null;
        }

        /// <summary>
        /// Porta il cursore sul sorgente al keyframe precedente o successivo
        /// </summary>
        /// <param name="direction">-1 per quello precedente, 1 per quello successivo</param>
        private async Task GoToKeyframeAsync(int direction)
        {
            int? keyframe = this.AdjacentKeyframe(direction);
            if (!keyframe.HasValue)
                return;
            await this.CancelGestureAsync();
            this._sourcePreview = true;
            this._sourceFrame = keyframe.Value;
            await this.RefreshAsync();
        }

        /// <summary>
        /// Seleziona un segmento senza spostare il cursore, per il menu contestuale
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="id">Segmento</param>
        private void SelectClip(Guid outputId, Guid id)
        {
            if (outputId != this._outputId)
            {
                int frame = this.DisplayedSourceFrame();
                this.SelectOutput(outputId);
                this._sourcePreview = true;
                this._sourceFrame = frame;
            }
            if (this.Output?.Clips.Any(c => c.Id == id) == true)
                this.View.ClipId = id;
        }

        /// <summary>
        /// Frame sorgente che contiene il tempo indicato, limitato al sorgente
        /// </summary>
        /// <param name="ms">Tempo sorgente in millisecondi</param>
        /// <returns>Frame sorgente</returns>
        private int SourceFrameAt(double ms)
        {
            MkvSplitAnalysis analysis = this._snapshot.Analysis;
            return this._service.ResolveSource(analysis, Math.Clamp(ms / 1000, analysis.SourcePts[0], analysis.Duration)).SourceFrame;
        }

        /// <summary>
        /// Confine in frame al tempo indicato: la fine del sorgente oltre la sua durata
        /// </summary>
        /// <param name="ms">Tempo sorgente in millisecondi</param>
        /// <returns>Confine in frame</returns>
        private int BoundaryAt(double ms)
        {
            return ms >= this._snapshot.Analysis.Duration * 1000 ? this.SourceFrameCount : this.SourceFrameAt(ms);
        }

        /// <summary>
        /// Porta il cursore al tempo di un gesto: dentro l'output montato se il segmento cliccato è suo, altrimenti sul sorgente
        /// </summary>
        /// <param name="clip">Segmento cliccato, null per un clic sulla timeline</param>
        /// <param name="ownerId">Output del segmento</param>
        /// <param name="ms">Tempo sorgente in millisecondi</param>
        private void SeekGesture(MkvSplitClip clip, Guid ownerId, double ms)
        {
            int frame = this.SourceFrameAt(ms);
            if (clip != null && !this._sourcePreview && ownerId == this._outputId)
            {
                MkvSplitFrameResolution resolved = this._service.ResolveClipSourceFrame(this._projection, this._snapshot.Analysis, ownerId, clip.Id,
                    Math.Clamp(frame, clip.StartFrame, clip.EndFrameExclusive - 1));
                if (resolved.IsValid)
                {
                    this.View.Seconds = resolved.ResultSeconds;
                    return;
                }
            }
            this._sourcePreview = true;
            this._sourceFrame = frame;
        }

        /// <summary>
        /// Segna il tratto trascinato su un'area vuota della timeline e porta il cursore sul punto raggiunto
        /// </summary>
        /// <param name="anchorMs">Punto di partenza del trascinamento in millisecondi</param>
        /// <param name="ms">Punto raggiunto in millisecondi</param>
        private void RangeGesture(double anchorMs, double ms)
        {
            int from = this.SourceFrameAt(Math.Min(anchorMs, ms));
            this._markIn = from;
            this._markOut = Math.Max(from + 1, this.BoundaryAt(Math.Max(anchorMs, ms)));
            this._sourcePreview = true;
            this._sourceFrame = this.SourceFrameAt(ms);
        }

        /// <summary>
        /// Avanza o arretra di un numero di frame nel sorgente o nell'output montato
        /// </summary>
        /// <param name="delta">Numero di frame, con segno</param>
        private void StepFrames(int delta)
        {
            if (this._sourcePreview)
                this._sourceFrame = Math.Clamp(this._sourceFrame + delta, 0, this.SourceFrameCount - 1);
            else if (this.Result?.FrameCount > 0)
            {
                MkvSplitFrameResolution currentFrame = this._service.ResolveResultBoundary(this.Projection, this._snapshot.Analysis, this._outputId, this.View.Seconds);
                if (currentFrame.IsValid)
                {
                    int targetFrame = Math.Clamp(currentFrame.ResultFrame + delta, 0, this.Result.FrameCount - 1);
                    MkvSplitFrameResolution steppedFrame = this._service.ResolveResultFrame(this.Projection, this._snapshot.Analysis, this._outputId, targetFrame);
                    if (steppedFrame.IsValid)
                        this.View.Seconds = steppedFrame.ResultSeconds;
                }
            }
        }

        /// <summary>
        /// Inizio dell'ultimo frame dell'output attivo, la posizione finale del cursore montato
        /// </summary>
        /// <returns>Secondi nel risultato</returns>
        private double LastResultSeconds()
        {
            if (this.Result == null || this.Result.FrameCount == 0)
                return 0;
            MkvSplitFrameResolution last = this._service.ResolveResultFrame(this.Projection, this._snapshot.Analysis, this._outputId, this.Result.FrameCount - 1);
            return last.IsValid ? last.ResultSeconds : 0;
        }

        /// <summary>
        /// Frame da mostrare in anteprima per la vista corrente
        /// </summary>
        /// <returns>Frame risolto</returns>
        private MkvSplitFrameResolution PreviewResolution()
        {
            return this._sourcePreview
                ? this._service.ResolveSource(this._snapshot.Analysis, this._snapshot.Analysis.SourcePts[this._sourceFrame])
                : this._service.ResolveResult(this.Projection, this._snapshot.Analysis, this._outputId, this.View.Seconds);
        }

        /// <summary>
        /// Frame sorgente mostrato in anteprima: quello del cursore sul sorgente o quello sotto il cursore montato
        /// </summary>
        /// <returns>Frame sorgente</returns>
        private int DisplayedSourceFrame()
        {
            if (this._snapshot == null || this._sourcePreview)
                return this._sourceFrame;
            MkvSplitFrameResolution frame = this.PreviewResolution();
            return frame.IsValid ? frame.SourceFrame : this._sourceFrame;
        }

        /// <summary>
        /// Posizione del cursore mostrata accanto ai comandi: nel sorgente o nell'output montato
        /// </summary>
        /// <returns>Secondi</returns>
        private double PositionSeconds()
        {
            return this._sourcePreview ? this._snapshot.Analysis.SourcePts[this._sourceFrame] : this.View.Seconds;
        }

        /// <summary>
        /// Durata di riferimento della posizione: quella del sorgente o dell'output montato
        /// </summary>
        /// <returns>Secondi</returns>
        private double PositionDuration()
        {
            return this._sourcePreview ? this._snapshot.Analysis.Duration : this.Result?.DurationSeconds ?? 0;
        }

        /// <summary>
        /// Riga informativa sotto l'anteprima: con un output montato indica il punto corrispondente del sorgente
        /// </summary>
        /// <returns>Testo informativo</returns>
        private string PreviewMeta()
        {
            if (this._snapshot == null || this._sourcePreview || this.Result == null || this.Result.Clips.Count == 0)
                return "";
            return T("source") + " " + Time(this._snapshot.Analysis.SourcePts[this.DisplayedSourceFrame()]);
        }

        /// <summary>
        /// Torna all'anteprima del sorgente sul frame mostrato
        /// </summary>
        private async Task ShowSourceAsync()
        {
            this._sourceFrame = this.DisplayedSourceFrame();
            this._sourcePreview = true;
            await this.RefreshAsync();
        }

        /// <summary>
        /// Mostra nell'anteprima l'output indicato già montato, nel punto del sorgente mostrato quando cade in un suo segmento
        /// </summary>
        /// <param name="id">Output da mostrare</param>
        private async Task ShowOutputAsync(Guid id)
        {
            await this.CancelGestureAsync();
            if (this._sourcePreview || this._outputId != id)
            {
                int frame = this.DisplayedSourceFrame();
                if (id != this._outputId)
                    this.SelectOutput(id);
                MkvSplitClipProjection clip = this.Result?.Clips.OrderByDescending(c => c.ClipId == this.View.ClipId)
                    .FirstOrDefault(c => frame >= c.StartFrame && frame < c.EndFrameExclusive);
                MkvSplitFrameResolution resolved = clip == null ? null : this._service.ResolveClipSourceFrame(this.Projection, this._snapshot.Analysis, id, clip.ClipId, frame);
                if (resolved?.IsValid == true)
                    this.View.Seconds = resolved.ResultSeconds;
                this._sourcePreview = false;
            }
            await this.RefreshAsync();
        }

        /// <summary>
        /// Segna l'inizio o la fine del tratto sul frame mostrato. La fine include il frame visibile.
        /// </summary>
        /// <param name="start">True per l'inizio</param>
        private async Task MarkAsync(bool start)
        {
            if (this._snapshot == null)
                return;
            int frame = this.DisplayedSourceFrame();
            if (start)
            {
                this._markIn = frame;
                if (this._markOut.HasValue && this._markOut.Value <= frame)
                    this._markOut = null;
            }
            else
            {
                this._markOut = frame + 1;
                if (this._markIn.HasValue && this._markIn.Value > frame)
                    this._markIn = null;
            }
            await this.RefreshAsync(false);
        }

        /// <summary>
        /// Toglie il tratto segnato
        /// </summary>
        private async Task ClearMarkAsync()
        {
            this._markIn = null;
            this._markOut = null;
            await this.RefreshAsync(false);
        }

        /// <summary>
        /// Crea un nuovo file vuoto in fondo all'elenco
        /// </summary>
        private async Task CreateEmptyAsync()
        {
            await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.CreateEmptyOutput, InsertIndex = this._draft.Outputs.Count });
        }

        /// <summary>
        /// Aggiunge in fondo al file selezionato un segmento dal frame mostrato alla fine del sorgente
        /// </summary>
        private async Task AppendFromCursorAsync()
        {
            if (this.Output == null)
                return;
            await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.AppendSource, OutputId = this._outputId, StartFrame = this.DisplayedSourceFrame(), EndFrameExclusive = this.SourceFrameCount });
        }

        /// <summary>
        /// Segmenti da dividere al cursore: nell'output montato quello sotto il cursore; sul sorgente il segmento selezionato
        /// se è sotto il cursore. Il cursore deve cadere dopo il primo frame del segmento.
        /// </summary>
        /// <returns>Output, segmento e confine nel risultato di ogni divisione</returns>
        private List<(Guid OutputId, Guid ClipId, int ResultFrame)> SplitTargets()
        {
            List<(Guid OutputId, Guid ClipId, int ResultFrame)> targets = new List<(Guid OutputId, Guid ClipId, int ResultFrame)>();
            if (this._snapshot == null || this._projection == null)
                return targets;
            if (!this._sourcePreview)
            {
                MkvSplitFrameResolution cursor = this._service.ResolveResultBoundary(this._projection, this._snapshot.Analysis, this._outputId, this.View.Seconds);
                MkvSplitClipProjection clip = cursor.IsValid && cursor.ClipId.HasValue ? this.Result?.Clips.Find(c => c.ClipId == cursor.ClipId.Value) : null;
                if (clip != null && cursor.ResultFrame > clip.ResultStartFrame && cursor.ResultFrame < clip.ResultStartFrame + clip.FrameCount)
                    targets.Add((this._outputId, clip.ClipId, cursor.ResultFrame));
                return targets;
            }
            int frame = this._sourceFrame;
            foreach (MkvSplitOutputProjection output in this._projection.Outputs)
                foreach (MkvSplitClipProjection clip in output.Clips.Where(c => frame > c.StartFrame && frame < c.EndFrameExclusive))
                    targets.Add((output.OutputId, clip.ClipId, clip.ResultStartFrame + frame - clip.StartFrame));
            return targets.Where(t => t.OutputId == this._outputId && t.ClipId == this.View.ClipId).ToList();
        }

        /// <summary>
        /// Divide in due il segmento selezionato nel punto del cursore
        /// </summary>
        private async Task SplitAsync()
        {
            List<(Guid OutputId, Guid ClipId, int ResultFrame)> targets = this.SplitTargets();
            if (targets.Count == 0)
                return;
            await this.ExecuteStepsAsync(targets.Select(t => (Func<MkvSplitEditResult, MkvSplitEditCommand>)(_ =>
                new MkvSplitEditCommand { Kind = MkvSplitEditKind.SplitClip, OutputId = t.OutputId, ClipId = t.ClipId, ResultFrame = t.ResultFrame })).ToList());
        }

        /// <summary>
        /// Rimuove un segmento e offre l'annullamento
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="clipId">Segmento da rimuovere</param>
        private async Task RemoveClipAsync(Guid outputId, Guid clipId)
        {
            if (await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.RemoveClips, OutputId = outputId, ClipIds = new List<Guid> { clipId } }))
                this.NotifyUndo(T("removed"));
        }

        /// <summary>
        /// Rimuove il segmento selezionato e offre l'annullamento
        /// </summary>
        private async Task RemoveSelectedClipAsync()
        {
            if (this.Clip != null)
                await this.RemoveClipAsync(this._outputId, this.Clip.Id);
        }

        /// <summary>
        /// Toglie la divisione fra due segmenti contigui dello stesso output
        /// </summary>
        /// <param name="outputId">Output dei segmenti</param>
        /// <param name="clipId">Primo segmento</param>
        /// <param name="nextId">Segmento successivo</param>
        private async Task RemoveDivisionAsync(Guid outputId, Guid clipId, Guid nextId)
        {
            await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.RemoveDivision, OutputId = outputId, ClipId = clipId, NextClipId = nextId });
        }

        /// <summary>
        /// Porta l'inizio o la fine del segmento selezionato sul frame mostrato. La fine include il frame visibile.
        /// </summary>
        /// <param name="start">True per l'inizio</param>
        private async Task TrimToCursorAsync(bool start)
        {
            MkvSplitClip clip = this.Clip;
            if (clip == null)
                return;
            int frame = this.DisplayedSourceFrame();
            await this.ExecuteAsync(new MkvSplitEditCommand
            {
                Kind = MkvSplitEditKind.TrimClip,
                OutputId = this._outputId,
                ClipId = clip.Id,
                StartFrame = start ? frame : clip.StartFrame,
                EndFrameExclusive = start ? clip.EndFrameExclusive : frame + 1
            });
        }

        /// <summary>
        /// Imposta inizio o fine di un segmento dal tempo scritto: accetta hh:mm:ss.mmm, mm:ss.mmm, secondi oppure f seguito dal frame
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="clipId">Segmento</param>
        /// <param name="start">True per l'inizio, false per la fine</param>
        /// <param name="text">Testo del campo</param>
        /// <returns>True se il segmento è cambiato</returns>
        private async Task<bool> TrimToTimeAsync(Guid outputId, Guid clipId, bool start, string text)
        {
            MkvSplitClip clip = this._draft?.Outputs.Find(o => o.Id == outputId)?.Clips.Find(c => c.Id == clipId);
            if (clip == null || this._busy || string.IsNullOrWhiteSpace(text))
                return false;
            int frame;
            try
            {
                (double value, bool isFrame) = MkvSplitSegmentService.ParseTime(text, this._snapshot.Analysis.Duration);
                frame = isFrame ? (int)value : start ? this.SourceFrameAt(value * 1000) : this.BoundaryAt(value * 1000);
            }
            catch (FormatException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
            return await this.ExecuteAsync(new MkvSplitEditCommand
            {
                Kind = MkvSplitEditKind.TrimClip,
                OutputId = outputId,
                ClipId = clip.Id,
                StartFrame = start ? frame : clip.StartFrame,
                EndFrameExclusive = start ? clip.EndFrameExclusive : frame
            });
        }

        /// <summary>
        /// Tempo confermato nel campo Inizio o Fine: lo applica al segmento, altrimenti riporta il campo al valore del segmento
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="clipId">Segmento</param>
        /// <param name="start">True per il campo Inizio, false per Fine</param>
        /// <param name="args">Evento di modifica del campo</param>
        private async Task TimeFieldAsync(Guid outputId, Guid clipId, bool start, ChangeEventArgs args)
        {
            if (await this.TrimToTimeAsync(outputId, clipId, start, args.Value?.ToString() ?? ""))
                return;
            this._timeFieldsVersion++;
            this.StateHasChanged();
        }

        /// <summary>
        /// Intervalli del risultato di un output coperti dal tratto segnato, dall'ultimo al primo
        /// così ogni rimozione lascia validi i frame di quelle successive
        /// </summary>
        /// <param name="outputId">Output</param>
        /// <returns>Intervalli in frame del risultato</returns>
        private List<(int From, int To)> MarkResultRanges(Guid outputId)
        {
            List<(int From, int To)> ranges = new List<(int From, int To)>();
            MkvSplitOutputProjection result = this.Projection?.Outputs.Find(o => o.OutputId == outputId);
            if (result == null || !this.HasMark)
                return ranges;
            foreach (MkvSplitClipProjection clip in result.Clips.OrderByDescending(c => c.ResultStartFrame))
            {
                int from = Math.Max(this.MarkStart, clip.StartFrame);
                int to = Math.Min(this.MarkEnd, clip.EndFrameExclusive);
                if (to > from)
                    ranges.Add((clip.ResultStartFrame + from - clip.StartFrame, clip.ResultStartFrame + to - clip.StartFrame));
            }
            return ranges;
        }

        /// <summary>
        /// Toglie il tratto segnato dall'output indicato, in un solo passo annullabile
        /// </summary>
        /// <param name="outputId">Output da cui togliere il tratto</param>
        private async Task RemoveMarkFromOutputAsync(Guid outputId)
        {
            List<(int From, int To)> ranges = this.MarkResultRanges(outputId);
            if (ranges.Count == 0)
                return;
            if (await this.ExecuteStepsAsync(ranges.Select(r => (Func<MkvSplitEditResult, MkvSplitEditCommand>)(_ =>
                new MkvSplitEditCommand { Kind = MkvSplitEditKind.RemoveResultRange, OutputId = outputId, ResultFrame = r.From, ResultEndFrameExclusive = r.To })).ToList()))
                await this.ClearMarkAsync();
        }

        /// <summary>
        /// Porta l'inizio di un segmento sul keyframe secondo lo snap configurato
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="clipId">Segmento</param>
        private async Task SnapStartAsync(Guid outputId, Guid clipId)
        {
            MkvSplitClip clip = this._draft?.Outputs.Find(o => o.Id == outputId)?.Clips.Find(c => c.Id == clipId);
            if (clip == null || this._busy || this._gestureToken != null || this._snapshot.Options.Snap == MkvSplitSnapMode.Off)
                return;
            MkvSplitSegment segment = new MkvSplitSegment
            {
                Num = 1,
                StartFrame = clip.StartFrame,
                FrameCount = clip.EndFrameExclusive - clip.StartFrame,
                StartTs = this._snapshot.Analysis.SourcePts[clip.StartFrame],
                EndTs = MkvSplitDocumentService.BoundarySeconds(this._snapshot.Analysis, clip.EndFrameExclusive)
            };
            MkvSplitSegmentService service = new MkvSplitSegmentService();
            service.ApplySnap(new List<MkvSplitSegment> { segment }, this._snapshot.Analysis.KeyFlags, this._snapshot.Analysis.SourcePts, this._snapshot.Options.Snap);
            if (service.Warnings.Count > 0)
            {
                this._feedback = service.Warnings.Select(w => new MkvSplitDiagnostic
                {
                    Code = w.Kind == MkvSplitWarningKind.SnapEatSegment ? "snapWouldEat" : "snap",
                    Message = w.Kind == MkvSplitWarningKind.SnapEatSegment ? AppText.T("split.montage.snapWouldEat") : w.Message,
                    Severity = MkvSplitDiagnosticSeverity.Warning,
                    OutputId = outputId,
                    ClipId = clip.Id
                }).ToList();
                return;
            }
            await this.ExecuteAsync(new MkvSplitEditCommand
            {
                Kind = MkvSplitEditKind.TrimClip,
                OutputId = outputId,
                ClipId = clip.Id,
                StartFrame = segment.StartFrame,
                EndFrameExclusive = segment.StartFrame + segment.FrameCount
            });
        }

        /// <summary>
        /// Valuta in sequenza i comandi sul draft senza applicarli, per l'anteprima di un gesto
        /// </summary>
        /// <param name="commands">Comandi da valutare</param>
        /// <returns>Documento provvisorio, null se invariato o rifiutato, e diagnostiche dell'ultimo comando valutato</returns>
        private (MkvSplitDocument Document, List<MkvSplitDiagnostic> Diagnostics) Simulate(List<MkvSplitEditCommand> commands)
        {
            MkvSplitDocument document = this._draft;
            List<MkvSplitDiagnostic> diagnostics = new List<MkvSplitDiagnostic>();
            bool changed = false;
            foreach (MkvSplitEditCommand command in commands)
            {
                MkvSplitEditResult result = this._service.Execute(document, command, this._snapshot.Analysis);
                if (!result.Changed && result.Diagnostics.Count > 0)
                    return (null, result.Diagnostics);
                document = result.Document;
                changed |= result.Changed;
                diagnostics = result.Diagnostics;
            }
            return (changed ? document : null, diagnostics);
        }

        /// <summary>
        /// Apre la rinomina in linea di un output
        /// </summary>
        /// <param name="id">Output da rinominare</param>
        private void StartRename(Guid id)
        {
            if (this._busy)
                return;
            this._renamingId = id;
            this._name = this.FileName(id);
            this._pendingFocus = ".split-editor-name-input";
        }

        /// <summary>
        /// Aggiorna il testo del nome durante la digitazione, per l'errore in linea
        /// </summary>
        /// <param name="args">Evento di input</param>
        private void NameInput(ChangeEventArgs args)
        {
            this._name = args.Value?.ToString() ?? "";
        }

        /// <summary>
        /// Invio conferma il nome digitato
        /// </summary>
        /// <param name="id">Output in rinomina</param>
        /// <param name="args">Evento del tasto</param>
        private async Task NameKeyAsync(Guid id, KeyboardEventArgs args)
        {
            if (args.Key == "Enter")
                await this.CommitRenameAsync(id);
        }

        /// <summary>
        /// Conferma il nome digitato come nome personalizzato, anche se non valido: l'errore resta visibile finché non è corretto
        /// </summary>
        /// <param name="id">Output in rinomina</param>
        private async Task CommitRenameAsync(Guid id)
        {
            if (this._renamingId != id)
                return;
            this._renamingId = null;
            if (this._name == this.FileName(id))
                return;
            await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.RenameOutput, OutputId = id, NameMode = MkvSplitNameMode.Custom, CustomFileName = this._name });
            this._pendingFocus = $"[data-split-output=\"{id}\"]";
        }

        /// <summary>
        /// Apre il menu contestuale di un segmento, dopo averlo selezionato
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="clipId">Segmento</param>
        /// <param name="args">Clic che apre il menu, per la posizione</param>
        private async Task OpenSegmentMenuAsync(Guid outputId, Guid clipId, MouseEventArgs args)
        {
            if (this._busy || this._draft?.Outputs.Find(o => o.Id == outputId)?.Clips.Any(c => c.Id == clipId) != true)
                return;
            await this.CancelGestureAsync();
            this.SelectClip(outputId, clipId);
            await this.RefreshAsync(false);
            this.OpenMenu(args, this.SegmentEntries(outputId, clipId), T("segmentMenu"));
        }

        /// <summary>
        /// Voci del menu di un segmento
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="clipId">Segmento</param>
        /// <returns>Voci del menu</returns>
        private List<SplitEditorMenuComponent.Entry> SegmentEntries(Guid outputId, Guid clipId)
        {
            MkvSplitOutput output = this._draft.Outputs.Find(o => o.Id == outputId);
            int index = output.Clips.FindIndex(c => c.Id == clipId);
            MkvSplitClip clip = output.Clips[index];
            MkvSplitClip next = output.Clips.ElementAtOrDefault(index + 1);
            bool joinable = next != null && next.StartFrame == clip.EndFrameExclusive;
            bool snap = this._snapshot.Options.Snap != MkvSplitSnapMode.Off;
            List<MkvSplitOutput> others = this._draft.Outputs.Where(o => o.Id != outputId).ToList();
            return new List<SplitEditorMenuComponent.Entry>
            {
                new SplitEditorMenuComponent.Entry { Text = T("goToStart"), Icon = "align_horizontal_left", Title = T("goToStartHelp"), Action = () => this.GoToClipEdgeAsync(outputId, clipId, true) },
                new SplitEditorMenuComponent.Entry { Text = T("goToEnd"), Icon = "align_horizontal_right", Title = T("goToEndHelp"), Action = () => this.GoToClipEdgeAsync(outputId, clipId, false) },
                new SplitEditorMenuComponent.Entry { Text = T("moveUp"), Icon = "arrow_upward", Separated = true, Disabled = index == 0, Action = () => this.MoveClipAsync(outputId, clipId, -1) },
                new SplitEditorMenuComponent.Entry { Text = T("moveDown"), Icon = "arrow_downward", Disabled = index == output.Clips.Count - 1, Action = () => this.MoveClipAsync(outputId, clipId, 1) },
                new SplitEditorMenuComponent.Entry { Text = T("moveTo"), Icon = "drive_file_move", Disabled = others.Count == 0, Title = others.Count == 0 ? T("needsOtherOutput") : "", Children = this.TransferEntries(outputId, clipId, others, false) },
                new SplitEditorMenuComponent.Entry { Text = T("copyTo"), Icon = "content_copy", Disabled = others.Count == 0, Title = others.Count == 0 ? T("needsOtherOutput") : "", Children = this.TransferEntries(outputId, clipId, others, true) },
                new SplitEditorMenuComponent.Entry { Text = T("newFileHere"), Icon = "call_split", Separated = true, Disabled = index == 0, Title = T(index == 0 ? "newFileHereNeedsPrevious" : "newFileHereHelp"), Action = () => this.NewFileFromClipAsync(outputId, clipId) },
                new SplitEditorMenuComponent.Entry { Text = T("join"), Icon = "vertical_align_center", Disabled = !joinable, Title = T(joinable ? "joinHelp" : "joinNeedsContiguous"), Action = () => this.RemoveDivisionAsync(outputId, clipId, next.Id) },
                new SplitEditorMenuComponent.Entry { Text = T("snapStart"), Icon = "align_horizontal_left", Disabled = !snap, Title = T(snap ? "snapStartHelp" : "snapNeedsConfiguration"), Action = () => this.SnapStartAsync(outputId, clipId) },
                new SplitEditorMenuComponent.Entry { Text = T("removeSegment"), Icon = "delete", Separated = true, Danger = true, Action = () => this.RemoveClipAsync(outputId, clipId) }
            };
        }

        /// <summary>
        /// Voci del sottomenu Sposta in o Copia in: un file di destinazione per voce
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="clipId">Segmento</param>
        /// <param name="others">File di destinazione</param>
        /// <param name="copy">True per copiare, false per spostare</param>
        /// <returns>Voci del sottomenu</returns>
        private List<SplitEditorMenuComponent.Entry> TransferEntries(Guid outputId, Guid clipId, List<MkvSplitOutput> others, bool copy)
        {
            return others.Select(o => new SplitEditorMenuComponent.Entry { Text = this.FileName(o.Id), Action = () => this.TransferClipAsync(outputId, clipId, o.Id, copy) }).ToList();
        }

        /// <summary>
        /// Apre il menu contestuale di un output
        /// </summary>
        /// <param name="id">Output del menu</param>
        /// <param name="args">Clic che apre il menu, per la posizione</param>
        private void OpenOutputMenu(Guid id, MouseEventArgs args)
        {
            int index = this._draft.Outputs.FindIndex(o => o.Id == id);
            if (index < 0 || this._busy)
                return;
            MkvSplitOutput output = this._draft.Outputs[index];
            MkvSplitOutput next = this._draft.Outputs.ElementAtOrDefault(index + 1);
            bool mergeable = next != null && output.Clips.Count > 0 && next.Clips.Count > 0;
            this.OpenMenu(args, new List<SplitEditorMenuComponent.Entry>
            {
                new SplitEditorMenuComponent.Entry { Text = T("rename"), Icon = "edit", Action = () => { this.StartRename(id); return Task.CompletedTask; } },
                new SplitEditorMenuComponent.Entry { Text = T("automaticName"), Icon = "auto_fix_high", Disabled = output.NameMode == MkvSplitNameMode.Automatic, Action = () => this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.RenameOutput, OutputId = id, NameMode = MkvSplitNameMode.Automatic }) },
                new SplitEditorMenuComponent.Entry { Text = T("moveUp"), Icon = "arrow_upward", Separated = true, Disabled = index == 0, Action = () => this.MoveOutputAsync(id, -1) },
                new SplitEditorMenuComponent.Entry { Text = T("moveDown"), Icon = "arrow_downward", Disabled = next == null, Action = () => this.MoveOutputAsync(id, 1) },
                new SplitEditorMenuComponent.Entry { Text = T("mergeNext"), Icon = "merge", Disabled = !mergeable, Title = mergeable ? "" : T("mergeNextNeedsClips"), Action = () => this.MergeWithNextAsync(id, next.Id) },
                new SplitEditorMenuComponent.Entry { Text = T("removeOutput"), Icon = "delete", Separated = true, Danger = true, Action = () => this.RemoveOutputAsync(id) }
            }, T("outputMenu"));
        }

        /// <summary>
        /// Apre il menu contestuale del tratto segnato sulla timeline
        /// </summary>
        /// <param name="args">Clic che apre il menu, per la posizione</param>
        private void OpenRangeMenu(MouseEventArgs args)
        {
            bool covered = this.Output != null && this.MarkResultRanges(this._outputId).Count > 0;
            Guid outputId = this._outputId;
            this.OpenMenu(args, new List<SplitEditorMenuComponent.Entry>
            {
                new SplitEditorMenuComponent.Entry { Text = AppText.F("web.splitMontage.removeMarkFrom", this.FileName(outputId)), Icon = "content_cut", Disabled = !covered, Title = T(covered ? "removeMarkHelp" : "removeMarkNeedsOverlap"), Action = () => this.RemoveMarkFromOutputAsync(outputId) },
                new SplitEditorMenuComponent.Entry { Text = T("clearMark"), Icon = "close", Action = this.ClearMarkAsync }
            }, T("rangeMenu"));
        }

        /// <summary>
        /// Apre un menu contestuale Radzen con le voci indicate
        /// </summary>
        /// <param name="args">Clic che apre il menu, per la posizione</param>
        /// <param name="items">Voci del menu</param>
        /// <param name="label">Etichetta del menu per la lettura assistita</param>
        private void OpenMenu(MouseEventArgs args, List<SplitEditorMenuComponent.Entry> items, string label)
        {
            this._menuOpen = true;
            this.ContextMenuService.Open(args, service => builder =>
            {
                builder.OpenComponent<SplitEditorMenuComponent>(0);
                builder.AddAttribute(1, nameof(SplitEditorMenuComponent.Items), items);
                builder.AddAttribute(2, nameof(SplitEditorMenuComponent.AriaLabel), label);
                builder.AddAttribute(3, nameof(SplitEditorMenuComponent.OnSelect), EventCallback.Factory.Create<SplitEditorMenuComponent.Entry>(this, this.RunMenuEntryAsync));
                builder.AddAttribute(4, nameof(SplitEditorMenuComponent.OnClose), EventCallback.Factory.Create(this, this.CloseMenu));
                builder.CloseComponent();
            });
        }

        /// <summary>
        /// Chiude il menu contestuale aperto
        /// </summary>
        private void CloseMenu()
        {
            if (!this._menuOpen)
                return;
            this._menuOpen = false;
            this.ContextMenuService.Close();
        }

        /// <summary>
        /// Registra la chiusura del menu da parte di Radzen: clic esterno, Esc o voce scelta
        /// </summary>
        private void HandleMenuClosed()
        {
            this._menuOpen = false;
        }

        /// <summary>
        /// Chiude il menu ed esegue la voce scelta, poi riporta il focus all'editor
        /// </summary>
        /// <param name="entry">Voce scelta</param>
        private async Task RunMenuEntryAsync(SplitEditorMenuComponent.Entry entry)
        {
            this.CloseMenu();
            if (this._disposed || entry.Action == null)
                return;
            await entry.Action();
            // La rinomina tiene il proprio focus
            if (this._module != null && !this._disposed && this._pendingFocus == null)
                await this._module.InvokeVoidAsync("focusEditor", this._root);
        }

        /// <summary>
        /// Sposta un segmento di una posizione nell'ordine del suo file
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="clipId">Segmento</param>
        /// <param name="delta">-1 verso l'alto, 1 verso il basso</param>
        private async Task MoveClipAsync(Guid outputId, Guid clipId, int delta)
        {
            int index = this._draft.Outputs.Find(o => o.Id == outputId)?.Clips.FindIndex(c => c.Id == clipId) ?? -1;
            if (index < 0)
                return;
            // Il comando inserisce nell'elenco senza il segmento spostato
            if (await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.ReorderClips, OutputId = outputId, ClipIds = new List<Guid> { clipId }, InsertIndex = index + delta }))
                this.SelectTransferred(outputId, index + delta);
        }

        /// <summary>
        /// Sposta o copia un segmento in fondo a un altro file
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="clipId">Segmento</param>
        /// <param name="destinationId">File di destinazione</param>
        /// <param name="copy">True per copiare, false per spostare</param>
        private async Task TransferClipAsync(Guid outputId, Guid clipId, Guid destinationId, bool copy)
        {
            MkvSplitOutput destination = this._draft.Outputs.Find(o => o.Id == destinationId);
            if (destination == null)
                return;
            int insert = destination.Clips.Count;
            if (await this.ExecuteAsync(new MkvSplitEditCommand { Kind = copy ? MkvSplitEditKind.CopyClips : MkvSplitEditKind.MoveClips, OutputId = outputId, DestinationOutputId = destinationId, ClipIds = new List<Guid> { clipId }, InsertIndex = insert }))
                this.SelectTransferred(destinationId, insert);
        }

        /// <summary>
        /// Crea un nuovo file con il segmento indicato e tutti quelli che lo seguono nel suo file
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="clipId">Primo segmento del nuovo file</param>
        private async Task NewFileFromClipAsync(Guid outputId, Guid clipId)
        {
            MkvSplitOutput output = this._draft.Outputs.Find(o => o.Id == outputId);
            int index = output?.Clips.FindIndex(c => c.Id == clipId) ?? -1;
            if (index <= 0)
                return;
            await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.SplitOutput, OutputId = outputId, ResultFrame = output.Clips.Take(index).Sum(c => c.EndFrameExclusive - c.StartFrame) });
        }

        /// <summary>
        /// Sposta un file di una posizione nell'elenco
        /// </summary>
        /// <param name="id">Output da spostare</param>
        /// <param name="delta">-1 verso l'alto, 1 verso il basso</param>
        private async Task MoveOutputAsync(Guid id, int delta)
        {
            int index = this._draft.Outputs.FindIndex(o => o.Id == id);
            if (index < 0)
                return;
            // Il comando inserisce nell'elenco senza l'output spostato
            await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.ReorderOutputs, OutputIds = new List<Guid> { id }, InsertIndex = index + delta });
        }

        /// <summary>
        /// Accoda al file i segmenti del file successivo e toglie quest'ultimo, offrendo l'annullamento
        /// </summary>
        /// <param name="id">File che riceve i segmenti</param>
        /// <param name="nextId">File successivo</param>
        private async Task MergeWithNextAsync(Guid id, Guid nextId)
        {
            if (await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.MergeOutputs, OutputIds = new List<Guid> { id, nextId } }))
                this.NotifyUndo(T("merged"));
        }

        /// <summary>
        /// Elimina un file, offrendo l'annullamento
        /// </summary>
        /// <param name="id">File da eliminare</param>
        private async Task RemoveOutputAsync(Guid id)
        {
            if (await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.RemoveOutputs, OutputIds = new List<Guid> { id } }))
                this.NotifyUndo(T("removed"));
        }

        /// <summary>
        /// Inizio del trascinamento di un segmento: lo seleziona e lo prepara per il rilascio
        /// </summary>
        /// <param name="outputId">Output del segmento</param>
        /// <param name="clipId">Segmento afferrato</param>
        private void BeginSegmentDrag(Guid outputId, Guid clipId)
        {
            this.SelectClip(outputId, clipId);
            this._drag = new DragPayload(DragKind.Segment, outputId, clipId);
        }

        /// <summary>
        /// Inizio del trascinamento di un output nell'elenco
        /// </summary>
        /// <param name="outputId">Output afferrato</param>
        private void BeginOutputDrag(Guid outputId)
        {
            this._drag = this._renamingId == outputId ? null : new DragPayload(DragKind.Output, outputId, Guid.Empty);
        }

        /// <summary>
        /// Rilascio fra due output: riordina l'elenco
        /// </summary>
        /// <param name="index">Posizione di rilascio nell'elenco corrente</param>
        private async Task DropOutputAsync(int index)
        {
            DragPayload drag = this._drag;
            this._drag = null;
            if (drag?.Kind != DragKind.Output)
                return;
            int from = this._draft.Outputs.FindIndex(o => o.Id == drag.OutputId);
            if (from < 0)
                return;
            // Il comando inserisce nell'elenco senza l'output spostato
            await this.ExecuteAsync(new MkvSplitEditCommand { Kind = MkvSplitEditKind.ReorderOutputs, OutputIds = new List<Guid> { drag.OutputId }, InsertIndex = index > from ? index - 1 : index });
        }

        /// <summary>
        /// Rilascio in una posizione dei segmenti di un output: nello stesso file riordina, in un altro lo sposta; con Alt lo copia
        /// </summary>
        /// <param name="outputId">Output di destinazione</param>
        /// <param name="index">Posizione di rilascio fra i segmenti correnti</param>
        /// <param name="args">Evento di rilascio, Alt copia</param>
        private async Task DropSegmentAsync(Guid outputId, int index, DragEventArgs args)
        {
            DragPayload drag = this._drag;
            this._drag = null;
            MkvSplitOutput destination = this._draft.Outputs.Find(o => o.Id == outputId);
            if (drag?.Kind != DragKind.Segment || destination == null)
                return;
            MkvSplitEditKind kind = args.AltKey ? MkvSplitEditKind.CopyClips : drag.OutputId == outputId ? MkvSplitEditKind.ReorderClips : MkvSplitEditKind.MoveClips;
            int insert = index;
            // Il riordino inserisce nell'elenco senza il segmento spostato
            if (kind == MkvSplitEditKind.ReorderClips)
                insert -= destination.Clips.Take(index).Count(c => c.Id == drag.ClipId);
            if (await this.ExecuteAsync(new MkvSplitEditCommand { Kind = kind, OutputId = drag.OutputId, DestinationOutputId = outputId, ClipIds = new List<Guid> { drag.ClipId }, InsertIndex = insert }))
                this.SelectTransferred(outputId, insert);
        }

        /// <summary>
        /// Rilascio su Nuovo file: crea un file con il segmento trascinato, spostato o copiato con Alt
        /// </summary>
        /// <param name="args">Evento di rilascio, Alt copia</param>
        private async Task DropOnNewFileAsync(DragEventArgs args)
        {
            DragPayload drag = this._drag;
            this._drag = null;
            if (drag?.Kind != DragKind.Segment)
                return;
            bool copy = args.AltKey;
            bool moved = await this.ExecuteStepsAsync(new List<Func<MkvSplitEditResult, MkvSplitEditCommand>>
            {
                _ => new MkvSplitEditCommand { Kind = MkvSplitEditKind.CreateEmptyOutput, InsertIndex = this._draft.Outputs.Count },
                created => new MkvSplitEditCommand { Kind = copy ? MkvSplitEditKind.CopyClips : MkvSplitEditKind.MoveClips, OutputId = drag.OutputId, DestinationOutputId = created.SelectedOutputId ?? Guid.Empty, ClipIds = new List<Guid> { drag.ClipId }, InsertIndex = 0 }
            });
            if (moved)
                this.SelectTransferred(this._outputId, 0);
        }

        /// <summary>
        /// Seleziona il segmento appena spostato o copiato nell'output di destinazione
        /// </summary>
        /// <param name="outputId">Output di destinazione</param>
        /// <param name="index">Posizione del segmento trasferito</param>
        private void SelectTransferred(Guid outputId, int index)
        {
            if (this._disposed || this._draft.Outputs.All(o => o.Id != outputId))
                return;
            if (outputId != this._outputId)
                this.SelectOutput(outputId);
            MkvSplitClip clip = this.Output.Clips.ElementAtOrDefault(index);
            if (clip == null)
                return;
            this.View.ClipId = clip.Id;
            this.StateHasChanged();
        }

        /// <summary>
        /// Notifica una modifica distruttiva con il comando Annulla, legato allo stato appena registrato
        /// </summary>
        /// <param name="summary">Testo della notifica</param>
        private void NotifyUndo(string summary)
        {
            Checkpoint checkpoint = this._undo.Peek();
            this.NotificationService.Notify(new NotificationMessage
            {
                Severity = NotificationSeverity.Info,
                Summary = summary,
                Duration = 8000,
                CloseOnClick = true,
                DetailContent = service => builder => this.BuildUndoButton(builder, checkpoint)
            });
        }

        /// <summary>
        /// Pulsante Annulla della notifica
        /// </summary>
        /// <param name="builder">Builder del render</param>
        /// <param name="checkpoint">Stato da ripristinare</param>
        private void BuildUndoButton(RenderTreeBuilder builder, Checkpoint checkpoint)
        {
            builder.OpenComponent<RadzenButton>(0);
            builder.AddAttribute(1, nameof(RadzenButton.Text), T("undoRemoved"));
            builder.AddAttribute(2, nameof(RadzenButton.Variant), Variant.Text);
            builder.AddAttribute(3, nameof(RadzenButton.ButtonStyle), ButtonStyle.Base);
            builder.AddAttribute(4, nameof(RadzenButton.Size), ButtonSize.Small);
            builder.AddAttribute(5, nameof(RadzenButton.Click), EventCallback.Factory.Create<MouseEventArgs>(this, () => this.UndoFromNotificationAsync(checkpoint)));
            builder.CloseComponent();
        }

        /// <summary>
        /// Annulla dalla notifica solo se lo stato notificato è ancora l'ultimo annullabile
        /// </summary>
        /// <param name="checkpoint">Stato registrato dalla modifica notificata</param>
        private async Task UndoFromNotificationAsync(Checkpoint checkpoint)
        {
            if (this._disposed || this._undo.Count == 0 || !ReferenceEquals(this._undo.Peek(), checkpoint))
                return;
            await this.HistoryAsync(false);
            if (this._module != null && !this._disposed)
                await this._module.InvokeVoidAsync("focusEditor", this._root);
        }

        /// <summary>
        /// Esegue un comando sul documento e registra lo stato precedente per l'annullamento
        /// </summary>
        /// <param name="command">Comando da eseguire</param>
        /// <param name="checkpoint">Stato da registrare, quello corrente se null</param>
        /// <returns>True se il documento è cambiato</returns>
        private Task<bool> ExecuteAsync(MkvSplitEditCommand command, Checkpoint checkpoint = null)
        {
            return this.ExecuteStepsAsync(new List<Func<MkvSplitEditResult, MkvSplitEditCommand>> { _ => command }, checkpoint);
        }

        /// <summary>
        /// Esegue in sequenza più comandi come un solo passo annullabile. Ogni passo riceve l'esito del precedente,
        /// null per il primo; un comando rifiutato annulla l'intera sequenza.
        /// </summary>
        /// <param name="steps">Costruttori dei comandi</param>
        /// <param name="checkpoint">Stato da registrare, quello corrente se null</param>
        /// <returns>True se il documento è cambiato</returns>
        private async Task<bool> ExecuteStepsAsync(IReadOnlyList<Func<MkvSplitEditResult, MkvSplitEditCommand>> steps, Checkpoint checkpoint = null)
        {
            if (this._busy || this._disposed || this._draft == null || this._gestureToken != null)
                return false;
            this._gestureDocument = null;
            this._gestureProjection = null;
            MkvSplitDocument document = this._draft;
            MkvSplitEditResult last = null;
            Guid? selectedOutput = null;
            Guid? selectedClip = null;
            bool changed = false;
            foreach (Func<MkvSplitEditResult, MkvSplitEditCommand> step in steps)
            {
                MkvSplitEditResult result = this._service.Execute(document, step(last), this._snapshot.Analysis);
                if (!result.Changed && result.Diagnostics.Count > 0)
                {
                    this._feedback = result.Diagnostics;
                    this.StateHasChanged();
                    return false;
                }
                document = result.Document;
                changed |= result.Changed;
                selectedOutput = result.SelectedOutputId ?? selectedOutput;
                selectedClip = result.SelectedClipId ?? selectedClip;
                last = result;
            }
            this._feedback = last?.Diagnostics ?? new List<MkvSplitDiagnostic>();
            if (!changed)
                return false;
            this._undo.Push(checkpoint ?? this.Capture());
            this._redo.Clear();
            this._draft = document;
            this._revision++;
            this._projection = this._service.Project(this._draft, this._snapshot.Analysis, this._snapshot.Options);
            this.SelectOutput(selectedOutput.HasValue && this._draft.Outputs.Any(o => o.Id == selectedOutput.Value) ? selectedOutput.Value
                : this._draft.Outputs.Any(o => o.Id == this._outputId) ? this._outputId : this._draft.Outputs.FirstOrDefault()?.Id ?? Guid.Empty);
            if (selectedClip.HasValue && this.Output?.Clips.Any(c => c.Id == selectedClip.Value) == true)
                this.View.ClipId = selectedClip.Value;
            await this.RefreshAsync();
            return true;
        }

        /// <summary>
        /// Cattura lo stato corrente dell'editor
        /// </summary>
        /// <returns>Stato catturato</returns>
        private Checkpoint Capture()
        {
            return new Checkpoint(this._draft.Clone(), this._outputId, this._views.ToDictionary(p => p.Key, p => p.Value.Clone()), this._sourceFrame, this._markIn, this._markOut, this._sourcePreview);
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
            await this.CancelGestureAsync();
            this.ClearGesture();
            this._renamingId = null;
            (redo ? this._undo : this._redo).Push(this.Capture());
            Checkpoint checkpoint = source.Pop();
            this._draft = checkpoint.Document;
            this._views.Clear();
            foreach (KeyValuePair<Guid, OutputView> item in checkpoint.Views)
                this._views[item.Key] = item.Value;
            this._sourceFrame = checkpoint.SourceFrame;
            this._markIn = checkpoint.MarkIn;
            this._markOut = checkpoint.MarkOut;
            this._sourcePreview = checkpoint.SourcePreview;
            this._projection = this._service.Project(this._draft, this._snapshot.Analysis, this._snapshot.Options);
            this._revision++;
            this.SelectOutput(checkpoint.OutputId);
            this._feedback = new List<MkvSplitDiagnostic>();
            await this.RefreshAsync();
        }

        /// <summary>
        /// Ripristina cursore, tratto segnato, diagnostica e vista catturati all'inizio del gesto
        /// </summary>
        private void RestoreGestureCursor()
        {
            if (this._gestureCheckpoint == null)
                return;
            this._sourceFrame = this._gestureCheckpoint.SourceFrame;
            this._markIn = this._gestureCheckpoint.MarkIn;
            this._markOut = this._gestureCheckpoint.MarkOut;
            this._feedback = this._gestureFeedback;
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
            this._gestureDocument = null;
            this._gestureProjection = null;
        }

        /// <summary>
        /// Segmenti di tutti gli output nell'ordine dell'elenco, con la corsia assegnata nella timeline
        /// </summary>
        /// <returns>Output, posizione dell'output, segmento e corsia</returns>
        private List<(MkvSplitOutput Output, int OutputIndex, MkvSplitClip Clip, int Lane)> LaneClips()
        {
            List<(MkvSplitOutput Output, int OutputIndex, MkvSplitClip Clip)> clips = this.Document.Outputs
                .SelectMany((output, index) => output.Clips.Select(clip => (output, index, clip))).ToList();
            List<int> lanes = SplitEditorLayout.AssignLanes(clips.Select(item => (item.Clip.StartFrame, item.Clip.EndFrameExclusive)).ToList());
            return clips.Select((item, index) => (item.Output, item.OutputIndex, item.Clip, lanes[index])).ToList();
        }

        /// <summary>
        /// Numero di corsie della timeline, almeno una
        /// </summary>
        /// <returns>Corsie occupate dai segmenti</returns>
        private int LaneCount()
        {
            return Math.Max(1, this.LaneClips().Select(item => item.Lane + 1).DefaultIfEmpty(1).Max());
        }

        /// <summary>
        /// Modello passato alla timeline JS
        /// </summary>
        /// <returns>Modello serializzabile</returns>
        private object TimelineModel()
        {
            MkvSplitAnalysis analysis = this._snapshot.Analysis;
            List<(MkvSplitOutput Output, int OutputIndex, MkvSplitClip Clip, int Lane)> clips = this.LaneClips();
            HashSet<Guid> errors = this.ErrorClipIds();
            return new
            {
                revision = this._revision,
                durationMs = analysis.Duration * 1000,
                playheadMs = analysis.SourcePts[this.DisplayedSourceFrame()] * 1000,
                audioMode = this._audioMode,
                waveformGain = this._gain,
                nyquistHz = (this._snapshot.SourceInfo.Tracks.FirstOrDefault(t => t.Id == this._trackId)?.SamplingFrequency ?? 48000) / 2.0,
                selectionStartMs = this.HasMark ? this.BoundarySeconds(this.MarkStart) * 1000 : 0,
                selectionEndMs = this.HasMark ? this.BoundarySeconds(this.MarkEnd) * 1000 : 0,
                keyframesLabel = T("keyframesLane"),
                keyframes = this._keyframes.Select(frame => analysis.SourcePts[frame] * 1000).ToArray(),
                audioUrl = this._trackId.HasValue ? $"/api/split-audio/{this.RecordIndex}/input?trackId={this._trackId}&durationMs={Math.Ceiling(analysis.Duration * 1000)}&mode={this._audioMode}&quality=high&sessionId={this._snapshot.SessionId}" : null,
                chapters = analysis.Chapters.Select(c => new { timeMs = c.StartSeconds * 1000, name = c.Name }).ToArray(),
                lanes = Math.Max(1, clips.Select(item => item.Lane + 1).DefaultIfEmpty(1).Max()),
                clips = clips.Select(item =>
                {
                    int index = item.Output.Clips.IndexOf(item.Clip);
                    return new
                    {
                        id = item.Clip.Id,
                        outputId = item.Output.Id,
                        startMs = this.BoundarySeconds(item.Clip.StartFrame) * 1000,
                        endMs = this.BoundarySeconds(item.Clip.EndFrameExclusive) * 1000,
                        lane = item.Lane,
                        color = SplitEditorLayout.OutputColor(item.OutputIndex),
                        label = this.ShortName(item.Output.Id) + (item.Output.Clips.Count > 1 ? " · " + (index + 1).ToString(CultureInfo.InvariantCulture) : ""),
                        selected = item.Output.Id == this._outputId && item.Clip.Id == this.View.ClipId,
                        dim = !this._sourcePreview && item.Output.Id != this._outputId,
                        error = errors.Contains(item.Clip.Id)
                    };
                }).ToArray()
            };
        }

        /// <summary>
        /// Corsia audio della timeline
        /// </summary>
        /// <returns>Corsie audio</returns>
        private List<MediaTimelinePanelComponent.AudioLane> AudioLanes()
        {
            return new List<MediaTimelinePanelComponent.AudioLane>
            {
                new MediaTimelinePanelComponent.AudioLane
                {
                    ElementId = "split-audio-source",
                    Label = T("audio"),
                    Tracks = this._snapshot.SourceInfo.Tracks.Where(t => t.Type == "audio").ToList(),
                    SelectedTrackId = this._trackId,
                    OnTrackChanged = EventCallback.Factory.Create<int>(this, async id =>
                    {
                        this._trackId = id;
                        await this.RefreshAsync(false);
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
            await this.RefreshAsync(false);
        }

        /// <summary>
        /// Cambia il guadagno della forma d'onda
        /// </summary>
        /// <param name="value">Guadagno</param>
        private async Task GainAsync(double value)
        {
            this._gain = value;
            await this.RefreshAsync(false);
        }

        /// <summary>
        /// Ridisegna il componente, aggiorna la timeline e ricarica l'anteprima
        /// </summary>
        /// <param name="preview">True per ricaricare anche l'anteprima</param>
        /// <param name="waitPreview">True per attendere il frame. I gesti non lo attendono: la timeline JS accetta
        /// un nuovo gesto solo a callback conclusa, e un frame lento farebbe perdere i clic successivi.</param>
        private async Task RefreshAsync(bool preview = true, bool waitPreview = true)
        {
            if (this._disposed)
                return;
            this.StateHasChanged();
            if (this._module == null || this._framePreview == null || this._timeline == null)
                return;
            if (preview)
            {
                ++this._requestSequence;
                await this._framePreview.InvokeVoidAsync("cancel");
                if (this._disposed)
                    return;
            }
            await this._timeline.UpdateAsync(this.TimelineModel(), false);
            if (this._disposed)
                return;
            if (preview && waitPreview)
                await this.LoadPreviewAsync(this.PreviewResolution());
            else if (preview)
                _ = this.LoadPreviewInBackgroundAsync(this.PreviewResolution());
        }

        /// <summary>
        /// Carica un frame senza che il chiamante lo attenda: la chiusura del circuito durante il caricamento non è un errore
        /// </summary>
        /// <param name="frame">Frame da caricare</param>
        private async Task LoadPreviewInBackgroundAsync(MkvSplitFrameResolution frame)
        {
            try
            {
                await this.LoadPreviewAsync(frame);
            }
            catch (JSDisconnectedException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
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
            this._previewMessage = frame.IsValid ? T("loading") : this.UnresolvedPreviewMessage();
            this.StateHasChanged();
            try
            {
                bool committed = await this._framePreview.InvokeAsync<bool>("load", frame.IsValid ? $"/api/split-preview/{this.RecordIndex}/input/{frame.SourceFrame}?sessionId={this._snapshot.SessionId}&draftRevision={revision}&request={sequence}" : null);
                if (!this._disposed && sequence == this._requestSequence && revision == this._revision && committed)
                    this._previewMessage = frame.IsValid ? "" : this.UnresolvedPreviewMessage();
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
        /// Messaggio per un frame non risolto: output senza segmenti e frame non risolvibile sono casi diversi per l'utente
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
            this._busyText = T("applying");
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
                    this._feedback = result.Diagnostics;
                    // La revisione e' avanzata con la richiesta: la timeline JS deve riceverla o scarterebbe ogni gesto
                    await this.RefreshAsync();
                }
            }
            finally
            {
                this._busy = false;
            }
        }

        /// <summary>
        /// Sostituisce il draft con il documento della regola o con il sorgente intero, come passo annullabile.
        /// Il record cambia solo con Applica.
        /// </summary>
        private async Task ResetAsync()
        {
            if (this._busy || this._confirming || this._gestureToken != null || this._snapshot == null)
                return;
            this._busy = true;
            this._busyText = T("computingRule");
            this.StateHasChanged();
            SplitRuleDocumentResult result;
            try
            {
                result = await this.SplitOrchestrator.BuildRuleDocumentAsync(this._snapshot.SessionId, this._snapshot.ExpectedOptionsRevision, this._lifetime.Token);
            }
            finally
            {
                this._busy = false;
            }
            if (this._disposed)
                return;
            if (result.Document == null)
            {
                this._feedback = result.Diagnostics;
                this.StateHasChanged();
                return;
            }
            this.ClearGesture();
            this._undo.Push(this.Capture());
            this._redo.Clear();
            this._draft = result.Document;
            this._revision++;
            this._projection = this._service.Project(this._draft, this._snapshot.Analysis, this._snapshot.Options);
            this._views.Clear();
            this._feedback = new List<MkvSplitDiagnostic>();
            this._renamingId = null;
            this.SelectOutput(this._draft.Outputs.FirstOrDefault()?.Id ?? Guid.Empty);
            await this.RefreshAsync();
        }

        /// <summary>
        /// Diagnostiche visibili: errori e poi avvisi, quelle dell'ultimo comando e quelle della proiezione
        /// </summary>
        /// <returns>Diagnostiche, una per riga</returns>
        private List<MkvSplitDiagnostic> Issues()
        {
            IEnumerable<MkvSplitDiagnostic> all = this._feedback.Concat(this._projection?.Diagnostics ?? new List<MkvSplitDiagnostic>());
            return all.OrderByDescending(d => d.Severity == MkvSplitDiagnosticSeverity.Error).ToList();
        }

        /// <summary>
        /// Numero di errori della proiezione riferiti a un output
        /// </summary>
        /// <param name="outputId">Output</param>
        /// <returns>Numero di errori</returns>
        private int OutputErrorCount(Guid outputId)
        {
            return this._projection?.Diagnostics.Count(d => d.Severity == MkvSplitDiagnosticSeverity.Error && d.OutputId == outputId) ?? 0;
        }

        /// <summary>
        /// Segmenti con un errore della proiezione
        /// </summary>
        /// <returns>Identificativi dei segmenti</returns>
        private HashSet<Guid> ErrorClipIds()
        {
            return new HashSet<Guid>((this._projection?.Diagnostics ?? new List<MkvSplitDiagnostic>())
                .Where(d => d.Severity == MkvSplitDiagnosticSeverity.Error && d.ClipId.HasValue).Select(d => d.ClipId.Value));
        }

        /// <summary>
        /// True se il segmento ha un errore della proiezione
        /// </summary>
        /// <param name="clipId">Segmento</param>
        /// <returns>True con almeno un errore</returns>
        private bool ClipHasError(Guid clipId)
        {
            return this._projection?.Diagnostics.Any(d => d.Severity == MkvSplitDiagnosticSeverity.Error && d.ClipId == clipId) == true;
        }

        /// <summary>
        /// True se la diagnostica indica un output ancora presente
        /// </summary>
        /// <param name="diagnostic">Diagnostica</param>
        /// <returns>True se Mostra può selezionarlo</returns>
        private bool CanGoTo(MkvSplitDiagnostic diagnostic)
        {
            return diagnostic.OutputId.HasValue && this._draft?.Outputs.Any(o => o.Id == diagnostic.OutputId.Value) == true;
        }

        /// <summary>
        /// Seleziona l'output e l'eventuale segmento della diagnostica, porta l'anteprima sul segmento e ne mette a fuoco l'elemento
        /// </summary>
        /// <param name="diagnostic">Diagnostica</param>
        private async Task GoToDiagnosticAsync(MkvSplitDiagnostic diagnostic)
        {
            if (!this.CanGoTo(diagnostic))
                return;
            await this.CancelGestureAsync();
            if (diagnostic.OutputId.Value != this._outputId)
                this.SelectOutput(diagnostic.OutputId.Value);
            MkvSplitClip clip = diagnostic.ClipId.HasValue ? this.Output?.Clips.Find(c => c.Id == diagnostic.ClipId.Value) : null;
            if (clip != null)
            {
                this.View.ClipId = clip.Id;
                this._sourcePreview = true;
                this._sourceFrame = clip.StartFrame;
            }
            this._pendingFocus = clip != null ? $"[data-split-clip=\"{clip.Id}\"]" : $"[data-split-output=\"{diagnostic.OutputId.Value}\"]";
            await this.RefreshAsync();
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
                await this.CancelGestureAsync();
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
            this.CloseMenu();
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
        /// Stato di vista di un output: cursore nel risultato e segmento selezionato
        /// </summary>
        private sealed class OutputView
        {
            #region Proprietà

            /// <summary>
            /// Cursore nel risultato, in secondi
            /// </summary>
            public double Seconds { get; set; }

            /// <summary>
            /// Segmento selezionato, vuoto se nessuno
            /// </summary>
            public Guid ClipId { get; set; }

            #endregion

            #region Metodi pubblici

            /// <summary>
            /// Copia indipendente della vista
            /// </summary>
            /// <returns>Vista copiata</returns>
            public OutputView Clone()
            {
                return new OutputView { Seconds = this.Seconds, ClipId = this.ClipId };
            }

            #endregion
        }

        /// <summary>
        /// Tipo di elemento trascinato nel pannello dei file
        /// </summary>
        private enum DragKind
        {
            /// <summary>
            /// Segmento di un output
            /// </summary>
            Segment,

            /// <summary>
            /// Output dell'elenco
            /// </summary>
            Output
        }

        /// <summary>
        /// Elemento trascinato nel pannello dei file
        /// </summary>
        /// <param name="Kind">Tipo di elemento</param>
        /// <param name="OutputId">Output di provenienza o trascinato</param>
        /// <param name="ClipId">Segmento trascinato, vuoto per un output</param>
        private sealed record DragPayload(DragKind Kind, Guid OutputId, Guid ClipId);

        /// <summary>
        /// Stato dell'editor registrato per annulla e ripristina
        /// </summary>
        /// <param name="Document">Documento</param>
        /// <param name="OutputId">Output attivo</param>
        /// <param name="Views">Viste per output</param>
        /// <param name="SourceFrame">Frame sorgente sotto il cursore</param>
        /// <param name="MarkIn">Inizio del tratto segnato</param>
        /// <param name="MarkOut">Fine esclusiva del tratto segnato</param>
        /// <param name="SourcePreview">True se l'anteprima mostrava il sorgente</param>
        private sealed record Checkpoint(MkvSplitDocument Document, Guid OutputId, Dictionary<Guid, OutputView> Views, int SourceFrame, int? MarkIn, int? MarkOut, bool SourcePreview);

        #endregion
    }
}
