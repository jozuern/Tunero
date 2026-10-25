// The desktop's live state: the tested demo logic (lib/demo-state.ts) and window placement (lib/windows.ts) in Svelte
// runes, with the steps of Undo all and what the desktop shows of each change (effects.ts).
// The demo state survives a language switch through sessionStorage; blocked storage just starts fresh.
import { getContext, setContext, tick } from 'svelte';
import { storageKeys } from '../lib/storage-keys.ts';
import type { Demo, Effect, Write } from '../lib/demo.ts';
import type { Page } from './app/appKeys.ts';
import { revive, shatterGone, trails } from './effects.ts';
import { bloom } from './wallpaper/wallpaper.ts';
import { chord } from './sound.ts';
import {
  initial,
  apply,
  undo,
  undoAll,
  undoable,
  clutter,
  isActive,
  isApplied,
  serialize,
  deserialize,
  type Change,
  type DemoState,
  type WindowId,
} from '../lib/demo-state.ts';
import { closedFrame, fittedFrame, frontWindow, openingFrame, taskbarAction, type WindowFrame } from '../lib/windows.ts';

const storageKey = storageKeys.demo;
/** Pause between two steps of Undo all, so the rewind can be followed. */
export const rewindStep = 180;

function readSaved(demo: Demo): DemoState {
  try {
    return deserialize(sessionStorage.getItem(storageKey), demo);
  } catch {
    return initial();
  }
}

export { reducedMotion } from './motion.ts';
import { reducedMotion } from './motion.ts';
const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
const screen = () => ({ width: innerWidth, height: innerHeight });

export class DesktopStore {
  readonly demo: Demo;
  state = $state<DemoState>(initial());
  windows = $state<Record<WindowId, WindowFrame>>({
    tunero: closedFrame('tunero'),
    trust: closedFrame('trust'),
    notepad: closedFrame('notepad'),
    settings: closedFrame('settings'),
  });
  startOpen = $state(false);
  /** The change the dive is showing, or null. */
  dive = $state<{ title: string; write: Write } | null>(null);
  /** The page the Tunero window shows. */
  appPage = $state<Page>('overview');
  /** A tweak the Tunero window should show next: its Tweaks page opens with it selected. */
  showTweak = $state<string | null>(null);
  /** True while Undo all plays its steps. */
  rewinding = $state(false);
  /** True from the moment everything is fixed until the visitor moves on; the end card shows. */
  finale = $state(false);
  /** True while the guided fix (guide.ts) clicks through the plan. */
  guiding = $state(false);
  /** The guided fix's current page and its place among the pages it visits, for the caption; null outside it. */
  guideStep = $state<{ page: 'overview' | 'tweaks' | 'startup' | 'apps'; index: number; total: number } | null>(null);
  /** True while the before/after comparison covers the desktop (Compare.svelte). */
  compare = $state(false);
  // Read on every frame of the wallpaper: worked out again only when the state changes.
  #clutter = $derived.by(() => clutter(this.state, this.demo));
  get clutter() {
    return this.#clutter;
  }
  /** The window in front, or null when none is open. */
  focused = $derived(frontWindow(this.windows));
  /** While Undo all plays, the state it ends in: that is what gets saved, so leaving mid-rewind keeps it. */
  private rewindEnd: DemoState | null = null;
  private topZ = 10;
  private rewindRun = 0;

  constructor(demo: Demo) {
    this.demo = demo;
  }

  /** Reads the saved state and reopens the window that was in front; call once in the browser. */
  restore() {
    this.state = readSaved(this.demo);
    if (this.state.window) this.open(this.state.window);
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

  /** Applies changes; a running rewind is cut short at its end state first. */
  apply(changes: Change[]) {
    this.stopRewind();
    const fresh = changes.filter((change) => !isApplied(this.state, change));
    const before = this.state;
    const after = apply(this.state, changes);
    const burst = shatterGone(before, after, fresh, this.demo);
    this.state = after;
    this.save();
    trails(fresh, 'apply', this.demo, burst);
    if (fresh.length > 0 && clutter(after, this.demo) === 0) this.finished();
  }

  /** Everything is fixed: the wallpaper blooms, the chord plays, the end card shows. */
  private finished() {
    if (!reducedMotion()) bloom();
    chord();
    this.finale = true;
  }

  undo(change: Change) {
    if (!isApplied(this.state, change)) return;
    this.stopRewind();
    this.finale = false;
    this.state = undo(this.state, change);
    revive(this.state, this.demo);
    this.save();
    trails([change], 'undo', this.demo);
  }

  /** Undoes everything one step at a time, newest first; instant under reduced motion. */
  async undoAll() {
    this.stopRewind();
    const run = ++this.rewindRun;
    this.finale = false;
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
      revive(this.state, this.demo);
      trails([step], 'undo', this.demo);
      await wait(rewindStep);
    }
    if (run === this.rewindRun) {
      this.rewinding = false;
      this.rewindEnd = null;
    }
  }

  /** Ends a running Undo all at once, in the state it was heading for. */
  stopRewind() {
    if (!this.rewinding) return;
    this.rewindRun++;
    this.rewinding = false;
    this.rewindEnd = null;
    this.state = undoAll(this.state).state;
    revive(this.state, this.demo);
  }

  markScanned() {
    this.state = { ...this.state, scanned: true };
    // During Undo all the saved state is the one it heads for: it must know about the scan too.
    if (this.rewindEnd) this.rewindEnd = { ...this.rewindEnd, scanned: true };
    this.save();
  }

  markBootSeen() {
    this.state = { ...this.state, bootSeen: true };
    if (this.rewindEnd) this.rewindEnd = { ...this.rewindEnd, bootSeen: true };
    this.save();
  }

  open(id: WindowId) {
    const w = this.windows[id];
    // A window that opens takes the focus as it mounts (Window.svelte); one already open that comes back from the taskbar
    // or to the front takes it here, so Esc and the keyboard reach it.
    const raise = w.open && (w.minimized || this.focused !== id);
    if (!w.open) Object.assign(w, openingFrame(id, screen(), Object.values(this.windows).filter((x) => x.open).length));
    w.open = true;
    w.minimized = false;
    this.focus(id);
    this.startOpen = false;
    if (raise) void tick().then(() => document.querySelector<HTMLElement>(`[data-window="${id}"]`)?.focus({ preventScroll: true }));
  }

  /** Keeps every window on screen and as large as it should be after the browser window changed size. */
  fit() {
    for (const [id, w] of Object.entries(this.windows) as [WindowId, WindowFrame][]) Object.assign(w, fittedFrame(id, w, screen()));
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

  /** Opens Tunero on the tweak that turns `effect` off. */
  openTweakFor(effect: Effect) {
    this.showTweak = this.demo.tweaks.find((t) => t.effect === effect)?.id ?? null;
    this.open('tunero');
  }

  /** Taskbar button: open, bring to front, or minimize when already in front. */
  toggle(id: WindowId) {
    if (taskbarAction(this.windows, id) === 'minimize') this.minimize(id);
    else this.open(id);
  }
}

const key = Symbol('desktop');
export const provideStore = (store: DesktopStore) => setContext(key, store);
export const useStore = () => getContext<DesktopStore>(key);

/**
 * The store as the before/after comparison shows it: the neglected PC (nothing applied) or the fixed one (everything
 * applied), with Start open. Reads go to the real store, writes are dropped: the comparison only shows.
 */
export function comparisonView(store: DesktopStore, side: 'before' | 'after'): DesktopStore {
  return new Proxy(store, {
    get(target, prop) {
      if (prop === 'active') return () => side === 'before';
      if (prop === 'applied') return () => side === 'after';
      if (prop === 'startOpen') return true;
      const value = Reflect.get(target, prop, target);
      return typeof value === 'function' ? value.bind(target) : value;
    },
    set: () => true,
  });
}
