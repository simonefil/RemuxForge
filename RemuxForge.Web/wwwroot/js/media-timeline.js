// Componenti condivisi degli editor visuali: rendering WebGL dei fotogrammi grezzi,
// anteprime, macchina generica della timeline su canvas e utilita' di disegno.
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

/**
 * Anteprima di un singolo fotogramma su un canvas WebGL: prepara l'URL con le dimensioni
 * correnti, scarica la finestra di fotogrammi riusando la cache e disegna. Le tre fasi sono
 * separate perché chi mostra più anteprime insieme deve aggiornarle in un colpo solo.
 */
export function createFramePreview(canvas) {
    fitPreviewCanvas(canvas);
    const renderer = new RawFrameRenderer(canvas);
    const cache = new Map();
    const resizeObserver = observePreviewCanvas(canvas, renderer);
    let lastIndex = null;
    let generation = 0;
    let controller = null;
    const preview = {
        renderer,
        /** Aggiunge le dimensioni correnti del canvas all'URL e ne estrae l'indice di fotogramma. */
        prepare(url) {
            if (!url) return null;
            const sizedUrl = withCanvasSize(url, renderer.canvas);
            return { sizedUrl, index: parseFrameUrl(sizedUrl).index };
        },
        /** Scarica la finestra attorno al fotogramma richiesto, riusando la cache di questa anteprima. */
        fetchWindow(sizedUrl, signal) {
            return fetchRawFrameWindow(sizedUrl, lastIndex, cache, signal);
        },
        /** Disegna il fotogramma scaricato e registra l'indice come partenza della prossima finestra. */
        commit(index, frame) {
            lastIndex = index;
            if (frame) renderer.render(frame.buffer, frame.metadata);
            else renderer.clear();
        },
        /** Scarica e disegna un fotogramma, per le anteprime che non vanno coordinate con altre. */
        async load(url) {
            generation++;
            const current = generation;
            if (controller) controller.abort();
            controller = new AbortController();
            const request = preview.prepare(url);
            const frame = request ? await preview.fetchWindow(request.sizedUrl, controller.signal) : null;
            if (current !== generation) return false;
            preview.commit(request ? request.index : null, frame);
            return true;
        },
        cancel() {
            generation++;
            if (controller) controller.abort();
            controller = null;
        },
        dispose() {
            preview.cancel();
            cache.clear();
            resizeObserver.disconnect();
            renderer.dispose();
        }
    };
    return preview;
}

/** Coppia di anteprime aggiornate insieme: il disegno avviene solo quando entrambe le richieste sono concluse. */
export function createPreviewPair(sourceCanvas, languageCanvas) {
    const source = createFramePreview(sourceCanvas);
    const language = createFramePreview(languageCanvas);
    let generation = 0;
    let controller = null;
    let resizeTimer = null;
    let loadedSize = '';
    const currentSize = () => `${sourceCanvas.parentElement?.clientWidth || 0}x${sourceCanvas.parentElement?.clientHeight || 0}|${languageCanvas.parentElement?.clientWidth || 0}x${languageCanvas.parentElement?.clientHeight || 0}`;
    const pair = {
        async loadPair(sourceUrl, languageUrl) {
            sourceUrl = sourceUrl || null;
            languageUrl = languageUrl || null;
            pair.sourceUrl = sourceUrl;
            pair.languageUrl = languageUrl;
            generation++;
            const current = generation;
            if (controller) controller.abort();
            controller = new AbortController();
            const sourceRequest = source.prepare(sourceUrl);
            const languageRequest = language.prepare(languageUrl);
            const results = await Promise.allSettled([
                sourceRequest ? source.fetchWindow(sourceRequest.sizedUrl, controller.signal) : Promise.resolve(null),
                languageRequest ? language.fetchWindow(languageRequest.sizedUrl, controller.signal) : Promise.resolve(null)
            ]);
            if (current !== generation) return false;
            if (results[0].status === 'rejected') throw new Error(`Source: ${results[0].reason}`);
            if (results[1].status === 'rejected') throw new Error(`Language: ${results[1].reason}`);
            source.commit(sourceRequest ? sourceRequest.index : null, results[0].value);
            language.commit(languageRequest ? languageRequest.index : null, results[1].value);
            loadedSize = currentSize();
            return true;
        },
        cancel() {
            generation++;
            if (controller) controller.abort();
            controller = null;
        },
        dispose() {
            this.cancel();
            if (resizeTimer) clearTimeout(resizeTimer);
            resizeObserver.disconnect();
            source.dispose();
            language.dispose();
        }
    };
    const resizeObserver = new ResizeObserver(() => {
        if (resizeTimer) clearTimeout(resizeTimer);
        resizeTimer = setTimeout(() => {
            resizeTimer = null;
            if (loadedSize !== currentSize() && (pair.sourceUrl || pair.languageUrl))
                pair.loadPair(pair.sourceUrl, pair.languageUrl).catch(error => { if (error?.name !== 'AbortError') console.warn('Preview resize failed', error); });
        }, 160);
    });
    const sourceStage = sourceCanvas.parentElement?.parentElement;
    const languageStage = languageCanvas.parentElement?.parentElement;
    if (sourceStage) resizeObserver.observe(sourceStage);
    if (languageStage && languageStage !== sourceStage) resizeObserver.observe(languageStage);
    return pair;
}


/**
 * Macchina generica di una timeline su canvas: viewport e zoom, navigator, righello,
 * immagini audio, osservatori di tema e dimensione, playhead e lettura dell'ora sotto il puntatore.
 * Il dominio vive nelle sottoclassi tramite drawContent, hitContent, onContentDragMove e onContentDragEnd.
 */
export class TimelineCanvas {
    constructor(host, canvas, dotNetReference, model, options) {
        const settings = options || {};
        this.host = host;
        this.canvas = canvas;
        this.context = canvas.getContext('2d');
        this.dotNetReference = dotNetReference;
        this.model = model;
        this.plotLeft = settings.plotLeft === undefined ? 112 : settings.plotLeft;
        this.compactNavigatorHeight = settings.navigatorHeight === undefined ? 22 : settings.navigatorHeight;
        this.navigatorHeight = model.precisionMode ? 48 : this.compactNavigatorHeight;
        this.rulerHeight = settings.rulerHeight === undefined ? 24 : settings.rulerHeight;
        this.navigatorAudioKey = settings.navigatorAudioKey || 'source';
        this.waveformGain = clampWaveformGain(model.waveformGain);
        this.audioImages = new Map();
        this.audioControllers = new Map();
        this.audioGenerations = new Map();
        this.range = host.querySelector(settings.rangeSelector || '.timeline-scroll-range');
        this.pixelsPerMs = Math.max(0.000001, Math.max(1, host.clientWidth - this.plotLeft) / Math.max(1, model.durationMs));
        this.fitToViewport = true;
        this.drag = null;
        this.hoverX = null;
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
    }

    /** Avvia il primo layout e il caricamento delle immagini audio; le sottoclassi la chiamano a fine costruttore. */
    start() {
        this.resize();
        this.fitTimeline();
        const urls = this.audioUrls(this.model);
        for (const key of Object.keys(urls)) this.loadAudioImage(key, urls[key]);
    }

    /** URL delle tracce audio da disegnare, per chiave di lane. */
    audioUrls(model) {
        return {};
    }

    async loadAudioImage(key, url) {
        const previousController = this.audioControllers.get(key);
        if (previousController) previousController.abort();
        const controller = new AbortController();
        const generation = (this.audioGenerations.get(key) || 0) + 1;
        this.audioGenerations.set(key, generation);
        this.audioControllers.set(key, controller);
        try {
            let image = null;
            if (url) {
                const response = await fetch(url, { signal: controller.signal, cache: 'no-store' });
                if (response.status !== 204) {
                    if (!response.ok) throw new Error(await response.text() || `Audio timeline request failed: ${response.status}`);
                    image = await parseAudioTimelineImage(await response.arrayBuffer());
                }
            }
            if (generation !== this.audioGenerations.get(key)) {
                disposeAudioTimelineImage(image);
                return;
            }
            disposeAudioTimelineImage(this.audioImages.get(key));
            this.audioImages.set(key, image);
            this.draw();
        } catch (error) {
            if (error?.name !== 'AbortError') console.warn('Audio timeline unavailable', error);
        }
    }

    update(model, centerPlayhead) {
        const previousUrls = this.audioUrls(this.model);
        const nextUrls = this.audioUrls(model);
        const reload = [];
        for (const key of Object.keys(nextUrls)) {
            if (previousUrls[key] !== nextUrls[key]) reload.push(key);
        }
        this.model = model;
        this.navigatorHeight = model.precisionMode ? 48 : this.compactNavigatorHeight;
        this.waveformGain = clampWaveformGain(model.waveformGain);
        for (const key of reload) {
            disposeAudioTimelineImage(this.audioImages.get(key));
            this.audioImages.set(key, null);
        }
        this.constrainScale();
        this.updateRange();
        if (centerPlayhead) this.center(model.playheadMs);
        this.draw();
        for (const key of reload) this.loadAudioImage(key, nextUrls[key]);
    }

    fitTimeline() {
        this.fitToViewport = true;
        this.pixelsPerMs = this.minimumPixelsPerMs();
        this.updateRange();
        this.center(this.model.playheadMs);
        this.draw();
    }

    zoomBy(factor, anchorClientX) {
        const rect = this.canvas.getBoundingClientRect();
        const anchorX = anchorClientX === undefined ? rect.width / 2 : anchorClientX - rect.left;
        const anchorTime = this.timeAtX(anchorX);
        const minimum = this.minimumPixelsPerMs();
        this.pixelsPerMs = Math.max(minimum, Math.min(1, this.pixelsPerMs * factor));
        this.fitToViewport = this.pixelsPerMs <= minimum * 1.000001;
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
        this.constrainScale();
        this.updateRange();
        this.draw();
    }

    minimumPixelsPerMs() {
        return Math.max(0.000001, Math.max(1, this.host.clientWidth - this.plotLeft) / Math.max(1, this.model.durationMs));
    }

    constrainScale() {
        const minimum = this.minimumPixelsPerMs();
        if (this.fitToViewport || this.pixelsPerMs < minimum) {
            this.pixelsPerMs = minimum;
            this.fitToViewport = true;
            this.host.scrollLeft = 0;
        }
    }

    updateRange() {
        const timelineWidth = this.plotLeft + (this.model.durationMs || 1) * Math.max(this.pixelsPerMs, 0.000001);
        const width = Math.max(this.host.clientWidth, Math.min(20000000, Math.ceil(timelineWidth)));
        this.range.style.width = `${width}px`;
    }

    /** Tavolozza letta dalle variabili CSS del tema Radzen. */
    palette(styles) {
        const background = cssColor(styles, '--rz-base-background-color', styles.backgroundColor);
        const text = cssColor(styles, '--rz-text-color', styles.color);
        const secondary = cssColor(styles, '--rz-text-secondary-color', text);
        const primary = cssColor(styles, '--rz-primary', text);
        return {
            background,
            text,
            secondary,
            primary,
            border: cssColor(styles, '--rz-border-color', secondary),
            info: cssColor(styles, '--rz-info', primary),
            success: cssColor(styles, '--rz-success', primary),
            danger: cssColor(styles, '--rz-danger', primary),
            warning: cssColor(styles, '--rz-warning', primary),
            onDanger: cssColor(styles, '--rz-on-danger', background),
            onWarning: cssColor(styles, '--rz-on-warning', background),
            fontFamily: cssColor(styles, '--rz-font-family', 'sans-serif')
        };
    }

    draw() {
        const ctx = this.context;
        const width = this.host.clientWidth;
        const height = Math.max(1, this.host.clientHeight - 16);
        if (width <= 0 || height <= 0) return;
        const styles = getComputedStyle(this.host);
        const colors = this.palette(styles);
        const plotLeft = this.plotLeft;
        const rulerTop = this.navigatorHeight;
        const trackTop = rulerTop + this.rulerHeight;
        ctx.clearRect(0, 0, width, height);
        ctx.fillStyle = colors.background;
        ctx.fillRect(0, 0, width, height);
        const bodyFontSize = cssColor(styles, '--rz-text-body2-font-size', '12px');
        ctx.font = bodyFontSize + ' ' + colors.fontFamily;
        ctx.textBaseline = 'middle';
        const startMs = this.host.scrollLeft / this.pixelsPerMs;
        const endMs = startMs + Math.max(1, width - plotLeft) / this.pixelsPerMs;
        this.drawNavigator(ctx, width, this.navigatorHeight, colors.background, colors.border, colors.primary, colors.info);
        drawRuler(ctx, width, plotLeft, startMs, endMs, this.pixelsPerMs, rulerTop, this.rulerHeight, colors.text, colors.border);
        this.drawContent(ctx, { width, height, plotLeft, trackTop, startMs, endMs }, colors, styles);
        const playheadX = this.xAtTime(this.model.playheadMs);
        ctx.strokeStyle = colors.primary;
        ctx.lineWidth = 2;
        ctx.beginPath(); ctx.moveTo(playheadX, rulerTop); ctx.lineTo(playheadX, height); ctx.stroke();
        ctx.fillStyle = colors.primary;
        ctx.beginPath(); ctx.moveTo(playheadX - 5, trackTop); ctx.lineTo(playheadX + 5, trackTop); ctx.lineTo(playheadX, trackTop + 7); ctx.closePath(); ctx.fill();
        if (this.hoverX !== null && !this.drag) {
            const hoverTime = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(this.hoverX)));
            const hoverText = formatTimelineTime(hoverTime);
            const hoverWidth = ctx.measureText(hoverText).width + 8;
            const hoverX = Math.max(plotLeft + 2, Math.min(width - hoverWidth - 2, this.hoverX + 8));
            ctx.fillStyle = colors.background;
            ctx.fillRect(hoverX, rulerTop + 2, hoverWidth, this.rulerHeight - 4);
            ctx.fillStyle = colors.text;
            ctx.fillText(hoverText, hoverX + 4, rulerTop + this.rulerHeight / 2);
        }
        ctx.lineWidth = 1;
    }

    /** Disegna il contenuto di dominio fra righello e playhead. */
    drawContent(ctx, layout, colors, styles) {
    }

    drawNavigator(ctx, width, height, background, border, primary, waveformColor) {
        const left = this.plotLeft;
        const availableWidth = Math.max(1, width - left);
        const duration = Math.max(1, this.model.durationMs || 1);
        ctx.save();
        ctx.fillStyle = background;
        ctx.fillRect(left, 1, availableWidth, height - 3);
        const navigatorImage = this.audioImages.get(this.navigatorAudioKey);
        const navigatorGain = navigatorImage?.kind === 'waveform' && navigatorImage.peak > 0 ? 32767 / navigatorImage.peak : 1.0;
        this.drawAudioImage(navigatorImage, 0, duration, left, width, 2, height - 4, waveformColor, this.model.audioMode !== 'spectrogram', navigatorGain);
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
        this.fitToViewport = viewDuration >= duration - 1;
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
        if (image.kind === 'waveform') {
            this.drawAudioEnvelope(image, mediaStartMs, mediaEndMs, destinationStartX, destinationEndX, top, height, color, gain === undefined ? this.waveformGain : gain);
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

    drawAudioEnvelope(envelope, mediaStartMs, mediaEndMs, destinationStartX, destinationEndX, top, height, color, gain) {
        const envelopeEndMs = envelope.originMs + envelope.levels[0].minimum.length * envelope.millisecondsPerPoint;
        const visibleStartMs = Math.max(mediaStartMs, envelope.originMs);
        const visibleEndMs = Math.min(mediaEndMs, envelopeEndMs);
        if (visibleEndMs <= visibleStartMs) return;
        const mediaSpanMs = mediaEndMs - mediaStartMs;
        const destinationSpan = destinationEndX - destinationStartX;
        const visibleDestinationStartX = destinationStartX + (visibleStartMs - mediaStartMs) / mediaSpanMs * destinationSpan;
        const visibleDestinationEndX = destinationStartX + (visibleEndMs - mediaStartMs) / mediaSpanMs * destinationSpan;
        let level = envelope.levels[0];
        let levelStepMs = envelope.millisecondsPerPoint;
        const requestedIndexesPerPixel = Math.max(0.000001, (visibleEndMs - visibleStartMs) / envelope.millisecondsPerPoint / Math.max(1, visibleDestinationEndX - visibleDestinationStartX));
        let levelIndex = 0;
        while (requestedIndexesPerPixel / (1 << levelIndex) > 2 && levelIndex + 1 < envelope.levels.length) {
            levelIndex++;
            level = envelope.levels[levelIndex];
            levelStepMs *= 2;
        }
        const relativeStartMs = visibleStartMs - envelope.originMs;
        const relativeEndMs = visibleEndMs - envelope.originMs;
        const destinationWidth = Math.max(1, Math.ceil(visibleDestinationEndX - visibleDestinationStartX));
        const firstIndex = Math.max(0, Math.floor(relativeStartMs / levelStepMs));
        const lastIndex = Math.min(level.minimum.length, Math.ceil(relativeEndMs / levelStepMs));
        if (lastIndex <= firstIndex) return;
        const centerY = top + height / 2;
        const amplitudeScale = Math.max(1, height - 4) / 2 * Math.max(0.000001, gain) / 32767;
        const indexesPerPixel = Math.max(0.000001, (lastIndex - firstIndex) / destinationWidth);
        this.context.save();
        this.context.strokeStyle = color;
        this.context.globalAlpha = 0.92;
        this.context.lineWidth = 1;
        this.context.beginPath();
        for (let pixel = 0; pixel < destinationWidth; pixel++) {
            const bucketStart = Math.min(lastIndex - 1, firstIndex + Math.floor(pixel * indexesPerPixel));
            const bucketEnd = Math.min(lastIndex, Math.max(bucketStart + 1, firstIndex + Math.ceil((pixel + 1) * indexesPerPixel)));
            let minimum = 32767;
            let maximum = -32767;
            for (let index = bucketStart; index < bucketEnd; index++) {
                minimum = Math.min(minimum, level.minimum[index]);
                maximum = Math.max(maximum, level.maximum[index]);
            }
            const x = visibleDestinationStartX + pixel + 0.5;
            this.context.moveTo(x, Math.max(top + 1, centerY - maximum * amplitudeScale));
            this.context.lineTo(x, Math.min(top + height - 1, centerY - minimum * amplitudeScale));
        }
        this.context.stroke();
        this.context.restore();
    }

    xAtTime(timeMs) {
        return this.plotLeft + timeMs * this.pixelsPerMs - this.host.scrollLeft;
    }

    timeAtX(x) {
        return (this.host.scrollLeft + Math.max(0, x - this.plotLeft)) / this.pixelsPerMs;
    }

    handleWheel(event) {
        event.preventDefault();
        if (Math.abs(event.deltaX) > Math.abs(event.deltaY) && !event.ctrlKey && !event.metaKey)
            this.host.scrollLeft += event.deltaX;
        else
            this.zoomBy(Math.exp(-event.deltaY * 0.0025), event.clientX);
    }

    /** Restituisce l'oggetto di drag di dominio sotto il puntatore, oppure null. */
    hitContent(x, y, event) {
        return null;
    }

    /** Restituisce il cursore richiesto dal contenuto di dominio sotto il puntatore. */
    contentCursor(x, y) {
        return 'default';
    }

    /** Aggiorna un drag di dominio in corso. */
    onContentDragMove(drag, x, y) {
    }

    /** Conclude un drag di dominio. */
    onContentDragEnd(drag) {
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
        const contentDrag = this.hitContent(x, y, event);
        if (contentDrag) {
            this.drag = contentDrag;
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
                this.canvas.style.cursor = this.contentCursor(x, y);
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
        } else if (this.drag.kind === 'seek') {
            this.drag.timeMs = Math.max(0, Math.min(this.model.durationMs, this.timeAtX(x)));
            this.draw();
        } else {
            this.onContentDragMove(this.drag, x, y);
        }
    }

    handlePointerUp(event) {
        if (!this.drag) return;
        const drag = this.drag;
        this.drag = null;
        this.canvas.style.cursor = 'default';
        if (drag.kind === 'seek') this.dotNetReference.invokeMethodAsync('OnTimelineSeek', drag.timeMs);
        else if (drag.kind !== 'pan' && !drag.kind.startsWith('navigator')) this.onContentDragEnd(drag);
        try { this.canvas.releasePointerCapture(event.pointerId); } catch { }
        this.draw();
    }

    dispose() {
        for (const key of this.audioGenerations.keys()) this.audioGenerations.set(key, this.audioGenerations.get(key) + 1);
        for (const controller of this.audioControllers.values()) controller.abort();
        for (const image of this.audioImages.values()) disposeAudioTimelineImage(image);
        this.audioImages.clear();
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

export function cssColor(styles, variable, fallback) {
    const value = styles.getPropertyValue(variable).trim();
    return value || fallback;
}

export function clampWaveformGain(value) {
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

export function drawRuler(context, width, left, startMs, endMs, pixelsPerMs, top, height, color, border) {
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

export function drawTimeGrid(context, width, left, startMs, endMs, pixelsPerMs, top, bottom, color) {
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

export function drawAmplitudeGrid(context, left, width, top, height, color, labelColor, fontFamily, labelFontSize = 9) {
    const center = top + height / 2;
    context.save();
    context.strokeStyle = color;
    context.fillStyle = labelColor;
    context.font = `${labelFontSize}px ${fontFamily}`;
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

export function drawFrequencyScale(context, left, top, height, labelColor, fontFamily, nyquistHz) {
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

export async function parseAudioTimelineImage(buffer) {
    const view = new DataView(buffer);
    if (view.byteLength >= 26 && view.getUint8(0) === 82 && view.getUint8(1) === 70 && view.getUint8(2) === 87 && view.getUint8(3) === 49) {
        let offset = 4;
        const millisecondsPerPoint = view.getFloat64(offset, true); offset += 8;
        const originMs = view.getFloat64(offset, true); offset += 8;
        const peak = view.getInt16(offset, true); offset += 2;
        const count = view.getInt32(offset, true); offset += 4;
        if (!Number.isFinite(millisecondsPerPoint) || millisecondsPerPoint <= 0 || count < 1 || count > 100000000 || offset + count * 4 !== view.byteLength)
            throw new Error('Invalid waveform timeline metadata');
        const minimum = new Int16Array(count);
        const maximum = new Int16Array(count);
        for (let index = 0; index < count; index++) {
            minimum[index] = view.getInt16(offset, true); offset += 2;
            maximum[index] = view.getInt16(offset, true); offset += 2;
        }
        return { kind: 'waveform', millisecondsPerPoint, originMs, peak, minimum, maximum, levels: buildWaveformLevels(minimum, maximum) };
    }
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
    return { kind: 'image', tileWidth, tileHeight, millisecondsPerPixel, tileDurationMs, originMs, tiles };
}

function buildWaveformLevels(minimum, maximum) {
    const levels = [{ minimum, maximum }];
    while (minimum.length > 4096) {
        const count = Math.ceil(minimum.length / 2);
        const nextMinimum = new Int16Array(count);
        const nextMaximum = new Int16Array(count);
        for (let index = 0; index < count; index++) {
            const first = index * 2;
            const second = Math.min(first + 1, minimum.length - 1);
            nextMinimum[index] = Math.min(minimum[first], minimum[second]);
            nextMaximum[index] = Math.max(maximum[first], maximum[second]);
        }
        minimum = nextMinimum;
        maximum = nextMaximum;
        levels.push({ minimum, maximum });
    }
    return levels;
}

export function disposeAudioTimelineImage(image) {
    for (const tile of image?.tiles || []) tile.close();
}

export function formatTimelineTime(milliseconds) {
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
