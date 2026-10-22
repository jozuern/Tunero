<script lang="ts">
  // The desktop wallpaper: the WebGL2 shader on a fixed canvas, with a CSS picture in front of it until the first
  // frame is drawn. The CSS picture comes back when WebGL2 is missing, the shader fails or the context is lost.
  import { untrack } from 'svelte';
  import { startWallpaper } from './wallpaper.ts';

  let {
    clutter,
    theme,
    paused = false,
  }: {
    /** 1 for the neglected demo PC, 0 when everything is fixed; the shader eases toward it. */
    clutter: number;
    theme: 'light' | 'dark';
    /** Nothing of the wallpaper can be seen moving (the boot, a window over everything): no frames are drawn. */
    paused?: boolean;
  } = $props();

  let canvas: HTMLCanvasElement | undefined = $state();
  /** True while the shader is drawing; the CSS picture steps back. */
  let live = $state(false);

  $effect(() => {
    if (!canvas) return;
    const target = canvas;
    // Started once per canvas: clutter and theme are read every frame, so they must not restart it.
    const stop = untrack(() => startWallpaper(target, () => clutter, () => theme, {
      onFirstFrame: () => {
        live = true;
      },
      onFail: () => {
        live = false;
      },
      isPaused: () => paused,
    }));
    return () => {
      stop();
      live = false;
    };
  });
</script>

<canvas class="wallpaper" bind:this={canvas} aria-hidden="true"></canvas>
<div
  class="fallback"
  class:live
  style:--clutter={Math.min(1, Math.max(0, clutter))}
  aria-hidden="true"
></div>

<style>
  .wallpaper,
  .fallback {
    position: fixed;
    inset: 0;
    z-index: 0;
    width: 100%;
    height: 100%;
    pointer-events: none;
  }

  .wallpaper {
    display: block;
  }

  /* A still approximation of the shader: ribbons of light as soft bands over the same base. */
  .fallback {
    opacity: 1;
    visibility: visible;
    transition:
      opacity 0.6s ease,
      visibility 0s linear 0s,
      filter 1.2s ease;
    filter: saturate(calc(1 - 0.75 * var(--clutter))) contrast(calc(1 - 0.15 * var(--clutter)));
    background:
      radial-gradient(ellipse 125% 118% at 100% -10%, transparent 75.2%, rgb(190 232 255 / 0.85) 76%, transparent 76.9%),
      radial-gradient(ellipse 120% 110% at 100% -8%, transparent 61%, rgb(15 60 190 / 0.55) 67%, rgb(85 165 255 / 0.85) 71.5%, rgb(20 70 200 / 0.6) 76%, transparent 82%),
      radial-gradient(ellipse 135% 100% at 88% -20%, transparent 70%, rgb(10 120 140 / 0.45) 76%, transparent 82%),
      radial-gradient(ellipse 85% 75% at 78% -16%, transparent 38%, rgb(85 65 200 / 0.42) 55%, transparent 72%),
      radial-gradient(ellipse 60% 50% at 55% 45%, rgb(20 50 140 / 0.45), transparent 72%),
      linear-gradient(180deg, #03050d, #060c1f 55%, #050b1c);
  }

  /* The theme on <html>, which Base.astro sets before the first paint: right before this component has started. */
  :global(html[data-theme='light']) .fallback {
    background:
      radial-gradient(ellipse 125% 118% at 100% -10%, transparent 75.2%, rgb(255 255 255 / 0.95) 76%, transparent 76.9%),
      radial-gradient(ellipse 120% 110% at 100% -8%, transparent 61%, rgb(80 125 240 / 0.45) 67%, rgb(170 200 255 / 0.85) 71.5%, rgb(70 115 235 / 0.5) 76%, transparent 82%),
      radial-gradient(ellipse 135% 100% at 88% -20%, transparent 70%, rgb(140 205 230 / 0.5) 76%, transparent 82%),
      radial-gradient(ellipse 85% 75% at 78% -16%, transparent 38%, rgb(185 175 245 / 0.55) 55%, transparent 72%),
      radial-gradient(ellipse 60% 50% at 55% 45%, rgb(255 255 255 / 0.7), transparent 72%),
      linear-gradient(180deg, #f6f8fe, #eaf0fb 50%, #dfe7f8);
  }

  /* Neglect: a muddy grey brown veil, as in the shader. */
  .fallback::after {
    content: '';
    position: absolute;
    inset: 0;
    background: rgb(70 60 48);
    mix-blend-mode: color;
    opacity: calc(0.55 * var(--clutter));
    transition: opacity 1.2s ease;
  }

  .fallback.live {
    opacity: 0;
    visibility: hidden;
    transition:
      opacity 0.6s ease,
      visibility 0s linear 0.6s,
      filter 1.2s ease;
  }

  :global([data-motion='reduce']) .fallback,
  :global([data-motion='reduce']) .fallback.live,
  :global([data-motion='reduce']) .fallback::after {
    transition: none;
  }
</style>
