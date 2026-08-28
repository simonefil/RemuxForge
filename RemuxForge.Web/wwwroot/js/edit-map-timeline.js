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
        this.positionBuffer = this.gl.createBuffer();
        this.gl.bindBuffer(this.gl.ARRAY_BUFFER, this.positionBuffer);
        this.gl.bufferData(this.gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]), this.gl.STATIC_DRAW);
        const location = this.gl.getAttribLocation(this.program, 'a_position');
        this.gl.enableVertexAttribArray(location);
        this.gl.vertexAttribPointer(location, 2, this.gl.FLOAT, false, 0, 0);
        this.currentBuffer = null;
        this.currentMetadata = null;
    }

    render(buffer, metadata) {
        this.currentBuffer = buffer;
        this.currentMetadata = metadata;
        this.renderCurrent();
    }

    renderCurrent() {
        if (!this.currentBuffer || !this.currentMetadata) {
            this.clear();
            return;
        }
        const gl = this.gl;
        const buffer = this.currentBuffer;
        const metadata = this.currentMetadata;
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
        this.currentBuffer = null;
        this.currentMetadata = null;
        this.canvas.width = Math.max(2, Math.floor(this.canvas.clientWidth / 2) * 2);
        this.canvas.height = Math.max(2, Math.floor(this.canvas.clientHeight / 2) * 2);
        gl.viewport(0, 0, this.canvas.width, this.canvas.height);
        gl.clearColor(0, 0, 0, 1);
        gl.clear(gl.COLOR_BUFFER_BIT);
    }

    resize() {
        this.renderCurrent();
    }

    dispose() {
        this.gl.deleteTexture(this.textures[0]);
        this.gl.deleteTexture(this.textures[1]);
        this.gl.deleteTexture(this.textures[2]);
        this.gl.deleteTexture(this.textures[3]);
        this.gl.deleteBuffer(this.positionBuffer);
        this.gl.deleteProgram(this.program);
    }
}

export function createPreviewPair(sourceCanvas, languageCanvas) {
    fitPreviewCanvas(sourceCanvas);
    fitPreviewCanvas(languageCanvas);
    const source = new RawFrameRenderer(sourceCanvas);
    const language = new RawFrameRenderer(languageCanvas);
    const sourceCache = new Map();
    const languageCache = new Map();
    let lastSourceIndex = null;
    let lastLanguageIndex = null;
    let generation = 0;
    let controller = null;
    const sourceResizeObserver = observePreviewCanvas(sourceCanvas, source);
    const languageResizeObserver = observePreviewCanvas(languageCanvas, language);
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
            sourceResizeObserver.disconnect();
            languageResizeObserver.disconnect();
            source.dispose();
            language.dispose();
        }
    };
}

function fitPreviewCanvas(canvas) {
    const host = canvas.parentElement;
    const stage = host?.parentElement;
    if (!host || !stage) return;
    const availableWidth = Math.max(2, stage.clientWidth);
    const availableHeight = Math.max(2, stage.clientHeight);
    const width = Math.min(availableWidth, availableHeight * 16 / 9);
    const height = width * 9 / 16;
    host.style.width = `${Math.floor(width)}px`;
    host.style.height = `${Math.floor(height)}px`;
}

function observePreviewCanvas(canvas, renderer) {
    const stage = canvas.parentElement?.parentElement;
    const observer = new ResizeObserver(() => {
        fitPreviewCanvas(canvas);
        renderer.resize();
    });
    if (stage) observer.observe(stage);
    return observer;
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

export function captureEditorKeyboard(root, dotNetReference) {
    let inFlight = false;
    let pendingSide = null;
    let pendingDelta = 0;
    let repeatDelay = null;
    let repeatTimer = null;
    const queueStep = (side, delta) => {
        if (pendingSide !== side) {
            pendingSide = side;
            pendingDelta = 0;
        }
        pendingDelta += delta;
        if (inFlight) return;
        const send = async () => {
            if (!pendingDelta) { inFlight = false; return; }
            inFlight = true;
            const currentSide = pendingSide;
            const currentDelta = pendingDelta;
            pendingDelta = 0;
            try { await dotNetReference.invokeMethodAsync('OnFrameStep', currentSide, currentDelta); }
            finally { send(); }
        };
        send();
    };
    const stopRepeat = () => {
        if (repeatDelay) clearTimeout(repeatDelay);
        if (repeatTimer) clearInterval(repeatTimer);
        repeatDelay = null;
        repeatTimer = null;
    };
    const handler = event => {
        const editing = event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement || event.target instanceof HTMLSelectElement || event.target?.isContentEditable === true;
        const handled = event.key === 'Escape' || (!editing && ['ArrowLeft', 'ArrowRight', 'Home', 'End', 'Delete'].includes(event.key));
        if (!handled) return;
        event.preventDefault();
        event.stopPropagation();
        if (!editing && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
            queueStep('source', (event.key === 'ArrowLeft' ? -1 : 1) * (event.shiftKey ? 10 : 1));
            return;
        }
        dotNetReference.invokeMethodAsync('OnEditorKey', event.key, event.shiftKey, editing);
    };
    const pointerDown = event => {
        const button = event.target.closest('[data-frame-side][data-frame-delta]');
        if (!button || button.disabled || button.getAttribute('aria-disabled') === 'true') return;
        const side = button.dataset.frameSide;
        const delta = Number(button.dataset.frameDelta);
        if (!Number.isFinite(delta)) return;
        event.preventDefault();
        stopRepeat();
        queueStep(side, delta);
        repeatDelay = setTimeout(() => { repeatTimer = setInterval(() => queueStep(side, delta), 35); }, 240);
    };
    root.addEventListener('keydown', handler);
    root.addEventListener('pointerdown', pointerDown);
    window.addEventListener('pointerup', stopRepeat);
    window.addEventListener('pointercancel', stopRepeat);
    return { dispose: () => {
        stopRepeat();
        root.removeEventListener('keydown', handler);
        root.removeEventListener('pointerdown', pointerDown);
        window.removeEventListener('pointerup', stopRepeat);
        window.removeEventListener('pointercancel', stopRepeat);
    } };
}

class EditMapTimeline {
    constructor(host, canvas, dotNetReference, model) {
        this.host = host;
        this.canvas = canvas;
        this.context = canvas.getContext('2d');
        this.dotNetReference = dotNetReference;
        this.model = model;
        this.sourceAudioImage = null;
        this.languageAudioImage = null;
        this.waveformGain = clampWaveformGain(model.waveformGain);
        this.plotLeft = 112;
        this.navigatorHeight = 22;
        this.rulerHeight = 24;
        this.sourceAudioImageController = null;
        this.languageAudioImageController = null;
        this.sourceAudioImageGeneration = 0;
        this.languageAudioImageGeneration = 0;
        this.range = host.querySelector('.edit-map-timeline-scroll-range');
        this.pixelsPerMs = Math.max(0.000001, Math.max(1, host.clientWidth - this.plotLeft) / Math.max(1, model.durationMs));
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
        this.onDoubleClick = () => this.fitTimeline();
        host.addEventListener('scroll', this.onScroll, { passive: true });
        canvas.addEventListener('wheel', this.onWheel, { passive: false });
        canvas.addEventListener('pointerdown', this.onPointerDown);
        canvas.addEventListener('pointermove', this.onPointerMove);
        canvas.addEventListener('pointerup', this.onPointerUp);
        canvas.addEventListener('pointercancel', this.onPointerUp);
        canvas.addEventListener('pointerleave', this.onPointerLeave);
        canvas.addEventListener('dblclick', this.onDoubleClick);
        this.resize();
        this.fitTimeline();
        this.loadAudioImage(true);
        this.loadAudioImage(false);
    }

    async loadAudioImage(source) {
        const previousController = source ? this.sourceAudioImageController : this.languageAudioImageController;
        if (previousController) previousController.abort();
        const controller = new AbortController();
        const generation = source ? ++this.sourceAudioImageGeneration : ++this.languageAudioImageGeneration;
        if (source)
            this.sourceAudioImageController = controller;
        else
            this.languageAudioImageController = controller;
        const url = source ? this.model.sourceAudioUrl : this.model.languageAudioUrl;
        try {
            let image = null;
            if (url) {
                const response = await fetch(url, { signal: controller.signal, cache: 'no-store' });
                if (response.status !== 204) {
                    if (!response.ok) throw new Error(await response.text() || `Audio timeline request failed: ${response.status}`);
                    image = await parseAudioTimelineImage(await response.arrayBuffer());
                }
            }
            const currentGeneration = source ? this.sourceAudioImageGeneration : this.languageAudioImageGeneration;
            if (generation !== currentGeneration) {
                disposeAudioTimelineImage(image);
                return;
            }
            if (source) {
                disposeAudioTimelineImage(this.sourceAudioImage);
                this.sourceAudioImage = image;
            } else {
                disposeAudioTimelineImage(this.languageAudioImage);
                this.languageAudioImage = image;
            }
            this.draw();
        } catch (error) {
            if (error?.name !== 'AbortError') console.warn('EditMap audio timeline unavailable', error);
        }
    }

    update(model, centerPlayhead) {
        const reloadSourceAudio = this.model.sourceAudioUrl !== model.sourceAudioUrl;
        const reloadLanguageAudio = this.model.languageAudioUrl !== model.languageAudioUrl;
        this.model = model;
        this.waveformGain = clampWaveformGain(model.waveformGain);
        if (reloadSourceAudio) {
            disposeAudioTimelineImage(this.sourceAudioImage);
            this.sourceAudioImage = null;
        }
        if (reloadLanguageAudio) {
            disposeAudioTimelineImage(this.languageAudioImage);
            this.languageAudioImage = null;
        }
        this.updateRange();
        if (centerPlayhead) this.center(model.playheadMs);
        this.draw();
        if (reloadSourceAudio) this.loadAudioImage(true);
        if (reloadLanguageAudio) this.loadAudioImage(false);
    }

    fitTimeline() {
        const viewport = Math.max(1, this.host.clientWidth - this.plotLeft);
        this.pixelsPerMs = viewport / (this.model.durationMs || 1);
        this.updateRange();
        this.center(this.model.playheadMs);
        this.draw();
    }

    zoomBy(factor, anchorClientX) {
        const rect = this.canvas.getBoundingClientRect();
        const anchorX = anchorClientX === undefined ? rect.width / 2 : anchorClientX - rect.left;
        const anchorTime = this.timeAtX(anchorX);
        const minimum = Math.max(0.000001, (this.host.clientWidth - this.plotLeft) / Math.max(1, this.model.durationMs));
        this.pixelsPerMs = Math.max(minimum, Math.min(1, this.pixelsPerMs * factor));
        this.updateRange();
        this.host.scrollLeft = Math.max(0, anchorTime * this.pixelsPerMs - Math.max(0, anchorX - this.plotLeft));
        this.draw();
    }

    center(timeMs) {
        this.host.scrollLeft = Math.max(0, timeMs * this.pixelsPerMs - Math.max(1, this.host.clientWidth - this.plotLeft) / 2);
    }

    setWaveformGain(value) {
        this.waveformGain = clampWaveformGain(value);
        this.draw();
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
        const timelineWidth = this.plotLeft + (this.model.durationMs || 1) * Math.max(this.pixelsPerMs, 0.000001);
        const width = Math.max(this.host.clientWidth, Math.min(20000000, Math.ceil(timelineWidth)));
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
        const sourceWaveformColor = cssColor(styles, '--rz-info', primary);
        const languageWaveformColor = cssColor(styles, '--rz-success', primary);
        const danger = cssColor(styles, '--rz-danger', primary);
        const warning = cssColor(styles, '--rz-warning', primary);
        const onDanger = cssColor(styles, '--rz-on-danger', background);
        const onWarning = cssColor(styles, '--rz-on-warning', background);
        const plotLeft = this.plotLeft;
        const navigatorHeight = this.navigatorHeight;
        const rulerTop = navigatorHeight;
        const rulerHeight = this.rulerHeight;
        const trackTop = rulerTop + rulerHeight;
        const laneGap = 4;
        const operationHeight = 18;
        const laneHeight = Math.max(20, Math.floor((height - trackTop - laneGap * 3 - operationHeight) / 2));
        const sourceTop = trackTop + laneGap;
        const languageTop = sourceTop + laneHeight + laneGap;
        const trackBottom = languageTop + laneHeight;
        const operationY = Math.min(height - 11, trackBottom + laneGap + 6);
        ctx.clearRect(0, 0, width, height);
        ctx.fillStyle = background;
        ctx.fillRect(0, 0, width, height);
        const fontFamily = cssColor(styles, '--rz-font-family', 'sans-serif');
        const bodyFontSize = cssColor(styles, '--rz-text-body2-font-size', '12px');
        ctx.font = bodyFontSize + ' ' + fontFamily;
        ctx.textBaseline = 'middle';
        const startMs = this.host.scrollLeft / this.pixelsPerMs;
        const endMs = startMs + Math.max(1, width - plotLeft) / this.pixelsPerMs;
        this.drawNavigator(ctx, width, navigatorHeight, background, border, primary, sourceWaveformColor);
        drawRuler(ctx, width, plotLeft, startMs, endMs, this.pixelsPerMs, rulerTop, rulerHeight, text, border);
        ctx.fillStyle = background;
        ctx.fillRect(plotLeft, sourceTop, width - plotLeft, laneHeight);
        ctx.fillRect(plotLeft, languageTop, width - plotLeft, laneHeight);
        drawTimeGrid(ctx, width, plotLeft, startMs, endMs, this.pixelsPerMs, trackTop, trackBottom, border);
        const spectrogram = this.model.audioMode === 'spectrogram';
        if (spectrogram) {
            drawFrequencyScale(ctx, plotLeft, sourceTop, laneHeight, secondary, fontFamily, this.model.sourceNyquistHz);
            drawFrequencyScale(ctx, plotLeft, languageTop, laneHeight, secondary, fontFamily, this.model.languageNyquistHz);
        } else {
            drawAmplitudeGrid(ctx, plotLeft, width, sourceTop, laneHeight, border, secondary, fontFamily);
            drawAmplitudeGrid(ctx, plotLeft, width, languageTop, laneHeight, border, secondary, fontFamily);
        }
        ctx.strokeStyle = border;
        ctx.strokeRect(plotLeft + 0.5, sourceTop + 0.5, width - plotLeft - 1, laneHeight - 1);
        ctx.strokeRect(plotLeft + 0.5, languageTop + 0.5, width - plotLeft - 1, laneHeight - 1);
        ctx.fillStyle = secondary;
        ctx.fillText(this.model.labels?.source || 'SOURCE', 7, sourceTop + laneHeight / 2);
        ctx.fillText(this.model.labels?.language || 'LANGUAGE', 7, languageTop + laneHeight / 2);
        this.drawAudioImage(this.sourceAudioImage, startMs, endMs, plotLeft, width, sourceTop, laneHeight, sourceWaveformColor, !spectrogram);
        for (const segment of this.model.segments || []) {
            const x1 = this.xAtTime(segment.sourceStartMs);
            const x2 = this.xAtTime(segment.sourceEndMs);
            if (x2 < plotLeft || x1 > width) continue;
            if (segment.kind === 'InsertedGap') {
                ctx.save();
                ctx.globalAlpha = 0.22;
                ctx.fillStyle = warning;
                ctx.fillRect(Math.max(plotLeft, x1), languageTop, Math.max(2, Math.min(width, x2) - Math.max(plotLeft, x1)), laneHeight);
                ctx.restore();
            } else if (segment.kind === 'Mapped') {
                const visibleStart = Math.max(startMs, segment.sourceStartMs);
                const visibleEnd = Math.min(endMs, segment.sourceEndMs);
                if (visibleEnd > visibleStart) {
                    const ratioStart = (visibleStart - segment.sourceStartMs) / Math.max(1, segment.sourceEndMs - segment.sourceStartMs);
                    const ratioEnd = (visibleEnd - segment.sourceStartMs) / Math.max(1, segment.sourceEndMs - segment.sourceStartMs);
                    const languageStart = segment.languageStartMs + (segment.languageEndMs - segment.languageStartMs) * ratioStart;
                    const languageEnd = segment.languageStartMs + (segment.languageEndMs - segment.languageStartMs) * ratioEnd;
                    this.drawAudioImage(this.languageAudioImage, languageStart, languageEnd, this.xAtTime(visibleStart), this.xAtTime(visibleEnd), languageTop, laneHeight, languageWaveformColor, !spectrogram);
                }
            }
        }
        if (!this.languageAudioImage) {
            ctx.strokeStyle = secondary;
            ctx.beginPath();
            ctx.moveTo(plotLeft, languageTop + laneHeight / 2);
            ctx.lineTo(width, languageTop + laneHeight / 2);
            ctx.stroke();
        }
        for (const operation of this.model.operations || []) {
            const temporary = this.drag && this.drag.kind === 'operation' && this.drag.operationIndex === operation.index ? this.drag.timeMs : operation.sourceMs;
            const temporaryDuration = this.drag && this.drag.kind === 'duration' && this.drag.operationIndex === operation.index ? this.drag.durationMs : Math.abs(operation.durationMs);
            const x = this.xAtTime(temporary);
            if (x < plotLeft - 80 || x > width + 80) continue;
            ctx.strokeStyle = operation.selected ? warning : danger;
            ctx.lineWidth = operation.selected ? 3 : 2;
            ctx.beginPath(); ctx.moveTo(x, trackTop); ctx.lineTo(x, height); ctx.stroke();
            ctx.fillStyle = operation.selected ? warning : danger;
            const durationEndX = x + Math.max(8, temporaryDuration * this.pixelsPerMs);
            ctx.fillRect(x, operationY - 2, Math.max(2, durationEndX - x), 4);
            ctx.fillRect(durationEndX - 3, operationY - 6, 6, 12);
            const label = operation.type === 'CUT_SEGMENT' ? `${this.model.labels?.cut || 'CUT'} −${Math.round(temporaryDuration)} ms` : `${this.model.labels?.insert || 'INSERT'} +${Math.round(temporaryDuration)} ms`;
            if (operation.selected) {
                const labelWidth = ctx.measureText(label).width;
                const labelX = Math.max(plotLeft + 3, Math.min(width - labelWidth - 11, x + 5));
                ctx.fillStyle = warning;
                ctx.fillRect(labelX - 4, operationY - 9, labelWidth + 8, 18);
                ctx.fillStyle = onWarning;
                ctx.fillText(label, labelX, operationY);
            } else {
                const compactLabel = operation.type === 'CUT_SEGMENT' ? 'C' : 'I';
                const compactX = Math.max(plotLeft + 2, Math.min(width - 17, x + 3));
                ctx.fillStyle = danger;
                ctx.fillRect(compactX, operationY - 8, 14, 16);
                ctx.fillStyle = onDanger;
                ctx.fillText(compactLabel, compactX + 4, operationY);
            }
        }
        const playheadX = this.xAtTime(this.model.playheadMs);
        ctx.strokeStyle = primary;
        ctx.lineWidth = 2;
        ctx.beginPath(); ctx.moveTo(playheadX, rulerTop); ctx.lineTo(playheadX, height); ctx.stroke();
        ctx.fillStyle = primary;
        ctx.beginPath(); ctx.moveTo(playheadX - 5, trackTop); ctx.lineTo(playheadX + 5, trackTop); ctx.lineTo(playheadX, trackTop + 7); ctx.closePath(); ctx.fill();
        if (this.hoverX !== null && !this.drag) {
            const hoverTime = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(this.hoverX)));
            const label = formatTimelineTime(hoverTime);
            ctx.fillStyle = text;
            ctx.fillText(label, Math.min(width - 85, this.hoverX + 8), height - 10);
        }
        ctx.lineWidth = 1;
    }

    drawNavigator(ctx, width, height, background, border, primary, waveformColor) {
        const left = this.plotLeft;
        const availableWidth = Math.max(1, width - left);
        const duration = Math.max(1, this.model.durationMs || 1);
        ctx.save();
        ctx.fillStyle = background;
        ctx.fillRect(left, 1, availableWidth, height - 3);
        this.drawAudioImage(this.sourceAudioImage, 0, duration, left, width, 2, height - 4, waveformColor, this.model.audioMode !== 'spectrogram', 1.0);
        const geometry = this.navigatorGeometry();
        ctx.globalAlpha = 0.58;
        ctx.fillStyle = background;
        ctx.fillRect(left, 1, Math.max(0, geometry.left - left), height - 3);
        ctx.fillRect(geometry.right, 1, Math.max(0, width - geometry.right), height - 3);
        ctx.globalAlpha = 1;
        ctx.strokeStyle = primary;
        ctx.lineWidth = 2;
        ctx.strokeRect(geometry.left + 1, 1, Math.max(1, geometry.right - geometry.left - 2), height - 3);
        ctx.fillStyle = primary;
        ctx.fillRect(geometry.left, 1, 4, height - 3);
        ctx.fillRect(geometry.right - 4, 1, 4, height - 3);
        ctx.restore();
        ctx.strokeStyle = border;
        ctx.beginPath();
        ctx.moveTo(left, height - 0.5);
        ctx.lineTo(width, height - 0.5);
        ctx.stroke();
    }

    navigatorGeometry() {
        const left = this.plotLeft;
        const width = Math.max(1, this.host.clientWidth - left);
        const duration = Math.max(1, this.model.durationMs || 1);
        const startMs = Math.max(0, this.host.scrollLeft / this.pixelsPerMs);
        const viewDurationMs = Math.min(duration, width / this.pixelsPerMs);
        const endMs = Math.min(duration, startMs + viewDurationMs);
        return {
            left: left + startMs / duration * width,
            right: left + endMs / duration * width,
            startMs,
            endMs,
            viewDurationMs
        };
    }

    navigatorTimeAtX(x) {
        const width = Math.max(1, this.host.clientWidth - this.plotLeft);
        const ratio = Math.max(0, Math.min(1, (x - this.plotLeft) / width));
        return ratio * Math.max(1, this.model.durationMs || 1);
    }

    applyViewport(startMs, endMs) {
        const duration = Math.max(1, this.model.durationMs || 1);
        const viewportWidth = Math.max(1, this.host.clientWidth - this.plotLeft);
        const minimumDuration = viewportWidth;
        let clampedStart = Math.max(0, Math.min(duration, startMs));
        let clampedEnd = Math.max(clampedStart, Math.min(duration, endMs));
        if (clampedEnd - clampedStart < minimumDuration) {
            if (clampedStart === 0)
                clampedEnd = Math.min(duration, minimumDuration);
            else
                clampedStart = Math.max(0, clampedEnd - minimumDuration);
        }
        const viewDuration = Math.max(1, clampedEnd - clampedStart);
        this.pixelsPerMs = Math.min(1, viewportWidth / viewDuration);
        this.updateRange();
        this.host.scrollLeft = clampedStart * this.pixelsPerMs;
        this.draw();
    }

    beginNavigatorDrag(x, pointerId) {
        const geometry = this.navigatorGeometry();
        const timeMs = this.navigatorTimeAtX(x);
        const edgeTolerance = 9;
        if (Math.abs(x - geometry.left) <= edgeTolerance) {
            this.drag = { kind: 'navigator-left', fixedEndMs: geometry.endMs, pointerId };
            return;
        }
        if (Math.abs(x - geometry.right) <= edgeTolerance) {
            this.drag = { kind: 'navigator-right', fixedStartMs: geometry.startMs, pointerId };
            return;
        }
        const offsetMs = x >= geometry.left && x <= geometry.right ? timeMs - geometry.startMs : geometry.viewDurationMs / 2;
        this.drag = { kind: 'navigator-pan', viewDurationMs: geometry.viewDurationMs, offsetMs, pointerId };
        if (x < geometry.left || x > geometry.right)
            this.panNavigator(timeMs, this.drag);
    }

    panNavigator(timeMs, drag) {
        const duration = Math.max(1, this.model.durationMs || 1);
        const startMs = Math.max(0, Math.min(duration - drag.viewDurationMs, timeMs - drag.offsetMs));
        this.host.scrollLeft = startMs * this.pixelsPerMs;
        this.draw();
    }

    drawAudioImage(image, mediaStartMs, mediaEndMs, destinationStartX, destinationEndX, top, height, color, tint, gain) {
        if (!image || mediaEndMs <= mediaStartMs || destinationEndX <= destinationStartX) {
            this.context.strokeStyle = color;
            this.context.globalAlpha = 0.35;
            this.context.beginPath();
            this.context.moveTo(destinationStartX, top + height / 2);
            this.context.lineTo(destinationEndX, top + height / 2);
            this.context.stroke();
            this.context.globalAlpha = 1;
            return;
        }
        const relativeStart = mediaStartMs - image.originMs;
        const relativeEnd = mediaEndMs - image.originMs;
        const span = relativeEnd - relativeStart;
        const targetWidth = Math.max(1, Math.ceil(destinationEndX - destinationStartX));
        const targetHeight = Math.max(1, Math.ceil(height - 6));
        const waveformGain = tint ? (gain === undefined ? this.waveformGain : clampWaveformGain(gain)) : 1.0;
        const sourceHeight = image.tileHeight / waveformGain;
        const sourceY = (image.tileHeight - sourceHeight) / 2;
        if (!image.scratch) image.scratch = document.createElement('canvas');
        image.scratch.width = targetWidth;
        image.scratch.height = targetHeight;
        const scratch = image.scratch.getContext('2d');
        scratch.clearRect(0, 0, targetWidth, targetHeight);
        for (let index = Math.max(0, Math.floor(relativeStart / image.tileDurationMs)); index < image.tiles.length; index++) {
            const tileStart = index * image.tileDurationMs;
            const tileEnd = tileStart + image.tileDurationMs;
            const visibleStart = Math.max(relativeStart, tileStart);
            const visibleEnd = Math.min(relativeEnd, tileEnd);
            if (visibleEnd <= visibleStart) {
                if (tileStart >= relativeEnd) break;
                continue;
            }
            const sourceX = (visibleStart - tileStart) / image.millisecondsPerPixel;
            const sourceWidth = Math.max(1, (visibleEnd - visibleStart) / image.millisecondsPerPixel);
            const destinationX = destinationStartX + (visibleStart - relativeStart) / span * (destinationEndX - destinationStartX);
            const destinationWidth = (visibleEnd - visibleStart) / span * (destinationEndX - destinationStartX);
            scratch.drawImage(image.tiles[index], sourceX, sourceY, sourceWidth, sourceHeight, destinationX - destinationStartX, 0, destinationWidth, targetHeight);
        }
        if (tint) {
            scratch.globalCompositeOperation = 'source-in';
            scratch.fillStyle = color;
            scratch.fillRect(0, 0, targetWidth, targetHeight);
            scratch.globalCompositeOperation = 'source-over';
        }
        this.context.save();
        this.context.globalAlpha = 1;
        this.context.drawImage(image.scratch, destinationStartX, top + 3);
        this.context.restore();
    }

    xAtTime(timeMs) {
        return this.plotLeft + timeMs * this.pixelsPerMs - this.host.scrollLeft;
    }

    timeAtX(x) {
        return (this.host.scrollLeft + Math.max(0, x - this.plotLeft)) / this.pixelsPerMs;
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
        if (Math.abs(event.deltaX) > Math.abs(event.deltaY) && !event.ctrlKey && !event.metaKey)
            this.host.scrollLeft += event.deltaX;
        else
            this.zoomBy(Math.exp(-event.deltaY * 0.0025), event.clientX);
    }

    handlePointerDown(event) {
        const rect = this.canvas.getBoundingClientRect();
        const x = event.clientX - rect.left;
        const y = event.clientY - rect.top;
        this.canvas.setPointerCapture(event.pointerId);
        if (y <= this.navigatorHeight) {
            this.beginNavigatorDrag(x, event.pointerId);
            this.draw();
            return;
        }
        const hit = this.hitOperation(x);
        if (hit) {
            const operation = hit.operation;
            this.drag = hit.kind === 'duration'
                ? { kind: 'duration', operationIndex: operation.index, sourceMs: operation.sourceMs, durationMs: Math.abs(operation.durationMs), pointerId: event.pointerId }
                : { kind: 'operation', operationIndex: operation.index, timeMs: operation.sourceMs, pointerId: event.pointerId };
            this.drag.selectionPromise = this.dotNetReference.invokeMethodAsync('OnTimelineOperationSelected', operation.index);
        } else if (y < this.navigatorHeight + this.rulerHeight) {
            this.drag = { kind: 'pan', startX: event.clientX, startScroll: this.host.scrollLeft, pointerId: event.pointerId };
        } else {
            this.drag = { kind: 'seek', timeMs: this.timeAtX(x), pointerId: event.pointerId };
        }
    }

    handlePointerMove(event) {
        const rect = this.canvas.getBoundingClientRect();
        const x = event.clientX - rect.left;
        const y = event.clientY - rect.top;
        this.hoverX = x;
        if (!this.drag) {
            if (y <= this.navigatorHeight) {
                const geometry = this.navigatorGeometry();
                this.canvas.style.cursor = Math.abs(x - geometry.left) <= 9 || Math.abs(x - geometry.right) <= 9 ? 'ew-resize' : 'grab';
            } else {
                this.canvas.style.cursor = 'default';
            }
            this.draw();
            return;
        }
        if (this.drag.kind === 'navigator-left') {
            this.applyViewport(this.navigatorTimeAtX(x), this.drag.fixedEndMs);
        } else if (this.drag.kind === 'navigator-right') {
            this.applyViewport(this.drag.fixedStartMs, this.navigatorTimeAtX(x));
        } else if (this.drag.kind === 'navigator-pan') {
            this.panNavigator(this.navigatorTimeAtX(x), this.drag);
        } else if (this.drag.kind === 'pan') {
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
        this.canvas.style.cursor = 'default';
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
        this.sourceAudioImageGeneration++;
        this.languageAudioImageGeneration++;
        if (this.sourceAudioImageController) this.sourceAudioImageController.abort();
        if (this.languageAudioImageController) this.languageAudioImageController.abort();
        disposeAudioTimelineImage(this.sourceAudioImage);
        disposeAudioTimelineImage(this.languageAudioImage);
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

function clampWaveformGain(value) {
    const numeric = Number(value);
    return Number.isFinite(numeric) ? Math.max(1, Math.min(12, numeric)) : 3;
}

function timelineGridStep(pixelsPerMs) {
    const candidates = [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 30000, 60000, 120000, 300000, 600000];
    let step = candidates[candidates.length - 1];
    for (const candidate of candidates) {
        if (candidate * pixelsPerMs >= 105) { step = candidate; break; }
    }
    return step;
}

function drawRuler(context, width, left, startMs, endMs, pixelsPerMs, top, height, color, border) {
    const step = timelineGridStep(pixelsPerMs);
    const first = Math.floor(startMs / step) * step;
    context.strokeStyle = border;
    context.fillStyle = color;
    context.beginPath();
    for (let time = first; time <= endMs + step; time += step) {
        const x = left + (time - startMs) * pixelsPerMs;
        if (x < left || x > width) continue;
        context.moveTo(x, top + height - 10);
        context.lineTo(x, top + height - 1);
        context.fillText(formatTimelineTime(time), x + 3, top + 9);
    }
    context.stroke();
}

function drawTimeGrid(context, width, left, startMs, endMs, pixelsPerMs, top, bottom, color) {
    const majorStep = timelineGridStep(pixelsPerMs);
    const minorStep = majorStep / 5;
    const firstMinor = Math.floor(startMs / minorStep) * minorStep;
    context.save();
    context.strokeStyle = color;
    context.lineWidth = 1;
    for (let time = firstMinor; time <= endMs + minorStep; time += minorStep) {
        const x = left + (time - startMs) * pixelsPerMs;
        if (x < left || x > width) continue;
        const major = Math.abs(time / majorStep - Math.round(time / majorStep)) < 0.0001;
        context.globalAlpha = major ? 0.55 : 0.22;
        context.beginPath();
        context.moveTo(Math.round(x) + 0.5, top);
        context.lineTo(Math.round(x) + 0.5, bottom);
        context.stroke();
    }
    context.restore();
}

function drawAmplitudeGrid(context, left, width, top, height, color, labelColor, fontFamily) {
    const center = top + height / 2;
    context.save();
    context.strokeStyle = color;
    context.fillStyle = labelColor;
    context.font = `9px ${fontFamily}`;
    context.lineWidth = 1;
    context.textAlign = 'right';
    context.globalAlpha = 0.5;
    context.beginPath();
    context.moveTo(left, Math.round(top) + 0.5);
    context.lineTo(width, Math.round(top) + 0.5);
    context.moveTo(left, Math.round(center) + 0.5);
    context.lineTo(width, Math.round(center) + 0.5);
    context.moveTo(left, Math.round(top + height) - 0.5);
    context.lineTo(width, Math.round(top + height) - 0.5);
    context.stroke();
    context.globalAlpha = 1;
    context.fillText('0 dB', left - 5, top + 7);
    context.fillText('-∞', left - 5, center);
    context.fillText('0 dB', left - 5, top + height - 7);
    context.textAlign = 'start';
    context.restore();
}

function drawFrequencyScale(context, left, top, height, labelColor, fontFamily, nyquistHz) {
    context.save();
    context.fillStyle = labelColor;
    context.font = `9px ${fontFamily}`;
    context.textAlign = 'right';
    context.fillText(formatFrequency(nyquistHz), left - 5, top + 7);
    context.fillText('0 Hz', left - 5, top + height - 7);
    context.textAlign = 'start';
    context.restore();
}

function formatFrequency(hertz) {
    const value = Number(hertz);
    if (!Number.isFinite(value) || value <= 0) return '— Hz';
    if (value >= 1000) {
        const kilohertz = value / 1000;
        return `${kilohertz.toFixed(Number.isInteger(kilohertz) ? 0 : 2).replace(/\.?0+$/, '')} kHz`;
    }
    return `${Math.round(value)} Hz`;
}

async function parseAudioTimelineImage(buffer) {
    const view = new DataView(buffer);
    if (view.byteLength < 40 || view.getUint8(0) !== 82 || view.getUint8(1) !== 70 || view.getUint8(2) !== 65 || view.getUint8(3) !== 49)
        throw new Error('Invalid audio timeline payload');
    let offset = 4;
    const tileWidth = view.getInt32(offset, true); offset += 4;
    const tileHeight = view.getInt32(offset, true); offset += 4;
    const millisecondsPerPixel = view.getFloat64(offset, true); offset += 8;
    const tileDurationMs = view.getFloat64(offset, true); offset += 8;
    const originMs = view.getFloat64(offset, true); offset += 8;
    const count = view.getInt32(offset, true); offset += 4;
    if (tileWidth < 1 || tileHeight < 1 || count < 1 || count > 64)
        throw new Error('Invalid audio timeline metadata');
    const tiles = [];
    try {
        for (let index = 0; index < count; index++) {
            if (offset + 4 > view.byteLength) throw new Error('Truncated audio timeline payload');
            const length = view.getInt32(offset, true); offset += 4;
            if (length < 1 || offset + length > view.byteLength) throw new Error('Truncated audio timeline tile');
            const blob = new Blob([buffer.slice(offset, offset + length)], { type: 'image/png' });
            tiles.push(await createImageBitmap(blob));
            offset += length;
        }
    } catch (error) {
        for (const tile of tiles) tile.close();
        throw error;
    }
    return { tileWidth, tileHeight, millisecondsPerPixel, tileDurationMs, originMs, tiles };
}

function disposeAudioTimelineImage(image) {
    for (const tile of image?.tiles || []) tile.close();
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
