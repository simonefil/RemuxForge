import { TimelineCanvas, drawTimeGrid, drawAmplitudeGrid, drawFrequencyScale } from './media-timeline.js';
import { captureEditorKeyboard as sharedKeyboard, createFramePreview as sharedPreview } from './media-timeline.js';

// Corsie dei segmenti in fondo alla timeline e tolleranza in pixel per afferrare un bordo
const LANE_HEIGHT = 24;
const LANE_GAP = 4;
const EDGE_TOLERANCE = 6;
// Spostamento in pixel oltre il quale un clic su un'area vuota diventa la selezione di un tratto
const RANGE_THRESHOLD = 4;
// Striscia dei keyframe fra la forma d'onda e le corsie, e riga dei nomi dei capitoli sotto il righello
const KEYFRAME_STRIP = 14;
const CHAPTER_ROW = 18;

const timelines = new Set();
const previews = new Map();
const editorFocus = new WeakMap();
export function rememberEditorFocus(root) { editorFocus.set(root, root.contains(document.activeElement) ? document.activeElement : root); }
export function restoreEditorFocus(root) { const target = editorFocus.get(root); (target?.isConnected ? target : root)?.focus({ preventScroll: true }); }
export function createFramePreview(canvas) {
    const preview = sharedPreview(canvas);
    const load = preview.load.bind(preview), dispose = preview.dispose.bind(preview);
    preview.load = async url => { try { return await load(url); } catch (error) { if (error?.name === 'AbortError') return false; throw error; } };
    preview.dispose = () => { previews.delete(canvas); dispose(); };
    previews.set(canvas, preview);
    return preview;
}
function invalidatePreview(host) { const root = host.closest('.split-editor'); for (const [canvas, preview] of previews) if (root?.contains(canvas)) preview.cancel(); }
export function focusEditor(root) { root?.focus({ preventScroll: true }); }
// Porta il focus sull'elemento indicato; un campo di testo riceve anche la selezione del contenuto.
export function focusEditorTarget(root, selector) {
    const target = root?.querySelector(selector);
    (target ?? root)?.focus({ preventScroll: false });
    if (target instanceof HTMLInputElement) target.select();
    target?.scrollIntoView({ block: 'nearest' });
}
export function cancelEditorGestures(root) { for (const timeline of timelines) if (root.contains(timeline.host)) timeline.cancelGesture(); }

function isEditing(target) {
    return target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement || target?.isContentEditable === true;
}

// Tastiera e trascinamento nel pannello dei file dell'editor di taglio.
export function captureEditorInput(root, reference) {
    // Un dialog Radzen aperto possiede focus ed Esc: non aprire conferme annidate.
    let disposed = false;
    const bridge = { invokeMethodAsync: (...args) => !disposed && root.contains(document.activeElement) ? reference.invokeMethodAsync(...args) : Promise.resolve() };
    // Nell'editor di taglio Maiusc+freccia avanza di un frame come la freccia semplice.
    const keyboard = sharedKeyboard(root, bridge, { shiftStep: 1 });
    const escape = event => {
        if (event.key !== 'Escape') return;
        if ([...timelines].some(t => root.contains(t.host) && t.drag)) { event.preventDefault(); event.stopImmediatePropagation(); cancelEditorGestures(root); return; }
        // Esc in un campo riporta il testo al valore del modello: il campo non ha ancora confermato la modifica.
        const field = event.target;
        if ((field instanceof HTMLInputElement || field instanceof HTMLSelectElement) && field.dataset.model !== undefined) field.value = field.dataset.model;
    };
    // Scorciatoie dell'editor: I, O, S, Ctrl/Cmd+Z, Ctrl/Cmd+Maiusc+Z e Ctrl+Y.
    const shortcuts = event => {
        if (event.defaultPrevented || isEditing(event.target)) return;
        const key = event.key.toLowerCase();
        let command = null;
        if ((event.ctrlKey || event.metaKey) && !event.altKey) {
            if (key === 'z') command = event.shiftKey ? 'Redo' : 'Undo';
            else if (key === 'y') command = 'Redo';
        } else if (!event.ctrlKey && !event.metaKey && !event.altKey) {
            if (key === 'i' || key === 'o' || key === 's') command = key;
        }
        if (!command) return;
        event.preventDefault();
        event.stopPropagation();
        bridge.invokeMethodAsync('OnEditorKey', command, event.shiftKey, false);
    };
    const clearDropTarget = () => { for (const zone of root.querySelectorAll('.drop-over')) zone.classList.remove('drop-over'); };
    const dragFinished = () => { root.classList.remove('is-dragging', 'is-dragging-segment', 'is-dragging-output'); clearDropTarget(); };
    const dropZone = event => {
        const zone = event.target instanceof Element ? event.target.closest('[data-drop]') : null;
        return zone && root.contains(zone) && root.classList.contains('is-dragging-' + zone.dataset.drop) ? zone : null;
    };
    const dragStart = event => {
        const source = event.target instanceof Element ? event.target.closest('[data-drag-kind]') : null;
        if (!source || !root.contains(source)) return;
        event.dataTransfer.effectAllowed = 'copyMove';
        try { event.dataTransfer.setData('text/plain', source.dataset.dragLabel || ''); } catch { }
        // Le zone di rilascio compaiono dopo l'avvio: cambiare il layout dentro dragstart annulla il trascinamento.
        setTimeout(() => root.classList.add('is-dragging', 'is-dragging-' + source.dataset.dragKind), 0);
    };
    const dragOver = event => {
        const zone = dropZone(event);
        if (!zone) return;
        event.preventDefault();
        event.dataTransfer.dropEffect = event.altKey && zone.dataset.drop === 'segment' ? 'copy' : 'move';
        if (!zone.classList.contains('drop-over')) { clearDropTarget(); zone.classList.add('drop-over'); }
    };
    const dragLeave = event => { const zone = dropZone(event); if (zone && !zone.contains(event.relatedTarget)) zone.classList.remove('drop-over'); };
    const drop = () => setTimeout(dragFinished, 0);
    // Un pulsante che sparisce dopo il clic (Unisci, elimina, voce di menu) lascia il focus sul body:
    // l'editor lo riprende, cosi' le scorciatoie restano attive.
    const focusLost = event => {
        if (event.relatedTarget) return;
        setTimeout(() => { if (!disposed && (!document.activeElement || document.activeElement === document.body)) root.focus({ preventScroll: true }); }, 0);
    };
    // Se il focus e' gia' finito sul body, il tasto arriva al documento: l'editor riprende il focus e lo riceve.
    const orphanKey = event => {
        if (disposed || event.target !== document.body || document.querySelector('.rz-dialog-wrapper')) return;
        root.focus({ preventScroll: true });
        const copy = new KeyboardEvent('keydown', { key: event.key, code: event.code, ctrlKey: event.ctrlKey, metaKey: event.metaKey, shiftKey: event.shiftKey, altKey: event.altKey, repeat: event.repeat, bubbles: true, cancelable: true });
        root.dispatchEvent(copy);
        if (copy.defaultPrevented) event.preventDefault();
    };
    root.addEventListener('keydown', escape, true);
    root.addEventListener('keydown', shortcuts);
    root.addEventListener('dragstart', dragStart);
    root.addEventListener('dragover', dragOver);
    root.addEventListener('dragleave', dragLeave);
    root.addEventListener('drop', drop);
    root.addEventListener('dragend', dragFinished);
    root.addEventListener('focusout', focusLost);
    document.addEventListener('keydown', orphanKey);
    return {
        dispose() {
            disposed = true;
            root.removeEventListener('keydown', escape, true);
            root.removeEventListener('keydown', shortcuts);
            root.removeEventListener('dragstart', dragStart);
            root.removeEventListener('dragover', dragOver);
            root.removeEventListener('dragleave', dragLeave);
            root.removeEventListener('drop', drop);
            root.removeEventListener('dragend', dragFinished);
            root.removeEventListener('focusout', focusLost);
            document.removeEventListener('keydown', orphanKey);
            dragFinished();
            keyboard.dispose();
        }
    };
}

// Testo ridotto con i puntini alla larghezza disponibile, vuoto se non ci sta nemmeno un carattere.
function fitText(ctx, text, maxWidth) {
    if (maxWidth <= 0 || !text) return '';
    if (ctx.measureText(text).width <= maxWidth) return text;
    let length = text.length;
    while (length > 0 && ctx.measureText(text.slice(0, length) + '…').width > maxWidth) length--;
    return length > 0 ? text.slice(0, length) + '…' : '';
}

function fillRounded(ctx, x, y, width, height, radius) {
    ctx.beginPath();
    if (ctx.roundRect) ctx.roundRect(x, y, width, height, Math.min(radius, width / 2));
    else ctx.rect(x, y, width, height);
    ctx.fill();
}

// Timeline del sorgente con i segmenti di tutti i file di uscita. Solo presentazione: intervalli,
// corsie e colori arrivano dal .NET, che valuta ed esegue ogni gesto sul documento.
class MontageTimeline extends TimelineCanvas {
    constructor(host, canvas, reference, model) {
        super(host, canvas, reference, model, { navigatorAudioKey: 'source' });
        this.reference = reference;
        this.pending = null;
        this.sending = false;
        this.sequence = 0;
        this.onLostCapture = () => this.cancelGesture();
        this.canvas.addEventListener('lostpointercapture', this.onLostCapture);
        timelines.add(this);
        this.start();
    }
    audioUrls(model) { return { source: model.audioUrl }; }
    update(model, center) {
        if (this.drag?.token && model.revision !== this.drag.revision) this.cancelGesture();
        const gestureViewport = this.drag?.token ? this.drag.viewport : null;
        super.update(model, center);
        if (gestureViewport) { this.pixelsPerMs = gestureViewport.scale; this.fitToViewport = gestureViewport.fit; this.updateRange(); this.host.scrollLeft = gestureViewport.scroll; this.draw(); }
    }
    lanesTop() {
        const height = Math.max(1, this.host.clientHeight - 16);
        return height - Math.max(1, this.model.lanes || 1) * (LANE_HEIGHT + LANE_GAP) - 2;
    }
    laneTop(lane) { return this.lanesTop() + lane * (LANE_HEIGHT + LANE_GAP); }
    lanesBottom() { return this.laneTop(Math.max(1, this.model.lanes || 1) - 1) + LANE_HEIGHT; }
    // Intervallo disegnato: durante un gesto il bordo trascinato segue subito il puntatore.
    clipRange(clip) {
        const drag = this.drag;
        if (drag?.kind === 'trim' && drag.clip.id === clip.id) return drag.start ? [drag.timeMs, clip.endMs] : [clip.startMs, drag.timeMs];
        return [clip.startMs, clip.endMs];
    }
    // Tratto disegnato: durante il trascinamento quello in corso, altrimenti quello segnato.
    selectionRange() {
        const drag = this.drag;
        if (drag?.kind === 'range') return [Math.min(drag.anchorMs, drag.timeMs), Math.max(drag.anchorMs, drag.timeMs)];
        return [this.model.selectionStartMs, this.model.selectionEndMs];
    }
    drawContent(ctx, layout, colors) {
        const { width, height, plotLeft, trackTop, startMs, endMs } = layout;
        const lanesTop = this.lanesTop(), keyframesTop = lanesTop - KEYFRAME_STRIP;
        const audioTop = trackTop + ((this.model.chapters || []).length > 0 ? CHAPTER_ROW : 4), audioHeight = Math.max(15, keyframesTop - 4 - audioTop);
        drawTimeGrid(ctx, width, plotLeft, startMs, endMs, this.pixelsPerMs, trackTop, height, colors.border);
        const [selectionStart, selectionEnd] = this.selectionRange();
        const selectionLeft = Math.max(plotLeft, this.xAtTime(selectionStart)), selectionRight = Math.min(width, this.xAtTime(selectionEnd));
        if (selectionRight > selectionLeft) {
            ctx.fillStyle = colors.text; ctx.globalAlpha = .1; ctx.fillRect(selectionLeft, trackTop, selectionRight - selectionLeft, height - trackTop); ctx.globalAlpha = 1;
            ctx.fillStyle = colors.text; ctx.fillRect(selectionLeft, trackTop, 2, height - trackTop); ctx.fillRect(selectionRight - 2, trackTop, 2, height - trackTop);
        }
        if (this.model.audioMode === 'spectrogram') drawFrequencyScale(ctx, plotLeft, audioTop, audioHeight, colors.secondary, colors.fontFamily, this.model.nyquistHz);
        else drawAmplitudeGrid(ctx, plotLeft, width, audioTop, audioHeight, colors.border, colors.secondary, colors.fontFamily);
        this.drawAudioImage(this.audioImages.get('source'), startMs, endMs, plotLeft, width, audioTop, audioHeight, colors.info, this.model.audioMode !== 'spectrogram');
        // Keyframe: tacche nella loro striscia, con il nome nel margine sinistro
        ctx.save();
        ctx.fillStyle = colors.secondary;
        ctx.font = `9px ${colors.fontFamily}`;
        ctx.textAlign = 'right';
        ctx.fillText(this.model.keyframesLabel || '', plotLeft - 5, keyframesTop + KEYFRAME_STRIP / 2 - 1);
        ctx.fillStyle = colors.warning;
        for (const time of this.model.keyframes || []) { const x = this.xAtTime(time); if (x >= plotLeft && x <= width) ctx.fillRect(Math.round(x), keyframesTop + 2, 2, KEYFRAME_STRIP - 6); }
        ctx.restore();
        const chapters = [...(this.model.chapters || [])].sort((a, b) => a.timeMs - b.timeMs);
        chapters.forEach((chapter, index) => {
            const x = this.xAtTime(chapter.timeMs);
            if (x < plotLeft || x > width) return;
            ctx.fillStyle = colors.secondary;
            ctx.fillRect(x, trackTop, 1, height - trackTop);
            // Il nome occupa lo spazio fino al capitolo successivo e si tronca con i puntini, come le bandierine Remux.
            const next = index + 1 < chapters.length ? this.xAtTime(chapters[index + 1].timeMs) : width;
            const label = fitText(ctx, chapter.name || '', Math.min(next, width) - x - 7);
            if (label) ctx.fillText(label, x + 3, trackTop + 9);
        });
        ctx.save();
        ctx.fillStyle = colors.border; ctx.globalAlpha = .25;
        for (let lane = 0; lane < Math.max(1, this.model.lanes || 1); lane++) ctx.fillRect(plotLeft, this.laneTop(lane), width - plotLeft, LANE_HEIGHT);
        ctx.restore();
        ctx.save();
        ctx.font = `600 11px ${colors.fontFamily}`;
        for (const clip of this.model.clips || []) {
            const [start, end] = this.clipRange(clip);
            const x1 = Math.max(plotLeft, this.xAtTime(Math.min(start, end))), x2 = Math.min(width, this.xAtTime(Math.max(start, end)));
            if (x2 <= x1) continue;
            const y = this.laneTop(clip.lane);
            ctx.globalAlpha = clip.dim ? .4 : 1;
            ctx.fillStyle = clip.color;
            fillRounded(ctx, x1, y, x2 - x1, LANE_HEIGHT, 4);
            ctx.fillStyle = 'rgba(0, 0, 0, .25)';
            ctx.fillRect(x1, y, Math.min(4, x2 - x1), LANE_HEIGHT);
            ctx.fillRect(Math.max(x1, x2 - 4), y, Math.min(4, x2 - x1), LANE_HEIGHT);
            if (clip.error) { ctx.strokeStyle = colors.danger; ctx.lineWidth = 2; ctx.setLineDash([4, 3]); ctx.strokeRect(x1 + 1, y + 1, x2 - x1 - 2, LANE_HEIGHT - 2); ctx.setLineDash([]); }
            if (clip.selected) { ctx.strokeStyle = colors.text; ctx.lineWidth = 2; ctx.strokeRect(x1 + 1, y + 1, x2 - x1 - 2, LANE_HEIGHT - 2); }
            const label = fitText(ctx, clip.label, x2 - x1 - 14);
            if (label) { ctx.fillStyle = '#14202a'; ctx.fillText(label, x1 + 7, y + LANE_HEIGHT / 2); }
        }
        ctx.restore();
        ctx.globalAlpha = 1;
        ctx.lineWidth = 1;
    }
    // Bordo del segmento selezionato, segmento o niente sotto il puntatore. Su due bordi vicini decide il lato del puntatore.
    clipHit(x, y) {
        const top = this.lanesTop();
        if (y < top) return null;
        const lane = Math.floor((y - top) / (LANE_HEIGHT + LANE_GAP));
        if (lane >= Math.max(1, this.model.lanes || 1) || y - this.laneTop(lane) > LANE_HEIGHT) return null;
        const clips = (this.model.clips || []).filter(c => c.lane === lane);
        let best = null;
        for (const clip of clips.filter(c => c.selected)) for (const start of [true, false]) {
            const edge = this.xAtTime(start ? clip.startMs : clip.endMs);
            const distance = Math.abs(x - edge);
            if (distance > EDGE_TOLERANCE) continue;
            const score = distance - ((start ? x >= edge : x <= edge) ? .5 : 0);
            if (!best || score < best.score) best = { clip, start, score };
        }
        if (best) return { kind: 'trim', clip: best.clip, start: best.start };
        for (const clip of clips) if (x >= this.xAtTime(clip.startMs) && x < this.xAtTime(clip.endMs)) return { kind: 'select', clip };
        return null;
    }
    hitContent(x, y, event) {
        const hit = this.clipHit(x, y);
        if (!hit) return null;
        const timeMs = hit.kind === 'trim' ? (hit.start ? hit.clip.startMs : hit.clip.endMs) : this.timeAtX(x);
        return { ...hit, revision: this.model.revision, timeMs, pointerId: event.pointerId };
    }
    contentCursor(x, y) {
        const hit = this.clipHit(x, y);
        return !hit ? 'default' : hit.kind === 'select' ? 'pointer' : 'ew-resize';
    }
    dragReadoutMs(drag) {
        return ['trim', 'select', 'range'].includes(drag.kind) ? drag.timeMs : super.dragReadoutMs(drag);
    }
    onContentDragMove(drag, x) {
        const time = drag.viewport ? (drag.viewport.scroll + x - this.plotLeft) / drag.viewport.scale : this.timeAtX(x);
        drag.timeMs = Math.max(0, Math.min(this.model.durationMs, time));
        this.canvas.style.cursor = drag.kind === 'select' ? 'pointer' : drag.kind === 'range' ? 'text' : 'ew-resize';
        this.draw();
    }
    handlePointerDown(event) {
        if (this.sending || this.drag) return;
        super.handlePointerDown(event);
        if (this.drag) this.drag.viewport = { scale: this.pixelsPerMs, scroll: this.host.scrollLeft, fit: this.fitToViewport };
        if (!this.drag || !['seek', 'select', 'trim'].includes(this.drag.kind)) return;
        // Un clic su un'area vuota sposta il cursore; se il puntatore si allontana diventa la selezione di un tratto
        if (this.drag.kind === 'seek') Object.assign(this.drag, { originX: event.clientX, anchorMs: this.drag.timeMs });
        Object.assign(this.drag, { token: crypto.randomUUID(), revision: this.model.revision });
        this.sendGesture(this.packet(this.drag, 'begin'));
    }
    handlePointerMove(event) {
        if (this.drag?.kind === 'seek' && this.drag.token && Math.abs(event.clientX - this.drag.originX) > RANGE_THRESHOLD) this.drag.kind = 'range';
        super.handlePointerMove(event);
        if (this.drag?.token) this.queueGesture(this.packet(this.drag, 'move'));
    }
    // Tasto destro: su una barra il menu del segmento, sul tratto segnato quello del tratto
    handleContextMenu(event) {
        event.preventDefault();
        if (this.drag || this.sending) return;
        const rect = this.canvas.getBoundingClientRect();
        const x = event.clientX - rect.left, y = event.clientY - rect.top;
        if (y <= this.navigatorHeight + this.rulerHeight) return;
        const hit = this.clipHit(x, y);
        if (hit) { this.reference.invokeMethodAsync('OnTimelineContextMenu', 'clip', hit.clip.id, hit.clip.outputId, event.clientX, event.clientY); return; }
        const [start, end] = this.selectionRange();
        const time = this.timeAtX(x);
        if (end > start && time >= start && time <= end) this.reference.invokeMethodAsync('OnTimelineContextMenu', 'range', '', '', event.clientX, event.clientY);
    }
    handlePointerUp(event) {
        if (!this.drag?.token) { if (event.type === 'pointercancel') this.cancelGesture(); else super.handlePointerUp(event); return; }
        const drag = this.drag;
        this.drag = null;
        this.queueGesture(this.packet(drag, event.type === 'pointercancel' ? 'cancel' : 'end'));
        try { this.canvas.releasePointerCapture(event.pointerId); } catch { }
        this.draw();
    }
    packet(drag, phase) {
        const ms = Math.max(0, Math.min(this.model.durationMs, drag.timeMs));
        return [drag.token, phase, drag.kind, drag.clip?.id || '', drag.clip?.outputId || '', !!drag.start, ms, drag.anchorMs ?? ms, drag.revision, ++this.sequence];
    }
    queueGesture(packet) {
        invalidatePreview(this.host);
        // Al massimo una callback in corso e un solo campione piu' recente. Fine/annulla sostituisce
        // i movimenti pendenti e l'inizio viene sempre inviato prima di qualsiasi campione.
        this.pending = packet;
        if (!this.sending) this.flushGesture();
    }
    async sendGesture(packet) {
        this.sending = true;
        try { await this.reference.invokeMethodAsync('OnMontageGesture', ...packet); }
        catch (error) { if (!this.disposed) console.warn('Split gesture unavailable', error); }
        finally { this.sending = false; if (!this.disposed) this.flushGesture(); }
    }
    flushGesture() { const packet = this.pending; this.pending = null; if (packet) this.sendGesture(packet); }
    cancelGesture() {
        if (!this.drag) return;
        const drag = this.drag; this.drag = null;
        if (drag.token) this.queueGesture(this.packet(drag, 'cancel'));
        else if (drag.viewport) { this.pixelsPerMs = drag.viewport.scale; this.fitToViewport = drag.viewport.fit; this.updateRange(); this.host.scrollLeft = drag.viewport.scroll; }
        try { this.canvas.releasePointerCapture(drag.pointerId); } catch { }
        this.draw();
    }
    dispose() {
        this.disposed = true; this.pending = null; this.drag = null;
        timelines.delete(this); this.canvas.removeEventListener('lostpointercapture', this.onLostCapture);
        super.dispose();
    }
}
export function createTimeline(host, canvas, reference, model) { return new MontageTimeline(host, canvas, reference, model); }
