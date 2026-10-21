// The desktop's live state: the tested demo logic (lib/demo-state.ts) in Svelte runes, plus the window manager.
// The demo state survives a language switch through sessionStorage; blocked storage just starts fresh.
import { getContext, setContext } from 'svelte';
import type { Demo, Effect, Write } from '../lib/demo.ts';
import { trail } from './effects.ts';
import {
  initial,
  apply,
  undo,
  undoAll,
  undoable,
  allChanges,
  clutter,
  isActive,
  isApplied,
  serialize,
  deserialize,
  type Change,
  type DemoState,
  type WindowId,
} from '../lib/demo-state.ts';

const storageKey = 'tunero-demo';
/** Pause between two steps of Undo all, so the rewind can be followed. */
export const rewindStep = 180;

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

const sizes: Record<WindowId, { width: number; height: number }> = {
  tunero: { width: 1040, height: 700 },
  trust: { width: 860, height: 560 },
  notepad: { width: 720, height: 520 },
  settings: { width: 820, height: 560 },
};

function readSaved(): DemoState {
  try {
    return deserialize(sessionStorage.getItem(storageKey));
  } catch {
    return initial();
  }
}

export { reducedMotion } from './motion.ts';
import { reducedMotion } from './motion.ts';
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

export class DesktopStore {
  readonly demo: Demo;
  state = $state<DemoState>(initial());
  windows = $state<Record<WindowId, WindowFrame>>({
    tunero: this.frame('tunero'),
    trust: this.frame('trust'),
    notepad: this.frame('notepad'),
    settings: this.frame('settings'),
  });
  startOpen = $state(false);
  /** The change the dive is showing, or null. */
  dive = $state<{ title: string; write: Write } | null>(null);
  /** True while Undo all plays its steps. */
  rewinding = $state(false);
  get clutter() {
    return clutter(this.state, this.demo);
  }
  /** The window in front, or null when none is open. */
  focused = $derived.by(() => {
    const open = (Object.entries(this.windows) as [WindowId, WindowFrame][]).filter(([, w]) => w.open && !w.minimized);
    return open.length === 0 ? null : open.reduce((a, b) => (b[1].z > a[1].z ? b : a))[0];
  });
  /** While Undo all plays, the state it ends in: that is what gets saved, so leaving mid-rewind keeps it. */
  private rewindEnd: DemoState | null = null;
  private topZ = 10;
  private rewindRun = 0;

  constructor(demo: Demo) {
    this.demo = demo;
  }

  /** Reads the saved state and reopens the window that was in front; call once in the browser. */
  restore() {
    this.state = readSaved();
    if (this.state.window) this.open(this.state.window);
  }

  private frame(id: WindowId): WindowFrame {
    return { open: false, minimized: false, maximized: false, x: 0, y: 0, ...sizes[id], z: 0 };
  }

  private save() {
    try {
      sessionStorage.setItem(storageKey, serialize({ ...$state.snapshot(this.rewindEnd ?? this.state), window: this.focused }));
    } catch {
      // Storage blocked: the demo still works, it just does not survive a reload.
    }
  }

  active(effect: Effect) {
    return isActive(this.state, this.demo, effect);
  }

  applied(change: Change) {
    return isApplied(this.state, change);
  }

  /** What the Changes page can undo (removed apps come back from the Microsoft Store instead). */
  get undoable() {
    return undoable(this.state);
  }

  /** Shows each change travelling to its place on the desktop, a little apart when several go at once. */
  private show(changes: Change[], direction: 'apply' | 'undo') {
    const effects = allChanges(this.demo);
    changes.forEach((change, i) => {
      const effect = effects.find((c) => c.kind === change.kind && c.id === change.id)?.effect;
      if (effect) setTimeout(() => trail(effect, direction), i * 90);
    });
  }

  /** Applies changes; a running rewind is cut short at its end state first. */
  apply(changes: Change[]) {
    this.stopRewind();
    const fresh = changes.filter((change) => !isApplied(this.state, change));
    this.state = apply(this.state, changes);
    this.save();
    this.show(fresh, 'apply');
  }

  undo(change: Change) {
    if (!isApplied(this.state, change)) return;
    this.stopRewind();
    this.state = undo(this.state, change);
    this.save();
    this.show([change], 'undo');
  }

  /** Undoes everything one step at a time, newest first; instant under reduced motion. */
  async undoAll() {
    this.stopRewind();
    const run = ++this.rewindRun;
    const { steps } = undoAll(this.state);
    if (reducedMotion()) {
      this.state = undoAll(this.state).state;
      this.save();
      return;
    }
    this.rewinding = true;
    this.rewindEnd = undoAll(this.state).state;
    this.save();
    for (const step of steps) {
      if (run !== this.rewindRun) return;
      this.state = undo(this.state, step);
      this.show([step], 'undo');
      await wait(rewindStep);
    }
    if (run === this.rewindRun) {
      this.rewinding = false;
      this.rewindEnd = null;
    }
  }

  private stopRewind() {
    if (!this.rewinding) return;
    this.rewindRun++;
    this.rewinding = false;
    this.rewindEnd = null;
    this.state = undoAll(this.state).state;
  }

  markScanned() {
    this.state = { ...this.state, scanned: true };
    this.save();
  }

  markBootSeen() {
    this.state = { ...this.state, bootSeen: true };
    this.save();
  }

  open(id: WindowId) {
    const w = this.windows[id];
    if (!w.open) {
      const width = Math.min(w.width, innerWidth - 32);
      const height = Math.min(w.height, innerHeight - 48 - 32);
      const offset = Object.values(this.windows).filter((x) => x.open).length * 28;
      w.width = width;
      w.height = height;
      w.x = Math.max(16, Math.round((innerWidth - width) / 2) + offset);
      w.y = Math.max(16, Math.round((innerHeight - 48 - height) / 2) + offset);
    }
    w.open = true;
    w.minimized = false;
    this.focus(id);
    this.startOpen = false;
  }

  /** Keeps every window on screen and as large as it should be after the browser window changed size. */
  fit() {
    for (const [id, w] of Object.entries(this.windows) as [WindowId, WindowFrame][]) {
      w.width = Math.min(sizes[id].width, innerWidth - 32);
      w.height = Math.min(sizes[id].height, innerHeight - 48 - 32);
      w.x = Math.min(Math.max(w.x, 0), Math.max(0, innerWidth - w.width));
      w.y = Math.min(Math.max(w.y, 0), Math.max(0, innerHeight - 48 - w.height));
    }
  }

  focus(id: WindowId) {
    this.windows[id].z = ++this.topZ;
    this.save();
  }

  close(id: WindowId) {
    this.windows[id].open = false;
    this.windows[id].maximized = false;
    this.save();
  }

  minimize(id: WindowId) {
    this.windows[id].minimized = true;
    this.save();
    // Focus goes to the window's taskbar button, not to the page.
    (document.querySelector(`[data-taskbar="${id}"]`) as HTMLElement | null)?.focus();
  }

  toggleMaximize(id: WindowId) {
    this.windows[id].maximized = !this.windows[id].maximized;
    this.focus(id);
  }

  /** Taskbar button: open, bring to front, or minimize when already in front. */
  toggle(id: WindowId) {
    const w = this.windows[id];
    if (w.open && !w.minimized && this.focused === id) this.minimize(id);
    else this.open(id);
  }
}

const key = Symbol('desktop');
export const provideStore = (store: DesktopStore) => setContext(key, store);
export const useStore = () => getContext<DesktopStore>(key);
