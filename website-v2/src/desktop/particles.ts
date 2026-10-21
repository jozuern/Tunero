// Shatter: when a change turns something off, it bursts into specks of its own colours that stream into Tunero's
// Changes page, where the change is now listed and can be undone. One 2D canvas over the desktop, drawn only while
// specks are in flight; nothing runs under reduced motion.
import { reducedMotion } from './motion.ts';

interface Speck {
  x: number;
  y: number;
  vx: number;
  vy: number;
  size: number;
  color: string;
  age: number;
  /** Seconds before it starts to home in, so the burst reads first. */
  hold: number;
  tx: number;
  ty: number;
}

let canvas: HTMLCanvasElement | null = null;
let context: CanvasRenderingContext2D | null = null;
let specks: Speck[] = [];
let frameId = 0;
let last = 0;

/** At most this many in flight, so a big step stays smooth. */
const maxSpecks = 2400;

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
  const colors = [style.color, accent, accent];
  const background = style.backgroundColor;
  if (background && !/rgba?\(0, 0, 0, 0\)|transparent/.test(background)) colors.push(background, background, background);
  return colors;
}

/** Bursts `element` into specks; the caller hides the element itself. */
export function shatter(element: Element, target = shatterTarget()) {
  if (reducedMotion()) return;
  const rect = element.getBoundingClientRect();
  if (rect.width < 2 || rect.height < 2) return;
  ensureCanvas();
  const colors = colorsOf(element);
  const area = rect.width * rect.height;
  const count = Math.min(Math.max(Math.round(area / 70), 40), 700, maxSpecks - specks.length);
  const cx = rect.left + rect.width / 2;
  const cy = rect.top + rect.height / 2;
  for (let i = 0; i < count; i++) {
    const x = rect.left + Math.random() * rect.width;
    const y = rect.top + Math.random() * rect.height;
    const angle = Math.atan2(y - cy, x - cx) + (Math.random() - 0.5) * 0.9;
    const speed = 60 + Math.random() * 260;
    specks.push({
      x,
      y,
      vx: Math.cos(angle) * speed,
      vy: Math.sin(angle) * speed - 40,
      size: 1.5 + Math.random() * 2.5,
      color: colors[Math.floor(Math.random() * colors.length)],
      age: 0,
      hold: 0.18 + Math.random() * 0.3,
      tx: target.x + (Math.random() - 0.5) * 16,
      ty: target.y + (Math.random() - 0.5) * 16,
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
  const alive: Speck[] = [];
  for (const speck of specks) {
    speck.age += dt;
    if (speck.age < speck.hold) {
      // The burst: outward, slowing down.
      speck.vx *= 1 - 3.2 * dt;
      speck.vy = speck.vy * (1 - 3.2 * dt) + 30 * dt;
    } else {
      // Home in: a spring toward the target that grows stronger, so the stream tightens as it arrives.
      const pull = 14 + (speck.age - speck.hold) * 60;
      speck.vx += (speck.tx - speck.x) * pull * dt;
      speck.vy += (speck.ty - speck.y) * pull * dt;
      speck.vx *= 1 - 5.5 * dt;
      speck.vy *= 1 - 5.5 * dt;
    }
    speck.x += speck.vx * dt;
    speck.y += speck.vy * dt;
    const distance = Math.hypot(speck.tx - speck.x, speck.ty - speck.y);
    if (speck.age > 3 || (speck.age > speck.hold && distance < 6)) continue;
    const fade = Math.min(1, distance / 60);
    context.globalAlpha = Math.min(1, speck.age * 8) * (0.35 + 0.65 * fade);
    context.fillStyle = speck.color;
    const size = speck.size * (0.5 + 0.5 * fade);
    context.fillRect(speck.x - size / 2, speck.y - size / 2, size, size);
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
