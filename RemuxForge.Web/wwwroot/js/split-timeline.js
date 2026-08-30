import {
    TimelineCanvas,
    drawTimeGrid,
    drawAmplitudeGrid,
    drawFrequencyScale
} from './media-timeline.js';

export { createFramePreview, captureEditorKeyboard, confirmDiscard } from './media-timeline.js';

/**
 * Timeline dell'editor Split: una corsia audio, le tacche dei capitoli e dei keyframe e i blocchi
 * dei segmenti, con trascinamento dei confini di inizio e fine di ciascun segmento.
 */
class SplitTimeline extends TimelineCanvas {
    constructor(host, canvas, dotNetReference, model) {
        super(host, canvas, dotNetReference, model, { navigatorAudioKey: 'source' });
        this.pendingDrag = null;
        this.dragFrame = null;
        this.start();
    }

    audioUrls(model) {
        return { source: model.audioUrl };
    }

    /** Geometria delle corsie, condivisa fra disegno e hit test. */
    lanes(layout) {
        const { height, trackTop } = layout;
        const laneGap = 4;
        const chapterHeight = 14;
        const keyframeHeight = 10;
        const segmentHeight = 26;
        const chapterTop = trackTop + laneGap;
        const audioTop = chapterTop + chapterHeight + laneGap;
        const audioHeight = Math.max(24, height - audioTop - keyframeHeight - segmentHeight - laneGap * 3);
        const keyframeTop = audioTop + audioHeight + laneGap;
        const segmentTop = keyframeTop + keyframeHeight + laneGap;
        return { chapterTop, chapterHeight, audioTop, audioHeight, keyframeTop, keyframeHeight, segmentTop, segmentHeight };
    }

    drawContent(ctx, layout, colors) {
        const { width, plotLeft, trackTop, startMs, endMs } = layout;
        const lane = this.lanes(layout);
        const spectrogram = this.model.audioMode === 'spectrogram';
        ctx.fillStyle = colors.background;
        ctx.fillRect(plotLeft, lane.audioTop, width - plotLeft, lane.audioHeight);
        drawTimeGrid(ctx, width, plotLeft, startMs, endMs, this.pixelsPerMs, trackTop, lane.segmentTop + lane.segmentHeight, colors.border);
        if (spectrogram) {
            drawFrequencyScale(ctx, plotLeft, lane.audioTop, lane.audioHeight, colors.secondary, colors.fontFamily, this.model.nyquistHz);
        } else {
            drawAmplitudeGrid(ctx, plotLeft, width, lane.audioTop, lane.audioHeight, colors.border, colors.secondary, colors.fontFamily);
        }
        ctx.strokeStyle = colors.border;
        ctx.strokeRect(plotLeft + 0.5, lane.audioTop + 0.5, width - plotLeft - 1, lane.audioHeight - 1);
        ctx.fillStyle = colors.secondary;
        ctx.fillText(this.model.labels?.chapters || 'CHAPTERS', 7, lane.chapterTop + lane.chapterHeight / 2);
        ctx.fillText(this.model.labels?.audio || 'AUDIO', 7, lane.audioTop + lane.audioHeight / 2);
        ctx.fillText(this.model.labels?.keyframes || 'KEYFRAMES', 7, lane.keyframeTop + lane.keyframeHeight / 2);
        ctx.fillText(this.model.labels?.segments || 'SEGMENTS', 7, lane.segmentTop + lane.segmentHeight / 2);
        this.drawAudioImage(this.audioImages.get('source'), startMs, endMs, plotLeft, width, lane.audioTop, lane.audioHeight, colors.info, !spectrogram);

        // I keyframe si infittiscono fino a diventare una banda piena: sotto i due pixel di passo
        // la corsia mente, quindi si disegna solo quando le tacche restano distinguibili
        const keyframes = this.model.keyframes || [];
        if (keyframes.length > 0) {
            const step = this.pixelsPerMs * (this.model.durationMs / Math.max(1, keyframes.length));
            ctx.strokeStyle = colors.secondary;
            ctx.lineWidth = 1;
            if (step >= 3) {
                ctx.beginPath();
                for (const timeMs of keyframes) {
                    if (timeMs < startMs || timeMs > endMs) continue;
                    const x = Math.round(this.xAtTime(timeMs)) + 0.5;
                    ctx.moveTo(x, lane.keyframeTop);
                    ctx.lineTo(x, lane.keyframeTop + lane.keyframeHeight);
                }
                ctx.stroke();
            } else {
                ctx.save();
                ctx.globalAlpha = 0.35;
                ctx.fillStyle = colors.secondary;
                ctx.fillRect(plotLeft, lane.keyframeTop, width - plotLeft, lane.keyframeHeight);
                ctx.restore();
            }
        }

        for (const chapter of this.model.chapters || []) {
            const x = this.xAtTime(chapter.timeMs);
            if (x < plotLeft || x > width) continue;
            ctx.strokeStyle = colors.secondary;
            ctx.beginPath();
            ctx.moveTo(Math.round(x) + 0.5, lane.chapterTop);
            ctx.lineTo(Math.round(x) + 0.5, lane.chapterTop + lane.chapterHeight);
            ctx.stroke();
            if (chapter.name) {
                ctx.fillStyle = colors.secondary;
                ctx.fillText(chapter.name, x + 4, lane.chapterTop + lane.chapterHeight / 2);
            }
        }

        for (const segment of this.model.segments || []) {
            const startMsValue = this.dragTime(segment, true);
            const endMsValue = this.dragTime(segment, false);
            const x1 = this.xAtTime(startMsValue);
            const x2 = this.xAtTime(endMsValue);
            if (x2 < plotLeft || x1 > width) continue;
            const left = Math.max(plotLeft, x1);
            const right = Math.min(width, x2);
            const blockWidth = Math.max(2, right - left);
            ctx.save();
            ctx.globalAlpha = segment.excluded ? 0.2 : 0.55;
            ctx.fillStyle = segment.excluded ? colors.secondary : (segment.selected ? colors.warning : colors.success);
            ctx.fillRect(left, lane.segmentTop, blockWidth, lane.segmentHeight);
            ctx.restore();
            ctx.strokeStyle = segment.selected ? colors.warning : colors.border;
            ctx.lineWidth = segment.selected ? 2 : 1;
            ctx.strokeRect(left + 0.5, lane.segmentTop + 0.5, blockWidth - 1, lane.segmentHeight - 1);
            ctx.fillStyle = colors.text;
            if (blockWidth > 34) {
                ctx.save();
                ctx.beginPath();
                ctx.rect(left + 3, lane.segmentTop, blockWidth - 6, lane.segmentHeight);
                ctx.clip();
                ctx.fillText(segment.label || '', left + 5, lane.segmentTop + lane.segmentHeight / 2);
                ctx.restore();
            }

            // Le maniglie stanno sui confini: si trascinano anche quando il blocco è largo due pixel
            ctx.fillStyle = segment.selected ? colors.warning : colors.success;
            if (x1 >= plotLeft) ctx.fillRect(x1 - 2, lane.segmentTop, 4, lane.segmentHeight);
            if (x2 <= width) ctx.fillRect(x2 - 2, lane.segmentTop, 4, lane.segmentHeight);
        }
        ctx.lineWidth = 1;
    }

    /** Tempo di un confine tenendo conto del trascinamento in corso. */
    dragTime(segment, isStart) {
        if (this.drag && this.drag.kind === 'boundary' && this.drag.segmentIndex === segment.index && this.drag.isStart === isStart) return this.drag.timeMs;
        return isStart ? segment.startMs : segment.endMs;
    }

    hitContent(x, y, event) {
        const layout = { width: this.host.clientWidth, height: Math.max(1, this.host.clientHeight - 16), trackTop: this.navigatorHeight + this.rulerHeight };
        const lane = this.lanes(layout);
        if (y < lane.segmentTop || y > lane.segmentTop + lane.segmentHeight) return null;
        for (const segment of this.model.segments || []) {
            for (const isStart of [true, false]) {
                const boundaryX = this.xAtTime(isStart ? segment.startMs : segment.endMs);
                if (Math.abs(boundaryX - x) <= 6) {
                    const drag = { kind: 'boundary', segmentIndex: segment.index, isStart, timeMs: isStart ? segment.startMs : segment.endMs, pointerId: event.pointerId };
                    drag.selectionPromise = this.dotNetReference.invokeMethodAsync('OnSegmentSelected', segment.index);
                    return drag;
                }
            }
        }
        for (const segment of this.model.segments || []) {
            if (x >= this.xAtTime(segment.startMs) && x <= this.xAtTime(segment.endMs)) {
                return { kind: 'select', segmentIndex: segment.index, pointerId: event.pointerId, selectionPromise: this.dotNetReference.invokeMethodAsync('OnSegmentSelected', segment.index) };
            }
        }
        return null;
    }

    onContentDragMove(drag, x) {
        if (drag.kind !== 'boundary') return;
        drag.timeMs = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(x)));
        this.pendingDrag = { index: drag.segmentIndex, isStart: drag.isStart, timeMs: drag.timeMs };
        if (!this.dragFrame) {
            this.dragFrame = requestAnimationFrame(() => {
                const pending = this.pendingDrag;
                this.dragFrame = null;
                if (pending) Promise.resolve(this.drag?.selectionPromise).then(() => this.dotNetReference.invokeMethodAsync('OnSegmentBoundaryDrag', pending.index, pending.isStart, pending.timeMs, false));
            });
        }
        this.draw();
    }

    onContentDragEnd(drag) {
        if (this.dragFrame) {
            cancelAnimationFrame(this.dragFrame);
            this.dragFrame = null;
        }
        this.pendingDrag = null;
        if (drag.kind === 'boundary') Promise.resolve(drag.selectionPromise).then(() => this.dotNetReference.invokeMethodAsync('OnSegmentBoundaryDrag', drag.segmentIndex, drag.isStart, drag.timeMs, true));
    }

    dispose() {
        if (this.dragFrame) cancelAnimationFrame(this.dragFrame);
        this.dragFrame = null;
        this.pendingDrag = null;
        super.dispose();
    }
}

export function createTimeline(host, canvas, dotNetReference, model) {
    return new SplitTimeline(host, canvas, dotNetReference, model);
}
