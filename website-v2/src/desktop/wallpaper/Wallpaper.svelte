<script lang="ts">
  // The desktop wallpaper: the WebGL2 shader on a fixed canvas, with a still of its first frame in front of it until
  // that frame is drawn (public/wallpaper/, made by scripts/render-wallpaper.mjs), so the hand-over cannot be seen. The
  // still comes back when WebGL2 is missing, the shader fails or the context is lost.
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
    let stop = () => {};
    // Started once per canvas: clutter and theme are read every frame, so they must not restart it. Again after the GPU
    // gave a lost context back (a driver update, waking from sleep); the CSS picture stands in meanwhile.
    const start = () => {
      stop();
      stop = untrack(() => startWallpaper(target, () => clutter, () => theme, {
        onFirstFrame: () => {
          live = true;
        },
        onFail: () => {
          live = false;
        },
        isPaused: () => paused,
      }));
    };
    // Not in the task that starts the page: making the WebGL context takes a while on some PCs, and the CSS picture
    // covers the moment.
    let timer: ReturnType<typeof setTimeout> | undefined;
    const frame = requestAnimationFrame(() => (timer = setTimeout(start)));
    target.addEventListener('webglcontextrestored', start);
    return () => {
      cancelAnimationFrame(frame);
      clearTimeout(timer);
      target.removeEventListener('webglcontextrestored', start);
      stop();
      live = false;
    };
  });
</script>

<canvas class="wallpaper" bind:this={canvas} aria-hidden="true"></canvas>
<div
  class="fallback"
  class:live
  class:calm={clutter < 1}
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

  /*
   * The shader's first frame, neglected, with the fixed one over it as the PC gets fixed. Shown at the screen's height
   * and centred, as the shader scales its picture; the colour is the shader's own background beyond the still's sides.
   * The fixed still loads only once something is fixed.
   */
  .fallback {
    opacity: 1;
    visibility: visible;
    transition:
      opacity 0.4s ease,
      visibility 0s linear 0s;
    background: #03040b url('/wallpaper/dark-neglected.webp') center / auto 100% no-repeat;
  }

  /* The theme on <html>, which Base.astro sets before the first paint: right before this component has started. */
  :global(html[data-theme='light']) .fallback {
    background: #f7f9fe url('/wallpaper/light-neglected.webp') center / auto 100% no-repeat;
  }

  .fallback::after {
    content: '';
    position: absolute;
    inset: 0;
    opacity: calc(1 - var(--clutter));
    transition: opacity 1.2s ease;
  }

  .fallback.calm::after {
    background: url('/wallpaper/dark-fixed.webp') center / auto 100% no-repeat;
  }

  :global(html[data-theme='light']) .fallback.calm::after {
    background-image: url('/wallpaper/light-fixed.webp');
  }

  .fallback.live {
    opacity: 0;
    visibility: hidden;
    transition:
      opacity 0.4s ease,
      visibility 0s linear 0.4s;
  }

  :global([data-motion='reduce']) .fallback,
  :global([data-motion='reduce']) .fallback.live,
  :global([data-motion='reduce']) .fallback::after {
    transition: none;
  }
</style>
