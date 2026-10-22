// A row that just changed lights up for a moment, so a change made by a click (the visitor's or the guided fix's) is
// seen where it happened. A Svelte action on the row: `use:flash={{ value, tone, delay }}` tints it whenever `value`
// changes, never on the first render, and not at all with animation effects off.
import { reducedMotion } from '../motion.ts';

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
  return {
    update(next: FlashOptions) {
      if (Object.is(next.value, last)) return;
      last = next.value;
      if (reducedMotion()) return;
      // An inset shadow over the row's own shadow: the row's background and border stay as they are.
      const base = getComputedStyle(node).boxShadow;
      const own = base && base !== 'none' ? `, ${base}` : '';
      const tint = `color-mix(in srgb, var(--${next.tone ?? 'accent'}) 18%, transparent)`;
      node.animate(
        [
          { boxShadow: `inset 0 0 0 999px transparent${own}` },
          { boxShadow: `inset 0 0 0 999px ${tint}${own}`, offset: 0.18 },
          { boxShadow: `inset 0 0 0 999px transparent${own}` },
        ],
        { duration: 900, delay: next.delay ?? 0, easing: 'ease-out' },
      );
    },
  };
}
