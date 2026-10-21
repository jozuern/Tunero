<script lang="ts">
  import { onMount, type Snippet } from 'svelte';
  import type { WindowId } from '../lib/demo-state.ts';
  import { useStore } from './store.svelte.ts';
  import Icon from './Icon.svelte';
  import TuneroMark from './TuneroMark.svelte';
  import type { IconName } from './icons.ts';

  let {
    id,
    title,
    icon,
    labels,
    children,
  }: {
    id: WindowId;
    title: string;
    /** A Fluent icon, or 'tunero' for the mark. */
    icon: IconName | 'tunero';
    labels: { minimize: string; maximize: string; restore: string; close: string };
    children: Snippet;
  } = $props();

  const store = useStore();
  const frame = $derived(store.windows[id]);
  let element: HTMLElement;

  // Focus moves into the window when it opens and back to where it came from when it closes.
  onMount(() => {
    const opener = document.activeElement as HTMLElement | null;
    element.focus({ preventScroll: true });
    return () => {
      if (opener && document.contains(opener)) opener.focus({ preventScroll: true });
    };
  });

  let drag: { pointer: number; dx: number; dy: number } | null = null;

  function startDrag(event: PointerEvent) {
    if (frame.maximized || matchMedia('(max-width: 899px)').matches || event.button !== 0 || (event.target as HTMLElement).closest('button')) return;
    drag = { pointer: event.pointerId, dx: event.clientX - frame.x, dy: event.clientY - frame.y };
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    store.focus(id);
  }

  function moveDrag(event: PointerEvent) {
    if (!drag || event.pointerId !== drag.pointer) return;
    // Keep the title bar reachable: never above the screen, never fully off a side, never under the taskbar.
    frame.x = Math.min(Math.max(event.clientX - drag.dx, 96 - frame.width), innerWidth - 96);
    frame.y = Math.min(Math.max(event.clientY - drag.dy, 0), innerHeight - 48 - 40);
  }

  function endDrag() {
    drag = null;
  }

  function onKeydown(event: KeyboardEvent) {
    if (event.key === 'Escape') {
      event.stopPropagation();
      store.close(id);
    }
  }
</script>

<section
  bind:this={element}
  class="window"
  data-window={id}
  class:maximized={frame.maximized}
  class:minimized={frame.minimized}
  class:focused={store.focused === id}
  role="dialog"
  aria-label={title}
  tabindex="-1"
  style:left="{frame.x}px"
  style:top="{frame.y}px"
  style:width="{frame.width}px"
  style:height="{frame.height}px"
  style:z-index={frame.z}
  onpointerdown={() => store.focused !== id && store.focus(id)}
  onkeydown={onKeydown}
>
  <header
    class="titlebar"
    onpointerdown={startDrag}
    onpointermove={moveDrag}
    onpointerup={endDrag}
    onpointercancel={endDrag}
    ondblclick={() => store.toggleMaximize(id)}
  >
    <span class="title">
      {#if icon === 'tunero'}<TuneroMark size={16} />{:else}<Icon name={icon} size={16} />{/if}
      {title}
    </span>
    <span class="caption">
      <button type="button" aria-label={labels.minimize} title={labels.minimize} onclick={() => store.minimize(id)}>
        <Icon name="minimize" size={16} />
      </button>
      <button
        type="button"
        aria-label={frame.maximized ? labels.restore : labels.maximize}
        title={frame.maximized ? labels.restore : labels.maximize}
        onclick={() => store.toggleMaximize(id)}
      >
        <Icon name={frame.maximized ? 'restore' : 'maximize'} size={16} />
      </button>
      <button type="button" class="close" aria-label={labels.close} title={labels.close} onclick={() => store.close(id)}>
        <Icon name="dismiss" size={16} />
      </button>
    </span>
  </header>
  <div class="body">
    {@render children()}
  </div>
</section>

<style>
  .window {
    position: absolute;
    display: flex;
    flex-direction: column;
    background: var(--bg);
    color: var(--ink);
    border: 1px solid var(--stroke-strong);
    border-radius: var(--radius);
    box-shadow: var(--shadow);
    overflow: hidden;
    outline: none;
    animation: open 200ms cubic-bezier(0.1, 0.9, 0.2, 1);
  }

  .window:not(.focused) {
    box-shadow: 0 4px 16px rgba(0, 0, 0, 0.12);
  }

  .window.maximized {
    left: 0 !important;
    top: 0 !important;
    width: 100% !important;
    height: calc(100% - 48px) !important;
    border-radius: 0;
    border: none;
  }

  @media (max-width: 899px) {
    .window {
      left: 0 !important;
      top: 64px !important;
      width: 100% !important;
      height: calc(100% - 64px - 48px) !important;
      border-radius: 0;
      border-left: none;
      border-right: none;
    }

    .titlebar {
      touch-action: auto;
    }
  }

  .window.minimized {
    display: none;
  }

  @keyframes open {
    from {
      opacity: 0;
      transform: scale(0.96) translateY(8px);
    }
  }

  :global([data-motion='reduce']) .window {
    animation: none;
  }

  .titlebar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    height: 40px;
    flex: none;
    padding-left: 16px;
    user-select: none;
    touch-action: none;
  }

  .title {
    display: flex;
    align-items: center;
    gap: 12px;
    font-size: 12px;
    color: var(--ink);
  }

  .window:not(.focused) .title {
    color: var(--ink-3);
  }

  .caption {
    display: flex;
    align-self: stretch;
  }

  .caption button {
    display: grid;
    place-items: center;
    width: 46px;
    border: none;
    background: transparent;
    color: var(--ink);
    cursor: default;
  }

  .caption button:hover {
    background: var(--stroke);
  }

  .caption .close:hover {
    background: #c42b1c;
    color: #fff;
  }

  .body {
    flex: 1;
    min-height: 0;
    overflow: hidden;
    display: flex;
    flex-direction: column;
  }
</style>
