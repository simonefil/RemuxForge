import { TimelineCanvas, drawTimeGrid, drawAmplitudeGrid, drawFrequencyScale } from './media-timeline.js';
import { captureEditorKeyboard as sharedKeyboard, createFramePreview as sharedPreview } from './media-timeline.js';

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
function invalidatePreview(host) { const root = host.closest('.split-montage'); for (const [canvas, preview] of previews) if (root?.contains(canvas)) preview.cancel(); }
export function focusEditor(root) { root?.focus({ preventScroll: true }); }
export function cancelEditorGestures(root) { for (const timeline of timelines) if (root.contains(timeline.host)) timeline.cancelGesture(); }
export function captureEditorKeyboard(root, reference) {
    // Un dialog Radzen aperto possiede focus ed Esc: non aprire conferme annidate.
    let disposed = false;
    const bridge = { invokeMethodAsync: (...args) => !disposed && root.contains(document.activeElement) ? reference.invokeMethodAsync(...args) : Promise.resolve() };
    const keyboard = sharedKeyboard(root, bridge);
    const escape = event => { if (event.key === 'Escape' && [...timelines].some(t => root.contains(t.host) && t.drag)) { event.preventDefault(); event.stopImmediatePropagation(); cancelEditorGestures(root); } };
    root.addEventListener('keydown', escape, true);
    return { dispose() { disposed = true; root.removeEventListener('keydown', escape, true); keyboard.dispose(); } };
}

// Solo presentazione: intervalli delle clip e posizioni risultato arrivano dalla proiezione Core.
class MontageTimeline extends TimelineCanvas {
    constructor(host, canvas, reference, model) {
        // Instrada la callback del canvas condiviso senza cambiare il contratto della timeline Remux.
        const bridge = { invokeMethodAsync: (method, ...args) => method === 'OnTimelineSeek'
            ? reference.invokeMethodAsync('OnMontageSeek', this.model.side, ...args)
            : reference.invokeMethodAsync(method, ...args) };
        super(host, canvas, bridge, model, { navigatorAudioKey: 'source' });
        this.reference = reference;
        this.viewports = new Map();
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
        if (this.drag?.token && (model.outputId !== this.drag.outputId || model.revision !== this.drag.revision)) this.cancelGesture();
        const gestureViewport = this.drag?.token ? this.drag.viewport : null;
        const changed = model.side === 'result' && model.outputId !== this.model.outputId;
        if (changed) this.viewports.set(this.model.outputId, { scale: this.pixelsPerMs, scroll: this.host.scrollLeft, fit: this.fitToViewport });
        super.update(model, center);
        if (gestureViewport) { this.pixelsPerMs = gestureViewport.scale; this.fitToViewport = gestureViewport.fit; this.updateRange(); this.host.scrollLeft = gestureViewport.scroll; this.draw(); }
        if (changed) {
            const view = this.viewports.get(model.outputId);
            if (view) { this.pixelsPerMs = view.scale; this.fitToViewport = view.fit; this.constrainScale(); this.updateRange(); this.host.scrollLeft = view.scroll; }
            else this.fitTimeline();
            this.draw();
        }
    }
    // Corsia principale e navigatore chiamano entrambi questo metodo, cosi' nessuno dei due mostra
    // un output riordinato o duplicato come audio sorgente continuo.
    drawAudioImage(image, start, end, left, right, top, height, color, tint, gain) {
        if (this.model.side !== 'result') { super.drawAudioImage(image, start, end, left, right, top, height, color, tint, gain); return; }
        if (end <= start) return;
        for (const clip of this.model.clips || []) {
            const from = Math.max(start, clip.startMs), to = Math.min(end, clip.endMs);
            if (to <= from) continue;
            const x1 = left + (from - start) / (end - start) * (right - left);
            const x2 = left + (to - start) / (end - start) * (right - left);
            super.drawAudioImage(image, clip.sourceStartMs + from - clip.startMs, clip.sourceStartMs + to - clip.startMs, x1, x2, top, height, color, tint, gain);
        }
    }
    drawContent(ctx, layout, colors) {
        const { width, height, plotLeft, trackTop, startMs, endMs } = layout;
        const bottom = height - 30, audioTop = trackTop + 18, audioHeight = Math.max(15, bottom - audioTop - 4);
        drawTimeGrid(ctx, width, plotLeft, startMs, endMs, this.pixelsPerMs, trackTop, bottom, colors.border);
        const selectionLeft = Math.max(plotLeft, this.xAtTime(this.model.selectionStartMs)), selectionRight = Math.min(width, this.xAtTime(this.model.selectionEndMs));
        if (selectionRight > selectionLeft) { ctx.fillStyle = colors.primary; ctx.globalAlpha = .12; ctx.fillRect(selectionLeft, trackTop, selectionRight - selectionLeft, height - trackTop); ctx.globalAlpha = 1; }
        if (this.model.audioMode === 'spectrogram') drawFrequencyScale(ctx, plotLeft, audioTop, audioHeight, colors.secondary, colors.fontFamily, this.model.nyquistHz);
        else drawAmplitudeGrid(ctx, plotLeft, width, audioTop, audioHeight, colors.border, colors.secondary, colors.fontFamily);
        this.drawAudioImage(this.audioImages.get('source'), startMs, endMs, plotLeft, width, audioTop, audioHeight, colors.info, this.model.audioMode !== 'spectrogram');
        ctx.fillStyle = colors.secondary;
        for (const time of this.model.keyframes || []) { const x = this.xAtTime(time); if (x >= plotLeft && x <= width) ctx.fillRect(x, bottom - 5, 1, 5); }
        for (const chapter of this.model.chapters || []) {
            const x = this.xAtTime(chapter.timeMs);
            if (x < plotLeft || x > width) continue;
            ctx.fillStyle = colors.secondary; ctx.fillText(chapter.name || '', x + 3, trackTop + 9);
            ctx.fillRect(x, trackTop, 1, bottom - trackTop);
        }
        if (this.model.side === 'result') for (const clip of this.model.clips || []) {
            const x1 = Math.max(plotLeft, this.xAtTime(clip.startMs)), x2 = Math.min(width, this.xAtTime(clip.endMs));
            if (x2 <= x1) continue;
            ctx.fillStyle = clip.selected ? colors.warning : colors.success;
            ctx.globalAlpha = .5; ctx.fillRect(x1, bottom, x2 - x1, 27); ctx.globalAlpha = 1;
            ctx.strokeStyle = clip.selected ? colors.warning : colors.border; ctx.lineWidth = clip.selected ? 3 : 1; ctx.strokeRect(x1, bottom, x2 - x1, 27); ctx.lineWidth = 1;
            ctx.fillStyle = colors.text; if (x2 - x1 > 15) ctx.fillText(clip.label, x1 + 5, bottom + 14);
            ctx.fillRect(x1, bottom, 3, 27); ctx.fillRect(x2 - 3, bottom, 3, 27);
        }
    }
    hitContent(x, y, event) {
        if (this.model.side !== 'result' || y < this.host.clientHeight - 46) return null;
        for (const clip of this.model.clips || []) for (const start of [true, false]) {
            if (Math.abs(x - this.xAtTime(start ? clip.startMs : clip.endMs)) < 6)
                return { kind: 'trim', clip, start, revision: this.model.revision, timeMs: start ? clip.startMs : clip.endMs, pointerId: event.pointerId };
        }
        for (const clip of this.model.clips || []) if (x >= this.xAtTime(clip.startMs) && x < this.xAtTime(clip.endMs))
            return { kind: 'reorder', clip, revision: this.model.revision, timeMs: this.timeAtX(x), pointerId: event.pointerId };
        return null;
    }
    onContentDragMove(drag, x) {
        if (drag.kind !== 'trim' && drag.kind !== 'reorder') return;
        const time = drag.viewport ? (drag.viewport.scroll + x - this.plotLeft) / drag.viewport.scale : this.timeAtX(x);
        drag.timeMs = drag.kind === 'trim' ? time : Math.max(0, Math.min(this.model.durationMs, time));
        this.draw();
    }
    handlePointerDown(event) {
        if (this.sending || this.drag) return;
        const root = this.host.closest('.split-montage');
        if ([...timelines].some(t => t !== this && root?.contains(t.host) && (t.sending || t.drag?.token))) return;
        super.handlePointerDown(event);
        if (this.drag) this.drag.viewport = { scale: this.pixelsPerMs, scroll: this.host.scrollLeft, fit: this.fitToViewport };
        if (!this.drag || !['seek', 'trim', 'reorder'].includes(this.drag.kind)) return;
        Object.assign(this.drag, { token: crypto.randomUUID(), revision: this.model.revision, outputId: this.model.outputId, side: this.model.side, originalClip: this.drag.clip, originalClips: this.model.clips });
        this.sendGesture(this.packet(this.drag, 'begin'));
    }
    handlePointerMove(event) {
        super.handlePointerMove(event);
        if (this.drag?.token) this.queueGesture(this.packet(this.drag, 'move'));
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
        const clip = drag.originalClip;
        let ms = drag.timeMs, index = 0;
        if (drag.kind === 'trim') ms = Math.max(0, Math.min(this.model.sourceDurationMs,
            (drag.start ? clip.sourceStartMs : clip.sourceEndMs) + drag.timeMs - (drag.start ? clip.startMs : clip.endMs)));
        if (drag.kind === 'reorder') index = (drag.originalClips || []).filter(c => c.id !== clip.id && (c.startMs + c.endMs) / 2 < drag.timeMs).length;
        return [drag.token, phase, drag.kind, drag.side, clip?.id || '', !!drag.start, ms, index, drag.revision, drag.outputId, ++this.sequence];
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
