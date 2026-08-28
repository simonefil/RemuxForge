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
        this.textures = [this.gl.createTexture(), this.gl.createTexture()];
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
        this.canvas.width = width;
        this.canvas.height = height;
        gl.viewport(0, 0, width, height);
        gl.useProgram(this.program);
        gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);

        if (p010) {
            const yLength = width * height;
            uploadIntegerPlane(gl, this.textures[0], 0, width, height, gl.R16UI, gl.RED_INTEGER, new Uint16Array(buffer, 0, yLength));
            uploadIntegerPlane(gl, this.textures[1], 1, width / 2, height / 2, gl.RG16UI, gl.RG_INTEGER, new Uint16Array(buffer, yLength * 2));
        } else {
            const yLength = width * height;
            uploadPlane(gl, this.textures[0], 0, width, height, gl.R8, gl.RED, new Uint8Array(buffer, 0, yLength));
            uploadPlane(gl, this.textures[1], 1, width / 2, height / 2, gl.RG8, gl.RG, new Uint8Array(buffer, yLength));
        }

        setUniforms(gl, this.program, metadata, p010);
        gl.drawArrays(gl.TRIANGLES, 0, 6);
    }

    dispose() {
        this.gl.deleteTexture(this.textures[0]);
        this.gl.deleteTexture(this.textures[1]);
        this.gl.deleteProgram(this.program);
    }
}

export function createPreviewPair(sourceCanvas, languageCanvas) {
    const source = new RawFrameRenderer(sourceCanvas);
    const language = new RawFrameRenderer(languageCanvas);
    let generation = 0;
    let controller = null;
    return {
        async loadPair(sourceUrl, languageUrl) {
            generation++;
            const current = generation;
            if (controller) controller.abort();
            controller = new AbortController();
            const [sourceFrame, languageFrame] = await Promise.all([
                fetchRawFrame(sourceUrl, controller.signal),
                fetchRawFrame(languageUrl, controller.signal)
            ]);
            if (current !== generation) return false;
            source.render(sourceFrame.buffer, sourceFrame.metadata);
            language.render(languageFrame.buffer, languageFrame.metadata);
            return true;
        },
        cancel() {
            generation++;
            if (controller) controller.abort();
            controller = null;
        },
        dispose() {
            this.cancel();
            source.dispose();
            language.dispose();
        }
    };
}

async function fetchRawFrame(url, signal) {
    const response = await fetch(url, { signal, cache: 'default' });
    if (!response.ok) throw new Error(await response.text() || `Preview request failed: ${response.status}`);
    return {
        buffer: await response.arrayBuffer(),
        metadata: {
            width: response.headers.get('X-Frame-Width'),
            height: response.headers.get('X-Frame-Height'),
            pixelFormat: response.headers.get('X-Pixel-Format'),
            colorSpace: response.headers.get('X-Color-Space'),
            colorRange: response.headers.get('X-Color-Range'),
            colorTransfer: response.headers.get('X-Color-Transfer')
        }
    };
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
    gl.uniform1i(gl.getUniformLocation(program, 'u_y16'), 0);
    gl.uniform1i(gl.getUniformLocation(program, 'u_uv16'), 1);
    gl.uniform1i(gl.getUniformLocation(program, 'u_p010'), p010 ? 1 : 0);
    gl.uniform1i(gl.getUniformLocation(program, 'u_limited'), metadata.colorRange !== 'pc' ? 1 : 0);
    const transfer = metadata.colorTransfer === 'smpte2084' ? 1 : metadata.colorTransfer === 'arib-std-b67' ? 2 : 0;
    gl.uniform1i(gl.getUniformLocation(program, 'u_hdr'), transfer !== 0 ? 1 : 0);
    gl.uniform1i(gl.getUniformLocation(program, 'u_transfer'), transfer);
    let matrix = [1, 1, 1, 0, -0.187324, 1.8556, 1.5748, -0.468124, 0];
    if (metadata.colorSpace === 'bt2020nc') matrix = [1, 1, 1, 0, -0.164553, 1.8814, 1.4746, -0.571353, 0];
    else if (metadata.colorSpace !== 'bt709') matrix = [1, 1, 1, 0, -0.344136, 1.772, 1.402, -0.714136, 0];
    gl.uniformMatrix3fv(gl.getUniformLocation(program, 'u_yuvMatrix'), false, matrix);
}
