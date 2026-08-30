import {
    TimelineCanvas,
    drawTimeGrid,
    drawAmplitudeGrid,
    drawFrequencyScale
} from './media-timeline.js';

export { createPreviewPair, createFramePreview, captureEditorKeyboard, confirmDiscard } from './media-timeline.js';

/**
 * Timeline dell'editor EditMap: due corsie audio (sorgente e lingua), i segmenti della mappa
 * e le operazioni, con trascinamento della posizione e della durata di ciascuna operazione.
 */
class EditMapTimeline extends TimelineCanvas {
    constructor(host, canvas, dotNetReference, model) {
        super(host, canvas, dotNetReference, model, { navigatorAudioKey: 'source' });
        this.pendingDrag = null;
        this.dragNotifyTimer = null;
        this.dragNotifyPromise = Promise.resolve();
        this.dragNotifyInFlight = false;
        this.start();
    }

    audioUrls(model) {
        const sourceFill = model.sourceFillAudioUrl === model.sourceAudioUrl ? null : model.sourceFillAudioUrl;
        return { source: model.sourceAudioUrl, language: model.languageAudioUrl, sourceFill };
    }

    drawContent(ctx, layout, colors) {
        const { width, height, plotLeft, trackTop, startMs, endMs } = layout;
        const laneGap = 4;
        const operationHeight = 18;
        const laneHeight = Math.max(20, Math.floor((height - trackTop - laneGap * 3 - operationHeight) / 2));
        const sourceTop = trackTop + laneGap;
        const languageTop = sourceTop + laneHeight + laneGap;
        const trackBottom = languageTop + laneHeight;
        const operationY = Math.min(height - 11, trackBottom + laneGap + 6);
        this.contentLayout = { trackTop, sourceTop, languageTop, laneHeight, trackBottom, operationY, height };
        ctx.fillStyle = colors.background;
        ctx.fillRect(plotLeft, sourceTop, width - plotLeft, laneHeight);
        ctx.fillRect(plotLeft, languageTop, width - plotLeft, laneHeight);
        drawTimeGrid(ctx, width, plotLeft, startMs, endMs, this.pixelsPerMs, trackTop, trackBottom, colors.border);
        const spectrogram = this.model.audioMode === 'spectrogram';
        if (spectrogram) {
            drawFrequencyScale(ctx, plotLeft, sourceTop, laneHeight, colors.secondary, colors.fontFamily, this.model.sourceNyquistHz);
            drawFrequencyScale(ctx, plotLeft, languageTop, laneHeight, colors.secondary, colors.fontFamily, this.model.languageNyquistHz);
        } else {
            const labelFontSize = this.model.precisionMode ? 11 : 9;
            drawAmplitudeGrid(ctx, plotLeft, width, sourceTop, laneHeight, colors.border, colors.secondary, colors.fontFamily, labelFontSize);
            drawAmplitudeGrid(ctx, plotLeft, width, languageTop, laneHeight, colors.border, colors.secondary, colors.fontFamily, labelFontSize);
        }
        ctx.strokeStyle = colors.border;
        ctx.strokeRect(plotLeft + 0.5, sourceTop + 0.5, width - plotLeft - 1, laneHeight - 1);
        ctx.strokeRect(plotLeft + 0.5, languageTop + 0.5, width - plotLeft - 1, laneHeight - 1);
        ctx.fillStyle = colors.secondary;
        ctx.fillText(this.model.labels?.source || 'SOURCE', 7, sourceTop + laneHeight / 2);
        ctx.fillText(this.model.labels?.language || 'LANGUAGE', 7, languageTop + laneHeight / 2);
        this.drawAudioImage(this.audioImages.get('source'), startMs, endMs, plotLeft, width, sourceTop, laneHeight, colors.info, !spectrogram);
        for (const segment of this.model.segments || []) {
            const x1 = this.xAtTime(segment.sourceStartMs);
            const x2 = this.xAtTime(segment.sourceEndMs);
            if (x2 < plotLeft || x1 > width) continue;
            if (segment.kind === 'InsertedGap') {
                ctx.save();
                const visibleStart = Math.max(startMs, segment.sourceStartMs);
                const visibleEnd = Math.min(endMs, segment.sourceEndMs);
                const visibleX1 = Math.max(plotLeft, this.xAtTime(visibleStart));
                const visibleX2 = Math.min(width, this.xAtTime(visibleEnd));
                if (visibleEnd > visibleStart) {
                    if (segment.sourceFilled) {
                        const ratioStart = (visibleStart - segment.sourceStartMs) / Math.max(1, segment.sourceEndMs - segment.sourceStartMs);
                        const ratioEnd = (visibleEnd - segment.sourceStartMs) / Math.max(1, segment.sourceEndMs - segment.sourceStartMs);
                        const fillStart = segment.fillSourceStartMs + (segment.fillSourceEndMs - segment.fillSourceStartMs) * ratioStart;
                        const fillEnd = segment.fillSourceStartMs + (segment.fillSourceEndMs - segment.fillSourceStartMs) * ratioEnd;
                        const fillImage = this.model.sourceFillAudioUrl === this.model.sourceAudioUrl
                            ? this.audioImages.get('source')
                            : this.audioImages.get('sourceFill');
                        const insertGain = Math.pow(10, Number(segment.gainDb || 0) / 20);
                        this.drawAudioImage(fillImage, fillStart, fillEnd, visibleX1, visibleX2, languageTop, laneHeight, colors.info, !spectrogram, this.waveformGain * insertGain);
                        ctx.globalAlpha = 0.12;
                        ctx.fillStyle = colors.info;
                        ctx.fillRect(visibleX1, languageTop, Math.max(2, visibleX2 - visibleX1), laneHeight);
                    } else {
                        ctx.globalAlpha = 0.22;
                        ctx.fillStyle = colors.warning;
                        ctx.fillRect(visibleX1, languageTop, Math.max(2, visibleX2 - visibleX1), laneHeight);
                        if (!spectrogram) {
                            ctx.globalAlpha = 0.65;
                            ctx.strokeStyle = colors.warning;
                            ctx.beginPath();
                            ctx.moveTo(visibleX1, languageTop + laneHeight / 2);
                            ctx.lineTo(visibleX2, languageTop + laneHeight / 2);
                            ctx.stroke();
                        }
                    }
                }
                ctx.restore();
            } else if (segment.kind === 'Mapped') {
                const visibleStart = Math.max(startMs, segment.sourceStartMs);
                const visibleEnd = Math.min(endMs, segment.sourceEndMs);
                if (visibleEnd > visibleStart) {
                    const ratioStart = (visibleStart - segment.sourceStartMs) / Math.max(1, segment.sourceEndMs - segment.sourceStartMs);
                    const ratioEnd = (visibleEnd - segment.sourceStartMs) / Math.max(1, segment.sourceEndMs - segment.sourceStartMs);
                    const languageStart = segment.languageStartMs + (segment.languageEndMs - segment.languageStartMs) * ratioStart;
                    const languageEnd = segment.languageStartMs + (segment.languageEndMs - segment.languageStartMs) * ratioEnd;
                    this.drawAudioImage(this.audioImages.get('language'), languageStart, languageEnd, this.xAtTime(visibleStart), this.xAtTime(visibleEnd), languageTop, laneHeight, colors.success, !spectrogram);
                }
            }
        }
        if (!this.audioImages.get('language')) {
            ctx.strokeStyle = colors.secondary;
            ctx.beginPath();
            ctx.moveTo(plotLeft, languageTop + laneHeight / 2);
            ctx.lineTo(width, languageTop + laneHeight / 2);
            ctx.stroke();
        }
        this.drawSelection(ctx, plotLeft, width, sourceTop, languageTop, laneHeight, colors);
        for (const operation of this.model.operations || []) {
            const temporary = this.drag && this.drag.kind === 'operation' && this.drag.operationIndex === operation.index ? this.drag.timeMs : operation.sourceMs;
            const temporaryDuration = this.drag && this.drag.kind === 'duration' && this.drag.operationIndex === operation.index ? this.drag.durationMs : Math.abs(operation.durationMs);
            const x = this.xAtTime(temporary);
            if (x < plotLeft - 80 || x > width + 80) continue;
            ctx.strokeStyle = operation.selected ? colors.warning : colors.danger;
            ctx.lineWidth = operation.selected ? 3 : 2;
            ctx.beginPath(); ctx.moveTo(x, trackTop); ctx.lineTo(x, height); ctx.stroke();
            ctx.fillStyle = operation.selected ? colors.warning : colors.danger;
            const insert = operation.type === 'INSERT_SILENCE';
            const durationEndX = insert ? x + Math.max(8, temporaryDuration * this.pixelsPerMs) : x + (this.model.precisionMode ? 14 : 10);
            const barHeight = this.model.precisionMode && operation.selected ? 6 : 4;
            const handleWidth = this.model.precisionMode && operation.selected ? 8 : 6;
            const handleHeight = this.model.precisionMode && operation.selected ? 18 : 12;
            ctx.fillRect(x, operationY - barHeight / 2, insert ? Math.max(2, durationEndX - x) : 2, barHeight);
            ctx.fillRect(durationEndX - handleWidth / 2, operationY - handleHeight / 2, handleWidth, handleHeight);
            if (this.model.precisionMode && operation.selected && operation.type === 'INSERT_SILENCE') {
                ctx.globalAlpha = 0.9;
                ctx.fillRect(x - 2, languageTop, 4, laneHeight);
                ctx.fillRect(durationEndX - 2, languageTop, 4, laneHeight);
                ctx.globalAlpha = 1;
            }
            const label = operation.type === 'CUT_SEGMENT' ? `${this.model.labels?.cut || 'CUT'} −${Math.round(temporaryDuration)} ms` : `${this.model.labels?.insert || 'INSERT'} +${Math.round(temporaryDuration)} ms`;
            if (operation.selected) {
                const labelWidth = ctx.measureText(label).width;
                const labelX = Math.max(plotLeft + 3, Math.min(width - labelWidth - 11, x + 5));
                ctx.fillStyle = colors.warning;
                ctx.fillRect(labelX - 4, operationY - 9, labelWidth + 8, 18);
                ctx.fillStyle = colors.onWarning;
                ctx.fillText(label, labelX, operationY);
            } else {
                const compactLabel = operation.type === 'CUT_SEGMENT' ? 'C' : 'I';
                const compactX = Math.max(plotLeft + 2, Math.min(width - 17, x + 3));
                ctx.fillStyle = colors.danger;
                ctx.fillRect(compactX, operationY - 8, 14, 16);
                ctx.fillStyle = colors.onDanger;
                ctx.fillText(compactLabel, compactX + 4, operationY);
            }
        }
    }

    drawSelection(ctx, plotLeft, width, sourceTop, languageTop, laneHeight, colors) {
        const dragging = this.drag && (this.drag.kind === 'selection' || this.drag.kind === 'selection-move');
        const startMs = dragging ? Math.min(this.drag.startMs, this.drag.endMs) : Number(this.model.selectionStartMs);
        const endMs = dragging ? Math.max(this.drag.startMs, this.drag.endMs) : Number(this.model.selectionEndMs);
        if (!Number.isFinite(startMs) || !Number.isFinite(endMs) || endMs <= startMs) return;
        const x1 = Math.max(plotLeft, this.xAtTime(startMs));
        const x2 = Math.min(width, this.xAtTime(endMs));
        if (x2 <= x1) return;
        ctx.save();
        ctx.globalAlpha = 0.12;
        ctx.fillStyle = colors.primary;
        ctx.fillRect(x1, sourceTop, x2 - x1, laneHeight);
        ctx.globalAlpha = 0.28;
        ctx.fillRect(x1, languageTop, x2 - x1, laneHeight);
        ctx.globalAlpha = 0.95;
        ctx.strokeStyle = colors.primary;
        ctx.lineWidth = this.model.precisionMode ? 2 : 1;
        ctx.beginPath();
        ctx.moveTo(x1, sourceTop);
        ctx.lineTo(x1, languageTop + laneHeight);
        ctx.moveTo(x2, sourceTop);
        ctx.lineTo(x2, languageTop + laneHeight);
        ctx.stroke();
        ctx.restore();
    }

    hitContent(x, y, event) {
        let result = null;
        let distance = this.model.precisionMode ? 12 : 9;
        const layout = this.contentLayout;
        for (const operation of this.model.operations || []) {
            const operationX = this.xAtTime(operation.sourceMs);
            const insert = operation.type === 'INSERT_SILENCE';
            const durationHandleX = insert ? operationX + Math.max(8, Math.abs(operation.durationMs) * this.pixelsPerMs) : operationX + (this.model.precisionMode ? 14 : 10);
            if (Math.abs(durationHandleX - x) <= (this.model.precisionMode ? 11 : 7)) {
                result = { operation, kind: 'duration' };
                distance = -1;
                break;
            }
            const currentDistance = Math.abs(this.xAtTime(operation.sourceMs) - x);
            if (currentDistance <= distance) {
                result = { operation, kind: 'operation' };
                distance = currentDistance;
            }
            if (!result && this.model.precisionMode && operation.type === 'INSERT_SILENCE' && layout && y >= layout.languageTop && y <= layout.languageTop + layout.laneHeight) {
                if (x > operationX && x < durationHandleX)
                    result = { operation, kind: 'operation', offsetMs: this.timeAtX(x) - operation.sourceMs };
            }
        }
        if (result) {
            const operation = result.operation;
            const drag = result.kind === 'duration'
                ? { kind: 'duration', operationIndex: operation.index, sourceMs: operation.sourceMs, durationMs: Math.abs(operation.durationMs), initialDurationMs: Math.abs(operation.durationMs), startX: x, moved: false, pointerId: event.pointerId }
                : { kind: 'operation', operationIndex: operation.index, timeMs: operation.sourceMs, offsetMs: result.offsetMs || 0, startX: x, moved: false, pointerId: event.pointerId };
            drag.selectionPromise = this.dotNetReference.invokeMethodAsync('OnTimelineOperationSelected', operation.index);
            return drag;
        }
        if (!this.model.selectionEnabled || !layout || y < layout.languageTop || y > layout.languageTop + layout.laneHeight) return null;
        const timeMs = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(x)));
        const selectionStartMs = Number(this.model.selectionStartMs);
        const selectionEndMs = Number(this.model.selectionEndMs);
        if (Number.isFinite(selectionStartMs) && Number.isFinite(selectionEndMs) && selectionEndMs > selectionStartMs && timeMs >= selectionStartMs && timeMs <= selectionEndMs) {
            const selectionSegment = (this.model.segments || []).find(candidate => candidate.kind === 'Mapped' && selectionStartMs >= candidate.sourceStartMs && selectionEndMs <= candidate.sourceEndMs);
            if (selectionSegment) {
                return {
                    kind: 'selection-move',
                    startMs: selectionStartMs,
                    endMs: selectionEndMs,
                    durationMs: selectionEndMs - selectionStartMs,
                    offsetMs: timeMs - selectionStartMs,
                    segmentStartMs: selectionSegment.sourceStartMs,
                    segmentEndMs: selectionSegment.sourceEndMs,
                    pointerId: event.pointerId
                };
            }
        }
        const segment = (this.model.segments || []).find(candidate => candidate.kind === 'Mapped' && timeMs >= candidate.sourceStartMs && timeMs <= candidate.sourceEndMs);
        if (!segment) return null;
        return { kind: 'selection', startMs: timeMs, endMs: timeMs, segmentStartMs: segment.sourceStartMs, segmentEndMs: segment.sourceEndMs, pointerId: event.pointerId };
    }

    onContentDragMove(drag, x) {
        if (drag.kind === 'operation') {
            if (!drag.moved && Math.abs(x - drag.startX) < 3) return;
            drag.moved = true;
            const raw = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(x) - drag.offsetMs));
            const snapped = Math.round(raw / this.model.frameDurationMs) * this.model.frameDurationMs;
            drag.timeMs = this.clampOperationTime(snapped, drag.operationIndex);
            this.pendingDrag = { index: drag.operationIndex, timeMs: drag.timeMs };
            this.scheduleDragNotification('OnTimelineOperationDrag', 'timeMs');
            this.canvas.style.cursor = 'ew-resize';
        } else if (drag.kind === 'duration') {
            if (!drag.moved && Math.abs(x - drag.startX) < 3) return;
            drag.moved = true;
            const rawDuration = Math.max(this.model.frameDurationMs, drag.initialDurationMs + (x - drag.startX) / this.pixelsPerMs);
            drag.durationMs = Math.round(rawDuration / this.model.frameDurationMs) * this.model.frameDurationMs;
            this.pendingDrag = { index: drag.operationIndex, durationMs: drag.durationMs };
            this.scheduleDragNotification('OnTimelineDurationDrag', 'durationMs');
            this.canvas.style.cursor = 'ew-resize';
        } else if (drag.kind === 'selection') {
            const raw = Math.max(drag.segmentStartMs, Math.min(drag.segmentEndMs, this.timeAtX(x)));
            drag.endMs = Math.round(raw / this.model.frameDurationMs) * this.model.frameDurationMs;
            drag.endMs = Math.max(drag.segmentStartMs, Math.min(drag.segmentEndMs, drag.endMs));
            this.canvas.style.cursor = 'crosshair';
        } else if (drag.kind === 'selection-move') {
            const maximumStartMs = Math.max(drag.segmentStartMs, drag.segmentEndMs - drag.durationMs);
            const rawStartMs = Math.max(drag.segmentStartMs, Math.min(maximumStartMs, this.timeAtX(x) - drag.offsetMs));
            drag.startMs = Math.round(rawStartMs / this.model.frameDurationMs) * this.model.frameDurationMs;
            drag.startMs = Math.max(drag.segmentStartMs, Math.min(maximumStartMs, drag.startMs));
            drag.endMs = drag.startMs + drag.durationMs;
            this.canvas.style.cursor = 'grabbing';
        }
        this.draw();
    }

    scheduleDragNotification(method, field) {
        if (this.dragNotifyTimer || this.dragNotifyInFlight) return;
        this.dragNotifyTimer = setTimeout(() => this.flushDragNotification(method, field), 75);
    }

    flushDragNotification(method, field) {
        this.dragNotifyTimer = null;
        if (this.dragNotifyInFlight || !this.pendingDrag) return;
        const pending = this.pendingDrag;
        const selectionPromise = this.drag?.selectionPromise;
        this.pendingDrag = null;
        this.dragNotifyInFlight = true;
        this.dragNotifyPromise = Promise.resolve(selectionPromise)
            .then(() => this.dotNetReference.invokeMethodAsync(method, pending.index, pending[field], false))
            .finally(() => {
                this.dragNotifyInFlight = false;
                if (this.pendingDrag) this.scheduleDragNotification(method, field);
            });
    }

    onContentDragEnd(drag) {
        if (this.dragNotifyTimer) clearTimeout(this.dragNotifyTimer);
        this.dragNotifyTimer = null;
        this.pendingDrag = null;
        if (drag.kind === 'operation' && drag.moved) Promise.resolve(drag.selectionPromise).then(() => this.dragNotifyPromise).then(() => this.dotNetReference.invokeMethodAsync('OnTimelineOperationDrag', drag.operationIndex, drag.timeMs, true));
        else if (drag.kind === 'duration' && drag.moved) Promise.resolve(drag.selectionPromise).then(() => this.dragNotifyPromise).then(() => this.dotNetReference.invokeMethodAsync('OnTimelineDurationDrag', drag.operationIndex, drag.durationMs, true));
        else if (drag.kind === 'selection' || drag.kind === 'selection-move') this.dotNetReference.invokeMethodAsync('OnTimelineSelectionChanged', Math.min(drag.startMs, drag.endMs), Math.max(drag.startMs, drag.endMs));
    }

    contentCursor(x, y) {
        const layout = this.contentLayout;
        if (!layout) return 'default';
        for (const operation of this.model.operations || []) {
            const startX = this.xAtTime(operation.sourceMs);
            const endX = operation.type === 'INSERT_SILENCE' ? startX + Math.max(8, Math.abs(operation.durationMs) * this.pixelsPerMs) : startX + (this.model.precisionMode ? 14 : 10);
            if (Math.abs(endX - x) <= (this.model.precisionMode ? 11 : 7) || Math.abs(startX - x) <= (this.model.precisionMode ? 12 : 9)) return 'ew-resize';
            if (this.model.precisionMode && operation.type === 'INSERT_SILENCE' && y >= layout.languageTop && y <= layout.languageTop + layout.laneHeight && x > startX && x < endX) return 'move';
        }
        const timeMs = this.timeAtX(x);
        const selectionStartMs = Number(this.model.selectionStartMs);
        const selectionEndMs = Number(this.model.selectionEndMs);
        if (this.model.selectionEnabled && y >= layout.languageTop && y <= layout.languageTop + layout.laneHeight && Number.isFinite(selectionStartMs) && Number.isFinite(selectionEndMs) && timeMs >= selectionStartMs && timeMs <= selectionEndMs) return 'grab';
        if (this.model.selectionEnabled && y >= layout.languageTop && y <= layout.languageTop + layout.laneHeight) return 'crosshair';
        return 'default';
    }

    clampOperationTime(timeMs, operationIndex) {
        let result = timeMs;
        const mapped = (this.model.segments || []).filter(segment => segment.kind === 'Mapped');
        let inside = false;
        let nearest = result;
        let nearestDistance = Number.POSITIVE_INFINITY;
        for (const segment of mapped) {
            if (result >= segment.sourceStartMs && result <= segment.sourceEndMs) inside = true;
            for (const boundary of [segment.sourceStartMs, segment.sourceEndMs]) {
                const distance = Math.abs(boundary - result);
                if (distance < nearestDistance) { nearest = boundary; nearestDistance = distance; }
            }
        }
        if (!inside && mapped.length > 0) result = nearest;
        const operations = this.model.operations || [];
        const position = operations.findIndex(operation => operation.index === operationIndex);
        const minimum = position > 0 ? operations[position - 1].sourceMs + this.model.frameDurationMs : 0;
        const maximum = position >= 0 && position + 1 < operations.length ? operations[position + 1].sourceMs - this.model.frameDurationMs : this.model.durationMs;
        result = Math.max(minimum, Math.min(maximum, result));
        return Math.max(0, Math.min(this.model.durationMs, result));
    }

    dispose() {
        if (this.dragNotifyTimer) clearTimeout(this.dragNotifyTimer);
        this.dragNotifyTimer = null;
        this.pendingDrag = null;
        super.dispose();
    }
}

export function createTimeline(host, canvas, dotNetReference, model) {
    return new EditMapTimeline(host, canvas, dotNetReference, model);
}
