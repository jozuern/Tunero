// A row that just changed lights up for a moment, so a change made by a click (the visitor's or the guided fix's) is
// seen where it happened. A Svelte action on the row: `use:flash={{ value, tone, delay }}` tints it whenever `value`
// changes, never on the first render, and not at all with animation effects off. The tint is a layer over the row
// (.flash-host::after in tokens.css) whose opacity is animated, which the compositor does without repainting the row.
import { reducedMotion } from '../motion.ts';

/**
 * The spacing of the switches a group's "Turn on" flips one after the other (app/Tweaks.svelte); the guided fix waits
 * for the whole cascade (guide.ts).
 */
export const cascadeStep = 110;

export interface FlashOptions {
  /** What the row shows; a change of it lights the row. */
  value: unknown;
  /** The colour of the light: the accent for a switch, green for a fixed problem. */
  tone?: 'accent' | 'ok';
  /** Milliseconds before the light, so rows changed at once light one after the other. */
  delay?: number;
}

export function flash(node: HTMLElement, options: FlashOptions) {
  let last = options.value;
  node.classList.add('flash-host');
  return {
    update(next: FlashOptions) {
      if (Object.is(next.value, last)) return;
      last = next.value;
      if (reducedMotion()) return;
      node.style.setProperty('--flash-tone', `var(--${next.tone ?? 'accent'})`);
      try {
        node.animate([{ opacity: 0 }, { opacity: 1, offset: 0.18 }, { opacity: 0 }], {
          duration: 900,
          delay: next.delay ?? 0,
          easing: 'ease-out',
          pseudoElement: '::after',
        });
      } catch {
        // A browser without animations of pseudo-elements shows the change without the light.
      }
    },
  };
}
