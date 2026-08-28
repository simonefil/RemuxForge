const vertexSource = `#version 300 es
in vec2 a_position;
out vec2 v_uv;
void main() {
    v_uv = vec2((a_position.x + 1.0) * 0.5, 1.0 - ((a_position.y + 1.0) * 0.5));
    gl_Position = vec4(a_position, 0.0, 1.0);
}`;

const fragmentSource = `#version 300 es
precision highp float;
precision highp usampler2D;
in vec2 v_uv;
uniform sampler2D u_y8;
uniform sampler2D u_uv8;
uniform usampler2D u_y16;
uniform usampler2D u_uv16;
uniform bool u_p010;
uniform bool u_limited;
uniform bool u_hdr;
uniform bool u_bt2020;
uniform int u_transfer;
uniform mat3 u_yuvMatrix;
out vec4 color;

float pqToLinear(float value) {
    float m1 = 0.1593017578125;
    float m2 = 78.84375;
    float c1 = 0.8359375;
    float c2 = 18.8515625;
    float c3 = 18.6875;
    float power = pow(max(value, 0.0), 1.0 / m2);
    return pow(max(power - c1, 0.0) / max(c2 - c3 * power, 0.00001), 1.0 / m1);
}

float hlgToLinear(float value) {
    return value <= 0.5 ? value * value / 3.0 : (exp((value - 0.55991073) / 0.17883277) + 0.28466892) / 12.0;
}

vec3 toneMap(vec3 value) {
    value = value / (value + vec3(0.25));
    return pow(clamp(value, 0.0, 1.0), vec3(1.0 / 2.2));
}

void main() {
    vec3 yuv;
    if (u_p010) {
        float y = float(texture(u_y16, v_uv).r) / 65535.0;
        vec2 uv = vec2(texture(u_uv16, v_uv).rg) / 65535.0;
        yuv = vec3(y, uv);
        if (u_limited) {
            yuv.x = (yuv.x - (64.0 / 1023.0)) * (1023.0 / 876.0);
            yuv.yz = (yuv.yz - vec2(512.0 / 1023.0)) * (1023.0 / 896.0);
        } else {
            yuv.yz -= vec2(0.5);
        }
    } else {
        yuv = vec3(texture(u_y8, v_uv).r, texture(u_uv8, v_uv).rg);
        if (u_limited) {
            yuv.x = (yuv.x - (16.0 / 255.0)) * (255.0 / 219.0);
            yuv.yz = (yuv.yz - vec2(128.0 / 255.0)) * (255.0 / 224.0);
        } else {
            yuv.yz -= vec2(0.5);
        }
    }
    vec3 rgb = max(u_yuvMatrix * yuv, vec3(0.0));
    if (u_hdr) {
        if (u_transfer == 1) rgb = vec3(pqToLinear(rgb.r), pqToLinear(rgb.g), pqToLinear(rgb.b));
        else if (u_transfer == 2) rgb = vec3(hlgToLinear(rgb.r), hlgToLinear(rgb.g), hlgToLinear(rgb.b));
        if (u_bt2020) rgb = mat3(1.6605, -0.1246, -0.0182, -0.5876, 1.1329, -0.1006, -0.0728, -0.0083, 1.1187) * rgb;
        rgb = toneMap(rgb);
    }
    color = vec4(clamp(rgb, 0.0, 1.0), 1.0);
}`;

class RawFrameRenderer {
    constructor(canvas) {
        this.canvas = canvas;
        this.gl = canvas.getContext('webgl2', { alpha: false, antialias: false });
        if (!this.gl) throw new Error('WebGL2 is required for raw video preview');
        this.program = createProgram(this.gl, vertexSource, fragmentSource);
        this.textures = [this.gl.createTexture(), this.gl.createTexture(), this.gl.createTexture(), this.gl.createTexture()];
        const positions = this.gl.createBuffer();
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, positions);
        this.gl.bufferData(this.gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]), this.gl.STATIC_DRAW);
        const location = this.gl.getAttribLocation(this.program, 'a_position');
        this.gl.enableVertexAttribArray(location);
        this.gl.vertexAttribPointer(location, 2, this.gl.FLOAT, false, 0, 0);
    }

    render(buffer, metadata) {
        const gl = this.gl;
        const width = Number(metadata.width);
        const height = Number(metadata.height);
        const p010 = metadata.pixelFormat === 'p010le';
        const canvasWidth = Math.max(2, Math.floor(this.canvas.clientWidth / 2) * 2);
        const canvasHeight = Math.max(2, Math.floor(this.canvas.clientHeight / 2) * 2);
        this.canvas.width = canvasWidth;
        this.canvas.height = canvasHeight;
        const scale = Math.min(canvasWidth / width, canvasHeight / height);
        const viewportWidth = Math.max(2, Math.floor(width * scale / 2) * 2);
        const viewportHeight = Math.max(2, Math.floor(height * scale / 2) * 2);
        const viewportX = Math.floor((canvasWidth - viewportWidth) / 2);
        const viewportY = Math.floor((canvasHeight - viewportHeight) / 2);
        gl.viewport(0, 0, canvasWidth, canvasHeight);
        gl.clearColor(0, 0, 0, 1);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.viewport(viewportX, viewportY, viewportWidth, viewportHeight);
        gl.useProgram(this.program);
        gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);

        if (p010) {
            const yLength = width * height;
            uploadPlane(gl, this.textures[0], 0, 1, 1, gl.R8, gl.RED, new Uint8Array([0]));
            uploadPlane(gl, this.textures[1], 1, 1, 1, gl.RG8, gl.RG, new Uint8Array([128, 128]));
            uploadIntegerPlane(gl, this.textures[2], 2, width, height, gl.R16UI, gl.RED_INTEGER, new Uint16Array(buffer, 0, yLength));
            uploadIntegerPlane(gl, this.textures[3], 3, width / 2, height / 2, gl.RG16UI, gl.RG_INTEGER, new Uint16Array(buffer, yLength * 2));
        } else {
            const yLength = width * height;
            uploadPlane(gl, this.textures[0], 0, width, height, gl.R8, gl.RED, new Uint8Array(buffer, 0, yLength));
            uploadPlane(gl, this.textures[1], 1, width / 2, height / 2, gl.RG8, gl.RG, new Uint8Array(buffer, yLength));
            uploadIntegerPlane(gl, this.textures[2], 2, 1, 1, gl.R16UI, gl.RED_INTEGER, new Uint16Array([0]));
            uploadIntegerPlane(gl, this.textures[3], 3, 1, 1, gl.RG16UI, gl.RG_INTEGER, new Uint16Array([32768, 32768]));
        }

        setUniforms(gl, this.program, metadata, p010);
        gl.drawArrays(gl.TRIANGLES, 0, 6);
    }

    clear() {
        const gl = this.gl;
        gl.viewport(0, 0, this.canvas.width, this.canvas.height);
        gl.clearColor(0, 0, 0, 1);
        gl.clear(gl.COLOR_BUFFER_BIT);
    }

    dispose() {
        this.gl.deleteTexture(this.textures[0]);
        this.gl.deleteTexture(this.textures[1]);
        this.gl.deleteTexture(this.textures[2]);
        this.gl.deleteTexture(this.textures[3]);
        this.gl.deleteProgram(this.program);
    }
}

export function createPreviewPair(sourceCanvas, languageCanvas) {
    const source = new RawFrameRenderer(sourceCanvas);
    const language = new RawFrameRenderer(languageCanvas);
    const sourceCache = new Map();
    const languageCache = new Map();
    let lastSourceIndex = null;
    let lastLanguageIndex = null;
    let generation = 0;
    let controller = null;
    return {
        async loadPair(sourceUrl, languageUrl) {
            generation++;
            const current = generation;
            if (controller) controller.abort();
            controller = new AbortController();
            const sourceSizedUrl = sourceUrl ? withCanvasSize(sourceUrl, source.canvas) : null;
            const languageSizedUrl = languageUrl ? withCanvasSize(languageUrl, language.canvas) : null;
            const sourceIndex = sourceSizedUrl ? parseFrameUrl(sourceSizedUrl).index : null;
            const languageIndex = languageSizedUrl ? parseFrameUrl(languageSizedUrl).index : null;
            const results = await Promise.allSettled([
                sourceSizedUrl ? fetchRawFrameWindow(sourceSizedUrl, lastSourceIndex, sourceCache, controller.signal) : Promise.resolve(null),
                languageSizedUrl ? fetchRawFrameWindow(languageSizedUrl, lastLanguageIndex, languageCache, controller.signal) : Promise.resolve(null)
            ]);
            if (current !== generation) return false;
            if (results[0].status === 'rejected') throw new Error(`Source: ${results[0].reason}`);
            if (results[1].status === 'rejected') throw new Error(`Language: ${results[1].reason}`);
            const sourceFrame = results[0].value;
            const languageFrame = results[1].value;
            lastSourceIndex = sourceIndex;
            lastLanguageIndex = languageIndex;
            if (sourceFrame) source.render(sourceFrame.buffer, sourceFrame.metadata);
            else source.clear();
            if (languageFrame) language.render(languageFrame.buffer, languageFrame.metadata);
            else language.clear();
            return true;
        },
        cancel() {
            generation++;
            if (controller) controller.abort();
            controller = null;
        },
        dispose() {
            this.cancel();
            sourceCache.clear();
            languageCache.clear();
            source.dispose();
            language.dispose();
        }
    };
}

function withCanvasSize(url, canvas) {
    const separator = url.includes('?') ? '&' : '?';
    const container = canvas.parentElement || canvas;
    const width = Math.max(2, Math.floor(container.clientWidth / 2) * 2);
    const height = Math.max(2, Math.floor(container.clientHeight / 2) * 2);
    return `${url}${separator}width=${width}&height=${height}`;
}

export function confirmDiscard(message) {
    return window.confirm(message);
}

export function focusElement(element) {
    element?.focus({ preventScroll: true });
    if (typeof element?.select === 'function') element.select();
}

export function captureEditorKeyboard(root, dotNetReference) {
    const handler = event => {
        const editing = event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement || event.target instanceof HTMLSelectElement || event.target?.isContentEditable === true;
        const handled = event.key === 'Escape' || (!editing && ['ArrowLeft', 'ArrowRight', 'PageUp', 'PageDown', 'Home', 'End', 'Delete'].includes(event.key));
        if (!handled) return;
        event.preventDefault();
        event.stopPropagation();
        dotNetReference.invokeMethodAsync('OnEditorKey', event.key, event.shiftKey, editing);
    };
    root.addEventListener('keydown', handler);
    return { dispose: () => root.removeEventListener('keydown', handler) };
}

class EditMapTimeline {
    constructor(host, canvas, dotNetReference, model) {
        this.host = host;
        this.canvas = canvas;
        this.context = canvas.getContext('2d');
        this.dotNetReference = dotNetReference;
        this.model = model;
        this.range = host.querySelector('.edit-map-timeline-scroll-range');
        this.pixelsPerMs = Math.max(0.000001, host.clientWidth / Math.max(1, model.durationMs));
        this.drag = null;
        this.hoverX = null;
        this.pendingDrag = null;
        this.dragFrame = null;
        this.resizeObserver = new ResizeObserver(() => this.resize());
        this.resizeObserver.observe(host);
        this.themeObserver = new MutationObserver(() => this.draw());
        this.themeObserver.observe(document.documentElement, { attributes: true, subtree: true, attributeFilter: ['class', 'style', 'href'] });
        this.onScroll = () => this.draw();
        this.onWheel = event => this.handleWheel(event);
        this.onPointerDown = event => this.handlePointerDown(event);
        this.onPointerMove = event => this.handlePointerMove(event);
        this.onPointerUp = event => this.handlePointerUp(event);
        this.onPointerLeave = () => { this.hoverX = null; this.draw(); };
        this.onDoubleClick = () => this.setZoom('fit');
        host.addEventListener('scroll', this.onScroll, { passive: true });
        canvas.addEventListener('wheel', this.onWheel, { passive: false });
        canvas.addEventListener('pointerdown', this.onPointerDown);
        canvas.addEventListener('pointermove', this.onPointerMove);
        canvas.addEventListener('pointerup', this.onPointerUp);
        canvas.addEventListener('pointercancel', this.onPointerUp);
        canvas.addEventListener('pointerleave', this.onPointerLeave);
        canvas.addEventListener('dblclick', this.onDoubleClick);
        this.resize();
        this.setZoom('fit');
    }

    update(model, centerPlayhead) {
        this.model = model;
        this.updateRange();
        if (centerPlayhead) this.center(model.playheadMs);
        this.draw();
    }

    setZoom(preset) {
        const viewport = Math.max(1, this.host.clientWidth);
        let windowMs = this.model.durationMs || 1;
        if (preset === '10s') windowMs = 10000;
        else if (preset === '1s') windowMs = 1000;
        else if (preset === 'frame') windowMs = Math.max(1, this.model.frameDurationMs);
        this.pixelsPerMs = viewport / windowMs;
        this.updateRange();
        this.center(this.model.playheadMs);
        this.draw();
    }

    zoomBy(factor, anchorClientX) {
        const rect = this.canvas.getBoundingClientRect();
        const anchorX = anchorClientX === undefined ? rect.width / 2 : anchorClientX - rect.left;
        const anchorTime = this.timeAtX(anchorX);
        this.pixelsPerMs = Math.max(this.host.clientWidth / Math.max(1, this.model.durationMs), Math.min(8, this.pixelsPerMs * factor));
        this.updateRange();
        this.host.scrollLeft = Math.max(0, anchorTime * this.pixelsPerMs - anchorX);
        this.draw();
    }

    center(timeMs) {
        this.host.scrollLeft = Math.max(0, timeMs * this.pixelsPerMs - this.host.clientWidth / 2);
    }

    resize() {
        const ratio = window.devicePixelRatio || 1;
        const width = Math.max(1, this.host.clientWidth);
        const height = Math.max(1, this.host.clientHeight - 16);
        this.canvas.style.width = `${width}px`;
        this.canvas.style.height = `${height}px`;
        this.canvas.width = Math.floor(width * ratio);
        this.canvas.height = Math.floor(height * ratio);
        this.context.setTransform(ratio, 0, 0, ratio, 0, 0);
        this.updateRange();
        this.draw();
    }

    updateRange() {
        const width = Math.max(this.host.clientWidth, Math.min(20000000, Math.ceil((this.model.durationMs || 1) * Math.max(this.pixelsPerMs, 0.000001))));
        this.range.style.width = `${width}px`;
    }

    draw() {
        const ctx = this.context;
        const width = this.host.clientWidth;
        const height = Math.max(1, this.host.clientHeight - 16);
        if (width <= 0 || height <= 0) return;
        const styles = getComputedStyle(this.host);
        const background = cssColor(styles, '--rz-base-background-color', styles.backgroundColor);
        const text = cssColor(styles, '--rz-text-color', styles.color);
        const secondary = cssColor(styles, '--rz-text-secondary-color', text);
        const border = cssColor(styles, '--rz-border-color', secondary);
        const primary = cssColor(styles, '--rz-primary', text);
        const danger = cssColor(styles, '--rz-danger', primary);
        const warning = cssColor(styles, '--rz-warning', primary);
        ctx.clearRect(0, 0, width, height);
        ctx.fillStyle = background;
        ctx.fillRect(0, 0, width, height);
        ctx.font = '12px sans-serif';
        ctx.textBaseline = 'middle';
        const startMs = this.host.scrollLeft / this.pixelsPerMs;
        const endMs = startMs + width / this.pixelsPerMs;
        drawRuler(ctx, width, startMs, endMs, this.pixelsPerMs, text, border);
        const sourceY = 48;
        const languageY = 82;
        const operationY = 116;
        ctx.fillStyle = secondary;
        ctx.fillText(this.model.labels?.source || 'SOURCE', 8, sourceY);
        ctx.fillText(this.model.labels?.language || 'LANGUAGE', 8, languageY);
        ctx.strokeStyle = border;
        ctx.beginPath();
        ctx.moveTo(70, sourceY); ctx.lineTo(width, sourceY);
        ctx.moveTo(70, languageY); ctx.lineTo(width, languageY);
        ctx.stroke();
        for (const segment of this.model.segments || []) {
            const x1 = this.xAtTime(segment.sourceStartMs);
            const x2 = this.xAtTime(segment.sourceEndMs);
            if (x2 < 0 || x1 > width) continue;
            if (segment.kind === 'InsertedGap') {
                ctx.fillStyle = warning;
                ctx.fillRect(x1, languageY - 5, Math.max(2, x2 - x1), 10);
            } else if (segment.kind === 'Mapped') {
                ctx.fillStyle = primary;
                ctx.fillRect(x1, languageY - 3, Math.max(1, x2 - x1), 6);
            }
        }
        for (const operation of this.model.operations || []) {
            const temporary = this.drag && this.drag.kind === 'operation' && this.drag.operationIndex === operation.index ? this.drag.timeMs : operation.sourceMs;
            const temporaryDuration = this.drag && this.drag.kind === 'duration' && this.drag.operationIndex === operation.index ? this.drag.durationMs : Math.abs(operation.durationMs);
            const x = this.xAtTime(temporary);
            if (x < -80 || x > width + 80) continue;
            ctx.strokeStyle = operation.selected ? warning : danger;
            ctx.lineWidth = operation.selected ? 3 : 2;
            ctx.beginPath(); ctx.moveTo(x, 32); ctx.lineTo(x, height - 8); ctx.stroke();
            ctx.fillStyle = operation.selected ? warning : danger;
            const durationEndX = x + Math.max(8, temporaryDuration * this.pixelsPerMs);
            ctx.fillRect(x, operationY - 3, Math.max(2, durationEndX - x), 6);
            ctx.fillRect(durationEndX - 4, operationY - 7, 8, 14);
            const label = operation.type === 'CUT_SEGMENT' ? `${this.model.labels?.cut || 'CUT'} −${Math.round(temporaryDuration)} ms` : `${this.model.labels?.insert || 'INSERT'} +${Math.round(temporaryDuration)} ms`;
            ctx.fillText(label, x + 5, operationY);
        }
        const playheadX = this.xAtTime(this.model.playheadMs);
        ctx.strokeStyle = primary;
        ctx.lineWidth = 2;
        ctx.beginPath(); ctx.moveTo(playheadX, 24); ctx.lineTo(playheadX, height); ctx.stroke();
        ctx.fillStyle = primary;
        ctx.beginPath(); ctx.moveTo(playheadX - 5, 24); ctx.lineTo(playheadX + 5, 24); ctx.lineTo(playheadX, 31); ctx.closePath(); ctx.fill();
        ctx.fillStyle = text;
        const sourceLabel = `${this.model.labels?.sourcePts || 'Source PTS'} ${this.model.sourcePlayheadLabel || '—'}`;
        const languageLabel = `${this.model.labels?.languagePts || 'Language PTS'} ${this.model.languagePlayheadLabel || '—'}`;
        ctx.fillText(sourceLabel, Math.max(4, Math.min(width - 180, playheadX + 8)), sourceY - 14);
        ctx.fillText(languageLabel, Math.max(4, Math.min(width - 190, playheadX + 8)), languageY + 14);
        const viewportStart = Math.max(0, startMs / Math.max(1, this.model.durationMs));
        const viewportEnd = Math.min(1, endMs / Math.max(1, this.model.durationMs));
        const overviewWidth = Math.min(160, Math.max(60, width * 0.16));
        const overviewX = width - overviewWidth - 8;
        ctx.strokeStyle = border;
        ctx.strokeRect(overviewX, height - 9, overviewWidth, 5);
        ctx.fillStyle = primary;
        ctx.fillRect(overviewX + overviewWidth * viewportStart, height - 9, Math.max(2, overviewWidth * (viewportEnd - viewportStart)), 5);
        if (this.hoverX !== null && !this.drag) {
            const hoverTime = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(this.hoverX)));
            const label = formatTimelineTime(hoverTime);
            ctx.fillStyle = text;
            ctx.fillText(label, Math.min(width - 85, this.hoverX + 8), height - 10);
        }
        ctx.lineWidth = 1;
    }

    xAtTime(timeMs) {
        return timeMs * this.pixelsPerMs - this.host.scrollLeft;
    }

    timeAtX(x) {
        return (this.host.scrollLeft + x) / this.pixelsPerMs;
    }

    hitOperation(x) {
        let result = null;
        let distance = 9;
        for (const operation of this.model.operations || []) {
            const durationHandleX = this.xAtTime(operation.sourceMs) + Math.max(8, Math.abs(operation.durationMs) * this.pixelsPerMs);
            const handleDistance = Math.abs(durationHandleX - x);
            if (handleDistance <= 7) return { operation, kind: 'duration' };
            const currentDistance = Math.abs(this.xAtTime(operation.sourceMs) - x);
            if (currentDistance <= distance) {
                result = { operation, kind: 'operation' };
                distance = currentDistance;
            }
        }
        return result;
    }

    handleWheel(event) {
        event.preventDefault();
        if (event.ctrlKey || event.metaKey) this.zoomBy(event.deltaY < 0 ? 1.2 : 0.8, event.clientX);
        else this.host.scrollLeft += Math.abs(event.deltaX) > Math.abs(event.deltaY) ? event.deltaX : event.deltaY;
    }

    handlePointerDown(event) {
        const rect = this.canvas.getBoundingClientRect();
        const x = event.clientX - rect.left;
        const hit = this.hitOperation(x);
        this.canvas.setPointerCapture(event.pointerId);
        if (hit) {
            const operation = hit.operation;
            this.drag = hit.kind === 'duration'
                ? { kind: 'duration', operationIndex: operation.index, sourceMs: operation.sourceMs, durationMs: Math.abs(operation.durationMs), pointerId: event.pointerId }
                : { kind: 'operation', operationIndex: operation.index, timeMs: operation.sourceMs, pointerId: event.pointerId };
            this.drag.selectionPromise = this.dotNetReference.invokeMethodAsync('OnTimelineOperationSelected', operation.index);
        } else if (event.offsetY < 32) {
            this.drag = { kind: 'pan', startX: event.clientX, startScroll: this.host.scrollLeft, pointerId: event.pointerId };
        } else {
            this.drag = { kind: 'seek', timeMs: this.timeAtX(x), pointerId: event.pointerId };
        }
    }

    handlePointerMove(event) {
        const rect = this.canvas.getBoundingClientRect();
        const x = event.clientX - rect.left;
        this.hoverX = x;
        if (!this.drag) { this.draw(); return; }
        if (this.drag.kind === 'pan') {
            this.host.scrollLeft = this.drag.startScroll - (event.clientX - this.drag.startX);
        } else if (this.drag.kind === 'operation') {
            const raw = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(x)));
            const snapped = Math.round(raw / this.model.frameDurationMs) * this.model.frameDurationMs;
            this.drag.timeMs = this.clampOperationTime(snapped, this.drag.operationIndex);
            this.pendingDrag = { index: this.drag.operationIndex, timeMs: this.drag.timeMs };
            if (!this.dragFrame) {
                this.dragFrame = requestAnimationFrame(() => {
                    const pending = this.pendingDrag;
                    this.dragFrame = null;
                    if (pending) Promise.resolve(this.drag?.selectionPromise).then(() => this.dotNetReference.invokeMethodAsync('OnTimelineOperationDrag', pending.index, pending.timeMs, false));
                });
            }
            this.draw();
        } else if (this.drag.kind === 'duration') {
            const rawDuration = Math.max(this.model.frameDurationMs, this.timeAtX(x) - this.drag.sourceMs);
            this.drag.durationMs = Math.round(rawDuration / this.model.frameDurationMs) * this.model.frameDurationMs;
            this.pendingDrag = { index: this.drag.operationIndex, durationMs: this.drag.durationMs };
            if (!this.dragFrame) {
                this.dragFrame = requestAnimationFrame(() => {
                    const pending = this.pendingDrag;
                    this.dragFrame = null;
                    if (pending) Promise.resolve(this.drag?.selectionPromise).then(() => this.dotNetReference.invokeMethodAsync('OnTimelineDurationDrag', pending.index, pending.durationMs, false));
                });
            }
            this.draw();
        } else {
            this.drag.timeMs = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(x)));
            this.draw();
        }
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
        for (const operation of this.model.operations || []) {
            if (operation.index === operationIndex) continue;
            if (Math.abs(result - operation.sourceMs) < this.model.frameDurationMs) {
                result = result < operation.sourceMs ? operation.sourceMs - this.model.frameDurationMs : operation.sourceMs + this.model.frameDurationMs;
            }
        }
        return Math.max(0, Math.min(this.model.durationMs, result));
    }

    handlePointerUp(event) {
        if (!this.drag) return;
        const drag = this.drag;
        this.drag = null;
        if (this.dragFrame) {
            cancelAnimationFrame(this.dragFrame);
            this.dragFrame = null;
        }
        this.pendingDrag = null;
        if (drag.kind === 'operation') Promise.resolve(drag.selectionPromise).then(() => this.dotNetReference.invokeMethodAsync('OnTimelineOperationDrag', drag.operationIndex, drag.timeMs, true));
        else if (drag.kind === 'duration') Promise.resolve(drag.selectionPromise).then(() => this.dotNetReference.invokeMethodAsync('OnTimelineDurationDrag', drag.operationIndex, drag.durationMs, true));
        else if (drag.kind === 'seek') this.dotNetReference.invokeMethodAsync('OnTimelineSeek', drag.timeMs);
        try { this.canvas.releasePointerCapture(event.pointerId); } catch { }
        this.draw();
    }

    dispose() {
        if (this.dragFrame) cancelAnimationFrame(this.dragFrame);
        this.resizeObserver.disconnect();
        this.themeObserver.disconnect();
        this.host.removeEventListener('scroll', this.onScroll);
        this.canvas.removeEventListener('wheel', this.onWheel);
        this.canvas.removeEventListener('pointerdown', this.onPointerDown);
        this.canvas.removeEventListener('pointermove', this.onPointerMove);
        this.canvas.removeEventListener('pointerup', this.onPointerUp);
        this.canvas.removeEventListener('pointercancel', this.onPointerUp);
        this.canvas.removeEventListener('pointerleave', this.onPointerLeave);
        this.canvas.removeEventListener('dblclick', this.onDoubleClick);
    }
}

export function createTimeline(host, canvas, dotNetReference, model) {
    return new EditMapTimeline(host, canvas, dotNetReference, model);
}

function cssColor(styles, variable, fallback) {
    const value = styles.getPropertyValue(variable).trim();
    return value || fallback;
}

function drawRuler(context, width, startMs, endMs, pixelsPerMs, color, border) {
    const candidates = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 30000, 60000, 300000, 600000];
    let step = candidates[candidates.length - 1];
    for (const candidate of candidates) {
        if (candidate * pixelsPerMs >= 75) { step = candidate; break; }
    }
    const first = Math.floor(startMs / step) * step;
    context.strokeStyle = border;
    context.fillStyle = color;
    context.beginPath();
    for (let time = first; time <= endMs + step; time += step) {
        const x = (time - startMs) * pixelsPerMs;
        context.moveTo(x, 18); context.lineTo(x, 28);
        context.fillText(formatTimelineTime(time), x + 3, 10);
    }
    context.stroke();
}

function formatTimelineTime(milliseconds) {
    const total = Math.max(0, Math.round(milliseconds));
    const hours = Math.floor(total / 3600000);
    const minutes = Math.floor(total / 60000) % 60;
    const seconds = Math.floor(total / 1000) % 60;
    const millis = total % 1000;
    return `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}.${String(millis).padStart(3, '0')}`;
}

async function fetchRawFrameWindow(url, previousIndex, cache, signal) {
    if (cache.has(url)) return cache.get(url);
    await waitForPreviewRequest(signal);
    if (cache.has(url)) return cache.get(url);
    const parsed = parseFrameUrl(url);
    const batchSize = 24;
    const startIndex = previousIndex !== null && parsed.index < previousIndex ? Math.max(0, parsed.index - (batchSize - 1)) : parsed.index;
    const requestUrl = `${parsed.prefix}${startIndex}${parsed.query}${parsed.query ? '&' : '?'}count=${batchSize}`;
    const response = await fetch(requestUrl, { signal, cache: 'default' });
    if (!response.ok) throw new Error(await response.text() || `Preview request failed: ${response.status}`);
    const buffer = await response.arrayBuffer();
    const count = Number(response.headers.get('X-Frame-Count')) || 1;
    const frameBytes = Number(response.headers.get('X-Frame-Bytes')) || buffer.byteLength;
    const metadata = {
        width: response.headers.get('X-Frame-Width'),
        height: response.headers.get('X-Frame-Height'),
        pixelFormat: response.headers.get('X-Pixel-Format'),
        colorSpace: response.headers.get('X-Color-Space'),
        colorRange: response.headers.get('X-Color-Range'),
        colorPrimaries: response.headers.get('X-Color-Primaries'),
        colorTransfer: response.headers.get('X-Color-Transfer')
    };
    for (let i = 0; i < count; i++) {
        const key = `${parsed.prefix}${startIndex + i}${parsed.query}`;
        cache.set(key, { buffer: buffer.slice(i * frameBytes, (i + 1) * frameBytes), metadata });
    }
    while (cache.size > 240) cache.delete(cache.keys().next().value);
    if (!cache.has(url)) throw new Error('Requested preview frame was not returned');
    return cache.get(url);
}

function waitForPreviewRequest(signal) {
    if (signal.aborted)
        return Promise.reject(new DOMException('Aborted', 'AbortError'));
    return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
            signal.removeEventListener('abort', abort);
            resolve();
        }, 30);
        const abort = () => {
            clearTimeout(timer);
            reject(new DOMException('Aborted', 'AbortError'));
        };
        signal.addEventListener('abort', abort, { once: true });
    });
}

function parseFrameUrl(url) {
    const question = url.indexOf('?');
    const path = question >= 0 ? url.substring(0, question) : url;
    const query = question >= 0 ? url.substring(question) : '';
    const match = path.match(/^(.*\/)(\d+)$/);
    if (!match) throw new Error('Invalid preview frame URL');
    return { prefix: match[1], index: Number(match[2]), query };
}

function createProgram(gl, vertex, fragment) {
    const program = gl.createProgram();
    const vertexShader = compileShader(gl, gl.VERTEX_SHADER, vertex);
    const fragmentShader = compileShader(gl, gl.FRAGMENT_SHADER, fragment);
    gl.attachShader(program, vertexShader);
    gl.attachShader(program, fragmentShader);
    gl.linkProgram(program);
    gl.deleteShader(vertexShader);
    gl.deleteShader(fragmentShader);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
    return program;
}

function compileShader(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(shader));
    return shader;
}

function uploadPlane(gl, texture, unit, width, height, internalFormat, format, data) {
    gl.activeTexture(gl.TEXTURE0 + unit);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    configureTexture(gl, true);
    gl.texImage2D(gl.TEXTURE_2D, 0, internalFormat, width, height, 0, format, gl.UNSIGNED_BYTE, data);
}

function uploadIntegerPlane(gl, texture, unit, width, height, internalFormat, format, data) {
    gl.activeTexture(gl.TEXTURE0 + unit);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    configureTexture(gl, false);
    gl.texImage2D(gl.TEXTURE_2D, 0, internalFormat, width, height, 0, format, gl.UNSIGNED_SHORT, data);
}

function configureTexture(gl, linear) {
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, linear ? gl.LINEAR : gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, linear ? gl.LINEAR : gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
}

function setUniforms(gl, program, metadata, p010) {
    gl.uniform1i(gl.getUniformLocation(program, 'u_y8'), 0);
    gl.uniform1i(gl.getUniformLocation(program, 'u_uv8'), 1);
    gl.uniform1i(gl.getUniformLocation(program, 'u_y16'), 2);
    gl.uniform1i(gl.getUniformLocation(program, 'u_uv16'), 3);
    gl.uniform1i(gl.getUniformLocation(program, 'u_p010'), p010 ? 1 : 0);
    gl.uniform1i(gl.getUniformLocation(program, 'u_limited'), metadata.colorRange !== 'pc' ? 1 : 0);
    const transfer = metadata.colorTransfer === 'smpte2084' ? 1 : metadata.colorTransfer === 'arib-std-b67' ? 2 : 0;
    gl.uniform1i(gl.getUniformLocation(program, 'u_hdr'), transfer !== 0 ? 1 : 0);
    gl.uniform1i(gl.getUniformLocation(program, 'u_bt2020'), metadata.colorPrimaries === 'bt2020' ? 1 : 0);
    gl.uniform1i(gl.getUniformLocation(program, 'u_transfer'), transfer);
    let matrix = [1, 1, 1, 0, -0.187324, 1.8556, 1.5748, -0.468124, 0];
    if (metadata.colorSpace === 'bt2020nc') matrix = [1, 1, 1, 0, -0.164553, 1.8814, 1.4746, -0.571353, 0];
    else if (metadata.colorSpace !== 'bt709') matrix = [1, 1, 1, 0, -0.344136, 1.772, 1.402, -0.714136, 0];
    gl.uniformMatrix3fv(gl.getUniformLocation(program, 'u_yuvMatrix'), false, matrix);
}
