// The wallpaper's WebGL2 driver: one full screen triangle, the fragment shader in wallpaper.frag, and a loop that eases
// the clutter value, speeds the motion up and makes it stutter as clutter rises. No libraries.
//
// Failure is reported through the optional `events.onFail` callback, at most once: when WebGL2 is missing, the shader
// does not compile or link, or the context is lost later. startWallpaper never throws for these; it returns a stop
// function either way (a no-op one when it could not start). `events.onFirstFrame` fires once, after the first frame
// has been drawn, so the caller knows when to retire its CSS stand-in.
import fragmentSource from './wallpaper.frag?raw';
import { reducedMotion, onMotionChange } from '../motion.ts';

export interface WallpaperEvents {
  /** The first frame is on the canvas. */
  onFirstFrame?: () => void;
  /** The wallpaper cannot run or has stopped for good; the caller shows its fallback. `reason` is for debugging. */
  onFail?: (reason: string) => void;
}

const vertexSource = `#version 300 es
void main() {
  vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
}`;

/** Highest device pixel ratio rendered; the picture is soft, so more pixels would only cost. */
const maxPixelRatio = 1.5;
/** Angular frequency of the critically damped clutter spring: about 95 percent of the way in 1.2 s. */
const clutterOmega = 4;
/** Rate of the theme cross-fade, per second. */
const themeRate = 9;
/** Pace of the calm motion: the main ribbon's slowest wave takes about 37 s, so at rest it reads as breathing. */
const calmSpeed = 0.8;
/** A frame from the middle of the calm motion, used for the still picture under reduced motion. */
const stillPhase = 14;
const stillTime = 3.5;

type Theme = 'light' | 'dark';

export function startWallpaper(
  canvas: HTMLCanvasElement,
  getClutter: () => number,
  getTheme: () => Theme,
  events: WallpaperEvents = {},
): () => void {
  let failed = false;
  const fail = (reason: string) => {
    if (failed) return;
    failed = true;
    events.onFail?.(reason);
  };

  const context = acquireContext(canvas);
  if (!context) {
    fail('WebGL2 is not available');
    return () => {};
  }
  const gl: WebGL2RenderingContext = context;

  // The shaders compile in the driver's background where KHR_parallel_shader_compile exists: asking for the result
  // right away would block the page for up to a second on some PCs, so the result is read once it is ready.
  const parts = startBuild(gl);
  if (typeof parts === 'string') {
    fail(parts);
    return () => {};
  }
  const parallel = gl.getExtension('KHR_parallel_shader_compile');
  let cancelled = false;
  let stopRunning = () => {};
  let pollId = 0;
  const proceed = () => {
    if (cancelled) return;
    const built = finishBuild(gl, parts);
    if (typeof built === 'string') fail(built);
    else stopRunning = begin(built.program, built.vertexArray);
  };
  const poll = () => {
    if (cancelled) return;
    if (gl.getProgramParameter(parts.program, parallel!.COMPLETION_STATUS_KHR)) proceed();
    else pollId = requestAnimationFrame(poll);
  };
  if (parallel) pollId = requestAnimationFrame(poll);
  else proceed();
  return () => {
    cancelled = true;
    cancelAnimationFrame(pollId);
    stopRunning();
  };

  function begin(program: WebGLProgram, vertexArray: WebGLVertexArrayObject): () => void {
    const uniform = (name: string) => gl.getUniformLocation(program, name);
    const uRes = uniform('uRes');
    const uPhase = uniform('uPhase');
    const uTime = uniform('uTime');
    const uClutter = uniform('uClutter');
    const uTheme = uniform('uTheme');
    const uSeed = uniform('uSeed');
    const uJitter = uniform('uJitter');

    const motionQuery = matchMedia('(prefers-reduced-motion: reduce)');
    let reduced = reducedMotion();

    const clamp01 = (value: number) => (Number.isFinite(value) ? Math.min(1, Math.max(0, value)) : 0);
    const themeTarget = () => (getTheme() === 'light' ? 1 : 0);

    // Animation state.
    let clutter = clamp01(getClutter());
    let clutterVelocity = 0;
    let theme = themeTarget();
    let truePhase = stillPhase;
    let shownPhase = stillPhase;
    let sinceShown = 0;
    let holdLeft = 0;
    let seed = 1;
    let jitterX = 0;
    let jitterY = 0;
    let clock = stillTime;

    // Size and quality.
    let cssWidth = canvas.clientWidth;
    let cssHeight = canvas.clientHeight;
    let qualityScale = 1;
    let slowFrames = 0;

    let frameId = 0;
    let lastNow: number | null = null;
    let stopped = false;
    let drewFirst = false;
    let stillClutter = -1;
    let stillTheme = -1;

    function resizeBuffer(context: WebGL2RenderingContext) {
      const ratio = Math.min(window.devicePixelRatio || 1, maxPixelRatio) * qualityScale;
      const width = Math.max(1, Math.round(cssWidth * ratio));
      const height = Math.max(1, Math.round(cssHeight * ratio));
      if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
      }
      context.viewport(0, 0, width, height);
    }

    function draw() {
      if (stopped || failed) return;
      if (gl.isContextLost()) {
        halt();
        fail('WebGL context lost');
        return;
      }
      resizeBuffer(gl);
      gl.useProgram(program);
      gl.bindVertexArray(vertexArray);
      gl.uniform2f(uRes, canvas.width, canvas.height);
      gl.uniform1f(uPhase, shownPhase);
      gl.uniform1f(uTime, clock);
      gl.uniform1f(uClutter, clutter);
      gl.uniform1f(uTheme, theme);
      gl.uniform1f(uSeed, seed);
      gl.uniform2f(uJitter, jitterX, jitterY);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
      if (!drewFirst) {
        drewFirst = true;
        events.onFirstFrame?.();
      }
    }

    /** The still picture for reduced motion: the target values, no easing, a fixed moment. */
    function drawStill() {
      clutter = clamp01(getClutter());
      clutterVelocity = 0;
      theme = themeTarget();
      shownPhase = truePhase = stillPhase;
      clock = stillTime;
      seed = 1;
      jitterX = jitterY = 0;
      stillClutter = clutter;
      stillTheme = theme;
      draw();
    }

    function advance(seconds: number) {
      // Critically damped spring toward the target clutter; exact for any step, so frame rate does not matter.
      const offset = clutter - clamp01(getClutter());
      const carry = (clutterVelocity + clutterOmega * offset) * seconds;
      const decay = Math.exp(-clutterOmega * seconds);
      clutter = clamp01(clutter - offset + (offset + carry) * decay);
      clutterVelocity = (clutterVelocity - clutterOmega * carry) * decay;
      theme += (themeTarget() - theme) * (1 - Math.exp(-themeRate * seconds));

      clock += seconds;
      // Neglect speeds the motion up (integrated, so a changing speed never jumps the picture) ...
      truePhase += seconds * calmSpeed * (1 + 2.4 * Math.pow(clutter, 1.3));
      // ... and makes it stutter: frames come at an uneven, lower rate, with an occasional longer hitch.
      sinceShown += seconds;
      if (holdLeft > 0) {
        holdLeft -= seconds;
        return;
      }
      const interval = clutter * clutter * (1 / 12);
      if (sinceShown < interval * (0.6 + Math.random() * 0.8)) return;
      const elapsed = sinceShown;
      shownPhase = truePhase;
      sinceShown = 0;
      seed = (seed % 4093) + 1;
      const shake = clutter > 0.05 ? 0.0028 * clutter : 0;
      jitterX = (Math.random() - 0.5) * shake;
      jitterY = (Math.random() - 0.5) * shake;
      if (Math.random() < clutter * clutter * 1.5 * elapsed) {
        holdLeft = 0.08 + Math.random() * 0.2 * clutter;
      }
    }

    /** If frames keep coming slowly, draw fewer pixels: the picture is soft and survives a lower resolution. */
    function watchSpeed(seconds: number) {
      if (qualityScale <= 0.6) return;
      slowFrames = seconds > 0.026 ? slowFrames + 1 : Math.max(0, slowFrames - 2);
      if (slowFrames > 90) {
        qualityScale = Math.max(0.6, qualityScale - 0.2);
        slowFrames = 0;
      }
    }

    function frame(now: number) {
      frameId = requestAnimationFrame(frame);
      if (reduced) {
        // Still: draw again only when what the picture shows has changed.
        if (clamp01(getClutter()) !== stillClutter || themeTarget() !== stillTheme) drawStill();
        return;
      }
      const seconds = lastNow === null ? 0 : Math.min((now - lastNow) / 1000, 0.1);
      lastNow = now;
      if (seconds > 0) watchSpeed(seconds);
      advance(seconds);
      draw();
    }

    function run() {
      if (stopped || failed || frameId || document.visibilityState === 'hidden') return;
      lastNow = null;
      frameId = requestAnimationFrame(frame);
    }

    function halt() {
      if (frameId) cancelAnimationFrame(frameId);
      frameId = 0;
    }

    const onVisibility = () => (document.visibilityState === 'hidden' ? halt() : run());
    const onMotion = () => {
      reduced = reducedMotion();
      if (reduced) drawStill();
      lastNow = null;
    };
    const onLost = () => {
      halt();
      fail('WebGL context lost');
    };

    const resizeObserver =
      typeof ResizeObserver === 'undefined'
        ? null
        : new ResizeObserver((entries) => {
            const box = entries[entries.length - 1].contentRect;
            cssWidth = box.width;
            cssHeight = box.height;
            // Resizing clears the canvas: draw right away so no empty frame shows.
            draw();
          });
    resizeObserver?.observe(canvas);
    const onWindowResize = () => {
      cssWidth = canvas.clientWidth;
      cssHeight = canvas.clientHeight;
      draw();
    };
    if (!resizeObserver) window.addEventListener('resize', onWindowResize);

    document.addEventListener('visibilitychange', onVisibility);
    motionQuery.addEventListener('change', onMotion);
    const stopMotionWatch = onMotionChange(onMotion);
    canvas.addEventListener('webglcontextlost', onLost);

    if (reduced) drawStill();
    else draw();
    run();

    return () => {
      if (stopped) return;
      halt();
      stopped = true;
      resizeObserver?.disconnect();
      window.removeEventListener('resize', onWindowResize);
      document.removeEventListener('visibilitychange', onVisibility);
      motionQuery.removeEventListener('change', onMotion);
      stopMotionWatch();
      canvas.removeEventListener('webglcontextlost', onLost);
      if (!gl.isContextLost()) {
        gl.deleteVertexArray(vertexArray);
        gl.deleteProgram(program);
      }
    };
  }
}

function acquireContext(canvas: HTMLCanvasElement): WebGL2RenderingContext | null {
  try {
    return canvas.getContext('webgl2', {
      alpha: false,
      antialias: false,
      depth: false,
      stencil: false,
      premultipliedAlpha: false,
      preserveDrawingBuffer: false,
      powerPreference: 'low-power',
    });
  } catch {
    return null;
  }
}

type Build = { program: WebGLProgram; vertexArray: WebGLVertexArrayObject; vertex: WebGLShader; fragment: WebGLShader };

/** Starts compiling and linking the two shaders without waiting for the result. */
function startBuild(gl: WebGL2RenderingContext): Build | string {
  const vertex = gl.createShader(gl.VERTEX_SHADER);
  const fragment = gl.createShader(gl.FRAGMENT_SHADER);
  const program = gl.createProgram();
  const vertexArray = gl.createVertexArray();
  if (!vertex || !fragment || !program || !vertexArray) return 'could not create the program';
  gl.shaderSource(vertex, vertexSource);
  gl.compileShader(vertex);
  gl.shaderSource(fragment, fragmentSource);
  gl.compileShader(fragment);
  gl.attachShader(program, vertex);
  gl.attachShader(program, fragment);
  gl.linkProgram(program);
  return { program, vertexArray, vertex, fragment };
}

/** Reads the result of startBuild; returns the reason as a string when the shaders did not compile or link. */
function finishBuild(gl: WebGL2RenderingContext, build: Build): { program: WebGLProgram; vertexArray: WebGLVertexArrayObject } | string {
  const { program, vertexArray, vertex, fragment } = build;
  let error = '';
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    for (const shader of [vertex, fragment])
      if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) error ||= `shader did not compile: ${gl.getShaderInfoLog(shader) ?? ''}`;
    error ||= `program did not link: ${gl.getProgramInfoLog(program) ?? ''}`;
  }
  gl.deleteShader(vertex);
  gl.deleteShader(fragment);
  if (error) {
    gl.deleteProgram(program);
    return error;
  }
  return { program, vertexArray };
}
