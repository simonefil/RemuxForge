import {
    TimelineCanvas,
    formatTimelineTime,
    drawTimeGrid,
    drawAmplitudeGrid,
    drawFrequencyScale
} from './media-timeline.js';

export { createPreviewPair, createFramePreview, captureEditorKeyboard } from './media-timeline.js';

/**
 * Timeline dell'editor EditMap: due corsie audio (sorgente e lingua), i segmenti della mappa
 * e le operazioni, con trascinamento della posizione e della durata di ciascuna operazione.
 */
class EditMapTimeline extends TimelineCanvas {
    constructor(host, canvas, dotNetReference, model) {
        super(host, canvas, dotNetReference, model, { navigatorAudioKey: 'source' });
        this.pendingDrag = null;
        this.hoverOperationIndex = -1;
        this.flagRects = [];
        this.hatchPatterns = new Map();
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
        const laneHeight = Math.max(20, Math.floor((height - trackTop - laneGap * 3) / 2));
        const sourceTop = trackTop + laneGap;
        const languageTop = sourceTop + laneHeight + laneGap;
        const trackBottom = languageTop + laneHeight;
        this.contentLayout = { rulerTop: trackTop - this.rulerHeight, trackTop, sourceTop, languageTop, laneHeight, trackBottom, height };
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
                        ctx.globalAlpha = 0.1;
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
        this.drawOperations(ctx, layout, colors);
    }

    /**
     * Operazioni come in un editor audio: l'INSERT è una regione a righe sulla corsia Language,
     * il CUT una giunta che strappa la corsia Language; ognuna ha una bandierina nel righello.
     * Il colore dice il tipo, l'evidenza cresce con passaggio del mouse e selezione.
     */
    drawOperations(ctx, layout, colors) {
        const { width, plotLeft } = layout;
        const { rulerTop, trackTop, sourceTop, languageTop, laneHeight, trackBottom } = this.contentLayout;
        const precision = this.model.precisionMode;
        const operations = this.model.operations || [];
        const emphasis = operation => {
            if (this.drag && this.drag.operationIndex === operation.index) return 3;
            if (operation.selected) return 2;
            return this.hoverX !== null && this.hoverOperationIndex === operation.index ? 1 : 0;
        };
        // Le operazioni in evidenza si disegnano per ultime, sopra alle vicine
        const ordered = operations.slice().sort((left, right) => emphasis(left) - emphasis(right));
        this.flagRects = [];
        for (const operation of ordered) {
            const dragged = this.drag && this.drag.operationIndex === operation.index ? this.drag : null;
            const moving = dragged && dragged.kind === 'operation' && dragged.moved;
            const resizing = dragged && dragged.kind === 'duration' && dragged.moved;
            const timeMs = moving ? dragged.timeMs : operation.sourceMs;
            const durationMs = resizing ? dragged.durationMs : Math.abs(operation.durationMs);
            const insert = operation.type === 'INSERT_SILENCE';
            const x = Math.round(this.xAtTime(timeMs)) + 0.5;
            const endX = this.durationHandleX(operation, x, durationMs);
            if (endX < plotLeft - 160 || x > width + 20) continue;
            const level = emphasis(operation);
            const active = level > 0;
            const color = insert ? colors.warning : colors.danger;
            const onColor = insert ? colors.onWarning : colors.onDanger;

            // Sagoma della posizione di partenza mentre si trascina
            if (moving || resizing) {
                const ghostX = moving ? Math.round(this.xAtTime(operation.sourceMs)) + 0.5 : this.durationHandleX(operation, x, Math.abs(operation.durationMs));
                ctx.save();
                ctx.globalAlpha = 0.45;
                ctx.strokeStyle = color;
                ctx.setLineDash([3, 3]);
                ctx.lineWidth = 1;
                ctx.beginPath(); ctx.moveTo(ghostX, trackTop); ctx.lineTo(ghostX, trackBottom); ctx.stroke();
                ctx.restore();
            }

            ctx.save();
            if (level >= 2) { ctx.shadowColor = color; ctx.shadowBlur = 8; }
            ctx.globalAlpha = active ? 1 : 0.75;
            ctx.strokeStyle = color;
            ctx.fillStyle = color;
            ctx.lineWidth = level >= 2 ? 2 : (level === 1 ? 1.5 : 1);
            if (insert) {
                const regionWidth = Math.max(1, endX - x);
                ctx.save();
                ctx.shadowBlur = 0;
                ctx.globalAlpha = active ? 0.14 : 0.08;
                ctx.fillRect(x, sourceTop, regionWidth, laneHeight);
                ctx.globalAlpha = active ? 0.5 : 0.32;
                ctx.fillStyle = this.hatchPattern(color);
                ctx.fillRect(x, languageTop, regionWidth, laneHeight);
                ctx.restore();
                ctx.beginPath();
                ctx.moveTo(x, trackTop); ctx.lineTo(x, trackBottom);
                ctx.moveTo(endX, sourceTop); ctx.lineTo(endX, trackBottom);
                ctx.stroke();
            } else {
                // La giunta: dritta sulla Source, a zig-zag dove la Language salta avanti
                const tooth = precision ? 4 : 3;
                const step = precision ? 7 : 6;
                ctx.beginPath();
                ctx.moveTo(x, trackTop);
                ctx.lineTo(x, languageTop);
                let side = 1;
                for (let y = languageTop + step / 2; y < trackBottom; y += step) {
                    ctx.lineTo(x + side * tooth, y);
                    side = -side;
                }
                ctx.lineTo(x, trackBottom);
                ctx.stroke();
            }
            ctx.restore();

            // Maniglia della durata: il bordo destro dell'INSERT, a fianco della giunta del CUT
            if (active) {
                const gripWidth = precision ? 8 : 6;
                const gripHeight = Math.max(14, Math.min(precision ? 36 : 26, laneHeight * 0.45));
                const gripY = languageTop + laneHeight / 2;
                ctx.save();
                ctx.fillStyle = color;
                ctx.strokeStyle = color;
                if (!insert) {
                    ctx.globalAlpha = 0.8;
                    ctx.setLineDash([2, 3]);
                    ctx.lineWidth = 1;
                    ctx.beginPath(); ctx.moveTo(x + 4, gripY + 0.5); ctx.lineTo(endX - gripWidth / 2, gripY + 0.5); ctx.stroke();
                    ctx.setLineDash([]);
                    ctx.globalAlpha = 1;
                }
                ctx.beginPath();
                ctx.roundRect(endX - gripWidth / 2, gripY - gripHeight / 2, gripWidth, gripHeight, gripWidth / 2);
                ctx.fill();
                ctx.strokeStyle = onColor;
                ctx.globalAlpha = 0.7;
                ctx.lineWidth = 1;
                ctx.beginPath();
                ctx.moveTo(endX - 1, gripY - 3); ctx.lineTo(endX - 1, gripY + 3);
                ctx.moveTo(endX + 1, gripY - 3); ctx.lineTo(endX + 1, gripY + 3);
                ctx.stroke();
                ctx.restore();
            }

            this.drawOperationFlag(ctx, operation, x, level, rulerTop, trackTop, width, color, onColor, moving ? formatTimelineTime(timeMs) : `${insert ? '+' : '−'}${Math.round(durationMs)} ms`);
        }
    }

    /** Bandierina nel righello: a riposo solo l'icona, in evidenza anche la durata. */
    drawOperationFlag(ctx, operation, x, level, rulerTop, trackTop, width, color, onColor, text) {
        const insert = operation.type === 'INSERT_SILENCE';
        const flagTop = rulerTop + 3;
        const flagHeight = trackTop - flagTop - 3;
        const iconSize = 10;
        const padding = 5;
        const showText = level > 0;
        const textWidth = showText ? ctx.measureText(text).width + 5 : 0;
        const flagWidth = padding * 2 + iconSize + textWidth;
        const flagX = Math.min(x, width - flagWidth - 1);
        ctx.save();
        if (level >= 2) { ctx.shadowColor = color; ctx.shadowBlur = 8; }
        ctx.globalAlpha = level > 0 ? 1 : 0.85;
        ctx.fillStyle = color;
        ctx.strokeStyle = color;
        ctx.beginPath();
        ctx.roundRect(flagX, flagTop, flagWidth, flagHeight, flagX === x ? [4, 4, 4, 0] : 4);
        ctx.fill();
        ctx.lineWidth = 1;
        ctx.beginPath(); ctx.moveTo(x, flagTop + flagHeight); ctx.lineTo(x, trackTop); ctx.stroke();
        ctx.shadowBlur = 0;
        const iconX = flagX + padding + iconSize / 2;
        const iconY = flagTop + flagHeight / 2;
        ctx.strokeStyle = onColor;
        ctx.lineCap = 'round';
        if (insert) {
            ctx.lineWidth = 1.8;
            ctx.beginPath();
            ctx.moveTo(iconX - 4, iconY); ctx.lineTo(iconX + 4, iconY);
            ctx.moveTo(iconX, iconY - 4); ctx.lineTo(iconX, iconY + 4);
            ctx.stroke();
        } else {
            // Forbici: due anelli e due lame incrociate
            ctx.lineWidth = 1.2;
            ctx.beginPath();
            ctx.arc(iconX - 2.6, iconY + 2.8, 1.9, 0, Math.PI * 2);
            ctx.moveTo(iconX + 2.6 + 1.9, iconY + 2.8);
            ctx.arc(iconX + 2.6, iconY + 2.8, 1.9, 0, Math.PI * 2);
            ctx.moveTo(iconX - 1.6, iconY + 1.2); ctx.lineTo(iconX + 3.2, iconY - 5);
            ctx.moveTo(iconX + 1.6, iconY + 1.2); ctx.lineTo(iconX - 3.2, iconY - 5);
            ctx.stroke();
        }
        if (showText) {
            ctx.fillStyle = onColor;
            ctx.fillText(text, flagX + padding + iconSize + 5, iconY + 0.5);
        }
        ctx.restore();
        this.flagRects.push({ index: operation.index, left: flagX, right: flagX + flagWidth });
    }

    /** Motivo a righe diagonali della regione INSERT, ancorato al contenuto che scorre. */
    hatchPattern(color) {
        let pattern = this.hatchPatterns.get(color);
        if (!pattern) {
            const tile = document.createElement('canvas');
            tile.width = 8;
            tile.height = 8;
            const tileContext = tile.getContext('2d');
            tileContext.strokeStyle = color;
            tileContext.lineWidth = 1.5;
            tileContext.beginPath();
            tileContext.moveTo(-1, 9); tileContext.lineTo(9, -1);
            tileContext.moveTo(-1, 1); tileContext.lineTo(1, -1);
            tileContext.moveTo(7, 9); tileContext.lineTo(9, 7);
            tileContext.stroke();
            pattern = this.context.createPattern(tile, 'repeat');
            this.hatchPatterns.set(color, pattern);
        }
        pattern.setTransform(new DOMMatrix([1, 0, 0, 1, -(this.host.scrollLeft % 8), 0]));
        return pattern;
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
        ctx.globalAlpha = 0.22;
        ctx.fillStyle = colors.primary;
        ctx.fillRect(x1, sourceTop, x2 - x1, laneHeight);
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

    /**
     * Ascissa della maniglia di durata. L'INSERT è largo quanto la sua durata; il CUT nel tempo
     * Source non occupa spazio, e la sua maniglia sta a fianco della giunta alla stessa scala.
     */
    durationHandleX(operation, startX, durationMs) {
        const minimum = operation.type === 'INSERT_SILENCE' ? 8 : 14;
        return startX + Math.max(minimum, durationMs * this.pixelsPerMs);
    }

    /** Operazione sotto il puntatore e parte colpita: bandierina, maniglia della durata, linea o corpo. */
    hitOperation(x, y) {
        const layout = this.contentLayout;
        if (!layout || y < layout.rulerTop) return null;
        const operations = this.model.operations || [];
        if (y < layout.trackTop) {
            // Le bandierine disegnate per ultime stanno sopra: si cercano a ritroso
            for (let i = this.flagRects.length - 1; i >= 0; i--) {
                const flag = this.flagRects[i];
                const operation = operations.find(candidate => candidate.index === flag.index);
                if (operation && x >= flag.left - 2 && x <= flag.right + 2) return { operation, part: 'flag' };
            }
            return null;
        }
        if (y > layout.trackBottom) return null;
        const precision = this.model.precisionMode;
        const handleTolerance = precision ? 8 : 6;
        let result = null;
        let distance = precision ? 8 : 6;
        for (const operation of operations) {
            const insert = operation.type === 'INSERT_SILENCE';
            const startX = this.xAtTime(operation.sourceMs);
            const endX = this.durationHandleX(operation, startX, Math.abs(operation.durationMs));
            // La maniglia del CUT esiste solo quando si vede
            const handleVisible = insert || operation.selected || operation.index === this.hoverOperationIndex;
            if (handleVisible && Math.abs(endX - x) <= handleTolerance && (insert || y >= layout.languageTop))
                return { operation, part: 'duration' };
            const currentDistance = Math.abs(startX - x);
            if (currentDistance <= distance) {
                result = { operation, part: 'line' };
                distance = currentDistance;
            }
            if (result || x <= startX || x >= endX) continue;
            const inLanguage = y >= layout.languageTop;
            const onLeader = !insert && handleVisible && Math.abs(y - (layout.languageTop + layout.laneHeight / 2)) <= 8;
            if ((insert && inLanguage) || onLeader) result = { operation, part: 'body' };
        }
        return result;
    }

    /** Selezione corrente del modello, null quando non c'è. */
    selectionRange() {
        if (this.model.selectionStartMs === null || this.model.selectionStartMs === undefined || this.model.selectionEndMs === null || this.model.selectionEndMs === undefined) return null;
        const startMs = Number(this.model.selectionStartMs);
        const endMs = Number(this.model.selectionEndMs);
        if (!Number.isFinite(startMs) || !Number.isFinite(endMs) || endMs <= startMs) return null;
        return { startMs, endMs };
    }

    /** Segmento mappato che contiene un istante Source: la selezione non ne esce. */
    mappedSegmentAt(timeMs) {
        return (this.model.segments || []).find(candidate => candidate.kind === 'Mapped' && timeMs >= candidate.sourceStartMs && timeMs <= candidate.sourceEndMs) || null;
    }

    /** Aggancia un istante al fotogramma dentro i limiti del segmento del drag. */
    snapSelectionTime(timeMs, drag) {
        const snapped = Math.round(timeMs / this.model.frameDurationMs) * this.model.frameDurationMs;
        return Math.max(drag.segmentStartMs, Math.min(drag.segmentEndMs, snapped));
    }

    hitContent(x, y, event) {
        const layout = this.contentLayout;
        const tool = this.model.tool || 'select';
        if (!layout || y < layout.rulerTop || (y < layout.trackTop && tool !== 'operations')) return null;
        if (tool === 'hand')
            return { kind: 'hand', startX: x, startScroll: this.host.scrollLeft, pointerId: event.pointerId };

        if (tool === 'operations') {
            // Un clic a vuoto torna alla base, che sposta il cursore
            const hit = this.hitOperation(x, y);
            if (!hit) return null;
            const operation = hit.operation;
            const drag = hit.part === 'duration'
                ? { kind: 'duration', operationIndex: operation.index, sourceMs: operation.sourceMs, durationMs: Math.abs(operation.durationMs), initialDurationMs: Math.abs(operation.durationMs), startX: x, moved: false, pointerId: event.pointerId }
                : { kind: 'operation', operationIndex: operation.index, timeMs: operation.sourceMs, offsetMs: hit.part === 'line' ? 0 : this.timeAtX(x) - operation.sourceMs, startX: x, moved: false, pointerId: event.pointerId };
            drag.selectionPromise = this.dotNetReference.invokeMethodAsync('OnTimelineOperationSelected', operation.index);
            return drag;
        }

        // Strumento Selezione: bordi per ridimensionare, Maiusc per estendere, dentro per spostare
        const timeMs = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(x)));
        const selection = this.selectionRange();
        const selectionSegment = selection ? this.mappedSegmentAt((selection.startMs + selection.endMs) / 2) : null;
        if (selectionSegment) {
            const edgeTolerance = this.model.precisionMode ? 7 : 5;
            const nearStart = Math.abs(this.xAtTime(selection.startMs) - x) <= edgeTolerance;
            const nearEnd = Math.abs(this.xAtTime(selection.endMs) - x) <= edgeTolerance;
            if (nearStart || nearEnd || event.shiftKey) {
                let anchorMs = selection.startMs;
                if (nearStart) anchorMs = selection.endMs;
                else if (!nearEnd && Math.abs(timeMs - selection.startMs) < Math.abs(timeMs - selection.endMs)) anchorMs = selection.endMs;
                const drag = { kind: 'selection', startMs: anchorMs, endMs: anchorMs === selection.startMs ? selection.endMs : selection.startMs, segmentStartMs: selectionSegment.sourceStartMs, segmentEndMs: selectionSegment.sourceEndMs, pointerId: event.pointerId };
                if (event.shiftKey && !nearStart && !nearEnd) drag.endMs = this.snapSelectionTime(timeMs, drag);
                return drag;
            }
            if (timeMs >= selection.startMs && timeMs <= selection.endMs) {
                return {
                    kind: 'selection-press',
                    inside: true,
                    timeMs,
                    startX: x,
                    startMs: selection.startMs,
                    endMs: selection.endMs,
                    durationMs: selection.endMs - selection.startMs,
                    offsetMs: timeMs - selection.startMs,
                    segmentStartMs: selectionSegment.sourceStartMs,
                    segmentEndMs: selectionSegment.sourceEndMs,
                    pointerId: event.pointerId
                };
            }
        }
        const segment = this.mappedSegmentAt(timeMs);
        if (segment && event.shiftKey && !selection && this.mappedSegmentAt(this.model.playheadMs) === segment) {
            const drag = { kind: 'selection', startMs: 0, endMs: 0, segmentStartMs: segment.sourceStartMs, segmentEndMs: segment.sourceEndMs, pointerId: event.pointerId };
            drag.startMs = this.snapSelectionTime(this.model.playheadMs, drag);
            drag.endMs = this.snapSelectionTime(timeMs, drag);
            return drag;
        }
        return { kind: 'selection-press', inside: false, timeMs, startX: x, segment, segmentStartMs: segment ? segment.sourceStartMs : 0, segmentEndMs: segment ? segment.sourceEndMs : 0, pointerId: event.pointerId };
    }

    onContentDragMove(drag, x) {
        if (drag.kind === 'hand') {
            this.host.scrollLeft = drag.startScroll - (x - drag.startX);
            this.canvas.style.cursor = 'grabbing';
            this.draw();
            return;
        }
        if (drag.kind === 'selection-press') {
            // Finché il puntatore resta fermo è un clic: il gesto si decide al primo spostamento
            if (Math.abs(x - drag.startX) < 3) return;
            if (drag.inside) {
                drag.kind = 'selection-move';
            } else if (drag.segment) {
                drag.kind = 'selection';
                drag.startMs = this.snapSelectionTime(drag.timeMs, drag);
                drag.endMs = drag.startMs;
            } else {
                // Fuori dal materiale mappato non si seleziona: il trascinamento sposta il cursore
                drag.kind = 'seek';
                drag.timeMs = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(x)));
                this.draw();
                return;
            }
        }
        if (drag.kind === 'operation') {
            if (!drag.moved && Math.abs(x - drag.startX) < 3) return;
            drag.moved = true;
            const raw = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(x) - drag.offsetMs));
            const snapped = Math.round(raw / this.model.frameDurationMs) * this.model.frameDurationMs;
            drag.timeMs = this.clampOperationTime(snapped, drag.operationIndex);
            this.pendingDrag = { index: drag.operationIndex, timeMs: drag.timeMs };
            this.scheduleDragNotification('OnTimelineOperationDrag', 'timeMs');
            this.canvas.style.cursor = 'grabbing';
        } else if (drag.kind === 'duration') {
            if (!drag.moved && Math.abs(x - drag.startX) < 3) return;
            drag.moved = true;
            const rawDuration = Math.max(this.model.frameDurationMs, drag.initialDurationMs + (x - drag.startX) / this.pixelsPerMs);
            drag.durationMs = Math.round(rawDuration / this.model.frameDurationMs) * this.model.frameDurationMs;
            this.pendingDrag = { index: drag.operationIndex, durationMs: drag.durationMs };
            this.scheduleDragNotification('OnTimelineDurationDrag', 'durationMs');
            this.canvas.style.cursor = 'ew-resize';
        } else if (drag.kind === 'selection') {
            drag.endMs = this.snapSelectionTime(this.timeAtX(x), drag);
            this.canvas.style.cursor = 'ew-resize';
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

    dragReadoutMs(drag) {
        // Durante il trascinamento di un'operazione tempo e durata stanno nella sua bandierina
        if (drag.kind === 'operation' || drag.kind === 'duration') return Number.NaN;
        if (drag.kind === 'selection') return drag.endMs;
        if (drag.kind === 'selection-move') return drag.startMs;
        return super.dragReadoutMs(drag);
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
        else if (drag.kind === 'selection-press') this.dotNetReference.invokeMethodAsync('OnTimelineSeek', drag.timeMs);
    }

    hoverReadoutMs(x, y) {
        return this.hoverOperationIndex >= 0 ? Number.NaN : super.hoverReadoutMs(x, y);
    }

    pointerCursor(x, y) {
        const layout = this.contentLayout;
        if (layout && (this.model.tool || 'select') === 'operations' && y >= layout.rulerTop) {
            // Con lo strumento Operazioni si prende l'operazione anche dalla sua bandierina
            const hit = this.hitOperation(x, y);
            this.hoverOperationIndex = hit ? hit.operation.index : -1;
            if (hit) return hit.part === 'duration' ? 'ew-resize' : 'grab';
            if (y < layout.trackTop) return 'grab';
            return 'default';
        }
        this.hoverOperationIndex = -1;
        return super.pointerCursor(x, y);
    }

    contentCursor(x, y) {
        const layout = this.contentLayout;
        if (!layout || y < layout.trackTop) return 'default';
        const tool = this.model.tool || 'select';
        if (tool === 'hand') return 'grab';
        const selection = this.selectionRange();
        if (selection && this.mappedSegmentAt((selection.startMs + selection.endMs) / 2)) {
            const edgeTolerance = this.model.precisionMode ? 7 : 5;
            if (Math.abs(this.xAtTime(selection.startMs) - x) <= edgeTolerance || Math.abs(this.xAtTime(selection.endMs) - x) <= edgeTolerance) return 'ew-resize';
            const timeMs = this.timeAtX(x);
            if (timeMs >= selection.startMs && timeMs <= selection.endMs) return 'grab';
        }
        return 'text';
    }

    handleContextMenu(event) {
        event.preventDefault();
        const layout = this.contentLayout;
        const rect = this.canvas.getBoundingClientRect();
        const x = event.clientX - rect.left;
        const y = event.clientY - rect.top;
        if (!layout || this.drag || y < layout.rulerTop || x < this.plotLeft) return;
        const hit = this.hitOperation(x, y);
        if (!hit && y < layout.trackTop) return;
        const timeMs = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(x)));
        const selection = this.selectionRange();
        const insideSelection = !hit && selection !== null && timeMs >= selection.startMs && timeMs <= selection.endMs;
        this.dotNetReference.invokeMethodAsync('OnTimelineContextMenu', event.clientX, event.clientY, timeMs, hit ? hit.operation.index : -1, insideSelection);
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
