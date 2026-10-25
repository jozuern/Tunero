// The wallpaper's WebGL2 driver: one full screen triangle, the fragment shader in wallpaper.frag, and a loop that eases
// the clutter value and speeds the motion up as clutter rises. It never drops frames on purpose. No libraries.
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
  /**
   * True while nobody can see the wallpaper move (the boot screen covers it, or a window covers all but the taskbar):
   * no frames are drawn then, and the motion goes on from where it stopped.
   */
  isPaused?: () => boolean;
}

const vertexSource = `#version 300 es
void main() {
  vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
}`;

/** Highest device pixel ratio rendered; the picture is soft, so more pixels would only cost. */
const maxPixelRatio = 1.5;
/**
 * Most pixels drawn per frame, about 720p: the shader's cost grows with the pixels, and a 1440p monitor would ask for
 * four times as many on every frame, enough to drop frames on a laptop's graphics (the motion and the headline's rise
 * stuttered). The browser scales the canvas up; the picture is soft, so it looks the same, the grain a little coarser.
 */
const maxPixels = 1280 * 720;
/**
 * Frames a second the wallpaper draws at least, as a share of the refreshes: every refresh up to about 110 Hz, every
 * second one on a 120 or 144 Hz monitor, every third on a 165 Hz one, every fourth at 240 Hz. Drawing every refresh of
 * a fast monitor would make the browser blur every glass panel above the canvas again each time; the motion is slow, so
 * this looks the same at a fraction of the cost. It is a whole number of refreshes so that each frame stays on screen
 * equally long: 60 frames a second on a 144 Hz monitor hold for 2, 3, 2, 3 refreshes, which reads as stutter.
 */
const leastFrameRate = 55;
/** Refresh intervals measured for the monitor's rate (the middle one counts, so a busy moment does not skew it). */
const refreshSamples = 31;
/** Slow frames in a row (beyond the ones a fast frame cancels) before the picture is drawn with fewer pixels. */
const slowFramesBeforeScaling = 30;
/**
 * Fast frames in a row before the picture tries more pixels again (after a busy moment that made it lower them): 5 s at
 * 60 frames a second, doubled after every try, so a PC that cannot keep up is not made to stutter again and again.
 */
const fastFramesBeforeRecovery = 300;
/** Lowest share of the pixels drawn; the picture is soft and survives it. */
const minQualityScale = 0.5;
/** Angular frequency of the critically damped clutter spring: about 95 percent of the way in 1.2 s. */
const clutterOmega = 4;
/** Rate of the theme cross-fade, per second. */
const themeRate = 9;
/** Pace of the calm motion: the main ribbon's slowest wave takes about 37 s, so at rest it reads as breathing. */
const calmSpeed = 0.8;
/** A frame from the middle of the calm motion, used for the still picture under reduced motion. */
const stillPhase = 14;

type Theme = 'light' | 'dark';

// Moments the desktop asks for; the running wallpaper reads them every frame.
const ripples: { x: number; y: number; start: number; strength: number }[] = [];
/** Seconds a ripple lasts; the shader fades it out by then. */
const rippleLife = 1.8;
let bloomStart = -1;
let split = -1;

/**
 * A ripple at (x, y) in CSS pixels, where something was just fixed. `size` is that thing's size in CSS pixels: a
 * notification makes a wider ripple than a tray icon.
 */
export function ripple(x: number, y: number, size = 300) {
  const strength = Math.min(1, Math.max(0.35, Math.sqrt(size / 300)));
  ripples.push({ x: x / innerWidth, y: y / innerHeight, start: performance.now(), strength });
  if (ripples.length > 4) ripples.shift();
}

/** The PC is fixed: a bloom of colour and a sweep of light. */
export function bloom() {
  bloomStart = performance.now();
}

/** Before/after: the divider in CSS pixels from the left, or null to end it. */
export function setSplit(x: number | null) {
  split = x === null ? -1 : x / innerWidth;
}

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
  const stop = () => {
    cancelled = true;
    cancelAnimationFrame(pollId);
    stopRunning();
    canvas.removeEventListener('webglcontextlost', onLost);
  };
  // Asks the browser for the context back (preventDefault); Wallpaper.svelte starts again once it is restored.
  const onLost = (event: Event) => {
    event.preventDefault();
    stop();
    fail('WebGL context lost');
  };
  canvas.addEventListener('webglcontextlost', onLost);
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
  return stop;

  function begin(program: WebGLProgram, vertexArray: WebGLVertexArrayObject): () => void {
    const uniform = (name: string) => gl.getUniformLocation(program, name);
    const uRes = uniform('uRes');
    const uPhase = uniform('uPhase');
    const uClutter = uniform('uClutter');
    const uTheme = uniform('uTheme');
    const uSeed = uniform('uSeed');
    const uRipples = uniform('uRipples');
    const uBloom = uniform('uBloom');
    const uSplit = uniform('uSplit');
    const rippleData = new Float32Array(16);

    let reduced = reducedMotion();

    const clamp01 = (value: number) => (Number.isFinite(value) ? Math.min(1, Math.max(0, value)) : 0);
    const themeTarget = () => (getTheme() === 'light' ? 1 : 0);

    // Animation state.
    let clutter = clamp01(getClutter());
    let clutterVelocity = 0;
    let theme = themeTarget();
    let phase = stillPhase;
    let seed = 1;

    // Size and quality.
    let cssWidth = canvas.clientWidth;
    let cssHeight = canvas.clientHeight;
    let qualityScale = 1;
    let slowFrames = 0;
    let fastFrames = 0;
    let framesBeforeRecovery = fastFramesBeforeRecovery;

    let frameId = 0;
    let lastNow: number | null = null;
    /** Draw every this many refreshes (leastFrameRate), and the refreshes since the last drawn frame. */
    let stride = 1;
    let sinceDrawn = 0;
    let lastRefresh: number | null = null;
    const refreshes: number[] = [];
    let stopped = false;
    let drewFirst = false;
    let stillClutter = -1;
    let stillTheme = -1;
    let stillSplit = -2;

    function resizeBuffer(context: WebGL2RenderingContext) {
      const budget = Math.sqrt(maxPixels / Math.max(1, cssWidth * cssHeight));
      const ratio = Math.min(window.devicePixelRatio || 1, maxPixelRatio, budget) * qualityScale;
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
      gl.uniform1f(uPhase, phase);
      gl.uniform1f(uClutter, clutter);
      gl.uniform1f(uTheme, theme);
      gl.uniform1f(uSeed, seed);
      const now = performance.now();
      rippleData.fill(0);
      ripples.forEach((r, i) => {
        const age = (now - r.start) / 1000;
        if (age < rippleLife && !reduced) rippleData.set([r.x, r.y, age, r.strength], i * 4);
      });
      gl.uniform4fv(uRipples, rippleData);
      const bloomAge = bloomStart < 0 || reduced ? -1 : (now - bloomStart) / 1000;
      gl.uniform1f(uBloom, bloomAge > 4 ? -1 : bloomAge);
      gl.uniform1f(uSplit, split);
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
      phase = stillPhase;
      seed = 1;
      stillClutter = clutter;
      stillTheme = theme;
      stillSplit = split;
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

      // Neglect speeds the motion up; integrated, so a changing speed never jumps the picture.
      phase += seconds * calmSpeed * (1 + 1.4 * Math.pow(clutter, 1.3));
      seed = (seed % 4093) + 1;
    }

    /**
     * If frames keep coming slowly, draw fewer pixels: the picture is soft and survives a lower resolution. If they then
     * keep coming fast, try more again.
     */
    function watchSpeed(seconds: number) {
      if (seconds > 0.026) {
        slowFrames++;
        fastFrames = 0;
      } else {
        slowFrames = Math.max(0, slowFrames - 2);
        fastFrames++;
      }
      if (slowFrames > slowFramesBeforeScaling && qualityScale > minQualityScale) {
        qualityScale = Math.max(minQualityScale, qualityScale - 0.2);
        slowFrames = 0;
        fastFrames = 0;
      } else if (fastFrames >= framesBeforeRecovery && qualityScale < 1) {
        qualityScale = Math.min(1, qualityScale + 0.2);
        fastFrames = 0;
        framesBeforeRecovery *= 2;
      }
    }

    /** Learns the monitor's refresh rate from the time between two refreshes, and the stride from it. */
    function measure(now: number) {
      const interval = lastRefresh === null ? 0 : now - lastRefresh;
      lastRefresh = now;
      // A hidden tab, a pause or a long busy moment says nothing about the monitor.
      if (interval < 2 || interval > 50) return;
      refreshes.push(interval);
      if (refreshes.length > refreshSamples) refreshes.shift();
      if (refreshes.length < 10) return;
      const middle = [...refreshes].sort((a, b) => a - b)[refreshes.length >> 1];
      stride = Math.max(1, Math.floor(1000 / middle / leastFrameRate));
    }

    function frame(now: number) {
      frameId = requestAnimationFrame(frame);
      measure(now);
      if (events.isPaused?.()) {
        lastNow = null;
        return;
      }
      if (reduced) {
        // Still: draw again only when what the picture shows has changed.
        if (clamp01(getClutter()) !== stillClutter || themeTarget() !== stillTheme || split !== stillSplit) drawStill();
        return;
      }
      // Every stride-th refresh draws, with the time since the last draw.
      if (lastNow !== null && ++sinceDrawn < stride) return;
      sinceDrawn = 0;
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
    const stopMotionWatch = onMotionChange(onMotion);

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
      stopMotionWatch();
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
