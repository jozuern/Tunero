<script lang="ts">
  // The boot of the demo PC: the mark, Windows style spinning dots and a few true lines, then a hard cut to the
  // desktop. First visit of a session only; any key, click or tap skips it.
  import { onMount } from 'svelte';
  import type { DesktopStrings } from '../i18n/desktop-en.ts';
  import { fill } from './app/appKeys.ts';
  import TuneroMark from './TuneroMark.svelte';

  let { copy, version, checks, tweaks, ondone }: { copy: DesktopStrings['boot']; version: string; checks: number; tweaks: number; ondone: () => void } =
    $props();

  const lines = $derived([fill(copy.version, version), copy.signed, fill(copy.counts, checks, tweaks), copy.ready]);
  let shown = $state(0);
  let touch = $state(false);

  onMount(() => {
    touch = matchMedia('(pointer: coarse)').matches;
    const timers = [400, 750, 1100, 1450].map((at, i) => setTimeout(() => (shown = i + 1), at));
    const end = setTimeout(finish, 2300);
    return () => {
      timers.forEach(clearTimeout);
      clearTimeout(end);
    };
  });

  let finished = false;
  function finish() {
    if (finished) return;
    finished = true;
    ondone();
  }
</script>

<svelte:window onkeydown={finish} onpointerdown={finish} />

<div class="boot" role="status" aria-label={copy.ready}>
  <TuneroMark size={88} />
  <span class="dots" aria-hidden="true">{#each [0, 1, 2, 3, 4] as dot (dot)}<i style:--i={dot}></i>{/each}</span>
  <ul>
    {#each lines.slice(0, shown) as line (line)}<li>{line}</li>{/each}
  </ul>
  <p class="skip-hint">{touch ? copy.tap : copy.skip}</p>
</div>

<style>
  .boot {
    position: fixed;
    inset: 0;
    z-index: 10000;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 32px;
    background: #000;
    color: #c8c8c8;
    cursor: none;
    /* The desktop around it stays hidden until the boot ends (tokens.css). */
    visibility: visible;
  }

  .dots {
    position: relative;
    width: 40px;
    height: 40px;
  }

  .dots i {
    position: absolute;
    inset: 0;
    animation: orbit 2.2s infinite;
    animation-delay: calc(var(--i) * 120ms);
    opacity: 0;
  }

  .dots i::before {
    content: '';
    position: absolute;
    left: 50%;
    top: 0;
    width: 5px;
    height: 5px;
    border-radius: 50%;
    background: #fff;
  }

  @keyframes orbit {
    0% {
      transform: rotate(180deg);
      opacity: 1;
      animation-timing-function: ease-out;
    }
    40% {
      transform: rotate(470deg);
      animation-timing-function: linear;
    }
    60% {
      transform: rotate(560deg);
      animation-timing-function: ease-in;
    }
    90% {
      transform: rotate(900deg);
      opacity: 1;
    }
    100% {
      transform: rotate(900deg);
      opacity: 0;
    }
  }

  ul {
    min-height: 96px;
    margin: 0;
    padding: 0;
    list-style: none;
    font-family: var(--mono);
    font-size: 13px;
    line-height: 1.8;
    text-align: center;
  }

  li {
    animation: line 200ms ease-out;
  }

  @keyframes line {
    from {
      opacity: 0;
    }
  }

  .skip-hint {
    position: absolute;
    bottom: 24px;
    margin: 0;
    color: #6b6b6b;
    font-size: 12px;
  }
</style>
