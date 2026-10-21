<script lang="ts">
  // The dive: from the Tunero window into the registry, key by key, to the one value a change writes, and back out.
  // CSS 3D transforms over the wallpaper; under reduced motion it shows the end card at once.
  import { onMount } from 'svelte';
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import type { Write } from '../lib/demo.ts';
  import { reducedMotion } from './motion.ts';

  let { write, title, copy, onclose }: { write: Write; title: string; copy: DesktopStrings['dive']; onclose: () => void } = $props();

  const parts = $derived(write.where.split('\\'));
  const hive = $derived(parts[0] as keyof DesktopStrings['dive']['hives']);
  const keys = $derived(parts.slice(0, -1));
  const value = $derived(parts.at(-1)!);

  /** Distance between two keys in the tunnel, in px of depth. */
  const gap = 520;
  let tunnel: HTMLElement;
  let card: HTMLElement;
  let leaving = false;

  onMount(() => {
    const opener = document.activeElement as HTMLElement | null;
    const depth = keys.length * gap;
    if (reducedMotion()) {
      tunnel.style.transform = `translateZ(${depth}px)`;
      card.style.opacity = '1';
    } else {
      tunnel.animate([{ transform: 'translateZ(-200px)' }, { transform: `translateZ(${depth}px)` }], {
        duration: 900 + keys.length * 260,
        easing: 'cubic-bezier(0.55, 0, 0.15, 1)',
        fill: 'forwards',
      });
      card.animate([{ opacity: 0, transform: 'translateY(16px) scale(0.96)' }, { opacity: 1, transform: 'none' }], {
        duration: 360,
        delay: 700 + keys.length * 260,
        easing: 'ease-out',
        fill: 'forwards',
      });
    }
    card.focus({ preventScroll: true });
    return () => {
      if (opener && document.contains(opener)) opener.focus({ preventScroll: true });
    };
  });

  function close() {
    if (leaving) return;
    leaving = true;
    if (reducedMotion()) return onclose();
    const depth = keys.length * gap;
    card.animate([{ opacity: 1 }, { opacity: 0 }], { duration: 160, fill: 'forwards' });
    tunnel.animate([{ transform: `translateZ(${depth}px)` }, { transform: 'translateZ(-400px)' }], {
      duration: 520,
      easing: 'cubic-bezier(0.6, 0, 0.8, 0.4)',
      fill: 'forwards',
    }).onfinish = onclose;
  }
</script>

<svelte:window onkeydown={(event) => event.key === 'Escape' && (event.stopPropagation(), close())} />

<!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
<div class="dive" onclick={close}>
  <div class="stage" aria-hidden="true">
    <div class="tunnel" bind:this={tunnel}>
      {#each keys as key, i (i)}
        <div class="key" style:transform="translateZ({-i * gap}px)">
          <span class="ring"></span>
          <span class="name">{key}</span>
        </div>
      {/each}
    </div>
  </div>

  <div class="card" role="dialog" aria-label={copy.title} tabindex="-1" bind:this={card} onclick={(event) => event.stopPropagation()}>
    <p class="label">{copy.title}</p>
    <h2>{title}</h2>
    <p class="hive">{parts[0]}: {copy.hives[hive] ?? ''}</p>
    <code class="path">{keys.join('\\')}</code>
    <div class="values">
      <div>
        <span>{copy.now}</span>
        <code class="name">{value}</code>
        <b class="from">{write.from}</b>
      </div>
      <div>
        <span>{copy.after}</span>
        <code class="name">{value}</code>
        <b class="to">{write.to}</b>
      </div>
    </div>
    <button type="button" class="back" onclick={close}>{copy.back}</button>
    <p class="hint">{copy.hint}</p>
  </div>
</div>

<style>
  .dive {
    position: fixed;
    inset: 0;
    z-index: 9800;
    display: grid;
    place-items: center;
    background: radial-gradient(ellipse at center, rgba(0, 10, 30, 0.55), rgba(0, 0, 0, 0.88));
    backdrop-filter: blur(6px);
    -webkit-backdrop-filter: blur(6px);
    cursor: zoom-out;
    animation: dim 300ms ease-out;
  }

  @keyframes dim {
    from {
      opacity: 0;
    }
  }

  .stage {
    position: absolute;
    inset: 0;
    perspective: 700px;
    overflow: hidden;
  }

  .tunnel {
    position: absolute;
    left: 50%;
    top: 50%;
    transform-style: preserve-3d;
    transform: translateZ(-200px);
  }

  .key {
    position: absolute;
    left: 0;
    top: 0;
    transform-style: preserve-3d;
  }

  .ring {
    position: absolute;
    left: -46vmin;
    top: -30vmin;
    width: 92vmin;
    height: 60vmin;
    border: 2px solid rgba(96, 205, 255, 0.55);
    border-radius: 18px;
    box-shadow:
      0 0 40px rgba(96, 205, 255, 0.35),
      inset 0 0 40px rgba(96, 205, 255, 0.2);
  }

  .key .name {
    position: absolute;
    left: -46vmin;
    top: -30vmin;
    transform: translate(16px, -40px);
    color: #bfe9ff;
    font-family: var(--mono);
    font-size: 28px;
    white-space: nowrap;
    text-shadow: 0 0 12px rgba(96, 205, 255, 0.8);
  }

  .card {
    position: relative;
    width: min(620px, calc(100vw - 32px));
    padding: 24px;
    border: 1px solid var(--stroke-strong);
    border-radius: 12px;
    background: var(--surface);
    color: var(--ink);
    box-shadow: var(--shadow);
    opacity: 0;
    cursor: default;
    outline: none;
  }

  .label {
    margin: 0;
    color: var(--ink-2);
    font-size: 12px;
  }

  h2 {
    margin: 4px 0 12px;
    font-family: var(--font-display);
    font-size: 22px;
    font-weight: 600;
  }

  .hive {
    margin: 0 0 4px;
    color: var(--ink-2);
  }

  .path {
    display: block;
    font-family: var(--mono);
    font-size: 12px;
    overflow-wrap: anywhere;
  }

  .values {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 12px;
    margin: 16px 0;
  }

  .values div {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 12px;
    border-radius: var(--radius);
    background: var(--surface-2);
    border: 1px solid var(--stroke);
  }

  .values span {
    color: var(--ink-2);
    font-size: 12px;
  }

  .values .name {
    font-family: var(--mono);
    font-size: 12px;
    overflow-wrap: anywhere;
  }

  .values b {
    font-family: var(--mono);
    font-size: 28px;
    font-weight: 400;
  }

  .from {
    color: var(--ink-2);
    text-decoration: line-through;
    text-decoration-thickness: 2px;
  }

  .to {
    color: var(--accent);
  }

  .back {
    height: 32px;
    padding: 0 16px;
    border: 1px solid var(--stroke-strong);
    border-radius: var(--radius-sm);
    background: var(--surface-2);
    color: var(--ink);
    font: inherit;
  }

  .hint {
    margin: 8px 0 0;
    color: var(--ink-3);
    font-size: 12px;
  }

  :global([data-motion='reduce']) .dive {
    animation: none;
  }

  @media (max-width: 520px) {
    .values {
      grid-template-columns: 1fr;
    }
  }
</style>
