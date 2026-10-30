// Where the demo's windows go, without the DOM: the store (desktop/store.svelte.ts) and Window.svelte apply it.
import type { WindowId } from './demo-state.ts';

export interface WindowFrame {
  open: boolean;
  minimized: boolean;
  maximized: boolean;
  x: number;
  y: number;
  width: number;
  height: number;
  z: number;
}

export interface Viewport {
  width: number;
  height: number;
}

/** The size each window opens at, when the screen has room for it. */
export const windowSizes: Record<WindowId, { width: number; height: number }> = {
  tunero: { width: 1160, height: 700 },
  trust: { width: 860, height: 560 },
  notepad: { width: 720, height: 520 },
  settings: { width: 820, height: 440 },
};

/** The taskbar's height: no window goes under it. */
export const taskbarHeight = 48;
/** The distance a window keeps from the screen's edges when it opens. */
const margin = 16;
/** How far each further open window opens down and right of the last. */
const cascade = 28;
/** How much of a dragged window's title bar stays on screen, across and below. */
const grip = { x: 96, y: 40 };

export const closedFrame = (id: WindowId): WindowFrame => ({ open: false, minimized: false, maximized: false, x: 0, y: 0, ...windowSizes[id], z: 0 });

/** The window's size on this screen: its own, or less, with a margin on each side and the taskbar below. */
export function fittedSize(id: WindowId, screen: Viewport) {
  return {
    width: Math.min(windowSizes[id].width, screen.width - 2 * margin),
    height: Math.min(windowSizes[id].height, screen.height - taskbarHeight - 2 * margin),
  };
}

/**
 * Where a window opens: centred, each further window (`openCount` are open already) a step down and right, but never
 * past the screen's edge or under the taskbar, so the caption buttons stay in reach.
 */
export function openingFrame(id: WindowId, screen: Viewport, openCount: number) {
  const { width, height } = fittedSize(id, screen);
  const offset = openCount * cascade;
  const room = screen.height - taskbarHeight;
  return {
    width,
    height,
    x: Math.max(margin, Math.min(Math.round((screen.width - width) / 2) + offset, screen.width - width - margin)),
    y: Math.max(margin, Math.min(Math.round((room - height) / 2) + offset, room - height - margin)),
  };
}

/** After the browser window changed size: the window as large as it should be, and moved back on screen. */
export function fittedFrame(id: WindowId, frame: Pick<WindowFrame, 'x' | 'y'>, screen: Viewport) {
  const { width, height } = fittedSize(id, screen);
  return {
    width,
    height,
    x: Math.min(Math.max(frame.x, 0), Math.max(0, screen.width - width)),
    y: Math.min(Math.max(frame.y, 0), Math.max(0, screen.height - taskbarHeight - height)),
  };
}

/** Where a dragged window goes: the title bar stays reachable, never above the screen, off a side or under the taskbar. */
export function draggedTo(x: number, y: number, width: number, screen: Viewport) {
  return {
    x: Math.min(Math.max(x, grip.x - width), screen.width - grip.x),
    y: Math.min(Math.max(y, 0), screen.height - taskbarHeight - grip.y),
  };
}

/** The window in front: the open one, not minimized, that was raised last. Null when none shows. */
export function frontWindow(windows: Record<WindowId, WindowFrame>): WindowId | null {
  const shown = (Object.entries(windows) as [WindowId, WindowFrame][]).filter(([, w]) => w.open && !w.minimized);
  return shown.length === 0 ? null : shown.reduce((a, b) => (b[1].z > a[1].z ? b : a))[0];
}

/** A taskbar button: minimizes its window when that one is in front, and opens or raises it otherwise. */
export function taskbarAction(windows: Record<WindowId, WindowFrame>, id: WindowId): 'minimize' | 'open' {
  return frontWindow(windows) === id ? 'minimize' : 'open';
}
