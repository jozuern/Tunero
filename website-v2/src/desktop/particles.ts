// Shatter: when a change turns something off, it bursts into specks of its own colours that stream into Tunero's
// Changes page, where the change is now listed and can be undone. One 2D canvas over the desktop, drawn only while
// specks are in flight; nothing runs under reduced motion.
//
// Each speck follows a fixed path in time rather than physics, so every frame is smooth and the timing reads the same
// on any machine: it drifts out from where it was and slows down, waits a moment, then leaves on an eased curve that
// bends to one side and lands in the target. Departures are spread out, so the specks leave as a stream, not a blob.
import { reducedMotion } from './motion.ts';

interface Speck {
  /** Where it was in the element, where the drift ends, the curve's control point and the target. */
  x0: number;
  y0: number;
  x1: number;
  y1: number;
  cx: number;
  cy: number;
  tx: number;
  ty: number;
  size: number;
  color: string;
  sprite: HTMLCanvasElement;
  age: number;
  /** Seconds of drifting out, then of waiting, then of travel to the target. */
  drift: number;
  wait: number;
  travel: number;
  /** Last drawn position, for the short motion streak while it travels. */
  px: number;
  py: number;
}

let canvas: HTMLCanvasElement | null = null;
let context: CanvasRenderingContext2D | null = null;
let specks: Speck[] = [];
let frameId = 0;
let last = 0;

/** At most this many in flight, so a big step stays smooth. */
const maxSpecks = 900;
/** Soft round dots, one pre-rendered sprite per colour. */
const sprites = new Map<string, HTMLCanvasElement>();
const spriteSize = 32;

function ensureCanvas() {
  if (canvas) return;
  canvas = document.createElement('canvas');
  canvas.className = 'shatter';
  canvas.setAttribute('aria-hidden', 'true');
  document.body.append(canvas);
  context = canvas.getContext('2d');
}

function resize() {
  if (!canvas || !context) return;
  const ratio = Math.min(devicePixelRatio || 1, 2);
  const width = Math.round(innerWidth * ratio);
  const height = Math.round(innerHeight * ratio);
  if (canvas.width !== width || canvas.height !== height) {
    canvas.width = width;
    canvas.height = height;
  }
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
}

/** A dot with a bright core and a soft edge in `color`. */
function spriteOf(color: string): HTMLCanvasElement {
  let sprite = sprites.get(color);
  if (sprite) return sprite;
  sprite = document.createElement('canvas');
  sprite.width = sprite.height = spriteSize;
  const g = sprite.getContext('2d')!;
  const half = spriteSize / 2;
  const gradient = g.createRadialGradient(half, half, 0, half, half, half);
  gradient.addColorStop(0, color);
  gradient.addColorStop(0.35, color);
  gradient.addColorStop(1, 'rgba(0, 0, 0, 0)');
  g.fillStyle = gradient;
  g.fillRect(0, 0, spriteSize, spriteSize);
  sprites.set(color, sprite);
  return sprite;
}

/** Where the specks go: the Changes entry of the Tunero window, else Tunero in the taskbar. */
export function shatterTarget(): { x: number; y: number } {
  const candidates = ['[data-window="tunero"]:not(.minimized) [data-changes]', '[data-taskbar="tunero"]', '[data-anchor="start"]'];
  for (const selector of candidates) {
    const element = document.querySelector(selector);
    const rect = element?.getBoundingClientRect();
    if (rect && rect.width > 0) return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
  }
  return { x: innerWidth / 2, y: innerHeight - 24 };
}

function colorsOf(element: Element): string[] {
  const style = getComputedStyle(element);
  const accent = getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#4cc2ff';
  // Dark text colour bursting over the light wallpaper reads as dirt, so in light mode the specks are accent and fill.
  const light = document.documentElement.dataset.theme === 'light';
  const colors = light ? [accent, accent, accent] : [style.color, accent, accent, accent];
  const background = style.backgroundColor;
  if (background && !/rgba?\(0, 0, 0, 0\)|transparent/.test(background)) colors.push(background, background);
  return colors;
}

const easeOut = (t: number) => 1 - (1 - t) ** 3;
const easeInOut = (t: number) => (t < 0.5 ? 4 * t * t * t : 1 - (-2 * t + 2) ** 3 / 2);

/** Bursts `element` into specks; the caller hides the element itself. */
export function shatter(element: Element, target = shatterTarget()) {
  if (reducedMotion()) return;
  const rect = element.getBoundingClientRect();
  if (rect.width < 2 || rect.height < 2) return;
  ensureCanvas();
  const colors = colorsOf(element);
  const area = rect.width * rect.height;
  const count = Math.min(Math.max(Math.round(area / 220), 18), 240, maxSpecks - specks.length);
  const cx = rect.left + rect.width / 2;
  const cy = rect.top + rect.height / 2;
  // The curve bends to the same side for the whole burst, so the stream reads as one gesture.
  const side = Math.random() < 0.5 ? -1 : 1;
  for (let i = 0; i < count; i++) {
    const x0 = rect.left + Math.random() * rect.width;
    const y0 = rect.top + Math.random() * rect.height;
    const angle = Math.atan2(y0 - cy, x0 - cx) + (Math.random() - 0.5) * 1.2;
    const reach = 14 + Math.random() * 46;
    const x1 = x0 + Math.cos(angle) * reach;
    const y1 = y0 + Math.sin(angle) * reach - 10;
    const tx = target.x + (Math.random() - 0.5) * 10;
    const ty = target.y + (Math.random() - 0.5) * 10;
    // Control point: off the straight line, to one side, so the path is an arc.
    const dx = tx - x1;
    const dy = ty - y1;
    const bend = (0.18 + Math.random() * 0.22) * side;
    const color = colors[Math.floor(Math.random() * colors.length)];
    specks.push({
      x0,
      y0,
      x1,
      y1,
      cx: (x1 + tx) / 2 - dy * bend,
      cy: (y1 + ty) / 2 + dx * bend,
      tx,
      ty,
      size: 2.5 + Math.random() * 3,
      color,
      sprite: spriteOf(color),
      age: 0,
      drift: 0.45 + Math.random() * 0.25,
      wait: Math.random() * 0.45,
      travel: 0.8 + Math.random() * 0.35,
      px: x0,
      py: y0,
    });
  }
  if (!frameId) {
    last = performance.now();
    frameId = requestAnimationFrame(step);
  }
}

function step(now: number) {
  if (!context || !canvas) return;
  const dt = Math.min((now - last) / 1000, 0.05);
  last = now;
  resize();
  context.clearRect(0, 0, innerWidth, innerHeight);
  const light = document.documentElement.dataset.theme === 'light';
  context.globalCompositeOperation = light ? 'source-over' : 'lighter';
  context.lineCap = 'round';
  const alive: Speck[] = [];
  for (const speck of specks) {
    speck.age += dt;
    let x: number;
    let y: number;
    let scale = 1;
    let alpha = Math.min(1, speck.age / 0.12);
    const moving = speck.age - speck.drift - speck.wait;
    if (moving < 0) {
      // Drift out and slow down, then hover where the drift ended.
      const t = easeOut(Math.min(1, speck.age / speck.drift));
      x = speck.x0 + (speck.x1 - speck.x0) * t;
      y = speck.y0 + (speck.y1 - speck.y0) * t;
    } else {
      const t = Math.min(1, moving / speck.travel);
      if (t >= 1) continue;
      const e = easeInOut(t);
      const u = 1 - e;
      x = u * u * speck.x1 + 2 * u * e * speck.cx + e * e * speck.tx;
      y = u * u * speck.y1 + 2 * u * e * speck.cy + e * e * speck.ty;
      scale = 1 - 0.6 * e;
      alpha *= 1 - 0.5 * e * e;
    }
    const size = speck.size * scale;
    context.globalAlpha = alpha * (light ? 0.9 : 0.8);
    if (moving > 0) {
      // A short streak along the way it moved this frame, so fast specks read as motion and not as flicker.
      context.strokeStyle = speck.color;
      context.globalAlpha *= 0.35;
      context.lineWidth = size * 0.45;
      context.beginPath();
      context.moveTo(speck.px, speck.py);
      context.lineTo(x, y);
      context.stroke();
      context.globalAlpha /= 0.35;
    }
    context.drawImage(speck.sprite, x - size, y - size, size * 2, size * 2);
    speck.px = x;
    speck.py = y;
    alive.push(speck);
  }
  context.globalAlpha = 1;
  specks = alive;
  if (specks.length > 0) frameId = requestAnimationFrame(step);
  else {
    frameId = 0;
    context.clearRect(0, 0, innerWidth, innerHeight);
  }
}
