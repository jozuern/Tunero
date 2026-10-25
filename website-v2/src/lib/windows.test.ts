import { test } from 'node:test';
import assert from 'node:assert/strict';
import { closedFrame, draggedTo, fittedFrame, frontWindow, openingFrame, taskbarAction, taskbarHeight, windowSizes, type Viewport, type WindowFrame } from './windows.ts';
import type { WindowId } from './demo-state.ts';

const ids = Object.keys(windowSizes) as WindowId[];
const closed = () => Object.fromEntries(ids.map((id) => [id, closedFrame(id)])) as Record<WindowId, WindowFrame>;

/** Opens the windows in this order as the store does: each one counts the windows already open and goes in front. */
function openAll(order: WindowId[], screen: Viewport) {
  const windows = closed();
  order.forEach((id, i) => Object.assign(windows[id], openingFrame(id, screen, i), { open: true, z: i + 1 }));
  return windows;
}

/** The whole window, with its caption buttons at the top right, is on screen and above the taskbar. */
function onScreen(w: WindowFrame, screen: Viewport) {
  return w.x >= 0 && w.y >= 0 && w.x + w.width <= screen.width && w.y + w.height <= screen.height - taskbarHeight;
}

test('a window opens at its own size, centred above the taskbar, on a large screen', () => {
  const screen = { width: 1920, height: 1080 };
  const frame = openingFrame('notepad', screen, 0);
  assert.deepEqual({ width: frame.width, height: frame.height }, windowSizes.notepad);
  assert.equal(frame.x, (1920 - 720) / 2);
  assert.equal(frame.y, (1080 - taskbarHeight - 520) / 2);
});

test('each further window opens a step down and right of the last', () => {
  const screen = { width: 1920, height: 1080 };
  const first = openingFrame('notepad', screen, 0);
  const second = openingFrame('notepad', screen, 1);
  assert.equal(second.x - first.x, 28);
  assert.equal(second.y - first.y, 28);
});

test('every window stays on screen with all four open on a 1024 x 768 screen, in any order', () => {
  // The caption buttons of Tunero were off screen here once, with other windows open before it.
  const screen = { width: 1024, height: 768 };
  for (const order of [ids, [...ids].reverse(), ['trust', 'notepad', 'settings', 'tunero'] as WindowId[]]) {
    const windows = openAll(order, screen);
    for (const id of ids) assert.ok(onScreen(windows[id], screen), `${id} after ${order.join(', ')}: ${JSON.stringify(windows[id])}`);
  }
});

test('a window larger than the screen shrinks to it, with a margin and the taskbar left free', () => {
  const screen = { width: 900, height: 600 };
  const frame = openingFrame('tunero', screen, 3);
  assert.equal(frame.width, 900 - 32);
  assert.equal(frame.height, 600 - taskbarHeight - 32);
  assert.ok(onScreen({ ...closedFrame('tunero'), ...frame }, screen));
});

test('after the browser window shrinks, a window moves and shrinks back on screen', () => {
  const large = { width: 1920, height: 1080 };
  const small = { width: 1024, height: 700 };
  const frame = { ...closedFrame('tunero'), ...openingFrame('tunero', large, 0), open: true };
  Object.assign(frame, fittedFrame('tunero', frame, small));
  assert.ok(onScreen(frame, small), JSON.stringify(frame));
});

test('after the browser window grows again, a window takes its own size back', () => {
  const frame = { ...closedFrame('tunero'), ...openingFrame('tunero', { width: 900, height: 600 }, 0) };
  Object.assign(frame, fittedFrame('tunero', frame, { width: 1920, height: 1080 }));
  assert.deepEqual({ width: frame.width, height: frame.height }, windowSizes.tunero);
});

test('a dragged window keeps its title bar on screen and above the taskbar', () => {
  const screen = { width: 1280, height: 800 };
  assert.deepEqual(draggedTo(-5000, -300, 720, screen), { x: 96 - 720, y: 0 });
  assert.deepEqual(draggedTo(5000, 5000, 720, screen), { x: 1280 - 96, y: 800 - taskbarHeight - 40 });
  assert.deepEqual(draggedTo(200, 150, 720, screen), { x: 200, y: 150 });
});

test('the window in front is the open, not minimized one raised last', () => {
  const windows = openAll(['tunero', 'trust', 'notepad'], { width: 1920, height: 1080 });
  assert.equal(frontWindow(windows), 'notepad');
  windows.notepad.minimized = true;
  assert.equal(frontWindow(windows), 'trust');
  windows.trust.open = false;
  assert.equal(frontWindow(windows), 'tunero');
  windows.tunero.open = false;
  assert.equal(frontWindow(windows), null);
});

test('a taskbar button minimizes its window in front, and opens or raises it otherwise', () => {
  const windows = openAll(['tunero', 'trust'], { width: 1920, height: 1080 });
  assert.equal(taskbarAction(windows, 'trust'), 'minimize');
  assert.equal(taskbarAction(windows, 'tunero'), 'open');
  assert.equal(taskbarAction(windows, 'settings'), 'open');
  windows.trust.minimized = true;
  assert.equal(taskbarAction(windows, 'trust'), 'open');
});
