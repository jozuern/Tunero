// Buttons that a click takes away (Scan once the scan runs, Turn on once the group is on, Undo with its row, Reinstall
// with its app) would drop the keyboard focus to the page, where Esc no longer reaches the window and a screen reader
// says nothing. handOff moves it on to what comes next, once the page has updated.
import { tick } from 'svelte';

/**
 * After a click on `from`: if the focus was on it (or has fallen to the page), it moves to `next()`. A button that
 * leaves with a transition still holds the focus until it is gone, so it is handed over at once, not when it is lost.
 */
export async function handOff(from: EventTarget | null, next: () => HTMLElement | null | undefined) {
  const had = from instanceof Element && from.contains(document.activeElement);
  await tick();
  const lost = !document.activeElement || document.activeElement === document.body;
  if (had || lost) next()?.focus({ preventScroll: true });
}
