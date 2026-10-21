<script lang="ts">
  // Before/after: the neglected demo PC left of a line the visitor drags, the fixed one right of it, both alive. The
  // wallpaper splits in its shader; the taskbar, Start and notifications are drawn twice and clipped at the line.
  import { onMount } from 'svelte';
  import { fade } from 'svelte/transition';
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import { useStore, reducedMotion } from './store.svelte.ts';
  import { setSplit } from './wallpaper/wallpaper.ts';
  import CompareSide from './CompareSide.svelte';

  let {
    copy,
    lang,
    download,
    version,
    classicHref,
    links,
  }: {
    copy: DesktopStrings;
    lang: string;
    download: string;
    version: string;
    classicHref: string;
    links: { download: string; faq: string; privacy: string };
  } = $props();
  const store = useStore();

  let width = $state(1920);
  let split = $state(960);
  let dragging = false;
  let handle: HTMLElement;

  const clamp = (x: number) => Math.min(Math.max(x, 24), width - 24);
  $effect(() => setSplit(split));

  onMount(() => {
    width = innerWidth;
    // It opens with the line sweeping in from the right, so both sides read at once.
    if (reducedMotion()) split = Math.round(width * 0.62);
    else {
      const start = performance.now();
      const from = width - 24;
      const to = Math.round(width * 0.62);
      const tick = (now: number) => {
        const t = Math.min(1, (now - start) / 1100);
        const eased = 1 - (1 - t) ** 3;
        if (!dragging) split = from + (to - from) * eased;
        if (t < 1 && !dragging) requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    }
    handle.focus({ preventScroll: true });
    return () => setSplit(null);
  });

  function close() {
    store.compare = false;
  }

  function onKeydown(event: KeyboardEvent) {
    const step = event.shiftKey ? 120 : 32;
    if (event.key === 'ArrowLeft') split = clamp(split - step);
    else if (event.key === 'ArrowRight') split = clamp(split + step);
    else if (event.key === 'Home') split = clamp(0);
    else if (event.key === 'End') split = clamp(width);
    else if (event.key === 'Escape') close();
    else return;
    event.preventDefault();
  }
</script>

<svelte:window bind:innerWidth={width} onkeydown={onKeydown} />

<!-- svelte-ignore a11y_no_static_element_interactions -->
<div
  class="compare"
  transition:fade={{ duration: reducedMotion() ? 0 : 200 }}
  onpointerdown={(event) => {
    if ((event.target as Element).closest('.done')) return;
    dragging = true;
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    split = clamp(event.clientX);
  }}
  onpointermove={(event) => dragging && (split = clamp(event.clientX))}
  onpointerup={() => (dragging = false)}
  onpointercancel={() => (dragging = false)}
>
  <div class="side" inert style:clip-path="inset(0 {width - split}px 0 0)">
    <CompareSide side="before" {copy} {lang} {download} {version} {classicHref} {links} />
  </div>
  <div class="side" inert style:clip-path="inset(0 0 0 {split}px)">
    <CompareSide side="after" {copy} {lang} {download} {version} {classicHref} {links} />
  </div>

  <div class="line" style:left="{split}px">
    <span class="tag before">{copy.compare.before}</span>
    <span class="tag after">{copy.compare.after}</span>
    <span
      class="handle"
      role="slider"
      tabindex="0"
      aria-label={copy.compare.divider}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round((split / width) * 100)}
      bind:this={handle}
    >
      <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true"><path d="M9 6l-6 6 6 6M15 6l6 6-6 6" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" /></svg>
    </span>
  </div>

  <div class="bar">
    <p>{copy.compare.hint}</p>
    <button type="button" class="done" onclick={close}>{copy.compare.done}</button>
  </div>
</div>

<style>
  .compare {
    position: fixed;
    inset: 0;
    z-index: 9500;
    cursor: ew-resize;
    touch-action: none;
  }

  .side {
    position: fixed;
    inset: 0;
    pointer-events: none;
  }

  .line {
    position: fixed;
    top: 0;
    bottom: 0;
    width: 2px;
    margin-left: -1px;
    background: #fff;
    box-shadow:
      0 0 0 1px rgba(0, 0, 0, 0.25),
      0 0 24px rgba(96, 205, 255, 0.8);
  }

  .handle {
    position: absolute;
    top: 50%;
    left: 50%;
    display: grid;
    place-items: center;
    width: 44px;
    height: 44px;
    border-radius: 50%;
    background: #fff;
    color: #0b1220;
    box-shadow: 0 6px 24px rgba(0, 0, 0, 0.45);
    translate: -50% -50%;
    outline: none;
  }

  .handle:focus-visible {
    box-shadow:
      0 0 0 3px var(--accent),
      0 6px 24px rgba(0, 0, 0, 0.45);
  }

  .tag {
    position: absolute;
    top: 76px;
    padding: 6px 14px;
    border-radius: 16px;
    background: rgba(0, 0, 0, 0.55);
    color: #fff;
    font-size: 14px;
    font-weight: 600;
    white-space: nowrap;
    -webkit-backdrop-filter: blur(12px);
    backdrop-filter: blur(12px);
  }

  .tag.before {
    right: 16px;
  }

  .tag.after {
    left: 16px;
  }

  .bar {
    position: fixed;
    right: 16px;
    top: 16px;
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 8px 8px 8px 16px;
    border: 1px solid var(--stroke-strong);
    border-radius: 24px;
    background: var(--acrylic-strong);
    -webkit-backdrop-filter: blur(30px);
    backdrop-filter: blur(30px);
    color: var(--ink);
    box-shadow: var(--shadow);
    cursor: default;
  }

  .bar p {
    margin: 0;
    font-size: 13px;
  }

  .done {
    height: 32px;
    padding: 0 16px;
    border: none;
    border-radius: 16px;
    background: var(--accent);
    color: var(--accent-ink);
    font: inherit;
    font-weight: 600;
    cursor: pointer;
  }
</style>
